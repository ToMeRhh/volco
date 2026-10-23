#include <string.h>
#include <unity.h>

#include "volco_chip.h"
#include "volco_pin_helpers.h"
#include "volco_runtime.h"

using namespace volco;

void setUp() {}
void tearDown() {}

static uint64_t bit(int p) { return 1ull << p; }
static bool has(uint64_t m, int p) { return (m >> p) & 1u; }

// A config with: sliders on 32 and 33; button 0 on 18 with an LED on 19; button 1 on 21 with an
// LED pair on 22 and 23; bus 0 used on 13/14; bus 1 unused on 15/16.
static RuntimeConfig makeConfig() {
  RuntimeConfig rc;
  memset(&rc, 0, sizeof rc);
  rc.sliderCount = 2;
  rc.sliderPins[0] = 32;
  rc.sliderPins[1] = 33;
  rc.buttonCount = 2;
  rc.buttons[0].pin = 18;
  rc.buttons[0].kind = KIND_LED;
  rc.buttons[0].pinA = 19;
  rc.buttons[1].pin = 21;
  rc.buttons[1].kind = KIND_LED_PAIR;
  rc.buttons[1].pinA = 22;
  rc.buttons[1].pinB = 23;
  rc.i2c[0].used = true;
  rc.i2c[0].sda = 13;
  rc.i2c[0].scl = 14;
  rc.i2c[1].used = false;
  rc.i2c[1].sda = 15;
  rc.i2c[1].scl = 16;
  return rc;
}

void test_pin_use_lists_used_buses_only() {
  RuntimeConfig rc = makeConfig();
  PinUse u = pinUse(rc);
  TEST_ASSERT_EQUAL_UINT64(bit(13) | bit(14), u.i2c);
  TEST_ASSERT_EQUAL_UINT64(bit(19) | bit(22) | bit(23), u.leds);
  TEST_ASSERT_EQUAL_UINT64(bit(18) | bit(21), u.buttons);
  TEST_ASSERT_EQUAL_UINT64(bit(32) | bit(33), u.sliders);
  TEST_ASSERT_EQUAL_UINT64(u.sliders | u.buttons | u.leds | u.i2c, u.all());
}

void test_analog_watch_pins() {
  const ChipInfo& chip = esp32Chip();
  PinUse u = pinUse(makeConfig());
  uint64_t m = watchPins(chip, u, WATCH_ANALOG, false);
  TEST_ASSERT_TRUE(has(m, 32));  // configured slider
  TEST_ASSERT_TRUE(has(m, 33));
  TEST_ASSERT_TRUE(has(m, 34));  // free ADC1
  TEST_ASSERT_TRUE(has(m, 25));  // free ADC2 with Wi-Fi off
  TEST_ASSERT_FALSE(has(m, 19)); // LED
  TEST_ASSERT_FALSE(has(m, 13)); // I2C
  TEST_ASSERT_FALSE(has(m, 14));
  TEST_ASSERT_FALSE(has(m, 18)); // button pin (not ADC anyway)
  TEST_ASSERT_FALSE(has(m, 17)); // no ADC
  uint64_t w = watchPins(chip, u, WATCH_ANALOG, true);
  const int adc2[] = {0, 2, 4, 12, 13, 14, 15, 25, 26, 27};
  for (int p : adc2) TEST_ASSERT_FALSE(has(w, p));
  const int never[] = {1, 3, 6, 7, 8, 9, 10, 11};
  for (int p : never) {
    TEST_ASSERT_FALSE(has(m, p));
    TEST_ASSERT_FALSE(has(w, p));
  }
  TEST_ASSERT_TRUE(has(w, 32));
  TEST_ASSERT_TRUE(has(w, 39));
  // A configured button on an ADC pin is not sampled by the analog watch.
  RuntimeConfig rc = makeConfig();
  rc.buttons[0].pin = 35;
  TEST_ASSERT_FALSE(has(watchPins(chip, pinUse(rc), WATCH_ANALOG, false), 35));
}

void test_button_watch_pins() {
  const ChipInfo& chip = esp32Chip();
  PinUse u = pinUse(makeConfig());
  uint64_t m = watchPins(chip, u, WATCH_BUTTON, false);
  TEST_ASSERT_TRUE(has(m, 18));   // configured button
  TEST_ASSERT_TRUE(has(m, 21));
  TEST_ASSERT_TRUE(has(m, 17));   // free
  TEST_ASSERT_FALSE(has(m, 34));  // free input-only: no pull-up, it would float
  TEST_ASSERT_FALSE(has(m, 39));
  TEST_ASSERT_FALSE(has(m, 19));  // LEDs
  TEST_ASSERT_FALSE(has(m, 22));
  TEST_ASSERT_FALSE(has(m, 23));
  TEST_ASSERT_FALSE(has(m, 13));  // I2C
  TEST_ASSERT_FALSE(has(m, 14));
  TEST_ASSERT_FALSE(has(m, 32));  // sliders
  TEST_ASSERT_FALSE(has(m, 33));
  TEST_ASSERT_FALSE(has(m, 1));   // reserved
  TEST_ASSERT_FALSE(has(m, 6));
  // A configured button on an input-only pin is watched (it has its own pull-up).
  RuntimeConfig rc = makeConfig();
  rc.buttons[0].pin = 35;
  TEST_ASSERT_TRUE(has(watchPins(chip, pinUse(rc), WATCH_BUTTON, false), 35));
  // Wi-Fi does not matter to a digital read.
  TEST_ASSERT_EQUAL_UINT64(m, watchPins(chip, u, WATCH_BUTTON, true));
  TEST_ASSERT_EQUAL_UINT64(0, watchPins(chip, u, WATCH_STOP, false));
}

void test_analog_reports_a_steady_move() {
  PinWatch w;
  w.start(WATCH_ANALOG, bit(32), 0);
  uint32_t t = 0;
  int reports = 0;
  for (int i = 0; i < 20; i++, t += kWatchSampleMs) reports += w.analog(32, 1000, t);
  TEST_ASSERT_EQUAL(0, reports);
  // The baseline follows the level (1/kAnalogBaselineFollow a sample), so the 300-code move
  // shows up later than the raw ramp: about raw 1500 of this one. One report by its end.
  uint32_t reportedAt = 0;
  for (uint16_t v = 1020; v <= 1600; v += 20, t += kWatchSampleMs) {
    if (w.analog(32, v, t)) {
      reports++;
      reportedAt = t;
    } else if (reports > 0) {
      // Not again within the gap: the rest of the ramp falls inside it.
      TEST_ASSERT_TRUE(t - reportedAt < kWatchReportGapMs);
    }
  }
  TEST_ASSERT_EQUAL(1, reports);
}

void test_analog_floating_pin_never_reports() {
  PinWatch w;
  w.start(WATCH_ANALOG, bit(34), 0);
  uint32_t seed = 12345, t = 0;
  for (int i = 0; i < 3000; i++, t += kWatchSampleMs) {
    seed = seed * 1664525u + 1013904223u;
    uint16_t raw = (uint16_t)((seed >> 16) % 4096);
    TEST_ASSERT_FALSE(w.analog(34, raw, t));
  }
  PinWatch d;
  d.start(WATCH_ANALOG, bit(34), 0);
  t = 0;
  for (int i = 0; i < 3000; i++, t += kWatchSampleMs) {
    int raw = 1000 + i + ((i & 1) ? 150 : -150);
    TEST_ASSERT_FALSE(d.analog(34, (uint16_t)raw, t));
  }
}

void test_analog_clean_slow_drift_never_reports() {
  PinWatch w;
  w.start(WATCH_ANALOG, bit(34), 0);
  uint32_t t = 0;
  for (int i = 0; i < 20; i++, t += kWatchSampleMs) TEST_ASSERT_FALSE(w.analog(34, 1000, t));
  for (int i = 1; i <= 3000; i++, t += kWatchSampleMs) TEST_ASSERT_FALSE(w.analog(34, (uint16_t)(1000 + i), t));
}

void test_digital_press_reports_once() {
  PinWatch w;
  w.start(WATCH_BUTTON, bit(17), 0);
  uint32_t t = 0;
  for (; t < 600; t += 10) TEST_ASSERT_FALSE(w.digital(17, false, t));
  int reports = 0;
  uint32_t lowStart = t, reportedAt = 0;
  for (; t < lowStart + 40; t += 10)
    if (w.digital(17, true, t)) {
      reports++;
      reportedAt = t;
    }
  TEST_ASSERT_EQUAL(1, reports);
  TEST_ASSERT_EQUAL(lowStart + 30, reportedAt);
  for (uint32_t end = t + 1000; t < end; t += 10) TEST_ASSERT_FALSE(w.digital(17, true, t));
  for (uint32_t end = t + 150; t < end; t += 10) TEST_ASSERT_FALSE(w.digital(17, false, t));
  reports = 0;
  for (uint32_t end = t + 40; t < end; t += 10) reports += w.digital(17, true, t);
  TEST_ASSERT_EQUAL(1, reports);
}

void test_digital_chatter_never_reports() {
  PinWatch w;
  w.start(WATCH_BUTTON, bit(17), 0);
  uint32_t t = 0;
  for (int i = 0; t < 3000; i++, t += 10) TEST_ASSERT_FALSE(w.digital(17, i & 1, t));
  PinWatch low;
  low.start(WATCH_BUTTON, bit(17), 0);
  for (t = 0; t < 3000; t += 10) TEST_ASSERT_FALSE(low.digital(17, true, t));
}

void test_watch_expires_after_30s_unless_renewed() {
  PinWatch w;
  TEST_ASSERT_FALSE(w.active());
  TEST_ASSERT_FALSE(w.expired(100000));
  w.start(WATCH_BUTTON, bit(17), 1000);
  TEST_ASSERT_TRUE(w.active());
  TEST_ASSERT_EQUAL(WATCH_BUTTON, w.role());
  TEST_ASSERT_EQUAL_UINT64(bit(17), w.pins());
  TEST_ASSERT_FALSE(w.expired(1000 + kWatchTimeoutMs - 1));
  TEST_ASSERT_TRUE(w.expired(1000 + kWatchTimeoutMs));
  w.start(WATCH_BUTTON, bit(17), 20000);
  TEST_ASSERT_FALSE(w.expired(1000 + kWatchTimeoutMs));
  TEST_ASSERT_TRUE(w.expired(20000 + kWatchTimeoutMs));
  // Not-active, unlisted and wrong-role pins report nothing.
  w.stop();
  TEST_ASSERT_FALSE(w.digital(17, true, 0));
}

void test_stop_role_stops() {
  PinWatch w;
  w.start(WATCH_ANALOG, bit(32), 0);
  w.start(WATCH_STOP, 0, 10);
  TEST_ASSERT_FALSE(w.active());
  TEST_ASSERT_EQUAL(WATCH_STOP, w.role());
  TEST_ASSERT_EQUAL_UINT64(0, w.pins());
  TEST_ASSERT_FALSE(w.expired(1000000));
  TEST_ASSERT_FALSE(w.analog(32, 4000, 20));
  w.start(WATCH_ANALOG, bit(32), 30);
  w.stop();
  TEST_ASSERT_FALSE(w.active());
}

void test_role_change_resets_pin_state() {
  PinWatch w;
  w.start(WATCH_ANALOG, bit(34), 0);
  uint32_t t = 0;
  for (int i = 0; i < 25; i++, t += 10) w.analog(34, 1000, t);
  // The same role keeps the baseline: a jump still reports.
  w.start(WATCH_ANALOG, bit(34), t);
  bool jumped = false;
  for (int i = 0; i < 40 && !jumped; i++, t += 10) jumped = w.analog(34, 2000, t);
  TEST_ASSERT_TRUE(jumped);
  // A different role and back resets it: the first samples seed a new baseline, so a jump to a
  // new steady level right after the restart does not report.
  w.start(WATCH_BUTTON, bit(34), t);
  w.start(WATCH_ANALOG, bit(34), t);
  bool again = false;
  for (int i = 0; i < 40; i++, t += 10) again |= w.analog(34, 3500, t);
  TEST_ASSERT_FALSE(again);
  // Wrong role and unlisted pins.
  TEST_ASSERT_FALSE(w.digital(34, true, t));
  TEST_ASSERT_FALSE(w.analog(35, 100, t));
}

void test_plan_scan() {
  const ChipInfo& chip = esp32Chip();
  RuntimeConfig rc = makeConfig();
  rc.i2c[2].used = true;
  rc.i2c[2].sda = 4;
  rc.i2c[2].scl = 5;
  ScanPlan p = planScan(chip, rc, 13, 14);
  TEST_ASSERT_EQUAL(SCAN_HW_BUS, p.path);
  TEST_ASSERT_EQUAL(0, p.bus);
  p = planScan(chip, rc, 4, 5);
  TEST_ASSERT_EQUAL(SCAN_SOFT_BUS, p.path);
  TEST_ASSERT_EQUAL(2, p.bus);
  TEST_ASSERT_EQUAL(SCAN_TEMPORARY, planScan(chip, rc, 16, 17).path);
  TEST_ASSERT_EQUAL(SCAN_REFUSED, planScan(chip, rc, 34, 17).path);  // input only
  TEST_ASSERT_EQUAL(SCAN_REFUSED, planScan(chip, rc, 17, 6).path);   // flash pin
  TEST_ASSERT_EQUAL(SCAN_REFUSED, planScan(chip, rc, 17, 17).path);
  TEST_ASSERT_EQUAL(SCAN_REFUSED, planScan(chip, rc, 13, 17).path);  // bus 0's SDA, another SCL
  TEST_ASSERT_EQUAL(SCAN_REFUSED, planScan(chip, rc, 14, 13).path);  // swapped
  TEST_ASSERT_EQUAL(SCAN_REFUSED, planScan(chip, rc, 17, 5).path);   // soft bus's SCL
  // An unused bus is not configured: its pins are free.
  TEST_ASSERT_EQUAL(SCAN_TEMPORARY, planScan(chip, rc, 15, 16).path);
}

void test_drive_allowed() {
  const ChipInfo& chip = esp32Chip();
  PinUse u = pinUse(makeConfig());
  TEST_ASSERT_TRUE(driveAllowed(chip, u, 17));   // free output pin
  TEST_ASSERT_TRUE(driveAllowed(chip, u, 19));   // LED
  TEST_ASSERT_TRUE(driveAllowed(chip, u, 22));
  TEST_ASSERT_FALSE(driveAllowed(chip, u, 18));  // button
  TEST_ASSERT_FALSE(driveAllowed(chip, u, 32));  // slider
  TEST_ASSERT_FALSE(driveAllowed(chip, u, 13));  // I2C
  TEST_ASSERT_FALSE(driveAllowed(chip, u, 34));  // input only
  TEST_ASSERT_FALSE(driveAllowed(chip, u, 1));
  TEST_ASSERT_FALSE(driveAllowed(chip, u, 6));
  TEST_ASSERT_FALSE(driveAllowed(chip, u, 40));
}

int main() {
  UNITY_BEGIN();
  RUN_TEST(test_pin_use_lists_used_buses_only);
  RUN_TEST(test_analog_watch_pins);
  RUN_TEST(test_button_watch_pins);
  RUN_TEST(test_analog_reports_a_steady_move);
  RUN_TEST(test_analog_floating_pin_never_reports);
  RUN_TEST(test_analog_clean_slow_drift_never_reports);
  RUN_TEST(test_digital_press_reports_once);
  RUN_TEST(test_digital_chatter_never_reports);
  RUN_TEST(test_watch_expires_after_30s_unless_renewed);
  RUN_TEST(test_stop_role_stops);
  RUN_TEST(test_role_change_resets_pin_state);
  RUN_TEST(test_plan_scan);
  RUN_TEST(test_drive_allowed);
  return UNITY_END();
}

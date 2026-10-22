#include <string.h>
#include <unity.h>

#include "../golden_config.h"
#include "volco_config.h"
#include "volco_runtime.h"

using namespace volco;

void setUp() {}
void tearDown() {}

static volco_config_ControllerConfig decoded(const char* goldenName) {
  volco_config_ControllerConfig c;
  for (auto& g : configCases("decode-ok"))
    if (g.name == goldenName) {
      TEST_ASSERT_TRUE(decodeConfig(g.blob.data(), g.blob.size(), c));
      return c;
    }
  TEST_FAIL_MESSAGE(goldenName);
  return c;
}

void test_diy_reference() {
  volco_config_ControllerConfig c = decoded("diy_reference");
  TEST_ASSERT_EQUAL_UINT(CFG_OK, validateConfig(c, esp32Chip()).code);
  RuntimeConfig r;
  toRuntime(c, r);
  TEST_ASSERT_EQUAL_UINT(5, r.sliderCount);
  const uint8_t sliders[] = {33, 35, 32, 36, 34};
  TEST_ASSERT_EQUAL_UINT8_ARRAY(sliders, r.sliderPins, 5);
  TEST_ASSERT_EQUAL_UINT(3, r.buttonCount);
  TEST_ASSERT_EQUAL_UINT(4, r.buttons[0].pin);
  TEST_ASSERT_EQUAL_UINT(KIND_LED, r.buttons[0].kind);
  TEST_ASSERT_EQUAL_UINT(12, r.buttons[0].pinA);
  TEST_ASSERT_TRUE(r.buttons[0].activeLow);
  TEST_ASSERT_EQUAL_UINT(KIND_LED, r.buttons[1].kind);
  TEST_ASSERT_EQUAL_UINT(21, r.buttons[1].pinA);
  TEST_ASSERT_EQUAL_UINT(KIND_LED_PAIR, r.buttons[2].kind);
  TEST_ASSERT_EQUAL_UINT(18, r.buttons[2].pinA);
  TEST_ASSERT_EQUAL_UINT(19, r.buttons[2].pinB);
  TEST_ASSERT_FALSE(r.buttons[2].activeLow);
  TEST_ASSERT_FALSE(r.i2c[0].used);  // no screens: both buses unused even though configured
  TEST_ASSERT_FALSE(r.i2c[1].used);
  TEST_ASSERT_FALSE(r.udp.enabled);
}

void test_defaults_converted_to_ms() {
  volco_config_ControllerConfig c = decoded("empty");
  RuntimeConfig r;
  toRuntime(c, r);
  TEST_ASSERT_EQUAL_UINT(255, r.screens.brightness);
  TEST_ASSERT_EQUAL_UINT(8, r.screens.dimBrightness);
  TEST_ASSERT_EQUAL_UINT(360, r.screens.transitionMs);
  TEST_ASSERT_EQUAL_UINT(2000, r.screens.scrollHoldMs);
  TEST_ASSERT_EQUAL_UINT(1200, r.screens.scrollEndHoldMs);
  TEST_ASSERT_EQUAL_UINT(30, r.screens.scrollSpeedPxS);
  TEST_ASSERT_EQUAL_UINT(120000, r.screens.dimAfterMs);
  TEST_ASSERT_EQUAL_UINT(1800000, r.screens.offAfterMs);
  TEST_ASSERT_EQUAL_UINT(180000, r.screens.pixelShiftEveryMs);
  TEST_ASSERT_TRUE(r.screens.dimEnabled && r.screens.offEnabled && r.screens.pixelShiftEnabled && r.screens.wakeSwallowsPress);
  TEST_ASSERT_EQUAL_UINT(30, r.input.debounceMs);
  TEST_ASSERT_EQUAL_UINT(10, r.input.sliderIntervalMs);
  TEST_ASSERT_FLOAT_WITHIN(0.0001f, 0.2f, r.input.filterWeight);
  TEST_ASSERT_EQUAL_UINT(25, r.input.sliderDeadband);
}

void test_defaults_are_empty_and_sane() {
  volco_config_ControllerConfig c = volco_config_ControllerConfig_init_default;
  RuntimeConfig r;
  memset(&r, 0xA5, sizeof r);  // garbage in, so every field must be set
  toRuntime(c, r);
  TEST_ASSERT_EQUAL_UINT(0, r.sliderCount);
  TEST_ASSERT_EQUAL_UINT(0, r.buttonCount);
  TEST_ASSERT_FALSE(r.i2c[0].used);
  TEST_ASSERT_FALSE(r.i2c[1].used);
  TEST_ASSERT_FALSE(r.udp.enabled);
  TEST_ASSERT_EQUAL_STRING("", r.udp.ssid);
}

void test_screen_buses_used_only() {
  volco_config_ControllerConfig c = volco_config_ControllerConfig_init_default;
  c.buttons_count = 2;
  c.buttons[0].has_pin = true;
  c.buttons[0].pin = 4;
  c.buttons[0].which_indicator = volco_config_Button_screen_tag;
  c.buttons[0].indicator.screen = volco_config_Screen_init_default;
  c.buttons[0].indicator.screen.bus = 1;
  c.buttons[0].indicator.screen.address = 0x3D;
  c.buttons[1].has_pin = true;
  c.buttons[1].pin = 5;
  c.has_i2c0 = true;
  c.i2c0.has_sda = true; c.i2c0.sda = 25; c.i2c0.has_scl = true; c.i2c0.scl = 26;
  c.has_i2c1 = true;
  c.i2c1.has_sda = true; c.i2c1.sda = 27; c.i2c1.has_scl = true; c.i2c1.scl = 13;
  c.i2c1.has_clock_hz = true; c.i2c1.clock_hz = 1000000;
  c.buttons_count = 3;  // a screen on software bus 3 too
  c.buttons[2].has_pin = true;
  c.buttons[2].pin = 18;
  c.buttons[2].which_indicator = volco_config_Button_screen_tag;
  c.buttons[2].indicator.screen = volco_config_Screen_init_default;
  c.buttons[2].indicator.screen.bus = 3;
  c.has_i2c3 = true;
  c.i2c3.has_sda = true; c.i2c3.sda = 22; c.i2c3.has_scl = true; c.i2c3.scl = 23;
  RuntimeConfig r;
  toRuntime(c, r);
  TEST_ASSERT_FALSE(r.i2c[2].used);
  TEST_ASSERT_TRUE(r.i2c[3].used);
  TEST_ASSERT_EQUAL_UINT(22, r.i2c[3].sda);
  TEST_ASSERT_EQUAL_UINT(23, r.i2c[3].scl);
  TEST_ASSERT_EQUAL_UINT(400000, r.i2c[3].clockHz);
  TEST_ASSERT_EQUAL_UINT(KIND_SCREEN, r.buttons[0].kind);
  TEST_ASSERT_EQUAL_UINT(1, r.buttons[0].bus);
  TEST_ASSERT_EQUAL_UINT(0x3D, r.buttons[0].address);
  TEST_ASSERT_EQUAL_UINT(KIND_NONE, r.buttons[1].kind);
  TEST_ASSERT_FALSE(r.i2c[0].used);
  TEST_ASSERT_TRUE(r.i2c[1].used);
  TEST_ASSERT_EQUAL_UINT(27, r.i2c[1].sda);
  TEST_ASSERT_EQUAL_UINT(13, r.i2c[1].scl);
  TEST_ASSERT_EQUAL_UINT(1000000, r.i2c[1].clockHz);
}

void test_udp_copied() {
  volco_config_ControllerConfig c = volco_config_ControllerConfig_init_default;
  c.has_udp = true;
  c.udp.has_enabled = true; c.udp.enabled = true;
  c.udp.has_ssid = true; strcpy(c.udp.ssid, "home-net");
  c.udp.has_password = true; strcpy(c.udp.password, "secret123");
  c.udp.has_pc_ip = true; c.udp.pc_ip = (192u << 24) | (168u << 16) | (1u << 8) | 10u;
  RuntimeConfig r;
  toRuntime(c, r);
  TEST_ASSERT_TRUE(r.udp.enabled);
  TEST_ASSERT_EQUAL_STRING("home-net", r.udp.ssid);
  TEST_ASSERT_EQUAL_STRING("secret123", r.udp.password);
  TEST_ASSERT_EQUAL_HEX32(0xC0A8010A, r.udp.pcIp);
  TEST_ASSERT_EQUAL_UINT(12345, r.udp.pcPort);
  TEST_ASSERT_EQUAL_UINT(12346, r.udp.localPort);
}

void test_auto_calibrate_copied_default_on() {
  RuntimeConfig r;
  toRuntime(decoded("diy_reference"), r);  // absent: on
  for (uint8_t i = 0; i < 5; i++) TEST_ASSERT_TRUE(r.sliderAutoCalibrate[i]);
  toRuntime(decoded("auto_calibrate_off"), r);
  TEST_ASSERT_TRUE(r.sliderAutoCalibrate[0]);
  TEST_ASSERT_FALSE(r.sliderAutoCalibrate[1]);
  TEST_ASSERT_TRUE(r.sliderAutoCalibrate[2]);
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_diy_reference);
  RUN_TEST(test_defaults_converted_to_ms);
  RUN_TEST(test_defaults_are_empty_and_sane);
  RUN_TEST(test_screen_buses_used_only);
  RUN_TEST(test_udp_copied);
  RUN_TEST(test_auto_calibrate_copied_default_on);
  return UNITY_END();
}

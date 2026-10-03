// SPIKE ONLY. Each test asserts what the spec expects. A failure is a FINDING to record,
// not a bug to fix. TEST_MESSAGE lines carry the observed values into the log.
#include <pb_decode.h>
#include <pb_encode.h>
#include <stdio.h>
#include <string.h>
#include <unity.h>

#include "volco_config.pb.h"

static volco_config_ControllerConfig cfg;

void setUp() {}
void tearDown() {}

static bool decode(const uint8_t* b, size_t n) {
  cfg = volco_config_ControllerConfig_init_default;
  pb_istream_t s = pb_istream_from_buffer(b, n);
  bool ok = pb_decode(&s, volco_config_ControllerConfig_fields, &cfg);
  char msg[160];
  snprintf(msg, sizeof msg, "decode=%d error=%s", ok, ok ? "-" : PB_GET_ERROR(&s));
  TEST_MESSAGE(msg);
  return ok;
}

void test_empty() {
  TEST_ASSERT_TRUE(decode(nullptr, 0));
  char msg[160];
  snprintf(msg, sizeof msg, "has_screens=%d screens.brightness=%u screens.dim_enabled=%d",
           cfg.has_screens, (unsigned)cfg.screens.brightness, cfg.screens.dim_enabled);
  TEST_MESSAGE(msg);  // records whether absent submessages are default-filled
}

void test_screen_default() {
  const uint8_t b[] = {0x12, 0x02, 0x22, 0x00};
  TEST_ASSERT_TRUE(decode(b, sizeof b));
  TEST_ASSERT_EQUAL_UINT(1, cfg.buttons_count);
  TEST_ASSERT_EQUAL_UINT(volco_config_Button_screen_tag, cfg.buttons[0].which_indicator);
  TEST_ASSERT_EQUAL_UINT(0, cfg.buttons[0].indicator.screen.bus);
  TEST_ASSERT_EQUAL_UINT(0x3C, cfg.buttons[0].indicator.screen.address);
}

void test_brightness_only() {
  const uint8_t b[] = {0x32, 0x02, 0x08, 0x64};
  TEST_ASSERT_TRUE(decode(b, sizeof b));
  TEST_ASSERT_TRUE(cfg.has_screens);
  TEST_ASSERT_EQUAL_UINT(100, cfg.screens.brightness);
  TEST_ASSERT_TRUE(cfg.screens.dim_enabled);
  TEST_ASSERT_EQUAL_UINT(360, cfg.screens.transition_ms);
}

void test_buttons33_fails() {
  uint8_t b[66];
  for (int i = 0; i < 33; i++) { b[2 * i] = 0x12; b[2 * i + 1] = 0x00; }
  TEST_ASSERT_FALSE(decode(b, sizeof b));
}

void test_ssid33_fails() {
  uint8_t b[4 + 33] = {0x2A, 0x23, 0x12, 0x21};
  memset(b + 4, 'a', 33);
  TEST_ASSERT_FALSE(decode(b, sizeof b));
}

void test_bad_utf8_fails() {
  const uint8_t b[] = {0x42, 0x02, 0xC3, 0x28};
  TEST_ASSERT_FALSE(decode(b, sizeof b));
}

void test_nul_in_string() {
  const uint8_t b[] = {0x42, 0x03, 0x61, 0x00, 0x62};
  bool ok = decode(b, sizeof b);
  char msg[120];
  snprintf(msg, sizeof msg, "nul: decode=%d strlen(preset)=%u", ok, ok ? (unsigned)strlen(cfg.preset) : 0u);
  TEST_MESSAGE(msg);  // spec §6.1: a NUL must be rejected by OUR check; this records nanopb's own behaviour
}

void test_u32_overflow_fails() {
  const uint8_t b[] = {0x0A, 0x06, 0x08, 0x80, 0x80, 0x80, 0x80, 0x10};
  TEST_ASSERT_FALSE(decode(b, sizeof b));
}

void test_unknown_field_skipped() {
  const uint8_t b[] = {0x98, 0x06, 0x01, 0x32, 0x02, 0x08, 0x64};
  TEST_ASSERT_TRUE(decode(b, sizeof b));
  TEST_ASSERT_EQUAL_UINT(100, cfg.screens.brightness);
}

void test_maximal_blob_size() {
  volco_config_ControllerConfig m = volco_config_ControllerConfig_init_default;
  m.sliders_count = 16;
  for (int i = 0; i < 16; i++) { m.sliders[i].has_pin = true; m.sliders[i].pin = 39; }
  m.buttons_count = 32;
  for (int i = 0; i < 32; i++) {
    volco_config_Button& b = m.buttons[i];
    b.has_pin = true; b.pin = 39;
    b.which_indicator = volco_config_Button_led_pair_tag;
    b.indicator.led_pair.has_pin_a = true; b.indicator.led_pair.pin_a = 39;
    b.indicator.led_pair.has_pin_b = true; b.indicator.led_pair.pin_b = 39;
    b.indicator.led_pair.has_active_low = true; b.indicator.led_pair.active_low = true;
  }
  volco_config_I2cBus bus = volco_config_I2cBus_init_default;
  bus.has_sda = true; bus.sda = 39; bus.has_scl = true; bus.scl = 39;
  bus.has_clock_hz = true; bus.clock_hz = 1000000;
  m.has_i2c0 = true; m.i2c0 = bus; m.has_i2c1 = true; m.i2c1 = bus;
  m.has_udp = true;
  m.udp.has_enabled = true; m.udp.enabled = true;
  m.udp.has_ssid = true; memset(m.udp.ssid, 'a', 32); m.udp.ssid[32] = 0;
  m.udp.has_password = true; memset(m.udp.password, 'a', 64); m.udp.password[64] = 0;
  m.udp.has_pc_ip = true; m.udp.pc_ip = 0xFFFFFFFFu;
  m.udp.has_pc_port = true; m.udp.pc_port = 65535;
  m.udp.has_local_port = true; m.udp.local_port = 65535;
  m.has_screens = true;
  volco_config_ScreenBehaviour& s = m.screens;
  s.has_brightness = true; s.brightness = 255;
  s.has_transition_ms = true; s.transition_ms = 2000;
  s.has_scroll_hold_ms = true; s.scroll_hold_ms = 10000;
  s.has_scroll_speed_px_s = true; s.scroll_speed_px_s = 200;
  s.has_scroll_end_hold_ms = true; s.scroll_end_hold_ms = 10000;
  s.has_dim_enabled = true; s.dim_enabled = true;
  s.has_dim_after_s = true; s.dim_after_s = 86400;
  s.has_dim_brightness = true; s.dim_brightness = 255;
  s.has_off_enabled = true; s.off_enabled = true;
  s.has_off_after_s = true; s.off_after_s = 86400;
  s.has_pixel_shift_enabled = true; s.pixel_shift_enabled = true;
  s.has_pixel_shift_every_s = true; s.pixel_shift_every_s = 86400;
  s.has_wake_swallows_press = true; s.wake_swallows_press = true;
  m.has_input = true;
  m.input.has_debounce_ms = true; m.input.debounce_ms = 200;
  m.input.has_slider_interval_ms = true; m.input.slider_interval_ms = 100;
  m.input.has_filter_weight_pct = true; m.input.filter_weight_pct = 100;
  m.input.has_slider_deadband = true; m.input.slider_deadband = 500;
  m.has_preset = true; memset(m.preset, 'p', 24); m.preset[24] = 0;

  size_t size = 0;
  TEST_ASSERT_TRUE(pb_get_encoded_size(&size, volco_config_ControllerConfig_fields, &m));
  char msg[80];
  snprintf(msg, sizeof msg, "maximal blob size=%u", (unsigned)size);
  TEST_MESSAGE(msg);
  TEST_ASSERT_LESS_OR_EQUAL_UINT(1024, size);
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_empty);
  RUN_TEST(test_screen_default);
  RUN_TEST(test_brightness_only);
  RUN_TEST(test_buttons33_fails);
  RUN_TEST(test_ssid33_fails);
  RUN_TEST(test_bad_utf8_fails);
  RUN_TEST(test_nul_in_string);
  RUN_TEST(test_u32_overflow_fails);
  RUN_TEST(test_unknown_field_skipped);
  RUN_TEST(test_maximal_blob_size);
  return UNITY_END();
}

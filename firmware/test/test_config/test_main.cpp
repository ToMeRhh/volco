#include <string.h>
#include <unity.h>

#include <string>

#include "../golden_config.h"
#include "volco_chip.h"
#include "volco_config.h"
#include "volco_crc32.h"

using namespace volco;

void setUp() {}
void tearDown() {}

static std::string value(const volco_config_ControllerConfig& c, size_t size, const std::string& key) {
  if (key == "size") return std::to_string(size);
  if (key == "sliders") return std::to_string(c.sliders_count);
  if (key == "buttons") return std::to_string(c.buttons_count);
  if (key == "preset") return c.preset;
  if (key == "screens.brightness") return std::to_string(c.screens.brightness);
  if (key == "screens.dim_enabled") return std::to_string(c.screens.dim_enabled ? 1 : 0);
  if (key == "screens.transition_ms") return std::to_string(c.screens.transition_ms);
  if (c.buttons_count > 0) {
    const volco_config_Button& b = c.buttons[0];
    if (key == "buttons.0.kind") {
      int k = b.which_indicator == volco_config_Button_led_tag        ? 1
              : b.which_indicator == volco_config_Button_led_pair_tag ? 2
              : b.which_indicator == volco_config_Button_screen_tag   ? 3
                                                                      : 0;
      return std::to_string(k);
    }
    if (key == "buttons.0.screen.bus") return std::to_string(b.indicator.screen.bus);
    if (key == "buttons.0.screen.address") return std::to_string(b.indicator.screen.address);
  }
  return "?" + key;
}

void test_golden_file_loaded() {
  TEST_ASSERT_GREATER_OR_EQUAL(6, (int)configCases("decode-ok").size());
  TEST_ASSERT_GREATER_OR_EQUAL(9, (int)configCases("decode-fail").size());
  TEST_ASSERT_GREATER_OR_EQUAL(80, (int)configCases("validate").size());
  TEST_ASSERT_GREATER_OR_EQUAL(3, (int)configCases("crc").size());
}

void test_crc_cases() {
  for (auto& c : configCases("crc")) {
    char got[9];
    snprintf(got, sizeof got, "%08x", (unsigned)crc32(c.blob.data(), c.blob.size()));
    TEST_ASSERT_EQUAL_STRING_MESSAGE(c.rest[0].c_str(), got, c.name.c_str());
  }
}

void test_decode_ok_cases() {
  for (auto& c : configCases("decode-ok")) {
    volco_config_ControllerConfig cfg;
    TEST_ASSERT_TRUE_MESSAGE(decodeConfig(c.blob.data(), c.blob.size(), cfg), c.name.c_str());
    for (auto& kv : c.rest) {
      size_t eq = kv.find('=');
      std::string key = kv.substr(0, eq), want = kv.substr(eq + 1);
      std::string msg = c.name + " " + key;
      TEST_ASSERT_EQUAL_STRING_MESSAGE(want.c_str(), value(cfg, c.blob.size(), key).c_str(), msg.c_str());
    }
  }
}

void test_decode_fail_cases() {
  for (auto& c : configCases("decode-fail")) {
    volco_config_ControllerConfig cfg;
    TEST_ASSERT_FALSE_MESSAGE(decodeConfig(c.blob.data(), c.blob.size(), cfg), c.name.c_str());
  }
}

void test_decode_rejects_oversized_blob() {
  static uint8_t big[kMaxConfigBlob + 1] = {0};
  volco_config_ControllerConfig cfg;
  TEST_ASSERT_FALSE(decodeConfig(big, sizeof big, cfg));
}

void test_nul_prescan() {
  const uint8_t top[] = {0x42, 0x03, 0x61, 0x00, 0x62};              // preset "a\0b"
  const uint8_t nested[] = {0x2A, 0x05, 0x12, 0x03, 0x61, 0x00, 0x62}; // udp.ssid "a\0b"
  const uint8_t clean[] = {0x42, 0x02, 0x61, 0x62, 0x32, 0x02, 0x08, 0x64};
  const uint8_t truncated[] = {0x42, 0x09, 0x61};                     // malformed: left to pb_decode
  TEST_ASSERT_TRUE(hasNulInStrings(top, sizeof top));
  TEST_ASSERT_TRUE(hasNulInStrings(nested, sizeof nested));
  TEST_ASSERT_FALSE(hasNulInStrings(clean, sizeof clean));
  TEST_ASSERT_FALSE(hasNulInStrings(truncated, sizeof truncated));
}

void test_chip_table() {
  const ChipInfo& c = esp32Chip();
  TEST_ASSERT_TRUE(c.usable(0));
  TEST_ASSERT_FALSE(c.usable(1));      // UART0 TX (USB serial)
  TEST_ASSERT_FALSE(c.usable(6));      // SPI flash
  TEST_ASSERT_FALSE(c.usable(20));     // doesn't exist
  TEST_ASSERT_FALSE(c.usable(64));     // out of range, no UB
  TEST_ASSERT_FALSE(c.usable(4294967295u));
  TEST_ASSERT_TRUE(c.usable(39));
  TEST_ASSERT_FALSE(c.outputCapable(34));  // input-only
  TEST_ASSERT_TRUE(c.outputCapable(33));
  TEST_ASSERT_TRUE(c.adcCapable(36));      // ADC1
  TEST_ASSERT_TRUE(c.adcCapable(25));      // ADC2
  TEST_ASSERT_FALSE(c.adcCapable(23));
  TEST_ASSERT_TRUE(c.isAdc2(25));
  TEST_ASSERT_FALSE(c.isAdc2(33));
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_golden_file_loaded);
  RUN_TEST(test_crc_cases);
  RUN_TEST(test_decode_ok_cases);
  RUN_TEST(test_decode_fail_cases);
  RUN_TEST(test_decode_rejects_oversized_blob);
  RUN_TEST(test_nul_prescan);
  RUN_TEST(test_chip_table);
  return UNITY_END();
}

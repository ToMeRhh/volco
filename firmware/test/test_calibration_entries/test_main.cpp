#include <unity.h>
#include "volco_calibration_entries.h"

using namespace volco;

void setUp() {}
void tearDown() {}

static void learn(SliderCalibration& c, uint16_t lo, uint16_t hi) {
  c.reading(lo, 0);
  c.reading(hi, 0);
}

void test_round_trip() {
  const uint8_t pins[] = {33, 35};
  SliderCalibration saved[2];
  learn(saved[0], 170, 2550);
  saved[1].configure(false);  // not learned
  uint8_t buf[2 * kEndsEntrySize];
  TEST_ASSERT_EQUAL(12, encodeEndsEntries(pins, saved, 2, buf));
  const uint8_t want[] = {33, 1, 0x00, 0xAA, 0x09, 0xF6, 35, 0, 0x00, 0x01, 0x00, 0x00};
  TEST_ASSERT_EQUAL_MEMORY(want, buf, sizeof want);

  SliderCalibration loaded[2];
  loaded[1].configure(false);
  restoreEndsEntries(buf, sizeof buf, pins, loaded, 2);
  TEST_ASSERT_TRUE(loaded[0].learned());
  TEST_ASSERT_EQUAL(170, loaded[0].lo());
  TEST_ASSERT_EQUAL(2550, loaded[0].hi());
  TEST_ASSERT_FALSE(loaded[0].needsSave());  // what was read is what is stored
  TEST_ASSERT_FALSE(loaded[1].learned());
}

void test_matches_by_pin_after_a_middle_slider_is_removed() {
  const uint8_t before[] = {33, 35, 32};
  SliderCalibration saved[3];
  learn(saved[0], 100, 2000);
  learn(saved[1], 200, 2100);
  learn(saved[2], 300, 2200);
  uint8_t buf[3 * kEndsEntrySize];
  const size_t len = encodeEndsEntries(before, saved, 3, buf);

  const uint8_t after[] = {33, 32};
  SliderCalibration loaded[2];
  restoreEndsEntries(buf, len, after, loaded, 2);
  TEST_ASSERT_EQUAL(100, loaded[0].lo());
  TEST_ASSERT_EQUAL(2000, loaded[0].hi());
  TEST_ASSERT_EQUAL(300, loaded[1].lo());
  TEST_ASSERT_EQUAL(2200, loaded[1].hi());
}

void test_an_entry_with_another_auto_setting_is_ignored() {
  const uint8_t pins[] = {33};
  SliderCalibration saved[1];
  learn(saved[0], 170, 2550);  // auto on
  uint8_t buf[kEndsEntrySize];
  encodeEndsEntries(pins, saved, 1, buf);

  SliderCalibration loaded[1];
  loaded[0].configure(false);
  restoreEndsEntries(buf, sizeof buf, pins, loaded, 1);
  TEST_ASSERT_FALSE(loaded[0].learned());
  TEST_ASSERT_EQUAL(1234, loaded[0].map(1234));  // raw
}

void test_an_unused_pin_and_a_partial_entry_are_ignored() {
  const uint8_t buf[] = {34, 1, 0x00, 0xAA, 0x09, 0xF6, 33, 1, 0x00};
  const uint8_t pins[] = {33};
  SliderCalibration loaded[1];
  restoreEndsEntries(buf, sizeof buf, pins, loaded, 1);
  TEST_ASSERT_FALSE(loaded[0].learned());
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_round_trip);
  RUN_TEST(test_matches_by_pin_after_a_middle_slider_is_removed);
  RUN_TEST(test_an_entry_with_another_auto_setting_is_ignored);
  RUN_TEST(test_an_unused_pin_and_a_partial_entry_are_ignored);
  return UNITY_END();
}

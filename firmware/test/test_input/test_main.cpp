#include <stdlib.h>
#include <unity.h>

#include "volco_input.h"

using namespace volco;

void setUp() {}
void tearDown() {}

// The firmware's smoothing before stage 1: a float EMA, truncated.
struct FloatReference {
  float s;
  explicit FloatReference(uint16_t seed) : s(seed) {}
  uint16_t update(uint16_t raw, float w) {
    s = raw * w + s * (1.0f - w);
    return (uint16_t)s;
  }
};

void test_seed_is_the_value_and_the_reference() {
  SliderFilter f;
  f.seed(1234);
  TEST_ASSERT_EQUAL_UINT16(1234, f.value());
  TEST_ASSERT_FALSE(f.update(1234, 20, 25));
  TEST_ASSERT_EQUAL_UINT16(1234, f.value());
}

void test_matches_the_float_version_within_one_unit() {
  const uint8_t weights[] = {1, 5, 20, 50, 100};
  for (uint8_t w : weights) {
    srand(42 + w);
    SliderFilter f;
    f.seed(2000);
    FloatReference ref(2000);
    uint16_t raw = 2000;
    for (int step = 0; step < 5000; step++) {
      // A noisy walk with occasional jumps, like a slider being moved.
      if (step % 500 == 0) raw = (uint16_t)(rand() % 4096);
      int noisy = raw + (rand() % 41) - 20;
      noisy = noisy < 0 ? 0 : noisy > 4095 ? 4095 : noisy;
      f.update((uint16_t)noisy, w, 0);
      const int expected = ref.update((uint16_t)noisy, w / 100.0f);
      TEST_ASSERT_INT_WITHIN_MESSAGE(1, expected, f.value(), "fixed-point EMA drifted from the float one");
    }
  }
}

void test_rails_and_extreme_weights() {
  SliderFilter f;
  f.seed(0);
  f.update(4095, 100, 0);
  TEST_ASSERT_EQUAL_UINT16(4095, f.value());
  for (int i = 0; i < 3000; i++) f.update(0, 1, 0);
  TEST_ASSERT_LESS_OR_EQUAL_UINT16(1, f.value());
  for (int i = 0; i < 3000; i++) f.update(4095, 1, 0);
  TEST_ASSERT_GREATER_OR_EQUAL_UINT16(4094, f.value());
  TEST_ASSERT_LESS_OR_EQUAL_UINT16(4095, f.value());
}

void test_deadband_is_exclusive() {
  SliderFilter f;
  f.seed(1000);
  TEST_ASSERT_FALSE(f.update(1025, 100, 25));  // moved exactly the deadband: not reported
  TEST_ASSERT_TRUE(f.update(1026, 100, 25));
  TEST_ASSERT_EQUAL_UINT16(1026, f.value());
  TEST_ASSERT_TRUE(f.update(1000, 100, 25));   // 26 down from the last reported value
}

void test_report_moves_the_deadband_reference() {
  SliderFilter f;
  f.seed(1000);
  TEST_ASSERT_FALSE(f.update(1020, 100, 25));
  TEST_ASSERT_EQUAL_UINT16(1020, f.report());  // Hello sends 1020
  TEST_ASSERT_FALSE(f.update(1040, 100, 25));  // 20 from 1020, though 40 from the seed
  TEST_ASSERT_TRUE(f.update(1046, 100, 25));
}

void test_debounce_press_and_release() {
  ButtonDebouncer b;
  b.reset();
  TEST_ASSERT_FALSE(b.pressed());
  TEST_ASSERT_FALSE(b.update(false, 0, 30));   // goes low at 0
  TEST_ASSERT_FALSE(b.update(false, 29, 30));
  TEST_ASSERT_TRUE(b.update(false, 30, 30));
  TEST_ASSERT_TRUE(b.pressed());
  TEST_ASSERT_FALSE(b.update(false, 31, 30));  // reported once
  TEST_ASSERT_FALSE(b.update(true, 100, 30));  // bounce up...
  TEST_ASSERT_FALSE(b.update(false, 110, 30)); // ...and back down: no change
  TEST_ASSERT_FALSE(b.update(true, 120, 30));
  TEST_ASSERT_FALSE(b.update(true, 149, 30));
  TEST_ASSERT_TRUE(b.update(true, 150, 30));
  TEST_ASSERT_FALSE(b.pressed());
}

void test_debounce_across_millis_wrap() {
  ButtonDebouncer b;
  b.reset();
  const uint32_t t0 = 0xFFFFFFF0u;
  TEST_ASSERT_FALSE(b.update(false, t0, 30));
  TEST_ASSERT_FALSE(b.update(false, t0 + 20, 30));  // wraps past zero
  TEST_ASSERT_TRUE(b.update(false, t0 + 30, 30));
  TEST_ASSERT_TRUE(b.pressed());
}

int main() {
  UNITY_BEGIN();
  RUN_TEST(test_seed_is_the_value_and_the_reference);
  RUN_TEST(test_matches_the_float_version_within_one_unit);
  RUN_TEST(test_rails_and_extreme_weights);
  RUN_TEST(test_deadband_is_exclusive);
  RUN_TEST(test_report_moves_the_deadband_reference);
  RUN_TEST(test_debounce_press_and_release);
  RUN_TEST(test_debounce_across_millis_wrap);
  return UNITY_END();
}

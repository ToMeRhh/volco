#include <unity.h>
#include "volco_slider_ends.h"

using namespace volco;

void setUp() {}
void tearDown() {}

static SliderEnds learnedBetween(uint16_t lo, uint16_t hi) {
  SliderEnds e;
  e.learn(lo);
  e.learn(hi);
  return e;
}

void test_raw_until_the_span_is_learned() {
  SliderEnds e;
  TEST_ASSERT_FALSE(e.learned());
  TEST_ASSERT_EQUAL(1234, e.map(1234));
  e.learn(170);
  e.learn(1169);  // span 999
  TEST_ASSERT_FALSE(e.learned());
  TEST_ASSERT_EQUAL(800, e.map(800));
  e.learn(1170);
  TEST_ASSERT_TRUE(e.learned());
}

void test_stops_map_to_exactly_0_and_4095() {
  // Board 5: a 10k pot between 6.2k and 1.5k reads about 170 to 2550.
  SliderEnds e = learnedBetween(170, 2550);
  TEST_ASSERT_EQUAL(0, e.map(170));
  TEST_ASSERT_EQUAL(4095, e.map(2550));
  TEST_ASSERT_EQUAL(0, e.map(100));      // below the learned end
  TEST_ASSERT_EQUAL(4095, e.map(3000));  // above it
}

void test_end_bands_absorb_noise_at_the_stops() {
  SliderEnds e = learnedBetween(170, 2550);  // 1.5% of 2380 is ~35 codes
  TEST_ASSERT_EQUAL(0, e.map(170 + 30));
  TEST_ASSERT_EQUAL(4095, e.map(2550 - 30));
}

void test_middle_maps_to_the_middle() {
  SliderEnds e = learnedBetween(170, 2550);
  TEST_ASSERT_UINT16_WITHIN(1, 2048, e.map(1360));
}

void test_full_range_pot_stays_full_range() {
  SliderEnds e = learnedBetween(0, 4095);
  TEST_ASSERT_EQUAL(0, e.map(0));
  TEST_ASSERT_EQUAL(4095, e.map(4095));
  TEST_ASSERT_UINT16_WITHIN(1, 2048, e.map(2048));
}

void test_restore_takes_only_a_learned_span() {
  SliderEnds e;
  e.restore(170, 900);  // too narrow
  TEST_ASSERT_FALSE(e.learned());
  e.restore(2550, 170);  // reversed
  TEST_ASSERT_FALSE(e.learned());
  e.restore(170, 2550);
  TEST_ASSERT_TRUE(e.learned());
  TEST_ASSERT_EQUAL(170, e.lo());
  TEST_ASSERT_EQUAL(2550, e.hi());
  TEST_ASSERT_FALSE(e.needsSave());
}

void test_save_only_when_an_end_moves_past_the_step() {
  SliderEnds e;
  e.learn(1000);
  TEST_ASSERT_FALSE(e.needsSave());  // not learned yet
  e.learn(170);
  e.learn(2550);
  TEST_ASSERT_TRUE(e.needsSave());
  e.markSaved();
  TEST_ASSERT_FALSE(e.needsSave());
  e.learn(170 - SliderEnds::kSaveStep);  // noise at the stop
  e.learn(2550 + SliderEnds::kSaveStep);
  TEST_ASSERT_FALSE(e.needsSave());
  e.learn(2550 + SliderEnds::kSaveStep + 1);
  TEST_ASSERT_TRUE(e.needsSave());
}

void test_reset_forgets_the_ends() {
  SliderEnds e = learnedBetween(170, 2550);
  e.reset();
  TEST_ASSERT_FALSE(e.learned());
  TEST_ASSERT_EQUAL(1234, e.map(1234));
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_raw_until_the_span_is_learned);
  RUN_TEST(test_stops_map_to_exactly_0_and_4095);
  RUN_TEST(test_end_bands_absorb_noise_at_the_stops);
  RUN_TEST(test_middle_maps_to_the_middle);
  RUN_TEST(test_full_range_pot_stays_full_range);
  RUN_TEST(test_restore_takes_only_a_learned_span);
  RUN_TEST(test_save_only_when_an_end_moves_past_the_step);
  RUN_TEST(test_reset_forgets_the_ends);
  return UNITY_END();
}

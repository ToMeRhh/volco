#include <unity.h>
#include "volco_calibration.h"

using namespace volco;

void setUp() {}
void tearDown() {}

// Moves the slider end to end (board 5: about 170 to 2550).
static void sweepEnds(SliderCalibration& c, uint32_t now, uint16_t lo = 170, uint16_t hi = 2550) {
  c.reading(lo, now);
  c.reading(hi, now);
}

void test_auto_on_learns_from_readings() {
  SliderCalibration c;
  c.configure(true);
  TEST_ASSERT_FALSE(c.learned());
  sweepEnds(c, 0);
  TEST_ASSERT_TRUE(c.learned());
  TEST_ASSERT_EQUAL(170, c.lo());
  TEST_ASSERT_EQUAL(2550, c.hi());
  TEST_ASSERT_EQUAL(0, c.map(170));
  TEST_ASSERT_EQUAL(4095, c.map(2550));
  TEST_ASSERT_TRUE(c.needsSave());
}

void test_auto_off_learns_only_in_a_sweep() {
  SliderCalibration c;
  c.configure(false);
  sweepEnds(c, 0);
  TEST_ASSERT_FALSE(c.learned());
  TEST_ASSERT_EQUAL(1234, c.map(1234));  // raw
  TEST_ASSERT_TRUE(c.startSweep(10));
  sweepEnds(c, 20);
  TEST_ASSERT_TRUE(c.learned());
  c.done(30);
  TEST_ASSERT_FALSE(c.sweeping());
  TEST_ASSERT_TRUE(c.learned());
  c.reading(3000, 40);  // outside a sweep the ends are fixed
  TEST_ASSERT_EQUAL(2550, c.hi());
}

void test_start_forgets_and_done_keeps() {
  SliderCalibration c;
  c.restore(170, 2550);
  TEST_ASSERT_TRUE(c.startSweep(0));
  TEST_ASSERT_TRUE(c.sweeping());
  TEST_ASSERT_FALSE(c.learned());
  TEST_ASSERT_FALSE(c.startSweep(5));  // one at a time
  sweepEnds(c, 10, 300, 2000);
  TEST_ASSERT_FALSE(c.needsSave());  // not while sweeping
  c.done(20);
  TEST_ASSERT_FALSE(c.sweeping());
  TEST_ASSERT_EQUAL(300, c.lo());
  TEST_ASSERT_EQUAL(2000, c.hi());
  TEST_ASSERT_TRUE(c.needsSave());  // stored once at the end of the sweep
  c.markSaved();
  TEST_ASSERT_FALSE(c.needsSave());
}

void test_auto_on_keeps_learning_after_done() {
  SliderCalibration c;
  c.startSweep(0);
  sweepEnds(c, 10, 300, 2000);
  c.done(20);
  c.reading(2500, 30);
  TEST_ASSERT_EQUAL(2500, c.hi());
}

void test_cancel_restores_the_old_ends() {
  SliderCalibration c;
  c.restore(170, 2550);
  c.startSweep(0);
  sweepEnds(c, 10, 300, 2000);
  c.cancel();
  TEST_ASSERT_FALSE(c.sweeping());
  TEST_ASSERT_EQUAL(170, c.lo());
  TEST_ASSERT_EQUAL(2550, c.hi());
  TEST_ASSERT_FALSE(c.needsSave());  // already stored
}

void test_cancel_of_an_uncalibrated_slider_leaves_it_uncalibrated() {
  SliderCalibration c;
  c.configure(false);
  c.startSweep(0);
  sweepEnds(c, 10);
  c.cancel();
  TEST_ASSERT_FALSE(c.learned());
  TEST_ASSERT_FALSE(c.needsSave());
}

void test_done_before_learned_is_ignored() {
  SliderCalibration c;
  c.startSweep(0);
  sweepEnds(c, 10, 1000, 1500);  // span 500
  c.done(20);
  TEST_ASSERT_TRUE(c.sweeping());
  TEST_ASSERT_FALSE(c.sweepEnded());
  c.reading(2500, 30);
  c.done(40);
  TEST_ASSERT_FALSE(c.sweeping());
  TEST_ASSERT_TRUE(c.learned());
}

void test_done_and_cancel_without_a_sweep_do_nothing() {
  SliderCalibration c;
  c.restore(170, 2550);
  c.done(0);
  c.cancel();
  c.hostLost();
  TEST_ASSERT_FALSE(c.sweepEnded());
  TEST_ASSERT_FALSE(c.reportDue(0));
  TEST_ASSERT_EQUAL(170, c.lo());
}

void test_sixty_second_timeout_cancels() {
  SliderCalibration c;
  c.restore(170, 2550);
  c.startSweep(1000);
  sweepEnds(c, 2000, 300, 2000);
  c.tick(1000 + SliderCalibration::kSweepTimeoutMs - 1);
  TEST_ASSERT_TRUE(c.sweeping());
  c.tick(1000 + SliderCalibration::kSweepTimeoutMs);
  TEST_ASSERT_FALSE(c.sweeping());
  TEST_ASSERT_EQUAL(170, c.lo());
  TEST_ASSERT_EQUAL(2550, c.hi());
}

void test_host_lost_cancels() {
  SliderCalibration c;
  c.restore(170, 2550);
  c.startSweep(0);
  sweepEnds(c, 10, 300, 2000);
  c.hostLost();
  TEST_ASSERT_FALSE(c.sweeping());
  TEST_ASSERT_EQUAL(170, c.lo());
}

void test_widening_reports_at_most_every_100ms() {
  SliderCalibration c;
  TEST_ASSERT_FALSE(c.reportDue(0));  // nothing widened yet
  c.reading(2000, 0);
  TEST_ASSERT_TRUE(c.reportDue(0));
  TEST_ASSERT_FALSE(c.reportDue(1));  // consumed
  c.reading(2100, 50);
  TEST_ASSERT_FALSE(c.reportDue(50));
  TEST_ASSERT_FALSE(c.reportDue(99));
  TEST_ASSERT_TRUE(c.reportDue(100));
  c.reading(2050, 300);  // inside the ends: no report
  TEST_ASSERT_FALSE(c.reportDue(300));
}

void test_no_widening_reports_with_auto_off_outside_a_sweep() {
  SliderCalibration c;
  c.configure(false);
  c.reading(2000, 0);
  TEST_ASSERT_FALSE(c.reportDue(0));
}

static void assertEndsOnce(SliderCalibration& c, uint32_t now) {
  TEST_ASSERT_TRUE(c.reportDue(now));  // at once, whatever the interval
  TEST_ASSERT_TRUE(c.sweepEnded());
  TEST_ASSERT_FALSE(c.sweepEnded());
  TEST_ASSERT_FALSE(c.reportDue(now));
}

void test_sweep_ended_fires_once_after_done_cancel_and_timeout() {
  SliderCalibration c;
  c.startSweep(0);
  sweepEnds(c, 10);
  TEST_ASSERT_TRUE(c.reportDue(10));
  TEST_ASSERT_FALSE(c.sweepEnded());
  c.done(20);  // 10 ms after the last report
  assertEndsOnce(c, 20);

  c.startSweep(100);
  c.cancel();
  assertEndsOnce(c, 101);

  c.startSweep(200);
  c.tick(200 + SliderCalibration::kSweepTimeoutMs);
  assertEndsOnce(c, 200 + SliderCalibration::kSweepTimeoutMs);
}

void test_flags() {
  SliderCalibration c;
  TEST_ASSERT_EQUAL_HEX8(SliderCalibration::kFlagAuto, c.flags());
  c.configure(false);
  c.restore(170, 2550);
  TEST_ASSERT_EQUAL_HEX8(SliderCalibration::kFlagLearned, c.flags());
  c.startSweep(0);
  TEST_ASSERT_EQUAL_HEX8(SliderCalibration::kFlagSweeping, c.flags());
  TEST_ASSERT_FALSE(c.autoCalibrate());
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_auto_on_learns_from_readings);
  RUN_TEST(test_auto_off_learns_only_in_a_sweep);
  RUN_TEST(test_start_forgets_and_done_keeps);
  RUN_TEST(test_auto_on_keeps_learning_after_done);
  RUN_TEST(test_cancel_restores_the_old_ends);
  RUN_TEST(test_cancel_of_an_uncalibrated_slider_leaves_it_uncalibrated);
  RUN_TEST(test_done_before_learned_is_ignored);
  RUN_TEST(test_done_and_cancel_without_a_sweep_do_nothing);
  RUN_TEST(test_sixty_second_timeout_cancels);
  RUN_TEST(test_host_lost_cancels);
  RUN_TEST(test_widening_reports_at_most_every_100ms);
  RUN_TEST(test_no_widening_reports_with_auto_off_outside_a_sweep);
  RUN_TEST(test_sweep_ended_fires_once_after_done_cancel_and_timeout);
  RUN_TEST(test_flags);
  return UNITY_END();
}

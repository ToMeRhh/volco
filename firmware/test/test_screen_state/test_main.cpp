#include <unity.h>
#include "volco_protocol.h"
#include "volco_screen_state.h"

using namespace volco;

static const uint32_t MIN = 60000;
static PanelConfig all() { return PanelConfig{true, true, true, true, 2 * MIN, 30 * MIN, 3 * MIN}; }

void setUp() {}
void tearDown() {}

void test_instant_show_replaces_content() {
  ScreenSlot s(360);
  s.show(screenText("", 1, "A"), TRANSITION_NONE, 100, false);
  TEST_ASSERT_FALSE(s.inTransition(100));
  TEST_ASSERT_EQUAL_STRING("A", s.current().main);
  TEST_ASSERT_EQUAL(50, s.scrollSince(150));
}

void test_slide_runs_for_its_duration_then_settles() {
  ScreenSlot s(360);
  s.show(screenText("", 1, "A"), TRANSITION_NONE, 0, false);
  s.show(screenText("", 1, "B"), TRANSITION_SLIDE_LEFT, 1000, false);
  TEST_ASSERT_TRUE(s.inTransition(1000));
  TEST_ASSERT_FLOAT_WITHIN(0.01, 0.5, s.progress(1180));
  TEST_ASSERT_EQUAL_STRING("A", s.previous().main);
  TEST_ASSERT_EQUAL(0, s.scrollSince(1200));
  TEST_ASSERT_FALSE(s.inTransition(1360));
  TEST_ASSERT_EQUAL(40, s.scrollSince(1400));
}

void test_new_content_mid_slide_snaps() {
  // Review Focus #1: rapid presses never queue animations.
  ScreenSlot s(360);
  s.show(screenText("", 1, "A"), TRANSITION_NONE, 0, false);
  s.show(screenText("", 1, "B"), TRANSITION_SLIDE_LEFT, 1000, false);
  s.show(screenText("", 1, "C"), TRANSITION_SLIDE_LEFT, 1100, false);
  TEST_ASSERT_EQUAL_STRING("B", s.previous().main);     // the first slide snapped to its end
  TEST_ASSERT_EQUAL_STRING("C", s.current().main);
  TEST_ASSERT_FLOAT_WITHIN(0.01, 0.0, s.progress(1100));
}

void test_no_transition_while_panels_are_off() {
  ScreenSlot s(360);
  s.show(screenText("", 1, "B"), TRANSITION_SLIDE_LEFT, 1000, true);
  TEST_ASSERT_FALSE(s.inTransition(1000));
}

void test_dim_then_off_and_activity_wakes() {
  Panels p(all());
  p.activity(0);
  TEST_ASSERT_EQUAL(POWER_NORMAL, p.power(2 * MIN - 1));
  TEST_ASSERT_EQUAL(POWER_DIM, p.power(2 * MIN));
  TEST_ASSERT_EQUAL(POWER_OFF, p.power(30 * MIN));
  p.activity(30 * MIN + 5);
  TEST_ASSERT_EQUAL(POWER_NORMAL, p.power(30 * MIN + 6));
}

void test_disabled_dim_and_off_stay_normal() {
  PanelConfig c = all();
  c.dim = c.off = false;
  Panels p(c);
  TEST_ASSERT_EQUAL(POWER_NORMAL, p.power(60 * MIN));
}

void test_pixel_shift_cycles_within_one_pixel() {
  Panels p(all());
  int8_t dx, dy;
  p.shift(0, dx, dy);
  TEST_ASSERT_EQUAL(0, dx); TEST_ASSERT_EQUAL(0, dy);
  p.shift(3 * MIN, dx, dy);
  TEST_ASSERT_EQUAL(1, dx); TEST_ASSERT_EQUAL(0, dy);
  for (uint32_t k = 0; k < 20; k++) {
    p.shift(k * 3 * MIN, dx, dy);
    TEST_ASSERT_TRUE(dx >= -1 && dx <= 1 && dy >= -1 && dy <= 1);
  }
  PanelConfig c = all(); c.shift = false;
  Panels q(c);
  q.shift(3 * MIN, dx, dy);
  TEST_ASSERT_EQUAL(0, dx);
}

void test_wake_press_and_its_release_are_swallowed_only_when_off() {
  Panels p(all());
  p.activity(0);
  TEST_ASSERT_TRUE(p.filterButton(0, true, 3 * MIN));      // only dimmed: sent
  TEST_ASSERT_TRUE(p.filterButton(0, false, 3 * MIN));
  TEST_ASSERT_FALSE(p.filterButton(1, true, 40 * MIN));    // off: wakes, swallowed
  TEST_ASSERT_EQUAL(POWER_NORMAL, p.power(40 * MIN));
  TEST_ASSERT_FALSE(p.filterButton(1, false, 40 * MIN + 100));
  TEST_ASSERT_TRUE(p.filterButton(1, true, 40 * MIN + 200));
}

void test_wake_press_is_sent_when_swallowing_is_off() {
  PanelConfig c = all(); c.swallow = false;
  Panels p(c);
  p.activity(0);
  TEST_ASSERT_TRUE(p.filterButton(0, true, 40 * MIN));
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_instant_show_replaces_content);
  RUN_TEST(test_slide_runs_for_its_duration_then_settles);
  RUN_TEST(test_new_content_mid_slide_snaps);
  RUN_TEST(test_no_transition_while_panels_are_off);
  RUN_TEST(test_dim_then_off_and_activity_wakes);
  RUN_TEST(test_disabled_dim_and_off_stay_normal);
  RUN_TEST(test_pixel_shift_cycles_within_one_pixel);
  RUN_TEST(test_wake_press_and_its_release_are_swallowed_only_when_off);
  RUN_TEST(test_wake_press_is_sent_when_swallowing_is_off);
  return UNITY_END();
}

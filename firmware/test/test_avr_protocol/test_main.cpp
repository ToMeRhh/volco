// volco_protocol built with the Arduino Uno / Nano limits (env native_avr).
#include <string.h>
#include <unity.h>

#include <utility>
#include <vector>

#include "../golden_frames.h"
#include "volco_protocol.h"

using namespace volco;

void setUp() {}
void tearDown() {}

struct FakeSink : LinkSink {
  std::vector<std::vector<uint8_t>> sent;
  std::vector<std::pair<uint8_t, uint8_t>> leds;
  int screens = 0;
  void send(const uint8_t* f, size_t n) override { sent.emplace_back(f, f + n); }
  void applyLed(uint8_t b, uint8_t v) override { leds.push_back({b, v}); }
  void applyScreen(uint8_t, uint8_t, const ScreenContent&, uint32_t) override { screens++; }
  void hostLost() override {}
  uint16_t sliderValue(uint8_t) override { return 0; }
};

// Feeds a whole golden frame to the link, as main.cpp does.
static void feedGolden(Link& link, const char* name, uint32_t now) {
  FrameParser p;
  for (uint8_t b : golden(name))
    if (p.feed(b)) link.onPayload(p.payload(), p.length(), now);
}

void test_profile_values() {
  TEST_ASSERT_EQUAL(16, kMaxButtons);
  TEST_ASSERT_EQUAL(112, kMaxPayload);
  TEST_ASSERT_FALSE(kHasScreens);
}

void test_screen_frames_are_ignored() {
  FakeSink s;
  const uint8_t kinds[] = {KIND_LED, KIND_SCREEN};  // screen_muted_mic is for button 1
  Link link({0, 2, kinds, 3000, 1000, 500}, s);
  link.begin(0);
  feedGolden(link, "screen_muted_mic", 10);
  TEST_ASSERT_EQUAL(0, s.screens);
}

void test_held_led_applies_on_commit() {
  FakeSink s;
  const uint8_t kinds[] = {KIND_LED, KIND_LED, KIND_LED_PAIR};  // led_2_value_2: button 2, value 2
  Link link({0, 3, kinds, 3000, 1000, 500}, s);
  link.begin(0);
  feedGolden(link, "begin", 10);
  feedGolden(link, "led_2_value_2", 11);
  TEST_ASSERT_EQUAL(0, s.leds.size());
  feedGolden(link, "commit", 12);
  TEST_ASSERT_EQUAL(1, s.leds.size());
  TEST_ASSERT_EQUAL(2, s.leds[0].first);
  TEST_ASSERT_EQUAL(2, s.leds[0].second);
}

void test_buttons_are_capped_at_the_limit() {
  FakeSink s;
  uint8_t kinds[20];
  memset(kinds, KIND_LED, sizeof kinds);
  Link link({0, 20, kinds, 3000, 1000, 500}, s);
  link.begin(0);
  // Hello's button count byte: the last field before the kinds; the frame is AA len payload cs.
  const std::vector<uint8_t>& hello = s.sent[0];
  const size_t kindsStart = hello.size() - 1 - 16;
  TEST_ASSERT_EQUAL(16, hello[kindsStart - 1]);
}

void test_oversized_frame_is_skipped() {
  FrameParser p;
  std::vector<uint8_t> big = {kStartMarker, 200};
  for (int i = 0; i < 200; i++) big.push_back(i == 50 ? kStartMarker : 0x01);  // a marker inside
  big.push_back(0x00);                                                           // its checksum
  for (uint8_t b : big) TEST_ASSERT_FALSE(p.feed(b));
  int frames = 0;
  for (uint8_t b : golden("alive")) frames += p.feed(b) ? 1 : 0;
  TEST_ASSERT_EQUAL(1, frames);
  TEST_ASSERT_EQUAL_HEX8(MSG_ALIVE, p.payload()[0]);
}

int main() {
  UNITY_BEGIN();
  RUN_TEST(test_profile_values);
  RUN_TEST(test_screen_frames_are_ignored);
  RUN_TEST(test_held_led_applies_on_commit);
  RUN_TEST(test_buttons_are_capped_at_the_limit);
  RUN_TEST(test_oversized_frame_is_skipped);
  return UNITY_END();
}

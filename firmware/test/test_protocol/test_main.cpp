#include <unity.h>
#include <string.h>
#include <vector>
#include "../golden_frames.h"
#include "volco_protocol.h"

using namespace volco;

// ── A recording sink ─────────────────────────────────────────────────────────
struct Call { char what; uint8_t button, value, transition; ScreenContent screen; uint32_t now; };
struct FakeSink : LinkSink {
  std::vector<std::vector<uint8_t>> sent;
  std::vector<Call> calls;
  int lost = 0;
  void send(const uint8_t* f, size_t n) override { sent.emplace_back(f, f + n); }
  void applyLed(uint8_t b, uint8_t v) override { calls.push_back({'L', b, v, 0, {}, 0}); }
  void applyScreen(uint8_t b, uint8_t t, const ScreenContent& c, uint32_t now) override {
    calls.push_back({'S', b, 0, t, c, now});
  }
  void hostLost() override { lost++; }
};

static const uint8_t KINDS[] = {KIND_LED, KIND_LED_PAIR, KIND_SCREEN, KIND_SCREEN};
static LinkConfig config() { return LinkConfig{5, 4, KINDS, 3000, 1000, 500}; }

// Feeds a whole frame through a parser and hands the payload to the link.
static void deliver(Link& link, const std::vector<uint8_t>& frame, uint32_t now) {
  FrameParser p;
  for (uint8_t b : frame)
    if (p.feed(b)) link.onPayload(p.payload(), p.length(), now);
}

static std::vector<uint8_t> frameOf(std::initializer_list<uint8_t> payload) {
  uint8_t out[300];
  std::vector<uint8_t> v(payload);
  size_t n = encodeFrame(v.data(), (uint8_t)v.size(), out);
  return std::vector<uint8_t>(out, out + n);
}

void setUp() {}
void tearDown() {}

// ── Framing ─────────────────────────────────────────────────────────────────
void test_parser_resyncs_after_garbage_and_bad_checksum() {
  FrameParser p;
  std::vector<uint8_t> bytes = {0x00, 0x55, 0xAA, 0x01, 0x82, 0x00 /*bad cs*/};
  auto good = golden("heartbeat");
  bytes.insert(bytes.end(), good.begin(), good.end());
  int frames = 0;
  for (uint8_t b : bytes) if (p.feed(b)) { frames++; TEST_ASSERT_EQUAL_HEX8(0x82, p.payload()[0]); }
  TEST_ASSERT_EQUAL(1, frames);
}

void test_parser_accepts_a_255_byte_payload() {
  std::vector<uint8_t> payload(255, 0x11);
  payload[0] = 0x84;
  uint8_t out[300];
  size_t n = encodeFrame(payload.data(), 255, out);
  FrameParser p;
  int frames = 0;
  for (size_t i = 0; i < n; i++) if (p.feed(out[i])) frames++;
  TEST_ASSERT_EQUAL(1, frames);
  TEST_ASSERT_EQUAL(255, p.length());
}

// ── Golden frames ───────────────────────────────────────────────────────────
void test_encodes_controller_messages_to_golden_bytes() {
  uint8_t out[300];
  const uint8_t kinds[] = {KIND_LED, KIND_LED, KIND_LED_PAIR};
  size_t n = encodeHello(5, kinds, 3, out);
  auto g = golden("hello_led_hardware");
  TEST_ASSERT_FALSE_MESSAGE(g.empty(), "golden/frames.txt not found");
  TEST_ASSERT_EQUAL(g.size(), n);
  TEST_ASSERT_EQUAL_MEMORY(g.data(), out, n);
  n = encodeAlive(out);            g = golden("alive");            TEST_ASSERT_EQUAL_MEMORY(g.data(), out, n);
  n = encodeSlider(2, 512, out);   g = golden("slider_2_512");     TEST_ASSERT_EQUAL_MEMORY(g.data(), out, n);
  n = encodeButton(1, true, out);  g = golden("button_1_pressed"); TEST_ASSERT_EQUAL_MEMORY(g.data(), out, n);
}

static ScreenContent decodeGolden(const char* name, uint8_t& button, uint8_t& transition) {
  auto g = golden(name);
  FrameParser p;
  ScreenContent c;
  for (uint8_t b : g)
    if (p.feed(b)) TEST_ASSERT_TRUE(decodeScreen(p.payload(), p.length(), button, transition, c));
  return c;
}

void test_decodes_golden_screens() {
  uint8_t button, transition;
  ScreenContent c = decodeGolden("screen_muted_mic", button, transition);
  TEST_ASSERT_EQUAL(1, button);
  TEST_ASSERT_EQUAL(TRANSITION_NONE, transition);
  TEST_ASSERT_EQUAL(TOP_TEXT, c.topKind);
  TEST_ASSERT_EQUAL_STRING("MUTED", c.top);
  TEST_ASSERT_EQUAL(7, c.icon);
  TEST_ASSERT_EQUAL(1, c.iconFlags);
  TEST_ASSERT_EQUAL_STRING("Microphone", c.main);

  c = decodeGolden("screen_preset_dots_slide_left", button, transition);
  TEST_ASSERT_EQUAL(TRANSITION_SLIDE_LEFT, transition);
  TEST_ASSERT_EQUAL(TOP_DOTS, c.topKind);
  TEST_ASSERT_EQUAL(1, c.dotsSelected);
  TEST_ASSERT_EQUAL(3, c.dotsCount);

  c = decodeGolden("screen_blank_slide_right", button, transition);
  TEST_ASSERT_EQUAL(kNoIcon, c.icon);
  TEST_ASSERT_EQUAL(0, c.mainLen);

  c = decodeGolden("screen_hebrew_main", button, transition);
  TEST_ASSERT_EQUAL_STRING("\xD7\xA8\xD7\x9E\xD7\xA7\xD7\x95\xD7\x9C\xD7\x99\xD7\x9D", c.main);   // רמקולים
}

void test_decode_screen_rejects_overrunning_lengths() {
  const uint8_t payload[] = {0x84, 0, 0, 1, TOP_TEXT, 50, 'a', 'b'};   // claims 50 bytes, has 2
  uint8_t button, transition;
  ScreenContent c;
  TEST_ASSERT_FALSE(decodeScreen(payload, sizeof payload, button, transition, c));
}

// ── Session ─────────────────────────────────────────────────────────────────
void test_begin_sends_hello() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  TEST_ASSERT_EQUAL(1, sink.sent.size());
  TEST_ASSERT_EQUAL_HEX8(0x01, sink.sent[0][2]);
}

void test_hello_request_is_answered() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("hello_request"), 10);
  TEST_ASSERT_EQUAL(2, sink.sent.size());
  TEST_ASSERT_EQUAL_HEX8(0x01, sink.sent[1][2]);
}

void test_led_and_screen_apply_only_to_matching_kinds() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, frameOf({0x83, 0, 1}), 1);     // LED button: applied
  deliver(link, frameOf({0x83, 2, 1}), 1);     // screen button: ignored
  deliver(link, frameOf({0x83, 9, 1}), 1);     // out of range: ignored
  deliver(link, golden("screen_blank_slide_right"), 1);   // button 2 is a screen: applied
  deliver(link, golden("screen_muted_mic"), 1);           // button 1 is an LED pair: ignored
  TEST_ASSERT_EQUAL(2, sink.calls.size());
  TEST_ASSERT_EQUAL('L', sink.calls[0].what);
  TEST_ASSERT_EQUAL('S', sink.calls[1].what);
  TEST_ASSERT_EQUAL(TRANSITION_SLIDE_RIGHT, sink.calls[1].transition);
}

void test_begin_commit_holds_then_applies_together() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("begin"), 100);
  deliver(link, frameOf({0x83, 0, 1}), 110);
  deliver(link, golden("screen_blank_slide_right"), 120);
  TEST_ASSERT_EQUAL(0, sink.calls.size());
  TEST_ASSERT_TRUE(link.holding());
  deliver(link, golden("commit"), 130);
  TEST_ASSERT_EQUAL(2, sink.calls.size());
  TEST_ASSERT_EQUAL(130, sink.calls[1].now);
  TEST_ASSERT_FALSE(link.holding());
}

void test_held_replacement_keeps_the_held_transition() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("begin"), 0);
  deliver(link, frameOf({0x84, 3, TRANSITION_SLIDE_LEFT, 1, TOP_EMPTY, 0xFF, 0, 1, 'a'}), 1);
  deliver(link, frameOf({0x84, 3, TRANSITION_NONE, 1, TOP_EMPTY, 0xFF, 0, 1, 'b'}), 2);
  deliver(link, golden("commit"), 3);
  TEST_ASSERT_EQUAL(1, sink.calls.size());
  TEST_ASSERT_EQUAL(TRANSITION_SLIDE_LEFT, sink.calls[0].transition);
  TEST_ASSERT_EQUAL_STRING("b", sink.calls[0].screen.main);
}

void test_commit_fallback_after_500ms() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("begin"), 1000);
  deliver(link, frameOf({0x83, 0, 1}), 1001);
  link.tick(1499);
  TEST_ASSERT_EQUAL(0, sink.calls.size());
  link.tick(1500);
  TEST_ASSERT_EQUAL(1, sink.calls.size());
}

void test_commit_without_begin_and_empty_commit_are_noops() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("commit"), 1);
  deliver(link, golden("begin"), 2);
  deliver(link, golden("commit"), 3);
  TEST_ASSERT_EQUAL(0, sink.calls.size());
}

void test_alive_when_idle_for_a_second() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);                       // Hello at t=0
  link.tick(999);
  TEST_ASSERT_EQUAL(1, sink.sent.size());
  link.tick(1000);
  TEST_ASSERT_EQUAL(2, sink.sent.size());
  TEST_ASSERT_EQUAL_HEX8(0x02, sink.sent[1][2]);
  link.sendSlider(0, 100, 1500);       // traffic resets the idle clock
  link.tick(2400);
  TEST_ASSERT_EQUAL(3, sink.sent.size());
}

void test_host_timeout_then_hello_on_next_frame() {
  FakeSink sink; Link link(config(), sink);
  link.begin(0);
  deliver(link, golden("heartbeat"), 100);
  TEST_ASSERT_TRUE(link.hostAlive());
  link.tick(3099);
  TEST_ASSERT_EQUAL(0, sink.lost);
  link.tick(3100);
  TEST_ASSERT_EQUAL(1, sink.lost);
  TEST_ASSERT_FALSE(link.hostAlive());
  size_t before = sink.sent.size();
  deliver(link, golden("heartbeat"), 5000);
  TEST_ASSERT_TRUE(link.hostAlive());
  TEST_ASSERT_EQUAL(before + 1, sink.sent.size());
  TEST_ASSERT_EQUAL_HEX8(0x01, sink.sent.back()[2]);     // Hello
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_parser_resyncs_after_garbage_and_bad_checksum);
  RUN_TEST(test_parser_accepts_a_255_byte_payload);
  RUN_TEST(test_encodes_controller_messages_to_golden_bytes);
  RUN_TEST(test_decodes_golden_screens);
  RUN_TEST(test_decode_screen_rejects_overrunning_lengths);
  RUN_TEST(test_begin_sends_hello);
  RUN_TEST(test_hello_request_is_answered);
  RUN_TEST(test_led_and_screen_apply_only_to_matching_kinds);
  RUN_TEST(test_begin_commit_holds_then_applies_together);
  RUN_TEST(test_held_replacement_keeps_the_held_transition);
  RUN_TEST(test_commit_fallback_after_500ms);
  RUN_TEST(test_commit_without_begin_and_empty_commit_are_noops);
  RUN_TEST(test_alive_when_idle_for_a_second);
  RUN_TEST(test_host_timeout_then_hello_on_next_frame);
  return UNITY_END();
}

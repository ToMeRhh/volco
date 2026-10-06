#include <string.h>
#include <unity.h>

#include <vector>

#include "../golden_frames.h"
#include "volco_protocol.h"
#include "volco_read_reply.h"

using namespace volco;

void setUp() {}
void tearDown() {}

// Splits a reply into its frames' payloads (each frame: AA len payload checksum).
static std::vector<std::vector<uint8_t>> payloads(const uint8_t* buf, size_t n) {
  std::vector<std::vector<uint8_t>> out;
  FrameParser p;
  for (size_t i = 0; i < n; i++)
    if (p.feed(buf[i])) out.emplace_back(p.payload(), p.payload() + p.length());
  return out;
}

void test_small_blob_is_one_golden_frame() {
  static uint8_t buf[kMaxReadReply];
  const uint8_t blob[] = {0x32, 0x02, 0x08};
  size_t n = buildReadReply(7, blob, 3, 0x11223344, buf, sizeof buf);
  auto g = golden("config_data_seq7");
  TEST_ASSERT_EQUAL(g.size(), n);
  TEST_ASSERT_EQUAL_MEMORY(g.data(), buf, n);
}

void test_empty_blob_is_the_empty_golden_frame() {
  static uint8_t buf[kMaxReadReply];
  size_t n = buildReadReply(7, nullptr, 0, 0, buf, sizeof buf);
  auto g = golden("config_data_empty_seq7");
  TEST_ASSERT_EQUAL(g.size(), n);
  TEST_ASSERT_EQUAL_MEMORY(g.data(), buf, n);
}

void test_maximal_blob_is_five_ordered_chunks() {
  static uint8_t buf[kMaxReadReply];
  static uint8_t blob[1024];
  for (int i = 0; i < 1024; i++) blob[i] = (uint8_t)(i * 3);
  size_t n = buildReadReply(5, blob, 1024, 0xDEADBEEF, buf, sizeof buf);
  TEST_ASSERT_GREATER_THAN(0, (int)n);
  auto frames = payloads(buf, n);
  TEST_ASSERT_EQUAL(5, frames.size());
  std::vector<uint8_t> joined;
  for (size_t f = 0; f < frames.size(); f++) {
    const auto& p = frames[f];
    TEST_ASSERT_EQUAL_HEX8(MSG_CONFIG_DATA, p[0]);
    TEST_ASSERT_EQUAL_UINT8(5, p[1]);                              // seq
    TEST_ASSERT_EQUAL_UINT16(1024, (uint16_t)(p[2] << 8 | p[3]));  // total
    TEST_ASSERT_EQUAL_UINT16(f * 240, (uint16_t)(p[4] << 8 | p[5]));
    TEST_ASSERT_EQUAL_HEX32(0xDEADBEEF, (uint32_t)p[6] << 24 | (uint32_t)p[7] << 16 | (uint32_t)p[8] << 8 | p[9]);
    joined.insert(joined.end(), p.begin() + 10, p.end());
  }
  TEST_ASSERT_EQUAL(1024, joined.size());
  TEST_ASSERT_EQUAL_MEMORY(blob, joined.data(), 1024);
}

void test_two_chunk_reply_is_the_golden_frames() {
  static uint8_t buf[kMaxReadReply];
  uint8_t blob[250];
  for (int i = 0; i < 250; i++) blob[i] = (uint8_t)(i * 7 + 1);
  size_t n = buildReadReply(3, blob, 250, 0xF6E6129E, buf, sizeof buf);
  auto g0 = golden("config_data_250_seq3_part0");
  auto g1 = golden("config_data_250_seq3_part1");
  TEST_ASSERT_EQUAL(g0.size() + g1.size(), n);
  TEST_ASSERT_EQUAL_MEMORY(g0.data(), buf, g0.size());
  TEST_ASSERT_EQUAL_MEMORY(g1.data(), buf + g0.size(), g1.size());
}

void test_too_small_buffer_gives_zero() {
  uint8_t buf[20];
  const uint8_t blob[] = {1, 2, 3, 4, 5, 6, 7, 8, 9, 10};
  TEST_ASSERT_EQUAL(0, (int)buildReadReply(1, blob, sizeof blob, 0, buf, sizeof buf));
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_small_blob_is_one_golden_frame);
  RUN_TEST(test_empty_blob_is_the_empty_golden_frame);
  RUN_TEST(test_maximal_blob_is_five_ordered_chunks);
  RUN_TEST(test_two_chunk_reply_is_the_golden_frames);
  RUN_TEST(test_too_small_buffer_gives_zero);
  return UNITY_END();
}

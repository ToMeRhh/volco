#include <string.h>
#include <unity.h>

#include <vector>

#include "volco_blob_receiver.h"
#include "volco_config.h"
#include "volco_crc32.h"

using namespace volco;
using K = ReceiverEvent::Kind;

void setUp() {}
void tearDown() {}

static std::vector<uint8_t> blobOf(size_t n) {
  std::vector<uint8_t> b(n);
  for (size_t i = 0; i < n; i++) b[i] = (uint8_t)(i * 7 + 1);
  return b;
}

// A receiver over its own buffer, with the ESP32 sizes unless given others.
struct Rx {
  uint8_t buf[kMaxConfigBlob];
  BlobReceiver r;
  explicit Rx(uint16_t capacity = kMaxConfigBlob, uint8_t chunk = kMaxChunk) : r(buf, capacity, chunk) {}
};

// Sends begin + all chunks (`chunk` at a time) on transport t, seq s; returns the End's event.
static ReceiverEvent sendAll(BlobReceiver& r, const std::vector<uint8_t>& b, uint8_t t, uint8_t s, uint32_t now,
                             size_t chunk = kMaxChunk) {
  TEST_ASSERT_EQUAL(K::NONE, r.onBegin(t, s, (uint16_t)b.size(), crc32(b.data(), b.size()), now).kind);
  for (size_t off = 0; off < b.size(); off += chunk) {
    size_t n = b.size() - off < chunk ? b.size() - off : chunk;
    TEST_ASSERT_EQUAL(K::NONE, r.onChunk(t, s, (uint16_t)off, b.data() + off, n, now).kind);
  }
  return r.onEnd(t, s, now);
}

void test_happy_one_chunk() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(100);
  ReceiverEvent e = sendAll(r, b, 0, 7, 0);
  TEST_ASSERT_EQUAL(K::COMPLETE, e.kind);
  TEST_ASSERT_EQUAL_UINT(7, e.seq);
  TEST_ASSERT_EQUAL_UINT(100, r.size());
  TEST_ASSERT_EQUAL_MEMORY(b.data(), r.data(), 100);
  TEST_ASSERT_FALSE(r.isOpen());
}

void test_happy_five_chunks() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(kMaxConfigBlob);  // 4 x 240 + 64
  TEST_ASSERT_EQUAL(K::COMPLETE, sendAll(r, b, 1, 2, 0).kind);
  TEST_ASSERT_EQUAL_MEMORY(b.data(), r.data(), kMaxConfigBlob);
}

void test_too_large_and_zero() {
  Rx rx;
  BlobReceiver& r = rx.r;
  ReceiverEvent e = r.onBegin(0, 3, kMaxConfigBlob + 1, 0, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_TOO_LARGE, e.code);
  TEST_ASSERT_EQUAL_UINT(3, e.seq);
  TEST_ASSERT_FALSE(r.isOpen());
  e = r.onBegin(0, 4, 0, 0, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_TOO_LARGE, e.code);
}

void test_gap_is_bad_sequence() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(500);
  r.onBegin(0, 1, 500, crc32(b.data(), 500), 0);
  ReceiverEvent e = r.onChunk(0, 1, 240, b.data() + 240, 240, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, e.code);
  TEST_ASSERT_FALSE(r.isOpen());
}

void test_wrong_seq_chunk_is_bad_sequence() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(10);
  r.onBegin(0, 1, 10, crc32(b.data(), 10), 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onChunk(0, 2, 0, b.data(), 10, 0).code);
}

void test_overrun_is_bad_sequence() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(120);
  r.onBegin(0, 1, 100, 0, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onChunk(0, 1, 0, b.data(), 120, 0).code);
}

void test_bad_chunk_lengths() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(300);
  r.onBegin(0, 1, 300, 0, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onChunk(0, 1, 0, b.data(), 0, 0).code);
  r.onBegin(0, 2, 300, 0, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onChunk(0, 2, 0, b.data(), kMaxChunk + 1, 0).code);
}

void test_end_early_and_wrong_seq() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(300);
  r.onBegin(0, 1, 300, crc32(b.data(), 300), 0);
  r.onChunk(0, 1, 0, b.data(), 240, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onEnd(0, 1, 0).code);
  r.onBegin(0, 2, 10, crc32(b.data(), 10), 0);
  r.onChunk(0, 2, 0, b.data(), 10, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onEnd(0, 9, 0).code);
}

void test_bad_crc() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(50);
  r.onBegin(0, 1, 50, crc32(b.data(), 50) ^ 1u, 0);
  r.onChunk(0, 1, 0, b.data(), 50, 0);
  ReceiverEvent e = r.onEnd(0, 1, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_CRC, e.code);
}

void test_no_transfer_is_silent() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(10);
  TEST_ASSERT_EQUAL(K::NONE, r.onChunk(0, 1, 0, b.data(), 10, 0).kind);
  TEST_ASSERT_EQUAL(K::NONE, r.onEnd(0, 1, 0).kind);
}

void test_other_transport_ignored_and_does_not_refresh() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(10);
  r.onBegin(0, 1, 10, crc32(b.data(), 10), 0);
  TEST_ASSERT_EQUAL(K::NONE, r.onChunk(1, 1, 0, b.data(), 10, 1500).kind);  // other transport
  TEST_ASSERT_EQUAL(K::NONE, r.onEnd(1, 1, 1600).kind);
  TEST_ASSERT_TRUE(r.isOpen());
  r.tick(2000);  // 2000 ms after the last accepted frame (the Begin at 0)
  TEST_ASSERT_FALSE(r.isOpen());
}

void test_begin_mid_transfer_restarts() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto a = blobOf(300), b = blobOf(20);
  r.onBegin(0, 1, 300, crc32(a.data(), 300), 0);
  r.onChunk(0, 1, 0, a.data(), 240, 0);
  TEST_ASSERT_EQUAL(K::COMPLETE, sendAll(r, b, 0, 2, 10).kind);
  TEST_ASSERT_EQUAL_UINT(20, r.size());
}

void test_timeout_closes_silently() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(10);
  r.onBegin(0, 1, 10, crc32(b.data(), 10), 100);
  r.tick(2099);
  TEST_ASSERT_TRUE(r.isOpen());
  r.tick(2100);
  TEST_ASSERT_FALSE(r.isOpen());
  TEST_ASSERT_EQUAL(K::NONE, r.onChunk(0, 1, 0, b.data(), 10, 2101).kind);
}

void test_late_frames_expire_without_tick() {
  // The caller may process frames before calling tick() (e.g. after a long redraw): a chunk or End
  // arriving 2000 ms after the last accepted frame must already find the transfer closed.
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(10);
  r.onBegin(0, 1, 10, crc32(b.data(), 10), 0);
  TEST_ASSERT_EQUAL(K::NONE, r.onChunk(0, 1, 0, b.data(), 10, 2000).kind);
  TEST_ASSERT_FALSE(r.isOpen());
  r.onBegin(0, 2, 10, crc32(b.data(), 10), 0);
  r.onChunk(0, 2, 0, b.data(), 10, 100);
  TEST_ASSERT_EQUAL(K::NONE, r.onEnd(0, 2, 2100).kind);
  TEST_ASSERT_FALSE(r.isOpen());
}

void test_at_most_one_result_per_begin() {
  Rx rx;
  BlobReceiver& r = rx.r;
  auto b = blobOf(300);
  int results = 0;
  auto count = [&](ReceiverEvent e) { if (e.kind != K::NONE) results++; };
  count(r.onBegin(0, 5, 300, crc32(b.data(), 300), 0));
  count(r.onChunk(0, 5, 240, b.data(), 60, 0));     // gap: BAD_SEQUENCE, closes
  count(r.onChunk(0, 5, 0, b.data(), 240, 0));      // leftovers: silent
  count(r.onChunk(0, 5, 240, b.data() + 240, 60, 0));
  count(r.onEnd(0, 5, 0));
  count(r.onEnd(0, 5, 0));
  TEST_ASSERT_EQUAL(1, results);
  results = 0;
  ReceiverEvent done = sendAll(r, b, 0, 6, 0);       // COMPLETE counts as the one result
  count(done);
  count(r.onEnd(0, 6, 0));                           // duplicate End: silent
  TEST_ASSERT_EQUAL(1, results);
}

void test_avr_sizes() {
  Rx rx(384, 96);
  BlobReceiver& r = rx.r;
  ReceiverEvent e = r.onBegin(0, 1, 385, 0, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_TOO_LARGE, e.code);
  auto b = blobOf(384);  // four 96-byte chunks
  TEST_ASSERT_EQUAL(K::COMPLETE, sendAll(r, b, 0, 2, 0, 96).kind);
  TEST_ASSERT_EQUAL_MEMORY(b.data(), rx.buf, 384);
}

void test_avr_chunk_over_limit() {
  Rx rx(384, 96);
  BlobReceiver& r = rx.r;
  auto b = blobOf(200);
  r.onBegin(0, 3, 200, crc32(b.data(), b.size()), 0);
  ReceiverEvent e = r.onChunk(0, 3, 0, b.data(), 97, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, e.code);
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_happy_one_chunk);
  RUN_TEST(test_happy_five_chunks);
  RUN_TEST(test_too_large_and_zero);
  RUN_TEST(test_gap_is_bad_sequence);
  RUN_TEST(test_wrong_seq_chunk_is_bad_sequence);
  RUN_TEST(test_overrun_is_bad_sequence);
  RUN_TEST(test_bad_chunk_lengths);
  RUN_TEST(test_end_early_and_wrong_seq);
  RUN_TEST(test_bad_crc);
  RUN_TEST(test_no_transfer_is_silent);
  RUN_TEST(test_other_transport_ignored_and_does_not_refresh);
  RUN_TEST(test_begin_mid_transfer_restarts);
  RUN_TEST(test_timeout_closes_silently);
  RUN_TEST(test_late_frames_expire_without_tick);
  RUN_TEST(test_at_most_one_result_per_begin);
  RUN_TEST(test_avr_sizes);
  RUN_TEST(test_avr_chunk_over_limit);
  return UNITY_END();
}

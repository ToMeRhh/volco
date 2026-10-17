#include "volco_read_reply.h"

#include <string.h>

#include "volco_protocol.h"

namespace volco {

void emitReadReply(uint8_t seq, BlobSource& src, uint16_t len, uint32_t crc, uint8_t chunk, FrameOut& out) {
  if (chunk > kMaxPayload - 10) chunk = (uint8_t)(kMaxPayload - 10);
  uint8_t data[kMaxPayload - 10];
  uint8_t frame[kMaxPayload + 3];
  uint16_t offset = 0;
  do {
    const uint16_t left = (uint16_t)(len - offset);
    const uint8_t n = (uint8_t)(left > chunk ? chunk : left);
    if (n) src.read(offset, data, n);
    out.frame(frame, encodeConfigData(seq, len, offset, crc, data, n, frame));
    offset = (uint16_t)(offset + n);
  } while (offset < len);
}

namespace {
struct Pointer : BlobSource {
  const uint8_t* blob;
  explicit Pointer(const uint8_t* b) : blob(b) {}
  void read(uint16_t offset, uint8_t* out, uint8_t n) override { memcpy(out, blob + offset, n); }
};
struct Into : FrameOut {
  uint8_t* out;
  size_t cap, used = 0;
  bool overflow = false;
  Into(uint8_t* o, size_t c) : out(o), cap(c) {}
  void frame(const uint8_t* f, size_t n) override {
    if (overflow || used + n > cap) {
      overflow = true;
      return;
    }
    memcpy(out + used, f, n);
    used += n;
  }
};
}  // namespace

size_t buildReadReply(uint8_t seq, const uint8_t* blob, uint16_t len, uint32_t crc, uint8_t* out, size_t cap) {
  Pointer src(blob);
  Into into(out, cap);
  emitReadReply(seq, src, len, crc, (uint8_t)kReadChunk, into);
  return into.overflow ? 0 : into.used;
}

}  // namespace volco

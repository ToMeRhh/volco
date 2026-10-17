#pragma once
// The controller's reply to a ConfigRead (spec §5.3): the stored bytes as ConfigData frames.
// emitReadReply streams them one frame at a time from any source (EEPROM on the Uno / Nano);
// buildReadReply puts them all in one buffer so the reply leaves in one send (one UDP datagram).
#include <stddef.h>
#include <stdint.h>

#include "volco_config.h"

namespace volco {

constexpr size_t kReadChunk = 240;  // the ESP32's chunk size
constexpr size_t kMaxReadReply = 5 * (3 + 10 + kReadChunk);  // 1024 bytes in 240-byte chunks

struct BlobSource {
  virtual void read(uint16_t offset, uint8_t* out, uint8_t n) = 0;
  virtual ~BlobSource() = default;
};
struct FrameOut {
  virtual void frame(const uint8_t* f, size_t n) = 0;
  virtual ~FrameOut() = default;
};

// Every ConfigData frame for a blob of `len` bytes (one empty frame when len is 0), `chunk` data
// bytes per frame (at most kMaxPayload - 10), in order.
void emitReadReply(uint8_t seq, BlobSource& src, uint16_t len, uint32_t crc, uint8_t chunk, FrameOut& out);

// Writes every ConfigData frame for `blob` into `out` in kReadChunk pieces; returns the byte
// count, or 0 when `cap` is too small.
size_t buildReadReply(uint8_t seq, const uint8_t* blob, uint16_t len, uint32_t crc, uint8_t* out, size_t cap);

}  // namespace volco

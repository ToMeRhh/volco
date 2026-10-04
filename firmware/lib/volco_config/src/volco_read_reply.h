#pragma once
// The controller's reply to a ConfigRead (spec §5.3): the stored bytes as ConfigData frames, built
// into one buffer so the whole reply leaves in one transport send (one UDP datagram).
#include <stddef.h>
#include <stdint.h>

#include "volco_config.h"

namespace volco {

constexpr size_t kReadChunk = 240;
constexpr size_t kMaxReadReply = 5 * (3 + 10 + kReadChunk);  // 1024 bytes in 240-byte chunks

// Writes every ConfigData frame for `blob` (one empty frame when len is 0) into `out`; returns the
// byte count, or 0 when `cap` is too small.
size_t buildReadReply(uint8_t seq, const uint8_t* blob, uint16_t len, uint32_t crc, uint8_t* out, size_t cap);

}  // namespace volco

#include "volco_read_reply.h"

#include <string.h>

#include "volco_protocol.h"

namespace volco {

size_t buildReadReply(uint8_t seq, const uint8_t* blob, uint16_t len, uint32_t crc, uint8_t* out, size_t cap) {
  uint8_t frame[3 + 10 + kReadChunk];
  size_t used = 0;
  uint16_t offset = 0;
  do {
    const uint16_t n = (uint16_t)(len - offset > kReadChunk ? kReadChunk : len - offset);
    const size_t size = encodeConfigData(seq, len, offset, crc, blob ? blob + offset : nullptr, (uint8_t)n, frame);
    if (used + size > cap) return 0;
    memcpy(out + used, frame, size);
    used += size;
    offset = (uint16_t)(offset + n);
  } while (offset < len);
  return used;
}

}  // namespace volco

#include "volco_config.h"

#include <pb_decode.h>
#include <string.h>

namespace volco {
namespace {

bool readVarint(const uint8_t*& p, const uint8_t* end, uint64_t& v) {
  v = 0;
  for (int shift = 0; shift < 64; shift += 7) {
    if (p >= end) return false;
    uint8_t b = *p++;
    v |= (uint64_t)(b & 0x7F) << shift;
    if (!(b & 0x80)) return true;
  }
  return false;
}

// Walks one message's fields. Top level: field 8 (preset) is a string and field 5 (udp) a nested
// message whose fields 2 (ssid) and 3 (password) are strings. Returns false on a NUL in a string.
bool stringsClean(const uint8_t* p, const uint8_t* end, bool inUdp) {
  while (p < end) {
    uint64_t key;
    if (!readVarint(p, end, key)) return true;
    uint32_t field = (uint32_t)(key >> 3), wireType = (uint32_t)(key & 7);
    if (wireType == 0) {
      uint64_t ignored;
      if (!readVarint(p, end, ignored)) return true;
    } else if (wireType == 1) {
      if (end - p < 8) return true;
      p += 8;
    } else if (wireType == 5) {
      if (end - p < 4) return true;
      p += 4;
    } else if (wireType == 2) {
      uint64_t n;
      if (!readVarint(p, end, n) || n > (uint64_t)(end - p)) return true;
      const uint8_t* s = p;
      p += n;
      bool isString = inUdp ? (field == 2 || field == 3) : field == 8;
      if (isString && memchr(s, 0, (size_t)n)) return false;
      if (!inUdp && field == 5 && !stringsClean(s, s + n, true)) return false;
    } else {
      return true;
    }
  }
  return true;
}

}  // namespace

bool hasNulInStrings(const uint8_t* data, size_t len) {
  return len > 0 && !stringsClean(data, data + len, false);
}

bool decodeConfig(const uint8_t* data, size_t len, volco_config_ControllerConfig& out) {
  out = volco_config_ControllerConfig_init_default;
  if (len > kMaxConfigBlob) return false;
  if (hasNulInStrings(data, len)) return false;
  pb_istream_t stream = pb_istream_from_buffer(data, len);
  return pb_decode(&stream, volco_config_ControllerConfig_fields, &out);
}

}  // namespace volco

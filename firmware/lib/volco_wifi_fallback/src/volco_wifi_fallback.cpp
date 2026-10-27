#include "volco_wifi_fallback.h"

#include <string.h>

namespace volco {

namespace {

constexpr uint8_t kTransportUdp = 1;  // TRANSPORT_UDP in src/transport.h

bool readVarint(const uint8_t* b, size_t len, size_t& pos, uint64_t& v) {
  v = 0;
  for (int shift = 0; shift < 70; shift += 7) {
    if (pos >= len) return false;
    const uint8_t c = b[pos++];
    if (shift < 64) v |= (uint64_t)(c & 0x7F) << shift;
    if (!(c & 0x80)) return true;
  }
  return false;
}

// One top-level field: where it starts, its number, and (for wire type 2) its payload.
struct Field {
  size_t start, end;
  uint32_t number;
  uint8_t wire;
  size_t valueAt, valueLen;
};

// Reads the field at `pos` and moves pos past it. False when it is malformed.
bool nextField(const uint8_t* b, size_t len, size_t& pos, Field& f) {
  f.start = pos;
  uint64_t tag;
  if (!readVarint(b, len, pos, tag)) return false;
  f.number = (uint32_t)(tag >> 3);
  f.wire = (uint8_t)(tag & 7);
  f.valueAt = pos;
  f.valueLen = 0;
  switch (f.wire) {
    case 0: {
      uint64_t v;
      if (!readVarint(b, len, pos, v)) return false;
      break;
    }
    case 1:
      if (len - pos < 8) return false;
      pos += 8;
      break;
    case 2: {
      uint64_t n;
      if (!readVarint(b, len, pos, n)) return false;
      if (n > len - pos) return false;
      f.valueAt = pos;
      f.valueLen = (size_t)n;
      pos += (size_t)n;
      break;
    }
    case 5:
      if (len - pos < 4) return false;
      pos += 4;
      break;
    default:
      return false;  // groups (3, 4) and the invalid 6, 7
  }
  f.end = pos;
  return true;
}

}  // namespace

bool findField(const uint8_t* blob, size_t len, uint32_t field, const uint8_t*& value, size_t& valueLen) {
  size_t pos = 0;
  bool found = false;
  const uint8_t* v = nullptr;
  size_t n = 0;
  while (pos < len) {
    Field f;
    if (!nextField(blob, len, pos, f)) return false;
    if (f.number == field && f.wire == 2) {
      found = true;
      v = blob + f.valueAt;
      n = f.valueLen;
    }
  }
  if (found) {
    value = v;
    valueLen = n;
  }
  return found;
}

size_t replaceField(const uint8_t* blob, size_t len, uint32_t field, const uint8_t* value, size_t valueLen,
                    uint8_t* out, size_t outCap) {
  size_t w = 0;
  size_t pos = 0;
  while (pos < len) {
    Field f;
    if (!nextField(blob, len, pos, f)) return 0;
    if (f.number == field) continue;
    const size_t n = f.end - f.start;
    if (n > outCap - w) return 0;
    memcpy(out + w, blob + f.start, n);
    w += n;
  }
  if (value == nullptr && valueLen == 0) return w;
  uint8_t head[10];
  size_t h = 0;
  uint64_t tag = ((uint64_t)field << 3) | 2;
  while (tag >= 0x80) {
    head[h++] = (uint8_t)(tag | 0x80);
    tag >>= 7;
  }
  head[h++] = (uint8_t)tag;
  size_t l = valueLen;
  while (l >= 0x80) {
    head[h++] = (uint8_t)(l | 0x80);
    l >>= 7;
  }
  head[h++] = (uint8_t)l;
  if (h > outCap - w || valueLen > outCap - w - h) return 0;
  memcpy(out + w, head, h);
  w += h;
  if (valueLen > 0) memcpy(out + w, value, valueLen);
  return w + valueLen;
}

bool keepsWifiCopy(bool oldEnabled, bool newEnabled, const uint8_t* oldUdp, size_t oldLen, const uint8_t* newUdp,
                   size_t newLen) {
  if (!oldEnabled || !newEnabled) return false;
  if (oldLen != newLen) return true;
  return oldLen > 0 && memcmp(oldUdp, newUdp, oldLen) != 0;
}

void WifiFallback::begin(bool provisional, uint32_t now, uint32_t windowMs) {
  provisional_ = provisional;
  confirmed_ = false;
  start_ = now;
  window_ = windowMs;
}

void WifiFallback::onValidFrame(uint8_t transport) {
  if (provisional_ && transport == kTransportUdp) confirmed_ = true;
}

WifiFallback::Action WifiFallback::tick(uint32_t now) {
  if (!provisional_) return NONE;
  if (confirmed_) {
    provisional_ = false;
    return CONFIRM;
  }
  if ((uint32_t)(now - start_) >= window_) {
    provisional_ = false;
    return RESTORE;
  }
  return NONE;
}

}  // namespace volco

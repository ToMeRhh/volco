#pragma once
#include <stdint.h>
#include <string.h>

namespace volco {

// A 128×64 1-bit frame in the SSD1306's own page order (the same layout as U8g2's full buffer):
// byte buf[page * 128 + x] holds rows page*8 .. page*8+7 of column x, bit 0 at the top.
class Canvas {
 public:
  static constexpr int kWidth = 128, kHeight = 64;
  uint8_t buf[kWidth * kHeight / 8];

  void clear() { memset(buf, 0, sizeof buf); }
  void set(int x, int y, bool on = true) {
    if (x < 0 || y < 0 || x >= kWidth || y >= kHeight) return;
    uint8_t& b = buf[(y >> 3) * kWidth + x];
    if (on) b |= (uint8_t)(1u << (y & 7)); else b &= (uint8_t)~(1u << (y & 7));
  }
  bool get(int x, int y) const {
    if (x < 0 || y < 0 || x >= kWidth || y >= kHeight) return false;
    return buf[(y >> 3) * kWidth + x] & (1u << (y & 7));
  }
  void fillRect(int x, int y, int w, int h, bool on = true);
  void rect(int x, int y, int w, int h);
  void line(int x0, int y0, int x1, int y1, int thickness, bool on = true);
  // XBM bitmap (row-major, LSB = leftmost); only 1-bits are drawn.
  void bitmap(int x, int y, int w, int h, const uint8_t* bits, int rowBytes);
  bool operator==(const Canvas& o) const { return memcmp(buf, o.buf, sizeof buf) == 0; }
};

}  // namespace volco

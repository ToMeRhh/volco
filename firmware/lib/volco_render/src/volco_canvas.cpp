#include "volco_canvas.h"
#include <stdlib.h>
#include <string.h>

namespace volco {

void Canvas::fillRect(int x, int y, int w, int h, bool on) {
  for (int yy = y; yy < y + h; yy++)
    for (int xx = x; xx < x + w; xx++) set(xx, yy, on);
}

void Canvas::rect(int x, int y, int w, int h) {
  for (int i = 0; i < w; i++) { set(x + i, y); set(x + i, y + h - 1); }
  for (int i = 0; i < h; i++) { set(x, y + i); set(x + w - 1, y + i); }
}

void Canvas::line(int x0, int y0, int x1, int y1, int thickness, bool on) {
  int dx = abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
  int dy = -abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
  int err = dx + dy, half = thickness / 2;
  while (true) {
    fillRect(x0 - half, y0 - half, thickness, thickness, on);
    if (x0 == x1 && y0 == y1) break;
    int e2 = 2 * err;
    if (e2 >= dy) { err += dy; x0 += sx; }
    if (e2 <= dx) { err += dx; y0 += sy; }
  }
}

void Canvas::bitmap(int x, int y, int w, int h, const uint8_t* bits, int rowBytes) {
  for (int row = 0; row < h; row++)
    for (int col = 0; col < w; col++)
      if (bits[row * rowBytes + (col >> 3)] & (1u << (col & 7))) set(x + col, y + row);
}

TileBox changedTiles(const Canvas& a, const Canvas& b) {
  int x0 = 16, y0 = 8, x1 = -1, y1 = -1;
  for (int page = 0; page < Canvas::kHeight / 8; page++)
    for (int tx = 0; tx < Canvas::kWidth / 8; tx++) {
      int i = page * Canvas::kWidth + tx * 8;
      if (memcmp(a.buf + i, b.buf + i, 8) == 0) continue;
      if (tx < x0) x0 = tx;
      if (tx > x1) x1 = tx;
      if (page < y0) y0 = page;
      if (page > y1) y1 = page;
    }
  if (x1 < 0) return {0, 0, 0, 0};
  return {(uint8_t)x0, (uint8_t)y0, (uint8_t)(x1 - x0 + 1), (uint8_t)(y1 - y0 + 1)};
}

}  // namespace volco

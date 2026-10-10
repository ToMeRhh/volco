#include "volco_render.h"

#include <math.h>
#include "volco_bidi.h"

namespace volco {
namespace {

constexpr int kTextX = 2, kTextW = 124;   // text area: x 2..125
constexpr int kTopBaseline = 11, kMainBaseline = 60, kIconX = 50, kIconY = 18;

uint32_t nextCodepoint(const char* s, size_t len, size_t& i) {
  uint8_t c = (uint8_t)s[i++];
  if (c < 0x80) return c;
  int extra = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : 0;
  uint32_t cp = c & (0x3F >> extra);
  for (int k = 0; k < extra && i < len; k++) cp = (cp << 6) | ((uint8_t)s[i++] & 0x3F);
  return cp;
}

// One text line: reordered for right-to-left, centred when it fits, scrolling when it doesn't.
void drawLine(Canvas& c, const Font& f, const char* text, uint8_t len, int baseline, uint32_t since,
              const ScrollTiming& t) {
  if (len == 0) return;
  char vis[2 * kMaxText + 1];
  bool rtl = false;
  size_t n = bidiVisual(text, len, vis, sizeof vis, &rtl);
  int w = textWidth(f, vis, n);
  if (w <= kTextW) {
    drawText(c, f, kTextX + (kTextW - w) / 2, baseline, vis, n, 0, Canvas::kWidth);
    return;
  }
  int off = scrollOffset(w - kTextW + 6, since, t);
  // Left-to-right text starts at its left edge and scrolls left; right-to-left text starts at
  // its right edge (the start of the sentence) and scrolls right.
  int x = rtl ? kTextX + kTextW - w + off : kTextX - off;
  drawText(c, f, x, baseline, vis, n, kTextX, kTextX + kTextW);
}

void drawDots(Canvas& c, uint8_t selected, uint8_t count) {
  if (count == 0) return;
  if (count > 12) count = 12;
  int total = count * 10 - 4;
  int x0 = 64 - total / 2;
  for (int i = 0; i < count; i++) {
    if (i == selected) c.fillRect(x0 + i * 10, 5, 6, 6);
    else c.rect(x0 + i * 10, 5, 6, 6);
  }
}

}  // namespace

const Glyph* findGlyph(const Font& f, uint32_t cp) {
  int lo = 0, hi = (int)f.glyphCount - 1;
  while (lo <= hi) {
    int mid = (lo + hi) / 2;
    if (f.glyphs[mid].codepoint == cp) return &f.glyphs[mid];
    if (f.glyphs[mid].codepoint < cp) lo = mid + 1; else hi = mid - 1;
  }
  return nullptr;
}

int textWidth(const Font& f, const char* s, size_t len) {
  int w = 0;
  for (size_t i = 0; i < len;) {
    const Glyph* g = findGlyph(f, nextCodepoint(s, len, i));
    if (g) w += g->advance;
  }
  return w;
}

void drawText(Canvas& c, const Font& f, int x, int baseline, const char* s, size_t len, int clipX0, int clipX1) {
  for (size_t i = 0; i < len;) {
    const Glyph* g = findGlyph(f, nextCodepoint(s, len, i));
    if (!g) continue;
    const uint8_t* bits = f.bitmaps + g->offset;
    int rowBytes = (g->width + 7) / 8;
    for (int row = 0; row < g->height; row++)
      for (int col = 0; col < g->width; col++) {
        int px = x + g->xOffset + col;
        if (px < clipX0 || px >= clipX1) continue;
        if (bits[row * rowBytes + (col >> 3)] & (1u << (col & 7))) c.set(px, baseline + g->yOffset + row);
      }
    x += g->advance;
  }
}

int scrollOffset(int distance, uint32_t since, const ScrollTiming& t) {
  if (distance <= 0 || t.speedPxS == 0) return 0;
  uint32_t scrollMs = (uint32_t)distance * 1000u / t.speedPxS;
  uint32_t k = since % (t.holdMs + scrollMs + t.endHoldMs);
  if (k < t.holdMs) return 0;
  if (k < t.holdMs + scrollMs) return (int)((k - t.holdMs) * t.speedPxS / 1000u);
  return distance;
}

void renderScreen(Canvas& c, const ScreenContent& s, uint32_t since, const ScrollTiming& t) {
  c.clear();
  if (s.layout != 1) {  // a layout this firmware doesn't know: just the main text (spec §3.3)
    drawLine(c, kFontMain, s.main, s.mainLen, 36, since, t);
    return;
  }
  if (s.topKind == TOP_TEXT) drawLine(c, kFontTop, s.top, s.topLen, kTopBaseline, since, t);
  else if (s.topKind == TOP_DOTS) drawDots(c, s.dotsSelected, s.dotsCount);
  if (s.icon != kNoIcon) {
    uint8_t id = s.icon < kIconCount ? s.icon : 0;  // unknown icon → generic
    c.bitmap(kIconX, kIconY, kIconSize, kIconSize, kIconBits[kIconFirstFrame[id]], kIconRowBytes);
    if (s.iconFlags & 1) {  // muted: a slash with a dark outline so it reads over filled shapes
      c.line(kIconX + 1, kIconY + 1, kIconX + 26, kIconY + 26, 5, false);
      c.line(kIconX + 1, kIconY + 1, kIconX + 26, kIconY + 26, 2, true);
    }
  }
  drawLine(c, kFontMain, s.main, s.mainLen, kMainBaseline, since, t);
}

ScreenContent notConnectedScreen() { return screenText("NOT CONNECTED", kNoIcon, "Volco"); }

float easeInOut(float p) {
  if (p <= 0) return 0;
  if (p >= 1) return 1;
  return p < 0.5f ? 4 * p * p * p : 1 - powf(-2 * p + 2, 3) / 2;
}

void composeSlide(const Canvas& from, const Canvas& to, float progress, bool left, Canvas& out) {
  int off = (int)lroundf(easeInOut(progress) * Canvas::kWidth);
  for (int x = 0; x < Canvas::kWidth; x++) {
    int sx = left ? x + off : x - off;
    const Canvas& src = left ? (sx < Canvas::kWidth ? from : to) : (sx >= 0 ? from : to);
    int col = left ? (sx < Canvas::kWidth ? sx : sx - Canvas::kWidth) : (sx >= 0 ? sx : sx + Canvas::kWidth);
    for (int page = 0; page < 8; page++) out.buf[page * Canvas::kWidth + x] = src.buf[page * Canvas::kWidth + col];
  }
}

void shiftFrame(const Canvas& in, int dx, int dy, Canvas& out) {
  for (int x = 0; x < Canvas::kWidth; x++) {
    int sx = x - dx;
    uint64_t column = 0;
    if (sx >= 0 && sx < Canvas::kWidth)
      for (int page = 0; page < 8; page++) column |= (uint64_t)in.buf[page * Canvas::kWidth + sx] << (8 * page);
    column = dy >= 0 ? column << dy : column >> -dy;
    for (int page = 0; page < 8; page++) out.buf[page * Canvas::kWidth + x] = (uint8_t)(column >> (8 * page));
  }
}

}  // namespace volco

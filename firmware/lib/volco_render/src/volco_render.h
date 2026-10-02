#pragma once
// Layout 1 ("C nudged", spec §2.1) drawn into a Canvas, plus the slide and pixel shift.
#include <stddef.h>
#include <stdint.h>
#include "volco_canvas.h"
#include "volco_fonts.h"
#include "volco_icons.h"
#include "volco_protocol.h"

namespace volco {

struct ScrollTiming {
  uint32_t holdMs, endHoldMs;
  uint16_t speedPxS;
};

const Glyph* findGlyph(const Font& f, uint32_t cp);
int textWidth(const Font& f, const char* utf8, size_t len);
// Draws UTF-8 text (already in visual order) with its pen at x on `baseline`; only columns in
// [clipX0, clipX1) are touched. Missing glyphs draw nothing and take no space.
void drawText(Canvas& c, const Font& f, int x, int baseline, const char* utf8, size_t len, int clipX0, int clipX1);
// How far text wider than its area by `distancePx` has scrolled after showing for sinceMs.
int scrollOffset(int distancePx, uint32_t sinceMs, const ScrollTiming& t);
// Clears `c` and draws one screen. sinceMs drives scrolling (ScreenSlot::scrollSince).
void renderScreen(Canvas& c, const ScreenContent& s, uint32_t sinceMs, const ScrollTiming& t);
// The local screen shown at boot and after the PC goes silent (spec §2.4).
ScreenContent notConnectedScreen();
float easeInOut(float p);
// `left`: new content enters from the right (slide left); otherwise from the left.
void composeSlide(const Canvas& from, const Canvas& to, float progress, bool left, Canvas& out);
void shiftFrame(const Canvas& in, int dx, int dy, Canvas& out);

}  // namespace volco

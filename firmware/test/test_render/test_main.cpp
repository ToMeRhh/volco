#include <unity.h>
#include <string.h>
#include <stdlib.h>
#include "../golden_render.h"
#include "volco_render.h"

using namespace volco;

static const ScrollTiming T = {2000, 1200, 30};

static bool anyIn(const Canvas& c, int x0, int y0, int x1, int y1) {   // [x0,x1) × [y0,y1)
  for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) if (c.get(x, y)) return true;
  return false;
}
static int count(const Canvas& c) {
  int n = 0;
  for (int y = 0; y < 64; y++) for (int x = 0; x < 128; x++) n += c.get(x, y);
  return n;
}

void setUp() {}
void tearDown() {}

void test_canvas_page_layout() {
  Canvas c; c.clear();
  c.set(5, 9);
  TEST_ASSERT_EQUAL_HEX8(0x02, c.buf[1 * 128 + 5]);   // page 1, bit 1
  TEST_ASSERT_TRUE(c.get(5, 9));
  c.set(-1, 0); c.set(128, 0); c.set(0, 64);           // ignored
  TEST_ASSERT_EQUAL(1, count(c));
}

void test_glyph_lookup() {
  TEST_ASSERT_NOT_NULL(findGlyph(kFontMain, 'A'));
  TEST_ASSERT_NOT_NULL(findGlyph(kFontTop, 0x5E9));    // ש
  TEST_ASSERT_NULL(findGlyph(kFontMain, 0x4E00));
  int w = findGlyph(kFontMain, 'A')->advance + findGlyph(kFontMain, 'B')->advance;
  TEST_ASSERT_EQUAL(w, textWidth(kFontMain, "AB", 2));
}

void test_blank_screen_is_empty() {
  Canvas c;
  ScreenContent blank;
  renderScreen(c, blank, 0, T);
  TEST_ASSERT_EQUAL(0, count(c));
}

void test_slots_stay_in_their_areas() {
  Canvas c;
  renderScreen(c, screenText("MUTED", kNoIcon, ""), 0, T);
  TEST_ASSERT_TRUE(anyIn(c, 0, 0, 128, 16));
  TEST_ASSERT_FALSE(anyIn(c, 0, 16, 128, 64));

  renderScreen(c, screenText("", kNoIcon, "Speakers"), 0, T);
  TEST_ASSERT_TRUE(anyIn(c, 0, 46, 128, 64));
  TEST_ASSERT_FALSE(anyIn(c, 0, 0, 128, 46));

  renderScreen(c, screenText("", 1, ""), 0, T);
  TEST_ASSERT_TRUE(anyIn(c, 50, 18, 78, 46));
  TEST_ASSERT_FALSE(anyIn(c, 0, 0, 50, 64));
  TEST_ASSERT_FALSE(anyIn(c, 78, 0, 128, 64));
}

void test_muted_slash_and_unknown_icon() {
  Canvas plain, muted, unknown, generic;
  ScreenContent s = screenText("", 7, "");
  renderScreen(plain, s, 0, T);
  s.iconFlags = 1;
  renderScreen(muted, s, 0, T);
  TEST_ASSERT_FALSE(plain == muted);
  renderScreen(unknown, screenText("", 200, ""), 0, T);
  renderScreen(generic, screenText("", 0, ""), 0, T);
  TEST_ASSERT_TRUE(unknown == generic);
}

void test_dots_fill_the_selected_one() {
  Canvas c;
  ScreenContent s;
  s.topKind = TOP_DOTS; s.dotsSelected = 1; s.dotsCount = 3;
  renderScreen(c, s, 0, T);
  // 3 dots: total width 26, start x = 64 - 13 = 51; the second dot is at x 61..66, rows 5..10.
  for (int y = 5; y < 11; y++) for (int x = 61; x < 67; x++) TEST_ASSERT_TRUE(c.get(x, y));
  TEST_ASSERT_FALSE(c.get(53, 7));   // inside the first, outlined dot
}

void test_scroll_offset_timing() {
  TEST_ASSERT_EQUAL(0, scrollOffset(60, 0, T));
  TEST_ASSERT_EQUAL(0, scrollOffset(60, 1999, T));
  TEST_ASSERT_EQUAL(30, scrollOffset(60, 3000, T));
  TEST_ASSERT_EQUAL(60, scrollOffset(60, 4100, T));       // scrolled 2000 ms, holding at the end
  TEST_ASSERT_EQUAL(0, scrollOffset(60, 5200, T));        // cycle = 2000 + 2000 + 1200
}

void test_long_text_scrolls_and_is_clipped() {
  Canvas a, b;
  ScreenContent s = screenText("", kNoIcon, "Speakers (Realtek High Definition Audio)");
  renderScreen(a, s, 0, T);
  renderScreen(b, s, 4000, T);
  TEST_ASSERT_FALSE(a == b);
  TEST_ASSERT_FALSE(anyIn(a, 0, 0, 2, 64));
  TEST_ASSERT_FALSE(anyIn(a, 126, 0, 128, 64));
}

void test_slide_composition() {
  Canvas from, to, out;
  from.clear(); to.clear();
  from.fillRect(64, 0, 1, 64);    // a column at x 64 in "from"
  to.fillRect(0, 0, 1, 64);       // a column at x 0 in "to"
  composeSlide(from, to, 0.0f, true, out);  TEST_ASSERT_TRUE(out == from);
  composeSlide(from, to, 1.0f, true, out);  TEST_ASSERT_TRUE(out == to);
  composeSlide(from, to, 0.5f, true, out);  // halfway: from's x 64 is at 0, to's x 0 is at 64
  TEST_ASSERT_TRUE(out.get(0, 10));
  TEST_ASSERT_TRUE(out.get(64, 10));
}

void test_shift_moves_pixels() {
  Canvas in, out;
  in.clear(); in.set(10, 10);
  shiftFrame(in, 1, 1, out);
  TEST_ASSERT_TRUE(out.get(11, 11));
  TEST_ASSERT_EQUAL(1, count(out));
  shiftFrame(in, -1, -1, out);
  TEST_ASSERT_TRUE(out.get(9, 9));
}

void test_changed_tiles() {
  Canvas a, b;
  a.clear(); b.clear();
  TileBox none = changedTiles(a, b);
  TEST_ASSERT_EQUAL(0, none.w);

  b.set(9, 17);                                         // tile x 1, page 2
  TileBox one = changedTiles(a, b);
  TEST_ASSERT_EQUAL(1, one.x); TEST_ASSERT_EQUAL(2, one.y);
  TEST_ASSERT_EQUAL(1, one.w); TEST_ASSERT_EQUAL(1, one.h);

  b.set(127, 63);                                       // tile x 15, page 7: the box spans both
  TileBox span = changedTiles(a, b);
  TEST_ASSERT_EQUAL(1, span.x); TEST_ASSERT_EQUAL(2, span.y);
  TEST_ASSERT_EQUAL(15, span.w); TEST_ASSERT_EQUAL(6, span.h);

  memset(a.buf, 0xFF, sizeof a.buf);                    // the first push after boot
  b.clear();
  TileBox all = changedTiles(a, b);
  TEST_ASSERT_EQUAL(0, all.x); TEST_ASSERT_EQUAL(0, all.y);
  TEST_ASSERT_EQUAL(16, all.w); TEST_ASSERT_EQUAL(8, all.h);
}

void test_animation_changes_only_the_icon_tiles() {
  Canvas f0, f1;
  ScreenContent s = screenText("PLAYING", kIcon_equalizer, "Spotify");
  renderScreen(f0, s, 0, T);
  renderScreen(f1, s, kIconFrameMs, T);
  TileBox box = changedTiles(f0, f1);
  // The icon is x 50..77, y 18..45: tiles x 6..9, pages 2..5, at most 16 tiles (128 bytes).
  TEST_ASSERT_TRUE(box.w > 0);
  TEST_ASSERT_TRUE(box.x >= 6 && box.x + box.w <= 10);
  TEST_ASSERT_TRUE(box.y >= 2 && box.y + box.h <= 6);
}

void test_not_connected_screen() {
  ScreenContent s = notConnectedScreen();
  TEST_ASSERT_EQUAL_STRING("NOT CONNECTED", s.top);
  TEST_ASSERT_EQUAL_STRING("Volco", s.main);
  TEST_ASSERT_EQUAL(kNoIcon, s.icon);
}

void test_animated_icon_frames() {
  Canvas f0, f1, again, slid;
  ScreenContent s = screenText("PLAYING", kIcon_equalizer, "Spotify");
  renderScreen(f0, s, 0, T);
  renderScreen(f1, s, kIconFrameMs, T);
  TEST_ASSERT_FALSE(f0 == f1);                          // the bars moved
  renderScreen(again, s, 4u * kIconFrameMs, T);
  TEST_ASSERT_TRUE(f0 == again);                        // 4 frames, then round again
  renderScreen(slid, s, kIconFrameMs - 1, T);
  TEST_ASSERT_TRUE(f0 == slid);                         // a frame lasts kIconFrameMs
  // Only the icon area changes between frames.
  for (int y = 0; y < 64; y++) for (int x = 0; x < 128; x++)
    if (x < 50 || x >= 78 || y < 18 || y >= 46) TEST_ASSERT_EQUAL(f0.get(x, y), f1.get(x, y));
}

void test_still_icons_ignore_the_clock() {
  Canvas a, b;
  renderScreen(a, screenText("PAUSED", kIcon_equalizerstill, "Spotify"), 0, T);
  renderScreen(b, screenText("PAUSED", kIcon_equalizerstill, "Spotify"), 3 * kIconFrameMs, T);
  TEST_ASSERT_TRUE(a == b);
  Canvas unknown, generic;
  renderScreen(unknown, screenText("", 200, ""), 5 * kIconFrameMs, T);
  renderScreen(generic, screenText("", 0, ""), 0, T);
  TEST_ASSERT_TRUE(unknown == generic);                 // unknown id → generic, at any time
}

void test_muted_slash_on_every_frame() {
  for (uint32_t f = 0; f < kIconFrames[kIcon_equalizer]; f++) {
    Canvas plain, muted;
    ScreenContent s = screenText("", kIcon_equalizer, "");
    renderScreen(plain, s, f * kIconFrameMs, T);
    s.iconFlags = 1;
    renderScreen(muted, s, f * kIconFrameMs, T);
    TEST_ASSERT_FALSE(plain == muted);
    TEST_ASSERT_TRUE(muted.get(51, 19));                // the slash starts at the icon's top-left
  }
}

// The renders shared with the web simulator (firmware/test/golden/render.txt). With
// VOLCO_WRITE_GOLDEN set, the canvases are rewritten from this renderer instead of checked.
void test_golden_renders() {
  std::vector<std::string> header;
  std::vector<GoldenRender> cases;
  TEST_ASSERT_TRUE_MESSAGE(loadGoldenRenders(header, cases), "golden/render.txt not found");
  TEST_ASSERT_TRUE(cases.size() >= 16);
  const bool write = getenv("VOLCO_WRITE_GOLDEN") != nullptr;
  for (GoldenRender& g : cases) {
    uint8_t button, transition;
    ScreenContent s;
    TEST_ASSERT_TRUE_MESSAGE(decodeScreen(g.payload.data(), (uint8_t)g.payload.size(), button, transition, s), g.name.c_str());
    Canvas c;
    renderScreen(c, s, g.since, T);
    if (write) { g.canvas.assign(c.buf, c.buf + sizeof c.buf); continue; }
    TEST_ASSERT_EQUAL_MESSAGE(sizeof c.buf, g.canvas.size(), g.name.c_str());
    TEST_ASSERT_EQUAL_HEX8_ARRAY_MESSAGE(g.canvas.data(), c.buf, sizeof c.buf, g.name.c_str());
  }
  if (write) TEST_ASSERT_TRUE(writeGoldenRenders(header, cases));
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_canvas_page_layout);
  RUN_TEST(test_glyph_lookup);
  RUN_TEST(test_blank_screen_is_empty);
  RUN_TEST(test_slots_stay_in_their_areas);
  RUN_TEST(test_muted_slash_and_unknown_icon);
  RUN_TEST(test_dots_fill_the_selected_one);
  RUN_TEST(test_scroll_offset_timing);
  RUN_TEST(test_long_text_scrolls_and_is_clipped);
  RUN_TEST(test_slide_composition);
  RUN_TEST(test_shift_moves_pixels);
  RUN_TEST(test_changed_tiles);
  RUN_TEST(test_animation_changes_only_the_icon_tiles);
  RUN_TEST(test_not_connected_screen);
  RUN_TEST(test_animated_icon_frames);
  RUN_TEST(test_still_icons_ignore_the_clock);
  RUN_TEST(test_muted_slash_on_every_frame);
  RUN_TEST(test_golden_renders);
  return UNITY_END();
}

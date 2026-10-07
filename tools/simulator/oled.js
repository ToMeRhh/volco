// The controller's screens, ported from firmware/lib/volco_render (layout 1, slide, pixel shift),
// volco_bidi (visual order) and volco_screen_state (slots and panel power), so a simulated screen
// is pixel-identical to the hardware. Time is passed in. No DOM: index.html loads it as a plain
// script (globalThis.VolcoOled), the Node tests with require().
(function (root) {
  'use strict';
  const node = typeof module === 'object' && module.exports;
  const FONTS = node ? require('./fonts.js') : root.VOLCO_FONTS;
  const ICONS = node ? require('./icons.js') : root.VOLCO_ICONS;
  const P = node ? require('./protocol.js') : root.VolcoProtocol;

  const W = 128, H = 64;
  const TEXT_X = 2, TEXT_W = 124, TOP_BASELINE = 11, MAIN_BASELINE = 60, ICON_X = 50, ICON_Y = 18;

  // ── Canvas: 128×64, 1 bit, SSD1306 page order (buf[page * 128 + x], bit 0 = top row) ──────
  function canvas() { return new Uint8Array(W * H / 8); }
  function set(c, x, y, on = true) {
    if (x < 0 || y < 0 || x >= W || y >= H) return;
    const i = (y >> 3) * W + x, bit = 1 << (y & 7);
    if (on) c[i] |= bit; else c[i] &= ~bit;
  }
  function get(c, x, y) { return x >= 0 && y >= 0 && x < W && y < H && (c[(y >> 3) * W + x] >> (y & 7)) & 1; }
  function fillRect(c, x, y, w, h, on = true) {
    for (let yy = y; yy < y + h; yy++) for (let xx = x; xx < x + w; xx++) set(c, xx, yy, on);
  }
  function rect(c, x, y, w, h) {
    for (let i = 0; i < w; i++) { set(c, x + i, y); set(c, x + i, y + h - 1); }
    for (let i = 0; i < h; i++) { set(c, x, y + i); set(c, x + w - 1, y + i); }
  }
  function line(c, x0, y0, x1, y1, thickness, on = true) {
    const dx = Math.abs(x1 - x0), sx = x0 < x1 ? 1 : -1, dy = -Math.abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
    let err = dx + dy;
    const half = Math.trunc(thickness / 2);
    for (;;) {
      fillRect(c, x0 - half, y0 - half, thickness, thickness, on);
      if (x0 === x1 && y0 === y1) break;
      const e2 = 2 * err;
      if (e2 >= dy) { err += dy; x0 += sx; }
      if (e2 <= dx) { err += dx; y0 += sy; }
    }
  }

  // ── Fonts (fonts.js: the firmware's glyph tables) ─────────────────────────────────────────
  const fonts = {};
  for (const key of ['top', 'main']) {
    const f = FONTS[key], bits = new Uint8Array(f.bitmaps.length / 2);
    for (let i = 0; i < bits.length; i++) bits[i] = parseInt(f.bitmaps.substr(i * 2, 2), 16);
    const map = new Map(f.glyphs.map(([cp, w, h, xo, yo, adv, off]) => [cp, { w, h, xo, yo, adv, off }]));
    fonts[key] = { map, bits };
  }
  const textWidth = (font, cps) => cps.reduce((w, cp) => w + (font.map.get(cp)?.adv ?? 0), 0);
  function drawText(c, font, x, baseline, cps, clipX0, clipX1) {
    for (const cp of cps) {
      const g = font.map.get(cp);
      if (!g) continue;   // missing glyphs draw nothing and take no space
      const rowBytes = (g.w + 7) >> 3;
      for (let row = 0; row < g.h; row++)
        for (let col = 0; col < g.w; col++) {
          const px = x + g.xo + col;
          if (px < clipX0 || px >= clipX1) continue;
          if (font.bits[g.off + row * rowBytes + (col >> 3)] & (1 << (col & 7))) set(c, px, baseline + g.yo + row);
        }
      x += g.adv;
    }
  }

  // ── Bidi (volco_bidi): logical → visual order, a small subset of the Unicode algorithm ────
  const L = 0, R = 1, EN = 2, CS = 3, N = 4;
  function classify(cp) {
    if ((cp >= 0x5D0 && cp <= 0x5EA) || (cp >= 0x5F0 && cp <= 0x5F4)) return R;
    if (cp >= 0x30 && cp <= 0x39) return EN;
    if (cp === 0x2E || cp === 0x2C || cp === 0x3A || cp === 0x2F) return CS;
    if ((cp >= 0x41 && cp <= 0x5A) || (cp >= 0x61 && cp <= 0x7A)) return L;
    if (cp >= 0xC0 && cp <= 0x24F && cp !== 0xD7 && cp !== 0xF7) return L;
    if (cp >= 0x370 && cp < 0x590) return L;
    return N;
  }
  const MIRROR = { 0x28: 0x29, 0x29: 0x28, 0x5B: 0x5D, 0x5D: 0x5B, 0x7B: 0x7D, 0x7D: 0x7B, 0x3C: 0x3E, 0x3E: 0x3C };
  /** { cps: code points in visual order, rtl: paragraph direction }. At most 128 code points. */
  function bidiVisual(text) {
    const cp = Array.from(text, ch => ch.codePointAt(0)).slice(0, 128), n = cp.length;
    const cls = cp.map(classify);
    for (let i = 1; i + 1 < n; i++) if (cls[i] === CS && cls[i - 1] === EN && cls[i + 1] === EN) cls[i] = EN;
    let paraRtl = false;
    for (let i = 0; i < n; i++) if (cls[i] === L || cls[i] === R) { paraRtl = cls[i] === R; break; }
    const dir = new Array(n).fill(false);
    let lastStrongRtl = paraRtl;
    for (let i = 0; i < n; i++) {
      if (cls[i] === L || cls[i] === R) lastStrongRtl = cls[i] === R;
      if (cls[i] === L) dir[i] = false;
      else if (cls[i] === R) dir[i] = true;
      else if (cls[i] === EN) dir[i] = lastStrongRtl;
    }
    for (let i = 0; i < n; i++) {
      if (cls[i] !== N && cls[i] !== CS) continue;
      let j = i;
      while (j < n && (cls[j] === N || cls[j] === CS)) j++;
      const before = i > 0 ? dir[i - 1] : paraRtl, after = j < n ? dir[j] : paraRtl;
      const d = before === after ? before : paraRtl;
      for (let k = i; k < j; k++) dir[k] = d;
      i = j - 1;
    }
    const runs = [];
    for (let i = 0; i < n;) { let j = i; while (j < n && dir[j] === dir[i]) j++; runs.push([i, j]); i = j; }
    const out = [];
    for (let r = 0; r < runs.length; r++) {
      const [s, e] = runs[paraRtl ? runs.length - 1 - r : r];
      if (!dir[s]) { for (let i = s; i < e; i++) out.push(cp[i]); continue; }
      for (let i = e - 1; i >= s;) {
        if (cls[i] === EN) {
          let k = i;
          while (k - 1 >= s && cls[k - 1] === EN) k--;
          for (let m = k; m <= i; m++) out.push(cp[m]);
          i = k - 1;
        } else { out.push(MIRROR[cp[i]] ?? cp[i]); i--; }
      }
    }
    return { cps: out, rtl: paraRtl };
  }

  // ── Layout 1 (volco_render) ───────────────────────────────────────────────────────────────
  /** Default scroll timing; the stored config's ScreenBehaviour overrides it. */
  const SCROLL = { holdMs: 2000, endHoldMs: 1200, speedPxS: 30 };
  function scrollOffset(distance, since, t) {
    if (distance <= 0 || t.speedPxS === 0) return 0;
    const scrollMs = Math.floor(distance * 1000 / t.speedPxS);
    const k = since % (t.holdMs + scrollMs + t.endHoldMs);
    if (k < t.holdMs) return 0;
    if (k < t.holdMs + scrollMs) return Math.floor((k - t.holdMs) * t.speedPxS / 1000);
    return distance;
  }
  function drawLine(c, font, text, baseline, since, t) {
    if (!text) return;
    const { cps, rtl } = bidiVisual(text);
    const w = textWidth(font, cps);
    if (w <= TEXT_W) { drawText(c, font, TEXT_X + Math.trunc((TEXT_W - w) / 2), baseline, cps, 0, W); return; }
    const off = scrollOffset(w - TEXT_W + 6, since, t);
    drawText(c, font, rtl ? TEXT_X + TEXT_W - w + off : TEXT_X - off, baseline, cps, TEXT_X, TEXT_X + TEXT_W);
  }
  function drawDots(c, selected, count) {
    if (count === 0) return;
    count = Math.min(count, 12);
    const x0 = 64 - Math.trunc((count * 10 - 4) / 2);
    for (let i = 0; i < count; i++) {
      if (i === selected) fillRect(c, x0 + i * 10, 5, 6, 6); else rect(c, x0 + i * 10, 5, 6, 6);
    }
  }
  function drawIcon(c, id) {
    const rows = ICONS[id < ICONS.length ? id : 0];
    rows.forEach((row, y) => { for (let x = 0; x < row.length; x++) if (row[x] === '1') set(c, ICON_X + x, ICON_Y + y); });
  }
  /** Clears c and draws one screen. since drives scrolling (ScreenSlot.scrollSince). */
  function renderScreen(c, s, since, t = SCROLL) {
    c.fill(0);
    if (s.layout !== 1) { drawLine(c, fonts.main, s.main, 36, since, t); return; }   // unknown layout: main text only
    if (s.topKind === P.TOP.TEXT) drawLine(c, fonts.top, s.top, TOP_BASELINE, since, t);
    else if (s.topKind === P.TOP.DOTS) drawDots(c, s.dotsSelected, s.dotsCount);
    if (s.icon !== P.NO_ICON) {
      drawIcon(c, s.icon);
      if (s.flags & 1) {   // muted: a slash with a dark outline so it reads over filled shapes
        line(c, ICON_X + 1, ICON_Y + 1, ICON_X + 26, ICON_Y + 26, 5, false);
        line(c, ICON_X + 1, ICON_Y + 1, ICON_X + 26, ICON_Y + 26, 2, true);
      }
    }
    drawLine(c, fonts.main, s.main, MAIN_BASELINE, since, t);
  }
  const NOT_CONNECTED = P.screenText('NOT CONNECTED', P.NO_ICON, 'Volco');

  const easeInOut = p => p <= 0 ? 0 : p >= 1 ? 1 : p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2;
  /** left: new content enters from the right (slide left); otherwise from the left. */
  function composeSlide(from, to, progress, left, out) {
    const off = Math.round(easeInOut(progress) * W);
    for (let x = 0; x < W; x++) {
      const sx = left ? x + off : x - off;
      const src = left ? (sx < W ? from : to) : (sx >= 0 ? from : to);
      const col = left ? (sx < W ? sx : sx - W) : (sx >= 0 ? sx : sx + W);
      for (let page = 0; page < 8; page++) out[page * W + x] = src[page * W + col];
    }
  }
  function shiftFrame(input, dx, dy, out) {
    for (let x = 0; x < W; x++) {
      const sx = x - dx;
      let column = 0n;
      if (sx >= 0 && sx < W) for (let page = 0; page < 8; page++) column |= BigInt(input[page * W + sx]) << BigInt(8 * page);
      column = dy >= 0 ? (column << BigInt(dy)) & 0xFFFFFFFFFFFFFFFFn : column >> BigInt(-dy);
      for (let page = 0; page < 8; page++) out[page * W + x] = Number((column >> BigInt(8 * page)) & 0xFFn);
    }
  }

  // ── Slots and panel power (volco_screen_state) ────────────────────────────────────────────
  function createSlot(transitionMs) {
    let current = NOT_CONNECTED, previous = NOT_CONNECTED, transition = 0, start = 0, settled = 0;
    const inTransition = now => transition !== 0 && now - start < transitionMs;
    return {
      /** New content; a running slide snaps to its end first. While the panels are off nothing animates. */
      show(c, t, now, panelsOff) {
        previous = current; current = c;
        transition = panelsOff ? 0 : t;
        start = now;
        settled = transition === 0 ? now : now + transitionMs;
      },
      current: () => current, previous: () => previous, transition: () => transition,
      inTransition,
      progress: now => inTransition(now) ? (now - start) / transitionMs : 1,
      scrollSince: now => now < settled ? 0 : now - settled,
    };
  }
  const POWER = { NORMAL: 0, DIM: 1, OFF: 2 };
  const SHIFT_CYCLE = [[0, 0], [1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1], [1, -1]];
  /** cfg: { dim, off, shift, swallow, dimAfterMs, offAfterMs, shiftEveryMs }. hasScreens false: a
   *  board with no screens never dims, shifts or swallows a press (firmware/src/screens.cpp). */
  function createPanels(cfg, hasScreens = true) {
    if (!hasScreens) cfg = { ...cfg, dim: false, off: false, shift: false, swallow: false };
    let last = 0, swallowed = 0;
    const power = now => {
      const idle = now - last;
      if (cfg.off && idle >= cfg.offAfterMs) return POWER.OFF;
      if (cfg.dim && idle >= cfg.dimAfterMs) return POWER.DIM;
      return POWER.NORMAL;
    };
    return {
      activity(now) { last = now; },
      power,
      shift(now) { return !cfg.shift || !cfg.shiftEveryMs ? [0, 0] : SHIFT_CYCLE[Math.floor(now / cfg.shiftEveryMs) % 9]; },
      /** Call for every button change. False = do not send it to the PC (the wake press). */
      filterButton(button, pressed, now) {
        const bit = button < 32 ? 2 ** button : 0;
        if (pressed) {
          const swallow = cfg.swallow && power(now) === POWER.OFF;
          last = now;
          if (swallow) swallowed |= bit;
          return !swallow;
        }
        last = now;
        if (swallowed & bit) { swallowed &= ~bit; return false; }
        return true;
      },
    };
  }
  /** The frame a slot shows now, as firmware/src/screens.cpp's render() composes it. */
  function frame(slot, panels, now, t = SCROLL) {
    const a = canvas(), b = canvas(), out = canvas();
    if (slot.inTransition(now)) {
      renderScreen(a, slot.previous(), 0, t);
      renderScreen(b, slot.current(), 0, t);
      composeSlide(a, b, slot.progress(now), slot.transition() !== P.TRANSITION.SLIDE_RIGHT, out);
    } else renderScreen(out, slot.current(), slot.scrollSince(now), t);
    const [dx, dy] = panels ? panels.shift(now) : [0, 0];
    if (!dx && !dy) return out;
    shiftFrame(out, dx, dy, a);
    return a;
  }
  /** ScreenBehaviour (protobufjs object or null) → { transitionMs, scroll, panels cfg, brightness, dimBrightness }. */
  function behaviour(sb) {
    const v = (k, d) => (sb && sb[k] !== undefined && sb[k] !== null ? sb[k] : d);
    return {
      transitionMs: v('transitionMs', 360),
      scroll: { holdMs: v('scrollHoldMs', 2000), endHoldMs: v('scrollEndHoldMs', 1200), speedPxS: v('scrollSpeedPxS', 30) },
      panels: { dim: v('dimEnabled', true), off: v('offEnabled', true), shift: v('pixelShiftEnabled', true), swallow: v('wakeSwallowsPress', true),
        dimAfterMs: v('dimAfterS', 120) * 1000, offAfterMs: v('offAfterS', 1800) * 1000, shiftEveryMs: v('pixelShiftEveryS', 180) * 1000 },
      brightness: v('brightness', 255), dimBrightness: v('dimBrightness', 8),
    };
  }

  const api = { W, H, canvas, get, set, renderScreen, scrollOffset, bidiVisual, easeInOut, composeSlide, shiftFrame,
    createSlot, createPanels, frame, behaviour, NOT_CONNECTED, POWER, SCROLL };
  if (node) module.exports = api;
  else root.VolcoOled = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);

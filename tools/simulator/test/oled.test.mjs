// Node tests of tools/simulator/oled.js: pixel-exact against the firmware renderer's golden
// bitmaps (firmware/test/golden/render.txt), and the slot/panel timing of volco_screen_state.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { golden } from './helpers.mjs';

const require = createRequire(import.meta.url);
const P = require('../protocol.js');
const O = require('../oled.js');

const picture = c => {   // an ASCII picture, for readable failures
  const rows = [];
  for (let y = 0; y < 64; y++) { let r = ''; for (let x = 0; x < 128; x++) r += O.get(c, x, y) ? '#' : '.'; rows.push(r); }
  return rows.join('\n');
};

test('every golden render matches the firmware pixel for pixel', () => {
  const cases = golden.renders();
  assert.ok(cases.length >= 16);
  for (const g of cases) {
    assert.ok(g.canvas, `${g.name}: canvas not generated`);
    const s = P.decodeScreen(g.payload);
    const c = O.canvas();
    O.renderScreen(c, s.content, g.since);
    assert.equal(picture(c), picture(Uint8Array.from(g.canvas)), g.name);
  }
});

test('bidi puts Hebrew in visual order and keeps numbers left to right', () => {
  const vis = t => String.fromCodePoint(...O.bidiVisual(t).cps);
  assert.equal(vis('abc'), 'abc');
  assert.equal(vis('אבג'), 'גבא');
  assert.equal(vis('אבג 12'), '12 גבא');
  assert.equal(vis('(א)'), '(א)');
  assert.equal(O.bidiVisual('אב').rtl, true);
  assert.equal(O.bidiVisual('a אב').rtl, false);
});

test('scroll offset holds, scrolls at its speed, then holds at the end', () => {
  const t = O.SCROLL;   // 2000 ms hold, 30 px/s, 1200 ms end hold
  assert.equal(O.scrollOffset(30, 0, t), 0);
  assert.equal(O.scrollOffset(30, 1999, t), 0);
  assert.equal(O.scrollOffset(30, 2500, t), 15);
  assert.equal(O.scrollOffset(30, 3100, t), 30);
  assert.equal(O.scrollOffset(30, 4200, t), 0);   // next cycle
  assert.equal(O.scrollOffset(0, 5000, t), 0);
});

test('a slot slides for its transition time and scrolls only after settling', () => {
  const slot = O.createSlot(360);
  const a = P.screenText('', 1, 'A'), b = P.screenText('', 1, 'B');
  slot.show(a, 0, 1000, false);
  slot.show(b, 1, 2000, false);
  assert.equal(slot.inTransition(2100), true);
  assert.equal(slot.progress(2180), 0.5);
  assert.equal(slot.previous(), a);
  assert.equal(slot.scrollSince(2300), 0);
  assert.equal(slot.inTransition(2360), false);
  assert.equal(slot.scrollSince(2460), 100);
  slot.show(a, 1, 3000, true);   // panels off: no animation
  assert.equal(slot.inTransition(3001), false);
});

test('a slide frame mixes both screens; the end frame is the new screen', () => {
  const slot = O.createSlot(360);
  slot.show(P.screenText('', 1, 'Left'), 0, 0, false);
  slot.show(P.screenText('', 3, 'Right'), 1, 1000, false);
  const end = O.canvas();
  O.renderScreen(end, slot.current(), 0);
  assert.notEqual(picture(O.frame(slot, null, 1180)), picture(end));
  assert.equal(picture(O.frame(slot, null, 1360)), picture(end));
});

test('panels dim, switch off, shift, and swallow the waking press', () => {
  const panels = O.createPanels({ dim: true, off: true, shift: true, swallow: true, dimAfterMs: 1000, offAfterMs: 5000, shiftEveryMs: 100 });
  panels.activity(0);
  assert.equal(panels.power(999), O.POWER.NORMAL);
  assert.equal(panels.power(1000), O.POWER.DIM);
  assert.equal(panels.power(5000), O.POWER.OFF);
  assert.deepEqual(panels.shift(0), [0, 0]);
  assert.deepEqual(panels.shift(150), [1, 0]);
  assert.equal(panels.filterButton(2, true, 6000), false);    // wakes, not sent
  assert.equal(panels.power(6000), O.POWER.NORMAL);
  assert.equal(panels.filterButton(2, false, 6100), false);   // its release isn't sent either
  assert.equal(panels.filterButton(2, true, 6200), true);
});

test('behaviour() reads ScreenBehaviour with the proto defaults', () => {
  const d = O.behaviour(null);
  assert.equal(d.transitionMs, 360);
  assert.deepEqual(d.scroll, O.SCROLL);
  assert.equal(d.panels.dimAfterMs, 120000);
  assert.equal(O.behaviour({ transitionMs: 0, dimEnabled: false }).panels.dim, false);
  assert.equal(O.behaviour({ transitionMs: 0 }).transitionMs, 0);
});

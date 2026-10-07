// Node tests of tools/simulator/protocol.js against the shared golden frames
// (firmware/test/golden/frames.txt, also used by the firmware and C# tests).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { golden } from './helpers.mjs';

const require = createRequire(import.meta.url);
const P = require('../protocol.js');
const frames = golden.frames();
const payloadOf = name => frames[name].slice(2, -1);

test('frame() reproduces every golden frame from its payload', () => {
  for (const [name, bytes] of Object.entries(frames))
    assert.deepEqual([...P.frame(payloadOf(name))], bytes, name);
});

test('the parser finds every golden frame in one stream split into odd chunks', () => {
  const stream = [0x00, 0x13, ...Object.values(frames).flat(), 0xAA];   // garbage before, a dangling start after
  const got = [];
  const parser = P.createParser(p => got.push(p));
  for (let i = 0; i < stream.length; i += 7) parser.feed(stream.slice(i, i + 7));
  assert.deepEqual(got, Object.keys(frames).map(payloadOf));
});

test('a bad checksum is dropped and reported', () => {
  const good = [], bad = [];
  const parser = P.createParser(p => good.push(p), p => bad.push(p));
  const f = [...frames.slider_2_512];
  f[f.length - 1] ^= 0xFF;
  parser.feed([...f, ...frames.alive]);
  assert.deepEqual(good, [[P.MSG.ALIVE]]);
  assert.deepEqual(bad, [payloadOf('slider_2_512')]);
});

test('the controller frames match the golden ones', () => {
  assert.deepEqual(P.hello({ firmware: 'dev', configState: 1, configCrc: 0x12345678, sliders: 5, kinds: [1, 1, 2] }), payloadOf('hello_led_hardware'));
  assert.deepEqual(P.hello({ firmware: 'dev', sliders: 5, kinds: [3, 3, 3, 3] }), payloadOf('hello_screen_controller'));
  assert.deepEqual(P.alive(), payloadOf('alive'));
  assert.deepEqual(P.slider(2, 512), payloadOf('slider_2_512'));
  assert.deepEqual(P.button(1, true), payloadOf('button_1_pressed'));
});

test('Screen payloads decode as the firmware decodes them', () => {
  assert.deepEqual(P.decodeScreen(payloadOf('screen_muted_mic')), { button: 1, transition: 0,
    content: { layout: 1, topKind: 1, top: 'MUTED', dotsSelected: 0, dotsCount: 0, icon: 7, flags: 1, main: 'Microphone' } });
  assert.deepEqual(P.decodeScreen(payloadOf('screen_preset_dots_slide_left')), { button: 0, transition: 1,
    content: { layout: 1, topKind: 2, top: '', dotsSelected: 1, dotsCount: 3, icon: 10, flags: 0, main: 'Gaming' } });
  assert.deepEqual(P.decodeScreen(payloadOf('screen_blank_slide_right')), { button: 2, transition: 2,
    content: { layout: 1, topKind: 0, top: '', dotsSelected: 0, dotsCount: 0, icon: 0xFF, flags: 0, main: '' } });
  assert.equal(P.decodeScreen(payloadOf('screen_hebrew_main')).content.main, 'רמקולים');
  assert.equal(P.decodeScreen(payloadOf('screen_hebrew_main')).content.top, 'NEXT: Speakers');
});

test('screen() encodes what decodeScreen() reads back', () => {
  for (const name of ['screen_muted_mic', 'screen_preset_dots_slide_left', 'screen_blank_slide_right', 'screen_hebrew_main']) {
    const d = P.decodeScreen(payloadOf(name));
    assert.deepEqual(P.screen(d.button, d.transition, d.content), payloadOf(name), name);
  }
});

test('a Screen whose lengths overrun is dropped, an unknown transition is none', () => {
  assert.equal(P.decodeScreen([0x84, 0, 0, 1, 1, 5, 0x41]), null);           // top says 5 bytes, has 1
  assert.equal(P.decodeScreen([0x84, 0, 0, 1, 0, 0xFF]), null);              // icon without flags
  assert.equal(P.decodeScreen([0x84, 0, 0, 1, 1, 61, ...Array(61).fill(0x41), 0xFF, 0, 0]), null);   // over 60 bytes
  assert.equal(P.decodeScreen([0x84, 0, 7, 1, 0, 0xFF, 0, 0]).transition, 0);
});

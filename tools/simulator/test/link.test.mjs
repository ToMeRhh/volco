// Node tests of tools/simulator/link.js: the firmware Link's behaviour, with a fake clock.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { clock } from './helpers.mjs';

const require = createRequire(import.meta.url);
const P = require('../protocol.js');
const { createLink } = require('../link.js');

function harness({ sliders = 2, kinds = [1, 2, 3], config = null } = {}) {
  const h = { sent: [], leds: [], screens: [], lost: 0, clock: clock() };
  h.link = createLink({
    sendFrame: (bytes, payload, bad) => h.sent.push({ bytes: [...bytes], payload, bad }),
    sliderValue: i => 100 * (i + 1),
    onLed: (b, v) => h.leds.push([b, v]),
    onScreen: (b, t, c) => h.screens.push([b, t, c.main]),
    onHostLost: () => h.lost++,
    onConfigFrame: config,
  });
  h.link.setHardware({ sliders, kinds, configState: 1, configCrc: 0xCAFE });
  h.types = () => h.sent.map(s => s.payload[0]);
  h.rx = p => h.link.onPayload(p, h.clock.now());
  h.tick = ms => { h.clock.advance(ms); h.link.tick(h.clock.now()); };
  return h;
}
const screenPayload = (b, t, main) => P.screen(b, t, P.screenText('', 1, main));

test('begin sends Hello and then every slider position', () => {
  const h = harness();
  h.link.begin(h.clock.now());
  assert.deepEqual(h.sent.map(s => s.payload), [
    P.hello({ sliders: 2, kinds: [1, 2, 3], configState: 1, configCrc: 0xCAFE }),
    P.slider(0, 100), P.slider(1, 200)]);
});

test('the first frame from the PC gets a Hello, a HelloRequest exactly one', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  assert.deepEqual(h.types(), [P.MSG.HELLO, P.MSG.SLIDER, P.MSG.SLIDER]);
  h.sent.length = 0;
  h.rx([P.MSG.HELLO_REQUEST]);
  assert.deepEqual(h.types(), [P.MSG.HELLO, P.MSG.SLIDER, P.MSG.SLIDER]);
  const g = harness();
  g.rx([P.MSG.HELLO_REQUEST]);   // first frame is a HelloRequest: one Hello, not two
  assert.equal(g.types().filter(t => t === P.MSG.HELLO).length, 1);
});

test('Alive goes out after 1 s with nothing sent', () => {
  const h = harness();
  h.link.begin(h.clock.now());
  h.sent.length = 0;
  h.tick(999);
  assert.deepEqual(h.types(), []);
  h.tick(1);
  assert.deepEqual(h.types(), [P.MSG.ALIVE]);
  h.link.sendSlider(0, 5, h.clock.now());
  h.sent.length = 0;
  h.tick(999);
  assert.deepEqual(h.types(), []);
});

test('3 s without a frame loses the host; the next frame brings a fresh Hello', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  h.tick(2999);
  assert.equal(h.lost, 0);
  h.tick(1);
  assert.equal(h.lost, 1);
  assert.equal(h.link.hostAlive(), false);
  h.sent.length = 0;
  h.rx([P.MSG.HEARTBEAT]);
  assert.equal(h.types()[0], P.MSG.HELLO);
});

test('Led and Screen reach only buttons of their kind', () => {
  const h = harness();
  h.rx([P.MSG.LED, 0, 1]); h.rx([P.MSG.LED, 1, 2]); h.rx([P.MSG.LED, 2, 1]); h.rx([P.MSG.LED, 9, 1]);
  h.rx(screenPayload(2, 0, 'A')); h.rx(screenPayload(0, 0, 'B'));
  assert.deepEqual(h.leds, [[0, 1], [1, 2]]);
  assert.deepEqual(h.screens, [[2, 0, 'A']]);
});

test('Begin holds updates until Commit, keeping a held slide', () => {
  const h = harness();
  h.rx([P.MSG.BEGIN]);
  h.rx([P.MSG.LED, 0, 1]);
  h.rx(screenPayload(2, 1, 'A'));
  h.rx(screenPayload(2, 0, 'B'));   // an instant update keeps the held slide
  assert.deepEqual(h.leds, []);
  h.rx([P.MSG.COMMIT]);
  assert.deepEqual(h.leds, [[0, 1]]);
  assert.deepEqual(h.screens, [[2, 1, 'B']]);
});

test('an open Begin is applied after 500 ms', () => {
  const h = harness();
  h.rx([P.MSG.BEGIN]);
  h.rx([P.MSG.LED, 0, 1]);
  h.tick(499);
  assert.deepEqual(h.leds, []);
  h.tick(1);
  assert.deepEqual(h.leds, [[0, 1]]);
});

test('the 500 ms fallback applies held updates before the 3 s host loss', () => {
  const h = harness();
  h.rx([P.MSG.BEGIN]);
  h.rx([P.MSG.LED, 0, 1]);
  h.tick(3000);   // the 500 ms fallback runs first and applies them, as on the firmware
  assert.deepEqual(h.leds, [[0, 1]]);
  assert.equal(h.lost, 1);
});

test('config frames go to the config session', () => {
  const taken = [];
  const h = harness({ config: p => { if (p[0] >= 0x87) { taken.push(p[0]); return true; } return false; } });
  h.rx([P.MSG.CONFIG_READ, 4]);
  h.rx([P.MSG.LED, 0, 1]);
  assert.deepEqual(taken, [P.MSG.CONFIG_READ]);
  assert.deepEqual(h.leds, [[0, 1]]);
});

test('fault: silent sends nothing and ignores everything, so the host is lost', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  h.sent.length = 0;
  h.link.faults.silent = true;
  h.rx([P.MSG.LED, 0, 1]);
  h.link.sendButton(0, true, h.clock.now());
  h.tick(3000);
  assert.deepEqual(h.sent, []);
  assert.deepEqual(h.leds, []);
  assert.equal(h.lost, 1);
});

test('fault: the next frame goes out with a corrupted checksum, once', () => {
  const h = harness();
  h.link.corruptNext();
  h.link.sendButton(1, true, h.clock.now());
  h.link.sendButton(1, false, h.clock.now());
  const [a, b] = h.sent;
  assert.equal(a.bad, true);
  assert.equal(a.bytes.at(-1), P.frame(P.button(1, true)).at(-1) ^ 0xFF);
  assert.equal(b.bad, false);
  assert.deepEqual(b.bytes, [...P.frame(P.button(1, false))]);
});

test('fault: ignore Commit leaves the 500 ms fallback', () => {
  const h = harness();
  h.link.faults.ignoreCommit = true;
  h.rx([P.MSG.BEGIN]);
  h.rx([P.MSG.LED, 0, 1]);
  h.rx([P.MSG.COMMIT]);
  assert.deepEqual(h.leds, []);
  h.tick(500);
  assert.deepEqual(h.leds, [[0, 1]]);
});

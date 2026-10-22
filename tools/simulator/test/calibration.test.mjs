// Node tests of tools/simulator/calibration.js and the Calibrate/Calibration frames: the
// firmware's calibration rules (docs/design/slider-calibration.md), with a fake clock.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { clock, golden } from './helpers.mjs';

const require = createRequire(import.meta.url);
const P = require('../protocol.js');
const C = require('../calibration.js');
const { createLink } = require('../link.js');

const frames = golden.frames();
const payloadOf = name => frames[name].slice(2, -1);
const { LEARNED, SWEEPING, AUTO } = P.CAL_FLAG;

test('the Calibration encoder and the Calibrate decoder match the golden frames', () => {
  assert.deepEqual(P.calibration(1, LEARNED | AUTO, 0x00AA, 0x09F6), payloadOf('calibration_1_learned_auto'));
  assert.deepEqual(P.calibration(0, SWEEPING, 0x0FFF, 0), payloadOf('calibration_0_raw_sweeping'));
  assert.deepEqual(P.decodeCalibrate(payloadOf('calibrate_2_start')), { index: 2, action: P.CALIBRATE.START });
  assert.deepEqual(P.decodeCalibrate(payloadOf('calibrate_0_query')), { index: 0, action: P.CALIBRATE.QUERY });
  assert.equal(P.decodeCalibrate([P.MSG.CALIBRATE, 0]), null);
  assert.deepEqual(P.decodeCalibrate([P.MSG.CALIBRATE, 0, 9]), { index: 0, action: P.CALIBRATE.QUERY });  // unknown: a query
  assert.equal(P.decodeCalibrate([P.MSG.CALIBRATE, 0, 0, 0]), null);   // exactly 3 bytes, as the firmware
  assert.equal(P.nameOf(P.MSG.CALIBRATE), 'Calibrate');
  assert.equal(P.nameOf(P.MSG.CALIBRATION), 'Calibration');
});

// A link with two sliders whose calibration answers Calibrate; `pos` is each fader's raw position.
function harness({ autos = [true, false] } = {}) {
  const h = { sent: [], clock: clock(), pos: [0, 0] };
  const sendFrame = (bytes, payload) => h.sent.push(payload);
  const cal = C.createCalibrations({
    send: (p, t) => h.link.sendPayload(p, t),
    sendSlider: (i, v, t) => h.link.sendSlider(i, v, t),
    raw: i => h.pos[i],
  });
  h.cal = cal;
  h.link = createLink({
    sendFrame, sliderValue: i => cal.map(i, h.pos[i]), onLed() {}, onScreen() {},
    onHostLost: () => cal.hostLost(), onCalibrate: (i, a, t) => cal.calibrate(i, a, t),
  });
  h.link.setHardware({ sliders: 2, kinds: [] });
  cal.configure(autos.map((auto, i) => ({ pin: 33 + i, auto })));
  h.rx = p => h.link.onPayload(p, h.clock.now());
  h.advance = ms => { h.clock.advance(ms); cal.tick(h.clock.now()); };
  /** The fader moves to v: the reading learns, then the position is sent as the board does. */
  h.move = (i, v) => { h.pos[i] = v; cal.reading(i, v); h.link.sendSlider(i, cal.map(i, v), h.clock.now()); };
  h.take = () => h.sent.splice(0);
  return h;
}

test('a query answers with the current state: raw, auto flag, empty ends', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  h.take();
  h.rx([P.MSG.CALIBRATE, 0, P.CALIBRATE.QUERY]);
  assert.deepEqual(h.take(), [P.calibration(0, AUTO, 0x0FFF, 0)]);
  h.rx([P.MSG.CALIBRATE, 1, P.CALIBRATE.QUERY]);
  assert.deepEqual(h.take(), [P.calibration(1, 0, 0x0FFF, 0)]);
  h.rx([P.MSG.CALIBRATE, 5, P.CALIBRATE.QUERY]);   // no such slider: no answer
  assert.deepEqual(h.take(), []);
});

test('start, drag 100..3000, learned report, done: an ending report then a Slider frame', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  h.take();
  h.rx([P.MSG.CALIBRATE, 1, P.CALIBRATE.START]);
  assert.deepEqual(h.take(), [P.calibration(1, SWEEPING, 0x0FFF, 0)]);

  h.move(1, 100); h.advance(100);
  h.take();
  for (let v = 200; v <= 3000; v += 200) { h.advance(100); h.move(1, v); }
  h.advance(100);
  const reports = h.take().filter(p => p[0] === P.MSG.CALIBRATION);
  assert.ok(reports.length > 0);
  assert.deepEqual(reports.at(-1), P.calibration(1, LEARNED | SWEEPING, 100, 3000));
  assert.ok(reports.every(r => r[1] === 1));

  h.pos[1] = 1550;
  h.rx([P.MSG.CALIBRATE, 1, P.CALIBRATE.DONE]);
  const out = h.take();
  assert.deepEqual(out, [P.calibration(1, LEARNED, 100, 3000), P.slider(1, h.cal.map(1, 1550))]);
  assert.equal(out[1][2] << 8 | out[1][3], 2048);   // the middle of the learned span
  h.advance(1000);
  assert.deepEqual(h.take(), []);   // manual: nothing more learned afterwards
  h.move(1, 4000);
  assert.deepEqual(h.cal.slider(1).hi(), 3000);
});

test('widening reports go out at most every 100 ms', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  h.take();
  h.cal.reading(0, 500); h.cal.tick(h.clock.now());   // auto slider: learns
  assert.equal(h.take().length, 1);
  h.cal.reading(0, 400); h.advance(50);
  assert.deepEqual(h.take(), []);
  h.advance(50);
  assert.deepEqual(h.take(), [P.calibration(0, AUTO, 400, 500)]);
});

test('Done before a span is learned is ignored; Cancel restores the saved ends', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  h.cal.reading(0, 200); h.cal.reading(0, 3200);   // auto learns 200..3200
  h.take();
  h.rx([P.MSG.CALIBRATE, 0, P.CALIBRATE.START]);
  h.take();
  h.cal.reading(0, 1000);
  h.rx([P.MSG.CALIBRATE, 0, P.CALIBRATE.DONE]);   // not learned yet: still sweeping
  assert.deepEqual(h.take(), [P.calibration(0, SWEEPING | AUTO, 1000, 1000)]);
  h.pos[0] = 700;
  h.rx([P.MSG.CALIBRATE, 0, P.CALIBRATE.CANCEL]);
  assert.deepEqual(h.take(), [P.calibration(0, LEARNED | AUTO, 200, 3200), P.slider(0, h.cal.map(0, 700))]);
});

test('a sweep with no Done ends as a Cancel after 60 s', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  h.rx([P.MSG.CALIBRATE, 1, P.CALIBRATE.START]);
  h.take();
  h.move(1, 0); h.move(1, 2000); h.take();
  h.advance(59000);
  assert.ok(h.cal.slider(1).sweeping());
  h.take();
  h.advance(1000);
  assert.ok(!h.cal.slider(1).sweeping());
  const out = h.take();
  assert.deepEqual(out.map(p => p[0]), [P.MSG.CALIBRATION, P.MSG.SLIDER]);
  assert.deepEqual(out[0], P.calibration(1, 0, 0x0FFF, 0));   // back to the ends it started from (none)
});

test('losing the host cancels a sweep', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  h.rx([P.MSG.CALIBRATE, 1, P.CALIBRATE.START]);
  h.take();
  h.link.tick(h.clock.now() + 4000);
  assert.ok(!h.cal.slider(1).sweeping());
});

test('mapping: raw until learned, then stretched with a 1.5 % band at each stop', () => {
  const s = C.createCalibrations({ send() {}, sendSlider() {}, raw: () => 0 });
  s.configure([{ pin: 33, auto: true }]);
  assert.equal(s.map(0, 777), 777);
  s.reading(0, 170); s.reading(0, 2550);
  assert.equal(s.map(0, 170), 0);
  assert.equal(s.map(0, 2550), 4095);
  assert.equal(s.map(0, 170 + Math.round(0.014 * 2380)), 0);
  assert.equal(s.map(0, 1360), 2048);
});

test('an unknown Calibrate action is answered as a query', () => {
  const h = harness();
  h.rx([P.MSG.HEARTBEAT]);
  h.take();
  h.rx([P.MSG.CALIBRATE, 0, 7]);
  assert.deepEqual(h.take(), [P.calibration(0, AUTO, 0x0FFF, 0)]);
  assert.ok(!h.cal.slider(0).sweeping());
});

test('a reboot keeps the ends by pin and Auto-calibrate setting, and drops any sweep', () => {
  const s = C.createCalibrations({ send() {}, sendSlider() {}, raw: () => 0 });
  s.configure([{ pin: 33, auto: true }, { pin: 35, auto: true }, { pin: 32, auto: true }]);
  [[100, 2000], [200, 2100], [300, 2200]].forEach(([lo, hi], i) => { s.reading(i, lo); s.reading(i, hi); });
  s.slider(2).startSweep(0);
  // The middle slider is removed: the others keep their ends, matched by pin.
  s.configure([{ pin: 33, auto: true }, { pin: 32, auto: true }]);
  assert.equal(s.count(), 2);
  assert.deepEqual([s.slider(0).lo(), s.slider(0).hi()], [100, 2000]);
  assert.ok(!s.slider(1).sweeping());
  assert.deepEqual([s.slider(1).lo(), s.slider(1).hi()], [300, 2200]);   // the stored ends, not the sweep's
  // Auto-calibrate flipped: that slider's ends are forgotten.
  s.configure([{ pin: 33, auto: false }, { pin: 32, auto: true }]);
  assert.ok(!s.slider(0).learned());
  assert.equal(s.map(0, 1234), 1234);
});

test('Auto-calibrate learning the span is flagged once, never in a sweep', () => {
  const s = C.createCalibrations({ send() {}, sendSlider() {}, raw: () => 0 });
  s.configure([{ pin: 33, auto: true }]);
  s.reading(0, 170);
  assert.equal(s.becameLearned(0), false);
  s.reading(0, 2550);
  assert.equal(s.becameLearned(0), true);
  assert.equal(s.becameLearned(0), false);
  s.slider(0).startSweep(0);
  s.reading(0, 170); s.reading(0, 2550);
  assert.equal(s.becameLearned(0), false);
});

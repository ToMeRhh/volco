// Node tests of tools/simulator/pinhelpers.js and the PinWatch/PinActivity/I2cScan/PinDrive frames:
// the firmware's pin helpers (issue #120), with a fake clock.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { golden } from './helpers.mjs';

const require = createRequire(import.meta.url);
const P = require('../protocol.js');
const H = require('../pinhelpers.js');
const { createLink } = require('../link.js');

const frames = golden.frames();
const payloadOf = name => frames[name].slice(2, -1);

test('the helper encoders and decoders match the golden frames', () => {
  assert.deepEqual(Array.from(P.frame(P.pinActivity(33))), frames.pin_activity_33);
  assert.deepEqual(P.pinActivity(33), payloadOf('pin_activity_33'));
  assert.deepEqual(P.i2cScanResult(25, 26, [0x3C, 0x3D]), payloadOf('i2c_scan_result_25_26_two'));
  assert.deepEqual(P.i2cScanResult(25, 26, []), payloadOf('i2c_scan_result_25_26_none'));
  assert.deepEqual(P.decodePinWatch(payloadOf('pin_watch_analog')), { role: P.WATCH.ANALOG });
  assert.deepEqual(P.decodePinWatch(payloadOf('pin_watch_button')), { role: P.WATCH.BUTTON });
  assert.deepEqual(P.decodePinWatch(payloadOf('pin_watch_stop')), { role: P.WATCH.STOP });
  assert.deepEqual(P.decodeI2cScan(payloadOf('i2c_scan_25_26')), { sda: 25, scl: 26 });
  assert.deepEqual(P.decodePinDrive(payloadOf('pin_drive_12_high_300')), { pin: 12, level: 1, ms: 300 });
  assert.deepEqual(P.WATCH, { STOP: 0, ANALOG: 1, BUTTON: 2 });
  assert.equal(P.nameOf(P.MSG.PIN_WATCH), 'PinWatch');
  assert.equal(P.nameOf(P.MSG.PIN_ACTIVITY), 'PinActivity');
  assert.equal(P.nameOf(P.MSG.I2C_SCAN), 'I2cScan');
  assert.equal(P.nameOf(P.MSG.I2C_SCAN_RESULT), 'I2cScanResult');
  assert.equal(P.nameOf(P.MSG.PIN_DRIVE), 'PinDrive');
});

test('the decoders accept only the exact length', () => {
  assert.equal(P.decodePinWatch([P.MSG.PIN_WATCH]), null);
  assert.equal(P.decodePinWatch([P.MSG.PIN_WATCH, 1, 0]), null);
  assert.equal(P.decodeI2cScan([P.MSG.I2C_SCAN, 25]), null);
  assert.equal(P.decodeI2cScan([P.MSG.I2C_SCAN, 25, 26, 0]), null);
  assert.equal(P.decodePinDrive([P.MSG.PIN_DRIVE, 12, 1, 1]), null);
  assert.equal(P.decodePinDrive([P.MSG.PIN_DRIVE, 12, 1, 1, 0x2C, 0]), null);
});

test('the Link hands the three frames to its callbacks', () => {
  const got = [];
  const link = createLink({
    sendFrame() {}, sliderValue: () => 0, onLed() {}, onScreen() {}, onHostLost() {},
    onPinWatch: (r, t) => got.push(['watch', r, t]),
    onI2cScan: (a, b, t) => got.push(['scan', a, b, t]),
    onPinDrive: (p, l, ms, t) => got.push(['drive', p, l, ms, t]),
  });
  link.setHardware({ sliders: 0, kinds: [] });
  link.onPayload(payloadOf('pin_watch_analog'), 10);
  link.onPayload(payloadOf('i2c_scan_25_26'), 11);
  link.onPayload(payloadOf('pin_drive_12_high_300'), 12);
  link.onPayload([P.MSG.PIN_WATCH], 13);   // malformed: ignored
  assert.deepEqual(got, [['watch', 1, 10], ['scan', 25, 26, 11], ['drive', 12, 1, 300, 12]]);
});

// Two sliders (pins 33, 32); button 0: a button on 4 with an active-low LED on 12; 1: a pair on 18/19
// (active-high); 2: a screen on bus 0 (0x3C) and button 3 on bus 0 (0x3D); button 4 a screen on bus 1.
const config = () => ({
  sliders: [{ pin: 33 }, { pin: 32 }],
  buttons: [
    { pin: 4, led: { pin: 12, activeLow: true } },
    { pin: 5, ledPair: { pinA: 18, pinB: 19, activeLow: false } },
    { pin: 14, screen: { bus: 0, address: 0x3C } },
    { pin: 15, screen: { bus: 0, address: 0x3D } },
    { pin: 16, screen: { bus: 1, address: 0x3D } },
    { pin: 17, screen: { bus: 0, address: 0x3C } },   // a duplicate: the list holds it once
  ],
  i2c0: { sda: 25, scl: 26 },
  i2c1: { sda: 27, scl: 13 },
});

function harness(cfg = config()) {
  const h = { sent: [], leds: [], last: [] };
  h.p = H.createPinHelpers({
    send: p => h.sent.push(p), config: cfg,
    showLed: (b, v) => h.leds.push([b, v]),
    lastLed: b => h.last[b] ?? 0,
  });
  h.take = () => h.sent.splice(0);
  return h;
}

test('an analog watch reports a slider pin and ignores buttons', () => {
  const h = harness();
  h.p.watch(P.WATCH.ANALOG, 0);
  h.p.slider(1, 0);
  h.p.button(0, 0);
  assert.deepEqual(h.take(), [P.pinActivity(32)]);
});

test('a button watch reports a button pin and ignores sliders', () => {
  const h = harness();
  h.p.watch(P.WATCH.BUTTON, 0);
  h.p.slider(0, 0);
  h.p.button(1, 0);
  assert.deepEqual(h.take(), [P.pinActivity(5)]);
});

test('nothing is reported without a watch, or for a control the config lacks', () => {
  const h = harness();
  h.p.slider(0, 0);
  assert.deepEqual(h.take(), []);
  h.p.watch(P.WATCH.ANALOG, 0);
  h.p.slider(7, 0);
  assert.deepEqual(h.take(), []);
});

test('a pin is reported at most every 250 ms, each pin on its own', () => {
  const h = harness();
  h.p.watch(P.WATCH.ANALOG, 0);
  h.p.slider(0, 0);
  h.p.slider(0, 100);
  h.p.slider(1, 100);
  h.p.slider(0, 249);
  assert.deepEqual(h.take(), [P.pinActivity(33), P.pinActivity(32)]);
  h.p.slider(0, 250);
  assert.deepEqual(h.take(), [P.pinActivity(33)]);
});

test('a watch ends 30 s after the last PinWatch, and a renewal extends it', () => {
  const h = harness();
  h.p.watch(P.WATCH.ANALOG, 0);
  h.p.watch(P.WATCH.ANALOG, 10000);
  h.p.slider(0, 39999);
  assert.equal(h.take().length, 1);
  h.p.slider(0, 40000);
  assert.deepEqual(h.take(), []);
  h.p.watch(P.WATCH.ANALOG, 50000);
  h.p.slider(0, 60000);
  assert.equal(h.take().length, 1);
});

test('role 0 and a lost host stop the watch', () => {
  const h = harness();
  h.p.watch(P.WATCH.ANALOG, 0);
  h.p.watch(P.WATCH.STOP, 100);
  h.p.slider(0, 1000);
  assert.deepEqual(h.take(), []);
  h.p.watch(P.WATCH.BUTTON, 2000);
  h.p.hostLost();
  h.p.button(0, 3000);
  assert.deepEqual(h.take(), []);
});

test('a new watch starts with no rate limit from the last one', () => {
  const h = harness();
  h.p.watch(P.WATCH.ANALOG, 0);
  h.p.slider(0, 10);
  h.p.watch(P.WATCH.STOP, 20);
  h.p.watch(P.WATCH.ANALOG, 30);
  h.p.slider(0, 40);
  assert.equal(h.take().length, 2);
});

test('a scan of a configured bus returns its screens, sorted and without duplicates', () => {
  const h = harness();
  h.p.scan(25, 26);
  h.p.scan(27, 13);
  assert.deepEqual(h.take(), [P.i2cScanResult(25, 26, [0x3C, 0x3D]), P.i2cScanResult(27, 13, [0x3D])]);
  assert.deepEqual(H.screensOn(config(), 25, 26), [0x3C, 0x3D]);
});

test('a scan of unknown or swapped pins finds nothing', () => {
  const h = harness();
  h.p.scan(21, 22);
  h.p.scan(26, 25);
  h.p.scan(25, 13);
  assert.deepEqual(h.take(), [P.i2cScanResult(21, 22, []), P.i2cScanResult(26, 25, []), P.i2cScanResult(25, 13, [])]);
});

test('a bus with no screen on it finds nothing', () => {
  const cfg = config();
  cfg.i2c2 = { sda: 21, scl: 22 };
  const h = harness(cfg);
  h.p.scan(21, 22);
  assert.deepEqual(h.take(), [P.i2cScanResult(21, 22, [])]);
});

test('a drive lights an active-low LED at level 0 and restores the last value after ms', () => {
  const h = harness();
  h.last[0] = 0;
  h.p.drive(12, 0, 300, 1000);
  assert.deepEqual(h.leds, [[0, 1]]);
  assert.ok(h.p.driven(0));
  h.p.tick(1299);
  assert.deepEqual(h.leds, [[0, 1]]);
  h.last[0] = 1;   // the PC changed it meanwhile
  h.p.tick(1300);
  assert.deepEqual(h.leds, [[0, 1], [0, 1]]);
  assert.ok(!h.p.driven(0));
});

test('level 1 on an active-low LED drives it dark, and level 1 on an active-high one lights it', () => {
  const h = harness();
  h.last[0] = 1;
  h.p.drive(12, 1, 300, 0);
  assert.deepEqual(h.leds, [[0, 0]]);
  h.p.tick(300);
  assert.deepEqual(h.leds.at(-1), [0, 1]);
  const g = harness();
  g.p.drive(18, 1, 300, 0);
  assert.deepEqual(g.leds, [[1, 1]]);
});

test('a pair drive changes only its side', () => {
  const h = harness();
  h.last[1] = 2;   // B lit
  h.p.drive(18, 1, 300, 0);   // A on: both lit shows the driven side
  assert.deepEqual(h.leds.at(-1), [1, 1]);
  h.p.tick(300);
  assert.deepEqual(h.leds.at(-1), [1, 2]);
  h.p.drive(19, 0, 300, 400);   // B off while B was lit
  assert.deepEqual(h.leds.at(-1), [1, 0]);
  h.p.drive(19, 1, 300, 800);   // B on
  assert.deepEqual(h.leds.at(-1), [1, 2]);
});

test('a drive on a pin that is no LED, or an unknown frame level, is ignored', () => {
  const h = harness();
  h.p.drive(4, 1, 300, 0);   // a button pin
  h.p.drive(25, 1, 300, 0);  // a bus pin
  h.p.drive(2, 1, 300, 0);   // free
  h.p.tick(1000);
  assert.deepEqual(h.leds, []);
  assert.ok(!h.p.driven(0));
});

test('a later drive of the same LED extends it, and a lost host restores at once', () => {
  const h = harness();
  h.p.drive(12, 0, 300, 0);
  h.p.drive(12, 0, 300, 200);
  h.p.tick(300);
  assert.ok(h.p.driven(0));
  h.p.tick(500);
  assert.ok(!h.p.driven(0));
  h.p.drive(12, 0, 300, 600);
  h.p.hostLost();
  assert.ok(!h.p.driven(0));
  assert.deepEqual(h.leds.at(-1), [0, 0]);
});

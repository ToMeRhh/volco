// Node tests of tools/simulator/layout.js: every config it produces passes the app's validator
// rules (re-stated below from Volco.Core.Controller.ControllerConfigValidator and ChipInfo.Esp32).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';
import fs from 'node:fs';
import { repo } from './helpers.mjs';

const require = createRequire(import.meta.url);
const protobuf = require('protobufjs/light');
const descriptor = require('../volco_config_descriptor.js');
const L = require('../layout.js');
const Config = protobuf.Root.fromJSON(descriptor).lookupType('volco.config.ControllerConfig');

/** ControllerConfigValidator steps 1-5: null when valid, else a reason. */
function validate(c) {
  const udp = !!(c.udp && c.udp.enabled);
  if (c.sliders.length > 16 || c.buttons.length > 32) return 'counts';
  for (const [i, s] of c.sliders.entries()) {
    if (!L.adcCapable(s.pin)) return `slider ${i} pin`;
    if (udp && L.CHIP.adc2.has(s.pin)) return `slider ${i} adc2`;
  }
  const seenSlots = new Set();
  for (const [i, b] of c.buttons.entries()) {
    if (!L.usable(b.pin)) return `button ${i} pin`;
    if (b.led && !L.outputCapable(b.led.pin)) return `button ${i} led`;
    if (b.ledPair && (!L.outputCapable(b.ledPair.pinA) || !L.outputCapable(b.ledPair.pinB) || b.ledPair.pinA === b.ledPair.pinB)) return `button ${i} pair`;
    if (b.screen) {
      const bus = b.screen.bus ?? 0, a = b.screen.address ?? 0x3C;
      if (bus > 3 || (a !== 0x3C && a !== 0x3D) || seenSlots.has(`${bus}:${a}`)) return `button ${i} screen`;
      seenSlots.add(`${bus}:${a}`);
    }
  }
  const used = [0, 1, 2, 3].filter(n => c.buttons.some(b => b.screen && (b.screen.bus ?? 0) === n));
  for (const n of used) {
    const d = c[`i2c${n}`];
    if (!d || !L.outputCapable(d.sda) || !L.outputCapable(d.scl) || d.sda === d.scl) return `i2c${n}`;
  }
  const seen = new Set(), add = p => (seen.has(p) ? false : (seen.add(p), true));
  for (const s of c.sliders) if (!add(s.pin)) return `duplicate ${s.pin}`;
  for (const b of c.buttons) {
    if (!add(b.pin)) return `duplicate ${b.pin}`;
    if (b.led && !add(b.led.pin)) return `duplicate ${b.led.pin}`;
    if (b.ledPair && (!add(b.ledPair.pinA) || !add(b.ledPair.pinB))) return 'duplicate pair';
  }
  for (const n of used) if (!add(c[`i2c${n}`].sda) || !add(c[`i2c${n}`].scl)) return `duplicate i2c${n}`;
  return null;
}
const ok = layout => {
  const r = L.toConfig(layout);
  assert.equal(r.error, undefined, JSON.stringify(r.error));
  assert.equal(validate(r.config), null);
  return r.config;
};
const withButtons = (layout, kinds) => ({ ...layout, buttons: kinds.map(k => L.withKind({}, k)) });

test('the DIY reference preset is proto/presets/diy-reference.json', () => {
  const file = JSON.parse(fs.readFileSync(path.join(repo, 'proto', 'presets', 'diy-reference.json'), 'utf8'));
  assert.deepEqual(L.DIY_REFERENCE, file);
  assert.deepEqual(ok(L.presetLayout('diy-reference')), file);   // its pins are kept as they are
});

test('the simulator presets allocate the same pins every time', () => {
  assert.deepEqual(ok(L.presetLayout('screens')), {
    preset: 'screens',
    sliders: [{ pin: 34 }, { pin: 35 }, { pin: 36 }, { pin: 39 }, { pin: 32 }],
    buttons: [
      { pin: 16, screen: { bus: 0, address: 0x3C } }, { pin: 17, screen: { bus: 0, address: 0x3D } },
      { pin: 18, screen: { bus: 1, address: 0x3C } }, { pin: 19, screen: { bus: 1, address: 0x3D } }],
    i2c0: { sda: 21, scl: 22 }, i2c1: { sda: 23, scl: 5 },
  });
  assert.deepEqual(ok(L.presetLayout('mixed')), {
    preset: 'mixed',
    sliders: [{ pin: 34 }, { pin: 35 }, { pin: 36 }, { pin: 39 }, { pin: 32 }],
    buttons: [
      { pin: 16, screen: { bus: 0, address: 0x3C } }, { pin: 17, screen: { bus: 0, address: 0x3D } },
      { pin: 18, led: { pin: 21 } }, { pin: 19, ledPair: { pinA: 22, pinB: 23 } }],
    i2c0: { sda: 5, scl: 25 },
  });
});

test('existing pins are kept and only new items get pins', () => {
  const diy = L.presetLayout('diy-reference');
  const c = ok({ ...diy, sliders: [...diy.sliders, {}], buttons: [...diy.buttons, L.withKind({}, L.KIND.SCREEN)] });
  assert.deepEqual(c.sliders.map(s => s.pin), [33, 35, 32, 36, 34, 39]);   // 39: the next free pin in slider order
  assert.deepEqual(c.buttons.slice(0, 3), L.DIY_REFERENCE.buttons);
  assert.deepEqual(c.buttons[3], { pin: 16, screen: { bus: 0, address: 0x3C } });
  assert.deepEqual(c.i2c0, { sda: 25, scl: 26 });   // the preset's bus 0 is free, so it is used
});

test('changing a kind keeps the button pin and re-allocates the indicator', () => {
  const diy = L.presetLayout('diy-reference');
  const buttons = diy.buttons.map((b, i) => (i === 0 ? L.withKind(b, L.KIND.LED_PAIR) : b));
  const c = ok({ ...diy, buttons });
  assert.equal(c.buttons[0].pin, 4);
  assert.equal(c.buttons[0].led, undefined);
  assert.ok(c.buttons[0].ledPair.pinA !== undefined && c.buttons[0].ledPair.pinB !== undefined);
});

test('reordering keeps every item with its pins', () => {
  const diy = L.presetLayout('diy-reference');
  const c = ok({ ...diy, buttons: [diy.buttons[2], diy.buttons[0], diy.buttons[1]] });
  assert.deepEqual(c.buttons, [L.DIY_REFERENCE.buttons[2], L.DIY_REFERENCE.buttons[0], L.DIY_REFERENCE.buttons[1]]);
});

test('non-hardware fields survive and the blob round-trips through protobufjs', () => {
  const base = { udp: { enabled: false, pcPort: 9000 }, screens: { transitionMs: 200, dimEnabled: false }, input: { debounceMs: 12 } };
  const c = ok(L.presetLayout('mixed', base));
  assert.deepEqual(c.screens, base.screens);
  const bytes = Config.encode(Config.fromObject(c)).finish();
  const back = L.fromConfig(Config.toObject(Config.decode(bytes)));
  assert.deepEqual(ok(back), c);
});

test('screens fill buses 0-3 at 0x3C then 0x3D, and stop at 8', () => {
  const blank = { preset: 'custom', sliders: [{}], buttons: [], base: {} };
  const c = ok(withButtons(blank, Array(8).fill(L.KIND.SCREEN)));
  assert.deepEqual(c.buttons.map(b => [b.screen.bus, b.screen.address]),
    [[0, 0x3C], [0, 0x3D], [1, 0x3C], [1, 0x3D], [2, 0x3C], [2, 0x3D], [3, 0x3C], [3, 0x3D]]);
  assert.equal(L.toConfig(withButtons(blank, Array(9).fill(L.KIND.SCREEN))).error.message, '8 screens maximum');
});

test('limits are where the pins run out', () => {
  const blank = n => ({ preset: 'custom', sliders: Array.from({ length: n }, () => ({})), buttons: [], base: {} });
  ok(blank(16));
  assert.equal(L.toConfig(blank(17)).error.message, '16 sliders maximum');
  const udp = { ...blank(9), base: { udp: { enabled: true } } };
  assert.equal(L.toConfig(udp).error.message, 'No free ADC pin');   // without ADC2, 8 ADC1 pins
  ok({ ...udp, sliders: udp.sliders.slice(0, 8) });
  // 6 sliders take 34, 35, 36, 39, 32, 33, leaving 18 pins that can drive an LED: 9 LED buttons.
  const leds = n => withButtons(blank(6), Array(n).fill(L.KIND.LED));
  ok(leds(9));
  assert.equal(L.toConfig(leds(10)).error.message, 'No free output pin');
  const plus = kind => l => ({ ...l, buttons: [...l.buttons, L.withKind({}, kind)] });
  // In the builder every existing item already has its pins (the stored config), so a new item
  // fails on what it needs itself.
  const saved = n => L.fromConfig(ok(leds(n)));
  assert.equal(L.check(saved(9), plus(L.KIND.NONE)), 'No free input pin');
  assert.equal(L.check(saved(8), plus(L.KIND.NONE)), null);
  assert.equal(L.check(saved(8), plus(L.KIND.LED)), null);
  assert.equal(L.check(saved(8), plus(L.KIND.LED_PAIR)), 'No free output pin');
});

test('toConfig counts the pins a layout uses, and explain says what ran out', () => {
  const blank = n => ({ preset: 'custom', sliders: Array.from({ length: n }, () => ({})), buttons: [], base: {} });
  // 6 sliders: 34, 35, 36, 39 (input-only) and 32, 33 (I/O); 9 LED buttons take the other 18 I/O pins.
  const full = withButtons(blank(6), Array(9).fill(L.KIND.LED));
  assert.deepEqual(L.toConfig(full).pins, { io: 20, ioTotal: 20, inputOnly: 4, inputOnlyTotal: 6 });
  assert.deepEqual(L.toConfig(L.fromConfig(L.DIY_REFERENCE)).pins, { io: 9, ioTotal: 20, inputOnly: 3, inputOnlyTotal: 6 });
  assert.equal(L.toConfig({ ...full, buttons: [...full.buttons, {}] }).pins, undefined);   // doesn't fit: no count
  assert.match(L.explain('No free output pin'), /All 20 I\/O pins are in use/);
  assert.match(L.explain('No free input pin'), /All 20 I\/O pins are in use/);
  assert.match(L.explain('No free ADC pin'), /analog pin/);
  assert.equal(L.explain('32 buttons maximum'), 'The firmware supports 32 buttons maximum.');
});

test('buttons never get the input-only pins 34-39', () => {
  const c = ok(withButtons({ preset: 'custom', sliders: [], buttons: [], base: {} }, Array(20).fill(L.KIND.NONE)));
  assert.ok(c.buttons.every(b => b.pin < 34));
});

test('a screen on a bus with no I2C definition gets bus pins', () => {
  // As Volco could write it: bus 2 is used but i2c2 is missing.
  const config = { sliders: [{ pin: 34 }], buttons: [{ pin: 16, screen: { bus: 2, address: 0x3D } }] };
  const c = ok(L.fromConfig(config));
  assert.deepEqual(c.buttons[0], { pin: 16, screen: { bus: 2, address: 0x3D } });
  assert.deepEqual(c.i2c2, { sda: 18, scl: 19 });
});

test('a board with no sliders or no buttons is valid', () => {
  assert.deepEqual(ok({ preset: 'custom', sliders: [], buttons: [], base: {} }), { preset: 'custom', sliders: [], buttons: [] });
  ok(withButtons({ preset: 'custom', sliders: [], buttons: [], base: {} }, [L.KIND.LED_PAIR]));
  ok({ preset: 'custom', sliders: [{}, {}], buttons: [], base: {} });
});

test('a defined bus that only a new screen uses keeps its pins', () => {
  // DIY reference + 4 LED buttons, then button 1 becomes a screen: bus 0's defined pins (25, 26)
  // must not go to the new LEDs, and the bus must not move onto strapping pins.
  const diy = L.presetLayout('diy-reference');
  const buttons = [...diy.buttons, ...Array.from({ length: 4 }, () => L.withKind({}, L.KIND.LED))];
  buttons[0] = L.withKind(buttons[0], L.KIND.SCREEN);
  const c = ok({ ...diy, buttons });
  assert.deepEqual(c.buttons[0], { pin: 4, screen: { bus: 0, address: 0x3C } });
  assert.deepEqual(c.i2c0, { sda: 25, scl: 26 });
  const leds = c.buttons.filter(b => b.led).map(b => b.led.pin);
  assert.ok(!leds.includes(25) && !leds.includes(26), `LEDs took bus pins: ${leds}`);
});

test('the pin map lists every pin a config uses, worded as the Controller page asks', () => {
  assert.deepEqual(L.pinMap(L.DIY_REFERENCE), [
    { label: 'Slider 1', value: '33' }, { label: 'Slider 2', value: '35' }, { label: 'Slider 3', value: '32' },
    { label: 'Slider 4', value: '36' }, { label: 'Slider 5', value: '34' },
    { label: 'Button 1', value: '4 · LED 12, lights when low' },
    { label: 'Button 2', value: '14 · LED 21, lights when low' },
    { label: 'Button 3', value: '5 · LED A 18, B 19, lights when high' },
  ]);   // its buses are defined, but no screen uses them
  assert.deepEqual(L.pinMap(ok(L.presetLayout('mixed'))).slice(5), [
    { label: 'Button 1', value: '16 · screen on bus 0, 0x3C' },
    { label: 'Button 2', value: '17 · screen on bus 0, 0x3D' },
    { label: 'Button 3', value: '18 · LED 21, lights when high' },
    { label: 'Button 4', value: '19 · LED A 22, B 23, lights when high' },
    { label: 'I2C bus 0', value: 'SDA 5 · SCL 25' },
  ]);
  assert.deepEqual(L.pinMap(null), []);
});

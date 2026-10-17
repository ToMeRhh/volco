// The builder's model (spec §6.2, §6.3): presets, the ESP32 chip table, and turning a layout into
// a ControllerConfig with every pin allocated so the app's validator accepts it. Configs are plain
// objects in protobufjs's shape (camelCase, as proto/presets/*.json). No DOM: index.html loads it
// as a plain script (globalThis.VolcoLayout), the Node tests with require().
(function (root) {
  'use strict';

  const KIND = { NONE: 0, LED: 1, LED_PAIR: 2, SCREEN: 3 };
  const LIMITS = { sliders: 16, buttons: 32, screens: 8 };

  /** The chip table from chips.js (generated from proto/chips.json): usable = exists minus reserved. */
  const CHIPS = typeof module === 'object' && module.exports ? require('./chips.js') : root.VolcoChips;
  const ESP32 = CHIPS.esp32;
  const CHIP = {
    usable: new Set(ESP32.exists.filter(p => !ESP32.reserved.includes(p))),
    inputOnly: new Set(ESP32.inputOnly),
    adc1: new Set(ESP32.adc1),
    adc2: new Set(ESP32.adc2),
  };
  const usable = p => CHIP.usable.has(p);
  const outputCapable = p => usable(p) && !CHIP.inputOnly.has(p);
  const adcCapable = p => usable(p) && (CHIP.adc1.has(p) || CHIP.adc2.has(p));

  /** Allocation order per role. Sliders take the input-only pins first, leaving output pins for
   *  LEDs and buses; 37 and 38 (not on most modules) and ADC2 come after. Strapping pins (0, 2,
   *  12, 15) come last. Buttons never get 34-39: input-only, no internal pull-up, and the
   *  firmware uses INPUT_PULLUP. */
  const PREFER = {
    slider: [34, 35, 36, 39, 32, 33, 37, 38, 25, 26, 27, 13, 14, 4, 15, 12, 2, 0],
    button: [16, 17, 18, 19, 21, 22, 23, 5, 13, 14, 4, 25, 26, 27, 32, 33, 15, 12, 2, 0],
    output: [18, 19, 21, 22, 23, 5, 16, 17, 25, 26, 27, 13, 14, 4, 32, 33, 15, 12, 2, 0],
  };
  const SCREEN_SLOTS = [0, 1, 2, 3].flatMap(bus => [[bus, 0x3C], [bus, 0x3D]]);

  const DIY_REFERENCE = {   // proto/presets/diy-reference.json (a test keeps them equal)
    preset: 'diy-reference',
    sliders: [{ pin: 33 }, { pin: 35 }, { pin: 32 }, { pin: 36 }, { pin: 34 }],
    buttons: [
      { pin: 4, led: { pin: 12, activeLow: true } },
      { pin: 14, led: { pin: 21, activeLow: true } },
      { pin: 5, ledPair: { pinA: 18, pinB: 19, activeLow: false } },
    ],
    i2c0: { sda: 25, scl: 26 },
    i2c1: { sda: 27, scl: 13 },
  };
  const PRESETS = [
    { id: 'diy-reference', name: 'DIY reference' },
    { id: 'screens', name: 'Screen controller', sliders: 5, kinds: [3, 3, 3, 3] },
    { id: 'mixed', name: 'Mixed', sliders: 5, kinds: [3, 3, 1, 2] },
  ];
  const HARDWARE = ['sliders', 'buttons', 'i2c0', 'i2c1', 'i2c2', 'i2c3', 'preset'];

  const clone = o => (o === undefined ? undefined : JSON.parse(JSON.stringify(o)));
  function kindOf(b) {
    if (b.led) return KIND.LED;
    if (b.ledPair) return KIND.LED_PAIR;
    if (b.screen) return KIND.SCREEN;
    return KIND.NONE;
  }
  /** A button of `kind`, keeping the button pin (and the indicator when the kind is unchanged). */
  function withKind(b, kind) {
    if (kindOf(b) === kind) return clone(b);
    const out = b.pin !== undefined ? { pin: b.pin } : {};
    if (kind === KIND.LED) out.led = {};
    if (kind === KIND.LED_PAIR) out.ledPair = {};
    if (kind === KIND.SCREEN) out.screen = {};
    return out;
  }

  /** The builder's view of a config: { preset, sliders, buttons, base }. base keeps everything
   *  else (UDP, screen behaviour, input tuning, I2C bus definitions). */
  function fromConfig(config) {
    const c = clone(config) || {};
    const buttons = (c.buttons || []).map(b => (b.screen ? { ...b, screen: { bus: 0, address: 0x3C, ...b.screen } } : b));
    return { preset: c.preset || 'custom', sliders: c.sliders || [], buttons, base: c };
  }
  /** A preset's layout over `base` (its non-hardware fields are kept, as the app's ApplyTo does). */
  function presetLayout(id, base = {}) {
    const keep = Object.fromEntries(Object.entries(clone(base)).filter(([k]) => !HARDWARE.includes(k)));
    if (id === 'diy-reference') return fromConfig({ ...keep, ...clone(DIY_REFERENCE) });
    const p = PRESETS.find(x => x.id === id);
    return { preset: id, sliders: Array.from({ length: p.sliders }, () => ({})),
      buttons: p.kinds.map(k => withKind({}, k)), base: keep };
  }
  const kinds = layout => layout.buttons.map(kindOf);

  /**
   * Turns a layout into a ControllerConfig, keeping every valid pin and allocating the rest.
   * Returns { config, pins } (pins: pinCount) or { error: { message, what: 'slider'|'button', index } }.
   */
  function toConfig(layout) {
    const base = layout.base || {};
    const udp = !!(base.udp && base.udp.enabled);
    if (layout.sliders.length > LIMITS.sliders) return fail(`${LIMITS.sliders} sliders maximum`, 'slider', LIMITS.sliders);
    if (layout.buttons.length > LIMITS.buttons) return fail(`${LIMITS.buttons} buttons maximum`, 'button', LIMITS.buttons);
    const used = new Set();
    const take = p => { used.add(p); return p; };
    const free = p => p !== undefined && p !== null && !used.has(p);
    const sliderOk = p => free(p) && adcCapable(p) && !(udp && CHIP.adc2.has(p));
    const sliders = layout.sliders.map(() => ({}));
    const buttons = layout.buttons.map(b => withKind(b, kindOf(b)));
    const slots = new Set();

    // 1. Keep every pin that is still valid, in the validator's walk order.
    layout.sliders.forEach((s, i) => { if (sliderOk(s.pin)) sliders[i].pin = take(s.pin); else delete sliders[i].pin; });
    buttons.forEach(b => {
      if (free(b.pin) && usable(b.pin)) take(b.pin); else delete b.pin;
      if (b.led) { if (free(b.led.pin) && outputCapable(b.led.pin)) take(b.led.pin); else delete b.led.pin; }
      if (b.ledPair) {
        const p = b.ledPair;
        if (free(p.pinA) && outputCapable(p.pinA)) take(p.pinA); else delete p.pinA;
        if (free(p.pinB) && outputCapable(p.pinB)) take(p.pinB); else delete p.pinB;
      }
      if (b.screen) {   // {} = a new screen (withKind); fromConfig fills in the proto defaults
        const { bus, address } = b.screen, key = `${bus}:${address}`;
        if (bus >= 0 && bus <= 3 && (address === 0x3C || address === 0x3D) && !slots.has(key)) slots.add(key);
        else b.screen = {};
      }
    });
    const buses = [0, 1, 2, 3].map(n => clone(base[`i2c${n}`]));
    const busUsed = n => buttons.some(b => b.screen && b.screen.bus === n);
    const keepBus = n => {
      const d = buses[n];
      if (d && free(d.sda) && free(d.scl) && outputCapable(d.sda) && outputCapable(d.scl) && d.sda !== d.scl) { take(d.sda); take(d.scl); return true; }
      return false;
    };
    // New screens get their (bus, address) slot now, so every bus a screen will use keeps its
    // defined pins before sliders, buttons and LEDs are allocated.
    for (let i = 0; i < buttons.length; i++) {
      const b = buttons[i];
      if (!b.screen || b.screen.bus !== undefined) continue;
      const slot = SCREEN_SLOTS.find(([bus, a]) => !slots.has(`${bus}:${a}`));
      if (!slot) return fail(`${LIMITS.screens} screens maximum`, 'button', i);
      slots.add(`${slot[0]}:${slot[1]}`);
      b.screen = { bus: slot[0], address: slot[1] };
    }
    const kept = [0, 1, 2, 3].map(n => busUsed(n) && keepBus(n));

    // 2. Allocate what is missing.
    const pick = (role, ok) => PREFER[role].find(p => free(p) && ok(p));
    for (let i = 0; i < sliders.length; i++) {
      if (sliders[i].pin !== undefined) continue;
      const p = pick('slider', sliderOk);
      if (p === undefined) return fail('No free ADC pin', 'slider', i);
      sliders[i].pin = take(p);
    }
    for (let i = 0; i < buttons.length; i++) {
      if (buttons[i].pin !== undefined) continue;
      const p = pick('button', usable);
      if (p === undefined) return fail('No free input pin', 'button', i);
      buttons[i].pin = take(p);
    }
    for (let i = 0; i < buttons.length; i++) {
      const b = buttons[i];
      const out = () => { const p = pick('output', outputCapable); return p === undefined ? undefined : take(p); };
      if (b.led && b.led.pin === undefined) { b.led.pin = out(); if (b.led.pin === undefined) return fail('No free output pin', 'button', i); }
      if (b.ledPair) {
        if (b.ledPair.pinA === undefined) b.ledPair.pinA = out();
        if (b.ledPair.pinB === undefined) b.ledPair.pinB = out();
        if (b.ledPair.pinA === undefined || b.ledPair.pinB === undefined) return fail('No free output pin', 'button', i);
      }
    }
    for (let n = 0; n < 4; n++) {
      if (!busUsed(n) || kept[n]) continue;
      const sda = pick('output', outputCapable); if (sda === undefined) return fail('No free output pin', 'button', firstScreenOn(buttons, n));
      take(sda);
      const scl = pick('output', outputCapable); if (scl === undefined) return fail('No free output pin', 'button', firstScreenOn(buttons, n));
      take(scl);
      buses[n] = { ...(buses[n] && buses[n].clockHz !== undefined ? { clockHz: buses[n].clockHz } : {}), sda, scl };
    }

    const config = Object.fromEntries(Object.entries(clone(base)).filter(([k]) => !HARDWARE.includes(k)));
    config.sliders = sliders;
    config.buttons = buttons;
    buses.forEach((d, n) => { if (d) config[`i2c${n}`] = d; });
    config.preset = layout.preset || 'custom';
    return { config, pins: pinCount(used) };
  }
  /** How many of each pin pool a layout uses: I/O (buttons, LEDs, I2C, and sliders once the
   *  input-only pins run out) and input-only (sliders only). */
  function pinCount(used) {
    const pins = [...used];
    const total = [...CHIP.usable];
    return {
      io: pins.filter(p => !CHIP.inputOnly.has(p)).length, ioTotal: total.filter(p => !CHIP.inputOnly.has(p)).length,
      inputOnly: pins.filter(p => CHIP.inputOnly.has(p)).length, inputOnlyTotal: total.filter(p => CHIP.inputOnly.has(p)).length,
    };
  }
  const firstScreenOn = (buttons, n) => buttons.findIndex(b => b.screen && b.screen.bus === n);
  const fail = (message, what, index) => ({ error: { message, what, index } });

  /** Why `change(layout)` wouldn't fit, or null when it would. change returns the new layout. */
  function check(layout, change) {
    const r = toConfig(change(layout));
    return r.error ? r.error.message : null;
  }

  /** A toConfig error message, explained for the builder: what ran out and what costs pins. */
  function explain(message) {
    const io = [...CHIP.usable].filter(p => !CHIP.inputOnly.has(p)).length;
    if (message === 'No free output pin' || message === 'No free input pin')
      return `All ${io} I/O pins are in use. A button takes 1, its LED 1 more, an LED pair 2 more, and the screens 2 per I2C bus. ` +
        'Remove a button or slider, or pick a lighter indicator.';
    if (message === 'No free ADC pin')
      return 'No analog pin is left for a slider. Sliders need ADC pins, and buttons and LEDs may hold some of them; with UDP on, only the 8 ADC1 pins work.';
    return `The firmware supports ${message}.`;
  }

  /**
   * A config's pins, worded as Volco's Controller page asks for them, so a board can be set up
   * there by hand: [{ label, value }], sliders, then buttons, then the I2C buses screens use.
   */
  function pinMap(config) {
    const c = config || {};
    const lights = low => `lights when ${low ? 'low' : 'high'}`;
    const hex = a => `0x${a.toString(16).toUpperCase()}`;
    const rows = (c.sliders || []).map((s, i) => ({ label: `Slider ${i + 1}`, value: `${s.pin}` }));
    (c.buttons || []).forEach((b, i) => {
      let value = `${b.pin}`;
      if (b.led) value += ` · LED ${b.led.pin}, ${lights(b.led.activeLow)}`;
      if (b.ledPair) value += ` · LED A ${b.ledPair.pinA}, B ${b.ledPair.pinB}, ${lights(b.ledPair.activeLow)}`;
      if (b.screen) value += ` · screen on bus ${b.screen.bus ?? 0}, ${hex(b.screen.address ?? 0x3C)}`;
      rows.push({ label: `Button ${i + 1}`, value });
    });
    [0, 1, 2, 3].forEach(n => {
      const d = c[`i2c${n}`];
      if (d && (c.buttons || []).some(b => b.screen && (b.screen.bus ?? 0) === n))
        rows.push({ label: `I2C bus ${n}`, value: `SDA ${d.sda} · SCL ${d.scl}` });
    });
    return rows;
  }

  const api = { KIND, LIMITS, CHIP, PREFER, PRESETS, DIY_REFERENCE, kindOf, withKind, kinds, fromConfig, presetLayout, toConfig, check, explain,
    pinMap, usable, outputCapable, adcCapable };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoLayout = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);

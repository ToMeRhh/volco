// The Volco wire protocol, controller side (firmware/lib/volco_protocol): framing, a streaming
// parser, the frames the controller sends and the Led/Screen decoders. No DOM: index.html loads
// it as a plain script (globalThis.VolcoProtocol), the Node tests with require().
(function (root) {
  'use strict';

  const MSG = {
    HELLO: 0x01, ALIVE: 0x02, SLIDER: 0x03, BUTTON: 0x04, CONFIG_DATA: 0x05, CONFIG_RESULT: 0x06,
    HELLO_REQUEST: 0x81, HEARTBEAT: 0x82, LED: 0x83, SCREEN: 0x84, BEGIN: 0x85, COMMIT: 0x86,
    CONFIG_READ: 0x87, CONFIG_BEGIN: 0x88, CONFIG_CHUNK: 0x89, CONFIG_END: 0x8A,
    CALIBRATE: 0x8E, CALIBRATION: 0x09,
    PIN_WATCH: 0x8B, PIN_ACTIVITY: 0x07, I2C_SCAN: 0x8C, I2C_SCAN_RESULT: 0x08, PIN_DRIVE: 0x8D,
  };
  const CALIBRATE = { QUERY: 0, START: 1, DONE: 2, CANCEL: 3 };
  const CAL_FLAG = { LEARNED: 1, SWEEPING: 2, AUTO: 4 };
  const WATCH = { STOP: 0, ANALOG: 1, BUTTON: 2 };
  const NAMES = {
    0x01: 'Hello', 0x02: 'Alive', 0x03: 'Slider', 0x04: 'Button', 0x05: 'ConfigData', 0x06: 'ConfigResult',
    0x81: 'HelloRequest', 0x82: 'Heartbeat', 0x83: 'Led', 0x84: 'Screen', 0x85: 'Begin', 0x86: 'Commit',
    0x87: 'ConfigRead', 0x88: 'ConfigWriteBegin', 0x89: 'ConfigWriteChunk', 0x8A: 'ConfigWriteEnd',
    0x09: 'Calibration', 0x8E: 'Calibrate',
    0x8B: 'PinWatch', 0x07: 'PinActivity', 0x8C: 'I2cScan', 0x08: 'I2cScanResult', 0x8D: 'PinDrive',
  };
  const KIND = { NONE: 0, LED: 1, LED_PAIR: 2, SCREEN: 3 };
  const TRANSITION = { NONE: 0, SLIDE_LEFT: 1, SLIDE_RIGHT: 2 };
  const TOP = { EMPTY: 0, TEXT: 1, DOTS: 2 };
  const START = 0xAA, VERSION = 1, NO_ICON = 0xFF, MAX_TEXT = 60, MAX_BUTTONS = 32, MAX_FIRMWARE_ID = 32;

  const utf8 = s => Array.from(new TextEncoder().encode(s));
  const fromUtf8 = bytes => new TextDecoder().decode(Uint8Array.from(bytes));

  /** The whole frame for a payload (type + body): 0xAA | length | payload | XOR(payload). */
  function frame(payload) {
    let cs = 0;
    for (const b of payload) cs ^= b;
    return Uint8Array.from([START, payload.length, ...payload, cs]);
  }

  /** A byte-stream parser. onPayload(payload: number[]) for each valid frame; onBad(payload)
   *  for a frame whose checksum doesn't match (it is dropped, as the firmware drops it). */
  function createParser(onPayload, onBad = () => {}) {
    let state = 0, len = 0, buf = [];
    return {
      feed(bytes) {
        for (const b of bytes) {
          if (state === 0) { if (b === START) state = 1; }
          else if (state === 1) { len = b; buf = []; state = len === 0 ? 0 : 2; }
          else if (state === 2) { buf.push(b); if (buf.length >= len) state = 3; }
          else {
            state = 0;
            let cs = 0;
            for (const x of buf) cs ^= x;
            if (cs === b) onPayload(buf); else onBad(buf);
          }
        }
      },
    };
  }

  /** Hello (spec §5.2). kinds: one KIND per button. */
  function hello({ chip = 1, firmware = 'sim', configState = 0, configCrc = 0, sliders, kinds }) {
    const fw = utf8(firmware).slice(0, MAX_FIRMWARE_ID), k = kinds.slice(0, MAX_BUTTONS);
    return [MSG.HELLO, VERSION, chip, fw.length, ...fw, configState,
      (configCrc >>> 24) & 255, (configCrc >>> 16) & 255, (configCrc >>> 8) & 255, configCrc & 255,
      sliders, k.length, ...k];
  }
  const alive = () => [MSG.ALIVE];
  const slider = (index, value) => [MSG.SLIDER, index, (value >> 8) & 255, value & 255];
  const button = (index, pressed) => [MSG.BUTTON, index, pressed ? 1 : 0];
  /** Calibration report (slider calibration spec): a slider's flags (CAL_FLAG) and learned ends. */
  const calibration = (index, flags, lo, hi) =>
    [MSG.CALIBRATION, index, flags, (lo >> 8) & 255, lo & 255, (hi >> 8) & 255, hi & 255];
  /** Decodes a Calibrate payload: { index, action }, or null unless it is exactly 3 bytes, as the
   *  firmware reads it. An unknown action is answered as a Query, so it comes back as one. */
  function decodeCalibrate(p) {
    if (p.length !== 3 || p[0] !== MSG.CALIBRATE) return null;
    return { index: p[1], action: p[2] > CALIBRATE.CANCEL ? CALIBRATE.QUERY : p[2] };
  }

  /** PinActivity: the pin a watched slider or button just moved. */
  const pinActivity = pin => [MSG.PIN_ACTIVITY, pin];
  /** I2cScanResult: the bus's pins and the addresses answering on it (an empty list when none). */
  const i2cScanResult = (sda, scl, addresses) => [MSG.I2C_SCAN_RESULT, sda, scl, addresses.length, ...addresses];
  /** Decodes a PinWatch payload: { role }, or null unless it is exactly 2 bytes with a known role. */
  function decodePinWatch(p) {
    if (p.length !== 2 || p[0] !== MSG.PIN_WATCH || p[1] > WATCH.BUTTON) return null;
    return { role: p[1] };
  }
  /** Decodes an I2cScan payload: { sda, scl }, or null unless it is exactly 3 bytes. */
  function decodeI2cScan(p) {
    if (p.length !== 3 || p[0] !== MSG.I2C_SCAN) return null;
    return { sda: p[1], scl: p[2] };
  }
  /** Decodes a PinDrive payload: { pin, level, ms }, or null unless it is exactly 5 bytes with level 0 or 1. */
  function decodePinDrive(p) {
    if (p.length !== 5 || p[0] !== MSG.PIN_DRIVE || p[2] > 1) return null;
    return { pin: p[1], level: p[2], ms: (p[3] << 8) | p[4] };
  }

  /** Layout-1 screen content, as the firmware's screenText builds it. */
  function screenText(top, icon, main) {
    return { layout: 1, topKind: top ? TOP.TEXT : TOP.EMPTY, top, dotsSelected: 0, dotsCount: 0, icon, flags: 0, main };
  }

  /** Encodes a Screen payload (the PC side; used by demo mode and the tests). */
  function screen(index, transition, c) {
    const text = s => { const b = utf8(s).slice(0, MAX_TEXT); return [b.length, ...b]; };
    const top = c.topKind === TOP.TEXT ? text(c.top) : c.topKind === TOP.DOTS ? [c.dotsSelected, c.dotsCount] : [];
    return [MSG.SCREEN, index, transition, c.layout, c.topKind, ...top, c.icon, c.flags, ...text(c.main)];
  }

  /** Decodes a Screen payload: { button, transition, content }, or null when a length overruns
   *  the payload (the firmware drops the message). An unknown transition becomes NONE. */
  function decodeScreen(p) {
    if (p.length < 5 || p[0] !== MSG.SCREEN) return null;
    let i = 5;
    const text = () => {
      if (i >= p.length) return null;
      const n = p[i++];
      if (n > MAX_TEXT || i + n > p.length) return null;
      const s = fromUtf8(p.slice(i, i + n));
      i += n;
      return s;
    };
    const c = { layout: p[3], topKind: p[4], top: '', dotsSelected: 0, dotsCount: 0, icon: NO_ICON, flags: 0, main: '' };
    if (c.topKind === TOP.TEXT) { const t = text(); if (t === null) return null; c.top = t; }
    else if (c.topKind === TOP.DOTS) { if (i + 2 > p.length) return null; c.dotsSelected = p[i++]; c.dotsCount = p[i++]; }
    else c.topKind = TOP.EMPTY;
    if (i + 2 > p.length) return null;
    c.icon = p[i++]; c.flags = p[i++];
    const main = text();
    if (main === null) return null;
    c.main = main;
    const transition = p[2] > TRANSITION.SLIDE_RIGHT ? TRANSITION.NONE : p[2];
    return { button: p[1], transition, content: c };
  }

  const hex = bytes => Array.from(bytes, b => b.toString(16).padStart(2, '0')).join(' ');
  const nameOf = type => NAMES[type] || '0x' + type.toString(16).padStart(2, '0');

  const api = { MSG, NAMES, CALIBRATE, CAL_FLAG, WATCH, KIND, TRANSITION, TOP, NO_ICON, MAX_TEXT, MAX_BUTTONS,
    frame, createParser, hello, alive, slider, button, calibration, decodeCalibrate,
    pinActivity, i2cScanResult, decodePinWatch, decodeI2cScan, decodePinDrive, screenText, screen, decodeScreen, hex, nameOf };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoProtocol = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);

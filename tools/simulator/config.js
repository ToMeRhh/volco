// The simulator's controller config (spec §10): stores one config blob, answers ConfigRead with it
// (§5.3), receives config writes by the firmware's receiver rules (§5.4) and builds the Hello (§5.2).
// No DOM: index.html uses it as a plain script (globalThis.VolcoSimConfig), the Node test with
// require(). Validation is a subset of the firmware's (§6.4): decode with the §6.1 limits and NUL
// checks, the counts, and the screen rules of step 3. Pins are not checked.
(function (root) {
  'use strict';

  const MSG = { CONFIG_DATA: 0x05, CONFIG_RESULT: 0x06, CONFIG_READ: 0x87, CONFIG_BEGIN: 0x88,
                CONFIG_CHUNK: 0x89, CONFIG_END: 0x8A, HELLO: 0x01 };
  const CODE = { OK: 0, BAD_SEQUENCE: 1, BAD_CRC: 2, TOO_LARGE: 3, DECODE_FAILED: 4, INVALID: 5 };
  const FIELD = { SCREEN_BUS: 6, SCREEN_ADDRESS: 7 };
  const NO_ITEM = 0xFF;
  const MAX_BLOB = 1024, CHUNK = 240, TIMEOUT_MS = 2000;
  const MAX_SLIDERS = 16, MAX_BUTTONS = 32;
  const KIND = { NONE: 0, LED: 1, LED_PAIR: 2, SCREEN: 3 };

  const CRC_TABLE = (() => {
    const t = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
      t[n] = c >>> 0;
    }
    return t;
  })();

  /** CRC-32 (zlib), as the firmware and the app compute it. */
  function crc32(bytes) {
    let c = 0xFFFFFFFF;
    for (const b of bytes) c = CRC_TABLE[(c ^ b) & 0xFF] ^ (c >>> 8);
    return (c ^ 0xFFFFFFFF) >>> 0;
  }

  const u16 = v => [(v >> 8) & 0xFF, v & 0xFF];
  const u32 = v => [(v >>> 24) & 0xFF, (v >>> 16) & 0xFF, (v >>> 8) & 0xFF, v & 0xFF];

  // ── Decode (§6.1 limits and NUL checks on the wire bytes, then protobufjs) ──────────────

  function readVarint(b, i) {
    let v = 0, shift = 0;
    for (; i < b.length && shift < 64; shift += 7) {
      const x = b[i++];
      v += (x & 0x7F) * 2 ** shift;
      if (!(x & 0x80)) return [v, i];
    }
    return null;
  }

  /** Checks the string fields nanopb limits: preset (top-level 8, max 24 bytes), and udp (5)
   *  ssid (2, max 32) and password (3, max 64). False when one is over its limit, holds a NUL or
   *  isn't valid UTF-8, or when the wire bytes are malformed. */
  function stringsClean(b, limits) {
    const utf8 = new TextDecoder('utf-8', { fatal: true });
    let i = 0;
    while (i < b.length) {
      const tag = readVarint(b, i);
      if (!tag) return false;
      i = tag[1];
      const field = Math.floor(tag[0] / 8), wire = tag[0] % 8;
      if (wire === 0) { const v = readVarint(b, i); if (!v) return false; i = v[1]; }
      else if (wire === 1) i += 8;
      else if (wire === 5) i += 4;
      else if (wire === 2) {
        const len = readVarint(b, i);
        if (!len) return false;
        const start = len[1], end = start + len[0];
        if (end > b.length) return false;
        const value = b.subarray(start, end);
        const limit = limits[field];
        if (typeof limit === 'number') {
          if (value.length > limit || value.includes(0)) return false;
          try { utf8.decode(value); } catch { return false; }
        } else if (limit && !stringsClean(value, limit)) return false;
        i = end;
      } else return false;
    }
    return i === b.length;
  }

  const STRING_LIMITS = { 8: 24, 5: { 2: 32, 3: 64 } };

  /** The decoded config, or null where the firmware answers DECODE_FAILED. */
  function decode(type, blob) {
    if (blob.length > MAX_BLOB || !stringsClean(blob, STRING_LIMITS)) return null;
    let config;
    try { config = type.decode(blob); } catch { return null; }
    if (config.sliders.length > MAX_SLIDERS || config.buttons.length > MAX_BUTTONS) return null;
    return config;
  }

  /** §6.4 step 3's screen rules (the simulator has no pins): [code, item, field]. */
  function validate(config) {
    const used = new Set();
    for (let i = 0; i < config.buttons.length; i++) {
      const s = config.buttons[i].screen;
      if (!s) continue;
      if (s.bus > 3) return [CODE.INVALID, i, FIELD.SCREEN_BUS];
      if (s.address !== 0x3C && s.address !== 0x3D) return [CODE.INVALID, i, FIELD.SCREEN_ADDRESS];
      const key = s.bus * 256 + s.address;
      if (used.has(key)) return [CODE.INVALID, i, FIELD.SCREEN_ADDRESS];
      used.add(key);
    }
    return [CODE.OK, NO_ITEM, NO_ITEM];
  }

  function kindOf(button) {
    if (button.led) return KIND.LED;
    if (button.ledPair) return KIND.LED_PAIR;
    if (button.screen) return KIND.SCREEN;
    return KIND.NONE;
  }

  /**
   * @param {object} o
   * @param {object} o.protobuf       protobufjs (the light build is enough)
   * @param {object} o.descriptor     the JSON descriptor of proto/volco_config.proto
   * @param {(payload:number[]) => void} o.send   sends one frame's payload (type + body)
   * @param {(blob:Uint8Array) => void} o.store   keeps a saved blob
   * @param {() => Uint8Array|null} o.stored      the stored blob, or null
   * @param {() => void} o.reboot     called after an OK, once the result has been sent
   */
  function createSession(o) {
    const type = o.protobuf.Root.fromJSON(o.descriptor).lookupType('volco.config.ControllerConfig');
    let t = null;   // the open transfer: { seq, total, crc, buf, received, last }

    const reply = (seq, code, item = NO_ITEM, field = NO_ITEM) =>
      o.send([MSG.CONFIG_RESULT, seq, code, item, field]);

    function tick(now) {
      if (t && now - t.last >= TIMEOUT_MS) t = null;   // rule 6: closes silently
    }

    function read(seq) {
      const blob = o.stored() || new Uint8Array(0);
      const crc = blob.length ? crc32(blob) : 0;
      let offset = 0;
      do {
        const data = blob.subarray(offset, offset + CHUNK);
        o.send([MSG.CONFIG_DATA, seq, ...u16(blob.length), ...u16(offset), ...u32(crc), ...data]);
        offset += data.length;
      } while (offset < blob.length);
    }

    function begin(seq, total, crc, now) {
      t = null;                                         // rule 1: (re)starts
      if (total === 0 || total > MAX_BLOB) { reply(seq, CODE.TOO_LARGE); return; }
      t = { seq, total, crc, buf: new Uint8Array(total), received: 0, last: now };
    }

    function chunk(seq, offset, data, now) {
      tick(now);
      if (!t) return;                                   // rule 3
      if (seq !== t.seq || offset !== t.received || data.length === 0 || data.length > CHUNK
          || t.received + data.length > t.total) {
        const open = t.seq;
        t = null;
        reply(open, CODE.BAD_SEQUENCE);   // like the firmware: the open transfer's seq
        return;
      }
      t.buf.set(data, t.received);
      t.received += data.length;
      t.last = now;
    }

    function end(seq, now) {
      tick(now);
      if (!t) return;
      const x = t;
      t = null;
      if (seq !== x.seq || x.received !== x.total) { reply(x.seq, CODE.BAD_SEQUENCE); return; }
      if (crc32(x.buf) !== x.crc) { reply(seq, CODE.BAD_CRC); return; }
      const config = decode(type, x.buf);
      if (!config) { reply(seq, CODE.DECODE_FAILED); return; }
      const [code, item, field] = validate(config);
      if (code !== CODE.OK) { reply(seq, code, item, field); return; }
      o.store(x.buf);
      reply(seq, CODE.OK);
      o.reboot();
    }

    /** Handles a config frame; false for any other type. */
    function onFrame(p, now) {
      const be16 = i => (p[i] << 8) | p[i + 1];
      switch (p[0]) {
        case MSG.CONFIG_READ: if (p.length >= 2) read(p[1]); return true;
        case MSG.CONFIG_BEGIN:
          if (p.length >= 8) begin(p[1], be16(2), ((p[4] << 24) | (p[5] << 16) | (p[6] << 8) | p[7]) >>> 0, now);
          return true;
        case MSG.CONFIG_CHUNK: if (p.length >= 4) chunk(p[1], be16(2), Uint8Array.from(p.slice(4)), now); return true;
        case MSG.CONFIG_END: if (p.length >= 2) end(p[1], now); return true;
        default: return false;
      }
    }

    /** The Hello payload (§5.2): chip 1, firmware "sim", the stored config's state and CRC, and
     *  the stored config's hardware, or the fallback controller type's when none is stored. */
    function hello(fallback) {
      const blob = o.stored();
      const config = blob && blob.length ? decode(type, blob) : null;
      const usable = config && validate(config)[0] === CODE.OK;
      const state = !blob || !blob.length ? 0 : usable ? 1 : 2;
      const crc = blob && blob.length ? crc32(blob) : 0;
      const sliders = usable ? config.sliders.length : fallback.sliders;
      const kinds = usable ? config.buttons.map(kindOf) : fallback.buttons;
      return [MSG.HELLO, 1, 1, 3, 0x73, 0x69, 0x6D, state, ...u32(crc), sliders, kinds.length, ...kinds];
    }

    return { onFrame, tick, hello, isOpen: () => t !== null };
  }

  const api = { createSession, crc32, CODE };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoSimConfig = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);

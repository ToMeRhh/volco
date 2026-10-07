// Node tests of the simulator's config session (tools/simulator/config.js), driven by the shared
// golden files: firmware/test/golden/frames.txt and config.txt.
//   cd tools/simulator/test && npm ci && node --test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';

const require = createRequire(import.meta.url);
const protobuf = require('protobufjs/light');
const sim = require('../config.js');
const descriptor = require('../volco_config_descriptor.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const golden = path.join(here, '..', '..', '..', 'firmware', 'test', 'golden');
const hexBytes = s => (s === '-' ? [] : s.match(/../g).map(h => parseInt(h, 16)));

const frames = Object.fromEntries(fs.readFileSync(path.join(golden, 'frames.txt'), 'utf8').split('\n')
  .filter(l => l.trim() && !l.startsWith('#'))
  .map(l => { const [name, ...hx] = l.trim().split(/\s+/); return [name, hx.map(h => parseInt(h, 16))]; }));
const payloadOf = frame => frame.slice(2, -1);

const cases = fs.readFileSync(path.join(golden, 'config.txt'), 'utf8').split('\n')
  .filter(l => l.trim() && !l.startsWith('#'))
  .map(l => { const [kind, name, hex, ...rest] = l.trim().split(/\s+/); return { kind, name, blob: hexBytes(hex), rest }; });

const DIY = Uint8Array.from(cases.find(c => c.name === 'diy_reference').blob);
const LED_TYPE = { sliders: 5, buttons: [1, 1, 2] };

function harness(initial = null) {
  const h = { sent: [], stored: initial, reboots: 0 };
  h.session = sim.createSession({
    protobuf, descriptor,
    send: p => h.sent.push(p),
    store: b => { h.stored = Uint8Array.from(b); },
    stored: () => h.stored,
    reboot: () => { h.reboots++; },
  });
  h.feed = (p, now = 0) => h.session.onFrame(p, now);
  return h;
}

/** The payloads of a whole write burst for `blob` (Begin, chunks, End). */
function burst(seq, blob, { total = blob.length, crc = sim.crc32(blob) } = {}) {
  const out = [[0x88, seq, total >> 8, total & 255, crc >>> 24, (crc >>> 16) & 255, (crc >>> 8) & 255, crc & 255]];
  for (let off = 0; off < blob.length; off += 240) out.push([0x89, seq, off >> 8, off & 255, ...blob.slice(off, off + 240)]);
  out.push([0x8A, seq]);
  return out;
}

function write(h, seq, blob, now = 0) {
  for (const p of burst(seq, [...blob])) h.feed(p, now);
  return h.sent.filter(p => p[0] === 0x06).at(-1);
}

test('the golden write burst stores the blob, answers OK and reboots', () => {
  const h = harness();
  for (const name of ['config_write_begin_seq9', 'config_write_chunk_seq9', 'config_write_end_seq9'])
    assert.equal(h.feed(payloadOf(frames[name])), true);
  assert.deepEqual(h.sent, [payloadOf(frames.config_result_ok_seq9)]);
  assert.deepEqual([...h.stored], [0x32, 0x02, 0x08, 0x64]);
  assert.equal(h.reboots, 1);
});

test('a read returns the stored blob in one ConfigData frame', () => {
  const h = harness(Uint8Array.from([0x32, 0x02, 0x08]));
  h.feed(payloadOf(frames.config_read_seq7));
  assert.equal(h.sent.length, 1);
  const p = h.sent[0];
  assert.deepEqual(p.slice(0, 6), [0x05, 7, 0, 3, 0, 0]);
  assert.equal(((p[6] << 24) | (p[7] << 16) | (p[8] << 8) | p[9]) >>> 0, sim.crc32([0x32, 0x02, 0x08]));
  assert.deepEqual(p.slice(10), [0x32, 0x02, 0x08]);
});

test('a read with nothing stored is the empty golden frame', () => {
  const h = harness();
  h.feed(payloadOf(frames.config_read_seq7));
  assert.deepEqual(h.sent, [payloadOf(frames.config_data_empty_seq7)]);
});

test('a 1024-byte blob reads back as five ordered chunks', () => {
  const blob = Uint8Array.from({ length: 1024 }, (_, i) => i & 255);
  const h = harness(blob);
  h.feed([0x87, 3]);
  assert.equal(h.sent.length, 5);
  assert.deepEqual(h.sent.map(p => (p[4] << 8) | p[5]), [0, 240, 480, 720, 960]);
  assert.deepEqual(h.sent.flatMap(p => p.slice(10)), [...blob]);
});

test('every decode-fail vector answers DECODE_FAILED', () => {
  for (const c of cases.filter(c => c.kind === 'decode-fail')) {
    const h = harness();
    const r = write(h, 1, c.blob);
    assert.equal(r[2], sim.CODE.DECODE_FAILED, c.name);
    assert.equal(h.stored, null, c.name);
  }
});

test('every decode-ok vector decodes (it may still fail the screen rules)', () => {
  for (const c of cases.filter(c => c.kind === 'decode-ok' && c.blob.length > 0)) {
    const r = write(harness(), 1, c.blob);
    assert.notEqual(r[2], sim.CODE.DECODE_FAILED, c.name);
  }
});

test('the screen-rule vectors give the firmware\'s triple', () => {
  const screenRules = cases.filter(c => c.kind === 'validate' && (c.rest[2] === '6' || c.rest[2] === '7'));
  assert.ok(screenRules.length >= 4);
  for (const c of screenRules) {
    const r = write(harness(), 1, c.blob);
    assert.deepEqual(r.slice(2), c.rest.map(Number), c.name);
  }
});

test('the valid vectors are stored', () => {
  for (const c of cases.filter(c => c.kind === 'validate' && c.rest[0] === '0')) {
    const h = harness();
    assert.equal(write(h, 1, c.blob)[2], sim.CODE.OK, c.name);
    assert.deepEqual([...h.stored], c.blob, c.name);
  }
});

test('receiver rules: TOO_LARGE, BAD_SEQUENCE, BAD_CRC, timeout', () => {
  let h = harness();
  h.feed([0x88, 4, 0x04, 0x01, 0, 0, 0, 0]);                       // total 1025
  assert.deepEqual(h.sent.at(-1), [0x06, 4, sim.CODE.TOO_LARGE, 0xFF, 0xFF]);

  h = harness();
  const [begin, chunk] = burst(5, [0x32, 0x02, 0x08, 0x64]);
  h.feed(begin);
  h.feed([0x89, 5, 0, 1, 0x02]);                                   // offset 1 before 0
  assert.deepEqual(h.sent.at(-1), [0x06, 5, sim.CODE.BAD_SEQUENCE, 0xFF, 0xFF]);
  h.feed(chunk);                                                   // closed: ignored
  assert.equal(h.sent.length, 1);

  h = harness();
  h.feed(begin);
  h.feed([0x89, 6, ...chunk.slice(2)]);                            // another seq
  assert.deepEqual(h.sent.at(-1), [0x06, 5, sim.CODE.BAD_SEQUENCE, 0xFF, 0xFF]);

  h = harness();
  for (const p of burst(7, [0x32, 0x02, 0x08, 0x64], { crc: 0x12345678 })) h.feed(p);
  assert.deepEqual(h.sent.at(-1), [0x06, 7, sim.CODE.BAD_CRC, 0xFF, 0xFF]);

  h = harness();
  h.feed(begin, 0);
  h.feed(chunk, 1999);
  h.feed([0x8A, 5], 3999);                                         // 2000 ms after the last chunk
  assert.equal(h.sent.length, 0);
  assert.equal(h.session.isOpen(), false);
});

test('Hello: the controller type with no config, the stored hardware after a save', () => {
  const h = harness();
  assert.deepEqual(h.session.hello(LED_TYPE),
    [0x01, 1, 1, 3, 0x73, 0x69, 0x6D, 0, 0, 0, 0, 0, 5, 3, 1, 1, 2]);
  assert.equal(write(h, 2, DIY)[2], sim.CODE.OK);
  const crc = sim.crc32(DIY);
  assert.deepEqual(h.session.hello({ sliders: 0, buttons: [3, 3] }),
    [0x01, 1, 1, 3, 0x73, 0x69, 0x6D, 1, crc >>> 24, (crc >>> 16) & 255, (crc >>> 8) & 255, crc & 255, 5, 3, 1, 1, 2]);
});

test('frames other than config frames are not handled', () => {
  assert.equal(harness().feed([0x82]), false);
});

test('fault: the next write is answered with the forced code and not stored', () => {
  const h = harness();
  h.session.forceNextResult(sim.CODE.BAD_CRC);
  assert.deepEqual(write(h, 3, DIY), [0x06, 3, sim.CODE.BAD_CRC, 0xFF, 0xFF]);
  assert.equal(h.stored, null);
  assert.equal(h.reboots, 0);
  assert.deepEqual(write(h, 4, DIY), [0x06, 4, sim.CODE.OK, 0xFF, 0xFF]);   // one-shot
  assert.equal(h.reboots, 1);
});

test('fault: a forced OK stores even a blob that fails validation', () => {
  const h = harness();
  h.session.forceNextResult(sim.CODE.OK);
  assert.deepEqual(write(h, 5, [0xFF, 0xFF]), [0x06, 5, sim.CODE.OK, 0xFF, 0xFF]);
  assert.deepEqual([...h.stored], [0xFF, 0xFF]);
  assert.equal(h.session.hello(LED_TYPE)[7], 2);   // Hello reports it damaged
});

test('current() reports the stored config', () => {
  assert.deepEqual(harness().session.current(), { state: 0, crc: 0, config: null, reason: null });
  const c = harness(DIY).session.current();
  assert.equal(c.state, 1);
  assert.equal(c.crc, sim.crc32(DIY));
  assert.deepEqual(c.config.sliders.map(s => s.pin), [33, 35, 32, 36, 34]);
  const damaged = harness(Uint8Array.from([0xFF, 0xFF])).session.current();
  assert.equal(damaged.state, 2);
  assert.equal(damaged.reason, 'It does not decode.');
});

test('encode() writes back the bytes current() read', () => {
  const h = harness(DIY);
  assert.deepEqual([...h.session.encode(h.session.current().config)], [...DIY]);
});

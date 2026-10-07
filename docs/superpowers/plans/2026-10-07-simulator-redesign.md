# Volco Simulator Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `tools/simulator/index.html` with a 3D desk-scene controller simulator that has a builder (presets, sliders, button kinds, automatic ESP32 pins), demo mode, a developer drawer, and pixel-exact OLEDs, while speaking the full Volco protocol and config session over WebSocket.

**Architecture:** A static page with no build step that opens from `file://`. One inline ES module loads a pinned three.js from jsDelivr and publishes it as `window.VolcoThree`; every other file is a classic script. The logic files (`protocol.js`, `link.js`, `config.js`, `layout.js`, `oled.js`, `demo.js`) are UMD scripts with no DOM, so Node's `node --test` runs them. The browser files (`stage.js`, `room.js`, `device.js`, `connection.js`, `builder.js`, `devtools.js`, `app.js`) build the scene and UI on top. The firmware's renderer produces golden bitmaps (`firmware/test/golden/render.txt`) that both test suites check, and `gen_assets.py` now also writes the firmware's fonts as `fonts.js`.

**Tech Stack:** Vanilla JS (ES2022, classic scripts + one inline module), three.js 0.170.0, protobufjs 7.4.0 (light), Node 22 `node:test`, PlatformIO native tests (Unity, C++17), Python 3 + Pillow ≥ 10 for `gen_assets.py`.

**Spec:** `docs/superpowers/specs/2026-10-07-simulator-redesign-design.md` (read it first; this plan argues from it). The approved visual mock is the artifact linked at the top of the spec.

**Provenance:** every code block below was run before this plan was written: the 58 Node tests pass, the firmware's `test_render` passes with the golden file, and the end-to-end browser check in Task 9 passes (13/13) against a fake Volco WebSocket server.

## Global Constraints

- **Opening the page:** `index.html` opens straight from disk (`file://`), with no build step and no local server. Over `file://` the browser refuses to import local ES modules, so every local file is a classic script. The one exception is the inline module that loads three.js.
- **Pinned versions:**
  - three.js `three@0.170.0`, loaded through an import map to `https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js` plus the `examples/jsm/` addons;
  - protobufjs `https://cdn.jsdelivr.net/npm/protobufjs@7.4.0/dist/light/protobuf.min.js`;
  - Node 22 for `node --test`, in `tools/simulator/test`.
- **No dotnet locally.** CI runs on GitHub Actions: `gh workflow run build.yml --ref worktree-simulator_new` (tests only; never pass `run_build=true`).
- **Git here:** use `/usr/bin/git` (plain `git` is refused in this worktree) and the Write tool rather than shell heredocs for new files.
- **Look:** a single light look. No dark theme and nothing glowing except the OLEDs and LEDs. Labels are few and short.
- **Timings** (spec §7.1, §6.4, D4): host timeout 3000 ms, keepalive 1000 ms, Commit fallback 500 ms, reboot 1000 ms, auto-save quiet 700 ms, slider send interval 20 ms, idle drift after 8000 ms.
- **Storage keys:** `volco-sim-config` (the base64 config blob, unchanged from today) and `volco-sim-prefs` (JSON).
- **Limits:** 16 sliders, 32 buttons, 8 screens (4 buses × 0x3C/0x3D). Pins come from the classic-ESP32 table of `ChipInfo.Esp32`.
- **No backward compatibility:** the old page is replaced outright.
- **Generated files** (`fonts.js`, `icons.js`, `volco_config_descriptor.js`, `render.txt`) are written only by their generators, never by hand.

## Review Focus

1. **A config Volco wrote whose screens use a bus with no I2C definition.** The builder must keep the screen and give the bus pins instead of failing or dropping the screen. Pinned by `layout.test.mjs` › "a screen on a bus with no I2C definition gets bus pins" (Task 6).
2. **Removing every slider or every button.** A 0-slider or 0-button board must still produce a valid config and build. Pinned by `layout.test.mjs` › "a board with no sliders or no buttons is valid" (Task 6).
3. **Several quick builder edits with Auto-save on.** They must cause exactly one save and one reboot, not one per click. Pinned by the end-to-end check "three quick edits cause one reboot" (Task 9).
4. **Apply while Volco is not connected.** The controller reboots but must stay in demo mode, with no connection attempt. Pinned by the end-to-end check "a reboot while disconnected stays in demo mode" (Task 9).
5. **Volco writes a config while connected.** The simulator answers OK, drops the link, reboots, reconnects by itself, and its next Hello reports the new hardware and the stored config's state. Pinned by the end-to-end checks "the write is answered OK" and "after the reboot Hello reports 5 sliders and 4 screens" (Task 9).

## File map

| File | Status | Responsibility |
|---|---|---|
| `firmware/tools/gen_assets.py` | modify | also writes `tools/simulator/fonts.js`; `icons.js` becomes UMD |
| `tools/simulator/fonts.js`, `icons.js` | generated | firmware fonts and icons for the simulator |
| `tools/simulator/protocol.js` | create | framing, parser, controller frames, Screen decode |
| `tools/simulator/link.js` | create | controller-side session (firmware Link) + fault hooks |
| `firmware/test/golden_render.h`, `firmware/test/golden/render.txt` | create | shared layout-1 golden bitmaps |
| `firmware/test/test_render/test_main.cpp` | modify | checks/rewrites `render.txt` |
| `tools/simulator/oled.js` | create | port of `volco_render`, `volco_bidi`, `volco_screen_state` |
| `tools/simulator/layout.js` | create | builder model, presets, pin allocation |
| `tools/simulator/config.js` | modify | `forceNextResult`, `current`, `encode` |
| `tools/simulator/demo.js` | create | demo host; output read from Volco screens |
| `tools/simulator/index.html` | rewrite | markup, styles, three.js loader, script order |
| `tools/simulator/stage.js`, `room.js`, `device.js` | create | 3D stage, home office, controller |
| `tools/simulator/connection.js`, `builder.js`, `devtools.js`, `app.js` | create | WebSocket, builder panel, drawer, wiring |
| `tools/simulator/test/helpers.mjs` + `*.test.mjs` | create | Node tests |
| `tools/simulator/README.md`, `CLAUDE.md`, `firmware/README.md`, `.github/workflows/build.yml`, `tools/simulator/test/package.json` | modify | docs and CI |

All commands below run from the worktree root, `/root/dev/volco/.claude/worktrees/simulator_new`, unless a step says otherwise. Run `cd tools/simulator/test && npm ci` once before the first Node test.

---

### Task 1: Generated assets: `fonts.js` and a UMD `icons.js`

**Files:**
- Modify: `firmware/tools/gen_assets.py`
- Generate: `tools/simulator/fonts.js`, `tools/simulator/icons.js`
- Create: `tools/simulator/test/helpers.mjs`, `tools/simulator/test/assets.test.mjs`

**Interfaces:**
- Produces: `fonts.js` exports `{ top, main }`, where each font is `{ ascent, descent, glyphs: [[codepoint, width, height, xOffset, yOffset, advance, offset], …], bitmaps: '<hex>' }` (the Volco font format of `volco_fonts.cpp`). It is `module.exports` under Node and `globalThis.VOLCO_FONTS` in the browser.
- Produces: `icons.js` exports an array of 11 icons, each 28 strings of 28 `'0'`/`'1'` characters. It is `module.exports` under Node and `globalThis.VOLCO_ICONS` in the browser, so today's page keeps working.
- Produces: `helpers.mjs` exports `repo`, `golden.frames()`, `golden.renders()` and `clock(start)`.

- [ ] **Step 1: Write the test helpers**

Create `tools/simulator/test/helpers.mjs`:

```js
// Shared helpers for the simulator's Node tests: the golden files and a fake clock.
import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const repo = path.join(here, '..', '..', '..');
const dir = path.join(repo, 'firmware', 'test', 'golden');
const lines = file => fs.readFileSync(path.join(dir, file), 'utf8').split('\n').filter(l => l.trim() && !l.startsWith('#'));

export const golden = {
  /** frames.txt: name → whole frame bytes. */
  frames: () => Object.fromEntries(lines('frames.txt').map(l => {
    const [name, ...hx] = l.trim().split(/\s+/);
    return [name, hx.map(h => parseInt(h, 16))];
  })),
  /** render.txt: [{ name, payload (Screen payload bytes), since, canvas (1024 bytes, page order) }]. */
  renders: () => lines('render.txt').map(l => {
    const [name, payload, since, canvas] = l.trim().split(/\s+/);
    const bytes = s => s.match(/../g).map(h => parseInt(h, 16));
    return { name, payload: bytes(payload), since: +since, canvas: canvas === '-' ? null : bytes(canvas) };
  }),
};

/** A manual clock: now() and advance(ms). */
export function clock(start = 1000) {
  let t = start;
  return { now: () => t, advance: ms => (t += ms) };
}
```

- [ ] **Step 2: Write the failing test**

Create `tools/simulator/test/assets.test.mjs`:

```js
// fonts.js and icons.js are generated by firmware/tools/gen_assets.py next to the firmware's C++
// tables. These tests read the committed C++ and check the simulator's copies are the same data,
// so a regeneration that skipped one side fails CI (no Pillow needed).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';
import fs from 'node:fs';
import { repo } from './helpers.mjs';

const require = createRequire(import.meta.url);
const src = path.join(repo, 'firmware', 'lib', 'volco_render', 'src');
const hexes = s => [...s.matchAll(/0x([0-9A-F]{2})\b/g)].map(m => parseInt(m[1], 16));

test('fonts.js holds the firmware glyph tables', () => {
  const cpp = fs.readFileSync(path.join(src, 'volco_fonts.cpp'), 'utf8');
  const fonts = require('../fonts.js');
  for (const [name, key] of [['kFontTop', 'top'], ['kFontMain', 'main']]) {
    const glyphs = cpp.match(new RegExp(`${name}Glyphs\\[\\] = \\{([\\s\\S]*?)\\n\\};`))[1];
    const rows = [...glyphs.matchAll(/\{(0x[0-9A-F]+), (-?\d+), (-?\d+), (-?\d+), (-?\d+), (-?\d+), (-?\d+)\}/g)].map(m => m.slice(1).map(Number));
    assert.deepEqual(fonts[key].glyphs, rows, `${key} glyphs`);
    const bitmaps = hexes(cpp.match(new RegExp(`${name}Bitmaps\\[\\] = \\{([\\s\\S]*?)\\};`))[1]);
    assert.equal(fonts[key].bitmaps, bitmaps.map(b => b.toString(16).padStart(2, '0')).join(''), `${key} bitmaps`);
    const [, ascent, descent] = cpp.match(new RegExp(`const Font ${name} = \\{${name}Glyphs, \\d+, ${name}Bitmaps, (\\d+), (\\d+)\\}`));
    assert.deepEqual([fonts[key].ascent, fonts[key].descent], [+ascent, +descent]);
  }
});

test('icons.js holds the firmware icon bitmaps', () => {
  const cpp = fs.readFileSync(path.join(src, 'volco_icons.cpp'), 'utf8');
  const icons = require('../icons.js');
  const blocks = [...cpp.matchAll(/\{ {2}\/\/ \d+ \w+\n([^}]*)\}/g)].map(m => hexes(m[1]));
  assert.equal(icons.length, blocks.length);
  icons.forEach((rows, id) => {
    const packed = rows.flatMap(r => [0, 8, 16, 24].map(b => [...r.slice(b, b + 8)].reduce((v, bit, i) => v | (bit === '1' ? 1 << i : 0), 0)));
    assert.deepEqual(packed, blocks[id], `icon ${id}`);
  });
});
```

- [ ] **Step 3: Run it to see it fail**

Run: `cd tools/simulator/test && node --test assets.test.mjs`
Expected: FAIL. `fonts.js` doesn't exist ("Cannot find module '../fonts.js'"), and `icons.js` sets `window.VOLCO_ICONS`, which throws `ReferenceError: window is not defined` under Node.

- [ ] **Step 4: Teach `gen_assets.py` to write both files**

Apply these exact replacements to `firmware/tools/gen_assets.py`. Each left-hand string appears once. The script below applies them for you:

```python
import sys
p = sys.argv[1]
s = open(p).read()
R = [
("""  tools/simulator/icons.js                            icon bitmaps for the web simulator
""", """  tools/simulator/icons.js                            icon bitmaps for the web simulator
  tools/simulator/fonts.js                            the same two fonts for the web simulator
"""),
("""    c = [HEADER, '#include "volco_fonts.h"\\n\\nnamespace volco {\\n']
    for name, file, size in FONTS:""", """    c = [HEADER, '#include "volco_fonts.h"\\n\\nnamespace volco {\\n']
    sim = {}
    for name, file, size in FONTS:"""),
("""        ascent, descent = font.getmetrics()
""", """        ascent, descent = font.getmetrics()
        sim["top" if name == "kFontTop" else "main"] = (glyphs, bitmap, ascent, descent)
"""),
("""    (RENDER / "volco_fonts.cpp").write_text("".join(c))
""", """    (RENDER / "volco_fonts.cpp").write_text("".join(c))
    return sim
"""),
("""def write_simulator(rows):
    js = ["// Generated by firmware/tools/gen_assets.py. Do not edit.", "window.VOLCO_ICONS = ["]
    for id_, stem, _, _ in rows:
        js.append(f"  [ // {id_} {stem}")
        js += [f'    "{"".join(map(str, r))}",' for r in icon_rows(stem)]
        js.append("  ],")
    js += ["];", ""]
    (ROOT / "tools" / "simulator" / "icons.js").write_text("\\n".join(js))
""", """# The simulator's copies are UMD scripts: a global in the browser, module.exports under Node.
UMD_HEAD = ["// Generated by firmware/tools/gen_assets.py. Do not edit.", "(function (root) {"]

def umd_tail(var, glob):
    return [f"  if (typeof module === 'object' && module.exports) module.exports = {var};",
            f"  else root.{glob} = {var};", "})(typeof globalThis !== 'undefined' ? globalThis : this);", ""]

def write_simulator(rows, fonts):
    sim = ROOT / "tools" / "simulator"
    js = UMD_HEAD + ["  const icons = ["]
    for id_, stem, _, _ in rows:
        js.append(f"    [ // {id_} {stem}")
        js += [f'      "{"".join(map(str, r))}",' for r in icon_rows(stem)]
        js.append("    ],")
    js += ["  ];"] + umd_tail("icons", "VOLCO_ICONS")
    (sim / "icons.js").write_text("\\n".join(js))
    js = UMD_HEAD + ["  const fonts = {"]
    for key, (glyphs, bitmap, ascent, descent) in fonts.items():
        js.append(f"    {key}: {{ ascent: {ascent}, descent: {descent},")
        js.append("      glyphs: [" + ",".join("[" + ",".join(map(str, g)) + "]" for g in glyphs) + "],")
        js.append("      bitmaps: '" + "".join(f"{b:02x}" for b in bitmap) + "' },")
    js += ["  };"] + umd_tail("fonts", "VOLCO_FONTS")
    (sim / "fonts.js").write_text("\\n".join(js))
"""),
("""    write_icons(rows); write_fonts(); write_csharp(rows); write_simulator(rows)""",
 """    write_icons(rows); fonts = write_fonts(); write_csharp(rows); write_simulator(rows, fonts)"""),
]
for a, b in R:
    assert a in s, a
    s = s.replace(a, b)
open(p, 'w').write(s)
print('patched')
```

Save it as a scratch file (for example in the session scratchpad), then run `python3 <scratch>/gen_assets_patch.py firmware/tools/gen_assets.py`. Expected output: `patched`.

- [ ] **Step 5: Regenerate the assets**

Run: `python3 firmware/tools/gen_assets.py`
Expected: `assets generated`. Then run `/usr/bin/git status --short` and expect only `firmware/tools/gen_assets.py`, `tools/simulator/icons.js` and the new `tools/simulator/fonts.js`. If `volco_fonts.cpp`, `volco_icons.cpp` or `ScreenIcon.g.cs` changed, your Pillow renders differently from the one that made the committed files: run `/usr/bin/git checkout -- firmware/lib src/Volco.Core` and stop to report it. This plan was checked with Pillow 10.2.0, which reproduces them byte for byte.

- [ ] **Step 6: Run the test to see it pass**

Run: `cd tools/simulator/test && node --test assets.test.mjs`
Expected: PASS, 2 tests.

- [ ] **Step 7: Commit**

```bash
/usr/bin/git add firmware/tools/gen_assets.py tools/simulator/fonts.js tools/simulator/icons.js tools/simulator/test/helpers.mjs tools/simulator/test/assets.test.mjs
/usr/bin/git commit -m "Generate the simulator's fonts.js and a UMD icons.js"
```

---

### Task 2: `protocol.js`

**Files:**
- Create: `tools/simulator/protocol.js`
- Test: `tools/simulator/test/protocol.test.mjs`

**Interfaces:**
- Produces: `VolcoProtocol` (Node: `require('../protocol.js')`) with:
  - constants `MSG`, `NAMES`, `KIND {NONE, LED, LED_PAIR, SCREEN}`, `TRANSITION {NONE, SLIDE_LEFT, SLIDE_RIGHT}`, `TOP {EMPTY, TEXT, DOTS}`, `NO_ICON = 0xFF`, `MAX_TEXT = 60`, `MAX_BUTTONS = 32`;
  - framing: `frame(payload: number[]) → Uint8Array` and `createParser(onPayload(payload), onBad(payload)) → { feed(bytes) }`;
  - controller frames: `hello({chip=1, firmware='sim', configState=0, configCrc=0, sliders, kinds}) → number[]`, `alive()`, `slider(i, value)`, `button(i, pressed)`;
  - screens: `screenText(top, icon, main) → content`, `screen(button, transition, content) → number[]`, `decodeScreen(payload) → {button, transition, content} | null`. A `content` is `{layout, topKind, top, dotsSelected, dotsCount, icon, flags, main}`;
  - formatting: `hex(bytes) → 'aa bb'` and `nameOf(type) → 'Hello'`.

- [ ] **Step 1: Write the failing test**

Create `tools/simulator/test/protocol.test.mjs`:

```js
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
```

- [ ] **Step 2: Run it to see it fail**

Run: `cd tools/simulator/test && node --test protocol.test.mjs`
Expected: FAIL with "Cannot find module '../protocol.js'".

- [ ] **Step 3: Implement**

Create `tools/simulator/protocol.js`:

```js
// The Volco wire protocol, controller side (firmware/lib/volco_protocol): framing, a streaming
// parser, the frames the controller sends and the Led/Screen decoders. No DOM: index.html loads
// it as a plain script (globalThis.VolcoProtocol), the Node tests with require().
(function (root) {
  'use strict';

  const MSG = {
    HELLO: 0x01, ALIVE: 0x02, SLIDER: 0x03, BUTTON: 0x04, CONFIG_DATA: 0x05, CONFIG_RESULT: 0x06,
    HELLO_REQUEST: 0x81, HEARTBEAT: 0x82, LED: 0x83, SCREEN: 0x84, BEGIN: 0x85, COMMIT: 0x86,
    CONFIG_READ: 0x87, CONFIG_BEGIN: 0x88, CONFIG_CHUNK: 0x89, CONFIG_END: 0x8A,
  };
  const NAMES = {
    0x01: 'Hello', 0x02: 'Alive', 0x03: 'Slider', 0x04: 'Button', 0x05: 'ConfigData', 0x06: 'ConfigResult',
    0x81: 'HelloRequest', 0x82: 'Heartbeat', 0x83: 'Led', 0x84: 'Screen', 0x85: 'Begin', 0x86: 'Commit',
    0x87: 'ConfigRead', 0x88: 'ConfigWriteBegin', 0x89: 'ConfigWriteChunk', 0x8A: 'ConfigWriteEnd',
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

  const api = { MSG, NAMES, KIND, TRANSITION, TOP, NO_ICON, MAX_TEXT, MAX_BUTTONS,
    frame, createParser, hello, alive, slider, button, screenText, screen, decodeScreen, hex, nameOf };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoProtocol = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
```

- [ ] **Step 4: Run the test to see it pass**

Run: `cd tools/simulator/test && node --test protocol.test.mjs`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git add tools/simulator/protocol.js tools/simulator/test/protocol.test.mjs
/usr/bin/git commit -m "Add the simulator's protocol module, checked against the golden frames"
```

---

### Task 3: `link.js`

**Files:**
- Create: `tools/simulator/link.js`
- Test: `tools/simulator/test/link.test.mjs`

**Interfaces:**
- Consumes: `protocol.js` (Task 2).
- Produces: `VolcoLink.createLink(o)`.
  - **Callbacks it takes:**
    - `o.sendFrame(bytes, payload, bad)`, `o.sliderValue(i) → 0..4095`;
    - `o.onLed(b, v)`, `o.onScreen(b, transition, content, now)`, `o.onHostLost(now)`;
    - `o.onConfigFrame(payload, now) → bool`, optional.
  - **What it returns:**
    - `setHardware({sliders, kinds, configState, configCrc})`, `hardware()`;
    - `begin(now)`, `onPayload(payload, now)`, `tick(now)`;
    - `sendSlider(i, v, now)`, `sendButton(i, pressed, now)`, `sendPayload(payload, now)`;
    - the fault hooks `faults { silent, ignoreCommit }`, `corruptNext()`, `corruptArmed()`;
    - state: `hostAlive()`, `holding()`.
  - Also exports `TIMING { hostTimeoutMs: 3000, keepaliveMs: 1000, commitFallbackMs: 500 }`.

- [ ] **Step 1: Write the failing test**

Create `tools/simulator/test/link.test.mjs`:

```js
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
```

- [ ] **Step 2: Run it to see it fail**

Run: `cd tools/simulator/test && node --test link.test.mjs`
Expected: FAIL with "Cannot find module '../link.js'".

- [ ] **Step 3: Implement**

Create `tools/simulator/link.js`:

```js
// The controller side of a session, mirroring firmware/lib/volco_protocol's Link (spec §3.4):
// Hello, keepalive, host timeout, Begin/Commit, plus the simulator's fault hooks. Time is passed
// in. No DOM: index.html loads it as a plain script (globalThis.VolcoLink), the tests with require().
(function (root) {
  'use strict';
  const P = typeof module === 'object' && module.exports ? require('./protocol.js') : root.VolcoProtocol;

  const TIMING = { hostTimeoutMs: 3000, keepaliveMs: 1000, commitFallbackMs: 500 };

  /**
   * @param {object} o
   * @param {(bytes:Uint8Array, payload:number[], bad:boolean) => void} o.sendFrame  writes one frame
   * @param {(index:number) => number} o.sliderValue        a slider's position, 0..4095
   * @param {(button:number, value:number) => void} o.onLed
   * @param {(button:number, transition:number, content:object, now:number) => void} o.onScreen
   * @param {(now:number) => void} o.onHostLost            LEDs off, "not connected" screens
   * @param {(payload:number[], now:number) => boolean} [o.onConfigFrame]  true when it took the frame
   */
  function createLink(o) {
    let hw = { sliders: 0, kinds: [], configState: 0, configCrc: 0, firmware: 'sim' };
    let hostAlive = false, lastRx = 0, lastTx = -Infinity, holding = false, holdStart = 0, held = [];
    let corrupt = false;
    const faults = { silent: false, ignoreCommit: false };

    const isLed = b => b < hw.kinds.length && (hw.kinds[b] === P.KIND.LED || hw.kinds[b] === P.KIND.LED_PAIR);
    const isScreen = b => b < hw.kinds.length && hw.kinds[b] === P.KIND.SCREEN;

    function sendPayload(payload, now) {
      if (faults.silent) return;
      const bytes = P.frame(payload);
      const bad = corrupt;
      if (bad) { bytes[bytes.length - 1] ^= 0xFF; corrupt = false; }
      o.sendFrame(bytes, payload, bad);
      lastTx = now;
    }
    function sendHello(now) {
      sendPayload(P.hello(hw), now);
      for (let i = 0; i < hw.sliders; i++) sendPayload(P.slider(i, o.sliderValue(i)), now);
    }
    function applyHeld(now) {
      holding = false;
      held.forEach((h, b) => {
        if (!h) return;
        if (h.led !== undefined) o.onLed(b, h.led);
        if (h.screen) o.onScreen(b, h.transition, h.screen, now);
      });
      held = [];
    }

    return {
      faults,
      /** The hardware Hello reports and the kinds Led/Screen are filtered by. */
      setHardware(h) { hw = { firmware: 'sim', ...h }; held = []; },
      hardware: () => hw,
      /** Boot: Hello, as the firmware sends it when it starts. */
      begin(now) { lastRx = now; hostAlive = false; holding = false; held = []; sendHello(now); },
      onPayload(p, now) {
        if (faults.silent || p.length < 1) return;
        lastRx = now;
        const type = p[0];
        if (!hostAlive) {
          hostAlive = true;
          if (type !== P.MSG.HELLO_REQUEST) sendHello(now);   // the PC is back: announce the hardware
        }
        if (o.onConfigFrame && o.onConfigFrame(p, now)) return;
        switch (type) {
          case P.MSG.HELLO_REQUEST: sendHello(now); break;
          case P.MSG.LED:
            if (p.length >= 3 && isLed(p[1])) {
              if (holding) held[p[1]] = { ...held[p[1]], led: p[2] };
              else o.onLed(p[1], p[2]);
            }
            break;
          case P.MSG.SCREEN: {
            const s = P.decodeScreen(p);
            if (!s || !isScreen(s.button)) break;
            if (holding) {
              const h = held[s.button] || {};
              // A newer instant update keeps a held slide (spec §2.3).
              const transition = h.screen && s.transition === P.TRANSITION.NONE ? h.transition : s.transition;
              held[s.button] = { ...h, screen: s.content, transition };
            } else o.onScreen(s.button, s.transition, s.content, now);
            break;
          }
          case P.MSG.BEGIN: holding = true; holdStart = now; break;
          case P.MSG.COMMIT: if (holding && !faults.ignoreCommit) applyHeld(now); break;
          default: break;   // Heartbeat (its arrival is the point) and unknown types
        }
      },
      tick(now) {
        if (holding && now - holdStart >= TIMING.commitFallbackMs) applyHeld(now);
        if (hostAlive && now - lastRx >= TIMING.hostTimeoutMs) {
          hostAlive = false; holding = false; held = [];
          o.onHostLost(now);
        }
        if (now - lastTx >= TIMING.keepaliveMs) sendPayload(P.alive(), now);
      },
      sendSlider(index, value, now) { sendPayload(P.slider(index, value), now); },
      sendButton(index, pressed, now) { sendPayload(P.button(index, pressed), now); },
      /** Any other payload (the config session's replies). */
      sendPayload,
      /** One-shot: the next frame sent goes out with its checksum XORed with 0xFF. */
      corruptNext() { corrupt = true; },
      corruptArmed: () => corrupt,
      hostAlive: () => hostAlive,
      holding: () => holding,
    };
  }

  const api = { createLink, TIMING };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoLink = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
```

- [ ] **Step 4: Run the test to see it pass**

Run: `cd tools/simulator/test && node --test link.test.mjs`
Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git add tools/simulator/link.js tools/simulator/test/link.test.mjs
/usr/bin/git commit -m "Add the simulator's link, mirroring the firmware Link, with fault hooks"
```

---

### Task 4: Golden layout-1 renders from the firmware

**Files:**
- Create: `firmware/test/golden_render.h`, `firmware/test/golden/render.txt`
- Modify: `firmware/test/test_render/test_main.cpp`

**Interfaces:**
- Produces: `firmware/test/golden/render.txt`. Each line is `<name> <Screen payload hex> <scroll ms> <canvas>`, where the canvas is 1024 bytes of hex in SSD1306 page order (`buf[page * 128 + x]`, bit 0 at the top), as `volco::Canvas` stores it. `golden.renders()` in `helpers.mjs` (Task 1) reads it.

- [ ] **Step 1: Write the golden loader**

Create `firmware/test/golden_render.h`:

```cpp
#pragma once
// Loads and rewrites firmware/test/golden/render.txt, the layout-1 renders shared with
// tools/simulator/test/oled.test.mjs: <name> <Screen payload hex> <scroll ms> <canvas hex or ->.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

struct GoldenRender {
  std::string name;
  std::vector<uint8_t> payload;
  uint32_t since = 0;
  std::vector<uint8_t> canvas;   // empty until generated
};

inline std::string goldenRenderPath() {
  std::string here = __FILE__;   // the test binary's working directory varies
  return here.substr(0, here.find_last_of("/\\") + 1) + "golden/render.txt";
}

inline std::vector<uint8_t> goldenHex(const char* s) {
  std::vector<uint8_t> out;
  if (strcmp(s, "-") == 0) return out;
  for (size_t i = 0; s[i] && s[i + 1]; i += 2) {
    char b[3] = {s[i], s[i + 1], 0};
    out.push_back((uint8_t)strtoul(b, nullptr, 16));
  }
  return out;
}

// header: the comment lines, kept for writing back.
inline bool loadGoldenRenders(std::vector<std::string>& header, std::vector<GoldenRender>& out) {
  FILE* f = fopen(goldenRenderPath().c_str(), "r");
  if (!f) return false;
  static char line[4096];
  while (fgets(line, sizeof line, f)) {
    if (line[0] == '#') { header.push_back(line); continue; }
    char* name = strtok(line, " \t\r\n");
    char* payload = strtok(nullptr, " \t\r\n");
    char* since = strtok(nullptr, " \t\r\n");
    char* canvas = strtok(nullptr, " \t\r\n");
    if (!name || !payload || !since || !canvas) continue;
    GoldenRender g;
    g.name = name;
    g.payload = goldenHex(payload);
    g.since = (uint32_t)strtoul(since, nullptr, 10);
    g.canvas = goldenHex(canvas);
    out.push_back(g);
  }
  fclose(f);
  return true;
}

inline bool writeGoldenRenders(const std::vector<std::string>& header, const std::vector<GoldenRender>& cases) {
  FILE* f = fopen(goldenRenderPath().c_str(), "w");
  if (!f) return false;
  for (const std::string& h : header) fputs(h.c_str(), f);
  for (const GoldenRender& g : cases) {
    fprintf(f, "%s ", g.name.c_str());
    for (uint8_t b : g.payload) fprintf(f, "%02X", b);
    fprintf(f, " %u ", (unsigned)g.since);
    if (g.canvas.empty()) fputc('-', f);
    for (uint8_t b : g.canvas) fprintf(f, "%02X", b);
    fputc('\n', f);
  }
  fclose(f);
  return true;
}
```

- [ ] **Step 2: Write the cases, with canvases not yet generated**

Create `firmware/test/golden/render.txt` with exactly these lines. Each payload is a Screen message for button 0; `-` means "not generated yet":

```text
# Golden layout-1 renders shared by firmware/test/test_render and tools/simulator/test/oled.test.mjs.
# Format: <name> <Screen payload hex> <scroll ms> <canvas: 1024 bytes hex, SSD1306 page order, or - before generation>.
# Regenerate the canvases: cd firmware && VOLCO_WRITE_GOLDEN=1 pio test -e native -f test_render
latin 8400000101064F5554505554020008537065616B657273 0 -
muted_mic 8400000101054D5554454407010A4D6963726F70686F6E65 0 -
dots 840000010201030A000647616D696E67 0 -
dots_clamped 84000001020B0EFF00044D616E79 0 -
hebrew 84000001010E4E4558543A20537065616B65727301000ED7A8D79ED7A7D795D79CD799D79D 0 -
hebrew_mixed 8400000100030016D790D795D796D7A0D799D795D7AA2032202855534229 0 -
marquee_start 84000001000100265265616C74656B204869676820446566696E6974696F6E20417564696F20537065616B657273 0 -
marquee_mid 84000001000100265265616C74656B204869676820446566696E6974696F6E20417564696F20537065616B657273 3000 -
marquee_end 84000001000100265265616C74656B204869676820446566696E6974696F6E20417564696F20537065616B657273 14000 -
marquee_rtl_mid 840000010003003AD790D795D796D7A0D799D795D7AA20D790D79CD797D795D798D799D795D7AA20D7A9D79C20D794D79ED797D7A9D79120D791D7A1D79CD795D79F 3500 -
top_marquee 8400000101234E4558543A205265616C74656B204869676820446566696E6974696F6E20417564696F0200044465736B 2500 -
unknown_icon 8400000100C8000158 0 -
unknown_layout 840000020103544F500100024869 0 -
not_connected 84000001010D4E4F5420434F4E4E4543544544FF0005566F6C636F 0 -
blank 8400000100FF0000 0 -
latin1 840000010105434146C38900000E5365C3B16F72204DC3BC6C6C6572 0 -
```

- [ ] **Step 3: Add the failing test**

Edit `firmware/test/test_render/test_main.cpp`:

1. After `#include "volco_render.h"`, add `#include <stdlib.h>` and `#include "../golden_render.h"` above it, so the include block reads:
   ```cpp
   #include <unity.h>
   #include <string.h>
   #include <stdlib.h>
   #include "../golden_render.h"
   #include "volco_render.h"
   ```
2. Add this test above `int main(int, char**) {`:
   ```cpp
   // The renders shared with the web simulator (firmware/test/golden/render.txt). With
   // VOLCO_WRITE_GOLDEN set, the canvases are rewritten from this renderer instead of checked.
   void test_golden_renders() {
     std::vector<std::string> header;
     std::vector<GoldenRender> cases;
     TEST_ASSERT_TRUE_MESSAGE(loadGoldenRenders(header, cases), "golden/render.txt not found");
     TEST_ASSERT_TRUE(cases.size() >= 16);
     const bool write = getenv("VOLCO_WRITE_GOLDEN") != nullptr;
     for (GoldenRender& g : cases) {
       uint8_t button, transition;
       ScreenContent s;
       TEST_ASSERT_TRUE_MESSAGE(decodeScreen(g.payload.data(), (uint8_t)g.payload.size(), button, transition, s), g.name.c_str());
       Canvas c;
       renderScreen(c, s, g.since, T);
       if (write) { g.canvas.assign(c.buf, c.buf + sizeof c.buf); continue; }
       TEST_ASSERT_EQUAL_MESSAGE(sizeof c.buf, g.canvas.size(), g.name.c_str());
       TEST_ASSERT_EQUAL_HEX8_ARRAY_MESSAGE(g.canvas.data(), c.buf, sizeof c.buf, g.name.c_str());
     }
     if (write) TEST_ASSERT_TRUE(writeGoldenRenders(header, cases));
   }
   ```
3. In `main`, after `RUN_TEST(test_not_connected_screen);`, add `RUN_TEST(test_golden_renders);`.

`T` is the file's existing `static const ScrollTiming T = {2000, 1200, 30};`, which is the proto defaults.

- [ ] **Step 4: Run it to see it fail**

Run: `cd firmware && pio test -e native -f test_render`
Expected: FAIL in `test_golden_renders` with "Expected 1024 Was 0" (the canvases are `-`).

- [ ] **Step 5: Generate the canvases and run again**

Run: `cd firmware && VOLCO_WRITE_GOLDEN=1 pio test -e native -f test_render && pio test -e native -f test_render`
Expected: both PASS (12 test cases). Every line of `render.txt` now ends in 2048 hex digits.

- [ ] **Step 6: Run every native test**

Run: `cd firmware && pio test -e native`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
/usr/bin/git add firmware/test/golden_render.h firmware/test/golden/render.txt firmware/test/test_render/test_main.cpp
/usr/bin/git commit -m "Add golden layout-1 renders shared with the simulator"
```

---

### Task 5: `oled.js`

**Files:**
- Create: `tools/simulator/oled.js`
- Test: `tools/simulator/test/oled.test.mjs`

**Interfaces:**
- Consumes: `fonts.js` and `icons.js` (Task 1), `protocol.js` (Task 2), and `render.txt` (Task 4).
- Produces: `VolcoOled`.
  - **Constants:** `W = 128`, `H = 64`, `NOT_CONNECTED`, `POWER {NORMAL, DIM, OFF}`, `SCROLL {holdMs: 2000, endHoldMs: 1200, speedPxS: 30}`.
  - **Canvas:** `canvas() → Uint8Array(1024)`, `get(c, x, y)`, `set(c, x, y, on)`.
  - **Rendering:** `renderScreen(c, content, since, timing = SCROLL)`, `scrollOffset(distance, since, timing)`, `bidiVisual(text) → {cps, rtl}`, `easeInOut(p)`, `composeSlide(from, to, progress, left, out)`, `shiftFrame(input, dx, dy, out)`.
  - **Slots and panels:**
    - `createSlot(transitionMs)`, with `show(content, transition, now, panelsOff)`, `current()`, `previous()`, `transition()`, `inTransition(now)`, `progress(now)`, `scrollSince(now)`;
    - `createPanels({dim, off, shift, swallow, dimAfterMs, offAfterMs, shiftEveryMs})`, with `activity(now)`, `power(now)`, `shift(now) → [dx, dy]`, `filterButton(b, pressed, now) → bool`.
  - **Composition:** `frame(slot, panels|null, now, timing) → Uint8Array` (what `firmware/src/screens.cpp`'s `render()` shows), and `behaviour(screenBehaviour|null) → {transitionMs, scroll, panels, brightness, dimBrightness}`.

- [ ] **Step 1: Write the failing test**

Create `tools/simulator/test/oled.test.mjs`:

```js
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
```

- [ ] **Step 2: Run it to see it fail**

Run: `cd tools/simulator/test && node --test oled.test.mjs`
Expected: FAIL with "Cannot find module '../oled.js'".

- [ ] **Step 3: Implement**

Create `tools/simulator/oled.js`:

```js
// The controller's screens, ported from firmware/lib/volco_render (layout 1, slide, pixel shift),
// volco_bidi (visual order) and volco_screen_state (slots and panel power), so a simulated screen
// is pixel-identical to the hardware. Time is passed in. No DOM: index.html loads it as a plain
// script (globalThis.VolcoOled), the Node tests with require().
(function (root) {
  'use strict';
  const node = typeof module === 'object' && module.exports;
  const FONTS = node ? require('./fonts.js') : root.VOLCO_FONTS;
  const ICONS = node ? require('./icons.js') : root.VOLCO_ICONS;
  const P = node ? require('./protocol.js') : root.VolcoProtocol;

  const W = 128, H = 64;
  const TEXT_X = 2, TEXT_W = 124, TOP_BASELINE = 11, MAIN_BASELINE = 60, ICON_X = 50, ICON_Y = 18;

  // ── Canvas: 128×64, 1 bit, SSD1306 page order (buf[page * 128 + x], bit 0 = top row) ──────
  function canvas() { return new Uint8Array(W * H / 8); }
  function set(c, x, y, on = true) {
    if (x < 0 || y < 0 || x >= W || y >= H) return;
    const i = (y >> 3) * W + x, bit = 1 << (y & 7);
    if (on) c[i] |= bit; else c[i] &= ~bit;
  }
  function get(c, x, y) { return x >= 0 && y >= 0 && x < W && y < H && (c[(y >> 3) * W + x] >> (y & 7)) & 1; }
  function fillRect(c, x, y, w, h, on = true) {
    for (let yy = y; yy < y + h; yy++) for (let xx = x; xx < x + w; xx++) set(c, xx, yy, on);
  }
  function rect(c, x, y, w, h) {
    for (let i = 0; i < w; i++) { set(c, x + i, y); set(c, x + i, y + h - 1); }
    for (let i = 0; i < h; i++) { set(c, x, y + i); set(c, x + w - 1, y + i); }
  }
  function line(c, x0, y0, x1, y1, thickness, on = true) {
    const dx = Math.abs(x1 - x0), sx = x0 < x1 ? 1 : -1, dy = -Math.abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
    let err = dx + dy;
    const half = Math.trunc(thickness / 2);
    for (;;) {
      fillRect(c, x0 - half, y0 - half, thickness, thickness, on);
      if (x0 === x1 && y0 === y1) break;
      const e2 = 2 * err;
      if (e2 >= dy) { err += dy; x0 += sx; }
      if (e2 <= dx) { err += dx; y0 += sy; }
    }
  }

  // ── Fonts (fonts.js: the firmware's glyph tables) ─────────────────────────────────────────
  const fonts = {};
  for (const key of ['top', 'main']) {
    const f = FONTS[key], bits = new Uint8Array(f.bitmaps.length / 2);
    for (let i = 0; i < bits.length; i++) bits[i] = parseInt(f.bitmaps.substr(i * 2, 2), 16);
    const map = new Map(f.glyphs.map(([cp, w, h, xo, yo, adv, off]) => [cp, { w, h, xo, yo, adv, off }]));
    fonts[key] = { map, bits };
  }
  const textWidth = (font, cps) => cps.reduce((w, cp) => w + (font.map.get(cp)?.adv ?? 0), 0);
  function drawText(c, font, x, baseline, cps, clipX0, clipX1) {
    for (const cp of cps) {
      const g = font.map.get(cp);
      if (!g) continue;   // missing glyphs draw nothing and take no space
      const rowBytes = (g.w + 7) >> 3;
      for (let row = 0; row < g.h; row++)
        for (let col = 0; col < g.w; col++) {
          const px = x + g.xo + col;
          if (px < clipX0 || px >= clipX1) continue;
          if (font.bits[g.off + row * rowBytes + (col >> 3)] & (1 << (col & 7))) set(c, px, baseline + g.yo + row);
        }
      x += g.adv;
    }
  }

  // ── Bidi (volco_bidi): logical → visual order, a small subset of the Unicode algorithm ────
  const L = 0, R = 1, EN = 2, CS = 3, N = 4;
  function classify(cp) {
    if ((cp >= 0x5D0 && cp <= 0x5EA) || (cp >= 0x5F0 && cp <= 0x5F4)) return R;
    if (cp >= 0x30 && cp <= 0x39) return EN;
    if (cp === 0x2E || cp === 0x2C || cp === 0x3A || cp === 0x2F) return CS;
    if ((cp >= 0x41 && cp <= 0x5A) || (cp >= 0x61 && cp <= 0x7A)) return L;
    if (cp >= 0xC0 && cp <= 0x24F && cp !== 0xD7 && cp !== 0xF7) return L;
    if (cp >= 0x370 && cp < 0x590) return L;
    return N;
  }
  const MIRROR = { 0x28: 0x29, 0x29: 0x28, 0x5B: 0x5D, 0x5D: 0x5B, 0x7B: 0x7D, 0x7D: 0x7B, 0x3C: 0x3E, 0x3E: 0x3C };
  /** { cps: code points in visual order, rtl: paragraph direction }. At most 128 code points. */
  function bidiVisual(text) {
    const cp = Array.from(text, ch => ch.codePointAt(0)).slice(0, 128), n = cp.length;
    const cls = cp.map(classify);
    for (let i = 1; i + 1 < n; i++) if (cls[i] === CS && cls[i - 1] === EN && cls[i + 1] === EN) cls[i] = EN;
    let paraRtl = false;
    for (let i = 0; i < n; i++) if (cls[i] === L || cls[i] === R) { paraRtl = cls[i] === R; break; }
    const dir = new Array(n).fill(false);
    let lastStrongRtl = paraRtl;
    for (let i = 0; i < n; i++) {
      if (cls[i] === L || cls[i] === R) lastStrongRtl = cls[i] === R;
      if (cls[i] === L) dir[i] = false;
      else if (cls[i] === R) dir[i] = true;
      else if (cls[i] === EN) dir[i] = lastStrongRtl;
    }
    for (let i = 0; i < n; i++) {
      if (cls[i] !== N && cls[i] !== CS) continue;
      let j = i;
      while (j < n && (cls[j] === N || cls[j] === CS)) j++;
      const before = i > 0 ? dir[i - 1] : paraRtl, after = j < n ? dir[j] : paraRtl;
      const d = before === after ? before : paraRtl;
      for (let k = i; k < j; k++) dir[k] = d;
      i = j - 1;
    }
    const runs = [];
    for (let i = 0; i < n;) { let j = i; while (j < n && dir[j] === dir[i]) j++; runs.push([i, j]); i = j; }
    const out = [];
    for (let r = 0; r < runs.length; r++) {
      const [s, e] = runs[paraRtl ? runs.length - 1 - r : r];
      if (!dir[s]) { for (let i = s; i < e; i++) out.push(cp[i]); continue; }
      for (let i = e - 1; i >= s;) {
        if (cls[i] === EN) {
          let k = i;
          while (k - 1 >= s && cls[k - 1] === EN) k--;
          for (let m = k; m <= i; m++) out.push(cp[m]);
          i = k - 1;
        } else { out.push(MIRROR[cp[i]] ?? cp[i]); i--; }
      }
    }
    return { cps: out, rtl: paraRtl };
  }

  // ── Layout 1 (volco_render) ───────────────────────────────────────────────────────────────
  /** Default scroll timing; the stored config's ScreenBehaviour overrides it. */
  const SCROLL = { holdMs: 2000, endHoldMs: 1200, speedPxS: 30 };
  function scrollOffset(distance, since, t) {
    if (distance <= 0 || t.speedPxS === 0) return 0;
    const scrollMs = Math.floor(distance * 1000 / t.speedPxS);
    const k = since % (t.holdMs + scrollMs + t.endHoldMs);
    if (k < t.holdMs) return 0;
    if (k < t.holdMs + scrollMs) return Math.floor((k - t.holdMs) * t.speedPxS / 1000);
    return distance;
  }
  function drawLine(c, font, text, baseline, since, t) {
    if (!text) return;
    const { cps, rtl } = bidiVisual(text);
    const w = textWidth(font, cps);
    if (w <= TEXT_W) { drawText(c, font, TEXT_X + Math.trunc((TEXT_W - w) / 2), baseline, cps, 0, W); return; }
    const off = scrollOffset(w - TEXT_W + 6, since, t);
    drawText(c, font, rtl ? TEXT_X + TEXT_W - w + off : TEXT_X - off, baseline, cps, TEXT_X, TEXT_X + TEXT_W);
  }
  function drawDots(c, selected, count) {
    if (count === 0) return;
    count = Math.min(count, 12);
    const x0 = 64 - Math.trunc((count * 10 - 4) / 2);
    for (let i = 0; i < count; i++) {
      if (i === selected) fillRect(c, x0 + i * 10, 5, 6, 6); else rect(c, x0 + i * 10, 5, 6, 6);
    }
  }
  function drawIcon(c, id) {
    const rows = ICONS[id < ICONS.length ? id : 0];
    rows.forEach((row, y) => { for (let x = 0; x < row.length; x++) if (row[x] === '1') set(c, ICON_X + x, ICON_Y + y); });
  }
  /** Clears c and draws one screen. since drives scrolling (ScreenSlot.scrollSince). */
  function renderScreen(c, s, since, t = SCROLL) {
    c.fill(0);
    if (s.layout !== 1) { drawLine(c, fonts.main, s.main, 36, since, t); return; }   // unknown layout: main text only
    if (s.topKind === P.TOP.TEXT) drawLine(c, fonts.top, s.top, TOP_BASELINE, since, t);
    else if (s.topKind === P.TOP.DOTS) drawDots(c, s.dotsSelected, s.dotsCount);
    if (s.icon !== P.NO_ICON) {
      drawIcon(c, s.icon);
      if (s.flags & 1) {   // muted: a slash with a dark outline so it reads over filled shapes
        line(c, ICON_X + 1, ICON_Y + 1, ICON_X + 26, ICON_Y + 26, 5, false);
        line(c, ICON_X + 1, ICON_Y + 1, ICON_X + 26, ICON_Y + 26, 2, true);
      }
    }
    drawLine(c, fonts.main, s.main, MAIN_BASELINE, since, t);
  }
  const NOT_CONNECTED = P.screenText('NOT CONNECTED', P.NO_ICON, 'Volco');

  const easeInOut = p => p <= 0 ? 0 : p >= 1 ? 1 : p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2;
  /** left: new content enters from the right (slide left); otherwise from the left. */
  function composeSlide(from, to, progress, left, out) {
    const off = Math.round(easeInOut(progress) * W);
    for (let x = 0; x < W; x++) {
      const sx = left ? x + off : x - off;
      const src = left ? (sx < W ? from : to) : (sx >= 0 ? from : to);
      const col = left ? (sx < W ? sx : sx - W) : (sx >= 0 ? sx : sx + W);
      for (let page = 0; page < 8; page++) out[page * W + x] = src[page * W + col];
    }
  }
  function shiftFrame(input, dx, dy, out) {
    for (let x = 0; x < W; x++) {
      const sx = x - dx;
      let column = 0n;
      if (sx >= 0 && sx < W) for (let page = 0; page < 8; page++) column |= BigInt(input[page * W + sx]) << BigInt(8 * page);
      column = dy >= 0 ? (column << BigInt(dy)) & 0xFFFFFFFFFFFFFFFFn : column >> BigInt(-dy);
      for (let page = 0; page < 8; page++) out[page * W + x] = Number((column >> BigInt(8 * page)) & 0xFFn);
    }
  }

  // ── Slots and panel power (volco_screen_state) ────────────────────────────────────────────
  function createSlot(transitionMs) {
    let current = NOT_CONNECTED, previous = NOT_CONNECTED, transition = 0, start = 0, settled = 0;
    const inTransition = now => transition !== 0 && now - start < transitionMs;
    return {
      /** New content; a running slide snaps to its end first. While the panels are off nothing animates. */
      show(c, t, now, panelsOff) {
        previous = current; current = c;
        transition = panelsOff ? 0 : t;
        start = now;
        settled = transition === 0 ? now : now + transitionMs;
      },
      current: () => current, previous: () => previous, transition: () => transition,
      inTransition,
      progress: now => inTransition(now) ? (now - start) / transitionMs : 1,
      scrollSince: now => now < settled ? 0 : now - settled,
    };
  }
  const POWER = { NORMAL: 0, DIM: 1, OFF: 2 };
  const SHIFT_CYCLE = [[0, 0], [1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1], [1, -1]];
  /** cfg: { dim, off, shift, swallow, dimAfterMs, offAfterMs, shiftEveryMs } */
  function createPanels(cfg) {
    let last = 0, swallowed = 0;
    const power = now => {
      const idle = now - last;
      if (cfg.off && idle >= cfg.offAfterMs) return POWER.OFF;
      if (cfg.dim && idle >= cfg.dimAfterMs) return POWER.DIM;
      return POWER.NORMAL;
    };
    return {
      activity(now) { last = now; },
      power,
      shift(now) { return !cfg.shift || !cfg.shiftEveryMs ? [0, 0] : SHIFT_CYCLE[Math.floor(now / cfg.shiftEveryMs) % 9]; },
      /** Call for every button change. False = do not send it to the PC (the wake press). */
      filterButton(button, pressed, now) {
        const bit = button < 32 ? 2 ** button : 0;
        if (pressed) {
          const swallow = cfg.swallow && power(now) === POWER.OFF;
          last = now;
          if (swallow) swallowed |= bit;
          return !swallow;
        }
        last = now;
        if (swallowed & bit) { swallowed &= ~bit; return false; }
        return true;
      },
    };
  }
  /** The frame a slot shows now, as firmware/src/screens.cpp's render() composes it. */
  function frame(slot, panels, now, t = SCROLL) {
    const a = canvas(), b = canvas(), out = canvas();
    if (slot.inTransition(now)) {
      renderScreen(a, slot.previous(), 0, t);
      renderScreen(b, slot.current(), 0, t);
      composeSlide(a, b, slot.progress(now), slot.transition() !== P.TRANSITION.SLIDE_RIGHT, out);
    } else renderScreen(out, slot.current(), slot.scrollSince(now), t);
    const [dx, dy] = panels ? panels.shift(now) : [0, 0];
    if (!dx && !dy) return out;
    shiftFrame(out, dx, dy, a);
    return a;
  }
  /** ScreenBehaviour (protobufjs object or null) → { transitionMs, scroll, panels cfg, brightness, dimBrightness }. */
  function behaviour(sb) {
    const v = (k, d) => (sb && sb[k] !== undefined && sb[k] !== null ? sb[k] : d);
    return {
      transitionMs: v('transitionMs', 360),
      scroll: { holdMs: v('scrollHoldMs', 2000), endHoldMs: v('scrollEndHoldMs', 1200), speedPxS: v('scrollSpeedPxS', 30) },
      panels: { dim: v('dimEnabled', true), off: v('offEnabled', true), shift: v('pixelShiftEnabled', true), swallow: v('wakeSwallowsPress', true),
        dimAfterMs: v('dimAfterS', 120) * 1000, offAfterMs: v('offAfterS', 1800) * 1000, shiftEveryMs: v('pixelShiftEveryS', 180) * 1000 },
      brightness: v('brightness', 255), dimBrightness: v('dimBrightness', 8),
    };
  }

  const api = { W, H, canvas, get, set, renderScreen, scrollOffset, bidiVisual, easeInOut, composeSlide, shiftFrame,
    createSlot, createPanels, frame, behaviour, NOT_CONNECTED, POWER, SCROLL };
  if (node) module.exports = api;
  else root.VolcoOled = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
```

- [ ] **Step 4: Run the test to see it pass**

Run: `cd tools/simulator/test && node --test oled.test.mjs`
Expected: PASS, 7 tests. All 16 golden renders match pixel for pixel.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git add tools/simulator/oled.js tools/simulator/test/oled.test.mjs
/usr/bin/git commit -m "Port the firmware screen renderer to the simulator, pixel-exact"
```

---

### Task 6: `layout.js`

**Files:**
- Create: `tools/simulator/layout.js`
- Test: `tools/simulator/test/layout.test.mjs`

**Interfaces:**
- Produces: `VolcoLayout`. Configs are plain objects in protobufjs's shape (camelCase, the same as `proto/presets/*.json`). A **layout** is `{ preset, sliders: [{pin?}], buttons: [button], base }`, where `base` keeps the non-hardware fields and the I2C definitions.
  - **Constants:** `KIND`, `LIMITS {sliders: 16, buttons: 32, screens: 8}`, `CHIP`, `PREFER`, `PRESETS [{id, name}]`, `DIY_REFERENCE`.
  - **Buttons:** `kindOf(button)`, `withKind(button, kind)`, `kinds(layout)`.
  - **Layouts:** `fromConfig(config) → layout` (fills a screen's proto defaults) and `presetLayout(id, base)`.
  - **Allocation:** `toConfig(layout) → {config} | {error: {message, what, index}}`, and `check(layout, change) → message | null`.
  - **Pin predicates:** `usable(p)`, `outputCapable(p)`, `adcCapable(p)`.

- [ ] **Step 1: Write the failing test**

Create `tools/simulator/test/layout.test.mjs`. Its last two tests are Review Focus 1 and 2:

```js
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
```

- [ ] **Step 2: Run it to see it fail**

Run: `cd tools/simulator/test && node --test layout.test.mjs`
Expected: FAIL with "Cannot find module '../layout.js'".

- [ ] **Step 3: Implement**

Create `tools/simulator/layout.js`:

```js
// The builder's model (spec §6.2, §6.3): presets, the ESP32 chip table, and turning a layout into
// a ControllerConfig with every pin allocated so the app's validator accepts it. Configs are plain
// objects in protobufjs's shape (camelCase, as proto/presets/*.json). No DOM: index.html loads it
// as a plain script (globalThis.VolcoLayout), the Node tests with require().
(function (root) {
  'use strict';

  const KIND = { NONE: 0, LED: 1, LED_PAIR: 2, SCREEN: 3 };
  const LIMITS = { sliders: 16, buttons: 32, screens: 8 };
  const range = (a, b) => Array.from({ length: b - a + 1 }, (_, i) => a + i);

  /** Classic ESP32, as Volco.Core.Controller.ChipInfo.Esp32: usable = exists minus reserved. */
  const CHIP = {
    usable: new Set([0, 2, 4, 5, ...range(12, 19), 21, 22, 23, 25, 26, 27, ...range(32, 39)]),
    inputOnly: new Set(range(34, 39)),
    adc1: new Set(range(32, 39)),
    adc2: new Set([0, 2, 4, ...range(12, 15), 25, 26, 27]),
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
   * Returns { config } or { error: { message, what: 'slider'|'button', index } }.
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
      if (b.screen && b.screen.bus === undefined) {
        const slot = SCREEN_SLOTS.find(([bus, a]) => !slots.has(`${bus}:${a}`));
        if (!slot) return fail(`${LIMITS.screens} screens maximum`, 'button', i);
        slots.add(`${slot[0]}:${slot[1]}`);
        b.screen = { bus: slot[0], address: slot[1] };
      }
    }
    for (let n = 0; n < 4; n++) {
      if (!busUsed(n) || kept[n]) continue;
      if (keepBus(n)) continue;
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
    return { config };
  }
  const firstScreenOn = (buttons, n) => buttons.findIndex(b => b.screen && b.screen.bus === n);
  const fail = (message, what, index) => ({ error: { message, what, index } });

  /** Why `change(layout)` wouldn't fit, or null when it would. change returns the new layout. */
  function check(layout, change) {
    const r = toConfig(change(layout));
    return r.error ? r.error.message : null;
  }

  const api = { KIND, LIMITS, CHIP, PREFER, PRESETS, DIY_REFERENCE, kindOf, withKind, kinds, fromConfig, presetLayout, toConfig, check,
    usable, outputCapable, adcCapable };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoLayout = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
```

- [ ] **Step 4: Run the test to see it pass**

Run: `cd tools/simulator/test && node --test layout.test.mjs`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git add tools/simulator/layout.js tools/simulator/test/layout.test.mjs
/usr/bin/git commit -m "Add the builder's layout model with ESP32 pin allocation"
```

---

### Task 7: `config.js`: forced answers, the stored config, encoding

**Files:**
- Modify: `tools/simulator/config.js`
- Test: `tools/simulator/test/config.test.mjs` (append)

**Interfaces:**
- Produces three new members on the session object:
  - `forceNextResult(code|null)`: a one-shot. The next complete write is answered with `code`, and only `OK` stores.
  - `current() → { state: 0|1|2, crc, config: object|null, reason: string|null }`.
  - `encode(config) → Uint8Array`.

- [ ] **Step 1: Write the failing tests**

Append to the end of `tools/simulator/test/config.test.mjs`:

```js
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
```

- [ ] **Step 2: Run them to see them fail**

Run: `cd tools/simulator/test && node --test config.test.mjs`
Expected: the 4 new tests FAIL with "h.session.forceNextResult is not a function" or "h.session.current is not a function".

- [ ] **Step 3: Implement**

In `tools/simulator/config.js`, inside `createSession`:

1. After `let t = null;   // the open transfer: { seq, total, crc, buf, received, last }`, add:
   ```js
   let forced = null;   // fault: the code the next complete write is answered with
   ```
2. In `end(seq, now)`, after the line `if (seq !== x.seq || x.received !== x.total) { reply(x.seq, CODE.BAD_SEQUENCE); return; }`, add:
   ```js
   if (forced !== null) {   // fault injection: answer with the chosen code; only OK stores
     const code = forced;
     forced = null;
     if (code !== CODE.OK) { reply(seq, code); return; }
     o.store(x.buf); reply(seq, CODE.OK); o.reboot(); return;
   }
   ```
3. Replace `return { onFrame, tick, hello, isOpen: () => t !== null };` with:
   ```js
   /** The stored config: { state: 0 none, 1 valid, 2 damaged; crc; config (a plain object in
    *  protobufjs's shape) or null; reason (why it is damaged) or null }. */
   function current() {
     const blob = o.stored();
     if (!blob || !blob.length) return { state: 0, crc: 0, config: null, reason: null };
     const crc = crc32(blob);
     const config = decode(type, blob);
     if (!config) return { state: 2, crc, config: null, reason: 'It does not decode.' };
     const [code, item, field] = validate(config);
     if (code !== CODE.OK)
       return { state: 2, crc, config: null, reason: `Button ${item + 1}: the screen ${field === FIELD.SCREEN_BUS ? 'bus' : 'address'} is invalid.` };
     return { state: 1, crc, config: type.toObject(config), reason: null };
   }

   /** A config object (layout.js toConfig) as the bytes to store. */
   function encode(config) { return type.encode(type.fromObject(config)).finish(); }

   /** Fault: answer the next complete config write with `code` (CODE.*), or null to stop. */
   function forceNextResult(code) { forced = code; }

   return { onFrame, tick, hello, current, encode, forceNextResult, isOpen: () => t !== null };
   ```

- [ ] **Step 4: Run the tests to see them pass**

Run: `cd tools/simulator/test && node --test config.test.mjs`
Expected: PASS, 15 tests.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git add tools/simulator/config.js tools/simulator/test/config.test.mjs
/usr/bin/git commit -m "Config session: forced answers, the stored config, encoding"
```

---

### Task 8: `demo.js`

**Files:**
- Create: `tools/simulator/demo.js`
- Test: `tools/simulator/test/demo.test.mjs`

**Interfaces:**
- Consumes: `protocol.js`.
- Produces: `VolcoDemo` with:
  - `outputFromScreen(content) → {device: 'speakers'|'headphones', muted} | null` (spec §7.3);
  - `createDemoHost(kinds)`, returning `{ state {output, outputMuted, micMuted, preset, discordMuted}, initial() → updates, press(i) → updates, overlay(i) → text|null }`. An update is `{button, led}` or `{button, transition, content}`;
  - `ICON`.

- [ ] **Step 1: Write the failing test**

Create `tools/simulator/test/demo.test.mjs`:

```js
// Node tests of tools/simulator/demo.js: the demo host and reading the output from Volco screens.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const P = require('../protocol.js');
const D = require('../demo.js');

test('output-related screens name the current output', () => {
  const s = (top, icon, flags = 0) => ({ ...P.screenText(top, icon, 'x'), flags });
  assert.deepEqual(D.outputFromScreen(s('NEXT: Arctis', 2)), { device: 'speakers', muted: false });
  assert.deepEqual(D.outputFromScreen(s('NEXT: Desk', 4)), { device: 'headphones', muted: false });
  assert.deepEqual(D.outputFromScreen(s('ACTIVE', 6)), { device: 'speakers', muted: false });
  assert.deepEqual(D.outputFromScreen(s('MUTED', 5, 1)), { device: 'headphones', muted: true });
  assert.deepEqual(D.outputFromScreen(s('LIVE', 1)), { device: 'speakers', muted: false });
  assert.equal(D.outputFromScreen(s('MUTED', 7, 1)), null);    // a microphone
  assert.equal(D.outputFromScreen(s('', 2)), null);             // a set-output for another device
  assert.equal(D.outputFromScreen(s('NOT CONNECTED', 255)), null);
  assert.equal(D.outputFromScreen(null), null);
});

test('the demo host gives every button a display to start with', () => {
  const host = D.createDemoHost([3, 3, 1, 2]);
  const init = host.initial();
  assert.equal(init.length, 4);
  assert.deepEqual(D.outputFromScreen(init[0].content), { device: 'speakers', muted: false });
  assert.equal(init[1].content.top, 'LIVE');
  assert.deepEqual(init.slice(2), [{ button: 2, led: 0 }, { button: 3, led: 1 }]);
});

test('the output screen and the LED pair toggle the same output', () => {
  const host = D.createDemoHost([3, 3, 1, 2]);
  const a = host.press(3);   // the LED pair
  assert.equal(host.state.output, 'headphones');
  assert.deepEqual(a.map(u => u.button), [0, 3]);
  assert.equal(a[0].transition, P.TRANSITION.SLIDE_LEFT);
  assert.deepEqual(D.outputFromScreen(a[0].content), { device: 'headphones', muted: false });
  assert.equal(a[1].led, 2);
  host.press(0);   // the output screen
  assert.equal(host.state.output, 'speakers');
  assert.equal(host.overlay(0), 'Speakers');
});

test('mutes toggle, presets cycle, plain buttons show nothing', () => {
  const host = D.createDemoHost([3, 3, 3, 3, 1, 0]);
  assert.equal(host.press(1)[0].content.top, 'MUTED');
  assert.equal(host.press(2)[0].content.dotsSelected, 1);
  assert.equal(host.press(3)[0].content.main, 'Discord');
  assert.deepEqual(host.press(4), [{ button: 4, led: 1 }]);
  assert.equal(host.state.outputMuted, true);
  assert.deepEqual(host.press(5), []);
  assert.equal(host.overlay(5), null);
});
```

- [ ] **Step 2: Run it to see it fail**

Run: `cd tools/simulator/test && node --test demo.test.mjs`
Expected: FAIL with "Cannot find module '../demo.js'".

- [ ] **Step 3: Implement**

Create `tools/simulator/demo.js`:

```js
// Demo mode (spec §8.2): while no WebSocket is open the simulator is its own host and answers
// presses the way Volco would. Also reads which output device a Volco screen shows (§7.3), which
// drives the sound waves. No DOM: index.html loads it as a plain script (globalThis.VolcoDemo),
// the Node tests with require().
(function (root) {
  'use strict';
  const P = typeof module === 'object' && module.exports ? require('./protocol.js') : root.VolcoProtocol;

  const ICON = { SPEAKER: 1, SPEAKERS: 2, HEADPHONES: 3, HEADSET: 4, EARBUDS: 5, DISPLAY: 6, STUDIOMIC: 8, PRESETS: 10 };
  const SPEAKER_ICONS = [ICON.SPEAKER, ICON.SPEAKERS, ICON.DISPLAY];
  const HEADPHONE_ICONS = [ICON.HEADPHONES, ICON.HEADSET, ICON.EARBUDS];

  /** What an output-related Volco screen says: { device: 'speakers'|'headphones', muted }, or
   *  null when the screen isn't about the current output (spec §7.3). */
  function outputFromScreen(c) {
    if (!c || c.layout !== 1 || c.topKind !== P.TOP.TEXT) return null;
    const device = SPEAKER_ICONS.includes(c.icon) ? 'speakers' : HEADPHONE_ICONS.includes(c.icon) ? 'headphones' : null;
    if (!device) return null;
    if (c.top.startsWith('NEXT:') || c.top === 'ACTIVE') return { device, muted: false };
    if (c.top === 'LIVE' || c.top === 'MUTED') return { device, muted: c.top === 'MUTED' };
    return null;
  }

  const DEVICES = { speakers: { icon: ICON.SPEAKERS, name: 'Speakers' }, headphones: { icon: ICON.HEADPHONES, name: 'Headphones' } };
  const PRESETS = ['Desk', 'Gaming', 'Calls'];
  /** Which demo role each screen gets, in order: output, microphone, presets, then Discord. */
  const ROLES = ['output', 'mic', 'presets'];

  /** kinds: one protocol KIND per button. Returns the demo host. */
  function createDemoHost(kinds) {
    const state = { output: 'speakers', outputMuted: false, micMuted: false, preset: 0, discordMuted: false };
    const screenRole = [];
    let n = 0;
    kinds.forEach((k, i) => { if (k === P.KIND.SCREEN) screenRole[i] = ROLES[n++] || 'discord'; });

    function content(i) {
      const role = screenRole[i];
      if (role === 'output') {
        const d = DEVICES[state.output], other = DEVICES[state.output === 'speakers' ? 'headphones' : 'speakers'];
        return P.screenText(`NEXT: ${other.name}`, d.icon, d.name);
      }
      if (role === 'mic') return { ...P.screenText(state.micMuted ? 'MUTED' : 'LIVE', ICON.STUDIOMIC, 'Shure SM7B'), flags: state.micMuted ? 1 : 0 };
      if (role === 'presets') return { ...P.screenText('', ICON.PRESETS, PRESETS[state.preset]), topKind: P.TOP.DOTS, dotsSelected: state.preset, dotsCount: PRESETS.length };
      return { ...P.screenText(state.discordMuted ? 'MUTED' : 'LIVE', ICON.HEADSET, 'Discord'), flags: state.discordMuted ? 1 : 0 };
    }
    const led = i => kinds[i] === P.KIND.LED_PAIR ? (state.output === 'speakers' ? 1 : 2) : state.outputMuted ? 1 : 0;
    const update = (i, transition = P.TRANSITION.NONE) =>
      kinds[i] === P.KIND.SCREEN ? { button: i, transition, content: content(i) } : { button: i, led: led(i) };
    const all = (transition, pick = () => true) =>
      kinds.map((k, i) => (k !== P.KIND.NONE && pick(i) ? update(i, transition) : null)).filter(Boolean);
    const outputBound = i => kinds[i] === P.KIND.LED_PAIR || screenRole[i] === 'output';

    return {
      state,
      /** Every button's display, for a fresh board. */
      initial: () => all(P.TRANSITION.NONE),
      /** A press: the new state, and the updates for every button it changed. */
      press(i) {
        const role = screenRole[i];
        if (kinds[i] === P.KIND.LED_PAIR || role === 'output') {
          state.output = state.output === 'speakers' ? 'headphones' : 'speakers';
          return all(P.TRANSITION.SLIDE_LEFT, outputBound);
        }
        if (kinds[i] === P.KIND.LED) { state.outputMuted = !state.outputMuted; return all(P.TRANSITION.NONE, j => kinds[j] === P.KIND.LED); }
        if (role === 'mic') state.micMuted = !state.micMuted;
        else if (role === 'presets') { state.preset = (state.preset + 1) % PRESETS.length; return [update(i, P.TRANSITION.SLIDE_LEFT)]; }
        else if (role === 'discord') state.discordMuted = !state.discordMuted;
        return kinds[i] === P.KIND.NONE ? [] : [update(i)];
      },
      /** The monitor overlay's text for a press (null when the press shows nothing). */
      overlay(i) {
        const role = screenRole[i];
        if (kinds[i] === P.KIND.LED_PAIR || role === 'output') return DEVICES[state.output].name;
        if (kinds[i] === P.KIND.LED) return state.outputMuted ? 'Output muted' : 'Output on';
        if (role === 'mic') return state.micMuted ? 'Microphone muted' : 'Microphone on';
        if (role === 'presets') return `Preset: ${PRESETS[state.preset]}`;
        if (role === 'discord') return state.discordMuted ? 'Discord muted' : 'Discord on';
        return null;
      },
    };
  }

  const api = { outputFromScreen, createDemoHost, ICON };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoDemo = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
```

- [ ] **Step 4: Run the test to see it pass**

Run: `cd tools/simulator/test && node --test demo.test.mjs`
Expected: PASS, 4 tests.

- [ ] **Step 5: Run the whole Node suite**

Run: `cd tools/simulator/test && node --test`
Expected: PASS, 58 tests (assets 2, config 15, demo 4, layout 11, link 12, oled 7, protocol 7).

- [ ] **Step 6: Commit**

```bash
/usr/bin/git add tools/simulator/demo.js tools/simulator/test/demo.test.mjs
/usr/bin/git commit -m "Add demo mode and reading the output device from Volco screens"
```

---

### Task 9: The page: scene, builder, connection, drawer, wiring

The browser layer. It has no Node tests; it is verified by the end-to-end check in Step 10, a headless Chromium run against a fake Volco, which carries Review Focus 3, 4 and 5.

**Files:**
- Rewrite: `tools/simulator/index.html`
- Create: `tools/simulator/stage.js`, `room.js`, `device.js`, `connection.js`, `builder.js`, `devtools.js`, `app.js`

**Interfaces:**
- **Loading three.js:** `index.html`'s inline module publishes `window.VolcoThree = { THREE, OrbitControls, RoomEnvironment, RoundedBoxGeometry, EffectComposer, RenderPass, BokehPass, OutputPass }` and dispatches `volco-three`, or `volco-three-failed` if the imports fail.
- **The scene:**
  - `VolcoStage.create(container, { target: {x, y, z}, onFrame(now, dt) })` → `{ THREE, RoundedBoxGeometry, scene, camera, controls, renderer, kit {canvasTex, rnd, std, phys, put, box, cyl}, reduceMotion, setOffset(x, y), wake() }`.
  - `VolcoRoom.build(stage)` → `{ setOutput(device, muted), showOverlay(text, value), update(now, dt) }`.
  - `VolcoDevice.create(stage, { onSlider(i, value01), onButton(i, pressed) })` → `{ build({sliders, kinds}, {drop}), setFinish(name), setLed(i, value), setScreen(i, frame1024, level), faderValue(i), setStatus(on), update(now, dt) }`.
- **The UI:**
  - `VolcoConnection.create({ settings, onOpen, onBytes, onClose, onSettings })` → `{ connect, disconnect, reboot, isOpen, send, address }`.
  - `VolcoBuilder.create({ stored, unavailable, onPreview, onApply, prefs, onPrefs, onFinish })` → `{ reload(byVolco), draft(), isDirty() }`.
  - `VolcoDevtools.create({ config, configJson, onClearConfig, onReboot, link, forceNextResult, prefs, onPrefs, onToggle })` → `{ frame(dir, payload, bad), note(text), refreshConfig(), setConnected(on), resetForcedResult() }`.

- [ ] **Step 1: Write `index.html`**

Replace `tools/simulator/index.html` entirely with:

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0, viewport-fit=cover">
<title>Volco Simulator</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Instrument+Sans:wght@400;500;600&family=IBM+Plex+Mono:wght@400;500&display=swap" rel="stylesheet">
<style>
/* One full-bleed 3D stage; four floating controls (connection, builder, developer tools). */
:root {
  --ground: #e8e5df;
  --ink: #2a2826;
  --ink-soft: #77726a;
  --hair: rgba(40, 34, 26, .10);
  --ui: 'Instrument Sans', 'Helvetica Neue', system-ui, sans-serif;
  color-scheme: light;
}
* { box-sizing: border-box; }
html, body { height: 100%; margin: 0; }
body { background: var(--ground); color: var(--ink); font: 14px var(--ui); overflow: hidden; }
[hidden] { display: none !important; }
#stage { position: fixed; inset: 0; }
#stage canvas { display: block; width: 100%; height: 100%; touch-action: none; }
.failed { position: fixed; inset: 0; display: grid; place-items: center; padding: 16px; text-align: center; font: 15px var(--ui); color: var(--ink-soft); }
.tag { font-size: 11px; font-weight: 600; letter-spacing: .08em; text-transform: uppercase; color: var(--ink-soft); background: rgba(40, 34, 26, .07); border-radius: 6px; padding: 2px 6px; }
.note { margin: 0; padding: 10px 16px; font-size: 12px; color: var(--ink-soft); border-top: 1px solid var(--hair); }
.memo { margin: 0; padding: 0 16px 10px; font-size: 12px; color: var(--ink-soft); }
/* Builder: a quiet paper-like panel floating at the right; the 3D view re-centres beside it. */
:root { --paper: rgba(251, 249, 245, .86); --paper-solid: #fbf9f5; --chip: #ffffff; --focus: #2d6cdf; }
.toggle { position: fixed; top: calc(14px + env(safe-area-inset-top, 0px)); right: 16px; width: 40px; height: 40px; border-radius: 12px; border: 0; cursor: pointer;
  display: grid; place-items: center; color: var(--ink); background: var(--paper); backdrop-filter: blur(18px); -webkit-backdrop-filter: blur(18px);
  box-shadow: 0 0 0 1px var(--hair), 0 6px 18px -10px rgba(40, 30, 20, .4); z-index: 3; }
.toggle[aria-expanded="true"] { background: var(--ink); color: var(--paper-solid); }
.toggle[aria-expanded="true"] rect { fill: var(--ink); }
.panel { position: fixed; top: calc(64px + env(safe-area-inset-top, 0px)); right: 16px; width: 292px; max-height: calc(100% - 80px); overflow: auto;
  background: var(--paper); backdrop-filter: blur(22px) saturate(1.15); -webkit-backdrop-filter: blur(22px) saturate(1.15);
  border-radius: 18px; box-shadow: 0 0 0 1px var(--hair), 0 18px 40px -18px rgba(40, 30, 20, .45); z-index: 2;
  transition: opacity .2s, transform .25s; }
.panel[hidden] { display: block !important; opacity: 0; transform: translateX(16px); pointer-events: none; }
.sec { padding: 14px 16px; display: flex; flex-direction: column; gap: 10px; }
.sec + .sec { border-top: 1px solid var(--hair); }
.sec.row { flex-direction: row; align-items: center; justify-content: space-between; }
.lbl { font-size: 11px; font-weight: 600; letter-spacing: .12em; text-transform: uppercase; color: var(--ink-soft); }
.select { position: relative; }
.select::after { content: ''; position: absolute; right: 12px; top: 50%; width: 7px; height: 7px; margin-top: -5px; border-right: 1.5px solid var(--ink-soft); border-bottom: 1.5px solid var(--ink-soft); transform: rotate(45deg); pointer-events: none; }
select { appearance: none; width: 100%; font: 500 14px var(--ui); color: var(--ink); background: var(--chip); border: 0; border-radius: 10px; padding: 9px 32px 9px 12px; box-shadow: 0 0 0 1px var(--hair); cursor: pointer; }
.step { display: flex; align-items: center; gap: 2px; background: var(--chip); border-radius: 10px; box-shadow: 0 0 0 1px var(--hair); padding: 2px; }
.step button { width: 32px; height: 30px; border: 0; background: transparent; font: 500 17px var(--ui); color: var(--ink); border-radius: 8px; cursor: pointer; }
.step button:hover:not(:disabled) { background: rgba(40, 34, 26, .06); }
.step button:disabled { color: rgba(40, 34, 26, .25); cursor: default; }
.step output { min-width: 26px; text-align: center; font: 600 14px var(--ui); font-variant-numeric: tabular-nums; }
.btns { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.btns li { display: flex; align-items: center; gap: 8px; padding: 4px 4px 4px 6px; border-radius: 12px; background: var(--chip); box-shadow: 0 0 0 1px var(--hair); }
.btns li.dragging { opacity: .4; }
.btns li.over { box-shadow: 0 0 0 1.5px var(--ink); }
.grip { width: 10px; height: 16px; cursor: grab; background-image: radial-gradient(circle, rgba(40, 34, 26, .35) 1.2px, transparent 1.4px); background-size: 5px 5px; flex: none; }
.num { width: 16px; font: 600 12px var(--ui); color: var(--ink-soft); font-variant-numeric: tabular-nums; text-align: center; flex: none; }
.kinds { display: flex; gap: 2px; flex: 1; background: rgba(40, 34, 26, .05); border-radius: 9px; padding: 2px; }
.kinds button { flex: 1; height: 28px; border: 0; background: transparent; border-radius: 7px; color: var(--ink-soft); cursor: pointer; display: grid; place-items: center; }
.kinds button:hover:not(:disabled) { color: var(--ink); }
.kinds button[aria-checked="true"] { background: var(--chip); color: var(--ink); box-shadow: 0 1px 2px rgba(40, 30, 20, .18); }
.kinds button:disabled { opacity: .3; cursor: default; }
.rm { width: 28px; height: 28px; border: 0; background: transparent; color: var(--ink-soft); border-radius: 8px; cursor: pointer; display: grid; place-items: center; flex: none; }
.rm:hover { background: rgba(40, 34, 26, .06); color: var(--ink); }
.add { display: flex; align-items: center; justify-content: center; gap: 6px; height: 36px; border: 1px dashed rgba(40, 34, 26, .22); border-radius: 12px; background: transparent; font: 500 13px var(--ui); color: var(--ink-soft); cursor: pointer; }
.add:hover:not(:disabled) { color: var(--ink); border-color: rgba(40, 34, 26, .4); }
.add:disabled { opacity: .4; cursor: default; }
.sws { display: flex; gap: 8px; }
.sw { width: 24px; height: 24px; border-radius: 50%; border: 0; cursor: pointer; padding: 0;
  box-shadow: inset 0 -2px 3px rgba(0, 0, 0, .18), inset 0 1px 1px rgba(255, 255, 255, .6), 0 0 0 1px var(--hair); }
.sw[aria-pressed="true"] { outline: 2px solid var(--ink); outline-offset: 2px; }
button:focus-visible, select:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
@media (max-width: 719px) {
  .panel { top: auto; left: 12px; right: 12px; bottom: calc(12px + env(safe-area-inset-bottom, 0px)); width: auto; max-height: 46%; }
  .panel[hidden] { transform: translateY(16px); }
}
/* Connection pill (top left), Apply footer (panel), developer drawer (bottom left). */
:root { --ok: #3f8f5a; --wait: #c98a2b; --off: #a39d94; --mono: 'IBM Plex Mono', ui-monospace, Menlo, monospace; --danger: #b2452c; }
.conn { position: fixed; top: calc(14px + env(safe-area-inset-top, 0px)); left: 16px; z-index: 3; }
.pill { display: flex; align-items: center; gap: 9px; height: 40px; padding: 0 16px 0 14px; border: 0; border-radius: 12px; cursor: pointer;
  font: 500 13px var(--ui); color: var(--ink); background: var(--paper); backdrop-filter: blur(18px); -webkit-backdrop-filter: blur(18px);
  box-shadow: 0 0 0 1px var(--hair), 0 6px 18px -10px rgba(40, 30, 20, .4); font-variant-numeric: tabular-nums; }
.dot { width: 8px; height: 8px; border-radius: 50%; background: var(--off); box-shadow: 0 0 0 3px rgba(163, 157, 148, .2); }
.dot.ok { background: var(--ok); box-shadow: 0 0 0 3px rgba(63, 143, 90, .18); }
.dot.wait { background: var(--wait); box-shadow: 0 0 0 3px rgba(201, 138, 43, .2); animation: blink 1s ease-in-out infinite; }
@keyframes blink { 50% { opacity: .35; } }
.pop { position: absolute; top: 48px; left: 0; width: 264px; padding: 14px; display: flex; flex-direction: column; gap: 12px; border-radius: 16px;
  background: var(--paper-solid); box-shadow: 0 0 0 1px var(--hair), 0 18px 40px -18px rgba(40, 30, 20, .45); }
.fields { display: flex; gap: 8px; }
.field { display: flex; flex-direction: column; gap: 5px; min-width: 0; }
.field.grow { flex: 1; } .field.port { width: 72px; }
.field span { font-size: 11px; font-weight: 600; letter-spacing: .12em; text-transform: uppercase; color: var(--ink-soft); }
.field input { font: 500 14px var(--ui); color: var(--ink); background: #fff; border: 0; border-radius: 9px; padding: 8px 10px; box-shadow: 0 0 0 1px var(--hair); min-width: 0; font-variant-numeric: tabular-nums; }
.field input:focus { outline: 2px solid var(--focus); outline-offset: 0; }
.check { display: flex; align-items: center; gap: 8px; font: 500 13px var(--ui); color: var(--ink); cursor: pointer; user-select: none; }
.check input { accent-color: var(--ink); width: 15px; height: 15px; margin: 0; }
.check.small { font-size: 12px; color: var(--ink-soft); }
.primary { height: 34px; padding: 0 16px; border: 0; border-radius: 10px; background: var(--ink); color: var(--paper-solid); font: 600 13px var(--ui); cursor: pointer; }
.primary:disabled { background: rgba(40, 34, 26, .14); color: rgba(40, 34, 26, .4); cursor: default; }
.primary.wide { width: 100%; }
.ghost { height: 34px; padding: 0 12px; border: 0; border-radius: 10px; background: transparent; color: var(--ink-soft); font: 500 13px var(--ui); cursor: pointer; }
.ghost:hover:not(:disabled) { background: rgba(40, 34, 26, .06); color: var(--ink); }
.ghost:disabled { opacity: .35; cursor: default; }
.ghost.small, .primary.small { height: 28px; font-size: 12px; padding: 0 10px; border-radius: 8px; }
.ghost.danger { color: var(--danger); }
.foot { position: sticky; bottom: 0; display: flex; align-items: center; gap: 6px; padding: 12px 12px 12px 16px; border-top: 1px solid var(--hair); background: var(--paper-solid); border-radius: 0 0 18px 18px; }
.foot .check { margin-right: auto; font-size: 13px; color: var(--ink-soft); }
.foot .pending { width: 6px; height: 6px; border-radius: 50%; background: var(--wait); margin-right: 2px; }
.devbtn { position: fixed; left: 16px; bottom: calc(16px + env(safe-area-inset-bottom, 0px)); width: 40px; height: 40px; border: 0; border-radius: 12px; cursor: pointer; z-index: 3;
  display: grid; place-items: center; color: var(--ink); background: var(--paper); backdrop-filter: blur(18px); -webkit-backdrop-filter: blur(18px);
  box-shadow: 0 0 0 1px var(--hair), 0 6px 18px -10px rgba(40, 30, 20, .4); }
.devbtn[aria-expanded="true"] { background: var(--ink); color: var(--paper-solid); }
.drawer { position: fixed; left: 16px; bottom: calc(64px + env(safe-area-inset-bottom, 0px)); width: min(600px, calc(100% - 32px - 308px)); height: 268px; z-index: 2;
  display: flex; flex-direction: column; border-radius: 18px; overflow: hidden; background: var(--paper); backdrop-filter: blur(22px) saturate(1.15); -webkit-backdrop-filter: blur(22px) saturate(1.15);
  box-shadow: 0 0 0 1px var(--hair), 0 18px 40px -18px rgba(40, 30, 20, .45); }
.dhead { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 8px 10px 8px 8px; border-bottom: 1px solid var(--hair); flex-wrap: wrap; }
.tabs { display: flex; gap: 2px; background: rgba(40, 34, 26, .05); border-radius: 10px; padding: 2px; }
.tabs button { height: 28px; padding: 0 12px; border: 0; border-radius: 8px; background: transparent; font: 500 12px var(--ui); color: var(--ink-soft); cursor: pointer; }
.tabs button[aria-selected="true"] { background: #fff; color: var(--ink); box-shadow: 0 1px 2px rgba(40, 30, 20, .18); }
.dtools { display: flex; align-items: center; gap: 4px; }
.pane { flex: 1; overflow: auto; min-height: 0; }
.log { font: 12px/1.75 var(--mono); padding: 6px 0; }
.log .r { display: grid; grid-template-columns: 64px 16px 128px minmax(0, 1fr); gap: 8px; padding: 0 14px; white-space: nowrap; }
.log .r:hover { background: rgba(40, 34, 26, .04); }
.log .t { color: var(--off); font-variant-numeric: tabular-nums; }
.log .in { color: #2f6f8f; } .log .out { color: #9a5b22; }
.log .n { color: var(--ink); font-weight: 500; }
.log .x { color: var(--ink-soft); overflow: hidden; text-overflow: ellipsis; }
.log .bad .n { color: var(--danger); }
.log .empty { padding: 28px 14px; color: var(--ink-soft); font-family: var(--ui); font-size: 13px; }
.kv { display: grid; grid-template-columns: 96px minmax(0, 1fr); gap: 8px 12px; margin: 0; padding: 16px; font-size: 13px; }
.kv dt { color: var(--ink-soft); } .kv dd { margin: 0; font: 500 13px var(--mono); color: var(--ink); overflow-wrap: anywhere; }
.acts { display: flex; gap: 6px; padding: 0 10px 12px; }
.faults { list-style: none; margin: 0; padding: 6px 0; }
.faults li { display: flex; align-items: center; justify-content: space-between; gap: 12px; min-height: 44px; padding: 0 14px 0 16px; font-size: 13px; }
.faults li + li { border-top: 1px solid var(--hair); }
.faults .armed { background: var(--wait); color: #fff; }
.select.small select { padding: 5px 28px 5px 10px; font-size: 12px; border-radius: 8px; }
.switch { position: relative; width: 34px; height: 20px; flex: none; cursor: pointer; }
.switch input { position: absolute; opacity: 0; inset: 0; margin: 0; cursor: pointer; }
.switch i { position: absolute; inset: 0; border-radius: 10px; background: rgba(40, 34, 26, .16); transition: background .15s; pointer-events: none; }
.switch i::after { content: ''; position: absolute; top: 2px; left: 2px; width: 16px; height: 16px; border-radius: 50%; background: #fff; box-shadow: 0 1px 2px rgba(0, 0, 0, .25); transition: transform .15s; }
.switch input:checked + i { background: var(--ink); }
.switch input:checked + i::after { transform: translateX(14px); }
.switch input:focus-visible + i { outline: 2px solid var(--focus); outline-offset: 2px; }
@media (max-width: 719px) {
  .drawer { width: auto; right: 12px; left: 12px; height: 46%; z-index: 4; }
  .pill { padding: 0 12px; }
}
</style>
</head>
<body>
<div id="stage"></div>
<p class="failed" id="scene-failed" hidden>The 3D view needs a network connection and WebGL. The connection and developer tools still work.</p>
<div class="conn">
  <button class="pill" id="conn-btn" aria-expanded="false" aria-controls="conn-pop"><span class="dot" id="conn-dot"></span><span id="conn-text">Not connected</span><span class="tag" id="conn-demo">Demo</span></button>
  <form class="pop" id="conn-pop" hidden>
    <div class="fields">
      <label class="field grow"><span>Host</span><input id="host" value="localhost" autocomplete="off" spellcheck="false"></label>
      <label class="field port"><span>Port</span><input id="port" value="8765" inputmode="numeric"></label>
    </div>
    <label class="check"><input type="checkbox" id="reconnect" checked> Reconnect automatically</label>
    <button class="primary wide" id="conn-go" type="submit">Connect</button>
  </form>
</div>
<button class="devbtn" id="dev-btn" aria-label="Developer tools" aria-expanded="false" aria-controls="drawer">
  <svg width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M6 5L2 9l4 4M12 5l4 4-4 4"></path></svg>
</button>
<section class="drawer" id="drawer" aria-label="Developer tools" hidden>
  <div class="dhead">
    <div class="tabs" role="tablist">
      <button role="tab" id="tab-frames" aria-selected="true" aria-controls="pane-frames">Frames</button>
      <button role="tab" id="tab-config" aria-selected="false" aria-controls="pane-config">Config</button>
      <button role="tab" id="tab-faults" aria-selected="false" aria-controls="pane-faults">Faults</button>
    </div>
    <div class="dtools" id="frame-tools">
      <label class="check small"><input type="checkbox" id="hide-ka" checked> Hide keepalive</label>
      <button class="ghost small" id="pause">Pause</button>
      <button class="ghost small" id="clear-log">Clear</button>
    </div>
  </div>
  <div class="pane log" id="pane-frames" role="tabpanel" aria-labelledby="tab-frames"></div>
  <div class="pane" id="pane-config" role="tabpanel" aria-labelledby="tab-config" hidden>
    <dl class="kv">
      <dt>State</dt><dd id="cfg-state">Valid</dd>
      <dt>Size</dt><dd id="cfg-size">—</dd>
      <dt>CRC</dt><dd id="cfg-crc">—</dd>
      <dt>Preset</dt><dd id="cfg-preset">—</dd>
      <dt>Hardware</dt><dd id="cfg-hw">—</dd>
    </dl>
    <p class="memo" id="cfg-memory-row" hidden>Kept in memory only: this browser blocks storage.</p>
    <div class="acts"><button class="ghost small" id="cfg-copy">Copy as JSON</button><button class="ghost small danger" id="cfg-clear">Clear config</button></div>
  </div>
  <div class="pane" id="pane-faults" role="tabpanel" aria-labelledby="tab-faults" hidden>
    <ul class="faults">
      <li><span>Go silent (no Alive, ignore the PC)</span><label class="switch"><input type="checkbox" id="f-silent"><i></i></label></li>
      <li><span>Corrupt the next frame's checksum</span><button class="ghost small" id="f-corrupt">Arm</button></li>
      <li><span>Answer the next config write with</span>
        <div class="select small"><select id="f-cfg" aria-label="Config write answer"></select></div></li>
      <li><span>Ignore Commit (fall back after 500 ms)</span><label class="switch"><input type="checkbox" id="f-commit"><i></i></label></li>
      <li><span>Reboot the controller</span><button class="ghost small" id="f-reboot">Reboot</button></li>
    </ul>
  </div>
</section>
<button class="toggle" id="toggle" aria-label="Builder" aria-expanded="true" aria-controls="panel">
  <svg width="18" height="18" viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><path d="M4 2v14M9 2v14M14 2v14"></path><rect x="2" y="9" width="4" height="3" rx="1" fill="var(--paper)"></rect><rect x="7" y="4" width="4" height="3" rx="1" fill="var(--paper)"></rect><rect x="12" y="11" width="4" height="3" rx="1" fill="var(--paper)"></rect></svg>
</button>
<aside class="panel" id="panel" aria-label="Builder">
  <div class="sec">
    <label class="lbl" for="preset">Preset</label>
    <div class="select"><select id="preset"></select></div>
  </div>
  <div class="sec row">
    <span class="lbl" id="sl-lbl">Sliders</span>
    <div class="step" role="group" aria-labelledby="sl-lbl">
      <button id="sl-dec" aria-label="Remove a slider">−</button>
      <output id="sl-n" aria-live="polite">5</output>
      <button id="sl-inc" aria-label="Add a slider">+</button>
    </div>
  </div>
  <div class="sec">
    <div class="lbl">Buttons</div>
    <ol class="btns" id="btns"></ol>
    <button class="add" id="add">
      <svg width="14" height="14" viewBox="0 0 14 14" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><path d="M7 2v10M2 7h10"></path></svg>
      Button
    </button>
  </div>
  <div class="sec row">
    <span class="lbl">Finish</span>
    <div class="sws">
      <button class="sw" data-finish="silver" aria-label="Silver" aria-pressed="false" style="background:linear-gradient(145deg,#eceded,#a9abad)"></button>
      <button class="sw" data-finish="graphite" aria-label="Graphite" aria-pressed="false" style="background:linear-gradient(145deg,#6b6e73,#2f3134)"></button>
      <button class="sw" data-finish="sand" aria-label="Sand" aria-pressed="false" style="background:linear-gradient(145deg,#f4f0e8,#d5cdbf)"></button>
    </div>
  </div>
  <p class="note" id="builder-note" hidden></p>
  <div class="foot">
    <label class="check"><input type="checkbox" id="autosave"> Auto-save</label>
    <button class="ghost" id="revert" disabled>Revert</button>
    <button class="primary" id="apply" disabled>Apply</button>
  </div>
</aside>

<!-- protobufjs for the config session; offline, config frames are ignored (spec §12). -->
<script src="https://cdn.jsdelivr.net/npm/protobufjs@7.4.0/dist/light/protobuf.min.js"></script>
<script type="importmap">
{ "imports": {
  "three": "https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js",
  "three/addons/": "https://cdn.jsdelivr.net/npm/three@0.170.0/examples/jsm/"
} }
</script>
<script type="module">
// Loads three.js and publishes it for the classic scripts below (spec §9).
try {
  const [THREE, { OrbitControls }, { RoomEnvironment }, { RoundedBoxGeometry }, { EffectComposer }, { RenderPass }, { BokehPass }, { OutputPass }] = await Promise.all([
    import('three'),
    import('three/addons/controls/OrbitControls.js'),
    import('three/addons/environments/RoomEnvironment.js'),
    import('three/addons/geometries/RoundedBoxGeometry.js'),
    import('three/addons/postprocessing/EffectComposer.js'),
    import('three/addons/postprocessing/RenderPass.js'),
    import('three/addons/postprocessing/BokehPass.js'),
    import('three/addons/postprocessing/OutputPass.js'),
  ]);
  window.VolcoThree = { THREE, OrbitControls, RoomEnvironment, RoundedBoxGeometry, EffectComposer, RenderPass, BokehPass, OutputPass };
  window.dispatchEvent(new Event('volco-three'));
} catch {
  window.dispatchEvent(new Event('volco-three-failed'));
}
</script>
<script defer src="fonts.js"></script>
<script defer src="icons.js"></script>
<script defer src="protocol.js"></script>
<script defer src="link.js"></script>
<script defer src="oled.js"></script>
<script defer src="layout.js"></script>
<script defer src="volco_config_descriptor.js"></script>
<script defer src="config.js"></script>
<script defer src="demo.js"></script>
<script defer src="stage.js"></script>
<script defer src="room.js"></script>
<script defer src="device.js"></script>
<script defer src="connection.js"></script>
<script defer src="builder.js"></script>
<script defer src="devtools.js"></script>
<script defer src="app.js"></script>
</body>
</html>
```

- [ ] **Step 2: Write `stage.js`**

Create `tools/simulator/stage.js`:

```js
// The 3D stage (spec §5, D4): renderer, scene, light, camera and controls, depth of field, the
// idle drift, re-centring beside the panels, and the mesh helpers room.js and device.js share.
// Browser only; needs window.VolcoThree (the inline module in index.html). Units are cm.
(function (root) {
  'use strict';

  /**
   * @param {HTMLElement} container
   * @param {object} o
   * @param {THREE.Vector3Like} o.target   what the camera orbits and focuses on (the board)
   * @param {(now:number, dt:number) => void} o.onFrame  called every frame before rendering
   */
  function create(container, o) {
    const { THREE, OrbitControls, RoomEnvironment, RoundedBoxGeometry, EffectComposer, RenderPass, BokehPass, OutputPass } = root.VolcoThree;
    const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

    const renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' });
    renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFShadowMap;
    container.appendChild(renderer.domElement);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color('#2e241d');
    scene.environment = new THREE.PMREMGenerator(renderer).fromScene(new RoomEnvironment(), 0.04).texture;
    scene.environmentIntensity = 0.32;

    // Camera: orbit and zoom around the board; right button rotates, left is for the board.
    const target = new THREE.Vector3(o.target.x, o.target.y, o.target.z);
    const camera = new THREE.PerspectiveCamera(34, 1, 1, 1500);
    camera.position.set(target.x - 26, target.y + 46, target.z + 94);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.target.copy(target);
    Object.assign(controls, { enableDamping: true, dampingFactor: 0.06, minDistance: 22, maxDistance: 190,
      minPolarAngle: 0.15, maxPolarAngle: 1.4, minAzimuthAngle: -1.0, maxAzimuthAngle: 1.0, enablePan: false });
    controls.mouseButtons = { LEFT: null, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.ROTATE };
    const AZ0 = Math.atan2(camera.position.x - target.x, camera.position.z - target.z);

    // Light: low golden window light from the left and a warm hemisphere fill (the lamp and the
    // monitor glow belong to room.js).
    const sun = new THREE.DirectionalLight('#ffcf9a', 1.9);
    sun.position.set(-160, 95, 55);
    sun.target.position.set(10, 0, -5);
    sun.castShadow = true;
    sun.shadow.mapSize.set(4096, 4096);
    Object.assign(sun.shadow.camera, { left: -110, right: 110, top: 90, bottom: -90, near: 20, far: 420 });
    Object.assign(sun.shadow, { radius: 6, bias: -0.0004, normalBias: 0.04 });
    scene.add(sun, sun.target, new THREE.HemisphereLight('#ffe6cc', '#5a4536', 0.42));

    // Post: gentle depth of field focused on the board, like a product photo.
    const composer = new EffectComposer(renderer, new THREE.WebGLRenderTarget(1, 1, { samples: 4, type: THREE.HalfFloatType }));
    composer.addPass(new RenderPass(scene, camera));
    const bokeh = new BokehPass(scene, camera, { focus: 90, aperture: 0.00009, maxblur: 0.007 });
    composer.addPass(bokeh);
    composer.addPass(new OutputPass());

    // Re-centre the board in the space the floating panels leave free (spec §4).
    let offset = { x: 0, y: 0 };
    function resize() {
      const w = container.clientWidth, h = container.clientHeight;
      renderer.setSize(w, h, false);
      composer.setSize(w, h);
      camera.aspect = w / h;
      camera.fov = w / h < 1 ? 50 : 34;
      camera.setViewOffset(w, h, offset.x, offset.y, w, h);
      camera.updateProjectionMatrix();
    }
    addEventListener('resize', resize);
    resize();

    // Idle drift (D4): only after 8 s with no sign of the viewer; eases in and out; any focus,
    // visibility, pointer, wheel or key event restarts the clock.
    let idleSince = performance.now(), swaySpeed = 0, swayDir = 1;
    const wake = () => { idleSince = performance.now(); swaySpeed = 0; };
    for (const ev of ['pointermove', 'pointerdown', 'wheel', 'keydown', 'focus', 'blur']) addEventListener(ev, wake, { passive: true });
    document.addEventListener('visibilitychange', wake);
    controls.addEventListener('start', () => { idleSince = Infinity; swaySpeed = 0; });
    controls.addEventListener('end', wake);

    const clock = new THREE.Clock();
    renderer.setAnimationLoop(() => {
      const now = performance.now(), dt = Math.min(clock.getDelta(), 0.05);
      const idle = !reduceMotion && !document.hidden && now - idleSince > 8000;
      if (idle) { const az = controls.getAzimuthalAngle() - AZ0; if (az > 0.3) swayDir = -1; else if (az < -0.3) swayDir = 1; }
      swaySpeed += ((idle ? 0.16 * swayDir : 0) - swaySpeed) * Math.min(1, dt * 0.5);
      controls.autoRotate = Math.abs(swaySpeed) > 0.001;
      controls.autoRotateSpeed = swaySpeed;
      o.onFrame(now, dt);
      controls.update();
      bokeh.uniforms.focus.value = camera.position.distanceTo(controls.target);
      composer.render();
    });

    // ── Mesh helpers shared by room.js and device.js ──
    function canvasTex(w, h, draw, { repeat = [1, 1], srgb = true } = {}) {
      const c = document.createElement('canvas'); c.width = w; c.height = h;
      draw(c.getContext('2d'), w, h);
      const t = new THREE.CanvasTexture(c);
      t.wrapS = t.wrapT = THREE.RepeatWrapping;
      t.repeat.set(...repeat);
      t.anisotropy = renderer.capabilities.getMaxAnisotropy();
      if (srgb) t.colorSpace = THREE.SRGBColorSpace;
      return t;
    }
    const kit = {
      canvasTex,
      rnd: (a, b) => a + Math.random() * (b - a),
      std: m => new THREE.MeshStandardMaterial(m),
      phys: m => new THREE.MeshPhysicalMaterial(m),
      put(parent, mesh, x, y, z, shadow = true) {
        mesh.position.set(x, y, z);
        mesh.castShadow = shadow; mesh.receiveShadow = true;
        parent.add(mesh);
        return mesh;
      },
      box: (w, h, d, m, r = 0) => new THREE.Mesh(r ? new RoundedBoxGeometry(w, h, d, 3, r) : new THREE.BoxGeometry(w, h, d), m),
      cyl: (rt, rb, h, m, seg = 40) => new THREE.Mesh(new THREE.CylinderGeometry(rt, rb, h, seg), m),
    };

    return {
      THREE, RoundedBoxGeometry, scene, camera, controls, renderer, kit, reduceMotion,
      /** Shift the view so the board sits in the free space: x right-panel, y bottom-sheet/drawer (px). */
      setOffset(x, y) { offset = { x, y }; resize(); },
      /** Restart the idle clock (e.g. when the board is touched). */
      wake,
    };
  }

  root.VolcoStage = { create };
})(globalThis);
```

- [ ] **Step 3: Write `room.js`**

Create `tools/simulator/room.js`:

```js
// The home office around the controller (spec §5.1, §5.2): room, light, desk and props, the
// monitor with its volume overlay, speakers and headphones with their sound waves. Browser only;
// built on stage.js. Ported from the approved mock (see the spec, docs/superpowers/specs/2026-10-07-simulator-redesign-design.md).
(function (root) {
  'use strict';

  /** Builds the room into stage.scene. The board goes at (30, 0, 0) on the desk (y = 0 is the desk top). */
  function build(stage) {
    const { THREE, RoundedBoxGeometry, scene, kit } = stage;
    const { canvasTex, rnd, std, phys, put, box, cyl } = kit;
    const period = stage.reduceMotion ? 5100 : 1700;   // one wave cycle, ms

    function wood(base, grain, repeat) {
      return canvasTex(2048, 2048, (g, W, H) => {
        g.fillStyle = base; g.fillRect(0, 0, W, H);
        for (let i = 0; i < 1100; i++) {
          const y0 = rnd(0, H), amp = rnd(3, 22), k1 = Math.ceil(rnd(1, 4)), k2 = Math.ceil(rnd(4, 11)), ph = rnd(0, 6.28);
          g.strokeStyle = `rgba(${grain},${rnd(0.05, 0.2)})`;
          g.lineWidth = rnd(0.5, 2.6);
          for (const off of [-H, 0, H]) {
            g.beginPath();
            for (let x = 0; x <= W; x += 16) {
              const y = y0 + off + Math.sin(x / W * 6.283 * k1 + ph) * amp + Math.sin(x / W * 6.283 * k2 + ph * 2) * amp * 0.22;
              x ? g.lineTo(x, y) : g.moveTo(x, y);
            }
            g.stroke();
          }
        }
        for (let i = 0; i < 60000; i++) { g.fillStyle = `rgba(${grain},${rnd(0, 0.06)})`; g.fillRect(rnd(0, W), rnd(0, H), rnd(2, 7), 1); }
      }, { repeat });
    }
    function speckle(base, size, n, lo, hi, a, repeat) {
      return canvasTex(size, size, (g, W, H) => {
        g.fillStyle = base; g.fillRect(0, 0, W, H);
        for (let i = 0; i < n; i++) { const v = rnd(lo, hi) | 0; g.fillStyle = `rgba(${v},${v - 6},${v - 14},${rnd(a[0], a[1])})`; g.fillRect(rnd(0, W), rnd(0, H), rnd(1, 3), rnd(1, 3)); }
      }, { repeat });
    }


    // ── Sound waves: rings that leave the selected output device ────────────────
    const EMITTERS = [];   // { name, obj, r, travel, rings[], level }
    const ringGeo = new THREE.RingGeometry(0.9, 1, 72);
    function addEmitter(name, obj, r, travel) {   // name: 'speakers' or 'headphones'
      const e = { name, obj, r, travel, rings: [], level: 0 };
      for (let i = 0; i < 3; i++) {
        const m = new THREE.Mesh(ringGeo, new THREE.MeshBasicMaterial({ color: '#f0a35e', transparent: true, opacity: 0, depthWrite: false, side: THREE.DoubleSide }));
        m.renderOrder = 3; m.visible = false;
        scene.add(m);
        e.rings.push(m);
      }
      EMITTERS.push(e);
    }
    const _p = new THREE.Vector3(), _d = new THREE.Vector3(), _q = new THREE.Quaternion();
    function updateWaves(now, dt, active) {
      for (const e of EMITTERS) {
        e.level += ((e.name === active ? 1 : 0) - e.level) * Math.min(1, dt * 4);
        const on = e.level > 0.01;
        if (on) { e.obj.getWorldPosition(_p); e.obj.getWorldDirection(_d); e.obj.getWorldQuaternion(_q); }
        e.rings.forEach((m, i) => {
          m.visible = on;
          if (!on) return;
          const ph = (now / period + i / 3) % 1;
          m.scale.setScalar(e.r * (1 + ph * 1.5));
          m.position.copy(_p).addScaledVector(_d, ph * e.travel);
          m.quaternion.copy(_q);
          m.material.opacity = e.level * 0.7 * (1 - ph) * Math.min(1, ph * 6);
        });
      }
    }


    // ── Room: plaster walls, window, floor and rug ──────────────────────────────
    const plaster = std({ map: speckle('#cdb497', 1024, 90000, 150, 225, [0.03, 0.09], [5, 3]), roughness: 0.96 });
    const back = put(scene, new THREE.Mesh(new THREE.PlaneGeometry(420, 320), plaster), 0, 85, -42, false);
    const left = new THREE.Mesh(new THREE.PlaneGeometry(260, 320), plaster);
    left.rotation.y = Math.PI / 2; put(scene, left, -125, 85, 70, false);
    const floor = new THREE.Mesh(new THREE.PlaneGeometry(420, 300), std({ map: wood('#7a5134', '60,34,18', [3, 6]), roughness: 0.55 }));
    floor.rotation.x = -Math.PI / 2; put(scene, floor, 0, -75, 100, false);
    const rug = box(170, 1, 120, std({ map: speckle('#a76f55', 512, 50000, 120, 200, [0.06, 0.2], [4, 3]), roughness: 1 }), 0.4);
    put(scene, rug, 10, -74.5, 60, false);
    // Skirting board along the back wall.
    put(scene, box(420, 9, 1.2, std({ color: '#efe6d8', roughness: 0.7 })), 0, -70.5, -41.4, false);

    // Window on the left wall: frame, mullions and a warm late-afternoon sky.
    (function windowPane() {
      const g = new THREE.Group();
      const sky = new THREE.Mesh(new THREE.PlaneGeometry(90, 110), new THREE.MeshBasicMaterial({ toneMapped: false,
        map: canvasTex(256, 512, (c, w, h) => {
          const gr = c.createLinearGradient(0, 0, 0, h);
          gr.addColorStop(0, '#f6d7ae'); gr.addColorStop(0.55, '#ffe9c9'); gr.addColorStop(1, '#f3c88f');
          c.fillStyle = gr; c.fillRect(0, 0, w, h);
          c.fillStyle = 'rgba(120,110,80,.35)';
          for (let i = 0; i < 9; i++) { c.beginPath(); c.ellipse(rnd(0, w), h * rnd(0.72, 0.95), rnd(30, 70), rnd(40, 90), 0, 0, 7); c.fill(); }
        }) }));
      g.add(sky);
      const frameM = std({ color: '#f1ebe1', roughness: 0.6 });
      for (const [w, h, x, y] of [[96, 4, 0, 57], [96, 4, 0, -57], [4, 118, -47, 0], [4, 118, 47, 0], [2.4, 110, 0, 0], [90, 2.4, 0, 12]])
        put(g, box(w, h, 4, frameM), x, y, 1.6);
      put(g, box(104, 3, 12, frameM), 0, -60, 5);   // sill
      g.rotation.y = Math.PI / 2;
      g.position.set(-124.8, 75, -2);
      scene.add(g);
    })();

    // Framed print on the back wall.
    (function print() {
      const art = canvasTex(512, 640, (c, w, h) => {
        c.fillStyle = '#efe4d2'; c.fillRect(0, 0, w, h);
        c.fillStyle = '#c7663f'; c.beginPath(); c.arc(w * 0.42, h * 0.4, 120, 0, 7); c.fill();
        c.fillStyle = '#2f4a4f'; c.fillRect(w * 0.18, h * 0.58, w * 0.64, h * 0.22);
        c.fillStyle = '#d8b06a'; c.beginPath(); c.arc(w * 0.68, h * 0.62, 70, Math.PI, 0); c.fill();
      });
      const g = new THREE.Group();
      put(g, box(44, 54, 2.4, std({ color: '#2a211b', roughness: 0.55 })), 0, 0, 0);
      put(g, box(40, 50, 0.2, std({ color: '#f6f1e8', roughness: 0.9 })), 0, 0, 1.25, false);
      put(g, new THREE.Mesh(new THREE.PlaneGeometry(30, 38), std({ map: art, roughness: 0.9 })), 0, 0, 1.4, false);
      g.position.set(58, 62, -40.6);
      scene.add(g);
    })();

    // ── Desk ────────────────────────────────────────────────────────────────────
    const DESK_TOP = 0;
    const deskTop = box(190, 3.2, 82, phys({ map: wood('#c79a6b', '92,52,24', [1, 1]), roughness: 0.5, clearcoat: 0.3, clearcoatRoughness: 0.45 }), 0.6);
    put(scene, deskTop, 0, -1.6, -0.5);
    const legM = std({ color: '#2b2622', roughness: 0.5, metalness: 0.6 });
    for (const x of [-88, 88]) for (const z of [-36, 35]) put(scene, box(4, 72, 4, legM, 0.4), x, -38.6, z, false);

    // Desk mat under keyboard and mouse.
    put(scene, box(74, 0.3, 32, std({ map: speckle('#4a4744', 512, 30000, 70, 110, [0.05, 0.2], [5, 2]), roughness: 1 }), 0.15), -24, 0.15, 10);

    // ── Monitor ─────────────────────────────────────────────────────────────────
    const MON = { x: 0, z: -25, w: 70, h: 39.4, y: 25.5 };
    const monCanvas = document.createElement('canvas'); monCanvas.width = 1280; monCanvas.height = 720;
    const monTex = new THREE.CanvasTexture(monCanvas); monTex.colorSpace = THREE.SRGBColorSpace; monTex.anisotropy = 8;
    const osd = { text: '', value: 0, shownAt: -1e9 };
    function drawMonitor(now) {
      const c = monCanvas.getContext('2d'), W = 1280, H = 720;
      const gr = c.createLinearGradient(0, 0, 0, H);
      gr.addColorStop(0, '#f1c79b'); gr.addColorStop(0.5, '#d98e68'); gr.addColorStop(1, '#6e3f4b');
      c.fillStyle = gr; c.fillRect(0, 0, W, H);
      const dunes = [['#c27459', 0.62, 50], ['#9b5653', 0.72, 70], ['#6d3b46', 0.84, 60], ['#4a2a36', 0.94, 40]];
      for (const [col, y, a] of dunes) {
        c.fillStyle = col; c.beginPath(); c.moveTo(0, H);
        for (let x = 0; x <= W; x += 20) c.lineTo(x, H * y - Math.sin(x / W * 5 + y * 9) * a - Math.sin(x / W * 13 + y * 3) * a * 0.25);
        c.lineTo(W, H); c.fill();
      }
      c.fillStyle = 'rgba(255,248,238,.9)'; c.beginPath(); c.arc(W * 0.7, H * 0.3, 46, 0, 7); c.fill();
      // Taskbar.
      c.fillStyle = 'rgba(250,244,236,.55)'; c.fillRect(0, H - 40, W, 40);
      for (let i = 0; i < 6; i++) { c.fillStyle = 'rgba(60,40,40,.35)'; c.beginPath(); c.roundRect(W / 2 - 130 + i * 45, H - 32, 24, 24, 6); c.fill(); }
      // Volco's volume OSD, as the app shows it while a slider moves.
      const age = now - osd.shownAt, alpha = age < 1400 ? 1 : Math.max(0, 1 - (age - 1400) / 300);
      if (alpha > 0) {
        c.globalAlpha = alpha;
        const w = 360, h = 86, x = (W - w) / 2, y = H - 40 - h - 40;
        c.fillStyle = 'rgba(250,247,242,.95)'; c.beginPath(); c.roundRect(x, y, w, h, 20); c.fill();
        c.fillStyle = '#2a2623'; c.font = '600 22px "Instrument Sans", sans-serif'; c.textBaseline = 'alphabetic';
        c.fillText(osd.text, x + 26, y + 36);
        c.textAlign = 'right'; c.fillStyle = '#77706a'; c.fillText(osd.value >= 0 ? `${Math.round(osd.value * 100)}` : '', x + w - 26, y + 36); c.textAlign = 'left';
        c.fillStyle = 'rgba(42,38,35,.12)'; c.beginPath(); c.roundRect(x + 26, y + 54, w - 52, 8, 4); c.fill();
        if (osd.value >= 0) { c.fillStyle = '#2a2623'; c.beginPath(); c.roundRect(x + 26, y + 54, (w - 52) * osd.value, 8, 4); c.fill(); }
        c.globalAlpha = 1;
      }
      monTex.needsUpdate = true;
      return alpha > 0;
    }
    function showOsd(text, value) { osd.text = text; osd.value = value; osd.shownAt = performance.now(); monitorDirty = true; }   // value < 0: no bar
    let monitorDirty = true;
    (function monitor() {
      const g = new THREE.Group();
      const alu = phys({ color: '#bfc0c2', metalness: 1, roughness: 0.32 });
      put(g, box(24, 0.9, 17, alu, 0.4), 0, 0.45, 2);
      put(g, box(5.5, 22, 1.6, alu, 0.6), 0, 11.5, -2.5);
      const shell = box(MON.w + 1.2, MON.h + 1.2, 2.2, phys({ color: '#1a1a1b', roughness: 0.45, clearcoat: 0.3 }), 0.5);
      put(g, shell, 0, MON.y, -0.6);
      put(g, box(MON.w - 6, MON.h - 6, 3, phys({ color: '#c9cacc', metalness: 1, roughness: 0.4 }), 1.2), 0, MON.y, -2.2);
      const screen = new THREE.Mesh(new THREE.PlaneGeometry(MON.w, MON.h), new THREE.MeshBasicMaterial({ map: monTex, toneMapped: false, color: '#d9d4cf' }));
      put(g, screen, 0, MON.y, 0.52, false);
      const glass = new THREE.Mesh(new THREE.PlaneGeometry(MON.w + 1, MON.h + 1), phys({ color: '#000', roughness: 0.08, clearcoat: 1, transparent: true, opacity: 0.12, depthWrite: false }));
      glass.renderOrder = 2; put(g, glass, 0, MON.y, 0.56, false);
      const glow = new THREE.PointLight('#ffd7c0', 260, 0, 2);
      put(g, glow, 0, MON.y - 4, 10, false);
      g.position.set(MON.x, DESK_TOP, MON.z);
      g.rotation.y = 0.06;
      scene.add(g);
    })();

    // ── Keyboard and mouse ──────────────────────────────────────────────────────
    (function keyboard() {
      const g = new THREE.Group();
      put(g, box(36, 1.5, 12.4, phys({ color: '#d9d9d6', metalness: 0.9, roughness: 0.35 }), 0.5), 0, 0.75, 0);
      const capGeo = new RoundedBoxGeometry(1.62, 0.7, 1.62, 2, 0.22);
      const capM = std({ color: '#efede8', roughness: 0.55 });
      const rows = [[15, 0], [15, 0], [14, 0.5], [13, 0.9], [12, 1.4]];
      const pitch = 1.9, keys = [];
      rows.forEach(([n, off], r) => { for (let i = 0; i < n; i++) keys.push([-14.2 + off * pitch / 2 + i * pitch * (15 / n) * 0.985, -4.6 + r * 1.95]); });
      const inst = new THREE.InstancedMesh(capGeo, capM, keys.length);
      const m4 = new THREE.Matrix4();
      keys.forEach(([x, z], i) => inst.setMatrixAt(i, m4.makeTranslation(x, 1.7, z)));
      inst.castShadow = true; inst.receiveShadow = true;
      g.add(inst);
      put(g, box(11, 0.7, 1.62, capM, 0.22), 0, 1.7, -4.6 + 5 * 1.95);
      g.position.set(-24, 0.3, 11);
      g.rotation.y = 0.04;
      scene.add(g);

      const mouse = new THREE.Mesh(new THREE.SphereGeometry(1, 40, 24), phys({ color: '#e9e7e2', roughness: 0.35, clearcoat: 0.5 }));
      mouse.scale.set(3.1, 1.7, 5.4);
      put(scene, mouse, 5, 0.1, 13);
      mouse.rotation.y = -0.15;
    })();

    // ── Desk things: mug, lamp, books, plant, notebook ──────────────────────────
    (function mug() {
      const g = new THREE.Group();
      const p = [];
      const R = 4.3, Hh = 9.6, wall = 0.32;
      p.push(new THREE.Vector2(0, 0.0), new THREE.Vector2(R - 0.6, 0), new THREE.Vector2(R - 0.15, 0.2), new THREE.Vector2(R, 0.8));
      for (let i = 1; i <= 12; i++) p.push(new THREE.Vector2(R + Math.sin(i / 12 * Math.PI) * 0.12, 0.8 + (Hh - 0.8) * i / 12));
      p.push(new THREE.Vector2(R - wall / 2, Hh + 0.12), new THREE.Vector2(R - wall, Hh), new THREE.Vector2(R - wall, 0.9), new THREE.Vector2(0, 0.9));
      const ceramic = phys({ color: '#2f4a4f', roughness: 0.3, clearcoat: 0.8, clearcoatRoughness: 0.15 });
      const body = new THREE.Mesh(new THREE.LatheGeometry(p, 64), ceramic);
      const handle = new THREE.Mesh(new THREE.TorusGeometry(2.3, 0.5, 20, 40, Math.PI * 1.15), ceramic);
      handle.rotation.z = -Math.PI / 2 - 0.25; handle.position.set(R + 0.4, Hh * 0.52, 0);
      const coffee = new THREE.Mesh(new THREE.CircleGeometry(R - wall - 0.02, 48), phys({ color: '#2a170b', roughness: 0.08, clearcoat: 1 }));
      coffee.rotation.x = -Math.PI / 2; coffee.position.y = Hh - 1.3;
      g.add(body, handle, coffee);
      g.traverse(o => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } });
      g.position.set(60, 0, 9); g.rotation.y = 2.4;
      scene.add(g);
    })();

    let lampLight;
    (function lamp() {
      const g = new THREE.Group();
      const black = phys({ color: '#1f1d1b', roughness: 0.4, metalness: 0.5, clearcoat: 0.3 });
      const brass = phys({ color: '#c8a46a', roughness: 0.3, metalness: 1 });
      put(g, cyl(6.5, 7, 1.6, black), 0, 0.8, 0);
      const a1 = put(g, cyl(0.45, 0.45, 36, black, 16), 0, 0, 0);
      const p0 = new THREE.Vector3(0, 1.6, 0), p1 = new THREE.Vector3(6, 34, 6), p2 = new THREE.Vector3(26, 38, 18);
      const along = (m, a, b) => { m.position.copy(a).add(b).multiplyScalar(0.5); m.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), b.clone().sub(a).normalize()); m.scale.y = a.distanceTo(b) / 36; };
      along(a1, p0, p1);
      along(put(g, cyl(0.4, 0.4, 36, black, 16), 0, 0, 0), p1, p2);
      put(g, new THREE.Mesh(new THREE.SphereGeometry(1, 20, 12), brass), p1.x, p1.y, p1.z);
      const head = new THREE.Group();
      head.position.copy(p2);
      const shade = new THREE.Mesh(new THREE.CylinderGeometry(2.2, 7.5, 10, 48, 1, true), phys({ color: '#1f1d1b', roughness: 0.45, metalness: 0.4, side: THREE.DoubleSide }));
      shade.castShadow = true; head.add(shade);
      const bulb = new THREE.Mesh(new THREE.CircleGeometry(6.9, 40), new THREE.MeshBasicMaterial({ color: '#ffe2b8', toneMapped: false }));
      bulb.rotation.x = Math.PI / 2; bulb.position.y = -3; head.add(bulb);
      head.rotation.z = -0.55; head.rotation.x = 0.35;
      g.add(head);
      g.position.set(-76, 0, -28);
      scene.add(g);
      lampLight = new THREE.SpotLight('#ffb86e', 5200, 0, 0.75, 0.85, 2);
      const lp = p2.clone().add(g.position); lp.y -= 3;
      lampLight.position.copy(lp);
      lampLight.target.position.set(-36, 0, 2);
      lampLight.castShadow = true;
      lampLight.shadow.mapSize.set(2048, 2048);
      lampLight.shadow.radius = 8;
      lampLight.shadow.bias = -0.0005;
      scene.add(lampLight, lampLight.target);
    })();

    (function books() {
      const cols = ['#7b4a35', '#34505c', '#c9b48a', '#5d6b45'];
      let y = 0;
      [[24, 3.2, 17, 0.06], [22, 2.6, 16, -0.1], [25, 3.6, 18, 0.12], [20, 2.2, 14, -0.04]].forEach(([w, h, d, r], i) => {
        const b = box(w, h, d, std({ color: cols[i], roughness: 0.8 }), 0.2);
        b.rotation.y = r;
        put(scene, b, -80, y + h / 2, -6, true);
        const pages = box(w - 0.6, h - 0.5, d - 0.2, std({ color: '#efe6d3', roughness: 0.9 }));
        pages.rotation.y = r;
        put(scene, pages, -80 + 0.4, y + h / 2, -6 + 0.1, false);
        y += h;
      });
    })();

    (function plant() {
      const g = new THREE.Group();
      const terracotta = std({ color: '#b86a4b', roughness: 0.85 });
      const pot = new THREE.Mesh(new THREE.CylinderGeometry(7.2, 5.6, 14, 48), terracotta);
      put(g, pot, 0, 7, 0);
      put(g, cyl(7.5, 7.5, 1.6, terracotta), 0, 13.4, 0);
      put(g, cyl(6.8, 6.8, 0.4, std({ color: '#3b2a1f', roughness: 1 })), 0, 13.2, 0, false);
      const leafShape = new THREE.Shape();
      leafShape.moveTo(-1.2, 0); leafShape.quadraticCurveTo(-2.4, 14, -0.1, 30); leafShape.lineTo(0.1, 30); leafShape.quadraticCurveTo(2.4, 14, 1.2, 0); leafShape.closePath();
      const leafGeo = new THREE.ExtrudeGeometry(leafShape, { depth: 0.25, bevelEnabled: true, bevelThickness: 0.1, bevelSize: 0.1, bevelSegments: 1 });
      for (let i = 0; i < 11; i++) {
        const m = std({ color: new THREE.Color().setHSL(0.28 + rnd(-0.02, 0.03), 0.42, rnd(0.22, 0.32)), roughness: 0.55, side: THREE.DoubleSide });
        const leaf = new THREE.Mesh(leafGeo, m);
        const a = i / 11 * Math.PI * 2 + rnd(-0.2, 0.2);
        leaf.position.set(Math.cos(a) * 2.2, 13, Math.sin(a) * 2.2);
        leaf.rotation.set(0, -a + Math.PI / 2, 0);
        leaf.rotateX(-rnd(0.05, 0.3));
        leaf.scale.setScalar(rnd(0.75, 1.3));
        leaf.castShadow = true; leaf.receiveShadow = true;
        g.add(leaf);
      }
      g.position.set(86, 0, -30);
      scene.add(g);
    })();

    (function notebook() {
      const g = new THREE.Group();
      put(g, box(15, 1.1, 21, std({ color: '#2d2a27', roughness: 0.75 }), 0.3), 0, 0.55, 0);
      put(g, box(14.6, 0.9, 20.6, std({ color: '#f1ead9', roughness: 0.9 })), 0.25, 0.55, 0, false);
      const pen = cyl(0.42, 0.42, 14, phys({ color: '#b08a4f', metalness: 1, roughness: 0.3 }), 20);
      pen.rotation.z = Math.PI / 2; pen.rotation.y = 0.5;
      put(g, pen, 0, 1.55, 2);
      g.position.set(76, 0, 25); g.rotation.y = -0.35;
      scene.add(g);
    })();


    // ── Speakers either side of the monitor, headphones on a stand ──────────────
    function speaker(x, z, toe) {
      const g = new THREE.Group();
      put(g, box(17, 28, 21, std({ color: '#ebe4d8', roughness: 0.6 }), 0.9), 0, 14, 0);
      const rubber = std({ color: '#1c1b1a', roughness: 0.9 });
      const trim = phys({ color: '#b9b4ab', metalness: 1, roughness: 0.35 });
      const cone = new THREE.Mesh(new THREE.CylinderGeometry(5.3, 1.6, 2, 48, 1, true), std({ color: '#2c2a28', roughness: 0.85, side: THREE.DoubleSide }));
      cone.rotation.x = Math.PI / 2; put(g, cone, 0, 9.5, 9.6, false);
      put(g, new THREE.Mesh(new THREE.TorusGeometry(5.5, 0.55, 16, 56), rubber), 0, 9.5, 10.4, false);
      put(g, new THREE.Mesh(new THREE.TorusGeometry(6.4, 0.35, 12, 56), trim), 0, 9.5, 10.5, false);
      const cap = new THREE.Mesh(new THREE.SphereGeometry(1.8, 24, 12, 0, Math.PI * 2, 0, Math.PI / 2), std({ color: '#3a3734', roughness: 0.5 }));
      cap.rotation.x = Math.PI / 2; put(g, cap, 0, 9.5, 8.7, false);
      put(g, new THREE.Mesh(new THREE.TorusGeometry(2.1, 0.35, 12, 40), trim), 0, 21.5, 10.5, false);
      const dome = new THREE.Mesh(new THREE.SphereGeometry(1.4, 24, 12, 0, Math.PI * 2, 0, Math.PI / 2), phys({ color: '#d8d4cc', metalness: 0.8, roughness: 0.25 }));
      dome.rotation.x = Math.PI / 2; dome.scale.y = 0.6; put(g, dome, 0, 21.5, 10.3, false);
      const mark = new THREE.Object3D(); mark.position.set(0, 9.5, 11); g.add(mark);
      g.position.set(x, 0, z); g.rotation.y = toe; g.scale.setScalar(0.7);
      scene.add(g);
      addEmitter('speakers', mark, 3.9, 11);
    }
    speaker(-46, -27, 0.28);
    speaker(46, -27, -0.28);

    (function headphones() {
      const g = new THREE.Group();
      const matte = std({ color: '#2b2a29', roughness: 0.7 });
      const soft = std({ color: '#3b3836', roughness: 0.95 });
      const metal = phys({ color: '#c3c0ba', metalness: 1, roughness: 0.3 });
      const walnut = phys({ color: '#6b4329', roughness: 0.5, clearcoat: 0.4 });
      put(g, cyl(5.5, 6, 1.4, walnut), 0, 0.7, 0);
      put(g, cyl(0.6, 0.6, 30, metal, 16), 0, 15.6, 0);
      put(g, box(7, 1.4, 3.4, walnut, 0.6), 0, 30.4, 0);
      put(g, new THREE.Mesh(new THREE.TorusGeometry(9, 1.0, 16, 64, Math.PI), matte), 0, 22.2, 0);
      const pad = new THREE.Mesh(new THREE.TorusGeometry(8.1, 0.7, 12, 48, Math.PI * 0.6), soft);
      pad.rotation.z = Math.PI * 0.2; put(g, pad, 0, 22.2, 0, false);
      for (const s of [-1, 1]) {
        put(g, cyl(0.32, 0.32, 5, metal, 12), s * 9, 21, 0);
        const cup = cyl(4.6, 4.6, 3.2, matte, 48); cup.rotation.z = Math.PI / 2; put(g, cup, s * 9.8, 17.5, 0);
        const plate = cyl(3.4, 3.4, 0.3, metal, 48); plate.rotation.z = Math.PI / 2; put(g, plate, s * 11.5, 17.5, 0, false);
        const cushion = new THREE.Mesh(new THREE.TorusGeometry(3.4, 1.3, 16, 40), soft);
        cushion.rotation.y = Math.PI / 2; put(g, cushion, s * 7.6, 17.5, 0);
        const mark = new THREE.Object3D(); mark.position.set(s * 11.7, 17.5, 0); mark.rotation.y = s * Math.PI / 2; g.add(mark);
        addEmitter('headphones', mark, 4.4, 8);
      }
      g.position.set(68, 0, -24); g.rotation.y = -0.55;
      scene.add(g);
    })();


    let output = 'speakers', muted = false;
    return {
      /** Which device plays (spec §7.3): 'speakers' or 'headphones'; muted stops the waves. */
      setOutput(device, isMuted) { output = device; muted = !!isMuted; },
      /** The monitor's volume overlay: text, and a bar for value 0..1 (no bar when value < 0). */
      showOverlay: showOsd,
      update(now, dt) {
        updateWaves(now, dt, muted ? null : output);
        if (monitorDirty) monitorDirty = drawMonitor(now);
      },
    };
  }

  root.VolcoRoom = { build };
})(globalThis);
```

- [ ] **Step 4: Write `device.js`**

Create `tools/simulator/device.js`:

```js
// The 3D controller (spec §5.3, §5.4): the wedge case built from a layout, fader caps you drag,
// keys you press, LEDs, and OLEDs that show the 128×64 frames oled.js renders. Browser only;
// built on stage.js. Ported from the approved mock (see the spec, docs/superpowers/specs/2026-10-07-simulator-redesign-design.md).
(function (root) {
  'use strict';

  const FINISHES = {
    silver:   { body: ['#c9cacc', 1.0, 0.36], key: '#ecebe7', cap: '#1d1d1e', line: '#f2f1ec', mark: 'rgba(0,0,0,0.30)' },
    graphite: { body: ['#5a5c61', 0.95, 0.42], key: '#2c2d30', cap: '#151516', line: '#f2f1ec', mark: 'rgba(255,255,255,0.26)' },
    sand:     { body: ['#e6e0d5', 0.0, 0.55], key: '#f4f1ea', cap: '#efebe3', line: '#e2602a', mark: 'rgba(80,60,30,0.30)' },
  };
  const PX_ON = '#e9f1ff', PX_OFF = '#07090c', CELL = 4;
  const POSITION = { x: 30, z: 0, turn: -0.12 };   // on the desk, angled a little toward the chair

  /**
   * @param {object} stage   stage.js
   * @param {object} o
   * @param {(index:number, value:number) => void} o.onSlider   value 0..1 (0 = front)
   * @param {(index:number, pressed:boolean) => void} o.onButton
   */
  function create(stage, o) {
    const { THREE, RoundedBoxGeometry, scene, camera, renderer, controls, kit } = stage;
    const M = {
      body: new THREE.MeshPhysicalMaterial(),
      key: new THREE.MeshPhysicalMaterial({ roughness: 0.42, clearcoat: 0.15, clearcoatRoughness: 0.6 }),
      cap: new THREE.MeshPhysicalMaterial({ roughness: 0.5, clearcoat: 0.2, clearcoatRoughness: 0.5 }),
      line: new THREE.MeshStandardMaterial({ roughness: 0.6 }),
      groove: new THREE.MeshStandardMaterial({ color: '#0b0b0b', roughness: 0.9 }),
      well: new THREE.MeshStandardMaterial({ color: '#141414', roughness: 0.85 }),
      bezel: new THREE.MeshPhysicalMaterial({ color: '#0d0d0e', roughness: 0.35, clearcoat: 0.6 }),
      glass: new THREE.MeshPhysicalMaterial({ color: '#000', roughness: 0.04, clearcoat: 1, transparent: true, opacity: 0.18, depthWrite: false, envMapIntensity: 2.2 }),
      tick: new THREE.MeshStandardMaterial({ color: '#2a2a2a', roughness: 0.8, transparent: true, opacity: 0.45 }),
    };
    let finish = 'silver', board = null, wordmark = null;
    const interactive = [];
    const faderValues = [];

    function drawWordmark() {
      if (!wordmark) return;
      const c = wordmark.image, g = c.getContext('2d');
      g.clearRect(0, 0, c.width, c.height);
      g.fillStyle = FINISHES[finish].mark;
      g.font = '600 64px "Instrument Sans", sans-serif';
      g.letterSpacing = '22px';
      g.textAlign = 'right'; g.textBaseline = 'middle';
      g.fillText('VOLCO', c.width - 8, c.height / 2);
      wordmark.needsUpdate = true;
    }
    function setFinish(name) {
      finish = FINISHES[name] ? name : 'silver';
      const f = FINISHES[finish];
      M.body.color.set(f.body[0]); M.body.metalness = f.body[1]; M.body.roughness = f.body[2];
      M.body.clearcoat = finish === 'sand' ? 0.25 : 0;
      M.key.color.set(f.key); M.cap.color.set(f.cap); M.line.color.set(f.line);
      M.tick.color.set(finish === 'graphite' ? '#d8d8d8' : '#2a2a2a');
      drawWordmark();
    }

    function roundedShape(pts, r) {
      const s = new THREE.Shape(), n = pts.length;
      for (let i = 0; i < n; i++) {
        const p0 = pts[(i - 1 + n) % n], p1 = pts[i], p2 = pts[(i + 1) % n];
        const a = p1.clone().sub(p1.clone().sub(p0).normalize().multiplyScalar(r[i]));
        const b = p1.clone().add(p2.clone().sub(p1).normalize().multiplyScalar(r[i]));
        i ? s.lineTo(a.x, a.y) : s.moveTo(a.x, a.y);
        s.quadraticCurveTo(p1.x, p1.y, b.x, b.y);
      }
      s.closePath();
      return s;
    }

    function makeScreen() {
      const out = document.createElement('canvas'); out.width = 128 * CELL; out.height = 64 * CELL;
      const tex = new THREE.CanvasTexture(out);
      tex.colorSpace = THREE.SRGBColorSpace; tex.anisotropy = 8;
      const mat = new THREE.MeshBasicMaterial({ map: tex, toneMapped: false });
      const s = { out, ctx: out.getContext('2d'), tex, mat, frame: null };
      paint(s, new Uint8Array(1024), 1);
      return s;
    }
    /** Paints a 1-bit page-order frame (oled.js) as a pixel grid; level 0..1 is the panel's brightness. */
    function paint(s, frame, level) {
      const g = s.ctx;
      g.fillStyle = PX_OFF; g.fillRect(0, 0, s.out.width, s.out.height);
      g.fillStyle = PX_ON;
      for (let x = 0; x < 128; x++) for (let page = 0; page < 8; page++) {
        const b = frame[page * 128 + x];
        if (b) for (let bit = 0; bit < 8; bit++) if (b & (1 << bit)) g.fillRect(x * CELL, (page * 8 + bit) * CELL, CELL - 0.7, CELL - 0.7);
      }
      s.mat.color.setScalar(level);
      s.tex.needsUpdate = true;
    }

    /** Builds the board for { sliders, kinds }; drop: animate it falling into place (a new preset). */
    function build(hw, { drop = false } = {}) {
      if (board) { scene.remove(board.group); board.group.traverse(x => { if (x.isMesh) x.geometry.dispose(); }); }
      interactive.length = 0;
      const n = hw.sliders, kinds = hw.kinds, b = kinds.length;
      const cols = b <= 3 ? b : Math.min(4, Math.ceil(b / 2)), rows = b ? Math.ceil(b / cols) : 1;
      const PITCH = 3.3, COLW = 5.0;
      const W = 2.9 + Math.max(0, n - 1) * PITCH + (b ? 5.4 + (cols - 1) * COLW + 4.2 : 2.9);
      const D = Math.max(14.5, rows * 7.0 + 2.4);
      const hf = 1.9, hb = 6.0, bs = 0.22, bt = 0.32;

      const group = new THREE.Group();
      const shape = roundedShape([new THREE.Vector2(0, 0), new THREE.Vector2(D, 0), new THREE.Vector2(D, hf), new THREE.Vector2(0, hb)], [0.25, 0.25, 0.7, 1.0]);
      const geo = new THREE.ExtrudeGeometry(shape, { depth: W - 2 * bt, bevelEnabled: true, bevelThickness: bt, bevelSize: bs, bevelSegments: 6, curveSegments: 28 });
      geo.rotateY(-Math.PI / 2);
      geo.translate((W - 2 * bt) / 2, bs, -D / 2);
      const body = new THREE.Mesh(geo, M.body);
      body.castShadow = true; body.receiveShadow = true;
      group.add(body);

      const top = new THREE.Group();
      top.position.y = (hb + hf) / 2 + 2 * bs;
      top.rotation.x = Math.atan2(hb - hf, D);
      group.add(top);
      const add = (mesh, x, y, z, shadow = true) => kit.put(top, mesh, x, y, z, shadow);

      const x0 = -W / 2 + 2.9, TRAVEL = 3.3, SZ = 0.2;
      const faders = [];
      for (let i = 0; i < n; i++) {
        const x = x0 + i * PITCH;
        add(new THREE.Mesh(new RoundedBoxGeometry(0.42, 0.2, TRAVEL * 2 + 0.9, 2, 0.08), M.groove), x, -0.095, SZ, false);
        for (let k = 0; k <= 10; k++) {
          const len = k % 5 === 0 ? 0.55 : 0.32;
          add(new THREE.Mesh(new THREE.BoxGeometry(len, 0.01, 0.045), M.tick), x - 0.95 - len / 2, 0.006, SZ - TRAVEL + k * TRAVEL * 2 / 10, false);
        }
        const cap = new THREE.Group();
        const shell = new THREE.Mesh(new RoundedBoxGeometry(1.55, 1.05, 1.05, 4, 0.2), M.cap);
        shell.position.y = 0.72; shell.castShadow = true;
        const stem = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.3, 0.3), M.groove); stem.position.y = 0.12;
        const line = new THREE.Mesh(new THREE.BoxGeometry(1.25, 0.012, 0.09), M.line); line.position.y = 1.247;
        cap.add(shell, stem, line);
        const v = faderValues[i] ?? 0.5;
        cap.position.set(x, 0, SZ + TRAVEL - v * TRAVEL * 2);
        top.add(cap);
        const f = { cap, zMin: SZ - TRAVEL, zMax: SZ + TRAVEL, index: i };
        shell.userData.fader = f; line.userData.fader = f;
        interactive.push(shell);
        faders.push(f);
      }

      const bx0 = x0 + Math.max(0, n - 1) * PITCH + 5.4;
      const keys = [];
      kinds.forEach((kind, i) => {
        const c = i % cols, r = Math.floor(i / cols);
        const x = bx0 + c * COLW, z = (r - (rows - 1) / 2) * 7.0 + 0.2;
        const zInd = z - 2.0, zKey = z + 1.4;
        add(new THREE.Mesh(new RoundedBoxGeometry(2.7, 0.2, 2.7, 3, 0.25), M.well), x, -0.095, zKey, false);
        const mesh = add(new THREE.Mesh(new RoundedBoxGeometry(2.45, 0.95, 2.45, 5, 0.3), M.key), x, 0.42, zKey);
        const k = { mesh, rest: 0.42, pressed: false, index: i, kind, leds: [], screen: null };
        mesh.userData.key = k;
        interactive.push(mesh);
        keys.push(k);
        if (kind === 3) {
          add(new THREE.Mesh(new RoundedBoxGeometry(4.15, 0.12, 2.35, 3, 0.1), M.bezel), x, 0.0, zInd);
          k.screen = makeScreen();
          const disp = new THREE.Mesh(new THREE.PlaneGeometry(3.6, 1.8), k.screen.mat);
          disp.rotation.x = -Math.PI / 2; add(disp, x, 0.065, zInd, false);
          const glass = new THREE.Mesh(new THREE.PlaneGeometry(4.05, 2.25), M.glass);
          glass.rotation.x = -Math.PI / 2; glass.renderOrder = 2; add(glass, x, 0.07, zInd, false);
        } else if (kind === 1 || kind === 2) {
          k.leds = (kind === 2 ? [-0.55, 0.55] : [0]).map(dx => {
            const mat = new THREE.MeshPhysicalMaterial({ color: '#4a2e14', roughness: 0.2, clearcoat: 1, emissive: '#ff9a2e', emissiveIntensity: 0 });
            const dome = new THREE.Mesh(new THREE.SphereGeometry(0.2, 24, 12, 0, Math.PI * 2, 0, Math.PI / 2), mat);
            dome.scale.y = 0.7;
            add(new THREE.Mesh(new THREE.CylinderGeometry(0.28, 0.28, 0.04, 24), M.well), x + dx, 0.0, zInd, false);
            add(dome, x + dx, 0.02, zInd);
            const glow = new THREE.PointLight('#ffa24a', 0, 2.6, 2);
            add(glow, x + dx, 0.35, zInd, false);
            return { mat, glow };
          });
        }
      });

      // Status LED, engraved wordmark, soft contact shadow.
      const status = new THREE.Mesh(new THREE.CircleGeometry(0.13, 20), new THREE.MeshBasicMaterial({ color: '#ffb35c', toneMapped: false }));
      status.rotation.x = -Math.PI / 2; add(status, -W / 2 + 1.3, 0.012, -D / 2 + 1.1, false);
      wordmark = kit.canvasTex(1024, 128, () => {});
      wordmark.wrapS = wordmark.wrapT = THREE.ClampToEdgeWrapping;
      drawWordmark();
      const wm = new THREE.Mesh(new THREE.PlaneGeometry(6.4, 0.8), new THREE.MeshStandardMaterial({ map: wordmark, transparent: true, roughness: 0.7, depthWrite: false }));
      wm.rotation.x = -Math.PI / 2; add(wm, W / 2 - 4.1, 0.01, -D / 2 + 1.05, false);
      const ao = new THREE.Mesh(new THREE.PlaneGeometry(W + 8, D + 8), new THREE.MeshBasicMaterial({
        map: kit.canvasTex(256, 256, (g, w, h) => { g.filter = 'blur(18px)'; g.fillStyle = '#000'; g.fillRect(34, 34, w - 68, h - 68); }, { srgb: false }),
        transparent: true, opacity: 0.5, depthWrite: false, color: '#2a2016' }));
      ao.rotation.x = -Math.PI / 2; ao.position.y = 0.012;
      group.add(ao);

      group.position.set(POSITION.x, 0, POSITION.z);
      group.rotation.y = POSITION.turn;
      scene.add(group);
      board = { group, top, faders, keys, status, dropT0: drop ? performance.now() : -1e9 };
    }

    /** value: an LED's value (spec §7): one LED lit when not 0; a pair lights A on 1, B on 2. */
    function setLed(i, value) {
      const k = board && board.keys[i];
      if (!k || !k.leds.length) return;
      k.leds.forEach((led, j) => {
        const on = k.kind === 2 ? value === j + 1 : value !== 0;
        led.mat.emissiveIntensity = on ? 3.2 : 0;
        led.mat.color.set(on ? '#ffcf8a' : '#4a2e14');
        led.glow.intensity = on ? 0.6 : 0;
      });
    }
    /** A screen button's frame (1024 bytes, page order) at brightness level 0..1 (0 = off). */
    function setScreen(i, frame, level) {
      const s = board && board.keys[i] && board.keys[i].screen;
      if (!s) return;
      if (s.frame && s.level === level && s.frame.every((b, j) => b === frame[j])) return;   // unchanged: no repaint
      s.frame = frame; s.level = level;
      paint(s, frame, level);
    }

    // ── Pointer: the left button works the board, everything else is the camera's ──
    const ray = new THREE.Raycaster(), ndc = new THREE.Vector2();
    let drag = null, held = null;
    const toNdc = e => {
      const r = renderer.domElement.getBoundingClientRect();
      ndc.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
      ray.setFromCamera(ndc, camera);
    };
    const pick = e => { toNdc(e); return ray.intersectObjects(interactive, false)[0]; };
    renderer.domElement.addEventListener('pointerdown', e => {
      if (e.pointerType === 'mouse' && e.button !== 0) return;
      const hit = pick(e);
      if (!hit) return;
      controls.enabled = false;
      renderer.domElement.setPointerCapture(e.pointerId);
      const u = hit.object.userData;
      if (u.fader) drag = u.fader;
      if (u.key) { held = u.key; held.pressed = true; o.onButton(held.index, true); }
    }, { capture: true });   // before OrbitControls sees it
    renderer.domElement.addEventListener('pointermove', e => {
      if (!drag) { renderer.domElement.style.cursor = pick(e) ? 'pointer' : ''; return; }
      const t = board.top;
      const normal = new THREE.Vector3(0, 1, 0).applyQuaternion(t.getWorldQuaternion(new THREE.Quaternion()));
      const plane = new THREE.Plane().setFromNormalAndCoplanarPoint(normal, t.getWorldPosition(new THREE.Vector3()));
      toNdc(e);
      const p = ray.ray.intersectPlane(plane, new THREE.Vector3());
      if (!p) return;
      t.worldToLocal(p);
      const z = THREE.MathUtils.clamp(p.z, drag.zMin, drag.zMax);
      drag.cap.position.z = z;
      const value = (drag.zMax - z) / (drag.zMax - drag.zMin);
      faderValues[drag.index] = value;
      o.onSlider(drag.index, value);
    });
    const release = () => {
      if (held) { held.pressed = false; o.onButton(held.index, false); }
      drag = null; held = null; controls.enabled = true;
    };
    renderer.domElement.addEventListener('pointerup', release);
    renderer.domElement.addEventListener('pointercancel', release);

    return {
      FINISHES: Object.keys(FINISHES),
      build, setFinish, setLed, setScreen,
      /** A slider's position 0..1 (0.5 until moved). */
      faderValue: i => faderValues[i] ?? 0.5,
      /** The status LED: lit while the controller runs, dark while it reboots. */
      setStatus(on) { if (board) board.status.material.color.set(on ? '#ffb35c' : '#2a1c10'); },
      update(now, dt) {
        if (!board) return;
        const k = Math.min(1, (now - board.dropT0) / 520), e = 1 - Math.pow(1 - k, 3);
        board.group.position.y = (1 - e) * 3;
        board.group.rotation.x = (1 - e) * -0.04;
        for (const key of board.keys) {
          const target = key.pressed ? key.rest - 0.24 : key.rest;
          key.mesh.position.y += (target - key.mesh.position.y) * Math.min(1, dt * 28);
        }
      },
    };
  }

  root.VolcoDevice = { create, FINISHES: Object.keys(FINISHES) };
})(globalThis);
```

- [ ] **Step 5: Write `connection.js`**

Create `tools/simulator/connection.js`:

```js
// The WebSocket to Volco and its pill and popover (spec §8.1). Browser only.
(function (root) {
  'use strict';

  /**
   * @param {object} o
   * @param {{host:string, port:string, reconnect:boolean}} o.settings  initial values (from prefs)
   * @param {() => void} o.onOpen
   * @param {(bytes:Uint8Array) => void} o.onBytes
   * @param {() => void} o.onClose            the socket closed (any reason)
   * @param {(settings:object) => void} o.onSettings  host/port/reconnect changed (to save them)
   */
  function create(o) {
    const $ = id => document.getElementById(id);
    let ws = null, retry = 0, userClosed = true, state = 'demo';
    $('host').value = o.settings.host;
    $('port').value = o.settings.port;
    $('reconnect').checked = o.settings.reconnect;
    const url = () => `ws://${$('host').value.trim() || 'localhost'}:${$('port').value.trim() || '8765'}/`;
    const where = () => `${$('host').value.trim() || 'localhost'}:${$('port').value.trim() || '8765'}`;

    function show(next) {
      state = next;
      $('conn-dot').className = 'dot' + (next === 'open' ? ' ok' : next === 'connecting' ? ' wait' : '');
      $('conn-text').textContent = next === 'open' ? where()
        : next === 'connecting' ? 'Connecting…'
        : next === 'unreachable' ? `Can't reach ${where()}` : 'Not connected';
      $('conn-demo').hidden = next === 'open';
      $('conn-go').textContent = next === 'open' || next === 'connecting' ? 'Disconnect' : 'Connect';
    }

    function connect() {
      clearTimeout(retry);
      userClosed = false;
      if (ws) return;
      show('connecting');
      const sock = new WebSocket(url());
      sock.binaryType = 'arraybuffer';
      ws = sock;
      let opened = false;
      sock.onopen = () => { opened = true; show('open'); o.onOpen(); };
      sock.onmessage = e => o.onBytes(new Uint8Array(e.data));
      sock.onclose = () => {
        if (ws !== sock) return;
        ws = null;
        show(opened || userClosed ? 'demo' : 'unreachable');
        o.onClose();
        if (!userClosed && $('reconnect').checked) retry = setTimeout(connect, 3000);
      };
    }
    /** Drops the socket now, without waiting for the close handshake. */
    function drop() {
      const sock = ws;
      ws = null;
      if (!sock) return;
      sock.onclose = null;
      sock.close();
      show('demo');
      o.onClose();
    }
    function disconnect() {
      userClosed = true;
      clearTimeout(retry);
      drop();
      show('demo');
    }
    /** Like the firmware after a save: drop the link and come back after 1 s, whatever the setting. */
    function reboot() {
      if (!ws && userClosed) return;   // not connected and not trying to be: stays in demo mode
      clearTimeout(retry);
      drop();
      userClosed = false;
      retry = setTimeout(connect, 1000);
    }

    const save = () => o.onSettings({ host: $('host').value.trim(), port: $('port').value.trim(), reconnect: $('reconnect').checked });
    for (const id of ['host', 'port', 'reconnect']) $(id).addEventListener('change', save);
    $('conn-btn').addEventListener('click', e => {
      e.stopPropagation();
      const pop = $('conn-pop');
      pop.hidden = !pop.hidden;
      $('conn-btn').setAttribute('aria-expanded', String(!pop.hidden));
    });
    document.addEventListener('pointerdown', e => {
      if (!$('conn-pop').hidden && !e.target.closest('.conn')) { $('conn-pop').hidden = true; $('conn-btn').setAttribute('aria-expanded', 'false'); }
    });
    $('conn-pop').addEventListener('submit', e => {
      e.preventDefault();
      save();
      if (state === 'open' || state === 'connecting') disconnect(); else connect();
    });
    show('demo');

    return {
      connect, disconnect, reboot,
      isOpen: () => !!ws && ws.readyState === WebSocket.OPEN,
      send(bytes) { if (ws && ws.readyState === WebSocket.OPEN) ws.send(bytes); },
      address: () => url(),
    };
  }

  root.VolcoConnection = { create };
})(globalThis);
```

- [ ] **Step 6: Write `builder.js`**

Create `tools/simulator/builder.js`:

```js
// The builder panel (spec §6.1, §6.4): preset, sliders, buttons and finish, edited as a draft that
// previews live on the board; Apply (or Auto-save) stores it. Browser only; uses layout.js.
(function (root) {
  'use strict';
  const L = root.VolcoLayout;

  const KIND_UI = [
    { kind: L.KIND.NONE, label: 'Plain', svg: '<rect x="3" y="3" width="10" height="10" rx="2.5"></rect>' },
    { kind: L.KIND.LED, label: 'LED', svg: '<circle cx="8" cy="8" r="2.6" fill="currentColor"></circle>' },
    { kind: L.KIND.LED_PAIR, label: 'LED pair', svg: '<circle cx="4.8" cy="8" r="2.3" fill="currentColor"></circle><circle cx="11.2" cy="8" r="2.3"></circle>' },
    { kind: L.KIND.SCREEN, label: 'Screen', svg: '<rect x="1.8" y="4" width="12.4" height="8" rx="1.5"></rect><path d="M4.5 9.5h7"></path>' },
  ];
  const AUTOSAVE_MS = 700;

  /**
   * @param {object} o
   * @param {() => object} o.stored                 the stored layout (layout.fromConfig)
   * @param {() => string|null} o.unavailable       why Apply can't work (no protobufjs), or null
   * @param {(layout:object) => void} o.onPreview   show this layout on the board
   * @param {(layout:object) => string|null} o.onApply  store it; returns an error message or null
   * @param {{autosave:boolean, finish:string}} o.prefs
   * @param {(prefs:object) => void} o.onPrefs
   * @param {(name:string) => void} o.onFinish
   */
  function create(o) {
    const $ = id => document.getElementById(id);
    const list = $('btns');
    let saved = o.stored(), draft = clone(saved), note = '', timer = 0;
    const prefs = { ...o.prefs };

    function clone(l) { return JSON.parse(JSON.stringify(l)); }
    const shape = l => JSON.stringify([l.preset, l.sliders, l.buttons]);
    const dirty = () => shape(draft) !== shape(saved);

    function edit(change) {
      draft = change(clone(draft));
      draft.preset = 'custom';
      note = '';
      changed();
    }
    function changed() {
      o.onPreview(draft);
      render();
      clearTimeout(timer);
      if (prefs.autosave && dirty()) timer = setTimeout(apply, AUTOSAVE_MS);
    }
    function apply() {
      clearTimeout(timer);
      const error = o.onApply(draft);
      if (error) { note = error; render(); }
      // On success the controller reboots and calls reload() with the stored layout.
    }

    function render() {
      const why = o.unavailable();
      $('preset').value = L.PRESETS.some(p => p.id === draft.preset) ? draft.preset : 'custom';
      $('sl-n').textContent = draft.sliders.length;
      $('sl-dec').disabled = draft.sliders.length === 0;
      const addSlider = L.check(draft, l => ({ ...l, sliders: [...l.sliders, {}] }));
      $('sl-inc').disabled = !!addSlider;
      $('sl-inc').title = addSlider || '';
      const addButton = L.check(draft, l => ({ ...l, buttons: [...l.buttons, L.withKind({}, L.KIND.LED)] }));
      $('add').disabled = !!addButton;
      $('add').title = addButton || '';
      list.replaceChildren(...draft.buttons.map((b, i) => row(b, i)));
      $('apply').hidden = $('revert').hidden = prefs.autosave;
      $('apply').disabled = !dirty() || !!why;
      $('revert').disabled = !dirty();
      $('autosave').checked = prefs.autosave;
      $('builder-note').textContent = why || note;
      $('builder-note').hidden = !(why || note);
      document.querySelectorAll('.sw').forEach(s => s.setAttribute('aria-pressed', String(s.dataset.finish === prefs.finish)));
    }

    function row(b, i) {
      const kind = L.kindOf(b);
      const li = document.createElement('li');
      li.draggable = true;
      const grip = Object.assign(document.createElement('span'), { className: 'grip' });
      grip.setAttribute('aria-hidden', 'true');
      const num = Object.assign(document.createElement('span'), { className: 'num', textContent: String(i + 1) });
      const kinds = document.createElement('div');
      kinds.className = 'kinds';
      kinds.setAttribute('role', 'radiogroup');
      kinds.setAttribute('aria-label', `Button ${i + 1}`);
      for (const k of KIND_UI) {
        const btn = document.createElement('button');
        btn.setAttribute('role', 'radio');
        btn.setAttribute('aria-checked', String(k.kind === kind));
        btn.setAttribute('aria-label', k.label);
        const why = k.kind === kind ? null : L.check(draft, l => ({ ...l, buttons: l.buttons.map((x, j) => (j === i ? L.withKind(x, k.kind) : x)) }));
        btn.title = why || k.label;
        btn.disabled = !!why;
        btn.innerHTML = `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4">${k.svg}</svg>`;
        btn.addEventListener('click', () => { if (k.kind !== kind) edit(l => { l.buttons[i] = L.withKind(l.buttons[i], k.kind); return l; }); });
        kinds.append(btn);
      }
      const rm = document.createElement('button');
      rm.className = 'rm';
      rm.title = 'Remove';
      rm.setAttribute('aria-label', `Remove button ${i + 1}`);
      rm.innerHTML = '<svg width="12" height="12" viewBox="0 0 12 12" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><path d="M2.5 2.5l7 7M9.5 2.5l-7 7"></path></svg>';
      rm.addEventListener('click', () => edit(l => { l.buttons.splice(i, 1); return l; }));
      li.addEventListener('dragstart', e => { e.dataTransfer.setData('text/plain', String(i)); e.dataTransfer.effectAllowed = 'move'; li.classList.add('dragging'); });
      li.addEventListener('dragend', () => li.classList.remove('dragging'));
      li.addEventListener('dragover', e => { e.preventDefault(); li.classList.add('over'); });
      li.addEventListener('dragleave', () => li.classList.remove('over'));
      li.addEventListener('drop', e => {
        e.preventDefault();
        li.classList.remove('over');
        const from = +e.dataTransfer.getData('text/plain');
        if (from !== i) edit(l => { l.buttons.splice(i, 0, ...l.buttons.splice(from, 1)); return l; });
      });
      li.append(grip, num, kinds, rm);
      return li;
    }

    $('preset').replaceChildren(...L.PRESETS.map(p => new Option(p.name, p.id)), Object.assign(new Option('Custom', 'custom'), { disabled: true }));
    $('preset').addEventListener('change', e => {
      draft = L.presetLayout(e.target.value, draft.base);
      note = '';
      o.onPreview(draft, { drop: true });
      render();
      clearTimeout(timer);
      if (prefs.autosave && dirty()) timer = setTimeout(apply, AUTOSAVE_MS);
    });
    $('sl-dec').addEventListener('click', () => edit(l => { l.sliders.pop(); return l; }));
    $('sl-inc').addEventListener('click', () => edit(l => { l.sliders.push({}); return l; }));
    $('add').addEventListener('click', () => edit(l => { l.buttons.push(L.withKind({}, L.KIND.LED)); return l; }));
    $('apply').addEventListener('click', apply);
    $('revert').addEventListener('click', () => { draft = clone(saved); note = ''; changed(); });
    $('autosave').addEventListener('change', e => {
      prefs.autosave = e.target.checked;
      o.onPrefs({ autosave: prefs.autosave });
      changed();
    });
    document.querySelectorAll('.sw').forEach(s => s.addEventListener('click', () => {
      prefs.finish = s.dataset.finish;
      o.onPrefs({ finish: prefs.finish });
      o.onFinish(prefs.finish);
      render();
    }));
    render();

    return {
      /** The stored config changed (Apply, Volco's write, Clear): it becomes the draft. */
      reload(byVolco = false) {
        const wasDirty = dirty();
        saved = o.stored();
        draft = clone(saved);
        note = byVolco && wasDirty ? 'Volco saved a new layout; your draft was replaced.' : '';
        clearTimeout(timer);
        render();
      },
      draft: () => draft,
      isDirty: dirty,
    };
  }

  root.VolcoBuilder = { create };
})(globalThis);
```

- [ ] **Step 7: Write `devtools.js`**

Create `tools/simulator/devtools.js`:

```js
// The developer drawer (spec §10): the frame log, the stored config, and fault injection.
// Browser only; uses protocol.js for frame names.
(function (root) {
  'use strict';
  const P = root.VolcoProtocol;
  const KEEP = 400, SHOW = 160;
  const KIND_NAMES = ['plain', 'LED', 'LED pair', 'screen'];
  const CODES = ['OK', 'BAD_SEQUENCE', 'BAD_CRC', 'TOO_LARGE', 'DECODE_FAILED', 'INVALID'];

  /**
   * @param {object} o
   * @param {() => object} o.config      { state, crc, size, preset, sliders, kinds, reason, memoryOnly, unavailable }
   * @param {() => string} o.configJson  the stored config as protobuf JSON
   * @param {() => void} o.onClearConfig
   * @param {() => void} o.onReboot
   * @param {object} o.link              link.js (faults, corruptNext, corruptArmed)
   * @param {(code:number|null) => void} o.forceNextResult
   * @param {{drawerOpen:boolean, drawerTab:string}} o.prefs
   * @param {(prefs:object) => void} o.onPrefs
   * @param {() => void} o.onToggle      the drawer opened or closed (to re-centre the board)
   */
  function create(o) {
    const $ = id => document.getElementById(id);
    const rows = [];
    let paused = false, dirty = true, t0 = performance.now(), connected = false;

    function push(r) {
      if (paused) return;
      rows.unshift(r);
      if (rows.length > KEEP) rows.pop();
      dirty = true;
    }
    function render() {
      const pane = $('pane-frames');
      if (!dirty || $('drawer').hidden || pane.hidden) return;
      dirty = false;
      const hideKa = $('hide-ka').checked;
      const shown = rows.filter(r => !(hideKa && r.payload && (r.payload[0] === P.MSG.ALIVE || r.payload[0] === P.MSG.HEARTBEAT))).slice(0, SHOW);
      if (!shown.length) {
        const e = Object.assign(document.createElement('div'), { className: 'empty' });
        e.textContent = connected ? 'Waiting for frames.' : 'Frames appear here once Volco connects.';
        pane.replaceChildren(e);
        return;
      }
      pane.replaceChildren(...shown.map(r => {
        const el = Object.assign(document.createElement('div'), { className: 'r' + (r.bad ? ' bad' : '') });
        const cell = (cls, text) => Object.assign(document.createElement('span'), { className: cls, textContent: text });
        el.append(cell('t', ((r.t - t0) / 1000).toFixed(3)), cell(r.dir === '←' ? 'in' : r.dir === '→' ? 'out' : 't', r.dir),
          cell('n', r.text ? '' : P.nameOf(r.payload[0]) + (r.bad ? ' ✕' : '')), cell('x', r.text || P.hex(r.payload)));
        return el;
      }));
    }
    setInterval(render, 200);

    function renderConfig() {
      const c = o.config();
      $('cfg-state').textContent = c.unavailable ? 'Unavailable: protobufjs did not load (offline?)'
        : c.state === 0 ? 'None' : c.state === 1 ? 'Valid' : `Invalid: ${c.reason}`;
      $('cfg-size').textContent = `${c.size} bytes`;
      $('cfg-crc').textContent = c.state === 0 ? '—' : c.crc.toString(16).toUpperCase().padStart(8, '0');
      $('cfg-preset').textContent = c.preset || '—';
      $('cfg-hw').textContent = `${c.sliders} sliders · ${c.kinds.map(k => KIND_NAMES[k]).join(', ') || 'no buttons'}`;
      $('cfg-memory-row').hidden = !c.memoryOnly;
    }

    // Frames tab
    $('hide-ka').addEventListener('change', () => { dirty = true; render(); });
    $('pause').addEventListener('click', () => { paused = !paused; $('pause').textContent = paused ? 'Resume' : 'Pause'; });
    $('clear-log').addEventListener('click', () => { rows.length = 0; dirty = true; render(); });
    // Config tab
    $('cfg-clear').addEventListener('click', () => o.onClearConfig());
    $('cfg-copy').addEventListener('click', async e => {
      try { await navigator.clipboard.writeText(o.configJson()); e.target.textContent = 'Copied'; }
      catch { e.target.textContent = 'Copy failed'; }
      setTimeout(() => (e.target.textContent = 'Copy as JSON'), 1200);
    });
    // Faults tab
    $('f-silent').addEventListener('change', e => { o.link.faults.silent = e.target.checked; });
    $('f-commit').addEventListener('change', e => { o.link.faults.ignoreCommit = e.target.checked; });
    $('f-corrupt').addEventListener('click', () => { o.link.corruptNext(); syncCorrupt(); });
    $('f-cfg').replaceChildren(new Option('Normal', ''), ...CODES.map((c, i) => new Option(c, String(i))));
    $('f-cfg').addEventListener('change', e => o.forceNextResult(e.target.value === '' ? null : +e.target.value));
    $('f-reboot').addEventListener('click', () => o.onReboot());
    function syncCorrupt() {
      const armed = o.link.corruptArmed();
      $('f-corrupt').textContent = armed ? 'Armed' : 'Arm';
      $('f-corrupt').classList.toggle('armed', armed);
    }

    // Drawer and tabs
    function showTab(id) {
      for (const t of ['frames', 'config', 'faults']) {
        $(`tab-${t}`).setAttribute('aria-selected', String(t === id));
        $(`pane-${t}`).hidden = t !== id;
      }
      $('frame-tools').style.visibility = id === 'frames' ? 'visible' : 'hidden';
      if (id === 'config') renderConfig();
      dirty = true; render();
      o.onPrefs({ drawerTab: id });
    }
    function setOpen(open) {
      $('drawer').hidden = !open;
      $('dev-btn').setAttribute('aria-expanded', String(open));
      o.onPrefs({ drawerOpen: open });
      dirty = true; render();
      o.onToggle();
    }
    $('dev-btn').addEventListener('click', () => setOpen($('drawer').hidden));
    for (const t of ['frames', 'config', 'faults']) $(`tab-${t}`).addEventListener('click', () => showTab(t));
    showTab(o.prefs.drawerTab || 'frames');
    setOpen(!!o.prefs.drawerOpen);

    return {
      /** One frame: dir '←' from Volco, '→' to Volco. */
      frame(dir, payload, bad = false) { push({ t: performance.now(), dir, payload, bad }); if (bad) syncCorrupt(); },
      note(text) { push({ t: performance.now(), dir: '·', text }); },
      /** The stored config changed. */
      refreshConfig: renderConfig,
      setConnected(on) {
        connected = on;
        if (on) t0 = performance.now();
        dirty = true;
      },
      /** The forced config answer was used: show "Normal" again. */
      resetForcedResult() { $('f-cfg').value = ''; },
    };
  }

  root.VolcoDevtools = { create };
})(globalThis);
```

- [ ] **Step 8: Write `app.js`**

Create `tools/simulator/app.js`:

```js
// Wires the simulator together (spec §9): the stored config, the link to Volco, the screens and
// LEDs, demo mode, the builder, the connection, the drawer, and the 3D scene once three.js is in.
(function (root) {
  'use strict';
  const P = root.VolcoProtocol, L = root.VolcoLayout, O = root.VolcoOled, D = root.VolcoDemo;
  const $ = id => document.getElementById(id);
  const now = () => performance.now();

  const CONFIG_KEY = 'volco-sim-config', PREFS_KEY = 'volco-sim-prefs';
  const DEFAULT_PREFS = { host: 'localhost', port: '8765', reconnect: true, autoConnect: false, autosave: false,
    finish: 'silver', panelOpen: true, drawerOpen: false, drawerTab: 'frames' };
  const SLIDER_NAMES = ['Master', 'Spotify', 'Discord', 'Firefox', 'Game', 'Chrome', 'Zoom', 'Steam'];
  const BOARD = { x: 30, y: 3, z: 0 };   // where device.js puts the board; the camera's target
  const BLANK = new Uint8Array(1024);
  const SLIDER_MS = 20, REBOOT_MS = 1000, TICK_MS = 50;

  // ── Storage (spec §11): every access guarded; without storage, values live in memory ──
  let memoryOnly = false;
  function read(key) { try { return localStorage.getItem(key); } catch { memoryOnly = true; return null; } }
  function write(key, value) {
    try { if (value === null) localStorage.removeItem(key); else localStorage.setItem(key, value); } catch { memoryOnly = true; }
  }
  const prefs = { ...DEFAULT_PREFS };
  try { Object.assign(prefs, JSON.parse(read(PREFS_KEY) || '{}')); } catch { /* damaged prefs: defaults */ }
  const savePrefs = p => { Object.assign(prefs, p); write(PREFS_KEY, JSON.stringify(prefs)); };
  let stored = null;
  try { const t = read(CONFIG_KEY); stored = t ? Uint8Array.from(atob(t), c => c.charCodeAt(0)) : null; } catch { stored = null; }
  function store(blob) {
    stored = blob && blob.length ? Uint8Array.from(blob) : null;
    write(CONFIG_KEY, stored ? btoa(String.fromCharCode(...stored)) : null);
  }

  // ── State ──
  let mode = 'demo';            // 'demo' (no socket) or 'live'
  let rebooting = false, openWhileRebooting = false;
  let hw, beh, slots = [], leds = [], panels, demoHost;
  let boardKinds = [];          // what the 3D board shows: the hardware, or the builder's draft
  let output = { device: 'speakers', muted: false };
  let stage = null, room = null, device = null, builder = null, devtools = null, connection = null;

  // ── Config session and link ──
  let wroteByVolco = false;
  const session = root.protobuf && root.VolcoSimConfig ? root.VolcoSimConfig.createSession({
    protobuf: root.protobuf, descriptor: root.VOLCO_CONFIG_DESCRIPTOR,
    send: p => link.sendPayload(p, now()),
    store: blob => { store(blob); wroteByVolco = true; },
    stored: () => stored,
    reboot: () => setTimeout(() => reboot(true), 50),   // after the OK has gone out
  }) : null;

  /** The stored config as the board runs it: none or damaged boots the DIY reference (spec §12). */
  function current() {
    const c = session ? session.current() : { state: 0, crc: 0, config: null, reason: null };
    const config = c.config || L.DIY_REFERENCE;
    const layout = L.fromConfig(config);
    return { ...c, config, layout, sliders: layout.sliders.length, kinds: L.kinds(layout), size: stored ? stored.length : 0 };
  }

  const link = root.VolcoLink.createLink({
    sendFrame(bytes, payload, bad) {
      connection.send(bytes);
      devtools.frame('→', payload, bad);
      if (payload[0] === P.MSG.CONFIG_RESULT) devtools.resetForcedResult();
    },
    sliderValue: i => Math.round((device ? device.faderValue(i) : 0.5) * 4095),
    onLed: (b, v) => show({ button: b, led: v }),
    onScreen: (b, transition, content, t) => show({ button: b, transition, content }, t),
    onHostLost: t => blankDisplays(t),
    onConfigFrame: (p, t) => (session ? session.onFrame(p, t) : false),
  });
  const parser = P.createParser(
    p => { devtools.frame('←', p); link.onPayload(p, now()); },
    () => devtools.note('Dropped a frame with a bad checksum'));

  // ── Displays ──
  function show(u, t = now()) {
    if (u.led !== undefined) {
      leds[u.button] = u.led;
      if (boardKinds[u.button] === hw.kinds[u.button]) device?.setLed(u.button, u.led);
      return;
    }
    const slot = slots[u.button];
    if (!slot) return;
    slot.show(u.content, u.transition, t, panels.power(t) === O.POWER.OFF);
    if (mode === 'live') {
      const r = D.outputFromScreen(u.content);   // spec §7.3: the waves follow output screens
      if (r) { output = r; room?.setOutput(r.device, r.muted); }
    }
  }
  function blankDisplays(t = now()) {
    hw.kinds.forEach((k, i) => {
      if (k === P.KIND.LED || k === P.KIND.LED_PAIR) show({ button: i, led: 0 }, t);
      if (slots[i]) slots[i].show(O.NOT_CONNECTED, P.TRANSITION.NONE, t, false);
    });
  }
  function applyLeds() {
    boardKinds.forEach((k, i) => {
      if ((k === P.KIND.LED || k === P.KIND.LED_PAIR) && hw.kinds[i] === k) device?.setLed(i, leds[i] ?? 0);
    });
  }
  function renderScreens(t) {
    if (!device) return;
    const power = panels.power(t);
    const level = rebooting || power === O.POWER.OFF ? 0
      : 0.25 + 0.75 * (power === O.POWER.DIM ? beh.dimBrightness : beh.brightness) / 255;
    boardKinds.forEach((k, i) => {
      if (k !== P.KIND.SCREEN) return;
      const frame = !rebooting && hw.kinds[i] === P.KIND.SCREEN && slots[i] ? O.frame(slots[i], panels, t, beh.scroll) : BLANK;
      device.setScreen(i, frame, level);
    });
  }

  // ── Boot, demo mode, reboot ──
  function boot({ drop = false } = {}) {
    hw = current();
    beh = O.behaviour(hw.config.screens);
    link.setHardware({ sliders: hw.sliders, kinds: hw.kinds, configState: hw.state, configCrc: hw.crc });
    slots = []; leds = [];
    hw.kinds.forEach((k, i) => {
      if (k !== P.KIND.SCREEN) return;
      slots[i] = O.createSlot(beh.transitionMs);
      slots[i].show(O.NOT_CONNECTED, P.TRANSITION.NONE, now(), false);
    });
    panels = O.createPanels(beh.panels);
    panels.activity(now());
    boardKinds = hw.kinds;
    device?.build({ sliders: hw.sliders, kinds: hw.kinds }, { drop });
    if (connection?.isOpen()) goLive(); else enterDemo();
  }
  function enterDemo() {
    mode = 'demo';
    demoHost = D.createDemoHost(hw.kinds);
    for (const u of demoHost.initial()) show(u);
    output = { device: demoHost.state.output, muted: demoHost.state.outputMuted };
    room?.setOutput(output.device, output.muted);
    applyLeds();
  }
  function goLive() {
    mode = 'live';
    blankDisplays();
    output = { device: 'speakers', muted: false };
    room?.setOutput('speakers', false);
    link.begin(now());   // the firmware's boot Hello
  }
  function reboot(byVolco = false) {
    if (rebooting) return;
    rebooting = true;
    device?.setStatus(false);
    devtools.note(byVolco ? 'Volco saved a config: rebooting' : 'Rebooting');
    connection.reboot();
    setTimeout(() => {
      rebooting = false;
      device?.setStatus(true);
      boot();
      builder.reload(byVolco && wroteByVolco);
      wroteByVolco = false;
      devtools.refreshConfig();
      if (openWhileRebooting) { openWhileRebooting = false; if (connection.isOpen()) goLive(); }
    }, REBOOT_MS);
  }

  // ── Input from the board ──
  const lastSlider = [], pendingSlider = [];
  function onSlider(i, value) {
    panels.activity(now());
    room?.showOverlay(mode === 'demo' ? SLIDER_NAMES[i % SLIDER_NAMES.length] : `Slider ${i + 1}`, value);
    if (mode !== 'live' || rebooting || i >= hw.sliders) return;
    const send = () => { lastSlider[i] = now(); link.sendSlider(i, Math.round(value * 4095), now()); };
    clearTimeout(pendingSlider[i]);
    if (now() - (lastSlider[i] || 0) >= SLIDER_MS) send();
    else pendingSlider[i] = setTimeout(send, SLIDER_MS);   // the last position always goes out
  }
  function onButton(i, pressed) {
    const real = i < hw.kinds.length && boardKinds[i] === hw.kinds[i] && !rebooting;
    if (!real) return;   // a draft-only button: the controller doesn't have it yet
    if (!panels.filterButton(i, pressed, now())) return;   // the press that woke the screens
    if (mode === 'live') { link.sendButton(i, pressed, now()); return; }
    if (!pressed) return;
    for (const u of demoHost.press(i)) show(u);
    room?.setOutput(demoHost.state.output, demoHost.state.outputMuted);
    const text = demoHost.overlay(i);
    if (text) room?.showOverlay(text, -1);
  }

  // ── Layout of the floating panels: keep the board centred in the free space ──
  function frameView() {
    if (!stage) return;
    const wide = innerWidth >= 720, panelOpen = !$('panel').hidden, r = $('panel').getBoundingClientRect();
    const x = panelOpen && wide ? (r.width + 16) / 2 : 0;
    const y = panelOpen && !wide ? (r.height + 12) / 2 : !$('drawer').hidden && wide ? 130 : 0;
    stage.setOffset(x, y);
  }
  function setPanel(open) {
    $('panel').hidden = !open;
    $('toggle').setAttribute('aria-expanded', String(open));
    savePrefs({ panelOpen: open });
    frameView();
  }

  // ── Start ──
  devtools = root.VolcoDevtools.create({
    config: () => { const c = current(); return { ...c, preset: c.config.preset, memoryOnly, unavailable: !session }; },
    configJson: () => JSON.stringify(session?.current().config ?? {}, null, 2),
    onClearConfig: () => { store(null); reboot(); },
    onReboot: () => reboot(),
    link,
    forceNextResult: code => session?.forceNextResult(code),
    prefs, onPrefs: savePrefs, onToggle: frameView,
  });
  connection = root.VolcoConnection.create({
    settings: prefs,
    onOpen: () => {
      savePrefs({ autoConnect: true });
      devtools.setConnected(true);
      devtools.note(`Connected to ${connection.address()}`);
      if (rebooting) openWhileRebooting = true; else goLive();
    },
    onBytes: bytes => parser.feed(bytes),
    onClose: () => {
      devtools.setConnected(false);
      if (!rebooting) { devtools.note('Disconnected'); enterDemo(); }
    },
    onSettings: savePrefs,
  });
  boot();
  builder = root.VolcoBuilder.create({
    stored: () => current().layout,
    unavailable: () => (session ? null : 'Saving needs protobufjs, which did not load (offline?).'),
    onPreview(layout, { drop = false } = {}) {
      boardKinds = L.kinds(layout);
      device?.build({ sliders: layout.sliders.length, kinds: boardKinds }, { drop });
      applyLeds();
    },
    onApply(layout) {
      const r = L.toConfig(layout);
      if (r.error) return r.error.message;
      store(session.encode(r.config));
      reboot();
      return null;
    },
    prefs, onPrefs: savePrefs,
    onFinish: name => device?.setFinish(name),
  });
  $('toggle').addEventListener('click', () => setPanel($('panel').hidden));
  setPanel(prefs.panelOpen);
  new ResizeObserver(frameView).observe($('panel'));
  addEventListener('resize', frameView);
  setInterval(() => {
    const t = now();
    session?.tick(t);
    if (mode === 'live' && !rebooting) link.tick(t);
  }, TICK_MS);
  if (prefs.autoConnect) connection.connect();

  function startScene() {
    try {
      stage = root.VolcoStage.create($('stage'), {
        target: BOARD,
        onFrame(t, dt) { room.update(t, dt); device.update(t, dt); renderScreens(t); },
      });
    } catch {
      showSceneFailure();
      return;
    }
    room = root.VolcoRoom.build(stage);
    device = root.VolcoDevice.create(stage, { onSlider, onButton });
    device.setFinish(prefs.finish);
    if (builder.isDirty()) builder.reload();   // a draft started before the scene loaded: start from the stored board
    boardKinds = hw.kinds;
    device.build({ sliders: hw.sliders, kinds: hw.kinds }, { drop: true });
    applyLeds();
    room.setOutput(output.device, output.muted);
    frameView();
    document.fonts.ready.then(() => device.setFinish(prefs.finish));   // the wordmark's font
  }
  function showSceneFailure() { $('scene-failed').hidden = false; }
  if (root.VolcoThree) startScene();
  else {
    addEventListener('volco-three', startScene, { once: true });
    addEventListener('volco-three-failed', showSceneFailure, { once: true });
  }
})(globalThis);
```

- [ ] **Step 9: Save the end-to-end check (not committed)**

Save this as a scratch file, for example `<scratchpad>/e2e.cjs`. It needs `playwright-core` with a Chromium. On this machine it is at `/root/.npm/_npx/e41f203b7505f1fb/node_modules/playwright-core`; elsewhere, set `PLAYWRIGHT` to the module's path. It also uses `tools/simulator/test/node_modules` (run `npm ci` there first).

```js
// End-to-end check of tools/simulator/index.html in headless Chromium, with a minimal WebSocket
// server playing Volco. Not committed: needs Playwright and a browser.
//   node e2e.cjs <repo>/tools/simulator   (PLAYWRIGHT=<path to playwright-core> to override)
const http = require('http'), crypto = require('crypto'), path = require('path');
const { chromium } = require(process.env.PLAYWRIGHT || '/root/.npm/_npx/e41f203b7505f1fb/node_modules/playwright-core');
const dir = path.resolve(process.argv[2]);
const P = require(path.join(dir, 'protocol.js'));
const L = require(path.join(dir, 'layout.js'));
const sim = require(path.join(dir, 'config.js'));
const protobuf = require(path.join(dir, 'test', 'node_modules', 'protobufjs', 'light'));
const Config = protobuf.Root.fromJSON(require(path.join(dir, 'volco_config_descriptor.js'))).lookupType('volco.config.ControllerConfig');

let failures = 0;
const check = (ok, what) => { console.log(`${ok ? 'PASS' : 'FAIL'} ${what}`); if (!ok) failures++; };
const wait = ms => new Promise(r => setTimeout(r, ms));

// ── A minimal WebSocket server (binary frames only) ──
const got = [];
let sock = null;
const parser = P.createParser(p => got.push(p));
const send = payload => { const f = P.frame(payload); sock.write(Buffer.concat([Buffer.from([0x82, f.length]), Buffer.from(f)])); };
const server = http.createServer();
server.on('upgrade', (req, s) => {
  const key = crypto.createHash('sha1').update(req.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  s.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${key}\r\n\r\n`);
  sock = s;
  let buf = Buffer.alloc(0);
  s.on('data', d => {
    buf = Buffer.concat([buf, d]);
    while (buf.length >= 2) {
      let len = buf[1] & 127, off = 2;
      if (len === 126) { len = buf.readUInt16BE(2); off = 4; }
      if (buf.length < off + 4 + len) return;
      const mask = buf.slice(off, off + 4), data = Buffer.from(buf.slice(off + 4, off + 4 + len));
      for (let i = 0; i < len; i++) data[i] ^= mask[i % 4];
      if ((buf[0] & 15) === 2) parser.feed(data);
      buf = buf.slice(off + 4 + len);
    }
  });
  s.on('error', () => {});
});
server.listen(8765);

(async () => {
  const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
  const page = await browser.newPage({ viewport: { width: 1100, height: 690 } });
  const errors = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', e => errors.push(String(e)));
  await page.goto('file://' + path.join(dir, 'index.html'));
  await page.waitForFunction(() => !!window.VolcoThree, null, { timeout: 60000 });
  await wait(3000);
  check(await page.isVisible('#conn-demo'), 'starts in demo mode');
  check(!(await page.isVisible('#scene-failed')), 'the 3D scene loaded');

  // Auto-save: three quick edits are one save and one reboot; disconnected, it stays in demo mode.
  await page.click('#dev-btn');
  await page.check('#autosave');
  await page.evaluate(() => { for (let i = 0; i < 3; i++) document.getElementById('sl-inc').click(); });   // within 0.7 s
  await wait(2500);
  const notes = await page.$$eval('#pane-frames .r .x', xs => xs.map(x => x.textContent));
  check(notes.filter(n => n === 'Rebooting').length === 1, 'three quick edits cause one reboot');
  check(await page.textContent('#sl-n') === '8', 'the saved layout has 8 sliders');
  check(await page.textContent('#conn-text') === 'Not connected', 'a reboot while disconnected stays in demo mode');
  await page.uncheck('#autosave');

  // Connect: Hello reports the stored config and its hardware, then the slider positions.
  await page.click('#conn-btn');
  await page.click('#conn-go');
  await wait(1500);
  const hello = got.find(p => p[0] === P.MSG.HELLO);
  check(!!hello && hello[7] === 1 && hello[12] === 8, 'Hello: a stored config, 8 sliders');
  check(got.filter(p => p[0] === P.MSG.SLIDER).length === 8, 'every slider position follows Hello');

  // Volco writes the Screen controller preset: OK, reboot, a new Hello with four screens.
  send([P.MSG.HEARTBEAT]);
  const blob = Config.encode(Config.fromObject(L.toConfig(L.presetLayout('screens')).config)).finish();
  const crc = sim.crc32(blob);
  send([0x88, 9, blob.length >> 8, blob.length & 255, crc >>> 24, (crc >>> 16) & 255, (crc >>> 8) & 255, crc & 255]);
  send([0x89, 9, 0, 0, ...blob]);
  got.length = 0;
  send([0x8A, 9]);
  await wait(400);
  check(P.hex(got.find(p => p[0] === P.MSG.CONFIG_RESULT) || []) === '06 09 00 ff ff', 'the write is answered OK');
  got.length = 0;
  await wait(3500);
  const hello2 = got.find(p => p[0] === P.MSG.HELLO);
  check(!!hello2 && P.hex(hello2.slice(-6)) === '05 04 03 03 03 03', 'after the reboot Hello reports 5 sliders and 4 screens');
  check(await page.$$eval('#btns li', li => li.length) === 4, 'the builder shows the new layout');

  // Faults: the next frame's checksum is corrupted; the fake Volco drops it.
  await page.click('#tab-faults');
  await page.click('#f-corrupt');
  got.length = 0;
  send([P.MSG.HELLO_REQUEST]);   // asks for a Hello: the first frame of the answer goes out corrupted
  await wait(500);
  check(!got.some(p => p[0] === P.MSG.HELLO), 'a corrupted Hello is dropped by the receiver');
  check(got.filter(p => p[0] === P.MSG.SLIDER).length === 5, 'the frames after it arrive intact');

  check(errors.length === 0, `no console errors${errors.length ? ': ' + errors.join(' | ') : ''}`);
  await browser.close();
  server.close();
  console.log(failures ? `${failures} FAILED` : 'ALL PASSED');
  process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
```

- [ ] **Step 10: Run the end-to-end check**

Run: `node <scratchpad>/e2e.cjs tools/simulator` (port 8765 must be free, so quit Volco first).
Expected: 13 `PASS` lines, then `ALL PASSED`. It takes about a minute with software rendering. A `FAIL` names the behaviour; fix the code, not the check.

- [ ] **Step 11: Look at it**

Take one screenshot to review the look, with the same Playwright launch arguments as `e2e.cjs` and a 1100×690 viewport, after waiting for `window.VolcoThree` and 10 s. Compare it with the approved mock: the desk scene, the board centred left of the builder panel, the pill reading "Not connected · Demo" at the top left, and the `<>` button at the bottom left.

- [ ] **Step 12: Commit**

```bash
/usr/bin/git add tools/simulator/index.html tools/simulator/stage.js tools/simulator/room.js tools/simulator/device.js tools/simulator/connection.js tools/simulator/builder.js tools/simulator/devtools.js tools/simulator/app.js
/usr/bin/git commit -m "Rebuild the simulator as a 3D desk scene with a builder, demo mode and developer tools"
```

---

### Task 10: Docs and CI

**Files:**
- Rewrite: `tools/simulator/README.md`
- Modify: `CLAUDE.md`, `firmware/README.md`, `.github/workflows/build.yml`, `tools/simulator/test/package.json`

- [ ] **Step 1: Rewrite the simulator README**

Replace `tools/simulator/README.md` entirely with:

````markdown
# Volco Simulator

A Volco controller on a desk, in 3D, that speaks the real protocol: test the app without hardware, try a hardware layout before building it, or just show Volco off. The design is in `docs/superpowers/specs/2026-10-07-simulator-redesign-design.md`.

## Use it

1. Open `index.html` in a browser, straight from disk. There is no build step; three.js and protobufjs load from jsDelivr, so the first load needs a network connection.
2. In Volco, open Settings → Connection, choose **WebSocket**, port `8765`.
3. In the simulator, click the pill at the top left and **Connect**.

Until Volco connects, the board runs in **demo mode** (the pill says *Demo*): it answers its own presses the way Volco would, so you can play with it without the app.

## Controls

| | |
|---|---|
| Left button | Drag a fader cap; press a key |
| Right button | Rotate the view |
| Wheel or pinch | Zoom toward the board |
| Top-right button | Show or hide the builder |
| `<>` (bottom left) | Developer tools |

After 8 seconds without input the camera drifts a little; any input stops it.

## Builder

The panel edits the controller's hardware: a preset (DIY reference, Screen controller, Mixed), the number of sliders, and each button's kind (plain, LED, LED pair, screen). Drag a row by its grip to reorder buttons. Edits show on the board at once but stay a draft until **Apply**; **Revert** drops them. With **Auto-save** on, each edit is saved after 0.7 s of quiet.

Saving stores a real controller config, the same protobuf the firmware keeps, and the simulated controller reboots into it (the screens go dark for a second and the link to Volco drops and comes back). Pins are allocated automatically from the ESP32's pin table, so Volco's Controller page accepts what it reads back. A control greys out when the chip has no pin left for it, with the reason in its tooltip. Pins themselves are edited in Volco; a config Volco writes keeps its pins.

If Volco writes a config while you have an unsaved draft, Volco's config wins and the panel says so.

## Sound waves

The selected output device, the speakers or the headphones, sends out soft rings. While Volco is connected the simulator learns the output only from what Volco shows on the screens: a toggle's `NEXT:` screen, an `ACTIVE` screen, or a mute screen for the current output, by its icon. With LED-only layouts it can't know, so the speakers play. In demo mode it knows exactly.

## Developer tools

- **Frames**: every frame in both directions, newest first, with its name and bytes. Keepalives are hidden unless you untick the box.
- **Config**: the stored config's state, size, CRC, preset and hardware; copy it as JSON (the shape of `proto/presets/*.json`) or clear it.
- **Faults**: go silent (Volco sees the controller time out), corrupt the next frame's checksum, answer the next config write with a chosen code, ignore Commit (updates then apply after 500 ms), or reboot.

## Firmware fidelity

`link.js` mirrors the firmware's link: Hello at connect, on HelloRequest and when the PC comes back after a silence, each followed by the slider positions; Alive after 1 s without sending; host loss after 3 s (LEDs off, screens show NOT CONNECTED); Begin/Commit with a 500 ms fallback. `oled.js` is a port of the firmware's renderer and uses the firmware's own fonts (`fonts.js`), so screens are pixel-identical, including right-to-left text and scrolling; dimming, switching off, pixel shift and the swallowed wake press follow the stored config's screen behaviour.

One deliberate difference: a stored config that fails validation boots the DIY reference layout (the firmware would boot with no inputs), so the board stays usable. Hello still reports the config as damaged.

## Files

| File | |
|---|---|
| `index.html` | Markup, styles, script order, and the module that loads three.js |
| `protocol.js`, `link.js`, `config.js`, `layout.js`, `oled.js`, `demo.js` | The logic, with no DOM; tested under Node |
| `stage.js`, `room.js`, `device.js` | The 3D scene, the room, the controller |
| `connection.js`, `builder.js`, `devtools.js`, `app.js` | The WebSocket, the panels, and the wiring |
| `fonts.js`, `icons.js` | Generated by `firmware/tools/gen_assets.py`; do not edit |
| `volco_config_descriptor.js` | Generated by `test/gen_descriptor.mjs` from `proto/volco_config.proto` |

## Tests

```bash
cd tools/simulator/test && npm ci && node --test
```

They check the protocol against `firmware/test/golden/frames.txt`, the screens pixel for pixel against `firmware/test/golden/render.txt` (produced by the firmware's renderer), every allocated pin against the app's validator rules, and that `fonts.js` and `icons.js` match the firmware's tables. After a proto change, run `node gen_descriptor.mjs`; CI checks the descriptor is current.

The 3D scene has no automated tests. Before merging a change to it, check by hand:

- [ ] The page loads with no console errors, in demo mode.
- [ ] Left-drag moves a fader; right-drag rotates; the wheel zooms toward the board.
- [ ] Pressing the output key (or the LED pair) moves the waves between speakers and headphones.
- [ ] Leaving the tab for a while and coming back doesn't start the camera drifting.
- [ ] A builder edit shows on the board at once; Apply reboots it; Revert undoes the draft.
- [ ] Connected to Volco, the frame log shows traffic both ways, and each fault does what it says.
````

- [ ] **Step 2: Update `CLAUDE.md`**

1. In the Build & Test section, change "`gen_assets.py` regenerates the firmware icons and fonts, the C# `ScreenIcon` enum and the simulator's `icons.js` from `firmware/assets`" to "`gen_assets.py` regenerates the firmware icons and fonts, the C# `ScreenIcon` enum and the simulator's `icons.js` and `fonts.js` from `firmware/assets`".
2. Replace the paragraph under `### Web Simulator (\`tools/simulator/\`)` with:

   ```markdown
   `index.html` is a 3D controller simulator (three.js; a home-office desk) that speaks the full protocol and config session to Volco over WebSocket. Open it directly in a browser; there is no build step: three.js (pinned 0.170.0, through an import map and one inline module that publishes `window.VolcoThree`) and protobufjs load from jsDelivr, and every other file is a classic script that also loads under Node. The logic has no DOM: `protocol.js`, `link.js` (the firmware Link, with fault hooks), `config.js` (the config session; the blob is kept in `localStorage`), `layout.js` (the builder's model, presets, and ESP32 pin allocation that passes the app's validator), `oled.js` (a port of `volco_render`, `volco_bidi` and `volco_screen_state`, pixel-identical through `fonts.js`) and `demo.js` (demo mode, and which output a Volco screen shows, for the sound waves). The scene is `stage.js`, `room.js` and `device.js`; the UI is `connection.js`, `builder.js` (a draft with Apply and Auto-save) and `devtools.js` (frames, config, faults), wired by `app.js`. While no WebSocket is open the board runs in demo mode. `tools/simulator/test` holds the Node tests (`npm ci && node --test`), run by the `simulator-test` CI job; they use `firmware/test/golden/frames.txt` and `render.txt` (layout-1 bitmaps from the firmware renderer; regenerate with `VOLCO_WRITE_GOLDEN=1 pio test -e native -f test_render`), and `gen_descriptor.mjs` regenerates `volco_config_descriptor.js` after a proto change.
   ```

- [ ] **Step 3: Update `firmware/README.md`**

Replace "and also writes the C# icon enum and the simulator's icon data." with "and also writes the C# icon enum and the simulator's icon and font data (`tools/simulator/icons.js`, `fonts.js`). `firmware/test/golden/render.txt` holds layout-1 renders that the simulator's tests compare against; after a renderer change, regenerate it with `VOLCO_WRITE_GOLDEN=1 pio test -e native -f test_render`."

- [ ] **Step 4: Update CI and the test package**

In `.github/workflows/build.yml`, rename the `simulator-test` job's last step from `- name: Config session tests` to `- name: Simulator tests` (its `run: node --test` stays; it already picks up every `*.test.mjs`). In `tools/simulator/test/package.json`, change `"description"` to `"Node tests of the web simulator (tools/simulator)"`.

- [ ] **Step 5: Run everything**

Run: `cd tools/simulator/test && node --test && cd ../../../firmware && pio test -e native`
Expected: 58 Node tests pass, and every native test passes.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git add tools/simulator/README.md CLAUDE.md firmware/README.md .github/workflows/build.yml tools/simulator/test/package.json
/usr/bin/git commit -m "Document the new simulator; CI step name"
```

---

### Task 11: CI

- [ ] **Step 1: Push and run the tests on CI**

```bash
/usr/bin/git push -u origin worktree-simulator_new
gh workflow run build.yml --ref worktree-simulator_new
gh run list --branch worktree-simulator_new --limit 3
```
Expected: the run's `firmware-test`, `simulator-test` and test jobs succeed. If `simulator-test` fails on the descriptor check, run `node gen_descriptor.mjs` in `tools/simulator/test`, then commit and push.

- [ ] **Step 2: Hand over**

Report the run's result, and remind the user that the README's manual checklist (the 3D scene) needs one pass by hand on a real GPU, connected to Volco.

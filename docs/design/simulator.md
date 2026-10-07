# Volco simulator

Date: 2026-10-07. Status: implemented.

Approved mocks:
- 3D desk scene with the builder, connection and developer drawer: https://claude.ai/artifact/3TYSN1q43FShoyfNBNNRth
- Earlier flat device directions (stashed, not used): https://claude.ai/artifact/RRsz6WZD3n63TpEwRYvvtk

## 1. Goal

Replace `tools/simulator/index.html` with a simulator that:

- renders the controller as a realistic 3D device on a desk in a warm home office;
- lets you build a controller (sliders and buttons of any kind) or start from a preset;
- speaks the full wire protocol and config session to Volco over WebSocket, exactly like the firmware;
- serves equally as a development tool (frame log, config state, fault injection) and as a showcase (demo mode while Volco is not connected).

The UI is light, minimal and almost label-free. There is no dark theme and nothing glows except what is physically lit: the OLEDs and the LEDs.

## 2. Locked decisions

| # | Decision |
|---|---|
| D1 | A real-time WebGL scene (three.js), not flat CSS. |
| D2 | The scene is a cozy home-office desk: an oak desk against warm plaster walls, a window with low golden light, a desk lamp, a monitor on a stand, two small bookshelf speakers either side of it, headphones on a stand, a keyboard and mouse on a desk mat, a mug, books, a plant, a notebook, a framed print and a rug. |
| D3 | The selected output device (speakers or headphones) emits soft amber sound-wave rings. The monitor is never an output. |
| D4 | The camera orbits and zooms around the board. The right mouse button rotates, the wheel zooms, and the left button only operates the board. Panning is off. After 8 s of real idleness the camera drifts gently within ±0.3 rad of its starting azimuth, easing in and out. Any focus, visibility, pointer, wheel or key event restarts the idle clock, so the drift never starts when the viewer returns to the tab. |
| D5 | The case is a wedge: front 1.9 cm, back about 6 cm, so the top tilts toward the user. It comes in three finishes (silver, graphite, sand), which only change the look. |
| D6 | The builder is a side panel on the right; on a phone it is a bottom sheet. Edits are a draft shown live on the 3D board. **Apply** saves the draft to the simulated controller (a reboot follows), and **Revert** discards it. An **Auto-save** checkbox saves each edit after 0.7 s of quiet and hides Apply and Revert. |
| D7 | Connection is a status pill at the top left with a popover holding host, port, "Reconnect automatically" and Connect/Disconnect. While no WebSocket is open, the pill also shows **Demo**. |
| D8 | The developer drawer opens from a `<>` button at the bottom left and has three tabs: Frames, Config, Faults. |
| D9 | Demo mode: while no WebSocket is open, the board answers its own presses, as the mock does. While one is open, the board behaves strictly like the firmware. |
| D10 | Sound waves while connected follow screen content (§7.3). With LED-only layouts they stay on the speakers. The protocol is not extended for the simulator. |
| D11 | Build approach A: a static page with no build step that opens straight from disk. A pinned three.js loads from jsDelivr through one small inline module, and every other file is a classic script (§9). |
| D12 | The builder allocates ESP32 pins automatically from the app's chip table. Its limits are what fits on the chip. There are no pin fields in the simulator; the app's Controller page edits pins. |
| D13 | The old page is replaced outright, with no compatibility layer (see the "no backward compatibility" rule for this project). |

## 3. Non-goals

- Pin editing, UDP/Wi-Fi settings and input-tuning editing. These belong to the app's Controller page; the simulator only stores whatever the app writes.
- Firmware flashing simulation. The Hello reports firmware id `sim`, as today.
- Working offline for the 3D scene (§12).
- Audio playback.

## 4. Page layout

A full-bleed `<canvas>` sits under four floating controls, each a light, translucent "paper" surface with a hairline border and soft shadow:

| Position | Element |
|---|---|
| Top left | Connection pill and popover (§8) |
| Top right | Builder toggle button; the builder panel sits below it (§6) |
| Bottom left | Developer-tools button; the drawer opens above it (§10) |
| Top centre | Transient readouts, if any are needed (none are planned) |

The view re-centres the board in the space the panel and drawer leave free, using `camera.setViewOffset`: half the panel's width on wide screens, half the sheet's height on phones, and a fixed lift while the drawer is open. Typography is Instrument Sans for the UI and IBM Plex Mono for frame bytes and config values, both from Google Fonts. Colours are tokens on `:root` (ink, soft ink, paper, hairline, semantic ok/wait/off/danger), defined once in a single deliberate light look.

## 5. The scene

### 5.1 Room and lighting

Units are centimetres. The desk top is at y = 0. Everything is procedural (geometry plus canvas textures) and nothing is loaded except three.js.

- **Room:** plaster back and left walls, a skirting board, a dark wood floor and a terracotta rug.
- **Window:** on the left wall, a frame with mullions and a sill, and a warm sky gradient with soft tree silhouettes.
- **Light:**
  - a directional "window" light, warm and low from the left, casting shadows with PCF filtering at 4096²;
  - the desk lamp's warm spotlight, also casting shadows;
  - a faint warm point light from the monitor;
  - a hemisphere fill;
  - `RoomEnvironment` at 0.32 intensity for reflections;
  - ACES tone mapping.
- **Post-processing:** a `BokehPass` for depth of field (focus is the camera's distance to the board), then an `OutputPass`. The composer renders to a 4-sample half-float target.
- **Props:** as in D2. The monitor is about 70 × 39 cm, centred, lowered on a short aluminium stand. The speakers are scaled to 0.7 and toed in. The headphones hang on a walnut-and-metal stand to the right.
- **Monitor content:** a warm dune wallpaper and a taskbar. While a fader moves, it shows a Volco-style volume overlay with the name and a bar. When an output or mute changes, it shows the device name for about 1.4 s and then fades.

### 5.2 Sound waves

Each output device has emitters: one on each speaker woofer and one on each headphone ear cup. Each emitter owns three ring meshes that grow and travel along the emitter's facing while fading, staggered by a third of a cycle (1.7 s). The active device's emitters fade in and every other device's fade out (about 0.25 s). When the current output is muted, all waves fade out. With `prefers-reduced-motion`, the rings slow to a third of their speed and the camera never drifts.

### 5.3 The device

`device.js` builds the board from a layout and the finish:

- **Body:** an extruded side profile with rounded corners (front 1.9 cm, back 6.0 cm, 0.22 cm bevel), with metal or matte material per finish.
- **Top group:** tilted to the top surface. Each slider is a recessed groove, ±3.3 cm of travel, scale ticks, and a fader cap with a white index line. Buttons sit in a grid of up to 4 columns at 5.0 cm pitch; each module has its indicator above a key that sits in a dark well.
  - **Plain button:** a key only.
  - **LED:** one LED dome.
  - **LED pair:** two LED domes.
  - **Screen:** a 128×64 OLED behind glass in a black bezel.
- **Size:** the width follows the slider count and button columns, and the depth follows the number of button rows.
- **Details:** the engraved "VOLCO" wordmark, a status LED, and a soft contact shadow.
- **Rebuild:**
  - Choosing a preset rebuilds with a short drop-in animation.
  - A builder edit rebuilds in place.
  - Fader positions are kept by index.
- **OLED rendering:** each screen draws its content at 128×64 with `oled.js` (§7.2), thresholds it to 1 bit, and paints it as a pixel grid into a canvas texture, unlit and not tone-mapped. The firmware's brightness, dimming, switch-off and pixel shift (§7.2) are applied there.
- **LEDs:** an emissive dome plus a tiny warm point light that spills onto the case.

### 5.4 Pointer

- **Left button:**
  - Faders: dragging a cap moves it along its groove by raycasting onto the top-surface plane, with value 0 at the front and 4095 at the back.
  - Keys: a press animates the key down; releasing the pointer releases it. Pointer capture keeps a drag alive outside the canvas.
- **Hover:** the cursor becomes a pointer over caps and keys.
- **Right button:** orbits. Pointer handling skips non-left mouse buttons, and the orbit controls map `LEFT: none, MIDDLE: dolly, RIGHT: rotate`.
- **Touch:** one finger on the board operates it; one finger elsewhere orbits; pinch zooms.
- **Keyboard:** none in the scene. The builder and drawer are fully keyboard-operable.

## 6. Builder

### 6.1 Panel

The panel has five sections, from top to bottom:

1. **Preset:** a select with *DIY reference*, *Screen controller* and *Mixed*. A disabled *Custom* entry shows after any edit.
2. **Sliders:** a − n + stepper.
3. **Buttons:** one row per button: a drag grip for native drag-and-drop reordering, the button number, a four-way kind picker (plain, LED, LED pair, screen, as icons with labels and tooltips), and a remove button. A dashed "+ Button" row adds an LED button.
4. **Finish:** three swatches.
5. **Footer:** Auto-save on the left; Revert and Apply on the right.

The panel takes its draft from the stored config at load and after every reboot. If Volco writes a config while a draft is unsaved, Volco's config replaces the draft and the panel shows a one-line note ("Volco saved a new layout; your draft was replaced") until the next edit.

### 6.2 Presets

| Id | Name | Sliders | Buttons |
|---|---|---|---|
| `diy-reference` | DIY reference | 5 | LED, LED, LED pair. The pins and I2C buses are exactly those of `proto/presets/diy-reference.json`. |
| `screens` | Screen controller | 5 | screen ×4 |
| `mixed` | Mixed | 5 | screen, screen, LED, LED pair |

The DIY reference preset is embedded in `layout.js`. A test checks it against the proto preset file. The other two presets are simulator-only, and their pins come from the allocator. Each stored config's `preset` field is set to the preset id, or `custom` after an edit.

### 6.3 Pin allocation (`layout.js`)

The chip is the classic ESP32, with exactly the masks of `Volco.Core.Controller.ChipInfo.Esp32`: usable pins, output-capable pins, ADC1 and ADC2. The allocator:

- **Keeps pins:** an item that already has valid pins keeps them, so configs the app wrote keep their pins. Only new items, or items whose kind changed, receive pins.
- **Never reuses a pin:** sliders, buttons, indicator pins and I2C bus pins all come from one pool.
- **Assigns by role:**
  - **Sliders:** ADC pins, ADC1 first (32–39) and then ADC2. ADC2 is used only while UDP is off, matching the validator.
  - **Buttons:** input pins with internal pull-ups, which excludes the input-only 34–39.
  - **LED and LED-pair indicators:** output-capable pins.
  - **Screens:** a bus and an address. Screens fill buses 0, 1, 2 and 3 in order, at 0x3C and then 0x3D. That gives at most 8 screens, matching the firmware's `kMaxScreens`. A bus receives SDA and SCL (output-capable) the first time a screen uses it.
- **Prefers safe pins:** the strapping pins 0, 2, 12 and 15 are used only once nothing else is left.
- **Disables what won't fit:** the builder greys out "+" for sliders and buttons, and individual kind choices, when allocation would fail, with a tooltip saying why ("No free ADC pin", "No free output pin", "8 screens maximum").

`layout.toConfig(layout, previousConfig)` returns a `ControllerConfig` object that keeps every non-hardware field of the previous config (UDP, screen behaviour, input tuning). `layout.fromConfig(config)` returns the builder's view of it: slider count and button kinds.

### 6.4 Apply and reboot

Apply (or Auto-save) does the following:
1. Encodes the config with protobufjs and stores the blob through `config.js`'s store.
2. Logs "Config saved, rebooting" in the frame log.
3. Reboots: the WebSocket closes, the OLEDs go dark for about 1 s, the board rebuilds from the stored config, and the simulator reconnects. On reconnect it sends Hello and the slider positions, as today's `simulateReboot` does.

With Auto-save on, edits within 0.7 s of each other cause one reboot.

## 7. Firmware fidelity

### 7.1 Link (`link.js`)

`link.js` mirrors `firmware/lib/volco_protocol`'s Link on the controller side, with an injected clock:

- **Hello:** sent when the socket opens, on HelloRequest, and when a frame arrives after the host was considered lost. Every Hello is followed by one Slider frame per slider with its current value.
- **Keepalive:** Alive is sent when nothing has been sent for 1 s.
- **Host loss:** after 3 s with no valid frame, the host is lost: LEDs go to 0, screens show NOT CONNECTED, and any held updates are dropped.
- **Begin/Commit:** Begin holds Led and Screen updates; Commit applies them together; a Begin left open for 500 ms is applied anyway. A held Screen keeps the first non-zero transition of the burst, as today.
- **Validation:** Led applies only to LED and LED-pair buttons and Screen only to screen buttons. A Screen that overruns its frame is dropped. An unknown transition counts as 0. An unknown layout draws only the main text, centred. An unknown icon draws `generic`.
- **Config frames:** they go to `config.js`'s session, unchanged except for the fault hook (§10.3).
- **Output:** display state (per button, an LED value or screen content plus a transition) and outgoing frames through `send`.

### 7.2 Screens (`oled.js`, `fonts.js`)

- **Fonts:** `firmware/tools/gen_assets.py` gains a fifth output, `tools/simulator/fonts.js`. It holds the same Arimo 10 px Regular and 14 px Bold glyph bitmaps as `volco_fonts.cpp`, in the same Volco font format, so simulator text is pixel-identical to the hardware.
- **Rendering:** `oled.js` ports layout 1 from `firmware/lib/volco_render` (and the bidi handling of `volco_bidi`) to draw into a 128×64 bitmap: the top line, or dots; the icon with the mute slash; the main line; centring; right-to-left text; marquee scrolling with its hold and speed; and the slide transitions.
- **Screen behaviour:** brightness, dim after N seconds, switch off after N seconds, pixel shift every N seconds, and "wake swallows press" follow the stored config's `ScreenBehaviour` (or the proto defaults), as `firmware/lib/volco_screen_state` does. Dimming and switching off scale the OLED texture's brightness, and pixel shift offsets the bitmap. A press while the screens are off wakes them and, if configured, is not sent to the host.

### 7.3 Which device emits waves

**Connected (strict).** Output-related screens set the current output:
- a screen whose top line starts with `NEXT:` (a toggle), or is `ACTIVE` (a set-output action);
- a mute screen whose icon is an output icon.

The icon decides the device: `speaker`, `speakers` or `display` mean the speakers; `headphones`, `headset` or `earbuds` mean the headphones. The most recent such screen wins. A `MUTED` top line on a screen that shows the current output stops the waves until a screen shows the device live again. Without any output-related screen, the waves stay on the speakers.

**Demo.** The simulator is its own host and knows the output exactly (§8.2).

## 8. Connection and demo mode

### 8.1 Connection (`connection.js`)

- **Pill states:**
  - *Demo* (grey dot, "Not connected · Demo");
  - *Connecting…* (blinking amber dot);
  - *Connected* (green dot, `host:port`);
  - *Can't reach host:port* (grey dot, shown after a failed attempt).
- **Popover:** host (default `localhost`), port (default `8765`), "Reconnect automatically" (default on), and Connect/Disconnect. It closes when you click outside it.
- **WebSocket:** `ws://host:port/` with binary type `arraybuffer`. When the socket closes, auto-reconnect retries every 3 s unless the user pressed Disconnect. A reboot (§6.4) reconnects after 1 s whatever the setting, as today.

### 8.2 Demo mode

Demo mode applies while no WebSocket is open. A small local "demo host" in `app.js` answers presses by producing the same display state Volco would:

| Buttons in demo mode | Behaviour |
|---|---|
| First screen | Output toggle: Speakers / Headphones, with `NEXT:` top lines and speaker/headphones icons. |
| Second screen | Microphone mute (`LIVE`/`MUTED`). |
| Third screen | Preset dots (Desk, Gaming, Calls). |
| Fourth screen and beyond | Discord mute. |
| LED pair | Output: A = speakers, B = headphones. |
| Single LED | Mute toggle. |
| Faders | Move freely and drive the monitor's volume overlay (Master, Spotify, Discord, Firefox, Game, …). |

Demo mode never sends frames. Its state is reset when a socket opens.

## 9. Code structure

All files are in `tools/simulator/`, and `index.html` opens straight from disk.

**Logic.** UMD-style classic scripts with no DOM or WebGL. Each exposes a global in the browser and `module.exports` under Node, like `config.js` today.

| File | Responsibility |
|---|---|
| `protocol.js` | Message constants, `frame(payload)`, a streaming `Parser`, encoders (Hello, Alive, Slider, Button), decoders (Led, Screen). |
| `link.js` | §7.1 |
| `config.js` | Existing; gains `forceNextResult(code)` (§10.3). |
| `layout.js` | Presets, chip table, pin allocation, `toConfig`/`fromConfig`, limits (§6.2–6.3). |
| `oled.js` | §7.2: bitmap rendering, transitions, marquee, screen power state. |
| `fonts.js`, `icons.js` | Generated by `gen_assets.py`. |
| `volco_config_descriptor.js` | Generated, as today. |

**Browser only.**

| File | Responsibility |
|---|---|
| `index.html` | Markup, styles, script tags, and the inline module that imports a pinned `three` (core plus `OrbitControls`, `RoomEnvironment`, `RoundedBoxGeometry`, `EffectComposer`, `RenderPass`, `BokehPass`, `OutputPass`) from jsDelivr, publishes it as `window.VolcoThree`, and dispatches `volco-three` (or `volco-three-failed`). |
| `stage.js` | Renderer, composer, camera and controls (D4), view offset, render loop. |
| `room.js` | §5.1–5.2 |
| `device.js` | §5.3–5.4 |
| `builder.js` | §6.1 and §6.4 |
| `connection.js` | §8.1 |
| `devtools.js` | §10 |
| `app.js` | Wires everything and hosts demo mode (§8.2). Starts the logic right away and the scene when `volco-three` fires. |

**Load order.** The inline module is placed before the `defer` classic scripts, and deferred classic and module scripts run in document order. A scene file reads `window.VolcoThree` only inside functions that `app.js` calls after the ready event.

## 10. Developer drawer

### 10.1 Frames

- **Rows:** time since connect, direction (← from Volco, → to Volco, · for notes), frame name, hex payload. Newest first, keeping the last 400 frames and drawing 160.
- **Controls:** "Hide keepalive" (on by default; hides Alive and Heartbeat), Pause/Resume, Clear.
- **Bad frames:** a frame sent with a corrupted checksum (§10.3) shows its name in the danger colour with ✕. Frames that arrive with a bad checksum and are dropped by the parser are listed too, as "bad checksum" notes.
- **Empty state:** "Frames appear here once Volco connects."

### 10.2 Config

- **Fields:**
  - state: none, valid, or invalid with the reason;
  - size in bytes;
  - CRC-32, as in Hello;
  - preset;
  - hardware summary ("5 sliders · screen, screen, LED, LED pair");
  - storage, shown only when `localStorage` is unavailable ("Kept in memory only").
- **Actions:** "Copy as JSON" (the protobuf JSON mapping, the same shape as `proto/presets/*.json`) and "Clear config", which forgets the stored config and reboots.

### 10.3 Faults

| Fault | Effect |
|---|---|
| Go silent | The simulator sends nothing (no Alive, no input) and ignores every incoming frame, so Volco sees the controller time out. |
| Corrupt the next frame's checksum | One-shot. The next outgoing frame's checksum byte is XORed with 0xFF. |
| Answer the next config write with … | One-shot. The next ConfigWriteEnd is answered with the chosen code (OK, BAD_SEQUENCE, BAD_CRC, TOO_LARGE, DECODE_FAILED, INVALID; item and field 0xFF). With anything other than OK, the blob is not stored. |
| Ignore Commit | Commit is ignored, so held updates apply only through the 500 ms fallback. |
| Reboot the controller | The same reboot as §6.4, without storing anything. |

## 11. Persistence

Everything is wrapped in try/catch; when storage is unavailable, values are kept in memory.

| Key | Value |
|---|---|
| `volco-sim-config` | The stored config blob (existing key and format). |
| `volco-sim-prefs` | JSON: host, port, reconnect, autosave, finish, panelOpen, drawerOpen, drawerTab. |

## 12. Errors and degraded modes

| Condition | Behaviour |
|---|---|
| three.js fails to load, or there is no WebGL | A centred notice replaces the scene ("The 3D view needs a network connection and WebGL."). The connection, link, config session and drawer keep working. The builder still edits and applies, without a preview. |
| protobufjs fails to load | Config frames are ignored, Apply is disabled with "Needs protobufjs (offline?)", and the Config tab says so. Hello falls back to the builder's layout with config state 0. |
| WebSocket error or close | The pill shows the state (§8.1). Demo mode resumes. |
| Stored config fails decode or validation | Hello reports state 2 with its CRC. The firmware would boot with no inputs; the simulator instead falls back to the DIY reference layout so the board stays usable, and Hello reports that hardware. The Config tab shows the reason. |
| Volco writes a config while the draft is dirty | §6.1 |
| Tab hidden | The render loop stops (three's animation loop pauses). Link timers keep running, as on hardware. |

## 13. Testing

**Node tests.** `cd tools/simulator/test && npm ci && node --test`, run by the existing `simulator-test` CI job:

- **`protocol.test.mjs`:** encode and parse every frame in `firmware/test/golden/frames.txt` (shared with the firmware and C# tests). Covers parsing a stream split across chunks, resyncing after garbage, and dropping a bad checksum.
- **`link.test.mjs`** (fake clock):
  - Hello on open, on HelloRequest and on host return, each followed by the slider frames;
  - Alive cadence;
  - the 3 s host loss (LEDs off, NOT CONNECTED);
  - Begin/Commit hold and the 500 ms fallback;
  - kind filtering;
  - each fault.
- **`layout.test.mjs`:**
  - every allocated pin passes the rules of `ControllerConfigValidator` (re-stated in the test from `ChipInfo.Esp32`), including uniqueness and the bus/address rules;
  - existing pins are kept;
  - the limits are hit at the right counts;
  - the DIY reference matches `proto/presets/diy-reference.json`;
  - `toConfig` → protobufjs encode → decode → `fromConfig` round-trips;
  - non-hardware fields survive.
- **`config.test.mjs`:** existing tests, plus `forceNextResult`.
- **`oled.test.mjs`:**
  - transition easing and timing, marquee offsets, and the power-state timeline;
  - **golden bitmaps:** a new `firmware/test/golden/render.txt` holds 128×64 bitmaps for a set of layout-1 cases (Latin, Hebrew, long marquee at fixed offsets, dots, mute slash, unknown layout). It is written by a small host-side generator built from `firmware/lib/volco_render` (run like the other golden generators). `pio test -e native` checks the firmware renderer against it, and `oled.test.mjs` checks `oled.js` against it.

**CI checks** that `fonts.js` and `icons.js` hold the same data as the firmware's committed `volco_fonts.cpp` and `volco_icons.cpp` (`assets.test.mjs`; no Pillow in CI, whose font rendering varies between versions). The descriptor check stays.

**Scene.** No automated tests. The README carries a short manual checklist:
- the page loads with no console errors;
- left-drag moves faders and right-drag orbits;
- zooming centres on the board;
- the waves follow output changes;
- the camera never drifts on returning to the tab;
- builder edits preview live, and Apply reboots;
- the drawer shows frames;
- every fault works.

## 14. Documentation

- `tools/simulator/README.md` is rewritten: how to use it, controls, demo mode, builder and pins, drawer and faults, files, tests, and the manual checklist.
- The "Web Simulator" section of `CLAUDE.md` is updated to the new file list and behaviour.
- `firmware/README.md` gets one line about `fonts.js` and `render.txt` where the generated outputs are listed.

## 15. Settled during implementation

- Pin preference within each class is `PREFER` in `layout.js`: sliders take the input-only pins first (34, 35, 36, 39), then 32, 33, 37, 38 and ADC2; buttons and outputs avoid the strapping pins until nothing else is left. New screens get their bus and address before any pin is allocated, so a defined bus keeps its pins. `layout.test.mjs` pins the result for each preset.
- The golden render generator is the native test itself: `VOLCO_WRITE_GOLDEN=1 pio test -e native -f test_render` rewrites `render.txt` from the firmware renderer; without the variable the test checks it.
- With no screens, the panels never dim, shift or swallow a press, as on the firmware.

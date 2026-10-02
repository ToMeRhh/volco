# Volco Per-Button OLED Screens — Design Spec

**Date:** 2026-10-02
**Status:** Draft for review (revision 2: subagent review round applied)
**Mockups** (open in a browser; where a mockup and this spec disagree, this spec wins):
- `docs/superpowers/mockups/2026-10-02-oled-screens-mockup.html`: screen layouts at real 128×64 resolution, and a pressable controller showing the preset-change slide. The chosen layout is "C nudged" with the synchronized "Slide".
- `docs/superpowers/mockups/2026-10-02-devices-page-mockup.html`: the new Devices settings page.

**Builds on:** `2026-10-01-volco-ux-redesign.md`. This spec settles what that spec deferred: how screen content is sent (§4.3, §7) and the Hello handshake (§4.3).

## 0. Scope

- **One branch, shipped together.** The firmware and the app change in the same PR and are flashed and installed together. There are no other users and no out-of-sync devices to support.
  - **The wire protocol is redesigned from scratch.** Nothing from the current protocol is kept for compatibility. What survives (the framing) survives because it is sound.
- **The user's current hardware must keep working without edits to its hardware tables:**
  - 5 sliders;
  - button 0: LED on GPIO 12, active-low;
  - button 1: LED on GPIO 21, active-low;
  - button 2: LED pair on GPIO 18/19, active-high.

  It is the first test bed for the new app and protocol, before any screen is wired.
- **Target screen hardware:** SSD1306 128×64 I2C modules with **white** panels. They are wired to the ESP32's **two hardware I2C buses × two addresses** (`0x3C` / `0x3D`), so there are at most 4 screens. A button has a screen *or* LEDs, never both.
- **Changes are allowed in:**
  - `src/Volco.Core/`, `src/Volco.App/`, `tests/`, `firmware/`, `tools/simulator/`, `docs/`, `CLAUDE.md`, `.github/workflows/build.yml` (firmware tests);
  - **one** change in `src/Volco.Windows/`: reading each endpoint's form factor (§5.2).
- **Out of scope:**
  - changing the baud rate (the user will try 921600 separately; it is a firmware constant plus the Connection-page value);
  - screen layouts other than layout 1;
  - custom transition durations;
  - translating the fixed screen words ("MUTED", "ACTIVE", …).

## 1. Decisions (summary)

| # | Decision |
|---|---|
| D1 | **The firmware renders.** The app sends each screen as a description built from a fixed template (top slot, icon, main text). The firmware owns fonts, layout, scrolling, animation and burn-in protection. The device does not depend on the app for pixels. |
| D2 | **A fixed template, with a layout ID byte** so a future firmware can add layouts. Layout 1 = "C nudged" (§2.1). |
| D3 | **A closed icon set** compiled into the firmware. Muted is a flag that draws a slash over any icon, not a separate icon. |
| D4 | **Hebrew is supported from day one.** Fonts are generated from one Latin + Hebrew typeface. The firmware reorders right-to-left text itself; the app sends text in normal reading order. |
| D5 | **The controller reports its hardware** (Hello: slider count, button count, indicator kind per button). The app remembers the last report, shows each button's indicator kind read-only, and sends each button only the message type that matches its kind. |
| D6 | **Preset change:** every screen does a horizontal slide, **synchronized** (all screens start on the same loop pass), about 360 ms. Next preset slides left. Go to preset slides by direction of travel. |
| D7 | **The transition is a property of a Screen message.** Begin/Commit only makes a set of updates atomic. Without Begin/Commit, every screen still slides, as its own message arrives. |
| D8 | **Device nicknames and icons**, set on a new **Devices** page. Used on screens, in the volume OSD, and in the app's device pickers. |
| D9 | **Burn-in protection:** dim after idle, turn off after a longer idle, and pixel shift. Each is a boolean (with its timings) in `config.h`. A press that wakes dark screens is swallowed (also a boolean). |
| D10 | **Keepalive in both directions:** the PC sends Heartbeat and the controller sends Alive, each when idle. When the PC goes silent, the controller turns LEDs off and shows a local "not connected" screen, so it never shows stale state. When the PC returns, the controller announces itself again with Hello. |
| D11 | **The simulator** offers two controller types: an LED controller (the user's current hardware) and a screen controller. |

## 2. Screen UX

### 2.1 Layout 1 ("C nudged")

Coordinates are in screen pixels on the 128×64 panel; rows are `y`.

| Slot | Area | Content |
|---|---|---|
| **Top** | rows 0–15 | Either **text** (10 px regular font, centred, baseline row 11) or **dots** (6×6 squares on a 10 px pitch, centred, rows 5–10; the selected dot is filled, the others outlined). |
| **Icon** | 28×28 at x 50, y 18 (rows 18–45) | An icon from the closed set (§4.5). Icon flag "muted" draws a diagonal slash from top-left to bottom-right, with a 1 px background-coloured outline so it reads over filled shapes. |
| **Main** | x 2–125, baseline row 60 | **Text** (14 px bold font), centred when it fits. |

The layout keeps the top band self-contained, so a yellow + blue panel would also work. The user's panels are white.

**Long text** (top or main), when it is wider than its area:
1. it is drawn left-aligned (right-aligned for right-to-left text);
2. it holds for `SCREEN_SCROLL_HOLD_MS` (2 s);
3. it scrolls toward its end at `SCREEN_SCROLL_SPEED_PX_S` (30 px/s);
4. it holds for `SCREEN_SCROLL_END_HOLD_MS` (1.2 s);
5. it jumps back and repeats.

Right-to-left text scrolls the opposite way. New content restarts the cycle.

### 2.2 What each button shows

The app computes one state per **controller** button, i.e. per entry in the last Hello, not per config card:
- **A button with no config card** shows **Not set up**.
- **A config card beyond the controller's button count** is ignored.

A button shows its **first action that is not "Do nothing"**, in the order Press → Double press → Hold, in the **live** preset. The same rule picks the LED value (§5.3).

| Action | Top | Icon | Main | LED value |
|---|---|---|---|---|
| Mute current output | `MUTED` / `LIVE` | the current default output's icon; slash while muted | the default output's display name | 1 while muted |
| Mute current input (mic) | `MUTED` / `LIVE` | the current default input's icon; slash while muted | the default input's display name | 1 while muted |
| Mute a specific device | `MUTED` / `LIVE` | that device's icon; slash while muted | its display name | 1 while muted |
| Switch output to… | `ACTIVE` while it is the default output, else empty | the target's icon | the target's display name | 1 while active |
| Toggle between two outputs | `NEXT: <other output's display name>` | the active output's icon (output A's when neither is active) | the active output's display name (A's when neither) | 1 = A, 2 = B, 0 = neither |
| Next preset | dots: live preset, preset count | presets | the live preset's name | 1 + live preset index |
| Switch to preset… | dots: live preset, preset count (the filled dot already shows whether the target is live) | presets | the target preset's name | 1 + live preset index |
| Not set up | empty | none | empty (a blank screen) | 0 |

**Display name and icon**
- **Display name:** the device's nickname, or else its **automatic short name**. The automatic short name is the Windows friendly name with a trailing parenthesised part removed, when anything is left: `Speakers (Realtek High Definition Audio)` → `Speakers`. The friendly name is the live one, or the stored one when the device is disconnected.
- **Icon:** the device's chosen icon, or else the icon for its Windows form factor (§5.2), or else `generic`.

**Incomplete or unresolvable configuration**
- **No device chosen, or an empty toggle side:**
  - main is `Choose a device`, icon `generic`, top empty, LED 0;
  - for a toggle with only one side set, that side is used as both A and B.
- **Device gone:** when a device can't be resolved (a config naming a device that is gone), the screen still shows its stored display name and icon, with an empty top.
- **No default device** (e.g. no output enabled): the mute-current actions show main `No device`, icon `generic`, top empty, LED 0.

**Other rules**
- **More than 12 presets:** the dots don't fit, so the top shows text `n/N` instead.
- **Top-slot words** are fixed English, uppercase.

### 2.3 Transitions

**Preset change.** All screens slide horizontally (old content out, new content in) in `SCREEN_TRANSITION_MS` (360 ms), synchronized.
- Next preset slides **left**.
- Go to preset slides **left** when the target is later in the preset list and **right** when it is earlier.
- LED changes for the new preset apply on the same loop pass.

**Live preset deleted** (the runtime falls back to the first preset):
- this counts as a preset change: the runtime pushes all screens with **no** transition (instant), since there is no meaningful direction;
- the push happens even when the first preset was already the fallback target.

**Undo of a preset deletion** does not change the live preset.

**Every other change** (mute toggled, output switched, nickname or icon edited, action edited) is an **instant** swap.

**Firmware rules when updates overlap:**
- **New content mid-slide:** the slide **snaps** to its end, and the new content is applied with its own transition, starting from the snapped content. Rapid Next presses therefore never queue up animations.
- **A held message replaced inside Begin/Commit:** the newer message replaces the held one's content. The **held transition is kept** when the newer message has none, so an instant update that sneaks into a preset change doesn't cancel the slide.
- **Panels off (§2.4):** content is applied instantly with no I2C work. The panels show it when they wake.

### 2.4 Local screens (firmware only)

- **At boot, and after `HOST_TIMEOUT_MS` (3 s) without any frame from the PC:**
  - every screen shows top `NOT CONNECTED`, no icon, main `Volco`;
  - every LED is off.

  On the first valid frame after that, the firmware sends **Hello** (§3.4), so the PC re-sends everything.
- **Burn-in protection.** Any button press or slider movement is **activity** and resets the idle timer. PC messages are not activity: audio changes never wake a dark screen.
  - **Dim** (`SCREEN_DIM_ENABLED`): after `SCREEN_DIM_AFTER_MS` (2 min), contrast drops to `SCREEN_DIM_BRIGHTNESS`.
  - **Off** (`SCREEN_OFF_ENABLED`): after `SCREEN_OFF_AFTER_MS` (30 min), the panels are switched off (SSD1306 power save).
  - **Pixel shift** (`SCREEN_PIXEL_SHIFT_ENABLED`): every `SCREEN_PIXEL_SHIFT_EVERY_MS` (3 min), every screen's content moves to the next offset in the cycle (0,0) → (1,0) → (1,1) → (0,1) → (−1,1) → (−1,0) → (−1,−1) → (0,−1) → (1,−1). The offset is at most 1 px, so no slot leaves its area by more than a pixel.
  - **Wake** (`SCREEN_WAKE_SWALLOWS_PRESS`): when the screens are **off** (not merely dimmed), the press that wakes them and its matching release are not sent to the PC. Slider moves always wake the screens and are always sent.
  - The idle timers only run when at least one screen exists.

## 3. Wire protocol

### 3.1 Framing

```
0xAA | length | type | body … | checksum
```

- `length` = the number of bytes in `type` + `body`, 1–255.
- `checksum` = XOR of `type` and every `body` byte.
- A receiver hunts for `0xAA` and drops a frame whose length is 0 or whose checksum fails.
- XOR is enough: every transport has its own error checking underneath (USB, UDP, TCP), so the checksum only has to resynchronise on garbage, such as boot-ROM output on the serial port.
- Both sides accept frames up to 258 bytes on the wire (255 + marker, length and checksum).

### 3.2 Messages

The high bit of `type` is the direction: `0x0_` comes from the controller, and `0x8_` comes from the PC. Paired messages share the low bits: Hello/HelloRequest, Alive/Heartbeat. Multi-byte integers are big-endian.

| Type | Name | Body | Notes |
|---|---|---|---|
| `0x01` | **Hello** | `version` u8 (= 1), `sliders` u8, `buttons` u8, `kind[buttons]` u8 each: `0` none · `1` LED · `2` LED pair · `3` screen | Sent at boot, in reply to every HelloRequest, and on the first valid frame after a host timeout. |
| `0x02` | **Alive** | — | Sent when the controller has sent nothing for 1 s. |
| `0x03` | **Slider** | `index` u8, `value` u16 (0–4095) | |
| `0x04` | **Button** | `index` u8, `pressed` u8 (0/1) | |
| `0x81` | **HelloRequest** | — | |
| `0x82` | **Heartbeat** | — | Sent when the PC has sent nothing for 1 s. |
| `0x83` | **Led** | `button` u8, `value` u8 | LED: lit when `value` ≠ 0. LED pair: `1` lights A, `2` lights B, anything else lights neither. |
| `0x84` | **Screen** | see §3.3 | |
| `0x85` | **Begin** | — | |
| `0x86` | **Commit** | — | |

### 3.3 Screen body

```
button      u8
transition  u8    0 none · 1 slide left · 2 slide right
layout      u8    1
topKind     u8    0 empty · 1 text · 2 dots
  topKind 1:  len u8, len bytes of UTF-8
  topKind 2:  selected u8 (0-based), count u8
icon        u8    icon ID (§4.5); 0xFF = no icon
iconFlags   u8    bit 0 = muted slash; other bits 0
mainLen     u8
main        mainLen bytes of UTF-8
```

**Text**
- It is UTF-8 in normal reading (logical) order.
- Each slot is at most **60 bytes**. The app truncates on a code-point boundary.
- The app removes Hebrew combining marks (U+0591–U+05C7, the niqqud and cantillation) before encoding. The firmware cannot stack them.

**Malformed input:** the firmware drops a Screen message whose lengths overrun the frame.

**Unknown values**
- An unknown `transition` is treated as `0`.
- An unknown `layout` draws only the main text, centred.
- An unknown icon ID draws `generic`.

**Size:** the largest Screen message is 129 bytes of type + body (a 132-byte frame).

### 3.4 Behaviour

**Connect and Hello**
- The PC sends HelloRequest when its transport connects, and again every 1 s until a Hello arrives on that connection.
- **The PC sends no Led or Screen message on a connection until a Hello has arrived on it.** This replaces every existing connect-race mitigation (§5.5): the post-connect re-push timers, the "awaiting first message" re-push, and the forced push on Connected.
- The firmware sends Hello:
  - at boot (this covers the ESP32 resetting when the serial port is opened);
  - in reply to every HelloRequest;
  - on the first valid frame after a host timeout.
- On every Hello, the PC:
  1. checks the version;
  2. stores the controller info (saving the config only if it changed);
  3. clears its dedup cache;
  4. sends every button's state: Led or Screen per its kind; nothing for kind `0`.

**Version mismatch.** When `version` isn't the one the app speaks:
- the runtime's **controller state** (§5.5) becomes `VersionMismatch`, and the UI shows "Controller firmware doesn't match this version of Volco. Reflash it.";
- the PC stops sending Led, Screen and **Heartbeat**, so the controller falls back to "not connected" instead of showing stale content;
- it keeps processing Slider and Button input, and keeps answering nothing else;
- the next Hello re-checks the version.

There is no compatibility handling beyond that.

**Kind mismatch.** The firmware ignores a Led for a button that isn't an LED or LED pair, a Screen for a button that isn't a screen, and any out-of-range index.

**Keepalive**
- **Each side sends its keepalive when it has sent no other frame for 1 s:** Heartbeat from the PC, Alive from the controller.
- **Each side checks every 250 ms,** so the gap between frames never exceeds about 1.25 s, against a 3 s timeout.
- **The controller side:** any valid frame from the PC is proof of life. After `HOST_TIMEOUT_MS` without one, the controller shows the local "not connected" state (§2.4).
- **The PC side:**
  - any valid frame from the controller is proof of life;
  - this keeps `UdpConnection` from flapping to Disconnected after 3 s without inbound packets;
  - it lets the UDP transport learn the controller's address even when the controller has nothing to report.

**Begin/Commit**
- **Holding:** after Begin, the firmware holds every Led and Screen message instead of applying it.
- **Applying:** on Commit, it applies all held messages on the same loop pass, so each held Screen starts its own transition at the same moment.
- **Fallback:** if no Commit arrives within 500 ms of Begin, the firmware applies what it holds.
- **Edge cases:**
  - a second Begin while holding keeps holding (the timer restarts);
  - a newer message for the same button replaces the held one (§2.3 keeps its transition);
  - Commit with nothing held is a no-op;
  - Commit without a Begin is ignored.

**When the PC uses Begin/Commit.** Only for a preset change with a direction (§2.3), and only when `VolcoRuntimeOptions.SynchronizedPresetTransitions` is true (the default). It sends:
1. Begin;
2. every screen button's Screen with the slide direction, even when the content is unchanged, so every screen moves;
3. every LED button's Led;
4. Commit.

With the option off, the same Led and Screen messages go out without Begin/Commit. Each screen then slides as soon as its message arrives. This is a test switch for judging the delay.

**Ordering and dedup**
- **One sender at a time:** all outbound frames go through **one serialized path** (§5.5). A burst (Begin … Commit, or the full push after Hello) is never interleaved with another push, and a push computed from older state can never be sent after a newer one.
- **Dedup:** the PC remembers the last Led value and Screen content it sent per button. Outside a preset change, it only sends a button whose content changed. The transition is not part of the comparison.

### 3.5 Transport requirements

A preset change on 4 screens is a burst of about 560 bytes. Every transport must carry it whole.

**Firmware**
- **Draining:** each loop pass reads **everything** available from the transport (repeated `transport_receive` until it returns 0), not one 64-byte buffer.
- **Serial:** `Serial.setRxBufferSize(1024)` before `Serial.begin`. A screen redraw blocks the loop for up to about 30 ms, during which about 350 bytes can arrive at 115200 baud.
- **WebSocket:** the receive buffer grows to 1024 bytes. A message that doesn't fit is logged rather than silently dropped.
- **UDP:** reads the whole datagram, up to 1024 bytes, into a buffer of its own. It is never truncated to the caller's buffer.

**PC**
- **Serial and UDP:** sends are already synchronous, so the serialized path orders them.
- **WebSocket:** sends are made **sequential**. `WebSocketConnection.Send` queues frames, and one sender awaits each `SendAsync` before starting the next, since .NET allows only one outstanding send per socket.

## 4. Firmware

### 4.1 Files

`config.h` remains the only file a user edits. Logic that must be unit-tested lives in `lib/`, so PlatformIO's `native` test environment builds it without Arduino.

| File | Job |
|---|---|
| `src/config.h` | Transport, Wi-Fi, serial, I2C buses, hardware tables, tuning, screen settings (§4.2). **Every setting has a one-line comment.** |
| `src/hardware.h` | Table types (`ButtonDef`, `Indicator`) and macros `IND_NONE`, `IND_LED`, `IND_LED_PAIR`, **`IND_SCREEN(bus, address)`**. |
| `src/transport.h/.cpp` | The four `TRANSPORT_*` implementations: `transport_init / connected / send / receive / reconnect`, with the §3.5 buffers. |
| `src/indicators.h/.cpp` | LED rendering (today's `initIndicator` / `showIndicator`). |
| `src/screens.h/.cpp` | U8g2 drivers and drawing: layout 1, scrolling, transitions, the local screen. Driven by `screen_state`. |
| `src/main.cpp` | `setup()` and `loop()` only: sliders → buttons → drain receive → protocol tick → screens tick → connection check. |
| `src/icons.h`, `src/fonts/*.h` | **Generated.** The icon bitmaps and ID table; the two U8g2 fonts. |
| `lib/volco_protocol/` | Frame parser and encoder, message decoding, Hello encoding from a button table, Begin/Commit holding, keepalive and host-timeout tracking. Pure C++; time is passed in. |
| `lib/volco_bidi/` | Logical → visual reordering of UTF-8 text (§4.4). Pure C++. |
| `lib/volco_screen_state/` | The pure state machine for each screen and for the panels: current, previous and pending content, transition progress, mid-slide snapping, dim, off, pixel shift, and wake-swallow decisions. Pure C++; time is passed in. |
| `assets/icons/manifest.txt`, `assets/icons/*.png`, `assets/fonts/*.ttf` (+ licence) | Asset sources. |
| `tools/gen_assets.py` | Generates `icons.h`, `fonts/*.h`, the Core icon enum (§5.4) and the simulator's icon data (§7). The outputs are checked in, so a normal build needs no Python. |
| `test/` | PlatformIO `native` tests (§8.3). |

**`platformio.ini`**
- adds U8g2 (`olikraus/U8g2`) to `lib_deps`;
- adds `[env:native]` (`platform = native`, building only `lib/`).

### 4.2 `config.h` additions

The current hardware tables are unchanged. Hello derives each button's kind from its `Indicator`.

```cpp
// I2C buses. IND_SCREEN(bus, address) picks one: bus 0 or 1, address 0x3C or 0x3D.
#define I2C0_SDA 25                 // bus 0 data pin
#define I2C0_SCL 26                 // bus 0 clock pin
#define I2C1_SDA 27                 // bus 1 data pin
#define I2C1_SCL 13                 // bus 1 clock pin
#define I2C_CLOCK_HZ 400000         // bus speed; SSD1306 modules usually tolerate 1000000

#define SCREEN_BRIGHTNESS 255           // normal brightness (contrast), 0-255
#define SCREEN_TRANSITION_MS 360        // how long a slide between two screens takes
#define SCREEN_SCROLL_HOLD_MS 2000      // long text waits this long before it scrolls
#define SCREEN_SCROLL_SPEED_PX_S 30     // how fast long text scrolls
#define SCREEN_SCROLL_END_HOLD_MS 1200  // pause at the end of the text before jumping back

// Burn-in protection. Any button press or slider move counts as activity.
#define SCREEN_DIM_ENABLED true                     // dim the screens when idle
#define SCREEN_DIM_AFTER_MS (2 * 60 * 1000)         //   ...after this long without activity
#define SCREEN_DIM_BRIGHTNESS 8                     //   ...to this brightness (0-255)
#define SCREEN_OFF_ENABLED true                     // turn the screens off when idle
#define SCREEN_OFF_AFTER_MS (30 * 60 * 1000)        //   ...after this long without activity
#define SCREEN_PIXEL_SHIFT_ENABLED true             // nudge content by 1 px now and then
#define SCREEN_PIXEL_SHIFT_EVERY_MS (3 * 60 * 1000) //   ...this often
#define SCREEN_WAKE_SWALLOWS_PRESS true             // a press that wakes dark screens isn't sent to the PC

#define HOST_TIMEOUT_MS 3000  // no frame from the PC for this long: LEDs off, "not connected" screen
#define KEEPALIVE_MS 1000     // send Alive when nothing else was sent for this long
```

- **Screen example:** a button entry `{4, IND_SCREEN(0, 0x3C)}`.
- **I2C is only started when needed.** A bus is started only if some button uses it. With no `IND_SCREEN` entries, the firmware never touches I2C, so the current hardware's LED on GPIO 21 (Arduino's default SDA) is unaffected.
- **Compile-time checks** (`static_assert`): at most two screens per bus, bus ∈ {0, 1}, address ∈ {0x3C, 0x3D}, and no two screens with the same bus and address.

### 4.3 Rendering

- **Drivers:** U8g2 full-buffer mode, one 1 KB frame buffer per screen. Bus 0 uses `U8G2_SSD1306_128X64_NONAME_F_HW_I2C` and bus 1 uses `…_F_2ND_HW_I2C`, with `setI2CAddress(address << 1)` and `setBusClock(I2C_CLOCK_HZ)`.
- **Redraw only on change:** new content, a scroll step, a transition frame, a pixel-shift step, or a local-state change. Static screens cost no I2C time.
- **Transitions.** Each transition is a function `draw(screen, old, new, progress 0–1)` in a table indexed by transition ID. Adding a transition means adding a function, a table entry, and a value in the shared enum.
  - The slide draws the old content offset by `−p·128` and the new content offset by `(1−p)·128` (mirrored for slide right), with an ease-in-out curve.
  - **Progress is time-based:** each frame draws the position for the current time, so a slow bus drops frames instead of stretching the slide.
- **Frame-rate expectation.** A full frame takes about 25–30 ms per panel at 400 kHz, because Wire sends in 32-byte chunks, and panels are updated one after another. A 360 ms slide on 4 screens therefore gets only about 3 frames per panel. The README recommends `I2C_CLOCK_HZ 1000000` (about 2.5× faster) once screens are wired, and the manual checklist judges the result.
- **The firmware keeps each screen's previous content** so it can draw the old side of a transition.
- **Text** is drawn with `drawUTF8` after `bidi` reordering (§4.4).

### 4.4 Fonts and right-to-left text

**Fonts**
- **Typeface:** Arimo, Regular and Bold (Apache 2.0, the font licence goes in `firmware/assets/fonts/`). It covers Latin and Hebrew in one consistent design.
- `gen_assets.py` turns it into two U8g2 fonts, rendered at an **exact pixel size** passed to `otf2bdf -p`:
  - **top:** 10 px Regular;
  - **main:** 14 px Bold.

  The app preview uses the same TTF at the same pixel sizes.
- **Glyph subset:** ASCII, Latin-1 Supplement, Hebrew letters (U+05D0–U+05EA, U+05F0–U+05F4), and `…`, `–`, `—`, `•`. Expected size is under 10 KB each.
- **Toolchain:**
  - `otf2bdf` (a pinned version, installed from the distribution package) → U8g2's `bdfconv`, built from a pinned U8g2 release tag;
  - `gen_assets.py` documents both versions and fails clearly when either tool is missing.
- **Missing glyphs** render as nothing (U8g2's behaviour).

**`bidi` (a deliberately small subset of the Unicode bidi algorithm)**
1. **Classify each code point:**
   - **R:** Hebrew letters (U+05D0–U+05EA, U+05F0–U+05F4);
   - **L:** other letters;
   - **EN:** digits;
   - **CS:** `.` `,` `:` `/`;
   - **N:** everything else: spaces, other punctuation, brackets.
2. **Join numbers** (UBA rules W4/W5, simplified): a **CS** between two **EN** becomes EN, so `1.5`, `10:30` and `1/2` stay one number.
3. **Paragraph direction:** RTL if the first strong character (L or R) is R; otherwise LTR.
4. **Resolve characters into runs:**
   - **EN** joins the run of the strong character before it, or the paragraph direction when there is none.
   - **N** (and any remaining CS) takes the direction of the strong characters on both sides when they agree, otherwise the paragraph direction.
5. **Reorder:**
   - reverse the characters inside each RTL run, mirroring `()[]{}<>`;
   - in an RTL paragraph, also reverse the order of the runs;
   - number sequences inside RTL runs keep their left-to-right order.
6. **Layout:** an RTL paragraph is right-aligned when it overflows, and scrolls toward the left end (§2.1).

### 4.5 Icons

`assets/icons/manifest.txt` lists icons in ID order. IDs are stable: new icons are appended, never inserted.

| ID | Name | Use |
|---|---|---|
| 0 | `generic` | Fallback, and devices with an unknown form factor |
| 1 | `speaker` | |
| 2 | `speakers` | Speaker pair |
| 3 | `headphones` | |
| 4 | `headset` | |
| 5 | `earbuds` | |
| 6 | `display` | Monitor / TV |
| 7 | `mic` | |
| 8 | `studiomic` | |
| 9 | `webcam` | |
| 10 | `presets` | Preset actions only; not offered on the Devices page |

- **Format:** each icon is a 28×28 1-bit PNG.
- **Drawings:** they start from the mockup's drawings.
- **Slash:** the "muted" slash is drawn by the firmware at render time, not stored per icon.

### 4.6 Main loop and input

- **Unchanged:** the slider and button reading (debounce, EMA, deadband).
- **Button events:** before sending a button event, the loop asks `screen_state` whether the event should be swallowed (§2.4).
- **Receive:** drains the transport completely (§3.5).
- **Screens tick:** runs every loop pass and redraws at most **one panel per pass**, round-robin among panels that need it. One pass therefore never blocks longer than one panel's redraw (~25–30 ms at 400 kHz). Transitions are time-based (§4.3), so this only lowers the frame rate.
- **Sliders:** keep sampling on their own 10 ms clock, so a redraw delays a sample rather than dropping it.
- **Buttons:** debounce on timestamps, so a press shorter than a redraw is still seen as long as it lasts at least `DEBOUNCE_MS`.

## 5. Core

### 5.1 Config schema v4

```jsonc
{
  "schemaVersion": 4,
  // … everything from v3 unchanged …
  "devices": [                       // only devices the user customized
    { "id": "{0.0.0.00000000}.{…}", "flow": "Render",
      "friendlyName": "Speakers (Realtek High Definition Audio)",  // last name Windows reported
      "nickname": "Desk speakers",   // "" = use the automatic short name
      "icon": "Speakers" }           // null = automatic (form factor)
  ],
  "controller": {                    // last Hello received; null until the first one
    "protocolVersion": 1, "sliders": 5,
    "buttons": ["Led", "Led", "LedPair"]   // IndicatorKind per button: None | Led | LedPair | Screen
  }
}
```

**Migration**
- **v1 detection changes.** Today `ConfigMigrator.NeedsMigration` is `SchemaVersion < CurrentSchemaVersion` (`ConfigMigrator.cs:28`). It must become `SchemaVersion < 2`, so the v1 name-resolution path (`MigrateFile`, `config.v1.bak`) runs **only** for real v1 files.
- **v3 → v4** is a shape step inside `ConfigManager.Load`, beside the existing v2 step:
  - the missing `devices` and `controller` properties already deserialize to their defaults (`[]` and `null`);
  - the step stamps `schemaVersion = 4`, writing a `config.v3.bak` first.
- **v2** continues to upgrade to the current version in `Load`.
- **Tests:**
  - the user's real v3 config migrates to v4 with nothing else changed;
  - a v3 config never produces `config.v1.bak`;
  - v1 and v2 fixtures still end at v4.

**Entries**
- **A `devices` entry is created** the first time the user sets a nickname or an icon. It is removed when both are cleared, or by "Forget this device".
- **`friendlyName` is refreshed by the runtime:** on `DevicesChanged`, a live device whose name differs from its entry's `friendlyName` gets the new name and a `RequestSave`. Nothing is saved when nothing changed.
- **Device identity** is the endpoint `id`, as for `DeviceRefConfig`.

### 5.2 Form factor (`Volco.Windows` + Core)

This is the one change in `Volco.Windows`.

**Core**
- `AudioDevice` gains `DeviceFormFactor FormFactor`, defaulting to `Unknown`. It is a positional record, so a defaulted trailing parameter keeps every caller compiling.
- `DeviceFormFactor` is a new Core enum: `Unknown, Speakers, Headphones, Headset, Microphone, Display, Other`.

**Windows**
- `DeviceRegistry` reads `PKEY_AudioEndpoint_FormFactor` once per endpoint, beside the friendly name it already reads from the property store. It maps:
  - Speakers → `Speakers`
  - Headphones → `Headphones`
  - Headset or Handset → `Headset`
  - Microphone → `Microphone`
  - DigitalAudioDisplayDevice → `Display`
  - anything else → `Other`
- A failed read gives `Unknown`.

**Icon for each form factor:** Speakers → `speaker`, Headphones → `headphones`, Headset → `headset`, Microphone → `mic`, Display → `display`, otherwise `generic`.

### 5.3 `ControllerDisplay` (replaces `ButtonFeedback`)

It lives in `Communication/` and is pure. It takes the config, the live preset ID, `IAudioEngine` and the stored controller info, and returns, for **each controller button** (§2.2), by its reported indicator kind:
- **Led / LedPair:** the LED value from §2.2's table.
- **Screen:** a `ScreenContent` (top: empty / text / dots, icon, muted flag, main text), built from §2.2.
- **None:** nothing.

With no controller info, it returns nothing at all.

`ButtonFeedback`'s existing tests move to `ControllerDisplay` and keep their cases.

**`DeviceDisplay`**
- Resolves a device's **display name** and **icon**: nickname → automatic short name; chosen icon → form-factor icon → `generic`.
- Shared by `ControllerDisplay`, the OSD labels (§5.5) and the settings UI.

### 5.4 Protocol code

- **`MessageType`** is rewritten to the §3.2 values.
- **`ProtocolParser`** decodes Hello, Alive, Slider and Button.
- **`ProtocolEncoder`** encodes HelloRequest, Heartbeat, Led, Screen, Begin and Commit. Screen text is truncated to 60 bytes on a code-point boundary, after Hebrew combining marks are removed.
- **`ScreenIcon`** is the Core enum generated from the manifest (`ScreenIcon.g.cs`), so IDs can't drift.
- **`ScreenTransition`** and **`IndicatorKind`** are hand-written enums.
- **Golden frames:** `firmware/test/golden/frames.txt` holds one line per case: a name and the frame's hex bytes. It covers every message type and the Screen variants. The C# tests encode and parse against it, and the firmware's native tests decode and encode against it, so the two implementations can't drift.

### 5.5 Runtime (`VolcoRuntime`)

**Controller state.** A new runtime-owned `ControllerState`, separate from the transport's `ConnectionStatus`, with a `ControllerStateChanged` event:
- `NoController` while the transport is not Connected;
- `AwaitingHello` after Connected, until a Hello arrives;
- `Ready` after a Hello with the right version;
- `VersionMismatch` after a Hello with the wrong version.

The UI reads both: the transport status ("Connected · COM3") and the controller state (mismatch error, hardware summary).

**Connect**
- On Connected: send HelloRequest, then repeat every `HelloRetryInterval` (1 s) until Hello arrives.
- **Removed:** every existing connect-race mitigation. That is `VolcoRuntimeOptions.PostConnectRepushDelays` and its timers, the `_awaitingFirstMessageAfterConnect` re-push, and the forced push on Connected. No Led or Screen is sent before Hello (§3.4).

**On Hello**
1. Check the version.
2. Save `controller` only if it differs from the stored info. The save raises `ConfigChanged`, which is harmless after the dedup clear.
3. Raise `ControllerInfoChanged`.
4. Clear the dedup cache.
5. Push everything.

**Pushing**
- **One serialized outbound path.** A single lock (or a single-consumer send queue) is held across **compute → dedup → send** for every push, every preset-change burst and every keepalive. A push computed from older state can therefore never follow a newer one, and Begin … Commit is never interleaved. Today `PushControllerState` sends outside `_pushStateLock` (`VolcoRuntime.cs:746-758`); that changes.
- **What triggers a re-push:**
  - today's triggers: device list changes, mute and volume changes, default-output changes, preset changes;
  - **plus a new subscription to `ConfigManager.ConfigChanged`.** Today only `ConnectionConfigChanged` is subscribed (`VolcoRuntime.cs:286`), so action edits never reach the LEDs. With it, edits to actions, presets, nicknames and icons reach the controller. Dedup keeps the push cheap.

**Preset change**
- The Begin → Screens (slide by direction) → LEDs → Commit sequence, gated by `SynchronizedPresetTransitions`.
- Deleting the live preset forces a full push with no transition (§2.3), including when `SetActivePreset` sees no change and returns early.

**Keepalive and liveness**
- A 250 ms timer sends Heartbeat when nothing was sent for `HeartbeatInterval` (1 s).
- Heartbeats stop during `VersionMismatch`.
- Alive, and any other valid inbound frame, counts as controller activity for the transports.

**OSD labels**
- `ShowMuteOsd`, `ShowDeviceOsd` (`VolcoRuntime.cs:675-704`) and `SliderProcessor`'s slider labels use `DeviceDisplay` display names.
- This replaces raw `FriendlyName` and the hard-coded "Microphone"; the default input's display name is used instead.

**Options**
- `VolcoRuntimeOptions` gains `SynchronizedPresetTransitions` (default true), `HelloRetryInterval` and `HeartbeatInterval`, so tests can shorten them.
- It loses `PostConnectRepushDelays`.

**WebSocket.** `WebSocketConnection.Send` becomes sequential (§3.5).

## 6. App UI

### 6.1 Devices page (new)

The page is mocked up in `2026-10-02-devices-page-mockup.html`.

**Navigation:** Sliders, Buttons, **Devices**, Connection, General.

**Header:** "Devices", with the description "Give your audio devices short names and icons. Volco uses them on your controller's screens, in the volume pop-up and in its own device lists."

**Two groups, Outputs and Inputs, listing:**
- every **enabled** device the engine reports (`GetDevices`), plus
- every **disconnected** device that has a `devices` entry or is referenced by a slider or button action.

Order: the default device first, then alphabetical by display name.

**Closed card:**
- the icon, drawn as on the screen (white on black tile);
- the display name (the nickname, or the automatic name in secondary text);
- the Windows name;
- a chip: "Default output" / "Default input" / "Not connected";
- a chevron.

**Open card:**
- a **Nickname** field: placeholder = automatic short name; hint "Leave empty to use “…”."; `dir=auto`, so Hebrew types right-to-left;
- an **Icon** picker: a row of the 10 device icons (IDs 0–9); the selection is highlighted, and "automatic" is the form-factor icon until the user picks one;
- a live **screen preview** (layout 1, as a "Switch output" button);
- a footer: "Windows name: …", the flow, and for disconnected devices, either **Forget this device** (when it is not referenced) or "Used by Slider 2, Button 3" (when it is).

Edits save as you make them, as on every page.

### 6.2 Nicknames elsewhere

- The **volume OSD** label uses the display name. The label is built in Core (§5.5), not in the App.
- Every **device picker** (slider device, action devices) lists display names, with the Windows name as secondary text when a nickname is set.
- **Button-card summaries** ("Toggle Desk speakers / Arctis") use display names.

### 6.3 Buttons, Sliders and Connection pages

**Button card**
- A read-only line in the open card: "Light: LED" / "LED pair" / "Screen" / "None".
- The closed card's preview matches the kind:
  - today's dots for LEDs;
  - for screens, a small screen preview (128×64 at 1×; 2× in the open card) of what the button shows in the edited preset.
- With no stored controller info, cards say "Connect your controller to see its lights and screens."

**Count hint** (Sliders and Buttons pages)
- When the stored controller info disagrees with the number of cards, a hint under the header reads "Your controller has 4 buttons; 3 are set up." with **Add the missing one**, which appends cards up to the controller's count.
- When there are more cards than controls, it reads "Your controller has 3 buttons; cards 4–5 do nothing."
- There's no hint when the counts agree or before the first Hello.

**Connection page**
- The status panel adds "5 sliders · 3 buttons (2 LED, 1 LED pair)" from the stored controller info.
- A `VersionMismatch` controller state shows the §3.4 error in the status panel and the nav footer.

**Screen preview rendering**
- `Volco.App` draws layout 1 into a 128×64 bitmap and shows it with nearest-neighbour scaling, white on black.
- It uses the **same icon PNGs**, linked from `firmware/assets/icons` as Avalonia resources, and the **same Arimo TTF at the same pixel sizes**. Text is thresholded to 1-bit.
- The result is close, not pixel-identical:
  - Avalonia uses the full Unicode bidi algorithm, so mixed Hebrew/Latin text with unusual punctuation can be ordered slightly differently than on the controller;
  - this is an accepted difference and is noted in a code comment.
- Nothing in `Volco.App` becomes platform-specific (the architecture tests still apply).

## 7. Web simulator (`tools/simulator/index.html`)

**Controller types.** A **controller type** picker offers:
- **LED controller:** 5 sliders; buttons LED, LED, LED pair (the user's hardware);
- **Screen controller:** 5 sliders; 4 screen buttons.

Changing the type disconnects. Hello is built from the selected type.

**Protocol**
- Speaks the §3 protocol:
  - answers HelloRequest with Hello;
  - sends Alive, Slider and Button;
  - applies Led, Screen, Begin and Commit with the 500 ms fallback;
  - tracks the heartbeat, shows "not connected" after 3 s of silence, and sends Hello on the next frame.
- The current `0x05` handler is replaced.
- Shows the last received frames in a small log.

**Rendering**
- Screen buttons render layout 1 on a 128×64 canvas: scrolling, the synchronized slide, and the muted slash.
- Icons come from a generated `tools/simulator/icons.js` (written by `gen_assets.py`, so it works when the page is opened from disk). Text uses the browser's fonts, so it is approximate.
- Dim, off and pixel shift are not simulated.

## 8. Tests

### 8.1 Core (`tests/Volco.Core.Tests`)

**Protocol**
- Encode and parse every message type against the golden frames (§5.4), including Hello with each indicator kind and Alive.
- Screen encoding: every top kind, the icon flags, 60-byte truncation on a code-point boundary for ASCII, Hebrew and emoji, and removal of Hebrew combining marks.
- The parser drops frames with a bad checksum and bodies too short for their type.

**`ControllerDisplay`**
- Every row of §2.2, and the first-non-None rule.
- Controller-vs-config button counts.
- Missing devices, a one-sided toggle, no default device.
- Display-name and icon fallbacks, including a disconnected device.
- The muted flag, and the `n/N` top beyond 12 presets.
- The right output per indicator kind, and nothing without controller info.

**`DeviceDisplay`:** short-name rules (parentheses, nothing left after stripping, Hebrew names).

**Runtime** (fake engine, fake connection)
- Nothing but HelloRequest before Hello, with retries.
- Hello stores the controller info (saving only on change) and pushes everything.
- A Hello after a gap clears dedup and re-pushes.
- Version mismatch: the `VersionMismatch` state, no Led, Screen or Heartbeat, input still processed.
- Heartbeat only when idle.
- Preset change: Begin, Screens with the right direction, Leds, Commit; and the same messages without Begin/Commit when the option is off.
- Live-preset deletion forces an instant push.
- Concurrent pushes from several threads never interleave a burst and never send older state after newer.
- `ConfigChanged` triggers a push.
- A nickname edit re-pushes the affected screens.
- `friendlyName` refresh on `DevicesChanged` saves only on change.
- OSD labels use display names.

**Migration**
- v3 → v4 with the user's real config as a fixture.
- No `config.v1.bak` for v3.
- v1 and v2 fixtures end at v4.

**Other:** `ScreenIcon.g.cs` matches `firmware/assets/icons/manifest.txt`.

### 8.2 App (`tests/Volco.App.Tests`, headless)

**Devices page**
- Lists enabled devices plus referenced or customized disconnected devices.
- Nickname and icon edits save and update the preview.
- Clearing both removes the entry.
- Forget appears only for unreferenced devices.
- `dir=auto` on the nickname field.

**Buttons, Sliders and Connection pages**
- Button cards show the indicator kind and the matching preview.
- The no-controller-info message.
- The count hint in all three states (fewer cards, more cards, none), and "Add the missing one".
- The Connection page's hardware summary and mismatch error.

**Nicknames elsewhere:** in device pickers.

**Coverage:** `SettingsCoverageTests` is extended to the Devices page.

### 8.3 Firmware (`[env:native]`)

**CI.** A new job in `.github/workflows/build.yml` on `ubuntu-latest`:
- `actions/setup-python`;
- `pip install platformio==<pinned>`;
- `pio test -e native` in `firmware/`.

It runs on every push, like the existing test job.

**`volco_protocol`**
- Frame parsing: resync after garbage, a bad checksum, max length, a body too short for its type.
- Decoding and encoding against the golden frames.
- Begin/Commit holding, replacement keeping the held transition, the 500 ms fallback, Commit with nothing held, Commit without Begin.
- Keepalive timing; host timeout, then Hello on the next frame.
- Hello encoding from a test button table.

**`volco_bidi`**
- Hebrew only; Latin only; mixed in both paragraph directions.
- Parentheses mirrored; digits inside Hebrew; `10:30`, `1.5` and `1/2` next to Hebrew.
- An empty string; a string with no strong characters.

**`volco_screen_state`**
- Snapping mid-slide; transitions skipped while off.
- Dim, then off timings; activity resets them.
- The pixel-shift cycle.
- Wake swallowing (press and matching release) only when off and only with the flag on.

### 8.4 Manual checklist (in the plan)

**On the current LED hardware, over serial, UDP and WebSocket**
- Hello arrives, and the cards show LED, LED and LED pair.
- LEDs follow actions, including right after editing an action.
- LEDs go dark 3 s after Volco quits, and come back when it restarts.
- LEDs change together on a preset switch.
- An idle UDP connection stays Connected.

**After wiring screens**
- Layout and fonts; Hebrew; scrolling in both directions.
- The synchronized slide, and the unsynchronized slide with `SynchronizedPresetTransitions` off.
- A 4-screen preset burst over each transport.
- Rapid Next presses.
- The muted slash.
- Dim, off, pixel shift, and the swallowed wake press.
- The local "not connected" screen, and recovery after sleep/resume.
- Slide smoothness at 400 kHz and 1 MHz.

## 9. Docs

**`firmware/README.md`, rewritten for:**
- the new file layout, including `lib/`;
- every `config.h` setting;
- the full protocol table;
- screen wiring and pin choices: free safe GPIOs on the current board are 13, 16, 17, 22, 23, 25, 26 and 27; avoid GPIO 21 while it drives an LED, and the strapping pins 0, 2 and 15;
- the 1 MHz I2C recommendation;
- **migrating a button from LED to screen**;
- regenerating fonts and icons with `gen_assets.py` (tool versions included);
- running the native tests.

It still does not cover building, flashing or wiring diagrams.

**`CLAUDE.md`:**
- the architecture, firmware and wire-protocol sections are updated;
- the `0x05` / `ButtonFeedback` description is replaced with the §3 protocol and `ControllerDisplay`;
- the new firmware test job is added to Build & Test.

**`2026-10-01-volco-ux-redesign.md`:** §4.3 and §7 point to this spec.

## 10. Future (not in this work)

- More layouts, such as a big-number volume screen for sliders with screens.
- More transitions, and per-transition durations in the protocol.
- Translating the fixed screen words.
- A faster baud rate (the user's own experiment).
- An app-side setting to override burn-in timings (today they are firmware constants by choice).

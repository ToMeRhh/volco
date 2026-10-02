# Volco Per-Button OLED Screens — Design Spec

**Date:** 2026-10-02
**Status:** Draft for review
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
| D10 | **PC heartbeat.** When the PC goes silent, the controller turns LEDs off and shows a local "not connected" screen, so it never shows stale state. |
| D11 | **The simulator** offers two controller types: an LED controller (the user's current hardware) and a screen controller. |

## 2. Screen UX

### 2.1 Layout 1 ("C nudged")

Coordinates are in screen pixels on the 128×64 panel; rows are `y`.

| Slot | Area | Content |
|---|---|---|
| **Top** | rows 0–15 | Either **text** (≈10 px regular font, centred, baseline row 11) or **dots** (6×6 squares on a 10 px pitch, centred, rows 5–10; the selected dot is filled, the others outlined). |
| **Icon** | 28×28 at x 50, y 18 (rows 18–45) | An icon from the closed set (§4.5). Icon flag "muted" draws a diagonal slash from top-left to bottom-right, with a 1 px background-coloured outline so it reads over filled shapes. |
| **Main** | x 2–125, baseline row 60 | **Text** (≈14 px bold font), centred when it fits. |

The layout keeps the top band self-contained, so a yellow + blue panel would also work. The user's panels are white.

**Long text** (top or main), when it is wider than its area:
1. it is drawn left-aligned (right-aligned for right-to-left text);
2. it holds for `SCREEN_SCROLL_HOLD_MS` (2 s);
3. it scrolls toward its end at `SCREEN_SCROLL_SPEED_PX_S` (30 px/s);
4. it holds for `SCREEN_SCROLL_END_HOLD_MS` (1.2 s);
5. it jumps back and repeats.

Right-to-left text scrolls the opposite way. New content restarts the cycle.

### 2.2 What each action shows

A button shows its **first action that is not "Do nothing"**, in the order Press → Double press → Hold, in the **live** preset. The same rule picks the LED value (§5.3).

| Action | Top | Icon | Main |
|---|---|---|---|
| Mute current output | `MUTED` / `LIVE` | the current default output's icon; slash while muted | the default output's display name |
| Mute current input (mic) | `MUTED` / `LIVE` | the current default input's icon; slash while muted | the default input's display name |
| Mute a specific device | `MUTED` / `LIVE` | that device's icon; slash while muted | its display name |
| Switch output to… | `ACTIVE` while it is the default output, else empty | the target's icon | the target's display name |
| Toggle between two outputs | `NEXT: <other output's display name>` | the active output's icon (output A's when neither is active) | the active output's display name (A's when neither) |
| Next preset | dots: live preset, preset count | presets | the live preset's name |
| Switch to preset… | dots: live preset, preset count (the filled dot already shows whether the target is live) | presets | the target preset's name |
| Not set up (no action in this preset) | empty | none | empty (a blank screen) |

- **Display name:** the device's nickname, or else its **automatic short name**. The automatic short name is the Windows friendly name with a trailing parenthesised part removed, when anything is left: `Speakers (Realtek High Definition Audio)` → `Speakers`. The friendly name is the live one, or the stored one when the device is disconnected.
- **Icon:** the device's chosen icon, or else the icon for its Windows form factor (§5.2), or else `generic`.
- **More than 12 presets:** the dots don't fit, so the top shows text `n/N` instead.
- **Top-slot words** are fixed English, uppercase.
- **Unresolvable device:** when a device can't be resolved (a config naming a device that is gone), the screen still shows its stored name and icon, with an empty top.

### 2.3 Transitions

- **Preset change:** all screens slide horizontally (old content out, new content in) in `SCREEN_TRANSITION_MS` (360 ms), synchronized.
  - Next preset slides **left**.
  - Go to preset slides **left** when the target is later in the preset list and **right** when it is earlier.
  - LED changes for the new preset apply on the same loop pass.
- **Every other change** (mute toggled, output switched, nickname or icon edited, config changed) is an **instant** swap.

### 2.4 Local screens (firmware only)

- **At boot, and after `HOST_TIMEOUT_MS` (3 s) without any frame from the PC:**
  - every screen shows top `NOT CONNECTED`, no icon, main `Volco`;
  - every LED is off.

  The next content from the PC replaces it.
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
- Both sides accept frames up to 255 bytes. The firmware's receive buffer grows from 32 to 255.

### 3.2 Messages

The high bit of `type` is the direction: `0x0_` comes from the controller, and `0x8_` comes from the PC. Multi-byte integers are big-endian.

| Type | Name | Body | Notes |
|---|---|---|---|
| `0x01` | **Hello** | `version` u8 (= 1), `sliders` u8, `buttons` u8, `kind[buttons]` u8 each: `0` none · `1` LED · `2` LED pair · `3` screen | Sent at boot and in reply to every HelloRequest. |
| `0x02` | **Slider** | `index` u8, `value` u16 (0–4095) | |
| `0x03` | **Button** | `index` u8, `pressed` u8 (0/1) | |
| `0x81` | **HelloRequest** | — | |
| `0x82` | **Heartbeat** | — | |
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

- **Text** is UTF-8 in normal reading (logical) order and at most **60 bytes** per slot. The app truncates on a code-point boundary; the firmware drops a Screen message whose lengths overrun the frame.
- **Unknown values:** an unknown `transition` is treated as `0`. An unknown `layout` draws only the main text, centred. An unknown icon ID draws `generic`.
- The largest Screen message is 128 bytes, well under the frame limit.

### 3.4 Behaviour

**Connect and Hello**
- The PC sends HelloRequest when its transport connects, and again every 1 s until a Hello arrives.
- The firmware also sends Hello at boot. This covers the ESP32 resetting when the serial port is opened.
- On every Hello, the PC:
  1. stores the controller info;
  2. clears its dedup cache;
  3. sends every button's state: Led or Screen per its kind; nothing for kind `0`.

  This **replaces** the post-connect re-push timers (`VolcoRuntimeOptions.PostConnectRepushDelays`), which existed only because the PC could not tell when the controller was ready.

**Version mismatch.** When `version` isn't the one the app speaks:
- the app sets the connection status to an error: "Controller firmware doesn't match this version of Volco. Reflash it.";
- it stops sending Led and Screen messages;
- it still processes Slider and Button input.

There is no compatibility handling beyond that.

**Kind mismatch.** The firmware ignores a Led for a button that isn't an LED or LED pair, a Screen for a button that isn't a screen, and any out-of-range index.

**Heartbeat.** The PC sends Heartbeat when it has sent no other frame for 1 s. The firmware treats *any* valid frame as proof of life and shows the local "not connected" state (§2.4) after `HOST_TIMEOUT_MS`.

**Begin/Commit.** After Begin, the firmware holds every Led and Screen message instead of applying it. On Commit, it applies all held messages on the same loop pass, so each held Screen starts its own transition at the same moment.
- If no Commit arrives within 500 ms of Begin, the firmware applies what it holds.
- A second Begin while holding keeps holding (the timer restarts).
- A newer message for the same button replaces the held one.

**When the PC uses Begin/Commit.** Only for a preset change, and only when `VolcoRuntimeOptions.SynchronizedPresetTransitions` is true (the default). It sends:
1. Begin;
2. every screen button's Screen, with the slide direction from §2.3 — even when the content is unchanged, so every screen moves;
3. every LED button's Led;
4. Commit.

With the option off, the same Led and Screen messages go out without Begin/Commit. Each screen then slides as soon as its message arrives. This is a test switch for judging the delay.

**Dedup.** The PC remembers the last Led value and Screen content it sent per button. Outside a preset change, it only sends a button whose content changed. The transition is not part of the comparison.

## 4. Firmware

### 4.1 Files

`config.h` remains the only file a user edits.

| File | Job |
|---|---|
| `src/config.h` | Transport, Wi-Fi, serial, I2C buses, hardware tables, tuning, screen settings (§4.2). **Every setting has a one-line comment.** |
| `src/hardware.h` | Table types (`ButtonDef`, `Indicator`) and macros `IND_NONE`, `IND_LED`, `IND_LED_PAIR`, **`IND_SCREEN(bus, address)`**. |
| `src/transport.h/.cpp` | The four `TRANSPORT_*` implementations, unchanged in behaviour: `transport_init / connected / send / receive / reconnect`. |
| `src/protocol.h/.cpp` | Frame parser and encoder, message dispatch, Hello, Begin/Commit holding, host-timeout tracking. No Arduino dependency except through small injected hooks, so it builds natively for tests. |
| `src/indicators.h/.cpp` | LED rendering (today's `initIndicator` / `showIndicator`). |
| `src/screens.h/.cpp` | One U8g2 instance per screen: layout 1, scrolling, transitions, the local screens, dim, off and pixel shift. |
| `src/bidi.h/.cpp` | Logical → visual reordering of UTF-8 text (§4.4). Pure C++, built natively for tests. |
| `src/icons.h` | **Generated.** The icon bitmaps and the ID table. |
| `src/fonts/*.h` | **Generated.** The two U8g2 fonts. |
| `src/main.cpp` | `setup()` and `loop()` only: sliders → buttons → receive → screens tick → connection check. |
| `assets/icons/manifest.txt`, `assets/icons/*.png`, `assets/fonts/*.ttf` (+ licence) | Asset sources. |
| `tools/gen_assets.py` | Generates `icons.h`, `fonts/*.h`, the Core icon enum (§5.4) and the simulator's icon data (§7). The outputs are checked in, so a normal build needs no Python. |
| `test/` | PlatformIO `native` environment tests (§8.3). |

`platformio.ini` adds U8g2 (`olikraus/U8g2`) to `lib_deps` and an `[env:native]` for tests.

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
```

- **Screen example:** a button entry `{4, IND_SCREEN(0, 0x3C)}`.
- **I2C is only started when needed.** A bus is started only if some button uses it. With no `IND_SCREEN` entries, the firmware never touches I2C, so the current hardware's LED on GPIO 21 (Arduino's default SDA) is unaffected.
- **Compile-time checks** (`static_assert`): at most two screens per bus, bus ∈ {0, 1}, address ∈ {0x3C, 0x3D}, and no two screens with the same bus and address.

### 4.3 Rendering

- **Drivers:** U8g2 full-buffer mode, one 1 KB frame buffer per screen. Bus 0 uses `U8G2_SSD1306_128X64_NONAME_F_HW_I2C` and bus 1 uses `…_F_2ND_HW_I2C`, with `setI2CAddress(address << 1)` and `setBusClock(I2C_CLOCK_HZ)`.
- **Redraw only on change:** new content, a scroll step, a transition frame, a pixel-shift step, or a local-state change. Static screens cost no I2C time.
- **Transitions.** Each transition is a function `draw(screen, old, new, progress 0–1)` in a table indexed by transition ID. Adding a transition means adding a function, a table entry, and a value in the shared enum.
  - The slide draws the old content offset by `−p·128` and the new content offset by `(1−p)·128` (mirrored for slide right), with an ease-in-out curve.
  - Expected frame rate: about 10 fps with 4 screens and about 15 fps with 2–3, limited by I2C at 400 kHz. If that looks rough, raise `I2C_CLOCK_HZ`.
- **The firmware keeps each screen's previous content** so it can draw the old side of a transition.
- **Text** is drawn with `drawUTF8` after `bidi` reordering (§4.4).

### 4.4 Fonts and right-to-left text

**Fonts**
- **Typeface:** Arimo, Regular and Bold (Apache 2.0, the font licence goes in `firmware/assets/fonts/`). It covers Latin and Hebrew in one consistent design.
- `gen_assets.py` turns it into two U8g2 fonts:
  - **top:** ≈10 px Regular;
  - **main:** ≈14 px Bold.
- **Glyph subset:** ASCII, Latin-1 Supplement, Hebrew (U+0590–U+05FF), and a few punctuation marks (`…`, `–`, `—`, `•`). Expected size is under 10 KB each.
- **Conversion toolchain:** `otf2bdf` → U8g2's `bdfconv`.
- **Missing glyphs** render as nothing (U8g2's behaviour). The app does not filter characters.

**`bidi` (a deliberately small subset of the Unicode bidi algorithm)**
1. **Classify each code point:**
   - **R:** Hebrew U+0590–U+05FF and U+FB1D–U+FB4F;
   - **L:** other letters;
   - **EN:** digits;
   - **N:** everything else: spaces, punctuation, brackets.
2. **Paragraph direction:** RTL if the first strong character (L or R) is R; otherwise LTR.
3. **Resolve characters into runs:**
   - **EN** joins the run of the strong character before it, or the paragraph direction when there is none.
   - **N** takes the direction of the strong characters on both sides when they agree, otherwise the paragraph direction.
4. **Reorder:**
   - reverse the characters inside each RTL run, mirroring `()[]{}<>`;
   - in an RTL paragraph, also reverse the order of the runs;
   - digit sequences inside RTL runs keep their left-to-right order.
5. **Layout:** an RTL paragraph is right-aligned when it overflows, and scrolls toward the left end (§2.1).

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
- **Button events:** before sending a button event, the loop asks `screens` whether the event should be swallowed (§2.4).
- **Screens tick:** runs every loop pass, does the minimum I2C work needed, and never blocks longer than one screen's redraw (~23 ms at 400 kHz).
- **Slider sampling** stays on its own 10 ms clock, so a long redraw delays a sample rather than dropping it.

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

- **`ConfigMigrator` v3 → v4** adds `devices: []` and `controller: null`, and is tested with the user's real config as a fixture.
- **A `devices` entry is created** the first time the user sets a nickname or an icon. It is removed when both are cleared, or by "Forget this device".
- **`friendlyName` is refreshed** whenever the device is seen live.
- **Device identity** is the endpoint `id`, as for `DeviceRefConfig`.

### 5.2 Form factor (`Volco.Windows` + Core)

This is the one change in `Volco.Windows`.

**Core**
- `AudioDevice` gains `DeviceFormFactor FormFactor`, defaulting to `Unknown`.
- `DeviceFormFactor` is a new Core enum: `Unknown, Speakers, Headphones, Headset, Microphone, Display, Other`.

**Windows**
- `DeviceRegistry` reads `PKEY_AudioEndpoint_FormFactor` once per endpoint, beside the friendly name it already reads. It maps:
  - Speakers → `Speakers`
  - Headphones → `Headphones`
  - Headset or Handset → `Headset`
  - Microphone → `Microphone`
  - DigitalAudioDisplayDevice → `Display`
  - anything else → `Other`
- A failed read gives `Unknown`.

**Icon for each form factor:** Speakers → `speaker`, Headphones → `headphones`, Headset → `headset`, Microphone → `mic`, Display → `display`, otherwise `generic`.

### 5.3 `ControllerDisplay` (replaces `ButtonFeedback`)

It lives in `Communication/` and is pure. It takes the config, the live preset ID, `IAudioEngine` and the stored controller info, and computes, per button, by the reported indicator kind:
- **Led / LedPair:** a value, using today's `ButtonFeedback` value rules:
  - mute: 1 while muted;
  - switch output: 1 while active;
  - toggle: 1 = A, 2 = B;
  - preset: 1 + live preset index.

  The action that drives it is picked by §2.2's first-action rule.
- **Screen:** a `ScreenContent`: top (empty / text / dots), icon, muted flag, main text. Built from §2.2.
- **None, or no controller info:** nothing.

**Helpers**
- `DeviceDisplay` resolves a device's **display name** and **icon**. It is shared by `ControllerDisplay`, the OSD and the settings UI.
- The rule: nickname → automatic short name; chosen icon → form-factor icon → `generic`.

### 5.4 Protocol code

- **`MessageType`** is rewritten to the §3.2 values.
- **`ProtocolParser`** decodes Hello, Slider and Button.
- **`ProtocolEncoder`** encodes HelloRequest, Heartbeat, Led, Screen, Begin and Commit, with the 60-byte UTF-8 truncation on a code-point boundary.
- **`ScreenIcon`** is the Core enum generated from the manifest (`ScreenIcon.g.cs`), so IDs can't drift. `ScreenTransition` and `IndicatorKind` are hand-written enums.

### 5.5 Runtime (`VolcoRuntime`)

- **Connect:** send HelloRequest, then repeat every 1 s until Hello arrives.
- **On Hello:** check the version (§3.4); save `controller` to the config (through `ConfigManager.RequestSave`); raise `ControllerInfoChanged`; clear the dedup cache; push everything.
- **Push:** `PushControllerState` sends `ControllerDisplay`'s output with per-button dedup. Re-push triggers are today's (device changes, mute changes, default output changes, preset changes, config changes) plus `devices` edits (nickname, icon).
- **Preset change:** the Begin → Screens (slide by direction) → LEDs → Commit sequence, gated by `SynchronizedPresetTransitions`.
- **Heartbeat:** a 1 s timer; it sends Heartbeat only when nothing else was sent in the last second.
- **Removed:** `VolcoRuntimeOptions.PostConnectRepushDelays` and its timers.
- **Added:** `VolcoRuntimeOptions.SynchronizedPresetTransitions` (default true) and `HelloRetryInterval` / `HeartbeatInterval`, so tests can shorten them.
- **Connection status:** gains the controller summary for the UI (§6.3) and the version-mismatch error.

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

- The **volume OSD** label uses the display name.
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
- The status panel adds "5 sliders · 3 buttons (2 LED, 1 LED pair)" from the last Hello.
- A version mismatch shows the §3.4 error in the status panel.

**Screen preview rendering**
- `Volco.App` draws layout 1 into a 128×64 bitmap and shows it with nearest-neighbour scaling, white on black.
- It uses the **same icon PNGs**, linked from `firmware/assets/icons` as Avalonia resources, and the **same Arimo TTF at the same pixel sizes**. Text is thresholded to 1-bit.
- The result is close, not pixel-identical. Nothing in `Volco.App` becomes platform-specific (the architecture tests still apply).

## 7. Web simulator (`tools/simulator/index.html`)

**Controller types.** A **controller type** picker offers:
- **LED controller:** 5 sliders; buttons LED, LED, LED pair (the user's hardware);
- **Screen controller:** 5 sliders; 4 screen buttons.

Changing the type disconnects. Hello is built from the selected type.

**Protocol**
- Speaks the §3 protocol:
  - answers HelloRequest with Hello;
  - sends Slider and Button;
  - applies Led, Screen, Begin and Commit with the 500 ms fallback;
  - tracks the heartbeat, and shows "not connected" after 3 s of silence.
- Shows the last received frames in a small log.

**Rendering**
- Screen buttons render layout 1 on a 128×64 canvas: scrolling, the synchronized slide, and the muted slash.
- Icons come from a generated `tools/simulator/icons.js` (written by `gen_assets.py`, so it works when the page is opened from disk). Text uses the browser's fonts, so it is approximate.
- Dim, off and pixel shift are not simulated.

## 8. Tests

### 8.1 Core (`tests/Volco.Core.Tests`)

**Protocol**
- Encode and parse every message type, including Hello with each indicator kind.
- Screen encoding: every top kind, the icon flags, and 60-byte truncation on a code-point boundary for ASCII, Hebrew and emoji.
- Frames over 255 bytes are rejected.

**`ControllerDisplay`**
- Every row of §2.2, and the first-non-None rule.
- Display-name and icon fallbacks, including a disconnected device.
- The muted flag, and the `n/N` top beyond 12 presets.
- The right output per indicator kind, and nothing without controller info.

**`DeviceDisplay`:** short-name rules (parentheses, nothing left after stripping, Hebrew names).

**Runtime** (fake engine, fake connection)
- HelloRequest retries until Hello.
- Hello stores the controller info and pushes everything.
- A version mismatch sets the error status and sends nothing.
- Heartbeat only when idle.
- Preset change: Begin, Screens with the right direction, Leds, Commit; and the same messages without Begin/Commit when the option is off.
- Dedup.
- A nickname edit re-pushes the affected screens.
- Kind-appropriate messages only.

**Other**
- Migration v3 → v4 with the user's real config as a fixture.
- `ScreenIcon.g.cs` matches `firmware/assets/icons/manifest.txt`.

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

**Nicknames elsewhere:** in device pickers and in the OSD label.

**Coverage:** `SettingsCoverageTests` is extended to the Devices page.

### 8.3 Firmware (`[env:native]`, run in a new Linux CI job in `build.yml`)

**Protocol**
- Frame parsing: resync after garbage, a bad checksum, max length.
- Decoding every PC message.
- Begin/Commit holding, replacement, and the 500 ms fallback.
- Host timeout.
- Hello encoding from a test button table.

**`bidi`**
- Hebrew only; Latin only; mixed in both paragraph directions.
- Parentheses mirrored; digits inside Hebrew.
- An empty string; a string with no strong characters.

### 8.4 Manual checklist (in the plan)

**On the current LED hardware**
- Hello arrives, and the cards show LED, LED and LED pair.
- LEDs follow actions.
- LEDs go dark 3 s after Volco quits.
- LEDs change together on a preset switch.

**After wiring screens**
- Layout and fonts; Hebrew; scrolling in both directions.
- The synchronized slide, and the unsynchronized slide with `SynchronizedPresetTransitions` off.
- The muted slash.
- Dim, off, pixel shift, and the swallowed wake press.
- The local "not connected" screen.

## 9. Docs

**`firmware/README.md`, rewritten for:**
- the new file layout;
- every `config.h` setting;
- the full protocol table;
- screen wiring and pin choices: free safe GPIOs on the current board are 13, 16, 17, 22, 23, 25, 26 and 27; avoid GPIO 21 while it drives an LED, and the strapping pins 0, 2 and 15;
- **migrating a button from LED to screen**;
- regenerating fonts and icons with `gen_assets.py`.

It still does not cover building, flashing or wiring diagrams.

**`CLAUDE.md`:** the architecture, wire-protocol and firmware sections updated.

**`2026-10-01-volco-ux-redesign.md`:** §4.3 and §7 point to this spec.

## 10. Future (not in this work)

- More layouts, such as a big-number volume screen for sliders with screens.
- More transitions, and per-transition durations in the protocol.
- Translating the fixed screen words.
- A faster baud rate (the user's own experiment).
- An app-side setting to override burn-in timings (today they are firmware constants by choice).

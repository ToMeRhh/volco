# Volco Controller Firmware

ESP32 firmware for the Volco hardware controller. This document explains how the code is organised, how to describe a different hardware layout, and the wire protocol. It does not cover building, flashing or wiring diagrams.

## What the firmware does, and what it doesn't

The controller is a plain input and display surface:

- It reports **raw input**: every slider's position and every button's press and release. At boot, on request, and after a silence from the PC it also reports its **hardware** (Hello): how many sliders and buttons it has, and what each button shows. Every Hello is followed by every slider's current position, so the PC knows where the sliders are without waiting for them to move.
- It shows **LED values the PC sends it**, one per button, rendered on that button's light.
- It **draws screens** from descriptions the PC sends (an icon, a top line, a name) on SSD1306 OLED modules.
- It runs **scrolling, slides and burn-in protection locally** (dimming, switching off, pixel shift), so the PC never streams frames.

It still knows nothing about what a button *means*. Single, double and long press detection, mute, output switching and presets all live in the Volco app. Changing what a button does never needs a reflash; only changing the physical hardware does.

## Files

| Path | Contents |
|---|---|
| `src/config.h` | Fixed constants: serial baud, buffer sizes, protocol timing. |
| `src/config_store.*` | Loads the controller config from NVS (namespace `volco`, key `cfg`), keeps its bytes for read replies, and saves new ones; keeps the previous Wi-Fi settings (key `wifi_prev`) and puts them back. |
| `src/config_session.*` | The config session: read replies, chunked writes, validate, store, reboot (spec §5.3, §5.4, §6.7). |
| `src/main.cpp` | `setup()` and `loop()`: input reading, the glue between the PC link and the hardware. |
| `src/transport.*` | USB serial (always on) and UDP over Wi-Fi (when enabled). |
| `src/slider_ends_store.*` | The sliders' learned ends in NVS (namespace `volco_ends`, key `ends2`: one blob of pin-keyed entries), apart from the controller config. |
| `src/pin_helpers.*` | The wiring helpers on the hardware: samples a watch's pins, runs the I2C scan, drives pins and puts them back. |
| `src/indicators.*` | LED rendering. |
| `src/screens.*` | The SSD1306 drivers: owns the panels and pushes the rendered buffers to them. |
| `src/debug_log.h` | The `DEBUG_LOG` macro. |
| `lib/volco_protocol/` | Frame parser and encoder, message decoding, Hello, Begin/Commit holding, keepalive and host timeout, the active-transport rule. |
| `lib/volco_config/` | The controller config: decode, validation, chip table, CRC, chunked receiver, `RuntimeConfig`. |
| `lib/volco_slider_ends/` | One slider's learned travel ends, and the mapping of its readings onto 0-4095 (the reverse direction is applied after it, last, in `volco_runtime.h`). |
| `lib/volco_calibration/` | One slider's calibration: Auto-calibrate, the Re-calibrate sweep (Done, Cancel, the 60 s timeout, host lost), when a Calibration report is due; and the stored entries, `{pin, auto-calibrate, low, high}`, looked up by pin. |
| `lib/volco_pin_helpers/` | The rules of the wiring helpers, pure: the pins in use, the pins a watch samples per role, when a pin is worth reporting (the thresholds), how an I2C scan reaches a pin pair, which pins may be driven. |
| `lib/volco_wifi_fallback/` | The Wi-Fi fallback's rules, pure: finding and replacing a top-level field of a config blob on the wire level, what a save does with the kept copy of the old Wi-Fi, and the window after a boot on new settings. |
| `lib/volco_bidi/` | Logical to visual reordering of UTF-8 text, for Hebrew. |
| `lib/volco_screen_state/` | The state machine of each screen: current, previous and pending content, slide progress, dim, off, pixel shift, wake handling. |
| `lib/volco_render/` | Volco's own 1-bit canvas and layout 1 renderer, with the generated fonts and icons. U8g2 is only the SSD1306 driver underneath. |
| `assets/` | Icon sources (`icons/manifest.txt` and a PNG per icon) and fonts (`fonts/`: Arimo, SIL Open Font License 1.1). |
| `tools/` | `draw_icons.py` and `gen_assets.py` (icons and fonts), `gen_config_vectors.py` (shared config test vectors), `config_tool.py` (read or write a controller's config over serial or UDP), `hello_check.py` (checks a board's boot Hello), `check_firmware_id_paths.py` (CI). |
| `scripts/` | `firmware_id.sh`: the firmware build id, a hash of the firmware's sources. |
| `test/` | Native tests of everything in `lib/`, and the shared `golden/frames.txt` and `golden/config.txt` that the C# tests read too. |

Everything in `lib/` is pure C++ with time passed in, so it builds and is tested on a PC with `pio test -e native`. Nothing in `lib/` touches Arduino.

## Configuration

The firmware is generic: nothing about a build's hardware is compiled in. At boot it loads the controller config from NVS (`config_store.cpp`; spec §6.6), decodes and validates it (`lib/volco_config`), and runs from the resulting `RuntimeConfig`:

- **Hardware:** the sliders (one ADC pin each), the buttons (an input pin, plus an indicator: none, one LED, an LED pair, or an SSD1306 screen on I2C bus 0-3 at 0x3C/0x3D), and the I2C buses' pins and clock. Buses 0 and 1 are the ESP32's I2C controllers; 2 and 3 are software I2C, for fixed-address screens that each need a bus of their own. **A list's order is the input number Volco sees.** Button pins use `INPUT_PULLUP` (pressed = connected to GND); LED polarity is `active_low`.
- **Wi-Fi/UDP:** off unless enabled; SSID, password, the PC's IP and port, and the local port. USB serial is always on.
- **Screens and input:** brightness, slide and scroll timing, dim/off/pixel-shift, debounce, slider sampling, smoothing and deadband, and each slider's reverse direction.

The schema, with every default and range, is `proto/volco_config.proto`. A config that is missing, oversized, or fails decoding or validation leaves the firmware on built-in defaults (no inputs, serial only), still answering Hello.

Volco's Controller page sets the config. For firmware work without the app, use `config_tool.py`. A layout is a preset JSON (the protobuf JSON mapping of `ControllerConfig`, like `proto/presets/diy-reference.json`):

```
~/.platformio/penv/bin/python firmware/tools/config_tool.py --port <port> write my-layout.json   # stores it; the board reboots into it
~/.platformio/penv/bin/python firmware/tools/config_tool.py --port <port> read                   # prints the stored config as JSON
```

A Wi-Fi change written over USB (while the stored Wi-Fi was on) is provisional: without a valid UDP frame from Volco within 30 s of the reboot, the controller restores its previous Wi-Fi settings and reboots (the Wi-Fi fallback, below).

`firmware/tools/hello_check.py --port <port> --sliders N --kinds 1,1,2` resets the board and confirms the Hello matches the layout. Fixed constants (serial baud, buffer sizes, protocol timing) stay in `src/config.h`.

## Wiring screens and choosing pins

- The ESP32 has **two hardware I2C buses** (0 and 1); the firmware adds **two software buses** (2 and 3). An SSD1306 answers on `0x3C` or `0x3D`, and a bus takes at most two screens. Two modules on the same bus need different addresses: move the address resistor on one of them to `0x3D`. Modules whose address is fixed need a bus each, so up to 4 of them.
- The software buses drive SCL push-pull and SDA open-drain, without clock stretching, which suits SSD1306 modules (they never hold SCL). A full frame on them takes about as long as on a hardware bus at 400 kHz, but the CPU is busy for all of it.
- Free, safe GPIOs on the current board: **13, 16, 17, 22, 23, 25, 26, 27**. Suggested pins: 25/26 for bus 0, 27/13 for bus 1, 16/17 for bus 2 and 22/23 for bus 3.
- Avoid GPIO 21 while it drives an LED (it is Arduino's default SDA), and avoid the strapping pins 0, 2 and 15.
- If slides look rough, raise the bus's `clock_hz` to 1000000. A full frame takes about 25-30 ms per panel at 400 kHz, and panels are updated one after another.

### Migrating a button from LED to screen

Change the button's indicator in its config from `led` to `screen` (bus and address), and make sure that bus has `sda`/`scl`. Do it in Volco's Controller page (or with `config_tool.py write`, see Configuration). The app picks the change up from Hello; there is nothing to change in Volco's actions.

## Wire protocol

Every message, in both directions, is a frame:

```
0xAA | length | type | body ... | checksum
```

`length` is the number of bytes in `type` plus `body` (1-255). `checksum` is the XOR of `type` and every body byte. A receiver hunts for `0xAA` and drops a frame whose length is 0 or whose checksum fails, which is also how it resynchronises after garbage such as boot-ROM output on the serial port. Frames can be up to 258 bytes on the wire.

The high bit of `type` is the direction: `0x0_` comes from the controller, `0x8_` from the PC. Multi-byte integers are big-endian.

| Type | Name | Body | Meaning |
|---|---|---|---|
| `0x01` | Hello | see below | The controller's chip, firmware, config and hardware. |
| `0x02` | Alive | none | Keepalive from the controller. |
| `0x03` | Slider | `index`, value u16 (0-4095) | A slider moved, or its position after a Hello. |
| `0x04` | Button | `index`, `pressed` (1 or 0) | A button changed. |
| `0x05` | ConfigData | `seq`, total u16, offset u16, crc32 u32, data (0-240 bytes) | One chunk of a config read reply. |
| `0x06` | ConfigResult | `seq`, `code`, `item`, `field` | The answer to a config write. |
| `0x09` | Calibration | `index`, `flags` (bit 0 learned, bit 1 sweeping, bit 2 auto-calibrate), low u16, high u16 | A slider's calibration: the answer to every Calibrate, when its ends widen (at most every 100 ms), and when a sweep ends. |
| `0x07` | PinActivity | `pin` | Detect: a watched pin moved (analog) or was pressed (button). |
| `0x08` | I2cScanResult | `sda`, `scl`, `count`, `count` addresses | The answer to an I2cScan: the addresses that acknowledged, 0x08-0x77 ascending (at most 112); count 0 for a refused pair. |
| `0x81` | HelloRequest | none | The PC asks for Hello. |
| `0x82` | Heartbeat | none | Keepalive from the PC. |
| `0x83` | Led | `button`, `value` | LED: lit when the value is not 0. LED pair: 1 lights A, 2 lights B, anything else neither. |
| `0x84` | Screen | see below | What a screen shows. |
| `0x85` | Begin | none | Start holding updates. |
| `0x86` | Commit | none | Apply everything held. |
| `0x87` | ConfigRead | `seq` | The PC asks for the stored config. |
| `0x88` | ConfigWriteBegin | `seq`, total u16, crc32 u32 | Starts a config write. |
| `0x89` | ConfigWriteChunk | `seq`, offset u16, data (1-240 bytes) | Part of a config write. |
| `0x8A` | ConfigWriteEnd | `seq` | Ends a config write. |
| `0x8B` | PinWatch | `role` (0 stop, 1 analog, 2 button) | The PC starts, renews or stops Detect's pin watch. |
| `0x8C` | I2cScan | `sda`, `scl` | The PC asks which I2C addresses answer on a pin pair (Find screens). |
| `0x8D` | PinDrive | `pin`, `level` (0 or 1), duration u16 (ms) | The PC drives a pin for a while (Try the lights). |
| `0x8E` | Calibrate | `index`, `action` (0 query, 1 start sweep, 2 done, 3 cancel) | The PC asks for a slider's calibration, or runs a Re-calibrate sweep. |

### Hello body

```
version      u8    1; a PC that doesn't speak this version reads only this byte
chip         u8    1 = ESP32
fwLen        u8    0-32
firmware     fwLen ASCII bytes: the firmware build id ("dev" for local builds)
configState  u8    0 none stored, 1 stored and running, 2 stored but unusable (running defaults)
configCrc    u32   CRC-32 of the stored bytes; 0 when none
sliders      u8
buttons      u8
kinds        one per button: 0 none, 1 LED, 2 LED pair, 3 screen
```

Every Hello is followed by one Slider frame per slider, with its current position.

### Screen body

```
button      u8
transition  u8    0 none, 1 slide left, 2 slide right
layout      u8    1
topKind     u8    0 empty, 1 text, 2 dots
  topKind 1:  len u8, len bytes of UTF-8
  topKind 2:  selected u8 (0-based), count u8
icon        u8    icon ID (assets/icons/manifest.txt); 0xFF = no icon
iconFlags   u8    bit 0 = muted slash; other bits 0
mainLen     u8
main        mainLen bytes of UTF-8
```

Text is UTF-8 in logical order, at most 60 bytes per slot; the app strips Hebrew combining marks first. A Screen whose lengths overrun the frame is dropped. An unknown `transition` is treated as 0, an unknown `layout` draws only the main text centred, and an unknown icon ID draws `generic`.

### Config read and write

The config is the protobuf blob of `proto/volco_config.proto`, at most 1024 bytes. Each request carries a `seq` that its replies echo, and replies go back on the transport the request came in on.

- **Read:** `ConfigRead` → the stored bytes as `ConfigData` frames, 240 bytes each (one empty frame when nothing is stored), all in one transport send. Each frame carries the CRC of the whole blob.
- **Write:** the PC sends `ConfigWriteBegin`, the chunks in order, then `ConfigWriteEnd`, all in one send. The controller answers once with `ConfigResult`. A transfer that stalls for 2 s is dropped without an answer. While one is open, chunks and End from the other transport are ignored, a wrong `seq` or offset answers BAD_SEQUENCE and ends the transfer, and a new Begin from either transport starts over.
- **After OK** the controller has stored the config; it flushes, waits 200 ms and reboots into it, so the next Hello reports state 1 and the new CRC. Any other answer leaves the old config in place.

| Code | Name | `item` / `field` |
|---|---|---|
| 0 | OK | 0xFF |
| 1 | BAD_SEQUENCE | 0xFF |
| 2 | BAD_CRC | 0xFF |
| 3 | TOO_LARGE | 0xFF |
| 4 | DECODE_FAILED | 0xFF |
| 5 | INVALID | the first failing rule (spec §6.4) |
| 6 | STORAGE_FAILED | 0xFF |

`tools/config_tool.py` speaks this from the command line (see Configuration).

### Behaviour rules

- **Hello** is sent at boot (which also covers the ESP32 resetting when the serial port is opened), in reply to every HelloRequest, and on the first valid frame from the PC while the PC is not considered alive (the first frame ever, or the first after a host timeout), unless that frame is itself a HelloRequest, whose reply covers it. Hello is always followed by one Slider frame per slider carrying its current smoothed value; the app sets the volumes from these without showing the volume OSD. The PC sends no Led or Screen on a connection until it has seen a Hello, then pushes every button's state.
- **Keepalive.** The controller sends Alive whenever it has sent nothing for 1 s. The PC sends Heartbeat the same way, but only once its session is Ready. Any valid frame from the PC counts as proof of life; after `HOST_TIMEOUT_MS` without one the LEDs go off and the screens show "not connected".
- **Begin / Commit.** After Begin the firmware holds every Led and Screen instead of applying it; on Commit it applies them all in the same pass, so every screen starts its slide at the same moment. A newer message for the same button replaces the held one, a second Begin restarts the timer, Commit with nothing held does nothing, and Commit without a Begin is ignored. If no Commit arrives within `COMMIT_FALLBACK_MS` of the Begin, the firmware applies what it holds.
- **Slider calibration** (`docs/design/slider-calibration.md`). Each slider learns its travel's ends from the smoothed reading, always with Auto-calibrate on (the default) and only during a sweep with it off. Until the span reaches 1000 codes a slider sends its raw reading, then the reading stretched between the ends. A sweep (Calibrate 1) forgets the ends and learns from scratch; Done (2) keeps them once learned, Cancel (3), a host timeout or 60 s without Done restore the ones before it. When a sweep ends the controller sends the Calibration, then a Slider frame with the position stretched by the final ends, whatever the deadband. Ends are stored at most every 5 s while learning, at once when a sweep ends, never during one; an entry is used only by the slider with its pin and Auto-calibrate setting, so changing either forgets them. A Calibrate for a slider past the slider count is ignored. **Reverse direction** (`Slider.reverse` in the config) is applied last to every Slider value the controller sends (the main loop, the values after a Hello, the value at the end of a sweep): `4095 − v`, after smoothing, the stretch and the deadband check; the Calibration report's ends and the pin watch stay raw.
- **Wiring helpers** (`docs/design/hardware-page.md` §9; normal input and output keep working while one runs). Older firmware doesn't answer them, and the app copes.
  - **Pin watch (PinWatch, PinActivity).** Role 1 (analog) samples the free ADC pins and the configured slider pins, role 2 (button) the free pins and the configured button pins, every 10 ms; pins of the other input kind are never touched, nor are LED pins or the pins of an I2C bus a screen uses. Analog skips ADC2 pins while Wi-Fi is enabled; button turns the pull-up on and leaves out free 34-39 (no pull-up, they would float; a button configured there is read as it is). A pin is free when no slider, button, LED or screen bus uses it. At most one PinActivity per pin per 250 ms. **A floating pin must not report:** an analog pin reports when its smoothed level is 300 codes from a baseline that follows it by 1/32 of the gap each sample, while the step between raw readings stays under 80 (a noisy pin never does, nor does a clean one drifting slowly), and the baseline then jumps to the level; a button pin reports after 30 ms low following 500 ms high, then again after it went high for 100 ms, and a pin that is low or chattering is never armed. The thresholds are constants in `volco_pin_helpers.h`, to be checked on hardware. The watch ends on role 0, on host loss, and 30 s after the last PinWatch (the app renews every 10 s and after each Hello); a PinWatch of the same role keeps each pin's state, another role starts clean. When it ends, the pins it changed go back to their mode.
  - **I2C scan (I2cScan, I2cScanResult).** Probes 0x08-0x77. A pair that is a configured bus's uses that bus (the hardware buses 0 and 1 through their driver, the software buses 2 and 3 bit-banged); any other usable pair is bit-banged at 100 kHz on those pins alone, with no clock stretching, so a shorted or unpulled bus ends the scan in milliseconds instead of hanging the loop. The pins and the bus go back afterwards. A pair that is not usable, an input-only pin, SDA = SCL, or a pair sharing a pin with a configured bus without being that bus answers count 0.
  - **PinDrive.** Drives an output-capable pin that is free or a configured LED pin to `level` for `duration` ms (up to four pins at once), else ignores it. A pin is restored when the drive ends: a free pin is released, an LED pin shows the PC's last value (a Led for a driven pin is held until the drive ends, and a mode change mid-flash is not shown until then).
- **Kind mismatches are ignored.** A Led for a button that is not an LED or LED pair, a Screen for a button that is not a screen, and any out-of-range index are dropped.

## Rendering

Screens are drawn into Volco's own 1-bit canvas (`lib/volco_render`) and U8g2 only sends the finished buffer to the SSD1306. That makes slides and pixel shift plain buffer copies, and lets the renderer be unit-tested on a PC.

Layout 1, in pixels on the 128x64 panel:

| Slot | Area | Content |
|---|---|---|
| Top | rows 0-15 | Text (10 px Regular, centred, baseline row 11) or dots (6x6 squares on a 10 px pitch, the selected one filled). |
| Icon | 28x28 at x 50, y 18 | An icon; the muted flag draws a diagonal slash. An animated icon (the playing equaliser) shows frame `(since / kIconFrameMs) % frames` of the screen's scroll clock, so it holds frame 0 during a slide. |
| Main | x 2-125, baseline row 60 | Text (14 px Bold), centred when it fits. |

Text wider than its area is left-aligned (right-aligned for Hebrew), holds for `scroll_hold_ms`, scrolls at `scroll_speed_px_s`, holds at the end, then jumps back. Hebrew is reordered into visual order by `volco_bidi` before drawing.

### Fonts and icons

Fonts and icons are generated by `tools/gen_assets.py`, never edited by hand:

```
python3 firmware/tools/gen_assets.py
```

It needs Python 3 and Pillow 10 or newer. It renders Arimo (10 px Regular and 14 px Bold) into a small Volco bitmap-font format and the icon PNGs into bitmaps, and also writes the C# icon enum and the simulator's icon and font data (`tools/simulator/icons.js`, `fonts.js`). `firmware/test/golden/render.txt` holds layout-1 renders that the simulator's tests compare against; after a renderer change, regenerate it with `VOLCO_WRITE_GOLDEN=1 pio test -e native -f test_render`. Commit its outputs; a normal build never runs it.

- **To change an icon**, edit its PNG in `assets/icons/` (28x28, 1-bit) and re-run the script.
- **To add one**, append a row to `assets/icons/manifest.txt`, add the PNG, and re-run the script.
- **An animated icon** has a frame count above 1 in the manifest's `frames` column and one PNG per frame, `<stem>_0.png`, `<stem>_1.png`, ...; every frame shows for `kIconFrameMs` (180 ms, `ICON_FRAME_MS` in the script). `screens.cpp` sends a panel only the 8x8 tiles that changed (`changedTiles`), so an animating icon costs about 128 bytes of I2C per frame.

## `main.cpp`

### Transports

`transport.cpp` runs USB serial always, and UDP over Wi-Fi when the config enables it (spec §5.5, §7). Each transport has its own frame parser. Until the PC is heard, every outgoing frame goes to every transport that is up; the first valid frame latches its transport (before the link replies), a frame on the other transport moves the latch, and a host timeout clears it. UDP is accepted only from the configured PC IP. `transport_maintain()` runs every pass: it re-joins a failed Wi-Fi at most every `WIFI_RETRY_MS`, and each time Wi-Fi connects the firmware sends a Hello on UDP.

**The Wi-Fi fallback** (`docs/design/hardware-page.md` §10.3). A config write that changes the Wi-Fi settings while the stored ones have Wi-Fi on first stores the old `Udp` section under NVS key `wifi_prev` (namespace `volco`), then the new config. A boot that finds `wifi_prev` runs the new settings provisionally: the first valid frame from the PC over UDP confirms them and drops the key (USB says nothing about the network). If none comes within `WIFI_FALLBACK_MS` (30 s) of boot, the old section is spliced back into the stored config, the rest unchanged, and the board checks it, stores it, drops the key and reboots; if any of that fails it keeps running on the settings it booted with, without a restart (a key left behind would restore again after every boot). A boot that finds the key next to a config that isn't stored or has Wi-Fi off (a power cut between a Wi-Fi-off save and the key's drop) drops it and runs normally. A write that turns Wi-Fi off, or one made while it was off, keeps no copy. The copy is always settings a PC has answered on: a write while the settings are still provisional leaves the copy as it is, unless it turns Wi-Fi off or goes back to the copy, which drops it (`wifiCopyOnSave`).

Debug output uses `DEBUG_LOG`, which prints `[DBG] ...` lines to USB serial only. They share the port with protocol frames; that is safe because the app's parser only acts on bytes that form a valid `0xAA` frame.

### Main loop

`loop()` never blocks for long and runs these steps on every pass, in this order:

1. **Sliders** (every `slider_interval_ms`): read each pin, smooth it with the moving average, let its calibration learn from it, and send a slider message (the value stretched between the learned ends, then reversed as `4095 − v` for a slider with `reverse`; `sliderOut`) when the smoothed value has moved more than `slider_deadband` since the last one sent. Then each slider's calibration: the sweep timeout, the report and fresh position when a sweep ended, a report when the ends widened, and storing the ends when due.
2. **Buttons**: debounce each pin and send a button message on every confirmed press or release. The debounce is timestamp-based, so a press is still seen when a screen redraw delayed the pass.
3. **Receive**: drain each transport, feeding each byte to its parser; each valid frame latches its transport, then goes to the link.
4. **Link tick**: keepalive, host timeout and the Commit fallback.
5. **Screens tick**: power state (dim, off, pixel shift), then at most **one panel redraw** per pass, so a redraw never starves input for long.
6. **Wi-Fi**: `transport_maintain()`; on a connect, a Hello on UDP. Then the fallback: confirm new settings, or put the old ones back and reboot.

## Tests

`pio test -e native` (from `firmware/`) runs the library tests for `volco_protocol`, `volco_config` (decode, validation, receiver, `RuntimeConfig`), `volco_slider_ends`, `volco_calibration`, `volco_pin_helpers`, `volco_wifi_fallback`, `volco_bidi`, `volco_screen_state` and `volco_render`. CI runs it too, checks the firmware id and the config vectors, and builds the `esp32` environment. The golden frames in `test/golden/frames.txt` and the config vectors in `test/golden/config.txt` are shared with the C# tests, so both ends agree on the bytes.

## Developing the firmware

```bash
cd firmware && pio run -e esp32 -t upload   # build and flash local firmware
cd firmware && pio device monitor           # DEBUG_LOG output
cd firmware && pio test -e native           # host-side tests (needs a host g++)
```

- **Same firmware, `dev` build id.** A local build reports `dev` in Hello unless `VOLCO_FW_VERSION` is set (CI sets it to `bash scripts/firmware_id.sh`, a hash of the firmware's sources, so a release whose firmware didn't change offers no update). Volco shows "Development firmware" and never offers to replace it; **Install firmware…** brings back the bundled build.
- **The config survives.** `pio run -t upload` writes the bootloader, the partition table, `boot_app0` and the app at their offsets and never touches NVS (0x9000–0xDFFF). PlatformIO patches the bootloader header on upload, which is harmless: Volco's probe compares the app only.
- **The build writes `dist/`.** `scripts/manifest.py` copies the four parts to `.pio/build/esp32/dist/` with `firmware-manifest.json` (offsets, sizes, MD5s, the NVS range), which is what Volco installs; it fails the build if a part would overlap NVS (`python scripts/manifest.py --selftest` tests that check).
- **Pins:** GPIO 34–39 are input-only with no internal pull-up (buttons there need a resistor to 3.3 V); 37/38 aren't pinned out on WROOM modules; 16/17 are used by PSRAM on WROVER/PICO boards.

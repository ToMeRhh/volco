# Volco Controller Firmware

ESP32 firmware for the Volco hardware controller. This document explains how the code is organised, how to describe a different hardware layout, and the wire protocol. It does not cover building, flashing or wiring diagrams.

## What the firmware does, and what it doesn't

The controller is a plain input and display surface:

- It reports **raw input**: every slider's position and every button's press and release. At boot, on request, and after a silence from the PC it also reports its **hardware** (Hello): how many sliders and buttons it has, and what each button shows.
- It shows **LED values the PC sends it**, one per button, rendered on that button's light.
- It **draws screens** from descriptions the PC sends (an icon, a top line, a name) on SSD1306 OLED modules.
- It runs **scrolling, slides and burn-in protection locally** (dimming, switching off, pixel shift), so the PC never streams frames.

It still knows nothing about what a button *means*. Single, double and long press detection, mute, output switching and presets all live in the Volco app. Changing what a button does never needs a reflash; only changing the physical hardware does.

## Files

| Path | Contents |
|---|---|
| `src/config.h` | Fixed constants: serial baud, buffer sizes, protocol timing. |
| `src/config_store.*` | Loads the controller config from NVS (and, in stage 3, stores the seed). |
| `src/seed_config.h` | Generated stage 3 seed (DIY reference). |
| `src/main.cpp` | `setup()` and `loop()`: input reading, the glue between the PC link and the hardware. |
| `src/transport.*` | USB serial (always on) and UDP over Wi-Fi (when enabled). |
| `src/indicators.*` | LED rendering. |
| `src/screens.*` | The SSD1306 drivers: owns the panels and pushes the rendered buffers to them. |
| `src/debug_log.h` | The `DEBUG_LOG` macro. |
| `lib/volco_protocol/` | Frame parser and encoder, message decoding, Hello, Begin/Commit holding, keepalive and host timeout, the active-transport rule. |
| `lib/volco_config/` | The controller config: decode, validation, chip table, CRC, chunked receiver, `RuntimeConfig`. |
| `lib/volco_bidi/` | Logical to visual reordering of UTF-8 text, for Hebrew. |
| `lib/volco_screen_state/` | The state machine of each screen: current, previous and pending content, slide progress, dim, off, pixel shift, wake handling. |
| `lib/volco_render/` | Volco's own 1-bit canvas and layout 1 renderer, with the generated fonts and icons. U8g2 is only the SSD1306 driver underneath. |
| `assets/` | Icon sources (`icons/manifest.txt` and a PNG per icon) and fonts (`fonts/`: Arimo, SIL Open Font License 1.1). |
| `tools/` | `draw_icons.py` and `gen_assets.py` (icons and fonts), `gen_config_vectors.py` (shared config test vectors), `gen_seed_config.py` (stage 3 seed), `hello_check.py` (checks a board's boot Hello), `check_firmware_id_paths.py` (CI). |
| `scripts/` | `firmware_id.sh`: the firmware build id, a hash of the firmware's sources. |
| `test/` | Native tests of everything in `lib/`, and the shared `golden/frames.txt` and `golden/config.txt` that the C# tests read too. |

Everything in `lib/` is pure C++ with time passed in, so it builds and is tested on a PC with `pio test -e native`. Nothing in `lib/` touches Arduino.

## Configuration

The firmware is generic: nothing about a build's hardware is compiled in. At boot it loads the controller config from NVS (`config_store.cpp`; spec §6.6), decodes and validates it (`lib/volco_config`), and runs from the resulting `RuntimeConfig`:

- **Hardware:** the sliders (one ADC pin each), the buttons (an input pin, plus an indicator: none, one LED, an LED pair, or an SSD1306 screen on I2C bus 0/1 at 0x3C/0x3D), and the I2C buses' pins and clock. **A list's order is the input number Volco sees.** Button pins use `INPUT_PULLUP` (pressed = connected to GND); LED polarity is `active_low`.
- **Wi-Fi/UDP:** off unless enabled; SSID, password, the PC's IP and port, and the local port. USB serial is always on.
- **Screens and input:** brightness, slide and scroll timing, dim/off/pixel-shift, debounce, slider sampling, smoothing and deadband.

The schema, with every default and range, is `proto/volco_config.proto`. A config that is missing, oversized, or fails decoding or validation leaves the firmware on built-in defaults (no inputs, serial only), still answering Hello.

**Until Volco can send a config (stage 4),** an empty NVS is seeded from `src/seed_config.h`. To try another layout, write a preset JSON (the protobuf JSON mapping of `ControllerConfig`, like `proto/presets/diy-reference.json`), then:

```
~/.platformio/penv/bin/python firmware/tools/gen_seed_config.py my-layout.json
esptool --port <port> erase-region 0x9000 0x5000
cd firmware && pio run -e esp32 -t upload
```

`firmware/tools/hello_check.py --port <port> --sliders N --kinds 1,1,2` resets the board and confirms the Hello matches the layout. Fixed constants (serial baud, buffer sizes, protocol timing) stay in `src/config.h`.

## Wiring screens and choosing pins

- The ESP32 has **two hardware I2C buses**, and an SSD1306 answers on `0x3C` or `0x3D`, so there are at most **4 screens**. Two modules on the same bus need different addresses: move the address resistor on at least one module per bus to `0x3D`.
- Free, safe GPIOs on the current board: **13, 16, 17, 22, 23, 25, 26, 27**. The defaults use 25/26 for bus 0 and 27/13 for bus 1.
- Avoid GPIO 21 while it drives an LED (it is Arduino's default SDA), and avoid the strapping pins 0, 2 and 15.
- If slides look rough, raise the bus's `clock_hz` to 1000000. A full frame takes about 25-30 ms per panel at 400 kHz, and panels are updated one after another.

### Migrating a button from LED to screen

Change the button's indicator in its config from `led` to `screen` (bus and address), and make sure that bus has `sda`/`scl`. In stage 3 that is a seed change (see Configuration); from stage 4, Volco's Controller page. The app picks the change up from Hello; there is nothing to change in Volco's actions.

## Wire protocol

Every message, in both directions, is a frame:

```
0xAA | length | type | body ... | checksum
```

`length` is the number of bytes in `type` plus `body` (1-255). `checksum` is the XOR of `type` and every body byte. A receiver hunts for `0xAA` and drops a frame whose length is 0 or whose checksum fails, which is also how it resynchronises after garbage such as boot-ROM output on the serial port. Frames can be up to 258 bytes on the wire.

The high bit of `type` is the direction: `0x0_` comes from the controller, `0x8_` from the PC. Multi-byte integers are big-endian.

| Type | Name | Body | Meaning |
|---|---|---|---|
| `0x01` | Hello | `version` (1), `sliders`, `buttons`, then one `kind` per button: 0 none, 1 LED, 2 LED pair, 3 screen | The controller's hardware. |
| `0x02` | Alive | none | Keepalive from the controller. |
| `0x03` | Slider | `index`, value u16 (0-4095) | A slider moved. |
| `0x04` | Button | `index`, `pressed` (1 or 0) | A button changed. |
| `0x81` | HelloRequest | none | The PC asks for Hello. |
| `0x82` | Heartbeat | none | Keepalive from the PC. |
| `0x83` | Led | `button`, `value` | LED: lit when the value is not 0. LED pair: 1 lights A, 2 lights B, anything else neither. |
| `0x84` | Screen | see below | What a screen shows. |
| `0x85` | Begin | none | Start holding updates. |
| `0x86` | Commit | none | Apply everything held. |

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

### Behaviour rules

- **Hello** is sent at boot (which also covers the ESP32 resetting when the serial port is opened), in reply to every HelloRequest, and on the first valid frame from the PC while the PC is not considered alive (the first frame ever, or the first after a host timeout), unless that frame is itself a HelloRequest, whose reply covers it. The PC sends no Led or Screen on a connection until it has seen a Hello, then pushes every button's state.
- **Keepalive.** The controller sends Alive whenever it has sent nothing for 1 s. The PC sends Heartbeat the same way, but only once its session is Ready. Any valid frame from the PC counts as proof of life; after `HOST_TIMEOUT_MS` without one the LEDs go off and the screens show "not connected".
- **Begin / Commit.** After Begin the firmware holds every Led and Screen instead of applying it; on Commit it applies them all in the same pass, so every screen starts its slide at the same moment. A newer message for the same button replaces the held one, a second Begin restarts the timer, Commit with nothing held does nothing, and Commit without a Begin is ignored. If no Commit arrives within `COMMIT_FALLBACK_MS` of the Begin, the firmware applies what it holds.
- **Kind mismatches are ignored.** A Led for a button that is not an LED or LED pair, a Screen for a button that is not a screen, and any out-of-range index are dropped.

## Rendering

Screens are drawn into Volco's own 1-bit canvas (`lib/volco_render`) and U8g2 only sends the finished buffer to the SSD1306. That makes slides and pixel shift plain buffer copies, and lets the renderer be unit-tested on a PC.

Layout 1, in pixels on the 128x64 panel:

| Slot | Area | Content |
|---|---|---|
| Top | rows 0-15 | Text (10 px Regular, centred, baseline row 11) or dots (6x6 squares on a 10 px pitch, the selected one filled). |
| Icon | 28x28 at x 50, y 18 | An icon; the muted flag draws a diagonal slash. |
| Main | x 2-125, baseline row 60 | Text (14 px Bold), centred when it fits. |

Text wider than its area is left-aligned (right-aligned for Hebrew), holds for `scroll_hold_ms`, scrolls at `scroll_speed_px_s`, holds at the end, then jumps back. Hebrew is reordered into visual order by `volco_bidi` before drawing.

### Fonts and icons

Fonts and icons are generated by `tools/gen_assets.py`, never edited by hand:

```
python3 firmware/tools/gen_assets.py
```

It needs Python 3 and Pillow 10 or newer. It renders Arimo (10 px Regular and 14 px Bold) into a small Volco bitmap-font format and the icon PNGs into bitmaps, and also writes the C# icon enum and the simulator's icon data. Commit its outputs; a normal build never runs it.

- **To change an icon**, edit its PNG in `assets/icons/` (28x28, 1-bit) and re-run the script.
- **To add one**, append a row to `assets/icons/manifest.txt`, add the PNG, and re-run the script.

## `main.cpp`

### Transports

`transport.cpp` runs USB serial always, and UDP over Wi-Fi when the config enables it (spec §5.5, §7). Each transport has its own frame parser. Until the PC is heard, every outgoing frame goes to every transport that is up; the first valid frame latches its transport (before the link replies), a frame on the other transport moves the latch, and a host timeout clears it. UDP is accepted only from the configured PC IP. `transport_maintain()` runs every pass: it re-joins a failed Wi-Fi at most every `WIFI_RETRY_MS`, and each time Wi-Fi connects the firmware sends a Hello on UDP.

Debug output uses `DEBUG_LOG`, which prints `[DBG] ...` lines to USB serial only. They share the port with protocol frames; that is safe because the app's parser only acts on bytes that form a valid `0xAA` frame.

### Main loop

`loop()` never blocks for long and runs these steps on every pass, in this order:

1. **Sliders** (every `slider_interval_ms`): read each pin, smooth it with the moving average, and send a slider message when the smoothed value has moved more than `slider_deadband` since the last one sent.
2. **Buttons**: debounce each pin and send a button message on every confirmed press or release. The debounce is timestamp-based, so a press is still seen when a screen redraw delayed the pass.
3. **Receive**: drain each transport, feeding each byte to its parser; each valid frame latches its transport, then goes to the link.
4. **Link tick**: keepalive, host timeout and the Commit fallback.
5. **Screens tick**: power state (dim, off, pixel shift), then at most **one panel redraw** per pass, so a redraw never starves input for long.
6. **Wi-Fi**: `transport_maintain()`; on a connect, a Hello on UDP.

## Tests

`pio test -e native` (from `firmware/`) runs the library tests for `volco_protocol`, `volco_config` (decode, validation, receiver, `RuntimeConfig`), `volco_bidi`, `volco_screen_state` and `volco_render`. CI runs it too, checks the firmware id and the config vectors, and builds the `esp32` environment. The golden frames in `test/golden/frames.txt` and the config vectors in `test/golden/config.txt` are shared with the C# tests, so both ends agree on the bytes.

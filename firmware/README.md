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
| `src/config.h` | Everything specific to one build: the transport, Wi-Fi settings, the hardware tables (sliders, buttons, indicators), screen settings and tuning constants. This is the file you edit. |
| `src/hardware.h` | The types the hardware tables are written in (`ButtonDef`, `Indicator`) and the `IND_*` / `ACTIVE_*` helpers. |
| `src/main.cpp` | `setup()` and `loop()`: input reading, the glue between the PC link and the hardware. |
| `src/transport.*` | The four `TRANSPORT_*` implementations behind one small interface. |
| `src/indicators.*` | LED rendering. |
| `src/screens.*` | The SSD1306 drivers: owns the panels and pushes the rendered buffers to them. |
| `src/debug_log.h` | The `DEBUG_LOG` macro. |
| `lib/volco_protocol/` | Frame parser and encoder, message decoding, Hello, Begin/Commit holding, keepalive and host timeout. |
| `lib/volco_bidi/` | Logical to visual reordering of UTF-8 text, for Hebrew. |
| `lib/volco_screen_state/` | The state machine of each screen: current, previous and pending content, slide progress, dim, off, pixel shift, wake handling. |
| `lib/volco_render/` | Volco's own 1-bit canvas and layout 1 renderer, with the generated fonts and icons. U8g2 is only the SSD1306 driver underneath. |
| `assets/` | Icon sources (`icons/manifest.txt` and a PNG per icon) and fonts (`fonts/`: Arimo, SIL Open Font License 1.1). |
| `tools/` | `draw_icons.py` (draws the icon PNGs) and `gen_assets.py` (generates fonts, icons, the C# icon enum and the simulator's icon data from `assets/`). |
| `test/` | Native tests of everything in `lib/`, and the shared `golden/frames.txt` that the C# tests read too. |

Everything in `lib/` is pure C++ with time passed in, so it builds and is tested on a PC with `pio test -e native`. Nothing in `lib/` touches Arduino.

## `config.h`

### Transport

Serial is the default. To switch, uncomment one of the other three `#define`s at the top of the transport section; the `TRANSPORT_SERIAL` line below them is guarded so it turns itself off when another is defined (the same guard lets CI build the others with `-DTRANSPORT_UDP` and so on). If more or fewer than one ends up defined, the build stops with `Define exactly one TRANSPORT_*`.

| Define | Link |
|---|---|
| `TRANSPORT_SERIAL` | USB serial. Wi-Fi is switched off. |
| `TRANSPORT_WEBSOCKET` | Wi-Fi, as a WebSocket client of the app (`SERVER_IP`:`SERVER_PORT`). |
| `TRANSPORT_UDP` | Wi-Fi, sending UDP to the app and listening on `UDP_LOCAL_PORT`. |
| `TRANSPORT_LOCAL_DEBUG` | No PC. Outgoing frames are dropped and input is logged to serial. |

### Wi-Fi (only used by the WebSocket and UDP transports)

| Setting | Meaning |
|---|---|
| `WIFI_SSID` | Network name. |
| `WIFI_PASSWORD` | Network password. |
| `SERVER_IP` | The PC running Volco. |
| `SERVER_PORT` | WebSocket port or UDP port on the PC. |
| `UDP_LOCAL_PORT` | Port this controller listens on for UDP from the PC. |

### Serial

| Setting | Meaning |
|---|---|
| `SERIAL_BAUD` | Must match the baud rate on Volco's Connection page. Used by every transport: for the protocol on USB, for the debug log on the others. |

### Hardware layout

**Position in a table is the input number.** The first entry in `SLIDER_PINS` is slider 0, the first entry in `BUTTONS` is button 0, and so on. The app identifies controls only by these numbers, and its settings window lists them in the same order.

```cpp
// Analog sliders: one ADC pin per slider.
static constexpr uint8_t SLIDER_PINS[] = {33, 35, 32, 36, 34};

// Buttons: { input pin, indicator }.
static constexpr ButtonDef BUTTONS[] = {
    {4, IND_LED(12, ACTIVE_LOW)},
    {14, IND_LED(21, ACTIVE_LOW)},
    {5, IND_LED_PAIR(18, 19, ACTIVE_HIGH)},
};
```

- **Slider pins** are read with 12-bit `analogRead` (0-4095).
- **Button pins** use `INPUT_PULLUP`: a button connects its pin to GND when pressed.
- **Indicators** say what a button has to show its state with. The firmware reports the kind in Hello, and the app decides what to send.

| Indicator | Hardware | What the PC sends |
|---|---|---|
| `IND_NONE` | nothing | nothing |
| `IND_LED(pin, polarity)` | one LED | a value: 0 is off, anything else on |
| `IND_LED_PAIR(pinA, pinB, polarity)` | two LEDs | a value: 0 both off, 1 lights A, 2 lights B |
| `IND_SCREEN(bus, address)` | an SSD1306 128x64 screen | a screen description (icon, top, name) |

`polarity` is `ACTIVE_HIGH` when driving the pin HIGH lights the LED, or `ACTIVE_LOW` when driving it LOW does. `bus` is 0-3 (0 and 1 are hardware I2C, 2 and 3 software I2C), `address` is `0x3C` or `0x3D`. A button has a screen or LEDs, never both. Every indicator starts dark at boot.

To describe different hardware, edit these two tables. The sizes of every array in `main.cpp` are derived from them, so there is nothing else to keep in sync.

### Screens (only used when a button is `IND_SCREEN`)

| Setting | Meaning |
|---|---|
| `I2C0_SDA`, `I2C0_SCL` | Bus 0 data and clock pins. |
| `I2C1_SDA`, `I2C1_SCL` | Bus 1 data and clock pins. A bus is only started when a screen uses it. |
| `I2C2_SDA`, `I2C2_SCL`, `I2C3_SDA`, `I2C3_SCL` | Data and clock pins of the software buses 2 and 3. |
| `I2C_CLOCK_HZ` | Speed of buses 0 and 1. 400000 by default; SSD1306 modules usually tolerate 1000000. |
| `I2C_SW_CLOCK_HZ` | Target speed of buses 2 and 3, 400000 by default. Lower it if a screen on them glitches. |
| `SCREEN_BRIGHTNESS` | Normal brightness (contrast), 0-255. |
| `SCREEN_TRANSITION_MS` | How long a slide between two screens takes. |
| `SCREEN_SCROLL_HOLD_MS` | Long text waits this long before it scrolls. |
| `SCREEN_SCROLL_SPEED_PX_S` | How fast long text scrolls. |
| `SCREEN_SCROLL_END_HOLD_MS` | Pause at the end of the text before it jumps back. |

Burn-in protection. Any button press or slider move counts as activity.

| Setting | Meaning |
|---|---|
| `SCREEN_DIM_ENABLED` | Dim the screens when idle. |
| `SCREEN_DIM_AFTER_MS` | ...after this long without activity (2 minutes). |
| `SCREEN_DIM_BRIGHTNESS` | ...to this brightness, 0-255. |
| `SCREEN_OFF_ENABLED` | Turn the screens off when idle. |
| `SCREEN_OFF_AFTER_MS` | ...after this long without activity (30 minutes). |
| `SCREEN_PIXEL_SHIFT_ENABLED` | Nudge the content by 1 px now and then. |
| `SCREEN_PIXEL_SHIFT_EVERY_MS` | ...this often (3 minutes). |
| `SCREEN_WAKE_SWALLOWS_PRESS` | A press that wakes dark screens is not sent to the PC. |

### Connection to the PC

| Setting | Meaning |
|---|---|
| `HOST_TIMEOUT_MS` | No frame from the PC for this long: LEDs off, screens show "not connected". |
| `KEEPALIVE_MS` | Send Alive when nothing else was sent for this long. |
| `COMMIT_FALLBACK_MS` | Apply held updates if a Begin's Commit never arrives. |
| `RECONNECT_INTERVAL_MS` | How often the link is checked and, if needed, restarted. |

### Input tuning

| Setting | Meaning |
|---|---|
| `DEBOUNCE_MS` | A button's raw reading must be stable this long before a change is reported. |
| `SLIDER_READ_INTERVAL_MS` | How often sliders are sampled. |
| `FILTER_WEIGHT` | Exponential moving average weight for slider readings (0-1). Lower is smoother but slower. |
| `SLIDER_DEADBAND` | A smoothed slider reading must move more than this many ADC units before it is reported. |
| `SLIDER_SEED_READS` | Reads averaged for each slider's starting value at boot (reported right after the boot Hello). |

## Wiring screens and choosing pins

- The ESP32 has **two hardware I2C buses** (0 and 1); the firmware adds **two software buses** (2 and 3). An SSD1306 answers on `0x3C` or `0x3D`, and a bus takes at most two screens. Two modules on the same bus need different addresses: move the address resistor on one of them to `0x3D`. Modules whose address is fixed need a bus each, so up to 4 of them.
- The software buses drive SCL push-pull and SDA open-drain, without clock stretching, which suits SSD1306 modules (they never hold SCL). A full frame on them takes about as long as on a hardware bus at 400 kHz, but the CPU is busy for all of it.
- Free, safe GPIOs on the current board: **13, 16, 17, 22, 23, 25, 26, 27**. The defaults use 25/26 for bus 0, 27/13 for bus 1, 16/17 for bus 2 and 22/23 for bus 3.
- Avoid GPIO 21 while it drives an LED (it is Arduino's default SDA), and avoid the strapping pins 0, 2 and 15.
- If slides look rough, try `I2C_CLOCK_HZ 1000000`. A full frame takes about 25-30 ms per panel at 400 kHz, and panels are updated one after another.

### Migrating a button from LED to screen

Change its entry in `BUTTONS` from `IND_LED(...)` to `IND_SCREEN(0, 0x3C)` (pick the bus and address), set the bus pins, and reflash. The app picks the change up from Hello. There is nothing to change in Volco.

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
| `0x03` | Slider | `index`, value u16 (0-4095) | A slider moved, or its position after a Hello. |
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

- **Hello** is sent at boot (which also covers the ESP32 resetting when the serial port is opened), in reply to every HelloRequest, and on the first valid frame from the PC while the PC is not considered alive (the first frame ever, or the first after a host timeout), unless that frame is itself a HelloRequest, whose reply covers it. Hello is always followed by one Slider frame per slider carrying its current smoothed value; the app sets the volumes from these without showing the volume OSD. The PC sends no Led or Screen on a connection until it has seen a Hello, then pushes every button's state.
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

Text wider than its area is left-aligned (right-aligned for Hebrew), holds for `SCREEN_SCROLL_HOLD_MS`, scrolls at `SCREEN_SCROLL_SPEED_PX_S`, holds at the end, then jumps back. Hebrew is reordered into visual order by `volco_bidi` before drawing.

### Fonts and icons

Fonts and icons are generated by `tools/gen_assets.py`, never edited by hand:

```
python3 firmware/tools/gen_assets.py
```

It needs Python 3 and Pillow 10 or newer. It renders Arimo (10 px Regular and 14 px Bold) into a small Volco bitmap-font format and the icon PNGs into bitmaps, and also writes the C# icon enum and the simulator's icon data. Commit its outputs; a normal build never runs it.

- **To change an icon**, edit its PNG in `assets/icons/` (28x28, 1-bit) and re-run the script.
- **To add one**, append a row to `assets/icons/manifest.txt`, add the PNG, and re-run the script.

## `main.cpp`

### Transport abstraction

Each transport in `transport.cpp` is a block guarded by its `TRANSPORT_*` define. Every block implements the same five functions, and nothing outside them knows which link is in use:

| Function | Purpose |
|---|---|
| `transport_init()` | Bring the link up (and the serial port for logging). |
| `transport_connected()` | Whether the link is currently usable. |
| `transport_send(data, len)` | Send bytes to the app. |
| `transport_receive(buf, maxLen)` | Non-blocking read of whatever bytes have arrived. Returns the count. |
| `transport_reconnect()` | Try to restore a lost link (a no-op where the link manages itself). |

Debug output uses `DEBUG_LOG`, which prints `[DBG] ...` lines to `Serial`. On `TRANSPORT_SERIAL` these share the port with protocol frames. That is safe because the app's parser only acts on bytes that form a valid `0xAA` frame.

### Main loop

`loop()` never blocks for long and runs these steps on every pass, in this order:

1. **Sliders** (every `SLIDER_READ_INTERVAL_MS`): read each pin, smooth it with the moving average, and send a slider message when the smoothed value has moved more than `SLIDER_DEADBAND` since the last one sent.
2. **Buttons**: debounce each pin and send a button message on every confirmed press or release. The debounce is timestamp-based, so a press is still seen when a screen redraw delayed the pass.
3. **Receive**: drain everything the transport has, feeding each byte to the frame parser and each valid frame to the link.
4. **Link tick**: keepalive, host timeout and the Commit fallback.
5. **Screens tick**: power state (dim, off, pixel shift), then at most **one panel redraw** per pass, so a redraw never starves input for long.
6. **Connection** (every `RECONNECT_INTERVAL_MS`): if `transport_connected()` is false, call `transport_reconnect()`.

## Tests

`pio test -e native` (from `firmware/`) runs the library tests for `volco_protocol`, `volco_bidi`, `volco_screen_state` and `volco_render`. CI runs it too, and also builds the `denky32` environment plus the build-only `denky32_screens_check`, `denky32_udp_check` and `denky32_websocket_check` environments. The golden frames in `test/golden/frames.txt` are shared with the C# tests, so both ends agree on the bytes.

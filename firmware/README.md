# Volco Controller Firmware

ESP32 firmware for the Volco hardware controller. This document explains how the code is organised and how to describe a different hardware layout. It does not cover building, flashing or wiring.

## What the firmware does, and what it doesn't

The controller is a plain input and display surface:

- It reports **raw input**: every slider's position and every button's press and release.
- It shows **feedback the PC sends it**: one value per button, rendered on that button's light.

It has no idea what a button *means*. Single, double and long press detection, mute, output switching and presets all live in the Volco app. Changing what a button does never needs a reflash; only changing the physical hardware does.

## Files

| File | Contents |
|---|---|
| `src/config.h` | Everything specific to one build: the transport, Wi-Fi settings, the hardware tables (sliders, buttons, indicators) and tuning constants. This is the file you edit. |
| `src/hardware.h` | The types the hardware tables are written in (`ButtonDef`, `Indicator`) and the `IND_*` / `ACTIVE_*` helpers. |
| `src/main.cpp` | The transport implementations, the wire protocol (sending and parsing frames), indicator rendering, `setup()` and `loop()`. |

## `config.h`

### Transport

Exactly one of these must be defined:

| Define | Link |
|---|---|
| `TRANSPORT_SERIAL` | USB serial. Wi-Fi is switched off. |
| `TRANSPORT_WEBSOCKET` | Wi-Fi, as a WebSocket client of the app (`SERVER_IP`:`SERVER_PORT`). |
| `TRANSPORT_UDP` | Wi-Fi, sending UDP to the app and listening on `UDP_LOCAL_PORT`. |
| `TRANSPORT_LOCAL_DEBUG` | No PC. Outgoing frames are dropped and input is logged to serial. |

`WIFI_SSID`, `WIFI_PASSWORD`, `SERVER_IP`, `SERVER_PORT` and `UDP_LOCAL_PORT` are only used by the Wi-Fi transports. `SERIAL_BAUD` is used by every transport, for the protocol on USB and for the debug log on the others.

### Hardware tables

**Position in a table is the input number.** The first entry in `SLIDER_PINS` is slider 0, the first entry in `BUTTONS` is button 0, and so on. The app identifies controls only by these numbers, and its settings window lists them in the same order.

```cpp
// One ADC pin per slider.
static const uint8_t SLIDER_PINS[] = {33, 35, 32, 36, 34};

// { input pin, indicator } per button.
static const ButtonDef BUTTONS[] = {
    {4, IND_LED(12, ACTIVE_LOW)},
    {14, IND_LED(21, ACTIVE_LOW)},
    {5, IND_LED_PAIR(18, 19, ACTIVE_HIGH)},
};
```

- **Slider pins** are read with 12-bit `analogRead` (0–4095).
- **Button pins** use `INPUT_PULLUP`: a button connects its pin to GND when pressed.
- **Indicators** say how a button shows its feedback value:

| Indicator | Hardware | Value 0 | Value 1 | Value 2 |
|---|---|---|---|---|
| `IND_NONE` | no light | — | — | — |
| `IND_LED(pin, polarity)` | one LED | off | on | on (any value but 0) |
| `IND_LED_PAIR(pinA, pinB, polarity)` | two LEDs | both off | A on | B on |

`polarity` is `ACTIVE_HIGH` when driving the pin HIGH lights the LED, or `ACTIVE_LOW` when driving it LOW does. Every indicator starts dark at boot. "Dark" is a different pin level for each polarity, so `initIndicator` sets it through the same code path as live feedback.

To describe different hardware, edit these two tables. The sizes of every array in `main.cpp` (`NUM_SLIDERS`, `NUM_BUTTONS`) are derived from them, so there is nothing else to keep in sync.

### Tuning

| Constant | Meaning |
|---|---|
| `DEBOUNCE_MS` | A button's raw reading must be stable this long before a change is reported. |
| `SLIDER_READ_INTERVAL_MS` | How often sliders are sampled. |
| `FILTER_WEIGHT` | Exponential moving average weight for slider readings (0–1). Lower is smoother but slower. |
| `SLIDER_DEADBAND` | A smoothed slider reading must move more than this many ADC units before it is reported. |
| `RECONNECT_INTERVAL_MS` | How often the connection is checked and, if needed, restarted. |

## `main.cpp`

### Transport abstraction

Each transport is a block guarded by its `TRANSPORT_*` define. Every block implements the same five functions, and nothing outside them knows which link is in use:

| Function | Purpose |
|---|---|
| `transport_init()` | Bring the link up (and the serial port for logging). |
| `transport_connected()` | Whether the link is currently usable. |
| `transport_send(data, len)` | Send bytes to the app. |
| `transport_receive(buf, maxLen)` | Non-blocking read of whatever bytes have arrived. Returns the count. |
| `transport_reconnect()` | Try to restore a lost link (a no-op where the link manages itself). |

Debug output uses `DEBUG_LOG`, which prints `[DBG] …` lines to `Serial`. On `TRANSPORT_SERIAL` these share the port with protocol frames. That is safe because the app's parser only acts on bytes that form a valid `0xAA` frame.

### Main loop

`loop()` never blocks and runs four steps on every pass:

1. **Sliders** (every `SLIDER_READ_INTERVAL_MS`): read each pin, smooth it with the moving average, and send a slider message when the smoothed value has moved more than `SLIDER_DEADBAND` since the last one sent.
2. **Buttons**: debounce each pin and send a button message on every confirmed press or release.
3. **Receive**: feed every byte from `transport_receive()` into the frame parser.
4. **Connection** (every `RECONNECT_INTERVAL_MS`): if `transport_connected()` is false, call `transport_reconnect()`.

### Wire protocol

Every message, in both directions, is a frame:

```
0xAA | length | payload (length bytes) | checksum
```

`checksum` is the XOR of the payload bytes. The first payload byte is the message type.

| Type | Direction | Payload after the type byte | Meaning |
|---|---|---|---|
| `0x01` | controller → PC | slider index, value high byte, value low byte | A slider moved (value 0–4095). |
| `0x02` | controller → PC | button index, state (1 = pressed, 0 = released) | A button changed. |
| `0x05` | PC → controller | button index, value | What the button's indicator shows: 0 = off/unknown, 1..N = on, or choice N. |

`sendFrame()` builds outgoing frames. Incoming bytes go through a small state machine in `processReceivedByte()` (`WAIT_START → WAIT_LENGTH → WAIT_PAYLOAD → WAIT_CHECKSUM`). It drops frames with a bad checksum or a length over 32 bytes, and hands valid payloads to `handleParsedMessage()`.

`handleParsedMessage()` acts on `0x05` by passing the value to `showIndicator()` for that button. It ignores feedback for a button index that isn't in `BUTTONS`, and ignores every other message type. The PC sends feedback for every button whenever something changes, and re-sends all of it after the connection comes up, so a rebooted controller catches up on its own.

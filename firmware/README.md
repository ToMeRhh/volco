# Volco ESP32 Controller Firmware

Firmware for the ESP32-based hardware controller that interfaces with the [Volco](../README.md) Windows app. Reads 5 analog sliders and 3 buttons, sends input over a binary protocol, and receives LED state updates from the server.

## Hardware

- ESP32 dev board
- 5 potentiometer sliders (analog)
- 2 mute buttons with single-color LEDs
- 1 device selector button with 2 single-color LEDs

See `src/config.h` for pin assignments and all configurable parameters.

## Building

Requires [PlatformIO](https://platformio.org/).

```bash
# Build
pio run

# Build and upload
pio run --target upload

# Serial monitor
pio device monitor
```

## Configuration

Edit `src/config.h` before building:

1. **Select transport** — uncomment exactly one of `TRANSPORT_SERIAL`, `TRANSPORT_WEBSOCKET`, or `TRANSPORT_UDP`
2. **WiFi credentials** — set `WIFI_SSID`, `WIFI_PASSWORD`, `SERVER_IP`, and `SERVER_PORT` (only needed for WiFi transports)
3. **Pin assignments** — adjust GPIO pins to match your wiring
4. **Tuning** — adjust deadband, debounce, and polling intervals if needed

## Transport Options

| Transport | Connection | Library |
|-----------|-----------|---------|
| Serial | USB cable | Built-in |
| WebSocket | WiFi | [WebSockets](https://github.com/Links2004/arduinoWebSockets) |
| UDP | WiFi | Built-in |

Switch transport by changing the `#define` in `config.h` and recompiling.

## Protocol

See the [main README](../README.md#wire-protocol) for the full wire protocol specification. The controller sends slider and button events, and receives mute state and active device updates from the server.

## Debug Logging

Debug messages are printed to serial (115200 baud) prefixed with `[DBG]`. When using serial transport, logs share the port with protocol data — the PC-side parser safely ignores ASCII text.

Monitor with: `pio device monitor`

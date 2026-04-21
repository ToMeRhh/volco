# ESP32 Controller Firmware Design

## Overview

Firmware for an ESP32-based hardware controller that interfaces with the Volco Windows app. The controller has 5 analog sliders and 3 buttons (2 mute + 1 device selector), each with LEDs. Communicates over serial USB, WebSocket, or UDP using a bidirectional binary protocol.

This project has two deliverables: server-side changes to enable bidirectional communication, and the ESP32 firmware itself.

## Hardware

- ESP32 devboard
- 5 analog sliders (potentiometers) connected to ADC pins
- 2 mute buttons, each with a single-color LED (on/off)
- 1 device selector button with 2 single-color LEDs (one per output device)

### Pin Assignments

```
Slider 0:              GPIO 33
Slider 1:              GPIO 35
Slider 2:              GPIO 32
Slider 3:              GPIO 36
Slider 4:              GPIO 34

Mute button 0:         GPIO 4
Mute button 0 LED:     GPIO 12
Mute button 1:         GPIO 14
Mute button 1 LED:     GPIO 21

Device selector button:       GPIO 5
Device selector LED (dev 0):  GPIO 18
Device selector LED (dev 1):  GPIO 19
```

## Protocol

### Framing (both directions)

All messages use the same frame format:

| Byte  | Field            |
|-------|------------------|
| 0     | Start marker `0xAA` |
| 1     | Payload length   |
| 2..N  | Payload          |
| N+1   | XOR checksum (XOR of all payload bytes) |

### Controller-to-server messages (existing)

**Slider** (payload: 4 bytes):

| Byte | Value |
|------|-------|
| 0    | `0x01` (type: slider) |
| 1    | Slider index (0-4) |
| 2    | Value high byte |
| 3    | Value low byte |

Value is 12-bit ADC (0-4095) sent as 16-bit big-endian.

**Button** (payload: 3 bytes):

| Byte | Value |
|------|-------|
| 0    | `0x02` (type: button) |
| 1    | Button index (0-2) |
| 2    | State: `0x01` = pressed, `0x00` = released |

Button press classification (single/double/long) is handled on the PC side.

### Server-to-controller messages (new)

**Mute state** (payload: 3 bytes):

| Byte | Value |
|------|-------|
| 0    | `0x03` (type: mute state) |
| 1    | Button index (0-1) |
| 2    | `0x01` = muted (LED on), `0x00` = unmuted (LED off) |

**Active device** (payload: 2 bytes):

| Byte | Value |
|------|-------|
| 0    | `0x04` (type: active device) |
| 1    | Device index: `0` or `1` |

Controller turns on the LED for the active device, turns off the other.

### When the server sends state

- On controller connect: push all current state (both mute states + active device)
- On mute state change: push the affected mute state
- On active device change: push the new active device index

## Firmware Architecture

### Project Structure

```
firmware/
├── platformio.ini
├── README.md
└── src/
    ├── config.h        # All user-configurable defines
    └── main.cpp        # All firmware logic
```

Single-file architecture with a config header. The firmware is ~400-500 lines, well within single-file comfort.

### Configuration (`config.h`)

All user-configurable values in one file:

- **Transport selection:** `#define TRANSPORT_SERIAL` / `TRANSPORT_WEBSOCKET` / `TRANSPORT_UDP` (uncomment one)
- **WiFi credentials:** SSID, password, server IP, server port (only used for WiFi transports)
- **Serial baud rate:** 115200
- **Pin assignments:** all GPIO pins as listed above
- **Tuning constants:**
  - `NUM_SLIDERS` = 5
  - `NUM_MUTE_BUTTONS` = 2
  - `DEBOUNCE_MS` = 30
  - `SLIDER_DEADBAND` = 20 (ADC units, ignore noise smaller than this)
  - `SLIDER_READ_INTERVAL_MS` = 5
  - `RECONNECT_INTERVAL_MS` = 3000

### Transport Abstraction

Compile-time transport switching via `#ifdef`. A small set of functions abstract the transport:

```
void transport_init()
bool transport_connected()
void transport_send(const uint8_t* data, size_t len)
int  transport_receive(uint8_t* buf, size_t maxLen)
void transport_reconnect()
```

**Serial:** Hardware UART over USB. Send/receive via `Serial`. Reconnect is a no-op (USB manages the connection).

**WebSocket:** Uses `links2004/WebSockets` library. Connects to the PC as a WebSocket client. Library handles auto-reconnect. Incoming data is buffered via callback for main loop consumption.

**UDP:** Uses built-in `WiFiUDP`. Sends datagrams to `SERVER_IP:SERVER_PORT`. Listens on a fixed local port for server-push messages. Server sends to the address it received packets from.

### Main Loop

`loop()` runs as fast as possible with no `delay()` calls. Four responsibilities per iteration:

**1. Read sliders** (gated by `SLIDER_READ_INTERVAL_MS` = 5ms):
- Read all 5 ADC pins via `analogRead()`
- Compare each against its last-sent value
- If delta exceeds `SLIDER_DEADBAND`, send a slider frame
- Worst-case latency: 5ms from physical movement to message sent

**2. Read buttons** (every iteration):
- Read all 3 digital button pins
- Debounce: on state change, record timestamp. Ignore further changes on that pin for `DEBOUNCE_MS` (30ms). After debounce window, if state differs from last sent, send a button frame.
- Raw pressed/released only — PC handles single/double/long classification

**3. Receive incoming messages** (every iteration):
- Read available bytes from transport (non-blocking)
- Feed into frame parser (same 0xAA framing + XOR checksum as outbound)
- On valid frame:
  - `0x03` (mute state): set mute button LED on/off
  - `0x04` (active device): turn on matching LED, turn off the other

**4. Connection management** (gated by `RECONNECT_INTERVAL_MS`):
- Serial: no-op
- WiFi transports: check WiFi status, reconnect if disconnected. For WebSocket, the library handles reconnection internally.

### LED Control

- Mute button LEDs: `digitalWrite(pin, HIGH)` when muted, `LOW` when unmuted
- Device selector LEDs: `digitalWrite(device_0_pin, active == 0 ? HIGH : LOW)`, same logic for device 1
- LEDs are only updated in response to server-push messages (no optimistic local state)
- On boot, all LEDs start off until the server sends initial state

### Debug Logging

Minimal `Serial.println()` debug output (when serial transport is not in use) or conditional logging via a `DEBUG_LOG` macro. Key events logged:
- Transport connect/disconnect
- WiFi connect/disconnect
- Reconnect attempts
- Received state messages (mute/device)

When using serial transport, debug logging goes over the same serial port — kept minimal to avoid interfering with protocol data. Each debug line is prefixed with `[DBG]` so it's distinguishable from binary protocol frames during development.

## Server-Side Changes

### 1. `IControllerConnection` — add send capability

Add to the interface:
```csharp
void Send(ReadOnlySpan<byte> data);
```

Implementations:
- `SerialConnection`: write to the serial port
- `UdpConnection`: send datagram to cached remote endpoint (recorded from last received packet)
- `WebSocketConnection`: send binary message to connected client

### 2. `ProtocolEncoder` — new static class

Builds outbound frames: `0xAA | length | payload | XOR checksum`.

Methods:
- `byte[] EncodeMuteState(int buttonIndex, bool isMuted)`
- `byte[] EncodeActiveDevice(int deviceIndex)`

### 3. `ControllerMessage.cs` — new message types

Add `MuteStateMessage` and `ActiveDeviceMessage` classes for protocol symmetry. Add `MessageType` enum values: `MuteState = 0x03`, `ActiveDevice = 0x04`.

### 4. State push triggers — in `App.xaml.cs`

- When a mute action executes: encode and send mute state
- When a device toggle/set action executes: encode and send active device index
- On `StatusChanged` to `Connected`: send full current state (all mute states + active device)

### 5. `SliderProcessor.cs` — ADC range update

Change `MapAdcToVolume` divisor from `1023f` to `4095f` to match 12-bit ADC.

### 6. `SerialConfig` — baud rate update

Change default `BaudRate` from `9600` to `115200`.

### 7. README — protocol documentation

Update the Wire Protocol section to document both directions and the new message types.

## Implementation Order

1. Server-side changes first (bidirectional protocol support)
2. ESP32 firmware second (test against working bidirectional server)

## Out of Scope

- Optimistic local LED state (controller flips LED immediately on button press before server confirms)
- WiFiManager / captive portal for WiFi credential entry
- FreeRTOS tasks / dual-core usage
- Double/long press detection on the controller
- OTA firmware updates
- RGB or addressable LED support

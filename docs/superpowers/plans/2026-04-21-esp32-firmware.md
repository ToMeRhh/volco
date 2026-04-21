# ESP32 Controller Firmware Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add bidirectional protocol support to the Volco server and build ESP32 controller firmware with slider/button input and LED state feedback.

**Architecture:** Server-side changes add `Send` to connections, a `ProtocolEncoder` for outbound frames, and state push logic. Firmware is a single `main.cpp` + `config.h` using compile-time transport switching (`#ifdef`) for serial/WebSocket/UDP.

**Tech Stack:** C# .NET 8 (server), C++17 Arduino framework on ESP32 via PlatformIO (firmware), `links2004/WebSockets` library (firmware WebSocket transport)

**Build/Test:** No local dotnet — push to branch and verify via GitHub Actions CI (`workflow_dispatch`). Firmware builds via PlatformIO locally.

---

## File Map

### Server-side (modify)

| File | Change |
|------|--------|
| `src/Volco/Communication/ControllerMessage.cs` | Add `MuteState = 0x03`, `ActiveDevice = 0x04` to `MessageType` enum; add `MuteStateMessage`, `ActiveDeviceMessage` classes |
| `src/Volco/Communication/IControllerConnection.cs` | Add `void Send(byte[] data)` |
| `src/Volco/Communication/SerialConnection.cs` | Implement `Send` |
| `src/Volco/Communication/UdpConnection.cs` | Implement `Send` with cached remote endpoint |
| `src/Volco/Communication/WebSocketConnection.cs` | Implement `Send` to active client |
| `src/Volco/Audio/IAudioManager.cs` | Add `bool IsOutputMuted(string?)`, `bool IsInputMuted()` |
| `src/Volco/Audio/AudioManager.cs` | Implement mute query methods |
| `src/Volco/Input/SliderProcessor.cs` | Change ADC divisor from `1023f` to `4095f` |
| `src/Volco/Config/AppConfig.cs` | Change `SerialConfig.BaudRate` default from `9600` to `115200` |
| `src/Volco/App.xaml.cs` | Add `PushControllerState` method, call on connect and after actions |
| `README.md` | Update Wire Protocol section for bidirectional protocol |

### Server-side (create)

| File | Purpose |
|------|---------|
| `src/Volco/Communication/ProtocolEncoder.cs` | Static class to build outbound frames |

### Server-side tests (modify)

| File | Change |
|------|--------|
| `tests/Volco.Tests/Communication/ProtocolParserTests.cs` | (no changes needed — parser doesn't parse outbound messages) |
| `tests/Volco.Tests/Input/SliderProcessorTests.cs` | Update ADC max from 1023 to 4095 |
| `tests/Volco.Tests/Input/Actions/ActionFactoryTests.cs` | Add `IsOutputMuted`, `IsInputMuted` to `FakeAudioManager` |

### Server-side tests (create)

| File | Purpose |
|------|---------|
| `tests/Volco.Tests/Communication/ProtocolEncoderTests.cs` | Tests for frame encoding |

### Firmware (create)

| File | Purpose |
|------|---------|
| `firmware/platformio.ini` | PlatformIO build config |
| `firmware/src/config.h` | All user-configurable defines |
| `firmware/src/main.cpp` | All firmware logic |
| `firmware/README.md` | Firmware documentation |

---

## Part 1: Server-Side Changes

### Task 1: Protocol message types and encoder

**Files:**
- Modify: `src/Volco/Communication/ControllerMessage.cs`
- Create: `src/Volco/Communication/ProtocolEncoder.cs`
- Create: `tests/Volco.Tests/Communication/ProtocolEncoderTests.cs`

- [ ] **Step 1: Add new message types to `ControllerMessage.cs`**

Add `MuteState` and `ActiveDevice` to the `MessageType` enum, and add two new message classes:

```csharp
// Add to MessageType enum:
    MuteState = 0x03,
    ActiveDevice = 0x04

// Add after ButtonMessage class:
public class MuteStateMessage : ControllerMessage
{
    public int ButtonIndex { get; }
    public bool IsMuted { get; }

    public MuteStateMessage(int buttonIndex, bool isMuted) : base(MessageType.MuteState)
    {
        ButtonIndex = buttonIndex;
        IsMuted = isMuted;
    }
}

public class ActiveDeviceMessage : ControllerMessage
{
    public int DeviceIndex { get; }

    public ActiveDeviceMessage(int deviceIndex) : base(MessageType.ActiveDevice)
    {
        DeviceIndex = deviceIndex;
    }
}
```

- [ ] **Step 2: Write failing tests for ProtocolEncoder**

Create `tests/Volco.Tests/Communication/ProtocolEncoderTests.cs`:

```csharp
using Volco.Communication;
using Xunit;

namespace Volco.Tests.Communication;

public class ProtocolEncoderTests
{
    [Fact]
    public void EncodeMuteState_MutedButton0_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeMuteState(0, isMuted: true);

        // Frame: 0xAA | 0x03 (length) | 0x03 0x00 0x01 (payload) | checksum
        Assert.Equal(0xAA, frame[0]);          // start marker
        Assert.Equal(3, frame[1]);             // payload length
        Assert.Equal(0x03, frame[2]);          // message type
        Assert.Equal(0x00, frame[3]);          // button index
        Assert.Equal(0x01, frame[4]);          // muted = true
        Assert.Equal((byte)(0x03 ^ 0x00 ^ 0x01), frame[5]); // XOR checksum
        Assert.Equal(6, frame.Length);         // total frame size
    }

    [Fact]
    public void EncodeMuteState_UnmutedButton1_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeMuteState(1, isMuted: false);

        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(3, frame[1]);
        Assert.Equal(0x03, frame[2]);
        Assert.Equal(0x01, frame[3]);
        Assert.Equal(0x00, frame[4]);
        Assert.Equal((byte)(0x03 ^ 0x01 ^ 0x00), frame[5]);
    }

    [Fact]
    public void EncodeActiveDevice_Device0_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeActiveDevice(0);

        // Frame: 0xAA | 0x02 (length) | 0x04 0x00 (payload) | checksum
        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(2, frame[1]);
        Assert.Equal(0x04, frame[2]);
        Assert.Equal(0x00, frame[3]);
        Assert.Equal((byte)(0x04 ^ 0x00), frame[4]);
        Assert.Equal(5, frame.Length);
    }

    [Fact]
    public void EncodeActiveDevice_Device1_CorrectFrame()
    {
        var frame = ProtocolEncoder.EncodeActiveDevice(1);

        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(2, frame[1]);
        Assert.Equal(0x04, frame[2]);
        Assert.Equal(0x01, frame[3]);
        Assert.Equal((byte)(0x04 ^ 0x01), frame[4]);
    }

    [Fact]
    public void EncodedFrames_ParseableByProtocolParser()
    {
        // Round-trip: encode a mute state, feed to parser, verify it finds a valid frame
        // (Parser won't recognize 0x03 as a known type, but it should not crash)
        var parser = new ProtocolParser();
        var received = new List<ControllerMessage>();
        parser.MessageParsed += (_, msg) => received.Add(msg);

        var frame = ProtocolEncoder.EncodeMuteState(0, true);
        parser.FeedBytes(frame);

        // Parser returns null for unknown types but doesn't crash — frame is valid
        Assert.Empty(received);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Push to branch and trigger CI, or note that `ProtocolEncoder` doesn't exist yet so tests won't compile.

- [ ] **Step 4: Create `ProtocolEncoder.cs`**

Create `src/Volco/Communication/ProtocolEncoder.cs`:

```csharp
namespace Volco.Communication;

public static class ProtocolEncoder
{
    private const byte StartMarker = 0xAA;

    public static byte[] EncodeMuteState(int buttonIndex, bool isMuted)
    {
        byte[] payload = { (byte)MessageType.MuteState, (byte)buttonIndex, (byte)(isMuted ? 1 : 0) };
        return BuildFrame(payload);
    }

    public static byte[] EncodeActiveDevice(int deviceIndex)
    {
        byte[] payload = { (byte)MessageType.ActiveDevice, (byte)deviceIndex };
        return BuildFrame(payload);
    }

    private static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[payload.Length + 3]; // marker + length + payload + checksum
        frame[0] = StartMarker;
        frame[1] = (byte)payload.Length;
        byte checksum = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            frame[2 + i] = payload[i];
            checksum ^= payload[i];
        }
        frame[^1] = checksum;
        return frame;
    }
}
```

- [ ] **Step 5: Verify tests pass via CI**

Push and trigger GitHub Actions workflow. Expected: all tests pass including the new `ProtocolEncoderTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Volco/Communication/ControllerMessage.cs \
        src/Volco/Communication/ProtocolEncoder.cs \
        tests/Volco.Tests/Communication/ProtocolEncoderTests.cs
git commit -m "feat: add bidirectional protocol types and encoder"
```

---

### Task 2: Add Send capability to connections

**Files:**
- Modify: `src/Volco/Communication/IControllerConnection.cs`
- Modify: `src/Volco/Communication/SerialConnection.cs`
- Modify: `src/Volco/Communication/UdpConnection.cs`
- Modify: `src/Volco/Communication/WebSocketConnection.cs`

- [ ] **Step 1: Add `Send` to `IControllerConnection`**

In `src/Volco/Communication/IControllerConnection.cs`, add to the interface:

```csharp
void Send(byte[] data);
```

- [ ] **Step 2: Implement `Send` in `SerialConnection`**

In `src/Volco/Communication/SerialConnection.cs`, add method:

```csharp
public void Send(byte[] data)
{
    if (_serialPort is { IsOpen: true })
        _serialPort.Write(data, 0, data.Length);
}
```

- [ ] **Step 3: Implement `Send` in `UdpConnection`**

In `src/Volco/Communication/UdpConnection.cs`:

Add a field to cache the remote endpoint:

```csharp
private IPEndPoint? _remoteEndPoint;
```

In `ReceiveLoopAsync`, after `var result = await _udpClient.ReceiveAsync(ct);`, add:

```csharp
_remoteEndPoint = result.RemoteEndPoint;
```

Add the `Send` method:

```csharp
public void Send(byte[] data)
{
    if (_udpClient != null && _remoteEndPoint != null)
        _udpClient.Send(data, data.Length, _remoteEndPoint);
}
```

In `Disconnect()`, add `_remoteEndPoint = null;` alongside the other field resets.

- [ ] **Step 4: Implement `Send` in `WebSocketConnection`**

In `src/Volco/Communication/WebSocketConnection.cs`:

Add a field to track the active client:

```csharp
private WebSocket? _activeClient;
```

In `HandleClientAsync`, after `SetStatus(ConnectionStatus.Connected)` (the `Interlocked.Increment` block), add:

```csharp
_activeClient = webSocket;
```

In the `finally` block of `HandleClientAsync`, after `SetStatus(ConnectionStatus.Disconnected)`, add:

```csharp
if (_activeClient == webSocket)
    _activeClient = null;
```

Add the `Send` method:

```csharp
public void Send(byte[] data)
{
    var ws = _activeClient;
    if (ws?.State == WebSocketState.Open)
        _ = ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
}
```

- [ ] **Step 5: Verify build passes via CI**

Push and trigger CI. Expected: build and tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Volco/Communication/IControllerConnection.cs \
        src/Volco/Communication/SerialConnection.cs \
        src/Volco/Communication/UdpConnection.cs \
        src/Volco/Communication/WebSocketConnection.cs
git commit -m "feat: add Send capability to all connection types"
```

---

### Task 3: Add mute and device query methods

**Files:**
- Modify: `src/Volco/Audio/IAudioManager.cs`
- Modify: `src/Volco/Audio/AudioManager.cs`
- Modify: `tests/Volco.Tests/Input/Actions/ActionFactoryTests.cs` (update `FakeAudioManager`)

- [ ] **Step 1: Add query methods to `IAudioManager`**

In `src/Volco/Audio/IAudioManager.cs`, add to the interface:

```csharp
bool IsOutputMuted(string? deviceId = null);
bool IsInputMuted();
```

- [ ] **Step 2: Implement in `AudioManager`**

In `src/Volco/Audio/AudioManager.cs`, add:

```csharp
public bool IsOutputMuted(string? deviceId = null)
{
    try
    {
        MMDevice? device;
        if (deviceId != null)
        {
            device = FindDeviceByName(DataFlow.Render, deviceId);
            if (device == null) return false;
        }
        else
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        return device.AudioEndpointVolume.Mute;
    }
    catch
    {
        return false;
    }
}

public bool IsInputMuted()
{
    try
    {
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        return device.AudioEndpointVolume.Mute;
    }
    catch
    {
        return false;
    }
}
```

- [ ] **Step 3: Update `FakeAudioManager` in tests**

In `tests/Volco.Tests/Input/Actions/ActionFactoryTests.cs`, add to the `FakeAudioManager` class:

```csharp
public bool IsOutputMuted(string? deviceId = null) => false;
public bool IsInputMuted() => false;
```

- [ ] **Step 4: Verify build passes via CI**

Push and trigger CI. Expected: build and all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Audio/IAudioManager.cs \
        src/Volco/Audio/AudioManager.cs \
        tests/Volco.Tests/Input/Actions/ActionFactoryTests.cs
git commit -m "feat: add mute state query methods to IAudioManager"
```

---

### Task 4: Wire up state push in App.xaml.cs

**Files:**
- Modify: `src/Volco/App.xaml.cs`

- [ ] **Step 1: Add `PushControllerState` and `GetMuteStateForButton` methods**

In `src/Volco/App.xaml.cs`, add these two methods to the `App` class:

```csharp
private void PushControllerState()
{
    if (_connection?.Status != ConnectionStatus.Connected) return;

    var config = _configManager.Config;

    // Push mute state for mute buttons (indices 0 and 1)
    for (int i = 0; i < Math.Min(2, config.Buttons.Count); i++)
    {
        var isMuted = GetMuteStateForButton(config.Buttons[i]);
        if (isMuted.HasValue)
            _connection.Send(ProtocolEncoder.EncodeMuteState(i, isMuted.Value));
    }

    // Push active device
    var currentDevice = _audioManager.GetDefaultOutputDeviceName();
    if (currentDevice != null)
    {
        var devices = config.OutputDevices;
        int activeIndex = currentDevice.Contains(devices.Device1, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        _connection.Send(ProtocolEncoder.EncodeActiveDevice(activeIndex));
    }
}

private bool? GetMuteStateForButton(ButtonConfig buttonConfig)
{
    // Scan all press types for the first mute-related action
    foreach (var actionConfig in new[] { buttonConfig.SinglePress, buttonConfig.DoublePress, buttonConfig.LongPress })
    {
        switch (actionConfig.Action)
        {
            case ButtonActionType.MuteCurrentOutput:
                return _audioManager.IsOutputMuted();
            case ButtonActionType.MuteSpecificDevice:
                return _audioManager.IsOutputMuted(actionConfig.DeviceId);
            case ButtonActionType.MuteCurrentInput:
                return _audioManager.IsInputMuted();
        }
    }
    return null;
}
```

Add the required `using` at the top of the file:

```csharp
using Volco.Communication;
```

(This using is already present — verify it covers `ProtocolEncoder`. It does since `ProtocolEncoder` is in `Volco.Communication` namespace.)

- [ ] **Step 2: Call `PushControllerState` on connection established**

In the `OnConnectionStatusChanged` method, add the state push call:

```csharp
private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
{
    AppLog.Info($"Connection status changed: {status}");
    if (status == ConnectionStatus.Connected)
        PushControllerState();
}
```

- [ ] **Step 3: Call `PushControllerState` after button actions execute**

In the `OnButtonPressDetected` method, add the call after `action?.Execute()`:

```csharp
private void OnButtonPressDetected(object? sender, ButtonPressEventArgs e)
{
    AppLog.Info($"Button press detected: Index={e.ButtonIndex}, Type={e.PressType}");
    var config = _configManager.Config;
    if (e.ButtonIndex < 0 || e.ButtonIndex >= config.Buttons.Count) return;

    var buttonConfig = config.Buttons[e.ButtonIndex];
    var actionConfig = e.PressType switch
    {
        PressType.Single => buttonConfig.SinglePress,
        PressType.Double => buttonConfig.DoublePress,
        PressType.Long => buttonConfig.LongPress,
        _ => null
    };

    if (actionConfig == null) return;
    var action = ActionFactory.Create(actionConfig, _audioManager, _configManager);
    action?.Execute();

    // Push updated state to controller after action changes audio state
    PushControllerState();
}
```

- [ ] **Step 4: Verify build passes via CI**

Push and trigger CI. Expected: build and all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/App.xaml.cs
git commit -m "feat: push mute/device state to controller on connect and action"
```

---

### Task 5: Update ADC range and baud rate

**Files:**
- Modify: `src/Volco/Input/SliderProcessor.cs:23`
- Modify: `src/Volco/Config/AppConfig.cs:25`
- Modify: `tests/Volco.Tests/Input/SliderProcessorTests.cs`

- [ ] **Step 1: Update slider processor tests for 12-bit ADC**

In `tests/Volco.Tests/Input/SliderProcessorTests.cs`, update the test methods:

Replace `MapAdcToVolume_MaxReturnsOne`:

```csharp
[Fact]
public void MapAdcToVolume_MaxReturnsOne()
{
    Assert.Equal(1f, SliderProcessor.MapAdcToVolume(4095, invert: false), 3);
}
```

Replace `MapAdcToVolume_MidReturnsHalf`:

```csharp
[Fact]
public void MapAdcToVolume_MidReturnsHalf()
{
    Assert.Equal(0.5f, SliderProcessor.MapAdcToVolume(2048, invert: false), 1);
}
```

- [ ] **Step 2: Update `MapAdcToVolume` divisor**

In `src/Volco/Input/SliderProcessor.cs`, line 24, change:

```csharp
float volume = adcValue / 4095f;
```

- [ ] **Step 3: Update default baud rate**

In `src/Volco/Config/AppConfig.cs`, line 25, change:

```csharp
public int BaudRate { get; set; } = 115200;
```

- [ ] **Step 4: Verify tests pass via CI**

Push and trigger CI. Expected: all tests pass including the updated slider processor tests.

- [ ] **Step 5: Commit**

```bash
git add src/Volco/Input/SliderProcessor.cs \
        src/Volco/Config/AppConfig.cs \
        tests/Volco.Tests/Input/SliderProcessorTests.cs
git commit -m "feat: update ADC range to 12-bit (4095) and baud rate to 115200"
```

---

### Task 6: Update README protocol documentation

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update the Wire Protocol section**

Replace the existing `### Wire Protocol` section in `README.md` with:

```markdown
### Wire Protocol

The controller and server exchange framed binary messages:

| Byte | Field |
|------|-------|
| 0 | Start marker `0xAA` |
| 1 | Payload length |
| 2..N | Payload |
| N+1 | XOR checksum (XOR of all payload bytes) |

#### Controller → Server

**Slider** (payload: 4 bytes): `0x01 | index | value_high | value_low`
- Index: 0–4
- Value: 12-bit ADC (0–4095) as 16-bit big-endian

**Button** (payload: 3 bytes): `0x02 | index | state`
- Index: 0–2
- State: `0x01` pressed, `0x00` released

#### Server → Controller

**Mute state** (payload: 3 bytes): `0x03 | button_index | mute_state`
- Button index: 0–1 (mute buttons)
- Mute state: `0x01` muted (LED on), `0x00` unmuted (LED off)

**Active device** (payload: 2 bytes): `0x04 | device_index`
- Device index: `0` or `1`

The server pushes state on controller connect (initial sync) and whenever mute or active device changes.
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: update README with bidirectional wire protocol"
```

---

## Part 2: ESP32 Firmware

### Task 7: Create firmware project structure

**Files:**
- Create: `firmware/platformio.ini`
- Create: `firmware/src/config.h`

- [ ] **Step 1: Create `firmware/platformio.ini`**

```ini
[env:denky32]
platform = espressif32
board = esp32dev
framework = arduino
monitor_speed = 115200
build_flags =
    -std=gnu++17
build_unflags =
    -std=gnu++11
lib_deps =
    links2004/WebSockets@^2.4.0
```

Note: `lib_deps` includes WebSockets for all builds. PlatformIO only links what's actually `#include`d, so this doesn't bloat non-WebSocket builds.

- [ ] **Step 2: Create `firmware/src/config.h`**

```cpp
#pragma once

// ============================================================
// Transport — uncomment exactly one
// ============================================================
#define TRANSPORT_SERIAL
// #define TRANSPORT_WEBSOCKET
// #define TRANSPORT_UDP

// ============================================================
// WiFi — only used for TRANSPORT_WEBSOCKET and TRANSPORT_UDP
// ============================================================
#define WIFI_SSID       "MyNetwork"
#define WIFI_PASSWORD   "MyPassword"
#define SERVER_IP        "192.168.1.100"
#define SERVER_PORT      8765   // WebSocket port or UDP port
#define UDP_LOCAL_PORT   12346  // Local port for receiving UDP from server

// ============================================================
// Serial
// ============================================================
#define SERIAL_BAUD 115200

// ============================================================
// Pin assignments
// ============================================================

// Analog sliders
#define SLIDER_0_PIN 33
#define SLIDER_1_PIN 35
#define SLIDER_2_PIN 32
#define SLIDER_3_PIN 36
#define SLIDER_4_PIN 34

// Mute buttons + LEDs
#define MUTE_BTN_0_PIN      4
#define MUTE_BTN_0_LED_PIN  12
#define MUTE_BTN_1_PIN      14
#define MUTE_BTN_1_LED_PIN  21

// Device selector button + LEDs
#define DEVICE_SEL_BTN_PIN      5
#define DEVICE_SEL_LED_0_PIN    18
#define DEVICE_SEL_LED_1_PIN    19

// ============================================================
// Tuning
// ============================================================
#define NUM_SLIDERS             5
#define NUM_MUTE_BUTTONS        2
#define DEBOUNCE_MS             30
#define SLIDER_DEADBAND         20   // ADC units — ignore changes smaller than this
#define SLIDER_READ_INTERVAL_MS 5    // How often to sample sliders (ms)
#define RECONNECT_INTERVAL_MS   3000 // How often to check connection (ms)
```

- [ ] **Step 3: Commit**

```bash
git add firmware/platformio.ini firmware/src/config.h
git commit -m "feat: add firmware project structure with PlatformIO config"
```

---

### Task 8: Write firmware main.cpp

**Files:**
- Create: `firmware/src/main.cpp`

- [ ] **Step 1: Create the complete `firmware/src/main.cpp`**

```cpp
#include <Arduino.h>
#include "config.h"

// ============================================================
// Conditional includes for transport
// ============================================================
#if defined(TRANSPORT_WEBSOCKET)
  #include <WiFi.h>
  #include <WebSocketsClient.h>
#elif defined(TRANSPORT_UDP)
  #include <WiFi.h>
  #include <WiFiUdp.h>
#endif

// ============================================================
// Debug logging — always enabled, shares serial port when
// using TRANSPORT_SERIAL. Protocol parser on PC skips non-0xAA
// bytes so ASCII log lines are harmlessly discarded.
// ============================================================
#define DEBUG_LOG(msg) Serial.println(msg)

// ============================================================
// Protocol constants
// ============================================================
static const uint8_t START_MARKER     = 0xAA;
static const uint8_t MSG_SLIDER       = 0x01;
static const uint8_t MSG_BUTTON       = 0x02;
static const uint8_t MSG_MUTE_STATE   = 0x03;
static const uint8_t MSG_ACTIVE_DEVICE = 0x04;

// ============================================================
// Pin tables
// ============================================================
static const uint8_t sliderPins[NUM_SLIDERS] = {
    SLIDER_0_PIN, SLIDER_1_PIN, SLIDER_2_PIN, SLIDER_3_PIN, SLIDER_4_PIN
};

static const uint8_t buttonPins[] = {
    MUTE_BTN_0_PIN, MUTE_BTN_1_PIN, DEVICE_SEL_BTN_PIN
};
static const uint8_t NUM_BUTTONS = sizeof(buttonPins) / sizeof(buttonPins[0]);

static const uint8_t muteLedPins[NUM_MUTE_BUTTONS] = {
    MUTE_BTN_0_LED_PIN, MUTE_BTN_1_LED_PIN
};

// ============================================================
// State
// ============================================================

// Sliders
static uint16_t lastSliderValues[NUM_SLIDERS];
static unsigned long lastSliderReadTime = 0;

// Buttons (debounce)
static bool buttonRawStates[3];       // last raw reading per button
static bool buttonReportedStates[3];  // last state sent to server
static unsigned long buttonChangeTime[3]; // when raw state last changed

// Connection
static unsigned long lastReconnectTime = 0;

// ============================================================
// Receive parser state machine
// ============================================================
enum RxState { WAIT_START, WAIT_LENGTH, WAIT_PAYLOAD, WAIT_CHECKSUM };
static RxState rxState = WAIT_START;
static uint8_t rxPayload[32];
static uint8_t rxExpectedLen = 0;
static uint8_t rxPayloadIdx = 0;

// ============================================================
// Transport abstraction
// ============================================================

#if defined(TRANSPORT_SERIAL)

void transport_init() {
    Serial.begin(SERIAL_BAUD);
}

bool transport_connected() {
    return (bool)Serial;
}

void transport_send(const uint8_t* data, size_t len) {
    Serial.write(data, len);
}

int transport_receive(uint8_t* buf, size_t maxLen) {
    int available = Serial.available();
    if (available <= 0) return 0;
    size_t toRead = min((size_t)available, maxLen);
    return Serial.readBytes(buf, toRead);
}

void transport_reconnect() {
    // No-op — USB manages the serial connection
}

#elif defined(TRANSPORT_WEBSOCKET)

static WebSocketsClient wsClient;
static uint8_t wsBuf[256];
static volatile size_t wsBufLen = 0;

static void wsEvent(WStype_t type, uint8_t* payload, size_t length) {
    switch (type) {
        case WStype_CONNECTED:
            DEBUG_LOG("[DBG] WebSocket connected");
            break;
        case WStype_DISCONNECTED:
            DEBUG_LOG("[DBG] WebSocket disconnected");
            break;
        case WStype_BIN:
            if (wsBufLen + length <= sizeof(wsBuf)) {
                memcpy(wsBuf + wsBufLen, payload, length);
                wsBufLen += length;
            }
            break;
        default:
            break;
    }
}

void transport_init() {
    Serial.begin(SERIAL_BAUD); // for debug logging
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
    DEBUG_LOG("[DBG] Connecting to WiFi...");
    while (WiFi.status() != WL_CONNECTED) {
        delay(100);
    }
    DEBUG_LOG("[DBG] WiFi connected: " + WiFi.localIP().toString());
    wsClient.begin(SERVER_IP, SERVER_PORT, "/");
    wsClient.onEvent(wsEvent);
    wsClient.setReconnectInterval(RECONNECT_INTERVAL_MS);
}

bool transport_connected() {
    return wsClient.isConnected();
}

void transport_send(const uint8_t* data, size_t len) {
    wsClient.sendBIN(data, len);
}

int transport_receive(uint8_t* buf, size_t maxLen) {
    wsClient.loop(); // drive the WebSocket event pump
    if (wsBufLen == 0) return 0;
    size_t toRead = min(wsBufLen, maxLen);
    memcpy(buf, wsBuf, toRead);
    if (toRead < wsBufLen) {
        memmove(wsBuf, wsBuf + toRead, wsBufLen - toRead);
    }
    wsBufLen -= toRead;
    return (int)toRead;
}

void transport_reconnect() {
    // WebSocketsClient handles reconnection internally
}

#elif defined(TRANSPORT_UDP)

static WiFiUDP udpClient;

void transport_init() {
    Serial.begin(SERIAL_BAUD); // for debug logging
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
    DEBUG_LOG("[DBG] Connecting to WiFi...");
    while (WiFi.status() != WL_CONNECTED) {
        delay(100);
    }
    DEBUG_LOG("[DBG] WiFi connected: " + WiFi.localIP().toString());
    udpClient.begin(UDP_LOCAL_PORT);
    DEBUG_LOG("[DBG] UDP listening on port " + String(UDP_LOCAL_PORT));
}

bool transport_connected() {
    return WiFi.status() == WL_CONNECTED;
}

void transport_send(const uint8_t* data, size_t len) {
    udpClient.beginPacket(SERVER_IP, SERVER_PORT);
    udpClient.write(data, len);
    udpClient.endPacket();
}

int transport_receive(uint8_t* buf, size_t maxLen) {
    int packetSize = udpClient.parsePacket();
    if (packetSize <= 0) return 0;
    return udpClient.read(buf, min((size_t)packetSize, maxLen));
}

void transport_reconnect() {
    if (WiFi.status() != WL_CONNECTED) {
        DEBUG_LOG("[DBG] WiFi reconnecting...");
        WiFi.reconnect();
    }
}

#else
  #error "Define exactly one of TRANSPORT_SERIAL, TRANSPORT_WEBSOCKET, or TRANSPORT_UDP in config.h"
#endif

// ============================================================
// Protocol: send framed messages
// ============================================================

static void sendFrame(const uint8_t* payload, uint8_t payloadLen) {
    uint8_t frame[payloadLen + 3]; // marker + length + payload + checksum
    frame[0] = START_MARKER;
    frame[1] = payloadLen;
    uint8_t checksum = 0;
    for (uint8_t i = 0; i < payloadLen; i++) {
        frame[2 + i] = payload[i];
        checksum ^= payload[i];
    }
    frame[payloadLen + 2] = checksum;
    transport_send(frame, payloadLen + 3);
}

static void sendSlider(uint8_t index, uint16_t value) {
    uint8_t payload[] = { MSG_SLIDER, index, (uint8_t)(value >> 8), (uint8_t)(value & 0xFF) };
    sendFrame(payload, sizeof(payload));
}

static void sendButton(uint8_t index, uint8_t state) {
    uint8_t payload[] = { MSG_BUTTON, index, state };
    sendFrame(payload, sizeof(payload));
}

// ============================================================
// Protocol: receive and parse incoming frames
// ============================================================

static void handleParsedMessage(const uint8_t* payload, uint8_t len) {
    if (len < 2) return;
    uint8_t type = payload[0];

    if (type == MSG_MUTE_STATE && len >= 3) {
        uint8_t btnIndex = payload[1];
        bool muted = payload[2] != 0;
        if (btnIndex < NUM_MUTE_BUTTONS) {
            digitalWrite(muteLedPins[btnIndex], muted ? HIGH : LOW);
            DEBUG_LOG("[DBG] Mute btn " + String(btnIndex) + " = " + (muted ? "ON" : "OFF"));
        }
    } else if (type == MSG_ACTIVE_DEVICE && len >= 2) {
        uint8_t deviceIndex = payload[1];
        digitalWrite(DEVICE_SEL_LED_0_PIN, deviceIndex == 0 ? HIGH : LOW);
        digitalWrite(DEVICE_SEL_LED_1_PIN, deviceIndex == 1 ? HIGH : LOW);
        DEBUG_LOG("[DBG] Active device = " + String(deviceIndex));
    }
}

static void processReceivedByte(uint8_t b) {
    switch (rxState) {
        case WAIT_START:
            if (b == START_MARKER) {
                rxState = WAIT_LENGTH;
            }
            break;

        case WAIT_LENGTH:
            rxExpectedLen = b;
            rxPayloadIdx = 0;
            if (rxExpectedLen == 0 || rxExpectedLen > sizeof(rxPayload)) {
                rxState = WAIT_START; // invalid length, reset
            } else {
                rxState = WAIT_PAYLOAD;
            }
            break;

        case WAIT_PAYLOAD:
            rxPayload[rxPayloadIdx++] = b;
            if (rxPayloadIdx >= rxExpectedLen) {
                rxState = WAIT_CHECKSUM;
            }
            break;

        case WAIT_CHECKSUM: {
            uint8_t checksum = 0;
            for (uint8_t i = 0; i < rxExpectedLen; i++) {
                checksum ^= rxPayload[i];
            }
            if (checksum == b) {
                handleParsedMessage(rxPayload, rxExpectedLen);
            } else {
                DEBUG_LOG("[DBG] Bad checksum");
            }
            rxState = WAIT_START;
            break;
        }
    }
}

// ============================================================
// Setup
// ============================================================

void setup() {
    // Slider pins — input (ADC)
    for (int i = 0; i < NUM_SLIDERS; i++) {
        pinMode(sliderPins[i], INPUT);
    }

    // Button pins — input with pull-up (LOW = pressed)
    for (uint8_t i = 0; i < NUM_BUTTONS; i++) {
        pinMode(buttonPins[i], INPUT_PULLUP);
        buttonRawStates[i] = HIGH;
        buttonReportedStates[i] = HIGH;
        buttonChangeTime[i] = 0;
    }

    // LED pins — output, start off
    for (int i = 0; i < NUM_MUTE_BUTTONS; i++) {
        pinMode(muteLedPins[i], OUTPUT);
        digitalWrite(muteLedPins[i], LOW);
    }
    pinMode(DEVICE_SEL_LED_0_PIN, OUTPUT);
    pinMode(DEVICE_SEL_LED_1_PIN, OUTPUT);
    digitalWrite(DEVICE_SEL_LED_0_PIN, LOW);
    digitalWrite(DEVICE_SEL_LED_1_PIN, LOW);

    // 12-bit ADC resolution
    analogReadResolution(12);

    // Seed slider values so we don't send spurious changes on startup
    for (int i = 0; i < NUM_SLIDERS; i++) {
        lastSliderValues[i] = analogRead(sliderPins[i]);
    }

    transport_init();
    DEBUG_LOG("[DBG] Setup complete");
}

// ============================================================
// Main loop
// ============================================================

void loop() {
    unsigned long now = millis();

    // --- 1. Read sliders (gated by interval) ---
    if (now - lastSliderReadTime >= SLIDER_READ_INTERVAL_MS) {
        lastSliderReadTime = now;
        for (int i = 0; i < NUM_SLIDERS; i++) {
            uint16_t value = analogRead(sliderPins[i]);
            int16_t delta = (int16_t)value - (int16_t)lastSliderValues[i];
            if (abs(delta) > SLIDER_DEADBAND) {
                lastSliderValues[i] = value;
                sendSlider(i, value);
            }
        }
    }

    // --- 2. Read buttons (with debounce) ---
    for (uint8_t i = 0; i < NUM_BUTTONS; i++) {
        bool currentRaw = digitalRead(buttonPins[i]);

        // Track when the raw state last changed
        if (currentRaw != buttonRawStates[i]) {
            buttonRawStates[i] = currentRaw;
            buttonChangeTime[i] = now;
        }

        // After stable for DEBOUNCE_MS, report if different from last reported
        if ((now - buttonChangeTime[i] >= DEBOUNCE_MS) && (buttonRawStates[i] != buttonReportedStates[i])) {
            buttonReportedStates[i] = buttonRawStates[i];
            // INPUT_PULLUP: LOW = pressed, HIGH = released
            sendButton(i, buttonReportedStates[i] == LOW ? 0x01 : 0x00);
        }
    }

    // --- 3. Receive incoming messages ---
    uint8_t buf[64];
    int bytesRead = transport_receive(buf, sizeof(buf));
    for (int i = 0; i < bytesRead; i++) {
        processReceivedByte(buf[i]);
    }

    // --- 4. Connection management ---
    if (now - lastReconnectTime >= RECONNECT_INTERVAL_MS) {
        lastReconnectTime = now;
        if (!transport_connected()) {
            DEBUG_LOG("[DBG] Reconnecting...");
            transport_reconnect();
        }
    }
}
```

- [ ] **Step 2: Build with PlatformIO to verify compilation**

```bash
cd firmware && pio run
```

Expected: successful build for `env:denky32`.

- [ ] **Step 3: Commit**

```bash
git add firmware/src/main.cpp
git commit -m "feat: add ESP32 controller firmware"
```

---

### Task 9: Write firmware README

**Files:**
- Create: `firmware/README.md`

- [ ] **Step 1: Create `firmware/README.md`**

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add firmware/README.md
git commit -m "docs: add firmware README"
```

---

## Verification

After all tasks are complete:

1. Push the branch and verify CI passes (server-side build + tests)
2. Build firmware locally with `cd firmware && pio run`
3. Open a PR against `main`

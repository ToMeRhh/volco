# Volco — Windows Volume Controller

A Windows system tray application that receives input from an external hardware controller (5 sliders, 3 buttons) and controls per-process and device-level audio volume, mute, and output device switching.

The hardware controller is out of scope — this spec defines the PC application and the wire protocol between them.

## Technology

- **C# / WinUI 3** (Windows App SDK)
- **Target:** Windows 10/11

## Wire Protocol

The controller sends binary messages over serial or UDP. Both transports use the same framed protocol.

### Message Payload

**Slider message (4 bytes):**

| Byte | Field | Description |
|------|-------|-------------|
| 0 | Type | `0x01` |
| 1 | ID | Slider index (0–4) |
| 2–3 | Value | 16-bit big-endian ADC value (0–1023) |

**Button message (3 bytes):**

| Byte | Field | Description |
|------|-------|-------------|
| 0 | Type | `0x02` |
| 1 | ID | Button index (0–2) |
| 2 | State | `0x01` = pressed, `0x00` = released |

### Framing

Each message is wrapped for reliable parsing:

| Byte | Field |
|------|-------|
| 0 | Start marker `0xAA` |
| 1 | Length (payload byte count) |
| 2..N | Payload |
| N+1 | Checksum (XOR of all payload bytes) |

## Communication Layer

### Interface: `IControllerConnection`

- `Connect(config)` — open the connection
- `Disconnect()` — close it
- `OnMessageReceived` — event fired with parsed controller messages

### Implementations

- **`SerialConnection`** — opens a COM port at a configured baud rate, reads byte stream, feeds into parser.
- **`UdpConnection`** — listens on a configured UDP port, receives datagrams, feeds into parser.

### Shared: `ProtocolParser`

Both implementations feed raw bytes into a shared `ProtocolParser` that handles:
- Start marker detection
- Length-delimited reads
- Checksum validation
- Deserialization into `ControllerMessage` objects

Connection errors (port unavailable, disconnects) surface as events so the UI can show connection status.

The app instantiates the correct implementation based on config and swaps it if the user changes the connection method.

## Button Press Detection

A `ButtonPressDetector` receives raw button pressed/released events and classifies them.

### Logic

- On button **release**: start a 300ms timer.
- If the same button is **pressed and released again** within that window: double press. Cancel the timer.
- If the timer expires with no second press: single press.
- If a button is **held down** for 500ms without release: long press, fired immediately. The subsequent release is ignored.

### Output

Classified events: `(buttonIndex, pressType)` where pressType is `Single`, `Double`, or `Long`.

Each button has independent detection state.

## Audio Control

Uses the Windows Core Audio API via NuGet packages.

### Capabilities

- **Set process volume** — find audio sessions by process name, set volume (0.0–1.0)
- **Set device volume** — set master volume of the default output device
- **Mute/unmute output device** — toggle mute on a specific or default output device
- **Mute/unmute input device** — toggle mute on the default input (microphone) device
- **Switch default output device** — change the Windows default playback device

### Slider Handling

1. Raw ADC value (0–1023) mapped to 0.0–1.0 float.
2. If "invert" is enabled, value is flipped: `1.0 - mapped`.
3. Based on slider mapping config:
   - **Process list**: set volume on all active audio sessions matching configured process names.
   - **Output device**: set master volume of the default output device.
   - **All non-mapped**: set volume on all active audio sessions whose process names don't appear in any other slider's process mapping.

### Session Discovery

The app periodically enumerates active audio sessions to track which processes are producing audio. Required for the "all non-mapped" slider mapping.

## Action System

Maps classified button events to actions. Each button has 3 configurable slots: single press, double press, long press.

### Available Actions

| Action | Parameter | Description |
|--------|-----------|-------------|
| `MuteSpecificDevice` | `deviceId` | Toggle mute on a named device |
| `MuteCurrentOutput` | — | Toggle mute on the current default output device |
| `MuteCurrentInput` | — | Toggle mute on the current default input device |
| `ToggleOutputDevice` | — | Switch default output between the two configured devices |
| `SetOutputDevice` | `deviceId` | Set default output to a specific device |
| `None` | — | No action |

Each action implements an `IButtonAction` interface with an `Execute()` method. The action system looks up the configured action for the `(buttonIndex, pressType)` pair and calls `Execute()`.

## Configuration

A single JSON file stored at `%APPDATA%/Volco/config.json`. Loaded on startup, saved whenever settings change in the UI.

A `ConfigManager` class handles read/write and exposes a change event so other components react to config updates.

### Schema

```json
{
  "connection": {
    "type": "serial",
    "serial": {
      "comPort": "COM3",
      "baudRate": 9600
    },
    "udp": {
      "listenPort": 12345
    }
  },
  "sliders": [
    { "index": 0, "type": "processes", "processes": ["spotify.exe", "chrome.exe"], "invert": false },
    { "index": 1, "type": "device", "invert": false },
    { "index": 2, "type": "unmapped", "invert": true },
    { "index": 3, "type": "processes", "processes": ["discord.exe"], "invert": false },
    { "index": 4, "type": "device", "invert": false }
  ],
  "buttons": [
    {
      "index": 0,
      "singlePress": { "action": "MuteCurrentOutput" },
      "doublePress": { "action": "ToggleOutputDevice" },
      "longPress": { "action": "None" }
    },
    {
      "index": 1,
      "singlePress": { "action": "MuteCurrentInput" },
      "doublePress": { "action": "None" },
      "longPress": { "action": "SetOutputDevice", "deviceId": "Speakers" }
    },
    {
      "index": 2,
      "singlePress": { "action": "MuteSpecificDevice", "deviceId": "Headphones" },
      "doublePress": { "action": "None" },
      "longPress": { "action": "None" }
    }
  ],
  "outputDevices": {
    "device1": "Speakers",
    "device2": "Headphones"
  }
}
```

## UI

WinUI 3 application that lives in the system tray. Clicking the tray icon opens the configuration window. Right-click menu: "Open Settings" / "Exit".

### Configuration Window — Tabbed Layout

**Connection tab:**
- Radio buttons: Serial / UDP
- Serial: COM port dropdown (auto-detected), baud rate dropdown
- UDP: listen port number input
- Connection status indicator

**Sliders tab:**
- 5 rows, one per slider
- Each row: slider index label, mapping type dropdown (Processes / Output Device / All Non-Mapped), process names text field (comma-separated, shown when type is Processes), invert checkbox

**Buttons tab:**
- 3 sections, one per button
- Each section: 3 action dropdowns (single / double / long press), with device picker shown when the selected action requires a device parameter

**Output Devices tab:**
- Two device dropdowns (Device 1 / Device 2) populated from Windows audio devices

### Behavior

- Settings apply immediately when changed (no save button)
- Window close minimizes to tray, does not exit the app
- App starts minimized to system tray

## Project Structure

```
Volco/
├── App.xaml / App.xaml.cs          # App entry point, tray icon setup
├── Config/
│   ├── AppConfig.cs                # Config data model (serializable to JSON)
│   └── ConfigManager.cs            # Load/save/change notification
├── Communication/
│   ├── IControllerConnection.cs    # Connection interface
│   ├── SerialConnection.cs         # Serial implementation
│   ├── UdpConnection.cs            # UDP implementation
│   ├── ProtocolParser.cs           # Framing, checksum, deserialization
│   └── ControllerMessage.cs        # Message data types
├── Audio/
│   ├── AudioManager.cs             # Volume/mute/device switching operations
│   └── SessionMonitor.cs           # Periodic audio session enumeration
├── Input/
│   ├── ButtonPressDetector.cs      # Single/double/long press classification
│   ├── SliderProcessor.cs          # ADC mapping, invert, dispatch to AudioManager
│   └── Actions/
│       ├── IButtonAction.cs        # Action interface
│       ├── MuteSpecificDevice.cs
│       ├── MuteCurrentOutput.cs
│       ├── MuteCurrentInput.cs
│       ├── ToggleOutputDevice.cs
│       └── SetOutputDevice.cs
├── UI/
│   ├── SettingsWindow.xaml/.cs     # Main settings window
│   ├── ConnectionTab.xaml/.cs
│   ├── SlidersTab.xaml/.cs
│   ├── ButtonsTab.xaml/.cs
│   └── OutputDevicesTab.xaml/.cs
└── volco-config.json               # Default config (actual stored in %APPDATA%)
```

### Key Dependencies (NuGet)

- `NAudio` — Windows Core Audio API access
- `AudioSwitcher.AudioApi.CoreAudio` — default device switching
- `H.NotifyIcon.WinUI` — system tray icon for WinUI 3
- `System.IO.Ports` — serial port communication

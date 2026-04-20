# Volco

A Windows system tray application that controls PC audio via an external hardware controller with 5 sliders and 3 buttons. Connect over USB serial or UDP wireless — the app handles per-process volume, device volume, mute, and output device switching.

## Features

- **Per-process volume control** — assign sliders to specific processes (e.g. `spotify.exe`, `discord.exe`)
- **Device volume control** — control the master volume of the default output device
- **"All non-mapped" slider** — catch-all that controls every process not assigned to another slider
- **Button actions** — configure single press, double press, and long press actions per button
- **Output device switching** — toggle the Windows default output between two configured devices
- **Mute controls** — mute/unmute specific devices, current output, or microphone input
- **Dual connectivity** — serial USB or UDP wireless, switchable in settings
- **System tray** — runs minimized, opens a settings UI on click

## Architecture

```
Controller (hardware)
    │
    │  Binary protocol over Serial/UDP
    │  (0xAA framing, length, payload, XOR checksum)
    │
    ▼
┌──────────────────────────────────────────┐
│  Communication Layer                     │
│  SerialConnection / UdpConnection        │
│  └─ ProtocolParser (framing + checksum)  │
├──────────────────────────────────────────┤
│  Input Processing                        │
│  ├─ SliderProcessor (ADC → volume)       │
│  └─ ButtonPressDetector                  │
│     └─ ActionFactory → IButtonAction     │
├──────────────────────────────────────────┤
│  Audio Layer                             │
│  ├─ AudioManager (NAudio + COM interop)  │
│  └─ SessionMonitor (process enumeration) │
├──────────────────────────────────────────┤
│  Config (JSON) + WinUI 3 Settings UI     │
└──────────────────────────────────────────┘
```

### Wire Protocol

The controller sends framed binary messages:

| Byte | Field |
|------|-------|
| 0 | Start marker `0xAA` |
| 1 | Payload length |
| 2..N | Payload (slider: type + index + 16-bit value, button: type + index + state) |
| N+1 | XOR checksum |

### Button Press Detection

Raw pressed/released events are classified on the PC side:
- **Single press** — release, then 300ms timeout with no second press
- **Double press** — two press-release cycles within 300ms
- **Long press** — held for 500ms, fires immediately

### Configurable Button Actions

| Action | Description |
|--------|-------------|
| Mute Current Output | Toggle mute on default output device |
| Mute Current Input | Toggle mute on default input (mic) |
| Mute Specific Device | Toggle mute on a named device |
| Toggle Output Device | Switch between two configured output devices |
| Set Output Device | Set a specific device as default output |

## Configuration

Settings are stored in `%APPDATA%/Volco/config.json` and applied immediately when changed through the UI. The config includes connection settings, slider mappings, button actions, and output device toggle pair.

## Tech Stack

- **C# / .NET 8 / WinUI 3** (Windows App SDK)
- **NAudio** — Windows Core Audio API for volume and session control
- **COM IPolicyConfig** — default audio device switching
- **H.NotifyIcon.WinUI** — system tray icon
- **System.IO.Ports** — serial communication
- **xUnit** — tests

## Building

Requires Windows with .NET 8 SDK and MSBuild (Visual Studio Build Tools).

```bash
msbuild Volco.sln -p:Configuration=Release -p:Platform=x64 -restore
```

Or use the GitHub Actions CI workflow which builds, tests, and produces a downloadable artifact on every push to `main`.

## Project Structure

```
src/Volco/
├── App.xaml/.cs              # Entry point, tray icon, component wiring
├── Config/                   # AppConfig data model, ConfigManager (JSON load/save)
├── Communication/            # IControllerConnection, Serial/UDP, ProtocolParser
├── Audio/                    # AudioManager (NAudio), SessionMonitor
├── Input/                    # ButtonPressDetector, SliderProcessor
│   └── Actions/              # IButtonAction + 5 action implementations + factory
└── UI/                       # SettingsWindow + 4 tab pages (Connection, Sliders, Buttons, Output Devices)

tests/Volco.Tests/            # xUnit tests for parser, button detection, slider mapping, config, actions
```

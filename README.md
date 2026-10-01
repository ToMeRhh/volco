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
│  AudioEngine (event-driven Core Audio)   │
│  ├─ DeviceRegistry (device state/events) │
│  └─ SessionRegistry (session events)     │
├──────────────────────────────────────────┤
│  Config (JSON) + Avalonia Settings UI    │
└──────────────────────────────────────────┘
```

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

### Button Press Detection

Raw pressed/released events are classified on the PC side:
- **Single press** — release, then 300ms timeout with no second press
- **Double press** — two press-release cycles within 300ms
- **Long press** — held for 500ms, fires immediately

#### Server → Controller

**Mute state** (payload: 3 bytes): `0x03 | button_index | mute_state`
- Button index: 0–1 (mute buttons)
- Mute state: `0x01` muted (LED on), `0x00` unmuted (LED off)

**Active device** (payload: 2 bytes): `0x04 | device_index`
- Device index: `0` or `1`

The server pushes state on controller connect (initial sync) and whenever mute or active device changes.

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

- **C# / .NET 8 / Avalonia 12** — cross-platform UI; only the audio engine and a click-through shim are Windows-specific
- **Windows Core Audio API (WASAPI)** — device and per-process session volume/mute, driven directly via COM interop with no polling
- **COM `IPolicyConfig`** — default audio device switching
- **Avalonia `TrayIcon`** — system tray icon
- **System.IO.Ports** — serial communication
- **xUnit** — tests

## Building

Requires the .NET 10 SDK (pinned in `global.json`; projects still target net8.0) and, for the Windows exe, Windows.

```bash
dotnet publish src/Volco.Windows/Volco.Windows.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish/
```

The tests build and run on any OS: `dotnet test tests/Volco.Core.Tests` and `dotnet test tests/Volco.App.Tests`.

Or use the GitHub Actions CI workflow which builds, tests, and produces a downloadable artifact on every push to `main`.

## Releases & In-App Updates

Release builds are stamped with a version and a `release` channel; local/dev builds use the
`local` channel. Only release builds expose the **Check for Updates…** item in the tray menu.

**Cutting a release:** push a semver tag and the `release.yml` workflow builds a stamped
single-file `Volco.exe` and publishes a GitHub Release with it attached:

```bash
git tag v1.2.3
git push origin v1.2.3
```

**Updating:** right-click the tray icon → **Check for Updates…**. The app compares the running
version against the latest GitHub Release and, if newer, downloads `Volco.exe` and relaunches into
it. A small helper script performs the in-place swap once the app exits.

The dialog also offers an advanced option to update from the **latest CI build artifact** (the
`Volco-win-x64` artifact of the most recent successful `main` build) instead of a published
release.

**Authentication:** Volco is a private repository, so the updater requires a GitHub Personal
Access Token to query the API and download binaries (for both the release and CI-artifact paths).
The token needs `repo` (read) scope, plus `actions:read` for the CI-artifact option. It is entered
in the update dialog and stored in your local `config.json`.

## Project Structure

```
src/Volco.Core/              # Platform-neutral (net8.0, builds/tests on Linux)
├── Config/                   # AppConfig data model, ConfigManager (JSON load/save)
├── Communication/            # IControllerConnection, Serial/UDP, ProtocolParser
├── Audio/                    # IAudioEngine + DTOs (interface only, no implementation)
├── Input/                    # ButtonPressDetector, SliderProcessor
│   └── Actions/              # IButtonAction + 5 action implementations + factory
├── Updates/                   # BuildInfo, GitHubUpdater
├── Diagnostics/               # AppLog
└── Runtime/                   # VolcoRuntime — orchestrates everything above

src/Volco.App/                # Avalonia UI (net8.0, platform-free, builds/tests on Linux)
├── VolcoApplication, CompositionRoot   # App shell: startup/shutdown, tray, windows
├── Views/                     # SettingsWindow + 5 pages (Connection, Sliders, Buttons, Output Devices, General), UpdateWindow
├── Osd/                       # Volume OSD pill
├── ViewModels/, Styles/, Controls/
└── Platform/                  # IWindowEffects seam

src/Volco.Windows/            # Windows exe (net8.0-windows) — ships as Volco.exe
├── Program.cs                 # Entry point: builds the engine, calls VolcoAppHost.Run
├── Audio/                     # AudioEngine, DeviceRegistry, SessionRegistry (Core Audio, event-driven)
└── Platform/                  # Win32WindowEffects (click-through OSD)

tests/Volco.Core.Tests/       # xUnit tests for parser, button detection, slider mapping, config, actions, VolcoRuntime
tests/Volco.App.Tests/        # Avalonia headless tests for ViewModels, pages, windows, OSD
```

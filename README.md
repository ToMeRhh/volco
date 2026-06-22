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
release. This path requires a GitHub Personal Access Token with `actions:read` scope, since the
Actions artifact API is authenticated even for public repos. The token is stored in your local
`config.json`.

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

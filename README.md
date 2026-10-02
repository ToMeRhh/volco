# Volco

A Windows system tray application that controls PC audio via an external hardware controller: any number of sliders and buttons, with optional LEDs or small OLED screens on the buttons. Connect over USB serial, Wi-Fi UDP or WebSocket — the app handles per-process volume, device volume, mute, output device switching and button presets, and shows what each button does on the controller.

## Features

- **Per-process volume control** — assign sliders to specific processes (e.g. `spotify.exe`, `discord.exe`)
- **Device volume control** — control the master volume of the default output device
- **"All non-mapped" slider** — catch-all that controls every process not assigned to another slider
- **Button actions** — configure single press, double press, and long press actions per button
- **Presets** — each button keeps its own actions per preset; switch the live preset from a button
- **Output device switching** — toggle the Windows default output between two configured devices
- **Mute controls** — mute/unmute specific devices, current output, or microphone input
- **Button screens** — buttons with an SSD1306 OLED show an icon, a state line and the action's name, with Hebrew/RTL text, scrolling for long names, a preset change that slides on all screens together, and burn-in protection (dim, off, pixel shift)
- **Devices page** — give devices nicknames and icons, used in the UI and on the controller screens
- **Controller previews** — the Buttons and Devices pages preview the lights and screens
- **Volume OSD** — a click-through on-screen volume pill
- **Any hardware layout** — the controller reports its sliders, buttons and indicators to the app (Hello), so the settings window follows the hardware
- **Flexible connectivity** — USB serial, Wi-Fi UDP or WebSocket, switchable in settings
- **System tray** — runs minimized, opens a settings UI on click

## Hardware

The controller is an ESP32 running the firmware in `firmware/`. Sliders are analog inputs on ADC pins, buttons are pins to GND, and each button can have an indicator: none, one LED, an LED pair, or an SSD1306 128x64 OLED screen (up to 4 screens, on the two I2C buses). The layout is two tables in `firmware/src/config.h`; **a control's position in its table is its input number**, and the app lists them in the same order. The firmware only reports raw input and draws what the app sends, so changing what a button does never needs a reflash. See [`firmware/README.md`](firmware/README.md) for configuration, wiring notes and the full protocol. `tools/simulator/` is a browser-based simulated controller (LED or screen variant) that connects over WebSocket.

## Architecture

```
Controller (ESP32: sliders, buttons, LEDs / OLED screens)
    ▲  │
    │  │  Binary protocol over Serial / UDP / WebSocket
    │  │  (0xAA framing, type, body, XOR checksum)
    │  ▼
┌──────────────────────────────────────────┐
│  Communication Layer                     │
│  Serial / Udp / WebSocketConnection      │
│  ├─ ProtocolParser (controller → app)    │
│  └─ ProtocolEncoder (app → controller)   │
├──────────────────────────────────────────┤
│  Runtime: VolcoRuntime                   │
│  ├─ ControllerLink (Hello, keepalive,    │
│  │    one ordered, deduplicated send     │
│  │    path)                              │
│  └─ ControllerDisplay (LED values and    │
│       screen content per button)         │
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
│  (Sliders, Buttons, Devices, Connection, │
│   General)                               │
└──────────────────────────────────────────┘
```

Input flows down: frames become slider and button events, and button presses run actions against the audio engine. Feedback flows back up: audio, preset and config changes are turned into LED values and screen descriptions and pushed to the controller.

### Wire Protocol

Every message, in both directions, is a frame: `0xAA | length | type | body ... | checksum`. `length` counts `type` plus `body`; `checksum` is the XOR of `type` and the body bytes. The high bit of `type` is the direction (`0x0_` from the controller, `0x8_` from the PC), and multi-byte integers are big-endian.

| Type | Name | Direction | Meaning |
|------|------|-----------|---------|
| `0x01` | Hello | controller → app | Slider count, button count, and each button's indicator kind (none, LED, LED pair, screen) |
| `0x02` | Alive | controller → app | Keepalive |
| `0x03` | Slider | controller → app | Slider index and 12-bit value (0–4095) |
| `0x04` | Button | controller → app | Button index and pressed/released |
| `0x81` | HelloRequest | app → controller | Ask the controller for Hello |
| `0x82` | Heartbeat | app → controller | Keepalive |
| `0x83` | Led | app → controller | LED value for a button |
| `0x84` | Screen | app → controller | Icon, top line and name for a button's screen |
| `0x85` | Begin | app → controller | Start holding updates |
| `0x86` | Commit | app → controller | Apply everything held at once (synchronized preset slide) |

The full protocol, including the Screen body and the Hello/keepalive/Begin-Commit rules, is in [`firmware/README.md`](firmware/README.md).

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
| Toggle Output Device | Switch between the action's own two output devices (A and B) |
| Set Output Device | Set a specific device as default output |
| Next Preset | Make the next button preset live |
| Switch to Preset | Make a specific button preset live |

## Configuration

Settings are stored in `%APPDATA%/Volco/config.json` and applied immediately when changed through the UI. The config includes connection settings, slider mappings, button presets and each button's actions per preset, device nicknames and icons, and the last hardware the controller reported (schema v4). Older v1–v3 configs are upgraded on load (v2 is rewritten with a `config.v2.bak`). The log is at `%APPDATA%/Volco/volco.log`.

## Tech Stack

- **C# / .NET 8 / Avalonia 12** — cross-platform UI; only the audio engine and a click-through shim are Windows-specific
- **Windows Core Audio API (WASAPI)** — device and per-process session volume/mute, driven directly via COM interop with no polling
- **COM `IPolicyConfig`** — default audio device switching
- **Avalonia `TrayIcon`** — system tray icon
- **System.IO.Ports** — serial communication
- **ESP32 / Arduino framework / PlatformIO** — controller firmware (C++17), with Unity-based native tests
- **U8g2** — SSD1306 OLED driver (Volco renders into its own 1-bit canvas)
- **Arimo font** (SIL Open Font License 1.1, `firmware/assets/fonts/OFL.txt`) — screen text; **Pillow** generates the font and icon assets
- **xUnit** — C# tests

## Building

Requires the .NET 10 SDK (pinned in `global.json`; projects still target net8.0) and, for the Windows exe, Windows.

```bash
dotnet publish src/Volco.Windows/Volco.Windows.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o ./publish/
```

The C# tests build and run on any OS: `dotnet test tests/Volco.Core.Tests` and `dotnet test tests/Volco.App.Tests`.

**Firmware** (needs [PlatformIO](https://platformio.org/)):

```bash
cd firmware
pio run                 # build
pio run -t upload       # flash
pio test -e native      # host-side tests of the libraries in lib/
```

Fonts and icons are generated, not hand-edited: from the repo root, `python3 firmware/tools/gen_assets.py` (needs Pillow).

**CI** (`build.yml`) runs on every push and pull request:
- **Run Tests** — the two C# test projects on Ubuntu.
- **Firmware Tests** — `pio test -e native`, plus ESP32 builds of the default firmware and build-checks of the screens, UDP and WebSocket variants.
- **Build and Publish** — the Windows single-file build and a downloadable `Volco-win-x64` artifact; runs on pushes to `main`, or manually with `run_build=true`.

## Releases & In-App Updates

Release builds are stamped with a version and a `release` channel; local/dev builds use the
`local` channel. Only release builds expose the **Check for Updates…** item in the tray menu.

**Cutting a release:** push a semver tag, or run the `release.yml` workflow manually from the
Actions tab with a version (it creates the tag too). It builds a stamped single-file `Volco.exe`
and publishes a GitHub Release with generated notes and the exe attached:

```bash
git tag v1.2.3
git push origin v1.2.3
```

**Updating:** the **Updates** panel on the General settings page (the tray's **Check for
Updates…** opens it) compares the running version against the latest GitHub Release and, if newer,
downloads `Volco.exe` and relaunches into it. A small helper script performs the in-place swap once
the app exits.

The panel also offers an advanced option to update from **development builds** (the `Volco-win-x64`
artifact of the most recent successful `main` build) instead of a published release.

**Authentication:** Volco is a private repository, so the updater requires a GitHub Personal
Access Token to query the API and download binaries (for both the release and CI-artifact paths).
The token needs `repo` (read) scope, plus `actions:read` for the CI-artifact option. It is entered
in the Updates panel and stored in your local `config.json`.

## Project Structure

```
src/Volco.Core/              # Platform-neutral (net8.0, builds/tests on Linux)
├── Config/                   # AppConfig data model, ConfigManager (JSON load/save), ConfigMigrator
├── Communication/            # IControllerConnection, Serial/UDP/WebSocket, ProtocolParser/Encoder, ControllerDisplay
├── Audio/                    # IAudioEngine + DTOs (interface only, no implementation)
├── Devices/                  # DeviceDisplay (nickname/icon resolution)
├── Input/                    # ButtonPressDetector, SliderProcessor
│   └── Actions/              # IButtonAction + 7 action implementations + factory
├── Updates/                   # BuildInfo, GitHubUpdater
├── Diagnostics/               # AppLog
└── Runtime/                   # VolcoRuntime, ControllerLink — orchestrates everything above

src/Volco.App/                # Avalonia UI (net8.0, platform-free, builds/tests on Linux)
├── VolcoApplication, CompositionRoot   # App shell: startup/shutdown, tray, windows
├── Views/                     # SettingsWindow + 5 pages (Sliders, Buttons, Devices, Connection, General)
├── Osd/                       # Volume OSD pill
├── ViewModels/, Styles/, Controls/     # Controls include the screen preview and the Updates panel
└── Platform/                  # IWindowEffects seam

src/Volco.Windows/            # Windows exe (net8.0-windows) — ships as Volco.exe
├── Program.cs                 # Entry point: builds the engine, calls VolcoAppHost.Run
├── Audio/                     # AudioEngine, DeviceRegistry, SessionRegistry (Core Audio, event-driven)
└── Platform/                  # Win32WindowEffects (click-through OSD)

firmware/                     # ESP32 firmware (PlatformIO, C++17)
├── src/                       # main, config.h (hardware tables), transports, indicators, screens
├── lib/                       # Pure C++ libs: volco_protocol, volco_bidi, volco_screen_state, volco_render
├── assets/                    # Icon PNGs and the Arimo fonts
├── tools/                     # gen_assets.py, draw_icons.py
└── test/                      # Native tests and golden frames shared with the C# tests

tools/simulator/              # Browser-based WebSocket controller simulator
docs/superpowers/             # Specs, plans, checklists and mockups

tests/Volco.Core.Tests/       # xUnit tests for parser, button detection, slider mapping, config, actions, VolcoRuntime
tests/Volco.App.Tests/        # Avalonia headless tests for ViewModels, pages, windows, OSD
```

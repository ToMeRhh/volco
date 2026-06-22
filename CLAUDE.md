# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test

**Do not run dotnet commands locally** — this dev machine doesn't have the right setup. Push to a branch and use GitHub Actions CI instead:

```bash
# Run tests only on any branch (default — use this)
gh workflow run build.yml --ref <branch-name>

# Check CI status
gh run list --branch <branch-name> --limit 3
```

**IMPORTANT — build job rules:**
- Pushing to `main` automatically triggers both tests and the build/publish job.
- Pushing to any other branch automatically triggers tests only.
- `gh workflow run` (workflow_dispatch) triggers tests only by default. **Never trigger the build on a branch unless the user explicitly asks.** If they do ask, use:
  ```bash
  gh workflow run build.yml --ref <branch-name> --field run_build=true
  ```

CI runs tests on Ubuntu (net8.0) and builds/publishes on Windows (net8.0-windows10.0.22621.0, x64). Tests use xUnit.

**Firmware** (ESP32, in `firmware/`):
```bash
cd firmware && pio run              # build
cd firmware && pio run -t upload    # flash
cd firmware && pio device monitor   # serial monitor
```

## Architecture

Volco is a Windows system tray app that controls PC audio via an external hardware controller (ESP32 with sliders and buttons). Two codebases live in this repo:

### Windows App (`src/Volco/`)

C# / .NET 8 / WinUI 3. Entry point is `App.xaml.cs` which wires everything together.

**Data flow:** Controller sends binary frames → `IControllerConnection` receives bytes → `ProtocolParser` extracts `ControllerMessage` → `SliderProcessor` or `ButtonPressDetector` handles input → `AudioManager` changes Windows audio state. For state feedback, `ProtocolEncoder` builds frames → `IControllerConnection.Send()` pushes to controller.

Key abstractions:
- `IControllerConnection` — transport interface (Serial, UDP, WebSocket). All three support bidirectional `Send`/receive.
- `ProtocolParser` — stateful byte stream parser. Finds `0xAA` start markers, validates XOR checksums, emits `ControllerMessage` events.
- `ProtocolEncoder` — static class that builds outbound frames (mute state, active device).
- `ButtonPressDetector` — classifies raw press/release into single, double, long press (300ms/500ms thresholds).
- `SliderProcessor` — maps 12-bit ADC values (0–4095) to volume, coalesces at 20ms intervals.
- `IAudioManager` — wraps NAudio/Core Audio for volume, mute, and device switching.
- `ActionFactory` — maps button config to `IButtonAction` implementations.

The csproj uses conditional compilation: on non-Windows platforms, UI, App.xaml.cs, and AudioManager are excluded so the protocol/config/input layers can build and test on Linux CI.

### ESP32 Firmware (`firmware/`)

PlatformIO / Arduino framework / C++17. Single `main.cpp` + `config.h`.

Transport is selected at compile time via `#define TRANSPORT_SERIAL|WEBSOCKET|UDP` in `config.h`. All pin assignments and tuning constants are in `config.h`.

### Wire Protocol

Bidirectional binary protocol. Frame: `0xAA | length | payload | XOR checksum`.

- `0x01` Slider (controller→server): index + 16-bit value
- `0x02` Button (controller→server): index + state
- `0x03` Mute State (server→controller): button index + muted flag
- `0x04` Active Device (server→controller): device index

### Web Simulator (`tools/simulator/`)

`index.html` — browser-based controller simulator using WebSocket. Supports full bidirectional protocol including LED state feedback. Open directly in a browser, no build step.

## Config

Runtime config stored at `%APPDATA%/Volco/config.json`. Managed by `ConfigManager` (load/save/change events). Includes connection settings, slider mappings, button actions, output device pair.

App log at `%APPDATA%/Volco/volco.log` via `AppLog` (async file writer with 500ms flush).

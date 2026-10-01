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

CI runs tests on Ubuntu (net8.0) and builds/publishes on Windows (net8.0-windows10.0.22621.0, win-x64) with `dotnet publish` (single-file, self-contained; no msbuild). There are two test projects, both run by the Linux job: `tests/Volco.Core.Tests/` (xUnit 2) and `tests/Volco.App.Tests/` (Avalonia headless, xUnit v3). `global.json` pins SDK 10.0.100 (needed by the Avalonia analyzers); every project still targets net8.0 / net8.0-windows10.0.22621.0, and both workflows install the 8.0.x and 10.0.x SDKs.

**Firmware** (ESP32, in `firmware/`):
```bash
cd firmware && pio run              # build
cd firmware && pio run -t upload    # flash
cd firmware && pio device monitor   # serial monitor
```

## Architecture

Volco is a Windows system tray app that controls PC audio via an external hardware controller (ESP32 with sliders and buttons). The app is split into four real projects (separate `.csproj`s, not `<Compile Remove>` tricks), plus the firmware:

### `src/Volco.Core/` — platform-neutral, `net8.0`, builds and tests on Linux

No UI, no platform APIs. Holds the protocol, config, input, and orchestration layers:
- `Communication/` — `IControllerConnection` (transport interface: Serial, UDP, WebSocket, all bidirectional `Send`/receive), `ProtocolParser` (stateful byte stream parser; finds `0xAA` start markers, validates XOR checksums, emits `ControllerMessage` events), `ProtocolEncoder` (static class building outbound frames — mute state, active device).
- `Input/` — `ButtonPressDetector` (classifies raw press/release into single, double, long press; 300ms/500ms thresholds), `SliderProcessor` (maps 12-bit ADC values 0–4095 to volume, coalesces at 20ms intervals), `Actions/` — `ActionFactory` maps button config to `IButtonAction` implementations.
- `Audio/` — `IAudioEngine`, the platform-neutral audio interface (the seam that lets everything else build and test on Linux), plus its DTOs: `AudioDevice`, `DeviceRef`, `AudioFlow` (`Render`/`Capture`). There is no `AudioRole` in this API — Core Audio's Console/Multimedia/Communications roles never varied per call site, so the public surface only exposes flow (`GetDefaultDevice(AudioFlow flow)`, no role parameter). `AudioRole` still exists, but only as an internal detail of the Windows engine.
- `Config/`, `Updates/` — `ConfigManager`/`ConfigMigrator`, `BuildInfo`/`GitHubUpdater`.
- `Diagnostics/` — `Volco.Core.Diagnostics.AppLog`.
- `Runtime/` — `VolcoRuntime`, the UI-facing orchestrator. This is what used to be ~350 lines of `App.xaml.cs`: it owns transport construction/`Connect()`/restart-on-config-change, routing `IControllerConnection.MessageReceived` to `SliderProcessor`/`ButtonPressDetector`, `ActionFactory` dispatch on detected presses, the pending-mute-OSD correlation, `PushControllerState` (with dedup against the last-pushed frame so slider drags don't flood the controller), and the post-connect re-push timers. It exposes only `Volco.Core` types (`IAudioEngine`, `ConfigManager`, `ConnectionStatus`, etc.) — nothing UI or Windows-specific leaks through it. Constructed headless (e.g. with a fake `IAudioEngine`), the whole app's behavior is driveable from Linux tests with no UI process.

### `src/Volco.Windows/` — `net8.0-windows10.0.22621.0`, class library

The COM audio engine, in namespace `Volco.Windows.Audio`:
- `AudioEngine` — the real Core Audio engine. Owns one dedicated MTA thread and a struct-command work queue; all COM activation, registration, and writes happen on that thread. `Start()` returns `bool` (whether the engine came up), which `VolcoRuntime` uses to skip config migration if audio isn't ready.
- `DeviceRegistry` — caches every live endpoint and its COM objects, kept current by `IMMNotificationClient` instead of re-enumeration.
- `SessionRegistry` — caches every live per-app audio session across all render devices, driven by `IAudioSessionNotification`; volume writes are an O(1) dictionary lookup, never an enumeration.
- `DefaultDevicePolicy` — switches the Windows default output device via the undocumented `IPolicyConfig` COM interface, replacing the old `svcl.exe` child process.

### `src/Volco.App/` — Avalonia 12 UI, `net8.0`, builds and tests on Linux

The whole UI as portable Avalonia code: `VolcoApplication`, `CompositionRoot` (startup/shutdown, tray, windows), the settings window and its five pages, the update window, the volume OSD, ViewModels, styles. It is a view over `VolcoRuntime`. **`Volco.App` and `Volco.Core` must stay platform-free** (no `DllImport`/`LibraryImport`, no `Windows.*`/`Microsoft.UI.*`, no CsWin32); the Linux CI job enforces this simply by compiling and testing them. Everything OS-specific comes in through two seams: `IAudioEngine` (Core) and `IWindowEffects` (App, `MakeClickThrough` for the OSD), bundled as `PlatformServices` and handed to `VolcoAppHost.Run`.

### `src/Volco.Windows/` — `net8.0-windows10.0.22621.0`, the shipping `Volco.exe`

`WinExe` with `<AssemblyName>Volco</AssemblyName>`, referencing `Volco.Core` and `Volco.App`. Entry point is `Program.Main`: it hooks the AppDomain/TaskScheduler exception loggers, builds the `AudioEngine`, and calls `VolcoAppHost.Run(args, () => new PlatformServices(engine, ready, new Win32WindowEffects()))`. Holds the Windows implementations of the two seams, in namespace `Volco.Windows.*`:
- `Audio/AudioEngine` — the real Core Audio engine. Owns one dedicated MTA thread and a struct-command work queue; all COM activation, registration, and writes happen on that thread. `Start()` returns `bool` (whether the engine came up), which `VolcoRuntime` uses to skip config migration if audio isn't ready.
- `Audio/DeviceRegistry` — caches every live endpoint and its COM objects, kept current by `IMMNotificationClient` instead of re-enumeration.
- `Audio/SessionRegistry` — caches every live per-app audio session across all render devices, driven by `IAudioSessionNotification`; volume writes are an O(1) dictionary lookup, never an enumeration.
- `Audio/DefaultDevicePolicy` — switches the Windows default output device via the undocumented `IPolicyConfig` COM interface.
- `Platform/Win32WindowEffects` — `IWindowEffects` via Win32 extended styles; the only UI P/Invoke in the repo.
- `app.manifest` (asInvoker, PerMonitorV2 DPI) and `volco.ico`.

**Data flow:** Controller sends binary frames → `IControllerConnection` receives bytes → `ProtocolParser` extracts `ControllerMessage` → `VolcoRuntime` routes to `SliderProcessor`/`ButtonPressDetector` → `IAudioEngine` changes Windows audio state. For state feedback, `ProtocolEncoder` builds frames → `IControllerConnection.Send()` pushes to controller.

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

Runtime config stored at `%APPDATA%/Volco/config.json`. Managed by `ConfigManager` (load/save/change events). Includes connection settings, slider mappings, button actions, output device pair. Schema v2 stores audio devices as endpoint IDs (with the friendly name kept for display and fallback); `ConfigMigrator` migrates v1 configs, which stored devices as bare name-strings, on load.

App log at `%APPDATA%/Volco/volco.log` via `AppLog` (async file writer with 500ms flush).

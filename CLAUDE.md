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
- Pushing to any branch, `main` included, automatically triggers tests only. The build/publish job never runs on push; it is manual only.
- `gh workflow run` (workflow_dispatch) triggers tests only by default. **Never trigger the build on a branch unless the user explicitly asks.** If they do ask, use:
  ```bash
  gh workflow run build.yml --ref <branch-name> --field run_build=true
  ```

CI runs tests on Ubuntu (net8.0) and builds/publishes on Windows (net8.0-windows10.0.22621.0, win-x64) with `dotnet publish` (single-file, self-contained; no msbuild). There are two test projects, both run by the Linux job: `tests/Volco.Core.Tests/` (xUnit 2) and `tests/Volco.App.Tests/` (Avalonia headless, xUnit v3). `global.json` pins SDK 10.0.100 (needed by the Avalonia analyzers); every project still targets net8.0 / net8.0-windows10.0.22621.0, and both workflows install the 8.0.x and 10.0.x SDKs.

**Firmware** (ESP32, in `firmware/`; needs PlatformIO Core >= 6.1.19 for the pinned pioarduino 55.03.38 platform, and a host `g++` for the native tests):
```bash
cd firmware && pio run -e esp32              # build
cd firmware && pio run -e esp32 -t upload    # flash (config in NVS survives)
cd firmware && pio device monitor   # serial monitor
cd firmware && pio test -e native   # host-side tests of firmware/lib (no hardware)
~/.platformio/penv/bin/python firmware/tools/gen_config_vectors.py  # regenerate firmware/test/golden/config.txt after a proto change
bash firmware/scripts/firmware_id.sh  # the firmware build id (hash of the firmware's sources)
~/.platformio/penv/bin/python firmware/tools/config_tool.py --port P read|write preset.json  # a controller's config (guarded port)
python3 firmware/tools/gen_assets.py  # regenerate icons and fonts (needs Pillow >= 10)
```

The `firmware-test` CI job runs `pio test -e native`, checks the firmware id and the config vectors, self-tests `scripts/manifest.py`, and builds the `esp32` environment with `VOLCO_FW_VERSION` = the build id; it uploads `.pio/build/esp32/dist/` (the four parts and `firmware-manifest.json`) as the `firmware-esp32` artifact and outputs the id. The Windows `build` job (`needs: firmware-test`) downloads it into `src/Volco.Windows/Firmware/esp32/`, which is embedded in `Volco.exe`, and publishes with `-p:RequireFirmware=true -p:VolcoFirmwareBuildId=<id>`, so the build fails without the firmware or with another build's. `firmware-test` also compiles `Volco.Windows` on Linux (`-p:EnableWindowsTargeting=true`) with that firmware embedded and `RequireFirmware`, and checks that a mismatched build id fails, so Windows-only code, the embedded resource names and the build-id check break on every push. `release.yml` runs version → firmware → release and attaches `volco-firmware-esp32-<version>.zip` with `THIRD-PARTY-NOTICES.txt`, `OFL.txt` and `relink/` (the LGPL relink bundle from `firmware/scripts/relink_bundle.py`: the firmware's link inputs as objects and the exact link command). `gen_assets.py` regenerates the firmware icons and fonts, the C# `ScreenIcon` enum and the simulator's `icons.js` and `fonts.js` from `firmware/assets`; its outputs are committed.

## Architecture

Volco is a Windows system tray app that controls PC audio via an external hardware controller (ESP32 with sliders and buttons). The app is split into three real source projects (`Volco.Core`, `Volco.App`, `Volco.Windows`) plus two test projects (separate `.csproj`s, not `<Compile Remove>` tricks), plus the firmware:

### `src/Volco.Core/` — platform-neutral, `net8.0`, builds and tests on Linux

No UI, no platform APIs. Holds the protocol, config, input, and orchestration layers:
- `Communication/` — `IControllerConnection` (transport interface: Serial, UDP, WebSocket, all bidirectional `Send`/receive; its `TransportStatus` (`Closed`/`Open`/`Failed`) is a port or socket fact, never "connected"), `ProtocolParser` (stateful byte stream parser; finds `0xAA` start markers, validates XOR checksums, emits `ControllerMessage` events), `ProtocolEncoder` (static class building outbound frames: HelloRequest, Heartbeat, Led, Screen, Begin, Commit, and the config read and write bursts), `ControllerDisplay` (decides what each button shows: its LED value or screen content, from its action in the live preset; replaces `ButtonFeedback`).
- `Input/` — `ButtonPressDetector` (classifies raw press/release into single, double, long press; 300ms/500ms thresholds; a release without its press fires nothing, and `Reset()` drops every gesture in progress), `SliderProcessor` (maps 12-bit ADC values 0–4095 to volume, coalesces at 20ms intervals), `Actions/` — `ActionFactory` maps button config to `IButtonAction` implementations.
- `Audio/` — `IAudioEngine`, the platform-neutral audio interface (the seam that lets everything else build and test on Linux), plus its DTOs: `AudioDevice`, `DeviceRef`, `AudioFlow` (`Render`/`Capture`). There is no `AudioRole` in this API — Core Audio's Console/Multimedia/Communications roles never varied per call site, so the public surface only exposes flow (`GetDefaultDevice(AudioFlow flow)`, no role parameter). `AudioRole` still exists, but only as an internal detail of the Windows engine.
- `Config/`, `Updates/` — `ConfigManager`/`ConfigMigrator`; `BuildInfo`, `GitHubUpdater` (the latest release from the public GitHub Releases API, unauthenticated; only its `Volco.exe` asset) and `UpdateInstaller` (downloads it and swaps it in after exit).
- `Devices/` — `DeviceDisplay`, the display name (nickname, else friendly name) and icon of a device, used by the OSD, the pickers and the controller screens.
- `Controller/` — the controller config: `ControllerConfigCodec`/`ControllerConfigValidator`/`ControllerPresets` (the protobuf blob), `ControllerConfigService` (owned by `VolcoRuntime`: reads the controller's config, saves one with retries, implied OK and confirmation, keeps the mirror, and runs the one automatic read), and `ControllerBannerLogic` (spec §8.3: what the Controller page shows after a Hello, as a pure function).
- `Firmware/` — `FirmwareManifest`/`FirmwareImage`/`FirmwareCatalog` (the bundled firmware, from an `IFirmwareSource`; a manifest part overlapping NVS is rejected), the `IFirmwareFlasher` seam, and `FirmwareService` (one probe or flash at a time: `VolcoRuntime.SuspendConnection()` frees the port and blocks every restart, a dialog port is saved while suspended (on the config's thread, the UI thread in the app), then the bundled build's Hello is awaited).
- `Diagnostics/` — `Volco.Core.Diagnostics.AppLog`.
- `Runtime/` — `VolcoRuntime`, the UI-facing orchestrator. This is what used to be ~350 lines of `App.xaml.cs`: it owns transport construction/`Connect()`/restart-on-config-change, routing `IControllerConnection.MessageReceived` to `SliderProcessor`/`ButtonPressDetector`, `ActionFactory` dispatch on detected presses, the pending-mute-OSD correlation, and `PushControllerState`, which delegates to `ControllerLink`. `Disconnect()` (the Connection page's and tray's Disconnect) drops the transport and starts none, even on a connection-config change, until `Connect()` or `Reconnect()`; it is not persisted. `ControllerLink` owns the controller protocol session: HelloRequest retries, the Hello handshake (nothing is pushed before it), keepalive, dedup against the last-sent content per button, and Begin/Commit bursts for preset changes. It computes, dedups and enqueues under its lock but sends through a single-consumer outbox outside it, dropping frames for a retired connection. It exposes only `Volco.Core` types (`IAudioEngine`, `ConfigManager`, `LinkStatus`, etc.) — nothing UI or Windows-specific leaks through it. It also runs the hardware test mode (spec §8.5): `EnterTestMode()` returns a handle; while it is held, Slider and Button messages raise `RawInput` and reach neither audio nor actions, LED buttons show `SetTestIndicator` values and screens a test screen; the runtime ends it itself on Detach, connection replacement and dispose. `LinkStatus` (`Runtime/LinkState.cs`, spec `docs/design/link-state.md`) is what the UI calls connected: `Connected` only after a Hello with this app's protocol version, else `Searching`, `PortBusy`, `NoAnswer`, `Incompatible`, the user's `Disconnected`, or `Waiting` (an expected gap: `ExpectInterruption` handles held by a firmware probe or flash, the connect grace of a start or reconnect, a config save); it is a pure function of the transport, the session, the silence, the interruptions and the save (`LinkStateLogic`), raised as `LinkStatusChanged`. Constructed headless (e.g. with a fake `IAudioEngine`), the whole app's behavior is driveable from Linux tests with no UI process.

### `src/Volco.App/` — Avalonia 12 UI, `net8.0`, builds and tests on Linux

The whole UI as portable Avalonia code: `VolcoApplication`, `CompositionRoot` (startup/shutdown, tray, windows; Exit and the updater's restart wait for a firmware write to finish through `ExitGate`, OS logoff never waits), the settings window and its six pages (Sliders, Buttons, Devices, Connection, Controller, General; General holds the Updates panel, release builds only), the volume OSD, ViewModels, styles. It is a view over `VolcoRuntime`. Every surface (tray, sidebar footer, Connection and Controller pages) reads only `VolcoRuntime.LinkStatus`, worded by `LinkText` ("Connected" only for a matching Hello, "Disconnected" only for the user's Disconnect; the physical layer is "plugged in", "port", "listening"); `ArchitectureTests` fails if `Volco.App` mentions `TransportStatus`. The tray icon turns red (`Assets/volco-disconnected.ico`) whenever the link is down (`LinkStatus.IsDown`: not Connected and not an expected wait), with a tooltip that says why (`TrayStatus`), and its menu has Connect/Disconnect; a controller that needs the user (a firmware update, none answering, not set up) adds a tooltip line and a menu item that opens the Controller page, and the attention icon (`Assets/volco-update.ico`) when the link is not down. The Devices page sets each device's nickname and screen icon (`config.devices`); device pickers, button-card summaries, screen previews and the OSD all show the resulting display name (`Volco.Core.Devices.DeviceDisplay`: nickname, else the automatic short name), with the Windows name as secondary text in pickers. The Controller page (`ControllerViewModel`, spec §8.4/§8.7) edits a draft of the controller config (a `Volco.Core.Controller.Proto.ControllerConfig`) and saves it through `ControllerConfigService`: status and the §8.3 banner with its actions, hardware rows (sliders, buttons and their indicators, I2C buses), the hardware test, screen behaviour, and an Advanced section (input tuning, bus clocks, Wi-Fi); editing confirmations are inline in its footer, while the Install confirmation and the setup dialog (a controller with no setup: apply the saved one, a preset or scratch; `ControllerViewModel.Setup.cs`) are modal overlays over the page, and leaving the page or hiding the window ends a test. Pages and their view models are cached, so a nickname/icon edit makes `SettingsViewModel` re-seed the Sliders and Buttons pickers and previews, and the Devices page re-reads "Used by" each time it is shown. **`Volco.App` and `Volco.Core` must stay platform-free** (no `DllImport`/`LibraryImport`, no `Windows.*`/`Microsoft.UI.*`, no CsWin32); `tests/Volco.App.Tests/ArchitectureTests.cs` enforces it by reflection (no P/Invoke, no Windows assembly references; `DllImport`, `LibraryImport` and CsWin32 all compile on Linux, so compiling alone cannot catch them), and the Linux compile catches WinRT and Windows SDK projections. Everything OS-specific comes in through two seams: `IAudioEngine` (Core) and `IWindowEffects` (App, `MakeClickThrough` for the OSD), bundled as `PlatformServices` and handed to `VolcoAppHost.Run`.

### `src/Volco.Windows/` — `net8.0-windows10.0.22621.0`, the shipping `Volco.exe`

`WinExe` with `<AssemblyName>Volco</AssemblyName>`, referencing `Volco.Core` and `Volco.App`. Entry point is `Program.Main`: it hooks the AppDomain/TaskScheduler exception loggers, builds the `AudioEngine`, and calls `VolcoAppHost.Run(args, () => new PlatformServices(engine, ready, new Win32WindowEffects()))`. Holds the Windows implementations of the two seams, in namespace `Volco.Windows.*`:
- `Audio/AudioEngine` — the real Core Audio engine. Owns one dedicated MTA thread and a struct-command work queue; all COM activation, registration, and writes happen on that thread. `Start()` returns `bool` (whether the engine came up), which `VolcoRuntime` uses to skip config migration if audio isn't ready.
- `Audio/DeviceRegistry` — caches every live endpoint and its COM objects, kept current by `IMMNotificationClient` instead of re-enumeration; it also reads each endpoint's form factor (`PKEY_AudioEndpoint_FormFactor`), which picks a device's default icon.
- `Audio/SessionRegistry` — caches every live per-app audio session across all render devices, driven by `IAudioSessionNotification`; volume writes are an O(1) dictionary lookup, never an enumeration.
- `Audio/DefaultDevicePolicy` — switches the Windows default output device via the undocumented `IPolicyConfig` COM interface.
- `Platform/Win32WindowEffects` — `IWindowEffects` via Win32 extended styles; the only UI P/Invoke in the repo.
- `Firmware/EspDotNetFirmwareFlasher` — `Volco.Core.Firmware.IFirmwareFlasher` over EspDotNet (the only project referencing it): ROM bootloader via DTR/RTS, chip check, the stub at 460800 baud when possible, MD5 probe of the app region, part-by-part writes with MD5 verify (Install also writes 0xFF over the manifest's NVS range; Update never touches it), reset. `Firmware/EmbeddedFirmwareSource` reads the embedded `Firmware/esp32/*`.
- `app.manifest` (asInvoker, PerMonitorV2 DPI) and `volco.ico`.

**Data flow:** Controller sends binary frames → `IControllerConnection` receives bytes → `ProtocolParser` extracts `ControllerMessage` → `VolcoRuntime` routes to `SliderProcessor`/`ButtonPressDetector` → `IAudioEngine` changes Windows audio state. For state feedback, `ProtocolEncoder` builds frames → `IControllerConnection.Send()` pushes to controller.

### ESP32 Firmware (`firmware/`)

PlatformIO / Arduino framework / C++17. `src/` (`main.cpp`, `config.h` (fixed constants), `config_store.*`, `config_session.*`, `transport.*`, `indicators.*`, `screens.*`) plus pure-C++ libraries in `lib/` (`volco_protocol`, `volco_config`, `volco_bidi`, `volco_screen_state`, `volco_render`) that are tested on the host with `pio test -e native`; `firmware/README.md` explains the code structure. Rendering uses Volco's own 1-bit canvas, with U8g2 only as the SSD1306 driver.

The controller config schema is `proto/volco_config.proto` (proto2; limits in `proto/volco_config.options`; presets in `proto/presets/`). It generates nanopb code for `firmware/lib/volco_config` (decode, validation, chip table, CRC, chunked receiver) and C# for `Volco.Core/Controller` (codec, validator, presets). Both are checked against the same generated vectors, `firmware/test/golden/config.txt`. Deferred review findings are listed in the spec's §14.1. The app runs the config session (`ControllerConfigService`), driven by the Controller settings page.

The firmware reads its hardware layout (sliders, buttons and their indicators, I2C buses 0-3, of which 2 and 3 are software I2C), the optional Wi-Fi/UDP link and the screen and input settings from the controller config stored in NVS (`config_store`, spec §6.6), never from `config.h`; `toRuntime()` turns it into the flat `RuntimeConfig` that `src/` uses. A table's order is the input number the app sees. An empty NVS boots on built-in defaults (no inputs); Volco's Controller page stores a config, and so does `firmware/tools/config_tool.py --port P write preset.json` for firmware work without the app (the board reboots into it; `read` prints the stored one). USB serial is always on; UDP runs alongside when the config enables it, and the controller answers on whichever transport last sent it a valid frame (spec §5.5). `firmware/tools/hello_check.py --port <port> --sliders N --kinds …` resets the board and checks its boot Hello; it refuses any USB device but the DIY board's CH340 (`1a86:7523`), and tty numbering can change after a replug, so always let it check first. The firmware has no notion of mute or devices; it reports raw input and its hardware (Hello), and renders the LED values and screen descriptions the PC sends.

### Wire Protocol

Bidirectional binary protocol. Frame: `0xAA | length | type | body | XOR checksum`. The high bit of `type` is the direction; integers are big-endian. Full details (Screen body, behaviour rules) are in `firmware/README.md`.

| Type | Name | Direction | Body |
|---|---|---|---|
| `0x01` | Hello | controller→PC | version, chip, firmware id, config state and CRC, slider count, button count, one kind per button (0 none, 1 LED, 2 LED pair, 3 screen) |
| `0x02` | Alive | controller→PC | none (keepalive) |
| `0x03` | Slider | controller→PC | index + 16-bit value |
| `0x04` | Button | controller→PC | index + pressed |
| `0x05` | ConfigData | controller→PC | seq, total, offset, CRC, one chunk of the stored config |
| `0x06` | ConfigResult | controller→PC | seq, code, item, field (the answer to a config write) |
| `0x81` | HelloRequest | PC→controller | none |
| `0x82` | Heartbeat | PC→controller | none (keepalive) |
| `0x83` | Led | PC→controller | button + value |
| `0x84` | Screen | PC→controller | button, transition, layout, top, icon, flags, main text |
| `0x85` | Begin | PC→controller | none (hold updates) |
| `0x86` | Commit | PC→controller | none (apply held updates together) |
| `0x87` | ConfigRead | PC→controller | seq |
| `0x88`/`0x89`/`0x8A` | ConfigWriteBegin/Chunk/End | PC→controller | seq + total and CRC / offset and data / none |

`Communication/ControllerDisplay` decides what each button shows (its LED value or screen content) from its action in the live preset. A single LED and an LED pair differ: a pair shows a toggle's side (1 = A, 2 = B) and the live preset's position, while one LED is lit on the toggle's B and while a "Go to preset" target is live. For `AnnounceSteps` link ticks (~1.5 s) after a preset change, a button with a preset action announces it: one LED blinks, and a screen that shows another action flashes the live preset. The firmware needs no change for this, because the link re-sends on each tick. `Runtime/ControllerLink` runs the session: Hello, keepalive, dedup, the Begin/Commit bursts for preset changes, and the announce ticks. (The old `0x03` mute state, `0x04` active device and `0x05` feedback messages are gone with protocol v1.)

### Web Simulator (`tools/simulator/`)

`index.html` is a 3D controller simulator (three.js; a home-office desk) that speaks the full protocol and config session to Volco over WebSocket. Open it directly in a browser; there is no build step: three.js (pinned 0.170.0, through an import map and one inline module that publishes `window.VolcoThree`) and protobufjs load from jsDelivr, and every other file is a classic script that also loads under Node. The logic has no DOM: `protocol.js`, `link.js` (the firmware Link, with fault hooks), `config.js` (the config session; the blob is kept in `localStorage`), `layout.js` (the builder's model, presets, and ESP32 pin allocation that passes the app's validator), `oled.js` (a port of `volco_render`, `volco_bidi` and `volco_screen_state`, pixel-identical through `fonts.js`) and `demo.js` (demo mode, and which output a Volco screen shows, for the sound waves). The scene is `stage.js`, `room.js` and `device.js`; the UI is `connection.js`, `builder.js` (a draft with Apply and Auto-save) and `devtools.js` (frames, config, faults), wired by `app.js`. While no WebSocket is open the board runs in demo mode. `tools/simulator/test` holds the Node tests (`npm ci && node --test`), run by the `simulator-test` CI job; they use `firmware/test/golden/frames.txt` and `render.txt` (layout-1 bitmaps from the firmware renderer; regenerate with `VOLCO_WRITE_GOLDEN=1 pio test -e native -f test_render`), and `gen_descriptor.mjs` regenerates `volco_config_descriptor.js` after a proto change.

## Config

Runtime config stored at `%APPDATA%/Volco/config.json`. Managed by `ConfigManager` (load/save/change events; a debounced `RequestSave` still pending at exit is written by `FlushPendingSave` after the runtime is disposed). Includes connection settings, slider mappings, button presets and button actions. A slider's or button's **position in its list is its hardware input number** (`Index` is rewritten to match on save). Schema v5 adds `controllerConfigBlob` (the mirror: base64 of the controller's config bytes, written only by `ControllerConfigService` on a confirmed save or an adopted read) and drops the serial baud rate (fixed at 115200). Schema v4 adds `devices` (per-device nicknames and icons) and `controller` (the last Hello's hardware summary). Schema v3: each button keeps its actions per preset (`buttons[i].actions[presetId]`), and the toggle action carries its own A/B devices (`device`/`deviceB`); there is no global output pair. Devices are stored as endpoint IDs with the friendly name kept for display and fallback. `ConfigManager.Load` upgrades v1/v2 JSON to the v3 shape (v2 is rewritten with a `config.v2.bak`); v1's device-name resolution still runs in `VolcoRuntime.Start` once audio is up.

App log at `%APPDATA%/Volco/volco.log` via `AppLog` (async file writer with 500ms flush).

# Volco on macOS — Design Spec

**Date:** 2026-10-03
**Status:** Draft, awaiting review
**Builds on:** PR #68 (slider positions after Hello), `2026-09-29-volco-avalonia-technical-design.md` (the `IAudioEngine` / `IWindowEffects` seams) and `2026-10-02-oled-screens-design.md` (current feature set). This spec supersedes the claim in `2026-09-26-volco-audio-core-design.md` §non-goals and `2026-09-26-volco-option-a-avalonia-design.md` that macOS per-app volume needs a virtual audio driver: since macOS 14.2, Core Audio process taps provide it with public API.

## 0. Scope

- **Goal:** a macOS build of Volco with the same functionality, settings UI and UX as the Windows build: all five settings pages, presets, every button action, every slider type (Device, Processes, Unmapped), the volume OSD, the controller screens and LEDs, all three transports, and the in-app updater.
- **Rewrites allowed.** There are no installed users, so `Volco.Core`, `Volco.App` and `Volco.Windows` may be refactored where that keeps the macOS path clean. The Windows build must stay green and behave the same after each step.
- **Targets:** Apple Silicon only (`osx-arm64`), macOS **14.2 (Sonoma) or later**. Tested by hand on the user's M-series Mac.
- **Out of scope:**
  - Intel Macs and universal binaries;
  - Developer ID signing and notarization (the user will not join the Apple Developer Program);
  - a virtual audio driver;
  - a DMG installer;
  - launch at login (Windows does not have it either);
  - any wire protocol or firmware change;
  - a Mac-native UI redesign.

## 1. Decisions (summary)

| # | Decision |
|---|---|
| D1 | **Per-app volume through Core Audio process taps** (macOS 14.2+), not a virtual driver and not dropped. Costs: the "System Audio Recording" permission prompt, and Volco runs a real-time gain stage for apps that are turned down. |
| D2 | **Self-signed code-signing certificate**, stored as a CI secret, used for every build. macOS keys privacy permissions on the signing identity, so the tap permission survives updates. Plain ad-hoc signing would change identity every build and re-prompt. |
| D3 | **Same app, Mac conventions at the edges.** Pages, tokens, OSD look and flows are unchanged. Mac-native only where a Mac user would notice: menu-bar template icon, click opens the menu, "Quit Volco" + ⌘Q, ⌘, for Settings, ⌘W, traffic-light title bar, no Dock icon, OSD placed like the system HUD. |
| D4 | **C# control plane plus a small native tap library.** `MacAudioEngine` and its registries are C# with P/Invoke to the Core Audio C API, mirroring the Windows engine. Only the process tap, its aggregate device and the real-time gain IO proc live in `libvolcotap.dylib` (Objective-C, ~300 lines, C ABI). Managed code never runs on a real-time audio thread. |
| D5 | **New platform seams instead of `OperatingSystem.IsMacOS()` checks** in Core/App: `IPlatformShell`, `IUpdateApplier`, `AppIdentity` (§3). Core and App stay platform-free. |
| D6 | **Spike first.** A throwaway probe on the user's Mac settles the tap semantics (§4.5) before the engine is built. |
| D7 | **The extra latency of tapped apps is documented** in the README, `CLAUDE.md` and the hardware checklist, with the figure measured in the spike (§4.6). |

## 2. Project layout

```
src/
  Volco.Core/          shared (changes in §3)
  Volco.App/           shared (changes in §3)
  Volco.Windows/       Windows head (gains WindowsPlatformShell, WindowsUpdateApplier)
  Volco.Mac/           NEW: macOS head, net8.0, RID osx-arm64
    Program.cs
    Audio/
      MacAudioEngine.cs      IAudioEngine; engine thread + command queue
      DeviceRegistry.cs      devices, volume/mute, defaults, listeners
      ProcessRegistry.cs     audio process objects → AudioSessionInfo
      TapManager.cs          slider targets → taps (planning logic + native calls)
      Interop/CoreAudio.cs   P/Invoke: AudioObject*, AudioHardwareService*, libproc
      Interop/VolcoTap.cs    P/Invoke into libvolcotap
    Platform/
      MacWindowEffects.cs    OSD click-through, level, Spaces
      MacPlatformShell.cs    paths, reveal-in-Finder, tray icon, activation
      MacUpdateApplier.cs
    Bundle/
      Info.plist
      Volco.icns
      tray-template.png (@1x, @2x)
native/
  volcotap/
    volcotap.h
    volcotap.m
build/
  build-mac.sh         publish + dylib + bundle + sign + zip
tests/
  Volco.Mac.Tests/     NEW: macOS-only interop smoke tests
```

`Info.plist` keys: `CFBundleIdentifier` `com.volco.app`, `LSUIElement` true, `LSMinimumSystemVersion` 14.2, `NSAudioCaptureUsageDescription` ("Volco turns individual apps' volume up and down from your controller."), `CFBundleShortVersionString` stamped from the release version.

## 3. Changes to shared code

### 3.1 `PlatformServices`

```csharp
public sealed record PlatformServices(
    IAudioEngine   Audio,
    bool           AudioReady,
    IWindowEffects WindowEffects,
    IPlatformShell Shell,
    IUpdateApplier Updates);
```

### 3.2 `IPlatformShell` (Volco.App)

| Member | Windows | macOS |
|---|---|---|
| `DataDirectory` | `%APPDATA%\Volco` | `~/Library/Application Support/Volco` |
| `LogDirectory` | `%APPDATA%\Volco` | `~/Library/Logs/Volco` |
| `RevealInFileManager(path)` | Explorer | `open -R` |
| `TrayIcon` (stream + `IsTemplate`) | `volco.ico` | template PNG |
| `ActivateApp()` | no-op | `NSApp activateIgnoringOtherApps:` (a Dock-less app otherwise opens windows behind others) |
| `AppIdentity` | `.exe` names | bundle IDs (§3.4) |

`AppLog` and `ConfigManager` take their directories from the shell instead of calling `SpecialFolder.ApplicationData` (on macOS .NET maps that to `~/.config`). `AppLog` must still log before the shell exists: `VolcoAppHost.Run` receives the shell first, or `AppLog` starts with a platform default set by each `Program.Main`.

### 3.3 `IUpdateApplier` (Volco.Core)

`UpdateInstaller.ApplyAndRestart` (the `cmd.exe` + `.bat` swap) moves to `Volco.Windows/WindowsUpdateApplier`. `GitHubUpdater` asks the applier for its release asset name: `Volco.exe` on Windows, `Volco-macos-arm64.zip` on macOS. Download and staging stay in Core.

### 3.4 `AppIdentity`

Per-app sliders match `AudioSessionInfo.ImageName` against `SliderConfig.Processes`. Today that, and `SliderProcessor.FriendlyProcessName`, assume `.exe` names. `AppIdentity` (supplied by the platform) provides:

- `DisplayName(string key)`: Windows strips `.exe` and capitalises (today's behaviour); macOS maps a bundle ID to the app's name ("com.spotify.client" → "Spotify"), falling back to the last bundle-ID component.
- `Matches(string configured, AudioSessionInfo session)`: Windows is today's case-insensitive image-name match; macOS accepts the bundle ID or the app name, case-insensitive.
- `PlaceholderExample`: "spotify.exe" / "Spotify".

On macOS, config stores bundle IDs. The Sliders page's app list (fed from `GetSessions()`) already offers live apps; free text is matched by `Matches`.

### 3.5 Smaller changes

- `SerialConfig.ComPort` default becomes `""` ("first available port"). The Connection page on macOS lists `/dev/cu.*` and hides the `/dev/tty.*` duplicates; `SerialConnection`'s presence check works on the full path.
- "Windows name" becomes **"System name"** on both platforms (`DeviceItemViewModel`, Devices page, picker secondary text, comments).
- Tray menu "Exit" becomes "Quit Volco" on macOS (from the shell). The macOS app menu (`NativeMenu`) gets Settings… (⌘,) and Quit Volco (⌘Q); the settings window gets ⌘W.
- `AudioDevice` gains `bool HasVolume = true` (§4.1). Device pickers show "no volume control" for a device without it; a Device slider on such a device does nothing.
- `IAudioEngine` gains `PerAppStatus PerAppStatus { get; }` (`Available`, `NeedsPermission`, `Unsupported`) and `event EventHandler? PerAppStatusChanged`. Windows always reports `Available`.
- The Sliders page shows a banner when `PerAppStatus != Available` and any slider is Processes/Unmapped: "Per-app volume needs the System Audio Recording permission." with **Open Privacy Settings** (opens `x-apple.systempreferences:com.apple.preference.security?Privacy_AudioCapture` through the shell).
- The Connection page shows a one-line hint on macOS in UDP/WebSocket mode: macOS may ask to allow incoming network connections.
- `ArchitectureTests` additionally assert that `Volco.Core` and `Volco.App` do not reference `Volco.Mac` or `Volco.Windows`.
- `IAudioEngine.SetDefaultDevice`'s doc comment loses "Windows" ("Sets the system default device for this flow").

## 4. The macOS audio engine

### 4.1 Threading and devices

- **One engine thread with a struct-command queue**, as on Windows. All Core Audio calls and registry mutations happen there; queries are served from the in-memory registries.
- **Listeners** use `AudioObjectAddPropertyListener` with an `[UnmanagedCallersOnly]` function pointer. A listener only posts a command; it does no work on the HAL notification thread.
- **Watched properties:** `kAudioHardwarePropertyDevices`, `DefaultOutputDevice`, `DefaultInputDevice`, `ProcessObjectList`; per device `VirtualMainVolume` and `Mute` in each scope it has, `kAudioDevicePropertyDataSource` (built-in output), and `kAudioDevicePropertyDeviceIsAlive`.
- **Identity:** one `AudioDevice` per (device, direction). `Id = "<kAudioDevicePropertyDeviceUID>:out"` or `":in"`. A direction exists when the device has streams in that scope. Skipped: `kAudioDevicePropertyIsHidden` devices and Volco's own aggregates (UID prefix `com.volco.tap.`).
- **Name:** `kAudioObjectPropertyName`.
- **Form factor:**

  | Source | `DeviceFormFactor` |
  |---|---|
  | input direction, any transport | `Microphone` (unless Bluetooth headset below) |
  | built-in output, data source `hdpn` | `Headphones` |
  | built-in output, other data source | `Speakers` |
  | Bluetooth / BLE device with both directions | `Headset` |
  | Bluetooth output only | `Headphones` |
  | HDMI, DisplayPort | `Display` |
  | USB output | `Speakers` |
  | AirPlay, virtual, aggregate, other | `Other` |

- **Volume:** `kAudioHardwareServiceDeviceProperty_VirtualMainVolume` (AudioToolbox `AudioHardwareService*`), the property the system volume keys use; it handles per-channel-only devices. `HasVolume` is whether that property is settable.
- **Mute:** `kAudioDevicePropertyMute` on the main element. A device without a settable mute (common for Mac microphones) gets **emulated mute**: mute stores the current level and sets 0; unmute restores it. `TryGetDeviceState` reports the emulated state. This keeps "Mute current input" and its LED/screen feedback correct.
- **Default device:** `SetDefaultDevice(id, Render)` sets `kAudioHardwarePropertyDefaultOutputDevice` and `DefaultSystemOutputDevice`; `Capture` sets `DefaultInputDevice`. Public API.
- **`Start()`** returns false if the HAL cannot be queried; same contract as Windows.

### 4.2 Own-change detection

Core Audio notifications carry no context, so `IsOwnChange` cannot come from the OS. The engine records each write it makes (device, level, muted, timestamp). A notification whose values match a write to the same device within 500 ms (level within 0.01) is raised with `IsOwnChange = true`, and consumes that record. The matching logic is a pure class (`OwnChangeFilter`) in Core so it is tested on Linux.

### 4.3 Sessions (`ProcessRegistry`)

- Source: `kAudioHardwarePropertyProcessObjectList`. Per process object: `kAudioProcessPropertyPID`, `BundleID`, `IsRunningOutput`, `Devices`. Listen to the list and to each object's `IsRunningOutput`.
- **Owning app:** audio often comes from helper processes (`com.apple.WebKit.GPU`, "Google Chrome Helper", Electron renderers). Resolve the owner by `responsibility_get_pid_responsible_for_pid` (libsystem); if unavailable or it returns the PID itself, walk `proc_pidpath` up to the outermost `.app` and read its `CFBundleIdentifier`. Several process objects can share one owner.
- **`AudioSessionInfo`:** `InstanceId` = process object ID, `ProcessId` = PID, `ImageName` = owner's bundle ID, `DeviceId` = first output device's `:out` id, `IsSystemSounds` = the system sound server (`com.apple.systemsoundserverd`). Volco's own process is excluded.
- `SessionsChanged` Created/Expired follow the process object list.

### 4.4 Per-app volume (`TapManager` + `libvolcotap`)

- **Targets.** Each Processes slider is a target with a set of configured apps; the Unmapped slider is one target meaning "everything not listed by a Processes slider". `SetSessionVolume(filter, level)` updates the target's gain and, when needed, its tap.
- **Tap per target:**
  - `ByImageNames` → `CATapDescription initStereoMixdownOfProcesses:` with the matching process objects.
  - `Unmapped` → `initStereoGlobalTapButExcludeProcesses:` with all mapped process objects and Volco's own.
  - `muteBehavior = CATapMutedWhenTapped`, `privateTap = YES`.
  - A private aggregate device (UID `com.volco.tap.<n>`) whose main sub-device is the output the target's apps play to, with the tap in its tap list. An IO proc copies tap input × gain to the output.
- **Gain:** an atomic float written from C#, read by the IO proc, ramped linearly across each buffer to avoid zipper noise.
- **Lifecycle:**
  - Level ≥ 0.999 → the tap and aggregate are destroyed (zero latency, zero CPU for untouched apps).
  - Level < 0.999 and no tap → create.
  - The target's process set changes (an app starts/stops playing, a Processes list is edited), or the output device the apps play to changes → rebuild, debounced 250 ms.
  - Engine `Dispose` destroys all taps. When Volco quits, apps return to 100% (Windows keeps per-app levels in its mixer; accepted difference).
  - **Restoring levels after a restart.** Since PR #68 the firmware follows every Hello with each slider's position, and `SliderProcessor.BeginSync` applies those values quietly (no smoothing, no OSD). On macOS that sync is what recreates the taps, so per-app levels come back as soon as the controller connects. The remaining gap is only while Volco is not running or the controller is not connected. Volco does not save per-app levels itself (not needed given the sync).
- **Native interface** (`volcotap.h`, C ABI; all decisions stay in C#):

  ```c
  typedef struct volcotap* volcotap_t;
  volcotap_t volcotap_create(const AudioObjectID* processes, int count, int exclude,
                             const char* output_device_uid, OSStatus* err);
  void       volcotap_set_gain(volcotap_t tap, float gain);
  void       volcotap_destroy(volcotap_t tap);
  ```

- **Permission:** the first tap triggers the "System Audio Recording" prompt. Because of the Hello sync, that first tap is usually created right when the controller connects (any per-app slider below 100%), not when the user first moves a slider; the prompt and the Sliders-page banner must make sense at that moment. A creation failure caused by a denied permission sets `PerAppStatus = NeedsPermission`; the engine retries tap creation when a per-app slider next moves, so granting the permission takes effect without a restart. macOS earlier than 14.2 never reaches here (`LSMinimumSystemVersion`), but the engine reports `Unsupported` if the tap API is missing.
- **Accepted differences:** tapped audio gains extra latency (§4.6); per-app levels are not persisted by the OS.

### 4.5 Spike (first task; throwaway)

A standalone probe on the user's Mac, not kept, answering:

1. Can a tap's replay be bound to the device each app plays to, so an app on a headset is not moved to the speakers? If a single tap mixes all devices, `TapManager` uses one tap per (target, output device), grouping process objects by `kAudioProcessPropertyDevices`.
2. Does the permission prompt appear, and does a granted permission survive re-signing a new build with the same self-signed identity?
3. Measured latency of the tap → aggregate → output path, and CPU at idle with one tap.
4. Does muting a process via `mutedWhenTapped` leave other processes on the same device untouched?

Findings update §4.4 before the engine work starts.

### 4.6 Added latency for turned-down apps (must be documented)

- **What happens.** An app at 100% plays straight to its output device. An app turned below 100% is tapped: macOS captures its audio, Volco's IO proc scales each buffer by the slider level, and the aggregate device plays it out. That detour adds one or two IO buffers, so the app's sound reaches the speakers later than it otherwise would. Estimate: **about 10–20 ms** (one buffer of 512 frames at 48 kHz is 10.7 ms). The spike (§4.5 item 3) measures the real figure; the measured number replaces this estimate everywhere below.
- **Who notices.** Not noticeable for music, podcasts, browsers, voice calls or video (audio lag under about 45 ms is generally not perceived as lip-sync error). It can matter for rhythm games, competitive games and live music work through a DAW, and only when that app is turned below 100%.
- **Not affected:** apps at 100% (no tap exists), Device sliders (hardware volume), button actions, and the Windows build.
- **Mitigation:** if the measured latency is above 15 ms, the aggregate device requests a smaller IO buffer (`kAudioDevicePropertyBufferFrameSize`, 128–256 frames, about 3–5 ms) at some CPU cost; the chosen size and its measured latency are recorded here.
- **Documentation requirement.** The latency is a user-visible behaviour difference from Windows and ships documented:
  - the README's macOS section explains it in user terms (what it is, the measured figure, who it affects, that apps at 100% are unaffected);
  - `CLAUDE.md`'s `Volco.Mac` architecture entry states it next to the tap design, so future changes to `TapManager` or the buffer size keep the figure current;
  - the hardware checklist records the measured value on the user's Mac.

  This is a done-criterion for step 5 of §10 (`TapManager` + `libvolcotap`), not a follow-up.

## 5. Shell and UX on macOS

- `MacOSPlatformOptions { ShowInDock = false }` plus `LSUIElement`.
- **Menu-bar icon:** monochrome template PNG (light/dark menu bars). Clicking it opens the menu: Open Settings, Check for Updates… (release builds), separator, Quit Volco.
- **Settings window:** native title bar with traffic lights; ⌘, opens it, ⌘W closes it, ⌘Q quits. Opening it calls `IPlatformShell.ActivateApp()`. Pages, layout and tokens unchanged.
- **OSD (`MacWindowEffects`):** on the `NSWindow` from `TryGetPlatformHandle()`: `setIgnoresMouseEvents:YES`, `setLevel:NSStatusWindowLevel`, `setCollectionBehavior:` `CanJoinAllSpaces | FullScreenAuxiliary | Stationary`. Position bottom-centre of the active screen, like the system volume HUD. Look unchanged.
- **High contrast:** follows Avalonia `PlatformSettings` (macOS "Increase contrast"); checked on hardware.

## 6. Packaging and release

`build/build-mac.sh`, run on the `macos-latest` (arm64) runner:

1. `dotnet publish src/Volco.Mac -c Release -r osx-arm64 --self-contained -p:UseAppHost=true` (not single-file).
2. `clang -dynamiclib -fobjc-arc -framework CoreAudio -framework AudioToolbox -framework Foundation -mmacosx-version-min=14.2 native/volcotap/volcotap.m -o libvolcotap.dylib`.
3. Assemble `Volco.app`: published files into `Contents/MacOS`, dylib into `Contents/Frameworks` (P/Invoke resolves it via a `NativeLibrary` resolver), `Info.plist`, `Volco.icns` into `Contents/Resources`.
4. Import the self-signed identity (`MAC_SIGNING_P12`, `MAC_SIGNING_PASSWORD` secrets) into a temporary keychain; `codesign --force --deep --sign "Volco Self-Signed"` (no hardened runtime: it is only required for notarization, and would need JIT entitlements for .NET); `codesign --verify --deep --strict`.
5. `ditto -c -k --keepParent Volco.app Volco-macos-arm64.zip`.

- `build.yml` gets a `build-mac` job under the same rules as the Windows build job (main push, or `run_build=true`), plus a `test-mac` job that runs `Volco.Mac.Tests` on every push.
- `release.yml` runs the Mac build and attaches the zip to the same Release as `Volco.exe`; `BuildInfo` stamping applies to both.
- README: install = unzip, move to Applications, first launch via System Settings → Privacy & Security → **Open Anyway** (macOS 15 removed right-click → Open for unnotarized apps).
- The self-signed certificate is created once (Keychain Access → Certificate Assistant, code-signing type) and its `.p12` stored in the repo secrets. Its creation steps go in `docs/`.

## 7. Updater on macOS

- `GitHubUpdater` downloads `Volco-macos-arm64.zip` to the staging directory and unzips it with `ditto -x -k`.
- `MacUpdateApplier.Stage` checks that the staged `Volco.app`'s signing identity (`codesign -dr -`) matches the running bundle; a mismatch aborts the update with an error in the update window.
- `ApplyAndRestart` writes and starts a detached `/bin/sh` script, then Volco exits. The script waits for the PID to exit, moves the running `.app` to `<name>.app.old`, moves the staged one into place, restores the old one on failure, runs `open` on the new bundle, and removes the old copy and itself.
- `HttpClient` downloads carry no quarantine attribute, so the swapped bundle does not hit Gatekeeper.

## 8. Error handling

- Engine start failure: `Start()` false; existing "audio engine failed" tooltip and log path.
- Device gone mid-write (`kAudioHardwareBadObjectError`, `kAudioHardwareBadDeviceError`): logged, ignored; the device-list listener removes it.
- Tap create/rebuild failure: logged, `PerAppStatus` updated, the target left untapped (apps at 100%); never throws into `VolcoRuntime`.
- `libvolcotap` failing to load: `PerAppStatus = Unsupported`, everything else keeps working.

## 9. Testing

- **Linux CI (unchanged jobs, new tests):** `AppIdentity` matching and display names for both platforms; `OwnChangeFilter`; `TapManager` planning (filter + sessions → create / rebuild / destroy decisions) against a fake native layer; Sliders page permission banner (headless Avalonia); Connection page port listing with macOS-style names; `ArchitectureTests` additions.
- **macOS CI (`Volco.Mac.Tests`):** P/Invoke smoke tests (list devices, read name/UID/transport, read default output, load `libvolcotap` and resolve its symbols), `Info.plist` validity, bundle layout and signature verification. CI runners have no real audio hardware, so no audio behaviour is asserted there.
- **Hardware checklist** (`docs/superpowers/checklists/`, on the user's Mac): each slider type (Device, Processes, Unmapped, None); each button action; device without hardware volume; emulated mic mute; hot-plug USB and Bluetooth; external default-device change (Control Center); OSD over a full-screen app and on a second Space; permission denied then granted; update round-trip with the permission kept; Serial, UDP and WebSocket against the real ESP32; screens and LEDs; the Windows build re-checked after the shared-code steps.

## 10. Order of work

Each step ends with a branch that builds and passes CI; steps 3–6 are tried on the user's Mac as they land.

1. **Spike** (§4.5), findings folded into this spec.
2. **Shared-code seams and cleanups** (§3), Windows behaviour unchanged.
3. **`Volco.Mac` head:** shell, window effects, bundle, signing, CI job; a device-only engine (`PerAppStatus = Unsupported`).
4. **Device and process registries** (§4.1–4.3).
5. **`TapManager` + `libvolcotap`** (§4.4), including the latency documentation (§4.6).
6. **Updater** (§7).
7. **Release wiring and README** (§6).

## 11. Alternatives rejected

Recorded so they are not reopened without new information.

| Alternative | Why rejected |
|---|---|
| **Virtual audio driver** (BackgroundMusic-style HAL plugin) for per-app volume | Needs an installer with admin rights and a signed driver; heavier to ship and maintain. Process taps (macOS 14.2+) give the same result with public API and no install step. |
| **Device-only on macOS**, per-app sliders hidden | Breaks the goal of feature parity (§0). Process taps make parity possible. |
| **Swift engine with a thin C# shim** | Moves the engine logic out of the .NET tests, puts shared state across two languages, and makes the native surface large. Only the real-time path needs to be native (D4). |
| **Pure C#, gain IO proc as an `[UnmanagedCallersOnly]` managed callback** | A garbage-collection suspension on the real-time audio thread would glitch every tapped app. |
| **.NET macOS workload (`net8.0-macos`, Xamarin.Mac bindings)** | Does not fit Avalonia's plain-`net8.0` host; adds nothing P/Invoke and `objc_msgSend` cannot do for the handful of AppKit calls needed. |
| **Developer ID signing + notarization** | The user will not join the Apple Developer Program. A self-signed certificate keeps privacy permissions stable across updates (D2); the cost is a one-time "Open Anyway" on first install. |
| **Plain ad-hoc signing** | The signing identity changes every build, so macOS would re-ask for the System Audio Recording permission after every update. |
| **Hardened runtime** | Only required for notarization, and would need JIT entitlements for .NET. |
| **Intel or universal builds** | The code is architecture-neutral, so adding Intel later is a CI-only change: a second publish, `clang -arch x86_64`, and either a second zip (updater picks by `RuntimeInformation.ProcessArchitecture`) or a `lipo`-merged universal bundle (about twice the size). Rejected for now because there is no Intel Mac to test on, GitHub's Intel macOS runners are being phased out, and macOS 26 is the last release for Intel Macs. Revisit if an Intel user asks. |
| **Volco saving per-app levels and reapplying them at launch** | Made unnecessary by the Hello slider sync (PR #68), which restores levels as soon as the controller connects (§4.4). |
| **DMG installer** | A zip serves both first install and the updater. |
| **Mac-native UI redesign** (menu-bar popover, SF typography) | Out of scope by D3; a separate UX effort if ever wanted. |

## 12. References

- **Apple:** [Capturing system audio with Core Audio taps](https://developer.apple.com/documentation/coreaudio/capturing-system-audio-with-core-audio-taps) (sample code and the `CATapDescription` / `AudioHardwareCreateProcessTap` API).
- **Open-source per-app mixers built on process taps** (the best guide for `libvolcotap`: tap creation, private aggregate devices, IO procs, permission handling):
  - [insidegui/AudioCap](https://github.com/insidegui/AudioCap): minimal process-tap capture sample.
  - [ericchiu-ca/AppVolume](https://github.com/ericchiu-ca/AppVolume)
  - [erdmncdr/Mikser](https://github.com/erdmncdr/Mikser)
  - [clement880101/audio-adjuster-mac](https://github.com/clement880101/audio-adjuster-mac): private tap + private aggregate on the current output + gain IO proc, the same shape as §4.4.
  - [minhdra/MacVolumeMixer](https://github.com/minhdra/MacVolumeMixer)
  - [Lokasta/fader](https://github.com/Lokasta/fader), [altuzar/sonicflow](https://github.com/altuzar/sonicflow), [tgkd/buried-anchor](https://github.com/tgkd/buried-anchor)
- **Avalonia on macOS:** [TrayIcon](https://docs.avaloniaui.net/docs/reference/controls/tray-icon), [hiding the Dock icon (`ShowInDock` / `LSUIElement`)](https://github.com/AvaloniaUI/Avalonia/discussions/13568), [v12 macOS tray icon adjustments (#19655)](https://github.com/avaloniaui/avalonia/issues/19655).

## 13. Open risks, and what the spike can change

Steps 4 and 5 of §10 are planned in detail only after the spike (§4.5) has run and its findings are written into this spec. Each risk names the section it can change.

| # | Risk | Settled by | If it goes the wrong way |
|---|---|---|---|
| R1 | A tap's replay cannot be bound to the device each app plays to | Spike item 1 | §4.4: one tap per (target, output device), grouped by `kAudioProcessPropertyDevices`. More taps, same native interface. |
| R2 | The System Audio Recording permission does not survive re-signing with the same self-signed identity | Spike item 2 | §7, §6: accept a re-prompt after each update and document it in the README; the banner (§3.5) already handles the denied state. |
| R3 | Latency higher than the 10–20 ms estimate | Spike item 3 | §4.6: smaller aggregate IO buffer; the measured figure goes in the docs regardless. |
| R4 | `mutedWhenTapped` affects processes other than the tapped ones | Spike item 4 | §4.4: rethink the Unmapped target (a global tap is the most exposed case). |
| R5 | Tap and output device run at different sample rates or drift apart, causing clicks over long sessions | Spike, extended run (about 30 min) | §4.4: enable drift compensation on the tap's sub-tap entry in the aggregate description, or resample in the IO proc. |
| R6 | Avalonia's macOS `TrayIcon` misbehaves (known reports of icons not rendering) | Step 3, on hardware | §5: own `NSStatusItem` through a small interop shim in `Volco.Mac`. |
| R7 | `responsibility_get_pid_responsible_for_pid` (undocumented libsystem function) is missing or changes | Step 4 | §4.3: the `proc_pidpath` → outermost `.app` fallback becomes the only path; WebKit GPU processes of Safari may then need a special case. |
| R8 | UDP/WebSocket listening triggers a Local Network or firewall prompt that blocks the connection | Step 3, on hardware | §3.5: a stronger hint on the Connection page, and the README. |
| R9 | Gatekeeper refuses a self-signed, non-notarized app more strictly than expected on the user's macOS version | Step 3, first install | §6: document `xattr -dr com.apple.quarantine /Applications/Volco.app` as the fallback. |
| R10 | The macOS CI runner cannot import the signing identity non-interactively | Step 3, CI | §6: `security set-key-partition-list` on the temporary keychain (standard fix). |

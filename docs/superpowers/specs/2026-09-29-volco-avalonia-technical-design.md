# Volco Avalonia Rewrite — Technical Design

**Date:** 2026-09-29
**Status:** Approved (design only — not implemented)
**Supersedes the implementation plan in:** `2026-09-26-volco-option-a-avalonia-design.md`
(that document remains the authority on *why* Avalonia; this one is the authority on
*how* and *in what order*)
**Depends on:** `2026-09-26-volco-audio-core-design.md` — already implemented and merged
(PR #41). Read it for the audio engine's internals; they are not repeated here.
**Behavioural authority for the OSD:** `2026-06-07-volume-osd-design.md` — what the OSD
shows. The condensed parity checklist is `2026-09-26-volco-option-a-avalonia-design.md` §5.4.

---

## 1. Where the repo actually is

`main` is ahead of the Option A spec. The shared audio core landed in PR #41:
`IAudioEngine`, `AudioEngine`, `DeviceRegistry`, `SessionRegistry`,
`DefaultDevicePolicy`, `ConfigMigrator` (schema v2), `ControllerStateReporter` and
`FakeAudioEngine` all exist and are tested. `svcl.exe` and `AudioManager` are gone.

Option A phases 1–4 are therefore **substantially complete inside the single project**.
What remains:

1. Turn the msbuild `<Compile Remove>` split into real project boundaries.
2. Extract the app's orchestration logic out of `App.xaml.cs`.
3. Replace the WinUI 3 UI and the XAML OSD with Avalonia.
4. Update CI and packaging.

## 2. Goals

1. **The UI and the OS-dependent engine are decoupled, and the build enforces it.**
   A future OS is added by adding one project and one CI job, editing no existing
   project.
2. **Every step leaves a working, shippable app.** The repo at any commit builds one
   `Volco.exe` that works.
3. **Each step is handed to a fresh agent** with a contract precise enough to execute
   without re-deriving this design.

Non-goals, unchanged from the Option A spec §10: shipping Linux or macOS, any wire
protocol or firmware change, replacing the update mechanism. Nothing in this document
adds Linux or macOS code, or scaffolding for it.

## 3. Decisions taken

| Question | Decision |
|---|---|
| UI cutover shape | **Hard cutover in one PR.** The Avalonia head lands and `src/Volco/` is deleted in the same PR. One exe at all times. |
| OSD surface | **Avalonia borderless window**, not a native Win32 + Direct2D overlay. See §5.3. |
| UX redesign | **Verbatim port first, redesign as a separate later PR.** The cutover PR is a pure framework swap. |
| Definition of done | Green CI (Linux tests + Windows publish) **plus** `Avalonia.Headless.XUnit` view tests **plus** a manual pass by the user against a per-step checklist. |
| Platform assembly granularity | **One head project per OS**, containing that OS's engine, platform seams, manifest, icon and entry point. Not a separate `Volco.Audio.Windows`. |

---

## 4. Architecture

### 4.1 Project topology

```
src/
  Volco.Core/       net8.0      No UI, no platform APIs. Builds and tests on Linux.
    Communication/              ControllerMessage, IControllerConnection,
                                ProtocolParser, ProtocolEncoder, SerialConnection,
                                UdpConnection, WebSocketConnection,
                                ControllerStateReporter
    Config/                     AppConfig, ConfigManager, ConfigMigrator,
                                DeviceRefConfig
    Input/                      ButtonPressDetector, SliderProcessor,
                                VolumeChangeNotification, Actions/
    Audio/                      IAudioEngine + DTOs. Interface only, no impl.
    Updates/                    BuildInfo, GitHubUpdater, UpdateInfo,
                                UpdateInstaller
    Diagnostics/                AppLog
    Runtime/                    VolcoRuntime                      [new, §4.3]

  Volco.App/        net8.0      Avalonia. No platform APIs, no P/Invoke.
    Views/                      *.axaml — five settings pages, shell, update window
    ViewModels/                 ported from src/Volco/UI/ViewModels/
    Controls/                   SettingsCard.axaml
    Styles/                     Colors, Tokens, Typography, Brushes
    Assets/                     volco.ico (tray)
    Osd/                        VolumeOsdWindow.axaml — portable       [§5.3]
    Platform/                   IWindowEffects — the one UI seam        [§5.2]
    CompositionRoot.cs

  Volco.Windows/    net8.0-windows10.0.22621.0   THE EXE
                                <AssemblyName>Volco</AssemblyName>
    Program.cs                  Main()
    Audio/                      AudioEngine, DeviceRegistry, SessionRegistry,
                                DefaultDevicePolicy
    Platform/                   Win32WindowEffects
    app.manifest                asInvoker, uiAccess=false, per-monitor DPI v2
    NativeMethods.txt           CsWin32 input
    volco.ico                   exe icon

tests/
  Volco.Core.Tests/ net8.0      the existing 15 files, retargeted, + VolcoRuntime tests
  Volco.App.Tests/  net8.0      Avalonia.Headless.XUnit view tests
```

Reference direction: `Volco.Windows → Volco.App → Volco.Core`, and
`Volco.Windows → Volco.Core`. Nothing references `Volco.Windows`.

### 4.2 The platform seams — there are exactly two

**`IAudioEngine`** (`Volco.Core/Audio/`) — already exists, unchanged in shape.

**`IWindowEffects`** (`Volco.App/Platform/`) — new, one method:

```csharp
public interface IWindowEffects
{
    /// <summary>Make the window ignore all pointer input, so clicks reach what is
    /// beneath it. A platform that has not implemented this yet may no-op: the OSD
    /// still works, it just swallows clicks.</summary>
    void MakeClickThrough(Window window);
}
```

It lives in `Volco.App` rather than `Volco.Core` deliberately: it is a *UI* platform
concern, and `Volco.Core` must have no concept of a window. `Volco.Windows` already
references `Volco.App`, so the head implements it with no new reference.

Three couplings the Option A spec expected and that turn out not to need a seam:

- **Thread marshalling.** `Microsoft.UI.Dispatching.DispatcherQueue` becomes
  Avalonia's own `Dispatcher.UIThread.Post`, available in `Volco.App`.
- **Serial port enumeration.** `SerialPort.GetPortNames()` is cross-platform in
  .NET 8.
- **Process enumeration for the slider picker.** Already goes through
  `IAudioEngine.GetSessions()`, not `Process.GetProcesses()`.

### 4.3 `VolcoRuntime` — the UI-facing API

Roughly 350 lines of `App.xaml.cs` are pure orchestration: none of it is UI and none
of it is Windows. It moves to `Volco.Core/Runtime/VolcoRuntime.cs`:

```csharp
public sealed class VolcoRuntime : IDisposable
{
    public VolcoRuntime(ConfigManager config, IAudioEngine audio, bool audioReady,
                        Func<ConnectionConfig, IControllerConnection?>? connectionFactory = null,
                        VolcoRuntimeOptions? options = null);
    public void Start();

    public IAudioEngine     Audio             { get; }
    public ConfigManager    Config            { get; }
    public bool             AudioEngineFailed { get; }
    public ConnectionStatus ConnectionStatus  { get; }

    public event EventHandler<VolumeChangeNotification>? VolumeChanged;
    public event EventHandler<SliderLevelEventArgs>?     SliderLevelChanged;
    public event EventHandler<ConnectionStatus>?         ConnectionStatusChanged;
    public event EventHandler<DevicesChangedEventArgs>?  DevicesChanged;
}
```

It owns everything today's `App.xaml.cs` owns that is not a window:

- transport construction, `Connect()`, and restart on connection-config change;
- `IControllerConnection.MessageReceived` routing to `SliderProcessor` /
  `ButtonPressDetector`;
- `ActionFactory` dispatch on detected button presses;
- the pending-mute-OSD correlation (`_pendingMuteOsd`, `ResolveExpectedMuteDeviceId`,
  `ShowMuteOsd` — the latter becomes a `VolumeChanged` raise);
- `PushControllerState` with `_pushStateLock` and the last-pushed-frames dedup;
- the post-connect re-push mitigations (`_postConnectTimer750`,
  `_postConnectTimer2500`, `_awaitingFirstMessageAfterConnect`).

Every type in the signature above is a `Volco.Core` type. **The UI never names a
platform type.** `audioReady` is what the platform engine's start-up returned (false →
no config migration, `AudioEngineFailed`); the connection factory and options exist so
tests can inject fake transports and short re-push delays.

Two consequences:

1. **`IVolumeOsd` does not exist.** The Option A spec §5.2 defines it; this design
   drops it. The runtime *publishes* `VolumeChanged`; the UI decides to render a
   pill. `Volco.Core` has no concept of an OSD, which is a cleaner dependency
   direction than an interface the core calls into.
2. **The runtime runs headless.** Constructed with `FakeAudioEngine`, the whole
   app — routing, LED pushes, mute correlation, connect-race handling — is driveable
   from Linux tests with no UI in the process. This is what de-risks the cutover PR:
   by the time the Avalonia shell arrives, the app's behaviour is already under test.

### 4.4 Cleaning the audio API

`AudioRole` (Console / Multimedia / Communications) is a Core Audio concept with no
analogue elsewhere, and it leaks through `IAudioEngine` today. Auditing every call
site outside the Windows engine shows it **never varies**: callers pass exactly one of
`(Render, Multimedia)` or `(Capture, Communications)`. The role is always implied by
the flow, never chosen. `AudioRole.Console` has no caller outside the engine at all.
`AudioDevice.IsDefaultConsole` / `IsDefaultMultimedia` / `IsDefaultCommunications`
have **no consumers anywhere**.

So the leak is removable, and removing it is a net simplification:

```csharp
// after
public enum AudioFlow { Render, Capture }

public readonly record struct DeviceRef(DeviceRefKind Kind, AudioFlow Flow, string Id)
{
    public static DeviceRef DefaultOutput() => new(DeviceRefKind.Default, AudioFlow.Render,  "");
    public static DeviceRef DefaultInput()  => new(DeviceRefKind.Default, AudioFlow.Capture, "");
    public static DeviceRef ById(string endpointId, AudioFlow flow = AudioFlow.Render);
    public static DeviceRef ByFriendlyName(string name, AudioFlow flow = AudioFlow.Render);
}

public sealed record AudioDevice(string Id, string FriendlyName, AudioFlow Flow, bool IsDefault);

AudioDevice? GetDefaultDevice(AudioFlow flow);   // role parameter gone
```

`AudioRole` becomes `internal` to `Volco.Windows`, which maps `Render → eMultimedia`
and `Capture → eCommunications` on read, and continues to write all three roles in
`SetDefaultDevice`. `AudioDevice.IsDefault` means "the device `GetDefaultDevice(flow)`
returns". No behaviour change.

`DeviceVolumeChangedEventArgs.IsOwnChange` **stays as-is.** "Did this process cause
this change?" is a generic question; only the mechanism (Core Audio's event-context
GUID) is Windows, and that is already inside the engine.

### 4.5 What does not fully decouple

Stated so it is on the record rather than discovered later:

- **`IWindowEffects` is irreducible.** Avalonia gives `WindowDecorations="None"`,
  `Topmost`, `ShowActivated="False"`, `ShowInTaskbar="False"`, `Background="Transparent"`
  + `TransparencyLevelHint`, and rounded corners from an ordinary rounded `Border`,
  all portably. It does not expose whole-window click-through: a transparent Avalonia
  window still swallows clicks, and `IsHitTestVisible` governs only hit-testing
  *within* a window. One method, one platform implementation each.
- **`app.manifest`, the `.ico`, and the single-file publish flags are per-head by
  nature.** That is the head project's job and not a leak.
- **`AudioSessionInfo.ImageName` assumes per-process audio sessions.** True on
  Windows and on PipeWire; not on macOS without a shipped virtual audio driver
  (shared spec §9). No action now — reshaping an interface against a backend nobody
  has written is speculative work.

---

## 5. Component notes

### 5.1 Avalonia version and bindings

Avalonia **12** (stable). Compiled bindings are **on by default** in 12, so `x:DataType`
is mandatory on every bound element and every `DataTemplate`; a missing one is a build
error, not a silent reflection fallback. `CommunityToolkit.Mvvm` is kept — it is
source-generator based and the existing `[ObservableProperty]` / `[RelayCommand]`
ViewModels port with near-zero edits.

### 5.2 Shell

- `Application.ShutdownMode = ShutdownMode.OnExplicitShutdown` — closing the settings
  window must not exit the app.
- Tray icon: Avalonia's `TrayIcon` with a `NativeMenu` (Open Settings / Check for
  Updates… / — / Exit). Windows requires a real `.ico`. Left-click is
  `TrayIcon.Command`; verify on Windows in stage 2a before relying on it.
- Settings window 720×600 default, `MinWidth`/`MinHeight` 680×560 — Avalonia supports
  these natively, which **closes the deferred TODO in `SettingsWindow.xaml.cs`**
  (WinUI 3 has no minimum-size API without a `WM_GETMINMAXINFO` subclass).
- Close → hide, not dispose (`e.Cancel = true; window.Hide();`), matching today's
  `AppWindow_Closing`. Keep the unsubscribe-on-real-shutdown leak avoidance.
- No `NavigationView` in Avalonia; the nav rail is a `ListBox` + `ContentControl`.
- `tk:SettingsCard` has no Avalonia equivalent — a local ~40-line `TemplatedControl`
  with `Header`, `Description`, `HeaderIcon` and a content slot.
- `AutoSuggestBox` → `AutoCompleteBox`; `NumberBox` → `NumericUpDown`;
  `Visibility` + `BoolToVisibilityConverter` → `IsVisible` bound to a bool, and the
  converter is **deleted**.
- **No Segoe Fluent Icons.** Every glyph becomes a `PathIcon` with inline vector
  data. A font dependency is the single UI decision that is genuinely painful to
  unwind later.

### 5.3 OSD

A borderless Avalonia window in `Volco.App/Osd/`, driven by
`VolcoRuntime.VolumeChanged`, kept alive hidden for the process lifetime.

- Click-through via `IWindowEffects.MakeClickThrough`, applied once the platform
  handle exists.
- Repaint only on value change; cap repaints at 60 Hz (the 20 ms slider coalescing
  already limits inbound events to ~50 Hz, but the OSD must not assume that).
- Auto-hide 1500 ms after the last update, then fade over ~250 ms via window opacity,
  then hide. No continuous render loop and no timer-driven repaint.
- Position bottom-centre of the **primary screen's working area**
  (`Screens.Primary.WorkingArea`, scaled by `Screens.Primary.Scaling`), 80 DIP above
  the bottom edge — parity with today, which in practice always lands on the primary
  monitor. Reposition **only on the hidden→visible transition**, which is what today's
  code correctly does to avoid flashing. Following the cursor's monitor is deliberately
  deferred: Avalonia has no portable global-cursor API, so it would add a second
  platform seam. It is a candidate for step 3.
- Per-monitor DPI awareness v2 must be declared in `app.manifest`; today's manifest
  lacks it.
- Behavioural parity per the Option A spec §5.4, itself drawn from
  `2026-06-07-volume-osd-design.md`: triggers, label rules
  from `BuildSliderLabel`, muted glyph and "Muted"/"On" in place of the percentage,
  never steals focus, never blocks clicks.

`VolumeChangeNotification` and `SliderProcessor.BuildSliderLabel` are already
platform-neutral and unit-tested. They are kept unchanged.

### 5.4 Version stamping — a trap to avoid

`BuildInfo.Read()` calls `Assembly.GetExecutingAssembly()`. Today `BuildInfo` and the
exe are the same assembly. After the split, `BuildInfo` lives in `Volco.Core`, so it
would read `Volco.Core.dll` — which carries no stamp. `IsRelease` would silently go
false and **the in-app updater would disappear from every release build.**

Fix: move the version block out of the csproj into a root `Directory.Build.props`, so
every project is stamped identically:

```xml
<Project>
  <PropertyGroup>
    <VolcoVersion Condition="'$(VolcoVersion)' == ''">0.0.0</VolcoVersion>
    <VolcoReleaseChannel Condition="'$(VolcoReleaseChannel)' == ''">local</VolcoReleaseChannel>
    <Version>$(VolcoVersion)</Version>
    <InformationalVersion>$(VolcoVersion)</InformationalVersion>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyMetadata Include="ReleaseChannel" Value="$(VolcoReleaseChannel)" />
  </ItemGroup>
</Project>
```

`-p:VolcoVersion=…` on `dotnet publish` propagates through `ProjectReference`, so both
workflows keep working with no code change. `Assembly.GetEntryAssembly()` is *not* the
fix — it returns the test host under xUnit.

---

## 6. Implementation steps

Three steps. Each is one PR, given to a fresh agent, and leaves a working app.

### Step 1 — Decompose. WinUI still ships. Zero behaviour change.

Topology at the end of this step. `Volco.Windows` is created here as a **class
library**, so the platform boundary is CI-proven before any UI churn:

```
Volco.Core/   net8.0            Volco.Windows/  net8.0-windows  (library)
Volco/        net8.0-windows    WinUI exe → Volco.Core + Volco.Windows
```

| Stage | Content |
|---|---|
| 1a | Create `Volco.Core`. Move `Communication/`, `Config/`, `Input/`, `Updates/`, `AppLog`, and `Audio/` interfaces + DTOs. Namespaces `Volco.*` → `Volco.Core.*`. |
| 1b | Create `Volco.Windows` class library. Move `Audio/Windows/**`, `NativeMethods.txt`, and the CsWin32 `PackageReference`. |
| 1c | Clean the audio API per §4.4. Update all call sites and tests. |
| 1d | Extract `VolcoRuntime` per §4.3. `App.xaml.cs` drops to roughly 60 lines: config, engine, runtime, tray, windows. |
| 1e | `Volco.Core.Tests`: retarget the existing suite (14 test files plus the `FakeAudioEngine` helper). **Add `VolcoRuntime` tests** against `FakeAudioEngine` — message routing, mute-OSD correlation, `PushControllerState` dedup, post-connect re-push. |
| 1f | Root `Directory.Build.props` per §5.4. Update `Volco.sln`. CI test job points at `Volco.Core.Tests`. |

No Avalonia in this step, so no headless view tests yet.

**Done when:** both CI jobs green; published artifact is still a single `Volco.exe`
named `Volco-win-x64`; manual pass is a **full regression of current behaviour** —
this step touches the audio API and moves all orchestration, so it carries the real
behavioural risk of the whole project.

### Step 2 — Avalonia cutover. One PR.

This step cannot be split: with a hard cutover there is no way to ship half a settings
UI. It gets internal gates instead of subdivision.

| Stage | Content |
|---|---|
| 2a | **Spike first, throwaway.** A minimal Avalonia head proving (i) `TrayIcon.Command` left-click on Windows and (ii) `MakeClickThrough` via `WS_EX_TRANSPARENT`. Published as a CI artifact for the user to run. These are the only two unknowns in the step — settle them before writing 1,500 lines of XAML. |
| 2b | `Volco.App`: `Application` subclass, `ShutdownMode.OnExplicitShutdown`, tray icon + `NativeMenu`, settings window shell, nav rail, close→hide. Per §5.2. |
| 2c | `Volco.Windows` gains `Program.Main`, `<AssemblyName>Volco</AssemblyName>`, `app.manifest` **with per-monitor DPI v2**, exe icon, `Win32WindowEffects`. |
| 2d | Port `Styles/*.xaml`. Local `SettingsCard` `TemplatedControl`. `PathIcon` + inline vectors replacing every Segoe Fluent glyph. |
| 2e | Port the five pages one at a time (Connection, Sliders, Buttons, Output Devices, General). ViewModels move near-verbatim: `DispatcherQueue` → `Dispatcher.UIThread.Post`, delete `BoolToVisibilityConverter`, `x:DataType` everywhere. |
| 2f | `Osd/VolumeOsdWindow.axaml` per §5.3. |
| 2g | Port `UpdateWindow`. |
| 2h | `Volco.App.Tests` with `Avalonia.Headless.XUnit` — Sliders and Buttons pages at minimum. |
| 2i | CI: drop `microsoft/setup-msbuild`, replace `msbuild` with `dotnet publish` in **both** `build.yml` and `release.yml`. **Delete `src/Volco/`.** |
| 2j | Measurement run recorded in `MEASUREMENTS.md` against the `main` baseline, per shared spec §8. |

**Done when:** both CI jobs green; headless view tests green on Linux;
`MEASUREMENTS.md` updated; manual checklist passes — see §8.

### Step 3 — UX redesign. Touches `Volco.App` only.

An expert reviewer audits the five ported pages critically, proposes changes with
reasoning, the user approves, implementation follows.

**Constraint that makes this a test rather than just a task: this PR may not modify
`Volco.Core` or `Volco.Windows`.** If it has to, the decoupling failed, and the
diagnosis is exactly where.

### Optional, off the main line

NativeAOT. Needs a source-generated `JsonSerializerContext` for `ConfigManager`
(currently reflection-based), `System.IO.Ports` AOT verification, and Avalonia trimming
descriptors. Only after the app ships; the single-file self-contained build stays the
shipping default until AOT is proven.

---

## 7. CI and packaging

**Test job (`ubuntu-latest`)** — two explicit project paths, never the solution
(`Volco.Windows` cannot restore on Linux):

```yaml
- name: Test
  run: |
    dotnet test tests/Volco.Core.Tests/Volco.Core.Tests.csproj --configuration Release
    dotnet test tests/Volco.App.Tests/Volco.App.Tests.csproj  --configuration Release
```

This job **is** the decoupling enforcement, in two parts. Neither test project references
`Volco.Windows`, so a WinRT or Windows SDK projection landing in `Volco.App` or `Volco.Core`
fails the Linux build. `[DllImport]`, `LibraryImport` and CsWin32 all compile on Linux, so
`tests/Volco.App.Tests/ArchitectureTests.cs` reflects over both assemblies and fails on any
P/Invoke method or Windows assembly reference. Goal 1 is expressed as a build rule, not a
convention.

**Build job (`windows-latest`)** — `microsoft/setup-msbuild` is removed along with the
Windows App SDK restore:

```
dotnet publish src/Volco.Windows/Volco.Windows.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

`IncludeNativeLibrariesForSelfExtract` becomes load-bearing: it carries Avalonia's
`libSkiaSharp` and `libHarfBuzzSharp` into the single file.

**Contracts preserved unchanged:** output file `Volco.exe`, artifact name
`Volco-win-x64`, `GitHubUpdater.AssetName`, `UpdateInstaller.ApplyAndRestart`'s
`move /Y` over the running exe. Job-triggering rules per `CLAUDE.md` are untouched:
tests on all branches, build and publish only on `main` or an explicit
`workflow_dispatch` with `run_build=true`.

**Adding an OS later** is exactly: one `Volco.<Os>` head project implementing
`IAudioEngine` and `IWindowEffects`, plus one CI job. No existing project is edited.

---

## 8. Acceptance criteria

1. Every step: Linux test job green, Windows publish job green, downloadable
   `Volco-win-x64` artifact, and a written manual checklist for that step.
2. From step 2: `Avalonia.Headless.XUnit` view tests for Sliders and Buttons, green on
   Linux.
3. `Volco.App` contains no `[DllImport]` and no `Microsoft.UI.*` / `Windows.*`
   reference; `Volco.Core` contains neither those nor any UI-toolkit reference.
   Enforced by the Linux job, not by review: the Linux compile (WinRT and Windows SDK
   projections) plus `ArchitectureTests` (P/Invoke and Windows assembly references; these
   compile on Linux, so compiling alone does not catch them).
4. All 14 existing test files pass, plus new `VolcoRuntime` tests.
5. Idle CPU average < 0.1 %; idle GPU 0; handle count flat across a 60 s sweep. Cold
   start to tray icon < 1 s. Recorded in `MEASUREMENTS.md`.
6. Manual Windows checklist at step 2: tray icon left-click and right-click menu; OSD
   appears bottom-centre, is click-through, and is correctly scaled on a second
   monitor at a different DPI; all five settings pages functional; a v1 `config.json`
   migrates silently with a `config.v1.bak` written; ESP32 firmware and
   `tools/simulator/index.html` work unchanged including LED feedback (`0x03`, `0x04`);
   in-app update from a published release works end-to-end.
7. No `svcl.exe`, no bundled third-party executables, no Windows App SDK.
8. README updated: tech stack, architecture diagram, build instructions.

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| `BuildInfo` silently loses its stamp across the split, killing the updater | §5.4. Verify `DisplayVersion` in a release-stamped build during step 1's manual pass. |
| Avalonia `TrayIcon` left-click behaviour on Windows | Stage 2a spike, before any page porting. Fallback: `Shell_NotifyIcon` P/Invoke in `Volco.Windows`. |
| Whole-window click-through is not portable | Known and accepted: `IWindowEffects`, one method. Spiked in 2a. |
| Step 2 is large and atomic | Internal gates: the 2a spike, then pages ported one at a time. The behavioural risk is already retired in step 1, because `VolcoRuntime` is tested headless before the UI exists. |
| No local Windows build (per `CLAUDE.md`) | Every step is CI-verifiable; manual checks are batched at step ends against the published artifact. |
| Avalonia 12 compiled-bindings default breaks ported XAML | It surfaces as a build error, not a runtime one. `x:DataType` everywhere is a porting rule, applied per page in 2e. |
| Single-file publish drops Avalonia's native libs | `IncludeNativeLibrariesForSelfExtract=true`; verified by running the artifact at the end of step 2. |

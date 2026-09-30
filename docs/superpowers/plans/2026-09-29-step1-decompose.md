# Step 1 — Decompose: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the single `src/Volco` project into `Volco.Core` + `Volco.Windows` (library) + the existing WinUI exe, clean the public audio API, and extract all orchestration into a headless, tested `VolcoRuntime` — with **zero user-visible behaviour change**.

**Architecture:** `Volco.Core` (net8.0) holds everything platform-neutral including the new `VolcoRuntime`. `Volco.Windows` (net8.0-windows class library) holds the COM audio engine. The WinUI project `src/Volco` stays the shipping exe and shrinks to a thin shell over `VolcoRuntime`. Step 2 later replaces that shell with Avalonia; nothing built here is throwaway.

**Tech Stack:** .NET 8, C#, xUnit 2.7, CsWin32 0.3.335, WinUI 3 (unchanged, still shipping).

**Spec:** `docs/superpowers/specs/2026-09-29-volco-avalonia-technical-design.md` — read §1–§4 and §5.4 before starting. Section numbers below refer to it.

**Level of detail:** deliberately above code level. Each task fixes the files, the exact interfaces other tasks depend on, the tests that must exist, and how to verify. Implementation bodies are the implementer's call, provided the interfaces match exactly.

## Global Constraints

- **Do not run `dotnet` locally.** Verify through CI only (`CLAUDE.md`): push the branch, then `gh workflow run build.yml --ref <branch>` for tests; `gh run list --branch <branch> --limit 3` to watch. **Never** pass `--field run_build=true` unless the user asks. Pushing a non-`main` branch runs tests automatically.
- Git in this worktree: use `/usr/bin/git`, and write commit messages to a scratch file with `git commit -F` (no heredocs).
- Branch: `step1-decompose` off `main`. One PR at the end.
- Target frameworks: `Volco.Core` and `Volco.Core.Tests` = `net8.0` **unconditionally**; `Volco.Windows` = `net8.0-windows10.0.22621.0`, `<Platforms>x64</Platforms>`, `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`.
- Namespaces: `Volco.*` → `Volco.Core.*` for everything moved into `Volco.Core` (e.g. `Volco.Audio` → `Volco.Core.Audio`, `AppLog` → `Volco.Core.Diagnostics.AppLog`). Engine code moves to `Volco.Windows.Audio`.
- **Wire protocol frozen:** `0xAA | length | payload | XOR`; types `0x01`–`0x04`. No firmware or `tools/simulator` changes.
- **The shipped artifact must not change shape:** single-file `Volco.exe`, CI artifact name `Volco-win-x64`, publish flags unchanged in this step. `build.yml`'s `build` job and `release.yml` keep invoking `msbuild src/Volco/Volco.csproj` exactly as today.
- Keep `%APPDATA%/Volco/config.json` and `volco.log` paths unchanged.
- Move files with `git mv` so history follows.

## Review Focus

Failure modes the spec implies that ordinary "does it compile / do old tests pass" checks miss. Each has a pinning test in the owning task.

1. **Release builds silently lose the updater** because `BuildInfo` now reads `Volco.Core.dll`, which is unstamped → pinned in Task 1 (`BuildInfoStampTests`).
2. **Settings page shows a stale "Disconnected" after a connection-config change**, because the UI subscribed to the *old* transport's `StatusChanged` → pinned in Task 4 (`Restart_ForwardsStatusFromNewTransportOnly`).
3. **A mute press against an unplugged device leaves a dangling pending OSD** that pops later for an unrelated device's volume change → pinned in Task 4 (`MutePress_UnresolvableDevice_NeverShowsOsd`).
4. **Audio engine failed to start, yet the v1→v2 config migration runs against an empty device list and destroys device mappings** → pinned in Task 4 (`Start_AudioNotReady_SkipsMigration`).
5. **Dragging a device-mapped slider floods the controller** with identical LED frames (≈50/s blocking serial writes) → pinned in Task 4 (`OwnVolumeChanges_DoNotResendIdenticalFrames`).

---

## File structure after this step

```
Directory.Build.props                       NEW  version stamping (§5.4)
Volco.sln                                   MOD  + Volco.Core, Volco.Windows, Volco.Core.Tests; − Volco.Tests
src/Volco.Core/Volco.Core.csproj            NEW
src/Volco.Core/{Communication,Config,Input,Updates,Audio}/**   MOVED from src/Volco/
src/Volco.Core/Diagnostics/AppLog.cs        MOVED from src/Volco/AppLog.cs, now public
src/Volco.Core/Runtime/VolcoRuntime.cs      NEW  (Task 4)
src/Volco.Core/Runtime/VolcoRuntimeOptions.cs NEW (Task 4)
src/Volco.Windows/Volco.Windows.csproj      NEW  class library
src/Volco.Windows/Audio/{AudioEngine,DeviceRegistry,SessionRegistry,DefaultDevicePolicy}.cs  MOVED
src/Volco.Windows/NativeMethods.txt         MOVED
src/Volco/                                  STAYS the WinUI exe: App.xaml(.cs), UI/**, app.manifest
tests/Volco.Core.Tests/                     RENAMED from tests/Volco.Tests/
.github/workflows/build.yml                 MOD  test job path only
```

---

### Task 1: Create `Volco.Core` and move the platform-neutral code

**Files:**
- Create: `Directory.Build.props`, `src/Volco.Core/Volco.Core.csproj`
- Move (`git mv`): `src/Volco/{Communication,Config,Input,Updates}/**` → `src/Volco.Core/…`; `src/Volco/Audio/{IAudioEngine,AudioEvents,AudioPrimitives}.cs` → `src/Volco.Core/Audio/`; `src/Volco/AppLog.cs` → `src/Volco.Core/Diagnostics/AppLog.cs`
- Move: `tests/Volco.Tests/` → `tests/Volco.Core.Tests/` (rename csproj to `Volco.Core.Tests.csproj`)
- Modify: `src/Volco/Volco.csproj`, `Volco.sln`, `.github/workflows/build.yml` (test job only)
- Create test: `tests/Volco.Core.Tests/Updates/BuildInfoStampTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: assembly `Volco.Core` with namespaces `Volco.Core.{Communication,Config,Input,Input.Actions,Updates,Audio,Diagnostics}`. `AppLog` is **`public static class`** (it is `internal` today; three assemblies now call it). `Volco.Core` declares `[assembly: InternalsVisibleTo("Volco.Core.Tests")]` and, **temporarily**, `InternalsVisibleTo("Volco")` because `App.xaml.cs` still calls `internal` `ActionFactory.Resolve` until Task 5 removes that.

**Requirements:**
- `Directory.Build.props` contains exactly the block in spec §5.4. Delete the equivalent `PropertyGroup`/`ItemGroup` from `src/Volco/Volco.csproj` (lines 10–23 today) so there is one definition.
- `Volco.Core.csproj`: `net8.0`, `Nullable` and `ImplicitUsings` enabled, `PackageReference System.IO.Ports 8.0.0`. No other package references unless a moved file needs one (check `ConfigManager`/`GitHubUpdater` usings).
- `src/Volco/Volco.csproj`: make it **Windows-only** — keep the Windows `PropertyGroup`/package `ItemGroup` unconditional, delete the non-Windows `PropertyGroup` and the four `<Compile Remove>` rules (nothing builds this project on Linux any more). Add `ProjectReference` to `..\Volco.Core\Volco.Core.csproj`. Drop `System.IO.Ports` if it is now only transitively needed.
- `Volco.Core.Tests.csproj`: `net8.0` unconditionally (delete both OS-conditional `PropertyGroup`s and `Platforms`/`RuntimeIdentifiers`/`UseRidGraph`), references `..\..\src\Volco.Core\Volco.Core.csproj` only. Update every test file's `using`/`namespace` to `Volco.Core.*`.
- `build.yml` test step: `dotnet test tests/Volco.Core.Tests/Volco.Core.Tests.csproj --configuration Release`. Touch nothing else in the workflow.
- `Volco.sln`: replace `Volco.Tests` with `Volco.Core.Tests`, add `Volco.Core`. Keep existing GUIDs for kept projects.

- [ ] **Step 1: Write `BuildInfoStampTests`** — pins Review Focus #1. `Assert.Contains(typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(), a => a.Key == "ReleaseChannel")` and `Assert.Equal("local", BuildInfo.Channel)`. It fails if `Directory.Build.props` is missing or does not reach `Volco.Core`.
- [ ] **Step 2: Create `Directory.Build.props` and `Volco.Core.csproj`; `git mv` the files; fix namespaces and usings across `src/Volco.Core`, `src/Volco` (UI, `App.xaml.cs`, remaining `Audio/Windows/**`) and tests.** `App.xaml` has no `Volco.*` xmlns to change, but check `UI/*.xaml` for `using:Volco.` xmlns entries and update them.
- [ ] **Step 3: Update the csprojs, sln and test-job path as above.**
- [ ] **Step 4: Commit, push, and confirm the test job is green** (`gh run list --branch step1-decompose --limit 3`). The Windows `build` job does not run on a branch push. Run it once at the end of the step (Task 6), not per task.

---

### Task 2: Create `Volco.Windows` and move the COM engine into it

**Files:**
- Create: `src/Volco.Windows/Volco.Windows.csproj`
- Move: `src/Volco/Audio/Windows/*.cs` → `src/Volco.Windows/Audio/`; `src/Volco/NativeMethods.txt` → `src/Volco.Windows/NativeMethods.txt`
- Modify: `src/Volco/Volco.csproj`, `Volco.sln`

**Interfaces:**
- Consumes: `Volco.Core.Audio.IAudioEngine` and DTOs (Task 1).
- Produces: `public sealed class Volco.Windows.Audio.AudioEngine : IAudioEngine` with `public bool Start()` — same behaviour as today. `DeviceRegistry`, `SessionRegistry` and `DefaultDevicePolicy` become `internal` unless the WinUI project references them (it should not; only `AudioEngine` is constructed in `App.xaml.cs`).

**Requirements:**
- `Volco.Windows.csproj`: class library, TFM/Platforms/RIDs per Global Constraints, `Nullable`/`ImplicitUsings` enabled, `ProjectReference` → `Volco.Core`, and the `Microsoft.Windows.CsWin32 0.3.335` `PackageReference` block copied verbatim from today's `Volco.csproj` (with its `PrivateAssets`/`IncludeAssets`). Add `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` only if the build demands it.
- Remove CsWin32 from `src/Volco/Volco.csproj`. The only remaining interop there is `VolumeOsdWindow.xaml.cs`, which uses hand-written `[DllImport]` (verified), not CsWin32. If the build nevertheless fails on a `Windows.Win32.*` type in `src/Volco`, that code belongs in `Volco.Windows`.
- `src/Volco/Volco.csproj` gains `ProjectReference` → `..\Volco.Windows\Volco.Windows.csproj`.
- `App.xaml.cs`: `new Volco.Audio.Windows.AudioEngine()` → `new Volco.Windows.Audio.AudioEngine()`.

- [ ] **Step 1: Create the project, `git mv` the four engine files and `NativeMethods.txt`, fix namespaces.**
- [ ] **Step 2: Update references and the sln.**
- [ ] **Step 3: Commit and push.** Linux CI cannot compile `Volco.Windows`, so the test job only proves Core is unaffected. The Windows compile is proven in Task 6's `run_build` dispatch. Note in the commit message that this task is not yet Windows-verified.

---

### Task 3: Remove Core Audio concepts from the public audio API (spec §4.4)

**Files:**
- Modify: `src/Volco.Core/Audio/AudioPrimitives.cs`, `src/Volco.Core/Audio/IAudioEngine.cs`
- Modify (call sites): `src/Volco.Core/Communication/ControllerStateReporter.cs`, `src/Volco.Core/Input/SliderProcessor.cs`, `src/Volco.Core/Input/Actions/{MuteCurrentInputAction,MuteCurrentOutputAction,ToggleOutputDeviceAction}.cs`, `src/Volco/App.xaml.cs`, anything under `src/Volco/UI/` that constructs `DeviceRef.Default(...)` or `AudioDevice(...)`
- Modify (engine): `src/Volco.Windows/Audio/{AudioEngine,DeviceRegistry}.cs`
- Modify tests: `tests/Volco.Core.Tests/Audio/{AudioPrimitivesTests,FakeAudioEngine,FakeAudioEngineTests}.cs`, plus every test that builds an `AudioDevice` or calls `GetDefaultDevice`

**Interfaces — Produces (exact, later tasks and Step 2 depend on these):**

```csharp
namespace Volco.Core.Audio;

public enum AudioFlow { Render, Capture }
public enum DeviceRefKind { Default, ById, ByFriendlyName }

public readonly record struct DeviceRef(DeviceRefKind Kind, AudioFlow Flow, string Id)
{
    public static DeviceRef DefaultOutput();                                   // (Default, Render, "")
    public static DeviceRef DefaultInput();                                    // (Default, Capture, "")
    public static DeviceRef ById(string endpointId, AudioFlow flow = AudioFlow.Render);
    public static DeviceRef ByFriendlyName(string friendlyName, AudioFlow flow = AudioFlow.Render);
}

public sealed record AudioDevice(string Id, string FriendlyName, AudioFlow Flow, bool IsDefault);

// IAudioEngine: only this member changes
AudioDevice? GetDefaultDevice(AudioFlow flow);
```

`AudioRole` is deleted from `Volco.Core`. `Volco.Windows` gets an `internal enum AudioRole { Console, Multimedia, Communications }` in `Volco.Windows.Audio`, used only by `DeviceRegistry`'s default cache.

**Mapping rule (engine-internal, must be documented in a comment on `AudioEngine.GetDefaultDevice`):** `Render → Multimedia`, `Capture → Communications`. `AudioDevice.IsDefault` is true iff the device is the cached default for its flow's mapped role. `SetDefaultDevice` still writes all three roles. `DeviceRef` resolution for `Kind == Default` uses the same mapping. `DevicesChanged(DefaultChanged)` must still fire only for the mapped role's changes, not for Console-only changes, so a Console-role-only switch does not push a spurious active-device LED update. Check `DeviceRegistry.ApplyDefaultChanged` and filter there.

**Call-site substitutions** (every one found by the audit in spec §4.4):
`DeviceRef.Default(AudioFlow.Render, AudioRole.Multimedia)` → `DeviceRef.DefaultOutput()`;
`DeviceRef.Default(AudioFlow.Capture, AudioRole.Communications)` → `DeviceRef.DefaultInput()`;
`GetDefaultDevice(AudioFlow.Render, AudioRole.Multimedia)` → `GetDefaultDevice(AudioFlow.Render)`;
`GetDefaultDevice(AudioFlow.Capture, AudioRole.Communications)` → `GetDefaultDevice(AudioFlow.Capture)`.
Any other combination found means the audit was wrong. Stop and report it rather than inventing a mapping.

- [ ] **Step 1: Update `AudioPrimitivesTests` first** — `DefaultOutput()` yields `(Default, Render, "")`; `DefaultInput()` yields `(Default, Capture, "")`; `ById`/`ByFriendlyName` unchanged. Add `FakeAudioEngineTests.GetDefaultDevice_ReturnsPerFlowDefault`: set `DefaultRender`/`DefaultCapture`, assert each flow returns its own.
- [ ] **Step 2: Change the primitives and interface; update `FakeAudioEngine`** (`GetDefaultDevice(AudioFlow flow) => flow == Render ? DefaultRender : DefaultCapture`; its `TryGetDeviceState` key for `Kind == Default` becomes the flow alone).
- [ ] **Step 3: Update all call sites and every test constructing `AudioDevice` (4 args now).**
- [ ] **Step 4: Update the Windows engine per the mapping rule.**
- [ ] **Step 5: Verify by search, then commit and push.** `grep -rn "AudioRole" src/Volco.Core src/Volco tests` must return nothing; `grep -rn "IsDefaultConsole\|IsDefaultMultimedia\|IsDefaultCommunications" src tests` must return nothing. Test job green.

---

### Task 4: Extract `VolcoRuntime` into `Volco.Core` (spec §4.3), with tests

**Files:**
- Create: `src/Volco.Core/Runtime/VolcoRuntime.cs`, `src/Volco.Core/Runtime/VolcoRuntimeOptions.cs`
- Modify: `src/Volco.Core/Config/ConfigManager.cs` (add `public string ConfigPath => _configPath;`)
- Create tests: `tests/Volco.Core.Tests/Runtime/VolcoRuntimeTests.cs`, `tests/Volco.Core.Tests/Runtime/FakeControllerConnection.cs`

**Interfaces:**
- Consumes: Task 3's audio API; existing `ConfigManager`, `ConfigMigrator`, `SliderProcessor`, `ButtonPressDetector`, `ActionFactory`, `ControllerStateReporter`, `IControllerConnection`, `SerialConnection`/`UdpConnection`/`WebSocketConnection`.
- Produces (exact — Task 5 and all of Step 2 bind to this):

```csharp
namespace Volco.Core.Runtime;

public sealed record VolcoRuntimeOptions(IReadOnlyList<TimeSpan> PostConnectRepushDelays)
{
    public static VolcoRuntimeOptions Default { get; } =
        new(new[] { TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(2500) });
}

public sealed class VolcoRuntime : IDisposable
{
    /// <param name="audioReady">What the platform engine's Start() returned. False → no config
    /// migration, AudioEngineFailed = true; everything else still starts.</param>
    /// <param name="connectionFactory">Null → the real Serial/Udp/WebSocket switch. Tests inject fakes.</param>
    public VolcoRuntime(ConfigManager config, IAudioEngine audio, bool audioReady,
                        Func<ConnectionConfig, IControllerConnection?>? connectionFactory = null,
                        VolcoRuntimeOptions? options = null);

    /// Migrates v1→v2 config if needed and audioReady; subscribes to the engine; creates
    /// SliderProcessor + ButtonPressDetector; starts the transport. Call once.
    public void Start();

    public IAudioEngine     Audio             { get; }
    public ConfigManager    Config            { get; }
    public bool             AudioEngineFailed { get; }
    public ConnectionStatus ConnectionStatus  { get; }   // Disconnected when no transport

    // All events may be raised on ANY thread (audio thread, transport threads, timer threads).
    // Subscribers marshal to their UI thread themselves.
    public event EventHandler<VolumeChangeNotification>? VolumeChanged;           // slider OSD + mute OSD
    public event EventHandler<SliderLevelEventArgs>?     SliderLevelChanged;
    public event EventHandler<ConnectionStatus>?         ConnectionStatusChanged; // survives transport restarts
    public event EventHandler<DevicesChangedEventArgs>?  DevicesChanged;          // every kind, forwarded

    /// Disposes the transport, SliderProcessor and pending timers and unsubscribes from the
    /// engine. Does NOT dispose the IAudioEngine: the head that created it owns it.
    public void Dispose();
}
```

**What moves in**, verbatim in logic, from `src/Volco/App.xaml.cs`: `PendingMuteOsd`, `_pendingMuteOsd`, `_pushStateLock`, `_lastPushedFrames`, `_postConnectLock`, both post-connect timers (now one timer per entry in `PostConnectRepushDelays`), `_awaitingFirstMessageAfterConnect`, the migration block from `OnLaunched`, `StartConnection`, `OnConnectionStatusChanged`, `ArmFirstMessagePush`, `ScheduleTimedRepushes`, `OnMessageReceived`, `OnButtonPressDetected`, `IsMuteAction`, `ResolveExpectedMuteDeviceId`, `ResolveSpecificDeviceId`, `OnDeviceVolumeChanged`, `OnDevicesChangedForControllerState`, `ShowMuteOsd` (now raises `VolumeChanged` instead of calling the OSD), `PushControllerState`, `FramesEqual`, `OnConfigChanged`. **Keep the explanatory comments**: they record real bugs (review items, Bug 1–4, the connect-race fix). Only update references that no longer apply, such as "App" → "the runtime".

`SliderProcessor.VolumeChanged` and `.SliderLevelChanged` are re-raised as the runtime's own events. `ConnectionStatusChanged` is raised from a handler the runtime attaches to **each** transport it creates and detaches on restart. That is the fix for Review Focus #2. `Config.ConnectionConfigChanged` triggers the restart as today.

**Tests** (`FakeAudioEngine` + `FakeControllerConnection`, where the fake records `Send` frames, exposes `RaiseMessage(ControllerMessage)` and `SetStatus(ConnectionStatus)`, and counts `Dispose`). Use a temp-dir `ConfigManager`. Use `VolcoRuntimeOptions` with short delays, such as 20 ms and 60 ms, for timer tests. Required tests:

| Test | Asserts |
|---|---|
| `SliderMessage_RoutesToSliderProcessor` | a `SliderMessage` for a Device-mapped slider eventually produces a `SetDeviceVolume` call on the fake engine |
| `ButtonPress_MuteCurrentOutput_TogglesDefaultOutput` | press/release → `ToggleMuteCalls` contains `DeviceRef.DefaultOutput()` |
| `MutePress_OwnChangeOnTargetDevice_RaisesMuteOsd` | after press, `RaiseDeviceVolumeChanged(defaultId, …, muted: true, isOwnChange: true)` → one `VolumeChanged` with `Kind == OsdKind.Mute`, `Muted == true` |
| `MutePress_ExternalChange_DoesNotRaiseOsd` | same, but `isOwnChange: false` → no `VolumeChanged` |
| `MutePress_UnresolvableDevice_NeverShowsOsd` | Review Focus #3: `MuteSpecificDevice` with a name no device has; press; then an own-change on some *other* device → no `VolumeChanged` |
| `Connected_PushesStateImmediately` | `SetStatus(Connected)` → at least the mute + active-device frames sent |
| `OwnVolumeChanges_DoNotResendIdenticalFrames` | Review Focus #5: after connect, 20 volume-only `DeviceVolumeChanged` raises with unchanged mute → no additional frames |
| `MuteChange_ResendsFrames` | a mute flip → new frames sent |
| `Connected_RepushesAfterEachConfiguredDelay` | with delays 20 ms and 60 ms, the forced push count rises twice after connect (poll with a bounded wait, max ~1 s, no fixed sleeps longer than needed) |
| `FirstMessageAfterConnect_ForcesOneRepush` | first inbound message → one extra push; second message → none |
| `Restart_ForwardsStatusFromNewTransportOnly` | Review Focus #2: change connection config → old fake disposed; `SetStatus` on the **old** fake raises nothing; on the **new** fake raises `ConnectionStatusChanged` |
| `Start_AudioNotReady_SkipsMigration` | Review Focus #4: v1 config on disk, `audioReady: false` → file untouched, no `config.v1.bak`, `AudioEngineFailed == true` |
| `Start_AudioReady_MigratesV1Config` | v1 config + devices → config now schema v2 and `config.v1.bak` exists |
| `DevicesChanged_IsForwardedForEveryKind` | raise `Added` and `DefaultChanged` → both forwarded |
| `Dispose_DisposesTransport_NotEngine` | transport `Dispose` count 1; fake engine not disposed; no events after dispose |

Use the existing `ConfigMigratorTests` fixtures for building a v1 config file rather than hand-writing JSON again.

- [ ] **Step 1: Add `ConfigManager.ConfigPath`, `FakeControllerConnection`, and the test file with all tests above; confirm they fail to compile or fail.**
- [ ] **Step 2: Implement `VolcoRuntimeOptions` and `VolcoRuntime` by moving the logic listed above.** Do not delete it from `App.xaml.cs` yet. Task 5 does that, so this commit leaves the app unchanged.
- [ ] **Step 3: Push; all runtime tests green; existing tests still green.**
- [ ] **Step 4: Commit.**

---

### Task 5: Make the WinUI shell a thin view over `VolcoRuntime`

**Files:**
- Modify: `src/Volco/App.xaml.cs`, `src/Volco/UI/SettingsWindow.xaml.cs`, `src/Volco/UI/ViewModels/{SettingsViewModel,ConnectionViewModel}.cs`, `src/Volco/UI/VolumeOsdWindow.xaml.cs` (only if a signature changes)
- Modify: `src/Volco.Core/AssemblyInfo` / wherever Task 1 put `InternalsVisibleTo("Volco")`: **remove it**

**Interfaces:**
- Consumes: `VolcoRuntime` exactly as Task 4 produced it.
- Produces, for Step 2's ViewModel port:
  - `SettingsViewModel(ConfigManager config, IAudioEngine audio, VolcoRuntime runtime)`, replacing the `IControllerConnection?` parameter.
  - `ConnectionViewModel(ConfigManager config, VolcoRuntime runtime)`, which reads `runtime.ConnectionStatus` and subscribes to `runtime.ConnectionStatusChanged`. `UpdateConnection(...)` is **deleted** from both ViewModels and from `SettingsWindow`, because the runtime event survives restarts.
  - `SettingsWindow(VolcoRuntime runtime)`, which subscribes to `runtime.SliderLevelChanged` and `runtime.DevicesChanged` (→ `RefreshDevices`), with the same unsubscribe-on-real-exit logic as today.

**Resulting `App.xaml.cs` responsibilities, and nothing else:** create `ConfigManager` + `Load()`; create `Volco.Windows.Audio.AudioEngine` and call `Start()`; construct `VolcoRuntime(config, engine, ready)` and call `Start()`; create `VolumeOsdWindow` and subscribe it to `runtime.VolumeChanged`; tray icon (tooltip uses `runtime.AudioEngineFailed`); `ShowSettings`/`ShowUpdates`; if `AudioEngineFailed`, show settings on launch; `Shutdown` = dispose runtime, **then** dispose engine, close windows, dispose tray. Target size is roughly 150 lines, most of it tray code, down from about 600.

- [ ] **Step 1: Rewrite `App.xaml.cs` as above; delete every member that moved into the runtime.**
- [ ] **Step 2: Retarget `SettingsViewModel`, `ConnectionViewModel` and `SettingsWindow`.**
- [ ] **Step 3: Remove the temporary `InternalsVisibleTo("Volco")`.** If `src/Volco` no longer compiles without it, something that should have moved into the runtime did not.
- [ ] **Step 4: Verify by search, then commit and push.** `grep -n "PushControllerState\|_pendingMuteOsd\|ControllerStateReporter" src/Volco -r` returns nothing. Test job green.

---

### Task 6: Windows build verification, docs, manual checklist, PR

**Files:**
- Create: `docs/superpowers/checklists/2026-09-29-step1-manual-checklist.md`
- Modify: `CLAUDE.md` (Architecture section: project list, where `VolcoRuntime` lives, audio API without `AudioRole`; Build & Test: test project path), `README.md` (build instructions only, if they name paths)

- [ ] **Step 1: Trigger the Windows build once:** `gh workflow run build.yml --ref step1-decompose --field run_build=true`. This is the explicit exception the user authorised for step verification. Fix until green. Confirm the artifact is `Volco-win-x64` containing a single `Volco.exe`.
- [ ] **Step 2: Write the manual checklist.** It is a full regression, because this step changed the audio API and moved all orchestration. At minimum, each item with its expected result:
  1. Launch → tray icon appears; tooltip "Volco".
  2. Left-click tray → settings opens; close → hides; tray → reopens.
  3. Each slider mapping type (Device, Processes, Unmapped, default output) changes the right volume; OSD shows the right label.
  4. Each button action (mute output, mute input, mute specific device, set output device, toggle output device), single/double/long → correct effect; mute OSD shows the correct glyph and state.
  5. Controller mute LEDs track mute state, including a mute made from the Windows volume mixer; active-device LED tracks a default-device switch made in Windows Sound settings.
  6. Unplug and replug the ESP32 → reconnects; LEDs correct within ~3 s.
  7. Change connection type in settings → status in the Connection page updates for the new transport (Review Focus #2).
  8. Unplug a device mapped to a mute button, press it → no OSD, then no stray OSD on the next volume change elsewhere (Review Focus #3).
  9. Place a v1 `config.json` → silent migration, `config.v1.bak` written.
  10. Release-stamped build (from `release.yml` or a local `-p:VolcoReleaseChannel=release` publish via CI) → tray shows "Check for Updates…" (Review Focus #1).
  11. `tools/simulator/index.html` over WebSocket → sliders, buttons, LED feedback work.
- [ ] **Step 3: Update `CLAUDE.md`/`README.md`; commit; open the PR** titled "Step 1: split Volco.Core / Volco.Windows, extract VolcoRuntime". The body links the spec and the checklist and ends with the attribution lines. **Do not merge.** The user runs the checklist and merges.

---

## Done when

- Test job green; one `run_build=true` Windows build green; artifact shape unchanged.
- The five Review Focus tests exist and pass.
- `grep` checks in Tasks 3 and 5 are clean.
- The user has run the checklist and approved the PR.

## Hand-off to Step 2

Step 2's agent may rely on exactly these, and nothing else from this step:
`Volco.Core.Runtime.VolcoRuntime` + `VolcoRuntimeOptions` (Task 4 signatures);
`Volco.Windows.Audio.AudioEngine` with `bool Start()`;
the Task 3 audio API;
`SettingsViewModel(ConfigManager, IAudioEngine, VolcoRuntime)` and `ConnectionViewModel(ConfigManager, VolcoRuntime)`;
`public static class Volco.Core.Diagnostics.AppLog`;
`Directory.Build.props` stamping.

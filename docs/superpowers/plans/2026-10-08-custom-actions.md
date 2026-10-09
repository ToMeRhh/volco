# Custom Button Actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Buttons can control media (play/pause, next, previous, stop; current app or a chosen
one), open a program (or bring it to the front), and run a user's script, with state shown on the
controller's LEDs and screens; programs and scripts run only for presses that came over USB serial.

**Architecture:** Two new platform seams in `Volco.Core` (`IMediaSessions`, `IProgramLauncher`),
implemented in `Volco.Windows`; script running (`ScriptRunner`), the program-open policy
(`ProgramOpener`) and everything the display needs (`ActionStatus`) are platform-neutral Core code
tested on Linux. `ActionFactory` takes an `ActionServices` bundle. `ControllerDisplay` takes an
optional `IActionStatus`. The UI (`ActionCatalog`, `ActionSlotViewModel`, `ActionRow`) gains a
Media group and the Open a program / Run a script actions.

**Tech Stack:** C# / .NET 8, Avalonia 12, xUnit 2 (Core) and xUnit v3 headless (App),
`Microsoft.Extensions.TimeProvider.Testing`, WinRT `Windows.Media.Control` (Windows only),
Python + Pillow (`gen_assets.py`), PlatformIO native tests, Node tests (simulator).

**Spec:** `docs/design/custom-actions.md`

## Global Constraints

- `Volco.Core` and `Volco.App` stay platform-free: no `DllImport`/`LibraryImport`, no `Windows.*`, no CsWin32 (`ArchitectureTests` enforces it). `System.Diagnostics.Process` is allowed (not P/Invoke).
- No dotnet locally: verify by pushing the branch and running `gh workflow run build.yml --ref worktree-custom_actions` (tests only; never `run_build=true`). Use `/usr/bin/git`.
- Config schema v6 (`AppConfig.CurrentSchemaVersion = 6`); v3/v4/v5 files are stamped to 6 with a backup, no other migration (no backward compatibility).
- `OpenProgram` and `RunScript` execute only for presses that arrived over USB serial, or from the settings UI's Test button. Over WebSocket/UDP: refused and logged.
- Screen layout 1 convention (existing code): top line = state (`PLAYING`, `PAUSED`, `RUNNING`, `FAILED`), main = name (app, program or script). The spec's §3.3 table is corrected to this order in Task 10.
- Failure flash: `ControllerDisplay.AnnounceSteps` (6) steps of 250 ms; LED on in even steps.
- Program launch debounce: 5 s. Program poll: 2 s. Script output tail: last 20 lines.
- Icon ids are append-only: new icons are 11 play, 12 pause, 13 next, 14 previous, 15 app, 16 script; none offered on the Devices page.

## Review Focus

1. A script path or program path with spaces, or user arguments with quotes: the interpreter command line must quote the path (`cmd /d /s /c ""C:\a b\x.bat" args"`). Tested in Task 4.
2. A press over UDP/WebSocket of a program/script action, including one whose release is decided by the double-press timer after the transport changed: refused. Tested in Task 7 (trust is captured per button at the press frame).
3. A script that writes megabytes of output or never ends: only the last 20 lines are kept; never killed; Ignore policy keeps ignoring. Tested in Task 5.
4. A media target app that is not running / no current session: press is a no-op, display shows the name with blank state. Tested in Task 6/7.
5. Config edited (command path changed, action type changed) while a script runs: the run continues, its state stays on that action object; a type change to a non-script action shows the new action's display. Tested in Task 5/6.

---

### Task 1: Config schema v6

**Files:**
- Modify: `src/Volco.Core/Config/AppConfig.cs` (enum, `ButtonActionConfig`, new classes, `CurrentSchemaVersion`)
- Modify: `src/Volco.Core/Config/ConfigManager.cs` (stamp v5 → v6)
- Test: `tests/Volco.Core.Tests/Config/CustomActionConfigTests.cs`; update existing tests asserting schema 5.

**Produces:**
```csharp
public enum ButtonActionType { None, MuteSpecificDevice, MuteCurrentOutput, MuteCurrentInput, ToggleOutputDevice,
    SetOutputDevice, NextPreset, GoToPreset, MediaPlayPause, MediaNext, MediaPrevious, MediaStop, OpenProgram, RunScript }
public class MediaAppConfig { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
public class CommandConfig { public string Path { get; set; } = ""; public string Arguments { get; set; } = "";
                             public string WorkingDirectory { get; set; } = ""; public string Name { get; set; } = ""; }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum WhileRunningPolicy { Ignore, StartAnother }
// ButtonActionConfig gains: MediaAppConfig? MediaApp; CommandConfig? Command; WhileRunningPolicy WhileRunning = Ignore;
public static class ButtonActionTypes {
    public static bool IsMedia(ButtonActionType t);       // the four Media* values
    public static bool RunsCode(ButtonActionType t);      // OpenProgram, RunScript
    public static bool NeedsCommand(ButtonActionType t);  // same as RunsCode
}
```

- [ ] **Step 1: Failing test** — round trip a config with a `RunScript` action (`Command` with spaces in the path, `WhileRunning = StartAnother`) and a `MediaPlayPause` with `MediaApp {Id="Spotify.exe", Name="Spotify"}` through `ConfigManager.Save`/`Load`; assert fields survive and JSON contains `"whileRunning": "StartAnother"` and `"action": "RunScript"`. Second test: a v5 file on disk loads as v6 and leaves `config.v5.bak`.
- [ ] **Step 2: Implement** the enum values (appended), classes, `ButtonActionTypes`, version 6, and `else if (version is 3 or 4 or 5)` in `ConfigManager.Load`.
- [ ] **Step 3: Update** tests that assert `CurrentSchemaVersion == 5` or a v5 file staying v5 (`grep -rn "5" tests/Volco.Core.Tests/Config` for schema asserts).
- [ ] **Step 4: Commit** `feat(config): schema v6 with media, program and script action fields`.

### Task 2: Core seams and fakes

**Files:**
- Create: `src/Volco.Core/Media/IMediaSessions.cs`, `src/Volco.Core/Programs/IProgramLauncher.cs`
- Create: `tests/Volco.Core.Tests/Fakes/FakeMediaSessions.cs`, `tests/Volco.Core.Tests/Fakes/FakeProgramLauncher.cs`

**Produces:**
```csharp
namespace Volco.Core.Media;
public enum MediaPlayback { Stopped, Paused, Playing }
public enum MediaCommand { PlayPause, Next, Previous, Stop }
public sealed record MediaApp(string Id, string Name, MediaPlayback Playback);
public interface IMediaSessions {
    IReadOnlyList<MediaApp> GetApps();          // one entry per app id
    string? CurrentAppId { get; }               // null: no current session
    void Send(string? appId, MediaCommand command);  // null = current; async, never throws, logs no-ops
    event EventHandler? Changed;                // any thread
}

namespace Volco.Core.Programs;
public interface IProgramLauncher {
    /// <summary>True/false for an exe path; null when it cannot tell (URL, document).</summary>
    bool? IsRunning(string resolvedPath);
    bool BringToFront(string resolvedPath);     // false when no window
    bool Start(CommandConfig command);          // shell execute; false + logged on failure
    void Watch(IReadOnlyCollection<string> resolvedPaths);  // polled set (2 s)
    event EventHandler? Changed;                // a watched program started or exited
}
```
- [ ] **Step 1:** Write interfaces and fakes (fakes record calls, expose settable state, `RaiseChanged()`).
- [ ] **Step 2: Commit** `feat(core): media and program seams`.

### Task 3: `LnkFile` and `ProgramOpener`

**Files:**
- Create: `src/Volco.Core/Programs/LnkFile.cs` (MS-SHLLINK: header flags, skip IDList, LinkInfo LocalBasePath + CommonPathSuffix; null on anything else)
- Create: `src/Volco.Core/Programs/ProgramOpener.cs`
- Test: `tests/Volco.Core.Tests/Programs/LnkFileTests.cs` (a hand-built .lnk byte array), `ProgramOpenerTests.cs`

**Produces:**
```csharp
public static class LnkFile { public static string? TryReadTarget(byte[] bytes); public static string? TryReadTarget(string path); }
public enum ProgramState { Untracked, NotRunning, Running }
public sealed class ProgramOpener {
    public ProgramOpener(IProgramLauncher launcher, TimeProvider time, Func<string, string?>? resolveLnk = null);
    public static TimeSpan LaunchDebounce { get; } = TimeSpan.FromSeconds(5);
    /// exe → itself; .lnk → its target when it is an exe; anything else (URL, document) → null.
    public string? TrackedPath(CommandConfig command);
    public ProgramState State(CommandConfig? command);
    public void Open(CommandConfig command);   // front if running; else start unless launched < 5 s ago
    public void Watch(IEnumerable<CommandConfig> commands);
    public event EventHandler? Changed;        // forwards the launcher's
}
```
- [ ] **Step 1: Failing tests:** running → `BringToFront`, no `Start`; not running → `Start`; second `Open` within 5 s (FakeTimeProvider) → nothing; after 5 s → `Start` again; URL path → `Start` every press, `State == Untracked`; `.lnk` resolved through `resolveLnk`; empty path → nothing.
- [ ] **Step 2: Implement**, **Step 3: Commit** `feat(core): program opener with launch debounce and .lnk targets`.

### Task 4: Script interpreters

**Files:**
- Create: `src/Volco.Core/Scripts/ScriptInterpreters.cs`, `src/Volco.Core/Scripts/PathLookup.cs`
- Test: `tests/Volco.Core.Tests/Scripts/ScriptInterpretersTests.cs`

**Produces:**
```csharp
public sealed record ProcessSpec(string FileName, string Arguments, string WorkingDirectory, bool UseShell,
                                 IReadOnlyDictionary<string, string> Environment);
public static class ScriptInterpreters {
    /// onPath(name) → full path or null (PathLookup.Find in the app).
    public static ProcessSpec Resolve(CommandConfig command, Func<string, string?> onPath, IReadOnlyDictionary<string, string> env);
}
public static class PathLookup { public static string? Find(string exeName); }   // PATH × PATHEXT
```
Table: `.bat/.cmd` → `cmd.exe`, `/d /s /c ""<path>" <args>"`; `.ps1` → `pwsh` else `powershell.exe`, `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<path>" <args>`; `.py` → `py` else `python`, `"<path>" <args>`; `.js` → `node`; `.exe` → itself, `<args>`; other → `UseShell = true`, FileName = path. Working directory: configured, else the script's folder. Trailing space trimmed when no args.
- [ ] **Step 1: Failing tests** for each row, pwsh missing → powershell.exe, py missing → python, path with spaces, empty args, working directory default.
- [ ] **Step 2: Implement**, **Step 3: Commit** `feat(core): script interpreter table`.

### Task 5: `ScriptRunner`

**Files:**
- Create: `src/Volco.Core/Scripts/ScriptRunner.cs`, `src/Volco.Core/Scripts/SystemProcessStarter.cs`
- Test: `tests/Volco.Core.Tests/Scripts/ScriptRunnerTests.cs` (fake `IProcessStarter`, `FakeTimeProvider`)

**Produces:**
```csharp
public interface IRunningProcess { Task<int?> Exited { get; } IReadOnlyList<string> OutputTail { get; } }  // null exit = unknown (shell)
public interface IProcessStarter { IRunningProcess Start(ProcessSpec spec); }   // throws on failure to start
public sealed record ScriptStatus(bool Running, int? FlashStep);  // FlashStep 0..5 during the failure flash
public sealed class ScriptRunner {
    public ScriptRunner(IProcessStarter starter, TimeProvider time, Func<string, string?> onPath, Func<string, bool> fileExists);
    public const int TailLines = 20;
    public void Run(ButtonActionConfig action, IReadOnlyDictionary<string, string> env);  // keyed by the action object
    public ScriptStatus Status(ButtonActionConfig action);
    public event EventHandler? Changed;   // run start/end, each flash step
}
```
Behaviour: missing file → failure flash + log; Ignore policy and a run going → ignored; exit 0 → silent; non-zero/start exception → log exit code + tail, flash for 6 × 250 ms via `time.CreateTimer`; never kills. `SystemProcessStarter` uses `Process` (`UseShellExecute=false`, `CreateNoWindow=true`, redirected stdout/stderr into a 20-line ring) or `UseShellExecute=true` + `WindowStyle=Hidden` for `UseShell`.
- [ ] **Step 1: Failing tests:** Ignore while running; StartAnother runs twice, `Running` until both end; exit 1 → FlashStep 0..5 as time advances 250 ms, then null; missing file → flash, starter not called; env passed; tail logged.
- [ ] **Step 2: Implement**, **Step 3: Commit** `feat(core): script runner with repeat policy and failure flash`.

### Task 6: `ActionStatus`, display and icons

**Files:**
- Modify: `firmware/assets/icons/manifest.txt` + 6 new 28×28 PNGs (`play`, `pause`, `next`, `previous`, `app`, `script`), drawn with a small Pillow script in the scratchpad.
- Modify: `firmware/tools/gen_assets.py` (also emit `ScreenIconSets.OfferedOnDevicesPage` into `ScreenIcon.g.cs`); run it.
- Modify: `src/Volco.App/ViewModels/DeviceItemViewModel.cs` (use `ScreenIconSets.OfferedOnDevicesPage`); `src/Volco.App/Styles/Icons.axaml` (vector `Icon.Device.Play` … for the six).
- Create: `src/Volco.Core/Runtime/ActionStatus.cs`
- Modify: `src/Volco.Core/Communication/ControllerDisplay.cs`
- Test: `tests/Volco.Core.Tests/Communication/ControllerDisplayCustomActionTests.cs`

**Produces:**
```csharp
public sealed record MediaStatus(bool Present, MediaPlayback Playback, string Name);
public interface IActionStatus {
    MediaStatus Media(MediaAppConfig? target);       // null target = current
    ProgramState Program(CommandConfig? command);
    ScriptStatus Script(ButtonActionConfig action);
}
public sealed class ActionStatus : IActionStatus { public ActionStatus(IMediaSessions? media, ProgramOpener? programs, ScriptRunner? scripts); }
public static class CommandNames { public static string Display(CommandConfig? c); }  // Name, else file name w/o extension, else the URL scheme+rest
// ControllerDisplay: Compute/For/LedValue/LedValueFor/Screen gain a trailing `IActionStatus? status = null`.
```
Display rules: Play/Pause and Stop: LED = playing; screen top `PLAYING`/`PAUSED` (blank when not present), icon play/pause, main = name ("Media" when Current has no session). Next/Previous: LED 0; screen top blank, icon next/previous, main = name. OpenProgram: LED = Running; top `RUNNING`/blank, icon App, main = name. RunScript: LED = running, or `step % 2 == 0` during the flash; top `FAILED` during flash, else `RUNNING`/blank; icon Script; main = name. Missing command → main "Choose a file".
- [ ] **Step 1:** Draw icons, regenerate, `pio test -e native` (if available) and simulator `node --test`.
- [ ] **Step 2: Failing display tests** for each rule with fakes.
- [ ] **Step 3: Implement**, **Step 4: Commit** `feat: controller display and icons for media, program and script actions`.

### Task 7: Actions, factory and runtime wiring

**Files:**
- Create: `src/Volco.Core/Input/Actions/MediaAction.cs`, `OpenProgramAction.cs`, `RunScriptAction.cs`, `ActionServices.cs`
- Modify: `ActionFactory.cs`, `src/Volco.Core/Runtime/VolcoRuntime.cs`
- Test: `tests/Volco.Core.Tests/Input/Actions/CustomActionFactoryTests.cs`, `tests/Volco.Core.Tests/Runtime/CustomActionRuntimeTests.cs`

**Produces:**
```csharp
public sealed record ActionOrigin(int Button, PressType Gesture, string PresetId);
public sealed record ActionServices(IAudioEngine Audio, IPresetController? Presets, IMediaSessions? Media = null,
                                    ProgramOpener? Programs = null, ScriptRunner? Scripts = null);
// ActionFactory.Create(ButtonActionConfig config, ActionServices services, ActionOrigin? origin = null)
// RunScriptAction env: VOLCO_BUTTON = origin.Button, VOLCO_GESTURE = single|double|long, VOLCO_PRESET = preset name
// VolcoRuntime: ctor gains `CustomActionServices? custom = null` (record: IMediaSessions? Media, IProgramLauncher? Launcher,
//   IProcessStarter? Processes); exposes IActionStatus ActionStatus, bool MediaAvailable, bool ProgramsAvailable,
//   bool ScriptsAvailable; Execute(ButtonActionConfig, ActionOrigin?) for the Test button (always trusted).
```
Trust: in `OnMessageReceived` for a `ButtonMessage` with `Pressed`, store `_buttonTrusted[index] = ReferenceEquals(sender, current) && current transport type == Serial` (type recorded when the transport is published). `OnButtonPressDetected` refuses `RunsCode` actions when the flag is false (log "refused: … arrived over UDP/WebSocket"). Subscribes Media/Programs/Scripts `Changed` → `PushControllerState`; after Start and on settings changes, `Programs.Watch(all OpenProgram commands in the live preset)`. Link compute passes `ActionStatus`.
- [ ] **Step 1: Failing runtime tests:** serial press runs the script (fake starter called with env); UDP press refused; WebSocket press refused; media press sends `Send("Spotify.exe", PlayPause)`; media `Changed` pushes a new screen frame; Test (Execute) runs even over UDP.
- [ ] **Step 2: Implement**, **Step 3: Commit** `feat(runtime): media, program and script actions; USB-only rule`.

### Task 8: App wiring and UI

**Files:**
- Modify: `src/Volco.App/PlatformServices.cs` (+`IMediaSessions? Media`, `IProgramLauncher? Launcher`), `CompositionRoot.cs` (pass `CustomActionServices` with `SystemProcessStarter`)
- Modify: `ActionCatalog.cs` (Media group; Custom → Programs & scripts group; `NeedsMediaApp`, `NeedsCommand`, `NeedsWhileRunning`; short labels; light/screen descriptions; availability per runtime)
- Modify: `ActionSlotViewModel.cs` (media app choices, command fields, while-running, Browse via an injected `Func<Task<string?>> pickFile`, `RunsOnlyOverUsb` note, `PathMissing`), `ButtonItemViewModel.cs`, `ButtonsViewModel.cs` (`Execute` with origin), `SettingsViewModel.cs`
- Modify: `src/Volco.App/Controls/ActionRow.axaml(.cs)` (MediaParams, CommandParams, WhileRunning)
- Test: `tests/Volco.App.Tests/ViewModels/CustomActionSlotTests.cs`, `Pages/ButtonsPageCustomActionTests.cs`

- [ ] **Step 1: Failing VM tests:** choosing Run a script shows command fields; editing Path updates config and saves; StartAnother saved; media picker lists Current + live apps + saved missing app "(not running)"; choices disabled without services; "Runs only over USB" visible when connection type is UDP; summary "Run backup" / "Play/Pause Spotify".
- [ ] **Step 2: Implement**, **Step 3: Headless page test** (fields visible per action), **Step 4: Commit** `feat(app): media, program and script action editors`.

### Task 9: Windows implementations

**Files:**
- Create: `src/Volco.Windows/Media/MediaSessions.cs` (`GlobalSystemMediaTransportControlsSessionManager`; snapshot on its own lock; `PlaybackInfoChanged` per session; control-enabled check; names via `AppInfo.GetFromAppUserModelId` else id minus `.exe`)
- Create: `src/Volco.Windows/Programs/ProgramLauncher.cs` (match by process name then `QueryFullProcessImageName`; 2 s timer; `MainWindowHandle`; `ShowWindow(SW_RESTORE)`; `AttachThreadInput` + `SetForegroundWindow`; `FlashWindowEx` fallback)
- Modify: `src/Volco.Windows/NativeMethods.txt`, `Program.cs`
- [ ] **Step 1: Implement**; CI's `firmware-test` job compiles Volco.Windows on Linux.
- [ ] **Step 2: Commit** `feat(windows): media sessions and program launcher`.

### Task 10: Docs and CI

- [ ] Update `CLAUDE.md` (architecture bullets, config v6), spec §3.3 table (top = state, main = name), manual checklist stays in spec §8.
- [ ] Push, run `gh workflow run build.yml --ref worktree-custom_actions`, fix failures until green.
- [ ] Commit `docs: custom actions in CLAUDE.md`.

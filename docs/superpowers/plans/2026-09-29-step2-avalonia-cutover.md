# Step 2 — Avalonia Cutover: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the WinUI 3 UI and XAML OSD with a platform-neutral Avalonia 12 app (`Volco.App`), turn `Volco.Windows` into the shipping `Volco.exe`, and delete `src/Volco/`, all in one PR, with the five settings pages ported visually 1:1.

**Architecture:** `Volco.App` (net8.0, Avalonia, no P/Invoke) is a view over `VolcoRuntime`. `Volco.Windows` becomes the exe: `Program.Main` builds the Windows engine and `Win32WindowEffects` and hands them to `VolcoAppHost.Run`. **The WinUI exe keeps shipping on every commit until Task 9**, so every commit in this PR builds a working app. The Avalonia app first runs end-to-end at Task 9.

**Tech Stack:** .NET 8, Avalonia **12.1.2** (pin exact; current stable), `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Headless.XUnit`, `CommunityToolkit.Mvvm 8.4.0`.

**Spec:** `docs/superpowers/specs/2026-09-29-volco-avalonia-technical-design.md`. Read §4 and §5 in full. Also read `docs/superpowers/specs/2026-06-07-volume-osd-design.md` for OSD behaviour.

**Prerequisite:** Step 1 is merged. This plan relies only on the Step 1 hand-off list at the end of `docs/superpowers/plans/2026-09-29-step1-decompose.md`.

**Level of detail:** above code level. Tasks fix files, exact cross-task interfaces, required tests and verification. XAML bodies are the implementer's call, subject to "visually 1:1 with today's pages".

## Global Constraints

- **Do not run `dotnet` locally.** CI only: push, then `gh run list --branch step2-avalonia --limit 3`. The Windows `build` job runs **only** at the two explicitly authorised points in this plan (Task 1 spike, Task 9 cutover), via `gh workflow run build.yml --ref <branch> --field run_build=true`.
- Git: `/usr/bin/git`; commit messages via `git commit -F <scratch file>`.
- Branch `step2-avalonia` off `main`. One PR.
- **Avalonia 12.1.2** for every Avalonia package, pinned, no floating versions.
- `Volco.App` and `Volco.App.Tests`: `net8.0` unconditionally. **No `[DllImport]`, `LibraryImport`, `Microsoft.UI.*`, `Windows.*` or CsWin32 in `Volco.App`.** The Linux test job enforces this by compiling it.
- Compiled bindings are the default in Avalonia 12: **`x:DataType` on every view, `DataTemplate` and `ControlTemplate` that binds.** No `ReflectionBinding`.
- **No Segoe Fluent Icons / `FontIcon` glyphs.** Every icon is a `PathIcon` whose `Data` comes from `Styles/Icons.axaml`.
- **Visual parity, not redesign.** Same pages, same order, same cards, same labels and copy, same design tokens. Improvements belong to Step 3. The only allowed visible differences are the ones listed in spec §5.2 (min window size; nav rail instead of `NavigationView`).
- Until Task 9, `Volco.Windows` must **not** reference `Volco.App`. Otherwise the WinUI exe would transitively load Avalonia.
- Preserved contracts at cutover: output `Volco.exe` (`<AssemblyName>Volco</AssemblyName>`), artifact `Volco-win-x64`, single-file self-contained win-x64, `GitHubUpdater.AssetName`, `UpdateInstaller.ApplyAndRestart`.
- Wire protocol, firmware and simulator are untouched.

### Carried from Step 1's final review

- (a) Construct `VolcoRuntime` and subscribe every handler (OSD `VolumeChanged` included) before calling `Start()`. Step 1's WinUI shell subscribes the OSD after `Start()`, which is a small window for lost OSD events.
- (b) Runtime events arrive on arbitrary threads, and `SerialConnection` raises `StatusChanged` while holding its internal lock, so Avalonia handlers must use `Dispatcher.UIThread.Post`, never `Invoke`/`InvokeAsync().Wait()`.
- (c) The `Volco.Windows` namespace shadows the global `Windows.*` inside any `namespace Volco.*`, so write `global::Windows…` or put the `using` above a file-scoped namespace line. Importing both `Windows.UI` and `Microsoft.UI` makes `Colors` ambiguous.
- (d) ViewModels must unsubscribe from runtime events when their lifetime is shorter than the app's (Step 1's `ConnectionViewModel` never unsubscribes).
- (e) `SettingsWindow.OnDevicesChanged` marshals twice, and `UpdateWindow.xaml.cs` has a stale `Updates.BuildInfo` cref; both disappear with the WinUI files, so don't port them.
- (f) The firmware only reports a slider's value when it moves, so `SliderLevelChanged` alone leaves a row stuck at a stale/zeroed level until that slider is touched - the Avalonia Sliders page must also seed its live levels from `runtime.GetSliderLevel(i)` (Step 1 fix, checklist finding D) every time it's shown, not only from `SliderLevelChanged` events.

## Review Focus

Spec-implied failure modes that the per-task tests would not otherwise catch. Each gets a pinning test in its owning task.

1. **Closing the settings window exits the app or disposes it,** so the tray's "Open Settings" then throws or opens a blank window → Task 3, `SettingsWindow_Close_HidesAndCanReopen`.
2. **A background-thread runtime event mutates a bound collection** and crashes the app, because runtime events arrive on the audio or transport thread → Task 4, `SettingsViewModel_DevicesChangedFromWorkerThread_MarshalsToUiThread`.
3. **A device picker shows an empty selection for a device configured by friendly name only** (a migrated v1 config) → Task 6, `ButtonsPage_FriendlyNameOnlyDevice_IsSelected`.
4. **The OSD shows "100%" for a muted device, or "Muted" for an unmuted one** → Task 8, `OsdViewModel_MuteKind_ShowsMutedOrOn`.
5. **The OSD is created but click-through is never applied,** for example if applied before the platform handle exists → Task 8, `OsdWindow_AppliesClickThroughExactlyOnceAfterOpen`.

---

## Integration contracts (defined here, used across tasks)

```csharp
// src/Volco.App/Platform/IWindowEffects.cs
namespace Volco.App.Platform;
public interface IWindowEffects
{
    /// Make the window ignore all pointer input so clicks reach what is beneath it.
    /// Called once, after the window's platform handle exists. May no-op on a platform
    /// that has not implemented it yet.
    void MakeClickThrough(Avalonia.Controls.Window window);
}

// src/Volco.App/PlatformServices.cs
namespace Volco.App;
public sealed record PlatformServices(IAudioEngine Audio, bool AudioReady, IWindowEffects WindowEffects);

// src/Volco.App/VolcoAppHost.cs
namespace Volco.App;
public static class VolcoAppHost
{
    /// Entry point for every OS head. createPlatform runs once on the UI thread during
    /// framework initialisation. Returns the process exit code.
    public static int Run(string[] args, Func<PlatformServices> createPlatform);

    /// Configured builder without a platform factory, for the previewer and tests.
    public static AppBuilder BuildAvaloniaApp();
}

// src/Volco.App/VolcoApplication.axaml.cs
public partial class VolcoApplication : Application
{
    /// Replaces WinUI's App.RequestExit/App.IsExiting (used by UpdateWindow and SettingsWindow).
    public static bool IsExiting { get; }
    public static void RequestExit();
}
```

`CompositionRoot` (internal to `Volco.App`) owns startup: `ConfigManager` at `%APPDATA%/Volco/config.json`, then `Load()`, then `createPlatform()`, then `new VolcoRuntime(config, p.Audio, p.AudioReady)`, then creates the OSD window and subscribes it and every other runtime handler (`runtime.VolumeChanged += (_, n) => osd.Show(n)`, etc.), and **only then** calls `runtime.Start()` (rule (a) above). After that it creates the tray icon, and shows settings if `AudioEngineFailed`. Shutdown order: runtime `Dispose` → engine `Dispose` → close windows → tray. This mirrors Step 1's `App.xaml.cs`.

Shared styles resource: `avares://Volco.App/Styles/VolcoStyles.axaml` merges `Colors`, `Tokens`, `Typography`, `Brushes`, `Icons` and the `SettingsCard` theme. Both `VolcoApplication` and the test app include it, and nothing else.

Test fakes: `Volco.App.Tests.csproj` **links** `FakeAudioEngine.cs` and `FakeControllerConnection.cs` from `tests/Volco.Core.Tests` via `<Compile Include="..\Volco.Core.Tests\…" Link="Fakes\…" />`. No shared test-support project.

---

### Task 1: Spike (throwaway): tray left-click, click-through, headless xUnit version

**Files:** a throwaway branch `step2-spike` only. Nothing from it merges.

This task settles the only three unknowns in the step before any real code exists.

- [ ] **Step 1: Minimal Avalonia 12.1.2 exe** (`net8.0-windows10.0.22621.0`, single-file win-x64) with a `TrayIcon` whose `Command` logs "left-click" and opens a window, and a `NativeMenu` with Exit. Add a 360×56 borderless `Topmost`, `ShowActivated=False`, `ShowInTaskbar=False` window with a rounded `Border`, made click-through by setting `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` on `TryGetPlatformHandle()!.Handle` inside its `Opened` handler.
- [ ] **Step 2: Headless test project on the same branch** with `Avalonia.Headless.XUnit 12.1.2` and one `[AvaloniaFact]`. Record which xUnit major it requires (v2 `xunit 2.x` or v3 `xunit.v3`). `Volco.App.Tests` uses what works here; `Volco.Core.Tests` stays on xUnit 2.7 regardless.
- [ ] **Step 3: Build with `run_build=true`** (adapt the spike branch's workflow to publish the spike exe as the artifact), and **hand the artifact to the user.** Gate: the user confirms (a) left-click opens the window, (b) right-click shows the menu, (c) clicks pass through the pill to the desktop and it never takes focus, (d) the pill is sharp at 150% scaling.
- [ ] **Step 4: Record the results** at the top of the Step 2 PR description: what worked, the xUnit major, and any fallback taken. If left-click fails, the fallback is `Shell_NotifyIcon` P/Invoke in `Volco.Windows` exposing a tray abstraction. Stop and ask the user before building it, because it changes the Task 3 contract.

---

### Task 2: `Volco.App` + `Volco.App.Tests` skeleton, styles, `SettingsCard`, icons

**Files:**
- Create: `src/Volco.App/Volco.App.csproj`, `src/Volco.App/Styles/{Colors,Tokens,Typography,Brushes,Icons,VolcoStyles}.axaml`, `src/Volco.App/Controls/SettingsCard.cs` + `Controls/SettingsCard.axaml` (theme), `src/Volco.App/Assets/volco.ico`
- Create: `tests/Volco.App.Tests/Volco.App.Tests.csproj`, `tests/Volco.App.Tests/TestAppBuilder.cs`, `tests/Volco.App.Tests/Controls/SettingsCardTests.cs`
- Modify: `Volco.sln`; `.github/workflows/build.yml` test step (add the second `dotnet test` line from spec §7)

**Interfaces:**
- Produces: `Volco.App.Controls.SettingsCard : TemplatedControl` with styled properties `Header` (string), `Description` (string?), `HeaderIcon` (object?) and a content slot (`Content`). `VolcoStyles.axaml` as described under Integration contracts. `Icons.axaml` has one `StreamGeometry` resource per glyph used today, keyed by purpose (e.g. `Icon.Volume`, `Icon.Muted`, `Icon.Connection`, `Icon.Sliders`, `Icon.Buttons`, `Icon.Speaker`, `Icon.Settings`, `Icon.Debug`). **Inventory the glyphs** with `grep -rn "Glyph=" src/Volco/UI` and map each one. Use Fluent UI System Icons (MIT) path data, and record its source in a header comment.
- `TestAppBuilder`: `[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]`; a test `Application` with `FluentTheme` (test app pins `RequestedThemeVariant=Dark`; the product follows the OS) + `VolcoStyles.axaml`; `.UseHeadless(new AvaloniaHeadlessPlatformOptions())`.

**Requirements:** tokens are ported value-for-value from `src/Volco/UI/Styles/*.xaml` (names kept). `Controls.xaml`'s implicit styles are ported where Avalonia has the control. The `.ico` is a real multi-size icon (16/20/24/32/48/256) that recreates today's generated tray icon: white "V" on RGB(30,100,200). Commit the `.ico` binary.

- [ ] **Step 1: Write `SettingsCardTests`:** `[AvaloniaFact]` rendering a `SettingsCard` with header, description and a `ToggleSwitch` inside a window → the template's header `TextBlock` shows the header, the description is visible, and the toggle is in the visual tree. Also, a card with `Description = null` hides the description element.
- [ ] **Step 2: Create both projects, styles, control and icons; add the CI test line; update the sln.**
- [ ] **Step 3: Push; both test projects green on Linux; commit.**

---

### Task 3: Application shell: host, composition root, tray, settings window, nav

**Files:**
- Create: `src/Volco.App/{VolcoAppHost,PlatformServices,CompositionRoot}.cs`, `src/Volco.App/VolcoApplication.axaml(.cs)`, `src/Volco.App/Platform/IWindowEffects.cs`, `src/Volco.App/Views/SettingsWindow.axaml(.cs)`, `src/Volco.App/Views/Pages/*Page.axaml(.cs)` as **empty placeholders** for the five pages
- Create tests: `tests/Volco.App.Tests/Shell/SettingsWindowTests.cs`, `tests/Volco.App.Tests/Shell/CompositionRootTests.cs`

**Interfaces:** Produces everything in "Integration contracts". `SettingsWindow(SettingsViewModel vm, VolcoRuntime runtime)`, which Task 4 fills in; until Task 4 lands, the window takes only `VolcoRuntime`.

**Requirements (spec §5.2):** `ShutdownMode.OnExplicitShutdown`. Tray via `TrayIcon.Icons` with `Command` → show settings. The menu is Open Settings / Check for Updates… (only when `BuildInfo.IsRelease`) / separator / Exit, and the tooltip is as in Step 1. Settings window: 720×600, `MinWidth=680`, `MinHeight=560`, `FluentTheme` following the OS theme (Default variant). `Closing` → `Cancel=true; Hide()` unless `VolcoApplication.IsExiting`. Nav rail is a `ListBox` bound to five nav items (icon + label, same order and labels as today's `NavigationView`) + a `ContentControl` whose content is the selected page's view. The first item is selected on open. `CompositionRoot` must be constructible with an injected `ConfigManager` path and `PlatformServices`, so tests can drive it. Tray creation must tolerate the headless platform, which has no tray: create it in a separate method that the tests can skip, or no-op when the platform provides no tray implementation.

- [ ] **Step 1: Write tests:**
  - `SettingsWindow_Close_HidesAndCanReopen` (Review Focus #1): show → `Close()` → `IsVisible == false`, window not disposed → `Show()` again works and content is intact.
  - `SettingsWindow_NavSelection_SwapsPage`: select each nav item → `ContentControl.Content` is the matching page type.
  - `SettingsWindow_MinSize_Is680x560`.
  - `CompositionRoot_AudioNotReady_ShowsSettingsOnStart`: `PlatformServices(fake, AudioReady: false, recordingEffects)` → a settings window is shown.
  - `CompositionRoot_Shutdown_DisposesRuntimeBeforeEngine`: use a fake engine that records whether the runtime was still subscribed when `Dispose` ran (it must not be).
- [ ] **Step 2: Implement.**
- [ ] **Step 3: Push; green; commit.**

---

### Task 4: Move the ViewModels; port the Connection and General pages

**Files:**
- Move (`git mv`): `src/Volco/UI/ViewModels/*.cs` → `src/Volco.App/ViewModels/`; namespace `Volco.App.ViewModels`
- Create: `src/Volco.App/Views/Pages/{ConnectionPage,GeneralPage}.axaml(.cs)` (replacing placeholders)
- Modify: `src/Volco/UI/*.cs` (the WinUI project) to reference the moved ViewModels
- Create tests: `tests/Volco.App.Tests/ViewModels/SettingsViewModelTests.cs`, `tests/Volco.App.Tests/Pages/{ConnectionPageTests,GeneralPageTests}.cs`

**Keeping WinUI working while the ViewModels move:** the WinUI project must still compile and ship, and it must **not** get a `ProjectReference` to `Volco.App`, because that would load Avalonia into the WinUI exe. So the ViewModels may depend only on `CommunityToolkit.Mvvm` and `Volco.Core`, never on Avalonia types, and UI-thread marshalling goes through an injected dispatcher:

```csharp
// src/Volco.App/ViewModels/IUiDispatcher.cs — ViewModel-internal, NOT a platform seam
public interface IUiDispatcher { void Post(Action action); }
```

To avoid the WinUI → `Volco.App` reference, the WinUI project **compile-links** the ViewModel sources: `<Compile Include="..\Volco.App\ViewModels\**\*.cs" Link="UI\ViewModels\%(RecursiveDir)%(Filename)%(Extension)" />`. It supplies a `DispatcherQueue`-backed `IUiDispatcher`; `Volco.App` supplies `AvaloniaUiDispatcher` (`Dispatcher.UIThread.Post`). The link is deleted with `src/Volco/` in Task 9. In `SettingsViewModel` and `ConnectionViewModel`, replace `DispatcherQueue.GetForCurrentThread()` with the injected `IUiDispatcher`.

**Interfaces:**
- Consumes: `SettingsViewModel(ConfigManager, IAudioEngine, VolcoRuntime)` and `ConnectionViewModel(ConfigManager, VolcoRuntime)` from Step 1.
- Produces: `SettingsViewModel(ConfigManager config, IAudioEngine audio, VolcoRuntime runtime, IUiDispatcher ui)`; `ConnectionViewModel(ConfigManager config, VolcoRuntime runtime, IUiDispatcher ui)`. The other ViewModels' constructors are unchanged.

**Page requirements:** Connection = transport type selector; Serial COM port picker (`SerialPort.GetPortNames()`) with refresh, and baud rate; UDP port and WebSocket port as `NumericUpDown`; live status indicator (port `ConnectionStatusToBrushConverter`). General = debug logging toggle. Copy is identical to the WinUI XAML.

- [ ] **Step 1: Write tests:**
  - `SettingsViewModel_DevicesChangedFromWorkerThread_MarshalsToUiThread` (Review Focus #2): a recording `IUiDispatcher` that queues actions; raise `DevicesChanged` from `Task.Run` → no collection mutated until the queued action runs; after running, the pickers contain the new device.
  - `ConnectionPage_StatusChange_UpdatesIndicator` (`[AvaloniaFact]`): fake connection → `SetStatus(Connected)` → indicator text/brush reflects Connected after `Dispatcher.UIThread.RunJobs()`.
  - `ConnectionPage_TypeSelection_ShowsOnlyThatTransportsFields`.
  - `GeneralPage_DebugToggle_WritesConfigAndAppLog`.
- [ ] **Step 2: Move the ViewModels, add the link and the WinUI dispatcher adapter, port both pages.**
- [ ] **Step 3: Push; green; commit.** The WinUI app is unchanged in behaviour. Nothing proves that on Windows until Task 9, so keep this diff mechanical.

---

### Task 5: Sliders page

**Files:** `src/Volco.App/Views/Pages/SlidersPage.axaml(.cs)`; test `tests/Volco.App.Tests/Pages/SlidersPageTests.cs`

**Requirements:** port `SlidersTab.xaml` 1:1: per-slider card with name, mapping type, device picker / process list with `AutoCompleteBox` fed by `SliderItemViewModel.GetProcessSuggestions`, invert, response curve, dead zone, smoothing, and the live level `ProgressBar`. Live levels come from `runtime.SliderLevelChanged`, posted through `IUiDispatcher`, into `SlidersViewModel.UpdateLiveLevel`. `BoolToVisibilityConverter` is not ported; bind `IsVisible`. `IntToDoubleConverter` is ported only if `NumericUpDown` needs it.

- [ ] **Step 1: Write tests** (`[AvaloniaFact]`, `FakeAudioEngine` with two devices and three sessions):
  - `SlidersPage_RendersOneCardPerConfiguredSlider`.
  - `SlidersPage_MappingTypeDevice_ShowsDevicePickerOnly` / `…Processes_ShowsProcessListOnly`.
  - `SlidersPage_ProcessAutoComplete_SuggestsLiveSessions`.
  - `SlidersPage_LiveLevel_UpdatesProgressBar`: raise via the ViewModel → `ProgressBar.Value` changes.
  - `SlidersPage_EditPersistsToConfig`: change invert → `ConfigManager.Config.Sliders[0].Invert` is true.
- [ ] **Step 2: Implement.** **Step 3: Push; green; commit.**

---

### Task 6: Buttons page

**Files:** `src/Volco.App/Views/Pages/ButtonsPage.axaml(.cs)`; test `tests/Volco.App.Tests/Pages/ButtonsPageTests.cs`

**Requirements:** port `ButtonsTab.xaml` 1:1: per-button card; single/double/long press action pickers (`PressActionViewModel`); device picker shown only for device-taking actions; long-press and double-press timing fields.

- [ ] **Step 1: Write tests:**
  - `ButtonsPage_RendersOneCardPerButton_WithThreePressRows`.
  - `ButtonsPage_ActionNeedingDevice_ShowsDevicePicker` / `ButtonsPage_MuteCurrentOutput_HidesDevicePicker`.
  - `ButtonsPage_FriendlyNameOnlyDevice_IsSelected` (Review Focus #3): config `Device = { Id = "", FriendlyName = "Speakers" }` with a fake device named "Speakers" → picker `SelectedItem` is that device.
  - `ButtonsPage_ChangeAction_PersistsToConfig`.
- [ ] **Step 2: Implement.** **Step 3: Push; green; commit.**

---

### Task 7: Output Devices page and Update window

**Files:** `src/Volco.App/Views/Pages/OutputDevicesPage.axaml(.cs)`, `src/Volco.App/Views/UpdateWindow.axaml(.cs)`; tests `tests/Volco.App.Tests/Pages/OutputDevicesPageTests.cs`, `tests/Volco.App.Tests/Shell/UpdateWindowTests.cs`

**Requirements:** Output Devices ports `OutputDevicesTab.xaml` 1:1 (device pair pickers). Update window ports `UpdateWindow.xaml(.cs)` 1:1, with `App.RequestExit()` → `VolcoApplication.RequestExit()`. Opened from the tray only when `BuildInfo.IsRelease`.

- [ ] **Step 1: Tests:** `OutputDevicesPage_PickersListRenderDevicesOnly`; `OutputDevicesPage_DeviceAddedEvent_AppearsInPickers`; `UpdateWindow_Renders_WithCurrentDisplayVersion`.
- [ ] **Step 2: Implement.** **Step 3: Push; green; commit.**

---

### Task 8: The OSD

**Files:** `src/Volco.App/Osd/{VolumeOsdWindow.axaml(.cs),VolumeOsdViewModel.cs}`; tests `tests/Volco.App.Tests/Osd/{VolumeOsdViewModelTests,VolumeOsdWindowTests}.cs`

**Interfaces:**
- Produces: `VolumeOsdWindow(IWindowEffects effects)` with `public void Show(VolumeChangeNotification n)`, which is thread-safe (posts to the UI thread). `CompositionRoot` wires, before calling `runtime.Start()`, `runtime.VolumeChanged += (_, n) => osd.Show(n)`.
- `VolumeOsdViewModel` is pure (plain `[Fact]`-testable): `Apply(VolumeChangeNotification)` → `Label`, `Level` (0–100), `ValueText` ("37%", or "Muted"/"On" when `Kind == Mute`), `IsMutedGlyph`.

**Requirements (spec §5.3):** created once and kept hidden. `WindowDecorations=None`, `Topmost`, `ShowActivated=False`, `ShowInTaskbar=False`, transparent background, rounded `Border` pill 360×56 DIP. `effects.MakeClickThrough(this)` is called exactly once, from the first `Opened`. Position is bottom-centre of `Screens.Primary.WorkingArea`, 80 DIP above the bottom, and is computed only on the hidden→visible transition. Repaint only on change, capped at 60 Hz: coalesce `Show` calls arriving within 16 ms into one UI post. Auto-hide 1500 ms after the last `Show`, then ~250 ms opacity fade (a `DispatcherTimer` stepping `Opacity`, or an Avalonia `Transitions` on `Opacity`), then `Hide()`. No continuous render loop. Icons come from `Icons.axaml` (`Icon.Volume` / `Icon.Muted`).

- [ ] **Step 1: Write tests:**
  - `OsdViewModel_VolumeKind_ShowsRoundedPercent` (0.374 → "37%").
  - `OsdViewModel_MuteKind_ShowsMutedOrOn` (Review Focus #4): `Muted=true` → "Muted" + muted glyph; `Muted=false` → "On" + volume glyph.
  - `OsdViewModel_LongLabel_IsPassedThroughForEllipsis` (the view uses `TextTrimming=CharacterEllipsis`; assert on the `TextBlock` in the window test).
  - `OsdWindow_AppliesClickThroughExactlyOnceAfterOpen` (Review Focus #5): recording `IWindowEffects`; `Show` three times → one call, made when the handle existed (`TryGetPlatformHandle() != null` at call time).
  - `OsdWindow_ShowFromWorkerThread_DoesNotThrow` (`Task.Run(() => osd.Show(n))`, then `RunJobs`).
  - `OsdWindow_NeverActivates` (`ShowActivated == false`, `Topmost == true`, `ShowInTaskbar == false`).
- [ ] **Step 2: Implement.** **Step 3: Push; green; commit.**

---

### Task 9: Cutover: `Volco.Windows` becomes the exe; delete WinUI; CI

**Files:**
- Modify: `src/Volco.Windows/Volco.Windows.csproj` (→ `WinExe`, `<AssemblyName>Volco</AssemblyName>`, `ApplicationManifest`, `ApplicationIcon`, `ProjectReference` → `Volco.App`, Avalonia.Desktop 12.1.2)
- Create: `src/Volco.Windows/Program.cs`, `src/Volco.Windows/Platform/Win32WindowEffects.cs`, `src/Volco.Windows/app.manifest` (moved from `src/Volco/`, plus PMv2 DPI), `src/Volco.Windows/volco.ico` (copy of the App asset)
- Delete: `src/Volco/` entirely
- Modify: `Volco.sln`, `.github/workflows/build.yml` (build job), `.github/workflows/release.yml`, `CLAUDE.md`, `README.md`

**Interfaces:**

```csharp
// src/Volco.Windows/Program.cs
internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => VolcoAppHost.Run(args, () =>
    {
        var engine = new Volco.Windows.Audio.AudioEngine();
        bool ready = engine.Start();
        return new PlatformServices(engine, ready, new Win32WindowEffects());
    });
}
```

`Win32WindowEffects.MakeClickThrough` applies the extended styles proven in the Task 1 spike to `window.TryGetPlatformHandle()!.Handle`. It is the only UI P/Invoke in the repo. Use CsWin32 (add `GetWindowLongPtr`, `SetWindowLongPtr` and the `WS_EX_*` constants to `NativeMethods.txt`) or `LibraryImport`, whichever builds cleanly.

**Manifest:** keep `asInvoker`, `uiAccess=false`, and add:
```xml
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
  </windowsSettings>
</application>
```

**CI** (spec §7): in `build.yml`'s `build` job and in `release.yml`, delete `microsoft/setup-msbuild` and replace the `msbuild` step with:
```
dotnet publish src/Volco.Windows/Volco.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish/
```
`release.yml` keeps passing `-p:VolcoVersion=… -p:VolcoReleaseChannel=release`, and keeps uploading `./publish/Volco.exe`. Job triggers are unchanged.

- [ ] **Step 1: Write `Win32WindowEffects`, `Program.cs` and the manifest; switch the csproj; delete `src/Volco/`; update the sln and both workflows.**
- [ ] **Step 2: Verify by search.** `grep -rn "Microsoft.UI\|WindowsAppSDK\|H.NotifyIcon\|CommunityToolkit.WinUI" src tests .github` returns nothing, and `grep -rn "DllImport\|LibraryImport" src/Volco.App src/Volco.Core` returns nothing.
- [ ] **Step 3: `gh workflow run build.yml --ref step2-avalonia --field run_build=true`; fix until green.** Artifact `Volco-win-x64` contains exactly one `Volco.exe`. Report its size.
- [ ] **Step 4: Update `CLAUDE.md`** (Architecture: three projects, the two seams, the entry point, and that `Volco.App`/`Volco.Core` must stay platform-free, enforced by the Linux job; Build & Test: both test projects) **and `README.md`** (tech stack, architecture diagram, build instructions). Commit.

---

### Task 10: Measurements, manual checklist, PR

**Files:** `MEASUREMENTS.md`, `docs/superpowers/checklists/2026-09-29-step2-manual-checklist.md`

- [ ] **Step 1: Write the manual checklist**, each item with its expected result:
  1. Cold start → tray icon in < 1 s; tooltip correct.
  2. Tray left-click opens settings; right-click menu has the right items (Check for Updates only on a release build); Exit quits cleanly (no process left in Task Manager).
  3. Close settings → hides; reopen → same state (Review Focus #1). Window cannot shrink below 680×560.
  4. All five pages match the WinUI screenshots (the user compares against the Step 1 build side by side); every setting persists across restart.
  5. OSD: appears bottom-centre on the primary monitor for each trigger (device slider, process slider, unmapped slider, each mute action); click-through (click a desktop icon underneath); never steals focus from a focused text editor; auto-hides after ~1.5 s with a fade; sharp at 100% and 150% scaling; no flash at the old position on first show.
  6. Muted OSD shows "Muted"; unmuted shows "On" (Review Focus #4).
  7. ESP32 over serial and `tools/simulator/index.html` over WebSocket: sliders, buttons, LED feedback (`0x03`, `0x04`); replug recovery.
  8. v1 `config.json` migrates silently with `config.v1.bak`.
  9. In-app update from a published release to this build's release-stamped artifact works end-to-end, including relaunch.
  10. No `svcl.exe`, no Windows App SDK DLLs, only `Volco.exe` in the install folder.
- [ ] **Step 2: Measurements.** Following `docs/superpowers/specs/2026-09-26-volco-audio-core-design.md` §8 and the existing `MEASUREMENTS.md` format, add the procedure for the user to run on Windows. Add an empty results table for this build next to the `main` baseline: idle CPU average (target < 0.1%), idle GPU (target 0), handle count over a 60 s sweep (target flat), cold start to tray (target < 1 s), working set, and exe size. The agent cannot run these; the user fills in the numbers before merge.
- [ ] **Step 3: Open the PR** "Step 2: Avalonia cutover". The body has the Task 1 spike results, links to the spec, plan and checklist, and ends with the attribution lines. **Do not merge.**

---

## Done when

- Linux test job green for both test projects; Windows `run_build` green; artifact is a single `Volco.exe`.
- All five Review Focus tests exist and pass; Tasks 5 and 6 headless tests exist (the spec's minimum).
- `src/Volco/` is gone; the Task 9 greps are clean.
- The user has run the checklist, filled in `MEASUREMENTS.md`, and approved the PR.

## Hand-off to Step 3

Step 3 may change anything under `src/Volco.App/` and `tests/Volco.App.Tests/`, and nothing else. It relies on: the page/view structure in `src/Volco.App/Views/`, the ViewModels in `src/Volco.App/ViewModels/`, `VolcoStyles.axaml` + `Icons.axaml`, `SettingsCard`, and `VolumeOsdWindow` / `VolumeOsdViewModel`.

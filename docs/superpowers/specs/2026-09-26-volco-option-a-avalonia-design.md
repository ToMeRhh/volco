# Volco Option A — Avalonia Rewrite

**Date:** 2026-09-26
**Status:** Approved (design only — not implemented)
**Depends on:** `2026-09-26-volco-audio-core-design.md` (the shared audio redesign —
read it first; it is not repeated here)
**Sibling option:** `2026-09-26-volco-option-d-winui-remediation-design.md`

> Option A and Option D are to be built and measured **independently, on separate
> branches**, then compared using the measurement protocol in the shared spec §8.
> Neither is a prerequisite for the other.

---

## 1. Goal and rationale

Replace WinUI 3 / Windows App SDK with **Avalonia**, restructure the repo around a
platform-neutral core, implement the shared audio redesign, and replace the
XAML-based OSD with a native Win32 overlay.

Why Avalonia over the alternatives that were considered:

- **The entire existing backend is C# and moves over unchanged.** `Communication/`,
  `Config/`, `Input/` and all nine test files are already platform-neutral —
  `Volco.csproj` forces that today with its non-Windows `<Compile Remove>` rules.
  A Rust/Tauri rewrite would discard all of it, including the only safety net the
  project has given that the dev machine cannot build locally.
- **It drops the Windows App SDK entirely.** No self-contained ~100 MB payload, no
  unpackaged-mode quirks, no XAML framework loaded just to draw a tray icon.
  Avalonia renders through Skia on Direct3D 11 with a software fallback.
- **WinUI 3's documented idle cost goes away.** Microsoft's own trackers record
  WinUI 3 desktop windows idling at 0.8–2 % CPU with no user code running
  (`microsoft-ui-xaml#4565`), mouse-move CPU spikes (`#6914`), and 2–6× higher
  input CPU than WinUI 2 (`#6750`).
- **It keeps the Linux door open for almost nothing.** `IAudioEngine` is the only
  platform seam; the UI is already portable. macOS would be device-volume-only
  (see shared spec §9).
- **Tauri was rejected** for this use case: the OSD is its weakest area (open
  `setIgnoreCursorEvents` bugs on Windows, no per-region hit-testing, the
  community workaround is polling the cursor at 60 fps), and a WebView2 surface is
  the wrong shape for a pill that repaints 50×/s.

**Non-goal:** shipping Linux or macOS support in this work. The point is to stop
*precluding* it.

---

## 2. Repository structure

```
src/
  Volco.Core/                  net8.0 — no UI, no platform APIs. Runs & tests on Linux.
    Communication/             ControllerMessage, IControllerConnection, ProtocolParser,
                               ProtocolEncoder, SerialConnection, UdpConnection,
                               WebSocketConnection          [moved verbatim]
    Config/                    AppConfig, ConfigManager      [moved]
                               ConfigMigrator                [new — shared spec §4]
    Input/                     ButtonPressDetector, SliderProcessor,
                               VolumeChangeNotification, Actions/  [moved, retargeted
                               from IAudioManager to IAudioEngine]
    Audio/                     IAudioEngine + DTOs only, no implementation  [new]
    Osd/                       IVolumeOsd (interface only)    [new]
    Diagnostics/               AppLog                         [moved]
    Updates/                   BuildInfo, GitHubUpdater, UpdateInfo,
                               UpdateInstaller                [moved verbatim]

  Volco.Audio.Windows/         net8.0-windows — the COM AudioEngine  [new]
                               CsWin32-generated interop; see shared spec §2

  Volco.Osd.Windows/           net8.0-windows — native overlay        [new, see §5]

  Volco.App/                   net8.0-windows (Avalonia) — tray, settings,
                               composition root               [new]
    Views/                     *.axaml
    ViewModels/                ported from src/Volco/UI/ViewModels/
    Controls/                  SettingsCard.axaml (local replacement for the
                               CommunityToolkit control)
    Styles/                    Colors, Tokens, Typography, Brushes (ported)
    Assets/                    volco.ico

tests/
  Volco.Core.Tests/            the existing nine test files, retargeted  + new
                               ConfigMigrator and SliderProcessor tests
```

`Volco.App` targets `net8.0-windows` **only because it references
`Volco.Audio.Windows` and `Volco.Osd.Windows`**. Those references are the sole
things to swap for a future Linux build; the Avalonia code itself is portable.

The old single-project layout with `<Compile Remove>` conditionals disappears —
the platform split becomes a real project boundary instead of an msbuild
condition. This is a meaningful clarity win and it makes Linux CI test *more* of
the codebase, not less.

---

## 3. What moves unchanged, and what changes

### 3.1 Moves verbatim (no logic edits)

`ProtocolParser`, `ProtocolEncoder`, `ControllerMessage`, `IControllerConnection`,
`SerialConnection`, `UdpConnection`, `WebSocketConnection`, `ButtonPressDetector`,
`VolumeChangeNotification`, `AppLog`, `ConfigManager`, all of `Updates/`, and all
nine test files. Namespaces change (`Volco.*` → `Volco.Core.*`) — nothing else.

The wire protocol is **frozen**: `0xAA | length | payload | XOR`, message types
`0x01` slider, `0x02` button, `0x03` mute state, `0x04` active device. The ESP32
firmware and `tools/simulator/index.html` both depend on it.

### 3.2 Changes

| Component | Change |
|---|---|
| `AudioManager`, `SessionMonitor`, `IAudioManager` | **Deleted.** Replaced by `IAudioEngine` / `AudioEngine` per the shared spec. |
| `SliderProcessor` | Retargeted to `IAudioEngine`. `SessionMonitor` dependency removed. `Unmapped` becomes `SessionFilter.Unmapped`. Keep the 20 ms coalescing — it is correct and cheap; the problem was never the timer. |
| `Actions/*` | Retargeted to `IAudioEngine`; `MuteSpecificDeviceAction` / `SetOutputDeviceAction` take a `DeviceRefConfig` instead of a name string. |
| `AppConfig` | Schema v2 per shared spec §4. |
| `App.xaml.cs` | Becomes `Volco.App/App.axaml.cs` + a `CompositionRoot` class. The ~350 lines of today's `App.xaml.cs` mix DI wiring, tray setup, message routing, OSD triggering, and controller-state pushing — split them: `CompositionRoot` (wiring), `TrayIconHost`, `ControllerRouter` (message → processor/detector), `ControllerStateReporter` (the `PushControllerState` / `GetMuteStateForButton` logic, which is currently buggy — see shared spec §1.1 item 2). |
| `VolumeOsdWindow` | **Deleted.** Replaced per §5. |
| `Tools/svcl.exe` | **Deleted** (shared spec §5). |

---

## 4. The Avalonia UI

### 4.1 Shell

- Avalonia **11.x stable** (verify the current version and whether 12 is stable at
  implementation time; 12 reports a further ~4× startup improvement under
  NativeAOT).
- `Application.ShutdownMode = ShutdownMode.OnExplicitShutdown` — closing the
  settings window must not exit the app.
- Tray icon: Avalonia's built-in `TrayIcon` with a `NativeMenu` (Open Settings /
  Check for Updates… / — / Exit). **Windows requires a real `.ico`** — add
  `Assets/volco.ico`. This replaces `H.NotifyIcon.WinUI` and, importantly,
  `GeneratedIconSource`, which today renders the tray icon *through XAML*.
  - **Verify during implementation:** left-click-to-open behaviour. Avalonia's
    `TrayIcon` exposes a `Clicked` event, but left-click handling has historically
    been inconsistent across versions. If it does not work, fall back to
    `Shell_NotifyIcon` P/Invoke in `Volco.Osd.Windows` (which already owns a
    message-only window).
- Settings window: 720×600 default. Avalonia supports `MinWidth`/`MinHeight`
  natively, which **closes the deferred TODO in `SettingsWindow.xaml.cs`** (WinUI
  3 has no minimum-size API without a `WM_GETMINMAXINFO` subclass). Set 680×560.
- Window close → hide, not dispose; matches today's `AppWindow_Closing` behaviour.

### 4.2 MVVM

Keep `CommunityToolkit.Mvvm` — it works with Avalonia and is source-generator
based, so the existing `[ObservableProperty]` / `[RelayCommand]` ViewModels port
with near-zero edits. The real work is in the views.

Use Avalonia **compiled bindings** (`x:DataType` + `{CompiledBinding}`, or
`{Binding}` with `x:CompileBindings="True"`) as the direct analogue of WinUI's
`x:Bind`. This matters for both performance and trimming/AOT viability.

### 4.3 Control mapping

The settings UI is five pages (Connection, Sliders, Buttons, Output Devices,
General), ~1,500 lines of XAML. The port is close to mechanical. Substitutions:

| WinUI 3 | Avalonia 11 | Note |
|---|---|---|
| `NavigationView` + `Frame` | `ListBox`/`TabControl` + `ContentControl` | Avalonia has no `NavigationView`; a simple nav rail is ~30 lines and looks the same |
| `tk:SettingsCard` (CommunityToolkit) | local `Controls/SettingsCard.axaml` | ~40-line `TemplatedControl`: `Header`, `Description`, `HeaderIcon`, content slot. No Avalonia equivalent exists |
| `AutoSuggestBox` | `AutoCompleteBox` | built in; used for the process picker |
| `NumberBox` | `NumericUpDown` | built in; used for UDP/WebSocket ports |
| `ItemsRepeater` | `ItemsControl` or `ItemsRepeater` | both exist in Avalonia 11 |
| `FontIcon` + Segoe Fluent glyphs | `PathIcon` with inline vector data | **recommended** — removes the Segoe Fluent Icons font dependency, which is the single biggest portability wart in the current XAML. Keep glyphs only if speed matters more |
| `ToggleSwitch`, `ComboBox`, `Slider`, `CheckBox`, `ProgressBar`, `TextBlock`, `Border`, `StackPanel`, `Grid`, `ScrollViewer` | same names | direct ports |
| `Visibility` + `BoolToVisibilityConverter` | `IsVisible` (bool) | **delete the converter** — Avalonia binds a bool directly |
| `x:Bind` | `{CompiledBinding}` | with `x:DataType` on the template |
| `StaticResource` token dictionaries | `ResourceDictionary` / `ResourceInclude` | `Styles/Colors|Tokens|Typography|Brushes.xaml` port almost line-for-line |

Theme: `FluentTheme` with `RequestedThemeVariant="Dark"`, plus the ported token
dictionaries. Avalonia supports Mica/Acrylic on Windows via `TransparencyLevelHint`
if a translucent settings window is wanted — optional, not required.

### 4.4 Live slider levels

Today `SettingsWindow` subscribes to `SliderProcessor.SliderLevelChanged` and
marshals with `DispatcherQueue.TryEnqueue`. In Avalonia the equivalent is
`Dispatcher.UIThread.Post`. Keep the same shape, and keep the unsubscribe on real
shutdown — today's leak-avoidance logic in `AppWindow_Closing` is correct and
should survive the port.

---

## 5. The OSD: a native Win32 overlay

**This component is deliberately shared with Option D.** Whichever option is built
first should produce it; the other branch takes it as-is.

### 5.1 Why not an Avalonia window

It would work, and it would already be far lighter than the WinUI 3 window. But
the OSD and the settings window have opposite requirements: the OSD is a 360×56
pill that repaints on every slider tick and is otherwise invisible, while the
settings window is a rich layout open ~1 % of the time. Serving both from one
general-purpose toolkit is what produced the current problem. A dedicated native
surface is ~300 lines and removes the whole class of "the OSD is flaky."

**Fallback:** if the native overlay proves painful, an Avalonia borderless window
behind the same `IVolumeOsd` interface is an acceptable compromise — implement the
interface first so the swap is cheap.

### 5.2 Interface (in `Volco.Core/Osd/`)

```csharp
public interface IVolumeOsd : IDisposable
{
    /// <summary>Thread-safe. Shows or updates the flyout and resets the auto-hide timer.</summary>
    void Show(VolumeChangeNotification notification);
}
```

`VolumeChangeNotification` already exists and is already unit-tested — keep it,
along with `SliderProcessor.BuildSliderLabel` and its tests.

### 5.3 Implementation spec (`Volco.Osd.Windows`)

- **Window:** a plain `WS_POPUP` window, class registered once, created once at
  startup and **kept alive hidden** for the process lifetime. A bare Win32 HWND
  costs essentially nothing idle — unlike a XAML window.
- **Extended styles:** `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
  WS_EX_TOPMOST | WS_EX_LAYERED`. The first three are what today's code already
  sets and are correct; keep them.
- **Do not use `UpdateLayeredWindow`.** It forces a GPU→CPU readback and is slow.
  The pill is an *opaque rounded rectangle*, so per-pixel alpha is not needed.
  Use `WS_EX_LAYERED` **only** with `SetLayeredWindowAttributes(hwnd, 0, alpha,
  LWA_ALPHA)` — whole-window alpha, composited by DWM, cheap. That is the fade-out
  mechanism.
- **Rounding:** `DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
  DWMWCP_ROUND)` — already done in the current code and worth keeping. Optionally
  `DWMWA_SYSTEMBACKDROP_TYPE` for acrylic on Windows 11.
- **Drawing:** Direct2D + DirectWrite onto a DXGI swap chain (or
  `ID2D1DCRenderTarget` on the window DC if simpler). Content: speaker/muted
  glyph, label text with ellipsis, a rounded progress bar, a percentage. Draw
  **only when the value changes** — no continuous render loop, no timer-driven
  repaint.
- **Update coalescing:** cap repaints at 60 Hz. The 20 ms slider coalescing
  already limits inbound events to ~50 Hz, but the OSD must not assume that.
- **Auto-hide:** 1500 ms after the last update (current behaviour), then fade
  alpha 255→0 over ~250 ms via `SetLayeredWindowAttributes` stepped on a timer, or
  via a DWM transition. Then `ShowWindow(SW_HIDE)`. **Do not** run a
  `DoubleAnimation` with `EnableDependentAnimation = true` — today's code does,
  and that is a CPU-side animation by definition.
- **Positioning:** bottom-centre of the work area of the monitor containing the
  cursor (or the foreground window). Use `MonitorFromPoint` /
  `GetMonitorInfo` + `GetDpiForWindow`. Declare **per-monitor DPI awareness v2**
  in the app manifest. Reposition only on the hidden→visible transition, which is
  what the current code correctly does to avoid flashing — preserve that logic.
- **Threading:** the overlay owns its own thread with a message pump, or lives on
  the UI thread. `Show` posts a message; it must be callable from the
  `SliderProcessor` coalesce thread without blocking.

### 5.4 Behavioural parity to preserve

From `docs/superpowers/specs/2026-06-07-volume-osd-design.md`, which remains the
authority on *what* the OSD shows:
- Triggers: current output device, specific device, process sliders, unmapped
  slider, and mute toggles.
- Label rules per `BuildSliderLabel` (device name / current output / friendly
  process names / "Other apps").
- Mute kind shows the muted glyph and "Muted"/"On" in place of the percentage.
- Never steals focus, never blocks clicks.

---

## 6. Build, packaging, and CI

### 6.1 Publish

Baseline (must work before anything fancier is attempted):

```
dotnet publish src/Volco.App/Volco.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

**The output must remain a single `Volco.exe`.** The in-app updater depends on it:
`GitHubUpdater.AssetName = "Volco.exe"`, `ArtifactName = "Volco-win-x64"`, and
`UpdateInstaller.ApplyAndRestart` writes a batch script that `move /Y`s one file
over the running exe and relaunches. Breaking the single-file shape breaks
updating for every existing install.

**NativeAOT is a stretch goal, not a requirement.** Avalonia supports it and it is
where the startup and working-set wins are, but it needs investigation:
`System.Text.Json` needs a source-generated `JsonSerializerContext`
(`ConfigManager` currently uses reflection-based serialization);
`System.IO.Ports` AOT compatibility must be verified; Avalonia needs trimming
descriptors so reflection-based binding is not stripped. Do this **after** the
app works, and keep the single-file self-contained build as the shipping default
until AOT is proven.

### 6.2 CI (`.github/workflows/build.yml`)

- **Test job (ubuntu-latest):** unchanged in shape, retargeted to
  `tests/Volco.Core.Tests/`. It now covers *more* of the codebase than before,
  because Avalonia itself is cross-platform. Consider adding
  `Avalonia.Headless.XUnit` view-model/view smoke tests later.
- **Build job (windows-latest):** replace the `msbuild` invocation with
  `dotnet publish` as above. **`microsoft/setup-msbuild` can be removed** —
  Avalonia does not need Visual Studio Build Tools. This also removes the
  Windows App SDK restore from CI.
- Keep the existing job-triggering rules exactly as they are (per `CLAUDE.md`:
  tests on all branches, build/publish only on `main` or explicit
  `workflow_dispatch` with `run_build=true`).
- Keep `release.yml`'s version-stamping contract: `VolcoVersion` and
  `VolcoReleaseChannel` msbuild properties feed `BuildInfo` via
  `AssemblyMetadata`. Carry those `PropertyGroup`s into `Volco.App.csproj`
  verbatim.

### 6.3 Manifest

Carry `app.manifest` over (`asInvoker`, `uiAccess=false`) and **add per-monitor
DPI awareness v2**, which the OSD needs and the current manifest lacks.

---

## 7. Implementation phases

Each phase should leave the repo building and green on CI.

1. **Scaffold the split.** Create `Volco.Core` + `Volco.Core.Tests`; move the
   verbatim files and fix namespaces. Old app still builds against the new core.
   *Green CI with zero behaviour change.*
2. **Define `IAudioEngine` + `FakeAudioEngine`.** Retarget `SliderProcessor` and
   `Actions/` to it; write an adapter from the existing `AudioManager` so the old
   app keeps working. Port and extend the tests. *Still zero behaviour change.*
3. **Config schema v2 + `ConfigMigrator`**, with tests. *Linux-testable.*
4. **`Volco.Audio.Windows`** — the real COM engine (shared spec §2). Delete
   `AudioManager`, `SessionMonitor`, `svcl.exe`. **First measurable win; take
   measurements here** per shared spec §8, because this isolates the audio fix
   from the UI change.
5. **`Volco.Osd.Windows`** — the native overlay behind `IVolumeOsd`. **Second
   measurable win.**
6. **`Volco.App`** — Avalonia shell, tray, then the five settings pages one at a
   time. Delete the old `src/Volco` project last.
7. **CI + packaging**, then the full measurement run.
8. *(Optional)* NativeAOT investigation.

Phases 4 and 5 are the ones that move the numbers. Phase 6 is the largest in
lines but the lowest in risk.

---

## 8. Risks and open questions

Things the implementer should resolve with a short spike, not guess at:

| Risk | Mitigation |
|---|---|
| Avalonia `TrayIcon` left-click behaviour on Windows | Spike it in phase 6 first thing. Fallback: `Shell_NotifyIcon` P/Invoke in `Volco.Osd.Windows`. |
| Avalonia version churn (11.x vs 12) | Pin a version at phase 6 start; check 12's stability and its NativeAOT claims before committing. |
| NativeAOT + reflection-based `System.Text.Json` config | Deferred to phase 8; source-gen context if pursued. |
| `IPolicyConfig` vtable/GUIDs | Shared spec §6 — verify against a reference header; failures non-fatal. |
| Direct2D interop surface in C# | Use CsWin32 for both the audio COM and the D2D/DWrite calls; it is the same generator either way. |
| No local Windows build (per `CLAUDE.md`) | Every phase must be CI-verifiable; manual Windows checks are batched at phase ends. This is why the phases are ordered to keep CI green throughout. |
| Regression in ported settings UI | The ViewModels are tested indirectly at best today. Add `Avalonia.Headless` smoke tests for at least the Sliders and Buttons pages. |

---

## 9. Acceptance criteria

1. Full measurement run recorded in `MEASUREMENTS.md` (shared spec §8), against a
   `main` baseline.
2. Idle CPU average < 0.1 %; idle GPU 0; handle count flat across a 60 s sweep.
3. Cold start to tray icon < 1 s.
4. All nine original test files pass, plus new `ConfigMigrator`,
   `SliderProcessor`-against-`FakeAudioEngine`, and `SessionFilter` tests.
5. The Windows manual checklist in shared spec §7 passes.
6. A v1 `config.json` from an existing install migrates silently, with a
   `config.v1.bak` written.
7. The ESP32 firmware and `tools/simulator/index.html` work against the new build
   with no changes — including LED feedback (`0x03`, `0x04`).
8. In-app update from a published release still works end-to-end.
9. No `svcl.exe`, no bundled third-party executables.
10. README updated: tech stack, architecture diagram, build instructions.

---

## 10. Out of scope

- Shipping Linux or macOS support.
- Any change to the wire protocol or the ESP32 firmware.
- Redesigning the settings UX. This is a port of the existing five pages, not a
  redesign. Visual polish beyond matching today's design tokens is a separate
  piece of work.
- Replacing the update mechanism.

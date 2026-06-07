# Volume OSD Flyout — Design

**Date:** 2026-06-07
**Status:** Approved

## Goal

Show an on-screen volume indicator when the user changes volume via the hardware
controller. Covers the current output device, any specific device, per-process /
app sliders, and mute toggles.

## Background / Why custom

The native Windows volume flyout has **no public API** to invoke — it is rendered
by the shell only in response to hardware media-key input, and it can only ever
represent the **default render device's master volume** in ~2% steps. Volco sets
arbitrary absolute volumes on specific devices and per-process sessions, none of
which the native OSD can represent. Therefore we draw our own flyout (the same
approach used by EarTrumpet and Microsoft's Core Audio "OSD" sample).

Today volume changes flow:
controller → `SliderProcessor.ProcessPending` → `AudioManager.SetDeviceVolume`
(direct `MasterVolumeLevelScalar` write) → no OSD.

## Design decisions (confirmed with user)

- **Triggers:** all of — current output device, specific named device,
  per-process / app sliders (Processes + Unmapped), and mute toggles.
- **Style:** minimal rounded pill, **bottom-center** of the screen.
- **Label:** yes — show the device / app name.

## Architecture

Three pieces, split along the existing Windows/Linux conditional-compilation
boundary (`Volco.csproj` excludes `UI/**`, `App.xaml.cs`, `Audio/AudioManager.cs`
on non-Windows) so the protocol/input layers keep building and testing on Linux CI.

### 1. Platform-neutral notification + label logic (builds & tests on Linux)

New file `src/Volco/Input/VolumeChangeNotification.cs`:

```csharp
public enum OsdKind { Volume, Mute }

public sealed record VolumeChangeNotification(
    string Label,   // device or app name shown on the pill
    float Level,    // 0..1, used for the bar + percentage
    bool Muted,     // true => show muted glyph / dimmed bar
    OsdKind Kind);
```

`SliderProcessor`:
- New `public event EventHandler<VolumeChangeNotification>? VolumeChanged;`
- Raised in `ProcessPending` after each slider is dispatched, with
  `Kind = Volume`, `Level = volume`, `Muted = false`, and a computed `Label`.
- New pure static helper for the label so it is unit-testable:

  ```csharp
  public static string BuildSliderLabel(SliderConfig slider, string? currentOutputName)
  ```

  Label rules:
  - `Device`, empty `DeviceName` → `currentOutputName` (the active output), or
    `"Output"` if null.
  - `Device`, non-empty `DeviceName` → `DeviceName`.
  - `Processes` → process names joined with ", ", `.exe` stripped, first letter
    upper-cased (e.g. `["spotify.exe"]` → `"Spotify"`). Empty list → `"Apps"`.
  - `Unmapped` → `"Other apps"`.

  For the empty-`DeviceName` Device case, `ProcessPending` passes
  `_audio.GetDefaultOutputDeviceName()` as `currentOutputName`.

### 2. Windows-only OSD window (excluded on Linux via `UI/**` rule)

New files `src/Volco/UI/VolumeOsdWindow.xaml` + `VolumeOsdWindow.xaml.cs`:

- A WinUI 3 `Window`, borderless and transparent, configured as an overlay:
  - `AppWindow` + `OverlappedPresenter`: no title bar, not resizable, not in
    taskbar, always-on-top.
  - Extended window styles via P/Invoke on the HWND:
    `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW`
    so it never steals focus and is click-through.
- Content: a rounded `Border` pill (semi-transparent dark background,
  `CornerRadius`) with a horizontal layout:
  speaker `FontIcon` (muted glyph when `Muted`) · name `TextBlock` ·
  `ProgressBar` · percentage `TextBlock`.
- Positioning: bottom-center of the nearest display's work area via
  `DisplayArea.GetFromWindowId(WindowId, DisplayAreaFallback.Nearest)`, with a
  small bottom margin. Recomputed on each `Show` (handles multi-monitor / DPI).
- Public API: `void Show(VolumeChangeNotification n)`:
  1. Marshal onto the window's `DispatcherQueue` (callers may be on the
     coalesce-timer thread).
  2. Update icon / label / bar / percentage. For `Kind == Mute` or `Muted`,
     show the muted glyph and the text `"Muted"` / `"Unmuted"` in place of the
     percentage and dim the bar.
  3. Reposition, set opacity to 1, show **without activating**.
  4. (Re)start a single `DispatcherQueueTimer` (~1.5 s). On tick: run a fade-out
     opacity `Storyboard`, then hide the window.
- One reused instance — no per-event window/allocation churn. The 20 ms slider
  coalescing already throttles update frequency.

### 3. Wiring in `App.xaml.cs` (Windows-only)

- Create `VolumeOsdWindow` during `OnLaunched` (hidden); dispose it in the Exit
  handler alongside the other disposables.
- Subscribe: `_sliderProcessor.VolumeChanged += (_, n) => _osd.Show(n);`
- In `OnButtonPressDetected`, after `action.Execute()` (and alongside the
  existing `PushControllerState()`), for mute action types build a `Mute`
  notification and call `_osd.Show(n)`:
  - `MuteCurrentOutput` → label = `GetDefaultOutputDeviceName()`,
    `Muted = IsOutputMuted()`.
  - `MuteSpecificDevice` → label = the device name,
    `Muted = IsOutputMuted(actionConfig.DeviceId)`.
  - `MuteCurrentInput` → label = `"Microphone"`, `Muted = IsInputMuted()`.
  - Non-mute actions (device switch, etc.) → no OSD.

## Threading

Slider `VolumeChanged` fires on the `SliderProcessor` coalesce-`Timer` thread;
`VolumeOsdWindow.Show` marshals to the UI `DispatcherQueue` internally, so
callers never touch UI affinity. Button-press handling already runs through the
app's normal flow.

## Testing

- **Linux CI (unit):** `BuildSliderLabel` for each mapping type + edge cases
  (empty device name with/without a current-output name, empty process list,
  `.exe` stripping, multiple processes). Existing fake/stub `IAudioManager`
  pattern in the test project is reused where a current-output name is needed.
- **Windows CI:** build/publish must succeed with the new XAML window.
- **Manual:** confirm the pill appears bottom-center on slider moves and mute
  toggles, shows the right name/level, auto-hides after ~1.5 s, and never steals
  focus or blocks clicks. (Per `CLAUDE.md`, no local dotnet — verified on CI +
  manual.)

## Out of scope

- Simulating the native OSD via media keys (rejected — can't represent
  specific devices / per-process / exact values).
- User-configurable OSD position, theme, or timeout (could be added later).
- OSD for output-device-switch button actions.

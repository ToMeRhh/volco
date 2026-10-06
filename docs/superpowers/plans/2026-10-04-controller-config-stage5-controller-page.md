# Controller Config, Stage 5: Controller Page and Test Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Configure and test the controller from Volco.
- A sixth settings page, **Controller**, with five sections: status, hardware, test, behaviour and advanced. It edits a draft of the controller config and saves it through `ControllerConfigService`.
- Test mode in the runtime: raw input is shown live; no audio, action or OSD happens.
- `ButtonPressDetector.Reset` and the press/release pairing fix.
- The sidebar dot.

**Out of scope (stage 6):** Install/Update/Check firmware, the probe, the tray indicators, and the bundled firmware. Until then the status section shows the firmware version only, and `FirmwareUpdateAvailable` is always false, because there is no catalog. The dot is wired and tested with a fake value.

**Architecture:**
- **Core:**
  - `Input/ButtonPressDetector`: `Reset()`, plus a `_down` flag per tracker.
  - `Runtime/VolcoRuntime`: test mode (spec §8.5).
  - `Controller/ControllerDraft.cs`: pure helpers for the page:
    - which I2C buses are used
    - "modified" detection
    - the two hints
    - field descriptions for errors
    - the target text for an (item, field)
- **App:**
  - `ViewModels/Controller/`:
    - `ControllerViewModel` (status, banner, the editing model, save/revert, confirmations, test)
    - row view models: `HwSliderRowViewModel`, `HwButtonRowViewModel`, `I2cBusRowViewModel`
    - `TestInputViewModel`
  - `Views/Pages/ControllerPage.axaml`
  - **Confirmations are inline:** the view model exposes one pending `Confirmation` (message, confirm label, Confirm and Cancel commands), and the page shows it as a panel above the footer. The app has no dialog windows, and this keeps every confirmation testable in the view model.
- **The draft** is a `Volco.Core.Controller.Proto.ControllerConfig`.
  - Row view models read and write it directly.
  - Every edit raises `DraftChanged`. The page view model then recomputes dirty, the local validation result, the inline error, the hints, the used buses and the "modified" caption.
  - Structural edits (add or remove a row, a preset, revert, rebase) rebuild the row view models.

**Tech Stack:** .NET 8 (CI only), Avalonia 12, CommunityToolkit.Mvvm, xUnit (Core) / xUnit v3 + Avalonia headless (App).

**Spec:** §8.4, §8.5, §8.7 (minus the firmware buttons), §12.3 test-mode bullets, §12.4, §12.7 items 2, 3, 4, 6, 8, 11 (stage 6), 12 and 13.

## Global Constraints

- **Tools:** `/usr/bin/git`; no local `dotnet`; Write tool for files.
- **`Volco.App` stays platform-free** (`ArchitectureTests`).
- **Threading:** every runtime and service event is marshalled with `IUiDispatcher.Post` (never Invoke). The service raises them on transport threads (stage 4b review, minor 8).
- **Strings:** the §8.3 banner texts and the §8.7 error texts are used verbatim.
- **Every new control has a `Name`.** The App tests find controls by name, as the existing page tests do.
- **Pins are entered as numbers.** An empty box means absent, which the validator reports.

## Review Focus

1. **Test mode can't leak:**
   - no audio call, action or OSD during test mode
   - a button held across entry or exit fires nothing
   - test mode ends on page leave, window hide, Detach, replacement and dispose
2. **The editing-allowed rule (§8.4):** a draft based on a stale mirror can never be saved over a different controller setup without the user choosing an action.
3. **Dirty** is `Encode(draft) ≠ baseline`. A mirror with unknown fields is not dirty. "Apply mine" sends the mirror bytes unchanged.
4. **Inline errors** map each (item, field) to the right row, for both local validation and a ConfigResult INVALID.

---

### Task 1: `ButtonPressDetector.Reset` and press/release pairing (Core)

**Files:** `src/Volco.Core/Input/ButtonPressDetector.cs`, `tests/Volco.Core.Tests/Input/ButtonPressDetectorTests.cs`

- `public void Reset()`: every tracker cancels and disposes both CTSs and clears `_longPressFired`, `_waitingForDoublePress` and `_down`, under its lock.
- **`_down`:** set in `OnPressed`, cleared in `OnReleased`. `OnReleased` returns at once when it is false.
- **Timer callbacks** also check that their CTS is still the tracker's current one, so a callback already past `Task.Delay` when `Reset` ran fires nothing.

- [ ] **Step 1 (RED):**
  - `UnmatchedRelease_FiresNothing`
  - `Reset_CancelsAPendingLongPress` (press, Reset, wait > long press → no event)
  - `Reset_CancelsAPendingSingle` (press, release with a 100 ms double window, Reset, wait → no event)
  - `Reset_ThenRelease_FiresNothing` (press, Reset, release → no event, with window 0 and with 100)
  - `AfterReset_NormalPressesWork`
- [ ] **Step 2 (GREEN):** Implement. Commit `feat(input): ButtonPressDetector.Reset and press/release pairing (spec 8.5)`.

### Task 2: Test mode in `VolcoRuntime` (Core)

**Files:** `src/Volco.Core/Runtime/VolcoRuntime.cs`, `src/Volco.Core/Runtime/TestMode.cs` (`TestIndicator`, `RawInputEventArgs`), `tests/Volco.Core.Tests/Runtime/VolcoRuntimeTestModeTests.cs`

**API** (spec §8.5):
```csharp
public enum TestIndicator { Off, On, A, B }
public enum RawInputKind { Slider, Button }
public sealed class RawInputEventArgs : EventArgs { RawInputKind Kind; int Index; int Value; }   // Value: 0..4095, or 1/0 pressed
public bool InTestMode { get; }
public event EventHandler<bool>? InTestModeChanged;
public IDisposable EnterTestMode();      // a second call while in test mode returns another handle to the same session
public event EventHandler<RawInputEventArgs>? RawInput;
public void SetTestIndicator(int button, TestIndicator state);
```

- **State:** a `_testMode` int (0/1, Interlocked) and a `TestIndicator[]` guarded by a small lock. The handle's `Dispose` exits only the session it entered (a generation number), so a stale handle can't end a later session.
- **Entry:**
  1. set the flag
  2. clear the indicators
  3. `ButtonPressDetector.Reset()`
  4. `_link.PushAll()`
  5. raise `InTestModeChanged(true)`
- **Exit:**
  1. clear the flag
  2. `Reset()` again
  3. `PushAll()`
  4. raise `InTestModeChanged(false)`
- **`OnMessageReceived`:** in test mode, Slider and Button messages raise `RawInput` and return before `SliderProcessor`, `ButtonActivity` and `ButtonPressDetector`. `OnButtonPressDetected` returns early in test mode.
- **Compute lambda:** in test mode, `ControllerDisplay.Compute` is replaced by `TestDisplays()`, which ignores the announce step. It reads the kinds from `Config.Config.Controller?.Buttons`:
  - LED: `LedDisplay(0 Off / 1 On / 1 A / 2 B)`
  - LED pair: `LedDisplay(0 / 1 / 1 / 2)`
  - screen: `ScreenDisplay(ScreenContent.Text("Test", ScreenIcon.Generic, false, $"Button {n}"))`
  - none: null
- **The runtime ends test mode itself:** in `StartConnection` (old or displaced), in `OnConnectionStatusChanged` when not Connected (Detach), and in `Dispose`.

- [ ] **Step 1 (RED):** one test per §12.3 test-mode bullet:
  - Slider and Button messages → zero `IAudioEngine` calls, zero actions, `RawInput` raised
  - a button held across entry: its long-press timer doesn't fire
  - held across exit: the release fires nothing, with `doublePressWindowMs` 0 and non-zero
  - `RawInput` isn't raised outside test mode
  - `SetTestIndicator` → the expected Led frames
  - a screen controller gets the test Screen frames
  - exit → `PushAll` restores the real Led values
  - Detach, `Reconnect()` and `Dispose` end test mode and raise `InTestModeChanged(false)`
  - a stale handle's `Dispose` doesn't end a newer session
- [ ] **Step 2 (GREEN):** Implement. CI green. Commit `feat(runtime): test mode - raw input, test indicators, nothing reaches audio or actions (spec 8.5)`.

### Task 3: `ControllerDraft` helpers (Core)

**Files:** `src/Volco.Core/Controller/ControllerDraft.cs`, `tests/Volco.Core.Tests/Controller/ControllerDraftTests.cs`

```csharp
public static class ControllerDraft
{
    public static IReadOnlyList<int> UsedBuses(ControllerConfig c);                 // buses some screen uses, ascending, only 0..3
    public static bool IsModified(ControllerConfig draft, ControllerPreset preset);  // hardware fields encode differently
    public static ControllerPreset? BasedOn(ControllerConfig draft);                 // the preset named by draft.Preset, if any
    public static bool HasHardware(ControllerConfig c);                              // any slider, button or I2C bus
    public static IReadOnlyList<DraftHint> Hints(ControllerConfig c, ChipInfo chip);
    public static string FieldDescription(ConfigField field);                        // "pin", "LED pin", "screen address", …
    public static string Target(byte item, ConfigField field);                       // "Slider 1", "Button 2", "I2C bus 0", "Wi-Fi", "Screen behaviour", "Input tuning"
}
public sealed record DraftHint(DraftHintKind Kind, int Index);   // ButtonNoPullUp (GPIO 34–39), SliderAdc2 (ADC2 while UDP is off)
```

- **Numbering:** user-visible numbers are 1-based ("Slider 1" is index 0), as on the Sliders and Buttons pages. Check this against `SlidersPage`'s header binding.
- **`IsModified`** compares `Encode(hardware fields of draft)` with `Encode(preset.Hardware)`. The hardware fields are sliders, buttons and `i2c0`..`i2c3`; `preset` itself is ignored.

- [ ] **Step 1 (RED):**
  - `UsedBuses` with screens on buses 2 and 0, bus 7 ignored
  - `IsModified` false for the DIY preset applied to a blank draft (with behaviour fields set), and true after a pin change
  - `BasedOn` for `diy-reference`, `blank` and an unknown name
  - `Hints` for a button on 35 and a slider on 25 with UDP off; none when UDP is on (the validator reports that)
  - `Target` and `FieldDescription` for every `ConfigField`
- [ ] **Step 2 (GREEN):** Implement. Commit with Task 2 or separately.

### Task 4: `ControllerViewModel`, the editing model (App)

**Files:**
- create `src/Volco.App/ViewModels/Controller/{ControllerViewModel.cs,HwSliderRowViewModel.cs,HwButtonRowViewModel.cs,I2cBusRowViewModel.cs,TestInputViewModel.cs,Confirmation.cs}`
- modify `src/Volco.App/ViewModels/SettingsViewModel.cs` (lazy `Controller`, disposed with the window VM)
- tests in `tests/Volco.App.Tests/ViewModels/ControllerViewModelTests.cs`

**State and rules** (all on the UI thread; service and runtime events are posted):

- **Status:**
  - `StatusText` (from `ControllerState`)
  - `ChipText` ("ESP32" / "Unknown")
  - `FirmwareText` (the Hello's id; "Development firmware" for `dev`)
  - `BannerText` and `BannerActions`, from `ControllerConfigService.Banner` through a fixed map of §8.3 strings
  - one command per action: UseControllers → `ReadAsync(adopt: true)`; Retry → `RetryReadAsync`; ApplyMine/ApplySaved → `SaveAsync(mirror bytes)`; SetUp/SetUpFromScratch → a new Blank draft with edit permission; FixIt → a draft decoded from the mirror with permission, always dirty, errors shown; Update → hidden (stage 6)
- **`Draft`, `Baseline`, `IsDirty`:**
  - `Baseline = Encode(Decode(mirror))`, or empty when there is no mirror or it doesn't decode
  - `IsDirty = FixItDraft || !Encode(draft).SequenceEqual(Baseline)`
- **`CanEdit`:**
  - `runtime.ControllerState == Ready` **and**
  - (in sync — Hello state 1 and CRC = mirror CRC — **or** `_newDraftPermission` is set)
  - **The permission ends** when the mirror changes, or a Hello arrives with a different (state, crc).
  - When `CanEdit` is false, the rows are read-only and show the mirror's decode, or Blank when there is none.
- **Mirror change** (`MirrorChanged`):
  - clean → rebase the draft on the mirror
  - dirty → keep it, and set `Notice = "The controller's setup changed. Revert to load it."`
- **Save** (`SaveCommand`, enabled when `CanEdit && IsDirty && !InTestMode && Operation == Idle`):
  1. Run the local validator. On a violation, set the inline error and stop.
  2. If `udp` differs from the baseline's and the connection type is UDP, ask the §8.4 UDP confirmation first.
  3. `Encode` the draft, then `await SaveAsync`.
  4. Progress: Sending… → Restarting… (on `Confirming`) → "Saved. The controller restarted with the new setup.", or the §8.7 error message. For INVALID, set the inline error from (item, field).
- **Revert:** the draft = the mirror's decode, and the notice clears.
- **Presets:** `Presets` (DIY reference, Volco PCB disabled, Blank) and `ApplyPresetCommand(preset)`. When `HasHardware(draft)`, it asks "Replace the current hardware with <preset>?" first. It uses `ControllerPresets.ApplyTo`, which keeps behaviour and UDP.
  - `BasedOnText`: "Based on DIY reference", or "Based on DIY reference (modified)", or empty.
- **Rows:**
  - `Sliders`, `Buttons` and `UsedBuses` (I2C rows), with `AddSlider`, `AddButton`, and `Remove` per row.
  - Removing a row that has later rows asks: "Sliders after it are renumbered, and their actions move with the number." (or "Buttons …").
  - Each row has `ErrorText` (inline) and `HintText`.
- **Behaviour and Advanced** bind straight to `draft.Screens` / `draft.Input` / `draft.Udp` (created on first edit) through properties with the defaults applied. `ShowBehaviour` is true when some button has a screen. `PcPort` is pre-filled from `Connection.Udp.ListenPort` when UDP is first enabled.
- **Test:**
  - `StartTestCommand`/`StopTestCommand` keep the handle from `EnterTestMode()`.
  - `TestSliders` (raw value, bar 0–1) and `TestButtons` (pressed lamp; for LED buttons an Off/On or Off/A/B choice calling `SetTestIndicator`) come from `Config.Controller`.
  - `RawInput` is posted to the UI thread.
  - `InTestModeChanged(false)` from the runtime clears the handle.
  - `OnPageLeft()` and `Dispose()` stop the test.
- **`FirmwareUpdateAvailable`:** from the banner (false until stage 6).

- [ ] **Step 1 (RED):** `ControllerViewModelTests` with the headless `TestRig` (a runtime over a fake connection) and an inline dispatcher. One test per §12.4 view-model bullet that stage 5 owns:
  - the editing-allowed rule: not Ready → read-only; in sync → editable; a different CRC → read-only until SetUpFromScratch; the permission ends on a different Hello
  - dirty against the baseline: a mirror with an unknown field (`98 06 01` + DIY) loads clean; one pin change is dirty; changing it back is clean
  - a clean draft is rebased on a mirror change; a dirty one is kept, with the notice
  - a preset applied keeps the behaviour and UDP fields; the confirmation appears only when the draft has hardware
  - the "modified" caption
  - inline error mapping: a local slider pin 6 → slider 1's row; a ConfigResult INVALID (5, 0, 2) → Button 1's row, plus the footer message from §8.7
  - the remove-row renumber confirmation: only when later rows exist; Cancel keeps the row
  - Save is disabled during test mode and while an operation runs
  - the UDP-change warning appears only on a UDP connection with changed UDP fields
  - ApplyMine sends the mirror bytes unchanged (the burst's chunk data equals the mirror)
  - FixIt is dirty and shows the local validator's error
  - test mode: Start enters it; RawInput updates `TestSliders[i].Value`; the LED choice sends a Led frame; `OnPageLeft` exits; a runtime-initiated exit clears `IsTesting`
  - `FirmwareUpdateAvailable` → `SettingsViewModel.ControllerNavDot` (with a fake banner)
- [ ] **Step 2 (GREEN):** Implement. CI green. Commit `feat(app): Controller page view model - editing model, presets, save, test (spec 8.4, 8.7)`.

### Task 5: `ControllerPage` view, navigation and the sidebar dot (App)

**Files:** `src/Volco.App/Views/Pages/ControllerPage.axaml(.cs)`, `src/Volco.App/Views/SettingsWindow.axaml(.cs)` (nav entry after Connection, tag `controller`, icon `Icon.Chip` added to `Styles/Icons.axaml`; the `NavItem` gains `HasDot`; `OnNavSelectionChanged` calls `Controller.OnPageLeft()` when leaving the page, and so does hiding the window), and `tests/Volco.App.Tests/Pages/ControllerPageTests.cs`, `TabOrderTests` and `SettingsCoverageTests` updates.

**Layout** (the existing `Panel`/`Title`/`Secondary`/`Caption` classes; `PagePadding`, `SpaceM`, `PageMaxWidth`):
1. **Status panel:**
   - state dot and text, chip, firmware
   - the banner (`BannerPanel`) with its action buttons (`UseControllersButton`, `ApplyMineButton`, `RetryButton`, `SetUpFromScratchButton`, `ApplySavedButton`, `SetUpButton`, `FixItButton`)
   - the notice
2. **Hardware panel:**
   - `PresetCombo` and `BasedOnText`
   - the sliders list (`AddSliderButton`; per row: "Slider N", `PinBox`, remove button, `ErrorText`/`HintText`)
   - the buttons list (per row: pin, `IndicatorCombo`, then the indicator's fields)
   - I2C bus rows (SDA, SCL)
3. **Test panel:** `StartTestButton`/`StopTestButton`, the note, a bar plus value per slider, a lamp per button, and the LED choices.
4. **Behaviour panel** (when `ShowBehaviour`; otherwise one line).
5. **Advanced:** an `Expander`, collapsed — input tuning, the I2C clock per used bus, and Wi-Fi with a `PasswordBox`-style `TextBox` (`PasswordChar`, show toggle).
6. **Footer** (sticky, `IsVisible="{Binding IsDirty}"` or while a save runs):
   - `SaveButton`, `RevertButton`, `ProgressText`
   - the confirmation panel (`ConfirmText`, `ConfirmButton`, `CancelConfirmButton`)

- [ ] **Step 1 (RED):** `ControllerPageTests` (headless):
  - the page builds; the Controller nav entry exists after Connection
  - read-only rows when not Ready
  - after a Hello in sync with the mirror, editing a pin shows the footer, and Revert hides it
  - the banner buttons follow `BannerActions`
  - the Behaviour panel is shown only with a screen button
  - Advanced is collapsed by default
  - leaving the page ends test mode
  - the nav dot follows `ControllerNavDot`
  - every named control is in the tab order (`TabOrderTests`)
- [ ] **Step 2 (GREEN):** Implement. CI green. Commit `feat(app): Controller page - status, hardware, test, behaviour, advanced (spec 8.7)`.

### Task 6: Docs, spec and the hardware checklist

- [ ] `docs/superpowers/checklists/2026-10-04-controller-config-hardware.md`: the §12.7 items, marked for stage 5 (2, 3, 4, 6, 8, 12, 13) and stage 6 (1, 5, 5a, 5b, 7, 9, 10, 11).
- [ ] CLAUDE.md:
  - the settings window has six pages; the Controller page summary
  - test mode in `VolcoRuntime`
  - `ButtonPressDetector.Reset`
- [ ] Push; CI green.
- [ ] **[USER-RUN]** Hardware on Windows: items 2, 3, 4, 6, 8, 12 and 13, with a CI-built exe from this branch. The user asks for the build job explicitly: it is never triggered without being asked.

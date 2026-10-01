# Volco UX Redesign — Implementation Plan

> **For agentic workers:** execute task by task, in order. Steps use checkbox (`- [ ]`) syntax for tracking. Each task ends with the solution compiling and **both test projects green on CI** (push, then `gh workflow run build.yml --ref claude/kind-hawking-pjhbrp` or let the push trigger it). Never run `dotnet` locally (CLAUDE.md).

**Goal:** Implement the approved spec `docs/superpowers/specs/2026-10-01-volco-ux-redesign.md` (the "spec"). The mockup `docs/superpowers/mockups/2026-10-01-ux-redesign-mockup.html` is the visual reference.

**Architecture:**
- Core goes first: config v3, actions and presets, runtime, protocol.
- Then firmware and the simulator.
- Then the App: tokens and controls, shell, pages, OSD.
- Core tasks adapt the App only as much as is needed to compile and keep existing tests meaningful. The App redesign lands in Tasks 7–13.

**Tech stack:** .NET 8 (Core, App), Avalonia 12.1.2, CommunityToolkit.Mvvm, xUnit 2 (Core tests), Avalonia.Headless.XUnit / xUnit v3 (App tests), PlatformIO/Arduino C++17 (firmware), plain HTML/JS (simulator).

## Global Constraints (verbatim from the Step 3 plan)

- **The PR may modify only `src/Volco.App/**` and `tests/Volco.App.Tests/**`** (plus the UX spec, plan, checklist and screenshots under `docs/`). This is the test of the decoupling. If a change seems to require touching `Volco.Core` or `Volco.Windows`, **stop and report it to the user** with the reason. That report is a finding, not a failure to hide.
- All Step 2 Global Constraints still apply: no P/Invoke in `Volco.App`, `x:DataType` everywhere, icons from `Icons.axaml`, CI-only verification, `/usr/bin/git`.
- Not in scope: new features that need new runtime or engine capabilities (e.g. per-app level meters, start-with-Windows, cursor-monitor OSD). Task 1 may *propose* them, but lists them separately as "needs engine work" for a future spec.

### Amendment approved by the user (spec §0), which overrides the first bullet

- The PR **may** modify `src/Volco.App/`, `src/Volco.Core/`, `tests/`, `firmware/`, `tools/simulator/` and `docs/`.
- It **may not** modify `src/Volco.Windows/`. If a change seems to require it, stop and report.
  - Known and accepted: the doc comment at `src/Volco.Windows/Audio/AudioEngine.cs:125` mentions `ControllerStateReporter`, which this plan deletes. Leave it; it is a comment only.
- The wire protocol and firmware change without backwards compatibility.
- The current hardware (3 buttons: two with a mute LED and one with an A/B LED pair; 5 sliders) must remain a valid firmware configuration.
- The "new runtime capabilities" exclusion is narrowed: the preset, button-activity and feedback runtime features in the spec §4.2 are in scope. The spec §6 items are not.

## Review Focus (verbatim from the Step 3 plan, with #2 and #5 amended)

1. **A redesigned page drops a setting that existed before,** so a user loses the ability to configure something → Task 3: `SettingsCoverageTests` asserts every `AppConfig` property the Step 2 pages edited is still editable (list derived in Task 1's inventory).
2. **A redesign changes copy or layout so that an existing `config.json` displays differently from what it does** (e.g. a friendly-name-only device no longer shows as selected) → Step 2's `ButtonsPage_FriendlyNameOnlyDevice_IsSelected` and siblings must keep passing unmodified, or be modified only with the user's approval noted in the PR.
3. **Accessibility regressions** (keyboard-only navigation, focus visibility, contrast) → Task 3: tab-order test per page, plus contrast checked against the token table in the UX spec.
4. **OSD redesign steals focus or blocks clicks** → Step 2's `OsdWindow_*` tests keep passing unmodified.
5. **The redesign reaches outside `Volco.App`** → Task 3 final step: `git diff --name-only main... | grep -v '^src/Volco.App/\|^tests/Volco.App.Tests/\|^docs/'` must print nothing.

**Amendments:**
- **#1:** the coverage test is Task 14 here. Its list is the spec §1 table.
- **#2:** the Output Devices page is removed by the approved spec. These Step 2 tests are **deleted or rewritten**, which this approval of the plan counts as user approval for. The PR body must list them:
  - `tests/Volco.App.Tests/Pages/OutputDevicesPageTests.cs`: deleted. Its friendly-name-only cases move to the toggle action's A/B pickers in `ButtonsPageTests`, as new tests with the same assertions.
  - `ButtonsPageTests` and `SlidersPageTests`: rewritten for the card UI. `ButtonsPage_FriendlyNameOnlyDevice_IsSelected` keeps its **name and assertion** (a friendly-name-only device shows as selected); only its setup and lookups change.
  - Core tests of `ControllerStateReporter` and `EncodeMuteState`/`EncodeActiveDevice`: replaced by `ButtonFeedback` and `EncodeButtonFeedback` tests.
- **#5:** the check becomes `git diff --name-only main... | grep '^src/Volco.Windows/'`, which must print nothing.

---

### Task 0: Mark the spec approved

- [ ] Set the spec's **Status** to "Approved 2026-10-01".
- [ ] Commit this plan.

### Task 1: Config schema v3 (Core)

**Files:** `src/Volco.Core/Config/AppConfig.cs`, `ConfigMigrator.cs`, `ConfigManager.cs`, `DeviceRefConfig.cs` (doc comment); `tests/Volco.Core.Tests/Config/*`.

- [ ] **`AppConfig`:**
  - `CurrentSchemaVersion = 3`.
  - `SliderMappingType.None`, the "Not set up" state.
  - `PresetConfig { string Id; string Name }` and `AppConfig.Presets`.
  - `ButtonConfig.Actions : Dictionary<string, ButtonBindingConfig>`, where `ButtonBindingConfig { SinglePress, DoublePress, LongPress }`. Remove `ButtonConfig.SinglePress/DoublePress/LongPress`.
  - `ButtonActionConfig.DeviceB` and `ButtonActionConfig.PresetId`.
  - `ButtonActionType.NextPreset` and `ButtonActionType.GoToPreset`.
  - Remove `OutputDevicesConfig` and `AppConfig.OutputDevices`.
- [ ] **`CreateDefault()`:** 5 sliders with `Type = None`, 3 buttons with an empty `Actions` map, and `Presets = [{ "default", "Default" }]`.
- [ ] **`ConfigMigrator.MigrateV2ToV3(JsonNode)`:** a pure transform on the raw JSON, because the v2 properties no longer exist on the types. It runs in `ConfigManager.Load` before deserialisation whenever `schemaVersion == 2`, and writes a `.v2.bak` backup the same way the v1 path does.
  - Press actions move to `actions["default"]`.
  - Every `ToggleOutputDevice` action gets `device = outputDevices.device1` and `deviceB = outputDevices.device2`.
  - Adds `presets`; drops `outputDevices`.
  - `index` is normalised to the list position.
- [ ] **v1 configs** still migrate v1→v2 in the runtime, because that step needs the device list. Restructure it so that v1 → v2 is done on the JSON (as today) and is then followed immediately by v2 → v3 before deserialising. One code path; tests cover v1 → v3 end to end.
- [ ] **`HealDeviceIds`** walks every action's `Device` and `DeviceB` in every preset, plus slider devices. It no longer walks `OutputDevices`.
- [ ] **On save**, `ConfigManager` rewrites `Index` to the list position (sliders and buttons).
- [ ] **Tests** (`ConfigMigratorTests`, `ConfigManagerTests`):
  - v2 → v3 with a fixture copied from a real v2 `config.json`, including the toggle pair moving into the action;
  - v1 → v3;
  - a v3 round trip;
  - `Index` normalisation;
  - `.v2.bak` written;
  - `HealDeviceIds` covers `DeviceB` and non-default presets.
- [ ] **App adaptation**, only enough to compile:
  - `ButtonItemViewModel` reads and writes `Actions["default"]`;
  - the toggle pair from Output Devices reads and writes the first toggle action's A/B (a temporary bridge, removed in Task 11);
  - the slider mapping-type combo maps `None` → "Unmapped" for now.
- [ ] **Commit:** `feat(core): config schema v3 — per-preset button actions, toggle pair on the action`.

### Task 2: Actions and presets (Core)

**Files:** `src/Volco.Core/Input/Actions/*`, new `src/Volco.Core/Runtime/IPresetController.cs`, `tests/Volco.Core.Tests/Input/Actions/ActionFactoryTests.cs`.

- [ ] **`IPresetController`:** `string ActivePresetId { get; }`, `void SetActivePreset(string id)`, `void NextPreset()`. `VolcoRuntime` implements it in Task 3; tests use a fake.
- [ ] **`ActionFactory.Create(ButtonActionConfig, IAudioEngine, IPresetController)`** no longer takes `ConfigManager`.
  - `ToggleOutputDeviceAction(audio, deviceA, deviceB)` replaces its `ConfigManager` dependency, and `ResolveTargetId(current, a, b)` follows. The current rule is unchanged: on A go to B; anywhere else go to A.
  - New `NextPresetAction` and `GoToPresetAction(presetId)`. A missing or deleted preset id is a no-op.
- [ ] **Tests:**
  - every action type;
  - the toggle A/B rule, including friendly-name-only A (keep `ToggleOutputDevice_Device1ConfiguredByFriendlyNameOnly_MatchesAndSwitchesToDevice2`, renamed `…DeviceAConfigured…`);
  - preset actions against a fake controller;
  - `GoToPreset` with an unknown id.
- [ ] **Commit:** `feat(core): toggle uses its own device pair; preset actions`.

### Task 3: Runtime (Core)

**Files:** `src/Volco.Core/Runtime/VolcoRuntime.cs`, `src/Volco.Core/Input/SliderProcessor.cs`, `src/Volco.Core/Input/VolumeChangeNotification.cs`, `tests/Volco.Core.Tests/Runtime/VolcoRuntimeTests.cs`, `tests/Volco.Core.Tests/Input/SliderProcessorTests.cs`.

- [ ] **`VolcoRuntime : IPresetController`:**
  - The active preset lives in memory and starts at `Presets[0]`.
  - `SetActivePreset` raises `ActivePresetChanged` and triggers `PushControllerState(force: true)`.
  - On a config change that removes the active preset, live moves to `Presets[0]`.
- [ ] **Button dispatch:** `config.Buttons[i].Actions[ActivePresetId]`. A missing key means no action.
  - The `Test` command path in the App calls a new `runtime.Execute(ButtonActionConfig)`, so preset actions work when tested.
- [ ] **`ButtonActivity(int index)` event**, raised for every raw press, configured or not, within the list bounds.
- [ ] **`SliderProcessor`:**
  - drop the 16-slot arrays and size by config (or use a dictionary keyed by index);
  - `None` sliders change no volume but still raise `SliderLevelChanged`;
  - the "Unmapped" label stays "Other apps".
- [ ] **Pending-switch OSD for toggle** resolves its target from the pressed action's A/B, replacing `OutputDevices`.
- [ ] **`OsdKind.Preset`:** `VolumeChangeNotification(label: preset name, level: 0, Muted: false, OsdKind.Preset)`, raised on preset change caused by a button.
- [ ] **Tests:**
  - dispatch follows the active preset;
  - `NextPreset` wraps;
  - `ActivePresetChanged` fires;
  - a forced re-push on preset change;
  - deleting the active preset;
  - `None` slider: no volume call, but a level event;
  - more than 16 sliders;
  - `ButtonActivity` for configured and unconfigured buttons;
  - the toggle OSD target taken from the action pair;
  - the preset OSD notification.
- [ ] **Commit:** `feat(core): active preset, activity events, unbounded sliders`.

### Task 4: Button feedback and protocol 0x05 (Core)

**Files:** delete `src/Volco.Core/Communication/ControllerStateReporter.cs`; new `src/Volco.Core/Communication/ButtonFeedback.cs`; `ProtocolEncoder.cs`; `ControllerMessage.cs` (`MessageType`); `VolcoRuntime.PushControllerState`; tests under `tests/Volco.Core.Tests/Communication/`.

- [ ] **`ButtonFeedback.Compute(AppConfig, string activePresetId, IAudioEngine) → IReadOnlyList<(int Button, byte Value)>`:**
  - one entry per button that has a stateful action in the active preset;
  - uses the first stateful action in the order Single → Double → Long;
  - values per spec §3.6.
  - Buttons with no stateful action get value 0 **once after a preset change**, so a light left on from the previous preset turns off. The runtime does this by sending 0 for every button absent from the new list. Covered by a test.
- [ ] **Protocol:** `MessageType.ButtonFeedback = 0x05` and `ProtocolEncoder.EncodeButtonFeedback(int button, byte value)`. Remove `MuteState`, `ActiveDevice` and their encoders.
- [ ] **`PushControllerState`** builds frames from `ButtonFeedback` and keeps the existing last-pushed dedup and post-connect re-push timers.
- [ ] **Tests:**
  - every action type's value;
  - first-stateful rule;
  - per preset;
  - toggle 1/2/0;
  - preset value = 1 + index;
  - the encoder frame bytes and checksum;
  - runtime pushes 0x05 frames, dedup, and clearing to 0 after a preset change.
- [ ] **Commit:** `feat(protocol): generic per-button feedback (0x05) replaces mute/active-device frames`.

### Task 5: Firmware

**Files:** `firmware/src/config.h`, `firmware/src/main.cpp`, new `firmware/README.md`.

- [ ] **`config.h`:**
  - a slider pin array;
  - a button table `{ pin, indicator }` with `IND_NONE`, `IND_LED(pin, polarity)` and `IND_LED_PAIR(pinA, pinB, polarity)`;
  - the current hardware expressed as that table (spec §4.4).
  - Remove `NUM_MUTE_BUTTONS`, `MUTE_BTN_*`, `DEVICE_SEL_*` and the mute/device naming.
- [ ] **`main.cpp`:**
  - loops sized by the tables;
  - boot sets every indicator to "off" honouring its polarity;
  - handles `0x05` by looking up the button's indicator: single LED on when value ≠ 0; pair 1 → A, 2 → B, 0 → both off; out-of-range index ignored;
  - remove the `0x03`/`0x04` handlers;
  - unknown types are ignored.
- [ ] **`firmware/README.md`:** how the code is structured, per spec §4.4. No build, flash or wiring content.
- [ ] **Verify:** `cd firmware && pio run` compiles. If PlatformIO isn't available in the session, say so in the PR and leave this to the manual checklist.
- [ ] **Commit:** `feat(firmware): per-button indicator table, 0x05 feedback, README`.

### Task 6: Web simulator

**Files:** `tools/simulator/index.html`.

- [ ] The number of sliders and buttons can be set on the page (default 5 and 3).
- [ ] Each button shows its last `0x05` value: one dot, plus an A/B pair view when it has received a 2.
- [ ] Remove the mute and device LED code.
- [ ] Manually smoke-test against the app over WebSocket; record the result in the checklist.
- [ ] **Commit:** `feat(simulator): configurable controls, 0x05 feedback`.

### Task 7: Design tokens, theme and icons (App)

**Files:** `src/Volco.App/Styles/{Colors,Brushes,Tokens,Typography,VolcoStyles,Icons}.axaml`; tests in `tests/Volco.App.Tests/Controls/`.

- [ ] **Colours:** the spec §3.7 token table, as Light and Dark theme dictionaries, plus a HighContrast dictionary mapping to system colours. HighContrast is applied when the OS reports high contrast; Avalonia exposes it through `PlatformSettings` colour values. If that isn't available headless, test the dictionary switch directly.
- [ ] **Type, spacing and shape:** type ramp (Title 20 / Subtitle 14 / Body 13 / Caption 12, Segoe UI Variable then the system fallback), spacing scale, radii.
- [ ] **Icons:** distinct `Icon.Volume` and `Icon.Speaker`; add `Icon.Grip`, `Icon.More`, `Icon.Plus`, `Icon.Preset`.
- [ ] **Tests:**
  - a token contrast test that computes WCAG ratios from the actual `Color` resources (text tokens ≥ 4.5 on `Surface` and `Bg`, `BorderStrong` and `Accent` ≥ 3), Light and Dark;
  - `Icon.Volume` ≠ `Icon.Speaker`.
- [ ] **Commit:** `feat(app): design tokens, high-contrast palette, icons`.

### Task 8: Shared controls (App)

**Files:** new `src/Volco.App/Controls/{ReorderableCardList,CardHeader,SegmentedControl,AdvancedSection,ChipInput,UndoToast}.*`; tests in `tests/Volco.App.Tests/Controls/`.

- [ ] **`ReorderableCardList`:**
  - an `ItemsControl` whose items are cards with a grip;
  - pointer drag: the dragged card follows the pointer via `RenderTransform`, and neighbours slide with a 180 ms transform transition;
  - on drop it calls `Move(from, to)` on the bound list (an `IReorderable` VM interface);
  - Alt+Up / Alt+Down on the grip;
  - the input numbers in the headers relabel while dragging;
  - honours reduce-motion (no transitions).
- [ ] **`SegmentedControl`:** single selection; keyboard arrows move selection; selected segment styled per spec.
- [ ] **`AdvancedSection`:** an expander with caption-style header, collapsed by default.
- [ ] **`ChipInput`:** chips with remove buttons plus an input. It reuses the Step 2 process autocomplete behaviour, including the fixes in commits `5fce0ce` and `ddb10cf`. Enter or picking a suggestion adds a chip.
- [ ] **`UndoToast`:** a host in the settings window; 6 s timeout; Undo invokes the callback.
- [ ] **Tests** (headless):
  - list `Move` via a simulated drag (pointer events) and via Alt+arrow;
  - segmented keyboard selection;
  - the advanced section starts collapsed;
  - chip add and remove, and autocomplete commit (port the Step 2 autocomplete regression tests);
  - toast undo and timeout (with an injectable clock).
- [ ] Replace `PressActionRow`'s `$parent[Border]` cast binding, one of the Step 3 first-commit candidates; it is rewritten in Task 11 anyway.
- [ ] **Commit:** `feat(app): reorderable card list, segmented control, chip input, undo toast`.

### Task 9: Shell (App)

**Files:** `src/Volco.App/Views/SettingsWindow.*`, `src/Volco.App/ViewModels/SettingsViewModel.cs`, `src/Volco.App/CompositionRoot.cs`; delete `Views/Pages/OutputDevicesPage.*`, `ViewModels/OutputDevicesViewModel.cs`, `Views/UpdateWindow.*` (moved into General in Task 13); tests in `tests/Volco.App.Tests/Shell/`.

- [ ] **Navigation:** Sliders, Buttons, Connection, General. The selected item has a `Surface` fill and an accent bar. The footer shows the connection status (dot + text), bound to `ConnectionStatusChanged`.
- [ ] **Tray:** "Check for Updates…" (release builds only, as today) opens Settings on the General page.
- [ ] **Step 3 first-commit candidates:**
  - a `CompositionRoot` test that the OSD is subscribed before `runtime.Start()`;
  - de-static `DeviceRefLookup`, then re-enable test parallelism in `Volco.App.Tests`.
- [ ] **Tests:**
  - four nav items in order;
  - footer status follows the runtime;
  - the tray update item opens General (via the `CompositionRoot` seam);
  - Settings window tab order (nav, then page).
- [ ] **Commit:** `feat(app): four-page shell with connection status footer`.

### Task 10: Sliders page (App)

**Files:** `Views/Pages/SlidersPage.*`, `ViewModels/SlidersViewModel.cs`, `ViewModels/SliderItemViewModel.cs`; `tests/Volco.App.Tests/Pages/SlidersPageTests.cs`.

- [ ] **Card list:**
  - header "Slider N" + summary + level bar + ⋯ + chevron;
  - an unset card has a dashed outline and the summary "Not set up".
- [ ] **Open card:**
  - Controls segmented: Apps / All other apps / A device (no segment selected when `None`);
  - Apps → `ChipInput`;
  - All other apps → hint listing excluded processes;
  - A device → picker with "Current default output" first;
  - `AdvancedSection` with Invert, Curve, Dead zone, Smoothing.
- [ ] **⋯ menu:** Move up, Move down, Reset, and "Remove last slider" (last card only). Add slider appends a `None` slider. Reset and Remove raise the `UndoToast`.
- [ ] **Live activity:** a `SliderLevelChanged` level updates the bar, and the card pulses (an accent outline for 150 ms; none when reduce-motion is on).
- [ ] **Tests:**
  - a v3 config renders each mapping type;
  - a friendly-name-only device shows as selected;
  - add, reset (with undo), remove-last;
  - reorder moves the config to the new index;
  - the "All other apps" hint lists the other sliders' processes;
  - tab order;
  - the level event updates only the matching card.
- [ ] **Commit:** `feat(app): sliders page — position-as-input cards`.

### Task 11: Buttons page and presets (App)

**Files:** `Views/Pages/ButtonsPage.*`, `ViewModels/ButtonsViewModel.cs`, `ViewModels/ButtonItemViewModel.cs`, new `ViewModels/PresetsViewModel.cs`, `ViewModels/ActionCatalog.cs`, `Controls/PressActionRow.*` (rewritten); `tests/Volco.App.Tests/Pages/ButtonsPageTests.cs`.

- [ ] **`ActionCatalog`:** groups, names, short labels, parameter kinds and light description per action type (spec §3.4 table), plus the disabled "Custom (coming later)" entry. Pickers render from it.
- [ ] **Preset bar:**
  - one preset → "Add preset" link only;
  - two or more → segmented tabs (editing) with a live dot bound to `ActivePresetChanged`, plus Add, Rename (inline) and Delete (with Undo);
  - banner "X is live" / "You are editing X… [Make X live]".
- [ ] **Button card:**
  - header "Button N" + summary for the edited preset + light preview (from `ButtonFeedback.Compute` over the **live** preset) + ⋯;
  - body: Press / Double press / Hold rows (picker + **Test** + inline parameters: device, A/B pair, preset), the "Controller light" line, and `AdvancedSection` with Hold after and Double-press window.
- [ ] **⋯ menu, add, undo:** as on the Sliders page. Reset clears actions in every preset and restores default timings.
- [ ] **`ButtonActivity`** pulses the matching card.
- [ ] **Remove** the Task 1 bridge for the toggle pair.
- [ ] **Tests:**
  - `ButtonsPage_FriendlyNameOnlyDevice_IsSelected` (same name, same assertion);
  - toggle A/B friendly-name-only cases ported from `OutputDevicesPageTests`;
  - editing preset ≠ live preset shows the banner, and "Make live" calls `SetActivePreset`;
  - live dot follows the runtime;
  - reorder moves actions in every preset;
  - reset clears all presets;
  - Test executes the selected action through the runtime;
  - the summary text per action type;
  - the light preview for mute, toggle and preset;
  - tab order.
- [ ] **Commit:** `feat(app): buttons page with presets, action catalog and controller-light preview`.

### Task 12: Connection page (App)

**Files:** `Views/Pages/ConnectionPage.*`, `ViewModels/ConnectionViewModel.cs`; `tests/Volco.App.Tests/Pages/ConnectionPageTests.cs`.

- [ ] **Status panel:** state, transport details and Reconnect. "Last message" age comes from a timestamp the VM updates on `SliderLevelChanged`/`ButtonActivity`.
  - Reconnect calls an existing runtime path. If none exists, the button is omitted rather than adding engine work; note it in the PR.
- [ ] **Connect using:** segmented (USB cable / Wi-Fi (UDP) / WebSocket) with that type's fields always visible. Keep the Step 2 validation ranges (ports 1024–65535; baud list).
- [ ] **Tests:**
  - each type shows exactly its fields;
  - edits persist to `Connection.*`;
  - tab order.
- [ ] **Commit:** `feat(app): connection page — status first, per-type settings visible`.

### Task 13: General page and OSD (App)

**Files:** `Views/Pages/GeneralPage.*`, `ViewModels/GeneralViewModel.cs` (absorbs `UpdateWindow`'s VM logic), `Osd/VolumeOsdWindow.*`, `Osd/VolumeOsdViewModel.cs`; tests in `tests/Volco.App.Tests/Pages/GeneralPageTests.cs` and `tests/Volco.App.Tests/Osd/`.

- [ ] **Updates panel** (release builds only, as today):
  - version, Check for updates, inline progress and "Update now";
  - GitHub token, always visible, with the "required, private repo" copy;
  - "Update from development builds" under Advanced.
  - Port every `UpdateWindowTests` case to `GeneralPageTests`.
- [ ] **Troubleshooting panel:** Detailed logging, Open log folder. "Open log folder" goes through a new `IWindowEffects`-style seam in **App** (`IShellLauncher` with a default `Process.Start` implementation, which is platform-neutral .NET). If that proves to need Windows-specific code, drop the button and note it.
- [ ] **OSD:**
  - dark tokens, 8 corners, hairline border, distinct speaker and volume icons;
  - `OsdKind.Preset` shows the preset icon, "Preset" and the name, with no bar.
  - Step 2 `OsdWindow_*` tests stay unmodified and must pass. Add tests for the preset kind.
- [ ] **Commit:** `feat(app): general page with inline updates; OSD restyle and preset kind`.

### Task 14: Coverage, accessibility and wrap-up

- [ ] **`SettingsCoverageTests`:** one test per spec §1 row. Each sets the value through the UI (headless) and asserts the corresponding `AppConfig` property after save, including `DeviceB` for the toggle and per-preset actions.
- [ ] **Tab order and focus visuals:** a tab-order test per page (where not already added); every focusable control shows the focus ring style.
- [ ] **Architecture tests** still pass: no P/Invoke and no Windows references in App or Core.
- [ ] **Scope check:** `git diff --name-only main... | grep '^src/Volco.Windows/'` prints nothing. State the result in the PR body.
- [ ] **CLAUDE.md:** update the Wire Protocol section (`0x05`, removal of 0x03/0x04), the Config section (schema v3, presets) and the firmware description (indicator table, README).
- [ ] **Manual checklist** `docs/superpowers/checklists/2026-10-01-ux-redesign-manual-checklist.md` covers:
  - every spec §1 row configurable and persisted;
  - real-config migration from your current `config.json`;
  - the firmware flashed on the real device: mute LEDs, the A/B pair, preset switch via hold, lights off after a preset change;
  - the simulator;
  - OSD parity items from Step 2's checklist plus the preset OSD;
  - a keyboard-only walk-through;
  - light, dark and high contrast.
- [ ] **Windows build:** `gh workflow run build.yml --ref claude/kind-hawking-pjhbrp --field run_build=true` **only after the user says go** (CLAUDE.md: never trigger the build unprompted).
- [ ] **PR:** update the body with the test deletions and rewrites listed under Review Focus #2 and the scope-check result. **Do not merge.**

## Done when

- The plan is approved.
- CI is green on every task's push.
- The scope check is clean.
- The user has run the checklist (including on the real device) and approved the PR.

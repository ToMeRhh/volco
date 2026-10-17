# Controls page: one page for sliders and buttons

Date: 2026-10-17. Status: approved design (pitch mock revision 7); implemented (steps 1–2).

The approved interactive mock is the reference for anything this text leaves visual: <https://claude.ai/artifact/TiSBFqTKEfThvVk7wr7CEe> (private to the owner). Related issues: #108 (double slider smoothing) and #109 (controller-wide button timing).

## 1. Goal

Setting up what the sliders and buttons do is hard today:
- Controls are numbered cards (counted from zero) in long accordion lists.
- One controller is spread over four pages.
- Each button has three 17-entry pickers.
- The light's behaviour is a prose paragraph.
- Slider apps are typed as exe names.
- Dragging a card in a list silently rewires hardware.

The Sliders and Buttons pages are replaced by one **Controls** page:
- **A drawing of the controller.** It shows the controller's real sliders, buttons, LEDs and screens, each labelled with what it does.
- **A fixed-size inspector** for the selected control. Every choice longer than one line opens in a **dialog** with fuzzy search.
- **Mode tabs** on the buttons area of the drawing. Today's button presets are renamed **modes**.

Desktop only. The window is resizable down to today's minimum (820 × 520), and there is no phone layout.

### Out of scope

- Choosing what a button's light or screen shows. It keeps today's rule (§7).
- An arrangeable drawing that matches the physical case.
- Per-mode sliders. Modes change buttons only, as presets do today.
- Controller-wide button timing (#109) and the smoothing question (#108). Until they are decided, the inspector keeps today's settings (§5).

## 2. Decisions (from the pitch review)

1. **The drawing comes from the controller.** Sliders form one wrapping grid and buttons another, in input order. There is no arrangeable layout.
2. **The inspector does not change length while you use it.**
   - Its rows are one line each, and choices open dialogs.
   - Only the Response (sliders) and Timing (buttons) sections expand, and they start closed.
3. **Modes are buttons-only, and each is a complete set of actions.** A mode is not a set of overrides of a base mode.
4. **Picking a mode tab edits that mode. It never changes the live mode.** "Make live" does.
5. **The first mode is the start-up mode**, as today (`AppConfig.Presets`: the first is live at start). It cannot be deleted.
6. **Add mode** creates an empty mode and opens its menu: name, Make live, Copy actions from.
7. **Edit modes:** a pencil at the right end of the tabs. While editing, tabs can be dragged to reorder, clicked to rename, and deleted with × (except the first). The pencil becomes a check mark that ends editing.
8. **Buttons have no names.** A button's label is its main action, with its other actions listed below.
9. **Swapping a button moves only its actions.** One grip, beside a button's action rows, drags all its actions onto another button in the edited mode only. The drag image is the action rows, not the cap or light. There are no per-action drag handles.
10. **Sliders keep optional names and are swapped whole**, by a grip beside the track.
11. **Inspector "Clear"** removes all of a button's actions in the edited mode, with Undo.
12. **The light or screen follows the main action** (§7), as today.
13. **The controller works normally while the window is open.**
    - A press or slider move acts as usual and also selects that control.
    - Only the hardware test mode ignores input, as today.
14. **The action and target dialogs:**
    - The search box is focused on open, and the full list shows.
    - Search is fuzzy, and sections are collapsible.
    - Fields are validated (§6.4).
15. **Minimal UI.** No page subtitle, hint lines, legends or explanatory captions; tooltips carry the detail. The one warning that stays is the "USB" tag on program and script actions (custom-actions spec §7).

## 3. Page structure

### 3.1 Navigation and window

- The sidebar has three entries: **General**, **Controls** and **Hardware**.
  - Sliders and Buttons are removed.
  - General loses its Devices section (§8).
- `SettingsWindow` opens at **1100 × 720** (today 860 × 660). The minimum stays 820 × 520.
- **1000 px wide and up:** a 168 px sidebar with labels, the drawing, and a 380 px inspector.
- **Below 1000 px:** a 56 px icon-only sidebar (tooltips give the names), and a 300 px inspector, still beside the drawing.
- In both layouts only the drawing scrolls. The inspector keeps its place and size.

### 3.2 Controls page layout

Left to right: the page title "Controls", then the drawing (§4), then the inspector (§5). Dialogs (§6) are modal over the window. They are at most 640 × 560 and fit inside the 520 px minimum height.

## 4. The drawing

### 4.1 Which controls

- Counts and indicator kinds come from the last Hello (`AppConfig.Controller`).
- Before the first Hello, the drawing comes from the config's lists, with indicators unknown. One line under the drawing says "Connect your controller to see its lights and screens."
- **The controller has more controls than the config:** the extra controls are drawn "Not set". Their config entries are created on first edit. This replaces the "Add the missing N" hint.
- **The config has more controls than the controller:** the extra controls are drawn dashed, with "Not on this controller". Their inspector offers **Remove**, which removes the last one only (input number is position) and shows Undo.

### 4.2 Sliders

- One grid row, wrapping: `repeat(auto-fit, minmax(84px, 1fr))`, so a few sliders spread across the case.
- **Each slider tile has:**
  - a fader showing the live level (`VolcoRuntime.SliderLevelChanged`, seeded with `GetSliderLevel`);
  - a grip beside the track;
  - a label of up to two lines that never breaks inside a word. The label is the slider's name, else its target (§5.1).
- **"Not set"** is drawn in the secondary text colour.

### 4.3 Buttons and the mode tabs

The buttons sit in a frame drawn in the edited mode's colour (today's preset tints, `PresetTint`). Its top edge carries the **mode tabs** (§9).

Each **button tile** has four rows, which every tile in a grid row shares (CSS `subgrid`; in Avalonia a shared-size grid per row, §11):

1. **Indicator.** LED, LED pair or screen preview, aligned to the bottom of the row. Nothing for "none".
2. **Cap.**
3. **Main action.** Bold, up to two lines. "Not set" when empty.
4. **Other actions.** One line each, with a gesture mark (•• double press, ▬ hold), ellipsized.

The grip sits in a column on the left of rows 3–4. The main action's line has no extra marker, because the light follows it by definition (§7).

Caps, main actions and action lists therefore line up across a row whatever each button has. The mock checked this in Chromium at all three sizes, on both 3- and 16-button boards.

**The previews show the edited mode:**
- The LEDs and screen show what the controller would show in that mode, from `ControllerDisplay`.
- When the edited mode is live, this is exactly what the controller shows.
- The brief "used another action" feedback (a double blink, or the pressed action on a screen) plays in the preview when Try runs a non-main action.

### 4.4 Dense boards

**When:** more than 8 sliders or more than 8 buttons.
- Faders are shorter, caps smaller, and screens text-only.
- A button's rows 3–4 become one row of three action icons (Press, Double press, Hold). The main one is outlined, empty slots are faint, and each icon has a tooltip.

### 4.5 Selection and touch-to-select

- **Selecting:** clicking a tile, or Enter or Space on a focused tile, selects it. The tile gets an accent outline.
- **A button press on the controller** (`ButtonActivity`) selects that button and pulses it. The press still runs its action, from the runtime.
- **A slider move on the controller** selects that slider and pulses it. Only real movement counts:
  - a change of at least 2 % since the last selection by that slider;
  - not within 1 s after a Hello, so the Hello's sync doesn't select a slider.
- The selection never moves while a dialog or menu is open. The pulse still shows.
- Arrow keys move the focus between tiles.

## 5. The inspector

The header shows the control's kind and number. Its body is a fixed set of rows: one line each, 44 px, plus at most one collapsed section.

### 5.1 Slider

- **Header:** "Slider N", with an editable name field. The placeholder is the target's label.
- **Controls row:** the target's icon or app icons and its label, e.g. "Speakers", "Spotify, Discord", "Main output" or "Everything else". It opens the target dialog (§6.2).
- **Response (collapsed):**
  - the curve as four small graphs: Linear, Log, Exp, S-curve;
  - Dead zone (0–45 %), Smoothing (0–95 %) and Reverse.
  - These are today's per-slider settings, unchanged. #108 may remove Smoothing.
  - The summary reads "Linear" or "Custom".

### 5.2 Button

- **Header:** the indicator kind ("LED", "LED pair", "Screen", "No light") and "Button N", with **Clear** on the right. Clear is disabled when the button has no action in the edited mode.
- **Actions, labelled with the edited mode's name as a coloured pill:** three rows, Press, Double press and Hold.
  - Each row shows the action's icon and label, a "USB" tag for programs and scripts, and **Try** (▶; runs it once, `ActionOrigin`, as today's Test).
  - An empty row shows "—" and a +.
  - Clicking a row opens the action dialog (§6.1).
- **Light or Screen row:** the preview and "Follows Press" (or Double press or Hold, whichever is the main action), or "Off". It is not editable.
- **Timing (collapsed):** Hold after and Double press within, per button as today, until #109 moves them.

## 6. Dialogs

**Shared layout:** a title (the gesture and "Button N · Mode", or "Slider N controls"), a search box, a list, and a footer.

**Shared behaviour:**
- **On open:** the search box has focus, and the whole list shows with each section's remembered collapsed state.
- **Escape**, the close button, Cancel or a click outside closes the dialog and changes nothing.
- **Done** validates and saves, or shows errors (§6.4).

### 6.1 Action dialog (a button's Press, Double press or Hold)

- **The list (left)** has sections: Sound, Media, Modes, Programs. They hold today's actions, labelled as in the mock:
  - Mute main output, Mute microphone, Mute a device, Switch output, Toggle outputs;
  - Play / pause, Next track, Previous track, Stop;
  - Next mode, Go to mode;
  - Open a program, Run a script.
  - Actions this platform can't run are disabled (`ActionCatalog.ChoicesFor`).
- **The settings pane (right)** shows the picked action's fields, as on today's action row (`ActionSlotViewModel`):
  - devices, A and B, mode, media app;
  - file with Browse, arguments, start in, while running;
  - the USB note.
  - Before an action is picked it reads "Choose an action."
- **Footer:** Remove (when the slot has an action), Cancel, Done.
- **New actions start with nothing pre-selected:** no device, no mode, no file. The user's choice is never guessed.

### 6.2 Target dialog (a slider's Controls)

The sections are:

- **Devices:**
  - Main output (follows the Windows default; shows the current one).
  - Every output device.
  - Every input device (new, §10.4).
  - Single choice. Each device row has a menu with Rename and Icon (§8).
- **Apps:**
  - The apps playing sound now, from audio sessions, with display name, icon, exe as secondary text, and a live peak meter (§10.3).
  - Then saved apps that aren't running ("Not running").
  - Then "Other app…", which opens a file picker for an .exe.
  - Multiple choice.
  - An app another slider has shows that slider's name as a pill. Done moves it here.
- **Other:** Everything else (today's "All other apps"). Single choice. Done turns any other slider set to Everything else into "Not set" apps.

### 6.3 Search

- **Fuzzy matching:**
  - Every query character must appear in order, ignoring case and whitespace.
  - The score adds 1 per matched character, +2 at a word start, and +3 when it directly follows the previous match. It subtracts 0.01 × the candidate's length.
  - A match on the name ranks above a match on secondary text: keywords for actions (e.g. "mic microphone" for Mute a device), and exe names and Windows device names for targets.
- **Results:**
  - Matched name characters are highlighted.
  - While a query is non-empty, every section with matches is open and shows its match count. Results within a section are sorted by score.
  - Sections with no match are hidden. If nothing matches: "No actions match “q”."
  - Clearing the query restores the full list and the remembered collapsed sections.
- **Keys:** Enter picks the top result. Down and Up move through results.
- The algorithm lives in `Volco.App` (`FuzzyMatcher`) with unit tests; it is the mock's, ported.

### 6.4 Validation

**Rules,** in `Volco.Core` (`ActionValidator`, `SliderTargetValidator`), so the UI and a hand-edited config.json share them:

| Action | Rule |
|---|---|
| Mute a device | a device |
| Switch output | an output device |
| Toggle outputs | two output devices, different from each other |
| Go to mode | an existing mode |
| Open a program, Run a script | A file that is a full path (drive letter or UNC), quotes stripped. No `< > \| ? *` after the drive. The file exists: today's check, skipped for URLs such as `steam://`, which Open a program accepts. |
| Run a script | additionally, the file is .bat, .cmd, .ps1, .py, .js or .exe |
| Start in (optional) | a full path when given |
| Slider set to apps | at least one app |
| Slider set to a device | a device |

Media actions need nothing. Their app defaults to "whatever is playing".

**Behaviour:**
- Done checks the draft. It shows each error under its field, outlines the field, and focuses the first invalid field.
- After the first Done, errors update as the user types.
- Picking a different action resets this.

**On load,** an invalid action in config.json is kept. It is logged, its row shows a warning icon with the reason as a tooltip, and the runtime does not run it (§10.2).

## 7. Light and screen (unchanged rule, stated here)

- A button's LED or screen follows its **main action**: the first action set, in the order Press, Double press, Hold (`ControllerDisplay`).
- Another action shows for 1.5 s when used: a double LED blink, or that action on the screen.
- There is no per-button choice.

## 8. Device names and icons

General's Devices section goes away. A device's nickname and screen icon (`config.devices`) are edited from any device list in the dialogs:
- The row menu offers **Rename** and **Icon**, which open a small dialog with the fields of today's `DeviceItemViewModel`.
- "Used by" moves into that dialog.

Display names work as today (`DeviceDisplay`): the nickname, else the short name, with the Windows name as secondary text.

## 9. Modes

### 9.1 Tabs

- **Placement:** tabs sit on the top edge of the buttons frame, in tab order. On the right end of the strip, in order: Make live (only when the edited mode isn't live), Add mode, and the pencil.
- **Look:** the edited tab is filled with its colour. The live mode's tab has a green dot (tooltip "Live on the controller").
- **Clicking a tab** edits that mode. The live mode doesn't change.

### 9.2 Menu

Right-clicking a tab, or the Add mode flow, opens the mode's menu:
- **Name field:** focused and selected. Enter or Escape closes the menu, and the name is saved when the menu closes.
- **Make live:** only when the mode isn't live.
- **Copy actions from ▸ other modes:** replaces this mode's actions with a copy, with Undo.

### 9.3 Add mode

1. Adds "Mode N" with no actions, at the end.
2. Makes it the edited mode, not live.
3. Opens its menu.

### 9.4 Edit modes

The pencil switches the strip into edit mode:
- Tabs can be dragged to reorder. Moving a tab first makes it the start-up mode, and while editing the first tab carries a small "start" label.
- Clicking a tab renames it inline.
- Each tab but the first has ×, which deletes it with Undo (today's DeletePreset logic: the live mode falls back to the first).
- The check mark ends edit mode.

**Colours:** a mode's colour follows its position, as today, so reordering recolours tabs. A pinned colour per mode is a possible follow-up (preset-tabs feature notes).

## 10. Core and Windows changes

### 10.1 Config (schema v7, with the usual `config.v6.bak`)

- **Rename presets to modes.** The button-preset concept is renamed everywhere user-visible and in code:
  - `presets` → `modes`;
  - `buttons[i].actions` keyed by mode id;
  - `presetId` → `modeId`;
  - actions `NextPreset`/`GoToPreset` → `NextMode`/`GoToMode`;
  - `ActivePresetId` → `ActiveModeId`;
  - script variable `VOLCO_PRESET` → `VOLCO_MODE`;
  - screen and OSD text.
  - The controller-layout presets (`ControllerPresets`, Hardware › Start from) keep their name; they are a different thing.
- **Slider names:** new `sliders[i].name` (string, default empty).
- **Input devices on sliders:** `sliders[i].device` may refer to a capture endpoint. The device type is already stored in the endpoint id; the flow is read from the device registry.

### 10.2 Runtime

- **Touch-to-select needs no new runtime event.** `ButtonActivity` and `SliderLevelChanged` exist. The 2 % and 1 s rules (§4.5) live in the view model. `ControllerInfoChanged` marks the Hello.
- **The runtime refuses invalid actions** via `ActionValidator`, logging once per action.

### 10.3 Audio sessions (Windows engine, seam extension)

`AudioSessionInfo` gains:
- `DisplayName`: the session's display name, else the exe's file description, else the exe name;
- `IconPath`: the session's icon path, else the exe path. The App draws the icon through a new `IAppIcons` seam, null on Linux; letter tiles are the fallback.

`IAudioEngine` gains `float GetSessionPeak(string instanceId)`, from `IAudioMeterInformation` on the audio thread, for the live meters. The target dialog polls it every 100 ms while open.

### 10.4 Capture devices on sliders

The device registry already holds capture endpoints. Slider routing calls `SetDeviceVolume` on a capture `DeviceRef` the same way. The OSD shows a mic icon.

## 11. Avalonia notes

- **Tile alignment.** Avalonia has no CSS `subgrid`. Each grid row of button tiles is one `Grid` with four shared rows (`Grid.IsSharedSizeScope` plus `SharedSizeGroup` per row index), or a custom panel that measures every row and arranges tiles by the tallest row. A panel test checks that the caps, main actions and action lists of a row have equal Y.
- **Drag.** Today's `CardReorder` pointer-based drag is reused:
  - The drag visual is an adorner holding a copy of the action rows (buttons) or of the slider tile.
  - The source dims its action rows (buttons) or the tile (sliders).
  - Keyboard alternative: the context menu of a tile has **Swap with ▸ Button N / Slider N**.
- **Dialogs** are overlays over the page, like today's setup and test dialogs: modal, Escape and click-outside close, focus trapped, and focus returns to the row that opened them.

## 12. Testing

Headless tests in `Volco.App.Tests`, replacing `ButtonsPageTests`, `ButtonsPageCustomActionTests` and `SlidersPageTests`:

- **Drawing:**
  - counts from the Hello versus config, "Not set", "Not on this controller" with Remove;
  - dense boards over 8;
  - row alignment (panel test).
- **Selection:**
  - click and keyboard;
  - `ButtonActivity` selects and still runs;
  - a slider move of 2 % or more selects; the Hello sync does not;
  - no selection change while a dialog is open.
- **Inspector:**
  - fixed rows;
  - Clear and Undo;
  - Try runs through `ActionOrigin`;
  - Response and Timing persist.
- **Action dialog:**
  - focus on open; full list with an empty query;
  - fuzzy order ("tgo" puts Toggle outputs first);
  - collapsed sections, which open while searching;
  - picking updates the settings pane; Enter picks the top match; Escape cancels;
  - validation for every rule in §6.4, errors live after the first Done;
  - Remove.
- **Target dialog:**
  - single and multiple choice;
  - an app on another slider moves on Done;
  - Everything else is exclusive;
  - zero apps is refused;
  - Rename from a row opens the device dialog.
- **Modes:**
  - a tab edits without changing live; Make live;
  - Add mode creates an empty mode and opens the menu;
  - Copy actions from, with Undo;
  - edit mode: reorder (the first becomes the start-up mode), rename, delete except the first, with Undo.
- **Swap:** sliders whole; button actions in the edited mode only (timing stays); the context-menu Swap with.
- **Core:** `ActionValidator` and `SliderTargetValidator` cases; the v6 → v7 migration (presets → modes, names, enum values, `.bak`); `FuzzyMatcher`.
- **`SettingsCoverageTests`:** the inventory follows the new page; every setting stays editable and survives a save and reload.
- **`ArchitectureTests`:** unchanged; the new seams keep Core and App platform-free.

Windows manual checklist:
- session names, icons and meters;
- a capture device on a slider;
- touch-to-select with the real controller;
- the window at 820 × 520 and at 1100 × 720.

## 13. Delivery

These are separate PRs, in this order, each green on CI:

1. **Core: validation and slider names.** `ActionValidator`, `SliderTargetValidator`, `sliders[i].name`, and runtime refusal of invalid actions.
2. **The Controls page with its drawing and inspector.** Fuzzy dialogs, validation in the UI, Clear, touch-to-select, and the window sizes. The Sliders and Buttons pages are removed. Modes keep today's tab behaviour on the buttons frame, and the new UI's text already says "mode", while the code and config keep "preset" until step 7.
3. **Mode tabs:** Add mode, the menu, edit mode (reorder, rename, delete), and Make live.
4. **Drag to swap** and Swap with.
5. **Windows: session display names, icons and peak meters**, and capture devices on sliders.
6. **Device names and icons** move from General into the dialogs.
7. **Rename presets to modes:** schema v7, code, text, `VOLCO_MODE`, and docs (CLAUDE.md, README screenshots).

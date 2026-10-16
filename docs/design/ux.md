# Volco UX Redesign — Design Spec

**Date:** 2026-10-01
**Status:** Approved 2026-10-01; implemented.

## 0. Scope change from the Step 3 plan

The Step 3 plan limits the PR to `src/Volco.App/` and `tests/Volco.App.Tests/`. The user's goals (any number of controls, presets, room for per-button OLED screens and custom actions) cannot be met that way, because Core still assumes the current hardware (§2.2). The user approved this scope instead:

- **One PR on one branch** may change `src/Volco.App/`, `src/Volco.Core/`, `tests/`, `firmware/`, `tools/simulator/` and `docs/`.
- **`src/Volco.Windows/` stays untouched.** The OS-specific layer is still the line the redesign must not cross. If a change seems to need it, stop and report it, as the original plan says.
- **The wire protocol changes without backwards compatibility.** There is one device running this firmware, the user's own, and it is reflashed together with the app update. The **current hardware (3 buttons with mute and device LEDs, 5 sliders) must remain a valid configuration** of the new firmware.
- The plan's scope check becomes:
  `git diff --name-only main... | grep '^src/Volco.Windows/'` must print nothing.

## 1. Inventory

Every setting the Step 2 UI edits, mapped to the `AppConfig` property and to where it lives after the redesign. This table feeds `SettingsCoverageTests` (Review Focus #1).

| Step 2 page | Setting | `AppConfig` (v2) | After redesign |
|---|---|---|---|
| Connection | Connection type (Serial / UDP / WebSocket) | `Connection.Type` | Connection page, segmented control "USB cable / Wi-Fi (UDP) / WebSocket" |
| Connection | COM port | `Connection.Serial.ComPort` | Connection page, shown when USB is selected |
| Connection | Baud rate | `Connection.Serial.BaudRate` | Connection page, shown when USB is selected |
| Connection | UDP listen port | `Connection.Udp.ListenPort` | Connection page, shown when Wi-Fi is selected |
| Connection | WebSocket port | `Connection.WebSocket.Port` | Connection page, shown when WebSocket is selected |
| Connection | Status text | (runtime `ConnectionStatus`) | Status panel at the top of Connection, plus the nav footer |
| Sliders | Mapping type: Processes / Output Device / Unmapped | `Sliders[i].Type` | Slider card: "Apps / All other apps / A device" ("Unmapped" is renamed "All other apps") |
| Sliders | Processes (comma-separated) | `Sliders[i].Processes` | Slider card, chip input with process autocomplete |
| Sliders | Output device | `Sliders[i].Device` | Slider card, device picker; empty = "Current default output" |
| Sliders | Invert | `Sliders[i].Invert` | Slider card, Advanced |
| Sliders | Response curve | `Sliders[i].Curve` | Slider card, Advanced |
| Sliders | Dead zone | `Sliders[i].DeadZonePercent` | Slider card, Advanced |
| Sliders | Smoothing | `Sliders[i].SmoothingFactor` | Slider card, Advanced |
| Sliders | Live level | (runtime `SliderLevelChanged`) | Level bar in the closed card header |
| Buttons | Single / double / long press action | `Buttons[i].SinglePress/DoublePress/LongPress.Action` | Button card: Press / Double press / Hold, **per preset** |
| Buttons | Action device | `Buttons[i].*.Device` | Inline under the action picker |
| Buttons | Test (runs the action once) | (command) | "Test" button next to each action picker |
| Buttons | Long-press threshold | `Buttons[i].LongPressMs` | Button card, Advanced ("Hold after") |
| Buttons | Double-press window | `Buttons[i].DoublePressWindowMs` | Button card, Advanced |
| Output Devices | Toggle device 1 / 2 | `OutputDevices.Device1/Device2` | Moves into the "Toggle between two outputs" action as its A and B devices. **The page is removed.** |
| General | Debug logging | `DebugLogging` | General page, "Detailed logging" |
| Update window | GitHub token | `Updates.GitHubToken` | Removed (2026-10-07): the updater reads public GitHub Releases without a token |
| Update window | Update from CI build | `Updates.UseCiArtifact` | Removed (2026-10-07): the updater offers releases only |
| Update window | Check for updates / Update now | (commands) | General page, Updates panel. The tray item "Check for Updates…" opens Settings on General. |

Not edited by any page today and still not edited: `SchemaVersion`, `Sliders[i].Index`, `Buttons[i].Index` (both become "always equal to list position", §4.1).

## 2. Critique of the current design

### 2.1 UX

1. **Five pages for about 20 settings.** Output Devices has two settings, and they only matter to one action type. General has one setting. Every visit means switching pages to finish one thought (e.g. "make button 2 toggle my outputs" spans Buttons and Output Devices).
2. **The toggle pair is global and detached from the action that uses it.** Nothing on the Buttons page tells you where the two devices come from.
3. **Fixed lists.** The pages show exactly the entries in `config.json` (5 sliders, 3 buttons by default). You can't add, remove or reorder controls.
4. **Generic labels.** "Slider 0", "Mapping Type", "Unmapped", "Processes (comma-separated)". "Unmapped" in particular doesn't say that it means "every app no other slider lists".
5. **Tuning shown at the same level as the essentials.** Curve, dead zone and smoothing sit next to the mapping itself.
6. **Connection status is only on the Connection page.**
7. **Update settings live in a separate window** with a required token that isn't discoverable from Settings.
8. **No high-contrast theme**, and `Icon.Volume` and `Icon.Speaker` are the same glyph.

### 2.2 Hardware assumptions in Core (why §0 was needed)

| Where | Assumption |
|---|---|
| `ControllerStateReporter.MuteButtonCount = 2` | Mute LEDs exist only for buttons 0 and 1 |
| `ProtocolEncoder` 0x03 / 0x04 | The firmware knows "mute" and "active device"; device LEDs are not tied to any button |
| `AppConfig.OutputDevices` | One global toggle pair |
| `SliderProcessor` 16-slot arrays | At most 16 sliders |
| `ButtonActionType` | A closed set with one `Device` parameter; no presets |

## 3. Design

### 3.1 Pages

Four pages in a 200 px navigation column: **Sliders, Buttons, Connection, General**. The navigation footer always shows the connection state ("● Connected · COM3" / "● Not connected").

### 3.2 Model: position is the input

- **A card's position in its list is its hardware input number.** The first slider card is slider input 0, and so on. This matches how the firmware numbers controls.
- There is **no separate input-number field** and no possibility of duplicates.
- **Dragging a card reassigns it:** its whole configuration moves to the physical control at the new position. Input numbers relabel live while dragging.
- **Controls have no names.** A card is identified by its number and what it does: "Slider 1 · spotify.exe", "Button 2 · Toggle Speakers / Headphones · Hold: Next preset".
- **One empty state, "Not set up".** It is how a newly added card starts and what **Reset** returns a card to. Unset cards have a dashed outline. An unset slider or button does nothing.
  - Sliders: "What it controls" has three choices (Apps / All other apps / A device). "Not set up" is not one of them; only Reset gets you there.
  - Buttons: unset means no action in any preset and default timings.
- **Add** appends a card at the end (the next input number), starting as Not set up.
- **Remove** exists only on the **last** card ("Remove last slider/button") and shrinks the list. A middle card can only be Reset, so no other control ever changes number.
- Reset and Remove show an **Undo** toast for 6 s.
- The ⋯ menu holds: Move up, Move down, Reset, and "Remove last…" on the last card.
- Keyboard reordering: Alt+Up / Alt+Down on the drag handle.

### 3.3 Sliders page

- **Header:** title, one-line description, primary "Add slider" button.
- **Closed card:** drag handle · "Slider N" · summary of what it controls · live level bar (4 px) · ⋯ · chevron. Clicking the header opens the card.
- **Open card:**
  - "Controls": segmented **Apps / All other apps / A device**.
    - Apps: chip input with autocomplete from running processes (replaces the comma-separated text box; keeps the Step 2 autocomplete behaviour).
    - All other apps: a hint listing what is excluded ("everything except spotify.exe, chrome.exe").
    - A device: device picker, first entry "Current default output".
    - Not set up (no segment selected): hint "Not set up. Choose what this slider controls."
  - **Advanced** (collapsed): Invert direction, Response curve, Dead zone, Smoothing.
- **Activity:** when the controller sends a value for a slider, its card's level bar moves and the card gets a brief accent outline. This is how a user finds which card is which physical slider.

### 3.4 Buttons page

- **Header:** title, description, "Add button".
- **Presets** (buttons only; sliders are not affected):
  - With one preset, only an "Add preset" link and a one-line explanation are shown.
  - With two or more, folder tabs show the presets, and the button cards sit inside the tab of the preset you are **editing**. Each preset has its own colour (by position, six tints per theme, `PresetFill{n}`/`PresetEdge{n}`): its tab is filled with it, and the edited tab's frame takes its fill and edge, so it is clear which preset the cards belong to. The preset **live** on the controller has a green dot. Next to the tabs: Add preset; at the top of the tab: the live line (or Make live), Rename, Delete.
  - A banner under the tabs says either "*Default* is live on the controller" or "You are editing *Gaming*. The controller is using *Default*. [Make Gaming live]".
  - The app always starts on the first preset.
  - Only actions are per preset. Timing and position are shared by every preset. Dragging a button card moves its actions in every preset; Reset clears them in every preset.
- **Closed card:** drag handle · "Button N" · summary of the edited preset's actions · controller-light preview (§3.6) · ⋯ · chevron.
- **Open card:**
  - Three action rows, always visible: **Press, Double press, Hold**. Each has a grouped action picker, a **Test** button, and its parameters indented below.
  - Actions:

    | Group | Action | Parameters | Light (§3.6) |
    |---|---|---|---|
    | Audio | Do nothing | — | — |
    | Audio | Mute current output | — | on while muted |
    | Audio | Mute current input (mic) | — | on while muted |
    | Audio | Mute a specific device | device | on while muted |
    | Audio | Switch output to… | output device | on while that device is the output |
    | Audio | Toggle between two outputs | output A, output B | A or B |
    | Presets | Next preset | — | which preset is live |
    | Presets | Switch to preset… | preset | which preset is live |
    | Custom (coming later) | disabled placeholder | — | — |

  - A "Controller light" line explains what the button's light will show and which action it follows.
  - **Advanced** (collapsed): Hold after (ms), Double-press window (ms).
- **OSD on preset switch:** pressing a preset action shows the OSD with "Preset" and the new preset's name.

### 3.5 Connection and General

- **Connection:**
  - Status panel first: state, transport details ("COM3 · 115,200 baud · last message 0.2 s ago"), Reconnect.
  - Then "Connect using" (USB cable / Wi-Fi (UDP) / WebSocket) and **that type's settings, always visible**: port + baud rate, listen port, or server port.
  - Hint: "These must match the values in your controller's firmware."
- **General:**
  - Updates panel: version, Check for updates. (The GitHub token and "Update from development builds" were removed on 2026-10-07; the updater reads public releases.)
  - Troubleshooting panel: Detailed logging, Open log folder.
  - The separate Update window goes away; its progress and "Update now" appear inline in the Updates panel.

### 3.6 Controller feedback (lights)

The app, not the firmware, decides what each button's light shows:

- Each action that has a state reports a **feedback value**: 0 means off or unknown, and 1..N means on, or choice *n*.
  - Mute: 1 when muted.
  - Toggle output: 1 = A is active, 2 = B is active, 0 = neither.
  - Preset actions: 1 + the live preset's index.
  - Switch output: 1 while that device is the output.
- A button's value comes from the **live** preset, using the first stateful action in the order Press → Double press → Hold.
- The runtime sends the value **per button input number** (§4.3), deduplicated against the last value sent. After a preset switch or a reconnect, it re-sends every button.
- The settings UI previews the lights on each button card (one dot for on/off, "A ●○ B" for toggle, one dot per preset for preset actions).

### 3.7 Visual design

The direction: quiet and neutral, letting the content carry the screen. Flat surfaces, hairline borders, one muted blue accent used sparingly. No gradients, glows, coloured shadows or acrylic.

**Colour tokens.** Ratios are WCAG contrast against `Surface`. All text tokens pass AA (≥ 4.5:1) and `BorderStrong` meets the 3:1 non-text minimum.

| Token | Light | Dark | Use | Contrast L / D |
|---|---|---|---|---|
| `Bg` | `#F6F6F7` | `#111113` | Window, navigation | — |
| `Surface` | `#FFFFFF` | `#1A1A1D` | Cards, inputs | — |
| `Subtle` | `#EFEFF1` | `#222226` | Segmented-control track, hover | — |
| `Border` | `#E4E4E7` | `#2C2C31` | Card outlines, dividers | decorative |
| `BorderStrong` | `#8E8E96` | `#6A6A73` | Input outlines | 3.3 / 3.2 |
| `Text` | `#18181B` | `#F4F4F5` | Primary text | 17.7 / 15.8 |
| `TextSecondary` | `#52525B` | `#A8A8B0` | Labels, summaries | 7.7 / 7.4 |
| `TextTertiary` | `#6B6B74` | `#8E8E97` | Captions, hints | 5.3 / 5.4 |
| `Accent` | `#3358C4` | `#8AA6F2` | Selection, primary button, level bar, focus ring | 6.3 / 7.3 |
| `OnAccent` | `#FFFFFF` | `#111113` | Text on accent | 6.3 / 7.9 |
| `Success` | `#1F7A45` | `#5CC98A` | Connected, live preset, lit indicator | 5.4 / 8.4 |
| `Warning` | `#9A5B00` | `#E7B04F` | Reconnecting | 5.4 / 8.9 |
| `Danger` | `#B42318` | `#F4877C` | Reset/Remove, errors, disconnected | 6.6 / 7.1 |

**HighContrast** variants (dark and light, picked from the OS high-contrast theme) replace these tokens with a fixed palette modelled on Windows' Night sky/Aquatic and Desert themes: text, accent and status ≥ 7:1, borders ≥ 3:1. They do not read the user's own system colours, which would need a Win32 call outside `Volco.App`. State is never shown by colour alone: the connection dot always has a text label.

**Type:** Segoe UI Variable (fallback: the platform UI font).

| Style | Size / weight | Use |
|---|---|---|
| Title | 20 / Semibold | Page title |
| Subtitle | 14 / Semibold | Card title |
| Body | 13 / Regular | Labels, inputs |
| Caption | 12 / Regular | Hints, chips, "Advanced" |

**Spacing and shape:**
- Spacing is a 4 px grid (4 / 8 / 12 / 16 / 24 / 32). Pages are padded 24, cards 16, with 8 between cards.
- Corners: cards 8, inputs and buttons 6, chips fully rounded.
- Elevation: none at rest. Only the card being dragged gets a shadow.

**Motion:** card open and close; drag (the card follows the pointer and neighbours slide out of the way, 180 ms); a 150 ms accent-outline pulse on activity. Everything respects the OS reduce-motion setting.

**Focus:** a 2 px accent ring with a 2 px offset on every interactive element. Every page is fully operable from the keyboard.

**OSD:** same layout and behaviour as today (Step 2 `OsdWindow_*` tests unchanged). It adopts the dark tokens (solid `Surface`, 8 corners, a hairline `Border`) and gets distinct `Icon.Volume` and `Icon.Speaker` glyphs. New content: the preset-switch message.

## 4. Core, protocol and firmware changes

### 4.1 Config schema v3

```jsonc
{
  "schemaVersion": 3,
  "sliders": [            // position == hardware slider input
    { "type": "Processes", "processes": ["spotify.exe"], "device": null,
      "invert": false, "curve": "Linear", "deadZonePercent": 0, "smoothingFactor": 0 }
    // type: Processes | Device | Unmapped | None   (None = "Not set up", new)
  ],
  "presets": [ { "id": "default", "name": "Default" } ],   // order = tab order; first = startup preset
  "buttons": [            // position == hardware button input
    { "longPressMs": 500, "doublePressWindowMs": 300,
      "actions": {        // keyed by preset id; a missing key = no actions in that preset
        "default": {
          "singlePress": { "action": "ToggleOutputDevice", "device": {…}, "deviceB": {…} },
          "doublePress": { "action": "None" },
          "longPress":   { "action": "NextPreset" }
        }
      } }
  ],
  "connection": { … unchanged … },
  "updates": { … unchanged … },
  "debugLogging": false
}
```

- **Position is identity.** `Index` stays in the JSON for readability but is rewritten to the list position on every save and ignored on load.
- **Per-preset actions live inside the button**, so moving a button card is one list move.
- `ButtonActionType` gains `NextPreset` and `GoToPreset`. `ButtonActionConfig` gains `DeviceB` (toggle's second device) and `PresetId` (GoToPreset).
  - `ToggleOutputDevice` uses `Device` (A) and `DeviceB` (B) instead of the global pair.
  - Custom actions are out of scope. The intended extension is new `ButtonActionType` values with their own parameter fields, or a reference into an action library.
- `OutputDevices` is removed.
- **`ConfigMigrator` v2 → v3:**
  - Button press actions move under `actions["default"]`.
  - Every `ToggleOutputDevice` action gets `Device = OutputDevices.Device1` and `DeviceB = OutputDevices.Device2`.
  - `presets = [{default, "Default"}]`.
  - `OutputDevices` is dropped.
  - `Index` is normalised to position.
  - The user's current `config.json` must migrate to equivalent behaviour. Test it with a fixture copied from a real v2 config.
- `AppConfig.CreateDefault()` still produces 5 sliders and 3 buttons, all Not set up, plus the Default preset.

### 4.2 Runtime (`VolcoRuntime` and friends)

- Routing stays **by list position** (it already is). No routing changes are needed beyond:
  - removing the 16-slot cap in `SliderProcessor` (size by config, or use a dictionary);
  - ignoring `None` sliders and buttons with no actions.
- **Live preset:**
  - `ActivePresetId` lives in memory and starts at `presets[0]`.
  - `SetActivePreset(id)` raises `ActivePresetChanged`, which the Buttons page uses for the live dot and the controller simulator uses for its lights.
  - `NextPreset` / `GoToPreset` actions call it, and it triggers a feedback re-push.
  - Deleting the live preset moves live to `presets[0]`.
- **Button dispatch** looks up `buttons[i].actions[ActivePresetId]`.
- **Activity events** for the UI:
  - `SliderLevelChanged` already exists; keep raising it for `None` sliders too, so they can be identified.
  - Add `ButtonActivity(int index)`, raised on each raw press.
- **Feedback** replaces `ControllerStateReporter`'s mute and active-device logic:
  - `ButtonFeedback.Compute(config, activePreset, audio) → IReadOnlyList<(int button, byte value)>`, pure and unit-tested.
  - Encoded by `ProtocolEncoder.EncodeButtonFeedback`.
  - Pushed through the existing `PushControllerState` dedup and re-push timers.
- **OSD:** add `OsdKind.Preset` for the preset-switch message.

### 4.3 Wire protocol

| Id | Direction | Payload | Status |
|---|---|---|---|
| `0x01` | controller → host | slider index, 16-bit value | unchanged |
| `0x02` | controller → host | button index, state | unchanged |
| `0x03` | host → controller | mute state | **removed** |
| `0x04` | host → controller | active device | **removed** |
| `0x05` | host → controller | **ButtonFeedback**: button index, value (0 = off/unknown, 1..N = on / choice *n*) | **new** |

**This table is superseded by `oled-screens.md`.**

- The firmware ignores unknown message types.
- **OLED is deferred.** Superseded by `oled-screens.md`, which replaces this protocol.
- **No Hello/capabilities handshake** and no per-button "indicator type" setting in the app for now. Superseded by `oled-screens.md`, which adds the Hello handshake.

### 4.4 Firmware

- **Remove all "mute" and "device" concepts.** Buttons are a plain list; every button reports raw press and release as today.
- **Per-button indicator table** in `config.h`. Each entry maps a button to how it displays `0x05` values:
  - `IND_NONE`
  - `IND_LED(pin, polarity)`: value ≠ 0 → on
  - `IND_LED_PAIR(pinA, pinB, polarity)`: 1 → A, 2 → B, 0 → both off

  The current hardware becomes this table:
  ```cpp
  { 4,  IND_LED(12, ACTIVE_LOW) },
  { 14, IND_LED(21, ACTIVE_LOW) },
  { 5,  IND_LED_PAIR(18, 19, ACTIVE_HIGH) },
  ```
  Boot state: every indicator off, keeping today's fix for the opposite polarities of the two LED groups.
- **Slider pins** become a plain array in `config.h`, sized by the table, with no fixed `NUM_SLIDERS` assumptions elsewhere.
- **New `firmware/README.md`:** how the code is structured. It covers:
  - the `config.h` sections (transport selection, slider and button tables, indicator table, tuning constants);
  - how to describe a different hardware layout;
  - the transport abstraction (`transport_init/send/reconnect` per `TRANSPORT_*`);
  - the main loop (sliders → buttons → receive);
  - frame parsing;
  - the message table.

  It does **not** cover building, flashing or wiring.

### 4.5 Web simulator (`tools/simulator/index.html`)

Update to the new protocol: show each button's `0x05` value (single LED, or A/B for a value of 2). It no longer shows mute and device LEDs, and the number of sliders and buttons is configurable in the page.

## 5. Interaction rules (summary)

- Changes save as you make them (existing debounced `ConfigManager.RequestSave`). There is no Save button.
- Destructive actions (Reset, Remove last, Delete preset) are immediate and offer Undo for 6 s.
- "Advanced" sections are collapsed by default. Open/closed state is remembered per card while the window is open.
- Opening, closing and reordering are all reachable by keyboard; tab order follows reading order on each page.

## 6. Needs engine work (not in this PR)

These are things the user may want later that need changes in `Volco.Windows` or new platform seams:

- OSD on the monitor under the cursor (new platform seam).
- OSD on/off and position, and a theme choice: persisted UX preferences.
- Start with Windows; Mica/acrylic backdrops.
- Per-app level meters on slider cards.
- An updater for non-Windows platforms (`IUpdateApplier`).

## 7. Future features this design leaves room for

- **Per-button OLED screens:** Superseded by `oled-screens.md`, which replaces this protocol. (Original text: `0x05` is keyed by button and carries a value the firmware renders per its own indicator table. Screens become another indicator type. The content format and an app-side indicator setting are designed with that feature.)
- **Preset toggle button:** implemented in this PR (`NextPreset` / `GoToPreset`).
- **Custom actions** (run a program, call a webhook): new action types in the grouped picker, where the disabled "Custom (coming later)" placeholder sits. Security and trust model to be designed separately.

## 8. Tests (to be planned in Task 2)

- `SettingsCoverageTests`: every row of §1 is editable in the new UI.
- `ConfigMigrator` v2 → v3 tests, including a real-config fixture and the toggle-pair move.
- `ButtonFeedback.Compute` unit tests: every action type, every preset, first-stateful-action rule.
- Runtime tests: preset switching, dispatch by active preset, feedback re-push on preset change, `None` sliders ignored.
- Protocol encoder/parser tests for `0x05`; removal of `0x03`/`0x04`.
- UI headless tests: add / reset / remove-last, drag and Alt+arrow reorder moving config with the card (and per-preset actions for buttons), preset tabs and live badge, tab order per page, `ButtonsPage_FriendlyNameOnlyDevice_IsSelected` and the `OsdWindow_*` tests still passing.
- Firmware: a native PlatformIO test (or a host-compiled unit) for the indicator table's value → pin mapping, if practical; otherwise covered by the manual checklist.

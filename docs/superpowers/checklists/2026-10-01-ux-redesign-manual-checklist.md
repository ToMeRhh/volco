# UX Redesign — Manual Checklist

Run on Windows with the build from this branch (`gh workflow run build.yml --ref claude/kind-hawking-pjhbrp --field run_build=true`), and the firmware from this branch flashed to the controller. Tick each item.

## 0. Before you start

- [ ] Copy your current `%APPDATA%\Volco\config.json` somewhere safe.
- [ ] Flash the firmware from `firmware/` (`pio run -t upload`). It must compile; nothing on this branch could run PlatformIO.

## 1. Migration of your real config

- [ ] First start: the app comes up with no error dialog.
- [ ] `%APPDATA%\Volco\config.v2.bak` exists and is byte-identical to your old `config.json`.
- [ ] The new `config.json` has `"schemaVersion": 3`, a `presets` list with Default, `actions.default` under every button, and no `outputDevices`.
- [ ] Every slider shows what it controlled before (apps, "All other apps" for the old Unmapped, or the device).
- [ ] Every button shows its old actions under the Default preset.
- [ ] The toggle-output action shows your old Device 1 / Device 2 as A / B.

## 2. Every old setting is still configurable and persists

For each row, change it, close Settings, restart the app, and check it stuck.

| | Setting | Where now |
|---|---|---|
| [ ] | Connection type | Connection → Connect using |
| [ ] | COM port | Connection (USB cable) |
| [ ] | Baud rate | Connection (USB cable), visible without Advanced |
| [ ] | UDP listen port | Connection (Wi-Fi (UDP)) |
| [ ] | WebSocket port | Connection (WebSocket) |
| [ ] | Slider: apps | Sliders → card → Apps chips (typing suggests running apps) |
| [ ] | Slider: all other apps | Sliders → card → All other apps (hint lists the excluded apps) |
| [ ] | Slider: device / current default output | Sliders → card → A device |
| [ ] | Slider: invert, curve, dead zone, smoothing | Sliders → card → Advanced |
| [ ] | Button: press / double / hold actions | Buttons → card |
| [ ] | Button: action device | inline under the action |
| [ ] | Button: Test | Test next to each action |
| [ ] | Button: hold after, double-press window | Buttons → card → Advanced |
| [ ] | Toggle outputs A / B | inline under "Toggle between two outputs" |
| [ ] | Detailed logging | General → Troubleshooting |
| [ ] | GitHub token, development builds, check / update | General → Updates (release build only) |

## 3. Cards

- [ ] Sliders and buttons are numbered 0, 1, 2… in the controller's order.
- [ ] Moving a physical slider moves the matching card's level bar and flashes the card.
- [ ] Pressing a physical button flashes its card.
- [ ] Dragging a card by its handle: it follows the pointer, the others slide aside, numbers relabel while dragging, and the drop moves the settings to that physical control (check by moving the slider/button).
- [ ] Escape during a drag puts everything back.
- [ ] Alt+Up / Alt+Down on a focused handle moves the card and keeps focus on it.
- [ ] Reset (⋯ menu) empties the card ("Not set up", quieter outline). Undo brings it back.
- [ ] "Remove last …" appears only on the last card. Undo brings it back.
- [ ] Add slider / Add button appends a "Not set up" card, opened.

## 4. Presets

- [ ] With one preset, only "Add preset" shows.
- [ ] Add preset copies the edited preset's actions and opens the name box.
- [ ] Rename (Enter commits, Escape cancels) and Delete (with Undo) work.
- [ ] The live preset has a green dot. Editing another shows "You are editing X. The controller is using Y." and "Make X live" works.
- [ ] A button with "Next preset" on Hold switches presets on the controller: the tabs' green dot moves, the OSD shows the preset name, and the other buttons now do the new preset's actions.
- [ ] The app always starts on the first preset.

## 5. Controller lights (current hardware)

- [ ] Both mute LEDs are off at boot (no false "muted" flash after re-plugging).
- [ ] Mute LEDs light while muted, from the controller and from the Windows mixer.
- [ ] The device-select LED pair shows which toggle output is active; both go off when the output is neither A nor B.
- [ ] After a preset switch, a button with no stateful action in the new preset goes dark.
- [ ] A button with a preset action shows which preset is live (LED pair: 1 → A, 2 → B).
- [ ] The light previews on the button cards match the hardware.

## 6. Simulator

- [ ] `tools/simulator/index.html` connects over WebSocket, slider and button counts can be changed, and feedback values show per button.

## 7. OSD (Step 2 parity, plus presets)

- [ ] The OSD never takes focus and clicks pass through it.
- [ ] Slider moves show the label and percentage; repeated moves don't flicker or restack.
- [ ] Mute shows "Muted"; unmute shows the real level.
- [ ] Output switch shows the speaker icon and the new device.
- [ ] Preset switch shows the preset icon, the preset name and "Preset", with no bar.
- [ ] It looks the same (dark) in a light Windows theme.

## 8. Keyboard only

- [ ] Every page can be used with Tab / Shift+Tab / Space / Enter / arrows only; the focus ring is always visible.
- [ ] Tab order: navigation → page header button → each card's handle, header, ⋯ in order.

## 9. Themes

- [ ] Light and dark both look right (Windows Settings → Personalisation → Colours).
- [ ] High contrast: everything is readable. (No dedicated high-contrast palette yet; see the PR's follow-ups.)

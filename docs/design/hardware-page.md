# Hardware page: the controller drawn with its wiring

> On branch unified-controller-page this two-page design is superseded by `docs/design/controller-page.md`.

Date: 2026-10-20. Status: design, from the approved pitch.

The pitch, with its mocks, is the reference for anything this text leaves visual: <https://claude.ai/code/artifact/f4a92632-870d-42cb-a9b8-9a58f3b01c0c> (private to the owner). Mock numbers below are the pitch's. Related: `docs/design/controller-config.md` §8 (today's page, whose rules this keeps unless it says otherwise), `docs/design/controls-page.md` (the drawing and inspector this reuses), #108 (double slider smoothing).

## 1. Goal

Today's Hardware page is five stacked panels in one long scroll (the pitch's "What goes wrong" table lists twelve problems). It asks people to think like the firmware: GPIO numbers typed into 0–63 boxes, ADC units, protocol states. The status panel alone can show nine lines and fourteen buttons.

The page becomes the Controls page's pattern:
- **One device card** that says what is going on and offers at most one next step.
- **The same drawing** of the controller as Controls, each tile decorated with its wiring instead of its actions.
- **An inspector** that edits the selected tile; pins are picked from a searchable list, never typed.

### Out of scope

- An arrangeable drawing that matches the physical case (as on Controls).
- Slider calibration (`docs/design/slider-calibration.md`, parked). It will add rows to the slider inspector; this design leaves room for them.
- Other chips than the ESP32 (the Arduino work, `arduino-avr-support`, plugs into the same pin list through `ChipInfo`).

## 2. Decisions (from the pitch review, 2026-10-19 and 2026-10-20)

1. **Controller-wide settings** (screens, input feel, Wi-Fi) open in the inspector from a **Controller settings** chip in the title row.
2. **Detect and Find screens are part of this work**, with the firmware scan modes they need (§9).
3. **Port:** Automatic by default, with a manual list in the Connection dialog.
4. **One transport at a time:** USB, Wi-Fi or Simulator.
5. **Live input always shows on the Hardware page:** slider levels and presses, without starting a test. Test is for the lights and for stopping input from reaching audio and actions.
6. **Any slider or button can be removed,** with a warning that later controls are renumbered.
7. **Smoothing (#108):** the firmware's smoothing stays here (Input feel, since split into Sliders and Buttons by `slider-feel-settings.md`); the app-side per-slider Smoothing is removed from Controls › Response.
8. **The controller is called "Volco controller"** on the card and in the tray tooltip.
9. **No jump between Controls and Hardware:** one shared drawing at identical geometry; only what is on and around it changes, with crossfades (§3.3).
10. **Pins are picked in a pin dialog showing every pin at once** (user, 2026-10-23; it replaced the fuzzy-searchable dropdown of the first build). Every GPIO the chip has, eight to a row by number, each saying free, its owner, or why it can't be used. While it is open, moving a slider or pressing a button marks its pin by colour, without picking it (§6.3). No assumptions about the dev kit's pinout.
11. **Minimal UI,** as on Controls: no page subtitle, legends or explanatory captions beyond what the mocks show; tooltips carry detail.

## 3. Page structure

### 3.1 Window

Unchanged from Controls (§3.1 there): 1540 × 900 default (changed 2026-10-22; was 1100 × 720), 820 × 520 minimum, the 1000 px breakpoint (168 px labelled sidebar and 380 px side panel, or 56 px icon sidebar and 300 px side panel). Only the drawing scrolls.

### 3.2 Layout

Left to right, as on Controls: the title row and drawing, then the **side panel**. On Hardware the side panel holds the device card (§4) at the top and the inspector (§6) under it.

**Title row:** "Hardware", then **+ Add** (a menu: Slider, Button), the **Controller settings** chip, and **Test** (§8). The row has the same height as Controls' title row.

### 3.3 One stage for Controls and Hardware (no jump)

- **One drawing control, hosted by the settings window.** `SettingsWindow` hosts the drawing (case, slider grid, button grid) once, in a shared **stage**. Controls and Hardware are two decorations of it: switching between them never rebuilds or re-lays out the tiles.
- **Same geometry.** Both pages lay out from one measure: the title row has the same height; button tiles reserve Controls' action-row height on Hardware; Hardware keeps the space of the mode tabs and frame (they fade out, they do not collapse).
- **Page-only things never push tiles.** Hardware's Add, Controller settings and Test sit in the title row; its I2C bus chips sit under the last row of buttons inside the case (space reserved on both pages when any screen exists).
- **What changes crossfades in 150 ms:** the title word, the title-row buttons, each tile's captions (actions or pins), the mode frame and tabs, and the side panel's content. Nothing slides. With Windows animations off it swaps at once, still without a jump.
- **Selection and scroll carry over:** Slider 3 selected on Controls is selected on Hardware, at the same scroll position.
- **General has no drawing,** so going there crossfades the whole content area.
- **States that replace the drawing** (the setup chooser, §7.1) fill the same case rectangle.
- **Leaving Hardware with unsaved changes asks first** (user, 2026-10-20), for any page: **Save and continue** (waits for the controller's confirmation, with the progress in the prompt; a failure keeps the user on Hardware with the error on the card), **Discard and continue**, **Stay**. Save is disabled while the draft has problems. Closing the window to the tray (its X, Alt+F4) asks too (user, 2026-10-24, replacing "hiding doesn't ask"): the Controller page shows under the prompt, whose buttons are **Save and close**, **Discard and close** and **Cancel**. Exit from the tray and an OS sign-out don't ask. Controls has no unsaved state (its edits save at once), so this is the only case.
- **Both pages therefore draw the same tiles.** With the prompt and the save rule of §6.5, an unsaved draft never meets Controls, and a saved one leaves Volco's settings and the controller with the same controls. The tiles re-lay out only when the controller and Volco's copy disagree for reasons the card already reports (a different setup, a controller erased by Reinstall, another controller plugged in); Controls keeps its dashed "Not on this controller" tiles for those cases.

## 4. The device card

The card replaces today's status panel, the §8.3 banner and the firmware row. It shows the name **Volco controller**, a status line with a coloured dot, one detail line, and at most one primary button. A **⋯** button holds the rare actions.

### 4.1 States

The card reads `VolcoRuntime.LinkStatus`, the §8.3 banner (`ControllerBannerLogic`, unchanged as logic) and the firmware notice. The first matching row wins.

| When | Status line | Detail | Button |
|---|---|---|---|
| A firmware install, update or probe runs | "Updating firmware… 62%" (or Installing, Checking) with a progress bar | "Don't unplug the controller." | — |
| A config save runs | "Saving to the controller…", then "Restarting…" | — | — |
| `Disconnected` (the user's) | "Disconnected" (grey) | "You disconnected Volco from the controller." | **Connect** |
| `PortBusy` | "COM3 is in use by another app" (red) | "Close the serial monitor or Arduino IDE." | Retry |
| `NoAnswer` | "No controller answers on COM3" (red) | "It may not have Volco's firmware yet." | **Check firmware** |
| `Searching` | "Looking for your controller" (grey) | "Plug it in with a USB cable." | — |
| `Incompatible` | "Firmware too old for this Volco" (amber) | "Update it to use the controller again." | **Update** |
| Banner: not set up (configState 0) | "Not set up yet" (accent) | "Choose how to start, below." | — (the chooser fills the drawing, §7.1) |
| Banner: the controller's setup can't be used (configState 2) | "The controller's setup can't be used" (amber) | "Start again from Volco's copy or from scratch, below." | — (the chooser, §7.1, offers Volco's copy first) |
| Banner: saved setup doesn't work with this firmware | "Your setup doesn't work with this firmware" (amber) | "Fix it in the drawing, then Save." | **Fix it** |
| Banner: a different setup | "This controller has a different setup" (amber) | "It isn't the one Volco saved for it." | **Compare…** (§7.2) |
| Banner: couldn't read the setup | "Couldn't read the controller's setup" (red) | — | Retry |
| Firmware notice | "Firmware update available" (amber) | "Your setup stays on the controller." | **Update** |
| Connected, in sync | "Connected over USB (COM3)" / "over Wi-Fi" / "to the simulator" (green), with › | "Firmware up to date" (or "Development firmware") | — |

- The status line, in every state that has a connection, opens the **Connection dialog** (§10) on click (the ›).
- Port names come from the configured or automatic port (§10.1).
- After a firmware install or a first save the card shows "Test your controller now" as its button; it starts Test (§8).
- Failures of an operation (save, install, update) replace the detail line with the §8.7 error message and keep it until the next action; the button becomes Retry where today's page offers one.
- The sidebar footer and the tray keep reading the same `LinkStatus`, so all three agree.

### 4.2 The ⋯ menu

Connection… · Reconnect · Disconnect · ― · Check firmware · **Reinstall firmware…** (red; today's Install, still confirmed first, still erases the setup) · ― · Load a preset… · Start over…

- **Load a preset…** opens a small chooser of `ControllerPresets` (today's "Start from" picker). It replaces the wiring in the draft, asking first when the draft has controls; nothing is sent until Save.
- **Start over…** returns the drawing to the setup chooser (§7.1), keeping the draft until a choice is made; Escape or the chooser's Back cancels it, and so do Discard and leaving Hardware. The save bar hides while the chooser shows, and Start over is offered only while the controller is ready.
- Items that write to the controller are disabled while an operation or a test runs (§8.3 rule, unchanged).

## 5. The drawing on Hardware

### 5.1 Which controls

The drawing is **the draft's** controls (the config being edited), not the Hello's. A control added in the draft is drawn like the others, with the change dot (§5.2). Before any Hello, and when the page is read-only (§5.4), the drawing shows Volco's saved copy (the mirror).

### 5.2 Tile captions (instead of actions)

- **Slider:** "Slider N" and its pin ("GPIO 33") as a pill. The fader shows the **live raw level** (decision 5).
- **Button:** "Button N" and its pin as a pill. Above the cap, the indicator's wiring: an LED's pin ("12"); a pair's pins ("18 · 19"); a screen's bus and address ("Bus 0 · 3C"). A press flashes the tile (as on Controls).
- **I2C bus chips:** one chip per bus that a screen uses, under the buttons: "I2C bus 0 · SDA 25 · SCL 26". A click selects the bus (§6.4).
- A tile with a draft change gets a dot until it is saved. A tile with a problem (§6.5) gets the danger outline and the problem as its tooltip.

### 5.3 Selection

As on Controls: a click, Enter or Space selects; the accent outline; arrow keys move focus. Input never selects. The Controller settings chip and bus chips are selectable too.

### 5.4 Read-only

Editing follows today's rule (controller-config §8.4: the link is Ready and in sync, or a "new draft" permission). Read-only, the drawing is greyed, the inspector's fields are disabled, and the card says why (its state row). Offline, the drawing shows the mirror greyed; with no mirror, an empty case with the card's state.

## 6. The inspector

The header names the selection; rows are one line each (44 px) and keep their place.

### 6.1 Slider

- **Header:** "Slider N", with "Controls: <target label>" in secondary text (from Controls, read-only here).
- **Pin** (§6.3).
- **Remove Slider N** (red, at the bottom).

### 6.2 Button

- **Header:** "Button N", with its main action in secondary text ("Toggles Speakers / Headphones").
- **Button pin** (§6.3).
- **Light:** a segment None / LED / 2 LEDs / Screen. It reshapes only the rows under it:
  - LED: **Light pin**, **Lit when pin is** High / Low.
  - 2 LEDs: **Light A pin**, **Light B pin**, **Lit when pin is** High / Low.
  - Screen: **Bus** (0–3, 2 and 3 marked "software") and **Address** (0x3C / 0x3D). **Find screens** lists the addresses that answer on each bus card of the I2C buses dialog (§9.2), one row each ("0x3C  Screen found, used by Button 1", "0x3D  Screen found, no button uses it"), then each configured screen that didn't ("0x3C  Button 1's screen didn't answer", in red); filling a button's Address from it is a follow-up.
- **Try the lights** (LED and 2 LEDs): flashes them on the controller (a single LED lit, unlit, lit, 300 ms each; a pair A lit with B unlit for 600 ms, then B lit with A unlit; every step drives its level, so a lit LED still flashes), for saved and unsaved pins alike (§9.3). Decision (2026-10-22): Try always uses PinDrive, so a saved LED is driven like an unsaved one instead of through the test indicators.
- **Remove Button N.**

### 6.3 The pin field

One control for every pin: button, slider, light, bus SDA/SCL.

- **Closed:** a button with a chip icon and the value ("GPIO 5", or "Not set"); no dropdown chevron, since it opens a dialog.
- **Open:** the pin dialog over the window (decision 2026-10-23, replacing the filtered dropdown): every GPIO the chip has (`ChipInfo.Exists`) at once, eight to a row in number order, with an empty place where the chip has no pin (20, 24, 28–31), so a pin's column never moves. The field's pin has an accent border; unusable pins are greyed. Arrows move the selection (Left/Right along a row, Up/Down along a column, over unusable pins), Enter or a click picks, Escape, Cancel or a click on the scrim keep the old value.
- **Each pin's note, under its number:**
  - **free**;
  - **its owner** ("Slider 1", "Button 2 light", "I2C bus 0 SDA"); taken pins can be picked;
  - **why it can't be used for this role**, greyed and not pickable: "USB serial" (1, 3), "flash" (6–11), "no analog input" (a slider on a non-ADC pin), "input only" (a light or bus on 34–39).
- **Problems on the field** (from `DraftProblems`, which already lists both ends of a conflict): the field gets the danger outline and the message under it ("Slider 1 also uses GPIO 33."), with one fix where there is one: **Swap with Slider 1** (exchanges the two pins in the draft). The other control's tile and field are marked too. Save waits until every problem is fixed. Today's informational hints become notes on the row: "no pull-up" for a button on 34–39, "not with Wi-Fi" for a slider on ADC2 while Wi-Fi is on.
- **Detect (the pin lights up):** while the dialog is open and the controller is connected with firmware that supports it, Volco asks the controller to watch its pins (§9.1), and the dialog says "Listening for slider movements…" (or "Listening for button presses…") beside a pulsing green dot, with "A pin you move gets a green dot." under it on the grid. Moving a slider (for a slider's pin) or pressing a button (for a button's pin) marks that pin, by colour only and never by words (user, 2026-10-23): **moving now**, a green dot and a green edge (user, 2026-10-24; was an accent fill), until 1 s passes without another report; then **moved before**, tinted with an accent dot, until the dialog closes. Several pins can be marked at once. Marking never picks a pin or moves the selection: the user picks. While the watch is on, the controls change no volume and run no action (§9.1). The watch stops when the dialog closes, the selection moves to another tile, the page is left or the window hides; it times out on the controller 30 s after the last renewal if Volco goes away. A pin's mark is the controller's report, not a guess: an older firmware simply marks nothing.

- **Adding a control** (user, 2026-10-24): + Add › Slider (or Button) adds it, selects its tile and opens its pin dialog to add, titled "Add Slider 6". While Detect can listen, the dialog starts with **no pins**: only the pins the user moves or presses appear, as tiles of the grid's size, first heard first, taken pins included (with their owner, pickable as on the grid). The first pin heard is selected, a click selects another, and the arrows move along the list. **or Choose manually** (a secondary link, bottom left) switches to the full grid, keeping the selection and the marks. **Add slider** (or **Add button**, primary, disabled until a pin is selected) gives the new control the pin; Enter does the same. **Cancel**, Escape, the scrim, or the dialog closing any other way (the page left, the window hidden, the draft no longer editable) removes the new control, and the selection goes back to what it was. Without a watch, it opens on the grid. Only the button's own pin is asked for; its light is set up afterwards in the inspector. Opened from an existing pin field, the dialog is unchanged: the grid, a click picks and closes.

### 6.4 I2C bus

Selected from its chip: **SDA** and **SCL** (pin fields), **Clock** (100 / 400 kHz, today's values), **Find screens** (§9.2: lists the addresses that answer and which buttons use them).

### 6.5 Problems and Save

- Every problem in the draft shows on its field and tile (`DraftProblems`, unchanged rules), including the controller's `ConfigResult` INVALID mapped to its field.
- **Save bar** across the top of the drawing area (user, 2026-10-24: was at the bottom), amber (`WarningSoftBrush` with a `WarningBrush` edge and warning sign) so it stands out from the neutral status banner above it, shown while dirty: "1 change not saved" (count of changed tiles plus changed controller cards, each counted once; Sliders if weight or deadband changed, Buttons if debounce, hold or double press changed), "Saving restarts the controller" (not shown when only the two button timings changed), **Discard**, **Save**. It replaces today's snackbar for the draft; the save's progress and result go to the card (§4.1). "This setup isn't on the controller yet" replaces the count for a new draft.
- **Removing a control** (any one, decision 6) asks a plain "Remove Slider 3?" and nothing more: the drawing shows the result. The controls after it are renumbered and **keep what they do** (user, 2026-10-20): in the draft, the tile that was Slider 4 is now Slider 3 and still shows Slider 4's target and actions. On the controller's confirmation of the save, Volco's settings are rewritten to match: the removed control's entry is deleted and the later ones shift down (`sliders[i]`, or `buttons[i]` with its actions in every mode, and the rest). A discarded or failed save changes nothing in Volco's settings. This replaces today's rule (actions stayed with the number).
- The UDP warning before saving Wi-Fi changes while connected over Wi-Fi is kept (controller-config §8.4).

### 6.6 Controller settings (the chip)

Header "Controller" (the controller's chip and counts moved under the sidebar's status line, controller-page spec §5). Four sections (Screens, Sliders, Buttons, Wi-Fi; Input feel was split by `slider-feel-settings.md`), each opened and closed on its own (Screens, Sliders and Buttons can be open together; user, 2026-10-22), each a card (controller-page spec §4.3, Cards): the header (in bold) is the card's first row, and the section's rows sit under it in the card, indented, and indented and tinted again for a sub-option or a sub-section (Fine tune). A setting whose label doesn't say enough has a "?" in front of its label whose tooltip (on hover) explains it; the plain ones (Brightness, Dim after, Dimmed to, Turn off after, Wi-Fi) don't (user, 2026-10-22).

- **Screens** (only when a button has a screen; summary "80%, off after 30 min"): Brightness (a % slider over 0–255), Dim after (Never, 1, 2, 5, 10, 30 minutes, 1 hour), Dimmed to (Low, Medium), Turn off after (same list) with its sub-option **When off, the first press only wakes it** (the firmware swallows the press only while the screens are off, not dimmed; disabled while Turn off after is Never, and its value is kept), Prevent burn-in (pixel shift on/off), I2C buses (a summary and Edit, which opens the I2C buses dialog, controller-page spec §4.3). **Fine tune** holds the scroll, slide and pixel-shift timings as today's numeric fields.
- **Sliders** (summary "Normal"; replaces Input feel, `slider-feel-settings.md`): Smoothing Low / Normal / High, mapped onto the firmware's smoothing weight and deadband (Feels (40, 15), (20, 25), (10, 40)). **Fine tune:** Smoothing weight, Ignore changes below; a raw value off the three pairs shows the segment as Custom.
- **Buttons** (summary "500 ms · 300 ms"): Hold after (200-2000 ms, step 50) and Double press within (100-1000 ms, step 50), controller-wide and stored by Volco in `config.json` (`buttonTiming`) but part of this draft, so Save commits them (a Save with only them sends nothing to the controller). **Fine tune:** Debounce. Read sliders every is not in the UI (the proto field is kept and a value set elsewhere is saved unchanged).
- **Wi-Fi** (summary "Off" or "On · HomeNet"): opens the Connection dialog on Wi-Fi (§10.2).

Values that don't land on a list entry (a config set elsewhere) show as "Custom (90 s)" and are kept.

## 7. Setup and a different setup

### 7.1 The setup chooser

Replaces today's setup dialog. It fills the case rectangle when the controller is not set up, its setup can't be used, or the user chose Start over:

"How do you want to start?" / "Nothing is sent to the controller until you Save." and one card per preset (after Volco's saved copy when there is one, before Start empty), each with a small picture of its controls:
- **Volco's saved copy** (only with a mirror; first and outlined): "5 sliders, 4 buttons, 1 screen".
- **A preset** per `ControllerPresets` entry (today: DIY reference).
- **Start empty** (dashed): "Add sliders and buttons yourself, with Detect".

Picking one fills the drawing as a draft (the "new draft" permission, as today) and the save bar says "This setup isn't on the controller yet."

### 7.2 Compare (a different setup)

**Compare…** on the card opens a dialog: "The controller has a different setup", two columns ("On the controller", "Volco's copy"), each a small picture and only the lines that differ (counts, pins that moved, Wi-Fi, screens), and a button under each:
- **Keep the controller's setup** (primary, the safe default): reads it and adopts it as Volco's copy. "The controller is untouched."
- **Send Volco's copy**: today's Apply mine. "The controller restarts with it."

Reading the controller's setup to compare is today's read (the user's click on Compare starts it); a failed read shows "Couldn't read the controller's setup" with Retry.

## 8. Test on the drawing

Replaces the test dialog. **Test** in the title row toggles it (**Stop test**):
- The case gets an accent outline and a band: "Testing: sliders and buttons don't change volume or run actions."
- Tiles keep their places; the inspector is hidden; editing is paused.
- Sliders show their raw value ("2310"); buttons show pressed; an LED or pair shows its test state and a click on it switches it (Off/On, Off/A/B); a screen shows the test screen and counts presses ("3 presses").
- The runtime's rules are today's (controller-config §8.5): input reaches neither audio nor actions. Stop, leaving the page or hiding the window ends it.

Outside a test the drawing still shows live levels and presses (decision 5), and input works normally.

## 9. Firmware and protocol

Three helpers need the firmware. Each ships behind the build id: an older firmware simply doesn't answer, and the app hides or no-ops the feature (no compatibility work beyond that).

### 9.1 Pin watch (Detect)

| Type | Name | Direction | Body |
|---|---|---|---|
| `0x8B` | PinWatch | PC→controller | role (0 stop, 1 analog, 2 button) |
| `0x07` | PinActivity | controller→PC | pin |

- While a watch is on, the controller samples every usable pin for the role that is not an output (configured LEDs) and not a configured I2C pin: for analog, the ADC-capable pins (ADC2 skipped while Wi-Fi is up); for button, every usable pin with its pull-up on except free 34–39 (no pull-up, so they would float; a button configured there is still watched). Decision (2026-10-22): only the role's own kind of configured pin is included: an analog watch samples free ADC pins and configured slider pins, a button watch free pins and configured button pins, so a taken pin of that kind lights up too, and a pin of the other kind is never touched (no pull-up lands on a slider). Thresholds are `volco_pin_helpers.h` constants (an analog pin must get 300 codes from a slowly following baseline with little jitter, so a slow drift never reports; a button must be low 30 ms after 500 ms high).
- It reports a pin that moved (analog) or went low (button), at most every 250 ms per pin. **A floating pin must not light up:** the builder picks the thresholds (for example a large, sustained change) and checks them on the hardware.
- The controller keeps reporting input during a watch, but Volco ignores it, as in the hardware test (§8.5; user, 2026-10-23): slider and button messages raise only raw input (the live meters) and reach neither audio nor actions, so moving a control to find its pin changes nothing. A gesture started before the watch never fires, nor one held across its end. Pins the watch changed go back to their previous mode when it stops.
- It stops on role 0, on host loss, and 30 s after the last PinWatch (the app renews every 10 s while the pin dialog is open).

### 9.2 Find screens

| Type | Name | Direction | Body |
|---|---|---|---|
| `0x8C` | I2cScan | PC→controller | SDA pin, SCL pin |
| `0x08` | I2cScanResult | controller→PC | SDA, SCL, count, addresses |

The controller scans 0x08–0x77 on the given pins (the configured bus when the pins are one, else a temporary software bus) and answers. Refused pins (not usable, input only, SDA = SCL, or a pair that shares a pin with a configured bus without being that bus) answer count 0. The app waits 3 s for the result. A draft pair that the controller would refuse that way (it shares a pin with a bus a screen uses in the saved setup without being it) isn't sent: the row says "Save first to scan these pins." No answer shows only "The controller didn't answer." with no rows.

### 9.3 Drive a pin (Try the lights on unsaved pins)

| Type | Name | Direction | Body |
|---|---|---|---|
| `0x8D` | PinDrive | PC→controller | pin, level (0/1), duration (u16 ms) |

Drives an output-capable pin that is free or a configured LED pin, then restores it. Refused silently otherwise. Decision (2026-10-22, replacing "saved LEDs keep using today's test indicators"): Try the lights uses PinDrive for saved and unsaved LED pins alike. A Led frame for a driven pin waits until the drive ends, then the LED shows the PC's last value.

### 9.4 Common

- Golden frames for each message in `firmware/test/golden/frames.txt`; parser, encoder and simulator tests.
- `kProtocolVersion` stays 1 (app and firmware ship together).
- The simulator answers all three: its pins are its builder's layout; dragging a slider or clicking a button while a watch is on reports its pin; Find screens reports the screens it draws.
- Slider calibration takes `0x8E` Calibrate (PC→controller) and `0x09` Calibration (controller→PC); see `slider-calibration.md`.

## 10. Connection dialog

Rewritten 2026-10-25 for #119 (user review of the mocks: choice cards, Windows' port descriptions, no numbered Wi-Fi headings). It replaces the interim version, whose tab strip only switched the view, with a separate **Use …** button and Wi-Fi fields that waited for the save bar, so it was unclear what switching tabs did.

Opened from the sidebar status menu's **Connection…**, and from Controller settings › Wi-Fi (on the Wi-Fi card; the row's summary reads "Off" or "On · HomeNet").

**Unsaved controller changes come first** (user, 2026-10-26): opening the dialog with unsaved changes shows the leave prompt (`LeavePromptViewModel`: Save and continue / Discard and continue / Stay), worded for opening Connection, and the dialog opens only once the draft is clean. So every save the dialog makes (§10.2, §10.3) starts from the controller's saved config with no draft to merge or leave stale. Asking on open rather than only on the Wi-Fi card is my call: it keeps one rule for the whole dialog.

### 10.1 Layout: one card per transport

Title: "How Volco reaches the controller". Three cards stacked: **USB cable**, **Wi-Fi**, **Simulator** (decision 4: one transport at a time). The footer has only **Close**; the old "Stored in Volco on this PC; applies at once." line goes.

- The card of the transport in use has an **In use** badge.
- Clicking a card opens it and closes the one that was open. Opening a card **never switches** anything. The dialog opens on the card in use, or on Wi-Fi when opened from Controller settings › Wi-Fi.
- **Only a button inside a card switches,** and its text says what it does: **Switch to USB**, **Save and switch** (Wi-Fi, §10.3), **Switch to the simulator**.
- Inside the card in use, edits apply at once (the USB port, the simulator's port). A card that is not in use keeps its edits until its switch button, and drops them on Close (my call).
- A closed card shows a one-line summary: USB "Automatic · found the controller on COM3" or "COM5"; Wi-Fi "On HomeNet · sending to 192.168.1.20" or "Off"; Simulator "Port 8765".

### 10.2 USB cable

- **Port** list: **Automatic (COM3)** first and the default (the parenthesis is the port Automatic is using now, left out when there is none), then each port as `COM3 · USB-SERIAL CH340`, `COM5 · Standard Serial over Bluetooth link`: Windows' own description, unedited (user, 2026-10-25, over a plain-language rename). The refresh button stays.
- **Automatic** (`connection.serial.comPort` empty): the app tries each port with a HelloRequest, the CH340 (`1a86:7523`) first, then `connection.serial.lastPort`, then the rest, and remembers the one that answered in `lastPort` (first next time). The status line and its tooltip name it: the card's status line reads "Connected over USB (COM3)", the tray's tooltip "Connected (COM3)".
- Automatic must not disturb other serial devices: it tries a port only while nothing else holds it, and closes it at once if it doesn't answer (the best candidate too: after a round with no answer no port stays open, and a new round runs on a port-list change, Reconnect or a firmware action). When the best candidate doesn't open because another app holds it (a serial monitor), the status is `PortBusy` on it ("Can't use COM7"), and Automatic tries that one port again every few seconds until it opens, then probes it as usual (final review ruling, 2026-10-27).
- How hard Automatic probes (my calls): with `ISerialPortInfo` it tries only USB-backed ports (a Bluetooth COM port is listed but never opened by Automatic); without it, every port. Each port is tried once per change of the port list and on Reconnect, never in an endless cycle, and each try waits long enough for an ESP32 reset by the open to boot and send its Hello.
- Picking a port for Check firmware (several candidates) makes it `lastPort`; `comPort` stays empty, so the setting stays Automatic (my call).
- **A board that doesn't answer** (blank or foreign firmware) with Automatic: no serial ports → `Searching`. Ports but no answer → `NoAnswer` on the best candidate (the CH340 if there is one, else the only port). Several candidates and no CH340 → the status says "No controller answers" without a port, and Check firmware first asks which port (today's dialog port picker). Firmware actions use the candidate, and a successful install makes it `lastPort`.
- Port descriptions and USB ids come from a new optional seam, `ISerialPortInfo` in `PlatformServices` (Windows: SetupAPI or WMI). Without it the list shows names only and Automatic tries the ports in name order.
- **Switch to USB** from Wi-Fi asks first, inside the card: "Plug the controller in, then switch. Volco turns its Wi-Fi off." with **Cancel** and **Switch to USB and turn Wi-Fi off**. Once a Hello arrives over USB, Volco saves the controller config with UDP off (the usual save, in the progress dialog). From the simulator it switches at once and the controller's config is untouched. If no USB Hello comes, Volco stays on USB, and the pending "turn Wi-Fi off" waits for the first USB Hello of this run of Volco and is dropped at exit (my call).

### 10.3 Wi-Fi: Save and switch

Fields, with no numbered headings (user, 2026-10-25):
- **Network**, **Password** (with Show), filled from the controller's saved config.
- **This PC's address**: a dropdown of this PC's IPv4 addresses, each with its adapter name ("192.168.1.20 · Ethernet"); the saved one is picked when it is still there.
- **Ports ›** folds the ports: the one Volco listens on (`connection.udp.listenPort`, also the controller's PC port) and the controller's own port.
- The line "Sent over USB, then Volco switches to Wi-Fi.", then **Save and switch**.

Save and switch (Volco on USB):
1. Requires a Hello over USB. Otherwise the button is disabled and a line under it says why ("Connect the controller over USB first; the Wi-Fi settings are sent through it.").
2. Writes the Wi-Fi settings into the controller config and saves it through `ControllerConfigService` (restart included), in the progress dialog: "Saved to the controller · restarting", then "Waiting for the controller over Wi-Fi… 9 s".
3. Switches Volco's transport to Wi-Fi and waits for a Hello (up to the fallback window below plus 15 s). On one, the Wi-Fi card is **In use** and closed with its summary.
4. No Hello: switches back to USB, and the open Wi-Fi card shows, in the danger colour, "The controller didn't answer over Wi-Fi. Check the network name and password." If the controller had Wi-Fi off before, it keeps the new settings; if it had other Wi-Fi settings, it has gone back to them (below).

**Save (Volco already on Wi-Fi)** (user, 2026-10-26): the same fields, the button reads **Save**, and the save goes over Wi-Fi. Volco moves its own listen port with it, waits as in step 3, and on success stays on Wi-Fi. If the controller doesn't come back on the new settings it falls back to the old ones; Volco returns to its old listen port, picks up the controller there, and says "The controller couldn't use the new Wi-Fi settings and went back to the old ones. Check the network name and password." If it answers on neither, the status shows `NoAnswer` as usual and USB is the way back.

**The controller's fallback** (firmware; user, 2026-10-26, details my call):
- When a config write changes the Wi-Fi section and the previous config had Wi-Fi on, the firmware keeps the previous Wi-Fi section in NVS (a new key next to the config) and marks the new one provisional.
- Booting on provisional Wi-Fi settings, the controller **confirms** them on the first valid frame from the PC over UDP. That proves the network, the password, the PC's address and the ports, not only that it joined the network. Confirming drops the kept copy.
- **Failed** means either case (user, 2026-10-26): it couldn't join the network, or it joined but no frame from Volco came. Either way, if no such frame comes within the fallback window (30 s from boot, my call), it writes the kept Wi-Fi section back into its stored config (the rest of the config unchanged) and reboots on it. USB keeps working throughout.
- A write that turns Wi-Fi off, or a write while Wi-Fi was off, keeps no copy.
- After a fallback the controller's config CRC differs from Volco's mirror. On the next Hello, Volco reads the controller's config and adopts it into the mirror without asking, when only the Wi-Fi section differs (my call; anything else is the usual different-setup banner).
- ~~The simulator's `config.js` does the same, so the flow can be tried without hardware.~~ (dropped at planning, 2026-10-26: the simulator speaks only WebSocket, so there is no UDP to fall back on.)

The draft-based Wi-Fi fields go: Controller settings › Wi-Fi is now a row that opens this card.

**Two later rulings** (user, 2026-10-26):
- **Turn Wi-Fi off** is a button on the Wi-Fi card while Volco is on USB, with no confirmation: it saves the controller config with Wi-Fi off (the same save as the turn-off in §10.2), which also drops the controller's kept copy, if any.
- **A provisional write keeps the existing kept copy.** The kept copy is always settings a PC answered on: a write made while the settings are still provisional leaves the copy as it is, unless the write turns Wi-Fi off or goes back to the copy, which drops it.

### 10.4 Simulator

Today's WebSocket option, named Simulator, with its port and **Switch to the simulator**.

## 11. Removed and moved

| Today | New place |
|---|---|
| Connection section, Connect/Reconnect/Disconnect | Connection dialog; ⋯ menu; the card's Connect |
| Status lines, port details, hardware summary, chip | The card (two lines); the drawing is the summary |
| §8.3 banner and its buttons | The card's state and one button; the chooser; Compare |
| Install / Update / Check firmware | ⋯ Reinstall firmware…; the card's Update; ⋯ Check firmware (the card's button when no controller answers) |
| "Start from" preset picker, "Based on …" | The chooser; ⋯ Load a preset… ("Based on" goes) |
| Slider and button rows, Add, Remove | Tiles; + Add; Remove in the inspector |
| Indicator rows, bus rows, Advanced › bus clock | Button inspector; bus chips and their inspector |
| Test dialog | Test on the drawing |
| Screen behaviour, Advanced › input tuning, Advanced › Wi-Fi | Controller settings |
| Snackbar (draft, save progress) | Save bar; the card |
| Setup dialog | The chooser |
| Install confirmation | Kept, from the ⋯ menu |

The tray's "Update controller firmware…" and the sidebar's attention dot keep opening the Hardware page.

## 12. Core and App changes

### 12.1 Core

- **Protocol:** the five messages of §9 in `MessageType`, `ProtocolEncoder`, `ProtocolParser` and new `ControllerMessage` types; golden frames.
- **Runtime:**
  - `StartPinWatch(role)` returning a handle (renewal and stop inside), `PinActivity` event.
  - `ScanI2cAsync(sda, scl)`, `DrivePin(pin, level, duration)`.
  - **Raw input outside test mode** (decision 5): `RawInput` is raised always, not only in test mode (the page subscribes only while shown); test mode still blocks audio and actions as today.
  - Automatic port: the connection layer tries ports in order (§10.1); `LinkStatus` stays the one source for the card.
- **Config:** `connection.serial.comPort` empty = Automatic, `connection.serial.lastPort` (new, optional; no schema bump needed beyond the usual rules if the field is additive). **#108:** `sliders[i].smoothingFactor` is dropped (no schema bump: an old file's field is ignored on load and gone on the next save), and `SliderProcessor` loses its smoothing.
- **Settings follow removed controls:** the draft records which inputs were removed; on a confirmed save (`ControllerConfigService`'s success), one `ConfigManager` change deletes those entries from `sliders` and `buttons` and shifts the later ones down, `Index` rewritten as on every save. Added controls get empty entries as today (`AddMissing`).
- **Pin rows:** a Core helper lists every pin for a role with its state (free, owner, unusable reason) from `ChipInfo` and the draft, so the pin dialog and tests share it.
- **Tray tooltip:** "Volco controller" in its controller lines.

### 12.2 App

- **The stage** (§3.3): the drawing (case, `FillWrapPanel` grids, `SliderTile`, `ButtonTile`, the mode frame) moves out of `ControlsPage` into a stage control in `SettingsWindow`, with a decoration mode (Controls, Hardware). Controls' behaviour is unchanged.
- **Hardware page:** a new view model over today's `ControllerViewModel` logic (draft, banner, save, firmware, setup); the old `ControllerPage` view, its row view models (`HardwareRows`) and the test overlay go.
- **Pin field:** a control (text box + popup list) used everywhere a pin is edited, with `FuzzyMatcher`.
- **Controls › Response** loses Smoothing (#108; Response itself was removed later, `slider-feel-settings.md`).

## 13. Testing

Headless (`Volco.App.Tests`), replacing `ControllerPage` tests where they test removed UI:
- **Stage:** switching Controls ↔ Hardware keeps every tile's bounds (a layout test with 5 sliders and 4 buttons, with and without a screen, and dense); selection and scroll carry over.
- **Leaving with unsaved changes:** the prompt for each page; Save and continue waits for confirmation, a failure stays; Discard and continue; Stay; Save disabled with problems; closing the window asks the same, with close wording.
- **Removing a middle control:** after a confirmed save, Volco's sliders (target, name) and buttons (actions in every mode) after it have shifted down and the removed one's are gone; a discarded or failed save leaves them untouched; the draft's tiles show the shifted targets and actions before the save.
- **Card:** each row of §4.1 from a `LinkStatus` × banner × firmware matrix; the ⋯ menu's enabled states during an operation and a test.
- **Pin field:** rows and notes per role from `ChipInfo` and a draft; filtering; greyed rows can't be picked; Escape restores; a taken pick shows the problem and Swap fixes it; PinActivity lights up and scrolls to its row past the filter.
- **Inspector:** Light segment reshapes only its rows; Try sends PinDrive for unsaved pins and test indicators for saved ones; Remove with the renumber warning.
- **Controller settings:** list values map to and from config fields; Custom kept; the Sliders card's three pairs and Custom.
- **Chooser and Compare:** cards per mirror and presets; Keep adopts without writing; Send writes the mirror bytes.
- **Test on the drawing:** enters and leaves test mode; lights cycle; leaving the page ends it.
- **Connection dialog:** the cards (opening one switches nothing; In use; edits in a card not in use are dropped on Close); Automatic and manual ports in the list, with and without `ISerialPortInfo`; the Wi-Fi flow with a fake transport (Hello within 15 s stays on Wi-Fi; none → back to USB with the message); Save and switch disabled without USB; Save over Wi-Fi with the controller falling back (Volco returns to the old listen port and adopts the fallen-back config); opening the dialog with unsaved changes shows the leave prompt; Switch to USB from Wi-Fi asks, then saves with UDP off.
- **Core:** encoder/parser and golden frames for §9; pin rows helper; Automatic port order (CH340, then `lastPort`, then the rest), `lastPort` written on an answer and on a successful install, and the `Searching` / `NoAnswer` / several-candidates cases of §10.2; schema bump for #108.
- **Firmware native tests:** the Wi-Fi fallback (a copy kept only when Wi-Fi was on, confirmed by a UDP frame, restored after the window, nothing else in the config touched); the pin watch's report rules and timeout; I2C scan result encoding; PinDrive refusals.
- **Simulator tests:** the three messages.
- **`SettingsCoverageTests`:** every controller setting stays editable and survives a save and reload.

Windows and hardware manual checklist: Detect on a real board (no floating pin lights up; ADC2 with Wi-Fi), Find screens on buses 0–3, Try the lights on unsaved pins, Automatic port with the CH340 and another serial device plugged in (a Bluetooth COM port), the Wi-Fi flow with the right and a wrong password and its fallback, the crossfade at 820 × 520 and 1100 × 720.

## 14. Delivery

Separate PRs, in this order, each green on CI:

1. **The shared stage.** Controls' drawing moves into the window's stage; Controls looks and behaves the same; Hardware still shows today's page beside it.
2. **The Hardware page on the stage:** card and ⋯ menu, tile captions, inspector with the pin dropdown (no Detect yet), bus chips, save bar, Controller settings, the chooser and Compare, test on the drawing, live input, the crossfade. Today's page goes. The Connection section moves into the dialog with today's fields (port list without Automatic).
3. **#108:** Controls › Response loses Smoothing (no schema bump; Response itself was removed later, `slider-feel-settings.md`).
4. **Connection dialog proper** (delivered, #119): the cards, Automatic port, port descriptions (Windows seam, SetupAPI), the Wi-Fi flow (Save and switch, Save over Wi-Fi, Turn Wi-Fi off) and the controller's Wi-Fi fallback in the firmware. The simulator part was dropped (§10.3).
5. **Firmware helpers:** PinWatch (Detect), I2cScan (Find screens), PinDrive (Try on unsaved pins); app wiring; simulator.
6. **Docs:** CLAUDE.md (Hardware page paragraph, wire table, firmware), `firmware/README.md`, controller-config §8.7 points here, README screenshots.

## 15. Left to the builder

Names, file layout, exact pixel values the mocks leave open, the pin-watch thresholds (§9.1, checked on hardware), the Sliders card's three pairs, wording not given above, and test structure.

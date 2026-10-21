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
7. **Smoothing (#108):** the firmware's smoothing stays here (Input feel); the app-side per-slider Smoothing is removed from Controls › Response.
8. **The controller is called "Volco controller"** on the card and in the tray tooltip.
9. **No jump between Controls and Hardware:** one shared drawing at identical geometry; only what is on and around it changes, with crossfades (§3.3).
10. **Pins are a fuzzy-searchable dropdown in the inspector, no dialog.** Every GPIO the chip has, in number order, each row saying free, its owner, or why it can't be used. While the list is open, moving a slider or pressing a button lights up its pin's row (§6.3). No assumptions about the dev kit's pinout.
11. **Minimal UI,** as on Controls: no page subtitle, legends or explanatory captions beyond what the mocks show; tooltips carry detail.

## 3. Page structure

### 3.1 Window

Unchanged from Controls (§3.1 there): 1100 × 720 default, 820 × 520 minimum, the 1000 px breakpoint (168 px labelled sidebar and 380 px side panel, or 56 px icon sidebar and 300 px side panel). Only the drawing scrolls.

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
- **Leaving Hardware with unsaved changes asks first** (user, 2026-10-20), for any page: **Save and continue** (waits for the controller's confirmation, with the progress in the prompt; a failure keeps the user on Hardware with the error on the card), **Discard and continue**, **Stay**. Save is disabled while the draft has problems. Hiding the window does not ask: the draft is kept and the window reopens on Hardware. Controls has no unsaved state (its edits save at once), so this is the only case.
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
  - Screen: **Bus** (0–3, 2 and 3 marked "software") and **Address** (0x3C / 0x3D, with **Find screens** filling it, §9.2).
- **Try the lights** (LED and 2 LEDs): flashes them on the controller (a single LED twice; a pair A then B), for saved and unsaved pins alike (§9.3).
- **Remove Button N.**

### 6.3 The pin field

One control for every pin: button, slider, light, bus SDA/SCL.

- **Closed:** the value ("GPIO 5 ▾"), or "Not set".
- **Open:** a text box with the list under it, every GPIO the chip has (`ChipInfo.Exists`) in number order. Typing filters with `FuzzyMatcher` over the number and the row's note ("3" keeps 3, 13, 23, 30–39; "slider" keeps the pins sliders use). Up/Down move, Enter picks, Escape restores the old value, a click outside closes without change.
- **Each row's note, on the right:**
  - **free**;
  - **its owner** ("Slider 1", "Button 2 light", "I2C bus 0 SDA"); taken rows can be picked;
  - **why it can't be used for this role**, greyed and not pickable: "USB serial" (1, 3), "flash" (6–11), "no analog input" (a slider on a non-ADC pin), "input only" (a light or bus on 34–39).
- **Problems on the field** (from `DraftProblems`, which already lists both ends of a conflict): the field gets the danger outline and the message under it ("Slider 1 also uses GPIO 33."), with one fix where there is one: **Swap with Slider 1** (exchanges the two pins in the draft). The other control's tile and field are marked too. Save waits until every problem is fixed. Today's informational hints become notes on the row: "no pull-up" for a button on 34–39, "not with Wi-Fi" for a slider on ADC2 while Wi-Fi is on.
- **Detect (the pin lights up):** while the list is open and the controller is connected with firmware that supports it, Volco asks the controller to watch its pins (§9.1). Moving a slider (for a slider's pin) or pressing a button (for a button's pin) lights up that pin's row ("moving now" / "pressed now"), scrolls to it, and makes it the keyboard selection, even when the typed text had hidden it; Enter or a click picks it. Light and bus pins have no detection. Without support, nothing lights up.

### 6.4 I2C bus

Selected from its chip: **SDA** and **SCL** (pin fields), **Clock** (100 / 400 kHz, today's values), **Find screens** (§9.2: lists the addresses that answer and which buttons use them).

### 6.5 Problems and Save

- Every problem in the draft shows on its field and tile (`DraftProblems`, unchanged rules), including the controller's `ConfigResult` INVALID mapped to its field.
- **Save bar** at the bottom of the drawing area, shown while dirty: "1 change not saved" (count of changed tiles plus changed controller settings), "Saving restarts the controller", **Discard**, **Save to controller**. It replaces today's snackbar for the draft; the save's progress and result go to the card (§4.1). "This setup isn't on the controller yet" replaces the count for a new draft.
- **Removing a control** (any one, decision 6) asks a plain "Remove Slider 3?" and nothing more: the drawing shows the result. The controls after it are renumbered and **keep what they do** (user, 2026-10-20): in the draft, the tile that was Slider 4 is now Slider 3 and still shows Slider 4's target and actions. On the controller's confirmation of the save, Volco's settings are rewritten to match: the removed control's entry is deleted and the later ones shift down (`sliders[i]`, or `buttons[i]` with its actions in every mode, timing and the rest). A discarded or failed save changes nothing in Volco's settings. This replaces today's rule (actions stayed with the number).
- The UDP warning before saving Wi-Fi changes while connected over Wi-Fi is kept (controller-config §8.4).

### 6.6 Controller settings (the chip)

Header "Controller", with "ESP32 · 5 sliders, 4 buttons" in secondary text. Three sections, one open at a time:

- **Screens** (only when a button has a screen; summary "80%, off after 30 min"): Brightness (a % slider over 0–255), Dim after (Never, 1, 2, 5, 10, 30 minutes, 1 hour), Dimmed to (Low, Medium), Turn off after (same list), Prevent burn-in (pixel shift on/off), First press only wakes the screen. **Fine tune** holds the scroll, slide and pixel-shift timings as today's numeric fields.
- **Input feel** (summary "Normal"): Slider smoothing Low / Normal / High, mapped onto the firmware's smoothing weight and deadband (the builder picks the three pairs; Normal = today's defaults). **Fine tune** shows today's four raw fields (debounce, read sliders every, smoothing weight, ignore changes below); a raw value off the three pairs shows the segment as Custom.
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

- While a watch is on, the controller samples every usable pin for the role that is not an output (configured LEDs) and not a configured I2C pin: for analog, the ADC-capable pins (ADC2 skipped while Wi-Fi is up); for button, every usable pin with its pull-up on (34–39 read without one). Configured slider and button pins are included, so a taken pin lights up too.
- It reports a pin that moved (analog) or went low (button), at most every 250 ms per pin. **A floating pin must not light up:** the builder picks the thresholds (for example a large, sustained change) and checks them on the hardware.
- Normal input keeps working during a watch. Pins the watch changed go back to their previous mode when it stops.
- It stops on role 0, on host loss, and 30 s after the last PinWatch (the app renews every 10 s while the list is open).

### 9.2 Find screens

| Type | Name | Direction | Body |
|---|---|---|---|
| `0x8C` | I2cScan | PC→controller | SDA pin, SCL pin |
| `0x08` | I2cScanResult | controller→PC | SDA, SCL, count, addresses |

The controller scans 0x08–0x77 on the given pins (the configured bus when the pins are one, else a temporary software bus) and answers. Refused pins (not usable, input only) answer count 0.

### 9.3 Drive a pin (Try the lights on unsaved pins)

| Type | Name | Direction | Body |
|---|---|---|---|
| `0x8D` | PinDrive | PC→controller | pin, level (0/1), duration (u16 ms) |

Drives an output-capable pin that is free or a configured LED pin, then restores it. Refused silently otherwise. Saved LEDs keep using today's test indicators.

### 9.4 Common

- Golden frames for each message in `firmware/test/golden/frames.txt`; parser, encoder and simulator tests.
- `kProtocolVersion` stays 1 (app and firmware ship together).
- The simulator answers all three: its pins are its builder's layout; dragging a slider or clicking a button while a watch is on reports its pin; Find screens reports the screens it draws.
- Slider calibration (parked) will take the next free types; its design doc is updated to say so.

## 10. Connection dialog

Opened from the card's status line or the ⋯ menu. "Connection", "Stored in Volco on this PC; applies at once." A segment **USB / Wi-Fi / Simulator** (decision 4: one transport at a time). For now (interim rule, until the Wi-Fi steps of §10.2 are built) the segment only selects a tab; a tab that isn't the transport in use says **Use USB** / **Use Wi-Fi** / **Use the simulator**, which switches Volco's transport at once, and the one in use says "In use".

### 10.1 USB

- **Port:** **Automatic (COM3)** by default, then each port with its Windows description ("COM3 · USB-SERIAL CH340", "COM5 · Bluetooth link"). Done applies.
- **Automatic** (`connection.serial.comPort` empty): the app tries each port with a HelloRequest, the CH340 (`1a86:7523`) first, and remembers the one that answered (`connection.serial.lastPort`, used first next time). The card names the port it found.
- **A board that doesn't answer** (blank or foreign firmware) with Automatic (my call): with no serial ports, the state is `Searching`. With ports but no answer, it is `NoAnswer` on the best candidate (the CH340 if there is one, else the only port); with several candidates and no CH340 the card says "No controller answers" without a port, and Check firmware first asks which port (today's dialog port picker). Firmware actions use that candidate, and a successful install makes it `lastPort`.
- Port descriptions and USB ids come from a new optional seam (`ISerialPortInfo` in `PlatformServices`, Windows: SetupAPI or WMI); without it the list shows names only and Automatic tries them in order.

### 10.2 Wi-Fi

"1. The controller joins your network": Network, Password. "2. It sends to this PC": this PC's address, picked from its IPv4 addresses with their adapter names. **Ports ›** folds the three ports (filled from Volco's listen port). "Sent over USB, then Volco switches to Wi-Fi." **Cancel**, **Save and switch**:
1. Requires a USB connection to the controller (else the button says why).
2. Writes the Wi-Fi settings into the controller config and saves it (the usual save, restart included).
3. Switches Volco's transport to Wi-Fi and waits up to 15 s for a Hello.
4. No Hello: switches back to USB and says "The controller didn't answer over Wi-Fi. Check the network name and password." The controller keeps the Wi-Fi settings.

Turning Wi-Fi off from the dialog (choosing USB while on Wi-Fi) saves the config with UDP off once back on USB, after asking.

### 10.3 Simulator

Today's WebSocket option, renamed, with its port.

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
- **Config:** `connection.serial.comPort` empty = Automatic, `connection.serial.lastPort` (new, optional; no schema bump needed beyond the usual rules if the field is additive). **#108:** `sliders[i].smoothingFactor` is dropped (schema bump with the usual `.bak`), and `SliderProcessor` loses its smoothing.
- **Settings follow removed controls:** the draft records which inputs were removed; on a confirmed save (`ControllerConfigService`'s success), one `ConfigManager` change deletes those entries from `sliders` and `buttons` and shifts the later ones down, `Index` rewritten as on every save. Added controls get empty entries as today (`AddMissing`).
- **Pin rows:** a Core helper lists every pin for a role with its state (free, owner, unusable reason) from `ChipInfo` and the draft, so the dropdown and tests share it.
- **Tray tooltip:** "Volco controller" in its controller lines.

### 12.2 App

- **The stage** (§3.3): the drawing (case, `FillWrapPanel` grids, `SliderTile`, `ButtonTile`, the mode frame) moves out of `ControlsPage` into a stage control in `SettingsWindow`, with a decoration mode (Controls, Hardware). Controls' behaviour is unchanged.
- **Hardware page:** a new view model over today's `ControllerViewModel` logic (draft, banner, save, firmware, setup); the old `ControllerPage` view, its row view models (`HardwareRows`) and the test overlay go.
- **Pin field:** a control (text box + popup list) used everywhere a pin is edited, with `FuzzyMatcher`.
- **Controls › Response** loses Smoothing (#108).

## 13. Testing

Headless (`Volco.App.Tests`), replacing `ControllerPage` tests where they test removed UI:
- **Stage:** switching Controls ↔ Hardware keeps every tile's bounds (a layout test with 5 sliders and 4 buttons, with and without a screen, and dense); selection and scroll carry over.
- **Leaving with unsaved changes:** the prompt for each page; Save and continue waits for confirmation, a failure stays; Discard and continue; Stay; Save disabled with problems; hiding the window keeps the draft.
- **Removing a middle control:** after a confirmed save, Volco's sliders (target, name, response) and buttons (actions in every mode, timing) after it have shifted down and the removed one's are gone; a discarded or failed save leaves them untouched; the draft's tiles show the shifted targets and actions before the save.
- **Card:** each row of §4.1 from a `LinkStatus` × banner × firmware matrix; the ⋯ menu's enabled states during an operation and a test.
- **Pin field:** rows and notes per role from `ChipInfo` and a draft; filtering; greyed rows can't be picked; Escape restores; a taken pick shows the problem and Swap fixes it; PinActivity lights up and scrolls to its row past the filter.
- **Inspector:** Light segment reshapes only its rows; Try sends PinDrive for unsaved pins and test indicators for saved ones; Remove with the renumber warning.
- **Controller settings:** list values map to and from config fields; Custom kept; Input feel's three pairs and Custom.
- **Chooser and Compare:** cards per mirror and presets; Keep adopts without writing; Send writes the mirror bytes.
- **Test on the drawing:** enters and leaves test mode; lights cycle; leaving the page ends it.
- **Connection dialog:** Automatic and manual ports; the Wi-Fi flow with a fake transport (Hello within 15 s; none → back to USB).
- **Core:** encoder/parser and golden frames for §9; pin rows helper; Automatic port order; schema bump for #108.
- **Firmware native tests:** the pin watch's report rules and timeout; I2C scan result encoding; PinDrive refusals.
- **Simulator tests:** the three messages.
- **`SettingsCoverageTests`:** every controller setting stays editable and survives a save and reload.

Windows and hardware manual checklist: Detect on a real board (no floating pin lights up; ADC2 with Wi-Fi), Find screens on buses 0–3, Try the lights on unsaved pins, Automatic port with the CH340 and another serial device plugged in, the Wi-Fi flow and its fallback, the crossfade at 820 × 520 and 1100 × 720.

## 14. Delivery

Separate PRs, in this order, each green on CI:

1. **The shared stage.** Controls' drawing moves into the window's stage; Controls looks and behaves the same; Hardware still shows today's page beside it.
2. **The Hardware page on the stage:** card and ⋯ menu, tile captions, inspector with the pin dropdown (no Detect yet), bus chips, save bar, Controller settings, the chooser and Compare, test on the drawing, live input, the crossfade. Today's page goes. The Connection section moves into the dialog with today's fields (port list without Automatic).
3. **#108:** Controls › Response loses Smoothing; schema bump.
4. **Connection dialog proper:** Automatic port, port descriptions (Windows seam), the Wi-Fi flow.
5. **Firmware helpers:** PinWatch (Detect), I2cScan (Find screens), PinDrive (Try on unsaved pins); app wiring; simulator.
6. **Docs:** CLAUDE.md (Hardware page paragraph, wire table, firmware), `firmware/README.md`, controller-config §8.7 points here, README screenshots.

## 15. Left to the builder

Names, file layout, exact pixel values the mocks leave open, the pin-watch thresholds (§9.1, checked on hardware), Input feel's three pairs, wording not given above, and test structure.

# Controller page: Controls and Hardware in one page

Date: 2026-10-21. Status: design. Branch `unified-controller-page` (from `hardware-page-redesign` at `d61d4db`, which stays untouched as the alternative).

This is a change on top of `docs/design/hardware-page.md` (the two-page design, built on `hardware-page-redesign`) and `docs/design/controls-page.md`. Everything those specs say still holds unless this one says otherwise. "My call" marks a decision the user didn't make in the conversation.

## 1. Why

The usual journey is: set the controller up once, then now and then change what a slider or button controls or tune its response. Wiring a new control is rare. Two pages, one for each, make the common path and the rare path look equally important, and send people between pages for one control. One page, with the rare part tucked away, is simpler.

## 2. Decisions (user, 2026-10-21)

1. **One page, called Controller.** The sidebar has two entries: General and Controller. Controls and Hardware go.
2. **Clicking a slider or button behaves as on Controls,** and its inspector combines both pages' properties: a Controls section (open) and a Hardware section (collapsed), and so on.
3. **Controller status in three levels** (revised 2026-10-21, §5): a quiet sidebar status line when all is fine, a banner on every page when the controller needs the user, and a modal progress dialog while firmware or a save runs.
4. **+ Add and Test stay in the page's UI** for now.

## 3. Non-goals

- No change to what each setting does, how Controls settings save (at once), or how the controller config saves (a draft, Save to controller, restart).
- The later Hardware plans (Detect, Find screens, Try the lights, Automatic port, the Wi-Fi switch flow) are unaffected and plug into this page the same way.
- #108 (app smoothing vs the firmware's Input feel) is not decided here; the slider's Response section is today's.

## 4. The page

### 4.1 Drawing

- The drawing is today's Controls drawing (the stage in its Controls decoration): slider tiles with their target and live level, button tiles with their light or screen preview and actions, the mode tabs on the buttons frame. No mode switch: there is one decoration.
- Tiles don't show pins (my call: wiring is the rare case; it lives in the Hardware section).
- No I2C bus chips on the drawing (user, 2026-10-21): a bus's own settings live in Controller settings › Screens (§4.3).
- **A control with a wiring problem** (`DraftProblems`) gets the danger outline and a warning mark on its tile, with the problem as the tooltip ("Slider 1 also uses GPIO 33."); both ends of a conflict are marked (user, 2026-10-21).
- **A control added with + Add** draws as a new tile, dashed in the accent colour, labelled "New · Save to controller to set it up" (user, 2026-10-21: Volco configures a slider, button, LED or screen only once the controller has it).
- The drawing is the draft's controls when the controller draft has added or removed ones (as Hardware mode draws them), else the Controls items: the same `HardwareViewModel` tile sources, kept.
- The setup chooser (not set up, can't be used, Start over) and the hardware test still take over the case rectangle, as on the redesign branch (my call).

### 4.2 Title row

"Controller", then **+ Add** (Slider, Button), the **Controller settings** chip and **Test**, as on the redesign branch's Hardware title row (decision 4).

### 4.3 Inspector

The header is today's Controls header ("Slider N" with its name field; "Button N" with Clear). Below it, **two fixed sections, both always shown** (user, 2026-10-21, revised): the Controls/Actions section takes the top two thirds of the side panel and the Hardware section the bottom third, each with a fixed header and its own scrolling; Hardware never gets less than about four rows (176 px of rows plus its header), the top section gives way first. One line per row.

**Slider**
1. **Controls**: Controls row (target), Response (curve, dead zone, smoothing, reverse; today's collapsed sub-section).
2. **Hardware**: Pin; Auto-calibrate (a switch, a draft field saved with the controller config); Calibration (the learned ends or "Not calibrated", and Re-calibrate, which opens the sweep dialog; spec `slider-calibration.md`); Remove Slider N.

**Button**
1. **Actions**: the mode pill and the three gesture rows, Light or Screen row (follows the main action), Timing (collapsed sub-section).
2. **Hardware**: Button pin; Light (None / LED / 2 LEDs / Screen) and its rows; Remove Button N.

**Controller settings chip**: its redesign-branch inspector, plus, in Screens, one row per I2C bus in use ("I2C bus 0 · SDA 25 · SCL 26 · 400 kHz") that opens its SDA, SCL and Clock fields (the redesign branch's bus inspector). A screen button's Hardware section shows "Bus 0, address 3C" with a link to that row (user, 2026-10-21).

- A Hardware section with a problem (`DraftProblems`) shows the danger mark on its header and the problem on its field.
- Read-only rules (spec hardware-page §5.4) apply to the Hardware section only; the Controls section is always editable, as today.
- **A control that exists only in the unsaved draft** (added with + Add): its Controls section is replaced by "Save to controller to set it up" and a Save button (user, 2026-10-21: Volco configures a control only after the controller has it). The same holds for a button whose light type changed: until saved, its Light or Screen row says it will follow the new type after saving.
- **A control Volco has and the controller lacks** (the dashed "Not on this controller" tile): its Hardware section says "Not on this controller" and has no pin rows; Remove there is Controls' remove (Volco's settings only, last one only), as today (my call). It appears only when the controller changed outside Volco (Reinstall erased its setup, a different controller, config_tool or another Volco), since removing in Volco deletes the settings; Volco keeps them so the original controller or a re-setup restores what each control did.
- **Which light a button shows:** the tile preview and the Actions section's Light or Screen row show what the controller has (the Hello), as today; the Hardware section shows the draft, and a changed light is marked there until saved (my call: the preview is what the controller shows).

### 4.4 Saving

- Controls settings save at once, as today.
- Controller changes (Hardware sections, Add, Remove, Controller settings, the chooser) go into the controller draft. The save bar ("N changes not saved · Saving restarts the controller · Discard · Save to controller") appears under the drawing only while the draft is dirty.
- Removing a control and saving shifts Volco's settings as the redesign branch does (spec §6.5).
- **Volco's saved copy in the setup chooser is sent to the controller at once** (with the usual restart, in the progress dialog), since the controller has run it before; presets and Start empty open a draft (user, 2026-10-21). Picking it discards the draft first (which ends a Start over); a controller that already holds the copy gets nothing, so it doesn't restart for nothing. The card is disabled while it can't be sent: "Connect the controller first" without a ready controller, "Wait for the current operation to finish" while a save, a test or a firmware operation runs.
- Leaving the Controller page for General with a dirty draft asks first (the leave prompt), as Hardware did. Hiding the window doesn't.

## 5. Controller status: three levels (user, 2026-10-21, revised)

The device card in the sidebar went: it was easy to miss and cut off. The status (spec hardware-page §4.1's states, same wording and buttons) shows at one of three levels, by how much it needs the user:

- **All fine** (connected and in sync, firmware up to date or development; the expected waits): a quiet one-line status at the bottom of the sidebar, above the version: a dot and "Connected" (or "Connecting…"), fitting the sidebar without cutting off; the full line ("Connected over USB (COM3)", "Firmware up to date") is its tooltip. Narrow sidebar: the dot only. A click opens a small menu: Connection…, Reconnect, Disconnect, ― Check firmware, Reinstall firmware…, ― Load a preset…, Start over… (the old ⋯ menu). The tray reads the same status.
- **Needs you** (no controller answers, port busy, searching for a long time, disconnected by the user, firmware update available, firmware too old, not set up, the setup can't be used, a different setup, couldn't read the setup, saved setup doesn't work with this firmware, a failed operation's result): a **banner across the top of the content area on every page** (General and Controller), full width, with the state's dot colour, its status and detail on one line, and its one button (Connect, Retry, Check firmware, Update, Fix it, Compare…, Choose how to start). It stays until the state clears. The sidebar status line shows the same colour, and the Controller nav entry keeps its dot.
- **Running, don't interrupt** (install, update, check firmware; a save's sending and restart): a **modal progress dialog over the window**: the operation's name, its steps or a progress bar, "Don't unplug the controller.", and no way to close it while it writes (Escape and the scrim do nothing). At the end it shows the result and its next step ("Test your controller now", "Try again", "Close"); closing it leaves any remaining need to the banner.
- **Can't be used** (controller, 2026-10-22): two of the needs-you states, **No controller answers** and **Firmware too old for this Volco**, also open a modal **attention dialog** over the window, once each time the state begins: the state's status as its title, its detail, its button (Check firmware, Update: the banner's action) and **Not now**. Not now, Escape and the scrim close it and the banner stays; it doesn't open again while the state persists, only after the state clears (connected, unplugged, port busy, disconnected, another banner) and begins again. Volco's own waits (an operation, a reconnect, a check's reset) neither clear nor begin the state, and a state that begins under the progress dialog doesn't open it (the result told the user). When its button starts an operation, the progress dialog takes over and the attention dialog closes. It sits with the window's other dialogs, under the progress dialog. It isn't shown while the window is hidden; when the window opens and the state is current and wasn't dismissed, it shows.

Actions that belong to the drawing (Fix it, Test your controller now, Load a preset…, Start over…, Choose how to start) switch to the Controller page first, then act, and close a Controller page dialog open over the drawing. The Connection, Compare, Reinstall and leave-prompt dialogs stay window-level.

Firmware wording (user, 2026-10-21; refined in the round-2 review): Check firmware has three outcomes that offer an install, and only one of them is a fresh install:

- **No Volco firmware**: the board finds no matching build and the board on the port hasn't said Hello since the port opened (it never answered as Volco), or the app region is erased (its MD5 is that of all 0xFF, whatever said Hello before; bug 2026-10-22). "This ESP32 doesn't have Volco's firmware yet.", the button is **Install firmware**, and the confirmation asks "Install Volco's firmware on this ESP32? Anything else on it is replaced." with an **Install** button in the primary (not danger) style.
- **A different build of Volco's firmware**: no matching build, not erased, and the board said Hello (any protocol version) since the port last opened; Volco's own gaps (the check's reset) keep that Hello, and the port closing otherwise (unplugged, taken by esptool, Disconnect) forgets it. "This ESP32 runs a different build of Volco's firmware.", the button is **Reinstall firmware…**, and the confirmation is the reinstall's ("Reinstalling erases the setup stored on the controller. Volco keeps a copy you can apply afterwards.", **Reinstall**, danger style).
- **Volco's firmware that didn't answer** after the check's reset: "Volco's firmware is installed but didn't answer. Reinstall it?", with the same reinstall button and confirmation.

The rule: the fresh-install wording only when the board is erased or hasn't sent a Volco Hello since the port opened; anything else is a reinstall, which erases the stored setup. "Reinstall firmware…" stays in the status menu for a board that has Volco's firmware. After a successful install (the controller is then not set up), the progress dialog's next step is **Choose how to start**, which opens the setup chooser on the Controller page.

The sidebar status line, when the status needs the user, says why in a word or two ("Not connected", "Port busy", "Update available", "Not set up", "Firmware too old", "Different setup", "Disconnected", else "Needs attention"); the full line is its tooltip. While an operation runs it shows the running step.

## 6. What goes

- The Controls and Hardware nav entries, the stage's mode switch and its crossfades between modes (the crossfade to and from General stays), the Hardware tile captions (pins on tiles), the Hardware-only side panel layout (card over inspector).
- Tests about switching modes keep what still applies (the same tiles; the side panel after General).

## 7. Left to the builder

Names, file layout, the section header look, the banner's and progress dialog's look, and the sidebar status line's exact short words.

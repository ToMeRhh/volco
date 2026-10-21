# Controller page: Controls and Hardware in one page

Date: 2026-10-21. Status: design. Branch `unified-controller-page` (from `hardware-page-redesign` at `d61d4db`, which stays untouched as the alternative).

This is a change on top of `docs/design/hardware-page.md` (the two-page design, built on `hardware-page-redesign`) and `docs/design/controls-page.md`. Everything those specs say still holds unless this one says otherwise. "My call" marks a decision the user didn't make in the conversation.

## 1. Why

The usual journey is: set the controller up once, then now and then change what a slider or button controls or tune its response. Wiring a new control is rare. Two pages, one for each, make the common path and the rare path look equally important, and send people between pages for one control. One page, with the rare part tucked away, is simpler.

## 2. Decisions (user, 2026-10-21)

1. **One page, called Controller.** The sidebar has two entries: General and Controller. Controls and Hardware go.
2. **Clicking a slider or button behaves as on Controls,** and its inspector combines both pages' properties: a Controls section (open) and a Hardware section (collapsed), and so on.
3. **The device card moves into the sidebar.** Today's connection indicator at the bottom left of the sidebar grows into the card (connection status, firmware status), on every page.
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

The header is today's Controls header ("Slider N" with its name field; "Button N" with Clear). Below it, collapsible sections, one line per row:

**Slider**
1. **Controls** (open): Controls row (target), Response (curve, dead zone, smoothing, reverse; today's collapsed sub-section).
2. **Hardware** (collapsed): Pin; Remove Slider N. Summary when collapsed: "GPIO 33".

**Button**
1. **Actions** (open): the mode pill and the three gesture rows, Light or Screen row (follows the main action), Timing (collapsed sub-section).
2. **Hardware** (collapsed): Button pin; Light (None / LED / 2 LEDs / Screen) and its rows; Remove Button N. Summary: "GPIO 5 · LED 12".

**Controller settings chip**: its redesign-branch inspector, plus, in Screens, one row per I2C bus in use ("I2C bus 0 · SDA 25 · SCL 26 · 400 kHz") that opens its SDA, SCL and Clock fields (the redesign branch's bus inspector). A screen button's Hardware section shows "Bus 0, address 3C" with a link to that row (user, 2026-10-21).

- The open/closed state of each section is remembered for the app run, per kind (my call), so opening Hardware once keeps it open while rewiring several controls.
- A Hardware section with a problem (`DraftProblems`) opens itself and shows the problem on its field; its summary shows the danger mark while closed.
- Read-only rules (spec hardware-page §5.4) apply to the Hardware section only; the Controls section is always editable, as today.
- **A control that exists only in the unsaved draft** (added with + Add): its Controls section is replaced by "Save to controller to set it up" and a Save button (user, 2026-10-21: Volco configures a control only after the controller has it). The same holds for a button whose light type changed: until saved, its Light or Screen row says it will follow the new type after saving.
- **A control Volco has and the controller lacks** (the dashed "Not on this controller" tile): its Hardware section says "Not on this controller" and has no pin rows; Remove there is Controls' remove (Volco's settings only, last one only), as today (my call). It appears only when the controller changed outside Volco (Reinstall erased its setup, a different controller, config_tool or another Volco), since removing in Volco deletes the settings; Volco keeps them so the original controller or a re-setup restores what each control did.
- **Which light a button shows:** the tile preview and the Actions section's Light or Screen row show what the controller has (the Hello), as today; the Hardware section shows the draft, and a changed light is marked there until saved (my call: the preview is what the controller shows).

### 4.4 Saving

- Controls settings save at once, as today.
- Controller changes (Hardware sections, Add, Remove, Controller settings, the chooser) go into the controller draft. The save bar ("N changes not saved · Saving restarts the controller · Discard · Save to controller") appears under the drawing only while the draft is dirty.
- Removing a control and saving shifts Volco's settings as the redesign branch does (spec §6.5).
- Leaving the Controller page for General with a dirty draft asks first (the leave prompt), as Hardware did. Hiding the window doesn't.

## 5. The device card in the sidebar

- The sidebar's bottom connection indicator becomes the device card: "Volco controller", the status line with its dot, the detail line (firmware status or the state's detail), at most one button, and ⋯ (spec hardware-page §4.1, §4.2, unchanged in content).
- It sits above the version and links, on every page, so an update or a lost connection is visible from General too.
- Narrow window (the 56 px icon sidebar): the card shows only its dot; the full card shows when the sidebar opens over the page on hover (my call), and the sidebar stays open while the card's ⋯ menu is.
- **Actions that belong to the drawing switch to the Controller page first, then act:** Fix it, Test your controller now, ⋯ Load a preset…, ⋯ Start over…, and a not-set-up state's chooser. The detail line reads "Choose how to start." without "below" (my call). Confirmations they raise show on the Controller page, so the draft only changes there and the leave prompt still covers it; a Controller page dialog open over the drawing closes.
- The card's last firmware result ("Installed…", "Test your controller now", the install offer) stays when the Controller page is left, since the card is on every page; it goes when the window is hidden and with the next operation.
- The status line opens the Connection dialog, which becomes a window-level overlay (it no longer belongs to one page). Compare and the Install confirmation also open over the window.
- The tray keeps reading the same status; the nav dot moves to the Controller entry.

## 6. What goes

- The Controls and Hardware nav entries, the stage's mode switch and its crossfades between modes (the crossfade to and from General stays), the Hardware tile captions (pins on tiles), the Hardware-only side panel layout (card over inspector).
- Tests about switching modes keep what still applies (the same tiles; the side panel after General).

## 7. Left to the builder

Names, file layout, the section header look, the collapsed summaries' exact text beyond the examples, and how the card fits the sidebar's width.

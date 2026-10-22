# Slider calibration

Status: design, 2026-10-20; updated 2026-10-22 for the Controller page (`docs/design/controller-page.md`, merged in #117). Builds on the experiment in commit `d52f641` (branch `firmware-slider-ends`, based on an older `main`), which this work brings onto current `main` and exposes in the app.

## Why

Some boards keep each slider's wiper away from the ADC rails (a resistor at each end of the pot). Their stops read about 170 and 2550 instead of 0 and 4095, so the slider never reaches 0 or 100 % in the app. The experiment learns each slider's travel ends on the controller and stretches the reading onto 0-4095. It works, but it is invisible: there is no way to see what was learned, to start over, or to stop it from learning.

## Goals

- Each slider has an **Auto-calibrate** setting, on by default, stored in the controller config.
- Each slider has a **Re-calibrate** button in its inspector's Hardware section that runs a guided sweep: move the slider end to end, see the range it learns, click Done.
- The slider's Hardware section shows its calibration state.

## Non-goals

- No change to the slider's Controls section; Response (curve, dead zone, reverse) is unchanged.
- No calibration of anything but the two ends (no midpoint, no curve fitting).
- No backward compatibility with older firmware or configs (single user; app and firmware ship together).

## Anti-goals

- Moving a slider end to end during a sweep must not blast the volume to 100 %.
- A finished calibration must not change behind the user's back, except by widening (below).

## Behaviour

### The learned ends

As in the experiment: the controller keeps a low and a high end per slider, learned from the smoothed reading. Learning only ever **widens** the ends (an end moves out when the reading passes it, never back in). Until the span reaches 1000 codes the slider sends its raw reading; after that it sends the reading stretched between the ends, with 1.5 % of the span at each stop mapped to exactly 0 / 4095. The moment auto-calibration first learns a span, the stretched position goes out at once, whatever the deadband says. These constants and the mapping are the experiment's `SliderEnds`, unchanged.

### Auto-calibrate on (the default)

The controller learns all the time, as in the experiment. A Re-calibrate sweep forgets the ends first and learns from scratch; after Done, learning simply carries on. A manual calibration is not protected: auto-calibration may later widen it, if the slider reads past an end (user, 2026-10-20).

### Auto-calibrate off

The controller learns only during a sweep. Outside a sweep the ends are fixed. Turning the setting off forgets the ends, so the slider sends its raw reading until the user calibrates it with Re-calibrate (user, 2026-10-20).

### When the ends are forgotten

- The slider's pin changes (as in the experiment).
- The slider's Auto-calibrate setting changes, either way (turning it off must forget them, per the user; turning it on forgets them too, so one rule covers both; my call).
- A sweep starts (they come back if the sweep is cancelled).
- The slider is removed. Removing shifts the later sliders' numbers; each keeps its own ends because stored entries are looked up by pin, not by position (a change from the experiment, which matched by position; my call), and the removed slider's entry is dropped on the next store.

The experiment's "hold button 1 at boot" reset is removed; Re-calibrate replaces it.

### The sweep

1. The user clicks **Re-calibrate** in the slider's Hardware section. A modal dialog opens over the window, like Connection and Compare: "Calibrate Slider 3", "Move the slider all the way to both ends, then click Done", a bar of 0-4095 showing the learned range with its two numbers, and Done / Cancel. It is interactive, so it is not the progress dialog (which is for work the user can't interrupt).
2. The app tells the controller to start a sweep for that slider. The controller remembers the current ends (for Cancel), forgets them, and learns from every smoothed reading, whatever Auto-calibrate says. The controller reports the ends each time they widen (below), so the bar grows live.
3. While the dialog is open, that slider's input does not reach audio (anti-goal above).
4. **Done** is enabled only once the reported span is learned (1000 codes); until then the dialog says "Keep moving: the travel is too short so far". Done ends the sweep and stores the ends; the dialog closes on the ending report (or after 2 s without one, cancelling a sweep the controller didn't end) and the Hardware section says "Calibrated: 170-2550". So a finished sweep is always a learned one (my call: a short Done would otherwise throw away a good calibration). No restart: the ends are not part of the controller config.
5. **Cancel**, Escape, a click on the scrim, hiding the window: the controller ends the sweep and restores the ends it had before it started. (The dialog is window-level, so the sidebar and the other page can't be reached while it is open.)
6. The controller ends a sweep by itself, as a Cancel, when the host is lost or 60 s pass without Done (the app is gone). The dialog closes and the status banner says why if the link dropped. A report that says a slider is sweeping while the app holds no sweep for it (the app restarted mid-sweep) gets a Cancel at once.
7. When a sweep ends (any way), the controller sends the slider's Calibration report and then a fresh Slider frame with its position stretched by the final ends, whatever the deadband says. The app releases the input hold when that report arrives, so the fresh position reaches audio (my call: the app's last position is from before the sweep and stretched by the old ends, so `ReapplySlider` would set a stale value).

**Re-calibrate is enabled only when** the controller is connected (Ready), that slider has no unsaved Hardware changes (its pin and Auto-calibrate as saved are what the controller runs; the button's tooltip says "Save to controller first"), the controller runs Volco's saved setup (else the tooltip says "Volco's setup differs from the controller's"), it is not a new slider, and no hardware test, save or firmware operation is running.

### What the Hardware section shows

The slider's Hardware section (bottom third of the inspector) gets two rows between Pin and Remove, each one line:

- **Auto-calibrate**: a toggle; a draft field, sent with Save like the pin. Changing it marks the Hardware section changed and, until saved, the row's caption says "Saving forgets this slider's calibration" (on → off and off → on alike).
- **Calibration**: the state the controller reports, and the **Re-calibrate** button on the right:
  - "Calibrated: 170-2550" (learned).
  - "Not calibrated yet: move the slider end to end" (auto on, not learned).
  - "Not calibrated: uses the raw reading" (auto off, not learned).
  - "—" while not connected or not yet reported (each Hello forgets the last reports until the answers).

The caption is one line, trimmed with an ellipsis; its tooltip has the full text.

Four rows (Pin, Auto-calibrate, Calibration, Remove) fit the Hardware section's minimum height. The tile is unchanged.

## Protocol and config

### Config (proto)

`Slider` gains `optional bool auto_calibrate = 2 [default = true];`. Regenerate nanopb, the C# codec, the golden config vectors and the simulator's descriptor; the presets need no change (absent = on). `ConfigDiff` (Compare) lists it per slider when it differs.

### Wire

Two new messages; `kProtocolVersion` stays 1 (app and firmware ship together; the build-id check already prompts for a firmware update). `0x8B`–`0x8D` and `0x07`–`0x08` are reserved by the Hardware page's firmware helpers (hardware-page §9), so calibration takes the next free numbers:

| Type | Name | Direction | Body |
|---|---|---|---|
| `0x8E` | Calibrate | PC→controller | slider index, action (0 query, 1 start sweep, 2 done, 3 cancel) |
| `0x09` | Calibration | controller→PC | slider index, flags (bit 0 learned, bit 1 sweeping, bit 2 auto-calibrate), low end (u16), high end (u16) |

The controller answers every Calibrate with a Calibration for that slider. It also sends one whenever a slider's ends widen, in a sweep or by auto-calibration (at most every 100 ms per slider), and when it ends a sweep by itself, so the Hardware section and the sweep's bar stay live (my call). An index past the slider count is ignored. The app forgets every slider's last report and queries every slider after each Hello (the Controller page is the main page, so there is no "page open" condition).

### Firmware

- Bring over `lib/volco_slider_ends` (pure, with its native tests) and `src/slider_ends_store.*` from `d52f641`, onto current `main` (re-apply its `main.cpp` hunks by hand; the file has moved on).
- Add a pure piece in `lib/` (name left to the builder) that owns one slider's calibration: the `SliderEnds`, the Auto-calibrate setting, the sweep (start, done, cancel with the saved ends, the 60 s timeout, the host-lost cancel) and when a Calibration report is due. `main.cpp` and `Link` only feed it readings and frames. Native tests cover each rule in this document.
- The NVS entry per slider becomes {pin, auto-calibrate, low, high}, all in one blob under a new key, `ends2`, in namespace `volco_ends` (the experiment's 5-byte entries under `ends` are removed at load, never read), looked up by pin (not by position) when the config loads; an entry whose auto-calibrate differs from the slider's setting, or whose pin no slider uses, is ignored (that is the "forgotten" rule). Stored at most every 5 s while learning, and once at the end of a sweep.
- Remove the button-1-at-boot clear.
- Golden frames for both messages in `firmware/test/golden/frames.txt`, checked by the firmware, the C# and the simulator tests.

### App (Core)

- `ProtocolEncoder.Calibrate(index, action)`; `ProtocolParser` decodes Calibration into a new `ControllerMessage`.
- `VolcoRuntime`: a way to send a Calibrate, an event for Calibration reports, the last report per slider, and a per-slider input hold (a handle, like `EnterTestMode`) that keeps the slider away from audio until the sweep's ending report arrives (or the link drops). The runtime cancels a sweep it holds on connection replacement and dispose.

### App (UI)

- `SliderWiringViewModel` (the slider's Hardware section): `AutoCalibrate` (draft, through `DraftPins`-style access to the draft's Slider), the calibration caption from the runtime's last report, `RecalibrateCommand` with the enable rule above.
- A calibration dialog view model and view, hosted with the window-level overlays in `SettingsWindow` (Escape and scrim = Cancel; focus moves in and back like the others).
- The save bar counts an Auto-calibrate change as a change of that slider.
- Problems: none (a bool has no invalid value).

### Simulator

The simulator speaks the protocol, so it answers Calibrate: its sliders are full range, so a sweep reports the range the user drags through, learned once it spans 1000 codes. It keeps the Auto-calibrate setting from the config like any field. Node tests for the new frames.

## Docs to update

CLAUDE.md (the wire table, the Controller page paragraph, the firmware paragraph), `firmware/README.md` (code structure, the protocol), `docs/design/controller-page.md` §4.3 (the two new Hardware rows), `docs/design/hardware-page.md` §9.4 (calibration takes `0x8E`/`0x09`), `docs/design/controller-config.md` (the Slider field and the messages; its "learn mode" note points here).

## Left to the builder

Names, file layout, the exact dialog layout and bar drawing, wording of messages other than those above, test structure, and the order of the work.

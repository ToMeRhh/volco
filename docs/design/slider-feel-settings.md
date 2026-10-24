# Slider feel and button timing settings

Status: design, 2026-10-24. Changes the Controller page (`controller-page.md` §4.3), Controller settings (`hardware-page.md` §6.5, §6.6) and the controls spec's inspector rows (`controls-page.md`). Resolves #109.

## Why

After #108 (one smoothing, the firmware's), the settings that change how a slider feels sit in three places: the slider's Controls › **Response** (curve, dead zone, reverse), its **Hardware** section (Auto-calibrate, Re-calibrate) and Controller settings › **Input feel** (smoothing, and under Fine tune debounce, read interval, smoothing weight, ignore changes below). They are grouped by where the code runs (app or firmware), not by what the user notices. Button timing (hold after, double press within) has the same problem: it describes how the person presses buttons, but is set per button under that button's Actions (#109).

The user picks a setting by its symptom, on the thing they are looking at. After this change each symptom has one fix in one place:

| Symptom | Fix | Where |
|---|---|---|
| The slider doesn't reach 0 or 100 % | Calibration | the slider's Hardware section (unchanged) |
| The slider goes the wrong way | Reverse direction | the slider's Hardware section |
| The volume jitters | Smoothing | Controller settings › Sliders |
| A hold is too slow, a double press too hard | Hold after, Double press within | Controller settings › Buttons |
| One press counts as two | Debounce | Controller settings › Buttons › Fine tune |

## Goals

- A slider's inspector has no Response section. Its Controls card is just what it controls.
- **Reverse direction** moves to the slider's Hardware section and is stored in the controller config.
- **Curve and Dead zone are removed** (the app, the config and the code behind them).
- A button's inspector has no Timing section. **Hold after** and **Double press within** are one controller-wide setting, in Controller settings › Buttons (#109).
- Controller settings' **Input feel** splits into **Sliders** and **Buttons**. **Read sliders every** leaves the UI.
- **Every setting in a draft section saves with Save.** The save bar's button is always **Save**.

## Non-goals

- No per-button timing override (e.g. a longer hold for a destructive action). If one is ever needed, it belongs on the action, not back in a Timing section.
- No per-slider smoothing. ADC noise is a property of the board; one value is the right model.
- No change to calibration's behaviour (`slider-calibration.md`).
- No change to which sections are drafts and which apply at once, beyond the fields this spec moves.
- No backward compatibility (single user; app and firmware ship together).

## Anti-goals

- A Controller settings card where some rows wait for Save and others apply at once.
- A setting about one slider that moves with its target on Swap, or a setting about the hardware that stays in "Controls".

## The save rule

A setting follows the section it sits in:

- **Applies at once:** a slider's Controls section, a button's Actions, the target, action and device dialogs, the General page. Unchanged.
- **Draft, committed by Save:** each tile's Hardware section and Controller settings. Unchanged, except that Hold after and Double press within, which Volco stores itself (`config.json`), now sit in Controller settings and so **join the draft**:
  - They are editable exactly when the rest of Controller settings is (`CanEdit`: the controller connected and in sync, no save or test running), and Save is available under the same rule (user, 2026-10-24). Changing button timing with no controller is no longer possible; without a controller there are no buttons to press.
  - Editing them makes the draft dirty, shows the save bar and counts as a change; Discard reverts them; the leave prompt asks about them like any other unsaved change.
  - **Save** with only these changed writes them to Volco's settings at once. Nothing is sent to the controller, the controller doesn't restart, no progress dialog opens, and the save bar's note "Saving restarts the controller" is not shown.
  - **Save** with controller changes too sends the controller's config as today and commits these values with it, when the controller confirms (as the layout is committed today, §6.5). A failed save leaves them in the draft, unsaved.
- The save bar's button reads **Save** in every case (user, 2026-10-24). The new-control prompts ("Save to controller to set it up" and its button) become "Save to set it up" and **Save**, for the same reason (my call).

Reverse direction is a controller config field (below), so it is an ordinary draft field and needs nothing new.

## The slider

### Inspector

- **Controls:** one card, the Controls row (the target). Response is gone.
- **Hardware:** pin, **Reverse direction** (a toggle; "?" tooltip: "Turn this on if turning the volume up moves this slider the wrong way, for example a slider mounted upside down."), Auto-calibrate, the calibration state and Re-calibrate, Remove. Reverse sits after the pin, before calibration (my call).
- Under calibration, one plain row **Smoothing (all sliders)** with the current value ("Normal") as a link that opens Controller settings with the Sliders card open (my call). It is read-only here: the setting is controller-wide and stays in one place, but the user who is looking at a jittery slider finds it.

### Reverse direction on the controller

- `proto/volco_config.proto`: `Slider` gains `optional bool reverse = 3 [default = false];`.
- The firmware applies it to the value it sends, last: `4095 − v`, after smoothing, calibration's stretch and the deadband check. A Slider frame, a test-mode reading and the tile's live position are all reversed; the Calibration report's ends and the pin watch stay raw.
- The firmware's every send path applies it: the main loop, the slider values sent after a Hello, and the value sent at the end of a sweep.
- The Re-calibrate dialog mirrors its range bar for a reversed slider, so it grows towards the end the user pushes (my call; the reported ends stay raw on the wire).
- The app no longer inverts (`SliderConfig.Invert` and `MapAdcToVolume`'s `invert` go).
- Swap leaves it with the physical slider, as every Hardware field.
- The simulator honours it (its sent slider values), and the generated artifacts follow the proto change: nanopb code, the C# codec, `firmware/test/golden/config.txt` (`gen_config_vectors.py`) and `tools/simulator/volco_config_descriptor.js` (`gen_descriptor.mjs`).

### Removed

- **Curve** (`ResponseCurve`, `SliderConfig.Curve`): Windows' volume scalar is already audio-tapered, the same 0–100 as the Windows mixer, so Linear matches what the user expects. The old "Log" was √x, which boosts the low end, the opposite of an audio log taper.
- **Dead zone** (`SliderConfig.DeadZonePercent`): it worked only at the bottom of the travel, a workaround for a slider that never reaches 0, which calibration now fixes. With both, a calibrated slider lost the bottom of its travel twice.

## The button

- **Actions:** the mode pill; one card with the three gesture rows; the Light or Screen row. Timing is gone.
- The press detector uses the controller-wide values. The rule that a button with no double-press action in the live mode doesn't wait (window 0) is kept.

## Controller settings

Header "Controller". Four sections, each a card, each opened on its own:

- **Screens:** unchanged.
- **Sliders** (summary "Normal"): **Smoothing** Low / Normal / High (Custom shown when the raw values match no pair), today's three pairs and tooltip. **Fine tune:** Smoothing weight, Ignore changes below (today's fields and tooltips).
- **Buttons** (summary "500 ms · 300 ms"):
  - **Hold after** (ms, 200–2000, step 50; "?": "How long you hold a button before it counts as a hold, not a press.").
  - **Double press within** (ms, 100–1000, step 50; "?": "How soon a second press must follow the first to count as a double press. A button with no double-press action doesn't wait for one, so its press runs at once.").
  - **Fine tune:** Debounce (today's field and tooltip).
- **Wi-Fi:** unchanged.

**Read sliders every** (`InputTuning.slider_interval_ms`) leaves the UI. The field stays in the proto; a value set elsewhere is kept in the draft and saved unchanged. It is a developer's tuning parameter that also changes how strong the smoothing is.

The save bar counts a changed card once: Sliders if weight or deadband changed, Buttons if debounce, hold or double press changed.

## Volco's settings (`config.json`)

Schema v8:

- `buttonTiming: { longPressMs: 500, doublePressWindowMs: 300 }` at the top level (`ButtonTimingConfig`, part of `AppConfig`, so no own `[JsonSerializable]`).
- Gone: `buttons[i].longPressMs`, `buttons[i].doublePressWindowMs`, `sliders[i].invert`, `sliders[i].curve`, `sliders[i].deadZonePercent`.
- Migration from v7: `buttonTiming` takes the first button's values, else the defaults. `invert` is not carried over: it now lives on the controller, so a reversed slider has to be set again once (no backward compatibility). Curve and dead zone are dropped. Older versions pass through the existing chain first. No `.bak` beyond what the chain already writes.

## Docs

- `controller-page.md` §4.3: the inspector rows above; the Hardware section's fixed height no longer opens Response or Timing (they are gone), so the probe measures each inspector as it is (`StagePage.InspectorProbe.cs`).
- `hardware-page.md` §6.5 (Save, the save bar) and §6.6 (Controller settings).
- `controls-page.md`: the Response and Timing rows, marked as replaced by this spec.
- `slider-calibration.md`: its non-goal about Response is out of date; point here.
- `CLAUDE.md`: config schema v8; the slider `Slider` proto field; Controller page and save rule wording where it mentions them.
- `firmware/README.md`: reverse in the slider pipeline.

## Tests

- Core: migration v7 → v8 (timing from the first button, defaults with none, the dropped fields); the press detector with the controller-wide values and the no-double-press rule; `MapAdcToVolume` without curve, dead zone or invert.
- Firmware native: reverse applied last; Calibration ends and pin watch raw; config vectors regenerated.
- Simulator: a reversed slider sends `4095 − v`.
- App: the save rule (timing-only Save writes `config.json`, sends nothing, shows no restart note; mixed Save commits timing on confirmation and not on failure; Discard reverts timing; the leave prompt counts it; the change count per card); the button reads Save; Swap leaves Reverse; the inspectors have no Response or Timing; Read sliders every is absent and its value survives a save.

## Left to the builder

Names, file layout, wording not given above, how the Smoothing link opens and scrolls Controller settings, the progress/status text after a timing-only Save (none, or a short "Saved."), and how the draft holds the two timing values (a small app-side part of `ControllerViewModel`'s draft is expected).

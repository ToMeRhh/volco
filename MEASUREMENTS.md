# Measurements — Option D (Core Audio engine) vs `main`

This file is the measurement protocol from `docs/superpowers/specs/2026-09-26-volco-audio-core-design.md`
§8, laid out as a table for the repository owner to fill in. It requires the app running on
the **Windows machine with the controller attached** — none of it can be done in CI or on this
Linux dev box.

The old audio layer (`main`) re-activated COM objects up to 50 times per second per session,
which showed up as elevated idle CPU and a handle count that grew without bound. The Core Audio
engine on `option-d-audio-engine` (this branch) replaces that with a cached, event-driven
registry. **A `main` baseline is required** — without it, any number recorded for this branch is
just a number; the whole point of this table is the *comparison*, and "flat vs. growing" only
means something next to "growing."

Do not fill in any result cell now. Every result cell below is intentionally blank.

## How to get a build

- **This branch (`option-d-audio-engine`):** download the `Volco-win-x64` artifact from the
  `build.yml` CI run on this branch. Most recent successful run: **36345436865**.
- **`main` baseline:** `main` must be built separately (its own `build.yml` run, or a local
  Windows build) — there is no shortcut; the whole comparison depends on measuring an actual
  `main` binary, not inferring its behavior.

Run both builds on the same machine, same controller, same conditions, one at a time.

## Reference targets

| Metric | Target |
|---|---|
| Idle CPU | under 0.1% average |
| Idle GPU | 0 |
| Handle count over the sweep | **flat** (not growing) |
| Cold start | under 1 s |

The handle count is the single most diagnostic number in this table: on `main` it is expected
to **grow** over the sweep — that growth *is* the original bug (COM objects re-activated on
every slider tick, never released). A flat handle count on this branch is the direct evidence
the fix worked.

## 1. Idle

App running, controller connected, settings window closed, no audio playing. Sample for 60 s:

```powershell
Get-Counter '\Process(Volco)\% Processor Time' -SampleInterval 1 -MaxSamples 60 |
  ForEach-Object { $_.CounterSamples.CookedValue } |
  Measure-Object -Average -Maximum
```

Also record the **GPU** column for Volco in Task Manager's Details tab over the same 60 s window.

| | `main` (baseline) | `option-d-audio-engine` |
|---|---|---|
| CPU average (%) | | |
| CPU max (%) | | |
| GPU | | |

## 2. Sustained sweep

Move all 5 sliders continuously for 60 s, same counters as step 1:

```powershell
Get-Counter '\Process(Volco)\% Processor Time' -SampleInterval 1 -MaxSamples 60 |
  ForEach-Object { $_.CounterSamples.CookedValue } |
  Measure-Object -Average -Maximum
```

| | `main` (baseline) | `option-d-audio-engine` |
|---|---|---|
| CPU average (%) | | |
| CPU max (%) | | |

## 3. Handle / leak check

Before and after the sweep in step 2:

```powershell
Get-Process Volco | Select-Object HandleCount, NPM, PM, WS
```

Handle count must be **flat**, not growing. (On current `main` it will grow — see "Reference
targets" above.)

| | `main` (baseline) | `option-d-audio-engine` |
|---|---|---|
| HandleCount before sweep | | |
| HandleCount after sweep | | |
| Handle count flat? (yes/no) | | |
| NPM before / after | | |
| PM before / after | | |
| WS before / after | | |

## 4. Cold start

Process launch to tray icon visible. Use the `AppLog` timestamp at "App launch complete" plus a
stopwatch for the icon appearing.

| | `main` (baseline) | `option-d-audio-engine` |
|---|---|---|
| Launch → "App launch complete" (AppLog) | | |
| Launch → tray icon visible (stopwatch) | | |

## 5. Working set at idle

From step 1's `WS` (capture alongside step 1, no separate run needed):

| | `main` (baseline) | `option-d-audio-engine` |
|---|---|---|
| Working Set (WS) at idle | | |

## 6. OSD latency

Subjective, but recorded. Does the pill appear within one frame of a slider move, and does it
ever flicker or steal focus?

| | `main` (baseline) | `option-d-audio-engine` |
|---|---|---|
| Pill appears within one frame? (yes/no) | | |
| Flicker observed? (yes/no) | | |
| Steals focus? (yes/no) | | |
| Notes | | |

---

## Manual checklist

Windows-only, manual — run this checklist per build (`main` and `option-d-audio-engine`). Base
list from spec §7, plus three added from this branch's own reviews (Task 12 and Task 13) that
are **not** in the spec.

### From spec §7

- [ ] Unplug the active USB headset mid-slider-sweep → no exception; mapping recovers when
      re-plugged.
- [ ] Rename a device in Sound Settings → mapping survives (the ID-vs-name regression test).
- [ ] Start Spotify while its slider sits at 30% → it starts at 30%.
- [ ] Play audio to the non-default device → its slider still controls it.
- [ ] Switch default device via a button → all three roles change (verify in Sound
      Settings → Advanced).

### Added from this branch's reviews (not in the spec)

- [ ] **Pre-existing sessions.** Start an app playing audio *first*, then start Volco, then move
      that app's slider, and confirm it responds. A Critical bug found in Task 12 meant sessions
      that already existed before Volco started were silently dropped — the spec's checklist
      above only covers apps started *after* Volco (the "Start Spotify while its slider sits at
      30%" case), so it would not have caught this.
- [ ] **`IPolicyConfig` vtable ordering.** Switch the output device and confirm it changes for
      all three roles (Console/Multimedia/Communications, visible in Sound Settings → Advanced)
      **without** any spurious share-mode or sample-format change alongside it.
      `SetDefaultEndpoint`'s vtable slot was derived from memory by two independent agents who
      agreed with each other, but neither verified it against a live header — a wrong slot
      compiles cleanly and would silently land on `SetShareMode`/`SetDeviceFormat` instead of
      `SetDefaultEndpoint`.
- [ ] **`IMMDevice.Activate(..., null, ...)` crash.** A CsWin32 issue reports a possible
      `NullReferenceException` from this exact call shape on some Windows versions. It would
      surface as a crash at startup. If it happens, the fix is passing `new PROPVARIANT()`
      instead of `null` in `DeviceRegistry.BuildHandle`'s two `Activate` calls.

---

# Step 2 — Avalonia exe vs `main` (WinUI)

Same protocol as above (spec §8), now for the Avalonia cutover build. Needs the Windows
machine with the controller attached. **Do not fill any result cell until you have run it.**

**Build under test:** `Volco-win-x64` from Windows run `36849004610` on `step2-avalonia`
(expires about 2026-10-08; re-dispatch with
`gh workflow run build.yml --ref step2-avalonia --field run_build=true` if gone, and use the
newest `run_build` artifact if the branch has moved). **Baseline:** a `main` (WinUI) build, run
on the same machine, same controller, one at a time.

## Procedure

1. Launch the build, controller connected, settings window closed, no audio playing.
2. **Idle (60 s):** run the `Get-Counter` command from section 1 above; record CPU average and
   max. Record Volco's GPU column in Task Manager's Details tab over the same window. Capture
   `WS` from `Get-Process Volco` at the same time.
3. **Handle sweep:** run `Get-Process Volco | Select-Object HandleCount, NPM, PM, WS` before,
   move all 5 sliders continuously for 60 s (sample CPU as in section 2), then run it again.
   Handle count should be flat.
4. **Cold start:** quit, relaunch, stopwatch from launch to tray icon visible; also note the
   AppLog "App launch complete" timestamp.
5. **Exe size:** `(Get-Item Volco.exe).Length`.
6. Repeat 1-5 for `main`.

## Results

| Metric | Target | `main` (WinUI) | `step2-avalonia` |
|---|---|---|---|
| Idle CPU average (%) | < 0.1 | | |
| Idle CPU max (%) | | | |
| Idle GPU | 0 | | |
| Sweep CPU average / max (%) | | | |
| HandleCount before / after 60 s sweep | flat | | |
| Handle count flat? (yes/no) | yes | | |
| Cold start to tray icon (s) | < 1 | | |
| AppLog "App launch complete" (s) | | | |
| Working set at idle (WS) | | | |
| Exe size (bytes) | | 66,568,009 (~63.5 MiB) | 53,005,077 (~50.6 MiB) |

The `main` WinUI exe size is from Step 1's Windows build artifact. OSD latency (section 6)
should also be re-checked on this build, including the Step 2 manual checklist's OSD items.

# Volco Option D — WinUI 3 Remediation In Place

**Date:** 2026-09-26
**Status:** Approved (design only — not implemented)
**Depends on:** `2026-09-26-volco-audio-core-design.md` (the shared audio redesign —
read it first; it is not repeated here)
**Sibling option:** `2026-09-26-volco-option-a-avalonia-design.md`

> Option A and Option D are to be built and measured **independently, on separate
> branches**, then compared using the measurement protocol in the shared spec §8.
> Neither is a prerequisite for the other.

---

## 1. Goal and rationale

Keep WinUI 3, the Windows App SDK, and the existing project layout. Fix the causes
of the CPU/GPU/reliability complaints without changing the UI framework:

1. Implement the shared audio redesign, replacing `AudioManager` + `SessionMonitor`.
2. Replace the XAML-based OSD with a native Win32 overlay.
3. Remove XAML from the tray-icon path.
4. Migrate config to device IDs; delete `svcl.exe`.

**Option D is the control experiment, and that is its real value.** The
expectation is that it clears roughly 80 % of the symptoms for roughly 20 % of the
work. If it does, then Option A stops being a *fix* and becomes a clean-headed
choice about portability and shell weight — which is a much better decision to
make with data than with a hunch. If it does not, that is an equally strong signal.

Build this one first if you want the answer sooner.

### 1.1 What Option D explicitly does *not* solve

State these plainly when comparing results, so the comparison is honest:

- **WinUI 3's documented per-window idle cost remains.** Microsoft's own trackers
  record WinUI 3 desktop windows idling at 0.8–2 % CPU with no user code running
  (`microsoft-ui-xaml#4565`), mouse-move CPU spikes up to 20 % over empty areas
  (`#6914`), and 2–6× higher input CPU than WinUI 2 (`#6750`). Removing the OSD
  window (§4) removes one of the two windows, which should help materially — but
  the framework is still resident, and the settings window still carries this cost
  whenever it is open.
- **The Windows App SDK payload remains.** `WindowsAppSDKSelfContained=true` plus
  `PublishSingleFile` is a large binary with a correspondingly slow cold start.
  §6 offers one lever; it is a trade, not a fix.
- **No portability.** This branch forecloses Linux/macOS as thoroughly as today's
  code does.
- **The build still needs MSBuild / VS Build Tools**, so it still cannot be built
  on the Linux dev machine — the CI-only workflow in `CLAUDE.md` stays as-is.

---

## 2. Scope: what changes and what does not

### 2.1 Unchanged

`src/Volco/` stays a single project with its `<Compile Remove>` platform
conditionals. `Communication/`, `Updates/`, `UI/` (the five settings pages and
their ViewModels), `AppLog`, `ConfigManager`'s save/debounce machinery, the WinUI
`SettingsWindow` and `UpdateWindow`, the `.csproj` shape, both CI workflows, and
`app.manifest` (other than §4.4's DPI addition) all stay.

The wire protocol is **frozen**: `0xAA | length | payload | XOR`, message types
`0x01` slider, `0x02` button, `0x03` mute state, `0x04` active device. The ESP32
firmware and `tools/simulator/index.html` both depend on it.

### 2.2 Changed

| Component | Change |
|---|---|
| `Audio/AudioManager.cs`, `Audio/SessionMonitor.cs`, `Audio/IAudioManager.cs` | **Deleted.** Replaced by `Audio/IAudioEngine.cs` + `Audio/Windows/AudioEngine.cs` per the shared spec. `IAudioEngine` lives on the platform-neutral side of the `<Compile Remove>` boundary; the implementation lives under `Audio/Windows/` and is excluded on Linux, exactly as `AudioManager.cs` is today. |
| `Input/SliderProcessor.cs` | Retargeted to `IAudioEngine`; `SessionMonitor` dependency removed; `Unmapped` becomes `SessionFilter.Unmapped`. Keep the 20 ms coalescing — it is correct and cheap; the timer was never the problem. |
| `Input/Actions/*` | Retargeted to `IAudioEngine`; `MuteSpecificDeviceAction` / `SetOutputDeviceAction` take a `DeviceRefConfig` instead of a name string. |
| `Config/AppConfig.cs` | Schema v2 (shared spec §4). |
| `Config/ConfigMigrator.cs` | **New** (shared spec §4). |
| `UI/VolumeOsdWindow.xaml{,.cs}` | **Deleted.** Replaced per §4. |
| `Osd/` | **New** — `IVolumeOsd` (neutral) + `Osd/Windows/NativeVolumeOsd.cs` (excluded on Linux). |
| `App.xaml.cs` | Tray-icon path de-XAML'd (§5); OSD wiring repointed at `IVolumeOsd`; `PushControllerState` / `GetMuteStateForButton` fixed (§3). |
| `Tools/svcl.exe` | **Deleted**, along with its `<Content Include>` item. |
| `Volco.csproj` | Drop `NAudio`. Possibly drop `H.NotifyIcon.WinUI` (§5). Add CsWin32. |
| `UI/ViewModels/*` | Minimal edits only: `IAudioManager` → `IAudioEngine`, and device combo boxes bind to `AudioDevice` (ID + name) rather than bare name strings. |

---

## 3. The `PushControllerState` correctness fix

Independent of the audio rewrite, but it is in the blast radius and it is a real
bug, so fix it here.

`App.xaml.cs:PushControllerState` currently does:

```csharp
int activeIndex = currentDevice.Contains(devices.Device1, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
```

Any device that is not device 1 — including a third device that is in neither slot
— reports as device 2 and lights the wrong LED on the controller. With schema v2
this becomes an endpoint-ID comparison against both configured devices, with a
third state for "neither," and the encoder call is skipped in that case rather than
lying to the controller.

Extract this and `GetMuteStateForButton` into a `ControllerStateReporter` class so
it is unit-testable on Linux CI against `FakeAudioEngine`. Today it is untestable
private logic inside a Windows-only file.

While there: `App.xaml.cs` is ~350 lines mixing DI wiring, tray setup, message
routing, OSD triggering, and controller-state pushing. Splitting out
`ControllerStateReporter` and a `ControllerRouter` (message → processor/detector)
is a targeted improvement that serves this work; a broader restructure of
`App.xaml.cs` is not in scope.

---

## 4. The OSD: a native Win32 overlay

**This component is identical to Option A §5 and is deliberately shared between
the two branches.** Whichever option is built first should produce it; the other
takes it as-is. The full implementation spec — window styles, the
`SetLayeredWindowAttributes` fade path, the Direct2D drawing model, positioning
and DPI, threading — is in **Option A §5.2–5.4** and is not repeated here.

Summary of why it replaces `VolumeOsdWindow`:

- Today's OSD is a **second full WinUI 3 `Window`**, kept alive for the process
  lifetime and shown/hidden on nearly every slider move. It carries the
  per-window idle cost cited in §1.1 and a DWM composition target of its own.
- Its hide path runs a `DoubleAnimation` with `EnableDependentAnimation = true`,
  which is a CPU-side animation by definition.
- A plain Win32 HWND kept alive hidden costs essentially nothing idle.

Two pieces of the current implementation are **correct and must be preserved**:

1. Repositioning only on the hidden→visible transition (`if (!_isVisible)` in
   `ShowCore`), which is what stops the window flashing on every tick.
2. `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW` and
   `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`.

Behavioural parity is governed by
`docs/superpowers/specs/2026-06-07-volume-osd-design.md`, which remains the
authority on *what* the OSD shows. `VolumeChangeNotification`,
`SliderProcessor.BuildSliderLabel` and its tests survive unchanged.

### 4.1 Manifest

`app.manifest` needs **per-monitor DPI awareness v2** added. The OSD needs it and
the current manifest lacks it.

---

## 5. De-XAML the tray path

Today `App.SetupTrayIcon` builds an `H.NotifyIcon.TaskbarIcon` with a
`GeneratedIconSource` — which renders the tray icon **through XAML** (a
`SolidColorBrush`, a letter "V") — plus a XAML `MenuFlyout`.

Two options, in order of preference:

1. **Replace `H.NotifyIcon.WinUI` with direct `Shell_NotifyIcon` P/Invoke** plus a
   static `.ico` resource and a Win32 `TrackPopupMenu` context menu. The overlay
   project already owns a message-only window that can host the notify-icon
   callback message. This takes XAML entirely off the always-resident path and
   removes a dependency.
2. **Keep `H.NotifyIcon` but replace `GeneratedIconSource` with a static `.ico`.**
   Much smaller change; removes the XAML-rendered icon but keeps the XAML flyout.

Option 1 is the one that actually matters for idle cost, because it is what makes
"no settings window open" mean "no XAML visual tree alive." Option 2 is the
fallback if `TrackPopupMenu` interop proves fiddly.

Either way, add `Assets/volco.ico` — the app currently has no icon file at all.

---

## 6. Optional: trimming the Windows App SDK payload

`Volco.csproj` sets `WindowsAppSDKSelfContained=true` with
`WindowsPackageType=None`, and CI publishes self-contained single-file. That is a
large binary and a slow cold start.

The lever is `WindowsAppSDKSelfContained=false`, which requires the Windows App
SDK runtime to be installed on the target machine. For a single-user tray app
installed by copying one `Volco.exe`, that is a meaningful regression in
"it just works" — and the in-app updater's whole model is "swap one exe."

**Recommendation: leave it alone.** Record the cold-start number as-is and let it
be one of the honest costs of Option D in the comparison. Revisit only if cold
start turns out to be the deciding factor.

**Constraint that must not be broken:** the output stays a single `Volco.exe`.
`GitHubUpdater.AssetName = "Volco.exe"`, `ArtifactName = "Volco-win-x64"`, and
`UpdateInstaller.ApplyAndRestart` writes a batch script that `move /Y`s one file
over the running exe and relaunches. Breaking the single-file shape breaks
updating for every existing install.

---

## 7. Implementation phases

Each phase should leave the repo building and green on CI.

1. **`IAudioEngine` + `FakeAudioEngine`.** Define the interface on the neutral
   side of the `<Compile Remove>` boundary; retarget `SliderProcessor` and
   `Actions/`; write a thin adapter over the existing `AudioManager` so the app
   keeps working. Port and extend the tests. *Zero behaviour change, green CI.*
2. **Config schema v2 + `ConfigMigrator`**, with tests. *Linux-testable.*
3. **`ControllerStateReporter`** extracted and fixed (§3), with tests.
   *Linux-testable.*
4. **`Audio/Windows/AudioEngine.cs`** — the real COM engine (shared spec §2).
   Delete `AudioManager`, `SessionMonitor`, `svcl.exe`, the `NAudio` reference.
   **First measurable win; take measurements here** per shared spec §8, because
   this isolates the audio fix from the OSD change.
5. **`Osd/Windows/NativeVolumeOsd.cs`** (Option A §5). Delete `VolumeOsdWindow`.
   **Second measurable win — measure again.**
6. **Tray de-XAML** (§5). **Third measurement.**
7. Update ViewModels for `AudioDevice` binding; README corrections; full
   measurement run.

Measuring after each of 4, 5 and 6 is the point of this branch — it attributes the
improvement to a cause instead of to "the rewrite."

---

## 8. Risks and open questions

| Risk | Mitigation |
|---|---|
| WinUI's idle cost dominates even after all of this | That is a *result*, not a failure — it is exactly what this branch exists to determine. Record it. |
| `TrackPopupMenu` / `Shell_NotifyIcon` interop from a WinUI 3 app | §5 option 2 is the fallback. Spike before committing to option 1. |
| CsWin32 alongside the Windows App SDK / WinUI tooling | Verify the generator coexists cleanly with `UseWinUI`; this is the main unknown that could force hand-written interop. |
| `IPolicyConfig` vtable/GUIDs | Shared spec §6 — verify against a reference header; failures non-fatal. |
| Removing NAudio reveals undocumented reliance on its behaviour | The `IAudioEngine` adapter in phase 1 means the swap happens behind a tested interface. |
| No local Windows build (per `CLAUDE.md`) | Every phase must be CI-verifiable; manual Windows checks batched at phase ends. |

---

## 9. Acceptance criteria

1. Measurements recorded in `MEASUREMENTS.md` (shared spec §8) against a `main`
   baseline, **with intermediate readings after phases 4, 5 and 6** so each fix's
   contribution is attributable.
2. Handle count flat across a 60 s slider sweep (today it grows).
3. All nine original test files pass, plus new `ConfigMigrator`,
   `ControllerStateReporter`, `SliderProcessor`-against-`FakeAudioEngine`, and
   `SessionFilter` tests.
4. The Windows manual checklist in shared spec §7 passes.
5. A v1 `config.json` from an existing install migrates silently, with a
   `config.v1.bak` written.
6. The ESP32 firmware and `tools/simulator/index.html` work against the new build
   with no changes — including LED feedback (`0x03`, `0x04`), and with the §3 fix
   the active-device LED is now correct for a third device.
7. In-app update from a published release still works end-to-end.
8. No `svcl.exe`, no bundled third-party executables, no NAudio.
9. README updated: the Tech Stack section currently claims `COM IPolicyConfig` is
   in use when the code shells out to `svcl.exe` — correct it.

---

## 10. Out of scope

- Changing the UI framework. That is Option A.
- Any Linux or macOS support.
- Any change to the wire protocol or the ESP32 firmware.
- Redesigning the settings UX, or reworking the five settings pages beyond the
  minimal ViewModel edits in §2.2.
- Replacing the update mechanism.

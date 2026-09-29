# Step 1 (Decompose) — Manual Regression Checklist

This is a full manual regression, because Step 1 changed the audio API
(`AudioRole` removed) and moved all orchestration out of `App.xaml.cs` into
`Volco.Core.Runtime.VolcoRuntime`.

**Build under test:** the `Volco-win-x64` artifact from the green
`workflow_dispatch` (`run_build=true`) run on branch `step1-decompose`,
run id [`36639772876`](https://github.com/ToMeRhh/volco/actions/runs/36639772876)
(commit `dbe0603`). Download the artifact, extract it, and run `Volco.exe`
directly — it is a self-contained single-file publish, no install needed.

**How to report failures:** for each failing item, note the item number,
what you did, what you expected, what actually happened, and — if possible —
the relevant lines from `%APPDATA%/Volco/volco.log`. Reply on the PR for this
branch (or to whoever asked you to run this checklist) with the list of
failing items; do not merge the PR until every item passes or the failures
are explicitly accepted.

---

## Checklist

1. **Launch.** Start `Volco.exe`.
   *Expected:* a tray icon appears; hovering it shows the tooltip "Volco".

2. **Tray → settings window.** Left-click the tray icon, then close the
   settings window, then left-click the tray icon again.
   *Expected:* settings window opens on click; closing it hides it (does not
   exit the app); clicking the tray again reopens it.

3. **Slider mappings.** For each slider mapping type — Device, Processes,
   Unmapped (catch-all), and the default-output slider — move the physical
   slider.
   *Expected:* the correct target's volume changes; the on-screen volume OSD
   shows the correct label for what changed.

4. **Button actions.** For each configured button action — mute output, mute
   input, mute a specific device, set output device, toggle output device —
   trigger single press, double press, and long press.
   *Expected:* each press type produces the correct effect; the mute OSD
   shows the correct glyph and mute/unmute state.

5. **Controller LED sync.**
   - Mute a device from the Windows volume mixer (not from the controller).
   - Switch the Windows default output device from Windows Sound settings
     (not from the controller).
   *Expected:* the controller's mute LEDs track the mixer-initiated mute; the
   active-device LED tracks the Sound-settings-initiated default-device
   switch.

6. **Controller reconnect.** Unplug the ESP32 controller, then replug it.
   *Expected:* the app reconnects automatically; mute and active-device LEDs
   are correct within about 3 seconds of reconnect.

7. **Connection-type switch** *(Review Focus #2 — pins
   `Restart_ForwardsStatusFromNewTransportOnly`)*. In Settings → Connection,
   change the connection type (e.g. Serial → UDP or vice versa).
   *Expected:* the status shown on the Connection page updates to reflect the
   *new* transport's state, not a stale status left over from the old one.

8. **Unplugged-device mute press** *(Review Focus #3 — pins
   `MutePress_UnresolvableDevice_NeverShowsOsd`)*. Unplug a device that is
   mapped to a mute button, then press that button. Afterwards, change the
   volume of something else (e.g. the default device or another mapped
   slider).
   *Expected:* pressing the button for the unplugged device shows no OSD;
   the later, unrelated volume change does not trigger a stray OSD either.

9. **v1 config migration.** Place a v1-format `config.json` (bare device
   name-strings, no endpoint IDs) at `%APPDATA%/Volco/config.json`, then
   launch the app.
   *Expected:* migration happens silently (no dialog, no crash); a
   `config.v1.bak` backup file is written next to `config.json`; the app
   comes up with the migrated device mappings intact.

10. **Release-channel updater visibility** *(Review Focus #1 — pins
    `BuildInfoStampTests`)*. Run a release-stamped build — either a
    `release.yml`-produced build, or a local publish with
    `-p:VolcoReleaseChannel=release` via CI — and open the tray menu.
    *Expected:* the tray menu shows "Check for Updates…". (A `local`-channel
    dev build, such as the artifact under test in item 1–9, should *not*
    show it — verify that too, as a contrast check.)

11. **Web simulator over WebSocket.** Switch the app's connection type to
    WebSocket, then open `tools/simulator/index.html` in a browser and
    connect it.
    *Expected:* moving simulated sliders and pressing simulated buttons in
    the browser drives the app exactly as physical hardware would; LED
    feedback (mute state, active device) is reflected back in the simulator.

---

## Done when

All 11 items pass, or any failures are explicitly reviewed and accepted
before the PR for `step1-decompose` is merged.

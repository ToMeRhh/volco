# Step 2 (Avalonia cutover) — Manual Windows Checklist

Step 2 replaced the WinUI 3 exe with an Avalonia 12 app (`src/Volco.App/`,
single-file `Volco.exe`) and deleted `src/Volco/`. Only a human on Windows can
verify rendering, focus, scaling, tray, updater and shutdown behaviour, so this
is a full manual pass. Run it side by side with the Step 1 (WinUI) build where
an item says "compare".

**Build under test:** the `Volco-win-x64` artifact from Windows run
[`36852267391`](https://github.com/ToMeRhh/volco/actions/runs/36852267391) on
branch `step2-avalonia`. Built from commit `21f698e`; its code equals the branch head (any
later commit only changes docs).
```bash
gh run download 36852267391 -n Volco-win-x64 --repo ToMeRhh/volco
```
Extract it and run `Volco.exe` directly (self-contained single-file, no
install).

`build.yml` keeps artifacts for 7 days, so this one expires around
**2026-10-08**. If it is gone, re-dispatch and use the new run's artifact:
```bash
gh workflow run build.yml --ref step2-avalonia --field run_build=true
```

**Check that the build under test is still current.** If the branch has new
commits after review fixes, do not test run `36852267391`: use the artifact of
the newest `run_build=true` run on `step2-avalonia` instead
(`gh run list --branch step2-avalonia --workflow build.yml --limit 5`).

**How to report failures:** for each failing item, note the item number, what
you did, what you expected, what happened, and the relevant lines of
`%APPDATA%/Volco/volco.log`. Reply on the PR (or to whoever asked you to run
this) with the list of failing items. Do not merge until every item passes or
the failures are explicitly accepted. Items marked *(judgement)* need your
opinion, not a pass/fail.

---

## Checklist

1. **Cold start.** Launch `Volco.exe` (stopwatch it).
   *Expected:* tray icon appears in under 1 s; tooltip reads "Volco".

2. **Tray icon.** Look at the tray icon at 100% and your actual scaling.
   *Expected:* white "V" on a blue background, sharp (not blurry or jagged).

3. **Tray menu and Exit.** Left-click the tray icon, then right-click it.
   *Expected:* left-click opens Settings; the right-click menu has the right
   items; Exit quits cleanly and no `Volco.exe` remains in Task Manager.
   (Check for Updates appears only on a release-stamped build; see item 17.)

4. **Close and reopen settings.** Change something, close the window, reopen
   from the tray.
   *Expected:* closing hides (app keeps running); reopening shows the same
   state. The window cannot be resized below 680x560.

5. **Pages vs WinUI.** Open all five pages (General, Connection, Output
   Devices, Sliders, Buttons) and compare with the Step 1 build.
   *Expected:* same content and layout intent; every setting persists across
   an app restart.

6. **Sliders page: process picker.** In a Processes mapping, type a partial
   name, then pick a process from the AutoCompleteBox dropdown with the mouse.
   Repeat with an existing comma-separated list.
   *Expected:* only the last comma-separated token is replaced; the dropdown
   does not immediately reopen after the pick.

7. **Sliders page: values and level bar.** Set Dead Zone and Smoothing to
   several values; check `%APPDATA%/Volco/config.json`. Move a slider and
   watch the live level bar.
   *Expected:* Dead Zone and Smoothing are saved as numbers with at most 2
   decimals (e.g. `0.25`, not `0.2500000149`); the level bar has sensible
   height and look (*judgement*).

8. **Buttons page.** Use the timing spinners (NumericUpDown, which looks
   different from WinUI's) and the "Test" button on an action.
   *Expected:* the spinners are usable by mouse and keyboard and values
   persist; "Test" triggers the configured action.

9. **OSD triggers.** Trigger the OSD with a device slider, a process slider,
   an unmapped slider, and each mute action.
   *Expected:* bottom-centre of the primary monitor each time, with the right
   label.

10. **OSD behaviour.** With a text editor focused, trigger the OSD; click a
    desktop icon beneath it; open Alt-Tab while it shows.
    *Expected:* click-through; never steals focus from the editor; auto-hides
    after about 1.5 s with a fade that looks right (*judgement*); not listed
    in Alt-Tab; no flash at an old position on first show.

11. **OSD appearance and scaling.** Check at 100%, 125% and **150%** display
    scaling.
    *Expected:* sharp at all three (the spike was only verified at 125%);
    corners are transparent, not black; corner radius looks right. It is 12 px
    versus WinUI's ~8 (*judgement*; report if it looks wrong).

12. **OSD mute text.** Mute then unmute.
    *Expected:* muted shows "Muted"; unmuted shows the device's volume
    percentage (e.g. "42%").

12a. **OSD output-device switch.** Press a Set Output Device and a Toggle
    Output Device button. Then switch the default output in Windows Sound
    settings.
    *Expected:* each button press shows the new device's name, the speaker icon
    and its volume (or the muted icon and "Muted" if it is muted). A switch
    made in Windows Sound settings shows no OSD.

13. **Controller I/O.** With the ESP32 over serial, then with
    `tools/simulator/index.html` over WebSocket: move sliders, press buttons,
    watch LEDs (`0x03` mute, `0x04` active device). Unplug and replug the
    ESP32.
    *Expected:* sliders, buttons and LED feedback all work on both
    transports; the app recovers after replug (LEDs correct within about 3 s).

14. **v1 config migration.** Put a v1 `config.json` (bare device name-strings)
    in `%APPDATA%/Volco/`, launch.
    *Expected:* silent migration, a `config.v1.bak` next to it, mappings
    intact.

15. **Clean exit logging.** Exit from the tray menu, open
    `%APPDATA%/Volco/volco.log`.
    *Expected:* the log ends with the final shutdown lines ("Shutting down",
    etc.), not cut off. (Crash logging is not practically user-testable; this
    is the stand-in.)

16. **Logoff with Settings open.** Open Settings, then sign out of Windows.
    *Expected:* no "app is preventing shutdown" prompt; on next login
    `volco.log` ends with the shutdown lines, not cut off.

17. **Updater and release channel.**
    - **Contrast check:** on the CI artifact under test, the tray menu does
      **not** show "Check for Updates…".
    - **Release-channel, option (a) — verify the update *from* this build.**
      On your own machine, make a release-stamped local publish:
      ```
      dotnet publish src/Volco.Windows/Volco.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:VolcoVersion=0.0.1 -p:VolcoReleaseChannel=release -o publish
      ```
      Run it. *Expected:* the tray menu shows "Check for Updates…". Open it,
      enter a GitHub token (the repo is private), press "Check for
      Updates". *Expected:* it finds the current latest release, and "Update
      Now" downloads it, swaps the exe and relaunches. This proves the new
      UpdateWindow's download, swap and relaunch path.
    - **CI channel:** tick "Update from the latest CI build instead of a
      release (advanced)", press Check. *Expected:* it reports the latest CI
      artifact (or "No CI build artifact was found."); unticking switches
      back and clears any previously found update.
    - **Option (b):** defer the update-*to*-this-build check to the first
      real release after merge.

    **Never run `release.yml` from this branch (or from any branch other than
    a real release).** `release.yml` calls `gh release create`, which is not a
    draft. Publishing one from `step2-avalonia` would put an unreviewed build
    at `/releases/latest`, which `GitHubUpdater` reads, and offer it to every
    installed user as an update.

18. **Artifact contents.** List the extracted artifact folder.
    *Expected:* only `Volco.exe` plus the 3 small `Volco*.pdb` files; no
    native `lib*.pdb`, no `svcl.exe`, no Windows App SDK DLLs.

19. **OSD on a second monitor with different DPI.** Attach a second monitor at a
    different scaling than the primary (e.g. primary 100%, second 150%), then
    trigger the OSD.
    *Expected:* the OSD is correctly scaled and positioned on the primary
    monitor.

20. **OSD re-show.** Trigger the OSD, let it auto-hide, trigger it again. Then
    open and close Settings and trigger it once more. (The product reuses one
    OSD window; the spike did not.)
    *Expected:* it shows again each time and, each time, stays click-through,
    does not take focus, and keeps transparent corners.

21. **Live level bars on first open.** Right after launch, before ever opening
    Settings, move the sliders on the controller. Then open Settings for the
    first time and go to the Sliders page.
    *Expected:* the level bars are correct immediately, without moving the
    sliders again.

22. **Typing a port.** On the Connection page, type a new UDP or WebSocket
    port with the keyboard (e.g. `12000`), then check
    `%APPDATA%/Volco/config.json`.
    *Expected:* the saved value is the full number (`12000`), not an
    intermediate like `1200`.

23. **Light theme.** Switch Windows to Light mode (Settings > Personalization >
    Colors), launch or restart `Volco.exe`, and open every Settings page.
    *Expected:* the app follows the OS theme and everything is readable
    (text, cards, level bars, icons, borders). Switch back to Dark and check
    again.

---

## Done when

All 23 items pass (or failures are explicitly accepted), and the results table
in `MEASUREMENTS.md` is filled in, before the Step 2 PR is merged.

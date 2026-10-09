# Custom button actions: media, programs, scripts

Date: 2026-10-08. Status: implemented (branch `worktree-custom_actions`); the Windows parts await a Windows build and the manual checklist (§8). §10 lists where the code differs from the first draft.

## 1. Goal

Buttons get three new kinds of action next to the audio and preset ones:

- **Media**: Play/Pause, Next, Previous, Stop, sent to the current media app or to a chosen one.
- **Open a program**: start a program (a game, an app), or bring it to the front if it runs.
- **Run a script**: run a file the user wrote (`.bat`, `.ps1`, `.py`, …) hidden, as an escape hatch
  for anything else (webhooks via `curl`, batch commands).

Each new action fits the existing model: a slot (single, double, long press) of a button in a
preset. Webhooks as a built-in action, a "stop the script" repeat mode, script timeouts, scripts
setting their own button's display, and a playing animation are out of scope (§9).

Single user, Windows only for now; app and firmware ship together, so there is no migration or
compatibility code.

## 2. Architecture

`Volco.Core` and `Volco.App` stay platform-free. The Windows-only parts come in through two new
seams, like `IAudioEngine`:

- **`IMediaSessions`** (Core, `Media/`): lists the apps with a media session, each one's playback
  state (playing, paused, stopped) and which one is current, raises `Changed`, and sends
  play/pause, next, previous and stop to "current" or to an app id. Windows implementation:
  `Volco.Windows/Media/MediaSessions` over `GlobalSystemMediaTransportControlsSessionManager`.
- **`IProgramLauncher`** (Core, `Programs/`): whether a program path runs, start it (shell
  execute), bring its main window to the front, and `Changed` when a watched program starts or
  exits. Windows implementation: `Volco.Windows/Programs/ProgramLauncher` (process enumeration,
  image-path match, Win32 foreground calls).

Script running is platform-neutral .NET (`System.Diagnostics.Process`, not P/Invoke) and stays in
Core: **`ScriptRunner`** (Core, `Scripts/`) starts processes through a small `IProcessStarter`
interface, so tests can use a fake process. Only its default interpreter table (§5.1) is
Windows-flavoured, and it is data.

`PlatformServices` gains `Media` and `Launcher`, both optional (null on Linux tests and on a future
macOS build). `ActionFactory.Create` takes an `ActionServices` bundle (audio, presets, media,
launcher, script runner, the current transport) instead of a growing parameter list. Without a
service, its actions are unavailable: the picker shows them disabled (`ActionChoice.IsAvailable`).

### 2.1 Config (schema v6)

`ButtonActionType` gains `MediaPlayPause`, `MediaNext`, `MediaPrevious`, `MediaStop`,
`OpenProgram`, `RunScript`. `ButtonActionConfig` gains, as loose optional fields like `Device` and
`PresetId`:

- `MediaApp`: `{ id, name }`, the app's Windows id (its AppUserModelId, e.g. `Spotify.exe`) and
  its display name; null = "Current".
- `Command`: `{ path, arguments, workingDirectory }`, shared by `OpenProgram` and `RunScript`.
- `WhileRunning`: `Ignore` (default) or `StartAnother`, for `RunScript`.

`CurrentSchemaVersion` becomes 6. No migration logic beyond the version bump.

### 2.2 Feedback

`ControllerDisplay` reads media state, program running state and script state (running, failed
flash) through the same services. `VolcoRuntime` subscribes to `IMediaSessions.Changed`,
`IProgramLauncher.Changed` and `ScriptRunner.Changed` and calls `PushControllerState`; the link's
dedup absorbs repeats. The script failure flash reuses the announce-tick mechanism
(`AnnounceSteps`, ~1.5 s), so the firmware needs no protocol change.

### 2.3 UI

`ActionCatalog` gets a **Media** group (the four media actions) and replaces the "Custom" placeholder
with **Open a program** and **Run a script**. The action card adds, per action:

- media: an app picker: "Current", every app with a session now, and the saved app when it is not
  running ("Spotify (not running)");
- program and script: a file picker, arguments, working folder (optional);
- script: the while-running choice ("Ignore presses while it runs" / "Start another run").

Program and script cards say "Runs only over USB" when the connection type is not Serial (§7).
A path that does not exist is marked on the card when the page is shown.

## 3. Media

### 3.1 Target and pressing

A media action's target is **Current** (Windows' current session, the one the volume flyout shows)
or a **specific app**. The display name comes from Windows' app info for packaged apps, else the id
without `.exe`.

A press looks up the target's session and sends the command in the background (never blocking
input). No session for Current, a specific app not running, or the app having disabled that
control (`PlaybackInfo.Controls`), means nothing happens; the press is logged. Several sessions
with the same app id: the one playing, else the most recent.

### 3.2 State

The runtime keeps a snapshot: per app id, its playback state, and the current app. It updates from
the session manager's sessions-changed and current-session-changed events and each session's
playback-info-changed event. If the session manager cannot start, media actions are unavailable
and this is logged once.

### 3.3 Display

| Action | Single LED / LED pair | Screen (layout 1) |
|---|---|---|
| Play/Pause, Stop | lit while the target plays (a pair lights A, like mute) | top: `PLAYING` / `PAUSED` / `STOPPED`; icon: play (playing) or pause; main: app name |
| Next, Previous | dark (no state, like Next preset) | top blank; icon: next / previous; main: app name |
| target not running | dark | top blank; icon: play; main: app name (`Media` for Current with no session) |

New screen icons: play, pause, next, previous (and the app and script icons of §4–5), added to
`firmware/assets` and generated by `gen_assets.py` into the firmware, the C# `ScreenIcon` enum and
the simulator's `icons.js`.

## 4. Open a program

- **Target:** an `.exe`, a `.lnk` (resolved to its target), or anything the shell opens (a
  `steam://rungameid/…` URL, a document). Arguments and working folder are optional; the folder
  defaults to the exe's.
- **Press:** if the program runs, bring its main window to the front, restoring it if minimised;
  else start it through the shell (a program that requires admin shows UAC). Presses within ~5 s
  of a launch are ignored, so a double tap does not start two copies.
- **Running detection** (exe and `.lnk` only): a process whose image path matches,
  case-insensitively. Polled about every 2 s, only for programs that an action in the live preset
  names; a program Volco started itself is known at once (its `Exited`). A process Volco cannot
  query (elevated) counts as not running. URLs and documents have no running state: dark, and
  every press opens them.
- **Bringing to front:** Windows restricts a background app taking the foreground; the
  implementation uses the usual workaround (attach to the foreground thread's input, then
  `SetForegroundWindow`). Where that fails, the window flashes in the taskbar.
- **Display:** LED lit while running. Screen: top `RUNNING` or blank, app icon, main = the
  command's name (its Name field, else the file name without extension, else the URL without
  its scheme); "Choose a file" when none is set.

## 5. Run a script

### 5.1 Running

Always hidden (no window), stdout and stderr captured, working folder = the script's unless set.

| File | Runs as |
|---|---|
| `.bat`, `.cmd` | `cmd.exe /d /c "<path>" <args>` |
| `.ps1` | `pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<path>" <args>`; `powershell.exe` with the same flags when `pwsh` is not on PATH |
| `.py` | `py "<path>" <args>`; `python` when `py` is not on PATH |
| `.js` | `node "<path>" <args>` |
| `.exe` | itself |
| other (`.ahk`, `.vbs`, …) | the shell's file association: no captured output; exit code and running state when the shell returns a process |

Start-up cost per press, for guidance in the picker's help text: `.exe` and `.bat` ~10–30 ms,
Python and Node tens of ms, PowerShell hundreds of ms. The help text recommends `.bat` or `.exe`
when speed matters.

The script gets `VOLCO_BUTTON` (input number), `VOLCO_GESTURE` (`single`, `double`, `long`) and
`VOLCO_PRESET` (the preset's name) in its environment.

### 5.2 Repeat presses

- **Ignore** (default): presses do nothing while a run of this action is going.
- **Start another:** each press starts a run; the action counts as running until all have ended.

### 5.3 Result

Exit code 0 is silent. A non-zero exit, or a failure to start (file missing, interpreter not
found), logs the exit code or error with the last ~20 lines of output and gives the **failure
flash** for ~1.5 s. Volco never kills a script, at exit either; there is no timeout.

### 5.4 State and display

Running and failed state is kept per (preset, button, gesture), so two buttons using one script
light independently, and switching presets does not stop a run (switching back shows it still lit).
Editing a command during a run leaves that run alone; the next press uses the new command.

LED lit while running, blinking during the failure flash. Screen: top `RUNNING`, `FAILED` during
the flash, else blank; script icon; main = the command's name.

## 6. Errors and edge cases

| Case | Behaviour |
|---|---|
| Media service missing or failed to start | media actions unavailable in the picker; logged once |
| Launcher missing (non-Windows) | program action unavailable |
| Program or script path missing at press | failure flash (scripts), log line naming the path; the card marks it |
| Interpreter missing | failure flash, log names the interpreter |
| Program or script press over WebSocket or UDP | refused, logged (§7) |

## 7. Security

A button press can now run code. Button frames reach Volco over three transports, and two of them
are open to others:

- **WebSocket** (the simulator, `ws://localhost:8765`): any web page in a local browser can connect
  and send Button frames.
- **UDP** (the Wi-Fi controller): anything on the LAN can send a forged Button frame; UDP carries
  nothing to authenticate.
- **USB serial**: needs physical access or local code, which can run anything anyway.

**Rule until this is solved:** `OpenProgram` and `RunScript` run **only when the press arrived over
USB serial**. Over WebSocket or UDP the press is refused and logged; the card says "Runs only over
USB" when the connection type is not Serial. Media actions, like the existing audio and preset
actions, run over every transport. A consequence: the simulator cannot trigger programs or
scripts.

**Resolution, a separate effort:** authenticate the controller, so a press can be trusted whatever
the transport. One direction is seeding the controller with a key (in its config, written over
USB) and authenticating frames with it. The design of this feature must not assume that solution:
the transport rule above is one check (`ActionServices` knows the press's transport) that a later
"is this press trusted" check replaces.

`config.json` holds the commands, but whoever can write it already runs as the user. The
controller's config blob carries no actions, so a controller cannot define a command.

## 8. Testing

- **Core (xUnit, Linux):** `ActionFactory` mapping; `ControllerDisplay` tables for every new action
  with fake `IMediaSessions` and `IProgramLauncher`; `ScriptRunner` with a fake `IProcessStarter`
  (exit codes, Ignore vs Start another, failure flash and its expiry, environment, output tail);
  the transport rule (refused over WebSocket and UDP, allowed over serial); the runtime pushing
  state on media, program and script events; config v6 round trip.
- **App (Avalonia headless):** picker groups; new actions unavailable without the services;
  card fields per action; the "Runs only over USB" note; card summaries.
- **Firmware and simulator:** new icons through `gen_assets.py`; regenerated render goldens;
  `icons.js`.
- **Windows, manual checklist:** Spotify and a browser tab (Current vs a named app, playing state
  on LED and screen); a game by exe and by `steam://` URL (start, bring to front, running LED);
  `.bat`, `.ps1`, `.py` (success, failure flash, a long run, repeat modes); a UAC program; a
  program or script press over the simulator is refused.

## 9. Later

- A built-in **Call a webhook** action (HTTP without a shell, result on the LED).
- Controller authentication (§7), then lifting the USB-only rule.
- A "Stop it" repeat mode, turning a script button into start/stop.
- A per-script timeout.
- A playing animation on screens (a firmware flag, e.g. an animated icon), after trying the static
  icons on hardware.
- Scripts setting their own button's LED or screen text.

## 10. As built

Where the implementation differs from the draft above:

- **Screens** follow the existing layout-1 convention: the top line is the state, the main line
  the name (§3.3 and §4–5 above are corrected).
- **Program names** come from an optional Name field on the command (also useful for
  `steam://` links), else the file name; the exe's file description is not read.
- **Media availability:** the session manager is requested in the background after start, so
  the media actions are always offered on Windows; if the request fails, the failure is logged
  once and presses do nothing.
- **Script state** is kept per action object (one slot of one button in one preset), which is
  the (preset, button, gesture) key of §5.4; moving a card moves its state with it.
- **`.url` files** (Steam's desktop shortcuts) are offered by the program picker; like any
  non-exe they open on every press and have no running state.
- **Program and script presses** remember, per button, whether the press frame came from the
  current USB serial transport; the gesture (decided at release, maybe after the double-press
  wait) uses that flag.

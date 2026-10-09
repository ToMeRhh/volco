# Playing indicator: animated equaliser on media screens

Date: 2026-10-09. Status: approved design, not built. Issue #90.

## 1. Goal

A media button's screen (Play/Pause, Stop; custom-actions spec §3.3) shows at a glance whether its
app plays:

- **Playing:** equaliser bars bouncing in the icon area.
- **Paused or stopped:** the same bars, frozen at a short, uneven height.

Motion means playing. The top text (`PLAYING`, `PAUSED`, `STOPPED`) and the main text (the app's
name) stay as they are.

Out of scope: a setting to turn the animation off (added later if it distracts on the desk), icons
uploaded from the PC, and animating anything but this icon.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | **An animated icon**, option 1 of issue #90: a new icon id whose art is several frames. The firmware picks the frame from its own clock; the PC sends the id once. No protocol change. |
| D2 | **Paused is a still icon** of the same bars (option B1), not the pause glyph and not a blink. |
| D3 | **No setting.** The bars move at normal and dimmed brightness and stop only while the panels are off. Frozen bars on a dimmed screen would read as paused. |
| D4 | **The frame clock is the scroll clock** (`ScreenSlot::scrollSince`): each screen's cycle starts when its content settles. Screens are not in step with each other. |
| D5 | **Dirty-tile pushes:** the firmware sends a panel only the 8×8 tiles that changed, so an animating icon costs ~128 bytes of I2C per frame, not 1 KB. |

The art stays on the controller: like every icon, the frames are compiled into the firmware
(`kIconBits`), and the `Screen` frame carries only the one-byte id. The C# and simulator copies are
for previews only.

## 3. Icons and assets

Two ids are appended to `firmware/assets/icons/manifest.txt` (ids are stable: append, never
insert):

| id | stem | C# name | frames | Devices page |
|---|---|---|---|---|
| 17 | `equalizer` | `Equalizer` | 4 | no |
| 18 | `equalizer_still` | `EqualizerStill` | 1 | no |

- The manifest gains a `frames` column; every existing icon has 1. An icon with one frame is
  `<stem>.png`; an icon with n > 1 frames is `<stem>_0.png` … `<stem>_<n-1>.png`.
- The art: 28×28, 1-bit, four or five vertical bars with a 1–2 px gap, standing on a common
  baseline. The four `equalizer` frames move each bar to a different height so the loop reads as
  bouncing; `equalizer_still` is one low, uneven set. Drawn by a small Pillow script (kept in
  `firmware/tools/`), committed as PNGs like the others; the user reviews the frames, enlarged,
  before the firmware work.
- `gen_assets.py` writes every frame into `kIconBits` (now indexed by frame, not id) plus
  `kIconFirstFrame[kIconCount]` and `kIconFrames[kIconCount]`, and a frame period constant,
  `kIconFrameMs = 180`. It emits the frame counts to the C# side (`ScreenIconSets.Frames` or
  equivalent) and frame lists to the simulator's `icons.js`.

## 4. Rendering (`volco_render`)

`renderScreen(canvas, content, sinceMs, scroll)` keeps its signature. For an icon with n frames it
draws frame `(sinceMs / kIconFrameMs) % n`; for one frame, frame 0 as today. An unknown id still
falls back to generic. During a slide both sides are rendered with `since = 0`, so the bars hold
frame 0 and start moving once the slide ends. The muted slash still applies to any frame.

A new pure function computes the box of 8×8 tiles in which two canvases differ (or none), so the
firmware's partial push is host-tested.

## 5. Firmware screen loop (`screens.cpp`)

`screens_tick` already re-renders each panel at most every 20 ms (round-robin, one panel per pass),
skips unchanged frames and stops while the panels are off, so the animation runs with no new
timer. The change is how a changed frame is sent: the dirty-tile box of §4 is pushed with U8g2's
`updateDisplayArea` instead of `sendBuffer`. A slide, scroll or pixel shift that changes most
tiles costs what it costs today. With four panels animating, the loop spends roughly 4 × ~4 ms of
I2C per 180 ms frame instead of 4 × ~25 ms.

`volco_screen_state` needs no change: a slide into or out of an animated screen is a slide between
two rendered canvases.

## 6. App

- `ControllerDisplay` (Play/Pause and Stop): `Playing` → `Equalizer`; `Paused` and `Stopped` →
  `EqualizerStill`. "Target not running" keeps the play icon. `Play` and `Pause` stay in the icon
  set.
- `ScreenPreview` (settings) animates an icon with several frames on a timer at `kIconFrameMs`,
  stopped while the control is detached. `ScreenIcons.Bitmap` loads frame PNGs by the same naming
  rule; the frame PNGs are linked into `Volco.App/Assets/Icons` like the others.
- `ScreenIcons.Geometry` (OSD, Devices page) is not used for these icons; neither is offered on the
  Devices page.

## 7. Simulator

`oled.js` draws frame `(since / FRAME_MS) % n` from the frame lists in `icons.js`, pixel-identical
with the firmware goldens. Its screen loop already re-renders with a live `since`.

## 8. Testing

- Native (`pio test -e native`): render goldens `equalizer_f0` (since 0), `equalizer_f2` (since
  360), `equalizer_wrap` (since 720, frame 0 again), `equalizer_still`; dirty-tile box tests
  (identical canvases, one pixel, the icon area, a full change).
- Simulator (`node --test`): the same goldens through `oled.test.mjs`.
- Core: `ControllerDisplay` sends `Equalizer` while playing and `EqualizerStill` while paused or
  stopped.
- App: `ScreenPreview` shows a multi-frame icon's frames in turn (headless).
- Hardware: the user's test board (one SSD1306 on I2C bus 0, SDA 25 / SCL 26; a button on GPIO 33
  wired to GND) with this branch's firmware and a config for it in `firmware/tools/bench/` (not `proto/presets/`, which the app embeds), written with `config_tool.py`; the
  user builds and flashes locally, then compares Playing, Paused and a slide with the Windows app.

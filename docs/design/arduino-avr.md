# Arduino Uno / Nano controllers (ATmega328P)

Date: 2026-10-17. Status: design approved in conversation, awaiting spec review.

## 1. Goal

The classic ATmega328P boards, the Arduino Uno and the Arduino Nano (the board most deej builds
use, usually a CH340 clone with the old bootloader), become first-class Volco controllers next to
the ESP32: the same Hardware page, the same config session, the same link status, and in-app
Install and Update.

Success: plug in an existing deej box, press Install on the Hardware page, set it up from the
"deej" preset, and its sliders control volume with no rewiring. Firmware updates then go through
the app, as on the ESP32.

Single user; app and firmware ship together, so there is no migration or compatibility code.

## 2. Decisions

| # | Decision |
|---|---|
| D1 | Target: the ATmega328P (and 328) on the Uno and the Nano, 16 MHz. One firmware image for both. |
| D2 | LEDs and LED pairs only: no screens, no I2C, no Wi-Fi/UDP on this chip. |
| D3 | One chip family, "Arduino Uno / Nano" (Hello chip 2). The boards can't be told apart, so A6/A7 are offered as "A6 (Nano only)", "A7 (Nano only)"; on an Uno they read noise, which the hardware test shows. |
| D4 | In-app flashing as for the ESP32: Install, Update, probe, banner. |
| D5 | A separate AVR firmware (`src/avr/`) with its own decoder and validator. It speaks exactly the ESP32's wire contract (Hello, config session, blob format, result codes, the same first-violation triple) and passes the same golden vectors. Merging the two firmwares later must be an internal refactor only. |
| D6 | Shared between the firmwares: `volco_protocol` (compile-time switches), `volco_crc32`, the config session logic (`BlobReceiver`, the read reply) over a storage interface, a new `lib/volco_input`, the LED `indicators`, and the generated chip table. |
| D7 | One source for chip facts, `proto/chips.json`, generating the C++, C# and simulator tables. |
| D8 | One firmware build id for all of `firmware/`; both images report it. |

## 3. Constraints

ATmega328P: 2 KB SRAM, 32 KB flash, 1 KB EEPROM, 10-bit ADC, one hardware UART (D0/D1) behind the
USB chip, no Wi-Fi. Bootloaders: Optiboot (Uno, newer Nanos; 0.5 KB, 115200 baud) or the old
ATmegaBOOT (most Nano clones; 2 KB, 57600 baud). EEPROM writes take about 3.4 ms per byte, while
115200 baud delivers about 11.5 bytes per ms. Optiboot can't write EEPROM.

## 4. Chip table (`proto/chips.json`)

One JSON file describes every chip family. `firmware/tools/gen_chips.py` writes:

- `firmware/lib/volco_config/src/volco_chip_table.cpp` (the tables behind `volco_chip.h`),
- `src/Volco.Core/Controller/ChipInfo.g.cs`,
- `tools/simulator/chips.js`.

The outputs are committed; CI regenerates and fails on a difference (like the config vectors).
Stage 1 adds only the ESP32 entry, and its generated tables must equal today's.

Per chip:

- `id` (the Hello chip byte: 1 ESP32, 2 Arduino Uno / Nano), `key` (`esp32`, `avr328`, also the
  firmware folder and manifest `chip`), `name` (shown in the app).
- Pin sets as lists: `exists`, `reserved`, `inputOnly`, `analogOnly` (new: slider only, never a
  button, LED or I2C pin), `adc1`, `adc2`. Derived as today: usable = exists − reserved;
  output-capable = usable − inputOnly − analogOnly; digital = usable − analogOnly;
  ADC-capable = (adc1 ∪ adc2) ∩ usable.
- `labels`: the pin's name in the UI (`GPIO 4`; `D2`, `A0`), and optional `notes` (`Nano only`).
- `limits`: `sliders`, `buttons`, `blob` (maximum config bytes), `chunk` (config chunk bytes).
- `features`: `screens`, `wifi`.

| | ESP32 | Arduino Uno / Nano |
|---|---|---|
| exists | 0–19, 21–23, 25–27, 32–39 | 0–21: D0–D13 (0–13), A0–A5 (14–19), A6–A7 (20–21) |
| reserved | 1, 3, 6–11 | 0, 1 (serial) |
| analog only | none | 20, 21 (A6, A7; note "Nano only") |
| ADC | as today | 14–21 |
| sliders / buttons | 16 / 32 | 8 / 16 |
| blob / chunk | 1024 / 240 | 384 / 96 |
| screens / Wi-Fi | yes / yes | no / no |

A full AVR config (8 sliders, 16 buttons with LED pairs, input tuning, a preset name) is about
280 bytes. The pin numbers are the Arduino core's (`A0` = 14 … `A7` = 21), which is what the AVR
firmware passes to `pinMode`/`analogRead`.

## 5. Contract changes

**Hello:** chip 2 = Arduino Uno / Nano (`ChipFamily.ArduinoAvr = 2`). The body is unchanged.
Slider values stay 0–4095: the AVR firmware scales its 10-bit readings (`v << 2 | v >> 8`).

**Validation (controller-config spec §6.4).** No new result codes. Changes, all chip-driven:

- Step 1 (counts) uses the chip's limits instead of 16/32; the triples stay (INVALID, 0xFF, 1)
  and (INVALID, 0xFF, 2).
- A button pin must be *digital*; an LED, LED pair or I2C pin must be *output-capable* (both now
  exclude analog-only pins). Existing fields 2–5 and 9 report it.
- New field 17, **screen unsupported**: a screen indicator on a chip without screens, reported at
  the button's `screen.bus` position (item = button index), before the bus checks.
- New field 18, **UDP unsupported**: `udp.enabled` on a chip without Wi-Fi, reported first in the
  UDP step (item 0xFF).
- I2C buses and screen behaviour are checked as today. Without screens no bus is ever started.
- A blob over the chip's `blob` limit is TOO_LARGE at ConfigWriteBegin (as today, with the chip's
  limit). The app checks the encoded size before it sends and says the config is too large for
  this board.

`firmware/test/golden/config.txt` gains a chip column; every existing case runs for both chips
(expected results per chip), plus cases for the new rules. `gen_config_vectors.py` writes them.

**Config session:** the app sends chunks of the chip's `chunk` size (`ProtocolEncoder`,
`ControllerConfigService`). The receiver rules are unchanged.

## 6. Firmware

### 6.1 Layout

```
firmware/
  src/esp32/   main.cpp, config_store (NVS), config_session, transport (serial + UDP), screens
  src/avr/     main.cpp, config_store (EEPROM), config_session, avr_config (decode + validate)
  lib/         volco_protocol, volco_config, volco_input (new), volco_indicators (moved), ...
```

`platformio.ini` selects the folder per environment with `build_src_filter`. New environment
`avr328`: a pinned `atmelavr` platform, `board = uno`, `-std=gnu++17` (no STL is used),
`-DVOLCO_CHIP_AVR328`, `-DSERIAL_RX_BUFFER_SIZE=128`, nanopb with `proto/volco_config_avr.options`.
The `native` environment builds both firmwares' pure parts.

### 6.2 Shared libraries

- **`volco_protocol`:** `VOLCO_MAX_BUTTONS` and `VOLCO_HAS_SCREENS` (defaults: 32, 1). Without
  screens, `Link::Held` keeps only the LED value and Screen frames are ignored. The parser's
  payload buffer is `VOLCO_MAX_PAYLOAD` (AVR: the chunk frame, 112 bytes; longer frames are
  dropped).
- **`volco_config` session parts:** `BlobReceiver` writes into a caller-supplied buffer with the
  chip's capacity and chunk limit (ESP32 1024/240, AVR 384/96; both buffer in RAM, §6.4). The read
  reply streams frame by frame from a `BlobSource` (`emitReadReply`; EEPROM on AVR); the ESP32
  keeps `buildReadReply`, which collects them into one buffer for one UDP datagram.
  `volco_crc32` gains an incremental form for EEPROM reads (stage 3).
- **`volco_input`** (new, pure C++, host-tested): slider smoothing and deadband, and button
  debounce, moved out of `main.cpp`. The EMA becomes fixed-point (value ×256, the config's weight in exact percent)
  on both chips; its output must stay within one ADC unit of the float version in the tests. This
  is the one behaviour change on the ESP32 and gets a hardware feel test (§9).
- **`volco_indicators`:** `indicators.cpp` as is (`digitalWrite`), moved to `lib/`.
- **Chip table:** `volco_chip.h` gains `analogOnly`, the limits and the features, filled by the
  generated `volco_chip_table.cpp`.

### 6.3 AVR decoder and validator

nanopb, generated with `proto/volco_config_avr.options`, which makes `sliders`, `buttons`,
`preset`, `udp.ssid` and `udp.password` callback fields. The callbacks write each slider and
button into a compact struct (pins as `uint32_t` with presence flags, so out-of-range values are
still seen), keep only the strings' length and the NUL/UTF-8 checks, and stop at the chip's count
limit plus one. Validation then runs over that struct in exactly the §6.4 order, and `toRuntime`
fills the AVR `RuntimeConfig` (8 sliders, 16 buttons, no buses, no UDP).

The decoder reads from a `pb_istream_t`: from the RAM buffer for a write, from EEPROM at boot. The
golden vectors run against it in `pio test -e native` with an AVR-limits build.

### 6.4 EEPROM store

Two 512-byte slots at 0x000 and 0x200. Slot header (12 bytes): magic `"VC"`, format 1,
sequence (u8), length (u16), CRC-32 of the data (u32), header checksum (u16). Data follows.

- **Boot:** a slot is valid when its magic, format, length (≤ 384), header checksum and data CRC
  are good. With two valid slots the newer sequence wins (serial-number arithmetic mod 256). None
  valid: no config (Hello config state None), as on a blank or foreign EEPROM.
- **Save:** write the data into the slot that isn't current, then its header last, then read it
  back and check the CRC. An interrupted save leaves the old slot current. A save takes about
  1.3 s and blocks; it runs before the OK reply, inside the app's 3 s `ResultWait`. After OK the
  board resets through the watchdog (as the ESP32 restarts).
- **Damaged:** a valid header whose data no longer decodes or validates is config state Damaged,
  with its CRC, as on the ESP32.

### 6.5 Main loop

The ESP32's order without screens and Wi-Fi: sliders, buttons, receive, link tick, config tick.
Boot: serial at 115200, load config, pins, seed sliders from 8 reads, Hello. The Uno resets when
the port opens and its bootloader waits about 0.5–1.5 s before the app runs; the app's connect
grace and HelloRequest retries must cover this (checked in §9).

### 6.6 Budgets

The `avr328` build must use at most 1,536 bytes of static RAM (leaving 512 for the stack) and
30,720 bytes of flash (fits the old 2 KB bootloader). CI fails the build above either; a post
script reads `avr-size`.

## 7. Flashing

### 7.1 `Stk500Flasher` (`Volco.Core/Firmware/`)

An `IFirmwareFlasher` speaking STK500v1 over `System.IO.Ports`, in Core so its tests run on Linux
against a simulated Optiboot.

- **Enter:** DTR (and RTS) low for 100 ms, then up; `STK_GET_SYNC` until answered, at 115200, then
  at 57600 after a second reset. No answer at either: `NoDeviceException`.
- **Chip check:** `STK_READ_SIGN`. 1E 95 0F (ATmega328P) and 1E 95 14 (ATmega328) are accepted;
  1E 98 01 is named "Arduino Mega (ATmega2560)", 1E 94 06 "ATmega168"; anything else "an unknown
  AVR". Others: `UnsupportedChipException`.
- **Probe:** read the bundled app's length of flash (`STK_READ_PAGE`) and compare byte for byte:
  `VolcoMatches` or `DifferentFirmware`. About 3 s at 115200, 6 s at 57600.
- **Flash:** `STK_LOAD_ADDRESS` + `STK_PROG_PAGE` per 128-byte page (progress per page),
  then read every page back and compare (Verifying), then `STK_LEAVE_PROGMODE`, which starts the
  app (Restarting). The cancel token is honoured until the first page write.
- **Install = Update** on this chip: EEPROM is not touched. A blank or foreign EEPROM is already
  "no config" (§6.4); an existing Volco config survives an Install, and the setup dialog's "start
  from scratch" replaces it.

### 7.2 Choosing a flasher

`PlatformServices.Flashers` is a list of `IFirmwareFlasher`, each with its `ChipFamily`. Windows
supplies the EspDotNet flasher and `Stk500Flasher`; tests supply fakes.

- **Chip known:** from the current Hello, else `config.controller.Chip`: that family's flasher and
  image.
- **Chip unknown** (no Hello: deej or other firmware, blank, a version mismatch): probe with the
  ESP32 flasher, then with `Stk500Flasher`, stopping at the first that finds its chip. A USB ID
  can reorder this: `2341:*` (Arduino's ATmega16U2) means AVR first; the CH340 is used on both and
  gives no hint. Both probes only reset the wrong kind of board.
- The `NoAnswer` banner keeps offering Install; `UnsupportedChipException` and the banner's
  messages name every family the catalog has firmware for.

### 7.3 Bundled firmware, CI, release

- `manifest.py` writes `firmware-manifest.json` for both environments. The AVR manifest has
  chip `avr328`, the version, one `app` part at offset 0 (`firmware.bin` from the `.hex`), and no
  `nvs` range; `FirmwareManifest` makes `nvs` optional and applies its checks only when present.
- `firmware_id.sh` hashes all of `firmware/`; both images carry the same id (D8).
- `FirmwareCatalog` holds an image per chip key; `FirmwareService` picks by family.
- The `firmware-test` job builds both environments, uploads `firmware-esp32` and
  `firmware-avr328`; the Windows build embeds `Firmware/esp32/` and `Firmware/avr328/`, and
  `RequireFirmware` fails without either or with another build's id. The Linux compile check
  embeds both too.
- `release.yml` attaches `volco-firmware-avr328-<version>.zip` with its relink bundle (the Arduino
  AVR core is LGPL) and `THIRD-PARTY-NOTICES.txt`.
- `hello_check.py` and `config_tool.py` also accept `2341:0043`/`2341:0001` (genuine Uno) next to
  the CH340.

## 8. App and simulator

**Core:**

- `ChipInfo` is the generated table with `ChipInfo.For(ChipFamily)`, `Limits`, `Features`,
  `PinLabel(pin)`, `PinNote(pin)`, `Digital(pin)`.
- `ControllerConfigValidator`, `DraftProblems` and `ControllerDraft.Hints` apply §5 for the given
  chip; every caller passes the connected chip (the current Hello's, else `config.controller`'s)
  instead of `ChipInfo.Esp32`.
- `ControllerBanner`: the firmware notice is for any chip the catalog has an image for.

**Presets:** files in `proto/presets/` gain `"chip"`. New `deej-nano.json`: chip `avr328`,
preset `deej`, sliders A0–A4 (14–18), no buttons. The setup dialog lists the connected chip's
presets, and offers "apply the saved one" only when the mirror validates for that chip.

**Hardware page:**

- The board line shows the chip's `name`.
- Pin pickers show the chip's labels and notes; analog-only pins appear only in slider pickers.
- On a chip without screens: no Screen indicator choice, no I2C rows, no screen behaviour, no bus
  clocks. Without Wi-Fi: no Wi-Fi section.
- "Add slider" / "Add button" are disabled at the limit, with "Arduino Uno / Nano supports up to
  8 sliders" (16 buttons).

The Buttons page needs nothing: it follows the per-button kinds in Hello.

**Simulator:** the builder gets a board choice (ESP32, Arduino Uno / Nano); `layout.js` allocates
from `chips.js`; Hello reports the chosen chip; the config session uses its limits and chunk
size; an AVR board has no screens in the builder and the 3D device shows LEDs only. The Node tests
cover both.

## 9. Testing

**Automated:**

- Firmware native: the golden vectors (`config.txt` per chip, `frames.txt`) against both
  validators and decoders; the EEPROM store over a fake EEPROM (interrupted save at every byte
  keeps the old config, sequence wrap, blank/foreign header, bad data CRC falls back); `volco_input`
  (fixed-point EMA against the float reference, deadband, debounce); `volco_protocol` with screens
  compiled out; `BlobReceiver` over both storages; the chunked read reply.
- CI gates: `avr328` builds; RAM ≤ 1,536 and flash ≤ 30,720 bytes; `chips.json` outputs and config
  vectors are current.
- Core: validator, `DraftProblems` and hints per chip; `Stk500Flasher` against a simulated
  Optiboot (sync at 115200, fallback to 57600, unknown signature, verify mismatch, cancel before
  the first write, progress); `FirmwareService` flasher choice and unknown-chip probe order;
  `FirmwareManifest` with and without `nvs`; chunk size per chip.
- App: pin labels and notes, hidden sections and disabled Add on AVR, presets by chip, the banner
  on an AVR Hello.
- Simulator: both chips.

**Manual** (the user has a CH340 Nano clone, a genuine Uno and the ESP32 board):

1. Nano clone with deej firmware: the `NoAnswer` banner offers Install; it installs at 57600;
   set up from the deej preset; sliders change volume; hardware test shows every input.
2. Genuine Uno: Install at 115200; Update; Update again probes as `VolcoMatches`; a saved config
   survives Update; LEDs and LED pairs follow their buttons.
3. Unplug during a config save: the old config loads after replug.
4. Port open after reset: Hello arrives and the link reaches Connected on both boards.
5. ESP32 regression: connects, flashes, saves. **Feel test of the fixed-point slider smoothing
   and the moved debounce** (stage 1).

## 10. Stages (one PR each)

1. **Shared groundwork, no AVR:** move ESP32 sources to `src/esp32/`; `chips.json` + generator
   (ESP32 only, outputs equal today's tables); `lib/volco_input` (fixed-point); `volco_indicators`;
   `ConfigStorage` under `BlobReceiver` and the chunked read reply; `volco_protocol` switches.
   ESP32 hardware feel test.
2. **Chip-aware contract:** AVR entry in `chips.json`; `ChipFamily.ArduinoAvr`; limits, fields 17
   and 18, digital/analog-only rules; per-chip golden vectors; chunk size by chip; callers pass the
   connected chip. No hardware needed.
3. **AVR firmware:** `avr328` environment, EEPROM store, decoder/validator, main loop, budgets in
   CI, tool USB IDs. Boards flashed with `pio run -e avr328 -t upload`, checked with
   `hello_check.py` and `config_tool.py`.
4. **In-app flashing:** `Stk500Flasher`, flasher list, catalog and manifest by chip, embedding both
   images, release zip and relink bundle.
5. **UI, presets, simulator, docs:** the Hardware page, `deej-nano.json` and the setup dialog, the
   simulator's board choice, `CLAUDE.md` and `firmware/README.md`.

## 11. Out of scope

- Other AVR boards (Mega, Leonardo/Pro Micro with native USB), the Nano Every, the Nano ESP32.
- Screens or Wi-Fi on the 328P.
- Telling a Uno from a Nano.
- Detecting deej's own serial output to word the banner.
- Erasing EEPROM on Install.
- Follow-up to consider after this feature: reusing the shared `validateConfig` on AVR (nanopb with
  AVR options, decoding straight from EEPROM) instead of the second validator; needs a RAM spike.

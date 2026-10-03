# Controller Config, Stage 1: Spikes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer every open technical question that stages 2–6 depend on (spec §12.6 S1–S3), record the answers, and land the one keeper change, the exact firmware platform pin. Everything else written here is throwaway.

**Architecture:**
- **S2 (toolchain) and the nanopb half of the codec facts** run locally with PlatformIO in this container.
- **S3 (Grpc.Tools / Google.Protobuf)** runs only in GitHub Actions, through a temporary `spike.yml` workflow, because this machine must not run `dotnet`.
- **S1 (flashing)** needs the real ESP32. One part runs with esptool, either in this container (if the board is passed through) or on the user's PC. The EspDotNet part runs on Windows, using a console exe built by `spike.yml`.
- **All answers go in one results document.** All spike code lives under `spikes/` and is deleted in the last task.

**Tech Stack:**
- PlatformIO 6.1.18 with pioarduino platform 55.03.38 (Arduino-ESP32 3.3.8)
- nanopb 0.4.9.2
- esptool 5.x (bundled `tool-esptoolpy`)
- .NET 8 (built in CI only): Google.Protobuf 3.36.2, Grpc.Tools 2.84.0, EspDotNet 5.0.2, System.IO.Ports 8.0.0

**Spec:** `docs/superpowers/specs/2026-10-03-controller-config-design.md` (§12.6 spikes, §15 stage 1; also §5.4, §6.1, §6.6, §7, §8.6, §9.1, §9.4)

## Global Constraints

- **Never run `dotnet` locally.** All .NET building and running happens in GitHub Actions (CLAUDE.md).
- **Use `/usr/bin/git`.** Plain `git` is refused in this worktree session. Write file contents with the Write tool, not heredocs.
- **Branch:** `worktree-firmware`. Pushing it triggers `build.yml` (tests only) and, from Task 4 on, `spike.yml`. **Never** trigger the build/publish job (`run_build=true`).
- **Platform pin (the keeper):** `platform = https://github.com/pioarduino/platform-espressif32/releases/download/55.03.38/platform-espressif32.zip` (verified: HTTP 200).
- **Library pins:** nanopb `nanopb/Nanopb@0.4.9.2`. U8g2 and WebSockets are pinned to the exact versions Task 1 resolves.
- **The spike proto is a verbatim copy of spec §6.1 and its options.** Stage 2 creates the real `proto/` from the spec, not from the spike copy.
- **Spike tests assert what the spec *expects*. A failing assertion is a finding, not a bug to fix.** Record the actual behaviour in the results doc and move on. Never weaken an assertion to make it pass.
- **Hardware steps marked [USER-RUN] are run by the user** (or here, if the board is passed through, see Task 5). The executor stops and asks the user to run them and paste the output.
- **Results document:** `docs/superpowers/spikes/2026-10-03-controller-config-spikes.md`, filled in by every task.

## Review Focus

1. **CI and the dev machine resolve different ESP32 cores.** `platform = espressif32` resolves to whatever each machine has installed. The pinned build must show framework 3.3.8 in CI *and* locally. Checked in Task 1 (CI log check).
2. **Writing firmware parts touches NVS** (erase granularity spilling into 0x9000–0xDFFF). Expectation: the NVS MD5 is unchanged after writing all four parts. Checked in Task 5 (esptool) and Task 6 (EspDotNet).
3. **C# and nanopb disagree on edge-case blobs** (invalid UTF-8, an embedded NUL, a uint32 overflow, count and size limits, unknown fields). Expectation: every difference is known and recorded. Checked by the same vector set in Task 3 (nanopb) and Task 4 (C#), compared in Task 7.
4. **A serial error during a reboot would make Volco drop the connection.** Expectation: we know exactly which `ErrorReceived` events Windows raises when the board resets with the port open. Checked in Task 6 (`serial-errors`).
5. **Firmware booting over garbage NVS** (for example, after foreign firmware) doesn't recover. Expectation: Arduino's `initArduino` erases NVS and the firmware runs and sends frames. Checked in Task 5 (marker, then boot).

---

## File Structure

| Path | Purpose | Fate |
|---|---|---|
| `firmware/platformio.ini` | Exact platform and library pins | **kept** |
| `docs/superpowers/spikes/2026-10-03-controller-config-spikes.md` | All answers | **kept** |
| `spikes/print_flash_layout.py` | PlatformIO extra script: prints flash parts and offsets | deleted in Task 7 |
| `spikes/proto/volco_config.proto`, `spikes/proto/volco_config.options` | Spec §6.1 copy for the spikes | deleted |
| `spikes/nanopb_pio/` | PlatformIO project: nanopb generation plus vector tests | deleted |
| `spikes/ProtoSpike/` | .NET console: Grpc.Tools generation plus vector checks | deleted |
| `spikes/EspDotNetSpike/` | .NET console: EspDotNet probe, flash, wipe, serial-errors | deleted |
| `spikes/flash_facts.py` | esptool-driven NVS and MD5 facts | deleted |
| `.github/workflows/spike.yml` | Temporary CI for the .NET spikes and the firmware parts | deleted |

---

### Task 1: Pin the firmware platform and record the toolchain facts (S2a)

**Files:**
- Modify: `firmware/platformio.ini` (the `[env:denky32]` block, lines 4–16)
- Create: `spikes/print_flash_layout.py`
- Create: `docs/superpowers/spikes/2026-10-03-controller-config-spikes.md`

**Interfaces:**
- Produces:
  - the pinned `platformio.ini`, used by every later task
  - the results doc, with sections S1, S2, S3 and Vectors
  - the **flash layout** (part files and offsets), used by Tasks 4, 5 and 6
  - the **pio upload command line**

- [ ] **Step 1: Find out what an unpinned fresh install resolves to (the CI situation)**

Run:
```bash
cd /root/dev/volco/.claude/worktrees/firmware/firmware
PLATFORMIO_CORE_DIR=/tmp/claude-0/-root-dev-volco/d68eb0f5-b900-4b17-8565-46298fd08eea/scratchpad/pio-fresh \
  ~/.platformio/penv/bin/pio pkg install -e denky32 2>&1 | grep -iE "platform|framework" | head
```
Expected: a line naming the platform and framework a clean machine gets (likely `platformio/espressif32 @ 6.x` with `framework-arduinoespressif32 @ 3.20017.x`, i.e. Arduino 2.0.17). Record it in the results doc under **S2 → "Unpinned resolution"**, next to the dev machine's (pioarduino 55.03.38, Arduino 3.3.8, found in `~/.platformio/platforms/espressif32/platform.json`).

- [ ] **Step 2: Write the results doc skeleton**

Create `docs/superpowers/spikes/2026-10-03-controller-config-spikes.md`:

```markdown
# Controller config — stage 1 spike results

Spec: `docs/superpowers/specs/2026-10-03-controller-config-design.md` §12.6. Plan:
`docs/superpowers/plans/2026-10-03-controller-config-stage1-spikes.md`.

## S2 — firmware toolchain

| Question | Answer | Evidence |
|---|---|---|
| Unpinned resolution on a fresh machine (= CI before this stage) | | |
| Dev machine platform/framework | pioarduino 55.03.38 / Arduino 3.3.8 | `~/.platformio/platforms/espressif32/platform.json` |
| Pinned platform builds `denky32`, the 3 check envs and `native` locally | | |
| CI builds with the pinned platform and logs framework 3.3.8 | | |
| Exact U8g2 / WebSockets versions pinned | | |
| Flash parts and offsets (`FLASH_EXTRA_IMAGES`, app offset) | | |
| Where `bootloader.bin` and `boot_app0.bin` come from | | |
| Partition table (nvs / otadata / app0 offsets and sizes) | | |
| `pio run -t upload` command line: which offsets it writes | | |
| `initArduino` erases NVS on NO_FREE_PAGES / NEW_VERSION_FOUND | | |
| nanopb generation from `../proto` works in `native` and `esp32` | | |
| Exact `custom_nanopb_*` keys that work | | |
| Generated names (prefix, oneof, has_ flags, string arrays) | | |
| Empty blob: are absent submessages filled with defaults? | | |
| Maximal blob encoded size (≤ 1024?) | | |

## S3 — Grpc.Tools / Google.Protobuf

| Question | Answer | Evidence |
|---|---|---|
| Generation works in the Linux job | | |
| Generation works in the Windows single-file publish | | |
| Required csproj items (`PrivateAssets`, `ProtoRoot`, …) | | |
| Maximal blob size (must equal nanopb's) | | |
| Encoding determinism (same message twice → same bytes) | | |
| Unknown field position after Decode+Encode | | |

## Vectors — nanopb vs C#

| Vector | Hex | nanopb | C# | Spec expectation |
|---|---|---|---|---|
| empty | (none) | | | decode ok, all defaults |
| screen_default | `12 02 22 00` | | | ok; screen bus 0, address 0x3C |
| brightness_only | `32 02 08 64` | | | ok; brightness 100, dim_enabled true, transition 360 |
| buttons33 | `12 00` ×33 | | | fail (max_count 32) |
| ssid33 | `2A 23 12 21` + `61`×33 | | | fail (max_size) |
| bad_utf8 | `42 02 C3 28` | | | fail |
| nul_in_string | `42 03 61 00 62` | | | fail (spec: our own check) |
| u32_overflow | `0A 06 08 80 80 80 80 10` | | | nanopb fail; C# truncates (spec §6.1 known divergence) |
| unknown_field | `98 06 01 32 02 08 64` | | | ok, brightness 100 |

## S1 — flashing

| Question | Answer | Evidence |
|---|---|---|
| Chip, flash size, MAC (esptool `flash_id`) | | |
| Writing the 4 parts leaves NVS (0x9000–0xDFFF) unchanged (esptool) | | |
| Firmware boots over a garbage NVS and recovers (NVS rewritten, frames sent) | | |
| Boot ROM log readable at 115200 | | |
| esptool `verify_flash` (MD5) passes for all 4 parts | | |
| EspDotNet: bootloader entry + chip detect via DTR/RTS on this board | | |
| EspDotNet: `SpiFlashMd5Async` works (stub) and matches local MD5 | | |
| EspDotNet: writing 4 parts leaves NVS MD5 unchanged | | |
| EspDotNet: bootloader region MD5 equals `bootloader.bin` MD5 (no header patching) | | |
| EspDotNet: 0xFF over NVS works | | |
| EspDotNet: flash ≥ 4 MB proxy (MD5 of 0x3FF000+0x1000 succeeds) | | |
| EspDotNet: timings (probe, install) | | |
| Windows `ErrorReceived` events when the board resets with the port open | | |

## Decisions and spec amendments

(filled in Task 7)
```

- [ ] **Step 3: Pin the platform and libraries**

In `firmware/platformio.ini`, change `[env:denky32]`:

```ini
[env:denky32]
; Exact pins: CI and every dev machine must build the same core (spec §7).
platform = https://github.com/pioarduino/platform-espressif32/releases/download/55.03.38/platform-espressif32.zip
board = esp32dev
framework = arduino
monitor_speed = 115200
build_flags =
    -std=gnu++17
build_unflags =
    -std=gnu++11
lib_deps =
    links2004/WebSockets@<exact version from Step 4>
    olikraus/U8g2@<exact version from Step 4>
test_ignore = *
```

- [ ] **Step 4: Resolve and pin the exact library versions**

Run:
```bash
cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio pkg install -e denky32 && ~/.platformio/penv/bin/pio pkg list -e denky32
```
Expected: the list shows `WebSockets @ X.Y.Z` and `U8g2 @ A.B.C`. Replace the two placeholders in Step 3 with `links2004/WebSockets@X.Y.Z` and `olikraus/U8g2@A.B.C` (exact, no `^`). Record both in the results doc.

- [ ] **Step 5: Build every environment and run the native tests with the pin**

Run:
```bash
cd /root/dev/volco/.claude/worktrees/firmware/firmware && for e in denky32 denky32_screens_check denky32_udp_check denky32_websocket_check; do ~/.platformio/penv/bin/pio run -e $e || break; done && ~/.platformio/penv/bin/pio test -e native
```
Expected: four `SUCCESS` builds and all native tests `PASSED`. Record the result.

- [ ] **Step 6: Write the flash layout probe script**

Create `spikes/print_flash_layout.py`:

```python
# PlatformIO extra script (spike only): prints what a flasher must write and where.
Import("env")  # noqa: F821  (provided by PlatformIO)


def _print_layout(source, target, env):
    print("SPIKE-LAYOUT BEGIN")
    for offset, path in env.get("FLASH_EXTRA_IMAGES", []):
        print(f"SPIKE-LAYOUT part {env.subst(offset)} {env.subst(path)}")
    print(f"SPIKE-LAYOUT app {env.subst(env.get('ESP32_APP_OFFSET', '?'))} {env.subst('$BUILD_DIR/${PROGNAME}.bin')}")
    print(f"SPIKE-LAYOUT partitions_csv {env.subst(env.get('PARTITIONS_TABLE_CSV', '?'))}")
    print("SPIKE-LAYOUT END")


env.AddPostAction("$BUILD_DIR/${PROGNAME}.bin", _print_layout)  # noqa: F821
```

- [ ] **Step 7: Run it in a throwaway environment and record the layout**

Append to `firmware/platformio.ini` (this is removed in Task 7):

```ini
; SPIKE ONLY (stage 1) — removed before merge.
[env:spike_layout]
extends = env:denky32
extra_scripts = post:../spikes/print_flash_layout.py
```

Run:
```bash
cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio run -e spike_layout -t clean && ~/.platformio/penv/bin/pio run -e spike_layout | grep SPIKE-LAYOUT
```
Expected: four lines, for the bootloader (`0x1000`), partitions (`0x8000`), `boot_app0` (`0xe000`) and app (`0x10000`), each with a file path. Then:
```bash
cat "$(~/.platformio/penv/bin/pio run -e spike_layout | grep 'SPIKE-LAYOUT partitions_csv' | awk '{print $3}')"
```
Record the parts table, the path each file comes from (build dir or framework dir), and the nvs/otadata/app0 rows in the results doc.

- [ ] **Step 8: Record the upload command line and the NVS erase code**

Run:
```bash
cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio run -e denky32 -t upload --upload-port /dev/null-spike -v 2>&1 | grep -m1 -E "esptool(\.py)?.*write[-_]flash"
```
Expected: the esptool command line pio would run (it fails afterwards, because the port doesn't exist). Record the offset/file pairs it writes, and confirm none is in 0x9000–0xDFFF.

Run:
```bash
sed -n 315,345p ~/.platformio/packages/framework-arduinoespressif32/cores/esp32/esp32-hal-misc.c
```
Expected: the `ESP_ERR_NVS_NO_FREE_PAGES || ESP_ERR_NVS_NEW_VERSION_FOUND` branch calls `nvs_flash_erase()` and then `nvs_flash_init()`. Quote the lines in the results doc.

- [ ] **Step 9: Commit and check CI**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/platformio.ini spikes/print_flash_layout.py docs/superpowers/spikes/2026-10-03-controller-config-spikes.md
/usr/bin/git commit -m "build(firmware): pin pioarduino 55.03.38 and exact libs; spike layout probe" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push -u origin worktree-firmware
gh run list --branch worktree-firmware --limit 3
```
Wait for the `build.yml` run (`gh run watch <id>`). Expected: `firmware-test` passes. Then:
```bash
gh run view <id> --log | grep -iE "framework-arduinoespressif32|PLATFORM:" | head -5
```
Expected: framework `3.3.8`. Record "CI builds with the pinned platform" as yes, with the run URL. If CI shows a different framework, that is Review Focus #1 firing: record it and stop to tell the user.

---

### Task 2: Shared spike proto (spec §6.1 copy)

**Files:**
- Create: `spikes/proto/volco_config.proto`
- Create: `spikes/proto/volco_config.options`

**Interfaces:**
- Produces: the schema with C prefix `volco_config_` and C# namespace `Volco.Core.ControllerConfig.Proto`, used by Tasks 3 and 4.

- [ ] **Step 1: Write the proto, verbatim from spec §6.1**

Create `spikes/proto/volco_config.proto`:

```proto
syntax = "proto2";
package volco.config;
option csharp_namespace = "Volco.Core.ControllerConfig.Proto";

message ControllerConfig {
  repeated Slider sliders = 1;
  repeated Button buttons = 2;
  optional I2cBus i2c0 = 3;
  optional I2cBus i2c1 = 4;
  optional Udp udp = 5;
  optional ScreenBehaviour screens = 6;
  optional InputTuning input = 7;
  optional string preset = 8;
}

message Slider {
  optional uint32 pin = 1;
}

message Button {
  optional uint32 pin = 1;
  oneof indicator {
    Led led = 2;
    LedPair led_pair = 3;
    Screen screen = 4;
  }
}

message Led {
  optional uint32 pin = 1;
  optional bool active_low = 2 [default = false];
}

message LedPair {
  optional uint32 pin_a = 1;
  optional uint32 pin_b = 2;
  optional bool active_low = 3 [default = false];
}

message Screen {
  optional uint32 bus = 1 [default = 0];
  optional uint32 address = 2 [default = 0x3C];
}

message I2cBus {
  optional uint32 sda = 1;
  optional uint32 scl = 2;
  optional uint32 clock_hz = 3 [default = 400000];
}

message Udp {
  optional bool enabled = 1 [default = false];
  optional string ssid = 2;
  optional string password = 3;
  optional fixed32 pc_ip = 4;
  optional uint32 pc_port = 5 [default = 12345];
  optional uint32 local_port = 6 [default = 12346];
}

message ScreenBehaviour {
  optional uint32 brightness = 1 [default = 255];
  optional uint32 transition_ms = 2 [default = 360];
  optional uint32 scroll_hold_ms = 3 [default = 2000];
  optional uint32 scroll_speed_px_s = 4 [default = 30];
  optional uint32 scroll_end_hold_ms = 5 [default = 1200];
  optional bool dim_enabled = 6 [default = true];
  optional uint32 dim_after_s = 7 [default = 120];
  optional uint32 dim_brightness = 8 [default = 8];
  optional bool off_enabled = 9 [default = true];
  optional uint32 off_after_s = 10 [default = 1800];
  optional bool pixel_shift_enabled = 11 [default = true];
  optional uint32 pixel_shift_every_s = 12 [default = 180];
  optional bool wake_swallows_press = 13 [default = true];
}

message InputTuning {
  optional uint32 debounce_ms = 1 [default = 30];
  optional uint32 slider_interval_ms = 2 [default = 10];
  optional uint32 filter_weight_pct = 3 [default = 20];
  optional uint32 slider_deadband = 4 [default = 25];
}
```

- [ ] **Step 2: Write the nanopb options**

Create `spikes/proto/volco_config.options`:

```
volco.config.ControllerConfig.sliders  max_count:16
volco.config.ControllerConfig.buttons  max_count:32
volco.config.ControllerConfig.preset   max_size:25
volco.config.Udp.ssid                  max_size:33
volco.config.Udp.password              max_size:65
```

- [ ] **Step 3: Commit**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add spikes/proto
/usr/bin/git commit -m "spike: config proto copy for the codec spikes" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: nanopb under PlatformIO, plus the vector facts (S2b)

**Files:**
- Create: `spikes/nanopb_pio/platformio.ini`
- Create: `spikes/nanopb_pio/src/main.cpp`
- Create: `spikes/nanopb_pio/test/test_vectors/test_main.cpp`

**Interfaces:**
- Consumes: `spikes/proto/volco_config.proto` and `.options` (Task 2), and the platform pin (Task 1).
- Produces: the nanopb column of the Vectors table, the generated names, the maximal blob size, and the working `custom_nanopb_*` keys.

- [ ] **Step 1: Write the PlatformIO project**

Create `spikes/nanopb_pio/platformio.ini`:

```ini
; SPIKE ONLY (stage 1). Mirrors the real layout: this project sits one level below the repo
; folder holding proto/, as firmware/ will sit next to the real proto/.
[platformio]
default_envs = native

[env]
lib_deps = nanopb/Nanopb@0.4.9.2
custom_nanopb_protos = +<../proto/volco_config.proto>
custom_nanopb_options = --error-on-unmatched
build_flags = -DPB_VALIDATE_UTF8

[env:native]
platform = native
test_build_src = no

[env:esp32]
platform = https://github.com/pioarduino/platform-espressif32/releases/download/55.03.38/platform-espressif32.zip
board = esp32dev
framework = arduino
build_flags = ${env.build_flags} -std=gnu++17
build_unflags = -std=gnu++11
test_ignore = *
```

- [ ] **Step 2: Write the esp32 build check**

Create `spikes/nanopb_pio/src/main.cpp`:

```cpp
// SPIKE ONLY: proves nanopb generation and decoding compile for the ESP32 target.
#include <Arduino.h>
#include <pb_decode.h>

#include "volco_config.pb.h"

static const uint8_t kScreenDefault[] = {0x12, 0x02, 0x22, 0x00};

void setup() {
  Serial.begin(115200);
  volco_config_ControllerConfig cfg = volco_config_ControllerConfig_init_default;
  pb_istream_t s = pb_istream_from_buffer(kScreenDefault, sizeof kScreenDefault);
  bool ok = pb_decode(&s, volco_config_ControllerConfig_fields, &cfg);
  Serial.printf("decode=%d buttons=%u\n", ok, (unsigned)cfg.buttons_count);
}

void loop() {}
```

- [ ] **Step 3: Write the vector tests**

Create `spikes/nanopb_pio/test/test_vectors/test_main.cpp`:

```cpp
// SPIKE ONLY. Each test asserts what the spec expects. A failure is a FINDING to record,
// not a bug to fix. TEST_MESSAGE lines carry the observed values into the log.
#include <pb_decode.h>
#include <pb_encode.h>
#include <stdio.h>
#include <string.h>
#include <unity.h>

#include "volco_config.pb.h"

static volco_config_ControllerConfig cfg;

static bool decode(const uint8_t* b, size_t n) {
  cfg = volco_config_ControllerConfig_init_default;
  pb_istream_t s = pb_istream_from_buffer(b, n);
  bool ok = pb_decode(&s, volco_config_ControllerConfig_fields, &cfg);
  char msg[160];
  snprintf(msg, sizeof msg, "decode=%d error=%s", ok, ok ? "-" : PB_GET_ERROR(&s));
  TEST_MESSAGE(msg);
  return ok;
}

void test_empty() {
  TEST_ASSERT_TRUE(decode(nullptr, 0));
  char msg[160];
  snprintf(msg, sizeof msg, "has_screens=%d screens.brightness=%u screens.dim_enabled=%d",
           cfg.has_screens, (unsigned)cfg.screens.brightness, cfg.screens.dim_enabled);
  TEST_MESSAGE(msg);  // records whether absent submessages are default-filled
}

void test_screen_default() {
  const uint8_t b[] = {0x12, 0x02, 0x22, 0x00};
  TEST_ASSERT_TRUE(decode(b, sizeof b));
  TEST_ASSERT_EQUAL_UINT(1, cfg.buttons_count);
  TEST_ASSERT_EQUAL_UINT(volco_config_Button_screen_tag, cfg.buttons[0].which_indicator);
  TEST_ASSERT_EQUAL_UINT(0, cfg.buttons[0].indicator.screen.bus);
  TEST_ASSERT_EQUAL_UINT(0x3C, cfg.buttons[0].indicator.screen.address);
}

void test_brightness_only() {
  const uint8_t b[] = {0x32, 0x02, 0x08, 0x64};
  TEST_ASSERT_TRUE(decode(b, sizeof b));
  TEST_ASSERT_TRUE(cfg.has_screens);
  TEST_ASSERT_EQUAL_UINT(100, cfg.screens.brightness);
  TEST_ASSERT_TRUE(cfg.screens.dim_enabled);
  TEST_ASSERT_EQUAL_UINT(360, cfg.screens.transition_ms);
}

void test_buttons33_fails() {
  uint8_t b[66];
  for (int i = 0; i < 33; i++) { b[2 * i] = 0x12; b[2 * i + 1] = 0x00; }
  TEST_ASSERT_FALSE(decode(b, sizeof b));
}

void test_ssid33_fails() {
  uint8_t b[4 + 33] = {0x2A, 0x23, 0x12, 0x21};
  memset(b + 4, 'a', 33);
  TEST_ASSERT_FALSE(decode(b, sizeof b));
}

void test_bad_utf8_fails() {
  const uint8_t b[] = {0x42, 0x02, 0xC3, 0x28};
  TEST_ASSERT_FALSE(decode(b, sizeof b));
}

void test_nul_in_string() {
  const uint8_t b[] = {0x42, 0x03, 0x61, 0x00, 0x62};
  bool ok = decode(b, sizeof b);
  char msg[120];
  snprintf(msg, sizeof msg, "nul: decode=%d strlen(preset)=%u", ok, ok ? (unsigned)strlen(cfg.preset) : 0u);
  TEST_MESSAGE(msg);  // spec §6.1: a NUL must be rejected by OUR check; this records nanopb's own behaviour
}

void test_u32_overflow_fails() {
  const uint8_t b[] = {0x0A, 0x06, 0x08, 0x80, 0x80, 0x80, 0x80, 0x10};
  TEST_ASSERT_FALSE(decode(b, sizeof b));
}

void test_unknown_field_skipped() {
  const uint8_t b[] = {0x98, 0x06, 0x01, 0x32, 0x02, 0x08, 0x64};
  TEST_ASSERT_TRUE(decode(b, sizeof b));
  TEST_ASSERT_EQUAL_UINT(100, cfg.screens.brightness);
}

void test_maximal_blob_size() {
  volco_config_ControllerConfig m = volco_config_ControllerConfig_init_default;
  m.sliders_count = 16;
  for (int i = 0; i < 16; i++) { m.sliders[i].has_pin = true; m.sliders[i].pin = 39; }
  m.buttons_count = 32;
  for (int i = 0; i < 32; i++) {
    volco_config_Button& b = m.buttons[i];
    b.has_pin = true; b.pin = 39;
    b.which_indicator = volco_config_Button_led_pair_tag;
    b.indicator.led_pair.has_pin_a = true; b.indicator.led_pair.pin_a = 39;
    b.indicator.led_pair.has_pin_b = true; b.indicator.led_pair.pin_b = 39;
    b.indicator.led_pair.has_active_low = true; b.indicator.led_pair.active_low = true;
  }
  volco_config_I2cBus bus = volco_config_I2cBus_init_default;
  bus.has_sda = true; bus.sda = 39; bus.has_scl = true; bus.scl = 39;
  bus.has_clock_hz = true; bus.clock_hz = 1000000;
  m.has_i2c0 = true; m.i2c0 = bus; m.has_i2c1 = true; m.i2c1 = bus;
  m.has_udp = true;
  m.udp.has_enabled = true; m.udp.enabled = true;
  m.udp.has_ssid = true; memset(m.udp.ssid, 'a', 32); m.udp.ssid[32] = 0;
  m.udp.has_password = true; memset(m.udp.password, 'a', 64); m.udp.password[64] = 0;
  m.udp.has_pc_ip = true; m.udp.pc_ip = 0xFFFFFFFFu;
  m.udp.has_pc_port = true; m.udp.pc_port = 65535;
  m.udp.has_local_port = true; m.udp.local_port = 65535;
  m.has_screens = true;
  volco_config_ScreenBehaviour& s = m.screens;
  s.has_brightness = true; s.brightness = 255;
  s.has_transition_ms = true; s.transition_ms = 2000;
  s.has_scroll_hold_ms = true; s.scroll_hold_ms = 10000;
  s.has_scroll_speed_px_s = true; s.scroll_speed_px_s = 200;
  s.has_scroll_end_hold_ms = true; s.scroll_end_hold_ms = 10000;
  s.has_dim_enabled = true; s.dim_enabled = true;
  s.has_dim_after_s = true; s.dim_after_s = 86400;
  s.has_dim_brightness = true; s.dim_brightness = 255;
  s.has_off_enabled = true; s.off_enabled = true;
  s.has_off_after_s = true; s.off_after_s = 86400;
  s.has_pixel_shift_enabled = true; s.pixel_shift_enabled = true;
  s.has_pixel_shift_every_s = true; s.pixel_shift_every_s = 86400;
  s.has_wake_swallows_press = true; s.wake_swallows_press = true;
  m.has_input = true;
  m.input.has_debounce_ms = true; m.input.debounce_ms = 200;
  m.input.has_slider_interval_ms = true; m.input.slider_interval_ms = 100;
  m.input.has_filter_weight_pct = true; m.input.filter_weight_pct = 100;
  m.input.has_slider_deadband = true; m.input.slider_deadband = 500;
  m.has_preset = true; memset(m.preset, 'p', 24); m.preset[24] = 0;

  size_t size = 0;
  TEST_ASSERT_TRUE(pb_get_encoded_size(&size, volco_config_ControllerConfig_fields, &m));
  char msg[80];
  snprintf(msg, sizeof msg, "maximal blob size=%u", (unsigned)size);
  TEST_MESSAGE(msg);
  TEST_ASSERT_LESS_OR_EQUAL_UINT(1024, size);
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_empty);
  RUN_TEST(test_screen_default);
  RUN_TEST(test_brightness_only);
  RUN_TEST(test_buttons33_fails);
  RUN_TEST(test_ssid33_fails);
  RUN_TEST(test_bad_utf8_fails);
  RUN_TEST(test_nul_in_string);
  RUN_TEST(test_u32_overflow_fails);
  RUN_TEST(test_unknown_field_skipped);
  RUN_TEST(test_maximal_blob_size);
  return UNITY_END();
}
```

- [ ] **Step 4: Run the native tests**

Run:
```bash
cd /root/dev/volco/.claude/worktrees/firmware/spikes/nanopb_pio && ~/.platformio/penv/bin/pio test -e native -v 2>&1 | tee /tmp/claude-0/-root-dev-volco/d68eb0f5-b900-4b17-8565-46298fd08eea/scratchpad/nanopb_native.log | grep -E "\[nanopb\]|INFO|PASS|FAIL|error" | head -60
```

Expected:
- `[nanopb] Processing '../proto/volco_config.proto' (volco_config.options)`
- then the 10 tests running

**If generation fails** because of the `../proto` path:
1. Retry with `custom_nanopb_project_dir = ${PROJECT_DIR}/..` and `custom_nanopb_protos = +<proto/volco_config.proto>` in `[env]`.
2. Record which form worked. That form is what stage 2 uses.

**If compilation fails on a generated name:** open the generated header with `find .pio -name volco_config.pb.h`, fix the *test's* names to match, and record the real naming (prefix, oneof union, `has_` flags, string arrays).

Record every test's PASS/FAIL and every `TEST_MESSAGE` value in the results doc (S2 rows and the nanopb column). **Failures are findings: do not edit assertions to pass.**

- [ ] **Step 5: Build the esp32 environment**

Run:
```bash
cd /root/dev/volco/.claude/worktrees/firmware/spikes/nanopb_pio && ~/.platformio/penv/bin/pio run -e esp32 2>&1 | grep -E "\[nanopb\]|SUCCESS|FAILED|RAM:|Flash:"
```
Expected: `SUCCESS`, with RAM and flash usage printed. Record "generation works in esp32" and the usage.

- [ ] **Step 6: Commit**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add spikes/nanopb_pio docs/superpowers/spikes/2026-10-03-controller-config-spikes.md
/usr/bin/git commit -m "spike: nanopb under PlatformIO and nanopb vector facts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Spike CI. Grpc.Tools / Google.Protobuf (S3), the EspDotNet exe, and the firmware parts

**Files:**
- Create: `spikes/ProtoSpike/ProtoSpike.csproj`
- Create: `spikes/ProtoSpike/Program.cs`
- Create: `spikes/EspDotNetSpike/EspDotNetSpike.csproj`
- Create: `spikes/EspDotNetSpike/Program.cs`
- Create: `.github/workflows/spike.yml`

**Interfaces:**
- Consumes: the Task 2 proto; the Task 1 layout (part paths); `firmware/platformio.ini` (`denky32`, pinned).
- Produces:
  - the C# column of the Vectors table and the S3 rows
  - artifact `spike-espdotnet-win-x64` (`EspDotNetSpike.exe`), used by Task 6
  - artifact `spike-firmware-parts` (`bootloader.bin`, `partitions.bin`, `boot_app0.bin`, `firmware.bin`, `parts.txt`), used by Tasks 5 and 6

`parts.txt` format: one part per line, `<hex offset> <file name>`, e.g. `0x1000 bootloader.bin`.

- [ ] **Step 1: Write the ProtoSpike project**

Create `spikes/ProtoSpike/ProtoSpike.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- SPIKE ONLY (stage 1): Grpc.Tools generation from a .proto outside the project folder. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.36.2" />
    <PackageReference Include="Grpc.Tools" Version="2.84.0" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <Protobuf Include="../proto/volco_config.proto" ProtoRoot="../proto" GrpcServices="None" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the ProtoSpike checks (the same vectors as Task 3)**

Create `spikes/ProtoSpike/Program.cs`:

```csharp
// SPIKE ONLY. Prints one line per vector: what Google.Protobuf does with it. Read the CI log.
using Google.Protobuf;
using Volco.Core.ControllerConfig.Proto;

static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

static void Check(string name, Func<string> f)
{
    try { Console.WriteLine($"VECTOR {name}: {f()}"); }
    catch (Exception e) { Console.WriteLine($"VECTOR {name}: THREW {e.GetType().Name}: {e.Message}"); }
}

static ControllerConfig Parse(string hex) => ControllerConfig.Parser.ParseFrom(Hex(hex));

Check("empty", () =>
{
    var c = ControllerConfig.Parser.ParseFrom(Array.Empty<byte>());
    return $"ok; Screens is null={c.Screens is null}; new ScreenBehaviour().Brightness={new ScreenBehaviour().Brightness}";
});
Check("screen_default", () =>
{
    var b = Parse("12 02 22 00").Buttons[0];
    return $"ok; case={b.IndicatorCase}; bus={b.Screen.Bus}; address=0x{b.Screen.Address:X2}; HasAddress={b.Screen.HasAddress}";
});
Check("brightness_only", () =>
{
    var s = Parse("32 02 08 64").Screens;
    return $"ok; brightness={s.Brightness}; dim_enabled={s.DimEnabled}; transition={s.TransitionMs}";
});
Check("buttons33", () => $"ok; buttons={Parse(string.Concat(Enumerable.Repeat("1200", 33))).Buttons.Count}");
Check("ssid33", () => $"ok; ssid length={Parse("2A23" + "1221" + string.Concat(Enumerable.Repeat("61", 33))).Udp.Ssid.Length}");
Check("bad_utf8", () => $"ok; preset={Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Parse("42 02 C3 28").Preset))}");
Check("nul_in_string", () => $"ok; preset length={Parse("42 03 61 00 62").Preset.Length}");
Check("u32_overflow", () => $"ok; pin={Parse("0A 06 08 80 80 80 80 10").Sliders[0].Pin}");
Check("unknown_field", () =>
{
    var c = Parse("98 06 01 32 02 08 64");
    return $"ok; brightness={c.Screens.Brightness}; re-encoded={Convert.ToHexString(c.ToByteArray())}";
});

static ControllerConfig Maximal()
{
    var m = new ControllerConfig { Preset = new string('p', 24) };
    for (var i = 0; i < 16; i++) m.Sliders.Add(new Slider { Pin = 39 });
    for (var i = 0; i < 32; i++)
        m.Buttons.Add(new Button { Pin = 39, LedPair = new LedPair { PinA = 39, PinB = 39, ActiveLow = true } });
    m.I2C0 = new I2cBus { Sda = 39, Scl = 39, ClockHz = 1000000 };
    m.I2C1 = new I2cBus { Sda = 39, Scl = 39, ClockHz = 1000000 };
    m.Udp = new Udp
    {
        Enabled = true, Ssid = new string('a', 32), Password = new string('a', 64),
        PcIp = 0xFFFFFFFFu, PcPort = 65535, LocalPort = 65535,
    };
    m.Screens = new ScreenBehaviour
    {
        Brightness = 255, TransitionMs = 2000, ScrollHoldMs = 10000, ScrollSpeedPxS = 200,
        ScrollEndHoldMs = 10000, DimEnabled = true, DimAfterS = 86400, DimBrightness = 255,
        OffEnabled = true, OffAfterS = 86400, PixelShiftEnabled = true, PixelShiftEveryS = 86400,
        WakeSwallowsPress = true,
    };
    m.Input = new InputTuning { DebounceMs = 200, SliderIntervalMs = 100, FilterWeightPct = 100, SliderDeadband = 500 };
    return m;
}

Check("maximal_size", () => $"{Maximal().CalculateSize()} bytes");
Check("determinism", () => $"equal={Maximal().ToByteArray().AsSpan().SequenceEqual(Maximal().ToByteArray())}");
```

If the build fails on a generated name (for example `I2C0` vs `I2c0`), fix the spike code to the generated name, and record the C# naming in the results doc.

- [ ] **Step 3: Write the EspDotNet spike project**

Create `spikes/EspDotNetSpike/EspDotNetSpike.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- SPIKE ONLY (stage 1): EspDotNet against the real board, on Windows. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="EspDotNet" Version="5.0.2" />
    <PackageReference Include="System.IO.Ports" Version="8.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write the EspDotNet spike commands**

Create `spikes/EspDotNetSpike/Program.cs`:

```csharp
// SPIKE ONLY. Usage (run from the folder holding the firmware parts and parts.txt):
//   EspDotNetSpike probe <COMx>          chip, stub, MD5 of each part's region vs the local file, >=4MB proxy
//   EspDotNetSpike flash <COMx>          NVS MD5 before, write the 4 parts, verify MD5s, NVS MD5 after, reset
//   EspDotNetSpike wipe-nvs <COMx>       write 0xFF over 0x9000..0xDFFF, verify, reset
//   EspDotNetSpike serial-errors <COMx>  open at 115200, reset via RTS, log ErrorReceived/data for 6 s
using System.Diagnostics;
using System.IO.Ports;
using System.Security.Cryptography;
using EspDotNet.Communication;
using EspDotNet.Config;
using EspDotNet.Loaders.SoftLoader;
using EspDotNet.Tools;
using EspDotNet.Tools.Firmware;

const uint NvsOffset = 0x9000, NvsSize = 0x5000;

if (args.Length < 2) { Console.WriteLine("usage: EspDotNetSpike probe|flash|wipe-nvs|serial-errors COMx"); return 2; }
var (cmd, portName) = (args[0], args[1]);
var sw = Stopwatch.StartNew();
void Log(string s) => Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F2}s] {s}");

static List<(uint Offset, string File, byte[] Data)> LoadParts() =>
    File.ReadAllLines("parts.txt").Where(l => l.Trim().Length > 0).Select(l =>
    {
        var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (Convert.ToUInt32(p[0], 16), p[1], File.ReadAllBytes(p[1]));
    }).ToList();

static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

if (cmd == "serial-errors")
{
    using var p = new SerialPort(portName, 115200);
    var bytes = 0; var aa = 0; var text = new System.Text.StringBuilder();
    p.ErrorReceived += (_, e) => Log($"ErrorReceived {e.EventType}");
    p.DataReceived += (_, _) =>
    {
        var buf = new byte[p.BytesToRead]; var n = p.Read(buf, 0, buf.Length);
        bytes += n; aa += buf.Take(n).Count(x => x == 0xAA);
        if (text.Length < 400) text.Append(System.Text.Encoding.ASCII.GetString(buf, 0, n));
    };
    p.Open();
    Log("port open; resetting via RTS (EN), like a reboot");
    p.DtrEnable = false; p.RtsEnable = true; await Task.Delay(100); p.RtsEnable = false;
    await Task.Delay(6000);
    Log($"received {bytes} bytes, {aa} x 0xAA; first text: {text.ToString().Replace("\r", "").Replace("\n", " | ")}");
    return 0;
}

var config = ConfigProvider.LoadDefaultConfig();
using var port = new SerialPort(portName, 115200);
port.Open();
var comm = new Communicator(port);
var ct = CancellationToken.None;

Log("entering bootloader");
var bootloader = await new BootloaderTool(comm, config.BootloaderSequence).StartBootloaderAsync(ct);
var device = await new ChipTypeDetectTool(bootloader, config).DetectAndGetDeviceConfigAsync(ct);
Log($"chip: {device.ChipType}");
var stub = DefaultFirmwareProviders.GetSoftloaderForDevice(device.ChipType);
SoftLoader soft = await new SoftLoaderTool(comm, new RamUploadTool(bootloader, device)).StartAsync(stub, ct);
Log("stub running");

async Task<string> Md5(uint offset, uint size) => Hex(await soft.SpiFlashMd5Async(offset, size, ct));

switch (cmd)
{
    case "probe":
        foreach (var (off, file, data) in LoadParts())
            Log($"{file} @0x{off:X}: flash={await Md5(off, (uint)data.Length)} local={Hex(MD5.HashData(data))}");
        try { Log($">=4MB proxy: MD5(0x3FF000, 0x1000)={await Md5(0x3FF000, 0x1000)}"); }
        catch (Exception e) { Log($">=4MB proxy FAILED: {e.Message}"); }
        break;

    case "flash":
        Log($"NVS before: {await Md5(NvsOffset, NvsSize)}");
        var parts = LoadParts();
        var upload = new FirmwareUploadTool(new FlashUploadDeflatedTool(soft, device))
        {
            Progress = new Progress<float>(p => { if ((int)(p * 100) % 25 == 0) Log($"progress {p:P0}"); }),
        };
        await upload.UploadFirmwareAsync(
            new FirmwareProvider(0, parts.Select(p => (IFirmwareSegmentProvider)new FirmwareSegmentProvider(p.Offset, p.Data))), ct);
        Log("written");
        foreach (var (off, file, data) in parts)
            Log($"verify {file}: {(await Md5(off, (uint)data.Length) == Hex(MD5.HashData(data)) ? "MATCH" : "MISMATCH")}");
        Log($"NVS after: {await Md5(NvsOffset, NvsSize)}");
        break;

    case "wipe-nvs":
        var ff = Enumerable.Repeat((byte)0xFF, (int)NvsSize).ToArray();
        await new FirmwareUploadTool(new FlashUploadDeflatedTool(soft, device)).UploadFirmwareAsync(
            new FirmwareProvider(0, new[] { (IFirmwareSegmentProvider)new FirmwareSegmentProvider(NvsOffset, ff) }), ct);
        Log($"NVS after wipe: {await Md5(NvsOffset, NvsSize)} expected {Hex(MD5.HashData(ff))}");
        break;

    default:
        Log($"unknown command {cmd}");
        return 2;
}

await new ResetDeviceTool(comm, config.ResetSequence).ResetAsync(ct);
Log("reset; done");
return 0;
```

If the build fails on an EspDotNet name or signature, correct the call against the package (the API was read from the `main` source at commit `62619b8`, matching 5.0.2), and record the correction.

- [ ] **Step 5: Write the spike workflow**

Create `.github/workflows/spike.yml`. Replace the four `cp` source paths in the `firmware-parts` job with the exact paths Task 1 Step 7 recorded (the build-dir paths are standard; `boot_app0.bin` comes from the framework).

```yaml
name: Spike (stage 1, temporary)

# SPIKE ONLY: removed before merge. Runs on pushes to the spike branch that touch spikes/.
on:
  push:
    branches: [ worktree-firmware ]
    paths: [ 'spikes/**', '.github/workflows/spike.yml' ]

jobs:
  proto-linux:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v7
    - uses: actions/setup-dotnet@v5
      with:
        dotnet-version: |
          8.0.x
          10.0.x
    - name: Run ProtoSpike
      run: dotnet run --project spikes/ProtoSpike -c Release

  windows:
    runs-on: windows-latest
    steps:
    - uses: actions/checkout@v7
    - uses: actions/setup-dotnet@v5
      with:
        dotnet-version: |
          8.0.x
          10.0.x
    - name: ProtoSpike single-file publish and run
      run: |
        dotnet publish spikes/ProtoSpike -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out/proto
        ./out/proto/ProtoSpike.exe
    - name: EspDotNetSpike single-file publish
      run: dotnet publish spikes/EspDotNetSpike -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out/esp
    - uses: actions/upload-artifact@v7
      with:
        name: spike-espdotnet-win-x64
        path: out/esp/EspDotNetSpike.exe
        retention-days: 7

  firmware-parts:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v7
    - uses: actions/setup-python@v6
      with:
        python-version: '3.12'
    - run: pip install platformio==6.1.18
    - name: Build denky32
      working-directory: firmware
      run: pio run -e denky32
    - name: Collect the four parts
      run: |
        mkdir parts
        cp firmware/.pio/build/denky32/bootloader.bin parts/
        cp firmware/.pio/build/denky32/partitions.bin parts/
        cp "$(find ~/.platformio/packages/framework-arduinoespressif32 -name boot_app0.bin | head -1)" parts/
        cp firmware/.pio/build/denky32/firmware.bin parts/
        printf '0x1000 bootloader.bin\n0x8000 partitions.bin\n0xe000 boot_app0.bin\n0x10000 firmware.bin\n' > parts/parts.txt
        ls -l parts
    - uses: actions/upload-artifact@v7
      with:
        name: spike-firmware-parts
        path: parts/
        retention-days: 7
```

Check the action versions match those already used in `.github/workflows/build.yml` (`actions/checkout@v7`, `actions/setup-dotnet@v5`, `actions/upload-artifact@v7`, and whichever `setup-python` it uses). Align them if they differ.

- [ ] **Step 6: Commit, push, watch**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add spikes/ProtoSpike spikes/EspDotNetSpike .github/workflows/spike.yml
/usr/bin/git commit -m "spike: CI for Grpc.Tools, EspDotNet exe and firmware parts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push
gh run list --branch worktree-firmware --workflow spike.yml --limit 1
gh run watch <id>
```
Expected: all three jobs succeed. If a job fails on compilation, fix the spike code (names and signatures only) and push again. Then:
```bash
gh run view <id> --log | grep "VECTOR"
```
Record every VECTOR line from **both** `proto-linux` and `windows` in the results doc (the C# column and the S3 rows), plus "generation works" for each job, with the run URL.

---

### Task 5: [USER-RUN or here] esptool flash facts (S1, part A)

**Files:**
- Create: `spikes/flash_facts.py`

**Interfaces:**
- Consumes: the `spike-firmware-parts` artifact (Task 4), unzipped into a folder with `parts.txt`.
- Produces: the esptool rows of S1 in the results doc.

**Where it runs.** This container has no serial device. Two options:

- **(a) Pass the board through to this container.** This is an Incus *container*, so a serial device is passed as a hot-pluggable unix device. **On the Ubuntu host** (not here), with the ESP32 plugged in:
  ```bash
  lsusb                                    # note the bridge's ID, e.g. 10c4:ea60 (CP210x) or 1a86:7523 (CH340)
  incus list                               # the instance name (this machine's hostname is "claude")
  incus config device add claude esp32 unix-hotplug vendorid=10c4 productid=ea60 mode=0666
  ```
  - It then shows up here as the same `/dev/ttyUSB0` path as on the host.
  - DTR/RTS (auto-reset) pass through.
  - Remove it later with `incus config device remove claude esp32`.
  - If `unix-hotplug` isn't available on the host's Incus version, use `unix-char source=/dev/ttyUSB0 path=/dev/ttyUSB0 mode=0666` (this needs re-adding after a replug).
- **(b) Run it on your PC,** wherever PlatformIO is installed (Windows: run the same commands in a terminal, with `COMx` as the port).

- [ ] **Step 1: Write the script**

Create `spikes/flash_facts.py`:

```python
"""SPIKE ONLY (stage 1). Facts about writing firmware parts and NVS, using esptool.

Run with PlatformIO's own Python (it has esptool's dependencies and pyserial):
    Linux:   ~/.platformio/penv/bin/python spikes/flash_facts.py --port /dev/ttyUSB0 --parts-dir parts
    Windows: %USERPROFILE%\.platformio\penv\Scripts\python.exe spikes\flash_facts.py --port COM3 --parts-dir parts
Paste the whole output back.

Every esptool call uses --after no_reset, so the app never runs between steps (each call's default
reset re-enters download mode). The final step lets the app boot over a garbage NVS.
"""
import argparse
import hashlib
import os
import subprocess
import sys
import tempfile
import time

NVS_OFFSET, NVS_SIZE = 0x9000, 0x5000
ESPTOOL = os.path.join(os.path.expanduser("~"), ".platformio", "packages", "tool-esptoolpy", "esptool.py")


def esptool(port, *args, after="no_reset"):
    cmd = [sys.executable, ESPTOOL, "--chip", "esp32", "--port", port, "--after", after, *args]
    print("$", " ".join(cmd), flush=True)
    out = subprocess.run(cmd, capture_output=True, text=True)
    print(out.stdout[-1500:], out.stderr[-800:], flush=True)
    if out.returncode != 0:
        raise SystemExit(f"esptool failed ({out.returncode})")
    return out.stdout


def nvs_md5(port, tmp):
    path = os.path.join(tmp, "nvs.bin")
    esptool(port, "read_flash", hex(NVS_OFFSET), hex(NVS_SIZE), path)
    data = open(path, "rb").read()
    return hashlib.md5(data).hexdigest(), data


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", required=True)
    ap.add_argument("--parts-dir", default=".")
    a = ap.parse_args()
    port, pdir = a.port, os.path.abspath(a.parts_dir)
    with open(os.path.join(pdir, "parts.txt")) as fh:
        parts = [(int(o, 16), os.path.join(pdir, f)) for o, f in (line.split() for line in fh if line.strip())]
    with tempfile.TemporaryDirectory() as tmp:
        print("== 1. chip, flash size, MAC")
        esptool(port, "flash_id")

        print("== 2. put a recognisable marker into NVS")
        marker = os.path.join(tmp, "marker.bin")
        open(marker, "wb").write(b"\xA5" * NVS_SIZE)
        esptool(port, "write_flash", hex(NVS_OFFSET), marker)
        before, _ = nvs_md5(port, tmp)
        print("NVS md5 with marker:", before)

        print("== 3. write the four parts separately (as Volco's Update will)")
        args = []
        for off, f in parts:
            args += [hex(off), f]
        esptool(port, "write_flash", *args)
        after, _ = nvs_md5(port, tmp)
        print("NVS md5 after writing parts:", after, "UNCHANGED" if after == before else "CHANGED  <-- FINDING")

        print("== 4. MD5 verify of every part (SPI_FLASH_MD5 via the stub)")
        esptool(port, "verify_flash", *args)

        print("== 5. let the app boot over the garbage NVS, capture 4 s of serial")
        esptool(port, "read_mac", after="hard_reset")
        import serial  # pyserial ships with PlatformIO's Python
        with serial.Serial(port, 115200, timeout=0.2) as s:
            end, buf = time.time() + 4, b""
            while time.time() < end:
                buf += s.read(4096)
        print(f"serial: {len(buf)} bytes, {buf.count(0xAA)} x 0xAA")
        print("serial text:", buf.decode("ascii", "replace")[:600])

        print("== 6. NVS after the app booted")
        booted, data = nvs_md5(port, tmp)
        print("NVS md5 after boot:", booted, "| first 32 bytes:", data[:32].hex())
        print("RECOVERED (rewritten by initArduino)" if booted != before else "NOT REWRITTEN  <-- FINDING")


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Commit and push**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add spikes/flash_facts.py
/usr/bin/git commit -m "spike: esptool flash and NVS facts script" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push
```

- [ ] **Step 3: [USER-RUN] Run it against the board**

Ask the user to, from a checkout of this branch (`git fetch && git checkout worktree-firmware`):
1. Download the parts: `gh run download <id> -n spike-firmware-parts -D parts`.
2. Run:
   - Windows: `%USERPROFILE%\.platformio\penv\Scripts\python.exe spikes\flash_facts.py --port COM3 --parts-dir parts`
   - Linux: `~/.platformio/penv/bin/python spikes/flash_facts.py --port /dev/ttyUSB0 --parts-dir parts`
3. Paste the whole output.

(If the board is passed through, the executor runs the Linux form here.)

Expected:
- `UNCHANGED` after step 3
- all four parts `verify OK` in step 4
- `RECOVERED`, and more than 0 `x 0xAA`, in steps 5–6
- readable ROM boot text

Record each in S1. Afterwards the board runs the freshly built firmware. The user can re-flash their usual build with `pio run -t upload`.

---

### Task 6: [USER-RUN on Windows] EspDotNet facts (S1, part B)

**Files:** none (uses the Task 4 artifacts)

**Interfaces:**
- Consumes: `EspDotNetSpike.exe` and the parts folder.
- Produces: the EspDotNet rows of S1.

- [ ] **Step 1: [USER-RUN] Run the four commands on Windows**

Ask the user to:
1. Put `EspDotNetSpike.exe` (artifact `spike-espdotnet-win-x64`) in the unzipped parts folder.
2. **Close anything holding the COM port** (Volco, the serial monitor).
3. Run, from a terminal in that folder:
   ```
   EspDotNetSpike.exe probe COM3
   EspDotNetSpike.exe flash COM3
   EspDotNetSpike.exe probe COM3
   EspDotNetSpike.exe wipe-nvs COM3
   EspDotNetSpike.exe serial-errors COM3
   ```
4. Paste the whole output.

Expected:
- **probe:** the chip is `ESP32`; the first probe shows mismatches (or matches, if Task 5 just wrote the same build); the `>=4MB proxy` line prints an MD5.
- **flash:** `NVS before` = `NVS after`; all four `MATCH`; timings printed.
- **second probe:** all four `flash` = `local` (including `bootloader.bin`, so there is no header patching).
- **wipe-nvs:** the MD5 matches the expected all-0xFF MD5.
- **serial-errors:** the list of `ErrorReceived` events (possibly none), the byte count, and the boot text.

- [ ] **Step 2: Record**

Fill the EspDotNet rows and the `ErrorReceived` row in S1. Note that `serial-errors` resets through EN (RTS), which approximates `ESP.restart()` (the same ROM boot output). Then commit the results doc:
```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add docs/superpowers/spikes/2026-10-03-controller-config-spikes.md
/usr/bin/git commit -m "docs: stage 1 spike results (flashing)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Decide, amend, clean up, open the stage 1 PR

**Files:**
- Modify: `docs/superpowers/spikes/2026-10-03-controller-config-spikes.md` (the "Decisions and spec amendments" section)
- Modify: `docs/superpowers/specs/2026-10-03-controller-config-design.md` (only amendments the user approves)
- Modify: `firmware/platformio.ini` (remove `[env:spike_layout]`)
- Delete: `spikes/`, `.github/workflows/spike.yml`

**Interfaces:**
- Consumes: every recorded answer.
- Produces: a clean branch with the spec, the plan, the results and the platform pin, ready to merge as stage 1.

- [ ] **Step 1: Compare the vector columns and write the decisions**

For each Vectors row, compare nanopb and C# with the spec's expectation. Under "Decisions and spec amendments", write one line per finding that changes something. Examples of the kind of line expected:
- "C# does not reject invalid UTF-8 → `ControllerConfigCodec.Decode` adds an explicit UTF-8 check (spec §6.1)."
- "nanopb accepts an embedded NUL → `volco_config::decode` adds the NUL check (already in spec §6.1)."
- "Absent submessages are / aren't default-filled by nanopb → `toRuntime` must / needn't apply defaults itself."
- "The working nanopb keys are: …"
- "Flasher: EspDotNet 5.0.2 is sufficient / needs <command> added."
- "Flash-size check: proxy works / needs a flash-id command."
- "ErrorReceived during reboot: <events> → the `SerialConnection` rule in §8.1 is right / must also ignore <event>."

Also re-check spec §7 against the findings: the PR #64 note and the platform pin value.

- [ ] **Step 2: Get the user's approval for the amendments**

Show the user the decisions list. Apply **only** the amendments they approve to the spec, each as a small edit in the section named.

- [ ] **Step 3: Remove all spike code**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git rm -r -q spikes .github/workflows/spike.yml
```
In `firmware/platformio.ini`, delete the `[env:spike_layout]` block (its comment line and three settings). Then verify:
```bash
cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio run -e denky32 && ~/.platformio/penv/bin/pio test -e native
cd /root/dev/volco/.claude/worktrees/firmware && grep -rn "spike" firmware/platformio.ini .github/workflows/ ; echo "grep exit $?"
```
Expected: `SUCCESS`, tests `PASSED`, and the grep finds nothing (`grep exit 1`).

- [ ] **Step 4: Commit, push, check CI**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add -A firmware/platformio.ini docs
/usr/bin/git commit -m "chore: remove stage 1 spike code; record decisions" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push
gh run list --branch worktree-firmware --limit 2
```
Expected: `build.yml` is green (tests only).

- [ ] **Step 5: Open the stage 1 PR (ask the user first)**

After the user confirms, run:
```bash
gh pr create --base main --head worktree-firmware --title "Controller config: spec, stage 1 spikes, firmware platform pin" --body-file <file written with the Write tool>
```
The body says:
- what is in the PR: the spec, the stage 1 plan, the spike results, and the exact platform and library pins
- that no app or firmware behaviour changes, except that CI now builds against Arduino core 3.3.8, the same as the dev machine
- what stage 2 is next

It ends with the attribution lines from the session's system reminder.

# Controller Config, Stage 2: Schema and Codecs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the controller config schema and every piece that reads, checks and receives it, on both sides: the proto, nanopb decoding, validation, the CRC, the chunked receiver (firmware), the Google.Protobuf codec, validation and presets (C#), shared golden vectors, and the firmware id script. **Nothing calls any of it yet** (spec §15 stage 2).

**Architecture:**
- One `proto/volco_config.proto` (proto2) generates code for both sides at build time: nanopb in the PlatformIO `native` env, and Grpc.Tools in `Volco.Core`.
- A Python generator writes `firmware/test/golden/config.txt`: blobs plus the expected decode results, validation triples and CRCs.
- Both test suites read that file, so the C++ and C# implementations can't drift apart.
- Firmware code is a new pure-C++ library, `firmware/lib/volco_config`. C# code is a new folder, `src/Volco.Core/Controller/`.

**Tech Stack:**
- Firmware: C++17, nanopb 0.4.9.2 (PlatformIO `nanopb/Nanopb@0.4.92`), Unity, PlatformIO 6.1.19
- App: .NET 8, Google.Protobuf 3.36.2, Grpc.Tools 2.84.0, xUnit 2
- Generator: Python 3 with `protobuf` 7.36.2 and `grpcio-tools` 1.84.0 (both already in PlatformIO's penv)

**Spec:** `docs/superpowers/specs/2026-10-03-controller-config-design.md` (§5.4 receiver rules, §6.1 schema and limits, §6.2 presets, §6.4 validation, §6.5 CRC, §9.2 firmware id, §12.1–§12.3 tests, §15 stage 2). Stage 1 results: `docs/superpowers/spikes/2026-10-03-controller-config-spikes.md`.

## Global Constraints

- **Never run `dotnet` locally** (CLAUDE.md). C# builds and tests run in GitHub Actions: push the branch and watch `Build Volco` with `gh run list --branch controller-config-stage2 --limit 3` and `gh run watch <id>`. Never trigger the build/publish job.
- **Use `/usr/bin/git`.** Plain `git` is refused in this worktree session. Write file contents with the Write tool. Keep shell commands simple: no `$VAR` command names and no heredocs that contain `git`.
- **Branch:** `controller-config-stage2`, stacked on `worktree-firmware` (PR #67). Its PR targets `main` after #67 merges.
- **Firmware test command:** `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native`. It needs host `g++` (installed).
- **Python for the generator:** `~/.platformio/penv/bin/python` (it has `protobuf` and `grpcio-tools`).
- **The proto content is spec §6.1, with one ruling:** `option csharp_namespace = "Volco.Core.Controller.Proto";`, and the C# folder and namespace are `Volco.Core/Controller/`, not `ControllerConfig/`. Under `Volco.Core.ControllerConfig`, the simple name `ControllerConfig` would resolve to the namespace instead of the generated message type. Ledger this as a ruling in Task 1.
- **Exact limits (spec §6.1):** sliders ≤ 16; buttons ≤ 32; `preset` ≤ 24 bytes; `udp.ssid` ≤ 32 bytes; `udp.password` ≤ 64 bytes; blob ≤ 1024 bytes (`kMaxConfigBlob`). A NUL in any string, or invalid UTF-8, is a decode failure on both sides.
- **Result codes (spec §5.4):** OK 0, BAD_SEQUENCE 1, BAD_CRC 2, TOO_LARGE 3, DECODE_FAILED 4, INVALID 5, STORAGE_FAILED 6. **Field codes (§6.4):** 1 slider pin, 2 button pin, 3 LED pin, 4 LED pair pin A, 5 LED pair pin B, 6 screen bus, 7 screen address, 8 I2C bus missing, 9 I2C bus pins, 10 I2C clock, 11 UDP ssid, 12 UDP password, 13 UDP pc_ip, 14 UDP port, 15 screen behaviour value, 16 input tuning value. "No item/field" is `0xFF`.
- **CRC (§6.5):** zlib CRC-32 (reflected 0xEDB88320, init and final XOR 0xFFFFFFFF). `crc("123456789") = 0xCBF43926`; `crc("") = 0`.
- **Receiver constants (§5.4):** chunk data 1–240 bytes; timeout 2000 ms; `total` must be 1–1024.
- **Every firmware library is pure C++** (no Arduino). Time is passed in.

## Review Focus

1. **The golden file isn't found, so the loops run zero cases and pass.** Every golden test asserts a minimum case count per kind (Task 3 C++, Task 6 C#).
2. **Huge pin numbers** (`pin = 64`, `pin = 4294967295`) cause undefined behaviour in a shift. The chip helpers must return false for any pin ≥ 64. Vectors `slider_pin_64` and `slider_pin_max` (Task 2) pin this down.
3. **UTF-8 SSIDs measured in characters instead of bytes.** 16 × "é" (32 bytes) is valid; 17 × "é" (34 bytes) fails decoding on both sides. Vectors `ssid_multibyte_32` (validate OK) and `ssid_multibyte_34` (decode-fail) (Task 2).
4. **The proto edited without regenerating `config.txt`.** A CI step re-runs the generator and fails on a diff (Task 10).
5. **Receiver frames after a transfer has ended** (a duplicate End, chunks after an error, chunks from the other transport): no second result, and no refreshed timeout. Tests `test_at_most_one_result_per_begin` and `test_other_transport_ignored_and_does_not_refresh` (Task 5).

---

## File Structure

| Path | Responsibility |
|---|---|
| `proto/volco_config.proto`, `proto/volco_config.options` | The schema (spec §6.1) and nanopb limits |
| `proto/presets/diy-reference.json` | DIY reference hardware (spec §6.2) |
| `firmware/platformio.ini` (`[env:native]`) | nanopb dependency and generation |
| `firmware/tools/gen_config_vectors.py` | Generates the shared golden vectors |
| `firmware/test/golden/config.txt` | Generated vectors (committed) |
| `firmware/test/golden_config.h` | C++ loader for `config.txt` |
| `firmware/lib/volco_config/src/volco_crc32.{h,cpp}` | CRC-32 |
| `firmware/lib/volco_config/src/volco_chip.{h,cpp}` | `ChipInfo` and the ESP32 table |
| `firmware/lib/volco_config/src/volco_config.{h,cpp}` | Result and field codes, `hasNulInStrings`, `decodeConfig`, `validateConfig` |
| `firmware/lib/volco_config/src/volco_blob_receiver.{h,cpp}` | Chunked write receiver (spec §5.4 rules 1–7) |
| `firmware/test/test_config/test_main.cpp` | Codec, chip, CRC and validation tests (goldens plus units) |
| `firmware/test/test_config_receiver/test_main.cpp` | Receiver tests |
| `src/Volco.Core/Volco.Core.csproj` | Protobuf packages, generation, embedded presets |
| `src/Volco.Core/Controller/ConfigCodes.cs` | `ConfigResultCode`, `ConfigField`, `ValidationResult` |
| `src/Volco.Core/Controller/ControllerConfigCodec.cs` | Encode, decode with limits, CRC, format exception |
| `src/Volco.Core/Controller/ControllerConfigExtensions.cs` | Effective submessages (absent → defaults) |
| `src/Volco.Core/Controller/ChipInfo.cs` | ESP32 pin table |
| `src/Volco.Core/Controller/ControllerConfigValidator.cs` | Spec §6.4 |
| `src/Volco.Core/Controller/ControllerPresets.cs` | Preset list and `ApplyTo` |
| `tests/Volco.Core.Tests/Controller/ConfigGoldenTests.cs` | C# side of `config.txt` |
| `tests/Volco.Core.Tests/Controller/ControllerConfigCodecTests.cs` | Codec unit tests |
| `tests/Volco.Core.Tests/Controller/ControllerConfigValidatorTests.cs` | Validator unit tests |
| `tests/Volco.Core.Tests/Controller/ControllerPresetsTests.cs` | Preset tests |
| `firmware/scripts/firmware_id.sh`, `firmware/scripts/check_firmware_id_paths.py` | Firmware build id (§9.2) and its path check |
| `.github/workflows/build.yml` | Firmware id check and vector-staleness check |

---

### Task 1: Schema, preset, and nanopb generation in the native env

**Files:**
- Create: `proto/volco_config.proto`, `proto/volco_config.options`, `proto/presets/diy-reference.json`
- Modify: `firmware/platformio.ini` (`[env:native]`)
- Create: `firmware/test/test_config/test_main.cpp` (a smoke test, extended in Tasks 3–4)

**Interfaces:**
- Produces:
  - nanopb types `volco_config_ControllerConfig`, `volco_config_Button` (tags `volco_config_Button_led_tag`, `_led_pair_tag`, `_screen_tag`), `volco_config_I2cBus`, `volco_config_Udp`, `volco_config_ScreenBehaviour`, `volco_config_InputTuning`, `_init_default`, `_fields`, from header `volco_config.pb.h`
  - C# namespace `Volco.Core.Controller.Proto` (Task 6)

- [ ] **Step 1: Write the failing smoke test**

Create `firmware/test/test_config/test_main.cpp`:

```cpp
#include <pb_decode.h>
#include <unity.h>

#include "volco_config.pb.h"

void setUp() {}
void tearDown() {}

void test_generated_defaults() {
  volco_config_ControllerConfig c = volco_config_ControllerConfig_init_default;
  TEST_ASSERT_EQUAL_UINT(255, c.screens.brightness);
  TEST_ASSERT_EQUAL_UINT(12345, c.udp.pc_port);
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_generated_defaults);
  return UNITY_END();
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native -f test_config`
Expected: a compile error, `volco_config.pb.h: No such file or directory` (no proto and no generation yet).

- [ ] **Step 3: Write the proto (spec §6.1, with the C# namespace ruling)**

Create `proto/volco_config.proto`:

```proto
syntax = "proto2";
package volco.config;
option csharp_namespace = "Volco.Core.Controller.Proto";

// The whole controller configuration. One blob in NVS key "volco"/"cfg".
// Field numbers are never reused. An absent field takes its default.
message ControllerConfig {
  repeated Slider sliders = 1;     // max 16. Order = slider input number.
  repeated Button buttons = 2;     // max 32. Order = button input number.
  optional I2cBus i2c0 = 3;        // required when a screen uses bus 0
  optional I2cBus i2c1 = 4;        // required when a screen uses bus 1
  optional Udp udp = 5;            // absent = UDP off
  optional ScreenBehaviour screens = 6;
  optional InputTuning input = 7;
  optional string preset = 8;      // max 24 bytes; informational, e.g. "diy-reference"
}

message Slider {
  optional uint32 pin = 1;         // ADC-capable GPIO
}

message Button {
  optional uint32 pin = 1;         // input GPIO, INPUT_PULLUP, pressed = LOW
  oneof indicator {                // none set = no indicator
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
  optional uint32 pin_a = 1;       // lit for value 1
  optional uint32 pin_b = 2;       // lit for value 2
  optional bool active_low = 3 [default = false];
}

message Screen {
  optional uint32 bus = 1 [default = 0];        // 0 or 1
  optional uint32 address = 2 [default = 0x3C]; // 0x3C or 0x3D
}

message I2cBus {
  optional uint32 sda = 1;
  optional uint32 scl = 2;
  optional uint32 clock_hz = 3 [default = 400000];   // 100000..1000000
}

message Udp {
  optional bool enabled = 1 [default = false];
  optional string ssid = 2;                         // 1..32 bytes when enabled
  optional string password = 3;                     // 0 (open network) or 8..64 bytes
  optional fixed32 pc_ip = 4;                       // IPv4, a.b.c.d = a<<24|b<<16|c<<8|d
  optional uint32 pc_port = 5 [default = 12345];    // 1..65535
  optional uint32 local_port = 6 [default = 12346]; // 1..65535
}

message ScreenBehaviour {
  optional uint32 brightness = 1 [default = 255];            // 0..255
  optional uint32 transition_ms = 2 [default = 360];         // 0..2000
  optional uint32 scroll_hold_ms = 3 [default = 2000];       // 0..10000
  optional uint32 scroll_speed_px_s = 4 [default = 30];      // 5..200
  optional uint32 scroll_end_hold_ms = 5 [default = 1200];   // 0..10000
  optional bool dim_enabled = 6 [default = true];
  optional uint32 dim_after_s = 7 [default = 120];           // 10..86400
  optional uint32 dim_brightness = 8 [default = 8];          // 0..255
  optional bool off_enabled = 9 [default = true];
  optional uint32 off_after_s = 10 [default = 1800];         // 10..86400
  optional bool pixel_shift_enabled = 11 [default = true];
  optional uint32 pixel_shift_every_s = 12 [default = 180];  // 10..86400
  optional bool wake_swallows_press = 13 [default = true];
}

message InputTuning {
  optional uint32 debounce_ms = 1 [default = 30];          // 1..200
  optional uint32 slider_interval_ms = 2 [default = 10];   // 1..100
  optional uint32 filter_weight_pct = 3 [default = 20];    // 1..100 (EMA weight, %)
  optional uint32 slider_deadband = 4 [default = 25];      // 0..500 (ADC units)
}
```

Create `proto/volco_config.options`:

```
volco.config.ControllerConfig.sliders  max_count:16
volco.config.ControllerConfig.buttons  max_count:32
volco.config.ControllerConfig.preset   max_size:25
volco.config.Udp.ssid                  max_size:33
volco.config.Udp.password              max_size:65
```

Create `proto/presets/diy-reference.json` (spec §6.2, verbatim):

```json
{
  "preset": "diy-reference",
  "sliders": [ {"pin": 33}, {"pin": 35}, {"pin": 32}, {"pin": 36}, {"pin": 34} ],
  "buttons": [
    {"pin": 4,  "led": {"pin": 12, "activeLow": true}},
    {"pin": 14, "led": {"pin": 21, "activeLow": true}},
    {"pin": 5,  "ledPair": {"pinA": 18, "pinB": 19, "activeLow": false}}
  ],
  "i2c0": {"sda": 25, "scl": 26},
  "i2c1": {"sda": 27, "scl": 13}
}
```

- [ ] **Step 4: Wire nanopb into the native env**

In `firmware/platformio.ini`, replace the `[env:native]` block with:

```ini
; Host-side unit tests of the pure libraries in lib/ (protocol, bidi, screen state, render, config).
; Run with: pio test -e native
[env:native]
platform = native
lib_deps =
    nanopb/Nanopb@0.4.92
; Generated from the repo's proto/ (spec §7); nanopb picks up the .options file next to it.
custom_nanopb_protos = +<../proto/volco_config.proto>
custom_nanopb_options = --error-on-unmatched
build_flags =
    -std=gnu++17
    -Wall
    -DVOLCO_NATIVE
    -DPB_VALIDATE_UTF8
test_build_src = no
```

- [ ] **Step 5: Run the smoke test and the whole native suite**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native`
Expected: `[nanopb] Processing '../proto/volco_config.proto' (volco_config.options)`, `test_config` PASSED (1 test), and the four existing suites still PASSED.

- [ ] **Step 6: Commit and ledger the namespace ruling**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add proto firmware/platformio.ini firmware/test/test_config
/usr/bin/git commit -m "feat(config): controller config proto, DIY preset, nanopb generation in native" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
Ledger: `Task 1: Ruling: csharp_namespace Volco.Core.Controller.Proto and C# folder Controller/ (spec said ControllerConfig) — under Volco.Core.ControllerConfig the name ControllerConfig binds to the namespace, not the message type — cost if wrong: a rename`.

---

### Task 2: Golden vector generator and `config.txt`

**Files:**
- Create: `firmware/tools/gen_config_vectors.py`
- Create (generated): `firmware/test/golden/config.txt`

**Interfaces:**
- Produces: `config.txt`, one case per line: `<kind> <name> <hex or -> <expectations…>`.
  - `decode-ok <name> <hex> key=value …`. Keys: `size`, `sliders`, `buttons`, `preset`, `screens.brightness`, `screens.dim_enabled` (0/1), `screens.transition_ms`, `buttons.0.kind` (0 none, 1 led, 2 led_pair, 3 screen), `buttons.0.screen.bus`, `buttons.0.screen.address`. Values are decimal; `preset=` may be empty.
  - `decode-fail <name> <hex>`
  - `validate <name> <hex> <code> <item> <field>`, decimal; a valid config is `0 255 255`
  - `crc <name> <hex> <8 lowercase hex digits>`
  - Lines starting with `#` are comments.

- [ ] **Step 1: Write the generator**

Create `firmware/tools/gen_config_vectors.py`:

```python
#!/usr/bin/env python3
"""Generates firmware/test/golden/config.txt: controller config blobs and what both codecs must
make of them (spec §12.1). Read by firmware/test/test_config and tests/Volco.Core.Tests.

The expected decode values, validation triples (code item field) and CRCs written here are the
spec's oracle (§6.1, §6.4, §6.5), stated by hand per case; only the blob bytes are computed.

Run with PlatformIO's Python (it has protobuf and grpcio-tools):
    ~/.platformio/penv/bin/python firmware/tools/gen_config_vectors.py
Commit the output. CI re-runs this and fails if config.txt is stale.
"""
import importlib
import os
import sys
import tempfile
import zlib

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PROTO_DIR = os.path.join(ROOT, "proto")
OUT = os.path.join(ROOT, "firmware", "test", "golden", "config.txt")


def load_pb2():
    from grpc_tools import protoc

    tmp = tempfile.mkdtemp()
    rc = protoc.main(["protoc", f"-I{PROTO_DIR}", f"--python_out={tmp}", os.path.join(PROTO_DIR, "volco_config.proto")])
    if rc != 0:
        raise SystemExit("protoc failed")
    sys.path.insert(0, tmp)
    return importlib.import_module("volco_config_pb2")


pb = load_pb2()
lines = []
OK = (0, 255, 255)


def ip(a, b, c, d):
    return (a << 24) | (b << 16) | (c << 8) | d


def blob(m):
    return m.SerializeToString(deterministic=True)


def hexs(data):
    return data.hex() if data else "-"


def diy():
    m = pb.ControllerConfig(preset="diy-reference")
    for p in (33, 35, 32, 36, 34):
        m.sliders.add(pin=p)
    b = m.buttons.add(pin=4)
    b.led.pin = 12
    b.led.active_low = True
    b = m.buttons.add(pin=14)
    b.led.pin = 21
    b.led.active_low = True
    b = m.buttons.add(pin=5)
    b.led_pair.pin_a = 18
    b.led_pair.pin_b = 19
    b.led_pair.active_low = False
    m.i2c0.sda, m.i2c0.scl = 25, 26
    m.i2c1.sda, m.i2c1.scl = 27, 13
    return m


def udp_ok(m):
    m.udp.enabled = True
    m.udp.ssid = "net"
    m.udp.password = ""
    m.udp.pc_ip = ip(192, 168, 1, 10)


def screen(m, i, bus, address):
    s = m.buttons[i].screen
    s.bus = bus
    s.address = address


def maximal():
    m = pb.ControllerConfig(preset="p" * 24)
    for _ in range(16):
        m.sliders.add(pin=39)
    for _ in range(32):
        b = m.buttons.add(pin=39)
        b.led_pair.pin_a, b.led_pair.pin_b, b.led_pair.active_low = 39, 39, True
    for bus in (m.i2c0, m.i2c1):
        bus.sda, bus.scl, bus.clock_hz = 39, 39, 1000000
    u = m.udp
    u.enabled, u.ssid, u.password = True, "a" * 32, "a" * 64
    u.pc_ip, u.pc_port, u.local_port = 0xFFFFFFFF, 65535, 65535
    s = m.screens
    s.brightness, s.transition_ms, s.scroll_hold_ms, s.scroll_speed_px_s = 255, 2000, 10000, 200
    s.scroll_end_hold_ms, s.dim_enabled, s.dim_after_s, s.dim_brightness = 10000, True, 86400, 255
    s.off_enabled, s.off_after_s, s.pixel_shift_enabled, s.pixel_shift_every_s = True, 86400, True, 86400
    s.wake_swallows_press = True
    i = m.input
    i.debounce_ms, i.slider_interval_ms, i.filter_weight_pct, i.slider_deadband = 200, 100, 100, 500
    return m


def decode_ok(name, data, **expect):
    kv = " ".join(f"{k.replace('__', '.')}={v}" for k, v in expect.items())
    lines.append(f"decode-ok {name} {hexs(data)} {kv}")


def decode_fail(name, hex_bytes):
    lines.append(f"decode-fail {name} {hex_bytes}")


def validate(name, m, triple):
    lines.append(f"validate {name} {hexs(blob(m))} {triple[0]} {triple[1]} {triple[2]}")


def case(name, mutate, triple):
    m = diy()
    mutate(m)
    validate(name, m, triple)


def bad(item, field):
    return (5, item, field)


# ---- decode-ok -------------------------------------------------------------------------------
lines.append("# decode-ok <name> <hex> key=value ...")
decode_ok("diy_reference", blob(diy()), sliders=5, buttons=3, preset="diy-reference",
          buttons__0__kind=1, screens__brightness=255)
decode_ok("empty", b"", size=0, sliders=0, buttons=0, preset="", screens__brightness=255,
          screens__dim_enabled=1, screens__transition_ms=360)
decode_ok("screen_default", bytes.fromhex("12022200"), buttons=1, buttons__0__kind=3,
          buttons__0__screen__bus=0, buttons__0__screen__address=60)
decode_ok("brightness_only", bytes.fromhex("32020864"), screens__brightness=100,
          screens__dim_enabled=1, screens__transition_ms=360)
decode_ok("unknown_field", bytes.fromhex("98060132020864"), screens__brightness=100)
mx = blob(maximal())
assert len(mx) == 663, len(mx)  # stage 1 measured 663 bytes in nanopb and C#
decode_ok("maximal", mx, size=663, sliders=16, buttons=32)

# ---- decode-fail -----------------------------------------------------------------------------
lines.append("# decode-fail <name> <hex>")
decode_fail("truncated", "1202")
decode_fail("bad_varint", "0a0c08ffffffffffffffffffff01")
decode_fail("ssid_33", "2a231221" + "61" * 33)
decode_fail("ssid_multibyte_34", "2a241222" + "c3a9" * 17)
decode_fail("buttons_33", "1200" * 33)
decode_fail("sliders_17", "0a00" * 17)
decode_fail("nul_in_preset", "4203610062")
decode_fail("nul_in_ssid", "2a051203610062")
decode_fail("bad_utf8", "4202c328")

# ---- validate --------------------------------------------------------------------------------
lines.append("# validate <name> <hex> <code> <item> <field>")
case("valid_diy", lambda m: None, OK)
case("valid_two_screens", lambda m: (screen(m, 0, 0, 0x3C), screen(m, 1, 0, 0x3D)), OK)

case("slider_missing_pin", lambda m: m.sliders[2].ClearField("pin"), bad(2, 1))
case("slider_reserved", lambda m: setattr(m.sliders[0], "pin", 6), bad(0, 1))
case("slider_nonexistent", lambda m: setattr(m.sliders[0], "pin", 24), bad(0, 1))
case("slider_pin_64", lambda m: setattr(m.sliders[0], "pin", 64), bad(0, 1))
case("slider_pin_max", lambda m: setattr(m.sliders[0], "pin", 4294967295), bad(0, 1))
case("slider_not_adc", lambda m: setattr(m.sliders[1], "pin", 23), bad(1, 1))
case("slider_adc2_with_udp", lambda m: (setattr(m.sliders[0], "pin", 25), udp_ok(m)), bad(0, 1))
case("slider_adc2_without_udp", lambda m: setattr(m.sliders[0], "pin", 25), OK)
case("dup_two_sliders", lambda m: setattr(m.sliders[1], "pin", 33), bad(1, 1))

case("button_missing_pin", lambda m: m.buttons[1].ClearField("pin"), bad(1, 2))
case("button_reserved", lambda m: setattr(m.buttons[0], "pin", 1), bad(0, 2))
case("button_nonexistent", lambda m: setattr(m.buttons[0], "pin", 20), bad(0, 2))
case("button_input_only_ok", lambda m: setattr(m.buttons[0], "pin", 39), OK)
case("led_missing_pin", lambda m: m.buttons[0].led.ClearField("pin"), bad(0, 3))
case("led_input_only", lambda m: setattr(m.buttons[0].led, "pin", 39), bad(0, 3))
case("led_reserved", lambda m: setattr(m.buttons[0].led, "pin", 10), bad(0, 3))
case("pair_a_missing", lambda m: m.buttons[2].led_pair.ClearField("pin_a"), bad(2, 4))
case("pair_b_reserved", lambda m: setattr(m.buttons[2].led_pair, "pin_b", 7), bad(2, 5))
case("pair_b_equals_a", lambda m: setattr(m.buttons[2].led_pair, "pin_b", 18), bad(2, 5))
case("screen_bad_bus", lambda m: screen(m, 0, 2, 0x3C), bad(0, 6))
case("screen_bad_address", lambda m: screen(m, 0, 0, 0x3E), bad(0, 7))
case("screen_duplicate", lambda m: (screen(m, 0, 0, 0x3C), screen(m, 1, 0, 0x3C)), bad(1, 7))
case("screen_third_on_bus",
     lambda m: (screen(m, 0, 0, 0x3C), screen(m, 1, 0, 0x3D), screen(m, 2, 0, 0x3C)), bad(2, 7))

case("i2c_missing_bus", lambda m: (screen(m, 0, 1, 0x3C), m.ClearField("i2c1")), bad(1, 8))
case("i2c_missing_scl", lambda m: (screen(m, 0, 0, 0x3C), m.i2c0.ClearField("scl")), bad(0, 9))
case("i2c_sda_eq_scl", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "scl", 25)), bad(0, 9))
case("i2c_input_only", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "sda", 39)), bad(0, 9))
case("i2c_clock_low", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "clock_hz", 99999)), bad(0, 10))
case("i2c_clock_high", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "clock_hz", 1000001)), bad(0, 10))
case("i2c_unused_bus_ignored", lambda m: setattr(m.i2c1, "sda", 1), OK)

case("dup_slider_button", lambda m: setattr(m.buttons[0], "pin", 33), bad(0, 2))
case("dup_button_led", lambda m: setattr(m.buttons[1].led, "pin", 4), bad(1, 3))
case("dup_led_i2c", lambda m: (screen(m, 0, 0, 0x3C), setattr(m.buttons[1].led, "pin", 25)), bad(0, 9))
case("dup_unused_bus_ok", lambda m: setattr(m.buttons[0], "pin", 27), OK)

case("udp_ok", udp_ok, OK)
case("udp_ssid_empty", lambda m: (udp_ok(m), setattr(m.udp, "ssid", "")), bad(255, 11))
case("ssid_multibyte_32", lambda m: (udp_ok(m), setattr(m.udp, "ssid", "é" * 16)), OK)
case("udp_password_7", lambda m: (udp_ok(m), setattr(m.udp, "password", "1234567")), bad(255, 12))
case("udp_password_8_ok", lambda m: (udp_ok(m), setattr(m.udp, "password", "12345678")), OK)
case("udp_password_64_ok", lambda m: (udp_ok(m), setattr(m.udp, "password", "p" * 64)), OK)
case("udp_ip_missing", lambda m: (udp_ok(m), m.udp.ClearField("pc_ip")), bad(255, 13))
case("udp_ip_zero", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", 0)), bad(255, 13))
case("udp_ip_broadcast", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", 0xFFFFFFFF)), bad(255, 13))
case("udp_ip_loopback", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", ip(127, 0, 0, 1))), bad(255, 13))
case("udp_ip_multicast", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", ip(224, 0, 0, 1))), bad(255, 13))
case("udp_ip_multicast_top", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", ip(239, 255, 255, 250))), bad(255, 13))
case("udp_ip_240_ok", lambda m: (udp_ok(m), setattr(m.udp, "pc_ip", ip(240, 0, 0, 1))), OK)
case("udp_pc_port_zero", lambda m: (udp_ok(m), setattr(m.udp, "pc_port", 0)), bad(255, 14))
case("udp_local_port_big", lambda m: (udp_ok(m), setattr(m.udp, "local_port", 65536)), bad(255, 14))
case("udp_disabled_incomplete_ok", lambda m: (setattr(m.udp, "enabled", False), setattr(m.udp, "ssid", "")), OK)

SCREEN_RANGES = [("brightness", 1, 0, 255), ("transition_ms", 2, 0, 2000), ("scroll_hold_ms", 3, 0, 10000),
                 ("scroll_speed_px_s", 4, 5, 200), ("scroll_end_hold_ms", 5, 0, 10000),
                 ("dim_after_s", 7, 10, 86400), ("dim_brightness", 8, 0, 255), ("off_after_s", 10, 10, 86400),
                 ("pixel_shift_every_s", 12, 10, 86400)]
INPUT_RANGES = [("debounce_ms", 1, 1, 200), ("slider_interval_ms", 2, 1, 100), ("filter_weight_pct", 3, 1, 100),
                ("slider_deadband", 4, 0, 500)]
for sub, field_code, table in (("screens", 15, SCREEN_RANGES), ("input", 16, INPUT_RANGES)):
    for fname, num, lo, hi in table:
        if lo > 0:
            case(f"range_{sub}_{fname}_below", lambda m, s=sub, f=fname, v=lo - 1: setattr(getattr(m, s), f, v),
                 bad(num, field_code))
        case(f"range_{sub}_{fname}_above", lambda m, s=sub, f=fname, v=hi + 1: setattr(getattr(m, s), f, v),
             bad(num, field_code))
for which in ("min", "max"):
    def all_at(m, which=which):
        for sub, table in (("screens", SCREEN_RANGES), ("input", INPUT_RANGES)):
            for fname, _num, lo, hi in table:
                setattr(getattr(m, sub), fname, lo if which == "min" else hi)
    case(f"range_all_{which}_ok", all_at, OK)

case("order_slider_before_udp",
     lambda m: (setattr(m.sliders[0], "pin", 6), udp_ok(m), setattr(m.udp, "ssid", "")), bad(0, 1))
case("order_button_pin_before_led",
     lambda m: (setattr(m.buttons[0], "pin", 1), setattr(m.buttons[0].led, "pin", 39)), bad(0, 2))
case("order_sliders_before_buttons",
     lambda m: (setattr(m.buttons[0], "pin", 6), setattr(m.sliders[4], "pin", 23)), bad(4, 1))
case("order_dup_before_range",
     lambda m: (setattr(m.buttons[0], "pin", 33), setattr(m.screens, "brightness", 256)), bad(0, 2))
case("order_i2c_before_dup",
     lambda m: (screen(m, 0, 0, 0x3C), setattr(m.i2c0, "clock_hz", 1), setattr(m.buttons[1], "pin", 33)),
     bad(0, 10))
case("order_screens_before_input",
     lambda m: (setattr(m.screens, "transition_ms", 2001), setattr(m.input, "debounce_ms", 0)), bad(2, 15))
case("order_range_field_order",
     lambda m: (setattr(m.screens, "dim_brightness", 256), setattr(m.screens, "brightness", 256)), bad(1, 15))

# ---- crc -------------------------------------------------------------------------------------
lines.append("# crc <name> <hex> <crc32 hex>")
for name, data in (("digits", b"123456789"), ("empty", b""), ("diy_reference", blob(diy()))):
    lines.append(f"crc {name} {hexs(data)} {zlib.crc32(data) & 0xFFFFFFFF:08x}")
assert "crc digits 313233343536373839 cbf43926" in lines

header = [
    "# Generated by firmware/tools/gen_config_vectors.py; do not edit. Shared by",
    "# firmware/test/test_config and tests/Volco.Core.Tests/Controller (spec §12.1).",
]
with open(OUT, "w", newline="\n") as fh:
    fh.write("\n".join(header + lines) + "\n")
print(f"wrote {OUT}: {sum(1 for l in lines if not l.startswith('#'))} cases")
```

- [ ] **Step 2: Run it**

Run: `cd /root/dev/volco/.claude/worktrees/firmware && ~/.platformio/penv/bin/python firmware/tools/gen_config_vectors.py`
Expected: `wrote …/config.txt: N cases`, with N ≥ 100. Then `grep -c "^validate" firmware/test/golden/config.txt` gives ≥ 85, and `grep "^crc digits" firmware/test/golden/config.txt` ends with `cbf43926`. Running it a second time must leave the file unchanged: `/usr/bin/git diff --stat` after the second run shows no change to `config.txt` (stage the file before the second run).

- [ ] **Step 3: Commit**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/tools/gen_config_vectors.py firmware/test/golden/config.txt
/usr/bin/git commit -m "test(config): shared golden config vectors and their generator" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Firmware CRC, chip table and decode

**Files:**
- Create: `firmware/test/golden_config.h`
- Create: `firmware/lib/volco_config/src/volco_crc32.h`, `volco_crc32.cpp`, `volco_chip.h`, `volco_chip.cpp`, `volco_config.h`, `volco_config.cpp`
- Modify: `firmware/test/test_config/test_main.cpp`

**Interfaces:**
- Consumes: `volco_config.pb.h` (Task 1) and `config.txt` (Task 2).
- Produces, in `namespace volco`:
  - `uint32_t crc32(const uint8_t* data, size_t len)`
  - `struct ChipInfo { uint64_t exists, reserved, inputOnly, adc1, adc2; bool usable(uint32_t) const; bool outputCapable(uint32_t) const; bool adcCapable(uint32_t) const; bool isAdc2(uint32_t) const; }`
  - `const ChipInfo& esp32Chip()`
  - `enum ConfigResultCode : uint8_t { CFG_OK=0, CFG_BAD_SEQUENCE=1, CFG_BAD_CRC=2, CFG_TOO_LARGE=3, CFG_DECODE_FAILED=4, CFG_INVALID=5, CFG_STORAGE_FAILED=6 }`
  - `enum ConfigField : uint8_t { FIELD_SLIDER_PIN=1, FIELD_BUTTON_PIN=2, FIELD_LED_PIN=3, FIELD_PAIR_PIN_A=4, FIELD_PAIR_PIN_B=5, FIELD_SCREEN_BUS=6, FIELD_SCREEN_ADDRESS=7, FIELD_I2C_MISSING=8, FIELD_I2C_PINS=9, FIELD_I2C_CLOCK=10, FIELD_UDP_SSID=11, FIELD_UDP_PASSWORD=12, FIELD_UDP_PC_IP=13, FIELD_UDP_PORT=14, FIELD_SCREEN_VALUE=15, FIELD_INPUT_VALUE=16 }`
  - `constexpr uint8_t kNoItem = 0xFF`
  - `constexpr size_t kMaxConfigBlob = 1024`
  - `struct ValidationResult { uint8_t code, item, field; }`
  - `bool hasNulInStrings(const uint8_t*, size_t)`
  - `bool decodeConfig(const uint8_t*, size_t, volco_config_ControllerConfig&)`
  - `ValidationResult validateConfig(const volco_config_ControllerConfig&, const ChipInfo&)` (declared here, implemented in Task 4)

- [ ] **Step 1: Write the golden loader**

Create `firmware/test/golden_config.h`:

```cpp
#pragma once
// Loads firmware/test/golden/config.txt (shared with the C# tests; generated by
// firmware/tools/gen_config_vectors.py) for native tests.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

struct ConfigCase {
  std::string kind, name;
  std::vector<uint8_t> blob;
  std::vector<std::string> rest;  // expectations after the hex
};

inline std::vector<ConfigCase> loadConfigCases() {
  std::string here = __FILE__;
  std::string dir = here.substr(0, here.find_last_of("/\\") + 1);
  FILE* f = fopen((dir + "golden/config.txt").c_str(), "r");
  if (!f) f = fopen("test/golden/config.txt", "r");
  std::vector<ConfigCase> out;
  if (!f) return out;
  static char line[8192];
  while (fgets(line, sizeof line, f)) {
    if (line[0] == '#' || line[0] == '\n' || line[0] == '\r') continue;
    ConfigCase c;
    char* tok = strtok(line, " \t\r\n");
    if (!tok) continue;
    c.kind = tok;
    c.name = strtok(nullptr, " \t\r\n");
    std::string hex = strtok(nullptr, " \t\r\n");
    if (hex != "-")
      for (size_t i = 0; i + 1 < hex.size(); i += 2) c.blob.push_back((uint8_t)strtoul(hex.substr(i, 2).c_str(), nullptr, 16));
    while ((tok = strtok(nullptr, " \t\r\n"))) c.rest.push_back(tok);
    out.push_back(c);
  }
  fclose(f);
  return out;
}

inline std::vector<ConfigCase> configCases(const char* kind) {
  static std::vector<ConfigCase> all = loadConfigCases();
  std::vector<ConfigCase> out;
  for (auto& c : all) if (c.kind == kind) out.push_back(c);
  return out;
}
```

- [ ] **Step 2: Write the failing tests**

Replace `firmware/test/test_config/test_main.cpp` with:

```cpp
#include <string.h>
#include <unity.h>

#include <string>

#include "../golden_config.h"
#include "volco_chip.h"
#include "volco_config.h"
#include "volco_crc32.h"

using namespace volco;

void setUp() {}
void tearDown() {}

static std::string value(const volco_config_ControllerConfig& c, size_t size, const std::string& key) {
  if (key == "size") return std::to_string(size);
  if (key == "sliders") return std::to_string(c.sliders_count);
  if (key == "buttons") return std::to_string(c.buttons_count);
  if (key == "preset") return c.preset;
  if (key == "screens.brightness") return std::to_string(c.screens.brightness);
  if (key == "screens.dim_enabled") return std::to_string(c.screens.dim_enabled ? 1 : 0);
  if (key == "screens.transition_ms") return std::to_string(c.screens.transition_ms);
  if (c.buttons_count > 0) {
    const volco_config_Button& b = c.buttons[0];
    if (key == "buttons.0.kind") {
      int k = b.which_indicator == volco_config_Button_led_tag        ? 1
              : b.which_indicator == volco_config_Button_led_pair_tag ? 2
              : b.which_indicator == volco_config_Button_screen_tag   ? 3
                                                                      : 0;
      return std::to_string(k);
    }
    if (key == "buttons.0.screen.bus") return std::to_string(b.indicator.screen.bus);
    if (key == "buttons.0.screen.address") return std::to_string(b.indicator.screen.address);
  }
  return "?" + key;
}

void test_golden_file_loaded() {
  TEST_ASSERT_GREATER_OR_EQUAL(6, (int)configCases("decode-ok").size());
  TEST_ASSERT_GREATER_OR_EQUAL(9, (int)configCases("decode-fail").size());
  TEST_ASSERT_GREATER_OR_EQUAL(85, (int)configCases("validate").size());
  TEST_ASSERT_GREATER_OR_EQUAL(3, (int)configCases("crc").size());
}

void test_crc_cases() {
  for (auto& c : configCases("crc")) {
    char got[9];
    snprintf(got, sizeof got, "%08x", (unsigned)crc32(c.blob.data(), c.blob.size()));
    TEST_ASSERT_EQUAL_STRING_MESSAGE(c.rest[0].c_str(), got, c.name.c_str());
  }
}

void test_decode_ok_cases() {
  for (auto& c : configCases("decode-ok")) {
    volco_config_ControllerConfig cfg;
    TEST_ASSERT_TRUE_MESSAGE(decodeConfig(c.blob.data(), c.blob.size(), cfg), c.name.c_str());
    for (auto& kv : c.rest) {
      size_t eq = kv.find('=');
      std::string key = kv.substr(0, eq), want = kv.substr(eq + 1);
      std::string msg = c.name + " " + key;
      TEST_ASSERT_EQUAL_STRING_MESSAGE(want.c_str(), value(cfg, c.blob.size(), key).c_str(), msg.c_str());
    }
  }
}

void test_decode_fail_cases() {
  for (auto& c : configCases("decode-fail")) {
    volco_config_ControllerConfig cfg;
    TEST_ASSERT_FALSE_MESSAGE(decodeConfig(c.blob.data(), c.blob.size(), cfg), c.name.c_str());
  }
}

void test_decode_rejects_oversized_blob() {
  static uint8_t big[kMaxConfigBlob + 1] = {0};
  volco_config_ControllerConfig cfg;
  TEST_ASSERT_FALSE(decodeConfig(big, sizeof big, cfg));
}

void test_nul_prescan() {
  const uint8_t top[] = {0x42, 0x03, 0x61, 0x00, 0x62};              // preset "a\0b"
  const uint8_t nested[] = {0x2A, 0x05, 0x12, 0x03, 0x61, 0x00, 0x62}; // udp.ssid "a\0b"
  const uint8_t clean[] = {0x42, 0x02, 0x61, 0x62, 0x32, 0x02, 0x08, 0x64};
  const uint8_t truncated[] = {0x42, 0x09, 0x61};                     // malformed: left to pb_decode
  TEST_ASSERT_TRUE(hasNulInStrings(top, sizeof top));
  TEST_ASSERT_TRUE(hasNulInStrings(nested, sizeof nested));
  TEST_ASSERT_FALSE(hasNulInStrings(clean, sizeof clean));
  TEST_ASSERT_FALSE(hasNulInStrings(truncated, sizeof truncated));
}

void test_chip_table() {
  const ChipInfo& c = esp32Chip();
  TEST_ASSERT_TRUE(c.usable(0));
  TEST_ASSERT_FALSE(c.usable(1));      // UART0 TX (USB serial)
  TEST_ASSERT_FALSE(c.usable(6));      // SPI flash
  TEST_ASSERT_FALSE(c.usable(20));     // doesn't exist
  TEST_ASSERT_FALSE(c.usable(64));     // out of range, no UB
  TEST_ASSERT_FALSE(c.usable(4294967295u));
  TEST_ASSERT_TRUE(c.usable(39));
  TEST_ASSERT_FALSE(c.outputCapable(34));  // input-only
  TEST_ASSERT_TRUE(c.outputCapable(33));
  TEST_ASSERT_TRUE(c.adcCapable(36));      // ADC1
  TEST_ASSERT_TRUE(c.adcCapable(25));      // ADC2
  TEST_ASSERT_FALSE(c.adcCapable(23));
  TEST_ASSERT_TRUE(c.isAdc2(25));
  TEST_ASSERT_FALSE(c.isAdc2(33));
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_golden_file_loaded);
  RUN_TEST(test_crc_cases);
  RUN_TEST(test_decode_ok_cases);
  RUN_TEST(test_decode_fail_cases);
  RUN_TEST(test_decode_rejects_oversized_blob);
  RUN_TEST(test_nul_prescan);
  RUN_TEST(test_chip_table);
  return UNITY_END();
}
```

- [ ] **Step 3: Run them and watch them fail**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native -f test_config`
Expected: a compile error, `volco_chip.h: No such file or directory`.

- [ ] **Step 4: Implement CRC, chip table and decode**

Create `firmware/lib/volco_config/src/volco_crc32.h`:

```cpp
#pragma once
// CRC-32 of a config blob (spec §6.5): zlib's crc32 (reflected 0xEDB88320, init/final 0xFFFFFFFF).
#include <stddef.h>
#include <stdint.h>

namespace volco {
uint32_t crc32(const uint8_t* data, size_t len);
}  // namespace volco
```

Create `firmware/lib/volco_config/src/volco_crc32.cpp`:

```cpp
#include "volco_crc32.h"

namespace volco {

uint32_t crc32(const uint8_t* data, size_t len) {
  uint32_t c = 0xFFFFFFFFu;
  for (size_t i = 0; i < len; i++) {
    c ^= data[i];
    for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (0u - (c & 1u)));
  }
  return ~c;
}

}  // namespace volco
```

Create `firmware/lib/volco_config/src/volco_chip.h`:

```cpp
#pragma once
// What each GPIO of a chip family can do (spec §6.4): facts about the chip, not user preferences.
#include <stdint.h>

namespace volco {

struct ChipInfo {
  uint64_t exists, reserved, inputOnly, adc1, adc2;  // bit n = GPIO n

  static bool has(uint64_t mask, uint32_t pin) { return pin < 64 && ((mask >> pin) & 1u); }
  bool usable(uint32_t pin) const { return has(exists, pin) && !has(reserved, pin); }
  bool outputCapable(uint32_t pin) const { return usable(pin) && !has(inputOnly, pin); }
  bool adcCapable(uint32_t pin) const { return usable(pin) && (has(adc1, pin) || has(adc2, pin)); }
  bool isAdc2(uint32_t pin) const { return has(adc2, pin); }
};

// Classic ESP32: exists 0-19, 21-23, 25-27, 32-39; reserved 1, 3 (USB serial) and 6-11 (flash);
// input-only 34-39; ADC1 32-39; ADC2 0, 2, 4, 12-15, 25-27.
const ChipInfo& esp32Chip();

}  // namespace volco
```

Create `firmware/lib/volco_config/src/volco_chip.cpp`:

```cpp
#include "volco_chip.h"

namespace volco {
namespace {
constexpr uint64_t bit(int p) { return 1ull << p; }
constexpr uint64_t span(int a, int b) {
  uint64_t m = 0;
  for (int p = a; p <= b; p++) m |= bit(p);
  return m;
}
constexpr ChipInfo kEsp32{
    span(0, 19) | span(21, 23) | span(25, 27) | span(32, 39),  // exists
    bit(1) | bit(3) | span(6, 11),                              // reserved
    span(34, 39),                                               // input-only
    span(32, 39),                                               // ADC1
    bit(0) | bit(2) | bit(4) | span(12, 15) | span(25, 27),     // ADC2
};
}  // namespace

const ChipInfo& esp32Chip() { return kEsp32; }

}  // namespace volco
```

Create `firmware/lib/volco_config/src/volco_config.h`:

```cpp
#pragma once
// The controller config (spec §6): decoding with the limits both sides enforce, and validation
// against the chip. Pure C++ on top of the nanopb code generated from proto/volco_config.proto.
#include <stddef.h>
#include <stdint.h>

#include "volco_chip.h"
#include "volco_config.pb.h"

namespace volco {

constexpr size_t kMaxConfigBlob = 1024;
constexpr uint8_t kNoItem = 0xFF;

// ConfigResult codes (spec §5.4).
enum ConfigResultCode : uint8_t {
  CFG_OK = 0, CFG_BAD_SEQUENCE = 1, CFG_BAD_CRC = 2, CFG_TOO_LARGE = 3,
  CFG_DECODE_FAILED = 4, CFG_INVALID = 5, CFG_STORAGE_FAILED = 6,
};

// Field codes of an INVALID result (spec §6.4).
enum ConfigField : uint8_t {
  FIELD_SLIDER_PIN = 1, FIELD_BUTTON_PIN = 2, FIELD_LED_PIN = 3, FIELD_PAIR_PIN_A = 4,
  FIELD_PAIR_PIN_B = 5, FIELD_SCREEN_BUS = 6, FIELD_SCREEN_ADDRESS = 7, FIELD_I2C_MISSING = 8,
  FIELD_I2C_PINS = 9, FIELD_I2C_CLOCK = 10, FIELD_UDP_SSID = 11, FIELD_UDP_PASSWORD = 12,
  FIELD_UDP_PC_IP = 13, FIELD_UDP_PORT = 14, FIELD_SCREEN_VALUE = 15, FIELD_INPUT_VALUE = 16,
};

struct ValidationResult {
  uint8_t code;   // CFG_OK or CFG_INVALID
  uint8_t item;   // slider/button index, I2C bus, range field number, or kNoItem
  uint8_t field;  // ConfigField, or kNoItem
};

// True when a string field (preset, udp.ssid, udp.password) holds a NUL byte. Works on the wire
// bytes: nanopb copies strings into zeroed char arrays, so "a\0" and "a" look the same once
// decoded. Malformed input returns false and is left to pb_decode to reject.
bool hasNulInStrings(const uint8_t* data, size_t len);

// Decodes a blob, applying the proto2 defaults (absent submessages included). False on anything
// the spec calls a decode failure: over kMaxConfigBlob, malformed, a count or string over its
// limit, invalid UTF-8, or a NUL in a string.
bool decodeConfig(const uint8_t* data, size_t len, volco_config_ControllerConfig& out);

// The first violation of spec §6.4, in its exact order, or {CFG_OK, kNoItem, kNoItem}.
ValidationResult validateConfig(const volco_config_ControllerConfig& config, const ChipInfo& chip);

}  // namespace volco
```

Create `firmware/lib/volco_config/src/volco_config.cpp` (Task 4 adds `validateConfig` to this file):

```cpp
#include "volco_config.h"

#include <pb_decode.h>
#include <string.h>

namespace volco {
namespace {

bool readVarint(const uint8_t*& p, const uint8_t* end, uint64_t& v) {
  v = 0;
  for (int shift = 0; shift < 64; shift += 7) {
    if (p >= end) return false;
    uint8_t b = *p++;
    v |= (uint64_t)(b & 0x7F) << shift;
    if (!(b & 0x80)) return true;
  }
  return false;
}

// Walks one message's fields. Top level: field 8 (preset) is a string and field 5 (udp) a nested
// message whose fields 2 (ssid) and 3 (password) are strings. Returns false on a NUL in a string.
bool stringsClean(const uint8_t* p, const uint8_t* end, bool inUdp) {
  while (p < end) {
    uint64_t key;
    if (!readVarint(p, end, key)) return true;
    uint32_t field = (uint32_t)(key >> 3), wireType = (uint32_t)(key & 7);
    if (wireType == 0) {
      uint64_t ignored;
      if (!readVarint(p, end, ignored)) return true;
    } else if (wireType == 1) {
      if (end - p < 8) return true;
      p += 8;
    } else if (wireType == 5) {
      if (end - p < 4) return true;
      p += 4;
    } else if (wireType == 2) {
      uint64_t n;
      if (!readVarint(p, end, n) || n > (uint64_t)(end - p)) return true;
      const uint8_t* s = p;
      p += n;
      bool isString = inUdp ? (field == 2 || field == 3) : field == 8;
      if (isString && memchr(s, 0, (size_t)n)) return false;
      if (!inUdp && field == 5 && !stringsClean(s, s + n, true)) return false;
    } else {
      return true;
    }
  }
  return true;
}

}  // namespace

bool hasNulInStrings(const uint8_t* data, size_t len) {
  return len > 0 && !stringsClean(data, data + len, false);
}

bool decodeConfig(const uint8_t* data, size_t len, volco_config_ControllerConfig& out) {
  out = volco_config_ControllerConfig_init_default;
  if (len > kMaxConfigBlob) return false;
  if (hasNulInStrings(data, len)) return false;
  pb_istream_t stream = pb_istream_from_buffer(data, len);
  return pb_decode(&stream, volco_config_ControllerConfig_fields, &out);
}

}  // namespace volco
```

`validateConfig` has no definition yet, but the Task 3 tests don't call it, so the link succeeds.

- [ ] **Step 5: Run the tests**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native -f test_config`
Expected: 7 tests PASSED.

**If the library can't find `volco_config.pb.h`** (nanopb's generated include path not visible to library builds), add to `[env:native]` `build_flags` the line `-I.pio/build/native/nanopb/generated-src`, re-run, and ledger it as a ruling (stage 3 needs the same line for `esp32`).

- [ ] **Step 6: Run the whole native suite and commit**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native`
Expected: every suite PASSED.

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/lib/volco_config firmware/test/golden_config.h firmware/test/test_config firmware/platformio.ini
/usr/bin/git commit -m "feat(config): firmware CRC-32, ESP32 chip table and config decoding" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Firmware validation (spec §6.4)

**Files:**
- Modify: `firmware/lib/volco_config/src/volco_config.cpp` (add `validateConfig`)
- Modify: `firmware/test/test_config/test_main.cpp` (add the validate tests)

**Interfaces:**
- Consumes: Task 3 declarations.
- Produces: `validateConfig`, whose triples match `config.txt` exactly.

- [ ] **Step 1: Write the failing tests**

In `firmware/test/test_config/test_main.cpp`, add before `main`:

```cpp
void test_validate_cases() {
  for (auto& c : configCases("validate")) {
    volco_config_ControllerConfig cfg;
    TEST_ASSERT_TRUE_MESSAGE(decodeConfig(c.blob.data(), c.blob.size(), cfg), c.name.c_str());
    ValidationResult r = validateConfig(cfg, esp32Chip());
    char got[32], want[32];
    snprintf(got, sizeof got, "%u %u %u", r.code, r.item, r.field);
    snprintf(want, sizeof want, "%s %s %s", c.rest[0].c_str(), c.rest[1].c_str(), c.rest[2].c_str());
    TEST_ASSERT_EQUAL_STRING_MESSAGE(want, got, c.name.c_str());
  }
}

void test_validate_counts_guard() {
  volco_config_ControllerConfig cfg = volco_config_ControllerConfig_init_default;
  cfg.sliders_count = 17;  // impossible after decode; the step exists for parity with C#
  ValidationResult r = validateConfig(cfg, esp32Chip());
  TEST_ASSERT_EQUAL_UINT(CFG_INVALID, r.code);
  TEST_ASSERT_EQUAL_UINT(kNoItem, r.item);
  TEST_ASSERT_EQUAL_UINT(FIELD_SLIDER_PIN, r.field);
}
```

and add `RUN_TEST(test_validate_cases);` and `RUN_TEST(test_validate_counts_guard);` to `main`.

- [ ] **Step 2: Run them and watch them fail**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native -f test_config`
Expected: a link error, an undefined reference to `volco::validateConfig`.

- [ ] **Step 3: Implement `validateConfig`**

Append to `firmware/lib/volco_config/src/volco_config.cpp`, inside `namespace volco` (after `decodeConfig`):

```cpp
namespace {

ValidationResult invalid(uint8_t item, uint8_t field) { return {CFG_INVALID, item, field}; }
bool inRange(uint32_t v, uint32_t lo, uint32_t hi) { return v >= lo && v <= hi; }

struct PinWalk {  // spec §6.4 step 5: the first pin already seen, reported at its later use
  uint64_t seen = 0;
  bool repeat(uint32_t pin) {
    uint64_t b = 1ull << pin;  // every pin here already passed a usable() check, so pin < 64
    bool r = (seen & b) != 0;
    seen |= b;
    return r;
  }
};

bool badIp(uint32_t ip) {
  return ip == 0 || ip == 0xFFFFFFFFu || (ip >> 24) == 127 || (ip >> 28) == 0xE;
}

}  // namespace

ValidationResult validateConfig(const volco_config_ControllerConfig& c, const ChipInfo& chip) {
  // 1. Counts (the decoder already enforces max_count; kept for parity with the C# path).
  if (c.sliders_count > 16) return invalid(kNoItem, FIELD_SLIDER_PIN);
  if (c.buttons_count > 32) return invalid(kNoItem, FIELD_BUTTON_PIN);
  const bool udp = c.has_udp && c.udp.enabled;

  // 2. Sliders.
  for (pb_size_t i = 0; i < c.sliders_count; i++) {
    const volco_config_Slider& s = c.sliders[i];
    if (!s.has_pin || !chip.adcCapable(s.pin)) return invalid((uint8_t)i, FIELD_SLIDER_PIN);
    if (udp && chip.isAdc2(s.pin)) return invalid((uint8_t)i, FIELD_SLIDER_PIN);
  }

  // 3. Buttons: pin, indicator fields, then the (bus, address) duplicate check, one button at a time.
  for (pb_size_t i = 0; i < c.buttons_count; i++) {
    const volco_config_Button& b = c.buttons[i];
    const uint8_t item = (uint8_t)i;
    if (!b.has_pin || !chip.usable(b.pin)) return invalid(item, FIELD_BUTTON_PIN);
    if (b.which_indicator == volco_config_Button_led_tag) {
      const volco_config_Led& l = b.indicator.led;
      if (!l.has_pin || !chip.outputCapable(l.pin)) return invalid(item, FIELD_LED_PIN);
    } else if (b.which_indicator == volco_config_Button_led_pair_tag) {
      const volco_config_LedPair& p = b.indicator.led_pair;
      if (!p.has_pin_a || !chip.outputCapable(p.pin_a)) return invalid(item, FIELD_PAIR_PIN_A);
      if (!p.has_pin_b || !chip.outputCapable(p.pin_b) || p.pin_b == p.pin_a) return invalid(item, FIELD_PAIR_PIN_B);
    } else if (b.which_indicator == volco_config_Button_screen_tag) {
      const volco_config_Screen& s = b.indicator.screen;
      if (s.bus > 1) return invalid(item, FIELD_SCREEN_BUS);
      if (s.address != 0x3C && s.address != 0x3D) return invalid(item, FIELD_SCREEN_ADDRESS);
      for (pb_size_t j = 0; j < i; j++) {
        const volco_config_Button& o = c.buttons[j];
        if (o.which_indicator == volco_config_Button_screen_tag && o.indicator.screen.bus == s.bus &&
            o.indicator.screen.address == s.address)
          return invalid(item, FIELD_SCREEN_ADDRESS);
      }
    }
  }

  // 4. I2C, for each bus some screen uses (an unused I2cBus is ignored entirely).
  bool used[2] = {false, false};
  for (pb_size_t i = 0; i < c.buttons_count; i++)
    if (c.buttons[i].which_indicator == volco_config_Button_screen_tag) used[c.buttons[i].indicator.screen.bus] = true;
  for (uint8_t bus = 0; bus < 2; bus++) {
    if (!used[bus]) continue;
    const bool present = bus == 0 ? c.has_i2c0 : c.has_i2c1;
    const volco_config_I2cBus& i2c = bus == 0 ? c.i2c0 : c.i2c1;
    if (!present) return invalid(bus, FIELD_I2C_MISSING);
    if (!i2c.has_sda || !i2c.has_scl || !chip.outputCapable(i2c.sda) || !chip.outputCapable(i2c.scl) ||
        i2c.sda == i2c.scl)
      return invalid(bus, FIELD_I2C_PINS);
    if (!inRange(i2c.clock_hz, 100000, 1000000)) return invalid(bus, FIELD_I2C_CLOCK);
  }

  // 5. Pin uniqueness, in walk order: sliders, buttons with their indicator pins, used I2C buses.
  PinWalk walk;
  for (pb_size_t i = 0; i < c.sliders_count; i++)
    if (walk.repeat(c.sliders[i].pin)) return invalid((uint8_t)i, FIELD_SLIDER_PIN);
  for (pb_size_t i = 0; i < c.buttons_count; i++) {
    const volco_config_Button& b = c.buttons[i];
    const uint8_t item = (uint8_t)i;
    if (walk.repeat(b.pin)) return invalid(item, FIELD_BUTTON_PIN);
    if (b.which_indicator == volco_config_Button_led_tag) {
      if (walk.repeat(b.indicator.led.pin)) return invalid(item, FIELD_LED_PIN);
    } else if (b.which_indicator == volco_config_Button_led_pair_tag) {
      if (walk.repeat(b.indicator.led_pair.pin_a)) return invalid(item, FIELD_PAIR_PIN_A);
      if (walk.repeat(b.indicator.led_pair.pin_b)) return invalid(item, FIELD_PAIR_PIN_B);
    }
  }
  for (uint8_t bus = 0; bus < 2; bus++) {
    if (!used[bus]) continue;
    const volco_config_I2cBus& i2c = bus == 0 ? c.i2c0 : c.i2c1;
    if (walk.repeat(i2c.sda) || walk.repeat(i2c.scl)) return invalid(bus, FIELD_I2C_PINS);
  }

  // 6. UDP, only when enabled.
  if (udp) {
    const size_t ssid = strlen(c.udp.ssid), password = strlen(c.udp.password);
    if (ssid < 1 || ssid > 32) return invalid(kNoItem, FIELD_UDP_SSID);
    if (password != 0 && (password < 8 || password > 64)) return invalid(kNoItem, FIELD_UDP_PASSWORD);
    if (!c.udp.has_pc_ip || badIp(c.udp.pc_ip)) return invalid(kNoItem, FIELD_UDP_PC_IP);
    if (!inRange(c.udp.pc_port, 1, 65535) || !inRange(c.udp.local_port, 1, 65535))
      return invalid(kNoItem, FIELD_UDP_PORT);
  }

  // 7. Ranges, in field-number order, screens before input (absent submessages hold defaults).
  const volco_config_ScreenBehaviour& s = c.screens;
  if (!inRange(s.brightness, 0, 255)) return invalid(1, FIELD_SCREEN_VALUE);
  if (!inRange(s.transition_ms, 0, 2000)) return invalid(2, FIELD_SCREEN_VALUE);
  if (!inRange(s.scroll_hold_ms, 0, 10000)) return invalid(3, FIELD_SCREEN_VALUE);
  if (!inRange(s.scroll_speed_px_s, 5, 200)) return invalid(4, FIELD_SCREEN_VALUE);
  if (!inRange(s.scroll_end_hold_ms, 0, 10000)) return invalid(5, FIELD_SCREEN_VALUE);
  if (!inRange(s.dim_after_s, 10, 86400)) return invalid(7, FIELD_SCREEN_VALUE);
  if (!inRange(s.dim_brightness, 0, 255)) return invalid(8, FIELD_SCREEN_VALUE);
  if (!inRange(s.off_after_s, 10, 86400)) return invalid(10, FIELD_SCREEN_VALUE);
  if (!inRange(s.pixel_shift_every_s, 10, 86400)) return invalid(12, FIELD_SCREEN_VALUE);
  const volco_config_InputTuning& in = c.input;
  if (!inRange(in.debounce_ms, 1, 200)) return invalid(1, FIELD_INPUT_VALUE);
  if (!inRange(in.slider_interval_ms, 1, 100)) return invalid(2, FIELD_INPUT_VALUE);
  if (!inRange(in.filter_weight_pct, 1, 100)) return invalid(3, FIELD_INPUT_VALUE);
  if (!inRange(in.slider_deadband, 0, 500)) return invalid(4, FIELD_INPUT_VALUE);

  return {CFG_OK, kNoItem, kNoItem};
}
```

- [ ] **Step 4: Run the tests**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native -f test_config`
Expected: 9 tests PASSED. If a golden case fails, the message names it. Compare the implementation and the generator's triple with spec §6.4. **The spec decides.** Fix whichever side disagrees with the spec, and ledger any case where the generator was wrong.

- [ ] **Step 5: Run the whole native suite and commit**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native`
Expected: every suite PASSED.

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/lib/volco_config firmware/test/test_config
/usr/bin/git commit -m "feat(config): firmware config validation (spec 6.4)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
Ledger: `Task 4: Ruling: count-step triples are (5, 0xFF, 1) for sliders and (5, 0xFF, 2) for buttons — spec §6.4 step 1 names no item/field — cost if wrong: two constants on each side`.

---

### Task 5: Firmware chunked receiver (spec §5.4)

**Files:**
- Create: `firmware/lib/volco_config/src/volco_blob_receiver.h`, `volco_blob_receiver.cpp`
- Create: `firmware/test/test_config_receiver/test_main.cpp`

**Interfaces:**
- Consumes: `crc32`, `kMaxConfigBlob`, `ConfigResultCode` (Task 3).
- Produces (stage 4 wires it to frames):
  ```cpp
  namespace volco {
  constexpr uint32_t kTransferTimeoutMs = 2000;
  constexpr size_t kMaxChunk = 240;
  struct ReceiverEvent { enum Kind : uint8_t { NONE, REPLY, COMPLETE }; Kind kind; uint8_t transport, seq, code; };
  class BlobReceiver {
   public:
    ReceiverEvent onBegin(uint8_t transport, uint8_t seq, uint16_t total, uint32_t crc, uint32_t now);
    ReceiverEvent onChunk(uint8_t transport, uint8_t seq, uint16_t offset, const uint8_t* data, size_t len, uint32_t now);
    ReceiverEvent onEnd(uint8_t transport, uint8_t seq, uint32_t now);
    void tick(uint32_t now);
    bool isOpen() const;
    const uint8_t* data() const;  // the received blob, valid after COMPLETE
    uint16_t size() const;
  };
  }
  ```
  A `REPLY` means: send ConfigResult(`seq`, `code`) on `transport`; the transfer is closed. A `COMPLETE` means: the bytes and CRC are good, the transfer is closed, and the caller now decodes, validates and stores, then sends **exactly one** ConfigResult. This preserves "at most one result per Begin".

- [ ] **Step 1: Write the failing tests**

Create `firmware/test/test_config_receiver/test_main.cpp`:

```cpp
#include <string.h>
#include <unity.h>

#include <vector>

#include "volco_blob_receiver.h"
#include "volco_config.h"
#include "volco_crc32.h"

using namespace volco;
using K = ReceiverEvent::Kind;

void setUp() {}
void tearDown() {}

static std::vector<uint8_t> blobOf(size_t n) {
  std::vector<uint8_t> b(n);
  for (size_t i = 0; i < n; i++) b[i] = (uint8_t)(i * 7 + 1);
  return b;
}

// Sends begin + all chunks (240 at a time) on transport t, seq s; returns the End's event.
static ReceiverEvent sendAll(BlobReceiver& r, const std::vector<uint8_t>& b, uint8_t t, uint8_t s, uint32_t now) {
  TEST_ASSERT_EQUAL(K::NONE, r.onBegin(t, s, (uint16_t)b.size(), crc32(b.data(), b.size()), now).kind);
  for (size_t off = 0; off < b.size(); off += kMaxChunk) {
    size_t n = b.size() - off < kMaxChunk ? b.size() - off : kMaxChunk;
    TEST_ASSERT_EQUAL(K::NONE, r.onChunk(t, s, (uint16_t)off, b.data() + off, n, now).kind);
  }
  return r.onEnd(t, s, now);
}

void test_happy_one_chunk() {
  BlobReceiver r;
  auto b = blobOf(100);
  ReceiverEvent e = sendAll(r, b, 0, 7, 0);
  TEST_ASSERT_EQUAL(K::COMPLETE, e.kind);
  TEST_ASSERT_EQUAL_UINT(7, e.seq);
  TEST_ASSERT_EQUAL_UINT(100, r.size());
  TEST_ASSERT_EQUAL_MEMORY(b.data(), r.data(), 100);
  TEST_ASSERT_FALSE(r.isOpen());
}

void test_happy_five_chunks() {
  BlobReceiver r;
  auto b = blobOf(kMaxConfigBlob);  // 4 x 240 + 64
  TEST_ASSERT_EQUAL(K::COMPLETE, sendAll(r, b, 1, 2, 0).kind);
  TEST_ASSERT_EQUAL_MEMORY(b.data(), r.data(), kMaxConfigBlob);
}

void test_too_large_and_zero() {
  BlobReceiver r;
  ReceiverEvent e = r.onBegin(0, 3, kMaxConfigBlob + 1, 0, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_TOO_LARGE, e.code);
  TEST_ASSERT_EQUAL_UINT(3, e.seq);
  TEST_ASSERT_FALSE(r.isOpen());
  e = r.onBegin(0, 4, 0, 0, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_TOO_LARGE, e.code);
}

void test_gap_is_bad_sequence() {
  BlobReceiver r;
  auto b = blobOf(500);
  r.onBegin(0, 1, 500, crc32(b.data(), 500), 0);
  ReceiverEvent e = r.onChunk(0, 1, 240, b.data() + 240, 240, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, e.code);
  TEST_ASSERT_FALSE(r.isOpen());
}

void test_wrong_seq_chunk_is_bad_sequence() {
  BlobReceiver r;
  auto b = blobOf(10);
  r.onBegin(0, 1, 10, crc32(b.data(), 10), 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onChunk(0, 2, 0, b.data(), 10, 0).code);
}

void test_overrun_is_bad_sequence() {
  BlobReceiver r;
  auto b = blobOf(120);
  r.onBegin(0, 1, 100, 0, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onChunk(0, 1, 0, b.data(), 120, 0).code);
}

void test_bad_chunk_lengths() {
  BlobReceiver r;
  auto b = blobOf(300);
  r.onBegin(0, 1, 300, 0, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onChunk(0, 1, 0, b.data(), 0, 0).code);
  r.onBegin(0, 2, 300, 0, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onChunk(0, 2, 0, b.data(), kMaxChunk + 1, 0).code);
}

void test_end_early_and_wrong_seq() {
  BlobReceiver r;
  auto b = blobOf(300);
  r.onBegin(0, 1, 300, crc32(b.data(), 300), 0);
  r.onChunk(0, 1, 0, b.data(), 240, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onEnd(0, 1, 0).code);
  r.onBegin(0, 2, 10, crc32(b.data(), 10), 0);
  r.onChunk(0, 2, 0, b.data(), 10, 0);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_SEQUENCE, r.onEnd(0, 9, 0).code);
}

void test_bad_crc() {
  BlobReceiver r;
  auto b = blobOf(50);
  r.onBegin(0, 1, 50, crc32(b.data(), 50) ^ 1u, 0);
  r.onChunk(0, 1, 0, b.data(), 50, 0);
  ReceiverEvent e = r.onEnd(0, 1, 0);
  TEST_ASSERT_EQUAL(K::REPLY, e.kind);
  TEST_ASSERT_EQUAL_UINT(CFG_BAD_CRC, e.code);
}

void test_no_transfer_is_silent() {
  BlobReceiver r;
  auto b = blobOf(10);
  TEST_ASSERT_EQUAL(K::NONE, r.onChunk(0, 1, 0, b.data(), 10, 0).kind);
  TEST_ASSERT_EQUAL(K::NONE, r.onEnd(0, 1, 0).kind);
}

void test_other_transport_ignored_and_does_not_refresh() {
  BlobReceiver r;
  auto b = blobOf(10);
  r.onBegin(0, 1, 10, crc32(b.data(), 10), 0);
  TEST_ASSERT_EQUAL(K::NONE, r.onChunk(1, 1, 0, b.data(), 10, 1500).kind);  // other transport
  TEST_ASSERT_EQUAL(K::NONE, r.onEnd(1, 1, 1600).kind);
  TEST_ASSERT_TRUE(r.isOpen());
  r.tick(2000);  // 2000 ms after the last accepted frame (the Begin at 0)
  TEST_ASSERT_FALSE(r.isOpen());
}

void test_begin_mid_transfer_restarts() {
  BlobReceiver r;
  auto a = blobOf(300), b = blobOf(20);
  r.onBegin(0, 1, 300, crc32(a.data(), 300), 0);
  r.onChunk(0, 1, 0, a.data(), 240, 0);
  TEST_ASSERT_EQUAL(K::COMPLETE, sendAll(r, b, 0, 2, 10).kind);
  TEST_ASSERT_EQUAL_UINT(20, r.size());
}

void test_timeout_closes_silently() {
  BlobReceiver r;
  auto b = blobOf(10);
  r.onBegin(0, 1, 10, crc32(b.data(), 10), 100);
  r.tick(2099);
  TEST_ASSERT_TRUE(r.isOpen());
  r.tick(2100);
  TEST_ASSERT_FALSE(r.isOpen());
  TEST_ASSERT_EQUAL(K::NONE, r.onChunk(0, 1, 0, b.data(), 10, 2101).kind);
}

void test_at_most_one_result_per_begin() {
  BlobReceiver r;
  auto b = blobOf(300);
  int results = 0;
  auto count = [&](ReceiverEvent e) { if (e.kind != K::NONE) results++; };
  count(r.onBegin(0, 5, 300, crc32(b.data(), 300), 0));
  count(r.onChunk(0, 5, 240, b.data(), 60, 0));     // gap: BAD_SEQUENCE, closes
  count(r.onChunk(0, 5, 0, b.data(), 240, 0));      // leftovers: silent
  count(r.onChunk(0, 5, 240, b.data() + 240, 60, 0));
  count(r.onEnd(0, 5, 0));
  count(r.onEnd(0, 5, 0));
  TEST_ASSERT_EQUAL(1, results);
  results = 0;
  ReceiverEvent done = sendAll(r, b, 0, 6, 0);       // COMPLETE counts as the one result
  count(done);
  count(r.onEnd(0, 6, 0));                           // duplicate End: silent
  TEST_ASSERT_EQUAL(1, results);
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_happy_one_chunk);
  RUN_TEST(test_happy_five_chunks);
  RUN_TEST(test_too_large_and_zero);
  RUN_TEST(test_gap_is_bad_sequence);
  RUN_TEST(test_wrong_seq_chunk_is_bad_sequence);
  RUN_TEST(test_overrun_is_bad_sequence);
  RUN_TEST(test_bad_chunk_lengths);
  RUN_TEST(test_end_early_and_wrong_seq);
  RUN_TEST(test_bad_crc);
  RUN_TEST(test_no_transfer_is_silent);
  RUN_TEST(test_other_transport_ignored_and_does_not_refresh);
  RUN_TEST(test_begin_mid_transfer_restarts);
  RUN_TEST(test_timeout_closes_silently);
  RUN_TEST(test_at_most_one_result_per_begin);
  return UNITY_END();
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native -f test_config_receiver`
Expected: a compile error, `volco_blob_receiver.h: No such file or directory`.

- [ ] **Step 3: Implement the receiver**

Create `firmware/lib/volco_config/src/volco_blob_receiver.h`:

```cpp
#pragma once
// Receives a config write in chunks (spec §5.4, receiver rules 1-7). Pure: frames are decoded by
// the caller, time is passed in. At most one result per Begin: a REPLY, or a COMPLETE after which
// the caller decodes, validates, stores and sends exactly one ConfigResult itself.
#include <stddef.h>
#include <stdint.h>

#include "volco_config.h"

namespace volco {

constexpr uint32_t kTransferTimeoutMs = 2000;
constexpr size_t kMaxChunk = 240;

struct ReceiverEvent {
  enum Kind : uint8_t { NONE, REPLY, COMPLETE };
  Kind kind;
  uint8_t transport, seq, code;  // REPLY: send ConfigResult(seq, code) on transport
};

class BlobReceiver {
 public:
  ReceiverEvent onBegin(uint8_t transport, uint8_t seq, uint16_t total, uint32_t crc, uint32_t now);
  ReceiverEvent onChunk(uint8_t transport, uint8_t seq, uint16_t offset, const uint8_t* data, size_t len, uint32_t now);
  ReceiverEvent onEnd(uint8_t transport, uint8_t seq, uint32_t now);
  void tick(uint32_t now);
  bool isOpen() const { return open_; }
  const uint8_t* data() const { return buf_; }
  uint16_t size() const { return total_; }

 private:
  ReceiverEvent reply(uint8_t code);
  bool open_ = false;
  uint8_t transport_ = 0, seq_ = 0;
  uint16_t total_ = 0, received_ = 0;
  uint32_t crc_ = 0, last_ = 0;
  uint8_t buf_[kMaxConfigBlob] = {0};
};

}  // namespace volco
```

Create `firmware/lib/volco_config/src/volco_blob_receiver.cpp`:

```cpp
#include "volco_blob_receiver.h"

#include <string.h>

#include "volco_crc32.h"

namespace volco {

ReceiverEvent BlobReceiver::reply(uint8_t code) {
  open_ = false;
  return {ReceiverEvent::REPLY, transport_, seq_, code};
}

ReceiverEvent BlobReceiver::onBegin(uint8_t transport, uint8_t seq, uint16_t total, uint32_t crc, uint32_t now) {
  open_ = false;  // rule 1: a Begin always discards an open transfer
  transport_ = transport;
  seq_ = seq;
  if (total == 0 || total > kMaxConfigBlob) return reply(CFG_TOO_LARGE);
  open_ = true;
  total_ = total;
  received_ = 0;
  crc_ = crc;
  last_ = now;
  return {ReceiverEvent::NONE, transport, seq, CFG_OK};
}

ReceiverEvent BlobReceiver::onChunk(uint8_t transport, uint8_t seq, uint16_t offset, const uint8_t* data, size_t len,
                                    uint32_t now) {
  if (!open_ || transport != transport_) return {ReceiverEvent::NONE, transport, seq, CFG_OK};  // rules 2-3
  if (seq != seq_ || offset != received_ || len == 0 || len > kMaxChunk || received_ + len > total_)
    return reply(CFG_BAD_SEQUENCE);  // rule 4
  memcpy(buf_ + received_, data, len);
  received_ = (uint16_t)(received_ + len);
  last_ = now;
  return {ReceiverEvent::NONE, transport, seq, CFG_OK};
}

ReceiverEvent BlobReceiver::onEnd(uint8_t transport, uint8_t seq, uint32_t now) {
  (void)now;
  if (!open_ || transport != transport_) return {ReceiverEvent::NONE, transport, seq, CFG_OK};
  if (seq != seq_ || received_ != total_) return reply(CFG_BAD_SEQUENCE);
  if (crc32(buf_, total_) != crc_) return reply(CFG_BAD_CRC);
  open_ = false;  // rule 5: the caller decodes, validates, stores and sends the one result
  return {ReceiverEvent::COMPLETE, transport_, seq_, CFG_OK};
}

void BlobReceiver::tick(uint32_t now) {
  if (open_ && now - last_ >= kTransferTimeoutMs) open_ = false;  // rule 6: silent
}

}  // namespace volco
```

- [ ] **Step 4: Run the tests, then the whole suite**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native`
Expected: `test_config_receiver` 14 tests PASSED, and every other suite PASSED.

- [ ] **Step 5: Commit**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/lib/volco_config firmware/test/test_config_receiver
/usr/bin/git commit -m "feat(config): firmware chunked config receiver (spec 5.4)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
Ledger: `Task 5: Ruling: a chunk with 0 or more than 240 data bytes is BAD_SEQUENCE — spec gives the range 1–240 but no rule for violations — cost if wrong: one condition`.

---

### Task 6: C# generation, the API skeleton and all C# tests (RED in CI)

**Files:**
- Modify: `src/Volco.Core/Volco.Core.csproj`
- Create: `src/Volco.Core/Controller/ConfigCodes.cs`, `ControllerConfigCodec.cs`, `ControllerConfigExtensions.cs`, `ChipInfo.cs`, `ControllerConfigValidator.cs`, `ControllerPresets.cs` (stubs that throw, except `ConfigCodes.cs` and `ChipInfo.cs`)
- Create: `tests/Volco.Core.Tests/Controller/ConfigGoldenTests.cs`, `ControllerConfigCodecTests.cs`, `ControllerConfigValidatorTests.cs`, `ControllerPresetsTests.cs`

**Interfaces:**
- Produces, in namespace `Volco.Core.Controller` (generated types are in `Volco.Core.Controller.Proto`):
  - `enum ConfigResultCode : byte { Ok = 0, BadSequence = 1, BadCrc = 2, TooLarge = 3, DecodeFailed = 4, Invalid = 5, StorageFailed = 6 }`
  - `enum ConfigField : byte { SliderPin = 1, ButtonPin, LedPin, PairPinA, PairPinB, ScreenBus, ScreenAddress, I2cMissing, I2cPins, I2cClock, UdpSsid, UdpPassword, UdpPcIp, UdpPort, ScreenValue, InputValue }`
  - `readonly record struct ValidationResult(ConfigResultCode Code, byte Item, byte Field)` with `static Ok` and `IsOk`; `const byte NoItem = 0xFF` on `ValidationResult`
  - `sealed class ControllerConfigFormatException : Exception`
  - `static class ControllerConfigCodec`:
    - constants `MaxBlob = 1024`, `MaxSliders = 16`, `MaxButtons = 32`, `MaxPresetBytes = 24`, `MaxSsidBytes = 32`, `MaxPasswordBytes = 64`, `BlankPresetId = "blank"`
    - `byte[] Encode(ControllerConfig)`, `ControllerConfig Decode(byte[])`, `uint Crc32(ReadOnlySpan<byte>)`
  - `static class ControllerConfigExtensions`: `EffectiveScreens()`, `EffectiveInput()`, `EffectiveUdp()`
  - `sealed class ChipInfo`: `Usable(uint)`, `OutputCapable(uint)`, `AdcCapable(uint)`, `IsAdc2(uint)`, `static ChipInfo Esp32`
  - `static class ControllerConfigValidator`: `ValidationResult Validate(ControllerConfig, ChipInfo)`
  - `sealed record ControllerPreset(string Id, string DisplayName, bool IsAvailable, ControllerConfig? Hardware)`
  - `static class ControllerPresets`:
    - `DiyReferenceId = "diy-reference"`, `VolcoPcbId = "volco-pcb"`, `BlankId = "blank"`
    - `IReadOnlyList<ControllerPreset> All`
    - `ControllerConfig ApplyTo(ControllerConfig draft, ControllerPreset preset)`

- [ ] **Step 1: Add the packages, generation and embedded presets**

In `src/Volco.Core/Volco.Core.csproj`, replace

```xml
  <ItemGroup>
    <PackageReference Include="System.IO.Ports" Version="8.0.0" />
  </ItemGroup>
```

with

```xml
  <ItemGroup>
    <PackageReference Include="System.IO.Ports" Version="8.0.0" />
    <PackageReference Include="Google.Protobuf" Version="3.36.2" />
    <PackageReference Include="Grpc.Tools" Version="2.84.0" PrivateAssets="all" />
  </ItemGroup>

  <!-- The controller config schema, shared with the firmware (spec §6.1), and the bundled presets
       (spec §6.2). Generated code lands in Volco.Core.Controller.Proto. -->
  <ItemGroup>
    <Protobuf Include="../../proto/volco_config.proto" ProtoRoot="../../proto" GrpcServices="None" />
    <EmbeddedResource Include="../../proto/presets/*.json" LogicalName="Volco.Core.Presets.%(Filename)%(Extension)" />
  </ItemGroup>
```

- [ ] **Step 2: Write the codes and the chip table (complete; no logic to drive by tests)**

Create `src/Volco.Core/Controller/ConfigCodes.cs`:

```csharp
namespace Volco.Core.Controller;

/// <summary>ConfigResult codes (spec §5.4); also the code of a <see cref="ValidationResult"/>.</summary>
public enum ConfigResultCode : byte
{
    Ok = 0,
    BadSequence = 1,
    BadCrc = 2,
    TooLarge = 3,
    DecodeFailed = 4,
    Invalid = 5,
    StorageFailed = 6,
}

/// <summary>The field an INVALID result points at (spec §6.4).</summary>
public enum ConfigField : byte
{
    SliderPin = 1, ButtonPin = 2, LedPin = 3, PairPinA = 4, PairPinB = 5, ScreenBus = 6,
    ScreenAddress = 7, I2cMissing = 8, I2cPins = 9, I2cClock = 10, UdpSsid = 11, UdpPassword = 12,
    UdpPcIp = 13, UdpPort = 14, ScreenValue = 15, InputValue = 16,
}

/// <summary>The first violation of spec §6.4, the same triple the firmware would report.
/// <see cref="Item"/> is a slider/button index, an I2C bus, a range field number, or
/// <see cref="NoItem"/>; <see cref="Field"/> is a <see cref="ConfigField"/> or NoItem.</summary>
public readonly record struct ValidationResult(ConfigResultCode Code, byte Item, byte Field)
{
    public const byte NoItem = 0xFF;
    public static ValidationResult Ok { get; } = new(ConfigResultCode.Ok, NoItem, NoItem);
    public bool IsOk => Code == ConfigResultCode.Ok;

    public static ValidationResult Invalid(byte item, ConfigField field) => new(ConfigResultCode.Invalid, item, (byte)field);
}
```

Create `src/Volco.Core/Controller/ChipInfo.cs`:

```csharp
namespace Volco.Core.Controller;

/// <summary>What each GPIO of a chip family can do (spec §6.4): facts about the chip, not user
/// preferences. Bit n of each mask is GPIO n; any pin of 64 or more is never usable.</summary>
public sealed class ChipInfo
{
    private readonly ulong _exists, _reserved, _inputOnly, _adc1, _adc2;

    private ChipInfo(ulong exists, ulong reserved, ulong inputOnly, ulong adc1, ulong adc2)
        => (_exists, _reserved, _inputOnly, _adc1, _adc2) = (exists, reserved, inputOnly, adc1, adc2);

    /// <summary>Classic ESP32: exists 0-19, 21-23, 25-27, 32-39; reserved 1, 3 (USB serial) and
    /// 6-11 (flash); input-only 34-39; ADC1 32-39; ADC2 0, 2, 4, 12-15, 25-27.</summary>
    public static ChipInfo Esp32 { get; } = new(
        Span(0, 19) | Span(21, 23) | Span(25, 27) | Span(32, 39),
        Bit(1) | Bit(3) | Span(6, 11),
        Span(34, 39),
        Span(32, 39),
        Bit(0) | Bit(2) | Bit(4) | Span(12, 15) | Span(25, 27));

    public bool Usable(uint pin) => Has(_exists, pin) && !Has(_reserved, pin);
    public bool OutputCapable(uint pin) => Usable(pin) && !Has(_inputOnly, pin);
    public bool AdcCapable(uint pin) => Usable(pin) && (Has(_adc1, pin) || Has(_adc2, pin));
    public bool IsAdc2(uint pin) => Has(_adc2, pin);

    private static bool Has(ulong mask, uint pin) => pin < 64 && ((mask >> (int)pin) & 1) != 0;
    private static ulong Bit(int p) => 1UL << p;

    private static ulong Span(int a, int b)
    {
        ulong m = 0;
        for (var p = a; p <= b; p++) m |= Bit(p);
        return m;
    }
}
```

- [ ] **Step 3: Write the stubs**

Create `src/Volco.Core/Controller/ControllerConfigCodec.cs`:

```csharp
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>A blob that isn't a valid controller config (spec §6.1): malformed protobuf, a count
/// or string over its limit, invalid UTF-8, or a NUL in a string.</summary>
public sealed class ControllerConfigFormatException : Exception
{
    public ControllerConfigFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class ControllerConfigCodec
{
    public const int MaxBlob = 1024;
    public const int MaxSliders = 16;
    public const int MaxButtons = 32;
    public const int MaxPresetBytes = 24;
    public const int MaxSsidBytes = 32;
    public const int MaxPasswordBytes = 64;
    public const string BlankPresetId = "blank";

    public static byte[] Encode(ControllerConfig config) => throw new NotImplementedException();
    public static ControllerConfig Decode(byte[] blob) => throw new NotImplementedException();
    public static uint Crc32(ReadOnlySpan<byte> data) => throw new NotImplementedException();
}
```

Create `src/Volco.Core/Controller/ControllerConfigExtensions.cs`:

```csharp
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

public static class ControllerConfigExtensions
{
    public static ScreenBehaviour EffectiveScreens(this ControllerConfig config) => throw new NotImplementedException();
    public static InputTuning EffectiveInput(this ControllerConfig config) => throw new NotImplementedException();
    public static Udp EffectiveUdp(this ControllerConfig config) => throw new NotImplementedException();
}
```

Create `src/Volco.Core/Controller/ControllerConfigValidator.cs`:

```csharp
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

public static class ControllerConfigValidator
{
    public static ValidationResult Validate(ControllerConfig config, ChipInfo chip) => throw new NotImplementedException();
}
```

Create `src/Volco.Core/Controller/ControllerPresets.cs`:

```csharp
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>A preset in the hardware picker (spec §6.2). <see cref="Hardware"/> holds only the
/// hardware fields; it is null for a placeholder that isn't available yet.</summary>
public sealed record ControllerPreset(string Id, string DisplayName, bool IsAvailable, ControllerConfig? Hardware);

public static class ControllerPresets
{
    public const string DiyReferenceId = "diy-reference";
    public const string VolcoPcbId = "volco-pcb";
    public const string BlankId = "blank";

    public static IReadOnlyList<ControllerPreset> All => throw new NotImplementedException();
    public static ControllerConfig ApplyTo(ControllerConfig draft, ControllerPreset preset) => throw new NotImplementedException();
}
```

- [ ] **Step 4: Write the golden tests**

Create `tests/Volco.Core.Tests/Controller/ConfigGoldenTests.cs`:

```csharp
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

/// <summary>The C# side of the shared config vectors (firmware/test/golden/config.txt, generated by
/// firmware/tools/gen_config_vectors.py). The firmware's native tests check the same cases.</summary>
public class ConfigGoldenTests
{
    private sealed record Case(string Kind, string Name, byte[] Blob, string[] Rest);

    private static readonly List<Case> All = Load();

    private static List<Case> Load()
    {
        var cases = new List<Case>();
        foreach (var raw in File.ReadAllLines(RepoPaths.Combine("firmware", "test", "golden", "config.txt")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var blob = p[2] == "-" ? Array.Empty<byte>() : Convert.FromHexString(p[2]);
            cases.Add(new Case(p[0], p[1], blob, p.Skip(3).ToArray()));
        }
        return cases;
    }

    public static IEnumerable<object[]> Of(string kind) => All.Where(c => c.Kind == kind).Select(c => new object[] { c.Name });
    private static Case Get(string kind, string name) => All.Single(c => c.Kind == kind && c.Name == name);

    [Fact]
    public void GoldenFile_HasEveryKind()
    {
        Assert.True(All.Count(c => c.Kind == "decode-ok") >= 6);
        Assert.True(All.Count(c => c.Kind == "decode-fail") >= 9);
        Assert.True(All.Count(c => c.Kind == "validate") >= 85);
        Assert.True(All.Count(c => c.Kind == "crc") >= 3);
    }

    [Theory]
    [MemberData(nameof(Of), "crc")]
    public void Crc(string name)
    {
        var c = Get("crc", name);
        Assert.Equal(c.Rest[0], ControllerConfigCodec.Crc32(c.Blob).ToString("x8"));
    }

    [Theory]
    [MemberData(nameof(Of), "decode-ok")]
    public void DecodeOk(string name)
    {
        var c = Get("decode-ok", name);
        var config = ControllerConfigCodec.Decode(c.Blob);
        foreach (var kv in c.Rest)
        {
            var eq = kv.IndexOf('=');
            Assert.Equal(kv[(eq + 1)..], ValueOf(config, c.Blob.Length, kv[..eq]));
        }
    }

    [Theory]
    [MemberData(nameof(Of), "decode-fail")]
    public void DecodeFail(string name)
    {
        var c = Get("decode-fail", name);
        Assert.Throws<ControllerConfigFormatException>(() => ControllerConfigCodec.Decode(c.Blob));
    }

    [Theory]
    [MemberData(nameof(Of), "validate")]
    public void Validate(string name)
    {
        var c = Get("validate", name);
        var r = ControllerConfigValidator.Validate(ControllerConfigCodec.Decode(c.Blob), ChipInfo.Esp32);
        Assert.Equal($"{c.Rest[0]} {c.Rest[1]} {c.Rest[2]}", $"{(byte)r.Code} {r.Item} {r.Field}");
    }

    private static string ValueOf(ControllerConfig c, int size, string key)
    {
        var b0 = c.Buttons.FirstOrDefault();
        return key switch
        {
            "size" => size.ToString(),
            "sliders" => c.Sliders.Count.ToString(),
            "buttons" => c.Buttons.Count.ToString(),
            "preset" => c.Preset,
            "screens.brightness" => c.EffectiveScreens().Brightness.ToString(),
            "screens.dim_enabled" => c.EffectiveScreens().DimEnabled ? "1" : "0",
            "screens.transition_ms" => c.EffectiveScreens().TransitionMs.ToString(),
            "buttons.0.kind" => b0?.IndicatorCase switch
            {
                Button.IndicatorOneofCase.Led => "1",
                Button.IndicatorOneofCase.LedPair => "2",
                Button.IndicatorOneofCase.Screen => "3",
                _ => "0",
            },
            "buttons.0.screen.bus" => b0!.Screen.Bus.ToString(),
            "buttons.0.screen.address" => b0!.Screen.Address.ToString(),
            _ => "?" + key,
        };
    }
}
```

- [ ] **Step 5: Write the codec, validator and preset unit tests**

Create `tests/Volco.Core.Tests/Controller/ControllerConfigCodecTests.cs`:

```csharp
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

public class ControllerConfigCodecTests
{
    [Fact]
    public void Encode_BlankConfig_IsNeverEmptyAndCarriesTheBlankPreset()
    {
        var blob = ControllerConfigCodec.Encode(new ControllerConfig());
        Assert.NotEmpty(blob);
        Assert.Equal(ControllerConfigCodec.BlankPresetId, ControllerConfigCodec.Decode(blob).Preset);
    }

    [Fact]
    public void Encode_DoesNotChangeTheCallersConfig()
    {
        var config = new ControllerConfig();
        ControllerConfigCodec.Encode(config);
        Assert.False(config.HasPreset);
    }

    [Fact]
    public void Encode_KeepsAnExplicitPreset()
    {
        var blob = ControllerConfigCodec.Encode(new ControllerConfig { Preset = "diy-reference" });
        Assert.Equal("diy-reference", ControllerConfigCodec.Decode(blob).Preset);
    }

    [Fact]
    public void Decode_RejectsABlobOverTheMaximum()
        => Assert.Throws<ControllerConfigFormatException>(() => ControllerConfigCodec.Decode(new byte[ControllerConfigCodec.MaxBlob + 1]));

    [Fact]
    public void Crc32_MatchesTheZlibCheckValue()
        => Assert.Equal(0xCBF43926u, ControllerConfigCodec.Crc32("123456789"u8));

    [Fact]
    public void Effective_AbsentSubmessages_HoldTheProtoDefaults()
    {
        var c = new ControllerConfig();
        Assert.Null(c.Screens);
        Assert.Equal(255u, c.EffectiveScreens().Brightness);
        Assert.Equal(30u, c.EffectiveInput().DebounceMs);
        Assert.False(c.EffectiveUdp().Enabled);
        Assert.Equal(12345u, c.EffectiveUdp().PcPort);
    }

    [Fact]
    public void Effective_PresentSubmessage_IsReturnedAsIs()
    {
        var c = new ControllerConfig { Screens = new ScreenBehaviour { Brightness = 7 } };
        Assert.Same(c.Screens, c.EffectiveScreens());
    }
}
```

Create `tests/Volco.Core.Tests/Controller/ControllerConfigValidatorTests.cs`:

```csharp
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

/// <summary>What the golden vectors can't reach: configs only C# can build (decode already rejects
/// them). Every §6.4 rule is covered by ConfigGoldenTests.</summary>
public class ControllerConfigValidatorTests
{
    [Fact]
    public void TooManySliders_IsInvalidSliderPinWithNoItem()
    {
        var c = new ControllerConfig();
        for (var i = 0; i < 17; i++) c.Sliders.Add(new Slider { Pin = 33 });
        Assert.Equal(new ValidationResult(ConfigResultCode.Invalid, ValidationResult.NoItem, (byte)ConfigField.SliderPin),
            ControllerConfigValidator.Validate(c, ChipInfo.Esp32));
    }

    [Fact]
    public void TooManyButtons_IsInvalidButtonPinWithNoItem()
    {
        var c = new ControllerConfig();
        for (var i = 0; i < 33; i++) c.Buttons.Add(new Button { Pin = 4 });
        Assert.Equal(new ValidationResult(ConfigResultCode.Invalid, ValidationResult.NoItem, (byte)ConfigField.ButtonPin),
            ControllerConfigValidator.Validate(c, ChipInfo.Esp32));
    }

    [Fact]
    public void EmptyConfig_IsValid()
        => Assert.True(ControllerConfigValidator.Validate(new ControllerConfig(), ChipInfo.Esp32).IsOk);
}
```

Create `tests/Volco.Core.Tests/Controller/ControllerPresetsTests.cs`:

```csharp
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Xunit;

namespace Volco.Core.Tests.Controller;

public class ControllerPresetsTests
{
    private static ControllerPreset Diy => ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);

    [Fact]
    public void All_ListsDiyThenThePcbPlaceholderThenBlank()
    {
        Assert.Equal(new[] { "diy-reference", "volco-pcb", "blank" }, ControllerPresets.All.Select(p => p.Id));
        var pcb = ControllerPresets.All[1];
        Assert.False(pcb.IsAvailable);
        Assert.Null(pcb.Hardware);
        Assert.Equal("Volco PCB (coming soon)", pcb.DisplayName);
    }

    [Fact]
    public void Diy_MatchesTheSpecTable()
    {
        var h = Diy.Hardware!;
        Assert.Equal("diy-reference", h.Preset);
        Assert.Equal(new uint[] { 33, 35, 32, 36, 34 }, h.Sliders.Select(s => s.Pin));
        Assert.Equal(new uint[] { 4, 14, 5 }, h.Buttons.Select(b => b.Pin));
        Assert.Equal((12u, true), (h.Buttons[0].Led.Pin, h.Buttons[0].Led.ActiveLow));
        Assert.Equal((21u, true), (h.Buttons[1].Led.Pin, h.Buttons[1].Led.ActiveLow));
        Assert.Equal((18u, 19u, false), (h.Buttons[2].LedPair.PinA, h.Buttons[2].LedPair.PinB, h.Buttons[2].LedPair.ActiveLow));
        Assert.Equal((25u, 26u), (h.I2C0.Sda, h.I2C0.Scl));
        Assert.Equal((27u, 13u), (h.I2C1.Sda, h.I2C1.Scl));
        Assert.Null(h.Screens);
        Assert.Null(h.Udp);
    }

    [Fact]
    public void Diy_PassesValidation()
        => Assert.True(ControllerConfigValidator.Validate(Diy.Hardware!, ChipInfo.Esp32).IsOk);

    [Fact]
    public void ApplyTo_ReplacesHardwareAndKeepsEverythingElse()
    {
        var draft = new ControllerConfig
        {
            Preset = "blank",
            Screens = new ScreenBehaviour { Brightness = 10 },
            Input = new InputTuning { DebounceMs = 50 },
            Udp = new Udp { Enabled = true, Ssid = "net" },
        };
        draft.Sliders.Add(new Slider { Pin = 39 });

        var result = ControllerPresets.ApplyTo(draft, Diy);

        Assert.Equal(new uint[] { 33, 35, 32, 36, 34 }, result.Sliders.Select(s => s.Pin));
        Assert.Equal(3, result.Buttons.Count);
        Assert.Equal("diy-reference", result.Preset);
        Assert.Equal(10u, result.Screens.Brightness);
        Assert.Equal(50u, result.Input.DebounceMs);
        Assert.Equal("net", result.Udp.Ssid);
        Assert.Single(draft.Sliders);  // the draft itself is untouched
    }

    [Fact]
    public void ApplyTo_Blank_ClearsHardware()
    {
        var draft = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
        var blank = ControllerPresets.ApplyTo(draft, ControllerPresets.All.Single(p => p.Id == ControllerPresets.BlankId));
        Assert.Empty(blank.Sliders);
        Assert.Empty(blank.Buttons);
        Assert.Null(blank.I2C0);
        Assert.Null(blank.I2C1);
        Assert.Equal("blank", blank.Preset);
    }

    [Fact]
    public void ApplyTo_UnavailablePreset_Throws()
        => Assert.Throws<InvalidOperationException>(() => ControllerPresets.ApplyTo(new ControllerConfig(), ControllerPresets.All[1]));
}
```

- [ ] **Step 6: Push and watch CI fail for the right reason**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add src/Volco.Core tests/Volco.Core.Tests/Controller
/usr/bin/git commit -m "test(config): C# controller config API skeleton and failing tests" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push -u origin controller-config-stage2
gh run list --branch controller-config-stage2 --limit 1
```
Wait for the run (`gh run watch <id>`). Expected:
- the **Run Tests** job compiles (generation works), then fails
- the failures are only in `Volco.Core.Tests.Controller.*`, with `NotImplementedException` (except `ChipInfo`-only paths)
- every pre-existing test passes
- `Firmware Tests` passes

Check with: `gh run view <id> --log-failed | grep -E "Failed Volco|NotImplemented" | head -20`.

If the build fails instead (a generated-name mismatch, for example `I2C0` vs `I2c0`), fix the **test and stub** names to match the generated code, record the real name in the ledger, and push again.

---

### Task 7: C# codec and effective defaults (GREEN)

**Files:**
- Modify: `src/Volco.Core/Controller/ControllerConfigCodec.cs`, `src/Volco.Core/Controller/ControllerConfigExtensions.cs`

**Interfaces:**
- Consumes: Task 6 stubs and tests.
- Produces: working `Encode`, `Decode`, `Crc32`, `EffectiveScreens`, `EffectiveInput`, `EffectiveUdp`.

- [ ] **Step 1: Implement the codec**

Replace the `ControllerConfigCodec` class body in `src/Volco.Core/Controller/ControllerConfigCodec.cs` (keep the exception class and usings; add `using System.Text;` and `using Google.Protobuf;` at the top):

```csharp
/// <summary>Encodes and decodes the controller config blob (spec §6.1), enforcing the limits the
/// firmware's nanopb code enforces, which Google.Protobuf doesn't know about. "Same config" is
/// "same bytes" (§6.5): callers keep the bytes they sent or read and never re-encode to compare.</summary>
public static class ControllerConfigCodec
{
    public const int MaxBlob = 1024;
    public const int MaxSliders = 16;
    public const int MaxButtons = 32;
    public const int MaxPresetBytes = 24;
    public const int MaxSsidBytes = 32;
    public const int MaxPasswordBytes = 64;
    public const string BlankPresetId = "blank";

    /// <summary>The blob for <paramref name="config"/>. Never empty: a config without a preset is
    /// sent as preset "blank" (an empty write is TOO_LARGE on the controller).</summary>
    public static byte[] Encode(ControllerConfig config)
    {
        var copy = config.Clone();
        if (string.IsNullOrEmpty(copy.Preset)) copy.Preset = BlankPresetId;
        return copy.ToByteArray();
    }

    /// <exception cref="ControllerConfigFormatException">Malformed, over a limit, invalid UTF-8, or a NUL in a string.</exception>
    public static ControllerConfig Decode(byte[] blob)
    {
        if (blob.Length > MaxBlob)
            throw new ControllerConfigFormatException($"The config is {blob.Length} bytes; the maximum is {MaxBlob}.");
        ControllerConfig config;
        try
        {
            config = ControllerConfig.Parser.ParseFrom(blob);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw new ControllerConfigFormatException("Not a valid controller config: " + e.Message, e);
        }
        if (config.Sliders.Count > MaxSliders)
            throw new ControllerConfigFormatException($"{config.Sliders.Count} sliders; the maximum is {MaxSliders}.");
        if (config.Buttons.Count > MaxButtons)
            throw new ControllerConfigFormatException($"{config.Buttons.Count} buttons; the maximum is {MaxButtons}.");
        CheckString(config.Preset, "preset", MaxPresetBytes);
        if (config.Udp is { } udp)
        {
            CheckString(udp.Ssid, "udp.ssid", MaxSsidBytes);
            CheckString(udp.Password, "udp.password", MaxPasswordBytes);
        }
        return config;
    }

    /// <summary>zlib CRC-32 (spec §6.5).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (0u - (c & 1u)));
        }
        return ~c;
    }

    private static void CheckString(string value, string name, int maxBytes)
    {
        if (value.Contains('\0')) throw new ControllerConfigFormatException($"{name} contains a NUL character.");
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > maxBytes) throw new ControllerConfigFormatException($"{name} is {bytes} bytes; the maximum is {maxBytes}.");
    }
}
```

- [ ] **Step 2: Implement the effective submessages**

Replace `src/Volco.Core/Controller/ControllerConfigExtensions.cs` with:

```csharp
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>Google.Protobuf returns null for an absent submessage, while nanopb fills it with the
/// proto2 defaults (stage 1). Read submessages through these, never directly (spec §6.1).</summary>
public static class ControllerConfigExtensions
{
    public static ScreenBehaviour EffectiveScreens(this ControllerConfig config) => config.Screens ?? new ScreenBehaviour();
    public static InputTuning EffectiveInput(this ControllerConfig config) => config.Input ?? new InputTuning();
    public static Udp EffectiveUdp(this ControllerConfig config) => config.Udp ?? new Udp();
}
```

- [ ] **Step 3: Push and watch the codec tests pass**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add src/Volco.Core/Controller
/usr/bin/git commit -m "feat(config): C# controller config codec and effective defaults" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push
```
Wait for the run. Expected:
- `ControllerConfigCodecTests` and `ConfigGoldenTests.Crc/DecodeOk/DecodeFail/GoldenFile_HasEveryKind` pass
- the remaining failures are only `Validate` (golden and unit) and `ControllerPresetsTests`, all `NotImplementedException`

---

### Task 8: C# validator (GREEN)

**Files:**
- Modify: `src/Volco.Core/Controller/ControllerConfigValidator.cs`

**Interfaces:**
- Consumes: `ChipInfo`, `ValidationResult`, `ConfigField`, `EffectiveScreens`/`EffectiveInput`/`EffectiveUdp`.
- Produces: `ControllerConfigValidator.Validate`, returning the same triple as the firmware for every input.

- [ ] **Step 1: Implement the validator**

Replace `src/Volco.Core/Controller/ControllerConfigValidator.cs` with:

```csharp
using System.Text;
using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>Spec §6.4, step by step and in the same order as the firmware's validateConfig, so both
/// report the same first violation. The firmware's verdict is the one that counts; this gives the
/// user immediate feedback.</summary>
public static class ControllerConfigValidator
{
    private const byte None = ValidationResult.NoItem;

    public static ValidationResult Validate(ControllerConfig c, ChipInfo chip)
    {
        // 1. Counts (decode already enforces them; reachable only for configs built in code).
        if (c.Sliders.Count > ControllerConfigCodec.MaxSliders) return ValidationResult.Invalid(None, ConfigField.SliderPin);
        if (c.Buttons.Count > ControllerConfigCodec.MaxButtons) return ValidationResult.Invalid(None, ConfigField.ButtonPin);
        var udp = c.EffectiveUdp();

        // 2. Sliders.
        for (var i = 0; i < c.Sliders.Count; i++)
        {
            var s = c.Sliders[i];
            if (!s.HasPin || !chip.AdcCapable(s.Pin)) return ValidationResult.Invalid((byte)i, ConfigField.SliderPin);
            if (udp.Enabled && chip.IsAdc2(s.Pin)) return ValidationResult.Invalid((byte)i, ConfigField.SliderPin);
        }

        // 3. Buttons: pin, indicator fields, then the (bus, address) duplicate check, one button at a time.
        for (var i = 0; i < c.Buttons.Count; i++)
        {
            var b = c.Buttons[i];
            var item = (byte)i;
            if (!b.HasPin || !chip.Usable(b.Pin)) return ValidationResult.Invalid(item, ConfigField.ButtonPin);
            switch (b.IndicatorCase)
            {
                case Button.IndicatorOneofCase.Led:
                    if (!b.Led.HasPin || !chip.OutputCapable(b.Led.Pin)) return ValidationResult.Invalid(item, ConfigField.LedPin);
                    break;
                case Button.IndicatorOneofCase.LedPair:
                    var p = b.LedPair;
                    if (!p.HasPinA || !chip.OutputCapable(p.PinA)) return ValidationResult.Invalid(item, ConfigField.PairPinA);
                    if (!p.HasPinB || !chip.OutputCapable(p.PinB) || p.PinB == p.PinA)
                        return ValidationResult.Invalid(item, ConfigField.PairPinB);
                    break;
                case Button.IndicatorOneofCase.Screen:
                    var screen = b.Screen;
                    if (screen.Bus > 1) return ValidationResult.Invalid(item, ConfigField.ScreenBus);
                    if (screen.Address != 0x3C && screen.Address != 0x3D) return ValidationResult.Invalid(item, ConfigField.ScreenAddress);
                    for (var j = 0; j < i; j++)
                    {
                        var o = c.Buttons[j];
                        if (o.IndicatorCase == Button.IndicatorOneofCase.Screen && o.Screen.Bus == screen.Bus
                            && o.Screen.Address == screen.Address)
                            return ValidationResult.Invalid(item, ConfigField.ScreenAddress);
                    }
                    break;
            }
        }

        // 4. I2C, for each bus some screen uses (an unused I2cBus is ignored entirely).
        var used = new bool[2];
        foreach (var b in c.Buttons)
            if (b.IndicatorCase == Button.IndicatorOneofCase.Screen) used[b.Screen.Bus] = true;
        for (byte bus = 0; bus < 2; bus++)
        {
            if (!used[bus]) continue;
            var i2c = bus == 0 ? c.I2C0 : c.I2C1;
            if (i2c is null) return ValidationResult.Invalid(bus, ConfigField.I2cMissing);
            if (!i2c.HasSda || !i2c.HasScl || !chip.OutputCapable(i2c.Sda) || !chip.OutputCapable(i2c.Scl) || i2c.Sda == i2c.Scl)
                return ValidationResult.Invalid(bus, ConfigField.I2cPins);
            if (i2c.ClockHz is < 100000 or > 1000000) return ValidationResult.Invalid(bus, ConfigField.I2cClock);
        }

        // 5. Pin uniqueness, in walk order: sliders, buttons with their indicator pins, used I2C buses.
        var seen = new HashSet<uint>();
        for (var i = 0; i < c.Sliders.Count; i++)
            if (!seen.Add(c.Sliders[i].Pin)) return ValidationResult.Invalid((byte)i, ConfigField.SliderPin);
        for (var i = 0; i < c.Buttons.Count; i++)
        {
            var b = c.Buttons[i];
            var item = (byte)i;
            if (!seen.Add(b.Pin)) return ValidationResult.Invalid(item, ConfigField.ButtonPin);
            if (b.IndicatorCase == Button.IndicatorOneofCase.Led && !seen.Add(b.Led.Pin))
                return ValidationResult.Invalid(item, ConfigField.LedPin);
            if (b.IndicatorCase == Button.IndicatorOneofCase.LedPair)
            {
                if (!seen.Add(b.LedPair.PinA)) return ValidationResult.Invalid(item, ConfigField.PairPinA);
                if (!seen.Add(b.LedPair.PinB)) return ValidationResult.Invalid(item, ConfigField.PairPinB);
            }
        }
        for (byte bus = 0; bus < 2; bus++)
        {
            if (!used[bus]) continue;
            var i2c = bus == 0 ? c.I2C0! : c.I2C1!;
            if (!seen.Add(i2c.Sda) || !seen.Add(i2c.Scl)) return ValidationResult.Invalid(bus, ConfigField.I2cPins);
        }

        // 6. UDP, only when enabled (lengths are UTF-8 bytes, as on the firmware).
        if (udp.Enabled)
        {
            var ssid = Encoding.UTF8.GetByteCount(udp.Ssid);
            var password = Encoding.UTF8.GetByteCount(udp.Password);
            if (ssid is < 1 or > 32) return ValidationResult.Invalid(None, ConfigField.UdpSsid);
            if (password != 0 && password is < 8 or > 64) return ValidationResult.Invalid(None, ConfigField.UdpPassword);
            if (!udp.HasPcIp || BadIp(udp.PcIp)) return ValidationResult.Invalid(None, ConfigField.UdpPcIp);
            if (udp.PcPort is < 1 or > 65535 || udp.LocalPort is < 1 or > 65535) return ValidationResult.Invalid(None, ConfigField.UdpPort);
        }

        // 7. Ranges, in field-number order, screens before input (absent submessages = defaults).
        var s7 = c.EffectiveScreens();
        if (s7.Brightness > 255) return ValidationResult.Invalid(1, ConfigField.ScreenValue);
        if (s7.TransitionMs > 2000) return ValidationResult.Invalid(2, ConfigField.ScreenValue);
        if (s7.ScrollHoldMs > 10000) return ValidationResult.Invalid(3, ConfigField.ScreenValue);
        if (s7.ScrollSpeedPxS is < 5 or > 200) return ValidationResult.Invalid(4, ConfigField.ScreenValue);
        if (s7.ScrollEndHoldMs > 10000) return ValidationResult.Invalid(5, ConfigField.ScreenValue);
        if (s7.DimAfterS is < 10 or > 86400) return ValidationResult.Invalid(7, ConfigField.ScreenValue);
        if (s7.DimBrightness > 255) return ValidationResult.Invalid(8, ConfigField.ScreenValue);
        if (s7.OffAfterS is < 10 or > 86400) return ValidationResult.Invalid(10, ConfigField.ScreenValue);
        if (s7.PixelShiftEveryS is < 10 or > 86400) return ValidationResult.Invalid(12, ConfigField.ScreenValue);
        var input = c.EffectiveInput();
        if (input.DebounceMs is < 1 or > 200) return ValidationResult.Invalid(1, ConfigField.InputValue);
        if (input.SliderIntervalMs is < 1 or > 100) return ValidationResult.Invalid(2, ConfigField.InputValue);
        if (input.FilterWeightPct is < 1 or > 100) return ValidationResult.Invalid(3, ConfigField.InputValue);
        if (input.SliderDeadband > 500) return ValidationResult.Invalid(4, ConfigField.InputValue);

        return ValidationResult.Ok;
    }

    private static bool BadIp(uint ip) => ip == 0 || ip == 0xFFFFFFFF || ip >> 24 == 127 || ip >> 28 == 0xE;
}
```

Note: `password != 0 && password is < 8 or > 64` parses as `password != 0 && (password is < 8 or > 64)`, because `is` binds tighter than `&&`.

- [ ] **Step 2: Push and watch the validator tests pass**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add src/Volco.Core/Controller/ControllerConfigValidator.cs
/usr/bin/git commit -m "feat(config): C# controller config validator (spec 6.4)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push
```
Wait for the run. Expected:
- every `ConfigGoldenTests.Validate` case and `ControllerConfigValidatorTests` pass
- only `ControllerPresetsTests` still fail (`NotImplementedException`)
- if a golden case fails, the theory name says which: compare with spec §6.4 and the firmware (which passes the same case), and fix the C# side

---

### Task 9: C# presets (GREEN)

**Files:**
- Modify: `src/Volco.Core/Controller/ControllerPresets.cs`

**Interfaces:**
- Consumes: the embedded resource `Volco.Core.Presets.diy-reference.json` and `ControllerConfig`.
- Produces: `ControllerPresets.All` and `ApplyTo`.

- [ ] **Step 1: Implement the presets**

Replace the `ControllerPresets` class in `src/Volco.Core/Controller/ControllerPresets.cs` (keep the record and usings; add `using Google.Protobuf;`):

```csharp
public static class ControllerPresets
{
    public const string DiyReferenceId = "diy-reference";
    public const string VolcoPcbId = "volco-pcb";
    public const string BlankId = "blank";

    /// <summary>DIY reference, the Volco PCB placeholder (not available until its pins are known),
    /// then Blank.</summary>
    public static IReadOnlyList<ControllerPreset> All { get; } = new[]
    {
        new ControllerPreset(DiyReferenceId, "DIY reference", true, LoadEmbedded(DiyReferenceId)),
        new ControllerPreset(VolcoPcbId, "Volco PCB (coming soon)", false, null),
        new ControllerPreset(BlankId, "Blank", true, new ControllerConfig { Preset = BlankId }),
    };

    /// <summary>A copy of <paramref name="draft"/> with exactly the hardware fields (sliders,
    /// buttons, i2c0, i2c1, preset) replaced by the preset's; behaviour, input tuning and UDP stay.</summary>
    public static ControllerConfig ApplyTo(ControllerConfig draft, ControllerPreset preset)
    {
        if (!preset.IsAvailable || preset.Hardware is null)
            throw new InvalidOperationException($"Preset '{preset.Id}' isn't available.");
        var result = draft.Clone();
        var hw = preset.Hardware;
        result.Sliders.Clear();
        result.Sliders.AddRange(hw.Sliders.Select(s => s.Clone()));
        result.Buttons.Clear();
        result.Buttons.AddRange(hw.Buttons.Select(b => b.Clone()));
        result.I2C0 = hw.I2C0?.Clone();
        result.I2C1 = hw.I2C1?.Clone();
        result.Preset = hw.Preset;
        return result;
    }

    private static ControllerConfig LoadEmbedded(string id)
    {
        var name = $"Volco.Core.Presets.{id}.json";
        using var stream = typeof(ControllerPresets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded preset {name} not found.");
        using var reader = new StreamReader(stream);
        return JsonParser.Default.Parse<ControllerConfig>(reader.ReadToEnd());
    }
}
```

- [ ] **Step 2: Push and watch everything pass**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add src/Volco.Core/Controller/ControllerPresets.cs
/usr/bin/git commit -m "feat(config): C# controller presets (DIY reference, PCB placeholder, Blank)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push
```
Wait for the run. Expected: `Build Volco` all green, meaning the .NET tests (including all of `Volco.Core.Tests.Controller`), `Volco.App.Tests` (including `ArchitectureTests`), and `Firmware Tests`.

---

### Task 10: Firmware id script, CI checks, docs

**Files:**
- Create: `firmware/scripts/firmware_id.sh`, `firmware/scripts/check_firmware_id_paths.py`
- Modify: `.github/workflows/build.yml` (the `firmware-test` job)
- Modify: `CLAUDE.md` (the firmware section)

**Interfaces:**
- Produces:
  - `firmware/scripts/firmware_id.sh` prints the 12-hex-character build id (spec §9.2)
  - `firmware_id.sh --list` prints the hashed paths, one per line
  - Stage 6 passes the id as `VOLCO_FW_VERSION`

- [ ] **Step 1: Write the path check, a test that fails until the id script exists**

Create `firmware/scripts/check_firmware_id_paths.py`:

```python
#!/usr/bin/env python3
"""Checks the firmware build id script (spec §9.2): it prints 12 lowercase hex digits, the same on
every run, and its path list covers every directory platformio.ini builds from (src_dir, lib_dir,
extra_scripts, custom_nanopb_protos). Run from anywhere inside the repo with any Python 3."""
import configparser
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
SCRIPT = os.path.join(HERE, "firmware_id.sh")


def run(*args):
    return subprocess.run(["bash", SCRIPT, *args], check=True, capture_output=True, text=True, cwd=ROOT).stdout


def main():
    first, second = run().strip(), run().strip()
    if not re.fullmatch(r"[0-9a-f]{12}", first):
        sys.exit(f"firmware_id.sh printed {first!r}, not 12 hex digits")
    if first != second:
        sys.exit(f"firmware_id.sh is not stable: {first} then {second}")
    listed = run("--list").split()

    ini = configparser.ConfigParser(interpolation=None)
    ini.read(os.path.join(ROOT, "firmware", "platformio.ini"))
    referenced = {"firmware/src", "firmware/lib"}  # PlatformIO's defaults when src_dir/lib_dir are unset
    for section in ini.sections():
        for key in ("src_dir", "lib_dir", "extra_scripts", "custom_nanopb_protos"):
            for token in ini.get(section, key, fallback="").split():
                token = re.sub(r"^(pre:|post:|\+<|-<)", "", token).rstrip(">")
                path = os.path.relpath(os.path.normpath(os.path.join(ROOT, "firmware", token)), ROOT)
                referenced.add(path.replace(os.sep, "/"))
    missing = [r for r in sorted(referenced) if not any(r == p or r.startswith(p + "/") for p in listed)]
    if missing:
        sys.exit(f"firmware_id.sh doesn't hash: {missing}")
    print(f"firmware id {first} ({len(listed)} paths) OK")


if __name__ == "__main__":
    main()
```

Run: `cd /root/dev/volco/.claude/worktrees/firmware && python3 firmware/scripts/check_firmware_id_paths.py`
Expected: it fails with a `CalledProcessError`/`No such file`, because `firmware_id.sh` doesn't exist yet.

- [ ] **Step 2: Write the id script**

Create `firmware/scripts/firmware_id.sh`:

```bash
#!/usr/bin/env bash
# The firmware build id (spec §9.2): a hash of exactly the sources that make up the firmware, so
# a release whose firmware didn't change gets the same id and Volco offers no update.
# Usage: firmware_id.sh          prints 12 hex digits
#        firmware_id.sh --list   prints the hashed paths
# check_firmware_id_paths.py (run in CI) fails if platformio.ini builds from a path not listed here.
set -euo pipefail
PATHS=(
  firmware/src
  firmware/lib
  firmware/platformio.ini
  firmware/scripts
  proto/volco_config.proto
  proto/volco_config.options
)
if [[ "${1:-}" == "--list" ]]; then
  printf '%s\n' "${PATHS[@]}"
  exit 0
fi
cd "$(git rev-parse --show-toplevel)"
for p in "${PATHS[@]}"; do git rev-parse "HEAD:$p"; done | sha256sum | cut -c1-12
```

- [ ] **Step 3: Commit the scripts (the id hashes committed trees), then run the check**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/scripts
/usr/bin/git commit -m "build(firmware): firmware build id script and its path check (spec 9.2)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
python3 firmware/scripts/check_firmware_id_paths.py
```
Expected: `firmware id <12 hex> (6 paths) OK`. The `custom_nanopb_protos` entry resolves to `proto/volco_config.proto`, which is listed.

- [ ] **Step 4: Add the CI checks**

In `.github/workflows/build.yml`, in the `firmware-test` job, insert after the `Install PlatformIO` step:

```yaml
    - name: Firmware build id
      run: python firmware/scripts/check_firmware_id_paths.py

    - name: Config vectors are up to date
      run: |
        pip install protobuf==7.36.2 grpcio-tools==1.84.0
        python firmware/tools/gen_config_vectors.py
        git diff --exit-code firmware/test/golden/config.txt
```

- [ ] **Step 5: Document stage 2 in CLAUDE.md**

In `CLAUDE.md`, in the **Firmware** code block, add after the `pio test -e native` line:

```bash
~/.platformio/penv/bin/python firmware/tools/gen_config_vectors.py  # regenerate firmware/test/golden/config.txt after a proto change
bash firmware/scripts/firmware_id.sh  # the firmware build id (hash of the firmware's sources)
```

After the firmware paragraph that begins `PlatformIO / Arduino framework / C++17.`, add this paragraph:

```markdown
The controller config schema is `proto/volco_config.proto` (proto2; limits in `proto/volco_config.options`; presets in `proto/presets/`). It generates nanopb code for `firmware/lib/volco_config` (decode, validation, chip table, CRC, chunked receiver) and C# for `Volco.Core/Controller` (codec, validator, presets). Both are checked against the same generated vectors, `firmware/test/golden/config.txt`. Nothing uses them yet: the firmware still reads `config.h` until stage 3 of `docs/superpowers/specs/2026-10-03-controller-config-design.md` §15.
```

- [ ] **Step 6: Commit, push, check CI**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add .github/workflows/build.yml CLAUDE.md
/usr/bin/git commit -m "ci: firmware id and config-vector staleness checks; document stage 2" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push
```
Wait for the run. Expected: `Build Volco` all green, with the `Firmware build id` and `Config vectors are up to date` steps passing in `Firmware Tests`.

# Controller Config, Stage 4a: Protocol and Firmware Config Session Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the redesigned v1 protocol on the wire, on both ends, and give the firmware its whole config session.
- **The new Hello** carries the chip, the firmware id, the config state and the config CRC.
- **New frames:** ConfigRead/ConfigData and ConfigWriteBegin/Chunk/End/ConfigResult, with `seq`.
- **The firmware session:** read reply, chunked write, validate, store, reply, reboot.
- **The stage 3 seed is removed.**
- **A dev tool** (`config_tool.py`) reads and writes a controller's config over serial or UDP, so the firmware session can be proven on hardware before the app has any UI (stage 5).

**Stage 4 is split.** 4a (this plan) is the protocol on both ends, the firmware session, the tool and hardware checks. **4b** is the app side: `ControllerConfigService` with retries, implied OK and confirmation; the mirror and schema v5; the banner logic; the runtime wiring; removing the baud setting; and the simulator's config support.

**Architecture:**
- **Firmware:**
  - `lib/volco_protocol` gains the new `encodeHello`, config frame encoders and decoders, and a `ConfigSink` that `Link` routes 0x87–0x8A frames to, together with the transport id they arrived on.
  - `src/config_session.cpp` implements `ConfigSink` with stage 2's `BlobReceiver` plus decode/validate/store, and reboots after OK.
  - `config_store` keeps the stored bytes for read replies, and saves new ones.
- **App (protocol layer only):** `HelloMessage` gains its fields plus a version-only form; `ConfigDataMessage` and `ConfigResultMessage`; `ProtocolEncoder.EncodeConfigRead`/`EncodeConfigWrite`; `ControllerInfoConfig` gains `Chip`/`FirmwareVersion`.
- **Shared:** both sides check the same golden frames in `firmware/test/golden/frames.txt`.

**Tech Stack:** C++17 / Arduino-ESP32 3.3.8 / Unity; .NET 8 (CI only) / xUnit; Python 3 (pyserial, protobuf from PlatformIO's penv).

**Spec:** `docs/superpowers/specs/2026-10-03-controller-config-design.md` §5.1–§5.5, §6.6, §6.7, §8.1, §12.1, §12.2, §15 stage 4.

## Global Constraints

- **Tools:** `/usr/bin/git`; no local `dotnet` (C# runs in CI on push); Write tool for files.
- **Hardware safety:**
  - Before **every** flash, erase or tool write, run `hello_check.py --port P --guard-only` (it must say `1a86:7523 OK`).
  - tty numbers change after a replug, so find the port by its guard result, never by habit.
  - Never touch `10c4:ea60`.
- **Hello body (§5.2):** `version u8 (1) | chip u8 (1 = ESP32) | fwLen u8 (0..32) | fw ASCII | configState u8 (0 none, 1 stored, 2 damaged) | configCrc u32 BE | sliders u8 | buttons u8 | kinds[buttons]`.
  - Every Hello is followed by one Slider frame per slider (PR #68, unchanged).
  - A Hello whose `version` ≠ 1 is parsed by the app as version-only.
  - A version-1 body shorter than its lengths claim, or with `fwLen` > 32, is dropped.
- **Config frames (§5.1, §5.3, §5.4):**

  | Frame | Body |
  |---|---|
  | `0x87` ConfigRead | `seq` |
  | `0x05` ConfigData | `seq, total u16, offset u16, crc32 u32, data 0..240` |
  | `0x88` ConfigWriteBegin | `seq, total u16, crc32 u32` |
  | `0x89` ConfigWriteChunk | `seq, offset u16, data` |
  | `0x8A` ConfigWriteEnd | `seq` |
  | `0x06` ConfigResult | `seq, code, item, field` |

  - All integers are big-endian.
  - A whole read reply is **one** transport send, as is a whole write burst.
  - Replies go to the transport the request arrived on.
- **Firmware id:** `VOLCO_FW_VERSION`, default `"dev"`, with a compile-time check that it is ≤ 32 bytes. CI sets it in stage 6.
- **After OK** (§6.7): process no more frames, flush, `delay(200)`, `ESP.restart()`.
- **Config frames count as proof of life** (§5.1). They already do: every valid frame updates `lastRx_`.

## Review Focus

1. **An old-firmware board** (old 7-byte Hello) must be dropped by the new parser, not misread as a version-1 Hello with garbage counts. Golden `hello_old_format`, parsed to nothing.
2. **A read reply** is one send containing every chunk, in order, all carrying the request's `seq`, and the CRC in every chunk matches the bytes. Tested by `test_config_session_read_reply` (a native test of the frame builder) and on hardware by `config_tool.py read`.
3. **A write from transport A while a transfer from B is open** is ignored, and its result never goes to B. Covered by stage 2's receiver tests, plus `test_link_routes_config_frames_with_transport` here.
4. **A save of a valid config reboots into it:** the next Hello reports state 1 and the CRC of the bytes sent. On hardware with `config_tool.py write`.
5. **A write that fails validation leaves the old config:** INVALID with the right `item`/`field`, no reboot, and the same CRC in the next Hello. On hardware, and the triple matches `config.txt`.

---

### Task 1: The new Hello on both sides (shared goldens; firmware encoder; C# parser)

**Files:** `firmware/test/golden/frames.txt`, `firmware/lib/volco_protocol/src/volco_protocol.{h,cpp}`, `firmware/test/test_protocol/test_main.cpp`, `firmware/src/main.cpp`, `src/Volco.Core/Communication/{ControllerMessage.cs,ProtocolParser.cs,ProtocolEncoder.cs}`, `src/Volco.Core/Config/DeviceSettingsConfig.cs` (`ControllerInfoConfig`), `src/Volco.Core/Runtime/VolcoRuntime.cs` (`OnHello`), `tests/Volco.Core.Tests/Communication/ProtocolGoldenTests.cs`

**Interfaces:**
- Firmware: `struct HelloInfo { uint8_t chip; const char* firmware; uint8_t configState; uint32_t configCrc; uint8_t sliders; uint8_t buttons; const uint8_t* kinds; }`, `size_t encodeHello(const HelloInfo&, uint8_t* out)`, `constexpr size_t kMaxHelloFrame = 80`. `LinkConfig` gains trailing members `uint8_t chip = 1; const char* firmware = "dev"; uint8_t configState = 0; uint32_t configCrc = 0;`.
- C#: `enum ChipFamily : byte { Unknown = 0, Esp32 = 1 }`, `enum ControllerConfigState : byte { None = 0, Stored = 1, Damaged = 2 }`. `HelloMessage` gets new properties `Chip`, `FirmwareVersion`, `ConfigState`, `ConfigCrc`.
  - New constructor: `HelloMessage(byte version, ChipFamily chip, string firmwareVersion, ControllerConfigState configState, uint configCrc, int sliderCount, IReadOnlyList<IndicatorKind> buttons)`.
  - The existing 3-argument constructor stays: chip Esp32, firmware `""`, state Stored, CRC 0.
  - Version-only messages use the 3-argument constructor with zero sliders and no buttons.
  - `ProtocolEncoder.EncodeHello(byte version, ChipFamily chip, string firmware, ControllerConfigState state, uint crc, int sliders, IReadOnlyList<IndicatorKind> kinds)` replaces the old overload.

- [ ] **Step 1 (RED):** In `frames.txt`, replace the two Hello lines, and add three, exactly:
  ```
  hello_led_hardware AA 11 01 01 01 03 64 65 76 01 12 34 56 78 05 03 01 01 02 78
  hello_screen_controller AA 12 01 01 01 03 64 65 76 00 00 00 00 00 05 04 03 03 03 03 74
  hello_damaged AA 0E 01 01 01 03 64 65 76 02 CA FE F0 0D 00 00 BE
  hello_version_2 AA 04 01 02 09 09 03
  hello_old_format AA 07 01 01 05 03 01 01 02 04
  ```
  - **Firmware test:** encodes `HelloInfo{1, "dev", 1, 0x12345678, 5, 3, {1,1,2}}` and compares with `hello_led_hardware`; also `hello_damaged` via `HelloInfo{1, "dev", 2, 0xCAFEF00D, 0, 0, nullptr}`.
  - **C# golden tests:**
    - `EncodeHello(1, Esp32, "dev", Stored, 0x12345678, 5, [Led, Led, LedPair])` equals `hello_led_hardware`
    - parsing `hello_led_hardware` gives Version 1, Chip Esp32, FirmwareVersion "dev", ConfigState Stored, ConfigCrc 0x12345678, SliderCount 5, Buttons [Led, Led, LedPair]
    - parsing `hello_screen_controller` gives state None, CRC 0, 4 screens
    - parsing `hello_damaged` gives state Damaged, CRC 0xCAFEF00D, 0 buttons
    - `hello_version_2` gives one HelloMessage with Version 2
    - `hello_old_format` gives **no** message

  Run native tests: they fail to compile (old `encodeHello` signature). C# runs in CI later in this task.
- [ ] **Step 2 (GREEN, firmware):** Implement `encodeHello(const HelloInfo&)`: build `p[]` in the spec order (firmware length clamped to 32, CRC big-endian), `encodeFrame`. `Link::sendHello` builds a `HelloInfo` from `config_`. In `main.cpp`:
  - `#ifndef VOLCO_FW_VERSION #define VOLCO_FW_VERSION "dev" #endif`
  - `static_assert(sizeof(VOLCO_FW_VERSION) - 1 <= 32, "VOLCO_FW_VERSION too long")`
  - the `Link` config gets `chip 1, VOLCO_FW_VERSION, loaded.state, loaded.crc`
  - `sendHelloOnUdp` uses the same `HelloInfo`

  Native tests pass; `pio run -e esp32` succeeds.
- [ ] **Step 3 (GREEN, C#):**
  - **Parser `Hello` case:** read `version = payload[1]` (length ≥ 2). If it's ≠ 1, return a version-only HelloMessage. Otherwise:
    1. `chip = payload[2]`, `fwLen = payload[3]`; reject if `fwLen > 32` or the payload is too short at any read
    2. read the firmware as ASCII, then state, then the CRC (big-endian), then sliders, buttons and kinds
    3. reject if the remaining length ≠ buttons (extra trailing bytes are rejected too, to catch misframing)
  - **Encoder** to match.
  - **`ControllerInfoConfig`** gains `ChipFamily Chip` and `string FirmwareVersion = ""`, with `SameAs` comparing them.
  - **`OnHello`** stores them.
  - **Callers:** fix compile errors in test helpers that call the old `EncodeHello` (there are none outside `ProtocolGoldenTests`).

  Commit `feat(protocol): new Hello - chip, firmware id, config state and CRC (spec 5.2)`. Push; CI green.

### Task 2: Config frames on both sides, and `Link` routing (firmware)

**Files:** `frames.txt`, `firmware/lib/volco_protocol/src/volco_protocol.{h,cpp}`, `firmware/test/test_protocol/test_main.cpp`, `src/Volco.Core/Communication/{ControllerMessage.cs,ProtocolParser.cs,ProtocolEncoder.cs}`, `tests/Volco.Core.Tests/Communication/{ProtocolGoldenTests.cs,ProtocolEncoderTests.cs}`

**Interfaces:**
- Firmware:
  - `MSG_CONFIG_DATA = 0x05, MSG_CONFIG_RESULT = 0x06, MSG_CONFIG_READ = 0x87, MSG_CONFIG_BEGIN = 0x88, MSG_CONFIG_CHUNK = 0x89, MSG_CONFIG_END = 0x8A`
  - `size_t encodeConfigData(uint8_t seq, uint16_t total, uint16_t offset, uint32_t crc, const uint8_t* data, uint8_t n, uint8_t* out)`
  - `size_t encodeConfigResult(uint8_t seq, uint8_t code, uint8_t item, uint8_t field, uint8_t* out)`
  - ```cpp
    struct ConfigSink {
      virtual void configRead(uint8_t transport, uint8_t seq, uint32_t now) = 0;
      virtual void configBegin(uint8_t transport, uint8_t seq, uint16_t total, uint32_t crc, uint32_t now) = 0;
      virtual void configChunk(uint8_t transport, uint8_t seq, uint16_t offset, const uint8_t* data, size_t len, uint32_t now) = 0;
      virtual void configEnd(uint8_t transport, uint8_t seq, uint32_t now) = 0;
      virtual ~ConfigSink() = default; };
    ```
  - `Link::setConfigSink(ConfigSink*)`
  - `Link::onPayload(const uint8_t*, uint8_t len, uint32_t now, uint8_t transport = 0)`
  - Malformed config frames are dropped: Read/End `len < 2`; Begin `len < 8`; Chunk `len < 4`, where data may be empty and the receiver answers BAD_SEQUENCE.
- C#:
  - `MessageType.ConfigData = 0x05, ConfigResult = 0x06, ConfigRead = 0x87, ConfigWriteBegin = 0x88, ConfigWriteChunk = 0x89, ConfigWriteEnd = 0x8A`
  - `ConfigDataMessage(byte Seq, ushort Total, ushort Offset, uint Crc32, byte[] Data)`
  - `ConfigResultMessage(byte Seq, ConfigResultCode Code, byte Item, byte Field)` (`ConfigResultCode` is from `Volco.Core.Controller`)
  - `ProtocolEncoder.EncodeConfigRead(byte seq) → byte[]`
  - `ProtocolEncoder.EncodeConfigWrite(byte seq, byte[] blob) → byte[]`: the whole burst, Begin + 240-byte chunks + End; `ArgumentException` unless the blob is 1..1024 bytes

- [ ] **Step 1 (RED):** Append to `frames.txt`:
  ```
  config_read_seq7 AA 02 87 07 80
  config_data_empty_seq7 AA 0A 05 07 00 00 00 00 00 00 00 00 02
  config_data_seq7 AA 0D 05 07 00 03 00 00 11 22 33 44 32 02 08 7D
  config_result_ok_seq9 AA 05 06 09 00 FF FF 0F
  config_result_invalid_seq9 AA 05 06 09 05 02 07 0F
  config_write_begin_seq9 AA 08 88 09 00 04 FA E4 14 11 9E
  config_write_chunk_seq9 AA 08 89 09 00 00 32 02 08 64 DC
  config_write_end_seq9 AA 02 8A 09 83
  ```
  `FA E4 14 11` is the CRC-32 of `32 02 08 64`.
  - **Firmware tests:**
    - `encodeConfigData` / `encodeConfigResult` reproduce `config_data_*` / `config_result_*`
    - `test_link_routes_config_frames_with_transport`: deliver `config_read_seq7`, `config_write_begin_seq9`, `config_write_chunk_seq9` and `config_write_end_seq9` on transport 1 to a `Link` with a recording `ConfigSink`. The sink sees read(1,7), begin(1,9,4,0xFAE41411), chunk(1,9,0,[32 02 08 64]), end(1,9).
    - `test_config_frames_are_proof_of_life`: only config frames for 2.5 s → no `hostLost`.
    - `test_short_config_frames_dropped`: Begin with 5 bytes → no sink call.
  - **C# tests:**
    - `EncodeConfigRead(7)` = `config_read_seq7`
    - `EncodeConfigWrite(9, [32 02 08 64])` = begin + chunk + end goldens concatenated
    - a 1024-byte blob → 5 chunks (240×4 + 64), offsets 0/240/480/720/960, each frame's payload ≤ 255
    - blobs of 0 or 1025 bytes throw
    - parsing `config_data_seq7` → ConfigDataMessage(7, 3, 0, 0x11223344, [32 02 08]); `config_data_empty_seq7` → total 0 and empty data
    - `config_result_invalid_seq9` → (9, Invalid, 2, 7)
- [ ] **Step 2 (GREEN):** Implement the firmware encoders and decoders and the `Link` routing (`switch` cases after the existing ones, calling `configSink_` if set), and the C# parser cases, records and encoder. Native tests pass; commit `feat(protocol): config read/write frames with seq, routed with their transport (spec 5.3, 5.4)`; push; CI green.

### Task 3: The firmware config session (store, read reply, write, reboot); seed removed

**Files:** create `firmware/src/config_session.{h,cpp}`; modify `firmware/src/config_store.{h,cpp}` and `firmware/src/main.cpp`; delete `firmware/src/seed_config.h` and `firmware/tools/gen_seed_config.py`; add native test `firmware/test/test_config_session_frames/test_main.cpp` for a pure read-reply builder in `lib/volco_config` (`buildReadReply`).

**Interfaces:**
- `lib/volco_config` (pure): `size_t buildReadReply(uint8_t seq, const uint8_t* blob, uint16_t len, uint32_t crc, uint8_t* out, size_t cap)`. It writes all ConfigData frames (240-byte chunks, or one empty frame when `len == 0`) into `out` and returns the byte count, or 0 if `cap` is too small. `kMaxReadReply = 5 * (3 + 10 + 240)`.
- `config_store`:
  - `const uint8_t* config_store_blob(uint16_t& len)`: the stored bytes, `len` 0 when none or oversized
  - `bool config_store_save(const uint8_t* blob, size_t len)`: `putBytes == len`
  - `config_load` no longer seeds
- `config_session`: `void config_session_begin(volco::Link& link, const LoadedConfig& loaded)` (registers the `ConfigSink`), `void config_session_tick(uint32_t now)` (`BlobReceiver::tick`).
  - **Read:** `buildReadReply` → `transport_send(t, buf, n)`, one send.
  - **Write:** forward to `BlobReceiver`.
    - **REPLY** → `encodeConfigResult` on that transport.
    - **COMPLETE** → `decodeConfig`, `validateConfig(esp32Chip)`, `config_store_save`:
      - any failure → result DECODE_FAILED, INVALID (with item/field) or STORAGE_FAILED
      - success → result OK on the transport, then `Serial.flush()`, `delay(200)`, `ESP.restart()`

- [ ] **Step 1 (RED):** native `test_config_session_frames`:
  - a 3-byte blob → one ConfigData frame equal to `config_data_seq7`'s structure, built with seq 7 and CRC 0x11223344 so the bytes equal the golden
  - a 0-byte blob → `config_data_empty_seq7`
  - a 1024-byte blob → 5 frames with offsets 0..960; each frame parses and its CRC field equals the argument
  - `cap` too small → 0

  Fails to compile.
- [ ] **Step 2 (GREEN):** implement `buildReadReply` (in `volco_config.cpp`, using an `encodeConfigData` included from `volco_protocol`), `config_store` save/blob, `config_session`, and the wiring in `main.cpp`:
  - `config_session_begin(link, loaded)` after the `Link` exists
  - `link.onPayload(..., t)`
  - `config_session_tick(now)` in the loop

  Remove the seed (delete both files and the seeding branch in `config_load`). Native tests pass; `pio run -e esp32` succeeds.
- [ ] **Step 3:** Grep: `grep -rn "seed" firmware/src firmware/tools CLAUDE.md firmware/README.md` shows only historical mentions. Update the README and CLAUDE.md stage 3 seed paragraphs: an empty NVS now boots with defaults, and you set a config with `config_tool.py write` (stage 5: Volco). Commit `feat(firmware): config session - read reply, chunked write, validate, store, reboot; seed removed (spec 5.3-5.4, 6.6, 6.7)`.

### Task 4: `config_tool.py` (dev/test tool, serial and UDP)

**Files:** create `firmware/tools/config_tool.py`

**Interfaces:**
```
config_tool.py --port P hello                      # reset, print the parsed Hello
config_tool.py --port P read [--out f.json]        # ConfigRead; verify CRC; print/save JSON
config_tool.py --port P write preset.json          # burst; wait ConfigResult (3 s); on OK wait Hello (10 s), check state 1 + CRC
               [--corrupt-crc] [--no-end] [--total N]   # fault injection: BAD_CRC, timeout, TOO_LARGE
config_tool.py --udp BOARD_IP[:12346] --listen 12345 read|write ...   # same over UDP (run on the PC configured as pc_ip)
```
- **Serial ports:** guarded (only `1a86:7523`), with the same `check_usb_id` as `hello_check.py`. Opening the port resets the board, so the tool waits for the boot Hello before sending.
- **Encoding and decoding:** compiles the proto with `grpc_tools.protoc` (like `gen_config_vectors.py`) and uses `json_format` for JSON ↔ protobuf. `seq` starts at a random byte and increments per attempt.
- **Output:** each step and the exact frames sent and received (hex), so hardware runs leave evidence.

- [ ] **Step 1:** Write the tool. Self-test without hardware: `python firmware/tools/config_tool.py --selftest` encodes `proto/presets/diy-reference.json`, builds the burst, parses it back with its own frame parser, and checks that it equals the golden write-frame structure. It prints `SELFTEST OK`.
- [ ] **Step 2:** Commit `feat(tools): config_tool.py - read/write a controller's config over serial or UDP`.

### Task 5: Hardware verification (the screen board on this host)

Each step guards the port first.
- [ ] **Step 1:** Flash. NVS still holds the stage 3 screen config.
  - `config_tool.py hello`: state 1, firmware "dev", chip 1, the CRC of the stored blob, 0 sliders, buttons [3, 3].
  - `read`: the JSON equals the screen layout (buttons 32/33, screens on buses 0/1, i2c0 21/22, i2c1 25/26).
- [ ] **Step 2:** `write` that layout with screen brightness 40 and preset "screen-test-2".
  - Expect: result OK, a reboot, a Hello with state 1 and the new CRC, and `read` returning the new JSON.
  - Ask the user to confirm the screens are now dimmer. That is visible proof that a write took effect.
- [ ] **Step 3 (faults):**
  - `write` with a slider on pin 6 → INVALID `5 0 1`, no reboot, the next `hello` shows the old CRC
  - `--corrupt-crc` → BAD_CRC
  - `--no-end` → no result within 3 s, then the old CRC still in `hello` (timeout path)
  - `--total 1025` → TOO_LARGE
- [ ] **Step 4:** Restore brightness 255 (`write` the original layout). Record all evidence in the ledger.
- [ ] **Step 5 [USER-RUN, optional]:** UDP. On the Windows PC, run `config_tool.py --port COMx write` with a layout that adds `udp` (the user's SSID/password and the PC's IP). Then `config_tool.py --udp <board ip> --listen 12345 read`: it reads over Wi-Fi. This is checklist item 6 at the firmware level; the app-level version is stage 5.

### Task 6: Docs, push, CI

- [ ] Update `firmware/README.md`:
  - the Wire protocol table and Hello body (new fields)
  - a new "Config read and write" subsection (frames, `seq`, one send per reply/burst, the result codes table from spec §5.4, the reboot after OK)
  - `tools/` (`config_tool.py`)

  Update `CLAUDE.md`: the Wire Protocol table rows and Hello description; the config_tool command. Then push and wait for CI green.

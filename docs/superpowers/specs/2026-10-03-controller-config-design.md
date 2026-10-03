# Controller configuration and firmware installation: design

Date: 2026-10-03. Status: draft for review.

## 1. Goal

Today a user edits `firmware/src/config.h` to describe their hardware and preferences, then builds and flashes the firmware with PlatformIO. After this change:

- **Nobody compiles firmware.** CI builds one generic firmware image per chip family. Only the classic ESP32 is supported in this change.
- **Volco installs and updates the firmware** over USB.
- **The user describes the hardware in Volco.** A Controller page holds the sliders, buttons, indicators and I2C buses. It can be filled from a preset ("DIY reference", or "Volco PCB", which is a placeholder for now) or from scratch.
- **Preferences are set in Volco** (screen behaviour, input tuning, and the optional Wi-Fi/UDP link), and are saved to the controller.
- **The user can test the hardware live** from the same page without changing Windows audio.

Simplicity is a hard requirement. Every mechanism here takes the simplest form that is still robust: one config blob, one save path, and a reboot after every save.

## 2. Decisions already made

These were settled while brainstorming and are not reopened by this spec.

| # | Decision |
|---|---|
| D1 | One generic firmware per chip family. No user compiles. Only ESP32 (Xtensa LX6) now; more families are a later CI matrix entry. |
| D2 | The controller owns its configuration, stored in NVS. Volco reads it, edits it and writes it back, and keeps a **single** mirror copy (the last config it read or saved). |
| D3 | Button and slider *actions* stay in Volco's `config.json`, keyed by input number, as today. They are not part of the controller config. |
| D4 | The protocol stays **v1**. There are no existing users, so frames are redesigned freely. App, firmware and simulator update together. |
| D5 | Hardware is entered manually (a pin per thing). Learn mode is deferred to a later task. The app does no electrical pin validation; the firmware rejects only configs that **cannot work on the chip** (§6.4). |
| D6 | Presets: "DIY reference" (today's `config.h`), "Volco PCB" (a placeholder until its pins are known) and Blank. Choosing a preset fills the form, and the user may edit the result. |
| D7 | USB serial is always on. UDP over Wi-Fi is an **advanced, opt-in** setting. It needs an explicit SSID, password, PC IP address and PC port. There is **no discovery and no broadcast**: the controller talks only to the configured IP. |
| D8 | The ESP32 firmware drops its WebSocket client and the `TRANSPORT_LOCAL_DEBUG` mode. Volco keeps its WebSocket *server* for the simulator and alternative controllers. |
| D9 | The config blob is **Protocol Buffers, proto2 syntax** (explicit defaults): nanopb on the firmware, Google.Protobuf in C#, protobufjs in the simulator. Code is generated at build time from one `.proto`. The wire frames stay hand-written binary. (Editions are not used: nanopb 0.4.9.2 has only minimal editions support, mapping presence but not defaults.) |
| D10 | User-facing behaviour settings are visible; tuning settings sit under a collapsed "Advanced" area; protocol timing (host timeout, keepalive, commit fallback, reconnect interval) and the serial baud rate are fixed constants. |
| D11 | Every successful save reboots the controller. There is no live preview. |
| D12 | Firmware version detection is hybrid: Hello carries the firmware version (the normal case, no reset), and a **bootloader probe** over the ROM serial bootloader handles a board that does not answer Hello. |
| D13 | A **Test hardware** mode shows live raw input and lets the user drive each indicator, while the runtime ignores input for audio and actions. |
| D14 | Firmware install and update happen only over USB. OTA is out of scope. |

## 3. Non-goals

- Learn mode or automatic wiring detection.
- Pin validation beyond §6.4: pull-up resistors, strapping-pin boot behaviour and wiring mistakes are not checked.
- OTA updates, UDP discovery, mDNS, WebSocket on the ESP32.
- More than one mirror copy, or identifying controllers by MAC address.
- Chip families other than the classic ESP32.
- Encrypting the Wi-Fi password, either in NVS or in Volco's `config.json`. It is stored in plain text, as it is in `config.h` today. This is called out for review in §14.
- Restricting which LAN hosts Volco's UDP listener accepts. That behaviour is unchanged.

## 4. Overview

```
            ┌──────────────────────────── Volco (Windows) ─────────────────────────────┐
            │ Controller page (Volco.App)                                               │
            │   status & firmware │ hardware │ test │ behaviour │ advanced              │
            │        │                 │          │                                      │
            │ FirmwareService     ControllerConfigService   VolcoRuntime (test mode)    │
            │  (EspDotNet,         (read / save / mirror,    ControllerLink (session)    │
            │   bundled images)     protobuf codec)                                      │
            └────────┬──────────────────────┬──────────────────────────────────────────┘
          ROM bootloader                 Volco protocol v1 (serial, or UDP)
          (flash, probe)                    │
            ┌────────┴──────────────────────┴──────── ESP32 firmware ─────────────────┐
            │ boot: NVS "volco"/"cfg" → volco_config decode + validate → RuntimeConfig │
            │       (or built-in defaults)                                             │
            │ Link (Hello with fw version + config CRC; config read / write frames)    │
            │ inputs, indicators, screens, transports — all sized from RuntimeConfig   │
            └──────────────────────────────────────────────────────────────────────────┘
```

New and changed pieces:

| Area | Piece | Kind |
|---|---|---|
| Repo root | `proto/volco_config.proto`, `proto/presets/*.json` | new |
| Firmware | `lib/volco_config` (pure C++: decode, defaults, validate, chip table, chunked receiver, blob CRC) | new |
| Firmware | `lib/volco_protocol` (new Hello body, config frames) | changed |
| Firmware | `src/` (runtime-sized tables, NVS store, dual transport, reboot after save) | changed |
| Firmware | `config.h` (only fixed constants remain) | reduced |
| App, Core | `Volco.Core/ControllerConfig/` (codec wrapper, validation, presets, service, mirror) | new |
| App, Core | `Volco.Core/Firmware/` (`IFirmwareFlasher`, catalog, probe) | new |
| App, Core | `Communication/` (Hello parsing, config frames encode/parse) | changed |
| App, Core | `Runtime/VolcoRuntime` (test mode, connection suspend), `ControllerLink` (config frames through the outbox) | changed |
| App, UI | `Views/ControllerPage`, `ViewModels/Controller*` | new |
| App, UI | `ConnectionViewModel` (the baud setting is removed) | changed |
| Windows | Embedded firmware images and manifest | new |
| Simulator | Config read and write in memory, new Hello | changed |
| CI | Firmware artifact job feeding the Windows build and the release | changed |

## 5. Wire protocol changes (v1, redesigned)

The frame format is unchanged: `0xAA | length | type | body | XOR checksum`, `length` 1–255 (type + body), big-endian integers. `kMaxPayload` stays 255.

### 5.1 Message table (complete, after this change)

| Type | Name | Direction | Body |
|---|---|---|---|
| `0x01` | Hello | C→PC | §5.2 |
| `0x02` | Alive | C→PC | none |
| `0x03` | Slider | C→PC | `index` u8, `value` u16 (0–4095) |
| `0x04` | Button | C→PC | `index` u8, `pressed` u8 (1/0) |
| `0x05` | ConfigData | C→PC | §5.3 |
| `0x06` | ConfigResult | C→PC | §5.4 |
| `0x81` | HelloRequest | PC→C | none |
| `0x82` | Heartbeat | PC→C | none |
| `0x83` | Led | PC→C | `button` u8, `value` u8 |
| `0x84` | Screen | PC→C | unchanged (firmware README "Screen body") |
| `0x85` | Begin | PC→C | none |
| `0x86` | Commit | PC→C | none |
| `0x87` | ConfigRead | PC→C | none |
| `0x88` | ConfigWriteBegin | PC→C | `total` u16, `crc32` u32 |
| `0x89` | ConfigWriteChunk | PC→C | `offset` u16, `data` (1–240 bytes) |
| `0x8A` | ConfigWriteEnd | PC→C | none |

Every PC→C frame, including the config frames, counts as proof of life for the host timeout, as every valid frame does today.

### 5.2 Hello body

```
version        u8     protocol version, 1
chip           u8     chip family: 1 = ESP32 (0 reserved/unknown)
fwLen          u8     0–32
fw             fwLen bytes ASCII: firmware build id (§9.2), e.g. "1.2.0" or "0.0.0-ci+3f9c2a1"
configState    u8     0 = no stored config (running built-in defaults)
                      1 = running the stored config
                      2 = a stored config exists but failed to decode or validate (running defaults)
configCrc      u32    CRC-32 of the stored blob (§6.5); 0 when configState is 0
sliders        u8     0–16
buttons        u8     0–32
kinds          buttons bytes: 0 none, 1 LED, 2 LED pair, 3 screen
```

- The maximum body is 1+1+1+32+1+4+1+1+32 = 74 bytes.
- Hello is still sent at boot, in reply to HelloRequest, and on the first valid frame after a host timeout. These rules are unchanged.
- `sliders`, `buttons` and `kinds` still drive everything else in the app. The runtime never needs to decode the config blob for normal operation.
- When `configState` is 2, `configCrc` is the CRC of the stored, broken blob, so the app can tell that blob apart from its mirror. The UI shows "The controller's saved setup is damaged" and offers to apply the mirror or start fresh.

### 5.3 ConfigRead and ConfigData

The PC sends `ConfigRead`. The controller replies with the **stored blob bytes exactly as stored**, split into ConfigData frames sent back-to-back:

```
total          u16    blob size in bytes; 0 = no stored config
offset         u16    offset of this chunk
crc32          u32    CRC-32 of the whole blob (same value as Hello's configCrc)
data           0–240 bytes
```

- If `total` is 0, exactly one frame is sent, with no data and a `crc32` of 0.
- Otherwise the chunks are sent at offsets 0, 240, 480, … in order. Every chunk except the last carries 240 bytes.
- If a stored blob is damaged (configState 2), it is still returned, so the user can see what was there if it decodes on the PC side. The app treats it as advisory.
- The firmware does not keep transfer state for reads; each ConfigRead produces a full fresh reply.

The app assembles the chunks and accepts the reply when all of these hold:

1. The chunks arrived in order (offset = bytes received so far).
2. Every chunk carries the same `total` and `crc32`.
3. The assembled size equals `total`.
4. The CRC-32 of the assembled bytes equals `crc32`.

Anything else (a gap, a mismatch, or no complete reply within **2 s** of the ConfigRead or of the last chunk) is a failed read. The app retries the whole read up to **3 times** in total, then reports "Couldn't read the controller's setup". A ConfigData frame that arrives while no read is pending is ignored.

### 5.4 Config write

```
PC: ConfigWriteBegin {total, crc32}
PC: ConfigWriteChunk {offset 0,   data[240]}
PC: ConfigWriteChunk {offset 240, data[…]}
…
PC: ConfigWriteEnd
C:  ConfigResult {code, item, field}
C:  (if code == OK) reboots ~200 ms later; on boot sends Hello with the new configCrc
```

**ConfigResult body:** `code` u8, `item` u8, `field` u8.
- `item` is the slider or button index the error concerns, or `0xFF` when none applies.
- `field` is a §6.4 field code, or `0xFF`.

| Code | Name | Meaning |
|---|---|---|
| 0 | OK | Stored. The controller reboots. |
| 1 | BAD_SEQUENCE | A chunk out of order (offset ≠ bytes received), ConfigWriteEnd before `total` bytes, a chunk overrunning `total`, or ConfigWriteChunk/ConfigWriteEnd with no transfer open. |
| 2 | BAD_CRC | The CRC-32 of the received bytes ≠ `crc32` from Begin. |
| 3 | TOO_LARGE | `total` > `kMaxConfigBlob` (1024). Sent in reply to Begin, which closes the transfer. |
| 4 | DECODE_FAILED | nanopb could not decode the blob, or a string or repeated field exceeds its maximum. |
| 5 | INVALID | Decoded, but violates a §6.4 rule. `item` and `field` say where. |
| 6 | STORAGE_FAILED | The NVS write or commit failed. The old config is unchanged. |

**Firmware receiver rules** (`volco_config::BlobReceiver`, host-tested):

- `ConfigWriteBegin` always (re)starts a transfer. It discards any open transfer, records `total` and `crc32`, and sets received = 0.
  - A `total` greater than 1024 → reply TOO_LARGE and close.
  - `total` = 0 means **factory reset**: on the matching ConfigWriteEnd (with no chunks), the firmware erases the stored config, replies OK, and reboots into defaults.
- `ConfigWriteChunk` with no open transfer → reply BAD_SEQUENCE.
- A chunk whose `offset` ≠ received, or whose data would exceed `total` → reply BAD_SEQUENCE and close.
- `ConfigWriteEnd` with no open transfer, or with received ≠ `total` → reply BAD_SEQUENCE and close.
- `ConfigWriteEnd` with all bytes received: check CRC → decode → validate → store → reply → reboot. The first failure produces its code, and the transfer closes.
- **Timeout:** an open transfer with no config frame for **2000 ms** closes silently. The app's own timeout (below) covers it.
- Exactly **one** ConfigResult is sent per transfer that ends with an error or with an End.
- A transfer does not pause the rest of the firmware. Inputs are still reported, and Led and Screen frames still apply.

**App write rules** (`ControllerConfigService`):

- **Send:** Begin, all chunks and End are enqueued together through `ControllerLink`'s outbox. They are sent in order with no per-chunk acknowledgements.
- **Wait for ConfigResult** for up to **3 s** after End.
  - **No result:** retry the whole write up to **3 times** in total, then fail with "The controller didn't answer".
  - **BAD_SEQUENCE or BAD_CRC:** a transport error (likely a dropped UDP datagram). Retry as above.
  - **TOO_LARGE, DECODE_FAILED, INVALID or STORAGE_FAILED:** fail immediately with the specific message (§8.7). These are not retried.
  - **OK:** wait for the next Hello for up to **10 s**.
- **Confirm the reboot:**
  - The save is confirmed only if that Hello has `configState` = 1 and `configCrc` = the CRC of the bytes sent.
  - Any other Hello fails with "The controller restarted but isn't running the new setup" (for example, if it reports `configState` 2).
  - No Hello within 10 s fails with "The controller didn't come back after restarting".
- **The mirror is updated only when the save is confirmed.**
- Only one read or write is in flight at a time. The service rejects a second request while one is running; the UI disables Save, Revert and Read during it.

**Throughput.** A full 1024-byte blob is 5 chunks, about 1.1 KB on the wire: about 0.1 s at 115200 baud, and a few datagrams over UDP. A screen redraw can block the loop for about 30 ms while a write is arriving, so **the firmware's serial RX buffer is raised from 1024 to 2048 bytes**, enough to hold a whole write. Decode, validate and store happen on End, after all the bytes have arrived.

### 5.5 Which transport the controller answers on

USB serial is always on. When UDP is enabled, each transport has its **own** `FrameParser`, so bytes from one cannot corrupt a frame from the other. Both parsers feed the same `Link`.

- **Active transport:** the transport that most recently delivered a valid frame. Every outgoing frame (Hello, Alive, Slider, Button, ConfigData, ConfigResult) goes to the active transport only.
- At boot the active transport is serial. The boot Hello goes to serial, and also to UDP if UDP is enabled and Wi-Fi is up.
- UDP frames are accepted **only from the configured PC IP**. Datagrams from any other source address are dropped before parsing.
- The controller sends UDP to `pcIp:pcPort` and listens on `localPort`.

This keeps the recovery path: a controller with wrong Wi-Fi settings is still fully usable over USB, because the serial frames make serial the active transport.

## 6. The controller config

### 6.1 Schema: `proto/volco_config.proto`

```proto
syntax = "proto2";
package volco.config;
option csharp_namespace = "Volco.Core.ControllerConfig.Proto";

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
  optional string password = 3;                     // 0..64 bytes
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

**nanopb options** (`proto/volco_config.options`):

```
volco.config.ControllerConfig.sliders  max_count:16
volco.config.ControllerConfig.buttons  max_count:32
volco.config.ControllerConfig.preset   max_size:25
volco.config.Udp.ssid                  max_size:33
volco.config.Udp.password              max_size:65
```

(`max_size` counts the NUL terminator.)

Notes:

- **Defaults live only in the `.proto`.** Neither side hand-codes them. The app reads defaults from the generated C# properties (proto2 presence: `HasX` / `ClearX`), and the firmware gets them from nanopb's generated initialiser.
- **The ranges in the comments are normative.** They are enforced by §6.4 in the firmware and mirrored by the UI's control limits.
- **Sizes:** the largest valid blob (16 sliders, 32 buttons each with an LED pair, both buses, UDP with maximum strings, every behaviour field) is under 700 bytes. `kMaxConfigBlob` = 1024 leaves room to grow, and a test asserts the maximal blob fits (§12.1).
- **Times are seconds** where the old constants were minutes, and milliseconds where they were sub-second. The firmware converts to milliseconds at load.

### 6.2 The DIY reference preset

`proto/presets/diy-reference.json` uses the protobuf JSON mapping and is equal to today's `config.h`:

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

`proto/presets/volco-pcb.json` is `{"preset": "volco-pcb"}` plus a top-level marker that the app's preset loader reads. Its exact form is decided in the plan: the protobuf JSON parser rejects unknown fields, so the marker must sit in a wrapper object, not inside `ControllerConfig`. While it is a placeholder, the preset picker shows "Volco PCB (coming soon)", disabled.

**A preset carries only the hardware fields** (`sliders`, `buttons`, `i2c0`, `i2c1`, `preset`). Applying it replaces exactly those fields in the form. Behaviour, input tuning and UDP fields are kept.

The presets are embedded as resources in `Volco.Core`. A test asserts that each non-placeholder preset passes §6.4 validation and that the DIY reference matches the table above.

### 6.3 Firmware runtime model

`volco_config` decodes into nanopb's generated `volco_config_ControllerConfig` struct, which has fixed-size arrays (static allocation). Then `toRuntime()` produces a `RuntimeConfig` holding plain values with the defaults applied, the times converted to milliseconds, and the I2C buses that are actually used. `main.cpp`, `indicators.cpp`, `screens.cpp` and `transport.cpp` read only `RuntimeConfig`. They never read the protobuf struct or `#define`s.

**What stays in `config.h` (fixed constants):**
- `SERIAL_BAUD` 115200
- `HOST_TIMEOUT_MS` 3000
- `KEEPALIVE_MS` 1000
- `COMMIT_FALLBACK_MS` 500
- `RECONNECT_INTERVAL_MS` 3000
- `kMaxConfigBlob` 1024
- the serial RX buffer size

Everything else in today's `config.h` moves into the schema. `VOLCO_SCREENS_CHECK` and the transport `#define`s are removed.

**Built-in defaults** (no stored config, or a broken one) are the protobuf defaults with no sliders, no buttons, no I2C buses and UDP off. The controller then sends Hello with 0 sliders and 0 buttons, answers config frames, and does nothing else.

### 6.4 Validation (firmware, authoritative)

`volco_config::validate(const ControllerConfig&, const ChipInfo&) → {code, item, field}` returns OK or the **first** violation, in the order below. The app runs the same rules before sending (§8.4), but the firmware's answer is the one that counts.

**Field codes** (for ConfigResult `field`):

| Code | Field | Code | Field |
|---|---|---|---|
| 1 | slider pin | 9 | i2c bus pins (sda/scl) |
| 2 | button pin | 10 | i2c clock |
| 3 | LED pin | 11 | udp ssid |
| 4 | LED pair pin A | 12 | udp password |
| 5 | LED pair pin B | 13 | udp pc_ip |
| 6 | screen bus | 14 | udp port |
| 7 | screen address | 15 | screen behaviour value |
| 8 | i2c bus missing | 16 | input tuning value |

**Rules, checked in this order:**

1. **Counts:** sliders ≤ 16 and buttons ≤ 32 (also enforced by the decoder's `max_count`).
2. **Each slider:** `pin` present, an existing GPIO, not reserved (rule 8), and ADC-capable.
3. **Each button:** `pin` present, an existing GPIO, not reserved, and input-capable.
4. **Each indicator:**
   - LED / LED pair: pins present, existing, not reserved, and **output-capable**. A pair's pin A ≠ pin B.
   - Screen: `bus` ∈ {0, 1} and `address` ∈ {0x3C, 0x3D}. At most 2 screens per bus, and (bus, address) is unique.
5. **I2C:** every bus used by a screen has an `I2cBus` with `sda` and `scl` present, existing, not reserved, output-capable, and distinct, and `clock_hz` in range. An `I2cBus` that no screen uses is ignored entirely: neither validated nor started.
6. **Pin uniqueness:** every pin used by a slider, a button, an LED, or a *used* I2C bus is used exactly once across the whole config.
7. **UDP**, if `enabled`:
   - `ssid` is 1–32 bytes, `password` is 0–64 bytes, `pc_ip` is present and ≠ 0, and the ports are in range.
   - **No slider uses an ADC2 pin** (ADC2 cannot be read while Wi-Fi is on). This is reported as field 1 on the offending slider.
8. **Reserved pins:** this is a chip fact, not a user preference.

**The chip table for ESP32** (`ChipInfo`, data in `lib/volco_config`, host-tested):

| Set | Pins |
|---|---|
| exists | 0–19, 21–23, 25–27, 32–39 |
| reserved | 1, 3 (UART0, the always-on USB serial), 6–11 (SPI flash) |
| input-only (not output-capable) | 34, 35, 36, 37, 38, 39 |
| ADC1 | 32–39 |
| ADC2 | 0, 2, 4, 12, 13, 14, 15, 25, 26, 27 |
| ADC-capable | ADC1 ∪ ADC2 |
| input-capable | exists − reserved |
| output-capable | exists − reserved − input-only |

**Range violations** in `ScreenBehaviour` / `InputTuning` are INVALID with field 15 or 16 and `item` = the protobuf field number. The firmware does **not** clamp silently, so the app and firmware never disagree about the effective value.

The DIY reference passes all of this. GPIO 36 and 34 are input-only ADC1 pins used as sliders, which is fine. GPIO 12 is a strapping pin used as an LED and is allowed, per D5.

### 6.5 Blob identity and the CRC

- **Algorithm:** CRC-32 (IEEE 802.3, the zlib `crc32`: polynomial 0xEDB88320 reflected, init 0xFFFFFFFF, final XOR 0xFFFFFFFF).
- **Byte identity, not semantic identity.** The CRC covers the **exact stored bytes**. Protobuf encodings are not canonical between implementations, so the app never re-encodes a config to compare it.
  - The mirror stores the exact bytes the app last sent, or last read.
  - The controller stores exactly the bytes it received. It never re-encodes them.
  - "Same config" means "same bytes".
- Test vector: the CRC-32 of ASCII `123456789` is `0xCBF43926`. Both implementations test it.

### 6.6 Storage

- **Location:** NVS namespace `volco`, key `cfg`, as a blob, written through Arduino `Preferences`.
- **Atomicity:** NVS replaces a blob key atomically. A power loss during the write leaves the old value or the new one. The firmware writes only after decode and validate succeed, then calls `end()` to commit, then replies.
- **Factory reset** removes the key.
- **Boot:**
  - Read the key. If absent → defaults, configState 0.
  - If present → decode and validate. If either fails → defaults, configState 2, keeping the CRC of the stored bytes. Otherwise → run the config, configState 1.
- **NVS init failure:** if NVS cannot initialise (`ESP_ERR_NVS_NO_FREE_PAGES` or `ESP_ERR_NVS_NEW_VERSION_FOUND`, for example after a foreign firmware), the firmware erases the NVS partition, re-initialises it, and boots with defaults. The Arduino core's `initArduino()` already does this; the plan verifies it on the pinned core version, and adds the erase-and-retry itself if not.
- **Partition table:** `platformio.ini` pins `board_build.partitions = default.csv`, so flash offsets are stable. The NVS partition is at 0x9000 (0x5000 bytes).

### 6.7 Reboot after save

After replying OK, the firmware:

1. flushes the active transport (`Serial.flush()`, or ends the UDP packet)
2. waits about 200 ms so the reply leaves
3. calls `ESP.restart()`

On serial the USB bridge stays enumerated, so Volco's COM port stays open. The parser already resynchronises after boot ROM output (firmware README). The boot Hello then confirms the save.

## 7. Firmware changes in detail

- **`main.cpp`:**
  - The arrays are sized by `kMaxSliders` (16) and `kMaxButtons` (32) and iterate over the runtime counts.
  - Debounce, sampling, filter (`filter_weight_pct / 100.0`) and deadband come from `RuntimeConfig`.
  - `setup()`: load the config (§6.6), init the pins from it, init the transports, then `Link` with the runtime counts and kinds.
- **`screens.cpp`:**
  - The `constexpr` counting and the `static_assert`s become runtime code. The assertions are replaced by validation rules 4 and 5.
  - Panels are created at runtime, one `U8G2_SSD1306_128X64_NONAME_F_HW_I2C` or `..._2ND_HW_I2C` per screen, by bus, allocated once in `setup()`.
  - The timings come from `RuntimeConfig`.
- **`indicators.cpp`:** unchanged logic; reads `RuntimeConfig`.
- **`transport.cpp`** is rewritten:
  - Serial is always initialised.
  - UDP is initialised only if `udp.enabled`: `WiFi.begin(ssid, password)`, a socket bound to `local_port`, the source-IP filter, and the active-transport rule (§5.5).
  - `transport_reconnect()` re-calls `WiFi.begin` when Wi-Fi is down and UDP is enabled.
  - Wi-Fi is explicitly off when UDP is disabled.
  - The WebSocket and LOCAL_DEBUG code is deleted, along with the `links2004/WebSockets` dependency.
- **`lib/volco_protocol`:** `encodeHello` takes the new fields. There are encoders for ConfigData and ConfigResult and decoders for the PC config frames. `Link` routes `0x87`–`0x8A` to a new `ConfigSink` interface implemented in `main.cpp`, which connects `BlobReceiver`, the store and the reboot.
- **`lib/volco_config`** (new, pure C++, no Arduino):
  - `crc32`
  - `ChipInfo` and the ESP32 table
  - `decode(bytes) → ControllerConfig` (nanopb)
  - `validate`
  - `toRuntime`
  - `BlobReceiver` (§5.4), with time passed in
- **Build version:** `-DVOLCO_FW_VERSION=\"…\"` is passed by CI (§9.2). The default for local builds is `"dev"`.
- **`platformio.ini`:**
  - Rename `denky32` → `esp32`.
  - Add the nanopb library (`nanopb/Nanopb`) with `custom_nanopb_protos = +<../proto/volco_config.proto>` and `custom_nanopb_options = --options-path=../proto`. The exact keys are verified by Spike S2.
  - Remove the `*_check` environments.
  - Add `board_build.partitions = default.csv`.
  - Add a post-build script that writes the firmware manifest (§9.1).
  - The `native` environment also builds `lib/volco_config` with nanopb.

## 8. App changes in detail

### 8.1 Core: protocol

- **`HelloMessage`** gains:
  - `Chip` (enum `ChipFamily { Unknown = 0, Esp32 = 1 }`)
  - `FirmwareVersion` (string)
  - `ConfigState` (enum `None = 0, Stored = 1, Damaged = 2`)
  - `ConfigCrc` (uint)
- **`ProtocolParser`** parses the new Hello, ConfigData and ConfigResult. A Hello with `fwLen` > 32, or with a body shorter than its lengths claim, is dropped, as malformed Screen frames are today.
- **`ProtocolEncoder`** adds `EncodeConfigRead`, `EncodeConfigWrite(byte[] blob) → IReadOnlyList<byte[]>` (Begin, chunks, End) and `EncodeFactoryReset`.
- **`ControllerInfoConfig`** gains `Chip` and `FirmwareVersion`, so the Controller page can show them while the controller is unplugged.

### 8.2 Core: `ControllerConfig/`

- **`ControllerConfigCodec`:** a thin wrapper over the generated `Volco.Core.ControllerConfig.Proto.ControllerConfig`, providing `Encode(model) → byte[]`, `Decode(byte[]) → model` (throws `ControllerConfigFormatException` on bad input), and `Crc32(byte[])`.
- **`ControllerConfigValidator`:** the §6.4 rules and the ESP32 chip table, ported. It returns the same `(code, item, field)` triple the firmware would. The shared golden vectors (§12.1) keep the two in step.
- **`ControllerPresets`:** loads the embedded `proto/presets/*.json` with `Google.Protobuf.JsonParser`.
- **`ControllerConfigService`**, constructed by `VolcoRuntime`:
  - State: `Idle | Reading | Writing`.
  - `Task<ReadResult> ReadAsync(CancellationToken)`: the §5.3 read with retries.
  - `Task<SaveResult> SaveAsync(byte[] blob, CancellationToken)`: the §5.4 write with retries and reboot confirmation, which updates the mirror on success.
  - `Task<SaveResult> FactoryResetAsync(...)`.
  - It sends through `ControllerLink`, using a new `ControllerLink.SendRaw(IReadOnlyList<byte[]>)` that enqueues frames on the existing single-consumer outbox in order. It receives ConfigData, ConfigResult and Hello from the runtime's message routing.
  - Cancellation, or a `ControllerLink.Detach` (connection change or suspend), fails an in-flight operation with "Connection lost" and leaves the mirror untouched.
- **Mirror:** `AppConfig.ControllerConfigBlob` (`string?`, base64 of the exact bytes). It is written through `ConfigManager` like every other field.
  - Config schema v5, which adds `controllerConfigBlob` and removes `connection.serial.baudRate`.
  - The `ConfigMigrator` step is trivial. There are no users (D4), but the migrator chain stays consistent.

### 8.3 What happens on each Hello (`ControllerConfigService.OnHello`)

| Hello `configState` | Mirror | Action |
|---|---|---|
| 1 (stored), CRC = mirror CRC | any | Nothing. This is the usual case, with no read. |
| 1, CRC ≠ mirror CRC | present | Read the config, then show the banner *"This controller has a different setup."* with **Use the controller's** (adopt the read bytes as the mirror) and **Apply mine** (save the mirror bytes to the controller). |
| 1 | absent | Read and adopt silently as the mirror. |
| 0 (none) | present | Banner *"This controller isn't set up."* with **Apply my saved setup** (save the mirror) and **Set up from scratch** (open the Hardware section, blank or from a preset). |
| 0 | absent | Banner *"Set up your controller"*, which opens the Hardware section. |
| 2 (damaged) | present | Banner *"The controller's saved setup is damaged."* with **Apply my saved setup** and **Set up from scratch**. |
| 2 | absent | The same banner without the apply option. |

- **When the check runs:** only when a Hello arrives while no write is in flight. During a write, the Hello is consumed by the write's confirmation step (§5.4).
- **Firmware version mismatch:** a Hello whose `FirmwareVersion` ≠ the bundled build id (§9.2) also shows *"Firmware update available"* on the Controller page and as a tray balloon. There is never more than one balloon per app session per controller version.
- **Protocol version mismatch** (Hello `version` ≠ 1): the link does not go Ready. The page shows *"This controller's firmware is incompatible. Update it to use it."* and offers only Update.

### 8.4 Editing model

- The Controller page edits a **draft**: a C# `ControllerConfig` (generated) cloned from the mirror. With no mirror, the draft starts empty.
- **Dirty** = the draft encodes to different bytes than the mirror. This is the same codec on the same side, so it is deterministic.
- **Save:**
  - Validate locally with `ControllerConfigValidator`. On failure, highlight the field and do not send.
  - Otherwise encode, then `SaveAsync`.
  - On confirmation the draft becomes the new mirror; the UI shows *"Saved. The controller restarted with the new setup."*
- **Revert** resets the draft to the mirror.
- **Editing requires a connected controller in the Ready state.** While disconnected the page is read-only and shows the mirror.

### 8.5 Test mode (`VolcoRuntime`)

**API:**

```csharp
public bool InTestMode { get; }
public IDisposable EnterTestMode();                 // dispose = exit
public event EventHandler<RawInputEventArgs>? RawInput; // raised for every Slider/Button message, always
public void SetTestIndicator(int button, TestIndicator state); // Off, On, A, B, TestScreen
```

**While in test mode:**

- `OnMessageReceived` raises `RawInput` and **does not** pass Slider or Button messages to `SliderProcessor`, `ButtonPressDetector` or `ActionFactory`. No audio call or action can happen, and there are no OSD pop-ups.
- `ControllerLink`'s `compute` delegate is swapped to a test-mode compute:
  - each LED button shows its `TestIndicator` (default Off)
  - each screen button shows a test screen: top text "Test", main text "Button N", the generic icon
  - `SetTestIndicator` changes the state and calls `Push()`
  - Dedup and the outbox work unchanged.
- **Exit** (dispose): restore the normal compute, then `PushAll()`. Slider positions do not jump volume, because volume changes only on the next Slider message, as today.
- **Exit is guaranteed:** test mode exits when the page is left, when the settings window closes, on connection loss or `Detach`, and on runtime dispose. The page holds the `IDisposable`.

`RawInput` is raised in normal mode too. The Test section shows live values even outside test mode, but its indicator controls need test mode.

### 8.6 Firmware install, update, probe (`Volco.Core/Firmware/`)

**`IFirmwareFlasher`** (the seam; the fake is in tests):

```csharp
Task<ProbeResult> ProbeAsync(string port, FirmwareImage bundled, IProgress<FlashProgress>, CancellationToken);
Task FlashAsync(string port, FirmwareImage image, FlashMode mode, IProgress<FlashProgress>, CancellationToken);
enum FlashMode { Install /* erase whole flash, then write */, Update /* write parts only; NVS kept */ }
```

The implementation is `EspDotNetFirmwareFlasher`.

- **Where it lives** is decided by Spike S1: `Volco.Core` if EspDotNet uses only `System.IO.Ports` and passes `ArchitectureTests`, otherwise `Volco.Windows` through `PlatformServices`.
- **Connection:** DTR/RTS auto-reset into the ROM bootloader, sync, chip detect, then upload the stub loader.
- **Flash:**
  - Check the detected chip ∈ the image's chip family; otherwise fail with "This board is an ESP32-S3; this build of Volco only includes firmware for ESP32".
  - Install only: erase the whole flash.
  - Write each part at its offset from the manifest (§9.1).
  - Verify each part's MD5 with `SPI_FLASH_MD5`.
  - Hard-reset via RTS.
- **Update never writes 0x9000–0xDFFF** (NVS), so the config survives. The parts are written separately at their offsets, **never as one merged image**, because a merged image fills the gaps between parts with 0xFF and would wipe NVS.
- **Probe:**
  - Enter the bootloader and detect the chip.
  - `SPI_FLASH_MD5` over `[app offset, app offset + bundled app size)`, compared with the bundled app image's MD5.
  - Hard-reset.
  - Result: `NoDevice | UnsupportedChip(name) | VolcoMatches | DifferentFirmware`. `DifferentFirmware` covers blank flash, other firmware, or another Volco version.

**`FirmwareCatalog`:** the bundled images and the manifest from an `IFirmwareSource` (embedded resources in `Volco.Windows`; empty in tests and local builds). `HasFirmware` is false when the build has no bundled firmware; the page then shows "This build doesn't include controller firmware" and disables Install and Update.

**`FirmwareService`** orchestrates one operation:

1. `using var suspend = runtime.SuspendConnection();`. This detaches `ControllerLink`, disposes the connection and frees the COM port. Test mode and config operations end.
2. Call the flasher with progress shown.
3. Dispose `suspend`, which reconnects with the current connection config.
4. **After Install or Update,** wait up to 10 s for a Hello. Success means `FirmwareVersion` = the bundled build id. The page then offers *"Test your controller now"*.

**Rules:**

- Install and Update require the connection type to be **Serial**. Over UDP or WebSocket the buttons explain "Connect the controller with USB and switch the connection to Serial to update it".
- **Port:** the configured `Serial.ComPort`, editable in the dialog. The list comes from `SerialPort.GetPortNames()`.
- **When the probe runs:**
  - on the user's **Check firmware** click
  - offered (not automatic) when the serial port is open but no Hello arrived within 5 s: *"No Volco controller answered on COM3. Check what's connected?"*
  - The probe resets the board, so it never runs automatically.
- **Probe result → UI:**
  - `NoDevice` → "No ESP32 found on COM3".
  - `UnsupportedChip` → the message above.
  - `VolcoMatches` → "Volco firmware is installed but didn't answer. Reinstall?" (Install).
  - `DifferentFirmware` → "This ESP32 doesn't have this version of Volco's firmware." (Install).
- **Cancellation:** only before writing starts. Once erase or write has started, the dialog cannot be cancelled. A failure mid-write leaves the board in the bootloader-recoverable state, and the dialog says "Install failed: <reason>. The controller won't work until the install succeeds. Try again."

### 8.7 The Controller page (UI)

A sixth settings page, **Controller**, after Connection. The sections, top to bottom:

**1. Status and firmware**
- Connection state, chip, firmware version, bundled version.
- Buttons: Install firmware…, Update firmware (shown when the versions differ), Check firmware.
- The §8.3 banners appear here.

**2. Hardware**
- **Preset picker:** DIY reference / Volco PCB (coming soon, disabled) / Blank.
  - Choosing a preset when the hardware draft is non-empty asks "Replace the current hardware with <preset>?"
  - Under the picker: "Based on DIY reference" or "Based on DIY reference (modified)". "Modified" means the hardware fields differ from the preset's.
- **Sliders:** a reorderable list (existing `IReorderableList` pattern). Each row: "Slider N", pin (number box), remove. Then "Add slider".
- **Buttons:** a reorderable list. Each row:
  - "Button N" and the pin.
  - Indicator: None / LED / LED pair / Screen.
  - LED: pin, "Lights when the pin is: High / Low".
  - LED pair: pin A, pin B, polarity.
  - Screen: bus 0/1, address 0x3C/0x3D.
  - Remove.
  - Then "Add button".
- **I2C buses:** bus 0 and bus 1 rows (SDA, SCL), shown only when a screen uses that bus. The clock is in Advanced.
- **Validation errors** appear inline on the offending field, from the local validator (§8.4) or from a ConfigResult INVALID (`item` and `field` map to the row and field).

**3. Test** (§8.5)
- **Start test / Stop test.**
- A live bar plus raw value per slider, and a lamp per button lit while pressed.
- Per LED button: Off / On (LED) or Off / A / B (pair). Per screen button: Show test screen.
- A note: "While testing, the controller doesn't change volume or run actions."

**4. Behaviour**
- Screen brightness.
- Dim when idle (on/off, after, dimmed brightness).
- Turn off when idle (on/off, after).
- Pixel shift (on/off, every).
- Wake press isn't sent to the PC.
- Scrolling (hold, speed, end hold), slide duration.
- Shown only when a button has a screen. Otherwise a single line explains these apply to screens.

**5. Advanced** (collapsed by default)
- Input tuning: debounce, slider sample interval, smoothing (%), deadband.
- I2C clock per bus.
- **Wi-Fi (UDP):** enable; SSID; password (masked, with a show toggle); PC IP address; PC port; controller port. A hint: "USB keeps working. If Wi-Fi settings are wrong, fix them over USB."
- Factory reset controller… (confirm dialog → `FactoryResetAsync`).

**Footer** (sticky while dirty): **Save to controller** and **Revert**.
- Save shows a progress line: *Sending… → Restarting… → Saved ✓*, or the error.

**Error messages:**

| Situation | Message |
|---|---|
| INVALID | "The controller rejected the setup: <field description> on <Button 2 / Slider 1 / Wi-Fi>." The field is highlighted. |
| DECODE_FAILED, TOO_LARGE | "The controller couldn't read the setup. Update the firmware and try again." |
| STORAGE_FAILED | "The controller couldn't save the setup. Its previous setup is unchanged." |
| Timeouts | §5.4 messages. |

**The Connection page** loses the baud rate field. Everything else is unchanged.

## 9. Build, CI, release

### 9.1 Firmware artifact

A post-build `extra_scripts` step in `platformio.ini` writes `firmware-manifest.json` next to the binaries in `.pio/build/esp32/`:

```json
{
  "chip": "esp32",
  "version": "<build id>",
  "parts": [
    {"name": "bootloader", "file": "bootloader.bin", "offset": 4096,  "size": 0, "md5": "…"},
    {"name": "partitions", "file": "partitions.bin", "offset": 32768, "size": 0, "md5": "…"},
    {"name": "boot_app0",  "file": "boot_app0.bin",  "offset": 57344, "size": 0, "md5": "…"},
    {"name": "app",        "file": "firmware.bin",   "offset": 65536, "size": 0, "md5": "…"}
  ]
}
```

- The offsets come from the build, not from constants: the script reads them from PlatformIO's `FLASH_EXTRA_IMAGES` and the application offset.
- `size` and `md5` are computed by the script.
- The NVS range must not overlap any part; the script fails the build if it does.

### 9.2 Build id

- A single string identifies a build of both the app and the firmware, and equality is the only comparison:
  - release: the release version, e.g. `1.2.0`
  - CI non-release: `0.0.0-ci+<short sha>`
  - local: `dev`
- The app gets it as an assembly attribute (`FirmwareBuildId`, alongside `ReleaseChannel`). The firmware gets it as `VOLCO_FW_VERSION`.
- **The app compares Hello's `fw` to the bundled manifest's `version`**, not to its own attribute. The two are equal in CI builds because the same job sets both. A local build has no bundled firmware, so no prompt appears.

### 9.3 Workflows

**`build.yml`:**
- `firmware-test` keeps `pio test -e native`, builds `esp32` with `VOLCO_FW_VERSION`, and uploads the artifact `firmware-esp32` (the four parts and the manifest).
- The Windows `build` job gains `needs: firmware-test`, downloads `firmware-esp32` into `src/Volco.Windows/Firmware/esp32/`, and publishes.
- `Volco.Windows.csproj` embeds `Firmware/**` as `EmbeddedResource` when present.
- The Linux test job does not need the firmware.

**`release.yml`:**
- A new Linux `firmware` job does the same build with the release version.
- The Windows release job `needs` it and embeds the artifact.
- The release also attaches `volco-firmware-esp32-<version>.zip` (parts and manifest).

**Protobuf generation:**
- **C#:** `Grpc.Tools` (`<Protobuf Include="../../proto/volco_config.proto" GrpcServices="None" />`) in `Volco.Core.csproj`, plus `Google.Protobuf`. Grpc.Tools bundles protoc for Linux and Windows. Verified by Spike S3.
- **Firmware:** nanopb's PlatformIO integration generates code at build time in both the `esp32` and `native` environments.
- **Simulator:** `firmware/tools/gen_assets.py` gains a step that writes `tools/simulator/config_proto.js` (the `.proto` text as a JS string, parsed at runtime by protobufjs). That file is committed, like `icons.js`.
- A CI check in `firmware-test` re-runs that step and fails on a diff, so it can't go stale.

## 10. Simulator changes

- **New Hello:**
  - chip 1, fw `"sim"`
  - `configState` and `configCrc` from its in-memory config
  - sliders, buttons and kinds from the selected controller type, or from the stored config when present (the config drives the simulated hardware)
- **ConfigRead:** returns the in-memory blob in ConfigData chunks.
- **Config write:**
  - Implements the §5.4 receiver rules. "Validation" means decode with protobufjs plus the counts and screen rules that affect the simulator's layout. Pin rules don't apply to a simulator.
  - On OK it simulates a reboot: the link drops for 1 s, then sends Hello.
- **Persistence:** the config is kept in `localStorage`, wrapped in try/catch, so a reload keeps it. A "Clear config" button provides a factory reset.
- **Loading protobufjs:** from cdnjs or jsdelivr. The file stays one HTML file (plus the generated JS files), with no build step.
- **Test mode needs nothing in the simulator.** It is app-side.

## 11. Error handling summary

| Failure | Where handled | Result |
|---|---|---|
| UDP datagram lost during write | BAD_SEQUENCE/BAD_CRC or no result → app retries ×3 | Usually invisible; otherwise "didn't answer". |
| Unplugged mid-write | `Detach` → op fails | "Connection lost". The mirror and the controller's old config are unchanged, because NVS is written only at End. |
| Unplugged or power loss during NVS write | NVS atomicity | Old or new config at next boot. Hello's CRC tells the app which. |
| Valid blob that fails validation in new firmware after an update | Boot → configState 2 | Damaged banner. Apply the mirror (the app re-validates first) or start fresh. |
| Wrong Wi-Fi settings | Serial always on | Fix over USB. |
| Hello with an old CRC after save | Confirmation step | "Restarted but isn't running the new setup". |
| Flash fails mid-write | Flasher error | Retry message. The ROM bootloader is untouched by app writes, so the board stays recoverable over USB. |
| Board that never answers Hello | 5 s hint → probe | Probe tells the user what's on the port. |
| Foreign NVS contents after Install | Install erases the whole flash | Clean NVS. |
| NVS init failure at boot | Erase and re-init (§6.6) | Defaults. |
| Protocol version mismatch | Link not Ready | Only Update offered. |

## 12. Testing

### 12.1 Shared golden vectors (both test suites)

- **`firmware/test/golden/frames.txt`** gains:
  - the new Hello forms (no config, stored config, damaged config, LED hardware, screen hardware)
  - ConfigRead, single-chunk and multi-chunk ConfigData, empty ConfigData
  - ConfigWriteBegin/Chunk/End
  - every ConfigResult code
- **New `firmware/test/golden/config.txt`**, where each case is a name, a blob in hex, and an expectation:
  - `decode-ok <field assertions>`: the DIY reference, the maximal config (§6.1 sizes, asserting it is ≤ 1024 bytes), an empty blob (all defaults), a blob with an unknown field (skipped), a blob with only `screens.brightness` set (others default).
  - `decode-fail`: truncated, bad varint, a string over its maximum size, too many buttons.
  - `invalid <field> <item>`: at least one case per validation rule and field code in §6.4, including ADC2 + UDP, duplicate pins, reserved pins, input-only LED, a third screen on a bus, a duplicate screen address, a missing I2C bus, and each range limit.
  - `crc <hex>`: CRC test vectors, including `123456789` → `CBF43926`.
- The blobs in `config.txt` are produced once by a small generator script (`firmware/tools/gen_config_vectors.py`, using the protobuf Python package) and committed. Both test suites read the file and check the same expectations. The firmware tests embed it the way `golden_frames.h` embeds `frames.txt`.

### 12.2 Firmware native tests (`pio test -e native`)

- **`test_config`:** decode, defaults, `toRuntime` unit conversion, every validation rule (driven by `config.txt`), chip table membership, CRC.
- **`test_config_receiver`:**
  - happy path, 1 chunk and 5 chunks
  - a duplicate chunk, a gap, an overrun, End early, End with no transfer, a chunk with no transfer
  - a Begin mid-transfer restarts it
  - TOO_LARGE
  - the 2000 ms timeout closes the transfer (time injected)
  - factory reset (`total` = 0)
  - exactly one result per transfer
- **`test_protocol`:**
  - new Hello encode against the goldens
  - the config frames
  - `Link` routes 0x87–0x8A to `ConfigSink`
  - config frames count as proof of life
  - the active-transport rule: frames from transport B switch the replies to B; the source-IP filter is tested at the transport boundary through a pure helper `acceptUdpSource(ip, configuredIp)`

### 12.3 C# tests (`Volco.Core.Tests`)

- **Codec and validator:** driven by `config.txt` (same expectations); presets load and validate; the DIY reference equals §6.2.
- **Protocol:** new goldens; a malformed Hello is dropped.
- **`ControllerConfigService`** with a fake connection and fake time:
  - **Read:** in-order assembly; a gap → retry; a CRC mismatch → retry; three failures → error; a stray ConfigData is ignored.
  - **Save:**
    - OK + confirming Hello → mirror updated.
    - OK + Hello with the old CRC → error, mirror unchanged.
    - OK + no Hello in 10 s → error.
    - BAD_CRC once then OK → success with 2 attempts.
    - INVALID → no retry, `item` and `field` surfaced.
    - No result ×3 → error.
    - Detach mid-save → "Connection lost", mirror unchanged.
    - A second request while busy → rejected.
  - **`OnHello`:** every row of the §8.3 table.
- **Test mode:**
  - Slider and Button in test mode → zero `IAudioEngine` calls and zero action executions; `RawInput` raised.
  - `SetTestIndicator` → expected Led and Screen frames.
  - Exit → `PushAll` frames restore the real state.
  - Exit on Detach and on Dispose.
- **`FirmwareService`** with a fake `IFirmwareFlasher`:
  - the connection is suspended (port released) before flashing and resumed after
  - progress is forwarded
  - a failure mid-flash → error and resumed connection
  - no Hello after flash → error
  - Install/Update disabled when the catalog is empty or the connection isn't Serial
  - every probe result → its UI message (view-model test)
- **Manifest parsing:** a valid manifest; a part overlapping NVS → rejected.

### 12.4 App tests (`Volco.App.Tests`, headless)

- The Controller page view model:
  - draft and dirty tracking
  - preset apply keeps behaviour and UDP fields
  - "modified" detection
  - inline error mapping from (`item`, `field`)
  - editing disabled while disconnected
  - footer state during a save
- `ArchitectureTests` stays green, and covers the flasher's location per Spike S1.

### 12.5 Spikes (first plan tasks, results recorded in the plan)

- **S1, EspDotNet:**
  - Does the package target net8.0, and does it run on `System.IO.Ports` without P/Invoke (to decide between Core and Windows)?
  - On the real DIY board: auto-reset into the bootloader, chip detect, write the four parts at their offsets, `SPI_FLASH_MD5` (add the command if missing; it is in Espressif's documented serial protocol, and the library is MIT-licensed), hard reset.
  - Measure install and probe times.
- **S2, nanopb under PlatformIO:** generation from `../proto` in both the `esp32` and `native` environments; the exact `custom_nanopb_*` keys; proto2 defaults present in the generated initialiser.
- **S3, Grpc.Tools:** generation in the Linux test job and the Windows single-file publish.

### 12.6 Manual hardware checklist (`docs/superpowers/checklists/`)

1. Install on a blank ESP32 → Hello, "Set up your controller".
2. Apply the DIY reference → save → reboot → test mode shows all 5 sliders and 3 buttons; LEDs toggle.
3. Normal mode: volume and mute work; the actions are unchanged.
4. Change a pin → save → the old pin no longer reacts and the new one does.
5. Update firmware (build a second version) → the config survives (same CRC, no banner).
6. Enable UDP with a wrong password → save → still works over USB; fix it → works over UDP with the app's connection set to UDP.
7. UDP from a different PC IP is ignored.
8. Unplug during a save → the old config stays; the app reports "Connection lost".
9. Unplug during a flash → Install again succeeds.
10. Probe: a blank board → "doesn't have Volco's firmware"; a matching board with the firmware stalled (hold reset) → as specified.
11. Factory reset → "Set up your controller".
12. Screens: an IND_SCREEN layout from the form → screens render; behaviour settings take effect after save.

## 13. Documentation updates

- **`firmware/README.md`:**
  - remove the `config.h` editing guide
  - describe the config model, the schema, the protocol additions, the active-transport rule and the chip table
  - keep the wiring guidance as advice
- **`CLAUDE.md`:**
  - the firmware section, the protocol table, the build commands (`esp32` environment, no `*_check` environments)
  - the CI description (artifact flow)
- **`tools/simulator/README.md`:** config support.
- **Root `README.md`:** the install and setup story.

## 14. Open points for the reviewers and the user

1. **The Wi-Fi password is plain text** in NVS and in `config.json`'s mirror, as in `config.h` today. This is accepted for now (§3).
2. **Volco PCB placeholder marker** format (§6.2). This is a plan-level detail.
3. **S1 may force the flasher into `Volco.Windows`.** The seam (`IFirmwareFlasher`) is the same either way.

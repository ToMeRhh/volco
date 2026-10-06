# Controller configuration and firmware installation: design

Date: 2026-10-03. Status: revision 3, waiting for the user's review. Revision 2 applied two independent first-round reviews (firmware and protocol; app, UX and CI). Revision 3 applies a second-round review of revision 2.

## 1. Goal

Today a user edits `firmware/src/config.h` to describe their hardware and preferences, then builds and flashes the firmware with PlatformIO. After this change:

- **Nobody compiles firmware.** CI builds one generic firmware image per chip family. Only the classic ESP32 is supported in this change.
- **Volco installs and updates the firmware** over USB.
- **The user describes the hardware in Volco.** A Controller page holds the sliders, buttons, indicators and I2C buses. It can be filled from a preset or from scratch.
- **Preferences are set in Volco** (screen behaviour, input tuning, and the optional Wi-Fi/UDP link), and are saved to the controller.
- **The user can test the hardware live** from the same page without changing Windows audio or running actions.

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
| D7 | USB serial is always on. UDP over Wi-Fi is an **advanced, opt-in** setting. It needs an explicit SSID, password, PC IP address and PC port. There is **no discovery and no broadcast**: the controller sends only to the configured IP and accepts UDP only from it. |
| D8 | The ESP32 firmware drops its WebSocket client and the `TRANSPORT_LOCAL_DEBUG` mode. Volco keeps its WebSocket *server* for the simulator and alternative controllers. |
| D9 | The config blob is **Protocol Buffers, proto2 syntax** (explicit defaults): nanopb on the firmware, Google.Protobuf in C#, protobufjs in the simulator. Code is generated at build time from one `.proto`. The wire frames stay hand-written binary. (Editions are not used: nanopb 0.4.9.2 has only minimal editions support, mapping presence but not defaults.) |
| D10 | User-facing behaviour settings are visible; tuning settings sit under a collapsed "Advanced" area; protocol timing (host timeout, keepalive, commit fallback, reconnect interval) and the serial baud rate (115200) are fixed constants. |
| D11 | Every successful save reboots the controller. There is no live preview. |
| D12 | Firmware version detection is hybrid: Hello carries the firmware build id (the normal case, no reset), and a **bootloader probe** over the ROM serial bootloader handles a board that does not answer Hello. |
| D13 | A **Test hardware** mode shows live raw input and lets the user drive each indicator, while input has no effect on audio or actions. |
| D14 | Firmware install and update happen only over USB. OTA is out of scope. |

## 3. Non-goals

- Learn mode or automatic wiring detection.
- Pin validation beyond §6.4: pull-up resistors, strapping-pin boot behaviour, module-specific unbonded pins and wiring mistakes are not checked. The UI gives hints, §8.7.
- OTA updates, UDP discovery, mDNS, WebSocket on the ESP32.
- More than one mirror copy, or identifying controllers by MAC address.
- Chip families other than the classic ESP32.
- Encrypting the Wi-Fi password, either in NVS or in Volco's `config.json`. It is stored in plain text, as it is in `config.h` today.
- Restricting which LAN hosts Volco's UDP listener accepts. That behaviour is unchanged.
- Tray balloon or toast notifications. Avalonia's `TrayIcon` has no balloon API. An available firmware update is signalled by a dot on the tray icon and on the sidebar entry instead (§8.3).

## 4. Overview

```
            ┌──────────────────────────── Volco (Windows) ─────────────────────────────┐
            │ Controller page (Volco.App)                                               │
            │   status & firmware │ hardware │ test │ behaviour │ advanced              │
            │        │                 │          │                                      │
            │ FirmwareService     ControllerConfigService   VolcoRuntime (test mode,    │
            │  + IFirmwareFlasher  (read / save / mirror,    suspend) → ControllerLink   │
            │  (Volco.Windows:      protobuf codec)                                      │
            │   EspDotNet)                                                               │
            └────────┬──────────────────────┬──────────────────────────────────────────┘
          ROM bootloader                 Volco protocol v1 (serial, or UDP)
          (flash, probe)                    │
            ┌────────┴──────────────────────┴──────── ESP32 firmware ─────────────────┐
            │ boot: NVS "volco"/"cfg" → volco_config decode + validate → RuntimeConfig │
            │       (or built-in defaults)                                             │
            │ Link (Hello with build id + config CRC; config read / write frames)      │
            │ inputs, indicators, screens, transports — all sized from RuntimeConfig   │
            └──────────────────────────────────────────────────────────────────────────┘
```

New and changed pieces:

| Area | Piece | Kind |
|---|---|---|
| Repo root | `proto/volco_config.proto`, `proto/volco_config.options`, `proto/presets/diy-reference.json` | new |
| Firmware | `lib/volco_config` (pure C++: decode, defaults, validate, chip table, chunked receiver, CRC) | new |
| Firmware | `lib/volco_protocol` (new Hello, config frames, active-transport logic) | changed |
| Firmware | `src/` (runtime-sized tables, NVS store, dual transport, reboot after save) | changed |
| Firmware | `config.h` (only fixed constants remain) | reduced |
| App, Core | `Volco.Core/Controller/` (codec, validator, presets, service, banner logic) | new |
| App, Core | `Volco.Core/Firmware/` (`IFirmwareFlasher` seam, catalog, manifest, `FirmwareService`) | new |
| App, Core | `Communication/` (Hello parsing, config frames encode/parse) | changed |
| App, Core | `Runtime/VolcoRuntime` (test mode, connection suspend, routing), `ControllerLink` (`SendRaw`) | changed |
| App, Core | `Config/` (schema v5: mirror blob, baud removed) | changed |
| App, Core | `Input/ButtonPressDetector` (`Reset()`, unmatched release ignored) | changed |
| App, UI | `Views/ControllerPage`, `ViewModels/Controller*` | new |
| App, UI | `ConnectionViewModel` (baud setting removed) | changed |
| Windows | `EspDotNetFirmwareFlasher`, embedded firmware images, `IFirmwareSource` | new |
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
| `0x87` | ConfigRead | PC→C | `seq` u8 |
| `0x88` | ConfigWriteBegin | PC→C | `seq` u8, `total` u16, `crc32` u32 |
| `0x89` | ConfigWriteChunk | PC→C | `seq` u8, `offset` u16, `data` (1–240 bytes) |
| `0x8A` | ConfigWriteEnd | PC→C | `seq` u8 |

- **Sizes:** the largest payloads are ConfigData at 1 + 9 + 240 = 250 bytes and ConfigWriteChunk at 1 + 3 + 240 = 244 bytes. Both are within 255.
- **Proof of life:** every PC→C frame, including the config frames, counts toward the host timeout, as every valid frame does today.
- **`seq`:** the app chooses a new value for each attempt (§5.4), and the controller echoes it in every ConfigData and ConfigResult belonging to that attempt. The app ignores ConfigData and ConfigResult whose `seq` is not the current attempt's. This is what makes retries safe.

### 5.2 Hello body

```
version        u8     protocol version, 1
chip           u8     chip family: 1 = ESP32 (0 reserved/unknown)
fwLen          u8     0–32
fw             fwLen bytes ASCII: firmware build id (§9.2)
configState    u8     0 = no stored config (running built-in defaults)
                      1 = running the stored config
                      2 = a stored config exists but couldn't be used (running defaults)
configCrc      u32    CRC-32 of the stored blob (§6.5); 0 when configState is 0, or when the
                      stored blob is longer than kMaxConfigBlob
sliders        u8     0–16
buttons        u8     0–32
kinds          buttons bytes: 0 none, 1 LED, 2 LED pair, 3 screen
```

- **Size:** the maximum body is 74 bytes.
- **When it's sent:** at boot, in reply to HelloRequest, and on the first valid frame after a host timeout. These rules are unchanged. Hello is also sent on UDP each time Wi-Fi connects (§5.5).
- **The CRC in Hello is always the CRC of what is stored now.** After a successful store, every later Hello (there is a reboot) reports the new CRC.
- **What the app uses:** `sliders`, `buttons` and `kinds` still drive everything else in the app. The runtime never decodes the config blob for normal operation.
- **Parsing rule (app):**
  - Read `version` first. If it is not 1, the parser emits `HelloMessage(version)` with no other fields, so the link can report a version mismatch.
  - If `version` is 1 and the body is shorter than its lengths claim, or `fwLen` > 32, the Hello is dropped as malformed.
  - The old pre-change Hello (7 bytes for 3 buttons) is malformed under this layout and is dropped. Such a board is found through the "no Hello" path (§8.6). A golden test covers this.
- **Build check:** the firmware `static_assert`s that `VOLCO_FW_VERSION` is at most 32 bytes.

### 5.3 ConfigRead and ConfigData

The PC sends `ConfigRead {seq}`. The controller replies with the **stored blob bytes exactly as stored**, split into ConfigData frames sent back-to-back on the active transport:

```
seq            u8     echoed from ConfigRead
total          u16    blob size in bytes; 0 = no stored config (or stored blob unusable as bytes, §6.6)
offset         u16    offset of this chunk
crc32          u32    CRC-32 of the whole blob (0 when total is 0)
data           0–240 bytes
```

- If `total` is 0, exactly one frame is sent, with no data.
- Otherwise the chunks are sent at offsets 0, 240, 480, … in order. Every chunk except the last carries 240 bytes.
- A damaged stored blob (configState 2) is returned like any other when its length is ≤ `kMaxConfigBlob`.
- **The whole reply is one send:** one serial write, or **one UDP datagram** (at most about 1.1 KB). Back-to-back datagrams risk lwIP send failures.
- The reply goes to the transport the ConfigRead arrived on.
- The firmware keeps no read state. Each ConfigRead produces a full fresh reply.

The app accepts the reply when all of these hold:

1. Every frame has the current `seq`.
2. The offsets are in order (offset = bytes received so far).
3. Every chunk carries the same `total` and `crc32`.
4. The assembled size equals `total`.
5. The CRC-32 of the assembled bytes equals `crc32`.

Frames with another `seq` are ignored. Anything else, including no complete reply within **2 s** of the ConfigRead or of the last accepted chunk, fails the attempt. The app retries with a new `seq` up to **3 attempts** in total, then reports "Couldn't read the controller's setup" with a **Retry** button.

### 5.4 Config write

```
PC: one burst = ConfigWriteBegin {seq, total, crc32}
               ConfigWriteChunk {seq, offset 0, data[240]} … (as many as needed)
               ConfigWriteEnd {seq}
C:  ConfigResult {seq, code, item, field}
C:  if code == OK: reboots ~200 ms later; boots; sends Hello (configState 1, configCrc = new CRC)
```

**The burst is one send.** `ControllerLink.SendRaw` concatenates the whole burst into one byte array and passes it to `IControllerConnection.Send` once. That is one serial write, one WebSocket message, and **one UDP datagram**. The largest burst is about 1.07 KB, under the 1472-byte UDP payload limit. This avoids lwIP's 6-datagram receive mailbox (`CONFIG_LWIP_UDP_RECVMBOX_SIZE=6`), which would otherwise drop part of a 7-datagram burst while the loop is blocked by a screen redraw. On the firmware, the UDP receive buffer is 1536 bytes and the serial RX buffer 2048 bytes.

**ConfigResult body:** `seq` u8, `code` u8, `item` u8, `field` u8.
- `item` and `field` are defined in §6.4.
- Both are `0xFF` when they don't apply.

| Code | Name | Meaning |
|---|---|---|
| 0 | OK | Stored. The controller reboots. |
| 1 | BAD_SEQUENCE | Within an open transfer: a chunk whose offset ≠ bytes received, a chunk overrunning `total`, End before `total` bytes, or a chunk or End whose `seq` ≠ the open transfer's `seq`. |
| 2 | BAD_CRC | The CRC-32 of the received bytes ≠ `crc32` from Begin. |
| 3 | TOO_LARGE | Begin's `total` is 0 or > `kMaxConfigBlob` (1024). Sent in reply to the Begin. |
| 4 | DECODE_FAILED | nanopb could not decode the blob. This includes a string or repeated field over its limit, and a string containing a NUL byte. |
| 5 | INVALID | Decoded, but violates a §6.4 rule. `item` and `field` say where. |
| 6 | STORAGE_FAILED | The NVS write failed. The old config is unchanged. |

**Firmware receiver rules** (`volco_config::BlobReceiver`, pure and host-tested; time is passed in):

1. **ConfigWriteBegin** always (re)starts a transfer. It discards any open one, records `seq`, `total`, `crc32` and the **transport it arrived on**, and sets received = 0.
   - `total` = 0 or > 1024 → reply TOO_LARGE and close. A saved blob is never empty: `ControllerConfigCodec.Encode` always sets `preset` (`"blank"` for a config not based on a preset). There is **no factory-reset frame**: Install already wipes NVS (§8.6), and saving a Blank config covers "start fresh".
2. **Chunks and End from a different transport** than the open transfer's are ignored.
3. **A chunk or End with no open transfer is ignored silently.** After a transfer closes, by success or by error, nothing more is sent for it. This guarantees **at most one ConfigResult per Begin**.
4. **Within an open transfer**, a chunk with the wrong `seq` or offset, with 0 or more than 240 data bytes, or overrunning `total` → reply BAD_SEQUENCE and close. An End with the wrong `seq`, or received ≠ `total` → reply BAD_SEQUENCE and close.
5. **End with all bytes:**
   - check the CRC → BAD_CRC
   - decode → DECODE_FAILED
   - validate → INVALID
   - store → STORAGE_FAILED

   The first failure replies with its code and closes. If all pass → reply OK, close, then reboot (§6.7). The ConfigResult always goes out on the transport the Begin arrived on.
6. **Timeout:** an open transfer with no accepted config frame for **2000 ms** closes silently. The check also runs on every incoming chunk and End, so a frame arriving late after a long pause finds the transfer already closed even if the loop hasn't ticked.
7. **Nothing else stops:** inputs are still reported during a transfer, and Led, Screen, Begin and Commit still apply.

**App write rules** (`ControllerConfigService.SaveAsync`):

- **An attempt** takes a new `seq` (incrementing u8, wrapping) and sends the burst.
- **Wait for ConfigResult** with that `seq`, for up to **3 s**:
  - **OK** → go to *confirming*.
  - **BAD_SEQUENCE or BAD_CRC** → a transport error. Start the next attempt.
  - **TOO_LARGE, DECODE_FAILED, INVALID or STORAGE_FAILED** → fail immediately with the specific message (§8.7). No retry.
  - **Nothing within 3 s** → start the next attempt.
  - **No connection attached:** `SendRaw` returns false when no connection is attached, for example while `UdpConnection` is detached after 3 s of silence during a reboot. That attempt counts as sent and unanswered.
  - **After the 3rd attempt ends with no ConfigResult,** the service doesn't fail yet. It waits the confirming timeout (10 s, or 20 s on UDP) for an implied-OK Hello. Only then does it fail with "The controller didn't answer." This covers an OK lost over UDP followed by a reboot and Wi-Fi rejoin that takes longer than the retries.
- **Implied OK.** From the first Begin onward, in **any** phase, a Hello with configState 1 and configCrc = the CRC of the bytes being saved **confirms the save** immediately. That covers an OK lost over UDP: the controller rebooted with the new config. If a retry's burst still reaches the controller after it stored the config, it stores the same bytes again and reboots once more. That is harmless.
- **Any other Hello** before a ConfigResult (for example, the board was reset by hand) fails the current attempt and starts the next one.
- **Confirming:** wait up to **10 s** (serial, WebSocket) or **20 s** (UDP, which includes rejoining Wi-Fi) for a Hello.
  - configState 1 and CRC = the sent CRC → **confirmed**.
  - Any other Hello → fail with "The controller restarted but isn't running the new setup."
  - No Hello → fail with "The controller didn't come back after restarting." When the save changed UDP settings, add: "If you changed the Wi-Fi settings, connect with USB to check them."
- **The mirror is updated only when the save is confirmed**, through `ConfigManager.RequestSave()`.
- **Connection drops** (§8.2): a Detach followed by a re-Attach of the **same** connection does not fail the save. The reboot itself can cause one, through the UDP inactivity timeout, the simulator's reconnect, or a serial error during the reboot. Only a connection *replacement* fails the save: a connection config change, `SuspendConnection`, `Reconnect()`, or runtime dispose. The message is then "Connection lost while saving. Reconnect to see which setup the controller has." The mirror is left unchanged.
- **Cancellation** is honoured only before the first burst is enqueued.
- **The save belongs to the runtime-owned service.** Closing the settings window never cancels a save, and a confirmed save still updates the mirror.
- **Only one read or write runs at a time.** The service rejects a second request.

**Throughput:** a full 1024-byte blob is one burst of about 1.07 KB, roughly 0.1 s at 115200 baud.

### 5.5 Which transport the controller answers on

USB serial is always on. When UDP is enabled, each transport has its **own** `FrameParser`, so bytes from one cannot corrupt a frame from the other. Both parsers feed the same `Link`. The logic lives in `lib/volco_protocol` as a pure `ActiveTransport` helper, which is host-tested.

- **Unlatched** (at boot, and after every host timeout): every outgoing frame (Hello, Alive, Slider, Button) goes to **every transport that is up**. Serial is always up; UDP is up once Wi-Fi is connected.
- **Latching:** the first valid frame from the PC latches its transport as **active**. The latch is set **before** the frame is passed to `Link::onPayload`, so the reply (for example, Hello to a HelloRequest) goes to the new transport. While latched, every outgoing frame goes to the active transport only, and a valid frame from the other transport moves the latch to it.
- **The host timeout** (`hostLost`) clears the latch.
- **When Wi-Fi connects** (`WL_CONNECTED`, including after a drop), the controller sends a Hello on UDP. This is the **one exception** to "latched → active transport only": it is sent even when serial is latched.
- **The UDP source filter:** datagrams are accepted only from the configured PC IP, through a pure helper `acceptUdpSource(srcIp, configuredIp)`. Datagrams from any other address are dropped before parsing. The controller sends to `pc_ip:pc_port` and listens on `local_port`.

Why this works:
- **Volco on UDP:** Volco's `UdpConnection` only listens until it hears a datagram. The controller's unlatched Alive and Hello frames on UDP are that first datagram.
- **Recovery:** a controller with wrong Wi-Fi settings is still fully usable over USB, because serial is always up and latches on its first frame.
- **`DEBUG_LOG` output** goes to serial only. It never forms a valid frame, so it never confuses the app.

## 6. The controller config

### 6.1 Schema: `proto/volco_config.proto`

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
  optional I2cBus i2c2 = 9;        // required when a screen uses bus 2 (software I2C)
  optional I2cBus i2c3 = 10;       // required when a screen uses bus 3 (software I2C)
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
  optional uint32 bus = 1 [default = 0];        // 0-3: 0 and 1 hardware I2C, 2 and 3 software I2C
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

**nanopb options** (`proto/volco_config.options`; `max_size` counts the NUL terminator):

```
volco.config.ControllerConfig.sliders  max_count:16
volco.config.ControllerConfig.buttons  max_count:32
volco.config.ControllerConfig.preset   max_size:25
volco.config.Udp.ssid                  max_size:33
volco.config.Udp.password              max_size:65
```

**Limits are enforced identically on both sides:**
- nanopb fails decoding when a string or repeated field exceeds its option.
- Google.Protobuf does not know these options. `ControllerConfigCodec.Decode` therefore checks them after parsing, throwing `ControllerConfigFormatException` on failure:
  - repeated counts
  - string UTF-8 byte lengths, excluding the NUL
- **A NUL byte inside any string is a decode failure on both sides.** nanopb would otherwise silently truncate the string.
- **Invalid UTF-8 in a string is a decode failure on both sides.** nanopb is built with `PB_VALIDATE_UTF8`; Google.Protobuf already rejects it.
- **Known divergence:** a `uint32` varint wider than 32 bits fails in nanopb but is truncated by C#. Volco never produces one, so this concerns foreign blobs only, and the firmware's verdict counts. It is deliberately kept out of the shared vectors.

Notes:

- **Defaults live only in the `.proto`.** The app reads them from the generated C# properties (proto2 presence: `HasX` / `ClearX`), and the firmware from nanopb's generated initialiser. A golden case checks that `screen {}` decodes to bus 0, address 0x3C on both sides.
  - **An absent submessage behaves differently on the two sides.** C# returns `null` for it (`Screens == null`), while nanopb fills it with defaults (stage 1).
  - `ControllerConfigCodec` therefore reads an absent submessage as a default instance (`config.Screens ?? new ScreenBehaviour()`, and likewise `Input`, `Udp`, `I2c0`/`I2c1`). It never dereferences a possibly-null submessage.
  - A golden case asserts that the empty blob gives `screens.brightness == 255` on both sides.
- **The ranges in the comments are normative.** They are enforced by §6.4 on the firmware and the app, and the UI uses them as its control limits.
- **Sizes:** the largest valid blob is about 683 bytes (with all four I2C buses). `kMaxConfigBlob` = 1024, and a golden case asserts the maximal blob fits.
- **Units:** times are seconds where the old constants were minutes, and milliseconds where they were sub-second. The firmware converts to milliseconds at load.

### 6.2 Presets

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

- **The "Volco PCB" placeholder is hard-coded in the app's preset list** as a disabled entry "Volco PCB (coming soon)", with no file. Once the PCB's pins are known, it becomes `proto/presets/volco-pcb.json` and the hard-coded entry goes away.
- **A preset carries only the hardware fields** (`sliders`, `buttons`, `i2c0`, `i2c1`, `preset`). Applying it replaces exactly those fields in the draft. Behaviour, input tuning and UDP fields are kept.
- **Packaging:** preset files are embedded as resources in `Volco.Core` and loaded with `Google.Protobuf.JsonParser`.
- **Tests:** a test asserts that the DIY reference passes §6.4 validation and equals the table above.

### 6.3 Firmware runtime model

`volco_config` decodes into nanopb's generated `volco_config_ControllerConfig` struct, which has fixed-size arrays (static allocation). Then `toRuntime()` produces a `RuntimeConfig` holding plain values with the defaults applied, the times converted to milliseconds, and only the I2C buses that are actually used. `main.cpp`, `indicators.cpp`, `screens.cpp` and `transport.cpp` read only `RuntimeConfig`.

**What stays in `config.h` (fixed constants):**

| Constant | Value |
|---|---|
| `SERIAL_BAUD` | 115200 |
| `SERIAL_RX_BUFFER` | 2048 |
| `SERIAL_TX_BUFFER` | 2048 (set before `Serial.begin()`, so a ConfigRead reply doesn't block the loop) |
| `UDP_BUFFER` | 1536 |
| `HOST_TIMEOUT_MS` | 3000 |
| `KEEPALIVE_MS` | 1000 |
| `COMMIT_FALLBACK_MS` | 500 |
| `WIFI_RETRY_MS` | 10000 |
| `kMaxConfigBlob` | 1024 |
| `kMaxSliders` | 16 |
| `kMaxScreens` | 8 (4 buses × 2 addresses) |

Everything else in today's `config.h` moves into the schema. `VOLCO_SCREENS_CHECK` and the transport `#define`s are removed.

**Built-in defaults** (no stored config, or an unusable one) are the protobuf defaults with no sliders, no buttons, no I2C buses and UDP off. The controller then sends Hello with 0 sliders and 0 buttons, answers config frames, and does nothing else.

### 6.4 Validation

`validate(config, chip) → (code, item, field)` returns OK or the **first** violation. "First" follows the exact order below, and the C# `ControllerConfigValidator` must return the **same triple** for every input. The shared golden vectors (§12.1) enforce this. The firmware's verdict is the one that counts; the app validates first to give immediate feedback.

**Definitions** (used by the steps; not steps themselves):

- **The chip table for ESP32** (`ChipInfo`, data in `lib/volco_config`):

  | Set | Pins |
  |---|---|
  | exists | 0–19, 21–23, 25–27, 32–39 |
  | reserved | 1, 3 (UART0, the always-on USB serial), 6–11 (SPI flash) |
  | input-only | 34, 35, 36, 37, 38, 39 |
  | ADC1 | 32–39 |
  | ADC2 | 0, 2, 4, 12, 13, 14, 15, 25, 26, 27 |
  | usable | exists − reserved |
  | output-capable | usable − input-only |
  | ADC-capable | (ADC1 ∪ ADC2) ∩ usable |

- **Field codes:**

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

- **`item`:**
  - Slider and button rules: the slider or button index.
  - I2C rules (fields 8–10): the bus number (0–3).
  - UDP rules (fields 11–14): 0xFF.
  - Range rules (fields 15–16): the protobuf field number within `ScreenBehaviour` or `InputTuning`.

- **Iteration order** within a step: sliders by index, then buttons by index. Within a button: its `pin`, then its indicator's fields in the order led.pin / led_pair.pin_a / led_pair.pin_b / screen.bus / screen.address. I2C bus 0 before bus 1, `sda` before `scl`. Range fields in field-number order, `screens` before `input`.

**Steps:**

1. **Counts:** sliders ≤ 16 and buttons ≤ 32. A decoded config already guarantees this, because the decoder enforces `max_count`; the step stays for the C# path. The violations are reported as (INVALID, 0xFF, 1) for sliders and (INVALID, 0xFF, 2) for buttons.
2. **Sliders:**
   - `pin` absent, not usable, or not ADC-capable → field 1.
   - If `udp.enabled` and `pin` ∈ ADC2 → field 1. ADC2 cannot be read while Wi-Fi is on.
3. **Buttons:**
   - `pin` absent or not usable → field 2.
   - **LED:** `pin` absent or not output-capable → field 3.
   - **LED pair:** `pin_a` absent or not output-capable → field 4. `pin_b` absent, not output-capable, or equal to `pin_a` → field 5.
   - **Screen:** `bus` ∉ {0, 1, 2, 3} → field 6. `address` ∉ {0x3C, 0x3D} → field 7. (bus, address) already used by an earlier button's screen → field 7, reported on the **later** button. There are only two addresses, so this also limits each bus to two screens; there is no separate "third screen" rule.
   - **This is a single pass, one button at a time:** its pin, then its indicator fields, then the (bus, address) duplicate check. All of button *n* is checked before button *n+1*.
4. **I2C**, for each bus that some screen uses, in bus order 0, 1, 2, 3 (`item` = the bus):
   - `I2cBus` message absent → field 8, `item` = bus.
   - `sda` or `scl` absent, not output-capable, or `sda` = `scl` → field 9, `item` = bus.
   - `clock_hz` out of range → field 10, `item` = bus.
   - An `I2cBus` no screen uses is ignored entirely: not validated and not started.
5. **Pin uniqueness:** walk every pin in iteration order (slider pins, button pins with their indicator pins, then the pins of *used* I2C buses). The first pin already seen is reported **at its later use**, with that use's `item` and `field`. For an I2C pin that means `item` = bus, field 9.
6. **UDP**, if `enabled`:
   - `ssid` not 1–32 bytes → field 11.
   - `password` neither 0 nor 8–64 bytes → field 12. WPA needs at least 8.
   - `pc_ip` absent, 0, 255.255.255.255, in 127.0.0.0/8, or in 224.0.0.0/4 → field 13. No broadcast, multicast or loopback (D7).
   - `pc_port` or `local_port` outside 1–65535 → field 14.
   - These checks run only when `enabled`. A disabled `Udp` with incomplete fields is fine.
7. **Ranges** (§6.1 comments), in iteration order → field 15 or 16. The firmware never clamps silently.

The DIY reference passes every step. Its sliders on 34/35/36 are input-only ADC1 pins, which is fine for sliders. GPIO 12 drives an LED: it's a strapping pin, but it is allowed (D5).

### 6.5 Blob identity and the CRC

- **Algorithm:** CRC-32 (IEEE 802.3, zlib `crc32`: reflected polynomial 0xEDB88320, init 0xFFFFFFFF, final XOR 0xFFFFFFFF). The CRC-32 of empty input is 0.
- **Byte identity, not semantic identity.** The CRC covers the **exact stored bytes**. Protobuf encodings are not canonical, so nothing ever re-encodes a config to compare it.
  - The mirror stores the exact bytes the app last saved or adopted.
  - The controller stores exactly the bytes it received.
  - "Same config" means "same bytes".
- Test vector: the CRC-32 of ASCII `123456789` is `0xCBF43926`. Both sides test it.

### 6.6 Storage

- **Location:** NVS namespace `volco`, key `cfg`, as a blob, through Arduino `Preferences`.
- **Saving:** the firmware writes only after decode and validate succeed. `putBytes` writes and commits (`nvs_set_blob` + `nvs_commit`). **Success means `putBytes` returned `total`.** Anything else is STORAGE_FAILED.
- **Atomicity:** NVS replaces a blob key atomically. A power loss during the write leaves the old value or the new one.
- **Boot:**

  | Stored key | Result |
  |---|---|
  | absent | defaults, configState 0 |
  | length > `kMaxConfigBlob` (foreign data) | defaults, configState 2, configCrc 0. ConfigRead returns `total` 0. |
  | fails decode or validate | defaults, configState 2, configCrc = CRC of the stored bytes |
  | otherwise | run it, configState 1 |

- **NVS init failure:** if NVS cannot initialise (`ESP_ERR_NVS_NO_FREE_PAGES` or `ESP_ERR_NVS_NEW_VERSION_FOUND`), the NVS partition is erased and re-initialised. The pinned Arduino-ESP32 core's `initArduino()` already does this; Spike S2 confirms it on the pinned version.
- **Wi-Fi driver:** the firmware calls `WiFi.persistent(false)`, so the Wi-Fi driver does not write its own copy of the credentials into the same NVS partition.
- **Partition table:** `platformio.ini` pins `board_build.partitions = default.csv`:

  | Partition | Offset | Size |
  |---|---|---|
  | NVS | 0x9000 | 0x5000 |
  | otadata | 0xE000 | — |
  | app0 | 0x10000 | 0x140000 |

### 6.7 Reboot after save

After replying OK, the firmware **processes no further frames and runs no more of the loop**. It:

1. flushes the reply's transport (`Serial.flush()` waits for the TX FIFO to empty; a UDP packet is sent with `endPacket()`)
2. waits 200 ms with a blocking `delay(200)`
3. calls `ESP.restart()`

This ensures no Hello is sent that reports the new CRC while the old config is still running, and that no retry burst is stored a second time before the reboot.

On serial the USB bridge stays enumerated, so Volco's COM port stays open. The ESP32 boot ROM log runs at 115200 baud and never forms a valid frame. The boot Hello then confirms the save.

## 7. Firmware changes in detail

- **Platform pinning:**
  - `platformio.ini` pins an **exact** platform version, so the bootloader, the core's NVS behaviour and the build output stay reproducible.
  - `platform = https://github.com/pioarduino/platform-espressif32/releases/download/55.03.38/platform-espressif32.zip` (Arduino-ESP32 3.3.8, ESP-IDF 5.5.4, esptool 5.2.0). This was already applied in stage 1. Before that, `platform = espressif32` resolved to Arduino 2.0.17 in CI and 3.3.8 on the dev machine.
  - The pioarduino platform needs **PlatformIO Core ≥ 6.1.19**, so CI installs `platformio==6.1.19`.
  - Exact library pins: `olikraus/U8g2@2.36.18`, `links2004/WebSockets@2.7.3` (until stage 3 removes it), and `nanopb/Nanopb@0.4.92`. That last one is the PlatformIO registry's name for nanopb 0.4.9.2; `@0.4.9.2` is rejected.
- **`main.cpp`:**
  - The arrays are sized by `kMaxSliders` and `kMaxButtons` and iterate over the runtime counts.
  - Debounce, sampling, filter (`filter_weight_pct / 100.0`) and deadband come from `RuntimeConfig`.
  - `setup()` runs, in order:
    1. `Serial.setRxBufferSize`, `setTxBufferSize`, `begin`
    2. load the config (§6.6)
    3. init the pins and indicators
    4. init the transports
    5. init the screens
    6. `Link` with the runtime counts and kinds
- **`screens.cpp`:**
  - The `constexpr` counting and the `static_assert`s become runtime code. Their rules are validation steps 3–4.
  - Panels are created once in `setup()`, keeping the per-bus approach from PR #64:
    - Each used bus is started from `RuntimeConfig`: buses 0 and 1 with `Wire.begin(sda, scl, clock_hz)` / `Wire1.begin(...)`; buses 2 and 3 are software I2C (PR #66's bit-banged `SoftI2c`, SCL push-pull, SDA open-drain), for fixed-address screens that need a bus of their own.
    - Each panel is the `Ssd1306` class writing through `i2cByte<bus>`. U8g2's `2ND_HW_I2C` driver sends nothing on ESP32 core 3.x, which is why PR #64 replaced it.
    - The panels live in a static `Screen[kMaxScreens]` array.
  - The timings come from `RuntimeConfig`.
- **`indicators.cpp`:** unchanged logic; reads `RuntimeConfig`.
- **`transport.cpp`** is rewritten:
  - Serial is always initialised.
  - **UDP** is initialised only if `udp.enabled`:
    - `WiFi.persistent(false)`, `WiFi.setAutoReconnect(true)`, `WiFi.mode(WIFI_STA)`, then `WiFi.begin(ssid, password)` **once**.
    - Init never blocks; today's wait-for-connect loop is removed.
    - The UDP socket binds to `local_port`.
    - The source filter and the active-transport rule (§5.5) apply.
  - **Wi-Fi retry:** `transport_maintain()`, which runs every loop pass, calls `WiFi.begin` again whenever the status isn't `WL_CONNECTED`, at most every `WIFI_RETRY_MS`. That includes `WL_CONNECTION_LOST` and `WL_IDLE_STATUS` (associated but no IP). It also retries a failed `udp.begin` on every pass while connected.
  - **Wi-Fi connect events:** the transition to `WL_CONNECTED` is noticed in the loop (a status edge), and it triggers the §5.5 Hello on UDP.
  - Wi-Fi is explicitly off (`WIFI_OFF`) when UDP is disabled.
  - The WebSocket and LOCAL_DEBUG code is deleted, along with the `links2004/WebSockets` dependency.
- **`lib/volco_protocol`:**
  - `encodeHello` takes the new fields, and there are encoders for ConfigData and ConfigResult.
  - Decoders for the four PC config frames.
  - `ActiveTransport`.
  - `Link` routes `0x87`–`0x8A`, together with the arriving transport, to a new `ConfigSink` interface. `main.cpp` implements it, connecting `BlobReceiver`, the store, the read reply and the reboot.
  - `Link::begin` and `hostLost` interact with `ActiveTransport` as §5.5 specifies.
- **`lib/volco_config`** (new, pure C++, no Arduino): `crc32`, `ChipInfo` and the ESP32 table, `decode` (nanopb, plus the NUL-in-string check), `validate`, `toRuntime`, `BlobReceiver`.
  - **The NUL check works on the wire bytes, not on the decoded struct.** nanopb copies a string into a zeroed `char[]`, so `"a\0"` and `"a"` look identical once decoded (stage 1: `strlen` was 1).
  - Each string field (`preset`, `udp.ssid`, `udp.password`) is checked as it is decoded, through a nanopb string callback or an equivalent pre-scan of the string fields' bytes.
- **Build id:** `-DVOLCO_FW_VERSION=\"…\"` is passed by CI (§9.2). The default is `"dev"`.
- **`platformio.ini` (the rest):**
  - Rename `denky32` → `esp32`.
  - **nanopb generation,** for both `esp32` and `native` (verified in stage 1):
    - `custom_nanopb_protos = +<../proto/volco_config.proto>`
    - `custom_nanopb_options = --error-on-unmatched`
    - `build_flags` gain `-DPB_VALIDATE_UTF8`
    - The `.options` file next to the proto is picked up automatically.
    - nanopb fills absent submessages with the proto2 defaults, so `toRuntime` reads values directly.
  - Remove the `*_check` environments.
  - `board_build.partitions = default.csv`.
  - A post-build manifest script (§9.1).
  - The `native` environment also builds `lib/volco_config` with nanopb.

## 8. App changes in detail

### 8.1 Core: protocol

- **`HelloMessage`** gains:
  - `Chip` (`ChipFamily { Unknown = 0, Esp32 = 1 }`)
  - `FirmwareVersion` (string)
  - `ConfigState` (`None = 0, Stored = 1, Damaged = 2`)
  - `ConfigCrc` (uint)

  A version-only `HelloMessage` (§5.2) has the defaults for these.
- **`ProtocolParser`** parses the new Hello per §5.2, plus ConfigData and ConfigResult. Short or overlong bodies are dropped.
- **`ProtocolEncoder`** adds `EncodeConfigRead(seq)`, and `EncodeConfigWrite(seq, blob) → byte[]` (the whole burst, §5.4).
- **`ControllerInfoConfig`** gains `Chip` and `FirmwareVersion`, and `SameAs` compares them too.
- **`SerialConnection`:** uses the fixed 115200 baud rate. Its error handling is otherwise unchanged. Stage 1 found that Windows raises no `ErrorReceived` when the board resets with the port open, and the ROM banner is clean at 115200.

### 8.2 Core: `Controller/`

- **`ControllerConfigCodec`:** wraps the generated `Volco.Core.Controller.Proto.ControllerConfig` (namespace `Volco.Core.Controller`, not `ControllerConfig`: under that name the message type would be shadowed by the namespace). It provides `Encode(model) → byte[]`, `Decode(byte[]) → model` (parse, then the §6.1 limit and NUL checks; throws `ControllerConfigFormatException`), and `Crc32(byte[])`.
- **`ControllerConfigValidator`:** the §6.4 steps and the ESP32 table, returning the same `(code, item, field)` triple.
- **`ControllerPresets`:** the embedded DIY reference, plus the hard-coded placeholder entry.
- **`ControllerConfigService`:** constructed and owned by `VolcoRuntime`.
  - **Operation state:** `Idle | Reading | Writing(phase)`.
  - **API:**
    - `ReadAsync()`: §5.3. It adopts the result as the mirror only when the caller asks.
    - `SaveAsync(byte[] blob)`: §5.4.
    - `Banner` (§8.3), with `BannerChanged` and `MirrorChanged` events.
  - **Sending:** through `ControllerLink.SendRaw(byte[] burst)`. That enqueues one item on the existing single-consumer outbox under `_gate`, and requires only an **attached** connection, not Ready. (The runtime's `HelloReceived` handling runs before the link goes Ready.)
  - **Receiving:** ConfigData, ConfigResult and Hello arrive from `VolcoRuntime.OnMessageReceived`, after the **same stale-sender filter** Hello uses today: messages from a connection other than the current one are dropped.
  - **Threading:** every `TaskCompletionSource` uses `TaskCreationOptions.RunContinuationsAsynchronously`, so no continuation runs on the serial `DataReceived` thread.
  - **Time:** a .NET 8 `TimeProvider` is added to `VolcoRuntimeOptions` (default `TimeProvider.System`) and passed to the service and `FirmwareService`. All their timeouts use it, and tests use `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing). Moving `ControllerLink` and `ButtonPressDetector` to it is not required.
  - **Connection lifecycle:** the runtime tells the service when the connection is *replaced* (config change, suspend, `Reconnect()`, dispose). A replacement fails an in-flight operation as §5.4 describes. A plain Detach and re-Attach of the same connection does not.
- **Mirror:** `AppConfig.ControllerConfigBlob` (`string?`, base64 of the exact bytes).
  - **Config schema v5:** adds `controllerConfigBlob` and removes `connection.serial.baudRate`.
  - **`ConfigManager.Load`** gains a `version == 4` branch: back up to `config.v4.bak`, stamp v5. It follows the existing `version == 3` pattern.
  - The mirror is written with `RequestSave()`.

### 8.3 What the page shows after a Hello (banner logic)

The banner is a **pure function** `Banner = f(lastHello, mirror, operation)`. It is recomputed whenever any of the three inputs changes. Repeated or duplicate Hellos therefore produce the same banner and no extra work. Reads start automatically **only** in the "no mirror" row; every other read is started by a user's click.

| Hello `configState` | Mirror | Banner | Actions |
|---|---|---|---|
| 1, CRC = mirror CRC | present | none (in sync) | — |
| 1, CRC ≠ mirror CRC | present | "This controller has a different setup." If "Use the controller's" fails (read error or undecodable bytes): "Couldn't read the controller's setup." | **Use the controller's** (read, adopt as mirror) · **Apply mine** (save the mirror bytes) · after a failed read also **Retry** |
| 1 | absent | none; an automatic read adopts it as the mirror. If the read fails, or returns bytes the app can't `Decode`: "Couldn't read the controller's setup." | **Retry** · **Set up from scratch** |
| 0 | present | "This controller isn't set up." | **Apply my saved setup** · **Set up from scratch** |
| 0 | absent | "Set up your controller." | **Set up** (opens Hardware, blank draft) |
| 2, CRC ≠ mirror CRC (or no mirror) | any | "The controller's saved setup can't be used." | **Apply my saved setup** (if mirror) · **Set up from scratch** |
| 2, CRC = mirror CRC | present | "Your saved setup doesn't work with this firmware." | **Fix it** (loads the mirror into the draft and shows the local validator's errors). There is no Apply, because it would just fail again. |
| version-only Hello (`version` ≠ 1) | any | "This controller's firmware is incompatible. Update it to use it." | **Update** (see below) |

**Rules:**

- **The version check comes first.** A version-only Hello has the default `ConfigState` (None), so the banner function tests `version` before any configState row.
- **A mirror that fails `Decode`** (for example, written by a newer app) is treated as **absent** for the table, and the page offers **Set up from scratch**.
- **During a write,** the banner shows the operation's progress instead, and Hellos go to the operation (§5.4).
- **During a read,** a Hello whose (configState, configCrc) equals the Hello the read started under is ignored. A ConfigRead sent after a host timeout provokes exactly such a Hello. A Hello that differs cancels the read, and the table is re-evaluated.
- **Disabled during test mode or an operation:** every action that writes to the controller (Apply mine, Apply my saved setup, Save, Install, Update, Check firmware).
- **Firmware notice:** "Firmware update available" shows when **all** of these hold:
  - connection type is Serial
  - Hello `Chip` is `Esp32`
  - the catalog has firmware
  - Hello `FirmwareVersion` ≠ the bundled manifest version
  - Hello `FirmwareVersion` ≠ `dev`. Development firmware (§9.4) is never offered an update automatically; the page shows "Development firmware", and **Install firmware…** still offers the bundled build.

  That excludes the simulator and alternative controllers.
- **Where the update is signalled.** When the firmware notice applies, or the firmware is incompatible (below), the update shows in three places:
  - **Controller page:** the banner and the Update button.
  - **Settings sidebar:** a small accent dot on the **Controller** navigation entry, with the accessible name "Controller needs attention". (Widened on 2026-10-05: the dot, and the tray below, also show for a controller that doesn't answer and for the setup states, from `ControllerBanner.NeedsAttention`.)
  - **Tray:**
    - The tray icon switches to a variant with a dot (`volco-update.ico`, next to `volco.ico`; set through Avalonia's `TrayIcon.Icon`, which is platform-free).
    - The tooltip gains a line: "Controller firmware update available".
    - The tray menu gains **Update controller firmware…**, which opens Settings on the Controller page.

  These three come from one view-model property, `FirmwareUpdateAvailable`, recomputed with the banner. They clear as soon as a Hello reports the bundled build id. There is still no balloon or toast; Avalonia has no API for one.
- **Protocol version mismatch:** a version-only Hello keeps the link out of Ready. Its `Chip` is unknown, so **Update** is offered whenever the connection type is Serial and the catalog has firmware; the flasher checks the chip itself (§8.6).

### 8.4 Editing model

- **The draft:** the page edits a draft, a C# `ControllerConfig` decoded from the mirror. With no mirror, the draft starts empty.
- **When editing is allowed:**
  - the link is Ready, **and**
  - the controller is in sync with the mirror (Hello configState 1 and CRC = mirror CRC), **or** the user chose an action that starts a new draft ("Set up", "Set up from scratch", "Fix it").

  Otherwise the page is read-only and shows the mirror. This stops a draft based on a stale mirror from overwriting a different controller setup without the user deciding.
- **How long a "new draft" permission lasts:** until the mirror changes, or a Hello arrives with a different (configState, configCrc).
- **Dirty:** `baseline = Encode(Decode(mirror))` is computed whenever the mirror changes, and **dirty** = `Encode(draft) ≠ baseline`. Both encodings use the same codec in the same process, so the comparison is deterministic, even when the mirror holds fields this app version doesn't know. "Apply mine" sends the original mirror bytes, never re-encoded.
  - With no mirror, or a mirror that fails `Decode`, the baseline is empty bytes. Any draft, even Blank (which encodes `preset` = `"blank"`), is then dirty.
  - **A "Fix it" draft always counts as dirty**, so Save is available. If it passes the local validator, the app and firmware disagree, so the page also shows the firmware-update notice when its conditions hold.
- **When the mirror changes** (adopt, confirmed save):
  - a **clean** draft is rebased onto the new mirror
  - a **dirty** draft is kept, and the page says "The controller's setup changed. Revert to load it."
- **Save:**
  - validate locally; on failure, highlight and don't send
  - otherwise encode and call `SaveAsync`
  - on confirmation the page shows "Saved. The controller restarted with the new setup."
  - Save is disabled while test mode is on and while an operation runs.
- **Revert** resets the draft to the mirror.
- **UDP warning:** saving changes to `udp` while the app is connected over UDP asks first: "Changing the Wi-Fi settings may disconnect the controller. If it doesn't come back, connect it with USB." When the user enables UDP, **PC port** is pre-filled from Volco's own UDP listen port.

### 8.5 Test mode (`VolcoRuntime`)

**API:**

```csharp
public bool InTestMode { get; }
public event EventHandler<bool>? InTestModeChanged;   // the runtime can end test mode itself
public IDisposable EnterTestMode();                   // dispose = exit; idempotent
public event EventHandler<RawInputEventArgs>? RawInput; // Slider/Button messages, in test mode only
public void SetTestIndicator(int button, TestIndicator state); // Off, On, A, B (LED buttons only)
```

**`ButtonPressDetector` changes** (`Input/ButtonPressDetector`, added to the §4 table as changed):
- **`Reset()`:** cancels and disposes both CTSs of every tracker (`_longPressCts`, `_singlePressCts`) and clears all tracker flags (`_longPressFired`, `_waitingForDoublePress`, …).
- **Each tracker tracks `_down`:** set on Pressed, cleared on Released. `OnReleased` returns immediately when `_down` is false, so **a release with no matching press does nothing**. Today such a release fires a Single.

**While in test mode:**

- **Input is blocked:**
  - Slider and Button messages raise `RawInput` and are **not** passed to `SliderProcessor` or `ButtonPressDetector`.
  - The runtime calls `ButtonPressDetector.Reset()` **on entry**, so a gesture started before test mode cannot fire during it.
  - **As a second guard,** the runtime's `OnButtonPressDetected` returns early while `InTestMode` is true.

  No audio call, action or OSD can happen.
- **Indicators show test state:**
  - `ControllerLink`'s compute delegate stays the same delegate (`_compute` is readonly; since PR #60 it is `Func<int?, …>` and takes the preset-change announce step). The runtime's lambda branches on a test-mode flag and **ignores the announce step in test mode**, so no preset blink or flash appears during a test:
    - each LED button shows its `TestIndicator` (default Off)
    - each screen button shows a test screen: top "Test", main "Button N", the generic icon
  - `SetTestIndicator` updates the state and calls `Push()`. Dedup and the outbox work unchanged.

**On exit:**

- The flag clears, the runtime calls `ButtonPressDetector.Reset()` **again**, and `PushAll()` restores the real indicator state.
- A button held across the exit: the detector never saw its press, so its release does nothing (`_down` is false).
- Sliders don't jump the volume: volume changes only on the next Slider message, as today.

**Exit is guaranteed:**

- The page disposes its handle when it is left or the settings window closes.
- The runtime ends test mode itself on connection replacement, on Detach, and on dispose. It raises `InTestModeChanged` so the page updates.

**`RawInput` is raised only in test mode.** The Test section shows live values only while a test is running, so normal operation has no extra path to throttle.

### 8.6 Firmware install, update, probe

**The seam (`Volco.Core/Firmware/IFirmwareFlasher`):**

```csharp
Task<ProbeResult> ProbeAsync(string port, FirmwareImage bundled, IProgress<FlashProgress>, CancellationToken);
Task FlashAsync(string port, FirmwareImage image, FlashMode mode, IProgress<FlashProgress>, CancellationToken);
enum FlashMode { Install, Update }
```

**The implementation is `EspDotNetFirmwareFlasher` in `Volco.Windows`,** handed over through `PlatformServices`. This is decided, not left to a spike. `ArchitectureTests` only reflects over `Volco.App` and `Volco.Core`, so it can't vouch for a dependency's P/Invokes, and keeping the dependency out of the platform-free projects avoids the question. Tests use a fake.

**`FirmwareImage` / `FirmwareCatalog`:** the bundled parts and the manifest (§9.1) from an `IFirmwareSource`. `Volco.Windows` supplies the embedded resources; tests and local builds get an empty source. `HasFirmware` is false when nothing is bundled.

**Flash procedure:**

1. Open the port, retrying for up to 1 s if Windows still holds it from the released connection.
2. DTR/RTS auto-reset into the ROM bootloader, sync, detect the chip, upload the stub loader.
   - **Switch to 460800 baud** (`ChangeBaudRateTool`) once the stub is running.
   - If the switch fails, carry on at 115200.
   - At 115200 a full install took about 67 s in stage 1; stage 6 verifies the speed-up (target under 20 s).
3. **Check the chip:** the detected chip must match the image's family; otherwise fail with "This board is an <chip>; this version of Volco only includes firmware for ESP32." There is no flash-size check: EspDotNet can't read the flash ID, a read-based proxy can't prove the size, and an undersized chip fails the write or the verify anyway.
4. **Write the parts:** each part at its manifest offset, then verify each part's MD5 with `SPI_FLASH_MD5`.
5. **Install only:** also write 0xFF over the whole NVS partition, 0x9000–0xDFFF. This clears any old or foreign settings. It avoids a full-chip erase, which needs the stub and takes 10–20 s, and it wipes exactly what matters.
6. Hard-reset via RTS.

**What Update protects.** Update **never writes 0x9000–0xDFFF**, so the config survives. The parts are always written separately, **never as one merged image**: a merged image fills the gaps between parts with 0xFF and would wipe NVS. Spike S1 verifies that the MD5 of 0x9000–0xDFFF is unchanged after an Update, which proves that no write's erase spills into NVS.

**Probe procedure:**

1. Enter the bootloader and detect the chip.
2. `SPI_FLASH_MD5` over `[app offset, app offset + app size)` from the manifest, compared with the bundled app MD5.
3. Hard-reset.

The result is one of: `NoDevice | UnsupportedChip(name) | VolcoMatches | DifferentFirmware`. `DifferentFirmware` covers blank flash, foreign firmware, or another Volco version.

**`FirmwareService`: one operation at a time, probe or flash:**

1. `using var suspend = runtime.SuspendConnection();`
   - This detaches `ControllerLink`, disposes the connection and frees the port.
   - Test mode and config operations end (connection replacement).
   - While any suspension is held, `StartConnection()` returns immediately, so a `ConnectionConfigChanged` restart or `Reconnect()` cannot reopen the port mid-flash.
   - **Race guard:** `StartConnection` also runs on the ConfigManager debounce thread, so it can pass that check just before a suspension starts. After publishing the new connection, it therefore re-checks `IsSuspended || IsDisposed`. If either is set, it retires that connection with `CompareExchange` and returns **without calling `Connect()`**. This is the same pattern used today for `IsDisposed`.
   - Disposing the suspension restarts the connection from the **current** config. Disposing it after the runtime is disposed does nothing.
2. Run the probe or flash, with progress shown.
3. If it succeeded and the dialog's port differs from the configured one, **write the new port to the config while still suspended**.
4. Dispose the suspension, which restarts from the current config, now with the new port.
5. **After a flash,** wait up to 10 s for a Hello. Success means `FirmwareVersion` = the bundled manifest version. The page then offers "Test your controller now". No Hello → "The firmware was installed but the controller didn't answer. Check the connection settings."

**Rules:**

- **Serial only:** probe, Install and Update require the connection type to be **Serial**. Otherwise the buttons are disabled with "Connect the controller with USB and set the connection to Serial."
- **Port:** the dialog's port defaults to the configured `Serial.ComPort` and lists `SerialPort.GetPortNames()`. A different port chosen in the dialog is written to the config only at step 3, so it can't trigger a restart mid-operation.
- **When the probe runs:**
  - when the user clicks **Check firmware**
  - when they accept the hint shown after the serial port has been open for 5 s with no Hello: "No Volco controller answered on COM3. Check what's connected?"
  - It resets the board, so it never runs automatically.
- **Probe results:**

  | Result | Message | Action |
  |---|---|---|
  | `NoDevice` | "No ESP32 found on COM3." | — |
  | `UnsupportedChip` | the chip message above | — |
  | `VolcoMatches`, and the board's Hello (bundled build) arrives within 10 s of the resume | "This controller runs Volco's firmware <id>." | — |
  | `VolcoMatches`, no Hello within 10 s | "Volco's firmware is installed but didn't answer. Reinstall it?" | Install |
  | `DifferentFirmware` | "This ESP32 doesn't have this version of Volco's firmware." | Install |

- **Cancellation:** possible only before the first write. After that the dialog can't be cancelled.
- **Failure mid-write:** "Install failed: <reason>. The controller won't work until the install succeeds. Try again." The ROM bootloader is in mask ROM, so the board always stays recoverable over USB.

### 8.7 The Controller page (UI)

A sixth settings page, **Controller**, after Connection. The sections, top to bottom:

**1. Status and firmware**
- Connection state, chip, firmware version, bundled version.
- Buttons: Install firmware…, Update firmware (under the §8.3 conditions), Check firmware.
- The §8.3 banner appears here.

**2. Hardware**
- **Preset picker:** DIY reference / Volco PCB (coming soon, disabled) / Blank.
  - Choosing one when the draft's hardware is non-empty asks "Replace the current hardware with <preset>?"
  - A caption shows "Based on DIY reference" or "Based on DIY reference (modified)". "Modified" means the draft's hardware fields encode differently from the preset's hardware fields.
- **Sliders:** a list. Each row: "Slider N" and pin. "Add slider" appends at the end. Removing a row asks for confirmation when later rows exist: "Sliders after it are renumbered, and their actions move with the number." **No drag reordering:** under D3, actions are tied to input numbers, so reordering would silently move actions to other physical controls.
- **Buttons:** the same list rules. Each row:
  - "Button N" and the pin.
  - Indicator: None / LED / LED pair / Screen.
  - LED: pin and "Lights when the pin is: High / Low".
  - LED pair: pin A, pin B and polarity.
  - Screen: bus 0–3 (2 and 3 are software I2C) and address 0x3C/0x3D.
- **I2C buses:** rows for buses 0–3 (SDA, SCL), each shown only when a screen uses that bus.
- **Hints** (informational, never blocking):
  - a button on GPIO 34–39: "This pin has no internal pull-up; add a resistor to 3.3 V."
  - a slider on ADC2 while UDP is off: "This pin can't be read if you enable Wi-Fi."
- **Inline errors** come from the local validator, or from a ConfigResult INVALID mapped through (`item`, `field`) to the row and field.

**3. Test** (§8.5)
- **Start test / Stop test.**
- A live bar plus raw value per slider, and a lamp per button lit while pressed.
- Per LED button: Off / On (LED) or Off / A / B (pair). Screen buttons always show the test screen while testing.
- A note: "While testing, the controller doesn't change volume or run actions."

**4. Behaviour**
- Screen brightness.
- Dim when idle (on/off, after, dimmed brightness).
- Turn off when idle (on/off, after).
- Pixel shift (on/off, every).
- "A press that wakes the screens isn't sent to the PC."
- Scrolling (hold, speed, end hold), slide duration.
- Shown only when a button has a screen. Otherwise a single line says these apply to screens.

**5. Advanced** (collapsed by default)
- Input tuning: debounce, slider sample interval, smoothing (%), deadband.
- I2C clock per used bus.
- **Wi-Fi (UDP):** enable; SSID; password (masked, with a show toggle); PC IP address; PC port; controller port. A hint: "USB keeps working. If the Wi-Fi settings are wrong, fix them over USB."

**Footer** (sticky while dirty): **Save to controller** and **Revert**.
- During a save it shows a progress line: *Sending… → Restarting… → Saved ✓*, or the error.

**Error messages:**

| Situation | Message |
|---|---|
| INVALID | "The controller rejected the setup: <field description> on <Button 2 / Slider 1 / I2C bus 0 / Wi-Fi>." The field is highlighted. |
| DECODE_FAILED, TOO_LARGE | "The controller couldn't read the setup. Update the firmware and try again." |
| STORAGE_FAILED | "The controller couldn't save the setup. Its previous setup is unchanged." |
| Timeouts and connection loss | the §5.4 messages |

**The Connection page** loses the baud rate field. Everything else is unchanged.

## 9. Build, CI, release

### 9.1 Firmware artifact

A post-build `extra_scripts` step (`firmware/scripts/manifest.py`) **copies all four parts** into `.pio/build/esp32/dist/` and writes `firmware-manifest.json` there. It copies each part from the path PlatformIO reports:
- `boot_app0.bin` lives in the framework's `tools/partitions/`
- `bootloader.bin` may be a prebuilt SDK file

```json
{
  "chip": "esp32",
  "version": "<build id>",
  "parts": [
    {"name": "bootloader", "file": "bootloader.bin", "offset": 4096,  "size": 0, "md5": "…"},
    {"name": "partitions", "file": "partitions.bin", "offset": 32768, "size": 0, "md5": "…"},
    {"name": "boot_app0",  "file": "boot_app0.bin",  "offset": 57344, "size": 0, "md5": "…"},
    {"name": "app",        "file": "firmware.bin",   "offset": 65536, "size": 0, "md5": "…"}
  ],
  "nvs": {"offset": 36864, "size": 20480}
}
```

- The offsets come from PlatformIO's `FLASH_EXTRA_IMAGES` and the application offset. The NVS range comes from the partition table. Nothing is hard-coded in the script.
- `size` and `md5` are computed by the script.
- The script **fails the build** if any part overlaps the NVS range.
- The app's manifest parser rejects the same overlap (tested).

### 9.2 Build id

**The build id is a hash of the firmware's source, not a release version.** A release whose firmware source didn't change therefore produces the same id, and the app offers no update.

- **Why not hash the binary:** Arduino/ESP-IDF builds embed the compile date and time, so the same source gives a different binary on every build.
- **How CI computes it** (in `firmware-test` and in the release `firmware` job), from git's content hashes of exactly the files that make up the firmware:
  ```bash
  git rev-parse HEAD:firmware/src HEAD:firmware/lib HEAD:firmware/platformio.ini \
                HEAD:firmware/scripts HEAD:proto/volco_config.proto HEAD:proto/volco_config.options \
    | sha256sum | cut -c1-12
  ```
  - `platformio.ini` carries the exact platform and library pins, so a toolchain change also changes the id.
  - README, tests, presets, generated simulator files and app code don't affect it.
  - `firmware/scripts/` holds the build scripts (the manifest script). `firmware/tools/` (asset generators) is not included: its outputs are committed under `firmware/lib`, which is.
  - **The list is defined once,** in `firmware/scripts/firmware_id.sh`. CI calls it and passes the result as `VOLCO_FW_VERSION`. A unit check in `firmware-test` fails if a directory referenced by `platformio.ini` (`src_dir`, `lib_dir`, `extra_scripts`) isn't in the list.
- **Format:** 12 lowercase hex characters, e.g. `3f9c2a1b5d07`. The manifest also carries `displayVersion` (the release version, or `ci+<short sha>`), but **the comparison uses only the id**. The page shows "Firmware 3f9c2a1b5d07", plus "(from Volco 1.2.0)" when the manifest's id matches.
- **The app compares Hello's `fw` with the bundled manifest's `version`** (the id). The app's own version is not involved.
- **Development firmware:** a local PlatformIO build reports `dev` (§9.4). The app never offers an update for it.
- A local build of the app has no bundled firmware, so it never offers an update.

### 9.3 Workflows

**`build.yml`:**
- **`firmware-test`:**
  - runs `pio test -e native`
  - computes the build id
  - builds `esp32` with `VOLCO_FW_VERSION`
  - runs the simulator proto check (below)
  - uploads the artifact `firmware-esp32` (the `dist/` folder)
  - exposes the build id as a job output
- **The Windows `build` job:**
  - `needs: firmware-test`
  - downloads `firmware-esp32` into `src/Volco.Windows/Firmware/esp32/`
  - publishes with `-p:RequireFirmware=true -p:VolcoFirmwareBuildId=<output>`
- **`Volco.Windows.csproj`:**
  - embeds `Firmware/**` as `EmbeddedResource`
  - when `RequireFirmware` is true, a build target **fails** if `Firmware/esp32/firmware-manifest.json` is missing, or if its `version` ≠ `$(VolcoFirmwareBuildId)`
  - local builds leave `RequireFirmware` false
- The Linux .NET test job doesn't need the firmware.

**`release.yml`:**
- A new Linux `version` step/job resolves the version once and exposes it as an output. (Today the version is resolved inside the Windows job.)
- A Linux `firmware` job runs `pio test -e native`, builds with the release version, and uploads `firmware-esp32`.
- The Windows release job `needs` both, embeds the firmware with `RequireFirmware=true`, and attaches `volco-firmware-esp32-<version>.zip` (parts and manifest) to the release.
- **Licensing:** releases now ship a firmware binary, both embedded and in the zip. The zip and the app's About/licence notices include `firmware/assets/fonts/OFL.txt` (Arimo) and a `THIRD-PARTY-NOTICES.txt` covering nanopb (zlib), U8g2 (BSD-2) and Arduino-ESP32 / ESP-IDF (LGPL-2.1 / Apache-2.0), with each licence's text.

**Protobuf generation:**
- **C#:** `Volco.Core.csproj` references `Google.Protobuf` and `Grpc.Tools` (`PrivateAssets="all"`), with `<Protobuf Include="../../proto/volco_config.proto" ProtoRoot="../../proto" GrpcServices="None" />`. Grpc.Tools bundles protoc for Linux and Windows. Spike S3 verifies it in both CI jobs.
- **Firmware:** nanopb's PlatformIO integration generates code at build time in both the `esp32` and `native` environments.
- **Simulator:**
  - `tools/simulator/test/gen_descriptor.mjs` (Node, protobufjs) writes `tools/simulator/volco_config_descriptor.js`, the schema as a protobufjs JSON descriptor in a script (the simulator opens from file://, where fetch fails).
  - The output is committed.
  - The `simulator-test` job re-runs it and fails on a diff.

### 9.4 Development firmware (PlatformIO)

The firmware stays a normal PlatformIO project, so a developer can build and flash their own firmware without the app:

```bash
cd firmware && pio run -e esp32 -t upload   # build and flash local firmware
cd firmware && pio device monitor           # DEBUG_LOG output
```

- **It is the same generic firmware.** A local build reports build id `dev`, unless `VOLCO_FW_VERSION` is set.
- **The config survives.** `pio run -t upload` writes the bootloader, the partition table, `boot_app0` and the app at their offsets, and never touches NVS. Switching between Volco-installed and PlatformIO-installed firmware keeps the controller's config. Stage 1 confirmed that the upload's offsets never include NVS.
- **The bootloader is patched on upload.** PlatformIO's upload passes `--flash-mode/--flash-size`, so esptool patches the bootloader header, and the bootloader in flash then won't match `bootloader.bin`. That's harmless, because the probe compares the app region only.
- **A blank board** flashed this way boots with built-in defaults, and the app sets it up as usual.
- **The app treats `dev` as development firmware:**
  - it shows "Development firmware"
  - it offers no update and shows no indicators
  - **Install firmware…** stays available to go back to the bundled build
- **Hello's other fields are unchanged.** A `dev` board otherwise behaves exactly like an installed one.
- **README and CLAUDE.md** keep these commands, under "Developing the firmware".

## 10. Simulator changes

- **New Hello:** chip 1, fw `"sim"`, plus `configState` and `configCrc` from its in-memory config. Sliders, buttons and kinds come from the stored config when there is one, and otherwise from the selected controller type.
- **Echo `seq`** in every ConfigData and ConfigResult.
- **ConfigRead:** returns the in-memory blob in chunks.
- **Config write:**
  - Follows the §5.4 receiver rules. "Validation" here means decode with protobufjs plus the limit and NUL checks, the counts, and the screen rules of step 3. Pin rules don't apply to a simulator.
  - On OK it simulates a reboot: the link drops for 1 s, then sends Hello. (The app treats this as a Detach and re-Attach, §5.4.)
- **Persistence:** the config is kept in `localStorage` (wrapped in try/catch). A "Clear config" button resets it.
- **protobufjs** loads from cdnjs or jsdelivr. There is still no build step.
- **Test mode needs nothing in the simulator.** It is app-side.

## 11. Error handling summary

| Failure | Handled by | Result |
|---|---|---|
| UDP datagram lost during write | BAD_SEQUENCE/BAD_CRC or no result → new attempt with new `seq` | Usually invisible. |
| OK lost over UDP | Implied OK: the next Hello carries the new CRC | Confirmed. |
| Stale results from an earlier attempt | `seq` mismatch → ignored | — |
| Reboot drops the transport | Detach + re-Attach allowed during the save | Confirmed by the post-boot Hello. |
| Connection replaced mid-save | Operation fails | "Connection lost while saving…". Mirror unchanged. |
| Power loss during NVS write | NVS atomicity | Old or new config; the next Hello's CRC says which. |
| Stored config fails new firmware's rules | Boot → configState 2 | "Doesn't work with this firmware" → Fix it. |
| Wrong Wi-Fi settings | Serial always on, unlatched sends | Fix over USB. |
| Volco on UDP, controller rebooted | Unlatched Alive on UDP, Hello on Wi-Fi connect | Reconnects. |
| Restart path during flash | Suspension blocks `StartConnection` | Port stays free. |
| Flash fails mid-write | Flasher error | Retry message; ROM bootloader always recoverable. |
| Board never answers Hello | 5 s hint → probe | The probe says what's on the port. |
| Foreign NVS after Install | Install wipes the NVS partition | Clean NVS. |
| NVS init failure at boot | Core erases and re-inits | Defaults. |
| Protocol version mismatch | Version-only Hello; link not Ready | Only Update offered. |
| Old-format Hello | Dropped as malformed | "No Hello" hint → probe → Install. |

## 12. Testing

### 12.1 Shared golden vectors (both test suites)

- **`firmware/test/golden/frames.txt`** gains:
  - Hello in each `configState`, LED and screen hardware, a version-2 Hello (the version-only parse), and the old-format Hello (dropped)
  - ConfigRead
  - ConfigData: empty, single-chunk and multi-chunk
  - a full write burst
  - every ConfigResult code
- **New `firmware/test/golden/config.txt`**, where each case is a name, a blob in hex, and an expectation:
  - **`decode-ok <field assertions>`:** the DIY reference; the maximal config (asserting ≤ 1024 bytes); an empty blob (all defaults); a blob with an unknown field; `screens.brightness` only; `screen {}` → bus 0, address 0x3C.
  - **`decode-fail`:** truncated; bad varint; SSID of 33 bytes; 33 buttons; 17 sliders; a NUL inside a string; invalid UTF-8 in a string.
  - **`invalid <code> <item> <field>`:** at least one case per step and field in §6.4. That includes reserved, nonexistent and input-only pins for each role; ADC2 + UDP; each duplicate-pin pairing (slider/button, button/LED, LED/I2C); a duplicate (bus, address), which is also what a third screen on a bus produces; a missing bus; equal SDA and SCL; each UDP rule (including a 7-byte password, a broadcast IP and a loopback IP); and the minimum and maximum of every range field. Plus **ordering cases**, where a config with two violations must report the first by §6.4 order.
  - **`crc <hex>`:** CRC vectors, including `123456789` → `CBF43926` and empty → `00000000`.
- **Generation:** `firmware/tools/gen_config_vectors.py` (protobuf Python package) produces the blobs once, and they are committed. Both suites read the file and check every expectation. The firmware embeds it the way `golden_frames.h` embeds `frames.txt`, and the C# side reads it through `RepoPaths`.

### 12.2 Firmware native tests (`pio test -e native`)

- **`test_config`:** driven by `config.txt`. Also `toRuntime` unit conversions and the chip-table sets.
- **`test_config_receiver`:**
  - happy path, 1 chunk and 5 chunks
  - wrong `seq` within a transfer, a gap, an overrun, End early → one BAD_SEQUENCE
  - a chunk or End with no transfer → no output
  - frames from another transport → ignored
  - a Begin mid-transfer restarts it
  - TOO_LARGE (`total` 0 and `total` 1025)
  - the timeout closes silently
  - **at most one result per Begin**, across every scenario
- **`test_protocol`:**
  - new Hello encoding against the goldens
  - config frame decode
  - `Link` routes 0x87–0x8A with their transport
  - config frames count as proof of life
  - **`ActiveTransport`:** unlatched sends go to all transports that are up; the first frame latches before the reply; a frame from the other transport moves the latch; `hostLost` clears it; the Wi-Fi-connected edge sends a Hello on UDP
  - `acceptUdpSource`

### 12.3 C# tests (`Volco.Core.Tests`)

- **Codec and validator:** driven by `config.txt`, including the limit, NUL and ordering cases. The DIY reference loads and validates.
- **Protocol:** new goldens; a version-2 Hello gives a version-only message; an old-format Hello is dropped.
- **`ControllerConfigService`**, with a fake connection and fake time:
  - **Read:**
    - assembly
    - frames with a stale `seq` are ignored
    - a gap or CRC mismatch → a new attempt
    - three failures → error and Retry
    - a Hello equal to the read's starting Hello is ignored; a different Hello cancels the read
  - **Save:**
    - OK + confirming Hello → mirror updated
    - OK + Hello with the old CRC → error, mirror unchanged
    - OK + no Hello → error (10 s serial, 20 s UDP)
    - BAD_CRC once, then OK → two attempts, and attempt 1's leftover results are ignored
    - **OK lost, then a new-CRC Hello → confirmed**
    - an unexpected Hello before the result → next attempt
    - INVALID → no retry; `item` and `field` surfaced
    - no result × 3 → error
    - **Detach and re-Attach during confirming → still confirmed**
    - connection replaced mid-save → "Connection lost while saving", mirror unchanged
    - a second request while busy → rejected
    - closing the page doesn't cancel the save
  - **Encode:** a Blank draft encodes to a non-empty blob (`preset` = `"blank"`).
  - **The final implied-OK wait:** after the 3rd unanswered attempt, a new-CRC Hello within the confirming timeout → confirmed. `SendRaw` with no attached connection → counted as unanswered.
  - **Banner function:**
    - every row of the §8.3 table
    - the version-only Hello is checked before the configState rows
    - an undecodable mirror is treated as absent
    - the failed-read states offer Set up from scratch
    - **repeated identical Hellos → one automatic read and an unchanged banner**
    - the firmware-notice conditions (simulator excluded)
    - Update offered on a version mismatch even though `Chip` is unknown
- **Test mode:**
  - Slider and Button messages → zero `IAudioEngine` calls, zero action executions, and `RawInput` raised.
  - **A button held across entry:** its long-press timer doesn't fire an action.
  - **Held across exit:** the release fires nothing, with `doublePressWindowMs` both 0 and non-zero.
  - **`ButtonPressDetector.Reset()`:** pending timers never fire after it; all flags are cleared.
  - **An unmatched release:** no event.
  - `RawInput` is not raised outside test mode.
  - `SetTestIndicator` → the expected Led and Screen frames.
  - Exit → `PushAll` restores the real state.
  - The runtime ends test mode on Detach and on dispose, raising `InTestModeChanged`.
- **Suspension:**
  - `SuspendConnection` releases the connection.
  - **A connection config change and `Reconnect()` while suspended don't open a connection.**
  - **A config change racing a suspend** (`StartConnection` past its first check when the suspension starts) → the published connection is retired and `Connect()` is never called.
  - Dispose restarts from the current config.
  - Dispose after the runtime is disposed does nothing.
- **`FirmwareService`** (fake flasher):
  - suspend and resume around both probe and flash
  - progress forwarded
  - a failure mid-flash → error and the connection resumed
  - no Hello after flash → error
  - the dialog's port is persisted after success while still suspended, and the resumed connection and the Hello wait use the new port
- **Manifest:** a valid manifest parses; a part overlapping NVS is rejected.
- **Config migration:** v4 → v5 writes `config.v4.bak`, removes `baudRate`, and stamps v5.

### 12.4 App tests (`Volco.App.Tests`, headless)

- **Controller page view model:**
  - the editing-allowed rule (§8.4)
  - dirty against `baseline`; **a mirror with unknown fields is not dirty**
  - a clean draft is rebased on a mirror change; a dirty draft is kept, with the notice
  - a preset applied keeps the behaviour and UDP fields
  - "modified" detection
  - inline error mapping from (`item`, `field`)
  - the remove-row renumber confirmation
  - Save disabled during test mode and during operations
  - every probe result → its message
  - Install/Update disabled when the catalog is empty or the connection isn't Serial
  - the UDP-change warning
  - `FirmwareUpdateAvailable` drives the sidebar dot, the tray icon variant, the tooltip line and the tray menu item, and all four clear on a Hello with the bundled build id
- `ArchitectureTests` stays green; EspDotNet is referenced only by `Volco.Windows`.

### 12.5 CI checks (tests of the pipeline itself)

- A deliberately missing manifest with `RequireFirmware=true` fails the build. This is checked once while implementing, and documented.
- The stale `volco_config_descriptor.js` check fails on a diff.

### 12.6 Spikes (done in stage 1)

All answered: results and decisions are in `docs/superpowers/spikes/2026-10-03-controller-config-spikes.md`, and the spec above includes the approved amendments. The list below is kept as the record of what was asked.

- **S1, EspDotNet on the real DIY board:**
  - target framework compatibility with `net8.0-windows`
  - auto-reset into the bootloader; chip and flash-size detection
  - writing the four parts at their offsets
  - `SPI_FLASH_MD5` (add the command if missing; it's in Espressif's documented serial protocol, and the library is MIT-licensed)
  - writing 0xFF over the NVS range
  - hard reset
  - **the MD5 of 0x9000–0xDFFF unchanged after an Update**
  - EspDotNet does not modify the bootloader header (so the manifest MD5 still matches after writing)
  - install and probe timings
  - which `SerialPort.ErrorReceived` events fire when the board reboots with the port open
- **S2, the firmware toolchain:**
  - the exact pinned platform version
  - nanopb generation from `../proto` in `esp32` and `native`, with the exact `custom_nanopb_*` keys
  - proto2 defaults present in the generated initialiser
  - `initArduino` NVS erase-on-failure on the pinned core
  - the manifest script finding all four parts
- **S3, Grpc.Tools:** generation in the Linux test job and in the Windows single-file publish.

### 12.7 Manual hardware checklist (`docs/superpowers/checklists/`)

1. Install on a blank ESP32 → Hello, "Set up your controller".
2. Apply the DIY reference → save → reboot confirmed → test mode shows all 5 sliders and 3 buttons; LEDs toggle; volume doesn't change during the test.
3. Normal mode: volume and mute work; the actions are unchanged.
4. Change a pin → save → the old pin no longer reacts and the new one does.
5. Update firmware (a second build id) → the config survives (same CRC, no banner).
5a. Flash a `dev` build with `pio run -t upload` → the config survives; the page shows "Development firmware" with no update indicator; Install brings back the bundled build.
5b. Release dry run: two consecutive CI builds with no firmware change → the same build id → no update offered.
6. Enable UDP with a wrong password → save → still works over USB. Fix it → set the app's connection to UDP → it connects. Then save a behaviour change over UDP → confirmed after the reboot.
7. UDP from a different PC IP is ignored.
8. Unplug during a save → the old config, or the new one, is reported correctly after reconnecting.
9. Unplug during a flash → Install again succeeds.
10. Probe: a blank board → "doesn't have this version of Volco's firmware"; another Volco build → the same; a board with no ESP32 on the port → "No ESP32 found".
11. Install over a configured board (NVS wiped) → "This controller isn't set up" (mirror kept) → Apply my saved setup.
12. Screens: a layout with screens from the form → screens render; behaviour settings take effect after save.
13. Reboot with the COM port open (save over serial) → no "Connection lost"; confirmed.

## 13. Documentation updates

- **`firmware/README.md`:**
  - remove the `config.h` editing guide
  - describe the config model, the schema, the protocol additions, the active-transport rule and the chip table
  - keep the wiring guidance as advice, adding the pull-up and module caveats: GPIO 37/38 aren't pinned out on WROOM modules, and 16/17 are used by PSRAM on WROVER/PICO boards
- **`CLAUDE.md`:**
  - the firmware section, the protocol table, the build commands (`esp32` environment, no `*_check` environments)
  - CI (the artifact flow, `RequireFirmware`)
  - the Core/Windows split for the flasher
  - `pio test -e native` needs a host C/C++ compiler (`g++`). CI has one; a fresh dev container may not.
- **`tools/simulator/README.md`:** config support.
- **Root `README.md`:** the install and setup story.

## 14. Open points

1. **The Wi-Fi password is plain text** in NVS and in `config.json`'s mirror, as in `config.h` today. Accepted (§3).
2. (Resolved) Firmware updates are offered only when the firmware source changed (§9.2).
3. **The firmware notices and the LGPL.** The Arduino core is LGPL 2.1, so a release that ships the firmware must let its users get the firmware's source to relink it. **Decided 2026-10-06 (the user): option C.** The repository stays private, and every release's firmware zip carries the firmware's object files and link command so it can be relinked with a modified core: `firmware/scripts/relink_bundle.py` writes `relink/` (the link inputs built for the firmware, the exact link command, and a README), and `release.yml` puts it in the zip. Relinking the unchanged objects reproduces `firmware.bin` byte for byte (checked 2026-10-06).

### 14.1 Deferred review findings

Minor findings each stage's final review raised and the user hasn't asked for yet. None blocks a release; each says what goes wrong if it's left.

| From | Finding | If left |
|---|---|---|
| Stage 4b | A Hello from a just-replaced connection can arrive after the replacement. | The Controller page briefly shows the old controller's banner. |
| Stage 4b | The config is saved debounced (`RequestSave`), and nothing flushes it at exit. | A save confirmed in the moment before Volco exits can be missing from the mirror; the next start shows "different setup", and "Use the controller's" fixes it. |
| Stage 5 | Clearing a number box in Screens or Advanced is dropped by the binding. | The box shows empty while the old value is kept. |
| Stage 6 | Exiting Volco during a flash isn't blocked or warned about. | The write stops midway; Install again recovers (the ROM bootloader always can). |
| Stage 6 | `FirmwareService` saves a new port from a thread-pool thread. | A rare race with a settings edit made at the same instant. |
| Stage 6 | `dist/` is rewritten only when `firmware.bin` relinks. | Locally, changing only `VOLCO_FW_DISPLAY` leaves a stale manifest; CI builds are always fresh. |
| Stage 6 | An install takes about 29 s at 460800 baud (measured 2026-10-05), over the §8.6 target of 20 s; 921600 with fallbacks is the next step. The user will handle it in a separate session. | Installs and updates take about half a minute. |
| Stage 6 | `CheckFirmware` (the build-id match) runs only in the Windows build and release jobs. | A mismatch shows only when those run; the resource names are checked on every push. |

Closed 2026-10-06: `transport_begin_udp` runs once, in `setup()`, and every config save reboots, so nothing restarts UDP at run time.

## 15. Delivery stages

The work is delivered in stages. **Each stage is its own PR that merges to `main`** with green CI and its listed checks, so there is never a long-lived branch. Between stages, `main` must always build, pass its tests, and drive the DIY hardware.

| Stage | Contents | Done when | User-visible change |
|---|---|---|---|
| **1. Spikes** | S1 (EspDotNet on the DIY board), S2 (platform pin, nanopb, NVS behaviour of `initArduino` and `pio upload`), S3 (Grpc.Tools in both CI jobs). Throwaway code; results recorded in the plan. | Each question answered. Any spec change it forces is agreed with the user. | none |
| **2. Schema and codecs** | `proto/`, nanopb and Google.Protobuf generation, `volco_config` (decode, validate, chip table, CRC, `BlobReceiver`), the C# codec and validator, the DIY preset, `config.txt` goldens, `firmware_id.sh`. Nothing calls it yet. | Both test suites pass the shared vectors. | none |
| **3. Firmware on runtime config** | `RuntimeConfig` throughout `src/`; NVS load at boot; dual transport with the active-transport rule; the WebSocket client and LOCAL_DEBUG removed; platform pinned. Until stage 4, the stored config is seeded from the DIY preset blob when NVS is empty, which keeps today's hardware working with the current app. The seed is removed in stage 4. | Native tests pass. Checklist items 3, 4 (pin change via a re-seeded blob) and 12 pass on hardware. | The ESP32 behaves the same; the WebSocket client is gone. |
| **4. Protocol and config session** | New Hello, config frames, `seq`, the firmware receiver/store/reboot, `ControllerConfigService`, the mirror and schema v5, the simulator's config support, the banner logic (Core only). The DIY seed is removed. | Goldens, service and banner tests pass. The simulator's Node test round-trips a config. A write is confirmed on hardware with `config_tool.py` over serial (stage 4a). Items 6, 8 and 13 need a UI to start a save from Volco, so they moved to stage 5. | Baud setting removed. Config can be saved through tests or the simulator; no UI yet. |
| **5. Controller page and test mode** | The page (status, hardware, test, behaviour, advanced), the editing model, `ButtonPressDetector.Reset`, test mode, the sidebar dot. | App tests pass. Items 2, 3, 4, 6, 8, 11, 12 and 13 pass. | Configure and test the controller from Volco. |
| **6. Firmware install and CI** | The flasher in `Volco.Windows`, `FirmwareService`, the probe, the suspension guard, embedded firmware with `RequireFirmware`, the build id, the release zip and notices, the tray indicators, the docs (§13). | Items 1, 5, 5a, 5b, 7, 9 and 10 pass. A release dry run produces an exe with firmware. | Install/update from Volco, update indicators, dev-firmware handling. |

**Each stage gets its own implementation plan,** written when the previous stage merges, so later plans can include what earlier stages taught us.

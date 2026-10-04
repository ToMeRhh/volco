# Controller config: stage 1 spike results

Spec: `docs/superpowers/specs/2026-10-03-controller-config-design.md` §12.6. Plan:
`docs/superpowers/plans/2026-10-03-controller-config-stage1-spikes.md`.

## S2: firmware toolchain

| Question | Answer | Evidence |
|---|---|---|
| Unpinned resolution on a fresh machine (= CI before this stage) | **platformio/espressif32 7.1.3 with framework-arduinoespressif32 4.20017.x, i.e. Arduino core 2.0.17.** This differs from the dev machine (3.3.8). | `pio pkg install -e denky32` with an empty `PLATFORMIO_CORE_DIR` |
| Dev machine platform/framework | pioarduino 55.03.38 / Arduino 3.3.8 | `~/.platformio/platforms/espressif32/platform.json` |
| Pinned platform builds `denky32`, the 3 check envs and `native` locally | **Yes.** 4× SUCCESS; native 45/45 passed (after installing host `g++` in the container) | local run |
| CI builds with the pinned platform and logs framework 3.3.8 | **Yes**, after bumping CI's `pip install platformio` from 6.1.18 to **6.1.19**. pioarduino 55.03.38 requires Core ≥ 6.1.19, and 6.1.18 fails with `IncompatiblePlatform`. The CI log shows `framework-arduinoespressif32 @ 3.3.8`, and all four ESP32 envs build. | runs 37150867357 (fail, 6.1.18) and 37151017070 (pass) |
| Exact U8g2 / WebSockets versions pinned | U8g2 **2.36.18**, WebSockets **2.7.3** (what `^` resolved to). Platform 55.3.38 brings Arduino 3.3.8, ESP-IDF 5.5.4 and esptool 5.2.0. | `pio pkg list -e denky32` |
| Flash parts and offsets (`FLASH_EXTRA_IMAGES`, app offset) | `0x1000` bootloader.bin, `0x8000` partitions.bin, `0xe000` boot_app0.bin, app `0x10000` firmware.bin | `spike_layout` env |
| Where `bootloader.bin` and `boot_app0.bin` come from | bootloader.bin and partitions.bin: **build dir** (`.pio/build/<env>/`). boot_app0.bin: **framework** (`framework-arduinoespressif32/tools/partitions/boot_app0.bin`). | `spike_layout` env |
| Partition table (nvs / otadata / app0 offsets and sizes) | default.csv: nvs 0x9000/0x5000, otadata 0xE000/0x2000, app0 0x10000/0x140000, app1 0x150000/0x140000, spiffs 0x290000/0x160000, coredump 0x3F0000/0x10000. Matches spec §6.6. | `tools/partitions/default.csv` |
| `pio run -t upload` command line: which offsets it writes | `esptool ... write-flash -z --flash-mode dio --flash-freq 40m --flash-size detect 0x1000 bootloader 0x8000 partitions 0xe000 boot_app0 0x10000 firmware`. It **never writes 0x9000–0xDFFF**. **Finding:** the `--flash-mode/--flash-size` arguments make esptool **patch the bootloader header**, so after a PlatformIO upload the bootloader region won't MD5-match `bootloader.bin`. The app region isn't patched. | `pio run -t upload -v` |
| `initArduino` erases NVS on NO_FREE_PAGES / NEW_VERSION_FOUND | **Yes.** `nvs_flash_init()`; on either error it calls `esp_partition_erase_range(nvs, 0, size)`, then `nvs_flash_init()` again. | `cores/esp32/esp32-hal-misc.c` ~L328–340 (3.3.8) |
| nanopb generation from `../proto` works in `native` and `esp32` | **Yes, both.** `[nanopb] Processing '../proto/volco_config.proto' (volco_config.options)`: the `.options` file next to the proto is picked up automatically. esp32 spike: RAM 22 KB, flash 295 KB (it is the bare spike, not Volco). | `spikes/nanopb_pio`, `pio test -e native`, `pio run -e esp32` |
| Exact `custom_nanopb_*` keys that work | `lib_deps = nanopb/Nanopb@0.4.92` (**the registry name of 0.4.9.2**; `@0.4.9.2` is rejected as "Invalid simple block"), `custom_nanopb_protos = +<../proto/volco_config.proto>`, `custom_nanopb_options = --error-on-unmatched`, `build_flags = -DPB_VALIDATE_UTF8`. `custom_nanopb_project_dir` is not needed. | same |
| Generated names (prefix, oneof, has_ flags, string arrays) | Prefix `volco_config_`. Types `volco_config_ControllerConfig` etc.; `_init_default`, `_fields`. Repeated: `sliders[16]` + `sliders_count`. oneof: `which_indicator` + `union { ... } indicator` (named union: `indicator.screen.bus`), tags `volco_config_Button_screen_tag`. Every optional field has `has_<name>`. Strings: `char ssid[33]`, `char password[65]`, `char preset[25]`. | `.pio/build/native/nanopb/generated-src/volco_config.pb.h` |
| Empty blob: are absent submessages filled with defaults? | **Yes.** After decoding an empty blob, `has_screens=0` but `screens.brightness=255` and `screens.dim_enabled=1`: `_init_default` plus `pb_decode` fill absent submessages with the proto2 defaults. `toRuntime` can read the values directly. | test_empty |
| Maximal blob encoded size (≤ 1024?) | **663 bytes** (≤ 1024), matching the spec's estimate. | test_maximal_blob_size |

## S3: Grpc.Tools / Google.Protobuf

| Question | Answer | Evidence |
|---|---|---|
| Generation works in the Linux job | **Yes** (`dotnet run`, net8.0, global.json SDK 10) | spike run 37151101930, job proto-linux |
| Generation works in the Windows single-file publish | **Yes** (`-r win-x64 --self-contained -p:PublishSingleFile=true`, then the exe ran) | same run, job windows |
| Required csproj items (`PrivateAssets`, `ProtoRoot`, …) | `Google.Protobuf 3.36.2`; `Grpc.Tools 2.84.0` with `PrivateAssets="all"`; `<Protobuf Include="../proto/volco_config.proto" ProtoRoot="../proto" GrpcServices="None" />`. Nothing else is needed. Generated C# names: `I2C0`/`I2C1`, `LedPair`, `IndicatorCase` (enum `IndicatorOneofCase.Screen`), proto2 `HasX` on scalars. | spikes/ProtoSpike |
| Maximal blob size (must equal nanopb's) | **663 bytes, the same as nanopb** | VECTOR maximal_size |
| Encoding determinism (same message twice → same bytes) | **Yes** | VECTOR determinism |
| Unknown field position after Decode+Encode | **Moved to the end**: `98 06 01 32 02 08 64` re-encodes as `32 02 08 64 98 06 01`. This confirms the spec §8.4 baseline rule (compare against `Encode(Decode(mirror))`, never against the raw mirror bytes). | VECTOR unknown_field |

## Vectors: nanopb vs C#

| Vector | Hex | nanopb | C# | Spec expectation |
|---|---|---|---|---|
| empty | (none) | ok; absent submessages default-filled | ok; `Screens` is **null** (a new message has the defaults: brightness 255) | decode ok, all defaults |
| screen_default | `12 02 22 00` | ok; which=screen, bus 0, address 0x3C | ok; case Screen, bus 0, address 0x3C (`HasAddress` false) | ok; screen bus 0, address 0x3C |
| brightness_only | `32 02 08 64` | ok; 100 / true / 360 | ok; 100 / true / 360 | ok; brightness 100, dim_enabled true, transition 360 |
| buttons33 | `12 00` ×33 | **fail** "array overflow" | **ok, 33 buttons** (no limit) | fail (max_count 32) |
| ssid33 | `2A 23 12 21` + `61`×33 | **fail** "string overflow" | **ok, 33 chars** (no limit) | fail (max_size) |
| bad_utf8 | `42 02 C3 28` | **fail** "invalid utf8" (with `PB_VALIDATE_UTF8`) | **fail**, `InvalidProtocolBufferException: String is invalid UTF-8` | fail |
| nul_in_string | `42 03 61 00 62` | **ok, silently truncated** (`strlen(preset)=1`) | **ok, kept** (length 3) | fail (spec: our own check) |
| u32_overflow | `0A 06 08 80 80 80 80 10` | **fail** "integer too large" | **ok, truncated to 0** | nanopb fail; C# truncates (spec §6.1 known divergence) |
| unknown_field | `98 06 01 32 02 08 64` | ok; skipped, brightness 100 | ok; brightness 100; unknown field kept and moved to the end on re-encode | ok, brightness 100 |

## S1: flashing

| Question | Answer | Evidence |
|---|---|---|
| Chip, flash size, MAC (esptool `flash_id`) | **ESP32-D0WD-V3 (rev v3.1)**, 40 MHz crystal, flash manufacturer 0x85 device 0x2016, **4 MB**, MAC 5c:01:3b:6a:19:94 | `spikes/flash_facts.py` on /dev/ttyUSB1 (CH340 1a86:7523), 2026-10-04 |
| Writing the 4 parts leaves NVS (0x9000–0xDFFF) unchanged (esptool) | **Yes.** NVS was filled with an 0xA5 marker (MD5 0f6b7d73…), then the four parts were written in one `write_flash`; NVS MD5 afterwards was identical. esptool erased only each part's own range. | same, step 3 |
| Firmware boots over a garbage NVS and recovers (NVS rewritten, frames sent) | **Yes.** After a hard reset over the 0xA5 NVS, the firmware ran (`[DBG] Setup complete`; 7314 bytes and 1029 × 0xAA frame markers in 4 s), and NVS was rewritten (new MD5 b4823d44…, starting with erased pages). | same, steps 5–6 |
| Boot ROM log readable at 115200 | **Not observed.** The port was opened after the reset, so the ROM banner was missed. The firmware's own log and frames at 115200 were read correctly, and the app's parser resyncs on 0xAA regardless. **Side finding:** today's serial transport logs `[E][STA.cpp:530] disconnect(): STA disconnect failed! ESP_ERR_WIFI_NOT_INIT` at boot (`WiFi.disconnect()` before Wi-Fi init). It's harmless and goes away with the stage 3 transport rewrite (spec §7: `WiFi.mode(WIFI_OFF)` only). | same, step 5 |
| esptool `verify_flash` (MD5) passes for all 4 parts | **Yes**, `Verification successful (digest matched)` ×4. Written without `--flash-mode/--flash-size`, the bootloader is not patched and matches `bootloader.bin` byte for byte. Note: esptool 5.2 accepts the old underscore spellings (`write_flash`, `no_reset`) with a deprecation warning; new code should use `write-flash`, `no-reset`. | same, step 4 |
| EspDotNet: bootloader entry and chip detect via DTR/RTS on this board | **Yes.** Bootloader and chip `ESP32` in about 1.2–1.3 s, stub running at about 1.7 s, using the library's default DTR/RTS sequences. | Windows, COM3, `EspDotNetSpike probe/flash/wipe-nvs` (2026-10-04) |
| EspDotNet: `SpiFlashMd5Async` works (stub) and matches local MD5 | **Yes.** 16-byte digests. The first probe showed mismatches for bootloader and app, as expected: the board held a different build (and a PlatformIO-patched bootloader); partitions and boot_app0 matched. MD5 of the 950 KB app takes about 1.3 s. | probe |
| EspDotNet: writing 4 parts leaves NVS MD5 unchanged | **Yes.** NVS MD5 cb5eabef… before and after. | flash |
| EspDotNet: bootloader region MD5 equals `bootloader.bin` MD5 (no header patching) | **Yes.** After `flash`, all four parts `MATCH`, including bootloader.bin. (The planned second probe wasn't run; flash's own verify covers it.) | flash |
| EspDotNet: 0xFF over NVS works | **Yes**, in about 0.3 s. The MD5 equals the all-0xFF MD5. | wipe-nvs |
| EspDotNet: flash ≥ 4 MB proxy (MD5 of 0x3FF000+0x1000 succeeds) | It succeeds on this 4 MB board, but **it proves nothing.** On a smaller chip a read past the end can wrap around instead of failing. EspDotNet has no flash-ID command. | probe |
| EspDotNet: timings (probe, install) | Probe about **3.6 s** end to end. Install (4 parts, about 1 MB, deflated) about **67 s**, because EspDotNet stays at **115200 baud**; esptool used 460800 in the PlatformIO upload. | probe, flash |
| Windows `ErrorReceived` events when the board resets with the port open | **None.** 449 bytes received; the ROM banner (`ets Jul 29 2019 … rst:0x1 (POWERON_RESET) …`) **is readable at 115200**; then the firmware log and 6 × 0xAA frames. (The reset was via EN/RTS, which gives the same ROM output as `ESP.restart()`.) | serial-errors |

## Decisions and spec amendments

(filled in Task 7)

# Controller config: stage 1 spike results

Spec: `docs/superpowers/specs/2026-10-03-controller-config-design.md` §12.6. Plan:
`docs/superpowers/plans/2026-10-03-controller-config-stage1-spikes.md`.

## S2: firmware toolchain

| Question | Answer | Evidence |
|---|---|---|
| Unpinned resolution on a fresh machine (= CI before this stage) | **platformio/espressif32 7.1.3 with framework-arduinoespressif32 4.20017.x, i.e. Arduino core 2.0.17.** This differs from the dev machine (3.3.8). | `pio pkg install -e denky32` with an empty `PLATFORMIO_CORE_DIR` |
| Dev machine platform/framework | pioarduino 55.03.38 / Arduino 3.3.8 | `~/.platformio/platforms/espressif32/platform.json` |
| Pinned platform builds `denky32`, the 3 check envs and `native` locally | **Yes.** 4× SUCCESS; native 45/45 passed (after installing host `g++` in the container) | local run |
| CI builds with the pinned platform and logs framework 3.3.8 | | |
| Exact U8g2 / WebSockets versions pinned | U8g2 **2.36.18**, WebSockets **2.7.3** (what `^` resolved to). Platform 55.3.38 brings Arduino 3.3.8, ESP-IDF 5.5.4 and esptool 5.2.0. | `pio pkg list -e denky32` |
| Flash parts and offsets (`FLASH_EXTRA_IMAGES`, app offset) | `0x1000` bootloader.bin, `0x8000` partitions.bin, `0xe000` boot_app0.bin, app `0x10000` firmware.bin | `spike_layout` env |
| Where `bootloader.bin` and `boot_app0.bin` come from | bootloader.bin and partitions.bin: **build dir** (`.pio/build/<env>/`). boot_app0.bin: **framework** (`framework-arduinoespressif32/tools/partitions/boot_app0.bin`). | `spike_layout` env |
| Partition table (nvs / otadata / app0 offsets and sizes) | default.csv: nvs 0x9000/0x5000, otadata 0xE000/0x2000, app0 0x10000/0x140000, app1 0x150000/0x140000, spiffs 0x290000/0x160000, coredump 0x3F0000/0x10000. Matches spec §6.6. | `tools/partitions/default.csv` |
| `pio run -t upload` command line: which offsets it writes | `esptool ... write-flash -z --flash-mode dio --flash-freq 40m --flash-size detect 0x1000 bootloader 0x8000 partitions 0xe000 boot_app0 0x10000 firmware`. It **never writes 0x9000–0xDFFF**. **Finding:** the `--flash-mode/--flash-size` arguments make esptool **patch the bootloader header**, so after a PlatformIO upload the bootloader region won't MD5-match `bootloader.bin`. The app region isn't patched. | `pio run -t upload -v` |
| `initArduino` erases NVS on NO_FREE_PAGES / NEW_VERSION_FOUND | **Yes.** `nvs_flash_init()`; on either error it calls `esp_partition_erase_range(nvs, 0, size)`, then `nvs_flash_init()` again. | `cores/esp32/esp32-hal-misc.c` ~L328–340 (3.3.8) |
| nanopb generation from `../proto` works in `native` and `esp32` | | |
| Exact `custom_nanopb_*` keys that work | | |
| Generated names (prefix, oneof, has_ flags, string arrays) | | |
| Empty blob: are absent submessages filled with defaults? | | |
| Maximal blob encoded size (≤ 1024?) | | |

## S3: Grpc.Tools / Google.Protobuf

| Question | Answer | Evidence |
|---|---|---|
| Generation works in the Linux job | | |
| Generation works in the Windows single-file publish | | |
| Required csproj items (`PrivateAssets`, `ProtoRoot`, …) | | |
| Maximal blob size (must equal nanopb's) | | |
| Encoding determinism (same message twice → same bytes) | | |
| Unknown field position after Decode+Encode | | |

## Vectors: nanopb vs C#

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

## S1: flashing

| Question | Answer | Evidence |
|---|---|---|
| Chip, flash size, MAC (esptool `flash_id`) | | |
| Writing the 4 parts leaves NVS (0x9000–0xDFFF) unchanged (esptool) | | |
| Firmware boots over a garbage NVS and recovers (NVS rewritten, frames sent) | | |
| Boot ROM log readable at 115200 | | |
| esptool `verify_flash` (MD5) passes for all 4 parts | | |
| EspDotNet: bootloader entry and chip detect via DTR/RTS on this board | | |
| EspDotNet: `SpiFlashMd5Async` works (stub) and matches local MD5 | | |
| EspDotNet: writing 4 parts leaves NVS MD5 unchanged | | |
| EspDotNet: bootloader region MD5 equals `bootloader.bin` MD5 (no header patching) | | |
| EspDotNet: 0xFF over NVS works | | |
| EspDotNet: flash ≥ 4 MB proxy (MD5 of 0x3FF000+0x1000 succeeds) | | |
| EspDotNet: timings (probe, install) | | |
| Windows `ErrorReceived` events when the board resets with the port open | | |

## Decisions and spec amendments

(filled in Task 7)

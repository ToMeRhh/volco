# Controller Config, Stage 3b: Sync with main and four I2C buses Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring `main`'s two firmware features into the runtime-config firmware, and extend the controller config to the four I2C buses that PR #66 introduced. The user approved uniform buses on 2026-10-04: buses 2 and 3 work like 0 and 1 (own SDA/SCL, up to two screens at 0x3C/0x3D, own clock), but are bit-banged.

The two features from `main`:
- **PR #68:** after every Hello, send each slider's position.
- **PR #66:** software I2C buses 2 and 3 for fixed-address screens.

**Architecture:**
- **Merge, don't rebase:** merge `origin/main` into `controller-config-stage2` rather than rebasing, so PR #69 needs no force-push. Conflicts resolve to the runtime-config versions, with #66/#68 re-applied on top.
- **Schema:** gains `i2c2` (field 9) and `i2c3` (field 10). Screen `bus` becomes 0..3; validation, `RuntimeConfig`, the C# validator, the presets' `ApplyTo` and the golden vectors follow.
- **Screens:** `screens.cpp` gets PR #66's `SoftI2c`, configured with runtime pins.

**Tech Stack:** as in stage 3: C++17, Arduino-ESP32 3.3.8, nanopb 0.4.92, Unity; .NET 8 in CI (Google.Protobuf); Python generators in PlatformIO's penv.

**Spec:** `docs/superpowers/specs/2026-10-03-controller-config-design.md` (§6.1, §6.3, §6.4, §7). This plan amends them: bus 0..3, `i2c2`/`i2c3`, and `kMaxScreens` = 8.

## Global Constraints

- **Git:** `/usr/bin/git` only; no `dotnet` locally; Write tool for files; simple shell commands.
- **No force-push.**
- **Hardware safety:** the ESP32 is `1a86:7523` at `/dev/ttyUSB1`. Run `hello_check.py --guard-only` before every flash or erase, and never touch `10c4:ea60`.
- **New field numbers:** `i2c2 = 9`, `i2c3 = 10`. Never reuse numbers.
- **Limits:**
  - screen bus ∈ {0, 1, 2, 3}, address ∈ {0x3C, 0x3D}, and (bus, address) is unique, which also caps each bus at two screens
  - `kMaxScreens` = 8
  - every bus clock is 100000..1000000; the software-bus default stays 400000, as in PR #66
- **Validation order (§6.4 step 4)** walks buses 0, 1, 2, 3; uniqueness (step 5) walks used buses 0..3, SDA before SCL; I2C findings use `item` = bus.
- **Slider sync (PR #68):**
  - `LinkSink::sliderValue(i)` returns the current filtered value and makes it the deadband reference
  - `Link::sendHello` follows the Hello with one Slider frame per slider
  - the firmware's own Hello-on-Wi-Fi-connect (spec §5.5) does the same
  - boot seeds each slider from `SLIDER_SEED_READS` (8) averaged reads

## Review Focus

1. **A screen on bus 2 or 3 with its `I2cBus` missing:** INVALID, `(bus, 8)`. Vectors `i2c_missing_bus2` and `i2c_missing_bus3`.
2. **A pin shared between a software bus and an LED:** caught by the uniqueness walk at the bus, `(2, 9)`. Vector `dup_led_i2c2`.
3. **The old "bus 2 is invalid" vector:** it must become bus 4. A stale oracle would wrongly pass bus 2 or 3.
4. **Hello-on-Wi-Fi-connect sends the slider positions too,** so a PC that just connected by UDP knows them. Covered by code review in Task 3 (Arduino code; no native harness).
5. **Eight screens:** the arrays are sized to `kMaxScreens` = 8, and `screenOf` covers 32 buttons. Covered by `test_screen_buses_used_only` (extended) and the build.

---

### Task 1: Merge `origin/main` and port PR #68 (slider sync)

**Files:**
- Merge conflicts in `firmware/src/{config.h,hardware.h,main.cpp,screens.cpp}`, `firmware/README.md`, `firmware/platformio.ini`, `.github/workflows/build.yml`, and possibly `firmware/test/test_protocol/test_main.cpp`
- Merged cleanly from main: `firmware/lib/volco_protocol` (`sliderValue` on `LinkSink`, sliders after Hello) and the C# `SliderProcessor`/`VolcoRuntime` changes

- [ ] **Step 1:** `/usr/bin/git merge --no-ff origin/main`.

  Resolve each conflicted file as follows:
  - `config.h`: ours, plus `#define SLIDER_SEED_READS 8  // reads averaged for each slider's starting value at boot`
  - `hardware.h`: stays deleted
  - `main.cpp`: ours, plus #68's `sliderValue` override and seed averaging (Step 2)
  - `screens.cpp`: ours (Task 3 ports #66)
  - `platformio.ini`, `build.yml`: ours
  - `README.md`: ours, keeping #68's behaviour-rule sentence (Hello is followed by every slider's position) and dropping #66's `config.h`-era text
  - `test_main.cpp`: union of both sides

- [ ] **Step 2:** In `HardwareSink` (`main.cpp`) add:
  ```cpp
  // The deadband then measures from the value just sent (PR #68).
  uint16_t sliderValue(uint8_t i) override {
    lastSliderValues[i] = (uint16_t)smoothedValues[i];
    return lastSliderValues[i];
  }
  ```
  In `setup()`, seed each slider with:
  ```cpp
  // Seed from an average, not one noisy read: Hello reports these positions right away.
  uint32_t sum = 0;
  for (uint8_t r = 0; r < SLIDER_SEED_READS; r++) sum += analogRead(rc.sliderPins[i]);
  lastSliderValues[i] = sum / SLIDER_SEED_READS;
  smoothedValues[i] = lastSliderValues[i];
  ```
  Make `sendHelloOnUdp()` also send `encodeSlider(i, sink.sliderValue(i), f)` for every slider after the Hello.

- [ ] **Step 3:** Run `pio test -e native` (all pass, including main's `assertHelloWithSliders` tests and our active-transport tests) and `pio run -e esp32` (SUCCESS).

- [ ] **Step 4:** Check that every conflict marker is gone: `grep -rn "<<<<<<<\|>>>>>>>" firmware src tests docs .github` prints nothing. Then commit the merge with message `merge: origin/main (slider sync after Hello, software I2C buses) into the runtime-config firmware`. **Push only after Task 4** (the C# side may need the merge to compile; CI runs then).

### Task 2: Schema, firmware validation, `toRuntime` and vectors for four buses (TDD)

**Files:** `proto/volco_config.proto`, `firmware/tools/gen_config_vectors.py`, `firmware/test/golden/config.txt`, `firmware/lib/volco_config/src/{volco_config.cpp,volco_runtime.h,volco_runtime.cpp}`, `firmware/test/test_runtime/test_main.cpp`

- [ ] **Step 1 (RED):**
  - **Proto:** add `optional I2cBus i2c2 = 9;  // required when a screen uses bus 2 (software I2C)` and `optional I2cBus i2c3 = 10; // required when a screen uses bus 3 (software I2C)`, and change the Screen comment to `// 0 and 1: hardware I2C; 2 and 3: software I2C`.
  - **Generator:**
    - `screen_bad_bus` uses bus **4**
    - add `valid_screen_bus2` (button 0 on bus 2 at 0x3C, `i2c2` sda 16 / scl 17) → OK
    - add `valid_screens_all_buses` (buttons 0..2 on buses 1, 2 and 3, each bus with its own pins: i2c1 27/13, i2c2 16/17, i2c3 22/23) → OK
    - add `i2c_missing_bus2` (screen on bus 2, no `i2c2`) → `(5, 2, 8)`
    - add `i2c_missing_bus3` → `(5, 3, 8)`
    - add `dup_led_i2c2` (screen 0 on bus 2 with i2c2 16/17, button 1's LED on pin 16) → `(5, 2, 9)`
    - add `order_bus2_before_bus3` (screens on buses 2 and 3, both buses missing) → `(5, 2, 8)`

    Regenerate `config.txt`.
  - **Runtime test:** in `test_screen_buses_used_only`, add a third button with a screen on bus 3 and `i2c3` 22/23, and assert `r.i2c[3].used`, its pins, and `!r.i2c[2].used`.

  Run `pio test -e native`. Expected: `test_config` fails on the new or changed vectors (`valid_screen_bus2` reports `(5, 0, 6)`), and `test_runtime` fails to compile on `i2c[3]`.

- [ ] **Step 2 (GREEN):**
  - **Firmware `validateConfig`:** bus `> 3` → FIELD_SCREEN_BUS. Steps 4 and 5 loop `bus < 4`, using a helper that returns `{has, bus}` for bus n: `c.has_i2c0/i2c0` … `c.has_i2c3/i2c3`. `used[4]`.
  - **`volco_runtime.h`:** `RuntimeBus i2c[4]`, and `kMaxScreens = 8` (4 buses × 2 addresses).
  - **`toRuntime`:** fills buses 0..3 from `i2c0..i2c3`.

  Run `pio test -e native`; all pass.

- [ ] **Step 3:** Commit `feat(config): four I2C buses (2 and 3 software) in the config schema and firmware validation`.

### Task 3: Screens drive software buses 2 and 3 from the config

**Files:** `firmware/src/screens.cpp`

- [ ] **Step 1:**
  - **Port PR #66's `SoftI2c`** class unchanged (`#include <driver/gpio.h>`). Replace `softBus[2] = {{I2C2_SDA, …}}` with `SoftI2c softBus[2];`, given a default constructor and `void configure(uint8_t sda, uint8_t scl)`, and set the pins in `screens_init` from `rc.i2c[2]` and `rc.i2c[3]`.
  - **Port PR #66's `i2cByte<Bus>`** with its `if constexpr (Bus < 2)` split, and `kByteCb[4]`.
  - **Bus start:** for bus 0/1 use `(bus == 0 ? Wire : Wire1).begin(sda, scl, clockHz)`; for bus 2/3 use `softBus[bus - 2].configure(sda, scl); softBus[bus - 2].begin(clockHz)`.
  - `started[4]`.
  - The `screens_init` loop runs to `kMaxScreens`, which is now 8.

  Build `pio run -e esp32`: SUCCESS, no warnings.

- [ ] **Step 2:** Commit `feat(firmware): screens on software I2C buses 2 and 3, pins and clock from the config`.

### Task 4: C# validator and presets for four buses; CI

**Files:** `src/Volco.Core/Controller/{ControllerConfigValidator.cs,ControllerPresets.cs}`, `tests/Volco.Core.Tests/Controller/ControllerPresetsTests.cs`

- [ ] **Step 1 (RED, via CI):**
  - In `ControllerPresetsTests.ApplyTo_ReplacesHardwareAndKeepsEverythingElse`, give the draft `I2C2 = new I2cBus { Sda = 16, Scl = 17 }` and assert that `result.I2C2` is null after applying DIY (hardware replaced).
  - Push with the merge.
  - Expected:
    - that test fails, because `ApplyTo` doesn't touch `I2C2`
    - the golden `Validate` cases for the new vectors fail (`valid_screen_bus2`, `valid_screens_all_buses`, `i2c_missing_bus2`, `i2c_missing_bus3`, `dup_led_i2c2`, `order_bus2_before_bus3`, `screen_bad_bus`)
    - everything else passes, including main's `SliderProcessor` tests
- [ ] **Step 2 (GREEN):**
  - **Validator:** `screen.Bus > 3` → ScreenBus. A `Bus(c, n)` helper returns `c.I2C0..c.I2C3`. `used = new bool[4]`; steps 4 and 5 loop `bus < 4`.
  - **`ApplyTo`:** also copies `I2C2` and `I2C3`.

  Push; CI is green.

### Task 5: Hardware verification and docs

- [ ] **Step 1:** Guard, then flash. NVS keeps the DIY config: field numbers didn't change, so the stored blob still decodes.
  - `hello_check.py --sliders 5 --kinds 1,1,2 --seconds 4`: expect `HELLO OK`, and **five Slider frames right after the Hello** (PR #68).
  - Print the frame types to confirm `03` ×5 follows `01`.
- [ ] **Step 2:** Spec amendments:
  - §6.1 proto: `i2c2`/`i2c3`, and the Screen bus comment
  - §6.3: `kMaxScreens` = 8
  - §6.4: bus ∈ {0..3}; steps 4 and 5 walk buses 0..3
  - §7 screens: software buses 2/3 are bit-banged

  README Configuration: four buses (2/3 software). CLAUDE.md: one clause. Commit, push, CI green.
- [ ] **Step 3: [USER-RUN] item 12.** If the user has screens wired, generate a seed for that layout (`gen_seed_config.py`), erase NVS, flash, and ask the user to confirm the screens render. Restore the DIY seed afterwards.

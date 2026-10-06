# Controller Config, Stage 3: Firmware on Runtime Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The ESP32 firmware reads its hardware layout and behaviour from the controller config stored in NVS (spec §6.3, §6.6, §7), runs USB serial and optional UDP side by side with the active-transport rule (§5.5), and drops the WebSocket client and `TRANSPORT_LOCAL_DEBUG`.

**Stage 3 bridge:** until stage 4 lets Volco send a config, an empty NVS is seeded from a compiled-in blob, generated from `proto/presets/diy-reference.json`. The wire protocol is unchanged, so today's Volco app keeps working (spec §15 stage 3).

**Architecture:**
- **New pure code, unit-tested natively:**
  - `toRuntime()`, which turns a validated nanopb config into a flat `RuntimeConfig` (in `lib/volco_config`)
  - `ActiveTransport` and `acceptUdpSource()` (in `lib/volco_protocol`)
- **Arduino code, build-checked and hardware-checked:**
  - `config_store`: the NVS load, the §6.6 boot table and the seed
  - `transport`, rewritten for serial plus UDP
  - `indicators`, `screens` and `main`, reading `RuntimeConfig` instead of `config.h`
- **Hardware is verified from this container** against the real ESP32 on `/dev/ttyUSB1`, using a new `firmware/tools/hello_check.py`. The boot Hello must equal the frame the config implies, and must change when the seeded layout changes.

**Tech Stack:** C++17, Arduino-ESP32 3.3.8 (pioarduino 55.03.38), nanopb 0.4.92, U8g2 2.36.18, Unity, PlatformIO 6.1.19, Python 3 (pyserial and protobuf from PlatformIO's penv).

**Spec:** `docs/superpowers/specs/2026-10-03-controller-config-design.md` (§5.5, §6.3, §6.6, §7, §12.2, §12.7 items 3, 4 and 12, §15 stage 3). The stage 2 code is in `firmware/lib/volco_config`.

## Global Constraints

- **Use `/usr/bin/git`;** plain `git` is refused. Write files with the Write tool, and keep shell commands simple. **Never run `dotnet` locally.**
- **Branch:** `controller-config-stage2` (the user asked for later stages to continue on it; PR #69 grows with them).
- **Firmware tests:** `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native`. **Build:** `~/.platformio/penv/bin/pio run -e esp32`.
- **HARDWARE SAFETY:** the ESP32 is the CH340 `1a86:7523` at `/dev/ttyUSB1`. The host also has a Sonoff Zigbee dongle (`10c4:ea60`), which must never be written to.
  - Every flash or erase command in this plan is preceded by `~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --guard-only`, which exits non-zero for any other device.
  - If `/dev/ttyUSB1` is missing, stop and ask the user to pass the board through (`incus config device add claude esp32 unix-char source=/dev/ttyUSB1 path=/dev/ttyUSB1 mode=0666` on the host).
- **Fixed constants (spec §6.3)**, in `config.h`:

  | Constant | Value |
  |---|---|
  | `SERIAL_BAUD` | 115200 |
  | `SERIAL_RX_BUFFER` | 2048 |
  | `SERIAL_TX_BUFFER` | 2048 (set before `Serial.begin`) |
  | `UDP_BUFFER` | 1536 |
  | `HOST_TIMEOUT_MS` | 3000 |
  | `KEEPALIVE_MS` | 1000 |
  | `COMMIT_FALLBACK_MS` | 500 |
  | `WIFI_RETRY_MS` | 10000 |

  `kMaxConfigBlob` (1024), `kMaxSliders` (16), `kMaxButtons` (32) and `kMaxScreens` (4) live in the libraries.
- **Wi-Fi rules (spec §7):**
  - `WiFi.persistent(false)`, `setAutoReconnect(true)`, `mode(WIFI_STA)`, then `begin` **once**. Never block.
  - Retry `begin` only when the status is `WL_CONNECT_FAILED`, `WL_NO_SSID_AVAIL` or `WL_DISCONNECTED`, and at most every `WIFI_RETRY_MS`.
  - `WiFi.mode(WIFI_OFF)` when UDP is disabled. **Never** call `WiFi.disconnect()` before Wi-Fi is initialised; stage 1 found that logs `ESP_ERR_WIFI_NOT_INIT`.
- **Active transport (spec §5.5):**
  - **Unlatched:** send on every transport that is up.
  - **Latching:** the first valid frame latches its transport *before* `Link::onPayload`; a frame on the other transport moves the latch.
  - **`hostLost`** clears the latch.
  - **Exception:** a Hello is sent on UDP whenever Wi-Fi connects.
  - **UDP** is accepted only from the configured `pc_ip`.
- **Boot order (spec §7):** Serial → load config → pins and indicators → transports → screens → `Link`.
- **Boot table (spec §6.6):**

  | Stored config | Result |
  |---|---|
  | absent (after the stage 3 seed attempt) | defaults, state NONE |
  | length > 1024 | defaults, state DAMAGED, CRC 0 |
  | fails decode or validate | defaults, state DAMAGED, CRC of the stored bytes |
  | otherwise | state STORED |

  Defaults are `toRuntime(volco_config_ControllerConfig_init_default)`: no sliders, no buttons, UDP off.

## Review Focus

1. **A config with 0 sliders or 0 buttons** (built-in defaults, or a seeded Blank) must boot, answer HelloRequest, and never index the arrays out of range. Task 1's `test_defaults_are_empty_and_sane`, plus Task 6's Blank-seed hardware check.
2. **Screens on bus 1 only, or on both buses** must start exactly the buses in use with that bus's pins and clock. Task 1's `test_screen_buses_used_only`.
3. **The first frame from the PC arriving on UDP while serial is unlatched** must make the Hello reply go out on UDP only. Task 2's `test_latch_set_before_reply_routes_to_new_transport`.
4. **A UDP datagram from a stranger's IP** must be dropped before parsing. Task 2's `test_accept_udp_source`.
5. **Re-seeding a different layout** (NVS erased, then re-flashed) must change what the firmware reports. Task 6's alternate-seed Hello check.

---

## File Structure

| Path | Change | Responsibility |
|---|---|---|
| `firmware/lib/volco_config/src/volco_runtime.{h,cpp}` | new | `RuntimeConfig` and `toRuntime` |
| `firmware/lib/volco_protocol/src/volco_protocol.h` | modify | `ActiveTransport`, `acceptUdpSource` (header-only) |
| `firmware/test/test_runtime/test_main.cpp` | new | `toRuntime` tests |
| `firmware/test/test_protocol/test_main.cpp` | modify | Active-transport and UDP-source tests |
| `firmware/tools/gen_seed_config.py` | new | Writes `src/seed_config.h` from a preset JSON (stage 3 only) |
| `firmware/src/seed_config.h` | new (generated) | The DIY seed blob |
| `firmware/src/config_store.{h,cpp}` | new | NVS load, §6.6 boot table, seed |
| `firmware/src/config.h` | rewrite | Fixed constants only |
| `firmware/src/hardware.h` | delete | Replaced by `volco::RuntimeButton` |
| `firmware/src/indicators.{h,cpp}` | modify | Take `RuntimeButton` |
| `firmware/src/screens.{h,cpp}` | modify | Runtime-sized, configured from `RuntimeConfig` |
| `firmware/src/transport.{h,cpp}` | rewrite | Serial always on, plus UDP |
| `firmware/src/main.cpp` | rewrite | Everything from `RuntimeConfig`; per-transport parsers; active transport |
| `firmware/src/debug_log.h` | modify | Comment only |
| `firmware/platformio.ini` | modify | `denky32` → `esp32` with nanopb; drop the WebSockets dependency and the `*_check` envs |
| `firmware/tools/hello_check.py` | new | USB-ID guard; reset the board, read its boot Hello, compare |
| `.github/workflows/build.yml`, `CLAUDE.md`, `firmware/README.md` | modify | Env names, firmware docs |

---

### Task 1: `RuntimeConfig` and `toRuntime` (native, TDD)

**Files:**
- Create: `firmware/lib/volco_config/src/volco_runtime.h`, `firmware/lib/volco_config/src/volco_runtime.cpp`
- Create: `firmware/test/test_runtime/test_main.cpp`

**Interfaces:**
- Consumes: `volco_config_ControllerConfig` (nanopb), `volco::Kind` and `volco::kMaxButtons` (`volco_protocol.h`).
- Produces, in `namespace volco`:
  ```cpp
  constexpr uint8_t kMaxSliders = 16;
  constexpr uint8_t kMaxScreens = 4;
  struct RuntimeButton { uint8_t pin, kind, pinA, pinB; bool activeLow; uint8_t bus, address; };  // kind = Kind
  struct RuntimeBus { bool used; uint8_t sda, scl; uint32_t clockHz; };
  struct RuntimeUdp { bool enabled; char ssid[33]; char password[65]; uint32_t pcIp; uint16_t pcPort, localPort; };
  struct RuntimeScreens { uint8_t brightness, dimBrightness; uint16_t scrollSpeedPxS;
    uint32_t transitionMs, scrollHoldMs, scrollEndHoldMs, dimAfterMs, offAfterMs, pixelShiftEveryMs;
    bool dimEnabled, offEnabled, pixelShiftEnabled, wakeSwallowsPress; };
  struct RuntimeInput { uint32_t debounceMs, sliderIntervalMs; float filterWeight; uint16_t sliderDeadband; };
  struct RuntimeConfig { uint8_t sliderCount; uint8_t sliderPins[kMaxSliders]; uint8_t buttonCount;
    RuntimeButton buttons[kMaxButtons]; RuntimeBus i2c[2]; RuntimeUdp udp; RuntimeScreens screens; RuntimeInput input; };
  void toRuntime(const volco_config_ControllerConfig& config, RuntimeConfig& out);  // config must have passed validateConfig
  ```

- [ ] **Step 1: Write the failing tests**

Create `firmware/test/test_runtime/test_main.cpp`:

```cpp
#include <string.h>
#include <unity.h>

#include "../golden_config.h"
#include "volco_config.h"
#include "volco_runtime.h"

using namespace volco;

void setUp() {}
void tearDown() {}

static volco_config_ControllerConfig decoded(const char* goldenName) {
  volco_config_ControllerConfig c;
  for (auto& g : configCases("decode-ok"))
    if (g.name == goldenName) {
      TEST_ASSERT_TRUE(decodeConfig(g.blob.data(), g.blob.size(), c));
      return c;
    }
  TEST_FAIL_MESSAGE(goldenName);
  return c;
}

void test_diy_reference() {
  volco_config_ControllerConfig c = decoded("diy_reference");
  TEST_ASSERT_EQUAL_UINT(CFG_OK, validateConfig(c, esp32Chip()).code);
  RuntimeConfig r;
  toRuntime(c, r);
  TEST_ASSERT_EQUAL_UINT(5, r.sliderCount);
  const uint8_t sliders[] = {33, 35, 32, 36, 34};
  TEST_ASSERT_EQUAL_UINT8_ARRAY(sliders, r.sliderPins, 5);
  TEST_ASSERT_EQUAL_UINT(3, r.buttonCount);
  TEST_ASSERT_EQUAL_UINT(4, r.buttons[0].pin);
  TEST_ASSERT_EQUAL_UINT(KIND_LED, r.buttons[0].kind);
  TEST_ASSERT_EQUAL_UINT(12, r.buttons[0].pinA);
  TEST_ASSERT_TRUE(r.buttons[0].activeLow);
  TEST_ASSERT_EQUAL_UINT(KIND_LED, r.buttons[1].kind);
  TEST_ASSERT_EQUAL_UINT(21, r.buttons[1].pinA);
  TEST_ASSERT_EQUAL_UINT(KIND_LED_PAIR, r.buttons[2].kind);
  TEST_ASSERT_EQUAL_UINT(18, r.buttons[2].pinA);
  TEST_ASSERT_EQUAL_UINT(19, r.buttons[2].pinB);
  TEST_ASSERT_FALSE(r.buttons[2].activeLow);
  TEST_ASSERT_FALSE(r.i2c[0].used);  // no screens: both buses unused even though configured
  TEST_ASSERT_FALSE(r.i2c[1].used);
  TEST_ASSERT_FALSE(r.udp.enabled);
}

void test_defaults_converted_to_ms() {
  volco_config_ControllerConfig c = decoded("empty");
  RuntimeConfig r;
  toRuntime(c, r);
  TEST_ASSERT_EQUAL_UINT(255, r.screens.brightness);
  TEST_ASSERT_EQUAL_UINT(8, r.screens.dimBrightness);
  TEST_ASSERT_EQUAL_UINT(360, r.screens.transitionMs);
  TEST_ASSERT_EQUAL_UINT(2000, r.screens.scrollHoldMs);
  TEST_ASSERT_EQUAL_UINT(1200, r.screens.scrollEndHoldMs);
  TEST_ASSERT_EQUAL_UINT(30, r.screens.scrollSpeedPxS);
  TEST_ASSERT_EQUAL_UINT(120000, r.screens.dimAfterMs);
  TEST_ASSERT_EQUAL_UINT(1800000, r.screens.offAfterMs);
  TEST_ASSERT_EQUAL_UINT(180000, r.screens.pixelShiftEveryMs);
  TEST_ASSERT_TRUE(r.screens.dimEnabled && r.screens.offEnabled && r.screens.pixelShiftEnabled && r.screens.wakeSwallowsPress);
  TEST_ASSERT_EQUAL_UINT(30, r.input.debounceMs);
  TEST_ASSERT_EQUAL_UINT(10, r.input.sliderIntervalMs);
  TEST_ASSERT_FLOAT_WITHIN(0.0001f, 0.2f, r.input.filterWeight);
  TEST_ASSERT_EQUAL_UINT(25, r.input.sliderDeadband);
}

void test_defaults_are_empty_and_sane() {
  volco_config_ControllerConfig c = volco_config_ControllerConfig_init_default;
  RuntimeConfig r;
  memset(&r, 0xA5, sizeof r);  // garbage in, so every field must be set
  toRuntime(c, r);
  TEST_ASSERT_EQUAL_UINT(0, r.sliderCount);
  TEST_ASSERT_EQUAL_UINT(0, r.buttonCount);
  TEST_ASSERT_FALSE(r.i2c[0].used);
  TEST_ASSERT_FALSE(r.i2c[1].used);
  TEST_ASSERT_FALSE(r.udp.enabled);
  TEST_ASSERT_EQUAL_STRING("", r.udp.ssid);
}

void test_screen_buses_used_only() {
  volco_config_ControllerConfig c = volco_config_ControllerConfig_init_default;
  c.buttons_count = 2;
  c.buttons[0].has_pin = true;
  c.buttons[0].pin = 4;
  c.buttons[0].which_indicator = volco_config_Button_screen_tag;
  c.buttons[0].indicator.screen = volco_config_Screen_init_default;
  c.buttons[0].indicator.screen.bus = 1;
  c.buttons[0].indicator.screen.address = 0x3D;
  c.buttons[1].has_pin = true;
  c.buttons[1].pin = 5;
  c.has_i2c0 = true;
  c.i2c0.has_sda = true; c.i2c0.sda = 25; c.i2c0.has_scl = true; c.i2c0.scl = 26;
  c.has_i2c1 = true;
  c.i2c1.has_sda = true; c.i2c1.sda = 27; c.i2c1.has_scl = true; c.i2c1.scl = 13;
  c.i2c1.has_clock_hz = true; c.i2c1.clock_hz = 1000000;
  RuntimeConfig r;
  toRuntime(c, r);
  TEST_ASSERT_EQUAL_UINT(KIND_SCREEN, r.buttons[0].kind);
  TEST_ASSERT_EQUAL_UINT(1, r.buttons[0].bus);
  TEST_ASSERT_EQUAL_UINT(0x3D, r.buttons[0].address);
  TEST_ASSERT_EQUAL_UINT(KIND_NONE, r.buttons[1].kind);
  TEST_ASSERT_FALSE(r.i2c[0].used);
  TEST_ASSERT_TRUE(r.i2c[1].used);
  TEST_ASSERT_EQUAL_UINT(27, r.i2c[1].sda);
  TEST_ASSERT_EQUAL_UINT(13, r.i2c[1].scl);
  TEST_ASSERT_EQUAL_UINT(1000000, r.i2c[1].clockHz);
}

void test_udp_copied() {
  volco_config_ControllerConfig c = volco_config_ControllerConfig_init_default;
  c.has_udp = true;
  c.udp.has_enabled = true; c.udp.enabled = true;
  c.udp.has_ssid = true; strcpy(c.udp.ssid, "home-net");
  c.udp.has_password = true; strcpy(c.udp.password, "secret123");
  c.udp.has_pc_ip = true; c.udp.pc_ip = (192u << 24) | (168u << 16) | (1u << 8) | 10u;
  RuntimeConfig r;
  toRuntime(c, r);
  TEST_ASSERT_TRUE(r.udp.enabled);
  TEST_ASSERT_EQUAL_STRING("home-net", r.udp.ssid);
  TEST_ASSERT_EQUAL_STRING("secret123", r.udp.password);
  TEST_ASSERT_EQUAL_HEX32(0xC0A8010A, r.udp.pcIp);
  TEST_ASSERT_EQUAL_UINT(12345, r.udp.pcPort);
  TEST_ASSERT_EQUAL_UINT(12346, r.udp.localPort);
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_diy_reference);
  RUN_TEST(test_defaults_converted_to_ms);
  RUN_TEST(test_defaults_are_empty_and_sane);
  RUN_TEST(test_screen_buses_used_only);
  RUN_TEST(test_udp_copied);
  return UNITY_END();
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native -f test_runtime`
Expected: a compile error, `volco_runtime.h: No such file or directory`.

- [ ] **Step 3: Implement**

Create `firmware/lib/volco_config/src/volco_runtime.h`:

```cpp
#pragma once
// The controller config as the firmware uses it (spec §6.3): plain values, defaults applied, times
// in ms, and only the I2C buses a screen actually uses. src/ reads nothing else.
#include <stdint.h>

#include "volco_config.h"
#include "volco_protocol.h"  // Kind, kMaxButtons

namespace volco {

constexpr uint8_t kMaxSliders = 16;
constexpr uint8_t kMaxScreens = 4;  // 2 buses x 2 addresses

struct RuntimeButton {
  uint8_t pin;
  uint8_t kind;          // Kind: KIND_NONE, KIND_LED, KIND_LED_PAIR or KIND_SCREEN
  uint8_t pinA, pinB;    // the LED (pinA), or LED pair A and B
  bool activeLow;
  uint8_t bus, address;  // screen: I2C bus 0/1, 7-bit address
};

struct RuntimeBus {
  bool used;  // a screen is on this bus; an unused bus is never started
  uint8_t sda, scl;
  uint32_t clockHz;
};

struct RuntimeUdp {
  bool enabled;
  char ssid[33];
  char password[65];
  uint32_t pcIp;  // a.b.c.d = a<<24 | b<<16 | c<<8 | d
  uint16_t pcPort, localPort;
};

struct RuntimeScreens {
  uint8_t brightness, dimBrightness;
  uint16_t scrollSpeedPxS;
  uint32_t transitionMs, scrollHoldMs, scrollEndHoldMs, dimAfterMs, offAfterMs, pixelShiftEveryMs;
  bool dimEnabled, offEnabled, pixelShiftEnabled, wakeSwallowsPress;
};

struct RuntimeInput {
  uint32_t debounceMs, sliderIntervalMs;
  float filterWeight;  // EMA weight 0.01..1
  uint16_t sliderDeadband;
};

struct RuntimeConfig {
  uint8_t sliderCount;
  uint8_t sliderPins[kMaxSliders];
  uint8_t buttonCount;
  RuntimeButton buttons[kMaxButtons];
  RuntimeBus i2c[2];
  RuntimeUdp udp;
  RuntimeScreens screens;
  RuntimeInput input;
};

// `config` must already have passed validateConfig (pins exist, buses present for screens, ...).
void toRuntime(const volco_config_ControllerConfig& config, RuntimeConfig& out);

}  // namespace volco
```

Create `firmware/lib/volco_config/src/volco_runtime.cpp`:

```cpp
#include "volco_runtime.h"

#include <string.h>

namespace volco {

void toRuntime(const volco_config_ControllerConfig& c, RuntimeConfig& r) {
  memset(&r, 0, sizeof r);

  r.sliderCount = (uint8_t)c.sliders_count;
  for (pb_size_t i = 0; i < c.sliders_count; i++) r.sliderPins[i] = (uint8_t)c.sliders[i].pin;

  r.buttonCount = (uint8_t)c.buttons_count;
  for (pb_size_t i = 0; i < c.buttons_count; i++) {
    const volco_config_Button& b = c.buttons[i];
    RuntimeButton& o = r.buttons[i];
    o.pin = (uint8_t)b.pin;
    o.kind = KIND_NONE;
    if (b.which_indicator == volco_config_Button_led_tag) {
      o.kind = KIND_LED;
      o.pinA = (uint8_t)b.indicator.led.pin;
      o.activeLow = b.indicator.led.active_low;
    } else if (b.which_indicator == volco_config_Button_led_pair_tag) {
      o.kind = KIND_LED_PAIR;
      o.pinA = (uint8_t)b.indicator.led_pair.pin_a;
      o.pinB = (uint8_t)b.indicator.led_pair.pin_b;
      o.activeLow = b.indicator.led_pair.active_low;
    } else if (b.which_indicator == volco_config_Button_screen_tag) {
      o.kind = KIND_SCREEN;
      o.bus = (uint8_t)b.indicator.screen.bus;
      o.address = (uint8_t)b.indicator.screen.address;
      r.i2c[o.bus].used = true;
    }
  }

  for (uint8_t bus = 0; bus < 2; bus++) {
    if (!r.i2c[bus].used) continue;
    const volco_config_I2cBus& src = bus == 0 ? c.i2c0 : c.i2c1;
    r.i2c[bus].sda = (uint8_t)src.sda;
    r.i2c[bus].scl = (uint8_t)src.scl;
    r.i2c[bus].clockHz = src.clock_hz;
  }

  r.udp.enabled = c.has_udp && c.udp.enabled;
  strncpy(r.udp.ssid, c.udp.ssid, sizeof r.udp.ssid - 1);
  strncpy(r.udp.password, c.udp.password, sizeof r.udp.password - 1);
  r.udp.pcIp = c.udp.pc_ip;
  r.udp.pcPort = (uint16_t)c.udp.pc_port;
  r.udp.localPort = (uint16_t)c.udp.local_port;

  const volco_config_ScreenBehaviour& s = c.screens;  // nanopb fills absent submessages with defaults
  r.screens.brightness = (uint8_t)s.brightness;
  r.screens.dimBrightness = (uint8_t)s.dim_brightness;
  r.screens.scrollSpeedPxS = (uint16_t)s.scroll_speed_px_s;
  r.screens.transitionMs = s.transition_ms;
  r.screens.scrollHoldMs = s.scroll_hold_ms;
  r.screens.scrollEndHoldMs = s.scroll_end_hold_ms;
  r.screens.dimAfterMs = s.dim_after_s * 1000u;
  r.screens.offAfterMs = s.off_after_s * 1000u;
  r.screens.pixelShiftEveryMs = s.pixel_shift_every_s * 1000u;
  r.screens.dimEnabled = s.dim_enabled;
  r.screens.offEnabled = s.off_enabled;
  r.screens.pixelShiftEnabled = s.pixel_shift_enabled;
  r.screens.wakeSwallowsPress = s.wake_swallows_press;

  const volco_config_InputTuning& in = c.input;
  r.input.debounceMs = in.debounce_ms;
  r.input.sliderIntervalMs = in.slider_interval_ms;
  r.input.filterWeight = (float)in.filter_weight_pct / 100.0f;
  r.input.sliderDeadband = (uint16_t)in.slider_deadband;
}

}  // namespace volco
```

- [ ] **Step 4: Run the tests, then the whole suite**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native`
Expected: `test_runtime` 5/5 PASSED; every other suite PASSED.

- [ ] **Step 5: Commit**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/lib/volco_config firmware/test/test_runtime
/usr/bin/git commit -m "feat(firmware): RuntimeConfig and toRuntime (spec 6.3)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `ActiveTransport` and `acceptUdpSource` (native, TDD)

**Files:**
- Modify: `firmware/lib/volco_protocol/src/volco_protocol.h` (append before the closing `}  // namespace volco`)
- Modify: `firmware/test/test_protocol/test_main.cpp`

**Interfaces:**
- Produces, in `namespace volco`:
  ```cpp
  class ActiveTransport {
   public:
    void onValidFrame(uint8_t id);              // call BEFORE Link::onPayload
    void onHostLost();
    uint8_t targets(uint8_t upMask) const;      // bit mask of transports to send on
    bool latched() const; uint8_t active() const;
  };
  bool acceptUdpSource(uint32_t srcIp, uint32_t configuredIp);
  ```
  Transport ids are bit positions: serial = 0, UDP = 1.

- [ ] **Step 1: Write the failing tests**

In `firmware/test/test_protocol/test_main.cpp`, add before `int main`:

```cpp
// ── Active transport (spec §5.5) ─────────────────────────────────────────────
void test_unlatched_sends_on_every_transport_that_is_up() {
  ActiveTransport a;
  TEST_ASSERT_FALSE(a.latched());
  TEST_ASSERT_EQUAL_UINT8(0b01, a.targets(0b01));  // only serial up
  TEST_ASSERT_EQUAL_UINT8(0b11, a.targets(0b11));  // both up
}

void test_first_frame_latches_its_transport() {
  ActiveTransport a;
  a.onValidFrame(1);
  TEST_ASSERT_TRUE(a.latched());
  TEST_ASSERT_EQUAL_UINT8(0b10, a.targets(0b11));
  TEST_ASSERT_EQUAL_UINT8(0b00, a.targets(0b01));  // latched transport down: nothing until host timeout
}

void test_frame_on_other_transport_moves_the_latch() {
  ActiveTransport a;
  a.onValidFrame(1);
  a.onValidFrame(0);
  TEST_ASSERT_EQUAL_UINT8(0b01, a.targets(0b11));
}

void test_host_lost_unlatches() {
  ActiveTransport a;
  a.onValidFrame(0);
  a.onHostLost();
  TEST_ASSERT_FALSE(a.latched());
  TEST_ASSERT_EQUAL_UINT8(0b11, a.targets(0b11));
}

void test_latch_set_before_reply_routes_to_new_transport() {
  // A HelloRequest arriving on UDP: latching first means the Hello it triggers goes to UDP only.
  ActiveTransport a;
  struct RoutingSink : FakeSink {
    ActiveTransport* active = nullptr;
    uint8_t lastTargets = 0;
    void send(const uint8_t* f, size_t n) override { lastTargets = active->targets(0b11); FakeSink::send(f, n); }
  } sink;
  sink.active = &a;
  Link link(config(), sink);
  link.begin(0);
  TEST_ASSERT_EQUAL_UINT8(0b11, sink.lastTargets);  // boot Hello: unlatched
  a.onValidFrame(1);
  deliver(link, golden("hello_request"), 10);
  TEST_ASSERT_EQUAL_UINT8(0b10, sink.lastTargets);
}

void test_accept_udp_source() {
  const uint32_t pc = (192u << 24) | (168u << 16) | (1u << 8) | 10u;
  TEST_ASSERT_TRUE(acceptUdpSource(pc, pc));
  TEST_ASSERT_FALSE(acceptUdpSource(pc + 1, pc));
  TEST_ASSERT_FALSE(acceptUdpSource(0, 0));  // no configured PC: accept nothing
}
```

and add to `main` before `return UNITY_END();`:

```cpp
  RUN_TEST(test_unlatched_sends_on_every_transport_that_is_up);
  RUN_TEST(test_first_frame_latches_its_transport);
  RUN_TEST(test_frame_on_other_transport_moves_the_latch);
  RUN_TEST(test_host_lost_unlatches);
  RUN_TEST(test_latch_set_before_reply_routes_to_new_transport);
  RUN_TEST(test_accept_udp_source);
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native -f test_protocol`
Expected: a compile error, `'ActiveTransport' was not declared in this scope`.

- [ ] **Step 3: Implement**

In `firmware/lib/volco_protocol/src/volco_protocol.h`, insert before the final `}  // namespace volco`:

```cpp
// Which transport the controller answers on (spec §5.5). Transport ids are bit positions in the
// masks: 0 = USB serial, 1 = UDP. Unlatched (boot, host timeout) every frame goes to every
// transport that is up; the first valid frame from the PC latches its transport.
class ActiveTransport {
 public:
  void onValidFrame(uint8_t id) {  // call BEFORE Link::onPayload, so the reply follows the latch
    latched_ = true;
    active_ = id;
  }
  void onHostLost() { latched_ = false; }
  uint8_t targets(uint8_t upMask) const { return latched_ ? (uint8_t)(upMask & (1u << active_)) : upMask; }
  bool latched() const { return latched_; }
  uint8_t active() const { return active_; }

 private:
  bool latched_ = false;
  uint8_t active_ = 0;
};

// UDP is accepted only from the configured PC (spec §5.5, D7): no PC configured, nothing accepted.
inline bool acceptUdpSource(uint32_t srcIp, uint32_t configuredIp) { return configuredIp != 0 && srcIp == configuredIp; }
```

- [ ] **Step 4: Run the tests, then the whole suite**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio test -e native`
Expected: `test_protocol` with 6 more tests, all PASSED; every suite PASSED.

If `FakeSink::send` isn't `virtual` in a way that allows the override (it overrides `LinkSink::send`, so it is virtual), the test compiles as written.

- [ ] **Step 5: Commit**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/lib/volco_protocol firmware/test/test_protocol
/usr/bin/git commit -m "feat(firmware): active transport rule and UDP source filter (spec 5.5)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The `esp32` environment, the seed and the hardware check tool

**Files:**
- Modify: `firmware/platformio.ini`
- Modify: `.github/workflows/build.yml` (the firmware build steps)
- Create: `firmware/tools/gen_seed_config.py`, `firmware/src/seed_config.h` (generated)
- Create: `firmware/tools/hello_check.py`

**Interfaces:**
- Produces:
  - env `esp32` (nanopb generation, `-DPB_VALIDATE_UTF8`, U8g2 and nanopb only)
  - `kSeedConfig[]` in `seed_config.h`
  - `hello_check.py --port P [--guard-only] [--sliders N --kinds k,k,...] [--no-reset]`, which exits 0 when the expected Hello is seen

- [ ] **Step 1: Rewrite the platformio envs**

In `firmware/platformio.ini`, replace everything from `[platformio]` through the end of the `[env:denky32]` block with:

```ini
[platformio]
default_envs = esp32

[env:esp32]
; Exact pins: CI and every dev machine must build the same core (spec §7).
platform = https://github.com/pioarduino/platform-espressif32/releases/download/55.03.38/platform-espressif32.zip
board = esp32dev
framework = arduino
monitor_speed = 115200
lib_deps =
    olikraus/U8g2@2.36.18
    nanopb/Nanopb@0.4.92
; The controller config, generated from the repo's proto/ (spec §7).
custom_nanopb_protos = +<../proto/volco_config.proto>
custom_nanopb_options = --error-on-unmatched
build_flags =
    -std=gnu++17
    -DPB_VALIDATE_UTF8
build_unflags =
    -std=gnu++11
test_ignore = *
```

Delete the `[env:denky32_screens_check]`, `[env:denky32_udp_check]` and `[env:denky32_websocket_check]` blocks, including their comment lines. The `[env:native]` block stays.

In `.github/workflows/build.yml`, replace the four `Build firmware …` steps (`denky32`, `denky32_screens_check`, `denky32_udp_check`, `denky32_websocket_check`) with one:

```yaml
    - name: Build firmware (ESP32)
      working-directory: firmware
      run: pio run -e esp32
```

- [ ] **Step 2: Write the seed generator and generate the DIY seed**

Create `firmware/tools/gen_seed_config.py`:

```python
#!/usr/bin/env python3
"""STAGE 3 ONLY (spec §15): writes firmware/src/seed_config.h, the config the firmware stores in
NVS when none is stored yet, until stage 4 lets Volco send one. Removed in stage 4.

    ~/.platformio/penv/bin/python firmware/tools/gen_seed_config.py [preset.json]

Default: proto/presets/diy-reference.json. To try another layout: write a JSON (protobuf JSON
mapping of ControllerConfig), run this with it, erase NVS (esptool erase-region 0x9000 0x5000),
then upload. Commit only the DIY seed.
"""
import importlib
import os
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
PROTO_DIR = os.path.join(ROOT, "proto")
OUT = os.path.join(ROOT, "firmware", "src", "seed_config.h")


def load_pb2():
    from grpc_tools import protoc

    tmp = tempfile.mkdtemp()
    if protoc.main(["protoc", f"-I{PROTO_DIR}", f"--python_out={tmp}", os.path.join(PROTO_DIR, "volco_config.proto")]):
        raise SystemExit("protoc failed")
    sys.path.insert(0, tmp)
    return importlib.import_module("volco_config_pb2")


def main():
    from google.protobuf import json_format

    src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(PROTO_DIR, "presets", "diy-reference.json")
    pb = load_pb2()
    config = json_format.Parse(open(src).read(), pb.ControllerConfig())
    blob = config.SerializeToString(deterministic=True)
    if not blob or len(blob) > 1024:
        raise SystemExit(f"seed is {len(blob)} bytes; must be 1..1024")
    rows = [", ".join(f"0x{b:02X}" for b in blob[i:i + 16]) for i in range(0, len(blob), 16)]
    rel = os.path.relpath(os.path.abspath(src), ROOT).replace(os.sep, "/")
    with open(OUT, "w", newline="\n") as fh:
        fh.write("#pragma once\n")
        fh.write(f"// GENERATED by firmware/tools/gen_seed_config.py from {rel}; do not edit.\n")
        fh.write("// Stage 3 only (spec §15): stored in NVS when no config is stored yet.\n")
        fh.write("#include <stdint.h>\n\n")
        fh.write("static const uint8_t kSeedConfig[] = {\n    " + ",\n    ".join(rows) + ",\n};\n")
    print(f"wrote {OUT}: {len(blob)} bytes from {rel}")


if __name__ == "__main__":
    main()
```

Run: `cd /root/dev/volco/.claude/worktrees/firmware && ~/.platformio/penv/bin/python firmware/tools/gen_seed_config.py`
Expected: `wrote …/seed_config.h: N bytes from proto/presets/diy-reference.json`, with N matching the `diy_reference` blob in `config.txt`. The header's bytes are the `diy_reference` hex from `firmware/test/golden/config.txt`; check by eye that the first bytes match.

- [ ] **Step 3: Write the hardware check tool**

Create `firmware/tools/hello_check.py`:

```python
#!/usr/bin/env python3
"""Resets the ESP32 over USB serial, reads its boot output and checks the Hello frame.

    ~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --sliders 5 --kinds 1,1,2
    ~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --guard-only

Safety: on Linux it refuses any port whose USB ID isn't the DIY board's CH340 (1a86:7523); the dev
host also has a Zigbee dongle that must never be touched. Exits 0 only when the expected Hello is seen.
"""
import argparse
import os
import sys
import time

ALLOWED = {"1a86:7523"}


def check_usb_id(port):
    if not port.startswith("/dev/"):
        return
    usb = os.path.realpath(f"/sys/class/tty/{os.path.basename(port)}/device")
    while usb != "/" and not os.path.exists(os.path.join(usb, "idVendor")):
        usb = os.path.dirname(usb)
    try:
        vid = open(os.path.join(usb, "idVendor")).read().strip()
        pid = open(os.path.join(usb, "idProduct")).read().strip()
    except OSError as e:
        sys.exit(f"cannot read the USB ID of {port}: {e}")
    if f"{vid}:{pid}" not in ALLOWED:
        sys.exit(f"{port} is {vid}:{pid}, not the ESP32 {sorted(ALLOWED)}; refusing")
    print(f"{port}: USB {vid}:{pid} OK")


def frames(data):
    """Yields (payload) for every valid 0xAA frame (len, payload, XOR checksum) in data."""
    i = 0
    while i < len(data):
        if data[i] != 0xAA or i + 2 >= len(data):
            i += 1
            continue
        n = data[i + 1]
        end = i + 2 + n
        if n == 0 or end >= len(data):
            i += 1
            continue
        payload = data[i + 2:end]
        cs = 0
        for b in payload:
            cs ^= b
        if cs == data[end]:
            yield payload
            i = end + 1
        else:
            i += 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", required=True)
    ap.add_argument("--guard-only", action="store_true")
    ap.add_argument("--sliders", type=int)
    ap.add_argument("--kinds", default="")
    ap.add_argument("--seconds", type=float, default=4.0)
    ap.add_argument("--no-reset", action="store_true")
    a = ap.parse_args()
    check_usb_id(a.port)
    if a.guard_only:
        return
    import serial

    with serial.Serial(a.port, 115200, timeout=0.2) as s:
        if not a.no_reset:
            s.dtr = False
            s.rts = True
            time.sleep(0.1)
            s.rts = False
        end, buf = time.time() + a.seconds, b""
        while time.time() < end:
            buf += s.read(4096)
    text = bytes(b if 32 <= b < 127 or b in (10, 13) else 46 for b in buf).decode("ascii")
    print("--- log lines ---")
    for line in text.splitlines():
        if "[DBG]" in line or "[E]" in line or "rst:" in line:
            print(line.strip()[:160])
    hellos = [p for p in frames(buf) if p[0] == 0x01]
    print(f"--- {len(hellos)} Hello frame(s) ---")
    for p in hellos:
        print(" ".join(f"{b:02X}" for b in p))
    if a.sliders is None:
        return
    kinds = [int(k) for k in a.kinds.split(",") if k != ""]
    want = bytes([0x01, 0x01, a.sliders, len(kinds), *kinds])
    print("expected payload:", " ".join(f"{b:02X}" for b in want))
    if any(p == want for p in hellos):
        print("HELLO OK")
    else:
        sys.exit("HELLO MISMATCH or missing")


if __name__ == "__main__":
    main()
```

Run: `cd /root/dev/volco/.claude/worktrees/firmware && ~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --guard-only && ~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB0 --guard-only; echo "exit $?"`
Expected: `/dev/ttyUSB1: USB 1a86:7523 OK`, then a refusal for `/dev/ttyUSB0` (or "cannot read the USB ID" if that node isn't present in the container) and a non-zero exit.

- [ ] **Step 4: Check today's firmware against the tool (baseline)**

The board currently runs the stage 1 CI build, which uses today's `config.h` (DIY).
Run: `~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --sliders 5 --kinds 1,1,2`
Expected: `HELLO OK` (payload `01 01 05 03 01 01 02`, the golden `hello_led_hardware` frame). If the board runs something else, record what it reported; Task 7 flashes the new build anyway.

- [ ] **Step 5: Build `esp32` (main.cpp is still the old one) and commit**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio run -e esp32 2>&1 | tail -3`
Expected: SUCCESS. The old `main.cpp` still compiles, because `config.h` is unchanged until Task 5, and nanopb is generated but unused.

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/platformio.ini .github/workflows/build.yml firmware/tools/gen_seed_config.py firmware/src/seed_config.h firmware/tools/hello_check.py
/usr/bin/git commit -m "build(firmware): esp32 env with nanopb; stage-3 seed generator; hello_check tool" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The config store (NVS, boot table, seed)

**Files:**
- Create: `firmware/src/config_store.h`, `firmware/src/config_store.cpp`

**Interfaces:**
- Consumes: `decodeConfig`, `validateConfig`, `esp32Chip`, `crc32`, `toRuntime`, `kSeedConfig`.
- Produces:
  ```cpp
  enum ConfigState : uint8_t { CONFIG_NONE = 0, CONFIG_STORED = 1, CONFIG_DAMAGED = 2 };  // = Hello configState (stage 4)
  struct LoadedConfig { volco::RuntimeConfig rc; ConfigState state; uint32_t crc; };
  void config_load(LoadedConfig& out);
  ```

- [ ] **Step 1: Write the store**

Create `firmware/src/config_store.h`:

```cpp
#pragma once
// The controller config in NVS (spec §6.6): namespace "volco", key "cfg", the exact bytes.
#include <stdint.h>

#include "volco_runtime.h"

enum ConfigState : uint8_t { CONFIG_NONE = 0, CONFIG_STORED = 1, CONFIG_DAMAGED = 2 };

struct LoadedConfig {
  volco::RuntimeConfig rc;  // what the firmware runs: the stored config, or the built-in defaults
  ConfigState state;
  uint32_t crc;             // CRC-32 of the stored bytes; 0 when none, or longer than kMaxConfigBlob
};

// Boot (spec §6.6): absent -> defaults/NONE; longer than kMaxConfigBlob -> defaults/DAMAGED/crc 0;
// fails decode or validate -> defaults/DAMAGED/crc of the bytes; otherwise STORED.
// Stage 3 only: when nothing is stored, seed_config.h is stored first (removed in stage 4).
void config_load(LoadedConfig& out);
```

Create `firmware/src/config_store.cpp`:

```cpp
#include "config_store.h"

#include <Preferences.h>

#include "debug_log.h"
#include "seed_config.h"
#include "volco_config.h"
#include "volco_crc32.h"

namespace {
uint8_t blob[volco::kMaxConfigBlob];
volco_config_ControllerConfig decoded;  // static: ~1.5 KB kept off the loop task's stack

void useDefaults(LoadedConfig& out, ConfigState state, uint32_t crc) {
  decoded = volco_config_ControllerConfig_init_default;
  volco::toRuntime(decoded, out.rc);
  out.state = state;
  out.crc = crc;
}
}  // namespace

void config_load(LoadedConfig& out) {
  Preferences prefs;
  prefs.begin("volco", false);
  if (!prefs.isKey("cfg")) {  // stage 3 bridge (spec §15)
    size_t written = prefs.putBytes("cfg", kSeedConfig, sizeof kSeedConfig);
    DEBUG_LOG(written == sizeof kSeedConfig ? "[DBG] Config: stored the seed" : "[DBG] Config: storing the seed failed");
  }
  if (!prefs.isKey("cfg")) {
    prefs.end();
    useDefaults(out, CONFIG_NONE, 0);
    return;
  }
  size_t len = prefs.getBytesLength("cfg");
  if (len > volco::kMaxConfigBlob) {
    prefs.end();
    useDefaults(out, CONFIG_DAMAGED, 0);
    return;
  }
  prefs.getBytes("cfg", blob, len);
  prefs.end();
  const uint32_t crc = volco::crc32(blob, len);
  if (!volco::decodeConfig(blob, len, decoded) || volco::validateConfig(decoded, volco::esp32Chip()).code != volco::CFG_OK) {
    useDefaults(out, CONFIG_DAMAGED, crc);
    return;
  }
  volco::toRuntime(decoded, out.rc);
  out.state = CONFIG_STORED;
  out.crc = crc;
}
```

- [ ] **Step 2: Build and commit**

Run: `cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio run -e esp32 2>&1 | grep -E "error|SUCCESS|FAILED" | head`
Expected: SUCCESS. `config_store.cpp` compiles (the old main.cpp doesn't call it yet).

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add firmware/src/config_store.h firmware/src/config_store.cpp
/usr/bin/git commit -m "feat(firmware): config store - NVS load, boot table, stage-3 seed (spec 6.6)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Firmware runs on `RuntimeConfig` (config.h, indicators, screens, transport, main)

**Files:**
- Rewrite: `firmware/src/config.h`, `firmware/src/transport.h`, `firmware/src/transport.cpp`, `firmware/src/main.cpp`
- Modify: `firmware/src/indicators.h`, `firmware/src/indicators.cpp`, `firmware/src/screens.h`, `firmware/src/screens.cpp`, `firmware/src/debug_log.h`
- Delete: `firmware/src/hardware.h`

**Interfaces:**
- Consumes: Tasks 1, 2 and 4.
- Produces (transport API, used by `main.cpp` only):
  ```cpp
  enum TransportId : uint8_t { TRANSPORT_SERIAL = 0, TRANSPORT_UDP = 1 };
  void transport_begin_serial();
  void transport_begin_udp(const volco::RuntimeUdp& udp);
  uint8_t transport_up_mask();
  void transport_send(uint8_t id, const uint8_t* data, size_t len);
  int transport_receive(uint8_t id, uint8_t* buf, size_t maxLen);
  bool transport_maintain(uint32_t now);  // true once each time Wi-Fi connects
  ```

- [ ] **Step 1: `config.h` holds fixed constants only**

Replace `firmware/src/config.h` with:

```cpp
#pragma once
// Fixed constants (spec §6.3). Everything a user sets (hardware layout, Wi-Fi, screen behaviour,
// input tuning) is the controller config stored in NVS: see lib/volco_config and
// proto/volco_config.proto.

#define SERIAL_BAUD 115200     // USB serial; also what Volco's serial connection uses
#define SERIAL_RX_BUFFER 2048  // holds a whole config write while a screen redraw blocks the loop
#define SERIAL_TX_BUFFER 2048  // a config read reply leaves without blocking the loop
#define UDP_BUFFER 1536        // one datagram: the largest is a ~1.1 KB config burst

#define HOST_TIMEOUT_MS 3000     // no frame from the PC for this long: LEDs off, "not connected" screen
#define KEEPALIVE_MS 1000        // send Alive when nothing else was sent for this long
#define COMMIT_FALLBACK_MS 500   // apply held updates if a Begin's Commit never arrives
#define WIFI_RETRY_MS 10000      // at most this often, re-join a Wi-Fi that failed or dropped
```

Delete `firmware/src/hardware.h`: `/usr/bin/git rm -q firmware/src/hardware.h`.

- [ ] **Step 2: Indicators take a `RuntimeButton`**

Replace `firmware/src/indicators.h` with:

```cpp
#pragma once
#include <stdint.h>

#include "volco_runtime.h"

// value 0 = off; LED: lit for any other value; LED pair: 1 lights A, 2 lights B.
void showIndicator(const volco::RuntimeButton& b, uint8_t value);
// Sets the pins up and shows "off" (a different pin level per polarity).
void initIndicator(const volco::RuntimeButton& b);
```

Replace `firmware/src/indicators.cpp` with:

```cpp
#include "indicators.h"

#include <Arduino.h>

static void writeLed(uint8_t pin, bool lit, bool activeLow) {
  digitalWrite(pin, (lit != activeLow) ? HIGH : LOW);
}

// value 0 = off/unknown, 1..N = on, or choice N. What that means (muted,
// output A, preset 2, ...) is the PC's business; this only renders it.
void showIndicator(const volco::RuntimeButton& b, uint8_t value) {
  switch (b.kind) {
    case volco::KIND_LED:
      writeLed(b.pinA, value != 0, b.activeLow);
      break;
    case volco::KIND_LED_PAIR:
      writeLed(b.pinA, value == 1, b.activeLow);
      writeLed(b.pinB, value == 2, b.activeLow);
      break;
    default:
      break;  // screens are drawn by screens.cpp; KIND_NONE shows nothing
  }
}

void initIndicator(const volco::RuntimeButton& b) {
  switch (b.kind) {
    case volco::KIND_LED_PAIR:
      pinMode(b.pinB, OUTPUT);
      [[fallthrough]];  // pin A is set up like a single LED
    case volco::KIND_LED:
      pinMode(b.pinA, OUTPUT);
      break;
    default:
      break;
  }
  // Start dark. "Off" is a different pin level per polarity, which is why
  // showIndicator, not a plain digitalWrite, sets it: driving an active-low
  // LED LOW here would show it lit until the PC pushes real state.
  showIndicator(b, 0);
}
```

- [ ] **Step 3: Screens are configured at runtime**

In `firmware/src/screens.h`, change the include and `screens_init`:

```cpp
#pragma once
#include <stdint.h>
#include "volco_protocol.h"
#include "volco_runtime.h"

// The screen subsystem (screens.cpp). With no screen buttons every call is a no-op and I2C is
// never touched.
void screens_init(const volco::RuntimeConfig& config, uint32_t now);
```

The other five declarations stay exactly as they are.

In `firmware/src/screens.cpp`:

1. Remove `#include "config.h"`. Keep the other includes.
2. Delete the compile-time block: from `constexpr uint8_t NUM_BUTTONS = …` through the three `static_assert` lines.
3. Keep `kFrameMs`, `i2cByte<Bus>`, `Ssd1306` and `render()` as they are, except that `render()` uses `scroll` instead of `kScroll`.
4. Replace the state declarations (from `struct Screen {` through `volco::Canvas frameA, frameB, frameOut;`) with:

```cpp
struct Screen {
  uint8_t button = 0;
  U8G2* panel = nullptr;
  volco::ScreenSlot slot{0};   // re-made with the configured transition time in screens_init
  volco::Canvas sent;          // the frame last pushed to the panel: unchanged frames cost no I2C
  uint32_t lastRender = 0;
};

Screen screens[volco::kMaxScreens];
uint8_t numScreens = 0;
int8_t screenOf[volco::kMaxButtons];  // button → index into screens, or -1

volco::Panels panels({false, false, false, false, 0, 0, 0});  // configured in screens_init
volco::ScrollTiming scroll = {0, 0, 0};
uint8_t brightness = 255, dimBrightness = 8;
volco::Power lastPower = volco::POWER_NORMAL;
uint8_t nextPanel = 0;

// Scratch frames, static to keep ~3 KB off the loop task's stack.
volco::Canvas frameA, frameB, frameOut;
```

5. Replace `screens_init` with:

```cpp
void screens_init(const volco::RuntimeConfig& rc, uint32_t now) {
  for (uint8_t b = 0; b < volco::kMaxButtons; b++) screenOf[b] = -1;
  numScreens = 0;
  const volco::RuntimeScreens& sc = rc.screens;
  panels = volco::Panels({sc.dimEnabled, sc.offEnabled, sc.pixelShiftEnabled, sc.wakeSwallowsPress,
                          sc.dimAfterMs, sc.offAfterMs, sc.pixelShiftEveryMs});
  scroll = {sc.scrollHoldMs, sc.scrollEndHoldMs, sc.scrollSpeedPxS};
  brightness = sc.brightness;
  dimBrightness = sc.dimBrightness;

  bool started[2] = {false, false};
  for (uint8_t b = 0; b < rc.buttonCount && numScreens < volco::kMaxScreens; b++) {
    const volco::RuntimeButton& btn = rc.buttons[b];
    if (btn.kind != volco::KIND_SCREEN) continue;
    const volco::RuntimeBus& bus = rc.i2c[btn.bus];
    if (!started[btn.bus]) {  // only buses a screen uses are started (GPIO 21 may be an LED)
      (btn.bus == 0 ? Wire : Wire1).begin(bus.sda, bus.scl, bus.clockHz);
      started[btn.bus] = true;
    }
    Screen& s = screens[numScreens];
    s.button = b;
    screenOf[b] = (int8_t)numScreens;
    s.slot = volco::ScreenSlot(sc.transitionMs);
    s.panel = new Ssd1306(btn.bus == 0 ? &i2cByte<0> : &i2cByte<1>);
    s.panel->setI2CAddress(btn.address << 1);  // U8g2 takes the 8-bit form
    s.panel->begin();
    s.panel->setContrast(brightness);
    s.slot.show(volco::notConnectedScreen(), volco::TRANSITION_NONE, now, false);
    memset(s.sent.buf, 0xFF, sizeof s.sent.buf);  // force the first push
    numScreens++;
  }
  if (numScreens == 0) return;
  panels.activity(now);
  DEBUG_LOG("[DBG] Screens: " + String(numScreens));
}
```

6. In the remaining functions, replace:
   - `NUM_SCREENS` → `numScreens` (in `screens_host_lost`, `screens_activity`, `screens_filter_button`, `screens_tick`)
   - `SCREEN_SLOTS` → `numScreens` (in `screens_tick`, where `numScreens > 0` is guaranteed by its early return)
   - `NUM_BUTTONS` → `volco::kMaxButtons` (in `screens_show`)
   - `SCREEN_DIM_BRIGHTNESS` → `dimBrightness`, and `SCREEN_BRIGHTNESS` → `brightness`

   Then check: `grep -nE "NUM_|SCREEN_|I2C[01]_|kScroll" firmware/src/screens.cpp` must print nothing.

- [ ] **Step 4: The transport is rewritten for serial plus UDP**

Replace `firmware/src/transport.h` with:

```cpp
#pragma once
#include <stddef.h>
#include <stdint.h>

#include "volco_runtime.h"

// Two transports (spec §5.5): USB serial, always on, and UDP over Wi-Fi when the config enables
// it. Ids are bit positions in transport_up_mask() and volco::ActiveTransport.
enum TransportId : uint8_t { TRANSPORT_SERIAL = 0, TRANSPORT_UDP = 1 };

void transport_begin_serial();                             // first thing in setup(): logs need it
void transport_begin_udp(const volco::RuntimeUdp& udp);    // Wi-Fi off unless udp.enabled; never blocks
uint8_t transport_up_mask();                               // bit per transport that can send now
void transport_send(uint8_t id, const uint8_t* data, size_t len);
int transport_receive(uint8_t id, uint8_t* buf, size_t maxLen);  // non-blocking; 0 when nothing waits
bool transport_maintain(uint32_t now);  // every loop pass: Wi-Fi retry; true once each time Wi-Fi connects
```

Replace `firmware/src/transport.cpp` with:

```cpp
#include "transport.h"

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>

#include "config.h"
#include "debug_log.h"
#include "volco_protocol.h"

namespace {
volco::RuntimeUdp cfg;
WiFiUDP udp;
uint8_t udpPacket[UDP_BUFFER];
size_t udpLen = 0, udpPos = 0;
bool udpStarted = false, wasConnected = false;
uint32_t lastBegin = 0;

uint32_t toU32(const IPAddress& a) {
  return ((uint32_t)a[0] << 24) | ((uint32_t)a[1] << 16) | ((uint32_t)a[2] << 8) | (uint32_t)a[3];
}
IPAddress pcAddress() {
  return IPAddress((cfg.pcIp >> 24) & 0xFF, (cfg.pcIp >> 16) & 0xFF, (cfg.pcIp >> 8) & 0xFF, cfg.pcIp & 0xFF);
}
bool udpUp() { return cfg.enabled && udpStarted && WiFi.status() == WL_CONNECTED; }
void joinWifi(uint32_t now) {
  WiFi.begin(cfg.ssid, cfg.password[0] ? cfg.password : nullptr);
  lastBegin = now;
}
}  // namespace

void transport_begin_serial() {
  // A preset change is a ~560-byte burst and a config write ~1.1 KB, while a screen redraw blocks
  // the loop for ~30 ms: the default buffers could overflow.
  Serial.setRxBufferSize(SERIAL_RX_BUFFER);
  Serial.setTxBufferSize(SERIAL_TX_BUFFER);
  Serial.begin(SERIAL_BAUD);
}

void transport_begin_udp(const volco::RuntimeUdp& udpConfig) {
  cfg = udpConfig;
  if (!cfg.enabled) {
    WiFi.mode(WIFI_OFF);  // never WiFi.disconnect() before Wi-Fi is up: it logs ESP_ERR_WIFI_NOT_INIT
    return;
  }
  WiFi.persistent(false);  // the driver must not copy credentials into our NVS partition
  WiFi.setAutoReconnect(true);
  WiFi.mode(WIFI_STA);
  joinWifi(millis());
  DEBUG_LOG(String("[DBG] Wi-Fi: joining ") + cfg.ssid);
}

uint8_t transport_up_mask() {
  return (uint8_t)((1u << TRANSPORT_SERIAL) | (udpUp() ? (1u << TRANSPORT_UDP) : 0u));
}

void transport_send(uint8_t id, const uint8_t* data, size_t len) {
  if (id == TRANSPORT_SERIAL) {
    Serial.write(data, len);
  } else if (id == TRANSPORT_UDP && udpUp()) {
    udp.beginPacket(pcAddress(), cfg.pcPort);
    udp.write(data, len);
    udp.endPacket();
  }
}

int transport_receive(uint8_t id, uint8_t* buf, size_t maxLen) {
  if (id == TRANSPORT_SERIAL) {
    int available = Serial.available();
    if (available <= 0) return 0;
    size_t toRead = min((size_t)available, maxLen);
    return Serial.readBytes(buf, toRead);
  }
  if (id != TRANSPORT_UDP || !udpUp()) return 0;
  if (udpPos >= udpLen) {
    int size = udp.parsePacket();
    if (size <= 0) return 0;
    int r = udp.read(udpPacket, sizeof udpPacket);
    if (r <= 0 || !volco::acceptUdpSource(toU32(udp.remoteIP()), cfg.pcIp)) {  // strangers: dropped
      udpLen = udpPos = 0;
      return 0;
    }
    udpLen = (size_t)r;
    udpPos = 0;
  }
  size_t n = min(udpLen - udpPos, maxLen);
  memcpy(buf, udpPacket + udpPos, n);
  udpPos += n;
  return (int)n;
}

bool transport_maintain(uint32_t now) {
  if (!cfg.enabled) return false;
  const wl_status_t st = WiFi.status();
  const bool connected = st == WL_CONNECTED;
  bool edge = false;
  if (connected && !wasConnected) {
    if (!udpStarted) udpStarted = udp.begin(cfg.localPort);
    DEBUG_LOG("[DBG] Wi-Fi: connected, " + WiFi.localIP().toString());
    edge = true;
  }
  if (!connected && (st == WL_CONNECT_FAILED || st == WL_NO_SSID_AVAIL || st == WL_DISCONNECTED) &&
      now - lastBegin >= WIFI_RETRY_MS)
    joinWifi(now);
  wasConnected = connected;
  return edge;
}
```

- [ ] **Step 5: `main.cpp` runs on the loaded config**

Replace `firmware/src/main.cpp` with:

```cpp
#include <Arduino.h>

#include "config.h"
#include "config_store.h"
#include "debug_log.h"
#include "indicators.h"
#include "screens.h"
#include "transport.h"
#include "volco_protocol.h"
#include "volco_runtime.h"

// ============================================================
// State. Everything about the hardware comes from the stored config (config_store).
// ============================================================
static LoadedConfig loaded;
static const volco::RuntimeConfig& rc = loaded.rc;

static uint16_t lastSliderValues[volco::kMaxSliders];
static float smoothedValues[volco::kMaxSliders] = {0};
static unsigned long lastSliderReadTime = 0;

static bool buttonRawStates[volco::kMaxButtons];
static bool buttonReportedStates[volco::kMaxButtons];
static unsigned long buttonChangeTime[volco::kMaxButtons];

// What Hello reports (wire values: 0 none, 1 LED, 2 LED pair, 3 screen).
static uint8_t kinds[volco::kMaxButtons];

static volco::ActiveTransport active;  // spec §5.5
static volco::FrameParser parsers[2];  // one per transport, so their bytes never mix

// ============================================================
// The PC link: protocol state lives in lib/volco_protocol; this sink connects it to the hardware.
// ============================================================
class HardwareSink : public volco::LinkSink {
 public:
  void send(const uint8_t* frame, size_t len) override {
    const uint8_t targets = active.targets(transport_up_mask());
    for (uint8_t t = 0; t < 2; t++)
      if (targets & (1u << t)) transport_send(t, frame, len);
  }
  void applyLed(uint8_t button, uint8_t value) override { showIndicator(rc.buttons[button], value); }
  void applyScreen(uint8_t button, uint8_t transition, const volco::ScreenContent& c, uint32_t now) override {
    screens_show(button, transition, c, now);
  }
  void hostLost() override {
    DEBUG_LOG("[DBG] PC silent: LEDs off, screens show 'not connected'");
    active.onHostLost();
    for (uint8_t i = 0; i < rc.buttonCount; i++) showIndicator(rc.buttons[i], 0);
    screens_host_lost(millis());
  }
};

static HardwareSink sink;
static volco::Link* pcLink = nullptr;

// Spec §5.5 exception: whenever Wi-Fi connects, Hello goes out on UDP even if serial is latched.
static void sendHelloOnUdp() {
  uint8_t f[4 + volco::kMaxButtons + 3];
  transport_send(TRANSPORT_UDP, f, volco::encodeHello(rc.sliderCount, kinds, rc.buttonCount, f));
}

// ============================================================
// Setup (spec §7 order): serial, config, pins, transports, screens, link.
// ============================================================
void setup() {
  transport_begin_serial();
  config_load(loaded);
  DEBUG_LOG("[DBG] Config: state " + String(loaded.state) + ", " + String(rc.sliderCount) + " sliders, " +
            String(rc.buttonCount) + " buttons");

  for (uint8_t i = 0; i < rc.sliderCount; i++) pinMode(rc.sliderPins[i], INPUT);
  for (uint8_t i = 0; i < rc.buttonCount; i++) {
    pinMode(rc.buttons[i].pin, INPUT_PULLUP);
    buttonRawStates[i] = HIGH;
    buttonReportedStates[i] = HIGH;
    buttonChangeTime[i] = 0;
    initIndicator(rc.buttons[i]);
    kinds[i] = rc.buttons[i].kind;  // Kind values are the wire values
  }

  analogReadResolution(12);
  for (uint8_t i = 0; i < rc.sliderCount; i++) {
    lastSliderValues[i] = analogRead(rc.sliderPins[i]);
    smoothedValues[i] = lastSliderValues[i];
  }

  transport_begin_udp(rc.udp);
  screens_init(rc, millis());
  static volco::Link link({rc.sliderCount, rc.buttonCount, kinds, HOST_TIMEOUT_MS, KEEPALIVE_MS, COMMIT_FALLBACK_MS},
                          sink);
  pcLink = &link;
  pcLink->begin(millis());  // Hello: the ESP32 may have just reset because the PC opened the port
  DEBUG_LOG("[DBG] Setup complete");
}

// ============================================================
// Main loop: sliders → buttons → receive → link → screens → Wi-Fi. Never blocks for long.
// ============================================================
void loop() {
  const uint32_t now = millis();

  // 1. Sliders: EMA smoothing, then a deadband.
  if (now - lastSliderReadTime >= rc.input.sliderIntervalMs) {
    lastSliderReadTime = now;
    for (uint8_t i = 0; i < rc.sliderCount; i++) {
      uint16_t raw = analogRead(rc.sliderPins[i]);
      smoothedValues[i] = raw * rc.input.filterWeight + smoothedValues[i] * (1.0f - rc.input.filterWeight);
      uint16_t filtered = (uint16_t)smoothedValues[i];
      int16_t delta = (int16_t)filtered - (int16_t)lastSliderValues[i];
      if (abs(delta) > rc.input.sliderDeadband) {
        lastSliderValues[i] = filtered;
        screens_activity(now);
        pcLink->sendSlider(i, filtered, now);
      }
    }
  }

  // 2. Buttons: debounce on timestamps, so a press is seen even when a redraw delayed this pass.
  for (uint8_t i = 0; i < rc.buttonCount; i++) {
    bool raw = digitalRead(rc.buttons[i].pin);
    if (raw != buttonRawStates[i]) {
      buttonRawStates[i] = raw;
      buttonChangeTime[i] = now;
    }
    if (now - buttonChangeTime[i] >= rc.input.debounceMs && buttonRawStates[i] != buttonReportedStates[i]) {
      buttonReportedStates[i] = buttonRawStates[i];
      bool pressed = buttonReportedStates[i] == LOW;  // INPUT_PULLUP: LOW = pressed
      if (screens_filter_button(i, pressed, now)) pcLink->sendButton(i, pressed, now);
    }
  }

  // 3. Receive: drain each transport, frame by frame. Latch before the link replies (spec §5.5).
  uint8_t buf[256];
  for (uint8_t t = 0; t < 2; t++) {
    int n;
    while ((n = transport_receive(t, buf, sizeof buf)) > 0)
      for (int i = 0; i < n; i++)
        if (parsers[t].feed(buf[i])) {
          active.onValidFrame(t);
          pcLink->onPayload(parsers[t].payload(), parsers[t].length(), now);
        }
  }

  // 4. Link: keepalive, host timeout, Commit fallback.
  pcLink->tick(now);

  // 5. Screens: power state, then redraw at most one panel.
  screens_tick(now);

  // 6. Wi-Fi: retry, and Hello on UDP each time it connects.
  if (transport_maintain(now)) sendHelloOnUdp();
}
```

In `firmware/src/debug_log.h`, change the comment to: `// Debug lines go to USB serial only. They share the port with frames; the PC's parser only acts on valid 0xAA frames, so log lines are harmlessly skipped.`

- [ ] **Step 6: Build, run the native suite, commit**

Run:
```bash
cd /root/dev/volco/.claude/worktrees/firmware/firmware && ~/.platformio/penv/bin/pio run -e esp32 2>&1 | grep -E "error|warning: unused|RAM:|Flash:|SUCCESS|FAILED" | head -20
~/.platformio/penv/bin/pio test -e native 2>&1 | tail -1
grep -rnE "TRANSPORT_(WEBSOCKET|LOCAL_DEBUG)|WebSockets|hardware\.h|SLIDER_PINS|BUTTONS\[|IND_" src/ ; echo "leftovers grep exit $?"
```
Expected:
- `SUCCESS`, with RAM and flash printed and no errors
- native suites all passed
- the leftovers grep prints nothing (`exit 1`)

Fix any compile error in the code just written (names and signatures only; the design is fixed above).

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add -A firmware/src
/usr/bin/git commit -m "feat(firmware): run on the stored config; serial + UDP with the active-transport rule (spec 5.5, 6.3, 7)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Hardware verification on the DIY board (this container)

**Files:** none (uses `hello_check.py`, esptool and pio)

**Interfaces:**
- Consumes: the Task 5 build, and the ESP32 on `/dev/ttyUSB1`.
- Produces: evidence lines in the ledger, and the board left running the DIY-seeded build.

- [ ] **Step 1: Guard, erase NVS (fresh seed), flash**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --guard-only
~/.platformio/penv/bin/python ~/.platformio/packages/tool-esptoolpy/esptool.py --chip esp32 --port /dev/ttyUSB1 erase-region 0x9000 0x5000
cd firmware && ~/.platformio/penv/bin/pio run -e esp32 -t upload --upload-port /dev/ttyUSB1 2>&1 | tail -3
```
Expected: the guard says OK; the erase succeeds; the upload ends with `SUCCESS`.

- [ ] **Step 2: The boot Hello matches the DIY layout**

Run: `cd /root/dev/volco/.claude/worktrees/firmware && ~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --sliders 5 --kinds 1,1,2`
Expected:
- the log shows `[DBG] Config: stored the seed`, then `[DBG] Config: state 1, 5 sliders, 3 buttons` and `[DBG] Setup complete`
- **no** `ESP_ERR_WIFI_NOT_INIT` line
- `HELLO OK`

- [ ] **Step 3: The config persists (no re-seed on the second boot)**

Run the same command again.
Expected: `[DBG] Config: state 1, 5 sliders, 3 buttons` **without** `stored the seed`, and `HELLO OK`.

- [ ] **Step 4: A different seeded layout changes what the firmware reports (checklist item 4 by re-seed)**

Write `/tmp/claude-0/-root-dev-volco/d68eb0f5-b900-4b17-8565-46298fd08eea/scratchpad/alt-seed.json`:

```json
{
  "preset": "alt-test",
  "sliders": [ {"pin": 33}, {"pin": 32} ],
  "buttons": [ {"pin": 4, "led": {"pin": 12, "activeLow": true}}, {"pin": 14} ]
}
```

Then:
```bash
cd /root/dev/volco/.claude/worktrees/firmware
~/.platformio/penv/bin/python firmware/tools/gen_seed_config.py /tmp/claude-0/-root-dev-volco/d68eb0f5-b900-4b17-8565-46298fd08eea/scratchpad/alt-seed.json
~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --guard-only
~/.platformio/penv/bin/python ~/.platformio/packages/tool-esptoolpy/esptool.py --chip esp32 --port /dev/ttyUSB1 erase-region 0x9000 0x5000
cd firmware && ~/.platformio/penv/bin/pio run -e esp32 -t upload --upload-port /dev/ttyUSB1 2>&1 | tail -1 && cd ..
~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --sliders 2 --kinds 1,0
```
Expected: `state 1, 2 sliders, 2 buttons`, then `HELLO OK` for payload `01 01 02 02 01 00`.

- [ ] **Step 5: A Blank seed boots with nothing configured (Review Focus 1)**

Write `…/scratchpad/blank-seed.json` as `{"preset": "blank"}` and repeat Step 4 with it, expecting `--sliders 0 --kinds ""`.
Expected: `state 1, 0 sliders, 0 buttons`, then `HELLO OK` for payload `01 01 00 00`. The firmware keeps running: after the first one, `Alive` frames (`AA 01 02 02`) appear in the raw capture (check with `--seconds 6`, looking for repeated output and no reset loop with `rst:` lines).

- [ ] **Step 6: Restore the DIY seed and leave the board on it**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
~/.platformio/penv/bin/python firmware/tools/gen_seed_config.py
/usr/bin/git diff --exit-code firmware/src/seed_config.h && echo "seed restored"
~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --guard-only
~/.platformio/penv/bin/python ~/.platformio/packages/tool-esptoolpy/esptool.py --chip esp32 --port /dev/ttyUSB1 erase-region 0x9000 0x5000
cd firmware && ~/.platformio/penv/bin/pio run -e esp32 -t upload --upload-port /dev/ttyUSB1 2>&1 | tail -1 && cd ..
~/.platformio/penv/bin/python firmware/tools/hello_check.py --port /dev/ttyUSB1 --sliders 5 --kinds 1,1,2
```
Expected: `seed restored` (the committed DIY seed is back, byte for byte), then `HELLO OK`. Record every step's result in the ledger.

---

### Task 7: [USER-RUN] Volco on Windows with the new firmware, and screens

**Files:** none

- [ ] **Step 1: Ask the user to run checklist items 3 and 12**

Tell the user:
1. **Item 3:** plug the ESP32 (now running this branch's firmware with the DIY seed) into the Windows PC and run Volco as usual (serial). Check that the sliders change volume, the buttons mute and switch, and the LEDs follow.
2. **Item 12 (only if you have SSD1306 screens wired):** screens need a config with screen buttons. In stage 3 that means a seed. Write a JSON like the DIY one, with `"screen": {"bus": 0, "address": 60}` on the screen buttons and `"i2c0": {"sda": 25, "scl": 26}`. Then run `gen_seed_config.py` with it, erase NVS, upload, and check that the screens render and dim. Restore the DIY seed afterwards (Task 6 Step 6).

Record what the user reports. If item 3 fails, stop and debug before Task 8.

---

### Task 8: Docs and CI pass

**Files:**
- Modify: `CLAUDE.md`, `firmware/README.md`

- [ ] **Step 1: Update CLAUDE.md**

In `CLAUDE.md`:
- Replace the sentence that begins `The \`firmware-test\` CI job runs \`pio test -e native\` and builds all four ESP32 environments` with: `The \`firmware-test\` CI job runs \`pio test -e native\`, checks the firmware id and the config vectors, and builds the \`esp32\` environment.` (keep the rest of that paragraph about `gen_assets.py`).
- Replace `` `src/` (`main.cpp`, `config.h`, `hardware.h`, `transport.*`, `indicators.*`, `screens.*`) `` with `` `src/` (`main.cpp`, `config.h` (fixed constants), `config_store.*`, `seed_config.h`, `transport.*`, `indicators.*`, `screens.*`) ``, and add `volco_config` to the `lib/` list.
- Replace the paragraph beginning `Transport is selected at compile time via` with:

  ```markdown
  The firmware reads its hardware layout (sliders, buttons and their indicators, I2C buses), the optional Wi-Fi/UDP link and the screen and input settings from the controller config stored in NVS (`config_store`, spec §6.6), never from `config.h`. Until stage 4 lets Volco send a config, an empty NVS is seeded from `src/seed_config.h`, which `firmware/tools/gen_seed_config.py` generates from `proto/presets/diy-reference.json` (or another preset JSON for testing; erase NVS with `esptool erase-region 0x9000 0x5000` first). USB serial is always on; UDP runs alongside when the config enables it, and the controller answers on whichever transport last sent it a valid frame (spec §5.5). `firmware/tools/hello_check.py --port /dev/ttyUSB1 --sliders N --kinds …` resets the board and checks its boot Hello (it refuses any USB device but the DIY board's CH340).
  ```
- In the firmware commands block, change `pio run` / `pio run -t upload` so they name the env: `cd firmware && pio run -e esp32` / `cd firmware && pio run -e esp32 -t upload`.

- [ ] **Step 2: Update firmware/README.md**

- Replace the whole `## \`config.h\`` section (from that heading up to, but not including, `## Wiring screens and choosing pins`) with:

  ```markdown
  ## Configuration

  The firmware is generic: nothing about a build's hardware is compiled in. At boot it loads the controller config from NVS (`config_store.cpp`; spec §6.6), decodes and validates it (`lib/volco_config`), and runs from the resulting `RuntimeConfig`:

  - **Hardware:** the sliders (one ADC pin each), the buttons (an input pin, plus an indicator: none, one LED, an LED pair, or an SSD1306 screen on I2C bus 0/1 at 0x3C/0x3D), and the I2C buses' pins and clock. A table's order is the input number Volco sees.
  - **Wi-Fi/UDP:** off unless enabled; SSID, password, the PC's IP and port, and the local port. USB serial is always on.
  - **Screens and input:** brightness, slide and scroll timing, dim/off/pixel-shift, debounce, slider sampling, smoothing and deadband.

  The schema is `proto/volco_config.proto`. A config that is missing, oversized, or fails decoding or validation leaves the firmware on built-in defaults (no inputs, serial only), still answering Hello.

  **Until Volco can send a config (stage 4),** an empty NVS is seeded from `src/seed_config.h`. To try another layout, write a preset JSON (the protobuf JSON mapping of `ControllerConfig`, like `proto/presets/diy-reference.json`), then:

  ```
  ~/.platformio/penv/bin/python firmware/tools/gen_seed_config.py my-layout.json
  esptool --port <port> erase-region 0x9000 0x5000
  cd firmware && pio run -e esp32 -t upload
  ```

  Fixed constants (serial baud, buffer sizes, protocol timing) stay in `src/config.h`.
  ```

- In `## Files`, replace the `src/config.h` and `src/hardware.h` rows with:

  ```markdown
  | `src/config.h` | Fixed constants: serial baud, buffer sizes, protocol timing. |
  | `src/config_store.*` | Loads the controller config from NVS (and, in stage 3, stores the seed). |
  | `src/seed_config.h` | Generated stage 3 seed (DIY reference). |
  ```

  Add `| \`lib/volco_config/\` | The controller config: decode, validation, chip table, CRC, chunked receiver, \`RuntimeConfig\`. |`, and change the `src/transport.*` row to `| \`src/transport.*\` | USB serial (always on) and UDP over Wi-Fi (when enabled). |`.

- In `### Migrating a button from LED to screen`, replace the body with: `Change the button's indicator in its config from \`led\` to \`screen\` (bus and address), and make sure that bus has \`sda\`/\`scl\`. In stage 3 that is a seed change (see Configuration); from stage 4, Volco's Controller page. There is nothing to change in Volco's actions.`

- Replace the `### Transport abstraction` table and its paragraph with a short description: serial always on; UDP when enabled; one parser per transport; the active-transport rule (spec §5.5); `transport_maintain()` retries Wi-Fi and reports each connect, which sends a Hello on UDP.

- In `## Tests`, replace the CI sentence with: `CI runs it too, checks the firmware id and the config vectors, and builds the \`esp32\` environment. The golden frames in \`test/golden/frames.txt\` and the config vectors in \`test/golden/config.txt\` are shared with the C# tests.`

Then check: `grep -nE "TRANSPORT_|IND_LED\(|SLIDER_PINS|denky32|config\.h\b.*edit" firmware/README.md CLAUDE.md` must print only lines that describe history, or nothing.

- [ ] **Step 3: Commit, push, check CI**

```bash
cd /root/dev/volco/.claude/worktrees/firmware
/usr/bin/git add CLAUDE.md firmware/README.md
/usr/bin/git commit -m "docs: firmware runs on the stored controller config (stage 3)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
/usr/bin/git push
```
Wait for `Build Volco` on `controller-config-stage2`. Expected: green, including `Build firmware (ESP32)` on `esp32` and the firmware-id check (the id changes, which is fine).

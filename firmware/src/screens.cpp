#include "screens.h"

#include <Arduino.h>
#include <U8g2lib.h>
#include <Wire.h>
#include <driver/gpio.h>
#include <string.h>

#include "debug_log.h"
#include "volco_render.h"
#include "volco_screen_state.h"

namespace {

constexpr uint32_t kFrameMs = 20;  // at most one redraw per panel every 20 ms

// Buses 2 and 3: the ESP32 has only two I2C controllers, so these are bit-banged (PR #66). Only
// screens talk on them and an SSD1306 never holds SCL low, so SCL is driven push-pull (sharp
// edges) and only SDA is open-drain, for the ACK, which is clocked but not read (as U8g2's own
// soft I2C). Interrupts only stretch the timing, which I2C allows.
class SoftI2c {
 public:
  void begin(uint8_t sda, uint8_t scl, uint32_t hz) {
    sda_ = static_cast<gpio_num_t>(sda);
    scl_ = static_cast<gpio_num_t>(scl);
    half_ = getCpuFrequencyMhz() * 1000000u / (2 * hz);
    // >= 300 ns of data hold after SCL falls, also when the config raises the clock to 1 MHz.
    hold_ = max(half_ / 4, getCpuFrequencyMhz() * 3u / 10u);
    gpio_set_level(sda_, 1);
    gpio_set_level(scl_, 1);
    gpio_config_t c = {};
    c.pin_bit_mask = 1ULL << sda_;
    c.mode = GPIO_MODE_INPUT_OUTPUT_OD;
    c.pull_up_en = GPIO_PULLUP_ENABLE;
    gpio_config(&c);
    c.pin_bit_mask = 1ULL << scl_;
    c.mode = GPIO_MODE_OUTPUT;
    c.pull_up_en = GPIO_PULLUP_DISABLE;
    gpio_config(&c);
  }

  void start(uint8_t address8) {  // the bus is idle: both lines high
    gpio_set_level(sda_, 0);
    spin(half_);
    gpio_set_level(scl_, 0);
    spin(hold_);
    write(address8);
  }

  void write(const uint8_t* p, size_t n) {
    while (n--) write(*p++);
  }

  void stop() {
    gpio_set_level(sda_, 0);
    spin(half_);
    gpio_set_level(scl_, 1);
    spin(half_);
    gpio_set_level(sda_, 1);
    spin(half_);
  }

 private:
  void write(uint8_t b) {
    for (uint8_t m = 0x80; m; m >>= 1) clockBit(b & m);
    clockBit(true);  // release SDA for the ACK clock
  }

  void clockBit(bool high) {
    gpio_set_level(sda_, high);
    spin(half_);  // data setup, and the open-drain rise, while SCL is low
    gpio_set_level(scl_, 1);
    spin(half_);
    gpio_set_level(scl_, 0);
    spin(hold_);
  }

  static void spin(uint32_t cycles) {
    const uint32_t t = ESP.getCycleCount();
    while (ESP.getCycleCount() - t < cycles) {}
  }

  gpio_num_t sda_ = GPIO_NUM_NC, scl_ = GPIO_NUM_NC;
  uint32_t half_ = 0, hold_ = 0;
};

SoftI2c softBus[2];  // buses 2 and 3; pins and clock come from the config in screens_init

// Every bus is started in screens_init, and each panel writes through this for its bus. (U8g2's
// own Wire1 driver, the *_2ND_HW_I2C panels, only exists when the core defines
// WIRE_INTERFACES_COUNT > 1, which ESP32 core 3.x doesn't: those panels silently sent nothing.)
template <int Bus>
uint8_t i2cByte(u8x8_t* u8x8, uint8_t msg, uint8_t argInt, void* argPtr) {
  if constexpr (Bus < 2) {
    TwoWire& wire = Bus == 0 ? Wire : Wire1;
    switch (msg) {
      case U8X8_MSG_BYTE_SEND: wire.write(static_cast<uint8_t*>(argPtr), argInt); return 1;
      case U8X8_MSG_BYTE_START_TRANSFER: wire.beginTransmission(u8x8_GetI2CAddress(u8x8) >> 1); return 1;
      case U8X8_MSG_BYTE_END_TRANSFER: wire.endTransmission(); return 1;
      case U8X8_MSG_BYTE_INIT:  // the bus is already started
      case U8X8_MSG_BYTE_SET_DC: return 1;
      default: return 0;
    }
  } else {
    SoftI2c& bus = softBus[Bus - 2];
    switch (msg) {
      case U8X8_MSG_BYTE_SEND: bus.write(static_cast<uint8_t*>(argPtr), argInt); return 1;
      case U8X8_MSG_BYTE_START_TRANSFER: bus.start(u8x8_GetI2CAddress(u8x8)); return 1;  // 8-bit write address
      case U8X8_MSG_BYTE_END_TRANSFER: bus.stop(); return 1;
      case U8X8_MSG_BYTE_INIT:
      case U8X8_MSG_BYTE_SET_DC: return 1;
      default: return 0;
    }
  }
}

constexpr u8x8_msg_cb kByteCb[volco::kI2cBuses] = {i2cByte<0>, i2cByte<1>, i2cByte<2>, i2cByte<3>};

void startBus(uint8_t index, const volco::RuntimeBus& bus) {
  switch (index) {
    // A bad pin pair fails here, and the screens would otherwise just stay dark (spec 14.1).
    case 0:
      if (!Wire.begin(bus.sda, bus.scl, bus.clockHz)) DEBUG_LOG("[DBG] I2C bus 0 failed to start on " + String(bus.sda) + "/" + String(bus.scl));
      break;
    case 1:
      if (!Wire1.begin(bus.sda, bus.scl, bus.clockHz)) DEBUG_LOG("[DBG] I2C bus 1 failed to start on " + String(bus.sda) + "/" + String(bus.scl));
      break;
    default: softBus[index - 2].begin(bus.sda, bus.scl, bus.clockHz); break;
  }
}

class Ssd1306 : public U8G2 {
 public:
  explicit Ssd1306(u8x8_msg_cb byteCb) {
    u8g2_Setup_ssd1306_i2c_128x64_noname_f(&u8g2, U8G2_R0, byteCb, u8x8_gpio_and_delay_arduino);
  }
};

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

void render(Screen& s, uint32_t now) {
  if (s.slot.inTransition(now)) {
    volco::renderScreen(frameA, s.slot.previous(), 0, scroll);
    volco::renderScreen(frameB, s.slot.current(), 0, scroll);
    volco::composeSlide(frameA, frameB, s.slot.progress(now), s.slot.transition() != volco::TRANSITION_SLIDE_RIGHT, frameOut);
  } else {
    volco::renderScreen(frameOut, s.slot.current(), s.slot.scrollSince(now), scroll);
  }
  int8_t dx, dy;
  panels.shift(now, dx, dy);
  if (dx || dy) {
    volco::shiftFrame(frameOut, dx, dy, frameA);
    frameOut = frameA;
  }
  if (frameOut == s.sent) return;
  s.sent = frameOut;
  memcpy(s.panel->getBufferPtr(), frameOut.buf, sizeof frameOut.buf);  // same page layout as U8g2's full buffer
  s.panel->sendBuffer();
}

}  // namespace

void screens_init(const volco::RuntimeConfig& rc, uint32_t now) {
  for (uint8_t b = 0; b < volco::kMaxButtons; b++) screenOf[b] = -1;
  numScreens = 0;
  const volco::RuntimeScreens& sc = rc.screens;
  panels = volco::Panels({sc.dimEnabled, sc.offEnabled, sc.pixelShiftEnabled, sc.wakeSwallowsPress,
                          sc.dimAfterMs, sc.offAfterMs, sc.pixelShiftEveryMs});
  scroll = {sc.scrollHoldMs, sc.scrollEndHoldMs, sc.scrollSpeedPxS};
  brightness = sc.brightness;
  dimBrightness = sc.dimBrightness;

  bool started[volco::kI2cBuses] = {false, false, false, false};
  for (uint8_t b = 0; b < rc.buttonCount && numScreens < volco::kMaxScreens; b++) {
    const volco::RuntimeButton& btn = rc.buttons[b];
    if (btn.kind != volco::KIND_SCREEN) continue;
    if (!started[btn.bus]) {  // only buses a screen uses are started (GPIO 21 may be an LED)
      startBus(btn.bus, rc.i2c[btn.bus]);
      started[btn.bus] = true;
    }
    Screen& s = screens[numScreens];
    s.button = b;
    screenOf[b] = (int8_t)numScreens;
    s.slot = volco::ScreenSlot(sc.transitionMs);
    s.panel = new Ssd1306(kByteCb[btn.bus]);
    s.panel->setI2CAddress(btn.address << 1);  // U8g2 takes the 8-bit form
    s.panel->begin();
    s.panel->setContrast(brightness);
    s.slot.show(volco::notConnectedScreen(), volco::TRANSITION_NONE, now, false);
    memset(s.sent.buf, 0xFF, sizeof s.sent.buf);  // force the first push
    numScreens++;
  }
  if (numScreens == 0) return;  // no screens: never touch I2C
  panels.activity(now);
  DEBUG_LOG("[DBG] Screens: " + String(numScreens));
}

void screens_show(uint8_t button, uint8_t transition, const volco::ScreenContent& content, uint32_t now) {
  if (button >= volco::kMaxButtons || screenOf[button] < 0) return;
  screens[screenOf[button]].slot.show(content, transition, now, panels.power(now) == volco::POWER_OFF);
}

void screens_host_lost(uint32_t now) {
  for (uint8_t i = 0; i < numScreens; i++)
    screens[i].slot.show(volco::notConnectedScreen(), volco::TRANSITION_NONE, now, false);
}

void screens_activity(uint32_t now) {
  if (numScreens > 0) panels.activity(now);
}

bool screens_filter_button(uint8_t button, bool pressed, uint32_t now) {
  if (numScreens == 0) return true;
  return panels.filterButton(button, pressed, now);
}

void screens_tick(uint32_t now) {
  if (numScreens == 0) return;

  const volco::Power power = panels.power(now);
  if (power != lastPower) {
    for (uint8_t i = 0; i < numScreens; i++) {
      screens[i].panel->setPowerSave(power == volco::POWER_OFF ? 1 : 0);
      if (power != volco::POWER_OFF)
        screens[i].panel->setContrast(power == volco::POWER_DIM ? dimBrightness : brightness);
    }
    lastPower = power;
  }
  if (power == volco::POWER_OFF) return;

  // Redraw at most one panel per pass (~25-30 ms of I2C at 400 kHz), round-robin, so one pass
  // never blocks the loop for longer than one panel's redraw (spec §4.6).
  for (uint8_t k = 0; k < numScreens; k++) {
    uint8_t i = (uint8_t)((nextPanel + k) % numScreens);
    Screen& s = screens[i];
    if (now - s.lastRender < kFrameMs) continue;
    s.lastRender = now;
    nextPanel = (uint8_t)((i + 1) % numScreens);
    render(s, now);
    break;
  }
}

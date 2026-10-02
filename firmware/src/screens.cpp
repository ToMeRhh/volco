#include "screens.h"

#include <Arduino.h>
#include <U8g2lib.h>
#include <Wire.h>
#include <string.h>

#include "config.h"
#include "debug_log.h"
#include "volco_render.h"
#include "volco_screen_state.h"

namespace {

constexpr uint8_t NUM_BUTTONS = sizeof(BUTTONS) / sizeof(BUTTONS[0]);

constexpr uint8_t countScreens(int bus = -1) {
  uint8_t n = 0;
  for (const ButtonDef& b : BUTTONS)
    if (b.indicator.kind == IND_KIND_SCREEN && (bus < 0 || b.indicator.bus == bus)) n++;
  return n;
}

constexpr bool screensValid() {
  for (const ButtonDef& b : BUTTONS)
    if (b.indicator.kind == IND_KIND_SCREEN &&
        (b.indicator.bus > 1 || (b.indicator.address != 0x3C && b.indicator.address != 0x3D)))
      return false;
  return true;
}

constexpr bool addressesUnique() {
  for (uint8_t i = 0; i < NUM_BUTTONS; i++)
    for (uint8_t j = i + 1; j < NUM_BUTTONS; j++) {
      const Indicator& a = BUTTONS[i].indicator;
      const Indicator& b = BUTTONS[j].indicator;
      if (a.kind == IND_KIND_SCREEN && b.kind == IND_KIND_SCREEN && a.bus == b.bus && a.address == b.address)
        return false;
    }
  return true;
}

constexpr uint8_t NUM_SCREENS = countScreens();
// Size of the screens[] array and the round-robin modulus: never 0, so a build with no screens
// (where all of this code is unreachable) neither declares a zero-length array nor divides by 0.
constexpr uint8_t SCREEN_SLOTS = NUM_SCREENS > 0 ? NUM_SCREENS : 1;
static_assert(screensValid(), "IND_SCREEN(bus, address): bus must be 0 or 1, address 0x3C or 0x3D");
static_assert(countScreens(0) <= 2 && countScreens(1) <= 2, "At most two screens per I2C bus");
static_assert(addressesUnique(), "Two screens share the same bus and address");

constexpr uint32_t kFrameMs = 20;  // at most one redraw per panel every 20 ms

struct Screen {
  uint8_t button = 0;
  U8G2* panel = nullptr;
  volco::ScreenSlot slot{SCREEN_TRANSITION_MS};
  volco::Canvas sent;          // the frame last pushed to the panel: unchanged frames cost no I2C
  uint32_t lastRender = 0;
};

Screen screens[SCREEN_SLOTS];
int8_t screenOf[NUM_BUTTONS];  // button → index into screens, or -1

volco::Panels panels({SCREEN_DIM_ENABLED, SCREEN_OFF_ENABLED, SCREEN_PIXEL_SHIFT_ENABLED,
                      SCREEN_WAKE_SWALLOWS_PRESS, SCREEN_DIM_AFTER_MS, SCREEN_OFF_AFTER_MS,
                      SCREEN_PIXEL_SHIFT_EVERY_MS});
const volco::ScrollTiming kScroll = {SCREEN_SCROLL_HOLD_MS, SCREEN_SCROLL_END_HOLD_MS, SCREEN_SCROLL_SPEED_PX_S};
volco::Power lastPower = volco::POWER_NORMAL;
uint8_t nextPanel = 0;

// Scratch frames, static to keep ~3 KB off the loop task's stack.
volco::Canvas frameA, frameB, frameOut;

void render(Screen& s, uint32_t now) {
  if (s.slot.inTransition(now)) {
    volco::renderScreen(frameA, s.slot.previous(), 0, kScroll);
    volco::renderScreen(frameB, s.slot.current(), 0, kScroll);
    volco::composeSlide(frameA, frameB, s.slot.progress(now), s.slot.transition() != volco::TRANSITION_SLIDE_RIGHT, frameOut);
  } else {
    volco::renderScreen(frameOut, s.slot.current(), s.slot.scrollSince(now), kScroll);
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

void screens_init(uint32_t now) {
  for (uint8_t b = 0; b < NUM_BUTTONS; b++) screenOf[b] = -1;
  if (NUM_SCREENS == 0) return;  // no screens: never touch I2C (GPIO 21 may be an LED)

  bool bus0 = false, bus1 = false;
  uint8_t n = 0;
  for (uint8_t b = 0; b < NUM_BUTTONS; b++) {
    const Indicator& ind = BUTTONS[b].indicator;
    if (ind.kind != IND_KIND_SCREEN) continue;
    Screen& s = screens[n];
    s.button = b;
    screenOf[b] = (int8_t)n;
    if (ind.bus == 0) {
      if (!bus0) { Wire.setPins(I2C0_SDA, I2C0_SCL); bus0 = true; }
      s.panel = new U8G2_SSD1306_128X64_NONAME_F_HW_I2C(U8G2_R0, U8X8_PIN_NONE);
    } else {
      if (!bus1) { Wire1.setPins(I2C1_SDA, I2C1_SCL); bus1 = true; }
      s.panel = new U8G2_SSD1306_128X64_NONAME_F_2ND_HW_I2C(U8G2_R0, U8X8_PIN_NONE);
    }
    s.panel->setI2CAddress(ind.address << 1);  // U8g2 takes the 8-bit form
    s.panel->setBusClock(I2C_CLOCK_HZ);
    s.panel->begin();
    s.panel->setContrast(SCREEN_BRIGHTNESS);
    s.slot.show(volco::notConnectedScreen(), volco::TRANSITION_NONE, now, false);
    memset(s.sent.buf, 0xFF, sizeof s.sent.buf);  // force the first push
    n++;
  }
  panels.activity(now);
  DEBUG_LOG("[DBG] Screens: " + String(NUM_SCREENS));
}

void screens_show(uint8_t button, uint8_t transition, const volco::ScreenContent& content, uint32_t now) {
  if (button >= NUM_BUTTONS || screenOf[button] < 0) return;
  screens[screenOf[button]].slot.show(content, transition, now, panels.power(now) == volco::POWER_OFF);
}

void screens_host_lost(uint32_t now) {
  for (uint8_t i = 0; i < NUM_SCREENS; i++)
    screens[i].slot.show(volco::notConnectedScreen(), volco::TRANSITION_NONE, now, false);
}

void screens_activity(uint32_t now) {
  if (NUM_SCREENS > 0) panels.activity(now);
}

bool screens_filter_button(uint8_t button, bool pressed, uint32_t now) {
  if (NUM_SCREENS == 0) return true;
  return panels.filterButton(button, pressed, now);
}

void screens_tick(uint32_t now) {
  if (NUM_SCREENS == 0) return;

  const volco::Power power = panels.power(now);
  if (power != lastPower) {
    for (uint8_t i = 0; i < NUM_SCREENS; i++) {
      screens[i].panel->setPowerSave(power == volco::POWER_OFF ? 1 : 0);
      if (power != volco::POWER_OFF)
        screens[i].panel->setContrast(power == volco::POWER_DIM ? SCREEN_DIM_BRIGHTNESS : SCREEN_BRIGHTNESS);
    }
    lastPower = power;
  }
  if (power == volco::POWER_OFF) return;

  // Redraw at most one panel per pass (~25-30 ms of I2C at 400 kHz), round-robin, so one pass
  // never blocks the loop for longer than one panel's redraw (spec §4.6).
  for (uint8_t k = 0; k < NUM_SCREENS; k++) {
    uint8_t i = (uint8_t)((nextPanel + k) % SCREEN_SLOTS);
    Screen& s = screens[i];
    if (now - s.lastRender < kFrameMs) continue;
    s.lastRender = now;
    nextPanel = (uint8_t)((i + 1) % SCREEN_SLOTS);
    render(s, now);
    break;
  }
}

#include "volco_pin_helpers.h"

#include <math.h>

namespace volco {
namespace {
constexpr uint64_t bit(uint32_t p) { return p < 64 ? (1ull << p) : 0; }
}  // namespace

PinUse pinUse(const RuntimeConfig& rc) {
  PinUse u = {0, 0, 0, 0};
  for (uint8_t i = 0; i < rc.sliderCount && i < kMaxSliders; i++) u.sliders |= bit(rc.sliderPins[i]);
  for (uint8_t i = 0; i < rc.buttonCount && i < kMaxButtons; i++) {
    const RuntimeButton& b = rc.buttons[i];
    u.buttons |= bit(b.pin);
    if (b.kind == KIND_LED) u.leds |= bit(b.pinA);
    if (b.kind == KIND_LED_PAIR) u.leds |= bit(b.pinA) | bit(b.pinB);
  }
  for (uint8_t b = 0; b < kI2cBuses; b++)
    if (rc.i2c[b].used) u.i2c |= bit(rc.i2c[b].sda) | bit(rc.i2c[b].scl);
  return u;
}

uint64_t watchPins(const ChipInfo& chip, const PinUse& use, WatchRole role, bool wifiOn) {
  if (role != WATCH_ANALOG && role != WATCH_BUTTON) return 0;
  const uint64_t taken = use.all();
  const uint64_t outputsAndBuses = use.leds | use.i2c;
  uint64_t m = 0;
  for (uint32_t p = 0; p < 64; p++) {
    if (!chip.usable(p)) continue;
    const bool free = !(taken & bit(p));
    if (role == WATCH_ANALOG) {
      if (!chip.adcCapable(p) || (wifiOn && chip.isAdc2(p))) continue;
      if (free || (use.sliders & bit(p) & ~outputsAndBuses)) m |= bit(p);
    } else if ((free && !chip.has(chip.inputOnly, p)) || (use.buttons & bit(p) & ~outputsAndBuses)) {
      m |= bit(p);
    }
  }
  return m;
}

void PinWatch::start(WatchRole role, uint64_t pins, uint32_t now) {
  if (role != WATCH_ANALOG && role != WATCH_BUTTON) {
    stop();
    return;
  }
  if (role != role_) {
    for (uint8_t i = 0; i < kPins; i++) {
      analog_[i] = Analog{};
      digital_[i] = Digital{};
    }
  }
  role_ = role;
  pins_ = pins;
  startedAt_ = now;
}

void PinWatch::stop() {
  role_ = WATCH_STOP;
  pins_ = 0;
}

bool PinWatch::watching(uint8_t pin, WatchRole role) const {
  return role_ == role && pin < kPins && (pins_ & bit(pin));
}

bool PinWatch::analog(uint8_t pin, uint16_t raw, uint32_t now) {
  if (!watching(pin, WATCH_ANALOG)) return false;
  Analog& a = analog_[pin];
  if (a.samples == 0) {
    a.level = raw;
    a.jitter = 0;
  } else {
    const float step = raw > a.prev ? raw - a.prev : a.prev - raw;
    a.level += (raw - a.level) * 0.25f;
    a.jitter += (step - a.jitter) * 0.25f;
  }
  a.prev = raw;
  if (a.samples < 0xFFFF) a.samples++;
  if (!a.settled) {
    if (a.samples >= kAnalogSettle) {
      a.settled = true;
      a.baseline = a.level;
    }
    return false;
  }
  a.baseline += (a.level - a.baseline) / kAnalogBaselineFollow;
  if (fabsf(a.level - a.baseline) < kAnalogMove || a.jitter >= kAnalogMaxJitter) return false;
  if (a.reported && now - a.lastReport < kWatchReportGapMs) return false;
  a.reported = true;
  a.lastReport = now;
  a.baseline = a.level;
  return true;
}

bool PinWatch::digital(uint8_t pin, bool low, uint32_t now) {
  if (!watching(pin, WATCH_BUTTON)) return false;
  Digital& d = digital_[pin];
  if (!d.seen || d.low != low) {
    d.seen = true;
    d.low = low;
    d.since = now;
  }
  const uint32_t held = now - d.since;
  if (!low) {
    if (held >= (d.reported ? kRearmMs : kArmMs)) d.armed = true;
    return false;
  }
  if (!d.armed || held < kPressMs) return false;
  if (d.reported && now - d.lastReport < kWatchReportGapMs) return false;
  d.armed = false;
  d.reported = true;
  d.lastReport = now;
  return true;
}

ScanPlan planScan(const ChipInfo& chip, const RuntimeConfig& rc, uint8_t sda, uint8_t scl) {
  const ScanPlan refused = {SCAN_REFUSED, 0};
  if (sda == scl || !chip.outputCapable(sda) || !chip.outputCapable(scl)) return refused;
  for (uint8_t b = 0; b < kI2cBuses; b++) {
    const RuntimeBus& bus = rc.i2c[b];
    if (!bus.used) continue;
    if (bus.sda == sda && bus.scl == scl) return {b < 2 ? SCAN_HW_BUS : SCAN_SOFT_BUS, b};
  }
  for (uint8_t b = 0; b < kI2cBuses; b++) {
    const RuntimeBus& bus = rc.i2c[b];
    if (bus.used && (bus.sda == sda || bus.sda == scl || bus.scl == sda || bus.scl == scl)) return refused;
  }
  return {SCAN_TEMPORARY, 0};
}

bool driveAllowed(const ChipInfo& chip, const PinUse& use, uint8_t pin) {
  if (!chip.outputCapable(pin)) return false;
  return (use.leds & bit(pin)) || !(use.all() & bit(pin));
}

}  // namespace volco

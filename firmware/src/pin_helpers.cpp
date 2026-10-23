#include "pin_helpers.h"

#include <Arduino.h>
#include <driver/gpio.h>
#include <esp32-hal-periman.h>

#include "debug_log.h"
#include "screens.h"
#include "volco_chip.h"
#include "volco_pin_helpers.h"

namespace {

const volco::RuntimeConfig* config = nullptr;
volco::PinUse use = {0, 0, 0, 0};
bool wifiOn = false;  // ADC2 belongs to the radio while Wi-Fi is enabled

constexpr uint64_t pinBit(uint8_t p) { return p < 64 ? (1ull << p) : 0; }
bool has(uint64_t mask, uint8_t p) { return (mask & pinBit(p)) != 0; }
const volco::ChipInfo& chip() { return volco::esp32Chip(); }

// A pin back in its default state; the peripheral manager forgets it first (an ADC channel the
// watch attached is detached, the ADC unit stays while a slider uses it).
void releasePin(uint8_t p) {
  perimanClearPinBus(p);
  gpio_reset_pin(static_cast<gpio_num_t>(p));
}

// ============================================================
// Detect: the pin watch. Configured pins are only read; free pins get the role's mode and are
// released when the watch ends.
// ============================================================
volco::PinWatch watch;
uint64_t touched = 0;  // free pins the watch set up
uint32_t lastSample = 0;

void setWatchMode(uint8_t p, volco::WatchRole role) {
  if (role == volco::WATCH_BUTTON) pinMode(p, INPUT_PULLUP);  // a button watch has no free input-only pin
  else pinMode(p, INPUT);  // the first analogRead attaches the ADC, with the pulls off
}

void stopWatch();  // below the drives: a driven pin is released when its drive ends

// ============================================================
// PinDrive: up to four pins at once, each until its end time.
// ============================================================
struct Drive {
  bool on;
  uint8_t pin, level;
  uint32_t until;
};
constexpr uint8_t kDrives = 4;
Drive drives[kDrives] = {};
uint64_t driving = 0;
uint64_t ledsToRestore = 0;  // LED pins a scan borrowed: re-shown on the next tick

void stopWatch() {
  watch.stop();
  for (uint8_t p = 0; p < 64; p++)
    if (has(touched & ~driving, p)) releasePin(p);
  touched = 0;
}

// A pin a scan or a drive borrowed, back as the config (or the helpers) use it.
void restorePin(uint8_t p) {
  for (const Drive& d : drives)
    if (d.on && d.pin == p) {
      pinMode(p, OUTPUT);
      digitalWrite(p, d.level);
      return;
    }
  if (has(use.leds, p)) {
    pinMode(p, OUTPUT);
    ledsToRestore |= pinBit(p);
  } else if (has(use.sliders, p)) {
    pinMode(p, INPUT);
  } else if (has(use.buttons, p)) {
    pinMode(p, INPUT_PULLUP);
  } else if (has(touched, p) && watch.active()) {
    setWatchMode(p, watch.role());
  } else {
    releasePin(p);
  }
}

// ============================================================
// Find screens off the hardware buses: a bit-banged scan at 100 kHz, both lines open-drain with
// the pull-ups on, reading the ACK on the 9th clock. No clock stretching: it never waits on a line,
// so a shorted bus can't hang it (~12 ms for the whole range).
// ============================================================
class ProbeI2c {
 public:
  ProbeI2c(uint8_t sda, uint8_t scl) : sda_(static_cast<gpio_num_t>(sda)), scl_(static_cast<gpio_num_t>(scl)) {
    perimanClearPinBus(sda);
    perimanClearPinBus(scl);
    gpio_set_level(sda_, 1);
    gpio_set_level(scl_, 1);
    gpio_config_t c = {};
    c.pin_bit_mask = pinBit(sda) | pinBit(scl);
    c.mode = GPIO_MODE_INPUT_OUTPUT_OD;
    c.pull_up_en = GPIO_PULLUP_ENABLE;
    gpio_config(&c);
    delayMicroseconds(kHalfUs);
  }

  // Both lines high, after up to 9 clocks to free a device that holds SDA. False: a line is stuck.
  bool idle() {
    if (!gpio_get_level(scl_)) return false;
    if (gpio_get_level(sda_)) return true;
    for (uint8_t i = 0; i < 9 && !gpio_get_level(sda_); i++) {
      gpio_set_level(scl_, 0);
      delayMicroseconds(kHalfUs);
      gpio_set_level(scl_, 1);
      delayMicroseconds(kHalfUs);
    }
    if (!gpio_get_level(sda_)) return false;
    stop();  // only after recovery clocks: ends whatever the device thought was running
    return gpio_get_level(sda_) && gpio_get_level(scl_);
  }

  // A write to the 7-bit address; true when it ACKed.
  bool probe(uint8_t address) {
    gpio_set_level(sda_, 0);  // START
    delayMicroseconds(kHalfUs);
    gpio_set_level(scl_, 0);
    delayMicroseconds(kHoldUs);
    const uint8_t b = static_cast<uint8_t>(address << 1);
    for (uint8_t m = 0x80; m; m >>= 1) clock(b & m);
    const bool ack = !clock(true);  // SDA released: the device pulls it low
    stop();
    return ack;
  }

 private:
  static constexpr uint32_t kHalfUs = 5;  // 100 kHz
  static constexpr uint32_t kHoldUs = 1;  // data hold after SCL falls (>= 300 ns, as SoftI2c)

  bool clock(bool high) {  // SCL is low on entry and on return; the SDA level seen while SCL was high
    gpio_set_level(sda_, high);
    delayMicroseconds(kHalfUs);
    gpio_set_level(scl_, 1);
    delayMicroseconds(kHalfUs);
    const bool level = gpio_get_level(sda_);
    gpio_set_level(scl_, 0);
    delayMicroseconds(kHoldUs);
    return level;
  }

  void stop() {
    gpio_set_level(sda_, 0);
    delayMicroseconds(kHalfUs);
    gpio_set_level(scl_, 1);
    delayMicroseconds(kHalfUs);
    gpio_set_level(sda_, 1);
    delayMicroseconds(kHalfUs);
  }

  gpio_num_t sda_, scl_;
};

uint8_t softScan(uint8_t sda, uint8_t scl, uint8_t* addresses) {
  ProbeI2c bus(sda, scl);
  uint8_t count = 0;
  for (uint8_t a = 0x08; a <= 0x77 && count < volco::kMaxScanAddresses; a++) {
    if (!bus.idle()) break;  // a line held low: what answered so far
    if (bus.probe(a)) addresses[count++] = a;
  }
  return count;
}

}  // namespace

void pin_helpers_begin(const volco::RuntimeConfig& rc) {
  config = &rc;
  use = volco::pinUse(rc);
  wifiOn = rc.udp.enabled;
}

void pin_helpers_watch(uint8_t role, uint32_t now) {
  if (!config) return;
  if (role != volco::WATCH_ANALOG && role != volco::WATCH_BUTTON) {
    if (watch.active()) DEBUG_LOG("[DBG] Pin watch stopped");
    stopWatch();
    return;
  }
  const volco::WatchRole r = static_cast<volco::WatchRole>(role);
  if (watch.active() && watch.role() != r) stopWatch();  // another role starts clean
  const bool renewing = watch.active();
  const uint64_t pins = volco::watchPins(chip(), use, r, wifiOn);
  watch.start(r, pins, now);
  if (renewing) return;
  const uint64_t free = pins & ~use.all();
  for (uint8_t p = 0; p < 64; p++)
    if (has(free, p) && !has(driving, p)) setWatchMode(p, r);
  touched = free;
  lastSample = now;
  DEBUG_LOG("[DBG] Pin watch: " + String(r == volco::WATCH_ANALOG ? "analog" : "button"));
}

uint8_t pin_helpers_scan(uint8_t sda, uint8_t scl, uint8_t* addresses) {
  if (!config) return 0;
  const volco::ScanPlan plan = volco::planScan(chip(), *config, sda, scl);
  uint8_t count = 0;
  switch (plan.path) {
    case volco::SCAN_HW_BUS:
      screens_scan_bus(plan.bus, addresses, count);
      break;
    case volco::SCAN_SOFT_BUS:
      count = softScan(sda, scl, addresses);
      screens_restart_bus(plan.bus);
      break;
    case volco::SCAN_TEMPORARY:
      count = softScan(sda, scl, addresses);
      restorePin(sda);
      restorePin(scl);
      break;
    default:
      break;  // refused: count 0
  }
  DEBUG_LOG("[DBG] I2C scan " + String(sda) + "/" + String(scl) + ": " + String(count) + " found");
  return count;
}

void pin_helpers_drive(uint8_t pin, uint8_t level, uint16_t durationMs, uint32_t now) {
  if (!config || !volco::driveAllowed(chip(), use, pin)) return;
  Drive* slot = nullptr;
  for (Drive& d : drives)
    if (d.on && d.pin == pin) slot = &d;  // a repeat replaces the level and the end time
  for (Drive& d : drives)
    if (!slot && !d.on) slot = &d;
  if (!slot) return;  // four already running
  *slot = Drive{true, pin, static_cast<uint8_t>(level ? 1 : 0), now + durationMs};
  driving |= pinBit(pin);
  pinMode(pin, OUTPUT);
  digitalWrite(pin, slot->level);
}

bool pin_helpers_driving(uint8_t pin) { return has(driving, pin); }

void pin_helpers_host_lost() {
  if (watch.active()) DEBUG_LOG("[DBG] Pin watch stopped: PC silent");
  stopWatch();
}

void pin_helpers_tick(uint32_t now, void (*report)(uint8_t pin, uint32_t now), void (*restored)(uint8_t pin)) {
  // Drives that ended: an LED pin gets the PC's last value back, any other pin is released.
  for (Drive& d : drives) {
    if (!d.on || static_cast<int32_t>(now - d.until) < 0) continue;
    d.on = false;
    driving &= ~pinBit(d.pin);
    if (has(use.leds, d.pin)) restored(d.pin);
    else restorePin(d.pin);
  }
  if (ledsToRestore) {
    for (uint8_t p = 0; p < 64; p++)
      if (has(ledsToRestore, p)) restored(p);
    ledsToRestore = 0;
  }

  if (!watch.active()) return;
  if (watch.expired(now)) {
    DEBUG_LOG("[DBG] Pin watch expired");
    stopWatch();
    return;
  }
  if (now - lastSample < volco::kWatchSampleMs) return;
  lastSample = now;
  const uint64_t pins = watch.pins() & ~driving;  // a driven pin reads its own level
  const bool analog = watch.role() == volco::WATCH_ANALOG;
  for (uint8_t p = 0; p < volco::PinWatch::kPins; p++) {
    if (!has(pins, p)) continue;
    const bool hit = analog ? watch.analog(p, static_cast<uint16_t>(analogRead(p)), now)
                            : watch.digital(p, digitalRead(p) == LOW, now);
    if (hit) report(p, now);
  }
}

#pragma once
// The rules behind the wiring helpers: Detect (a pin watch that says which pin moved or was
// pressed), Find screens (how an I2C scan reaches a pin pair) and PinDrive (which pins may be
// driven). Pure: no Arduino, time is passed in, the chip and the config are arguments. src/
// samples the pins, runs the scan and drives the pins; this decides what is worth reporting.
// The thresholds are first guesses, to be checked on hardware.
#include <stdint.h>

#include "volco_chip.h"
#include "volco_protocol.h"  // WatchRole
#include "volco_runtime.h"

namespace volco {

// The pins the running config uses, one bit per GPIO.
struct PinUse {
  uint64_t sliders, buttons, leds, i2c;
  uint64_t all() const { return sliders | buttons | leds | i2c; }  // "free" = not in all()
};

// i2c holds the SDA and SCL of the buses a screen uses only; leds holds a pair's A and B.
PinUse pinUse(const RuntimeConfig& rc);

// The pins a watch samples. Analog: free ADC pins and the configured slider pins, never an ADC2
// pin while Wi-Fi is on (the radio owns it). Button: free pins and the configured button pins,
// but no free input-only pin (no pull-up, so it floats). Never an LED or a used I2C pin, never
// the other kind of input. 0 for WATCH_STOP.
uint64_t watchPins(const ChipInfo& chip, const PinUse& use, WatchRole role, bool wifiOn);

constexpr uint32_t kWatchSampleMs = 10;       // how often src/ samples the watched pins
constexpr uint32_t kWatchReportGapMs = 250;   // at most one report per pin in this time
constexpr uint32_t kWatchTimeoutMs = 30000;   // the watch ends this long after the last start
// Analog: the level (EMA, weight 1/4) must move kAnalogMove from the baseline while the jitter
// (EMA of the step between raw readings) is under kAnalogMaxJitter. A floating pin is noisy, a
// moved potentiometer is not. The baseline follows the level by 1/kAnalogBaselineFollow of the
// gap each sample, so a clean pin drifting slowly never gets kAnalogMove away from it.
constexpr uint16_t kAnalogMove = 300, kAnalogMaxJitter = 80;
constexpr uint8_t kAnalogSettle = 20;           // samples before the baseline is taken
constexpr uint8_t kAnalogBaselineFollow = 32;
// Button: low for kPressMs after being high for kArmMs (the first press), or kRearmMs after a
// reported press was released. A pin that is low or chattering is never armed.
constexpr uint32_t kPressMs = 30, kArmMs = 500, kRearmMs = 100;

class PinWatch {
 public:
  static constexpr uint8_t kPins = 40;  // GPIO 0-39

  // WATCH_STOP stops. The same role renews the 30 s and keeps each pin's state; another role
  // starts clean.
  void start(WatchRole role, uint64_t pins, uint32_t now);
  void stop();
  bool active() const { return role_ != WATCH_STOP; }
  WatchRole role() const { return role_; }
  uint64_t pins() const { return pins_; }
  bool expired(uint32_t now) const { return active() && now - startedAt_ >= kWatchTimeoutMs; }

  // One raw ADC reading / one digital reading (true = low, pressed). True: report this pin now.
  // False too when the watch is not active, the pin is not watched or the role differs.
  bool analog(uint8_t pin, uint16_t raw, uint32_t now);
  bool digital(uint8_t pin, bool low, uint32_t now);

 private:
  struct Analog {
    float level, jitter;
    uint16_t prev;
    uint16_t samples;
    float baseline;
    bool settled;
    uint32_t lastReport;
    bool reported;
  };
  struct Digital {
    bool seen, low, armed, reported;
    uint32_t since, lastReport;
  };
  bool watching(uint8_t pin, WatchRole role) const;

  WatchRole role_ = WATCH_STOP;
  uint64_t pins_ = 0;
  uint32_t startedAt_ = 0;
  Analog analog_[kPins] = {};
  Digital digital_[kPins] = {};
};

// How an I2C scan of (sda, scl) is run: through the config's own bus, or on those pins alone.
enum ScanPath : uint8_t { SCAN_REFUSED, SCAN_HW_BUS, SCAN_SOFT_BUS, SCAN_TEMPORARY };
struct ScanPlan {
  ScanPath path;
  uint8_t bus;  // 0-3 for SCAN_HW_BUS (0, 1) and SCAN_SOFT_BUS (2, 3)
};
// Refused: a pin that is not usable or is input only, SDA = SCL, or a pair that shares a pin with
// a used bus without being that bus (in its order). A pair that is a used bus's maps to it.
ScanPlan planScan(const ChipInfo& chip, const RuntimeConfig& rc, uint8_t sda, uint8_t scl);

// PinDrive: an output-capable pin that is free or a configured LED pin.
bool driveAllowed(const ChipInfo& chip, const PinUse& use, uint8_t pin);

}  // namespace volco

#pragma once
// Slider smoothing and button debounce (docs/design/arduino-avr.md §6.2), shared by every chip's
// firmware. Pure C++: readings and time are passed in. Built natively for tests.
#include <stdint.h>

namespace volco {

// One slider: an EMA in fixed point (the value × 256, the weight in percent, so no floats on the
// 8-bit chips), then a deadband against the value last reported to the PC.
class SliderFilter {
 public:
  void seed(uint16_t raw);  // boot: the value and the last reported value are `raw`
  // One reading. True when the filtered value moved more than `deadband` from the last reported
  // one; that value is then the last reported one (send value()).
  bool update(uint16_t raw, uint8_t weightPct, uint16_t deadband);
  uint16_t value() const { return (uint16_t)(q8_ >> 8); }
  // Hello sends every slider's position (PR #68): the deadband then measures from it.
  uint16_t report() {
    reported_ = value();
    return reported_;
  }

 private:
  uint32_t q8_ = 0;  // ≤ 4095 × 256: (raw << 8) × 100 fits 32 bits
  uint16_t reported_ = 0;
};

// One button on an INPUT_PULLUP pin (LOW = pressed), debounced on timestamps, so a press is seen
// even when a long loop pass delayed this one.
class ButtonDebouncer {
 public:
  void reset() {  // released, nothing pending
    raw_ = reported_ = true;
    changed_ = 0;
  }
  // One read of the pin (true = HIGH). True when the debounced state changed; pressed() holds it.
  bool update(bool high, uint32_t now, uint32_t debounceMs);
  bool pressed() const { return !reported_; }

 private:
  bool raw_ = true, reported_ = true;
  uint32_t changed_ = 0;
};

}  // namespace volco

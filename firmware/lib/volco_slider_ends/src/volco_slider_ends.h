#pragma once
// One slider's learned travel ends, and the mapping of its readings onto the full 0-4095 range.
// A board may keep its wipers away from the ADC's rails on purpose (a resistor at each end of the
// pot), so a slider's stops can read e.g. 170 and 2550. The ends are learned from the smoothed
// readings: moving a slider end to end once calibrates it. Pure: no Arduino, no storage.
#include <stdint.h>

namespace volco {

class SliderEnds {
 public:
  static constexpr uint16_t kFull = 4095;
  static constexpr uint16_t kMinSpan = 1000;   // learned once hi - lo reaches this; raw until then
  static constexpr float kEndBand = 0.015f;    // this much of the span at each stop maps to exactly 0 / 4095
  static constexpr uint16_t kSaveStep = 8;     // an end must move this far past the stored one to be saved again

  SliderEnds() { reset(); }

  void reset() {
    lo_ = kFull;
    hi_ = 0;
    savedLo_ = kFull;
    savedHi_ = 0;
  }

  // Ends read back from storage. Ignored unless they form a learned span.
  void restore(uint16_t lo, uint16_t hi) {
    if (hi > kFull || lo > hi || hi - lo < kMinSpan) return;
    lo_ = savedLo_ = lo;
    hi_ = savedHi_ = hi;
  }

  // Widens the ends to include a smoothed reading.
  void learn(uint16_t v) {
    if (v < lo_) lo_ = v;
    if (v > hi_) hi_ = v;
  }

  bool learned() const { return hi_ >= lo_ && hi_ - lo_ >= kMinSpan; }

  // The reading on the full range: raw until learned, then stretched between the ends.
  uint16_t map(uint16_t v) const;

  // A learned end moved past the stored one by more than kSaveStep.
  bool needsSave() const {
    return learned() && (lo_ + kSaveStep < savedLo_ || hi_ > savedHi_ + kSaveStep);
  }
  void markSaved() {
    savedLo_ = lo_;
    savedHi_ = hi_;
  }

  uint16_t lo() const { return lo_; }
  uint16_t hi() const { return hi_; }

 private:
  uint16_t lo_, hi_;
  uint16_t savedLo_, savedHi_;
};

}  // namespace volco

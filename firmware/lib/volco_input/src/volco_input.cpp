#include "volco_input.h"

namespace volco {

void SliderFilter::seed(uint16_t raw) {
  q8_ = (uint32_t)raw << 8;
  reported_ = raw;
}

bool SliderFilter::update(uint16_t raw, uint8_t weightPct, uint16_t deadband) {
  const uint32_t w = weightPct;
  q8_ = (((uint32_t)raw << 8) * w + q8_ * (100u - w) + 50u) / 100u;
  const uint16_t v = value();
  const uint16_t moved = v > reported_ ? (uint16_t)(v - reported_) : (uint16_t)(reported_ - v);
  if (moved <= deadband) return false;
  reported_ = v;
  return true;
}

bool ButtonDebouncer::update(bool high, uint32_t now, uint32_t debounceMs) {
  if (high != raw_) {
    raw_ = high;
    changed_ = now;
  }
  if (now - changed_ >= debounceMs && raw_ != reported_) {
    reported_ = raw_;
    return true;
  }
  return false;
}

}  // namespace volco

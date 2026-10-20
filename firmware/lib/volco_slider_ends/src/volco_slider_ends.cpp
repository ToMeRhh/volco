#include "volco_slider_ends.h"

namespace volco {

uint16_t SliderEnds::map(uint16_t v) const {
  if (!learned()) return v;
  float p = (float)((int32_t)v - lo_) / (float)(hi_ - lo_);
  p = (p - kEndBand) / (1.0f - 2 * kEndBand);
  if (p <= 0) return 0;
  if (p >= 1) return kFull;
  return (uint16_t)(p * kFull + 0.5f);
}

}  // namespace volco

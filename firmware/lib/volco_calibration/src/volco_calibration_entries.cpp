#include "volco_calibration_entries.h"

namespace volco {

size_t encodeEndsEntries(const uint8_t* pins, const SliderCalibration* cals, uint8_t count, uint8_t* out) {
  for (uint8_t i = 0; i < count; i++) {
    uint8_t* e = out + i * kEndsEntrySize;
    const bool learned = cals[i].learned();
    const uint16_t lo = learned ? cals[i].lo() : 1, hi = learned ? cals[i].hi() : 0;
    e[0] = pins[i];
    e[1] = cals[i].autoCalibrate() ? 1 : 0;
    e[2] = lo >> 8;
    e[3] = lo & 0xFF;
    e[4] = hi >> 8;
    e[5] = hi & 0xFF;
  }
  return count * kEndsEntrySize;
}

void restoreEndsEntries(const uint8_t* data, size_t len, const uint8_t* pins, SliderCalibration* cals,
                        uint8_t count) {
  for (uint8_t i = 0; i < count; i++)
    for (size_t off = 0; off + kEndsEntrySize <= len; off += kEndsEntrySize) {
      const uint8_t* e = data + off;
      if (e[0] != pins[i]) continue;
      if ((e[1] != 0) == cals[i].autoCalibrate())
        cals[i].restore((uint16_t)(e[2] << 8 | e[3]), (uint16_t)(e[4] << 8 | e[5]));
      break;  // a pin has one entry
    }
}

}  // namespace volco

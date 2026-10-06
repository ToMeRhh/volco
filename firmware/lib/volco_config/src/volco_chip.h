#pragma once
// What each GPIO of a chip family can do (spec §6.4): facts about the chip, not user preferences.
#include <stdint.h>

namespace volco {

struct ChipInfo {
  uint64_t exists, reserved, inputOnly, adc1, adc2;  // bit n = GPIO n

  static bool has(uint64_t mask, uint32_t pin) { return pin < 64 && ((mask >> pin) & 1u); }
  bool usable(uint32_t pin) const { return has(exists, pin) && !has(reserved, pin); }
  bool outputCapable(uint32_t pin) const { return usable(pin) && !has(inputOnly, pin); }
  bool adcCapable(uint32_t pin) const { return usable(pin) && (has(adc1, pin) || has(adc2, pin)); }
  bool isAdc2(uint32_t pin) const { return has(adc2, pin); }
};

// Classic ESP32: exists 0-19, 21-23, 25-27, 32-39; reserved 1, 3 (USB serial) and 6-11 (flash);
// input-only 34-39; ADC1 32-39; ADC2 0, 2, 4, 12-15, 25-27.
const ChipInfo& esp32Chip();

}  // namespace volco

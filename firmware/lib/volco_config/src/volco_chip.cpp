#include "volco_chip.h"

namespace volco {
namespace {
constexpr uint64_t bit(int p) { return 1ull << p; }
constexpr uint64_t span(int a, int b) {
  uint64_t m = 0;
  for (int p = a; p <= b; p++) m |= bit(p);
  return m;
}
constexpr ChipInfo kEsp32{
    span(0, 19) | span(21, 23) | span(25, 27) | span(32, 39),  // exists
    bit(1) | bit(3) | span(6, 11),                              // reserved
    span(34, 39),                                               // input-only
    span(32, 39),                                               // ADC1
    bit(0) | bit(2) | bit(4) | span(12, 15) | span(25, 27),     // ADC2
};
}  // namespace

const ChipInfo& esp32Chip() { return kEsp32; }

}  // namespace volco

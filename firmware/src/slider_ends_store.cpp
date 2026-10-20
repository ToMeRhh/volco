#include "slider_ends_store.h"

#include <Preferences.h>

namespace {
constexpr size_t kEntry = 5;  // pin, lo (big-endian u16), hi (big-endian u16)
uint8_t buf[volco::kMaxSliders * kEntry];
}  // namespace

void slider_ends_load(const volco::RuntimeConfig& rc, volco::SliderEnds* ends) {
  Preferences prefs;
  if (!prefs.begin("volco_ends", true)) return;  // nothing stored yet
  const size_t len = prefs.isKey("ends") ? prefs.getBytes("ends", buf, sizeof buf) : 0;
  prefs.end();
  for (uint8_t i = 0; i < rc.sliderCount && (i + 1) * kEntry <= len; i++) {
    const uint8_t* e = buf + i * kEntry;
    if (e[0] != rc.sliderPins[i]) continue;
    ends[i].restore((uint16_t)(e[1] << 8 | e[2]), (uint16_t)(e[3] << 8 | e[4]));
  }
}

bool slider_ends_save(const volco::RuntimeConfig& rc, const volco::SliderEnds* ends) {
  for (uint8_t i = 0; i < rc.sliderCount; i++) {
    uint8_t* e = buf + i * kEntry;
    // A slider not learned yet is stored as hi < lo, which restore() ignores.
    const uint16_t lo = ends[i].learned() ? ends[i].lo() : 1, hi = ends[i].learned() ? ends[i].hi() : 0;
    e[0] = rc.sliderPins[i];
    e[1] = lo >> 8;
    e[2] = lo & 0xFF;
    e[3] = hi >> 8;
    e[4] = hi & 0xFF;
  }
  Preferences prefs;
  if (!prefs.begin("volco_ends", false)) return false;
  const size_t len = rc.sliderCount * kEntry;
  const size_t written = prefs.putBytes("ends", buf, len);
  prefs.end();
  return written == len;
}

void slider_ends_clear() {
  Preferences prefs;
  if (!prefs.begin("volco_ends", false)) return;
  prefs.clear();
  prefs.end();
}

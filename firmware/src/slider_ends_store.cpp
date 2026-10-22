#include "slider_ends_store.h"

#include <Preferences.h>
#include <string.h>

#include "volco_calibration_entries.h"

namespace {
uint8_t stored[volco::kMaxSliders * volco::kEndsEntrySize];
uint8_t current[volco::kMaxSliders * volco::kEndsEntrySize];
}  // namespace

void slider_ends_load(const volco::RuntimeConfig& rc, volco::SliderCalibration* cals) {
  size_t len = 0;
  Preferences prefs;
  if (prefs.begin("volco_ends", true)) {  // fails when nothing was ever stored
    if (prefs.isKey("ends")) len = prefs.getBytes("ends", stored, sizeof stored);
    prefs.end();
  }
  volco::restoreEndsEntries(stored, len, rc.sliderPins, cals, rc.sliderCount);
  if (len == 0) return;  // nothing to forget
  const size_t n = volco::encodeEndsEntries(rc.sliderPins, cals, rc.sliderCount, current);
  if (n != len || memcmp(stored, current, n) != 0) slider_ends_save(rc, cals);
}

bool slider_ends_save(const volco::RuntimeConfig& rc, const volco::SliderCalibration* cals) {
  const size_t len = volco::encodeEndsEntries(rc.sliderPins, cals, rc.sliderCount, current);
  Preferences prefs;
  if (!prefs.begin("volco_ends", false)) return false;
  // No sliders: remove the key (putBytes of zero bytes is not a write).
  const bool ok = len == 0 ? (!prefs.isKey("ends") || prefs.remove("ends")) : prefs.putBytes("ends", current, len) == len;
  prefs.end();
  return ok;
}

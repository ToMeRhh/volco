#pragma once
// The stored form of the sliders' learned ends (docs/design/slider-calibration.md): one 6-byte
// entry per slider, {pin, auto-calibrate, low (u16 BE), high (u16 BE)}. Entries are looked up by
// pin, so removing a slider keeps the others' ends; an entry whose auto-calibrate differs from the
// slider's setting is ignored, which is how changing the setting forgets the ends. Pure: the NVS
// read and write are src/slider_ends_store.
#include <stddef.h>
#include <stdint.h>

#include "volco_calibration.h"

namespace volco {

constexpr size_t kEndsEntrySize = 6;

// Writes one entry per slider into out (count * kEndsEntrySize bytes) and returns the length. A
// slider not learned yet is stored as low > high, which restore() ignores.
size_t encodeEndsEntries(const uint8_t* pins, const SliderCalibration* cals, uint8_t count, uint8_t* out);

// Restores each slider's ends from the stored entry with its pin and its auto-calibrate setting
// (configure() each calibration first). Entries that match no slider are ignored, as is a
// trailing partial entry.
void restoreEndsEntries(const uint8_t* data, size_t len, const uint8_t* pins, SliderCalibration* cals,
                        uint8_t count);

}  // namespace volco

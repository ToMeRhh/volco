#pragma once
// The sliders' learned travel ends in NVS: namespace "volco_ends", key "ends2" (a load removes the
// experiment's old "ends"), one {pin, auto-calibrate, low, high} entry per slider (lib/volco_calibration's entry codec). Kept
// apart from the controller config ("volco"/"cfg"), so a config save never touches them.
#include <stdint.h>

#include "volco_calibration.h"
#include "volco_runtime.h"

// Restores each slider's ends from the entry with its pin and its auto-calibrate setting (configure
// the calibrations first). When that drops an entry (a slider removed, a pin or the setting
// changed), stores the current ends at once, so a forgotten calibration never comes back.
void slider_ends_load(const volco::RuntimeConfig& rc, volco::SliderCalibration* cals);

// Stores every slider's ends. True when NVS took them.
bool slider_ends_save(const volco::RuntimeConfig& rc, const volco::SliderCalibration* cals);

#pragma once
// The sliders' learned travel ends in NVS: namespace "volco_ends", key "ends", one
// {pin, lo, hi} entry per slider. Kept apart from the controller config ("volco"/"cfg").
#include <stdint.h>

#include "volco_runtime.h"
#include "volco_slider_ends.h"

// Restores each slider's ends, unless the slider's pin changed since they were stored.
void slider_ends_load(const volco::RuntimeConfig& rc, volco::SliderEnds* ends);

// Stores every slider's ends. True when NVS took them.
bool slider_ends_save(const volco::RuntimeConfig& rc, const volco::SliderEnds* ends);

// Removes the stored ends.
void slider_ends_clear();

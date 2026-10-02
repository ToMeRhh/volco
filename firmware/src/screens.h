#pragma once
#include <stdint.h>
#include "volco_protocol.h"

// The screen subsystem (screens.cpp). With no IND_SCREEN buttons every call is a no-op and I2C
// is never touched.
void screens_init(uint32_t now);
void screens_show(uint8_t button, uint8_t transition, const volco::ScreenContent& content, uint32_t now);
void screens_host_lost(uint32_t now);    // every screen shows the local "not connected" screen
void screens_activity(uint32_t now);     // a slider moved (button changes go through filter_button)
bool screens_filter_button(uint8_t button, bool pressed, uint32_t now);  // false = swallow
void screens_tick(uint32_t now);         // every loop pass: power, then redraw at most one panel

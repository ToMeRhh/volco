#pragma once
#include <stdint.h>
#include "volco_protocol.h"
#include "volco_runtime.h"

// The screen subsystem (screens.cpp). With no screen buttons every call is a no-op and I2C is
// never touched.
void screens_init(const volco::RuntimeConfig& config, uint32_t now);
void screens_show(uint8_t button, uint8_t transition, const volco::ScreenContent& content, uint32_t now);
void screens_host_lost(uint32_t now);    // every screen shows the local "not connected" screen
void screens_activity(uint32_t now);     // a slider moved (button changes go through filter_button)
bool screens_filter_button(uint8_t button, bool pressed, uint32_t now);  // false = swallow
void screens_tick(uint32_t now);         // every loop pass: power, then redraw at most one panel
// Find screens (pin_helpers.cpp). A hardware bus (0, 1) a screen uses is scanned through Wire;
// false for any other bus. A software bus is scanned on its pins by pin_helpers, then restarted
// here (nothing for a bus screens_init didn't start).
bool screens_scan_bus(uint8_t bus, uint8_t* addresses, uint8_t& count);
void screens_restart_bus(uint8_t bus);

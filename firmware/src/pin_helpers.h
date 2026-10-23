#pragma once
#include <stdint.h>

#include "volco_runtime.h"

// The wiring helpers on the hardware (pin_helpers.cpp): Detect (a pin watch), Find screens (an I2C
// scan) and PinDrive. The rules are lib/volco_pin_helpers; this samples, scans and drives.
void pin_helpers_begin(const volco::RuntimeConfig& rc);
void pin_helpers_watch(uint8_t role, uint32_t now);  // a WatchRole; 0 stops
// Scans and fills addresses; returns the count (0 when refused).
uint8_t pin_helpers_scan(uint8_t sda, uint8_t scl, uint8_t* addresses);
void pin_helpers_drive(uint8_t pin, uint8_t level, uint16_t durationMs, uint32_t now);
bool pin_helpers_driving(uint8_t pin);
void pin_helpers_host_lost();  // stops the watch
// Every loop pass: samples every kWatchSampleMs, reports through `report`, ends expired drives
// and calls `restored(pin)` for a configured LED pin whose drive ended.
void pin_helpers_tick(uint32_t now, void (*report)(uint8_t pin, uint32_t now), void (*restored)(uint8_t pin));

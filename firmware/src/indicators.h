#pragma once
#include <stdint.h>
#include "hardware.h"

// value 0 = off; LED: lit for any other value; LED pair: 1 lights A, 2 lights B.
void showIndicator(const Indicator& ind, uint8_t value);
// Sets the pins up and shows "off" (a different pin level per polarity).
void initIndicator(const Indicator& ind);

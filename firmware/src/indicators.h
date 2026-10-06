#pragma once
#include <stdint.h>

#include "volco_runtime.h"

// value 0 = off; LED: lit for any other value; LED pair: 1 lights A, 2 lights B.
void showIndicator(const volco::RuntimeButton& b, uint8_t value);
// Sets the pins up and shows "off" (a different pin level per polarity).
void initIndicator(const volco::RuntimeButton& b);

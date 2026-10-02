#include "indicators.h"

#include <Arduino.h>

static void writeLed(uint8_t pin, bool lit, bool activeLow) {
  digitalWrite(pin, (lit != activeLow) ? HIGH : LOW);
}

// value 0 = off/unknown, 1..N = on, or choice N. What that means (muted,
// output A, preset 2, ...) is the PC's business; this only renders it.
void showIndicator(const Indicator& ind, uint8_t value) {
  switch (ind.kind) {
    case IND_KIND_LED:
      writeLed(ind.pinA, value != 0, ind.activeLow);
      break;
    case IND_KIND_LED_PAIR:
      writeLed(ind.pinA, value == 1, ind.activeLow);
      writeLed(ind.pinB, value == 2, ind.activeLow);
      break;
    case IND_KIND_SCREEN:
      break;  // drawn by screens.cpp
    case IND_KIND_NONE:
      break;
  }
}

void initIndicator(const Indicator& ind) {
  switch (ind.kind) {
    case IND_KIND_LED_PAIR:
      pinMode(ind.pinB, OUTPUT);
      [[fallthrough]];  // pin A is set up like a single LED
    case IND_KIND_LED:
      pinMode(ind.pinA, OUTPUT);
      break;
    case IND_KIND_SCREEN:
      break;  // drawn by screens.cpp
    case IND_KIND_NONE:
      break;
  }
  // Start dark. "Off" is a different pin level per polarity, which is why
  // showIndicator, not a plain digitalWrite, sets it: driving an active-low
  // LED LOW here would show it lit until the PC pushes real state.
  showIndicator(ind, 0);
}

#include "volco_indicators.h"

#include <Arduino.h>

static void writeLed(uint8_t pin, bool lit, bool activeLow) {
  digitalWrite(pin, (lit != activeLow) ? HIGH : LOW);
}

// value 0 = off/unknown, 1..N = on, or choice N. What that means (muted,
// output A, preset 2, ...) is the PC's business; this only renders it.
void showIndicator(const volco::RuntimeButton& b, uint8_t value) {
  switch (b.kind) {
    case volco::KIND_LED:
      writeLed(b.pinA, value != 0, b.activeLow);
      break;
    case volco::KIND_LED_PAIR:
      writeLed(b.pinA, value == 1, b.activeLow);
      writeLed(b.pinB, value == 2, b.activeLow);
      break;
    default:
      break;  // screens are drawn by screens.cpp; KIND_NONE shows nothing
  }
}

void initIndicator(const volco::RuntimeButton& b) {
  switch (b.kind) {
    case volco::KIND_LED_PAIR:
      pinMode(b.pinB, OUTPUT);
      [[fallthrough]];  // pin A is set up like a single LED
    case volco::KIND_LED:
      pinMode(b.pinA, OUTPUT);
      break;
    default:
      break;
  }
  // Start dark. "Off" is a different pin level per polarity, which is why
  // showIndicator, not a plain digitalWrite, sets it: driving an active-low
  // LED LOW here would show it lit until the PC pushes real state.
  showIndicator(b, 0);
}

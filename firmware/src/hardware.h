#pragma once

#include <stdint.h>

// ============================================================
// Types for the hardware tables in config.h
// ============================================================
//
// The firmware knows nothing about what a button *does* (mute, switch output,
// change preset, ...). The PC decides that and sends each button a feedback
// value (protocol message 0x05). An Indicator says how one button shows that
// value on this particular hardware.

enum IndicatorKind : uint8_t {
  IND_KIND_NONE,      // no light: feedback for this button is ignored
  IND_KIND_LED,       // one LED: lit for any value except 0
  IND_KIND_LED_PAIR,  // two LEDs: value 1 lights A, value 2 lights B, 0 neither
};

struct Indicator {
  IndicatorKind kind;
  uint8_t pinA;    // the LED (IND_KIND_LED) or LED A (IND_KIND_LED_PAIR)
  uint8_t pinB;    // LED B (IND_KIND_LED_PAIR only)
  bool activeLow;  // true when driving the pin LOW lights the LED
};

struct ButtonDef {
  uint8_t pin;          // input pin; wired to GND when pressed (INPUT_PULLUP)
  Indicator indicator;  // how this button shows its feedback value
};

// LED polarity: which pin level lights the LED.
#define ACTIVE_HIGH false
#define ACTIVE_LOW true

// Indicator constructors, for the BUTTONS table in config.h.
#define IND_NONE Indicator{IND_KIND_NONE, 0, 0, false}
#define IND_LED(pin, polarity) Indicator{IND_KIND_LED, (pin), 0, (polarity)}
#define IND_LED_PAIR(pinA, pinB, polarity) \
  Indicator{IND_KIND_LED_PAIR, (pinA), (pinB), (polarity)}

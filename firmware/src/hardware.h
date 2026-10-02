#pragma once

#include <stdint.h>

// ============================================================
// Types for the hardware tables in config.h
// ============================================================
//
// The firmware knows nothing about what a button *does*. The PC sends each LED button a value
// (message 0x83) and each screen button what to draw (0x84); an Indicator says what this
// particular hardware has for a button, and Hello reports it to the PC.

enum IndicatorKind : uint8_t {
  IND_KIND_NONE,      // nothing to show
  IND_KIND_LED,       // one LED: lit for any value except 0
  IND_KIND_LED_PAIR,  // two LEDs: value 1 lights A, value 2 lights B, anything else neither
  IND_KIND_SCREEN,    // a 128x64 SSD1306 on an I2C bus
};

struct Indicator {
  IndicatorKind kind;
  uint8_t pinA;     // the LED (IND_KIND_LED) or LED A (IND_KIND_LED_PAIR)
  uint8_t pinB;     // LED B (IND_KIND_LED_PAIR only)
  bool activeLow;   // true when driving the pin LOW lights the LED
  uint8_t bus;      // screen: I2C bus 0 or 1
  uint8_t address;  // screen: 7-bit I2C address, 0x3C or 0x3D
};

struct ButtonDef {
  uint8_t pin;          // input pin; wired to GND when pressed (INPUT_PULLUP)
  Indicator indicator;  // what this button shows
};

// LED polarity: which pin level lights the LED.
#define ACTIVE_HIGH false
#define ACTIVE_LOW true

// Indicator constructors, for the BUTTONS table in config.h.
#define IND_NONE Indicator{IND_KIND_NONE, 0, 0, false, 0, 0}
#define IND_LED(pin, polarity) Indicator{IND_KIND_LED, (pin), 0, (polarity), 0, 0}
#define IND_LED_PAIR(pinA, pinB, polarity) Indicator{IND_KIND_LED_PAIR, (pinA), (pinB), (polarity), 0, 0}
#define IND_SCREEN(bus, address) Indicator{IND_KIND_SCREEN, 0, 0, false, (bus), (address)}

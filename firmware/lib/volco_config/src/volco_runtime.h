#pragma once
// The controller config as the firmware uses it (spec §6.3): plain values, defaults applied, times
// in ms, and only the I2C buses a screen actually uses. src/ reads nothing else.
#include <stdint.h>

#include "volco_config.h"
#include "volco_protocol.h"  // Kind, kMaxButtons

namespace volco {

constexpr uint8_t kMaxSliders = 16;
constexpr uint8_t kMaxScreens = 4;  // 2 buses x 2 addresses

struct RuntimeButton {
  uint8_t pin;
  uint8_t kind;          // Kind: KIND_NONE, KIND_LED, KIND_LED_PAIR or KIND_SCREEN
  uint8_t pinA, pinB;    // the LED (pinA), or LED pair A and B
  bool activeLow;
  uint8_t bus, address;  // screen: I2C bus 0/1, 7-bit address
};

struct RuntimeBus {
  bool used;  // a screen is on this bus; an unused bus is never started
  uint8_t sda, scl;
  uint32_t clockHz;
};

struct RuntimeUdp {
  bool enabled;
  char ssid[33];
  char password[65];
  uint32_t pcIp;  // a.b.c.d = a<<24 | b<<16 | c<<8 | d
  uint16_t pcPort, localPort;
};

struct RuntimeScreens {
  uint8_t brightness, dimBrightness;
  uint16_t scrollSpeedPxS;
  uint32_t transitionMs, scrollHoldMs, scrollEndHoldMs, dimAfterMs, offAfterMs, pixelShiftEveryMs;
  bool dimEnabled, offEnabled, pixelShiftEnabled, wakeSwallowsPress;
};

struct RuntimeInput {
  uint32_t debounceMs, sliderIntervalMs;
  float filterWeight;  // EMA weight 0.01..1
  uint16_t sliderDeadband;
};

struct RuntimeConfig {
  uint8_t sliderCount;
  uint8_t sliderPins[kMaxSliders];
  uint8_t buttonCount;
  RuntimeButton buttons[kMaxButtons];
  RuntimeBus i2c[2];
  RuntimeUdp udp;
  RuntimeScreens screens;
  RuntimeInput input;
};

// `config` must already have passed validateConfig (pins exist, buses present for screens, ...).
void toRuntime(const volco_config_ControllerConfig& config, RuntimeConfig& out);

}  // namespace volco

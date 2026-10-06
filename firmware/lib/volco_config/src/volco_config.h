#pragma once
// The controller config (spec §6): decoding with the limits both sides enforce, and validation
// against the chip. Pure C++ on top of the nanopb code generated from proto/volco_config.proto.
#include <stddef.h>
#include <stdint.h>

#include "volco_chip.h"
#include "volco_config.pb.h"

namespace volco {

constexpr size_t kMaxConfigBlob = 1024;
constexpr uint8_t kNoItem = 0xFF;
constexpr uint8_t kI2cBuses = 4;  // 0 and 1: the ESP32's I2C controllers; 2 and 3: software I2C

// The config's I2C bus n (i2c0..i2c3) and whether it is present.
inline bool hasI2cBus(const volco_config_ControllerConfig& c, uint8_t bus) {
  switch (bus) {
    case 0: return c.has_i2c0;
    case 1: return c.has_i2c1;
    case 2: return c.has_i2c2;
    default: return c.has_i2c3;
  }
}
inline const volco_config_I2cBus& i2cBus(const volco_config_ControllerConfig& c, uint8_t bus) {
  switch (bus) {
    case 0: return c.i2c0;
    case 1: return c.i2c1;
    case 2: return c.i2c2;
    default: return c.i2c3;
  }
}

// ConfigResult codes (spec §5.4).
enum ConfigResultCode : uint8_t {
  CFG_OK = 0, CFG_BAD_SEQUENCE = 1, CFG_BAD_CRC = 2, CFG_TOO_LARGE = 3,
  CFG_DECODE_FAILED = 4, CFG_INVALID = 5, CFG_STORAGE_FAILED = 6,
};

// Field codes of an INVALID result (spec §6.4).
enum ConfigField : uint8_t {
  FIELD_SLIDER_PIN = 1, FIELD_BUTTON_PIN = 2, FIELD_LED_PIN = 3, FIELD_PAIR_PIN_A = 4,
  FIELD_PAIR_PIN_B = 5, FIELD_SCREEN_BUS = 6, FIELD_SCREEN_ADDRESS = 7, FIELD_I2C_MISSING = 8,
  FIELD_I2C_PINS = 9, FIELD_I2C_CLOCK = 10, FIELD_UDP_SSID = 11, FIELD_UDP_PASSWORD = 12,
  FIELD_UDP_PC_IP = 13, FIELD_UDP_PORT = 14, FIELD_SCREEN_VALUE = 15, FIELD_INPUT_VALUE = 16,
};

struct ValidationResult {
  uint8_t code;   // CFG_OK or CFG_INVALID
  uint8_t item;   // slider/button index, I2C bus, range field number, or kNoItem
  uint8_t field;  // ConfigField, or kNoItem
};

// True when a string field (preset, udp.ssid, udp.password) holds a NUL byte. Works on the wire
// bytes: nanopb copies strings into zeroed char arrays, so "a\0" and "a" look the same once
// decoded. Malformed input returns false and is left to pb_decode to reject.
bool hasNulInStrings(const uint8_t* data, size_t len);

// Decodes a blob, applying the proto2 defaults (absent submessages included). False on anything
// the spec calls a decode failure: over kMaxConfigBlob, malformed, a count or string over its
// limit, invalid UTF-8, or a NUL in a string.
bool decodeConfig(const uint8_t* data, size_t len, volco_config_ControllerConfig& out);

// The first violation of spec §6.4, in its exact order, or {CFG_OK, kNoItem, kNoItem}.
ValidationResult validateConfig(const volco_config_ControllerConfig& config, const ChipInfo& chip);

}  // namespace volco

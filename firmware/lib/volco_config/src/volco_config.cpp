#include "volco_config.h"

#include <pb_decode.h>
#include <string.h>

namespace volco {
namespace {

bool readVarint(const uint8_t*& p, const uint8_t* end, uint64_t& v) {
  v = 0;
  for (int shift = 0; shift < 64; shift += 7) {
    if (p >= end) return false;
    uint8_t b = *p++;
    v |= (uint64_t)(b & 0x7F) << shift;
    if (!(b & 0x80)) return true;
  }
  return false;
}

// Walks one message's fields. Top level: field 8 (preset) is a string and field 5 (udp) a nested
// message whose fields 2 (ssid) and 3 (password) are strings. Returns false on a NUL in a string.
bool stringsClean(const uint8_t* p, const uint8_t* end, bool inUdp) {
  while (p < end) {
    uint64_t key;
    if (!readVarint(p, end, key)) return true;
    uint32_t field = (uint32_t)(key >> 3), wireType = (uint32_t)(key & 7);
    if (wireType == 0) {
      uint64_t ignored;
      if (!readVarint(p, end, ignored)) return true;
    } else if (wireType == 1) {
      if (end - p < 8) return true;
      p += 8;
    } else if (wireType == 5) {
      if (end - p < 4) return true;
      p += 4;
    } else if (wireType == 2) {
      uint64_t n;
      if (!readVarint(p, end, n) || n > (uint64_t)(end - p)) return true;
      const uint8_t* s = p;
      p += n;
      bool isString = inUdp ? (field == 2 || field == 3) : field == 8;
      if (isString && memchr(s, 0, (size_t)n)) return false;
      if (!inUdp && field == 5 && !stringsClean(s, s + n, true)) return false;
    } else {
      return true;
    }
  }
  return true;
}

}  // namespace

bool hasNulInStrings(const uint8_t* data, size_t len) {
  return len > 0 && !stringsClean(data, data + len, false);
}

bool decodeConfig(const uint8_t* data, size_t len, volco_config_ControllerConfig& out) {
  out = volco_config_ControllerConfig_init_default;
  if (len > kMaxConfigBlob) return false;
  if (hasNulInStrings(data, len)) return false;
  pb_istream_t stream = pb_istream_from_buffer(data, len);
  return pb_decode(&stream, volco_config_ControllerConfig_fields, &out);
}

namespace {

ValidationResult invalid(uint8_t item, uint8_t field) { return {CFG_INVALID, item, field}; }
bool inRange(uint32_t v, uint32_t lo, uint32_t hi) { return v >= lo && v <= hi; }

struct PinWalk {  // spec §6.4 step 5: the first pin already seen, reported at its later use
  uint64_t seen = 0;
  bool repeat(uint32_t pin) {
    uint64_t b = 1ull << pin;  // every pin here already passed a usable() check, so pin < 64
    bool r = (seen & b) != 0;
    seen |= b;
    return r;
  }
};

bool badIp(uint32_t ip) {
  return ip == 0 || ip == 0xFFFFFFFFu || (ip >> 24) == 127 || (ip >> 28) == 0xE;
}

}  // namespace

ValidationResult validateConfig(const volco_config_ControllerConfig& c, const ChipInfo& chip) {
  // 1. Counts (the decoder already enforces max_count; kept for parity with the C# path).
  if (c.sliders_count > 16) return invalid(kNoItem, FIELD_SLIDER_PIN);
  if (c.buttons_count > 32) return invalid(kNoItem, FIELD_BUTTON_PIN);
  const bool udp = c.has_udp && c.udp.enabled;

  // 2. Sliders.
  for (pb_size_t i = 0; i < c.sliders_count; i++) {
    const volco_config_Slider& s = c.sliders[i];
    if (!s.has_pin || !chip.adcCapable(s.pin)) return invalid((uint8_t)i, FIELD_SLIDER_PIN);
    if (udp && chip.isAdc2(s.pin)) return invalid((uint8_t)i, FIELD_SLIDER_PIN);
  }

  // 3. Buttons: pin, indicator fields, then the (bus, address) duplicate check, one button at a time.
  for (pb_size_t i = 0; i < c.buttons_count; i++) {
    const volco_config_Button& b = c.buttons[i];
    const uint8_t item = (uint8_t)i;
    if (!b.has_pin || !chip.usable(b.pin)) return invalid(item, FIELD_BUTTON_PIN);
    if (b.which_indicator == volco_config_Button_led_tag) {
      const volco_config_Led& l = b.indicator.led;
      if (!l.has_pin || !chip.outputCapable(l.pin)) return invalid(item, FIELD_LED_PIN);
    } else if (b.which_indicator == volco_config_Button_led_pair_tag) {
      const volco_config_LedPair& p = b.indicator.led_pair;
      if (!p.has_pin_a || !chip.outputCapable(p.pin_a)) return invalid(item, FIELD_PAIR_PIN_A);
      if (!p.has_pin_b || !chip.outputCapable(p.pin_b) || p.pin_b == p.pin_a) return invalid(item, FIELD_PAIR_PIN_B);
    } else if (b.which_indicator == volco_config_Button_screen_tag) {
      const volco_config_Screen& s = b.indicator.screen;
      if (s.bus >= kI2cBuses) return invalid(item, FIELD_SCREEN_BUS);
      if (s.address != 0x3C && s.address != 0x3D) return invalid(item, FIELD_SCREEN_ADDRESS);
      for (pb_size_t j = 0; j < i; j++) {
        const volco_config_Button& o = c.buttons[j];
        if (o.which_indicator == volco_config_Button_screen_tag && o.indicator.screen.bus == s.bus &&
            o.indicator.screen.address == s.address)
          return invalid(item, FIELD_SCREEN_ADDRESS);
      }
    }
  }

  // 4. I2C, for each bus some screen uses (an unused I2cBus is ignored entirely).
  bool used[kI2cBuses] = {false, false, false, false};
  for (pb_size_t i = 0; i < c.buttons_count; i++)
    if (c.buttons[i].which_indicator == volco_config_Button_screen_tag) used[c.buttons[i].indicator.screen.bus] = true;
  for (uint8_t bus = 0; bus < kI2cBuses; bus++) {
    if (!used[bus]) continue;
    const bool present = hasI2cBus(c, bus);
    const volco_config_I2cBus& i2c = i2cBus(c, bus);
    if (!present) return invalid(bus, FIELD_I2C_MISSING);
    if (!i2c.has_sda || !i2c.has_scl || !chip.outputCapable(i2c.sda) || !chip.outputCapable(i2c.scl) ||
        i2c.sda == i2c.scl)
      return invalid(bus, FIELD_I2C_PINS);
    if (!inRange(i2c.clock_hz, 100000, 1000000)) return invalid(bus, FIELD_I2C_CLOCK);
  }

  // 5. Pin uniqueness, in walk order: sliders, buttons with their indicator pins, used I2C buses.
  PinWalk walk;
  for (pb_size_t i = 0; i < c.sliders_count; i++)
    if (walk.repeat(c.sliders[i].pin)) return invalid((uint8_t)i, FIELD_SLIDER_PIN);
  for (pb_size_t i = 0; i < c.buttons_count; i++) {
    const volco_config_Button& b = c.buttons[i];
    const uint8_t item = (uint8_t)i;
    if (walk.repeat(b.pin)) return invalid(item, FIELD_BUTTON_PIN);
    if (b.which_indicator == volco_config_Button_led_tag) {
      if (walk.repeat(b.indicator.led.pin)) return invalid(item, FIELD_LED_PIN);
    } else if (b.which_indicator == volco_config_Button_led_pair_tag) {
      if (walk.repeat(b.indicator.led_pair.pin_a)) return invalid(item, FIELD_PAIR_PIN_A);
      if (walk.repeat(b.indicator.led_pair.pin_b)) return invalid(item, FIELD_PAIR_PIN_B);
    }
  }
  for (uint8_t bus = 0; bus < kI2cBuses; bus++) {
    if (!used[bus]) continue;
    const volco_config_I2cBus& i2c = i2cBus(c, bus);
    if (walk.repeat(i2c.sda) || walk.repeat(i2c.scl)) return invalid(bus, FIELD_I2C_PINS);
  }

  // 6. UDP, only when enabled.
  if (udp) {
    const size_t ssid = strlen(c.udp.ssid), password = strlen(c.udp.password);
    if (ssid < 1 || ssid > 32) return invalid(kNoItem, FIELD_UDP_SSID);
    if (password != 0 && (password < 8 || password > 64)) return invalid(kNoItem, FIELD_UDP_PASSWORD);
    if (!c.udp.has_pc_ip || badIp(c.udp.pc_ip)) return invalid(kNoItem, FIELD_UDP_PC_IP);
    if (!inRange(c.udp.pc_port, 1, 65535) || !inRange(c.udp.local_port, 1, 65535))
      return invalid(kNoItem, FIELD_UDP_PORT);
  }

  // 7. Ranges, in field-number order, screens before input (absent submessages hold defaults).
  const volco_config_ScreenBehaviour& s = c.screens;
  if (!inRange(s.brightness, 0, 255)) return invalid(1, FIELD_SCREEN_VALUE);
  if (!inRange(s.transition_ms, 0, 2000)) return invalid(2, FIELD_SCREEN_VALUE);
  if (!inRange(s.scroll_hold_ms, 0, 10000)) return invalid(3, FIELD_SCREEN_VALUE);
  if (!inRange(s.scroll_speed_px_s, 5, 200)) return invalid(4, FIELD_SCREEN_VALUE);
  if (!inRange(s.scroll_end_hold_ms, 0, 10000)) return invalid(5, FIELD_SCREEN_VALUE);
  if (!inRange(s.dim_after_s, 10, 86400)) return invalid(7, FIELD_SCREEN_VALUE);
  if (!inRange(s.dim_brightness, 0, 255)) return invalid(8, FIELD_SCREEN_VALUE);
  if (!inRange(s.off_after_s, 10, 86400)) return invalid(10, FIELD_SCREEN_VALUE);
  if (!inRange(s.pixel_shift_every_s, 10, 86400)) return invalid(12, FIELD_SCREEN_VALUE);
  const volco_config_InputTuning& in = c.input;
  if (!inRange(in.debounce_ms, 1, 200)) return invalid(1, FIELD_INPUT_VALUE);
  if (!inRange(in.slider_interval_ms, 1, 100)) return invalid(2, FIELD_INPUT_VALUE);
  if (!inRange(in.filter_weight_pct, 1, 100)) return invalid(3, FIELD_INPUT_VALUE);
  if (!inRange(in.slider_deadband, 0, 500)) return invalid(4, FIELD_INPUT_VALUE);

  return {CFG_OK, kNoItem, kNoItem};
}

}  // namespace volco

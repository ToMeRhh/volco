#include "volco_runtime.h"

#include <string.h>

namespace volco {

void toRuntime(const volco_config_ControllerConfig& c, RuntimeConfig& r) {
  memset(&r, 0, sizeof r);

  r.sliderCount = (uint8_t)c.sliders_count;
  for (pb_size_t i = 0; i < c.sliders_count; i++) r.sliderPins[i] = (uint8_t)c.sliders[i].pin;

  r.buttonCount = (uint8_t)c.buttons_count;
  for (pb_size_t i = 0; i < c.buttons_count; i++) {
    const volco_config_Button& b = c.buttons[i];
    RuntimeButton& o = r.buttons[i];
    o.pin = (uint8_t)b.pin;
    o.kind = KIND_NONE;
    if (b.which_indicator == volco_config_Button_led_tag) {
      o.kind = KIND_LED;
      o.pinA = (uint8_t)b.indicator.led.pin;
      o.activeLow = b.indicator.led.active_low;
    } else if (b.which_indicator == volco_config_Button_led_pair_tag) {
      o.kind = KIND_LED_PAIR;
      o.pinA = (uint8_t)b.indicator.led_pair.pin_a;
      o.pinB = (uint8_t)b.indicator.led_pair.pin_b;
      o.activeLow = b.indicator.led_pair.active_low;
    } else if (b.which_indicator == volco_config_Button_screen_tag) {
      o.kind = KIND_SCREEN;
      o.bus = (uint8_t)b.indicator.screen.bus;
      o.address = (uint8_t)b.indicator.screen.address;
      r.i2c[o.bus].used = true;
    }
  }

  for (uint8_t bus = 0; bus < kI2cBuses; bus++) {
    if (!r.i2c[bus].used) continue;
    const volco_config_I2cBus& src = i2cBus(c, bus);
    r.i2c[bus].sda = (uint8_t)src.sda;
    r.i2c[bus].scl = (uint8_t)src.scl;
    r.i2c[bus].clockHz = src.clock_hz;
  }

  r.udp.enabled = c.has_udp && c.udp.enabled;
  strncpy(r.udp.ssid, c.udp.ssid, sizeof r.udp.ssid - 1);
  strncpy(r.udp.password, c.udp.password, sizeof r.udp.password - 1);
  r.udp.pcIp = c.udp.pc_ip;
  r.udp.pcPort = (uint16_t)c.udp.pc_port;
  r.udp.localPort = (uint16_t)c.udp.local_port;

  const volco_config_ScreenBehaviour& s = c.screens;  // nanopb fills absent submessages with defaults
  r.screens.brightness = (uint8_t)s.brightness;
  r.screens.dimBrightness = (uint8_t)s.dim_brightness;
  r.screens.scrollSpeedPxS = (uint16_t)s.scroll_speed_px_s;
  r.screens.transitionMs = s.transition_ms;
  r.screens.scrollHoldMs = s.scroll_hold_ms;
  r.screens.scrollEndHoldMs = s.scroll_end_hold_ms;
  r.screens.dimAfterMs = s.dim_after_s * 1000u;
  r.screens.offAfterMs = s.off_after_s * 1000u;
  r.screens.pixelShiftEveryMs = s.pixel_shift_every_s * 1000u;
  r.screens.dimEnabled = s.dim_enabled;
  r.screens.offEnabled = s.off_enabled;
  r.screens.pixelShiftEnabled = s.pixel_shift_enabled;
  r.screens.wakeSwallowsPress = s.wake_swallows_press;

  const volco_config_InputTuning& in = c.input;
  r.input.debounceMs = in.debounce_ms;
  r.input.sliderIntervalMs = in.slider_interval_ms;
  r.input.filterWeight = (float)in.filter_weight_pct / 100.0f;
  r.input.sliderDeadband = (uint16_t)in.slider_deadband;
}

}  // namespace volco

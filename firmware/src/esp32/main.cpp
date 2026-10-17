#include <Arduino.h>

#include "config.h"
#include "config_session.h"
#include "config_store.h"
#include "debug_log.h"
#include "volco_indicators.h"
#include "volco_input.h"
#include "screens.h"
#include "transport.h"
#include "volco_protocol.h"
#include "volco_runtime.h"

// The firmware build id Hello reports (spec §9.2); CI sets it, local builds are "dev".
#ifndef VOLCO_FW_VERSION
#define VOLCO_FW_VERSION "dev"
#endif
static_assert(sizeof(VOLCO_FW_VERSION) - 1 <= volco::kMaxFirmwareId, "VOLCO_FW_VERSION is longer than 32 bytes");

// ============================================================
// State. Everything about the hardware comes from the stored config (config_store).
// ============================================================
static LoadedConfig loaded;
static const volco::RuntimeConfig& rc = loaded.rc;

static volco::SliderFilter sliders[volco::kMaxSliders];
static unsigned long lastSliderReadTime = 0;
static volco::ButtonDebouncer buttons[volco::kMaxButtons];

// What Hello reports (wire values: 0 none, 1 LED, 2 LED pair, 3 screen).
static uint8_t kinds[volco::kMaxButtons];

static volco::ActiveTransport active;  // spec §5.5
static volco::FrameParser parsers[2];  // one per transport, so their bytes never mix

// ============================================================
// The PC link: protocol state lives in lib/volco_protocol; this sink connects it to the hardware.
// ============================================================
class HardwareSink : public volco::LinkSink {
 public:
  void send(const uint8_t* frame, size_t len) override {
    const uint8_t targets = active.targets(transport_up_mask());
    for (uint8_t t = 0; t < 2; t++)
      if (targets & (1u << t)) transport_send(t, frame, len);
  }
  void applyLed(uint8_t button, uint8_t value) override { showIndicator(rc.buttons[button], value); }
  void applyScreen(uint8_t button, uint8_t transition, const volco::ScreenContent& c, uint32_t now) override {
    screens_show(button, transition, c, now);
  }
  void hostLost() override {
    DEBUG_LOG("[DBG] PC silent: LEDs off, screens show 'not connected'");
    active.onHostLost();
    for (uint8_t i = 0; i < rc.buttonCount; i++) showIndicator(rc.buttons[i], 0);
    screens_host_lost(millis());
  }
  // Sent after every Hello (PR #68). The deadband then measures from the value just sent.
  uint16_t sliderValue(uint8_t i) override { return sliders[i].report(); }
};

static HardwareSink sink;
static volco::Link* pcLink = nullptr;

// Spec §5.5 exception: whenever Wi-Fi connects, Hello goes out on UDP even if serial is latched,
// followed by every slider's position like any Hello.
static void sendHelloOnUdp() {
  uint8_t f[volco::kMaxHelloFrame];
  const volco::HelloInfo h{1, VOLCO_FW_VERSION, (uint8_t)loaded.state, loaded.crc, rc.sliderCount, rc.buttonCount, kinds};
  transport_send(TRANSPORT_UDP, f, volco::encodeHello(h, f));
  for (uint8_t i = 0; i < rc.sliderCount; i++)
    transport_send(TRANSPORT_UDP, f, volco::encodeSlider(i, sink.sliderValue(i), f));
}

// ============================================================
// Setup (spec §7 order): serial, config, pins, transports, screens, link.
// ============================================================
void setup() {
  transport_begin_serial();
  config_load(loaded);
  DEBUG_LOG("[DBG] Config: state " + String(loaded.state) + ", " + String(rc.sliderCount) + " sliders, " +
            String(rc.buttonCount) + " buttons");

  for (uint8_t i = 0; i < rc.sliderCount; i++) pinMode(rc.sliderPins[i], INPUT);
  for (uint8_t i = 0; i < rc.buttonCount; i++) {
    pinMode(rc.buttons[i].pin, INPUT_PULLUP);
    buttons[i].reset();
    initIndicator(rc.buttons[i]);
    kinds[i] = rc.buttons[i].kind;  // Kind values are the wire values
  }

  // Seed from an average, not one noisy read: Hello reports these positions right away.
  analogReadResolution(12);
  for (uint8_t i = 0; i < rc.sliderCount; i++) {
    uint32_t sum = 0;
    for (uint8_t r = 0; r < SLIDER_SEED_READS; r++) sum += analogRead(rc.sliderPins[i]);
    sliders[i].seed(sum / SLIDER_SEED_READS);
  }

  transport_begin_udp(rc.udp);
  screens_init(rc, millis());
  static volco::Link link({rc.sliderCount, rc.buttonCount, kinds, HOST_TIMEOUT_MS, KEEPALIVE_MS, COMMIT_FALLBACK_MS,
                           1, VOLCO_FW_VERSION, (uint8_t)loaded.state, loaded.crc},
                          sink);
  pcLink = &link;
  config_session_begin(link);
  pcLink->begin(millis());  // Hello: the ESP32 may have just reset because the PC opened the port
  DEBUG_LOG("[DBG] Setup complete");
}

// ============================================================
// Main loop: sliders → buttons → receive → link → screens → Wi-Fi. Never blocks for long.
// ============================================================
void loop() {
  const uint32_t now = millis();

  // 1. Sliders: EMA smoothing, then a deadband (lib/volco_input).
  if (now - lastSliderReadTime >= rc.input.sliderIntervalMs) {
    lastSliderReadTime = now;
    for (uint8_t i = 0; i < rc.sliderCount; i++)
      if (sliders[i].update(analogRead(rc.sliderPins[i]), rc.input.filterWeightPct, rc.input.sliderDeadband)) {
        screens_activity(now);
        pcLink->sendSlider(i, sliders[i].value(), now);
      }
  }

  // 2. Buttons: debounced on timestamps, so a press is seen even when a redraw delayed this pass.
  for (uint8_t i = 0; i < rc.buttonCount; i++)
    if (buttons[i].update(digitalRead(rc.buttons[i].pin) == HIGH, now, rc.input.debounceMs)) {
      const bool pressed = buttons[i].pressed();
      if (screens_filter_button(i, pressed, now)) pcLink->sendButton(i, pressed, now);
    }

  // 3. Receive: drain each transport, frame by frame. Latch before the link replies (spec §5.5).
  uint8_t buf[256];
  for (uint8_t t = 0; t < 2; t++) {
    int n;
    while ((n = transport_receive(t, buf, sizeof buf)) > 0)
      for (int i = 0; i < n; i++)
        if (parsers[t].feed(buf[i])) {
          active.onValidFrame(t);
          pcLink->onPayload(parsers[t].payload(), parsers[t].length(), now, t);
        }
  }

  // 4. Link: keepalive, host timeout, Commit fallback; the config write timeout.
  pcLink->tick(now);
  config_session_tick(now);

  // 5. Screens: power state, then redraw at most one panel.
  screens_tick(now);

  // 6. Wi-Fi: retry, and Hello on UDP each time it connects.
  if (transport_maintain(now)) sendHelloOnUdp();
}

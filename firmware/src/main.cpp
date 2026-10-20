#include <Arduino.h>

#include "config.h"
#include "config_session.h"
#include "config_store.h"
#include "debug_log.h"
#include "indicators.h"
#include "screens.h"
#include "slider_ends_store.h"
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

static uint16_t lastSliderValues[volco::kMaxSliders];
static float smoothedValues[volco::kMaxSliders] = {0};
static unsigned long lastSliderReadTime = 0;
// Each slider's learned travel ends: what is sent is the reading stretched between them.
static volco::SliderEnds sliderEnds[volco::kMaxSliders];
static unsigned long lastEndsSaveTime = 0;

static bool buttonRawStates[volco::kMaxButtons];
static bool buttonReportedStates[volco::kMaxButtons];
static unsigned long buttonChangeTime[volco::kMaxButtons];

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
  uint16_t sliderValue(uint8_t i) override {
    lastSliderValues[i] = (uint16_t)smoothedValues[i];
    return sliderEnds[i].map(lastSliderValues[i]);
  }
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
    buttonRawStates[i] = HIGH;
    buttonReportedStates[i] = HIGH;
    buttonChangeTime[i] = 0;
    initIndicator(rc.buttons[i]);
    kinds[i] = rc.buttons[i].kind;  // Kind values are the wire values
  }

  // Holding button 1 at boot forgets the learned slider ends.
  if (rc.buttonCount > 0 && digitalRead(rc.buttons[0].pin) == LOW) {
    slider_ends_clear();
    DEBUG_LOG("[DBG] Slider ends: cleared (button 1 held at boot)");
  }
  slider_ends_load(rc, sliderEnds);
  for (uint8_t i = 0; i < rc.sliderCount; i++)
    DEBUG_LOG("[DBG] Slider " + String(i) + " ends: " +
              (sliderEnds[i].learned() ? String(sliderEnds[i].lo()) + "-" + String(sliderEnds[i].hi()) : String("not learned")));

  // Seed from an average, not one noisy read: Hello reports these positions right away.
  analogReadResolution(12);
  for (uint8_t i = 0; i < rc.sliderCount; i++) {
    uint32_t sum = 0;
    for (uint8_t r = 0; r < SLIDER_SEED_READS; r++) sum += analogRead(rc.sliderPins[i]);
    lastSliderValues[i] = sum / SLIDER_SEED_READS;
    smoothedValues[i] = lastSliderValues[i];
    sliderEnds[i].learn(lastSliderValues[i]);
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

  // 1. Sliders: EMA smoothing, learn the ends, then a deadband on the reading. What is sent is
  //    the reading stretched between the learned ends.
  if (now - lastSliderReadTime >= rc.input.sliderIntervalMs) {
    lastSliderReadTime = now;
    for (uint8_t i = 0; i < rc.sliderCount; i++) {
      uint16_t raw = analogRead(rc.sliderPins[i]);
      smoothedValues[i] = raw * rc.input.filterWeight + smoothedValues[i] * (1.0f - rc.input.filterWeight);
      uint16_t filtered = (uint16_t)smoothedValues[i];
      sliderEnds[i].learn(filtered);
      int16_t delta = (int16_t)filtered - (int16_t)lastSliderValues[i];
      if (abs(delta) > rc.input.sliderDeadband) {
        lastSliderValues[i] = filtered;
        screens_activity(now);
        pcLink->sendSlider(i, sliderEnds[i].map(filtered), now);
      }
    }
  }

  // 1b. Store the learned ends when one moved, at most every SLIDER_ENDS_SAVE_MS (flash wear).
  if (now - lastEndsSaveTime >= SLIDER_ENDS_SAVE_MS) {
    bool dirty = false;
    for (uint8_t i = 0; i < rc.sliderCount; i++) dirty |= sliderEnds[i].needsSave();
    if (dirty) {
      lastEndsSaveTime = now;
      if (slider_ends_save(rc, sliderEnds)) {
        for (uint8_t i = 0; i < rc.sliderCount; i++) {
          sliderEnds[i].markSaved();
          DEBUG_LOG("[DBG] Slider " + String(i) + " ends saved: " +
                    (sliderEnds[i].learned() ? String(sliderEnds[i].lo()) + "-" + String(sliderEnds[i].hi()) : String("not learned")));
        }
      }
    }
  }

  // 2. Buttons: debounce on timestamps, so a press is seen even when a redraw delayed this pass.
  for (uint8_t i = 0; i < rc.buttonCount; i++) {
    bool raw = digitalRead(rc.buttons[i].pin);
    if (raw != buttonRawStates[i]) {
      buttonRawStates[i] = raw;
      buttonChangeTime[i] = now;
    }
    if (now - buttonChangeTime[i] >= rc.input.debounceMs && buttonRawStates[i] != buttonReportedStates[i]) {
      buttonReportedStates[i] = buttonRawStates[i];
      bool pressed = buttonReportedStates[i] == LOW;  // INPUT_PULLUP: LOW = pressed
      if (screens_filter_button(i, pressed, now)) pcLink->sendButton(i, pressed, now);
    }
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

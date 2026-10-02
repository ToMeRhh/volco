#include <Arduino.h>

#include "config.h"
#include "debug_log.h"
#include "indicators.h"
#include "screens.h"
#include "transport.h"
#include "volco_protocol.h"

// ============================================================
// Table sizes (the tables themselves are in config.h)
// ============================================================
static constexpr uint8_t NUM_SLIDERS = sizeof(SLIDER_PINS) / sizeof(SLIDER_PINS[0]);
static constexpr uint8_t NUM_BUTTONS = sizeof(BUTTONS) / sizeof(BUTTONS[0]);
static_assert(NUM_BUTTONS <= volco::kMaxButtons, "Too many buttons for the protocol");

// ============================================================
// State
// ============================================================
static uint16_t lastSliderValues[NUM_SLIDERS];
static float smoothedValues[NUM_SLIDERS] = {0};
static unsigned long lastSliderReadTime = 0;

static bool buttonRawStates[NUM_BUTTONS];
static bool buttonReportedStates[NUM_BUTTONS];
static unsigned long buttonChangeTime[NUM_BUTTONS];

static unsigned long lastReconnectTime = 0;

// What Hello reports, derived from BUTTONS (wire values: 0 none, 1 LED, 2 LED pair, 3 screen).
static uint8_t kinds[NUM_BUTTONS];

// ============================================================
// The PC link: protocol state lives in lib/volco_protocol; this sink connects it to the hardware.
// ============================================================
class HardwareSink : public volco::LinkSink {
 public:
  void send(const uint8_t* frame, size_t len) override { transport_send(frame, len); }
  void applyLed(uint8_t button, uint8_t value) override { showIndicator(BUTTONS[button].indicator, value); }
  void applyScreen(uint8_t button, uint8_t transition, const volco::ScreenContent& c, uint32_t now) override {
    screens_show(button, transition, c, now);
  }
  void hostLost() override {
    DEBUG_LOG("[DBG] PC silent: LEDs off, screens show 'not connected'");
    for (uint8_t i = 0; i < NUM_BUTTONS; i++) showIndicator(BUTTONS[i].indicator, 0);
    screens_host_lost(millis());
  }
};

static HardwareSink sink;
static volco::FrameParser parser;
static volco::Link pcLink({NUM_SLIDERS, NUM_BUTTONS, kinds, HOST_TIMEOUT_MS, KEEPALIVE_MS, COMMIT_FALLBACK_MS}, sink);

// ============================================================
// Setup
// ============================================================
void setup() {
  for (uint8_t i = 0; i < NUM_SLIDERS; i++) pinMode(SLIDER_PINS[i], INPUT);

  for (uint8_t i = 0; i < NUM_BUTTONS; i++) {
    pinMode(BUTTONS[i].pin, INPUT_PULLUP);
    buttonRawStates[i] = HIGH;
    buttonReportedStates[i] = HIGH;
    buttonChangeTime[i] = 0;
    initIndicator(BUTTONS[i].indicator);
    kinds[i] = (uint8_t)BUTTONS[i].indicator.kind;  // IndicatorKind values are the wire values
  }

  analogReadResolution(12);
  for (uint8_t i = 0; i < NUM_SLIDERS; i++) {
    lastSliderValues[i] = analogRead(SLIDER_PINS[i]);
    smoothedValues[i] = lastSliderValues[i];
  }

  transport_init();
  screens_init(millis());
  pcLink.begin(millis());  // Hello: the ESP32 may have just reset because the PC opened the port
#if defined(TRANSPORT_LOCAL_DEBUG)
  DEBUG_LOG("[DBG] *** LOCAL DEBUG MODE — no host connection, verbose event logging ***");
#endif
  DEBUG_LOG("[DBG] Setup complete");
}

// ============================================================
// Main loop: sliders → buttons → receive → link → screens → connection. Never blocks for long.
// ============================================================
void loop() {
  const uint32_t now = millis();

  // 1. Sliders (every SLIDER_READ_INTERVAL_MS): EMA smoothing, then a deadband.
  if (now - lastSliderReadTime >= SLIDER_READ_INTERVAL_MS) {
    lastSliderReadTime = now;
    for (uint8_t i = 0; i < NUM_SLIDERS; i++) {
      uint16_t raw = analogRead(SLIDER_PINS[i]);
      smoothedValues[i] = raw * FILTER_WEIGHT + smoothedValues[i] * (1.0 - FILTER_WEIGHT);
      uint16_t filtered = (uint16_t)smoothedValues[i];
      int16_t delta = (int16_t)filtered - (int16_t)lastSliderValues[i];
      if (abs(delta) > SLIDER_DEADBAND) {
        lastSliderValues[i] = filtered;
        screens_activity(now);
        pcLink.sendSlider(i, filtered, now);
#if defined(TRANSPORT_LOCAL_DEBUG)
        Serial.printf("[SLIDER] idx=%d  filtered=%d  delta=%d\n", i, filtered, delta);
#endif
      }
    }
  }

  // 2. Buttons: debounce on timestamps, so a press is seen even when a redraw delayed this pass.
  for (uint8_t i = 0; i < NUM_BUTTONS; i++) {
    bool raw = digitalRead(BUTTONS[i].pin);
    if (raw != buttonRawStates[i]) {
      buttonRawStates[i] = raw;
      buttonChangeTime[i] = now;
    }
    if (now - buttonChangeTime[i] >= DEBOUNCE_MS && buttonRawStates[i] != buttonReportedStates[i]) {
      buttonReportedStates[i] = buttonRawStates[i];
      bool pressed = buttonReportedStates[i] == LOW;  // INPUT_PULLUP: LOW = pressed
      if (screens_filter_button(i, pressed, now)) pcLink.sendButton(i, pressed, now);
#if defined(TRANSPORT_LOCAL_DEBUG)
      Serial.printf("[BUTTON] idx=%d  state=%s\n", i, pressed ? "PRESSED" : "RELEASED");
#endif
    }
  }

  // 3. Receive: drain everything waiting (spec §3.5), frame by frame into the link.
  uint8_t buf[256];
  int n;
  while ((n = transport_receive(buf, sizeof buf)) > 0)
    for (int i = 0; i < n; i++)
      if (parser.feed(buf[i])) pcLink.onPayload(parser.payload(), parser.length(), now);

  // 4. Link: keepalive, host timeout, Commit fallback.
  pcLink.tick(now);

  // 5. Screens: power state, then redraw at most one panel.
  screens_tick(now);

  // 6. Connection.
  if (now - lastReconnectTime >= RECONNECT_INTERVAL_MS) {
    lastReconnectTime = now;
    if (!transport_connected()) {
      DEBUG_LOG("[DBG] Reconnecting...");
      transport_reconnect();
    }
  }
}

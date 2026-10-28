#include <Arduino.h>

#include "config.h"
#include "config_session.h"
#include "config_store.h"
#include "debug_log.h"
#include "indicators.h"
#include "pin_helpers.h"
#include "screens.h"
#include "slider_ends_store.h"
#include "transport.h"
#include "volco_calibration.h"
#include "volco_protocol.h"
#include "volco_runtime.h"
#include "volco_wifi_fallback.h"

// The firmware build id Hello reports (spec §9.2); CI sets it, local builds are "dev".
#ifndef VOLCO_FW_VERSION
#define VOLCO_FW_VERSION "dev"
#endif
static_assert(sizeof(VOLCO_FW_VERSION) - 1 <= volco::kMaxFirmwareId, "VOLCO_FW_VERSION is longer than 32 bytes");
static_assert(TRANSPORT_UDP == 1, "volco_wifi_fallback.cpp's kTransportUdp must match TRANSPORT_UDP");

// ============================================================
// State. Everything about the hardware comes from the stored config (config_store).
// ============================================================
static LoadedConfig loaded;
static const volco::RuntimeConfig& rc = loaded.rc;

static uint16_t lastSliderValues[volco::kMaxSliders];
static float smoothedValues[volco::kMaxSliders] = {0};
static unsigned long lastSliderReadTime = 0;
// Each slider's calibration (docs/design/slider-calibration.md): what is sent is the reading
// stretched between its learned ends.
static volco::SliderCalibration calibrations[volco::kMaxSliders];
static unsigned long lastEndsSaveTime = 0;
static bool endsSaveNow = false;  // a sweep ended: store without waiting for SLIDER_ENDS_SAVE_MS

static bool buttonRawStates[volco::kMaxButtons];
static bool buttonReportedStates[volco::kMaxButtons];
static unsigned long buttonChangeTime[volco::kMaxButtons];

// What Hello reports (wire values: 0 none, 1 LED, 2 LED pair, 3 screen).
static uint8_t kinds[volco::kMaxButtons];
// The PC's last LED value per button: shown again when a PinDrive (Try the lights) ends.
static uint8_t ledValues[volco::kMaxButtons];

static volco::ActiveTransport active;  // spec §5.5
static volco::FrameParser parsers[2];  // one per transport, so their bytes never mix
static volco::WifiFallback fallback;   // new Wi-Fi settings: confirmed by the PC over UDP, or the old ones return

static volco::Link* pcLink = nullptr;

// A PinDrive owns the pin until it ends: the LED shows the PC's value only once neither pin is driven.
static bool ledDriven(const volco::RuntimeButton& b) {
  if (b.kind == volco::KIND_LED) return pin_helpers_driving(b.pinA);
  if (b.kind == volco::KIND_LED_PAIR) return pin_helpers_driving(b.pinA) || pin_helpers_driving(b.pinB);
  return false;
}

static void showLed(uint8_t button) {
  if (!ledDriven(rc.buttons[button])) showIndicator(rc.buttons[button], ledValues[button]);
}

// A drive on a configured LED pin ended: its button shows the PC's latest value again.
static void ledRestored(uint8_t pin) {
  for (uint8_t i = 0; i < rc.buttonCount; i++) {
    const volco::RuntimeButton& b = rc.buttons[i];
    const bool owns = (b.kind == volco::KIND_LED && b.pinA == pin) ||
                      (b.kind == volco::KIND_LED_PAIR && (b.pinA == pin || b.pinB == pin));
    if (owns) showLed(i);
  }
}

static void reportPinActivity(uint8_t pin, uint32_t now) { pcLink->sendPinActivity(pin, now); }

// What a slider sends: its reading stretched between the learned ends, mirrored when reversed.
static uint16_t sent(uint8_t i, uint16_t v) { return volco::sliderOut(calibrations[i].map(v), rc.sliderReverse[i]); }

static void sendCalibration(uint8_t i, uint32_t now) {
  const volco::SliderCalibration& c = calibrations[i];
  pcLink->sendCalibration(i, c.flags(), c.lo(), c.hi(), now);
}

// A sweep ended (any way): its report, then the position stretched by the final ends, whatever the
// deadband says; and the ends are stored at once.
static void finishSweep(uint8_t i, uint32_t now) {
  calibrations[i].reportDue(now);  // consumed: the report goes out here
  sendCalibration(i, now);
  lastSliderValues[i] = (uint16_t)smoothedValues[i];
  pcLink->sendSlider(i, sent(i, lastSliderValues[i]), now);
  endsSaveNow = true;
}

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
  void applyLed(uint8_t button, uint8_t value) override {
    ledValues[button] = value;
    showLed(button);
  }
  void applyScreen(uint8_t button, uint8_t transition, const volco::ScreenContent& c, uint32_t now) override {
    screens_show(button, transition, c, now);
  }
  void hostLost() override {
    DEBUG_LOG("[DBG] PC silent: LEDs off, screens show 'not connected'");
    active.onHostLost();
    for (uint8_t i = 0; i < rc.buttonCount; i++) applyLed(i, 0);
    screens_host_lost(millis());
    for (uint8_t i = 0; i < rc.sliderCount; i++) calibrations[i].hostLost();  // a sweep ends as a Cancel
    pin_helpers_host_lost();
  }
  // Sent after every Hello (PR #68). The deadband then measures from the value just sent.
  uint16_t sliderValue(uint8_t i) override {
    lastSliderValues[i] = (uint16_t)smoothedValues[i];
    return sent(i, lastSliderValues[i]);
  }
  // Every Calibrate for a slider that exists is answered with its Calibration.
  void calibrate(uint8_t i, uint8_t action, uint32_t now) override {
    if (i >= rc.sliderCount) return;
    volco::SliderCalibration& c = calibrations[i];
    if (action == volco::CALIBRATE_START) c.startSweep(now);
    else if (action == volco::CALIBRATE_DONE) c.done(now);
    else if (action == volco::CALIBRATE_CANCEL) c.cancel();
    if (c.sweepEnded()) {
      finishSweep(i, now);
    } else {
      c.reportDue(now);  // this answer is the report
      sendCalibration(i, now);
    }
  }
  // Firmware helpers: Detect, Find screens and Try the lights (pin_helpers.cpp).
  void pinWatch(uint8_t role, uint32_t now) override { pin_helpers_watch(role, now); }
  // The one blocking call: a bounded scan (stops at the first timeout), answered at once.
  void i2cScan(uint8_t sda, uint8_t scl, uint32_t /*now*/) override {
    uint8_t addresses[volco::kMaxScanAddresses];
    const uint8_t count = pin_helpers_scan(sda, scl, addresses);
    pcLink->sendI2cScanResult(sda, scl, count, addresses, millis());
  }
  void pinDrive(uint8_t pin, uint8_t level, uint16_t durationMs, uint32_t now) override {
    pin_helpers_drive(pin, level, durationMs, now);
  }
};

static HardwareSink sink;

// Spec §5.5 exception: whenever Wi-Fi connects, Hello goes out on UDP even if serial is latched,
// followed by every slider's position like any Hello.
static void sendHelloOnUdp() {
  uint8_t f[volco::kMaxHelloFrame];
  const volco::HelloInfo h{1, VOLCO_FW_VERSION, (uint8_t)loaded.state, loaded.crc, rc.sliderCount, rc.buttonCount, kinds};
  transport_send(TRANSPORT_UDP, f, volco::encodeHello(h, f));
  for (uint8_t i = 0; i < rc.sliderCount; i++)
    transport_send(TRANSPORT_UDP, f, volco::encodeSlider(i, sink.sliderValue(i), f));
}

// New Wi-Fi settings (docs/design/hardware-page.md §10.3): a UDP frame from the PC confirms them;
// none within WIFI_FALLBACK_MS of boot puts the kept ones back, stored, and reboots on them.
static void runFallback(uint32_t now) {
  switch (fallback.tick(now)) {
    case volco::WifiFallback::CONFIRM:
      if (config_store_drop_wifi()) DEBUG_LOG("[DBG] Wi-Fi: new settings confirmed over UDP");
      else DEBUG_LOG("[DBG] Wi-Fi: new settings confirmed over UDP, but the kept copy could not be dropped");
      break;
    case volco::WifiFallback::RESTORE:
      if (config_store_restore_wifi()) {
        DEBUG_LOG("[DBG] Wi-Fi: no PC over UDP, restored the previous settings, restarting");
        Serial.flush();
        delay(200);
        ESP.restart();
      }
      // Not restarted: a copy that couldn't be dropped would restore again after the next boot.
      DEBUG_LOG("[DBG] Wi-Fi: no PC over UDP, the previous settings could not be restored (or the kept copy "
                "dropped); keeping these, no restart");
      break;
    case volco::WifiFallback::NONE:
      break;
  }
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

  // Each slider's calibration: its Auto-calibrate setting, then its stored ends.
  for (uint8_t i = 0; i < rc.sliderCount; i++) calibrations[i].configure(rc.sliderAutoCalibrate[i]);
  slider_ends_load(rc, calibrations);
  for (uint8_t i = 0; i < rc.sliderCount; i++)
    DEBUG_LOG("[DBG] Slider " + String(i) + " ends: " +
              (calibrations[i].learned() ? String(calibrations[i].lo()) + "-" + String(calibrations[i].hi())
                                         : String("not learned")) +
              (calibrations[i].autoCalibrate() ? ", auto" : ", manual"));

  // Seed from an average, not one noisy read: Hello reports these positions right away.
  analogReadResolution(12);
  for (uint8_t i = 0; i < rc.sliderCount; i++) {
    uint32_t sum = 0;
    for (uint8_t r = 0; r < SLIDER_SEED_READS; r++) sum += analogRead(rc.sliderPins[i]);
    lastSliderValues[i] = sum / SLIDER_SEED_READS;
    smoothedValues[i] = lastSliderValues[i];
    calibrations[i].reading(lastSliderValues[i], millis());
  }

  pin_helpers_begin(rc);  // after the pins are set: the helpers only borrow free and LED pins
  transport_begin_udp(rc.udp);
  screens_init(rc, millis());
  static volco::Link link({rc.sliderCount, rc.buttonCount, kinds, HOST_TIMEOUT_MS, KEEPALIVE_MS, COMMIT_FALLBACK_MS,
                           1, VOLCO_FW_VERSION, (uint8_t)loaded.state, loaded.crc},
                          sink);
  pcLink = &link;
  config_session_begin(link);
  // A kept copy is only valid next to a stored config with Wi-Fi on: a power cut between a Wi-Fi-off
  // save and the copy's drop leaves one behind, which must not splice the old Wi-Fi back in later.
  bool provisional = config_store_wifi_provisional();
  if (provisional && (loaded.state != CONFIG_STORED || !config_store_wifi_enabled())) {
    DEBUG_LOG(config_store_drop_wifi() ? "[DBG] Wi-Fi: dropped a kept copy left by an interrupted save"
                                       : "[DBG] Wi-Fi: a kept copy left by an interrupted save could not be dropped");
    provisional = false;
  }
  fallback.begin(provisional, millis(), WIFI_FALLBACK_MS);
  if (fallback.provisional()) DEBUG_LOG("[DBG] Wi-Fi: new settings, waiting for the PC over UDP");
  pcLink->begin(millis());  // Hello: the ESP32 may have just reset because the PC opened the port
  DEBUG_LOG("[DBG] Setup complete");
}

// ============================================================
// Main loop: sliders → buttons → receive → link → screens → Wi-Fi. Never blocks for long.
// ============================================================
void loop() {
  const uint32_t now = millis();

  // 1. Sliders: EMA smoothing, the calibration learns from it, then a deadband on the reading.
  //    What is sent is the reading stretched between the learned ends.
  if (now - lastSliderReadTime >= rc.input.sliderIntervalMs) {
    lastSliderReadTime = now;
    for (uint8_t i = 0; i < rc.sliderCount; i++) {
      uint16_t raw = analogRead(rc.sliderPins[i]);
      smoothedValues[i] = raw * rc.input.filterWeight + smoothedValues[i] * (1.0f - rc.input.filterWeight);
      uint16_t filtered = (uint16_t)smoothedValues[i];
      calibrations[i].reading(filtered, now);
      int16_t delta = (int16_t)filtered - (int16_t)lastSliderValues[i];
      // Auto-calibrate just learned the span: the stretched position goes out at once.
      if (calibrations[i].becameLearned() || abs(delta) > rc.input.sliderDeadband) {
        lastSliderValues[i] = filtered;
        screens_activity(now);
        pcLink->sendSlider(i, sent(i, filtered), now);
      }
    }
  }

  // 1b. Calibration: the 60 s sweep timeout, a sweep's end, and a report when the ends widened.
  bool sweeping = false;
  for (uint8_t i = 0; i < rc.sliderCount; i++) {
    calibrations[i].tick(now);
    if (calibrations[i].sweepEnded()) finishSweep(i, now);
    else if (calibrations[i].reportDue(now)) sendCalibration(i, now);
    sweeping |= calibrations[i].sweeping();
  }

  // 1c. Store the learned ends when one moved: at most every SLIDER_ENDS_SAVE_MS (flash wear), at
  //     once when a sweep ended, and never during a sweep (storage keeps the ends a Cancel restores).
  if (!sweeping && (endsSaveNow || now - lastEndsSaveTime >= SLIDER_ENDS_SAVE_MS)) {
    bool dirty = false;
    for (uint8_t i = 0; i < rc.sliderCount; i++) dirty |= calibrations[i].needsSave();
    endsSaveNow = false;
    if (dirty) {
      lastEndsSaveTime = now;
      if (slider_ends_save(rc, calibrations)) {
        for (uint8_t i = 0; i < rc.sliderCount; i++) {
          calibrations[i].markSaved();
          DEBUG_LOG("[DBG] Slider " + String(i) + " ends saved: " +
                    (calibrations[i].learned() ? String(calibrations[i].lo()) + "-" + String(calibrations[i].hi())
                                               : String("not learned")));
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

  // 2b. Wiring helpers: the pin watch's samples and reports, the end of each PinDrive.
  pin_helpers_tick(now, reportPinActivity, ledRestored);

  // 3. Receive: drain each transport, frame by frame. Latch before the link replies (spec §5.5).
  uint8_t buf[256];
  for (uint8_t t = 0; t < 2; t++) {
    int n;
    while ((n = transport_receive(t, buf, sizeof buf)) > 0)
      for (int i = 0; i < n; i++)
        if (parsers[t].feed(buf[i])) {
          active.onValidFrame(t);
          fallback.onValidFrame(t);
          runFallback(now);  // confirmed before the payload: a config write in it sees proven settings
          pcLink->onPayload(parsers[t].payload(), parsers[t].length(), now, t);
        }
  }

  // 4. Link: keepalive, host timeout, Commit fallback; the config write timeout.
  pcLink->tick(now);
  config_session_tick(now);

  // 5. Screens: power state, then redraw at most one panel.
  screens_tick(now);

  // 6. Wi-Fi: retry, and Hello on UDP each time it connects. Then the fallback's window.
  if (transport_maintain(now)) sendHelloOnUdp();
  runFallback(now);
}

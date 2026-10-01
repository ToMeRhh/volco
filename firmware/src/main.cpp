#include <Arduino.h>

#include "config.h"

// ============================================================
// Conditional includes for transport
// ============================================================
#if defined(TRANSPORT_WEBSOCKET)
#include <WebSocketsClient.h>
#include <WiFi.h>
#elif defined(TRANSPORT_UDP)
#include <WiFi.h>
#include <WiFiUdp.h>
#elif defined(TRANSPORT_SERIAL)
// Import the WiFi library so we can explicitly disable WiFi and turn off the
// WiFi radio.
#include <WiFi.h>
#endif

// ============================================================
// Debug logging — always enabled, shares serial port when
// using TRANSPORT_SERIAL. Protocol parser on PC skips non-0xAA
// bytes so ASCII log lines are harmlessly discarded.
// ============================================================
#define DEBUG_LOG(msg) Serial.println(msg)

// ============================================================
// Protocol constants
// ============================================================
static const uint8_t START_MARKER = 0xAA;
static const uint8_t MSG_SLIDER = 0x01;           // controller -> PC
static const uint8_t MSG_BUTTON = 0x02;           // controller -> PC
static const uint8_t MSG_BUTTON_FEEDBACK = 0x05;  // PC -> controller

// ============================================================
// Table sizes (the tables themselves are in config.h)
// ============================================================
static constexpr uint8_t NUM_SLIDERS = sizeof(SLIDER_PINS) / sizeof(SLIDER_PINS[0]);
static constexpr uint8_t NUM_BUTTONS = sizeof(BUTTONS) / sizeof(BUTTONS[0]);

// ============================================================
// State
// ============================================================

// Sliders
static uint16_t lastSliderValues[NUM_SLIDERS];
static unsigned long lastSliderReadTime = 0;
static float smoothedValues[NUM_SLIDERS] = {0};

// Buttons (debounce)
static bool buttonRawStates[NUM_BUTTONS];            // last raw reading
static bool buttonReportedStates[NUM_BUTTONS];       // last state sent to PC
static unsigned long buttonChangeTime[NUM_BUTTONS];  // when raw state changed

// Connection
static unsigned long lastReconnectTime = 0;

// ============================================================
// Receive parser state machine
// ============================================================
enum RxState { WAIT_START, WAIT_LENGTH, WAIT_PAYLOAD, WAIT_CHECKSUM };
static RxState rxState = WAIT_START;
static uint8_t rxPayload[32];
static uint8_t rxExpectedLen = 0;
static uint8_t rxPayloadIdx = 0;

// ============================================================
// Transport abstraction
// ============================================================

#if defined(TRANSPORT_SERIAL)

void transport_init() {
  WiFi.disconnect(true, true);
  WiFi.mode(WIFI_OFF);
  Serial.begin(SERIAL_BAUD);
}

bool transport_connected() { return (bool)Serial; }

void transport_send(const uint8_t* data, size_t len) {
  Serial.write(data, len);
}

int transport_receive(uint8_t* buf, size_t maxLen) {
  int available = Serial.available();
  if (available <= 0) return 0;
  size_t toRead = min((size_t)available, maxLen);
  return Serial.readBytes(buf, toRead);
}

void transport_reconnect() {
  // No-op — USB manages the serial connection
}

#elif defined(TRANSPORT_WEBSOCKET)

static WebSocketsClient wsClient;
static uint8_t wsBuf[256];
static volatile size_t wsBufLen = 0;

static void wsEvent(WStype_t type, uint8_t* payload, size_t length) {
  switch (type) {
    case WStype_CONNECTED:
      DEBUG_LOG("[DBG] WebSocket connected");
      break;
    case WStype_DISCONNECTED:
      DEBUG_LOG("[DBG] WebSocket disconnected");
      break;
    case WStype_BIN:
      if (wsBufLen + length <= sizeof(wsBuf)) {
        memcpy(wsBuf + wsBufLen, payload, length);
        wsBufLen += length;
      }
      break;
    default:
      break;
  }
}

void transport_init() {
  Serial.begin(SERIAL_BAUD);  // for debug logging
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  DEBUG_LOG("[DBG] Connecting to WiFi...");
  while (WiFi.status() != WL_CONNECTED) {
    delay(100);
  }
  DEBUG_LOG("[DBG] WiFi connected: " + WiFi.localIP().toString());
  wsClient.begin(SERVER_IP, SERVER_PORT, "/");
  wsClient.onEvent(wsEvent);
  wsClient.setReconnectInterval(RECONNECT_INTERVAL_MS);
}

bool transport_connected() { return wsClient.isConnected(); }

void transport_send(const uint8_t* data, size_t len) {
  wsClient.sendBIN(data, len);
}

int transport_receive(uint8_t* buf, size_t maxLen) {
  wsClient.loop();  // drive the WebSocket event pump
  if (wsBufLen == 0) return 0;
  size_t toRead = min(wsBufLen, maxLen);
  memcpy(buf, wsBuf, toRead);
  if (toRead < wsBufLen) {
    memmove(wsBuf, wsBuf + toRead, wsBufLen - toRead);
  }
  wsBufLen -= toRead;
  return (int)toRead;
}

void transport_reconnect() {
  // WebSocketsClient handles reconnection internally
}

#elif defined(TRANSPORT_UDP)

static WiFiUDP udpClient;

void transport_init() {
  Serial.begin(SERIAL_BAUD);  // for debug logging
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  DEBUG_LOG("[DBG] Connecting to WiFi...");
  while (WiFi.status() != WL_CONNECTED) {
    delay(100);
  }
  DEBUG_LOG("[DBG] WiFi connected: " + WiFi.localIP().toString());
  udpClient.begin(UDP_LOCAL_PORT);
  DEBUG_LOG("[DBG] UDP listening on port " + String(UDP_LOCAL_PORT));
}

bool transport_connected() { return WiFi.status() == WL_CONNECTED; }

void transport_send(const uint8_t* data, size_t len) {
  udpClient.beginPacket(SERVER_IP, SERVER_PORT);
  udpClient.write(data, len);
  udpClient.endPacket();
}

int transport_receive(uint8_t* buf, size_t maxLen) {
  int packetSize = udpClient.parsePacket();
  if (packetSize <= 0) return 0;
  return udpClient.read(buf, min((size_t)packetSize, maxLen));
}

void transport_reconnect() {
  if (WiFi.status() != WL_CONNECTED) {
    DEBUG_LOG("[DBG] WiFi reconnecting...");
    WiFi.reconnect();
  }
}

#elif defined(TRANSPORT_LOCAL_DEBUG)

void transport_init() { Serial.begin(SERIAL_BAUD); }

bool transport_connected() { return true; }

void transport_send(const uint8_t* data, size_t len) {
  // No host — discard outbound frames silently
  (void)data;
  (void)len;
}

int transport_receive(uint8_t* buf, size_t maxLen) {
  // No host — nothing to receive
  (void)buf;
  (void)maxLen;
  return 0;
}

void transport_reconnect() {}

#else
#error \
    "Define exactly one of TRANSPORT_SERIAL, TRANSPORT_WEBSOCKET, TRANSPORT_UDP, or TRANSPORT_LOCAL_DEBUG in config.h"
#endif

// ============================================================
// Protocol: send framed messages
// ============================================================

static void sendFrame(const uint8_t* payload, uint8_t payloadLen) {
  uint8_t frame[payloadLen + 3];  // marker + length + payload + checksum
  frame[0] = START_MARKER;
  frame[1] = payloadLen;
  uint8_t checksum = 0;
  for (uint8_t i = 0; i < payloadLen; i++) {
    frame[2 + i] = payload[i];
    checksum ^= payload[i];
  }
  frame[payloadLen + 2] = checksum;
  transport_send(frame, payloadLen + 3);
}

static void sendSlider(uint8_t index, uint16_t value) {
  uint8_t payload[] = {MSG_SLIDER, index, (uint8_t)(value >> 8),
                       (uint8_t)(value & 0xFF)};
  sendFrame(payload, sizeof(payload));
}

static void sendButton(uint8_t index, uint8_t state) {
  uint8_t payload[] = {MSG_BUTTON, index, state};
  sendFrame(payload, sizeof(payload));
}

// ============================================================
// Indicators: show a button's feedback value
// ============================================================

static void writeLed(uint8_t pin, bool lit, bool activeLow) {
  digitalWrite(pin, (lit != activeLow) ? HIGH : LOW);
}

// value 0 = off/unknown, 1..N = on, or choice N. What that means (muted,
// output A, preset 2, ...) is the PC's business; this only renders it.
static void showIndicator(const Indicator& ind, uint8_t value) {
  switch (ind.kind) {
    case IND_KIND_LED:
      writeLed(ind.pinA, value != 0, ind.activeLow);
      break;
    case IND_KIND_LED_PAIR:
      writeLed(ind.pinA, value == 1, ind.activeLow);
      writeLed(ind.pinB, value == 2, ind.activeLow);
      break;
    case IND_KIND_NONE:
      break;
  }
}

static void initIndicator(const Indicator& ind) {
  switch (ind.kind) {
    case IND_KIND_LED_PAIR:
      pinMode(ind.pinB, OUTPUT);
      [[fallthrough]];  // pin A is set up like a single LED
    case IND_KIND_LED:
      pinMode(ind.pinA, OUTPUT);
      break;
    case IND_KIND_NONE:
      break;
  }
  // Start dark. "Off" is a different pin level per polarity, which is why
  // showIndicator, not a plain digitalWrite, sets it: driving an active-low
  // LED LOW here would show it lit until the PC pushes real state.
  showIndicator(ind, 0);
}

// ============================================================
// Protocol: receive and parse incoming frames
// ============================================================

static void handleParsedMessage(const uint8_t* payload, uint8_t len) {
  if (len < 1) return;
  uint8_t type = payload[0];

  if (type == MSG_BUTTON_FEEDBACK && len >= 3) {
    uint8_t button = payload[1];
    uint8_t value = payload[2];
    if (button < NUM_BUTTONS) {
      showIndicator(BUTTONS[button].indicator, value);
      DEBUG_LOG("[DBG] Feedback btn " + String(button) + " = " + String(value));
    }
  }
  // Any other type is not for the controller (or is newer than this
  // firmware): ignore it.
}

static void processReceivedByte(uint8_t b) {
  switch (rxState) {
    case WAIT_START:
      if (b == START_MARKER) {
        rxState = WAIT_LENGTH;
      }
      break;

    case WAIT_LENGTH:
      rxExpectedLen = b;
      rxPayloadIdx = 0;
      if (rxExpectedLen == 0 || rxExpectedLen > sizeof(rxPayload)) {
        rxState = WAIT_START;  // invalid length, reset
      } else {
        rxState = WAIT_PAYLOAD;
      }
      break;

    case WAIT_PAYLOAD:
      rxPayload[rxPayloadIdx++] = b;
      if (rxPayloadIdx >= rxExpectedLen) {
        rxState = WAIT_CHECKSUM;
      }
      break;

    case WAIT_CHECKSUM: {
      uint8_t checksum = 0;
      for (uint8_t i = 0; i < rxExpectedLen; i++) {
        checksum ^= rxPayload[i];
      }
      if (checksum == b) {
        handleParsedMessage(rxPayload, rxExpectedLen);
      } else {
        DEBUG_LOG("[DBG] Bad checksum");
      }
      rxState = WAIT_START;
      break;
    }
  }
}

// ============================================================
// Setup
// ============================================================

void setup() {
  // Slider pins — input (ADC)
  for (int i = 0; i < NUM_SLIDERS; i++) {
    pinMode(SLIDER_PINS[i], INPUT);
  }

  // Button pins — input with pull-up (LOW = pressed); indicators start off.
  for (uint8_t i = 0; i < NUM_BUTTONS; i++) {
    pinMode(BUTTONS[i].pin, INPUT_PULLUP);
    buttonRawStates[i] = HIGH;
    buttonReportedStates[i] = HIGH;
    buttonChangeTime[i] = 0;
    initIndicator(BUTTONS[i].indicator);
  }

  // 12-bit ADC resolution
  analogReadResolution(12);

  // Seed slider values so we don't send spurious changes on startup
  for (int i = 0; i < NUM_SLIDERS; i++) {
    lastSliderValues[i] = analogRead(SLIDER_PINS[i]);
  }

  transport_init();
#if defined(TRANSPORT_LOCAL_DEBUG)
  DEBUG_LOG(
      "[DBG] *** LOCAL DEBUG MODE — no host connection, verbose event logging "
      "***");
#endif
  DEBUG_LOG("[DBG] Setup complete");
}

// ============================================================
// Main loop
// ============================================================

void loop() {
  unsigned long now = millis();

  // --- 1. Read sliders (gated by interval) ---
  if (now - lastSliderReadTime >= SLIDER_READ_INTERVAL_MS) {
    lastSliderReadTime = now;
    for (int i = 0; i < NUM_SLIDERS; i++) {
      // 1. Get raw reading
      uint16_t rawValue = analogRead(SLIDER_PINS[i]);

      // 2. Exponential Moving Average (EMA) - Non-blocking smoothing
      // Formula: Smooth = (Current * Weight) + (Previous * (1 - Weight))
      smoothedValues[i] = (rawValue * FILTER_WEIGHT) +
                          (smoothedValues[i] * (1.0 - FILTER_WEIGHT));

      // 3. Cast to integer for comparison
      uint16_t currentFilteredValue = (uint16_t)smoothedValues[i];

      // 4. Deadband check using the smoothed value
      int16_t delta =
          (int16_t)currentFilteredValue - (int16_t)lastSliderValues[i];

      if (abs(delta) > SLIDER_DEADBAND) {
        lastSliderValues[i] = currentFilteredValue;
        sendSlider(i, currentFilteredValue);

#if defined(TRANSPORT_LOCAL_DEBUG)
        Serial.printf("[SLIDER] idx=%d  filtered=%d  delta=%d\n", i,
                      currentFilteredValue, delta);
#endif
      }
    }
  }

  // --- 2. Read buttons (with debounce) ---
  for (uint8_t i = 0; i < NUM_BUTTONS; i++) {
    bool currentRaw = digitalRead(BUTTONS[i].pin);

    // Track when the raw state last changed
    if (currentRaw != buttonRawStates[i]) {
      buttonRawStates[i] = currentRaw;
      buttonChangeTime[i] = now;
    }

    // After stable for DEBOUNCE_MS, report if different from last reported
    if ((now - buttonChangeTime[i] >= DEBOUNCE_MS) &&
        (buttonRawStates[i] != buttonReportedStates[i])) {
      buttonReportedStates[i] = buttonRawStates[i];
      // INPUT_PULLUP: LOW = pressed, HIGH = released
      bool pressed = (buttonReportedStates[i] == LOW);
      sendButton(i, pressed ? 0x01 : 0x00);
#if defined(TRANSPORT_LOCAL_DEBUG)
      Serial.print("[BUTTON] idx=");
      Serial.print(i);
      Serial.println(pressed ? "  state=PRESSED" : "  state=RELEASED");
#endif
    }
  }

  // --- 3. Receive incoming messages ---
  uint8_t buf[64];
  int bytesRead = transport_receive(buf, sizeof(buf));
  for (int i = 0; i < bytesRead; i++) {
    processReceivedByte(buf[i]);
  }

  // --- 4. Connection management ---
  if (now - lastReconnectTime >= RECONNECT_INTERVAL_MS) {
    lastReconnectTime = now;
    if (!transport_connected()) {
      DEBUG_LOG("[DBG] Reconnecting...");
      transport_reconnect();
    }
  }
}

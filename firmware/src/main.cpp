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
static const uint8_t MSG_SLIDER = 0x01;
static const uint8_t MSG_BUTTON = 0x02;
static const uint8_t MSG_MUTE_STATE = 0x03;
static const uint8_t MSG_ACTIVE_DEVICE = 0x04;

// ============================================================
// Pin tables
// ============================================================
static const uint8_t sliderPins[NUM_SLIDERS] = {
    SLIDER_0_PIN, SLIDER_1_PIN, SLIDER_2_PIN, SLIDER_3_PIN, SLIDER_4_PIN};

static const uint8_t buttonPins[] = {MUTE_BTN_0_PIN, MUTE_BTN_1_PIN,
                                     DEVICE_SEL_BTN_PIN};
static const uint8_t NUM_BUTTONS = sizeof(buttonPins) / sizeof(buttonPins[0]);

static const uint8_t muteLedPins[NUM_MUTE_BUTTONS] = {MUTE_BTN_0_LED_PIN,
                                                      MUTE_BTN_1_LED_PIN};

// ============================================================
// State
// ============================================================

// Sliders
static uint16_t lastSliderValues[NUM_SLIDERS];
static unsigned long lastSliderReadTime = 0;
static float smoothedValues[NUM_SLIDERS] = {0};

// Buttons (debounce)
static bool buttonRawStates[3];            // last raw reading per button
static bool buttonReportedStates[3];       // last state sent to server
static unsigned long buttonChangeTime[3];  // when raw state last changed

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
// Protocol: receive and parse incoming frames
// ============================================================

static void handleParsedMessage(const uint8_t* payload, uint8_t len) {
  if (len < 2) return;
  uint8_t type = payload[0];

  if (type == MSG_MUTE_STATE && len >= 3) {
    uint8_t btnIndex = payload[1];
    bool muted = payload[2] != 0;
    if (btnIndex < NUM_MUTE_BUTTONS) {
      digitalWrite(muteLedPins[btnIndex], muted ? LOW : HIGH);
      DEBUG_LOG("[DBG] Mute btn " + String(btnIndex) + " = " +
                (muted ? "ON" : "OFF"));
    }
  } else if (type == MSG_ACTIVE_DEVICE && len >= 2) {
    uint8_t deviceIndex = payload[1];
    digitalWrite(DEVICE_SEL_LED_0_PIN, deviceIndex == 0 ? HIGH : LOW);
    digitalWrite(DEVICE_SEL_LED_1_PIN, deviceIndex == 1 ? HIGH : LOW);
    DEBUG_LOG("[DBG] Active device = " + String(deviceIndex));
  }
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
    pinMode(sliderPins[i], INPUT);
  }

  // Button pins — input with pull-up (LOW = pressed)
  for (uint8_t i = 0; i < NUM_BUTTONS; i++) {
    pinMode(buttonPins[i], INPUT_PULLUP);
    buttonRawStates[i] = HIGH;
    buttonReportedStates[i] = HIGH;
    buttonChangeTime[i] = 0;
  }

  // LED pins — output, start off.
  //
  // The two LED groups have OPPOSITE polarity, so "off" is a different level for
  // each — don't unify these writes:
  //   - mute LEDs are active-low. The MSG_MUTE_STATE handler drives LOW to light
  //     them for "muted", so HIGH is off.
  //   - device-select LEDs are active-high. MSG_ACTIVE_DEVICE drives HIGH for the
  //     selected device, so LOW is off.
  //
  // This previously drove the mute pins LOW, which lit both of them on every boot
  // and falsely showed "muted" until the host pushed real state — visible for a
  // second or two after every USB re-plug.
  for (int i = 0; i < NUM_MUTE_BUTTONS; i++) {
    pinMode(muteLedPins[i], OUTPUT);
    digitalWrite(muteLedPins[i], HIGH);
  }
  pinMode(DEVICE_SEL_LED_0_PIN, OUTPUT);
  pinMode(DEVICE_SEL_LED_1_PIN, OUTPUT);
  digitalWrite(DEVICE_SEL_LED_0_PIN, LOW);
  digitalWrite(DEVICE_SEL_LED_1_PIN, LOW);

  // 12-bit ADC resolution
  analogReadResolution(12);

  // Seed slider values so we don't send spurious changes on startup
  for (int i = 0; i < NUM_SLIDERS; i++) {
    lastSliderValues[i] = analogRead(sliderPins[i]);
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
      uint16_t rawValue = analogRead(sliderPins[i]);

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
    bool currentRaw = digitalRead(buttonPins[i]);

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

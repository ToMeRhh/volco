#include <Arduino.h>

#include "config.h"
#include "debug_log.h"
#include "transport.h"

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
// Transport implementations, one per TRANSPORT_*
// ============================================================

#if defined(TRANSPORT_SERIAL)

void transport_init() {
  WiFi.disconnect(true, true);
  WiFi.mode(WIFI_OFF);
  // A preset change is a ~560-byte burst and a screen redraw blocks the loop for ~30 ms:
  // the default 256-byte UART buffer could overflow.
  Serial.setRxBufferSize(1024);
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
static uint8_t wsBuf[1024];
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
      } else {
        DEBUG_LOG("[DBG] WebSocket receive buffer full; message dropped");
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
static uint8_t udpPacket[1024];
static size_t udpLen = 0, udpPos = 0;

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
  if (udpPos >= udpLen) {
    int size = udpClient.parsePacket();
    if (size <= 0) return 0;
    if (size > (int)sizeof(udpPacket)) DEBUG_LOG("[DBG] UDP packet larger than the buffer; truncated");
    int r = udpClient.read(udpPacket, sizeof(udpPacket));
    if (r <= 0) {
      udpLen = udpPos = 0;
      return 0;
    }
    udpLen = (size_t)r;
    udpPos = 0;
  }
  size_t n = min(udpLen - udpPos, maxLen);
  memcpy(buf, udpPacket + udpPos, n);
  udpPos += n;
  return (int)n;
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

#include "transport.h"

#include <Arduino.h>
#include <WiFi.h>
#include <WiFiUdp.h>

#include "config.h"
#include "debug_log.h"
#include "volco_protocol.h"

namespace {
volco::RuntimeUdp cfg;
WiFiUDP udp;
uint8_t udpPacket[UDP_BUFFER];
size_t udpLen = 0, udpPos = 0;
bool udpStarted = false, wasConnected = false;
uint32_t lastBegin = 0;

uint32_t toU32(const IPAddress& a) {
  return ((uint32_t)a[0] << 24) | ((uint32_t)a[1] << 16) | ((uint32_t)a[2] << 8) | (uint32_t)a[3];
}
IPAddress pcAddress() {
  return IPAddress((cfg.pcIp >> 24) & 0xFF, (cfg.pcIp >> 16) & 0xFF, (cfg.pcIp >> 8) & 0xFF, cfg.pcIp & 0xFF);
}
bool udpUp() { return cfg.enabled && udpStarted && WiFi.status() == WL_CONNECTED; }
void joinWifi(uint32_t now) {
  WiFi.begin(cfg.ssid, cfg.password[0] ? cfg.password : nullptr);
  lastBegin = now;
}
}  // namespace

void transport_begin_serial() {
  // A preset change is a ~560-byte burst and a config write ~1.1 KB, while a screen redraw blocks
  // the loop for ~30 ms: the default buffers could overflow.
  Serial.setRxBufferSize(SERIAL_RX_BUFFER);
  Serial.setTxBufferSize(SERIAL_TX_BUFFER);
  Serial.begin(SERIAL_BAUD);
}

void transport_begin_udp(const volco::RuntimeUdp& udpConfig) {
  cfg = udpConfig;
  if (!cfg.enabled) {
    WiFi.mode(WIFI_OFF);  // never WiFi.disconnect() before Wi-Fi is up: it logs ESP_ERR_WIFI_NOT_INIT
    return;
  }
  WiFi.persistent(false);  // the driver must not copy credentials into our NVS partition
  WiFi.setAutoReconnect(true);
  WiFi.mode(WIFI_STA);
  joinWifi(millis());
  DEBUG_LOG(String("[DBG] Wi-Fi: joining ") + cfg.ssid);
}

uint8_t transport_up_mask() {
  return (uint8_t)((1u << TRANSPORT_SERIAL) | (udpUp() ? (1u << TRANSPORT_UDP) : 0u));
}

void transport_send(uint8_t id, const uint8_t* data, size_t len) {
  if (id == TRANSPORT_SERIAL) {
    Serial.write(data, len);
  } else if (id == TRANSPORT_UDP && udpUp()) {
    udp.beginPacket(pcAddress(), cfg.pcPort);
    udp.write(data, len);
    udp.endPacket();
  }
}

int transport_receive(uint8_t id, uint8_t* buf, size_t maxLen) {
  if (id == TRANSPORT_SERIAL) {
    int available = Serial.available();
    if (available <= 0) return 0;
    size_t toRead = min((size_t)available, maxLen);
    return Serial.readBytes(buf, toRead);
  }
  if (id != TRANSPORT_UDP || !udpUp()) return 0;
  if (udpPos >= udpLen) {
    int size = udp.parsePacket();
    if (size <= 0) return 0;
    int r = udp.read(udpPacket, sizeof udpPacket);
    if (r <= 0 || !volco::acceptUdpSource(toU32(udp.remoteIP()), cfg.pcIp)) {  // strangers: dropped
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

bool transport_maintain(uint32_t now) {
  if (!cfg.enabled) return false;
  const wl_status_t st = WiFi.status();
  const bool connected = st == WL_CONNECTED;
  bool edge = false;
  if (connected && !wasConnected) {
    DEBUG_LOG("[DBG] Wi-Fi: connected, " + WiFi.localIP().toString());
    edge = true;
  }
  if (connected && !udpStarted) {  // retried every pass, so a failed bind doesn't leave UDP dead
    udpStarted = udp.begin(cfg.localPort);
    if (!udpStarted) DEBUG_LOG("[DBG] UDP: bind failed, retrying");
  }
  // Any state but connected (failed, no SSID, dropped, lost, or associated without an IP) is
  // retried, at most every WIFI_RETRY_MS so a slow attempt isn't interrupted.
  if (!connected && now - lastBegin >= WIFI_RETRY_MS) joinWifi(now);
  wasConnected = connected;
  return edge;
}

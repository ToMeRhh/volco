#include "config_store.h"

#include <Preferences.h>

#include "debug_log.h"
#include "volco_chip.h"
#include "volco_config.h"
#include "volco_crc32.h"
#include "volco_wifi_fallback.h"

namespace {
uint8_t blob[volco::kMaxConfigBlob];
uint16_t blobLen = 0;                   // the stored bytes, kept for ConfigRead replies
bool wifiOn = false;                    // the loaded config's udp.enabled (decoded is reused by a restore)
volco_config_ControllerConfig decoded;  // static: ~1.5 KB kept off the loop task's stack
// The fallback's buffers (static, as above): the kept Udp bytes, and the stored config with them spliced in.
uint8_t keptUdp[volco::kMaxConfigBlob];
uint8_t restored[volco::kMaxConfigBlob];

constexpr const char* kWifiPrevKey = "wifi_prev";

void useDefaults(LoadedConfig& out, ConfigState state, uint32_t crc) {
  decoded = volco_config_ControllerConfig_init_default;
  volco::toRuntime(decoded, out.rc);
  wifiOn = false;
  out.state = state;
  out.crc = crc;
}
}  // namespace

void config_load(LoadedConfig& out) {
  blobLen = 0;
  Preferences prefs;
  prefs.begin("volco", true);
  if (!prefs.isKey("cfg")) {
    prefs.end();
    DEBUG_LOG("[DBG] Config: none stored, running defaults");
    useDefaults(out, CONFIG_NONE, 0);
    return;
  }
  size_t len = prefs.getBytesLength("cfg");
  if (len > volco::kMaxConfigBlob) {
    prefs.end();
    useDefaults(out, CONFIG_DAMAGED, 0);
    return;
  }
  const size_t read = prefs.getBytes("cfg", blob, len);
  prefs.end();
  if (read != len) {  // a read error: don't report a CRC of stale bytes
    useDefaults(out, CONFIG_DAMAGED, 0);
    return;
  }
  blobLen = (uint16_t)len;
  const uint32_t crc = volco::crc32(blob, len);
  if (!volco::decodeConfig(blob, len, decoded) || volco::validateConfig(decoded, volco::esp32Chip()).code != volco::CFG_OK) {
    useDefaults(out, CONFIG_DAMAGED, crc);
    return;
  }
  volco::toRuntime(decoded, out.rc);
  wifiOn = out.rc.udp.enabled;
  out.state = CONFIG_STORED;
  out.crc = crc;
}

const uint8_t* config_store_blob(uint16_t& len) {
  len = blobLen;
  return blob;
}

bool config_store_save(const uint8_t* data, size_t len) {
  Preferences prefs;
  if (!prefs.begin("volco", false)) return false;
  const size_t written = prefs.putBytes("cfg", data, len);  // nvs_set_blob + nvs_commit: atomic per key
  prefs.end();
  return written == len;
}

bool config_store_wifi_enabled() { return wifiOn; }

bool config_store_keep_wifi(const uint8_t* udp, size_t len) {
  Preferences prefs;
  if (!prefs.begin("volco", false)) return false;
  const size_t written = prefs.putBytes(kWifiPrevKey, udp, len);
  prefs.end();
  return written == len;
}

void config_store_drop_wifi() {
  Preferences prefs;
  if (!prefs.begin("volco", false)) return;
  if (prefs.isKey(kWifiPrevKey)) prefs.remove(kWifiPrevKey);
  prefs.end();
}

bool config_store_wifi_provisional() {
  Preferences prefs;
  if (!prefs.begin("volco", true)) return false;
  const bool kept = prefs.isKey(kWifiPrevKey);
  prefs.end();
  return kept;
}

const uint8_t* config_store_kept_wifi(size_t& len) {
  len = 0;
  Preferences prefs;
  if (!prefs.begin("volco", true)) return nullptr;
  const size_t n = prefs.isKey(kWifiPrevKey) ? prefs.getBytesLength(kWifiPrevKey) : 0;
  const bool ok = n > 0 && n <= sizeof keptUdp && prefs.getBytes(kWifiPrevKey, keptUdp, n) == n;
  prefs.end();
  if (!ok) return nullptr;
  len = n;
  return keptUdp;
}

bool config_store_restore_wifi() {
  size_t keptLen;
  const uint8_t* kept = config_store_kept_wifi(keptLen);
  // The stored config as loaded at boot (none, oversized or unreadable: blobLen 0); the rest of it is kept.
  const size_t n = kept && blobLen
                       ? volco::replaceField(blob, blobLen, volco::kUdpField, kept, keptLen, restored, sizeof restored)
                       : 0;
  const bool ok = n && volco::decodeConfig(restored, n, decoded) &&
                  volco::validateConfig(decoded, volco::esp32Chip()).code == volco::CFG_OK &&
                  config_store_save(restored, n);
  config_store_drop_wifi();
  return ok;
}

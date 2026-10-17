#include "config_store.h"

#include <Preferences.h>

#include "debug_log.h"
#include "volco_chip.h"
#include "volco_config.h"
#include "volco_crc32.h"

namespace {
uint8_t blob[volco::kMaxConfigBlob];
uint16_t blobLen = 0;                   // the stored bytes, kept for ConfigRead replies
volco_config_ControllerConfig decoded;  // static: ~1.5 KB kept off the loop task's stack

void useDefaults(LoadedConfig& out, ConfigState state, uint32_t crc) {
  decoded = volco_config_ControllerConfig_init_default;
  volco::toRuntime(decoded, out.rc);
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

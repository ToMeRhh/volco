#include "config_store.h"

#include <Preferences.h>

#include "debug_log.h"
#include "seed_config.h"
#include "volco_config.h"
#include "volco_crc32.h"

namespace {
uint8_t blob[volco::kMaxConfigBlob];
volco_config_ControllerConfig decoded;  // static: ~1.5 KB kept off the loop task's stack

void useDefaults(LoadedConfig& out, ConfigState state, uint32_t crc) {
  decoded = volco_config_ControllerConfig_init_default;
  volco::toRuntime(decoded, out.rc);
  out.state = state;
  out.crc = crc;
}
}  // namespace

void config_load(LoadedConfig& out) {
  Preferences prefs;
  prefs.begin("volco", false);
  if (!prefs.isKey("cfg")) {  // stage 3 bridge (spec §15)
    size_t written = prefs.putBytes("cfg", kSeedConfig, sizeof kSeedConfig);
    DEBUG_LOG(written == sizeof kSeedConfig ? "[DBG] Config: stored the seed" : "[DBG] Config: storing the seed failed");
  }
  if (!prefs.isKey("cfg")) {
    prefs.end();
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
  const uint32_t crc = volco::crc32(blob, len);
  if (!volco::decodeConfig(blob, len, decoded) || volco::validateConfig(decoded, volco::esp32Chip()).code != volco::CFG_OK) {
    useDefaults(out, CONFIG_DAMAGED, crc);
    return;
  }
  volco::toRuntime(decoded, out.rc);
  out.state = CONFIG_STORED;
  out.crc = crc;
}

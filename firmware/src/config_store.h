#pragma once
// The controller config in NVS (spec §6.6): namespace "volco", key "cfg", the exact bytes. Next to it,
// key "wifi_prev": the previous Udp sub-message while the stored Wi-Fi settings are unproven
// (docs/design/hardware-page.md §10.3).
#include <stddef.h>
#include <stdint.h>

#include "volco_runtime.h"

enum ConfigState : uint8_t { CONFIG_NONE = 0, CONFIG_STORED = 1, CONFIG_DAMAGED = 2 };

struct LoadedConfig {
  volco::RuntimeConfig rc;  // what the firmware runs: the stored config, or the built-in defaults
  ConfigState state;
  uint32_t crc;             // CRC-32 of the stored bytes; 0 when none, or longer than kMaxConfigBlob
};

// Boot (spec §6.6): absent -> defaults/NONE; longer than kMaxConfigBlob -> defaults/DAMAGED/crc 0;
// fails decode or validate -> defaults/DAMAGED/crc of the bytes; otherwise STORED.
void config_load(LoadedConfig& out);

// The stored bytes as loaded at boot, for a ConfigRead reply (spec §5.3): len 0 when nothing is
// stored or the stored value is longer than kMaxConfigBlob.
const uint8_t* config_store_blob(uint16_t& len);

// Stores a new blob (already decoded and validated). True when NVS took every byte (spec §6.6).
bool config_store_save(const uint8_t* blob, size_t len);

// Whether the config loaded at boot has Wi-Fi on (false for the built-in defaults).
bool config_store_wifi_enabled();

// Keeps the old Wi-Fi settings (the Udp sub-message bytes, as in the stored blob) before a save that
// changes them. True when NVS took every byte.
bool config_store_keep_wifi(const uint8_t* udp, size_t len);

// Drops the kept copy: the new settings were confirmed, or a save kept none.
void config_store_drop_wifi();

// The kept copy's bytes (valid until the next call), or null when there is none or it can't be read.
const uint8_t* config_store_kept_wifi(size_t& len);

// The stored Wi-Fi settings are provisional: a kept copy exists.
bool config_store_wifi_provisional();

// Puts the kept copy back as the stored config's udp section (the rest unchanged), checks the result
// decodes and validates, saves it and drops the copy. On any failure it drops the copy too and
// returns false: the controller keeps running the stored config.
bool config_store_restore_wifi();

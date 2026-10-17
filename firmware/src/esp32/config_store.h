#pragma once
// The controller config in NVS (spec §6.6): namespace "volco", key "cfg", the exact bytes.
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

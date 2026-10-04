#pragma once
// The controller config in NVS (spec §6.6): namespace "volco", key "cfg", the exact bytes.
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
// Stage 3 only: when nothing is stored, seed_config.h is stored first (removed in stage 4).
void config_load(LoadedConfig& out);

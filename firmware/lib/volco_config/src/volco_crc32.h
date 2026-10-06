#pragma once
// CRC-32 of a config blob (spec §6.5): zlib's crc32 (reflected 0xEDB88320, init/final 0xFFFFFFFF).
#include <stddef.h>
#include <stdint.h>

namespace volco {
uint32_t crc32(const uint8_t* data, size_t len);
}  // namespace volco

#pragma once
// Logical → visual reordering for screen text (spec §4.4): a deliberately small subset of the
// Unicode bidi algorithm, enough for Hebrew device and preset names mixed with Latin and numbers.
#include <stddef.h>
#include <stdint.h>

namespace volco {
// Writes `in` (UTF-8, logical order) to `out` in visual (left-to-right drawing) order.
// Returns the bytes written; `out` is NUL-terminated when cap > that. *rtl = paragraph direction.
size_t bidiVisual(const char* in, size_t len, char* out, size_t cap, bool* rtl);
}  // namespace volco

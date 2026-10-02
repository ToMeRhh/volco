#include "screens.h"

// Placeholder until the screen renderer lands (Task 18): this build has no screens.
void screens_init(uint32_t) {}
void screens_show(uint8_t, uint8_t, const volco::ScreenContent&, uint32_t) {}
void screens_host_lost(uint32_t) {}
void screens_activity(uint32_t) {}
bool screens_filter_button(uint8_t, bool, uint32_t) { return true; }
void screens_tick(uint32_t) {}

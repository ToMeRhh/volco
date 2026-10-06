#pragma once
// The firmware side of the config session (spec §5.3, §5.4, §6.7): answers ConfigRead with the
// stored bytes, receives a config write, then decodes, validates, stores, answers and reboots.
#include <stdint.h>

#include "config_store.h"
#include "volco_protocol.h"

void config_session_begin(volco::Link& link);  // registers the session as the link's ConfigSink
void config_session_tick(uint32_t now);         // every loop pass: the write timeout (spec §5.4 rule 6)

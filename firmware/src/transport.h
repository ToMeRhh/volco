#pragma once
#include <stddef.h>
#include <stdint.h>

#include "volco_runtime.h"

// Two transports (spec §5.5): USB serial, always on, and UDP over Wi-Fi when the config enables
// it. Ids are bit positions in transport_up_mask() and volco::ActiveTransport.
enum TransportId : uint8_t { TRANSPORT_SERIAL = 0, TRANSPORT_UDP = 1 };

void transport_begin_serial();                             // first thing in setup(): logs need it
void transport_begin_udp(const volco::RuntimeUdp& udp);    // Wi-Fi off unless udp.enabled; never blocks
uint8_t transport_up_mask();                               // bit per transport that can send now
void transport_send(uint8_t id, const uint8_t* data, size_t len);
int transport_receive(uint8_t id, uint8_t* buf, size_t maxLen);  // non-blocking; 0 when nothing waits
bool transport_maintain(uint32_t now);  // every loop pass: Wi-Fi retry; true once each time Wi-Fi connects

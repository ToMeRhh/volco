#pragma once
#include <stddef.h>
#include <stdint.h>

// One implementation per TRANSPORT_* (transport.cpp). Nothing else knows which link is in use.
void transport_init();                                // bring the link up (and Serial for logging)
bool transport_connected();                           // whether the link is usable
void transport_send(const uint8_t* data, size_t len); // one frame
int transport_receive(uint8_t* buf, size_t maxLen);   // non-blocking; 0 when nothing is waiting
void transport_reconnect();                           // restore a lost link (no-op where it manages itself)

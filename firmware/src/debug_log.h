#pragma once
#include <Arduino.h>
// Debug lines go to Serial. On TRANSPORT_SERIAL they share the port with frames; the PC's parser
// only acts on valid 0xAA frames, so log lines are harmlessly skipped.
#define DEBUG_LOG(msg) Serial.println(msg)

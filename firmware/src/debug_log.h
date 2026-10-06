#pragma once
#include <Arduino.h>
// Debug lines go to USB serial only. They share the port with frames; the PC's parser only acts on
// valid 0xAA frames, so log lines are harmlessly skipped.
#define DEBUG_LOG(msg) Serial.println(msg)

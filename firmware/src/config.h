#pragma once

#include "hardware.h"

// ============================================================
// Transport — uncomment exactly one
// ============================================================
#define TRANSPORT_SERIAL
// #define TRANSPORT_WEBSOCKET
// #define TRANSPORT_UDP
// #define TRANSPORT_LOCAL_DEBUG  // No host needed. Verbose serial logging
// only.

// ============================================================
// WiFi — only used for TRANSPORT_WEBSOCKET and TRANSPORT_UDP
// ============================================================
#define WIFI_SSID "SSID"
#define WIFI_PASSWORD "PASSWORD"
#define SERVER_IP "192.168.1.10"
#define SERVER_PORT 12345     // WebSocket port or UDP port
#define UDP_LOCAL_PORT 12346  // Local port for receiving UDP from server

// ============================================================
// Serial
// ============================================================
#define SERIAL_BAUD 115200

// ============================================================
// Hardware layout
// ============================================================
//
// The order of each table is the input number the PC sees: the first slider
// is slider 0, the first button is button 0, and so on. The Volco settings
// window lists sliders and buttons in this same order.

// Analog sliders: one ADC pin per slider.
static const uint8_t SLIDER_PINS[] = {33, 35, 32, 36, 34};

// Buttons: { input pin, indicator }.
//   IND_NONE                             no light
//   IND_LED(pin, polarity)               one LED: on for any value except 0
//   IND_LED_PAIR(pinA, pinB, polarity)   two LEDs: 1 lights A, 2 lights B
// polarity is ACTIVE_HIGH (HIGH lights the LED) or ACTIVE_LOW (LOW does).
static const ButtonDef BUTTONS[] = {
    {4, IND_LED(12, ACTIVE_LOW)},
    {14, IND_LED(21, ACTIVE_LOW)},
    {5, IND_LED_PAIR(18, 19, ACTIVE_HIGH)},
};

// ============================================================
// Tuning
// ============================================================
#define DEBOUNCE_MS 30
#define SLIDER_DEADBAND 25          // ADC units: ignore smaller changes
#define SLIDER_READ_INTERVAL_MS 10  // How often to sample sliders (ms)
#define RECONNECT_INTERVAL_MS 3000  // How often to check connection (ms)
#define FILTER_WEIGHT \
  0.2  // Smoothing factor (0.0 to 1.0). Lower = smoother but slower.

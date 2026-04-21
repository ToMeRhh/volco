#pragma once

// ============================================================
// Transport — uncomment exactly one
// ============================================================
#define TRANSPORT_SERIAL
// #define TRANSPORT_WEBSOCKET
// #define TRANSPORT_UDP
// #define TRANSPORT_LOCAL_DEBUG  // No host needed. Verbose serial logging only.

// ============================================================
// WiFi — only used for TRANSPORT_WEBSOCKET and TRANSPORT_UDP
// ============================================================
#define WIFI_SSID "MyNetwork"
#define WIFI_PASSWORD "MyPassword"
#define SERVER_IP "192.168.1.100"
#define SERVER_PORT 8765      // WebSocket port or UDP port
#define UDP_LOCAL_PORT 12346  // Local port for receiving UDP from server

// ============================================================
// Serial
// ============================================================
#define SERIAL_BAUD 115200

// ============================================================
// Pin assignments
// ============================================================

// Analog sliders
#define SLIDER_0_PIN 33
#define SLIDER_1_PIN 35
#define SLIDER_2_PIN 32
#define SLIDER_3_PIN 36
#define SLIDER_4_PIN 34

// Mute buttons + LEDs
#define MUTE_BTN_0_PIN 4
#define MUTE_BTN_0_LED_PIN 12
#define MUTE_BTN_1_PIN 14
#define MUTE_BTN_1_LED_PIN 21

// Device selector button + LEDs
#define DEVICE_SEL_BTN_PIN 5
#define DEVICE_SEL_LED_0_PIN 18
#define DEVICE_SEL_LED_1_PIN 19

// ============================================================
// Tuning
// ============================================================
#define NUM_SLIDERS 5
#define NUM_MUTE_BUTTONS 2
#define DEBOUNCE_MS 30
#define SLIDER_DEADBAND 20  // ADC units — ignore changes smaller than this
#define SLIDER_READ_INTERVAL_MS 5   // How often to sample sliders (ms)
#define RECONNECT_INTERVAL_MS 3000  // How often to check connection (ms)

#pragma once

#include "hardware.h"

// ============================================================
// Transport — uncomment exactly one
// ============================================================
#define TRANSPORT_SERIAL         // USB serial; Wi-Fi off
// #define TRANSPORT_WEBSOCKET   // Wi-Fi, WebSocket client of the app (SERVER_IP:SERVER_PORT)
// #define TRANSPORT_UDP         // Wi-Fi, UDP to the app, listening on UDP_LOCAL_PORT
// #define TRANSPORT_LOCAL_DEBUG // no PC: frames dropped, input logged to serial

// ============================================================
// Wi-Fi — only used for TRANSPORT_WEBSOCKET and TRANSPORT_UDP
// ============================================================
#define WIFI_SSID "SSID"          // network name
#define WIFI_PASSWORD "PASSWORD"  // network password
#define SERVER_IP "192.168.1.10"  // the PC running Volco
#define SERVER_PORT 12345         // WebSocket port or UDP port on the PC
#define UDP_LOCAL_PORT 12346      // port this controller listens on for UDP from the PC

// ============================================================
// Serial
// ============================================================
#define SERIAL_BAUD 115200  // must match the baud rate on Volco's Connection page

// ============================================================
// Hardware layout
// ============================================================
//
// The order of each table is the input number the PC sees: the first slider is slider 0, the
// first button is button 0, and so on. The Volco settings window lists them in the same order.

// Analog sliders: one ADC pin per slider.
static constexpr uint8_t SLIDER_PINS[] = {33, 35, 32, 36, 34};

// Buttons: { input pin, indicator }.
//   IND_NONE                             nothing to show
//   IND_LED(pin, polarity)               one LED: on for any value except 0
//   IND_LED_PAIR(pinA, pinB, polarity)   two LEDs: 1 lights A, 2 lights B
//   IND_SCREEN(bus, address)             an SSD1306 screen: bus 0 or 1, address 0x3C or 0x3D
// polarity is ACTIVE_HIGH (HIGH lights the LED) or ACTIVE_LOW (LOW does).
static constexpr ButtonDef BUTTONS[] = {
    {4, IND_LED(12, ACTIVE_LOW)},
    {14, IND_LED(21, ACTIVE_LOW)},
    {5, IND_LED_PAIR(18, 19, ACTIVE_HIGH)},
};

// ============================================================
// Screens — only used when a button in BUTTONS is IND_SCREEN
// ============================================================
// I2C buses. A bus is only started when a screen uses it, so these pins are free otherwise.
#define I2C0_SDA 25                 // bus 0 data pin
#define I2C0_SCL 26                 // bus 0 clock pin
#define I2C1_SDA 27                 // bus 1 data pin
#define I2C1_SCL 13                 // bus 1 clock pin
#define I2C_CLOCK_HZ 400000         // bus speed; SSD1306 modules usually tolerate 1000000

#define SCREEN_BRIGHTNESS 255           // normal brightness (contrast), 0-255
#define SCREEN_TRANSITION_MS 360        // how long a slide between two screens takes
#define SCREEN_SCROLL_HOLD_MS 2000      // long text waits this long before it scrolls
#define SCREEN_SCROLL_SPEED_PX_S 30     // how fast long text scrolls
#define SCREEN_SCROLL_END_HOLD_MS 1200  // pause at the end of the text before jumping back

// Burn-in protection. Any button press or slider move counts as activity.
#define SCREEN_DIM_ENABLED true                     // dim the screens when idle
#define SCREEN_DIM_AFTER_MS (2 * 60 * 1000)         //   ...after this long without activity
#define SCREEN_DIM_BRIGHTNESS 8                     //   ...to this brightness (0-255)
#define SCREEN_OFF_ENABLED true                     // turn the screens off when idle
#define SCREEN_OFF_AFTER_MS (30 * 60 * 1000)        //   ...after this long without activity
#define SCREEN_PIXEL_SHIFT_ENABLED true             // nudge content by 1 px now and then
#define SCREEN_PIXEL_SHIFT_EVERY_MS (3 * 60 * 1000) //   ...this often
#define SCREEN_WAKE_SWALLOWS_PRESS true             // a press that wakes dark screens isn't sent to the PC

// ============================================================
// Connection to the PC
// ============================================================
#define HOST_TIMEOUT_MS 3000      // no frame from the PC for this long: LEDs off, "not connected" screen
#define KEEPALIVE_MS 1000         // send Alive when nothing else was sent for this long
#define COMMIT_FALLBACK_MS 500    // apply held updates if a Begin's Commit never arrives
#define RECONNECT_INTERVAL_MS 3000  // how often the link is checked and, if needed, restarted

// ============================================================
// Input tuning
// ============================================================
#define DEBOUNCE_MS 30              // a button's reading must be stable this long before a change is reported
#define SLIDER_READ_INTERVAL_MS 10  // how often sliders are sampled
#define FILTER_WEIGHT 0.2           // slider smoothing (0-1); lower is smoother but slower
#define SLIDER_DEADBAND 25          // a smoothed slider must move more than this (ADC units) to be reported

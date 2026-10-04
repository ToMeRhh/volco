#pragma once
// Fixed constants (spec §6.3). Everything a user sets (hardware layout, Wi-Fi, screen behaviour,
// input tuning) is the controller config stored in NVS: see lib/volco_config and
// proto/volco_config.proto.

#define SERIAL_BAUD 115200     // USB serial; also what Volco's serial connection uses
#define SERIAL_RX_BUFFER 2048  // holds a whole config write while a screen redraw blocks the loop
#define SERIAL_TX_BUFFER 2048  // a config read reply leaves without blocking the loop
#define UDP_BUFFER 1536        // one datagram: the largest is a ~1.1 KB config burst

#define HOST_TIMEOUT_MS 3000     // no frame from the PC for this long: LEDs off, "not connected" screen
#define KEEPALIVE_MS 1000        // send Alive when nothing else was sent for this long
#define COMMIT_FALLBACK_MS 500   // apply held updates if a Begin's Commit never arrives
#define WIFI_RETRY_MS 10000      // at most this often, re-join a Wi-Fi that failed or dropped

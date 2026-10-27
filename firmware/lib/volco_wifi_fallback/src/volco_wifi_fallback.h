#pragma once
// The rules behind the controller keeping its previous Wi-Fi settings and falling back to them
// (spec docs/design/hardware-page.md §10.3). Pure: no Arduino, no nanopb, time is passed in. A
// config write that changes the Wi-Fi keeps a copy of the old udp settings; if no valid UDP frame
// from the PC arrives within the window after the boot that uses the new ones, src/ puts the old
// ones back. The copy is spliced into the config blob on the wire level, so it needs no decode.
#include <stddef.h>
#include <stdint.h>

namespace volco {

constexpr uint32_t kUdpField = 5;  // ControllerConfig.udp

// The last top-level length-delimited occurrence of `field` in a config blob. False when there
// is none or the blob is malformed (bad varint, length past the end, wire type 3 or 4).
bool findField(const uint8_t* blob, size_t len, uint32_t field, const uint8_t*& value, size_t& valueLen);

// Copies every top-level field of the blob except `field` to `out`, then appends `field` with
// `value` (nothing when value is null and valueLen is 0). Returns the bytes written, or 0 on a
// malformed blob or when outCap is too small.
size_t replaceField(const uint8_t* blob, size_t len, uint32_t field, const uint8_t* value, size_t valueLen,
                    uint8_t* out, size_t outCap);

// Whether a save keeps a copy of the old Wi-Fi: both are enabled and their bytes differ.
bool keepsWifiCopy(bool oldEnabled, bool newEnabled, const uint8_t* oldUdp, size_t oldLen, const uint8_t* newUdp,
                   size_t newLen);

// What a save does with the kept copy, which is always Wi-Fi settings a PC has answered on.
enum WifiCopy : uint8_t {
  WIFI_COPY_NONE,           // no copy, and none is needed
  WIFI_COPY_STORE_OLD,      // keep the old settings before the save
  WIFI_COPY_KEEP_EXISTING,  // a copy exists and the new settings are unproven too: leave it as it is
  WIFI_COPY_DROP,           // a copy exists, and the new settings are it, or have Wi-Fi off
};

// hasKept: a copy exists (the stored settings are provisional), with bytes kept/keptLen. Without
// one, keepsWifiCopy decides; with one, the copy stays unless the new settings are off or equal it.
WifiCopy wifiCopyOnSave(bool hasKept, const uint8_t* kept, size_t keptLen, bool oldEnabled, bool newEnabled,
                        const uint8_t* oldUdp, size_t oldLen, const uint8_t* newUdp, size_t newLen);

// The window after a boot on new Wi-Fi settings. Only a valid frame over UDP (transport 1)
// proves them; USB serial says nothing about the network. CONFIRM and RESTORE are each returned once.
class WifiFallback {
 public:
  enum Action : uint8_t { NONE, CONFIRM, RESTORE };

  // provisional: this boot runs settings no PC has answered on yet. The window starts at `now`.
  void begin(bool provisional, uint32_t now, uint32_t windowMs);
  void onValidFrame(uint8_t transport);
  Action tick(uint32_t now);
  bool provisional() const { return provisional_; }

 private:
  bool provisional_ = false;
  bool confirmed_ = false;
  uint32_t start_ = 0;
  uint32_t window_ = 0;
};

}  // namespace volco

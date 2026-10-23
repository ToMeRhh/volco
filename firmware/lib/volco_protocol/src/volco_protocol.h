#pragma once
// The Volco wire protocol, controller side (spec §3). Pure C++: no Arduino, time is passed in,
// output goes through a LinkSink. Built natively for tests (pio test -e native).
#include <stddef.h>
#include <stdint.h>

namespace volco {

constexpr uint8_t kStartMarker = 0xAA;
constexpr uint8_t kProtocolVersion = 1;
constexpr uint16_t kMaxPayload = 255;  // type + body
constexpr uint8_t kMaxText = 60;       // UTF-8 bytes per text slot
constexpr uint8_t kMaxButtons = 32;
constexpr uint8_t kNoIcon = 0xFF;

enum MsgType : uint8_t {
  MSG_HELLO = 0x01, MSG_ALIVE = 0x02, MSG_SLIDER = 0x03, MSG_BUTTON = 0x04,
  MSG_HELLO_REQUEST = 0x81, MSG_HEARTBEAT = 0x82, MSG_LED = 0x83, MSG_SCREEN = 0x84,
  MSG_BEGIN = 0x85, MSG_COMMIT = 0x86,
  MSG_CONFIG_DATA = 0x05, MSG_CONFIG_RESULT = 0x06,                     // controller → PC (spec §5.3, §5.4)
  MSG_CONFIG_READ = 0x87, MSG_CONFIG_BEGIN = 0x88, MSG_CONFIG_CHUNK = 0x89, MSG_CONFIG_END = 0x8A,
  MSG_CALIBRATE = 0x8E, MSG_CALIBRATION = 0x09,                         // slider calibration (its design doc)
  MSG_PIN_WATCH = 0x8B, MSG_PIN_ACTIVITY = 0x07,                        // firmware helpers (issue #120)
  MSG_I2C_SCAN = 0x8C, MSG_I2C_SCAN_RESULT = 0x08, MSG_PIN_DRIVE = 0x8D,
};
// A Calibrate's action.
enum CalibrateAction : uint8_t { CALIBRATE_QUERY = 0, CALIBRATE_START = 1, CALIBRATE_DONE = 2, CALIBRATE_CANCEL = 3 };
// A PinWatch's role: stop, watch the free and slider pins' analog levels, or watch the free and button pins' edges.
enum WatchRole : uint8_t { WATCH_STOP = 0, WATCH_ANALOG = 1, WATCH_BUTTON = 2 };
// An I2cScan probes the addresses 0x08-0x77: at most this many answer.
constexpr uint8_t kMaxScanAddresses = 112;
enum Kind : uint8_t { KIND_NONE = 0, KIND_LED = 1, KIND_LED_PAIR = 2, KIND_SCREEN = 3 };
enum Transition : uint8_t { TRANSITION_NONE = 0, TRANSITION_SLIDE_LEFT = 1, TRANSITION_SLIDE_RIGHT = 2 };
enum TopKind : uint8_t { TOP_EMPTY = 0, TOP_TEXT = 1, TOP_DOTS = 2 };

// One screen in layout 1 (spec §3.3). Text is NUL-terminated UTF-8 in logical order.
struct ScreenContent {
  uint8_t layout = 1;
  uint8_t topKind = TOP_EMPTY;
  uint8_t topLen = 0;
  char top[kMaxText + 1] = {0};
  uint8_t dotsSelected = 0, dotsCount = 0;
  uint8_t icon = kNoIcon;
  uint8_t iconFlags = 0;  // bit 0: muted slash
  uint8_t mainLen = 0;
  char main[kMaxText + 1] = {0};
  bool operator==(const ScreenContent& o) const;
  bool operator!=(const ScreenContent& o) const { return !(*this == o); }
};

// Builds a ScreenContent with top text (or none for ""), an icon and main text.
ScreenContent screenText(const char* top, uint8_t icon, const char* main);

// Byte-at-a-time frame parser: 0xAA | length | payload | XOR(payload).
class FrameParser {
 public:
  // True when this byte completed a valid frame; payload()/length() then hold it.
  bool feed(uint8_t b);
  const uint8_t* payload() const { return buf_; }
  uint8_t length() const { return len_; }

 private:
  enum State : uint8_t { WAIT_START, WAIT_LENGTH, WAIT_PAYLOAD, WAIT_CHECKSUM };
  State state_ = WAIT_START;
  uint8_t buf_[kMaxPayload] = {0};
  uint8_t len_ = 0;
  uint16_t idx_ = 0;
};

// Writes a whole frame for `payload` (type + body) into `out` (len + 3 bytes). Returns its size.
size_t encodeFrame(const uint8_t* payload, uint8_t len, uint8_t* out);
// What Hello reports (spec §5.2). firmware: the build id, at most 32 bytes (longer is cut).
struct HelloInfo {
  uint8_t chip;            // 1 = ESP32
  const char* firmware;
  uint8_t configState;     // 0 none, 1 stored, 2 damaged
  uint32_t configCrc;
  uint8_t sliders;
  uint8_t buttons;
  const uint8_t* kinds;    // one Kind per button
};
constexpr uint8_t kMaxFirmwareId = 32;
constexpr size_t kMaxHelloFrame = 3 + 1 + 10 + kMaxFirmwareId + kMaxButtons;  // marker, length, checksum; type; body

size_t encodeHello(const HelloInfo& hello, uint8_t* out);
size_t encodeAlive(uint8_t* out);
size_t encodeSlider(uint8_t index, uint16_t value, uint8_t* out);
size_t encodeButton(uint8_t index, bool pressed, uint8_t* out);
// One chunk of a config read reply (spec §5.3): seq, total, offset, CRC of the whole blob, data.
size_t encodeConfigData(uint8_t seq, uint16_t total, uint16_t offset, uint32_t crc, const uint8_t* data, uint8_t n,
                        uint8_t* out);
// The answer to a config write (spec §5.4): seq, result code, item, field.
size_t encodeConfigResult(uint8_t seq, uint8_t code, uint8_t item, uint8_t field, uint8_t* out);
// A slider's calibration: index, flags (bit 0 learned, bit 1 sweeping, bit 2 auto-calibrate), low and high end.
size_t encodeCalibration(uint8_t index, uint8_t flags, uint16_t lo, uint16_t hi, uint8_t* out);
// A pin that moved during a watch.
size_t encodePinActivity(uint8_t pin, uint8_t* out);
// The answer to an I2cScan: the bus's SDA and SCL, then the addresses that answered (count is clamped to kMaxScanAddresses).
size_t encodeI2cScanResult(uint8_t sda, uint8_t scl, uint8_t count, const uint8_t* addresses, uint8_t* out);

// Decodes a Screen payload. False when a length overruns the payload (the message is dropped).
bool decodeScreen(const uint8_t* payload, uint8_t len, uint8_t& button, uint8_t& transition, ScreenContent& out);

// What the session does to the outside world.
struct LinkSink {
  virtual void send(const uint8_t* frame, size_t len) = 0;
  virtual void applyLed(uint8_t button, uint8_t value) = 0;
  virtual void applyScreen(uint8_t button, uint8_t transition, const ScreenContent& content, uint32_t now) = 0;
  virtual void hostLost() = 0;  // LEDs off, local "not connected" screen
  virtual uint16_t sliderValue(uint8_t index) = 0;  // current filtered position, sent after every Hello
  // A Calibrate frame (a CalibrateAction); the index is not checked here.
  virtual void calibrate(uint8_t /*index*/, uint8_t /*action*/, uint32_t /*now*/) {}
  // Firmware helpers (a WatchRole; the pins are not checked here).
  virtual void pinWatch(uint8_t /*role*/, uint32_t /*now*/) {}
  virtual void i2cScan(uint8_t /*sda*/, uint8_t /*scl*/, uint32_t /*now*/) {}
  virtual void pinDrive(uint8_t /*pin*/, uint8_t /*level*/, uint16_t /*durationMs*/, uint32_t /*now*/) {}
  virtual ~LinkSink() = default;
};

// Where Link sends the config frames (spec §5.3, §5.4), each with the transport it arrived on.
struct ConfigSink {
  virtual void configRead(uint8_t transport, uint8_t seq, uint32_t now) = 0;
  virtual void configBegin(uint8_t transport, uint8_t seq, uint16_t total, uint32_t crc, uint32_t now) = 0;
  virtual void configChunk(uint8_t transport, uint8_t seq, uint16_t offset, const uint8_t* data, size_t len,
                           uint32_t now) = 0;
  virtual void configEnd(uint8_t transport, uint8_t seq, uint32_t now) = 0;
  virtual ~ConfigSink() = default;
};

struct LinkConfig {
  uint8_t sliders;
  uint8_t buttons;
  const uint8_t* kinds;  // one Kind per button
  uint32_t hostTimeoutMs;
  uint32_t keepaliveMs;
  uint32_t commitFallbackMs;
  uint8_t chip = 1;            // what Hello reports (spec §5.2)
  const char* firmware = "dev";
  uint8_t configState = 0;
  uint32_t configCrc = 0;
};

// The controller side of a session (spec §3.4): Hello, keepalive, host timeout, Begin/Commit.
class Link {
 public:
  Link(const LinkConfig& config, LinkSink& sink);
  void begin(uint32_t now);                                       // boot: sends Hello
  void setConfigSink(ConfigSink* sink) { configSink_ = sink; }
  // transport: the id the frame arrived on (passed on to the ConfigSink; 0 = USB serial).
  void onPayload(const uint8_t* payload, uint8_t len, uint32_t now, uint8_t transport = 0);
  void tick(uint32_t now);                                        // every loop pass
  void sendSlider(uint8_t index, uint16_t value, uint32_t now);
  void sendButton(uint8_t index, bool pressed, uint32_t now);
  void sendCalibration(uint8_t index, uint8_t flags, uint16_t lo, uint16_t hi, uint32_t now);
  void sendPinActivity(uint8_t pin, uint32_t now);
  void sendI2cScanResult(uint8_t sda, uint8_t scl, uint8_t count, const uint8_t* addresses, uint32_t now);
  bool hostAlive() const { return hostAlive_; }
  bool holding() const { return holding_; }

 private:
  struct Held {
    bool hasLed = false;
    uint8_t led = 0;
    bool hasScreen = false;
    uint8_t transition = TRANSITION_NONE;
    ScreenContent screen;
  };
  void sendFrame(const uint8_t* payload, uint8_t len, uint32_t now);
  void sendHello(uint32_t now);
  void applyHeld(uint32_t now);
  bool isLedButton(uint8_t b) const;
  bool isScreenButton(uint8_t b) const;

  LinkConfig config_;
  LinkSink& sink_;
  ConfigSink* configSink_ = nullptr;
  Held held_[kMaxButtons];
  bool holding_ = false;
  uint32_t holdStart_ = 0;
  bool hostAlive_ = false;
  uint32_t lastRx_ = 0;
  uint32_t lastTx_ = 0;
};

// Which transport the controller answers on (spec §5.5). Transport ids are bit positions in the
// masks: 0 = USB serial, 1 = UDP. Unlatched (boot, host timeout) every frame goes to every
// transport that is up; the first valid frame from the PC latches its transport.
class ActiveTransport {
 public:
  void onValidFrame(uint8_t id) {  // call BEFORE Link::onPayload, so the reply follows the latch
    latched_ = true;
    active_ = id;
  }
  void onHostLost() { latched_ = false; }
  uint8_t targets(uint8_t upMask) const { return latched_ ? (uint8_t)(upMask & (1u << active_)) : upMask; }
  bool latched() const { return latched_; }
  uint8_t active() const { return active_; }

 private:
  bool latched_ = false;
  uint8_t active_ = 0;
};

// UDP is accepted only from the configured PC (spec §5.5, D7): no PC configured, nothing accepted.
inline bool acceptUdpSource(uint32_t srcIp, uint32_t configuredIp) { return configuredIp != 0 && srcIp == configuredIp; }

}  // namespace volco

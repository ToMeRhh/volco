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
};
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
size_t encodeHello(uint8_t sliders, const uint8_t* kinds, uint8_t buttons, uint8_t* out);
size_t encodeAlive(uint8_t* out);
size_t encodeSlider(uint8_t index, uint16_t value, uint8_t* out);
size_t encodeButton(uint8_t index, bool pressed, uint8_t* out);

// Decodes a Screen payload. False when a length overruns the payload (the message is dropped).
bool decodeScreen(const uint8_t* payload, uint8_t len, uint8_t& button, uint8_t& transition, ScreenContent& out);

// What the session does to the outside world.
struct LinkSink {
  virtual void send(const uint8_t* frame, size_t len) = 0;
  virtual void applyLed(uint8_t button, uint8_t value) = 0;
  virtual void applyScreen(uint8_t button, uint8_t transition, const ScreenContent& content, uint32_t now) = 0;
  virtual void hostLost() = 0;  // LEDs off, local "not connected" screen
  virtual uint16_t sliderValue(uint8_t index) = 0;  // current filtered position, sent after every Hello
  virtual ~LinkSink() = default;
};

struct LinkConfig {
  uint8_t sliders;
  uint8_t buttons;
  const uint8_t* kinds;  // one Kind per button
  uint32_t hostTimeoutMs;
  uint32_t keepaliveMs;
  uint32_t commitFallbackMs;
};

// The controller side of a session (spec §3.4): Hello, keepalive, host timeout, Begin/Commit.
class Link {
 public:
  Link(const LinkConfig& config, LinkSink& sink);
  void begin(uint32_t now);                                       // boot: sends Hello
  void onPayload(const uint8_t* payload, uint8_t len, uint32_t now);
  void tick(uint32_t now);                                        // every loop pass
  void sendSlider(uint8_t index, uint16_t value, uint32_t now);
  void sendButton(uint8_t index, bool pressed, uint32_t now);
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
  Held held_[kMaxButtons];
  bool holding_ = false;
  uint32_t holdStart_ = 0;
  bool hostAlive_ = false;
  uint32_t lastRx_ = 0;
  uint32_t lastTx_ = 0;
};

}  // namespace volco

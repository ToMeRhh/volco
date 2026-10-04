#include "volco_protocol.h"

#include <string.h>

namespace volco {

bool ScreenContent::operator==(const ScreenContent& o) const {
  return layout == o.layout && topKind == o.topKind && topLen == o.topLen && memcmp(top, o.top, topLen) == 0 &&
         dotsSelected == o.dotsSelected && dotsCount == o.dotsCount && icon == o.icon &&
         iconFlags == o.iconFlags && mainLen == o.mainLen && memcmp(main, o.main, mainLen) == 0;
}

static uint8_t copyText(char* dst, const char* src) {
  size_t n = strlen(src);
  if (n > kMaxText) n = kMaxText;
  memcpy(dst, src, n);
  dst[n] = 0;
  return (uint8_t)n;
}

ScreenContent screenText(const char* top, uint8_t icon, const char* main) {
  ScreenContent c;
  c.topLen = copyText(c.top, top);
  c.topKind = c.topLen ? TOP_TEXT : TOP_EMPTY;
  c.icon = icon;
  c.mainLen = copyText(c.main, main);
  return c;
}

bool FrameParser::feed(uint8_t b) {
  switch (state_) {
    case WAIT_START:
      if (b == kStartMarker) state_ = WAIT_LENGTH;
      return false;
    case WAIT_LENGTH:
      len_ = b;
      idx_ = 0;
      state_ = len_ == 0 ? WAIT_START : WAIT_PAYLOAD;
      return false;
    case WAIT_PAYLOAD:
      buf_[idx_++] = b;
      if (idx_ >= len_) state_ = WAIT_CHECKSUM;
      return false;
    case WAIT_CHECKSUM: {
      state_ = WAIT_START;
      uint8_t cs = 0;
      for (uint16_t i = 0; i < len_; i++) cs ^= buf_[i];
      return cs == b;
    }
  }
  return false;
}

size_t encodeFrame(const uint8_t* payload, uint8_t len, uint8_t* out) {
  out[0] = kStartMarker;
  out[1] = len;
  uint8_t cs = 0;
  for (uint8_t i = 0; i < len; i++) {
    out[2 + i] = payload[i];
    cs ^= payload[i];
  }
  out[2 + len] = cs;
  return (size_t)len + 3;
}

size_t encodeHello(uint8_t sliders, const uint8_t* kinds, uint8_t buttons, uint8_t* out) {
  uint8_t p[4 + kMaxButtons];
  p[0] = MSG_HELLO;
  p[1] = kProtocolVersion;
  p[2] = sliders;
  p[3] = buttons;
  for (uint8_t i = 0; i < buttons && i < kMaxButtons; i++) p[4 + i] = kinds[i];
  return encodeFrame(p, (uint8_t)(4 + buttons), out);
}

size_t encodeAlive(uint8_t* out) {
  const uint8_t p[] = {MSG_ALIVE};
  return encodeFrame(p, 1, out);
}

size_t encodeSlider(uint8_t index, uint16_t value, uint8_t* out) {
  const uint8_t p[] = {MSG_SLIDER, index, (uint8_t)(value >> 8), (uint8_t)(value & 0xFF)};
  return encodeFrame(p, sizeof p, out);
}

size_t encodeButton(uint8_t index, bool pressed, uint8_t* out) {
  const uint8_t p[] = {MSG_BUTTON, index, (uint8_t)(pressed ? 1 : 0)};
  return encodeFrame(p, sizeof p, out);
}

// Reads a length-prefixed text slot at payload[i]; advances i. False on overrun.
static bool readText(const uint8_t* p, uint8_t len, uint16_t& i, char* dst, uint8_t& dstLen) {
  if (i >= len) return false;
  uint8_t n = p[i++];
  if (n > kMaxText || i + n > len) return false;
  memcpy(dst, p + i, n);
  dst[n] = 0;
  dstLen = n;
  i += n;
  return true;
}

bool decodeScreen(const uint8_t* p, uint8_t len, uint8_t& button, uint8_t& transition, ScreenContent& out) {
  if (len < 5 || p[0] != MSG_SCREEN) return false;
  ScreenContent c;
  button = p[1];
  transition = p[2];
  c.layout = p[3];
  c.topKind = p[4];
  uint16_t i = 5;
  if (c.topKind == TOP_TEXT) {
    if (!readText(p, len, i, c.top, c.topLen)) return false;
  } else if (c.topKind == TOP_DOTS) {
    if (i + 2 > len) return false;
    c.dotsSelected = p[i++];
    c.dotsCount = p[i++];
  } else {
    c.topKind = TOP_EMPTY;
  }
  if (i + 2 > len) return false;
  c.icon = p[i++];
  c.iconFlags = p[i++];
  if (!readText(p, len, i, c.main, c.mainLen)) return false;
  if (transition > TRANSITION_SLIDE_RIGHT) transition = TRANSITION_NONE;  // unknown → none
  out = c;
  return true;
}

Link::Link(const LinkConfig& config, LinkSink& sink) : config_(config), sink_(sink) {
  if (config_.buttons > kMaxButtons) config_.buttons = kMaxButtons;
}

void Link::begin(uint32_t now) {
  lastRx_ = now;
  sendHello(now);
}

bool Link::isLedButton(uint8_t b) const {
  return b < config_.buttons && (config_.kinds[b] == KIND_LED || config_.kinds[b] == KIND_LED_PAIR);
}

bool Link::isScreenButton(uint8_t b) const { return b < config_.buttons && config_.kinds[b] == KIND_SCREEN; }

void Link::onPayload(const uint8_t* p, uint8_t len, uint32_t now) {
  if (len < 1) return;
  lastRx_ = now;
  const uint8_t type = p[0];
  if (!hostAlive_) {
    hostAlive_ = true;
    // The PC is back (or here for the first time): announce the hardware so it re-sends
    // everything. A HelloRequest gets its Hello below instead.
    if (type != MSG_HELLO_REQUEST) sendHello(now);
  }
  switch (type) {
    case MSG_HELLO_REQUEST:
      sendHello(now);
      break;
    case MSG_LED:
      if (len >= 3 && isLedButton(p[1])) {
        if (holding_) {
          held_[p[1]].hasLed = true;
          held_[p[1]].led = p[2];
        } else {
          sink_.applyLed(p[1], p[2]);
        }
      }
      break;
    case MSG_SCREEN: {
      uint8_t button, transition;
      ScreenContent c;
      if (!decodeScreen(p, len, button, transition, c) || !isScreenButton(button)) break;
      if (holding_) {
        Held& h = held_[button];
        // A newer instant update keeps a held slide (spec §2.3).
        if (!(h.hasScreen && transition == TRANSITION_NONE)) h.transition = transition;
        h.hasScreen = true;
        h.screen = c;
      } else {
        sink_.applyScreen(button, transition, c, now);
      }
      break;
    }
    case MSG_BEGIN:
      holding_ = true;
      holdStart_ = now;
      break;
    case MSG_COMMIT:
      if (holding_) applyHeld(now);
      break;
    default:
      break;  // Heartbeat (its arrival is the point) and unknown types
  }
}

void Link::applyHeld(uint32_t now) {
  holding_ = false;
  for (uint8_t b = 0; b < config_.buttons; b++) {
    Held& h = held_[b];
    if (h.hasLed) sink_.applyLed(b, h.led);
    if (h.hasScreen) sink_.applyScreen(b, h.transition, h.screen, now);
    h = Held();
  }
}

void Link::tick(uint32_t now) {
  if (holding_ && now - holdStart_ >= config_.commitFallbackMs) applyHeld(now);
  if (hostAlive_ && now - lastRx_ >= config_.hostTimeoutMs) {
    hostAlive_ = false;
    holding_ = false;
    for (uint8_t b = 0; b < config_.buttons; b++) held_[b] = Held();
    sink_.hostLost();
  }
  if (now - lastTx_ >= config_.keepaliveMs) {
    uint8_t f[4];
    sink_.send(f, encodeAlive(f));
    lastTx_ = now;
  }
}

void Link::sendFrame(const uint8_t* payload, uint8_t len, uint32_t now) {
  uint8_t f[kMaxPayload + 3];
  sink_.send(f, encodeFrame(payload, len, f));
  lastTx_ = now;
}

// Hello, then every slider's current position: the PC may have just started, so it knows
// nothing until a slider moves otherwise.
void Link::sendHello(uint32_t now) {
  uint8_t f[4 + kMaxButtons + 3];
  sink_.send(f, encodeHello(config_.sliders, config_.kinds, config_.buttons, f));
  for (uint8_t i = 0; i < config_.sliders; i++) sink_.send(f, encodeSlider(i, sink_.sliderValue(i), f));
  lastTx_ = now;
}

void Link::sendSlider(uint8_t index, uint16_t value, uint32_t now) {
  const uint8_t p[] = {MSG_SLIDER, index, (uint8_t)(value >> 8), (uint8_t)(value & 0xFF)};
  sendFrame(p, sizeof p, now);
}

void Link::sendButton(uint8_t index, bool pressed, uint32_t now) {
  const uint8_t p[] = {MSG_BUTTON, index, (uint8_t)(pressed ? 1 : 0)};
  sendFrame(p, sizeof p, now);
}

}  // namespace volco

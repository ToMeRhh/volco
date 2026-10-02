#include "volco_screen_state.h"

namespace volco {

void ScreenSlot::show(const ScreenContent& c, uint8_t transition, uint32_t now, bool panelsOff) {
  previous_ = current_;  // a running slide snaps: its target becomes the new "from"
  current_ = c;
  transition_ = panelsOff ? (uint8_t)TRANSITION_NONE : transition;
  start_ = now;
  settled_ = transition_ == TRANSITION_NONE ? now : now + transitionMs_;
}

float ScreenSlot::progress(uint32_t now) const {
  if (!inTransition(now)) return 1.0f;
  return (float)(now - start_) / (float)transitionMs_;
}

Power Panels::power(uint32_t now) const {
  uint32_t idle = now - last_;
  if (c_.off && idle >= c_.offAfterMs) return POWER_OFF;
  if (c_.dim && idle >= c_.dimAfterMs) return POWER_DIM;
  return POWER_NORMAL;
}

void Panels::shift(uint32_t now, int8_t& dx, int8_t& dy) const {
  static const int8_t kCycle[9][2] = {{0, 0}, {1, 0}, {1, 1}, {0, 1}, {-1, 1}, {-1, 0}, {-1, -1}, {0, -1}, {1, -1}};
  if (!c_.shift || c_.shiftEveryMs == 0) { dx = dy = 0; return; }
  uint32_t i = (now / c_.shiftEveryMs) % 9;
  dx = kCycle[i][0];
  dy = kCycle[i][1];
}

bool Panels::filterButton(uint8_t button, bool pressed, uint32_t now) {
  const uint32_t bit = button < 32 ? (1u << button) : 0;
  if (pressed) {
    bool swallow = c_.swallow && power(now) == POWER_OFF;
    activity(now);
    if (swallow) swallowed_ |= bit;
    return !swallow;
  }
  activity(now);
  if (swallowed_ & bit) {
    swallowed_ &= ~bit;
    return false;
  }
  return true;
}

}  // namespace volco

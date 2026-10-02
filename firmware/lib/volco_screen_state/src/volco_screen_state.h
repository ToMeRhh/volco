#pragma once
// Per-screen content/transition state and panel power (spec §2.3, §2.4). Pure: time is passed in.
#include <stdint.h>
#include "volco_protocol.h"

namespace volco {

// One screen: what it shows, what it showed before, and the transition between them.
class ScreenSlot {
 public:
  explicit ScreenSlot(uint32_t transitionMs) : transitionMs_(transitionMs) {}

  // New content. A slide still running snaps to its end first, so the new transition starts
  // from what the old one was heading to. While the panels are off, nothing animates.
  void show(const ScreenContent& c, uint8_t transition, uint32_t now, bool panelsOff);

  const ScreenContent& current() const { return current_; }
  const ScreenContent& previous() const { return previous_; }
  uint8_t transition() const { return transition_; }
  bool inTransition(uint32_t now) const {
    return transition_ != TRANSITION_NONE && now - start_ < transitionMs_;
  }
  float progress(uint32_t now) const;
  // The scroll clock: ms since the content settled (after its slide); 0 while sliding.
  uint32_t scrollSince(uint32_t now) const {
    return (int32_t)(now - settled_) < 0 ? 0 : now - settled_;
  }

 private:
  uint32_t transitionMs_;
  ScreenContent current_, previous_;
  uint8_t transition_ = TRANSITION_NONE;
  uint32_t start_ = 0, settled_ = 0;
};

struct PanelConfig {
  bool dim, off, shift, swallow;
  uint32_t dimAfterMs, offAfterMs, shiftEveryMs;
};

enum Power : uint8_t { POWER_NORMAL, POWER_DIM, POWER_OFF };

// Burn-in protection shared by every panel: idle dim/off, pixel shift, swallowing the wake press.
class Panels {
 public:
  explicit Panels(const PanelConfig& c) : c_(c) {}
  void activity(uint32_t now) { last_ = now; }
  Power power(uint32_t now) const;
  void shift(uint32_t now, int8_t& dx, int8_t& dy) const;
  // Call for every debounced button change. False = do not send it to the PC (spec §2.4).
  bool filterButton(uint8_t button, bool pressed, uint32_t now);

 private:
  PanelConfig c_;
  uint32_t last_ = 0;
  uint32_t swallowed_ = 0;  // bit per button whose wake press was swallowed
};

}  // namespace volco

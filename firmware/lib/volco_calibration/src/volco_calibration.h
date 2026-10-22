#pragma once
// One slider's calibration (docs/design/slider-calibration.md): its learned ends, the
// Auto-calibrate setting, the Re-calibrate sweep (start, done, cancel back to the saved ends, the
// 60 s timeout, the host-lost cancel) and when a Calibration report is due. Pure: no Arduino, no
// storage, time is passed in. main.cpp feeds it readings and the Link's Calibrate frames.
#include <stdint.h>
#include "volco_slider_ends.h"

namespace volco {

class SliderCalibration {
 public:
  static constexpr uint32_t kSweepTimeoutMs = 60000;   // a sweep with no Done ends as a Cancel
  static constexpr uint32_t kReportIntervalMs = 100;   // widening reports, at most this often
  // Calibration report flags (the wire's 0x09 body).
  static constexpr uint8_t kFlagLearned = 1 << 0;
  static constexpr uint8_t kFlagSweeping = 1 << 1;
  static constexpr uint8_t kFlagAuto = 1 << 2;

  // The slider's Auto-calibrate setting: on, it learns from every reading; off, only in a sweep.
  void configure(bool autoCalibrate) { auto_ = autoCalibrate; }
  // Ends read back from storage (ignored unless they form a learned span).
  void restore(uint16_t lo, uint16_t hi) { ends_.restore(lo, hi); }

  // A smoothed reading. Learns from it when auto-calibrating or sweeping.
  void reading(uint16_t smoothed, uint32_t now);
  // The reading on the full range: raw until learned, then stretched between the ends.
  uint16_t map(uint16_t v) const { return ends_.map(v); }

  // Starts a sweep: keeps the current ends for Cancel and forgets them. False if one is running.
  bool startSweep(uint32_t now);
  // Ends the sweep and keeps what it learned. Ignored unless sweeping and learned.
  void done(uint32_t now);
  // Ends the sweep and restores the ends it started from. Ignored unless sweeping.
  void cancel();
  // Every loop pass: a sweep that has run kSweepTimeoutMs ends as a Cancel.
  void tick(uint32_t now);
  // The PC is gone: a sweep ends as a Cancel.
  void hostLost() { cancel(); }

  // True when a Calibration report should be sent now: the ends widened (at most every
  // kReportIntervalMs), or a sweep just ended (at once). Consumes the pending report.
  bool reportDue(uint32_t now);
  // True once after a sweep ends (any way): send the report, then a fresh Slider frame.
  bool sweepEnded() {
    bool e = sweepEnded_;
    sweepEnded_ = false;
    return e;
  }

  // True once after Auto-calibrate first learned the span (not in a sweep, which ends with its own
  // Slider frame): send the stretched position at once, whatever the deadband says.
  bool becameLearned() {
    bool b = becameLearned_;
    becameLearned_ = false;
    return b;
  }

  bool learned() const { return ends_.learned(); }
  bool sweeping() const { return sweeping_; }
  bool autoCalibrate() const { return auto_; }
  uint16_t lo() const { return ends_.lo(); }
  uint16_t hi() const { return ends_.hi(); }
  uint8_t flags() const {
    return (uint8_t)((learned() ? kFlagLearned : 0) | (sweeping_ ? kFlagSweeping : 0) | (auto_ ? kFlagAuto : 0));
  }

  // The ends should be stored: a learned end moved past the stored one. Never during a sweep
  // (a Cancel must find the old ends in storage); a finished sweep always needs a save.
  bool needsSave() const { return !sweeping_ && ends_.needsSave(); }
  void markSaved() { ends_.markSaved(); }

 private:
  void endSweep();

  SliderEnds ends_;
  SliderEnds beforeSweep_;  // the ends a Cancel restores
  bool auto_ = true;
  bool sweeping_ = false;
  uint32_t sweepStart_ = 0;
  bool widened_ = false;     // a widening report is pending
  bool forced_ = false;      // a sweep ended: report at once
  bool reported_ = false;    // lastReport_ is set
  uint32_t lastReport_ = 0;
  bool sweepEnded_ = false;
  bool becameLearned_ = false;
};

}  // namespace volco

#include "volco_calibration.h"

namespace volco {

void SliderCalibration::reading(uint16_t smoothed, uint32_t) {
  if (!auto_ && !sweeping_) return;
  const uint16_t lo = ends_.lo(), hi = ends_.hi();
  const bool was = ends_.learned();
  ends_.learn(smoothed);
  if (ends_.lo() != lo || ends_.hi() != hi) widened_ = true;
  if (!was && ends_.learned() && !sweeping_) becameLearned_ = true;
}

bool SliderCalibration::startSweep(uint32_t now) {
  if (sweeping_) return false;
  beforeSweep_ = ends_;
  ends_.reset();
  sweeping_ = true;
  sweepStart_ = now;
  return true;
}

void SliderCalibration::done(uint32_t) {
  if (!sweeping_ || !ends_.learned()) return;
  endSweep();
}

void SliderCalibration::cancel() {
  if (!sweeping_) return;
  ends_ = beforeSweep_;
  endSweep();
}

void SliderCalibration::endSweep() {
  sweeping_ = false;
  forced_ = true;
  sweepEnded_ = true;
}

void SliderCalibration::tick(uint32_t now) {
  if (sweeping_ && now - sweepStart_ >= kSweepTimeoutMs) cancel();
}

bool SliderCalibration::reportDue(uint32_t now) {
  bool due = forced_ || (widened_ && (!reported_ || now - lastReport_ >= kReportIntervalMs));
  if (!due) return false;
  forced_ = widened_ = false;
  reported_ = true;
  lastReport_ = now;
  return true;
}

}  // namespace volco

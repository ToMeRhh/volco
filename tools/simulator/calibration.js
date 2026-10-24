// The controller's slider calibration, mirroring firmware/lib/volco_calibration and
// volco_slider_ends (docs/design/slider-calibration.md): learned ends, the Auto-calibrate setting,
// the Re-calibrate sweep (start, done, cancel, the 60 s timeout, the host-lost cancel) and when a
// Calibration report goes out. The simulator's sliders are full range, so a sweep learns the range
// the user drags through. Time is passed in. No DOM: index.html loads it as a plain script
// (globalThis.VolcoCalibration), the tests with require().
(function (root) {
  'use strict';
  const P = typeof module === 'object' && module.exports ? require('./protocol.js') : root.VolcoProtocol;

  const FULL = 4095, MIN_SPAN = 1000, END_BAND = 0.015;
  const SWEEP_TIMEOUT_MS = 60000, REPORT_INTERVAL_MS = 100;

  /** One slider: SliderEnds + SliderCalibration of the firmware. */
  function createSlider() {
    let lo = FULL, hi = 0, auto = true, reverse = false, sweeping = false, sweepStart = 0;
    let before = null, widened = false, forced = false, reported = false, lastReport = 0, ended = false;
    let becameLearned = false;
    const learned = () => hi >= lo && hi - lo >= MIN_SPAN;
    const endSweep = () => { sweeping = false; forced = true; ended = true; };
    return {
      configure(a, r) { auto = a; reverse = r === true; },
      /** Ends read back from storage (ignored unless they form a learned span). */
      restore(l, h) { if (h >= l && h - l >= MIN_SPAN) { lo = l; hi = h; } },
      autoCalibrate: () => auto,
      /** The ends storage holds: during a sweep, the ones it started from (what a Cancel restores). */
      stored: () => (sweeping ? { ...before } : { lo, hi }),
      reading(v) {
        if (!auto && !sweeping) return;
        const l = lo, h = hi, was = learned();
        if (v < lo) lo = v;
        if (v > hi) hi = v;
        if (lo !== l || hi !== h) widened = true;
        if (!was && learned() && !sweeping) becameLearned = true;
      },
      /** True once after Auto-calibrate first learned the span (a sweep ends with its own Slider frame). */
      becameLearned() { const b = becameLearned; becameLearned = false; return b; },
      /** The reading on the full range: raw until learned, then stretched between the ends. */
      map(v) {
        if (!learned()) return v;
        let p = (v - lo) / (hi - lo);
        p = (p - END_BAND) / (1 - 2 * END_BAND);
        if (p <= 0) return 0;
        if (p >= 1) return FULL;
        return Math.floor(p * FULL + 0.5);
      },
      /** What goes on the wire: map, mirrored (4095 - x) when the slider is reversed. */
      sent(v) { const x = this.map(v); return reverse ? FULL - x : x; },
      startSweep(now) {
        if (sweeping) return false;
        before = { lo, hi };
        lo = FULL; hi = 0;
        sweeping = true; sweepStart = now;
        return true;
      },
      done() { if (sweeping && learned()) endSweep(); },
      cancel() {
        if (!sweeping) return;
        ({ lo, hi } = before);
        endSweep();
      },
      tick(now) { if (sweeping && now - sweepStart >= SWEEP_TIMEOUT_MS) this.cancel(); },
      /** True when a report is due now; consumes the pending one. */
      reportDue(now) {
        const due = forced || (widened && (!reported || now - lastReport >= REPORT_INTERVAL_MS));
        if (!due) return false;
        forced = widened = false;
        reported = true; lastReport = now;
        return true;
      },
      sweepEnded() { const e = ended; ended = false; return e; },
      sweeping: () => sweeping,
      learned,
      lo: () => lo,
      hi: () => hi,
      flags: () => (learned() ? P.CAL_FLAG.LEARNED : 0) | (sweeping ? P.CAL_FLAG.SWEEPING : 0) | (auto ? P.CAL_FLAG.AUTO : 0),
    };
  }

  /**
   * Every slider's calibration, and the frames the controller sends for it.
   * @param {object} o
   * @param {(payload:number[], now:number) => void} o.send   writes a payload (the Link's sendPayload)
   * @param {(index:number, value:number, now:number) => void} o.sendSlider
   * @param {(index:number) => number} o.raw                   a slider's position now, 0..4095
   */
  function createCalibrations(o) {
    const sliders = [];
    const at = i => (sliders[i] = sliders[i] || createSlider());
    const report = (i, now) => { const c = at(i); o.send(P.calibration(i, c.flags(), c.lo(), c.hi()), now); };
    // A sweep ended (any way): its report, then the position stretched by the final ends.
    function finishSweep(i, now) {
      at(i).reportDue(now);
      report(i, now);
      o.sendSlider(i, at(i).sent(o.raw(i)), now);
    }
    return {
      /**
       * Boot: the sliders the config has, as { pin, auto, reverse }. As the firmware's restoreEndsEntries
       * does, a slider keeps the learned ends of the one with its pin and the same Auto-calibrate
       * setting; the rest start unlearned, and no sweep survives a reboot.
       */
      configure(list) {
        const old = sliders.map(c => c && { pin: c.pin, auto: c.autoCalibrate(), ...c.stored() });
        sliders.length = 0;
        list.forEach(({ pin, auto, reverse }, i) => {
          const c = at(i);
          c.pin = pin;
          c.configure(auto, reverse);
          const e = old.find(o => o && o.pin === pin);
          if (e && e.auto === auto) c.restore(e.lo, e.hi);
        });
      },
      count: () => sliders.length,
      slider: at,
      map: (i, v) => at(i).map(v),
      /** The value a Slider frame carries for a reading: map, mirrored for a reversed slider. */
      sent: (i, v) => at(i).sent(v),
      /** A smoothed reading: the ends learn from it when auto-calibrating or sweeping. */
      reading(i, v) { if (i < sliders.length) at(i).reading(v); },
      /** True once after Auto-calibrate first learned slider i's span: send its position at once. */
      becameLearned: i => i < sliders.length && at(i).becameLearned(),
      /** A Calibrate frame for a slider that exists is always answered with its Calibration. */
      calibrate(i, action, now) {
        if (i >= sliders.length) return;
        const c = at(i);
        if (action === P.CALIBRATE.START) c.startSweep(now);
        else if (action === P.CALIBRATE.DONE) c.done(now);
        else if (action === P.CALIBRATE.CANCEL) c.cancel();
        if (c.sweepEnded()) finishSweep(i, now);
        else { c.reportDue(now); report(i, now); }
      },
      /** Every pass: the sweep timeout, a sweep's end, and a report when the ends widened. */
      tick(now) {
        sliders.forEach((c, i) => {
          c.tick(now);
          if (c.sweepEnded()) finishSweep(i, now);
          else if (c.reportDue(now)) report(i, now);
        });
      },
      /** The PC is gone: a sweep ends as a Cancel. */
      hostLost() { sliders.forEach(c => c.cancel()); },
    };
  }

  const api = { createCalibrations, FULL, MIN_SPAN, END_BAND, SWEEP_TIMEOUT_MS, REPORT_INTERVAL_MS };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoCalibration = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);

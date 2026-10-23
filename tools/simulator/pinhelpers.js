// The controller's pin helpers, mirroring the firmware's (issue #120): PinWatch (reports the pin of a
// slider that moved or a button that was pressed), I2cScan (reports the screens on a bus) and
// PinDrive (lights an LED pin for a while, then gives it back). The simulator's controls are its
// config's: slider i is on config.sliders[i].pin, button i on config.buttons[i].pin. Time is passed
// in. No DOM: index.html loads it as a plain script (globalThis.VolcoPinHelpers), the tests with
// require().
(function (root) {
  'use strict';
  const P = typeof module === 'object' && module.exports ? require('./protocol.js') : root.VolcoProtocol;

  const REPORT_GAP_MS = 250, WATCH_TIMEOUT_MS = 30000;

  /** The screen addresses on the configured bus whose pins are (sda, scl), sorted, once each; [] when
   *  the pins are no configured bus. */
  function screensOn(config, sda, scl) {
    const found = new Set();
    for (let n = 0; n < 4; n++) {
      const bus = config[`i2c${n}`];
      if (!bus || bus.sda !== sda || bus.scl !== scl) continue;
      for (const b of config.buttons || [])
        if (b.screen && (b.screen.bus ?? 0) === n) found.add(b.screen.address ?? 0x3C);
    }
    return [...found].sort((a, b) => a - b);
  }

  /**
   * @param {object} o
   * @param {(payload:number[]) => void} o.send                writes a payload (the Link's sendPayload)
   * @param {object} o.config                                  the stored config (sliders, buttons, i2c0..3)
   * @param {(button:number, value:number) => void} o.showLed  puts an LED value on a button's LED(s)
   * @param {(button:number) => number} [o.lastLed]            the PC's last LED value for a button
   */
  function createPinHelpers(o) {
    const lastLed = o.lastLed || (() => 0);
    const sliders = o.config.sliders || [], buttons = o.config.buttons || [];
    let role = P.WATCH.STOP, lastWatch = 0;
    const reported = new Map();                   // pin → when it was last reported
    const drives = new Map();                     // pin → { button, side, lit, until }

    function active(now) {
      if (role !== P.WATCH.STOP && now - lastWatch >= WATCH_TIMEOUT_MS) role = P.WATCH.STOP;
      return role;
    }
    function report(pin, now) {
      if (pin === undefined) return;
      const last = reported.get(pin);
      if (last !== undefined && now - last < REPORT_GAP_MS) return;
      reported.set(pin, now);
      o.send(P.pinActivity(pin));
    }
    /** The LED value a button shows: the PC's last value with each driven side overridden. */
    function valueOf(button) {
      const b = buttons[button];
      const last = lastLed(button);
      const mine = [...drives.values()].filter(d => d.button === button);
      if (!b.ledPair) return mine.length ? (mine[0].lit ? 1 : 0) : last;
      const lit = [last === 1, last === 2];
      for (const d of mine) lit[d.side] = d.lit;
      if (lit[0] && lit[1]) {   // both sides cannot show: a driven one wins
        const d = mine.find(x => x.lit);
        return d ? d.side + 1 : 1;
      }
      return lit[0] ? 1 : lit[1] ? 2 : 0;
    }
    function release(pin) {
      const d = drives.get(pin);
      if (!d) return;
      drives.delete(pin);
      o.showLed(d.button, valueOf(d.button));
    }
    /** The button, side and active level of the LED on a pin, or null. */
    function ledOn(pin) {
      for (let i = 0; i < buttons.length; i++) {
        const b = buttons[i];
        if (b.led && b.led.pin === pin) return { button: i, side: 0, activeLow: !!b.led.activeLow };
        if (b.ledPair && b.ledPair.pinA === pin) return { button: i, side: 0, activeLow: !!b.ledPair.activeLow };
        if (b.ledPair && b.ledPair.pinB === pin) return { button: i, side: 1, activeLow: !!b.ledPair.activeLow };
      }
      return null;
    }

    return {
      /** PinWatch: role 0 stops, 1 watches sliders, 2 watches buttons; each frame renews the 30 s. */
      watch(r, now) {
        reported.clear();
        role = r;
        lastWatch = now;
      },
      /** Slider i moved. */
      slider(i, now) { if (active(now) === P.WATCH.ANALOG && sliders[i]) report(sliders[i].pin, now); },
      /** Button i was pressed. */
      button(i, now) { if (active(now) === P.WATCH.BUTTON && buttons[i]) report(buttons[i].pin, now); },
      /** I2cScan: answers with the screens on the bus with these pins. */
      scan(sda, scl) { o.send(P.i2cScanResult(sda, scl, screensOn(o.config, sda, scl))); },
      /** PinDrive: level is the raw pin level; the LED is lit when it differs from activeLow. */
      drive(pin, level, ms, now) {
        const led = ledOn(pin);
        if (!led) return;
        drives.set(pin, { button: led.button, side: led.side, lit: (level === 1) !== led.activeLow, until: now + ms });
        o.showLed(led.button, valueOf(led.button));
      },
      /** True while a drive holds this button's LED (the PC's writes wait). */
      driven: button => [...drives.values()].some(d => d.button === button),
      /** Every pass: drives that ran out give their LED back. */
      tick(now) { for (const [pin, d] of [...drives]) if (now >= d.until) release(pin); },
      /** The PC is gone: the watch stops and driven LEDs are given back. */
      hostLost() {
        role = P.WATCH.STOP;
        for (const pin of [...drives.keys()]) release(pin);
      },
    };
  }

  const api = { createPinHelpers, screensOn, REPORT_GAP_MS, WATCH_TIMEOUT_MS };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoPinHelpers = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);

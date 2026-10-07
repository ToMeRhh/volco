// The controller side of a session, mirroring firmware/lib/volco_protocol's Link (spec §3.4):
// Hello, keepalive, host timeout, Begin/Commit, plus the simulator's fault hooks. Time is passed
// in. No DOM: index.html loads it as a plain script (globalThis.VolcoLink), the tests with require().
(function (root) {
  'use strict';
  const P = typeof module === 'object' && module.exports ? require('./protocol.js') : root.VolcoProtocol;

  const TIMING = { hostTimeoutMs: 3000, keepaliveMs: 1000, commitFallbackMs: 500 };

  /**
   * @param {object} o
   * @param {(bytes:Uint8Array, payload:number[], bad:boolean) => void} o.sendFrame  writes one frame
   * @param {(index:number) => number} o.sliderValue        a slider's position, 0..4095
   * @param {(button:number, value:number) => void} o.onLed
   * @param {(button:number, transition:number, content:object, now:number) => void} o.onScreen
   * @param {(now:number) => void} o.onHostLost            LEDs off, "not connected" screens
   * @param {(payload:number[], now:number) => boolean} [o.onConfigFrame]  true when it took the frame
   */
  function createLink(o) {
    let hw = { sliders: 0, kinds: [], configState: 0, configCrc: 0, firmware: 'sim' };
    let hostAlive = false, lastRx = 0, lastTx = -Infinity, holding = false, holdStart = 0, held = [];
    let corrupt = false;
    const faults = { silent: false, ignoreCommit: false };

    const isLed = b => b < hw.kinds.length && (hw.kinds[b] === P.KIND.LED || hw.kinds[b] === P.KIND.LED_PAIR);
    const isScreen = b => b < hw.kinds.length && hw.kinds[b] === P.KIND.SCREEN;

    function sendPayload(payload, now) {
      if (faults.silent) return;
      const bytes = P.frame(payload);
      const bad = corrupt;
      if (bad) { bytes[bytes.length - 1] ^= 0xFF; corrupt = false; }
      o.sendFrame(bytes, payload, bad);
      lastTx = now;
    }
    function sendHello(now) {
      sendPayload(P.hello(hw), now);
      for (let i = 0; i < hw.sliders; i++) sendPayload(P.slider(i, o.sliderValue(i)), now);
    }
    function applyHeld(now) {
      holding = false;
      held.forEach((h, b) => {
        if (!h) return;
        if (h.led !== undefined) o.onLed(b, h.led);
        if (h.screen) o.onScreen(b, h.transition, h.screen, now);
      });
      held = [];
    }

    return {
      faults,
      /** The hardware Hello reports and the kinds Led/Screen are filtered by. */
      setHardware(h) { hw = { firmware: 'sim', ...h }; held = []; },
      hardware: () => hw,
      /** Boot: Hello, as the firmware sends it when it starts. */
      begin(now) { lastRx = now; hostAlive = false; holding = false; held = []; sendHello(now); },
      onPayload(p, now) {
        if (faults.silent || p.length < 1) return;
        lastRx = now;
        const type = p[0];
        if (!hostAlive) {
          hostAlive = true;
          if (type !== P.MSG.HELLO_REQUEST) sendHello(now);   // the PC is back: announce the hardware
        }
        if (o.onConfigFrame && o.onConfigFrame(p, now)) return;
        switch (type) {
          case P.MSG.HELLO_REQUEST: sendHello(now); break;
          case P.MSG.LED:
            if (p.length >= 3 && isLed(p[1])) {
              if (holding) held[p[1]] = { ...held[p[1]], led: p[2] };
              else o.onLed(p[1], p[2]);
            }
            break;
          case P.MSG.SCREEN: {
            const s = P.decodeScreen(p);
            if (!s || !isScreen(s.button)) break;
            if (holding) {
              const h = held[s.button] || {};
              // A newer instant update keeps a held slide (spec §2.3).
              const transition = h.screen && s.transition === P.TRANSITION.NONE ? h.transition : s.transition;
              held[s.button] = { ...h, screen: s.content, transition };
            } else o.onScreen(s.button, s.transition, s.content, now);
            break;
          }
          case P.MSG.BEGIN: holding = true; holdStart = now; break;
          case P.MSG.COMMIT: if (holding && !faults.ignoreCommit) applyHeld(now); break;
          default: break;   // Heartbeat (its arrival is the point) and unknown types
        }
      },
      tick(now) {
        if (holding && now - holdStart >= TIMING.commitFallbackMs) applyHeld(now);
        if (hostAlive && now - lastRx >= TIMING.hostTimeoutMs) {
          hostAlive = false; holding = false; held = [];
          o.onHostLost(now);
        }
        if (now - lastTx >= TIMING.keepaliveMs) sendPayload(P.alive(), now);
      },
      sendSlider(index, value, now) { sendPayload(P.slider(index, value), now); },
      sendButton(index, pressed, now) { sendPayload(P.button(index, pressed), now); },
      /** Any other payload (the config session's replies). */
      sendPayload,
      /** One-shot: the next frame sent goes out with its checksum XORed with 0xFF. */
      corruptNext() { corrupt = true; },
      corruptArmed: () => corrupt,
      hostAlive: () => hostAlive,
      holding: () => holding,
    };
  }

  const api = { createLink, TIMING };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoLink = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);

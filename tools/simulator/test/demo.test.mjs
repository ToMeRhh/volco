// Node tests of tools/simulator/demo.js: the demo host and reading the output from Volco screens.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const P = require('../protocol.js');
const D = require('../demo.js');

test('output-related screens name the current output', () => {
  const s = (top, icon, flags = 0) => ({ ...P.screenText(top, icon, 'x'), flags });
  assert.deepEqual(D.outputFromScreen(s('NEXT: Arctis', 2)), { device: 'speakers', muted: false });
  assert.deepEqual(D.outputFromScreen(s('NEXT: Desk', 4)), { device: 'headphones', muted: false });
  assert.deepEqual(D.outputFromScreen(s('ACTIVE', 6)), { device: 'speakers', muted: false });
  assert.deepEqual(D.outputFromScreen(s('MUTED', 5, 1)), { device: 'headphones', muted: true });
  assert.deepEqual(D.outputFromScreen(s('LIVE', 1)), { device: 'speakers', muted: false });
  assert.equal(D.outputFromScreen(s('MUTED', 7, 1)), null);    // a microphone
  assert.equal(D.outputFromScreen(s('', 2)), null);             // a set-output for another device
  assert.equal(D.outputFromScreen(s('NOT CONNECTED', 255)), null);
  assert.equal(D.outputFromScreen(null), null);
});

test('the demo host gives every button a display to start with', () => {
  const host = D.createDemoHost([3, 3, 1, 2]);
  const init = host.initial();
  assert.equal(init.length, 4);
  assert.deepEqual(D.outputFromScreen(init[0].content), { device: 'speakers', muted: false });
  assert.equal(init[1].content.top, 'LIVE');
  assert.deepEqual(init.slice(2), [{ button: 2, led: 0 }, { button: 3, led: 1 }]);
});

test('the output screen and the LED pair toggle the same output', () => {
  const host = D.createDemoHost([3, 3, 1, 2]);
  const a = host.press(3);   // the LED pair
  assert.equal(host.state.output, 'headphones');
  assert.deepEqual(a.map(u => u.button), [0, 3]);
  assert.equal(a[0].transition, P.TRANSITION.SLIDE_LEFT);
  assert.deepEqual(D.outputFromScreen(a[0].content), { device: 'headphones', muted: false });
  assert.equal(a[1].led, 2);
  host.press(0);   // the output screen
  assert.equal(host.state.output, 'speakers');
  assert.equal(host.overlay(0), 'Speakers');
});

test('mutes toggle, presets cycle, plain buttons show nothing', () => {
  const host = D.createDemoHost([3, 3, 3, 3, 1, 0]);
  assert.equal(host.press(1)[0].content.top, 'MUTED');
  assert.equal(host.press(2)[0].content.dotsSelected, 1);
  assert.equal(host.press(3)[0].content.main, 'Discord');
  assert.deepEqual(host.press(4), [{ button: 4, led: 1 }]);
  assert.equal(host.state.outputMuted, true);
  assert.deepEqual(host.press(5), []);
  assert.equal(host.overlay(5), null);
});

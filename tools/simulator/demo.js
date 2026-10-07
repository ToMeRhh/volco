// Demo mode (spec §8.2): while no WebSocket is open the simulator is its own host and answers
// presses the way Volco would. Also reads which output device a Volco screen shows (§7.3), which
// drives the sound waves. No DOM: index.html loads it as a plain script (globalThis.VolcoDemo),
// the Node tests with require().
(function (root) {
  'use strict';
  const P = typeof module === 'object' && module.exports ? require('./protocol.js') : root.VolcoProtocol;

  const ICON = { SPEAKER: 1, SPEAKERS: 2, HEADPHONES: 3, HEADSET: 4, EARBUDS: 5, DISPLAY: 6, STUDIOMIC: 8, PRESETS: 10 };
  const SPEAKER_ICONS = [ICON.SPEAKER, ICON.SPEAKERS, ICON.DISPLAY];
  const HEADPHONE_ICONS = [ICON.HEADPHONES, ICON.HEADSET, ICON.EARBUDS];

  /** What an output-related Volco screen says: { device: 'speakers'|'headphones', muted }, or
   *  null when the screen isn't about the current output (spec §7.3). */
  function outputFromScreen(c) {
    if (!c || c.layout !== 1 || c.topKind !== P.TOP.TEXT) return null;
    const device = SPEAKER_ICONS.includes(c.icon) ? 'speakers' : HEADPHONE_ICONS.includes(c.icon) ? 'headphones' : null;
    if (!device) return null;
    if (c.top.startsWith('NEXT:') || c.top === 'ACTIVE') return { device, muted: false };
    if (c.top === 'LIVE' || c.top === 'MUTED') return { device, muted: c.top === 'MUTED' };
    return null;
  }

  const DEVICES = { speakers: { icon: ICON.SPEAKERS, name: 'Speakers' }, headphones: { icon: ICON.HEADPHONES, name: 'Headphones' } };
  const PRESETS = ['Desk', 'Gaming', 'Calls'];
  /** Which demo role each screen gets, in order: output, microphone, presets, then Discord. */
  const ROLES = ['output', 'mic', 'presets'];

  /** kinds: one protocol KIND per button. Returns the demo host. */
  function createDemoHost(kinds) {
    const state = { output: 'speakers', outputMuted: false, micMuted: false, preset: 0, discordMuted: false };
    const screenRole = [];
    let n = 0;
    kinds.forEach((k, i) => { if (k === P.KIND.SCREEN) screenRole[i] = ROLES[n++] || 'discord'; });

    function content(i) {
      const role = screenRole[i];
      if (role === 'output') {
        const d = DEVICES[state.output], other = DEVICES[state.output === 'speakers' ? 'headphones' : 'speakers'];
        return P.screenText(`NEXT: ${other.name}`, d.icon, d.name);
      }
      if (role === 'mic') return { ...P.screenText(state.micMuted ? 'MUTED' : 'LIVE', ICON.STUDIOMIC, 'Shure SM7B'), flags: state.micMuted ? 1 : 0 };
      if (role === 'presets') return { ...P.screenText('', ICON.PRESETS, PRESETS[state.preset]), topKind: P.TOP.DOTS, dotsSelected: state.preset, dotsCount: PRESETS.length };
      return { ...P.screenText(state.discordMuted ? 'MUTED' : 'LIVE', ICON.HEADSET, 'Discord'), flags: state.discordMuted ? 1 : 0 };
    }
    const led = i => kinds[i] === P.KIND.LED_PAIR ? (state.output === 'speakers' ? 1 : 2) : state.outputMuted ? 1 : 0;
    const update = (i, transition = P.TRANSITION.NONE) =>
      kinds[i] === P.KIND.SCREEN ? { button: i, transition, content: content(i) } : { button: i, led: led(i) };
    const all = (transition, pick = () => true) =>
      kinds.map((k, i) => (k !== P.KIND.NONE && pick(i) ? update(i, transition) : null)).filter(Boolean);
    const outputBound = i => kinds[i] === P.KIND.LED_PAIR || screenRole[i] === 'output';

    return {
      state,
      /** Every button's display, for a fresh board. */
      initial: () => all(P.TRANSITION.NONE),
      /** A press: the new state, and the updates for every button it changed. */
      press(i) {
        const role = screenRole[i];
        if (kinds[i] === P.KIND.LED_PAIR || role === 'output') {
          state.output = state.output === 'speakers' ? 'headphones' : 'speakers';
          return all(P.TRANSITION.SLIDE_LEFT, outputBound);
        }
        if (kinds[i] === P.KIND.LED) { state.outputMuted = !state.outputMuted; return all(P.TRANSITION.NONE, j => kinds[j] === P.KIND.LED); }
        if (role === 'mic') state.micMuted = !state.micMuted;
        else if (role === 'presets') { state.preset = (state.preset + 1) % PRESETS.length; return [update(i, P.TRANSITION.SLIDE_LEFT)]; }
        else if (role === 'discord') state.discordMuted = !state.discordMuted;
        return kinds[i] === P.KIND.NONE ? [] : [update(i)];
      },
      /** The monitor overlay's text for a press (null when the press shows nothing). */
      overlay(i) {
        const role = screenRole[i];
        if (kinds[i] === P.KIND.LED_PAIR || role === 'output') return DEVICES[state.output].name;
        if (kinds[i] === P.KIND.LED) return state.outputMuted ? 'Output muted' : 'Output on';
        if (role === 'mic') return state.micMuted ? 'Microphone muted' : 'Microphone on';
        if (role === 'presets') return `Preset: ${PRESETS[state.preset]}`;
        if (role === 'discord') return state.discordMuted ? 'Discord muted' : 'Discord on';
        return null;
      },
    };
  }

  const api = { outputFromScreen, createDemoHost, ICON };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VolcoDemo = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);

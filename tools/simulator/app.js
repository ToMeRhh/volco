// Wires the simulator together (spec §9): the stored config, the link to Volco, the screens and
// LEDs, demo mode, the builder, the connection, the drawer, and the 3D scene once three.js is in.
(function (root) {
  'use strict';
  const P = root.VolcoProtocol, L = root.VolcoLayout, O = root.VolcoOled, D = root.VolcoDemo;
  const $ = id => document.getElementById(id);
  const now = () => performance.now();

  const CONFIG_KEY = 'volco-sim-config', PREFS_KEY = 'volco-sim-prefs';
  const DEFAULT_PREFS = { host: 'localhost', port: '8765', reconnect: true, autoConnect: false, autosave: false,
    finish: 'silver', panelOpen: true, drawerOpen: false, drawerTab: 'frames' };
  const SLIDER_NAMES = ['Master', 'Spotify', 'Discord', 'Firefox', 'Game', 'Chrome', 'Zoom', 'Steam'];
  const BOARD = { x: 0, y: 3, z: -5 };   // where device.js puts the board; the camera's target
  const BLANK = new Uint8Array(1024);
  const SLIDER_MS = 20, REBOOT_MS = 1000, TICK_MS = 50;

  // ── Storage (spec §11): every access guarded; without storage, values live in memory ──
  let memoryOnly = false;
  function read(key) { try { return localStorage.getItem(key); } catch { memoryOnly = true; return null; } }
  function write(key, value) {
    try { if (value === null) localStorage.removeItem(key); else localStorage.setItem(key, value); } catch { memoryOnly = true; }
  }
  const prefs = { ...DEFAULT_PREFS };
  try { Object.assign(prefs, JSON.parse(read(PREFS_KEY) || '{}')); } catch { /* damaged prefs: defaults */ }
  const savePrefs = p => { Object.assign(prefs, p); write(PREFS_KEY, JSON.stringify(prefs)); };
  let stored = null;
  try { const t = read(CONFIG_KEY); stored = t ? Uint8Array.from(atob(t), c => c.charCodeAt(0)) : null; } catch { stored = null; }
  function store(blob) {
    stored = blob && blob.length ? Uint8Array.from(blob) : null;
    write(CONFIG_KEY, stored ? btoa(String.fromCharCode(...stored)) : null);
  }

  // ── State ──
  let mode = 'demo';            // 'demo' (no socket) or 'live'
  let rebooting = false, openWhileRebooting = false;
  let hw, beh, slots = [], leds = [], panels, demoHost;
  let boardKinds = [];          // what the 3D board shows: the hardware, or the builder's draft
  let output = { device: 'speakers', muted: false };
  let stage = null, room = null, device = null, builder = null, devtools = null, connection = null;

  // ── Config session and link ──
  let wroteByVolco = false;
  const session = root.protobuf && root.VolcoSimConfig ? root.VolcoSimConfig.createSession({
    protobuf: root.protobuf, descriptor: root.VOLCO_CONFIG_DESCRIPTOR,
    send: p => link.sendPayload(p, now()),
    store: blob => { store(blob); wroteByVolco = true; },
    stored: () => stored,
    reboot: () => setTimeout(() => reboot(true), 50),   // after the OK has gone out
  }) : null;

  /** The stored config as the board runs it: none or damaged boots the DIY reference (spec §12). */
  function current() {
    const c = session ? session.current() : { state: 0, crc: 0, config: null, reason: null };
    const config = c.config || L.DIY_REFERENCE;
    const layout = L.fromConfig(config);
    return { ...c, config, layout, sliders: layout.sliders.length, kinds: L.kinds(layout), size: stored ? stored.length : 0 };
  }

  const link = root.VolcoLink.createLink({
    sendFrame(bytes, payload, bad) {
      connection.send(bytes);
      devtools.frame('→', payload, bad);
      if (payload[0] === P.MSG.CONFIG_RESULT) devtools.resetForcedResult();
    },
    sliderValue: i => Math.round((device ? device.faderValue(i) : 0.5) * 4095),
    onLed: (b, v) => show({ button: b, led: v }),
    onScreen: (b, transition, content, t) => show({ button: b, transition, content }, t),
    onHostLost: t => blankDisplays(t),
    onConfigFrame: (p, t) => (session ? session.onFrame(p, t) : false),
  });
  const parser = P.createParser(
    p => { devtools.frame('←', p); link.onPayload(p, now()); },
    () => devtools.note('Dropped a frame with a bad checksum'));

  // ── Displays ──
  function show(u, t = now()) {
    if (u.led !== undefined) {
      leds[u.button] = u.led;
      if (boardKinds[u.button] === hw.kinds[u.button]) device?.setLed(u.button, u.led);
      return;
    }
    const slot = slots[u.button];
    if (!slot) return;
    slot.show(u.content, u.transition, t, panels.power(t) === O.POWER.OFF);
    if (mode === 'live') {
      const r = D.outputFromScreen(u.content);   // spec §7.3: the waves follow output screens
      if (r) { output = r; room?.setOutput(r.device, r.muted); }
    }
  }
  function blankDisplays(t = now()) {
    hw.kinds.forEach((k, i) => {
      if (k === P.KIND.LED || k === P.KIND.LED_PAIR) show({ button: i, led: 0 }, t);
      if (slots[i]) slots[i].show(O.NOT_CONNECTED, P.TRANSITION.NONE, t, false);
    });
  }
  function applyLeds() {
    boardKinds.forEach((k, i) => {
      if ((k === P.KIND.LED || k === P.KIND.LED_PAIR) && hw.kinds[i] === k) device?.setLed(i, leds[i] ?? 0);
    });
  }
  function renderScreens(t) {
    if (!device) return;
    const power = panels.power(t);
    const level = rebooting || power === O.POWER.OFF ? 0
      : 0.25 + 0.75 * (power === O.POWER.DIM ? beh.dimBrightness : beh.brightness) / 255;
    boardKinds.forEach((k, i) => {
      if (k !== P.KIND.SCREEN) return;
      const frame = !rebooting && hw.kinds[i] === P.KIND.SCREEN && slots[i] ? O.frame(slots[i], panels, t, beh.scroll) : BLANK;
      device.setScreen(i, frame, level);
    });
  }

  // ── Boot, demo mode, reboot ──
  function boot({ drop = false } = {}) {
    hw = current();
    beh = O.behaviour(hw.config.screens);
    link.setHardware({ sliders: hw.sliders, kinds: hw.kinds, configState: hw.state, configCrc: hw.crc });
    slots = []; leds = [];
    hw.kinds.forEach((k, i) => {
      if (k !== P.KIND.SCREEN) return;
      slots[i] = O.createSlot(beh.transitionMs);
      slots[i].show(O.NOT_CONNECTED, P.TRANSITION.NONE, now(), false);
    });
    panels = O.createPanels(beh.panels, hw.kinds.includes(P.KIND.SCREEN));
    panels.activity(now());
    boardKinds = hw.kinds;
    device?.build({ sliders: hw.sliders, kinds: hw.kinds }, { drop });
    if (connection?.isOpen()) goLive(); else enterDemo();
  }
  function enterDemo() {
    mode = 'demo';
    demoHost = D.createDemoHost(hw.kinds);
    for (const u of demoHost.initial()) show(u);
    output = { device: demoHost.state.output, muted: demoHost.state.outputMuted };
    room?.setOutput(output.device, output.muted);
    applyLeds();
  }
  function goLive() {
    mode = 'live';
    blankDisplays();
    output = { device: 'speakers', muted: false };
    room?.setOutput('speakers', false);
    link.begin(now());   // the firmware's boot Hello
  }
  function reboot(byVolco = false) {
    if (rebooting) return;
    rebooting = true;
    device?.setStatus(false);
    devtools.note(byVolco ? 'Volco saved a config: rebooting' : 'Rebooting');
    connection.reboot();
    setTimeout(() => {
      rebooting = false;
      device?.setStatus(true);
      boot();
      builder.reload(byVolco && wroteByVolco);
      wroteByVolco = false;
      devtools.refreshConfig();
      if (openWhileRebooting) { openWhileRebooting = false; if (connection.isOpen()) goLive(); }
    }, REBOOT_MS);
  }

  // ── Input from the board ──
  const lastSlider = [], pendingSlider = [];
  function onSlider(i, value) {
    panels.activity(now());
    room?.showOverlay(mode === 'demo' ? SLIDER_NAMES[i % SLIDER_NAMES.length] : `Slider ${i + 1}`, value);
    if (mode !== 'live' || rebooting || i >= hw.sliders) return;
    const send = () => { lastSlider[i] = now(); link.sendSlider(i, Math.round(value * 4095), now()); };
    clearTimeout(pendingSlider[i]);
    if (now() - (lastSlider[i] || 0) >= SLIDER_MS) send();
    else pendingSlider[i] = setTimeout(send, SLIDER_MS);   // the last position always goes out
  }
  function onButton(i, pressed) {
    const real = i < hw.kinds.length && boardKinds[i] === hw.kinds[i] && !rebooting;
    if (!real) return;   // a draft-only button: the controller doesn't have it yet
    if (!panels.filterButton(i, pressed, now())) return;   // the press that woke the screens
    if (mode === 'live') { link.sendButton(i, pressed, now()); return; }
    if (!pressed) return;
    for (const u of demoHost.press(i)) show(u);
    room?.setOutput(demoHost.state.output, demoHost.state.outputMuted);
    const text = demoHost.overlay(i);
    if (text) room?.showOverlay(text, -1);
  }

  // ── Layout of the floating panels: keep the board centred in the free space ──
  function frameView() {
    if (!stage) return;
    const wide = innerWidth >= 720, panelOpen = !$('panel').hidden, r = $('panel').getBoundingClientRect();
    const x = panelOpen && wide ? (r.width + 16) / 2 : 0;
    const y = panelOpen && !wide ? (r.height + 12) / 2 : !$('drawer').hidden && wide ? 130 : 0;
    stage.setOffset(x, y);
  }
  function setPanel(open) {
    $('panel').hidden = !open;
    $('toggle').setAttribute('aria-expanded', String(open));
    savePrefs({ panelOpen: open });
    frameView();
  }

  // ── Start ──
  devtools = root.VolcoDevtools.create({
    config: () => { const c = current(); return { ...c, preset: c.config.preset, memoryOnly, unavailable: !session }; },
    configJson: () => JSON.stringify(session?.current().config ?? {}, null, 2),
    onClearConfig: () => { store(null); reboot(); },
    onReboot: () => reboot(),
    link,
    forceNextResult: code => session?.forceNextResult(code),
    prefs, onPrefs: savePrefs, onToggle: frameView,
  });
  connection = root.VolcoConnection.create({
    settings: prefs,
    onOpen: () => {
      savePrefs({ autoConnect: true });
      devtools.setConnected(true);
      devtools.note(`Connected to ${connection.address()}`);
      if (rebooting) openWhileRebooting = true; else goLive();
    },
    onBytes: bytes => parser.feed(bytes),
    onClose: () => {
      devtools.setConnected(false);
      if (!rebooting) { devtools.note('Disconnected'); enterDemo(); }
    },
    onSettings: savePrefs,
  });
  boot();
  builder = root.VolcoBuilder.create({
    stored: () => current().layout,
    unavailable: () => (session ? null : 'Saving needs protobufjs, which did not load (offline?).'),
    onPreview(layout, { drop = false } = {}) {
      boardKinds = L.kinds(layout);
      device?.build({ sliders: layout.sliders.length, kinds: boardKinds }, { drop });
      applyLeds();
    },
    onApply(layout) {
      const r = L.toConfig(layout);
      if (r.error) return r.error.message;
      store(session.encode(r.config));
      reboot();
      return null;
    },
    prefs, onPrefs: savePrefs,
    onFinish: name => device?.setFinish(name),
  });
  $('toggle').addEventListener('click', () => setPanel($('panel').hidden));
  setPanel(prefs.panelOpen);
  new ResizeObserver(frameView).observe($('panel'));
  addEventListener('resize', frameView);
  setInterval(() => {
    const t = now();
    session?.tick(t);
    if (mode === 'live' && !rebooting) link.tick(t);
  }, TICK_MS);
  if (prefs.autoConnect) connection.connect();

  // Built over several tasks (stage, room, board, shaders), so the page never stalls for long.
  const nextTask = () => new Promise(r => setTimeout(r, 0));
  async function startScene() {
    try {
      stage = root.VolcoStage.create($('stage'), {
        target: BOARD,
        onFrame(t, dt) { room.update(t, dt); device.update(t, dt); renderScreens(t); },
      });
    } catch {
      showSceneFailure();
      return;
    }
    await nextTask();
    room = await root.VolcoRoom.build(stage);
    room.setOutput(output.device, output.muted);
    await nextTask();
    device = root.VolcoDevice.create(stage, { onSlider, onButton });
    device.setFinish(prefs.finish);
    if (builder.isDirty()) builder.reload();   // a draft started before the scene loaded: start from the stored board
    boardKinds = hw.kinds;
    device.build({ sliders: hw.sliders, kinds: hw.kinds }, { drop: true });
    applyLeds();
    frameView();
    document.fonts.ready.then(() => device.setFinish(prefs.finish));   // the wordmark's font
    await nextTask();
    await stage.start();
    device.drop();   // the board drops in on the first frame, not while the shaders compiled
  }
  function showSceneFailure() { $('scene-failed').hidden = false; }
  if (root.VolcoThree) startScene();
  else {
    addEventListener('volco-three', startScene, { once: true });
    addEventListener('volco-three-failed', showSceneFailure, { once: true });
  }
})(globalThis);

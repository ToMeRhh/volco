// The developer drawer (spec §10): the frame log, the stored config, and fault injection.
// Browser only; uses protocol.js for frame names.
(function (root) {
  'use strict';
  const P = root.VolcoProtocol, L = root.VolcoLayout;
  const KEEP = 400, SHOW = 160;
  const KIND_NAMES = ['plain', 'LED', 'LED pair', 'screen'];
  const CODES = ['OK', 'BAD_SEQUENCE', 'BAD_CRC', 'TOO_LARGE', 'DECODE_FAILED', 'INVALID'];

  /**
   * @param {object} o
   * @param {() => object} o.config      { state, crc, size, preset, sliders, kinds, config, reason, memoryOnly, unavailable }
   * @param {() => string} o.configJson  the stored config as protobuf JSON
   * @param {() => void} o.onClearConfig
   * @param {() => void} o.onReboot
   * @param {object} o.link              link.js (faults, corruptNext, corruptArmed)
   * @param {(code:number|null) => void} o.forceNextResult
   * @param {{drawerOpen:boolean, drawerTab:string}} o.prefs
   * @param {(prefs:object) => void} o.onPrefs
   * @param {() => void} o.onToggle      the drawer opened or closed (to re-centre the board)
   */
  function create(o) {
    const $ = id => document.getElementById(id);
    const rows = [];
    let paused = false, dirty = true, t0 = performance.now(), connected = false;

    function push(r) {
      if (paused) return;
      rows.unshift(r);
      if (rows.length > KEEP) rows.pop();
      dirty = true;
    }
    function render() {
      const pane = $('pane-frames');
      if (!dirty || $('drawer').hidden || pane.hidden) return;
      dirty = false;
      const hideKa = $('hide-ka').checked;
      const shown = rows.filter(r => !(hideKa && r.payload && (r.payload[0] === P.MSG.ALIVE || r.payload[0] === P.MSG.HEARTBEAT))).slice(0, SHOW);
      if (!shown.length) {
        const e = Object.assign(document.createElement('div'), { className: 'empty' });
        e.textContent = connected ? 'Waiting for frames.' : 'Frames appear here once Volco connects.';
        pane.replaceChildren(e);
        return;
      }
      pane.replaceChildren(...shown.map(r => {
        const el = Object.assign(document.createElement('div'), { className: 'r' + (r.bad ? ' bad' : '') });
        const cell = (cls, text) => Object.assign(document.createElement('span'), { className: cls, textContent: text });
        el.append(cell('t', ((r.t - t0) / 1000).toFixed(3)), cell(r.dir === '←' ? 'in' : r.dir === '→' ? 'out' : 't', r.dir),
          cell('n', r.text ? '' : P.nameOf(r.payload[0]) + (r.bad ? ' ✕' : '')), cell('x', r.text || P.hex(r.payload)));
        return el;
      }));
    }
    setInterval(render, 200);

    function renderConfig() {
      const c = o.config();
      $('cfg-state').textContent = c.unavailable ? 'Unavailable: protobufjs did not load (offline?)'
        : c.state === 0 ? 'None' : c.state === 1 ? 'Valid' : `Invalid: ${c.reason}`;
      $('cfg-size').textContent = `${c.size} bytes`;
      $('cfg-crc').textContent = c.state === 0 ? '—' : c.crc.toString(16).toUpperCase().padStart(8, '0');
      $('cfg-preset').textContent = c.preset || '—';
      $('cfg-hw').textContent = `${c.sliders} sliders · ${c.kinds.map(k => KIND_NAMES[k]).join(', ') || 'no buttons'}`;
      $('cfg-pins').textContent = L.pinMap(c.config).map(r => `${r.label}: ${r.value}`).join('\n') || '—';
      $('cfg-memory-row').hidden = !c.memoryOnly;
    }

    // Frames tab
    $('hide-ka').addEventListener('change', () => { dirty = true; render(); });
    $('pause').addEventListener('click', () => { paused = !paused; $('pause').textContent = paused ? 'Resume' : 'Pause'; });
    $('clear-log').addEventListener('click', () => { rows.length = 0; dirty = true; render(); });
    // Config tab
    $('cfg-clear').addEventListener('click', () => o.onClearConfig());
    $('cfg-copy').addEventListener('click', async e => {
      try { await navigator.clipboard.writeText(o.configJson()); e.target.textContent = 'Copied'; }
      catch { e.target.textContent = 'Copy failed'; }
      setTimeout(() => (e.target.textContent = 'Copy as JSON'), 1200);
    });
    // Faults tab
    $('f-silent').addEventListener('change', e => { o.link.faults.silent = e.target.checked; });
    $('f-commit').addEventListener('change', e => { o.link.faults.ignoreCommit = e.target.checked; });
    $('f-corrupt').addEventListener('click', () => { o.link.corruptNext(); syncCorrupt(); });
    $('f-cfg').replaceChildren(new Option('Normal', ''), ...CODES.map((c, i) => new Option(c, String(i))));
    $('f-cfg').addEventListener('change', e => o.forceNextResult(e.target.value === '' ? null : +e.target.value));
    $('f-reboot').addEventListener('click', () => o.onReboot());
    function syncCorrupt() {
      const armed = o.link.corruptArmed();
      $('f-corrupt').textContent = armed ? 'Armed' : 'Arm';
      $('f-corrupt').classList.toggle('armed', armed);
    }

    // Drawer and tabs
    function showTab(id) {
      for (const t of ['frames', 'config', 'faults']) {
        $(`tab-${t}`).setAttribute('aria-selected', String(t === id));
        $(`pane-${t}`).hidden = t !== id;
      }
      $('frame-tools').style.visibility = id === 'frames' ? 'visible' : 'hidden';
      if (id === 'config') renderConfig();
      dirty = true; render();
      o.onPrefs({ drawerTab: id });
    }
    function setOpen(open) {
      $('drawer').hidden = !open;
      $('dev-btn').setAttribute('aria-expanded', String(open));
      o.onPrefs({ drawerOpen: open });
      dirty = true; render();
      o.onToggle();
    }
    $('dev-btn').addEventListener('click', () => setOpen($('drawer').hidden));
    for (const t of ['frames', 'config', 'faults']) $(`tab-${t}`).addEventListener('click', () => showTab(t));
    showTab(o.prefs.drawerTab || 'frames');
    setOpen(!!o.prefs.drawerOpen);

    return {
      /** One frame: dir '←' from Volco, '→' to Volco. */
      frame(dir, payload, bad = false) { push({ t: performance.now(), dir, payload, bad }); if (bad) syncCorrupt(); },
      note(text) { push({ t: performance.now(), dir: '·', text }); },
      /** The stored config changed. */
      refreshConfig: renderConfig,
      setConnected(on) {
        connected = on;
        if (on) t0 = performance.now();
        dirty = true;
      },
      /** The forced config answer was used: show "Normal" again. */
      resetForcedResult() { $('f-cfg').value = ''; },
    };
  }

  root.VolcoDevtools = { create };
})(globalThis);

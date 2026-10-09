// The builder panel (spec §6.1, §6.4): preset, sliders, buttons and finish, edited as a draft that
// previews live on the board; Apply (or Auto-save) stores it. Browser only; uses layout.js.
(function (root) {
  'use strict';
  const L = root.VolcoLayout;

  const KIND_UI = [
    { kind: L.KIND.NONE, label: 'Plain', article: 'no indicator', svg: '<rect x="3" y="3" width="10" height="10" rx="2.5"></rect>' },
    { kind: L.KIND.LED, label: 'LED', article: 'an LED', svg: '<circle cx="8" cy="8" r="2.6" fill="currentColor"></circle>' },
    { kind: L.KIND.LED_PAIR, label: 'LED pair', article: 'an LED pair', svg: '<circle cx="4.8" cy="8" r="2.3" fill="currentColor"></circle><circle cx="11.2" cy="8" r="2.3"></circle>' },
    { kind: L.KIND.SCREEN, label: 'Screen', article: 'a screen', svg: '<rect x="1.8" y="4" width="12.4" height="8" rx="1.5"></rect><path d="M4.5 9.5h7"></path>' },
  ];
  const AUTOSAVE_MS = 700;

  /**
   * @param {object} o
   * @param {() => object} o.stored                 the stored layout (layout.fromConfig)
   * @param {() => string|null} o.unavailable       why Apply can't work (no protobufjs), or null
   * @param {(layout:object) => void} o.onPreview   show this layout on the board
   * @param {(layout:object) => string|null} o.onApply  store it; returns an error message or null
   * @param {{autosave:boolean, finish:string, pinMapOpen:boolean}} o.prefs
   * @param {(prefs:object) => void} o.onPrefs
   * @param {(name:string) => void} o.onFinish
   */
  function create(o) {
    const $ = id => document.getElementById(id);
    const list = $('btns');
    let saved = o.stored(), draft = clone(saved), note = '', timer = 0;
    let blocked = null;   // { where: 'row'|'add'|'sliders', index, text }: why the control just clicked can't be used; cleared by the next edit
    let editedSinceApply = false;   // the draft changed after the last save started
    const prefs = { ...o.prefs };

    function clone(l) { return JSON.parse(JSON.stringify(l)); }
    const shape = l => JSON.stringify([l.preset, l.sliders, l.buttons]);
    const dirty = () => shape(draft) !== shape(saved);

    function edit(change) {
      draft = change(clone(draft));
      draft.preset = 'custom';
      note = ''; blocked = null;
      editedSinceApply = true;
      changed();
    }
    function changed() {
      o.onPreview(draft);
      render();
      clearTimeout(timer);
      if (prefs.autosave && dirty()) timer = setTimeout(apply, AUTOSAVE_MS);
    }
    function apply() {
      clearTimeout(timer);
      editedSinceApply = false;
      const error = o.onApply(draft);
      if (error) { note = error; render(); }
      // On success the controller reboots and calls reload() with the stored layout.
    }

    function render() {
      const why = o.unavailable();
      $('preset').value = L.PRESETS.some(p => p.id === draft.preset) ? draft.preset : 'custom';
      $('sl-n').textContent = draft.sliders.length;
      $('sl-dec').disabled = draft.sliders.length === 0;
      const addSlider = L.check(draft, l => ({ ...l, sliders: [...l.sliders, {}] }));
      block($('sl-inc'), addSlider, 'Add a slider', 'No room for another slider', { where: 'sliders' });
      const addButton = L.check(draft, l => ({ ...l, buttons: [...l.buttons, L.withKind({}, newKind())] }));
      block($('add'), addButton, newKind() === L.KIND.LED ? 'Add a button with an LED' : 'Add a plain button (no pins left for an LED)', 'No room for another button', { where: 'add' });
      list.replaceChildren(...draft.buttons.flatMap((b, i) => (blocked?.where === 'row' && blocked.index === i ? [row(b, i), hintRow(blocked.text)] : [row(b, i)])));
      renderPins(addSlider, addButton);
      $('apply').hidden = $('revert').hidden = prefs.autosave;
      $('apply').disabled = !dirty() || !!why;
      $('revert').disabled = !dirty();
      $('autosave').checked = prefs.autosave;
      $('builder-note').textContent = why || note;
      $('builder-note').hidden = !(why || note);
      document.querySelectorAll('.sw').forEach(s => s.setAttribute('aria-pressed', String(s.dataset.finish === prefs.finish)));
    }

    /** The kind Add gives a new button: an LED when it fits, else a plain button. */
    function newKind() {
      return L.check(draft, l => ({ ...l, buttons: [...l.buttons, L.withKind({}, L.KIND.LED)] })) ? L.KIND.NONE : L.KIND.LED;
    }
    /** Marks a control unusable without disabling it, so its tooltip shows and a click says why. */
    function block(el, why, label, what, at) {
      el.setAttribute('aria-disabled', String(!!why));
      el.title = why ? `${what}: ${why}` : label;
      // Its own click listener checks aria-disabled; this one says why, next to the control.
      el.onclick = why ? () => { blocked = { ...at, text: `${what}. ${L.explain(why)}` }; render(); } : null;
    }
    function hintRow(text) {
      const li = Object.assign(document.createElement('li'), { className: 'hint-row' });
      li.append(Object.assign(document.createElement('p'), { className: 'hint', textContent: text }));
      li.setAttribute('role', 'status');
      return li;
    }
    /** The pin meter, and why a button or slider can't be added. */
    function renderPins(addSlider, addButton) {
      const r = L.toConfig(draft);
      const pins = r.pins;
      $('pins').hidden = !pins;
      if (pins) {
        $('pins-n').textContent = `${pins.io} of ${pins.ioTotal} I/O`;
        $('pins-bar').style.width = `${(100 * pins.io) / pins.ioTotal}%`;
        $('pins').classList.toggle('full', !!addButton);
        $('pins').title = `I/O pins: buttons, LEDs, I2C buses for screens, and sliders once the input-only pins are used.\n` +
          `Input-only pins (sliders only): ${pins.inputOnly} of ${pins.inputOnlyTotal}.`;
      }
      $('pinmap-note').textContent = dirty() ? 'The draft\'s pins. Apply to store them.' : 'GPIO numbers, as Volco\'s Controller page asks for them.';
      $('pinmap-list').replaceChildren(...(pins ? L.pinMap(r.config) : []).flatMap(({ label, value }) => [
        Object.assign(document.createElement('dt'), { textContent: label }),
        Object.assign(document.createElement('dd'), { textContent: value }),
      ]));
      const show = (id, text) => { $(id).textContent = text; $(id).hidden = !text; };
      show('add-hint', (r.error ? `This layout doesn't fit. ${L.explain(r.error.message)}` : '')
        || (addButton ? `No room for another button. ${L.explain(addButton)}` : ''));
      show('sl-hint', blocked?.where === 'sliders' ? blocked.text : '');
    }

    function row(b, i) {
      const kind = L.kindOf(b);
      const li = document.createElement('li');
      li.draggable = true;
      const grip = Object.assign(document.createElement('span'), { className: 'grip' });
      grip.setAttribute('aria-hidden', 'true');
      const num = Object.assign(document.createElement('span'), { className: 'num', textContent: String(i + 1) });
      const kinds = document.createElement('div');
      kinds.className = 'kinds';
      kinds.setAttribute('role', 'radiogroup');
      kinds.setAttribute('aria-label', `Button ${i + 1}`);
      for (const k of KIND_UI) {
        const btn = document.createElement('button');
        btn.setAttribute('role', 'radio');
        btn.setAttribute('aria-checked', String(k.kind === kind));
        btn.setAttribute('aria-label', k.label);
        const why = k.kind === kind ? null : L.check(draft, l => ({ ...l, buttons: l.buttons.map((x, j) => (j === i ? L.withKind(x, k.kind) : x)) }));
        block(btn, why, k.label, `Button ${i + 1} can't take ${k.article}`, { where: 'row', index: i });
        btn.innerHTML = `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4">${k.svg}</svg>`;
        btn.addEventListener('click', () => { if (k.kind !== kind && !why) edit(l => { l.buttons[i] = L.withKind(l.buttons[i], k.kind); return l; }); });
        kinds.append(btn);
      }
      const rm = document.createElement('button');
      rm.className = 'rm';
      rm.title = 'Remove';
      rm.setAttribute('aria-label', `Remove button ${i + 1}`);
      rm.innerHTML = '<svg width="12" height="12" viewBox="0 0 12 12" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><path d="M2.5 2.5l7 7M9.5 2.5l-7 7"></path></svg>';
      rm.addEventListener('click', () => edit(l => { l.buttons.splice(i, 1); return l; }));
      li.addEventListener('dragstart', e => { e.dataTransfer.setData('text/plain', String(i)); e.dataTransfer.effectAllowed = 'move'; li.classList.add('dragging'); });
      li.addEventListener('dragend', () => li.classList.remove('dragging'));
      li.addEventListener('dragover', e => { e.preventDefault(); li.classList.add('over'); });
      li.addEventListener('dragleave', () => li.classList.remove('over'));
      li.addEventListener('drop', e => {
        e.preventDefault();
        li.classList.remove('over');
        const from = +e.dataTransfer.getData('text/plain');
        if (from !== i) edit(l => { l.buttons.splice(i, 0, ...l.buttons.splice(from, 1)); return l; });
      });
      li.append(grip, num, kinds, rm);
      return li;
    }

    $('preset').replaceChildren(...L.PRESETS.map(p => new Option(p.name, p.id)), Object.assign(new Option('Custom', 'custom'), { disabled: true }));
    $('preset').addEventListener('change', e => {
      draft = L.presetLayout(e.target.value, draft.base);
      note = ''; blocked = null;
      editedSinceApply = true;
      o.onPreview(draft, { drop: true });
      render();
      clearTimeout(timer);
      if (prefs.autosave && dirty()) timer = setTimeout(apply, AUTOSAVE_MS);
    });
    $('pinmap').open = !!prefs.pinMapOpen;
    $('pinmap').addEventListener('toggle', () => { prefs.pinMapOpen = $('pinmap').open; o.onPrefs({ pinMapOpen: prefs.pinMapOpen }); });
    $('sl-dec').addEventListener('click', () => edit(l => { l.sliders.pop(); return l; }));
    const usable = el => el.getAttribute('aria-disabled') !== 'true';
    $('sl-inc').addEventListener('click', () => { if (usable($('sl-inc'))) edit(l => { l.sliders.push({}); return l; }); });
    $('add').addEventListener('click', () => { const k = newKind(); if (usable($('add'))) edit(l => { l.buttons.push(L.withKind({}, k)); return l; }); });
    $('apply').addEventListener('click', apply);
    $('revert').addEventListener('click', () => { draft = clone(saved); note = ''; blocked = null; changed(); });
    $('autosave').addEventListener('change', e => {
      prefs.autosave = e.target.checked;
      o.onPrefs({ autosave: prefs.autosave });
      changed();
    });
    document.querySelectorAll('.sw').forEach(s => s.addEventListener('click', () => {
      prefs.finish = s.dataset.finish;
      o.onPrefs({ finish: prefs.finish });
      o.onFinish(prefs.finish);
      render();
    }));
    render();

    return {
      /** The stored config changed (Apply, Volco's write, Clear): it becomes the draft. */
      reload(byVolco = false) {
        const wasDirty = dirty();
        saved = o.stored();
        if (!byVolco && editedSinceApply) { note = ''; changed(); return; }   // edited while it rebooted: keep it, save it next
        editedSinceApply = false;
        draft = clone(saved);
        note = byVolco && wasDirty ? 'Volco saved a new layout; your draft was replaced.' : '';
        clearTimeout(timer);
        render();
      },
      draft: () => draft,
      isDirty: dirty,
    };
  }

  root.VolcoBuilder = { create };
})(globalThis);

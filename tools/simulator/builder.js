// The builder panel (spec §6.1, §6.4): preset, sliders, buttons and finish, edited as a draft that
// previews live on the board; Apply (or Auto-save) stores it. Browser only; uses layout.js.
(function (root) {
  'use strict';
  const L = root.VolcoLayout;

  const KIND_UI = [
    { kind: L.KIND.NONE, label: 'Plain', svg: '<rect x="3" y="3" width="10" height="10" rx="2.5"></rect>' },
    { kind: L.KIND.LED, label: 'LED', svg: '<circle cx="8" cy="8" r="2.6" fill="currentColor"></circle>' },
    { kind: L.KIND.LED_PAIR, label: 'LED pair', svg: '<circle cx="4.8" cy="8" r="2.3" fill="currentColor"></circle><circle cx="11.2" cy="8" r="2.3"></circle>' },
    { kind: L.KIND.SCREEN, label: 'Screen', svg: '<rect x="1.8" y="4" width="12.4" height="8" rx="1.5"></rect><path d="M4.5 9.5h7"></path>' },
  ];
  const AUTOSAVE_MS = 700;

  /**
   * @param {object} o
   * @param {() => object} o.stored                 the stored layout (layout.fromConfig)
   * @param {() => string|null} o.unavailable       why Apply can't work (no protobufjs), or null
   * @param {(layout:object) => void} o.onPreview   show this layout on the board
   * @param {(layout:object) => string|null} o.onApply  store it; returns an error message or null
   * @param {{autosave:boolean, finish:string}} o.prefs
   * @param {(prefs:object) => void} o.onPrefs
   * @param {(name:string) => void} o.onFinish
   */
  function create(o) {
    const $ = id => document.getElementById(id);
    const list = $('btns');
    let saved = o.stored(), draft = clone(saved), note = '', timer = 0;
    let editedSinceApply = false;   // the draft changed after the last save started
    const prefs = { ...o.prefs };

    function clone(l) { return JSON.parse(JSON.stringify(l)); }
    const shape = l => JSON.stringify([l.preset, l.sliders, l.buttons]);
    const dirty = () => shape(draft) !== shape(saved);

    function edit(change) {
      draft = change(clone(draft));
      draft.preset = 'custom';
      note = '';
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
      $('sl-inc').disabled = !!addSlider;
      $('sl-inc').title = addSlider || '';
      const addButton = L.check(draft, l => ({ ...l, buttons: [...l.buttons, L.withKind({}, L.KIND.LED)] }));
      $('add').disabled = !!addButton;
      $('add').title = addButton || '';
      list.replaceChildren(...draft.buttons.map((b, i) => row(b, i)));
      $('apply').hidden = $('revert').hidden = prefs.autosave;
      $('apply').disabled = !dirty() || !!why;
      $('revert').disabled = !dirty();
      $('autosave').checked = prefs.autosave;
      $('builder-note').textContent = why || note;
      $('builder-note').hidden = !(why || note);
      document.querySelectorAll('.sw').forEach(s => s.setAttribute('aria-pressed', String(s.dataset.finish === prefs.finish)));
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
        btn.title = why || k.label;
        btn.disabled = !!why;
        btn.innerHTML = `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4">${k.svg}</svg>`;
        btn.addEventListener('click', () => { if (k.kind !== kind) edit(l => { l.buttons[i] = L.withKind(l.buttons[i], k.kind); return l; }); });
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
      note = '';
      editedSinceApply = true;
      o.onPreview(draft, { drop: true });
      render();
      clearTimeout(timer);
      if (prefs.autosave && dirty()) timer = setTimeout(apply, AUTOSAVE_MS);
    });
    $('sl-dec').addEventListener('click', () => edit(l => { l.sliders.pop(); return l; }));
    $('sl-inc').addEventListener('click', () => edit(l => { l.sliders.push({}); return l; }));
    $('add').addEventListener('click', () => edit(l => { l.buttons.push(L.withKind({}, L.KIND.LED)); return l; }));
    $('apply').addEventListener('click', apply);
    $('revert').addEventListener('click', () => { draft = clone(saved); note = ''; changed(); });
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

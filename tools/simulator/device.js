// The 3D controller (spec §5.3, §5.4): the wedge case built from a layout, fader caps you drag,
// keys you press, LEDs, and OLEDs that show the 128×64 frames oled.js renders. Browser only;
// built on stage.js. Ported from the approved mock (docs/design/simulator.md).
(function (root) {
  'use strict';

  const FINISHES = {
    silver:   { body: ['#c9cacc', 1.0, 0.36], key: '#ecebe7', cap: '#1d1d1e', line: '#f2f1ec', mark: 'rgba(0,0,0,0.30)' },
    graphite: { body: ['#5a5c61', 0.95, 0.42], key: '#2c2d30', cap: '#151516', line: '#f2f1ec', mark: 'rgba(255,255,255,0.26)' },
    sand:     { body: ['#e6e0d5', 0.0, 0.55], key: '#f4f1ea', cap: '#efebe3', line: '#e2602a', mark: 'rgba(80,60,30,0.30)' },
  };
  const PX_ON = '#e9f1ff', PX_OFF = '#07090c', CELL = 4;
  const POSITION = { x: 30, z: 0, turn: -0.12 };   // on the desk, angled a little toward the chair

  /**
   * @param {object} stage   stage.js
   * @param {object} o
   * @param {(index:number, value:number) => void} o.onSlider   value 0..1 (0 = front)
   * @param {(index:number, pressed:boolean) => void} o.onButton
   */
  function create(stage, o) {
    const { THREE, RoundedBoxGeometry, scene, camera, renderer, controls, kit } = stage;
    const M = {
      body: new THREE.MeshPhysicalMaterial(),
      key: new THREE.MeshPhysicalMaterial({ roughness: 0.42, clearcoat: 0.15, clearcoatRoughness: 0.6 }),
      cap: new THREE.MeshPhysicalMaterial({ roughness: 0.5, clearcoat: 0.2, clearcoatRoughness: 0.5 }),
      line: new THREE.MeshStandardMaterial({ roughness: 0.6 }),
      groove: new THREE.MeshStandardMaterial({ color: '#0b0b0b', roughness: 0.9 }),
      well: new THREE.MeshStandardMaterial({ color: '#141414', roughness: 0.85 }),
      bezel: new THREE.MeshPhysicalMaterial({ color: '#0d0d0e', roughness: 0.35, clearcoat: 0.6 }),
      glass: new THREE.MeshPhysicalMaterial({ color: '#000', roughness: 0.04, clearcoat: 1, transparent: true, opacity: 0.18, depthWrite: false, envMapIntensity: 2.2 }),
      tick: new THREE.MeshStandardMaterial({ color: '#2a2a2a', roughness: 0.8, transparent: true, opacity: 0.45 }),
    };
    let finish = 'silver', board = null, wordmark = null;
    const interactive = [];
    const faderValues = [];

    function drawWordmark() {
      if (!wordmark) return;
      const c = wordmark.image, g = c.getContext('2d');
      g.clearRect(0, 0, c.width, c.height);
      g.fillStyle = FINISHES[finish].mark;
      g.font = '600 64px "Instrument Sans", sans-serif';
      g.letterSpacing = '22px';
      g.textAlign = 'right'; g.textBaseline = 'middle';
      g.fillText('VOLCO', c.width - 8, c.height / 2);
      wordmark.needsUpdate = true;
    }
    function setFinish(name) {
      finish = FINISHES[name] ? name : 'silver';
      const f = FINISHES[finish];
      M.body.color.set(f.body[0]); M.body.metalness = f.body[1]; M.body.roughness = f.body[2];
      M.body.clearcoat = finish === 'sand' ? 0.25 : 0;
      M.key.color.set(f.key); M.cap.color.set(f.cap); M.line.color.set(f.line);
      M.tick.color.set(finish === 'graphite' ? '#d8d8d8' : '#2a2a2a');
      drawWordmark();
    }

    function roundedShape(pts, r) {
      const s = new THREE.Shape(), n = pts.length;
      for (let i = 0; i < n; i++) {
        const p0 = pts[(i - 1 + n) % n], p1 = pts[i], p2 = pts[(i + 1) % n];
        const a = p1.clone().sub(p1.clone().sub(p0).normalize().multiplyScalar(r[i]));
        const b = p1.clone().add(p2.clone().sub(p1).normalize().multiplyScalar(r[i]));
        i ? s.lineTo(a.x, a.y) : s.moveTo(a.x, a.y);
        s.quadraticCurveTo(p1.x, p1.y, b.x, b.y);
      }
      s.closePath();
      return s;
    }

    function makeScreen() {
      const out = document.createElement('canvas'); out.width = 128 * CELL; out.height = 64 * CELL;
      const tex = new THREE.CanvasTexture(out);
      tex.colorSpace = THREE.SRGBColorSpace; tex.anisotropy = 8;
      const mat = new THREE.MeshBasicMaterial({ map: tex, toneMapped: false });
      const s = { out, ctx: out.getContext('2d'), tex, mat, frame: null };
      paint(s, new Uint8Array(1024), 1);
      return s;
    }
    /** Paints a 1-bit page-order frame (oled.js) as a pixel grid; level 0..1 is the panel's brightness. */
    function paint(s, frame, level) {
      const g = s.ctx;
      g.fillStyle = PX_OFF; g.fillRect(0, 0, s.out.width, s.out.height);
      g.fillStyle = PX_ON;
      for (let x = 0; x < 128; x++) for (let page = 0; page < 8; page++) {
        const b = frame[page * 128 + x];
        if (b) for (let bit = 0; bit < 8; bit++) if (b & (1 << bit)) g.fillRect(x * CELL, (page * 8 + bit) * CELL, CELL - 0.7, CELL - 0.7);
      }
      s.mat.color.setScalar(level);
      s.tex.needsUpdate = true;
    }

    /** Builds the board for { sliders, kinds }; drop: animate it falling into place (a new preset). */
    function build(hw, { drop = false } = {}) {
      if (board) { scene.remove(board.group); board.group.traverse(x => { if (x.isMesh) x.geometry.dispose(); }); }
      interactive.length = 0;
      const n = hw.sliders, kinds = hw.kinds, b = kinds.length;
      const cols = b <= 3 ? b : Math.min(4, Math.ceil(b / 2)), rows = b ? Math.ceil(b / cols) : 1;
      const PITCH = 3.3, COLW = 5.0;
      const W = 2.9 + Math.max(0, n - 1) * PITCH + (b ? 5.4 + (cols - 1) * COLW + 4.2 : 2.9);
      const D = Math.max(14.5, rows * 7.0 + 2.4);
      const hf = 1.9, hb = 6.0, bs = 0.22, bt = 0.32;

      const group = new THREE.Group();
      const shape = roundedShape([new THREE.Vector2(0, 0), new THREE.Vector2(D, 0), new THREE.Vector2(D, hf), new THREE.Vector2(0, hb)], [0.25, 0.25, 0.7, 1.0]);
      const geo = new THREE.ExtrudeGeometry(shape, { depth: W - 2 * bt, bevelEnabled: true, bevelThickness: bt, bevelSize: bs, bevelSegments: 6, curveSegments: 28 });
      geo.rotateY(-Math.PI / 2);
      geo.translate((W - 2 * bt) / 2, bs, -D / 2);
      const body = new THREE.Mesh(geo, M.body);
      body.castShadow = true; body.receiveShadow = true;
      group.add(body);

      const top = new THREE.Group();
      top.position.y = (hb + hf) / 2 + 2 * bs;
      top.rotation.x = Math.atan2(hb - hf, D);
      group.add(top);
      const add = (mesh, x, y, z, shadow = true) => kit.put(top, mesh, x, y, z, shadow);

      const x0 = -W / 2 + 2.9, TRAVEL = 3.3, SZ = 0.2;
      const faders = [];
      for (let i = 0; i < n; i++) {
        const x = x0 + i * PITCH;
        add(new THREE.Mesh(new RoundedBoxGeometry(0.42, 0.2, TRAVEL * 2 + 0.9, 2, 0.08), M.groove), x, -0.095, SZ, false);
        for (let k = 0; k <= 10; k++) {
          const len = k % 5 === 0 ? 0.55 : 0.32;
          add(new THREE.Mesh(new THREE.BoxGeometry(len, 0.01, 0.045), M.tick), x - 0.95 - len / 2, 0.006, SZ - TRAVEL + k * TRAVEL * 2 / 10, false);
        }
        const cap = new THREE.Group();
        const shell = new THREE.Mesh(new RoundedBoxGeometry(1.55, 1.05, 1.05, 4, 0.2), M.cap);
        shell.position.y = 0.72; shell.castShadow = true;
        const stem = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.3, 0.3), M.groove); stem.position.y = 0.12;
        const line = new THREE.Mesh(new THREE.BoxGeometry(1.25, 0.012, 0.09), M.line); line.position.y = 1.247;
        cap.add(shell, stem, line);
        const v = faderValues[i] ?? 0.5;
        cap.position.set(x, 0, SZ + TRAVEL - v * TRAVEL * 2);
        top.add(cap);
        const f = { cap, zMin: SZ - TRAVEL, zMax: SZ + TRAVEL, index: i };
        shell.userData.fader = f; line.userData.fader = f;
        interactive.push(shell);
        faders.push(f);
      }

      const bx0 = x0 + Math.max(0, n - 1) * PITCH + 5.4;
      const keys = [];
      kinds.forEach((kind, i) => {
        const c = i % cols, r = Math.floor(i / cols);
        const x = bx0 + c * COLW, z = (r - (rows - 1) / 2) * 7.0 + 0.2;
        const zInd = z - 2.0, zKey = z + 1.4;
        add(new THREE.Mesh(new RoundedBoxGeometry(2.7, 0.2, 2.7, 3, 0.25), M.well), x, -0.095, zKey, false);
        const mesh = add(new THREE.Mesh(new RoundedBoxGeometry(2.45, 0.95, 2.45, 5, 0.3), M.key), x, 0.42, zKey);
        const k = { mesh, rest: 0.42, pressed: false, index: i, kind, leds: [], screen: null };
        mesh.userData.key = k;
        interactive.push(mesh);
        keys.push(k);
        if (kind === 3) {
          add(new THREE.Mesh(new RoundedBoxGeometry(4.15, 0.12, 2.35, 3, 0.1), M.bezel), x, 0.0, zInd);
          k.screen = makeScreen();
          const disp = new THREE.Mesh(new THREE.PlaneGeometry(3.6, 1.8), k.screen.mat);
          disp.rotation.x = -Math.PI / 2; add(disp, x, 0.065, zInd, false);
          const glass = new THREE.Mesh(new THREE.PlaneGeometry(4.05, 2.25), M.glass);
          glass.rotation.x = -Math.PI / 2; glass.renderOrder = 2; add(glass, x, 0.07, zInd, false);
        } else if (kind === 1 || kind === 2) {
          k.leds = (kind === 2 ? [-0.55, 0.55] : [0]).map(dx => {
            const mat = new THREE.MeshPhysicalMaterial({ color: '#4a2e14', roughness: 0.2, clearcoat: 1, emissive: '#ff9a2e', emissiveIntensity: 0 });
            const dome = new THREE.Mesh(new THREE.SphereGeometry(0.2, 24, 12, 0, Math.PI * 2, 0, Math.PI / 2), mat);
            dome.scale.y = 0.7;
            add(new THREE.Mesh(new THREE.CylinderGeometry(0.28, 0.28, 0.04, 24), M.well), x + dx, 0.0, zInd, false);
            add(dome, x + dx, 0.02, zInd);
            const glow = new THREE.PointLight('#ffa24a', 0, 2.6, 2);
            add(glow, x + dx, 0.35, zInd, false);
            return { mat, glow };
          });
        }
      });

      // Status LED, engraved wordmark, soft contact shadow.
      const status = new THREE.Mesh(new THREE.CircleGeometry(0.13, 20), new THREE.MeshBasicMaterial({ color: '#ffb35c', toneMapped: false }));
      status.rotation.x = -Math.PI / 2; add(status, -W / 2 + 1.3, 0.012, -D / 2 + 1.1, false);
      wordmark = kit.canvasTex(1024, 128, () => {});
      wordmark.wrapS = wordmark.wrapT = THREE.ClampToEdgeWrapping;
      drawWordmark();
      const wm = new THREE.Mesh(new THREE.PlaneGeometry(6.4, 0.8), new THREE.MeshStandardMaterial({ map: wordmark, transparent: true, roughness: 0.7, depthWrite: false }));
      wm.rotation.x = -Math.PI / 2; add(wm, W / 2 - 4.1, 0.01, -D / 2 + 1.05, false);
      const ao = new THREE.Mesh(new THREE.PlaneGeometry(W + 8, D + 8), new THREE.MeshBasicMaterial({
        map: kit.canvasTex(256, 256, (g, w, h) => { g.filter = 'blur(18px)'; g.fillStyle = '#000'; g.fillRect(34, 34, w - 68, h - 68); }, { srgb: false }),
        transparent: true, opacity: 0.5, depthWrite: false, color: '#2a2016' }));
      ao.rotation.x = -Math.PI / 2; ao.position.y = 0.012;
      group.add(ao);

      group.position.set(POSITION.x, 0, POSITION.z);
      group.rotation.y = POSITION.turn;
      scene.add(group);
      board = { group, top, faders, keys, status, dropT0: drop ? performance.now() : -1e9 };
    }

    /** value: an LED's value (spec §7): one LED lit when not 0; a pair lights A on 1, B on 2. */
    function setLed(i, value) {
      const k = board && board.keys[i];
      if (!k || !k.leds.length) return;
      k.leds.forEach((led, j) => {
        const on = k.kind === 2 ? value === j + 1 : value !== 0;
        led.mat.emissiveIntensity = on ? 3.2 : 0;
        led.mat.color.set(on ? '#ffcf8a' : '#4a2e14');
        led.glow.intensity = on ? 0.6 : 0;
      });
    }
    /** A screen button's frame (1024 bytes, page order) at brightness level 0..1 (0 = off). */
    function setScreen(i, frame, level) {
      const s = board && board.keys[i] && board.keys[i].screen;
      if (!s) return;
      if (s.frame && s.level === level && s.frame.every((b, j) => b === frame[j])) return;   // unchanged: no repaint
      s.frame = frame; s.level = level;
      paint(s, frame, level);
    }

    // ── Pointer: the left button works the board, everything else is the camera's ──
    const ray = new THREE.Raycaster(), ndc = new THREE.Vector2();
    let drag = null, held = null;
    const toNdc = e => {
      const r = renderer.domElement.getBoundingClientRect();
      ndc.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
      ray.setFromCamera(ndc, camera);
    };
    const pick = e => { toNdc(e); return ray.intersectObjects(interactive, false)[0]; };
    renderer.domElement.addEventListener('pointerdown', e => {
      if (e.pointerType === 'mouse' && e.button !== 0) return;
      const hit = pick(e);
      if (!hit) return;
      controls.enabled = false;
      renderer.domElement.setPointerCapture(e.pointerId);
      const u = hit.object.userData;
      if (u.fader) drag = u.fader;
      if (u.key) { held = u.key; held.pressed = true; o.onButton(held.index, true); }
    }, { capture: true });   // before OrbitControls sees it
    renderer.domElement.addEventListener('pointermove', e => {
      if (!drag) { renderer.domElement.style.cursor = pick(e) ? 'pointer' : ''; return; }
      const t = board.top;
      const normal = new THREE.Vector3(0, 1, 0).applyQuaternion(t.getWorldQuaternion(new THREE.Quaternion()));
      const plane = new THREE.Plane().setFromNormalAndCoplanarPoint(normal, t.getWorldPosition(new THREE.Vector3()));
      toNdc(e);
      const p = ray.ray.intersectPlane(plane, new THREE.Vector3());
      if (!p) return;
      t.worldToLocal(p);
      const z = THREE.MathUtils.clamp(p.z, drag.zMin, drag.zMax);
      drag.cap.position.z = z;
      const value = (drag.zMax - z) / (drag.zMax - drag.zMin);
      faderValues[drag.index] = value;
      o.onSlider(drag.index, value);
    });
    const release = () => {
      if (held) { held.pressed = false; o.onButton(held.index, false); }
      drag = null; held = null; controls.enabled = true;
    };
    renderer.domElement.addEventListener('pointerup', release);
    renderer.domElement.addEventListener('pointercancel', release);

    return {
      FINISHES: Object.keys(FINISHES),
      build, setFinish, setLed, setScreen,
      /** A slider's position 0..1 (0.5 until moved). */
      faderValue: i => faderValues[i] ?? 0.5,
      /** The status LED: lit while the controller runs, dark while it reboots. */
      setStatus(on) { if (board) board.status.material.color.set(on ? '#ffb35c' : '#2a1c10'); },
      update(now, dt) {
        if (!board) return;
        const k = Math.min(1, (now - board.dropT0) / 520), e = 1 - Math.pow(1 - k, 3);
        board.group.position.y = (1 - e) * 3;
        board.group.rotation.x = (1 - e) * -0.04;
        for (const key of board.keys) {
          const target = key.pressed ? key.rest - 0.24 : key.rest;
          key.mesh.position.y += (target - key.mesh.position.y) * Math.min(1, dt * 28);
        }
      },
    };
  }

  root.VolcoDevice = { create, FINISHES: Object.keys(FINISHES) };
})(globalThis);

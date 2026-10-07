// The home office around the controller (spec §5.1, §5.2): room, light, desk and props, the
// monitor with its volume overlay, speakers and headphones with their sound waves. Browser only;
// built on stage.js. Ported from the approved mock (docs/design/simulator.md).
(function (root) {
  'use strict';

  /** Builds the room into stage.scene over several tasks, resolving to the room. The board goes in
   *  front of the monitor, centred on x = 0 with its back edge at z = -13 (y = 0 is the desk top);
   *  the keyboard and mouse sit in front of it. */
  async function build(stage) {
    const { THREE, RoundedBoxGeometry, scene, kit } = stage;
    const { canvasTex, rnd, std, phys, put, box, cyl } = kit;
    const period = stage.reduceMotion ? 5100 : 1700;   // one wave cycle, ms

    // Grain and specks are written into a pixel layer, then drawn over the texture in one call:
    // tens of thousands of fillRect calls, each with its own colour string, kept the page busy
    // for hundreds of ms at load (and reading the canvas back would cost as much).
    function specks(g, W, H, n, colour, alpha, size) {
      const img = new ImageData(W, H), d = img.data;
      for (let i = 0; i < n; i++) {
        const [r, gr, b] = colour(), a = rnd(alpha[0], alpha[1]);
        const x0 = rnd(0, W) | 0, y0 = rnd(0, H) | 0, w = Math.round(rnd(size[0], size[1])), h = Math.round(rnd(size[2], size[3]));
        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
          const o = (((y0 + y) % H) * W + ((x0 + x) % W)) * 4;
          const below = d[o + 3] / 255, out = a + below * (1 - a);   // "over", unpremultiplied
          if (out <= 0) continue;
          d[o] = (r * a + d[o] * below * (1 - a)) / out; d[o + 1] = (gr * a + d[o + 1] * below * (1 - a)) / out; d[o + 2] = (b * a + d[o + 2] * below * (1 - a)) / out;
          d[o + 3] = out * 255;
        }
      }
      const layer = document.createElement('canvas'); layer.width = W; layer.height = H;
      layer.getContext('2d').putImageData(img, 0, 0);
      g.drawImage(layer, 0, 0);
    }
    function wood(base, grain, repeat) {
      const rgb = grain.split(',').map(Number);
      return canvasTex(2048, 2048, (g, W, H) => {
        g.fillStyle = base; g.fillRect(0, 0, W, H);
        for (let i = 0; i < 1100; i++) {
          const y0 = rnd(0, H), amp = rnd(3, 22), k1 = Math.ceil(rnd(1, 4)), k2 = Math.ceil(rnd(4, 11)), ph = rnd(0, 6.28);
          g.strokeStyle = `rgba(${grain},${rnd(0.05, 0.2)})`;
          g.lineWidth = rnd(0.5, 2.6);
          for (const off of [-H, 0, H]) {
            if (y0 + off + amp * 1.22 + 2 < 0 || y0 + off - amp * 1.22 - 2 > H) continue;   // off the canvas: only the wrap copies that show
            g.beginPath();
            for (let x = 0; x <= W; x += 16) {
              const y = y0 + off + Math.sin(x / W * 6.283 * k1 + ph) * amp + Math.sin(x / W * 6.283 * k2 + ph * 2) * amp * 0.22;
              x ? g.lineTo(x, y) : g.moveTo(x, y);
            }
            g.stroke();
          }
        }
        specks(g, W, H, 60000, () => rgb, [0, 0.06], [2, 7, 1, 1]);
      }, { repeat });
    }
    function speckle(base, size, n, lo, hi, a, repeat) {
      return canvasTex(size, size, (g, W, H) => {
        g.fillStyle = base; g.fillRect(0, 0, W, H);
        specks(g, W, H, n, () => { const v = rnd(lo, hi) | 0; return [v, v - 6, v - 14]; }, a, [1, 3, 1, 3]);
      }, { repeat });
    }


    // The large textures first, one task each, so building the room never stalls the page for long.
    const pause = () => new Promise(r => setTimeout(r, 0));
    const tex = {};
    tex.plaster = speckle('#cdb497', 1024, 90000, 150, 225, [0.03, 0.09], [5, 3]); await pause();
    tex.floor = wood('#7a5134', '60,34,18', [3, 6]); await pause();
    tex.rug = speckle('#a76f55', 512, 50000, 120, 200, [0.06, 0.2], [4, 3]); await pause();
    tex.desk = wood('#c79a6b', '92,52,24', [1, 1]); await pause();
    tex.mat = speckle('#4a4744', 512, 30000, 70, 110, [0.05, 0.2], [5, 2]); await pause();


    // ── Sound waves: rings that leave the selected output device ────────────────
    const EMITTERS = [];   // { name, obj, r, travel, rings[], level }
    const ringGeo = new THREE.RingGeometry(0.9, 1, 72);
    function addEmitter(name, obj, r, travel) {   // name: 'speakers' or 'headphones'
      const e = { name, obj, r, travel, rings: [], level: 0 };
      for (let i = 0; i < 3; i++) {
        const m = new THREE.Mesh(ringGeo, new THREE.MeshBasicMaterial({ color: '#f0a35e', transparent: true, opacity: 0, depthWrite: false, side: THREE.DoubleSide }));
        m.renderOrder = 3; m.visible = false;
        scene.add(m);
        e.rings.push(m);
      }
      EMITTERS.push(e);
    }
    const _p = new THREE.Vector3(), _d = new THREE.Vector3(), _q = new THREE.Quaternion();
    function updateWaves(now, dt, active) {
      for (const e of EMITTERS) {
        e.level += ((e.name === active ? 1 : 0) - e.level) * Math.min(1, dt * 4);
        const on = e.level > 0.01;
        if (on) { e.obj.getWorldPosition(_p); e.obj.getWorldDirection(_d); e.obj.getWorldQuaternion(_q); }
        e.rings.forEach((m, i) => {
          m.visible = on;
          if (!on) return;
          const ph = (now / period + i / 3) % 1;
          m.scale.setScalar(e.r * (1 + ph * 1.5));
          m.position.copy(_p).addScaledVector(_d, ph * e.travel);
          m.quaternion.copy(_q);
          m.material.opacity = e.level * 0.7 * (1 - ph) * Math.min(1, ph * 6);
        });
      }
    }


    // ── Room: plaster walls, window, floor and rug ──────────────────────────────
    const plaster = std({ map: tex.plaster, roughness: 0.96 });
    const back = put(scene, new THREE.Mesh(new THREE.PlaneGeometry(420, 320), plaster), 0, 85, -42, false);
    const left = new THREE.Mesh(new THREE.PlaneGeometry(260, 320), plaster);
    left.rotation.y = Math.PI / 2; put(scene, left, -125, 85, 70, false);
    const floor = new THREE.Mesh(new THREE.PlaneGeometry(420, 300), std({ map: tex.floor, roughness: 0.55 }));
    floor.rotation.x = -Math.PI / 2; put(scene, floor, 0, -75, 100, false);
    const rug = box(170, 1, 120, std({ map: tex.rug, roughness: 1 }), 0.4);
    put(scene, rug, 10, -74.5, 60, false);
    // Skirting board along the back wall.
    put(scene, box(420, 9, 1.2, std({ color: '#efe6d8', roughness: 0.7 })), 0, -70.5, -41.4, false);

    // Window on the left wall: frame, mullions and a warm late-afternoon sky.
    (function windowPane() {
      const g = new THREE.Group();
      const sky = new THREE.Mesh(new THREE.PlaneGeometry(90, 110), new THREE.MeshBasicMaterial({ toneMapped: false,
        map: canvasTex(256, 512, (c, w, h) => {
          const gr = c.createLinearGradient(0, 0, 0, h);
          gr.addColorStop(0, '#f6d7ae'); gr.addColorStop(0.55, '#ffe9c9'); gr.addColorStop(1, '#f3c88f');
          c.fillStyle = gr; c.fillRect(0, 0, w, h);
          c.fillStyle = 'rgba(120,110,80,.35)';
          for (let i = 0; i < 9; i++) { c.beginPath(); c.ellipse(rnd(0, w), h * rnd(0.72, 0.95), rnd(30, 70), rnd(40, 90), 0, 0, 7); c.fill(); }
        }) }));
      g.add(sky);
      const frameM = std({ color: '#f1ebe1', roughness: 0.6 });
      for (const [w, h, x, y] of [[96, 4, 0, 57], [96, 4, 0, -57], [4, 118, -47, 0], [4, 118, 47, 0], [2.4, 110, 0, 0], [90, 2.4, 0, 12]])
        put(g, box(w, h, 4, frameM), x, y, 1.6);
      put(g, box(104, 3, 12, frameM), 0, -60, 5);   // sill
      g.rotation.y = Math.PI / 2;
      g.position.set(-124.8, 75, -2);
      scene.add(g);
    })();

    // Framed print on the back wall.
    (function print() {
      const art = canvasTex(512, 640, (c, w, h) => {
        c.fillStyle = '#efe4d2'; c.fillRect(0, 0, w, h);
        c.fillStyle = '#c7663f'; c.beginPath(); c.arc(w * 0.42, h * 0.4, 120, 0, 7); c.fill();
        c.fillStyle = '#2f4a4f'; c.fillRect(w * 0.18, h * 0.58, w * 0.64, h * 0.22);
        c.fillStyle = '#d8b06a'; c.beginPath(); c.arc(w * 0.68, h * 0.62, 70, Math.PI, 0); c.fill();
      });
      const g = new THREE.Group();
      put(g, box(44, 54, 2.4, std({ color: '#2a211b', roughness: 0.55 })), 0, 0, 0);
      put(g, box(40, 50, 0.2, std({ color: '#f6f1e8', roughness: 0.9 })), 0, 0, 1.25, false);
      put(g, new THREE.Mesh(new THREE.PlaneGeometry(30, 38), std({ map: art, roughness: 0.9 })), 0, 0, 1.4, false);
      g.position.set(58, 62, -40.6);
      scene.add(g);
    })();

    // ── Desk ────────────────────────────────────────────────────────────────────
    const DESK_TOP = 0;
    const deskTop = box(190, 3.2, 82, phys({ map: tex.desk, roughness: 0.5, clearcoat: 0.3, clearcoatRoughness: 0.45 }), 0.6);
    put(scene, deskTop, 0, -1.6, -0.5);
    const legM = std({ color: '#2b2622', roughness: 0.5, metalness: 0.6 });
    for (const x of [-88, 88]) for (const z of [-36, 35]) put(scene, box(4, 72, 4, legM, 0.4), x, -38.6, z, false);

    // Desk mat under keyboard and mouse.
    put(scene, box(74, 0.3, 30, std({ map: tex.mat, roughness: 1 }), 0.15), 8, 0.15, 21);

    // ── Monitor ─────────────────────────────────────────────────────────────────
    const MON = { x: 0, z: -25, w: 70, h: 39.4, y: 25.5 };
    const monCanvas = document.createElement('canvas'); monCanvas.width = 1280; monCanvas.height = 720;
    const monTex = new THREE.CanvasTexture(monCanvas); monTex.colorSpace = THREE.SRGBColorSpace; monTex.anisotropy = 8;
    const osd = { text: '', value: 0, shownAt: -1e9 };
    function drawMonitor(now) {
      const c = monCanvas.getContext('2d'), W = 1280, H = 720;
      const gr = c.createLinearGradient(0, 0, 0, H);
      gr.addColorStop(0, '#f1c79b'); gr.addColorStop(0.5, '#d98e68'); gr.addColorStop(1, '#6e3f4b');
      c.fillStyle = gr; c.fillRect(0, 0, W, H);
      const dunes = [['#c27459', 0.62, 50], ['#9b5653', 0.72, 70], ['#6d3b46', 0.84, 60], ['#4a2a36', 0.94, 40]];
      for (const [col, y, a] of dunes) {
        c.fillStyle = col; c.beginPath(); c.moveTo(0, H);
        for (let x = 0; x <= W; x += 20) c.lineTo(x, H * y - Math.sin(x / W * 5 + y * 9) * a - Math.sin(x / W * 13 + y * 3) * a * 0.25);
        c.lineTo(W, H); c.fill();
      }
      c.fillStyle = 'rgba(255,248,238,.9)'; c.beginPath(); c.arc(W * 0.7, H * 0.3, 46, 0, 7); c.fill();
      // Taskbar.
      c.fillStyle = 'rgba(250,244,236,.55)'; c.fillRect(0, H - 40, W, 40);
      for (let i = 0; i < 6; i++) { c.fillStyle = 'rgba(60,40,40,.35)'; c.beginPath(); c.roundRect(W / 2 - 130 + i * 45, H - 32, 24, 24, 6); c.fill(); }
      // Volco's volume OSD, as the app shows it while a slider moves.
      const age = now - osd.shownAt, alpha = age < 1400 ? 1 : Math.max(0, 1 - (age - 1400) / 300);
      if (alpha > 0) {
        c.globalAlpha = alpha;
        const w = 360, h = 86, x = (W - w) / 2, y = H - 40 - h - 40;
        c.fillStyle = 'rgba(250,247,242,.95)'; c.beginPath(); c.roundRect(x, y, w, h, 20); c.fill();
        c.fillStyle = '#2a2623'; c.font = '600 22px "Instrument Sans", sans-serif'; c.textBaseline = 'alphabetic';
        c.fillText(osd.text, x + 26, y + 36);
        c.textAlign = 'right'; c.fillStyle = '#77706a'; c.fillText(osd.value >= 0 ? `${Math.round(osd.value * 100)}` : '', x + w - 26, y + 36); c.textAlign = 'left';
        c.fillStyle = 'rgba(42,38,35,.12)'; c.beginPath(); c.roundRect(x + 26, y + 54, w - 52, 8, 4); c.fill();
        if (osd.value >= 0) { c.fillStyle = '#2a2623'; c.beginPath(); c.roundRect(x + 26, y + 54, (w - 52) * osd.value, 8, 4); c.fill(); }
        c.globalAlpha = 1;
      }
      monTex.needsUpdate = true;
      return alpha > 0;
    }
    function showOsd(text, value) { osd.text = text; osd.value = value; osd.shownAt = performance.now(); monitorDirty = true; }   // value < 0: no bar
    let monitorDirty = true;
    (function monitor() {
      const g = new THREE.Group();
      const alu = phys({ color: '#bfc0c2', metalness: 1, roughness: 0.32 });
      put(g, box(24, 0.9, 17, alu, 0.4), 0, 0.45, 2);
      put(g, box(5.5, 22, 1.6, alu, 0.6), 0, 11.5, -2.5);
      const shell = box(MON.w + 1.2, MON.h + 1.2, 2.2, phys({ color: '#1a1a1b', roughness: 0.45, clearcoat: 0.3 }), 0.5);
      put(g, shell, 0, MON.y, -0.6);
      put(g, box(MON.w - 6, MON.h - 6, 3, phys({ color: '#c9cacc', metalness: 1, roughness: 0.4 }), 1.2), 0, MON.y, -2.2);
      const screen = new THREE.Mesh(new THREE.PlaneGeometry(MON.w, MON.h), new THREE.MeshBasicMaterial({ map: monTex, toneMapped: false, color: '#d9d4cf' }));
      put(g, screen, 0, MON.y, 0.52, false);
      const glass = new THREE.Mesh(new THREE.PlaneGeometry(MON.w + 1, MON.h + 1), phys({ color: '#000', roughness: 0.08, clearcoat: 1, transparent: true, opacity: 0.12, depthWrite: false }));
      glass.renderOrder = 2; put(g, glass, 0, MON.y, 0.56, false);
      const glow = new THREE.PointLight('#ffd7c0', 260, 0, 2);
      put(g, glow, 0, MON.y - 4, 10, false);
      g.position.set(MON.x, DESK_TOP, MON.z);
      g.rotation.y = 0.06;
      scene.add(g);
    })();

    // ── Keyboard and mouse ──────────────────────────────────────────────────────
    (function keyboard() {
      const g = new THREE.Group();
      put(g, box(36, 1.5, 12.4, phys({ color: '#d9d9d6', metalness: 0.9, roughness: 0.35 }), 0.5), 0, 0.75, 0);
      const capGeo = new RoundedBoxGeometry(1.62, 0.7, 1.62, 2, 0.22);
      const capM = std({ color: '#efede8', roughness: 0.55 });
      const rows = [[15, 0], [15, 0], [14, 0.5], [13, 0.9], [12, 1.4]];
      const pitch = 1.9, keys = [];
      rows.forEach(([n, off], r) => { for (let i = 0; i < n; i++) keys.push([-14.2 + off * pitch / 2 + i * pitch * (15 / n) * 0.985, -4.6 + r * 1.95]); });
      const inst = new THREE.InstancedMesh(capGeo, capM, keys.length);
      const m4 = new THREE.Matrix4();
      keys.forEach(([x, z], i) => inst.setMatrixAt(i, m4.makeTranslation(x, 1.7, z)));
      inst.castShadow = true; inst.receiveShadow = true;
      g.add(inst);
      put(g, box(11, 0.7, 1.62, capM, 0.22), 0, 1.7, -4.6 + 5 * 1.95);
      g.position.set(0, 0.3, 19);
      g.rotation.y = 0.02;
      scene.add(g);

      const mouse = new THREE.Mesh(new THREE.SphereGeometry(1, 40, 24), phys({ color: '#e9e7e2', roughness: 0.35, clearcoat: 0.5 }));
      mouse.scale.set(3.1, 1.7, 5.4);
      put(scene, mouse, 30, 0.1, 21);
      mouse.rotation.y = -0.15;
    })();

    // ── Desk things: mug, lamp, books, plant, notebook ──────────────────────────
    (function mug() {
      const g = new THREE.Group();
      const p = [];
      const R = 4.3, Hh = 9.6, wall = 0.32;
      p.push(new THREE.Vector2(0, 0.0), new THREE.Vector2(R - 0.6, 0), new THREE.Vector2(R - 0.15, 0.2), new THREE.Vector2(R, 0.8));
      for (let i = 1; i <= 12; i++) p.push(new THREE.Vector2(R + Math.sin(i / 12 * Math.PI) * 0.12, 0.8 + (Hh - 0.8) * i / 12));
      p.push(new THREE.Vector2(R - wall / 2, Hh + 0.12), new THREE.Vector2(R - wall, Hh), new THREE.Vector2(R - wall, 0.9), new THREE.Vector2(0, 0.9));
      const ceramic = phys({ color: '#2f4a4f', roughness: 0.3, clearcoat: 0.8, clearcoatRoughness: 0.15 });
      const body = new THREE.Mesh(new THREE.LatheGeometry(p, 64), ceramic);
      const handle = new THREE.Mesh(new THREE.TorusGeometry(2.3, 0.5, 20, 40, Math.PI * 1.15), ceramic);
      handle.rotation.z = -Math.PI / 2 - 0.25; handle.position.set(R + 0.4, Hh * 0.52, 0);
      const coffee = new THREE.Mesh(new THREE.CircleGeometry(R - wall - 0.02, 48), phys({ color: '#2a170b', roughness: 0.08, clearcoat: 1 }));
      coffee.rotation.x = -Math.PI / 2; coffee.position.y = Hh - 1.3;
      g.add(body, handle, coffee);
      g.traverse(o => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } });
      g.position.set(60, 0, 9); g.rotation.y = 2.4;
      scene.add(g);
    })();

    let lampLight;
    (function lamp() {
      const g = new THREE.Group();
      const black = phys({ color: '#1f1d1b', roughness: 0.4, metalness: 0.5, clearcoat: 0.3 });
      const brass = phys({ color: '#c8a46a', roughness: 0.3, metalness: 1 });
      put(g, cyl(6.5, 7, 1.6, black), 0, 0.8, 0);
      const a1 = put(g, cyl(0.45, 0.45, 36, black, 16), 0, 0, 0);
      const p0 = new THREE.Vector3(0, 1.6, 0), p1 = new THREE.Vector3(6, 34, 6), p2 = new THREE.Vector3(26, 38, 18);
      const along = (m, a, b) => { m.position.copy(a).add(b).multiplyScalar(0.5); m.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), b.clone().sub(a).normalize()); m.scale.y = a.distanceTo(b) / 36; };
      along(a1, p0, p1);
      along(put(g, cyl(0.4, 0.4, 36, black, 16), 0, 0, 0), p1, p2);
      put(g, new THREE.Mesh(new THREE.SphereGeometry(1, 20, 12), brass), p1.x, p1.y, p1.z);
      const head = new THREE.Group();
      head.position.copy(p2);
      const shade = new THREE.Mesh(new THREE.CylinderGeometry(2.2, 7.5, 10, 48, 1, true), phys({ color: '#1f1d1b', roughness: 0.45, metalness: 0.4, side: THREE.DoubleSide }));
      shade.castShadow = true; head.add(shade);
      const bulb = new THREE.Mesh(new THREE.CircleGeometry(6.9, 40), new THREE.MeshBasicMaterial({ color: '#ffe2b8', toneMapped: false }));
      bulb.rotation.x = Math.PI / 2; bulb.position.y = -3; head.add(bulb);
      head.rotation.z = -0.55; head.rotation.x = 0.35;
      g.add(head);
      g.position.set(-76, 0, -28);
      scene.add(g);
      lampLight = new THREE.SpotLight('#ffb86e', 5200, 0, 0.75, 0.85, 2);
      const lp = p2.clone().add(g.position); lp.y -= 3;
      lampLight.position.copy(lp);
      lampLight.target.position.set(-24, 0, 6);
      lampLight.castShadow = true;
      lampLight.shadow.mapSize.set(2048, 2048);
      lampLight.shadow.radius = 8;
      lampLight.shadow.bias = -0.0005;
      scene.add(lampLight, lampLight.target);
    })();

    (function books() {
      const cols = ['#7b4a35', '#34505c', '#c9b48a', '#5d6b45'];
      let y = 0;
      [[24, 3.2, 17, 0.06], [22, 2.6, 16, -0.1], [25, 3.6, 18, 0.12], [20, 2.2, 14, -0.04]].forEach(([w, h, d, r], i) => {
        const b = box(w, h, d, std({ color: cols[i], roughness: 0.8 }), 0.2);
        b.rotation.y = r;
        put(scene, b, -80, y + h / 2, -6, true);
        const pages = box(w - 0.6, h - 0.5, d - 0.2, std({ color: '#efe6d3', roughness: 0.9 }));
        pages.rotation.y = r;
        put(scene, pages, -80 + 0.4, y + h / 2, -6 + 0.1, false);
        y += h;
      });
    })();

    (function plant() {
      const g = new THREE.Group();
      const terracotta = std({ color: '#b86a4b', roughness: 0.85 });
      const pot = new THREE.Mesh(new THREE.CylinderGeometry(7.2, 5.6, 14, 48), terracotta);
      put(g, pot, 0, 7, 0);
      put(g, cyl(7.5, 7.5, 1.6, terracotta), 0, 13.4, 0);
      put(g, cyl(6.8, 6.8, 0.4, std({ color: '#3b2a1f', roughness: 1 })), 0, 13.2, 0, false);
      const leafShape = new THREE.Shape();
      leafShape.moveTo(-1.2, 0); leafShape.quadraticCurveTo(-2.4, 14, -0.1, 30); leafShape.lineTo(0.1, 30); leafShape.quadraticCurveTo(2.4, 14, 1.2, 0); leafShape.closePath();
      const leafGeo = new THREE.ExtrudeGeometry(leafShape, { depth: 0.25, bevelEnabled: true, bevelThickness: 0.1, bevelSize: 0.1, bevelSegments: 1 });
      for (let i = 0; i < 11; i++) {
        const m = std({ color: new THREE.Color().setHSL(0.28 + rnd(-0.02, 0.03), 0.42, rnd(0.22, 0.32)), roughness: 0.55, side: THREE.DoubleSide });
        const leaf = new THREE.Mesh(leafGeo, m);
        const a = i / 11 * Math.PI * 2 + rnd(-0.2, 0.2);
        leaf.position.set(Math.cos(a) * 2.2, 13, Math.sin(a) * 2.2);
        leaf.rotation.set(0, -a + Math.PI / 2, 0);
        leaf.rotateX(-rnd(0.05, 0.3));
        leaf.scale.setScalar(rnd(0.75, 1.3));
        leaf.castShadow = true; leaf.receiveShadow = true;
        g.add(leaf);
      }
      g.position.set(86, 0, -30);
      scene.add(g);
    })();

    (function notebook() {
      const g = new THREE.Group();
      put(g, box(15, 1.1, 21, std({ color: '#2d2a27', roughness: 0.75 }), 0.3), 0, 0.55, 0);
      put(g, box(14.6, 0.9, 20.6, std({ color: '#f1ead9', roughness: 0.9 })), 0.25, 0.55, 0, false);
      const pen = cyl(0.42, 0.42, 14, phys({ color: '#b08a4f', metalness: 1, roughness: 0.3 }), 20);
      pen.rotation.z = Math.PI / 2; pen.rotation.y = 0.5;
      put(g, pen, 0, 1.55, 2);
      g.position.set(76, 0, 25); g.rotation.y = -0.35;
      scene.add(g);
    })();


    // ── Speakers either side of the monitor, headphones on a stand ──────────────
    function speaker(x, z, toe) {
      const g = new THREE.Group();
      put(g, box(17, 28, 21, std({ color: '#ebe4d8', roughness: 0.6 }), 0.9), 0, 14, 0);
      const rubber = std({ color: '#1c1b1a', roughness: 0.9 });
      const trim = phys({ color: '#b9b4ab', metalness: 1, roughness: 0.35 });
      const cone = new THREE.Mesh(new THREE.CylinderGeometry(5.3, 1.6, 2, 48, 1, true), std({ color: '#2c2a28', roughness: 0.85, side: THREE.DoubleSide }));
      cone.rotation.x = Math.PI / 2; put(g, cone, 0, 9.5, 9.6, false);
      put(g, new THREE.Mesh(new THREE.TorusGeometry(5.5, 0.55, 16, 56), rubber), 0, 9.5, 10.4, false);
      put(g, new THREE.Mesh(new THREE.TorusGeometry(6.4, 0.35, 12, 56), trim), 0, 9.5, 10.5, false);
      const cap = new THREE.Mesh(new THREE.SphereGeometry(1.8, 24, 12, 0, Math.PI * 2, 0, Math.PI / 2), std({ color: '#3a3734', roughness: 0.5 }));
      cap.rotation.x = Math.PI / 2; put(g, cap, 0, 9.5, 8.7, false);
      put(g, new THREE.Mesh(new THREE.TorusGeometry(2.1, 0.35, 12, 40), trim), 0, 21.5, 10.5, false);
      const dome = new THREE.Mesh(new THREE.SphereGeometry(1.4, 24, 12, 0, Math.PI * 2, 0, Math.PI / 2), phys({ color: '#d8d4cc', metalness: 0.8, roughness: 0.25 }));
      dome.rotation.x = Math.PI / 2; dome.scale.y = 0.6; put(g, dome, 0, 21.5, 10.3, false);
      const mark = new THREE.Object3D(); mark.position.set(0, 9.5, 11); g.add(mark);
      g.position.set(x, 0, z); g.rotation.y = toe; g.scale.setScalar(0.7);
      scene.add(g);
      addEmitter('speakers', mark, 3.9, 11);
    }
    speaker(-46, -27, 0.28);
    speaker(46, -27, -0.28);

    (function headphones() {
      const g = new THREE.Group();
      const matte = std({ color: '#2b2a29', roughness: 0.7 });
      const soft = std({ color: '#3b3836', roughness: 0.95 });
      const metal = phys({ color: '#c3c0ba', metalness: 1, roughness: 0.3 });
      const walnut = phys({ color: '#6b4329', roughness: 0.5, clearcoat: 0.4 });
      put(g, cyl(5.5, 6, 1.4, walnut), 0, 0.7, 0);
      put(g, cyl(0.6, 0.6, 30, metal, 16), 0, 15.6, 0);
      put(g, box(7, 1.4, 3.4, walnut, 0.6), 0, 30.4, 0);
      put(g, new THREE.Mesh(new THREE.TorusGeometry(9, 1.0, 16, 64, Math.PI), matte), 0, 22.2, 0);
      const pad = new THREE.Mesh(new THREE.TorusGeometry(8.1, 0.7, 12, 48, Math.PI * 0.6), soft);
      pad.rotation.z = Math.PI * 0.2; put(g, pad, 0, 22.2, 0, false);
      for (const s of [-1, 1]) {
        put(g, cyl(0.32, 0.32, 5, metal, 12), s * 9, 21, 0);
        const cup = cyl(4.6, 4.6, 3.2, matte, 48); cup.rotation.z = Math.PI / 2; put(g, cup, s * 9.8, 17.5, 0);
        const plate = cyl(3.4, 3.4, 0.3, metal, 48); plate.rotation.z = Math.PI / 2; put(g, plate, s * 11.5, 17.5, 0, false);
        const cushion = new THREE.Mesh(new THREE.TorusGeometry(3.4, 1.3, 16, 40), soft);
        cushion.rotation.y = Math.PI / 2; put(g, cushion, s * 7.6, 17.5, 0);
        const mark = new THREE.Object3D(); mark.position.set(s * 11.7, 17.5, 0); mark.rotation.y = s * Math.PI / 2; g.add(mark);
        addEmitter('headphones', mark, 4.4, 8);
      }
      g.position.set(68, 0, -24); g.rotation.y = -0.55;
      scene.add(g);
    })();


    let output = 'speakers', muted = false;
    return {
      /** Which device plays (spec §7.3): 'speakers' or 'headphones'; muted stops the waves. */
      setOutput(device, isMuted) { output = device; muted = !!isMuted; },
      /** The monitor's volume overlay: text, and a bar for value 0..1 (no bar when value < 0). */
      showOverlay: showOsd,
      update(now, dt) {
        updateWaves(now, dt, muted ? null : output);
        if (monitorDirty) monitorDirty = drawMonitor(now);
      },
    };
  }

  root.VolcoRoom = { build };
})(globalThis);

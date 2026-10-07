// The 3D stage (spec §5, D4): renderer, scene, light, camera and controls, post-processing, the
// idle drift, re-centring beside the panels, and the mesh helpers room.js and device.js share.
// Browser only; needs window.VolcoThree (the inline module in index.html). Units are cm.
(function (root) {
  'use strict';

  /**
   * @param {HTMLElement} container
   * @param {object} o
   * @param {THREE.Vector3Like} o.target   what the camera orbits and focuses on (the board)
   * @param {(now:number, dt:number) => void} o.onFrame  called every frame before rendering, from start() on
   */
  function create(container, o) {
    const { THREE, OrbitControls, RoomEnvironment, RoundedBoxGeometry, EffectComposer, RenderPass, OutputPass } = root.VolcoThree;
    const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

    const renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' });
    renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFShadowMap;
    container.appendChild(renderer.domElement);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color('#2e241d');
    scene.environment = new THREE.PMREMGenerator(renderer).fromScene(new RoomEnvironment(), 0.04).texture;
    scene.environmentIntensity = 0.32;

    // Camera: orbit and zoom around the board; right button rotates, left is for the board.
    const target = new THREE.Vector3(o.target.x, o.target.y, o.target.z);
    const camera = new THREE.PerspectiveCamera(34, 1, 1, 1500);
    camera.position.set(target.x - 8, target.y + 56, target.z + 62);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.target.copy(target);
    Object.assign(controls, { enableDamping: true, dampingFactor: 0.06, minDistance: 22, maxDistance: 190,
      minPolarAngle: 0.15, maxPolarAngle: 1.4, minAzimuthAngle: -1.0, maxAzimuthAngle: 1.0, enablePan: false });
    controls.mouseButtons = { LEFT: null, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.ROTATE };
    const AZ0 = Math.atan2(camera.position.x - target.x, camera.position.z - target.z);

    // Light: low golden window light from the left and a warm hemisphere fill (the lamp and the
    // monitor glow belong to room.js).
    const sun = new THREE.DirectionalLight('#ffcf9a', 1.9);
    sun.position.set(-160, 95, 55);
    sun.target.position.set(10, 0, -5);
    sun.castShadow = true;
    sun.shadow.mapSize.set(4096, 4096);
    Object.assign(sun.shadow.camera, { left: -110, right: 110, top: 90, bottom: -90, near: 20, far: 420 });
    Object.assign(sun.shadow, { radius: 6, bias: -0.0004, normalBias: 0.04 });
    scene.add(sun, sun.target, new THREE.HemisphereLight('#ffe6cc', '#5a4536', 0.42));

    // Post: a multisampled half-float render, then tone mapping and sRGB output. No depth of field:
    // it blurred the board whenever the camera zoomed out.
    const composer = new EffectComposer(renderer, new THREE.WebGLRenderTarget(1, 1, { samples: 4, type: THREE.HalfFloatType }));
    composer.addPass(new RenderPass(scene, camera));
    composer.addPass(new OutputPass());

    // Re-centre the board in the space the floating panels leave free (spec §4).
    let offset = { x: 0, y: 0 };
    function resize() {
      const w = container.clientWidth, h = container.clientHeight;
      renderer.setSize(w, h, false);
      composer.setSize(w, h);
      camera.aspect = w / h;
      camera.fov = w / h < 1 ? 50 : 34;
      camera.setViewOffset(w, h, offset.x, offset.y, w, h);
      camera.updateProjectionMatrix();
    }
    addEventListener('resize', resize);
    resize();

    // Idle drift (D4): only after 8 s with no sign of the viewer; eases in and out; any focus,
    // visibility, pointer, wheel or key event restarts the clock.
    let idleSince = performance.now(), swaySpeed = 0, swayDir = 1;
    const wake = () => { idleSince = performance.now(); swaySpeed = 0; };
    for (const ev of ['pointermove', 'pointerdown', 'wheel', 'keydown', 'focus', 'blur']) addEventListener(ev, wake, { passive: true });
    document.addEventListener('visibilitychange', wake);
    controls.addEventListener('start', () => { idleSince = Infinity; swaySpeed = 0; });
    controls.addEventListener('end', wake);

    const clock = new THREE.Clock();
    function frame() {
      const now = performance.now(), dt = Math.min(clock.getDelta(), 0.05);
      const idle = !reduceMotion && !document.hidden && now - idleSince > 8000;
      if (idle) { const az = controls.getAzimuthalAngle() - AZ0; if (az > 0.3) swayDir = -1; else if (az < -0.3) swayDir = 1; }
      swaySpeed += ((idle ? 0.16 * swayDir : 0) - swaySpeed) * Math.min(1, dt * 0.5);
      controls.autoRotate = Math.abs(swaySpeed) > 0.001;
      controls.autoRotateSpeed = swaySpeed;
      o.onFrame(now, dt);
      controls.update();
      composer.render();
    }

    // ── Mesh helpers shared by room.js and device.js ──
    function canvasTex(w, h, draw, { repeat = [1, 1], srgb = true } = {}) {
      const c = document.createElement('canvas'); c.width = w; c.height = h;
      draw(c.getContext('2d'), w, h);
      const t = new THREE.CanvasTexture(c);
      t.wrapS = t.wrapT = THREE.RepeatWrapping;
      t.repeat.set(...repeat);
      t.anisotropy = renderer.capabilities.getMaxAnisotropy();
      if (srgb) t.colorSpace = THREE.SRGBColorSpace;
      return t;
    }
    const kit = {
      canvasTex,
      rnd: (a, b) => a + Math.random() * (b - a),
      std: m => new THREE.MeshStandardMaterial(m),
      phys: m => new THREE.MeshPhysicalMaterial(m),
      put(parent, mesh, x, y, z, shadow = true) {
        mesh.position.set(x, y, z);
        mesh.castShadow = shadow; mesh.receiveShadow = true;
        parent.add(mesh);
        return mesh;
      },
      box: (w, h, d, m, r = 0) => new THREE.Mesh(r ? new RoundedBoxGeometry(w, h, d, 3, r) : new THREE.BoxGeometry(w, h, d), m),
      cyl: (rt, rb, h, m, seg = 40) => new THREE.Mesh(new THREE.CylinderGeometry(rt, rb, h, seg), m),
    };

    return {
      THREE, RoundedBoxGeometry, scene, camera, controls, renderer, kit, reduceMotion,
      /** Shift the view so the board sits in the free space: x right-panel, y bottom-sheet/drawer (px). */
      setOffset(x, y) { offset = { x, y }; resize(); },
      /** Restart the idle clock (e.g. when the board is touched). */
      wake,
      /** Compiles the scene's shaders without blocking the page (where the GPU allows), then starts
       *  rendering; the first frame would otherwise compile them all at once. */
      async start() {
        try { await renderer.compileAsync(scene, camera); } catch { /* the first frame compiles them */ }
        clock.getDelta();
        renderer.setAnimationLoop(frame);
      },
    };
  }

  root.VolcoStage = { create };
})(globalThis);

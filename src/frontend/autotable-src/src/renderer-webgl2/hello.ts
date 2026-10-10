// Phase K Wave 15 — WebGL2 renderer hello-world mount (Hicks).
//
// Phase L W1 spike entry.  Loaded by `src/index.ts` ONLY when the
// URL contains `?renderer=webgl2-hello` (or the W16
// `?renderer=webgl2-tile-mesh` smoke, or the W17
// `?renderer=webgl2-scene` smoke); never runs on the lobby cold
// path.  Its sole job through W15 / W16 / W17 is to:
//
//   1. Prove the chunk split works — `vite.config.ts:manualChunks`
//      routes everything under `src/renderer-webgl2/` into a
//      `renderer-webgl2.<hash>.js` chunk.  `append-dist-size.js`
//      records the chunk size in `dist-size.json`.
//   2. Render one of three smoke modes (textured-quad hello-world,
//      instanced tile-mesh, or full scene-runtime + picking) against
//      the canonical tile atlas (W17) or its synth fallback.
//   3. NOT depend on three.js — the renderer-webgl2 module is
//      free-standing.
//
// Future Phase L waves (W4+) expand this entry into the full
// renderer: animation graph, lighting model, post-fx.  The
// W15+W16+W17 baselines are the "hello world + tile graph + scene
// runtime + picking" costs we measure every subsequent wave
// against.

import { helloWorld } from './index';
import {
  createTileMesh,
  drawTileMesh,
  populateWallDemo,
  setTileInstance,
  TILE_INSTANCE_FS,
  TILE_INSTANCE_VS,
  uploadTileInstances,
} from './tile-mesh';
import { acquireTileAtlas } from './tile-atlas';
import { TILE_FACE_COUNT, tileFace } from './tile-faces';
import {
  applyCameraMode,
  attachMouseControls,
  attachTouchControls,
  createOrbitCamera,
  viewProjMatrix,
  type CameraMode,
} from './camera';
import { compileProgram, createWebgl2Context } from './index';
import { identity4, scaleMatrix4, translateMatrix4 } from './math';
import { createTileScene } from './scene';
import { buildPickRay, pickTile } from './picking';
import {
  populateWallWithDora,
  CANONICAL_WALL_TILE_COUNT,
} from './wall-geometry';
import {
  startPickAnimation,
  type PickAnimationHandle,
} from './tile-pick-animation';
import { attachTileDrag } from './tile-drag';
// Phase K Wave 21 — claim animation + meld-display smoke (Hicks).
// The renderer-webgl2 manualChunks rule routes anything under
// `src/renderer-webgl2/` into the same chunk regardless of import
// path, but tree-shaking would drop the W21 modules if no path
// referenced them.  Importing here keeps them in the graph + lets
// `?renderer=webgl2-meld` exercise the new code paths end-to-end.
import {
  appendMeld,
  createMeldDisplay,
  nextMeldOriginXZ,
  type MeldDisplayState,
  type MeldGroup,
} from './meld-display';
import {
  claimDurationMs,
  meldSlotMatrix,
  startClaimAnimation,
  type ClaimAnimationHandle,
} from './tile-claim-animation';

const CONTAINER_ID = 'webgl2-hello-container';
const TEXTURE_URL = '/img/tiles-labels.auto.png';

async function loadTexture(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = (): void => resolve(img);
    img.onerror = (): void => reject(new Error(`[webgl2-hello] failed to load ${url}`));
    img.src = url;
  });
}

function ensureContainer(): { canvas: HTMLCanvasElement; status: HTMLElement } {
  let container = document.getElementById(CONTAINER_ID);
  if (container === null) {
    container = document.createElement('div');
    container.id = CONTAINER_ID;
    container.setAttribute('data-testid', 'webgl2-hello-container');
    container.style.cssText =
      'position:fixed;inset:0;display:flex;flex-direction:column;'
      + 'align-items:center;justify-content:center;background:#0d161e;'
      + 'color:#eaeaea;font-family:system-ui,sans-serif;z-index:99999;';
    document.body.appendChild(container);
  }
  let canvas = container.querySelector<HTMLCanvasElement>('canvas');
  if (canvas === null) {
    canvas = document.createElement('canvas');
    canvas.width = 800;
    canvas.height = 512;
    canvas.style.cssText = 'width:800px;height:512px;border:1px solid #2a3a4a;background:#000;';
    canvas.setAttribute('data-testid', 'webgl2-hello-canvas');
    container.appendChild(canvas);
  }
  let status = container.querySelector<HTMLElement>('.webgl2-hello-status');
  if (status === null) {
    status = document.createElement('p');
    status.className = 'webgl2-hello-status';
    status.setAttribute('data-testid', 'webgl2-hello-status');
    status.style.cssText = 'margin:12px 0 0;font-size:14px;';
    container.appendChild(status);
  }
  return { canvas, status };
}

function mode(): 'hello' | 'tile-mesh' | 'scene' | 'wall' | 'interactive' | 'meld' {
  const s = window.location.search;
  if (/[?&]renderer=webgl2-meld/.test(s)) return 'meld';
  if (/[?&]renderer=webgl2-interactive/.test(s)) return 'interactive';
  if (/[?&]renderer=webgl2-wall/.test(s)) return 'wall';
  if (/[?&]renderer=webgl2-scene/.test(s)) return 'scene';
  if (/[?&]renderer=webgl2-tile-mesh/.test(s)) return 'tile-mesh';
  return 'hello';
}

/**
 * Public entry point invoked by `src/index.ts` behind the
 * `?renderer=webgl2-hello` / `?renderer=webgl2-tile-mesh` /
 * `?renderer=webgl2-scene` / `?renderer=webgl2-wall` /
 * `?renderer=webgl2-interactive` / `?renderer=webgl2-meld` URL
 * guards.
 */
export async function mount(): Promise<void> {
  switch (mode()) {
    case 'meld':        return mountMeld();
    case 'interactive': return mountInteractive();
    case 'wall':        return mountWall();
    case 'scene':       return mountScene();
    case 'tile-mesh':   return mountTileMesh();
    default:            return mountHelloWorld();
  }
}

async function mountHelloWorld(): Promise<void> {
  const { canvas, status } = ensureContainer();
  status.textContent = 'Loading tile texture…';
  let texture: HTMLImageElement;
  try {
    texture = await loadTexture(TEXTURE_URL);
  } catch (err) {
    status.textContent = `Texture load failed: ${(err as Error).message}`;
    return;
  }
  try {
    const handle = helloWorld(canvas, texture);
    status.textContent = `WebGL2 hello-world rendered (${texture.naturalWidth}×${texture.naturalHeight} texture).`;
    window.addEventListener('resize', () => handle.redraw(), { passive: true });
  } catch (err) {
    status.textContent = `WebGL2 init failed: ${(err as Error).message}`;
  }
}

async function mountTileMesh(): Promise<void> {
  const { canvas, status } = ensureContainer();
  status.textContent = 'Initialising tile mesh…';
  const gl = createWebgl2Context(canvas);
  if (gl === null) {
    status.textContent = 'WebGL2 unavailable in this browser.';
    return;
  }

  let program: WebGLProgram;
  try {
    program = compileProgram(gl, TILE_INSTANCE_VS, TILE_INSTANCE_FS);
  } catch (err) {
    status.textContent = `Shader compile failed: ${(err as Error).message}`;
    return;
  }

  const mesh = createTileMesh(gl, program);
  populateWallDemo(mesh);

  // Add 14 in-hand tiles in front of the player so the smoke render
  // exercises a mix of orientations.
  for (let i = 0; i < 14 && mesh.instanceCount < mesh.capacity; i++) {
    const m = identity4();
    m[12] = (i - 6.5) * 1.05;
    m[13] = 0.5;
    m[14] = 2.0;
    setTileInstance(mesh, mesh.instanceCount, m, i % 34);
  }

  uploadTileInstances(gl, mesh);

  status.textContent = 'Loading tile atlas…';
  const atlas = await acquireTileAtlas(gl);
  const atlasLabel = atlas.fallback
    ? `synthesized ${atlas.width}×${atlas.height} fallback atlas`
    : `loaded ${atlas.width}×${atlas.height} atlas`;

  const cam = createOrbitCamera();
  const uViewProj = gl.getUniformLocation(program, 'u_viewProj');
  const uAtlasGrid = gl.getUniformLocation(program, 'u_atlasGrid');
  const uAtlas = gl.getUniformLocation(program, 'u_atlas');
  const uLightDir = gl.getUniformLocation(program, 'u_lightDir');

  const draw = (): void => {
    const w = canvas.clientWidth | 0;
    const h = canvas.clientHeight | 0;
    if (canvas.width !== w) canvas.width = Math.max(1, w);
    if (canvas.height !== h) canvas.height = Math.max(1, h);
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.clearColor(0.05, 0.08, 0.05, 1.0);
    gl.enable(gl.DEPTH_TEST);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

    gl.useProgram(program);
    gl.uniformMatrix4fv(uViewProj, false, viewProjMatrix(cam, canvas));
    gl.uniform2f(uAtlasGrid, atlas.gridCols, atlas.gridRows);
    gl.uniform3f(uLightDir, 0.4, 1.0, 0.3);

    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, atlas.texture);
    gl.uniform1i(uAtlas, 0);

    drawTileMesh(gl, mesh);
  };

  draw();
  status.textContent =
    `WebGL2 tile-mesh rendered (${mesh.instanceCount} instances across `
    + `${TILE_FACE_COUNT} tile faces, ${atlasLabel}). `
    + 'Drag = orbit; right-drag = pan; wheel = zoom.';

  attachMouseControls(canvas, cam, draw);
  attachTouchControls(canvas, cam, draw);
  window.addEventListener('resize', draw, { passive: true });
}

// Phase K Wave 17 — full scene-runtime smoke.  Exercises the
// `createTileScene()` orchestrator + the `pickTile()` ray-cast in
// one go.  Click any tile to highlight it (the front column hue
// inverts for the picked instance for one frame so the hit is
// visible without a separate UI overlay).
async function mountScene(): Promise<void> {
  const { canvas, status } = ensureContainer();
  status.textContent = 'Booting tile-scene runtime…';

  let scene: Awaited<ReturnType<typeof createTileScene>>;
  try {
    scene = await createTileScene(canvas);
  } catch (err) {
    status.textContent = `WebGL2 scene init failed: ${(err as Error).message}`;
    return;
  }

  // Wall demo + in-hand row, but plumbed through the scene helper so
  // every change schedules a coalesced rAF redraw (no manual draw()).
  let i = 0;
  for (let row = 0; row < 4; row++) {
    for (let col = 0; col < 36; col++) {
      if (i >= scene.mesh.capacity) break;
      const xOffset = (col - 17.5) * 1.02;
      const zOffset = (row - 1.5) * 0.7 - 6.0;
      const m = identity4();
      scaleMatrix4(m, 1.0, 1.0, 1.0);
      translateMatrix4(m, xOffset, 0, zOffset);
      scene.setTileAt(i, m, i % 34);
      i++;
    }
  }
  for (let j = 0; j < 14 && i < scene.mesh.capacity; j++, i++) {
    const m = identity4();
    m[12] = (j - 6.5) * 1.05;
    m[13] = 0.5;
    m[14] = 2.0;
    scene.setTileAt(i, m, j % 34);
  }

  scene.drawNow();

  // Mouse-pick: build a world-space ray from the click, intersect
  // every instance, and bump the picked tile a quarter-tile up to
  // confirm the pick.  Right-click resets the bump.
  let pickedIndex: number | null = null;
  let pickedMatrix: Float32Array | null = null;
  canvas.addEventListener('click', (ev: MouseEvent) => {
    if (ev.button !== 0) return;
    if (ev.shiftKey) return; // shift-click = pan (camera handler)
    const vp = viewProjMatrix(scene.camera, canvas);
    const ray = buildPickRay(canvas, ev.clientX, ev.clientY, vp);
    const hit = pickTile(scene.mesh, ray);
    if (hit === null) {
      status.textContent = `No tile hit at (${ev.clientX}, ${ev.clientY}).`;
      return;
    }
    // Restore previous pick.
    if (pickedIndex !== null && pickedMatrix !== null) {
      scene.setTileAt(pickedIndex, pickedMatrix, scene.mesh.tileIdData[pickedIndex]);
    }
    // Stash the current matrix so we can undo the bump on the next pick.
    const base = hit.instanceIndex * 16;
    pickedMatrix = new Float32Array(scene.mesh.modelData.subarray(base, base + 16));
    pickedIndex = hit.instanceIndex;
    const bumped = new Float32Array(pickedMatrix);
    bumped[13] += 0.75; // lift along +y
    scene.setTileAt(pickedIndex, bumped, scene.mesh.tileIdData[pickedIndex]);
    const pickedTileId = scene.mesh.tileIdData[pickedIndex];
    const face = tileFace(pickedTileId);
    const faceLabel = face ? `${face.label} (${face.suit}-${face.value})` : `id ${pickedTileId}`;
    status.textContent =
      `Picked tile #${pickedIndex} [${faceLabel}] at world (${hit.point[0].toFixed(2)}, `
      + `${hit.point[1].toFixed(2)}, ${hit.point[2].toFixed(2)}).`;
  });

  const atlasLabel = scene.atlas.fallback
    ? `synthesized ${scene.atlas.width}×${scene.atlas.height} fallback atlas`
    : `loaded ${scene.atlas.width}×${scene.atlas.height} canonical atlas`;
  status.textContent =
    `WebGL2 scene runtime rendered (${scene.mesh.instanceCount} instances across `
    + `${TILE_FACE_COUNT} tile faces, ${atlasLabel}). `
    + 'Drag = orbit; right-drag = pan; wheel = zoom; click = pick tile.';
}

// Phase K Wave 19 — canonical Changsha wall smoke.  Lays the full
// 4 × 18 × 2 wall using `populateWallWithDora()` and exposes the
// three W19 camera modes (orbital / isometric-flat / perspective
// three-quarter) via a small button row above the canvas.  This is
// the visual-parity baseline the W19 e2e spec captures against.
async function mountWall(): Promise<void> {
  const { canvas, status } = ensureContainer();
  status.textContent = 'Booting canonical wall scene…';

  // We don't pre-bump the mesh capacity here — the W16 TileMesh ships
  // with MAX_INSTANCES = 200 which is comfortably above the 144 wall
  // tiles (+ 1 dora) we need for the W19 smoke.
  let scene: Awaited<ReturnType<typeof createTileScene>>;
  try {
    scene = await createTileScene(canvas);
  } catch (err) {
    status.textContent = `WebGL2 scene init failed: ${(err as Error).message}`;
    return;
  }

  populateWallWithDora(scene.mesh, /*seed=*/ 0x77313939);
  scene.drawNow();

  // Camera-mode picker — a 3-button strip above the status line.  We
  // host it in the existing container so the test selector
  // (`[data-testid="webgl2-wall-camera-<mode>"]`) is stable.
  installCameraModePicker(scene.camera, scene.canvas, scene.requestRedraw);

  // Click-on-tile shows the face label, matching the W17 scene smoke.
  canvas.addEventListener('click', (ev: MouseEvent) => {
    if (ev.button !== 0 || ev.shiftKey) return;
    const vp = viewProjMatrix(scene.camera, canvas);
    const ray = buildPickRay(canvas, ev.clientX, ev.clientY, vp);
    const hit = pickTile(scene.mesh, ray);
    if (hit === null) {
      status.textContent = `No wall tile at (${ev.clientX}, ${ev.clientY}).`;
      return;
    }
    const pickedTileId = scene.mesh.tileIdData[hit.instanceIndex];
    const face = tileFace(pickedTileId);
    const faceLabel = face ? `${face.label} (${face.suit}-${face.value})` : `id ${pickedTileId}`;
    status.textContent =
      `Wall tile #${hit.instanceIndex} [${faceLabel}] — `
      + `mode=${scene.camera.mode}.`;
  });

  const atlasLabel = scene.atlas.fallback
    ? `synthesized ${scene.atlas.width}×${scene.atlas.height} fallback atlas`
    : `loaded ${scene.atlas.width}×${scene.atlas.height} canonical atlas`;
  status.textContent =
    `Canonical wall rendered — ${CANONICAL_WALL_TILE_COUNT} tiles `
    + `(4 × 18 × 2 + dora), ${atlasLabel}.  `
    + 'Camera modes above; drag = orbit; wheel = zoom; click = pick.';
}

function installCameraModePicker(
  camera: Parameters<typeof applyCameraMode>[0],
  canvas: HTMLCanvasElement,
  requestRedraw: () => void,
): void {
  const container = canvas.parentElement;
  if (container === null) return;
  const existing = container.querySelector<HTMLElement>('.webgl2-wall-camera-picker');
  if (existing !== null) existing.remove();
  const strip = document.createElement('div');
  strip.className = 'webgl2-wall-camera-picker';
  strip.setAttribute('data-testid', 'webgl2-wall-camera-picker');
  strip.style.cssText =
    'display:flex;gap:8px;margin:8px 0;justify-content:center;';
  const modes: Array<{ id: CameraMode; label: string }> = [
    { id: 'orbital', label: 'Orbital' },
    { id: 'isometric-flat', label: 'Iso flat' },
    { id: 'perspective-three-quarter', label: '¾ persp' },
  ];
  for (const m of modes) {
    const b = document.createElement('button');
    b.type = 'button';
    b.textContent = m.label;
    b.setAttribute('data-testid', `webgl2-wall-camera-${m.id}`);
    b.style.cssText =
      'padding:6px 12px;background:#1a2733;color:#eaeaea;'
      + 'border:1px solid #2a3a4a;border-radius:4px;cursor:pointer;';
    b.addEventListener('click', () => {
      applyCameraMode(camera, m.id);
      requestRedraw();
    });
    strip.appendChild(b);
  }
  // Insert above the canvas so the picker sits between the
  // container's flex children naturally.
  container.insertBefore(strip, canvas);
}

// Phase K Wave 20 — interactive smoke.  Builds on the W19 canonical
// wall scene + adds the W20 tile-pick-animation (lift / drop tween)
// and tile-drag drag-and-drop with hover outline highlight.  Click +
// drag any tile to lift it; drop on empty space sets it back, drop
// on another tile leaves both lifted (placeholder swap-or-merge UX
// that the production renderer will refine).
async function mountInteractive(): Promise<void> {
  const { canvas, status } = ensureContainer();
  status.textContent = 'Booting interactive (drag + lift) scene…';

  let scene: Awaited<ReturnType<typeof createTileScene>>;
  try {
    scene = await createTileScene(canvas);
  } catch (err) {
    status.textContent = `WebGL2 scene init failed: ${(err as Error).message}`;
    return;
  }

  populateWallWithDora(scene.mesh, /*seed=*/ 0x77313939);
  scene.drawNow();

  // Track per-instance tween + the "base" matrix (the matrix the
  // tile occupied before the lift started, so a `drop` returns it).
  const baseMatrices = new Map<number, Float32Array>();
  const tweens = new Map<number, PickAnimationHandle>();
  let hoveredIndex: number | null = null;
  let dragSourceIndex: number | null = null;

  function captureBase(idx: number): Float32Array {
    const cached = baseMatrices.get(idx);
    if (cached !== undefined) return cached;
    const base = new Float32Array(scene.mesh.modelData.subarray(idx * 16, idx * 16 + 16));
    baseMatrices.set(idx, base);
    return base;
  }

  function tickTweens(): void {
    if (tweens.size === 0) return;
    const now = performance.now();
    let stillRunning = false;
    for (const [idx, tween] of tweens) {
      const running = tween.step(now);
      const tileId = scene.mesh.tileIdData[idx];
      scene.setTileAt(idx, tween.out, tileId);
      if (running) stillRunning = true;
      else if (tween.kind === 'drop') tweens.delete(idx);
    }
    scene.requestRedraw();
    if (stillRunning || tweens.size > 0) {
      window.requestAnimationFrame(tickTweens);
    }
  }

  function lift(idx: number): void {
    const base = captureBase(idx);
    tweens.set(idx, startPickAnimation(base, 'lift', performance.now()));
    window.requestAnimationFrame(tickTweens);
  }

  function drop(idx: number): void {
    const base = baseMatrices.get(idx);
    if (base === undefined) return;
    // Build a drop tween from the CURRENT (lifted) position back to
    // base by re-using startPickAnimation with kind=`drop` against the
    // base matrix.
    tweens.set(idx, startPickAnimation(base, 'drop', performance.now()));
    window.requestAnimationFrame(tickTweens);
  }

  function applyHoverHighlight(next: number | null): void {
    // Hover highlight is a tiny y-bump (no rotation) so the user sees
    // which tile is under the cursor without a separate outline pass.
    // We don't tween the hover bump — instantaneous bump in / out so
    // the W20 footprint stays under the 45 KB ceiling.
    if (hoveredIndex !== null && hoveredIndex !== dragSourceIndex
        && hoveredIndex !== next) {
      // Restore previous hover tile.
      const base = baseMatrices.get(hoveredIndex);
      if (base !== undefined && !tweens.has(hoveredIndex)) {
        const tileId = scene.mesh.tileIdData[hoveredIndex];
        scene.setTileAt(hoveredIndex, base, tileId);
        baseMatrices.delete(hoveredIndex);
      }
    }
    hoveredIndex = next;
    if (next !== null && next !== dragSourceIndex && !tweens.has(next)) {
      const base = captureBase(next);
      const bumped = new Float32Array(base);
      bumped[13] += 0.08; // 1/9 of a lift — purely a hover cue
      scene.setTileAt(next, bumped, scene.mesh.tileIdData[next]);
      scene.requestRedraw();
    }
  }

  attachTileDrag(canvas, scene.mesh, scene.camera, {
    onHover: (idx) => { applyHoverHighlight(idx); },
    onDragStart: (idx) => {
      dragSourceIndex = idx;
      lift(idx);
      status.textContent = `Dragging tile #${idx} — drop on another tile or empty space.`;
    },
    onDragMove: (_src, hit, _floor) => {
      // Show a hover bump on the candidate drop target (different
      // tile from the source).
      const candidate =
        hit !== null && hit.instanceIndex !== dragSourceIndex
          ? hit.instanceIndex
          : null;
      applyHoverHighlight(candidate);
    },
    onDragEnd: (source, target, floor) => {
      dragSourceIndex = null;
      // Drop the source back to its base; target gets a transient
      // lift so the user sees the "swap-or-place" affordance.
      drop(source);
      if (target !== null) {
        lift(target);
        status.textContent =
          `Dropped tile #${source} on tile #${target} `
          + `(swap-or-merge affordance — Phase L W6 wires the actual swap).`;
      } else if (floor !== null) {
        status.textContent =
          `Dropped tile #${source} at world `
          + `(${floor[0].toFixed(2)}, ${floor[2].toFixed(2)}).`;
      } else {
        status.textContent = `Dropped tile #${source} off-table.`;
      }
    },
    onDragCancel: (source) => {
      dragSourceIndex = null;
      drop(source);
      status.textContent = `Drag cancelled — tile #${source} returned to wall.`;
    },
  });

  // Camera-mode picker (re-use the W19 picker so the W20 smoke has
  // every camera angle the production renderer will need).
  installCameraModePicker(scene.camera, scene.canvas, scene.requestRedraw);

  const atlasLabel = scene.atlas.fallback
    ? `synthesized ${scene.atlas.width}×${scene.atlas.height} fallback atlas`
    : `loaded ${scene.atlas.width}×${scene.atlas.height} canonical atlas`;
  status.textContent =
    `Interactive wall rendered — ${CANONICAL_WALL_TILE_COUNT} tiles, `
    + `${atlasLabel}.  Hover = bump; drag = lift; drop = settle.`;
}

// Phase K Wave 21 — meld / claim-animation smoke (Hicks).
//
// Builds on the canonical wall scene, then drives the new W21
// `tile-claim-animation` + `meld-display` modules: declares one
// pung, one chi, and one kong against seat 0's meld row, animating
// each claim with a fan-in tween into the row's next slot.  This
// surfaces every code path in the two new modules end-to-end so the
// Vasquez lane gets a visual capture target for W21 visual-
// regression baselines.
async function mountMeld(): Promise<void> {
  const { canvas, status } = ensureContainer();
  status.textContent = 'Booting meld (claim animation + meld display) scene…';

  let scene: Awaited<ReturnType<typeof createTileScene>>;
  try {
    scene = await createTileScene(canvas);
  } catch (err) {
    status.textContent = `WebGL2 scene init failed: ${(err as Error).message}`;
    return;
  }

  populateWallWithDora(scene.mesh, /*seed=*/ 0x515421);
  scene.drawNow();

  // Self-seat meld row.  Phase L W7+ will own one row per seat.
  const display: MeldDisplayState = createMeldDisplay(0);
  // Track per-instance claim tweens so the rAF tick can drive them.
  const claims: ClaimAnimationHandle[] = [];

  function tickClaims(): void {
    if (claims.length === 0) return;
    const now = performance.now();
    let anyStillRunning = false;
    for (const claim of claims) {
      if (claim.step(now)) anyStillRunning = true;
    }
    scene.requestRedraw();
    if (anyStillRunning) {
      window.requestAnimationFrame(tickClaims);
    }
  }

  // Stage a meld group: append to the display, build source +
  // target matrices, fire the claim animation.  Source matrices
  // are pulled from the live wall (so the tiles visibly leave the
  // wall as the claim fires); targets are the meld-slot matrices
  // for the new group's anchor.
  function stageClaim(group: MeldGroup, baseStart: number): void {
    // First compute the meld-row anchor BEFORE appending so the
    // animation's target matches where appendMeld() will place the
    // tiles.
    const origin = nextMeldOriginXZ(display);
    const { matrices } = appendMeld(display, group);
    // Last `group.tiles.length` matrices in the display are the
    // freshly-appended group's resolved target poses.
    const tileCount = group.tiles.length;
    const targets: Float32Array[] = [];
    const sources: Float32Array[] = [];
    for (let t = 0; t < tileCount; t++) {
      const targetIdx = matrices.length - tileCount + t;
      targets.push(new Float32Array(matrices[targetIdx]));
      // Source: pick a wall tile starting at instance index baseStart+t.
      const wallIdx = baseStart + t;
      sources.push(
        new Float32Array(scene.mesh.modelData.subarray(wallIdx * 16, wallIdx * 16 + 16)),
      );
    }
    const handle = startClaimAnimation(group.kind, sources, targets, performance.now());
    claims.push(handle);
    // Wire the handle to a redraw — every `step()` mutates
    // handle.tiles[i].out; we copy those out matrices into the
    // wall instances each frame for the duration of the tween.
    const baseStartIdx = baseStart;
    const tween = handle;
    const tileIds = group.tiles;
    const animate = (): void => {
      const now = performance.now();
      const still = tween.step(now);
      for (let t = 0; t < tween.tiles.length; t++) {
        scene.setTileAt(baseStartIdx + t, tween.tiles[t].out, tileIds[t]);
      }
      scene.requestRedraw();
      if (still) window.requestAnimationFrame(animate);
    };
    window.requestAnimationFrame(animate);
    // Force a redraw at the meld-slot anchor in case the rAF tick
    // is throttled.  Slot anchor is a debugging helper used by the
    // Phase L W7 scene-runtime to seed neighbouring meld rows.
    void meldSlotMatrix(0, 0, origin);
  }

  // Stage three claims with a small wall-clock gap so each renders
  // independently.  Source-tile indices are arbitrary (0..2, 3..5,
  // 6..9) — the smoke is purely visual.
  stageClaim({ kind: 'pung', tiles: [1, 1, 1], claimedFromSeat: 2 }, 0);
  window.setTimeout(() => {
    stageClaim({ kind: 'chi', tiles: [10, 11, 12], claimedFromSeat: 3 }, 3);
  }, claimDurationMs('pung') + 80);
  window.setTimeout(() => {
    stageClaim({ kind: 'kong', tiles: [25, 25, 25, 25], claimedFromSeat: 1 }, 6);
  }, claimDurationMs('pung') + claimDurationMs('chi') + 200);

  window.requestAnimationFrame(tickClaims);
  installCameraModePicker(scene.camera, scene.canvas, scene.requestRedraw);

  const atlasLabel = scene.atlas.fallback
    ? `synthesized ${scene.atlas.width}×${scene.atlas.height} fallback atlas`
    : `loaded ${scene.atlas.width}×${scene.atlas.height} canonical atlas`;
  status.textContent =
    `Meld scene rendered — ${CANONICAL_WALL_TILE_COUNT} tiles, ${atlasLabel}.  `
    + `Pung / chi / kong claims fan into seat 0's meld row over ~1.5 s.`;
}

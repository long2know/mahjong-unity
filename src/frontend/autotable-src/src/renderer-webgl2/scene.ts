// Phase K Wave 17 — WebGL2 scene runtime (Hicks, Frontend).
//
// Phase L W3 spike — a scene orchestrator that ties together the W15
// hello-world (`./index`), the W16 tile-mesh graph (`./tile-mesh`),
// the W16 tile-atlas (`./tile-atlas`), and the W16 orbital camera
// (`./camera`) into a single reusable runtime.  Until W17 the
// mahjong-table consumer (`./hello`) wired these by hand inside one
// async function; W17 extracts the wiring into `createTileScene()` so
// the production renderer-webgl2 path (Phase L W4+) can mount the
// same scene from a real game's `World` instance without re-deriving
// the GL plumbing.
//
// What's HERE in W17:
//   • `createTileScene()`   — build a tile-mesh scene against the
//     supplied canvas (compiles the shader, allocates the mesh,
//     acquires the atlas, attaches mouse + touch controls, wires
//     a request-animation-frame loop with on-demand draw scheduling).
//   • `TileScene` interface — the public handle, exposing the mesh /
//     atlas / camera + `requestRedraw()`, `setTileInstanceAt()`,
//     `disposeScene()`.
//   • Adaptive devicePixelRatio handling — re-sizes the canvas
//     framebuffer to match `canvas.clientWidth × clientHeight ×
//     window.devicePixelRatio` on every redraw so the rendered tile
//     edges stay crisp on hi-DPI displays.
//   • On-demand draw loop — `requestRedraw()` schedules ONE frame via
//     rAF, so an idle scene burns zero CPU.  Continuous-anim mode is
//     opt-in (Phase L W4 animation graph).
//
// What's NOT here (Phase L W4+):
//   • Picking / raycaster integration (W17 ships `./picking`
//     separately; the consumer wires the pick callback to mouse
//     up itself for now).
//   • Animation scheduler / tween graph (W4).
//   • Multi-pass lighting / post-fx (W6).
//
// Bundle math (W16 → W17 target, ≤ 40 KB total for
// `renderer-webgl2`):
//   • W16 baseline: 19,017 B.
//   • W17 budget for scene + picking + math additions: ≤ ~21 KB
//     headroom (the W17 target of ≤ 40 KB).

import { compileProgram, createWebgl2Context } from './index';
import {
  TileMesh,
  createTileMesh,
  drawTileMesh,
  setTileInstance,
  uploadTileInstances,
  TILE_INSTANCE_FS,
  TILE_INSTANCE_VS,
  disposeTileMesh,
} from './tile-mesh';
import {
  TileAtlas,
  acquireTileAtlas,
  disposeTileAtlas,
} from './tile-atlas';
import {
  OrbitCamera,
  attachMouseControls,
  attachTouchControls,
  createOrbitCamera,
  viewProjMatrix,
  type CameraControlsHandle,
} from './camera';

export interface TileSceneOptions {
  /** Override the canonical atlas URL (defaults to TILE_ATLAS_URL_DEFAULT). */
  atlasUrl?: string;
  /** Initial orbit-camera state overrides. */
  cameraInitial?: Parameters<typeof createOrbitCamera>[0];
  /** Override the default light direction (defaults to (0.4, 1.0, 0.3)). */
  lightDir?: [number, number, number];
  /** Override the clear colour (defaults to a dark-green felt). */
  clearColor?: [number, number, number, number];
  /** When true, mouse + touch controls are NOT attached. */
  noPointerControls?: boolean;
  /** When true, the window-level resize listener is NOT attached. */
  noResizeListener?: boolean;
}

export interface TileScene {
  gl: WebGL2RenderingContext;
  program: WebGLProgram;
  mesh: TileMesh;
  atlas: TileAtlas;
  camera: OrbitCamera;
  canvas: HTMLCanvasElement;
  /** Schedule ONE rAF-aligned redraw.  Subsequent calls before the
   *  frame fires are coalesced (only one rAF callback runs). */
  requestRedraw(): void;
  /** Push one tile instance update; calls `uploadTileInstances` lazily
   *  on the next redraw (batched). */
  setTileAt(index: number, modelMatrix: Float32Array, tileId: number): void;
  /** Force an immediate upload + draw (synchronous, bypasses rAF). */
  drawNow(): void;
  /** Release every GL handle + detach controls. */
  dispose(): void;
}

const DEFAULT_LIGHT_DIR: [number, number, number] = [0.4, 1.0, 0.3];
const DEFAULT_CLEAR_COLOR: [number, number, number, number] = [0.05, 0.08, 0.05, 1.0];

/**
 * Create a fully-wired tile scene against `canvas`.  The async path
 * waits for the atlas to load (or the synthesized fallback) before
 * resolving so the caller knows the first draw will produce a real
 * image, not a blank texture.
 */
export async function createTileScene(
  canvas: HTMLCanvasElement,
  options: TileSceneOptions = {},
): Promise<TileScene> {
  const gl = createWebgl2Context(canvas);
  if (gl === null) {
    throw new Error('[scene] WebGL2 unavailable in this browser');
  }

  const program = compileProgram(gl, TILE_INSTANCE_VS, TILE_INSTANCE_FS);
  const mesh = createTileMesh(gl, program);
  const atlas = await acquireTileAtlas(gl, options.atlasUrl);
  const camera = createOrbitCamera(options.cameraInitial);

  const lightDir = options.lightDir ?? DEFAULT_LIGHT_DIR;
  const clearColor = options.clearColor ?? DEFAULT_CLEAR_COLOR;

  const uViewProj = gl.getUniformLocation(program, 'u_viewProj');
  const uAtlasGrid = gl.getUniformLocation(program, 'u_atlasGrid');
  const uAtlas = gl.getUniformLocation(program, 'u_atlas');
  const uLightDir = gl.getUniformLocation(program, 'u_lightDir');

  let pendingFrame: number | null = null;
  let pendingUpload = false;
  let disposed = false;

  const draw = (): void => {
    if (disposed) return;
    // Adaptive devicePixelRatio: match the framebuffer to the CSS-
    // pixel × DPR product so retina displays don't render fuzzy
    // tile edges.
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const cssW = Math.max(1, canvas.clientWidth | 0);
    const cssH = Math.max(1, canvas.clientHeight | 0);
    const w = Math.floor(cssW * dpr);
    const h = Math.floor(cssH * dpr);
    if (canvas.width !== w) canvas.width = w;
    if (canvas.height !== h) canvas.height = h;
    gl.viewport(0, 0, canvas.width, canvas.height);

    gl.clearColor(clearColor[0], clearColor[1], clearColor[2], clearColor[3]);
    gl.enable(gl.DEPTH_TEST);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

    gl.useProgram(program);
    gl.uniformMatrix4fv(uViewProj, false, viewProjMatrix(camera, canvas));
    gl.uniform2f(uAtlasGrid, atlas.gridCols, atlas.gridRows);
    gl.uniform3f(uLightDir, lightDir[0], lightDir[1], lightDir[2]);

    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, atlas.texture);
    gl.uniform1i(uAtlas, 0);

    if (pendingUpload) {
      uploadTileInstances(gl, mesh);
      pendingUpload = false;
    }

    drawTileMesh(gl, mesh);
  };

  const requestRedraw = (): void => {
    if (pendingFrame !== null) return;
    pendingFrame = window.requestAnimationFrame(() => {
      pendingFrame = null;
      draw();
    });
  };

  const setTileAt = (index: number, modelMatrix: Float32Array, tileId: number): void => {
    setTileInstance(mesh, index, modelMatrix, tileId);
    pendingUpload = true;
    requestRedraw();
  };

  const drawNow = (): void => {
    if (pendingFrame !== null) {
      window.cancelAnimationFrame(pendingFrame);
      pendingFrame = null;
    }
    draw();
  };

  let mouseHandle: CameraControlsHandle | null = null;
  let touchHandle: CameraControlsHandle | null = null;
  if (options.noPointerControls !== true) {
    mouseHandle = attachMouseControls(canvas, camera, requestRedraw);
    touchHandle = attachTouchControls(canvas, camera, requestRedraw);
  }

  let resizeListener: (() => void) | null = null;
  if (options.noResizeListener !== true) {
    resizeListener = (): void => requestRedraw();
    window.addEventListener('resize', resizeListener, { passive: true });
  }

  return {
    gl,
    program,
    mesh,
    atlas,
    camera,
    canvas,
    requestRedraw,
    setTileAt,
    drawNow,
    dispose(): void {
      if (disposed) return;
      disposed = true;
      if (pendingFrame !== null) {
        window.cancelAnimationFrame(pendingFrame);
        pendingFrame = null;
      }
      mouseHandle?.detach();
      touchHandle?.detach();
      if (resizeListener !== null) {
        window.removeEventListener('resize', resizeListener);
      }
      disposeTileMesh(gl, mesh);
      disposeTileAtlas(gl, atlas);
      gl.deleteProgram(program);
    },
  };
}

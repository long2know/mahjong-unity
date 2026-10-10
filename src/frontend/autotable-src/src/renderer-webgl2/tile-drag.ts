// Phase K Wave 20 — WebGL2 tile drag-and-drop (Hicks, Frontend).
//
// Phase L W5 spike continuation — mouse / touch drag-and-drop with
// pointer-events + hover outline highlight.  W19 wired the canonical
// wall + camera modes; W20 layers an interactive surface on top so a
// player can press-and-drag a tile from the wall to the in-hand row
// (or vice versa).
//
// Design:
//   • One unified pointer-events handler covers both mouse and
//     touch (saves ~1 KB vs. duplicating mousedown / touchstart).
//   • Hover state is its own pointer-move probe — we don't drag-on-
//     hover, the user must `pointerdown` to start the drag.  Hover
//     just highlights the tile the cursor is over with an outline
//     animation.
//   • The drag handler delegates the actual ray-pick to
//     `pickTile()` from `./picking`, so the same hit-test path the
//     W17/W19 click handlers use also drives the drag.
//   • Modifier keys: shift = pan (the camera handler owns shift, so
//     we early-out on shift-drag); ctrl = orbit (camera owns); the
//     plain left-button drag is the tile drag.
//   • Drop semantics:
//       - drag to canvas region that hits another tile → "place on"
//         (the consumer decides whether to swap, stack, etc.)
//       - drag to empty canvas region → "place at" with the world-
//         space hit point against a y=0 plane (the floor of the
//         table).  The consumer can clamp to the in-hand row's
//         canonical position itself.
//
// The module ships a thin event surface — `attachTileDrag()` —
// that takes the consumer's mesh + camera + pick helpers + a
// configurable callback bag, and returns a teardown handle.
//
// Bundle math: target the W20 `renderer-webgl2` chunk ≤ 45 KB.
// W19 baseline 30,174 B; tile-pick-animation + tile-drag together
// must fit in ~15 KB.  This module sticks to function exports
// (no class hierarchies); branches kept tight; no allocations in
// the per-pointer-move hot path beyond what `pickTile()` already
// does inside its loop.

import type { TileMesh } from './tile-mesh';
import type { OrbitCamera } from './camera';
import { viewProjMatrix } from './camera';
import { buildPickRay, pickTile, type PickHit } from './picking';

export interface TileDragCallbacks {
  /** Called when the pointer hovers over a tile (or null = off-tile). */
  onHover?: (instanceIndex: number | null) => void;
  /** Called when a drag starts (pointerdown on a tile). */
  onDragStart?: (instanceIndex: number, hit: PickHit) => void;
  /** Called as the pointer moves while dragging.  `hit` is the
   *  current ray-vs-tile hit (a different tile = potential drop
   *  target); `floor` is the world-space y=0 hit point (always
   *  defined unless the ray is parallel to y=0). */
  onDragMove?: (
    instanceIndex: number,
    hit: PickHit | null,
    floor: [number, number, number] | null,
  ) => void;
  /** Called on pointerup.  `targetIndex` is the tile under the
   *  cursor at release (null = empty space); `floor` is the
   *  world-space y=0 hit point (used for "place at" semantics). */
  onDragEnd?: (
    sourceIndex: number,
    targetIndex: number | null,
    floor: [number, number, number] | null,
  ) => void;
  /** Called when the drag is cancelled (escape key / pointercancel). */
  onDragCancel?: (sourceIndex: number) => void;
}

export interface TileDragOptions {
  /** When true, drags fire even when shift / ctrl held (default false). */
  modifierBypass?: boolean;
}

export interface TileDragHandle {
  /** Currently-hovered tile instance index (null = none). */
  hoverIndex(): number | null;
  /** Currently-dragging tile instance index (null = no active drag). */
  dragIndex(): number | null;
  /** Detach every event listener + clear hover/drag state. */
  detach(): void;
}

/**
 * Attach drag-and-drop + hover outline to a canvas backed by a
 * WebGL2 tile-mesh + orbit camera.  Returns a handle the consumer
 * can detach on scene teardown.
 *
 * The function is pure event wiring — it does NOT mutate the mesh
 * itself.  All side effects (highlight outline, lift animation,
 * tile-swap) happen inside the consumer's callbacks.  The picker
 * raycasts the current `mesh.modelData` on every move, so the
 * consumer's animation updates show up in subsequent picks for
 * free (no cache to invalidate).
 */
export function attachTileDrag(
  canvas: HTMLCanvasElement,
  mesh: TileMesh,
  camera: OrbitCamera,
  callbacks: TileDragCallbacks = {},
  options: TileDragOptions = {},
): TileDragHandle {
  let hoverIndex: number | null = null;
  let dragIndex: number | null = null;

  function shouldSkipModifier(ev: PointerEvent | KeyboardEvent): boolean {
    if (options.modifierBypass === true) return false;
    return ev.shiftKey || ev.ctrlKey || ev.metaKey;
  }

  function rayPick(ev: PointerEvent): PickHit | null {
    const vp = viewProjMatrix(camera, canvas);
    const ray = buildPickRay(canvas, ev.clientX, ev.clientY, vp);
    return pickTile(mesh, ray);
  }

  function rayFloor(ev: PointerEvent): [number, number, number] | null {
    const vp = viewProjMatrix(camera, canvas);
    const ray = buildPickRay(canvas, ev.clientX, ev.clientY, vp);
    if (Math.abs(ray.direction[1]) < 1e-8) return null;
    // Solve origin.y + t * direction.y = 0  =>  t = -origin.y / direction.y
    const t = -ray.origin[1] / ray.direction[1];
    if (t <= 0) return null;
    return [
      ray.origin[0] + ray.direction[0] * t,
      ray.origin[1] + ray.direction[1] * t,
      ray.origin[2] + ray.direction[2] * t,
    ];
  }

  function setHover(next: number | null): void {
    if (next === hoverIndex) return;
    hoverIndex = next;
    callbacks.onHover?.(next);
  }

  function onPointerMove(ev: PointerEvent): void {
    if (dragIndex !== null) {
      const hit = rayPick(ev);
      const floor = rayFloor(ev);
      // While dragging, the "hover" surface shows the candidate
      // drop target (different tile) — exclude the source from
      // the hover highlight (it's already lifted).
      const targetHover =
        hit !== null && hit.instanceIndex !== dragIndex ? hit.instanceIndex : null;
      setHover(targetHover);
      callbacks.onDragMove?.(dragIndex, hit, floor);
      return;
    }
    // Not dragging — just update hover.
    const hit = rayPick(ev);
    setHover(hit !== null ? hit.instanceIndex : null);
  }

  function onPointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return; // only left button / primary touch
    if (shouldSkipModifier(ev)) return;
    const hit = rayPick(ev);
    if (hit === null) return;
    dragIndex = hit.instanceIndex;
    // Capture future pointer events to this canvas even when the
    // pointer leaves the canvas's bounding box (matches the
    // standard HTML5 drag-and-drop UX).
    try { canvas.setPointerCapture(ev.pointerId); } catch { /* not supported */ }
    setHover(null);
    callbacks.onDragStart?.(dragIndex, hit);
    ev.preventDefault();
  }

  function onPointerUp(ev: PointerEvent): void {
    if (dragIndex === null) return;
    const source = dragIndex;
    const hit = rayPick(ev);
    const floor = rayFloor(ev);
    const target = hit !== null && hit.instanceIndex !== source
      ? hit.instanceIndex
      : null;
    dragIndex = null;
    try { canvas.releasePointerCapture(ev.pointerId); } catch { /* not supported */ }
    callbacks.onDragEnd?.(source, target, floor);
    // Re-evaluate hover at the release point.
    setHover(hit !== null ? hit.instanceIndex : null);
  }

  function onPointerCancel(_ev: PointerEvent): void {
    if (dragIndex === null) return;
    const source = dragIndex;
    dragIndex = null;
    setHover(null);
    callbacks.onDragCancel?.(source);
  }

  function onPointerLeave(_ev: PointerEvent): void {
    if (dragIndex !== null) return; // capture should keep us alive
    setHover(null);
  }

  function onKeyDown(ev: KeyboardEvent): void {
    if (ev.key !== 'Escape') return;
    if (dragIndex === null) return;
    const source = dragIndex;
    dragIndex = null;
    setHover(null);
    callbacks.onDragCancel?.(source);
  }

  canvas.addEventListener('pointermove', onPointerMove, { passive: true });
  canvas.addEventListener('pointerdown', onPointerDown);
  canvas.addEventListener('pointerup', onPointerUp);
  canvas.addEventListener('pointercancel', onPointerCancel);
  canvas.addEventListener('pointerleave', onPointerLeave, { passive: true });
  window.addEventListener('keydown', onKeyDown);

  return {
    hoverIndex(): number | null { return hoverIndex; },
    dragIndex(): number | null { return dragIndex; },
    detach(): void {
      canvas.removeEventListener('pointermove', onPointerMove);
      canvas.removeEventListener('pointerdown', onPointerDown);
      canvas.removeEventListener('pointerup', onPointerUp);
      canvas.removeEventListener('pointercancel', onPointerCancel);
      canvas.removeEventListener('pointerleave', onPointerLeave);
      window.removeEventListener('keydown', onKeyDown);
      hoverIndex = null;
      dragIndex = null;
    },
  };
}

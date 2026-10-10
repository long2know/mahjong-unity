// Phase K Wave 7 — Custom outline replacement for OutlinePass.
//
// Why this exists
// ---------------
// `three/examples/jsm/postprocessing/OutlinePass.js` carries
// ~85 kB minified — the bulk of which is the full post-process
// pipeline (depth pre-pass, edge detection, gaussian blur,
// pattern texture, optional selective bloom).  We use exactly one
// feature: a yellow halo around the currently-pointed-at tile
// (`outlinePass.selectedObjects = [mesh]`).
//
// Apone has been pushing back on the renderer chunk for two waves
// (W6 logged it as the bundler-swap-or-bust blocker).  W7 takes
// the bundler swap (Parcel → Vite) but the OutlinePass also goes
// — replaced by this ~3 kB helper.  Together they take the heavy
// three-renderer chunk under the <550 kB target.
//
// How it works
// ------------
// The classic "inverted hull" outline: clone each selected mesh's
// geometry, scale it slightly along its vertex normals in a
// `BackSide` ShaderMaterial that writes the outline colour, and
// add the cloned mesh as a child of the original.  The result:
//
//   • The original mesh renders normally (it sits inside the
//     hull).
//   • The hull pokes through the silhouette as a constant-width
//     outline ring.
//
// Compared to the OutlinePass shader pipeline this is one extra
// draw call per selected mesh and zero post-processing — we can
// keep `renderer.render(scene, camera)` directly (no
// EffectComposer, no RenderPass).
//
// Visual parity
// -------------
// OutlinePass renders a yellow stroke ~3 px wide at the discard
// prompt zoom level.  The hull-expansion factor is tuned to the
// tile mesh's bounding sphere so the visible width matches under
// both orthographic + perspective cameras.  See
// `docs/frontend-three-budget.md` §3 for the side-by-side audit.
//
// API surface (drop-in replacement subset of OutlinePass)
// -------------------------------------------------------
//   • `new CustomOutline()` — construct once at renderer setup.
//   • `outline.setSelected(Mesh[])` — equivalent to
//     `outlinePass.selectedObjects = [...]`.
//   • `outline.setEdgeColor(0xffff99)` — visible-edge tint.
//
// All other OutlinePass features (hidden-edge color, edge
// thickness in pixels, pattern texture, selective-bloom hook)
// are intentionally not ported — none were configured in the W6
// codebase.

import { BackSide, Color, Mesh, ShaderMaterial } from 'three';
import type { BufferGeometry } from 'three';
/** Hull-expansion factor as a fraction of the mesh's bounding-sphere radius. */
const OUTLINE_THICKNESS = 0.022;

/** Default visible-edge tint matches OutlinePass's W6 setting (`0xffff99`). */
const DEFAULT_EDGE_COLOR = 0xffff99;

/**
 * Phase K Wave 9 — Default tint for the commentary tile-ref highlight
 * pool.  Distinct from `DEFAULT_EDGE_COLOR` so a player who clicks a
 * commentary chip can see the 3D mesh outline cleanly even when the
 * same tile is also part of a current selection (mouse hover or
 * drag-claim).
 */
const DEFAULT_HIGHLIGHT_COLOR = 0xff8c1a;

/**
 * Phase K Wave 9 — Hull-expansion factor for the highlight pool.  A
 * slightly thicker base than the selection outline so the screenshot
 * diff in `outline-shader-visual.spec.ts` and the upcoming
 * `mesh-highlight-pulse.spec.ts` can detect the pulse on the silhouette
 * without confusing it with the steady selection ring.
 */
const HIGHLIGHT_OUTLINE_THICKNESS = 0.036;

const OUTLINE_USERDATA_KEY = '__autotableOutlineHull';

interface OutlineHullUserData {
  /** Index into CustomOutline's hull pool. */
  readonly index: number;
}

const VERTEX_SHADER = /* glsl */ `
  uniform float outlineThickness;
  void main() {
    // Expand along the vertex normal in object-space; the world-
    // space scale follows because the parent mesh's matrixWorld
    // is propagated through the cloned hull as a child.  The
    // factor is multiplied by an attribute-space length to keep
    // thickness roughly constant under the orthographic camera
    // (object-space units, not pixels).
    vec3 expanded = position + normalize(normal) * outlineThickness;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(expanded, 1.0);
  }
`;

const FRAGMENT_SHADER = /* glsl */ `
  uniform vec3 outlineColor;
  void main() {
    gl_FragColor = vec4(outlineColor, 1.0);
  }
`;

function makeHullMaterial(color: Color, thickness: number): ShaderMaterial {
  return new ShaderMaterial({
    uniforms: {
      outlineColor: { value: color.clone() },
      outlineThickness: { value: thickness },
    },
    vertexShader: VERTEX_SHADER,
    fragmentShader: FRAGMENT_SHADER,
    side: BackSide,
    transparent: false,
    depthTest: true,
    depthWrite: true,
  });
}

function computeThicknessFor(geometry: BufferGeometry, factor: number = OUTLINE_THICKNESS): number {
  // Mesh thickness ∝ bounding-sphere radius so tiny meshes and
  // very large meshes both get a visually consistent stroke.
  if (geometry.boundingSphere === null) {
    geometry.computeBoundingSphere();
  }
  const radius = geometry.boundingSphere?.radius ?? 1;
  return radius * factor;
}

/**
 * Drop-in replacement for the subset of OutlinePass we actually
 * use.  Manages a pool of hull-mesh children attached to the
 * selected meshes.
 *
 * Phase K Wave 9 — Adds a second, independent hull pool for the
 * "commentary tile-ref highlight" feature: when the user clicks a
 * tile-ref chip in the commentary panel, the corresponding 3D mesh
 * outlines in an attention-grabbing colour for 2 s with a sin-wave
 * intensity envelope.  The highlight pool is parallel to the
 * selection pool (separate active list, separate hull materials)
 * so the two surfaces co-exist on the same mesh without fighting
 * the per-frame `setSelected` rebuild.
 */
export class CustomOutline {
  private color: Color = new Color(DEFAULT_EDGE_COLOR);
  private active: Mesh[] = [];
  private hulls = new WeakMap<Mesh, Mesh>();

  // Phase K Wave 9 — Highlight pool internals (commentary tile-ref).
  private highlightColor: Color = new Color(DEFAULT_HIGHLIGHT_COLOR);
  private highlightActive: Mesh[] = [];
  private highlightHulls = new WeakMap<Mesh, Mesh>();
  private highlightIntensity: number = 1;

  /**
   * Set the currently-outlined meshes.  Idempotent — passing
   * the same array twice in a row is a no-op (the hulls stay
   * attached).
   */
  setSelected(meshes: ReadonlyArray<Mesh>): void {
    if (meshes.length === 0 && this.active.length === 0) return;

    // Detach hulls from meshes no longer selected.
    for (const prev of this.active) {
      if (meshes.indexOf(prev) === -1) {
        this.detachHull(prev);
      }
    }
    // Attach hulls to newly-selected meshes.
    for (const next of meshes) {
      if (this.active.indexOf(next) === -1) {
        this.attachHull(next);
      }
    }
    this.active = meshes.slice();
  }

  /** Update the visible-edge color (`outlinePass.visibleEdgeColor` analog). */
  setEdgeColor(hex: number): void {
    this.color.setHex(hex);
    // Propagate to live hulls.
    for (const mesh of this.active) {
      const hull = this.hulls.get(mesh);
      if (hull !== undefined) {
        const mat = hull.material as ShaderMaterial;
        (mat.uniforms.outlineColor.value as Color).copy(this.color);
      }
    }
  }

  /**
   * Phase K Wave 9 — Set the commentary highlight pool.  Separate
   * from `setSelected` so a click on a tile-ref chip can outline a
   * mesh in attention-orange even when the same mesh is also part
   * of the current mouse selection.  Empty array clears the pool.
   *
   * `intensity` is a 0..1 scalar (sin-wave envelope value from
   * `MainView`) that scales the outline thickness for the pulse
   * animation.  Pass 1.0 for a steady outline.
   */
  setHighlight(meshes: ReadonlyArray<Mesh>, intensity: number = 1): void {
    if (meshes.length === 0 && this.highlightActive.length === 0) {
      this.highlightIntensity = intensity;
      return;
    }
    // Detach hulls from meshes no longer highlighted.
    for (const prev of this.highlightActive) {
      if (meshes.indexOf(prev) === -1) {
        this.detachHighlightHull(prev);
      }
    }
    // Attach hulls to newly-highlighted meshes.
    for (const next of meshes) {
      if (this.highlightActive.indexOf(next) === -1) {
        this.attachHighlightHull(next);
      }
    }
    this.highlightActive = meshes.slice();
    this.highlightIntensity = intensity;
    this.applyHighlightIntensity();
  }

  /**
   * Phase K Wave 9 — Update only the per-frame pulse intensity
   * without rebuilding the hull pool.  `MainView.render()` calls this
   * each frame while a highlight pulse is in-flight so the outline
   * width modulates with the sin-wave envelope.
   */
  setHighlightIntensity(intensity: number): void {
    if (this.highlightIntensity === intensity) return;
    this.highlightIntensity = intensity;
    this.applyHighlightIntensity();
  }

  /** Update the commentary-highlight tint (defaults to `0xff8c1a`). */
  setHighlightColor(hex: number): void {
    this.highlightColor.setHex(hex);
    for (const mesh of this.highlightActive) {
      const hull = this.highlightHulls.get(mesh);
      if (hull !== undefined) {
        const mat = hull.material as ShaderMaterial;
        (mat.uniforms.outlineColor.value as Color).copy(this.highlightColor);
      }
    }
  }

  /**
   * Pre-warm one hull instance against a placeholder mesh so the
   * shader is compiled (avoids the first-selection stutter the
   * W6 codebase worked around with `outlinePass.selectedObjects
   * .push(dummy); composer.render(); pop()` in `main-view.ts`).
   */
  precompile(dummy: Mesh, renderOnce: () => void): void {
    this.attachHull(dummy);
    renderOnce();
    this.detachHull(dummy);
  }

  /** Dispose all hull resources.  Idempotent. */
  dispose(): void {
    for (const mesh of this.active) {
      this.detachHull(mesh);
    }
    this.active = [];
    for (const mesh of this.highlightActive) {
      this.detachHighlightHull(mesh);
    }
    this.highlightActive = [];
  }

  // ── Internals ─────────────────────────────────────────────────

  private attachHull(mesh: Mesh): void {
    if (this.hulls.has(mesh)) return;
    const geom = mesh.geometry as BufferGeometry;
    const thickness = computeThicknessFor(geom, OUTLINE_THICKNESS);
    const material = makeHullMaterial(this.color, thickness);
    const hull = new Mesh(geom, material);
    hull.renderOrder = (mesh.renderOrder ?? 0) - 1;
    hull.userData[OUTLINE_USERDATA_KEY] = {
      index: this.active.length,
    } satisfies OutlineHullUserData;
    mesh.add(hull);
    this.hulls.set(mesh, hull);
  }

  private detachHull(mesh: Mesh): void {
    const hull = this.hulls.get(mesh);
    if (hull === undefined) return;
    mesh.remove(hull);
    (hull.material as ShaderMaterial).dispose();
    this.hulls.delete(mesh);
  }

  private attachHighlightHull(mesh: Mesh): void {
    if (this.highlightHulls.has(mesh)) return;
    const geom = mesh.geometry as BufferGeometry;
    const baseThickness = computeThicknessFor(geom, HIGHLIGHT_OUTLINE_THICKNESS);
    const material = makeHullMaterial(this.highlightColor, baseThickness * this.highlightIntensity);
    // Stash the base thickness so the per-frame intensity update can
    // multiply against it without recomputing the bounding sphere.
    material.userData.baseOutlineThickness = baseThickness;
    const hull = new Mesh(geom, material);
    // Push the highlight hull one rendering tier behind the selection
    // hull so when both are attached the highlight ring sits on the
    // outside (the inverted-hull silhouette is widest first; the
    // selection ring layers on top because its hull is thinner).
    hull.renderOrder = (mesh.renderOrder ?? 0) - 2;
    mesh.add(hull);
    this.highlightHulls.set(mesh, hull);
  }

  private detachHighlightHull(mesh: Mesh): void {
    const hull = this.highlightHulls.get(mesh);
    if (hull === undefined) return;
    mesh.remove(hull);
    (hull.material as ShaderMaterial).dispose();
    this.highlightHulls.delete(mesh);
  }

  private applyHighlightIntensity(): void {
    for (const mesh of this.highlightActive) {
      const hull = this.highlightHulls.get(mesh);
      if (hull === undefined) continue;
      const mat = hull.material as ShaderMaterial;
      const base = (mat.userData.baseOutlineThickness as number) ?? HIGHLIGHT_OUTLINE_THICKNESS;
      mat.uniforms.outlineThickness.value = base * this.highlightIntensity;
    }
  }
}

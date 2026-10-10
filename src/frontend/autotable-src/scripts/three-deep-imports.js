#!/usr/bin/env node
/*
 * Phase K Wave 8 — `from 'three'` → per-class deep-import rewriter.
 *
 * Three.js's published entry point (`build/three.module.js`) is one
 * giant ESM file with cross-references between every class.  Rollup
 * can tree-shake exported names but not the transitive class-to-class
 * references inside the bundle, so unused materials, loaders, and
 * texture classes (~35 kB minified) tag along inside the heavy
 * `three-renderer-big` chunk.
 *
 * Switching each named import to its individual source file at
 * `three/src/<group>/<Class>.js` cleanly cuts the graph rollup
 * follows.  This script rewrites every `from 'three';` (or
 * "three";) import in src/**.ts into a fan of per-class imports
 * pointing at the deep paths.
 *
 * Run from `src/frontend/autotable-src/`:
 *
 *     node scripts/three-deep-imports.js [--dry-run]
 *
 * Idempotent: a file already converted is left alone (the script
 * only matches the bare `from 'three'` specifier).
 */
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { resolve, join } from 'node:path';

const ROOT = resolve(new URL('..', import.meta.url).pathname, 'src');
const DRY = process.argv.includes('--dry-run');

// ── Symbol → deep path table ────────────────────────────────────────
// Constants live in `three/src/constants.js`; classes follow the
// `three/src/<group>/<Name>.js` convention used throughout three.
const SYMBOL_PATHS = {
  // ── constants ──────────────────────────────────────────────────
  BackSide: 'three/src/constants.js',
  FrontSide: 'three/src/constants.js',
  DoubleSide: 'three/src/constants.js',
  RepeatWrapping: 'three/src/constants.js',
  ClampToEdgeWrapping: 'three/src/constants.js',
  LinearSRGBColorSpace: 'three/src/constants.js',
  SRGBColorSpace: 'three/src/constants.js',
  // ── math ───────────────────────────────────────────────────────
  Box3: 'three/src/math/Box3.js',
  Color: 'three/src/math/Color.js',
  Euler: 'three/src/math/Euler.js',
  Frustum: 'three/src/math/Frustum.js',
  Matrix4: 'three/src/math/Matrix4.js',
  Quaternion: 'three/src/math/Quaternion.js',
  Vector2: 'three/src/math/Vector2.js',
  Vector3: 'three/src/math/Vector3.js',
  // ── core ───────────────────────────────────────────────────────
  BufferAttribute: 'three/src/core/BufferAttribute.js',
  BufferGeometry: 'three/src/core/BufferGeometry.js',
  InstancedBufferAttribute: 'three/src/core/InstancedBufferAttribute.js',
  InstancedBufferGeometry: 'three/src/core/InstancedBufferGeometry.js',
  Object3D: 'three/src/core/Object3D.js',
  Raycaster: 'three/src/core/Raycaster.js',
  // ── cameras ────────────────────────────────────────────────────
  Camera: 'three/src/cameras/Camera.js',
  OrthographicCamera: 'three/src/cameras/OrthographicCamera.js',
  PerspectiveCamera: 'three/src/cameras/PerspectiveCamera.js',
  // ── lights ─────────────────────────────────────────────────────
  AmbientLight: 'three/src/lights/AmbientLight.js',
  DirectionalLight: 'three/src/lights/DirectionalLight.js',
  // ── objects ────────────────────────────────────────────────────
  Group: 'three/src/objects/Group.js',
  InstancedMesh: 'three/src/objects/InstancedMesh.js',
  Mesh: 'three/src/objects/Mesh.js',
  // ── materials ──────────────────────────────────────────────────
  Material: 'three/src/materials/Material.js',
  MeshBasicMaterial: 'three/src/materials/MeshBasicMaterial.js',
  MeshLambertMaterial: 'three/src/materials/MeshLambertMaterial.js',
  MeshStandardMaterial: 'three/src/materials/MeshStandardMaterial.js',
  ShaderMaterial: 'three/src/materials/ShaderMaterial.js',
  // ── geometries ─────────────────────────────────────────────────
  BoxGeometry: 'three/src/geometries/BoxGeometry.js',
  PlaneGeometry: 'three/src/geometries/PlaneGeometry.js',
  // ── scenes ─────────────────────────────────────────────────────
  Scene: 'three/src/scenes/Scene.js',
  // ── textures ───────────────────────────────────────────────────
  CanvasTexture: 'three/src/textures/CanvasTexture.js',
  Texture: 'three/src/textures/Texture.js',
  // ── loaders ────────────────────────────────────────────────────
  TextureLoader: 'three/src/loaders/TextureLoader.js',
  // ── renderers ──────────────────────────────────────────────────
  WebGLRenderer: 'three/src/renderers/WebGLRenderer.js',
};

const TYPE_ONLY = new Set(['MeshStandardMaterial']);

const IMPORT_RE = /^import(\s+type)?\s+\{\s*([^}]+?)\s*\}\s+from\s+(['"])three\3;?\s*$/gm;

function rewriteFileContent(text) {
  let changed = false;
  const newText = text.replace(IMPORT_RE, (match, typeOnly, body, quote) => {
    const wholeTypeOnly = typeOnly !== undefined;
    const symbols = body.split(',').map(s => s.trim()).filter(Boolean);
    const byPath = new Map();
    const unknown = [];
    for (const raw of symbols) {
      // tolerate `Foo as Bar`, `type Foo`
      let perItemTypeOnly = false;
      let symRaw = raw;
      if (symRaw.startsWith('type ')) {
        perItemTypeOnly = true;
        symRaw = symRaw.slice(5).trim();
      }
      const symKey = symRaw.split(/\s+/)[0];
      const path = SYMBOL_PATHS[symKey];
      if (path === undefined) {
        unknown.push(symRaw);
        continue;
      }
      const isType = wholeTypeOnly || perItemTypeOnly || TYPE_ONLY.has(symKey);
      const key = `${path}:${isType ? 'type' : 'value'}`;
      const list = byPath.get(key) ?? [];
      list.push(symRaw);
      byPath.set(key, list);
    }
    if (unknown.length > 0) {
      console.error(`Unknown three symbols: ${unknown.join(', ')}`);
      return match;
    }
    changed = true;
    const lines = [];
    // Stable per-path order: sort alphabetically by path then by name.
    const keys = Array.from(byPath.keys()).sort();
    for (const key of keys) {
      const [path, kind] = key.split(':');
      const items = byPath.get(key).sort();
      const typeMark = kind === 'type' ? ' type' : '';
      lines.push(`import${typeMark} { ${items.join(', ')} } from ${quote}${path}${quote};`);
    }
    return lines.join('\n');
  });
  return { text: newText, changed };
}

function walk(dir) {
  const out = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      out.push(...walk(full));
    } else if (entry.endsWith('.ts')) {
      out.push(full);
    }
  }
  return out;
}

const files = walk(ROOT);
let totalChanged = 0;
for (const f of files) {
  const text = readFileSync(f, 'utf8');
  if (!text.includes("from 'three'") && !text.includes('from "three"')) continue;
  const { text: newText, changed } = rewriteFileContent(text);
  if (changed && newText !== text) {
    if (DRY) {
      console.log(`would rewrite: ${f}`);
    } else {
      writeFileSync(f, newText);
      console.log(`rewrote: ${f}`);
    }
    totalChanged += 1;
  }
}
console.log(`${DRY ? 'would change' : 'changed'}: ${totalChanged} file(s)`);

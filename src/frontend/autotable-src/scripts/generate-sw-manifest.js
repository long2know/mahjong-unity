#!/usr/bin/env node
/* eslint-disable */
// Phase K Wave 3 — Post-build SW pre-cache manifest generator.
//
// Parcel emits content-hashed filenames for every asset in the
// distribution.  Wave 2's sw.js cached responses lazily on first hit;
// Wave 3 wants the install-cycle to pre-warm the eager lobby chain so
// a returning user with a flaky network sees the cached lobby on
// page-load rather than a spinner.
//
// This script scans the parcel output directory, picks the artefacts
// that are safe to pre-cache (the eager lobby JS + CSS, the icons,
// `index.html`, and the small unhashed files), and emits a
// `manifest-precache.json` document that sw.js consumes on `install`.
//
// We deliberately exclude:
//   • Hashed scene / chat / voice / audit / history / tournaments /
//     tour chunks — these are still cached lazily on first hit.  Pre-
//     caching them would balloon the install payload from ~250 kB to
//     ~1.5 MB and we'd be back to the pre-Wave-2 cold-load tax.
//   • Large media assets (mp4 / glb) — they're already cache-first on
//     hit, and ~1 MB+ on install is a hostile experience on metered
//     connections.
//
// Invocation: `node scripts/generate-sw-manifest.js`
// Wired from package.json#scripts.build:post (chained after parcel build).

const fs = require('fs');
const path = require('path');

const DIST = path.resolve(__dirname, '..', '..', 'autotable');
const SRC_DIR = path.resolve(__dirname, '..');
const MANIFEST_PATH = path.join(DIST, 'manifest-precache.json');
const SW_SRC = path.join(SRC_DIR, 'sw.js');
const SW_DEST = path.join(DIST, 'sw.js');

// Eager lobby chunk pattern: `autotable-src.<8hex>.{js,css}` is the
// parcel-emitted name of the bootstrap bundle.
const EAGER_RE = /^autotable-src\.[0-9a-f]+\.(js|css)$/i;
// Small unhashed files we want on install (always live at root).
const UNHASHED_ALLOW = new Set([
  'index.html',
  'manifest.webmanifest',
  'about.html',
  'app-icon.svg',
]);
// PWA icons — multiple sizes referenced from manifest.webmanifest.
// Phase K Wave 6 — also pre-cache 192/512 + maskable variants.
const ICON_RE = /^app-icon\.[0-9a-f]+\.svg$/i;
// Phase K Wave 3 — Include the shell chunk too so a player who tap-
// returns to a game URL gets the HUD chrome from cache; the scene
// chunk is still lazy (its weight defeats the install budget).
const SHELL_RE = /^game-bootstrap\.[0-9a-f]+\.(js|css)$/i;
// Phase K Wave 5 — Pre-cache the renderer-critical scene-shell
// coordinator (~5 kB after the Wave-5 split) and the heavy
// three-renderer chunk (~600 kB) so a returning user with the SW
// installed gets the full WebGL boot path from cache on warm
// game-URL loads.  Wave 4 deliberately excluded the renderer chunk
// because pre-caching ~900 kB on install was hostile; Wave 5 splits
// that into a tiny shell + a heavy renderer, both of which we now
// pre-warm (the user is going to fetch them on first game-URL hit
// anyway — install-time gets the SW to commit the cache eviction
// strategy before the first paint).
const SCENE_SHELL_RE = /^scene-shell\.[0-9a-f]+\.(js|css)$/i;
const MODEL_MATH_RE = /^model-math\.[0-9a-f]+\.(js|css)$/i;
// Phase K Wave 6 — Commentary panel is small (<80 kB target) and a
// returning replay-viewer user will pay the chunk cost anyway, so we
// pre-warm it on install.  Spectator livestream is excluded (it pulls
// hls.js from CDN at runtime + only matters for `#/spectate/*` deep
// links — pre-caching is wasted bytes for the average lobby visitor).
const COMMENTARY_RE = /^commentary-panel\.[0-9a-f]+\.(js|css)$/i;
// Phase K Wave 6 — Bracket renderer is dragged in alongside the
// tournaments chunk via static import; parcel inlines it into the
// tournaments hash, so no separate regex is needed.

function listAssets() {
  const files = fs.readdirSync(DIST);
  const assets = new Set();
  for (const f of files) {
    if (EAGER_RE.test(f)) { assets.add(f); continue; }
    if (SHELL_RE.test(f)) { assets.add(f); continue; }
    if (SCENE_SHELL_RE.test(f)) { assets.add(f); continue; }
    if (MODEL_MATH_RE.test(f)) { assets.add(f); continue; }
    if (COMMENTARY_RE.test(f)) { assets.add(f); continue; }
    if (ICON_RE.test(f))  { assets.add(f); continue; }
    if (UNHASHED_ALLOW.has(f)) { assets.add(f); continue; }
  }
  return Array.from(assets).sort();
}

// Hashed-bundle pattern: `<name>.<8+hex>.{js,css}` — used to detect
// candidate stale chunks left behind by previous parcel runs.
const HASHED_CHUNK_RE = /^([a-z0-9_-]+)\.([0-9a-f]{6,})\.(js|css)$/i;

function readIndexBundleNames() {
  const indexPath = path.join(DIST, 'index.html');
  if (!fs.existsSync(indexPath)) return new Set();
  const html = fs.readFileSync(indexPath, 'utf8');
  // Collect every hashed bundle filename referenced by the entry html.
  const live = new Set();
  const RE = /([a-z0-9_-]+\.[0-9a-f]{6,}\.(?:js|css))/gi;
  let m;
  while ((m = RE.exec(html)) !== null) live.add(m[1]);
  return live;
}

function readReferencedFromJsAndCss(live) {
  // Walk every live chunk and any chunk it references — keeps dynamic
  // imports' targets alive even if `index.html` doesn't name them.
  const seen = new Set(live);
  const queue = Array.from(live);
  while (queue.length > 0) {
    const name = queue.shift();
    const full = path.join(DIST, name);
    if (!fs.existsSync(full)) continue;
    let text;
    try { text = fs.readFileSync(full, 'utf8'); } catch { continue; }
    const RE = /([a-z0-9_-]+\.[0-9a-f]{6,}\.(?:js|css))/gi;
    let m;
    while ((m = RE.exec(text)) !== null) {
      if (!seen.has(m[1])) {
        seen.add(m[1]);
        queue.push(m[1]);
      }
    }
  }
  return seen;
}

function pruneStaleChunks() {
  const live = readReferencedFromJsAndCss(readIndexBundleNames());
  const files = fs.readdirSync(DIST);
  let pruned = 0;
  for (const f of files) {
    if (!HASHED_CHUNK_RE.test(f)) continue;
    if (live.has(f)) continue;
    fs.unlinkSync(path.join(DIST, f));
    pruned += 1;
    console.log(`[sw-manifest] pruned stale chunk: ${f}`);
  }
  return pruned;
}

function main() {
  if (!fs.existsSync(DIST)) {
    console.error(`[sw-manifest] dist directory not found: ${DIST}`);
    process.exit(1);
  }

  // Phase K Wave 3 — parcel does not bundle `sw.js` (it's referenced
  // via a string literal in `pwa.ts`, not imported), so copy it
  // ourselves so the dist always carries the latest service-worker.
  if (fs.existsSync(SW_SRC)) {
    fs.copyFileSync(SW_SRC, SW_DEST);
    console.log(`[sw-manifest] copied sw.js (${fs.statSync(SW_DEST).size} bytes)`);
  } else {
    console.warn(`[sw-manifest] sw.js source missing at ${SW_SRC} — skipping copy`);
  }

  // Phase K Wave 3 — prune content-hashed chunks left behind by
  // previous parcel runs.  Parcel's `--no-cache` flag clears its own
  // cache but does not delete superseded outputs from `--dist-dir`,
  // so old `game-bootstrap.<oldhash>.js` keeps growing the deploy
  // payload across waves.  We walk index.html → JS-of-JS to find the
  // live chunk set and delete every other hashed sibling.
  const pruned = pruneStaleChunks();
  if (pruned > 0) {
    console.log(`[sw-manifest] pruned ${pruned} stale chunk(s)`);
  }

  const assets = listAssets();
  if (assets.length === 0) {
    console.error('[sw-manifest] no matching assets found — did parcel build run?');
    process.exit(1);
  }
  const manifest = {
    generatedAt: new Date().toISOString(),
    version: 'autotable-unity-v1',
    assets: assets.map(name => `./${name}`),
  };
  fs.writeFileSync(MANIFEST_PATH, JSON.stringify(manifest, null, 2) + '\n');
  console.log(
    `[sw-manifest] wrote ${assets.length} assets to ${path.relative(process.cwd(), MANIFEST_PATH)}`);
}

main();

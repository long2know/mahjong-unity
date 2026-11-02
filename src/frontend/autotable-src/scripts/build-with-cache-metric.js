#!/usr/bin/env node
/* eslint-disable */
// Phase K Wave 11 — Vite cache hit-rate metric wrapper.
//
// Owned by Hicks (Frontend).  Wraps `vite build` with a pre/post
// inventory of emitted chunk filenames so we can measure how
// effective the W10 Vite cache (`cacheDir = .vite/`) is across
// consecutive builds.  Each chunk that re-emits with an identical
// content-hash + identical byte size between runs is counted as a
// "hit" (the source graph was deterministic — cache effective).
// Each chunk whose hash advanced or whose size changed is a "miss"
// (real source change OR cache invalidation).
//
// Why chunk-hash stability instead of literal "cache directory"
// inventory: production `vite build` runs Rollup, which does NOT
// persist its own transform cache to disk.  Vite's `cacheDir` is
// populated by the dev-server's dep-pre-bundle pass (`vite serve`)
// — it stays empty for `vite build` invocations.  So an mtime walk
// of `.vite/deps/` would report 0/0 every time and tell us nothing.
// Chunk-hash stability captures the practical thing we care about:
// "do unrelated builds produce identical output?".  When CI's
// build cache (the `actions/cache@v4` step in pwa-audit.yml that
// snapshots `.vite/` + the `node_modules/` symlink) is warm, the
// Rollup graph re-runs deterministically and every chunk's
// 8-char content hash matches the previous run.
//
// Emits a single summary line that CI can grep for:
//
//   Cache hit-rate: 86% (12/14 chunks)  cold=0  duration=10.4s
//
// Behaviour
// ---------
//   • Reads the prior chunk inventory from `.vite-cache-metric.json`
//     (the previous run's persisted ledger).
//   • Runs `vite build` with any extra args passed through.
//   • Re-inventories the dist directory, compares chunk hashes,
//     classifies each chunk as `hit` / `miss` / `new`.
//   • First-ever run: `cold=1`, hit-rate 0% by definition.
//
// Invocation
// ----------
//   node scripts/build-with-cache-metric.js                # local
//   THRESHOLD=0.70 node scripts/build-with-cache-metric.js  # CI gate
//
// The `THRESHOLD` env var enables the hit-rate gate; absent → no gate
// (used for cold-baseline runs).  Gate format: 0.0–1.0 float.

const { spawnSync } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');

const SRC_ROOT = path.resolve(__dirname, '..');
const DIST_ROOT = path.resolve(SRC_ROOT, '..', 'autotable');
const LEDGER_FILE = path.resolve(SRC_ROOT, '.vite-cache-metric.json');

// Hashed-chunk filename pattern emitted by vite.config.ts (W7
// stable contract).  Capture group 1 = name, 2 = hash.
const CHUNK_RE = /^([a-z0-9-]+)\.([0-9a-f]{8})\.(?:js|css|webmanifest)$/i;

function inventoryDist(dir) {
  if (!fs.existsSync(dir)) return new Map();
  const out = new Map();
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (!entry.isFile()) continue;
    const m = CHUNK_RE.exec(entry.name);
    if (m === null) continue;
    const stat = fs.statSync(path.join(dir, entry.name));
    // Group by stable chunk name; record latest hash + size.
    out.set(m[1], { hash: m[2], size: stat.size });
  }
  return out;
}

function loadPriorLedger() {
  if (!fs.existsSync(LEDGER_FILE)) return null;
  try {
    const raw = JSON.parse(fs.readFileSync(LEDGER_FILE, 'utf8'));
    if (raw && typeof raw === 'object' && raw.chunks && typeof raw.chunks === 'object') {
      return raw;
    }
  } catch {
    /* corrupt ledger — treat as cold */
  }
  return null;
}

function classifyChunks(before, after) {
  // before: Map<name, {hash, size}> from prior ledger (may be null)
  // after:  Map<name, {hash, size}> from current build
  const names = new Set([...after.keys(), ...(before === null ? [] : before.keys())]);
  let hits = 0; let misses = 0; let news = 0;
  for (const n of names) {
    const a = after.get(n);
    const b = before === null ? undefined : before.get(n);
    if (a === undefined) continue;     // chunk removed; don't count
    if (b === undefined) { news++; continue; }
    if (b.hash === a.hash && b.size === a.size) hits++;
    else misses++;
  }
  return { hits, misses, news, total: hits + misses + news };
}

function main() {
  const prior = loadPriorLedger();
  const beforeChunks = prior === null
    ? null
    : new Map(Object.entries(prior.chunks).map(([k, v]) => [k, v]));
  const cold = prior === null;

  const startMs = Date.now();
  const result = spawnSync(
    path.resolve(SRC_ROOT, 'node_modules', '.bin', 'vite'),
    ['build', ...process.argv.slice(2)],
    { cwd: SRC_ROOT, stdio: 'inherit' }
  );
  const durationS = (Date.now() - startMs) / 1000;

  const afterChunks = inventoryDist(DIST_ROOT);
  const { hits, misses, news, total } = classifyChunks(beforeChunks, afterChunks);
  const hitRate = cold || total === 0 ? 0 : hits / total;
  const pct = (hitRate * 100).toFixed(0);

  // Single-line CI grep target.  Spec format:
  //   "Cache hit-rate: 85% (12/14 chunks)"
  const summary = `Cache hit-rate: ${pct}% (${hits}/${total} chunks)  cold=${cold ? 1 : 0}  duration=${durationS.toFixed(1)}s`;
  console.log(`\n${summary}`);

  // Persist for the next run + downstream tooling (PR comment
  // renderer, ledger).  `.vite-cache-metric.json` is `.gitignore`d.
  const chunksOut = {};
  for (const [name, meta] of afterChunks.entries()) chunksOut[name] = meta;
  const ledger = {
    recordedAt: new Date().toISOString(),
    cold,
    hits, misses, news, total,
    hitRate: Number(hitRate.toFixed(3)),
    durationSeconds: Number(durationS.toFixed(2)),
    chunks: chunksOut,
  };
  fs.writeFileSync(LEDGER_FILE, JSON.stringify(ledger, null, 2));

  // Threshold gate (opt-in via env).  Cold runs skip the gate by
  // design — first build has no prior to compare against.
  const thresholdRaw = process.env.THRESHOLD;
  if (thresholdRaw !== undefined && thresholdRaw !== '') {
    const threshold = Number(thresholdRaw);
    if (Number.isFinite(threshold) && !cold && hitRate < threshold) {
      console.error(`::error::cache hit-rate ${pct}% below threshold ${(threshold * 100).toFixed(0)}%`);
      process.exit(2);
    }
  }

  if (result.status === null) {
    console.error('[build-with-cache-metric] build terminated by signal');
    process.exit(1);
  }
  process.exit(result.status ?? 0);
}

main();

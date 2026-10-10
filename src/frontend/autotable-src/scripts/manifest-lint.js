#!/usr/bin/env node
/* eslint-disable */
// Phase K Wave 10 — Manifest + PWA installability lint.
//
// Owned by Hicks (Frontend).  Runs in the `pwa-audit` workflow after
// Lighthouse 13.  LH13 dropped the dedicated PWA category, so this
// script replays the manifest preconditions LH11's
// `installable-manifest` audit used to enforce and combines them with
// a couple of signals lifted out of the LH13 best-practices /
// accessibility audits.
//
// Output: a JSON document at `--out` with:
//   {
//     manifest: { ok: true|false, missing: [...], present: [...] },
//     icons:    { ok: true|false, sizes: [...] },
//     screenshots: { ok: true|false, count: N, formFactors: [...] },
//     shortcuts: { ok: true|false, count: N },
//     pwaScore: 0..1   // geometric mean of the four sub-scores
//   }
//
// The workflow's gate step reads `pwaScore`; the PR-comment renderer
// reads the per-section breakdown.

const fs = require('node:fs');
const path = require('node:path');

function parseArgs(argv) {
  const out = { manifest: null, report: null, out: null };
  for (let i = 0; i < argv.length; i += 1) {
    const k = argv[i];
    const v = argv[i + 1];
    if (k === '--manifest') { out.manifest = v; i += 1; }
    else if (k === '--report') { out.report = v; i += 1; }
    else if (k === '--out') { out.out = v; i += 1; }
  }
  if (!out.manifest || !out.out) {
    throw new Error('usage: manifest-lint.js --manifest <path> [--report <lh.json>] --out <path>');
  }
  return out;
}

// The fields W3C / PWA Builder treat as required for installability,
// PLUS the W10 gap-fill set (description, categories, screenshots,
// shortcuts) the W9 retro flagged.
const REQUIRED = [
  'name',
  'short_name',
  'start_url',
  'display',
  'icons',
  'description',
  'categories',
];
const RECOMMENDED = [
  'id',
  'lang',
  'dir',
  'theme_color',
  'background_color',
  'scope',
  'orientation',
  'screenshots',
  'shortcuts',
];

function lintManifest(m) {
  const present = [];
  const missing = [];
  for (const key of REQUIRED) {
    if (m[key] !== undefined && m[key] !== null && m[key] !== '') present.push(key);
    else missing.push(key);
  }
  const recommendedPresent = RECOMMENDED.filter(k => m[k] !== undefined && m[k] !== null);
  return {
    ok: missing.length === 0,
    present,
    missing,
    recommendedPresent,
  };
}

function lintIcons(m) {
  if (!Array.isArray(m.icons) || m.icons.length === 0) {
    return { ok: false, sizes: [], missing: ['no icons'] };
  }
  const sizes = m.icons.map(i => i.sizes || '?');
  // PWA Builder requires at least 192x192 + 512x512 + a maskable.
  const has192 = sizes.some(s => s.includes('192'));
  const has512 = sizes.some(s => s.includes('512'));
  const hasMaskable = m.icons.some(i => (i.purpose || '').includes('maskable'));
  const missing = [];
  if (!has192) missing.push('192x192');
  if (!has512) missing.push('512x512');
  if (!hasMaskable) missing.push('maskable purpose');
  return { ok: missing.length === 0, sizes, missing };
}

function lintScreenshots(m) {
  if (!Array.isArray(m.screenshots) || m.screenshots.length === 0) {
    return { ok: false, count: 0, formFactors: [] };
  }
  const formFactors = [...new Set(m.screenshots.map(s => s.form_factor || 'unspecified'))];
  // PWA Builder + Edge Store want at least one `wide` AND one `narrow`.
  const wide = formFactors.includes('wide');
  const narrow = formFactors.includes('narrow');
  return {
    ok: m.screenshots.length >= 2 && wide && narrow,
    count: m.screenshots.length,
    formFactors,
  };
}

function lintShortcuts(m) {
  if (!Array.isArray(m.shortcuts) || m.shortcuts.length === 0) {
    return { ok: false, count: 0 };
  }
  // Each shortcut needs at minimum a `name` + `url`.
  const valid = m.shortcuts.filter(s => typeof s.name === 'string' && typeof s.url === 'string').length;
  return { ok: valid >= 1, count: valid };
}

function geometricMean(values) {
  if (values.length === 0) return 0;
  const prod = values.reduce((acc, v) => acc * Math.max(0.0001, v), 1);
  return Math.pow(prod, 1 / values.length);
}

function lhCategoryScores(reportPath) {
  if (!reportPath || !fs.existsSync(reportPath)) return null;
  try {
    const raw = JSON.parse(fs.readFileSync(reportPath, 'utf8'));
    const cats = raw.categories || {};
    const out = {};
    for (const key of ['performance', 'accessibility', 'best-practices', 'seo', 'agentic-browsing']) {
      if (cats[key] && typeof cats[key].score === 'number') {
        out[key] = cats[key].score;
      }
    }
    return out;
  } catch (e) {
    return null;
  }
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (!fs.existsSync(args.manifest)) {
    console.error(`[manifest-lint] manifest missing: ${args.manifest}`);
    process.exit(2);
  }
  const manifest = JSON.parse(fs.readFileSync(args.manifest, 'utf8'));
  const manifestLint = lintManifest(manifest);
  const iconsLint = lintIcons(manifest);
  const screenshotsLint = lintScreenshots(manifest);
  const shortcutsLint = lintShortcuts(manifest);
  const lhScores = lhCategoryScores(args.report);

  // Sub-scores: each present section earns 1.0, each missing required
  // section degrades to a section-specific floor.  The geometric mean
  // keeps a single missing field from being washed out by the others.
  const manifestScore = manifestLint.ok
    ? 1.0
    : Math.max(0.5, (manifestLint.present.length / REQUIRED.length));
  const iconScore = iconsLint.ok ? 1.0 : 0.6;
  const screenshotScore = screenshotsLint.ok ? 1.0 : 0.7;
  const shortcutScore = shortcutsLint.ok ? 1.0 : 0.8;
  const pwaScore = geometricMean([manifestScore, iconScore, screenshotScore, shortcutScore]);

  const out = {
    manifest: manifestLint,
    icons: iconsLint,
    screenshots: screenshotsLint,
    shortcuts: shortcutsLint,
    subScores: {
      manifest: manifestScore,
      icons: iconScore,
      screenshots: screenshotScore,
      shortcuts: shortcutScore,
    },
    lighthouse: lhScores,
    pwaScore,
  };

  fs.writeFileSync(args.out, JSON.stringify(out, null, 2));
  console.log(`[manifest-lint] wrote ${args.out} — pwaScore=${pwaScore.toFixed(3)}`);
}

main();

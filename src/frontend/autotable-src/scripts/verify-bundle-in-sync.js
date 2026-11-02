#!/usr/bin/env node
/* eslint-disable */
// #119 (Hicks) — deterministic bundle-in-sync gate.
//
// Proves the committed `src/frontend/autotable/` (the dist the backend
// serves) is byte-identical to a fresh `npm run build`, so a stale bundle
// can never ship. Run this AFTER building, from `src/frontend/autotable-src`:
//
//   npm run build && node scripts/verify-bundle-in-sync.js
//
// or, self-contained (builds for you):
//
//   node scripts/verify-bundle-in-sync.js --build
//
// The gate compares the freshly-built dist against the committed (git HEAD)
// dist. The ONLY tolerated difference is the `generatedAt` field inside
// `manifest-precache.json` — an inherent build timestamp with no bearing on
// what is served (every hashed asset is content-addressed). Any other drift
// (a changed/added/removed chunk, html, css, image, sound, or a *semantic*
// manifest change) fails the gate.
//
// Exit codes: 0 = in sync; 1 = drift detected; 2 = usage/environment error.

const { execFileSync, execSync } = require('node:child_process');
const path = require('node:path');

const BUNDLE_DIR = 'src/frontend/autotable';
const MANIFEST_REL = `${BUNDLE_DIR}/manifest-precache.json`;

function git(args, opts = {}) {
  return execFileSync('git', args, { encoding: 'utf8', ...opts }).replace(/\n$/, '');
}

function repoRoot() {
  try {
    return git(['rev-parse', '--show-toplevel']);
  } catch (e) {
    console.error('verify-bundle-in-sync: not inside a git repository.');
    process.exit(2);
  }
}

// A manifest whose ONLY change is the build timestamp is considered in-sync.
// Compare committed vs working with `generatedAt` stripped from both.
function manifestIsTimestampOnlyDrift(root) {
  const abs = path.join(root, MANIFEST_REL);
  let committedRaw;
  try {
    committedRaw = git(['show', `HEAD:${MANIFEST_REL}`], { cwd: root });
  } catch (e) {
    return false; // no committed manifest → treat as real drift
  }
  const workingRaw = require('node:fs').readFileSync(abs, 'utf8');
  let a, b;
  try {
    a = JSON.parse(committedRaw);
    b = JSON.parse(workingRaw);
  } catch (e) {
    return false;
  }
  delete a.generatedAt;
  delete b.generatedAt;
  return JSON.stringify(a) === JSON.stringify(b);
}

function main() {
  const root = repoRoot();

  if (process.argv.includes('--build')) {
    console.log('verify-bundle-in-sync: running `npm run build` ...');
    execSync('npm run build', {
      cwd: path.join(root, 'src/frontend/autotable-src'),
      stdio: 'inherit',
    });
  }

  // Tracked modifications/deletions under the served bundle dir.
  const changed = git(['diff', '--name-only', '--', BUNDLE_DIR], { cwd: root })
    .split('\n')
    .filter(Boolean);
  // Untracked (newly emitted) files under the served bundle dir.
  const untracked = git(
    ['ls-files', '--others', '--exclude-standard', '--', BUNDLE_DIR],
    { cwd: root },
  )
    .split('\n')
    .filter(Boolean);

  const drift = [];
  for (const f of changed) {
    if (f === MANIFEST_REL && manifestIsTimestampOnlyDrift(root)) {
      continue; // tolerated: build-timestamp only
    }
    drift.push(f);
  }
  for (const f of untracked) {
    drift.push(`${f} (new file not committed)`);
  }

  if (drift.length === 0) {
    console.log(
      'verify-bundle-in-sync: OK — committed src/frontend/autotable/ equals a fresh build ' +
        '(only the manifest build-timestamp differs).',
    );
    process.exit(0);
  }

  console.error('verify-bundle-in-sync: DRIFT — the committed bundle is NOT in sync with a fresh build.');
  console.error('The following served-bundle files differ from `npm run build` output:');
  for (const f of drift) console.error(`  • ${f}`);
  console.error('');
  console.error('Fix: from src/frontend/autotable-src run `npm run build`, then commit the');
  console.error('regenerated src/frontend/autotable/ in the SAME commit as the source change.');
  process.exit(1);
}

main();

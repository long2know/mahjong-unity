#!/usr/bin/env node
/* eslint-disable */
// #119 (Hicks) — deterministic served-bundle identity digest.
//
// The backend serves `src/frontend/autotable/`; the SW pre-cache manifest
// (`manifest-precache.json`) enumerates the core content-hashed assets
// (entry chunks, icons, html). Because Vite/Rollup names every emitted
// chunk `[name].[hash:8].[ext]` from its *content*, and every source edit
// cascades into an entry-chunk hash change, the sorted `assets[]` array is
// a stable fingerprint of the whole served bundle. The only non-content
// field (`generatedAt`, a build timestamp) is ignored.
//
// This gives WP-F (Hudson) a "served-hash === build-hash" precondition:
//   • committed identity:  `node scripts/bundle-hash.js`
//   • served identity:     fetch `<baseURL>/manifest-precache.json`, then
//                          sha256 of JSON.stringify([...assets].sort())
// If the two digests match, the backend is serving exactly the committed
// (and, via the bundle-in-sync gate, freshly built) bundle.
//
// Usage:
//   node scripts/bundle-hash.js            # digest of committed bundle
//   node scripts/bundle-hash.js --json     # { digest, version, assets }
//   node scripts/bundle-hash.js --file <path/to/manifest-precache.json>

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

function repoRelDefault() {
  // scripts/ lives at src/frontend/autotable-src/scripts; the served bundle
  // is the sibling `../autotable` dist directory.
  return path.resolve(__dirname, '..', '..', 'autotable', 'manifest-precache.json');
}

function parseArgs(argv) {
  const args = { json: false, file: repoRelDefault() };
  for (let i = 2; i < argv.length; i++) {
    if (argv[i] === '--json') args.json = true;
    else if (argv[i] === '--file') args.file = path.resolve(argv[++i]);
  }
  return args;
}

/**
 * Compute the deterministic bundle-identity digest from a parsed
 * manifest-precache.json object. Exported so a Playwright spec (or the
 * bundle-in-sync gate) can compute the identity of a *fetched* manifest
 * with the exact same algorithm.
 */
function bundleDigestFromManifest(manifest) {
  const assets = Array.isArray(manifest.assets) ? manifest.assets.slice() : [];
  assets.sort();
  const canonical = JSON.stringify(assets);
  const digest = crypto.createHash('sha256').update(canonical).digest('hex');
  return { digest, version: manifest.version ?? null, assets };
}

function main() {
  const args = parseArgs(process.argv);
  if (!fs.existsSync(args.file)) {
    console.error(`bundle-hash: manifest not found at ${args.file} — run \`npm run build\` first.`);
    process.exit(2);
  }
  const manifest = JSON.parse(fs.readFileSync(args.file, 'utf8'));
  const { digest, version, assets } = bundleDigestFromManifest(manifest);
  if (args.json) {
    process.stdout.write(JSON.stringify({ digest, version, assets }, null, 2) + '\n');
  } else {
    process.stdout.write(`sha256:${digest}\n`);
  }
}

module.exports = { bundleDigestFromManifest };

if (require.main === module) {
  main();
}

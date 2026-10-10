#!/usr/bin/env node
/* eslint-disable */
// Phase K Wave 11 — Lighthouse 13 baseline calibration.
//
// W10 migrated `lighthouse@11 → lighthouse@13` and shipped CI
// thresholds that were conservative carry-overs from W9 manual
// runs.  This script computes p50 / p95 over N consecutive LH13
// runs against a local Vite preview so the W11 docs can pin
// observed baselines and CI can flag drift instead of hard-failing
// on noise.
//
// Usage:
//   cd src/frontend/autotable-src
//   npm run build:vite
//   node scripts/lh-baseline.js [runs=5]
//
// Output: prints a Markdown table to stdout AND writes
// `.lh-baseline.json` with per-run scores for downstream tooling.

const { spawn } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');
const http = require('node:http');

const SRC_ROOT = path.resolve(__dirname, '..');
const DIST_ROOT = path.resolve(SRC_ROOT, '..', 'autotable');
const RUNS = Number(process.argv[2] || process.env.LH_RUNS || 5);
const PORT = Number(process.env.LH_PORT || 4175);
const URL = `http://127.0.0.1:${PORT}/`;

async function waitForServer(url, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      await new Promise((resolve, reject) => {
        const req = http.get(url, res => { res.resume(); resolve(res.statusCode); });
        req.on('error', reject);
        req.setTimeout(2000, () => req.destroy(new Error('timeout')));
      });
      return true;
    } catch {
      await new Promise(r => setTimeout(r, 500));
    }
  }
  return false;
}

function percentile(sorted, p) {
  if (sorted.length === 0) return null;
  const idx = Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length));
  return sorted[idx];
}

async function runLighthouse(reportPath) {
  return new Promise((resolve, reject) => {
    const proc = spawn(
      path.resolve(SRC_ROOT, 'node_modules', '.bin', 'lighthouse'),
      [
        URL,
        '--quiet',
        '--chrome-flags=--headless=new --no-sandbox --disable-gpu',
        '--output=json',
        `--output-path=${reportPath}`,
        '--only-categories=performance,accessibility,best-practices,seo',
        '--form-factor=desktop',
        '--screenEmulation.disabled=true',
        '--throttling-method=provided',
        '--max-wait-for-load=45000',
      ],
      { cwd: SRC_ROOT, stdio: ['ignore', 'inherit', 'inherit'] }
    );
    proc.on('exit', code => code === 0 ? resolve() : reject(new Error(`lighthouse exit ${code}`)));
  });
}

async function main() {
  const preview = spawn(
    path.resolve(SRC_ROOT, 'node_modules', '.bin', 'vite'),
    ['preview', '--host', '127.0.0.1', '--port', String(PORT), '--strictPort', '--outDir', DIST_ROOT],
    { cwd: SRC_ROOT, stdio: ['ignore', 'pipe', 'pipe'] }
  );
  preview.stdout.on('data', () => undefined);
  preview.stderr.on('data', () => undefined);

  const results = [];
  let failed = 0;
  try {
    const up = await waitForServer(URL, 30000);
    if (!up) throw new Error(`preview server didn't come up at ${URL}`);

    for (let i = 0; i < RUNS; i++) {
      const reportPath = path.resolve(SRC_ROOT, `.lh-run-${i + 1}.json`);
      try {
        process.stdout.write(`[lh-baseline] run ${i + 1}/${RUNS} ... `);
        await runLighthouse(reportPath);
        // Lighthouse may write to either the exact --output-path or
        // append `.report.json` to whatever was passed; tolerate both.
        let raw;
        if (fs.existsSync(reportPath)) raw = fs.readFileSync(reportPath, 'utf8');
        else if (fs.existsSync(`${reportPath}.report.json`)) raw = fs.readFileSync(`${reportPath}.report.json`, 'utf8');
        else throw new Error('lighthouse produced no report output');
        const r = JSON.parse(raw);
        const scores = {};
        for (const cat of Object.keys(r.categories)) {
          const c = r.categories[cat];
          scores[cat] = c.score === null ? null : Math.round(c.score * 100);
        }
        results.push(scores);
        process.stdout.write(`perf=${scores.performance} a11y=${scores.accessibility} bp=${scores['best-practices']} seo=${scores.seo}\n`);
      } catch (err) {
        failed++;
        process.stdout.write(`FAILED — ${err.message}\n`);
      } finally {
        for (const candidate of [reportPath, `${reportPath}.report.json`]) {
          try { fs.unlinkSync(candidate); } catch { /* ignore */ }
        }
      }
    }
  } finally {
    preview.kill('SIGTERM');
  }

  if (results.length === 0) {
    console.error('[lh-baseline] no successful runs');
    process.exit(1);
  }

  const categories = ['performance', 'accessibility', 'best-practices', 'seo'];
  const stats = {};
  for (const cat of categories) {
    const xs = results.map(r => r[cat]).filter(v => v !== null).sort((a, b) => a - b);
    stats[cat] = {
      min: xs[0] ?? null,
      p50: percentile(xs, 50),
      p95Worst: percentile(xs, 5),  // 5th percentile = worst-case bound
      max: xs[xs.length - 1] ?? null,
      mean: xs.length === 0 ? null : Math.round(xs.reduce((a, b) => a + b, 0) / xs.length),
      runs: xs.length,
    };
  }

  const ledger = {
    recordedAt: new Date().toISOString(),
    url: URL,
    runs: results.length,
    failed,
    stats,
    raw: results,
  };
  fs.writeFileSync(path.resolve(SRC_ROOT, '.lh-baseline.json'), JSON.stringify(ledger, null, 2));

  // Markdown table for the docs.
  console.log('\n| Category | p50 | p95 (worst) | mean | min | max |');
  console.log('|----------|-----|-------------|------|-----|-----|');
  for (const cat of categories) {
    const s = stats[cat];
    console.log(`| ${cat.padEnd(15)} | ${s.p50 ?? '–'} | ${s.p95Worst ?? '–'} | ${s.mean ?? '–'} | ${s.min ?? '–'} | ${s.max ?? '–'} |`);
  }
  console.log(`\nruns successful: ${results.length} / ${RUNS}    failed: ${failed}`);
}

main().catch(err => { console.error(err); process.exit(1); });

import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd(), require = createRequire(path.join(root, 'src/frontend/autotable-src/package.json'));
const { chromium } = require('@playwright/test');
const target = new URL(process.argv[2]), phase = process.argv[3];
assert.equal(target.hostname, '127.0.0.1');
assert.notEqual(target.port, '5086');
assert.match(phase, /^[a-z0-9-]+$/);
const out = path.join(root, 'artifacts/frame-pacing/hotpath', phase);
fs.mkdirSync(out, { recursive: true });
const flags = ['--no-sandbox', '--disable-dev-shm-usage', '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
  '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1'];
const browser = await chromium.launch({ executablePath: '/usr/bin/google-chrome', headless: true, args: flags });
const page = await browser.newPage({ viewport: { width: 960, height: 640 }, deviceScaleFactor: 1, serviceWorkers: 'block' });
try {
  const room = `projection-${phase}-${Date.now()}`;
  const gameUrl = new URL('./', target);
  gameUrl.search = new URLSearchParams({ gameId: room, createGame: room, variant: 'changsha',
    seat: '0', botCount: '3', dealMode: 'auto', seed: '20261010' }).toString();
  await page.goto(gameUrl.href);
  await page.waitForFunction(() => window.game?.renderer?.state === 'ready'
    && window.game.adapter.latest?.session.snapshotReady && window.game.renderer.scene.committed.frame.entities.length > 50);
  await page.waitForTimeout(1000);
  const result = await page.evaluate(async () => {
    const game = window.game, host = game.renderer, scene = host.scene, gl = scene.gl;
    const cpuWorld = [], cpuProjection = [], cpuScene = [], completions = [], submitted = [];
    const initialFrame = scene.committed.frame;
    const normalizedScene = initialFrame.entities.map(e => [e.kind, e.slotName, e.rotationIndex,
      e.appearance.faceIndex, e.appearance.showFace, e.appearance.backIndex]).sort();
    const originalWorld = game.world.updateView, originalRefresh = game.adapter.refresh;
    const originalDraw = gl.drawElementsInstanced, originalFence = gl.fenceSync;
    let active = true, opportunities = 0, readyButUnsubmitted = 0, glDrawCalls = 0, lastChange = -1000;
    const started = performance.now(), initialCompleted = host.metrics.renderedFrames;
    game.world.updateView = function (...args) {
      const start = performance.now();
      try { return originalWorld.apply(this, args); }
      finally { if (active) cpuWorld.push(performance.now() - start); }
    };
    game.adapter.refresh = function (...args) {
      const start = performance.now();
      try { return originalRefresh.apply(this, args); }
      finally { if (active) cpuProjection.push(performance.now() - start); }
    };
    gl.drawElementsInstanced = function (...args) {
      if (active) glDrawCalls++;
      return originalDraw.apply(this, args);
    };
    gl.fenceSync = function (...args) {
      const sync = originalFence.apply(this, args);
      if (active && sync) submitted.push(performance.now() - started);
      return sync;
    };
    const completed = event => {
      if (active) { completions.push(performance.now() - started); cpuScene.push(event.detail.drawMilliseconds); }
    };
    window.addEventListener('mahjong:renderer-frame', completed);
    const motion = now => {
      if (!active) return;
      opportunities++;
      if (!host.inFlight && host.queue) readyButUnsubmitted++;
      const elapsed = Math.max(0, now - started);
      if (elapsed - lastChange >= 750) {
        lastChange = elapsed;
        const target = Math.floor(lastChange / 750) % 2;
        game.lookDown.start(target * .7); game.zoom.start(target * .1);
      }
      requestAnimationFrame(motion);
    };
    requestAnimationFrame(motion);
    await new Promise(resolve => setTimeout(resolve, 5000));
    active = false;
    const elapsedMilliseconds = performance.now() - started;
    game.world.updateView = originalWorld; game.adapter.refresh = originalRefresh;
    gl.drawElementsInstanced = originalDraw; gl.fenceSync = originalFence;
    window.removeEventListener('mahjong:renderer-frame', completed);
    const ext = gl.getExtension('WEBGL_debug_renderer_info');
    return { elapsedMilliseconds, opportunities, readyButUnsubmitted, glDrawCalls, submitted, completions,
      completedMetricDelta: host.metrics.renderedFrames - initialCompleted, cpuWorld, cpuProjection, cpuScene,
      hostState: host.state, adapterFailed: game.adapter.failed, status: document.getElementById('renderer-status')?.textContent,
      normalizedScene, renderer: ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER) };
  });
  assert.equal(result.completedMetricDelta, result.completions.length);
  assert.equal(result.hostState, 'ready', result.status);
  assert.equal(result.adapterFailed, false);
  const summary = samples => {
    const values = samples.slice().sort((a, b) => a - b);
    return { median: values[Math.floor(values.length / 2)], p95: values[Math.floor(values.length * .95)], samples: values.length };
  };
  const build = await (await page.request.get(new URL('./web-build.json', target).href)).json();
  const health = await (await page.request.get(new URL('./health', target).href)).json();
  const sceneSha256 = createHash('sha256').update(JSON.stringify(result.normalizedScene)).digest('hex');
  delete result.normalizedScene;
  const receipt = { status: 'passed', phase, frontendSource: build.sourceRevision, apiSource: health.buildSha,
    scope: 'Five-second real production Game -> World/adapter -> host sample with pass-through CPU timing wrappers; no helper projects entities.',
    timingScope: 'World+projection/publish elapsed CPU time includes World; not GPU execution duration. rAF opportunities are not FPS.',
    browser: browser.version(), flags, viewport: { width: 960, height: 640, dpr: 1 }, sceneSha256, ...result,
    completedHz: result.completions.length * 1000 / result.elapsedMilliseconds,
    submittedHz: result.submitted.length * 1000 / result.elapsedMilliseconds,
    worldCpu: summary(result.cpuWorld), worldProjectionPublishCpu: summary(result.cpuProjection),
    sceneLayoutSubmitCpu: summary(result.cpuScene) };
  fs.writeFileSync(path.join(out, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n');
  console.log(JSON.stringify({ phase, frontendSource: receipt.frontendSource, completed: result.completions.length,
    completedHz: receipt.completedHz, world: receipt.worldCpu, projectionPublish: receipt.worldProjectionPublishCpu,
    scene: receipt.sceneLayoutSubmitCpu }, null, 2));
} finally {
  await browser.close();
}

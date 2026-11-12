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
const out = path.join(root, 'artifacts/frame-pacing', phase);
fs.mkdirSync(out, { recursive: true });
const flags = ['--no-sandbox', '--disable-dev-shm-usage', '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
  '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1'];
const browser = await chromium.launch({ executablePath: '/usr/bin/google-chrome', headless: true, args: flags });
const context = await browser.newContext({ viewport: { width: 960, height: 640 }, deviceScaleFactor: 1, serviceWorkers: 'block' });
const page = await context.newPage();
const errors = [];
page.on('pageerror', error => errors.push(error.message));
try {
  await page.goto(target.href);
  await page.waitForFunction(() => window.pacingFixture?.host.state === 'ready', null, { timeout: 30000 });
  await page.waitForTimeout(1000);
  const result = await page.evaluate(async () => {
    const fixture = window.pacingFixture, host = fixture.host, scene = host.scene, gl = scene.gl;
    const extension = gl.getExtension('WEBGL_debug_renderer_info');
    const debugRenderer = extension ? gl.getParameter(extension.UNMASKED_RENDERER_WEBGL) : null;
    const started = performance.now(), submissions = [], completions = [], cpu = [], observedFenceLag = [];
    let active = true, glDrawCalls = 0, lastSubmission = null, animationOpportunities = 0, readyButUnsubmitted = 0;
    const inspectOpportunity = () => {
      if (!active) return;
      animationOpportunities++;
      if (!host.inFlight && host.queue) readyButUnsubmitted++;
      requestAnimationFrame(inspectOpportunity);
    };
    requestAnimationFrame(inspectOpportunity);
    const draw = gl.drawElementsInstanced.bind(gl), fence = gl.fenceSync.bind(gl);
    gl.drawElementsInstanced = (...args) => { if (active) glDrawCalls++; return draw(...args); };
    gl.fenceSync = (...args) => {
      const result = fence(...args);
      if (active && result) { lastSubmission = performance.now(); submissions.push(lastSubmission - started); }
      return result;
    };
    const completed = event => {
      if (!active) return;
      const now = performance.now();
      completions.push({ at: now - started, frameId: event.detail.receipt.frameId });
      cpu.push(event.detail.drawMilliseconds);
      if (lastSubmission !== null) observedFenceLag.push(now - lastSubmission);
    };
    window.addEventListener('mahjong:renderer-frame', completed);
    const firstMetric = host.metrics.renderedFrames, firstProduced = fixture.framesProduced;
    await new Promise(resolve => setTimeout(resolve, 5000));
    active = false;
    const elapsed = performance.now() - started;
    window.removeEventListener('mahjong:renderer-frame', completed);
    fixture.stop();
    return {
      source: fixture.source, elapsedMilliseconds: elapsed, viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio },
      context: scene.device, debugRenderer, framesProduced: fixture.framesProduced - firstProduced,
      glDrawCalls, submissions, completions, animationOpportunities, readyButUnsubmitted,
      cpuLayoutAndSubmitMilliseconds: cpu,
      submissionToObservedCompletionMilliseconds: observedFenceLag,
      completedMetricDelta: host.metrics.renderedFrames - firstMetric,
      scene: { entities: scene.committed.frame.entities.length, style: scene.style },
    };
  });
  assert.equal(result.completedMetricDelta, result.completions.length);
  assert(result.submissions.length > 0 && result.completions.length > 0);
  assert.deepEqual(errors, []);
  const intervals = result.completions.slice(1).map((entry, i) => entry.at - result.completions[i].at);
  const summary = values => {
    const sorted = values.slice().sort((a, b) => a - b);
    return { median: sorted[Math.floor(sorted.length / 2)] ?? null, p95: sorted[Math.floor(sorted.length * .95)] ?? null };
  };
  const receipt = {
    status: 'passed', phase, browser: browser.version(), flags, target: target.href,
    scope: 'Local headless SwiftShader controlled viewer-safe frozen scene; actual GL submissions and GPU-fence commits, not physical GPU/device FPS.',
    motion: 'Same 960x640 DPR1 populated snapshot, continuous camera look-down/zoom and one entitled tile vertical animation.',
    timingScope: 'CPU layout/submit is not GPU duration; submission-to-observed-completion includes fence polling/CPU scheduling.',
    ...result,
    submittedFramesHz: result.submissions.length * 1000 / result.elapsedMilliseconds,
    completedFramesHz: result.completions.length * 1000 / result.elapsedMilliseconds,
    completionIntervalsMilliseconds: summary(intervals),
    cpuLayoutAndSubmitSummaryMilliseconds: summary(result.cpuLayoutAndSubmitMilliseconds),
    submissionToObservedCompletionSummaryMilliseconds: summary(result.submissionToObservedCompletionMilliseconds),
  };
  const snapshot = await (await context.request.get(new URL('./snapshot.json', target).href)).body();
  receipt.snapshotSha256 = createHash('sha256').update(snapshot).digest('hex');
  await page.screenshot({ path: path.join(out, 'controlled-motion.png') });
  fs.writeFileSync(path.join(out, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n');
  console.log(JSON.stringify({ source: receipt.source, phase, submitted: result.submissions.length,
    completed: result.completions.length, submittedHz: receipt.submittedFramesHz, completedHz: receipt.completedFramesHz,
    cpu: receipt.cpuLayoutAndSubmitSummaryMilliseconds, observedFenceLag: receipt.submissionToObservedCompletionSummaryMilliseconds,
    debugRenderer: result.debugRenderer }, null, 2));
} finally {
  await browser.close();
}

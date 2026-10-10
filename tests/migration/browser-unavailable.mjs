import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';

const root = process.cwd();
const require = createRequire(path.join(root, 'src/frontend/autotable-src/package.json'));
const { chromium } = require('@playwright/test');
console.error('Browser probe stage: module loaded; launching pinned browser');
const base = process.argv[2];
assert(base && new URL(base).hostname === '127.0.0.1');
const origin = new URL(base).origin;
const receipt = {
  qualification: 'Actual Chrome browser shell and unavailable renderer only; no Unity/player/device evidence',
  executable: process.env.MAHJONG_BROWSER_MODE === 'playwright' ? 'lock-matched Playwright Chromium' : '/usr/bin/google-chrome',
  cases: [],
};
let browser;
const launchFailures = [];
const attempts = process.env.MAHJONG_BROWSER_MODE === 'playwright' ? [
  ['playwright-chromium', {}],
] : [
  ['default-headless', {}],
  ['gpu-disabled-shell-only', {
    args: ['--no-sandbox', '--disable-gpu'],
    ignoreDefaultArgs: ['--enable-unsafe-swiftshader'],
  }],
];
for (const [name, options] of attempts) {
  try {
    browser = await chromium.launch({
      ...(process.env.MAHJONG_BROWSER_MODE !== 'playwright' ? { executablePath: '/usr/bin/google-chrome' } : {}),
      headless: true,
      args: ['--no-sandbox'], ...options,
    });
    receipt.launchMode = name;
    break;
  } catch (error) {
    launchFailures.push({
      mode: name, reason: error.message.split('\n')[0],
      signal: /signal=([A-Z]+)/.exec(error.message)?.[1] ?? null,
    });
  }
}
if (!browser) {
  receipt.status = 'blocked';
  receipt.reason = 'Native Chrome could not launch; no application page was loaded';
  receipt.launchFailures = launchFailures;
  process.stdout.write(JSON.stringify(receipt, null, 2) + '\n');
  process.exit(77);
}
try {
  receipt.browserVersion = browser.version();
  console.error('Browser probe stage: launched; loading actual lobby');
  for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
    const context = await browser.newContext({
      viewport, deviceScaleFactor: viewport.width === 390 ? 3 : 1,
      serviceWorkers: 'block',
    });
    try {
      await context.route('**/*', route => new URL(route.request().url()).origin === origin
        ? route.continue() : route.abort());
      const page = await context.newPage();
      page.setDefaultTimeout(10000);
      page.setDefaultNavigationTimeout(20000);
      const pageErrors = [];
      page.on('pageerror', error => pageErrors.push({ name: error.name, message: error.message.slice(0, 250) }));
      await page.goto(`${base}/autotable/`, { waitUntil: 'domcontentloaded' });
      await page.waitForSelector('#lobby-panel.lobby-open');
      assert.equal(await page.locator('#lobby-panel').count(), 1);
      assert.equal(await page.locator('#unity-canvas').count(), 0, 'Bare URL must remain lobby-only');
      assert.equal(await page.evaluate(() => document.body.getAttribute('data-game-shell-ready')), null,
        'DOM game marker is intentionally absent on the lazy lobby route');
      assert.equal(await page.locator('#sticky').isDisabled(), true);
      assert.equal(await page.locator('#benchmark').isDisabled(), true);
      console.error('Browser probe stage: lobby-only route verified; loading table without Unity build');
      const manifestRequests = [];
      page.on('response', response => {
        if (new URL(response.url()).pathname === '/autotable/unity/renderer-build.json') {
          manifestRequests.push(response.status());
        }
      });
      const room = `migration-browser-${viewport.width}-${Date.now()}`;
      await page.goto(
        `${base}/autotable/?gameId=${room}&variant=changsha&seat=0&botCount=0&dealMode=manual`,
        { waitUntil: 'domcontentloaded' },
      );
      await page.waitForSelector('#unity-status[data-state="unavailable"]', { timeout: 10000 });
      assert.match(await page.locator('#unity-status h2').innerText(), /Unity build unavailable/);
      assert(manifestRequests.includes(404), 'The actual missing manifest must be requested and observed as 404');
      assert.equal(await page.locator('#unity-canvas').count(), 1);
      assert.equal(await page.locator('#unity-canvas').isVisible(), false);
      assert.equal(await page.evaluate(() => document.body.getAttribute('data-unity-renderer-ready')), null);
      assert.equal(await page.evaluate(() => document.body.getAttribute('data-scene-shell-ready')), null);
      await page.waitForFunction(() => window.__mahjongClient?.connected(), { timeout: 10000 });
      assert.equal(await page.locator('#chat-panel').count(), 1);
      assert.equal(await page.locator('script[src*="/unity/"]').count(), 0);
      assert.equal(pageErrors.length, 0, JSON.stringify(pageErrors));
      receipt.cases.push({
        viewport, lobbyOnlyWithoutCanvas: true, manifestStatus: 404,
        visibleState: 'unavailable', primaryClientConnected: true,
        rendererReady: false, sceneReady: false, legacyFallback: false, pageErrors: 0,
      });
    } finally {
      await context.close();
    }
  }
  receipt.status = 'passed';
  process.stdout.write(JSON.stringify(receipt, null, 2) + '\n');
} catch (error) {
  receipt.status = 'failed';
  receipt.failure = { name: error.name, message: error.message.slice(0, 2500) };
  process.stdout.write(JSON.stringify(receipt, null, 2) + '\n');
  process.exitCode = 1;
} finally {
  await browser.close();
}

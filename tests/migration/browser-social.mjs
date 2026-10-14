import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';

const require = createRequire(path.join(process.cwd(), 'src/frontend/autotable-src/package.json'));
const { chromium } = require('@playwright/test');
const base = process.argv[2];
assert(base && new URL(base).hostname === '127.0.0.1');
const origin = new URL(base).origin;
const receipt = {
  qualification: 'Actual Chromium lobby, invitations, chat and unavailable-table viewport routing; not Unity/player/device gameplay',
  cases: [],
};
let browser;
const contexts = [];
const errors = [];
console.error('Social browser stage: launch');
try {
  browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
  receipt.browserVersion = browser.version();
  async function pageFor(viewport, mobile = false) {
    const context = await browser.newContext({
      viewport, deviceScaleFactor: mobile ? 3 : 1,
      ...(mobile ? { isMobile: true, hasTouch: true } : {}),
      serviceWorkers: 'block',
    });
    contexts.push(context);
    await context.route('**/*', route => new URL(route.request().url()).origin === origin
      ? route.continue() : route.abort());
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    page.setDefaultNavigationTimeout(20000);
    page.on('pageerror', error => errors.push({ name: error.name, message: error.message.slice(0, 350) }));
    return page;
  }
  async function openChat(page) {
    if (await page.locator('#lobby-panel.lobby-open').count()) {
      await page.locator('#lobby-open-chat').click();
    } else if (await page.locator('#chat-panel.chat-panel-collapsed').count()) {
      await page.locator('#chat-toggle').click();
    }
    await page.waitForSelector('#chat-panel:not(.chat-panel-collapsed)');
    await page.waitForSelector('#online-players-list[aria-busy="false"]', { state: 'attached' });
  }
  async function unavailable(page) {
    await page.waitForSelector('#unity-status[data-state="unavailable"]');
    assert.match(await page.locator('#unity-status h2').innerText(), /Unity build unavailable/);
    await page.waitForFunction(() => window.__mahjongClient?.connected()
      && window.__mahjongClient.serverSnapshotGameId === window.__mahjongClient.lastGameId);
    assert.equal(await page.evaluate(() => document.body.getAttribute('data-unity-renderer-ready')), null);
    assert.equal(await page.evaluate(() => document.body.getAttribute('data-scene-shell-ready')), null);
    assert.equal(await page.locator('canvas').count(), 1);
    assert.equal(await page.locator('#unity-canvas').isVisible(), false);
    assert.equal(await page.locator('script[src*="/unity/"]').count(), 0);
  }
  const owner = await pageFor({ width: 1280, height: 800 });
  const guest = await pageFor({ width: 390, height: 844 }, true);
  console.error('Social browser stage: real lobby and independent identities');
  for (const page of [owner, guest]) {
    await page.goto(`${base}/autotable/`, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#lobby-panel.lobby-open');
    assert.equal(await page.locator('canvas').count(), 0);
    await openChat(page);
    assert.equal(await page.locator('#chat-input').isDisabled(), true);
    assert.equal(await page.locator('#chat-send').isDisabled(), true);
    assert.match(await page.locator('#chat-unavailable').innerText(), /join.*table/i);
  }
  receipt.cases.push({ name: 'lobby-only', canvasAbsent: true, bareRoomChatDisabled: true, bothSocialShellsMounted: true });
  console.error('Social browser stage: create a real manual room without a Unity build');
  const room = `migration-social-${Date.now()}`;
  const params = new URLSearchParams({
    gameId: room, createGame: room, variant: 'changsha', seat: '0', botCount: '0', dealMode: 'manual',
  });
  const manifestStatuses = [];
  owner.on('response', response => {
    if (new URL(response.url()).pathname === '/autotable/unity/renderer-build.json') {
      manifestStatuses.push(response.status());
    }
  });
  await owner.goto(`${base}/autotable/?${params}`, { waitUntil: 'domcontentloaded' });
  await unavailable(owner);
  assert(manifestStatuses.includes(404));
  await openChat(owner);
  await owner.waitForFunction(() => document.getElementById('chat-input')?.disabled === false);
  assert.equal(await owner.evaluate(() => window.__mahjongClient.seat), 0);
  console.error('Social browser stage: deliver the source invitation through the DOM/hub');
  await owner.waitForSelector('[data-testid="online-player-invite"]:not([disabled])');
  await owner.locator('[data-testid="online-player-invite"]:not([disabled])').first().click();
  await guest.waitForSelector('[data-testid="table-invite-join"][href]');
  const join = guest.locator('[data-testid="table-invite-join"][href]').first();
  const joinUrl = new URL(await join.getAttribute('href'), origin);
  assert.equal(joinUrl.origin, origin);
  assert.equal(joinUrl.searchParams.get('gameId'), room);
  assert.equal(joinUrl.searchParams.get('join'), '1');
  await join.click();
  await unavailable(guest);
  await openChat(guest);
  await guest.waitForFunction(() => document.getElementById('chat-input')?.disabled === false);
  assert.equal(await guest.evaluate(() => window.__mahjongClient.lastGameId), room);
  const guestSeat = await guest.evaluate(() => window.__mahjongClient.seat);
  assert(Number.isInteger(guestSeat) && guestSeat !== 0);
  receipt.cases.push({
    name: 'real-invitation-and-join', sameOriginAllowlistedJoinOnlyUrl: true,
    ownerSeat: 0, guestSeat, manifest404Observed: true, rendererReady: false, legacyFallback: false,
  });
  console.error('Social browser stage: real chat send, receive and reload');
  await owner.locator('#chat-input').fill('Migration browser message');
  await owner.locator('#chat-send').click();
  await owner.waitForSelector('.chat-message-body');
  await guest.waitForFunction(() => [...document.querySelectorAll('.chat-message-body')]
    .some(element => element.textContent === 'Migration browser message'));
  await guest.reload({ waitUntil: 'domcontentloaded' });
  await unavailable(guest);
  await openChat(guest);
  await guest.waitForFunction(() => [...document.querySelectorAll('.chat-message-body')]
    .some(element => element.textContent === 'Migration browser message'));
  assert.equal(await guest.evaluate(() => window.__mahjongClient.lastGameId), room);
  receipt.cases.push({ name: 'real-chat-reload', sentThroughDom: true, otherBrowserReceived: true, signedRoomReattached: true, historyRestored: true });
  console.error('Social browser stage: real desktop/mobile viewport changes on unavailable table');
  const seat = await owner.evaluate(() => window.__mahjongClient.seat);
  const viewports = [];
  for (const size of [{ width: 390, height: 844 }, { width: 844, height: 390 }, { width: 1280, height: 800 }]) {
    await owner.setViewportSize(size);
    await owner.waitForFunction(() => {
      const main = document.getElementById('main').getBoundingClientRect();
      const viewport = window.game?.renderer.viewport;
      return viewport && Math.abs(viewport.widthCssPx - main.width) < 1
        && Math.abs(viewport.heightCssPx - main.height) < 1;
    });
    const measured = await owner.evaluate(() => {
      const viewport = window.game.renderer.viewport;
      return {
        widthCssPx: viewport.widthCssPx, heightCssPx: viewport.heightCssPx,
        widthPx: viewport.widthPx, heightPx: viewport.heightPx,
        renderPixelRatio: viewport.renderPixelRatio, resizeRevision: viewport.resizeRevision,
        handBandCssPx: viewport.handBandCssPx,
        actorSeat: window.__mahjongClient.seat, state: window.game.renderer.state,
      };
    });
    assert.equal(measured.actorSeat, seat);
    assert.equal(measured.state, 'unavailable');
    assert.equal(measured.widthPx, Math.round(measured.widthCssPx * measured.renderPixelRatio));
    assert.equal(measured.heightPx, Math.round(measured.heightCssPx * measured.renderPixelRatio));
    assert.equal(measured.handBandCssPx > 0, measured.widthCssPx < 768 || measured.heightCssPx < 520);
    if (viewports.length) assert(measured.resizeRevision > viewports.at(-1).resizeRevision);
    viewports.push(measured);
  }
  receipt.cases.push({ name: 'actual-unavailable-viewport-routing', measurements: viewports, seatAuthorityUnchanged: true, unityFrameRendered: false });
  const response = await owner.request.get(`${base}/autotable/renderer-module-graph.json`);
  assert.equal(response.status(), 200);
  const graph = await response.json();
  assert.equal(graph.qualification, 'source-only');
  assert.equal(graph.modules.some(module =>
    /src\/(?:asset-loader|main-view|object-view|mouse-ui|three-renderer|thing-group|center)\.ts|src\/renderer-webgl2\//.test(module)), false);
  assert.equal(errors.length, 0, JSON.stringify(errors));
  receipt.moduleGraph = { moduleCount: graph.modules.length, legacySourceImports: false, actualServedGraph: true };
  receipt.pageErrors = errors;
  receipt.status = 'passed';
} catch (error) {
  receipt.status = browser ? 'failed' : 'blocked';
  receipt.failure = { name: error.name, message: error.message.slice(0, 3000) };
  receipt.pageErrors = errors;
  process.exitCode = browser ? 1 : 77;
} finally {
  for (const context of contexts) await context.close();
  if (browser) await browser.close();
  process.stdout.write(JSON.stringify(receipt, null, 2) + '\n');
}

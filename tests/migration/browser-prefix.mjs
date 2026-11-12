import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const root = process.cwd();
const require = createRequire(path.join(root, 'src/frontend/autotable-src/package.json'));
const { chromium } = require('@playwright/test');
const target = new URL(process.argv[2]);
assert.equal(target.hostname, '127.0.0.1');
assert.notEqual(target.port, '5086');
const prefix = target.pathname.replace(/\/$/, '');
const publicRoot = target.origin + prefix;
const name = process.env.PREFIX_CASE_NAME ?? 'native-prefix';
assert.match(name, /^[a-z0-9-]+$/);
const output = path.join(root, 'artifacts/base-url', name);
fs.mkdirSync(output, { recursive: true });
const receipt = { status: 'running', target: publicRoot, prefix,
  qualification: process.env.PREFIX_QUALIFICATION ?? 'Native ASP.NET path-base browser case; not nginx evidence',
  requests: [], sockets: [], sent: [], pageErrors: [], cases: [] };
const browser = await chromium.launch({
  executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', headless: true,
  args: ['--no-sandbox', '--disable-dev-shm-usage', '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
    '--ignore-certificate-errors', '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1'],
});
const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, serviceWorkers: 'allow', ignoreHTTPSErrors: true });
await context.addInitScript(() => {
  const NativeSocket = window.WebSocket;
  window.__prefixSockets = [];
  window.WebSocket = class extends NativeSocket {
    constructor(...args) { super(...args); window.__prefixSockets.push(this); }
  };
});
const page = await context.newPage();
page.on('pageerror', error => receipt.pageErrors.push(error.message));
page.on('request', request => {
  const url = new URL(request.url());
  receipt.requests.push({ origin: url.origin, path: url.pathname, method: request.method() });
});
page.on('websocket', socket => {
  const url = new URL(socket.url()); receipt.sockets.push({ protocol: url.protocol, path: url.pathname });
  socket.on('framesent', frame => {
    try { receipt.sent.push(JSON.parse(String(frame.payload))); } catch {}
  });
});

async function ready() {
  await page.waitForFunction(() => window.game?.renderer?.state === 'ready'
    && window.game.renderer.scene.committed?.frame.session.snapshotReady
    && window.game.renderer.scene.committed.frame.entities.length > 50, null, { timeout: 45000 });
}
try {
  const health = await (await context.request.get(target.origin + prefix + '/health')).json();
  const buildResponse = await context.request.get(publicRoot + '/web-build.json');
  const build = await buildResponse.json();
  assert.equal(health.status, 'healthy'); assert.equal(build.engine, 'webgl2');
  assert.equal(build.sourceRevision, health.buildSha);
  if (process.env.EXPECTED_SOURCE_SHA) assert.equal(build.sourceRevision, process.env.EXPECTED_SOURCE_SHA);
  receipt.source = build.sourceRevision;
  assert(!buildResponse.headers()['content-security-policy'].includes("'wasm-unsafe-eval'"));
  const entry = await context.request.get(publicRoot + '/?action=none', { maxRedirects: 0 });
  assert.equal(entry.status(), 200, 'the public mount must serve the UI directly, never redirect to a legacy suffix');
  assert.equal(entry.headers().location, undefined);
  assert(entry.headers()['content-type'].startsWith('text/html'));
  assert((await entry.text()).includes('Mahjong Web'));
  receipt.entryResponse = { status: entry.status(), location: null };
  if (prefix) {
    const bare = await context.request.get(publicRoot + '?action=none', { maxRedirects: 0 });
    assert.equal(bare.status(), 302);
    assert.equal(bare.headers().location, prefix + '/?action=none');
    receipt.trailingSlashRedirect = { status: bare.status(), location: bare.headers().location };
  }
  await page.goto(publicRoot + '/?action=none', { waitUntil: 'domcontentloaded' });
  assert.equal(new URL(page.url()).pathname, prefix + '/');
  await page.waitForFunction(() => navigator.serviceWorker.controller !== null, null, { timeout: 15000 });
  const sw = await page.evaluate(async () => {
    const registration = await navigator.serviceWorker.ready;
    return { scope: registration.scope, script: registration.active.scriptURL };
  });
  assert.equal(sw.scope, publicRoot + '/');
  assert.equal(sw.script, publicRoot + '/sw.js');
  receipt.cases.push({ name: 'direct-mount-root-ui-and-worker', ...sw });
  const manifest = await (await context.request.get(publicRoot + '/manifest.webmanifest')).json();
  assert.equal(new URL(manifest.start_url, publicRoot + '/manifest.webmanifest').pathname, prefix + '/');
  assert.equal(new URL(manifest.scope, publicRoot + '/manifest.webmanifest').pathname, prefix + '/');
  assert(manifest.shortcuts.every(shortcut => shortcut.url.startsWith('./')));

  const room = `prefix-changsha-${Date.now()}`;
  await page.goto(publicRoot + '/?' + new URLSearchParams({
    gameId: room, createGame: room, variant: 'changsha', seat: '0', botCount: '3', dealMode: 'auto', seed: '20261010',
  }));
  await ready();
  await page.waitForFunction(() => window.__prefixSockets.some(socket => socket.url.includes('/hubs/changsha') && socket.readyState === 1));
  const hubCount = await page.evaluate(() => window.__prefixSockets.filter(socket => socket.url.includes('/hubs/changsha')).length);
  await page.evaluate(() => window.__prefixSockets.find(socket => socket.url.includes('/hubs/changsha') && socket.readyState === 1).close());
  await page.waitForFunction(count => window.__prefixSockets.filter(socket => socket.url.includes('/hubs/changsha')).length > count
    && window.__prefixSockets.some(socket => socket.url.includes('/hubs/changsha') && socket.readyState === 1), hubCount,
  { timeout: 15000 });
  await ready();
  receipt.cases.push({ name: 'actual-mounted-signalr-negotiate-websocket-and-reconnect', hubConnections: hubCount + 1 });
  const tile = await page.evaluate(() => {
    const scene = window.game.renderer.scene;
    const entity = scene.committed.frame.entities.find(entity => entity.interaction.canDiscard && entity.appearance.showFace);
    return { id: entity.entityId, ...scene.project(entity.entityId) };
  });
  const box = await page.locator('#webgl-canvas').boundingBox();
  const targetPoint = { x: box.x + tile.x, y: box.y + tile.y };
  assert.equal(await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.id, targetPoint), 'webgl-canvas');
  const before = receipt.sent.flatMap(message => message.entries ?? []).filter(entry => entry[0] === 'discard').length;
  await page.mouse.dblclick(targetPoint.x, targetPoint.y, { delay: 20 });
  await page.waitForFunction(id => !window.game.adapter.latest.localHand.entityIds.includes(id), tile.id);
  await page.waitForTimeout(150);
  assert.equal(receipt.sent.flatMap(message => message.entries ?? []).filter(entry => entry[0] === 'discard').length, before + 1);
  await page.screenshot({ path: path.join(output, 'mounted-game-progressed.png'), fullPage: true });
  receipt.cases.push({ name: 'actual-authoritative-discard-and-assets-under-mount', serverRemovedFromHand: true });
  await page.locator('#settings-button').click(); await page.keyboard.press('Escape');
  await page.locator('#lobby-toggle').click();
  assert.equal(new URL(page.url()).origin, target.origin);
  assert.equal(new URL(page.url()).pathname, prefix + '/');
  await page.locator('#lobby-close').click();
  const previousRoom = new URL(page.url()).searchParams.get('gameId');
  await Promise.all([
    page.waitForURL(url => url.searchParams.get('gameId') !== previousRoom && url.pathname === prefix + '/'),
    page.locator('#new-game').click(),
  ]);
  await ready();
  assert.equal(new URL(page.url()).pathname, prefix + '/');
  receipt.cleanRootNewGameNavigation = { gameIdChanged: true, snapshotReady: true };

  const relayRoom = `prefix-relay-${Date.now()}`;
  await page.goto(publicRoot + '/?' + new URLSearchParams({
    gameId: relayRoom, createGame: relayRoom, variant: 'four_player', seat: '0', botCount: '0',
  }));
  await ready();
  assert.equal(await page.evaluate(() => window.game.client.connectionMode), 'relay');
  assert(receipt.sockets.some(socket => socket.path === prefix + '/ws'));
  assert(receipt.sockets.every(socket => !socket.path.toLowerCase().includes('autotable')));
  receipt.cases.push({ name: 'actual-clean-relay-websocket-mounted', realSnapshotReady: true });
  const cookies = await context.cookies(publicRoot + '/');
  const identity = cookies.find(cookie => cookie.name === 'mahjong_pid');
  assert(identity && identity.path === (prefix || '') + '/');
  if (target.protocol === 'https:') assert.equal(identity.secure, true, 'trusted nginx forwarded scheme must reach real cookie issuance');
  receipt.identityCookie = { path: identity.path, secure: identity.secure, httpOnly: identity.httpOnly };
  const cachePaths = await page.evaluate(async () => {
    const names = await caches.keys(), result = [];
    for (const name of names) {
      if (!name.startsWith('mahjong-web:')) continue;
      const cache = await caches.open(name);
      for (const request of await cache.keys()) result.push({ cache: name, path: new URL(request.url).pathname });
    }
    return result;
  });
  assert(cachePaths.length > 0);
  const publicAssets = new Set(build.files.map(file => prefix + '/' + file.path));
  assert(cachePaths.every(entry => publicAssets.has(entry.path) || entry.path === prefix + '/'
    || entry.path === prefix + '/api/games/public'));
  assert(cachePaths.every(entry => !entry.path.toLowerCase().includes('autotable')));
  receipt.cachePaths = cachePaths;
  assert(receipt.requests.some(request => request.path === prefix + '/api/identity'));
  assert(receipt.requests.some(request => request.path === prefix + '/hubs/changsha/negotiate'));
  assert(receipt.requests.some(request => /Faces(?:Labelled)?\.[0-9a-f]+\.png$/.test(request.path)));
  assert.deepEqual(receipt.requests.filter(request => request.origin !== target.origin
    || !request.path.startsWith(prefix + '/')), []);
  assert.deepEqual(receipt.requests.filter(request => request.path.toLowerCase().includes('autotable')), []);
  assert.deepEqual(receipt.pageErrors, []);
  receipt.status = 'passed';
} catch (error) {
  receipt.status = 'failed'; receipt.error = error.message;
  receipt.inputState = await page.evaluate(() => {
    const renderer = window.game?.renderer;
    return { state: renderer?.state, inputBlocked: renderer?.inputBlocked, suspended: renderer?.suspended,
      session: renderer?.scene?.committed?.frame.session, click: renderer?.input?.click };
  }).catch(() => null);
  await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {});
  throw error;
} finally {
  receipt.completedAtUtc = new Date().toISOString();
  fs.writeFileSync(path.join(output, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n');
  await browser.close();
}
console.log(JSON.stringify({ status: receipt.status, source: receipt.source, prefix, cases: receipt.cases }, null, 2));

import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { inflateSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';

const require = createRequire(path.join(process.cwd(), 'src/frontend/autotable-src/package.json'));
const { chromium } = require('@playwright/test');
const base = process.argv[2];
assert(base && new URL(base).hostname === '127.0.0.1');
const origin = new URL(base).origin;
const outputPart = process.env.MAHJONG_PLAYER_OUTPUT_SUBDIR ?? '';
assert(/^[a-zA-Z0-9_-]*$/.test(outputPart));
const probeRoot = path.join('/probe', outputPart);
const layoutOnly = process.env.MAHJONG_PLAYER_SCOPE === 'manual-layout';
const receipt = {
  qualification: 'Actual Unity-generated WebGL initial-board/startup/layout in container Chromium; not complete gameplay/rule/dealt-hand-privacy or physical mobile qualification',
  scope: process.env.MAHJONG_PLAYER_SCOPE ?? 'all-variant-startup',
  stage: 'browser-launch', cases: [], caseFailures: [], pageErrors: [], consoleErrors: [], consoleMessages: [], cspViolations: [], responses: [],
};
const wire = [];
let browser;
let page;
const contexts = [];

function pngPixels(bytes) {
  assert.equal(bytes.subarray(1, 4).toString(), 'PNG');
  let width, height, channels;
  const chunks = [];
  for (let offset = 8; offset < bytes.length;) {
    const size = bytes.readUInt32BE(offset);
    const kind = bytes.subarray(offset + 4, offset + 8).toString();
    const data = bytes.subarray(offset + 8, offset + 8 + size);
    if (kind === 'IHDR') {
      width = data.readUInt32BE(0); height = data.readUInt32BE(4);
      assert.equal(data[8], 8);
      channels = data[9] === 6 ? 4 : data[9] === 2 ? 3 : 0;
      assert(channels, `Unsupported screenshot PNG type ${data[9]}`);
      assert.equal(data[12], 0);
    }
    if (kind === 'IDAT') chunks.push(data);
    offset += size + 12;
  }
  const raw = inflateSync(Buffer.concat(chunks));
  const stride = width * channels;
  let previous = Buffer.alloc(stride);
  const colors = new Map();
  let boardGreen = 0, tileLight = 0, pixels = 0;
  const paeth = (a, b, c) => {
    const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
    return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
  };
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const row = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let x = 0; x < stride; x++) {
      const left = x >= channels ? row[x - channels] : 0;
      const up = previous[x], diagonal = x >= channels ? previous[x - channels] : 0;
      const predictor = [0, left, up, Math.floor((left + up) / 2), paeth(left, up, diagonal)][filter];
      assert.notEqual(predictor, undefined);
      row[x] = (row[x] + predictor) & 255;
    }
    for (let x = 0; x < width; x++) {
      const index = x * channels, r = row[index], g = row[index + 1], b = row[index + 2];
      const key = `${r >> 3},${g >> 3},${b >> 3}`;
      colors.set(key, (colors.get(key) ?? 0) + 1);
      if (g > r * 1.4 && g > b * 1.1 && g > 35) boardGreen++;
      if (r > 145 && g > 145 && b > 120) tileLight++;
      pixels++;
    }
    previous = row;
  }
  return {
    width, height, pixels, quantizedColors: colors.size,
    largestColorFraction: Math.max(...colors.values()) / pixels,
    boardGreenFraction: boardGreen / pixels, tileLightFraction: tileLight / pixels,
    sha256: createHash('sha256').update(bytes).digest('hex'),
  };
}

async function snapshot(target) {
  return target.evaluate(() => {
    const game = window.game, renderer = game?.renderer;
    return {
      state: renderer?.state,
      bodyRendererReady: document.body.getAttribute('data-unity-renderer-ready'),
      bodySceneReady: document.body.getAttribute('data-scene-shell-ready'),
      canvas: renderer ? { width: renderer.canvas.width, height: renderer.canvas.height } : null,
      viewport: renderer?.viewport, lastRendered: renderer?.lastRendered,
      frame: game?.adapter?.latest, layout: game?.adapter?.layout,
      client: game ? {
        connected: game.client.connected(), mode: game.client.connectionMode,
        seat: game.client.seat, gameId: game.client.lastGameId, viewer: game.client.viewerAuthority,
      } : null,
      worldConditions: game?.world.conditions,
      bridge: window.__playerProbe?.bridge,
      ready: window.__playerProbe?.ready,
      actions: window.__playerProbe?.actions,
      results: window.__playerProbe?.results,
      duplicateReceipts: window.__playerProbe?.duplicateReceipts,
      clearReasons: window.__playerProbe?.clearReasons,
      actionTimes: window.__playerProbe?.actionTimes,
      dragFrameEvidence: window.__playerProbe?.dragFrameEvidence,
      actionContextEvidence: window.__playerProbe?.actionContextEvidence,
      inputEvents: window.__playerProbe?.inputEvents,
      csp: window.__playerProbe?.csp,
      published: window.__playerProbe?.frames?.slice(-2),
      statusText: document.getElementById('unity-status')?.innerText,
    };
  });
}

async function pageFor(viewport, dpr = 1, mobile = false) {
  const context = await browser.newContext({
    viewport, deviceScaleFactor: dpr,
    ...(mobile ? { isMobile: true, hasTouch: true } : {}),
    serviceWorkers: 'block',
  });
  contexts.push(context);
  await context.route('**/*', route => new URL(route.request().url()).origin === origin
    ? route.continue() : route.abort());
  await context.addInitScript(() => {
    const probe = window.__playerProbe = {
      bridge: [], csp: [], frames: [], layouts: {}, actions: [], results: [], ready: null,
      rendered: [], renderedKeys: {}, duplicateReceipts: [], clearReasons: [],
      actionTimes: [], inputEvents: [],
      dragEntityIds: [], dragFrameEvidence: [], actionContextEvidence: [],
    };
    for (const type of ['mousedown', 'mouseup', 'touchstart', 'touchend', 'keydown', 'keyup']) {
      document.addEventListener(type, event => {
        probe.inputEvents.push({ type, at: performance.now(), target: event.target?.id ?? '',
          key: event.key ?? null });
        if (probe.inputEvents.length > 40) probe.inputEvents.shift();
      }, { passive: true, capture: true });
    }
    let game;
    Object.defineProperty(window, 'game', {
      configurable: true,
      get: () => game,
      set: value => {
        game = value;
        if (!value?.renderer) return;
        const actionResult = value.renderer.actionResult.bind(value.renderer);
        value.renderer.actionResult = result => {
          probe.results.push(JSON.parse(JSON.stringify(result)));
          return actionResult(result);
        };
        const clear = value.renderer.clear.bind(value.renderer);
        value.renderer.clear = (sessionId, reason) => {
          probe.clearReasons.push({ sessionId, reason });
          return clear(sessionId, reason);
        };
        const publish = value.renderer.publish.bind(value.renderer);
        value.renderer.publish = (layout, frame) => {
          probe.frames.push(JSON.parse(JSON.stringify(frame)));
          if (probe.frames.length > 4) probe.frames.shift();
          probe.layouts[`${layout.sessionId}:${layout.layoutRevision}`] = JSON.parse(JSON.stringify(layout));
          if (probe.dragEntityIds.length && probe.dragFrameEvidence.length < 24) {
            probe.dragFrameEvidence.push({
              frameId: frame.frameId, sessionId: frame.sessionId,
              entities: frame.entities.filter(entity => probe.dragEntityIds.includes(entity.entityId))
                .map(entity => ({ entityId: entity.entityId, slotName: entity.slotName,
                  display: entity.display, interaction: entity.interaction })),
            });
          }
          return publish(layout, frame);
        };
      },
    });
    let bridge;
    Object.defineProperty(window, 'MahjongUnityBridge', {
      configurable: true,
      get: () => bridge,
      set: value => {
        bridge = value;
        if (!value) return;
        const emit = value.emit.bind(value);
        value.emit = json => {
          try {
            const message = JSON.parse(json);
            probe.bridge.push(message);
            if (probe.bridge.length > 400) probe.bridge.shift();
            if (message.type === 'ready') probe.ready = message;
            if (message.type === 'action') {
              probe.actions.push(message);
              probe.actionTimes.push({ actionId: message.actionId, kind: message.intent.kind, at: performance.now() });
              if (message.intent.kind === 'beginDrag') probe.dragEntityIds = [...message.intent.entityIds];
              if (['beginDrag', 'dragToSlot', 'endDrag'].includes(message.intent.kind)
                  && probe.actionContextEvidence.length < 24) {
                const frame = probe.frames.find(frame => frame.frameId === message.contextFrameId);
                probe.actionContextEvidence.push({
                  actionId: message.actionId, kind: message.intent.kind, contextFrameId: message.contextFrameId,
                  contextEntities: frame?.entities.filter(entity => probe.dragEntityIds.includes(entity.entityId))
                    .map(entity => ({ entityId: entity.entityId, slotName: entity.slotName,
                      display: entity.display, interaction: entity.interaction })) ?? [],
                  latestInput: probe.inputEvents.at(-1) ?? null,
                });
              }
            }
            if (message.type === 'frameRendered') {
              const key = `${message.sessionId}:${message.frameId}:${message.layoutRevision}`;
              if (probe.renderedKeys[key]) probe.duplicateReceipts.push(message);
              probe.renderedKeys[key] = true;
              probe.rendered.push(message);
              if (probe.rendered.length > 500) probe.rendered.shift();
            }
          } catch {}
          return emit(json);
        };
      },
    });
    document.addEventListener('securitypolicyviolation', event => probe.csp.push({
      effectiveDirective: event.effectiveDirective, blockedURI: event.blockedURI,
      disposition: event.disposition,
    }));
  });
  const target = await context.newPage();
  target.on('websocket', socket => {
    if (new URL(socket.url()).pathname !== '/autotable/ws') return;
    const url = new URL(socket.url());
    const connection = {
      path: '/autotable/ws',
      query: Object.fromEntries([...url.searchParams].filter(([key]) =>
        ['gameId', 'variant', 'seat', 'botCount', 'dealMode', 'seed', 'join'].includes(key))),
      sent: [], received: [], closed: false,
    };
    wire.push(connection);
    const observe = (list, event) => {
      try { list.push(JSON.parse(event.payload.toString())); }
      catch { list.push({ invalidJson: true }); }
    };
    socket.on('framesent', event => observe(connection.sent, event));
    socket.on('framereceived', event => observe(connection.received, event));
    socket.on('close', () => { connection.closed = true; });
  });
  target.setDefaultTimeout(20000);
  target.setDefaultNavigationTimeout(30000);
  target.on('pageerror', error => receipt.pageErrors.push({ name: error.name, message: error.message.slice(0, 1500) }));
  target.on('console', message => {
    if (message.type() === 'error') receipt.consoleErrors.push(message.text().slice(0, 1500));
    receipt.consoleMessages.push({ type: message.type(), message: message.text().slice(0, 1500) });
    if (receipt.consoleMessages.length > 100) receipt.consoleMessages.shift();
  });
  target.on('response', response => {
    if (new URL(response.url()).pathname.startsWith('/autotable/unity/')) receipt.responses.push({
      path: new URL(response.url()).pathname, status: response.status(),
      contentType: response.headers()['content-type'], cache: response.headers()['cache-control'],
    });
  });
  return target;
}

async function ready(target) {
  await target.waitForFunction(() => ['ready', 'failed', 'unsupported', 'unavailable'].includes(window.game?.renderer.state),
    null, { timeout: 90000 });
  const state = await snapshot(target);
  assert.equal(state.state, 'ready', JSON.stringify({
    state: state.state, status: state.statusText, bridge: state.bridge, csp: state.csp,
  }));
  await target.waitForFunction(() => window.game?.adapter?.latest?.session.snapshotReady
    && window.game.adapter.latest.entities.length > 0 && window.game.renderer.lastRendered > 0);
  await target.waitForTimeout(400);
  return snapshot(target);
}

async function capture(target, name) {
  const bytes = await target.locator('#unity-canvas').screenshot({ path: `${probeRoot}/screenshots/${name}.png` });
  const pixels = pngPixels(bytes);
  assert(pixels.quantizedColors > 12, JSON.stringify(pixels));
  assert(pixels.largestColorFraction < 0.95, JSON.stringify(pixels));
  assert(pixels.boardGreenFraction > 0.01, JSON.stringify(pixels));
  assert(pixels.tileLightFraction > 0.001, JSON.stringify(pixels));
  return { screenshot: `screenshots/${name}.png`, ...pixels };
}

async function recordCaseFailure(target, name, error) {
  const state = await snapshot(target);
  fs.writeFileSync(`${probeRoot}/${name}-failure-state.json`, JSON.stringify(state, null, 2) + '\n');
  await target.screenshot({ path: `${probeRoot}/screenshots/${name}-failure.png`, fullPage: true });
  receipt.caseFailures.push({
    name, error: { name: error.name, message: error.message.slice(0, 4000), stack: error.stack?.slice(0, 4000) },
    state: state.state, statusText: state.statusText, client: state.client,
    worldConditions: state.worldConditions, bridge: state.bridge, csp: state.csp,
    lastPublishedPickup: state.published?.at(-1)?.pickup,
    lastPublishedSelectableNonHands: state.published?.at(-1)?.entities
      .filter(entity => entity.interaction.canSelect && entity.group !== 'hand')
      .map(entity => ({ entityId: entity.entityId, slotName: entity.slotName, group: entity.group,
        seat: entity.seat, appearance: entity.appearance, interaction: entity.interaction })),
    evidence: `${name}-failure-state.json`, screenshot: `screenshots/${name}-failure.png`,
  });
}

async function manualMeasurements(target, prefix) {
  for (const viewport of [{ width: 390, height: 844 }, { width: 844, height: 390 }, { width: 1280, height: 800 }]) {
    const previous = await snapshot(target);
    await target.setViewportSize(viewport);
    await target.waitForFunction(({ previous, viewport }) => {
      const renderer = window.game.renderer;
      return renderer.state === 'ready' && renderer.lastRendered > previous.lastRendered
        && renderer.viewport.resizeRevision > previous.viewport.resizeRevision
        && renderer.viewport.widthCssPx === viewport.width && renderer.viewport.heightCssPx === viewport.height;
    }, { previous, viewport });
    await target.waitForTimeout(250);
    const current = await snapshot(target);
    assert.equal(current.viewport.renderPixelRatio, Math.min(current.viewport.devicePixelRatio, 2));
    assert.equal(current.canvas.width, current.viewport.widthPx);
    assert.equal(current.canvas.height, current.viewport.heightPx);
    assert.equal(current.viewport.widthPx, Math.round(current.viewport.widthCssPx * current.viewport.renderPixelRatio));
    assert.equal(current.viewport.heightPx, Math.round(current.viewport.heightCssPx * current.viewport.renderPixelRatio));
    receipt.cases.push({
      name: `${prefix}-${viewport.width}x${viewport.height}`, ...rendererSummary(current),
      pixels: await capture(target, `${prefix}-${viewport.width}x${viewport.height}`),
    });
  }
  const before = await snapshot(target);
  await target.locator('#unity-canvas').focus();
  await target.keyboard.press('p');
  await target.waitForFunction(previous => window.game.renderer.state === 'ready'
    && window.game.renderer.lastRendered > previous.lastRendered
    && window.game.adapter.latest.presentation.viewMode !== previous.frame.presentation.viewMode, before);
  await target.waitForTimeout(250);
  const after = await snapshot(target);
  const pixels = await capture(target, `${prefix}-camera-toggle`);
  assert.notEqual(pixels.sha256, receipt.cases.at(-1).pixels.sha256);
  receipt.cases.push({ name: `${prefix}-actual-camera-control`, ...rendererSummary(after), pixels });
  await target.getByRole('button', { name: 'More', exact: true }).click();
  const beforeLayout = await snapshot(target);
  await target.locator('#renderer-board-layout').selectOption('rectangular');
  await target.locator('#renderer-hand-layout').selectOption('separate');
  await target.waitForFunction(previous => window.game.renderer.state === 'ready'
    && window.game.renderer.lastRendered > previous.lastRendered
    && window.game.adapter.latest.presentation.boardLayout === 'rectangular'
    && window.game.adapter.latest.presentation.handLayout === 'separate', beforeLayout);
  await target.keyboard.press('Escape');
  await target.mouse.click(30, 200, { delay: 55 });
  await target.waitForTimeout(250);
  const layout = await snapshot(target);
  receipt.cases.push({
    name: `${prefix}-actual-rectangular-separate-controls`, ...rendererSummary(layout),
    pixels: await capture(target, `${prefix}-rectangular-separate`),
  });
}

function rendererSummary(state) {
  const frame = state.frame;
  return {
    state: state.state, markers: [state.bodyRendererReady, state.bodySceneReady],
    instanceId: frame.instanceId, sessionId: frame.sessionId, frameId: state.lastRendered,
    layoutRevision: state.layout.layoutRevision, viewport: state.viewport,
    realReady: state.ready ?? state.bridge.find(message => message.type === 'ready'),
    realFrameRenderedCount: state.bridge.filter(message => message.type === 'frameRendered').length,
    layout: { gameType: state.layout.gameType, activeSeats: state.layout.activeSeats,
      slots: state.layout.slots.length, groups: [...new Set(state.layout.slots.map(slot => slot.group))] },
    entities: frame.entities.length, localHandCount: frame.localHand.entityIds.length,
    session: frame.session, presentation: frame.presentation, turn: frame.turn, pickup: frame.pickup, dice: frame.dice,
  };
}

async function launchPlayerBrowser() {
  fs.mkdirSync(`${probeRoot}/screenshots`, { recursive: true });
  browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] });
  receipt.browserVersion = browser.version();
  receipt.browserArgs = ['--no-sandbox'];
}

async function finishPlayerBrowser() {
  for (const context of contexts) await context.close();
  if (browser) await browser.close();
  fs.mkdirSync(`${probeRoot}/wire`, { recursive: true });
  fs.writeFileSync(`${probeRoot}/wire/native-ws.json`, JSON.stringify(wire, null, 2) + '\n');
  receipt.nativeWire = {
    file: 'wire/native-ws.json', connections: wire.length,
    sentMessages: wire.reduce((total, connection) => total + connection.sent.length, 0),
    receivedMessages: wire.reduce((total, connection) => total + connection.received.length, 0),
    closedConnections: wire.filter(connection => connection.closed).length,
  };
  fs.writeFileSync(`${probeRoot}/browser-result.json`, JSON.stringify(receipt, null, 2) + '\n');
  process.stdout.write(JSON.stringify(receipt, null, 2) + '\n');
}

export {
  base, origin, probeRoot, receipt, wire, pageFor, ready, snapshot, capture, recordCaseFailure, rendererSummary,
  manualMeasurements, launchPlayerBrowser, finishPlayerBrowser,
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
try {
  await launchPlayerBrowser();
  receipt.stage = 'actual-generated-player-startup';
  if (!layoutOnly) {
  page = await pageFor({ width: 1280, height: 800 });
  const room = `migration-player-${Date.now()}`;
  const query = new URLSearchParams({
    gameId: room, createGame: room, variant: 'changsha', seat: '0', botCount: '3',
    dealMode: 'auto', seed: '20261010', unityLocalBuild: '1',
  });
  receipt.qualificationUrl = `${base}/autotable/?${query}`;
  await page.goto(receipt.qualificationUrl, { waitUntil: 'domcontentloaded' });
  try {
    const state = await ready(page);
    assert.equal(state.bodyRendererReady, 'true');
    assert.equal(state.bodySceneReady, 'true');
    assert.equal(state.client.gameId, room);
    assert.equal(state.client.mode, 'changsha');
    assert.equal(state.frame.session.actorSeat, 0);
    const realReady = state.bridge.find(message => message.type === 'ready');
    assert(realReady && realReady.buildId.startsWith('renderer-'));
    const rendered = state.bridge.filter(message => message.type === 'frameRendered');
    assert(rendered.length > 0);
    assert(rendered.some(message => message.frameId === state.lastRendered
      && message.sessionId === state.frame.sessionId && message.layoutRevision === state.layout.layoutRevision));
    const pixels = await capture(page, 'desktop-startup');
    receipt.cases.push({ name: 'actual-changsha-csharp-rendered-player', ...rendererSummary(state), pixels });
    receipt.cspViolations.push(...state.csp);
  } catch (error) {
    await recordCaseFailure(page, 'changsha-auto-startup', error);
  }
  }
  receipt.stage = 'actual-generated-changsha-manual-player';
  page = await pageFor({ width: 1280, height: 800 });
  const manualRoom = `migration-player-manual-${Date.now()}`;
  const manualParameters = new URLSearchParams({
    gameId: manualRoom, createGame: manualRoom, variant: 'changsha', seat: '0', botCount: '3',
    dealMode: 'manual', seed: '20261010', unityLocalBuild: '1',
  });
  await page.goto(`${base}/autotable/?${manualParameters}`, { waitUntil: 'domcontentloaded' });
  try {
    const state = await ready(page);
    receipt.cases.push({
      name: 'actual-changsha-manual-csharp-rendered-player', ...rendererSummary(state),
      pixels: await capture(page, 'changsha-manual-desktop'),
    });
    if (layoutOnly) {
      await manualMeasurements(page, 'manual-dpr1');
      for (const dpr of [2, 3]) {
        const mobile = await pageFor({ width: 390, height: 844 }, dpr, true);
        const id = `migration-manual-dpr${dpr}-${Date.now()}`;
        const parameters = new URLSearchParams({
          gameId: id, createGame: id, variant: 'changsha', seat: '0', botCount: '3',
          dealMode: 'manual', seed: '20261010', unityLocalBuild: '1',
        });
        await mobile.goto(`${base}/autotable/?${parameters}`, { waitUntil: 'domcontentloaded' });
        const state = await ready(mobile);
        assert.equal(state.viewport.devicePixelRatio, dpr);
        assert.equal(state.viewport.renderPixelRatio, 2);
        receipt.cases.push({
          name: `actual-mobile-emulated-dpr${dpr}`, ...rendererSummary(state),
          pixels: await capture(mobile, `manual-mobile-dpr${dpr}`),
        });
      }
      const spectator = await pageFor({ width: 1280, height: 800 });
      const spectatorParameters = new URLSearchParams({
        gameId: manualRoom, variant: 'changsha', seat: '-1', join: '1', unityLocalBuild: '1',
      });
      await spectator.goto(`${base}/autotable/?${spectatorParameters}`, { waitUntil: 'domcontentloaded' });
      const initial = await ready(spectator);
      assert.equal(initial.frame.session.actorSeat, null);
      assert(initial.frame.entities.every(entity => entity.appearance.faceIndex === null));
      const firstPixels = await capture(spectator, 'manual-spectator-initial');
      await spectator.getByTestId('spectator-follow-seat-2').click();
      await spectator.waitForFunction(previous => window.game.renderer.lastRendered > previous.lastRendered
        && window.game.adapter.latest.session.viewSeat === 2, initial);
      await spectator.waitForTimeout(250);
      const followed = await snapshot(spectator);
      assert.equal(followed.frame.session.actorSeat, null);
      assert.equal(followed.client.seat, null);
      assert(followed.frame.entities.every(entity => entity.appearance.faceIndex === null
        && Object.values(entity.interaction).every(value => value === false)));
      const followedPixels = await capture(spectator, 'manual-spectator-followed');
      assert.notEqual(firstPixels.sha256, followedPixels.sha256);
      receipt.cases.push({
        name: 'actual-spectator-follow-initial-wall-privacy-only', ...rendererSummary(followed),
        actualActorUnchanged: true, initialWallFacesAllHidden: true, firstPixels, followedPixels,
        dealtHandPrivacyQualified: false,
      });
    }
    if (await page.locator('#roll-dice').isVisible()) {
      await page.locator('#roll-dice').click();
      await page.waitForFunction(() => window.game?.adapter?.latest?.pickup?.canTake
        || window.game?.renderer.state === 'failed');
      const rolled = await snapshot(page);
      receipt.cases.push({
        name: 'actual-manual-dice-server-roundtrip', buttonClicked: true,
        phase: rolled.frame?.turn, pickup: rolled.frame?.pickup, state: rolled.state,
      });
      assert.equal(rolled.state, 'ready', rolled.statusText);
    }
  } catch (error) {
    await recordCaseFailure(page, 'changsha-manual-startup', error);
  }
  for (const variant of layoutOnly ? [] : ['FOUR_PLAYER', 'THREE_PLAYER', 'BAMBOO', 'MINEFIELD']) {
    receipt.stage = `actual-generated-${variant}-player`;
    page = await pageFor({ width: 1280, height: 800 });
    const roomId = `migration-${variant.toLowerCase()}-${Date.now()}`;
    const parameters = new URLSearchParams({
      gameId: roomId, createGame: roomId, variant: variant.toLowerCase(), seat: '0',
      dealMode: 'auto', botCount: '0', seed: '20261010', unityLocalBuild: '1',
    });
    await page.goto(`${base}/autotable/?${parameters}`, { waitUntil: 'domcontentloaded' });
    try {
      const state = await ready(page);
      assert.equal(state.layout.gameType, variant);
      const pixels = await capture(page, `${variant.toLowerCase()}-desktop`);
      receipt.cases.push({ name: `actual-${variant}-csharp-rendered-player`, ...rendererSummary(state), pixels });
      for (const size of [{ width: 390, height: 844 }, { width: 844, height: 390 }]) {
        const previous = state.lastRendered;
        await page.setViewportSize(size);
        await page.waitForFunction(previous => window.game.renderer.lastRendered > previous
          && window.game.renderer.state === 'ready', previous);
        await page.waitForTimeout(400);
        const resized = await snapshot(page);
        assert.equal(resized.viewport.widthPx, Math.round(resized.viewport.widthCssPx * resized.viewport.renderPixelRatio));
        assert.equal(resized.viewport.heightPx, Math.round(resized.viewport.heightCssPx * resized.viewport.renderPixelRatio));
        assert.equal(resized.viewport.handBandCssPx > 0, size.width < 768);
        receipt.cases.push({
          name: `actual-${variant}-${size.width}x${size.height}`,
          ...rendererSummary(resized), pixels: await capture(page, `${variant.toLowerCase()}-${size.width}x${size.height}`),
        });
      }
    } catch (error) {
      await recordCaseFailure(page, variant.toLowerCase() + '-startup', error);
    }
  }
  assert.equal(receipt.pageErrors.length, 0, JSON.stringify(receipt.pageErrors));
  assert.equal(receipt.cspViolations.length, 0, JSON.stringify(receipt.cspViolations));
  assert.equal(receipt.caseFailures.length, 0, JSON.stringify(receipt.caseFailures.map(failure => ({
    name: failure.name, statusText: failure.statusText, error: failure.error,
  }))));
  receipt.stage = 'completed';
  receipt.status = 'passed';
} catch (error) {
  receipt.status = 'failed';
  receipt.failure = { stage: receipt.stage, name: error.name, message: error.message.slice(0, 6000) };
  if (page) {
    try {
      receipt.actualFailureState = await snapshot(page);
      await page.screenshot({ path: '/probe/screenshots/failure-page.png', fullPage: true });
      receipt.failureScreenshot = 'screenshots/failure-page.png';
    } catch (captureError) {
      receipt.captureError = captureError.message.slice(0, 300);
    }
  }
  process.exitCode = 1;
} finally {
  await finishPlayerBrowser();
}
}

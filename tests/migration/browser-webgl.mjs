import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { inflateSync } from 'node:zlib';

const root = process.cwd();
const require = createRequire(path.join(root, 'src/frontend/autotable-src/package.json'));
const { chromium } = require('@playwright/test');
const origin = process.argv[2] ?? 'http://127.0.0.1:5087';
assert.equal(new URL(origin).hostname, '127.0.0.1');
assert.notEqual(new URL(origin).port, '5086', 'Interactive checks may only write to a disposable fixture server.');
const artifactName = process.env.MAHJONG_ARTIFACT_NAME ?? 'webgl-only';
assert.match(artifactName, /^[a-z0-9-]+$/);
const work = path.join(root, 'artifacts', artifactName);
const output = path.join(work, 'browser');
fs.mkdirSync(path.join(output, 'screenshots'), { recursive: true });
const receipt = {
  status: 'running',
  engine: 'webgl2', scope: 'Default WebGL-only actual gameplay/style/engine-removal checks; not exhaustive physical-device qualification',
  origin, browser: 'Host Google Chrome, headless ANGLE SwiftShader software GPU; not a physical phone',
  cases: [], errors: [], consoleErrors: [], actions: [], results: [], rendered: [], wire: [], requests: [],
  selectedCase: process.env.MAHJONG_WEBGL_CASE ?? 'all',
  source: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
  sourceDirty: execFileSync('git', ['status', '--porcelain'], { encoding: 'utf8' }).trim().length > 0,
};
const browser = await chromium.launch({
  executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', headless: true,
  args: ['--no-sandbox', '--disable-dev-shm-usage', '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
    '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1'],
});
async function newPage(mobile = false, dimensions = mobile ? { width: 390, height: 844 } : { width: 1280, height: 800 }) {
  const context = await browser.newContext({ viewport: dimensions, deviceScaleFactor: mobile ? 2 : 1,
    ...(mobile ? { isMobile: true, hasTouch: true } : {}), serviceWorkers: 'block' });
  await context.addInitScript(() => {
    window.__comparison = { actions: [], results: [], rendered: [], usableAt: null, raf: [], published: {}, committedFrame: null };
    for (const [event, field, key] of [
      ['mahjong:renderer-action', 'actions', 'action'],
      ['mahjong:renderer-action-result', 'results', 'result'],
      ['mahjong:renderer-frame', 'rendered', 'receipt'],
    ]) window.addEventListener(event, value => {
      window.__comparison[field].push(value.detail[key]);
      if (window.__comparison[field].length > 500) window.__comparison[field].shift();
    });
    let game;
    Object.defineProperty(window, 'game', {
      configurable: true, get: () => game,
      set(value) {
        game = value;
        const publish = value.renderer.publish.bind(value.renderer);
        value.renderer.publish = (layout, frame) => {
          const frames = window.__comparison.published;
          frames[`${frame.sessionId}:${frame.frameId}`] = frame;
          const keys = Object.keys(frames);
          if (keys.length > 150) delete frames[keys[0]];
          return publish(layout, frame);
        };
      },
    });
    let previous;
    const sample = now => {
      const game = window.game, frame = game?.renderer?.scene?.committed?.frame ?? window.__comparison.committedFrame;
      if (game?.renderer?.state === 'ready' && frame?.session.snapshotReady && frame.entities.length > 50) {
        window.__comparison.usableAt ??= performance.now();
        if (previous !== undefined) window.__comparison.raf.push(now - previous);
        if (window.__comparison.raf.length > 300) window.__comparison.raf.shift();
        previous = now;
      }
      requestAnimationFrame(sample);
    };
    requestAnimationFrame(sample);
  });
  const page = await context.newPage();
  page.on('request', request => receipt.requests.push({ origin: new URL(request.url()).origin, path: new URL(request.url()).pathname }));
  page.on('pageerror', error => receipt.errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') receipt.consoleErrors.push(message.text()); });
  page.on('websocket', socket => {
    socket.on('framesent', frame => {
      try { receipt.wire.push({ direction: 'sent', value: JSON.parse(String(frame.payload)) }); } catch {}
    });
  });
  return { page, context };
}
async function table(page, { variant = 'changsha', dealMode = 'auto' } = {}) {
  const room = `webgl-${Date.now()}-${Math.random().toString(16).slice(2, 7)}`;
  await page.goto(`${origin}/autotable/?${new URLSearchParams({
    gameId: room, createGame: room, variant, seat: '0',
    botCount: variant === 'changsha' ? '3' : '0', dealMode, seed: '20261010',
  })}`, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => ['failed', 'unavailable', 'unsupported'].includes(window.game?.renderer?.state)
    || window.game?.renderer?.state === 'ready' && window.game.adapter.latest?.session.snapshotReady, null, { timeout: 60000 });
  assert.equal((await snapshot(page)).state, 'ready');
  return room;
}
async function snapshot(page) {
  return page.evaluate(() => {
    const renderer = window.game.renderer;
    return {
      engine: renderer.engine, state: renderer.state, status: document.getElementById('renderer-status')?.textContent,
      frame: renderer.scene?.committed?.frame ?? window.game.adapter.latest, viewport: renderer.viewport,
      lastRendered: renderer.lastRendered, device: renderer.scene?.device, metrics: renderer.metrics,
      style: renderer.scene?.style,
      probe: { actions: window.__comparison.actions, results: window.__comparison.results,
        rendered: window.__comparison.rendered, usableAt: window.__comparison.usableAt, raf: window.__comparison.raf },
      memory: performance.memory ? { usedJsHeap: performance.memory.usedJSHeapSize,
        totalJsHeap: performance.memory.totalJSHeapSize, jsHeapLimit: performance.memory.jsHeapSizeLimit } : null,
    };
  });
}
async function save(page, name, extra = {}) {
  const state = await snapshot(page);
  assert.equal(state.state, 'ready', state.status);
  for (const entity of state.frame.entities) {
    if (entity.group === 'wall' || entity.group === 'hand' && entity.seat !== state.frame.session.actorSeat) {
      assert.equal(entity.appearance.faceIndex, null);
      assert.equal(entity.appearance.showFace, false);
    }
  }
  fs.writeFileSync(path.join(output, `${name}-state.json`), JSON.stringify(state, null, 2));
  await page.screenshot({ path: path.join(output, `screenshots/${name}.png`), fullPage: true });
  const entry = { name, engine: state.engine, entities: state.frame.entities.length,
    hand: state.frame.localHand.entityIds.length, presentation: state.frame.presentation, device: state.device, ...extra };
  receipt.cases.push(entry);
  fs.writeFileSync(path.join(output, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n');
  return state;
}
async function point(page, id) {
  const at = await page.evaluate(id => window.game.renderer.scene.project(id), id);
  assert(at, `Rendered target ${id} is not projectable`);
  const box = await page.locator('#webgl-canvas').boundingBox();
  const result = { x: box.x + at.x, y: box.y + at.y };
  assert.equal(await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.id, result), 'webgl-canvas');
  return result;
}
const commands = kind => receipt.wire.flatMap(message => message.value.entries ?? []).filter(entry => entry[0] === kind);
async function ownTurn(page) {
  for (let attempt = 0; attempt < 8; attempt++) {
    await page.waitForFunction(() => window.game.renderer.state === 'failed'
      || window.game.adapter.latest?.entities.some(entity => entity.interaction.canDiscard)
      || document.querySelector('.ferro-claim-pass:not(:disabled)')?.getClientRects().length);
    const pass = page.locator('.ferro-claim-pass');
    if (await pass.isVisible() && await pass.isEnabled()) { await pass.click(); await page.waitForTimeout(200); continue; }
    await page.waitForTimeout(150);
    return snapshot(page);
  }
  throw Error('Fixture did not return an actionable discard turn.');
}
async function discard(page, touch = false) {
  const before = await ownTurn(page);
  const entity = before.frame.entities.find(entity => entity.interaction.canDiscard && entity.appearance.showFace);
  assert(entity);
  const at = await point(page, entity.entityId), count = commands('discard').length;
  const start = before.probe.actions.length;
  if (touch) await page.touchscreen.tap(at.x, at.y);
  else await page.mouse.dblclick(at.x, at.y, { delay: 20 });
  await page.waitForFunction(({ start, id }) => window.__comparison.actions.slice(start)
    .some(action => action.intent.kind === 'discard' && action.intent.entityId === id), { start, id: entity.entityId });
  await page.waitForFunction(id => !window.game.adapter.latest.localHand.entityIds.includes(id), entity.entityId);
  await page.waitForTimeout(300);
  const after = await snapshot(page);
  assert.equal(commands('discard').length, count + 1, 'Exactly one real server discard command must follow this gesture.');
  const action = after.probe.actions.slice(start).filter(action => action.intent.kind === 'discard');
  assert.equal(action.length, 1);
  assert(after.probe.results.some(result => result.actionId === action[0].actionId && result.status === 'submitted'));
  assert(!after.frame.localHand.entityIds.includes(entity.entityId));
  receipt.actions.push({ operation: touch ? 'ordinary-touch-discard' : 'mouse-double-click-discard', action: action[0],
    wire: commands('discard').at(-1), serverRemovedFromHand: true });
}
async function desktop() {
  const { page, context } = await newPage();
  await table(page);
  const first = await save(page, 'desktop-perspective');
  assert.equal(first.engine, 'webgl2'); assert(first.probe.rendered.length > 0);
  await discard(page);
  await save(page, 'desktop-progressed');
  await page.locator('#webgl-canvas').focus();
  const before = (await snapshot(page)).frame.presentation.camera?.yaw ?? 0;
  await page.keyboard.press('ArrowLeft');
  await page.waitForFunction(before => (window.game.renderer.scene.committed?.frame.presentation.camera?.yaw ?? 0) !== before, before);
  await save(page, 'desktop-camera');
  const pan = await page.evaluate(() => window.game.renderer.scene.pan.slice());
  const board = await page.evaluate(() => window.game.renderer.scene.committed.plan.board);
  await page.keyboard.down('Shift');
  await page.mouse.move(board.x + board.width / 2, board.y + board.height / 2);
  await page.mouse.down({ button: 'right' });
  await page.mouse.move(board.x + board.width / 2 + 35, board.y + board.height / 2 + 15, { steps: 4 });
  await page.mouse.up({ button: 'right' }); await page.keyboard.up('Shift');
  await page.waitForTimeout(200);
  assert.notDeepEqual(await page.evaluate(() => window.game.renderer.scene.pan), pan);
  await page.locator('#webgl-canvas').focus(); await page.keyboard.press('Home');
  await page.waitForTimeout(200);
  assert.deepEqual(await page.evaluate(() => window.game.renderer.scene.pan), [0, 0]);
  await context.close();
}
async function phone() {
  const { page, context } = await newPage(true);
  await table(page);
  const before = await save(page, 'phone-portrait-flat');
  assert.equal(before.frame.presentation.viewMode, 'flat');
  assert.equal(before.frame.presentation.handLayout, 'separate');
  const hand = await page.evaluate(() => window.game.renderer.scene.committed.plan.hand);
  const clip = { x: hand.x + 5, y: hand.y + 5, width: hand.width - 10, height: hand.height - 10 };
  const pixels = await page.screenshot({ clip });
  const cdp = await context.newCDPSession(page);
  const board = await page.evaluate(() => window.game.renderer.scene.committed.plan.board);
  const x = board.x + board.width / 2, y = board.y + board.height / 2;
  const start = (await snapshot(page)).probe.actions.length, previous = commands('discard').length;
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ id: 1, x: x - 40, y }, { id: 2, x: x + 40, y }] });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ id: 1, x: x - 65, y: y - 25 }, { id: 2, x: x + 65, y: y + 25 }] });
  await page.waitForTimeout(120);
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await page.waitForTimeout(300);
  const after = await snapshot(page), moved = await page.screenshot({ clip });
  assert.equal(commands('discard').length, previous);
  assert(!after.probe.actions.slice(start).some(action => ['discard', 'takePickup', 'flip', 'rollDice'].includes(action.intent.kind)));
  assert.deepEqual(moved, pixels, 'The independently fixed hand must retain identical actual pixels while the board camera moves.');
  await save(page, 'phone-camera-fixed-hand', { fixedHandPngSha256: createHash('sha256').update(moved).digest('hex') });
  await cdp.send('Emulation.setFocusEmulationEnabled', { enabled: false });
  const other = await context.newPage(); await other.goto('about:blank'); await other.bringToFront();
  await page.waitForFunction(() => document.hidden || !document.hasFocus());
  await page.waitForTimeout(150);
  const paused = await page.evaluate(() => ({ frames: window.game.renderer.metrics.renderedFrames,
    frame: window.game.adapter.latest.frameId, focused: document.hasFocus(), hidden: document.hidden,
    queue: window.game.renderer.queue?.frame.frameId }));
  await page.waitForTimeout(2200);
  const idle = await page.evaluate(() => ({ frames: window.game.renderer.metrics.renderedFrames,
    frame: window.game.adapter.latest.frameId, queue: window.game.renderer.queue?.frame.frameId,
    pending: window.game.renderer.inFlight !== null }));
  assert.equal(idle.frames, paused.frames);
  assert(idle.frame > paused.frame);
  assert.equal(idle.pending, false);
  await other.close(); await page.bringToFront();
  await page.waitForFunction(previous => window.game.renderer.lastRendered > previous, paused.frame);
  receipt.cases.push({ name: 'actual-browser-focus-suspension', paused, idle,
    scope: 'Actual Chrome focus/visibility change, not physical-device or visible-OS-unfocused qualification' });
  await discard(page, true);
  await save(page, 'phone-progressed');
  await page.setViewportSize({ width: 844, height: 390 });
  await page.waitForTimeout(350);
  await save(page, 'phone-landscape');
  await context.close();
}
async function relay() {
  const { page, context } = await newPage();
  await table(page, { variant: 'four_player' });
  await page.locator('.seat-button-0 .take-seat').click();
  await page.waitForFunction(() => window.game.client.seat === 0);
  await page.locator('#toggle-setup').click();
  await page.locator('#points').selectOption('35'); await page.locator('#reset-points').click();
  await page.locator('#toggle-setup').click(); await page.locator('#deal').click();
  await page.waitForFunction(() => window.game.renderer.scene.committed?.frame.entities.some(entity =>
    entity.group === 'hand' && entity.seat === 0 && entity.interaction.canFlip));
  await page.waitForTimeout(150);
  let state = await snapshot(page);
  const tile = state.frame.entities.find(entity => entity.group === 'hand' && entity.seat === 0 && entity.interaction.canFlip);
  const at = await point(page, tile.entityId), start = state.probe.actions.length;
  await page.mouse.click(at.x, at.y, { button: 'right' });
  await page.waitForFunction(({ id, rotation }) => window.game.renderer.scene.committed.frame.entities
    .some(entity => entity.entityId === id && entity.rotationIndex !== rotation), { id: tile.entityId, rotation: tile.rotationIndex });
  state = await snapshot(page);
  assert(state.probe.actions.slice(start).some(action => action.intent.kind === 'flip'));
  const selectedAt = await point(page, tile.entityId);
  await page.mouse.click(selectedAt.x, selectedAt.y); await page.waitForTimeout(100);
  const beforeKeyboard = await snapshot(page), keyboardStart = beforeKeyboard.probe.actions.length;
  const rotation = beforeKeyboard.frame.entities.find(entity => entity.entityId === tile.entityId).rotationIndex;
  await page.locator('#webgl-canvas').focus();
  const yaw = beforeKeyboard.frame.presentation.camera?.yaw ?? 0;
  await page.keyboard.press('ArrowLeft');
  await page.waitForFunction(yaw => (window.game.renderer.scene.committed?.frame.presentation.camera?.yaw ?? 0) !== yaw, yaw);
  assert.equal(await page.evaluate(() => window.game.tableCamera.blocksGameplay), true);
  await page.keyboard.press('f');
  await page.waitForFunction(({ id, rotation }) => window.game.renderer.scene.committed.frame.entities
    .some(entity => entity.entityId === id && entity.rotationIndex !== rotation), { id: tile.entityId, rotation });
  state = await snapshot(page);
  assert.equal(state.probe.actions.slice(keyboardStart).filter(action => action.intent.kind === 'flip').length, 1,
    'A focused F-key must not flip through both the browser and renderer input paths.');
  const stick = state.frame.entities.find(entity => entity.kind === 'STICK' && entity.seat === 0 && entity.interaction.canSelect);
  assert(stick);
  const stickAt = await point(page, stick.entityId);
  const selectionStart = state.probe.actions.length;
  await page.mouse.click(stickAt.x, stickAt.y);
  await page.waitForFunction(({ start, id }) => window.__comparison.actions.slice(start)
    .some(action => action.intent.kind === 'select' && action.intent.entityIds.includes(id)), { start: selectionStart, id: stick.entityId });
  const target = await page.evaluate(() => {
    const game = window.game, frame = game.renderer.scene.committed.frame;
    const slot = game.adapter.layout.slots.find(slot => slot.group === 'discard' && slot.seat === 0
      && !frame.entities.some(entity => entity.slotName === slot.slotName));
    return { name: slot.slotName, ...game.renderer.scene.projectSlot(slot.slotName) };
  });
  const from = await point(page, tile.entityId), dragStart = (await snapshot(page)).probe.actions.length;
  await page.mouse.move(from.x, from.y); await page.mouse.down();
  await page.waitForTimeout(50);
  await page.mouse.move(from.x + 15, from.y, { steps: 2 }); await page.waitForTimeout(70);
  await page.mouse.move(target.x, target.y, { steps: 5 }); await page.waitForTimeout(160);
  await page.mouse.up();
  await page.waitForTimeout(200);
  fs.writeFileSync(path.join(output, 'relay-drag-state.json'), JSON.stringify({ target, state: await snapshot(page) }, null, 2));
  await page.waitForFunction(({ id, slot }) => window.game.adapter.latest.entities.some(entity =>
    entity.entityId === id && entity.slotName === slot), { id: tile.entityId, slot: target.name });
  await page.waitForTimeout(200);
  const after = await snapshot(page), actions = after.probe.actions.slice(dragStart);
  assert(actions.some(action => action.intent.kind === 'beginDrag'));
  assert(actions.some(action => action.intent.kind === 'dragToSlot' && action.intent.slotName === target.name));
  assert(actions.some(action => action.intent.kind === 'endDrag' && action.intent.commit));
  assert(commands('things').some(entry => entry[2]?.slotName === target.name));
  receipt.actions.push({ operation: 'real-relay-right-click-flip-scoring-stick-and-uninterrupted-drag',
    target: target.name, actions, serverSlotReached: true, scoringStickValue: stick.appearance.valueIndex });
  await save(page, 'relay-progressed');
  await context.close();
}
async function manualPickup() {
  const { page, context } = await newPage();
  await table(page, { dealMode: 'manual' });
  await save(page, 'manual-initial');
  const count = commands('pickup').filter(entry => entry[1] === 'rollDice').length;
  await page.locator('#roll-dice').click();
  await page.waitForFunction(() => window.game.renderer.scene.committed?.frame.pickup?.canTake);
  assert.equal(commands('pickup').filter(entry => entry[1] === 'rollDice').length, count + 1);
  const before = await snapshot(page);
  const tile = before.frame.entities.find(entity => entity.interaction.canPickup);
  assert(tile && tile.appearance.faceIndex === null);
  const at = await point(page, tile.entityId), start = before.probe.actions.length;
  const takeCount = commands('pickup').filter(entry => entry[1] === 'take').length;
  await page.mouse.click(at.x, at.y);
  await page.waitForFunction(start => window.__comparison.actions.slice(start).some(action => action.intent.kind === 'takePickup'), start);
  await page.waitForFunction(previous => JSON.stringify(window.game.adapter.latest.pickup) !== previous,
    JSON.stringify(before.frame.pickup));
  assert.equal(commands('pickup').filter(entry => entry[1] === 'take').length, takeCount + 1);
  await page.waitForTimeout(200);
  receipt.actions.push({ operation: 'single-wall-pickup', diceInput: 'retained DOM roll-dice control; not renderer-dice input qualification',
    diceWire: commands('pickup').filter(entry => entry[1] === 'rollDice').at(-1),
    pickupWire: commands('pickup').filter(entry => entry[1] === 'take').at(-1) });
  await save(page, 'manual-pickup-progressed');
  await context.close();
}
function pngRgb(bytes) {
  let width, height, channels;
  const chunks = [];
  for (let offset = 8; offset < bytes.length;) {
    const length = bytes.readUInt32BE(offset), kind = bytes.subarray(offset + 4, offset + 8).toString();
    const data = bytes.subarray(offset + 8, offset + 8 + length);
    if (kind === 'IHDR') {
      width = data.readUInt32BE(0); height = data.readUInt32BE(4);
      assert.equal(data[8], 8);
      channels = data[9] === 6 ? 4 : data[9] === 2 ? 3 : 0;
      assert(channels, 'actual screenshots must be RGB/RGBA PNGs');
    }
    if (kind === 'IDAT') chunks.push(data);
    offset += length + 12;
  }
  const raw = inflateSync(Buffer.concat(chunks)), stride = width * channels;
  const rgb = Buffer.alloc(width * height * 3);
  let previous = Buffer.alloc(stride);
  const paeth = (a, b, c) => {
    const p = a + b - c, x = Math.abs(p - a), y = Math.abs(p - b), z = Math.abs(p - c);
    return x <= y && x <= z ? a : y <= z ? b : c;
  };
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const row = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let x = 0; x < stride; x++) {
      const left = x >= channels ? row[x - channels] : 0, up = previous[x], diagonal = x >= channels ? previous[x - channels] : 0;
      const predictor = [0, left, up, Math.floor((left + up) / 2), paeth(left, up, diagonal)][filter];
      assert.notEqual(predictor, undefined);
      row[x] = (row[x] + predictor) & 255;
    }
    for (let x = 0; x < width; x++) row.copy(rgb, (y * width + x) * 3, x * channels, x * channels + 3);
    previous = row;
  }
  return { width, height, rgb };
}
async function appearance() {
  const desktop = await newPage();
  await table(desktop.page);
  await desktop.page.waitForFunction(() => window.game.renderer.scene.committed?.frame.entities.length > 50);
  await save(desktop.page, 'style-desktop-perspective');
  await desktop.page.locator('#webgl-canvas').focus();
  for (let index = 0; index < 5; index++) await desktop.page.keyboard.press('ArrowDown');
  for (let index = 0; index < 2; index++) await desktop.page.keyboard.press('+');
  await desktop.page.waitForTimeout(300);
  await save(desktop.page, 'style-desktop-angled');
  const closeup = await desktop.page.evaluate(() => {
    const scene = window.game.renderer.scene;
    const points = scene.committed.frame.entities.filter(entity => entity.group === 'wall' && entity.seat === 0)
      .map(entity => scene.project(entity.entityId)).filter(Boolean);
    const xs = points.map(point => point.x), ys = points.map(point => point.y);
    const x = Math.max(0, Math.min(...xs) - 36), y = Math.max(0, Math.min(...ys) - 54);
    return { x, y, width: Math.min(innerWidth - x, Math.max(...xs) - x + 36),
      height: Math.min(innerHeight - y, Math.max(...ys) - y + 74) };
  });
  assert(closeup.width > 40 && closeup.height > 40);
  await desktop.page.screenshot({ path: path.join(output, 'screenshots/style-tiles-closeup.png'), clip: closeup });
  await desktop.context.close();

  const phone = await newPage(true);
  await table(phone.page);
  await phone.page.waitForFunction(() => window.game.renderer.scene.committed?.frame.entities.length > 50);
  await phone.page.waitForTimeout(150);
  const state = await save(phone.page, 'style-phone-flat');
  assert.equal(state.style.riverCount, 4);
  assert(state.style.verticesPerInstance <= 200 && state.style.trianglesPerInstance < 128);
  assert.equal(state.style.frameDrawCalls, 2, 'tile polish retains the table/hand instanced draw-call budget');
  const samples = await phone.page.evaluate(() => {
    const scene = window.game.renderer.scene, commit = scene.committed, picks = scene.picking;
    const project = (matrix, point, rect) => {
      const out = [0, 0, 0, 0];
      for (let row = 0; row < 4; row++) for (let col = 0; col < 4; col++) out[row] += matrix[col * 4 + row] * point[col];
      return { x: rect.x + (out[0] / out[3] + 1) * rect.width / 2,
        y: rect.y + (1 - out[1] / out[3]) * rect.height / 2 };
    };
    const target = commit.frame.entities.find(entity => entity.group === 'hand' && entity.seat === 2 && entity.indexes[0] === 6);
    const index = picks.table.targets.findIndex(item => item.id === target.entityId);
    const model = picks.table.modelData.slice(index * 16, index * 16 + 16), points = [];
    for (const x of [-.5, .5]) for (const y of [-20 / 24, 20 / 24]) for (const z of [-8 / 24, 8 / 24]) {
      const world = [0, 0, 0, 0];
      for (let row = 0; row < 4; row++) for (let col = 0; col < 4; col++) world[row] += model[col * 4 + row] * [x, y, z, 1][col];
      points.push(project(picks.matrices[0], world, commit.plan.board));
    }
    const minX = Math.min(...points.map(p => p.x)), minY = Math.min(...points.map(p => p.y));
    const rivers = [...commit.plan.rivers].map(([seat, box]) => {
      const center = box.getCenter(box.min.clone());
      const at = project(picks.matrices[0], [center.x, -.00375, -center.z, 1], commit.plan.board);
      return { seat, ...at, hit: scene.pick(at.x, at.y, false) };
    });
    return { dpr: devicePixelRatio, rivers,
      culling: { enabled: scene.gl.isEnabled(scene.gl.CULL_FACE), mode: scene.gl.getParameter(scene.gl.CULL_FACE_MODE),
        back: scene.gl.BACK },
      tile: { x: minX, y: minY, width: Math.max(...points.map(p => p.x)) - minX,
        height: Math.max(...points.map(p => p.y)) - minY } };
  });
  assert(samples.tile.width > 10 && samples.tile.height > 10);
  assert(samples.culling.enabled && samples.culling.mode === samples.culling.back,
    'actual styled geometry must cull back faces rather than rasterizing hidden plastic surfaces');
  const tileBytes = await phone.page.screenshot({ clip: samples.tile });
  fs.writeFileSync(path.join(output, 'screenshots/style-back-cap-closeup.png'), tileBytes);
  const tile = pngRgb(tileBytes);
  let ivory = 0, teal = 0;
  for (let i = 0; i < tile.rgb.length; i += 3) {
    const [r, g, b] = tile.rgb.subarray(i, i + 3);
    if (r > 155 && g > 150 && b > 135 && r > b * 1.02) ivory++;
    if (g > r * 1.25 && b > r * 1.25 && b > g * .94 && g > 50) teal++;
  }
  const count = tile.width * tile.height;
  assert(ivory / count > .12, 'actual hidden-tile GPU pixels must show an ivory body/rim, not an all-teal block');
  assert(teal / count > .25, 'actual hidden-tile GPU pixels must retain a substantial muted teal inset');
  const image = pngRgb(await phone.page.screenshot());
  const riverPixels = samples.rivers.map(sample => {
    assert.equal(sample.hit, null, 'a visible discard area must not be a new input target');
    const x = Math.round(sample.x * samples.dpr), y = Math.round(sample.y * samples.dpr);
    const rgb = Array.from(image.rgb.subarray((y * image.width + x) * 3, (y * image.width + x) * 3 + 3));
    assert(rgb.every((value, index) => Math.abs(value - [23, 92, 78][index]) <= 4),
      `actual river ${sample.seat} must render its subtle felt fill: ${rgb}`);
    return { seat: sample.seat, rgb };
  });
  receipt.cases.push({ name: 'actual-style-geometry-cap-and-four-river-pixels', style: state.style,
    ivoryFraction: ivory / count, tealFraction: teal / count, riverPixels,
    culling: samples.culling,
    scope: 'Real populated-game WebGL screenshots and actual picking; no replacement images or personal photo used' });
  await phone.context.close();
}
async function benchmark(mobile = false) {
  const renderer = 'webgl2';
  const { page, context } = await newPage(mobile);
  const network = [], cdp = await context.newCDPSession(page), requests = new Map();
  await cdp.send('Network.enable');
  cdp.on('Network.responseReceived', event => {
    const url = new URL(event.response.url);
    if (url.origin !== origin) return;
    const headers = Object.fromEntries(Object.entries(event.response.headers).map(([name, value]) => [name.toLowerCase(), value]));
    requests.set(event.requestId, { path: url.pathname, status: event.response.status,
      encoding: headers['content-encoding'] ?? 'identity', declaredLength: headers['content-length'] ?? null,
      fromDiskCache: event.response.fromDiskCache ?? false, encodedWireBytes: 0 });
  });
  cdp.on('Network.loadingFinished', event => {
    const response = requests.get(event.requestId);
    if (response) { response.encodedWireBytes = event.encodedDataLength; network.push(response); }
  });
  const trials = [];
  for (const cache of ['cold', 'warm']) {
    network.length = 0; requests.clear();
    if (cache === 'cold') {
      await cdp.send('Network.clearBrowserCache');
      await table(page);
    } else await page.reload({ waitUntil: 'domcontentloaded' });
    try {
      await page.waitForFunction(() => window.__comparison.usableAt !== null
        || ['failed', 'unsupported', 'unavailable'].includes(window.game?.renderer?.state), null, { timeout: 60000 });
    } catch (error) {
      const debug = await page.evaluate(() => ({ state: window.game?.renderer.state, lastRendered: window.game?.renderer.lastRendered,
        receipts: window.__comparison.rendered.slice(-3), published: Object.keys(window.__comparison.published).slice(-5),
        committed: window.__comparison.committedFrame, focused: document.hasFocus(), hidden: document.hidden }));
      fs.writeFileSync(path.join(output, `benchmark-${renderer}-failure.json`), JSON.stringify(debug, null, 2));
      throw error;
    }
    await page.waitForTimeout(2000);
    const state = await snapshot(page);
    assert.equal(state.state, 'ready', state.status);
    assert(state.probe.usableAt > 0);
    const measurement = await page.evaluate(() => {
      const probe = window.__comparison, renderer = window.game.renderer;
      const frame = renderer.scene?.committed?.frame ?? probe.committedFrame;
      const memory = performance.memory ? {
        scope: 'Chrome performance.memory JavaScript heap only; excludes GPU/total browser RAM',
        usedJsHeap: performance.memory.usedJSHeapSize, totalJsHeap: performance.memory.totalJSHeapSize,
      } : null;
      return {
        firstUsableBoardMilliseconds: probe.usableAt,
        memory, rafIntervals: probe.raf, customCpuLayoutAndSubmitMilliseconds: renderer.metrics?.frameMilliseconds ?? null,
        resources: [...performance.getEntriesByType('navigation'), ...performance.getEntriesByType('resource')]
          .filter(entry => entry.responseEnd <= probe.usableAt)
          .map(entry => ({ path: new URL(entry.name).pathname, transferSize: entry.transferSize,
            encodedBodySize: entry.encodedBodySize, decodedBodySize: entry.decodedBodySize })),
        scene: frame.entities.map(entity => ({ kind: entity.kind, slot: entity.slotName,
          face: entity.appearance.faceIndex, shown: entity.appearance.showFace, rotation: entity.rotationIndex }))
          .sort((a, b) => a.slot.localeCompare(b.slot) || a.kind.localeCompare(b.kind)),
      };
    });
    const summary = samples => {
      if (!samples?.length) return null;
      const sorted = samples.slice().sort((a, b) => a - b);
      return { samples: sorted.length, median: sorted[Math.floor(sorted.length / 2)],
        p95: sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * .95))] };
    };
    const { rafIntervals, customCpuLayoutAndSubmitMilliseconds, scene, ...scoped } = measurement;
    const trial = { cache, ...scoped, sceneSha256: createHash('sha256').update(JSON.stringify(scene)).digest('hex'),
      entityCount: scene.length, rafIntervalMilliseconds: summary(rafIntervals),
      customCpuLayoutAndSubmitMilliseconds: summary(customCpuLayoutAndSubmitMilliseconds),
      firstUsableEncodedBodyBytes: measurement.resources.reduce((sum, entry) => sum + entry.encodedBodySize, 0),
      firstUsableTransferSizeIncludingHeaders: measurement.resources.reduce((sum, entry) => sum + entry.transferSize, 0),
      networkResponses: network.slice(), observedEncodedWireBytes: network.reduce((sum, entry) => sum + entry.encodedWireBytes, 0) };
    trials.push(trial);
    await save(page, `benchmark-${renderer}-${mobile ? 'phone' : 'desktop'}-${cache}`, { cache,
      firstUsableBoardMilliseconds: trial.firstUsableBoardMilliseconds, firstUsableEncodedBodyBytes: trial.firstUsableEncodedBodyBytes });
  }
  receipt.benchmarks ??= [];
  receipt.benchmarks.push({ renderer, viewport: mobile ? '390x844 DPR2 touch emulation' : '1280x800 DPR1',
    scope: 'Same host Chrome/software GPU, native fixture backend, fixed seed, actual correlated populated-board receipt; two single observations, not statistical device/FPS evidence',
    timingScope: 'Browser requestAnimationFrame intervals; CPU layout/submit is separately scoped, not GPU timing or physical-device FPS',
    cacheScope: 'Real HTTP cache enabled; new browser context + cleared cache for cold, same context reload for warm; service workers blocked',
    trials });
  await context.close();
}
async function lifecycle() {
  const { page, context } = await newPage();
  await table(page);
  const before = await snapshot(page), count = commands('discard').length;
  await page.evaluate(() => window.game.renderer.scene.gl.getExtension('WEBGL_lose_context').loseContext());
  await page.waitForFunction(() => window.game.renderer.state === 'failed');
  assert.equal(await page.evaluate(() => document.body.getAttribute('data-webgl-renderer-ready')), null);
  assert.equal(await page.evaluate(() => !!window.MahjongUnityBridge), false);
  await page.mouse.click(400, 400);
  assert.equal(commands('discard').length, count);
  await page.screenshot({ path: path.join(output, 'screenshots/context-loss-explicit-error.png'), fullPage: true });
  await page.evaluate(() => window.game.dispose());
  assert.equal(await page.locator('#webgl-canvas').count(), 0);
  const frames = await page.evaluate(() => window.game.renderer.metrics.renderedFrames);
  await page.waitForTimeout(150);
  assert.equal(await page.evaluate(() => window.game.renderer.metrics.renderedFrames), frames);
  receipt.cases.push({ name: 'actual-gpu-context-loss-and-disposal', beforeFrames: before.metrics.renderedFrames,
    noFallback: true, noActionAfterFailure: true, canvasRemovedOnDispose: true, framesStableAfterDispose: true,
    scope: 'Explicit GPU fault injection on an isolated fixture; not a natural device-loss rate measurement' });
  await context.close();
}
async function dependencyClosure() {
  const response = await fetch(origin + '/autotable/web-build.json');
  assert.equal(response.status, 200);
  const identity = await response.json();
  assert.equal(identity.engine, 'webgl2');
  assert.equal(identity.artifactKind, 'mahjong-web-build');
  assert(!identity.files.some(file => /unity|\.wasm$|\.data$|\.loader\.js$|\.framework\.js$/i.test(file.path)));
  assert(!response.headers.get('content-security-policy').includes("'wasm-unsafe-eval'"));
  for (const file of identity.files) {
    assert.match(file.path, /^[A-Za-z0-9_.-]+$/);
    const asset = await fetch(origin + '/autotable/' + file.path);
    const data = Buffer.from(await asset.arrayBuffer());
    assert.equal(asset.status, 200);
    assert.equal(data.length, file.bytes);
    assert.equal(createHash('sha256').update(data).digest('hex'), file.sha256);
  }
  for (const old of ['/autotable/unity/renderer-build.json', '/autotable/old.wasm', '/autotable/old.loader.js']) {
    const removed = await fetch(origin + old);
    assert.equal(removed.status, 404);
    assert.equal((await removed.arrayBuffer()).byteLength, 0);
  }
  receipt.cases.push({ name: 'actual-default-runtime-engine-free-artifact-closure', source: identity.sourceRevision,
    filesVerified: identity.files.length, noPrebuiltPlayer: true, strictCspWithoutWasmPermission: true });

  const noWasm = await newPage();
  await noWasm.page.addInitScript(() => Object.defineProperty(window, 'WebAssembly', { value: undefined, configurable: true }));
  await table(noWasm.page);
  await discard(noWasm.page);
  await save(noWasm.page, 'actual-playable-without-webassembly', { WebAssemblyRemovedForThisContext: true });
  await noWasm.context.close();

  const retired = await newPage();
  await table(retired.page);
  const url = new URL(retired.page.url()); url.searchParams.set('renderer', 'unity');
  await retired.page.goto(url.href, { waitUntil: 'domcontentloaded' });
  await retired.page.waitForFunction(() => window.game?.renderer?.state === 'ready'
    && window.game.renderer.scene.committed?.frame.entities.length > 50);
  assert.equal(await retired.page.evaluate(() => window.game.renderer.engine), 'webgl2');
  assert.equal(await retired.page.evaluate(() => typeof window.createUnityInstance), 'undefined');
  await save(retired.page, 'retired-selector-cannot-load-an-engine');
  await retired.context.close();

  const unsupported = await newPage();
  await unsupported.page.addInitScript(() => { delete window.WebGL2RenderingContext; });
  const room = `unsupported-webgl-${Date.now()}`;
  await unsupported.page.goto(origin + '/autotable/?' + new URLSearchParams({
    gameId: room, createGame: room, variant: 'changsha', seat: '0', botCount: '3', seed: '20261010',
  }));
  await unsupported.page.waitForFunction(() => window.game?.renderer?.state === 'unsupported');
  assert.equal(await unsupported.page.locator('#renderer-status').isVisible(), true);
  assert.equal(await unsupported.page.evaluate(() => document.body.dataset.webglRendererReady), undefined);
  assert.equal(await unsupported.page.evaluate(() => window.__comparison.actions.length), 0);
  receipt.cases.push({ name: 'unsupported-webgl-is-explicit-without-engine-fallback', sourceBrowserGlobalRemoved: true,
    qualification: 'Actual browser failure-path test with WebGL capability disabled in this isolated context' });
  await unsupported.context.close();
}
try {
  receipt.browserVersion = await browser.version();
  if (['all', 'style'].includes(receipt.selectedCase)) await appearance();
  if (['all', 'desktop'].includes(receipt.selectedCase)) await desktop();
  if (['all', 'phone'].includes(receipt.selectedCase)) await phone();
  if (['all', 'relay'].includes(receipt.selectedCase)) await relay();
  if (['all', 'manual'].includes(receipt.selectedCase)) await manualPickup();
  if (['all', 'benchmark'].includes(receipt.selectedCase)) {
    for (const mobile of [false, true]) await benchmark(mobile);
    const scenes = new Set(receipt.benchmarks.flatMap(entry => entry.trials.map(trial => trial.sceneSha256)));
    assert.equal(scenes.size, 1, 'Cold/warm views must receive the same viewer-safe populated test scene.');
    receipt.sceneComparability = { sameSceneAcrossViewportsAndCacheStates: true, sceneSha256: [...scenes][0],
      projection: 'kind/semantic slot/entitled face/visibility/rotation; opaque IDs and personal names excluded' };
  }
  if (['all', 'lifecycle'].includes(receipt.selectedCase)) await lifecycle();
  if (['all', 'dependencies'].includes(receipt.selectedCase)) await dependencyClosure();
  assert.deepEqual(receipt.errors, []);
  const forbidden = receipt.requests.filter(request => request.origin !== origin
    || /\/unity\/|\.wasm$|\.data$|\.loader\.js$|\.framework\.js$|renderer-build\.json/i.test(request.path));
  assert.deepEqual(forbidden, [], 'Normal actual browser loads must make no engine/prebuilt-player or external service requests.');
  receipt.runtimeDependencyProof = { observedBrowserRequests: receipt.requests.length, forbiddenEngineOrExternalRequests: 0,
    externalAutotableInstallOrService: false, defaultEngine: 'webgl2' };
  receipt.status = 'passed';
} catch (error) {
  receipt.status = 'failed'; receipt.failure = { name: error.name, message: error.message };
  throw error;
} finally {
  receipt.completedAtUtc = new Date().toISOString();
  fs.writeFileSync(path.join(output, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n');
  await browser.close();
}
console.log(JSON.stringify({ cases: receipt.cases, errors: receipt.errors }, null, 2));

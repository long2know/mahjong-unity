import assert from 'node:assert/strict';
import fs from 'node:fs';
import {
  base, probeRoot, receipt, wire, pageFor, ready, snapshot, capture, rendererSummary,
  launchPlayerBrowser, finishPlayerBrowser, recordCaseFailure,
} from './browser-player.mjs';
import { aimPoint } from './player-aim.mjs';

receipt.scope = 'mobile-playability';
receipt.qualification = 'Genuine Unity/C# input and production-server outcomes, progressed public-tile screenshots; Chromium emulation, not physical iOS/Android evidence';
receipt.transcript = [];
let page;
let room;

function commands(kind) {
  return wire.filter(connection => connection.query.gameId === room)
    .flatMap(connection => connection.sent.flatMap(message => message.entries ?? []))
    .filter(entry => entry[0] === kind);
}

async function table(viewport, dpr, mobile, mode = 'auto') {
  page = await pageFor(viewport, dpr, mobile);
  room = `mobile-contract-${Date.now()}-${Math.random().toString(16).slice(2, 8)}`;
  await page.goto(`${base}/autotable/?${new URLSearchParams({
    gameId: room, createGame: room, variant: 'changsha', seat: '0', botCount: '3',
    dealMode: mode, seed: '20261010', unityLocalBuild: '1',
  })}`, { waitUntil: 'domcontentloaded' });
  return ready(page);
}

async function ownTurn() {
  for (let attempt = 0; attempt < 8; attempt++) {
    await page.waitForFunction(() => {
      const game = window.game;
      const claim = document.getElementById('claim-window-actions');
      return game.renderer.state === 'failed' || game.renderer.state === 'ready' &&
        (game.adapter.latest.entities.some(entity => entity.interaction.canDiscard)
          || claim && !claim.hidden && !claim.querySelector('.ferro-claim-pass')?.disabled);
    }, null, { timeout: 15000 });
    const pass = page.locator('.ferro-claim-pass');
    if (await pass.isVisible()) {
      await pass.click();
      await page.waitForTimeout(150);
      continue;
    }
    const state = await snapshot(page);
    assert.equal(state.state, 'ready', state.statusText);
    if (state.frame.entities.some(entity => entity.interaction.canDiscard)) {
      await page.waitForTimeout(250);
      return snapshot(page);
    }
  }
  throw new Error('The real server did not restore the human discard turn');
}

async function point(entity, dx = 0) {
  const state = await snapshot(page);
  const box = await page.locator('#unity-canvas').boundingBox();
  const at = aimPoint(state, entity);
  const coordinates = { x: box.x + at.x + dx, y: box.y + at.y };
  const hit = await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.id, coordinates);
  assert.equal(hit, 'unity-canvas', JSON.stringify({ coordinates, hit, slotName: entity.slotName }));
  return coordinates;
}

async function handTargets() {
  const state = await snapshot(page);
  assert.equal(state.frame.localHand.entityIds.length, 14);
  const targets = [];
  for (const id of state.frame.localHand.entityIds) {
    const entity = state.frame.entities.find(entity => entity.entityId === id);
    targets.push({ entityId: id, slotName: entity.slotName, ...await point(entity) });
  }
  return targets;
}

async function discard({ touch = true, index = 0, dx = 0 } = {}) {
  const before = await ownTurn();
  const tile = before.frame.entities.find(entity =>
    entity.entityId === before.frame.localHand.entityIds[index] && entity.interaction.canDiscard);
  assert(tile?.appearance.showFace, 'The selected real hand tile must be currently discardable and entitled');
  const at = await point(tile, dx);
  const count = commands('discard').length;
  const start = before.actions.length;
  if (touch) await page.touchscreen.tap(at.x, at.y);
  else await page.mouse.dblclick(at.x, at.y, { delay: 75 });
  await page.waitForFunction(({ id, start }) => window.__playerProbe.actions.slice(start)
    .some(action => action.intent.kind === 'discard' && action.intent.entityId === id),
  { id: tile.entityId, start });
  await page.waitForFunction(id => window.game.renderer.state === 'ready'
    && !window.game.adapter.latest.entities.some(entity => entity.entityId === id && entity.group === 'hand'), tile.entityId);
  await page.waitForTimeout(500);
  const after = await snapshot(page);
  const actions = after.actions.slice(start).filter(action => action.intent.kind === 'discard');
  assert.equal(actions.length, 1, 'One gesture must emit one C# discard');
  assert.equal(actions[0].intent.entityId, tile.entityId, 'The native collider must choose the intended tile, not its neighbor');
  assert.equal(commands('discard').length, count + 1, 'One gesture must send exactly one native server command');
  const command = commands('discard').at(-1);
  assert.equal(Number(command[1]), before.frame.session.actorSeat);
  assert.deepEqual(Object.keys(command[2]), ['tileId']);
  assert.equal(Math.floor(command[2].tileId / 4), tile.appearance.faceIndex);
  assert(!after.frame.entities.some(entity => entity.entityId === tile.entityId && entity.group === 'hand'));
  const outcome = { input: touch ? 'ordinary-single-touch' : 'desktop-double-click',
    nativeActions: actions, command, target: { entityId: tile.entityId, slotName: tile.slotName, ...at },
    serverOutcomeVerified: true, nativeWireVerified: true };
  receipt.transcript.push(outcome);
  return outcome;
}

async function save(name, fields = {}) {
  const state = await snapshot(page);
  assert.equal(state.state, 'ready', state.statusText);
  assert.deepEqual(state.duplicateReceipts, []);
  assert.deepEqual(state.csp, []);
  for (const entity of state.frame.entities) {
    if (entity.group === 'wall' || entity.group === 'hand' && entity.seat !== 0) {
      assert.equal(entity.appearance.faceIndex, null, entity.slotName);
      assert.equal(entity.appearance.showFace, false, entity.slotName);
    }
  }
  const publicCenters = state.frame.entities.filter(entity => ['discard', 'meld'].includes(entity.group)
    && entity.appearance.showFace).map(entity => ({ entityId: entity.entityId, slotName: entity.slotName,
    ...aimPoint(state, entity) }));
  fs.writeFileSync(`${probeRoot}/${name}-state.json`, JSON.stringify({ ...state, publicCenters }, null, 2) + '\n');
  const pixels = await capture(page, name);
  await page.screenshot({ path: `${probeRoot}/screenshots/${name}-full.png`, fullPage: true });
  const result = { name, ...rendererSummary(state), pixels,
    publicTiles: state.frame.entities.filter(entity => ['discard', 'meld'].includes(entity.group)).length, ...fields };
  receipt.cases.push(result);
  fs.writeFileSync(`${probeRoot}/progress.json`, JSON.stringify(receipt, null, 2) + '\n');
  return state;
}

async function resize(viewport) {
  await page.setViewportSize(viewport);
  await page.waitForFunction(viewport => window.game.renderer.state === 'ready'
    && window.game.renderer.viewport.widthCssPx === viewport.width
    && window.game.renderer.viewport.heightCssPx === viewport.height, viewport);
  await page.waitForTimeout(300);
}

async function gestures() {
  const before = await ownTurn();
  const id = before.frame.localHand.entityIds[0];
  const tile = before.frame.entities.find(entity => entity.entityId === id);
  const at = await point(tile);
  const session = await page.context().newCDPSession(page);
  const outcomes = [];
  try {
    for (const kind of ['moved', 'canceled', 'long-pressed', 'multi-touch', 'resized']) {
      const count = commands('discard').length;
      const start = (await snapshot(page)).actions.length;
      await session.send('Input.dispatchTouchEvent', {
        type: 'touchStart', touchPoints: [{ ...at, id: 0 }],
      });
      await page.waitForTimeout(100);
      if (kind === 'moved') {
        await session.send('Input.dispatchTouchEvent', {
          type: 'touchMove', touchPoints: [{ x: at.x + 28, y: at.y, id: 0 }],
        });
        await page.waitForTimeout(100);
      } else if (kind === 'long-pressed') await page.waitForTimeout(650);
      else if (kind === 'multi-touch') {
        await session.send('Input.dispatchTouchEvent', {
          type: 'touchStart', touchPoints: [{ ...at, id: 0 }, { x: at.x + 40, y: at.y, id: 1 }],
        });
        await page.waitForTimeout(100);
      } else if (kind === 'resized') await resize({ width: 500, height: 900 });
      await session.send('Input.dispatchTouchEvent', {
        type: kind === 'canceled' || kind === 'multi-touch' ? 'touchCancel' : 'touchEnd', touchPoints: [],
      });
      await page.waitForTimeout(500);
      const after = await snapshot(page);
      assert.equal(commands('discard').length, count, kind + ' must not send a discard');
      assert(!after.actions.slice(start).some(action => action.intent.kind === 'discard'), kind);
      assert(after.frame.entities.some(entity => entity.entityId === id && entity.group === 'hand'));
      outcomes.push({ kind, nativeActions: after.actions.slice(start), noDiscardVerified: true });
    }
  } finally { await session.detach(); }
  return outcomes;
}

try {
  await launchPlayerBrowser();
  for (const [width, height] of [[360, 800], [390, 844], [500, 900]]) {
    for (const dpr of [1, 2, 3]) {
      receipt.stage = `actual-mobile-${width}-dpr${dpr}-single-touch`;
      const initial = await table({ width, height }, dpr, true);
      assert.equal(initial.frame.presentation.viewMode, 'flat');
      assert.equal(initial.frame.presentation.boardLayout, 'rectangular');
      assert.equal(initial.frame.presentation.handLayout, 'separate');
      assert.equal(initial.viewport.renderPixelRatio, Math.min(dpr, 2));
      const targets = await handTargets();
      const outcome = await discard({ index: dpr === 1 ? 0 : dpr === 2 ? 6 : 13, dx: dpr === 1 ? -18 : dpr === 2 ? 18 : 0 });
      await save(receipt.stage, { ...outcome, handTargets: targets, handCentersReached: 14 });
      await page.context().close();
    }
  }

  receipt.stage = 'actual-mobile-canceled-gestures';
  await table({ width: 390, height: 844 }, 3, true);
  const canceled = await gestures();
  await save(receipt.stage, { gestures: canceled, noUnintendedCommands: true });

  receipt.stage = 'actual-mobile-progressed-readable-table';
  for (let turn = 0; turn < 10; turn++) {
    await discard();
    await page.waitForTimeout(750);
    const state = await snapshot(page);
    if (state.frame.entities.filter(entity => entity.group === 'discard').length >= 24
      && state.frame.entities.filter(entity => entity.group === 'meld').length >= 12) break;
  }
  const progressed = await ownTurn();
  assert(progressed.frame.entities.filter(entity => entity.group === 'discard').length >= 24);
  assert(progressed.frame.entities.filter(entity => entity.group === 'meld').length >= 12);
  for (const viewport of [{ width: 360, height: 800 }, { width: 390, height: 844 }, { width: 500, height: 900 }]) {
    await resize(viewport);
    await save(`progressed-${viewport.width}x${viewport.height}`, { progressedRealGame: true,
      handTargets: await handTargets(), handCentersReached: 14 });
  }
  await page.locator('#chat-toggle').click();
  await page.waitForTimeout(350);
  const chatTargets = await handTargets();
  await save('progressed-portrait-chat-open', { handTargets: chatTargets, handCentersReached: 14 });
  const chatOutcome = await discard({ index: 13 });
  await save('actual-mobile-chat-open-single-touch', chatOutcome);
  await page.locator('#chat-toggle').click();
  await ownTurn();
  await resize({ width: 844, height: 390 });
  let state = await snapshot(page);
  assert.equal(state.frame.presentation.handLayout, 'separate');
  assert.equal(state.frame.presentation.viewMode, 'perspective');
  const landscapeOutcome = await discard();
  await save('actual-mobile-landscape-single-touch', landscapeOutcome);
  await ownTurn();
  await resize({ width: 390, height: 844 });
  assert.equal((await snapshot(page)).frame.presentation.viewMode, 'flat');
  await page.locator('#settings-button').click();
  await page.locator('#settings-tab-display').click();
  await page.locator('#settings-camera-view').selectOption('perspective');
  await page.locator('#settings-close-v2').click();
  await page.waitForTimeout(300);
  await save('progressed-portrait-explicit-perspective', { explicitPreferencePreserved: true });
  await resize({ width: 500, height: 900 });
  assert.equal((await snapshot(page)).frame.presentation.viewMode, 'perspective');
  await page.locator('#settings-button').click();
  await page.locator('#settings-tab-display').click();
  await page.locator('#settings-camera-view').selectOption('auto');
  await page.locator('#settings-close-v2').click();
  await resize({ width: 1280, height: 800 });
  await save('progressed-desktop-with-move-log', { progressedRealGame: true });
  await page.context().close();

  receipt.stage = 'actual-desktop-native-double-click';
  await table({ width: 1280, height: 800 }, 1, false);
  const desktopOutcome = await discard({ touch: false });
  await save(receipt.stage, desktopOutcome);
  await page.context().close();

  receipt.stage = 'actual-mobile-manual-pickup';
  await table({ width: 390, height: 844 }, 2, true, 'manual');
  await page.locator('#roll-dice').click();
  await page.waitForFunction(() => window.game.renderer.state === 'ready' && window.game.adapter.latest.pickup?.canTake);
  await page.waitForTimeout(250);
  const before = await snapshot(page);
  const pickup = before.frame.pickup;
  const trigger = before.frame.entities.find(entity => entity.slotName === pickup.triggerSlotName);
  assert(trigger?.interaction.canPickup);
  const at = await point(trigger);
  const count = commands('pickup').filter(entry => entry[1] === 'take').length;
  await page.touchscreen.tap(at.x, at.y);
  await page.waitForFunction(previous => JSON.stringify(window.game.adapter.latest.pickup) !== previous, JSON.stringify(pickup));
  const after = await snapshot(page);
  const native = after.actions.slice(before.actions.length).filter(action => action.intent.kind === 'takePickup');
  assert.equal(native.length, 1);
  assert.equal(commands('pickup').filter(entry => entry[1] === 'take').length, count + 1);
  const command = commands('pickup').filter(entry => entry[1] === 'take').at(-1);
  assert.deepEqual(command[2], { seatIndex: 0, count: pickup.count });
  await save(receipt.stage, { nativeActions: native, command, serverOutcomeVerified: true, nativeWireVerified: true });
  await page.context().close();
  assert.deepEqual(receipt.pageErrors, []);
  receipt.status = 'passed';
  receipt.stage = 'completed';
} catch (error) {
  if (page && !page.isClosed()) await recordCaseFailure(page, receipt.stage, error);
  receipt.status = 'failed';
  receipt.failure = { name: error.name, message: error.message, stack: error.stack };
  process.exitCode = 1;
} finally {
  await finishPlayerBrowser();
}

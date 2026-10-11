import assert from 'node:assert/strict';
import fs from 'node:fs';
import {
  base, probeRoot, receipt, wire, pageFor, ready, snapshot, capture, recordCaseFailure, rendererSummary,
  manualMeasurements, launchPlayerBrowser, finishPlayerBrowser,
} from './browser-player.mjs';
import { aimPoint } from './player-aim.mjs';

receipt.scope = 'gameplay';
receipt.qualification = 'Actual generated Unity/C# browser input, native server outcomes, rendering/privacy and runtime-origin qualification; emulated mobile, not physical devices';
receipt.transcript = [];
receipt.selectedCase = process.env.MAHJONG_PLAYER_CASE ?? null;
let currentPage;

const commands = (room, kind, key) => wire.filter(connection => connection.query.gameId === room)
  .flatMap(connection => connection.sent.flatMap(message => message.entries ?? []))
  .filter(entry => entry[0] === kind && (key === undefined || String(entry[1]) === String(key)));

async function newTable({ mode = 'auto', variant = 'changsha', bots = 3, dpr = 1, mobile = false } = {}) {
  const page = await pageFor(mobile ? { width: 390, height: 844 } : { width: 1280, height: 800 }, dpr, mobile);
  currentPage = page;
  const room = `native-${variant}-${mode}-${Date.now()}-${Math.random().toString(16).slice(2, 7)}`;
  const params = new URLSearchParams({
    gameId: room, createGame: room, variant, seat: '0', botCount: String(bots),
    dealMode: mode, seed: '20261010',
  });
  await page.goto(`${base}/autotable/?${params}`, { waitUntil: 'domcontentloaded' });
  const state = await ready(page);
  assert.equal(state.client.gameId, room);
  return { page, room, state };
}

async function observeAction(page, start, kind, entityId) {
  await page.waitForFunction(({ start, kind, entityId }) => {
    const p = window.__playerProbe;
    return p.actions.slice(start).some(action => action.intent.kind === kind
      && (entityId === undefined || action.intent.entityId === entityId
        || action.intent.triggerEntityId === entityId || action.intent.entityIds?.includes(entityId)))
      || window.game.renderer.state === 'failed';
  }, { start, kind, entityId }, { timeout: 3500 });
  const state = await snapshot(page);
  assert.equal(state.state, 'ready', state.statusText);
  const action = state.actions.slice(start).find(action => action.intent.kind === kind
    && (entityId === undefined || action.intent.entityId === entityId
      || action.intent.triggerEntityId === entityId || action.intent.entityIds?.includes(entityId)));
  assert(action, `Real C# did not emit ${kind} for ${entityId}`);
  const result = state.results.find(result => result.actionId === action.actionId);
  assert(result && ['local', 'submitted'].includes(result.status), JSON.stringify({ action, result }));
  return action;
}

async function touchDoubleTap(page, x, y) {
  const session = await page.context().newCDPSession(page);
  try {
    const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
    for (let tap = 0; tap < 2; tap++) {
      await session.send('Input.dispatchTouchEvent', {
        type: 'touchStart', touchPoints: [{ x, y, id: 0 }],
      });
      await delay(55);
      await session.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
      if (tap === 0) await delay(40);
    }
  } finally { await session.detach(); }
}

async function pointAt(page, entity, { hover = true } = {}) {
  const state = await snapshot(page);
  const point = aimPoint(state, entity);
  const box = await page.locator('#unity-canvas').boundingBox();
  assert(box && point.x >= 0 && point.x < box.width && point.y >= 0 && point.y < box.height,
    JSON.stringify({ entity: entity.slotName, point, box }));
  const coordinates = { x: box.x + point.x, y: box.y + point.y };
  if (!hover) return coordinates;
  const start = state.actions.length;
  await page.mouse.move(coordinates.x, coordinates.y);
  if (state.actions.filter(action => action.intent.kind === 'hover').at(-1)?.intent.entityId === entity.entityId) {
    await page.waitForTimeout(90);
    const moved = await snapshot(page);
    if (moved.actions.filter(action => action.intent.kind === 'hover').at(-1)?.intent.entityId === entity.entityId) {
      return coordinates;
    }
  }
  try {
    await observeAction(page, start, 'hover', entity.entityId);
    return coordinates;
  } catch (error) {
    // A pose at a collider edge can be sub-pixel in perspective; use only real
    // mouse movement and verify the resulting C# target, never synthesize it.
    for (const [dx, dy] of [[0, -3], [0, 3], [-3, 0], [3, 0], [0, -6], [0, 6]]) {
      const before = (await snapshot(page)).actions.length;
      await page.mouse.move(coordinates.x + dx, coordinates.y + dy);
      try {
        await observeAction(page, before, 'hover', entity.entityId);
        return { x: coordinates.x + dx, y: coordinates.y + dy };
      } catch {}
    }
    throw error;
  }
}

async function actualDiscard(page, room, { touch = false, entityId = null } = {}) {
  await page.waitForFunction(() => window.game.renderer.state === 'failed'
    || window.game.adapter.latest?.entities.some(entity => entity.interaction.canDiscard));
  await page.waitForTimeout(150);
  const before = await snapshot(page);
  assert.equal(before.state, 'ready', before.statusText);
  const tile = before.frame.localHand.entityIds
    .map(id => before.frame.entities.find(entity => entity.entityId === id))
    .find(entity => entity?.interaction.canDiscard && entity.appearance.showFace
      && (!entityId || entity.entityId === entityId));
  assert(tile, 'Actual authoritative hand has no discardable visible tile');
  const point = await pointAt(page, tile, { hover: !touch });
  const start = (await snapshot(page)).actions.length;
  const previousCommands = commands(room, 'discard').length;
  if (touch) {
    await touchDoubleTap(page, point.x, point.y);
  } else {
    await page.mouse.dblclick(point.x, point.y, { delay: 75 });
  }
  const select = await observeAction(page, start, 'select', tile.entityId);
  const discard = await observeAction(page, start, 'discard', tile.entityId);
  await page.waitForFunction(entityId => window.game.renderer.state === 'failed'
    || window.game.adapter.latest?.entities.some(entity => entity.entityId === entityId && entity.group === 'discard')
    || !window.game.adapter.latest?.entities.some(entity => entity.entityId === entityId && entity.group === 'hand'), tile.entityId);
  await page.waitForTimeout(200);
  const after = await snapshot(page);
  assert.equal(after.state, 'ready', after.statusText);
  assert.equal(commands(room, 'discard').length, previousCommands + 1,
    'Exactly one retained server discard command must result from the C# double tap');
  const command = commands(room, 'discard').at(-1);
  assert.equal(Number(command[1]), before.frame.session.actorSeat);
  assert.deepEqual(Object.keys(command[2]), ['tileId']);
  assert(Number.isInteger(command[2].tileId) && command[2].tileId >= 0 && command[2].tileId <= 107);
  assert(!after.frame.entities.some(entity => entity.entityId === tile.entityId && entity.group === 'hand'));
  assert.equal(after.duplicateReceipts.length, 0);
  receipt.transcript.push({ room, operation: touch ? 'touch-select-discard' : 'mouse-select-discard',
    nativeActions: [select, discard], command, sourceSlot: tile.slotName,
    outcome: { entityNoLongerInOwnedHand: true, turn: after.frame.turn, localHandCount: after.frame.localHand.entityIds.length } });
  return { state: after, nativeActions: [select, discard], command, sourceSlot: tile.slotName };
}

async function actualPickup(page, room) {
  await page.waitForFunction(() => window.game.renderer.state === 'failed'
    || window.game.adapter.latest?.pickup?.canTake);
  await page.waitForTimeout(150);
  const before = await snapshot(page), pickup = before.frame.pickup;
  assert.equal(before.state, 'ready', before.statusText);
  assert(pickup?.canTake && pickup.count > 0);
  const tile = before.frame.entities.find(entity => entity.slotName === pickup.triggerSlotName
    && entity.interaction.canPickup);
  assert(tile && tile.appearance.faceIndex === null && tile.appearance.knownFace === false);
  const point = await pointAt(page, tile);
  const start = (await snapshot(page)).actions.length;
  const count = commands(room, 'pickup', 'take').length;
  await page.mouse.click(point.x, point.y, { delay: 55 });
  const action = await observeAction(page, start, 'takePickup', tile.entityId);
  await page.waitForFunction(previous => window.game.renderer.state === 'failed'
    || JSON.stringify(window.game.adapter.latest?.pickup) !== previous, JSON.stringify(pickup));
  await page.waitForTimeout(100);
  assert.equal(commands(room, 'pickup', 'take').length, count + 1);
  const command = commands(room, 'pickup', 'take').at(-1);
  assert.deepEqual(command[2], { seatIndex: before.frame.session.actorSeat, count: pickup.count });
  assert.deepEqual(Object.keys(command[2]).sort(), ['count', 'seatIndex']);
  receipt.transcript.push({ room, operation: 'manual-wall-pickup', phase: pickup.phase,
    anonymousTriggerSlot: pickup.triggerSlotName, nativeAction: action, nativeCommand: command });
  return action;
}

async function requirePrivacy(page, actor = null) {
  const state = await snapshot(page);
  assert.equal(state.state, 'ready', state.statusText);
  assert.equal(state.frame.session.actorSeat, actor);
  for (const entity of state.frame.entities) {
    assert.match(entity.entityId, /^entity_[0-9a-f]{32}$/);
    const privateTile = entity.group === 'wall'
      || entity.group === 'hand' && entity.seat !== actor
      || entity.group === 'meld' && !entity.appearance.showFace && entity.seat !== actor;
    if (privateTile) {
      assert.equal(entity.appearance.faceIndex, null, entity.slotName);
      assert.equal(entity.appearance.knownFace, false, entity.slotName);
      assert.equal(entity.appearance.showFace, false, entity.slotName);
    }
    if (actor === null) assert(Object.values(entity.interaction).every(flag => flag === false));
  }
  assert.equal(state.duplicateReceipts.length, 0);
  assert.deepEqual(state.csp, []);
  return state;
}

async function handControlReachability(page) {
  const state = await snapshot(page);
  const box = await page.locator('#unity-canvas').boundingBox();
  const points = state.frame.localHand.entityIds.map(id => {
    const entity = state.frame.entities.find(entity => entity.entityId === id);
    const point = aimPoint(state, entity);
    return { entityId: id, slotName: entity.slotName, x: box.x + point.x, y: box.y + point.y };
  });
  return page.evaluate(points => points.map(point => {
    const hit = document.elementFromPoint(point.x, point.y);
    return { ...point, hitTag: hit?.tagName ?? null, hitId: hit?.id ?? null,
      chatCovered: !!hit?.closest('#chat-panel'), canvasReceivesInput: hit?.id === 'unity-canvas' };
  }), points);
}

async function record(name, page, fields = {}) {
  const state = await snapshot(page);
  assert.equal(state.state, 'ready', state.statusText);
  assert.equal(state.duplicateReceipts.length, 0);
  const pixels = await capture(page, name);
  const summary = { name, ...rendererSummary(state), duplicateReceipts: state.duplicateReceipts, pixels, ...fields };
  receipt.cases.push(summary);
  fs.writeFileSync(`${probeRoot}/progress.json`, JSON.stringify({ stage: receipt.stage, cases: receipt.cases,
    caseFailures: receipt.caseFailures, transcript: receipt.transcript }, null, 2) + '\n');
}

async function caseRun(name, action) {
  if (receipt.selectedCase && !receipt.selectedCase.split(',').includes(name)) return;
  receipt.stage = name;
  try { await action(); }
  catch (error) {
    try {
      if (currentPage && !currentPage.isClosed()) await recordCaseFailure(currentPage, name, error);
      else throw new Error('No live failure page');
    } catch (captureError) {
      receipt.caseFailures.push({ name, error: { name: error.name, message: error.message.slice(0, 4000) },
        captureError: captureError.message.slice(0, 300) });
    }
    fs.writeFileSync(`${probeRoot}/progress.json`, JSON.stringify({ stage: receipt.stage, cases: receipt.cases,
      caseFailures: receipt.caseFailures, transcript: receipt.transcript }, null, 2) + '\n');
    if (currentPage && !currentPage.isClosed()) await currentPage.context().close();
  }
}

try {
  await launchPlayerBrowser();
  await caseRun('actual-auto-csharp-select-discard', async () => {
    const table = await newTable();
    currentPage = table.page;
    await requirePrivacy(table.page, 0);
    assert.equal(table.state.frame.localHand.entityIds.length, 14);
    const priorFlip = table.state.actions.filter(action => action.intent.kind === 'flip').length;
    const previousDiscard = commands(table.room, 'discard').length;
    await table.page.locator('#unity-canvas').focus();
    await table.page.keyboard.press('f');
    await table.page.keyboard.press('p');
    await table.page.waitForFunction(() => window.game.adapter.latest.presentation.viewMode === 'flat'
      && window.game.renderer.state === 'ready');
    await table.page.keyboard.press('p');
    await table.page.keyboard.press('l');
    await table.page.waitForFunction(() => window.game.adapter.latest.presentation.viewMode === 'perspective'
      && window.game.renderer.state === 'ready');
    assert.equal((await snapshot(table.page)).actions.filter(action => action.intent.kind === 'flip').length, priorFlip);
    assert.equal(commands(table.room, 'discard').length, previousDiscard);
    const outcome = await actualDiscard(table.page, table.room);
    await record('actual-auto-csharp-select-discard', table.page, {
      nativeActions: outcome.nativeActions, serverOutcomeVerified: true, nativeWireVerified: true,
      changshaKeyboardViewSmoke: { cameraToggledAndRestored: true, labelsChanged: true, forbiddenFlipNoRuleWrite: true },
    });
    await table.page.context().close();
  });
  await caseRun('actual-manual-csharp-count-only-pickup-discard', async () => {
    const table = await newTable({ mode: 'manual' });
    currentPage = table.page;
    await table.page.locator('#roll-dice').click();
    const actions = [];
    for (let step = 0; step < 16; step++) {
      await table.page.waitForFunction(() => window.game.renderer.state === 'failed'
        || window.game.adapter.latest?.pickup?.canTake
        || window.game.adapter.latest?.entities.some(entity => entity.interaction.canDiscard));
      const state = await snapshot(table.page);
      assert.equal(state.state, 'ready', state.statusText);
      if (state.frame.entities.some(entity => entity.interaction.canDiscard)) break;
      actions.push(await actualPickup(table.page, table.room));
    }
    assert(actions.length >= 4, 'The actual full manual pickup chain must include 4/4/4/1 and dealer extra');
    await requirePrivacy(table.page, 0);
    assert.equal((await snapshot(table.page)).frame.localHand.entityIds.length, 14);
    const outcome = await actualDiscard(table.page, table.room);
    await record('actual-manual-csharp-count-only-pickup-discard', table.page, {
      nativeActions: [...actions, ...outcome.nativeActions],
      countOnlyPickupCommands: commands(table.room, 'pickup', 'take'),
      serverOutcomeVerified: true, nativeWireVerified: true,
    });
    await table.page.context().close();
  });
  await caseRun('actual-dealt-hand-viewport-camera-controls', async () => {
    const table = await newTable();
    currentPage = table.page;
    const beforeCases = receipt.cases.length;
    await manualMeasurements(table.page, 'dealt-dpr1');
    assert.equal((await snapshot(table.page)).frame.localHand.entityIds.length, 14);
    await requirePrivacy(table.page, 0);
    await record('actual-dealt-hand-viewport-camera-controls', table.page, {
      measuredCases: receipt.cases.slice(beforeCases).map(c => c.name), dealtHandCount: 14,
      rendererReceiptsStrictlyNonduplicated: true,
    });
    await table.page.context().close();
  });
  await caseRun('actual-mobile-combined-reachability', async () => {
      const measurements = [], nativeActions = [], lowerRows = [], touchSelections = [];
      async function reflow(page, before) {
        await page.waitForFunction(previous => window.game.renderer.state === 'ready'
          && window.game.renderer.lastRendered > previous.lastRendered
          && window.game.renderer.viewport.resizeRevision > previous.viewport.resizeRevision, before);
        await page.waitForTimeout(150);
      }
      async function chat(page, open) {
        const current = await page.locator('#chat-toggle').getAttribute('aria-expanded');
        if (current === String(open)) return;
        const before = await snapshot(page);
        await page.locator('#chat-toggle').click();
        await reflow(page, before);
        assert.equal(await page.locator('#chat-toggle').getAttribute('aria-expanded'), String(open));
        if (open) {
          await page.waitForFunction(() => !document.getElementById('chat-input').disabled);
          assert(await page.locator('#chat-input').isVisible());
        }
      }
      async function layout(page, board, hand, perspective) {
        const before = await snapshot(page);
        await page.locator('#settings-button').click();
        await page.locator('#settings-tab-display').click();
        await page.locator('#settings-board-layout').selectOption(board);
        await page.locator('#settings-hand-layout').selectOption(hand);
        await page.locator('#settings-perspective-toggle').setChecked(perspective);
        await page.locator('#settings-close-v2').click();
        await page.waitForFunction(({ board, hand, perspective, previous }) =>
          window.game.renderer.state === 'ready' && window.game.renderer.lastRendered > previous
          && window.game.adapter.latest.presentation.boardLayout === board
          && window.game.adapter.latest.presentation.handLayout === hand
          && window.game.adapter.latest.presentation.viewMode === (perspective ? 'perspective' : 'flat'),
        { board, hand, perspective, previous: before.lastRendered });
        await page.waitForTimeout(150);
      }
      async function measure(page, name) {
        const state = await requirePrivacy(page, 0);
        assert.equal(state.frame.localHand.entityIds.length, 14);
        assert.equal(state.viewport.renderPixelRatio, Math.min(state.viewport.devicePixelRatio, 2));
        assert.deepEqual(state.canvas, { width: state.viewport.widthPx, height: state.viewport.heightPx });
        const controls = await handControlReachability(page);
        assert.equal(controls.length, 14);
        assert(controls.every(control => control.canvasReceivesInput && !control.chatCovered),
          JSON.stringify(controls.filter(control => !control.canvasReceivesInput)));
        const chrome = await page.locator('#chat-panel').evaluate(element => {
          const bounds = element.getBoundingClientRect(), style = getComputedStyle(element);
          return { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height,
            display: style.display, visibility: style.visibility,
            expanded: document.getElementById('chat-toggle').getAttribute('aria-expanded') };
        });
        assert.notEqual(chrome.display, 'none');
        assert.equal(chrome.visibility, 'visible');
        measurements.push({ name, viewport: state.viewport, presentation: state.frame.presentation,
          frameId: state.lastRendered, controls, chrome, pixels: await capture(page, name) });
        fs.writeFileSync(`${probeRoot}/mobile-geometry-progress.json`,
          JSON.stringify({ measurements, lowerRows, touchSelections }, null, 2) + '\n');
        return controls;
      }
      for (const dpr of [1, 2, 3]) {
        const table = await newTable({ dpr, mobile: true });
        currentPage = table.page;
        const page = table.page;
        await measure(page, `clean-dpr${dpr}-portrait-auto-collapsed`);
        await chat(page, true);
        await measure(page, `clean-dpr${dpr}-portrait-auto-open`);
        await chat(page, false);
        let before = await snapshot(page);
        await page.setViewportSize({ width: 844, height: 390 });
        await reflow(page, before);
        await measure(page, `clean-dpr${dpr}-landscape-auto-collapsed`);
        await chat(page, true);
        await measure(page, `clean-dpr${dpr}-landscape-auto-open`);
        await layout(page, 'rectangular', 'separate', true);
        await measure(page, `clean-dpr${dpr}-landscape-rectangular-separate-open`);
        await chat(page, false);
        before = await snapshot(page);
        await page.setViewportSize({ width: 390, height: 844 });
        await reflow(page, before);
        await layout(page, 'classic', 'separate', false);
        await measure(page, `clean-dpr${dpr}-portrait-flat-classic-separate`);
        await layout(page, 'rectangular', 'separate', false);
        const controls = await measure(page, `clean-dpr${dpr}-portrait-flat-rectangular-separate`);
        const lowerY = Math.max(...controls.map(control => control.y));
        const previouslyCovered = new Set(['hand.1@0', 'hand.5@0', 'hand.8@0', 'hand.10@0', 'hand.0@0', 'hand.4@0']);
        const target = controls.find(control => Math.abs(control.y - lowerY) < 0.1
          && previouslyCovered.has(control.slotName));
        assert(target, 'The actual fourteen-tile layout must include a formerly covered lower-row tile');
        const actionStart = (await snapshot(page)).actions.length;
        const touch = await page.context().newCDPSession(page);
        await touch.send('Input.dispatchTouchEvent', {
          type: 'touchStart', touchPoints: [{ x: target.x, y: target.y, id: 0 }],
        });
        await new Promise(resolve => setTimeout(resolve, 70));
        await touch.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
        await touch.detach();
        const selected = await observeAction(page, actionStart, 'select', target.entityId);
        touchSelections.push({ dpr, action: selected, formerlyBlockedSourceSlot: target.slotName });
        await page.waitForTimeout(400);
        const outcome = await actualDiscard(page, table.room, { entityId: target.entityId });
        nativeActions.push(selected, ...outcome.nativeActions);
        lowerRows.push({ dpr, point: target, formerlyBlockedSourceSlot: target.slotName,
          nativeActions: outcome.nativeActions, command: outcome.command,
          serverOutcomeVerified: true, nativeWireVerified: true });
        if (dpr === 3) {
          assert.equal(measurements.length, 21);
          await record('actual-mobile-combined-reachability', page, {
            actualDeviceLevel: 'Chromium viewport/DPR/touch emulation, not physical iOS/Android',
            measurements, measuredLayouts: 21, measuredHandCenters: 294,
            allHandCentersHitUnityCanvas: true, chatPreservedVisible: true,
            nativeActions, lowerRows, touchSelections,
            touchscreenSingleSelectionQualified: true,
            touchscreenDoubleTapDiscardQualified: false,
            serverOutcomeVerified: true, nativeWireVerified: true,
          });
        }
        await page.context().close();
      }
  });
  for (const dpr of [2, 3]) {
    await caseRun(`actual-mobile-dpr${dpr}-touch`, async () => {
      const table = await newTable({ dpr, mobile: true });
      currentPage = table.page;
      assert.equal(table.state.viewport.devicePixelRatio, dpr);
      assert.equal(table.state.viewport.renderPixelRatio, 2);
      assert.equal(table.state.frame.presentation.handLayout, 'separate');
      const handReachability = await handControlReachability(table.page);
      fs.writeFileSync(`${probeRoot}/dpr${dpr}-hand-input-reachability.json`,
        JSON.stringify({ viewport: table.state.viewport, controls: handReachability }, null, 2) + '\n');
      assert(handReachability.every(control => control.canvasReceivesInput && !control.chatCovered),
        `Portrait hand input is blocked by browser chrome: ${JSON.stringify(
          handReachability.filter(control => !control.canvasReceivesInput))}`);
      const outcome = await actualDiscard(table.page, table.room, { touch: true });
      await table.page.setViewportSize({ width: 844, height: 390 });
      await table.page.waitForFunction(() => window.game.renderer.state === 'failed'
        || window.game.renderer.viewport.widthCssPx === 844 && window.game.renderer.lastRendered > 0);
      await table.page.waitForTimeout(200);
      await record(`actual-mobile-dpr${dpr}-touch`, table.page, {
        nativeActions: outcome.nativeActions, actualDeviceLevel: 'Chromium mobile emulation with real touchscreen events',
        handInputReachabilityBeforeDiscard: handReachability,
        touchGestureInputMethod: 'One CDP session dispatching a real 55ms/40ms/55ms double-touch sequence',
        serverOutcomeVerified: true, nativeWireVerified: true,
      });
      await table.page.context().close();
    });
  }
  await caseRun('actual-two-human-chat-spectator-privacy', async () => {
    const owner = await newTable({ bots: 2 });
    currentPage = owner.page;
    const guest = await pageFor({ width: 1280, height: 800 });
    await guest.goto(`${base}/autotable/?${new URLSearchParams({
      gameId: owner.room, variant: 'changsha', join: '1',
    })}`, { waitUntil: 'domcontentloaded' });
    const guestState = await ready(guest);
    const guestSeat = guestState.frame.session.actorSeat;
    assert(Number.isInteger(guestSeat) && guestSeat !== 0);
    await requirePrivacy(owner.page, 0);
    await requirePrivacy(guest, guestSeat);
    async function chat(page) {
      if (await page.locator('#chat-panel.chat-panel-collapsed').count()) await page.locator('#chat-toggle').click();
      await page.waitForFunction(() => document.getElementById('chat-input')?.disabled === false);
    }
    await chat(owner.page); await chat(guest);
    await owner.page.locator('#chat-input').fill('Actual Unity two-human chat');
    await owner.page.locator('#chat-send').click();
    await guest.waitForFunction(() => [...document.querySelectorAll('.chat-message-body')]
      .some(element => element.textContent === 'Actual Unity two-human chat'));
    await guest.reload({ waitUntil: 'domcontentloaded' });
    await ready(guest); await chat(guest);
    await guest.waitForFunction(() => [...document.querySelectorAll('.chat-message-body')]
      .some(element => element.textContent === 'Actual Unity two-human chat'));
    await requirePrivacy(guest, guestSeat);
    const spectator = await pageFor({ width: 1280, height: 800 });
    await spectator.goto(`${base}/autotable/?${new URLSearchParams({
      gameId: owner.room, variant: 'changsha', seat: '-1',
    })}`, { waitUntil: 'domcontentloaded' });
    await ready(spectator);
    await requirePrivacy(spectator, null);
    const first = await capture(spectator, 'dealt-spectator-before-follow');
    await spectator.getByTestId('spectator-follow-seat-2').click();
    await spectator.waitForFunction(() => window.game.adapter.latest?.session.viewSeat === 2
      && window.game.renderer.state === 'ready');
    await spectator.waitForTimeout(200);
    await spectator.getByTestId('spectator-show-all-toggle').check();
    await requirePrivacy(spectator, null);
    const after = await capture(spectator, 'dealt-spectator-after-follow');
    assert.notEqual(first.sha256, after.sha256);
    await record('actual-two-human-chat-spectator-privacy', spectator, {
      actualTwoHumanSeats: [0, guestSeat], chatSentReceivedReloaded: true, showAllDidNotRevealConcealedFaces: true,
      spectatorViewSeat: 2, authoritySeat: null, followPixelChangeObserved: true,
    });
    currentPage = owner.page;
    if (!await owner.page.locator('#chat-panel').evaluate(element =>
      element.classList.contains('chat-panel-collapsed'))) {
      await owner.page.locator('#chat-toggle').click();
    }
    const before = await snapshot(owner.page);
    await owner.page.locator('#leave-seat').click();
    await owner.page.waitForTimeout(200);
    const retained = await snapshot(owner.page);
    await requirePrivacy(owner.page, retained.frame.session.actorSeat);
    const oldSession = retained.frame.sessionId;
    const disconnect = await owner.page.evaluate(() => {
      const game = window.game;
      game.client.disconnect();
      return {
        clientConnected: game.client.connected(), sessionId: game.adapter.sessionId,
        privateProjectionAbsent: game.adapter.latest === null || game.adapter.latest.entities.length === 0,
        unityFrameQueueEmpty: game.renderer.queue === null, renderedMarkerRemoved:
          document.body.getAttribute('data-unity-renderer-ready') !== 'true',
        clearReason: window.__playerProbe.clearReasons.at(-1)?.reason,
      };
    });
    assert.equal(disconnect.clientConnected, false);
    assert.equal(disconnect.privateProjectionAbsent, true);
    assert.equal(disconnect.unityFrameQueueEmpty, true);
    assert.equal(disconnect.renderedMarkerRemoved, true);
    assert.equal(disconnect.clearReason, 'disconnect');
    assert.notEqual(disconnect.sessionId, oldSession);
    await owner.page.waitForFunction(oldSession => window.game.client.connected()
      && window.game.renderer.state === 'ready' && window.game.adapter.latest?.sessionId !== oldSession, oldSession,
    { timeout: 30000 });
    const reconnected = await snapshot(owner.page);
    await requirePrivacy(owner.page, reconnected.frame.session.actorSeat);
    assert.notEqual(reconnected.frame.sessionId, before.frame.sessionId);
    await record('actual-seat-revocation-reconnect', owner.page, {
      actualAuthorityEpochsChanged: true, oldPrivateEpochNotRetained: true,
      clearReasons: reconnected.clearReasons, reconnectActorSeat: reconnected.frame.session.actorSeat,
      actualDisconnectRevocation: disconnect,
      midHandLeaveSeatObservedAuthority: retained.frame.session.actorSeat,
    });
    for (const page of [owner.page, guest, spectator]) await page.context().close();
  });
  for (const variant of ['FOUR_PLAYER', 'THREE_PLAYER', 'BAMBOO', 'MINEFIELD']) {
    await caseRun(`actual-${variant}-relay-native-input`, async () => {
      const table = await newTable({ variant: variant.toLowerCase(), bots: 0 });
      currentPage = table.page;
      await table.page.locator('.seat-button-0 .take-seat').click();
      await table.page.waitForFunction(() => window.game.client.seat === 0);
      await table.page.locator('#toggle-setup').click();
      await table.page.locator('#points').selectOption('35');
      await table.page.locator('#reset-points').click();
      await table.page.waitForFunction(() => window.game.renderer.state === 'ready'
        && window.game.adapter.latest.entities.some(entity => entity.kind === 'STICK'));
      await table.page.locator('#toggle-setup').click();
      await table.page.locator('#deal').click();
      await table.page.waitForFunction(variant => window.game.renderer.state === 'failed'
        || window.game.adapter.latest?.match?.conditions.gameType === variant
          && window.game.adapter.latest.entities.some(entity => entity.kind === 'STICK')
          && window.game.adapter.latest.entities.some(entity =>
            entity.group === (variant === 'MINEFIELD' ? 'wall.open' : 'hand') && entity.seat === 0), variant);
      await table.page.waitForTimeout(250);
      let state = await snapshot(table.page);
      assert.equal(state.state, 'ready', state.statusText);
      assert.equal(state.frame.session.mode, 'relay');
      assert.equal(state.layout.gameType, variant);
      const tile = state.frame.entities.find(entity =>
        entity.group === (variant === 'MINEFIELD' ? 'wall.open' : 'hand')
        && entity.seat === 0 && entity.interaction.canFlip);
      assert(tile, 'The actual variant deal did not produce a flippable own-hand tile');
      const point = await pointAt(table.page, tile);
      let start = (await snapshot(table.page)).actions.length;
      await table.page.mouse.click(point.x, point.y, { button: 'right', delay: 55 });
      const flip = await observeAction(table.page, start, 'flip', tile.entityId);
      await table.page.waitForTimeout(200);
      state = await snapshot(table.page);
      assert.notEqual(state.frame.entities.find(entity => entity.entityId === tile.entityId).rotationIndex, tile.rotationIndex);
      const stick = state.frame.entities.find(entity => entity.kind === 'STICK' && entity.seat === 0
        && entity.interaction.canSelect);
      assert(stick, 'The actual Reset points control must produce native selectable scoring sticks');
      const stickPoint = await pointAt(table.page, stick);
      start = (await snapshot(table.page)).actions.length;
      await table.page.mouse.click(stickPoint.x, stickPoint.y, { delay: 55 });
      const stickSelect = await observeAction(table.page, start, 'select', stick.entityId);
      receipt.transcript.push({ room: table.room, operation: 'relay-native-flip-and-scoring-stick',
        variant, nativeActions: [flip, stickSelect], scoringStickValue: stick.appearance.valueIndex,
        sourceFlipSlot: tile.slotName, actualResetPointsClicked: true });
      let keyboardFlip = null;
      if (variant === 'FOUR_PLAYER') {
        await pointAt(table.page, state.frame.entities.find(entity => entity.entityId === tile.entityId));
        const selectStart = (await snapshot(table.page)).actions.length;
        const selectedPoint = await pointAt(table.page,
          (await snapshot(table.page)).frame.entities.find(entity => entity.entityId === tile.entityId));
        await table.page.mouse.click(selectedPoint.x, selectedPoint.y, { delay: 55 });
        await observeAction(table.page, selectStart, 'select', tile.entityId);
        await table.page.waitForTimeout(150);
        state = await snapshot(table.page);
        const beforeKeyboard = state.frame.entities.find(entity => entity.entityId === tile.entityId);
        const poseCount = state.layout.slots.find(slot => slot.slotName === beforeKeyboard.slotName).poses.length;
        const keyboardStart = state.actions.length;
        await table.page.locator('#unity-canvas').focus();
        await table.page.keyboard.down('f');
        await table.page.waitForTimeout(55);
        await table.page.keyboard.up('f');
        await table.page.waitForTimeout(200);
        state = await snapshot(table.page);
        const afterKeyboard = state.frame.entities.find(entity => entity.entityId === tile.entityId);
        keyboardFlip = {
          before: beforeKeyboard.rotationIndex, after: afterKeyboard.rotationIndex,
          expected: (beforeKeyboard.rotationIndex + 1) % poseCount,
          nativeActions: state.actions.slice(keyboardStart).filter(action => action.intent.kind === 'flip'),
        };
        receipt.transcript.push({ room: table.room, operation: 'single-F-key-flip', ...keyboardFlip });
      }
      const target = state.layout.slots.find(slot => slot.group === 'discard' && slot.seat === 0
        && !state.frame.entities.some(entity => entity.slotName === slot.slotName));
      assert(target);
      const anchor = state.frame.entities.find(entity => entity.entityId === tile.entityId);
      const from = await pointAt(table.page, anchor);
      const to = aimPoint(state, target), box = await table.page.locator('#unity-canvas').boundingBox();
      start = (await snapshot(table.page)).actions.length;
      try {
        await table.page.mouse.move(from.x, from.y);
        await table.page.mouse.down();
        await table.page.waitForTimeout(45);
        await table.page.mouse.move(from.x + 15, from.y, { steps: 2 });
        await table.page.waitForTimeout(60);
        await table.page.mouse.move(box.x + to.x, box.y + to.y, { steps: 4 });
        await table.page.waitForTimeout(120);
      } finally {
        await table.page.mouse.up();
      }
      const begin = await observeAction(table.page, start, 'beginDrag', anchor.entityId);
      const end = await observeAction(table.page, start, 'endDrag');
      const nativeGesture = await snapshot(table.page);
      const drag = nativeGesture.actions.slice(start).find(action =>
        action.intent.kind === 'dragToSlot' && action.intent.slotName === target.slotName);
      receipt.transcript.push({
        room: table.room, variant, operation: 'relay-native-uninterrupted-mouse-drag',
        inputMethod: '45ms down, 60ms first motion, 120ms target; no probe snapshots or action polling while button held',
        anchorSourceSlot: anchor.slotName, requestedSourceSlot: target.slotName,
        nativeActions: nativeGesture.actions.slice(start),
        nativeResults: nativeGesture.results.filter(result =>
          nativeGesture.actions.slice(start).some(action => action.actionId === result.actionId)),
        actionContextEvidence: nativeGesture.actionContextEvidence,
        dragFrameEvidence: nativeGesture.dragFrameEvidence,
        sourceSlotReached: nativeGesture.frame.entities.some(entity =>
          entity.entityId === tile.entityId && entity.slotName === target.slotName),
      });
      fs.writeFileSync(`${probeRoot}/progress.json`, JSON.stringify({
        stage: receipt.stage, cases: receipt.cases, caseFailures: receipt.caseFailures,
        transcript: receipt.transcript,
      }, null, 2) + '\n');
      assert.equal(end.intent.commit, true,
        'An uninterrupted owned relay drag must remain authorized after its own claim and commit on real mouse release');
      assert(drag,
        'Real C# picking must reach the requested valid source-slot target');
      if (keyboardFlip) assert.equal(keyboardFlip.after, keyboardFlip.expected,
        'A single retained F-key gesture must advance the tile once, not both browser and C# handlers');
      await table.page.waitForFunction(({ id, slot }) => window.game.renderer.state === 'failed'
        || window.game.adapter.latest?.entities.some(entity => entity.entityId === id && entity.slotName === slot),
      { id: tile.entityId, slot: target.slotName });
      state = await snapshot(table.page);
      assert.equal(state.state, 'ready', state.statusText);
      assert(commands(table.room, 'things').some(entry => entry[2]?.slotName === target.slotName));
      if (variant === 'MINEFIELD') assert(state.layout.slots.some(slot => slot.group === 'wall.open'));
      if (variant === 'THREE_PLAYER') assert(state.layout.slots.some(slot => slot.group === 'kita'));
      await record(`actual-${variant}-relay-native-input`, table.page, {
        nativeActions: [flip, begin, drag, end, stickSelect],
        sourceSlotDragTarget: target.slotName, scoringStickValue: stick.appearance.valueIndex,
        singleFKeyGesture: keyboardFlip,
        serverOutcomeVerified: true, nativeWireVerified: true,
      });
      await table.page.context().close();
    });
  }
  assert.equal(receipt.caseFailures.length, 0, JSON.stringify(receipt.caseFailures.map(f => ({ name: f.name, error: f.error }))));
  assert.equal(receipt.pageErrors.length, 0, JSON.stringify(receipt.pageErrors));
  receipt.status = 'passed';
  receipt.stage = 'completed';
} catch (error) {
  receipt.status = 'failed';
  receipt.failure = { stage: receipt.stage, name: error.name, message: error.message.slice(0, 6000) };
  process.exitCode = 1;
} finally {
  await finishPlayerBrowser();
}

import test from 'node:test';
import assert from 'node:assert/strict';
import { gameHarness, plain, thing } from './unity-test-support.mjs';

function router(h) {
  const results = [], notices = [], failures = [];
  const { RendererActionRouter } = h.load('renderer/renderer-actions.ts');
  const actions = new RendererActionRouter(h.adapter, {
    result: result => results.push(result), notice: message => notices.push(message),
    unavailable: message => { failures.push(message); h.adapter.fail(message); },
  });
  let counter = 0;
  const action = (intent, overrides = {}) => ({
    contractVersion: '1.0.0', instanceId: h.adapter.instanceId, sessionId: h.adapter.sessionId,
    type: 'action', actionId: 'action-' + ++counter, contextFrameId: h.adapter.latest.frameId, intent, ...overrides,
  });
  const send = (intent, overrides) => actions.handle(action(intent, overrides));
  const wire = () => h.sockets.at(-1).sent.flatMap(message => message.type === 'UPDATE' ? message.entries : []);
  return { actions, action, send, wire, results, notices, failures };
}

test('Unity discard resolves a live entitled own tile to the exact unchanged source wire and never moves it optimistically', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  const frame = h.snapshot([['4', thing('hand.0@0', 1)], ['h_other', thing('hand.0@1', null, 2)]]);
  const own = frame.entities.find(e => e.slotName === 'hand.0@0');
  const r = router(h);
  h.sockets.at(-1).sent.length = 0;
  const result = r.send({ kind: 'discard', entityId: own.entityId });
  assert.equal(result.status, 'submitted');
  assert.deepEqual(r.wire().filter(([kind]) => kind === 'discard'), [['discard', 0, { tileId: 4 }]]);
  assert.equal(h.world.things.get(4).slot.name, 'hand.0@0');
  assert.equal(h.world.things.get(4).claimedBy, null);
  assert.equal(r.send({ kind: 'discard', entityId: '4' }).status, 'rejected', 'raw physical IDs are not entity IDs');
});

test('camera contact gate explicitly rejects native game intents but allows stable hover and a fresh real discard afterward', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  const frame = h.snapshot([['4', thing('hand.0@0', 1)]]);
  const own = frame.entities[0];
  const r = router(h);
  h.sockets.at(-1).sent.length = 0;
  assert.equal(r.actions.handle(r.action({ kind: 'hover', entityId: own.entityId }), true).status, 'local');
  assert.equal(r.actions.handle(r.action({ kind: 'select', entityIds: [] }), true).status, 'local',
    'Escape/local cancellation must remain available during camera navigation');
  for (const intent of [
    { kind: 'discard', entityId: own.entityId }, { kind: 'select', entityIds: [own.entityId] },
    { kind: 'rollDice' }, { kind: 'flip', entityIds: [own.entityId], direction: 1, animated: true },
  ]) {
    const result = r.actions.handle(r.action(intent), true);
    assert.equal(result.status, 'rejected');
    assert.equal(result.reason, 'not-actionable');
  }
  assert.equal(r.wire().length, 0, 'a camera release cannot become any native wire write');
  assert.equal(r.send({ kind: 'discard', entityId: own.entityId }).status, 'submitted');
  assert.deepEqual(r.wire(), [['discard', 0, { tileId: 4 }]]);
});

test('manual pickup uses the single live uncovered trigger and emits only seatIndex/count', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  let frame = h.snapshot([
    ['h_trigger', thing('wall.0.1@0', null)], ['h_bottom', thing('wall.0.0@0', null)],
    ['h_next', thing('wall.1.1@0', null)],
  ], { phase: 'PickupRound1', pickup: { phase: 'PickupRound1', seatIndex: 0, count: 4, dealMode: 'manual', targetSlots: ['wall.0.1@0'] } });
  const r = router(h);
  const trigger = frame.entities.find(e => e.slotName === 'wall.0.1@0');
  assert.equal(trigger.interaction.canPickup, true);
  assert.equal(frame.entities.find(e => e.slotName === 'wall.0.0@0').interaction.canPickup, false);
  h.sockets.at(-1).sent.length = 0;
  assert.equal(r.send({ kind: 'takePickup', triggerEntityId: trigger.entityId }).status, 'submitted');
  assert.deepEqual(r.wire().filter(([kind, key]) => kind === 'pickup' && key === 'take'), [['pickup', 'take', { seatIndex: 0, count: 4 }]]);
  const next = frame.entities.find(e => e.slotName === 'wall.1.1@0');
  assert.equal(r.send({ kind: 'takePickup', triggerEntityId: next.entityId }).status, 'rejected');
  const oldContext = frame.frameId;
  h.delta([['turn', 'current', { activeSeat: 0, phase: 'AwaitingDiscard', awaitingDiscard: true }]]);
  assert.equal(r.send({ kind: 'takePickup', triggerEntityId: trigger.entityId }, { contextFrameId: oldContext }).status, 'rejected');
  assert.equal(r.wire().filter(([kind, key]) => kind === 'pickup' && key === 'take').length, 1);
});

test('dice is dealer/manual/authoritative-phase gated and keeps the existing rollDice command', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  h.snapshot([], { phase: 'RollingDice', activeSeat: 0 });
  const r = router(h);
  h.sockets.at(-1).sent.length = 0;
  assert.equal(r.send({ kind: 'rollDice' }).status, 'submitted');
  assert.deepEqual(r.wire().filter(([kind]) => kind === 'pickup'), [['pickup', 'rollDice', { seatIndex: 0 }]]);
  h.delta([['turn', 'current', { activeSeat: null, phase: 'Scoring', awaitingDiscard: false }]]);
  assert.equal(r.send({ kind: 'rollDice' }).status, 'rejected');
  assert.equal(r.wire().filter(([kind]) => kind === 'pickup').length, 1);
});

test('duplicate IDs, stale contexts, revoked sessions and spectator viewpoints cannot emit new commands', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  const frame = h.snapshot([['4', thing('hand.0@0', 1)]]);
  const r = router(h);
  const action = r.action({ kind: 'discard', entityId: frame.entities[0].entityId });
  h.sockets.at(-1).sent.length = 0;
  assert.equal(r.actions.handle(action).status, 'submitted');
  assert.equal(r.actions.handle(action).status, 'ignored');
  assert.equal(r.results.length, 1, 'duplicate callback cannot produce duplicate Unity action results');
  assert.equal(r.wire().filter(([kind]) => kind === 'discard').length, 1);
  h.delta([['things', '4', null]]);
  assert.equal(r.actions.handle({ ...action, actionId: 'tombstoned' }).status, 'rejected');
  h.delta([['seats', 'verified-player', { seat: null }]], { actor: null, rev: 2 });
  assert.equal(r.actions.handle({ ...action, actionId: 'old-session' }).reason, 'stale-session');
  h.snapshot([['h_concealed', thing('hand.0@0', null, 2)]], { actor: null, rev: 2, activeSeat: 0 });
  h.world.seat = 0;
  assert.equal(r.send({ kind: 'discard', entityId: h.adapter.latest.entities[0].entityId }).status, 'rejected');
  assert.equal(r.wire().filter(([kind]) => kind === 'discard').length, 1);
});

test('animation-only newer frames retain a known actionable context, while a moved source slot invalidates it', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  const frame = h.snapshot([['4', thing('hand.0@0', 1)]]);
  const r = router(h);
  h.adapter.setPresentation({ ...frame.presentation, zoom: 0.25 });
  h.adapter.refresh();
  assert.equal(r.send({ kind: 'discard', entityId: frame.entities[0].entityId }, { contextFrameId: frame.frameId }).status, 'submitted');
  h.delta([['things', '4', thing('discard.0.0@0', 1)]]);
  assert.equal(r.send({ kind: 'discard', entityId: frame.entities[0].entityId }, { contextFrameId: frame.frameId }).reason, 'stale-entity');
});

test('malformed envelopes and arbitrary UPDATE/seat/tile payloads are rejected visibly before the source pipeline', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  h.snapshot([['4', thing('hand.0@0', 1)]]);
  const r = router(h);
  h.sockets.at(-1).sent.length = 0;
  const good = r.action({ kind: 'discard', entityId: h.adapter.latest.entities[0].entityId });
  assert.equal(r.actions.handle({ ...good, intent: { ...good.intent, tileId: 4, seat: 0 } }), null);
  assert.equal(r.failures.length, 1);
  assert.equal(r.wire().length, 0);
  assert.equal(h.adapter.latest, null);
  assert.equal(r.actions.handle({ ...good, type: 'UPDATE', entries: [['things', 4, {}]] }), null);
});

for (const slotName of ['hand.0@0', 'hand.extra@0']) {
  test(`actual relay ${slotName} drag survives its own claim through release in every variant`, t => {
    for (const variant of ['FOUR_PLAYER', 'THREE_PLAYER', 'BAMBOO', 'MINEFIELD']) {
      const h = gameHarness(variant);
      t.after(h.close);
      h.join();
      h.snapshot([]);
      const source = variant === 'MINEFIELD'
        ? slotName === 'hand.0@0' ? 'wall.open.0.0@0' : 'wall.open.0.1@0'
        : slotName === 'hand.extra@0' && !h.world.slots.has(slotName) ? 'hand.13@0' : slotName;
      const group = variant === 'MINEFIELD' ? 'wall.open' : 'hand';
      const sourceSlots = [...h.world.slots.values()].filter(slot => slot.group === group && slot.seat === 0).slice(0, 14);
      if (!sourceSlots.some(slot => slot.name === source)) sourceSlots[13] = h.world.slots.get(source);
      const entries = sourceSlots.map((slot, id) => [id, thing(slot.name, undefined, 1)]);
      const frame = h.snapshot(entries);
      const anchor = frame.entities.find(e => e.slotName === source);
      const key = entries.find(([, info]) => info.slotName === source)[0];
      const r = router(h);
      h.sockets.at(-1).sent.length = 0;
      assert.equal(r.send({ kind: 'beginDrag', gestureId: 'gesture-one', anchorEntityId: anchor.entityId, entityIds: [anchor.entityId] }).status, 'submitted');
      const held = h.world.things.get(key);
      assert.equal(held.claimedBy, 0);
      const claim = r.wire().filter(([kind, id]) => kind === 'things' && id === key).at(-1);
      assert.equal(claim[2].claimedBy, 0, 'the real retained beginDrag caller authors the exclusive claim');
      h.delta([claim]);
      const claimed = h.adapter.latest.entities.find(e => e.entityId === anchor.entityId);
      assert.equal(claimed.interaction.canDrag, true, `${variant}: the actual claimed frame must not cancel the pressed gesture`);
      assert.equal(claimed.interaction.canSelect, false, 'an active claim does not authorize a new selection');
      assert.equal(claimed.interaction.canFlip, false, 'holding does not authorize flipping');
      assert.equal(claimed.display.held, true);
      assert.equal(h.world.handPointerDown, true);
      assert.notEqual(h.world.heldMouse, null);
      assert.equal(held.slot.name, source, 'acquiring the claim must not move the source slot');
      assert.equal(r.wire().some(([kind]) => kind === 'discard'), false);
      assert.equal(r.send({ kind: 'dragToSlot', gestureId: 'gesture-one', slotName: 'discard.0.0@0' }).status, 'submitted');
      const whilePressed = r.wire().filter(([kind, id]) => kind === 'things' && id === key);
      assert.equal(whilePressed.some(([, , info]) => info.slotName === 'discard.0.0@0'), false);
      h.adapter.refresh();
      assert.equal(h.adapter.latest.entities.find(e => e.entityId === anchor.entityId).interaction.canDrag, true);
      assert.equal(r.send({ kind: 'endDrag', gestureId: 'gesture-one', commit: true }).status, 'submitted');
      const moved = h.world.things.get(key);
      assert.equal(moved.slot.name, 'discard.0.0@0');
      assert.equal(moved.slot.thing, moved);
      assert.equal(moved.claimedBy, null);
      assert.equal(h.adapter.latest.entities.find(e => e.slotName === 'discard.0.0@0').entityId, anchor.entityId,
        'relay source-local movement keeps opaque identity and geometry until server echo');
      assert.equal(r.wire().filter(([kind, id, info]) => kind === 'things' && id === key
        && info.slotName === 'discard.0.0@0' && info.claimedBy === null).length, 1,
      'one release authors exactly one requested source-slot mutation');
      assert.equal(h.world.handPointerDown, false);
      assert.equal(h.world.heldMouse, null);
      assert.equal(r.send({ kind: 'endDrag', gestureId: 'gesture-one', commit: true }).status, 'rejected');
    }
  });
}

test('actual relay multiselect flip, shifts and cancel finish source state without stale tokens', t => {
  const h = gameHarness('FOUR_PLAYER');
  t.after(h.close);
  h.join();
  const frame = h.snapshot([[0, thing('hand.0@0', undefined, 1)], [1, thing('hand.1@0', undefined, 1)], [2, thing('hand.2@0', undefined, 1)]]);
  const r = router(h);
  const ids = frame.entities.filter(e => e.group === 'hand').map(e => e.entityId);
  assert.equal(r.send({ kind: 'flip', entityIds: ids.slice(0, 2), direction: 1, animated: false }).status, 'submitted');
  assert.equal(h.world.things.get(0).rotationIndex, 2);
  assert.equal(h.world.things.get(1).rotationIndex, 2);
  const latest = h.adapter.latest;
  const anchor = latest.entities.find(e => e.slotName === 'hand.0@0');
  assert.equal(r.send({ kind: 'beginDrag', gestureId: 'g', anchorEntityId: anchor.entityId, entityIds: [anchor.entityId] }).status, 'submitted');
  const unheld = h.world.things.get(2);
  h.delta([['things', 2, { ...thing('hand.2@0', undefined, 1), claimedBy: 0 }]]);
  assert.equal(h.world.rendererInteraction(unheld).canDrag, false, 'another self-claim is not part of this active gesture');
  assert.equal(h.world.rendererCanSelect(unheld), false, 'self ownership alone must never grant new selection');
  assert.equal(h.world.rendererInteraction(h.world.things.get(0)).canDrag, false,
    'an unrelated held self-claim must not be swept into the retained Movement');
  h.delta([['things', 2, thing('hand.2@0', undefined, 1)]]);
  assert.equal(r.send({ kind: 'dragToSlot', gestureId: 'different', slotName: 'hand.1@0' }).status, 'rejected');
  assert.equal(r.send({ kind: 'dragToSlot', gestureId: 'g', slotName: 'hiddenpool@0' }).status, 'rejected');
  assert.equal(r.send({ kind: 'dragToSlot', gestureId: 'g', slotName: 'hand.1@0' }).status, 'submitted');
  r.actions.cancelGesture();
  assert.equal(h.world.things.get(0).slot.name, 'hand.0@0');
  assert.equal([...h.world.things.values()].every(t => t.claimedBy === null), true);
  assert.equal(r.send({ kind: 'endDrag', gestureId: 'g', commit: true }).status, 'rejected');
});

test('point sticks use the actual source Movement/tray/payment operations and never a tile-only path', t => {
  const h = gameHarness('FOUR_PLAYER');
  t.after(h.close);
  h.join();
  const frame = h.snapshot([[1000, thing('tray.0.0@0', undefined)]]);
  const stick = frame.entities[0];
  assert.equal(stick.kind, 'STICK');
  assert.equal(typeof stick.appearance.valueIndex, 'number');
  const r = router(h);
  assert.equal(r.send({ kind: 'beginDrag', gestureId: 'payment', anchorEntityId: stick.entityId, entityIds: [stick.entityId] }).status, 'submitted');
  assert.equal(r.send({ kind: 'dragToSlot', gestureId: 'payment', slotName: 'payment.0@2' }).status, 'submitted');
  assert.equal(r.send({ kind: 'endDrag', gestureId: 'payment', commit: true }).status, 'submitted');
  assert.equal(h.world.things.get(1000).slot.name, 'payment.0@2');
});

test('foreign claims, privacy loss, authority changes and removal revoke actual relay gestures without stale writes', t => {
  for (const loss of ['foreign', 'hidden', 'moved', 'pointer', 'disconnect', 'spectator', 'room']) {
    const h = gameHarness('FOUR_PLAYER');
    t.after(h.close);
    h.join();
    const frame = h.snapshot([[0, thing('hand.0@0', undefined, 1)], [1, thing('hand.1@0', undefined, 1)]]);
    const r = router(h);
    const ids = frame.entities.map(e => e.entityId);
    assert.equal(r.send({ kind: 'select', entityIds: ids }).status, 'local');
    assert.equal(r.send({ kind: 'beginDrag', gestureId: 'pressed', anchorEntityId: ids[0], entityIds: ids }).status, 'submitted');
    const claims = r.wire().filter(([kind]) => kind === 'things');
    h.delta(claims);
    assert.equal(h.adapter.latest.entities.filter(e => ids.includes(e.entityId)).every(e => e.interaction.canDrag), true);
    const release = r.action({ kind: 'endDrag', gestureId: 'pressed', commit: true });
    const held = h.world.things.get(0);
    if (loss === 'foreign') h.delta([['things', 0, { ...thing('hand.0@0', undefined, 1), claimedBy: 1 }]]);
    if (loss === 'hidden') { held.hidden = true; h.adapter.refresh(); }
    if (loss === 'moved') h.delta([['things', 0, { ...thing('discard.0.0@0', undefined, 1), claimedBy: 0 }]]);
    if (loss === 'pointer') h.world.setHandPointerDown(false);
    if (loss === 'disconnect') h.client.disconnect();
    if (loss === 'spectator') h.delta([['seats', 'verified-player', { seat: null }]]);
    if (loss === 'room') h.join('other-room');
    assert.equal(h.world.rendererInteraction(held).canDrag, false, loss);
    if (['foreign', 'hidden', 'moved', 'pointer'].includes(loss)) {
      assert.equal(h.world.rendererInteraction(h.world.things.get(1)).canDrag, false,
        'loss of one exclusively held member revokes the whole multiselect gesture');
    }
    h.sockets.at(-1).sent.length = 0;
    const slotsBeforeCancel = new Map([...h.world.things].map(([key, thing]) => [key, thing.slot.name]));
    r.actions.cancelGesture();
    assert.equal(r.actions.handle(release).status === 'submitted', false);
    assert.equal(r.wire().filter(([kind]) => kind === 'things')
      .every(([, key, info]) => info.slotName === slotsBeforeCancel.get(key)), true,
    'cancel only releases local claims; it must not move any source slot');
    if (loss === 'foreign') {
      assert.equal(held.claimedBy, 1, 'cancel must not release a foreign claim');
      assert.equal(h.world.rendererCanSelect(held), false);
    }
    if (loss === 'disconnect' || loss === 'spectator' || loss === 'room') {
      assert.equal(r.wire().length, 0, 'revocation must not author stale relay writes');
      assert.equal(h.world.heldMouse, null);
      assert.equal(h.world.handPointerDown, false);
    }
  }
  const h = gameHarness('FOUR_PLAYER');
  t.after(h.close);
  h.join();
  const frame = h.snapshot([[0, thing('hand.0@0', undefined, 1)], [1, thing('hand.1@0', undefined, 1)]]);
  const r = router(h);
  const anchor = frame.entities.find(e => e.slotName === 'hand.0@0');
  assert.equal(r.send({ kind: 'beginDrag', gestureId: 'old', anchorEntityId: anchor.entityId, entityIds: [anchor.entityId] }).status, 'submitted');
  h.delta([['things', 0, null]]);
  h.sockets.at(-1).sent.length = 0;
  assert.equal(r.send({ kind: 'endDrag', gestureId: 'old', commit: true }).status, 'rejected');
  assert.equal(r.wire().some(([kind, key]) => kind === 'things' && key === 0), false);
  const next = h.adapter.latest.entities.find(e => e.slotName === 'hand.1@0');
  assert.equal(r.send({ kind: 'beginDrag', gestureId: 'new', anchorEntityId: next.entityId, entityIds: [next.entityId] }).status, 'submitted');
  r.actions.cancelGesture();
});

test('an advertised own-turn runtime version invalidates old Changsha contexts without being confused with viewer revision or added to wire', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join('room-a', 0, 17);
  const frame = h.snapshot([['4', thing('hand.0@0', 1)]], {
    extra: [['ownTurn', 0, { gameId: 'room-a', stateVersion: 703, hu: false, concealedKongs: [], addedKongs: [] }]],
  });
  const r = router(h);
  h.delta([['ownTurn', 0, { gameId: 'room-a', stateVersion: 704, hu: false, concealedKongs: [], addedKongs: [] }]]);
  h.sockets.at(-1).sent.length = 0;
  assert.equal(r.send({ kind: 'discard', entityId: frame.entities[0].entityId }, { contextFrameId: frame.frameId }).status, 'rejected');
  assert.equal(r.wire().length, 0);
  assert.equal(h.adapter.latest.session.viewerRevision, 17);
  assert.equal(JSON.stringify(h.adapter.latest).includes('704'), false);
});

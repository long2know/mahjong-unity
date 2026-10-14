import test from 'node:test';
import assert from 'node:assert/strict';
import { gameHarness, plain, thing, sourceLoader } from './unity-test-support.mjs';

test('actual model placeholders never cross the bridge before a bound FULL snapshot', t => {
  const h = gameHarness();
  t.after(h.close);
  assert.equal(h.world.things.size, 108);
  let frame = h.adapter.refresh();
  assert.equal(frame.session.snapshotReady, false);
  assert.equal(frame.entities.length, 0);
  h.join();
  frame = h.adapter.refresh();
  assert.equal(frame.session.actorSeat, null);
  assert.equal(frame.entities.length, 0);
  h.snapshot([['4', thing('hand.0@0', 1)]], { bound: false });
  assert.equal(h.adapter.latest.session.snapshotReady, false);
  h.delta([['turn', 'current', { activeSeat: 0, phase: 'AwaitingDiscard', awaitingDiscard: true }]]);
  assert.equal(h.adapter.latest.session.snapshotReady, false, 'a partial turn cannot promote an unbound FULL');
  frame = h.snapshot([['4', thing('hand.0@0', 1)]]);
  assert.equal(frame.entities.length, 1);
  assert.equal(frame.session.snapshotReady, true);
  assert.deepEqual(h.failures, []);
});

test('complete DTOs use exact frozen fields, entitled numeric-string faces and private random IDs', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join('room-a', 0, 17);
  const frame = h.snapshot([
    ['4', thing('hand.0@0', 1)], ['8', thing('hand.1@0', null)],
    ['h_private_wire_handle', thing('hand.0@1', null, 2)], ['h_wall_handle', thing('wall.0.1@0', null)],
    ['72', thing('discard.0.0@2', 18)],
  ], { extra: [['ownTurn', 0, { gameId: 'room-a', stateVersion: 703, hu: false, concealedKongs: [], addedKongs: [] }]] });
  const { assertDto } = h.load('renderer/renderer-contract.ts');
  assert.doesNotThrow(() => assertDto('RendererFrame', frame));
  assert.doesNotThrow(() => assertDto('RendererLayout', h.adapter.layout));
  assert.equal(frame.session.viewerRevision, 17);
  assert.equal(frame.entities.length, 5);
  assert.equal(frame.entities.find(e => e.slotName === 'hand.0@0').appearance.faceIndex, 1);
  for (const slot of ['hand.1@0', 'hand.0@1', 'wall.0.1@0']) {
    const entity = frame.entities.find(e => e.slotName === slot);
    assert.deepEqual(plain(entity.appearance), { faceIndex: null, knownFace: false, showFace: false, backIndex: 0, valueIndex: null });
  }
  assert.equal(frame.localHand.entityIds.length, 1, 'unknown own tiles cannot enter known-face hand sorting');
  for (const entity of frame.entities) {
    assert.match(entity.entityId, /^entity_[0-9a-f]{32}$/);
    assert.deepEqual(Object.keys(entity).sort(), [
      'entityId', 'kind', 'slotName', 'group', 'seat', 'indexes', 'rotationIndex', 'pose', 'scale', 'appearance', 'display', 'interaction',
    ].sort());
  }
  const assertPrivateFixtureDataAbsent = candidate => {
    const json = JSON.stringify(candidate, (_key, value) => {
      assert.notEqual(value, 703, 'private runtime version must not cross the bridge');
      assert.notEqual(value, '703', 'private runtime version must not cross the bridge');
      return value;
    });
    for (const hidden of ['h_private_wire_handle', 'h_wall_handle', 'heldRotation', 'claimedBy', 'tileId', 'stateVersion', 'resultToken']) {
      assert.equal(json.includes(hidden), false, hidden);
    }
  };
  assertPrivateFixtureDataAbsent(frame);
  // An opaque random ID may contain the same digits without exposing a numeric wire value.
  const collision = plain(frame);
  const previousId = collision.entities[0].entityId;
  collision.entities[0].entityId = `entity_${'703'.padEnd(32, '0')}`;
  collision.localHand.entityIds = collision.localHand.entityIds.map(id =>
    id === previousId ? collision.entities[0].entityId : id);
  assert.doesNotThrow(() => assertDto('RendererFrame', collision));
  assert.doesNotThrow(() => assertPrivateFixtureDataAbsent(collision));
  for (const leaked of [703, '703']) {
    const corrupted = plain(frame);
    corrupted.session.viewerRevision = leaked;
    assert.throws(() => assertPrivateFixtureDataAbsent(corrupted), /private runtime version/);
  }
  assert.equal(h.adapter.layout.slots.some(s => s.slotName === 'hiddenpool@0'), false);
  assert.deepEqual(h.failures, []);
});

test('a spectator viewpoint and local show-all preference cannot grant actor authority or hidden faces', t => {
  const h = gameHarness('CHANGSHA', { spectator: true });
  t.after(h.close);
  h.join('room-a', null);
  h.snapshot([['h_foreign', thing('hand.0@2', null, 2)], ['72', thing('discard.0.0@1', 18)]], { activeSeat: 2 });
  const id = h.adapter.latest.entities.find(e => e.slotName === 'hand.0@2').entityId;
  h.world.seat = 2;
  const frame = h.adapter.refresh();
  assert.equal(h.client.seat, null);
  assert.equal(h.world.seat, null);
  assert.equal(frame.session.actorSeat, null);
  assert.equal(frame.session.viewSeat, 2);
  assert.equal(frame.entities.find(e => e.entityId === id).appearance.faceIndex, null);
  assert.equal(frame.entities.every(e => Object.values(e.interaction).every(v => v === false)), true);
  assert.deepEqual(plain(frame.localHand), { seat: null, entityIds: [], drawnEntityId: null });
  assert.equal(Object.isFrozen(h.client.viewerAuthority), true);
});

test('delta tombstones, full omissions and hidden-to-real reveals remove old private identities', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  let frame = h.snapshot([['h_old', thing('wall.0.1@0', null)], ['4', thing('hand.0@0', 1)]]);
  const hiddenId = frame.entities.find(e => e.slotName === 'wall.0.1@0').entityId;
  const ownId = frame.entities.find(e => e.slotName === 'hand.0@0').entityId;
  const oldContext = frame.frameId;
  frame = h.delta([['things', 'h_old', null]]);
  assert.equal(frame.entities.some(e => e.entityId === hiddenId), false);
  assert.equal(h.adapter.resolveEntity(hiddenId, oldContext), null);
  assert.equal(frame.entities[0].entityId, ownId);
  frame = h.snapshot([['12', thing('discard.0.0@0', 3)]]);
  assert.equal(frame.entities.length, 1);
  assert.equal(frame.entities.some(e => e.entityId === ownId), false);
  assert.equal(h.adapter.resolveEntity(ownId, oldContext), null);
  frame = h.snapshot([['h_reveal', thing('wall.1.1@1', null)]]);
  const anonymous = frame.entities[0].entityId;
  frame = h.snapshot([['16', thing('hand.0@0', 4)]]);
  assert.notEqual(frame.entities[0].entityId, anonymous, 'reveals never guess/link a hidden pool index to a physical ID');
  assert.equal(frame.entities[0].appearance.faceIndex, 4);
});

test('authority revision/seat loss blanks cached entitlements until a new FULL and remaps IDs', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  const frame = h.snapshot([['4', thing('hand.0@0', 1)]]);
  const { sessionId, frameId } = frame;
  const id = frame.entities[0].entityId;
  h.delta([['turn', 'current', { activeSeat: 0, phase: 'AwaitingDiscard', awaitingDiscard: true }]], { rev: 2 });
  assert.notEqual(h.adapter.sessionId, sessionId);
  assert.equal(h.world.rendererSnapshotReady(), true, 'same-seat revision changes cannot rely on the World seat gate');
  assert.equal(h.adapter.latest.session.snapshotReady, false);
  assert.equal(h.adapter.latest.entities.length, 0);
  assert.equal(h.adapter.actionable(), false);
  assert.equal(h.adapter.resolveEntity(id, frameId), null);
  assert.equal(h.adapter.refresh().entities.length, 0, 'animation refresh cannot promote cached private faces');
  const rebound = h.snapshot([['4', thing('hand.0@0', 1)]], { rev: 2 });
  assert.equal(rebound.session.snapshotReady, true);
  assert.equal(h.adapter.actionable(), true);
  assert.notEqual(rebound.entities[0].entityId, id);
  h.delta([['turn', 'current', { activeSeat: null, phase: 'Scoring', awaitingDiscard: false }]], { actor: null, rev: 3 });
  assert.notEqual(h.adapter.sessionId, rebound.sessionId);
  assert.equal(h.adapter.latest.session.snapshotReady, false);
  assert.equal(h.adapter.latest.entities.length, 0);
  assert.equal(h.adapter.actionable(), false);
  assert.equal(h.adapter.resolveEntity(rebound.entities[0].entityId, rebound.frameId), null);
  const refreshed = h.snapshot([['h_now_hidden', thing('hand.0@0', null, 2)]], { actor: null, rev: 3, phase: 'Scoring', activeSeat: null });
  assert.equal(refreshed.session.actorSeat, null);
  assert.notEqual(refreshed.entities[0].entityId, id);
  h.client.disconnect();
  assert.equal(h.adapter.latest, null);
  assert.equal(h.sink.renders.length, 0);
  assert.equal(h.adapter.actionable(), false);
  const disconnected = h.adapter.refresh();
  assert.equal(disconnected.entities.length, 0);
  assert.equal(disconnected.session.snapshotReady, false);
  h.join('room-b', 0);
  const next = h.snapshot([['4', thing('hand.0@0', 1)]]);
  assert.equal(next.session.gameId, 'room-b');
  assert.notEqual(next.entities[0].entityId, id);
});

test('only a coherent complete Client UPDATE is published; immutable sink poses cannot be changed underneath it', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  const marker = h.frames.length;
  h.snapshot([['4', thing('hand.0@0', 1)]], { phase: 'RollingDice', activeSeat: 0 });
  const publications = h.frames.slice(marker);
  assert.equal(publications.length, 1);
  assert.equal(publications[0].frame.turn.phase, 'RollingDice');
  assert.equal(publications[0].frame.entities[0].interaction.canDiscard, false);
  const old = JSON.stringify(publications[0].frame.entities[0].pose);
  h.world.things.get(4).slot.origin.x += 500;
  assert.equal(JSON.stringify(publications[0].frame.entities[0].pose), old);
});

test('source EndGame normalizes to GameComplete but unknown phases fail visibly and revoke caches', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  const frame = h.snapshot([['4', thing('hand.0@0', 1)]], { phase: 'EndGame', activeSeat: null });
  assert.equal(frame.turn.phase, 'GameComplete');
  h.delta([['turn', 'current', { activeSeat: null, phase: 'InventedPhase' }]]);
  assert.equal(h.adapter.latest, null);
  assert.match(h.failures[0], /Unsupported source phase/);
});

test('the frozen schema rejects hidden face leaks, unsafe numbers and extra private fields at runtime', () => {
  const { assertDto } = sourceLoader().load('renderer/renderer-contract.ts');
  for (const appearance of [
    { faceIndex: 0, knownFace: false, showFace: false, backIndex: 0, valueIndex: null },
    { faceIndex: null, knownFace: false, showFace: true, backIndex: 0, valueIndex: null },
    { faceIndex: null, knownFace: false, showFace: false, backIndex: 0, valueIndex: null, physicalTileId: 3 },
    { faceIndex: Infinity, knownFace: true, showFace: true, backIndex: 0, valueIndex: null },
  ]) assert.throws(() => assertDto('Appearance', appearance));
});

test('malformed model UPDATE fan-out clears private state synchronously instead of publishing a partially applied frame', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  h.snapshot([['4', thing('hand.0@0', 1)]]);
  const before = h.frames.length;
  h.delta([['things', '4', { ...thing('hand.0@0', 1), heldRotation: null }]]);
  assert.equal(h.adapter.latest, null);
  assert.equal(h.sink.renders.length, 0);
  assert.equal(h.frames.length, before);
  assert.match(h.client.protocolUnavailableReason, /could not be applied safely/);
  assert.equal(h.clears.at(-2).reason, 'protocol-unavailable');
  assert.equal(h.client.connected(), false);
});

test('owned concealed kong knowledge is permitted without showing its face; tombstones vacate source occupancy with no echo', t => {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  const frame = h.snapshot([['4', thing('meld.0.0@0', 1, 2)], ['h_upper', thing('wall.0.1@0', null)]]);
  const kong = frame.entities.find(e => e.group === 'meld');
  assert.equal(kong.appearance.knownFace, true);
  assert.equal(kong.appearance.showFace, false);
  h.sockets.at(-1).sent.length = 0;
  h.delta([['things', '4', null], ['things', 'h_upper', null]]);
  assert.equal(h.world.slots.get('meld.0.0@0').thing, null);
  assert.equal(h.world.slots.get('wall.0.1@0').thing, null);
  assert.equal(h.sockets.at(-1).sent.length, 0);
  assert.equal(h.adapter.latest.entities.length, 0);
});

for (const variant of ['FOUR_PLAYER', 'THREE_PLAYER', 'BAMBOO', 'MINEFIELD']) {
  test(`actual ${variant} Setup slots, extras and relay seat deltas stay coherent without foreign hand disclosure`, t => {
    const h = gameHarness(variant);
    t.after(h.close);
    h.join('room-a', null);
    const sourceMatch = {
      dealer: 0, honba: 0,
      conditions: { ...h.types.Conditions.defaultsFor(h.types.GameType[variant]), variant: variant.toLowerCase() },
    };
    const extra = [['match', 0, sourceMatch]];
    const unbound = h.delta(extra);
    assert.equal(unbound.session.snapshotReady, false, 'initial relay deltas cannot bind a room without its first FULL');
    assert.equal(unbound.entities.length, 0);
    assert.equal(h.adapter.actionable(), false);
    // The first full match selects the actual variant before things are projected.
    assert.notEqual(h.snapshot([], { extra }), null, h.failures.join('; '));
    const tiles = [...h.world.things.values()].filter(t => t.type === 'TILE');
    const id = tiles[0].index;
    const another = tiles[1].index;
    const own = tiles[2].index;
    let frame = h.snapshot([
      [id, thing('discard.0.0@2', null, 0)], [another, thing('hand.0@2', null, 2)],
      [own, thing('hand.0@0', undefined)], [2000, thing('marker@0', undefined)],
    ], { extra });
    assert.notEqual(frame, null, h.failures.join('; '));
    assert.equal(h.client.match.get(0).conditions.variant, variant.toLowerCase());
    assert.deepEqual(plain(frame.match.conditions), plain(h.types.Conditions.defaultsFor(h.types.GameType[variant])));
    assert.equal(frame.entities.find(e => e.slotName === 'discard.0.0@2').appearance.knownFace, true, 'legacy relay face:null cannot hide public discards');
    assert.equal(frame.entities.find(e => e.slotName === 'hand.0@2').appearance.faceIndex, null);
    const groups = new Set(h.adapter.layout.slots.map(s => s.group));
    for (const group of ['hand', 'discard', 'tray', 'payment', 'riichi', 'marker']) assert(groups.has(group), `${variant}: ${group}`);
    assert.equal(groups.has('meld'), variant !== 'MINEFIELD');
    if (variant === 'MINEFIELD') assert(groups.has('wall.open'));
    if (variant === 'THREE_PLAYER') assert(groups.has('kita'));
    assert.equal(h.adapter.layout.slots.some(s => s.slotName.startsWith('hand.extra@')), ['FOUR_PLAYER', 'BAMBOO'].includes(variant));
    assert.equal(h.adapter.layout.slots.some(s => s.slotName.startsWith('discard.extra.')), ['FOUR_PLAYER', 'THREE_PLAYER'].includes(variant));
    const { makeSlots } = h.load('setup-slots.ts');
    assert.deepEqual(h.adapter.layout.slots.map(s => s.slotName).sort(), makeSlots(h.types.GameType[variant]).map(s => s.name).sort());
    assert.equal(h.adapter.layout.slots.some(s => s.slotName === 'hiddenpool@0'), false);
    assert.equal(frame.session.actorSeat, null);
    assert.equal(frame.entities.find(e => e.slotName === 'hand.0@0').appearance.faceIndex, null);
    const spectator = frame;
    const previousIds = new Set(frame.entities.map(e => e.entityId));
    frame = h.delta([['seats', 'verified-player', { seat: 0 }]], { actor: 0 });
    assert.equal(frame.session.snapshotReady, true, 'standard relay seat deltas preserve the coherent bound snapshot');
    assert.equal(frame.session.actorSeat, 0);
    assert.equal(frame.session.viewSeat, 0);
    assert.equal(h.world.seat, h.client.seat);
    assert.equal(h.adapter.actionable(), true, 'the live relay player must be actionable without an invented FULL');
    assert.notEqual(frame.sessionId, spectator.sessionId);
    assert.equal(h.clears.at(-1).reason, 'authority-change');
    assert.equal(frame.frameId, 1);
    assert.equal(frame.entities.some(e => previousIds.has(e.entityId)), false);
    assert.equal(h.adapter.resolveEntity(spectator.entities[0].entityId, spectator.frameId), null);
    assert.deepEqual(plain(frame.match.conditions), plain(h.types.Conditions.defaultsFor(h.types.GameType[variant])));
    assert.equal(frame.entities.find(e => e.slotName === 'hand.0@2').appearance.faceIndex, null);
    const owned = frame.entities.find(e => e.slotName === 'hand.0@0');
    assert.equal(owned.appearance.faceIndex, h.world.things.get(own).typeIndex % 37);
    assert.equal(owned.interaction.canSelect, true);
    assert.equal(h.sockets.length, 1, 'seat authority changes must not force a reconnect');
    assert.equal(h.sockets[0].sent.filter(message => message.type === 'JOIN').length, 1);
    assert.equal(h.sockets[0].sent.some(message => message.type === 'UPDATE' && message.full), false);
    const actorFrame = frame;
    frame = h.delta([['things', own, null]]);
    assert.equal(frame.sessionId, actorFrame.sessionId);
    assert.equal(frame.entities.some(e => e.entityId === owned.entityId), false);
    assert.equal(h.adapter.resolveEntity(owned.entityId, actorFrame.frameId), null);
    frame = h.delta([['things', own, thing('hand.0@0', undefined)]]);
    assert.notEqual(frame.entities.find(e => e.slotName === 'hand.0@0').entityId, owned.entityId);
    frame = h.delta([['seats', 'verified-player', { seat: 2 }]], { actor: 2 });
    assert.equal(frame.session.snapshotReady, true);
    assert.equal(frame.session.actorSeat, 2);
    assert.equal(frame.session.viewSeat, 2);
    assert.equal(frame.entities.find(e => e.slotName === 'hand.0@0').appearance.faceIndex, null);
    assert.equal(frame.entities.find(e => e.slotName === 'hand.0@2').appearance.faceIndex, null,
      'taking a seat cannot invent a face concealed by the source projection');
    frame = h.delta([['seats', 'verified-player', null]], { actor: null });
    assert.equal(frame.session.snapshotReady, true);
    assert.equal(frame.session.actorSeat, null);
    assert.equal(h.adapter.actionable(), false);
    h.world.seat = 0;
    frame = h.adapter.refresh();
    assert.equal(frame.session.viewSeat, 0);
    assert.equal(frame.session.actorSeat, null);
    assert.equal(frame.entities.find(e => e.slotName === 'hand.0@0').appearance.faceIndex, null);
    frame = h.delta([['seats', 'verified-player', { seat: 0 }]], { actor: 0 });
    assert.equal(frame.session.snapshotReady, true, 'same-room seat rejoin requires no extra FULL');
    assert.equal(h.adapter.actionable(), true);
    const rejoined = frame;
    h.join('room-b', 0);
    frame = h.delta([['seats', 'verified-player', { seat: 0 }]]);
    assert.equal(frame.session.gameId, 'room-b');
    assert.equal(frame.session.snapshotReady, false, 'room switch cannot reuse the previous room collections');
    assert.equal(frame.entities.length, 0);
    assert.equal(h.adapter.actionable(), false);
    frame = h.snapshot([[own, thing('hand.0@0', undefined)]], { extra });
    assert.equal(frame.session.snapshotReady, true);
    assert.equal(frame.entities.length, 1, 'a new FULL omits and removes the old room entities');
    assert.notEqual(frame.entities[0].entityId, rejoined.entities.find(e => e.slotName === 'hand.0@0').entityId);
    const beforeDisconnect = frame;
    h.client.disconnect();
    assert.equal(h.adapter.latest, null);
    assert.equal(h.sink.renders.length, 0);
    assert.equal(h.adapter.actionable(), false);
    assert.equal(h.adapter.refresh().entities.length, 0);
    h.join('room-b', 0);
    frame = h.delta([['seats', 'verified-player', { seat: 0 }]]);
    assert.equal(frame.session.snapshotReady, false, 'reconnect must bind a new initial room snapshot');
    frame = h.snapshot([[own, thing('hand.0@0', undefined)]], { extra });
    assert.equal(frame.session.snapshotReady, true);
    assert.equal(h.adapter.actionable(), true);
    assert.notEqual(frame.sessionId, beforeDisconnect.sessionId);
    assert.notEqual(frame.entities[0].entityId, beforeDisconnect.entities[0].entityId);
    assert.deepEqual(h.failures, []);
  });
}

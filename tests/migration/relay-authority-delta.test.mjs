import test from 'node:test';
import assert from 'node:assert/strict';
import { gameHarness, thing } from '../../src/frontend/autotable-src/tests/node/unity-test-support.mjs';

for (const variant of ['FOUR_PLAYER', 'THREE_PLAYER', 'BAMBOO', 'MINEFIELD']) {
  test(`actual ${variant} relay FULL followed by standard seat DELTA remains renderable without invented FULL`, t => {
    const h = gameHarness(variant);
    t.after(h.close);
    h.join('relay-seat-delta', null);
    const tile = [...h.world.things.values()].find(thing => thing.type === 'TILE').index;
    const first = h.snapshot([[tile, thing('hand.0@0', 0)]], { actor: null });
    assert.equal(first.session.snapshotReady, true);
    assert.equal(first.session.actorSeat, null);
    const session = first.sessionId;
    const frame = h.delta([['seats', 'verified-player', { seat: 0 }]], { actor: 0 });
    assert.equal(h.client.seat, 0, 'The retained relay caller really received its normal seat delta');
    assert.notEqual(frame.sessionId, session, 'Authority changes must still clear the old renderer epoch');
    assert.equal(frame.session.snapshotReady, true,
      'Relay seat updates are deltas; existing coherent collections must not await a new FULL the protocol never sends');
    assert.equal(frame.session.actorSeat, 0);
    assert.equal(frame.match.conditions.gameType, variant);
    assert.equal(frame.entities.length, 1);
    assert.deepEqual(h.failures, []);
    assert.equal(h.sockets.at(-1).sent.some(message => message.type === 'JOIN' && message.full === true), false);
  });
}

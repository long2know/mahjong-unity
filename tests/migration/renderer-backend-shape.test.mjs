import test from 'node:test';
import assert from 'node:assert/strict';
import { gameHarness, plain, thing } from '../../src/frontend/autotable-src/tests/node/unity-test-support.mjs';

for (const dealMode of ['auto', 'manual']) {
  test(`real Client/World caller preserves backend variant but publishes exact renderer conditions (${dealMode})`, t => {
    const h = gameHarness();
    t.after(h.close);
    h.join();
    // This is the retained Production WS match shape observed by browser-player,
    // including the backend's variant extension absent from the renderer DTO.
    const match = {
      dealer: 0, honba: 0,
      conditions: {
        gameType: 'CHANGSHA', variant: 'changsha', back: 0, fives: '000', points: '25',
        baseUnit: 1, dealMode, dealType: 'HANDS',
      },
    };
    const original = JSON.stringify(match);
    const frame = h.snapshot([
      ['4', thing('hand.0@0', 1)],
      ['h_concealed_foreign', thing('hand.0@1', null, 2)],
    ], { extra: [['match', 0, match]] });
    assert.equal(h.client.match.get(0).conditions.variant, 'changsha',
      'The retained backend protocol must not be rewritten to accommodate the renderer');
    assert.equal(JSON.stringify(match), original, 'Projection must not mutate the server snapshot');
    assert.notEqual(frame, null, h.failures.join('; '));
    assert.deepEqual(h.failures, []);
    assert.deepEqual(Object.keys(frame.match.conditions).sort(), [
      'gameType', 'back', 'fives', 'points', 'baseUnit', 'dealMode', 'dealType',
    ].sort());
    assert.deepEqual(plain(frame.match.conditions), {
      gameType: 'CHANGSHA', back: 0, fives: '000', points: '25',
      baseUnit: 1, dealMode, dealType: 'HANDS',
    });
    const { assertDto } = h.load('renderer/renderer-contract.ts');
    assert.doesNotThrow(() => assertDto('RendererFrame', frame));
    assert.throws(() => assertDto('Conditions', match.conditions),
      'Keep the frozen renderer schema closed to extra wire metadata');
    const concealed = frame.entities.find(entity => entity.slotName === 'hand.0@1');
    assert.equal(concealed.appearance.faceIndex, null);
    assert.equal(concealed.appearance.knownFace, false);
    assert.equal(concealed.appearance.showFace, false);
  });
}

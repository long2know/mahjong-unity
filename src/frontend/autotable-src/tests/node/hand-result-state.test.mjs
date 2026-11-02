import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(path.join(root, 'package.json'));
const ts = require('typescript');
require.extensions['.ts'] = (module, filename) => module._compile(ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, filename);
const { HandResultState, parseHandResultContinuation, handResultIdentity } = require(path.join(root, 'src/hand-result-state.ts'));

const gate = overrides => ({
  gameId: 'canonical-runtime-guid', handNumber: 1, resultToken: 'opaque-settlement-token',
  requiredSeats: [0, 2], acknowledgedSeats: [], waitingSeats: [0, 2], ...overrides,
});
const result = (continuation = gate(), type = 'Hu') => ({
  winner: 1, type, score: [{ seat: 0, delta: -4 }, { seat: 1, delta: 12 }, { seat: 2, delta: -4 }, { seat: 3, delta: -4 }],
  hand: [1, 2, 3], nextBanker: 1, continuation,
});
const context = overrides => ({
  roomId: 'public-room-alias-NOT-runtime-guid', connected: true, freshSnapshot: true,
  seat: 0, changsha: true, complete: false, ...overrides,
});

test('frozen continuation DTO is read without interpreting or mutating opaque identity', () => {
  const original = Object.freeze(gate({
    requiredSeats: Object.freeze([0, 2]), acknowledgedSeats: Object.freeze([0]), waitingSeats: Object.freeze([2]),
  }));
  const parsed = parseHandResultContinuation(original);
  assert.deepEqual(parsed, original);
  assert.notEqual(parsed.requiredSeats, original.requiredSeats);
  assert.equal(parseHandResultContinuation(gate({ handNumber: 0 })).handNumber, 0);
  for (const bad of [
    null, [], '', {}, gate({ gameId: '' }), gate({ resultToken: ' \n' }), gate({ handNumber: NaN }),
    gate({ handNumber: 1.5 }), gate({ handNumber: -1 }), gate({ requiredSeats: [0, 4] }),
    gate({ requiredSeats: [2, 0] }), gate({ requiredSeats: [0, 0] }), gate({ waitingSeats: [0, 1] }),
    gate({ acknowledgedSeats: [0] }), gate({ waitingSeats: [2] }),
  ]) assert.equal(parseHandResultContinuation(bad), null);
});

for (const type of ['Hu', 'Draw', 'ZhaHu']) {
  test(`${type}: observations, duplicate snapshots and metadata cannot acknowledge; one click binds the exact hand`, () => {
    const state = new HandResultState();
    const payload = result(gate(), type);
    const before = JSON.stringify(payload);
    for (let i = 0; i < 10; i++) assert.equal(state.update(payload, context()).mode, 'continue');
    assert.equal(state.dismiss(), false);
    assert.deepEqual(state.acknowledge(), {
      gameId: 'canonical-runtime-guid', handNumber: 1, resultToken: 'opaque-settlement-token',
    });
    assert.equal(state.presentation().mode, 'pending');
    for (let i = 0; i < 10; i++) {
      assert.equal(state.update(payload, context()).mode, 'pending');
      assert.equal(state.acknowledge(), null);
    }
    assert.equal(JSON.stringify(payload), before);
  });
}

test('acknowledged owners wait for others; same-token readiness changes cannot re-enable the command', () => {
  const state = new HandResultState();
  const payload = result();
  state.update(payload, context());
  state.acknowledge();
  const acknowledged = result(gate({ acknowledgedSeats: [0], waitingSeats: [2] }));
  assert.equal(handResultIdentity(payload, 'alias'), handResultIdentity(acknowledged, 'alias'));
  for (let i = 0; i < 8; i++) {
    assert.equal(state.update(acknowledged, context()).mode, 'waiting');
    assert.deepEqual(state.presentation().waitingSeats, [2]);
    assert.equal(state.dismiss(), false);
    assert.equal(state.acknowledge(), null);
  }
  assert.equal(state.update(result(gate({ acknowledgedSeats: [0, 2], waitingSeats: [] })), context()).mode, 'advancing');
  assert.equal(state.update(null, context()).visible, false);
});

test('spectators, duplicate observers and non-required seats may dismiss locally without a command', () => {
  for (const seat of [null, 1, 3]) {
    const state = new HandResultState();
    assert.equal(state.update(result(), context({ seat })).mode, 'observer');
    assert.equal(state.acknowledge(), null);
    assert.equal(state.dismiss(), true);
    assert.equal(state.update(result(), context({ seat })).visible, false);
    assert.equal(state.update(result(gate({ acknowledgedSeats: [2], waitingSeats: [0] })), context({ seat })).visible, false);
  }
});

test('an observer dismissal cannot hide the result if the server later grants a required seat', () => {
  const state = new HandResultState();
  state.update(result(), context({ seat: null }));
  state.dismiss();
  assert.equal(state.update(result(), context()).mode, 'continue');
  assert.equal(state.presentation().visible, true);
});

test('disconnect retains the result without an ack; reconnect waits for a fresh authority snapshot', () => {
  const state = new HandResultState();
  state.update(result(), context());
  state.acknowledge();
  const offline = context({ connected: false, freshSnapshot: false, seat: null });
  assert.equal(state.update(result(), offline).mode, 'reconnecting');
  assert.equal(state.dismiss(), false);
  assert.equal(state.acknowledge(), null);
  assert.equal(state.update(result(), context({ freshSnapshot: false })).mode, 'reconnecting');
  assert.equal(state.acknowledge(), null);
  assert.equal(state.update(result(), context()).mode, 'continue');
  assert.deepEqual(state.acknowledge(), { gameId: 'canonical-runtime-guid', handNumber: 1, resultToken: 'opaque-settlement-token' });
});

test('already-acknowledged reconnect restores waiting, never an automatic or duplicate ack', () => {
  const state = new HandResultState();
  const acknowledged = result(gate({ acknowledgedSeats: [0], waitingSeats: [2] }));
  assert.equal(state.update(acknowledged, context()).mode, 'waiting');
  state.update(acknowledged, context({ connected: false, freshSnapshot: false, seat: null }));
  assert.equal(state.update(acknowledged, context()).mode, 'waiting');
  assert.equal(state.acknowledge(), null);
});

test('specific rejection remains visible while corrective readiness permits an explicit retry only', () => {
  const state = new HandResultState();
  state.update(result(), context());
  state.acknowledge();
  state.reject('stale-hand-result');
  assert.equal(state.presentation().error, 'stale-hand-result');
  assert.equal(state.update(result(), context()).mode, 'continue');
  assert.equal(state.presentation().error, 'stale-hand-result');
  assert(state.acknowledge());
  assert.equal(state.presentation().error, null);
  assert.equal(state.acknowledge(), null);
  state.update(result(gate({ acknowledgedSeats: [0], waitingSeats: [2] })), context());
  state.reject('hand-result-already-acknowledged');
  assert.equal(state.presentation().error, 'hand-result-already-acknowledged');
  assert.equal(state.presentation().mode, 'waiting');
});

test('new hand/token resets pending state; old command identity is not reused', () => {
  const state = new HandResultState();
  state.update(result(), context());
  state.acknowledge();
  state.reject('invalid-hand-result-ack');
  state.update(result(gate({ handNumber: 2, resultToken: 'second-token' })), context());
  assert.equal(state.presentation().error, null);
  assert.deepEqual(state.acknowledge(), { gameId: 'canonical-runtime-guid', handNumber: 2, resultToken: 'second-token' });
});

test('old room result cannot reappear before the new room has an authoritative full snapshot', () => {
  const state = new HandResultState();
  state.update(result(), context());
  for (let i = 0; i < 3; i++) {
    assert.equal(state.update(result(), context({ roomId: 'different-room', freshSnapshot: false })).visible, false);
    assert.equal(state.acknowledge(), null);
  }
});

test('invalid advertised continuation fails closed instead of falling back to legacy nextHand', () => {
  const state = new HandResultState();
  for (const malformed of [{}, { resultToken: 'x' }, { ...gate(), waitingSeats: [3] }]) {
    assert.equal(state.update(result(malformed), context()).mode, 'invalid');
    assert.equal(state.acknowledge(), null);
    assert.equal(state.dismiss(), false);
  }
});

test('final game completion and bots-only nullable continuation never send an ack or a next-hand command', () => {
  const state = new HandResultState();
  assert.equal(state.update(result(null), context({ seat: null })).mode, 'dismiss');
  assert.equal(state.acknowledge(), null);
  assert.equal(state.dismiss(), true);
  assert.equal(state.update(result(null), context({ complete: true })).visible, false);
  assert.equal(state.acknowledge(), null);
  assert.equal(state.update(result(), context({ complete: true })).visible, false);
});

test('legacy relay result remains a distinct local dismissal/nextHand path', () => {
  const state = new HandResultState();
  assert.equal(state.update(result(null), context({ changsha: false, connected: false, freshSnapshot: false, roomId: null })).mode, 'relay');
  assert.equal(state.acknowledge(), null);
  assert.equal(state.dismiss(), true);
});

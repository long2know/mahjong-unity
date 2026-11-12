import test from 'node:test';
import assert from 'node:assert/strict';
import { runInNewContext } from 'node:vm';
import { execFileSync } from 'node:child_process';
import { installInputTrace, probeFiles } from './browser-input-trace.mjs';
import { sourceLoader, plain } from '../../src/frontend/autotable-src/tests/node/renderer-test-support.mjs';

class Target {
  listeners = [];
  addEventListener(type, callback, options = {}) {
    this.listeners.push({ type, callback, capture: options === true || !!options.capture });
  }
  fire(event, capture) {
    for (const listener of this.listeners) {
      if (listener.type === event.type && listener.capture === capture) listener.callback.call(this, event);
    }
  }
}

function fixture({ traced = true, capacity = 256 } = {}) {
  const window = new Target(), canvas = new Target(), intents = [], captured = new Set();
  let now = 0, picks = 0, fault = null;
  const performance = { now: () => now, timeOrigin: 1000000 };
  const document = { hidden: false, hasFocus: () => true, activeElement: canvas };
  Object.assign(canvas, { id: 'webgl-canvas', focus() {}, setPointerCapture: id => captured.add(id),
    hasPointerCapture: id => captured.has(id),
    getBoundingClientRect: () => ({ x: 0, y: 0, left: 0, top: 0, width: 1280, height: 800 }) });
  const frame = {
    instanceId: 'instance', sessionId: 'session', frameId: 7, layoutRevision: 2,
    session: { mode: 'changsha', connected: true, snapshotReady: true, actorSeat: 0, viewSeat: 0, viewerRevision: 1 },
    turn: { phase: 'AwaitingDiscard', activeSeat: 0, awaitingDiscard: true },
    presentation: { viewMode: 'perspective', boardLayout: 'rectangular', handLayout: 'in-table', lookDown: 0, zoom: 0 },
    entities: [{ entityId: 'tile', seat: 0, group: 'hand', display: { selected: false },
      interaction: { canSelect: true, canDrag: true, canDiscard: true, canFlip: true, canPickup: false },
      appearance: { showFace: true, faceIndex: 'secret-face-test' } }],
    privateFixtureToken: 'secret-token-test',
  };
  const picked = { kind: 'entity', id: 'tile' };
  const scene = { committed: { frame }, pan: [0, 0],
    pick() { assert.equal(this, scene); picks++; if (fault) throw fault; return picked; } };
  const result = { actionId: 'action', instanceId: 'instance', sessionId: 'session', status: 'local', reason: null };
  const actions = { epoch: 'session', handle() { assert.equal(this, actions); return result; } };
  const hooks = {
    frame() { assert.equal(this, hooks); return frame; },
    pick(x, y, touch) { assert.equal(this, hooks); return scene.pick(x, y, touch); },
    slot() { return null; }, pan() {},
    intent(value) {
      assert.equal(this, hooks); intents.push(plain(value));
      actions.handle({ actionId: 'action', instanceId: 'instance', sessionId: 'session', contextFrameId: 7, intent: value });
    },
  };
  // Match browser initialization: native capture observers precede the actual application's listeners.
  if (traced) runInNewContext(`(${installInputTrace.toString()})(${JSON.stringify({ capacity })})`,
    { window, document, performance });
  const { WebglInput } = sourceLoader({ window, performance }).load('renderer/webgl-input.ts');
  const input = new WebglInput(canvas, hooks);
  const renderer = { input, scene, canvas, state: 'ready', inputBlocked: false, suspended: false,
    instanceId: 'instance', sessionId: 'session', viewport: { resizeRevision: 3 },
    cancelInput() { this.input.cancel(); this.inputBlocked = true; },
    measure() { this.cancelInput(); }, publish() {}, clear(_session, reason) { this.input.rejected(reason); } };
  const tableCamera = { contacts: new Map(), blocksGameplay: false, consumed: false, manipulating: false,
    cancel() {}, block() {}, resumeGameplay() { assert.equal(this, tableCamera); return false; } };
  window.game = { renderer, tableCamera, actions, adapter: { sessionId: 'session', latest: frame } };
  if (traced) window.__runtimeInputTrace.attach();
  const pointer = (type, processedAt, eventTimeStamp = processedAt) => {
    now = processedAt;
    const event = { type, timeStamp: eventTimeStamp, isTrusted: true, target: canvas, detail: 0,
      clientX: 20, clientY: 20, button: 0, buttons: type === 'pointerup' ? 0 : 1,
      pointerId: 1, pointerType: 'mouse', isPrimary: true, altKey: false, shiftKey: false,
      ctrlKey: false, metaKey: false, defaultPrevented: false,
      preventDefault() { this.defaultPrevented = true; } };
    window.event = event;
    window.fire(event, true);
    if (type === 'pointerdown') { canvas.fire(event, true); canvas.fire(event, false); }
    window.fire(event, false);
    window.event = undefined;
  };
  return { window, input, renderer, frame, picked, hooks, result, tableCamera, intents, pointer,
    picks: () => picks, fault: value => { fault = value; }, trace: () => plain(window.__runtimeInputTrace.snapshot()) };
}

test('trace forwards actual input calls once and retains ordinary double-click semantics without extra picking', () => {
  const baseline = fixture({ traced: false }), observed = fixture();
  for (const f of [baseline, observed]) {
    f.pointer('pointerdown', 80); f.pointer('pointerup', 100);
    f.pointer('pointerdown', 120); f.pointer('pointerup', 140);
  }
  assert.deepEqual(observed.intents, baseline.intents);
  assert.equal(observed.intents.filter(value => value.kind === 'discard').length, 1);
  assert.equal(observed.picks(), baseline.picks());
  assert.equal(observed.picks(), 4);
  const trace = observed.trace();
  assert.equal(trace.observerErrors, 0);
  assert.equal(trace.records.filter(value => value.kind === 'native:before').length, 4);
  assert.equal(trace.records.filter(value => value.kind === 'native:after').length, 4);
  assert.equal(trace.records.filter(value => value.kind === 'hooks.pick:return').length, 4);
  assert(trace.records.filter(value => value.kind === 'hooks.pick:return').every(value => value.calledAt <= value.processedAt));
  assert(trace.records.filter(value => value.kind === 'hooks.frame:return' && value.returnedEntity)
    .every(value => value.returnedEntity.interaction.canDiscard));
  const release = trace.records.filter(value => value.kind === 'native:before' && value.event.type === 'pointerup').at(-1);
  assert.equal(release.state.contact.target.id, 'tile');
  assert.equal(release.state.click.ageMilliseconds, 40);
  assert.equal(release.event.isTrusted, true);
  assert.equal(release.state.authority.latest.frameId, 7);
  assert.equal(trace.records.find(value => value.kind === 'actions.handle:return').result.actionId, 'action');
});

test('trace separates queued processing age from the corrected native double-click interval without changing input', () => {
  const f = fixture();
  f.pointer('pointerdown', 80, 80); f.pointer('pointerup', 100, 100);
  f.pointer('pointerdown', 520, 120); f.pointer('pointerup', 540, 140);
  assert.equal(f.intents.filter(value => value.kind === 'discard').length, 1);
  const before = f.trace().records.filter(value => value.kind === 'native:before' && value.event.type === 'pointerup').at(-1);
  assert.equal(before.event.dispatchAgeMilliseconds, 400);
  assert.equal(before.state.contact.ageMilliseconds, 420);
  assert.equal(before.event.eventTimeStamp - before.state.contact.start, 20);
  assert.equal(before.state.click.ageMilliseconds, 440);
  assert.equal(before.event.eventTimeStamp - before.state.click.at, 40);
  assert.equal(before.state.contactEntity.interaction.canDiscard, true);
  assert.equal(f.input.click, null, 'the real input consumes the valid native double-click exactly once');
});

test('trace retains the cancellation call chain and rejected reason instead of attributing every loss to timing', () => {
  const f = fixture();
  f.pointer('pointerdown', 80); f.pointer('pointerup', 100);
  f.pointer('pointerdown', 120);
  f.renderer.measure();
  f.pointer('pointerup', 140);
  assert.equal(f.intents.filter(value => value.kind === 'discard').length, 0);
  assert.equal(f.picks(), 3, 'canceled release must not gain a diagnostic pick');
  const canceled = f.trace().records.find(value => value.kind === 'input.cancel:before');
  assert.deepEqual(canceled.callChain, ['host.measure', 'host.cancelInput', 'input.cancel']);
  assert.equal(canceled.state.contact.target.id, 'tile');
  f.input.rejected('stale-entity');
  assert.equal(f.trace().records.find(value => value.kind === 'input.rejected:before').reason, 'stale-entity');
  f.renderer.clear('different-session', 'disconnect');
  assert.equal(f.trace().records.find(value => value.kind === 'host.clear:before').reason, 'disconnect');
});

test('observer wrappers preserve this, return identity and the original exception, even when observation fails', () => {
  const f = fixture();
  assert.equal(f.hooks.frame(), f.frame);
  assert.equal(f.hooks.pick(1, 2, false), f.picked);
  assert.equal(f.window.game.actions.handle({}), f.result);
  assert.equal(f.tableCamera.resumeGameplay(), false);
  const sentinel = new Error('test sentinel');
  f.fault(sentinel);
  assert.throws(() => f.hooks.pick(1, 2, false), error => error === sentinel);
  assert.equal(f.picks(), 2);
  Object.defineProperty(f.frame, 'frameId', { get() { throw sentinel; } });
  assert.equal(f.hooks.frame(), f.frame);
  assert(f.trace().observerErrors > 0, 'diagnostic failure is explicit but cannot change application behavior');
});

test('trace is bounded, primitive-only and excludes faces and fixture credentials', () => {
  const f = fixture({ capacity: 8 });
  f.pointer('pointerdown', 80); f.pointer('pointerup', 100);
  for (let i = 0; i < 20; i++) f.hooks.pick(1, 2, false);
  const trace = f.trace(), json = JSON.stringify(trace);
  assert.equal(trace.records.length, 8);
  assert(trace.dropped > 0);
  assert.equal(trace.observerErrors, 0);
  assert(trace.observerMilliseconds >= trace.maxObserverMilliseconds);
  assert.doesNotMatch(json, /secret-face-test|secret-token-test|faceIndex|privateFixtureToken/);
});

test('both container probes keep backward-compatible defaults and share the executed-tool inventory', () => {
  for (const script of ['container_smoke.py', 'container_proxy_smoke.py']) {
    const help = execFileSync('python3', ['-B', `tests/migration/${script}`, '--help'], { encoding: 'utf8' });
    assert.match(help, /--image IMAGE/); assert.match(help, /--source-sha SOURCE_SHA/); assert.match(help, /--output-dir OUTPUT_DIR/);
  }
  const proof = JSON.parse(execFileSync('python3', ['-B', '-c', `
import json,sys
from pathlib import Path
sys.path.insert(0, "tests/migration")
from runtime_probe import artifact_output,PROBE_FILES
root=Path.cwd()
assert artifact_output(root,"artifacts/runtime-input-trace/pinned-6455").is_relative_to(root/"artifacts/runtime-input-trace")
try:
    artifact_output(root,"../escaped")
    raise AssertionError("output escaped artifacts")
except ValueError:
    pass
print(json.dumps(PROBE_FILES))
`], { encoding: 'utf8' }));
  assert.deepEqual(proof, probeFiles);
});

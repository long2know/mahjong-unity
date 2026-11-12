import test from 'node:test';
import assert from 'node:assert/strict';
import { sourceLoader, gameHarness, thing, plain, require } from './renderer-test-support.mjs';
const { Matrix4 } = require('three');

function inputFixture() {
  const canvas = Object.assign(new EventTarget(), {
    setPointerCapture() {}, focus() {}, getBoundingClientRect: () => ({ left: 0, top: 0 }),
  });
  const window = new EventTarget(), intents = [];
  const resume = { allowed: true, calls: 0 };
  let time = 0;
  const frame = { entities: [{ entityId: 'tile', seat: 0, group: 'hand', display: { selected: false },
    interaction: { canSelect: true, canDrag: true, canDiscard: true, canFlip: true, canPickup: false } }] };
  const { WebglInput } = sourceLoader({ window, performance: { now: () => time } }).load('renderer/webgl-input.ts');
  const input = new WebglInput(canvas, {
    frame: () => frame, pick: () => ({ kind: 'entity', id: 'tile' }), slot: () => 'discard.0.0@0',
    intent: intent => intents.push(plain(intent)), pan() {},
    resumeGameplay: () => { resume.calls++; return resume.allowed; },
  });
  function pointer(type, values = {}) {
    const event = new Event(type, { cancelable: true });
    Object.assign(event, { clientX: 20, clientY: 20, button: 0, buttons: type === 'pointerup' ? 0 : 1,
      pointerId: 1, pointerType: 'mouse', isPrimary: true, altKey: false, shiftKey: false, ...values });
    (type === 'pointerdown' ? canvas : window).dispatchEvent(event);
    time += 40;
  }
  const key = value => {
    const event = new Event('keydown', { cancelable: true });
    Object.assign(event, { key: value, altKey: false, ctrlKey: false, metaKey: false, repeat: false, shiftKey: false });
    canvas.dispatchEvent(event);
  };
  return { input, intents, pointer, frame, resume, key };
}

test('ordinary touch emits one semantic discard; duplicate release and compatibility mouse do not replay it', () => {
  const f = inputFixture();
  f.pointer('pointerdown', { pointerType: 'touch' });
  f.pointer('pointerup', { pointerType: 'touch' });
  f.pointer('pointerup', { pointerType: 'touch' });
  assert.deepEqual(f.intents, [{ kind: 'discard', entityId: 'tile' }]);
  f.input.dispose();
});

test('camera cancellation breaks double click and cancels a real drag without committing', () => {
  const f = inputFixture();
  f.pointer('pointerdown'); f.pointer('pointerup');
  f.input.cancel();
  f.pointer('pointerdown'); f.pointer('pointerup');
  assert.equal(f.intents.filter(intent => intent.kind === 'discard').length, 0);
  f.pointer('pointerdown'); f.pointer('pointermove', { clientX: 50 });
  f.input.cancel();
  const drag = f.intents.filter(intent => ['beginDrag', 'dragToSlot', 'endDrag'].includes(intent.kind));
  assert.deepEqual(drag.map(intent => intent.kind), ['beginDrag', 'dragToSlot', 'endDrag']);
  assert.equal(drag[2].commit, false);
  assert.equal(drag[0].gestureId, drag[2].gestureId);
  f.pointer('pointerup');
  assert.equal(f.intents.filter(intent => intent.kind === 'endDrag').length, 1);
  f.frame.entities[0].display.selected = true;
  f.resume.allowed = false; f.key('f');
  assert.equal(f.intents.filter(intent => intent.kind === 'flip').length, 0, 'old contacts still block a keyboard action');
  f.resume.allowed = true; f.key('f');
  assert.equal(f.resume.calls, 2);
  assert.equal(f.intents.filter(intent => intent.kind === 'flip').length, 1, 'a fresh key resumes the shared camera gate once');
  f.input.dispose();
});

test('moved, canceled and second-finger contacts cannot become ordinary touch discards', () => {
  for (const reason of ['moved', 'canceled', 'second-finger']) {
    const f = inputFixture();
    f.frame.entities[0].interaction.canDrag = false;
    f.pointer('pointerdown', { pointerType: 'touch' });
    if (reason === 'moved') f.pointer('pointermove', { pointerType: 'touch', clientX: 45 });
    else if (reason === 'canceled') f.pointer('pointercancel', { pointerType: 'touch' });
    else f.pointer('pointerdown', { pointerType: 'touch', pointerId: 2, isPrimary: false });
    f.pointer('pointerup', { pointerType: 'touch' });
    assert.equal(f.intents.some(intent => intent.kind === 'discard'), false, reason);
    f.input.dispose();
  }
});

test('crossing an invalid drop target preserves a drag for the next eligible slot and never commits that invalid target', () => {
  const f = inputFixture();
  f.pointer('pointerdown'); f.pointer('pointermove', { clientX: 45 });
  f.input.rejected('invalid-target');
  f.pointer('pointermove', { clientX: 80 }); f.pointer('pointerup', { clientX: 80 });
  const actions = f.intents.filter(intent => ['beginDrag', 'dragToSlot', 'endDrag'].includes(intent.kind));
  assert.deepEqual(actions.map(intent => intent.kind), ['beginDrag', 'dragToSlot', 'dragToSlot', 'endDrag']);
  assert.equal(actions[3].commit, true);
  assert.equal(actions[0].gestureId, actions[3].gestureId);
  f.input.dispose();
  const canceled = inputFixture();
  canceled.pointer('pointerdown'); canceled.pointer('pointermove', { clientX: 45 });
  canceled.input.rejected('invalid-target'); canceled.pointer('pointerup', { clientX: 45 });
  assert.equal(canceled.intents.find(intent => intent.kind === 'endDrag').commit, false);
  canceled.input.dispose();
});

function modelFixture(t) {
  const h = gameHarness();
  t.after(h.close);
  h.join();
  h.snapshot([
    ...Array.from({ length: 14 }, (_, i) => [String(i * 4), thing(`hand.${i}@0`, i)]),
    ['h_foreign', thing('hand.0@1', null, 2)], ['h_wall', thing('wall.0.1@0', null)],
    ['72', thing('discard.0.0@2', 18)],
  ]);
  const { buildWebglPlan, fittedCamera, tileModel, sourcePose } = h.load('renderer/webgl-layout.ts');
  const { rendererViewport } = h.load('renderer/renderer-viewport.ts');
  const viewport = (width = 390, height = 844) => rendererViewport(h.adapter.instanceId, 1,
    { width, height, dpr: 2, separateHand: true, insets: { top: 90, left: 0, right: 0, bottom: 0 } });
  return { h, buildWebglPlan, fittedCamera, tileModel, sourcePose, viewport };
}

test('actual viewer-safe model fits a separate portrait hand; board camera cannot move that hand', t => {
  const f = modelFixture(t);
  f.h.adapter.setPresentation({
    viewMode: 'flat', boardLayout: 'rectangular', handLayout: 'separate', tileLabels: true, lookDown: 0, zoom: 0,
  });
  const frame = f.h.adapter.refresh(), viewport = f.viewport();
  const plan = f.buildWebglPlan(f.h.adapter.layout, frame, viewport);
  assert.equal(plan.entities.size, frame.entities.length);
  for (const id of frame.localHand.entityIds) assert.equal(plan.entities.get(id).separate, true);
  assert(plan.hand.height >= 148);
  const hand = plain(f.fittedCamera(plan, frame, true));
  const board = plain(f.fittedCamera(plan, frame));
  const handModels = frame.localHand.entityIds.map(id => plain(f.tileModel(plan.entities.get(id).pose)));
  const moved = plain(frame);
  moved.presentation.camera = { scale: 1.4, yaw: 35, pitch: 65, roll: 12 };
  const next = f.buildWebglPlan(f.h.adapter.layout, moved, viewport);
  assert.notDeepEqual(plain(f.fittedCamera(next, moved)), board);
  assert.deepEqual(plain(f.fittedCamera(next, moved, true)), hand);
  assert.deepEqual(frame.localHand.entityIds.map(id => plain(f.tileModel(next.entities.get(id).pose))), handModels);
  for (const id of ['h_foreign', 'h_wall']) {
    assert.equal(JSON.stringify(frame).includes(id), false);
  }
  for (const id of frame.localHand.entityIds) {
    const point = next.entities.get(id).pose.position.clone();
    point.z *= -1; point.applyMatrix4(new Matrix4().fromArray(f.fittedCamera(next, moved, true)));
    assert(Math.abs(point.x) < 1 && Math.abs(point.y) < 1, `${id} must fit inside the hand viewport`);
  }
});

test('source poses and actual retained ray/AABB picking share one geometry basis', t => {
  const f = modelFixture(t);
  const pose = f.sourcePose({ position: { x: 87, y: 87, z: 2 }, rotation: { x: 0, y: 0, z: 0, w: 1 },
    size: { x: 6, y: 9, z: 4 } });
  const { pickTile } = f.h.load('renderer/gpu/picking.ts');
  const modelData = f.tileModel(pose);
  const hit = pickTile({ modelData, instanceCount: 1 }, { origin: [0, 1, 0], direction: [0, -1, 0] });
  assert.equal(hit.instanceIndex, 0);
  assert(Math.abs(hit.point[1] - .04) < .00001);
  assert.equal(pickTile({ modelData, instanceCount: 1 }, { origin: [.1, 1, 0], direction: [0, -1, 0] }), null);
});

test('the reused instanced mesh releases every allocated vertex/index/instance buffer and VAO on teardown', () => {
  const buffers = new Set(), vaos = new Set();
  const gl = {
    createBuffer() { const buffer = {}; buffers.add(buffer); return buffer; },
    createVertexArray() { const vao = {}; vaos.add(vao); return vao; },
    deleteBuffer(buffer) { assert(buffers.delete(buffer)); },
    deleteVertexArray(vao) { assert(vaos.delete(vao)); },
    bindBuffer() {}, bindVertexArray() {}, bufferData() {}, enableVertexAttribArray() {},
    vertexAttribPointer() {}, vertexAttribDivisor() {}, getAttribLocation() { return 0; },
  };
  const { createTileMesh, disposeTileMesh } = sourceLoader().load('renderer/gpu/tile-mesh.ts');
  for (let iteration = 0; iteration < 3; iteration++) {
    const mesh = createTileMesh(gl, {}, 512);
    assert(buffers.size > 3);
    disposeTileMesh(gl, mesh);
    assert.equal(buffers.size, 0, 'per-vertex buffers must not leak on each renderer mount');
    assert.equal(vaos.size, 0);
  }
});

class Element extends EventTarget {
  constructor() {
    super(); this.children = []; this.attributes = new Map(); this.dataset = {}; this.hidden = false;
    this.classList = { add() {} }; this.style = { setProperty() {} };
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  removeAttribute(name) { this.attributes.delete(name); }
  append(...children) { for (const child of children) { this.children.push(child); child.parent = this; } }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); }
  getBoundingClientRect() { return { left: 0, top: 0, right: 390, bottom: 844, width: 390, height: 844 }; }
}
function hostFixture() {
  const document = Object.assign(new EventTarget(), {
    body: new Element(), documentElement: new Element(), hidden: false, focused: true,
    hasFocus: () => document.focused, createElement: () => new Element(),
    getElementById: id => {
      const walk = element => element.id === id ? element : element.children.map(walk).find(Boolean);
      return walk(document.body) ?? null;
    },
  });
  const window = Object.assign(new EventTarget(), { innerHeight: 844, devicePixelRatio: 1,
    location: new URL('http://localhost/autotable/'), WebGL2RenderingContext: class {}, matchMedia: () => new EventTarget() });
  const jobs = new Map(), draws = [], receipts = [];
  let now = 0, counter = 0, canFinish = false, disposed = 0, failures = 0;
  const scene = {
    committed: null, pending: null, drawCount: 0, pan: [0, 0],
    draw(layout, frame, viewport) {
      assert.equal(this.pending, null, 'never submit a second GPU frame before completion');
      draws.push(frame.frameId); this.pending = { frame, viewport, plan: {}, milliseconds: 2 };
    },
    poll() {
      if (!canFinish || !this.pending) return null;
      this.committed = this.pending; this.pending = null; return this.committed;
    },
    cancelPending() { this.pending = null; }, clear() { this.pending = this.committed = null; },
    dispose() { disposed++; },
  };
  class Observer { observe() {} disconnect() {} }
  const { WebglHost } = sourceLoader({
    window, document, ResizeObserver: Observer, MutationObserver: Observer,
    getComputedStyle: () => ({ paddingTop: '0', paddingRight: '0', paddingBottom: '0', paddingLeft: '0' }),
    performance: { now: () => now },
    requestAnimationFrame: callback => { const id = ++counter; jobs.set(id, callback); return id; },
    cancelAnimationFrame: id => jobs.delete(id),
  }, { 'renderer/webgl-scene.ts': { WebglScene: { create: async () => scene } } }).load('renderer/webgl-host.ts');
  const main = new Element(); document.body.append(main);
  const host = new WebglHost(main, { action() {}, resized() {}, resumed() {}, failure() { failures++; }, notice() {} });
  window.addEventListener('mahjong:renderer-frame', event => receipts.push(plain(event.detail)));
  host.clear('session', 'join');
  const layout = { contractVersion: '1.0.0', instanceId: host.instanceId, sessionId: 'session', type: 'layout',
    layoutRevision: 1, gameType: 'CHANGSHA', activeSeats: [0, 1, 2, 3], canonicalBoard: { width: 174, depth: 174 }, slots: [] };
  const frame = frameId => ({
    contractVersion: '1.0.0', instanceId: host.instanceId, sessionId: 'session', type: 'frame', frameId, layoutRevision: 1,
    session: { gameId: null, mode: 'offline', connected: false, snapshotReady: false, actorSeat: null, viewSeat: null, viewerRevision: null },
    presentation: { viewMode: 'flat', boardLayout: 'classic', handLayout: 'in-table', tileLabels: true, lookDown: 0, zoom: 0 },
    entities: [], seats: [], localHand: { seat: null, entityIds: [], drawnEntityId: null }, turn: null, pickup: null, dice: null, match: null,
  });
  const tick = (milliseconds = 34) => {
    now += milliseconds;
    const callbacks = [...jobs.values()]; jobs.clear(); callbacks.forEach(callback => callback(now));
  };
  return { host, layout, frame, tick, window, document, scene, draws, receipts, jobs,
    finish: () => { canFinish = true; }, disposed: () => disposed, failures: () => failures };
}

for (const hz of [60, 120]) {
  test(`production host submits the latest frame on each ${hz}Hz opportunity without a post-receipt bubble`, async t => {
    const f = hostFixture(); t.after(() => f.host.dispose()); f.finish(); await f.host.mount();
    for (let frame = 1; frame <= hz; frame++) {
      for (let update = 2; update >= 0; update--) f.host.publish(f.layout, f.frame(frame * 3 - update));
      f.tick(1000 / hz);
      assert.equal(f.draws.length, frame, 'a completed fence must permit the latest queued draw in this same rAF');
      assert.equal(f.draws.at(-1), frame * 3, 'superseded pending snapshots must not be submitted');
      assert(f.jobs.size <= 1);
    }
    f.tick(1000 / hz);
    assert.deepEqual(f.receipts.map(value => value.receipt.frameId), f.draws);
    assert.equal(f.jobs.size, 0, 'idle hosts must stop scheduling after the final genuine completion');
  });

  test(`production Game feeds ${hz}Hz animation opportunities only while focused and stops after disposal`, () => {
    const document = { hidden: false, hasFocus: () => focused };
    let focused = true, count = 0, handle = 0;
    const jobs = new Map();
    const { Game } = sourceLoader({
      window: { innerWidth: 960, innerHeight: 640 }, document,
      requestAnimationFrame: callback => { const id = ++handle; jobs.set(id, callback); return id; },
    }, {
      'world.ts': {}, 'client.ts': {}, 'client-ui.ts': {}, 'sound-player.ts': {},
      'settings-drawer.ts': { getSettings: () => ({}), syncCameraInputs() {} },
      'renderer/renderer-sink.ts': {}, 'renderer/renderer-frame.ts': {},
      'renderer/renderer-actions.ts': {}, 'renderer/webgl-host.ts': {}, 'renderer/table-camera-controls.ts': {},
    }).load('game.ts');
    const game = Object.assign(Object.create(Game.prototype), {
      disposed: false, lastUpdate: -Infinity, width: 960, height: 640,
      lookDown: { update() {} }, zoom: { update() {} },
      presentation: () => ({}), cameraPitch: () => 45,
      adapter: { setPresentation() {}, refresh() { count++; } },
    });
    const tick = time => { const callbacks = [...jobs.values()]; jobs.clear(); callbacks.forEach(callback => callback(time)); };
    game.mainLoop(0);
    for (let frame = 1; frame <= hz; frame++) tick(frame * 1000 / hz);
    assert.equal(count, hz + 1, 'World/adapter animation production must not retain an independent 30Hz gate');
    focused = false; tick(1100); assert.equal(count, hz + 1);
    focused = true; document.hidden = true; tick(1200); assert.equal(count, hz + 1);
    document.hidden = false; tick(1300); assert.equal(count, hz + 2);
    game.disposed = true; tick(1400); assert.equal(count, hz + 2);
    assert.equal(jobs.size, 0);
  });
}

test('WebGL host acknowledges only a completed GPU frame and coalesces to the newest correlated publication', async t => {
  const f = hostFixture(); t.after(() => f.host.dispose());
  f.host.publish(f.layout, f.frame(1));
  await f.host.mount(); f.tick();
  assert.equal(f.host.state, 'awaiting-ready');
  assert.deepEqual(f.receipts, [], 'parsing/drawing without the GPU fence must not acknowledge readiness');
  f.host.publish(f.layout, f.frame(2)); f.host.publish(f.layout, f.frame(3));
  assert.equal(f.host.queue.frame.frameId, 3);
  assert.deepEqual(f.draws, [1]);
  f.finish(); f.tick(); f.tick();
  assert.equal(f.host.state, 'ready');
  assert.deepEqual(f.draws, [1, 3]);
  assert.deepEqual(f.receipts.map(value => value.receipt.frameId), [1, 3]);
  assert(f.receipts.every(value => value.engine === 'webgl2' && value.receipt.instanceId === f.host.instanceId));
  assert.equal(f.document.body.getAttribute('data-webgl-renderer-ready'), 'true');
  assert.equal(f.document.body.getAttribute('data-unity-renderer-ready'), null);
  assert.equal(f.window.MahjongUnityBridge, undefined);
});

test('unfocused publication stays bounded, resumes newest frame and disposal stops all callbacks', async () => {
  const f = hostFixture(); f.finish(); await f.host.mount();
  f.host.publish(f.layout, f.frame(1)); f.tick(); f.tick();
  f.document.focused = false; f.window.dispatchEvent(new Event('blur'));
  for (let id = 2; id < 500; id++) f.host.publish(f.layout, f.frame(id));
  f.tick();
  assert.deepEqual(f.draws, [1]);
  assert.equal(f.host.queue.frame.frameId, 499);
  assert.equal(f.jobs.size, 0);
  f.document.focused = true; f.window.dispatchEvent(new Event('focus')); f.tick(); f.tick();
  assert.equal(f.receipts.at(-1).receipt.frameId, 499);
  await f.host.dispose(); f.tick();
  assert.equal(f.disposed(), 1);
  assert.equal(f.jobs.size, 0);
  assert.equal(f.document.body.getAttribute('data-webgl-renderer-ready'), null);
});

test('stale sessions/publications and context loss revoke input without selecting Unity or acknowledging success', async t => {
  const f = hostFixture(); t.after(() => f.host.dispose()); await f.host.mount();
  f.host.publish(f.layout, { ...f.frame(1), sessionId: 'obsolete' });
  assert.equal(f.host.state, 'failed');
  assert.deepEqual(f.draws, []); assert.deepEqual(f.receipts, []);
  assert.equal(f.failures(), 1);
  const other = hostFixture(); t.after(() => other.host.dispose()); await other.host.mount();
  other.host.canvas.dispatchEvent(new Event('webglcontextlost', { cancelable: true }));
  assert.equal(other.host.state, 'failed');
  assert.equal(other.window.MahjongUnityBridge, undefined);
  assert.equal(other.failures(), 1);
  const unsupported = hostFixture(); t.after(() => unsupported.host.dispose());
  delete unsupported.window.WebGL2RenderingContext;
  await unsupported.host.mount();
  assert.equal(unsupported.host.state, 'unsupported');
  unsupported.host.unavailable('second failure');
  assert.equal(unsupported.failures(), 1);
  assert.deepEqual(unsupported.draws, []);
});

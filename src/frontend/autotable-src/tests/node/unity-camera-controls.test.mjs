import test from 'node:test';
import assert from 'node:assert/strict';
import { sourceLoader, plain } from './unity-test-support.mjs';

function fixture(saved = {}) {
  const storage = new Map([['mahjong.settings.v1', JSON.stringify(saved)]]);
  const frames = new Map();
  let frameId = 0, cancellations = 0, resets = 0;
  const document = Object.assign(new EventTarget(), {
    activeElement: null, hidden: false, getElementById: () => null,
    body: { classList: { toggle() {} } }, documentElement: { style: { setProperty() {} } },
  });
  const window = Object.assign(new EventTarget(), {
    innerWidth: 390, innerHeight: 844,
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
  });
  window.addEventListener('mahjong:camera-reset', () => resets++);
  class Canvas extends EventTarget {
    captured = new Set();
    attributes = new Map();
    setAttribute(name, value) { this.attributes.set(name, value); }
    getBoundingClientRect() { return { left: 10, top: 20, width: 390, height: 844 }; }
    focus() { document.activeElement = this; }
    setPointerCapture(id) { this.captured.add(id); }
    hasPointerCapture(id) { return this.captured.has(id); }
    releasePointerCapture(id) { this.captured.delete(id); }
  }
  const canvas = new Canvas();
  const loader = sourceLoader({
    window, document,
    requestAnimationFrame: fn => { frames.set(++frameId, fn); return frameId; },
    cancelAnimationFrame: id => frames.delete(id),
  }, {
    'profile.ts': {}, 'rule-presets.ts': {}, 'theme.ts': {}, 'game-state.ts': {},
    'i18n.ts': { t: key => key, translateElements() {}, onLanguageChange() {} },
  });
  const settings = loader.load('settings-drawer.ts');
  const { rendererViewport } = loader.load('renderer/renderer-viewport.ts');
  const viewport = rendererViewport('test-instance', 1, {
    width: 390, height: 844, dpr: 2, insets: { top: 100, right: 40, bottom: 50, left: 20 }, separateHand: true,
  });
  const { TableCameraControls } = loader.load('renderer/table-camera-controls.ts');
  const controller = new TableCameraControls(canvas, {
    viewport: () => viewport,
    pitch: () => {
      const s = settings.getSettings();
      return settings.cameraPerspective(s, 390, 844) ? s.tablePitch ?? 78 : 90;
    },
    cancel: () => cancellations++,
  });
  const emit = (target, type, properties = {}) => {
    const event = new Event(type, { cancelable: true });
    for (const [key, value] of Object.entries(properties)) Object.defineProperty(event, key, { value });
    target.dispatchEvent(event);
    return event;
  };
  const pointer = (type, id, x, y, extra = {}) => {
    const event = emit(type === 'pointerdown' || type === 'lostpointercapture' ? canvas : window, type, {
      pointerId: id, clientX: x, clientY: y, pointerType: 'touch', button: 0,
      altKey: false, shiftKey: false, ...extra,
    });
    if (type === 'pointerup' || type === 'pointercancel') {
      canvas.captured.delete(id);
      emit(canvas, 'lostpointercapture', { pointerId: id });
    }
    return event;
  };
  const flush = () => {
    const pending = [...frames.values()];
    frames.clear();
    for (const fn of pending) fn(0);
  };
  const key = (key, extra = {}) => emit(canvas, 'keydown', {
    key, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false, ...extra,
  });
  const wheel = (deltaY, extra = {}) => emit(canvas, 'wheel', {
    clientX: 190, clientY: 300, deltaY, deltaMode: 0, ...extra,
  });
  return { ...loader, window, document, canvas, settings, controller, pointer, key, wheel, emit, flush,
    camera: () => plain(settings.tableCameraPresentation(settings.getSettings())),
    cancellations: () => cancellations, resets: () => resets };
}

test('real two-contact pinch coalesces into board scaling without tilting automatic portrait or moving a leftover finger into gameplay', t => {
  const h = fixture();
  t.after(() => h.controller.dispose());
  const changes = [];
  h.settings.onSettingsChange(s => changes.push(plain(s)));
  h.pointer('pointerdown', 1, 110, 300);
  h.pointer('pointerdown', 2, 270, 300);
  assert.equal(h.controller.blocksGameplay, true);
  h.pointer('pointermove', 1, 90, 300);
  h.pointer('pointermove', 2, 290, 300);
  assert.equal(h.camera(), null, 'moves are evaluated together on a presentation frame');
  h.flush();
  assert.deepEqual(h.camera(), { scale: 1.25, yaw: 0, pitch: null, roll: 0 });
  assert.equal(changes.length, 1, 'do not render intermediate one-finger centroid jumps');
  assert.equal(h.settings.getSettings().cameraMode, 'auto');
  h.pointer('pointerup', 1, 90, 300);
  h.pointer('pointermove', 2, 330, 350);
  h.flush();
  assert.equal(h.camera().scale, 1.25);
  assert.equal(h.controller.blocksGameplay, true);
  h.pointer('pointerup', 2, 330, 350);
  assert.equal(h.controller.blocksGameplay, true, 'last release remains a camera contact');
  h.pointer('pointerdown', 3, 100, 750);
  assert.equal(h.controller.blocksGameplay, false, 'a fresh hand contact restores ordinary tile input');
  h.pointer('pointerup', 3, 100, 750);
});

test('two-finger drag changes actual yaw/pitch, twist changes roll, and all camera axes stay bounded', t => {
  const h = fixture();
  t.after(() => h.controller.dispose());
  h.pointer('pointerdown', 1, 110, 300);
  h.pointer('pointerdown', 2, 270, 300);
  h.pointer('pointermove', 1, 150, 380);
  h.pointer('pointermove', 2, 310, 380);
  h.flush();
  assert.deepEqual(h.camera(), { scale: 1, yaw: -14, pitch: 70, roll: 0 });
  assert.equal(h.settings.getSettings().cameraMode, 'perspective');
  h.pointer('pointermove', 1, 230, 300);
  h.pointer('pointermove', 2, 230, 460);
  h.flush();
  assert.equal(h.camera().roll, 90);
  assert.equal(h.camera().yaw, -14);
  h.pointer('pointermove', 1, 2000, 1500);
  h.pointer('pointermove', 2, 2000, 1660);
  h.flush();
  assert.equal(h.camera().pitch, 20, 'never orbit beneath the table');
  assert(h.camera().yaw >= -180 && h.camera().yaw <= 180);
  h.pointer('pointermove', 1, 2000, -1500);
  h.pointer('pointermove', 2, 2000, -1340);
  h.flush();
  assert.equal(h.camera().pitch, 90);
});

test('small pinch centroid jitter cannot unexpectedly switch flat view to perspective', t => {
  const h = fixture();
  t.after(() => h.controller.dispose());
  h.pointer('pointerdown', 1, 110, 300);
  h.pointer('pointerdown', 2, 270, 300);
  h.pointer('pointermove', 1, 90, 304);
  h.pointer('pointermove', 2, 290, 304);
  h.flush();
  assert.equal(h.camera().scale, 1.25);
  assert.equal(h.camera().pitch, null);
  assert.equal(h.settings.getSettings().cameraMode, 'auto');
});

test('hand band, reserved chrome and side insets never act as table camera surfaces', t => {
  const h = fixture();
  t.after(() => h.controller.dispose());
  for (const [x, y] of [[100, 750], [100, 60], [15, 300], [380, 300]]) {
    h.pointer('pointerdown', 1, x, y);
    h.pointer('pointerdown', 2, x + 5, y + 5);
    h.pointer('pointermove', 1, x - 50, y - 50);
    h.pointer('pointermove', 2, x + 50, y + 50);
    h.flush();
    assert.equal(h.camera(), null);
    assert.equal(h.controller.blocksGameplay, true, 'non-camera multi-touch must still be safe for native input');
    h.pointer('pointerup', 1, x - 50, y - 50);
    h.pointer('pointerup', 2, x + 50, y + 50);
  }
  assert.equal(h.wheel(-100, { clientY: 750 }).defaultPrevented, false);
  assert.equal(h.camera(), null);
});

test('third contact, pointer cancellation and capture loss cancel queued camera work until all old contacts end', t => {
  const h = fixture();
  t.after(() => h.controller.dispose());
  h.pointer('pointerdown', 1, 110, 300);
  h.pointer('pointerdown', 2, 270, 300);
  h.pointer('pointermove', 2, 320, 350);
  h.pointer('pointerdown', 3, 200, 400);
  h.flush();
  assert.equal(h.camera(), null, 'canceled queued input must not change the camera later');
  h.pointer('pointerup', 3, 200, 400);
  h.pointer('pointermove', 2, 330, 380);
  h.flush();
  assert.equal(h.camera(), null, 'falling from three fingers to two is not a new gesture');
  h.pointer('pointercancel', 1, 110, 300);
  h.pointer('pointerdown', 4, 150, 400);
  h.pointer('pointermove', 4, 120, 450);
  h.flush();
  assert.equal(h.camera(), null);
  h.pointer('pointerup', 2, 330, 380);
  h.pointer('pointerup', 4, 120, 450);
  h.pointer('pointerdown', 5, 110, 300);
  h.pointer('pointerdown', 6, 270, 300);
  h.pointer('lostpointercapture', 5, 110, 300);
  h.pointer('pointermove', 6, 320, 350);
  h.flush();
  assert.equal(h.camera(), null);
  assert.equal(h.controller.blocksGameplay, true);
  h.pointer('pointerup', 5, 110, 300);
  h.pointer('pointerup', 6, 320, 350);
  h.pointer('pointerdown', 7, 100, 750);
  assert.equal(h.controller.blocksGameplay, false);
});

test('wheel zoom honors input units and limits while ordinary tile drags and right-click remain untouched', t => {
  const h = fixture();
  t.after(() => h.controller.dispose());
  h.pointer('pointerdown', 1, 110, 300, { pointerType: 'mouse' });
  assert.equal(h.pointer('pointermove', 1, 170, 380, { pointerType: 'mouse' }).defaultPrevented, false);
  h.pointer('pointerup', 1, 170, 380, { pointerType: 'mouse' });
  assert.equal(h.camera(), null);
  assert.equal(h.wheel(-100).defaultPrevented, true);
  assert(Math.abs(h.camera().scale - Math.exp(0.2)) < 1e-12);
  h.wheel(-2, { deltaMode: 1 });
  assert(Math.abs(h.camera().scale - Math.exp(0.264)) < 1e-12);
  h.wheel(-100000);
  h.wheel(-100000);
  assert.equal(h.camera().scale, 2.5);
  for (let i = 0; i < 4; i++) h.wheel(100000);
  assert.equal(h.camera().scale, 0.5);
  h.pointer('pointerdown', 2, 110, 300, { pointerType: 'mouse', button: 2 });
  assert.equal(h.controller.blocksGameplay, false, 'a fresh retained right-click must also resume game input');
  const right = h.emit(h.window, 'mousedown', { target: h.canvas, button: 2 });
  assert.equal(right.defaultPrevented, false);
});

test('Alt-left and middle mouse orbit; Shift-drag rolls and consumes only the camera mouse gesture', t => {
  const h = fixture();
  t.after(() => h.controller.dispose());
  h.pointer('pointerdown', 1, 110, 300, { pointerType: 'mouse', altKey: true });
  assert.equal(h.controller.manipulating, true);
  const compatibility = h.emit(h.window, 'mousedown', { target: h.canvas, button: 0, altKey: true });
  assert.equal(compatibility.defaultPrevented, true, 'the same Alt press must not start Unity selection/drag');
  h.pointer('pointermove', 1, 150, 340, { pointerType: 'mouse', altKey: true });
  h.flush();
  assert.deepEqual(h.camera(), { scale: 1, yaw: -14, pitch: 80, roll: 0 });
  h.pointer('pointerup', 1, 150, 340, { pointerType: 'mouse', altKey: true });
  h.pointer('pointerdown', 2, 110, 300, { pointerType: 'mouse', button: 1, shiftKey: true });
  h.pointer('pointermove', 2, 190, 320, { pointerType: 'mouse', shiftKey: true });
  h.flush();
  assert.equal(h.camera().roll, 32);
  assert.equal(h.camera().yaw, -14);
  assert.equal(h.camera().pitch, 80);
  h.pointer('pointerup', 2, 190, 320, { pointerType: 'mouse', button: 1 });
  assert.equal(h.controller.blocksGameplay, true);
});

test('focused canvas keyboard changes all camera dimensions, supports repeat, preserves browser shortcuts and resets through the shared setting', t => {
  const h = fixture();
  t.after(() => h.controller.dispose());
  h.document.activeElement = { tagName: 'INPUT' };
  assert.equal(h.key('ArrowRight').defaultPrevented, false);
  h.canvas.focus();
  assert.equal(h.key('+', { ctrlKey: true }).defaultPrevented, false);
  assert.equal(h.camera(), null);
  assert.equal(h.key('ArrowRight').defaultPrevented, true);
  h.key('ArrowRight', { repeat: true });
  h.key('ArrowDown');
  h.key('ArrowLeft', { shiftKey: true });
  h.key('+');
  assert.deepEqual(h.camera(), { scale: 1.1, yaw: 10, pitch: 85, roll: -5 });
  h.key('Home');
  assert.equal(h.camera(), null);
  assert.equal(h.settings.getSettings().cameraMode, 'auto');
  assert.equal(h.resets(), 1);
  assert.equal(h.controller.resumeGameplay(), true, 'a new game key is allowed after every camera contact has ended');
  h.pointer('pointerdown', 1, 110, 300);
  h.pointer('pointerdown', 2, 270, 300);
  assert.equal(h.controller.resumeGameplay(), false, 'keyboard input cannot commit while camera fingers remain down');
});

test('resize cancellation, blur, visibility loss and disposal cannot replay a queued camera move or leave pointer capture behind', () => {
  const h = fixture();
  h.pointer('pointerdown', 1, 110, 300, { pointerType: 'mouse', button: 1 });
  h.pointer('pointermove', 1, 190, 340, { pointerType: 'mouse' });
  h.controller.cancel();
  h.flush();
  assert.equal(h.camera(), null);
  assert.equal(h.controller.blocksGameplay, true);
  h.emit(h.window, 'blur');
  assert.equal(h.canvas.captured.size, 0);
  h.pointer('pointerdown', 2, 110, 300);
  assert.equal(h.controller.blocksGameplay, false);
  h.pointer('pointerdown', 3, 270, 300);
  h.pointer('pointermove', 3, 310, 340);
  h.document.hidden = true;
  h.emit(h.document, 'visibilitychange');
  h.flush();
  assert.equal(h.camera(), null);
  assert.equal(h.canvas.captured.size, 0);
  h.controller.dispose();
  const before = h.cancellations();
  assert.equal(h.wheel(-100).defaultPrevented, false);
  h.canvas.focus();
  assert.equal(h.key('+').defaultPrevented, false);
  assert.equal(h.camera(), null);
  assert.equal(h.cancellations(), before);
});

test('an intervening settings/control press cancels native input and pending camera movement even without a view change', t => {
  const h = fixture();
  t.after(() => h.controller.dispose());
  h.pointer('pointerdown', 1, 110, 300, { pointerType: 'mouse', button: 1 });
  h.pointer('pointermove', 1, 190, 340, { pointerType: 'mouse' });
  h.emit(h.document, 'pointerdown', { target: { id: 'settings-camera-reset' } });
  h.flush();
  assert.equal(h.camera(), null);
  assert.equal(h.controller.manipulating, false);
  assert.equal(h.controller.blocksGameplay, true);
  h.pointer('pointerup', 1, 190, 340, { pointerType: 'mouse' });
  assert.equal(h.controller.resumeGameplay(), true);
  const before = h.cancellations();
  h.emit(h.document, 'pointerdown', { target: { id: 'settings-camera-zoom' } });
  assert.equal(h.cancellations(), before + 1, 'a settings interaction must also break the native double-click chain');
});

test('the actual renderer schema accepts backward-compatible camera defaults and rejects incomplete, nonfinite, out-of-range or injected camera data', () => {
  const { assertDto } = sourceLoader().load('renderer/renderer-contract.ts');
  const presentation = { viewMode: 'perspective', boardLayout: 'rectangular', handLayout: 'separate', tileLabels: true, lookDown: 0, zoom: 0 };
  const camera = { scale: 1.5, yaw: 120, pitch: 45, roll: -30 };
  assert.doesNotThrow(() => assertDto('Presentation', presentation));
  assert.doesNotThrow(() => assertDto('Presentation', { ...presentation, camera: null }));
  assert.doesNotThrow(() => assertDto('Presentation', { ...presentation, camera }));
  assert.doesNotThrow(() => assertDto('CameraView', { ...camera, pitch: null }));
  assert.doesNotThrow(() => assertDto('CancelInput', { contractVersion: '1.0.0', instanceId: 'camera', type: 'cancelInput' }));
  assert.throws(() => assertDto('CancelInput', {
    contractVersion: '1.0.0', instanceId: 'camera', type: 'cancelInput', actorSeat: 0,
  }), /Invalid renderer DTO/);
  for (const patch of [{ scale: 0 }, { scale: 2.51 }, { pitch: 19 }, { pitch: 91 }, { yaw: 181 },
    { roll: -181 }, { pitch: NaN }, { scale: Infinity }, { yaw: '30' }, { actorSeat: 0 }]) {
    assert.throws(() => assertDto('Presentation', { ...presentation, camera: { ...camera, ...patch } }), /Invalid renderer DTO/);
  }
  const incomplete = { ...camera };
  delete incomplete.scale;
  assert.throws(() => assertDto('CameraView', incomplete), /Invalid renderer DTO/);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { runInNewContext } from 'node:vm';
import { root, require, sourceLoader, plain, thing } from './unity-test-support.mjs';

const ts = require('typescript');
class Input extends EventTarget {
  checked = true;
  value = 'auto';
}
function settingsFixture(saved = {}) {
  const storage = new Map([['mahjong.settings.v1', JSON.stringify(saved)]]);
  const board = new Input(), hand = new Input(), drawerBoard = new Input(), drawerHand = new Input();
  const nodes = new Map([
    ['renderer-board-layout', board], ['renderer-hand-layout', hand],
    ['settings-board-layout', drawerBoard], ['settings-hand-layout', drawerHand],
  ]);
  const { load } = sourceLoader({
    window: Object.assign(new EventTarget(), { localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) } }),
    document: {
      getElementById: id => nodes.get(id) ?? null, body: { classList: { toggle() {} } },
      documentElement: { style: { setProperty() {} } },
    },
  }, {
    'profile.ts': {}, 'rule-presets.ts': {}, 'theme.ts': {}, 'game-state.ts': {},
    'i18n.ts': { t: key => key, translateElements() {}, onLanguageChange() {} },
  });
  return { settings: load('settings-drawer.ts'), board, hand, drawerBoard, drawerHand, saved: () => JSON.parse(storage.get('mahjong.settings.v1')) };
}

function keyboardGameFixture() {
  class Element extends EventTarget {
    constructor(tagName) {
      super();
      this.tagName = tagName.toUpperCase();
      this.children = [];
      this.dataset = {};
      this.style = { setProperty() {} };
      this.classList = { add() {}, toggle() {} };
      this.checked = false;
      this.captured = new Set();
    }
    append(...children) { this.children.push(...children); }
    setAttribute() {}
    removeAttribute() {}
    getBoundingClientRect() { return { left: 0, top: 0, width: 1280, height: 800 }; }
    focus() { document.activeElement = this; }
    setPointerCapture(id) { this.captured.add(id); }
    hasPointerCapture(id) { return this.captured.has(id); }
    releasePointerCapture(id) { this.captured.delete(id); }
    remove() {}
  }
  const nodes = new Map(['main', 'loading', 'perspective', 'tile-labels', 'benchmark', 'muted', 'sticky', 'unity-notice']
    .map(id => [id, new Element(id === 'main' ? 'main' : 'input')]));
  const document = Object.assign(new EventTarget(), {
    body: new Element('body'), head: new Element('head'), hidden: false, activeElement: null, hasFocus: () => true,
    documentElement: new Element('html'), createElement: tag => new Element(tag),
    getElementById: id => nodes.get(id) ?? null,
  });
  const window = Object.assign(new EventTarget(), {
    location: new URL('http://localhost/autotable/?gameId=keyboard-room&variant=four_player'),
    innerWidth: 1280, innerHeight: 800, devicePixelRatio: 1,
    localStorage: { getItem: () => null, setItem() {} }, matchMedia: () => new EventTarget(),
  });
  const sockets = [];
  class Socket {
    static OPEN = 1; static CLOSING = 2; static CLOSED = 3;
    readyState = 0;
    sent = [];
    constructor() { sockets.push(this); }
    send(json) { this.sent.push(JSON.parse(json)); }
    receive(message) { this.onmessage({ data: JSON.stringify(message) }); }
    close() { this.readyState = 3; this.onclose?.(); }
  }
  const loader = sourceLoader({
    document, window, WebSocket: Socket,
    ResizeObserver: class { observe() {} disconnect() {} },
    getComputedStyle: () => ({ paddingTop: '0px', paddingRight: '0px', paddingBottom: '0px', paddingLeft: '0px' }),
    requestAnimationFrame: () => 1, cancelAnimationFrame() {}, setInterval: () => 1, clearInterval() {},
    console: { log() {}, warn() {}, error() {} },
  }, {
    'sound-player.ts': { SoundPlayer: class { play() {} } },
    'theme.ts': {}, 'rule-presets.ts': {},
    'profile.ts': { loadProfile: async () => ({ displayName: 'Player' }), refreshProfile: async () => {},
      snapshotStatsForGame() {}, onProfile() {}, initProfileHubBindings() {} },
    'hub.ts': { getHubConnection: async () => ({}) }, 'reconnect.ts': { clearSession() {}, saveSession() {} },
    'game-state.ts': { clearGameState() {}, refreshGameState: async () => {}, setGameRoomConnected() {} },
    'lobby.ts': { bindNewGameControls() {} },
    'identity.ts': { bootstrapIdentity: async () => null, getIdentityBootstrapState: () => ({ status: 'ready' }) },
    'ui/rule-action-controls.ts': { getRuleActionControls: () => ({}) },
    'i18n.ts': { t: key => key, onLanguageChange() {} }, 'toast.ts': { showToast() {} },
  });
  loader.load('client-ui.ts').ClientUi = class { start() {} };
  const { Game } = loader.load('game.ts');
  const game = new Game();
  game.client.join('ws://localhost/autotable/ws?variant=four_player', 'keyboard-room');
  const socket = sockets.at(-1);
  socket.readyState = Socket.OPEN;
  socket.onopen();
  socket.receive({ type: 'JOINED', gameId: 'keyboard-room', playerId: 'keyboard-player', isFirst: false });
  const { Conditions, GameType } = loader.load('types.ts');
  socket.receive({ type: 'UPDATE', full: true, entries: [
    ['match', 0, { dealer: 0, honba: 0, conditions: Conditions.defaultsFor(GameType.FOUR_PLAYER) }],
    ['seats', 'keyboard-player', { seat: 0 }], ['things', 0, thing('hand.0@0', undefined, 2)],
  ] });
  game.renderer.state = 'ready';
  let sequence = 0;
  const send = intent => game.actions.handle({
    contractVersion: '1.0.0', instanceId: game.adapter.instanceId, sessionId: game.adapter.sessionId,
    type: 'action', actionId: 'keyboard-' + ++sequence, contextFrameId: game.adapter.latest.frameId, intent,
  });
  const entityId = game.adapter.latest.entities.find(e => e.slotName === 'hand.0@0').entityId;
  const select = () => {
    assert.equal(send({ kind: 'hover', entityId }).status, 'local');
    assert.equal(send({ kind: 'select', entityIds: [entityId] }).status, 'local');
    socket.sent.length = 0;
  };
  const key = (type, value) => {
    const event = new Event(type);
    Object.defineProperty(event, 'key', { value });
    window.dispatchEvent(event);
  };
  const writes = () => socket.sent.flatMap(packet => packet.entries ?? [])
    .filter(([kind, id]) => kind === 'things' && id === 0);
  return { game, document, window, GameType, entityId, select, send, key, writes,
    preferences: () => loader.load('settings-drawer.ts').getSettings() };
}

test('registered Game P handler persists the clicked value before applying automatic camera preferences', async t => {
  const h = keyboardGameFixture();
  t.after(() => h.game.dispose());
  h.document.activeElement = h.game.renderer.canvas;
  assert.equal(h.preferences().cameraMode, 'auto');
  assert.equal(h.game.settings.perspective.checked, true);
  h.key('keydown', 'p');
  assert.equal(h.preferences().cameraMode, 'flat');
  assert.equal(h.game.adapter.latest.presentation.viewMode, 'flat');
  h.key('keydown', 'p');
  assert.equal(h.preferences().cameraMode, 'flat', 'a held P must not repeat');
  h.key('keyup', 'p');
  h.key('keydown', 'p');
  assert.equal(h.preferences().cameraMode, 'perspective');
  assert.equal(h.game.adapter.latest.presentation.viewMode, 'perspective');
  h.key('keyup', 'p');
  assert.equal(h.writes().length, 0, 'camera changes must not rewrite any tile');
});

test('actual Game F/R handlers resume after camera input but cannot flip while camera contacts are held', async t => {
  const h = keyboardGameFixture();
  t.after(() => h.game.dispose());
  h.select();
  const wheel = new Event('wheel', { cancelable: true });
  for (const [key, value] of Object.entries({ clientX: 300, clientY: 300, deltaY: -100, deltaMode: 0 }))
    Object.defineProperty(wheel, key, { value });
  h.game.renderer.canvas.dispatchEvent(wheel);
  assert.equal(wheel.defaultPrevented, true);
  assert.equal(h.game.tableCamera.blocksGameplay, true);
  const callback = id => h.game.renderer.hooks.action({
    contractVersion: '1.0.0', type: 'action', instanceId: h.game.adapter.instanceId, sessionId: h.game.adapter.sessionId,
    actionId: id, contextFrameId: h.game.adapter.latest.frameId,
    intent: { kind: 'flip', entityIds: [h.entityId], direction: 1, animated: true },
  });
  callback('unit-camera-blocked');
  assert.equal(h.writes().length, 0, 'the actual HostHooks -> router caller must pass the camera guard');
  h.document.activeElement = h.game.renderer.canvas;
  h.key('keydown', 'f');
  assert.equal(h.game.tableCamera.blocksGameplay, false, 'native canvas F must not remain locked after camera zoom');
  assert.equal(h.writes().length, 0, 'canvas F is still owned by native C# only');
  callback('unit-camera-resumed');
  assert.equal(h.writes().length, 1, 'the same host caller resumes after a fresh game key');
  h.key('keyup', 'f');
  h.select();
  h.document.activeElement = h.document.body;
  h.key('keydown', 'r');
  assert.equal(h.writes().length, 1, 'the retained browser R path still flips exactly once');
  h.key('keyup', 'r');
  h.select();
  const pointer = type => {
    const event = new Event(type, { cancelable: true });
    for (const [key, value] of Object.entries({
      clientX: 300, clientY: 300, pointerId: 77, pointerType: 'mouse', button: 0, altKey: true, shiftKey: false,
    })) Object.defineProperty(event, key, { value });
    (type === 'pointerdown' ? h.game.renderer.canvas : h.window).dispatchEvent(event);
    return event;
  };
  pointer('pointerdown');
  assert.equal(h.game.tableCamera.blocksGameplay, true);
  h.key('keydown', 'r');
  assert.equal(h.writes().length, 0, 'held camera contacts must not also perform a keyboard tile flip');
  h.key('keyup', 'r');
  pointer('pointerup');
  h.key('keydown', 'r');
  assert.equal(h.writes().length, 1, 'a new game key after all camera contacts end resumes the retained path');
  h.key('keyup', 'r');
});

test('actual layout settings default/normalize to auto and persist synchronized legacy/Display choices without resetting language', () => {
  const f = settingsFixture({ boardLayout: 'stretched', handLayout: 'all-hands', lang: 'zh-Hans', perspective: false });
  f.settings.installRendererLayoutSettings();
  assert.equal(f.settings.getSettings().boardLayout, 'auto');
  assert.equal(f.settings.getSettings().handLayout, 'auto');
  f.board.value = 'rectangular';
  f.board.dispatchEvent(new Event('change'));
  assert.equal(f.drawerBoard.value, 'rectangular');
  assert.equal(f.saved().boardLayout, 'rectangular');
  f.settings.setSettings({ handLayout: 'separate' });
  assert.equal(f.hand.value, 'separate');
  assert.equal(f.drawerHand.value, 'separate');
  assert.equal(f.saved().lang, 'zh-Hans');
  assert.equal(f.saved().perspective, false);
  f.settings.resetSettings();
  assert.equal(f.board.value, 'auto');
  assert.equal(f.hand.value, 'auto');
});

test('table camera preferences normalize storage, round-trip all axes, select perspective for explicit tilt, and reset without losing unrelated settings', () => {
  const f = settingsFixture({ lang: 'zh-Hant', tableZoom: 20, tableYaw: -900, tablePitch: 5, tableRoll: 'bad', boardLayout: 'rectangular' });
  assert.deepEqual(plain(f.settings.tableCameraPresentation(f.settings.getSettings())),
    { scale: 2.5, yaw: -180, pitch: 20, roll: 0 });
  f.settings.setSettings({ tableZoom: 1.4, tableYaw: 80, tablePitch: 42, tableRoll: -25 });
  assert.equal(f.settings.getSettings().cameraMode, 'perspective');
  assert.deepEqual(plain(f.settings.tableCameraPresentation(f.settings.getSettings())),
    { scale: 1.4, yaw: 80, pitch: 42, roll: -25 });
  assert.equal(f.saved().lang, 'zh-Hant');
  const reloaded = settingsFixture(f.saved());
  assert.deepEqual(plain(reloaded.settings.tableCameraPresentation(reloaded.settings.getSettings())),
    { scale: 1.4, yaw: 80, pitch: 42, roll: -25 });
  for (const invalid of [{ tableZoom: NaN }, { tableYaw: Infinity }, { tablePitch: '45' }, { tableRoll: undefined }]) {
    assert.throws(() => f.settings.setSettings(invalid), /Invalid table camera setting/);
  }
  assert.equal(f.saved().tableZoom, 1.4, 'invalid updates must not be persisted');
  f.settings.resetTableCamera();
  assert.equal(f.settings.tableCameraPresentation(f.settings.getSettings()), null);
  assert.equal(f.saved().cameraMode, 'auto');
  assert.equal(f.saved().boardLayout, 'rectangular');
  assert.equal(f.saved().lang, 'zh-Hant');
});

test('actual Game presentation and registered keyboard callers preserve layouts and exclusive F ownership', async t => {
  const source = ts.createSourceFile('game.ts', fs.readFileSync(path.join(root, 'src/game.ts'), 'utf8'), ts.ScriptTarget.Latest, true);
  const game = source.statements.find(s => ts.isClassDeclaration(s) && s.name?.text === 'Game');
  const method = game.members.find(m => ts.isMethodDeclaration(m) && m.name.getText(source) === 'presentation');
  const body = ts.createPrinter().printNode(ts.EmitHint.Unspecified, method, source);
  const code = ts.transpileModule(`class Fixture { ${body} }\nFixture;`, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  const { cameraPerspective, tableCameraPresentation, SETTINGS_DEFAULT } = settingsFixture().settings;
  const h = keyboardGameFixture();
  t.after(() => h.game.dispose());
  const Fixture = runInNewContext(code, { cameraPerspective, tableCameraPresentation, GameType: h.GameType });
  const fixture = new Fixture();
  fixture.world = { conditions: { gameType: h.GameType.FOUR_PLAYER } };
  fixture.height = 844;
  fixture.settings = { perspective: { checked: false }, tileLabels: { checked: true } };
  fixture.lookDown = { pos: 0.5 };
  fixture.zoom = { pos: -0.2 };
  for (const [width, boardLayout, handLayout] of [[390, 'rectangular', 'separate'], [767, 'rectangular', 'separate'], [768, 'classic', 'in-table'], [1024, 'classic', 'in-table']]) {
    fixture.width = width;
    const presentation = fixture.presentation({ ...SETTINGS_DEFAULT, cameraMode: 'auto', boardLayout: 'auto', handLayout: 'auto' });
    assert.deepEqual(plain(presentation), { viewMode: width < 768 ? 'flat' : 'perspective',
      boardLayout, handLayout, tileLabels: true, lookDown: 0.5, zoom: -0.2 });
  }
  fixture.world.conditions.gameType = h.GameType.CHANGSHA;
  assert.equal(fixture.presentation({ ...SETTINGS_DEFAULT, cameraMode: 'auto', boardLayout: 'auto', handLayout: 'auto' }).boardLayout,
    'rectangular', 'desktop Changsha also uses compact public lanes');
  fixture.width = 844;
  fixture.height = 390;
  assert.equal(fixture.presentation({ ...SETTINGS_DEFAULT, cameraMode: 'auto', boardLayout: 'auto', handLayout: 'auto' }).handLayout,
    'separate', 'phone landscape must not shrink the hand back into the table');
  fixture.width = 390;
  fixture.settings.perspective.checked = true;
  const explicit = fixture.presentation({ ...SETTINGS_DEFAULT, cameraMode: 'perspective', boardLayout: 'classic', handLayout: 'in-table' });
  assert.equal(explicit.boardLayout, 'classic');
  assert.equal(explicit.handLayout, 'in-table');
  assert.equal(explicit.viewMode, 'perspective');
  assert.equal(h.game.world.things.get(0).rotationIndex, 2,
    'the actual authoritative relay hand snapshot must retain its authored pose');
  h.select();
  h.document.activeElement = h.game.renderer.canvas;
  h.key('keydown', 'f');
  assert.equal(h.writes().length, 0, 'the browser must not also flip the physical F owned by the focused native canvas');
  // Only the native callback boundary is simulated here; the Game event listener,
  // adapter, action router, World, Movement and Client/wire callers are actual.
  assert.equal(h.send({ kind: 'flip', entityIds: [h.entityId], direction: 1, animated: true }).status, 'submitted');
  h.key('keyup', 'f');
  assert.equal(h.writes().length, 1);
  assert.equal(h.game.world.things.get(0).rotationIndex, 0, JSON.stringify({
    writes: h.writes(), slot: h.game.world.things.get(0).slot.name,
    poses: h.game.world.things.get(0).slot.rotations.length,
  }));
  h.select();
  h.key('keydown', 'F');
  assert.equal(h.writes().length, 0);
  assert.equal(h.send({ kind: 'flip', entityIds: [h.entityId], direction: -1, animated: true }).status, 'submitted');
  h.key('keyup', 'F');
  assert.equal(h.game.world.things.get(0).rotationIndex, 2, 'native Shift-F reverses once');
  assert.equal(h.writes().length, 1);
  h.document.activeElement = h.document.body;
  h.select();
  h.key('keydown', 'f');
  h.key('keydown', 'f');
  h.key('keyup', 'f');
  assert.equal(h.writes().length, 1, 'the retained browser route remains usable and held keys do not repeat');
  assert.equal(h.game.world.things.get(0).rotationIndex, 0);
  h.select();
  h.key('keydown', 'r');
  h.key('keyup', 'r');
  assert.equal(h.game.world.things.get(0).rotationIndex, 2, 'retained R reverses');
  assert.equal(h.writes().length, 1);
  for (const active of [{ tagName: 'INPUT' }, { tagName: 'TEXTAREA' }, { tagName: 'SELECT' },
    { tagName: 'DIV', isContentEditable: true }]) {
    h.select();
    h.document.activeElement = active;
    for (const key of ['f', 'F', 'r', 'R']) { h.key('keydown', key); h.key('keyup', key); }
    assert.equal(h.writes().length, 0, active.tagName + ': editing must not mutate the game');
  }
  h.select();
  h.document.activeElement = h.document.body;
  h.game.world.conditions.gameType = h.GameType.CHANGSHA;
  for (const key of ['f', 'F', 'r', 'R']) { h.key('keydown', key); h.key('keyup', key); }
  assert.equal(h.writes().length, 0, 'Changsha must not inherit relay keyboard writes');
});

test('viewport DTOs round real backing buffers, cap DPR at two and remove the separate hand band when disabled', () => {
  const { rendererViewport } = sourceLoader().load('renderer/renderer-viewport.ts');
  for (const [width, height, dpr] of [[390, 844, 1], [844, 390, 2], [1024, 768, 3], [375.5, 700.5, 1.25]]) {
    const measurement = { width, height, dpr, separateHand: true, insets: { top: 20, right: 5, bottom: 10, left: 5 } };
    const value = rendererViewport('instance', 1, measurement);
    assert.equal(value.renderPixelRatio, Math.min(dpr, 2));
    assert.equal(value.widthPx, Math.round(width * Math.min(dpr, 2)));
    assert.equal(value.heightPx, Math.round(height * Math.min(dpr, 2)));
    assert.deepEqual(plain(value.insetsCssPx), measurement.insets);
    assert(value.handBandCssPx > 0 && value.handBandCssPx < height);
    assert.equal(rendererViewport('instance', 2, { ...measurement, separateHand: false }).handBandCssPx, 0);
    for (const insets of [
      { top: 20, right: 5, bottom: 52, left: 5 },
      { top: height * 0.4, right: 5, bottom: 10, left: 5 },
      { top: 20, right: width * 0.4, bottom: 10, left: 5 },
    ]) {
      const clear = rendererViewport('instance', 3, { ...measurement, insets });
      assert.deepEqual(plain(clear.insetsCssPx), insets, 'the frozen viewport forwards actual chrome clearance unchanged');
      assert.equal(clear.handBandCssPx, Math.min(180, (height - insets.top - insets.bottom) * 0.3, Math.max(148, width * 0.24)));
      assert(clear.handBandCssPx + insets.top + insets.bottom < height, 'the complete hand band leaves a real board viewport');
      assert.equal(clear.widthPx, value.widthPx, 'reserving UI space does not change DPR or the input backing buffer');
      assert.equal(clear.heightPx, value.heightPx);
    }
  }
  assert.throws(() => rendererViewport('instance', 1, { width: 0, height: 844, dpr: 1, insets: { top: 0, right: 0, bottom: 0, left: 0 }, separateHand: false }));
});

test('all shipped locales label the new controls and inherited art is absent from the active shell/result CSS', () => {
  for (const locale of ['en', 'zh-Hans', 'zh-Hant']) {
    const catalog = JSON.parse(fs.readFileSync(path.join(root, `src/i18n/${locale}.json`), 'utf8'));
    for (const key of ['settings.board_layout', 'settings.hand_layout', 'settings.layout_auto', 'settings.layout_classic',
      'settings.layout_rectangular', 'settings.layout_in-table', 'settings.layout_separate', 'settings.table_camera',
      'settings.table_zoom', 'settings.table_yaw', 'settings.table_pitch', 'settings.table_roll',
      'settings.camera_reset', 'settings.camera_auto_angle', 'settings.camera_gestures_hint']) {
      assert.equal(typeof catalog[key], 'string');
      assert(catalog[key].length > 0);
    }
  }
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'src/style.css'), 'utf8');
  assert.match(html, /id="sticky" disabled/);
  assert.match(html, /Sticky drag is unavailable in renderer v1/);
  assert.equal(/tiles-labels\.auto|Segment7Standard\.otf|models\.auto|dice\.auto\.png|\.pdf/.test(html + css), false);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { runInNewContext } from 'node:vm';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(path.join(root, 'package.json'));
const ts = require('typescript');
const THREE = require('three');
require.extensions['.ts'] = (module, filename) => module._compile(ts.transpileModule(
  fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText, filename);
const { handSortMode, ownHandTiles, sortHand } = require(path.join(root, 'src/hand-sort.ts'));
const { mobileInfoPanel, toggleMobilePanel } = require(path.join(root, 'src/mobile-overlay-policy.ts'));
const { fitTableProjection } = require(path.join(root, 'src/table-fit.ts'));
const { makeSlots } = require(path.join(root, 'src/setup-slots.ts'));
const { Thing } = require(path.join(root, 'src/thing.ts'));
const { Slot } = require(path.join(root, 'src/slot.ts'));
const { GameType } = require(path.join(root, 'src/types.ts'));

const tile = (id, face, index = id, seat = 0) => ({ id, face, slotName: `hand.${index}@${seat}` });
const ids = items => Array.from(items, t => t.id);

test('suit/rank default: sequences, every suit boundary and stable physical-ID duplicates', () => {
  const input = Object.freeze([
    tile(91, 22), tile(70, 17), tile(36, 9), tile(22, 5), tile(33, 8),
    tile(8, 2), tile(7, 1), tile(0, 0), tile(21, 5), tile(72, 18), tile(107, 26),
  ].map(Object.freeze));
  assert.deepEqual(ids(sortHand(input, 'suit')), [0, 7, 8, 21, 22, 33, 36, 70, 72, 91, 107]);
  assert.deepEqual(ids(sortHand([...input].reverse(), 'suit')), ids(sortHand(input, 'suit')));
  assert.deepEqual(ids(input), [91, 70, 36, 22, 33, 8, 7, 0, 21, 72, 107]);
  assert.equal(handSortMode(null), 'suit');
  assert.equal(handSortMode('manual'), 'suit');
  assert.equal(handSortMode('groups'), 'groups');
});

test('pairs/triples first includes quads, then suit/rank singles, without inventing melds', () => {
  const input = [tile(0, 0), tile(11, 2), tile(9, 2), tile(43, 10), tile(41, 10),
    tile(40, 10), tile(85, 21), tile(84, 21), tile(87, 21), tile(86, 21), tile(32, 8), tile(107, 26)];
  assert.deepEqual(ids(sortHand(input, 'groups')), [9, 11, 40, 41, 43, 84, 85, 86, 87, 0, 32, 107]);
});

test('unknown, opaque, foreign, exposed, unconfirmed and preview entries never enter the own-hand view', () => {
  const own = { ...tile(8, 2), confirmed: true };
  const entries = [
    own, { ...tile(9, 2, 1), face: undefined, knownFace: 2, confirmed: true },
    { ...tile(10, 2, 2), face: null, knownFace: 2, confirmed: true },
    { ...tile(11, 2, 3), id: 'opaque-hand', knownFace: 2, confirmed: true },
    { ...tile(12, 3, 4, 1), confirmed: true },
    { ...tile(13, 3, 5), hidden: true, confirmed: true },
    { ...tile(14, 3, 6), confirmed: false },
    { ...tile(15, 3, 7), slotName: 'meld.0.0@0', confirmed: true },
    { ...tile(16, 4, 8), slotName: 'hand.extra@0', confirmed: true },
    { ...tile(17, 4, 9), face: undefined, confirmed: true },
  ];
  assert.deepEqual(ids(ownHandTiles(entries, 0)), [8, 9]);
  assert.deepEqual(ownHandTiles(entries, null), []);
  assert.deepEqual(ids(ownHandTiles(entries, 1)), [12]);
  // The optional privacy field is not a hidden-ID-to-face decoding scheme.
  assert.equal(ownHandTiles([{ ...own, face: 8, knownFace: 2 }], 0)[0].face, 2);
});

test('actual settings reads saved mode before lazy drawer mount, survives reload and preserves sibling preferences', () => {
  const storage = new Map([['mahjong.settings.v1', JSON.stringify({ handSort: 'groups', lang: 'zh-Hans', motion: 'reduced', masterVolume: .3 })]]);
  let unrelatedChanges = 0;
  const viewToggle = { checked: false, dispatchEvent: () => { unrelatedChanges++; } };
  const soundToggle = { checked: false, dispatchEvent: () => { unrelatedChanges++; } };
  const load = () => {
    const filename = path.join(root, 'src/settings-drawer.ts');
    const module = { exports: {} };
    runInNewContext(ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
    }).outputText, {
      module, exports: module.exports,
      window: { localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) } },
      document: { getElementById: id => id === 'perspective' ? viewToggle : id === 'settings-sound' ? soundToggle : null,
        body: { classList: { toggle: () => {} } }, documentElement: { style: { setProperty: () => {} } } },
      require: name => name === './hand-sort' ? { handSortMode }
        : name === './mobile-overlay-policy' ? { mobileInfoPanel } : {},
    });
    return module.exports;
  };
  const settings = load();
  assert.equal(settings.getSettings().handSort, 'groups');
  assert.equal(settings.getSettings().masterVolume, .3);
  assert.equal(settings.getSettings().mobileTableStatus, false);
  assert.equal(settings.getSettings().mobileInfoPanel, 'none');
  settings.setSettings({ mobileTableStatus: true, mobileInfoPanel: 'move-log' });
  assert.equal(load().getSettings().mobileTableStatus, true);
  assert.equal(load().getSettings().mobileInfoPanel, 'move-log');
  assert.equal(viewToggle.checked, false, 'an overlay choice must not reset a keyboard-selected flat view');
  assert.equal(soundToggle.checked, false, 'an overlay choice must not reset the legacy mute control');
  assert.equal(unrelatedChanges, 0);
  settings.setSettings({ handSort: 'suit' });
  assert.equal(load().getSettings().handSort, 'suit');
  assert.equal(JSON.parse(storage.get('mahjong.settings.v1')).lang, 'zh-Hans');
  assert.equal(JSON.parse(storage.get('mahjong.settings.v1')).motion, 'reduced');
  storage.set('mahjong.settings.v1', '{broken json');
  const repaired = load();
  assert.equal(repaired.getSettings().handSort, 'suit');
  repaired.setSettings({ handSort: 'groups' });
  assert.equal(load().getSettings().handSort, 'groups');
});

test('compact overlay defaults, mutual exclusion and explicit close preserve the other panel choice', () => {
  assert.equal(mobileInfoPanel(undefined), 'none');
  assert.equal(mobileInfoPanel('unknown'), 'none');
  assert.equal(mobileInfoPanel('chat'), 'chat');
  assert.equal(mobileInfoPanel('move-log'), 'move-log');
  assert.equal(toggleMobilePanel('none', 'chat', true), 'chat');
  assert.equal(toggleMobilePanel('chat', 'move-log', true), 'move-log');
  assert.equal(toggleMobilePanel('move-log', 'chat', false), 'move-log');
  assert.equal(toggleMobilePanel('chat', 'chat', false), 'none');
});

function worldHarness() {
  const filename = path.join(root, 'src/world.ts');
  const module = { exports: {} };
  const sideEffects = {
    './client-ui': { readSpectatorFromUrl: () => false },
    './toast': { showToast: () => {} },
  };
  runInNewContext(ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText, {
    module, exports: module.exports, console,
    require: name => sideEffects[name] ?? require(path.join(root, 'src', name)),
  });
  const world = Object.create(module.exports.World.prototype);
  const slots = new Map(makeSlots(GameType.CHANGSHA).map(slot => [slot.name, slot]));
  Slot.setLinks(slots);
  const things = new Map(), entries = new Map(), claims = new Map();
  const writes = [];
  Object.assign(world, {
    slots, things, seat: 0, conditions: { gameType: GameType.CHANGSHA },
    handMode: 'suit', handScale: 1, handPointerDown: false, handPlaces: new Map(),
    presentedHand: [], handSignature: '', handDirty: true, drawnTile: null,
    selected: [], hovered: null, highlightedThing: null, movement: null,
    client: { seat: 0, connected: () => true,
      things: { entries: () => entries.entries(), get: id => entries.get(id) ?? null },
      claim: { get: id => claims.get(id) ?? null },
      discard: { set: (seat, value) => writes.push({ seat, ...value }) } },
  });
  const put = descriptors => {
    for (const slot of slots.values()) slot.thing = null;
    things.clear();
    entries.clear();
    for (const { id, face, slotName } of descriptors) {
      const thing = new Thing(id, 'TILE', face, slots.get(slotName));
      thing.rotationIndex = 1;
      things.set(id, thing);
      entries.set(id, { slotName, rotationIndex: 1 });
    }
    world.handDirty = true;
  };
  const snapshot = () => JSON.stringify([...things].map(([id, thing]) => ({
    id, slot: thing.slot.name, occupant: thing.slot.thing?.index, sent: thing.sent,
    hidden: thing.hidden, entry: entries.get(id),
  })));
  return { world, put, entries, claims, writes, snapshot };
}

test('actual World presentation changes no authoritative slot, collection, ownership or sent flag', () => {
  const h = worldHarness();
  h.put([tile(100, 25, 0), tile(8, 2, 1), tile(5, 1, 2), tile(4, 1, 3), tile(20, 5, 0, 1)]);
  const before = h.snapshot();
  h.world.updateHandPresentation();
  assert.deepEqual(ids(h.world.ownHandPresentation().tiles), [4, 5, 8, 100]);
  assert.equal(h.snapshot(), before);
  assert.deepEqual(h.writes, []);
  const sortedPosition = h.world.handPlaces.get(4);
  assert.equal(sortedPosition.position.x, h.world.slots.get('hand.0@0').placeWithOffset(1).position.x);
  assert.equal(h.world.things.get(4).slot.name, 'hand.3@0');
  assert.equal(h.world.toSelect().find(t => t.id === 4).position.x, sortedPosition.position.x);
  h.world.isMyDiscardTurn = () => true;
  h.world.hovered = h.world.things.get(4);
  assert.equal(h.world.onDragStart(), false, 'the board press emits a discard, never a free drag');
  assert.deepEqual(h.writes, [{ seat: 0, tileId: 4 }]);
});

test('both sort preferences immediately reposition the rendered and raycast hand, never the server tiles', () => {
  const h = worldHarness();
  h.put([tile(0, 0, 0), tile(10, 2, 1), tile(100, 25, 2), tile(8, 2, 3)]);
  const before = h.snapshot();
  let rendered = [];
  h.world.objectView = { updateThings: items => { rendered = items; } };
  for (const [mode, expected] of [['groups', [8, 10, 0, 100]], ['suit', [0, 8, 10, 100]]]) {
    h.world.setHandSortMode(mode);
    h.world.updateHandPresentation();
    h.world.updateViewThings();
    assert.deepEqual(ids(h.world.ownHandPresentation().tiles), expected);
    assert.equal(rendered.length, 4);
    const picks = h.world.toSelect();
    assert.equal(picks.length, 4, 'own tiles remain on-board raycast targets on every screen');
    for (const item of rendered) {
      assert.notEqual(item.hidden, true);
      assert.deepEqual(item.place.position, picks.find(pick => pick.id === item.thingIndex).position);
    }
    assert.equal(h.snapshot(), before);
  }
  assert.deepEqual(h.writes, []);
});

test('actual settings binding applies the saved sort and holds reflow through pointerup, cancel and blur', () => {
  const source = ts.createSourceFile('game-ui.ts', fs.readFileSync(path.join(root, 'src/game-ui.ts'), 'utf8'), ts.ScriptTarget.Latest, true);
  const gui = source.statements.find(s => ts.isClassDeclaration(s) && s.name?.text === 'GameUi');
  const method = gui.members.find(m => ts.isMethodDeclaration(m) && m.name.getText(source) === 'setupHandSorting');
  const body = ts.createPrinter().printNode(ts.EmitHint.Unspecified, method, source);
  const events = new Map(), frames = [];
  let mode = 'groups', onChange;
  const Fixture = runInNewContext(ts.transpileModule(`class Fixture { ${body} }\nFixture;`, {
    compilerOptions: { target: ts.ScriptTarget.ES2022 },
  }).outputText, {
    getAppSettings: () => ({ handSort: mode }),
    onAppSettingsChange: fn => { onChange = fn; },
    document: { addEventListener: (event, fn) => events.set(event, fn) },
    window: { addEventListener: (event, fn) => events.set(event, fn) },
    requestAnimationFrame: fn => frames.push(fn),
  });
  const h = worldHarness();
  const ui = new Fixture();
  ui.world = h.world;
  ui.setupHandSorting();
  assert.equal(h.world.handMode, 'groups');
  mode = 'suit';
  onChange();
  assert.equal(h.world.handMode, 'suit');
  events.get('pointerdown')({ pointerId: 1 });
  events.get('pointerdown')({ pointerId: 2 });
  events.get('pointerup')({ pointerId: 1 });
  assert.equal(h.world.handPointerDown, true, 'compatibility mouse events see the same tile positions');
  frames.shift()();
  assert.equal(h.world.handPointerDown, true, 'second pointer still holds the hand');
  events.get('pointercancel')({ pointerId: 2 });
  frames.shift()();
  assert.equal(h.world.handPointerDown, false);
  events.get('pointerdown')({ pointerId: 3 });
  events.get('blur')();
  assert.equal(h.world.handPointerDown, false);
});

test('pointer-down and claim choice defer automatic reflow, including a newly drawn tile', () => {
  const h = worldHarness();
  const hand = Array.from({ length: 13 }, (_, i) => tile(i + 20, (i + 6) % 27, i));
  h.put(hand);
  h.world.updateHandPresentation();
  const before = ids(h.world.ownHandPresentation().tiles);
  h.put([...hand, tile(1, 0, 13)]);
  h.world.setHandPointerDown(true);
  h.world.updateHandPresentation();
  assert.deepEqual(ids(h.world.ownHandPresentation().tiles), before);
  h.world.setHandPointerDown(false);
  h.claims.set('0', { available: ['Chow'], chowOptions: [[20, 21]], deadline: Date.now() + 1000 });
  h.world.updateHandPresentation();
  assert.deepEqual(ids(h.world.ownHandPresentation().tiles), before);
  assert.deepEqual(h.claims.get('0').chowOptions, [[20, 21]]);
  h.claims.clear();
  h.world.updateHandPresentation();
  assert.equal(h.world.ownHandPresentation().tiles.length, 14);
  assert.equal(h.world.ownHandPresentation().drawn, 1);
  assert.equal(h.world.ownHandPresentation().tiles[0].id, 1);
  assert.deepEqual(h.writes, []);
});

for (const [name, concealed, meldSize] of [
  ['draw', 14, 0], ['discard', 13, 0], ['pung', 11, 3], ['chow', 11, 3],
  ['exposed kong replacement', 11, 4], ['concealed kong replacement', 11, 4],
  ['added kong replacement', 11, 4], ['two melds', 8, 6],
]) {
  test(`actual presentation preserves physical counts after ${name}`, () => {
    const h = worldHarness();
    const hand = Array.from({ length: concealed }, (_, i) => tile(i + 30, (i * 7) % 27, i));
    const melds = Array.from({ length: meldSize }, (_, i) => ({
      id: i, face: 0, slotName: `meld.${Math.floor(i / 3)}.${i % 3}@0`,
    }));
    // Fourth kong position is explicit; it is not a fourth concealed tile.
    if (meldSize === 4) melds[3].slotName = 'meld.0.4@0';
    h.put([...hand, ...melds]);
    const before = h.snapshot();
    h.world.setHandSortMode('groups');
    h.world.updateHandPresentation();
    assert.equal(h.world.ownHandPresentation().tiles.length, concealed);
    assert.deepEqual(ids(h.world.ownHandPresentation().tiles).sort((a, b) => a - b), ids(hand));
    assert.equal(h.snapshot(), before);
    assert.equal(h.world.toSelect().filter(t => h.world.things.get(t.id).slot.group === 'hand').length, concealed);
    assert.deepEqual(h.writes, []);
  });
}

test('spectator, disconnected and relay worlds do not sort, hide or discard their hands', () => {
  for (const mode of ['spectator', 'disconnected', 'relay']) {
    const h = worldHarness();
    h.put([tile(99, 24, 0), tile(4, 1, 1)]);
    if (mode === 'spectator') h.world.client.seat = null;
    if (mode === 'disconnected') h.world.client.connected = () => false;
    if (mode === 'relay') h.world.conditions.gameType = GameType.FOUR_PLAYER;
    const before = h.snapshot();
    h.world.updateHandPresentation();
    assert.equal(h.world.ownHandPresentation().tiles.length, 0);
    assert.equal(h.snapshot(), before);
    assert.deepEqual(h.writes, []);
  }
});

test('seat revocation clears the old concealed presentation even during a held pointer', () => {
  const h = worldHarness();
  h.put([tile(8, 2, 0), tile(4, 1, 1)]);
  h.world.updateHandPresentation();
  assert.equal(h.world.ownHandPresentation().tiles.length, 2);
  h.world.setHandPointerDown(true);
  h.world.client.seat = 1;
  h.world.updateHandPresentation();
  assert.equal(h.world.ownHandPresentation().tiles.length, 0);
  assert.equal(h.world.handPlaces.size, 0);
  assert.deepEqual(h.writes, []);
});

test('a genuinely advanced hand clears the draw marker even when a physical tile ID is reused', () => {
  const h = worldHarness();
  h.put([tile(8, 2, 0), tile(4, 1, 1)]);
  h.world.updateHandPresentation();
  h.world.drawnTile = 8;
  h.world.clearDrawnHandTile();
  h.world.updateHandPresentation();
  assert.equal(h.world.ownHandPresentation().drawn, null);
  assert.deepEqual(ids(h.world.ownHandPresentation().tiles), [4, 8]);
  assert.deepEqual(h.writes, []);
});

for (const [width, height] of [[360, 800], [390, 844], [844, 390], [820, 1180], [1280, 900]]) {
  for (const perspective of [false, true]) for (const seat of [0, 1, 2, 3, null]) {
    test(`projected 3D fit ${width}×${height}, ${perspective ? 'perspective' : 'flat'}, seat ${seat}`, () => {
      const camera = perspective ? new THREE.PerspectiveCamera(30, width / height, .1, 1000)
        : new THREE.OrthographicCamera(-105, 105, 105 * height / width, -105 * height / width, .1, 1000);
      const rig = new THREE.Group();
      rig.position.set(87, 87, 0);
      rig.rotation.z = (seat ?? 0) * Math.PI / 2;
      rig.add(camera);
      if (seat === null) camera.position.set(0, 0, perspective ? 400 : 100);
      else {
        camera.position.set(0, perspective ? -250 : -174, perspective ? 182 : 174);
        camera.rotation.x = perspective ? Math.PI * .3 : Math.PI * .25;
      }
      rig.updateMatrixWorld(true);
      const corners = [];
      for (const x of [-4.5, 178.5]) for (const y of [-4.5, 178.5]) for (const z of [0, 9]) {
        corners.push(new THREE.Vector3(x, y, z));
      }
      const area = { left: 8, top: 140, right: width - 8, bottom: height - 110 };
      fitTableProjection(camera, corners, width, height, area);
      for (const point of corners) {
        const p = point.clone().project(camera);
        const x = (p.x + 1) * width / 2, y = (1 - p.y) * height / 2;
        assert(x >= area.left - 1e-6 && x <= area.right + 1e-6);
        assert(y >= area.top - 1e-6 && y <= area.bottom + 1e-6);
      }
      assert(camera.projectionMatrix.clone().multiply(camera.projectionMatrixInverse)
        .elements.every((value, i) => Math.abs(value - (i % 5 === 0 ? 1 : 0)) < 1e-6));
    });
  }
}

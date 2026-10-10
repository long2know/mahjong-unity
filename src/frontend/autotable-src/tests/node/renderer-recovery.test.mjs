import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { runInNewContext } from 'node:vm';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(path.join(root, 'package.json'));
const ts = require('typescript');
const THREE = require('three');
const compile = filename => ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText;
require.extensions['.ts'] = (module, filename) => module._compile(compile(filename), filename);
const types = require(path.join(root, 'src/types.ts'));
const fit = require(path.join(root, 'src/table-fit.ts'));
const { Setup } = require(path.join(root, 'src/setup.ts'));
const { makeSlots } = require(path.join(root, 'src/setup-slots.ts'));
const groups = require(path.join(root, 'src/thing-group.ts'));
const load = (name, stubs) => {
  const module = { exports: {} };
  runInNewContext(compile(path.join(root, 'src', name + '.ts')), {
    module, exports: module.exports, console,
    require: id => stubs[id] ?? (id === 'three' ? THREE : require(path.join(root, 'src', id))),
  });
  return module.exports;
};
const { World } = load('world', {
  './client-ui': { readSpectatorFromUrl: () => false },
  './toast': { showToast() {} },
});
const { MainView } = load('main-view', {
  './world': { World }, './render/custom-outline': { CustomOutline: class {} },
});
const { ObjectView } = load('object-view', {
  './world': { World }, './client-ui': { readVariantFromUrl: () => 'CHANGSHA' },
  './center': { Center: class {
    mesh = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), new THREE.MeshLambertMaterial());
    setScores() {}
    draw() {}
  } },
});
const { Game } = load('game', Object.fromEntries([
  'object-view', 'world', 'client', 'base-client', 'asset-loader', 'utils',
  'mouse-ui', 'main-view', 'client-ui', 'sound-player',
].map(id => ['./' + id, {}])));
const { MouseUi } = load('mouse-ui', { './world': { World } });

const { GLTFLoader } = await import(pathToFileURL(require.resolve('three/examples/jsm/loaders/GLTFLoader.js')));
const loader = new GLTFLoader();
loader.register(() => ({
  name: 'CPU_GEOMETRY_NO_TEXTURE_IO',
  loadMaterial: () => Promise.resolve(new THREE.MeshLambertMaterial()),
}));
const bytes = fs.readFileSync(path.join(root, 'img/models.auto.glb'));
const model = await new Promise((resolve, reject) => loader.parse(
  bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength), '', resolve, reject));
const clone = name => {
  const mesh = model.scene.getObjectByName(name).clone();
  mesh.geometry = mesh.geometry.clone();
  mesh.material = mesh.material.clone();
  return mesh;
};
const assets = {
  meshes: { tile: clone('tile'), stick: clone('stick') }, textures: { tilesLabels: null },
  make: clone, makeMarker: () => clone('marker'),
  makeTable: () => new THREE.Mesh(new THREE.PlaneGeometry(183, 183), new THREE.MeshLambertMaterial()),
};
const noop = () => {};

function harness(width, height, perspective, seat = 0) {
  const setup = new Setup();
  setup.setup({ ...types.Conditions.initial(), dealType: 'INITIAL' });
  const entries = new Map();
  for (const thing of setup.things.values()) {
    thing.hidden = true;
    thing.sent = true;
  }
  for (let i = 0; i < 14; i++) {
    const thing = setup.things.get(53 - i);
    thing.prepareMove();
    thing.moveTo(setup.slots.get(`hand.${i}@${seat ?? 0}`), 1);
    thing.hidden = false;
    thing.sent = true;
    entries.set(thing.index, { slotName: thing.slot.name, rotationIndex: 1 });
  }
  const client = {
    seat, online: true, connectionMode: 'changsha', lastGameId: 'room-a', serverSnapshotGameId: 'room-a',
    connected() { return this.online; },
    things: { entries: () => entries.entries(), get: id => entries.get(id) ?? null },
    claim: { get: () => null },
  };
  const mainGroup = new THREE.Group();
  const objectView = new ObjectView(mainGroup, assets, client);
  const world = Object.create(World.prototype);
  Object.assign(world, {
    setup, slots: setup.slots, things: setup.things, conditions: setup.conditions, pushes: setup.pushes,
    client, objectView, authoritySeat: seat, viewpointAuthoritySeat: seat, viewSeat: seat, handSnapshotSeat: seat,
    handMode: 'groups', handScale: 1, handPointerDown: false, handPlaces: new Map(),
    presentedSeat: null, presentedHand: [], handSignature: '', handDirty: true, drawnTile: null,
    selected: [], hovered: null, highlightedThing: null, movement: null,
  });
  world.setupView();
  const view = Object.create(MainView.prototype);
  const camera = perspective ? new THREE.PerspectiveCamera(30, width / height, .1, 1000)
    : new THREE.OrthographicCamera(-1, 1, 1, -1, .1, 1000);
  const rig = new THREE.Group();
  rig.position.set(87, 87, 0);
  rig.add(camera);
  const fitCorners = [];
  for (const x of [-4.5, 178.5]) for (const y of [-4.5, 178.5]) for (const z of [0, 9]) {
    fitCorners.push(new THREE.Vector3(x, y, z));
  }
  Object.assign(view, {
    camera, perspective, viewGroup: rig, width, height, fitCorners,
    playArea: () => ({ left: 8, right: view.width - 8, top: 114, bottom: view.height - 8 }),
    updateViewport: noop, updateOutline: noop, updateHighlight: noop, render: noop,
  });
  const mouse = Object.create(MouseUi.prototype);
  Object.assign(mouse, {
    world, mouse2: null, raycastObjects: Array.from({ length: world.slots.size },
      () => new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1))),
    setCamera: noop, update: noop, updateCursors: noop,
  });
  const game = Object.create(Game.prototype);
  Object.assign(game, {
    world, mainView: view, objectView, mouseUi: mouse,
    lookDown: { pos: 0, update: noop }, zoom: { pos: 0, update: noop },
  });
  const frame = () => game.update();
  frame();
  const snapshot = () => JSON.stringify([...world.things].map(([id, thing]) => ({
    id, slot: thing.slot.name, occupant: thing.slot.thing?.index,
    sent: thing.sent, hidden: thing.hidden, entry: entries.get(id),
  })));
  const cameraState = () => ({
    type: camera.type, pose: [...camera.position.toArray(), ...camera.quaternion.toArray(), ...rig.quaternion.toArray()],
    projection: camera.projectionMatrix.toArray(),
    anchors: fitCorners.map(p => p.clone().project(camera).toArray()),
  });
  return { world, client, objectView, mouse, view, game, frame, cameraState, snapshot };
}

function instanceMatrix(group, id) {
  const mesh = group.meshes[id - group.startIndex];
  if (mesh.visible) return mesh.matrixWorld.clone();
  const matrix = new THREE.Matrix4();
  group.instancedMesh.getMatrixAt(id - group.startIndex, matrix);
  return matrix.premultiply(group.instancedMesh.matrixWorld);
}

function visible(group, id) {
  const mesh = group.meshes[id - group.startIndex];
  return mesh.visible || instanceMatrix(group, id).determinant() !== 0;
}

for (const [width, height] of [[1280, 900], [390, 844], [844, 390]]) {
  for (const perspective of [true, false]) {
    test(`every reconnect frame keeps the actual pose/projection at ${width}x${height}, perspective=${perspective}`, () => {
      for (const seat of [0, 1, 2, 3, null]) {
        const h = harness(width, height, perspective, seat);
        const before = h.cameraState(), authority = h.snapshot();
        const order = h.world.ownHandPresentation().tiles.map(t => t.id);
        h.client.online = false;
        h.client.seat = null;
        h.client.serverSnapshotGameId = null;
        for (let i = 0; i < 183; i++) {
          h.frame();
          assert.equal(h.world.seat, null, 'camera continuity must never preserve seat authority');
          assert.equal(h.world.viewSeat, seat);
          assert.deepEqual(h.cameraState(), before, `transient overhead/default frame ${i}, seat ${seat}`);
          assert.equal(h.world.ownHandPresentation().tiles.length, 0);
          assert.equal(h.world.toSelect().length, 0);
          for (const id of order) assert(!visible(h.objectView.thingGroups.get('TILE'), id), 'no stale private mesh/instance');
        }
        // JOINED can acknowledge transport before the room's FULL snapshot binds.
        h.client.online = true;
        h.frame();
        assert.deepEqual(h.cameraState(), before);
        h.client.seat = seat;
        h.frame();
        assert.deepEqual(h.cameraState(), before);
        assert.equal(h.world.ownHandPresentation().tiles.length, 0);
        h.client.serverSnapshotGameId = 'room-a';
        h.frame();
        assert.deepEqual(h.cameraState(), before);
        assert.deepEqual(h.world.ownHandPresentation().tiles.map(t => t.id), order);
        assert.equal(h.world.handMode, 'groups');
        assert.equal(h.view.perspective, perspective);
        assert.equal(h.snapshot(), authority, 'presentation changes never alter private/server tile records');
      }
    });
  }
}

test('real seat/spectator changes are honored; pending authority never exposes the previous private hand', () => {
  const h = harness(390, 844, true, 2);
  const seated = h.cameraState();
  h.client.seat = 1;
  h.frame();
  assert.equal(h.world.viewSeat, 1);
  assert.notDeepEqual(h.cameraState(), seated);
  assert.equal(h.world.ownHandPresentation().tiles.length, 0);
  for (const id of Array.from({ length: 14 }, (_, i) => 53 - i)) {
    assert(!visible(h.objectView.thingGroups.get('TILE'), id));
    assert(!h.world.toSelect().some(p => p.id === id));
  }
  h.client.seat = null;
  h.frame();
  assert.equal(h.world.viewSeat, null);
  assert.equal(h.view.camera.rotation.x, 0);
  h.client.online = false;
  h.frame();
  h.view.width = 844;
  h.view.height = 390;
  h.frame();
  assert.equal(h.world.viewSeat, null);
  assert.notDeepEqual(h.cameraState().projection, seated.projection, 'intentional resize remains effective');
  h.client.connectionMode = 'relay';
  h.client.seat = 3;
  h.frame();
  assert.equal(h.world.viewSeat, 3, 'offline/relay seat changes retain their original behavior');
});

test('reapplying the saved perspective never recreates a camera with its default pose', () => {
  for (const perspective of [true, false]) {
    const h = harness(390, 844, perspective);
    const before = h.cameraState();
    h.view.setupRendering = () => { throw new Error('Unchanged preference must retain the fitted camera'); };
    h.view.setPerspective(perspective);
    assert.deepEqual(h.cameraState(), before);
  }
});

test('existing spectator-follow seat writes move only the viewpoint and survive ordinary updates/reconnect', () => {
  const h = harness(390, 844, true, null);
  for (const followed of [0, 1, 2, 3, null]) {
    h.world.seat = followed;
    h.frame();
    assert.equal(h.world.viewSeat, followed);
    assert.equal(h.world.seat, null);
    assert.equal(h.world.toSelect().length, 0);
    assert.equal(h.world.ownHandPresentation().tiles.length, 0);
    const before = h.cameraState();
    h.client.online = false;
    h.client.serverSnapshotGameId = null;
    h.frame();
    assert.deepEqual(h.cameraState(), before);
    h.client.online = true;
    h.client.serverSnapshotGameId = 'room-a';
    h.frame();
    assert.deepEqual(h.cameraState(), before);
  }
});

test('Changsha has no marker Thing, slot or surviving mesh; every relay variant restores its marker', () => {
  const h = harness(390, 844, true);
  const markerGroup = h.objectView.thingGroups.get('MARKER');
  assert.equal(h.world.things.has(2000), false);
  assert.equal(h.world.slots.has('marker@0'), false);
  assert.equal(markerGroup.meshes.length, 0);
  assert.equal(h.world.toSelect().some(p => p.id === 2000), false);
  for (const variant of ['FOUR_PLAYER', 'THREE_PLAYER', 'BAMBOO', 'MINEFIELD']) {
    const setup = new Setup();
    setup.setup(types.Conditions.defaultsFor(variant));
    h.objectView.replaceThings(setup.things);
    const marker = setup.things.get(2000);
    const place = marker.place();
    h.objectView.updateThings([{ type: 'MARKER', thingIndex: 2000, place }]);
    const mesh = markerGroup.meshes[0];
    assert.equal(mesh.visible, true, variant);
    assert(mesh.parent);
    assert.deepEqual(mesh.position.toArray(), place.position.toArray());
    setup.replace(types.Conditions.initial());
    h.objectView.replaceThings(setup.things);
    h.objectView.updateThings([]);
    assert.equal(markerGroup.meshes.length, 0);
    assert.equal(mesh.parent, null, 'removal must detach the real, previously visible mesh');
    assert.equal(setup.things.has(2000), false);
    assert.equal(setup.slots.has('marker@0'), false);
    assert.equal([...setup.slots.values()].filter(s => s.group === 'wall').length, 108, 'wall-break targets remain');
  }
});

function projectedBounds(geometry, matrix, camera, width, height) {
  if (!geometry.boundingBox) geometry.computeBoundingBox();
  const box = geometry.boundingBox, points = [];
  for (const x of [box.min.x, box.max.x]) for (const y of [box.min.y, box.max.y]) for (const z of [box.min.z, box.max.z]) {
    const p = new THREE.Vector3(x, y, z).applyMatrix4(matrix).project(camera);
    points.push({ x: (p.x + 1) * width / 2, y: (1 - p.y) * height / 2 });
  }
  const left = Math.min(...points.map(p => p.x)), right = Math.max(...points.map(p => p.x));
  const top = Math.min(...points.map(p => p.y)), bottom = Math.max(...points.map(p => p.y));
  return { left, right, top, bottom, width: right - left, height: bottom - top };
}

for (const [width, height] of [[360, 800], [390, 844], [844, 390]]) {
  for (const perspective of [true, false]) {
    test(`real GLB enlarged own hand has larger faces, separation and matching hit IDs at ${width}x${height}, perspective=${perspective}`, t => {
      let minWidth = Infinity, minHeight = Infinity, tableHeight = 0;
      for (const seat of [0, 1, 2, 3]) {
        const h = harness(width, height, perspective, seat), before = h.snapshot();
        const group = h.objectView.thingGroups.get('TILE');
        const table = h.objectView.mainGroup.getObjectByName('table');
        tableHeight = projectedBounds(table.geometry, table.matrixWorld, h.view.camera, width, height).height;
        const order = h.world.ownHandPresentation().tiles.map(tile => tile.id);
        const fixed = h.cameraState();
        for (const mode of ['suit', 'groups']) {
          h.world.setHandSortMode(mode);
          h.frame();
          for (const tile of h.world.ownHandPresentation().tiles) {
            const matrix = instanceMatrix(group, tile.id);
            const rect = projectedBounds(group.instancedMesh.geometry, matrix, h.view.camera, width, height);
            minWidth = Math.min(minWidth, rect.width);
            minHeight = Math.min(minHeight, rect.height);
            assert(rect.width >= 18 && rect.height >= 27, JSON.stringify(rect));
            assert(rect.left >= 8 - 1e-4 && rect.right <= width - 8 + 1e-4, JSON.stringify({ seat, rect }));
            assert(rect.top >= 114 - 1e-4 && rect.bottom <= height - 8 + 1e-4, JSON.stringify({ seat, rect }));
            const ray = new THREE.Raycaster();
            ray.setFromCamera(new THREE.Vector2((rect.left + rect.right) / width - 1,
              1 - (rect.top + rect.bottom) / height), h.view.camera);
            assert.equal(ray.intersectObjects(h.mouse.currentObjects)[0]?.object.userData.id, tile.id);
            const box = group.instancedMesh.geometry.boundingBox.clone().applyMatrix4(matrix);
            for (const slot of h.world.slots.values()) {
              if (!['wall', 'meld'].includes(slot.group)) continue;
              const place = slot.placeWithOffset(0);
              const other = group.instancedMesh.geometry.boundingBox.clone().applyMatrix4(
                new THREE.Matrix4().compose(place.position, place.rotation, new THREE.Vector3(1, 1, 1)));
              assert(!box.intersectsBox(other), `${tile.id} overlaps ${slot.name}`);
              const otherRect = projectedBounds(group.instancedMesh.geometry,
                new THREE.Matrix4().compose(place.position, place.rotation, new THREE.Vector3(1, 1, 1)),
                h.view.camera, width, height);
              assert(Math.min(rect.right, otherRect.right) - Math.max(rect.left, otherRect.left) < 1e-4
                || Math.min(rect.bottom, otherRect.bottom) - Math.max(rect.top, otherRect.top) < 1e-4,
              `projected hand ${tile.id} obscures ${slot.name}`);
            }
          }
          assert.equal(h.snapshot(), before);
          assert.deepEqual(h.cameraState(), fixed, 'sorting does not reframe the board');
        }
        const id = order[0], thing = h.world.things.get(id);
        h.world.hovered = thing;
        h.frame();
        assert.equal(group.meshes[id].scale.x, 1.7, 'custom selection mesh uses the same scale');
        thing.prepareMove();
        thing.moveTo(h.world.slots.get(`discard.0.0@${seat}`), 0);
        h.world.handDirty = true;
        h.frame();
        assert.equal(group.meshes[id].scale.x, 1, 'a discarded custom mesh immediately loses hand enlargement');
        h.world.hovered = null;
        h.frame();
        assert(Math.abs(new THREE.Vector3().setFromMatrixScale(instanceMatrix(group, id)).x - 1) < 1e-6);
        assert.deepEqual(h.cameraState(), fixed, 'changing hand count does not reframe the board');
      }
      t.diagnostic(`CPU projected real-mesh minimum ${minWidth.toFixed(2)}x${minHeight.toFixed(2)} CSS px; table height ${tableHeight.toFixed(2)}px; screenshots still required`);
    });
  }
}

test('scale changes at the same pose update the instance cache; hidden/revealed meshes do not retain enlargement', () => {
  const group = new groups.TileThingGroup(assets, new THREE.Group());
  const params = [{ index: 0, type: 'TILE', typeIndex: 0 }];
  group.replace(0, params);
  const position = new THREE.Vector3(49, 4.5, 2), rotation = new THREE.Quaternion();
  for (const scale of [1, 1.7, 1]) {
    group.setSimple(0, position, rotation, scale);
    assert(Math.abs(new THREE.Vector3().setFromMatrixScale(instanceMatrix(group, 0)).x - scale) < 1e-6);
  }
  group.setCustom(0, position, rotation, 1.7);
  group.hide(0);
  assert(!visible(group, 0));
  group.setSimple(0, position, rotation);
  assert(visible(group, 0));
  assert.equal(group.meshes[0].scale.x, 1);
  assert.equal(fit.responsiveHandScale(1280, 900), 1);
  assert.equal(fit.localHandFitCorners(null, 1.7).length, 0);
  assert.equal(makeSlots('CHANGSHA').filter(s => s.group === 'hand').length, 60);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';

const frontend = path.resolve(process.env.MAHJONG_GEOMETRY_SOURCE_ROOT
  ?? path.join(path.dirname(fileURLToPath(import.meta.url)), '../..'));
const require = createRequire(path.join(frontend, 'package.json'));
const THREE = require('three');
const ts = require('typescript');
require.extensions['.ts'] = (module, filename) => module._compile(ts.transpileModule(
  fs.readFileSync(filename, 'utf8'), {
    fileName: filename,
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText, filename);
const { makeSlots } = require(path.join(frontend, 'src/setup-slots.ts'));
const { Slot } = require(path.join(frontend, 'src/slot.ts'));
const { Thing } = require(path.join(frontend, 'src/thing.ts'));
const { Setup } = require(path.join(frontend, 'src/setup.ts'));
const { TileThingGroup } = require(path.join(frontend, 'src/thing-group.ts'));
const { Size, Conditions } = require(path.join(frontend, 'src/types.ts'));
const { hasExtraDiscardTile } = require(path.join(frontend, 'src/hand-accounting.ts'));
const EPS = 1e-5;

const { GLTFLoader } = await import(pathToFileURL(require.resolve('three/examples/jsm/loaders/GLTFLoader.js')));
const loader = new GLTFLoader();
loader.register(() => ({
  name: 'CPU_GEOMETRY_NO_TEXTURE_IO',
  loadMaterial: () => Promise.resolve(new THREE.MeshLambertMaterial()),
}));
const bytes = fs.readFileSync(path.join(frontend, 'img/models.auto.glb'));
const model = await new Promise((resolve, reject) => loader.parse(
  bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength), '', resolve, reject));
const original = model.scene.getObjectByName('tile');
assert(original?.isMesh);
original.geometry.computeBoundingBox();
const dimensions = original.geometry.boundingBox.getSize(new THREE.Vector3());
assert(Math.abs(dimensions.x - Size.TILE.x) < EPS);
assert(Math.abs(dimensions.y - Size.TILE.y) < EPS);
assert(Math.abs(dimensions.z - Size.TILE.z) < EPS);

// The original GLB geometry and production placement methods are used.
// The adapter only omits image loading and performs AssetLoader's mesh clone.
const assets = {
  meshes: { tile: original }, textures: { tilesLabels: null },
  make: () => {
    const mesh = original.clone();
    mesh.material = original.material.clone();
    return mesh;
  },
};

function scene(descriptors) {
  const slots = new Map(makeSlots('CHANGSHA').map(slot => [slot.name, slot]));
  Slot.setLinks(slots);
  const things = descriptors.map((item, index) => {
    const slot = slots.get(item.name);
    assert(slot, `Missing actual slot ${item.name}`);
    const thing = new Thing(index, 'TILE', 0, slot);
    thing.rotationIndex = item.rotation ?? 0;
    return thing;
  });
  for (const [source, target] of Slot.computePushes([...slots.values()])) target.handlePush(source);
  const parent = new THREE.Group();
  const group = new TileThingGroup(assets, parent);
  group.replace(0, things);
  return things.map((thing, index) => {
    const place = thing.place();
    group.setSimple(index, place.position, place.rotation);
    const matrix = new THREE.Matrix4();
    group.instancedMesh.getMatrixAt(index, matrix);
    const mesh = new THREE.Mesh(group.instancedMesh.geometry, new THREE.MeshBasicMaterial());
    mesh.matrixAutoUpdate = false;
    mesh.matrix.copy(matrix);
    mesh.updateMatrixWorld(true);
    const bounds = new THREE.Box3().setFromObject(mesh, true);
    const custom = group.setCustom(index, place.position, place.rotation);
    custom.updateMatrix();
    custom.updateMatrixWorld(true);
    const customBounds = new THREE.Box3().setFromObject(custom, true);
    assert(bounds.min.distanceTo(customBounds.min) < EPS);
    assert(bounds.max.distanceTo(customBounds.max) < EPS);
    return { name: thing.slot.name, slot: thing.slot, bounds, center: bounds.getCenter(new THREE.Vector3()) };
  });
}

function penetrates(a, b) {
  return ['x', 'y', 'z'].every(axis =>
    Math.min(a.max[axis], b.max[axis]) - Math.max(a.min[axis], b.min[axis]) > EPS);
}
const wall = scene(makeSlots('CHANGSHA').filter(slot => slot.group === 'wall').map(slot => ({ name: slot.name })));
const byName = new Map(wall.map(body => [body.name, body]));
const distanceXY = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);

test('real GLB wall has108 distinct bodies,54 contacting pairs and no solid-volume intersections', () => {
  assert.equal(wall.length, 108);
  assert.equal(new Set(wall.map(p => p.center.toArray().join(','))).size, 108);
  for (let i = 0; i < wall.length; i++) for (let j = i + 1; j < wall.length; j++) {
    assert(!penetrates(wall[i].bounds, wall[j].bounds), `${wall[i].name} intersects ${wall[j].name}`);
  }
  let stacks = 0;
  for (const body of wall.filter(p => p.slot.indexes[1] === 0)) {
    const top = byName.get(`wall.${body.slot.indexes[0]}.1@${body.slot.seat}`);
    assert(top);
    assert(distanceXY(body.center, top.center) < EPS);
    assert(Math.abs(top.bounds.min.z - body.bounds.max.z) < EPS);
    assert(Math.abs(top.center.z - body.center.z - Size.TILE.z) < EPS);
    stacks++;
  }
  assert.equal(stacks, 54);
});

test('all real mesh-centre AND origin seams stay within1.6pitch; straight pitch remains unchanged', () => {
  for (let seat = 0; seat < 4; seat++) {
    const side = wall.filter(body => body.slot.seat === seat && body.slot.indexes[1] === 0)
      .sort((a, b) => a.slot.indexes[0] - b.slot.indexes[0]);
    const next = byName.get(`wall.0.0@${(seat + 1) % 4}`);
    const last = side.at(-1);
    assert(distanceXY(last.center, next.center) <= 1.6 * Size.TILE.x + EPS);
    assert(distanceXY(last.slot.origin, next.slot.origin) <= 1.6 * Size.TILE.x + EPS);
    for (const body of side) assert(distanceXY(body.center, body.slot.origin) < EPS);
    for (let i = 1; i < side.length; i++) {
      assert(Math.abs(distanceXY(side[i - 1].center, side[i].center) - Size.TILE.x) < EPS);
    }
  }
});

test('actual wall bodies remain separated from all canonical hands and discards', () => {
  const others = scene(makeSlots('CHANGSHA').filter(slot =>
    (slot.group === 'hand' && !slot.name.startsWith('hand.extra'))
    || (slot.group === 'discard' && !slot.name.startsWith('discard.extra')))
    .map(slot => ({ name: slot.name, rotation: slot.group === 'hand' ? 1 : 0 })));
  for (const a of wall) for (const b of others) {
    assert(!penetrates(a.bounds, b.bounds), `${a.name} intersects ${b.name}`);
  }
});

test('added-only fourth tile contacts the middle Pung at all16 locations without interpenetration', () => {
  const slots = new Map(makeSlots('CHANGSHA').map(slot => [slot.name, slot]));
  for (let seat = 0; seat < 4; seat++) for (let meld = 0; meld < 4; meld++) {
    const topName = `meld.${meld}.4@${seat}`;
    // The explicit before-profile route was the flat fourth slot. Evaluating
    // it when the new slot is absent gives a real-geometry RED, not a fake box.
    const fourthName = slots.has(topName) ? topName : `meld.${meld}.3@${seat}`;
    const bodies = scene([0, 1, 2].map(tile => ({ name: `meld.${meld}.${tile}@${seat}` }))
      .concat([{ name: fourthName }]));
    const base = bodies[1], upper = bodies[3];
    assert(distanceXY(base.center, upper.center) < EPS, `${fourthName} is not on the Pung`);
    assert(Math.abs(upper.bounds.min.z - base.bounds.max.z) < EPS);
    assert(Math.abs(upper.center.z - base.center.z - Size.TILE.z) < EPS);
    for (let i = 0; i < bodies.length; i++) for (let j = i + 1; j < bodies.length; j++) {
      assert(!penetrates(bodies[i].bounds, bodies[j].bounds));
    }
    assert.equal(base.slot.links.up, undefined, 'Ordinary Pung must not acquire wall-bottom shading');
    const entries = bodies.map(body => ({
      group: body.slot.group, seat, name: body.name,
      ownsSlot: body.slot.thing !== null && body.slot.thing.slot === body.slot,
    }));
    for (let previous = 0; previous < meld; previous++) for (let tile = 0; tile < 3; tile++) {
      entries.push({ group: 'meld', seat, name: `meld.${previous}.${tile}@${seat}`, ownsSlot: true });
    }
    for (let tile = 0; tile < 10 - 3 * meld; tile++) {
      entries.push({ group: 'hand', seat, name: `hand.${tile}@${seat}`, ownsSlot: true });
    }
    assert.equal(hasExtraDiscardTile(entries, seat, true), false);
    entries.push({ group: 'hand', seat, name: `hand.${10 - 3 * meld}@${seat}`, ownsSlot: true });
    assert.equal(hasExtraDiscardTile(entries, seat, true), true, 'Upper slot remains one meld after replacement draw');
  }
});

const baselineHashes = {
  FOUR_PLAYER: '43a372de42d697868449ea4380a9f1334d99717b6556fb0eb4f12b927c28d17c',
  THREE_PLAYER: '7864d4f2c0c97b27626b2ccf272ba5aea611e596fb8a407139903bad7a0f64e2',
  BAMBOO: 'c32ec31c36ce5c0f587302d993fcadd0814a95f3c83e5a29ab016d349abbc9c0',
  MINEFIELD: 'bb9b9e8f0da46919355f9b223b833c77a5046d80f12654281e424b19f5d76bfe',
  CHANGSHA_FLAT_MELDS: 'dc9b8232ae40370b5306c5321488a7451226003e56a42ce2ffede567f5d158af',
};
function geometryHash(slots) {
  const round = n => Math.abs(n) < 1e-10 ? 0 : +n.toFixed(9);
  const vector = v => v.toArray().map(round);
  const data = slots.map(s => ({
    name: s.name, origin: vector(s.origin), direction: vector(s.direction),
    rotations: s.rotations.map(vector), indexes: s.indexes, seat: s.seat,
    links: s.linkDesc, drawShadow: s.drawShadow, shadowRotation: s.shadowRotation,
    rotateHeld: s.rotateHeld, canFlipMultiple: s.canFlipMultiple, offTable: s.offTable,
  })).sort((a, b) => a.name.localeCompare(b.name));
  return crypto.createHash('sha256').update(JSON.stringify(data)).digest('hex');
}
test('all relay geometry and ordinary Changsha meld geometry/style retain the before-profile hashes', () => {
  for (const variant of ['FOUR_PLAYER', 'THREE_PLAYER', 'BAMBOO', 'MINEFIELD']) {
    assert.equal(geometryHash(makeSlots(variant)), baselineHashes[variant], variant);
  }
  assert.equal(geometryHash(makeSlots('CHANGSHA').filter(slot =>
    slot.group === 'meld' && slot.indexes[1] < 4)), baselineHashes.CHANGSHA_FLAT_MELDS);
});

test('manual/auto initial and unshuffled local bootstrap allocate all108 tiles to real wall slots', () => {
  for (const dealMode of ['manual', 'auto']) for (const dealType of ['INITIAL', 'UNSHUFFLED']) {
    const setup = new Setup();
    setup.setup({ ...Conditions.initial(), gameType: 'CHANGSHA', dealMode, dealType });
    const tiles = [...setup.things.values()].filter(thing => thing.type === 'TILE');
    assert.equal(tiles.length, 108);
    assert(tiles.every(thing => thing.slot.group === 'wall'));
    assert.equal(new Set(tiles.map(thing => thing.slot.name)).size, 108);
  }
});

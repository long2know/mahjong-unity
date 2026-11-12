import test from 'node:test';
import assert from 'node:assert/strict';
import { sourceLoader, gameHarness, thing, plain, require } from './renderer-test-support.mjs';
const { Matrix4, Vector3 } = require('three');

test('beveled tile has outward unit normals, complete face UVs and the exact original outer bounds', () => {
  const { tileGeometry, beveledTileGeometry, TILE_DEPTH } = sourceLoader().load('renderer/gpu/tile-mesh.ts');
  const old = tileGeometry(), mesh = beveledTileGeometry();
  assert(mesh.positions.length > old.positions.length);
  assert(mesh.positions.length / 3 <= 200 && mesh.indices.length / 3 < 128, 'shared geometry stays small');
  for (let axis = 0; axis < 3; axis++) {
    const values = data => Array.from(data).filter((_value, index) => index % 3 === axis);
    assert.equal(Math.min(...values(mesh.positions)), Math.min(...values(old.positions)));
    assert.equal(Math.max(...values(mesh.positions)), Math.max(...values(old.positions)));
  }
  const front = [];
  let diagonal = 0;
  for (let index = 0; index < mesh.positions.length / 3; index++) {
    const normal = new Vector3().fromArray(mesh.normals, index * 3);
    assert(Math.abs(normal.length() - 1) < .000001);
    if ([normal.x, normal.y, normal.z].filter(value => Math.abs(value) > .05).length > 1) diagonal++;
    const position = new Vector3().fromArray(mesh.positions, index * 3);
    assert([position.x, position.y, position.z].every(Number.isFinite));
    if (normal.z > .999 && Math.abs(position.z - TILE_DEPTH / 2) < .000001 && mesh.uvs[index * 2] < 1.01) {
      front.push([mesh.uvs[index * 2], mesh.uvs[index * 2 + 1]]);
      assert(Math.abs(position.x) < .5, 'the face inset leaves a physical ivory chamfer');
    }
    if ([normal.x, normal.y, normal.z].filter(value => Math.abs(value) > .05).length > 1) {
      assert.equal(mesh.uvs[index * 2], 2.5, 'corner facets sample only ivory body, never face artwork');
    }
  }
  assert(diagonal > 0, 'corners need real diagonal normals, not a box shader illusion');
  assert.deepEqual(front.sort(), [[0, 0], [0, 1], [1, 0], [1, 1]]);
  for (let index = 0; index < mesh.indices.length; index += 3) {
    const ids = Array.from(mesh.indices.slice(index, index + 3));
    assert(ids.every(id => id >= 0 && id < mesh.positions.length / 3));
    const [a, b, c] = ids.map(id => new Vector3().fromArray(mesh.positions, id * 3));
    const normal = ids.map(id => new Vector3().fromArray(mesh.normals, id * 3)).reduce((a, b) => a.add(b));
    const cross = b.sub(a).cross(c.sub(a));
    assert(cross.length() > 1e-7 && cross.dot(normal) > 0, 'triangles are nondegenerate and outward wound');
  }
});

test('plastic front/back/side finish preserves art selection and never encodes concealed faces', () => {
  const { tileMaterialId, tileSurface } = sourceLoader().load('renderer/webgl-style.ts');
  const entity = faceIndex => ({ kind: 'TILE',
    appearance: { faceIndex, knownFace: true, showFace: true, backIndex: 0, valueIndex: null } });
  const visible = tileMaterialId(entity(9));
  assert.equal(tileSurface(visible, 0).face, 9);
  assert.notEqual(tileSurface(visible, 0).base, tileSurface(visible, 2).base);
  assert(tileSurface(visible, 1).cap && !tileSurface(visible, 2).cap);
  const gold = entity(9); gold.appearance.backIndex = 1;
  assert.equal(tileSurface(tileMaterialId(gold), 0).face, 9);
  assert.notDeepEqual(plain(tileSurface(tileMaterialId(gold), 1).cap), plain(tileSurface(visible, 1).cap));
  const concealed = [null, 0, 9, 36].map(face => {
    const tile = entity(face); tile.appearance.showFace = false; tile.appearance.knownFace = false;
    return tileMaterialId(tile);
  });
  assert(concealed.every(material => material === concealed[0]), 'secret face changes cannot affect GPU material IDs');
  for (const material of concealed) for (const side of [0, 1, 2]) {
    assert.equal(tileSurface(material, side).face, null);
    assert.equal(tileSurface(material, side).cap !== null, side !== 2, 'only back caps are colored; the body remains ivory');
  }
});

test('four camera-relative discard regions stay on felt, avoid dice/each other, and do not mutate source poses', () => {
  const overlaps = (a, b) => Math.min(a.max.x, b.max.x) - Math.max(a.min.x, b.min.x) > .00001
    && Math.min(a.max.z, b.max.z) - Math.max(a.min.z, b.min.z) > .00001;
  for (const variant of ['CHANGSHA', 'FOUR_PLAYER', 'THREE_PLAYER', 'BAMBOO', 'MINEFIELD']) {
    const h = gameHarness(variant);
    try {
      h.join(); h.snapshot([['4', thing('hand.0@0', 1)]]);
      const { buildWebglPlan, discardRegions } = h.load('renderer/webgl-layout.ts');
      const { rendererViewport } = h.load('renderer/renderer-viewport.ts');
      for (const [width, height] of [[390, 844], [1280, 800]]) for (const boardLayout of ['classic', 'rectangular']) {
        for (const viewSeat of [0, 1, 2, 3]) {
          const frame = h.adapter.refresh();
          frame.session.viewSeat = viewSeat;
          frame.presentation = { ...frame.presentation, boardLayout, handLayout: 'separate' };
          const viewport = rendererViewport(h.adapter.instanceId, 1,
            { width, height, dpr: 1, separateHand: true, insets: { top: 60, left: 0, right: 0, bottom: 0 } });
          const plan = buildWebglPlan(h.adapter.layout, frame, viewport);
          const entities = plain([...plan.entities]), slots = plain([...plan.slots]);
          const regions = [...discardRegions(plan, viewSeat).values()];
          assert.equal(regions.length, 4, `${variant}/${boardLayout}/${viewSeat}`);
          const dice = { min: { x: -.075, z: -.075 }, max: { x: .075, z: .075 } };
          for (const region of regions) {
            assert(region.max.x - region.min.x > .02 && region.max.z - region.min.z > .02, 'regions must remain visible rectangles');
            assert(region.min.x >= plan.bounds.min.x && region.max.x <= plan.bounds.max.x);
            assert(region.min.z >= plan.bounds.min.z && region.max.z <= plan.bounds.max.z);
            assert(!overlaps(region, dice), 'felt regions leave the dice plate clear');
            assert.equal(region.min.y, -.004); assert.equal(region.max.y, -.004);
          }
          for (let a = 0; a < 4; a++) for (let b = a + 1; b < 4; b++) assert(!overlaps(regions[a], regions[b]));
          assert.deepEqual(plain([...plan.entities]), entities);
          assert.deepEqual(plain([...plan.slots]), slots);
        }
      }
    } finally { h.close(); }
  }
});

test('rivers, contact shadows and expanded cue meshes cannot intercept or enlarge canonical picking', () => {
  const loader = sourceLoader();
  const { pickableModels } = loader.load('renderer/webgl-style.ts');
  const { sourcePose, tileModel } = loader.load('renderer/webgl-layout.ts');
  const { pickTile } = loader.load('renderer/gpu/picking.ts');
  const pose = sourcePose({ position: { x: 87, y: 87, z: 2 },
    rotation: { x: 0, y: 0, z: 0, w: 1 }, size: { x: 6, y: 9, z: 4 } });
  const canonical = tileModel(pose), expanded = Float32Array.from(
    new Matrix4().fromArray(canonical).multiply(new Matrix4().makeScale(5, 5, 5)).elements);
  const data = new Float32Array(16 * 5);
  [expanded, expanded, canonical, expanded, expanded].forEach((model, index) => data.set(model, index * 16));
  const picks = pickableModels(data, [null, null, { id: 'opaque-tile' }, null, null], 5);
  assert.deepEqual(Array.from(picks.modelData), Array.from(canonical));
  assert.deepEqual(plain(picks.targets), [{ id: 'opaque-tile' }]);
  assert(pickTile(picks, { origin: [0, 1, 0], direction: [0, -1, 0] }));
  assert.equal(pickTile(picks, { origin: [.031, 1, 0], direction: [0, -1, 0] }), null);
});

test('the real instanced upload/draw path accepts shared beveled geometry and releases its buffers', () => {
  const loader = sourceLoader(), uploads = [], draws = [], buffers = new Set(), vaos = new Set();
  const gl = {
    createBuffer() { const buffer = {}; buffers.add(buffer); return buffer; },
    createVertexArray() { const vao = {}; vaos.add(vao); return vao; },
    deleteBuffer(buffer) { assert(buffers.delete(buffer)); }, deleteVertexArray(vao) { assert(vaos.delete(vao)); },
    bindBuffer() {}, bindVertexArray() {}, bufferData(_target, data) { uploads.push(data); }, bufferSubData() {},
    enableVertexAttribArray() {}, vertexAttribPointer() {}, vertexAttribDivisor() {}, getAttribLocation() { return 0; },
    drawElementsInstanced(_mode, count, _type, _offset, instances) { draws.push({ count, instances }); },
  };
  const { beveledTileGeometry, createTileMesh, setTileInstance, uploadTileInstances, drawTileMesh, disposeTileMesh } =
    loader.load('renderer/gpu/tile-mesh.ts');
  const geometry = beveledTileGeometry(), mesh = createTileMesh(gl, {}, 512, geometry);
  assert(uploads.includes(geometry.positions) && uploads.includes(geometry.normals) && uploads.includes(geometry.indices));
  setTileInstance(mesh, 0, Float32Array.from(new Matrix4().elements), 37);
  uploadTileInstances(gl, mesh); drawTileMesh(gl, mesh);
  assert.deepEqual(draws, [{ count: geometry.indices.length, instances: 1 }]);
  disposeTileMesh(gl, mesh);
  assert.equal(buffers.size, 0); assert.equal(vaos.size, 0);
});

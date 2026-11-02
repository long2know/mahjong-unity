import { test, expect, type Page } from '@playwright/test';
import { BoxGeometry, Group, InstancedMesh, Matrix4, Mesh, MeshLambertMaterial, Quaternion, Texture, Vector3 } from 'three';
import type { AssetLoader } from '../../src/asset-loader';
import { TileThingGroup, MarkerThingGroup, type ThingParams } from '../../src/thing-group';
import { ThingType, type DealType } from '../../src/types';
import type { World } from '../../src/world';

interface RenderGroup {
  startIndex: number;
  meshes: Mesh[];
  instancedMesh: InstancedMesh;
}

interface LiveGame {
  world: World;
  objectView: { thingGroups: Map<string, RenderGroup> };
  client: { things: { update(entries: Array<[string | number, unknown]>): void } };
}

interface PhysicalState {
  hidden: number;
  hiddenCustom: number;
  hiddenInstances: number;
  active: number;
  activeMissing: number;
  activeDouble: number;
  orphans: number;
  asymmetric: number;
  hiddenRaycast: number;
}

const zero = new Matrix4().makeScale(0, 0, 0).toArray();
const position = new Vector3(80, 80, 4);
const parked = new Vector3(0, 0, -100000);
const rotation = new Quaternion();

function fixture(params: ThingParams[]): TileThingGroup {
  const tile = new Mesh(new BoxGeometry(6, 8, 4), new MeshLambertMaterial({ map: new Texture() }));
  const assets = {
    meshes: { tile },
    textures: { tilesLabels: new Texture() },
    make: () => tile.clone(),
    makeMarker: () => tile.clone(),
  } as unknown as AssetLoader;
  const group = new TileThingGroup(assets, new Group());
  group.replace(0, params);
  return group;
}

function renderGroup(group: TileThingGroup): RenderGroup {
  return group as unknown as RenderGroup;
}

function matrix(group: TileThingGroup, index: number): number[] {
  const result = new Matrix4();
  renderGroup(group).instancedMesh.getMatrixAt(index, result);
  return result.toArray();
}

test.describe('renderer hidden physical mesh visibility', () => {
  test('source: initially hidden custom meshes and instances are absent, then reveal at origin', () => {
    const params = [
      { index: 0, type: ThingType.TILE, typeIndex: 0, hidden: true },
      { index: 1, type: ThingType.TILE, typeIndex: 1, hidden: false },
    ];
    const group = fixture(params);
    group.setSimple(1, position, rotation);

    expect(renderGroup(group).meshes[0].visible).toBe(false);
    expect(matrix(group, 0)).toEqual(zero);
    expect(matrix(group, 1)).not.toEqual(zero);

    params[0].hidden = false;
    group.setSimple(0, new Vector3(), rotation);
    expect(matrix(group, 0)).toEqual(new Matrix4().toArray());
    expect(renderGroup(group).meshes[0].visible).toBe(false);
  });

  test('source: a concealed instance clears once and restores at the identical cached transform', () => {
    const params = [{ index: 0, type: ThingType.TILE, typeIndex: 0, hidden: false }];
    const group = fixture(params);
    group.setSimple(0, position, rotation);
    const visible = matrix(group, 0);
    expect(visible).not.toEqual(zero);

    params[0].hidden = true;
    group.setSimple(0, parked, rotation);
    expect(renderGroup(group).meshes[0].visible).toBe(false);
    expect(matrix(group, 0)).toEqual(zero);
    const hiddenVersion = renderGroup(group).instancedMesh.instanceMatrix.version;
    group.setSimple(0, parked, rotation);
    expect(renderGroup(group).instancedMesh.instanceMatrix.version).toBe(hiddenVersion);

    params[0].hidden = false;
    group.setSimple(0, position, rotation);
    expect(matrix(group, 0)).toEqual(visible);
  });

  test('source: a concealed custom mesh clears both representations and reveals exactly once', () => {
    const params = [{ index: 0, type: ThingType.TILE, typeIndex: 0, hidden: false }];
    const group = fixture(params);
    const custom = group.setCustom(0, position, rotation);
    expect(custom.visible).toBe(true);
    expect(matrix(group, 0)).toEqual(zero);

    params[0].hidden = true;
    group.setSimple(0, parked, rotation);
    expect(custom.visible).toBe(false);
    expect(matrix(group, 0)).toEqual(zero);
    expect(group.setCustom(0, position, rotation).visible).toBe(false);

    params[0].hidden = false;
    expect(group.setCustom(0, position, rotation).visible).toBe(true);
    expect(matrix(group, 0)).toEqual(zero);
    group.setSimple(0, position, rotation);
    expect(custom.visible).toBe(false);
    expect(matrix(group, 0)).not.toEqual(zero);
  });

  test('source: relay-shaped params without hidden flags and marker visibility remain supported', () => {
    const group = fixture([{ index: 0, type: ThingType.TILE, typeIndex: 0 }]);
    group.setSimple(0, position, rotation);
    expect(matrix(group, 0)).not.toEqual(zero);
    const version = renderGroup(group).instancedMesh.instanceMatrix.version;
    group.setSimple(0, position, rotation);
    expect(renderGroup(group).instancedMesh.instanceMatrix.version).toBe(version);
    expect(group.setCustom(0, position, rotation).visible).toBe(true);
    group.setSimple(0, position, rotation);
    expect(renderGroup(group).meshes[0].visible).toBe(false);
    expect(matrix(group, 0)).not.toEqual(zero);

    const marker = new MarkerThingGroup({
      makeMarker: () => new Mesh(new BoxGeometry(), new MeshLambertMaterial()),
    } as unknown as AssetLoader, new Group());
    const params = [{ index: 2000, type: ThingType.MARKER, typeIndex: 0, hidden: false }];
    marker.replace(2000, params);
    expect(marker.setCustom(2000, position, rotation).visible).toBe(true);
    params[0].hidden = true;
    expect(marker.setCustom(2000, parked, rotation).visible).toBe(false);
    params[0].hidden = false;
    expect(marker.setCustom(2000, position, rotation).visible).toBe(true);
  });

  // Local fixture input, as in renderer-hidden-park-slot: no server game or
  // gameplay injection. Assertions inspect the actual served render objects.
  async function boot(page: Page, baseURL: string | undefined): Promise<string[]> {
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${baseURL}?variant=changsha&dealMode=manual`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => Boolean((window as unknown as { game?: LiveGame }).game?.world));
    await page.evaluate(() => {
      const g = (window as unknown as { game: LiveGame }).game;
      g.client.things.update([['physical-back', {
        slotName: 'wall.0.0@0', rotationIndex: 0, claimedBy: null,
        heldRotation: { x: 0, y: 0, z: 0, w: 1 }, shiftSlotName: null,
      }]]);
    });
    return errors;
  }

  async function physical(page: Page): Promise<PhysicalState> {
    return page.evaluate(async () => {
      await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
      const g = (window as unknown as { game: LiveGame }).game;
      const w = g.world, group = g.objectView.thingGroups.get('TILE')!;
      const data = group.instancedMesh.instanceMatrix.array;
      const tiles = [...w.things.values()].filter(t => t.type === 'TILE');
      const instances = (index: number): boolean => {
        const offset = (index - group.startIndex) * 16;
        return [0, 1, 2, 4, 5, 6, 8, 9, 10].some(k => data[offset + k] !== 0);
      };
      const custom = (index: number): boolean => group.meshes[index - group.startIndex].visible;
      const hidden = tiles.filter(t => t.hidden), active = tiles.filter(t => !t.hidden);
      return {
        hidden: hidden.length,
        hiddenCustom: hidden.filter(t => custom(t.index)).length,
        hiddenInstances: hidden.filter(t => instances(t.index)).length,
        active: active.length,
        activeMissing: active.filter(t => !custom(t.index) && !instances(t.index)).length,
        activeDouble: active.filter(t => custom(t.index) && instances(t.index)).length,
        orphans: tiles.filter(t => w.slots.get(t.slot.name) !== t.slot).length,
        asymmetric: tiles.filter(t => !t.slot.offTable && t.slot.thing !== t).length,
        hiddenRaycast: w.toSelect().filter(t => w.things.get(t.id)?.hidden).length,
      };
    });
  }

  function assertPhysical(state: PhysicalState): void {
    expect(state.hidden).toBeGreaterThan(100);
    expect(state.active).toBeGreaterThan(100);
    expect(state.hiddenCustom).toBe(0);
    expect(state.hiddenInstances).toBe(0);
    expect(state.activeMissing).toBe(0);
    expect(state.activeDouble).toBe(0);
    expect(state.orphans).toBe(0);
    expect(state.asymmetric).toBe(0);
    expect(state.hiddenRaycast).toBe(0);
  }

  test('browser: hidden backs stay physically absent across a live conditions rebuild', async ({ page, baseURL }) => {
    const errors = await boot(page, baseURL);
    assertPhysical(await physical(page));
    const stable = await page.evaluate(() => {
      const w = (window as unknown as { game: LiveGame }).game.world;
      const park = w.slots.get('hiddenpool@0');
      w.updateConditions({ ...w.conditions, dealMode: 'auto', dealType: 'HANDS' as DealType });
      return w.slots.get('hiddenpool@0') === park;
    });
    expect(stable).toBe(true);
    assertPhysical(await physical(page));
    expect(errors).toEqual([]);
  });

  test('browser: conceal and reveal clear and restore a previously customized real tile', async ({ page, baseURL }) => {
    const errors = await boot(page, baseURL);
    const tile = await page.evaluate(() => {
      const w = (window as unknown as { game: LiveGame }).game.world;
      const real = [...w.things.values()].find(t => t.type === 'TILE' && !t.hidden && t.hiddenHandle === null)!;
      w.setHighlightedThing(real);
      return { index: real.index, slotName: real.slot.name, rotationIndex: real.rotationIndex };
    });
    await physical(page);
    expect(await page.evaluate(index => {
      const group = (window as unknown as { game: LiveGame }).game.objectView.thingGroups.get('TILE')!;
      return group.meshes[index - group.startIndex].visible;
    }, tile.index)).toBe(true);

    await page.evaluate(info => {
      const g = (window as unknown as { game: LiveGame }).game;
      g.client.things.update([['physical-conceal', {
        slotName: info.slotName, rotationIndex: info.rotationIndex, claimedBy: null,
        heldRotation: { x: 0, y: 0, z: 0, w: 1 }, shiftSlotName: null,
      }]]);
    }, tile);
    assertPhysical(await physical(page));
    expect(await page.evaluate(index => (window as unknown as { game: LiveGame }).game.world.things.get(index)?.hidden, tile.index)).toBe(true);

    await page.evaluate(info => {
      const g = (window as unknown as { game: LiveGame }).game;
      g.client.things.update([[info.index, {
        slotName: info.slotName, rotationIndex: info.rotationIndex, claimedBy: null,
        heldRotation: { x: 0, y: 0, z: 0, w: 1 }, shiftSlotName: null,
      }]]);
      g.world.setHighlightedThing(null);
    }, tile);
    assertPhysical(await physical(page));
    expect(await page.evaluate(index => (window as unknown as { game: LiveGame }).game.world.things.get(index)?.hidden, tile.index)).toBe(false);
    expect(errors).toEqual([]);
  });
});

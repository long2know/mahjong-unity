// =============================================================================
//  RENDERER INVARIANT — off-table hidden park slot (Dietrich)
// =============================================================================
//
//  Regression lock for the renderer invariant chain that produced ONE root
//  pageerror per real-play run plus a per-frame TypeError storm:
//
//    root  : `trying to move thing to slot hiddenpool@0, but it doesn't exist`
//            (setup.ts, Setup.replace reslot loop)
//    stack : ws.onmessage -> BaseClient.onMessage -> Collection.onUpdate
//            -> World.onMatch -> World.updateConditions -> Setup.replace
//    cause : the SC-2 park slot `hiddenpool@0` was created LAZILY by World on
//            first hidden-back activation and registered into the map that
//            `Setup.addSlots()` CLEARS on every conditions rebuild. A rebuild
//            (the backend legitimately flips match conditions auto/HANDS ->
//            manual/INITIAL when it applies the manual deal mode) therefore
//            destroyed the slot while 108 Things still lived in it, and the
//            reslot loop threw mid-rebuild — leaving every Thing pointing at a
//            dead slot generation with asymmetric `slot.thing` pointers.
//    storm : `Cannot read properties of undefined (reading 'x')`, thrown by
//            `MouseUi.prepareObjects` -> `Vector3.copy(select.position)` because
//            `World.toSelect()` offered NON-RENDERED parked Things as raycast
//            targets and their `Thing.place()` is undefined.
//
//  Everything below drives the SHIPPED objects through their production entry
//  points (`client.things.update` is exactly what the WS layer calls;
//  `world.updateConditions` is exactly what `World.onMatch` calls) with no
//  server, so the assertions are deterministic. Every one of them fails on the
//  pre-fix bundle.
// =============================================================================

import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';

const PARK = 'hiddenpool@0';

interface SceneReport {
  hasPark: boolean;
  parkOffTable: boolean | null;
  parkGroup: string | null;
  things: number;
  slots: number;
  realTiles: number;
  tileThings: number;
  anonymousBacks: number;
  markers: number;
  visibleMarkerMeshes: number;
  thingIds: number[];
  parkedIds: number[];
  parked: number;
  /** Things whose `slot` is no longer the instance registered under its name. */
  orphanSlotRefs: number;
  /** On-table Things whose `slot.thing` back-pointer isn't the Thing itself. */
  asymmetric: number;
  /** Things whose `place()` is undefined (rotation outside the slot's domain). */
  undefinedPlaces: number;
  select: {
    total: number;
    withoutPosition: number;
    hiddenOffered: number;
    prepareObjectsError: string | null;
  };
}

/** OBSERVE — read the renderer's scene invariants out of the live objects. */
async function readScene(page: Page): Promise<SceneReport> {
  return page.evaluate((park) => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const g = (window as any).game;
    const w = g.world;
    const parkSlot = w.slots.get(park) ?? null;
    let parked = 0, orphanSlotRefs = 0, asymmetric = 0, undefinedPlaces = 0, realTiles = 0;
    let tileThings = 0, markers = 0;
    const parkedIds: number[] = [];
    const realTileLimit = w.conditions.gameType === 'CHANGSHA' ? 108 : 136;
    for (const t of w.things.values()) {
      const name = String(t?.slot?.name ?? '');
      if (name === park) { parked++; parkedIds.push(t.index); }
      if (w.slots.get(name) !== t.slot) orphanSlotRefs++;
      // The park slot is intentionally MULTI-TENANT: many parked Things share it
      // and its `.thing` back-pointer is not authoritative. Symmetry is only an
      // invariant for on-table slots.
      if (name !== park && t.slot?.thing !== t) asymmetric++;
      let place;
      try { place = t.place(); } catch { place = undefined; }
      if (!place) undefinedPlaces++;
      if (t.type === 'TILE') {
        tileThings++;
        if (t.index < realTileLimit) realTiles++;
      }
      if (t.type === 'MARKER') markers++;
    }
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let sel: Array<any> = [];
    try { sel = w.toSelect(); } catch { sel = []; }
    let prepareObjectsError: string | null = null;
    try { g.mouseUi.prepareObjects(); } catch (e) {
      prepareObjectsError = String((e as Error)?.message ?? e);
    }
    return {
      hasPark: Boolean(parkSlot),
      parkOffTable: parkSlot ? parkSlot.offTable === true : null,
      parkGroup: parkSlot ? String(parkSlot.group) : null,
      things: w.things.size,
      slots: w.slots.size,
      realTiles,
      tileThings,
      anonymousBacks: (w.hiddenBackPool ?? []).filter((t: { index: number }) => w.things.get(t.index) === t).length,
      markers,
      visibleMarkerMeshes: (g.objectView.thingGroups.get('MARKER')?.meshes ?? [])
        .filter((mesh: { visible: boolean; parent: unknown }) => mesh.visible && mesh.parent !== null).length,
      thingIds: [...w.things.keys()].sort((a, b) => a - b),
      parkedIds: parkedIds.sort((a, b) => a - b),
      parked,
      orphanSlotRefs,
      asymmetric,
      undefinedPlaces,
      select: {
        total: sel.length,
        withoutPosition: sel.filter((s) => !s || !s.position).length,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        hiddenOffered: sel.filter((s) => { const t = w.things.get(s.id); return Boolean(t && t.hidden); }).length,
        prepareObjectsError,
      },
    };
  }, PARK);
}

/** ADVANCE — activate SC-2 exactly like the WS layer: opaque STRING-keyed things. */
async function activateHiddenBacks(page: Page): Promise<void> {
  await page.evaluate(() => {
    const info = (slotName: string, rotationIndex: number): unknown => ({
      slotName, rotationIndex, claimedBy: null,
      heldRotation: { x: 0, y: 0, z: 0, w: 1 }, shiftSlotName: null,
    });
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const client = (window as any).game.client;
    client.things.update([
      ['renderer-inv-h1', info('wall.0.0@0', 0)],
      // A back bound at a NON-ZERO rotation: releasing it parks a Thing whose
      // rotation is outside the park slot's single-rotation domain.
      ['renderer-inv-h2', info('discard.0.0@1', 2)],
    ]);
    client.things.update([['renderer-inv-h2', null]]);
  });
}

/** ADVANCE — the production conditions-rebuild entry point (World.onMatch). */
async function rebuildConditions(
  page: Page,
  patch: Record<string, unknown>,
): Promise<string | null> {
  return page.evaluate((p) => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const w = (window as any).game.world;
    try {
      w.updateConditions({ ...w.conditions, ...p });
      return null;
    } catch (e) {
      return String((e as Error)?.message ?? e);
    }
  }, patch);
}

async function boot(page: Page, query: string): Promise<string[]> {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(query, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    () => Boolean((window as any).game?.world && (window as any).game?.client),
    undefined,
    { timeout: 40_000 },
  );
  await page.waitForTimeout(1000);
  return errors;
}

test.describe('renderer — off-table hidden park slot', () => {
  test('park slot exists at first paint, before any snapshot can target it', async ({ page, baseURL }) => {
    const errors = await boot(page, `${baseURL}?variant=changsha&dealMode=manual`);
    const scene = await readScene(page);

    // ORDERING INVARIANT: the park slot must be part of the slot set from
    // construction — never created lazily on first use, or a snapshot that
    // reslots a parked Thing races its creation.
    expect(scene.hasPark).toBe(true);
    expect(scene.parkGroup).toBe('hiddenpool');
    // Off-table: never a drop target, never rendered, never raycast.
    expect(scene.parkOffTable).toBe(true);
    // Nothing is parked before SC-2 activates, and the canonical scene is intact.
    expect(scene.parked).toBe(0);
    expect(scene.realTiles).toBe(108);
    expect(scene.orphanSlotRefs).toBe(0);
    expect(scene.asymmetric).toBe(0);
    expect(errors).toEqual([]);
  });

  test('a conditions rebuild preserves the park slot and every parked Thing', async ({ page, baseURL }) => {
    const errors = await boot(page, `${baseURL}?variant=changsha&dealMode=manual`);
    await activateHiddenBacks(page);

    const active = await readScene(page);
    // Non-vacuity: SC-2 really is active — 108 backs exist and the pool parks
    // everything it isn't rendering.
    expect(active.hasPark).toBe(true);
    expect(active.things).toBe(216); // 108 real tiles + 108 anonymous backs; Changsha has no relay marker.
    expect(active.realTiles).toBe(108);
    expect(active.tileThings).toBe(216);
    expect(active.anonymousBacks).toBe(108);
    expect(active.markers).toBe(0);
    expect(active.visibleMarkerMeshes).toBe(0);
    expect(active.parked).toBeGreaterThan(100);
    expect(active.orphanSlotRefs).toBe(0);
    expect(active.undefinedPlaces).toBe(0);

    // The exact production rebuild the backend triggers when it applies the
    // manual deal mode (match conditions auto/HANDS -> manual/INITIAL).
    const threw = await rebuildConditions(page, { dealMode: 'auto', dealType: 'HANDS' });
    expect(threw).toBeNull();

    const after = await readScene(page);
    expect(after.hasPark).toBe(true);
    expect(after.parkOffTable).toBe(true);
    expect(after.things).toBe(216);
    expect(after.realTiles).toBe(108);
    expect(after.tileThings).toBe(216);
    expect(after.anonymousBacks).toBe(108);
    expect(after.markers).toBe(0);
    expect(after.visibleMarkerMeshes).toBe(0);
    expect(after.thingIds).toEqual(active.thingIds);
    expect(after.parkedIds).toEqual(active.parkedIds);
    expect(after.parked).toBe(active.parked);
    // The rebuild must not strand Things in a dead slot generation, and every
    // on-table slot keeps symmetric thing.slot / slot.thing pointers.
    expect(after.orphanSlotRefs).toBe(0);
    expect(after.asymmetric).toBe(0);
    expect(after.undefinedPlaces).toBe(0);
    expect(errors).toEqual([]);
  });

  test('parked / hidden Things are never raycast targets', async ({ page, baseURL }) => {
    const errors = await boot(page, `${baseURL}?variant=changsha&dealMode=manual`);
    await activateHiddenBacks(page);
    await rebuildConditions(page, { dealMode: 'auto', dealType: 'HANDS' });

    const scene = await readScene(page);
    expect(scene.select.total).toBeGreaterThan(0);      // non-vacuous
    // A non-rendered Thing has no on-table place; offering it to the raycaster
    // made MouseUi.prepareObjects call Vector3.copy(undefined) every frame.
    expect(scene.select.hiddenOffered).toBe(0);
    expect(scene.select.withoutPosition).toBe(0);
    expect(scene.select.prepareObjectsError).toBeNull();
    expect(errors).toEqual([]);
  });

  test('relay variants keep their scene and their park slot across variant flips', async ({ page, baseURL }) => {
    const errors = await boot(page, `${baseURL}?variant=four_player`);

    // FOUR_PLAYER: upstream 136-tile catalog + 60 sticks + the Riichi slot groups.
    expect(await rebuildConditions(page, {
      gameType: 'FOUR_PLAYER', fives: '111', points: '25', dealType: 'INITIAL',
    })).toBeNull();
    const relay = await readScene(page);
    expect(relay.things).toBe(197);        // 136 tiles + 60 sticks + marker
    expect(relay.realTiles).toBe(136);
    expect(relay.tileThings).toBe(136);
    expect(relay.markers).toBe(1);
    await expect.poll(async () => (await readScene(page)).visibleMarkerMeshes).toBe(1);
    expect(relay.hasPark).toBe(true);
    expect(relay.parked).toBe(0);          // relay never parks anything
    expect(relay.orphanSlotRefs).toBe(0);
    expect(relay.asymmetric).toBe(0);
    expect(relay.undefinedPlaces).toBe(0);
    expect(relay.select.withoutPosition).toBe(0);
    expect(relay.select.prepareObjectsError).toBeNull();

    // ...and back into Changsha's 108-tile catalog.
    expect(await rebuildConditions(page, {
      gameType: 'CHANGSHA', fives: '000', points: '25', dealType: 'HANDS',
    })).toBeNull();
    const back = await readScene(page);
    expect(back.things).toBe(108);
    expect(back.realTiles).toBe(108);
    expect(back.tileThings).toBe(108);
    expect(back.markers).toBe(0);
    expect(back.visibleMarkerMeshes).toBe(0);
    expect(back.hasPark).toBe(true);
    expect(back.orphanSlotRefs).toBe(0);
    expect(back.asymmetric).toBe(0);

    expect(await rebuildConditions(page, {
      gameType: 'FOUR_PLAYER', fives: '111', points: '25', dealType: 'INITIAL',
    })).toBeNull();
    const restored = await readScene(page);
    expect(restored.things).toBe(197);
    expect(restored.realTiles).toBe(136);
    expect(restored.tileThings).toBe(136);
    expect(restored.markers).toBe(1);
    await expect.poll(async () => (await readScene(page)).visibleMarkerMeshes).toBe(1);
    expect(restored.parked).toBe(0);
    expect(restored.orphanSlotRefs).toBe(0);
    expect(restored.asymmetric).toBe(0);
    expect(restored.undefinedPlaces).toBe(0);
    expect(restored.select.prepareObjectsError).toBeNull();
    expect(errors).toEqual([]);
  });
});

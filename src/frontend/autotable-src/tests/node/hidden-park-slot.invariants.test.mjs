// Off-table park-slot lifetime invariants (Dietrich) — browser-free backstop for
// the renderer fix in src/setup.ts + src/world.ts + src/slot.ts.
//
// The Playwright spec `tests/e2e/renderer-hidden-park-slot.spec.ts` is the primary
// proof (it drives the SHIPPED objects). This pure Node model locks the ORDERING
// CLASS deterministically without a WebGL browser: a synthetic slot that lives in
// the same map `Setup.addSlots()` clears must be owned by Setup and re-registered
// on every rebuild, or a conditions rebuild destroys it under the Things that are
// parked in it.
//
// For non-vacuousness the BUGGY variant (the pre-fix arrangement: World creates the
// park slot lazily and registers it into Setup's map) is asserted to VIOLATE the
// invariants the FIXED variant upholds.
//
// Run: node --test tests/node/hidden-park-slot.invariants.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';

const PARK = 'hiddenpool@0';
const CANONICAL = ['wall.0.0@0', 'wall.0.1@0', 'hand.0@0', 'hand.1@0', 'discard.0.0@1'];

// -- minimal Slot/Thing/Setup model (setup.ts / slot.ts / thing.ts subset) ------
const makeSlot = (name, rotations = 1, offTable = false) => ({
  name, offTable, thing: null,
  places: Array.from({ length: rotations }, (_, i) => ({ rotation: i })),
});
const placeOf = (thing) => thing.slot.places[thing.rotationIndex];

function makeSetup({ setupOwnsPark }) {
  const setup = {
    slots: new Map(),
    things: new Map(),
    park: setupOwnsPark ? makeSlot(PARK, 1, true) : null,
    addSlots() {
      // Setup.addSlots(): the canonical slot set is rebuilt from scratch.
      this.slots.clear();
      for (const name of CANONICAL) this.slots.set(name, makeSlot(name, 4));
      // FIXED: the SAME park instance is re-registered on every rebuild.
      if (this.park) this.slots.set(this.park.name, this.park);
    },
    isPark(slot) { return this.park !== null && slot === this.park; },
    // Setup.replace(): remember each Thing's slot NAME, rebuild the slot set,
    // then re-resolve every name.
    replace() {
      const byName = new Map();
      for (const t of this.things.values()) {
        t.slot.thing = null;             // prepareMove()
        byName.set(t.index, t.slot.name);
      }
      this.addSlots();
      for (const t of this.things.values()) {
        const name = byName.get(t.index);
        const slot = this.slots.get(name);
        if (slot === undefined) {
          throw new Error(`trying to move thing to slot ${name}, but it doesn't exist`);
        }
        if (this.isPark(slot)) { t.slot = slot; continue; } // multi-tenant park
        if (slot.thing !== null) throw new Error(`slot not empty: ${t.index} ${name}`);
        t.slot = slot; slot.thing = t;
      }
    },
  };
  setup.addSlots();
  return setup;
}

// -- World subset: hidden-back pool + parking + raycast targets -----------------
function makeWorld(setup, { worldCreatesPark }) {
  const world = {
    setup,
    slots: setup.slots,
    things: setup.things,
    seat: 0,
    lazyPark: null,
    park() {
      if (worldCreatesPark) {
        // BUGGY (pre-fix): created on first use and registered into the SAME map
        // `addSlots()` clears, so a rebuild destroys it under the parked Things.
        if (this.lazyPark === null) this.lazyPark = makeSlot(PARK, 1, true);
        this.slots.set(PARK, this.lazyPark);
        return this.lazyPark;
      }
      return this.setup.park;
    },
    parkThing(thing, { normalizeRotation }) {
      const park = this.park();
      if (thing.slot !== park && thing.slot.thing === thing) thing.slot.thing = null;
      thing.slot = park;
      // FIXED: the park slot has a single rotation, so a Thing parked while
      // carrying a slot-specific rotation would index past `places`.
      if (normalizeRotation) thing.rotationIndex = 0;
    },
    // World.toSelect(): raycast targets.
    toSelect({ skipHidden }) {
      const out = [];
      if (this.seat === null) return out;
      for (const t of this.things.values()) {
        if (skipHidden && t.hidden) continue;
        out.push({ ...placeOf(t), id: t.index });
      }
      return out;
    },
  };
  return world;
}

function seedScene(setup, world, opts) {
  // one on-table tile + one concealed real parked at a non-zero rotation
  const onTable = { index: 0, hidden: false, rotationIndex: 1, slot: setup.slots.get('wall.0.0@0') };
  onTable.slot.thing = onTable;
  setup.things.set(0, onTable);
  const concealed = { index: 1, hidden: false, rotationIndex: 2, slot: setup.slots.get('discard.0.0@1') };
  concealed.slot.thing = concealed;
  setup.things.set(1, concealed);
  // the SC-2 pool: anonymous backs are born parked
  for (let i = 0; i < 4; i++) {
    const back = { index: 108 + i, hidden: true, rotationIndex: 0, slot: world.park() };
    setup.things.set(back.index, back);
  }
  concealed.hidden = true;
  world.parkThing(concealed, opts);
  return { onTable, concealed };
}

const invariants = (setup, world) => {
  let parked = 0, orphan = 0, asym = 0, undefPlace = 0;
  for (const t of setup.things.values()) {
    if (t.slot.name === PARK) parked++;
    if (setup.slots.get(t.slot.name) !== t.slot) orphan++;
    if (t.slot.name !== PARK && t.slot.thing !== t) asym++;
    if (!placeOf(t)) undefPlace++;
  }
  return { parked, orphan, asym, undefPlace, hasPark: setup.slots.has(PARK) };
};

test('FIXED: the park slot survives a conditions rebuild with Things parked in it', () => {
  const setup = makeSetup({ setupOwnsPark: true });
  const world = makeWorld(setup, { worldCreatesPark: false });

  // ORDERING: the park slot exists before anything can target it.
  assert.equal(setup.slots.has(PARK), true, 'park slot must exist at construction');
  assert.equal(setup.slots.get(PARK).offTable, true, 'park slot must be off-table');

  seedScene(setup, world, { normalizeRotation: true });
  const before = invariants(setup, world);
  assert.equal(before.parked, 5, 'non-vacuous: 4 backs + 1 concealed real are parked');
  assert.equal(before.undefPlace, 0);

  assert.doesNotThrow(() => setup.replace(), 'rebuild must not throw');

  const after = invariants(setup, world);
  assert.equal(after.hasPark, true, 'park slot survives the rebuild');
  assert.equal(after.parked, 5, 'every parked Thing keeps its off-table home');
  assert.equal(after.orphan, 0, 'no Thing points at a dead slot generation');
  assert.equal(after.asym, 0, 'on-table thing.slot / slot.thing stay symmetric');
  assert.equal(after.undefPlace, 0, 'every Thing has a place its slot can express');
  assert.equal(setup.things.size, 6, 'representation count is preserved');
});

test('BUGGY (pre-fix): a lazily World-created park slot is destroyed by the rebuild', () => {
  const setup = makeSetup({ setupOwnsPark: false });
  const world = makeWorld(setup, { worldCreatesPark: true });

  assert.equal(setup.slots.has(PARK), false, 'pre-fix: no park slot until first use');
  seedScene(setup, world, { normalizeRotation: false });
  assert.equal(setup.slots.has(PARK), true, 'pre-fix: park appears only on activation');

  assert.throws(
    () => setup.replace(),
    /trying to move thing to slot hiddenpool@0, but it doesn't exist/,
    'pre-fix rebuild throws the root renderer exception',
  );

  // ...and abandons the scene mid-rebuild.
  const after = invariants(setup, world);
  assert.equal(after.hasPark, false, 'pre-fix: park slot destroyed under parked Things');
  assert.ok(after.orphan > 0, 'pre-fix: Things stranded on a dead slot generation');
});

test('FIXED: parked Things are never offered as raycast targets', () => {
  const setup = makeSetup({ setupOwnsPark: true });
  const world = makeWorld(setup, { worldCreatesPark: false });
  seedScene(setup, world, { normalizeRotation: true });

  const targets = world.toSelect({ skipHidden: true });
  assert.ok(targets.length > 0, 'non-vacuous: on-table tiles are still selectable');
  assert.equal(targets.filter((t) => t.rotation === undefined).length, 0,
    'every raycast target has a place');
  const hiddenIds = [...setup.things.values()].filter((t) => t.hidden).map((t) => t.index);
  assert.equal(targets.filter((t) => hiddenIds.includes(t.id)).length, 0,
    'no non-rendered Thing is a raycast target');
});

test('BUGGY (pre-fix): un-normalized parked rotations yield position-less raycast targets', () => {
  const setup = makeSetup({ setupOwnsPark: true });
  const world = makeWorld(setup, { worldCreatesPark: false });
  const { concealed } = seedScene(setup, world, { normalizeRotation: false });

  assert.equal(concealed.rotationIndex, 2, 'parked while carrying a discard rotation');
  assert.equal(placeOf(concealed), undefined, 'park slot cannot express that rotation');
  const targets = world.toSelect({ skipHidden: false });
  assert.ok(
    targets.some((t) => t.id === concealed.index && t.rotation === undefined),
    'pre-fix: a parked Thing reaches the raycaster with no position — ' +
    'MouseUi.prepareObjects then calls Vector3.copy(undefined) every frame',
  );
});

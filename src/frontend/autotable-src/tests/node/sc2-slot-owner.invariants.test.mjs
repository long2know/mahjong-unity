// Blocker A (Bishop rev2) — browser-free invariant backstop for the SC-2 single-owner
// slot reconciliation shipped in src/world.ts (onThings / reconcileRealThingVisibility /
// releaseHiddenBacks / placeHiddenBacks / parkThing / recycleBack).
//
// The Playwright scene gate (window.world Three.js inspection) is the primary proof, but
// this pure Node model exercises the SAME pointer discipline deterministically so the
// invariants are locked without a WebGL browser. It faithfully transcribes the fixed apply
// order (VACATE non-entitled reals + released backs -> BIND entitled reals -> PLACE backs,
// all with symmetric thing.slot / slot.thing detach) and, for non-vacuousness, a BUGGY apply
// mirroring the pre-fix code (hide real without vacating its slot; place/release backs
// without vacating the displaced side) and asserts the buggy variant VIOLATES the invariants
// the fixed one upholds.
//
// Run: node --test tests/node/sc2-slot-owner.invariants.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';

const HIDDEN_BASE = 108;
const POOL = 108;
const isBack = (t) => t.index >= HIDDEN_BASE;

// -- minimal Slot/Thing model (world.ts Thing/Slot subset) -----------------------
function makeModel() {
  const park = { name: 'park', thing: null };
  const slots = new Map([['park', park]]);
  const things = new Map();
  const handleToBack = new Map();
  const slot = (name) => {
    let s = slots.get(name);
    if (!s) { s = { name, thing: null }; slots.set(name, s); }
    return s;
  };
  for (let i = 0; i < POOL; i++) {
    const s = slot(`wall.${i}`);
    const t = { index: i, hidden: false, hiddenHandle: null, slot: s };
    s.thing = t; things.set(i, t);
  }
  for (let i = 0; i < POOL; i++) {
    things.set(HIDDEN_BASE + i, { index: HIDDEN_BASE + i, hidden: true, hiddenHandle: null, slot: park });
  }
  return { park, slots, things, handleToBack, slot };
}

// -- pure plan (transcribes src/sc2-hidden-pool.ts, already contract-tested) ------
function reconcileHiddenBacks(hidden, prevHandles, full) {
  const place = [], release = [], seen = new Set();
  for (const [handle, info] of hidden) {
    seen.add(handle);
    if (info === null) release.push(handle);
    else place.push({ handle, slotName: info.slotName });
  }
  if (full) for (const h of prevHandles) if (!seen.has(h)) release.push(h);
  return { place, release };
}
function reconcileRealVisibility(numericKeys, poolSize, full) {
  const present = new Set(numericKeys);
  const show = [...present].filter((id) => id >= 0 && id < poolSize);
  if (!full) return { show, hide: [] };
  const hide = [];
  for (let id = 0; id < poolSize; id++) if (!present.has(id)) hide.push(id);
  return { show, hide };
}

// -- FIXED apply — transcribes world.ts onThings SC-2 discipline ------------------
function parkThing(m, thing) {
  if (thing.slot !== m.park && thing.slot.thing === thing) thing.slot.thing = null;
  thing.slot = m.park;
}
function recycleBack(m, back) {
  parkThing(m, back);
  back.hidden = true;
  if (back.hiddenHandle !== null) { m.handleToBack.delete(back.hiddenHandle); back.hiddenHandle = null; }
}
function applyFixed(m, entries, full) {
  const numeric = entries.filter(([k]) => typeof k === 'number');
  const hidden = entries.filter(([k]) => typeof k === 'string');
  const backPlan = reconcileHiddenBacks(hidden, m.handleToBack.keys(), full);
  const realPlan = reconcileRealVisibility(numeric.filter(([, i]) => i !== null).map(([k]) => k), POOL, full);
  for (const id of realPlan.show) { const r = m.things.get(id); if (r) r.hidden = false; }
  for (const id of realPlan.hide) { const r = m.things.get(id); if (r) { r.hidden = true; parkThing(m, r); } }
  for (const h of backPlan.release) { const b = m.handleToBack.get(h); if (b) recycleBack(m, b); }
  for (const [id, info] of numeric) { if (info === null) continue; const t = m.things.get(id); if (t) t.slot.thing = null; }
  for (const [id, info] of numeric) {
    if (info === null) continue;
    const s = m.slots.get(info.slotName); if (!s || s.thing === null) continue;
    if (s.thing.index === id) continue;
    if (s.thing.hiddenHandle !== null) recycleBack(m, s.thing);
    s.thing = null;
  }
  for (const [id, info] of numeric) {
    if (info === null) continue;
    const t = m.things.get(id); const s = m.slot(info.slotName); if (!t) continue;
    if (s.thing !== null && s.thing !== t) { if (s.thing.hiddenHandle !== null) recycleBack(m, s.thing); s.thing = null; }
    t.slot = s; t.hidden = false; s.thing = t;
  }
  for (const { handle, slotName } of backPlan.place) {
    const s = m.slot(slotName);
    let back = m.handleToBack.get(handle);
    if (!back) { back = [...m.things.values()].find((b) => isBack(b) && b.hiddenHandle === null && b.hidden); if (!back) continue; back.hiddenHandle = handle; m.handleToBack.set(handle, back); }
    if (back.slot !== s && back.slot.thing === back) back.slot.thing = null;
    const occ = s.thing;
    if (occ !== null && occ !== back) {
      if (occ.hiddenHandle !== null) recycleBack(m, occ);
      else { if (occ.slot === s) occ.slot = m.park; occ.hidden = true; }
      s.thing = null;
    }
    back.slot = s; back.hidden = false; s.thing = back;
  }
}

// -- BUGGY apply — pre-fix (asymmetric) for non-vacuous discrimination ------------
function applyBuggy(m, entries, full) {
  const numeric = entries.filter(([k]) => typeof k === 'number');
  const hidden = entries.filter(([k]) => typeof k === 'string');
  const backPlan = reconcileHiddenBacks(hidden, m.handleToBack.keys(), full);
  for (const h of backPlan.release) { const b = m.handleToBack.get(h); if (b) { b.hidden = true; b.hiddenHandle = null; b.slot = m.park; m.handleToBack.delete(h); } }
  for (const { handle, slotName } of backPlan.place) {
    const s = m.slot(slotName); let back = m.handleToBack.get(handle);
    if (!back) { back = [...m.things.values()].find((b) => isBack(b) && b.hiddenHandle === null && b.hidden); if (!back) continue; back.hiddenHandle = handle; m.handleToBack.set(handle, back); }
    if (s.thing !== null && s.thing !== back) s.thing = null;
    back.slot = s; back.hidden = false; s.thing = back;
  }
  const realPlan = reconcileRealVisibility(numeric.filter(([, i]) => i !== null).map(([k]) => k), POOL, full);
  for (const id of realPlan.show) { const r = m.things.get(id); if (r) r.hidden = false; }
  for (const id of realPlan.hide) { const r = m.things.get(id); if (r) r.hidden = true; }
  for (const [id, info] of numeric) { if (info === null) continue; const t = m.things.get(id); if (t) t.slot.thing = null; }
  for (const [id, info] of numeric) { if (info === null) continue; const s = m.slots.get(info.slotName); if (!s || s.thing === null) continue; if (s.thing.index === id) continue; s.thing = null; }
  for (const [id, info] of numeric) { if (info === null) continue; const t = m.things.get(id); const s = m.slot(info.slotName); if (!t) continue; if (s.thing !== null && s.thing !== t) s.thing = null; t.slot = s; t.hidden = false; s.thing = t; }
}

// -- invariants ------------------------------------------------------------------
function violations(m) {
  const v = [];
  for (const s of m.slots.values()) if (s.thing !== null && s.thing.slot !== s) v.push(`asym:${s.name}#${s.thing.index}`);
  const bySlot = new Map();
  for (const t of m.things.values()) {
    if (t.hidden || t.slot === m.park) continue;
    const arr = bySlot.get(t.slot) ?? []; arr.push(t.index); bySlot.set(t.slot, arr);
  }
  for (const [s, ids] of bySlot) if (ids.length > 1) v.push(`cores:${s.name}=[${ids}]`);
  for (const t of m.things.values()) if (!t.hidden && t.slot !== m.park && !isBack(t) && t.slot.thing !== t) v.push(`stray:${t.index}@${t.slot.name}`);
  return v;
}
function renderedWall(m) {
  return [...m.things.values()].filter((t) => !t.hidden && t.slot !== m.park && t.slot.name.startsWith('wall.')).length;
}
const wallEntry = (i) => [`h:${i}`, { slotName: `wall.${i}` }];

test('FIXED: spectator full — 108 backs render, 108 reals parked, invariants hold', () => {
  const m = makeModel();
  applyFixed(m, Array.from({ length: 108 }, (_, i) => wallEntry(i)), true);
  assert.equal(violations(m).length, 0, violations(m).join(','));
  assert.equal(renderedWall(m), 108);
  assert.equal([...m.things.values()].filter((t) => !isBack(t) && !t.hidden).length, 0);
});

test('FIXED: seated full — 14 own numeric + 94 backs, total 108, no leak/co-residency', () => {
  const m = makeModel();
  const numeric = Array.from({ length: 14 }, (_, i) => [i, { slotName: `hand.${i}` }]);
  const hidden = Array.from({ length: 94 }, (_, i) => wallEntry(100 + i));
  applyFixed(m, [...numeric, ...hidden], true);
  assert.equal(violations(m).length, 0, violations(m).join(','));
  assert.equal([...m.things.values()].filter((t) => !t.hidden && t.slot !== m.park).length, 108);
});

test('FIXED: incremental reveal (no tombstone) — real replaces back, back recycled, no double', () => {
  const m = makeModel();
  applyFixed(m, Array.from({ length: 108 }, (_, i) => wallEntry(i)), true);
  applyFixed(m, [[5, { slotName: 'wall.5' }]], false);
  const v = violations(m);
  assert.equal(v.length, 0, v.join(','));
  const slot5 = m.slots.get('wall.5');
  assert.equal(slot5.thing.index, 5);
  assert.equal(m.things.get(5).hidden, false);
  assert.equal([...m.things.values()].filter((t) => !t.hidden && t.slot === slot5).length, 1);
});

test('FIXED: incremental conceal — back covers a real, real hidden+parked, no stray', () => {
  const m = makeModel();
  const numeric = Array.from({ length: 14 }, (_, i) => [i, { slotName: `H.${i}` }]);
  applyFixed(m, numeric, true);
  applyFixed(m, [['h:3', { slotName: 'H.3' }]], false);
  const v = violations(m);
  assert.equal(v.length, 0, v.join(','));
  assert.equal(m.things.get(3).hidden, true);
  assert.notEqual(m.slots.get('H.3').thing.index, 3);
});

test('NON-VACUOUS: buggy apply VIOLATES invariants on incremental reveal', () => {
  const m = makeModel();
  applyBuggy(m, Array.from({ length: 108 }, (_, i) => wallEntry(i)), true);
  applyBuggy(m, [[5, { slotName: 'wall.5' }]], false);
  assert.ok(violations(m).length > 0, 'buggy reveal should leave a co-residency/stray violation');
});

test('NON-VACUOUS: buggy apply leaves a stray rendered real on incremental conceal', () => {
  const m = makeModel();
  const numeric = Array.from({ length: 14 }, (_, i) => [i, { slotName: `H.${i}` }]);
  applyBuggy(m, numeric, true);
  applyBuggy(m, [['h:3', { slotName: 'H.3' }]], false);
  assert.ok(violations(m).length > 0, 'buggy conceal should leave the real rendering at its stolen slot');
});

test('FIXED: stale local optimistic scatter (real at a discard slot) is parked; 108 render reps, one per slot', () => {
  // Hudson rev2 — the legacy setup.deal(0) scatters local optimistic tiles (e.g. a real at
  // discard.0.0 / table center) BEFORE the authoritative snapshot. The first full snapshot
  // (spectator / pre-deal synth wall: all 108 hidden) must RECONCILE that stale object away:
  // the real is hidden + parked (no stray center tile), the discard slot is vacated, and the
  // rendered set is exactly 108 anonymous backs — one per authoritative wall slot, zero doubles.
  const m = makeModel();
  const stray = m.things.get(2);
  const discard = m.slot('discard.0.0@0');
  stray.slot.thing = null; stray.slot = discard; discard.thing = stray; stray.hidden = false;

  applyFixed(m, Array.from({ length: 108 }, (_, i) => wallEntry(i)), true);

  assert.equal(m.things.get(2).hidden, true, 'stray local real must be concealed');
  assert.equal(m.slots.get('discard.0.0@0').thing, null, 'stray discard slot must be vacated');
  assert.equal(violations(m).length, 0, violations(m).join(','));
  const reps = [...m.things.values()].filter((t) => !t.hidden && t.slot !== m.park);
  assert.equal(reps.length, 108, 'exactly 108 authoritative render reps');
  assert.equal(new Set(reps.map((t) => t.slot)).size, 108, 'one render rep per authoritative slot');
});

test('NON-VACUOUS: buggy apply leaves the stale local scatter tile pointer on the discard slot', () => {
  const m = makeModel();
  const stray = m.things.get(2);
  const discard = m.slot('discard.0.0@0');
  stray.slot.thing = null; stray.slot = discard; discard.thing = stray; stray.hidden = false;
  applyBuggy(m, Array.from({ length: 108 }, (_, i) => wallEntry(i)), true);
  // Buggy hide sets hidden=true but never vacates the slot pointer, so the discard/center
  // slot still references the stale real (asymmetric) — exactly the leak the fixed path
  // eliminates by symmetric parking (test above vacates the slot to null).
  assert.equal(m.slots.get('discard.0.0@0').thing?.index, 2);
});

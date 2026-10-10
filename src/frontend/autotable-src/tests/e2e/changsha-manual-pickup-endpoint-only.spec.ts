// G17 (OWNED by hudson-1) — Vasquez R-1 §D10/§B + FINAL SC-4 pickup semantics
// (Ripley PARENT-LOCKED 2026-08-07 11:14, ripley-SC4-FINAL-single-trigger-slot.md;
// supersedes targetHandles + batch-actionable): `pickup.targetSlots: string[]` of
// EXACTLY length 1 = the single exposed-end trigger tile = state.Wall[0] (public
// render-slot name — NO raw tile ids, NO opaque handles). `pickup.count` (4 for
// BreakPointMarked/PickupRound1-3, 1 for SingleTile/DealerExtra) = the SERVER batch.
// ONLY targetSlots[0] is interactable; the other (count-1) batch tiles, any
// `batchPreviewSlots` (display-only), and every other wall tile REJECT (no hover-
// select/hold/drag/mutation/take). A real-pointer click on targetSlots[0] ⇒ the
// server takes the whole batch, and the client's `pickup.take` payload = {seatIndex,
// count} ONLY (zero client tile authority — no id/handle/slot). Assertions:
//   S1 exactly-one-front: targetSlots present, length==1, a real rendered wall
//      slot the viewer sees (the exposed front), count ∈ {1,4};
//   S2 no raw id / no handle leak: the entry is a slot-NAME (public position),
//      never a numeric id and never an opaque handle string;
//   S3 wrong endpoint (a non-front wall tile incl. the other batch tiles) /
//      off-phase / AUTO ⇒ inert (no move/take); S4 the one front slot ⇒ whole
//      batch of `count`, server-confirmed (no optimistic local move);
//   S5 explicit clear tombstones the designation post-batch.
// PHYSICAL-STACK extension (Ripley 2026-08-07 10:28, F1-independent — render
// occlusion + world footprint, not the break frame):
//   S6 targetSlots[0] is the TOP/reachable tile of the frontier stack (its `up`
//      link empty), NEVER the bottom/occluded layer; any lower sibling is occluded;
//   S7 after a count=1 pickup the SAME stack's lower layer becomes the next
//      reachable trigger (single-tile phases go top→bottom within a stack);
//   S8 a count=4 press on the single front trigger consumes EXACTLY two adjacent
//      front stacks (2 footprints × 2 layers = 4 tiles).
// (The dealer×dice break ANCHOR position is G4's F1-blocked concern.)
// WIRE GUARD + FAIL-CLOSED (Ripley 11:23 — catches the live nextTileSlots-vs-
// targetSlots mismatch): S10 inspects the RAW `pickup` frame and asserts the field
// is `targetSlots` (NOT nextTileSlots/targetHandles/targetTileIds) with length
// EXACTLY 1 — a silent backend/client field no-match fails at the wire, not just
// the happy path. S11 asserts fail-closed: when targetSlots is missing/empty/
// length>1, NO wall tile is interactable (the client must not fall back to any-wall).
// S12 (Vasquez F2 reachability): the single actionable tile is the REACHABLE TOP of
// the frontier stack (targetSlots[0], form wall.{col}.1@{seat}) — a real click TAKES;
// the OCCLUDED bottom (layer 0) of that same stack is INERT (canSelect blocks it);
// take payload = {seatIndex,count}. Guards against a designation pointing at an
// unreachable/occluded bottom tile.
// AUTO reject is hudson-2's. F1-independent (Bishop co-derives targetSlots).
import { test, expect, type Page } from '@playwright/test';
import { buildGameUrl, makeConfig, dismissLobbyAndTour, ensureConnected, takeSeatByClick, clickDeal, waitForPlayableHand, hasExtraHandTile, readIsMyPickupTurn, takePickup, pressWallTargetByHover } from './_playability';
import { pollWithStallGuard, readCeremonyKey, type StallGuardOutcome } from './_ceremony-progress';
import { realDragWallTile, recordEvidence, shot } from './_uat_red';

// Real per-batch ceremony drive (Hicks's centralized clickDeal only ROLLS the
// dealer's dice to START the manual ceremony now — the client auto-drive that
// used to self-complete every batch was removed; see S13 below). The seated
// human dealer must press the rendered #pickup-take-btn (the shared
// takePickup helper, same control manual-deal-ceremony.spec.ts drives) for
// every one of the five ceremony batches before the table reaches
// AwaitingDiscard. Bots auto-pick their own turns between the human's; this
// only presses when it is genuinely our turn (readIsMyPickupTurn), so it
// never fabricates a press the human didn't need to make.
async function driveHumanPickupsUntilPlayable(page: Page): Promise<StallGuardOutcome> {
  // Progress-aware, stall-guarded drive of the human dealer's OWN pickup batches to
  // ceremony completion (the hand holds its drawn tile ⇒ hasExtraHandTile). Replaces the
  // fixed 45s window that, under CI mobile saturation (run 31594744369, job 94107472857),
  // expired while the deal was still genuinely advancing — parked on a LIVE BreakPointMarked
  // (pickup count 4) — leaving the callers' post-ceremony gates (POST-deal wall-inert @362,
  // D1 tombstone @923) asserting against MID-CEREMONY state. We press ONLY when it is
  // genuinely our turn; between our batches the bots auto-pick, so the authoritative ceremony
  // fingerprint (readCeremonyKey) keeps changing and the stall timer resets. A GENUINE
  // no-progress park (dice never roll / a handoff never reaches our seat) surfaces as
  // hasExtraHandTile staying false and the returned outcome's stalled/capped flag — the
  // CALLER's own post-ceremony assertion then fails with that diagnostic. This helper never
  // asserts, sleeps-to-pass, retries, or skips. stallMs 45s ≫ the worst measured inter-batch
  // gap; capMs 120s is bounded under the callers' 150s test timeouts (minus connect/seat/deal).
  return pollWithStallGuard(page, async () => {
    const done = await hasExtraHandTile(page);
    if (!done && await readIsMyPickupTurn(page)) await takePickup(page);
    return { done, key: await readCeremonyKey(page) };
  }, { stallMs: 45_000, capMs: 120_000, pollMs: 400 });
}

// FINAL designation reader. GATE = pickup.targetSlots = EXACTLY ONE public
// exposed-front render-slot name (state.Wall[0]); count is the server batch size.
async function readDesignation(page: Page): Promise<any> {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game; const w = g?.world;
    const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
    if (!pu || pu.seatIndex !== g?.client?.seat) return null;
    const wallSlots = new Set<string>();
    if (w?.things) for (const t of w.things.values()) if (t?.slot?.group === 'wall') wallSlots.add(String(t.slot?.name));
    const gate: string[] | null = Array.isArray((pu as any).targetSlots) ? (pu as any).targetSlots.map(String) : null;
    const preview: string[] | null = Array.isArray((pu as any).batchPreviewSlots) ? (pu as any).batchPreviewSlots.map(String) : null;
    const slotNameRe = /^wall\.\d+\.\d+@\d+$/;
    let kind: string | null = null;
    if (gate && gate.length) {
      if (gate.every((v) => slotNameRe.test(v))) kind = 'slot-names';
      else if (gate.every((v) => /^-?\d+$/.test(v))) kind = 'raw-ids';
      else kind = 'opaque-or-other';   // opaque handle strings do NOT belong in targetSlots
    }
    return {
      phase: pu.phase, count: pu.count, gate, gateLen: gate ? gate.length : 0, kind,
      preview, previewLen: preview ? preview.length : 0,
      exactlyOneFront: !!gate && gate.length === 1,
      isSlotNames: kind === 'slot-names',
      mapsToRenderedWall: !!gate && gate.length > 0 && gate.every((s) => wallSlots.has(s)),
      rawIdOrHandleLeak: kind === 'raw-ids' || kind === 'opaque-or-other',
    };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

// Real-click the single designated exposed-front slot vs a non-designated wall
// tile (matches by PUBLIC slot name). Right (the one front slot) ⇒ whole batch of
// `count` to hand; wrong (a non-front wall tile, incl. the other batch tiles) ⇒ inert.
// ── ACTOR-SCOPED MEASUREMENT (Hudson adjudication 2026-08-11) ────────────────────
// The wall is SHARED state: three bots take their own batches concurrently inside
// any settle window, so a `wallBefore - wallAfter === count` equality gate reports
// a false NEGATIVE on a correct take (the wall drops by more than my batch) and a
// false POSITIVE on an inert press (the wall drops although I did nothing). Hudson
// adjudicated the client/backend correct — no pacing or batchPreview defect — so
// these gates now assert only signals scoped to THIS viewer:
//   • handDelta  — tiles entering MY hand (`hand.*@0`); no bot can change it;
//   • frames     — outbound `pickup.take` frames THIS client emitted: the direct
//                  causal record of what our pointer actually requested.
// A correct take = handDelta === count AND exactly one emitted frame.
// An inert press = handDelta === 0 AND zero emitted frames.
interface TakeFrame { seatIndex: number | null; count: number | null; keys: string[] }
interface PressOutcome { ok: boolean; handBefore: number; handAfter: number; handDelta: number; frames: TakeFrame[] }

// Install BEFORE page.goto — records every outbound pickup.take this client emits.
function installTakeRecorder(page: Page): TakeFrame[] {
  const out: TakeFrame[] = [];
  page.on('websocket', (ws) => ws.on('framesent', (ev: any) => {
    let raw = '';
    try { raw = typeof ev.payload === 'string' ? ev.payload : Buffer.from(ev.payload).toString('utf8'); } catch { return; }
    if (!/pickup/.test(raw) || !/take/.test(raw)) return;
    try {
      const msg = JSON.parse(raw);
      for (const e of (msg?.entries ?? [])) {
        if (!Array.isArray(e) || String(e[0]) !== 'pickup' || String(e[1]) !== 'take') continue;
        const p = (e[2] ?? {}) as Record<string, unknown>;
        out.push({
          seatIndex: typeof p.seatIndex === 'number' ? p.seatIndex : null,
          count: typeof p.count === 'number' ? p.count : null,
          keys: Object.keys(p),
        });
      }
    } catch { /* non-JSON frame — ignore */ }
  }));
  return out;
}

/** OBSERVE — tiles in THIS viewer's own hand. Bot-immune (`hand.*@0` is seat-local). */
async function myHandCount(page: Page): Promise<number> {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world;
    let hand = 0;
    if (w?.things) for (const t of w.things.values()) if (/^hand\.\d+@0$/.test(String(t?.slot?.name ?? ''))) hand++;
    return hand;
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

/** ADVANCE — one real pointer press at (cx,cy); returns the actor-scoped outcome. */
async function pressAt(page: Page, cx: number, cy: number, takes: TakeFrame[], settleMs = 1200): Promise<PressOutcome> {
  const handBefore = await myHandCount(page);
  const mark = takes.length;
  await page.mouse.move(cx, cy); await page.waitForTimeout(120);
  await page.mouse.down(); await page.waitForTimeout(80); await page.mouse.up();
  await page.waitForTimeout(settleMs);
  const handAfter = await myHandCount(page);
  return { ok: true, handBefore, handAfter, handDelta: handAfter - handBefore, frames: takes.slice(mark) };
}

// TARGET press (Hudson 2026-08-11 projection-offset fix) — the reachable-top
// designated trigger's world CENTER can project ~16 px BELOW its angled clickable
// face after a wall shift, so a bare center press raycasts to nothing and emits no
// take. `pressWallTargetByHover` (shared, _playability.ts) settles then grid-scans a
// REAL pointer across the tile footprint until world.hovered === the exact trigger,
// hard-records that hover, and only then issues a genuine mouse down/up. Returns the
// same actor-scoped PressOutcome as pressAt plus the hover-match proof; `matched`
// MUST be asserted by callers so a real miss is a loud diagnostic, never a swallowed
// no-op. INERT probes keep pressAt/pressSlotByName (projected center) on purpose.
interface TargetPressOutcome extends PressOutcome { matched: boolean; hovered: string | null }
async function pressTargetByHover(page: Page, name: string, takes: TakeFrame[], settleMs = 1200): Promise<TargetPressOutcome> {
  const r = await pressWallTargetByHover(page, name, takes, { settleMs });
  return { ok: r.found, handBefore: r.handBefore, handAfter: r.handAfter, handDelta: r.handDelta, frames: r.frames, matched: r.matched, hovered: r.hovered };
}

// Real-click the single designated exposed-front slot, then a NON-designated wall
// tile, reporting each press's actor-scoped outcome.
async function clickWallByDesignationAndCount(page: Page, values: string[], takes: TakeFrame[]): Promise<{ target: TargetPressOutcome; nonTarget: PressOutcome }> {
  // NON-TARGET picker (projected center is fine for an INERT probe — the tile is
  // non-selectable, so a center press that hits it OR misses is inert either way).
  const pickNonTarget = async () => page.evaluate((values) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game; const w = g?.world; const camera = g?.mainView?.camera; const main = document.getElementById('main');
    const rect = main?.getBoundingClientRect();
    if (camera) { try { camera.parent?.updateMatrixWorld(true); camera.updateMatrixWorld(true); camera.matrixWorldInverse?.copy(camera.matrixWorld).invert(); } catch { /* */ } }
    const set = new Set(values.map(String));
    const proj = (p: any) => { const mw = camera.matrixWorldInverse.elements, pm = camera.projectionMatrix.elements; const vx = mw[0]*p.x+mw[4]*p.y+mw[8]*p.z+mw[12], vy = mw[1]*p.x+mw[5]*p.y+mw[9]*p.z+mw[13], vz = mw[2]*p.x+mw[6]*p.y+mw[10]*p.z+mw[14], vw = mw[3]*p.x+mw[7]*p.y+mw[11]*p.z+mw[15]; const cx = pm[0]*vx+pm[4]*vy+pm[8]*vz+pm[12]*vw, cy = pm[1]*vx+pm[5]*vy+pm[9]*vz+pm[13]*vw, cw = pm[3]*vx+pm[7]*vy+pm[11]*vz+pm[15]*vw; return { sx: (rect?.left??0)+(cx/cw+1)*0.5*(rect?.width??0), sy: (rect?.top??0)+(1-cy/cw)*0.5*(rect?.height??0) }; };
    let hand = 0; for (const t of w.things.values()) if (/^hand\.\d+@0$/.test(String(t?.slot?.name ?? ''))) hand++;
    for (const t of w.things.values()) {
      if (t?.slot?.group !== 'wall' || (t.claimedBy !== null && t.claimedBy !== undefined)) continue;
      const up = t.slot?.links?.up; if (up && up.thing) continue;
      if (set.has(String(t.slot?.name))) continue;   // skip the designated trigger(s)
      const s = proj(t.place().position);
      return { ok: true, cx: Math.round(s.sx), cy: Math.round(s.sy), handBefore: hand };
    }
    return { ok: false, handBefore: hand };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, values);
  // TARGET — grid-scan to the exact designated trigger's face, then real press.
  const target = await pressTargetByHover(page, String(values[0]), takes);
  // NON-TARGET — a non-designated reachable wall top; press its projected center; must be inert.
  const wp = await pickNonTarget();
  const nonTarget = wp.ok && typeof wp.cx === 'number' && typeof wp.cy === 'number'
    ? await pressAt(page, wp.cx, wp.cy, takes)
    : { ok: false, handBefore: wp.handBefore, handAfter: wp.handBefore, handDelta: 0, frames: [] as TakeFrame[] };
  return { target, nonTarget };
}

// PHYSICAL-STACK extension (F1-independent — uses render occlusion + world
// footprint, not the break frame). Reads the SINGLE designated front trigger and
// classifies it: reachable ⟺ no tile occupies its slot's `up` link; top-of-stack
// ⟺ its footprint sibling (same world x,y) sits below it. `filterMine` restricts
// to my own pickup window; the timeline reader (readAnyPickupStack) does not.
async function readStackDesignation(page: Page, filterMine = true): Promise<any> {
  return page.evaluate((filterMine) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game; const w = g?.world;
    const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
    if (!pu) return null;
    if (filterMine && pu.seatIndex !== g?.client?.seat) return null;
    const target: string[] | null = Array.isArray((pu as any).targetSlots) ? (pu as any).targetSlots.map(String) : null;
    const wall: any[] = [];
    if (w?.things) for (const t of w.things.values()) if (t?.slot?.group === 'wall') {
      let p = { x: 0, y: 0, z: 0 }; try { const pl = t.place(); p = { x: pl.position.x, y: pl.position.y, z: pl.position.z }; } catch { /* */ }
      const up = t.slot?.links?.up;
      wall.push({ name: String(t.slot?.name), reachable: !(up && up.thing), x: p.x, y: p.y, z: p.z });
    }
    const byName: Record<string, any> = {}; for (const t of wall) byName[t.name] = t;
    const t0 = target && target.length ? byName[target[0]] : null;
    const sib = t0 ? wall.find((t) => t.name !== t0.name && Math.abs(t.x - t0.x) < 3 && Math.abs(t.y - t0.y) < 3) : null;
    return {
      phase: pu.phase, count: pu.count, seatIndex: pu.seatIndex,
      targetLen: target ? target.length : 0,
      targetName: target && target.length ? target[0] : null,
      targetFound: !!t0,
      targetReachable: t0 ? t0.reachable : null,
      targetIsTopOfStack: (t0 && sib) ? (t0.z >= sib.z) : (t0 ? true : null),
      hasOccludedSibling: !!sib, siblingReachable: sib ? sib.reachable : null,
      footprint: t0 ? { x: Math.round(t0.x), y: Math.round(t0.y) } : null,
    };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, filterMine);
}

// Group wall tiles into physical stacks by world footprint (x,y); a full stack
// has 2 layers. Used to prove a count=4 batch removes EXACTLY two adjacent stacks.
async function wallStacks(page: Page): Promise<Array<{ x: number; y: number; layers: number }>> {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world; const pts: Array<{ x: number; y: number }> = [];
    if (w?.things) for (const t of w.things.values()) if (t?.slot?.group === 'wall') { let p = { x: 0, y: 0 }; try { const pl = t.place(); p = { x: pl.position.x, y: pl.position.y }; } catch { /* */ } pts.push({ x: p.x, y: p.y }); }
    const stacks: Array<{ x: number; y: number; layers: number }> = [];
    for (const p of pts) { const s = stacks.find((q) => Math.abs(q.x - p.x) < 3 && Math.abs(q.y - p.y) < 3); if (s) s.layers++; else stacks.push({ x: Math.round(p.x), y: Math.round(p.y), layers: 1 }); }
    return stacks;
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

/** OBSERVE — of the given wall slots, those that are genuinely REACHABLE tops
 *  (no tile occupying their `up` link). Only a reachable top is a valid physical
 *  click probe: pressing an occluded lower layer's projected coords raycasts to
 *  the tile above it, which makes the probe meaningless. */
async function reachableTops(page: Page, slots: string[]): Promise<string[]> {
  return page.evaluate((slots: string[]) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world; const set = new Set(slots.map(String)); const out: string[] = [];
    if (w?.things) for (const t of w.things.values()) {
      if (t?.slot?.group !== 'wall' || !set.has(String(t.slot?.name))) continue;
      const up = t.slot?.links?.up; if (up && up.thing) continue;
      if (t.claimedBy !== null && t.claimedBy !== undefined) continue;
      out.push(String(t.slot.name));
    }
    return out;
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, slots);
}

/** OBSERVE — hover the given slot's projected coords and report what the renderer
 *  actually picked. Proves the raycast resolves to the reachable TOP, never the
 *  occluded tile beneath it. */
async function hoverAndReadPick(page: Page, name: string): Promise<{ ok: boolean; hovered: string | null }> {
  const s = await page.evaluate((name) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game; const w = g?.world; const camera = g?.mainView?.camera; const main = document.getElementById('main');
    const rect = main?.getBoundingClientRect();
    if (camera) { try { camera.parent?.updateMatrixWorld(true); camera.updateMatrixWorld(true); camera.matrixWorldInverse?.copy(camera.matrixWorld).invert(); } catch { /* */ } }
    for (const t of w.things.values()) {
      if (t?.slot?.group !== 'wall' || String(t.slot?.name) !== name) continue;
      const p = t.place().position; const mw = camera.matrixWorldInverse.elements, pm = camera.projectionMatrix.elements;
      const vx = mw[0]*p.x+mw[4]*p.y+mw[8]*p.z+mw[12], vy = mw[1]*p.x+mw[5]*p.y+mw[9]*p.z+mw[13], vz = mw[2]*p.x+mw[6]*p.y+mw[10]*p.z+mw[14], vw = mw[3]*p.x+mw[7]*p.y+mw[11]*p.z+mw[15];
      const cx = pm[0]*vx+pm[4]*vy+pm[8]*vz+pm[12]*vw, cy = pm[1]*vx+pm[5]*vy+pm[9]*vz+pm[13]*vw, cw = pm[3]*vx+pm[7]*vy+pm[11]*vz+pm[15]*vw;
      return { ok: true, cx: Math.round((rect?.left??0)+(cx/cw+1)*0.5*(rect?.width??0)), cy: Math.round((rect?.top??0)+(1-cy/cw)*0.5*(rect?.height??0)) };
    }
    return { ok: false, cx: 0, cy: 0 };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, name);
  if (!s.ok) return { ok: false, hovered: null };
  await page.mouse.move(s.cx, s.cy);
  await page.waitForTimeout(250);
  const hovered = await page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world; const h = w?.hovered;
    return h ? String(h?.slot?.name ?? '') : null;
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
  return { ok: true, hovered };
}

/** ADVANCE — press ONE named wall slot and return the actor-scoped outcome. */
async function pressSlotByName(page: Page, name: string, takes: TakeFrame[], settleMs = 1200): Promise<PressOutcome | null> {
  const s = await page.evaluate((name) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game; const w = g?.world; const camera = g?.mainView?.camera; const main = document.getElementById('main');
    const rect = main?.getBoundingClientRect();
    if (camera) { try { camera.parent?.updateMatrixWorld(true); camera.updateMatrixWorld(true); camera.matrixWorldInverse?.copy(camera.matrixWorld).invert(); } catch { /* */ } }
    for (const t of w.things.values()) {
      if (t?.slot?.group !== 'wall' || String(t.slot?.name) !== name) continue;
      const p = t.place().position; const mw = camera.matrixWorldInverse.elements, pm = camera.projectionMatrix.elements;
      const vx = mw[0]*p.x+mw[4]*p.y+mw[8]*p.z+mw[12], vy = mw[1]*p.x+mw[5]*p.y+mw[9]*p.z+mw[13], vz = mw[2]*p.x+mw[6]*p.y+mw[10]*p.z+mw[14], vw = mw[3]*p.x+mw[7]*p.y+mw[11]*p.z+mw[15];
      const cx = pm[0]*vx+pm[4]*vy+pm[8]*vz+pm[12]*vw, cy = pm[1]*vx+pm[5]*vy+pm[9]*vz+pm[13]*vw, cw = pm[3]*vx+pm[7]*vy+pm[11]*vz+pm[15]*vw;
      return { ok: true, cx: Math.round((rect?.left??0)+(cx/cw+1)*0.5*(rect?.width??0)), cy: Math.round((rect?.top??0)+(1-cy/cw)*0.5*(rect?.height??0)) };
    }
    return { ok: false, cx: 0, cy: 0 };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, name);
  if (!s.ok) return null;
  return pressAt(page, s.cx, s.cy, takes, settleMs);
}

// Scan RAW received WS frames for the most-recent pickup entry and return its
// value object — protocol Entry = [kind, key, value] tuple. Used to guard the WIRE
// field name (`targetSlots`, NOT nextTileSlots/targetHandles/targetTileIds) so a
// backend/client field mismatch (silent no-match) is caught at the wire, not just
// via the client's parsed state.
function scanForPickup(node: any): any {
  if (Array.isArray(node)) {
    if (node.length >= 3 && node[0] === 'pickup' && node[2] && typeof node[2] === 'object' && !Array.isArray(node[2])) return node[2];
    for (const el of node) { const r = scanForPickup(el); if (r) return r; }
  } else if (node && typeof node === 'object') {
    for (const v of Object.values(node)) { const r = scanForPickup(v); if (r) return r; }
  }
  return null;
}
function extractRawPickup(recv: string[]): any {
  for (let i = recv.length - 1; i >= 0; i--) { let m: any; try { m = JSON.parse(recv[i]); } catch { continue; } const p = scanForPickup(m); if (p) return p; }
  return null;
}


test.describe('G17 manual pickup endpoint-only (§D10/§B/§E2)', () => {
  test('manual PRE-ceremony: wall press is inert', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-pre-${Date.now()}`, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await page.waitForTimeout(1500);
    const drag = await realDragWallTile(page);
    recordEvidence('g17-manual-pre-ceremony.json', { isMyPickupTurn: drag.isMyPickupTurn, held: drag.held });
    expect(drag.held?.isHolding ?? false, 'pre-ceremony wall tile must NOT be held').toBe(false);
    expect(drag.held?.claimedBy ?? null, 'pre-ceremony wall tile must NOT be claimed').toBeNull();
  });

  test('manual POST-deal (AwaitingDiscard): wall press is inert', async ({ page }, testInfo) => {
    testInfo.setTimeout(150_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-post-${Date.now()}`, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page);
    // The table no longer self-completes the ceremony (D5 removed the client
    // auto-drive), so without a real press per batch this test would sample a
    // MID-CEREMONY table — where `isMyPickupTurn` is legitimately true and the
    // wall IS interactable — and assert the post-deal invariant against the
    // wrong phase. Drive every batch through the rendered #pickup-take-btn so
    // the assertions below genuinely observe AwaitingDiscard.
    const drive = await driveHumanPickupsUntilPlayable(page);
    // Ferro predicate 2: converge to the AwaitingDiscard epoch (hasExtraHandTile && !isMyPickupTurn)
    // before probing the wall. The drive above only proves the dealer drew its extra tile; a LIVE
    // same-hand opening batch (BreakPointMarked count4 seat0) can still read isMyPickupTurn true for
    // a snapshot after hasExtraHandTile flips, and that is the exact wrong-epoch the RED cell hit —
    // asserting the inert-wall contract mid-pickup-turn. Gating on BOTH authoritative signals means
    // the wall is only probed once this seat has genuinely left its pickup turn. This replaces the
    // redundant waitForPlayableHand (itself only a hasExtraHandTile wait) plus the fixed 1500ms
    // settle. A genuine failure to leave the pickup turn surfaces as stalled/capped (done=false)
    // with diagnostics; the assertion below then fails — never hidden, no sleep-to-pass.
    const epoch = await pollWithStallGuard(page, async () => {
      const done = (await hasExtraHandTile(page)) && !(await readIsMyPickupTurn(page));
      return { done, key: await readCeremonyKey(page) };
    }, { stallMs: 30_000, capMs: 45_000, pollMs: 300 });
    const drag = await realDragWallTile(page);
    await shot(page, 'g17-post-deal-drag.png');
    recordEvidence('g17-manual-post-deal.json', { isMyPickupTurn: drag.isMyPickupTurn, held: drag.held,
      stallGuard: { done: drive.done, stalled: drive.stalled, capped: drive.capped, elapsedMs: drive.elapsedMs, keyChanges: drive.keyChanges, maxIdleMs: drive.maxIdleMs },
      epochGuard: { done: epoch.done, stalled: epoch.stalled, capped: epoch.capped, elapsedMs: epoch.elapsedMs, keyChanges: epoch.keyChanges, maxIdleMs: epoch.maxIdleMs } });
    expect(drag.isMyPickupTurn, `post-deal is not a pickup turn (drive: done=${drive.done} stalled=${drive.stalled} capped=${drive.capped}; epoch hasExtra&&!myPickupTurn: done=${epoch.done} stalled=${epoch.stalled} capped=${epoch.capped} elapsedMs=${epoch.elapsedMs} maxIdleMs=${epoch.maxIdleMs})`).toBe(false);
    expect(drag.held?.isHolding ?? false, 'post-deal wall tile must NOT be held').toBe(false);
  });

  test('S1/S2: pickup ships targetSlots == EXACTLY ONE exposed-front slot (public name, no raw id/handle)', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-sig-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    const seen: any[] = [];
    // Progress-aware, stall-guarded observation (replaces the fixed 22s window that flaked
    // under CI saturation, run 31576171218): wait for the dealer's pickup window to target
    // our seat while the authoritative ceremony fingerprint keeps advancing (turn/pickup/
    // hand — server-pushed). A GENUINE RollingDice stall (the window never targets our seat
    // ⇒ no progress) is surfaced by `seen.length > 0` below — NOT hidden. stallMs 45s ≫ the
    // worst measured inter-progress gap under saturation; capMs 90s < the 120s test timeout
    // minus connect/seat/deal setup.
    const guard = await pollWithStallGuard(page, async () => {
      const d = await readDesignation(page);
      if (d) { const key = d.phase + ':' + d.count; if (!seen.some((s) => s.phase + ':' + s.count === key)) seen.push({ phase: d.phase, count: d.count, gateLen: d.gateLen, kind: d.kind, exactlyOneFront: d.exactlyOneFront, isSlotNames: d.isSlotNames, mapsToRenderedWall: d.mapsToRenderedWall, rawIdOrHandleLeak: d.rawIdOrHandleLeak }); }
      const awaiting = await page.evaluate(() => { const t = (window as any).game?.client?.turn; return !!(t && t.awaitingDiscard); });
      return { done: seen.length > 0 || awaiting, key: await readCeremonyKey(page) };
    }, { stallMs: 45_000, capMs: 90_000, pollMs: 300 });
    recordEvidence('g17-pickup-signal.json', { windowsSeen: seen.length, windows: seen, stallGuard: { done: guard.done, stalled: guard.stalled, capped: guard.capped, elapsedMs: guard.elapsedMs, keyChanges: guard.keyChanges, maxIdleMs: guard.maxIdleMs } });
    expect(seen.length, 'must observe a manual pickup window targeting my seat').toBeGreaterThan(0);
    for (const s of seen) {
      expect([1, 4], `pickup ${s.phase} server batch count must be 4 (BreakPointMarked/Round1-3) or 1 (SingleTile/DealerExtra); got ${s.count}`).toContain(s.count);
      expect(s.exactlyOneFront, `S1: pickup ${s.phase} targetSlots must contain EXACTLY ONE exposed-front slot (Wall[0]); got len=${s.gateLen}`).toBe(true);
      expect(s.isSlotNames, `S2: pickup ${s.phase} targetSlots must be a PUBLIC slot NAME, never a raw id or opaque handle; kind=${s.kind}`).toBe(true);
      expect(s.rawIdOrHandleLeak, `S2: pickup ${s.phase} targetSlots must NOT leak a raw tile id or opaque handle`).toBe(false);
      expect(s.mapsToRenderedWall, `S1: pickup ${s.phase} targetSlots must name a real rendered wall slot the viewer sees (the exposed front)`).toBe(true);
    }
  });

  test('S3/S4: the ONE front slot takes the whole batch (server-confirmed); every other wall tile inert', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-int-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    const takes = installTakeRecorder(page);
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    let d: any = null; const t0 = Date.now();
    while (Date.now() - t0 < 22000) {
      d = await readDesignation(page);
      if (d && d.gate && d.gateLen) break;
      if (await page.evaluate(() => { const t = (window as any).game?.client?.turn; return !!(t && t.awaitingDiscard); })) break;
      await page.waitForTimeout(250);
    }
    recordEvidence('g17-endpoint-interaction.json', { designation: d ? { phase: d.phase, count: d.count, gateLen: d.gateLen, kind: d.kind } : null,
      note: 'RED@200cad4: pickup ships no targetSlots, so endpoint-only gating cannot hold; any wall press currently takes.' });

    expect(d && d.gate && d.exactlyOneFront,
      `endpoint-only interaction requires pickup.targetSlots == exactly one exposed-front slot; got ${JSON.stringify(d)}`).toBe(true);
    if (d && d.gate && d.gateLen) {
      const out = await clickWallByDesignationAndCount(page, d.gate, takes);
      recordEvidence('g17-endpoint-actor-scoped.json', { count: d.count, target: out.target, nonTarget: out.nonTarget,
        note: 'Actor-scoped: handDelta is `hand.*@0` (bot-immune) and frames are THIS client\'s outbound pickup.take. Shared-wall deltas are NOT used — three bots take concurrently.' });
      // S4 — the designated press is the causal actor: exactly one command, whole batch.
      expect(out.target.matched, `S4: the real pointer must resolve world.hovered to the EXACT designated front slot ${d.gate[0]} before pressing (grid-scan defeats the ~16px angled-face projection offset); hovered=${out.target.hovered}`).toBe(true);
      expect(out.target.frames.length, `S4: the ONE exposed-front press must emit EXACTLY ONE pickup.take; emitted=${out.target.frames.length}`).toBe(1);
      expect(out.target.handDelta, `S4: pressing the ONE exposed-front slot must move the whole batch of ${d.count} into MY hand; handDelta=${out.target.handDelta}`).toBe(d.count);
      // S3 — a non-designated wall tile is inert: zero command, zero hand movement.
      expect(out.nonTarget.frames.length, `S3: a non-front wall tile (incl. the other batch tiles) must emit ZERO pickup.take; emitted=${out.nonTarget.frames.length}`).toBe(0);
      expect(out.nonTarget.handDelta, `S3: a non-front wall press must not change MY hand; handDelta=${out.nonTarget.handDelta}`).toBe(0);
    }
  });

  test('S9 (FINAL SC-4 parent-locked): pickup.take payload = {seatIndex,count} ONLY + batchPreviewSlots inert', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    // OUTBOUND ws capture (observation only — no injection/emit).
    const takes = installTakeRecorder(page);
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-take-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    let d: any = null; const t0 = Date.now();
    while (Date.now() - t0 < 22000) {
      d = await readDesignation(page);
      if (d && d.gate && d.gateLen) break;
      if (await page.evaluate(() => { const t = (window as any).game?.client?.turn; return !!(t && t.awaitingDiscard); })) break;
      await page.waitForTimeout(250);
    }
    // SC-4 preview probe — batchPreviewSlots MINUS targetSlots[0]. The trigger is
    // itself a member of its own batch preview, so probing the raw preview set would
    // click the LEGITIMATE trigger and correctly take: that is a spec artefact, not a
    // product defect (Hudson adjudication). Only the NON-trigger preview slots are
    // display-only and must be inert.
    //
    // RAYCAST CORRECTNESS (Hudson final): a preview slot on the LOWER layer must NOT be
    // probed by a physical click. Projecting an occluded tile's world position to screen
    // coords and pressing there is an INVALID probe — the ray at those coords correctly
    // hits the TOP tile that occludes it, so the resulting take is right behaviour, not a
    // fail-closed breach. Occlusion is asserted non-physically in S12 (canSelect / hovered).
    // Here the real non-trigger press uses a genuinely REACHABLE adjacent-stack TOP.
    const trigger: string | null = d && d.gate && d.gateLen ? String(d.gate[0]) : null;
    const previewOnly: string[] = (d?.preview ?? []).map(String).filter((s: string) => s !== trigger);
    const previewTops: string[] = previewOnly.length ? await reachableTops(page, previewOnly) : [];
    const previewProbes: Array<{ slot: string; handDelta: number; frames: number }> = [];
    const previewSkipped: string[] = [];
    let targetPress: TargetPressOutcome | null = null;
    let payloadKeys: string[] | null = null;
    if (d && d.gate && d.gateLen) {
      for (const slot of previewTops) {
        // Re-read the LIVE designation immediately before each press: the ceremony can
        // advance between the snapshot above and this press, and a slot that has since
        // become the current trigger would legitimately take. Skipping those removes
        // the last non-defect explanation for a preview slot emitting a command.
        const live = await readDesignation(page);
        const liveTrigger = live && live.gate && live.gateLen === 1 ? String(live.gate[0]) : null;
        if (liveTrigger !== null && liveTrigger === slot) { previewSkipped.push(slot); continue; }
        const p = await pressSlotByName(page, slot, takes);
        if (p) previewProbes.push({ slot, handDelta: p.handDelta, frames: p.frames.length });
      }
      // the single trigger press → grid-scan to its exact face, then real press;
      // the server takes the whole batch and we capture the outbound take frame.
      targetPress = await pressTargetByHover(page, trigger!, takes);
      await page.waitForTimeout(400);
      const last = targetPress && targetPress.frames.length ? targetPress.frames[targetPress.frames.length - 1] : null;
      payloadKeys = last ? last.keys : null;
    }
    recordEvidence('g17-take-payload.json', { designation: d ? { gateLen: d.gateLen, previewLen: d.previewLen, count: d.count } : null,
      trigger, previewOnly, previewProbes, previewSkipped, targetPress, takePayloadKeys: payloadKeys, totalTakeFrames: takes.length,
      note: 'Actor-scoped (Hudson): inert ⟺ zero outbound pickup.take AND unchanged `hand.*@0`. Preview probe excludes targetSlots[0], which is a legitimate trigger.' });

    // FINAL SC-4: single-trigger-slot; take carries ZERO tile authority.
    expect(d && d.exactlyOneFront, `SC-4: pickup.targetSlots must be EXACTLY length 1 (single trigger); got ${JSON.stringify(d && { gateLen: d.gateLen })}`).toBe(true);
    for (const p of previewProbes) {
      expect(p.frames, `SC-4: non-trigger batchPreviewSlot ${p.slot} (display-only) must emit ZERO pickup.take; emitted=${p.frames}`).toBe(0);
      expect(p.handDelta, `SC-4: non-trigger batchPreviewSlot ${p.slot} must not change MY hand; handDelta=${p.handDelta}`).toBe(0);
    }
    expect(targetPress, 'SC-4: the single trigger slot must be pressable').not.toBeNull();
    expect(targetPress!.matched, `SC-4: the real pointer must resolve world.hovered to the EXACT trigger ${trigger} before pressing; hovered=${targetPress!.hovered}`).toBe(true);
    expect(targetPress!.frames.length, `SC-4: the single trigger press must emit EXACTLY ONE pickup.take; emitted=${targetPress!.frames.length}`).toBe(1);
    expect(targetPress!.handDelta, `SC-4: the single trigger press must move the whole batch of ${d?.count} into MY hand; handDelta=${targetPress!.handDelta}`).toBe(d.count);
    expect(payloadKeys, 'SC-4: an outbound pickup.take frame must be observed').not.toBeNull();
    if (payloadKeys) {
      const extra = payloadKeys.filter((k) => k !== 'seatIndex' && k !== 'count');
      expect(extra, `SC-4: pickup.take payload must be {seatIndex,count} ONLY — no tile id/handle/slot; extra=${JSON.stringify(extra)}`).toEqual([]);
    }
  });

  test('S10 (wire guard): RAW pickup frame field == targetSlots len1 (not nextTileSlots/targetHandles/targetTileIds)', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    // capture RAW incoming frames (observation only)
    const recv: string[] = [];
    page.on('websocket', (ws) => ws.on('framereceived', (ev: any) => { try { recv.push(typeof ev.payload === 'string' ? ev.payload : Buffer.from(ev.payload).toString('utf8')); } catch { /* */ } }));
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-wire-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    // let the ceremony emit a pickup frame
    const t0 = Date.now();
    while (Date.now() - t0 < 22000) { if (extractRawPickup(recv)) break; if (await page.evaluate(() => { const t = (window as any).game?.client?.turn; return !!(t && t.awaitingDiscard); })) break; await page.waitForTimeout(250); }
    const pu = extractRawPickup(recv);
    const keys = pu ? Object.keys(pu) : [];
    const arrLen = (k: string) => pu && Array.isArray(pu[k]) ? pu[k].length : -1;
    const tsLen = arrLen('targetSlots');
    const ntsLen = arrLen('nextTileSlots');
    // Frost baseline: backend emits nextTileSlots length=count; frontend needs
    // targetSlots length 1. Reject ANY multi-element batch designation.
    const multiElementBatch = ['nextTileSlots', 'targetSlots', 'targetTileIds', 'targetHandles'].some((k) => arrLen(k) > 1);
    recordEvidence('g17-wire-fieldname.json', { observedPickup: !!pu, keys, targetSlotsLen: tsLen, nextTileSlotsLen: ntsLen, multiElementBatch, hasNextTileSlots: keys.includes('nextTileSlots'), hasTargetHandles: keys.includes('targetHandles'), hasTargetTileIds: keys.includes('targetTileIds'),
      note: 'WIRE guard for the Frost baseline (backend nextTileSlots length=count vs frontend targetSlots length1). RED@200cad4: served build emits no targetSlots (absent/renamed).' });
    // WIRE FIELD-NAME + CARDINALITY DISCRIMINATOR — catches a silent backend/client no-match.
    expect(pu, 'must observe a RAW pickup frame during the manual ceremony').not.toBeNull();
    expect(keys.includes('nextTileSlots'), `raw pickup must NOT use the retired field 'nextTileSlots' (Frost baseline: backend emits nextTileSlots length=${ntsLen}); keys=${JSON.stringify(keys)}`).toBe(false);
    expect(keys.includes('targetHandles'), 'raw pickup must NOT use the retired field targetHandles').toBe(false);
    expect(keys.includes('targetTileIds'), 'raw pickup must NOT use the retired field targetTileIds').toBe(false);
    expect(multiElementBatch, `raw pickup must NOT carry a MULTI-ELEMENT batch designation (single-trigger only); nextTileSlots=${ntsLen} targetSlots=${tsLen}`).toBe(false);
    expect(keys.includes('targetSlots'), `raw pickup must carry the field 'targetSlots'; keys=${JSON.stringify(keys)}`).toBe(true);
    expect(tsLen, `raw pickup.targetSlots must be length EXACTLY 1 (exact top Wall[0]); got ${tsLen}`).toBe(1);
  });

  test('S11 (fail-closed): with NO valid targetSlots, NO wall tile is interactable (no any-wall fallback)', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-failclosed-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    const takes = installTakeRecorder(page);
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    // takePickup catches click failures; a successful outcome + an automatic
    // outbound take alone must not impersonate a genuine human gesture.
    const pickupClicks = await page.evaluateHandle(() => {
      const observed = { count: 0 };
      document.addEventListener('click', (event) => {
        if (event.isTrusted && event.target instanceof Element && event.target.closest('#pickup-take-btn')) observed.count++;
      }, true);
      return observed;
    });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    // ── S11 STABLE-TOMBSTONE PRECONDITION (preserved) ──
    // The ORIGINAL setup sampled the designation 1.5 s after the deal trigger and
    // required "no exact-1 targetSlots". That only held because the RED build shipped
    // NO designation at all: on a build where Bishop's targetSlots is present, the
    // dealer's own batch window is open at exactly that instant, so the PRECONDITION
    // (not the invariant) flips false and S11 goes red for the wrong reason. The setup
    // was pinned to the very defect the gate is meant to outlive.
    //
    // The INVARIANT below is unchanged and is NOT weakened. Only the state we exercise it in
    // moves to a STABLE, same-hand ceremony-complete terminal. We advance ONLY our own batches
    // (never fabricating a press we don't own), driving EVERY owned pickup window to completion,
    // and converge solely on the post-ceremony TOMBSTONE: the pickup collection has cleared
    // (pickup["current"] === null) AND the dealer holds its drawn extra tile / is AwaitingDiscard.
    //
    // We DELIBERATELY do NOT accept a transient `foreignLive` (a bot's fleeting turn) as terminal.
    // Under the manual-deal parking model the cursor rotates back to the viewer's NEXT PickupRound
    // within a poll; the previous fix converged on foreignLive and the downstream `noValidDesignation`
    // re-read then caught the reopened seat-0 window — S11 flaked in BOTH projects on the otherwise
    // green run 31650150219 (converged foreignLive, re-read saw the viewer's window). The tombstone is
    // the only no-designation state that is STABLE for this hand: no further owned window opens after it.
    //
    // Progress-aware + stall-guarded (replaces the fixed 45s window that flaked under CI mobile
    // saturation, run 31594744369). The `observedOwnWindow` milestone still blocks the first-poll
    // VACUOUS convergence (Dietrich's DPR1 probe: the seat-0 window opens ~3.4s post-deal and PARKS
    // until pressed). A GENUINE no-progress park surfaces as the guard's stalled/capped flag with the
    // ceremony NOT complete — the ceremony-complete assertion below then fails with that diagnostic
    // (never hidden). stallMs 45s / capMs 90s bounded under the 120s test timeout (unchanged from the
    // pre-existing gate — the full owned-batch drive to the tombstone measures ~10.5s, well inside it).
    let observedOwnWindow = false;
    let vacuousBlockedPolls = 0;
    let ownBatchesPressed = 0;
    const ownPickupActions: Array<{ targetSlot: string; trustedClicks: number; outcome: Awaited<ReturnType<typeof takePickup>>; frames: TakeFrame[] }> = [];
    let foreignLiveBlockedPolls = 0;
    let d = await readDesignation(page);
    const conv = await pollWithStallGuard(page, async () => {
      const myTurnNow = await readIsMyPickupTurn(page);
      const frontier = await readStackDesignation(page);
      // Observe an OWNED rendered reachable frontier, not merely a turn flag.
      // A helper no-op (including ownership lost before its recheck) is not a human take.
      if (myTurnNow && frontier?.targetLen === 1 && frontier.targetFound && frontier.targetReachable) {
        observedOwnWindow = true;
        const takeMark = takes.length;
        const clicksBefore = await pickupClicks.evaluate((observed) => observed.count);
        const outcome = await takePickup(page);
        const trustedClicks = await pickupClicks.evaluate((observed) => observed.count) - clicksBefore;
        const frames = takes.slice(takeMark);
        expect(outcome.ok, `S11 confirmed pickup: the real owned take must be successful; frontier=${JSON.stringify(frontier)}, outcome=${JSON.stringify(outcome)}`).toBe(true);
        expect(trustedClicks, `S11 confirmed pickup: require exactly one trusted take-button click, not an automatic take after a swallowed click failure; outcome=${JSON.stringify(outcome)}`).toBe(1);
        expect(frames, `S11 confirmed pickup: a successful real take must emit exactly one actor-scoped pickup.take; outcome=${JSON.stringify(outcome)}, frames=${JSON.stringify(frames)}`).toHaveLength(1);
        expect({ ...frames[0], keys: [...frames[0].keys].sort() }, `S11 confirmed pickup: payload must match the observed owner and batch, with no client tile authority; frontier=${JSON.stringify(frontier)}`).toEqual({
          seatIndex: frontier.seatIndex, count: frontier.count, keys: ['count', 'seatIndex'],
        });
        ownPickupActions.push({ targetSlot: frontier.targetName, trustedClicks, outcome, frames });
        ownBatchesPressed++;
      }
      d = await readDesignation(page);
      const myTurn = await readIsMyPickupTurn(page);
      const stillMine = myTurn || !!(d && d.gate && d.gateLen === 1);
      // STABLE same-hand ceremony-complete terminal: pickup tombstoned (null) AND dealer holds its
      // drawn extra tile / AwaitingDiscard. `foreignLive` (a bot's fleeting turn) is recorded for
      // diagnostics but is NOT a terminal (see rationale above).
      const term = await page.evaluate(() => {
        /* eslint-disable @typescript-eslint/no-explicit-any */
        const g = (window as any).game; const w = g?.world; const cli = g?.client;
        const pu = cli?.pickup?.get ? cli.pickup.get('current') : null;
        const seat = cli?.seat ?? null;
        const extra = !!(w?.hasExtraHandTile && w.hasExtraHandTile());
        const tn = cli?.turn?.get ? cli.turn.get('current') : null;
        return { pickupNull: pu === null, extra, awaiting: !!(tn && tn.awaitingDiscard), foreignLive: !!pu && (pu.seatIndex ?? null) !== seat };
        /* eslint-enable @typescript-eslint/no-explicit-any */
      });
      const ceremonyComplete = term.pickupNull && (term.extra || term.awaiting);
      // Non-vacuity proof: OLD bare predicate would converge with no own designation before we ever
      // observed our window (pre-window) — blocked here.
      if (!stillMine && !observedOwnWindow) vacuousBlockedPolls++;
      // Transient-terminal proof: the PRIOR fix accepted foreignLive; count polls where it would have
      // converged on a fleeting bot turn that is NOT ceremony-complete — now blocked.
      if (observedOwnWindow && !stillMine && term.foreignLive && !ceremonyComplete) foreignLiveBlockedPolls++;
      return { done: observedOwnWindow && !stillMine && ceremonyComplete, key: await readCeremonyKey(page) };
    }, { stallMs: 45_000, capMs: 90_000, pollMs: 250 });
    // Record WHICH reachable no-designation state we landed in, so the gate is provably
    // non-vacuous rather than silently degenerating to "nothing was happening".
    const reached = await page.evaluate(() => {
      /* eslint-disable @typescript-eslint/no-explicit-any */
      const g = (window as any).game; const w = g?.world; const cli = g?.client;
      const pu = cli?.pickup?.get ? cli.pickup.get('current') : null;
      const tn = cli?.turn?.get ? cli.turn.get('current') : null;
      return { pickupLive: !!pu, pickupSeat: pu ? (pu.seatIndex ?? null) : null, mySeat: cli?.seat ?? null, phase: pu ? pu.phase : null, hasExtraHandTile: !!(w?.hasExtraHandTile && w.hasExtraHandTile()), awaitingDiscard: !!(tn && tn.awaitingDiscard) };
      /* eslint-enable @typescript-eslint/no-explicit-any */
    });
    // the client-parsed designation: missing/empty/multiple ⇒ NO exact-1 trigger
    // (re-read after the convergence loop above; `d` is declared there)
    d = await readDesignation(page);
    const noValidDesignation = !d || !d.gate || d.gateLen !== 1;
    // press an ARBITRARY wall tile (real pointer) and assert nothing moves/takes.
    // ACTOR-SCOPED: the shared-wall term `after.wall < before.wall` was a FALSE
    // POSITIVE generator — any of the three bots taking its own batch inside this
    // window flipped it true. Fail-closed is now proven by MY hand (`hand.*@0`)
    // plus MY outbound pickup.take frames, and the local drag/hold state.
    const handBefore = await myHandCount(page);
    const mark = takes.length;
    const drag = await realDragWallTile(page);
    await page.waitForTimeout(600);
    const handAfter = await myHandCount(page);
    const emitted = takes.slice(mark);
    const anyWallActed = (handAfter > handBefore) || emitted.length > 0 || !!(drag.held && drag.held.isHolding) || ((drag.held?.dragOffsetWorld ?? 0) > 5);
    recordEvidence('g17-fail-closed.json', { noValidDesignation, designationLen: d ? d.gateLen : null, reached, handBefore, handAfter, emittedTakes: emitted, pick: drag.pick, held: drag.held, anyWallActed,
      convGuard: { done: conv.done, stalled: conv.stalled, capped: conv.capped, elapsedMs: conv.elapsedMs, keyChanges: conv.keyChanges, maxIdleMs: conv.maxIdleMs },
      milestone: { observedOwnWindow, vacuousBlockedPolls, ownBatchesPressed, ownPickupActions, foreignLiveBlockedPolls },
      note: 'Fail-closed (actor-scoped, Hudson): inert ⟺ zero outbound pickup.take AND unchanged `hand.*@0` AND no local hold/drag. Shared-wall deltas are NOT used — bots take concurrently. S11 converges ONLY on the STABLE same-hand ceremony-complete TOMBSTONE (reached.pickupLive=false + hasExtraHandTile/awaitingDiscard); the transient foreignLive (a bot turn) is NOT accepted — `foreignLiveBlockedPolls`>0 counts polls where the prior fix would have converged on a fleeting bot turn (now blocked, killing the gate-4 re-read race). `observedOwnWindow` requires a rendered reachable owned single trigger; `ownBatchesPressed`>0 counts only successful takes with one trusted button click and exactly one matching {seatIndex,count} frame. Non-null `pick` and `held` prove the wall helper exercised its real mouse-down/drag/up branch. `vacuousBlockedPolls`>0 means the old bare predicate would have converged vacuously pre-window.' });
    // PRECONDITION at the STABLE tombstone: the ceremony has completed this hand, so the viewer
    // holds no single-trigger designation and no owned window will reopen. The convergence is
    // NON-VACUOUS (we observed and drove our own windows) and STABLE (no transient foreignLive).
    expect(observedOwnWindow, `S11 non-vacuity: must have HELD this viewer's own single-trigger pickup window before converging (guards the DPR1 first-poll vacuous case); observedOwnWindow=${observedOwnWindow}, reached=${JSON.stringify(reached)}, drive done=${conv.done} stalled=${conv.stalled} capped=${conv.capped} elapsedMs=${conv.elapsedMs}`).toBe(true);
    expect(ownBatchesPressed, `S11 non-vacuity: must confirm at least one successful real owned pickup with matching outbound take evidence; actions=${JSON.stringify(ownPickupActions)}`).toBeGreaterThan(0);
    expect(reached.pickupLive === false && (reached.hasExtraHandTile || reached.awaitingDiscard), `S11 stable terminal: must converge on the same-hand ceremony-complete TOMBSTONE (pickup["current"]==null AND dealer hasExtraHandTile/AwaitingDiscard), never a transient live pickup; reached=${JSON.stringify(reached)}, drive done=${conv.done} stalled=${conv.stalled} capped=${conv.capped} elapsedMs=${conv.elapsedMs} ownBatchesPressed=${ownBatchesPressed} foreignLiveBlockedPolls=${foreignLiveBlockedPolls}`).toBe(true);
    expect(noValidDesignation, `precondition: this viewer holds no exact-1 targetSlots designation (converged state: ${JSON.stringify(reached)}; drive: done=${conv.done} stalled=${conv.stalled} capped=${conv.capped} elapsedMs=${conv.elapsedMs} maxIdleMs=${conv.maxIdleMs}; vacuousBlockedPolls=${vacuousBlockedPolls})`).toBe(true);
    expect(drag.pick, 'S11 wall probe: must find a rendered reachable wall candidate, not pass without a pointer target').not.toBeNull();
    expect(drag.held, 'S11 wall probe: must sample hold/drag state after the real pointer press').not.toBeNull();
    expect(anyWallActed, `FAIL-CLOSED: with no valid targetSlots, pressing ANY wall tile must be inert — zero pickup.take (emitted=${emitted.length}), unchanged hand (${handBefore}→${handAfter}), no hold/drag`).toBe(false);
  });

  test('S12 (F2 reachability): reachable TOP frontier tile actionable; OCCLUDED bottom inert (canSelect blocks)', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const sent = installTakeRecorder(page);
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-f2-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    let d: any = null; const t0 = Date.now();
    while (Date.now() - t0 < 22000) {
      d = await readDesignation(page);
      if (d && d.gate && d.gateLen) break;
      if (await page.evaluate(() => { const t = (window as any).game?.client?.turn; return !!(t && t.awaitingDiscard); })) break;
      await page.waitForTimeout(250);
    }
    // classify the frontier stack: the designated TOP tile (targetSlots[0]) + its
    // same-footprint OCCLUDED sibling; canSelect (esbuild keeps method names) with an
    // occlusion fallback. Ripley F2: top=reachable (layer 1) selectable; bottom inert.
    const react = d && d.gate && d.gateLen ? await page.evaluate((topSlot) => {
      /* eslint-disable @typescript-eslint/no-explicit-any */
      const g = (window as any).game; const w = g?.world;
      const pos = (t: any) => { let p = { x: 0, y: 0, z: 0 }; try { const pl = t.place(); p = { x: pl.position.x, y: pl.position.y, z: pl.position.z }; } catch { /* */ } return p; };
      const cs = (t: any) => { try { return typeof w.canSelect === 'function' ? !!w.canSelect(t, []) : null; } catch { return null; } };
      let top: any = null;
      if (w?.things) for (const t of w.things.values()) if (t?.slot?.group === 'wall' && String(t.slot?.name) === topSlot) top = t;
      let bottom: any = null;
      if (top) { const tp = pos(top); if (w?.things) for (const t of w.things.values()) if (t?.slot?.group === 'wall' && String(t.slot?.name) !== topSlot) { const p = pos(t); if (Math.abs(p.x - tp.x) < 3 && Math.abs(p.y - tp.y) < 3) bottom = t; } }
      const upOcc = (t: any) => { const up = t?.slot?.links?.up; return !!(up && up.thing); };
      return {
        topName: top ? String(top.slot?.name) : null, topReachable: top ? !upOcc(top) : null, topCanSelect: top ? cs(top) : null,
        hasBottom: !!bottom, bottomName: bottom ? String(bottom.slot?.name) : null, bottomOccluded: bottom ? upOcc(bottom) : null, bottomCanSelect: bottom ? cs(bottom) : null,
        canSelectAvailable: typeof w?.canSelect === 'function',
      };
      /* eslint-enable @typescript-eslint/no-explicit-any */
    }, d.gate[0]) : null;

    let topPress: TargetPressOutcome | null = null; let bottomHover: { ok: boolean; hovered: string | null } | null = null; let payloadKeys: string[] | null = null;
    if (d && d.gate && d.gateLen) {
      // (a) real press on the designated reachable TOP → one command + my batch.
      // F2(b) OCCLUSION — asserted NON-PHYSICALLY (Hudson final adjudication). A click at
      // the occluded bottom's projected coords is an INVALID probe: the raycast correctly
      // resolves to the reachable TOP that occludes it, so the resulting take is right
      // behaviour, not a fail-closed breach. Occlusion is instead proven by the renderer's
      // own pick: canSelect(bottom) === false AND a real hover at those coords resolves to
      // something other than the bottom tile.
      if (react && react.bottomName) bottomHover = await hoverAndReadPick(page, String(react.bottomName));
      // (a) grid-scan to the designated reachable TOP's exact face → one command + my batch.
      topPress = await pressTargetByHover(page, String(d.gate[0]), sent);
      await page.waitForTimeout(400);
      const last = topPress && topPress.frames.length ? topPress.frames[topPress.frames.length - 1] : null;
      payloadKeys = last ? last.keys : null;
    }
    recordEvidence('g17-f2-reachability.json', { designation: d ? { gateLen: d.gateLen, count: d.count, top: d.gate?.[0] } : null, react, topPress, bottomHover, payloadKeys,
      note: 'F2 (actor-scoped, Hudson): take ⟺ exactly one outbound pickup.take AND `hand.*@0` +count; inert ⟺ zero command AND unchanged hand. Shared-wall deltas are NOT used.' });

    expect(d && d.exactlyOneFront, `F2: requires a single-trigger pickup.targetSlots designation; got ${JSON.stringify(d && { gateLen: d.gateLen })}`).toBe(true);
    // (top) reachable + selectable
    expect(react && react.topReachable, `F2(a): targetSlots[0]=${react?.topName} must be the REACHABLE top of the frontier stack`).toBe(true);
    if (react && react.canSelectAvailable) expect(react.topCanSelect, 'F2(a): the reachable top tile must be selectable (canSelect true)').toBe(true);
    // (b) occluded bottom inert
    if (react && react.hasBottom) {
      expect(react.bottomOccluded, `F2(b): the same-stack bottom tile ${react.bottomName} must be OCCLUDED (a tile on top)`).toBe(true);
      const bottomInert = react.bottomCanSelect === false || (react.bottomCanSelect === null && react.bottomOccluded === true);
      expect(bottomInert, `F2(b): the OCCLUDED bottom tile must be INERT (canSelect blocks it); bottomCanSelect=${react.bottomCanSelect}`).toBe(true);
      // (b) the renderer's own pick must never resolve to the occluded bottom: a real
      // hover at its projected coords lands on the tile above it (or nothing).
      if (bottomHover && bottomHover.ok) {
        expect(bottomHover.hovered, `F2(b): a real hover at the OCCLUDED bottom ${react.bottomName} must NOT pick that tile (raycast resolves to the reachable top); hovered=${bottomHover.hovered}`).not.toBe(String(react.bottomName));
      }
    }
    // (a) real click on the top → take; (d) payload seat+count only
    expect(topPress, 'F2(a): the designated TOP tile must be pressable').not.toBeNull();
    expect(topPress!.matched, `F2(a): the real pointer must resolve world.hovered to the EXACT reachable TOP ${d.gate?.[0]} before pressing; hovered=${topPress!.hovered}`).toBe(true);
    expect(topPress!.frames.length, `F2(a): a real-pointer click on the reachable TOP tile must emit EXACTLY ONE pickup.take; emitted=${topPress!.frames.length}`).toBe(1);
    expect(topPress!.handDelta, `F2(a): the TOP-tile press must move the whole batch of ${d.count} into MY hand; handDelta=${topPress!.handDelta}`).toBe(d.count);
    expect(payloadKeys, 'F2(d): an outbound pickup.take frame must be observed').not.toBeNull();
    if (payloadKeys) expect(payloadKeys.filter((k) => k !== 'seatIndex' && k !== 'count'), `F2(d): take payload = {seatIndex,count} only; extra=${JSON.stringify(payloadKeys)}`).toEqual([]);
  });

  test('S6: targetSlots[0] is the TOP/reachable frontier tile — never bottom/occluded', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-top-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    const seen: any[] = []; const t0 = Date.now();
    while (Date.now() - t0 < 22000) {
      const d = await readStackDesignation(page);
      if (d && d.targetName) { const key = d.phase + ':' + d.targetName; if (!seen.some((s) => s.key === key)) seen.push({ key, phase: d.phase, count: d.count, targetName: d.targetName, targetFound: d.targetFound, targetReachable: d.targetReachable, targetIsTopOfStack: d.targetIsTopOfStack, hasOccludedSibling: d.hasOccludedSibling, siblingReachable: d.siblingReachable }); }
      if (await page.evaluate(() => { const t = (window as any).game?.client?.turn; return !!(t && t.awaitingDiscard); })) break;
      await page.waitForTimeout(250);
    }
    recordEvidence('g17-top-reachable.json', { seen: seen.length, samples: seen.slice(0, 8),
      note: 'RED@200cad4: pickup ships no targetSlots ⇒ no named front trigger to classify top/reachable.' });
    expect(seen.length, 'S6 requires an observed pickup with a named targetSlots[0]').toBeGreaterThan(0);
    for (const s of seen) {
      expect(s.targetFound, `S6: ${s.phase} targetSlots[0]=${s.targetName} must name a rendered wall tile`).toBe(true);
      expect(s.targetReachable, `S6: ${s.phase} targetSlots[0]=${s.targetName} must be REACHABLE (no tile on top) — the exposed front`).toBe(true);
      expect(s.targetIsTopOfStack, `S6: ${s.phase} targetSlots[0]=${s.targetName} must be the TOP of its stack, not the bottom/occluded layer`).toBe(true);
      if (s.hasOccludedSibling) expect(s.siblingReachable, `S6: ${s.phase} the lower layer under the trigger must be OCCLUDED (rejected as a target)`).toBe(false);
    }
  });

  test('S7: after a one-tile pickup the LOWER layer of the same stack becomes the next trigger', async ({ page }, testInfo) => {
    testInfo.setTimeout(150_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-lower-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    const takes = installTakeRecorder(page);
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    // Timeline of the GLOBAL pickup trigger across the whole ceremony (any seat).
    // ── OBSERVATION ROBUSTNESS (Hicks 2026-08-11) ────────────────────────────────
    // The former Playwright-side 180 ms sample MISSED same-stack top→lower
    // transitions: each bot single-tile pickup is held only ~400 ms and, worse, no
    // sampling happens at all while THIS test is blocked inside a real wall press —
    // so on the slower mobile-chrome the count=1 lower-layer trigger appeared and was
    // consumed inside a gap and the transition was never recorded (RED there only).
    // Replace the lossy external sample with an IN-PAGE recorder hooked to the client's
    // own `pickup` 'update' event (the same event game-ui.onPickupUpdate renders from):
    // it captures EVERY distinct designation the client sees — including those that
    // fire during a press's waits — with the tile's live footprint. Observation only,
    // no emit/mutation; the ceremony is still driven by REAL wall presses below.
    await page.evaluate(() => {
      /* eslint-disable @typescript-eslint/no-explicit-any */
      const g = (window as any).game; const cli = g?.client; const w = g?.world;
      if (!cli?.pickup || (window as any).__pickupTimeline) return;
      const tl: any[] = []; (window as any).__pickupTimeline = tl;
      const record = () => {
        const pu = cli.pickup.get ? cli.pickup.get('current') : null;
        if (!pu) return;
        const gate = Array.isArray(pu.targetSlots) ? pu.targetSlots.map(String) : null;
        if (!gate || !gate.length) return;
        const name = String(gate[0]);
        const last = tl[tl.length - 1];
        if (last && last.targetName === name) return;   // dedupe consecutive
        let fx: number | null = null, fy: number | null = null, reachable: boolean | null = null;
        if (w?.things) for (const t of w.things.values()) {
          if (t?.slot?.group !== 'wall' || String(t.slot?.name) !== name) continue;
          try { const p = t.place().position; fx = Math.round(p.x); fy = Math.round(p.y); } catch { /* */ }
          const up = t.slot?.links?.up; reachable = !(up && up.thing);
        }
        tl.push({ phase: pu.phase, count: pu.count, seatIndex: pu.seatIndex, targetName: name, reachable, fx, fy });
      };
      cli.pickup.on('update', record);
      record();   // seed with whatever designation is already live
      /* eslint-enable @typescript-eslint/no-explicit-any */
    });
    // Drive the ceremony with REAL wall presses so it PROGRESSES to the count=1
    // SingleTilePickup phase: the human owns its own batches, so press ONLY when it is
    // genuinely our turn (readIsMyPickupTurn) — bots drive their own. The in-page
    // recorder above captures every transition, including those that land while a press
    // is in flight, so no explicit per-iteration sample is needed.
    //
    // NOTE (Hudson final): a count=4 batch consumes two FULL stacks (top+bottom together),
    // so no lower layer is exposed after it — the top→same-stack-lower advance is a
    // property of the count=1 SingleTilePickup phase only, and is asserted there.
    const t0 = Date.now();
    while (Date.now() - t0 < 60000) {
      if (await readIsMyPickupTurn(page)) {
        const mine = await readStackDesignation(page, true);
        if (mine && mine.targetName) await pressTargetByHover(page, String(mine.targetName), takes, 300);
      } else {
        await page.waitForTimeout(120);
      }
      if (await hasExtraHandTile(page)) break;
      if (await page.evaluate(() => { const t = (window as any).game?.client?.turn; return !!(t && t.awaitingDiscard); })) break;
    }
    // Read the in-page timeline (every distinct global designation the client saw).
    const timeline: any[] = await page.evaluate(() => (window as any).__pickupTimeline ?? []);
    // find a count=1 → same-footprint, different-slot, reachable transition
    let lowerLayerNext = false; let evidencePair: any = null;
    for (let i = 0; i + 1 < timeline.length; i++) {
      const a = timeline[i], b = timeline[i + 1];
      if (a.count === 1 && a.fx !== null && b.fx !== null && Math.abs(a.fx - b.fx) < 3 && Math.abs(a.fy - b.fy) < 3 && a.targetName !== b.targetName && a.reachable === true && b.reachable === true) { lowerLayerNext = true; evidencePair = { a, b }; break; }
    }
    const singleTileSeen = timeline.filter((t) => t.count === 1).length;
    recordEvidence('g17-lower-layer-next.json', { timelineLen: timeline.length, singleTileSeen, timeline: timeline.slice(0, 24), lowerLayerNext, evidencePair,
      note: 'S7 is a SingleTilePickup property, observed via an in-page pickup.on(update) recorder (no sampling gaps): a count=1 trigger must be followed by the SAME-footprint lower layer as the next reachable trigger (across seat transitions). count=4 batches consume both layers of two stacks, so they expose no lower layer.' });
    expect(singleTileSeen, `S7 precondition: the ceremony must reach the count=1 SingleTilePickup phase; timelineLen=${timeline.length}, counts=${JSON.stringify(timeline.map((t) => t.count))}`).toBeGreaterThan(0);
    expect(lowerLayerNext, 'S7: a count=1 pickup must expose the SAME stack\u2019s lower layer as the next reachable trigger').toBe(true);
  });

  test('S8: a count=4 batch press on the ONE front trigger consumes EXACTLY two adjacent front stacks', async ({ page }, testInfo) => {
    testInfo.setTimeout(150_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-2stacks-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    const takes = installTakeRecorder(page);
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    let d: any = null; const t0 = Date.now();
    while (Date.now() - t0 < 22000) {
      d = await readStackDesignation(page);
      if (d && d.count === 4 && d.targetLen === 1 && d.targetFound) break;
      if (await page.evaluate(() => { const t = (window as any).game?.client?.turn; return !!(t && t.awaitingDiscard); })) break;
      await page.waitForTimeout(220);
    }
    expect(d && d.count === 4 && d.targetLen === 1 && d.targetFound,
      `S8 needs a count=4 pickup with a single found front trigger; got ${JSON.stringify(d && { phase: d.phase, count: d.count, targetLen: d.targetLen, targetFound: d.targetFound })}`).toBe(true);
    // ACTOR-SCOPED (Hudson): the causal truth is MY hand delta + exactly one
    // {seatIndex:0,count:4} frame. The stack FOOTPRINT is still checked (that is the
    // physical two-adjacent-stacks claim S8 exists for), but it is measured against MY
    // batchPreviewSlots footprint rather than a time-boxed shared-wall diff. An early
    // (220 ms) diff was still contaminated on the slower mobile-chrome — it read
    // `fullStacksRemoved=4` (my 2 + another seat's 2) — so the diff is now INTERSECTED
    // with the footprint the server designated for MY batch, which no bot can enter.
    const before = await wallStacks(page);
    const handBefore = await myHandCount(page);
    // Footprints (world x,y) of the slots the server designated as MY batch preview.
    const mineFootprint: Array<{ x: number; y: number }> = await page.evaluate(() => {
      /* eslint-disable @typescript-eslint/no-explicit-any */
      const g = (window as any).game; const w = g?.world;
      const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
      const names = new Set<string>();
      if (pu && pu.seatIndex === g?.client?.seat) {
        for (const s of (pu.batchPreviewSlots ?? [])) names.add(String(s));
        for (const s of (pu.targetSlots ?? [])) names.add(String(s));
      }
      const out: Array<{ x: number; y: number }> = [];
      if (w?.things) for (const t of w.things.values()) {
        if (t?.slot?.group !== 'wall' || !names.has(String(t.slot?.name))) continue;
        try { const p = t.place().position; out.push({ x: p.x, y: p.y }); } catch { /* */ }
      }
      return out;
      /* eslint-enable @typescript-eslint/no-explicit-any */
    });
    // EARLY settle (220 ms) so the footprint below is sampled close to the press; the
    // hand delta is bot-immune so it is polled separately. Grid-scan to the exact
    // designated face first so the count-4 press reliably actuates post-shift.
    const press = await pressTargetByHover(page, d.targetName, takes, 220);
    const afterEarly = await wallStacks(page);
    let handAfter = await myHandCount(page);
    const hDeadline = Date.now() + 4000;
    while (handAfter - handBefore < 4 && Date.now() < hDeadline) { await page.waitForTimeout(100); handAfter = await myHandCount(page); }
    const emitted = press ? press.frames : [];
    // stacks fully removed = footprints present-before, absent-after …
    const removedRaw = before.filter((s) => !afterEarly.some((q) => Math.abs(q.x - s.x) < 3 && Math.abs(q.y - s.y) < 3));
    // … INTERSECTED with MY designated batch footprint, so a concurrent bot pickup
    // elsewhere on the wall can never be counted against this gate.
    const removed = mineFootprint.length
      ? removedRaw.filter((s) => mineFootprint.some((m) => Math.abs(m.x - s.x) < 3 && Math.abs(m.y - s.y) < 3))
      : removedRaw;
    const adjacent = removed.length === 2 && Math.hypot(removed[0].x - removed[1].x, removed[0].y - removed[1].y) < 20;
    recordEvidence('g17-two-stacks.json', { count: d.count, handBefore, handAfter, handDelta: handAfter - handBefore, emittedTakes: emitted, mineFootprintLen: mineFootprint.length, removedRawLen: removedRaw.length, fullStacksRemoved: removed.length, removed, adjacent,
      note: 'Actor-scoped: hand delta + exactly one {seatIndex,count} frame are the causal gates; the footprint is sampled early (pre-bot-pickup) and is the physical two-adjacent-stacks claim only.' });
    // (1) exactly one outbound command, carrying exactly {seatIndex:0,count:4}
    expect(press.matched, `S8: the real pointer must resolve world.hovered to the EXACT designated trigger ${d.targetName} before pressing; hovered=${press.hovered}`).toBe(true);
    expect(emitted.length, `S8: one designated press must emit EXACTLY ONE pickup.take; emitted=${emitted.length}`).toBe(1);
    expect(emitted[0].seatIndex, `S8: the take must be scoped to MY seat 0; got ${emitted[0]?.seatIndex}`).toBe(0);
    expect(emitted[0].count, `S8: the take must carry count=4; got ${emitted[0]?.count}`).toBe(4);
    // (2) my hand grew by exactly the batch — bot-immune
    expect(handAfter - handBefore, `S8: a count=4 batch must move exactly 4 tiles into MY hand; handDelta=${handAfter - handBefore}`).toBe(4);
    // (3) the physical footprint: exactly two adjacent full stacks
    expect(removed.length, `S8: those 4 tiles must be EXACTLY two full stacks (both layers each); fullStacksRemoved=${removed.length}`).toBe(2);
    expect(adjacent, 'S8: the two consumed stacks must be adjacent at the front (one-pitch apart)').toBe(true);
  });

  test('S13 (D5 auto-drive removal): manual deal must NOT auto-take the human pickups without a real press', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-noauto-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    // WITHOUT pressing any wall tile: the human's pickups must NOT self-advance.
    // (§B one-interaction-per-batch + the single-trigger lock; auto-drive removed.)
    await page.waitForTimeout(7000);
    const obs = await page.evaluate(() => {
      /* eslint-disable @typescript-eslint/no-explicit-any */
      const g = (window as any).game; const w = g?.world;
      let hand = 0; if (w?.things) for (const t of w.things.values()) if (/^hand\.\d+@0$/.test(String(t?.slot?.name ?? ''))) hand++;
      const t = g?.client?.turn; const awaitingDiscard = !!(t && t.awaitingDiscard);
      return { handNoPress: hand, awaitingDiscard };
      /* eslint-enable @typescript-eslint/no-explicit-any */
    });
    recordEvidence('g17-no-auto-take.json', { obs,
      note: 'D5: WITHOUT a human press the manual pickup must NOT advance. RED@200cad4: world.ts driveManualDealChain auto-rolls + auto-takes the local seat ⇒ hand self-fills without any press.' });
    // D5: no client-side auto-take of the human pickups — hand stays empty until a
    // real endpoint press. RED@200cad4 (auto-drive fills it).
    expect(obs.handNoPress, `D5: without any human wall press the hand must NOT auto-advance (auto-drive removed); got ${obs.handNoPress}`).toBe(0);
  });

  test('pickup tombstone GREEN LOCK (Vasquez D1): pickup["current"]==null + isMyPickupTurn()==false post-ceremony', async ({ page }, testInfo) => {
    // BOTH projects. Hudson's Pixel 5 probe proved `#pickup-take-btn` is
    // rendered, hit-testable and 5/5 pressable under touch emulation, and the
    // full chromium-vs-mobile-chrome matrix (Ripley, 2026-08-11, 30/30 cases,
    // 0 skipped) came back BIT-IDENTICAL on both projects. There is no
    // mobile-specific defect here — a chromium-only skip would have masked a
    // real product defect as a "mobile viewport" issue.
    testInfo.setTimeout(150_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g17-tomb-${Date.now()}`, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page);
    // Drive every real ceremony batch through the visible #pickup-take-btn
    // (shared takePickup) — the table no longer reaches AwaitingDiscard on
    // its own, so without this the pickup cursor is still parked on the
    // dealer's own (unpressed) first batch and this GREEN LOCK would be
    // observing mid-ceremony state, not the post-ceremony tombstone it names.
    const drive = await driveHumanPickupsUntilPlayable(page);
    await waitForPlayableHand(page, 60_000).catch(() => {});
    // Bounded, progress-aware wait for the POST-DEAL TOMBSTONE (pickup["current"] → null on
    // the full-snapshot map.clear() path this D1 GREEN LOCK is about) — replaces a fixed 2s
    // settle so we observe the real post-ceremony state, not a mid-clear race, under CI
    // saturation. The drive above reaches ceremony completion (hasExtraHandTile); the pickup
    // clear is a SEPARATE later snapshot, so it needs its own authoritative gate. A product
    // that genuinely never tombstones (the D1 defect) fails the assertions below (rawPickup
    // non-null) with this telemetry — never hidden, no sleep-to-pass.
    const tomb = await pollWithStallGuard(page, async () => {
      const cleared = await page.evaluate(() => {
        /* eslint-disable @typescript-eslint/no-explicit-any */
        const g = (window as any).game;
        const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
        return pu === null;
        /* eslint-enable @typescript-eslint/no-explicit-any */
      });
      return { done: cleared, key: await readCeremonyKey(page) };
    }, { stallMs: 30_000, capMs: 45_000, pollMs: 300 });
    const post = await page.evaluate(() => {
      /* eslint-disable @typescript-eslint/no-explicit-any */
      const g = (window as any).game; const w = g?.world;
      const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
      const takeBtn = Array.from(document.querySelectorAll('button')).some((b) => /take\s*\d|pick your tiles/i.test((b.textContent || '')) && (b as HTMLElement).offsetParent !== null);
      const rollVisible = (() => { const e = document.getElementById('roll-dice'); return !!e && e.offsetParent !== null; })();
      return { rawPickup: pu ? { phase: pu.phase, count: pu.count } : null, designationLen: Array.isArray((pu as any)?.targetSlots) ? (pu as any).targetSlots.length : 0, isMyPickupTurn: !!(w?.isMyPickupTurn && w.isMyPickupTurn()), takeBtnVisible: takeBtn, rollVisible };
      /* eslint-enable @typescript-eslint/no-explicit-any */
    });
    await shot(page, 'g17-pickup-tombstone.png');
    recordEvidence('g17-pickup-tombstone.json', { post, stallGuard: { done: drive.done, stalled: drive.stalled, capped: drive.capped, elapsedMs: drive.elapsedMs, keyChanges: drive.keyChanges, maxIdleMs: drive.maxIdleMs }, tombGuard: { done: tomb.done, stalled: tomb.stalled, capped: tomb.capped, elapsedMs: tomb.elapsedMs, keyChanges: tomb.keyChanges, maxIdleMs: tomb.maxIdleMs }, note: 'Vasquez D1 CONCEDED: full-snapshot map.clear() wipes pickup["current"] when the post-deal snapshot omits pickup ⇒ pickup null / isMyPickupTurn false is a GREEN LOCK (defensive). HUD button state is a recorded observation, not gated (D1 rules pickup-clear GREEN, not the HUD).' });
    // GREEN LOCK (Vasquez D1) — pickup state clears on the full-update path; assert
    // the defensive must-preserve, NOT a RED. (The takeBtn HUD is recorded only.)
    expect(post.rawPickup, `D1: pickup["current"] must be null post-ceremony; got ${JSON.stringify(post.rawPickup)} (drive: done=${drive.done} stalled=${drive.stalled} capped=${drive.capped}; tombstone wait: done=${tomb.done} stalled=${tomb.stalled} capped=${tomb.capped} elapsedMs=${tomb.elapsedMs} maxIdleMs=${tomb.maxIdleMs})`).toBeNull();
    expect(post.designationLen, 'D1: targetSlots designation must be empty post-ceremony').toBe(0);
    expect(post.isMyPickupTurn, 'D1: isMyPickupTurn() must be false post-ceremony').toBe(false);
  });
});

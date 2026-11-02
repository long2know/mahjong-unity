// NEW-2 (parent anti-happy-path, formalized by Ripley 2026-08-07). The manual
// pickup ceremony must be HUMAN-DRIVEN: the viewer must real-press targetSlots[0]
// to take EACH batch, and the ceremony must make NO progress absent a press. The
// SC-4 single-trigger interaction is currently DEAD CODE — world.ts
// driveManualDealChain auto-rolls + auto-takes, so the hand self-fills to 14 with
// ZERO human presses (proven live: S13 in changsha-manual-pickup-endpoint-only).
//
// This is the anti-happy-path partner of D2/NEW-1 (changsha-manual-deal-
// orchestration): NEW-1 proves a NON-DEALER human is not stalled by an unscheduled
// bot-dealer roll; NEW-2 proves the pickup INTERACTION itself is real (a human
// press moves each batch and nothing moves without one). We isolate NEW-2 at the
// DEALER seat (seat 0) ON PURPOSE: @200cad4 a non-dealer stalls in RollingDice
// (that is NEW-1's defect) and never reaches a pickup window, which would MASK the
// auto-drive. Seat 0's ceremony opens, so the auto-drive (or, post-fix, the human
// press) is directly observable. Dealer completes to 14 tiles.
//
// Acceptance (GREEN after Bishop ships pickup.targetSlots len-1 + Hicks/world.ts
// removes the client auto-drive):
//   (1) window opens with a single-trigger designation; the hand does NOT climb on
//       its own (no auto-advance) — RED@200cad4: hand auto-fills to 14 unpressed.
//   (2) each real press on targetSlots[0] takes EXACTLY that batch's `count` and
//       then STOPS — no auto-advance to the next batch until the next press.
//   (3) the dealer reaches 14 THROUGH discrete human presses (>=1) — RED@200cad4:
//       0 presses are possible (no targetSlots to click); the 14 is auto-driven,
//       so we assert completion-via-presses, never a bare hand>=13 (which the
//       auto-drive would falsely satisfy).
//
// Discipline: genuine rendered controls only — real-pointer clicks projected onto
// the reachable top wall tile (up-link empty). No emitDiscard, no synthetic
// pickup.take, no collection injection, no direct roll API (roll is a real control
// click if rendered; @200cad4 the auto-drive rolls, so it is best-effort).

import { test, expect, type Page } from '@playwright/test';
import { buildGameUrl, makeConfig, dismissLobbyAndTour, ensureConnected, takeSeatByClick, clickDeal, hasExtraHandTile, readIsMyPickupTurn, takePickup, installWallTakeRecorder, pressWallTargetByHover, type WallTakeFrame } from './_playability';
import { pollWithStallGuard, readCeremonyKey } from './_ceremony-progress';
import { recordEvidence, shot } from './_uat_red';

// Reads the viewer's current single-trigger pickup designation (SC-4 v4:
// pickup.targetSlots — a public exposed-front slot NAME, length EXACTLY 1). Returns
// null when there is no designation targeted at the local seat.
async function readDesignation(page: Page): Promise<{ phase: string; count: number; gate: string[]; gateLen: number } | null> {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game;
    const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
    if (!pu || pu.seatIndex !== g?.client?.seat) return null;
    const gate: string[] | null = Array.isArray((pu as any).targetSlots) ? (pu as any).targetSlots.map(String) : null;
    if (!gate || gate.length === 0) return null;
    return { phase: pu.phase, count: pu.count, gate, gateLen: gate.length };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

// Own-hand tile count (hand.*@0) — the local seat is 0 in these runs.
async function handCount(page: Page): Promise<number> {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world; let hand = 0;
    if (w?.things) for (const t of w.things.values()) if (/^hand\.\d+@0$/.test(String(t?.slot?.name ?? ''))) hand++;
    return hand;
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

// Sample the hand count over `ms` WITHOUT any input; returns the net climb.
async function handClimbOver(page: Page, ms: number): Promise<{ start: number; end: number; climb: number }> {
  const start = await handCount(page);
  await page.waitForTimeout(ms);
  const end = await handCount(page);
  return { start, end, climb: end - start };
}

// Real-pointer press on the REACHABLE top wall tile named gate[0] (up-link empty
// ⇒ not occluded). Uses the shared grid-scan primitive (pressWallTargetByHover),
// which moves a genuine pointer across the tile footprint until the renderer's own
// raycast reports world.hovered === gate[0] — defeating the intermittent ~16 px
// angled-face projection offset — then issues a real mouse down/up. Returns the
// hard-recorded hover match, the outbound pickup.take frames this press emitted,
// and the bot-immune own-hand delta.
async function pressDesignationTop(page: Page, gate: string[], takes: WallTakeFrame[]): Promise<{
  clicked: boolean; matched: boolean; hovered: string | null; handBefore: number; handAfter: number; frames: WallTakeFrame[];
}> {
  const r = await pressWallTargetByHover(page, gate[0], takes);
  return { clicked: r.found, matched: r.matched, hovered: r.hovered, handBefore: r.handBefore, handAfter: r.handAfter, frames: r.frames };
}

// Best-effort real-pointer roll for the dealer, if a roll control is rendered.
// @200cad4 the client auto-drive rolls, so this is not required for the RED; it
// exists so the same test drives cleanly once the auto-drive is removed (GREEN).
async function tryRealRoll(page: Page): Promise<void> {
  const roll = page.locator('#roll-dice, button:has-text("Roll")').first();
  if (await roll.isVisible().catch(() => false)) {
    await roll.click({ timeout: 2000 }).catch(() => {});
  }
}

test.describe('NEW-2 human-driven pickup — real press per batch; no progress absent a press', () => {
  test('dealer completes the manual ceremony ONLY through discrete real presses on targetSlots[0]; the hand never auto-advances', async ({ page }, testInfo) => {
    testInfo.setTimeout(150_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `new2-humandrive-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    // OUTBOUND wire capture (observation only — no injection/emit). Every batch
    // press must emit EXACTLY ONE {seatIndex:0,count} pickup.take frame; 0 emit fails.
    const takes = installWallTakeRecorder(page);
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    await tryRealRoll(page);

    // PHASE 1 — no auto-advance: the window opens but the hand must NOT climb on its
    // own. @200cad4 the client auto-drive self-fills the hand (S13 observed 14),
    // so this climb is > 0 (RED). GREEN: climb === 0 (a designation is present and
    // waits for the human).
    const preDesignation = await readDesignation(page);
    const noPress = await handClimbOver(page, 6000);

    // PHASE 2 — per-batch human drive: the dealer owns EXACTLY 5 batches
    // (PickupRound1..3 = 4 each, SingleTilePickup = 1, DealerExtra = 1 ⇒ 14), interleaved
    // with the three bots' own windows. Press ONLY when it is genuinely our turn
    // (readIsMyPickupTurn) — the previous fixed 8-iteration loop pressed blind during bot
    // windows, which produced 7 no-op presses and a false `allBatchesExact=false`.
    let presses = 0;
    let allBatchesExact = true;             // every OWNED press moved exactly `count`
    let allBatchesExactHover = true;        // world.hovered === targetSlots[0] before every press
    let allBatchesOneEmit = true;           // exactly one outbound {seatIndex,count} pickup.take per press
    let allBatchesMySeat = true;            // every emitted take was scoped to seat 0
    let anyBetweenBatchAutoAdvance = false; // hand moved during a bot window w/o input
    let sawAnyDesignation = !!preDesignation;
    const batches: Array<{ phase: string; count: number; delta: number; emit: number; seatIndex: number | null; matched: boolean; hovered: string | null; betweenClimb: number }> = [];
    // Progress-aware, stall-guarded per-batch drive (replaces the fixed 90s window that
    // flaked under CI saturation, run 31576171218): keep driving while the authoritative
    // ceremony fingerprint advances (turn/pickup cursor/our hand — server-pushed). Press
    // ONLY when it is genuinely our turn; the anti-auto-advance hand-hold samples
    // (handClimbOver) and every per-batch hard signal are preserved verbatim. A GENUINE
    // stall (no designation ever / RollingDice park ⇒ no progress) is NOT hidden — it falls
    // through to the killer asserts below (sawAnyDesignation / presses===5 / handFinal===14
    // fail with the RED@200cad4 diagnostic). stallMs 45s ≫ the worst measured inter-progress
    // gap under saturation; capMs 120s < the 150s test timeout minus setup. pollMs 0: each
    // tick already blocks on a real hand-hold sample / press, which is the cadence.
    const guard = await pollWithStallGuard(page, async () => {
      if ((await handCount(page)) >= 14) return { done: true, key: await readCeremonyKey(page) };
      if (!(await readIsMyPickupTurn(page))) {
        // BOT WINDOW — our hand must hold completely while another seat picks up.
        const idle = await handClimbOver(page, 700);
        if (idle.climb > 0) anyBetweenBatchAutoAdvance = true;
        return { done: false, key: await readCeremonyKey(page) };
      }
      const d = await readDesignation(page);
      if (!d || !d.gate || !d.gate.length) { await page.waitForTimeout(200); return { done: false, key: await readCeremonyKey(page) }; }
      sawAnyDesignation = true;
      const res = await pressDesignationTop(page, d.gate, takes);
      if (!res.clicked) { await page.waitForTimeout(200); return { done: false, key: await readCeremonyKey(page) }; }
      const delta = res.handAfter - res.handBefore;
      presses++;
      // Per-batch hard signals (actor-scoped). Aggregated so the failing batch is
      // named in `batches`; the killer asserts fire after the loop.
      if (!res.matched || res.hovered !== d.gate[0]) allBatchesExactHover = false;
      if (res.frames.length !== 1) allBatchesOneEmit = false;
      if (!(res.frames.length === 1 && res.frames[0].seatIndex === 0)) allBatchesMySeat = false;
      if (delta !== d.count) allBatchesExact = false;
      // after the press, the hand must hold until the NEXT deliberate press
      const between = await handClimbOver(page, 1200);
      if (between.climb > 0) anyBetweenBatchAutoAdvance = true;
      batches.push({ phase: d.phase, count: d.count, delta, emit: res.frames.length, seatIndex: res.frames[0]?.seatIndex ?? null, matched: res.matched, hovered: res.hovered, betweenClimb: between.climb });
      return { done: res.handAfter >= 14, key: await readCeremonyKey(page) };
    }, { stallMs: 45_000, capMs: 120_000, pollMs: 0 });

    const handFinal = await handCount(page);
    await shot(page, 'new2-human-driven-pickup.png');
    recordEvidence('new2-human-driven-pickup.json', {
      preDesignationPresent: !!preDesignation,
      noPressClimb: noPress, presses, allBatchesExact, allBatchesExactHover, allBatchesOneEmit, allBatchesMySeat, anyBetweenBatchAutoAdvance,
      sawAnyDesignation, batches, handFinal,
      stallGuard: { done: guard.done, stalled: guard.stalled, capped: guard.capped, elapsedMs: guard.elapsedMs, keyChanges: guard.keyChanges, maxIdleMs: guard.maxIdleMs },
      note: 'NEW-2 anti-happy-path (actor-scoped). Each batch is driven by a REAL grid-scanned pointer press on targetSlots[0] (pressWallTargetByHover: settle→scan footprint until world.hovered===target→real mouse down/up, defeating the intermittent ~16px angled-face projection offset Hudson proved). GREEN: designation present (noPressClimb=0), 5 discrete presses of counts [4,4,4,1,1] each hard-hovering the exact target and emitting EXACTLY ONE {seatIndex:0,count} pickup.take, own-hand delta === count, hand holds during bot windows, hand reaches 14. Drive is progress-aware/stall-guarded (stallGuard); a genuine RollingDice/no-designation stall surfaces via the asserts, never hidden.',
    });

    // (1) NO auto-advance without a press — the killer anti-happy-path assertion.
    expect(noPress.climb, `NEW-2(1): the hand must NOT auto-advance without a human press; it climbed ${noPress.climb} (from ${noPress.start} to ${noPress.end}) — RED@200cad4 = client auto-drive self-fills`).toBe(0);
    // (2) a real single-trigger designation must exist for the human to press.
    expect(sawAnyDesignation, 'NEW-2(2): a single-trigger pickup.targetSlots must be shipped for the human to press each batch — RED@200cad4 = no targetSlots designation ever appears').toBe(true);
    // (3) the ceremony was driven by discrete human presses, each taking exactly one batch.
    // The dealer owns EXACTLY 5 batches: 3 × count-4 (PickupRound1..3) + 2 × count-1
    // (SingleTilePickup + DealerExtra) ⇒ 14 tiles.
    expect(presses, `NEW-2(3): the dealer must drive its OWN 5 batches by real presses on targetSlots[0]; performed ${presses}, batches=${JSON.stringify(batches)}`).toBe(5);
    expect(batches.map((b) => b.count), `NEW-2(3): the dealer's owned batch sizes must be [4,4,4,1,1]; got ${JSON.stringify(batches.map((b) => b.count))}`).toEqual([4, 4, 4, 1, 1]);
    // (3a) EXACT HOVER — every press hard-hovered the exact designated trigger (no
    // trust in the projected slot center). A miss surfaces here, never as a swallowed no-op.
    expect(allBatchesExactHover, `NEW-2(3): every press must first resolve world.hovered to the EXACT targetSlots[0] before pressing; batches=${JSON.stringify(batches)}`).toBe(true);
    // (3b) EXACTLY ONE outbound take per press (fail on any 0-emit), scoped to my seat.
    expect(allBatchesOneEmit, `NEW-2(3): every press must emit EXACTLY ONE outbound pickup.take frame (0 emit is a failed actuation); batches=${JSON.stringify(batches)}`).toBe(true);
    expect(allBatchesMySeat, `NEW-2(3): every outbound take must carry {seatIndex:0}; batches=${JSON.stringify(batches)}`).toBe(true);
    expect(allBatchesExact, 'NEW-2(3): every press must take EXACTLY the designated batch count (one interaction per batch)').toBe(true);
    expect(handFinal, `NEW-2(3): the dealer's five owned batches must total 14 tiles; handFinal=${handFinal}`).toBe(14);
    expect(anyBetweenBatchAutoAdvance, 'NEW-2(2): the hand must NOT advance between presses or during a bot window (no auto-drive)').toBe(false);
  });
});

// NEW-2b (Ripley 2026-08-07): the pickup HUD TEARDOWN, ruled a VALID RED that is
// DISTINCT from D1. D1 (Vasquez) is the COLLECTION clear — pickup["current"] goes
// null via the post-deal full-snapshot map.clear() — and is a GREEN LOCK (defensive,
// correct @200cad4). But that same map.clear() path fires NO per-key 'update' for
// 'current', so game-ui.ts onPickupUpdate never re-runs renderPickupHud(null), and
// the "Take N" banner (#pickup-hud) ORPHANS on screen while the collection is already
// empty. That lingering banner is a separate FRONTEND UI-teardown defect (routed to
// Hicks): the HUD must hide when rawPickup clears. This gate proves the RENDERED
// result, not the collection (which D1 owns): once pickup["current"] is null, the
// #pickup-hud banner must be gone. RED@200cad4 (collection null BUT banner visible);
// GREEN when the HUD teardown is bound to the clear (or the backend emits the
// EncodePickupCleared that fires an 'update'). A VISIBLE banner at the assertion point
// is intrinsic proof it rendered ⇒ the gate cannot pass vacuously; only a genuine
// teardown (banner hidden) greens it. Discipline: observation only — no emitTakePickup,
// no direct pickup mutation; we read the real DOM + the client's own pickup collection.
test.describe('NEW-2b pickup HUD teardown — the Take-N banner must not orphan after the collection clears', () => {
  test('#pickup-hud is torn down once pickup["current"] clears post-ceremony (no lingering Take-N banner)', async ({ page }, testInfo) => {
    // BOTH projects. Hudson's Pixel 5 probe proved `#pickup-take-btn` is
    // rendered, hit-testable and 5/5 pressable under touch emulation, and the
    // full chromium-vs-mobile-chrome matrix (Ripley, 2026-08-11, 30/30 cases,
    // 0 skipped) came back BIT-IDENTICAL on both projects: every case that
    // passes on chromium passes on mobile-chrome and every failure reproduces
    // on both. There is no mobile-specific defect here — a chromium-only skip
    // would have masked a real product defect as a "mobile viewport" issue.
    // No extra readiness gate is needed either: makeConfig carries `seat: 0`
    // on the URL, so the client is auto-seated and the `.take-seat` control is
    // never rendered (seatsRowDisplay:"none").
    testInfo.setTimeout(150_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `new2b-hudteardown-${Date.now()}`, seat: 0, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
    await tryRealRoll(page);

    // Robust visibility read for the position:absolute banner + the client's own
    // authoritative pickup["current"]. visible ⟺ not [hidden], computed display not
    // none, not visibility:hidden, and it actually has layout (rect height > 0).
    const readHud = () => page.evaluate(() => {
      /* eslint-disable @typescript-eslint/no-explicit-any */
      const el = document.getElementById('pickup-hud');
      let visible = false;
      if (el) {
        const cs = getComputedStyle(el); const rect = el.getBoundingClientRect();
        visible = !el.hidden && cs.display !== 'none' && cs.visibility !== 'hidden' && rect.height > 0;
      }
      const g = (window as any).game;
      const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
      let hand = 0; const w = g?.world; if (w?.things) for (const t of w.things.values()) if (/^hand\.\d+@0$/.test(String(t?.slot?.name ?? ''))) hand++;
      return { present: !!el, visible, pickupCurrent: pu ? { count: pu.count, seatIndex: pu.seatIndex } : null, hand };
      /* eslint-enable @typescript-eslint/no-explicit-any */
    });

    // Drive the ceremony to completion via real per-batch presses on the
    // rendered #pickup-take-btn (shared takePickup) — the client auto-drive
    // that used to self-fill the hand (@200cad4) was removed (D5), so
    // without a real press per batch the ceremony stalls on the dealer's
    // own unpressed first window and never reaches the post-ceremony
    // tombstone this gate is actually about. Still sampling the banner every
    // tick so the teardown assertion below stays provably non-vacuous (the
    // banner is expected to appear while the pickup affordance is live).
    let hudEverVisible = false;
    // Progress-aware, stall-guarded drive to the POST-CEREMONY tombstone: press our own
    // batches until the hand holds its drawn tile (hasExtraHandTile) AND the authoritative
    // pickup collection has cleared (pickup["current"] === null — the post-deal full-snapshot
    // map.clear() path this gate is about). Replaces the fixed 90s window (+2.5s settle) that,
    // under CI mobile saturation (run 31594744369, job 94107472857), expired mid-ceremony — a
    // LIVE BreakPointMarked, count 4, seat 0 — so the teardown gate asserted against a live
    // pickup, not the tombstone. Sample the banner every tick so hudEverVisible stays a real
    // observation (non-vacuity). The pickup-clear transition IS an authoritative fingerprint
    // change (readCeremonyKey folds pickup.phase/seatIndex/count), so it counts as progress,
    // not a stall. A GENUINE no-progress park surfaces as the guard's stalled/capped flag with
    // pickupCurrent still live — the assertions below then fail with that diagnostic; never
    // hidden, no sleep-to-pass. stallMs 45s / capMs 120s bounded under the 150s test timeout.
    const drive = await pollWithStallGuard(page, async () => {
      const h = await readHud();
      if (h.visible) hudEverVisible = true;
      const cleared = h.pickupCurrent === null;
      const extra = await hasExtraHandTile(page);
      if (!extra && await readIsMyPickupTurn(page)) await takePickup(page);
      return { done: extra && cleared, key: await readCeremonyKey(page) };
    }, { stallMs: 45_000, capMs: 120_000, pollMs: 300 });
    const finalState = await readHud();
    await shot(page, 'new2b-pickup-hud-teardown.png');
    recordEvidence('new2b-pickup-hud-teardown.json', {
      hudEverVisible, finalState,
      stallGuard: { done: drive.done, stalled: drive.stalled, capped: drive.capped, elapsedMs: drive.elapsedMs, keyChanges: drive.keyChanges, maxIdleMs: drive.maxIdleMs },
      note: 'NEW-2b pickup HUD teardown (distinct from D1 collection clear). RED@200cad4: the post-deal full-snapshot map.clear() empties pickup["current"] WITHOUT firing a per-key update, so game-ui.ts renderPickupHud(null) never runs and the #pickup-hud "Take N" banner orphans on screen (finalState.pickupCurrent==null AND finalState.visible==true). GREEN when the HUD teardown is bound to the collection clear (Hicks) or the backend emits EncodePickupCleared that fires the update. A visible banner here is intrinsic proof it rendered — the gate cannot pass vacuously.',
    });

    // Precondition (D1 GREEN LOCK): the collection MUST have cleared post-ceremony, so
    // the ONLY thing under test is the rendered banner teardown.
    expect(finalState.pickupCurrent, `precondition (D1): pickup["current"] must be null post-ceremony so this gate isolates the HUD teardown; got ${JSON.stringify(finalState.pickupCurrent)} (ceremony drive: done=${drive.done} stalled=${drive.stalled} capped=${drive.capped} elapsedMs=${drive.elapsedMs} maxIdleMs=${drive.maxIdleMs})`).toBeNull();
    // NEW-2b RED gate: once the collection is empty, the banner must be torn down.
    expect(finalState.visible, `NEW-2b: the #pickup-hud "Take N" banner must be TORN DOWN once pickup["current"] clears — RED@200cad4 the banner orphans (collection null but banner still visible); hudEverVisible=${hudEverVisible}`).toBe(false);
  });
});

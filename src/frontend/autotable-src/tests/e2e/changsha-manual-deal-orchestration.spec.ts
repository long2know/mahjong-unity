// D2 / NEW-1 (revision owner: hicks, 2026-08-11 — stale test-harness revision per
// Hudson's re-review; original harness rejected for not driving the human ceremony).
// A REAL, SEPARATE backend deal-orchestration golden (NOT the R-1 pickup-interaction
// defect — tagged separately so it does not muddy G17). Root (Vasquez, grounded
// @200cad4): ChangshaGameRuntime.StartGameAsync manual branch returned WITHOUT
// scheduling the BOT dealer's dice roll; the bot-dealer roll only fired via
// ScheduleBotIfNeededAsync on hands 2+. So a manual HAND 1 with a BOT dealer parked in
// RollingDice forever (nobody rolled: the bot wasn't scheduled and a non-dealer human's
// blind rollDice is server-rejected) ⇒ no pickup cursor ever targeted the human ⇒ the
// human's hand stayed 0. The product candidate (:18088) SCHEDULES the bot-dealer roll
// for hand-1 manual too, so the non-dealer human now receives their pickup windows.
//
// Acceptance (GREEN on the candidate): a NON-DEALER human (seat 1; the dealer is the
// seat-0 BOT) in a manual deal receives their per-batch pickup windows and, DRIVING
// each owned batch with a real #pickup-take-btn press (never clicking during a bot's
// window), reaches exactly 13 tiles — 4 owned batches [4,4,4,1]; a non-dealer gets NO
// DealerExtra. Then the ceremony pickups for our seat are exhausted. The hand is read
// on the LOCAL seat (world.seat), so the seat-0 BOT dealer's hand filling to 14 can
// NEVER satisfy this vacuously (the prior harness counted `hand.*@0` — the bot dealer's
// seat — and would have passed without the human ever acting). RED@200cad4 stalls (0
// human presses possible: the window never reaches the human).
//
// Discipline: genuine rendered controls only — the human takes each owned batch via the
// real "Take N" HUD button (shared takePickup), pressed ONLY when it is genuinely our
// pickup turn (readIsMyPickupTurn). No emitTakePickup, no synthetic pickup.take, no
// collection injection, no roll backdoor (the bot dealer rolls server-side; the human,
// a non-dealer, never rolls).
import { test, expect } from '@playwright/test';
import { buildGameUrl, makeConfig, dismissLobbyAndTour, ensureConnected, takeSeatByClick, clickDeal, readIsMyPickupTurn, takePickup } from './_playability';
import { pollWithStallGuard, readCeremonyKey } from './_ceremony-progress';
import { recordEvidence, shot } from './_uat_red';

test.describe('D2 manual deal orchestration — non-dealer human must not stall (backend)', () => {
  test('non-dealer human (seat 1) in a manual deal reaches 13 tiles (no RollingDice stall)', async ({ page }, testInfo) => {
    testInfo.setTimeout(150_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `d2-nondealer-${Date.now()}`, seat: 1, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    // Take a NON-dealer seat so the DEALER is the seat-0 BOT (the stall condition).
    await takeSeatByClick(page, 1); await page.waitForTimeout(800);
    await clickDeal(page).catch(() => {});

    // Read the LOCAL (seat-1) human's hand + orchestration cursor. hand is counted via
    // world.seat, NOT a hardcoded `@0`, so the seat-0 BOT dealer's own hand can never
    // satisfy this vacuously. The dealer/pickup cursor come from the authoritative
    // match/pickup collections.
    const readOrchestration = (): Promise<{ seat: number | null; hand: number; phase: string | null; dealer: number | null; pickupSeat: number | null }> => page.evaluate(() => {
      /* eslint-disable @typescript-eslint/no-explicit-any */
      const g = (window as any).game; const w = g?.world;
      const seat = typeof w?.seat === 'number' ? w.seat : null;
      let h = 0;
      if (w?.things && seat !== null) for (const t of w.things.values()) {
        if (t?.slot?.group === 'hand' && t.slot?.seat === seat && t.slot?.thing === t
            && !String(t.slot?.name ?? '').startsWith('hand.extra@')) h++;
      }
      const m = g?.client?.match?.get ? g.client.match.get(0) : null;
      const tn = g?.client?.turn?.get ? g.client.turn.get('current') : null;
      const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
      return {
        seat, hand: h, phase: tn?.phase ?? null,
        dealer: typeof m?.dealer === 'number' ? m.dealer : null,
        pickupSeat: pu && typeof pu.seatIndex === 'number' ? pu.seatIndex : null,
      };
      /* eslint-enable @typescript-eslint/no-explicit-any */
    });

    // HUMAN-DRIVEN pickup ceremony: the seat-1 non-dealer owns 4 batches [4,4,4,1] ⇒ 13.
    // Press the real "Take N" HUD button ONLY when it is genuinely our pickup turn; the
    // three bots (incl. the seat-0 dealer) auto-take their own windows between ours, so
    // we NEVER click during a bot window. Nothing advances our hand absent a real press.
    let hand = 0; let phase: string | null = null; let dealer: number | null = null;
    let seat: number | null = null; let presses = 0;
    // Progress-aware, stall-guarded drive (replaces the fixed 90s window that flaked under
    // CI saturation, run 31576171218): keep driving as long as the authoritative ceremony
    // fingerprint advances (turn phase/activeSeat, pickup cursor phase@seat:count, our hand
    // — all server-pushed). We press ONLY when it is genuinely our pickup turn; bots take
    // their own windows between ours. A GENUINE RollingDice stall (the cursor never reaches
    // the human ⇒ no progress) is NOT hidden — it falls through to the acceptance asserts
    // below (presses===0 / hand<13 fail with the RED@200cad4 diagnostic). stallMs 45s ≫ the
    // worst measured inter-progress gap under saturation; capMs 120s < the 150s test timeout
    // minus connect/seat/deal setup.
    const guard = await pollWithStallGuard(page, async () => {
      const s = await readOrchestration();
      hand = s.hand; phase = s.phase; dealer = s.dealer; seat = s.seat;
      if (s.hand < 13 && await readIsMyPickupTurn(page)) {
        const pu = await takePickup(page);
        if (pu.ok) presses++;
      }
      return { done: s.hand >= 13, key: await readCeremonyKey(page) };
    }, { stallMs: 45_000, capMs: 120_000, pollMs: 500 });
    { const s = await readOrchestration(); hand = s.hand; phase = s.phase; dealer = s.dealer; seat = s.seat; }

    // Settle: once our 13 tiles are in, the only remaining ceremony batch is the
    // DEALER's DealerExtra (a bot), so our seat must no longer own a pickup — the
    // non-dealer analog of the post-ceremony pickup tombstone / awaiting-discard state.
    let isMyPickupTurnAfter = await readIsMyPickupTurn(page);
    const settleBy = Date.now() + 8000;
    while (isMyPickupTurnAfter && Date.now() < settleBy) {
      await page.waitForTimeout(300);
      isMyPickupTurnAfter = await readIsMyPickupTurn(page);
    }
    const post = await readOrchestration();

    await shot(page, 'd2-nondealer-stall.png');
    recordEvidence('d2-nondealer-deal.json', {
      seat, dealer, phase, handReached: hand, presses, isMyPickupTurnAfter, pickupSeatAfter: post.pickupSeat,
      stallGuard: { done: guard.done, stalled: guard.stalled, capped: guard.capped, elapsedMs: guard.elapsedMs, keyChanges: guard.keyChanges, maxIdleMs: guard.maxIdleMs },
      note: 'GREEN on candidate :18088: the bot-dealer roll IS scheduled for hand-1 manual, so the seat-1 non-dealer human receives their pickup windows and — driving each owned batch with a real #pickup-take-btn press (never during a bot window) — reaches exactly 13 tiles (4 owned batches [4,4,4,1]; no DealerExtra). Hand is read on the LOCAL seat (world.seat), so the seat-0 bot dealer hand can never satisfy this vacuously. RED@200cad4 stalled in RollingDice (bot-dealer roll unscheduled) ⇒ 0 human presses possible. Drive is progress-aware/stall-guarded (stallGuard); a genuine RollingDice stall surfaces via presses/hand asserts, never hidden.' });

    // ACCEPTANCE — orchestration: the dealer is the seat-0 BOT (the stall condition),
    // the non-dealer human is dealt in through their OWN real presses, reaches exactly
    // 13 (nondealer13), and the ceremony pickups for our seat are then exhausted.
    expect(seat, 'the human must be seated at the non-dealer seat 1').toBe(1);
    expect(dealer, `D2: the dealer must be resolved (bot-dealer orchestration condition); got dealer=${dealer}`).not.toBeNull();
    expect(dealer, `D2: the dealer must be a BOT (seat 0), not the human at seat 1; got ${dealer}`).not.toBe(1);
    expect(presses, `D2: the non-dealer human must DRIVE its own pickup batches with real #pickup-take-btn presses (≥1); performed ${presses} — RED@200cad4 = 0 presses possible (RollingDice stall: the window never reaches the human)`).toBeGreaterThanOrEqual(1);
    expect(hand, `D2: a non-dealer human in a manual deal must reach exactly 13 tiles through its own presses; got ${hand} (dealer=${dealer}, phase=${phase}) — RED@200cad4 = RollingDice stall`).toBe(13);
    expect(isMyPickupTurnAfter, 'D2: after reaching 13 the ceremony pickups for our seat must be exhausted (isMyPickupTurn()==false — the remaining DealerExtra belongs to the bot dealer)').toBe(false);
  });
});

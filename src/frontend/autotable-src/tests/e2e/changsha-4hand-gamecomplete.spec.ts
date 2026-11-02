// GATE (likely GREEN lock): full 4-hand human-vs-3-bot game reaches authoritative
// GameComplete via REAL clicks, in flat + perspective, on desktop + mobile
// (project matrix). Complements the existing playability-gate.
//
// Drake (Lane C, 2026-08-11) — mobile cadence correction. The old fixed 200s
// deadline + call-`discardByPointer`-every-iteration loop finished only ~2.1 of
// 4 hands on the Pixel-5 viewport (measured discards≈28, nextHands=0) even
// though the game progressed normally: the wall depleted, hands ended, and the
// next hand AUTO-DEALT server-side (the `#result-modal`/Next-Hand path never
// gates progression here, and never displays on the mobile layout). Two fresh
// instrumented mobile games reached authoritative GameComplete at 339s and 341s
// (both zero-sum, 0 errors), with a max within-hand progress gap of ~12s. So
// this is SLOW PROGRESS, not a stall or a product defect. The fix is a
// justified per-project time budget covering that measured cadence, a loop that
// only spends a real pointer discard when the seat authoritatively owes one, and
// a monotonic no-progress guard that FAILS WITH DIAGNOSTICS on a genuine hard
// stall (distinguishing it from slow play) rather than masking one with retries.
// GameComplete is not weakened — it is strengthened with a zero-sum score check.
import { test, expect, type Page } from '@playwright/test';
import {
  buildGameUrl, makeConfig, dismissLobbyAndTour, ensureConnected, takeSeatByClick,
  clickDeal, waitForPlayableHand, discardByPointer, claimByClick, readClaimWindow,
  readGameComplete, isResultModalVisible, clickNextHand, readIsMyPickupTurn, takePickup,
  hasExtraHandTile, readDiscardCount, readTotalScores,
  installHandEndObserver, readHandEndObserver, pressViewToggle, readCameraType,
} from './_playability';
import { recordEvidence, shot, installErrorGate } from './_uat_red';

// OBSERVE — authoritative turn cursor (read-only), for progress diagnostics only.
async function readTurn(page: Page): Promise<{ phase: string | null; active: number | null }> {
  return page.evaluate(() => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const t = (window as any).game?.client?.turn?.get('current') ?? null;
    return { phase: t?.phase ?? null, active: typeof t?.activeSeat === 'number' ? t.activeSeat : null };
  });
}

test.describe('GATE gamecomplete: 4-hand authoritative GameComplete (real clicks)', () => {
  test('reaches GameComplete; toggles flat/perspective; zero errors', async ({ page }, testInfo) => {
    const isMobile = testInfo.project.name === 'mobile-chrome';
    // Justified play/time budget covering the MEASURED cadence (Drake 2026-08-11):
    // mobile 4-hand GameComplete lands ≈340s on :18089 under real bot pacing, so
    // 480s play (≈1.4×) absorbs contention spikes; desktop is far faster and keeps
    // the original ≈200s envelope. Test timeout = play budget + setup/teardown.
    const playBudgetMs = isMobile ? 480_000 : 210_000;
    // No-progress guard: max within-hand progress gap measured ≈12s, so this is
    // far above normal slow play yet well under a real hard stall (≈300s).
    const stallLimitMs = isMobile ? 90_000 : 60_000;
    testInfo.setTimeout(playBudgetMs + 90_000);

    const gate = installErrorGate(page);
    await page.setViewportSize(isMobile ? { width: 390, height: 844 } : { width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `red-gc-${testInfo.project.name}-${Date.now()}`, dealMode: 'auto', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    // Latch every authoritative hand end (the same `result` stream game-ui.ts
    // renders) so the progress/stall metric is server-authoritative, not visual.
    await installHandEndObserver(page);
    await takeSeatByClick(page, 0); await clickDeal(page); await waitForPlayableHand(page, 60_000).catch(() => {});
    await installHandEndObserver(page); // re-arm after the deal (idempotent).

    const cam0 = await readCameraType(page);
    await pressViewToggle(page); await page.waitForTimeout(400);
    const cam1 = await readCameraType(page);
    await pressViewToggle(page); await page.waitForTimeout(300); // back

    const t0 = Date.now();
    const deadline = t0 + playBudgetMs;
    let discards = 0, claimsPassed = 0, nextHands = 0, iters = 0;
    // Monotonic progress: a hand ended, OR this hand's discard pile hit a new
    // high-water (a claim can briefly shrink the pile, so we track the max, which
    // never regresses). Either resets the stall clock.
    let handsSeen = 0, handDiscHigh = -1, lastProgressAt = t0, stalled = false;
    let gc = await readGameComplete(page);
    while (Date.now() < deadline && !gc.isComplete) {
      iters++;
      const claim = await readClaimWindow(page);
      if (await isResultModalVisible(page)) {
        // A settled winning hand can still contain fourteen tiles. Continue
        // before looking for another discard; the server owns the next deal.
        if (await clickNextHand(page, 6000)) nextHands++;
      } else if (claim.open) {
        // Real click on the visible claim overlay (wins with Hu when offered,
        // else passes) — advances play without any synthetic dispatch.
        await claimByClick(page); claimsPassed++;
      } else if (await readIsMyPickupTurn(page)) {
        await takePickup(page).catch(() => undefined);
      } else if (await hasExtraHandTile(page)) {
        // Only spend a real pointer discard when the seat AUTHORITATIVELY owes
        // one — this is what keeps the mobile loop fast enough to finish 4 hands.
        const d = await discardByPointer(page); if (d.ok) discards++;
      } else {
        await page.waitForTimeout(500); // bots acting / between-hand auto-deal.
      }

      const heTick = await readHandEndObserver(page);
      if (heTick.ends.length > handsSeen) { handsSeen = heTick.ends.length; handDiscHigh = -1; lastProgressAt = Date.now(); }
      const disc = await readDiscardCount(page);
      if (disc > handDiscHigh) { handDiscHigh = disc; lastProgressAt = Date.now(); }
      gc = await readGameComplete(page);
      if (!gc.isComplete && Date.now() - lastProgressAt > stallLimitMs) { stalled = true; break; }
    }

    const scores = await readTotalScores(page);
    const scoreSum = scores ? Object.values(scores).reduce((a, b) => a + b, 0) : null;
    const he = await readHandEndObserver(page);
    const turn = await readTurn(page);
    await shot(page, `gc-${testInfo.project.name}-final.png`);
    recordEvidence(`red-gamecomplete-${testInfo.project.name}.json`, {
      project: testInfo.project.name, reachedGameComplete: gc.isComplete, discards, claimsPassed, nextHands,
      iters, handEnds: he.ends.length, elapsedSec: Math.round((Date.now() - t0) / 1000),
      stalled, lastProgressAgoMs: Date.now() - lastProgressAt, terminalTurn: turn,
      scores, scoreSum, cameraToggle: { cam0, cam1, toggled: cam0 !== cam1 }, consoleErrors: gate.errors,
    });

    expect(cam0 !== cam1, 'view toggle must switch flat<->perspective').toBe(true);
    // A genuine hard stall (no authoritative progress for stallLimitMs) is a
    // real defect to report, NOT something to mask — surface it distinctly.
    expect(stalled, `no authoritative progress for ${stallLimitMs}ms — HARD STALL at phase=${turn.phase} active=${turn.active} handEnds=${he.ends.length} discards=${discards}`).toBe(false);
    expect(gc.isComplete, `must reach authoritative GameComplete (elapsed=${Math.round((Date.now() - t0) / 1000)}s discards=${discards} handEnds=${he.ends.length} nextHands=${nextHands})`).toBe(true);
    expect(scores, 'GameComplete must surface authoritative totalScores').not.toBeNull();
    expect(scoreSum, `Changsha scoring must be zero-sum; scores=${JSON.stringify(scores)}`).toBe(0);
    expect(gate.errors, `zero console/page errors; saw ${JSON.stringify(gate.errors.slice(0, 3))}`).toEqual([]);
  });
});

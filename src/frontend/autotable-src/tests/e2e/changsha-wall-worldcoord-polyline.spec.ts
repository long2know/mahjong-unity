// G4 (OWNED by hudson-1) — wall WORLD-COORD contiguity. ACCEPTANCE = the RENDER
// world-position polyline is ONE physically-contiguous perimeter
// (`worldSingleContiguous`: single run + no >70u gap) — corners ALLOWED. This is
// F1-INDEPENDENT (Ripley 2026-08-07 10:57): it catches four-half-walls (runs>1)
// and real gaps, but NOT dice-ANCHOR drift. GREEN@200cad4 for dealer 0 (the #160
// wall-flow fix) = a must-preserve regression guard.
// REJECTED metric: strict "one-pitch across every corner" (`strictOnePitchPolyline`)
// is over-tight/UNSATISFIABLE for a real square wall (normal corner insets ~39-53u
// >> ~6u pitch) — kept as a DIAGNOSTIC only, never the gate. perimeterRuns (STACKS
// frame [14,14,13,13]) is likewise F1-frame DIAGNOSTIC, NOT the gate.
// Live UI only deals dealer 0 (match.dealer=this.seat); dealers 1-3 × sums 2..12
// are Bishop's backend DICE-ANCHOR golden test (∀ dealer×sum, incl. Wall[0]→top /
// Wall[1]→bottom / no-occluded-trigger). This browser gate is the world-contiguity
// complement, plus an OPTIONAL top-first DEPLETION observation (F2 locked — half-
// consumed columns keep the BOTTOM; recorded as evidence, the gate stays
// worldSingleContiguous).
// BREAK-ANCHOR extension (Ripley 2026-08-07 10:28): additionally sweep dice sums
// at dealer 0 and assert the break trigger (pickup.targetSlots[0] = Wall[0] at
// BreakPointMarked) resolves to the REACHABLE-TOP exposed-front tile. Schema-
// blocked on Bishop's targetSlots (RED@200cad4) and §F1 for the anchor position;
// dealers 1-3 × sums remain Bishop's backend golden (the live UI deals dealer 0).
import { test, expect } from '@playwright/test';
import { buildGameUrl, makeConfig, dismissLobbyAndTour, ensureConnected, takeSeatByClick, clickDeal, waitForPlayableHand } from './_playability';
import { analyzeWall, recordEvidence, shot } from './_uat_red';

async function dealerDice(page) {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game; const d = g?.client?.dice?.get ? g.client.dice.get(0) : null; const m = g?.client?.match?.get ? g.client.match.get(0) : null;
    const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
    return { dealer: m?.dealer ?? null, diceSum: Array.isArray(d?.dice) ? d.dice.reduce((a: number, b: number) => a + b, 0) : null, breakPoint: pu?.breakPoint ?? null };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

// Capture the BREAK trigger during BreakPointMarked: pickup.targetSlots[0] is
// Wall[0] (the break tile). Its render position + reachability tells us whether
// the dice/dealer break ANCHOR resolves to the reachable-top exposed-front tile.
async function captureBreakAnchor(page) {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game; const w = g?.world;
    const pu = g?.client?.pickup?.get ? g.client.pickup.get('current') : null;
    if (!pu) return null;
    const target: string[] | null = Array.isArray((pu as any).targetSlots) ? (pu as any).targetSlots.map(String) : null;
    let t0: any = null;
    if (target && target.length && w?.things) for (const t of w.things.values()) if (String(t?.slot?.name) === target[0]) { let p = { x: 0, y: 0, z: 0 }; try { const pl = t.place(); p = { x: pl.position.x, y: pl.position.y, z: pl.position.z }; } catch { /* */ } const up = t.slot?.links?.up; t0 = { x: Math.round(p.x), y: Math.round(p.y), reachable: !(up && up.thing) }; }
    return { phase: pu.phase, count: pu.count, breakPoint: pu.breakPoint, hasDesignation: !!(target && target.length), breakName: target && target.length ? target[0] : null, breakReachable: t0 ? t0.reachable : null, breakPos: t0 ? { x: t0.x, y: t0.y } : null };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

// TOP-first depletion observation (Ripley F2, OPTIONAL — acceptance stays
// worldSingleContiguous): group wall tiles by world footprint; classify each
// remaining tile as top vs bottom by z within its column. Under top-first draw a
// half-consumed column keeps its BOTTOM (the top was drawn first) ⇒ halfTop==0.
async function readDepletionTopFirst(page) {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world;
    const cols: Record<string, number[]> = {};
    if (w?.things) for (const t of w.things.values()) if (t?.slot?.group === 'wall') { let p = { x: 0, y: 0, z: 0 }; try { const pl = t.place(); p = { x: pl.position.x, y: pl.position.y, z: pl.position.z }; } catch { /* */ } const key = Math.round(p.x / 3) + ',' + Math.round(p.y / 3); (cols[key] ||= []).push(p.z); }
    const zAll = Object.values(cols).flat().sort((a, b) => a - b);
    const zmid = zAll.length ? zAll[Math.floor(zAll.length / 2)] : 0;
    let full = 0, halfBottom = 0, halfTop = 0;
    for (const zs of Object.values(cols)) { if (zs.length >= 2) full++; else if (zs.length === 1) { if (zs[0] <= zmid) halfBottom++; else halfTop++; } }
    return { columns: Object.keys(cols).length, full, halfBottom, halfTop };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

test.describe('G4 wall WORLD-COORD contiguity (worldSingleContiguous; corners allowed)', () => {
  test('auto seed-sweep over dice sums — every deal is ONE physically-contiguous perimeter', async ({ page }, testInfo) => {
    testInfo.setTimeout(240_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const seeds = [101, 202, 303, 404, 505, 606, 707, 808, 909, 1010];
    const samples: any[] = [];
    for (const seed of seeds) {
      const cfg = makeConfig({ gameId: `red-wall-${seed}-${Date.now()}`, seed, dealMode: 'auto', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
      await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
      await page.waitForTimeout(900); await dismissLobbyAndTour(page); await ensureConnected(page);
      await takeSeatByClick(page, 0); await clickDeal(page); await waitForPlayableHand(page, 45_000).catch(() => {});
      await page.waitForTimeout(1200);
      const dd = await dealerDice(page); const a = await analyzeWall(page); const dep = await readDepletionTopFirst(page);
      samples.push({ seed, dealer: dd.dealer, diceSum: dd.diceSum, tiles: a.tiles, worldSingleContiguous: a.worldSingleContiguous, worldPolylineRuns: a.worldPolylineRuns, perimeterRuns: a.perimeterRuns, pitch: a.pitch, cornerDiscontinuities: a.cornerDiscontinuities, strictOnePitch: a.strictOnePitchPolyline, discontinuitySamples: a.discontinuitySamples, depletion: dep });
    }
    await shot(page, 'wall-seed-sweep-last.png');
    const diceSumsCovered = [...new Set(samples.map((s) => s.diceSum))].sort((a, b) => a - b);
    const dealersCovered = [...new Set(samples.map((s) => s.dealer))];
    const failing = samples.filter((s) => !s.worldSingleContiguous);
    // OPTIONAL top-first depletion observation (acceptance stays worldSingleContiguous):
    // under top-first draw, half-consumed columns keep the BOTTOM ⇒ Σ halfTop should
    // be ~0. Recorded as evidence + a soft signal (NOT the gate).
    const halfTopTotal = samples.reduce((n, s) => n + (s.depletion?.halfTop ?? 0), 0);
    const halfBottomTotal = samples.reduce((n, s) => n + (s.depletion?.halfBottom ?? 0), 0);
    recordEvidence('red-wall-perimeter-sweep.json', { seeds: seeds.length, diceSumsCovered, dealersCovered, failingCount: failing.length, depletionTopFirst: { halfTopTotal, halfBottomTotal, note: 'top-first ⇒ halfTop≈0; observation only, gate = worldSingleContiguous' }, samples });

    for (const s of samples) {
      // ACCEPTANCE = worldSingleContiguous (render WORLD positions, F1-independent):
      // one physically-contiguous perimeter — corners ALLOWED (~one-pitch is over-
      // tight / unsatisfiable for a real square wall), but four-half-walls (runs>1)
      // and real >70u gaps are caught. perimeterRuns / strictOnePitch / cornerDisc
      // are F1-frame DIAGNOSTICS only (STACKS=[14,14,13,13]), NOT the gate.
      expect(s.worldSingleContiguous, `seed ${s.seed} (dealer ${s.dealer}, sum ${s.diceSum}): rendered wall must be ONE physically-contiguous perimeter (world coords; corners allowed, real gaps/half-walls caught). diag: perimeterRuns=${s.perimeterRuns} worldPolylineRuns=${s.worldPolylineRuns} pitch=${s.pitch} cornerDisc=${s.cornerDiscontinuities}`).toBe(true);
    }
    expect(dealersCovered, 'browser sweep covers dealer 0 only; dealers 1-3 x sums are Bishop golden-test scope (§F1)').toContain(0);
  });

  test('manual: break anchor + world-contiguous perimeter', async ({ page }, testInfo) => {
    testInfo.setTimeout(120_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `red-wall-man-${Date.now()}`, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1000); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page); await waitForPlayableHand(page, 60_000).catch(() => {});
    await page.waitForTimeout(1500);
    const dd = await dealerDice(page); const a = await analyzeWall(page);
    recordEvidence('red-wall-perimeter-manual.json', { dealer: dd.dealer, diceSum: dd.diceSum, breakPoint: dd.breakPoint, tiles: a.tiles, worldSingleContiguous: a.worldSingleContiguous, worldPolylineRuns: a.worldPolylineRuns, perimeterRuns: a.perimeterRuns, pitch: a.pitch, cornerDiscontinuities: a.cornerDiscontinuities, strictOnePitch: a.strictOnePitchPolyline, discontinuitySamples: a.discontinuitySamples });
    await shot(page, 'wall-manual-at-deal.png');
    expect(a.worldSingleContiguous, `manual: rendered wall must be ONE physically-contiguous perimeter (world coords; corners allowed, real gaps caught). diag: perimeterRuns=${a.perimeterRuns} worldPolylineRuns=${a.worldPolylineRuns} cornerDisc=${a.cornerDiscontinuities}`).toBe(true);
  });

  test('break ANCHOR (dealer\u00d7dice): the break trigger is the reachable-top exposed-front tile', async ({ page }, testInfo) => {
    testInfo.setTimeout(240_000);
    await page.setViewportSize({ width: 1600, height: 900 });
    const base = testInfo.project.use.baseURL as string;
    // Sweep seeds → vary the dice sum (hence the break ordinal) at dealer 0. Per
    // §F1 the [14,13,14,13] vs [28,28,26,26] frame mismatch mis-anchors the break
    // for some dealer×sum; dealers 1-3 are unreachable in the live UI (it always
    // deals match.dealer=this.seat=0) → Bishop's backend dice-anchor golden test.
    const seeds = [111, 222, 333, 444, 555, 666, 777, 888];
    const samples: any[] = [];
    for (const seed of seeds) {
      const cfg = makeConfig({ gameId: `red-break-${seed}-${Date.now()}`, seed, dealMode: 'manual', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
      await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
      await page.waitForTimeout(900); await dismissLobbyAndTour(page); await ensureConnected(page);
      await takeSeatByClick(page, 0); await clickDeal(page).catch(() => {});
      // grab the BreakPointMarked designation before the manual chain advances
      let brk: any = null; const t0 = Date.now();
      while (Date.now() - t0 < 8000) {
        const b = await captureBreakAnchor(page);
        if (b && b.phase === 'BreakPointMarked') { brk = b; if (b.hasDesignation) break; }
        else if (b && b.phase) { brk = brk || b; break; }
        await page.waitForTimeout(120);
      }
      const dd = await dealerDice(page);
      samples.push({ seed, dealer: dd.dealer, diceSum: dd.diceSum, breakPoint: brk?.breakPoint ?? dd.breakPoint, hasDesignation: brk?.hasDesignation ?? false, breakName: brk?.breakName ?? null, breakReachable: brk?.breakReachable ?? null, breakPos: brk?.breakPos ?? null });
    }
    const dealersCovered = [...new Set(samples.map((s) => s.dealer))];
    const diceSums = [...new Set(samples.map((s) => s.diceSum))].sort((a, b) => a - b);
    await shot(page, 'wall-break-anchor-last.png');
    recordEvidence('red-wall-break-anchor.json', { dealersCovered, diceSums, samples,
      note: 'RED@200cad4: pickup ships no targetSlots ⇒ break trigger undesignated; GREEN needs Bishop targetSlots + §F1 frame-collapse. Dealer 1-3 × sums = Bishop backend golden (browser deals dealer 0 only).' });
    const designated = samples.filter((s) => s.hasDesignation);
    expect(designated.length, 'break ANCHOR requires a targetSlots[0] break trigger at BreakPointMarked — RED@200cad4 (absent); Bishop targetSlots + §F1 fix ⇒ GREEN').toBeGreaterThan(0);
    for (const s of designated) {
      expect(s.breakReachable, `break trigger (dealer ${s.dealer}, sum ${s.diceSum}) must be the REACHABLE-TOP exposed-front tile; pos=${JSON.stringify(s.breakPos)}`).toBe(true);
    }
    expect(dealersCovered, 'browser covers dealer 0; dealers 1-3 × sums are Bishop golden-test scope (§F1)').toContain(0);
  });
});

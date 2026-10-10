// =============================================================================
//  CI real-UI walkthrough — PROMOTED TO A GATING REGRESSION TEST (#122, P1-11)
// =============================================================================
//
//  History: this spec (Phase K W23, Vasquez) used to walk home → lobby →
//  Changsha → connect → seat → deal and only WRITE findings — it could not
//  fail on a broken game. Per issue #122 (P1-11) it is now promoted to REAL
//  gating assertions, and (P1-4 E2E side) adds a real BOTS-PROGRESS gate.
//
//  Scope discipline: this is the "the real UI is not broken up to the known
//  blocker, and the bots engage" regression gate. It asserts ONLY the flow
//  that is verified to work at HEAD through real DOM/canvas interaction:
//    game boots → WS connects → real take-seat → real #deal → the seated human
//    DEALER (seat 0) drives its OWN manual-pickup batches with real
//    #pickup-take-btn presses while the bots auto-take theirs, until the pickup
//    cursor advances across ≥2 (phase, seat) states AND the dealer holds its
//    drawn 14th tile (awaiting discard). We take only our own batches (guarded
//    by the server's readIsMyPickupTurn), so a bot window is never clicked.
//  Full 4-hand completion + real Hu + scoring modal is the P0 keystone owned
//  by tests/e2e/playability-gate.spec.ts (blocked-green on #116/#119/#120).
//
//  NO WS backdoor / auto-drive / direct API / force-click is used here — every
//  forward move is a real pointer/keyboard affordance (see
//  tests/e2e/_playability.ts). Screenshots/findings are still written to
//  playtest-artifacts/ for evidence.
// =============================================================================

import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import {
  makeConfig,
  buildGameUrl,
  defangOverlays,
  dismissLobbyAndTour,
  ensureConnected,
  takeSeatByClick,
  clickDeal,
  readPickup,
  readConnected,
  waitForGameObject,
  readIsMyPickupTurn,
  takePickup,
  rollDiceIfDealer,
  hasExtraHandTile,
} from './_playability';

const ARTIFACT_DIR = path.resolve(__dirname, '../../../../../playtest-artifacts');
if (!fs.existsSync(ARTIFACT_DIR)) fs.mkdirSync(ARTIFACT_DIR, { recursive: true });

test.describe('Changsha Mahjong — real-UI walkthrough gate (boot→connect→seat→deal→bots)', () => {
  test('the real UI connects, seats, deals, and the bots engage', async ({ page, baseURL }) => {
    test.setTimeout(150_000);

    const pageErrors: string[] = [];
    page.on('pageerror', (err) => pageErrors.push(err.message));

    const resolvedBase =
      baseURL ?? process.env.E2E_BASE_URL ?? 'http://localhost:8080/autotable/';
    // Genuinely unique per run AND per process/worker so a heavily-reused backend
    // never resolves this walkthrough URL to a persisted game (identical-config
    // reconnect); every run mints a fresh seat-0 dealer ceremony.
    const uniqueGameId =
      `changsha-walkthrough-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const cfg = makeConfig({ gameId: uniqueGameId });

    // ── Boot ─────────────────────────────────────────────────────────────
    await defangOverlays(page);
    await page.goto(buildGameUrl(resolvedBase, cfg), { waitUntil: 'domcontentloaded' });
    const booted = await waitForGameObject(page);
    await page.screenshot({ path: path.join(ARTIFACT_DIR, 'walkthrough-01-boot.png'), fullPage: true });
    expect(booted, 'window.game (client+world) never initialised — bundle failed to boot').toBe(true);

    // The bundle must not throw a fatal, uncaught page error while booting.
    expect(
      pageErrors,
      `Uncaught page errors during boot: ${JSON.stringify(pageErrors.slice(0, 5))}`,
    ).toEqual([]);

    // ── Real connect + seat ───────────────────────────────────────────────
    await dismissLobbyAndTour(page);
    const connected = await ensureConnected(page);
    expect(connected, 'WS never reached connected() state through the real connect flow').toBe(true);

    const seat = await takeSeatByClick(page, cfg.seat);
    await page.screenshot({ path: path.join(ARTIFACT_DIR, 'walkthrough-02-seat.png'), fullPage: true });
    expect(seat, 'real .take-seat click did not seat the human (client.seat is null)').not.toBeNull();

    // Canvas must be present for real pointer play.
    const canvasCount = await page.locator('#main, canvas').count();
    expect(canvasCount, 'no #main canvas — 3D scene did not mount').toBeGreaterThan(0);

    // ── Real deal ─────────────────────────────────────────────────────────
    const dealButton = page.locator('#deal');
    expect(await dealButton.count(), '#deal button missing').toBeGreaterThan(0);
    const dealt = await clickDeal(page);
    expect(dealt, 'real #deal press failed (button not visible/clickable)').toBe(true);

    // ── HUMAN-DEALER CEREMONY DRIVE + BOTS-PROGRESS GATE (P1-4 E2E side) ───
    // The client auto-drive (driveManualDealChain) was removed, so the manual
    // deal ceremony is server-authoritative: the seated human DEALER (seat 0 —
    // ChangshaStateMachine sets DealerSeatIndex=0 for hand 1) must take EACH of
    // its ceremony batches with a REAL #pickup-take-btn press before its drawn
    // 14th tile lands. clickDeal above only rolled the dice to START the
    // ceremony. We take ONLY our own batches — takePickup is a no-op unless the
    // server says it is our turn (readIsMyPickupTurn), so we NEVER click a bot
    // window; the three bots auto-take their windows between ours. As the
    // ceremony advances, the server-authoritative pickup cursor moves across
    // multiple (phase, seat) states — we record each. The cursor can only leave
    // seat 0 and later RETURN to it in a subsequent phase if seats 1–3 (the
    // bots) took their batches, so ≥2 distinct states genuinely proves the bots
    // engaged. No auto-drive, no direct API, no force-click — real HUD
    // affordances only (rollDiceIfDealer / takePickup mirror the proven
    // playRealGame deal loop).
    const pickupStates = new Set<string>();
    let ownedPickups = 0;
    let lastPickupRaw: unknown = null;
    const dealDeadline = Date.now() + 90_000;
    while (Date.now() < dealDeadline && !(await hasExtraHandTile(page))) {
      if (await readIsMyPickupTurn(page)) {
        // Our authoritative pickup turn: press the REAL Take-N control.
        const pu = await takePickup(page);
        if (pu.ok) ownedPickups++;
      } else if (await rollDiceIfDealer(page)) {
        // Poll-safe: (re)fire the dealer dice roll only while its HUD is showing.
      }
      const p = await readPickup(page);
      if (p.present) {
        pickupStates.add(`${p.phase}@${p.seatIndex}`);
        lastPickupRaw = p.raw;
      }
      await page.waitForTimeout(350);
    }
    const dealerHoldsExtra = await hasExtraHandTile(page);
    await page.screenshot({ path: path.join(ARTIFACT_DIR, 'walkthrough-03-deal-ceremony.png'), fullPage: true });

    fs.writeFileSync(
      path.join(ARTIFACT_DIR, 'walkthrough-findings.json'),
      JSON.stringify(
        {
          url: page.url(),
          connected: await readConnected(page),
          seat,
          canvasCount,
          ownedPickups,
          dealerHoldsExtra,
          pickupStatesObserved: [...pickupStates],
          lastPickup: lastPickupRaw,
          pageErrors,
        },
        null,
        2,
      ),
    );

    // GOLDEN (preserved): the manual deal ceremony pickup cursor advanced across
    // ≥2 distinct (phase, seat) states after a real deal — the bots engaged.
    expect(
      pickupStates.size,
      `BOTS DID NOT ENGAGE: the manual deal ceremony pickup cursor did not ` +
        `advance across ≥2 states after a real deal (observed: ${[...pickupStates].join(', ') || 'none'}). ` +
        `A broken deal or non-advancing bots would look exactly like this.`,
    ).toBeGreaterThanOrEqual(2);

    // ANTI-VACUITY 1 — the human dealer actually DROVE ≥1 of its OWN pickup
    // batches with a real #pickup-take-btn press (never a bot window). With the
    // auto-drive removed, the ceremony cannot advance at all absent a real owned
    // press, so this rules out a vacuous pass where the cursor moved by itself.
    expect(
      ownedPickups,
      `the human dealer must drive ≥1 real ceremony pickup batch with ` +
        `#pickup-take-btn (auto-drive removed); observed ownedPickups=${ownedPickups}`,
    ).toBeGreaterThan(0);

    // ANTI-VACUITY 2 — the ceremony ran to completion for the dealer: seat 0
    // holds its drawn 14th (extra) tile and discard is armed (a genuinely
    // playable end state), reached THROUGH real presses — not a setup that
    // skipped the human-driven pickup ceremony.
    expect(
      dealerHoldsExtra,
      'the human dealer must hold its drawn 14th tile (awaiting discard) after the ' +
        'real manual pickup ceremony — reached via #pickup-take-btn, not auto-drive',
    ).toBe(true);
  });
});

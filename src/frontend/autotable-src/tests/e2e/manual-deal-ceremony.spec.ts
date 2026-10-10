// #119 (Hicks) — Changsha manual-deal ceremony regression.
//
// Blocker (found by Hudson's WP-F real-click gate, PR #128): the client's
// `World.driveManualDealChain()` drove only 4 pickups — PickupRound1..3 +
// SingleTilePickup — on the false assumption the runtime "collapses"
// SingleTilePickup and DealerExtra into one affordance.  It does NOT: they
// are distinct `ChangshaPhase` states (both gated by IsPickupPhase; see
// ChangshaStateMachine.cs / ChangshaDealingCeremony.cs).  So the runtime
// pushed the DealerExtra affordance AFTER the 4th take and the client never
// consumed it, stranding the dealer at 13 tiles with the real UI hung before
// the first discard.
//
// Fix: the chain now takes every ceremony pickup the runtime presents to our
// seat and stops the instant the terminal DealerExtra tile is claimed — the
// dealer reaches 14 tiles and the game arms AwaitingDiscard.
//
// This regression proves, through the REAL UI (connect → take-seat → click
// #deal → the production pickup chain drives the real `pickup.set('take')`
// protocol emit — NO WS `client.update([...])` backdoor):
//   1. all FIVE ceremony pickup phases fire (PickupRound1..3, SingleTilePickup,
//      DealerExtra) — captured event-driven off the pickup collection so the
//      transient DealerExtra affordance is never missed;
//   2. the dealer's hand reaches 14 tiles;
//   3. the game is discard-ready (extra tile in hand, no pending pickup);
//   4. SEVERAL distinct hand tiles are selectable via a REAL page.mouse hover
//      (regression-guards the #119 `claimedBy` undefined-vs-null defect that
//      left only 1 of 109 tiles raycastable), and a discard occurs by a REAL
//      pointer-down on a hand tile — driving the production
//      World.onDragStart click-to-discard, NOT a direct emitDiscard/backdoor.
//
// Runs against the backend serving the freshly-built bundle (C-8 harness).

import { test, expect, type Page } from '@playwright/test';

const CEREMONY_PHASES = [
  'PickupRound1',
  'PickupRound2',
  'PickupRound3',
  'SingleTilePickup',
  'DealerExtra',
];

// Defang the full-page overlays that intercept pointer events (tour /
// magic-link / signin backdrop) — same real accessibility+playability bug
// the playtest specs work around.  Injected before any document script runs.
async function defangOverlays(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const inject = (): void => {
      if (document.getElementById('hicks-de-defang')) return;
      const style = document.createElement('style');
      style.id = 'hicks-de-defang';
      style.textContent = `
        #tour-overlay,#magic-link-landing,#magic-link-overlay,#signin-modal-backdrop,
        [data-testid="tour-overlay"],[data-testid="signin-modal-backdrop"]
          { display:none !important; pointer-events:none !important; visibility:hidden !important; }
        [aria-hidden="true"] { pointer-events:none !important; }
      `;
      document.head.appendChild(style);
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', inject);
    else inject();
  });
}

// Count the local seat's concealed hand tiles from the server-authoritative
// `things` collection (keyed by tile id; slot name lives on the value).
function seatHandCountInPage(): number {
  const cli = (window as any).game?.client;
  if (!cli) return -1;
  const seat = cli.seat;
  if (seat === null || seat === undefined) return -1;
  const suffix = '@' + seat;
  let n = 0;
  for (const [, v] of cli.things.entries()) {
    const slot = v?.slotName ?? v?.SlotName;
    if (typeof slot === 'string' && slot.startsWith('hand.') && slot.endsWith(suffix)) n++;
  }
  return n;
}

test.describe('#119 Changsha manual-deal ceremony — DealerExtra regression', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'WebGL manual-deal ceremony validated on chromium only.',
    );
  });

  test('dealer walks all 5 pickup phases to 14 tiles and can discard', async ({ page }) => {
    test.setTimeout(180_000);
    await defangOverlays(page);

    const consoleErrors: string[] = [];
    page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
    page.on('pageerror', (e) => consoleErrors.push(`PAGEERROR: ${e.message}`));

    // 1) Real-UI boot: manual-deal Changsha, human at seat 0 + 3 bots, fresh id.
    const gameId = `hicks-de-${Date.now()}`;
    await page.goto(
      `./?variant=changsha&dealMode=manual&botCount=3&botDifficulty=Medium&handCount=4&gameId=${gameId}`,
      { waitUntil: 'domcontentloaded' },
    );
    await page.waitForTimeout(2000);
    for (const sel of ['#lobby-close', '#tour-skip', '#onboarding-skip']) {
      const el = page.locator(sel);
      if (await el.isVisible().catch(() => false)) {
        await el.click({ force: true, timeout: 3000 }).catch(() => undefined);
      }
    }

    // 2) Connect (real click).
    const connect = page.locator('#connect');
    if (await connect.first().isVisible().catch(() => false)) {
      await connect.first().click({ timeout: 8000 }).catch(() => undefined);
    }
    await page.waitForTimeout(2000);

    // 3) Take the first open seat (real click).
    const seats = page.locator('.take-seat');
    const seatCount = await seats.count();
    for (let i = 0; i < seatCount; i++) {
      if (await seats.nth(i).isVisible().catch(() => false)) {
        await seats.nth(i).click({ timeout: 8000 }).catch(() => undefined);
        break;
      }
    }

    // 4) Renderer up + connected + seated.
    await page
      .locator('[data-testid="three-renderer-ready"]')
      .waitFor({ state: 'attached', timeout: 90_000 });
    await page.waitForFunction(
      () => {
        const g: any = (window as any).game;
        return !!(g && g.world && g.client && g.client.connected?.() && g.world.seat !== null);
      },
      undefined,
      { timeout: 30_000 },
    );

    // 5) Capture EVERY pickup phase the runtime pushes — event-driven so the
    //    transient DealerExtra affordance (consumed within ~120ms by the fix)
    //    is never lost to a poll gap.
    await page.evaluate(() => {
      (window as any).__dealPhases = [];
      const cli = (window as any).game.client;
      cli.pickup.on('update', () => {
        const p = cli.pickup.get('current');
        const phases: string[] = (window as any).__dealPhases;
        if (p && p.phase && !phases.includes(p.phase)) phases.push(p.phase);
      });
    });

    // 6) Manual trigger via the REAL rendered control. The legacy #deal button is
    //    relay-only (hidden in Changsha) and the local world.deal auto-drive
    //    (driveManualDealChain) was dropped in the server-authoritative integration.
    //    A seated human dealer ROLLS (real click on #roll-dice), then takes each batch
    //    with the rendered "Take N" control; bots auto-pick their own batches.
    const roll = page.locator('#roll-dice');
    await roll.waitFor({ state: 'visible', timeout: 15_000 });
    expect(await roll.isVisible().catch(() => false), '#roll-dice must be visible for the seated human dealer in RollingDice').toBe(true);
    await roll.click({ timeout: 8000 });

    // 6b) Drive the human dealer's OWN pickups with genuine "Take N" clicks until the
    //     dealer holds 14. Between the human's turns the cursor rotates through the bots
    //     (which auto-pick), then returns to the human and the Take control re-arms.
    const takeBtn = page.locator('#pickup-take-btn');
    for (let i = 0; i < 8; i++) {
      const state = await page.waitForFunction(() => {
        const cli: any = (window as any).game?.client;
        if (!cli) return false;
        const seat = cli.seat;
        let n = 0;
        for (const [, v] of cli.things.entries()) {
          const sl = v?.slotName ?? v?.SlotName;
          if (typeof sl === 'string' && sl.startsWith('hand.') && sl.endsWith('@' + seat)) n++;
        }
        if (n >= 14) return 'done';
        const p = cli.pickup.get('current');
        return (p && p.seatIndex === seat && p.count > 0) ? 'mine' : false;
      }, undefined, { timeout: 40_000 }).then((h) => h.jsonValue()).catch(() => false);
      if (state === 'done') break;
      if (state === 'mine') await takeBtn.click({ timeout: 5000 }).catch(() => undefined);
    }

    // 7) Wait for the ceremony to bring the dealer to 14 tiles.
    await page.waitForFunction(
      () => {
        const cli: any = (window as any).game?.client;
        if (!cli) return false;
        const seat = cli.seat;
        if (seat === null || seat === undefined) return false;
        const suffix = '@' + seat;
        let n = 0;
        for (const [, v] of cli.things.entries()) {
          const slot = v?.slotName ?? v?.SlotName;
          if (typeof slot === 'string' && slot.startsWith('hand.') && slot.endsWith(suffix)) n++;
        }
        return n >= 14;
      },
      undefined,
      { timeout: 90_000 },
    ).catch(() => undefined);

    const capturedPhases: string[] = await page.evaluate(() => (window as any).__dealPhases ?? []);
    const dealerHand: number = await page.evaluate(seatHandCountInPage);

    // ── Assertions: all five ceremony phases, dealer at 14 ──────────────
    for (const phase of CEREMONY_PHASES) {
      expect(
        capturedPhases,
        `ceremony phase "${phase}" must fire (saw: ${JSON.stringify(capturedPhases)})`,
      ).toContain(phase);
    }
    expect(
      dealerHand,
      `dealer must hold 14 tiles after DealerExtra (was ${dealerHand}) — the 13-tile stall is the #119 bug`,
    ).toBe(14);

    // 8) Discard-ready: extra tile in hand, no pending pickup.
    const ready = await page.evaluate(() => {
      const w: any = (window as any).game.world;
      return { hasExtra: w.hasExtraHandTile?.() ?? null, isPickupTurn: w.isMyPickupTurn?.() ?? null };
    });
    expect(ready.hasExtra, 'dealer must hold the extra (14th) tile → discard armed').toBe(true);
    expect(ready.isPickupTurn, 'ceremony pickups must be exhausted (no pending pickup)').toBe(false);

    // 9) REAL pointer path — prove several distinct hand tiles are
    //    selectable via genuine page.mouse hover (the #119 `claimedBy`
    //    undefined-vs-null defect made only 1 of 109 tiles raycastable), then
    //    discard one by a real pointer-down on a hand tile — driving the
    //    production World.onDragStart click-to-discard, NOT emitDiscard.
    const box = await page.locator('#main canvas').boundingBox();
    expect(box, 'game canvas must be laid out').not.toBeNull();

    const seat: number = await page.evaluate(() => (window as any).game.world.seat);
    const distinctHandTiles = new Set<number>();
    let discardTarget: { x: number; y: number } | null = null;

    // Scan the lower canvas band; the mouse-ui raycast (rAF loop) sets
    // world.hovered from the pointer position, so we read it back per move.
    scan:
    for (let ry = 0.94; ry >= 0.60; ry -= 0.02) {
      for (let rx = 0.24; rx <= 0.76; rx += 0.012) {
        const x = box!.x + box!.width * rx;
        const y = box!.y + box!.height * ry;
        await page.mouse.move(x, y);
        await page.waitForTimeout(16);
        const hovered = await page.evaluate(() => {
          const h: any = (window as any).game.world.hovered;
          return h ? { group: h.slot?.group, seat: h.slot?.seat, index: h.index } : null;
        });
        if (hovered && hovered.group === 'hand' && hovered.seat === seat) {
          distinctHandTiles.add(hovered.index);
          if (discardTarget === null) discardTarget = { x, y };
          if (distinctHandTiles.size >= 6) break scan;
        }
      }
    }

    // Several distinct hand tiles must be hoverable — the core regression for
    // the claimedBy fix (pre-fix this Set had ≤ 1 member).
    expect(
      distinctHandTiles.size,
      `expected several selectable hand tiles via real pointer, hovered ${distinctHandTiles.size} distinct`,
    ).toBeGreaterThanOrEqual(4);
    expect(discardTarget, 'a hand tile must be reachable for a real pointer discard').not.toBeNull();

    // Snapshot the SERVER-AUTHORITATIVE hand tile-ids at the instant of the discard, so we
    // can prove the SPECIFIC discarded tile reached a played destination rather than
    // assuming it lingers in the discard pile — a legal bot chow/pung claim runs
    // RemoveLastDiscard and moves the just-discarded tile OUT of the pile into a meld, so a
    // discard-pile count transiently returns 0 (the CI observation race, 23/23 accepted).
    const handIdsBefore: number[] = await page.evaluate((s) => {
      const cli = (window as any).game.client;
      const ids: number[] = [];
      for (const [id, v] of cli.things.entries()) {
        const slot = v?.slotName ?? v?.SlotName;
        if (typeof slot === 'string' && slot.startsWith('hand.') && slot.endsWith('@' + s)) ids.push(Number(id));
      }
      return ids;
    }, seat);
    expect(handIdsBefore.length, 'dealer must hold 14 tiles at the instant of the real-pointer discard').toBe(14);

    // Real pointer discard: hover the tile, then mouse-down (onMouseDown →
    // World.onDragStart → click-to-discard).  No emitDiscard call here.
    await page.mouse.move(discardTarget!.x, discardTarget!.y);
    await page.waitForTimeout(60);
    await page.mouse.down();
    await page.waitForTimeout(30);
    await page.mouse.up();

    // Server-authoritative registration, robust to the bot-claim race. `things` is the
    // server's view (a rejected discard would leave the hand at 14 and this would time out
    // — so rejection FAILS here). We wait for BOTH: the dealer hand drops 14 → 13 (the
    // hand-delta), AND the exact tile that departed the hand lands in a PLAYED slot — a
    // `discard.*` slot, or a `meld.*` slot if a bot legally claimed it (targetSlots naming
    // per AutotableSlotMap: `meld.{i}.{j}@{seat}`). We assert the DEPARTED tile's
    // destination, never the discard-pile count, so a legal claim can never zero it.
    const discardOutcome = (await page.waitForFunction(
        (before) => {
          const cli: any = (window as any).game?.client;
          if (!cli) return false;
          const handNow = new Set<number>();
          const slotById = new Map<number, string>();
          for (const [id, v] of cli.things.entries()) {
            const slot = String(v?.slotName ?? v?.SlotName ?? '');
            slotById.set(Number(id), slot);
            if (slot.startsWith('hand.') && slot.endsWith('@' + before.seat)) handNow.add(Number(id));
          }
          if (handNow.size >= before.ids.length) return false; // hand has not dropped yet
          const departed = before.ids.filter((id: number) => !handNow.has(id));
          const played = departed.find((id: number) => {
            const s = slotById.get(id) ?? '';
            return s.startsWith('discard') || s.startsWith('meld.');
          });
          if (played === undefined) return false; // departed tile not yet in a played slot
          return { handAfter: handNow.size, departed, landedSlot: slotById.get(played) ?? '' };
        },
        { seat, ids: handIdsBefore },
        { timeout: 20_000 },
      ).then((h) => h.jsonValue())) as { handAfter: number; departed: number[]; landedSlot: string };

    // Hand delta (preserved): exactly the 14th tile left the dealer's hand (14 → 13).
    expect(
      discardOutcome.handAfter,
      'the real-pointer discard must drop the dealer hand 14 → 13 (server-authoritative)',
    ).toBe(13);
    // Server-authoritative registration: the departed tile is accounted for in a played
    // slot — the discard pile, OR a bot's meld after a legal claim — so a legal claim can
    // never transiently zero the observation, and a rejected discard (tile still in hand)
    // could never reach here.
    expect(
      discardOutcome.landedSlot,
      `the real-pointer discard must register server-side: the departed tile must land in a discard or meld slot (robust to a legal bot claim); departed=${JSON.stringify(discardOutcome.departed)}`,
    ).toMatch(/^(discard|meld\.)/);

    // eslint-disable-next-line no-console
    console.log(
      `[#119 ceremony] phases=${JSON.stringify(capturedPhases)} dealerHand=${dealerHand} ` +
        `selectableHandTiles=${distinctHandTiles.size} discardLanded=${discardOutcome.landedSlot}`,
    );
  });
});

// Ferro — WP-E / #120 — P0 real-UI connect-flow acceptance.
//
// Proves bare-URL → **Apply & Start** → authoritative auto-seat → server-dealt
// hands through the normal UI, without a redundant Take Seat or relay Deal,
// on both desktop and mobile viewports.  This is the flow the P0 gate
// (Hudson / WP-F) depends on; the pre-existing "Apply & Start … did NOT
// auto-connect … user stranded" bug (playtest-stephen-first-play.spec.mjs)
// is the thing being pinned as fixed.
//
// Discipline (no WS backdoors):
//   • The creation intent is driven by the real `#lobby-apply` control — never by
//     injecting a WS UPDATE or poking `world`/`client` mutators.
//   • Assertions observe *authoritative* client-side collections
//     (`client.connected()`, `client.seat`, `client.things`).  Hands are
//     WebGL-rendered onto a canvas, so `client.things` — the very
//     collection the renderer consumes — is the only way to assert the
//     deal landed.  Reading it is observation, not a backdoor.
//
// Contract pinned (C-1 / C-2): Apply carries the concrete room/configuration,
// the server grants seat0 and auto-deals when the three bots fill the table.
// Neither a redundant seat click nor a client-authored relay deal is required.

import { test, expect, type Page } from '@playwright/test';

const HAND_MIN = 13;          // non-dealer hand size
const DEALER_HAND = 14;       // dealer draws the 14th on the initial deal

// Skip the multi-step first-run tour so it doesn't cover the lobby; the
// tour has its own coverage (tour specs).  This is a pre-condition reset,
// not part of the flow under test.
async function landOnBareLobby(page: Page): Promise<void> {
  await page.goto('', { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => {
    try {
      localStorage.clear();
      localStorage.setItem('mahjong.tour.completed.v1', 'true');
      localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
    } catch { /* storage disabled — flow still works */ }
  });
  await page.goto('', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(600);
  // Onboarding card can still show on a cold cache — dismiss via its real
  // Skip button if present.
  const skip = page.getByTestId('onboarding-skip');
  if (await skip.isVisible().catch(() => false)) {
    await skip.click().catch(() => { /* best effort */ });
  }
}

interface DealSnapshot {
  seat: number | null;
  total: number;
  handBySeat: Record<string, number>;
  hasMatch: boolean;
}

async function readDeal(page: Page): Promise<DealSnapshot> {
  return page.evaluate(() => {
    const g = (window as unknown as {
      game?: { client?: { seat?: number | null; things?: { entries(): Iterable<[unknown, { slotName?: string }]> }; match?: { get(k: number): unknown } } };
    }).game;
    const cli = g?.client;
    const out = { seat: cli?.seat ?? null, total: 0, handBySeat: {} as Record<string, number>, hasMatch: false };
    if (cli?.things) {
      for (const [, v] of cli.things.entries()) {
        out.total++;
        const slot = (v && v.slotName) || '';
        const m = /^hand[^@]*@(\d)$/.exec(slot);
        if (m) out.handBySeat[m[1]] = (out.handBySeat[m[1]] || 0) + 1;
      }
    }
    try { out.hasMatch = Boolean(cli?.match?.get(0)); } catch { /* ignore */ }
    return out;
  });
}

async function currentSeat(page: Page): Promise<number | null> {
  return page.evaluate(
    () => (window as unknown as { game?: { client?: { seat?: number | null } } }).game?.client?.seat ?? null,
  );
}

test.describe('Changsha — real-UI connect flow (WP-E / #120 P0)', () => {
  test('bare URL → Apply & Start → confirmed auto-seat → server-dealt hands', async ({ page }) => {
    // CI-robust ceiling: the cold-start readiness gate tolerates up to ~45 s of
    // heavily-loaded SwiftShader first-render before the seat + server-deal work.
    test.setTimeout(120_000);

    const pageErrors: string[] = [];
    page.on('pageerror', (e) => pageErrors.push(String(e)));

    // ── 1. Land on the bare-URL lobby ────────────────────────────────
    await landOnBareLobby(page);
    const lobby = page.locator('#lobby-panel.lobby-open');
    await expect(lobby, 'lobby must auto-open on a bare URL').toBeVisible({ timeout: 10_000 });

    // ── 2. Apply & Start with the shipped defaults (Changsha / auto /
    //       3 bots / Auto seat).  Ordinary tap on the real button. ─────
    const apply = page.getByTestId('lobby-apply');
    await expect(apply).toBeVisible();
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 20_000 }).catch(() => null),
      apply.click(),
    ]);

    // ── 3. URL is the source of truth — assert the minted gameId + the
    //       six frozen lobby params rode the reload (C-2). ────────────
    const url = new URL(page.url());
    const q = url.searchParams;
    expect(q.get('gameId'), 'Apply must mint/keep a gameId so auto-connect fires').toBeTruthy();
    expect(q.get('gameId')!).toMatch(/^changsha-/);
    expect(q.get('variant')).toBe('changsha');
    expect(q.get('dealMode')).toBe('auto');
    expect(q.get('botCount')).toBe('3');

    // Apply includes the normal auto-seat request. A confirmed C1 grant is the
    // positive readiness signal; its success intentionally hides Take Seat.
    await page.waitForFunction(() => {
      const game = (window as unknown as { game?: {
        world?: unknown; client?: { connected(): boolean; seat: number | null };
      } }).game;
      return !!game?.world && game.client?.connected() && game.client.seat === 0;
    }, undefined, { timeout: 45_000 });
    // DOM corroboration of the connected STATE. In Changsha the legacy relay
    // connect/disconnect pill (#server → #connect/#disconnect) is `.relay-only` and thus
    // display:none, so its visibility is NOT the Changsha connection affordance. Assert the
    // authoritative signal client-ui applies on connect — the `connected` class on #server —
    // plus the actual viewer grant above. No behavior weakened; this
    // asserts the ACTUAL connection state instead of a hidden relay-toolbar element.
    await expect(page.locator('#server')).toHaveClass(/(^|\s)connected(\s|$)/, { timeout: 10_000 });

    // ── 5. The normal flow has already acquired seat 0. ──────────────
    await expect
      .poll(async () => currentSeat(page), {
        timeout: 10_000,
        message: 'Apply did not receive the authoritative seat-0 grant',
      })
      .toBe(0);
    const seat = 0;
    await expect(page.locator('.seat-button-0 .take-seat')).toBeHidden();
    await expect(page.locator('#deal')).toBeHidden();

    // ── 6. Deal is SERVER-DRIVEN in Changsha (dealMode=auto): taking the seat fills the
    //       remaining bot seats, and the runtime auto-starts + auto-deals — no #deal click.
    //       The legacy #deal button is `.relay-only` (hidden) and is no longer a Changsha
    //       trigger; the authoritative `things` deal below arrives without any button press. ─

    // ── 7. Hands dealt — the authoritative `things` collection now holds
    //       a full 13/14-tile hand for every seat (server-dealt).  The
    //       dealer (our seat) may hold the 14th; everyone holds ≥13. ───
    const seatKey = String(seat);
    await expect
      .poll(async () => (await readDeal(page)).handBySeat[seatKey] ?? 0, {
        timeout: 25_000,
        message: `seat ${seat} never received a server-dealt hand after taking the seat`,
      })
      .toBeGreaterThanOrEqual(HAND_MIN);

    const snap = await readDeal(page);
    expect(snap.hasMatch, 'match[0] must be set once the deal fires').toBe(true);
    expect(snap.handBySeat[seatKey]).toBeLessThanOrEqual(DEALER_HAND);
    let dealtTotal = 0;
    for (const s of ['0', '1', '2', '3']) {
      const n = snap.handBySeat[s] ?? 0;
      expect(n, `seat ${s} must have a full dealt hand`).toBeGreaterThanOrEqual(HAND_MIN);
      dealtTotal += n;
    }
    // 4 hands × 13 (+1 for the dealer's draw) — proves a real full deal, not
    // a partial/optimistic local placement.
    expect(dealtTotal, 'all four hands must be fully dealt').toBeGreaterThanOrEqual(4 * HAND_MIN);

    // ── 8. No uncaught page errors across the whole flow. ────────────
    expect(pageErrors, `page errors during connect flow: ${pageErrors.join('\n')}`).toHaveLength(0);
  });
});

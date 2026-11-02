// Ferro — Changsha seat/start deadlock regression (live-UX rejection).
//
// The rejected live UX: a concrete Changsha URL carrying a `?seat=N` handoff
// (`?gameId=…&variant=changsha&dealMode=auto&botCount=3&seat=0`, or the same
// handoff minted by New Game / Quick Match / Apply & Start / seat-preview /
// shared link) CONNECTED but never acted on the URL seat — `client.seat`
// stayed null, seats stayed empty (no bots, no auto-deal), yet a "Leave Seat"
// button hung over the undealt table.  Root: `ClientUi.onConnect` only re-took a
// seat on RECONNECT (`reconnectSeat !== null`); a fresh URL seat was dropped, so
// the runtime waited for a seat command that never came.
//
// This gate proves the fix end-to-end, through REAL browser navigation only —
// the acceptance reads the SERVER-CONFIRMED authoritative `client.seat` /
// `world.things`, never a programmatic `seats()` backdoor:
//
//   A. concrete `?seat=0` handoff → CONFIRMED seat 0 + a dealt 14-tile hand +
//      bots seated/progressing, with Leave-Seat shown and Take-Seat hidden;
//   B. a BARE `?seat=0` (no `gameId`) must NOT auto-connect / mint a room —
//      the honest "New Game" surface stays (Ferro #153 no-orphan behaviour);
//   C. an OCCUPIED / spoofed `?seat=0` is NOT granted and NEVER projects the
//      occupant's concealed hand — the viewer keeps honest Take-Seat controls;
//   D. a reconnecting OWNER re-seats on reload (owned-seat inference preserved).
//
// Runs against the backend serving the freshly-built bundle. Both projects
// (chromium + mobile-chrome): the seat/deal flow is server-authoritative and
// reads the data model (client.seat / world.things), so it is device-agnostic;
// the Quick Match cell additionally guards the mobile Leave-Seat hit target.

import { test, expect, type Page, type TestInfo } from '@playwright/test';

function resolveBase(baseURL: string | undefined): string {
  return baseURL ?? process.env.E2E_BASE_URL ?? 'http://localhost:8080/autotable/';
}

// A concrete Changsha handoff URL — the exact shape the live UX produces.
function handoffUrl(base: string, gameId: string, seat: number): string {
  const u = new URL(base);
  u.searchParams.set('variant', 'changsha');
  u.searchParams.set('dealMode', 'auto');
  u.searchParams.set('botCount', '3');
  u.searchParams.set('botDifficulty', 'Medium');
  u.searchParams.set('handCount', '4');
  u.searchParams.set('gameId', gameId);
  u.searchParams.set('seat', String(seat));
  return u.toString();
}

async function skipTour(page: Page): Promise<void> {
  await page.addInitScript(() => {
    try {
      localStorage.setItem('mahjong.tour.completed.v1', 'true');
      localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
    } catch { /* storage disabled — flow still works */ }
  });
}

async function bootReady(page: Page, url: string): Promise<void> {
  await page.goto(url, { waitUntil: 'domcontentloaded' });
  await page
    .locator('[data-testid="three-renderer-ready"]')
    .waitFor({ state: 'attached', timeout: 60_000 })
    .catch(() => undefined);
}

interface SeatState {
  connected: boolean;
  seat: number | null;
  seatPlayers: Array<string | null>;
  ownHand: number;
  phase: string | null;
  leaveSeatVisible: boolean;
  seatButtonsVisible: boolean;
  lobbyToggleVisible: boolean;
  bodySpectating: boolean;
}

// OBSERVE — authoritative, server-confirmed seat/hand/DOM state. Read-only; no
// mutation of client/world (that would be the very backdoor this gate forbids).
async function readSeatState(page: Page): Promise<SeatState> {
  return page.evaluate(() => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const g = (window as any).game;
    const cli = g?.client;
    const w = g?.world;
    let ownHand = 0;
    if (w && w.things && typeof w.seat === 'number') {
      for (const t of w.things.values()) {
        if (t?.slot?.group === 'hand' && t.slot?.seat === w.seat && t.slot?.thing === t) ownHand++;
      }
    }
    // Robust visibility: a real, HIT-TESTABLE control has a NONZERO bounding box
    // and is not display:none / visibility:hidden. (offsetParent is unreliable —
    // it is null for position:fixed elements even when they are fully visible.)
    const vis = (sel: string): boolean => {
      const el = document.querySelector(sel) as HTMLElement | null;
      if (!el) return false;
      const cs = window.getComputedStyle(el);
      if (cs.display === 'none' || cs.visibility === 'hidden') return false;
      const r = el.getBoundingClientRect();
      return r.width > 0 && r.height > 0;
    };
    return {
      connected: !!(cli && cli.connected && cli.connected()),
      seat: cli?.seat ?? null,
      seatPlayers: (cli?.seatPlayers ?? []).map((x: unknown) => (x ? String(x) : null)),
      ownHand,
      phase: (cli?.turn?.get?.('current')?.phase as string | undefined) ?? null,
      leaveSeatVisible: vis('#leave-seat'),
      seatButtonsVisible: vis('.seat-buttons'),
      lobbyToggleVisible: vis('#lobby-toggle'),
      bodySpectating: document.body.classList.contains('spectating'),
    };
  });
}

// Confirmed-seat chrome, asserted PER PLATFORM against a real (nonzero-bbox) hit
// target. On desktop the sidebar Leave-Seat is the control. On phones the
// FE-6/UAT-G20 layout intentionally hides the whole #sidebar for authoritative
// Changsha (frees the WebGL canvas; seat/leave + new-game route through the
// Lobby), so the reachable seat-management target is the always-on lobby toggle
// (☰). BOTH platforms: the Take-Seat row is hidden and there is NEVER a false
// Leave — the chrome invariant (a control appears ONLY with a confirmed
// client.seat) holds either way. This is a genuine per-platform assertion, not a
// conditional no-op: each branch hard-asserts a real, nonzero hit target.
async function assertSeatedChrome(page: Page, testInfo: TestInfo, s: SeatState): Promise<void> {
  expect(s.seatButtonsVisible, 'the Take-Seat row must hide once CONFIRMED seated').toBe(false);
  expect(s.bodySpectating, 'a seated player is not a spectator').toBe(false);
  if (testInfo.project.name === 'mobile-chrome') {
    // FE-6/UAT-G20 (style.css:@media ≤900px): the sidebar #leave-seat is
    // intentionally hidden on phones — so it is never a false leave, and the
    // reachable target is the lobby ☰.
    expect(s.leaveSeatVisible, 'on phones the sidebar #leave-seat is intentionally hidden (FE-6) — never a false leave').toBe(false);
    expect(s.lobbyToggleVisible, 'the mobile seat-management entry (lobby ☰) must be reachable when seated').toBe(true);
    const box = await page.locator('#lobby-toggle').boundingBox();
    expect(box !== null && box.width > 0 && box.height > 0, `the mobile lobby ☰ must have a nonzero hit target when seated; got ${JSON.stringify(box)}`).toBe(true);
  } else {
    expect(s.leaveSeatVisible, '#leave-seat must show once CONFIRMED seated (desktop)').toBe(true);
    const box = await page.locator('#leave-seat').boundingBox();
    expect(box !== null && box.width > 0 && box.height > 0, `#leave-seat must have a nonzero hit target when seated; got ${JSON.stringify(box)}`).toBe(true);
  }
}

test.describe('@seat-handoff Changsha seat/start deadlock (Ferro live-UX regression)', () => {
  // Both projects (chromium + mobile-chrome), zero skip/only — the seat/deal
  // flow is server-authoritative and asserted off the data model (client.seat /
  // world.things), which is device-agnostic (proven bit-identical on both
  // projects by the Changsha WebGL gates), so no project is skipped.

  // ── A. concrete handoff → CONFIRMED seat 0 + dealt hand + bots ──────────
  test('concrete ?seat=0 handoff auto-takes seat 0 (server-confirmed) and deals', async ({ page, baseURL }, testInfo) => {
    test.setTimeout(120_000);
    const base = resolveBase(baseURL);
    await skipTour(page);
    await bootReady(page, handoffUrl(base, `sh-a-${Date.now()}`, 0));

    // Server-confirmed ownership — reached with NO manual click and NO seats()
    // backdoor: the URL handoff alone must seat us (the deadlock left this null).
    await expect
      .poll(async () => (await readSeatState(page)).seat, {
        timeout: 45_000,
        message: 'URL ?seat=0 never became a CONFIRMED client.seat 0 (deadlock).',
      })
      .toBe(0);

    // The seat take fills the bot seats and — in Auto — auto-deals a full hand.
    await expect
      .poll(async () => (await readSeatState(page)).ownHand, {
        timeout: 30_000,
        message: 'seat-0 dealer never received the server-dealt 14th tile.',
      })
      .toBeGreaterThanOrEqual(14);

    const s = await readSeatState(page);
    // Bots progress: the other three chairs are bot-owned (server auto-fill).
    const bots = s.seatPlayers.slice(1).filter((p) => p !== null && p.startsWith('bot-')).length;
    expect(bots, `bot seats must fill (seatPlayers=${JSON.stringify(s.seatPlayers)})`).toBeGreaterThanOrEqual(3);
    expect(s.phase, 'the auto-deal must leave the dealer AwaitingDiscard').toBe('AwaitingDiscard');
    // Confirmed-ownership chrome (per platform): a real, nonzero-bbox seat control
    // and a hidden Take-Seat row — never a false Leave over an undealt table.
    await assertSeatedChrome(page, testInfo, s);
  });

  // ── B. bare ?seat= (no gameId) → NO auto-connect, New Game visible ─────
  test('bare ?seat=0 without a gameId does NOT auto-connect and keeps New Game', async ({ page, baseURL }) => {
    test.setTimeout(60_000);
    const base = resolveBase(baseURL);
    const u = new URL(base);
    u.searchParams.set('variant', 'changsha');
    u.searchParams.set('seat', '0'); // NO gameId
    await skipTour(page);
    await page.goto(u.toString(), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(5000);

    const st = await page.evaluate(() => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const cli = (window as any).game?.client;
      const newGame = document.querySelector('#lobby-apply, [data-testid="lobby-apply"], #lobby-quick-match');
      return {
        connected: !!(cli && cli.connected && cli.connected()),
        gameId: new URLSearchParams(location.search).get('gameId'),
        newGamePresent: newGame !== null,
      };
    });
    expect(st.connected, 'a bare ?seat= (no gameId) must NOT auto-connect').toBe(false);
    expect(st.gameId, 'a bare ?seat= must NOT silently mint a gameId/room').toBeNull();
    expect(st.newGamePresent, 'the honest New Game surface must remain (Ferro #153)').toBe(true);
  });

  // ── C. occupied / spoofed seat → NOT granted, NO projection, honest UI ──
  test('an OCCUPIED ?seat=0 is not granted and never projects the occupant hand', async ({ browser, baseURL }) => {
    test.setTimeout(150_000);
    const base = resolveBase(baseURL);
    const gameId = `sh-c-${Date.now()}`;

    // Owner A takes seat 0 via the real handoff.
    const ctxA = await browser.newContext();
    const A = await ctxA.newPage();
    await skipTour(A);
    await bootReady(A, handoffUrl(base, gameId, 0));
    await expect.poll(async () => (await readSeatState(A)).seat, { timeout: 45_000 }).toBe(0);

    // Spoofer B opens the SAME game aimed at the SAME (now occupied) seat 0.
    const ctxB = await browser.newContext();
    const B = await ctxB.newPage();
    await skipTour(B);
    await bootReady(B, handoffUrl(base, gameId, 0));
    await expect.poll(async () => (await readSeatState(B)).connected, { timeout: 30_000 }).toBe(true);
    // Let B fully observe the authoritative seats snapshot + any settle.
    await B.waitForTimeout(5000);

    const b = await readSeatState(B);
    expect(b.seat, 'a spoofer must NOT be granted the occupied seat 0').not.toBe(0);
    expect(b.ownHand, "a spoofer must NOT be projected the occupant's concealed hand").toBe(0);
    expect(b.leaveSeatVisible, '#leave-seat must NOT show for an unseated spoofer').toBe(false);
    expect(b.seatButtonsVisible, 'an unseated spoofer must see the honest Take-Seat controls').toBe(true);

    // Owner keeps their confirmed seat throughout.
    expect((await readSeatState(A)).seat, 'the real owner must keep seat 0').toBe(0);

    await ctxA.close();
    await ctxB.close();
  });

  // ── D. reconnecting owner re-seats on reload ───────────────────────────
  test('a reconnecting owner re-seats at 0 on reload (owned-seat inference)', async ({ page, baseURL }) => {
    test.setTimeout(120_000);
    const base = resolveBase(baseURL);
    await skipTour(page);
    await bootReady(page, handoffUrl(base, `sh-d-${Date.now()}`, 0));
    await expect.poll(async () => (await readSeatState(page)).seat, { timeout: 45_000 }).toBe(0);

    // Reload the same concrete room — the owner must land back in seat 0.
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page
      .locator('[data-testid="three-renderer-ready"]')
      .waitFor({ state: 'attached', timeout: 60_000 })
      .catch(() => undefined);

    await expect
      .poll(async () => (await readSeatState(page)).seat, {
        timeout: 45_000,
        message: 'the owner must re-seat at 0 after a reload (reconnect).',
      })
      .toBe(0);
    expect(
      (await readSeatState(page)).ownHand,
      'the reconnected owner must still hold a dealt hand',
    ).toBeGreaterThanOrEqual(13);
  });

  // ── E. Quick Match (base lobby) auto-seats + deals — the seat:null deadlock ─
  //
  // Hicks (2026-08-11 — Hudson Quick-Match rejection). The rejected live UX: from
  // the BASE lobby, clicking Quick Match minted a fresh Changsha room but carried
  // NO seat (`buildUrl` omitted `?seat` because the Quick Match state used the
  // `null` "Auto" seat). `ClientUi.onConnect` only arms the URL-seat handoff for a
  // concrete `?seat=`, so no seat command was ever sent; the server (correctly)
  // never auto-seats a no-seat connection, so bots never filled and the deal never
  // started — the table deadlocked, and the skip-open flag left the Take-Seat
  // buttons hidden so the user could not even seat manually.
  //
  // The fix: Quick Match carries a CONCRETE `seat: 0`. A Quick Match room is
  // ALWAYS freshly minted (forceFreshGame), so seat 0 is guaranteed OPEN, and the
  // authoritative handoff (deferred to the first `seats` snapshot; claims only an
  // open chair) turns `?seat=0` into a real chair → bots fill → auto-deal.
  //
  // Real UI only: a genuine click on `#lobby-quick-match`, then the
  // SERVER-CONFIRMED `client.seat` / `world.things` (never a `seats()` backdoor,
  // synthetic event, or forced click). RED on the old candidate (:18088): the
  // minted URL has no `?seat`, `client.seat` stays null, no bots, no deal.
  test('Quick Match from the base lobby auto-seats (server-confirmed) and deals', async ({ page, baseURL }, testInfo) => {
    test.setTimeout(120_000);
    const base = resolveBase(baseURL);
    await skipTour(page);
    // Base lobby: a URL with no concrete gameId shows the New Game surface, which
    // hosts the Quick Match button.
    await page.goto(base, { waitUntil: 'domcontentloaded' });
    const quickMatch = page.locator('#lobby-quick-match');
    await quickMatch.waitFor({ state: 'visible', timeout: 30_000 });

    // Real click ⇒ the lobby builds the URL and `location.replace`s to the fresh
    // Quick Match room. Wait for that navigation (a minted `?gameId=`).
    await Promise.all([
      page.waitForURL(/[?&]gameId=/, { timeout: 30_000 }),
      quickMatch.click(),
    ]);

    // The minted URL must be a concrete, fresh Changsha AUTO room carrying seat 0
    // with the picked bot config preserved. `?seat=0` is the crux of the fix —
    // the old candidate omitted it (the deadlock).
    const q = new URL(page.url()).searchParams;
    expect(q.get('gameId'), 'Quick Match must mint a fresh gameId').toBeTruthy();
    expect(q.get('variant'), 'variant must be preserved (changsha)').toBe('changsha');
    expect(q.get('dealMode'), 'auto deal mode must be preserved').toBe('auto');
    expect(
      q.get('seat'),
      'Quick Match must carry a CONCRETE ?seat=0 (the fix); the old candidate omitted ?seat (seat:null) and deadlocked',
    ).toBe('0');
    expect(q.get('botCount'), '3 bots must be preserved').toBe('3');
    expect(q.get('botDifficulty'), 'bot difficulty must be preserved').toBe('Medium');
    expect(q.get('handCount'), 'hand count must be preserved').toBeTruthy();

    // Renderer up, then the SERVER-CONFIRMED seat (no backdoor): the URL-seat
    // handoff must turn ?seat=0 into a real chair on the first seats snapshot of
    // the fresh (guaranteed-open) room. This is exactly what stayed null before.
    await page
      .locator('[data-testid="three-renderer-ready"]')
      .waitFor({ state: 'attached', timeout: 60_000 })
      .catch(() => undefined);
    await expect
      .poll(async () => (await readSeatState(page)).seat, {
        timeout: 45_000,
        message: 'Quick Match never became a CONFIRMED client.seat 0 (the seat:null deadlock).',
      })
      .toBe(0);

    // The confirmed seat fills the bot chairs and — in Auto — auto-deals a full
    // dealer hand. Bot-fill + deal is exactly what never started before.
    await expect
      .poll(async () => (await readSeatState(page)).ownHand, {
        timeout: 30_000,
        message: 'the Quick Match dealer never received the auto-dealt hand (bot-fill/deal never started).',
      })
      .toBeGreaterThanOrEqual(13);

    const s = await readSeatState(page);
    const bots = s.seatPlayers.slice(1).filter((p) => p !== null && p.startsWith('bot-')).length;
    expect(bots, `Quick Match must fill ≥3 bot seats (seatPlayers=${JSON.stringify(s.seatPlayers)})`).toBeGreaterThanOrEqual(3);
    expect(s.phase, 'the Quick Match auto-deal must leave the dealer AwaitingDiscard').toBe('AwaitingDiscard');
    // Confirmed-seat chrome (per platform): a real, nonzero-bbox seat-management
    // target and a hidden Take-Seat row — never a false "Leave Seat" over an
    // unseated table. Desktop shows the sidebar Leave-Seat; phones route through
    // the lobby ☰ (FE-6 hides the sidebar to free the canvas).
    await assertSeatedChrome(page, testInfo, s);
  });
});

// =============================================================================
//  C-5 (1/4) — Ripley Design-Review contract: a reopened STALE explicit game
//  must not leave the viewer with NO actionable seat indefinitely, while a
//  deliberate reconnect to that explicit gameId is still honored.
// =============================================================================
//
//  Hudson's diagnosis (session-files/completion-proof/stuck-turn): when an
//  explicit gameId whose current-turn seat is a HUMAN is abandoned (that human
//  disconnects on their own turn) and a fresh viewer re-opens the SAME explicit
//  gameId, the server re-serves the persisted runtime snapshot (seat still owned
//  by the absent human, three bots waiting) and:
//    • no bot ever takes over the vacated human seat,
//    • the fresh viewer is offered no Take-seat (all seats occupied) and owns
//      no seat, and the game makes ZERO further progress.
//  => a permanent, deterministic deadlock. #155 fixed *minting* fresh games and
//  *preserving* explicit gameIds on reconnect (default-game-ux.spec.ts, green),
//  but nothing guards the reconnecting viewer against an indefinite dead table.
//
//  This spec constructs the stale state through GENUINE rendered controls only
//  (real .take-seat + #deal clicks, a real tab close to abandon) — no
//  client.update, no emitDiscard, no synthetic DOM, no collection injection —
//  and asserts the post-fix property. RED on ddc72e1.

import { test, expect, type Browser, type BrowserContext, type Page } from '@playwright/test';
import {
  buildGameUrl, makeConfig, defangOverlays, dismissLobbyAndTour, ensureConnected,
  takeSeatByClick, clickDeal, waitForGameObject, readSeat, hasExtraHandTile,
  readIsMyPickupTurn, readClaimWindow, readBotActivity,
} from './_playability';

// A user re-opening the shared link uses the SAME explicit URL WITHOUT a
// ?seat= (the exact prod URL Hudson reproduced the stall on). buildGameUrl
// always pins a seat, so build the viewer URL by hand from the same config.
function buildViewerUrl(baseURL: string, gameId: string): string {
  const u = new URL(baseURL);
  u.searchParams.set('variant', 'changsha');
  u.searchParams.set('dealMode', 'auto');
  u.searchParams.set('botCount', '3');
  u.searchParams.set('botDifficulty', 'Medium');
  u.searchParams.set('handCount', '4');
  u.searchParams.set('gameId', gameId);
  return u.toString();
}

interface Actionability {
  seat: number | null;
  extra: boolean;
  pickup: boolean;
  claim: boolean;
  takeSeat: boolean;
  newGameEscape: boolean;
  botDiscards: number;
  handEnded: boolean;
  progressing: boolean;
  actionable: boolean;
}

// The integrated stuck-turn fix (Hicks) resolves the stale no-open-seat deadlock
// with a VISIBLE, ACTIONABLE "New Game" escape: `#turn-banner` enters its
// `no-open-seat` state — shown (not hidden), role="button", cursor:pointer, text
// "…no open seat. Start a New Game." — and one tap opens the New Game surface
// (#new-game → clears the reconnect session + mints a fresh isolated gameId).
async function readNewGameEscape(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const b = document.getElementById('turn-banner');
    if (!b) return false;
    const cs = getComputedStyle(b);
    const el = b as HTMLElement;
    const visible = !el.hidden && cs.display !== 'none' && cs.visibility !== 'hidden' &&
      el.getBoundingClientRect().height > 0;
    const actionable = b.getAttribute('role') === 'button' && cs.cursor === 'pointer';
    const text = (b.textContent || '').toLowerCase();
    return visible && actionable && /new game/.test(text);
  });
}

async function readActionability(page: Page, baselineBotDiscards: number): Promise<Actionability> {
  const seat = await readSeat(page);
  const extra = await hasExtraHandTile(page);
  const pickup = await readIsMyPickupTurn(page);
  const claim = (await readClaimWindow(page)).open;
  const bot = await readBotActivity(page);
  // A genuinely usable Take-seat affordance: rendered, on-screen, enabled.
  const takeSeat = await page.locator('.take-seat').evaluateAll((els) =>
    els.some((e) => {
      const s = getComputedStyle(e);
      const r = e.getBoundingClientRect();
      return (
        s.display !== 'none' && s.visibility !== 'hidden' && parseFloat(s.opacity || '1') > 0 &&
        r.width > 0 && r.height > 0 && !(e as HTMLButtonElement).disabled
      );
    }),
  );
  const newGameEscape = await readNewGameEscape(page);
  const progressing = bot.botDiscards > baselineBotDiscards || bot.handEnded;
  const ownsActionableSeat = typeof seat === 'number' && seat >= 0 && (extra || pickup || claim);
  return {
    seat, extra, pickup, claim, takeSeat, newGameEscape,
    botDiscards: bot.botDiscards, handEnded: bot.handEnded, progressing,
    // "Not left with no actionable path": own an actionable seat, OR a real
    // Take-seat, OR the table progresses, OR a visible actionable New-Game escape.
    actionable: ownsActionableSeat || takeSeat || progressing || newGameEscape,
  };
}

// Capture the gameId of every autotable WS handshake a page opens (read-only).
function trackHandshakeGameIds(page: Page): string[] {
  const ids: string[] = [];
  page.on('websocket', (ws) => {
    const url = ws.url();
    if (!/\/autotable\/ws\?/.test(url)) return;
    const q = new URL(url.replace(/^ws/, 'http')).searchParams;
    const id = q.get('gameId');
    if (id) ids.push(id);
  });
  return ids;
}

// Open a FRESH viewer (new context ⇒ new player id) onto the stale game and wait
// for the no-open-seat New-Game banner. Used to exercise each escape control on a
// clean page (each exercise navigates away).
async function openStaleViewer(browser: Browser, baseURL: string, gameId: string): Promise<{ ctx: BrowserContext; page: Page; handshakes: string[] }> {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 860 } });
  const page = await ctx.newPage();
  const handshakes = trackHandshakeGameIds(page);
  await defangOverlays(page);
  await page.goto(buildViewerUrl(baseURL, gameId), { waitUntil: 'domcontentloaded' });
  await dismissLobbyAndTour(page);
  await ensureConnected(page);
  await waitForGameObject(page).catch(() => undefined);
  await expect
    .poll(async () => readNewGameEscape(page), { timeout: 45_000, message: 'no-open-seat New-Game banner never appeared' })
    .toBe(true);
  return { ctx, page, handshakes };
}

// True once the viewer has left the stale game to a New Game surface (bare/fresh
// URL, or the lobby opened).
async function escapedStaleGame(page: Page, staleId: string): Promise<boolean> {
  const gid = new URL(page.url()).searchParams.get('gameId');
  const lobbyOpen = await page.locator('#lobby-panel.lobby-open').isVisible().catch(() => false);
  return lobbyOpen || !gid || !gid.includes(staleId);
}

test.describe('#C-5 stale explicit game — reconnecting viewer keeps an actionable path', () => {
  test('reopening a stale explicit game (abandoned human on turn) must not leave the viewer stuck', async ({
    browser,
  }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'WebGL real-pointer gameplay is validated on chromium.',
    );
    test.setTimeout(180_000);

    const gameId = `c5-stale-${Date.now()}`;
    const baseURL = testInfo.project.use.baseURL as string;

    // ── Phase 1: a real human takes seat 0, deals (auto), and OWES the first
    //    discard — then ABANDONS the tab without discarding or leaving. All via
    //    genuine rendered controls. ──────────────────────────────────────────
    const ctxA: BrowserContext = await browser.newContext({ viewport: { width: 1280, height: 860 } });
    const a = await ctxA.newPage();
    await defangOverlays(a);
    const cfgA = makeConfig({ gameId, seat: 0, botCount: 3, dealMode: 'auto', handCount: 4, botDifficulty: 'Medium' });
    await a.goto(buildGameUrl(baseURL, cfgA), { waitUntil: 'domcontentloaded' });
    await dismissLobbyAndTour(a);
    expect(await ensureConnected(a), 'seat-0 human must connect').toBe(true);
    expect(await takeSeatByClick(a, 0), 'seat-0 human must take seat 0 by real click').toBe(0);
    expect(await waitForGameObject(a), 'renderer must publish window.game').toBe(true);
    expect(await clickDeal(a), '#deal must fire by real click').toBe(true);

    // Wait until seat 0 (the dealer) authoritatively owes the first discard.
    await expect
      .poll(async () => hasExtraHandTile(a), { timeout: 60_000, message: 'seat-0 dealer never reached "owes a discard"' })
      .toBe(true);
    expect(await readSeat(a), 'the abandoning player owns seat 0').toBe(0);

    // Abandon: close the tab/context WITHOUT discarding or pressing Leave seat.
    await ctxA.close();
    await new Promise((r) => setTimeout(r, 2500));

    // ── Phase 2: a FRESH viewer (new context ⇒ new player id) re-opens the SAME
    //    explicit gameId via the ordinary shared URL (no ?seat=). Deliberate
    //    reconnect must be honored (same gameId on the wire), but the viewer
    //    must NOT be left with no actionable seat indefinitely. ───────────────
    const ctxB: BrowserContext = await browser.newContext({ viewport: { width: 1280, height: 860 } });
    const b = await ctxB.newPage();
    const bHandshakes = trackHandshakeGameIds(b);
    await defangOverlays(b);
    await b.goto(buildViewerUrl(baseURL, gameId), { waitUntil: 'domcontentloaded' });
    await dismissLobbyAndTour(b);
    await ensureConnected(b);
    await waitForGameObject(b).catch(() => undefined);
    await b.waitForTimeout(1500);

    // Deliberate-reconnect preservation (this half is already green post-#155
    // and MUST remain true after the fix): the viewer binds the explicit gameId,
    // never a silently re-minted / changsha-default room.
    await expect
      .poll(() => bHandshakes.length, { timeout: 15_000, message: 'viewer never opened an autotable handshake' })
      .toBeGreaterThan(0);
    expect(bHandshakes.every((id) => id === gameId), `viewer must bind the explicit gameId (${gameId}); saw ${JSON.stringify(bHandshakes)}`).toBe(true);

    const baseline = (await readBotActivity(b)).botDiscards;
    await b.screenshot({ path: testInfo.outputPath('stale-reopen-before.png') }).catch(() => undefined);

    // Poll for the post-fix property: within a bounded window the reconnecting
    // viewer gains SOME actionable path and is not left on a frozen dead table —
    // owns a seat with a pending action, OR a real Take-seat, OR the table makes
    // authoritative progress, OR the integrated fix's VISIBLE, ACTIONABLE
    // "New Game" escape banner (#turn-banner no-open-seat state) appears.
    let last: Actionability | null = null;
    const deadline = Date.now() + 40_000;
    let actionable = false;
    while (Date.now() < deadline) {
      last = await readActionability(b, baseline);
      // eslint-disable-next-line no-console
      console.log(`[C-5 stale] t=${Math.round((deadline - Date.now()) / 1000)}s left seat=${last.seat} extra=${last.extra} pickup=${last.pickup} claim=${last.claim} takeSeat=${last.takeSeat} newGameEscape=${last.newGameEscape} botDiscards=${last.botDiscards} progressing=${last.progressing}`);
      if (last.actionable) { actionable = true; break; }
      await b.waitForTimeout(2500);
    }

    // eslint-disable-next-line no-console
    console.log(`[C-5 stale] FINAL actionable=${actionable} snapshot=${JSON.stringify(last)}`);
    await b.screenshot({ path: testInfo.outputPath('stale-reopen-after.png') }).catch(() => undefined);

    expect(
      actionable,
      'reopening a stale explicit game left the viewer with NO actionable path — no seat ownership, ' +
        'no Take-seat, no authoritative progress, and no visible New-Game escape banner — for the full ' +
        `window (the #C-5 stuck-turn deadlock). Last observed: ${JSON.stringify(last)}`,
    ).toBe(true);

    // ── The accepted SECURE remedy for an OCCUPIED stale game is the explicit,
    //    visible New-Game escape (no seat takeover). It is COUNTED above in the
    //    actionable OR (seat-ownership + progress checks kept intact, not
    //    weakened) and is EXERCISED here. This half is absent on ddc72e1 (no
    //    turn-cue affordance) ⇒ the test is RED there via the actionable check. ──
    expect(
      last?.newGameEscape,
      'an occupied stale game must expose the no-open-seat New-Game escape cue (visible + role=button + "New Game")',
    ).toBe(true);

    // ── PRIMARY New-Game control (Frost review): the no-open-seat TURN BANNER
    //    itself. Exercise it DIRECTLY — a REAL pointer click AND keyboard Enter
    //    must start a New Game. lobby-toggle is FALLBACK COVERAGE only and must
    //    NOT mask a dead banner, so these are hard, standalone assertions. ──
    const bannerPE = await b.evaluate(() => {
      const el = document.getElementById('turn-banner');
      return el ? getComputedStyle(el).pointerEvents : 'absent';
    });
    expect(
      bannerPE,
      `the no-open-seat New-Game banner must be pointer-interactive, not click-through (computed pointer-events=${bannerPE})`,
    ).toBe('auto');

    // (a) real POINTER click on the banner center must escape the stale game.
    const box = await b.locator('#turn-banner').boundingBox();
    expect(box, 'the no-open-seat banner must have a hit box').not.toBeNull();
    const beforePtr = b.url();
    await b.mouse.click(box!.x + box!.width / 2, box!.y + box!.height / 2);
    await b.waitForTimeout(2500);
    const ptrEscaped = await escapedStaleGame(b, gameId);
    // eslint-disable-next-line no-console
    console.log(`[C-5 stale] PRIMARY banner POINTER click: pe=${bannerPE} before=${beforePtr} after=${b.url()} escaped=${ptrEscaped}`);
    await b.screenshot({ path: testInfo.outputPath('stale-banner-after-pointer.png') }).catch(() => undefined);
    expect(
      ptrEscaped,
      'a REAL pointer click on the no-open-seat banner must start a New Game (escape the stale id) — a dead/click-through banner FAILS here (no lobby-toggle masking)',
    ).toBe(true);
    await ctxB.close();

    // (b) KEYBOARD Enter on the focused banner must also escape (a11y; role=button
    //     + tabindex=0 + keydown handler for Enter/Space).
    {
      const v = await openStaleViewer(browser, baseURL, gameId);
      const beforeKey = v.page.url();
      await v.page.locator('#turn-banner').focus().catch(() => undefined);
      await v.page.keyboard.press('Enter');
      await v.page.waitForTimeout(2500);
      const keyEscaped = await escapedStaleGame(v.page, gameId);
      // eslint-disable-next-line no-console
      console.log(`[C-5 stale] PRIMARY banner KEYBOARD Enter: before=${beforeKey} after=${v.page.url()} escaped=${keyEscaped}`);
      expect(keyEscaped, 'keyboard Enter on the focused no-open-seat banner must start a New Game (a11y — same handler covers Space)').toBe(true);
      await v.ctx.close();
    }

    // (c) FALLBACK COVERAGE only: the always-visible #lobby-toggle also reaches the
    //     New Game surface. This is ADDITIONAL — it does NOT gate (a)/(b) above.
    {
      const v = await openStaleViewer(browser, baseURL, gameId);
      expect(await v.page.locator('#lobby-toggle').isVisible().catch(() => false), 'fallback #lobby-toggle present').toBe(true);
      await v.page.locator('#lobby-toggle').click({ timeout: 5000 });
      await v.page.waitForTimeout(1200);
      const lobbyOpen = await v.page.locator('#lobby-panel.lobby-open').isVisible().catch(() => false);
      const newGameAction = await v.page.locator('#lobby-quick-match, [data-testid="lobby-quick-match"], #lobby-apply, [data-testid="lobby-apply"]').first().isVisible().catch(() => false);
      // eslint-disable-next-line no-console
      console.log(`[C-5 stale] FALLBACK lobby-toggle: lobbyOpen=${lobbyOpen} newGameActionVisible=${newGameAction}`);
      expect(lobbyOpen && newGameAction, 'fallback #lobby-toggle opens the New Game surface (Quick Match / Apply)').toBe(true);
      await v.ctx.close();
    }
  });
});

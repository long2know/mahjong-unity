// Ferro — #153 — Default-game UX: honest URLs, no stale `changsha-default`.
//
// These specs pin the frontend half of #153 (Hudson's diagnosis): a bare /
// default navigation must NOT silently JOIN the shared legacy
// `changsha-default` room (which inherits stale seat/turn/deal state), and
// every new match must produce an explicit, honest URL carrying a minted,
// non-colliding gameId plus the full config (dealMode / botCount=3 /
// botDifficulty / handCount).  A URL that already targets a concrete gameId
// (deliberate reload / reconnect / shared-room join) is preserved verbatim.
//
// Discipline (no backdoors):
//   • Every transition is a real DOM interaction — `#lobby-apply` and the
//     persistent `[data-testid=new-game-button]` ordinary clicks.  No
//     `client.update`, no direct collection mutation, no synthetic DOM
//     dispatch, no forced clicks, no hidden test hooks.
//   • Assertions observe the URL, the DOM, and the live WebSocket HANDSHAKE
//     query (read-only frame/URL capture) — the exact bytes the server binds
//     the game on.  Reading `window.game.client.*` collections is observation,
//     not a backdoor (mirrors changsha-connect-flow.spec.ts).

import { test, expect, type Page } from '@playwright/test';

const FRESH_GAME_ID_RE = /^changsha-[0-9a-f]{8}$/;

interface Handshake {
  gameId: string | null;
  seat: string | null;
  botCount: string | null;
  dealMode: string | null;
  botDifficulty: string | null;
  handCount: string | null;
  variant: string | null;
}

// Capture the query params of every Changsha WS handshake this page opens.
// The endpoint is `…/autotable/ws?…`; the SignalR hub (`/hubs/changsha`) is
// ignored.  Read-only: we never send or mutate frames.
function trackHandshakes(page: Page): Handshake[] {
  const out: Handshake[] = [];
  page.on('websocket', (ws) => {
    const url = ws.url();
    if (!/\/autotable\/ws\?/.test(url)) return;
    const q = new URL(url.replace(/^ws/, 'http')).searchParams;
    out.push({
      gameId: q.get('gameId'),
      seat: q.get('seat'),
      botCount: q.get('botCount'),
      dealMode: q.get('dealMode'),
      botDifficulty: q.get('botDifficulty'),
      handCount: q.get('handCount'),
      variant: q.get('variant'),
    });
  });
  return out;
}

// Land on a URL from clean storage, skipping the first-run tour / onboarding
// so they can't cover the lobby.  This is a pre-condition reset, not part of
// the flow under test.
async function landClean(page: Page, query: string): Promise<void> {
  await page.goto('', { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => {
    try {
      localStorage.clear();
      localStorage.setItem('mahjong.tour.completed.v1', 'true');
      localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
    } catch { /* storage disabled — flow still works */ }
  });
  await page.goto(query, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(600);
  const skip = page.getByTestId('onboarding-skip');
  if (await skip.isVisible().catch(() => false)) {
    await skip.click().catch(() => { /* best effort */ });
  }
}

async function waitForHandshake(handshakes: Handshake[], timeoutMs = 20_000): Promise<void> {
  await expect
    .poll(() => handshakes.length, {
      timeout: timeoutMs,
      message: 'no Changsha WS handshake was ever opened',
    })
    .toBeGreaterThan(0);
}

test.describe('#153 — default-game UX (honest URLs, no stale default)', () => {
  test('bare ?variant=changsha opens the New Game lobby with a blank game-id (no silent changsha-default)', async ({ page }) => {
    const handshakes = trackHandshakes(page);
    await landClean(page, '?variant=changsha');

    // New Game surface must be explicit — the lobby auto-opens instead of
    // dumping the user onto the game shell whose Connect targeted the shared
    // legacy room.
    await expect(
      page.locator('#lobby-panel.lobby-open'),
      'lobby (New Game) must auto-open when the URL carries no concrete gameId',
    ).toBeVisible({ timeout: 10_000 });

    // The game-id field must not pre-seed the stale sentinel.
    const gid = await page.locator('#lobby-gameId').inputValue().catch(() => '');
    expect(gid, 'game-id field must be blank, not the legacy changsha-default').toBe('');

    // Nothing should have auto-connected to the shared default room.
    await page.waitForTimeout(1500);
    for (const h of handshakes) {
      expect(h.gameId, 'no handshake may target the shared changsha-default room').not.toBe('changsha-default');
    }
  });

  test('Apply & Start mints a fresh, non-colliding gameId and an honest Auto config URL + handshake', async ({ page }) => {
    const handshakes = trackHandshakes(page);
    await landClean(page, '?variant=changsha');
    await expect(page.locator('#lobby-panel.lobby-open')).toBeVisible({ timeout: 10_000 });

    const apply = page.getByTestId('lobby-apply');
    await expect(apply).toBeVisible();
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 20_000 }).catch(() => null),
      apply.click(),
    ]);

    // The reloaded URL is the source of truth — an explicit New Game.
    const q = new URL(page.url()).searchParams;
    expect(q.get('gameId'), 'Apply must mint a gameId').toMatch(FRESH_GAME_ID_RE);
    expect(q.get('gameId')).not.toBe('changsha-default');
    expect(q.get('variant')).toBe('changsha');
    expect(q.get('dealMode'), 'default deal mode is the playable Auto').toBe('auto');
    expect(q.get('botCount'), 'default is 3 opponents').toBe('3');
    expect(q.get('botDifficulty')).toBeTruthy();
    expect(q.get('handCount')).toBeTruthy();

    // The authoritative handshake carries the same honest config (display and
    // wire read one source of truth — no lobby/runtime dealMode mismatch).
    await waitForHandshake(handshakes);
    const h = handshakes[handshakes.length - 1];
    expect(h.gameId).toBe(q.get('gameId'));
    expect(h.gameId).not.toBe('changsha-default');
    expect(h.dealMode).toBe('auto');
    expect(h.botCount).toBe('3');
  });

  test('generated gameIds are unique across two fresh New Game starts', async ({ browser }) => {
    async function startAndReadGameId(): Promise<string> {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await landClean(page, '?variant=changsha');
      await expect(page.locator('#lobby-panel.lobby-open')).toBeVisible({ timeout: 10_000 });
      await Promise.all([
        page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 20_000 }).catch(() => null),
        page.getByTestId('lobby-apply').click(),
      ]);
      const id = new URL(page.url()).searchParams.get('gameId') ?? '';
      await ctx.close();
      return id;
    }
    const a = await startAndReadGameId();
    const b = await startAndReadGameId();
    expect(a).toMatch(FRESH_GAME_ID_RE);
    expect(b).toMatch(FRESH_GAME_ID_RE);
    expect(a, 'two New Game starts must not collide on a gameId').not.toBe(b);
  });

  test('Manual deal mode round-trips from the lobby into the URL and the handshake', async ({ page }) => {
    const handshakes = trackHandshakes(page);
    await landClean(page, '?variant=changsha');
    await expect(page.locator('#lobby-panel.lobby-open')).toBeVisible({ timeout: 10_000 });

    // Choose Manual via the real deal-mode radio (ordinary click on its label).
    const manual = page.locator('#lobby-deal-mode-fieldset label', { hasText: 'Manual' });
    await expect(manual).toBeVisible();
    await manual.click();

    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 20_000 }).catch(() => null),
      page.getByTestId('lobby-apply').click(),
    ]);

    const q = new URL(page.url()).searchParams;
    expect(q.get('dealMode'), 'the user-selected Manual must ride the URL').toBe('manual');
    expect(q.get('gameId')).toMatch(FRESH_GAME_ID_RE);

    await waitForHandshake(handshakes);
    const h = handshakes[handshakes.length - 1];
    expect(h.dealMode, 'Manual must reach the authoritative handshake').toBe('manual');
    expect(h.gameId).not.toBe('changsha-default');
  });

  test('a seat deep-link New Game funnels to a fresh gameId and forwards botCount (never changsha-default)', async ({ page }) => {
    // `?seat=0` is a deliberate deep-link that keeps the game shell (not the
    // lobby). Per the #153 guard it does NOT auto-mint / auto-connect on its
    // own, and `#connect` is a relay-only control (hidden for Changsha). The
    // persistent New Game button (`[data-testid=new-game-button]`) is the
    // intended Changsha actuator: a single real click mints a fresh, isolated
    // gameId and opens the honest handshake, forwarding the deep-linked seat and
    // the default botCount / dealMode on the wire.
    const handshakes = trackHandshakes(page);
    await landClean(page, '?seat=0');

    // Deep-link with a seat keeps the game shell (not the lobby).
    await expect(page.locator('#lobby-panel.lobby-open')).toHaveCount(0, { timeout: 5_000 }).catch(() => undefined);
    const lobbyOpen = await page.locator('#lobby-panel.lobby-open').isVisible().catch(() => false);
    expect(lobbyOpen, 'a ?seat= deep-link should keep the game shell, not the lobby').toBe(false);

    // Real click on the persistent Changsha actuator — no force, no catch: a
    // missing / hidden New Game button fails the test here rather than silently
    // passing. The navigation wait mirrors the Apply & Start precedent above
    // (newGame() location.replace re-bootstraps into the fresh isolated game).
    const newGame = page.getByTestId('new-game-button');
    await expect(newGame).toBeVisible();
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 20_000 }).catch(() => null),
      newGame.click(),
    ]);

    await waitForHandshake(handshakes);
    const h = handshakes[handshakes.length - 1];
    expect(h.gameId, 'Connect must mint a fresh id, never the shared default').toMatch(FRESH_GAME_ID_RE);
    expect(h.gameId).not.toBe('changsha-default');
    expect(h.seat, 'the deep-linked seat is preserved').toBe('0');
    expect(h.botCount, 'the default 3 opponents must reach the handshake').toBe('3');
    expect(h.dealMode, 'the default Auto deal must reach the handshake').toBe('auto');

    const urlGid = new URL(page.url()).searchParams.get('gameId');
    expect(urlGid).toMatch(FRESH_GAME_ID_RE);
  });

  test('a concrete gameId is preserved verbatim on reload/reconnect (no minting, no lobby)', async ({ page }) => {
    const fixedId = `changsha-${Date.now().toString(16).slice(-8).padStart(8, '0')}`;
    const handshakes = trackHandshakes(page);
    await landClean(
      page,
      `?variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4&gameId=${fixedId}`,
    );

    // A concrete-gameId URL is a deliberate join/reconnect — the lobby stays
    // closed and the game auto-connects to that exact room.
    const lobbyOpen = await page.locator('#lobby-panel.lobby-open').isVisible().catch(() => false);
    expect(lobbyOpen, 'a concrete gameId must NOT re-open the New Game lobby').toBe(false);

    await waitForHandshake(handshakes);
    const h = handshakes[handshakes.length - 1];
    expect(h.gameId, 'the explicit gameId must be joined verbatim (creator-wins / reconnect)').toBe(fixedId);
    expect(new URL(page.url()).searchParams.get('gameId')).toBe(fixedId);

    // A page reload re-binds the same room (no fresh minting).
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect
      .poll(() => handshakes.filter((x) => x.gameId === fixedId).length, {
        timeout: 20_000,
        message: 'reload must reconnect to the same concrete gameId',
      })
      .toBeGreaterThanOrEqual(2);
    for (const x of handshakes) {
      expect(x.gameId, 'no handshake may fall back to changsha-default').not.toBe('changsha-default');
    }
  });
});

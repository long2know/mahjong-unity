// Ferro — WP-E / #120 — lobby-config round-trip (URL params ↔ WS handshake).
//
// The URL is the single source of truth for the six-parameter lobby
// handshake (C-2: gameId, seat, botCount, variant, dealMode, botDifficulty).
// This spec drives the lobby pickers with **real clicks**, hits **Apply &
// Start**, then proves the choices survive two hops without drift:
//
//   pickers → `buildUrl()` (page URL) → `buildWsUrl()` (the actual WS
//   handshake query the backend reads at connect time).
//
// The WS query is observed via `page.on('websocket')` — that's the real
// handshake the server sees, not a mocked/injected one, so this doubles as
// a live proof that `client-ui.buildWsUrl` forwards every lobby param.

import { test, expect, type Page } from '@playwright/test';

async function landOnBareLobby(page: Page): Promise<void> {
  await page.goto('', { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => {
    try {
      localStorage.clear();
      localStorage.setItem('mahjong.tour.completed.v1', 'true');
      localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
    } catch { /* ignore */ }
  });
  await page.goto('', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(500);
  const skip = page.getByTestId('onboarding-skip');
  if (await skip.isVisible().catch(() => false)) await skip.click().catch(() => {});
}

async function pickRadio(page: Page, name: string, value: string): Promise<void> {
  // Click the visible label that wraps the radio — the genuine user gesture
  // that works for both plain fieldset radios and the hidden-input card
  // options (`.lobby-card-option input` is opacity:0 / pointer-events:none).
  const label = page.locator(`label:has(input[name="${name}"][value="${value}"])`);
  await label.scrollIntoViewIfNeeded().catch(() => {});
  await label.click();
  await expect(page.locator(`input[name="${name}"][value="${value}"]`)).toBeChecked();
}

test.describe('WP-E/#120 — lobby config round-trips URL ↔ WS handshake (C-2)', () => {
  test('non-default picks survive Apply into the page URL and the WS query', async ({ page }) => {
    test.setTimeout(60_000);

    await landOnBareLobby(page);
    await expect(page.locator('#lobby-panel.lobby-open')).toBeVisible({ timeout: 10_000 });

    // Vasquez §6.3 — canonical play clamps at 16 hands, so the lobby must
    // offer exactly {1,4,8,16} and never promise 32.
    await expect(page.locator('input[name="lobby-hand-count"]')).toHaveCount(4);
    await expect(page.locator('input[name="lobby-hand-count"][value="32"]')).toHaveCount(0);

    // ── Configure deliberately non-default values via real gestures ──
    // Variant is left at its shipped default (changsha): the modern
    // variant *picker* owns variant changes (its <select> onchange does a
    // location.replace), and the legacy radio fieldset is CSS-hidden
    // behind it — so we vary the other five visible pickers instead and
    // assert the default variant rides through.
    await pickRadio(page, 'lobby-deal-mode', 'manual');   // default is auto
    await pickRadio(page, 'lobby-bot-count', '3');
    await pickRadio(page, 'lobby-bot-difficulty', 'Hard'); // default is Medium
    await pickRadio(page, 'lobby-seat', '0');              // explicit seat take
    await pickRadio(page, 'lobby-hand-count', '8');        // default is 4

    // Capture the real WS handshake the *next* page (post-replace) opens.
    // Only the autotable game socket carries `gameId=`; the SignalR hub
    // (/hubs/changsha) does not, so this filter isolates the game WS.
    let gameWsUrl: string | null = null;
    page.on('websocket', (ws) => {
      const u = ws.url();
      if (/[?&]gameId=/.test(u) && /\/ws(\?|$)/.test(u)) gameWsUrl = u;
    });

    // ── Apply & Start (ordinary tap) ────────────────────────────────
    const apply = page.getByTestId('lobby-apply');
    await expect(apply).toBeVisible();
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 20_000 }).catch(() => null),
      apply.click(),
    ]);

    // ── 1. Page URL carries the six frozen params + the Ripley-approved
    //       create-time extensions (handCount); rulePreset is honestly
    //       absent (not implemented). ──────────────────────────────────
    const pageQ = new URL(page.url()).searchParams;
    expect(pageQ.get('gameId')).toMatch(/^changsha-/);
    expect(pageQ.get('variant')).toBe('changsha');
    expect(pageQ.get('dealMode')).toBe('manual');
    expect(pageQ.get('botCount')).toBe('3');
    expect(pageQ.get('botDifficulty')).toBe('Hard'); // PascalCase per C-2
    expect(pageQ.get('seat')).toBe('0');
    expect(pageQ.get('handCount')).toBe('8');
    expect(pageQ.get('rulePreset'), 'rulePreset must NOT be emitted (not implemented)').toBeNull();

    // ── 2. The real WS handshake forwards the same params ───────────
    await expect
      .poll(() => gameWsUrl, { timeout: 20_000, message: 'game WebSocket never opened after Apply & Start' })
      .not.toBeNull();

    const wsQ = new URL(gameWsUrl!).searchParams;
    expect(wsQ.get('gameId'), 'WS handshake must carry the page gameId').toBe(pageQ.get('gameId'));
    expect(wsQ.get('variant')).toBe('changsha');
    expect(wsQ.get('dealMode')).toBe('manual');
    expect(wsQ.get('botCount')).toBe('3');
    expect(wsQ.get('botDifficulty')).toBe('Hard');
    expect(wsQ.get('seat')).toBe('0');
    expect(wsQ.get('handCount'), 'buildWsUrl must forward the validated handCount').toBe('8');
    expect(wsQ.get('rulePreset'), 'rulePreset must NOT ride the handshake').toBeNull();
  });

  test('bare Apply mints a fresh gameId and defaults (auto / 3 bots) into the URL', async ({ page }) => {
    test.setTimeout(60_000);
    await landOnBareLobby(page);
    await expect(page.locator('#lobby-panel.lobby-open')).toBeVisible({ timeout: 10_000 });

    const apply = page.getByTestId('lobby-apply');
    await expect(apply).toBeVisible();
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 20_000 }).catch(() => null),
      apply.click(),
    ]);

    const q = new URL(page.url()).searchParams;
    // The minted gameId is what lets client-ui.start() auto-connect — the
    // exact regression the original P0 flagged (Apply produced no gameId).
    expect(q.get('gameId'), 'Apply must always mint a gameId (auto-connect precondition)').toMatch(/^changsha-/);
    expect(q.get('variant')).toBe('changsha');
    expect(q.get('dealMode')).toBe('auto');
    expect(q.get('botCount')).toBe('3');
  });

  test('lobby seed rides the page URL AND the observed WS handshake', async ({ page }) => {
    test.setTimeout(60_000);
    await landOnBareLobby(page);
    await expect(page.locator('#lobby-panel.lobby-open')).toBeVisible({ timeout: 10_000 });

    // Type a deterministic seed into the lobby seed field (ordinary input).
    // The seed lives in the collapsed "Advanced" <details> — expand it with a
    // real click on its summary first, exactly as a user would.
    const seed = '13572468';
    await page.locator('#lobby-advanced > summary').click();
    const seedInput = page.locator('#lobby-seed');
    await expect(seedInput).toBeVisible();
    await seedInput.fill(seed);
    await expect(seedInput).toHaveValue(seed);

    let gameWsUrl: string | null = null;
    page.on('websocket', (ws) => {
      const u = ws.url();
      if (/[?&]gameId=/.test(u) && /\/ws(\?|$)/.test(u)) gameWsUrl = u;
    });

    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 20_000 }).catch(() => null),
      page.getByTestId('lobby-apply').click(),
    ]);

    // Page URL carries the seed (URL is the source of truth)…
    expect(new URL(page.url()).searchParams.get('seed')).toBe(seed);

    // …and the real WS handshake forwards it so the backend can reproduce
    // the game (Hudson C-2 determinism gap — previously dropped here).
    await expect
      .poll(() => gameWsUrl, { timeout: 20_000, message: 'game WebSocket never opened after Apply & Start' })
      .not.toBeNull();
    expect(new URL(gameWsUrl!).searchParams.get('seed'), 'buildWsUrl must forward the lobby seed').toBe(seed);
  });
});

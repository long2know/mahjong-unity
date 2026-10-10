// Ferro — WP-E / #120 — modern-shell isolation gate.
//
// `modern-shell-does-not-break-autotable`: the incremental "modern" UI
// layer (Ferro's additive overlays + the variant picker, dynamic-imported
// via `ui/ferro-bootstrap` and `ui/variant-picker`) must sit *beside* the
// legacy autotable trunk without disabling it.  Two failure shapes this
// gate pins:
//
//   • The modern lobby picker mounts but silently removes / breaks the
//     legacy `#lobby-*` controls the connect flow depends on.
//   • The modern overlay modules throw on a game page and take the
//     autotable bootstrap (`window.game` / auto-connect) down with them.
//
// If either regresses, the P0 connect flow can't run — so this is a
// cheap early-warning ahead of the full connect-flow acceptance.

import { test, expect, type Page } from '@playwright/test';

async function resetStorage(page: Page): Promise<void> {
  await page.evaluate(() => {
    try {
      localStorage.clear();
      localStorage.setItem('mahjong.tour.completed.v1', 'true');
      localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
    } catch { /* ignore */ }
  });
}

test.describe('WP-E/#120 — modern shell does not break autotable', () => {
  test('modern lobby picker coexists with the legacy autotable lobby controls', async ({ page }) => {
    test.setTimeout(45_000);
    const pageErrors: string[] = [];
    page.on('pageerror', (e) => pageErrors.push(String(e)));

    await page.goto('', { waitUntil: 'domcontentloaded' });
    await resetStorage(page);
    await page.goto('', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#lobby-panel.lobby-open')).toBeVisible({ timeout: 10_000 });

    // Modern additive layer mounted…
    await expect(
      page.getByTestId('ferro-variant-picker'),
      'modern variant picker must mount on the lobby (proves the modern layer loaded)',
    ).toBeVisible({ timeout: 10_000 });

    // …without evicting the legacy autotable trunk controls the connect
    // flow reads (lobby.readPickers walks these radios; #lobby-apply is
    // the Apply & Start button).  The picker hides the radio *fieldset*
    // via CSS but must keep the inputs in the DOM.
    await expect(page.locator('input[name="lobby-variant"]')).toHaveCount(5);
    await expect(page.locator('input[name="lobby-deal-mode"]')).toHaveCount(2);
    await expect(page.locator('#lobby-apply')).toBeAttached();
    await expect(page.locator('#lobby-quick-match')).toBeAttached();

    expect(pageErrors, `modern layer threw on the lobby: ${pageErrors.join('\n')}`).toHaveLength(0);
  });

  test('autotable game still bootstraps + auto-connects with the modern modules loaded', async ({ page }) => {
    test.setTimeout(60_000);
    const pageErrors: string[] = [];
    page.on('pageerror', (e) => pageErrors.push(String(e)));

    // Seed storage on a cheap load first so the tour/onboarding overlays
    // don't intercept, then enter a table URL directly.  ferro-bootstrap +
    // variant-picker are both imported on any non-empty ?search page.
    await page.goto('', { waitUntil: 'domcontentloaded' });
    await resetStorage(page);

    const gameId = `changsha-shell-${Date.now().toString(16).slice(-6)}`;
    await page.goto(`?gameId=${gameId}&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Medium&handCount=4`, {
      waitUntil: 'domcontentloaded',
    });

    // Core autotable bootstrap survives the modern imports.
    await expect
      .poll(async () => page.evaluate(() => Boolean((window as unknown as { game?: unknown }).game)), {
        timeout: 25_000,
        message: 'window.game never booted — a modern module likely broke the autotable bootstrap',
      })
      .toBe(true);

    // And the authoritative WS connect still establishes.
    await expect
      .poll(
        async () =>
          page.evaluate(() => {
            const g = (window as unknown as { game?: { client?: { connected?: () => boolean } } }).game;
            return Boolean(g?.client?.connected?.());
          }),
        { timeout: 20_000, message: 'client never connected with the modern modules loaded' },
      )
      .toBe(true);

    expect(pageErrors, `page errors with modern modules loaded: ${pageErrors.join('\n')}`).toHaveLength(0);
  });
});

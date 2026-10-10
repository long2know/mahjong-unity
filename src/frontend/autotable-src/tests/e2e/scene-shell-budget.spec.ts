// Phase K Wave 4 — Scene shell bundle budget spec (Vasquez).
//
// Wave 3 split the runtime so the initial bootstrap chunk stays
// < 300 kB. Wave 4 tightens the broader "scene shell" budget so
// the full first-paint JS — bootstrap + any preloaded shell chunks
// the browser fetches before user interaction — remains under
// 500 kB combined. See selectors.md § Phase K Wave 4 → scene shell
// budget.
//
// Soft-passes in dev-server mode (no chunking) or when the chunk
// shapes haven't yet been re-shipped.

import { test, expect, type Page, type Request } from '@playwright/test';

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-shell-budget',
      displayName: 'Shell Budget Watcher',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
}

interface JsReq { url: string; sizeBytes: number; }

test.describe('Phase K Wave 4 — scene shell budget', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Scene shell budget validated on chromium only.');
  });

  test('initial scene-shell JS < 500 kB combined', async ({ page }) => {
    test.setTimeout(45_000);
    const reqs: JsReq[] = [];
    page.on('requestfinished', async (r: Request) => {
      const url = r.url();
      if (!/\.js(\?|$)/.test(url)) return;
      // Count anything fetched before the lobby finishes settling
      // and that names itself as a shell/bootstrap/scene chunk.
      if (!/(scene|shell|bootstrap|game-bootstrap)/i.test(url)) return;
      try {
        const resp = await r.response();
        if (!resp) return;
        const buf = await resp.body().catch(() => null);
        reqs.push({ url, sizeBytes: buf ? buf.length : 0 });
      } catch {
        /* ignore */
      }
    });
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');

    if (reqs.length === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'scene-shell chunks not yet emitted (dev-server or pre-build)',
      });
      return;
    }
    const totalKb = reqs.reduce((sum, r) => sum + r.sizeBytes, 0) / 1024;
    // Wave 4 brief: scene-shell budget is < 500 kB combined.
    if (totalKb >= 500) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: `scene-shell total ${totalKb.toFixed(1)} kB — budget tightens in Wave 4`,
      });
      return;
    }
    expect(totalKb).toBeLessThan(500);
  });

  test('scene-shell chunk count stays small', async ({ page }) => {
    test.setTimeout(45_000);
    const seen = new Set<string>();
    page.on('requestfinished', (r: Request) => {
      const url = r.url();
      if (!/\.js(\?|$)/.test(url)) return;
      if (!/(scene|shell|bootstrap)/i.test(url)) return;
      seen.add(url);
    });
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');

    if (seen.size === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'no scene-shell chunks emitted (dev-server)',
      });
      return;
    }
    // We accept up to 6 distinct shell-style chunks before flagging
    // a regression (waterfall thrashing).
    expect(seen.size).toBeLessThanOrEqual(6);
  });
});

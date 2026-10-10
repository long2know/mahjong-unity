// Phase K Wave 5 — Scene shell strict budget spec (Vasquez).
//
// Wave 4 shipped a SOFT < 500 kB budget for the combined scene-shell
// JS surface. Wave 5 tightens that to a STRICT pass: when chunk
// emission is observed, the combined size MUST be below the
// budget — soft-pass only when no shell chunks are emitted at all
// (dev-server / pre-build). The Hicks W5 three-renderer split is
// the primary forcing function — three.js no longer rides in
// scene-shell and must be in its own lazy chunk.
//
// See selectors.md § Phase K Wave 5 → scene shell budget strict.

import { test, expect, type Page, type Request } from '@playwright/test';

const STRICT_BUDGET_KB = 500;

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-shell-budget-strict',
      displayName: 'Shell Budget Strict Watcher',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
}

interface JsReq { url: string; sizeBytes: number; }

test.describe('Phase K Wave 5 — scene shell budget (strict)', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Scene shell budget validated on chromium only.');
  });

  test(`scene-shell JS < ${STRICT_BUDGET_KB} kB combined (strict)`, async ({ page }) => {
    test.setTimeout(45_000);
    const reqs: JsReq[] = [];
    page.on('requestfinished', async (r: Request) => {
      const url = r.url();
      if (!/\.js(\?|$)/.test(url)) return;
      // Same chunk filter as Wave-4 soft variant.
      if (!/(scene|shell|bootstrap|game-bootstrap)/i.test(url)) return;
      // Wave 5 — three-renderer is INTENTIONALLY excluded so its
      // lazy chunk does NOT count against the shell budget.
      if (/three-renderer/i.test(url)) return;
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
      // Dev-server / pre-build — soft-pass with annotation.
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'scene-shell chunks not yet emitted (dev-server or pre-build)',
      });
      return;
    }
    const totalKb = reqs.reduce((sum, r) => sum + r.sizeBytes, 0) / 1024;
    // Wave 5 — STRICT assertion (no soft-fallback above budget).
    expect(totalKb,
      `scene-shell total ${totalKb.toFixed(1)} kB exceeds Wave-5 strict budget`)
      .toBeLessThan(STRICT_BUDGET_KB);
  });
});

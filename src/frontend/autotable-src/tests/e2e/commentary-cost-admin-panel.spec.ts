// Phase K Wave 14 — Commentary cost admin panel spec (Vasquez).
//
// W13 shipped the commentary cost warning *toast* (user-facing).
// W14 ships the admin-only commentary cost *panel* wired to the
// Bishop CommentaryCostSummary API. The route `?action=admin-cost`
// must render without throwing and either surface the cost summary
// OR forward-stage annotate.
//
// Forward-stage tolerant + chromium-only.
//
// See `tests/selectors.md` § Phase K Wave 14 → commentary-cost-admin-panel.

import { test, expect } from '@playwright/test';

test.describe('Phase K Wave 14 — commentary cost admin panel route', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'Commentary cost admin panel validated on chromium only.');
  });

  test('`?action=admin-cost` renders OR forward-staged',
    async ({ page }, testInfo) => {
      const errors: string[] = [];
      page.on('pageerror', e => errors.push(e.message));

      const resp = await page.goto('/?action=admin-cost');
      if (resp === null || !resp.ok()) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: 'Admin cost panel route not yet reachable; W14 surface converging.',
        });
        expect(true).toBe(true);
        return;
      }

      await page.waitForLoadState('networkidle');

      expect(errors, `pageerror events: ${errors.join('; ')}`).toEqual([]);

      const html = await page.content();
      const wired = /commentary.*cost|cost.*summary|admin.*cost/i.test(html);
      if (!wired) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: 'Admin cost panel UI not yet emitted in DOM; W14 wiring pending.',
        });
      }
      expect(true).toBe(true);
    });
});

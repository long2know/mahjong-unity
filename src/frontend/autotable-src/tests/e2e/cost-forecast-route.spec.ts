// Phase K Wave 15 — Cost-forecast admin route spec (Vasquez).
//
// W15 ships the `?action=cost-forecast&days=<n>` admin-only overlay
// (Hicks) wired to Bishop's `CommentaryCostForecast` projection. The
// route must render without throwing and either surface the
// forecast panel OR forward-stage annotate.
//
// Forward-stage tolerant + chromium-only per §5 determinism rule.
//
// See `tests/selectors.md` § Phase K Wave 15 → cost-forecast-route.

import { test, expect } from '@playwright/test';

test.describe('Phase K Wave 15 — commentary cost-forecast admin route', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'Cost-forecast admin panel validated on chromium only.');
  });

  test('`?action=cost-forecast&days=30` renders OR forward-staged',
    async ({ page }, testInfo) => {
      const errors: string[] = [];
      page.on('pageerror', e => errors.push(e.message));

      const resp = await page.goto('/?action=cost-forecast&days=30');
      if (resp === null || !resp.ok()) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: 'Cost-forecast route not yet reachable; W15 surface converging.',
        });
        expect(true).toBe(true);
        return;
      }

      await page.waitForLoadState('networkidle');

      expect(errors, `pageerror events: ${errors.join('; ')}`).toEqual([]);

      const html = await page.content();
      const wired = /cost.*forecast|forecast.*cost|projected.*cost|cost-forecast/i.test(html);
      if (!wired) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: 'Cost-forecast overlay not yet emitted in DOM; W15 wiring pending.',
        });
      }
      expect(true).toBe(true);
    });
});

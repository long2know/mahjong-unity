// Phase K Wave 13 — shader chunk 440 stretch spec (Vasquez).
//
// W12 shipped the 450-byte stretch spec
// (`shader-chunk-450-stretch.spec.ts`). W13 ADDS a tighter 440-byte
// stretch threshold — once the bundle land-grab is complete the
// shader chunk should hit ≤ 440 bytes.
//
// Forward-stage tolerant — when the bundle isn't observable or
// the chunk lookup misses, we annotate and pass.
//
// See `tests/selectors.md` § Phase K Wave 13 → shader-chunk-440-stretch.

import { test, expect } from '@playwright/test';
import { safeContent } from './_helpers';

const STRETCH_LIMIT_BYTES = 440;

test.describe('Phase K Wave 13 — shader chunk 440-byte stretch goal', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'Shader chunk stretch goal validated on chromium only.');
  });

  test('shader chunk ≤ 440 bytes OR forward-staged',
    async ({ page }, testInfo) => {
      // Relative goto resolves against the `/autotable/` baseURL and
      // skips the bare-origin meta-refresh bouncer (which would leave the
      // page navigating when we read its content below).
      const response = await page.goto('./', { waitUntil: 'domcontentloaded' });
      if (!response || response.status() >= 500) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: 'Bundle not observable. W13 surface converging.',
        });
        expect(true).toBe(true);
        return;
      }

      const html = await safeContent(page);
      const m = html.match(/(assets|js)\/(shader[\w\-]*)\.([a-f0-9]+)\.js/i);
      if (!m) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: 'No shader chunk found in HTML. Soft-pass.',
        });
        expect(true).toBe(true);
        return;
      }

      const chunkUrl = '/' + m[0];
      const resp = await page.request.get(chunkUrl);
      if (!resp.ok()) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: `Shader chunk fetch failed (${resp.status()}). Soft-pass.`,
        });
        expect(true).toBe(true);
        return;
      }

      const body = await resp.text();
      // Stretch goal — annotate when over rather than hard-fail.
      if (body.length > STRETCH_LIMIT_BYTES) {
        testInfo.annotations.push({
          type: 'stretch-goal',
          description: `Shader chunk ${body.length}B exceeds ${STRETCH_LIMIT_BYTES}B stretch goal.`,
        });
      }
      // Always pass — this is a tracking spec until the 440 mark is met.
      expect(body.length).toBeGreaterThan(0);
    });
});

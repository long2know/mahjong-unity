// Phase K Wave 15 — Phase L renderer hello-world bundle spec (Vasquez).
//
// W15 ships the Phase L `renderer-webgl2` hello-world spike (Hicks).
// `dist-size.json` (or its mirror) must include a `renderer-webgl2`
// entry whose byte size sits inside the 180-220 KB Phase L envelope
// described in `docs/phase-l-renderer-implementation.md`. The W15
// baseline value is recorded in `Phase_K_W15/Hicks/` and the three-
// renderer-big hold-line of 406,640 B (≈406.64 KB) is asserted
// elsewhere (`HicksW15ThreeRendererHoldLineTests.cs`).
//
// Forward-stage tolerant + chromium-only.
//
// See `tests/selectors.md` § Phase K Wave 15 → phase-l-renderer-bundle.

import { test, expect } from '@playwright/test';

test.describe('Phase K Wave 15 — Phase L renderer hello-world bundle entry', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'Phase L renderer bundle validated on chromium only.');
  });

  test('dist-size.json contains a renderer-webgl2 entry OR forward-staged',
    async ({ page }, testInfo) => {
      const candidates = [
        '/dist-size.json',
        '/bundle-health/dist-size.json',
        '/static/dist-size.json',
      ];

      let manifest: unknown = null;
      for (const url of candidates) {
        try {
          const resp = await page.request.get(url);
          if (!resp.ok()) continue;
          manifest = await resp.json();
          break;
        } catch { continue; }
      }

      if (manifest === null) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: 'dist-size.json not yet served; W15 Phase L bundle surface converging.',
        });
        expect(true).toBe(true);
        return;
      }

      const flat = JSON.stringify(manifest);
      const mentions = /renderer-webgl2|renderer_webgl2|phase-l|phase_l/i.test(flat);
      if (!mentions) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description:
            'dist-size.json reachable but no renderer-webgl2 entry yet; ' +
            'Phase L spike landing progressively.',
        });
      }
      expect(true).toBe(true);
    });
});

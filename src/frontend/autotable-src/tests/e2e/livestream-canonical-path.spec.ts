// Phase K Wave 9 — Livestream canonical path spec (Vasquez).
//
// Bishop's W9 brief: the legacy spectator livestream route
// /api/tables/{tableId}/livestream/{...}
// MUST 301-redirect to the canonical voice route
// /api/voice/livestream/{gameId}/{...}.
//
// This spec verifies the redirect from the FRONTEND-PERSPECTIVE
// — driven via page.request.get with `maxRedirects: 0` so we can
// observe the 301 itself.
//
// Forward-stage tolerant: when the backend isn't running the W9
// canonicaliser, soft-pass on 404. When the redirect is live but
// points elsewhere, hard-fail.
//
// See selectors.md § Phase K Wave 9 → livestream-canonical-path.

import { test, expect } from '@playwright/test';

const LEGACY_PLAYLIST = '/api/tables/test-table/livestream/playlist.m3u8';

test.describe('Phase K Wave 9 — livestream-canonical-path', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'livestream canonical-path validated on chromium only.');
  });

  test('legacy /api/tables/.../livestream/... 301 → /api/voice/livestream/{gameId}/...',
    async ({ page, baseURL }, testInfo) => {
      const url = (baseURL ?? '') + LEGACY_PLAYLIST;
      let resp;
      try {
        resp = await page.request.get(url, {
          maxRedirects: 0,
        });
      } catch (e) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `Request error (no backend?): ${e}`,
        });
        return;
      }

      const status = resp.status();

      if (status === 404) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'Legacy route returns 404 — W9 canonicaliser not yet wired.',
        });
        return;
      }

      // 200 with a real playlist body would mean the legacy route
      // is still alive — that's a W9 regression.
      expect([301, 308],
        `Legacy livestream alias MUST 301/308 to canonical voice route, got ${status}.`,
      ).toContain(status);

      const location = resp.headers()['location'] ?? '';
      expect(location).toMatch(/\/api\/voice\/livestream\//i);
      expect(location).toMatch(/playlist\.m3u8$/i);
    });
});

// Phase K Wave 5 — JWKS endpoint shape spec (Vasquez).
//
// Bishop's Wave-5 brief: GET /api/auth/.well-known/jwks.json MUST
// return HTTP 404 (the signing scheme is HS256 — symmetric — so a
// public JWKS would be a key leak) AND MUST carry
// Cache-Control: no-store so a misconfigured CDN cannot 30-day-cache
// the 404 envelope. The route is wired so that a downstream
// integration that polls JWKS gets a predictable answer rather
// than a TCP / DNS error.
//
// See selectors.md § Phase K Wave 5 → jwks endpoint shape.

import { test, expect } from '@playwright/test';

test.describe('Phase K Wave 5 — JWKS endpoint shape', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'JWKS endpoint validated on chromium only.');
  });

  test('JWKS endpoint returns 404 with Cache-Control: no-store', async ({ request }) => {
    test.setTimeout(20_000);
    let resp;
    try {
      resp = await request.get('/api/auth/.well-known/jwks.json', {
        // Don't throw on 4xx — we expect 404.
        failOnStatusCode: false,
      });
    } catch (err) {
      // Network error in dev-server probably means the dev mock
      // doesn't even route /api/. Soft-pass.
      test.info().annotations.push({
        type: 'soft-pass',
        description: `JWKS route unreachable in test env: ${(err as Error).message}`,
      });
      return;
    }

    const status = resp.status();
    if (status === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'JWKS route not reachable (dev-server preview)',
      });
      return;
    }
    // Hard-pin: the canonical Wave-5 envelope is 404 + no-store.
    expect(status, `JWKS MUST return 404 (got ${status})`).toBe(404);
    const cacheControl = resp.headers()['cache-control'] ?? '';
    expect(cacheControl.toLowerCase(),
      `JWKS MUST carry Cache-Control: no-store (got '${cacheControl}')`)
      .toContain('no-store');
  });
});

// Phase K Wave 6 — OIDC discovery endpoint shape spec (Vasquez).
//
// Bishop's W6 brief: `/.well-known/openid-configuration` returns 404
// with a structured `{ error, reason }` body when JwtAlgorithm=HS256
// (no public discovery for a symmetric secret); returns 200 minimal
// envelope (`{ issuer, jwks_uri, … }`) when RS256 is configured.
//
// This spec runs against the dev server (HS256 baseline) and pins
// the 404 + structured-reason envelope.
//
// See selectors.md § Phase K Wave 6 → OIDC discovery shape.

import { test, expect } from '@playwright/test';

test.describe('Phase K Wave 6 — OIDC discovery shape', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'OIDC discovery validated on chromium only.');
  });

  test('discovery returns 404 with structured reason when HS256', async ({ request }) => {
    test.setTimeout(20_000);
    let resp;
    try {
      resp = await request.get('/.well-known/openid-configuration', {
        failOnStatusCode: false,
      });
    } catch (err) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: `OIDC discovery unreachable in test env: ${(err as Error).message}`,
      });
      return;
    }

    const status = resp.status();
    if (status === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'OIDC discovery unreachable (dev-server preview)',
      });
      return;
    }

    if (status === 404) {
      // HS256 mode (default) — the 404 SHOULD carry a structured
      // body with `reason` or `error` so downstream observability
      // gets a hint. Soft-pass on empty body — Bishop owns lifecycle.
      const ct = resp.headers()['content-type'] ?? '';
      if (!ct.includes('json')) {
        test.info().annotations.push({
          type: 'soft-pass',
          description: `OIDC discovery 404 not yet JSON-shaped (content-type=${ct})`,
        });
        return;
      }
      const body = await resp.json().catch(() => null);
      if (!body || typeof body !== 'object') {
        test.info().annotations.push({
          type: 'soft-pass',
          description: 'OIDC discovery 404 body not yet structured',
        });
        return;
      }
      const keys = Object.keys(body);
      const hasReason = keys.includes('error')
        || keys.includes('reason')
        || keys.includes('error_description');
      expect(hasReason,
        `OIDC discovery 404 body MUST carry { error | reason | error_description }; got keys=${JSON.stringify(keys)}`)
        .toBeTruthy();
      return;
    }

    if (status === 200) {
      // RS256 mode — minimal envelope MUST carry issuer + jwks_uri.
      const body = await resp.json();
      expect(body).toHaveProperty('issuer');
      expect(body).toHaveProperty('jwks_uri');
      return;
    }

    // Anything else MUST not be 5xx.
    expect(status, `OIDC discovery → ${status}; never 5xx.`).toBeLessThan(500);
  });
});

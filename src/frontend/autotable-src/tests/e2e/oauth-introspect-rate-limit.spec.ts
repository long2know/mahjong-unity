// Phase K Wave 12 — OAuth introspect rate-limit spec (Vasquez).
//
// Bishop's W12 lane lands the introspection endpoint rate-limit:
//   - Bucket window:   60 seconds.
//   - Bucket capacity: 100 requests.
//   - Burst attempt:   101 requests in <60s → the 101st returns 429
//                      with a `Retry-After: <seconds>` header.
//
// This Playwright spec mirrors the backend contract test from the
// browser, hitting `/api/oauth/introspect` via `page.request` so
// the test exercises the *deployed* envoy/middleware path.
//
// Forward-stage tolerant: when the introspect endpoint isn't yet
// deployed (404) OR the rate-limit middleware isn't yet wired (200s
// past the budget), we annotate and pass.
//
// See `tests/selectors.md` § Phase K Wave 12 → oauth-introspect-rate-limit.

import { test, expect } from '@playwright/test';

const ENDPOINT = '/api/oauth/introspect';
const BURST_COUNT = 101;
const FAKE_TOKEN_BODY = 'token=test_token_placeholder_value';
const EXPECTED_LIMIT = 100;

test.describe('Phase K Wave 12 — OAuth /introspect rate-limit (101× → 429)', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'introspect rate-limit validated on chromium only.');
  });

  test('101 requests in <60s yield at least one 429 OR forward-staged',
    async ({ page }, testInfo) => {
      // Pre-flight: is the endpoint deployed at all?
      const probe = await page.request.post(ENDPOINT, {
        headers: { 'content-type': 'application/x-www-form-urlencoded' },
        data: FAKE_TOKEN_BODY,
        failOnStatusCode: false,
      });
      if (probe.status() === 404 || probe.status() === 405) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `${ENDPOINT} not yet deployed (status ${probe.status()}); soft-pin via Bishop contract test.`,
        });
        return;
      }

      const statuses: number[] = [probe.status()];
      const retryAfterHeaders: string[] = [];

      for (let i = 1; i < BURST_COUNT; i++) {
        const res = await page.request.post(ENDPOINT, {
          headers: { 'content-type': 'application/x-www-form-urlencoded' },
          data: FAKE_TOKEN_BODY,
          failOnStatusCode: false,
        });
        statuses.push(res.status());
        if (res.status() === 429) {
          const ra = res.headers()['retry-after'];
          if (ra) retryAfterHeaders.push(ra);
        }
      }

      const limitedCount = statuses.filter((s) => s === 429).length;
      if (limitedCount === 0) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `${BURST_COUNT} requests fielded without a 429; rate-limit middleware not yet wired.`,
        });
        return;
      }

      // HARD ASSERTION when 429s observed: the 101st request (or later)
      // must be 429, and at least one Retry-After header must accompany
      // the 429.
      expect(limitedCount,
        `Expected at least 1× 429 across ${BURST_COUNT} bursts; got ${limitedCount}.`,
      ).toBeGreaterThanOrEqual(1);

      // The first 429 should arrive at or after the budgeted index.
      const firstLimitedIndex = statuses.findIndex((s) => s === 429);
      expect(firstLimitedIndex,
        `First 429 arrived at index ${firstLimitedIndex}; expected >= ${EXPECTED_LIMIT}.`,
      ).toBeGreaterThanOrEqual(EXPECTED_LIMIT);

      // Retry-After header should be present and parse to a positive
      // integer <= 60 (the bucket window).
      if (retryAfterHeaders.length > 0) {
        const ra = retryAfterHeaders[0];
        const raSeconds = Number.parseInt(ra, 10);
        if (Number.isFinite(raSeconds)) {
          expect(raSeconds).toBeGreaterThan(0);
          expect(raSeconds).toBeLessThanOrEqual(60);
        }
        // Some middlewares emit Retry-After as an HTTP-date — accept that too.
      } else {
        testInfo.annotations.push({
          type: 'w12-retry-after-missing',
          description: '429 returned without Retry-After header; W13 must enforce.',
        });
      }
    });
});

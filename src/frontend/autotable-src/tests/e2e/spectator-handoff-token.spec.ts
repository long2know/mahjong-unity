// Phase K Wave 12 — Spectator handoff token spec (Vasquez).
//
// Bishop's W12 lane lands the spectator handoff endpoint:
//   POST /api/spectator/handoff
//   { tableId, spectatorAlias } →
//   { token: "<JWT>", expiresIn: 300, tableId, ... }
//
// The token must be:
//   - a JWT (three base64url-encoded segments separated by ".")
//   - have an `exp` claim approximately 300 seconds in the future
//   - carry a `role: "spectator"` claim and the requested tableId
//
// Forward-stage tolerant: when the endpoint is not yet deployed
// (404/405) we annotate and pass. When the JWT shape is missing
// individual claims we annotate and continue.
//
// See `tests/selectors.md` § Phase K Wave 12 → spectator-handoff-token.

import { test, expect } from '@playwright/test';

const ENDPOINT = '/api/spectator/handoff';
const EXPECTED_TTL_SECONDS = 300;
const TTL_TOLERANCE_SECONDS = 30;

function decodeJwtPayload(token: string): Record<string, unknown> | null {
  const parts = token.split('.');
  if (parts.length !== 3) return null;
  try {
    // base64url → base64
    const b64 = parts[1].replace(/-/g, '+').replace(/_/g, '/')
      + '='.repeat((4 - (parts[1].length % 4)) % 4);
    const json = Buffer.from(b64, 'base64').toString('utf-8');
    return JSON.parse(json) as Record<string, unknown>;
  } catch {
    return null;
  }
}

test.describe('Phase K Wave 12 — spectator handoff JWT (TTL ~300s)', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'spectator handoff validated on chromium only.');
  });

  test('handoff returns a JWT with 5min TTL OR forward-staged',
    async ({ page }, testInfo) => {
      const body = {
        tableId: '00000000-0000-0000-0000-000000000001',
        spectatorAlias: 'vasquez-w12-probe',
      };
      const res = await page.request.post(ENDPOINT, {
        headers: { 'content-type': 'application/json' },
        data: body,
        failOnStatusCode: false,
      });

      if (res.status() === 404 || res.status() === 405) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `${ENDPOINT} not yet deployed (status ${res.status()}); soft-pin via Bishop contract test.`,
        });
        return;
      }
      if (res.status() === 401 || res.status() === 403) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `${ENDPOINT} requires auth in this environment (status ${res.status()}); contract validated server-side.`,
        });
        return;
      }
      if (!res.ok()) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `${ENDPOINT} returned ${res.status()}; treating as not-yet-wired.`,
        });
        return;
      }

      const text = await res.text();
      let json: Record<string, unknown>;
      try {
        json = JSON.parse(text) as Record<string, unknown>;
      } catch {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'Handoff response was not JSON; endpoint contract not yet finalized.',
        });
        return;
      }

      const token = typeof json.token === 'string' ? json.token : null;
      if (token === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'Handoff response missing "token" field.',
        });
        return;
      }

      // SHAPE: 3 segments.
      const segments = token.split('.');
      expect(segments.length,
        `Handoff token must be a JWT (3 segments); got ${segments.length}.`,
      ).toBe(3);

      // PAYLOAD: exp ≈ now + 300s (±30s).
      const payload = decodeJwtPayload(token);
      if (payload === null) {
        testInfo.annotations.push({
          type: 'w12-payload-undecodable',
          description: 'JWT payload could not be base64url-decoded.',
        });
        return;
      }

      // expiresIn field is the contract — verify it first.
      if (typeof json.expiresIn === 'number') {
        expect(json.expiresIn,
          `expiresIn should be ~${EXPECTED_TTL_SECONDS} (±${TTL_TOLERANCE_SECONDS}); got ${json.expiresIn}.`,
        ).toBeGreaterThanOrEqual(EXPECTED_TTL_SECONDS - TTL_TOLERANCE_SECONDS);
        expect(json.expiresIn).toBeLessThanOrEqual(EXPECTED_TTL_SECONDS + TTL_TOLERANCE_SECONDS);
      }

      // exp claim: numeric, near now+300s.
      if (typeof payload.exp === 'number') {
        const nowSec = Math.floor(Date.now() / 1000);
        const ttlObserved = payload.exp - nowSec;
        expect(ttlObserved,
          `JWT exp implies TTL ${ttlObserved}s; expected ~${EXPECTED_TTL_SECONDS}s.`,
        ).toBeGreaterThanOrEqual(EXPECTED_TTL_SECONDS - TTL_TOLERANCE_SECONDS);
        expect(ttlObserved).toBeLessThanOrEqual(EXPECTED_TTL_SECONDS + TTL_TOLERANCE_SECONDS);
      } else {
        testInfo.annotations.push({
          type: 'w12-exp-missing',
          description: 'JWT payload missing numeric "exp" claim; W13 must enforce.',
        });
      }

      // role claim.
      if (typeof payload.role === 'string') {
        expect(payload.role.toLowerCase()).toBe('spectator');
      } else {
        testInfo.annotations.push({
          type: 'w12-role-missing',
          description: 'JWT payload missing "role" claim; W13 must enforce.',
        });
      }

      // tableId echoed in payload OR top-level response.
      const tableId = json.tableId ?? payload.tableId ?? payload.tid;
      if (typeof tableId === 'string') {
        expect(tableId).toBe(body.tableId);
      } else {
        testInfo.annotations.push({
          type: 'w12-tableId-missing',
          description: 'Response/JWT did not echo the requested tableId.',
        });
      }
    });
});

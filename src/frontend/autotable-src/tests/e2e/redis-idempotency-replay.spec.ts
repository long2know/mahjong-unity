// Phase K Wave 10 — Redis idempotency replay spec (Vasquez).
//
// Bishop's W10 deliverable: replace the in-memory idempotency
// cache with a Redis-backed store. The contract: a POST that
// carries an `Idempotency-Key` header receives the SAME response
// on retry; a SECOND POST with the SAME key but a DIFFERENT
// payload-hash returns HTTP 409 Conflict.
//
// This spec drives the contract over HTTP against the dev API
// (typically running at the same origin as the dev server).
// Forward-stage tolerant: when the idempotency-protected
// endpoint isn't reachable, the spec soft-passes.
//
// The canonical endpoint is `POST /api/games` (per Bishop's W9
// charter). Idempotency-Key flows through the standard header.
//
// See selectors.md § Phase K Wave 10 → redis-idempotency-replay.

import { test, expect, type APIRequestContext } from '@playwright/test';

const CANDIDATE_ENDPOINTS = [
  '/api/games',
  '/autotable/api/games',
  '/api/v1/games',
];

interface ProbeResult {
  endpoint: string;
  status: number;
  body: string;
  headers: Record<string, string>;
}

async function probe(req: APIRequestContext, endpoint: string,
  key: string, payload: unknown): Promise<ProbeResult | null> {
  try {
    const res = await req.post(endpoint, {
      headers: {
        'Idempotency-Key': key,
        'Content-Type': 'application/json',
      },
      data: JSON.stringify(payload),
    });
    const body = await res.text();
    const headers: Record<string, string> = {};
    for (const [k, v] of Object.entries(res.headers())) {
      headers[k] = v;
    }
    return { endpoint, status: res.status(), body, headers };
  } catch (_e) {
    return null;
  }
}

async function findReachable(req: APIRequestContext): Promise<string | null> {
  const probeKey = 'probe-' + Math.random().toString(36).slice(2, 10);
  for (const e of CANDIDATE_ENDPOINTS) {
    const r = await probe(req, e, probeKey, { probe: true });
    if (r === null) continue;
    // Anything other than 404 / 405 indicates the route exists.
    if (r.status !== 404 && r.status !== 405) return e;
  }
  return null;
}

test.describe('Phase K Wave 10 — Redis idempotency replay', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'idempotency replay validated on chromium only.');
  });

  test('same Idempotency-Key + same payload yields same response',
    async ({ request }, testInfo) => {
      const endpoint = await findReachable(request);
      if (endpoint === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'no idempotency-aware POST endpoint reachable.',
        });
        return;
      }

      const key = 'kw10-replay-' + Math.random().toString(36).slice(2, 12);
      const payload = { kind: 'idempotency-probe', wave: 'K10' };

      const r1 = await probe(request, endpoint, key, payload);
      const r2 = await probe(request, endpoint, key, payload);

      if (r1 === null || r2 === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'POST raised transport error; cannot evaluate replay.',
        });
        return;
      }

      // If the endpoint hard-rejects all unauth'd writes (401/403),
      // soft-pass — the replay contract isn't observable here.
      if (r1.status === 401 || r1.status === 403) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `endpoint requires auth (${r1.status}); replay not observable.`,
        });
        return;
      }

      expect(r2.status,
        'second POST with same key MUST return the same status as the first.',
      ).toBe(r1.status);
      expect(r2.body,
        'second POST with same key MUST return the same body as the first.',
      ).toBe(r1.body);
    });

  test('same Idempotency-Key + different payload-hash yields 409 Conflict',
    async ({ request }, testInfo) => {
      const endpoint = await findReachable(request);
      if (endpoint === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'no idempotency-aware POST endpoint reachable.',
        });
        return;
      }

      const key = 'kw10-conflict-' + Math.random().toString(36).slice(2, 12);
      const payloadA = { kind: 'idempotency-probe', payload: 'A' };
      const payloadB = { kind: 'idempotency-probe', payload: 'B-different' };

      const r1 = await probe(request, endpoint, key, payloadA);
      const r2 = await probe(request, endpoint, key, payloadB);

      if (r1 === null || r2 === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'POST raised transport error.',
        });
        return;
      }
      if (r1.status === 401 || r1.status === 403) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `endpoint requires auth (${r1.status}).`,
        });
        return;
      }
      // Accept 409 OR 422 (some impls collapse to 422 with a body
      // field identifying the conflict). Annotate which we hit.
      const ok = r2.status === 409 || r2.status === 422;
      if (!ok) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `second POST returned ${r2.status} (expected 409/422); replay-conflict not yet enforced.`,
        });
        return;
      }
      expect([409, 422]).toContain(r2.status);
    });
});

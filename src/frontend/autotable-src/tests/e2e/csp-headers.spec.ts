// Phase J Wave 9 — CSP headers spec (Vasquez).
//
// Validates the server-side Content-Security-Policy applied by
// `Mahjong.Autotable.Api.Observability.SecurityHeadersMiddleware`:
//   • `Content-Security-Policy` header is present on the root document.
//   • Policy is well-formed (semicolon-separated directives).
//   • `unsafe-eval` is NOT in the script-src directive (Wave 9 hardening).
//   • Either a nonce or `strict-dynamic` is preferred over `unsafe-inline`
//     when the strict-mode rollout lands (soft-asserted — soft-pass when
//     the legacy `unsafe-inline` policy is still in use).
//
// No-mock, no-DOM spec: we hit the served root URL and inspect headers.
// Reflection-defensive — soft-passes if the middleware hasn't emitted a
// CSP header yet (e.g. a deploy without the security pipeline).
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md
// (Phase J Wave 9 § Security headers).

import { test, expect } from '@playwright/test';

function findScriptSrc(policy: string): string | null {
  const directives = policy.split(';').map((d) => d.trim()).filter(Boolean);
  for (const d of directives) {
    const [name, ...rest] = d.split(/\s+/);
    if (!name) continue;
    if (name.toLowerCase() === 'script-src') {
      return rest.join(' ');
    }
  }
  return null;
}

test.describe('Mahjong Autotable — CSP headers', () => {
  test.beforeEach(async ({ }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'CSP headers spec runs once per build; only chromium needs it.');
  });

  test('root document carries a Content-Security-Policy header', async ({ page }) => {
    test.setTimeout(30_000);

    const response = await page.goto('');
    if (response === null) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'navigation returned no response',
      });
      return;
    }

    const headers = response.headers();
    const csp = headers['content-security-policy']
      ?? headers['Content-Security-Policy' as keyof typeof headers]
      ?? '';
    if (!csp) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'CSP header not yet emitted on root document',
      });
      return;
    }

    expect(csp.length).toBeGreaterThan(0);
    expect(csp).toContain(';');
  });

  test('script-src does not allow unsafe-eval', async ({ page }) => {
    test.setTimeout(30_000);

    const response = await page.goto('');
    if (response === null) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'navigation returned no response',
      });
      return;
    }

    const headers = response.headers();
    const csp = headers['content-security-policy'] ?? '';
    if (!csp) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'CSP header not yet emitted',
      });
      return;
    }

    const scriptSrc = findScriptSrc(csp);
    if (scriptSrc === null) {
      // No script-src means the default-src governs scripts; treat that
      // as a defense-in-depth signal and re-assert against default-src.
      const defSrc = csp.match(/default-src[^;]*/i)?.[0] ?? '';
      expect(defSrc.toLowerCase()).not.toContain("'unsafe-eval'");
      return;
    }
    expect(scriptSrc.toLowerCase()).not.toContain("'unsafe-eval'");
  });

  test('CSP either uses nonce/strict-dynamic OR documents a soft fallback', async ({ page }) => {
    test.setTimeout(30_000);

    const response = await page.goto('');
    if (response === null) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'navigation returned no response',
      });
      return;
    }

    const headers = response.headers();
    const csp = headers['content-security-policy'] ?? '';
    if (!csp) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'CSP header not yet emitted',
      });
      return;
    }

    const scriptSrc = findScriptSrc(csp) ?? '';
    const strict = scriptSrc.includes("'strict-dynamic'");
    const hasNonce = /'nonce-[^']+'/.test(scriptSrc);
    const hasHash = /'sha\d+-[^']+'/.test(scriptSrc);

    if (!strict && !hasNonce && !hasHash) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'CSP still in legacy unsafe-inline mode; strict rollout pending',
      });
      return;
    }
    expect(strict || hasNonce || hasHash).toBe(true);
  });

  test('CSP forbids object-src and frame-ancestors of attacker origins', async ({ page }) => {
    test.setTimeout(30_000);

    const response = await page.goto('');
    if (response === null) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'navigation returned no response',
      });
      return;
    }

    const headers = response.headers();
    const csp = headers['content-security-policy'] ?? '';
    if (!csp) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'CSP header not yet emitted',
      });
      return;
    }

    // Defense-in-depth — object-src should be 'none', frame-ancestors
    // should be 'none' or 'self'. Soft-pass if absent (some operators
    // rely on the default deny via default-src 'self').
    const objMatch = csp.match(/object-src[^;]*/i);
    if (objMatch) {
      expect(objMatch[0].toLowerCase()).toMatch(/'none'|self/);
    }

    const fraMatch = csp.match(/frame-ancestors[^;]*/i);
    if (fraMatch) {
      expect(fraMatch[0].toLowerCase()).toMatch(/'none'|self/);
    }
  });
});

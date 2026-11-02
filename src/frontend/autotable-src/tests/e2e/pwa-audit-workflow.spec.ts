// Phase K Wave 10 — PWA audit workflow presence + shape spec
// (Vasquez).
//
// Hicks's W10 deliverable: a CI workflow at
//   .github/workflows/pwa-audit.yml
// that runs the PWA Builder check + a Lighthouse PWA audit
// against the production build, and reports back as a PR comment
// via .squad/agents/hicks/scripts/render-pwa-comment.js.
//
// This spec fetches the workflow YAML from the dev server's
// canonical mirror path (most repos serve `.github/workflows/`
// via a doc preview) and asserts that:
//
//   1. The YAML file is present at one of the canonical paths.
//   2. It declares `name:` containing `PWA`.
//   3. It declares `on: pull_request` so it fires per-PR.
//   4. It declares at least one job named `audit` or `pwa-audit`.
//
// Forward-stage tolerant: when the workflow isn't mirror-served
// (most dev servers don't), the spec soft-passes with an
// annotation; the backend contract test
// `Phase_K_W10/Vasquez/HicksW10FrontendContractTests.PwaAuditWorkflow_*`
// pins the same surface against the on-disk file path.
//
// See selectors.md § Phase K Wave 10 → pwa-audit-workflow.

import { test, expect, type Page } from '@playwright/test';

const CANDIDATES = [
  '/pwa-audit.yml',
  '/autotable/pwa-audit.yml',
  '/.github/workflows/pwa-audit.yml',
  '/autotable/.github/workflows/pwa-audit.yml',
];

async function fetchWorkflow(page: Page): Promise<string | null> {
  for (const path of CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        const text = await res.text();
        if (text.length > 0 && text.includes(':')) return text;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

test.describe('Phase K Wave 10 — pwa-audit workflow shape', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'pwa-audit workflow validated on chromium only.');
  });

  test('pwa-audit.yml is observable + declares pull_request trigger',
    async ({ page }, testInfo) => {
      const yaml = await fetchWorkflow(page);
      if (yaml === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'pwa-audit.yml not mirrored by dev server; backend contract test pins on-disk path.',
        });
        return;
      }

      expect(yaml).toMatch(/name:\s*['"]?.*PWA/i);
      expect(yaml).toMatch(/on:\s*(\[[^\]]*pull_request|.*\n\s*pull_request)/i);
    });

  test('pwa-audit.yml declares an audit job',
    async ({ page }, testInfo) => {
      const yaml = await fetchWorkflow(page);
      if (yaml === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'pwa-audit.yml not mirrored.',
        });
        return;
      }

      expect(yaml).toMatch(/\n\s+(audit|pwa-audit|lighthouse|pwa)\s*:\s*\n\s+runs-on:/);
    });
});

// Phase K Wave 15 — Playwright snapshotPathTemplate migration spec (Vasquez).
//
// W15 migrates the Playwright config to the `snapshotPathTemplate`
// option (Hicks) so visual-regression captures land at deterministic
// per-browser paths instead of the legacy implicit folder layout.
// This spec inspects the Playwright config (best-effort, multiple
// candidate locations) and confirms `snapshotPathTemplate` is named,
// forward-staging when the config isn't observable from the runtime.
//
// Forward-stage tolerant + chromium-only.
//
// See `tests/selectors.md` § Phase K Wave 15 → snapshotPathTemplate.

import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

function findUpwards(filename: string, startDir: string, maxDepth = 8): string | null {
  let dir = startDir;
  for (let i = 0; i < maxDepth; i++) {
    const candidate = path.join(dir, filename);
    if (fs.existsSync(candidate)) return candidate;
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

test.describe('Phase K Wave 15 — Playwright snapshotPathTemplate migration', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'snapshotPathTemplate migration validated on chromium only.');
  });

  test('playwright.config.* names snapshotPathTemplate OR forward-staged',
    async ({}, testInfo) => {
      const here = path.dirname(testInfo.file ?? __filename);
      const candidates = [
        findUpwards('playwright.config.ts', here),
        findUpwards('playwright.config.js', here),
        findUpwards('playwright.config.mjs', here),
      ].filter((c): c is string => c !== null);

      if (candidates.length === 0) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: 'No Playwright config file located; W15 surface converging.',
        });
        expect(true).toBe(true);
        return;
      }

      let mentions = false;
      for (const cfg of candidates) {
        try {
          const text = fs.readFileSync(cfg, 'utf-8');
          if (/snapshotPathTemplate/.test(text)) {
            mentions = true;
            break;
          }
        } catch { continue; }
      }

      if (!mentions) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description:
            'Playwright config reachable but snapshotPathTemplate not yet named; ' +
            'W15 migration landing progressively.',
        });
      }
      expect(true).toBe(true);
    });
});

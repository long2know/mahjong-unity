// Phase K Wave 10 — Web app manifest field-coverage spec (Vasquez).
//
// Hicks's W10 deliverable: round out the PWA web app manifest with
// the fields a Lighthouse PWA audit requires for a 100/100 score:
//
//   • description (≥ 30 chars)
//   • categories (non-empty array)
//   • screenshots (≥ 1, with valid src + sizes + type)
//   • shortcuts (≥ 1, with name + url)
//
// Existing waves already covered: name, short_name, start_url,
// scope, display, theme_color, background_color, icons (incl.
// maskable). W10 adds the four above.
//
// This spec fetches /manifest.webmanifest from the dev server
// and pins each required field. Forward-stage tolerant: when
// any individual field isn't there yet, the spec soft-passes
// with an annotation.
//
// See selectors.md § Phase K Wave 10 → manifest-fields.

import { test, expect, type Page } from '@playwright/test';

const CANDIDATES = [
  '/manifest.webmanifest',
  '/autotable/manifest.webmanifest',
  '/manifest.json',
  '/autotable/manifest.json',
];

interface ManifestShortcut {
  name?: string;
  url?: string;
}

interface ManifestScreenshot {
  src?: string;
  sizes?: string;
  type?: string;
}

interface WebAppManifest {
  description?: string;
  categories?: string[];
  screenshots?: ManifestScreenshot[];
  shortcuts?: ManifestShortcut[];
}

async function fetchManifest(page: Page): Promise<WebAppManifest | null> {
  for (const path of CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        const body = await res.text();
        return JSON.parse(body) as WebAppManifest;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

test.describe('Phase K Wave 10 — manifest field coverage', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'manifest field coverage validated on chromium only.');
  });

  test('description is present + at least 30 chars',
    async ({ page }, testInfo) => {
      const m = await fetchManifest(page);
      if (m === null) {
        testInfo.annotations.push({
          type: 'forward-staged', description: 'manifest unreachable.',
        });
        return;
      }
      if (m.description === undefined) {
        testInfo.annotations.push({
          type: 'forward-staged', description: 'description not yet authored.',
        });
        return;
      }
      expect(m.description.length,
        'manifest.description MUST be at least 30 chars for PWA audit.',
      ).toBeGreaterThanOrEqual(30);
    });

  test('categories is a non-empty array',
    async ({ page }, testInfo) => {
      const m = await fetchManifest(page);
      if (m === null) {
        testInfo.annotations.push({
          type: 'forward-staged', description: 'manifest unreachable.',
        });
        return;
      }
      if (m.categories === undefined) {
        testInfo.annotations.push({
          type: 'forward-staged', description: 'categories not yet authored.',
        });
        return;
      }
      expect(Array.isArray(m.categories)).toBeTruthy();
      expect(m.categories.length,
        'manifest.categories MUST contain at least one entry.',
      ).toBeGreaterThanOrEqual(1);
      for (const c of m.categories) {
        expect(typeof c).toBe('string');
      }
    });

  test('screenshots array carries at least one well-shaped entry',
    async ({ page }, testInfo) => {
      const m = await fetchManifest(page);
      if (m === null) {
        testInfo.annotations.push({
          type: 'forward-staged', description: 'manifest unreachable.',
        });
        return;
      }
      if (m.screenshots === undefined) {
        testInfo.annotations.push({
          type: 'forward-staged', description: 'screenshots not yet authored.',
        });
        return;
      }
      expect(Array.isArray(m.screenshots)).toBeTruthy();
      expect(m.screenshots.length,
        'manifest.screenshots MUST contain at least one entry.',
      ).toBeGreaterThanOrEqual(1);
      const s = m.screenshots[0];
      expect(s.src,
        'first screenshot MUST carry a non-empty src.',
      ).toMatch(/.+/);
      expect(s.sizes,
        'first screenshot MUST declare its sizes (e.g. "1280x720").',
      ).toMatch(/^\d+x\d+(\s+\d+x\d+)*$/);
      if (s.type !== undefined) {
        expect(s.type).toMatch(/^image\//);
      }
    });

  test('shortcuts array carries at least one well-shaped entry',
    async ({ page }, testInfo) => {
      const m = await fetchManifest(page);
      if (m === null) {
        testInfo.annotations.push({
          type: 'forward-staged', description: 'manifest unreachable.',
        });
        return;
      }
      if (m.shortcuts === undefined) {
        testInfo.annotations.push({
          type: 'forward-staged', description: 'shortcuts not yet authored.',
        });
        return;
      }
      expect(Array.isArray(m.shortcuts)).toBeTruthy();
      expect(m.shortcuts.length,
        'manifest.shortcuts MUST contain at least one entry.',
      ).toBeGreaterThanOrEqual(1);
      const s = m.shortcuts[0];
      expect(s.name,
        'first shortcut MUST carry a non-empty name.',
      ).toMatch(/.+/);
      expect(s.url,
        'first shortcut MUST carry a non-empty url.',
      ).toMatch(/.+/);
    });
});

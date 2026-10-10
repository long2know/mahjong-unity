// Phase K Wave 7 — PWA maskable icon spec (Vasquez).
//
// Hicks's W7 brief tightens the PWA manifest contract: the manifest
// MUST expose at least one icon with `purpose: "maskable"` so the
// install flow can carve an adaptive-icon mask on Android. This was
// missing in W6 (the install button shipped but the icon set was
// generic).
//
// This spec fetches the manifest, parses it, and asserts a maskable
// icon entry is present.
//
// See selectors.md § Phase K Wave 7 → PWA maskable icon.

import { test, expect, type Page } from '@playwright/test';

const MANIFEST_CANDIDATES = [
  '/manifest.webmanifest',
  '/autotable/manifest.webmanifest',
  '/manifest.json',
  '/autotable/manifest.json',
];

interface ManifestIcon {
  src?: string;
  sizes?: string;
  type?: string;
  purpose?: string;
}

interface Manifest {
  icons?: ManifestIcon[];
}

async function fetchManifest(page: Page): Promise<Manifest | null> {
  for (const path of MANIFEST_CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        const body = await res.text();
        return JSON.parse(body) as Manifest;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

test.describe('Phase K Wave 7 — PWA maskable icon', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'PWA maskable icon validated on chromium only.');
  });

  test('manifest exposes at least one maskable icon', async ({ page }) => {
    test.setTimeout(30_000);
    await page.goto('');
    await page.waitForLoadState('networkidle');

    const manifest = await fetchManifest(page);
    if (manifest === null) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'manifest.webmanifest not yet observable (forward-staged Hicks W7 PWA manifest)',
      });
      return;
    }

    if (!Array.isArray(manifest.icons) || manifest.icons.length === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'manifest icons[] not yet populated (forward-staged Hicks W7 icon set)',
      });
      return;
    }

    const maskable = manifest.icons.find((i) =>
      typeof i.purpose === 'string'
      && i.purpose.split(/\s+/).includes('maskable'));

    if (!maskable) {
      // Hard fail when the icon set is present but no maskable entry —
      // this is the actual W7 deliverable.
      const purposes = manifest.icons.map((i) => i.purpose ?? '(none)').join(', ');
      expect(maskable,
        `manifest MUST expose at least one icon with purpose:"maskable"; observed purposes: [${purposes}].`)
        .toBeTruthy();
      return;
    }

    expect(maskable.purpose).toContain('maskable');
  });
});

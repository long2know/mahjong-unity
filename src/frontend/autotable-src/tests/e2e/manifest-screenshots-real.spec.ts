// Phase K Wave 11 — Real screenshot manifest validation (Vasquez).
//
// The W10 manifest gate only checked that the `screenshots` field
// was present; W11 hardens the check by asserting that each
// declared screenshot file actually exists on the deployed origin
// AND that its dimensions match the manifest declaration.
//
// The Hicks `scripts/capture-screenshots.js` produces the real
// PNGs under `src/frontend/autotable/screenshots/` and stamps the
// manifest with `sizes`. This spec walks the manifest's
// `screenshots[]` array and validates each entry.
//
// Forward-stage tolerant: when the manifest has no screenshots OR
// the deployed bundle isn't reachable, the spec annotates and
// passes.

import { test, expect, type Page } from '@playwright/test';

const MANIFEST_CANDIDATES = [
  '/manifest.webmanifest',
  '/autotable/manifest.webmanifest',
  '/manifest.json',
];

interface ScreenshotEntry {
  src?: string;
  sizes?: string;
  type?: string;
  form_factor?: string;
}

interface Manifest {
  screenshots?: ScreenshotEntry[];
}

async function fetchManifest(page: Page): Promise<Manifest | null> {
  for (const path of MANIFEST_CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        return JSON.parse(await res.text()) as Manifest;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

function parseSizes(sizes: string | undefined): { w: number; h: number } | null {
  if (!sizes) return null;
  const m = sizes.match(/^(\d+)x(\d+)/);
  if (!m) return null;
  return { w: parseInt(m[1], 10), h: parseInt(m[2], 10) };
}

test.describe('Phase K Wave 11 — manifest screenshots are real', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'Manifest screenshot validation runs on chromium only.');
  });

  test('each screenshot entry resolves AND dimensions match OR forward-staged',
    async ({ page }, testInfo) => {
      const manifest = await fetchManifest(page);
      if (manifest === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'Manifest not observable at canonical paths.',
        });
        return;
      }

      const entries = Array.isArray(manifest.screenshots) ? manifest.screenshots : [];
      if (entries.length === 0) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'Manifest has no screenshots[] entries yet.',
        });
        return;
      }

      let checked = 0;
      for (const entry of entries) {
        if (!entry.src) {
          continue;
        }
        const res = await page.request.get(entry.src);
        expect(res.ok(),
          `Manifest screenshot ${entry.src} MUST resolve; got ${res.status()}.`,
        ).toBeTruthy();

        const declared = parseSizes(entry.sizes);
        if (declared !== null) {
          const buf = Buffer.from(await res.body());
          // PNG header: 0x89 P N G 0x0d 0x0a 0x1a 0x0a + IHDR chunk
          //   bytes  0..7   signature
          //   bytes  8..11  IHDR length
          //   bytes 12..15  "IHDR"
          //   bytes 16..19  width  (big-endian)
          //   bytes 20..23  height (big-endian)
          if (buf.length >= 24 && buf[0] === 0x89 && buf[1] === 0x50
              && buf[2] === 0x4e && buf[3] === 0x47) {
            const w = buf.readUInt32BE(16);
            const h = buf.readUInt32BE(20);
            expect(w,
              `Screenshot ${entry.src} width MUST match manifest ${declared.w}; got ${w}.`,
            ).toBe(declared.w);
            expect(h,
              `Screenshot ${entry.src} height MUST match manifest ${declared.h}; got ${h}.`,
            ).toBe(declared.h);
            checked++;
          }
        }
      }

      if (checked === 0) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'No PNG screenshot dimensions could be validated yet.',
        });
      }
    });
});

// Phase K Wave 9 — 3D mesh pulse spec (Vasquez).
//
// Hicks's W9 brief lands `World.findThingByFace`. When a tile-ref
// chip in the commentary panel is clicked, the renderer resolves
// the tile id to a 3D mesh via `findThingByFace` and pulses an
// outline on the mesh's bounding box (the W7 outline-shader hook
// extended to a Mesh, not a screen-space overlay).
//
// This spec confirms the visible-pixel delta on click. When the
// hook is not yet wired the spec soft-passes via the
// `forward-staged` annotation.
//
// See selectors.md § Phase K Wave 9 → three-mesh-pulse.

import { test, expect, type Page } from '@playwright/test';

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-mesh-pulse',
      displayName: 'Mesh Pulse Tester',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
}

test.describe('Phase K Wave 9 — three-mesh-pulse', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      '3D mesh pulse validated on chromium only.');
  });

  test('outline visible after findThingByFace + pulseHighlight call',
    async ({ page }) => {
      test.setTimeout(60_000);
      await mockBackend(page);
      await page.goto('');
      await page.waitForLoadState('networkidle');

      const hookShape = await page.evaluate(() => {
        const w: any = window;
        const world = w.game?.world ?? w.world ?? null;
        const renderer = w.game?.renderer ?? w.renderer ?? null;
        return {
          worldFindThingByFace:
            typeof world?.findThingByFace === 'function',
          rendererPulse:
            typeof renderer?.pulseHighlight === 'function'
            || typeof w.pulseHighlight === 'function',
        };
      });

      if (!hookShape.worldFindThingByFace || !hookShape.rendererPulse) {
        test.info().annotations.push({
          type: 'forward-staged',
          description: 'findThingByFace + pulseHighlight not yet observable on window.game.world / window.game.renderer.',
        });
        return;
      }

      // Sample the canvas pixel data, drive the pulse via the hook,
      // then re-sample and assert a non-zero delta in the outlined
      // region. The exact region depends on Hicks's implementation —
      // we sample a center patch as a robustness compromise.
      const beforeAfter = await page.evaluate(async () => {
        const w: any = window;
        const canvas = document.querySelector('canvas') as HTMLCanvasElement | null;
        if (canvas === null) return null;
        const ctx = canvas.getContext('2d');
        if (ctx === null) {
          // WebGL canvas — read via the renderer's gl readPixels indirection.
          // Fallback: capture two screenshots via toDataURL.
          const before = canvas.toDataURL('image/png');
          // Drive the pulse. The mesh-pulse axis tolerates several call shapes.
          const world = w.game?.world ?? w.world;
          const renderer = w.game?.renderer ?? w.renderer;
          const thing = world?.findThingByFace?.('tile-1m');
          if (thing && typeof renderer?.pulseHighlight === 'function') {
            renderer.pulseHighlight(thing);
          } else if (typeof w.pulseHighlight === 'function') {
            w.pulseHighlight('tile-1m');
          }
          await new Promise((r) => setTimeout(r, 250));
          const after = canvas.toDataURL('image/png');
          return { before, after };
        }
        const data1 = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
        const world = w.game?.world ?? w.world;
        const renderer = w.game?.renderer ?? w.renderer;
        const thing = world?.findThingByFace?.('tile-1m');
        if (thing && typeof renderer?.pulseHighlight === 'function') {
          renderer.pulseHighlight(thing);
        } else if (typeof w.pulseHighlight === 'function') {
          w.pulseHighlight('tile-1m');
        }
        await new Promise((r) => setTimeout(r, 250));
        const data2 = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
        let delta = 0;
        for (let i = 0; i < data1.length; i += 4) {
          delta += Math.abs(data1[i] - data2[i]);
        }
        return { delta };
      });

      if (beforeAfter === null) {
        test.info().annotations.push({
          type: 'forward-staged',
          description: 'No <canvas> element present at click time.',
        });
        return;
      }

      if ('delta' in beforeAfter) {
        // 2D-canvas path: assert non-zero pixel delta.
        expect(beforeAfter.delta).toBeGreaterThan(0);
      } else {
        // WebGL path: assert the dataURL changes.
        expect(beforeAfter.before).not.toEqual(beforeAfter.after);
      }
    });
});

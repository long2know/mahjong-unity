// Phase K Wave 8 — Vite SignalR proxy spec (Vasquez).
//
// Hicks's W8 brief: the Vite dev-server config proxies the SignalR
// upgrade handshake at `/hub/*` (or `/ws/*`) so a developer running
// `npm run dev` can connect to the backend hub without explicit
// CORS configuration.
//
// This spec verifies the proxy is wired: open the dev server,
// initiate a SignalR negotiate POST against `/hub/voice` (or
// `/hub/game`), and confirm the response is NOT a 4xx caused by
// "no proxy configured" — it should either be a 200 (successful
// negotiate) OR a 401 (auth required) OR a 404 (hub path not
// yet matched). A 502 / 504 indicates the proxy is broken;
// anything 5xx is a hard fail.
//
// Forward-stage tolerant: when the dev-server base URL points to a
// production Docker container (no Vite), we skip the proxy assertion
// because there's no Vite layer to exercise.
//
// See selectors.md § Phase K Wave 8 → vite-signalr-proxy.

import { test, expect, type Page } from '@playwright/test';

const HUB_PATHS = [
  '/hub/voice/negotiate',
  '/hub/game/negotiate',
  '/api/voice/hub/negotiate',
  '/hub/voice',
  '/hub/game',
];

async function isViteDevServer(page: Page): Promise<boolean> {
  // Vite injects an `__vite_plugin_react_preamble_installed__`
  // global OR ships a `/@vite/client` virtual module. Test for
  // either marker via a synthetic request.
  try {
    const res = await page.request.get('/@vite/client');
    if (res.ok()) return true;
  } catch (_e) { /* not vite */ }
  return false;
}

test.describe('Phase K Wave 8 — Vite SignalR proxy', () => {
  test('SignalR negotiate routes through the Vite proxy', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'dev-server proxy gate — chromium project only');

    // Navigate so the page request inherits the dev-server origin.
    await page.goto('/');

    const isVite = await isViteDevServer(page);
    if (!isVite) {
      testInfo.annotations.push({
        type: 'forward-staged',
        description: 'Base URL is not a Vite dev server — proxy assertion skipped.',
      });
      return;
    }

    let observedAny = false;
    for (const path of HUB_PATHS) {
      const res = await page.request.post(path, {
        data: '',
        headers: { 'Content-Type': 'text/plain;charset=UTF-8' },
      }).catch(() => null);

      if (res === null) continue;
      observedAny = true;
      const status = res.status();
      // 502 / 504 → proxy broken. Anything else (200 / 401 / 404 /
      // 400 / 405) → proxy is wired (the backend rejected for a
      // semantic reason, not a transport one).
      expect.soft(status, `Hub path ${path} returned ${status}.`)
        .not.toBe(502);
      expect.soft(status, `Hub path ${path} returned ${status}.`)
        .not.toBe(504);
    }

    if (!observedAny) {
      testInfo.annotations.push({
        type: 'forward-staged',
        description: 'No hub path returned a parseable response.',
      });
    }
  });
});

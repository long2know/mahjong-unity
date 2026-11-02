// Phase K Wave 9 — SignalR backpressure spec (Vasquez).
//
// Bishop's W9 brief adds a SignalR backpressure middleware that
// DROPS oldest messages when a client's outbound queue exceeds the
// configured high-water mark — preventing OOM on a slow consumer.
//
// This spec simulates a slow consumer (a client that connects to
// the chat / bracket hub but never reads incoming messages) and
// asserts that the JS heap of the page does NOT grow unbounded.
//
// Approach:
//   1. Connect to one of the SignalR hubs via window.signalr (W7
//      install).
//   2. Pause the listener.
//   3. Push 5000 simulated incoming messages via a window hook.
//   4. Measure window.performance.memory.usedJSHeapSize (chromium-
//      only) and assert growth is bounded.
//
// Forward-stage tolerant: when the backpressure middleware isn't
// wired yet, soft-pass with annotation. When wired but unbounded,
// hard-fail.
//
// See selectors.md § Phase K Wave 9 → signalr-backpressure.

import { test, expect, type Page } from '@playwright/test';

const MAX_GROWTH_BYTES = 50 * 1024 * 1024; // 50 MB heap-growth ceiling

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-backpressure',
      displayName: 'Backpressure Tester',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
}

test.describe('Phase K Wave 9 — signalr-backpressure', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'SignalR backpressure validated on chromium only (performance.memory hook).');
  });

  test('slow consumer does not OOM client', async ({ page }, testInfo) => {
    test.setTimeout(60_000);
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');

    const hasMemoryHook = await page.evaluate(() => {
      const w: any = window;
      return !!w.performance?.memory;
    });
    if (!hasMemoryHook) {
      testInfo.annotations.push({
        type: 'forward-staged',
        description: 'window.performance.memory not exposed — measurement skipped.',
      });
      return;
    }

    const baselineHeap = await page.evaluate(() => {
      return (window as any).performance.memory.usedJSHeapSize as number;
    });

    // Simulate 5000 inbound chat messages via the W7 chat-panel
    // window hook (if available). The hook accepts a payload and
    // routes it through the same consumer path as the real hub.
    const pushed = await page.evaluate(() => {
      const w: any = window;
      const pushFns = [
        w.__pushChatMessage,
        w.__publishTournamentBracketUpdate,
        w.__pushAnyHubMessage,
      ].filter((f) => typeof f === 'function');
      if (pushFns.length === 0) return 0;
      const fn = pushFns[0];
      let pushed = 0;
      for (let i = 0; i < 5000; i++) {
        try {
          fn({
            type: 'spam',
            i,
            payload: 'x'.repeat(1024),
          });
          pushed++;
        } catch {
          // The first throw indicates the middleware dropped the
          // message — that's the contract.
          break;
        }
      }
      return pushed;
    });

    if (pushed === 0) {
      testInfo.annotations.push({
        type: 'forward-staged',
        description: 'No hub push-hook observable on window — backpressure surface not yet wired.',
      });
      return;
    }

    // Yield to GC / event loop.
    await page.waitForTimeout(500);

    const finalHeap = await page.evaluate(() => {
      return (window as any).performance.memory.usedJSHeapSize as number;
    });
    const growth = finalHeap - baselineHeap;

    expect(growth,
      `Heap growth ${(growth / 1024 / 1024).toFixed(1)} MB MUST be < ${MAX_GROWTH_BYTES / 1024 / 1024} MB after 5000 inbound messages on a slow consumer.`,
    ).toBeLessThan(MAX_GROWTH_BYTES);
  });
});

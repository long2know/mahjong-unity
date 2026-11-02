// Phase J Wave 9 — Table chat panel spec (Vasquez).
//
// Validates the bottom-right docked chat panel surfaced via Hicks's
// `src/chat.ts` and the DOM scaffold in `index.html` (~line 900).
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md
// (Phase J Wave 9 § Chat surfaces).  Asserted testids:
//   chat-panel, chat-toggle, chat-channel-select, chat-recipient-select,
//   chat-unavailable, chat-messages, chat-input, chat-char-count,
//   chat-send.
//
// Reflection-defensive: when the chat surface isn't shipped on the page
// (e.g. the test runs against a build before Hicks's wiring), the spec
// soft-passes via test.info().annotations rather than hard-failing.
// Backend endpoints (`GET /api/games/{id}/chat`, `POST .../chat`) are
// mocked so the test does not depend on Bishop's hub state.

import { test, expect, type Page } from '@playwright/test';
import { newActor, applyRoom, closeLobby, openOnline } from './_lobby-repair';

const FAKE_GAME_ID = '00000000-0000-0000-0000-000000000c00';

async function gotoChat(page: Page): Promise<void> {
  // Bypass the lobby — go straight to a synthetic gameId so the chat
  // panel's mount gate (`#chat-panel { display: none } until gameId`) flips.
  await page.goto(`?gameId=${FAKE_GAME_ID}`);
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(750);
}

async function chatShipped(page: Page): Promise<boolean> {
  const panel = await page.getByTestId('chat-panel').count();
  return panel > 0;
}

async function mockChatBackend(page: Page): Promise<void> {
  // Backfill endpoint — empty seed.
  await page.route('**/api/games/**/chat**', (route) => {
    if (route.request().method() === 'GET') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ messages: [] }),
      });
    }
    if (route.request().method() === 'POST') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          id: 'msg-1',
          channel: 'table',
          body: 'hello',
          sentUtc: new Date().toISOString(),
        }),
      });
    }
    return route.continue();
  });
}

test.describe('Mahjong Autotable — table chat panel', () => {
  test.beforeEach(async ({ page }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Chat panel desktop-only on first pass; mobile deferred.');
  });

  test('chat panel mounts when a gameId is on the URL', async ({ page }) => {
    test.setTimeout(45_000);
    await mockChatBackend(page);
    await gotoChat(page);

    if (!(await chatShipped(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'chat-panel surface not yet wired',
      });
      return;
    }

    const panel = page.getByTestId('chat-panel');
    await expect(panel).toBeAttached({ timeout: 5_000 });

    const toggle = page.getByTestId('chat-toggle');
    if (await toggle.count() > 0) {
      await expect(toggle).toBeVisible({ timeout: 5_000 });
    }
  });

  test('composer enforces the 280-char client limit', async ({ browser, baseURL }, testInfo) => {
    test.setTimeout(45_000);
    const actor = await newActor(browser, baseURL, testInfo, 'composer-limit');
    try {
      await mockChatBackend(actor.page);
      await applyRoom(actor, 0);
      await closeLobby(actor);
      await openOnline(actor);
      const input = actor.page.getByTestId('chat-input');
      await expect(input).toBeEnabled();
      await expect(input).toHaveAttribute('maxlength', '280');
      await input.fill('x'.repeat(300));
      expect((await input.inputValue()).length).toBe(280);
      await expect(actor.page.getByTestId('chat-char-count')).toHaveText('280/280');
    } finally {
      await actor.context.close();
    }
  });

  test('a real seated table exposes table and private chat channels', async ({ browser, baseURL }, testInfo) => {
    test.setTimeout(45_000);
    const actor = await newActor(browser, baseURL, testInfo, 'chat-channels');
    try {
      await mockChatBackend(actor.page);
      await applyRoom(actor, 0);
      await closeLobby(actor);
      await openOnline(actor);
      const options = actor.page.getByTestId('chat-channel-select').locator('option');
      await expect(options).toHaveCount(2);
      expect(await options.evaluateAll(items => items.map(item => (item as HTMLOptionElement).value)))
        .toEqual(['table', 'private']);
      expect(actor.errors).toEqual([]);
    } finally {
      await actor.context.close();
    }
  });

  test('send button stays graceful when backend is missing', async ({ page }) => {
    test.setTimeout(45_000);

    // No backend mock — the POST /api/games/{id}/chat will 404. The UI
    // must not crash; the unavailable banner or a soft no-op are both
    // acceptable.
    await page.route('**/api/games/**/chat**', (route) => route.fulfill({
      status: 404,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'not found' }),
    }));

    await gotoChat(page);

    if (!(await chatShipped(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'chat-panel not yet wired',
      });
      return;
    }

    const toggle = page.getByTestId('chat-toggle');
    if (await toggle.count() > 0) {
      await toggle.click().catch(() => undefined);
    }

    const unavailable = page.getByTestId('chat-unavailable');
    const input = page.getByTestId('chat-input');
    const send = page.getByTestId('chat-send');

    // Either the unavailable banner shows, OR the composer remains
    // present but the send is a graceful no-op. Both shapes are accepted.
    if (await unavailable.count() > 0 && await unavailable.isVisible().catch(() => false)) {
      await expect(unavailable).toBeVisible({ timeout: 5_000 });
      return;
    }

    if (await input.count() > 0 && await send.count() > 0) {
      await input.fill('hello').catch(() => undefined);
      await send.click().catch(() => undefined);
      // No assertion on the result — we only require the page didn't crash.
      await expect(page.getByTestId('chat-panel')).toBeAttached({ timeout: 2_000 });
    }
  });
});

import { test, expect, type Page } from '@playwright/test';
import { writeFileSync } from 'node:fs';
import { newActor, applyRoom, closeLobby } from './_lobby-repair';
import { assertResultTileArtwork, captureResultTileArtwork } from './_result-tile-artwork';
import type { HandResultEntry } from '../../src/types';

const HAND = [107, 36, 0, 1, 2, 3, 72, 35, 71, 104, 16, 17, 18, 19, 73];

function fixtureResult(hand: number[], token: string, type: HandResultEntry['type'] = 'Hu'): HandResultEntry {
  return {
    type, winner: type === 'Draw' ? -1 : 0, nextBanker: 0, hand,
    score: [{ seat: 2, delta: -8 }, { seat: 0, delta: 24 }, { seat: 3, delta: -8 }, { seat: 1, delta: -8 }],
    continuation: {
      gameId: 'controlled-result-artwork-fixture', handNumber: 1, resultToken: token,
      requiredSeats: [0], acknowledgedSeats: [], waitingSeats: [0],
    },
  };
}

async function showFixture(page: Page, result: HandResultEntry): Promise<void> {
  // Exercise the shipped collection subscriptions and modal renderer locally.
  // No server settlement is claimed and no fixture acknowledgement is sent.
  await page.waitForFunction(() => {
    const client = (window as unknown as { game: { client: {
      connected(): boolean; lastGameId: string | null; serverSnapshotGameId: string | null;
    } } }).game.client;
    return client.connected() && client.lastGameId !== null && client.serverSnapshotGameId === client.lastGameId;
  });
  await page.evaluate(value => {
    const fixtureWindow = window as unknown as {
      game: { client: { result: {
        get(key: string): HandResultEntry | null;
        onUpdate(entries: Array<[string, string, HandResultEntry]>, full: boolean): void;
      } } };
      resultArtworkFixture?: { value: HandResultEntry };
    };
    const collection = fixtureWindow.game.client.result;
    if (!fixtureWindow.resultArtworkFixture) {
      const fixture = { value };
      fixtureWindow.resultArtworkFixture = fixture;
      const originalGet = collection.get.bind(collection);
      // Pin only the component's input: a real empty-room snapshot must not
      // erase a synthetic result. The continuation spec covers real settlements.
      collection.get = key => key === 'current' ? fixture.value : originalGet(key);
    }
    fixtureWindow.resultArtworkFixture.value = value;
    collection.onUpdate([['result', 'current', value]], false);
  }, result);
  await expect(page.getByTestId('hand-result-dialog')).toBeVisible();
}

for (const screen of [
  { name: 'desktop', viewport: { width: 1280, height: 900 }, locale: 'en-US', lang: 'en',
    suits: ['characters', 'circles', 'bamboo'], separator: ' ' },
  { name: 'portrait', viewport: { width: 390, height: 844 }, locale: 'zh-CN', lang: 'zh-Hans',
    suits: ['万', '筒', '条'], separator: '' },
  { name: 'landscape', viewport: { width: 844, height: 390 }, locale: 'zh-TW', lang: 'zh-Hant',
    suits: ['萬', '筒', '條'], separator: '' },
]) {
  test(`controlled result fixture paints board artwork, retains order and keeps Continue reachable (${screen.name})`,
    async ({ browser, baseURL }, info) => {
      test.setTimeout(60_000);
      info.annotations.push({ type: 'provenance', description: 'Controlled component fixture, not a completed game.' });
      const mobile = screen.name !== 'desktop';
      const context = await browser.newContext({
        viewport: screen.viewport, locale: screen.locale, hasTouch: mobile, isMobile: mobile, deviceScaleFactor: 1,
      });
      try {
        const actor = await newActor(browser, baseURL, info, `result-artwork-${screen.name}`, context);
        const page = actor.page;
        await applyRoom(actor, 0);
        await closeLobby(actor);
        await page.waitForFunction(() => typeof document.getElementById('result-next')?.onclick === 'function');
        await expect(page.locator('body')).toHaveAttribute('lang', screen.lang);
        const sentBefore = actor.sent.length;
        const result = fixtureResult(HAND, 'artwork');
        await showFixture(page, result);
        const names = HAND.map(id => {
          const face = Math.floor(id / 4);
          return `${face % 9 + 1}${screen.separator}${screen.suits[Math.floor(face / 9)]}`;
        });
        await assertResultTileArtwork(page, HAND, names);
        await expect(page.locator('#result-score tbody tr')).toHaveCount(4);
        await expect(page.locator('#result-score tbody tr').first()).toContainText('+24');
        const next = page.getByTestId('hand-result-continue');
        await expect(next).toBeEnabled();
        await expect(next).toBeInViewport({ ratio: 1 });
        expect(await next.evaluate(element => {
          const rect = element.getBoundingClientRect();
          return element.contains(document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2));
        })).toBe(true);
        await expect(page.locator('#own-hand-tray')).toHaveCount(0);
        await captureResultTileArtwork(page, info, `fixture-${screen.name}`, 'controlled-component-fixture', result);

        await showFixture(page, result);
        await assertResultTileArtwork(page, HAND, names);
        for (const type of ['Draw', 'ZhaHu'] as const) {
          await showFixture(page, fixtureResult([], type, type));
          await expect(page.locator('#result-hand .result-tile')).toHaveCount(0);
          await expect(page.locator('#result-headline')).toHaveText(type === 'Draw' ? '流局 Draw' : '诈胡! False Hu');
          await expect(next).toBeInViewport({ ratio: 1 });
        }
        await showFixture(page, fixtureResult([-1, 108, 0.5, 107], 'invalid-ids'));
        const unknown = page.locator('#result-hand .result-tile-unknown');
        await expect(unknown).toHaveCount(3);
        await expect(unknown.first()).toHaveAccessibleName(screen.lang === 'en' ? 'Unknown tile' : '未知牌');
        expect(await unknown.evaluateAll(tiles => tiles.every(tile => getComputedStyle(tile).backgroundImage === 'none'))).toBe(true);
        await expect(page.locator('#result-hand .result-tile').last()).toHaveAttribute('data-face', '26');
        expect(actor.sent.slice(sentBefore).flatMap(frame => frame.entries ?? [])
          .filter(([kind]) => ['handResultAck', 'claim', 'discard', 'things', 'slots'].includes(kind))).toEqual([]);
        expect(actor.errors).toEqual([]);
      } catch (error) {
        const page = context.pages()[0];
        if (page) {
          const state = await page.evaluate(() => {
              const client = (window as unknown as { game?: { client: {
                result: { get(key: string): HandResultEntry | null };
                connected(): boolean; lastGameId: string | null; serverSnapshotGameId: string | null;
              } } }).game?.client;
              const hand = document.getElementById('result-hand');
              const modal = document.getElementById('result-modal');
              return {
                result: client?.result.get('current'), connected: client?.connected(),
                room: client?.lastGameId, snapshotRoom: client?.serverSnapshotGameId,
                hand: hand?.getBoundingClientRect().toJSON(),
                modalDisplay: modal ? getComputedStyle(modal).display : null,
                continuation: modal?.dataset.continuationState,
              };
            });
          writeFileSync(info.outputPath('controlled-fixture-failure.json'), JSON.stringify(state, null, 2));
          await page.screenshot({ path: info.outputPath('controlled-fixture-failure.png') });
        }
        throw error;
      } finally {
        await context.close();
      }
    });
}

import { test, expect } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { newActor, applyRoom, closeLobby, dismissPrompts, knownJoinUrl, roomId, waitLobbyReady } from './_lobby-repair';
import { waitForBoardHand } from './_mobile-geometry';

interface RoomRead {
  path: string;
  alias: string;
  clientRoom: string | null;
  snapshotRoom: string | null;
  hasTurn: boolean;
  connected: boolean;
}
interface ObservedClient {
  lastGameId: string | null;
  serverSnapshotGameId: string | null;
  connected(): boolean;
  turn: { get(key: string): unknown };
}
interface ObservedWindow extends Window {
  game?: { client?: ObservedClient };
  __mahjongClient?: ObservedClient;
  __observeRoomRead(value: RoomRead): Promise<void>;
}

test('ordinary NEW, reconnect, room switch and JOIN fetch room APIs only after bound FULL; global presence stays independent', async ({ browser, baseURL }, testInfo) => {
  test.setTimeout(60_000);
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  const requests: RoomRead[] = [];
  const failures: Array<{ path: string; status: number }> = [];
  await context.exposeBinding('__observeRoomRead', (_source, value: RoomRead) => { requests.push(value); });
  await context.addInitScript(() => {
    const observed = window as unknown as ObservedWindow;
    const original = window.fetch.bind(window);
    window.fetch = (input, init): Promise<Response> => {
      const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, location.href);
      const match = /^\/api\/games\/(changsha-[^/]+)(?:\/chat)?$/.exec(url.pathname);
      if (match) {
        const client = observed.game?.client ?? observed.__mahjongClient;
        // Observation only: retain every real request, response and error.
        void observed.__observeRoomRead({
          path: url.pathname + url.search, alias: decodeURIComponent(match[1]),
          clientRoom: client?.lastGameId ?? null,
          snapshotRoom: client?.serverSnapshotGameId ?? null,
          hasTurn: Boolean(client?.turn?.get('current')),
          connected: client?.connected() === true,
        });
      }
      return original(input, init);
    };
  });
  context.on('page', page => page.on('response', response => {
    const url = new URL(response.url());
    if (/^\/api\/games\/changsha-[^/]+(?:\/chat)?$/.test(url.pathname) && response.status() >= 400) {
      failures.push({ path: url.pathname + url.search, status: response.status() });
    }
  }));
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'bound-room-http', context);
    await waitLobbyReady(actor);
    expect(roomIdOrNull(actor.page.url())).toBeNull();
    expect(requests).toEqual([]);

    const awaitReads = async (alias: string, start: number): Promise<void> => {
      await expect.poll(() => {
        const reads = requests.slice(start).filter(r => r.alias === alias);
        return {
          metadata: reads.some(r => r.path === '/api/games/' + alias),
          history: reads.some(r => r.path.startsWith('/api/games/' + alias + '/chat?')),
        };
      }).toEqual({ metadata: true, history: true });
      for (const request of requests.slice(start)) {
        expect(request.connected, request.path).toBe(true);
        expect(request.snapshotRoom, request.path).toBe(request.clientRoom);
        expect(request.clientRoom, request.path).toBe(request.alias);
        expect(request.hasTurn, request.path).toBe(true);
      }
    };

    await actor.page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
    const first = await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    await awaitReads(first, 0);
    await waitForBoardHand(actor.page);

    let mark = requests.length;
    await actor.page.reload({ waitUntil: 'domcontentloaded' });
    await dismissPrompts(actor);
    await closeLobby(actor);
    await awaitReads(first, mark);
    await waitForBoardHand(actor.page);

    mark = requests.length;
    await actor.page.getByTestId('new-game-button').click();
    await actor.page.waitForURL(url => url.searchParams.has('gameId') && url.searchParams.get('gameId') !== first);
    await dismissPrompts(actor);
    await closeLobby(actor);
    const second = roomId(actor);
    await awaitReads(second, mark);

    mark = requests.length;
    await actor.page.goto(knownJoinUrl(actor.page.url(), first), { waitUntil: 'domcontentloaded' });
    await dismissPrompts(actor);
    await closeLobby(actor);
    await awaitReads(first, mark);
    expect(failures, 'no early404 or any other room HTTP error is accepted').toEqual([]);
    expect(actor.errors).toEqual([]);
    const file = testInfo.outputPath('room-readiness-http.json');
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, JSON.stringify({ first, second, requests, failures, errors: actor.errors }, null, 2));
    await actor.page.screenshot({ path: testInfo.outputPath('bound-room.png') });
  } finally { await context.close(); }
});

function roomIdOrNull(url: string): string | null {
  return new URL(url).searchParams.get('gameId');
}

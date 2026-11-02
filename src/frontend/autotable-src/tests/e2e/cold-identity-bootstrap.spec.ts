import { test, expect, type Page } from '@playwright/test';
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { waitForBoardHand } from './_mobile-geometry';

interface IdentityReply {
  page: string;
  path: string;
  playerId: string;
  cookieDigest: string | null;
}
interface Handshake {
  page: string;
  path: string;
  cookieDigest: string | null;
}

function guestCookieDigest(raw: string | undefined): string | null {
  const pair = raw?.split(';').map(value => value.trim()).find(value => value.startsWith('mahjong_pid='));
  return pair ? createHash('sha256').update(pair).digest('hex') : null;
}

test('fresh normal pages keep one verified identity across API, signed cookie, primary WS and global lobby', async ({ browser, baseURL }, testInfo) => {
  test.setTimeout(60_000);
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  const replies: IdentityReply[] = [], handshakes: Handshake[] = [];
  const joined: Array<{ page: string; playerId: string; seat: number | null }> = [];
  const hubSnapshots: string[] = [], errors: string[] = [], responseWork: Promise<void>[] = [];
  const observe = async (page: Page, label: string): Promise<void> => {
    const cdp = await context.newCDPSession(page);
    const sockets = new Map<string, string>();
    await cdp.send('Network.enable');
    cdp.on('Network.webSocketCreated', event => sockets.set(event.requestId, new URL(event.url).pathname));
    cdp.on('Network.webSocketWillSendHandshakeRequest', event => {
      const pathname = sockets.get(event.requestId) ?? '';
      if (pathname !== '/autotable/ws' && pathname !== '/hubs/changsha') return;
      const header = Object.entries(event.request.headers).find(([name]) => name.toLowerCase() === 'cookie')?.[1];
      handshakes.push({ page: label, path: pathname, cookieDigest: guestCookieDigest(typeof header === 'string' ? header : undefined) });
    });
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', response => {
      const pathname = new URL(response.url()).pathname;
      if (!['/api/identity', '/api/auth/me'].includes(pathname)) return;
      const task = (async (): Promise<void> => {
        expect(response.status(), pathname).toBe(200);
        const body = await response.json() as { playerId?: string };
        expect(body.playerId, pathname + ' must identify the actual signed session').toBeTruthy();
        const set = (await response.headersArray()).find(header =>
          header.name.toLowerCase() === 'set-cookie' && header.value.startsWith('mahjong_pid='));
        replies.push({ page: label, path: pathname, playerId: body.playerId!, cookieDigest: guestCookieDigest(set?.value) });
      })();
      responseWork.push(task);
    });
    page.on('websocket', socket => {
      socket.on('framereceived', event => {
        for (const fragment of String(event.payload).split('\u001e').filter(Boolean)) {
          try {
            const frame = JSON.parse(fragment) as {
              type?: string | number; playerId?: string; viewer?: { seat: number | null };
              target?: string; result?: { players?: unknown[] };
            };
            if (frame.type === 'JOINED') joined.push({ page: label, playerId: frame.playerId!, seat: frame.viewer!.seat });
            if (frame.target === 'LobbyPlayersChanged' || Array.isArray(frame.result?.players)) hubSnapshots.push(label);
          } catch { /* Only observe complete game/SignalR JSON frames. */ }
        }
      });
    });
  };
  const dismiss = async (page: Page): Promise<void> => {
    for (const selector of ['#tour-skip', '#onboarding-skip', '#lobby-close']) {
      const element = page.locator(selector);
      if (await element.isVisible()) await element.click();
    }
  };
  try {
    expect(await context.cookies()).toEqual([]);
    const owner = await context.newPage();
    await observe(owner, 'owner');
    await owner.goto(baseURL ?? 'http://localhost:8080/autotable/', { waitUntil: 'domcontentloaded' });
    await expect.poll(() => replies.some(r => r.path === '/api/auth/me')).toBe(true);
    await expect.poll(() => hubSnapshots.includes('owner')).toBe(true);
    for (const selector of ['#tour-skip', '#onboarding-skip']) {
      const element = owner.locator(selector);
      if (await element.isVisible()) await element.click();
    }
    await owner.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
    await owner.locator('label:has(input[name="lobby-deal-mode"][value="auto"])').click();
    await owner.locator('label:has(input[name="lobby-bot-count"][value="3"])').click();
    await owner.getByTestId('lobby-apply').click();
    await owner.waitForURL(url => url.searchParams.has('gameId'), { waitUntil: 'domcontentloaded' });
    await dismiss(owner);
    await expect.poll(() => joined.some(j => j.page === 'owner' && j.seat === 0)).toBe(true);
    await waitForBoardHand(owner);
    const alias = new URL(owner.url()).searchParams.get('gameId')!;
    const duplicate = await context.newPage();
    await observe(duplicate, 'duplicate');
    const link = new URL('/autotable/', owner.url());
    link.search = new URLSearchParams({ gameId: alias, variant: 'changsha', join: '1' }).toString();
    await duplicate.goto(link.href, { waitUntil: 'domcontentloaded' });
    await dismiss(duplicate);
    await expect.poll(() => joined.some(j => j.page === 'duplicate')).toBe(true);
    await expect.poll(() => replies.some(r => r.page === 'duplicate' && r.path === '/api/auth/me')).toBe(true);
    await Promise.all(responseWork);
    const id = replies.find(r => r.path === '/api/identity')!.playerId;
    expect(replies.every(r => r.playerId === id), JSON.stringify(replies)).toBe(true);
    expect(joined.every(j => j.playerId === id), JSON.stringify(joined)).toBe(true);
    expect(joined.find(j => j.page === 'owner')!.seat).toBe(0);
    expect(joined.find(j => j.page === 'duplicate')!.seat).toBeNull();
    const issued = new Set(replies.map(r => r.cookieDigest).filter(Boolean));
    expect(handshakes.some(h => h.path === '/autotable/ws')).toBe(true);
    expect(handshakes.some(h => h.path === '/hubs/changsha')).toBe(true);
    for (const handshake of handshakes) {
      expect(handshake.cookieDigest, handshake.path + ' must carry the established signed cookie').not.toBeNull();
      expect(issued.has(handshake.cookieDigest), JSON.stringify(handshake)).toBe(true);
    }
    expect(errors).toEqual([]);
  } finally {
    await Promise.allSettled(responseWork);
    const file = testInfo.outputPath('cold-identity-audit.json');
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, JSON.stringify({ replies, handshakes, joined, hubSnapshots, errors,
      fixture: 'Fresh empty BrowserContext; no API pre-issued identity, fake cookie, response mock, or weakened ID equality.' }, null, 2));
    await context.close();
  }
});

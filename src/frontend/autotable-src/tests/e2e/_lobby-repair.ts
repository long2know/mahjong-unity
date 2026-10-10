import { expect, type Browser, type BrowserContext, type Page, type TestInfo } from '@playwright/test';
import { discardByPointer } from './_playability';

export interface WireFrame {
  type?: string | number;
  gameId?: string;
  target?: string;
  invocationId?: string;
  viewer?: { roomId: string | null; revision: number; seat: number | null };
  result?: { success?: boolean; players?: unknown[]; [key: string]: unknown };
  arguments?: unknown[];
  entries?: Array<[string, string | number, Record<string, unknown> | null]>;
}

export interface Actor {
  context: BrowserContext;
  page: Page;
  id: string;
  name: string;
  label: string;
  sent: WireFrame[];
  received: WireFrame[];
  errors: string[];
  httpFailures: Array<{ url: string; status: number }>;
  transports: string[];
}

export interface RoomMetadata {
  gameId: string;
  ownerId: string | null;
  viewerIsOwner: boolean;
  phase: string;
  isPublic: boolean;
  publicName: string | null;
  botCount: number;
  seatedCount: number;
  openHumanSeats: number;
  canMakePublic: boolean;
  canInvite: boolean;
}

export interface TableProbe {
  connected: boolean;
  seat: number | null;
  seats: Array<string | null>;
  handCount: number;
  handIds: number[];
  turn: { phase: string; activeSeat: number | null; awaitingDiscard: boolean } | null;
  pickup: { phase: string; seatIndex: number; count: number } | null;
}

function recordFrames(payload: string | Buffer, destination: WireFrame[]): void {
  for (const fragment of String(payload).split('\u001e').filter(Boolean)) {
    try {
      const frame = JSON.parse(fragment) as WireFrame;
      if (frame.entries) {
        frame.entries = frame.entries.filter(([kind]) =>
          ['seats', 'turn', 'pickup', 'ownTurn', 'claim', 'match', 'discard', 'actionRejected'].includes(kind));
      }
      destination.push(frame);
    } catch { /* Binary asset frames are not game/SignalR JSON. */ }
  }
}

export async function newActor(
  browser: Browser, baseURL: string | undefined, testInfo: TestInfo, label: string,
  sharedContext?: BrowserContext, destination?: string,
): Promise<Actor> {
  const use = testInfo.project.use;
  const context = sharedContext ?? await browser.newContext({
    viewport: use.viewport,
    userAgent: use.userAgent,
    deviceScaleFactor: use.deviceScaleFactor,
    isMobile: use.isMobile,
    hasTouch: use.hasTouch,
    locale: use.locale,
    colorScheme: use.colorScheme,
    reducedMotion: use.contextOptions?.reducedMotion,
  });
  const page = await context.newPage();
  const actor: Actor = { context, page, id: '', name: '', label, sent: [], received: [], errors: [], httpFailures: [], transports: [] };
  page.on('pageerror', error => actor.errors.push(error.message));
  page.on('request', request => {
    const pathname = new URL(request.url()).pathname;
    if (pathname.startsWith('/hubs/changsha')) actor.transports.push(request.url());
    if (pathname === '/hubs/changsha' && request.method() === 'POST')
      recordFrames(request.postData() ?? '', actor.sent);
  });
  page.on('response', response => {
    if (response.status() >= 400) actor.httpFailures.push({ url: response.url(), status: response.status() });
    if (new URL(response.url()).pathname === '/hubs/changsha'
      && response.request().method() === 'GET' && response.status() === 200) {
      void response.body().then(body => recordFrames(body, actor.received)).catch(() => undefined);
    }
  });
  page.on('websocket', socket => {
    actor.transports.push(socket.url());
    socket.on('framesent', event => recordFrames(event.payload, actor.sent));
    socket.on('framereceived', event => recordFrames(event.payload, actor.received));
  });
  const identityResponse = page.waitForResponse(response =>
    new URL(response.url()).pathname === '/api/identity'
      && response.request().method() === 'POST');
  try {
    await page.goto(destination ?? baseURL ?? 'http://localhost:8080/autotable/', { waitUntil: 'domcontentloaded' });
    const response = await identityResponse;
    expect(response.status(), 'a cached offline identity is not verified bootstrap').toBe(200);
    const identity = await response.json() as { playerId: string; displayName: string };
    expect(identity.playerId).toBeTruthy();
    actor.id = identity.playerId;
    actor.name = identity.displayName;
    await dismissPrompts(actor);
    return actor;
  } catch (error) {
    if (sharedContext) await page.close();
    else await context.close();
    throw error;
  }
}

export async function newActors(
  browser: Browser, baseURL: string | undefined, testInfo: TestInfo, labels: string[],
): Promise<Actor[]> {
  const results = await Promise.allSettled(labels.map(label => newActor(browser, baseURL, testInfo, label)));
  const actors = results.flatMap(result => result.status === 'fulfilled' ? [result.value] : []);
  const failed = results.find(result => result.status === 'rejected');
  if (failed?.status === 'rejected') {
    await Promise.all(actors.map(actor => actor.context.close()));
    throw failed.reason;
  }
  return actors;
}

export async function dismissPrompts(actor: Actor): Promise<void> {
  for (const selector of ['#onboarding-skip', '#tour-skip']) {
    const button = actor.page.locator(selector);
    if (await button.isVisible()) await button.click();
  }
}

export async function probe(actor: Actor): Promise<TableProbe> {
  return actor.page.evaluate(() => {
    type Collection<T> = { get(key: string): T | null };
    const client = (window as unknown as {
      game?: { client?: {
        connected(): boolean;
        seat: number | null;
        seatPlayers: Array<string | null>;
        things: { entries(): Iterable<[string | number, { slotName?: string } | null]> };
        turn: Collection<TableProbe['turn']>;
        pickup: Collection<TableProbe['pickup']>;
      } };
    }).game?.client;
    if (!client) return { connected: false, seat: null, seats: [], handCount: 0, handIds: [], turn: null, pickup: null };
    const hand = [...client.things.entries()].filter(([, thing]) =>
      thing?.slotName?.startsWith('hand.') && thing.slotName.endsWith('@' + client.seat));
    return {
      connected: client.connected(), seat: client.seat, seats: client.seatPlayers,
      handCount: hand.length, handIds: hand.map(([id]) => id).filter((id): id is number => typeof id === 'number'),
      turn: client.turn.get('current'), pickup: client.pickup.get('current'),
    };
  });
}

export function roomId(actor: Actor): string {
  const id = new URL(actor.page.url()).searchParams.get('gameId');
  expect(id, 'the normal UI must navigate to a concrete room').toBeTruthy();
  return id!;
}

export async function metadata(actor: Actor, alias = roomId(actor)): Promise<RoomMetadata> {
  const url = new URL('/api/games/' + encodeURIComponent(alias), actor.page.url());
  const response = await actor.page.request.get(url.href);
  expect(response.status(), 'metadata must exist on the actual signed-guest route').toBe(200);
  return await response.json() as RoomMetadata;
}

export async function openLobby(actor: Actor): Promise<void> {
  if (!await actor.page.locator('#lobby-panel').isVisible())
    await actor.page.getByTestId('lobby-toggle').click();
  await expect(actor.page.getByTestId('lobby-apply')).toBeVisible();
}

export async function closeLobby(actor: Actor): Promise<void> {
  if (await actor.page.locator('#lobby-panel').isVisible())
    await actor.page.locator('#lobby-close').click();
}

export async function applyRoom(actor: Actor, bots: number, seat = 0, dealMode: 'manual' | 'auto' = 'manual'): Promise<string> {
  await openLobby(actor);
  await actor.page.locator('input[name="lobby-variant"][value="changsha"]').check();
  await actor.page.locator(`input[name="lobby-seat"][value="${seat}"]`).check();
  await actor.page.locator(`input[name="lobby-deal-mode"][value="${dealMode}"]`).check();
  await actor.page.locator(`input[name="lobby-bot-count"][value="${bots}"]`).check();
  const oldURL = actor.page.url();
  await actor.page.getByTestId('lobby-apply').click();
  await actor.page.waitForURL(url => url.href !== oldURL && url.searchParams.has('gameId'),
    { waitUntil: 'domcontentloaded' });
  const url = new URL(actor.page.url());
  expect(url.searchParams.get('botCount')).toBe(String(bots));
  expect(url.searchParams.has('bots'), 'ordinary Apply must not conceal a botCount bug with bots=false').toBe(false);
  await waitForSeat(actor, seat < 0 ? null : seat);
  return roomId(actor);
}

export async function waitForSeat(actor: Actor, seat?: number | null): Promise<TableProbe> {
  await actor.page.waitForFunction(() => {
    const game = (window as unknown as { game?: { client?: { connected(): boolean }; world?: unknown } }).game;
    return game?.client?.connected() && game.world;
  });
  if (seat !== undefined) await expect.poll(async () => (await probe(actor)).seat).toBe(seat);
  else await expect.poll(async () => (await probe(actor)).seat).not.toBeNull();
  return probe(actor);
}

export function assertJoinUrl(value: string, base: string, alias: string): URL {
  const url = new URL(value, base);
  expect(url.origin).toBe(new URL(base).origin);
  expect(url.pathname).toBe('/autotable/');
  expect(url.hash).toBe('');
  expect([...url.searchParams.keys()].sort()).toEqual(['gameId', 'join', 'variant']);
  expect(url.searchParams.getAll('gameId')).toEqual([alias]);
  expect(url.searchParams.get('variant')).toBe('changsha');
  expect(url.searchParams.get('join')).toBe('1');
  return url;
}

export function knownJoinUrl(base: string, alias: string): string {
  const url = new URL('/autotable/', base);
  url.search = new URLSearchParams({ gameId: alias, variant: 'changsha', join: '1' }).toString();
  return assertJoinUrl(url.href, base, alias).href;
}

export async function copyJoinLink(actor: Actor): Promise<string> {
  await openLobby(actor);
  await actor.context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: new URL(actor.page.url()).origin });
  await actor.page.getByTestId('copy-table-link').click();
  const copied = await actor.page.evaluate(() => navigator.clipboard.readText());
  const url = assertJoinUrl(copied, actor.page.url(), roomId(actor));
  await closeLobby(actor);
  return url.href;
}

export async function followJoinLink(actor: Actor, href: string, alias: string): Promise<TableProbe> {
  assertJoinUrl(href, actor.page.url(), alias);
  const start = actor.sent.length;
  await actor.page.goto(href, { waitUntil: 'domcontentloaded' });
  await dismissPrompts(actor);
  const state = await waitForSeat(actor);
  expect(roomId(actor)).toBe(alias);
  expect(actor.sent.slice(start).filter(frame => frame.type === 'NEW')).toEqual([]);
  expect((await metadata(actor)).gameId).toBe(alias);
  return state;
}

export async function waitForJoinClose(actor: Actor): Promise<void> {
  const socket = await actor.page.waitForEvent('websocket', {
    predicate: connection => new URL(connection.url()).pathname === '/autotable/ws',
  });
  if (!socket.isClosed()) await socket.waitForEvent('close');
}

export async function publish(actor: Actor, name: string): Promise<void> {
  await openLobby(actor);
  const toggle = actor.page.getByTestId('lobby-set-public-toggle');
  await expect(toggle).toBeEnabled();
  await toggle.hover();
  await toggle.focus();
  await expect(toggle, 'hover/focus must not reset authoritative creator eligibility').toBeEnabled();
  await actor.page.getByTestId('lobby-public-name-input').fill(name);
  await toggle.check();
  await expect.poll(async () => {
    const state = await metadata(actor);
    return { isPublic: state.isPublic, publicName: state.publicName };
  }).toEqual({ isPublic: true, publicName: name.trim() });
  await closeLobby(actor);
}

export async function openOnline(actor: Actor): Promise<void> {
  const panel = actor.page.getByTestId('chat-panel');
  if (await actor.page.getByTestId('lobby-open-chat').isVisible()
    && await actor.page.locator('#lobby-panel').isVisible()) {
    await actor.page.getByTestId('lobby-open-chat').click();
  } else {
    if (await actor.page.locator('#lobby-panel').isVisible()) await closeLobby(actor);
    if (await actor.page.getByTestId('chat-toggle').getAttribute('aria-expanded') !== 'true')
      await actor.page.getByTestId('chat-toggle').click();
  }
  await expect(panel).toBeVisible();
  await expect(actor.page.getByTestId('online-players')).toBeVisible();
}

export function onlineRow(actor: Actor, playerId: string) {
  return actor.page.locator(`[data-testid="online-player"][data-player-id="${playerId}"]`);
}

export async function invite(sender: Actor, recipient: Actor): Promise<void> {
  await waitLobbyReady(sender);
  await waitLobbyReady(recipient);
  await openOnline(sender);
  const row = onlineRow(sender, recipient.id);
  await expect(row).toHaveCount(1);
  await expect(row.getByTestId('online-player-invite')).toBeEnabled();
  const sentStart = sender.sent.length;
  const receivedStart = sender.received.length;
  await row.getByTestId('online-player-invite').click();
  await expect.poll(() => sender.sent.slice(sentStart).some(frame => frame.target === 'SendTableInvite')).toBe(true);
  const request = sender.sent.slice(sentStart).find(frame => frame.target === 'SendTableInvite')!;
  expect(request.arguments).toEqual([roomId(sender), recipient.id]);
  await expect.poll(() => sender.received.slice(receivedStart).find(frame =>
    frame.type === 3 && frame.invocationId === request.invocationId)?.result?.success).toBe(true);
}

export async function waitLobbyReady(actor: Actor): Promise<void> {
  await expect.poll(() => actor.received.some(frame => frame.target === 'LobbyPlayersChanged'
    || (frame.type === 3 && Array.isArray(frame.result?.players)))).toBe(true);
}

export async function driveManualCeremony(actors: Actor[]): Promise<void> {
  const observer = actors[0];
  for (let step = 0; step < 40; step++) {
    const current = await probe(observer);
    if (current.turn?.phase === 'AwaitingDiscard') return;
    if (current.turn?.phase === 'RollingDice' || await observer.page.locator('#roll-dice').isVisible()) {
      await expect(observer.page.locator('#roll-dice')).toBeVisible();
      await observer.page.locator('#roll-dice').click();
      await expect.poll(async () => (await probe(observer)).turn?.phase).not.toBe('RollingDice');
      continue;
    }
    const states = await Promise.all(actors.map(actor => probe(actor)));
    const pickerIndex = states.findIndex(state => state.seat !== null
      && state.pickup?.seatIndex === state.seat && state.pickup.count > 0);
    if (pickerIndex >= 0) {
      const picker = actors[pickerIndex];
      const before = states[pickerIndex];
      const count = before.pickup!.count;
      await picker.page.locator('#pickup-take-btn').click();
      await expect.poll(async () => (await probe(picker)).handCount).toBe(before.handCount + count);
      continue;
    }
    const signature = JSON.stringify({ turn: current.turn, pickup: current.pickup });
    await expect.poll(async () => {
      const next = await probe(observer);
      return JSON.stringify({ turn: next.turn, pickup: next.pickup });
    }).not.toBe(signature);
  }
  expect((await probe(observer)).turn?.phase, 'ceremony did not reach an authoritative discard turn').toBe('AwaitingDiscard');
}

export async function discardThroughPointer(actor: Actor): Promise<void> {
  await closeLobby(actor);
  if (await actor.page.getByTestId('chat-toggle').getAttribute('aria-expanded') === 'true')
    await actor.page.getByTestId('chat-toggle').click();
  const before = actor.sent.length;
  const result = await discardByPointer(actor.page);
  const commands = actor.sent.slice(before).flatMap(frame => frame.entries ?? []).filter(([kind]) => kind === 'discard');
  expect(commands, JSON.stringify(result)).toHaveLength(1);
  const tile = commands[0][2]?.tileId;
  expect(typeof tile).toBe('number');
  await expect.poll(async () => (await probe(actor)).handIds).not.toContain(tile);
}

export async function saveAndClose(testInfo: TestInfo, actors: Actor[]): Promise<void> {
  try {
    await Promise.all(actors.map(async actor => {
      const state = await probe(actor).catch(() => null);
      await testInfo.attach(actor.label + '-lobby-evidence', {
        body: JSON.stringify({
          playerId: actor.id, url: actor.page.url(), state,
          sent: actor.sent, received: actor.received, errors: actor.errors,
          httpFailures: actor.httpFailures, transports: actor.transports,
        }, null, 2),
        contentType: 'application/json',
      });
      if (!actor.page.isClosed()) {
        await actor.page.screenshot({ path: testInfo.outputPath(actor.label + '.png') });
      }
    }));
  } finally {
    await Promise.all([...new Set(actors.map(actor => actor.context))].map(context => context.close()));
  }
}

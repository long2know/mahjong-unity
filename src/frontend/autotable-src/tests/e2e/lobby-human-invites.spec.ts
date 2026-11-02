import { test, expect, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  type Actor, newActor, newActors, applyRoom, metadata, probe, copyJoinLink, followJoinLink,
  assertJoinUrl, knownJoinUrl, roomId, waitForSeat, openLobby, closeLobby, openOnline,
  publish, invite, waitLobbyReady, waitForJoinClose, dismissPrompts, saveAndClose,
} from './_lobby-repair';

async function firstObserverLobbyPollAfterAck(page: Page): Promise<{
  games: Array<{ gameId: string; botCount: number; openHumanSeats: number }>;
  startedAt: number;
}> {
  const sourceRoot = process.env.E2E_SOURCE_ROOT ?? resolve(__dirname, '../..');
  const pollingSource = readFileSync(resolve(sourceRoot, 'src/matchmaking.ts'), 'utf8');
  expect(pollingSource).toMatch(/export const MATCHMAKING_POLL_MS = 5000;/);
  expect(pollingSource).toMatch(/window\.setInterval\(\(\) => \{ void pollOnce\(\); \}, MATCHMAKING_POLL_MS\)/);
  // The caller arms this only after server acknowledgement; older in-flight requests cannot match.
  const request = await page.waitForRequest(value => value.method() === 'GET'
    && new URL(value.url()).pathname === '/api/matchmaking/lobby');
  const response = await request.response();
  expect(response, 'the first new observer-page poll must produce a response').not.toBeNull();
  expect(response!.status()).toBe(200);
  const body = await response!.json() as { games: Array<{ gameId: string; botCount: number; openHumanSeats: number }> };
  expect(Array.isArray(body.games)).toBe(true);
  return { games: body.games, startedAt: request.timing().startTime };
}

test('a targeted chat invite reaches only the selected player and joins the existing two-bot room', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const [sender, recipient, third] = await newActors(browser, baseURL, testInfo,
      ['inviter', 'recipient', 'unrelated-third']);
    actors.push(sender, recipient, third);
    expect(new Set(actors.map(actor => actor.id)).size).toBe(3);
    const [destination, originalRoom] = await Promise.all([applyRoom(sender, 2), applyRoom(recipient, 0)]);
    expect(originalRoom).not.toBe(destination);
    const title = `<b>Invite ${destination.slice(-8)}</b>`;
    await publish(sender, title);
    await expect(recipient.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'false');
    await waitLobbyReady(third);
    const originalURL = recipient.page.url();
    await invite(sender, recipient);
    await expect(recipient.page.getByTestId('chat-invite-unread')).toBeVisible();
    await expect(recipient.page.getByTestId('table-invite-toast')).toBeVisible();
    expect(recipient.page.url(), 'receiving an invite must not navigate').toBe(originalURL);
    await openOnline(recipient);
    const card = recipient.page.getByTestId('table-invite');
    await expect(card).toHaveCount(1);
    await expect(card.getByTestId('table-invite-sender')).toContainText(sender.name);
    await expect(card.getByTestId('table-invite-table')).toContainText(title);
    await expect(card.getByTestId('table-invite-table').locator('b')).toHaveCount(0);
    const inviteId = await card.getAttribute('data-invite-id');
    expect(inviteId).toBeTruthy();
    const href = await card.getByTestId('table-invite-join').getAttribute('href');
    expect(href).toBeTruthy();
    assertJoinUrl(href!, recipient.page.url(), destination);
    expect(new URL(sender.page.url()).searchParams.has('seat')).toBe(true);
    await expect.poll(() => recipient.received.filter(frame => frame.target === 'TableInviteReceived').length).toBe(1);
    await openOnline(third);
    await expect(third.page.getByTestId('table-invite')).toHaveCount(0);
    expect(third.received.filter(frame => frame.target === 'TableInviteReceived')).toEqual([]);
    await expect(third.page.getByTestId('chat-messages')).not.toContainText(inviteId!);

    await invite(sender, recipient);
    await expect(sender.page.getByTestId('online-invite-status')).not.toBeEmpty();
    await expect(recipient.page.getByTestId('table-invite')).toHaveCount(1);
    await recipient.page.reload({ waitUntil: 'domcontentloaded' });
    await waitForSeat(recipient, 0);
    await openOnline(recipient);
    await expect(recipient.page.getByTestId('table-invite')).toHaveCount(1);
    await expect(recipient.page.getByTestId('table-invite')).toHaveAttribute('data-invite-id', inviteId!);
    expect(roomId(recipient)).toBe(originalRoom);
    const beforeJoin = recipient.sent.length;
    await recipient.page.getByTestId('table-invite-join').click();
    await recipient.page.waitForURL(url => url.searchParams.get('gameId') === destination && url.searchParams.get('join') === '1',
      { waitUntil: 'domcontentloaded' });
    assertJoinUrl(recipient.page.url(), sender.page.url(), destination);
    await waitForSeat(recipient, 3);
    expect(recipient.sent.slice(beforeJoin).filter(frame => frame.type === 'NEW')).toEqual([]);
    expect(await metadata(recipient)).toMatchObject({
      gameId: destination, ownerId: sender.id, viewerIsOwner: false,
      botCount: 2, seatedCount: 2, openHumanSeats: 0, phase: 'RollingDice',
    });
    expect((await probe(sender)).seat).toBe(0);
    await openLobby(recipient);
    await expect(recipient.page.getByTestId('lobby-set-public-toggle')).toBeDisabled();
    await expect(recipient.page.getByTestId('lobby-public-status')).not.toBeEmpty();
    expect(actors.flatMap(actor => actor.errors)).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('public-card Join uses a safe alias without seating the metadata hub', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const [owner, cardJoiner] = await newActors(browser, baseURL, testInfo,
      ['public-card-owner', 'card-joiner']);
    actors.push(owner, cardJoiner);
    expect(owner.id).not.toBe(cardJoiner.id);
    const creating = applyRoom(owner, 1);
    await cardJoiner.page.getByTestId('lobby-public-games-tab').hover();
    const alias = await creating;
    const title = `Public ${alias.slice(-8)}`;
    await publish(owner, title);
    const firstList = firstObserverLobbyPollAfterAck(cardJoiner.page);
    await cardJoiner.page.getByTestId('lobby-public-games-tab').click();
    const observedList = await firstList;
    const card = cardJoiner.page.locator('.public-game-card').filter({ hasText: title });
    await expect(card).toHaveCount(1);
    await expect(card.getByTestId(/^lobby-public-game-seats-\d+$/)).toHaveText(/2.*(?:open|available|free)|(?:open|available|free).*2/i);
    const listed = observedList.games.find(game => game.gameId === alias);
    expect(listed).toMatchObject({ gameId: alias, botCount: 1, openHumanSeats: 2 });
    await card.getByRole('button', { name: 'Join', exact: true }).click();
    await cardJoiner.page.waitForURL(url => url.searchParams.get('gameId') === alias
      && url.searchParams.get('join') === '1', { waitUntil: 'domcontentloaded' });
    await waitForSeat(cardJoiner);
    assertJoinUrl(cardJoiner.page.url(), owner.page.url(), alias);
    expect(await metadata(owner)).toMatchObject({
      gameId: alias, botCount: 1, seatedCount: 2, openHumanSeats: 1, phase: 'Seating', isPublic: true,
    });
    // Leave no joinable public room for another independently arranged case.
    await openLobby(owner);
    const publicToggle = owner.page.getByTestId('lobby-set-public-toggle');
    await expect(publicToggle).toBeEnabled();
    await expect(publicToggle).toBeChecked();
    const sentStart = owner.sent.length;
    const receivedStart = owner.received.length;
    await Promise.all([
      expect.poll(() => {
        const invocation = owner.sent.slice(sentStart).find(frame => frame.target === 'SetGamePublic'
          && frame.arguments?.[0] === alias && frame.arguments?.[1] === false);
        const result = invocation ? owner.received.slice(receivedStart).find(frame =>
          frame.type === 3 && frame.invocationId === invocation.invocationId)?.result : undefined;
        return { invocationId: invocation?.invocationId, arguments: invocation?.arguments?.slice(0, 2), result };
      }).toMatchObject({
        invocationId: expect.any(String), arguments: [alias, false],
        result: { success: true, gameId: alias, isPublic: false },
      }),
      publicToggle.uncheck(),
    ]);
    await expect.poll(async () => (await metadata(owner)).isPublic).toBe(false);
    expect(actors.flatMap(actor => actor.errors)).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('Join Random uses a safe alias without seating the metadata hub', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const [owner, randomJoiner] = await newActors(browser, baseURL, testInfo,
      ['random-owner', 'random-joiner']);
    actors.push(owner, randomJoiner);
    expect(owner.id).not.toBe(randomJoiner.id);
    expect(new URL(randomJoiner.page.url()).searchParams.has('gameId')).toBe(false);
    const creating = applyRoom(owner, 1);
    await randomJoiner.page.getByTestId('lobby-public-games-tab').hover();
    const alias = await creating;
    const title = `Random ${alias.slice(-8)}`;
    await publish(owner, title);
    const firstList = firstObserverLobbyPollAfterAck(randomJoiner.page);
    await randomJoiner.page.getByTestId('lobby-public-games-tab').click();
    await dismissPrompts(randomJoiner);
    const observedList = await firstList;
    expect(observedList.games.find(game => game.gameId === alias))
      .toMatchObject({ gameId: alias, botCount: 1, openHumanSeats: 2 });
    const card = randomJoiner.page.locator('.public-game-card').filter({ hasText: title });
    await expect(card).toHaveCount(1);
    await expect(card.getByTestId(/^lobby-public-game-seats-\d+$/)).toHaveText(/2.*(?:open|available|free)|(?:open|available|free).*2/i);
    const companion = await newActor(browser, baseURL, testInfo, 'random-companion',
      undefined, knownJoinUrl(owner.page.url(), alias));
    actors.push(companion);
    expect(new Set(actors.map(actor => actor.id)).size).toBe(3);
    await waitForSeat(companion);
    assertJoinUrl(companion.page.url(), owner.page.url(), alias);
    expect(await metadata(owner)).toMatchObject({
      gameId: alias, botCount: 1, seatedCount: 2, openHumanSeats: 1, phase: 'Seating', isPublic: true,
    });
    await randomJoiner.page.getByTestId('lobby-join-random').click();
    await randomJoiner.page.waitForURL(url => url.searchParams.get('gameId') === alias
      && url.searchParams.get('join') === '1', { waitUntil: 'domcontentloaded' });
    await waitForSeat(randomJoiner);
    assertJoinUrl(randomJoiner.page.url(), owner.page.url(), alias);
    expect(randomJoiner.sent.some(frame => frame.target === 'FindJoinableGame')).toBe(true);
    expect(randomJoiner.sent.some(frame => frame.target === 'JoinRandom')).toBe(false);
    expect((await probe(companion)).seat).not.toBe((await probe(randomJoiner)).seat);
    expect(await metadata(owner)).toMatchObject({ gameId: alias, botCount: 1, seatedCount: 3, openHumanSeats: 0 });
    await openLobby(owner);
    const publicToggle = owner.page.getByTestId('lobby-set-public-toggle');
    await expect(publicToggle).toBeDisabled();
    await publicToggle.hover();
    await expect(publicToggle).toBeDisabled();
    await expect(owner.page.getByTestId('lobby-public-status')).not.toBeEmpty();
    expect(actors.flatMap(actor => actor.errors)).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('creator hover and keyboard focus preserve public eligibility and unpublishing removes the actual card', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const [owner, watcher] = await newActors(browser, baseURL, testInfo, ['public-toggle-owner', 'public-list-watcher']);
    actors.push(owner, watcher);
    const alias = await applyRoom(owner, 0);
    const title = `Toggle ${alias.slice(-8)}`;
    await publish(owner, title);
    const publishedPoll = firstObserverLobbyPollAfterAck(watcher.page);
    await watcher.page.getByTestId('lobby-public-games-tab').click();
    const published = await publishedPoll;
    expect(published.games.find(game => game.gameId === alias)).toMatchObject({ gameId: alias, botCount: 0, openHumanSeats: 3 });
    const card = watcher.page.locator('.public-game-card').filter({ hasText: title });
    await expect(card).toHaveCount(1);
    await openLobby(owner);
    const toggle = owner.page.getByTestId('lobby-set-public-toggle');
    await toggle.hover();
    await toggle.focus();
    await expect(toggle).toBeEnabled();
    await toggle.uncheck();
    await expect.poll(async () => (await metadata(owner)).isPublic).toBe(false);
    const unpublished = await firstObserverLobbyPollAfterAck(watcher.page);
    expect(unpublished.games.some(game => game.gameId === alias)).toBe(false);
    expect(unpublished.startedAt).toBeGreaterThan(published.startedAt);
    await expect(card).toHaveCount(0);
    expect(await metadata(owner)).toMatchObject({ gameId: alias, phase: 'Seating', canMakePublic: true });
    expect(actors.flatMap(actor => actor.errors)).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('a missing known-link room rejects visibly without creating or navigating to a substitute', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const joiner = await newActor(browser, baseURL, testInfo, 'missing-room-joiner');
    actors.push(joiner);
    const alias = `missing-lobby-${Date.now()}`;
    const href = knownJoinUrl(joiner.page.url(), alias);
    const start = joiner.sent.length;
    const closed = waitForJoinClose(joiner);
    await joiner.page.goto(href, { waitUntil: 'domcontentloaded' });
    await closed;
    await expect(joiner.page.getByTestId('room-join-error')).toBeVisible();
    await expect(joiner.page.getByTestId('room-join-error')).not.toBeEmpty();
    expect(joiner.received.flatMap(frame => frame.entries ?? []).some(([kind, , value]) =>
      kind === 'actionRejected' && value?.action === 'join' && value.reason === 'room-not-found')).toBe(true);
    expect(roomId(joiner)).toBe(alias);
    expect(joiner.sent.slice(start).filter(frame => frame.type === 'NEW')).toEqual([]);
    const missing = await joiner.page.request.get(new URL('/api/games/' + alias, href).href);
    expect(missing.status()).toBe(404);
    expect(joiner.errors).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('an invitation reserves no seat: a stale Join rejects without replacing the now-started room', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const [owner, invited, faster] = await newActors(browser, baseURL, testInfo,
      ['stale-invite-owner', 'stale-invite-recipient', 'last-seat-taker']);
    actors.push(owner, invited, faster);
    const alias = await applyRoom(owner, 2);
    const link = await copyJoinLink(owner);
    await invite(owner, invited);
    await openOnline(invited);
    await expect(invited.page.getByTestId('table-invite')).toHaveCount(1);
    await followJoinLink(faster, link, alias);
    expect(await metadata(owner)).toMatchObject({ botCount: 2, seatedCount: 2, openHumanSeats: 0, phase: 'RollingDice' });
    const start = invited.sent.length;
    const closed = waitForJoinClose(invited);
    await invited.page.getByTestId('table-invite-join').click();
    await closed;
    await expect(invited.page.getByTestId('room-join-error')).toBeVisible();
    expect(roomId(invited)).toBe(alias);
    expect(invited.received.flatMap(frame => frame.entries ?? []).some(([kind, , value]) =>
      kind === 'actionRejected' && value?.action === 'join' && value.reason === 'room-not-seating')).toBe(true);
    expect(invited.sent.slice(start).filter(frame => frame.type === 'NEW')).toEqual([]);
    expect(await metadata(owner)).toMatchObject({ gameId: alias, ownerId: owner.id, botCount: 2, seatedCount: 2 });
    expect(actors.flatMap(actor => actor.errors)).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('ordinary three-person chat uses real DTOs and private history does not leak to the third person', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const [alice, bob, carol] = await newActors(browser, baseURL, testInfo, ['chat-alice', 'chat-bob', 'chat-carol']);
    actors.push(alice, bob, carol);
    const alias = await applyRoom(alice, 0);
    const link = await copyJoinLink(alice);
    await Promise.all([followJoinLink(bob, link, alias), followJoinLink(carol, link, alias)]);
    await openOnline(alice);
    const send = async (body: string): Promise<Record<string, unknown>> => {
      const response = alice.page.waitForResponse(value => value.request().method() === 'POST'
        && new URL(value.url()).pathname === `/api/games/${alias}/chat`);
      await alice.page.getByTestId('chat-input').fill(body);
      await alice.page.getByTestId('chat-send').click();
      const actual = await response;
      expect(actual.status()).toBe(200);
      const dto = await actual.json() as Record<string, unknown>;
      expect(dto.senderPlayerId).toBe(alice.id);
      expect(dto.senderDisplayName).toBe(alice.name);
      expect(dto.gameId).toBe(alias);
      expect(Number.isNaN(Date.parse(String(dto.sentUtc)))).toBe(false);
      return dto;
    };
    const tableBody = `table-${Date.now()}`;
    await alice.page.getByTestId('chat-channel-select').selectOption('table');
    await send(tableBody);
    for (const recipient of [bob, carol]) {
      await openOnline(recipient);
      await expect(recipient.page.getByTestId('chat-messages')).toContainText(tableBody);
    }
    const privateBody = `private-${Date.now()}`;
    await alice.page.getByTestId('chat-channel-select').selectOption('private');
    await alice.page.getByTestId('chat-recipient-select').selectOption(bob.id);
    const privateDto = await send(privateBody);
    expect(privateDto.channel).toBe('private');
    expect(privateDto.recipientPlayerId).toBe(bob.id);
    const bobHistory = await bob.page.request.get(new URL(`/api/games/${alias}/chat`, bob.page.url()).href);
    const carolHistory = await carol.page.request.get(new URL(`/api/games/${alias}/chat?limit=1`, carol.page.url()).href);
    expect(bobHistory.status()).toBe(200);
    expect(carolHistory.status()).toBe(200);
    expect(JSON.stringify(await bobHistory.json())).toContain(privateBody);
    const thirdMessages = (await carolHistory.json() as { messages: Array<{ body: string }> }).messages;
    expect(thirdMessages).toHaveLength(1);
    expect(thirdMessages[0].body).toBe(tableBody);
    await expect(carol.page.getByTestId('chat-messages')).not.toContainText(privateBody);
    await bob.page.reload({ waitUntil: 'domcontentloaded' });
    await waitForSeat(bob);
    await closeLobby(bob);
    await openOnline(bob);
    await bob.page.getByTestId('chat-channel-select').selectOption('private');
    await bob.page.getByTestId('chat-recipient-select').selectOption(alice.id);
    await expect(bob.page.getByTestId('chat-messages')).toContainText(privateBody);
    expect(actors.flatMap(actor => actor.errors)).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

import { test, expect } from '@playwright/test';
import {
  type Actor, newActor, newActors, applyRoom, openOnline, onlineRow, metadata, publish, waitLobbyReady, saveAndClose,
  knownJoinUrl, waitForSeat, probe,
} from './_lobby-repair';

function hasLobbySnapshot(actor: Actor): boolean {
  return actor.received.some(frame => frame.target === 'LobbyPlayersChanged'
    || (frame.type === 3 && Array.isArray((frame as { result?: { players?: unknown[] } }).result?.players)));
}

test('different rooms and a lobby-only browser are discoverable before anyone opens chat', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const [alice, bob, lobbyOnly] = await newActors(browser, baseURL, testInfo, ['alice', 'bob', 'lobby-only']);
    actors.push(alice, bob, lobbyOnly);
    expect(new Set(actors.map(actor => actor.id)).size).toBe(3);
    const [aliceRoom, bobRoom] = await Promise.all([applyRoom(alice, 1), applyRoom(bob, 0)]);
    expect(aliceRoom).not.toBe(bobRoom);
    expect(new URL(lobbyOnly.page.url()).searchParams.has('gameId')).toBe(false);
    for (const actor of actors) {
      await expect.poll(() => hasLobbySnapshot(actor)).toBe(true);
      await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'false');
    }
    for (const actor of actors) {
      await openOnline(actor);
      for (const other of actors.filter(candidate => candidate.id !== actor.id))
        await expect(onlineRow(actor, other.id)).toHaveCount(1);
      await expect(onlineRow(actor, actor.id)).toHaveCount(0);
      await expect(actor.page.getByTestId('online-player')).toHaveCount(2);
      await expect(actor.page.getByTestId('online-players')).not.toContainText(aliceRoom);
      await expect(actor.page.getByTestId('online-players')).not.toContainText(bobRoom);
      expect(actor.errors).toEqual([]);
    }
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('failed identity refresh does not authorize cached-identity transports and the real retry recovers', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const [returning, other] = await newActors(browser, baseURL, testInfo, ['cached-display-identity', 'online-control']);
    actors.push(returning, other);
    await waitLobbyReady(returning);
    const transportMark = returning.transports.length;
    const receiveMark = returning.received.length;
    // Failure injection only: no fabricated identity, response, roster or game action.
    await returning.page.route('**/api/identity', route => route.abort('failed'));
    await returning.page.reload({ waitUntil: 'domcontentloaded' });
    await openOnline(returning);
    await expect(returning.page.getByTestId('online-players-status')).toContainText(/unavailable|offline|retry|connecting/i);
    await expect(returning.page.getByTestId('online-players-status')).not.toContainText(/no.*players|nobody online/i);
    expect(returning.transports.slice(transportMark)).toEqual([]);
    await returning.page.unroute('**/api/identity');
    const verified = returning.page.waitForResponse(response =>
      new URL(response.url()).pathname === '/api/identity' && response.request().method() === 'POST');
    await returning.page.getByTestId('online-players-retry').click();
    const response = await verified;
    expect(response.status()).toBe(200);
    expect((await response.json() as { playerId: string }).playerId).toBe(returning.id);
    await expect.poll(() => returning.received.slice(receiveMark).some(frame =>
      frame.target === 'LobbyPlayersChanged' || Array.isArray(frame.result?.players))).toBe(true);
    await expect(onlineRow(returning, other.id)).toHaveCount(1);
    expect(returning.errors).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('same-cookie tabs deduplicate; closing a metadata tab preserves the WS host; final closure removes presence', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const [owner, watcher] = await newActors(browser, baseURL, testInfo, ['ws-owner', 'watcher']);
    actors.push(owner, watcher);
    const alias = await applyRoom(owner, 0);
    await publish(owner, 'Presence lifecycle');
    const duplicate = await newActor(browser, baseURL, testInfo, 'same-cookie-tab', owner.context);
    actors.push(duplicate);
    expect(duplicate.id).toBe(owner.id);
    expect(watcher.id).not.toBe(owner.id);
    await expect.poll(() => hasLobbySnapshot(duplicate)).toBe(true);
    await openOnline(watcher);
    await expect(onlineRow(watcher, owner.id)).toHaveCount(1);
    await duplicate.page.close();
    await expect(onlineRow(watcher, owner.id)).toHaveCount(1);
    expect(await metadata(owner, alias)).toMatchObject({
      gameId: alias, ownerId: owner.id, viewerIsOwner: true, canMakePublic: true, isPublic: true,
      seatedCount: 1, botCount: 0,
    });

    await owner.page.close();
    await expect(onlineRow(watcher, owner.id)).toHaveCount(0, { timeout: 5000 });
    const returned = await newActor(browser, baseURL, testInfo, 'same-cookie-returned', owner.context);
    actors.push(returned);
    expect(returned.id).toBe(owner.id);
    await expect(onlineRow(watcher, owner.id)).toHaveCount(1);
    await openOnline(returned);
    await expect(onlineRow(returned, returned.id)).toHaveCount(0);
    expect(actors.flatMap(actor => actor.errors)).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('C1 real duplicate table tab is an observer while the public occupant and original private seat remain', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const owner = await newActor(browser, baseURL, testInfo, 'c1-active-owner');
    actors.push(owner);
    const alias = await applyRoom(owner, 3, 0, 'auto');
    await expect.poll(async () => (await probe(owner)).handCount).toBe(14);
    const ownedIds = (await probe(owner)).handIds;
    expect(ownedIds).toHaveLength(14);
    expect((await probe(owner)).seats[0], 'the server occupant must match the verified fixture identity').toBe(owner.id);
    expect(await owner.page.evaluate(() =>
      (window as unknown as { game: { client: { playerId(): string } } }).game.client.playerId()),
    'C1 owner identity must match the API bootstrap').toBe(owner.id);
    const duplicate = await newActor(browser, baseURL, testInfo, 'c1-live-duplicate',
      owner.context, knownJoinUrl(owner.page.url(), alias));
    actors.push(duplicate);
    expect(duplicate.id).toBe(owner.id);
    await waitForSeat(duplicate, null);
    await expect.poll(async () => (await probe(duplicate)).seats[0],
      { message: 'the observer full snapshot must retain the original public occupant' }).toBe(owner.id);
    const observed = await probe(duplicate);
    expect(await duplicate.page.evaluate(() =>
      (window as unknown as { game: { client: { playerId(): string } } }).game.client.playerId()),
    'duplicate C1 identity must equal the same verified API identity').toBe(owner.id);
    expect(observed.connected).toBe(true);
    expect(observed.seats[0]).toBe(owner.id);
    expect(observed.handIds).toEqual([]);
    const ack = duplicate.received.filter(frame => frame.type === 'JOINED').pop();
    expect(ack?.viewer).toEqual({ roomId: alias, revision: expect.any(Number), seat: null });
    expect(ack!.viewer!.revision).toBeGreaterThanOrEqual(0);
    const worldSeat = await duplicate.page.evaluate(() =>
      (window as unknown as { game: { world: { seat: number | null } } }).game.world.seat);
    expect(worldSeat).toBeNull();
    expect((await probe(owner)).seat).toBe(0);
    expect((await probe(owner)).handIds.slice().sort((a, b) => a - b)).toEqual(ownedIds.slice().sort((a, b) => a - b));
    await duplicate.page.close();
    expect((await probe(owner)).seat).toBe(0);
    expect((await metadata(owner)).viewerIsOwner).toBe(true);
    expect(actors.flatMap(actor => actor.errors)).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

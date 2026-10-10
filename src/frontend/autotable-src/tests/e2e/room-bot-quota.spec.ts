import { test, expect } from '@playwright/test';
import {
  type Actor, newActor, newActors, applyRoom, metadata, probe, copyJoinLink, followJoinLink,
  waitForSeat, driveManualCeremony, discardThroughPointer, saveAndClose,
} from './_lobby-repair';

for (const bots of [0, 1, 2]) {
  test(`Apply with ${bots} bots waits for ${4 - bots} distinct humans before manual start`, async ({ browser, baseURL }, testInfo) => {
    const actors: Actor[] = [];
    try {
      actors.push(...await newActors(browser, baseURL, testInfo,
        Array.from({ length: 4 - bots }, (_, index) => `human-${index}`)));
      expect(new Set(actors.map(actor => actor.id)).size).toBe(4 - bots);
      const owner = actors[0];
      const alias = await applyRoom(owner, bots);
      expect(await metadata(owner)).toMatchObject({
        gameId: alias, phase: 'Seating', botCount: bots, seatedCount: 1, openHumanSeats: 3 - bots,
      });
      const link = await copyJoinLink(owner);
      for (let index = 1; index < actors.length; index++) {
        expect((await metadata(owner)).phase).toBe('Seating');
        const joined = await followJoinLink(actors[index], link, alias);
        expect(joined.seat).not.toBe(0);
        expect(await metadata(owner)).toMatchObject({
          gameId: alias, botCount: bots, seatedCount: index + 1, openHumanSeats: 3 - bots - index,
        });
      }
      const states = await Promise.all(actors.map(actor => probe(actor)));
      expect(new Set(states.map(state => state.seat)).size).toBe(actors.length);
      for (let index = 0; index < actors.length; index++) {
        expect(states[index].seats[states[index].seat!]).toBe(actors[index].id);
        expect(new Set(states[index].seats.filter(id => actors.some(actor => actor.id === id))).size).toBe(actors.length);
      }
      await expect.poll(async () => (await metadata(owner)).phase).toBe('RollingDice');
      await owner.page.locator('#roll-dice').click();
      await expect.poll(async () => (await probe(owner)).pickup?.seatIndex).toBe(0);
      await owner.page.locator('#pickup-take-btn').click();
      await expect.poll(async () => (await probe(owner)).handCount).toBe(4);
      expect((await metadata(owner)).botCount).toBe(bots);
      expect(actors.flatMap(actor => actor.errors)).toEqual([]);
    } finally {
      await saveAndClose(testInfo, actors);
    }
  });
}

test('Quick Match deliberately chooses three Medium bots and supports a real manual discard', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const owner = await newActor(browser, baseURL, testInfo, 'quick-match-owner');
    actors.push(owner);
    await owner.page.locator('input[name="lobby-bot-count"][value="0"]').check();
    await owner.page.locator('input[name="lobby-bot-difficulty"][value="Hard"]').check();
    await owner.page.locator('input[name="lobby-deal-mode"][value="manual"]').check();
    await owner.page.getByTestId('lobby-quick-match').click();
    await owner.page.waitForURL(url => url.searchParams.has('gameId'), { waitUntil: 'domcontentloaded' });
    await waitForSeat(owner, 0);
    const url = new URL(owner.page.url());
    expect(url.searchParams.get('botCount')).toBe('3');
    expect(url.searchParams.get('botDifficulty')).toBe('Medium');
    expect(await metadata(owner)).toMatchObject({ botCount: 3, seatedCount: 1, openHumanSeats: 0 });
    await driveManualCeremony([owner]);
    expect((await probe(owner)).handCount).toBe(14);
    await discardThroughPointer(owner);
    expect(owner.errors).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

for (const dealMode of ['manual', 'auto'] as const) {
  test(`Apply with three bots supports ${dealMode} deal and a real discard`, async ({ browser, baseURL }, testInfo) => {
    const actors: Actor[] = [];
    try {
      const owner = await newActor(browser, baseURL, testInfo, 'three-bot-owner');
      actors.push(owner);
      await applyRoom(owner, 3, 0, dealMode);
      expect(await metadata(owner)).toMatchObject({ botCount: 3, seatedCount: 1, openHumanSeats: 0 });
      if (dealMode === 'manual') await driveManualCeremony([owner]);
      await expect.poll(async () => (await probe(owner)).handCount).toBe(14);
      await discardThroughPointer(owner);
      expect(owner.errors).toEqual([]);
    } finally {
      await saveAndClose(testInfo, actors);
    }
  });
}

test('four-bot Apply requires the spectator option and never grants the viewer a human seat', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const viewer = await newActor(browser, baseURL, testInfo, 'four-bot-spectator');
    actors.push(viewer);
    await applyRoom(viewer, 4, -1);
    expect(await metadata(viewer)).toMatchObject({ botCount: 4, seatedCount: 0, openHumanSeats: 0 });
    await expect.poll(async () => (await metadata(viewer)).phase).not.toBe('Seating');
    expect((await probe(viewer)).seat).toBeNull();
    expect((await probe(viewer)).handIds).toEqual([]);
    expect(viewer.errors).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

test('explicit legacy bots=false is forwarded separately from the ordinary Apply quota gates', async ({ browser, baseURL }, testInfo) => {
  const actors: Actor[] = [];
  try {
    const owner = await newActor(browser, baseURL, testInfo, 'explicit-legacy-bots');
    actors.push(owner);
    const alias = `legacy-bots-${Date.now()}`;
    const url = new URL('/autotable/', owner.page.url());
    url.search = new URLSearchParams({
      gameId: alias, createGame: alias, variant: 'changsha', seat: '0',
      dealMode: 'manual', botCount: '3', bots: 'false',
    }).toString();
    const start = owner.transports.length;
    await owner.page.goto(url.href, { waitUntil: 'domcontentloaded' });
    await waitForSeat(owner, 0);
    const handshake = owner.transports.slice(start).map(value => new URL(value))
      .find(value => value.pathname === '/autotable/ws');
    expect(handshake).toBeDefined();
    expect(handshake!.searchParams.get('bots')).toBe('false');
    expect(await metadata(owner)).toMatchObject({
      gameId: alias, phase: 'Seating', botCount: 0, seatedCount: 1, openHumanSeats: 3,
    });
    expect(owner.errors).toEqual([]);
  } finally {
    await saveAndClose(testInfo, actors);
  }
});

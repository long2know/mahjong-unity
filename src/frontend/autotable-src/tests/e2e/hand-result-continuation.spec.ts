import { test, expect, type Page, type TestInfo } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import {
  newActor, applyRoom, closeLobby, copyJoinLink, followJoinLink, roomId,
  dismissPrompts, probe, driveManualCeremony, waitForSeat, type Actor,
} from './_lobby-repair';
import type { HandResultEntry } from '../../src/types';
import { mobileGeometry } from './_mobile-geometry';
import { assertResultTileArtwork, captureResultTileArtwork } from './_result-tile-artwork';

type Frame = { type?: string; full?: boolean; entries?: Array<[string, string | number, Record<string, unknown> | null]> };

function wire(page: Page): { sent: Frame[]; received: Frame[] } {
  const result = { sent: [] as Frame[], received: [] as Frame[] };
  page.on('websocket', socket => {
    if (new URL(socket.url()).pathname !== '/autotable/ws') return;
    socket.on('framesent', e => { try { result.sent.push(JSON.parse(String(e.payload)) as Frame); } catch { /* non-JSON */ } });
    socket.on('framereceived', e => { try { result.received.push(JSON.parse(String(e.payload)) as Frame); } catch { /* non-JSON */ } });
  });
  return result;
}

async function currentResult(page: Page): Promise<HandResultEntry | null> {
  return page.evaluate(() => (window as unknown as { game?: {
    client: { result: { get(key: string): HandResultEntry | null } };
  } }).game?.client.result.get('current') ?? null);
}

function observedSettlement(
  current: HandResultEntry | null, complete: boolean, received: readonly Frame[],
): HandResultEntry | null {
  if (current !== null) return current;
  if (!complete) return null;
  // A final hand is not held for acknowledgements. Preserve its actual server
  // result frame instead of requiring polling to catch a transient singleton.
  for (let i = received.length - 1; i >= 0; i--) {
    const entries = received[i].entries ?? [];
    for (let j = entries.length - 1; j >= 0; j--) {
      const [kind, key, value] = entries[j];
      if (kind === 'result' && key === 'current' && value !== null) {
        return value as unknown as HandResultEntry;
      }
    }
  }
  return null;
}

async function readSettlement(page: Page, received: readonly Frame[]): Promise<HandResultEntry | null> {
  const snapshot = await page.evaluate(() => {
    const c = (window as unknown as { game: { client: {
      result: { get(key: string): HandResultEntry | null };
      gameComplete: { get(key: string): { isComplete?: boolean; IsComplete?: boolean; isGameComplete?: boolean; IsGameComplete?: boolean } | null };
    } } }).game.client;
    const done = c.gameComplete.get('current');
    return { result: c.result.get('current'),
      complete: done?.isComplete ?? done?.IsComplete ?? done?.isGameComplete ?? done?.IsGameComplete ?? false };
  });
  return observedSettlement(snapshot.result, snapshot.complete, received);
}

async function signature(actor: Actor): Promise<string> {
  return actor.page.evaluate(() => {
    const client = (window as unknown as { game: { client: {
      seat: number | null;
      things: { entries(): Iterable<[string | number, { slotName?: string }]> };
      turn: { get(key: string): unknown };
      claim: { get(key: string): unknown };
      result: { get(key: string): unknown };
    } } }).game.client;
    return JSON.stringify({
      hand: [...client.things.entries()].filter(([, tile]) => tile.slotName?.startsWith('hand.') && tile.slotName.endsWith('@' + client.seat)),
      turn: client.turn.get('current'), claim: client.claim.get(String(client.seat)), result: client.result.get('current'),
    });
  });
}

async function board(page: Page): Promise<unknown> {
  return page.evaluate(() => {
    const client = (window as unknown as { game: { client: {
      things: { entries(): Iterable<[string | number, unknown]> }; match: { get(key: number): unknown };
    } } }).game.client;
    return {
      things: [...client.things.entries()].sort(([a], [b]) => String(a).localeCompare(String(b))),
      match: client.match.get(0),
    };
  });
}

async function stalledHandEvidence(actors: Actor[]): Promise<unknown> {
  return Promise.all(actors.map(async actor => ({
    label: actor.label,
    state: await actor.page.evaluate(() => {
      const c = (window as unknown as { game: { client: {
        seat: number | null; playerId(): string;
        things: { entries(): Iterable<[string | number, { slotName?: string }]> };
        turn: { get(key: string): unknown }; pickup: { get(key: string): unknown };
        claim: { get(key: string): unknown }; ownTurn: { get(key: number): unknown };
        result: { get(key: string): unknown }; actionRejected: { get(key: string): unknown };
      } } }).game.client;
      const handCounts = [0, 0, 0, 0], meldCounts = [0, 0, 0, 0];
      for (const [, tile] of c.things.entries()) {
        const match = /^(hand|meld)\.[^@]*@([0-3])$/.exec(tile.slotName ?? '');
        if (match) (match[1] === 'hand' ? handCounts : meldCounts)[Number(match[2])]++;
      }
      return { seat: c.seat, playerId: c.playerId(), handCounts, meldCounts,
        turn: c.turn.get('current'), pickup: c.pickup.get('current'),
        claim: c.claim.get(String(c.seat)), ownTurn: c.seat === null ? null : c.ownTurn.get(c.seat),
        result: c.result.get('current'), rejection: c.actionRejected.get('current') };
    }),
    sent: actor.sent,
    received: actor.received,
  })));
}

async function configure(actor: Actor, hands: 1 | 4, seed: number): Promise<void> {
  await actor.page.locator(`#lobby-hand-count-fieldset label:has(input[value="${hands}"])`).click();
  await actor.page.locator('#lobby-advanced summary').click();
  await actor.page.locator('#lobby-seed').fill(String(seed));
}

interface DiscardReadiness {
  connected: boolean;
  seat: number | null;
  activeSeat: number | null;
  phase: string | null;
  awaitingDiscard: boolean;
  concealed: number;
  melds: number;
}

function legalNormalDiscard(state: DiscardReadiness): boolean {
  return state.connected && state.seat !== null && state.activeSeat === state.seat
    && state.phase === 'AwaitingDiscard' && state.awaitingDiscard
    && state.concealed + 3 * state.melds === 14;
}

async function discardReadiness(page: Page): Promise<DiscardReadiness> {
  return page.evaluate(() => {
    const client = (window as unknown as { game: { client: {
      connected(): boolean; seat: number | null;
      turn: { get(key: string): { activeSeat: number | null; phase: string; awaitingDiscard: boolean } | null };
      things: { entries(): Iterable<[string | number, { slotName?: string } | null]> };
    } } }).game.client;
    const seat = client.seat, turn = client.turn.get('current');
    let concealed = 0;
    const melds = new Set<string>();
    for (const [, tile] of client.things.entries()) {
      const hand = /^hand\.\d+@([0-3])$/.exec(tile?.slotName ?? '');
      if (hand && Number(hand[1]) === seat) concealed++;
      const meld = /^meld\.(\d+)\.\d+@([0-3])$/.exec(tile?.slotName ?? '');
      if (meld && Number(meld[2]) === seat) melds.add(meld[1]);
    }
    return { connected: client.connected(), seat, activeSeat: turn?.activeSeat ?? null,
      phase: turn?.phase ?? null, awaitingDiscard: turn?.awaitingDiscard === true,
      concealed, melds: melds.size };
  });
}

test('normal-play driver requires an authoritative14-effective hand, not only a phase flag', () => {
  const ready: DiscardReadiness = {
    connected: true, seat: 0, activeSeat: 0, phase: 'AwaitingDiscard',
    awaitingDiscard: true, concealed: 14, melds: 0,
  };
  expect(legalNormalDiscard(ready)).toBe(true);
  expect(legalNormalDiscard({ ...ready, concealed: 13 })).toBe(false);
  expect(legalNormalDiscard({ ...ready, concealed: 11, melds: 1 })).toBe(true);
  expect(legalNormalDiscard({ ...ready, concealed: 10, melds: 1 })).toBe(false);
  expect(legalNormalDiscard({ ...ready, concealed: 8, melds: 2 })).toBe(true);
  expect(legalNormalDiscard({ ...ready, seat: null })).toBe(false);
  expect(legalNormalDiscard({ ...ready, activeSeat: 1 })).toBe(false);
  expect(legalNormalDiscard({ ...ready, phase: 'AwaitingDraw' })).toBe(false);
  expect(legalNormalDiscard({ ...ready, awaitingDiscard: false })).toBe(false);
  expect(legalNormalDiscard({ ...ready, connected: false })).toBe(false);
});

test('terminal observation requires GameComplete and a real received result, never an invented fallback', () => {
  const result: HandResultEntry = { winner: 2, type: 'Hu', score: [], hand: [1, 2], nextBanker: 2, continuation: null };
  const received: Frame[] = [
    { type: 'UPDATE', entries: [['result', 'current', result as unknown as Record<string, unknown>]] },
    { type: 'UPDATE', entries: [['result', 'current', null], ['gameComplete', 'current', { isComplete: true }]] },
  ];
  expect(observedSettlement(null, false, received)).toBeNull();
  expect(observedSettlement(null, true, [])).toBeNull();
  expect(observedSettlement(null, true, received)).toBe(result);
  expect(observedSettlement(result, false, [])).toBe(result);
});

/** Bounded one-hand play, using only visible legal Pass/discard controls. */
async function playOneHand(actors: Actor[], observed: { received: Frame[] }): Promise<HandResultEntry> {
  const deadline = Date.now() + 140_000;
  let discards = 0;
  const passedWindows = new Map<Actor, string>();
  while (Date.now() < deadline) {
    const result = await readSettlement(actors[0].page, observed.received);
    if (result !== null) return result;
    let acted = false;
    for (const actor of actors) {
      const settled = await currentResult(actor.page);
      if (settled !== null) return settled;
      const state = await probe(actor);
      const claimKey = await actor.page.evaluate(() => {
        const client = (window as unknown as { game: { client: {
          seat: number | null; claim: { get(key: string): unknown };
        } } }).game.client;
        const claim = client.claim.get(String(client.seat));
        return claim === null ? null : JSON.stringify(claim);
      });
      const pass = actor.page.locator('.ferro-claim-overlay-visible .ferro-claim-pass');
      if (claimKey !== null && await pass.isVisible()) {
        if (passedWindows.get(actor) === claimKey) continue;
        try { await pass.click({ timeout: 1200 }); }
        catch {
          const completed = await readSettlement(actors[0].page, observed.received);
          if (completed !== null) return completed;
          continue; // A genuine server claim deadline may close before the press.
        }
        // Pass does not promise an immediate phase change: other players may
        // still respond until this server-advertised window expires.
        passedWindows.set(actor, claimKey);
        acted = true;
      } else if (state.seat !== null && state.turn?.activeSeat === state.seat && state.turn.awaitingDiscard) {
        // A predraw phase-only snapshot can still hold13 effective tiles.
        // Wait for the authoritative legal shape; this does not fix or mask
        // the separately reported runtime race or extend the hand budget.
        if (!legalNormalDiscard(await discardReadiness(actor.page))) continue;
        // Coordinate presses do not get a DOM locator's actionability wait:
        // the copied-invite toast must clear the actual board before clicking.
        await expect.poll(async () => (await mobileGeometry(actor.page)).tiles.at(-1)?.hit,
          { message: 'the rendered discard target must be unobscured and raycastable' }).toBe(true);
        const layout = await mobileGeometry(actor.page);
        const tile = layout.tiles.at(-1)!;
        expect(tile.hit, JSON.stringify({ seat: state.seat, tile, chrome: layout.chrome })).toBe(true);
        const before = await signature(actor);
        try { await actor.page.mouse.click(tile.x, tile.y); }
        catch (error) {
          const completed = await readSettlement(actors[0].page, observed.received);
          if (completed !== null) return completed;
          if (await signature(actor) === before) throw error;
          continue; // The server advanced while the ordinary press was queued.
        }
        await expect.poll(() => signature(actor)).not.toBe(before);
        expect(++discards, 'one wall cannot require an unbounded human-play campaign').toBeLessThanOrEqual(48);
        acted = true;
      }
    }
    if (!acted) await actors[0].page.waitForTimeout(80);
  }
  throw new Error(`A normal one-hand play did not settle; last state=${await signature(actors[0])}`);
}

function acknowledgements(frames: Frame[]): Frame[] {
  return frames.filter(frame => frame.entries?.some(([kind]) => kind === 'handResultAck'));
}

async function save(testInfo: TestInfo, name: string, value: unknown, page: Page): Promise<void> {
  const json = testInfo.outputPath(name + '.json');
  mkdirSync(dirname(json), { recursive: true });
  writeFileSync(json, JSON.stringify(value, null, 2));
  await page.screenshot({ path: testInfo.outputPath(name + '.png') });
}

for (const dealMode of ['auto', 'manual'] as const) {
test(`real bot win/draw (${dealMode}) is held through backdrop, Escape, metadata and reconnect until one owner Continue`, async ({ browser, baseURL }, testInfo) => {
  test.setTimeout(180_000);
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'result-owner', context);
    const transport = wire(actor.page);
    await configure(actor, 4, 4100);
    await applyRoom(actor, 3, 0, dealMode);
    await closeLobby(actor);
    if (dealMode === 'manual') await driveManualCeremony([actor]);
    let result: HandResultEntry;
    try {
      result = await playOneHand([actor], transport);
    } catch (error) {
      await save(testInfo, 'unsettled-hand-observation', {
        error: String(error), observed: await stalledHandEvidence([actor]), transport,
      }, actor.page);
      throw error;
    }
    expect(result.continuation?.waitingSeats).toEqual([0]);
    await expect(actor.page.getByTestId('hand-result-dialog')).toBeVisible();
    await expect(actor.page.locator('#result-score tbody tr')).toHaveCount(4);
    await expect(actor.page.locator('#result-winner')).not.toBeEmpty();
    await expect(actor.page.getByTestId('hand-result-continue')).toHaveText('Continue');
    await assertResultTileArtwork(actor.page, result.hand);
    if (dealMode === 'auto') {
      for (const [name, viewport] of [
        ['desktop', { width: 1280, height: 900 }],
        ['portrait', { width: 390, height: 844 }],
        ['landscape', { width: 844, height: 390 }],
      ] as const) {
        await actor.page.setViewportSize(viewport);
        await assertResultTileArtwork(actor.page, result.hand);
        await expect(actor.page.getByTestId('hand-result-continue')).toBeInViewport({ ratio: 1 });
        await captureResultTileArtwork(actor.page, testInfo, `real-auto-${name}`, 'server-completed-hand', result);
      }
      await actor.page.setViewportSize({ width: 390, height: 844 });
    }
    const held = await board(actor.page);
    await actor.page.keyboard.press('Escape');
    await actor.page.getByTestId('hand-result-dialog').click({ position: { x: 2, y: 2 } });
    await actor.page.waitForTimeout(2500); // Longer than the former immediate bot-driven next deal.
    await expect(actor.page.getByTestId('hand-result-dialog')).toBeVisible();
    expect(await currentResult(actor.page)).toEqual(result);
    expect(await board(actor.page)).toEqual(held);
    expect(acknowledgements(transport.sent)).toHaveLength(0);
    await actor.page.setViewportSize({ width: 844, height: 390 });
    await actor.page.locator('#result-score').scrollIntoViewIfNeeded();
    const landscapeButton = await actor.page.getByTestId('hand-result-continue').boundingBox();
    expect(landscapeButton!.y).toBeGreaterThanOrEqual(0);
    expect(landscapeButton!.y + landscapeButton!.height).toBeLessThanOrEqual(390);
    await save(testInfo, 'held-result-landscape', { result, transport }, actor.page);
    await actor.page.setViewportSize({ width: 390, height: 844 });
    await actor.page.request.get(new URL('/api/games/' + roomId(actor), actor.page.url()).href);
    expect(acknowledgements(transport.sent)).toHaveLength(0);

    await actor.page.reload({ waitUntil: 'domcontentloaded' });
    await dismissPrompts(actor);
    await closeLobby(actor);
    await waitForSeat(actor, 0);
    await expect(actor.page.getByTestId('hand-result-dialog')).toBeVisible();
    expect((await currentResult(actor.page))?.continuation).toEqual(result.continuation);
    expect(acknowledgements(transport.sent)).toHaveLength(0);
    const button = actor.page.getByTestId('hand-result-continue');
    const rect = await button.boundingBox();
    expect(rect).not.toBeNull();
    expect(rect!.y).toBeGreaterThanOrEqual(0);
    expect(rect!.y + rect!.height).toBeLessThanOrEqual(844);
    await save(testInfo, 'held-result-before-continue', { result, held, transport }, actor.page);
    await button.tap();
    await expect.poll(() => acknowledgements(transport.sent).length).toBe(1);
    const { gameId, handNumber, resultToken } = result.continuation!;
    expect(acknowledgements(transport.sent)[0]).toEqual({
      type: 'UPDATE', full: false, entries: [['handResultAck', 'current', { gameId, handNumber, resultToken }]],
    });
    expect(gameId).not.toBe(roomId(actor));
    await expect.poll(() => currentResult(actor.page)).toBeNull();
    await expect(actor.page.getByTestId('hand-result-dialog')).toBeHidden();
    expect(await board(actor.page)).not.toEqual(held);
    await save(testInfo, 'server-advanced-after-continue', { result, transport, after: await board(actor.page) }, actor.page);
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});
}

test('two actual humans must both Continue; an acknowledged reconnect waits and an observer cannot acknowledge', async ({ browser, baseURL }, testInfo) => {
  test.setTimeout(210_000);
  const first = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  const second = await browser.newContext({ viewport: { width: 360, height: 800 }, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  try {
    const owner = await newActor(browser, baseURL, testInfo, 'first-human', first);
    const ownerWire = wire(owner.page);
    await configure(owner, 4, 4101);
    await applyRoom(owner, 2, 0, 'auto');
    const href = await copyJoinLink(owner);
    const peer = await newActor(browser, baseURL, testInfo, 'second-human', second);
    const peerWire = wire(peer.page);
    await followJoinLink(peer, href, roomId(owner));
    await closeLobby(peer);
    const peerSeat = (await probe(peer)).seat;
    expect(peerSeat).not.toBeNull();
    expect(peerSeat).not.toBe(0);
    const result = await playOneHand([owner, peer], ownerWire);
    expect(result.continuation?.waitingSeats).toEqual([0, peerSeat!].sort((a, b) => a - b));
    await expect(peer.page.getByTestId('hand-result-dialog')).toBeVisible();
    const held = await board(owner.page);

    const observer = await newActor(browser, baseURL, testInfo, 'duplicate-observer', first);
    expect(observer.id).toBe(owner.id);
    const observerWire = wire(observer.page);
    await observer.page.goto(href, { waitUntil: 'domcontentloaded' });
    await dismissPrompts(observer);
    await closeLobby(observer);
    await expect.poll(async () => (await probe(observer)).connected).toBe(true);
    expect((await probe(observer)).seat).toBeNull();
    await expect(observer.page.getByTestId('hand-result-continue')).toHaveText('Dismiss');
    await observer.page.getByTestId('hand-result-continue').tap();
    await expect(observer.page.getByTestId('hand-result-dialog')).toBeHidden();
    expect(acknowledgements(observerWire.sent)).toHaveLength(0);
    await observer.page.close();

    await owner.page.getByTestId('hand-result-continue').tap();
    await expect.poll(() => acknowledgements(ownerWire.sent).length).toBe(1);
    await expect(owner.page.getByTestId('hand-result-status')).toContainText('Waiting for the other players');
    await expect(owner.page.getByTestId('hand-result-continue')).toBeDisabled();
    await owner.page.waitForTimeout(1800);
    expect((await currentResult(owner.page))?.continuation?.waitingSeats).toEqual([peerSeat]);
    expect(await board(owner.page)).toEqual(held);
    await owner.page.reload({ waitUntil: 'domcontentloaded' });
    await dismissPrompts(owner);
    await closeLobby(owner);
    await expect(owner.page.getByTestId('hand-result-status')).toContainText('Waiting for the other players');
    await expect(owner.page.getByTestId('hand-result-continue')).toBeDisabled();
    expect(acknowledgements(ownerWire.sent)).toHaveLength(1);
    await save(testInfo, 'waiting-for-second-owner', { result, ownerWire, peerWire, held }, owner.page);
    await peer.page.getByTestId('hand-result-continue').tap();
    await expect.poll(() => acknowledgements(peerWire.sent).length).toBe(1);
    await expect.poll(() => currentResult(owner.page)).toBeNull();
    await expect.poll(() => currentResult(peer.page)).toBeNull();
    expect(await board(owner.page)).not.toEqual(held);
    await save(testInfo, 'both-owners-acknowledged', { result, ownerWire, peerWire }, owner.page);
    expect([...owner.errors, ...peer.errors, ...observer.errors]).toEqual([]);
  } finally { await first.close(); await second.close(); }
});

test('terminal hand keeps authoritative GameComplete and the existing final New Game without an ack', async ({ browser, baseURL }, testInfo) => {
  test.setTimeout(180_000);
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'final-hand', context);
    const transport = wire(actor.page);
    await configure(actor, 1, 4100);
    const originalRoom = await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    const result = await playOneHand([actor], transport);
    expect(result.continuation).toBeNull();
    await expect(actor.page.locator('#game-complete-modal')).toBeVisible();
    await expect(actor.page.getByTestId('hand-result-dialog')).toBeHidden();
    expect(acknowledgements(transport.sent)).toHaveLength(0);
    await save(testInfo, 'terminal-result-no-ack', { result, transport }, actor.page);
    await actor.page.locator('#game-complete-new-game').tap();
    await actor.page.waitForURL(url => url.searchParams.get('gameId') !== originalRoom, { waitUntil: 'domcontentloaded' });
    expect(acknowledgements(transport.sent)).toHaveLength(0);
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});

// Delay original lazy-module delivery, not application bytes or game state.
import { randomUUID } from 'node:crypto';
import { test, expect, type Page, type TestInfo } from '@playwright/test';

interface StartupThing {
  slot?: { group: string; seat: number | null; thing: unknown } | null;
}

interface StartupGame {
  gameUi: unknown;
  client: {
    connected(): boolean;
    seat: number | null;
    turn: { get(key: string): { phase?: string } | null };
    pickup: { get(key: string): { seatIndex: number; count: number } | null };
    match: { get(key: number): { dealer: number } | null };
  };
  world: { things: Map<number, StartupThing> };
}

interface StartupState {
  connected: boolean;
  uiInstalled: boolean;
  seat: number | null;
  phase: string | null;
  dealer: number | null;
  pickupSeat: number | null;
  pickupCount: number | null;
  ownHand: number;
}

interface UiDelayGate {
  requested(): boolean;
  release(): void;
}

async function readState(page: Page): Promise<StartupState> {
  return page.evaluate(() => {
    const game = (window as unknown as { game?: StartupGame }).game;
    const client = game?.client;
    const seat = client?.seat ?? null;
    const pickup = client?.pickup.get('current') ?? null;
    const ownHand = Array.from(game?.world.things.values() ?? []).filter((thing) =>
      thing.slot?.group === 'hand' && thing.slot.seat === seat && thing.slot.thing === thing,
    ).length;
    return {
      connected: !!client?.connected(),
      uiInstalled: !!game?.gameUi,
      seat,
      phase: client?.turn.get('current')?.phase ?? null,
      dealer: client?.match.get(0)?.dealer ?? null,
      pickupSeat: pickup?.seatIndex ?? null,
      pickupCount: pickup?.count ?? null,
      ownHand,
    };
  });
}

async function skipOnboarding(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('mahjong.tour.completed.v1', 'true');
    localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
  });
}

function manualUrl(baseURL: string | undefined): string {
  const url = new URL(baseURL ?? 'http://localhost:8080/autotable/');
  url.search = new URLSearchParams({
    variant: 'changsha', gameId: `late-ui-${randomUUID()}`, seat: '0',
    dealMode: 'manual', botCount: '3', botDifficulty: 'Medium', handCount: '4', seed: '94209',
  }).toString();
  return url.href;
}

async function waitForAuthority(page: Page, phase: string): Promise<void> {
  await expect.poll(async () => {
    const state = await readState(page);
    return state.connected && state.seat === 0 && state.dealer === 0 && state.phase === phase;
  }, { timeout: 30_000 }).toBe(true);
}

async function delayUi(page: Page): Promise<UiDelayGate> {
  let requested = false;
  let release!: () => void;
  const released = new Promise<void>((resolve) => { release = resolve; });
  await page.route('**/scene-effects.*.js', async (route) => {
    requested = true;
    await released;
    await route.continue();
  });
  return { requested: () => requested, release };
}

async function releaseUi(page: Page, gate: UiDelayGate, testInfo: TestInfo): Promise<void> {
  await expect.poll(gate.requested).toBe(true);
  const before = await readState(page);
  expect(before.uiInstalled, 'The authoritative snapshot must precede lazy UI installation').toBe(false);
  await testInfo.attach('authority-before-ui', { body: JSON.stringify(before), contentType: 'application/json' });
  gate.release();
  await expect.poll(async () => (await readState(page)).uiInstalled, { timeout: 15_000 }).toBe(true);
  await testInfo.attach('authority-after-ui', { body: JSON.stringify(await readState(page)), contentType: 'application/json' });
  await page.screenshot({ path: testInfo.outputPath('late-ui-controls.png') });
}

async function activate(page: Page, selector: string, testInfo: TestInfo): Promise<void> {
  const control = page.locator(selector);
  await expect(control).toBeVisible();
  await expect(control).toBeEnabled();
  if (testInfo.project.name === 'mobile-chrome') await control.tap();
  else await control.click();
}

function observeTakes(page: Page): unknown[] {
  const takes: unknown[] = [];
  page.on('websocket', (ws) => ws.on('framesent', (event) => {
    try {
      const message = JSON.parse(String(event.payload)) as { entries?: unknown[] };
      for (const entry of message.entries ?? []) {
        if (Array.isArray(entry) && entry[0] === 'pickup' && entry[1] === 'take') takes.push(entry[2]);
      }
    } catch { /* Ignore unrelated transport frames. */ }
  }));
  return takes;
}

async function rollToPickup(page: Page, testInfo: TestInfo): Promise<void> {
  await activate(page, '#roll-dice', testInfo);
  await waitForAuthority(page, 'BreakPointMarked');
  await expect.poll(async () => {
    const state = await readState(page);
    return state.pickupSeat === 0 && state.pickupCount === 4;
  }).toBe(true);
  await expect(page.locator('#roll-dice')).toBeHidden();
}

async function takeFirstBatch(page: Page, takes: unknown[], testInfo: TestInfo): Promise<void> {
  expect(takes, 'Hydrating UI must not auto-take for the human').toEqual([]);
  expect((await readState(page)).ownHand).toBe(0);
  await expect(page.locator('#pickup-hud')).toBeVisible();
  await expect(page.locator('#pickup-take-count')).toHaveText('4');
  await activate(page, '#pickup-take-btn', testInfo);
  await expect.poll(() => takes).toEqual([{ seatIndex: 0, count: 4 }]);
  await expect.poll(async () => (await readState(page)).ownHand, { timeout: 15_000 }).toBe(4);
}

test.describe('Changsha late UI — authoritative Roll and pickup hydration', () => {
  // Keep lazy-module requests observable even when a prior page installed the PWA.
  test.use({ serviceWorkers: 'block' });

  test('normal startup retains real Roll and human pickup controls', async ({ page, baseURL }, testInfo) => {
    test.setTimeout(60_000);
    const takes = observeTakes(page);
    await skipOnboarding(page);
    await page.goto(manualUrl(baseURL), { waitUntil: 'domcontentloaded' });
    await waitForAuthority(page, 'RollingDice');
    await rollToPickup(page, testInfo);
    await takeFirstBatch(page, takes, testInfo);
  });

  test('a RollingDice snapshot received before lazy UI still offers a real Roll', async ({ page, baseURL }, testInfo) => {
    test.setTimeout(60_000);
    const takes = observeTakes(page);
    await skipOnboarding(page);
    const gate = await delayUi(page);
    try {
      await page.goto(manualUrl(baseURL), { waitUntil: 'domcontentloaded' });
      await waitForAuthority(page, 'RollingDice');
      await releaseUi(page, gate, testInfo);
      await rollToPickup(page, testInfo);
      await takeFirstBatch(page, takes, testInfo);
    } finally {
      gate.release();
    }
  });

  test('owner reconnect hydrates an existing pickup without resurrecting Roll', async ({ page, baseURL }, testInfo) => {
    test.setTimeout(60_000);
    const takes = observeTakes(page);
    await skipOnboarding(page);
    const url = manualUrl(baseURL);
    await page.goto(url, { waitUntil: 'domcontentloaded' });
    await waitForAuthority(page, 'RollingDice');
    await rollToPickup(page, testInfo);

    const gate = await delayUi(page);
    try {
      await page.reload({ waitUntil: 'domcontentloaded' });
      await waitForAuthority(page, 'BreakPointMarked');
      expect(new URL(page.url()).searchParams.get('gameId')).toBe(new URL(url).searchParams.get('gameId'));
      const cached = await readState(page);
      expect(cached.pickupSeat).toBe(0);
      expect(cached.pickupCount).toBe(4);
      await releaseUi(page, gate, testInfo);
      await expect(page.locator('#roll-dice')).toBeHidden();
      await takeFirstBatch(page, takes, testInfo);
    } finally {
      gate.release();
    }
  });
});

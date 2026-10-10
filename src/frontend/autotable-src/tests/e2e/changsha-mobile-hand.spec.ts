import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { newActor, applyRoom, closeLobby, dismissPrompts, probe, driveManualCeremony } from './_lobby-repair';
import { mobileGeometry, setHandOrder, waitForBoardHand } from './_mobile-geometry';

type Frame = { type?: string; entries?: Array<[string, string | number, { slotName?: string; tileId?: number } | null]> };
type Bounds = { left: number; top: number; right: number; bottom: number };
const overlap = (a: Bounds, b: Bounds): boolean =>
  Math.min(a.right, b.right) > Math.max(a.left, b.left)
  && Math.min(a.bottom, b.bottom) > Math.max(a.top, b.top);

function saveEvidence(filename: string, value: unknown): void {
  mkdirSync(dirname(filename), { recursive: true });
  writeFileSync(filename, JSON.stringify(value, null, 2));
}

async function oneHand(page: Page): Promise<void> {
  // Card radios are visually hidden; click their real visible label, not a forced input.
  await page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
  await expect(page.locator('input[name="lobby-hand-count"][value="1"]')).toBeChecked();
}

interface TableSnapshot {
  wire: Array<[string | number, unknown]>;
  slots: Array<{ id: number; slot: string; occupant: number | undefined; sent: boolean }>;
}

async function snapshot(page: Page): Promise<TableSnapshot> {
  return page.evaluate(() => {
    const g = (window as unknown as { game: {
      client: { things: { entries(): Iterable<[string | number, unknown]> } };
      world: { things: Map<number, { slot: { name: string; thing?: { index: number } }; sent: boolean }> };
    } }).game;
    return {
      wire: [...g.client.things.entries()],
      slots: [...g.world.things].map(([id, tile]) => ({ id, slot: tile.slot.name, occupant: tile.slot.thing?.index, sent: tile.sent })),
    };
  });
}

async function viewMode(page: Page, perspective: boolean): Promise<void> {
  await page.getByTestId('settings-button').click();
  await page.getByTestId('settings-tab-display').click();
  await page.getByTestId('settings-perspective-toggle').setChecked(perspective);
  await page.getByTestId('settings-close').click();
  await expect.poll(async () => (await mobileGeometry(page)).camera).toBe(perspective ? 'PerspectiveCamera' : 'OrthographicCamera');
}

function assertLayout(layout: Awaited<ReturnType<typeof mobileGeometry>>, own = true): void {
  expect(layout.canvas.width / layout.width).toBeGreaterThan(0.98);
  expect(layout.canvas.height / layout.height).toBeGreaterThan(0.98);
  expect(layout.pageWidth).toBeLessThanOrEqual(layout.width);
  expect(layout.pageHeight).toBeLessThanOrEqual(layout.height);
  expect(layout.table, 'the actual table mesh must be included in the fit').not.toBeNull();
  for (const r of [...layout.meshes, layout.table!]) {
    expect(r.left, JSON.stringify(r)).toBeGreaterThanOrEqual(0);
    expect(r.right, JSON.stringify(r)).toBeLessThanOrEqual(layout.width);
    expect(r.top, JSON.stringify(r)).toBeGreaterThanOrEqual(0);
    expect(r.bottom, JSON.stringify(r)).toBeLessThanOrEqual(layout.height);
  }
  for (const id of ['new-game', 'lobby-toggle', 'settings-button', 'settings-toggle', 'move-log-toggle']) {
    const r = layout.chrome[id];
    if (!r && id === 'move-log-toggle' && layout.width > 900) continue;
    expect(r, id).toBeTruthy();
    expect(r.hit, `${id} must receive an ordinary press`).toBe(true);
    expect(r.left, id).toBeGreaterThanOrEqual(0);
    expect(r.top, id).toBeGreaterThanOrEqual(0);
    expect(r.right, id).toBeLessThanOrEqual(layout.width);
    expect(r.bottom, id).toBeLessThanOrEqual(layout.height);
  }
  if (!own) {
    expect(layout.tiles).toHaveLength(0);
    expect(layout.ownMeshCount).toBe(0);
    return;
  }
  expect(layout.tiles.length).toBeGreaterThanOrEqual(11);
  expect(new Set(layout.tiles.map(tile => tile.id)).size).toBe(layout.tiles.length);
  expect(layout.ownMeshCount).toBe(layout.tiles.length);
  // Compact framing reserves the enlarged on-board hand below the felt.
  // Measure those submitted meshes together, not the now-smaller felt alone.
  const content = [layout.table!, ...layout.tiles];
  const bounds = {
    left: Math.min(...content.map(r => r.left)), right: Math.max(...content.map(r => r.right)),
    top: Math.min(...content.map(r => r.top)), bottom: Math.max(...content.map(r => r.bottom)),
  };
  expect(bounds.left).toBeGreaterThanOrEqual(layout.playArea.left - .01);
  expect(bounds.right).toBeLessThanOrEqual(layout.playArea.right + .01);
  expect(bounds.top).toBeGreaterThanOrEqual(layout.playArea.top - .01);
  expect(bounds.bottom).toBeLessThanOrEqual(layout.playArea.bottom + .01);
  expect(Math.max((bounds.right - bounds.left) / (layout.playArea.right - layout.playArea.left),
    (bounds.bottom - bounds.top) / (layout.playArea.bottom - layout.playArea.top)),
  'the real table and owned-hand meshes use the limiting free dimension').toBeGreaterThan(.85);
  const compact = layout.width <= 900 || layout.height <= 520;
  for (const tile of layout.tiles) {
    expect(tile.hit, `tile ${tile.id} body center must raycast its physical ID`).toBe(true);
    // Guard measured on-board legibility as well as hit testing; screenshots
    // remain the visual acceptance check, not this internal size threshold.
    expect(tile.width).toBeGreaterThanOrEqual(compact ? 18 : 30);
    expect(tile.height).toBeGreaterThanOrEqual(compact ? 27 : 44);
    for (const id of ['bot-banner', 'turn-banner', 'chat-panel', 'pickup-hud']) {
      const r = layout.chrome[id];
      if (r) expect(overlap(tile, r), `${id} covers tile ${tile.id}`).toBe(false);
    }
  }
  expect(overlap(layout.chrome['turn-banner'], layout.chrome['lobby-toggle']), 'lobby does not cover turn text').toBe(false);
}

test('real private table fits projected geometry and on-board hand across runtime rotations', async ({ browser, baseURL }, testInfo) => {
  test.setTimeout(90_000); // Twelve real camera/viewport combinations in one unchanged hand.
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'mobile-fit', context);
    await oneHand(actor.page);
    await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    await expect.poll(async () => (await probe(actor)).handCount).toBe(14);
    await waitForBoardHand(actor.page);
    await expect(actor.page.locator('#own-hand-tray, #hand-sort, .hand-tile')).toHaveCount(0);
    await expect(actor.page.locator('#bot-banner .bot-banner-summary')).toHaveText('1 human · 3 bots · 0 open');
    const reports = [];
    for (const [width, height] of [[390, 844], [360, 800], [844, 390], [780, 1180], [820, 1180], [1280, 900]]) {
      await actor.page.setViewportSize({ width, height });
      for (const perspective of [true, false]) {
        await viewMode(actor.page, perspective);
        const layout = await mobileGeometry(actor.page);
        reports.push(layout);
        const name = `${width}x${height}-${perspective ? 'perspective' : 'flat'}`;
        const image = testInfo.outputPath(`${name}.png`);
        await actor.page.screenshot({ path: image });
        await testInfo.attach(name, { path: image, contentType: 'image/png' });
        saveEvidence(testInfo.outputPath(`${name}.json`), layout);
        assertLayout(layout);
      }
    }
    await testInfo.attach('actual-mesh-and-hand-bounds', { body: JSON.stringify(reports, null, 2), contentType: 'application/json' });
    saveEvidence(testInfo.outputPath('mesh-measurements.json'), reports);
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});

for (const viewport of [{ width: 360, height: 800 }, { width: 844, height: 390 }]) {
for (const mode of ['groups', 'suit'] as const) {
test(`${viewport.width}x${viewport.height} ${mode} sorting persists and a real board tap discards the displayed ID`, async ({ browser, baseURL }, testInfo) => {
  test.setTimeout(60_000); // Two DPR-2 cold starts plus the authoritative discard round trip.
  const context = await browser.newContext({ viewport, hasTouch: true, isMobile: true, deviceScaleFactor: 2 });
  const sent: Frame[] = [], received: Frame[] = [];
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'mobile-sort-tap', context);
    await oneHand(actor.page);
    await actor.page.locator('#lobby-advanced summary').click();
    await actor.page.locator('#lobby-seed').fill('4100');
    actor.page.on('websocket', socket => {
      socket.on('framesent', e => { try { sent.push(JSON.parse(String(e.payload)) as Frame); } catch { /* non-JSON */ } });
      socket.on('framereceived', e => { try { received.push(JSON.parse(String(e.payload)) as Frame); } catch { /* non-JSON */ } });
    });
    await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    await waitForBoardHand(actor.page);
    await setHandOrder(actor.page, mode === 'suit' ? 'groups' : 'suit');
    const initial = await snapshot(actor.page);
    const initialTiles = (await mobileGeometry(actor.page)).tiles;
    const start = sent.length;
    await setHandOrder(actor.page, mode);
    const expectedOrder = initialTiles.slice().sort((a, b) => {
        const count = (face: number): number => initialTiles.filter(tile => tile.face === face).length;
        return (mode === 'groups' ? Number(count(b.face) > 1) - Number(count(a.face) > 1) : 0)
          || a.face - b.face || a.id - b.id;
      }).map(tile => tile.id);
    expect(expectedOrder, 'seeded hand must actually change order, not merely change a dropdown').not.toEqual(initialTiles.map(tile => tile.id));
    await expect.poll(async () => (await mobileGeometry(actor.page)).tiles.map(tile => tile.id)).toEqual(expectedOrder);
    expect(await snapshot(actor.page)).toEqual(initial);
    expect(sent.slice(start).flatMap(frame => frame.entries ?? []).filter(([kind]) =>
      ['things', 'slots', 'discard', 'claim', 'match', 'seats'].includes(kind))).toEqual([]);
    await actor.page.getByTestId('settings-button').click();
    await actor.page.getByTestId('settings-tab-display').click();
    await expect(actor.page.getByTestId('settings-hand-sort')).toHaveValue(mode);
    await expect(actor.page.getByTestId('settings-hand-sort')).toHaveAttribute('aria-describedby', 'settings-hand-sort-hint');
    await actor.page.getByTestId('settings-close').click();
    await actor.page.reload({ waitUntil: 'domcontentloaded' });
    await dismissPrompts(actor);
    await closeLobby(actor);
    // DOM readiness precedes the lazy WebGL/effects chunks on a DPR-2 reload.
    await expect(actor.page.locator('body')).toHaveAttribute('data-scene-effects-ready', 'true', { timeout: 30_000 });
    await waitForBoardHand(actor.page);
    await expect.poll(async () => (await mobileGeometry(actor.page)).tiles.map(tile => tile.id)).toEqual(expectedOrder);
    expect(await actor.page.evaluate(() => JSON.parse(localStorage.getItem('mahjong.settings.v1')!).handSort)).toBe(mode);
    expect((await snapshot(actor.page)).wire).toEqual(initial.wire);
    // A sticky mobile hover must not rotate the whole 44px target off-screen.
    await actor.page.getByTestId('settings-button').hover();
    const layout = await mobileGeometry(actor.page);
    await actor.page.screenshot({ path: testInfo.outputPath('board-before-tap.png') });
    assertLayout(layout);
    expect(layout.dpr).toBe(2);
    const chosen = layout.tiles[4];
    const mark = sent.length;
    await actor.page.touchscreen.tap(chosen.x, chosen.y);
    await expect.poll(() => sent.slice(mark).flatMap(frame => frame.entries ?? []).filter(([kind]) => kind === 'discard')).toHaveLength(1);
    expect(sent.slice(mark).flatMap(frame => frame.entries ?? []).find(([kind]) => kind === 'discard')?.[2]?.tileId).toBe(chosen.id);
    await expect.poll(() => received.flatMap(frame => frame.entries ?? []).some(([kind, id, info]) =>
      kind === 'things' && id === chosen.id && info?.slotName?.startsWith('discard.'))).toBe(true);
    await expect.poll(async () => (await probe(actor)).handIds).not.toContain(chosen.id);
    await testInfo.attach('tap-wire-server-confirmation', {
      body: JSON.stringify({ chosen, layout, sent: sent.slice(mark).filter(frame => frame.type === 'UPDATE'),
        confirmed: received.flatMap(frame => frame.entries ?? []).filter(([kind, id]) => kind === 'things' && id === chosen.id) }, null, 2),
      contentType: 'application/json',
    });
    saveEvidence(testInfo.outputPath('tap-confirmation.json'), {
      chosen, layout, sent: sent.slice(mark).filter(frame => frame.type === 'UPDATE'),
      confirmed: received.flatMap(frame => frame.entries ?? []).filter(([kind, id]) => kind === 'things' && id === chosen.id),
    });
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});
}
}

test('desktop sorted mesh raycasts the displayed ID and intentional zoom is not fitted away', async ({ browser, baseURL }, testInfo) => {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'desktop-sort', context);
    await oneHand(actor.page);
    await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    await expect.poll(async () => (await probe(actor)).handCount).toBe(14);
    await actor.page.getByTestId('settings-button').click();
    await expect(actor.page.getByTestId('settings-tab-general')).toHaveAttribute('aria-selected', 'true');
    await actor.page.getByTestId('settings-tab-display').click();
    await actor.page.getByTestId('settings-panel-display').getByRole('button', { name: 'Hand order', exact: true }).click();
    await actor.page.getByRole('option', { name: 'Pairs / triples first', exact: true }).click();
    await actor.page.getByTestId('settings-close').click();
    const initial = await mobileGeometry(actor.page);
    assertLayout(initial);
    expect(initial.ownMeshCount).toBe(14);
    const width = initial.table!.right - initial.table!.left;
    await actor.page.keyboard.down('z');
    await expect.poll(async () => {
      const table = (await mobileGeometry(actor.page)).table!;
      return table.right - table.left;
    }).toBeGreaterThan(width * 1.05);
    await actor.page.keyboard.up('z');
    await expect.poll(async () => {
      const table = (await mobileGeometry(actor.page)).table!;
      return Math.abs(table.right - table.left - width);
    }).toBeLessThan(1);
    const chosen = (await mobileGeometry(actor.page)).tiles[4];
    await actor.page.mouse.move(chosen.x, chosen.y);
    await expect.poll(() => actor.page.evaluate(() =>
      (window as unknown as { game: { world: { hovered?: { index: number } } } }).game.world.hovered?.index,
    )).toBe(chosen.id);
    const start = actor.sent.length;
    await actor.page.mouse.click(chosen.x, chosen.y);
    await expect.poll(() => actor.sent.slice(start).flatMap(frame => frame.entries ?? [])
      .filter(([kind]) => kind === 'discard').map(([, , value]) => value?.tileId)).toEqual([chosen.id]);
    await expect.poll(async () => (await probe(actor)).handIds).not.toContain(chosen.id);
    saveEvidence(testInfo.outputPath('desktop-click-confirmation.json'), { chosen, initial, sent: actor.sent.slice(start) });
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});

for (const seat of [1, 2, 3, -1]) {
  test(`real rotated seat ${seat} fits in both views without sorting anyone else's hand`, async ({ browser, baseURL }, testInfo) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, deviceScaleFactor: 1 });
    try {
      const actor = await newActor(browser, baseURL, testInfo, `seat-${seat}`, context);
      await oneHand(actor.page);
      await applyRoom(actor, seat === -1 ? 4 : 3, seat, 'auto');
      await closeLobby(actor);
      if (seat !== -1) await expect.poll(async () => (await probe(actor)).handCount).toBeGreaterThanOrEqual(13);
      for (const perspective of [true, false]) {
        await viewMode(actor.page, perspective);
        const layout = await mobileGeometry(actor.page);
        assertLayout(layout, seat !== -1);
        await testInfo.attach(`seat-${seat}-${perspective}`, { body: JSON.stringify(layout), contentType: 'application/json' });
        saveEvidence(testInfo.outputPath(`seat-${seat}-${perspective}.json`), layout);
      }
      expect(actor.errors).toEqual([]);
    } finally { await context.close(); }
  });
}

test('ordinary Quick Match manual pickup keeps the growing on-board hand visible', async ({ browser, baseURL }, testInfo) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'manual-board-hand', context);
    await oneHand(actor.page);
    await actor.page.locator('input[name="lobby-deal-mode"][value="manual"]').check();
    await actor.page.getByTestId('lobby-quick-match').click();
    await actor.page.waitForURL(url => url.searchParams.has('gameId'), { waitUntil: 'domcontentloaded' });
    await closeLobby(actor);
    await driveManualCeremony([actor]);
    await waitForBoardHand(actor.page);
    assertLayout(await mobileGeometry(actor.page));
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});

for (const viewport of [
  { width: 320, height: 568 }, { width: 360, height: 800 },
  { width: 844, height: 390 }, { width: 667, height: 320 },
]) {
  test(`mobile gear exposes Perspective view, persists both modes and keeps sorting live ${viewport.width}x${viewport.height}`, async ({ browser, baseURL }, testInfo) => {
    test.setTimeout(60_000);
    const context = await browser.newContext({ viewport, hasTouch: true, isMobile: true, deviceScaleFactor: 1 });
    try {
      const actor = await newActor(browser, baseURL, testInfo, 'mobile-view-settings', context);
      const page = actor.page;
      await oneHand(page);
      await page.locator('#lobby-advanced summary').click();
      await page.locator('#lobby-seed').fill('4100');
      await applyRoom(actor, 3, 0, 'auto');
      await closeLobby(actor);
      await waitForBoardHand(page);
      const original = await snapshot(page);
      const initial = await mobileGeometry(page);
      const suitOrder = initial.tiles.map(tile => tile.id);
      const groupsOrder = initial.tiles.slice().sort((a, b) => {
        const count = (face: number): number => initial.tiles.filter(tile => tile.face === face).length;
        return Number(count(b.face) > 1) - Number(count(a.face) > 1) || a.face - b.face || a.id - b.id;
      }).map(tile => tile.id);
      expect(groupsOrder).not.toEqual(suitOrder);
      const commandStart = actor.sent.length;

      const checkbox = page.getByRole('checkbox', { name: 'Perspective view', exact: true });
      const camera = async (perspective: boolean): Promise<void> => {
        await expect.poll(async () => {
          const layout = await mobileGeometry(page);
          return {
            camera: layout.camera,
            fitted: !!layout.table && layout.table.left >= 0 && layout.table.right <= viewport.width
              && layout.table.top >= 0 && layout.table.bottom <= viewport.height,
          };
        }).toEqual({ camera: perspective ? 'PerspectiveCamera' : 'OrthographicCamera', fitted: true });
        if (perspective) await expect(page.locator('#perspective')).toBeChecked();
        else await expect(page.locator('#perspective')).not.toBeChecked();
        expect(await page.evaluate(() => JSON.parse(localStorage.getItem('mahjong.settings.v1') ?? '{}').perspective)).toBe(perspective);
      };
      await page.getByTestId('settings-button').tap();
      await expect(page.getByTestId('settings-tab-display')).toHaveAttribute('aria-selected', 'true');
      await expect(checkbox).toBeInViewport({ ratio: 1 });
      await expect(checkbox.locator('..')).toBeInViewport({ ratio: 1 });
      await expect(page.getByTestId('settings-panel-display').getByText('Perspective view', { exact: true })).toBeInViewport({ ratio: 1 });
      await expect(page.getByTestId('settings-close')).toBeInViewport({ ratio: 1 });
      await expect(checkbox).toBeChecked();
      await expect(checkbox).toHaveAttribute('aria-describedby', 'settings-perspective-hint');
      await expect(page.locator('#settings-perspective-hint')).toContainText('Uncheck for flat view');
      const row = await checkbox.locator('..').boundingBox();
      expect(row!.height, 'the labeled checkbox row is a real mobile touch target').toBeGreaterThanOrEqual(44);
      await page.screenshot({ path: testInfo.outputPath('perspective-settings.png') });
      await checkbox.tap();
      await expect(checkbox).not.toBeChecked();
      await camera(false);

      const handOrder = page.getByTestId('settings-panel-display').getByRole('button', { name: 'Hand order', exact: true });
      await handOrder.click();
      await page.getByRole('option', { name: 'Pairs / triples first', exact: true }).click();
      await page.getByTestId('settings-close').click();
      await expect.poll(async () => (await mobileGeometry(page)).tiles.map(tile => tile.id)).toEqual(groupsOrder);
      await camera(false);
      const flat = await mobileGeometry(page);
      expect(Math.max(
        Math.abs((flat.table!.bottom - flat.table!.top) - (initial.table!.bottom - initial.table!.top)),
        Math.abs((flat.table!.right - flat.table!.left) - (initial.table!.right - initial.table!.left))),
        'the actual projected board must visibly change, not only the checkbox').toBeGreaterThan(10);
      await page.screenshot({ path: testInfo.outputPath('flat-board.png') });

      await page.getByTestId('settings-button').tap();
      await checkbox.scrollIntoViewIfNeeded();
      await expect(checkbox).toBeInViewport({ ratio: 1 });
      await checkbox.tap();
      await expect(checkbox).toBeChecked();
      await camera(true);
      await page.getByTestId('settings-close').click();
      await expect.poll(async () => (await mobileGeometry(page)).tiles.map(tile => tile.id)).toEqual(groupsOrder);
      expect(await snapshot(page)).toEqual(original);
      expect(actor.sent.slice(commandStart).flatMap(frame => frame.entries ?? [])
        .filter(([kind]) => ['things', 'slots', 'discard', 'claim', 'match', 'seats'].includes(kind))).toEqual([]);

      await page.keyboard.press('p');
      await camera(false);
      await page.getByTestId('settings-button').tap();
      await expect(checkbox).not.toBeChecked();
      await page.getByTestId('settings-close').click();
      await page.reload({ waitUntil: 'domcontentloaded' });
      await dismissPrompts(actor);
      await closeLobby(actor);
      await waitForBoardHand(page);
      await camera(false);
      await expect.poll(async () => (await mobileGeometry(page)).tiles.map(tile => tile.id)).toEqual(groupsOrder);
      await page.getByTestId('settings-button').tap();
      await expect(checkbox).not.toBeChecked();
      await checkbox.tap();
      await camera(true);
      await page.getByTestId('settings-close').click();
      await setHandOrder(page, 'suit');
      await expect.poll(async () => (await mobileGeometry(page)).tiles.map(tile => tile.id)).toEqual(suitOrder);
      const chosen = (await mobileGeometry(page)).tiles[4];
      expect(chosen.hit).toBe(true);
      const mark = actor.sent.length;
      await page.touchscreen.tap(chosen.x, chosen.y);
      await expect.poll(() => actor.sent.slice(mark).flatMap(frame => frame.entries ?? [])
        .filter(([kind]) => kind === 'discard').map(([, , value]) => value?.tileId)).toEqual([chosen.id]);
      await expect.poll(async () => (await probe(actor)).handIds).not.toContain(chosen.id);
      expect(actor.errors).toEqual([]);
    } finally { await context.close(); }
  });
}

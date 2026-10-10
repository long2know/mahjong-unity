import { test, expect, type Page, type TestInfo } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, relative } from 'node:path';
import type { Camera, Vector2, Vector3 } from 'three';
import { newActor, applyRoom, closeLobby, probe } from './_lobby-repair';
import { mobileGeometry, waitForBoardHand } from './_mobile-geometry';

interface Bounds { left: number; top: number; right: number; bottom: number }
interface CameraGame {
  mainView: { camera: Camera; fitCorners: Vector3[]; playArea(): Bounds };
  lookDown: { pos: number };
  zoom: { pos: number };
  mouseUi: { mouse2: Vector2 | null };
  world: {
    hovered: { index: number } | null;
    slots: Map<string, { placeWithOffset(offset: number): { position: Vector3 } }>;
  };
}
interface CameraFrame {
  camera: string; position: number[]; rotation: number[]; scale: number[]; projection: number[];
  lookDown: number; zoom: number; mouse: number[] | null; playArea: Bounds;
  anchors: Array<{ name: string; x: number; y: number }>;
}
const STATIC_ANCHOR_TOLERANCE = 0.25; // CSS pixels, independent of DPR and backing-buffer size.

async function cameraFrame(page: Page): Promise<CameraFrame> {
  return page.evaluate(() => {
    const g = (window as unknown as { game: CameraGame }).game;
    const camera = g.mainView.camera;
    const canvas = document.querySelector('#main canvas')!.getBoundingClientRect();
    const project = (name: string, point: Vector3): { name: string; x: number; y: number } => {
      const p = point.clone().project(camera);
      return { name, x: canvas.left + (p.x + 1) * canvas.width / 2, y: canvas.top + (1 - p.y) * canvas.height / 2 };
    };
    const anchors = g.mainView.fitCorners.map((point, i) => project(`table-${i}`, point));
    const center = g.mainView.fitCorners[0].clone().add(g.mainView.fitCorners[7]).multiplyScalar(.5);
    center.z = 0;
    anchors.push(project('table-center', center));
    for (const [name, slot] of g.world.slots) {
      if (/^wall\.0\.[01]@[0-3]$/.test(name)) anchors.push(project(name, slot.placeWithOffset(0).position));
    }
    return {
      camera: camera.type, position: camera.position.toArray(), rotation: [camera.rotation.x, camera.rotation.y, camera.rotation.z],
      scale: camera.scale.toArray(), projection: camera.projectionMatrix.elements.slice(),
      lookDown: g.lookDown.pos, zoom: g.zoom.pos, mouse: g.mouseUi.mouse2?.toArray() ?? null,
      playArea: g.mainView.playArea(), anchors,
    };
  });
}

function anchorDeviation(before: CameraFrame, after: CameraFrame): number {
  expect(after.anchors.map(anchor => anchor.name)).toEqual(before.anchors.map(anchor => anchor.name));
  return Math.max(...after.anchors.map((anchor, i) => Math.hypot(
    anchor.x - before.anchors[i].x, anchor.y - before.anchors[i].y,
  )));
}

function stationary(before: CameraFrame, after: CameraFrame, stage: string): void {
  expect(after.playArea, `${stage}: incidental pointer state is not structural chrome`).toEqual(before.playArea);
  expect(after.position, `${stage}: camera pose`).toEqual(before.position);
  expect(after.rotation, `${stage}: camera rotation`).toEqual(before.rotation);
  expect(after.lookDown, `${stage}: look-down`).toBe(before.lookDown);
  expect(after.zoom, `${stage}: zoom`).toBe(before.zoom);
  expect(anchorDeviation(before, after), `${stage}: stationary table/wall anchors`).toBeLessThan(STATIC_ANCHOR_TOLERANCE);
}

async function frames(page: Page, count = 3): Promise<void> {
  await page.evaluate(n => new Promise<void>(resolve => {
    const next = (): void => { if (--n <= 0) resolve(); else requestAnimationFrame(next); };
    requestAnimationFrame(next);
  }), count);
}

async function viewMode(page: Page, perspective: boolean): Promise<void> {
  await page.getByTestId('settings-button').click();
  await page.getByTestId('settings-tab-display').click();
  await page.getByTestId('settings-perspective-toggle').setChecked(perspective);
  await page.getByTestId('settings-close').click();
  await expect.poll(async () => (await cameraFrame(page)).camera).toBe(perspective ? 'PerspectiveCamera' : 'OrthographicCamera');
  await frames(page);
}

async function screenshot(page: Page, info: TestInfo, label: string): Promise<void> {
  const filename = relative(process.cwd(), info.outputPath(`${label}.png`));
  await page.screenshot({ path: filename });
  await info.attach(label, { path: filename, contentType: 'image/png' });
}

async function saveEvidence(info: TestInfo, name: string, value: unknown): Promise<void> {
  const filename = relative(process.cwd(), info.outputPath(`${name}.json`));
  mkdirSync(dirname(filename), { recursive: true });
  writeFileSync(filename, JSON.stringify(value, null, 2));
  await info.attach(name, { path: filename, contentType: 'application/json' });
}

test.describe('desktop click camera projection', () => {
  test.beforeEach(async ({}, info) => {
    test.skip(info.project.name !== 'chromium', 'Explicit desktop and compact viewports are covered in the Chromium project.');
  });

  for (const viewport of [{ width: 1280, height: 900 }, { width: 1920, height: 1080 }]) {
    for (const perspective of [true, false]) {
      test(`${viewport.width}x${viewport.height} ${perspective ? 'perspective' : 'flat'} ordinary presses never reset the fitted camera`, async ({ browser, baseURL }, info) => {
        test.setTimeout(60_000);
        const context = await browser.newContext({ viewport, deviceScaleFactor: 1 });
        const evidence: Array<{ stage: string; frame: CameraFrame }> = [];
        try {
          const actor = await newActor(browser, baseURL, info, 'desktop-camera', context);
          const page = actor.page;
          await page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
          await applyRoom(actor, 3, 0, 'auto');
          await closeLobby(actor);
          await expect.poll(async () => (await probe(actor)).handCount).toBe(14);
          await viewMode(page, perspective);
          const initial = await cameraFrame(page);
          const center = initial.anchors.find(anchor => anchor.name === 'table-center')!;
          await page.mouse.move(center.x, center.y);
          await frames(page);
          const before = await cameraFrame(page);
          evidence.push({ stage: 'before', frame: before });
          await screenshot(page, info, 'before');
          await page.mouse.down();
          const down = await cameraFrame(page);
          evidence.push({ stage: 'down', frame: down });
          await screenshot(page, info, 'down');
          stationary(before, down, 'down');
          await page.waitForTimeout(220);
          const held = await cameraFrame(page);
          evidence.push({ stage: 'held', frame: held });
          stationary(before, held, 'held');
          await screenshot(page, info, 'held');
          await page.mouse.up();
          const up = await cameraFrame(page);
          evidence.push({ stage: 'up', frame: up });
          stationary(before, up, 'up');
          await screenshot(page, info, 'up');
          await frames(page);
          const settled = await cameraFrame(page);
          evidence.push({ stage: 'settled', frame: settled });
          stationary(before, settled, 'settled');
          await screenshot(page, info, 'settled');

          await page.mouse.down();
          await page.mouse.move(center.x + 60, center.y + 35, { steps: 5 });
          await frames(page);
          const drag = await cameraFrame(page);
          evidence.push({ stage: 'selection-drag', frame: drag });
          stationary(before, drag, 'selection drag');
          await page.mouse.move(-5, -5);
          await page.mouse.up();
          await frames(page);
          stationary(before, await cameraFrame(page), 'selection cancelled outside the board');

          await page.mouse.move(center.x, center.y);
          await page.keyboard.down('Space');
          await expect.poll(async () => anchorDeviation(before, await cameraFrame(page))).toBeGreaterThan(10);
          await page.keyboard.up('Space');
          await expect.poll(async () => anchorDeviation(before, await cameraFrame(page))).toBeLessThan(STATIC_ANCHOR_TOLERANCE);
          await page.keyboard.down('z');
          await expect.poll(async () => anchorDeviation(before, await cameraFrame(page))).toBeGreaterThan(10);
          await page.keyboard.up('z');
          await expect.poll(async () => anchorDeviation(before, await cameraFrame(page))).toBeLessThan(STATIC_ANCHOR_TOLERANCE);

          await page.setViewportSize({ width: viewport.width - 120, height: viewport.height - 80 });
          await frames(page);
          const resized = await cameraFrame(page);
          expect(anchorDeviation(before, resized), 'a real viewport change must still refit').toBeGreaterThan(10);
          for (const anchor of resized.anchors.filter(anchor => /^table-\d+$/.test(anchor.name))) {
            expect(anchor.x).toBeGreaterThanOrEqual(resized.playArea.left - .01);
            expect(anchor.x).toBeLessThanOrEqual(resized.playArea.right + .01);
            expect(anchor.y).toBeGreaterThanOrEqual(resized.playArea.top - .01);
            expect(anchor.y).toBeLessThanOrEqual(resized.playArea.bottom + .01);
          }
          await page.setViewportSize(viewport);
          await frames(page);
          stationary(before, await cameraFrame(page), 'restored viewport');

          const tile = (await mobileGeometry(page)).tiles[4];
          await page.mouse.move(tile.x, tile.y);
          await expect.poll(() => page.evaluate(() =>
            (window as unknown as { game: CameraGame }).game.world.hovered?.index)).toBe(tile.id);
          await page.evaluate(() => {
            const sample = (): { projection: number[]; area: Bounds } => {
              const g = (window as unknown as { game: CameraGame }).game;
              return { projection: g.mainView.camera.projectionMatrix.elements.slice(), area: g.mainView.playArea() };
            };
            const target = window as unknown as { __tilePressCamera?: { before?: ReturnType<typeof sample>; after?: ReturnType<typeof sample> } };
            target.__tilePressCamera = {};
            document.addEventListener('mousedown', () => { target.__tilePressCamera!.before = sample(); }, { capture: true, once: true });
            document.addEventListener('mousedown', () => { target.__tilePressCamera!.after = sample(); }, { once: true });
          });
          const mark = actor.sent.length;
          await page.mouse.down();
          const tilePress = await page.evaluate(() =>
            (window as unknown as { __tilePressCamera: { before: unknown; after: unknown } }).__tilePressCamera);
          expect(tilePress.after, 'a real tile press must not reset the projection inside input dispatch').toEqual(tilePress.before);
          await page.mouse.up();
          await expect.poll(() => actor.sent.slice(mark).flatMap(frame => frame.entries ?? [])
            .filter(([kind]) => kind === 'discard').map(([, , value]) => value?.tileId)).toEqual([tile.id]);
          await expect.poll(async () => (await probe(actor)).handIds).not.toContain(tile.id);
          await saveEvidence(info, 'physical-discard', { tileId: tile.id, tilePress });
          expect(actor.errors).toEqual([]);
        } finally {
          await saveEvidence(info, 'camera-stages', evidence);
          await context.close();
        }
      });
    }
  }

  for (const viewport of [{ width: 360, height: 800 }, { width: 390, height: 844 }, { width: 844, height: 390 }]) {
    test(`compact ${viewport.width}x${viewport.height} keeps both views and the board hand fitted beside optional panels`, async ({ browser, baseURL }, info) => {
      test.setTimeout(60_000);
      const context = await browser.newContext({ viewport, deviceScaleFactor: 1, hasTouch: true, isMobile: true });
      const evidence = [];
      try {
        const actor = await newActor(browser, baseURL, info, 'compact-camera', context);
        const page = actor.page;
        await page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
        await applyRoom(actor, 3, 0, 'auto');
        await closeLobby(actor);
        await waitForBoardHand(page);
        const initial = await probe(actor);
        const mark = actor.sent.length;
        for (const perspective of [true, false]) {
          await viewMode(page, perspective);
          for (const panel of ['none', 'move-log', 'chat']) {
            if (await page.getByTestId('chat-toggle').getAttribute('aria-expanded') === 'true') {
              await page.getByTestId('chat-toggle').tap();
            }
            if (await page.locator('#move-log-toggle').getAttribute('aria-expanded') === 'true') {
              await page.locator('#move-log-toggle').tap();
            }
            if (panel === 'move-log') await page.locator('#move-log-toggle').tap();
            if (panel === 'chat') await page.getByTestId('chat-toggle').tap();
            await page.waitForTimeout(250);
            const layout = await mobileGeometry(page);
            evidence.push({ perspective, panel, layout });
            expect(layout.tiles).toHaveLength(14);
            expect(layout.ownMeshCount).toBe(14);
            expect(layout.pageWidth).toBeLessThanOrEqual(viewport.width);
            expect(layout.pageHeight).toBeLessThanOrEqual(viewport.height);
            expect(layout.table).not.toBeNull();
            for (const bounds of [layout.table!, ...layout.areas, ...layout.meshes]) {
              expect(bounds.left, `${panel}: projected left`).toBeGreaterThanOrEqual(layout.playArea.left - .5);
              expect(bounds.right, `${panel}: projected right`).toBeLessThanOrEqual(layout.playArea.right + .5);
              expect(bounds.top, `${panel}: projected top`).toBeGreaterThanOrEqual(layout.playArea.top - .5);
              expect(bounds.bottom, `${panel}: projected bottom`).toBeLessThanOrEqual(layout.playArea.bottom + .5);
            }
            for (const tile of layout.tiles) {
              expect(tile.hit, `physical tile ${tile.id} must remain unobscured`).toBe(true);
              expect(tile.width).toBeGreaterThanOrEqual(10);
              expect(tile.height).toBeGreaterThanOrEqual(16);
              expect(tile.left).toBeGreaterThanOrEqual(0);
              expect(tile.right).toBeLessThanOrEqual(viewport.width);
              expect(tile.top).toBeGreaterThanOrEqual(0);
              expect(tile.bottom).toBeLessThanOrEqual(viewport.height);
            }
            if (panel !== 'none') {
              const visible = page.locator(panel === 'chat' ? '#chat-panel' : '#move-log');
              await expect(visible).toBeInViewport({ ratio: 1 });
              const bounds = await visible.boundingBox();
              expect(bounds!.y + bounds!.height < Math.min(...layout.tiles.map(tile => tile.top))
                || bounds!.x > Math.max(...layout.tiles.map(tile => tile.right))).toBe(true);
            }
            await screenshot(page, info, `${perspective ? 'perspective' : 'flat'}-${panel}`);
          }
        }
        expect((await probe(actor)).handIds).toEqual(initial.handIds);
        expect(actor.sent.slice(mark).flatMap(frame => frame.entries ?? [])
          .filter(([kind]) => ['things', 'discard', 'claim', 'match'].includes(kind))).toEqual([]);
        expect(actor.errors).toEqual([]);
      } finally {
        await saveEvidence(info, 'compact-geometry', evidence);
        await context.close();
      }
    });
  }
});

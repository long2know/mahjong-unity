import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import type { Camera, InstancedMesh, Mesh, Vector3 } from 'three';
import type { World } from '../../src/world';
import { newActor, applyRoom, closeLobby, probe } from './_lobby-repair';
import { mobileGeometry, setHandOrder, waitForBoardHand } from './_mobile-geometry';

interface Rect { left: number; top: number; right: number; bottom: number }
interface MotionFrame {
  at: number; document: number; phase: string | null; activeSeat: number | null;
  ownClaim: boolean; area: Rect; anchors: Array<{ x: number; y: number }>;
  camera: string; projection: number[]; pose: number[]; zoom: number; lookDown: number;
  connected: boolean; seat: number | null; viewSeat: number | null; privateMeshes: number;
  bodyClasses: string;
  chrome: Record<string, { bounds: Rect; text: string }>;
}
interface MotionGame {
  mainView: { camera: Camera; fitCorners: Vector3[]; playArea(): Rect };
  world: World;
  objectView: { thingGroups: Map<string, { startIndex: number; meshes: Mesh[]; instancedMesh: InstancedMesh }> };
  zoom: { pos: number }; lookDown: { pos: number };
  client: {
    seat: number | null;
    ws: WebSocket | null; connected(): boolean; serverSnapshotGameId: string | null;
    turn: { get(key: string): { phase: string; activeSeat: number | null } | null };
    claim: { get(key: string): unknown };
  };
}
interface MotionWindow extends Window {
  game: MotionGame;
  __tableMotion: { running: boolean; frames: MotionFrame[] };
}

async function startMeasurement(page: Page, everyFrame = false): Promise<void> {
  await page.evaluate(everyFrame => {
    const win = window as unknown as MotionWindow;
    const recording = { running: true, frames: [] as MotionFrame[] };
    win.__tableMotion = recording;
    const privateIds = win.game.world.ownHandPresentation().tiles.map(tile => tile.id);
    let last = -Infinity;
    const sample = (at: number): void => {
      if (!recording.running) return;
      requestAnimationFrame(sample);
      if ((!everyFrame && at - last < 80) || recording.frames.length >= 2000) return;
      last = at;
      const game = win.game;
      const camera = game.mainView.camera;
      const canvas = document.querySelector('#main canvas')!.getBoundingClientRect();
      const turn = game.client.turn.get('current');
      const tiles = game.objectView.thingGroups.get('TILE')!;
      const matrix = camera.matrix.clone();
      const privateMeshes = privateIds.filter(id => {
        const index = id - tiles.startIndex;
        if (tiles.meshes[index]?.visible) return true;
        tiles.instancedMesh.getMatrixAt(index, matrix);
        return matrix.determinant() !== 0;
      }).length;
      const chrome: MotionFrame['chrome'] = {};
      for (const selector of ['#new-game', '#variant-badge', '#settings-button', '#lobby-toggle',
        '#turn-banner', '#bot-banner', '#pickup-hud',
        '.ferro-claim-overlay-visible', '#sidebar', '#chat-panel', '#move-log']) {
        const element = document.querySelector<HTMLElement>(selector);
        if (!element?.getClientRects().length || getComputedStyle(element).visibility === 'hidden') continue;
        const r = element.getBoundingClientRect();
        chrome[selector] = { bounds: { left: r.left, top: r.top, right: r.right, bottom: r.bottom },
          text: element.textContent?.trim().slice(0, 150) ?? '' };
      }
      recording.frames.push({
        at, document: performance.timeOrigin,
        phase: turn?.phase ?? null, activeSeat: turn?.activeSeat ?? null,
        ownClaim: game.client.claim.get(String(game.client.seat)) !== null,
        connected: game.client.connected(), seat: game.client.seat, viewSeat: game.world.viewSeat, privateMeshes,
        bodyClasses: document.body.className,
        camera: camera.type, projection: camera.projectionMatrix.elements.slice(),
        pose: [...camera.position.toArray(), camera.rotation.x, camera.rotation.y, camera.rotation.z, ...camera.scale.toArray()],
        zoom: game.zoom.pos, lookDown: game.lookDown.pos,
        area: game.mainView.playArea(),
        anchors: game.mainView.fitCorners.map(point => {
          const p = point.clone().project(camera);
          return { x: canvas.left + (p.x + 1) * canvas.width / 2, y: canvas.top + (1 - p.y) * canvas.height / 2 };
        }),
        chrome,
      });
    };
    requestAnimationFrame(sample);
  }, everyFrame);
}

for (const viewport of [
  { width: 1280, height: 900 }, { width: 390, height: 844 }, { width: 844, height: 390 },
]) {
  test(`socket interruption preserves every perspective pose without private meshes or marker flash ${viewport.width}x${viewport.height}`, async ({ browser, baseURL }, info) => {
    test.setTimeout(75_000);
    const mobile = viewport.width <= 900;
    const context = await browser.newContext({ viewport, hasTouch: mobile, isMobile: mobile, deviceScaleFactor: 1 });
    try {
      const actor = await newActor(browser, baseURL, info, 'stable-socket-view', context);
      const page = actor.page;
      await page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
      await page.locator('#lobby-advanced summary').click();
      await page.locator('#lobby-seed').fill('4100');
      await applyRoom(actor, 3, 0, 'auto');
      await closeLobby(actor);
      await waitForBoardHand(page);
      await setHandOrder(page, 'groups');
      await page.getByTestId('settings-button').click();
      await page.getByTestId('settings-tab-display').click();
      await page.getByTestId('settings-perspective-toggle').setChecked(true);
      await page.getByTestId('settings-close').click();
      await page.mouse.move(0, 0);
      const before = await mobileGeometry(page);
      const ids = before.tiles.map(tile => tile.id);
      const navigations: string[] = [];
      page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations.push(frame.url()); });
      const markerState = (): Promise<{ things: number; meshes: number }> => page.evaluate(() => {
        const game = (window as unknown as MotionWindow).game;
        return {
          things: [...game.world.things.values()].filter(thing => thing.type === 'MARKER').length,
          meshes: game.objectView.thingGroups.get('MARKER')!.meshes.filter(mesh => mesh.visible && mesh.parent !== null).length,
        };
      });
      expect(await markerState()).toEqual({ things: 0, meshes: 0 });
      await startMeasurement(page, true);
      await page.waitForFunction(() => (window as unknown as MotionWindow).__tableMotion.frames.length >= 3);
      await page.screenshot({ path: info.outputPath('before-socket-loss.png') });
      // Transport-only fault injection: no game state, authority, camera or DOM is changed.
      await context.setOffline(true);
      await page.evaluate(() => {
        const socket = (window as unknown as MotionWindow).game.client.ws;
        if (socket?.readyState !== WebSocket.OPEN) throw new Error('Expected the real primary socket');
        socket.close(4000, 'renderer reconnect regression');
      });
      await expect.poll(() => page.evaluate(() => (window as unknown as MotionWindow).game.client.connected())).toBe(false);
      await page.waitForTimeout(1200); // Observe the disconnected frames, not merely the final camera type.
      await page.screenshot({ path: info.outputPath('during-socket-loss.png') });
      await context.setOffline(false);
      await expect.poll(() => page.evaluate(() => {
        const client = (window as unknown as MotionWindow).game.client;
        return client.connected() && client.serverSnapshotGameId !== null && client.seat === 0;
      }), { timeout: 30_000 }).toBe(true);
      await waitForBoardHand(page);
      await expect.poll(async () => (await mobileGeometry(page)).tiles.map(tile => tile.id)).toEqual(ids);
      await page.screenshot({ path: info.outputPath('after-socket-recovery.png') });
      const frames = await page.evaluate(() => {
        const recording = (window as unknown as MotionWindow).__tableMotion;
        recording.running = false;
        return recording.frames;
      });
      const maxAnchorJump = Math.max(...frames.map(frame => distance(frames[0], frame)));
      const maxPoseDrift = Math.max(...frames.flatMap(frame => frame.pose.map((v, i) => Math.abs(v - frames[0].pose[i]))));
      const maxProjectionDrift = Math.max(...frames.flatMap(frame => frame.projection.map((v, i) => Math.abs(v - frames[0].projection[i]))));
      const output = info.outputPath('reconnect-camera-frames.json');
      mkdirSync(dirname(output), { recursive: true });
      writeFileSync(output, JSON.stringify({ viewport, maxAnchorJump, maxPoseDrift, maxProjectionDrift, frames }, null, 2));
      await info.attach('reconnect-camera-frames', { path: output, contentType: 'application/json' });
      const lost = frames.filter(frame => !frame.connected);
      expect(lost.length, 'the test must sample the actual authority gap').toBeGreaterThan(5);
      expect(lost.every(frame => frame.seat === null && frame.privateMeshes === 0)).toBe(true);
      expect(frames.every(frame => frame.viewSeat === 0)).toBe(true);
      expect(frames.every(frame => frame.camera === 'PerspectiveCamera')).toBe(true);
      expect(frames.every(frame => frame.pose[3] !== 0), 'no overhead frames in a perspective camera').toBe(true);
      expect(new Set(frames.map(frame => frame.document)).size).toBe(1);
      expect(navigations).toEqual([]);
      expect(maxAnchorJump, 'all intermediate poses and framing must remain stable').toBeLessThan(.25);
      expect(maxPoseDrift).toBe(0);
      expect(maxProjectionDrift).toBeLessThan(1e-10);
      expect(await markerState()).toEqual({ things: 0, meshes: 0 });
      const settings = await page.evaluate(() => JSON.parse(localStorage.getItem('mahjong.settings.v1')!));
      expect(settings.perspective).toBe(true);
      expect(settings.handSort).toBe('groups');
      expect(actor.errors).toEqual([]);
    } finally { await context.close(); }
  });
}

function distance(a: MotionFrame, b: MotionFrame): number {
  return Math.max(...a.anchors.map((point, i) => Math.hypot(point.x - b.anchors[i].x, point.y - b.anchors[i].y)));
}

for (const viewport of [
  { width: 1280, height: 900 }, { width: 390, height: 844 }, { width: 844, height: 390 },
]) {
  test(`live bot turns, claim expiry and idle keep static table anchors stable ${viewport.width}x${viewport.height}`, async ({ browser, baseURL }, info) => {
    test.setTimeout(140_000);
    const mobile = viewport.width <= 900;
    const context = await browser.newContext({ viewport, hasTouch: mobile, isMobile: mobile, deviceScaleFactor: 1 });
    const actions: Array<{ id: number; at: number; x: number; y: number }> = [];
    const navigations: string[] = [];
    let idleStart = 0, idleEnd = 0, expiredClaims = 0;
    try {
      const actor = await newActor(browser, baseURL, info, 'stable-live-table', context);
      const page = actor.page;
      await page.locator('#lobby-hand-count-fieldset label:has(input[value="4"])').click();
      await page.locator('#lobby-advanced summary').click();
      await page.locator('#lobby-seed').fill('4100');
      await applyRoom(actor, 3, 0, 'auto');
      await closeLobby(actor);
      await waitForBoardHand(page);
      await page.mouse.move(0, 0);
      await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
      page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations.push(frame.url()); });
      await startMeasurement(page);
      idleStart = await page.evaluate(() => performance.now());
      await page.screenshot({ path: info.outputPath('idle-before.png') });
      await page.waitForTimeout(12_000); // Genuine no-input observation, not a UI synchronization workaround.
      idleEnd = await page.evaluate(() => performance.now());
      const deadline = Date.now() + 85_000;
      let activeClaim: string | null = null;
      let grouped = false;
      while (Date.now() < deadline) {
        const state = await probe(actor);
        const claim = await page.evaluate(() => {
          const game = (window as unknown as MotionWindow).game;
          const value = game.client.claim.get(String(game.client.seat));
          return value === null ? null : JSON.stringify(value);
        });
        if (claim !== null) {
          if (claim !== activeClaim) {
            activeClaim = claim;
            await page.screenshot({ path: info.outputPath('claim-open.png') });
          }
          // Deliberately let the server's real claim deadline expire.
          await page.waitForTimeout(100);
          continue;
        }
        if (activeClaim !== null) {
          expiredClaims++;
          activeClaim = null;
          await page.screenshot({ path: info.outputPath('claim-expired.png') });
        }
        const continuation = page.getByTestId('hand-result-continue');
        if (await continuation.isVisible() && await continuation.isEnabled()) {
          await continuation.click();
          continue;
        }
        if (state.turn?.activeSeat === state.seat && state.turn.awaitingDiscard
          && state.handCount % 3 === 2) {
          if (actions.length >= 4 && expiredClaims > 0) {
            await page.mouse.move(0, 0);
            await page.screenshot({ path: info.outputPath('gameplay-after.png') });
            await page.waitForTimeout(8_000); // A second real idle interval after bot/claim updates.
            break;
          }
          if (actions.length === 1 && !grouped) {
            await setHandOrder(page, 'groups');
            grouped = true;
          }
          await expect.poll(async () => (await mobileGeometry(page)).tiles.at(-1)?.hit).toBe(true);
          const tile = (await mobileGeometry(page)).tiles.at(-1)!;
          const mark = actor.sent.length;
          const at = await page.evaluate(() => performance.now());
          if (mobile) await page.touchscreen.tap(tile.x, tile.y);
          else await page.mouse.click(tile.x, tile.y);
          await expect.poll(() => actor.sent.slice(mark).flatMap(frame => frame.entries ?? [])
            .filter(([kind]) => kind === 'discard').map(([, , value]) => value?.tileId)).toEqual([tile.id]);
          await expect.poll(async () => (await probe(actor)).handIds).not.toContain(tile.id);
          actions.push({ id: tile.id, x: tile.x, y: tile.y, at });
        } else {
          await page.waitForTimeout(100);
        }
      }
      const frames = await page.evaluate(() => {
        const recording = (window as unknown as MotionWindow).__tableMotion;
        recording.running = false;
        return recording.frames;
      });
      const jumps = frames.slice(1).map((frame, i) => ({
        pixels: distance(frames[i], frame), at: frame.at,
        previous: { phase: frames[i].phase, ownClaim: frames[i].ownClaim, area: frames[i].area, chrome: frames[i].chrome },
        next: { phase: frame.phase, ownClaim: frame.ownClaim, area: frame.area, chrome: frame.chrome },
      })).filter(jump => jump.pixels > .25);
      const initialIdle = frames.filter(frame => frame.at >= idleStart && frame.at <= idleEnd);
      const summary = {
        viewport, actions, expiredClaims, navigations, documentOrigins: [...new Set(frames.map(frame => frame.document))],
        sampledFrames: frames.length, maxAdjacentJump: Math.max(0, ...jumps.map(jump => jump.pixels)),
        maxDisplacement: Math.max(...frames.map(frame => distance(frames[0], frame))),
        idleDisplacement: Math.max(...initialIdle.map(frame => distance(initialIdle[0], frame))),
        maxProjectionDrift: frames.reduce((max, frame) => Math.max(max,
          ...frame.projection.map((value, i) => Math.abs(value - frames[0].projection[i]))), 0),
        maxPoseDrift: frames.reduce((max, frame) => Math.max(max,
          ...frame.pose.map((value, i) => Math.abs(value - frames[0].pose[i]))), 0),
        statusHeights: [...new Set(frames.flatMap(frame => {
          const bounds = frame.chrome['#bot-banner']?.bounds;
          return bounds ? [bounds.bottom - bounds.top] : [];
        }))],
        jumps,
      };
      const output = info.outputPath('camera-motion.json');
      mkdirSync(dirname(output), { recursive: true });
      writeFileSync(output, JSON.stringify({ summary, frames }, null, 2));
      await info.attach('camera-motion', { path: output, contentType: 'application/json' });
      expect(actions.length, 'several actual human discards and intervening bot turns').toBeGreaterThanOrEqual(4);
      expect(expiredClaims, 'a real own claim was shown and allowed to expire').toBeGreaterThan(0);
      expect(frames.some(frame => frame.activeSeat !== 0 && frame.activeSeat !== null)).toBe(true);
      expect(navigations, 'in-page motion must not be mistaken for a document reload').toEqual([]);
      expect(summary.documentOrigins).toHaveLength(1);
      expect(frames.every(frame => frame.zoom === 0 && frame.lookDown === 0)).toBe(true);
      expect(summary.idleDisplacement, 'idle must not move the table').toBeLessThan(.25);
      expect(summary.maxDisplacement, `game events moved table anchors; largest step ${summary.maxAdjacentJump}px`).toBeLessThan(.25);
      expect(summary.maxProjectionDrift, 'the fitted projection must not pulse with runtime UI').toBeLessThan(1e-10);
      expect(summary.maxPoseDrift, 'no game event changes the camera pose').toBe(0);
      if (!mobile) expect(summary.statusHeights, 'loading metadata keeps the same status region').toHaveLength(1);
      expect(actor.errors).toEqual([]);
    } finally { await context.close(); }
  });
}

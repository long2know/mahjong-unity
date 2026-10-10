import { expect, type Page, type TestInfo } from '@playwright/test';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import type { HandResultEntry } from '../../src/types';

export async function assertResultTileArtwork(page: Page, hand: readonly number[], names?: readonly string[]): Promise<void> {
  const tiles = page.locator('#result-hand .result-tile');
  await expect(tiles).toHaveCount(hand.length);
  await expect(page.locator('#result-hand button, #result-hand a, #result-hand [tabindex]')).toHaveCount(0);
  if (hand.length === 0) return;
  await expect(page.locator('#result-hand')).toBeVisible();
  await page.locator('#result-hand').scrollIntoViewIfNeeded({ timeout: 5_000 });
  const rendered = await tiles.evaluateAll(elements => elements.map(element => {
    const tile = element as HTMLElement;
    const style = getComputedStyle(tile);
    const rect = tile.getBoundingClientRect();
    return {
      tileId: tile.dataset.tileId, face: tile.dataset.face, role: tile.getAttribute('role'),
      label: tile.getAttribute('aria-label'), title: tile.title, text: tile.textContent,
      image: style.backgroundImage, size: style.backgroundSize,
      x: parseFloat(style.backgroundPositionX), y: parseFloat(style.backgroundPositionY),
      width: rect.width, height: rect.height,
      painted: [rect.left + 8, (rect.left + rect.right) / 2, rect.right - 8].every(x =>
        [rect.top + 8, (rect.top + rect.bottom) / 2, rect.bottom - 8].every(y =>
          tile.contains(document.elementFromPoint(x, y)))),
    };
  }));
  for (let i = 0; i < hand.length; i++) {
    const face = Math.floor(hand[i] / 4);
    const expectedName = names?.[i] ?? `${face % 9 + 1} ${['characters', 'circles', 'bamboo'][Math.floor(face / 9)]}`;
    const tile = rendered[i];
    expect(tile.tileId).toBe(String(hand[i]));
    expect(tile.face).toBe(String(face));
    expect(tile.role).toBe('img');
    expect(tile.label).toBe(expectedName);
    expect(tile.title).toBe(expectedName);
    expect(tile.text).toBe('');
    expect(tile.size).toBe('800% 640%');
    expect(tile.x / 100 * 448).toBeCloseTo(face % 8 * 64, 2);
    expect(tile.y / 100 * 432).toBeCloseTo(Math.floor(face / 8) * 80, 2);
    expect(tile.width).toBeGreaterThanOrEqual(40);
    expect(tile.height).toBeCloseTo(tile.width * 5 / 4, 1);
    expect(tile.painted, `physical tile ${hand[i]} must not be clipped or covered`).toBe(true);
  }
  const images = [...new Set(rendered.map(tile => tile.image))];
  expect(images).toHaveLength(1);
  const imageUrl = /^url\(["']?(.+?)["']?\)$/.exec(images[0])?.[1];
  expect(imageUrl, 'the result must use the board tile atlas, not just a colored rectangle').toMatch(/tiles-labels\.auto\.[a-f0-9]+\.png$/);
  const decoded = await page.evaluate(async url => {
    const image = new Image();
    image.src = url;
    await image.decode();
    return { width: image.naturalWidth, height: image.naturalHeight, complete: image.complete };
  }, imageUrl!);
  expect(decoded).toEqual({ width: 512, height: 512, complete: true });
  const response = await page.request.get(imageUrl!);
  expect(response.ok()).toBe(true);
  expect(await response.body()).toEqual(readFileSync(resolve(__dirname, '../../img/tiles-labels.auto.png')));
}

export async function captureResultTileArtwork(
  page: Page, info: TestInfo, name: string, provenance: 'controlled-component-fixture' | 'server-completed-hand',
  result: HandResultEntry,
): Promise<void> {
  const directory = process.env.RESULT_TILE_ARTIFACT_DIR;
  const png = directory ? join(directory, `${name}.png`) : info.outputPath(`${name}.png`);
  const json = directory ? join(directory, `${name}.json`) : info.outputPath(`${name}.json`);
  mkdirSync(dirname(png), { recursive: true });
  await page.screenshot({ path: png });
  writeFileSync(json, JSON.stringify({ provenance, viewport: page.viewportSize(), result }, null, 2));
  await info.attach(name, { path: png, contentType: 'image/png' });
  await info.attach(`${name}-provenance`, { path: json, contentType: 'application/json' });
}

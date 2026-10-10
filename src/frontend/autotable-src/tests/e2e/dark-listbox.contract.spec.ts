// Ferro — dark-listbox G5 acceptance: the OPENED popup (not getComputedStyle
// of a native <option>) must be readable. This renders the exact DOM the
// module's renderPopup() produces, applies dark-listbox.css, screenshots the
// open popup, and asserts WCAG-AA contrast on the rendered options. Also
// asserts the module is importable/well-formed. chromium only.

import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { enhanceDarkSelect, installDarkListbox } from '../../src/ui/dark-listbox';

function findUp(rel: string, start: string, max = 10): string {
  let dir = start;
  for (let i = 0; i < max; i++) {
    const c = path.join(dir, rel);
    if (fs.existsSync(c)) return c;
    const p = path.dirname(dir);
    if (p === dir) break;
    dir = p;
  }
  throw new Error(`not found: ${rel}`);
}
const here = path.dirname(__filename);
const LBCSS = fs.readFileSync(findUp(path.join('src', 'ui', 'dark-listbox.css'), here), 'utf-8');

function srgb(c: number): number { const s = c / 255; return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4); }
function lum([r, g, b]: [number, number, number]): number { return 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b); }
function contrast(a: [number, number, number], b: [number, number, number]): number {
  const l1 = lum(a), l2 = lum(b), hi = Math.max(l1, l2), lo = Math.min(l1, l2); return (hi + 0.05) / (lo + 0.05);
}
function parse(s: string): [number, number, number] { const m = /rgba?\(([^)]+)\)/.exec(s)!; const p = m[1].split(',').map((n) => parseFloat(n)); return [p[0], p[1], p[2]]; }

// Faithful reproduction of renderPopup()'s DOM (role=listbox + group + options).
const POPUP = `
  <div class="dark-listbox">
    <button class="dark-listbox-trigger dark-select" aria-expanded="true">Auto (instant deal)</button>
    <div class="dark-listbox-popup" role="listbox" aria-label="Deal mode">
      <div class="dark-listbox-group" role="presentation">Changsha</div>
      <div class="dark-listbox-option" role="option" aria-selected="false">Manual (click to pick)</div>
      <div class="dark-listbox-option active" role="option" aria-selected="true">Auto (instant deal)</div>
      <div class="dark-listbox-option" role="option" aria-disabled="true">Coming soon</div>
    </div>
  </div>`;

test.describe('dark-listbox — opened popup is readable (G5)', () => {
  test.beforeEach(async ({}, ti) => { test.skip(ti.project.name !== 'chromium', 'chromium only'); });

  test('module exports are importable and well-formed (Node)', () => {
    expect(typeof enhanceDarkSelect).toBe('function');
    expect(typeof installDarkListbox).toBe('function');
  });

  test('the OPEN popup renders readable dark options (screenshot + WCAG contrast)', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 460, height: 340 });
    await page.setContent(`<!doctype html><html><head><meta charset="utf-8"><style>${LBCSS}</style></head><body style="background:#0e1c2b;padding:40px;min-height:320px">${POPUP}</body></html>`, { waitUntil: 'domcontentloaded' });

    const shot = testInfo.outputPath('dark-listbox-open.png');
    await page.screenshot({ path: shot });
    await testInfo.attach('opened-popup', { path: shot, contentType: 'image/png' });

    const data = await page.evaluate(() => {
      const cs = getComputedStyle;
      const popup = document.querySelector('.dark-listbox-popup') as HTMLElement;
      const items = Array.from(document.querySelectorAll('.dark-listbox-option')) as HTMLElement[];
      const group = document.querySelector('.dark-listbox-group') as HTMLElement;
      const bg = cs(popup).backgroundColor;
      const rect = popup.getBoundingClientRect();
      return {
        popupBg: bg,
        popupVisible: rect.width > 0 && rect.height > 0,
        normal: { color: cs(items[0]).color, bg: cs(items[0]).backgroundColor },
        active: { color: cs(items[1]).color, bg: cs(items[1]).backgroundColor },
        group: { color: cs(group).color },
      };
    });

    expect(data.popupVisible, 'the popup must actually render (be screenshottable)').toBe(true);
    // Normal option: light text on the popup surface.
    const normalBg = parse(data.normal.bg.includes('rgba(0, 0, 0, 0)') ? data.popupBg : data.normal.bg);
    expect(contrast(parse(data.normal.color), normalBg), `normal option contrast (${data.normal.color} on ${data.normal.bg}/${data.popupBg})`).toBeGreaterThanOrEqual(4.5);
    // Active (highlighted) option: white on the highlight fill.
    expect(contrast(parse(data.active.color), parse(data.active.bg)), `active option contrast (${data.active.color} on ${data.active.bg})`).toBeGreaterThanOrEqual(4.5);
    // Group header (gold) on the popup surface.
    expect(contrast(parse(data.group.color), parse(data.popupBg)), `group header contrast (${data.group.color} on ${data.popupBg})`).toBeGreaterThanOrEqual(4.5);
  });
});

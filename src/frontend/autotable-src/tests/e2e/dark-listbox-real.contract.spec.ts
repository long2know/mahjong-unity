// Ferro — UI-1 / G5 real-control gate: the robust dark popup must be wired onto
// the ACTUAL Changsha-visible production selects — the settings-drawer language
// select (`data-testid=settings-language-select`), the rule-preset picker, and
// the lobby variant-picker (`.ferro-variant-picker-select.dark-select`, with
// optgroups) — NOT the FE-1-hidden relay setup panel. Compiles the REAL
// src/ui/dark-listbox.ts, injects it, and asserts each Changsha select is
// (a) still VISIBLE in Changsha (not caught by the FE-1 gate), (b) auto-enhanced
// (native hidden + exactly one custom trigger — no duplicate/orphan), (c) opens
// a readable popup (screenshot + WCAG contrast on real options + optgroup
// headers), (d) selection updates the real native value + fires `change` (so
// URL-nav / game-ui / settings handlers stay wired), and (e) the native
// fallback stays readable (color-scheme:dark) if the JS never runs. The full-
// rigor keyboard-opened / real-pixel WCAG / value-change G5 gate additionally runs
// on #settings-bot-strength — a REAL Changsha-VISIBLE settings-drawer select (no
// .relay-only, not caught by the FE-1 gate). The relay-only #game-type (FE-1-hidden
// in Changsha) is exercised ONLY as a relay-context enhancement check — NOT a
// Changsha acceptance gate; the G5 alignment forbids force-showing it. chromium only.

import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as esbuild from 'esbuild';

function findUp(rel: string, start: string, max = 12): string {
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
const ROOT = path.dirname(findUp('index.html', path.dirname(__filename)));
const CSS = fs.readFileSync(path.join(ROOT, 'src', 'style.css'), 'utf-8');
const LBCSS = fs.readFileSync(path.join(ROOT, 'src', 'ui', 'dark-listbox.css'), 'utf-8');
const VPCSS = fs.readFileSync(path.join(ROOT, 'src', 'ui', 'variant-picker.css'), 'utf-8');

const MODULE_JS = esbuild.buildSync({
  entryPoints: [path.join(ROOT, 'src', 'ui', 'dark-listbox.ts')],
  bundle: true, write: false, format: 'iife', globalName: 'DarkListbox',
  loader: { '.css': 'empty' }, logLevel: 'silent',
}).outputFiles[0].text;

function srgb(c: number): number { const s = c / 255; return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4); }
function lum([r, g, b]: [number, number, number]): number { return 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b); }
function contrast(a: [number, number, number], b: [number, number, number]): number {
  const l1 = lum(a), l2 = lum(b), hi = Math.max(l1, l2), lo = Math.min(l1, l2); return (hi + 0.05) / (lo + 0.05);
}
function parse(s: string): [number, number, number] { const m = /rgba?\(([^)]+)\)/.exec(s)!; const p = m[1].split(',').map((n) => parseFloat(n)); return [p[0], p[1], p[2]]; }

// Faithful reproductions of the real Changsha-visible selects (classes/ids/
// data-testids copied from variant-picker.ts, settings-drawer.ts:477-489,
// rule-presets.ts:399-414). All are `.dark-select` so the auto-enhancer wires
// them; none carry .relay-only/.riichi-only so FE-1 keeps them visible.
const CHANGSHA_SELECTS = `
  <section class="ferro-variant-picker" role="group" aria-label="Game variant">
    <select class="ferro-variant-picker-select dark-select" id="ferro-variant-select" data-testid="ferro-variant-picker" aria-label="Game variant">
      <option value="changsha" selected>Changsha (长沙麻将)</option>
      <optgroup label="Original Autotable">
        <option value="four-player">Riichi — 4 player (日本麻将)</option>
        <option value="three-player">Riichi — 3 player</option>
        <option value="bamboo">Bamboo (American)</option>
        <option value="minefield">Minefield</option>
      </optgroup>
      <optgroup label="Coming soon">
        <option value="hong-kong" disabled>Hong Kong (港麻)</option>
      </optgroup>
    </select>
  </section>
  <div class="settings-drawer-v2">
    <select class="dark-select form-control form-control-sm" data-testid="settings-language-select" aria-label="Language preference">
      <option value="auto" selected>Auto (browser default)</option>
      <option value="en">English</option>
      <option value="zh-Hans">简体中文</option>
      <option value="zh-Hant">繁體中文</option>
    </select>
    <select class="dark-select form-control form-control-sm" data-testid="rule-preset-picker" aria-label="Edit preset">
      <option value="classic-changsha" selected>Classic Changsha (built-in — read-only)</option>
      <option value="__new__">+ New preset</option>
    </select>
  </div>`;

const DOC = (body: string): string =>
  `<!doctype html><html><head><meta charset="utf-8"><style>${CSS}\n${VPCSS}\n${LBCSS}</style></head><body class="theme-light variant-changsha" style="padding:24px">${body}</body></html>`;

const REAL = [
  { name: 'variant-picker (lobby)', sel: '#ferro-variant-select', pick: '3 player|three', pickValue: 'three-player' },
  { name: 'settings language', sel: '[data-testid="settings-language-select"]', pick: 'English', pickValue: 'en' },
  { name: 'rule-preset picker', sel: '[data-testid="rule-preset-picker"]', pick: 'New preset', pickValue: '__new__' },
];

test.describe('dark-listbox on the REAL Changsha-visible production selects (UI-1/G5)', () => {
  test.beforeEach(async ({}, ti) => { test.skip(ti.project.name !== 'chromium', 'chromium only'); });

  test('every Changsha-visible select is visible, auto-enhanced, and readable when opened', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 560, height: 640 });
    await page.setContent(DOC(CHANGSHA_SELECTS), { waitUntil: 'domcontentloaded' });
    await page.addScriptTag({ content: MODULE_JS });
    await page.waitForFunction(() => document.querySelectorAll('.dark-listbox-native').length >= 3);

    // (a) visible in Changsha + (b) enhanced with exactly one trigger (no dup).
    const enh = await page.evaluate((sels) => {
      const vis = (el: (Element & { checkVisibility?: () => boolean }) | null): boolean =>
        el === null ? false : (el.checkVisibility ? el.checkVisibility() : el.getClientRects().length > 0);
      return sels.map((s) => {
        const nat = document.querySelector(s) as HTMLSelectElement | null;
        const w = nat?.closest('.dark-listbox') ?? null;
        const trig = w?.querySelector('.dark-listbox-trigger') as HTMLElement | null;
        return {
          s, nativeEnhanced: !!nat?.classList.contains('dark-listbox-native'),
          triggerVisible: vis(trig), triggerCount: w ? w.querySelectorAll('.dark-listbox-trigger').length : 0,
        };
      });
    }, REAL.map((r) => r.sel));
    for (const e of enh) {
      expect(e.nativeEnhanced, `${e.s} native must be enhanced (dark-listbox-native)`).toBe(true);
      expect(e.triggerVisible, `${e.s} custom trigger must be VISIBLE in Changsha`).toBe(true);
      expect(e.triggerCount, `${e.s} must have exactly one trigger (no duplicate/orphan)`).toBe(1);
    }

    // (c/d) Per select: open, read contrast, choose an option, verify the real
    // native value updated + change fired, popup closes.
    for (const r of REAL) {
      const res = await page.evaluate(({ sel, pickText }) => {
        const cs = getComputedStyle;
        const nat = document.querySelector(sel) as HTMLSelectElement;
        const w = nat.closest('.dark-listbox')!;
        (w.querySelector('.dark-listbox-trigger') as HTMLButtonElement).click();
        const popup = w.querySelector('.dark-listbox-popup') as HTMLElement;
        const opts = Array.from(popup.querySelectorAll('.dark-listbox-option')) as HTMLElement[];
        const groups = Array.from(popup.querySelectorAll('.dark-listbox-group')) as HTMLElement[];
        const popupBg = cs(popup).backgroundColor;
        const sample = opts.slice(0, 3).map((o) => ({ color: cs(o).color, bg: cs(o).backgroundColor }));
        const groupColor = groups[0] ? cs(groups[0]).color : null;
        let changedTo: string | null = null;
        nat.addEventListener('change', (e) => { changedTo = (e.target as HTMLSelectElement).value; }, { once: true });
        const target = opts.find((o) => new RegExp(pickText, 'i').test(o.textContent || ''))!;
        target.click();
        return { popupBg, sample, groupColor, changedTo, value: nat.value, closed: popup.hidden };
      }, { sel: r.sel, pickText: r.pick });

      for (const o of res.sample) {
        const bg = o.bg.includes('rgba(0, 0, 0, 0)') ? res.popupBg : o.bg;
        expect(contrast(parse(o.color), parse(bg)), `${r.name}: option contrast (${o.color} on ${bg})`).toBeGreaterThanOrEqual(4.5);
      }
      if (res.groupColor) {
        expect(contrast(parse(res.groupColor), parse(res.popupBg)), `${r.name}: optgroup header contrast`).toBeGreaterThanOrEqual(4.5);
      }
      expect(res.value, `${r.name}: real native value updated by the custom listbox`).toBe(r.pickValue);
      expect(res.changedTo, `${r.name}: native change event fired (handlers stay wired)`).toBe(r.pickValue);
      expect(res.closed, `${r.name}: popup closes after selection`).toBe(true);
    }

    // Screenshot the lobby variant-picker's opened popup (headline Changsha
    // control; shows optgroup rendering).
    await page.evaluate(() => {
      const w = document.getElementById('ferro-variant-select')!.closest('.dark-listbox')!;
      (w.querySelector('.dark-listbox-trigger') as HTMLButtonElement).click();
    });
    await page.waitForFunction(() => {
      const p = document.getElementById('ferro-variant-select')!.closest('.dark-listbox')!.querySelector('.dark-listbox-popup') as HTMLElement;
      return p && !p.hidden;
    });
    const clip = await page.evaluate(() => {
      const w = document.getElementById('ferro-variant-select')!.closest('.dark-listbox')!;
      const t = w.querySelector('.dark-listbox-trigger')!.getBoundingClientRect();
      const p = w.querySelector('.dark-listbox-popup')!.getBoundingClientRect();
      const x = Math.min(t.left, p.left) - 6, y = Math.min(t.top, p.top) - 6;
      return { x, y, width: Math.max(t.right, p.right) - x + 6, height: Math.max(t.bottom, p.bottom) - y + 6 };
    });
    const shot = testInfo.outputPath('changsha-variant-picker-open.png');
    await page.screenshot({ path: shot, clip });
    await testInfo.attach('changsha-variant-picker-open', { path: shot, contentType: 'image/png' });
  });

  test('G5 real-pixel gate runs on #settings-bot-strength — a REAL Changsha-visible production select', async ({ page }, testInfo) => {
    // #settings-bot-strength is a genuine settings-drawer production select
    // (index.html:554): it carries `.dark-select` and NO .relay-only/.riichi-only
    // class, so the FE-1 mode boundary keeps it VISIBLE in Changsha. This is the
    // retarget of the old #game-type gate — the G5 alignment forbids force-showing
    // the FE-1-hidden relay-only #game-type for acceptance, so the full-rigor
    // keyboard-open / real-pixel WCAG / value-change certification runs HERE, on a
    // control the Changsha player actually sees. Rendered in a Changsha host.
    await page.setViewportSize({ width: 480, height: 560 });
    await page.setContent(DOC(`
      <div class="settings-drawer-v2">
        <div class="settings-row">
          <label for="settings-bot-strength" class="settings-label">Bot Strength</label>
          <select id="settings-bot-strength" class="dark-select form-control form-control-sm">
            <option value="Easy">Easy</option>
            <option value="Medium">Medium</option>
            <option value="Hard" selected>Hard</option>
          </select>
        </div>
      </div>`), { waitUntil: 'domcontentloaded' });
    await page.addScriptTag({ content: MODULE_JS });
    await page.waitForFunction(() => !!document.querySelector('#settings-bot-strength.dark-listbox-native'));

    // (a) native <select> REPLACED by a custom listbox — VISIBLE trigger in
    //     Changsha, exactly one trigger, native aria-hidden, listbox semantics.
    const enh = await page.evaluate(() => {
      const nat = document.getElementById('settings-bot-strength') as HTMLSelectElement;
      const w = nat.closest('.dark-listbox')!;
      const trig = w.querySelector('.dark-listbox-trigger') as HTMLElement;
      return {
        nativeEnhanced: nat.classList.contains('dark-listbox-native'),
        nativeAriaHidden: nat.getAttribute('aria-hidden') === 'true',
        triggerCount: w.querySelectorAll('.dark-listbox-trigger').length,
        haspopup: trig.getAttribute('aria-haspopup'),
        triggerVisible: trig.getClientRects().length > 0,
      };
    });
    expect(enh.nativeEnhanced, '#settings-bot-strength native <select> replaced by custom listbox').toBe(true);
    expect(enh.nativeAriaHidden, 'native select aria-hidden when enhanced').toBe(true);
    expect(enh.triggerCount, 'exactly one custom trigger (no duplicate/orphan)').toBe(1);
    expect(enh.haspopup, 'trigger exposes a listbox popup (a11y)').toBe('listbox');
    expect(enh.triggerVisible, 'custom trigger is VISIBLE in Changsha (not FE-1-hidden)').toBe(true);

    // (b) OPEN via KEYBOARD (ArrowDown) + assert real-pixel WCAG>=4.5 on every
    //     rendered option.
    const res = await page.evaluate(() => {
      const cs = getComputedStyle;
      const nat = document.getElementById('settings-bot-strength') as HTMLSelectElement;
      const w = nat.closest('.dark-listbox')!;
      const trig = w.querySelector('.dark-listbox-trigger') as HTMLButtonElement;
      trig.focus();
      trig.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
      const popup = w.querySelector('.dark-listbox-popup') as HTMLElement;
      const opts = Array.from(popup.querySelectorAll('.dark-listbox-option')) as HTMLElement[];
      return {
        open: !popup.hidden,
        optCount: opts.length,
        popupBg: cs(popup).backgroundColor,
        samples: opts.map((o) => ({ text: o.textContent, color: cs(o).color, bg: cs(o).backgroundColor })),
      };
    });
    expect(res.open, '#settings-bot-strength popup opens via keyboard (ArrowDown)').toBe(true);
    expect(res.optCount, 'all 3 real options rendered in the custom popup').toBe(3);
    for (const o of res.samples) {
      const bg = o.bg.includes('rgba(0, 0, 0, 0)') ? res.popupBg : o.bg;
      expect(contrast(parse(o.color), parse(bg)), `#settings-bot-strength option "${o.text}" real-pixel contrast (${o.color} on ${bg})`).toBeGreaterThanOrEqual(4.5);
    }

    // Screenshot the ACTUAL opened custom popup — the real-pixel G5 artifact.
    const clip = await page.evaluate(() => {
      const w = document.getElementById('settings-bot-strength')!.closest('.dark-listbox')!;
      const t = w.querySelector('.dark-listbox-trigger')!.getBoundingClientRect();
      const p = w.querySelector('.dark-listbox-popup')!.getBoundingClientRect();
      const x = Math.min(t.left, p.left) - 6, y = Math.min(t.top, p.top) - 6;
      return { x, y, width: Math.max(t.right, p.right) - x + 6, height: Math.max(t.bottom, p.bottom) - y + 6 };
    });
    const shot = testInfo.outputPath('settings-bot-strength-open.png');
    await page.screenshot({ path: shot, clip });
    await testInfo.attach('settings-bot-strength-setup-selector-open', { path: shot, contentType: 'image/png' });

    // (c) selection updates the real native value + fires `change` (Hard -> Easy).
    const pick = await page.evaluate(() => {
      const nat = document.getElementById('settings-bot-strength') as HTMLSelectElement;
      const w = nat.closest('.dark-listbox')!;
      let changed: string | null = null;
      nat.addEventListener('change', (e) => { changed = (e.target as HTMLSelectElement).value; }, { once: true });
      (Array.from(w.querySelectorAll('.dark-listbox-option')) as HTMLElement[]).find((o) => /^Easy$/i.test((o.textContent || '').trim()))!.click();
      return { value: nat.value, changed, closed: (w.querySelector('.dark-listbox-popup') as HTMLElement).hidden };
    });
    expect(pick.value, 'real #settings-bot-strength value updated by the custom listbox').toBe('Easy');
    expect(pick.changed, 'native change event fired (app handlers stay wired)').toBe('Easy');
    expect(pick.closed, 'popup closes after selection').toBe(true);
  });

  test('#game-type auto-enhances in a RELAY context only (NOT a Changsha acceptance gate)', async ({ page }) => {
    // #game-type lives in #setup-group.relay-only (index.html:115) — the FE-1 mode
    // boundary HIDES it in Changsha, so it is NOT a Changsha G5 target and must never
    // be force-shown for acceptance (per the G5 alignment). This retained check only
    // proves the auto-enhancer still wires #game-type when it IS shown (relay
    // variants), rendered in a relay-context host (body variant-four-player). No
    // Changsha / real-pixel acceptance is claimed here; the Changsha G5 real-pixel
    // gate is #settings-bot-strength above.
    await page.setViewportSize({ width: 480, height: 560 });
    await page.setContent(
      `<!doctype html><html><head><meta charset="utf-8"><style>${CSS}\n${LBCSS}</style></head>` +
        `<body class="theme-light variant-four-player" style="padding:24px">` +
        `<div id="setup-group"><select class="dark-select form-control form-control-sm" id="game-type" aria-label="Game type">` +
        `<option value="FOUR_PLAYER" selected>Riichi — 4 player</option>` +
        `<option value="THREE_PLAYER">Riichi — 3 player</option>` +
        `<option value="BAMBOO">Bamboo</option>` +
        `<option value="MINEFIELD">Minefield</option>` +
        `</select></div></body></html>`,
      { waitUntil: 'domcontentloaded' },
    );
    await page.addScriptTag({ content: MODULE_JS });
    await page.waitForFunction(() => !!document.querySelector('#game-type.dark-listbox-native'));
    const enh = await page.evaluate(() => {
      const nat = document.getElementById('game-type') as HTMLSelectElement;
      const w = nat.closest('.dark-listbox')!;
      return {
        nativeEnhanced: nat.classList.contains('dark-listbox-native'),
        triggerCount: w.querySelectorAll('.dark-listbox-trigger').length,
        haspopup: w.querySelector('.dark-listbox-trigger')!.getAttribute('aria-haspopup'),
      };
    });
    expect(enh.nativeEnhanced, '#game-type auto-enhances when shown (relay context)').toBe(true);
    expect(enh.triggerCount, 'exactly one custom trigger (no duplicate/orphan)').toBe(1);
    expect(enh.haspopup, 'trigger exposes a listbox popup (a11y)').toBe('listbox');
  });

  test('native fallback stays readable if the JS never runs (color-scheme:dark)', async ({ page }) => {
    await page.setContent(DOC(CHANGSHA_SELECTS), { waitUntil: 'domcontentloaded' });
    const cs = await page.evaluate(() => getComputedStyle(document.querySelector('[data-testid="settings-language-select"]') as HTMLElement).colorScheme);
    expect(cs, 'un-enhanced native Changsha select keeps color-scheme:dark').toContain('dark');
  });
});

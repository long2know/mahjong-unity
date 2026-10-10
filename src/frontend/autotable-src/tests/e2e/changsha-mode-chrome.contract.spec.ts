// Ferro — Changsha mode/input-boundary UI contract (P0, UAT-driven).
//
// Guards the frontend half of Ripley's mode-boundary synthesis: in
// authoritative Changsha the upstream relay chrome (Deal / Setup / Dealer /
// manual Connect·Disconnect·GameID panel / Riichi scoring links) and every
// Riichi-only control must be ABSENT from the accessibility tree (display:none,
// non-operable) — including the pre-JS first paint (no variant body class yet),
// so no "Riichi 4p / 4p-no-red / Deal" flash occurs. Relay variants keep them.
//
// Also guards dropdown readability via the platform-independent `color-scheme:
// dark` on the setup/settings selects (the acceptance the UAT locked, replacing
// the invalid getComputedStyle(option) proxy — the guarantee rides on the
// control's resolved color-scheme so the NATIVE popup is dark on Chromium /
// Tauri / mobile regardless of per-option styling).
//
// Tests the REAL index.html markup against the REAL src/style.css (Bootstrap is
// intentionally not loaded — this isolates our own gating rules). Browser-free
// of any backend; chromium project only.

import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

function findUp(rel: string, start: string, max = 10): string {
  let dir = start;
  for (let i = 0; i < max; i++) {
    const c = path.join(dir, rel);
    if (fs.existsSync(c)) return c;
    const p = path.dirname(dir);
    if (p === dir) break;
    dir = p;
  }
  throw new Error(`not found: ${rel} from ${start}`);
}

const here = path.dirname(__filename);
const CSS = fs.readFileSync(findUp(path.join('src', 'style.css'), here), 'utf-8');
const INDEX = fs.readFileSync(findUp('index.html', here), 'utf-8');

// Strip external scripts so setContent doesn't try to run the module bundle.
const INDEX_NOSCRIPT = INDEX.replace(/<script[\s\S]*?<\/script>/gi, '');

// Relay-mode chrome that must vanish in authoritative Changsha.
const RELAY_CHROME = ['#deal', '#toggle-setup', '#setup-group', '#toggle-dealer', '#connect', '#disconnect', '#lobby-gameId-row'];
// Riichi-only controls.
const RIICHI_ONLY = ['#fives', '#points', '#toggle-honba'];
// Authoritative controls that MUST stay reachable in Changsha.
const AUTHORITATIVE = ['#claim-pung', '#claim-hu', '#leave-seat'];

async function visMap(page: import('@playwright/test').Page, bodyClass: string, sels: string[]): Promise<Record<string, boolean | 'MISSING'>> {
  return page.evaluate(({ bodyClass, sels }) => {
    document.body.className = bodyClass;
    const out: Record<string, boolean | 'MISSING'> = {};
    for (const s of sels) {
      const el = document.querySelector(s) as (Element & { checkVisibility?: () => boolean }) | null;
      // checkVisibility() is false when the element OR any ancestor is
      // display:none — the correct "absent from the render/a11y tree" signal
      // (a relay control hidden via its .relay-only parent still reports its
      // own computed display, so we must walk rendering, not the property).
      out[s] = el === null ? 'MISSING' : (el.checkVisibility ? el.checkVisibility() : el.getClientRects().length > 0);
    }
    return out;
  }, { bodyClass, sels });
}

test.describe('Changsha mode-boundary UI contract (P0)', () => {
  test.beforeEach(async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'contract checked on chromium');
    await page.setContent(`<!doctype html><html><head><meta charset="utf-8"><style>${CSS}</style></head>${INDEX_NOSCRIPT.replace(/^[\s\S]*?<body[^>]*>/i, '<body>').replace(/<\/html>[\s\S]*$/i, '</html>')}`, { waitUntil: 'domcontentloaded' });
  });

  for (const bodyClass of ['', 'theme-light variant-changsha']) {
    const label = bodyClass === '' ? 'first paint (no variant class)' : 'authoritative Changsha';
    test(`relay chrome + Riichi-only are display:none — ${label}`, async ({ page }) => {
      const relay = await visMap(page, bodyClass, [...RELAY_CHROME, ...RIICHI_ONLY]);
      const offenders = Object.entries(relay).filter(([, v]) => v === true);
      expect(offenders, `these relay/Riichi controls are visible in ${label} (must be absent from the render/a11y tree):\n` +
        offenders.map(([s]) => `  ${s}`).join('\n')).toEqual([]);
    });
  }

  test('authoritative controls stay reachable in Changsha', async ({ page }) => {
    const auth = await visMap(page, 'theme-light variant-changsha', AUTHORITATIVE);
    for (const [s, v] of Object.entries(auth)) {
      expect(v, `authoritative control ${s} must remain visible in Changsha`).toBe(true);
    }
  });

  test('relay chrome IS restored for explicit Riichi variants', async ({ page }) => {
    // #connect/#disconnect visibility is connection-state toggled (server class),
    // not variant-gated, so exclude them from the "restored" assertion.
    const restore = RELAY_CHROME.filter((s) => s !== '#connect' && s !== '#disconnect');
    const relay = await visMap(page, 'variant-riichi', restore);
    for (const [s, v] of Object.entries(relay)) {
      expect(v, `relay control ${s} must be visible under variant-riichi`).toBe(true);
    }
  });

  test('setup/settings selects carry color-scheme:dark for readable native popups', async ({ page }) => {
    const cs = await page.evaluate(() => {
      const el = document.querySelector('#deal-type, .dark-select') as HTMLElement | null;
      return el ? getComputedStyle(el).colorScheme : 'MISSING';
    });
    expect(cs, '.dark-select must resolve color-scheme:dark so the native option popup renders light-on-dark cross-platform').toContain('dark');
  });

  test('index.html marks relay chrome + Riichi scoring links (markup contract)', () => {
    // Relay panels carry .relay-only; the Riichi cheat-sheet links carry .riichi-only.
    for (const id of ['setup-group', 'server', 'lobby-gameId-row']) {
      const re = new RegExp(`id="${id}"[^>]*class="[^"]*relay-only|class="[^"]*relay-only[^"]*"[^>]*id="${id}"`);
      expect(INDEX, `#${id} must be authored with class "relay-only"`).toMatch(re);
    }
    expect(INDEX, 'Riichi scoring cheat-sheet links must be wrapped in .riichi-only').toMatch(/riichi-only[^>]*>[\s\S]*riichi-cheat-sheet|riichi-cheat-sheet[\s\S]*?<\/small>\s*<\/div>/);
  });
});

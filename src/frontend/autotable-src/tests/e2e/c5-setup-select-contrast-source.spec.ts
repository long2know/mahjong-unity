// C-5 (source/stylesheet half) — Ripley Design-Review C-4/C-5/C-6:
// setup/settings <select> option contrast.  Owner: Ferro (Frontend/UI).
//
// Division of labor (distinct files, no duplicate edits): the
// BROWSER-VISIBLE half — which drives the live running app + built bundle
// and reads the real Setup selects' option lists — lives in the sibling
// `c5-setup-select-contrast.spec.ts`.  THIS file is the SOURCE/STYLESHEET
// half: it injects the released `src/style.css` into a blank page (no
// bundle build, no backend, no live app) and asserts the option/optgroup
// contrast contract deterministically against the stylesheet source.
//
// Bug reproduced: the setup/settings dropdowns (`.dark-select`: the
// settings-drawer Language / Motion / Theme selects and the rule-preset
// picker; the lobby variant-picker is also `.dark-select`) only recolor
// the CLOSED control — `.dark-select { color:#fff; background:#343a40 }`.
// The native listbox that opens on click INHERITS that white `color` but
// keeps a transparent `background-color`, so every <option>/<optgroup>
// paints white-on-white over the browser/OS default light popup surface
// → unreadable "white-on-white setup dropdown".
//
// Ripley's Design Review contracts C-4/C-5/C-6 require that every themed
// <select> explicitly style its <option>/<optgroup> FOREGROUND and
// BACKGROUND with a WCAG AA contrast ratio >= 4.5:1.  This spec asserts
// that contract deterministically:
//
//   1. Inject the REAL, released `src/style.css` (no bundle build needed).
//   2. Reproduce the exact production markup/classes for the setup &
//      settings selects.
//   3. Read each option/optgroup's COMPUTED color + background-color and
//      assert (a) the background is explicitly opaque (not transparent /
//      inherited) and (b) contrast(color, background) >= 4.5:1.
//
// RED on ddc72e1 (no `.dark-select option` rule → transparent option
// background, inherited white text); GREEN once style.css paints the
// option/optgroup palette.
//
// Native-popup caveat: on some OS/native widget backends the closed
// <select>'s popup is drawn by the platform and may ignore author
// <option> colors entirely.  This spec asserts the CSS contract that the
// shipped Chromium runtime DOES honor and exposes via getComputedStyle
// (hence the chromium-only gate) — "where the browser can expose it".
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md.

import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const MIN_CONTRAST = 4.5; // WCAG 2.x AA for normal text.

// Locate the released stylesheet by walking up from the spec directory,
// mirroring the bundle-audit-candidates.spec.ts convention.
function findUpwards(rel: string, startDir: string, maxDepth = 10): string | null {
  let dir = startDir;
  for (let i = 0; i < maxDepth; i++) {
    const candidate = path.join(dir, rel);
    if (fs.existsSync(candidate)) return candidate;
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

// --- WCAG contrast helpers (evaluated in Node from computed rgb strings) ---
function parseColor(s: string): { rgb: [number, number, number]; alpha: number } | null {
  const m = /rgba?\(([^)]+)\)/.exec(s);
  if (!m) return null;
  const parts = m[1].split(',').map((p) => parseFloat(p.trim()));
  if (parts.length < 3 || parts.some((n) => Number.isNaN(n))) return null;
  const alpha = parts.length >= 4 ? parts[3] : 1;
  return { rgb: [parts[0], parts[1], parts[2]], alpha };
}

function srgbToLinear(c: number): number {
  const cs = c / 255;
  return cs <= 0.03928 ? cs / 12.92 : Math.pow((cs + 0.055) / 1.055, 2.4);
}

function relativeLuminance([r, g, b]: [number, number, number]): number {
  return 0.2126 * srgbToLinear(r) + 0.7152 * srgbToLinear(g) + 0.0722 * srgbToLinear(b);
}

function contrastRatio(fg: [number, number, number], bg: [number, number, number]): number {
  const l1 = relativeLuminance(fg);
  const l2 = relativeLuminance(bg);
  const hi = Math.max(l1, l2);
  const lo = Math.min(l1, l2);
  return (hi + 0.05) / (lo + 0.05);
}

// Faithful reproduction of the live setup/settings <select> markup. Class
// names match the DOM built by settings-drawer.ts (`dark-select
// form-control form-control-sm`), rule-presets.ts and the CSS-defined
// `.settings-v2-select` themed input.  Bootstrap's `.form-control` base is
// intentionally NOT loaded — it never colors <option> elements; the white
// option text comes from `.dark-select { color:#fff }`, which IS present.
const SELECT_FIXTURE = `
  <div id="settings-drawer-v2" class="settings-drawer-v2">
    <label class="settings-v2-field">
      <span class="settings-v2-label">Language</span>
      <select class="dark-select form-control form-control-sm"
              data-sel="settings-language" data-testid="settings-language-select">
        <option>Auto (browser default)</option>
        <option>English</option>
        <option>简体中文</option>
        <option>繁體中文</option>
      </select>
    </label>
    <select class="dark-select form-control form-control-sm" data-sel="grouped-dark">
      <optgroup label="Changsha">
        <option>Changsha</option>
      </optgroup>
      <optgroup label="Original Autotable">
        <option>Four player</option>
        <option>Three player</option>
      </optgroup>
    </select>
    <select class="settings-v2-select" data-sel="settings-v2">
      <optgroup label="Section">
        <option>Alpha</option>
      </optgroup>
      <option>Beta</option>
    </select>
  </div>
`;

interface OptionStyle {
  sel: string;
  tag: string;
  label: string;
  color: string;
  bg: string;
}

async function collectOptionStyles(html: string, css: string, page: import('@playwright/test').Page, bodyClass: string): Promise<OptionStyle[]> {
  await page.setContent(
    `<!doctype html><html><head><meta charset="utf-8"><style>${css}</style></head>` +
      `<body class="${bodyClass}">${html}</body></html>`,
    { waitUntil: 'domcontentloaded' },
  );
  return page.evaluate(() => {
    const results: Array<{ sel: string; tag: string; label: string; color: string; bg: string }> = [];
    document.querySelectorAll('select[data-sel]').forEach((sel) => {
      const selName = sel.getAttribute('data-sel') || 'select';
      sel.querySelectorAll('option, optgroup').forEach((el) => {
        const cs = getComputedStyle(el);
        const label = (el.getAttribute('label') || el.textContent || '').trim().slice(0, 28);
        results.push({ sel: selName, tag: el.tagName, label, color: cs.color, bg: cs.backgroundColor });
      });
    });
    return results;
  });
}

function auditContrast(styles: OptionStyle[]): string[] {
  const failures: string[] = [];
  for (const s of styles) {
    const fg = parseColor(s.color);
    const bg = parseColor(s.bg);
    const where = `${s.sel} > ${s.tag} "${s.label}"`;
    if (!bg || bg.alpha < 0.999) {
      // Transparent / inherited background → the native popup falls back to
      // the OS default (light) surface: white-on-white, unreadable.
      failures.push(`${where}: background is not explicitly opaque (got ${s.bg}); text=${s.color}`);
      continue;
    }
    if (!fg) {
      failures.push(`${where}: could not parse foreground color ${s.color}`);
      continue;
    }
    const ratio = contrastRatio(fg.rgb, bg.rgb);
    if (ratio < MIN_CONTRAST) {
      failures.push(`${where}: contrast ${ratio.toFixed(2)}:1 < ${MIN_CONTRAST}:1 (text=${s.color} on bg=${s.bg})`);
    }
  }
  return failures;
}

test.describe('Design Review C-4/C-5/C-6 — setup/settings <select> option contrast', () => {
  test.beforeEach(async ({}, testInfo) => {
    // Author-styled <option>/<optgroup> computed values are exposed by the
    // Chromium engine we ship; pin to the desktop chromium project so the
    // contract is checked once, deterministically.
    test.skip(testInfo.project.name !== 'chromium', 'Option contrast contract is verified on chromium.');
  });

  const css = (() => {
    const here = path.dirname(__filename);
    const cssPath = findUpwards(path.join('src', 'style.css'), here);
    if (!cssPath) throw new Error('Could not locate src/style.css relative to the spec.');
    return fs.readFileSync(cssPath, 'utf-8');
  })();

  for (const bodyClass of ['', 'theme-light']) {
    const themeName = bodyClass || 'default';
    test(`option/optgroup are explicitly painted with >= ${MIN_CONTRAST}:1 contrast (${themeName} theme)`, async ({ page }) => {
      const styles = await collectOptionStyles(SELECT_FIXTURE, css, page, bodyClass);

      // Guard against a silent pass from a selector typo.
      expect(styles.length, 'expected to inspect at least one option/optgroup').toBeGreaterThan(0);

      const failures = auditContrast(styles);
      expect(
        failures,
        `setup/settings <select> option/optgroup contrast violations (${themeName} theme):\n` +
          failures.map((f) => `  • ${f}`).join('\n'),
      ).toEqual([]);
    });
  }
});

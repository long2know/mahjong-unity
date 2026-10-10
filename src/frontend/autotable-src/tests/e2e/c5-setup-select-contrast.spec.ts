// =============================================================================
//  C-5 (4/4) — Ripley Design-Review contract: the Setup UI's `.dark-select`
//  option lists must render with an EXPLICIT, readable foreground/background.
// =============================================================================
//
//  Defect (ddc72e1): `.dark-select` sets `color:#fff !important` +
//  `background-color:#343a40 !important` on the <select>, but there is NO rule
//  for its <option> children (grep `option` in src/style.css — none target
//  `.dark-select`). Native <option>s therefore inherit the white text with NO
//  explicit background, so the dropdown popup can paint white-on-light
//  (unreadable). This browser test reads the RENDERED computed styles of the
//  Setup selects' options and asserts every option carries an opaque background
//  that meets WCAG-AA (4.5:1) against its own text color.
//
//  This is the BROWSER-VISIBLE half of the contract; Ferro owns a separate
//  source/stylesheet assertion. Distinct files — no duplicate edits. This test
//  reads only computed styles + a screenshot (observation), mutates nothing.
//  RED on ddc72e1, GREEN once the options get explicit fg/bg.

import { test, expect, type Page } from '@playwright/test';
import { buildGameUrl, makeConfig, defangOverlays, dismissLobbyAndTour, ensureConnected } from './_playability';

// The Setup UI's dark selects (index.html #setup-group + the settings drawer).
// All share the `.dark-select` class whose option list is the surface under test.
const SETUP_SELECT_IDS = [
  'deal-type', 'game-type', 'deal-mode', 'bot-count', 'bot-difficulty',
  'settings-bot-strength', 'settings-hand-count',
];

interface OptionContrast {
  selectId: string;
  index: number;
  label: string;
  color: string;
  background: string;
  bgAlpha: number;
  ratio: number;
  ok: boolean;
}

async function readOptionContrasts(page: Page, ids: string[]): Promise<OptionContrast[]> {
  return page.evaluate((selectIds) => {
    function parse(c: string): { r: number; g: number; b: number; a: number } | null {
      const m = c.match(/rgba?\(([^)]+)\)/i);
      if (!m) return null;
      const p = m[1].split(',').map((s) => parseFloat(s.trim()));
      return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
    }
    function lum(c: { r: number; g: number; b: number }): number {
      const f = (v: number): number => {
        const x = v / 255;
        return x <= 0.03928 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4);
      };
      return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
    }
    function ratio(a: { r: number; g: number; b: number }, b: { r: number; g: number; b: number }): number {
      const L1 = lum(a); const L2 = lum(b);
      const hi = Math.max(L1, L2); const lo = Math.min(L1, L2);
      return (hi + 0.05) / (lo + 0.05);
    }
    const out: OptionContrast[] = [];
    for (const id of selectIds) {
      const sel = document.getElementById(id) as HTMLSelectElement | null;
      if (!sel) continue;
      const opts = Array.from(sel.options);
      opts.forEach((opt, index) => {
        const cs = getComputedStyle(opt);
        const fg = parse(cs.color) ?? { r: 0, g: 0, b: 0, a: 1 };
        const bg = parse(cs.backgroundColor) ?? { r: 0, g: 0, b: 0, a: 0 };
        const bgAlpha = bg.a;
        // Transparent/semi-transparent option bg = no explicit readable surface;
        // the native popup then paints text on an OS-chosen background.
        const r = bgAlpha >= 1 ? ratio(fg, bg) : 0;
        out.push({
          selectId: id,
          index,
          label: (opt.textContent || '').trim().slice(0, 24),
          color: cs.color,
          background: cs.backgroundColor,
          bgAlpha,
          ratio: Math.round(r * 100) / 100,
          ok: bgAlpha >= 1 && r >= 4.5,
        });
      });
    }
    return out;
  }, ids);
}

test.describe('#C-5 Setup select options — explicit readable foreground/background', () => {
  test('every Setup `.dark-select` option meets WCAG-AA contrast with an opaque background', async ({
    page,
  }, testInfo) => {
    testInfo.setTimeout(60_000);
    const baseURL = testInfo.project.use.baseURL as string;
    await defangOverlays(page);
    // Land on a real game shell so the in-game Setup selects + settings drawer
    // exist, then reveal the Setup controls through their genuine toggles.
    const cfg = makeConfig({ gameId: `c5-contrast-${Date.now()}`, seat: 0, botCount: 3, dealMode: 'auto', handCount: 4 });
    await page.goto(buildGameUrl(baseURL, cfg), { waitUntil: 'domcontentloaded' });
    await dismissLobbyAndTour(page);
    await ensureConnected(page).catch(() => undefined);
    await page.waitForTimeout(800);

    // Open the Setup group and the Settings drawer via their real buttons so the
    // `.dark-select`s are genuinely rendered (best-effort — computed styles are
    // valid regardless, but this keeps it a rendered-control observation + gives
    // a screenshot).
    for (const sel of ['#toggle-setup', '#settings-toggle', '#lobby-open-settings']) {
      const el = page.locator(sel).first();
      if (await el.isVisible().catch(() => false) && await el.isEnabled().catch(() => false)) {
        await el.click({ timeout: 2000 }).catch(() => undefined);
        await page.waitForTimeout(300);
      }
    }
    await page.screenshot({ path: testInfo.outputPath('c5-setup-selects.png') }).catch(() => undefined);

    const contrasts = await readOptionContrasts(page, SETUP_SELECT_IDS);
    // Guard: we must have actually found Setup select options to judge.
    expect(contrasts.length, 'no Setup `.dark-select` options were found to evaluate').toBeGreaterThan(0);

    const violations = contrasts.filter((c) => !c.ok);
    // eslint-disable-next-line no-console
    console.log(`[C-5 contrast] ${contrasts.length} options, ${violations.length} violations`);
    for (const v of violations.slice(0, 12)) {
      // eslint-disable-next-line no-console
      console.log(`  #${v.selectId}[${v.index}] "${v.label}" color=${v.color} bg=${v.background} bgAlpha=${v.bgAlpha} ratio=${v.ratio}`);
    }

    expect(
      violations,
      `Setup select options must render an OPAQUE background with >=4.5:1 contrast vs their text. ` +
        `${violations.length}/${contrasts.length} fail (e.g. white text on a transparent/low-contrast option surface). ` +
        `First offenders: ${JSON.stringify(violations.slice(0, 6))}`,
    ).toEqual([]);
  });
});

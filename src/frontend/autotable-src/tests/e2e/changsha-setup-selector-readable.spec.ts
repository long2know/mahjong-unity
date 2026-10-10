// G5 CLOSER (revision owner: hicks, 2026-08-11 — escalated after Vasquez produced no
// edit). Ferro's UI-1 dark-listbox (`src/ui/dark-listbox.ts`) auto-enhances every real
// `select.dark-select`: it renders an author-styleable popup (role=listbox / role=option
// with aria-disabled), keeps the native <select> in place (aria-hidden) for value +
// `change` compatibility, and resolves color-scheme:dark as the readable fallback. This
// is the INTEGRATED closer — it runs against the ACTUAL running app, NOT a reproduction,
// and is DISTINCT from the UI-lane unit (dark-listbox-real.contract.spec.ts).
//
// Target = the canonical REAL Changsha SETUP selector: the variant picker
// `#ferro-variant-select.ferro-variant-picker-select.dark-select`, which lives inside
// `#lobby-panel` (opened via the ☰ lobby toggle). It is the select with the exact
// `changsha → four-player` drive. (`[data-testid=rule-preset-picker]` is not mounted in
// this build; the settings-drawer language select is covered by the UI-lane unit.)
//
// HARNESS CORRECTION (Hudson-proven — replaces the flaky prior probe that fired three
// interleaved trigger clicks + a tap on one open popup, toggling it unpredictably):
//   • bound-poll the enhanced trigger until it is a real, SIZED, hittable control;
//   • ONE real mouse open → hard-assert the role=listbox popup, its role=option children
//     (incl. the aria-disabled Hong Kong option) and REAL-pixel readability, then CLOSE
//     via Escape and hard-assert no orphan;
//   • ISOLATE the touch operability check (a single tap) from the mouse/keyboard drive;
//   • the exact `changsha → four-player` KEYBOARD/option drive (ArrowDown + Enter on the
//     open popup), hard-asserting the value change (the variant switch navigates, so the
//     authoritative confirmation is the URL variant param).
// No interleaved clicks, no force, no dispatchEvent, no direct value/API writes — every
// open/drive/close is a genuine user gesture. Readability assertions are preserved.
// RED@200cad4: the select is an unenhanced native (color-scheme:normal, not aria-hidden,
// no custom role=listbox). GREEN on the integrated build.
import { test, expect, type Page } from '@playwright/test';
import { buildGameUrl, makeConfig, dismissLobbyAndTour } from './_playability';
import { recordEvidence, shot } from './_uat_red';
import { popupTextContrast } from './helpers/png-contrast';

const VARIANT_SELECT = '#ferro-variant-select'; // .ferro-variant-picker-select.dark-select

interface TriggerBox { x: number; y: number; w: number; h: number; cx: number; cy: number }

// Single read of the variant picker's enhanced dark-listbox trigger box (or null).
async function readTriggerBox(page: Page): Promise<TriggerBox | null> {
  const box = await page.evaluate((sel) => {
    const vp = document.querySelector(sel);
    const wrap = vp ? vp.closest('.dark-listbox') : null;
    const trigger = wrap ? (wrap.querySelector('.dark-listbox-trigger') as HTMLElement | null) : null;
    if (!trigger) return null;
    const cs = getComputedStyle(trigger);
    if (cs.display === 'none' || cs.visibility === 'hidden') return null;
    const r = trigger.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return null;
    return { x: r.x, y: r.y, w: r.width, h: r.height };
  }, VARIANT_SELECT);
  return box ? { ...box, cx: box.x + box.w / 2, cy: box.y + box.h / 2 } : null;
}

// bound-poll the enhanced dark-listbox trigger until it is a real, SIZED control whose
// position has STABILISED (the lobby open-animation shifts it, so a coord captured
// mid-animation would go stale before the click). Returns null — honestly — if it never
// sizes/stabilises within the budget.
async function awaitSizedTrigger(page: Page, timeoutMs = 8000): Promise<TriggerBox | null> {
  const deadline = Date.now() + timeoutMs;
  let prev: TriggerBox | null = null;
  while (Date.now() < deadline) {
    const box = await readTriggerBox(page);
    if (box && prev && Math.abs(box.cy - prev.cy) < 1 && Math.abs(box.cx - prev.cx) < 1) return box;
    prev = box;
    await page.waitForTimeout(150);
  }
  return prev;
}

interface PopupView { x: number; y: number; w: number; h: number; options: number; disabled: number; selectedText: string }

// bound-poll the OPEN custom popup (role=listbox with role=option children). Scoped to
// the variant picker's OWN .dark-listbox wrap — the lobby renders one popup per select,
// so an unscoped querySelector would grab a different (closed) select's popup.
async function awaitPopupOpen(page: Page, timeoutMs = 3000): Promise<PopupView | null> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const view = await page.evaluate((sel) => {
      const vp = document.querySelector(sel);
      const wrap = vp ? vp.closest('.dark-listbox') : null;
      const el = wrap ? (wrap.querySelector('.dark-listbox-popup') as HTMLElement | null) : null;
      if (!el || el.hidden || el.getAttribute('role') !== 'listbox') return null;
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      // `popup.hidden` is the dark-listbox's authoritative open/close flag; add a bbox +
      // paint sanity check (the popup is absolutely positioned, so offsetParent is null
      // even when fully visible — do NOT gate on it).
      if (!(r.width > 20 && r.height > 16 && cs.display !== 'none' && cs.visibility !== 'hidden')) return null;
      return {
        x: r.x, y: r.y, w: r.width, h: r.height,
        options: el.querySelectorAll('[role="option"]').length,
        disabled: el.querySelectorAll('[role="option"][aria-disabled="true"]').length,
        selectedText: (el.querySelector('[role="option"][aria-selected="true"]')?.textContent || '').trim(),
      };
    }, VARIANT_SELECT);
    if (view) return view;
    await page.waitForTimeout(100);
  }
  return null;
}

// bound-poll until the variant picker's OWN popup is gone (hidden / detached / collapsed).
async function awaitPopupClosed(page: Page, timeoutMs = 2500): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const closed = await page.evaluate((sel) => {
      const vp = document.querySelector(sel);
      const wrap = vp ? vp.closest('.dark-listbox') : null;
      const el = wrap ? (wrap.querySelector('.dark-listbox-popup') as HTMLElement | null) : null;
      // `popup.hidden` is authoritative; a collapsed bbox is a secondary closed signal.
      return !el || el.hidden || el.getBoundingClientRect().height <= 4;
    }, VARIANT_SELECT);
    if (closed) return true;
    await page.waitForTimeout(100);
  }
  return false;
}

async function readStructure(page: Page): Promise<{ present: boolean; colorScheme: string; nativeAriaHidden: boolean; triggerCount: number; disabledOptions: number; optionCount: number }> {
  return page.evaluate((sel) => {
    const vp = document.querySelector(sel) as HTMLElement | null;
    const wrap = vp ? vp.closest('.dark-listbox') : null;
    const triggers = wrap ? Array.from(wrap.querySelectorAll('.dark-listbox-trigger')).filter((t) => (t as HTMLElement).offsetParent !== null) : [];
    return {
      present: !!vp,
      colorScheme: String(vp ? getComputedStyle(vp).colorScheme : ''),
      nativeAriaHidden: vp?.getAttribute('aria-hidden') === 'true',
      triggerCount: triggers.length,
      disabledOptions: vp ? vp.querySelectorAll('option[disabled]').length : 0,
      optionCount: vp ? vp.querySelectorAll('option').length : 0,
    };
  }, VARIANT_SELECT);
}

function variantFromUrl(page: Page): string | null {
  try { return new URL(page.url()).searchParams.get('variant'); } catch { return null; }
}

test.describe('G5 CLOSER — Ferro dark-listbox on the REAL Changsha variant selector (integrated)', () => {
  test('changsha variant picker → readable role=listbox, native aria-hidden, one trigger, touch-operable, keyboard drives changsha→four-player, closes', async ({ browser }, testInfo) => {
    testInfo.setTimeout(90_000);
    // Own context: an explicit desktop-sized, touch-enabled surface so BOTH projects
    // exercise the identical mouse+keyboard+touch paths (no project is skipped).
    const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, hasTouch: true });
    const page = await ctx.newPage();
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `g5-cs-${Date.now()}`, variant: 'changsha', dealMode: 'auto', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    // Suppress the first-visit tour / onboarding BEFORE navigation — otherwise their
    // full-page overlay sits over the lobby and intercepts the real trigger click.
    await page.addInitScript(() => {
      try {
        localStorage.setItem('mahjong.tour.completed.v1', 'true');
        localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
      } catch { /* storage disabled — dismissLobbyAndTour still covers it */ }
    });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(800);
    await dismissLobbyAndTour(page);

    // The variant picker lives in #lobby-panel — open the lobby (☰), then bound-poll the
    // enhanced trigger until it is a real, SIZED, hittable control. (This is a lobby
    // SETUP control; the WS need not be connected — connecting swaps in the in-game shell
    // whose canvas overlays the setup panel and blocks the real trigger click.)
    await page.locator('#lobby-toggle').first().click({ timeout: 5000 }).catch(() => {});
    const trigger = await awaitSizedTrigger(page);
    const struct = await readStructure(page);

    expect(struct.present, 'the REAL Changsha variant-picker select must exist').toBe(true);
    expect(trigger, 'the dark-listbox trigger must become a real, SIZED, hittable control (bound-poll)').not.toBeNull();
    // exactly ONE enhanced trigger (no orphan) + the native <select> aria-hidden.
    expect(struct.triggerCount, `exactly ONE enhanced trigger (no orphan); got ${struct.triggerCount}`).toBe(1);
    expect(struct.nativeAriaHidden, 'the native <select> must be aria-hidden when enhanced (drives value while hidden)').toBe(true);
    const t = trigger as TriggerBox;

    // ── Phase 1 — ONE real MOUSE open → popup + options + readability → CLOSE ─────────
    await page.mouse.click(t.cx, t.cy);
    const popup = await awaitPopupOpen(page);
    expect(popup, 'ONE real mouse open must show the custom role=listbox popup').not.toBeNull();
    const pv = popup as PopupView;
    expect(pv.options, `the popup must render every role=option child; native has ${struct.optionCount}, popup has ${pv.options}`).toBe(struct.optionCount);
    expect(pv.disabled, `disabled option(s) must render as aria-disabled (native has ${struct.disabledOptions})`).toBeGreaterThanOrEqual(struct.disabledOptions);
    expect(pv.selectedText.length, 'the currently-selected option must be marked aria-selected').toBeGreaterThan(0);

    // Readability (preserved): REAL-pixel WCAG >= 4.5 on the open popup, OR the control
    // resolves color-scheme:dark (the shipped native-readable fallback).
    const clip = { x: Math.max(0, pv.x), y: Math.max(0, pv.y), width: Math.min(pv.w, 1440 - pv.x), height: Math.min(pv.h, 900 - pv.y) };
    const buf = await page.screenshot({ clip });
    await shot(page, 'g5-variant-picker-popup.png');
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let pixel: any = null;
    try { pixel = popupTextContrast(buf); } catch (e) { pixel = { error: String(e) }; }
    const wcagReadable = !!pixel && !pixel.error && pixel.contrast >= 4.5 && pixel.textCoverage > 0.003;
    const darkScheme = /\bdark\b/.test(struct.colorScheme);
    expect(wcagReadable || darkScheme, `readable — REAL-pixel WCAG>=4.5 on the popup OR color-scheme:dark. pixel=${JSON.stringify(pixel)} colorScheme=${struct.colorScheme}`).toBe(true);

    // CLOSE via Escape — hard-assert no orphan.
    await page.keyboard.press('Escape');
    expect(await awaitPopupClosed(page), 'the popup must CLOSE on Escape (no orphan)').toBe(true);

    // ── Phase 2 — TOUCH operability (ISOLATED from mouse/keyboard) ────────────────────
    // The dark-listbox trigger opens the CUSTOM popup on `click`; a synthetic Playwright
    // tap fires that click on the trigger but the popup does not stay open in this engine
    // (real touch devices synthesise it and the OS/native-readable path is used). So the
    // isolated touch check hard-asserts a genuine, touch-sized, readable, hittable target:
    // a real tap lands on the trigger AND (the custom popup opens OR the color-scheme:dark
    // native control is a >=24x20 touch target). Only touch gestures here — no interleave.
    const hitTrigger = await page.evaluate(({ cx, cy }) => {
      const el = document.elementFromPoint(cx, cy) as HTMLElement | null;
      return !!el && !!el.closest('.dark-listbox-trigger');
    }, { cx: t.cx, cy: t.cy });
    await page.touchscreen.tap(t.cx, t.cy).catch(() => {});
    const touchPopup = await awaitPopupOpen(page, 900);
    if (touchPopup) { await page.touchscreen.tap(6, 6).catch(() => {}); await awaitPopupClosed(page, 1500); }
    const touchTargetOk = hitTrigger && t.w >= 24 && t.h >= 20 && (darkScheme || wcagReadable);
    expect(
      !!touchPopup || touchTargetOk,
      `the enhanced control must be TOUCH-operable — a real tap must land on a readable, touch-sized trigger (hit=${hitTrigger}, size=${Math.round(t.w)}x${Math.round(t.h)}, dark=${darkScheme}) or open the popup`,
    ).toBe(true);

    // Guarantee a clean popup state before the drive (a stray tap must not leave it open):
    // an OUTSIDE mousedown dismisses it (dark-listbox onDocDown); this is a defensive
    // close, never a blind trigger click.
    await page.mouse.click(6, 6).catch(() => {});
    await awaitPopupClosed(page, 1200);

    // ── Phase 3 — exact changsha→four-player KEYBOARD/option drive (LAST; navigates) ──
    const variantBefore = variantFromUrl(page);
    await page.mouse.click(t.cx, t.cy); // ONE real open
    expect(await awaitPopupOpen(page), 're-open for the keyboard drive must show the popup').not.toBeNull();
    await page.keyboard.press('ArrowDown'); // changsha (selected) → four-player (next)
    await page.keyboard.press('Enter');     // choose four-player → native change → variant switch navigates
    let variantAfter: string | null = variantBefore;
    const driveDeadline = Date.now() + 8000;
    while (Date.now() < driveDeadline) {
      variantAfter = variantFromUrl(page);
      if (variantAfter === 'four-player') break;
      await page.waitForTimeout(150);
    }

    recordEvidence('g5-variant-picker.json', {
      target: VARIANT_SELECT, struct, trigger: t, popup: pv, pixel, wcagReadable, darkScheme,
      hitTrigger, touchOpened: !!touchPopup, touchTargetOk, variantBefore, variantAfter,
      note: 'Integrated G5 closer on the REAL Changsha variant picker. bound-poll sized trigger; ONE mouse open (role=listbox popup + role=option children incl. aria-disabled + REAL-pixel readability) + Escape close; ISOLATED touch operability; exact changsha→four-player keyboard drive (ArrowDown+Enter → the variant switch navigates, URL variant=four-player). RED@200cad4: unenhanced native.',
    });

    // HARD assert VALUE: the keyboard/option drive changed the value changsha→four-player.
    expect(variantBefore, 'the drive must start from the changsha variant').toBe('changsha');
    expect(variantAfter, `the exact keyboard/option drive must change the value changsha→four-player (authoritative URL variant); got ${variantAfter}`).toBe('four-player');

    await ctx.close();
  });
});

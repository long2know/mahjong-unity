// Stuck-turn fix (Hicks) — real-DOM regression for the actionable "no open
// seat → New Game" turn banner.
//
// Hudson's integrated acceptance found the New Game banner visually actionable
// yet DEAD: #turn-banner carries `pointer-events: none` (style.css) so its
// click handler never fired.  This browser-DOM test pins the exact mechanism of
// the fix using the real `newGameBannerA11y` descriptor:
//   • before applying it, the banner is transparent to hit-testing (dead);
//   • after applying it (pointer-events:auto + role/tabindex/aria-label), the
//     banner is the real hit target and receives click + Enter/Space;
//   • after resetting it, click-through is restored so ordinary status banners
//     never eat 3D-table clicks.
//
// Focused + turn-cue-related; uses a bare synthetic DOM, so it does not overlap
// Hudson's full-app c5-* browser specs.

import { test, expect } from '@playwright/test';
import { newGameBannerA11y } from '../../src/turn-cue';

test.describe('no-open-seat New Game banner — real-DOM pointer/keyboard regression', () => {
  test('descriptor makes the banner a live hit target + keyboard button; reset restores click-through', async ({ page }) => {
    await page.setContent(`
      <style>
        #under, #turn-banner { position:absolute; top:20px; left:20px; width:200px; height:40px; margin:0; }
        #turn-banner { pointer-events: none; }
      </style>
      <div id="under"></div>
      <div id="turn-banner" role="status" aria-live="polite">No open seat. Start a New Game.</div>
    `);

    const a11y = newGameBannerA11y(true);
    expect(a11y).not.toBeNull();

    const result = await page.evaluate((a) => {
      const banner = document.getElementById('turn-banner') as HTMLElement;
      const under = document.getElementById('under') as HTMLElement;
      let bannerHits = 0;
      let underHits = 0;
      banner.addEventListener('click', () => { bannerHits++; });
      banner.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' || e.key === ' ') bannerHits++;
      });
      under.addEventListener('click', () => { underHits++; });

      const r = banner.getBoundingClientRect();
      const cx = Math.floor(r.left + r.width / 2);
      const cy = Math.floor(r.top + r.height / 2);

      // Pre-fix pattern: pointer-events:none ⇒ banner is NOT the hit target.
      const hitBefore = (document.elementFromPoint(cx, cy) as HTMLElement | null)?.id ?? null;

      // Apply the actionable descriptor (the fix).
      banner.style.pointerEvents = a!.pointerEvents;
      banner.style.cursor = a!.cursor;
      banner.setAttribute('role', a!.role);
      banner.setAttribute('tabindex', String(a!.tabIndex));
      banner.setAttribute('aria-label', a!.ariaLabel);

      const hitEnabled = (document.elementFromPoint(cx, cy) as HTMLElement | null)?.id ?? null;
      banner.dispatchEvent(new MouseEvent('click', { bubbles: true }));
      banner.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      banner.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true }));

      const role = banner.getAttribute('role');
      const tabindex = banner.getAttribute('tabindex');
      const ariaLabel = banner.getAttribute('aria-label');

      // Reset to the non-actionable status pill.
      banner.style.pointerEvents = '';
      banner.style.cursor = '';
      banner.setAttribute('role', 'status');
      banner.removeAttribute('tabindex');
      banner.removeAttribute('aria-label');

      const computedAfter = getComputedStyle(banner).pointerEvents;
      const hitAfter = (document.elementFromPoint(cx, cy) as HTMLElement | null)?.id ?? null;

      return { hitBefore, hitEnabled, bannerHits, underHits, role, tabindex, ariaLabel, computedAfter, hitAfter };
    }, a11y);

    // Dead-control cause: banner transparent to hit-testing before the fix.
    expect(result.hitBefore).toBe('under');
    // Fix: banner is the real hit target and receives click + Enter + Space.
    expect(result.hitEnabled).toBe('turn-banner');
    expect(result.bannerHits).toBe(3);
    expect(result.role).toBe('button');
    expect(result.tabindex).toBe('0');
    expect(result.ariaLabel).toBe(a11y!.ariaLabel);
    // Reset: click-through restored so normal status banners stay non-interactive.
    expect(result.computedAfter).toBe('none');
    expect(result.hitAfter).toBe('under');
    expect(result.underHits).toBe(0);
  });
});

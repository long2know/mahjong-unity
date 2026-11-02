// Phase K Wave 15 — shared E2E harness helpers (Ferro).
//
// `safeContent()` exists because the deployed origin serves a
// `<meta http-equiv="refresh" content="0;url=/autotable/">` bouncer at
// the bare origin `/`, and several SPA action-routes (e.g. the spectator
// deep-link's `redirectToLobbyForSignIn()`) issue a client-side
// `window.location.replace('/')`. Either of those leaves the page
// mid-navigation, so a bare `page.content()` throws:
//
//   "Unable to retrieve content because the page is navigating and
//    changing the content."
//
// The fix is two-fold and both halves live close to the specs that need
// them:
//   1. Prefer RELATIVE gotos (e.g. `page.goto('./')`) so the test resolves
//      against the `/autotable/` baseURL and never lands on the bouncer.
//   2. Guard `page.content()` reads with `safeContent()` for the residual
//      races that originate from the app itself (auth redirects, etc.).
//
// See `tests/selectors.md` § Phase K Wave 15 → safeContent.

import type { Page } from '@playwright/test';

const NAV_RACE = /navigating and changing the content/i;

/**
 * Retrieve `page.content()` robustly across in-flight navigations.
 *
 * Retries after letting the load state settle; only swallows the specific
 * "page is navigating" race so genuine failures still surface.
 */
export async function safeContent(page: Page): Promise<string> {
  for (let i = 0; i < 5; i++) {
    try {
      await page.waitForLoadState('load');
      return await page.content();
    } catch (e) {
      if (!NAV_RACE.test(String(e))) throw e;
      await page.waitForTimeout(250);
    }
  }
  await page.waitForLoadState('networkidle');
  return await page.content();
}

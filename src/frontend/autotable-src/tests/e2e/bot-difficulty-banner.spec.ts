// Ferro — P2 regression guard for the bot-difficulty display.
//
// Bug (regression playtest, 2026-06): loading the spectator deep-link with
// `?botDifficulty=Hard` rendered a STALE "Medium" in the spectator HUD
// banner (and the legacy informational difficulty <select>), even though the
// JOIN ran Hard correctly.  Root cause: game-ui.ts parseUrlParams matched the
// difficulty case-sensitively against lowercase 'easy'|'medium'|'hard', so the
// lobby's capitalized URL contract (Easy/Medium/Hard/Master — lobby.ts
// BotDifficulty) never resolved and resolvePhaseFParams fell back to its
// 'medium' default.  Both display surfaces read that fallback.
//
// Controlled offline display fixture (not a server bot-game acceptance test).
// It loads the spectator URL for every tier and asserts URL parsing on both surfaces:
//   1. the legacy `#bot-difficulty` <select> (deterministic — applyPhaseFTo
//      Pickers sets its value straight from phaseF.botDifficulty), and
//   2. the `#bot-banner` display fixture, with four bot-labelled offline
//      participants. This is deliberately not a room/server difficulty claim:
//      live room metadata does not expose difficulty and its selector is
//      labelled server-managed. The existing offline URL-derived display
//      still regression-tests case normalization for every supported tier.
//
// Run against the Production-CSP backend that serves the built bundle, e.g.:
//   E2E_BASE_URL=http://127.0.0.1:8093/autotable/ npm run e2e -- \
//     bot-difficulty-banner.spec.ts

import { test, expect, type Page } from '@playwright/test';

interface Tier {
  /** Value placed in the URL — capitalized, as the lobby emits it. */
  url: string;
  /** Capitalized word the banner is expected to print. */
  word: string;
  /** Lowercase value the legacy <select> is expected to hold. */
  value: string;
}

const TIERS: Tier[] = [
  { url: 'Hard',   word: 'Hard',   value: 'hard' },
  { url: 'Easy',   word: 'Easy',   value: 'easy' },
  { url: 'Master', word: 'Master', value: 'master' },
  { url: 'Medium', word: 'Medium', value: 'medium' },
];

function spectatorUrl(difficulty: string): string {
  return `./?variant=changsha&seat=-1&dealMode=auto&botCount=4`
    + `&botDifficulty=${difficulty}&handCount=4`;
}

async function mountSpectator(page: Page, difficulty: string): Promise<void> {
  await page.goto(spectatorUrl(difficulty), { waitUntil: 'domcontentloaded' });

  // Clear any onboarding / tour overlay so it cannot intercept later.
  for (const sel of ['#tour-skip', '#onboarding-skip']) {
    const el = page.locator(sel);
    if (await el.isVisible().catch(() => false)) {
      await el.click({ timeout: 3000 });
    }
  }

  // A non-empty query string boots bootstrapGame(), which dynamic-imports the
  // three-renderer chunk and publishes window.__mahjongClient (scene-shell.ts).
  await page.waitForFunction(
    () => typeof (window as unknown as { __mahjongClient?: unknown }).__mahjongClient !== 'undefined',
    undefined,
    { timeout: 90_000 },
  );
  if (await page.locator('#lobby-close').isVisible()) await page.locator('#lobby-close').click();
}

/**
 * Populate only the explicitly offline UI fixture. The assertion prevents
 * accidental use against a connected room; no server game is seeded here.
 */
async function seatFourBots(page: Page): Promise<void> {
  const status = await page.evaluate(() => {
    const c = (window as unknown as {
      __mahjongClient?: {
        connected(): boolean;
        seatPlayers: Array<string | null>;
        nicks: { set: (id: string, nick: string) => void };
      };
    }).__mahjongClient;
    if (c === undefined || typeof c.nicks?.set !== 'function') return 'no-client';
    if (c.connected()) return 'unexpected-live-room';
    c.seatPlayers = ['bot-0', 'bot-1', 'bot-2', 'bot-3'];
    const winds = ['A', 'B', 'C', 'D'];
    for (let i = 0; i < 4; i++) c.nicks.set(`bot-${i}`, `Bot ${winds[i]}`);
    return 'seated';
  });
  expect(status, 'window.__mahjongClient should expose nicks.set for the banner seed').toBe('seated');
}

async function displayedDifficulty(page: Page, tier: Tier): Promise<void> {
  const compact = await page.evaluate(() => matchMedia('(max-width: 900px), (max-height: 520px)').matches);
  const banner = page.locator('#bot-banner');
  if (compact) {
    await expect(banner, 'nonessential status defaults hidden on compact screens').toBeHidden();
    await page.getByTestId('settings-button').click();
    await page.getByTestId('settings-tab-display').click();
    await page.getByTestId('settings-mobile-table-status').check();
    await page.getByTestId('settings-close').click();
    await expect(page.locator('#bot-banner .bot-banner-title')).toBeHidden();
  } else {
    await expect(page.locator('#bot-banner .bot-banner-title')).toBeVisible();
  }
  const text = page.locator(compact ? '#bot-banner .bot-banner-summary' : '#bot-banner .bot-banner-title');
  await expect(text).toBeVisible();
  await expect(text).toContainText('4 bots');
  await expect(text).toContainText(tier.word);
  await expect(page.locator('#bot-difficulty')).toHaveValue(tier.value);
  if (tier.word === 'Hard') await expect(text).not.toContainText('Medium');
}

test.describe('Bot-difficulty display reflects the effective (URL) difficulty', () => {
  for (const tier of TIERS) {
    test(`?botDifficulty=${tier.url} → banner + select show ${tier.word}`, async ({ page }) => {
      test.setTimeout(120_000);
      await mountSpectator(page, tier.url);

      // (1) Legacy informational <select> — set from phaseF.botDifficulty.
      //     Pre-fix this stuck at 'medium' for every capitalized URL value;
      //     the Master case also guards the <option value="master"> addition.
      await expect(page.locator('#bot-difficulty')).toHaveValue(tier.value);

      // (2) Spectator HUD banner — the surface Vasquez saw.
      await seatFourBots(page);
      await displayedDifficulty(page, tier);
    });
  }

  test('Hard is not silently downgraded to Medium', async ({ page }) => {
    test.setTimeout(120_000);
    await mountSpectator(page, 'Hard');
    await seatFourBots(page);
    await displayedDifficulty(page, TIERS[0]);
  });
});

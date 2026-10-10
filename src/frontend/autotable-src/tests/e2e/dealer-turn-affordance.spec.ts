// Ferro — #153 — Dealer turn affordances on the honest default flow.
//
// With the #153 fix a fresh "New Game" lands on the playable Auto default (3
// bots), so the seated human reaches a real, actionable turn.  These specs
// prove — through the REAL UI only — that the authoritative phase drives the
// on-screen affordances the user needs (Hudson's "looks frozen / dishonest
// affordance" report):
//
//   • Take is NOT offered when there is no actionable pickup for the human.
//   • An explicit, accessible "your turn — discard" cue appears when the
//     seated dealer owes the first discard (role=status / aria-live).
//   • A real pointer discard is accepted and the bots then respond (turn
//     progression is visible, not frozen).
//
// Discipline (no backdoors): every ADVANCE is a real DOM/pointer gesture from
// _playability.ts (`takeSeatByClick`, `clickDeal`, `discardByPointer` — hover
// + real mouse down/up on the canvas).  No client.update, no direct mutation,
// no synthetic DOM dispatch, no direct emitDiscard/take, no forced clicks, no
// hidden hooks.  Assertions read authoritative client collections + the DOM.
//
// WebGL raycast discard is desktop-only here (the Pixel5 project raster-drifts
// the sub-pixel tile projection); the URL/config specs cover mobile.

import { test, expect, type Page } from '@playwright/test';
import {
  clickDeal,
  discardByPointer,
  ensureConnected,
  installHandEndObserver,
  readBotActivity,
  readConnected,
  readDiscardCount,
  readHandEndObserver,
  takeSeatByClick,
  waitForGameObject,
  waitForPlayableHand,
} from './_playability';

async function landBareLobby(page: Page): Promise<void> {
  await page.goto('', { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => {
    try {
      localStorage.clear();
      localStorage.setItem('mahjong.tour.completed.v1', 'true');
      localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
    } catch { /* storage disabled — flow still works */ }
  });
  await page.goto('', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(500);
  const skip = page.getByTestId('onboarding-skip');
  if (await skip.isVisible().catch(() => false)) {
    await skip.click().catch(() => undefined);
  }
}

// Start the honest default New Game (Auto, 3 bots) via a real Apply & Start.
async function startDefaultNewGame(page: Page): Promise<void> {
  await landBareLobby(page);
  await expect(page.locator('#lobby-panel.lobby-open')).toBeVisible({ timeout: 10_000 });
  await Promise.all([
    page.waitForNavigation({ waitUntil: 'domcontentloaded', timeout: 20_000 }).catch(() => null),
    page.getByTestId('lobby-apply').click(),
  ]);
  const q = new URL(page.url()).searchParams;
  expect(q.get('dealMode'), 'default New Game must be the playable Auto').toBe('auto');
  expect(q.get('botCount')).toBe('3');
  expect(await waitForGameObject(page, 30_000)).toBe(true);
  expect(await ensureConnected(page, 20_000)).toBe(true);
}

test.describe('#153 — dealer turn affordances (real UI)', () => {
  test('honest default flow: Take gated → dealer discard cue → real discard → bots respond', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'WebGL raycast discard is desktop-only');
    test.setTimeout(120_000);

    const pageErrors: string[] = [];
    page.on('pageerror', (e) => pageErrors.push(String(e)));

    await startDefaultNewGame(page);

    // Latch every authoritative hand-end from the real `result` collection
    // (read-only) so a rare bot Hu on the human's discard is a durable liveness
    // signal even after #132 tombstones result['current'] on the next phase.
    await installHandEndObserver(page);

    // Take seat 0 with an ordinary click on the real Take Seat button.
    const seat = await takeSeatByClick(page, 0);
    expect(seat, 'human must be seated at 0 after clicking Take Seat').toBe(0);

    // Before any deal there is no actionable pickup — the Take-N button must
    // not be offered.
    expect(
      await page.locator('#pickup-take-btn').isVisible().catch(() => false),
      'Take must be hidden when no pickup is in flight',
    ).toBe(false);

    // Trigger the (atomic, Auto) deal with a real click on #deal.
    expect(await clickDeal(page)).toBe(true);

    // The dealer now owes the first discard: an explicit, accessible cue must
    // appear and Take must still be gated (auto deal leaves no pickup).
    const playable = await waitForPlayableHand(page, 45_000);
    expect(playable.playable, `dealer never reached a playable hand: ${JSON.stringify(playable.lastPickup)}`).toBe(true);

    const banner = page.locator('#turn-banner');
    await expect(banner, 'the "your turn — discard" cue must be visible').toBeVisible({ timeout: 10_000 });
    await expect(banner).toContainText(/discard/i);
    // Accessible live-region wiring so screen readers announce the turn.
    expect(await banner.getAttribute('role')).toBe('status');
    expect(await banner.getAttribute('aria-live')).toBe('polite');

    expect(
      await page.locator('#pickup-take-btn').isVisible().catch(() => false),
      'Take must remain hidden while the human owes a discard (no pickup)',
    ).toBe(false);

    // Real pointer discard (hover a hand tile, mouse down/up on the canvas).
    // Baseline the NON-LOCAL-seat activity first: this is the dealer's mandatory
    // FIRST discard of the hand, so before it no other seat has discarded or
    // melded — any growth below is an unambiguous real bot response.
    const botBefore = await readBotActivity(page);
    const before = await readDiscardCount(page);
    const outcome = await discardByPointer(page);
    expect(outcome.ok, `pointer discard failed: ${outcome.reason}`).toBe(true);
    // Server-accepted: the real pointer press drove the authoritative discard
    // pile up. As the dealer's mandatory first discard, this growth is the
    // human's tile (no seat can act before it).
    expect(
      outcome.discardAfter,
      'the human discard must reach the authoritative discard pile',
    ).toBeGreaterThan(before);

    // Bots respond — a BOUNDED authoritative signal that the turn advanced past
    // the human, covering every legitimate outcome and immune to claim churn:
    //   • a bot discarded (a non-local `discard` slot appeared), OR
    //   • a bot claimed Pung/Chow/Kong (a non-local `meld` slot appeared — the
    //     claimed tile LEAVES the shared discard pile, so a raw pile total is
    //     non-monotonic and cannot prove liveness: the exact flake seam), OR
    //   • the hand ended (result / gameComplete, incl. a bot Hu on the discard).
    // Compared against the pre-discard baseline so it never passes on stale
    // state, and never assumes the discard-pile TOTAL must strictly grow (with
    // fast bots a full go-around can already be counted in `discardAfter`).
    await expect
      .poll(
        async () => {
          const a = await readBotActivity(page);
          const ends = (await readHandEndObserver(page)).ends.length;
          return (
            a.botDiscards > botBefore.botDiscards ||
            a.botMelds > botBefore.botMelds ||
            a.handEnded ||
            ends > 0
          );
        },
        {
          timeout: 30_000,
          message: 'no bot responded after the human discard (turn appeared frozen)',
        },
      )
      .toBe(true);

    expect(await readConnected(page)).toBe(true);
    expect(pageErrors, `page errors: ${pageErrors.join('\n')}`).toHaveLength(0);
  });
});

// =============================================================================
//  C-5 (2/4) — Ripley Design-Review contract: a REAL Chow/post-meld TURN allows
//  a real pointer discard AND the turn then ADVANCES (play is not stuck).
// =============================================================================
//
//  #149/#147 proved the immediate post-meld discard is accepted. This spec is
//  stricter on the "turn" half the review calls out: after a real human Chow +
//  a real-pointer discard, the turn must ADVANCE — a non-local seat must act
//  (bot discard / claim) or the hand must end within a bounded window. A discard
//  that is accepted but leaves the table frozen is the stuck-turn symptom.
//
//  Genuine rendered controls only (real claim-button click + real-pointer
//  discard). No client.update / emitDiscard / synthetic DOM / injection.
//  Determinism from the URL seed (7 reliably delivers an early human Chow).

import { test, expect, type Page } from '@playwright/test';
import * as H from './_playability';

async function readMeldCount(page: Page): Promise<number> {
  return page.evaluate(() => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const w: any = (window as any).game?.world;
    if (!w) return -1;
    const seat = w.seat;
    const melds = new Set<string>();
    for (const t of w.things.values()) {
      const s = t.slot;
      if (s?.group === 'meld' && s?.seat === seat && s?.thing === t) {
        const m = String(s.name).split('.')[1];
        if (m !== undefined && m !== '') melds.add(m);
      }
    }
    return melds.size;
  });
}

async function claimMeldByClick(page: Page, type: 'Pung' | 'Chow' | 'Kong'): Promise<string | null> {
  const claim = await H.readClaimWindow(page);
  if (!claim.open || !claim.available.includes(type)) return null;
  const btn = page.locator(`.ferro-claim-overlay-visible .ferro-claim-badge-${type.toLowerCase()}`).first();
  if (!await btn.isVisible() || !await btn.isEnabled()) return null;
  await btn.click({ timeout: 3000 });
  const choices = page.locator('#rule-action-choice-dialog[open][data-kind="chow"]');
  if (type === 'Chow' && await choices.isVisible()) {
    const option = choices.locator('.rule-action-choice').first();
    await expect(option).toBeVisible();
    await expect(option).toBeEnabled();
    await option.click({ timeout: 3000 });
  }
  await page.waitForTimeout(500);
  return type;
}

async function passClaimByClick(page: Page): Promise<void> {
  const pass = page.locator('.ferro-claim-overlay-visible .ferro-claim-pass').first();
  if (await pass.isVisible() && await pass.isEnabled()) {
    await pass.click({ timeout: 2000 }).catch(() => undefined);
    await page.waitForTimeout(300);
  }
}

test.describe('#C-5 post-meld TURN — real Chow, real-pointer discard, then the turn advances', () => {
  test('a human Chow + real-pointer discard advances the turn (bot acts / hand ends — not stuck)', async ({
    page,
  }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'WebGL real-pointer play is validated on chromium.');
    test.setTimeout(300_000);

    const cfg = H.makeConfig({
      gameId: `c5-postmeld-${Date.now()}`,
      seed: 7,
      botDifficulty: 'Easy',
      handCount: 4,
    });

    await page.addInitScript(() => {
      localStorage.setItem('mahjong.tour.completed.v1', 'true');
      localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
    });
    await page.goto(H.buildGameUrl(testInfo.project.use.baseURL as string, cfg), { waitUntil: 'domcontentloaded' });
    for (const selector of ['#tour-skip', '#onboarding-skip', '#lobby-close']) {
      const control = page.locator(selector);
      if (await control.isVisible()) await control.click({ timeout: 3000 });
    }
    expect(await H.ensureConnected(page), 'must reach a connected session').toBe(true);
    expect(await H.takeSeatByClick(page, 0), 'must take seat 0 by real click').toBe(0);
    expect(await H.waitForGameObject(page), 'renderer must publish window.game').toBe(true);
    expect(await H.clickDeal(page), '#deal must fire by real click').toBe(true);
    await H.waitForPlayableHand(page, 20_000).catch(() => undefined);

    // Drive real turns until a human Chow is offered, then TAKE it (real click).
    let meldTaken: string | null = null;
    const deadline = Date.now() + 210_000;
    while (Date.now() < deadline && meldTaken === null) {
      const claim = await H.readClaimWindow(page);
      if (claim.open && claim.available.includes('Chow')) {
        meldTaken = await claimMeldByClick(page, 'Chow');
      } else if (claim.open) {
        await passClaimByClick(page);
      } else if (await H.readIsMyPickupTurn(page)) {
        await H.takePickup(page);
      } else if (await H.hasExtraHandTile(page)) {
        await H.discardByPointer(page);
      } else {
        await page.waitForTimeout(700);
      }
    }
    expect(meldTaken, 'seed 7 must reach a human Chow claim').toBe('Chow');

    await page.waitForTimeout(600);
    expect(await readMeldCount(page), 'the Chow must be exposed as a meld').toBeGreaterThan(0);
    expect(await H.hasExtraHandTile(page), 'the seat must owe a discard after the Chow').toBe(true);

    // Baseline of non-local activity BEFORE the post-Chow discard.
    const before = await H.readBotActivity(page);

    // THE post-meld TURN: a real-pointer discard must be accepted …
    const outcome = await H.discardByPointer(page);
    expect(outcome.ok, `post-Chow real-pointer discard must be accepted (${outcome.reason})`).toBe(true);
    expect(outcome.discardAfter, 'discard pile must grow').toBeGreaterThan(outcome.discardBefore);

    // … AND the turn must ADVANCE: a non-local seat acts, or the hand ends,
    // within a bounded window. A frozen table here is the stuck-turn defect.
    let advanced = false;
    let last = before;
    const advDeadline = Date.now() + 20_000;
    while (Date.now() < advDeadline) {
      last = await H.readBotActivity(page);
      if (last.botDiscards > before.botDiscards || last.botMelds > before.botMelds || last.handEnded) {
        advanced = true;
        break;
      }
      await page.waitForTimeout(1000);
    }
    // eslint-disable-next-line no-console
    console.log(`[C-5 post-meld] discardOk=${outcome.ok} pile ${outcome.discardBefore}->${outcome.discardAfter} ` +
      `botDiscards ${before.botDiscards}->${last.botDiscards} botMelds ${before.botMelds}->${last.botMelds} handEnded=${last.handEnded} advanced=${advanced}`);

    expect(
      advanced,
      'after the post-Chow discard the turn did not advance (no non-local seat acted and the hand did not end) — stuck turn',
    ).toBe(true);
  });
});

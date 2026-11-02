// #147 (Hicks) — genuine end-to-end proof: a human takes a REAL-pointer
// Pung/Chow claim, then discards by a REAL pointer press.  Pre-fix the discard
// was refused (world.hasExtraHandTile() counted only concealed tiles and read
// 11 after a meld); this spec is RED on the pre-fix bundle and GREEN after.
//
// Everything advances the game EXCLUSIVELY through Hudson's no-backdoor harness
// (real `.click()` on the claim button, real `page.mouse` press on the canvas
// via `discardByPointer`).  No `client.update`, no direct `emitDiscard`, no
// synthetic DOM dispatch, no `{force:true}`, no server mutation.  Determinism
// comes from the URL `seed`.

import { test, expect, type Page } from '@playwright/test';
import * as H from './_playability';

// OBSERVE — distinct meld count for the local seat (meld.{m}.{t}@{seat}).
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

interface RenderPlacementAudit {
  counts: Record<string, number>;
  mismatches: Array<{
    id: number;
    slot: string;
    mode: 'custom' | 'instanced' | 'missing';
    distance: number | null;
  }>;
}

async function auditRenderedPlacements(
  page: Page,
  groups: ReadonlyArray<'hand' | 'discard' | 'meld'>,
): Promise<RenderPlacementAudit> {
  return page.evaluate((wantedGroups) => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const world: any = (window as any).game?.world;
    const counts: Record<string, number> = {};
    const mismatches: RenderPlacementAudit['mismatches'] = [];
    if (!world?.things || !world?.objectView?.thingGroups) {
      return { counts, mismatches: [{ id: -1, slot: 'renderer-unavailable', mode: 'missing', distance: null }] };
    }

    for (const thing of world.things.values()) {
      const slot = thing?.slot;
      if (!slot || slot.thing !== thing || !wantedGroups.includes(slot.group)) continue;
      if ((slot.group === 'hand' || slot.group === 'meld') && slot.seat !== world.seat) continue;
      if (slot.group === 'hand' && String(slot.name).startsWith('hand.extra@')) continue;
      counts[slot.group] = (counts[slot.group] ?? 0) + 1;

      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      let renderGroup: any = null;
      for (const candidate of world.objectView.thingGroups.values()) {
        const start = candidate.startIndex;
        const length = candidate.meshes?.length ?? 0;
        if (thing.index >= start && thing.index < start + length) {
          renderGroup = candidate;
          break;
        }
      }
      if (!renderGroup) {
        mismatches.push({ id: thing.index, slot: slot.name, mode: 'missing', distance: null });
        continue;
      }

      const index = thing.index - renderGroup.startIndex;
      const mesh = renderGroup.meshes[index];
      const expected = world.presentedPlace(thing).position;
      let actual: { x: number; y: number; z: number };
      let mode: 'custom' | 'instanced';
      if (mesh?.visible) {
        actual = mesh.position;
        mode = 'custom';
      } else {
        const matrix = renderGroup.instancedMesh?.instanceMatrix?.array;
        if (!matrix) {
          mismatches.push({ id: thing.index, slot: slot.name, mode: 'missing', distance: null });
          continue;
        }
        actual = { x: matrix[index * 16 + 12], y: matrix[index * 16 + 13], z: matrix[index * 16 + 14] };
        mode = 'instanced';
      }
      const distance = Math.hypot(actual.x - expected.x, actual.y - expected.y, actual.z - expected.z);
      if (distance > 0.01) mismatches.push({ id: thing.index, slot: slot.name, mode, distance });
    }
    return { counts, mismatches };
  }, groups);
}

// Drake (Lane C, 2026-08-11) — click the GENUINELY VISIBLE claim control.
//
// On the mobile (Pixel-5) layout the legacy side-panel `#claim-{type}` button is
// present and `isEnabled()` returns true, but the collapsed `#sidebar` pill
// renders it with a ZERO-AREA box ({0,0,0,0}) so Playwright reports it not
// visible and `.click()` times out ("element is not visible").  The shipped
// CSS's own comment names the Ferro claim overlay "the primary surface during
// claim windows" (the side panel is a desktop-only fallback), and the overlay
// badge `.ferro-claim-badge-{type}` is a real 70×44 on-screen control the player
// taps.  Measured on :18089: clicking that overlay badge commits the Pung
// (concealed 13→11, meld exposed, seat owes discard, window closed) exactly like
// the desktop side-panel click.  So try the visible overlay first, side panel as
// a bounded fallback — a real pointer click either way, no force / synthetic
// dispatch / backdoor.
async function clickFirstActionable(
  page: Page,
  selectors: string[],
  timeoutMs: number,
): Promise<boolean> {
  for (const sel of selectors) {
    const loc = page.locator(sel).first();
    // Genuinely actionable = visible AND enabled right now (the overlay mounts
    // when the authoritative window opens; the zero-area side panel never is).
    const visible = await loc.isVisible().catch(() => false);
    const enabled = visible && (await loc.isEnabled().catch(() => false));
    if (visible && enabled) {
      try {
        await loc.click({ timeout: timeoutMs });
        return true;
      } catch {
        /* fall through to the next candidate control */
      }
    }
  }
  return false;
}

// ADVANCE — take a specific meld claim with a real actionable button click
// (no force, no synthetic dispatch).  Returns the type taken, or null.
async function claimMeldByClick(page: Page, type: 'Pung' | 'Chow' | 'Kong'): Promise<string | null> {
  const claim = await H.readClaimWindow(page);
  if (!claim.open || !claim.available.includes(type)) return null;
  const lower = type.toLowerCase();
  const clicked = await clickFirstActionable(
    page,
    [`.ferro-claim-badge-${lower}`, `#claim-${lower}`],
    4000,
  );
  if (!clicked) return null;
  await page.waitForTimeout(500);
  return type;
}

// ADVANCE — decline a claim we don't want with a real Pass click, so play
// continues toward the target meld.
async function passClaimByClick(page: Page): Promise<void> {
  if (await clickFirstActionable(page, ['.ferro-claim-pass', '#claim-pass'], 2000)) {
    await page.waitForTimeout(300);
  }
}

interface MeldCase {
  seed: number;
  meld: 'Pung' | 'Chow';
  // Also exercise the real touch-emulation pointer path on mobile-chrome.
  // Kept to the most projection-stable case (Pung/seed 12345) — the small
  // Pixel-5 viewport makes some hands' canvas projection flakier, so the
  // broader Chow case stays chromium-only.
  mobile: boolean;
}

// Deterministic seeds verified to deliver an early human claim of each type
// (Easy bots, 4 hands).  Exposed/concealed/added Kong are covered by the
// deterministic contract test (hand-accounting.contract.spec.ts) — live Kongs
// are too rare to force reliably.
const CASES: MeldCase[] = [
  { seed: 12345, meld: 'Pung', mobile: true },
  { seed: 7, meld: 'Chow', mobile: false },
];

test.describe('#147 post-meld discard — real-pointer claim then real-pointer discard', () => {
  for (const c of CASES) {
    test(`human ${c.meld} claim then a real-pointer discard advances play (seed ${c.seed})`, async ({
      page,
    }, testInfo) => {
      test.skip(
        testInfo.project.name !== 'chromium' &&
          !(c.mobile && testInfo.project.name === 'mobile-chrome'),
        'WebGL real-pointer play validated on chromium (+ mobile-chrome for the mobile-tagged case).',
      );
      test.setTimeout(300_000);

      const cfg = H.makeConfig({
        gameId: `h147-${c.meld.toLowerCase()}-${c.seed}-${Date.now()}`,
        seed: c.seed,
        botDifficulty: 'Easy',
        handCount: 4,
      });

      await H.defangOverlays(page);
      await page.goto(H.buildGameUrl(testInfo.project.use.baseURL as string, cfg), {
        waitUntil: 'domcontentloaded',
      });
      await H.dismissLobbyAndTour(page);
      expect(await H.ensureConnected(page), 'must reach a connected session').toBe(true);
      expect(await H.takeSeatByClick(page, 0), 'must take seat 0 by real click').toBe(0);
      expect(await H.waitForGameObject(page), 'the renderer must publish window.game').toBe(true);
      expect(await H.clickDeal(page), '#deal must fire by real click').toBe(true);

      // Give the client-auto-driven manual deal a moment to start; the loop
      // below is the robust driver — it actively takes any pickup affordance
      // (incl. a DealerExtra the auto-chain may drop under a slow CI renderer),
      // so we do NOT hard-assert waitForPlayableHand (that passive wait is
      // flaky under SwiftShader; the playability gate treats it the same way).
      await H.waitForPlayableHand(page, 20_000).catch(() => undefined);

      // Drive real turns until the target meld is offered, then TAKE it (real
      // pointer).  Take any pickup affordance (deal ceremony + hands 2..N),
      // discard on our own turns, and pass claims we don't want.
      let meldTaken: string | null = null;
      let preHand = -1;
      let preMelds = -1;
      const deadline = Date.now() + 210_000;
      while (Date.now() < deadline && meldTaken === null) {
        const claim = await H.readClaimWindow(page);
        if (claim.open && claim.available.includes(c.meld)) {
          preHand = await H.countMyHandTiles(page);
          preMelds = await readMeldCount(page);
          meldTaken = await claimMeldByClick(page, c.meld);
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

      expect(meldTaken, `seed ${c.seed} must reach a human ${c.meld} claim`).toBe(c.meld);

      // Authoritative post-claim state: a meld is exposed, concealed dropped,
      // and the seat now owes a discard.
      await page.waitForTimeout(600);
      const postHand = await H.countMyHandTiles(page);
      const postMelds = await readMeldCount(page);
      expect(postMelds, 'the claimed meld must be exposed in a meld slot').toBeGreaterThan(preMelds);
      expect(postHand, 'concealed tiles must drop after the meld').toBeLessThan(preHand);
      await expect.poll(async () => (await auditRenderedPlacements(page, ['hand', 'meld'])).mismatches,
        { message: 'claimed meld and presented hand must reach their actual render objects' }).toEqual([]);
      const postClaimRender = await auditRenderedPlacements(page, ['hand', 'meld']);
      expect(postClaimRender.counts.meld, 'all three claimed tiles must be rendered').toBeGreaterThanOrEqual(3);
      expect(
        await H.hasExtraHandTile(page),
        'the seat must now owe a discard after claiming the meld (#147)',
      ).toBe(true);

      // THE FIX under test: a REAL pointer discard must now be accepted, and
      // the authoritative discard pile must grow.  (We do NOT assert a
      // post-discard concealed count: with fast Easy bots the turn can loop
      // all the way back to us — auto-draw included — before we could read it,
      // which itself proves play is no longer stalled.)
      const outcome = await H.discardByPointer(page);
      expect(
        outcome.ok,
        `post-${c.meld} real-pointer discard must be accepted; was blocked pre-#147 (${outcome.reason})`,
      ).toBe(true);
      expect(
        outcome.discardAfter,
        'the authoritative discard pile must grow',
      ).toBeGreaterThan(outcome.discardBefore);
      await expect.poll(async () => (await auditRenderedPlacements(page, ['hand', 'discard', 'meld'])).mismatches,
        { message: 'discard, meld and presented hand must reach their actual render objects' }).toEqual([]);
      const postDiscardRender = await auditRenderedPlacements(page, ['hand', 'discard', 'meld']);
      expect(postDiscardRender.counts.discard, 'a discard must be visibly placed').toBeGreaterThan(0);

      // eslint-disable-next-line no-console
      console.log(
        `[#147 ${c.meld} seed ${c.seed}] preHand=${preHand} postClaimHand=${postHand} ` +
          `melds=${postMelds} discardOk=${outcome.ok} ` +
          `discardPile ${outcome.discardBefore}->${outcome.discardAfter}`,
      );
    });
  }
});

// =============================================================================
//  #137 REGRESSION — the perspective-toggle key (`p`) must never commit a claim.
// =============================================================================
//
//  Root cause of #137: the shipped bundle bound the SAME `p` key to two things —
//    • game.ts onKeyDown → toggle the perspective / flat camera (a view control)
//    • claim-window-overlay.ts onKeyDown → commit a Pung (an irreversible meld)
//  So a human pressing `p` to change the camera while a claim window (with a Pung
//  opportunity) happened to be open SILENTLY MELDED. Post-#135 that meld was
//  accepted by the server, leaving the human holding a meld with no drawn 14th
//  tile — unable to click-to-discard (world.hasExtraHandTile() counts only
//  hand-group tiles) — and the hand wedged forever (handEnds=0). That is exactly
//  the stall the P0 playability gate hit.
//
//  This gate presses the REAL `p` key while a REAL, server-opened meld-claim
//  window is open and asserts the bundle sends NO meld claim on the wire. It is
//  RED on the pre-fix bundle (`{action:"claim","type":"Pung"}` is emitted) and
//  GREEN once `p` is removed from the claim-overlay keyboard map.
//
//  Real keyboard + real WS only. We NEVER inject state or drive a claim: the
//  claim windows are opened by the real server as bots discard. Discards use a
//  canvas-only click guard so the pointer can never itself hit the bottom claim
//  badges (a separate overlap) — isolating the assertion to the `p` KEY.
// =============================================================================

import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import * as crypto from 'crypto';
import {
  makeConfig,
  buildGameUrl,
  defangOverlays,
  dismissLobbyAndTour,
  ensureConnected,
  takeSeatByClick,
  clickDeal,
  waitForPlayableHand,
  waitForGameObject,
  readClaimWindow,
  readMyHandTiles,
  readDiscardCount,
  projectTileToCanvas,
  hasExtraHandTile,
  readIsMyPickupTurn,
  takePickup,
  rollDiceIfDealer,
  readCameraType,
} from './_playability';

function resolveBase(baseURL: string | undefined): string {
  return baseURL ?? process.env.E2E_BASE_URL ?? 'http://localhost:8080/autotable/';
}

const MELD_TYPES = ['Pung', 'Chow', 'Kong'];

// The server (AutotableWsEndpoint.MaxGameIdLength) closes the WS with a
// PolicyViolation — SILENTLY, from the client's point of view — when a trimmed
// gameId exceeds this many chars, so `ensureConnected` just times out with no
// visible error. Mirror the cap here as a hard, test-local upper bound on the
// room key we generate, asserted before any URL is built.
const SERVER_GAME_ID_MAX = 64;

// Build a compact room key that is (a) unique across repeated AND concurrent runs
// and (b) PROVABLY bounded well under SERVER_GAME_ID_MAX no matter how long
// PLAYABILITY_RUN_ID is. The ONLY caller-controlled input (runId) is folded into a
// FIXED 8-hex-char SHA-256 fingerprint, so its length cannot influence the output
// length at all; every other segment is intrinsically bounded — a short literal
// prefix, the Playwright worker index, the OS pid, and a base36 millisecond stamp
// — and a 48-bit crypto-random suffix keeps keys unique even when two workers draw
// within the same millisecond on a heavily-reused backend DB (where a REUSED key
// would otherwise resolve to a persisted game instead of dealing a fresh seat-0
// dealer ceremony — the identical-config reconnect that made a fixed key flake).
// Worst-case width is ~48 chars; the caller still asserts <= SERVER_GAME_ID_MAX
// before constructing any URL, so the silent 64-char wire cap can never be tripped.
function buildCollisionResistantGameId(runId: string, workerIndex: number): string {
  const runFingerprint = crypto.createHash('sha256').update(runId).digest('hex').slice(0, 8);
  const worker = workerIndex.toString(36).slice(-2); // base36 worker index (lossless < 1296)
  const proc = process.pid.toString(36).slice(-5); // base36 pid (lossless for Linux pid_max)
  const stamp = Date.now().toString(36); // base36 ms clock (8 chars into the 2050s)
  const rand = crypto.randomBytes(6).toString('hex'); // 48 bits of entropy, 12 hex chars
  return `ckc-w${worker}-p${proc}-t${stamp}-h${runFingerprint}-${rand}`;
}

// Discard a hand tile via a REAL canvas click, but ONLY when the projected
// screen point actually resolves to the WebGL canvas — never an overlay badge.
// This keeps the pointer from ever committing a meld itself, so a captured meld
// claim can only have come from the `p` keypress under test. Returns true when a
// discard fired.
async function guardedCanvasDiscard(page: Page): Promise<boolean> {
  if ((await readClaimWindow(page)).open) return false; // never click over the badges
  const before = await readDiscardCount(page);
  const tiles = await readMyHandTiles(page);
  if (tiles.length === 0) return false;
  const mid = Math.floor(tiles.length / 2);
  const order = [mid];
  for (let off = 1; order.length < Math.min(5, tiles.length); off++) {
    if (mid + off < tiles.length) order.push(mid + off);
    if (mid - off >= 0 && order.length < 5) order.push(mid - off);
  }
  for (const idx of order) {
    const proj = await projectTileToCanvas(page, tiles[idx]);
    if (!proj.ok) continue;
    // Only click if the point is the canvas AND no claim window snuck open.
    const target = await page.evaluate(
      ({ x, y }) => {
        const el = document.elementFromPoint(x, y);
        return { tag: el?.tagName ?? null, id: (el as HTMLElement | null)?.id ?? null };
      },
      { x: proj.clientX, y: proj.clientY },
    );
    if (target.tag !== 'CANVAS' && target.id !== 'main') continue;
    if ((await readClaimWindow(page)).open) return false;
    await page.mouse.move(proj.clientX, proj.clientY, { steps: 6 });
    await page.waitForTimeout(90);
    await page.mouse.down();
    await page.waitForTimeout(80);
    await page.mouse.up();
    await page.waitForTimeout(1000);
    if ((await readDiscardCount(page)) > before) return true;
  }
  return false;
}

test.describe('@playability-gate #137 keyboard-collision regression', () => {
  test('pressing the perspective key (p) during a claim window never sends a meld claim', async ({
    page,
    baseURL,
  }, testInfo) => {
    // Desktop-canonical: the collision is a physical-keyboard shortcut. The
    // hc1/hc8/hc16 + 4-hand human gates already cover mobile playability.
    test.skip(
      testInfo.project.name === 'mobile-chrome',
      'p-key collision is a desktop physical-keyboard shortcut; mobile has no `p` view toggle',
    );
    test.setTimeout(4 * 60_000);

    // OBSERVE-ONLY, OUT-OF-PROCESS: capture every outbound frame that commits a
    // meld claim by reading the wire through Playwright's CDP `framesent` stream —
    // the same transparent observer every other WS spec uses (see
    // `installWallTakeRecorder` in _playability.ts). It is attached from the Node
    // side and NEVER touches the page's `WebSocket.prototype`, so — unlike the
    // prior in-page `send` monkeypatch (the ONLY such patch in the whole e2e
    // suite) — it cannot delay, drop, reorder, or otherwise perturb the
    // JOIN→JOINED handshake that `ensureConnected` waits on. That matters because
    // this spec cold-starts (WebGL init → connect) into a host the full 16-worker
    // run has already CPU-saturated, where the original 20 s connect budget was
    // the tightest gate to blow. The captured meld frame is the exact wire shape
    // of game-ui.ts `sendClaim`:
    //   {"type":"UPDATE",…["claim",<seat>,{"action":"claim","type":"Pung|Chow|Kong"}]}
    // A `{"action":"pass","type":null}` frame carries `"claim"` too but never
    // matches `"type":"(Pung|Chow|Kong)"`, so passes are correctly NOT counted.
    // `outboundFrames` proves the pipe is live so a zero meld count is non-vacuous.
    const meldClaimFrames: string[] = [];
    let outboundFrames = 0;
    page.on('websocket', (ws) => {
      if (!/\/autotable\/ws/.test(ws.url())) return; // ignore SignalR / other sockets
      ws.on('framesent', (data) => {
        let s = '';
        try {
          s = typeof data.payload === 'string' ? data.payload : Buffer.from(data.payload).toString('utf8');
        } catch {
          return;
        }
        outboundFrames++;
        if (s.includes('"claim"') && /"type":"(Pung|Chow|Kong)"/.test(s)) {
          meldClaimFrames.push(s);
        }
      });
    });

    // Genuinely unique per run AND per process/worker: the deterministic seed
    // (4100) drives the shuffle/hand, but the ROOM KEY must never be reused, or a
    // heavily-reused backend resolves this URL to a persisted game (identical-config
    // reconnect, see apply-gameid.contract) instead of dealing a fresh seat-0 dealer
    // ceremony. The prior revision embedded the RAW, unbounded PLAYABILITY_RUN_ID
    // after a 20-char prefix, so a realistic CI runner id pushed the gameId to 75–108
    // chars — past the server's 64-char cap — and the socket was silently refused,
    // timing out `ensureConnected`. That is the exact root Hudson isolated (RUN_ID=x
    // at ~53 chars still fit, so it passed). The bounded builder folds RUN_ID into a
    // fixed-width hash, so the key is provably <= SERVER_GAME_ID_MAX for ANY RUN_ID.
    const runId = process.env.PLAYABILITY_RUN_ID ?? 'local';
    const gameId = buildCollisionResistantGameId(runId, testInfo.workerIndex);
    // Hard, test-local length guard BEFORE any URL is constructed: the generated
    // room key can never trip the server's silent 64-char gameId cap.
    expect(
      gameId.length,
      `room key must stay within the server's ${SERVER_GAME_ID_MAX}-char gameId cap ` +
        `(was ${gameId.length}: "${gameId}")`,
    ).toBeLessThanOrEqual(SERVER_GAME_ID_MAX);
    const cfg = makeConfig({
      handCount: 4,
      seed: 4100,
      botDifficulty: 'Hard',
      gameId,
    });

    await defangOverlays(page);
    await page.goto(buildGameUrl(resolveBase(baseURL), cfg), { waitUntil: 'domcontentloaded' });
    expect(await waitForGameObject(page, 60_000), 'game object never booted').toBe(true);
    await dismissLobbyAndTour(page);
    expect(await ensureConnected(page, 90_000), 'WS never connected').toBe(true);
    const seat = await takeSeatByClick(page, cfg.seat);
    expect(seat, 'seat 0 not taken').toBe(0);
    expect(await clickDeal(page), 'deal press failed').toBe(true);

    // Server-authoritative manual deal: the client auto-drive (driveManualDealChain)
    // was removed, so the seated human DEALER (seat 0 — ChangshaStateMachine sets
    // DealerSeatIndex=0) must take EACH of its five ceremony batches with a real
    // #pickup-take-btn press before a playable 14th tile lands; clickDeal only rolls
    // the dice to START the ceremony. Take our OWN batches when the cursor targets us
    // (readIsMyPickupTurn) and poll-safe re-roll if the dealer HUD re-arms; the three
    // bots auto-take their own windows between ours, so we NEVER click during a bot
    // window. Mirrors the proven playRealGame deal loop; nothing advances the hand
    // absent a real press.
    let dealerPickups = 0;
    const dealBy = Date.now() + 60_000;
    while (Date.now() < dealBy && !(await hasExtraHandTile(page))) {
      if (await readIsMyPickupTurn(page)) {
        const pu = await takePickup(page);
        if (pu.ok) dealerPickups++;
      } else if (await rollDiceIfDealer(page)) {
        // dealer roll (re)fired — the ceremony can now present our pickups
      }
      await page.waitForTimeout(350);
    }
    const dealt = await waitForPlayableHand(page, 45_000);
    expect(dealt.playable, 'no playable hand dealt after the real human pickup ceremony').toBe(true);
    // Hard, non-vacuous ceremony completion (dealer14 → awaiting discard): the seat-0
    // DEALER holds its drawn 14th (extra) tile and discard is armed, reached THROUGH
    // real presses — so the collision assertions below run against a genuinely playable
    // hand, never a vacuous setup that skipped the human-driven pickup ceremony.
    expect(await hasExtraHandTile(page), 'dealer must hold the drawn 14th tile (awaiting discard) after the manual pickup ceremony').toBe(true);
    expect(dealerPickups, 'the human dealer must drive ≥1 real ceremony pickup batch with #pickup-take-btn (auto-drive removed)').toBeGreaterThan(0);

    let meldWindowsExercised = 0;
    let pPresses = 0;
    const deadline = Date.now() + 3 * 60_000;
    while (Date.now() < deadline && meldWindowsExercised < 3) {
      const claim = await readClaimWindow(page);
      if (claim.open) {
        if (claim.available.some((a) => MELD_TYPES.includes(a))) {
          const before = meldClaimFrames.length;
          // THE collision trigger: press the perspective-view key while a meld
          // claim window is open. The bundle must NOT read this as a meld.
          await page.keyboard.press('p');
          pPresses++;
          await page.waitForTimeout(500);
          expect(
            meldClaimFrames.length - before,
            `pressing "p" (perspective toggle) during a claim window offering ${JSON.stringify(
              claim.available,
            )} committed a MELD — the #137 keyboard collision has regressed`,
          ).toBe(0);
          meldWindowsExercised++;
        }
        // Decline via the real Esc pass shortcut so the hand keeps moving (never
        // meld). We deliberately do NOT click #claim-pass here: the additive
        // bottom-center claim overlay (z-index 1080, pointer-events:auto while a
        // window is open) can sit over the side-panel Pass button, so a Playwright
        // #claim-pass click issued right after the `p` view-toggle occasionally
        // resolves onto the overlay's Chow badge and commits a meld — a
        // test-interaction artifact that has nothing to do with the `p` key under
        // test. Esc (overlay.commitPass) is unambiguous and cannot hit a badge.
        await page.keyboard.press('Escape');
        await page.waitForTimeout(400);
        continue;
      }
      if (await readIsMyPickupTurn(page)) {
        await takePickup(page);
        await page.waitForTimeout(400);
        continue;
      }
      if (await hasExtraHandTile(page)) {
        await guardedCanvasDiscard(page);
        await page.waitForTimeout(300);
        continue;
      }
      await page.waitForTimeout(600);
    }

    // The whole game must never have leaked a single meld claim from a keypress.
    expect(meldClaimFrames.length, 'a meld claim was emitted from a keypress during the game').toBe(0);
    // Non-vacuous observation proof: the out-of-process framesent pipe genuinely
    // recorded THIS client's outbound wire traffic (JOIN/UPDATE/pickup/pass), so
    // the zero meld-claim result above is a real negative — not a dead or
    // misattached observer trivially reporting nothing.
    expect(
      outboundFrames,
      'the framesent meld observer captured ZERO outbound frames — it never attached to the live socket, so a zero meld-claim count would be vacuous',
    ).toBeGreaterThan(0);
    // Anti-vacuous: prove we actually pressed `p` against real meld windows.
    expect(
      meldWindowsExercised,
      `expected to exercise ≥1 real meld-claim window with the p-key (pPresses=${pPresses}); ` +
        'the collision path was never tested',
    ).toBeGreaterThan(0);
    // And `p` still does its real job: a camera kind is observable.
    expect(await readCameraType(page), 'perspective/flat camera not observable').not.toBeNull();
  });
});

// =============================================================================
//  F1 GATE-2 — VISUAL PERIMETER (CCW handedness + break column)  [Hicks/Ferro]
// =============================================================================
//
//  Frost RV-2 FINAL spec + ownership CORRECTION (Vasquez). The backend F1 anchor
//  oracle is UNCONDITIONAL and CORRECT: WallOrdinalToSlot's col is a fixed
//  LOGICAL assignment, and a render mirror (setup-slots.ts col→screen mapping)
//  leaves that logical col UNCHANGED. So the render CCW handedness is a FRONTEND
//  concern certifiable ONLY at the browser, and any mirror/scatter is a FRONTEND
//  fix Hicks owns — the backend wall gate STANDS. Outcomes for dealer0/dice2:
//    • col12 (R1) — EXPECTED, render CCW-correct ⇒ bind the visual regression.
//    • col1  (MIRROR) — a FRONTEND render bug ⇒ FIX setup-slots.ts (col→x /
//      seat×90° so logical col-max(B) sits physically adjacent to seat B+1, CCW).
//      Do NOT wait for a backend oracle change.
//    • col11 (R2) — UNEXPECTED / physically non-coherent ⇒ ping Frost + Vasquez
//      to re-examine the rules; do NOT self-resolve. (Only case touching the
//      backend, and it is not expected.)
//  The CCW-correct condition is DEFINED (Frost): logical col-max(B) physically
//  adjacent to seat B+1. Part A asserts exactly that browser-free (my lane).
//
//  Vasquez CANNOT certify the break column (R1 vs R2) OR the handedness from
//  §2.4 prose (internal step3/step4 tension). ⇒ Gate-2 is a USER-OBSERVATION
//  gate FIRST, then a codified browser regression. Do NOT self-derive the
//  expected from WallOrdinalToSlot / setup-slots — SURFACE the observation for a
//  human read, then HARD-CODE the confirmed physical positions.
//
//  WORKED EXAMPLES (egocentric §2.4: break wall B=(dealer+S−1)%4; count S from
//  player B's RIGHT corner toward seat B+1 CCW; take TOP; deplete CCW as ONE
//  contiguous arc). First-removed stack = one of three DISTINCT columns:
//  R1 (col=Stacks[B]−S), R2 (col=Stacks[B]−S−1), MIRROR (col=S−1, opposite end).
//
//  dealer0/dice2  → seat1 (South): R1=12 | R2=11 | MIRROR=1   ← BEST DISCRIMINATOR
//  dealer0/dice5  → seat0 (East):  R1=9  | R2=8  | MIRROR=4
//  dealer0/dice12 → seat3 (North): R1=1  | R2=0  | MIRROR=11
//  dealer1/dice6  → seat2 (West):  R1=7  | R2=6  | MIRROR=5
//  dealer2/dice11 → seat0 (East):  R1=3  | R2=2  | MIRROR=10
//  dealer3/dice12 → seat2 (West):  R1=1  | R2=0  | MIRROR=11
//  (AVOID dealer3/dice7 — R2 and MIRROR both col6, non-discriminating.)
//
//  NB (Vasquez): handedness is purely SEAT-INDEX-based. §2.1/§2.4/the 44-oracle/
//  BreakPointService/WallOrdinalToSlot are ALL seat-ABSOLUTE; the E/S/W/N winds
//  are DEALER-RELATIVE labels (dealer=East) that NEVER enter the geometry/oracle.
//  The (E)/(S)/(W)/(N) above is NARRATION only — the hard assert keys purely on
//  seat indices (B+1)%4 / (B+3)%4 + observed seat centroids; no wind lookup.
//
//  ┌────────────────────────── SEAMS / GATING ──────────────────────────┐
//  │ S1  FORCING (dealer,diceSum) needs the backend `seed` handshake      │
//  │     (Bishop C-2; today `seed` never reaches the server). Part B (live │
//  │     R1-vs-R2 observation) skips until then — it never false-greens.   │
//  │ S2  HANDEDNESS is §2.1-CONFIRMED by Vasquez. MIRROR has TWO sub-paths:│
//  │     (m1) GEOMETRY (makeSlots col→origin) ⇒ Part A HARD ASSERT         │
//  │          (browser-free, passes: not mirrored);                        │
//  │     (m2) CAMERA/VIEW reflection ⇒ ruled out by static review (camera  │
//  │          pipeline is rotations/translations/uniform-scale only) AND   │
//  │          automated by the live (m2) CHIRALITY test (seat S+1 renders  │
//  │          right of S−1; needs the running app, NOT S1). Human eye is   │
//  │          the ultimate arbiter for (m2) + R1-vs-R2.                     │
//  │ Gate on the F2-FIXED slotmap merge; RED@200cad4 is expected.          │
//  └─────────────────────────────────────────────────────────────────────┘

import { test, expect, type Page } from '@playwright/test';
import { makeSlots } from '../../src/setup-slots';
import { GameType } from '../../src/types';
import {
  defangOverlays, dismissLobbyAndTour, ensureConnected, takeSeatByClick, clickDeal,
  waitForPlayableHand, readMatch,
} from './_playability';

// Live-app reachability gate. The PART B / (m2) tests below need the running
// backend serving the built bundle. When it is absent (browser-free / no-app
// context) they must SKIP WITH REASON — never false-RED on a missing
// precondition (and a skip is never a false-GREEN). Without this, page.goto
// throws ERR_CONNECTION_REFUSED before the in-test skip guards are reached.
async function appReachable(baseURL: string | undefined): Promise<boolean> {
  if (baseURL === undefined || baseURL === '') return false;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 5000);
  try {
    const resp = await fetch(baseURL, { method: 'GET', signal: ctrl.signal });
    return resp.status < 500;
  } catch {
    return false;
  } finally {
    clearTimeout(timer);
  }
}

interface Example {
  dealer: number; dice: number; seat: number;
  r1: number; r2: number; mirror: number;
}
const WORKED_EXAMPLES: Example[] = [
  { dealer: 0, dice: 2,  seat: 1, r1: 12, r2: 11, mirror: 1 },  // best discriminator
  { dealer: 0, dice: 5,  seat: 0, r1: 9,  r2: 8,  mirror: 4 },
  { dealer: 0, dice: 12, seat: 3, r1: 1,  r2: 0,  mirror: 11 },
  { dealer: 1, dice: 6,  seat: 2, r1: 7,  r2: 6,  mirror: 5 },
  { dealer: 2, dice: 11, seat: 0, r1: 3,  r2: 2,  mirror: 10 },
  { dealer: 3, dice: 12, seat: 2, r1: 1,  r2: 0,  mirror: 11 },
];

// The EXPECTED reading is now R1 (col12), CCW-correct (Frost's correction:
// R1=expected/bind, MIRROR=my setup-slots fix, R2=unexpected⇒ping). `CONFIRMED`
// is set from the Part B LIVE human observation (S1) — until then null ⇒ the
// codified Part B regression stays `test.fixme` (never false-greens). When the
// live run shows R1 the codified assert binds to col12; MIRROR ⇒ I fix
// setup-slots; R2 ⇒ ping Frost/Vasquez.
const EXPECTED_READING = 'R1' as const;
const CONFIRMED: { reading: 'R1' | 'R2'; mirrored: boolean } | null = null;

// ── Geometry read from the REAL slot map (base-layer footprint per seat). ──
interface ColPos { col: number; x: number; y: number; }
function seatCols(seat: number): ColPos[] {
  const out: ColPos[] = [];
  for (const s of makeSlots(GameType.CHANGSHA)) {
    if (s.group !== 'wall') continue;
    const m = /^wall\.(\d+)\.(\d+)@(\d+)$/.exec(s.name);
    if (m === null || Number(m[2]) !== 0 || Number(m[3]) !== seat) continue;
    out.push({ col: Number(m[1]), x: Math.round(s.origin.x), y: Math.round(s.origin.y) });
  }
  return out.sort((a, b) => a.col - b.col);
}
const at = (cols: ColPos[], col: number): ColPos | undefined => cols.find((c) => c.col === col);
const dist = (a: ColPos, b: ColPos): number => Math.hypot(a.x - b.x, a.y - b.y);
const seatCentroid = (seat: number): ColPos => {
  const cols = seatCols(seat);
  return { col: -1, x: cols.reduce((s, c) => s + c.x, 0) / cols.length, y: cols.reduce((s, c) => s + c.y, 0) / cols.length };
};

test.describe('F1 Gate-2 — PART A: render handedness (browser-free mirror regression, Hicks)', () => {
  // S2 HARD ASSERT — CONFIRMED by Vasquez (rules owner), rules-CERTIFIED by §2.1
  // (objective, not a user preference). CCW play ⇒ player B's right = seat B+1;
  // §2.4 counts from wall B's RIGHT end; declared P-B lays col ASCENDING along
  // the CCW walk ⇒ col-max(B) abuts col-0(B+1) ⇒ col-max is physically toward
  // seat B+1. Codified NON-CIRCULARLY (Frost): the observed col-axis
  // (col-min→col-max) dotted with (centroid(seat B+1) − centroid(seat B−1)) must
  // be > 0. The seat centroids come from OBSERVED wall-tile positions (a layout
  // fact orthogonal to the within-wall col-direction a mirror corrupts) — never
  // the setup-slots col→x mapping under test. A col-mirror flips the axis ⇒
  // dot < 0 ⇒ hard RED ⇒ MY setup-slots fix (col→screen so col-max(B) abuts B+1).
  test('CCW handedness (S2, §2.1-CERTIFIED): observed col ascends from the seat B−1 corner toward seat B+1 (NOT mirrored)', () => {
    for (const B of [0, 1, 2, 3]) {
      const cols = seatCols(B);
      const next = seatCentroid((B + 1) % 4);   // seat B+1 (CCW = player B's right)
      const prev = seatCentroid((B + 3) % 4);    // seat B−1
      // Fallback guard: a neighbour wall with no tiles has no centroid — skip
      // with reason rather than assert on a missing reference. (Never triggers
      // in browser-free Part A: makeSlots always yields the full 14/14/13/13.)
      if (cols.length < 2 || Number.isNaN(next.x) || Number.isNaN(prev.x)) {
        test.skip(true, `seat${B}: neighbour wall centroid unavailable (depleted) — cannot reference.`);
      }
      const axisVec = { x: cols[cols.length - 1].x - cols[0].x, y: cols[cols.length - 1].y - cols[0].y };  // col-min → col-max, observed
      const toward = { x: next.x - prev.x, y: next.y - prev.y };                                            // seat B−1 → seat B+1
      const dot = axisVec.x * toward.x + axisVec.y * toward.y;
      expect(dot, `seat${B}: col must ASCEND from the seat${(B + 3) % 4} corner toward seat${(B + 1) % 4} (CCW). dot=${dot}; a col-mirror flips it negative.`).toBeGreaterThan(0);
    }
  });

  // Surfaces, for every worked example, WHERE the R1/R2/MIRROR columns
  // physically render + which seat-end they sit at (col0 end ↔ seat B−1; col-max
  // end ↔ seat B+1) — the human-read data for the R1-vs-R2 break column.
  test('R1/R2/MIRROR columns render at DISTINCT positions, mirror at the OPPOSITE corner', () => {
    for (const ex of WORKED_EXAMPLES) {
      const cols = seatCols(ex.seat);
      const col0 = cols[0];
      const colMax = cols[cols.length - 1];
      const r1 = at(cols, ex.r1)!; const r2 = at(cols, ex.r2)!; const mir = at(cols, ex.mirror)!;
      // Which physical END is each near? (col0 end ↔ seat B−1; colMax end ↔ seat B+1.)
      const nearEnd = (p: ColPos): string => (dist(p, col0) <= dist(p, colMax) ? `col0-end→seat${(ex.seat + 3) % 4}` : `colMax-end→seat${(ex.seat + 1) % 4}`);
      // eslint-disable-next-line no-console
      console.log(
        `[Gate-2/A] dealer${ex.dealer}/dice${ex.dice} seat${ex.seat}: ` +
        `R1(col${ex.r1})=(${r1.x},${r1.y})@${nearEnd(r1)}  ` +
        `R2(col${ex.r2})=(${r2.x},${r2.y})@${nearEnd(r2)}  ` +
        `MIRROR(col${ex.mirror})=(${mir.x},${mir.y})@${nearEnd(mir)}  ` +
        `[seat${ex.seat} col0=(${col0.x},${col0.y}) colMax=(${colMax.x},${colMax.y})]`,
      );
      // The three readings MUST be physically distinct (else the observation
      // can't discriminate) and the MIRROR must sit at the OPPOSITE corner from
      // R1 (that is the whole point — a mirror flips col-max↔col0 end).
      expect(dist(r1, mir), `dealer${ex.dealer}/dice${ex.dice}: R1 vs MIRROR must be far apart (opposite corners)`).toBeGreaterThan(dist(r1, r2));
      expect(nearEnd(r1), `dealer${ex.dealer}/dice${ex.dice}: R1 and MIRROR must be at OPPOSITE ends`).not.toBe(nearEnd(mir));
    }
  });
});

// ── PART B: live first-removed observation (needs S1 + running backend). ──
interface WallTilePhysical { seat: number; col: number; x: number; y: number; }
async function readWallPhysical(page: Page): Promise<WallTilePhysical[]> {
  return page.evaluate(() => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const w = (window as any).game?.world;
    const out: Array<{ seat: number; col: number; x: number; y: number }> = [];
    if (w?.things) {
      for (const t of w.things.values()) {
        if (t?.slot?.group !== 'wall') continue;
        const m = /^wall\.(\d+)\.(\d+)@(\d+)$/.exec(String(t.slot.name));
        if (m === null || Number(m[2]) !== 0) continue;
        const o = t.slot.origin ?? {};
        out.push({ col: Number(m[1]), seat: Number(m[3]), x: Number(o.x), y: Number(o.y) });
      }
    }
    return out;
  });
}

// Self-contained LOCAL Changsha mount for the DEAL-INDEPENDENT camera gates
// ((m2) / (m2-sweep)).  Those gates only read a live camera, the FIXED wall-slot
// geometry (world.slots — depletion-invariant, = Part A's makeSlots read) and a
// viewer seat the camera orients to; they NEVER need tiles dealt.  The former
// bring-up ran the full server connect + auto-deal + 3-bot LIVE game, which
// churned the SwiftShader software renderer for 60-90 s and leaked WebGL /
// serialisation resources until the page TARGET CLOSED at the 90 s timeout — the
// exact old red ("Target page, context or browser has been closed").
//
// This enters the product's real Changsha table LOCALLY instead: `?variant=
// changsha` makes the ObjectView ctor set the Changsha statics and the World ctor
// lay the canonical 108-tile Changsha wall (Conditions.initial() === CHANGSHA);
// omitting `?gameId=` keeps us fully local (ClientUi.start() only opens a WS when
// a gameId is present), so there is NO WS, NO bots and NO live-deal churn.  The
// camera pipeline (main-view.ts makeCamera / updateCamera — viewGroup.rotation.z
// = seat·90°) is byte-identical to the live path, so the chirality assertion is
// unchanged; only the irrelevant, resource-hungry live game is removed.
//
// Readiness is BOUNDED (renderer sentinel → camera + wall slots) and a WebGL
// context-loss tripwire fails fast with a diagnosable reason rather than hanging
// to the test timeout.
async function mountLocalChangsha(page: Page): Promise<void> {
  // Context-loss tripwire: flag a lost WebGL context so we can fail fast (bounded)
  // instead of silently rendering a dead canvas up to the timeout.
  await page.addInitScript(() => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (window as any).__wglContextLost = false;
    document.addEventListener(
      'webglcontextlost',
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      () => { (window as any).__wglContextLost = true; },
      true,
    );
  });
  await page.goto('./?variant=changsha&seat=0', { waitUntil: 'domcontentloaded' });
  await dismissLobbyAndTour(page);
  // The heavy three-renderer chunk mints this sentinel right after it publishes
  // window.game + game.start().
  await page
    .locator('[data-testid="three-renderer-ready"]')
    .waitFor({ state: 'attached', timeout: 60_000 });
  // BOUNDED readiness: camera + the FIXED wall-slot geometry present, and the
  // viewer seat set (the seat the camera is oriented to).  No deal, no WS.
  await page.waitForFunction(
    () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const g = (window as any).game;
      if (!g || !g.world || !g.mainView || !g.mainView.camera || !g.world.slots) return false;
      if (typeof g.world.seat !== 'number') g.world.seat = 0;
      let wallSlots = 0;
      for (const slot of g.world.slots.values()) {
        if (slot && slot.group === 'wall') { wallSlots++; if (wallSlots > 8) break; }
      }
      return wallSlots > 8;
    },
    undefined,
    { timeout: 30_000 },
  );
  // Fail fast on a genuine context loss (bounded, diagnosable) — never a 90 s hang.
  const lost = await page.evaluate(() => (window as unknown as { __wglContextLost?: boolean }).__wglContextLost === true);
  if (lost) {
    throw new Error('mountLocalChangsha: WebGL context lost during local Changsha mount (SwiftShader) — cannot evaluate camera chirality');
  }
}

test.describe('F1 Gate-2 — PART B: live CCW observation (dealer0/dice2), then codified', () => {
  test('surface the first-removed corner + stack for the human read (then hard-code CONFIRMED)', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'WebGL render geometry is validated on chromium.');
    test.skip(!(await appReachable(testInfo.project.use.baseURL as string | undefined)),
      'live backend (built bundle) not reachable at baseURL — PART B live observation pends the integrator bring-up + S1 seed. Skip (never false-RED).');
    test.setTimeout(120_000);
    const ex = WORKED_EXAMPLES[0]; // dealer0/dice2 collapses it in one observation

    await defangOverlays(page);
    await page.goto('?variant=changsha&dealMode=auto&botCount=3', { waitUntil: 'domcontentloaded' });
    await dismissLobbyAndTour(page);
    await ensureConnected(page);
    await takeSeatByClick(page, 0);
    await clickDeal(page);
    await waitForPlayableHand(page, 60_000).catch(() => undefined);
    await page.waitForTimeout(1200);

    // S1: can only observe the BOUND example when the deal ACTUALLY matches it.
    // The break point (⇒ first-removed column) is a function of BOTH the dealer
    // AND the dice-sum, so we must confirm BOTH before scoring against ex's
    // columns. Gating on dealer ALONE lets a random-dice deal (dealer0/dice≠2)
    // be mis-scored against the dice2 baseline and emit a SPURIOUS mirror/off-by-
    // one reading (observed 2026-08-07 on the canonical bring-up: an unseeded
    // dealer0 auto-deal logged a phantom MIRROR(col1)). Dice is authoritative on
    // client.dice (center.ts reads the same); ex.dice is the two-die SUM. Forcing
    // dealer0/dice2 needs the backend seed (Bishop C-2 / S1); until then BOTH
    // guards SKIP — never false-green.
    const match = await readMatch(page);
    const diceSum = await page.evaluate(() => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const g = (window as any).game;
      const d = g?.client?.dice?.get(0) ?? g?.client?.dice?.get('0');
      const arr = d?.dice;
      return Array.isArray(arr) && arr.length === 2 ? Number(arr[0]) + Number(arr[1]) : null;
    });
    test.skip(match.dealer !== ex.dealer || diceSum !== ex.dice,
      `Gate-2 bound to dealer${ex.dealer}/dice${ex.dice}; observed dealer=${match.dealer}/diceSum=${diceSum ?? 'n/a'}. ` +
      `Break-point column depends on BOTH dealer AND dice — needs seed→deterministic-deal (S1) to force the bound pair. Skip (never false-green).`);

    // The remaining wall's leading edge on seat B = the depletion boundary; the
    // first-removed stack is the col just consumed at the break. Surface which
    // physical corner (col0-end vs colMax-end) it sits at + the exact col.
    const tiles = (await readWallPhysical(page)).filter((t) => t.seat === ex.seat).sort((a, b) => a.col - b.col);
    // Auto-mode deals atomically and subsequent draws can fully consume a seat's
    // wall quadrant before we sample it; a residual-wall read then cannot see the
    // first-removed stack (observed empty/partial walls across repeats). Treat an
    // already-depleted seat wall as INCONCLUSIVE and skip (never false-RED) — the
    // break-moment read needs manual-mode step capture or the S1 seed, not a
    // post-depletion snapshot.
    test.skip(tiles.length === 0,
      `seat${ex.seat} wall already fully depleted at observation (auto-mode deal/draw race) — cannot read first-removed from a residual wall; needs manual-mode/break-moment capture or S1. Skip (never false-RED).`);
    const present = new Set(tiles.map((t) => t.col));
    const geomCols = seatCols(ex.seat);
    const firstRemoved = [ex.r1, ex.r2, ex.mirror].find((c) => !present.has(c));
    const g = firstRemoved === undefined ? undefined : at(geomCols, firstRemoved);
    const classify = firstRemoved === ex.r1 ? `${EXPECTED_READING}(col${ex.r1})=EXPECTED CCW-correct`
      : firstRemoved === ex.mirror ? `MIRROR(col${ex.mirror})=render bug ⇒ FIX setup-slots.ts (Hicks)`
      : firstRemoved === ex.r2 ? `R2(col${ex.r2})=UNEXPECTED off-by-one ⇒ BACKEND fix (BreakPointService + Vasquez oracle re-issue), NOT render; ping Frost/Vasquez`
      : 'inconclusive';
    // eslint-disable-next-line no-console
    console.log(`[Gate-2/B OBSERVE] dealer${ex.dealer}/dice${ex.dice} seat${ex.seat}: firstRemoved col=${firstRemoved} physical=${g ? `(${g.x},${g.y})` : 'n/a'} ⇒ ${classify}; remaining cols=[${[...present].sort((a, b) => a - b).join(',')}]`);
    testInfo.annotations.push({ type: 'gate2-observation', description: `firstRemoved col=${firstRemoved} (${classify})` });
    expect(tiles.length, 'seat wall rendered').toBeGreaterThan(0);
  });

  test('CODIFIED regression: live first-removed lands on the CCW side (handedness confirmed live)', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'chromium only.');
    // HANDEDNESS is now §2.1-CERTIFIED (Vasquez) and hard-asserted browser-free
    // in PART A. This live counterpart CONFIRMS the same on the running render
    // once S1 lands: the first-removed tile must sit on the seat B+1 (CCW) side,
    // NOT the seat B−1 (mirror) side. R1-vs-R2 (col12 vs col11) is NOT asserted
    // here — it stays observe/log above (a BACKEND question if it's R2). Blocked
    // on S1 (Bishop seed→deterministic-deal) to FORCE dealer0/dice2, so it stays
    // fixme until then (never false-greens); wire the CCW-side assert when S1 +
    // the F2-fixed running backend are available.
    test.fixme(CONFIRMED === null, 'PENDING S1 (Bishop seed → force dealer0/dice2) + live run; handedness itself is already certified + hard-asserted in Part A.');
    expect(CONFIRMED).not.toBeNull();
  });

  // (m2) CAMERA/VIEW mirror — Part A rules out only the (m1) GEOMETRY mirror
  // (makeSlots col→origin, world coords, pre-camera). A view/camera reflection
  // could flip handedness ON SCREEN without touching makeSlots. Static code
  // review of the whole camera pipeline (makeCamera symmetric frustum,
  // updateCamera viewGroup.rotation.z=seat·90°, camera position/rotation,
  // camera.scale.setScalar uniform-POSITIVE, updateCameraProjection symmetric
  // frustum) shows ONLY rotations/translations/uniform-scales ⇒ NO reflection ⇒
  // (m2) ruled out in source. This live test additionally AUTOMATES it: a
  // reflection is dot-product-INVARIANT (both operands reflect), so it uses a
  // CHIRALITY check — the SIGNED AREA (z of the cross product) of the triangle
  // (viewer seat, seat S+1, seat S−1). The EXPECTED sign is EXTERNALLY anchored
  // (Frost anti-circularity): it is the sign of that triangle in WORLD space
  // (slot.origin — the geometry Part A HARD-proves CCW-correct), NOT a hardcoded
  // direction and NOT read back from the projection under test. The camera is
  // orientation-PRESERVING (static ruling ⇒ det>0), so the SCREEN-NDC signed area
  // MUST share that world sign; a camera reflection/negative-scale flips the screen
  // sign ⇒ RED. Deal-independent (NOT S1); skips with reason if the app / camera
  // hook is absent, or if the centroids are degenerate/off-screen.
  test('(m2) camera does NOT mirror handedness: screen chirality matches the Part A world chirality', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'WebGL camera projection is validated on chromium.');
    test.skip(!(await appReachable(testInfo.project.use.baseURL as string | undefined)),
      'live backend (built bundle) not reachable at baseURL — (m2) camera check pends the integrator bring-up (deal-independent, no S1). Skip (never false-RED).');
    test.setTimeout(90_000);
    await defangOverlays(page);
    // Self-contained LOCAL Changsha mount (deal-independent) — replaces the old
    // connect + auto-deal + 3-bot live-game bring-up that churned SwiftShader for
    // 60-90 s and closed the page target at the 90 s timeout (the old red). The
    // camera pipeline + FIXED wall-slot geometry this gate reads are identical
    // either way; only the irrelevant live game is removed.
    await mountLocalChangsha(page);

    const r = await page.evaluate(() => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const g = (window as any).game;
      const world = g?.world; const mv = g?.mainView;
      const camera = mv?.camera; const viewGroup = mv?.viewGroup;
      if (!world || !camera || typeof camera.position?.clone !== 'function') {
        return { ok: false, reason: 'no world/camera hook (window.game.mainView.camera)' };
      }
      // Frost caveat (1): key S±1 off the LIVE viewer seat — the seat the camera is
      // oriented to (updateCamera(world.seat) ⇒ viewGroup.rotation.z=seat·90°), NEVER
      // a hardcoded 0. With no seat there is no camera orientation to test, so bail
      // and skip below (never a false-green on an unoriented camera; robust if the
      // harness ever seats somewhere other than 0).
      const localSeat: number | null = (typeof world.seat === 'number') ? world.seat : null;
      if (localSeat === null) {
        return { ok: false, reason: 'no viewer seat — (m2) chirality needs the seat the camera is oriented to' };
      }
      // Frost caveat (2) — SCENE GRAPH: wall tiles live in `mainGroup`, which is added
      // to the scene at IDENTITY (game.ts `new Group`, no transform). The seat·90°
      // rotation lives on `viewGroup`, which holds ONLY the camera + lights — the
      // camera ORBITS the seat; the board stays world-fixed. So a wall tile's WORLD
      // position IS its slot.origin: project it DIRECTLY. Do NOT pre-apply
      // viewGroup.matrixWorld (the board is not a viewGroup child) — that would double-
      // transform it (spurious +WIDTH/2 offset + a rotation the tiles don't have) and
      // throw the NDC off-screen. camera.project() already folds in the camera's own
      // world matrix (it IS a viewGroup child). Reuse camera.position (a THREE.Vector3)
      // to avoid a THREE import.
      // Frost review pt (3) — CENTROID SOURCE = FIXED SLOT GEOMETRY, depletion-
      // INVARIANT. Iterate `world.slots` (the fixed board geometry, = setup.slots =
      // makeSlots) NOT `world.things` (the remaining tiles): drawing/moving a tile
      // removes the THING, never the SLOT, so every seat's wall anchor stays the
      // FULL fixed wall for the whole game. A lopsided auto-deal depletion therefore
      // cannot shift a centroid across the sign boundary — this is exactly the
      // "anchor is slot-geometry (fixed), not remaining-tiles" case Frost flagged as
      // ideal, and it makes the (m2) sourcing identical in spirit to Part A's
      // makeSlots read (:114). n===0 (no wall slot defined for a seat) ⇒ null ⇒
      // honest skip below, never a degenerate near-zero centroid.
      // Return BOTH the WORLD centroid (slot.origin mean — pre-camera; the same
      // geometry Part A HARD-proves CCW-correct) and its projected SCREEN NDC.
      const centroid = (seat: number): { world: { x: number; y: number }; ndc: { x: number; y: number } } | null => {
        let sx = 0, sy = 0, sz = 0, n = 0;
        for (const slot of world.slots.values()) {
          if (slot?.group !== 'wall') continue;
          const mm = /@(\d+)$/.exec(String(slot.name)); if (mm === null || Number(mm[1]) !== seat) continue;
          const o = slot.origin ?? {}; sx += o.x; sy += o.y; sz += o.z ?? 0; n++;
        }
        if (n === 0) return null;
        const wx = sx / n, wy = sy / n, wz = sz / n;
        const v = camera.position.clone(); v.set(wx, wy, wz);
        v.project(camera);                                                  // world → NDC (−1..1)
        return { world: { x: wx, y: wy }, ndc: { x: v.x, y: v.y } };
      };
      const self = centroid(localSeat);
      const next = centroid((localSeat + 1) % 4);
      const prev = centroid((localSeat + 3) % 4);
      if (self === null || next === null || prev === null) return { ok: false, reason: 'a wall centroid (self/next/prev) is unavailable' };
      // Signed area (z of the cross product) of triangle (self → next → prev) in
      // WORLD and in SCREEN space. worldCross = the Part A-certified CCW ground
      // truth; screenCross = what the camera renders. An orientation-preserving
      // camera keeps the sign; a reflection flips it ⇒ the sign-match IS the mirror
      // gate, with the expected sign anchored to world geometry, not the projection.
      const cross = (a: { x: number; y: number }, b: { x: number; y: number }, c: { x: number; y: number }): number =>
        (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
      const worldCross = cross(self.world, next.world, prev.world);
      const screenCross = cross(self.ndc, next.ndc, prev.ndc);
      const angleDeg = (viewGroup?.rotation?.z !== undefined) ? (viewGroup.rotation.z * 180 / Math.PI) : null;
      const coords = [self.ndc.x, self.ndc.y, next.ndc.x, next.ndc.y, prev.ndc.x, prev.ndc.y];
      const finite = coords.every((c) => Number.isFinite(c)) && Number.isFinite(worldCross) && Number.isFinite(screenCross);
      const onScreen = coords.every((c) => Math.abs(c) <= 1.5);
      return {
        ok: true, localSeat,
        nextSeat: (localSeat + 1) % 4, prevSeat: (localSeat + 3) % 4,
        nextX: next.ndc.x, nextY: next.ndc.y, prevX: prev.ndc.x, prevY: prev.ndc.y,
        worldCross, screenCross,
        angleDeg, finite, onScreen,
      };
    });

    test.skip(!r.ok, r.reason ?? 'camera projection data unavailable — running-app gate.');
    // eslint-disable-next-line no-console
    console.log(`[Gate-2/m2] viewer seat${r.localSeat} (viewGroup ${r.angleDeg?.toFixed(0)}°): ` +
      `seat${r.nextSeat}(S+1/right) ndc=(${r.nextX?.toFixed(3)},${r.nextY?.toFixed(3)}) vs ` +
      `seat${r.prevSeat}(S-1/left) ndc=(${r.prevX?.toFixed(3)},${r.prevY?.toFixed(3)}) ` +
      `| worldCross=${r.worldCross?.toFixed(0)} screenCross=${r.screenCross?.toFixed(4)} (expect SAME sign) ` +
      `nextX>prevX=${(r.nextX as number) > (r.prevX as number)} finite=${r.finite} onScreen=${r.onScreen}`);
    // Tripwire: non-finite / off-screen ⇒ scene-graph/transform assumption or camera
    // hook not ready; near-zero (degenerate/collinear) cross ⇒ chirality undefined.
    // Either ⇒ SKIP (honest "can't evaluate"), NEVER a false-RED mirror verdict.
    const DEGEN = 1e-6;
    test.skip(!r.finite || !r.onScreen,
      `(m2) projected NDC not finite/on-screen — transform or camera hook not ready; skip rather than false-RED: ${JSON.stringify(r)}`);
    test.skip(Math.abs(r.worldCross as number) < DEGEN || Math.abs(r.screenCross as number) < DEGEN,
      `(m2) degenerate (near-collinear) wall centroids — chirality undefined; skip rather than false-RED: ${JSON.stringify(r)}`);
    // EXTERNALLY-ANCHORED chirality gate (Frost anti-circularity): the SCREEN signed
    // area must share the sign of the WORLD signed area (Part A-certified CCW). An
    // orientation-preserving camera preserves it; a reflection flips it ⇒ RED. The
    // expected sign comes from world geometry, never the projection under test.
    expect(Math.sign(r.screenCross as number),
      `(m2) camera mirror: screen chirality (screenCross=${(r.screenCross as number).toFixed(4)}) must match the Part A-certified WORLD chirality (worldCross=${(r.worldCross as number).toFixed(0)}); an orientation-preserving camera preserves the sign, a reflection flips it`)
      .toBe(Math.sign(r.worldCross as number));
  });

  // (m2-SWEEP) Frost caveat (ii): the seat-0 (m2) test above exercises viewGroup
  // rotation = 0° (identity) — precisely the rotation that MASKED the original
  // double-transform mirror for non-seat0 viewers. The population of concern is
  // viewGroup 90/180/270°, which the det=+1 proper-rotation argument covers only
  // ANALYTICALLY. This sweep makes it EMPIRICAL: for every viewer seat S it drives
  // the REAL production camera path (main-view.ts updateCamera → viewGroup.rotation
  // .z = S·90°; game.ts:151 calls exactly this each frame) and asserts the same
  // world-anchored chirality sign-match. The seat·90° rotation lives SOLELY on
  // viewGroup; the orthographic camera intrinsics are seat-INDEPENDENT
  // (updateOrthographicCamera keys on `seat === null` only), so re-running
  // updateCamera(S,…) + recomputing camera.matrixWorldInverse (exactly what
  // WebGLRenderer.render does each frame) reproduces the exact camera a seat-S
  // viewer renders. A RED at ANY seat ⇒ a rotation-dependent mirror or a
  // seat→angle sign bug (⇒ col11/R2, ping Frost+Vasquez). Deal-independent.
  test('(m2-sweep) camera does NOT mirror at ANY viewer seat: screen chirality matches the Part A world chirality at viewGroup 0/90/180/270°', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'WebGL camera projection is validated on chromium.');
    test.skip(!(await appReachable(testInfo.project.use.baseURL as string | undefined)),
      'live backend (built bundle) not reachable at baseURL — (m2) seat sweep pends the integrator bring-up (deal-independent, no S1). Skip (never false-RED).');
    test.setTimeout(90_000);
    await defangOverlays(page);
    // Self-contained LOCAL Changsha mount (deal-independent) — replaces the old
    // connect + auto-deal + 3-bot live-game bring-up that churned SwiftShader for
    // 60-90 s and closed the page target at the 90 s timeout (the old red). The
    // real production camera path (updateCamera → viewGroup.rotation.z = S·90°)
    // this sweep drives + the FIXED wall-slot geometry are identical either way;
    // only the irrelevant live game is removed.
    await mountLocalChangsha(page);

    const sweep = await page.evaluate(() => {
      interface SweepRow {
        seat: number; available: boolean; nextSeat: number; prevSeat: number;
        nextX: number; prevX: number; worldCross: number; screenCross: number;
        finite: boolean; onScreen: boolean;
      }
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const g = (window as any).game;
      const world = g?.world; const mv = g?.mainView;
      const camera = mv?.camera;
      if (!world || !camera || typeof camera.position?.clone !== 'function' || typeof mv?.updateCamera !== 'function') {
        return { ok: false, reason: 'no world/camera hook (window.game.mainView.updateCamera/camera)', liveSeat: 0, rows: [] as SweepRow[] };
      }
      const liveSeat: number = (typeof world.seat === 'number') ? world.seat : 0;
      // Reproduce game.ts:151's exact camera args (seat-independent intrinsics).
      const ld: number = (typeof g.lookDown?.pos === 'number') ? g.lookDown.pos : 0;
      const zm: number = (typeof g.zoom?.pos === 'number') ? g.zoom.pos : 0;
      const m2 = g.mouseUi?.mouse2 ?? null;
      // Centroid = FIXED slot geometry (depletion-invariant, = Part A's makeSlots
      // read), projected under the CURRENT camera.matrixWorldInverse (recomputed
      // per rotation below). Identical sourcing to the seat-0 (m2) test.
      const centroid = (seat: number): { world: { x: number; y: number }; ndc: { x: number; y: number } } | null => {
        let sx = 0, sy = 0, sz = 0, n = 0;
        for (const slot of world.slots.values()) {
          if (slot?.group !== 'wall') continue;
          const mm = /@(\d+)$/.exec(String(slot.name)); if (mm === null || Number(mm[1]) !== seat) continue;
          const o = slot.origin ?? {}; sx += o.x; sy += o.y; sz += o.z ?? 0; n++;
        }
        if (n === 0) return null;
        const wx = sx / n, wy = sy / n, wz = sz / n;
        const v = camera.position.clone(); v.set(wx, wy, wz);
        v.project(camera);
        return { world: { x: wx, y: wy }, ndc: { x: v.x, y: v.y } };
      };
      const cross = (a: { x: number; y: number }, b: { x: number; y: number }, c: { x: number; y: number }): number =>
        (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
      const rows: SweepRow[] = [];
      for (let S = 0; S < 4; S++) {
        // Drive the REAL production camera orientation for viewer seat S, then
        // recompute matrixWorldInverse exactly as WebGLRenderer.render does.
        mv.updateCamera(S, ld, zm, m2);
        camera.updateMatrixWorld(true);
        camera.matrixWorldInverse.copy(camera.matrixWorld).invert();
        const self = centroid(S); const next = centroid((S + 1) % 4); const prev = centroid((S + 3) % 4);
        if (self === null || next === null || prev === null) {
          rows.push({ seat: S, available: false, nextSeat: (S + 1) % 4, prevSeat: (S + 3) % 4, nextX: NaN, prevX: NaN, worldCross: NaN, screenCross: NaN, finite: false, onScreen: false });
          continue;
        }
        const worldCross = cross(self.world, next.world, prev.world);
        const screenCross = cross(self.ndc, next.ndc, prev.ndc);
        const coords = [self.ndc.x, self.ndc.y, next.ndc.x, next.ndc.y, prev.ndc.x, prev.ndc.y];
        const finite = coords.every((c) => Number.isFinite(c)) && Number.isFinite(worldCross) && Number.isFinite(screenCross);
        const onScreen = coords.every((c) => Math.abs(c) <= 1.5);
        rows.push({ seat: S, available: true, nextSeat: (S + 1) % 4, prevSeat: (S + 3) % 4, nextX: next.ndc.x, prevX: prev.ndc.x, worldCross, screenCross, finite, onScreen });
      }
      // Restore the live viewer's orientation (belt-and-braces; the raf loop resets it anyway).
      mv.updateCamera(liveSeat, ld, zm, m2);
      return { ok: true, reason: '', liveSeat, rows };
    });

    test.skip(!sweep.ok, sweep.reason || 'camera projection data unavailable — running-app gate.');
    for (const r of sweep.rows) {
      // eslint-disable-next-line no-console
      console.log(`[Gate-2/m2-sweep] viewGroup ${r.seat * 90}° (viewer seat${r.seat}): ` + (r.available
        ? `seat${r.nextSeat}(S+1) ndc.x=${r.nextX.toFixed(3)} vs seat${r.prevSeat}(S-1) ndc.x=${r.prevX.toFixed(3)} ` +
          `| worldCross=${r.worldCross.toFixed(0)} screenCross=${r.screenCross.toFixed(4)} (expect SAME sign) ` +
          `nextX>prevX=${r.nextX > r.prevX} finite=${r.finite} onScreen=${r.onScreen}`
        : 'wall centroid unavailable ⇒ this seat not evaluable'));
    }
    // Honest can't-evaluate (never a false-RED): require all four seats finite,
    // on-screen and non-degenerate before asserting. A skip is not a pass.
    const DEGEN = 1e-6;
    const evaluable = sweep.rows.filter((r) => r.available && r.finite && r.onScreen
      && Math.abs(r.worldCross) >= DEGEN && Math.abs(r.screenCross) >= DEGEN);
    test.skip(evaluable.length < 4,
      `(m2-sweep) not all four viewer seats evaluable (finite/on-screen/non-degenerate) — camera/render not ready; skip rather than false-RED: ${JSON.stringify(sweep.rows)}`);
    // EXTERNALLY-ANCHORED chirality gate at EVERY rotation (Frost anti-circularity):
    // each seat's SCREEN signed area must share the sign of that seat's WORLD signed
    // area (Part A-certified CCW). A proper rotation preserves it; a reflection at
    // ANY rotation flips it ⇒ RED. The expected sign is world-derived per seat,
    // never read from the projection under test.
    for (const r of evaluable) {
      expect(Math.sign(r.screenCross),
        `(m2-sweep) camera mirror at viewer seat${r.seat} (viewGroup ${r.seat * 90}°): screen chirality (screenCross=${r.screenCross.toFixed(4)}) must match the Part A-certified WORLD chirality (worldCross=${r.worldCross.toFixed(0)}); a proper rotation preserves the sign, a reflection flips it`)
        .toBe(Math.sign(r.worldCross));
    }
  });
});

// G19 (OWNED by hudson-1) — raw-WS opaque-handle privacy + inbound authority.
// Corrected identity model (Ripley 2026-08-07): opaque handles are non-numeric
// STRINGS keyed by durable playerId + server secret (NOT ints, NOT per-connection).
// P-1 opaque + non-brute-forceable; P-2 own/public keep numeric real ids;
// P-3 own actionable; P-4 same durable-playerId reconnect ⇒ byte-identical;
// P-5 two distinct players ⇒ uncorrelated. Plus inbound authority: a client
// things push must not be observed by peers. REAL observation only.
import { test, expect, type Page } from '@playwright/test';
import { buildGameUrl, makeConfig, dismissLobbyAndTour, ensureConnected, takeSeatByClick, clickDeal, waitForPlayableHand } from './_playability';
import { realDragWallTile, recordEvidence } from './_uat_red';
import {
  newSink, attachRawWsCapture, readPlayerId, faceLeaks,
  thingIdentityMap, backHandleMap, realIndexMap, isBack, isOpaqueHandle,
  handleHealth, crossViewerLinkable, duplicateSlots, type ThingIdentity,
} from './helpers/changsha-raw-ws';

function stripSeed(url: string): string { const u = new URL(url); u.searchParams.delete('seed'); return u.toString(); }

test.describe('G19 raw-WS opaque-handle privacy (P1–P5) + inbound authority', () => {
  test('P1 opaque + P2 numeric + P3 actionable + P4 durable-stable + P5 unlinkable', async ({ browser }, testInfo) => {
    testInfo.setTimeout(180_000);
    const gameId = `g19-priv-${Date.now()}`;
    const base = testInfo.project.use.baseURL as string;

    // POST-SNAPSHOT reconciliation guard. The privacy projection parks every tile a
    // viewer is NOT entitled to as an opaque face-down BACK (the 3 foreign hands +
    // the wall). Asserting before that reconciliation would read the pre-snapshot
    // LOCAL setup (real tiles everywhere) and pass VACUOUSLY, so every viewer must
    // first observe a substantial pool of opaque backs. `backs>=40` cannot hold on
    // local setup state — it requires the authoritative wall + 3 foreign hands.
    const reconcile = (p: Page): Promise<boolean> =>
      p.waitForFunction(() => {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const w = (window as any).game?.world; if (!w?.things) return false;
        let backs = 0; for (const t of w.things.values()) if (t.hiddenHandle !== null) backs++;
        return backs >= 40;
      }, undefined, { timeout: 60_000 }).then(() => true).catch(() => false);

    // Two DISTINCT durable identities via independent contexts; server-random seed.
    const ctxA = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const a = await ctxA.newPage(); const sinkA = newSink(); attachRawWsCapture(a, sinkA);
    await a.goto(stripSeed(buildGameUrl(base, makeConfig({ gameId, seat: 0, dealMode: 'auto', botCount: 3, botDifficulty: 'Medium', handCount: 4 }))), { waitUntil: 'domcontentloaded' });
    await a.waitForTimeout(1000); await dismissLobbyAndTour(a); await ensureConnected(a);
    await takeSeatByClick(a, 0); await clickDeal(a); await waitForPlayableHand(a, 60_000).catch(() => {});
    const reconciledA = await reconcile(a);

    const ctxB = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const b = await ctxB.newPage(); const sinkB = newSink(); attachRawWsCapture(b, sinkB);
    await b.goto(stripSeed(buildGameUrl(base, makeConfig({ gameId, seat: 1, dealMode: 'auto', botCount: 3, botDifficulty: 'Medium', handCount: 4 }))), { waitUntil: 'domcontentloaded' });
    await b.waitForTimeout(1000); await dismissLobbyAndTour(b); await ensureConnected(b); await takeSeatByClick(b, 1);
    const reconciledB = await reconcile(b);

    const pidA = await readPlayerId(a); const pidB = await readPlayerId(b);
    const leaksA = faceLeaks(sinkA.raw, 0);

    // P-3: the viewer's OWN hand renders face-up / actionable.
    const ownFaceUp = await a.evaluate(() => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const w = (window as any).game?.world; let up = 0, tot = 0;
      if (w?.things) for (const t of w.things.values()) { const nm = String(t?.slot?.name ?? ''); if (/^hand\.\d+@0$/.test(nm)) { tot++; if (t.rotationIndex === 1) up++; } }
      return { up, tot };
    });

    // AUTHORITATIVE identity of every wall + hand tile from A's & B's reconciled snapshot.
    const aAll = await thingIdentityMap(a, '^(wall\\.|hand\\.\\d+@[0-9])');
    const bAll = await thingIdentityMap(b, '^(wall\\.|hand\\.\\d+@[0-9])');
    const A: ThingIdentity[] = Object.values(aAll);
    const aBacks = A.filter(isBack);                                                  // opaque foreign hands + wall
    const aForeignSlots = A.filter((t) => /^wall\./.test(t.slot) || /^hand\.\d+@[123]$/.test(t.slot));
    const aOwn = A.filter((t) => /^hand\.\d+@0$/.test(t.slot));

    // P-1: every hidden back exposes an OPAQUE "h_…" handle (never the numeric mesh id).
    const p1Fails = aBacks.filter((t) => !isOpaqueHandle(String(t.hiddenHandle)));
    // Renderer back invariants: anonymous pool mesh id >=108 + NO face (typeIndex sentinel 0).
    const meshBad = aBacks.filter((t) => !(Number.isInteger(t.index) && t.index >= 108));
    const faceBad = aBacks.filter((t) => t.typeIndex !== 0);
    // Entitlement / no OVER-REVEAL: a foreign-concealed/wall slot must never render an
    // entitled real (hiddenHandle===null) — that would leak an opponent's / wall tile.
    const overReveal = aForeignSlots.filter((t) => !isBack(t));
    // …and the OWN hand must be entitled reals (never masked by an opaque back).
    const ownAsBack = aOwn.filter(isBack);
    // P-2: own/public tiles keep their numeric REAL id (0-107) and stay actionable.
    const p2Bad = aOwn.filter((t) => !(t.hiddenHandle === null && Number.isInteger(t.index) && t.index >= 0 && t.index <= 107));
    // No double-occupied slot (ghost / duplicate over-reveal).
    const dups = await duplicateSlots(a, '^(wall\\.|hand\\.\\d+@[0-9])');
    // Opaque back handles must be collision-free + JS-safe.
    const health = handleHealth(aBacks.map((t) => String(t.hiddenHandle)));
    // P-5: the SAME physical hidden tile must map to DIFFERENT handles per viewer.
    const p5 = crossViewerLinkable(backHandleMap(aAll), backHandleMap(bAll));

    // P-4: same durable-PlayerId reconnect ⇒ byte-identical ENTITLED ids, and the
    // privacy projection must survive (backs stay opaque; no reveal on reconnect).
    const aOwnBefore = realIndexMap(await thingIdentityMap(a, '^hand\\.\\d+@0$'));
    await a.locator('#disconnect').click({ timeout: 5000 }).catch(() => {});
    await a.waitForTimeout(1000);
    await a.locator('#connect').click({ timeout: 5000 }).catch(() => {});
    await a.waitForTimeout(2500);
    const reconciledAafter = await reconcile(a);
    const pidAafter = await readPlayerId(a);
    const aOwnAfter = realIndexMap(await thingIdentityMap(a, '^hand\\.\\d+@0$'));
    let p4Changed = 0; for (const k of Object.keys(aOwnBefore)) if (k in aOwnAfter && aOwnBefore[k] !== aOwnAfter[k]) p4Changed++;
    const aBacksAfter = Object.values(await thingIdentityMap(a, '^(wall\\.|hand\\.\\d+@[123])')).filter(isBack);
    const p4OpaqueFails = aBacksAfter.filter((t) => !isOpaqueHandle(String(t.hiddenHandle)));

    recordEvidence('g19-privacy-opaque-handles.json', {
      playerIds: { pidA, pidB, pidAafter, distinctAB: pidA !== pidB, aDurableStable: pidA === pidAafter },
      reconciled: { A: reconciledA, B: reconciledB, Aafter: reconciledAafter },
      counts: { aBacks: aBacks.length, aOwn: aOwn.length, foreignSlots: aForeignSlots.length, p5Compared: p5.compared },
      P1_opaqueFails: p1Fails.length, backMeshBad: meshBad.length, backFaceBad: faceBad.length,
      overReveal: overReveal.length, ownAsBack: ownAsBack.length, P2_bad: p2Bad.length,
      dups, health, P4_changed: p4Changed, P4_opaqueFails: p4OpaqueFails.length, P5: p5,
      sampleBacks: aBacks.slice(0, 6).map((t) => ({ slot: t.slot, hiddenHandle: t.hiddenHandle, index: t.index, typeIndex: t.typeIndex })),
      wireFaceLeaks: leaksA.slice(0, 6),
    });
    await ctxA.close(); await ctxB.close();

    // ── Gate ───────────────────────────────────────────────────────────────────
    // Must assert the reconciled authoritative snapshot, NOT pre-snapshot local setup.
    expect(reconciledA, 'authoritative snapshot must reconcile (opaque backs present) — the gate must not read pre-snapshot local setup').toBe(true);
    expect(reconciledB, 'peer viewer must also reach the reconciled snapshot').toBe(true);
    expect(aBacks.length, `substantial opaque backs (wall + foreign hands) required post-deal; got ${aBacks.length}`).toBeGreaterThanOrEqual(40);
    // Wire carries no hidden-tile face.
    expect(leaksA, `wire must carry no hidden-tile face; leaks=${JSON.stringify(leaksA.slice(0, 5))}`).toEqual([]);
    // Distinct durable identities.
    expect(Boolean(pidA && pidB && pidA !== pidB), `two distinct durable PlayerIds required (A=${pidA}, B=${pidB})`).toBe(true);
    // P-1: opaque handles (the correction — assert hiddenHandle, never the mesh index).
    expect(p1Fails.length, `P-1: every hidden back must expose an opaque h_ handle (NOT the numeric mesh id); ${p1Fails.length} bad e.g. ${JSON.stringify(p1Fails.slice(0, 4).map((t) => [t.slot, t.hiddenHandle, t.index]))}`).toBe(0);
    // Renderer back invariants.
    expect(meshBad.length, `hidden back must render as the anonymous pool mesh (index>=108); ${meshBad.length} bad e.g. ${JSON.stringify(meshBad.slice(0, 4).map((t) => [t.slot, t.index]))}`).toBe(0);
    expect(faceBad.length, `hidden back must carry NO face (typeIndex sentinel 0); ${faceBad.length} bad e.g. ${JSON.stringify(faceBad.slice(0, 4).map((t) => [t.slot, t.typeIndex]))}`).toBe(0);
    // Entitlement / no over-reveal / no ghost.
    expect(overReveal.length, `over-reveal: a foreign-concealed/wall slot rendered an ENTITLED real tile; ${overReveal.length} e.g. ${JSON.stringify(overReveal.slice(0, 4).map((t) => [t.slot, t.index, t.typeIndex]))}`).toBe(0);
    expect(ownAsBack.length, `the viewer's OWN hand must be entitled reals (no opaque back masking it); ${ownAsBack.length}`).toBe(0);
    expect(dups.length, `no slot may be double-occupied (ghost / duplicate over-reveal); dups=${JSON.stringify(dups.slice(0, 6))}`).toBe(0);
    // P-2: own/public actionable numeric real ids (0-107).
    expect(aOwn.length, `own hand must be present as entitled reals; got ${aOwn.length}`).toBeGreaterThanOrEqual(13);
    expect(p2Bad.length, `P-2: own tiles must keep numeric REAL ids 0-107 (actionable) with hiddenHandle null; ${p2Bad.length} bad e.g. ${JSON.stringify(p2Bad.slice(0, 4).map((t) => [t.slot, t.index, t.hiddenHandle]))}`).toBe(0);
    // P-3: own actionable/face-up.
    expect(ownFaceUp.up, `P-3: own hand actionable/face-up; up=${ownFaceUp.up}/${ownFaceUp.tot}`).toBeGreaterThanOrEqual(13);
    // Handle health.
    expect(health.collisions, `opaque back handles must be collision-free; collisions=${health.collisions}`).toBe(0);
    expect(health.precisionRisk, `handles must be JS-safe; risky=${health.precisionRisk}`).toBe(0);
    // P-5: distinct viewers ⇒ unlinkable.
    expect(p5.compared, `P-5: viewers must share hidden slots to compare; compared=${p5.compared}`).toBeGreaterThan(20);
    expect(p5.sameHandle, `P-1/P-5: the SAME physical hidden tile must map to DIFFERENT opaque handles per viewer (unlinkable); shared ${p5.sameHandle}/${p5.compared}`).toBe(0);
    // P-4: durable reconnect stability + privacy preserved across reconnect.
    expect(reconciledAafter, 'reconnect must re-reach the reconciled snapshot').toBe(true);
    expect(pidA === pidAafter, `P-4: reconnect must retain durable PlayerId (before=${pidA}, after=${pidAafter})`).toBe(true);
    expect(p4Changed, `P-4: same-PlayerId reconnect ⇒ byte-identical entitled ids; ${p4Changed} changed`).toBe(0);
    expect(aBacksAfter.length, `P-4: reconnect must keep foreign tiles concealed as backs; got ${aBacksAfter.length}`).toBeGreaterThanOrEqual(20);
    expect(p4OpaqueFails.length, `P-4: reconnect must preserve opaque backs (no reveal); ${p4OpaqueFails.length} degraded`).toBe(0);
  });

  test('inbound authority: a peer must not observe a client wall things push', async ({ browser }, testInfo) => {
    testInfo.setTimeout(150_000);
    const gameId = `g19-inbound-${Date.now()}`;
    const base = testInfo.project.use.baseURL as string;
    const ctxB = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    const b = await ctxB.newPage(); const sinkB = newSink(); attachRawWsCapture(b, sinkB);
    await b.goto(buildGameUrl(base, makeConfig({ gameId, seat: 1, dealMode: 'auto', botCount: 2, botDifficulty: 'Medium', handCount: 4 })), { waitUntil: 'domcontentloaded' });
    await b.waitForTimeout(1000); await dismissLobbyAndTour(b); await ensureConnected(b); await takeSeatByClick(b, 1); await b.waitForTimeout(1500);

    const ctxA = await browser.newContext({ viewport: { width: 1000, height: 700 } });
    const a = await ctxA.newPage();
    await a.goto(buildGameUrl(base, makeConfig({ gameId, seat: 0, dealMode: 'auto', botCount: 2, botDifficulty: 'Medium', handCount: 4 })), { waitUntil: 'domcontentloaded' });
    await a.waitForTimeout(1000); await dismissLobbyAndTour(a); await ensureConnected(a); await takeSeatByClick(a, 0);
    await clickDeal(a); await waitForPlayableHand(a, 60_000).catch(() => {}); await a.waitForTimeout(1500);

    const wallB_before = await b.evaluate(() => { const w = (window as any).game?.world; let n = 0; if (w?.things) for (const t of w.things.values()) if (t?.slot?.group === 'wall') n++; return n; });
    // offense = a GENUINE real-pointer wall drag by seat 0 (the client things push)
    const off = await realDragWallTile(a); await a.waitForTimeout(1500);
    const peer = await b.evaluate(() => { const w = (window as any).game?.world; let claimed = 0, n = 0; if (w?.things) for (const t of w.things.values()) if (t?.slot?.group === 'wall') { n++; if (t.claimedBy != null) claimed++; } return { n, claimed }; });

    recordEvidence('g19-inbound-authority.json', { offenderHeld: off.held, wallB_before, peer,
      discriminators: { peerWallUnchanged: peer.n === wallB_before, peerNoForeignClaim: peer.claimed === 0 } });
    await ctxA.close(); await ctxB.close();

    expect(peer.claimed, 'a peer must NOT observe an inbound client wall claim/move').toBe(0);
    expect(peer.n, 'peer authoritative wall unchanged by an offender push').toBe(wallB_before);
  });
});

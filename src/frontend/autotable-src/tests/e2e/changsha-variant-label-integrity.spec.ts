// G15 (OWNED by hudson-1) — variant LABEL integrity + authoritative variant field.
// The visible variant label MUST derive from an authoritative variant signal on
// the WIRE (not the hardcoded FOUR_PLAYER match, not URL-only). Asserts BOTH the
// wire authority AND the rendered label. RED@200cad4 (badge shows Riichi 4p;
// match.conditions.gameType hardcoded FOUR_PLAYER; no `variant` kind). Read-only.
import { test, expect, type Page, type WebSocket as PWWebSocket } from '@playwright/test';
import { buildGameUrl, makeConfig, dismissLobbyAndTour, ensureConnected, takeSeatByClick, clickDeal } from './_playability';
import { recordEvidence } from './_uat_red';

const RIICHI = /Riichi|4p|no red/i;

function captureKinds(page: Page, sink: { kinds: Record<string, number>; variantEntries: any[]; matchConditions: any } ) {
  page.on('websocket', (ws: PWWebSocket) => {
    ws.on('framereceived', (data) => {
      const p = typeof data.payload === 'string' ? data.payload : data.payload?.toString('utf8');
      if (!p || p[0] !== '{') return; let msg: any; try { msg = JSON.parse(p); } catch { return; }
      for (const e of (msg?.entries ?? [])) {
        if (!Array.isArray(e)) continue; const kind = e[0], key = e[1], v = e[2];
        sink.kinds[kind] = (sink.kinds[kind] ?? 0) + 1;
        if (/variant/i.test(String(kind))) sink.variantEntries.push({ key, v });
        if (kind === 'match' && v && typeof v === 'object' && v.conditions) sink.matchConditions = v.conditions;
      }
    });
  });
}

async function readWireAuthority(page: Page) {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game; const c = g?.client;
    const match = c?.match?.get ? c.match.get(0) : null;
    const pickup = c?.pickup?.get ? c.pickup.get('current') : null;
    // Look for ANY authoritative variant/dealMode signal on client collections.
    const anyVariantColl = !!(c?.variant || (c?.collections && c.collections.variant));
    return {
      matchGameType: match?.conditions?.gameType ?? null,          // hardcoded FOUR_PLAYER on 200cad4
      matchDealMode: match?.conditions?.dealMode ?? null,          // stripped by translator
      pickupDealMode: pickup?.dealMode ?? null,
      hasAuthoritativeVariantField: anyVariantColl,
      urlVariant: new URLSearchParams(location.search).get('variant'),
      urlDealMode: new URLSearchParams(location.search).get('dealMode'),
    };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

for (const dealMode of ['auto', 'manual'] as const) {
  test(`G15/G21 authoritative variant+dealMode on the wire (${dealMode})`, async ({ page }, testInfo) => {
    testInfo.setTimeout(90_000);
    await page.setViewportSize({ width: 1280, height: 800 });
    const sink = { kinds: {} as Record<string, number>, variantEntries: [] as any[], matchConditions: null as any };
    captureKinds(page, sink);
    const base = testInfo.project.use.baseURL as string;
    const cfg = makeConfig({ gameId: `red-wire-${dealMode}-${Date.now()}`, dealMode, botCount: 3, botDifficulty: 'Medium', handCount: 4 });
    await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
    await takeSeatByClick(page, 0); await clickDeal(page); await page.waitForTimeout(3000);
    const wire = await readWireAuthority(page);
    // Rendered label integrity: the visible badge + setup-desc must read the
    // authoritative variant (Changsha), NEVER the hardcoded Riichi/4p.
    const labels = await page.evaluate(() => {
      const t = (id: string) => { const e = document.getElementById(id); return e ? (e.textContent || '').trim() : null; };
      return { badge: t('variant-badge'), setupDesc: t('setup-desc') };
    });

    const hasVariantKind = Object.keys(sink.kinds).some((k) => /variant/i.test(k));
    const authoritativeVariantIsChangsha = wire.hasAuthoritativeVariantField || hasVariantKind || wire.matchGameType === 'CHANGSHA';
    const authoritativeDealModePresent = wire.matchDealMode != null || wire.pickupDealMode != null;
    const labelIsRiichi = RIICHI.test(String(labels.badge)) || RIICHI.test(String(labels.setupDesc));

    recordEvidence(`red-variant-dealmode-wire-${dealMode}.json`, { dealMode, wire, wireKinds: sink.kinds, hasVariantKind, matchConditions: sink.matchConditions, labels,
      discriminators: { authoritativeVariantIsChangsha, authoritativeDealModePresent, matchGameTypeHardcodedFourPlayer: wire.matchGameType === 'FOUR_PLAYER', labelIsRiichi } });

    // ACCEPTANCE (RED@200cad4):
    // (label integrity) the rendered variant label must never show Riichi/4p in Changsha.
    expect(labelIsRiichi, `G15: variant label must not show Riichi/4p in Changsha; badge=${labels.badge}, setupDesc=${labels.setupDesc}`).toBe(false);
    // (wire authority) the wire must expose the authoritative variant, not hardcoded FOUR_PLAYER.
    expect(wire.matchGameType, `G15: match.conditions.gameType must not be the hardcoded FOUR_PLAYER`).not.toBe('FOUR_PLAYER');
    expect(authoritativeVariantIsChangsha, `G15: an authoritative variant=changsha signal must exist on the wire; wire=${JSON.stringify(wire)} kinds=${JSON.stringify(Object.keys(sink.kinds))}`).toBe(true);
    // (dealMode wire authority) authoritative dealMode must be on the wire, not URL-only.
    expect(authoritativeDealModePresent, `G21: authoritative dealMode must be on the wire (match/pickup), not only the URL; wire=${JSON.stringify(wire)}`).toBe(true);
  });
}

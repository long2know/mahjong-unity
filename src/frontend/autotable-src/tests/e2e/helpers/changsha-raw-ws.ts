// Shared raw-WS spectator-JOIN capture harness (OWNED by hudson-1). Captures the
// actual server→client `things` frames (autotable Entry tuples [kind,key,value])
// and provides opaque-handle / face / reconstruction analysis for the G19 gate.
// REAL observation only — no client.update / injection.
import { type Page, type WebSocket as PWWebSocket } from '@playwright/test';

export interface RawWsSink {
  frames: number; things: number;
  kinds: Record<string, number>;
  kindSample: Record<string, any>;
  identityKinds: Record<string, number>;
  raw: { key: any; v: any }[];
}
export function newSink(): RawWsSink {
  return { frames: 0, things: 0, kinds: {}, kindSample: {}, identityKinds: {}, raw: [] };
}

// Attach a raw-WS frame listener. Entry wire shape is a TUPLE: [kind, key, value].
export function attachRawWsCapture(page: Page, sink: RawWsSink): void {
  page.on('websocket', (ws: PWWebSocket) => {
    ws.on('framereceived', (data) => {
      const payload = typeof data.payload === 'string' ? data.payload : data.payload?.toString('utf8');
      if (!payload || payload[0] !== '{') return;
      let msg: any; try { msg = JSON.parse(payload); } catch { return; }
      sink.frames++;
      const entries = msg?.entries; if (!Array.isArray(entries)) return;
      for (const e of entries) {
        if (!Array.isArray(e)) continue;
        const kind = e[0], key = e[1], v = e[2];
        sink.kinds[kind] = (sink.kinds[kind] ?? 0) + 1;
        if (!(kind in sink.kindSample)) sink.kindSample[kind] = { key, value: v, keys: (v && typeof v === 'object') ? Object.keys(v) : typeof v };
        if (v && typeof v === 'object' && (('face' in v && v.face != null) || 'typeIndex' in v || 'type' in v)) sink.identityKinds[kind] = (sink.identityKinds[kind] ?? 0) + 1;
        if (kind !== 'things') continue;
        sink.things++;
        if (v && typeof v === 'object') sink.raw.push({ key, v });
      }
    });
  });
}

export function slotOf(v: any): string { return String(v?.slotName ?? v?.slot?.name ?? v?.slot ?? ''); }

// --- client-state readers (AUTHORITATIVE per-viewer identity) ----------------
// IMPORTANT (Frost adjudication 2026-08-11): a Thing's numeric `index` is NOT an
// identity. For an anonymous face-down BACK (a tile the viewer is not entitled to)
// `index` is a local InstancedMesh/back-pool ALLOCATION id (>=108, numeric BY
// DESIGN, disjoint from the real tile ids 0-107) and `typeIndex` is 0 (a sentinel
// — the back carries no face). The authoritative per-viewer identity of a back is
// the opaque `hiddenHandle` ("h_…"); an entitled/real tile has `hiddenHandle===null`
// and its real numeric tile id in `index`. Privacy (P-1/P-5) MUST be asserted on
// `hiddenHandle`, never on `index` (asserting `String(index)` was the prior bug
// that produced 94 false "numeric handle" failures against a correct backend).
export interface ThingIdentity {
  slot: string;
  index: number;               // 0-107 = real tile id; >=108 = anonymous back allocation id
  typeIndex: number;           // real face for an entitled tile; 0 (sentinel) for a back
  hidden: boolean;
  hiddenHandle: string | null; // opaque "h_…" for a back; null for a real/entitled tile
  rotationIndex: number;
}

// Read the reconciled world.things for every slot matching slotRe, exposing the
// full identity shape (not the allocation id alone) so P-1..P-5 can assert on the
// authoritative fields.
export async function thingIdentityMap(page: Page, slotRe: string): Promise<Record<string, ThingIdentity>> {
  return page.evaluate((re) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world; const out: Record<string, any> = {};
    const rx = new RegExp(re);
    if (w?.things) for (const t of w.things.values()) {
      const nm = String(t?.slot?.name ?? '');
      if (!rx.test(nm) || t.index === undefined || t.index === null) continue;
      out[nm] = {
        slot: nm,
        index: Number(t.index),
        typeIndex: Number(t.typeIndex),
        hidden: t.hidden === true,
        hiddenHandle: (t.hiddenHandle === undefined ? null : t.hiddenHandle),
        rotationIndex: Number(t.rotationIndex),
      };
    }
    return out;
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, slotRe);
}

// A BACK (anonymous face-down, viewer not entitled) ⟺ it carries an opaque handle.
export function isBack(t: ThingIdentity): boolean { return t.hiddenHandle !== null; }

// The authoritative per-viewer identity STRING compared across viewers/reconnects:
// the opaque handle for a back, else the real numeric tile id. Never the mesh id.
export function authoritativeHandle(t: ThingIdentity): string {
  return t.hiddenHandle !== null ? t.hiddenHandle : String(t.index);
}

// slot → opaque handle, restricted to the BACKS in the map (entitled reals dropped).
export function backHandleMap(m: Record<string, ThingIdentity>): Record<string, string> {
  const o: Record<string, string> = {};
  for (const [k, t] of Object.entries(m)) if (t.hiddenHandle !== null) o[k] = t.hiddenHandle;
  return o;
}

// slot → real numeric tile id (as string), restricted to the ENTITLED reals.
export function realIndexMap(m: Record<string, ThingIdentity>): Record<string, string> {
  const o: Record<string, string> = {};
  for (const [k, t] of Object.entries(m)) if (t.hiddenHandle === null) o[k] = String(t.index);
  return o;
}

// Opaque ⟺ starts "h_" AND is not a bare integer (defeats the index-as-identity bug).
export function isOpaqueHandle(h: string): boolean { return /^h_/.test(h) && !/^-?\d+$/.test(h); }

// Slots (matching slotRe) rendered by more than one Thing — a double-occupancy /
// ghost / over-reveal detector.
export async function duplicateSlots(page: Page, slotRe: string): Promise<string[]> {
  return page.evaluate((re) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world; const rx = new RegExp(re); const seen: Record<string, number> = {};
    if (w?.things) for (const t of w.things.values()) { const nm = String(t?.slot?.name ?? ''); if (rx.test(nm)) seen[nm] = (seen[nm] ?? 0) + 1; }
    return Object.keys(seen).filter((k) => seen[k] > 1);
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, slotRe);
}
export async function faceMap(page: Page, slotRe: string): Promise<Record<string, number>> {
  return page.evaluate((re) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world; const out: Record<string, number> = {};
    const rx = new RegExp(re);
    if (w?.things) for (const t of w.things.values()) { const nm = String(t?.slot?.name ?? ''); if (rx.test(nm) && typeof t.typeIndex === 'number') out[nm] = t.typeIndex % 37; }
    return out;
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, slotRe);
}
export async function readPlayerId(page: Page): Promise<string | null> {
  return page.evaluate(() => { const g = (window as any).game; return (g?.client?.playerId) ? g.client.playerId() : null; });
}

// --- analysis --------------------------------------------------------------
// opaque ⟺ non-numeric STRING (keyed by durable playerId + server secret).
export function handleIsOpaque(h: string): boolean { return !/^-?\d+$/.test(h); }
export function allNumeric(handles: string[]): boolean { return handles.length > 0 && handles.every((h) => /^-?\d+$/.test(h)); }
export function handleHealth(handles: string[]): { collisions: number; precisionRisk: number; distinct: number } {
  const seen = new Map<string, number>(); let precisionRisk = 0;
  for (const h of handles) { seen.set(h, (seen.get(h) ?? 0) + 1); if (/^-?\d+$/.test(h) && Math.abs(Number(h)) > Number.MAX_SAFE_INTEGER) precisionRisk++; }
  let collisions = 0; for (const [, c] of seen) if (c > 1) collisions += c - 1;
  return { collisions, precisionRisk, distinct: seen.size };
}
export function crossViewerLinkable(a: Record<string, string>, b: Record<string, string>): { compared: number; sameHandle: number } {
  let compared = 0, sameHandle = 0;
  for (const k of Object.keys(a)) if (k in b) { compared++; if (a[k] === b[k]) sameHandle++; }
  return { compared, sameHandle };
}
export function multisetOverlap(a: Record<string, number>, b: Record<string, number>): { aCount: number; bCount: number; sharedBySlot: number } {
  let sharedBySlot = 0; for (const k of Object.keys(a)) if (k in b && a[k] === b[k]) sharedBySlot++;
  return { aCount: Object.keys(a).length, bCount: Object.keys(b).length, sharedBySlot };
}
// wire-level face leak: any hidden hand/wall `things` value carrying a concrete face.
export function faceLeaks(raw: { key: any; v: any }[], viewerSeat: number): { slot: string; seat: number; group: string }[] {
  const out: { slot: string; seat: number; group: string }[] = [];
  for (const { v } of raw) {
    const slot = slotOf(v); const m = /^(hand|wall)\.\d+(?:\.\d+)?@(\d+)$/.exec(slot); if (!m) continue;
    const group = m[1], seat = Number(m[2]);
    const hidden = (group === 'hand' && seat !== viewerSeat) || group === 'wall';
    const hasFace = v && typeof v === 'object' && 'face' in v && v.face !== null && v.face !== undefined;
    if (hidden && hasFace) out.push({ slot, seat, group });
  }
  return out;
}

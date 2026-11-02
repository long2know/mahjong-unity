// Shared helpers for the UAT RED matrix (200cad4). Read-only observation +
// genuine controls only. NO client.update / collection injection / synthetic
// DOM / direct emitDiscard for gameplay advancement.
import { type Page, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

export const RED_OUT = process.env.RED_OUT
  ?? path.resolve(__dirname, '../../../../../session-files/completion-proof/uat-200cad4/hudson/red-matrix');

export function recordEvidence(name: string, obj: unknown): void {
  const dir = path.join(RED_OUT, 'evidence');
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, name), JSON.stringify(obj, null, 2));
}
export async function shot(page: Page, name: string): Promise<void> {
  const dir = path.join(RED_OUT, 'shots');
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: path.join(dir, name) });
}

export const STACKS = [14, 14, 13, 13];
export const BASE_STACK = [0, 14, 28, 41]; // canonical perimeter start per seat (54 stacks)

// ---- canonical + PHYSICAL wall analysis (retires the fullOrEmpty proxy) -----
export interface WallAnalysis {
  tiles: number;
  perSeatStacks: number[];
  occStacks: number;
  perimeterRuns: number;          // canonical circular runs of occupied stacks
  worldPolylineRuns: number;      // runs derived from REAL render positions (one-pitch)
  worldSingleContiguous: boolean; // exactly one physical run, corners allowed
  pitch: number;                  // median within-run step (world units)
  stepCount: number;
  cornerDiscontinuities: number;  // Ripley G4: steps > 1.6× pitch (incl. corners)
  discontinuitySamples: { from: number; to: number; d: number; pitch: number }[];
  strictOnePitchPolyline: boolean; // Ripley G4 acceptance
}

export async function analyzeWall(page: Page): Promise<WallAnalysis> {
  return page.evaluate(({ STACKS, BASE_STACK }) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world;
    const occ: Record<string, { wx: number; wy: number }> = {};
    let tiles = 0;
    if (w?.things) for (const t of w.things.values()) {
      if (t?.slot?.group !== 'wall') continue;
      const m = /^wall\.(\d+)\.(\d+)@(\d+)$/.exec(String(t.slot?.name)); if (!m) continue;
      tiles++;
      const key = m[3] + '.' + m[1];
      if (!occ[key]) { let pos = { x: 0, y: 0 }; try { const pl = t.place(); pos = { x: pl.position.x, y: pl.position.y }; } catch { /* */ } occ[key] = { wx: pos.x, wy: pos.y }; }
    }
    const arr: boolean[] = new Array(54).fill(false);
    const per = [0, 0, 0, 0];
    const posByIdx: Record<number, { wx: number; wy: number }> = {};
    for (let seat = 0; seat < 4; seat++) for (let c = 0; c < STACKS[seat]; c++) {
      const k = seat + '.' + c;
      if (occ[k]) { const idx = BASE_STACK[seat] + c; arr[idx] = true; per[seat]++; posByIdx[idx] = occ[k]; }
    }
    const occStacks = arr.filter(Boolean).length;
    // canonical circular runs
    let runs = 0; for (let i = 0; i < 54; i++) if (arr[i] && !arr[(i - 1 + 54) % 54]) runs++;
    if (occStacks === 54) runs = 1;
    // Ripley G4: STRICT one-pitch polyline — walk occupied stacks in canonical
    // draw order and measure world-distance between consecutive occupied stacks
    // (INCLUDING corners). pitch = the median within-run step; any step (corner
    // included) exceeding ~1.6× pitch is a physical discontinuity.
    const occIdxAll: number[] = []; for (let i = 0; i < 54; i++) if (arr[i]) occIdxAll.push(i);
    const steps: { from: number; to: number; d: number }[] = [];
    if (occIdxAll.length > 1 && occIdxAll.length < 54) {
      for (let i = 0; i < 54; i++) {
        const j = (i + 1) % 54;
        if (arr[i] && arr[j] && posByIdx[i] && posByIdx[j]) steps.push({ from: i, to: j, d: Math.hypot(posByIdx[i].wx - posByIdx[j].wx, posByIdx[i].wy - posByIdx[j].wy) });
      }
    }
    const ds = steps.map((s) => s.d).sort((a, b) => a - b);
    const pitch = ds.length ? ds[Math.floor(ds.length / 2)] : 0;
    const discontinuities = pitch > 0 ? steps.filter((s) => s.d > pitch * 1.6).map((s) => ({ from: s.from, to: s.to, d: Math.round(s.d), pitch: Math.round(pitch) })) : [];
    // physical polyline: consecutive occupied indices must be within one pitch
    // (~6u) OR a single corner jump; count breaks that exceed a corner.
    const occIdx: number[] = []; for (let i = 0; i < 54; i++) if (arr[i]) occIdx.push(i);
    let worldRuns = runs;
    // dist between adjacent-in-canonical occupied stacks (circular)
    const CORNER_MAX = 70; // corner gap ~53u; within-wall pitch ~6u
    let physicalBreaks = 0;
    if (occIdx.length > 1 && occIdx.length < 54) {
      // Walk the single canonical run(s); for each pair of canonical-adjacent
      // occupied stacks verify physical proximity <= CORNER_MAX.
      for (let i = 0; i < 54; i++) {
        const j = (i + 1) % 54;
        if (arr[i] && arr[j]) {
          const a = posByIdx[i], b = posByIdx[j];
          if (a && b) { const d = Math.hypot(a.wx - b.wx, a.wy - b.wy); if (d > CORNER_MAX) physicalBreaks++; }
        }
      }
    }
    return {
      tiles, perSeatStacks: per, occStacks, perimeterRuns: runs,
      worldPolylineRuns: worldRuns + physicalBreaks,
      worldSingleContiguous: runs === 1 && physicalBreaks === 0,
      // Ripley G4 strict metric:
      pitch: Math.round(pitch), stepCount: steps.length,
      cornerDiscontinuities: discontinuities.length, discontinuitySamples: discontinuities.slice(0, 6),
      strictOnePitchPolyline: runs === 1 && discontinuities.length === 0,
    };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, { STACKS, BASE_STACK });
}

// ---- mode / labels / connection ------------------------------------------
export interface ChromeState {
  title: string; badge: string | null; setupDesc: string | null;
  connectVisible: boolean; disconnectVisible: boolean; connected: boolean;
  bannerText: string | null; rawMatchGameType: string | null; worldGameType: string | null;
  variantChangshaClass: boolean; variantRiichiClass: boolean;
  dealDisabled: boolean | null;
}
export async function readChrome(page: Page): Promise<ChromeState> {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game;
    const vis = (id: string) => { const e = document.getElementById(id); return !!e && e.offsetParent !== null; };
    const txt = (id: string) => { const e = document.getElementById(id); return e ? (e.textContent || '').trim() : null; };
    const rawMatch = g?.client?.match?.get ? g.client.match.get(0) : null;
    const dealEl = document.getElementById('deal') as HTMLButtonElement | null;
    return {
      title: document.title,
      badge: txt('variant-badge'), setupDesc: txt('setup-desc'),
      connectVisible: vis('connect'), disconnectVisible: vis('disconnect'),
      connected: !!g?.client?.connected,
      bannerText: txt('turn-banner'),
      rawMatchGameType: rawMatch?.conditions?.gameType ?? null,
      worldGameType: g?.world?.conditions?.gameType ?? null,
      variantChangshaClass: /variant-changsha/.test(document.body.className),
      variantRiichiClass: /variant-riichi/.test(document.body.className),
      dealDisabled: dealEl ? dealEl.disabled : null,
    };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

// ---- hand face orientation ------------------------------------------------
export async function readHandFace(page: Page, seat: number): Promise<{ up: number; down: number; total: number }> {
  return page.evaluate((s) => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const w = (window as any).game?.world; const up: number[] = [];
    if (w?.things) for (const t of w.things.values()) {
      const nm = String(t?.slot?.name ?? '');
      if (new RegExp(`^hand\\.\\d+@${s}$`).test(nm) && typeof t.rotationIndex === 'number') up.push(t.rotationIndex);
    }
    return { up: up.filter((r) => r === 1).length, down: up.filter((r) => r !== 1).length, total: up.length };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  }, seat);
}

// ---- real-pointer wall drag ----------------------------------------------
export interface WallDragResult { pick: any; isMyPickupTurn: boolean; held: any; }
export async function realDragWallTile(page: Page): Promise<WallDragResult> {
  const info = await page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game; const w = g?.world;
    const camera = g?.mainView?.camera; const main = document.getElementById('main');
    const rect = main?.getBoundingClientRect();
    if (camera) { try { camera.parent?.updateMatrixWorld(true); camera.updateMatrixWorld(true); camera.matrixWorldInverse?.copy(camera.matrixWorld).invert(); } catch { /* */ } }
    const proj = (p: any) => {
      const mw = camera.matrixWorldInverse.elements, pm = camera.projectionMatrix.elements;
      const vx = mw[0]*p.x+mw[4]*p.y+mw[8]*p.z+mw[12], vy = mw[1]*p.x+mw[5]*p.y+mw[9]*p.z+mw[13], vz = mw[2]*p.x+mw[6]*p.y+mw[10]*p.z+mw[14], vw = mw[3]*p.x+mw[7]*p.y+mw[11]*p.z+mw[15];
      const cx = pm[0]*vx+pm[4]*vy+pm[8]*vz+pm[12]*vw, cy = pm[1]*vx+pm[5]*vy+pm[9]*vz+pm[13]*vw, cw = pm[3]*vx+pm[7]*vy+pm[11]*vz+pm[15]*vw;
      return { sx: (rect?.left ?? 0) + (cx/cw+1)*0.5*(rect?.width??0), sy: (rect?.top ?? 0) + (1-cy/cw)*0.5*(rect?.height??0) };
    };
    let best: any = null;
    for (const t of w.things.values()) {
      const nm = String(t?.slot?.name ?? ''); const m = /^wall\.(\d+)\.(\d+)@(\d+)$/.exec(nm);
      if (!m || t.claimedBy != null) continue;
      const up = t.slot?.links?.up; if (up && up.thing) continue;
      const pl = t.place(); const s = proj(pl.position);
      best = { id: t.index, slot: nm, cx: Math.round(s.sx), cy: Math.round(s.sy) };
      if (Number(m[2]) === 1) break;
    }
    return { pick: best, isMyPickupTurn: !!(w?.isMyPickupTurn && w.isMyPickupTurn()),
      centerX: (rect?.left ?? 0) + (rect?.width ?? 0) / 2, centerY: (rect?.top ?? 0) + (rect?.height ?? 0) / 2 };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
  let held: any = null;
  if (info.pick) {
    await page.mouse.move(info.pick.cx, info.pick.cy); await page.waitForTimeout(120);
    await page.mouse.down();
    for (let i = 1; i <= 6; i++) { await page.mouse.move(info.pick.cx + (info.centerX - info.pick.cx) * i / 6, info.pick.cy + (info.centerY - info.pick.cy) * i / 6); await page.waitForTimeout(60); }
    held = await page.evaluate((tid) => {
      /* eslint-disable @typescript-eslint/no-explicit-any */
      const g = (window as any).game; const w = g?.world; const t = w?.things?.get(tid);
      const hm = (w as any)?.heldMouse, mo = (w as any)?.mouse;
      const offset = (hm && mo) ? Math.hypot((mo.x ?? 0) - (hm.x ?? 0), (mo.y ?? 0) - (hm.y ?? 0)) : null;
      return { isHolding: !!(w?.isHolding && w.isHolding()), claimedBy: t?.claimedBy ?? null,
        dragOffsetWorld: offset != null ? Math.round(offset) : null };
      /* eslint-enable @typescript-eslint/no-explicit-any */
    }, info.pick.id);
    await page.mouse.up(); await page.waitForTimeout(400);
  }
  return { pick: info.pick, isMyPickupTurn: info.isMyPickupTurn, held };
}

// ---- console/page/server error gate --------------------------------------
export function installErrorGate(page: Page): { errors: string[] } {
  const errors: string[] = [];
  const benign = (t: string) => /\/api\/games\/[^ ]*\b(404|Not Found)|Failed to load resource: the server responded with a status of 404/.test(t);
  page.on('console', (m) => { if (m.type() === 'error') { const t = m.text(); if (!benign(t)) errors.push('console:' + t.slice(0, 200)); } });
  page.on('pageerror', (e) => errors.push('pageerror:' + String(e).slice(0, 200)));
  return { errors };
}

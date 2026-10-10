// Phase K Wave 4 — Pattern ordering utilities (renderer-free).
//
// Wave 3 kept `comparePatterns` / `sortPatterns` / `setPatternDisplayOrder`
// / `loadPatternOrderingFromApi` in `game-ui.ts`.  The trouble is that
// `game-ui.ts` (~102 kB) carries the result modal, settings drawer
// wiring, replay launcher and four hundred lines of DOM glue —
// importing it from `move-log.ts` (or the upcoming `scene-shell.ts`)
// drags the whole HUD into the renderer-critical chunk.
//
// Wave 4 splits the pure pattern-ordering helpers + the canonical
// display order into this small module so the renderer / move-log
// chain can pull only the ~1 kB of comparator logic.  `game-ui.ts`
// re-exports the helpers for backward-compat with anyone outside the
// frontend bundle (Vasquez's tests reference them by name).

// Phase J Wave 3 — Canonical display order for AllPatterns.  Mirrors
// Bishop's backend `ChangshaPatternOrdering` table.  Patterns NOT in
// this list sort alphabetically after the listed ones.
const PATTERN_DISPLAY_ORDER: ReadonlyArray<string> = [
  'heavenlyHand',
  'earthlyHand',
  'lastTileFromWall',
  'lastDiscardCatch',
  'kongReplacementWin',
  'robbedKong',
  'robbingKong',
  'nineGates',
  'nineTerminals',
  'allPungs',
  'allConcealed',
  'sevenPairs',
  'selfDraw',
  'singleWait',
];

const patternDisplayOrderIndex: Record<string, number> = (() => {
  const out: Record<string, number> = {};
  PATTERN_DISPLAY_ORDER.forEach((key, i) => { out[key] = i; });
  return out;
})();

// Normalise a pattern key for label lookup.  Backend canonical wire form is
// camelCase (e.g. `sevenPairs`); legacy code paths or test fixtures may still
// emit PascalCase (`SevenPairs`).  Lowercasing the first character collapses
// both spellings to the same key.
export function normalizePatternKey(p: string): string {
  if (!p) return p;
  return p.charAt(0).toLowerCase() + p.slice(1);
}

export function setPatternDisplayOrder(map: Record<string, number>): void {
  for (const k of Object.keys(patternDisplayOrderIndex)) {
    delete patternDisplayOrderIndex[k];
  }
  for (const [k, v] of Object.entries(map)) {
    if (typeof v === 'number' && isFinite(v)) {
      patternDisplayOrderIndex[normalizePatternKey(k)] = v;
    }
  }
}

export function comparePatterns(a: string, b: string): number {
  const ka = normalizePatternKey(a);
  const kb = normalizePatternKey(b);
  const ia = patternDisplayOrderIndex[ka];
  const ib = patternDisplayOrderIndex[kb];
  if (ia !== undefined && ib !== undefined) return ia - ib;
  if (ia !== undefined) return -1;
  if (ib !== undefined) return 1;
  return ka < kb ? -1 : ka > kb ? 1 : 0;
}

export function sortPatterns(patterns: ReadonlyArray<string>): string[] {
  return [...patterns].sort(comparePatterns);
}

export async function loadPatternOrderingFromApi(): Promise<void> {
  try {
    const res = await fetch('/api/changsha/pattern-ordering', {
      credentials: 'same-origin',
    });
    if (!res.ok) return;
    const json = (await res.json()) as Record<string, unknown>;
    const map: Record<string, number> = {};
    for (const [k, v] of Object.entries(json)) {
      if (typeof v === 'number' && isFinite(v)) map[k] = v;
    }
    if (Object.keys(map).length > 0) {
      setPatternDisplayOrder(map);
    }
  } catch {
    /* hardcoded fallback stays — nothing to do */
  }
}

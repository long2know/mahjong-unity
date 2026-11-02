// Phase K Wave 7 — AI commentary panel (Bishop's finalized contract).
//
// W6 shipped this panel against a stub contract
// (`{ lines: [{ turn, actor, text }] }`) on a backend that returned
// 404 / 503 in steady-state.  W7 swaps in Bishop's finalized JSON
// contract from `IReadOnlyList<CommentaryRecord>`:
//
//   GET /api/games/{gameId}/commentary/replay
//     → 200 [
//         {
//           "gameId": "GUID",
//           "turnNumber": 4,
//           "phase": "draw" | "discard" | "claim" | "score" | "deal" | "narration",
//           "speaker": "pbp" | "color" | "analyst" | "narrator",
//           "text": "South draws a yellow dragon — that's the
//                    second dragon they need for a Big Dragons hand.",
//           "emotionIntensity": 0.65,         // 0..1
//           "tileReferences": ["S2-Z7"],      // optional, may be empty
//           "generatedAt": "2026-09-12T11:23:41Z"
//         },
//         ...
//       ]
//     → 404 / 503 → "Phase L feature — not yet available"
//
// Renderer
// --------
// Records are grouped by `turnNumber` into collapsible sections.
// Inside each turn, records render top-to-bottom in array order —
// the backend ships them sorted by `generatedAt`.  Each record
// surfaces:
//   • Speaker badge — PBP / Color / Analyst / Narrator.
//   • Emotion-intensity bar — 0..1 → 0..100 % CSS width.
//   • Inline text — interpolated tile-reference chips
//     (clickable; emit a `commentary:tile-ref` event so the
//     replay surface can pulse the relevant tile on the board).
//
// W6 back-compat
// --------------
// The legacy `{ lines: [...] }` envelope is still parsed (fields
// re-shaped onto `CommentaryRecord`) so a deploy where Bishop's
// service falls back to the stub doesn't break the panel.

const PHASE_L_COPY = 'Phase L feature — not yet available.';
const EMPTY_COPY = 'No commentary available for this replay yet.';
const LOADING_COPY = 'Loading commentary…';

// ── Bishop's W7 contract types ──────────────────────────────────────

export type CommentarySpeaker = 'pbp' | 'color' | 'analyst' | 'narrator';

export type CommentaryPhase =
  | 'draw'
  | 'discard'
  | 'claim'
  | 'score'
  | 'deal'
  | 'narration';

/**
 * Phase K Wave 10 — Bishop's pinned `TileReference` shape on the
 * commentary wire.  Wave 9 shipped tile refs as bare strings
 * (`tileReferences: ["man5", "pin3"]`); W10 promotes them to
 * objects with explicit `suit` + `rank` so the renderer can avoid
 * re-parsing the wire id at click time (the parsing happens in
 * `world.findThingByFace` which still accepts the bare id for
 * back-compat).  See `docs/contracts/commentary-tile-ref.md` for
 * the canonical schema + the W9→W10 migration discipline.
 *
 * `suit` accepts: `'man' | 'pin' | 'sou' | 'honor'`.
 * `rank` is 1..9 for numbered suits, 1..7 for `honor`.
 */
export interface TileReference {
  tileId: string;
  suit: string;
  rank: number;
}

export interface CommentaryRecord {
  gameId: string;
  turnNumber: number;
  phase: CommentaryPhase;
  speaker: CommentarySpeaker;
  text: string;
  /** 0..1.  Drives the per-record intensity bar. */
  emotionIntensity: number;
  /**
   * Stable tile IDs the record references.  Phase K Wave 10 — the
   * wire now ships canonical `TileReference` objects per Bishop's
   * `CommentaryTileReference` shape pinning.  W9 ship-shape (bare
   * strings) is still accepted by `coerceRecord` for one-wave
   * compatibility while Bishop's flag flips.  Clicking a chip emits
   * `mahjong:highlight-tile` with `{ tileId, source: 'commentary-
   * panel' }` so the 3D outline pulse can route the highlight.
   */
  tileReferences: ReadonlyArray<TileReference>;
  /** ISO-8601 timestamp.  Used as the per-record meta + sort key. */
  generatedAt: string;
}

/** Legacy W6 envelope (kept for parser back-compat). */
export interface CommentaryLine {
  turn: number;
  actor: number;
  text: string;
  timestampUtc?: string;
}

interface PanelState {
  installed: boolean;
  panel: HTMLDivElement | null;
  gameId: string | null;
  /** Collapsed-turn set; key is `turn-{n}`. */
  collapsed: Set<string>;
}

const state: PanelState = {
  installed: false,
  panel: null,
  gameId: null,
  collapsed: new Set(),
};

// ── Public surface ──────────────────────────────────────────────────

/**
 * Mount (or re-use) the panel inside `host` and fetch commentary
 * for `gameId`.  Re-entrant — calling twice replaces the records.
 */
export async function openCommentaryPanel(host: HTMLElement, gameId: string): Promise<void> {
  ensurePanel(host);
  state.gameId = gameId;
  if (state.panel === null) return;
  renderLoading(state.panel);

  try {
    const resp = await fetch(`/api/games/${encodeURIComponent(gameId)}/commentary/replay`, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
    if (resp.status === 404 || resp.status === 503) {
      renderError(state.panel);
      return;
    }
    if (!resp.ok) {
      renderError(state.panel);
      return;
    }
    const raw = (await resp.json()) as unknown;
    const records = normalizeRecords(raw, gameId);
    if (records.length === 0) {
      renderEmpty(state.panel);
      return;
    }
    renderRecords(state.panel, records);
  } catch {
    // Network failure / backend offline — surface the phase-L copy
    // rather than a hard error; the replay still plays.
    renderError(state.panel);
  }
}

/** Close + tear down the panel.  Idempotent. */
export function closeCommentaryPanel(): void {
  if (state.panel !== null && state.panel.parentNode !== null) {
    state.panel.parentNode.removeChild(state.panel);
  }
  state.panel = null;
  state.installed = false;
  state.gameId = null;
  state.collapsed.clear();
}

// ── Internals ──────────────────────────────────────────────────────

function ensurePanel(host: HTMLElement): void {
  if (state.installed && state.panel !== null && state.panel.parentNode === host) return;
  if (state.panel !== null && state.panel.parentNode !== null) {
    state.panel.parentNode.removeChild(state.panel);
  }
  const panel = document.createElement('div');
  panel.className = 'commentary-panel';
  panel.setAttribute('data-testid', 'commentary-panel');
  panel.setAttribute('role', 'log');
  panel.setAttribute('aria-live', 'polite');
  panel.setAttribute('aria-label', 'AI commentary');

  const header = document.createElement('div');
  header.className = 'commentary-panel-header';
  const title = document.createElement('h3');
  title.className = 'commentary-panel-title';
  title.textContent = 'Commentary';
  header.appendChild(title);
  panel.appendChild(header);

  const body = document.createElement('div');
  body.className = 'commentary-panel-body';
  body.setAttribute('data-commentary-body', '');
  panel.appendChild(body);

  host.appendChild(panel);
  state.panel = panel;
  state.installed = true;
}

function getBody(panel: HTMLDivElement): HTMLDivElement {
  let body = panel.querySelector<HTMLDivElement>('[data-commentary-body]');
  if (body === null) {
    body = document.createElement('div');
    body.className = 'commentary-panel-body';
    body.setAttribute('data-commentary-body', '');
    panel.appendChild(body);
  }
  return body;
}

function renderLoading(panel: HTMLDivElement): void {
  const body = getBody(panel);
  body.replaceChildren();
  const el = document.createElement('div');
  el.className = 'commentary-panel-loading';
  el.setAttribute('data-testid', 'commentary-panel-loading');
  el.setAttribute('role', 'status');
  el.textContent = LOADING_COPY;
  body.appendChild(el);
}

function renderError(panel: HTMLDivElement): void {
  const body = getBody(panel);
  body.replaceChildren();
  const el = document.createElement('div');
  el.className = 'commentary-panel-error';
  el.setAttribute('data-testid', 'commentary-panel-error');
  el.setAttribute('role', 'status');
  el.textContent = PHASE_L_COPY;
  body.appendChild(el);
}

function renderEmpty(panel: HTMLDivElement): void {
  const body = getBody(panel);
  body.replaceChildren();
  const el = document.createElement('div');
  el.className = 'commentary-panel-empty';
  el.setAttribute('data-testid', 'commentary-panel-empty');
  el.textContent = EMPTY_COPY;
  body.appendChild(el);
}

function renderRecords(panel: HTMLDivElement, records: ReadonlyArray<CommentaryRecord>): void {
  const body = getBody(panel);
  body.replaceChildren();

  const byTurn = groupByTurn(records);
  let recordIdx = 0;
  for (const [turn, group] of byTurn) {
    const section = document.createElement('section');
    section.className = 'commentary-turn';
    section.setAttribute('data-testid', `commentary-turn-${turn}`);
    section.setAttribute('data-turn', String(turn));

    const collapsed = state.collapsed.has(turnKey(turn));
    section.dataset.collapsed = collapsed ? 'true' : 'false';

    // ── turn header: collapsible toggle ─────────────────────────
    const head = document.createElement('button');
    head.type = 'button';
    head.className = 'commentary-turn-header';
    head.setAttribute('data-testid', `commentary-turn-toggle-${turn}`);
    head.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
    head.setAttribute('aria-controls', `commentary-turn-body-${turn}`);
    const headLabel = turn < 0 ? 'Pre-game' : `Turn ${turn}`;
    head.textContent = `${collapsed ? '▸' : '▾'} ${headLabel} (${group.length})`;
    head.addEventListener('click', () => {
      const key = turnKey(turn);
      if (state.collapsed.has(key)) state.collapsed.delete(key);
      else state.collapsed.add(key);
      // Re-render this section in place.
      const nextCollapsed = state.collapsed.has(key);
      section.dataset.collapsed = nextCollapsed ? 'true' : 'false';
      head.setAttribute('aria-expanded', nextCollapsed ? 'false' : 'true');
      head.textContent = `${nextCollapsed ? '▸' : '▾'} ${headLabel} (${group.length})`;
      sectionBody.hidden = nextCollapsed;
    });
    section.appendChild(head);

    // ── records body ────────────────────────────────────────────
    const sectionBody = document.createElement('div');
    sectionBody.className = 'commentary-turn-body';
    sectionBody.id = `commentary-turn-body-${turn}`;
    sectionBody.hidden = collapsed;
    for (const record of group) {
      sectionBody.appendChild(renderRecord(record, recordIdx));
      recordIdx += 1;
    }
    section.appendChild(sectionBody);
    body.appendChild(section);
  }
}

function renderRecord(record: CommentaryRecord, idx: number): HTMLDivElement {
  const row = document.createElement('div');
  row.className = `commentary-record commentary-record-${record.phase}`;
  row.setAttribute('data-testid', `commentary-record-${idx}`);
  row.setAttribute('data-record-index', String(idx));
  row.setAttribute('data-turn', String(record.turnNumber));
  row.setAttribute('data-phase', record.phase);
  row.setAttribute('data-speaker', record.speaker);

  // ── speaker badge ────────────────────────────────────────────
  const speakerBadge = document.createElement('span');
  speakerBadge.className = `commentary-speaker commentary-speaker-${record.speaker}`;
  speakerBadge.setAttribute('data-testid', `commentary-speaker-${record.speaker}`);
  speakerBadge.textContent = speakerLabel(record.speaker);
  row.appendChild(speakerBadge);

  // ── text with inline tile-ref chips ──────────────────────────
  const textWrap = document.createElement('span');
  textWrap.className = 'commentary-text';
  textWrap.appendChild(document.createTextNode(record.text));
  row.appendChild(textWrap);

  // ── tile-ref chips ───────────────────────────────────────────
  if (record.tileReferences.length > 0) {
    const refsWrap = document.createElement('div');
    refsWrap.className = 'commentary-tile-refs';
    for (const ref of record.tileReferences) {
      refsWrap.appendChild(renderTileRef(ref));
    }
    row.appendChild(refsWrap);
  }

  // ── emotion-intensity bar ────────────────────────────────────
  const intensityBarWrap = document.createElement('div');
  intensityBarWrap.className = 'commentary-intensity';
  intensityBarWrap.setAttribute('data-testid', `commentary-intensity-${idx}`);
  intensityBarWrap.setAttribute(
    'aria-label',
    `Emotion intensity ${Math.round(record.emotionIntensity * 100)}%`,
  );
  const intensityBar = document.createElement('div');
  intensityBar.className = 'commentary-intensity-fill';
  const pct = Math.max(0, Math.min(1, record.emotionIntensity));
  intensityBar.style.width = `${(pct * 100).toFixed(0)}%`;
  intensityBarWrap.appendChild(intensityBar);
  row.appendChild(intensityBarWrap);

  // ── meta footer: timestamp ───────────────────────────────────
  const meta = document.createElement('div');
  meta.className = 'commentary-record-meta';
  meta.textContent = formatTimestamp(record.generatedAt);
  row.appendChild(meta);

  return row;
}

function renderTileRef(ref: TileReference): HTMLButtonElement {
  const chip = document.createElement('button');
  chip.type = 'button';
  chip.className = 'commentary-tile-ref';
  chip.setAttribute('data-testid', `commentary-tile-ref-${ref.tileId}`);
  chip.setAttribute('data-tile-id', ref.tileId);
  // Phase K Wave 10 — surface Bishop's pinned suit + rank fields
  // as data attributes so consumer scripts (analyst overlay,
  // Vasquez specs) can filter by suit without re-parsing the id.
  chip.setAttribute('data-tile-suit', ref.suit);
  chip.setAttribute('data-tile-rank', String(ref.rank));
  chip.textContent = ref.tileId;
  chip.addEventListener('click', () => {
    // Phase K Wave 8 — Two events on click.  The legacy
    // `commentary:tile-ref` event stays for back-compat with
    // any listener Hank wires up in the analyst overlay; the
    // new `mahjong:highlight-tile` event is consumed by
    // `main-view.ts` to flash a `CustomOutline` pulse on the
    // 3D tile within 500ms.  Both events ship the same payload.
    //
    // Phase K Wave 10 — `mahjong:highlight-tile` payload now
    // carries an explicit `source` field so listeners can
    // distinguish chip-driven highlights from in-game gestures
    // (drag-hover, opponent-action callouts).  Existing main-
    // view + game.ts listeners read `detail.tileId` and ignore
    // unknown keys, so this is back-compat-safe.  Documented in
    // `docs/contracts/commentary-tile-ref.md §4`.
    window.dispatchEvent(
      new CustomEvent<{ tileId: string }>('commentary:tile-ref', { detail: { tileId: ref.tileId } }),
    );
    window.dispatchEvent(
      new CustomEvent<{ tileId: string; source: 'commentary-panel' }>(
        'mahjong:highlight-tile',
        { detail: { tileId: ref.tileId, source: 'commentary-panel' } },
      ),
    );
  });
  return chip;
}

// ── Helpers ────────────────────────────────────────────────────────

function speakerLabel(speaker: CommentarySpeaker): string {
  switch (speaker) {
    case 'pbp': return 'PBP';
    case 'color': return 'Color';
    case 'analyst': return 'Analyst';
    case 'narrator': return '🎙';
    default: return 'Commentary';
  }
}

function formatTimestamp(iso: string): string {
  try {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  } catch {
    return iso;
  }
}

function groupByTurn(records: ReadonlyArray<CommentaryRecord>): Array<[number, CommentaryRecord[]]> {
  const map = new Map<number, CommentaryRecord[]>();
  for (const r of records) {
    let bucket = map.get(r.turnNumber);
    if (bucket === undefined) {
      bucket = [];
      map.set(r.turnNumber, bucket);
    }
    bucket.push(r);
  }
  return Array.from(map.entries()).sort((a, b) => a[0] - b[0]);
}

function turnKey(turn: number): string {
  return `turn-${turn}`;
}

// ── Response normalization ─────────────────────────────────────────

/**
 * Coerce Bishop's W7 array contract OR the legacy W6 `{ lines: [...] }`
 * envelope into a `CommentaryRecord[]`.  Parse failures collapse to
 * an empty list rather than an error so the panel stays graceful.
 */
function normalizeRecords(raw: unknown, gameId: string): CommentaryRecord[] {
  // W7 shape: top-level array.
  if (Array.isArray(raw)) {
    const out: CommentaryRecord[] = [];
    raw.forEach((entry, idx) => {
      const r = coerceRecord(entry, gameId, idx);
      if (r !== null) out.push(r);
    });
    return out;
  }
  // W6 fallback: { lines: [{ turn, actor, text, timestampUtc }] }.
  if (typeof raw === 'object' && raw !== null) {
    const rec = raw as Record<string, unknown>;
    // W7 may also wrap the array under `{ records: [...] }` — pick
    // either field if present.
    const records = Array.isArray(rec.records)
      ? rec.records
      : Array.isArray(rec.lines)
        ? rec.lines
        : null;
    if (records !== null) {
      const out: CommentaryRecord[] = [];
      records.forEach((entry, idx) => {
        const r = coerceRecord(entry, gameId, idx);
        if (r !== null) out.push(r);
      });
      return out;
    }
  }
  return [];
}

function coerceRecord(raw: unknown, gameId: string, idx: number): CommentaryRecord | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const r = raw as Record<string, unknown>;
  const text = typeof r.text === 'string' ? r.text : '';
  if (text === '') return null;

  // W7 canonical fields.
  const turnNumber = pickNumber(r.turnNumber ?? r.turn, -1);
  const phase = pickPhase(r.phase);
  const speaker = pickSpeaker(r.speaker, pickNumber(r.actor, -1));
  const emotionIntensity = clamp01(pickNumber(r.emotionIntensity ?? r.intensity, 0));
  const tileReferences = pickTileReferences(r.tileReferences ?? r.tiles);
  const generatedAt = typeof r.generatedAt === 'string'
    ? r.generatedAt
    : typeof r.timestampUtc === 'string'
      ? r.timestampUtc
      : new Date(0).toISOString();
  const gameIdOut = typeof r.gameId === 'string' && r.gameId !== '' ? r.gameId : gameId;

  return {
    gameId: gameIdOut,
    turnNumber,
    phase,
    speaker,
    text,
    emotionIntensity,
    tileReferences,
    generatedAt,
  };
}

function pickNumber(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function clamp01(n: number): number {
  if (Number.isNaN(n)) return 0;
  if (n < 0) return 0;
  if (n > 1) return 1;
  return n;
}

function pickPhase(value: unknown): CommentaryPhase {
  if (
    value === 'draw' ||
    value === 'discard' ||
    value === 'claim' ||
    value === 'score' ||
    value === 'deal' ||
    value === 'narration'
  ) {
    return value;
  }
  return 'narration';
}

function pickSpeaker(value: unknown, actorFallback: number): CommentarySpeaker {
  if (value === 'pbp' || value === 'color' || value === 'analyst' || value === 'narrator') {
    return value;
  }
  // W6 fallback — actor seat 0..3 means a real player, no commentator
  // bot was attributed; default to PBP for in-play, narrator for -1.
  return actorFallback >= 0 ? 'pbp' : 'narrator';
}

/**
 * Phase K Wave 10 — Coerce a raw `tileReferences` field into the
 * canonical `TileReference[]` shape Bishop ships on the W10 wire.
 *
 * Accepts:
 *   • W10 canonical: `[{ tileId: 'man5', suit: 'man', rank: 5 }, ...]`
 *   • W9 strings:    `['man5', 'pin3', ...]`  (parsed via
 *                    `parseTileIdShape` for suit + rank inference)
 *   • Anything else: dropped silently (empty array).
 *
 * Documented in `docs/contracts/commentary-tile-ref.md §3`.
 */
function pickTileReferences(value: unknown): TileReference[] {
  if (!Array.isArray(value)) return [];
  const out: TileReference[] = [];
  for (const v of value) {
    if (typeof v === 'string' && v !== '') {
      out.push(parseTileIdShape(v));
      continue;
    }
    if (typeof v === 'object' && v !== null) {
      const obj = v as Record<string, unknown>;
      const tileId = typeof obj.tileId === 'string' && obj.tileId !== ''
        ? obj.tileId
        : '';
      if (tileId === '') continue;
      const suit = typeof obj.suit === 'string' ? obj.suit : parseTileIdShape(tileId).suit;
      const rank = typeof obj.rank === 'number' && Number.isFinite(obj.rank)
        ? Math.trunc(obj.rank)
        : parseTileIdShape(tileId).rank;
      out.push({ tileId, suit, rank });
    }
  }
  return out;
}

/**
 * Parse a wire-format tile id (`man5`, `pin3`, `sou9`, `east`,
 * `white`, etc.) into `{ tileId, suit, rank }`.  Mirrors the
 * accepted spellings in `world.findThingByFace` so the highlight
 * pulse remains in sync.  Unknown ids fall back to
 * `{ suit: 'unknown', rank: 0 }` — the 3D pulse will simply no-op.
 */
function parseTileIdShape(tileId: string): TileReference {
  const id = tileId.toLowerCase();
  const m = /^(man|pin|sou)([1-9])$/.exec(id);
  if (m !== null) return { tileId, suit: m[1], rank: Number(m[2]) };
  const r = /^([1-9])([mps])$/.exec(id);
  if (r !== null) {
    const suit = r[2] === 'm' ? 'man' : r[2] === 'p' ? 'pin' : 'sou';
    return { tileId, suit, rank: Number(r[1]) };
  }
  const honors: Record<string, number> = {
    east: 1, south: 2, west: 3, north: 4,
    white: 5, green: 6, red: 7,
    haku: 5, hatsu: 6, chun: 7,
  };
  if (id in honors) return { tileId, suit: 'honor', rank: honors[id] };
  return { tileId, suit: 'unknown', rank: 0 };
}

// Phase K Wave 6 → Wave 8 — Bracket renderer strategy.
//
// The Wave 1 / Wave 2 / Wave 4 tournaments panel rendered ONLY
// single-elimination brackets (as an interactive SVG) and parked the
// "round-robin / Swiss" rendering on a flat textual list (see
// `buildMatchesList` in `tournaments.ts`).  Wave 6 lifted the renderer
// into a strategy interface so a single host element can dispatch to
// the format-appropriate view:
//
//   • SingleElimRenderer — delegates back to `tournaments.ts`'s
//     existing `buildBracketSvg` so the visual stays identical to
//     Waves 4-5.  This module owns the dispatch wrapper only.
//   • SwissRenderer       — round-by-round W/L/D table.
//   • DoubleElimRenderer  — winners + losers brackets side-by-side
//     plus a highlighted grand-final card.
//
// Wave 8 extends the DoubleElimRenderer to Bishop's finalised wire
// shape: `{ winnersBracket, losersBracket, grandFinal: { match,
// resetMatch } }`.  The renderer now emits a dedicated losers-
// bracket column with `data-testid="losers-bracket-round-{n}"` on
// each round group, a grand-final row that spans both columns, and
// a conditional reset-match row keyed `data-testid="grand-final-
// reset"` that only shows when the losers-bracket champion beats
// the winners-bracket champion in the first grand final.  Live
// updates emit `data-testid="bracket-live-update"` so the panel
// repaint can be observed by the Playwright spec.
//
// Format detection is a substring match on `tournament.format`:
//
//   • contains 'double'                              → double-elim
//   • contains 'swiss'                               → swiss
//   • contains 'round-robin' / 'round_robin' / 'rr'  → round-robin (Swiss view)
//   • otherwise (single / bracket / elim / unknown)  → single-elim
//
// Testids exported (consumed by Vasquez):
//   • bracket-format-{format}              — wrapper, one of single-elim |
//     swiss | double-elim | round-robin
//   • bracket-round-{n}                    — per-round group (Swiss + winners
//     bracket; n is 1-based round number)
//   • losers-bracket-round-{n}             — NEW (W8) per-round group inside
//     the losers-bracket column
//   • bracket-double-elim-{winners|losers} — column wrappers
//   • tournament-bracket-svg               — preserved on the single-elim SVG
//   • tournament-grand-final               — double-elim grand-final card
//   • grand-final-reset                    — NEW (W8) reset-match row
//   • bracket-live-update                  — NEW (W8) emitted on each
//     re-render so SignalR-driven repaints are observable

import type { BracketMatch, BracketSlot, StandingsRow } from './tournaments';

export type BracketFormatKey = 'single-elim' | 'swiss' | 'double-elim' | 'round-robin';

export interface BracketRendererInput {
  format: string;
  matches: ReadonlyArray<BracketMatch>;
  standings: ReadonlyArray<StandingsRow>;
  /** Optional registered-players list for sparse Swiss rounds. */
  players?: ReadonlyArray<BracketSlot>;
  tournamentId: string;
  /** Renderer for the single-elim SVG; injected by `tournaments.ts`
   *  so this module stays unaware of the SVG-layout constants. */
  singleElimSvg: (input: BracketRendererInput) => SVGSVGElement;
  /**
   * Phase K Wave 8 — Bishop's finalised double-elim wire shape.
   * When present, the DoubleElimRenderer trusts the server-side
   * partition.  Phase K Wave 9 made this field the only supported
   * shape (legacy `matches[]` fallback removed; renderer surfaces
   * a `bracket-shape-error` testid when `layout` is null).  Phase
   * K Wave 10 removed the round-number-sign heuristic
   * (`partitionDoubleElim`) outright — see git history for the
   * pre-removal implementation.
   */
  layout?: DoubleElimLayout | null;
}

/**
 * Phase K Wave 8 — Server-authored double-elim partition.  Each
 * field is a normalised match array (winners-bracket round 1..N,
 * losers-bracket round 1..M).  `grandFinal.match` is the first
 * grand-final game; `grandFinal.resetMatch` is the bracket-reset
 * game that materialises only when the losers-bracket champion
 * wins the first grand final (and the winners-bracket champion is
 * forced to play a second one — "true" double elimination).
 */
export interface DoubleElimLayout {
  winnersBracket: ReadonlyArray<BracketMatch>;
  losersBracket: ReadonlyArray<BracketMatch>;
  grandFinal: {
    match: BracketMatch | null;
    resetMatch: BracketMatch | null;
  };
}

export interface BracketRenderer {
  readonly format: BracketFormatKey;
  render(input: BracketRendererInput): HTMLElement | SVGSVGElement;
}

/** Public dispatch entry point. */
export function pickBracketRenderer(format: string): BracketRenderer {
  const key = resolveFormatKey(format);
  switch (key) {
    case 'double-elim':  return new DoubleElimRenderer();
    case 'swiss':        return new SwissRenderer();
    case 'round-robin':  return new SwissRenderer('round-robin');
    case 'single-elim':
    default:             return new SingleElimRenderer();
  }
}

export function resolveFormatKey(format: string): BracketFormatKey {
  const f = (format ?? '').toLowerCase();
  if (f.includes('double')) return 'double-elim';
  if (f.includes('swiss')) return 'swiss';
  if (f.includes('round-robin') || f.includes('round_robin') || f === 'rr') return 'round-robin';
  return 'single-elim';
}

// ── Single-elim wrapper ─────────────────────────────────────────────

class SingleElimRenderer implements BracketRenderer {
  readonly format: BracketFormatKey = 'single-elim';
  render(input: BracketRendererInput): HTMLElement {
    const wrap = document.createElement('div');
    wrap.className = 'bracket-format-wrap bracket-format-single-elim';
    wrap.setAttribute('data-testid', 'bracket-format-single-elim');
    if (input.matches.length === 0) {
      wrap.appendChild(buildEmptyNotice('No matches yet — start the tournament to seed the bracket.'));
      return wrap;
    }
    wrap.appendChild(input.singleElimSvg(input));
    return wrap;
  }
}

// ── Swiss / round-robin renderer ────────────────────────────────────

class SwissRenderer implements BracketRenderer {
  readonly format: BracketFormatKey;
  constructor(format: BracketFormatKey = 'swiss') {
    this.format = format;
  }
  render(input: BracketRendererInput): HTMLElement {
    const wrap = document.createElement('div');
    wrap.className = `bracket-format-wrap bracket-format-${this.format}`;
    wrap.setAttribute('data-testid', `bracket-format-${this.format}`);
    if (input.matches.length === 0) {
      wrap.appendChild(buildEmptyNotice(
        this.format === 'round-robin'
          ? 'Round-robin schedule lands when the tournament starts.'
          : 'Swiss pairings appear once the first round is published.'));
      return wrap;
    }

    const byRound = groupByRound(input.matches);
    for (const [round, rows] of byRound) {
      const group = document.createElement('div');
      group.className = `bracket-${this.format}-round`;
      group.setAttribute('data-testid', `bracket-round-${round}`);
      group.setAttribute('data-round', String(round));

      const title = document.createElement('h5');
      title.className = `bracket-${this.format}-round-title`;
      title.textContent = `Round ${round}`;
      group.appendChild(title);

      const table = document.createElement('table');
      table.className = 'bracket-swiss-table';
      const head = document.createElement('thead');
      const hr = document.createElement('tr');
      for (const label of ['Player 1', 'Player 2', 'Result']) {
        const th = document.createElement('th');
        th.scope = 'col';
        th.textContent = label;
        hr.appendChild(th);
      }
      head.appendChild(hr);
      table.appendChild(head);

      const body = document.createElement('tbody');
      for (const m of rows) {
        const tr = document.createElement('tr');
        tr.className = 'bracket-swiss-row';
        tr.setAttribute('data-testid', `bracket-match-${m.round}-${m.matchIndex}`);
        tr.setAttribute('data-match-id', m.id);

        const td1 = document.createElement('td');
        td1.className = 'bracket-swiss-p1';
        td1.textContent = displayName(m.player1);
        tr.appendChild(td1);

        const td2 = document.createElement('td');
        td2.className = 'bracket-swiss-p2';
        td2.textContent = displayName(m.player2);
        tr.appendChild(td2);

        const tdR = document.createElement('td');
        tdR.className = `bracket-swiss-result bracket-swiss-result-${m.status}`;
        tdR.textContent = describeSwissResult(m);
        tr.appendChild(tdR);
        body.appendChild(tr);
      }
      table.appendChild(body);
      group.appendChild(table);
      wrap.appendChild(group);
    }
    return wrap;
  }
}

// ── Double-elim renderer ────────────────────────────────────────────

class DoubleElimRenderer implements BracketRenderer {
  readonly format: BracketFormatKey = 'double-elim';
  render(input: BracketRendererInput): HTMLElement {
    const wrap = document.createElement('div');
    wrap.className = 'bracket-format-wrap bracket-format-double-elim';
    wrap.setAttribute('data-testid', 'bracket-format-double-elim');

    // Phase K Wave 8 — `bracket-live-update` is a no-op marker
    // element observable by Playwright; it gets a unique key on
    // each re-render so a `TournamentBracketUpdated` SignalR push
    // can be detected (Vasquez's `losers-bracket-live-update` spec
    // mutation-observes its `data-update-id` attribute).
    const liveUpdate = document.createElement('div');
    liveUpdate.className = 'bracket-live-update';
    liveUpdate.setAttribute('data-testid', 'bracket-live-update');
    liveUpdate.setAttribute('data-update-id', String(Date.now()));
    liveUpdate.setAttribute('aria-hidden', 'true');
    wrap.appendChild(liveUpdate);

    if (input.matches.length === 0 && input.layout === undefined) {
      wrap.appendChild(buildEmptyNotice(
        'Double-elimination bracket appears once the tournament starts.'));
      return wrap;
    }

    // Phase K Wave 9 — Bishop's canonical wire shape is the ONLY
    // accepted source of truth.  The W6→W8 heuristic that scanned
    // round numbers (negative = losers) was kept as a transitional
    // fallback while Bishop's controller migrated; it tolerated
    // wire-shape drift in a way that hid real bugs.  W9 hard-
    // requires `input.layout` (a successfully normalised
    // `DoubleElimLayout` per `docs/contracts/bracket-api.md`).
    // When absent we surface a visible error tagged with
    // `data-testid="bracket-shape-error"` and log to the console
    // so QA / CI can flag the regression rather than silently
    // mis-rendering.
    const layoutInput = input.layout ?? null;
    if (layoutInput === null) {
      console.error(
        '[bracket] Unknown double-elim wire shape — expected '
        + '{ layout: { winnersBracket, losersBracket, grandFinal: '
        + '{ match, resetMatch } } } per docs/contracts/bracket-api.md',
      );
      const err = document.createElement('div');
      err.className = 'bracket-shape-error';
      err.setAttribute('data-testid', 'bracket-shape-error');
      err.setAttribute('role', 'alert');
      err.textContent = 'Bracket data is in an unrecognised format. '
        + 'Please refresh; if the problem persists, contact support.';
      wrap.appendChild(err);
      return wrap;
    }

    const partition = partitionForDoubleElim({ ...input, layout: layoutInput });
    if (partition.winners.length === 0
        && partition.losers.length === 0
        && partition.grandFinal === null
        && partition.resetMatch === null) {
      wrap.appendChild(buildEmptyNotice(
        'Double-elimination bracket appears once the tournament starts.'));
      return wrap;
    }

    const layout = document.createElement('div');
    layout.className = 'bracket-double-elim-layout';

    layout.appendChild(this.renderWinnersBracket(partition.winners));
    layout.appendChild(this.renderLosersBracket(partition.losers));

    const finals = this.renderGrandFinalRow(partition.grandFinal, partition.resetMatch);
    if (finals !== null) {
      layout.appendChild(finals);
    }
    wrap.appendChild(layout);
    return wrap;
  }

  /**
   * Phase K Wave 8 — Winners-bracket column.  Same shape as the
   * pre-W8 column helper kept for callers that want only one
   * side rendered (used by the future split-view spec).
   */
  renderWinnersBracket(matches: ReadonlyArray<BracketMatch>): HTMLDivElement {
    return buildBracketColumn('Winners bracket', 'winners', matches);
  }

  /**
   * Phase K Wave 8 — Losers-bracket column.  Each round group is
   * tagged `data-testid="losers-bracket-round-{n}"` (vs. the
   * winners' `bracket-round-{n}`) so Playwright specs can target
   * either side without ambiguity.
   */
  renderLosersBracket(matches: ReadonlyArray<BracketMatch>): HTMLDivElement {
    return buildBracketColumn('Losers bracket', 'losers', matches);
  }

  /**
   * Phase K Wave 8 — Grand-final row.  Spans both winners + losers
   * columns at the bottom of the layout (achieved via the
   * `bracket-double-elim-finals-row` CSS class which sets
   * `grid-column: 1 / -1` / `width: 100%` in the autotable
   * stylesheet).  The reset-match row only renders when (a) it
   * exists in the wire and (b) the losers-bracket champion won
   * the first grand final — i.e. the bracket is forced into a
   * second game to honour the "must lose twice" rule.
   *
   * Returns `null` when neither match is available.
   */
  renderGrandFinalRow(
    grandFinal: BracketMatch | null,
    resetMatch: BracketMatch | null,
  ): HTMLDivElement | null {
    if (grandFinal === null && resetMatch === null) return null;
    const wrap = document.createElement('div');
    wrap.className = 'bracket-double-elim-finals-row';

    if (grandFinal !== null) {
      const card = document.createElement('div');
      card.className = 'bracket-double-elim-grand-final';
      // Phase K Wave 8 — Vasquez's `losers-bracket-render.spec.ts`
      // and the live-update spec target the grand-final card by
      // `bracket-grand-final` (the W8 canonical name); the legacy
      // `tournament-grand-final` is kept as `data-testid-legacy`
      // so any pre-W8 fixture finds it via attribute selector
      // without breaking new specs.
      card.setAttribute('data-testid', 'bracket-grand-final');
      card.setAttribute('data-testid-legacy', 'tournament-grand-final');
      const title = document.createElement('h5');
      title.textContent = '🏆 Grand final';
      card.appendChild(title);

      const row = document.createElement('div');
      row.className = 'bracket-double-elim-grand-final-row';
      row.setAttribute('data-testid', 'bracket-match-grand-final');
      row.setAttribute('data-match-id', grandFinal.id);
      row.setAttribute('data-status', grandFinal.status);
      row.textContent = `${displayName(grandFinal.player1)} vs ${displayName(grandFinal.player2)} — ${describeSwissResult(grandFinal)}`;
      card.appendChild(row);
      wrap.appendChild(card);
    }

    // Reset-match row.  Only render when the bracket actually
    // needs a reset (the losers-bracket champion beat the
    // winners-bracket champion in `grandFinal`).  When the reset
    // match exists in the wire but the grand final isn't yet
    // complete OR the winners-bracket champion won outright, the
    // reset row stays hidden — it would confuse the viewer about
    // the bracket's actual state.
    if (shouldRenderResetMatch(grandFinal, resetMatch) && resetMatch !== null) {
      const reset = document.createElement('div');
      reset.className = 'bracket-double-elim-grand-final-reset';
      reset.setAttribute('data-testid', 'grand-final-reset');
      const title = document.createElement('h5');
      title.textContent = '↺ Reset match';
      reset.appendChild(title);

      const row = document.createElement('div');
      row.className = 'bracket-double-elim-grand-final-reset-row';
      row.setAttribute('data-testid', 'bracket-match-grand-final-reset');
      row.setAttribute('data-match-id', resetMatch.id);
      row.setAttribute('data-status', resetMatch.status);
      row.textContent = `${displayName(resetMatch.player1)} vs ${displayName(resetMatch.player2)} — ${describeSwissResult(resetMatch)}`;
      reset.appendChild(row);
      wrap.appendChild(reset);
    }
    return wrap;
  }
}

/**
 * Phase K Wave 8 — Decide whether the bracket-reset row should
 * render.  Three states matter:
 *
 *   1. The reset match doesn't exist on the wire — no row.
 *   2. The reset match exists but the grand final isn't complete
 *      yet — no row (the viewer hasn't earned the right to see
 *      it until the first final is decided).
 *   3. The reset match exists and the grand final is complete:
 *      render IFF the losers-bracket champion (`player2` by
 *      Bishop's convention) won.  This matches the wire — Bishop
 *      ships `resetMatch` populated only when the first grand
 *      final's winner is the losers-side player, but we belt-and-
 *      braces the check on the client too so a stale cache
 *      doesn't surface a row that should already be hidden.
 */
function shouldRenderResetMatch(
  grandFinal: BracketMatch | null,
  resetMatch: BracketMatch | null,
): boolean {
  if (resetMatch === null) return false;
  if (grandFinal === null) return false;
  // Pre-decided cases: when the reset match is already in-progress
  // or complete, render it regardless of who's winning the first
  // game (the bracket clearly entered the reset state).
  const resetStatus = (resetMatch.status ?? '').toLowerCase();
  if (resetStatus === 'in-progress' || resetStatus === 'complete') return true;

  const finalStatus = (grandFinal.status ?? '').toLowerCase();
  if (finalStatus !== 'complete') return false;
  // The losers-bracket champion sits in `player2` by Bishop's
  // ordering convention; if they won the first grand final, the
  // bracket resets.
  const winner = grandFinal.winnerPlayerId;
  if (winner === null || winner === '') return false;
  return winner === grandFinal.player2?.playerId;
}

interface PartitionResult {
  winners: BracketMatch[];
  losers: BracketMatch[];
  grandFinal: BracketMatch | null;
  resetMatch: BracketMatch | null;
}

/**
 * Phase K Wave 9 — Bishop's canonical `layout` is the only source
 * of truth.  Caller (`DoubleElimRenderer.render`) is responsible
 * for the hard-fail surface when `input.layout` is null; this
 * helper assumes it's non-null.  Phase K Wave 10 removed the W6
 * round-number-sign heuristic (`partitionDoubleElim`) that used
 * to live below — the canonical-only path is the production code
 * path now.
 */
function partitionForDoubleElim(input: BracketRendererInput): PartitionResult {
  const layout = input.layout ?? null;
  if (layout === null) {
    return { winners: [], losers: [], grandFinal: null, resetMatch: null };
  }
  return {
    winners: layout.winnersBracket.slice(),
    losers: layout.losersBracket.slice(),
    grandFinal: layout.grandFinal.match,
    resetMatch: layout.grandFinal.resetMatch,
  };
}

// ── Helpers ─────────────────────────────────────────────────────────

function buildEmptyNotice(text: string): HTMLDivElement {
  const div = document.createElement('div');
  div.className = 'tournament-bracket-empty';
  div.textContent = text;
  return div;
}

function buildBracketColumn(
  title: string,
  kind: 'winners' | 'losers',
  matches: ReadonlyArray<BracketMatch>,
): HTMLDivElement {
  const col = document.createElement('div');
  col.className = `bracket-double-elim-column bracket-double-elim-${kind}`;
  // Phase K Wave 8 — Primary testid is the W8 canonical name
  // (`losers-bracket` / `winners-bracket`) which Vasquez's
  // `losers-bracket-render.spec.ts` targets directly.  The W6
  // `bracket-double-elim-{kind}` name is documented but no test
  // code referenced it (search:
  // `grep bracket-double-elim-losers` returned only docs); the W8
  // selectors.md note documents the migration.
  col.setAttribute('data-testid', `${kind}-bracket`);
  col.setAttribute('data-double-elim-side', kind);

  const h = document.createElement('h5');
  h.textContent = title;
  col.appendChild(h);

  if (matches.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'bracket-double-elim-empty';
    empty.textContent = kind === 'winners'
      ? 'Winners bracket pairings pending.'
      : 'No eliminations yet.';
    col.appendChild(empty);
    return col;
  }

  // Phase K Wave 8 — Round testids diverge by side so Playwright
  // can disambiguate.  Winners keeps the legacy `bracket-round-{n}`
  // testid (carried over from W6); losers gets the new
  // `losers-bracket-round-{n}` testid.
  const roundTestidPrefix = kind === 'losers' ? 'losers-bracket-round-' : 'bracket-round-';

  const byRound = groupByRound(matches);
  for (const [round, rows] of byRound) {
    const group = document.createElement('div');
    group.className = `bracket-double-elim-round bracket-double-elim-round-${round}`;
    group.setAttribute('data-testid', `${roundTestidPrefix}${round}`);
    group.setAttribute('data-round', String(round));

    const title = document.createElement('div');
    title.className = 'bracket-double-elim-round-title';
    // Phase K Wave 8 — Round-label element also exposes the
    // bare-name testid (`losers-bracket-round` for losers,
    // `bracket-round` for winners) so a `getAllByTestId` assert in
    // Vasquez's spec returns the per-round count without needing
    // a numeric suffix scan.
    title.setAttribute('data-testid', kind === 'losers' ? 'losers-bracket-round' : 'bracket-round');
    title.textContent = `Round ${round}`;
    group.appendChild(title);

    const table = document.createElement('table');
    table.className = 'bracket-double-elim-table';
    const body = document.createElement('tbody');
    for (const m of rows) {
      const tr = document.createElement('tr');
      tr.className = 'bracket-double-elim-row';
      // Phase K Wave 8 — Primary testid is the W8 canonical name
      // `bracket-match` (Vasquez's live-update spec counts rows by
      // this name via `getAllByTestId`).  The match-specific
      // round/index identifier moves to data-* attributes so a
      // spec can still pick out a single row when needed.
      tr.setAttribute('data-testid', 'bracket-match');
      tr.setAttribute('data-match-round', String(m.round));
      tr.setAttribute('data-match-index', String(m.matchIndex));
      tr.setAttribute('data-match-id', m.id);
      const td1 = document.createElement('td');
      td1.textContent = displayName(m.player1);
      tr.appendChild(td1);
      const td2 = document.createElement('td');
      td2.textContent = displayName(m.player2);
      tr.appendChild(td2);
      const tdR = document.createElement('td');
      tdR.textContent = describeSwissResult(m);
      tdR.className = `bracket-match-${m.status}`;
      tr.appendChild(tdR);
      body.appendChild(tr);
    }
    table.appendChild(body);
    group.appendChild(table);
    col.appendChild(group);
  }
  return col;
}

function groupByRound(matches: ReadonlyArray<BracketMatch>): Map<number, BracketMatch[]> {
  const out = new Map<number, BracketMatch[]>();
  for (const m of matches) {
    const list = out.get(m.round) ?? [];
    list.push(m);
    out.set(m.round, list);
  }
  for (const list of out.values()) {
    list.sort((a, b) => a.matchIndex - b.matchIndex);
  }
  return new Map(Array.from(out.entries()).sort((a, b) => a[0] - b[0]));
}

function displayName(slot: BracketSlot | null): string {
  if (slot === null || slot === undefined) return 'TBD';
  const name = slot.displayName;
  return name === '' || name === undefined || name === null ? 'TBD' : name;
}

function describeSwissResult(m: BracketMatch): string {
  if (m.status === 'complete') {
    const s1 = m.score1 ?? null;
    const s2 = m.score2 ?? null;
    if (s1 !== null && s2 !== null) {
      if (s1 > s2) return `${displayName(m.player1)} won (${s1}-${s2})`;
      if (s2 > s1) return `${displayName(m.player2)} won (${s2}-${s1})`;
      return `Draw (${s1}-${s2})`;
    }
    if (m.winnerPlayerId !== null && m.winnerPlayerId !== '') {
      const winner = m.player1?.playerId === m.winnerPlayerId ? m.player1 : m.player2;
      return `${displayName(winner)} won`;
    }
    return 'Complete';
  }
  if (m.status === 'in-progress') return 'In progress';
  return 'Pending';
}

// Phase K Wave 10 — `partitionDoubleElim` removal.
//
// W6 shipped this round-number-sign + bracket-tag heuristic as the
// double-elim partitioner.  W7→W8 layered Bishop's canonical
// `layout` field on top so the renderer could trust the server-
// authored partition; W9 made the canonical `layout` the only
// supported shape (DoubleElimRenderer.render surfaces a
// `bracket-shape-error` testid when `layout` is null).  After
// three waves of zero production calls, the heuristic + its
// dedicated `PartitionedMatches` interface are removed outright
// in W10.  Documented in `docs/contracts/bracket-api.md
// §"Canonical wire shape"` + `docs/frontend-build-tooling.md §4`.
//
// If a future wave needs to revive the heuristic, it lives in git
// history at
// `f518196:src/frontend/autotable-src/src/bracket-renderer.ts:588`.

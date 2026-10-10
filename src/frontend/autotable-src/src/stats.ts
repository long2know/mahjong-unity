// Phase J Wave 5 — Stats display module.
//
// Tiny formatter that turns a PlayerStats payload into a DOM fragment.
// Two surfaces consume it:
//
//   • Lobby stats panel (`#lobby-stats-panel`) — shows the logged-in
//     player's running stats.
//   • Post-game modal "Your stats" section — shows how the just-completed
//     game affected the stats (delta from the pre-game snapshot stored
//     in profile.ts:snapshotStatsForGame()).

import type { PlayerStats } from './profile';

// data-testid contract — mirrors the brief's §Task 3 list.  Exposed as
// constants so the lobby + the end-of-game modal can both use the same
// strings without drift.
export const STATS_TESTIDS = {
  panel: 'stats-panel',
  gamesPlayed: 'stats-games-played',
  gamesWon: 'stats-games-won',
  winRate: 'stats-win-rate',
  longestStreak: 'stats-longest-streak',
  currentStreak: 'stats-current-streak',
  highestScore: 'stats-highest-score',
} as const;

interface StatRow {
  testid: string;
  label: string;
  value: string;
  // Optional delta string (e.g. "+1") rendered in a smaller secondary span.
  delta?: string | null;
  // True → render the delta in the positive-accent colour (green); false
  // → negative-accent colour (red); null → neutral grey.
  deltaSign?: 1 | -1 | 0 | null;
}

function formatWinRate(stats: PlayerStats): string {
  if (stats.gamesPlayed === 0) return '—';
  const pct = (stats.gamesWon / stats.gamesPlayed) * 100;
  // 1 decimal place keeps the value visually compact while still
  // surfacing small movements between rounds.
  return `${pct.toFixed(1)}%`;
}

function winRateDelta(prev: PlayerStats, next: PlayerStats): string | null {
  if (prev.gamesPlayed === 0 && next.gamesPlayed === 0) return null;
  const p = prev.gamesPlayed === 0 ? 0 : (prev.gamesWon / prev.gamesPlayed) * 100;
  const n = next.gamesPlayed === 0 ? 0 : (next.gamesWon / next.gamesPlayed) * 100;
  const d = n - p;
  if (Math.abs(d) < 0.05) return null;
  return d > 0 ? `+${d.toFixed(1)}pp` : `${d.toFixed(1)}pp`;
}

function sign(n: number): 1 | -1 | 0 {
  if (n > 0) return 1;
  if (n < 0) return -1;
  return 0;
}

function buildRows(stats: PlayerStats, prev: PlayerStats | null): Array<StatRow> {
  const rows: Array<StatRow> = [
    {
      testid: STATS_TESTIDS.gamesPlayed,
      label: 'Games played',
      value: String(stats.gamesPlayed),
      delta: prev === null ? null : deltaInt(stats.gamesPlayed - prev.gamesPlayed),
      deltaSign: prev === null ? null : sign(stats.gamesPlayed - prev.gamesPlayed),
    },
    {
      testid: STATS_TESTIDS.gamesWon,
      label: 'Games won',
      value: String(stats.gamesWon),
      delta: prev === null ? null : deltaInt(stats.gamesWon - prev.gamesWon),
      deltaSign: prev === null ? null : sign(stats.gamesWon - prev.gamesWon),
    },
    {
      testid: STATS_TESTIDS.winRate,
      label: 'Win rate',
      value: formatWinRate(stats),
      delta: prev === null ? null : winRateDelta(prev, stats),
      deltaSign: prev === null
        ? null
        : sign((stats.gamesPlayed === 0 ? 0 : stats.gamesWon / stats.gamesPlayed) -
               (prev.gamesPlayed === 0 ? 0 : prev.gamesWon / prev.gamesPlayed)),
    },
    {
      testid: STATS_TESTIDS.longestStreak,
      label: 'Longest streak',
      value: String(stats.longestStreak),
      delta: prev === null ? null : deltaInt(stats.longestStreak - prev.longestStreak),
      deltaSign: prev === null ? null : sign(stats.longestStreak - prev.longestStreak),
    },
    {
      testid: STATS_TESTIDS.currentStreak,
      label: 'Current streak',
      value: String(stats.currentStreak),
      delta: prev === null ? null : deltaInt(stats.currentStreak - prev.currentStreak),
      deltaSign: prev === null ? null : sign(stats.currentStreak - prev.currentStreak),
    },
    {
      testid: STATS_TESTIDS.highestScore,
      label: 'Highest score',
      value: String(stats.highestScore),
      delta: prev === null ? null : deltaInt(stats.highestScore - prev.highestScore),
      deltaSign: prev === null ? null : sign(stats.highestScore - prev.highestScore),
    },
  ];
  return rows;
}

function deltaInt(n: number): string | null {
  if (n === 0) return null;
  return n > 0 ? `+${n}` : String(n);
}

/**
 * Build a DOM fragment that renders the stats grid.  When `prev` is
 * provided each row is annotated with a "delta" badge showing the
 * change since `prev` — used by the post-game modal.  When omitted the
 * grid is a plain readout (lobby panel).
 *
 * The container itself carries the `stats-panel` testid so callers
 * don't have to wrap it.  Caller is expected to attach the returned
 * fragment to a host element and is free to set its own additional
 * classes via `wrapperClass`.
 */
export function formatStats(
  stats: PlayerStats,
  prev: PlayerStats | null = null,
  wrapperClass: string = '',
): DocumentFragment {
  const frag = document.createDocumentFragment();

  const grid = document.createElement('div');
  grid.className = `stats-grid${wrapperClass !== '' ? ' ' + wrapperClass : ''}`;
  grid.setAttribute('data-testid', STATS_TESTIDS.panel);
  grid.setAttribute('role', 'list');

  for (const row of buildRows(stats, prev)) {
    grid.appendChild(buildRow(row));
  }
  frag.appendChild(grid);
  return frag;
}

function buildRow(row: StatRow): HTMLElement {
  const cell = document.createElement('div');
  cell.className = 'stats-cell';
  cell.setAttribute('role', 'listitem');

  const label = document.createElement('div');
  label.className = 'stats-cell-label';
  label.textContent = row.label;

  const value = document.createElement('div');
  value.className = 'stats-cell-value';
  value.setAttribute('data-testid', row.testid);
  value.textContent = row.value;

  cell.appendChild(label);
  cell.appendChild(value);

  if (row.delta !== undefined && row.delta !== null) {
    const delta = document.createElement('div');
    delta.className = 'stats-cell-delta';
    if (row.deltaSign === 1) delta.classList.add('stats-cell-delta-pos');
    else if (row.deltaSign === -1) delta.classList.add('stats-cell-delta-neg');
    else delta.classList.add('stats-cell-delta-neutral');
    delta.textContent = row.delta;
    delta.setAttribute('aria-label', `${row.label} change: ${row.delta}`);
    cell.appendChild(delta);
  }

  return cell;
}

/**
 * Build the post-game "Your stats" delta panel.  Renders a heading + a
 * stats grid where each row carries a delta badge sourced from `prev`.
 * Returns null when no prev snapshot is available (a brand-new tab that
 * never finished a prior game).
 */
export function formatStatsDelta(
  stats: PlayerStats,
  prev: PlayerStats | null,
): DocumentFragment | null {
  if (prev === null) return null;
  const frag = document.createDocumentFragment();

  const section = document.createElement('div');
  section.className = 'game-complete-section stats-delta-section';

  const title = document.createElement('h4');
  title.className = 'game-complete-section-title';
  title.textContent = 'Your stats';
  section.appendChild(title);

  section.appendChild(formatStats(stats, prev, 'stats-grid-delta'));
  frag.appendChild(section);
  return frag;
}

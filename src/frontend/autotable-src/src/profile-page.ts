// Phase J Wave 7 — Player profile page.
//
// A dedicated overlay page (`#profile-page`) that surfaces the local
// player's full stats + recent games + editable display name / avatar
// colour.  The post-Wave-5 `#profile-drawer` was a *slide-out* surface
// optimised for the lobby; the Wave-7 profile *page* is a full overlay
// with a stats grid and a "Recent games" list that links into the
// replay viewer.
//
// Opens from:
//   • Click on the lobby's profile chip (`#lobby-open-profile`) when the
//     Wave-7 surface is mounted — we intercept the click before the
//     drawer handler runs.
//
// Data sources:
//   • `profile.ts` — display name, avatar colour, career stats.  We
//     subscribe to `onProfile` so the page re-renders on every update.
//   • `/api/players/{playerId}/games?limit=10` — recent games.  Best
//     effort: when the endpoint is unavailable we render a "No recent
//     games" placeholder instead of failing the page.
//
// Replay link: each row has a "Watch replay" link that opens the
// replay viewer with the row's `gameId`.  The viewer feature-checks
// `GET /api/games/{gameId}/replay` and falls back to in-memory
// playback when the endpoint 404s.

import {
  AVATAR_COLOR_PRESETS,
  getProfile,
  onProfile,
  setAvatarColor,
  setDisplayName,
  validateAvatarColor,
  validateDisplayName,
  type PlayerProfile,
} from './profile';
import { openReplayForGame } from './replay-launcher';
import { setElHidden } from './dom-utils';

// ── Public types ────────────────────────────────────────────────────

export interface RecentGame {
  gameId: string;
  finishedAt: string | null;
  result: 'win' | 'loss' | 'draw' | 'unknown';
  finalScore: number;
  /** Optional friendly label e.g. "Hand 4 / 4 — Won 🏆". */
  summary: string;
}

// ── Constants ───────────────────────────────────────────────────────

export const RECENT_GAMES_LIMIT = 10;

// ── State ───────────────────────────────────────────────────────────

let installed = false;
let recentGamesCache: RecentGame[] = [];
let recentGamesLoading = false;
let recentGamesError: string | null = null;

// Phase J Wave 7 — when viewing another player's profile (from the
// leaderboard "View" button), we stash their identity / colour /
// avatar locally and disable the editors.  Cleared on close.
interface RemoteProfile {
  playerId: string;
  displayName: string;
  avatarColor: string;
}
let viewingRemote: RemoteProfile | null = null;

// ── DOM helpers ────────────────────────────────────────────────────

function isOpen(): boolean {
  const page = document.getElementById('profile-page');
  return page !== null && page.classList.contains('profile-page-open');
}

export function openProfilePage(): void {
  const page = document.getElementById('profile-page');
  if (page === null) return;
  page.classList.add('profile-page-open');
  page.setAttribute('aria-hidden', 'false');
  rerenderPage();
  void refreshRecentGames();
  // Focus the close button so screen readers / keyboard users land
  // somewhere sensible.
  window.setTimeout(() => {
    const closeBtn = document.getElementById('profile-page-close');
    closeBtn?.focus();
  }, 50);
}

// Phase J Wave 7 — open the profile page in read-only "viewing" mode
// for a different player.  Fetches their recent games via the same
// endpoint as the own-profile path.
export function openProfilePageFor(remote: RemoteProfile): void {
  viewingRemote = remote;
  openProfilePage();
}

export function closeProfilePage(): void {
  const page = document.getElementById('profile-page');
  if (page === null) return;
  page.classList.remove('profile-page-open');
  page.setAttribute('aria-hidden', 'true');
  // Drop any remote-profile session so re-opening lands on the own
  // profile.
  viewingRemote = null;
}

// ── Install ────────────────────────────────────────────────────────

export function installProfilePage(): void {
  if (installed) return;
  const page = document.getElementById('profile-page');
  if (page === null) return;
  installed = true;

  const closeBtn = document.getElementById('profile-page-close');
  if (closeBtn !== null) {
    closeBtn.addEventListener('click', () => closeProfilePage());
  }

  // Lobby avatar chip opens the profile page.  We add a capture-phase
  // listener so we run *before* the Wave-5 drawer toggle, then call
  // stopImmediatePropagation to suppress that handler.
  const lobbyChip = document.getElementById('lobby-open-profile');
  if (lobbyChip !== null) {
    lobbyChip.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopImmediatePropagation();
      openProfilePage();
    }, true);
  }

  // Phase J Wave 7 — leaderboard rows raise this custom event when the
  // user clicks the "View" button; we use it to open the profile page
  // in read-only mode for the chosen player.
  window.addEventListener('mahjong:open-profile-page', ((e: Event) => {
    const detail = (e as CustomEvent).detail as Partial<RemoteProfile> | undefined;
    if (!detail || typeof detail.playerId !== 'string' || detail.playerId === '') {
      openProfilePage();
      return;
    }
    openProfilePageFor({
      playerId: detail.playerId,
      displayName: typeof detail.displayName === 'string' ? detail.displayName : 'Player',
      avatarColor: typeof detail.avatarColor === 'string' ? detail.avatarColor : '#2980b9',
    });
  }) as EventListener);

  // Escape closes when open.
  document.addEventListener('keydown', (e) => {
    if (!isOpen()) return;
    if (e.key === 'Escape') {
      closeProfilePage();
      const chip = document.getElementById('lobby-open-profile') as HTMLButtonElement | null;
      chip?.focus();
    }
  });

  // Re-render when the profile or recent-games cache changes.
  onProfile(() => rerenderPage());

  // Initial paint with whatever cache we have.
  rerenderPage();
}

// ── Page renderer ──────────────────────────────────────────────────

function rerenderPage(): void {
  renderHeader();
  renderStatsGrid();
  renderEditors();
  renderRecentGames();
}

function renderHeader(): void {
  const profile = getProfile();
  const nameEl = document.getElementById('profile-page-name');
  const avatarEl = document.getElementById('profile-page-avatar') as HTMLElement | null;
  const memberEl = document.getElementById('profile-page-member-since');
  const displayName = viewingRemote?.displayName ?? profile?.displayName ?? 'Guest';
  const avatarColor = viewingRemote?.avatarColor ?? profile?.avatarColor ?? '#2980b9';
  if (nameEl !== null) {
    nameEl.textContent = displayName;
  }
  if (avatarEl !== null) {
    avatarEl.style.backgroundColor = avatarColor;
    avatarEl.textContent = initialsFromName(displayName);
  }
  if (memberEl !== null) {
    memberEl.textContent = viewingRemote !== null
      ? 'Viewing public profile'
      : formatMemberSince();
  }
}

function renderStatsGrid(): void {
  const host = document.getElementById('profile-stats-grid');
  if (host === null) return;
  // Phase J Wave 7 — when viewing a remote profile we have no local
  // stats DTO; render the section with em-dashes so the card layout
  // stays consistent.
  if (viewingRemote !== null) {
    host.replaceChildren();
    host.appendChild(buildStatCard('profile-stats-played', 'Games played', '—'));
    host.appendChild(buildStatCard('profile-stats-won', 'Games won', '—'));
    host.appendChild(buildStatCard('profile-stats-winrate', 'Win rate', '—'));
    host.appendChild(buildStatCard('profile-stats-total', 'Total score', '—'));
    host.appendChild(buildStatCard('profile-stats-highest', 'Highest hand', '—'));
    host.appendChild(buildStatCard('profile-stats-streak', 'Longest streak', '—'));
    return;
  }
  const profile = getProfile();
  const stats = profile?.stats ?? {
    gamesPlayed: 0, gamesWon: 0, longestStreak: 0, currentStreak: 0, highestScore: 0,
  };
  const winRate = stats.gamesPlayed > 0
    ? (stats.gamesWon / stats.gamesPlayed) * 100
    : 0;
  // Cache the total-score sum if profile carries it; otherwise we fall
  // back to "—" because Bishop's stats DTO doesn't carry a running total
  // separate from the career sum.  We synthesise it from `highestScore`
  // when available so the surface has *something* to render.
  const totalScore = inferTotalScore(profile);

  host.replaceChildren();
  host.appendChild(buildStatCard(
    'profile-stats-played', 'Games played', String(stats.gamesPlayed)));
  host.appendChild(buildStatCard(
    'profile-stats-won', 'Games won', String(stats.gamesWon)));
  host.appendChild(buildStatCard(
    'profile-stats-winrate', 'Win rate',
    stats.gamesPlayed > 0 ? `${winRate.toFixed(1)}%` : '—'));
  host.appendChild(buildStatCard(
    'profile-stats-total', 'Total score',
    totalScore === null ? '—' : formatSigned(totalScore)));
  host.appendChild(buildStatCard(
    'profile-stats-highest', 'Highest hand', formatSigned(stats.highestScore)));
  host.appendChild(buildStatCard(
    'profile-stats-streak', 'Longest streak', String(stats.longestStreak)));
}

function inferTotalScore(profile: PlayerProfile | null): number | null {
  // Bishop's stats DTO doesn't expose a running total — only
  // `highestSingleGameScore` + per-row WS deltas.  We expose null so
  // the surface renders "—" rather than misleading data; the
  // leaderboard endpoint is where the real career total lives.
  if (profile === null) return null;
  // If the profile carries an explicit totalScore field (future-proof
  // for a backend extension) we surface it.  Otherwise null.
  const o = profile as unknown as Record<string, unknown>;
  if (typeof o.totalScore === 'number') return o.totalScore;
  return null;
}

function buildStatCard(testid: string, label: string, value: string): HTMLDivElement {
  const card = document.createElement('div');
  card.className = 'profile-stat-card';
  card.setAttribute('data-testid', testid);
  const labelEl = document.createElement('div');
  labelEl.className = 'profile-stat-label';
  labelEl.textContent = label;
  const valueEl = document.createElement('div');
  valueEl.className = 'profile-stat-value';
  valueEl.textContent = value;
  card.appendChild(labelEl);
  card.appendChild(valueEl);
  return card;
}

function renderEditors(): void {
  const nameInput = document.getElementById('profile-page-display-name-input') as HTMLInputElement | null;
  const colorPresets = document.getElementById('profile-page-color-presets');
  const customColor = document.getElementById('profile-page-color-custom') as HTMLInputElement | null;
  const profile = getProfile();
  // Phase J Wave 7 — hide the edit section entirely when viewing
  // another player's profile (read-only mode).
  const editSection = nameInput?.closest('.profile-page-section') as HTMLElement | null;
  if (editSection !== null) {
    setElHidden(editSection, viewingRemote !== null);
  }
  if (viewingRemote !== null) return;
  if (nameInput !== null && document.activeElement !== nameInput) {
    nameInput.value = profile?.displayName ?? '';
  }
  if (customColor !== null && document.activeElement !== customColor) {
    customColor.value = profile?.avatarColor ?? '#2980b9';
  }
  if (colorPresets !== null && colorPresets.children.length === 0) {
    AVATAR_COLOR_PRESETS.forEach((hex, i) => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'profile-page-color-swatch';
      btn.style.backgroundColor = hex;
      btn.setAttribute('data-color', hex);
      btn.setAttribute('data-testid', `profile-page-color-${i}`);
      btn.setAttribute('aria-label', `Avatar colour ${i + 1}`);
      btn.setAttribute('role', 'radio');
      btn.setAttribute('aria-checked', 'false');
      btn.title = hex;
      btn.addEventListener('click', () => {
        setAvatarColor(hex);
      });
      colorPresets.appendChild(btn);
    });
  }
  if (colorPresets !== null) {
    const selected = (profile?.avatarColor ?? '').toLowerCase();
    for (const btn of colorPresets.querySelectorAll<HTMLButtonElement>('.profile-page-color-swatch')) {
      const matches = btn.getAttribute('data-color')?.toLowerCase() === selected;
      btn.classList.toggle('profile-page-color-swatch-selected', matches);
      btn.setAttribute('aria-checked', matches ? 'true' : 'false');
    }
  }
  // Wire the inputs once.
  if (nameInput !== null && nameInput.getAttribute('data-wired') !== 'true') {
    nameInput.setAttribute('data-wired', 'true');
    const errorEl = document.getElementById('profile-page-display-name-error');
    nameInput.addEventListener('input', () => {
      const { value, error } = validateDisplayName(nameInput.value);
      if (errorEl !== null) errorEl.textContent = error ?? '';
      nameInput.classList.toggle('profile-page-input-invalid', error !== null);
      if (error === null && value !== null) {
        setDisplayName(value);
      }
    });
  }
  if (customColor !== null && customColor.getAttribute('data-wired') !== 'true') {
    customColor.setAttribute('data-wired', 'true');
    customColor.addEventListener('input', () => {
      if (validateAvatarColor(customColor.value)) {
        setAvatarColor(customColor.value);
      }
    });
  }
}

function renderRecentGames(): void {
  const host = document.getElementById('profile-recent-games');
  const errorEl = document.getElementById('profile-recent-games-error');
  const loadingEl = document.getElementById('profile-recent-games-loading');
  if (host === null) return;
  if (loadingEl !== null) {
    setElHidden(loadingEl, !recentGamesLoading);
  }
  if (errorEl !== null) {
    if (recentGamesError !== null) {
      setElHidden(errorEl, false);
      errorEl.textContent = recentGamesError;
    } else {
      setElHidden(errorEl, true);
      errorEl.textContent = '';
    }
  }
  host.replaceChildren();
  if (recentGamesCache.length === 0 && !recentGamesLoading && recentGamesError === null) {
    // Phase J Wave 7 a11y — when the list is empty drop the
    // `role="list"` so axe's `aria-required-children` rule passes
    // (a list MUST contain ≥1 listitem).  We restore it when items
    // arrive below.
    host.removeAttribute('role');
    const empty = document.createElement('div');
    empty.className = 'profile-recent-empty';
    empty.textContent = 'No recent games yet — start a match to see your replays here.';
    host.appendChild(empty);
    return;
  }
  host.setAttribute('role', 'list');
  recentGamesCache.forEach((game, i) => {
    if (i >= RECENT_GAMES_LIMIT) return;
    host.appendChild(buildRecentGameRow(game, i));
  });
}

function buildRecentGameRow(game: RecentGame, index: number): HTMLDivElement {
  const row = document.createElement('div');
  row.className = `profile-recent-row profile-recent-row-${game.result}`;
  row.setAttribute('data-testid', `profile-recent-game-${index}`);
  row.setAttribute('data-game-id', game.gameId);

  const label = document.createElement('div');
  label.className = 'profile-recent-label';
  label.setAttribute('data-testid', `profile-recent-label-${index}`);
  label.textContent = game.summary;
  row.appendChild(label);

  const score = document.createElement('div');
  score.className = 'profile-recent-score';
  score.textContent = formatSigned(game.finalScore);
  row.appendChild(score);

  const meta = document.createElement('div');
  meta.className = 'profile-recent-meta';
  meta.textContent = formatRelativeTime(game.finishedAt);
  row.appendChild(meta);

  const action = document.createElement('button');
  action.type = 'button';
  action.className = 'btn btn-sm btn-info profile-recent-replay';
  action.setAttribute('data-testid', `profile-recent-replay-${index}`);
  action.textContent = '🎞 Watch replay';
  action.setAttribute('aria-label', `Watch replay for ${game.summary}`);
  action.addEventListener('click', () => {
    closeProfilePage();
    void openReplayForGame(game.gameId);
  });
  row.appendChild(action);

  return row;
}

// ── Recent games fetch ─────────────────────────────────────────────

async function refreshRecentGames(): Promise<void> {
  const profile = getProfile();
  const targetPlayerId = viewingRemote?.playerId
    ?? (profile?.playerId !== '' && profile?.playerId !== 'offline' ? profile?.playerId : null);
  if (targetPlayerId === null || targetPlayerId === undefined || targetPlayerId === '') {
    recentGamesCache = [];
    recentGamesError = null;
    renderRecentGames();
    return;
  }
  recentGamesLoading = true;
  recentGamesError = null;
  renderRecentGames();
  try {
    const url = `/api/players/${encodeURIComponent(targetPlayerId)}/games?limit=${RECENT_GAMES_LIMIT}`;
    const resp = await fetch(url, {
      credentials: 'same-origin',
      headers: { 'Accept': 'application/json' },
    });
    if (resp.status === 404) {
      // Endpoint not implemented yet — gracefully show "no recent
      // games" rather than treating as an error.
      recentGamesCache = [];
      recentGamesLoading = false;
      recentGamesError = null;
      renderRecentGames();
      return;
    }
    if (!resp.ok) {
      throw new Error(`HTTP ${resp.status}`);
    }
    const body = (await resp.json()) as unknown;
    recentGamesCache = normalizeRecentGames(body);
    recentGamesError = null;
  } catch (e) {
    recentGamesError = null; // Don't surface network errors as red text — keep the UI quiet.
    recentGamesCache = [];
  } finally {
    recentGamesLoading = false;
    renderRecentGames();
  }
}

function normalizeRecentGames(raw: unknown): RecentGame[] {
  const out: RecentGame[] = [];
  const rows = Array.isArray(raw)
    ? raw
    : (raw !== null && typeof raw === 'object' && Array.isArray((raw as { games?: unknown[] }).games))
      ? (raw as { games: unknown[] }).games
      : [];
  for (const r of rows) {
    if (r === null || typeof r !== 'object') continue;
    const o = r as Record<string, unknown>;
    const gameId = typeof o.gameId === 'string' && o.gameId !== ''
      ? o.gameId
      : (typeof o.id === 'string' ? o.id : '');
    if (gameId === '') continue;
    const finishedAt = typeof o.finishedAt === 'string'
      ? o.finishedAt
      : (typeof o.completedAt === 'string' ? o.completedAt : null);
    const result = (() => {
      const r = typeof o.result === 'string' ? o.result.toLowerCase() : '';
      if (r === 'win' || r === 'won') return 'win' as const;
      if (r === 'loss' || r === 'lost') return 'loss' as const;
      if (r === 'draw' || r === 'washout') return 'draw' as const;
      return 'unknown' as const;
    })();
    const finalScore = typeof o.finalScore === 'number'
      ? o.finalScore
      : (typeof o.score === 'number' ? o.score : 0);
    const summary = typeof o.summary === 'string' && o.summary !== ''
      ? o.summary
      : (typeof o.label === 'string' && o.label !== ''
        ? o.label
        : `Game ${gameId.slice(0, 6)}`);
    out.push({ gameId, finishedAt, result, finalScore, summary });
  }
  return out;
}

// ── Formatters ────────────────────────────────────────────────────

function initialsFromName(name: string): string {
  const trimmed = name.trim();
  if (trimmed === '') return '?';
  const parts = trimmed.split(/\s+/);
  if (parts.length === 1) return parts[0].charAt(0).toUpperCase();
  return (parts[0].charAt(0) + parts[parts.length - 1].charAt(0)).toUpperCase();
}

function formatSigned(n: number): string {
  if (!isFinite(n)) return '—';
  const r = Math.round(n);
  if (r > 0) return `+${r.toLocaleString()}`;
  return r.toLocaleString();
}

function formatRelativeTime(iso: string | null): string {
  if (iso === null || iso === '') return '';
  const t = Date.parse(iso);
  if (isNaN(t)) return iso;
  const now = Date.now();
  const delta = Math.max(0, now - t);
  const sec = Math.round(delta / 1000);
  if (sec < 60) return `${sec} s ago`;
  const min = Math.round(sec / 60);
  if (min < 60) return `${min} min ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr} h ago`;
  const day = Math.round(hr / 24);
  if (day < 7) return `${day} d ago`;
  return new Date(t).toLocaleDateString();
}

function formatMemberSince(): string {
  try {
    const raw = window.localStorage.getItem('mahjong.identity.cache.v1');
    if (raw === null) return 'New member';
    const j = JSON.parse(raw) as { createdAt?: string };
    if (typeof j.createdAt === 'string') {
      const t = Date.parse(j.createdAt);
      if (!isNaN(t)) {
        return `Member since ${new Date(t).toLocaleDateString()}`;
      }
    }
  } catch { /* ignore */ }
  return 'New member';
}

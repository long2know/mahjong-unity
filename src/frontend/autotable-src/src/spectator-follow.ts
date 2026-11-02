// Phase J Wave 8 — Spectator follow-seat helper.
//
// Spectators (URL `?seat=-1`) start with the top-down camera that lives
// at `world.seat === null`.  This module adds:
//
//   • A floating "Spectator" panel (visible only when
//     `document.body.classList.contains('spectator')`) with four buttons
//     labelled 1-4 plus "Top-down".  Clicking a seat button overrides
//     `world.seat = 0..3`, which switches the camera to that seat's POV.
//   • Keyboard shortcuts 1 / 2 / 3 / 4 (follow seat) and 0 / ESC
//     (return to top-down).
//   • A "Show all hands" toggle that flips `document.body.classList`
//     `spectator-show-all`.  CSS in style.css reveals the other players'
//     concealed tiles when this class is present.  The toggle is a
//     best-effort visual hint — the canonical tile reveal lives on the
//     backend; if Bishop hasn't deployed the spectator reveal
//     permission yet the local CSS is the only effect.
//
// The module is intentionally framework-free and never imports
// `world.ts` directly — instead it pokes at `(window as any).game.world`
// which `index.ts` already exposes for debugging.  This keeps the
// helper outside the asset-load critical path so it can install in
// lobby.ts:initLobby() before the 3D Game has booted.
//
// LS keys (none persistent — follow-seat is per-session state).

import { readSpectatorFromUrl } from './client-ui';

let installed = false;
let followedSeat: number | null = null;

interface WorldLike { seat: number | null; }
interface GameLike { world?: WorldLike; }

function getWorld(): WorldLike | null {
  try {
    const g = (window as unknown as { game?: GameLike }).game;
    if (g === undefined || g === null) return null;
    return g.world ?? null;
  } catch {
    return null;
  }
}

function setFollowSeat(seat: number | null): void {
  followedSeat = seat;
  const world = getWorld();
  if (world !== null) {
    world.seat = seat;
  }
  // Repaint the panel buttons' active state.
  paintActiveStates();
}

function paintActiveStates(): void {
  const buttons = document.querySelectorAll<HTMLButtonElement>('[data-spectator-follow-seat]');
  for (const btn of Array.from(buttons)) {
    const raw = btn.getAttribute('data-spectator-follow-seat');
    if (raw === null) continue;
    const matches = raw === 'null'
      ? followedSeat === null
      : followedSeat !== null && String(followedSeat) === raw;
    btn.classList.toggle('active', matches);
    btn.setAttribute('aria-pressed', matches ? 'true' : 'false');
  }
}

function applyShowAll(showAll: boolean): void {
  document.body.classList.toggle('spectator-show-all', showAll);
  const toggle = document.querySelector<HTMLInputElement>(
    '[data-testid="spectator-show-all-toggle"]');
  if (toggle !== null) toggle.checked = showAll;
}

function buildPanel(): void {
  if (document.getElementById('spectator-follow-panel') !== null) return;
  const panel = document.createElement('div');
  panel.id = 'spectator-follow-panel';
  panel.className = 'spectator-follow-panel';
  panel.setAttribute('data-testid', 'spectator-follow-panel');
  panel.setAttribute('role', 'region');
  panel.setAttribute('aria-label', 'Spectator controls');

  const heading = document.createElement('div');
  heading.className = 'spectator-follow-heading';
  heading.textContent = 'Follow seat';
  panel.appendChild(heading);

  const buttonRow = document.createElement('div');
  buttonRow.className = 'spectator-follow-buttons';
  for (const slot of [
    { seat: 0, label: 'Seat 1', testid: 'spectator-follow-seat-0', key: '1' },
    { seat: 1, label: 'Seat 2', testid: 'spectator-follow-seat-1', key: '2' },
    { seat: 2, label: 'Seat 3', testid: 'spectator-follow-seat-2', key: '3' },
    { seat: 3, label: 'Seat 4', testid: 'spectator-follow-seat-3', key: '4' },
  ]) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-secondary btn-sm spectator-follow-button';
    btn.textContent = slot.label;
    btn.title = `Follow ${slot.label} POV (${slot.key})`;
    btn.setAttribute('data-spectator-follow-seat', String(slot.seat));
    btn.setAttribute('data-testid', slot.testid);
    btn.setAttribute('aria-label', `Follow ${slot.label}`);
    btn.addEventListener('click', () => setFollowSeat(slot.seat));
    buttonRow.appendChild(btn);
  }
  const topBtn = document.createElement('button');
  topBtn.type = 'button';
  topBtn.className = 'btn btn-secondary btn-sm spectator-follow-button';
  topBtn.textContent = 'Top-down';
  topBtn.title = 'Top-down (0 / Esc)';
  topBtn.setAttribute('data-spectator-follow-seat', 'null');
  topBtn.setAttribute('data-testid', 'spectator-follow-topdown');
  topBtn.setAttribute('aria-label', 'Top-down camera');
  topBtn.addEventListener('click', () => setFollowSeat(null));
  buttonRow.appendChild(topBtn);
  panel.appendChild(buttonRow);

  // Show-all toggle.
  const showAllRow = document.createElement('label');
  showAllRow.className = 'spectator-follow-toggle-row';
  const showAllInput = document.createElement('input');
  showAllInput.type = 'checkbox';
  showAllInput.setAttribute('data-testid', 'spectator-show-all-toggle');
  showAllInput.setAttribute('aria-label', 'Show all hands');
  showAllInput.addEventListener('change', () => applyShowAll(showAllInput.checked));
  const showAllSpan = document.createElement('span');
  showAllSpan.textContent = 'Show all hands';
  const showAllHint = document.createElement('span');
  showAllHint.className = 'spectator-follow-hint';
  showAllHint.textContent = '(local hint — backend reveal may not be enabled)';
  showAllRow.appendChild(showAllInput);
  showAllRow.appendChild(showAllSpan);
  showAllRow.appendChild(showAllHint);
  panel.appendChild(showAllRow);

  document.body.appendChild(panel);
  paintActiveStates();
}

function onKeydown(ev: KeyboardEvent): void {
  if (!readSpectatorFromUrl()) return;
  // Ignore when typing in inputs / contenteditable.
  const target = ev.target as HTMLElement | null;
  if (target !== null) {
    const tag = target.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
        || target.isContentEditable) {
      return;
    }
  }
  if (ev.altKey || ev.ctrlKey || ev.metaKey) return;
  switch (ev.key) {
    case '1': setFollowSeat(0); ev.preventDefault(); break;
    case '2': setFollowSeat(1); ev.preventDefault(); break;
    case '3': setFollowSeat(2); ev.preventDefault(); break;
    case '4': setFollowSeat(3); ev.preventDefault(); break;
    case '0':
    case 'Escape':
      setFollowSeat(null); ev.preventDefault(); break;
    default: break;
  }
}

/** Install the spectator follow panel + keyboard shortcuts.  Idempotent. */
export function installSpectatorFollow(): void {
  if (installed) return;
  installed = true;
  // Only spectators get the panel + key bindings.
  if (!readSpectatorFromUrl()) return;
  buildPanel();
  document.addEventListener('keydown', onKeydown);
}

/** Current follow-seat (null = top-down). */
export function getFollowedSeat(): number | null {
  return followedSeat;
}

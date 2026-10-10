// Ferro — Claim-window countdown overlay (new self-contained module).
//
// Renders a fixed-position translucent bar at the bottom of the
// canvas while a claim window is open on the local seat.  The bar
// surfaces:
//
//   • The claimable tile (中文 glyph fallback when no sprite handle)
//   • PUNG / CHOW / KONG / HU badges, lit when available
//   • A horizontal progress bar shrinking 100% → 0% over the window
//   • Remaining-seconds readout (aria-live="polite")
//   • Keyboard-shortcut hints (C / K / H / Esc — Pung has none; see #137)
//
// This overlay is ADDITIVE to the existing in-side-panel claim UI
// that game-ui.ts:setupClaimButtons() renders.  We do not modify
// Hicks's trunk (game-ui.ts, world.ts, lobby.ts, index.html) — the
// overlay subscribes to the same `client.claim` Collection and
// `client.claim.set(...)` to commit a claim/pass, mirroring the
// game-ui.ts:sendClaim() wire shape.
//
// Mobile sizing: laid out so the controls remain ≥44px tall at the
// iPhone-SE viewport (375 × 667), per Apple/Material touch-target
// guidance.
//
// Lifecycle:
//   • new ClaimWindowOverlay(game).attach()  — install DOM + handlers
//   • the overlay never tears itself down; the host page reload
//     (lobby return / new game) clears it via window unload.
//
// No imports from Hicks's trunk modules — we only depend on the
// shape of the `client` accessor + the `claim` collection.

import './claim-window-overlay.css';
import type { Client } from '../client';
import type { ClaimWindowEntry } from '../types';
import { getRuleActionControls, type RuleActionControls } from './rule-action-controls';

// #137 — keys the always-on game view/navigation handler (game.ts onKeyDown)
// owns. The claim overlay's global keydown listener must never turn one of
// these into an irreversible meld/win commit. `p` (perspective toggle) is the
// acute case: pressing it to change the camera while a claim window was open
// silently committed a Pung. Kept in lock-step with game.ts's switch.
const RESERVED_GAME_KEYS = new Set<string>(['f', 'r', ' ', 'z', 'x', 'q', 'p', 'l']);

type ClaimType = 'Pung' | 'Chow' | 'Kong' | 'Hu';

type ClaimEntry = ClaimWindowEntry;

interface GameLike {
  client?: Client;
}

const TICK_MS = 100;

// Display label table — 中文 primary + pinyin sublabel (matches Default
// #5, Vasquez Q5: Chinese primary, pinyin secondary).
const LABELS: Record<ClaimType, { zh: string; py: string; key: string }> = {
  // #137 — Pung has no keyboard shortcut: its only mnemonic (`p`) is reserved by
  // the game's global perspective-view toggle, and sharing it silently melded.
  Pung: { zh: '碰', py: 'PUNG', key: '' },
  Chow: { zh: '吃', py: 'CHOW', key: 'C' },
  Kong: { zh: '杠', py: 'KONG', key: 'K' },
  Hu:   { zh: '胡', py: 'HU',   key: 'H' },
};

// Changsha tile-id → glyph map.  IDs 0..26 are the 3 number suits 1-9
// in stripe order (man=0..8 → 1m..9m, but Changsha drops m/s/p suit
// markers and uses a single 27-tile deck of pin/dot/man depending on
// the variant; we render a generic glyph since the overlay just needs
// to communicate "this tile" to the player).  The actual sprite is
// in three.js scene-space; we surface a textual fallback that's
// always visible regardless of canvas state.
function tileGlyph(tileId: number): string {
  if (tileId < 0) return '?';
  // Changsha (no honors, no winds in the claimable subset): 27 tiles.
  // We map the index into 3 suits × 9 numerals.  This matches the
  // display convention move-log.ts:tileGlyph() ships.
  const suits = ['一二三四五六七八九', '①②③④⑤⑥⑦⑧⑨', '⓵⓶⓷⓸⓹⓺⓻⓼⓽'];
  const suit = Math.floor(tileId / 9);
  const num  = tileId % 9;
  if (suit < 0 || suit >= suits.length) return '?';
  return suits[suit][num] ?? '?';
}

export class ClaimWindowOverlay {
  private readonly game: GameLike;
  private root: HTMLDivElement | null = null;
  private tileEl!: HTMLDivElement;
  private badgesEl!: HTMLDivElement;
  private progressEl!: HTMLDivElement;
  private timerEl!: HTMLSpanElement;
  private hintEl!: HTMLDivElement;
  private passBtn!: HTMLButtonElement;
  private badges: Partial<Record<ClaimType, HTMLButtonElement>> = {};

  private activeClaim: ClaimEntry | null = null;
  private lastClaimRef: ClaimEntry | null = null;
  private windowMs: number = 5000;
  private tickHandle: number | null = null;
  private keyboardBound: boolean = false;
  private actions!: RuleActionControls;
  private unsubscribeActions: (() => void) | null = null;

  constructor(game: GameLike) {
    this.game = game;
  }

  attach(): void {
    if (this.root !== null) return;
    const client = this.game.client;
    if (client === undefined) return;

    this.root = this.buildDom();
    document.body.appendChild(this.root);

    this.actions = getRuleActionControls(client);
    this.unsubscribeActions = this.actions.onChange(() => this.syncFromCollection());
    this.bindKeyboard();
    // Reflect any pre-existing claim (reconnect path).
    this.syncFromCollection();
  }

  detach(): void {
    if (this.root === null) return;
    this.stopTicker();
    this.root.remove();
    this.root = null;
    this.activeClaim = null;
    this.unsubscribeActions?.();
    this.unsubscribeActions = null;
    if (this.keyboardBound) {
      window.removeEventListener('keydown', this.onKeyDown);
      this.keyboardBound = false;
    }
  }

  // ---------------------------------------------------------------------
  // DOM construction.
  // ---------------------------------------------------------------------

  private buildDom(): HTMLDivElement {
    const root = document.createElement('div');
    root.className = 'ferro-claim-overlay';
    root.setAttribute('role', 'region');
    root.setAttribute('aria-label', 'Claim window');
    root.hidden = true;

    // Tile column — shows the claimable tile glyph.
    const tile = document.createElement('div');
    tile.className = 'ferro-claim-tile';
    tile.setAttribute('aria-label', 'Claimable tile');
    const tileFace = document.createElement('div');
    tileFace.className = 'ferro-claim-tile-face';
    tile.appendChild(tileFace);
    const tileSrc = document.createElement('div');
    tileSrc.className = 'ferro-claim-tile-source';
    tile.appendChild(tileSrc);
    this.tileEl = tile;

    // Center column — countdown timer + progress bar.
    const center = document.createElement('div');
    center.className = 'ferro-claim-center';

    const header = document.createElement('div');
    header.className = 'ferro-claim-header';
    const title = document.createElement('span');
    title.className = 'ferro-claim-title';
    title.textContent = 'Claim window';
    const timerWrap = document.createElement('span');
    timerWrap.className = 'ferro-claim-timer';
    timerWrap.setAttribute('aria-live', 'polite');
    timerWrap.setAttribute('aria-atomic', 'true');
    const timer = document.createElement('span');
    timer.className = 'ferro-claim-timer-value';
    timer.textContent = '0.0';
    timerWrap.appendChild(timer);
    const timerSuffix = document.createElement('span');
    timerSuffix.className = 'ferro-claim-timer-suffix';
    timerSuffix.textContent = 's';
    timerWrap.appendChild(timerSuffix);
    header.appendChild(title);
    header.appendChild(timerWrap);
    center.appendChild(header);
    this.timerEl = timer;

    const progressTrack = document.createElement('div');
    progressTrack.className = 'ferro-claim-progress';
    progressTrack.setAttribute('role', 'progressbar');
    progressTrack.setAttribute('aria-valuemin', '0');
    progressTrack.setAttribute('aria-valuemax', '100');
    const progressFill = document.createElement('div');
    progressFill.className = 'ferro-claim-progress-fill';
    progressTrack.appendChild(progressFill);
    center.appendChild(progressTrack);
    this.progressEl = progressFill;

    // Badges row — Pung / Chow / Kong / Hu.
    const badges = document.createElement('div');
    badges.className = 'ferro-claim-badges';
    for (const t of ['Pung', 'Chow', 'Kong', 'Hu'] as ClaimType[]) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = `ferro-claim-badge ferro-claim-badge-${t.toLowerCase()}`;
      btn.dataset.claimType = t;
      btn.setAttribute(
        'aria-label',
        LABELS[t].key ? `Claim ${LABELS[t].py} (shortcut ${LABELS[t].key})` : `Claim ${LABELS[t].py}`);
      btn.disabled = true;
      const zh = document.createElement('span');
      zh.className = 'ferro-claim-badge-zh';
      zh.textContent = LABELS[t].zh;
      const py = document.createElement('span');
      py.className = 'ferro-claim-badge-py';
      py.textContent = LABELS[t].py;
      btn.appendChild(zh);
      btn.appendChild(py);
      // #137 — only render a shortcut hint when the badge actually has one
      // (Pung's `p` was removed to avoid colliding with the camera toggle).
      if (LABELS[t].key) {
        const shortcut = document.createElement('span');
        shortcut.className = 'ferro-claim-badge-key';
        shortcut.textContent = LABELS[t].key;
        btn.appendChild(shortcut);
      }
      btn.addEventListener('click', () => this.commitClaim(t));
      badges.appendChild(btn);
      this.badges[t] = btn;
    }
    center.appendChild(badges);
    this.badgesEl = badges;

    // Pass column.
    const passWrap = document.createElement('div');
    passWrap.className = 'ferro-claim-pass-wrap';
    const passBtn = document.createElement('button');
    passBtn.type = 'button';
    passBtn.className = 'ferro-claim-pass';
    passBtn.setAttribute('aria-label', 'Pass on this claim (shortcut Esc)');
    const passLabel = document.createElement('span');
    passLabel.className = 'ferro-claim-pass-label';
    passLabel.innerHTML = '<span class="zh">跳过</span><span class="py">PASS</span>';
    const passKey = document.createElement('span');
    passKey.className = 'ferro-claim-pass-key';
    passKey.textContent = 'Esc';
    passBtn.appendChild(passLabel);
    passBtn.appendChild(passKey);
    passBtn.addEventListener('click', () => this.commitPass());
    passWrap.appendChild(passBtn);
    this.passBtn = passBtn;

    // Hint line.
    const hint = document.createElement('div');
    hint.className = 'ferro-claim-hint';
    hint.textContent = 'Click a chip or press the highlighted key — auto-pass at 0.';
    center.appendChild(hint);
    this.hintEl = hint;

    root.appendChild(tile);
    root.appendChild(center);
    root.appendChild(passWrap);
    return root;
  }

  // ---------------------------------------------------------------------
  // Subscriptions.
  // ---------------------------------------------------------------------

  private syncFromCollection(): void {
    this.activeClaim = this.actions.claim;
    this.refresh();
  }

  // ---------------------------------------------------------------------
  // Render.
  // ---------------------------------------------------------------------

  private refresh(): void {
    if (this.root === null) return;
    const claim = this.activeClaim;
    if (claim === null) {
      this.stopTicker();
      this.root.hidden = true;
      this.root.classList.remove('ferro-claim-overlay-visible');
      return;
    }

    // Frost 2026-05-29 — when the backend emits `deadline=0` it means
    // "no client-side countdown; server enforces the timeout".  Render
    // the overlay (badges + Pass) without a countdown rather than
    // treating 0 as "already expired" (which used to auto-hide the
    // overlay the instant a claim window opened for the local seat).
    const hasCountdown = claim.deadline > 0;
    const remaining = hasCountdown ? Math.max(0, claim.deadline - Date.now()) : 0;
    if (claim !== this.lastClaimRef) {
      this.windowMs = hasCountdown ? Math.max(remaining, 1) : 1;
      this.lastClaimRef = claim;
    }

    // Tile face + source seat.
    const face = this.tileEl.querySelector('.ferro-claim-tile-face') as HTMLDivElement | null;
    if (face !== null) face.textContent = tileGlyph(Math.floor(claim.tile / 4));
    const src = this.tileEl.querySelector('.ferro-claim-tile-source') as HTMLDivElement | null;
    if (src !== null) src.textContent = `Seat ${claim.source}`;

    // Badge state.
    for (const t of ['Pung', 'Chow', 'Kong', 'Hu'] as ClaimType[]) {
      const btn = this.badges[t];
      if (btn === undefined) continue;
      const ok = claim.available.includes(t);
      btn.disabled = !ok;
      btn.classList.toggle('ferro-claim-badge-available', ok);
    }
    this.passBtn.disabled = false;

    this.root.hidden = false;
    this.root.classList.add('ferro-claim-overlay-visible');
    if (hasCountdown) {
      this.startTicker();
      this.tick();
    } else {
      // Server-only timer — surface a static "—" instead of a counting timer
      // and hold the progress bar full so the overlay reads "open, no expiry".
      this.stopTicker();
      this.timerEl.textContent = '—';
      this.progressEl.style.width = '100%';
      this.progressEl.parentElement?.setAttribute('aria-valuenow', '100');
      this.root.classList.remove('ferro-claim-urgent', 'ferro-claim-critical');
    }
  }

  private startTicker(): void {
    this.stopTicker();
    this.tickHandle = window.setInterval(() => this.tick(), TICK_MS);
  }

  private stopTicker(): void {
    if (this.tickHandle !== null) {
      window.clearInterval(this.tickHandle);
      this.tickHandle = null;
    }
  }

  private tick(): void {
    if (this.root === null) return;
    const claim = this.activeClaim;
    if (claim === null) {
      this.stopTicker();
      return;
    }
    // Frost 2026-05-29 — guard against deadline=0 reaching the ticker
    // (defensive — refresh() should already have skipped startTicker, but
    // an inflight tick from a previous claim could land after a transition).
    if (claim.deadline <= 0) {
      this.stopTicker();
      return;
    }
    const remaining = Math.max(0, claim.deadline - Date.now());
    const pct = this.windowMs > 0 ? Math.max(0, Math.min(100, (remaining / this.windowMs) * 100)) : 0;
    this.progressEl.style.width = `${pct.toFixed(1)}%`;
    this.progressEl.parentElement?.setAttribute('aria-valuenow', String(Math.round(pct)));
    this.timerEl.textContent = (remaining / 1000).toFixed(1);

    // Urgency colors.
    this.root.classList.toggle('ferro-claim-urgent', remaining <= 1500);
    this.root.classList.toggle('ferro-claim-critical', remaining <= 500);

    if (remaining <= 0) {
      // game-ui.ts already auto-passes on its own timer; we just
      // visually settle.  Don't double-fire pass — leave the wire
      // contract to the trunk owner.
      this.stopTicker();
      this.activeClaim = null;
      this.root.classList.remove('ferro-claim-overlay-visible');
      window.setTimeout(() => {
        if (this.activeClaim === null && this.root !== null) {
          this.root.hidden = true;
        }
      }, 350);
    }
  }

  // ---------------------------------------------------------------------
  // Commit actions.  Mirrors game-ui.ts:sendClaim wire shape:
  //   client.claim.set(selfKey, { action: 'claim', type })
  //   client.claim.set(selfKey, { action: 'pass',  type: null })
  // ---------------------------------------------------------------------

  private commitClaim(type: ClaimType): void {
    const claim = this.activeClaim;
    if (claim === null) return;
    if (!claim.available.includes(type)) return;
    this.actions.requestClaim(type);
  }

  private commitPass(): void {
    const claim = this.activeClaim;
    if (claim === null) return;
    this.actions.requestClaim('Pass');
  }

  // ---------------------------------------------------------------------
  // Keyboard shortcuts.  Only fire when the overlay is visible AND the
  // focused element isn't an input/textarea (avoid hijacking chat).
  // ---------------------------------------------------------------------

  private bindKeyboard(): void {
    if (this.keyboardBound) return;
    window.addEventListener('keydown', this.onKeyDown);
    this.keyboardBound = true;
  }

  private readonly onKeyDown = (ev: KeyboardEvent): void => {
    if (this.activeClaim === null) return;
    if (this.root === null || this.root.hidden) return;
    const target = ev.target as HTMLElement | null;
    if (target !== null) {
      const tag = target.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || target.isContentEditable) return;
    }
    if (ev.altKey || ev.ctrlKey || ev.metaKey) return;
    const key = ev.key.toLowerCase();
    // #137 — do NOT bind letter keys that the always-on game view/navigation
    // handler (game.ts onKeyDown) already owns to an irreversible claim commit.
    // A bare `p` toggles the perspective/flat camera GLOBALLY; when it ALSO
    // committed a Pung here, pressing `p` to change the view while a claim
    // window happened to be open silently melded — leaving the human holding a
    // meld with no drawn 14th tile, unable to discard, and the hand wedged
    // (handEnds=0). Camera keys (game.ts binds f/F/r/R/space/z/x/q/p/l) must
    // never shadow a meld/win commit. Chow/Kong/Hu use non-camera keys and the
    // full claim set remains one click away on the on-screen buttons; Pung has
    // no keyboard shortcut by design (its only safe mnemonic, `p`, is reserved).
    if (RESERVED_GAME_KEYS.has(key)) return;
    if (key === 'c') {
      if (this.activeClaim.available.includes('Chow')) {
        ev.preventDefault();
        this.commitClaim('Chow');
      }
    } else if (key === 'k') {
      if (this.activeClaim.available.includes('Kong')) {
        ev.preventDefault();
        this.commitClaim('Kong');
      }
    } else if (key === 'h') {
      if (this.activeClaim.available.includes('Hu')) {
        ev.preventDefault();
        this.commitClaim('Hu');
      }
    } else if (ev.key === 'Escape') {
      ev.preventDefault();
      this.commitPass();
    }
  };
}

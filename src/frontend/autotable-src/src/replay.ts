// Phase J Wave 3 — Tile-by-tile replay viewer.
//
// A self-contained replay screen that captures the live game's tile
// movements + hand-result history and lets the user scrub through any
// completed hand of the match.
//
// Scope choice (per Wave 3 directive): SIMPLIFIED 2D TOP-DOWN view.
// The autotable 3D scene is owned by World/ObjectView and tightly
// coupled to the live `things` collection — reusing it for replay
// would require deep state-snapshot machinery (de-spawning live tiles,
// re-spawning replay tiles, juggling camera state, etc.).  The 2D
// view ships the meaningful UX (timeline scrubber + per-move tile
// glyph display) at a fraction of the surface area.  Reusing the 3D
// scene is documented as a future-wave upgrade in the inbox memo.
//
// Data sources:
//   • `things` collection — captures tile slot transitions in real time.
//     A draw is a tile entering `hand.*`, a discard is a tile entering
//     `discard.*`, a meld is a tile entering `meld.*`.  Each transition
//     is timestamped + tagged with the current hand number and pushed
//     into a per-hand `moves` array.
//   • `result.current` — flushes the current hand's move buffer into the
//     completed-hands array and advances the hand counter.
//   • `match.0.dealer` change — alternative hand-boundary signal, used
//     when no result arrives (e.g. drawn games where the runtime
//     bypasses the result.current update).
//   • `gameComplete["current"].handHistory` — preferred source for the
//     per-hand winning hand snapshots when Bishop's runtime broadcasts
//     it.  Falls back to the client-side accumulator otherwise.

import { Client } from "./client";
import { HandResultEntry, ThingInfo, MatchInfo } from "./types";
import { parseRealTileId as changshaParseRealTileId } from "./changsha-mode-policy";
import {
  parseTilesJson,
  readFinalsFlagFromUrl,
  registerReplayLauncher,
  type ServerReplayEvent,
  type ServerReplayResponse,
} from "./replay-launcher";
import { setAuditGameId } from "./audit";

// ── Move-log types ───────────────────────────────────────────────────

export type MoveKind = 'draw' | 'discard' | 'meld';

export interface ReplayMove {
  kind: MoveKind;
  seat: number;
  tile: number;       // tile id (0..107 → mod 27 = suit/rank glyph)
  face: number | null;
  slotName: string;
  timestamp: number;
}

export interface ReplayHand {
  handNumber: number;
  moves: ReplayMove[];
  result: HandResultEntry | null;
}

// ── Playback speed dropdown ──────────────────────────────────────────

export const REPLAY_SPEEDS: ReadonlyArray<{ value: number; label: string }> = [
  { value: 0.5, label: '0.5x' },
  { value: 1.0, label: '1x' },
  { value: 2.0, label: '2x' },
  { value: 4.0, label: '4x' },
];
const REPLAY_BASE_DELAY_MS = 700;

// ── Tile glyphs (shared with move-log.ts conventions) ────────────────

function tileGlyph(tile: number | null | undefined): { text: string; suit: string } {
  if (tile === null || tile === undefined || tile < 0) {
    return { text: '?', suit: 'suit-unknown' };
  }
  const suits = ['m', 'p', 's'];
  const labels = ['万', '筒', '条'];
  const idx = ((tile % 27) + 27) % 27;
  const suit = suits[Math.floor(idx / 9)];
  const label = labels[Math.floor(idx / 9)];
  const rank = (idx % 9) + 1;
  return { text: `${rank}${label}`, suit: `suit-${suit}` };
}

// Phase J Wave 7 — map a server replay event's `action` / `phase` to
// the existing ReplayMove `kind` enum.  Unknown actions are skipped.
function mapServerActionToKind(action: string, phase: string): MoveKind | null {
  const a = (action || '').toLowerCase();
  const p = (phase || '').toLowerCase();
  if (a.includes('draw') || a.includes('pick')) return 'draw';
  if (a.includes('discard')) return 'discard';
  if (a.includes('meld') || a.includes('chow') || a.includes('pung') || a.includes('kong')) return 'meld';
  if (p.includes('draw')) return 'draw';
  if (p.includes('discard')) return 'discard';
  if (p.includes('meld')) return 'meld';
  return null;
}

// ── Replay manager ───────────────────────────────────────────────────

export class Replay {
  private client: Client;

  // Completed hands buffer.  We push a snapshot every time `result.current`
  // resolves (Hu / Draw / ZhaHu) or the match's dealer rotates without a
  // result (defensive — covers an edge case where Bishop's runtime might
  // not push `result.current` on a fresh new hand).
  private hands: ReplayHand[] = [];
  private currentHandNumber: number = 0;

  // In-progress capture buffer for the live hand.  Flushed into `hands[]`
  // on hand completion.
  private currentMoves: ReplayMove[] = [];
  private seenSlots: Map<number, string> = new Map(); // tileId → last slotName

  // UI state.
  private screenEl: HTMLElement | null = null;
  private selectorEl: HTMLSelectElement | null = null;
  private timelineEl: HTMLInputElement | null = null;
  private timelineLabelEl: HTMLElement | null = null;
  private boardEl: HTMLElement | null = null;
  private moveLogEl: HTMLElement | null = null;
  private playBtn: HTMLButtonElement | null = null;
  private stepBackBtn: HTMLButtonElement | null = null;
  private stepFwdBtn: HTMLButtonElement | null = null;
  private closeBtn: HTMLButtonElement | null = null;
  // Phase J Wave 7 — new controls.
  private prevHandBtn: HTMLButtonElement | null = null;
  private nextHandBtn: HTMLButtonElement | null = null;
  private speedSelectEl: HTMLSelectElement | null = null;
  private eventCounterEl: HTMLElement | null = null;
  private sourceLabelEl: HTMLElement | null = null;

  private selectedHandIdx: number = 0;
  private selectedMoveIdx: number = 0;
  private playInterval: number | null = null;
  private playDelayMs: number = REPLAY_BASE_DELAY_MS;
  // Phase J Wave 7 — speed multiplier (1x by default).  Drives playDelayMs.
  private speedMultiplier: number = 1.0;
  // Phase J Wave 7 — server-loaded replay payload (null when we're
  // showing the in-memory client capture).
  private serverPayload: ServerReplayResponse | null = null;
  private domWired: boolean = false;

  constructor(client: Client) {
    this.client = client;
  }

  start(): void {
    // Subscribe to live game collections.
    this.client.things.on('update', this.onThings.bind(this));
    this.client.result.on('update', this.onResult.bind(this));
    this.client.match.on('update', this.onMatch.bind(this));

    // Clear state on every fresh connect (new match starts fresh).
    this.client.on('connect', () => {
      this.hands = [];
      this.currentMoves = [];
      this.seenSlots.clear();
      this.currentHandNumber = 0;
    });

    // Lazy-resolve DOM nodes — they may not exist when the viewer is
    // first imported.  We re-resolve in `open()` to handle the case
    // where DOM nodes are added after this constructor runs.
    this.resolveDom();

    // Phase J Wave 7 — expose the server-replay open path through the
    // launcher module so any surface (leaderboard, profile page,
    // post-game modal) can request `openReplayForGame(gameId)`.
    registerReplayLauncher((payload, options) => this.openServer(payload, options));
  }

  // ── Public API ─────────────────────────────────────────────────────

  /** Return the snapshotted completed hands plus the in-progress one. */
  getHands(): ReplayHand[] {
    const out: ReplayHand[] = [...this.hands];
    if (this.currentMoves.length > 0) {
      out.push({
        handNumber: this.currentHandNumber,
        moves: this.currentMoves.slice(),
        result: null,
      });
    }
    return out;
  }

  /** Open the replay screen.  Optionally seed with a `handHistory` array
   *  pushed by Bishop's runtime (preferred over the client-side capture
   *  when the wire payload carries it).
   */
  open(serverHandHistory?: ReadonlyArray<HandResultEntry>): void {
    this.resolveDom();
    if (!this.screenEl) return;

    // Phase J Wave 7 — clear any prior server payload; this is the
    // in-memory open path.
    this.serverPayload = null;
    this.updateSourceLabel('Live capture');
    // Phase J Wave 9 — surface the live gameId so the Audit tab can
    // fetch /api/games/{gameId}/audit when an admin opens the tab.
    setAuditGameId(this.client.lastGameId);

    // Merge server-side history with the client-side capture.  If the
    // server ships a full handHistory array we trust it for the result
    // payloads but keep our per-hand move captures (server doesn't ship
    // those yet).  When lengths disagree we pad / truncate to the longer
    // of the two so the dropdown stays consistent.
    if (serverHandHistory && serverHandHistory.length > 0) {
      const merged: ReplayHand[] = [];
      const maxLen = Math.max(serverHandHistory.length, this.hands.length);
      for (let i = 0; i < maxLen; i++) {
        const moves = this.hands[i]?.moves ?? [];
        const result = serverHandHistory[i] ?? this.hands[i]?.result ?? null;
        merged.push({
          handNumber: i + 1,
          moves,
          result,
        });
      }
      this.hands = merged;
    }

    this.populateSelector();
    this.selectedHandIdx = Math.max(0, this.hands.length - 1);
    this.selectedMoveIdx = 0;
    this.render();
    this.screenEl.classList.add('replay-open');
    this.screenEl.setAttribute('aria-hidden', 'false');
  }

  /**
   * Phase J Wave 7 — open the replay screen with a server-fetched
   * payload (from `GET /api/games/{gameId}/replay`).  Converts the
   * flat event list into per-hand `ReplayMove` buckets so the
   * existing renderer can chew on them unchanged.
   */
  openServer(payload: ServerReplayResponse, options?: { finals?: boolean }): void {
    this.resolveDom();
    if (!this.screenEl) return;

    this.serverPayload = payload;
    // Phase J Wave 9 — propagate the gameId so the Audit tab can fetch
    // matching audit rows when an admin clicks the Audit tab.
    setAuditGameId(payload.gameId);
    this.hands = this.materializeHandsFromServerPayload(payload);
    if (this.hands.length === 0) {
      // Empty payload (Bishop's endpoint not implemented yet, or no
      // events captured).  Push a single empty hand so the UI shell
      // renders something instead of going blank.
      this.hands = [{ handNumber: 1, moves: [], result: null }];
      this.updateSourceLabel(`Game ${payload.gameId.slice(0, 8)} — no events recorded`);
    } else {
      this.updateSourceLabel(`Game ${payload.gameId.slice(0, 8)} — ${payload.events.length} events`);
    }

    this.populateSelector();
    // Phase K Wave 2 — `?finals=true` deep-link: jump straight to the
    // last hand, fully scrubbed to the final move.  Honours both the
    // explicit `options.finals` from the tournament finals button and
    // the URL flag for shared links.
    const wantFinals = options?.finals === true || readFinalsFlagFromUrl();
    if (wantFinals && this.hands.length > 0) {
      this.selectedHandIdx = this.hands.length - 1;
      const last = this.hands[this.selectedHandIdx];
      this.selectedMoveIdx = last.moves.length;
    } else {
      this.selectedHandIdx = 0;
      this.selectedMoveIdx = 0;
    }
    this.render();
    this.screenEl.classList.add('replay-open');
    this.screenEl.setAttribute('aria-hidden', 'false');
    if (wantFinals) {
      // Scroll the screen into view so a deep-link visitor lands on
      // the replay rather than the original lobby surface.
      this.screenEl.scrollIntoView({ behavior: 'auto', block: 'start' });
    }
    // Phase K Wave 6 — Lazy-load the AI commentary panel.  Only the
    // server-replay path has a stable gameId so we only fetch the
    // chunk here (the in-memory `open()` path has no commentary
    // source yet).  Failures inside the panel are self-contained;
    // the replay viewer keeps working.
    void this.mountCommentaryPanel(payload.gameId);
  }

  /**
   * Phase K Wave 6 — Dynamic-import the commentary chunk and mount
   * it inside `#replay-commentary-host`.  No-op if the host element
   * isn't in the DOM (older `index.html` snapshots).  All failures
   * are swallowed — the replay viewer never depends on commentary.
   */
  private async mountCommentaryPanel(gameId: string): Promise<void> {
    const host = document.getElementById('replay-commentary-host');
    if (host === null || gameId === '') return;
    try {
      const mod = await import('./commentary-panel');
      await mod.openCommentaryPanel(host, gameId);
    } catch {
      /* commentary is best-effort; never tear down the replay */
    }
  }

  /** Convert flat ServerReplayEvent[] → per-hand ReplayMove[] buckets. */
  private materializeHandsFromServerPayload(payload: ServerReplayResponse): ReplayHand[] {
    const buckets = new Map<number, ReplayMove[]>();
    for (const ev of payload.events) {
      const handNum = Math.max(1, ev.turn || 1);
      let bucket = buckets.get(handNum);
      if (bucket === undefined) {
        bucket = [];
        buckets.set(handNum, bucket);
      }
      const kind = mapServerActionToKind(ev.action, ev.phase);
      if (kind === null) continue;
      const tiles = parseTilesJson(ev.tilesJson);
      const tileId = tiles.length > 0 ? tiles[0] : -1;
      bucket.push({
        kind,
        seat: ev.actor,
        tile: tileId,
        face: tileId,
        slotName: ev.phase,
        timestamp: Date.parse(ev.timestampUtc) || Date.now(),
      });
    }
    const hands: ReplayHand[] = [];
    const sorted = Array.from(buckets.keys()).sort((a, b) => a - b);
    for (const handNum of sorted) {
      const moves = buckets.get(handNum) ?? [];
      // Pull the matching handHistory entry (if the server attached one).
      const result = payload.handHistory?.[handNum - 1] ?? null;
      hands.push({ handNumber: handNum, moves, result });
    }
    return hands;
  }

  private updateSourceLabel(text: string): void {
    if (this.sourceLabelEl !== null) {
      this.sourceLabelEl.textContent = text;
    }
  }

  close(): void {
    if (!this.screenEl) return;
    this.stopPlay();
    this.screenEl.classList.remove('replay-open');
    this.screenEl.setAttribute('aria-hidden', 'true');
    // Phase K Wave 6 — Tear down the commentary panel so a re-open
    // for a different game can re-fetch.  Best-effort — the dynamic
    // import may not have resolved.
    void this.unmountCommentaryPanel();
  }

  private async unmountCommentaryPanel(): Promise<void> {
    try {
      const mod = await import('./commentary-panel');
      mod.closeCommentaryPanel();
    } catch {
      /* never resolved — nothing to tear down */
    }
  }

  // ── DOM wiring ─────────────────────────────────────────────────────

  private resolveDom(): void {
    if (this.domWired) return;
    this.screenEl = document.getElementById('replay-screen');
    if (this.screenEl === null) return;
    this.selectorEl = document.getElementById('replay-hand-selector') as HTMLSelectElement;
    this.timelineEl = document.getElementById('replay-timeline') as HTMLInputElement;
    this.timelineLabelEl = document.getElementById('replay-timeline-label');
    this.boardEl = document.getElementById('replay-board');
    this.moveLogEl = document.getElementById('replay-move-log');
    this.playBtn = document.getElementById('replay-play') as HTMLButtonElement;
    this.stepBackBtn = document.getElementById('replay-step-back') as HTMLButtonElement;
    this.stepFwdBtn = document.getElementById('replay-step-fwd') as HTMLButtonElement;
    this.closeBtn = document.getElementById('replay-close') as HTMLButtonElement;
    // Phase J Wave 7 — additional controls.
    this.prevHandBtn = document.getElementById('replay-prev') as HTMLButtonElement;
    this.nextHandBtn = document.getElementById('replay-next') as HTMLButtonElement;
    this.speedSelectEl = document.getElementById('replay-speed-select') as HTMLSelectElement;
    this.eventCounterEl = document.getElementById('replay-event-counter');
    this.sourceLabelEl = document.getElementById('replay-source-label');

    this.domWired = true;

    if (this.selectorEl) {
      this.selectorEl.addEventListener('change', () => {
        this.stopPlay();
        this.selectedHandIdx = parseInt(this.selectorEl!.value, 10) || 0;
        this.selectedMoveIdx = 0;
        this.render();
      });
    }
    if (this.timelineEl) {
      this.timelineEl.addEventListener('input', () => {
        this.stopPlay();
        this.selectedMoveIdx = parseInt(this.timelineEl!.value, 10) || 0;
        this.render();
      });
    }
    if (this.playBtn) {
      this.playBtn.addEventListener('click', () => this.togglePlay());
    }
    if (this.stepBackBtn) {
      this.stepBackBtn.addEventListener('click', () => {
        this.stopPlay();
        this.selectedMoveIdx = Math.max(0, this.selectedMoveIdx - 1);
        this.render();
      });
    }
    if (this.stepFwdBtn) {
      this.stepFwdBtn.addEventListener('click', () => {
        this.stopPlay();
        const hand = this.hands[this.selectedHandIdx];
        const max = hand ? hand.moves.length : 0;
        this.selectedMoveIdx = Math.min(max, this.selectedMoveIdx + 1);
        this.render();
      });
    }
    if (this.prevHandBtn) {
      this.prevHandBtn.addEventListener('click', () => {
        this.stopPlay();
        this.selectedHandIdx = Math.max(0, this.selectedHandIdx - 1);
        this.selectedMoveIdx = 0;
        this.render();
      });
    }
    if (this.nextHandBtn) {
      this.nextHandBtn.addEventListener('click', () => {
        this.stopPlay();
        this.selectedHandIdx = Math.min(this.hands.length - 1, this.selectedHandIdx + 1);
        this.selectedMoveIdx = 0;
        this.render();
      });
    }
    if (this.speedSelectEl) {
      // Populate options from the canonical REPLAY_SPEEDS list.
      this.speedSelectEl.replaceChildren();
      for (const s of REPLAY_SPEEDS) {
        const opt = document.createElement('option');
        opt.value = String(s.value);
        opt.textContent = s.label;
        if (s.value === 1.0) opt.selected = true;
        this.speedSelectEl.appendChild(opt);
      }
      this.speedSelectEl.addEventListener('change', () => {
        const v = parseFloat(this.speedSelectEl!.value);
        if (!isNaN(v) && v > 0) {
          this.speedMultiplier = v;
          this.playDelayMs = Math.round(REPLAY_BASE_DELAY_MS / v);
          // If we're playing, restart the interval with the new delay.
          if (this.playInterval !== null) {
            this.stopPlay();
            this.startPlay();
          }
        }
      });
    }
    if (this.closeBtn) {
      this.closeBtn.addEventListener('click', () => this.close());
    }
    // Phase J Wave 7 — Escape closes the replay viewer when open.
    document.addEventListener('keydown', (e: KeyboardEvent) => {
      if (!this.screenEl?.classList.contains('replay-open')) return;
      if (e.key === 'Escape') {
        e.preventDefault();
        this.close();
      }
    });
  }

  // ── Capture loop ───────────────────────────────────────────────────

  private onThings(allEntries: Array<[string | number, ThingInfo | null]>): void {
    // FE-7/SC-2 (G19) — keep only ENTITLED real tiles (numeric key 0..107,
    // number OR numeric-string), normalized to a number; skip opaque hidden
    // handles (`h_<base64url>`). Never does arithmetic on a handle.
    const entries = allEntries
      .map(([k, info]): [number | null, ThingInfo | null] => [changshaParseRealTileId(k), info])
      .filter((e): e is [number, ThingInfo | null] => e[0] !== null);
    for (const [tileId, info] of entries) {
      if (!info) continue;
      const slot = info.slotName ?? '';
      const prev = this.seenSlots.get(tileId);
      if (prev === slot) continue;
      this.seenSlots.set(tileId, slot);

      let kind: MoveKind | null = null;
      if (slot.startsWith('discard.')) kind = 'discard';
      else if (slot.startsWith('meld.')) kind = 'meld';
      else if (slot.startsWith('hand.')) {
        // Only count as a draw when the tile previously lived in the wall.
        // Initial dealing fires hand.* transitions too, but that's still
        // a "draw" semantically (player picks tile from wall).
        if (!prev || prev.startsWith('wall.') || prev === '') {
          kind = 'draw';
        }
      }
      if (kind === null) continue;

      const seatMatch = slot.match(/@(\d+)$/);
      const seat = seatMatch ? parseInt(seatMatch[1], 10) : -1;
      if (seat < 0 || seat > 3) continue;

      this.currentMoves.push({
        kind,
        seat,
        tile: tileId,
        face: info.face ?? null,
        slotName: slot,
        timestamp: Date.now(),
      });
    }
  }

  private onResult(entries: Array<[string, HandResultEntry | null]>): void {
    for (const [key, value] of entries) {
      if (key !== 'current') continue;
      if (value === null) {
        // Tombstone — a fresh hand begins.  Don't flush here; flush on
        // the first non-null result (we want the result attached to the
        // hand it describes).
        continue;
      }
      this.flushCurrentHand(value);
    }
  }

  private onMatch(entries: Array<[number, MatchInfo | null]>): void {
    for (const [key, value] of entries) {
      if (key !== 0 || !value) continue;
      // Dealer rotation without a result.current update — defensive flush.
      // Suppress on the very first match push (no prior hand exists).
      if (this.currentHandNumber === 0) {
        this.currentHandNumber = 1;
        continue;
      }
    }
  }

  private flushCurrentHand(result: HandResultEntry): void {
    const handNumber = this.currentHandNumber || (this.hands.length + 1);
    this.hands.push({
      handNumber,
      moves: this.currentMoves.slice(),
      result,
    });
    this.currentMoves = [];
    this.seenSlots.clear();
    this.currentHandNumber = handNumber + 1;
  }

  // ── Rendering ──────────────────────────────────────────────────────

  private populateSelector(): void {
    if (!this.selectorEl) return;
    const sel = this.selectorEl;
    sel.innerHTML = '';
    if (this.hands.length === 0) {
      const opt = document.createElement('option');
      opt.value = '0';
      opt.textContent = 'No hands recorded';
      sel.appendChild(opt);
      sel.disabled = true;
      return;
    }
    sel.disabled = false;
    this.hands.forEach((hand, i) => {
      const opt = document.createElement('option');
      opt.value = String(i);
      let label = `Hand ${hand.handNumber}`;
      if (hand.result) {
        if (hand.result.type === 'Hu') {
          label += ` — Seat ${hand.result.winner} won`;
        } else if (hand.result.type === 'ZhaHu') {
          label += ` — Seat ${hand.result.winner} false-Hu`;
        } else {
          label += ' — Washout 流局';
        }
      } else {
        label += ' — (in progress)';
      }
      opt.textContent = label;
      sel.appendChild(opt);
    });
  }

  private render(): void {
    if (!this.screenEl) return;
    const hand = this.hands[this.selectedHandIdx];
    const moves = hand?.moves ?? [];

    if (this.timelineEl) {
      this.timelineEl.min = '0';
      this.timelineEl.max = String(moves.length);
      this.timelineEl.value = String(this.selectedMoveIdx);
      this.timelineEl.disabled = moves.length === 0;
      this.timelineEl.setAttribute('aria-valuemin', '0');
      this.timelineEl.setAttribute('aria-valuemax', String(moves.length));
      this.timelineEl.setAttribute('aria-valuenow', String(this.selectedMoveIdx));
    }
    if (this.timelineLabelEl) {
      this.timelineLabelEl.textContent =
        moves.length === 0
          ? 'No moves recorded for this hand'
          : `Move ${this.selectedMoveIdx} / ${moves.length}`;
    }
    // Phase J Wave 7 — event counter mirrors the same data as the
    // timeline label but is exposed via a dedicated testid for E2E.
    if (this.eventCounterEl) {
      this.eventCounterEl.textContent =
        moves.length === 0
          ? '0 / 0'
          : `${this.selectedMoveIdx} / ${moves.length}`;
    }
    if (this.selectorEl) {
      this.selectorEl.value = String(this.selectedHandIdx);
    }
    if (this.prevHandBtn) {
      this.prevHandBtn.disabled = this.selectedHandIdx <= 0;
    }
    if (this.nextHandBtn) {
      this.nextHandBtn.disabled = this.selectedHandIdx >= this.hands.length - 1;
    }
    if (this.playBtn) {
      const playing = this.playInterval !== null;
      this.playBtn.setAttribute('aria-pressed', playing ? 'true' : 'false');
    }

    this.renderBoard(hand, moves);
    this.renderMoveLog(moves);
  }

  // Render the per-seat board state at the current scrubber index.  We
  // accumulate by replaying every move up to selectedMoveIdx into four
  // per-seat zones (hand / meld / discard).
  private renderBoard(hand: ReplayHand | undefined, moves: ReplayMove[]): void {
    if (!this.boardEl) return;
    this.boardEl.innerHTML = '';

    if (!hand) {
      const empty = document.createElement('div');
      empty.className = 'replay-empty';
      empty.textContent = 'No hand selected — finish a hand to populate the replay.';
      this.boardEl.appendChild(empty);
      return;
    }

    type SeatState = {
      hand: Set<number>;        // tile ids in hand
      meld: number[];           // tile ids in meld order
      discard: number[];        // tile ids in discard order
    };
    const states: SeatState[] = [0, 1, 2, 3].map(() => ({
      hand: new Set<number>(),
      meld: [],
      discard: [],
    }));

    const upTo = Math.min(this.selectedMoveIdx, moves.length);
    for (let i = 0; i < upTo; i++) {
      const move = moves[i];
      const s = states[move.seat];
      if (!s) continue;
      if (move.kind === 'draw') {
        s.hand.add(move.tile);
      } else if (move.kind === 'discard') {
        s.hand.delete(move.tile);
        s.discard.push(move.tile);
      } else if (move.kind === 'meld') {
        s.hand.delete(move.tile);
        s.meld.push(move.tile);
      }
    }

    // If we've replayed everything AND the hand has a final result with
    // a hand[] snapshot, paint that as the winner's final concealed hand
    // — it's the authoritative truth (the move log may miss tiles drawn
    // before the user opened the page).
    const result = hand.result;
    if (result && upTo === moves.length && result.type === 'Hu' && result.hand) {
      const winnerState = states[result.winner];
      if (winnerState) {
        winnerState.hand = new Set<number>(result.hand);
      }
    }

    const winds = ['East 东', 'South 南', 'West 西', 'North 北'];
    const seatOrder = [0, 1, 2, 3];
    for (const seat of seatOrder) {
      const state = states[seat];
      const seatBox = document.createElement('div');
      seatBox.className = `replay-seat replay-seat-${seat}`;
      const title = document.createElement('div');
      title.className = 'replay-seat-title';
      const isWinner =
        result && (result.type === 'Hu' || result.type === 'ZhaHu') && result.winner === seat;
      title.textContent = `${winds[seat]} — Seat ${seat}` + (isWinner ? ' 🏆' : '');
      if (isWinner) title.classList.add('replay-seat-winner');
      seatBox.appendChild(title);

      this.appendTileRow(seatBox, '手 Hand', [...state.hand].sort((a, b) => a - b), 'replay-tile-hand');
      this.appendTileRow(seatBox, '副露 Melds', state.meld, 'replay-tile-meld');
      this.appendTileRow(seatBox, '弃牌 Discards', state.discard, 'replay-tile-discard');

      this.boardEl.appendChild(seatBox);
    }
  }

  private appendTileRow(parent: HTMLElement, label: string, tiles: number[], cls: string): void {
    const row = document.createElement('div');
    row.className = 'replay-tile-row';
    const labelEl = document.createElement('span');
    labelEl.className = 'replay-tile-row-label';
    labelEl.textContent = label;
    row.appendChild(labelEl);
    const tilesBox = document.createElement('span');
    tilesBox.className = 'replay-tile-row-tiles';
    if (tiles.length === 0) {
      const empty = document.createElement('span');
      empty.className = 'replay-tile-empty';
      empty.textContent = '—';
      tilesBox.appendChild(empty);
    } else {
      for (const tile of tiles) {
        const glyph = tileGlyph(tile);
        const chip = document.createElement('span');
        chip.className = `replay-tile-chip ${glyph.suit} ${cls}`;
        chip.textContent = glyph.text;
        tilesBox.appendChild(chip);
      }
    }
    row.appendChild(tilesBox);
    parent.appendChild(row);
  }

  private renderMoveLog(moves: ReplayMove[]): void {
    if (!this.moveLogEl) return;
    this.moveLogEl.innerHTML = '';
    const upTo = Math.min(this.selectedMoveIdx, moves.length);
    // Show the most recent ~8 moves with the cursor at the last visible row.
    const start = Math.max(0, upTo - 8);
    for (let i = start; i < upTo; i++) {
      const move = moves[i];
      const row = document.createElement('div');
      row.className = `replay-move replay-move-${move.kind}`;
      const idx = document.createElement('span');
      idx.className = 'replay-move-idx';
      idx.textContent = `#${i + 1}`;
      const seat = document.createElement('span');
      seat.className = 'replay-move-seat';
      seat.textContent = `S${move.seat}`;
      const verb = document.createElement('span');
      verb.className = 'replay-move-verb';
      verb.textContent = move.kind === 'draw' ? 'drew' :
                         move.kind === 'discard' ? 'discarded' :
                         'melded';
      const tile = document.createElement('span');
      const glyph = tileGlyph(move.face ?? move.tile);
      tile.className = `replay-move-tile ${glyph.suit}`;
      tile.textContent = glyph.text;
      row.appendChild(idx);
      row.appendChild(seat);
      row.appendChild(verb);
      row.appendChild(tile);
      this.moveLogEl.appendChild(row);
    }
    // Auto-scroll to bottom so the latest replayed move stays visible.
    this.moveLogEl.scrollTop = this.moveLogEl.scrollHeight;
  }

  // ── Playback controls ──────────────────────────────────────────────

  private togglePlay(): void {
    if (this.playInterval !== null) {
      this.stopPlay();
    } else {
      this.startPlay();
    }
  }

  private startPlay(): void {
    if (this.playInterval !== null) return;
    const hand = this.hands[this.selectedHandIdx];
    if (!hand || hand.moves.length === 0) return;
    if (this.selectedMoveIdx >= hand.moves.length) {
      this.selectedMoveIdx = 0;
    }
    if (this.playBtn) this.playBtn.textContent = '⏸ Pause';
    this.playInterval = window.setInterval(() => {
      const h = this.hands[this.selectedHandIdx];
      if (!h) { this.stopPlay(); return; }
      if (this.selectedMoveIdx >= h.moves.length) {
        this.stopPlay();
        return;
      }
      this.selectedMoveIdx += 1;
      this.render();
    }, this.playDelayMs);
  }

  private stopPlay(): void {
    if (this.playInterval !== null) {
      window.clearInterval(this.playInterval);
      this.playInterval = null;
    }
    if (this.playBtn) this.playBtn.textContent = '▶ Play';
  }
}

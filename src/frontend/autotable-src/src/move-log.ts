// Phase I Wave 1 — streaming move-log sidebar.
//
// Self-contained UX module that subscribes to the existing client-side WS
// collections (no new wire contract) and renders a chronological list of
// user-visible game actions.  Anchored top-right under the variant badge.
//
// Data sources (every collection is already plumbed in client.ts):
//   • match  → new-hand banners (dealer / honba transitions).
//   • dice   → "Dice rolled: D1 + D2 = N → break @ col M".
//   • things → tile-aware discard + meld entries (tracks the slot a tile
//              lands in; first transition into discard.*@N / meld.*@N
//              produces a log row).
//   • sound  → fallback discard ping when a remote seat's discard arrives
//              via the sound collection but isn't echoed through things in
//              the same frame (rare; defensive).
//   • claim  → claim-window opens and outcome (Pung/Chow/Kong/Hu).
//   • pickup → manual-pickup phase transitions (Roll Dice, picking N tiles,
//              etc.).  Progress/in-play noise is suppressed.
//   • result → win / draw / false-Hu with pattern breakdown.
//
// Keeps at most MAX_ENTRIES rows in the DOM; auto-scrolls to newest on push.
// Bishop-independent: friendly-label maps fall back to the raw wire string
// so the 5 new contextual Big-Win patterns (heavenlyHand, earthlyHand,
// lastTileFromWall, lastDiscardCatch, kongReplacementWin) light up as soon
// as Bishop's branch lands, without touching this module.

import { Client } from "./client";
import { comparePatterns } from "./pattern-utils";
import { parseRealTileId as changshaParseRealTileId, formatBreakPointLabel } from "./changsha-mode-policy";
import {
  ClaimWindowEntry,
  DiceInfo,
  HandResultEntry,
  MatchInfo,
  PickupEntry,
  SoundInfo,
  SoundType,
  ThingInfo,
} from "./types";

const MAX_ENTRIES = 50;

// Short Chinese-first labels used by the move-log row text.  Mirrors
// game-ui.ts:PATTERN_LABELS but trims the English subtitle on the well-known
// Wave-2 patterns so the log row stays compact (≤ ~28 chars).
const PATTERN_LABELS_SHORT: Readonly<Record<string, string>> = {
  sevenPairs:         '七对',
  allPungs:           '碰碰胡',
  fullFlush:          '清一色',
  nineTerminals:      '九幺',
  heavenlyHand:       '天和 Heavenly',
  earthlyHand:        '地和 Earthly',
  lastTileFromWall:   '海底捞月 Last Tile',
  lastDiscardCatch:   '河底捞鱼 Last Discard',
  kongReplacementWin: '杠上开花 Kong Bloom',
};

function normalizePatternKey(p: string): string {
  if (!p) return p;
  return p.charAt(0).toLowerCase() + p.slice(1);
}

function shortPatternLabel(p: string): string {
  return PATTERN_LABELS_SHORT[normalizePatternKey(p)] ?? p;
}

// Tile-id → readable glyph.  Changsha tile-ids 0..26 enumerate three suits
// (m=characters, p=dots, s=bamboo) of nine ranks; the wider 0..107 deck
// repeats that 4× per copy, so we modulo by 27 to recover suit+rank.
function tileGlyph(tile: number | null | undefined): string {
  if (tile === null || tile === undefined) return '?';
  const suits = ['万', '筒', '条'];
  const idx = ((tile % 27) + 27) % 27;
  const suit = suits[Math.floor(idx / 9)];
  const rank = (idx % 9) + 1;
  return `${rank}${suit}`;
}

type LogCategory =
  | 'match'
  | 'dice'
  | 'pickup'
  | 'discard'
  | 'claim'
  | 'kong'
  | 'win'
  | 'info';

interface LogEntry {
  ts: string;
  seat: number | null;
  action: string;
  tile?: number;
  category: LogCategory;
}

// Loose shape we use to read ClaimWindowEntry + the local-only ClaimAction
// shape (set by game-ui.ts:sendClaim) off the same collection.  Keep this
// inline rather than importing from game-ui.ts to preserve module isolation.
interface ClaimEntryLoose {
  available?: ReadonlyArray<string>;
  tile?: number;
  source?: number;
  action?: 'claim' | 'pass';
  type?: 'Pung' | 'Chow' | 'Kong' | 'Hu' | null;
}

interface WinResultLoose {
  allPatterns?: ReadonlyArray<string>;
  AllPatterns?: ReadonlyArray<string>;
  method?: string;
  Method?: string;
  isRobbedKong?: boolean;
  IsRobbedKong?: boolean;
  // Phase I Wave 2 — wire `winType` (one of 'selfDraw' / 'discard' /
  // 'robbingKong') for the move-log emoji prefix.  Optional so a pre-W2
  // payload falls through to the legacy (no-emoji) row text.
  winType?: string;
  WinType?: string;
  // Phase J Wave 3 — Bishop's `WinResult.IsSelfDraw` + `IsKongReplacement`
  // bools (see `bishop-phase-j-wave-3.md` Task 1).  Preferred over
  // inferring from `method`/`AllPatterns` when present.
  isSelfDraw?: boolean;
  IsSelfDraw?: boolean;
  isKongReplacement?: boolean;
  IsKongReplacement?: boolean;
}

interface HandResultLoose {
  pattern?: string;
  method?: string;
  allPatterns?: ReadonlyArray<string>;
  isRobbedKong?: boolean;
  Pattern?: string;
  Method?: string;
  AllPatterns?: ReadonlyArray<string>;
  IsRobbedKong?: boolean;
  winResult?: WinResultLoose;
  WinResult?: WinResultLoose;
}

export class MoveLog {
  private client: Client;
  private mount: HTMLElement;
  private listEl: HTMLElement;
  private entries: LogEntry[] = [];

  // Dedup state.  Reset on every match-start.
  private seenDiscardTiles: Set<number> = new Set();
  private seenMeldTiles: Set<number> = new Set();
  private lastMatchSig: string = '';
  private lastPickupSig: string = '';
  private lastDiceSig: string = '';

  constructor(client: Client, mountId: string = 'move-log') {
    this.client = client;
    let mount = document.getElementById(mountId);
    if (!mount) {
      // Defensive: create the mount lazily if the host page didn't pre-place
      // the <aside>.  Keeps the module deployable in isolation.
      mount = document.createElement('aside');
      mount.id = mountId;
      document.body.appendChild(mount);
    }
    this.mount = mount;
    this.mount.innerHTML = '';

    const header = document.createElement('div');
    header.className = 'move-log-header';
    header.innerHTML = '<span class="move-log-title">📜 Move Log</span>' +
      '<button type="button" class="move-log-clear" title="Clear log">×</button>';
    this.mount.appendChild(header);

    this.listEl = document.createElement('div');
    this.listEl.className = 'move-log-entries';
    this.mount.appendChild(this.listEl);

    const clearBtn = header.querySelector('.move-log-clear') as HTMLButtonElement | null;
    if (clearBtn) {
      clearBtn.addEventListener('click', () => this.clear());
    }
  }

  start(): void {
    this.client.match.on('update', this.onMatch.bind(this));
    this.client.dice.on('update', this.onDice.bind(this));
    this.client.sound.on('update', this.onSound.bind(this));
    this.client.things.on('update', this.onThings.bind(this));
    this.client.claim.on('update', this.onClaim.bind(this));
    this.client.pickup.on('update', this.onPickup.bind(this));
    this.client.result.on('update', this.onResult.bind(this));
  }

  clear(): void {
    this.entries = [];
    this.listEl.innerHTML = '';
  }

  // --- helpers -------------------------------------------------------

  private nickForSeat(seat: number): string | null {
    const pid = this.client.seatPlayers[seat];
    if (!pid) return null;
    return this.client.nicks.get(pid) ?? null;
  }

  private seatLabel(seat: number | null | undefined): string {
    if (seat === null || seat === undefined || seat < 0) return 'System';
    const nick = this.nickForSeat(seat);
    return nick ? `Seat ${seat} (${nick})` : `Seat ${seat}`;
  }

  private nowStamp(): string {
    const d = new Date();
    const hh = String(d.getHours()).padStart(2, '0');
    const mm = String(d.getMinutes()).padStart(2, '0');
    const ss = String(d.getSeconds()).padStart(2, '0');
    return `${hh}:${mm}:${ss}`;
  }

  private push(entry: { seat: number | null; action: string; category: LogCategory; tile?: number }): void {
    const row: LogEntry = { ...entry, ts: this.nowStamp() };
    this.entries.push(row);
    if (this.entries.length > MAX_ENTRIES) {
      this.entries.splice(0, this.entries.length - MAX_ENTRIES);
    }
    this.renderEntry(row);
    this.trimDom();
    // Auto-scroll to newest.  Defer to next frame so the DOM has flushed.
    requestAnimationFrame(() => {
      this.listEl.scrollTop = this.listEl.scrollHeight;
    });
  }

  private renderEntry(entry: LogEntry): void {
    const row = document.createElement('div');
    row.className = `move-log-entry move-log-${entry.category}`;

    const ts = document.createElement('span');
    ts.className = 'move-log-ts';
    ts.textContent = `[${entry.ts}]`;

    const seat = document.createElement('span');
    seat.className = 'move-log-seat';
    seat.textContent = this.seatLabel(entry.seat) + ':';

    const action = document.createElement('span');
    action.className = 'move-log-action';
    action.textContent = ' ' + entry.action;

    row.appendChild(ts);
    row.appendChild(seat);
    row.appendChild(action);
    this.listEl.appendChild(row);
  }

  private trimDom(): void {
    while (this.listEl.childElementCount > MAX_ENTRIES) {
      const first = this.listEl.firstChild;
      if (!first) break;
      this.listEl.removeChild(first);
    }
  }

  private lastEntryForSeat(seat: number | null | undefined): LogEntry | null {
    if (seat === null || seat === undefined) return null;
    for (let i = this.entries.length - 1; i >= 0; i--) {
      const e = this.entries[i];
      if (e.seat === seat) return e;
    }
    return null;
  }

  // --- event handlers ------------------------------------------------

  private onMatch(entries: Array<[number, MatchInfo | null]>): void {
    for (const [key, value] of entries) {
      if (key !== 0 || !value) continue;
      const sig = `${value.dealer}|${value.honba}|${value.conditions?.gameType}`;
      if (sig === this.lastMatchSig) continue;
      const isFirst = this.lastMatchSig === '';
      this.lastMatchSig = sig;
      // Reset hand-scoped dedup state so a new hand starts fresh.
      this.seenDiscardTiles.clear();
      this.seenMeldTiles.clear();
      this.lastPickupSig = '';
      this.lastDiceSig = '';
      this.push({
        seat: null,
        action: isFirst
          ? `Match started — dealer is ${this.seatLabel(value.dealer)}`
          : `New hand — dealer is ${this.seatLabel(value.dealer)}`,
        category: 'match',
      });
    }
  }

  private onDice(entries: Array<[string | number, DiceInfo | null]>): void {
    for (const [, value] of entries) {
      if (!value) continue;
      const d1 = value.d1 ?? value.dice?.[0];
      const d2 = value.d2 ?? value.dice?.[1];
      if (d1 === null || d1 === undefined || d2 === null || d2 === undefined) continue;
      // Skip the "no roll" sentinel (matches game-ui.onDiceUpdate gating).
      if (value.state === 'ignore' && value.breakPoint === undefined) continue;
      const sig = `${d1}-${d2}-${value.breakPoint ?? ''}`;
      if (sig === this.lastDiceSig) continue;
      this.lastDiceSig = sig;
      const breakSuffix = value.breakPoint !== undefined
        ? ` → break @ col ${value.breakPoint}`
        : '';
      this.push({
        seat: null,
        action: `Dice rolled: ${d1} + ${d2} = ${d1 + d2}${breakSuffix}`,
        category: 'dice',
      });
    }
  }

  private onSound(entries: Array<[number, SoundInfo | null]>): void {
    // Sound is best-effort: we only fall through to a "discarded a tile" row
    // when the things-collection path didn't already log a tile-aware entry
    // for this seat as the most-recent row.  Avoids double-logging.
    for (const [, sound] of entries) {
      if (!sound) continue;
      if (sound.type !== SoundType.DISCARD) continue;
      if (sound.side === null) continue;
      const last = this.lastEntryForSeat(sound.side);
      if (last && last.category === 'discard') continue;
      this.push({ seat: sound.side, action: 'discarded a tile', category: 'discard' });
    }
  }

  private onThings(allEntries: Array<[string | number, ThingInfo | null]>): void {
    // FE-7/SC-2 (G19) — keep only ENTITLED real tiles (numeric key 0..107,
    // number OR numeric-string) and normalize the key to a number; opaque
    // hidden handles (`h_<base64url>`) never appear in the public discard/meld
    // slots this log records and are dropped (never used as a fallback face).
    const entries = allEntries
      .map(([k, info]): [number | null, ThingInfo | null] => [changshaParseRealTileId(k), info])
      .filter((e): e is [number, ThingInfo | null] => e[0] !== null);
    for (const [tileIdx, info] of entries) {
      if (!info) continue;
      const slot = info.slotName ?? '';
      const seatMatch = slot.match(/@(\d+)$/);
      const seat = seatMatch ? Number(seatMatch[1]) : null;

      if (slot.startsWith('discard.')) {
        // First time a given tile id lands in any discard slot this hand —
        // log it.  Subsequent re-positions within the discard tray are
        // ignored by the dedup set.
        if (this.seenDiscardTiles.has(tileIdx)) continue;
        this.seenDiscardTiles.add(tileIdx);
        // Concealed tiles arrive with face=null from the per-viewer privacy
        // mask; fall back to tile id so the glyph still resolves.
        const face = info.face ?? tileIdx;
        this.push({
          seat,
          action: `discarded ${tileGlyph(face)}`,
          tile: face,
          category: 'discard',
        });
      } else if (slot.startsWith('meld.')) {
        if (this.seenMeldTiles.has(tileIdx)) continue;
        this.seenMeldTiles.add(tileIdx);
        // Pung/Chow/Kong arrive as 3-4 back-to-back tile entries.  Only the
        // first tile per seat per recent burst produces a row; siblings are
        // suppressed by the cluster check below.
        if (this.recentMeldClusterSeat(seat)) continue;
        const face = info.face ?? tileIdx;
        this.push({
          seat,
          action: `formed a meld with ${tileGlyph(face)}`,
          tile: face,
          category: 'claim',
        });
      }
    }
  }

  private recentMeldClusterSeat(seat: number | null): boolean {
    if (seat === null) return false;
    // Look at the last 3 entries — if any are a meld for the same seat,
    // we're in the middle of a single claim burst and shouldn't re-log.
    const recent = this.entries.slice(-3);
    return recent.some(e => e.seat === seat && e.category === 'claim');
  }

  private onClaim(entries: Array<[string, ClaimWindowEntry | null]>): void {
    for (const [keyStr, value] of entries) {
      if (!value) continue;
      const seat = Number(keyStr);
      if (!Number.isFinite(seat)) continue;
      const v = value as unknown as ClaimEntryLoose;

      if (Array.isArray(v.available) && v.available.length > 0) {
        const opts = v.available.join('/');
        const tile = v.tile;
        const source = v.source;
        this.push({
          seat,
          action: `claim window — ${opts} on ${tileGlyph(tile)}` +
            (source !== undefined ? ` (from Seat ${source})` : ''),
          tile,
          category: 'claim',
        });
      } else if (v.action === 'claim' && v.type) {
        const isKong = v.type === 'Kong';
        this.push({
          seat,
          action: `claimed ${v.type}`,
          category: isKong ? 'kong' : 'claim',
        });
      }
      // 'pass' is intentionally not logged — too noisy.
    }
  }

  private onPickup(entries: Array<[string | number, PickupEntry | null]>): void {
    for (const [, value] of entries) {
      if (!value) continue;
      const phase = (value.phase ?? '').toString();
      const norm = phase.toLowerCase();
      // Suppress the noisy intermediate progress phases.
      if (norm.includes('progress')) continue;
      const sig = `${norm}|${value.seatIndex}|${value.count}`;
      if (sig === this.lastPickupSig) continue;
      this.lastPickupSig = sig;
      const label = this.pickupLabel(norm, value);
      if (!label) continue;
      this.push({ seat: value.seatIndex, action: label, category: 'pickup' });
    }
  }

  private pickupLabel(phase: string, p: PickupEntry): string | null {
    if (phase === 'rolldice' || phase === 'roll-dice') return 'time to roll the dice';
    if (phase === 'breakpointmarked' || phase === 'break-point-marked') {
      // Hudson rev2 — breakPoint is a typed { wallIndex, stackIndex, tileIndex } object;
      // format it via the pure helper (was `@ col ${p.breakPoint}` => "[object Object]").
      const where = formatBreakPointLabel(p.breakPoint);
      return `break-point marked${where ? ` @ ${where}` : ''}`;
    }
    if (phase === 'inplay' || phase === 'in-play') return null;
    if (phase.startsWith('pickup-r') || phase.startsWith('pickupround') ||
        phase === 'single' || phase === 'dealer-extra' || phase === 'dealerextra') {
      const human = phase.replace(/-/g, ' ').replace(/^pickupround/, 'round ');
      return `picking ${p.count} tile${p.count === 1 ? '' : 's'} (${human})`;
    }
    return null;
  }

  private onResult(entries: Array<[string, HandResultEntry | null]>): void {
    for (const [key, value] of entries) {
      if (key !== 'current') continue;
      if (!value) continue;

      const extras = value as HandResultEntry & HandResultLoose;
      const allPatterns: ReadonlyArray<string> =
        extras.allPatterns ?? extras.AllPatterns ??
        extras.winResult?.allPatterns ?? extras.WinResult?.AllPatterns ?? [];
      const method = (extras.method ?? extras.Method ??
        extras.winResult?.method ?? extras.WinResult?.Method ?? '').toString();
      const isRobbed = extras.isRobbedKong ?? extras.IsRobbedKong ??
        extras.winResult?.isRobbedKong ?? extras.WinResult?.IsRobbedKong ??
        (normalizePatternKey(method) === 'robbingKong');
      // Phase I Wave 2 — `winType` distinguishes self-draw / discard /
      // robbing-kong wins.  Backend wire vocabulary (camelCase) confirmed
      // in ChangshaToAutotableTranslator.WinMethodToWire.  Fall back to
      // `isRobbed` so robbingKong still emojis correctly on a pre-W2
      // payload that lacks `winType`.
      const winType = (
        extras.winResult?.winType ?? extras.WinResult?.WinType ?? ''
      ).toString();
      // Phase J Wave 3 — prefer Bishop's explicit `isSelfDraw` /
      // `isKongReplacement` bools when present (see
      // bishop-phase-j-wave-3.md Task 1).  Both fall back to the older
      // inference path (winType / KongReplacementWin pattern) when the
      // payload predates Wave 3.
      const isSelfDraw = extras.winResult?.isSelfDraw ?? extras.WinResult?.IsSelfDraw
        ?? (winType === 'selfDraw');
      const isKongReplacement = extras.winResult?.isKongReplacement
        ?? extras.WinResult?.IsKongReplacement
        ?? allPatterns.some(p => normalizePatternKey(p) === 'kongReplacementWin');

      switch (value.type) {
        case 'Hu': {
          // Phase J Wave 3 — sort patterns through the canonical Wave-3
          // ordering so the move-log row mirrors the result modal's chip
          // strip (HeavenlyHand → EarthlyHand → contextual big-wins →
          // structural big-wins → singleWait, then alphabetical).
          const filtered = Array.from(allPatterns)
            .filter(p => normalizePatternKey(p) !== 'standard')
            .sort(comparePatterns);
          const patternsLabel = filtered.map(shortPatternLabel).join(' / ');
          const multiplier = Math.max(1, Math.min(3, filtered.length || 1));
          // Highlight the most "contextual" pattern in the verb (matches the
          // spec example: "won by 河底捞鱼 (Last Discard) — [清一色] ×2").
          const contextual = filtered.find(p => {
            const k = normalizePatternKey(p);
            return k === 'heavenlyHand' || k === 'earthlyHand' ||
                   k === 'lastTileFromWall' || k === 'lastDiscardCatch' ||
                   k === 'kongReplacementWin';
          });
          let verb = 'won the hand';
          if (contextual) {
            verb = `won by ${shortPatternLabel(contextual)}`;
          } else if (isRobbed) {
            verb = 'won by 抢杠胡 (Robbing Kong)';
          }
          const otherPatterns = filtered.filter(p => p !== contextual);
          const tail = otherPatterns.length > 0
            ? ` — [${otherPatterns.map(shortPatternLabel).join(' / ')}]` +
              (multiplier > 1 ? ` ×${multiplier}` : '')
            : (patternsLabel && !contextual
                ? ` — [${patternsLabel}]${multiplier > 1 ? ` ×${multiplier}` : ''}`
                : '');
          // Phase I Wave 2 — single-glyph win-type prefix so a glance at
          // the move-log sidebar tells you *how* the hand was won.
          //   🀄 self-draw, 🎯 discard, ⚡ robbing-kong.
          // Phase J Wave 3 — fall back to Bishop's `isSelfDraw` /
          // `isKongReplacement` bools when `winType` is absent (e.g. an
          // older client connected to a Wave-3 backend).
          let prefix = '';
          if (winType === 'selfDraw' || (!winType && isSelfDraw)) {
            prefix = '🀄 ';
          } else if (winType === 'robbingKong' || (!winType && isRobbed)) {
            prefix = '⚡ ';
          } else if (winType === 'discard' || (!winType && !isSelfDraw && !isRobbed && allPatterns.length > 0)) {
            prefix = '🎯 ';
          }
          // Touch the `isKongReplacement` derivation so the lint pass
          // doesn't complain about the unused destructure — the contextual
          // verb above already picks up "kongReplacementWin" from the
          // patterns array, so the bool itself is informational only.
          void isKongReplacement;
          this.push({
            seat: value.winner,
            action: `${prefix}${verb}${tail}`,
            category: 'win',
          });
          break;
        }
        case 'ZhaHu': {
          this.push({
            seat: value.winner,
            action: 'declared a false win (诈胡)',
            category: 'win',
          });
          break;
        }
        case 'Draw': {
          this.push({
            seat: null,
            action: 'hand ended in a draw (流局)',
            category: 'win',
          });
          break;
        }
      }
    }
  }
}

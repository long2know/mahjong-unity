import { Quaternion, Vector3 } from "three";
export enum ThingType {
  TILE = 'TILE',
  STICK = 'STICK',
  MARKER = 'MARKER',
}

export const Size = {
  TILE: new Vector3(6, 9, 4),
  STICK: new Vector3(20, 2, 1),
  MARKER: new Vector3(12, 6, 1),
};

export interface Place {
  position: Vector3;
  rotation: Quaternion;
  size: Vector3;
  /** Display-only uniform mesh scale; size already includes it for raycasting. */
  scale?: number;
}

export interface ThingInfo {
  slotName: string;
  rotationIndex: number;
  claimedBy: number | null;
  heldRotation: { x: number; y: number; z: number, w: number };
  shiftSlotName: string | null;
  // Phase D: per-viewer privacy. Bishop's WS endpoint strips `face` to null
  // for concealed tiles in other players' hands. The bundle defends against
  // a backend that forgets to flip rotationIndex face-down by treating any
  // entry with face === null as "must render as a back" (world.ts coerces
  // rotation accordingly). When `face` is omitted entirely the field is
  // ignored — preserves Phase B sandbox behaviour.
  face?: number | null;
}

export interface MatchInfo {
  dealer: number;
  honba: number;
  conditions: Conditions;
}

export interface Game {
  gameId: string;
  num: number;
}

export enum DealType {
  INITIAL = 'INITIAL',
  WINDS = 'WINDS',
  HANDS = 'HANDS',
  UNSHUFFLED = 'UNSHUFFLED',
}

export type Fives = '000' | '111' | '121';
export type Points = '25' | '30' | '35' | '40' | '100';
export type DealMode = 'manual' | 'auto';

// Phase F — full variant matrix restored.  Changsha is the Phase F default
// (runtime-authoritative); the four upstream variants restore byte-identical
// behaviour with pwmarcz/autotable's relay mode.  FOUR_PLAYER_DEMO was dropped
// per Ripley Phase F design §1.1: it was a cosmetic dupe of FOUR_PLAYER and
// was never wired to a real flow upstream.
export enum GameType {
  CHANGSHA = 'CHANGSHA',           // Phase F default — runtime-authoritative
  FOUR_PLAYER = 'FOUR_PLAYER',     // Upstream Riichi 4-player
  THREE_PLAYER = 'THREE_PLAYER',   // Upstream Riichi 3-player (sanma)
  BAMBOO = 'BAMBOO',               // Upstream 2-player bamboo
  MINEFIELD = 'MINEFIELD',         // Upstream 2-player minefield
}

interface GameTypeMeta {
  points: Points;
  seats: Array<number>;
}

export const GAME_TYPES: Record<GameType, GameTypeMeta> = {
  CHANGSHA:     { points: '25',  seats: [0, 1, 2, 3] },
  FOUR_PLAYER:  { points: '25',  seats: [0, 1, 2, 3] },
  THREE_PLAYER: { points: '35',  seats: [0, 1, 2]    },
  BAMBOO:       { points: '100', seats: [0, 2]       },
  MINEFIELD:    { points: '25',  seats: [0, 2]       },
};

export interface Conditions {
  gameType: GameType;
  // Upstream fields — restored from pre-Phase-B for non-Changsha variants.
  // For Changsha these stay pinned (back=0, fives='000', points='25') by the
  // backend translator regardless of UI input (ChangshaToAutotableTranslator
  // line 204-209). They're carried in the type so the upstream Setup.setup
  // path can render the original Riichi scene unchanged.
  back: number;            // 0 or 1 — toggles tile-back colour cycle
  fives: Fives;            // red-five density: 000 / 111 / 121
  points: Points;          // starting points (drives addSticks)
  dealType: DealType;
  // Phase F additions.
  baseUnit: number;        // Authoritative creation-time Changsha scoring unit
  dealMode: DealMode;      // manual = click-driven pickup, auto = one-shot deal
}

export namespace Conditions {
  export function initial(): Conditions {
    return defaultsFor(GameType.CHANGSHA);
  }

  /** Phase F — variant-aware defaults. Changsha boots in manual-pickup mode
   *  with 108-tile / fives='000' (translator pins these anyway); upstream
   *  variants boot in auto-deal mode with their canonical Riichi defaults. */
  export function defaultsFor(gameType: GameType): Conditions {
    if (gameType === GameType.CHANGSHA) {
      return {
        gameType: GameType.CHANGSHA,
        back: 0,
        fives: '000',
        points: '25',
        dealType: DealType.HANDS,
        baseUnit: 1,
        dealMode: 'manual',
      };
    }
    return {
      gameType,
      back: 0,
      fives: '111',
      points: GAME_TYPES[gameType].points,
      dealType: DealType.HANDS,
      baseUnit: 1,
      dealMode: 'auto',
    };
  }

  export function equals(a: Conditions, b: Conditions): boolean {
    return a.gameType === b.gameType
        && a.back === b.back
        && a.fives === b.fives
        && a.points === b.points
        && a.dealType === b.dealType
        && a.baseUnit === b.baseUnit
        && a.dealMode === b.dealMode;
  }

  export function describe(ts: Conditions): string {
    if (ts.gameType === GameType.CHANGSHA) {
      return ts.dealMode === 'manual' ? 'Changsha (manual)' : 'Changsha (auto)';
    }
    const game = {
      'FOUR_PLAYER':  '4p',
      'THREE_PLAYER': '3p',
      'BAMBOO':       'b',
      'MINEFIELD':    'm',
      'CHANGSHA':     'Changsha', // unreachable — handled above
    }[ts.gameType];
    const fives = {'000': 'no red', '111': '1-1-1', '121': '1-2-1'}[ts.fives];
    return `${game}, ${fives}`;
  }
}

export interface MouseInfo {
  held: {x: number; y: number; z: number} | null;
  mouse: {x: number; y: number; z: number; time: number} | null;
}

export enum SoundType {
  DISCARD = 'DISCARD',
  STICK = 'STICK',
};

export interface SoundInfo {
  type: SoundType;
  seat: number;
  side: number | null;
}

export interface SeatInfo {
  seat: number | null;
}

export interface DiceInfo {
  dice: [number, number];
  state: 'ignore' | 'rolled';
  // Phase D additions (Bishop's `dice` collection extension): server can push
  // either legacy {dice,state} or a richer shape with explicit d1/d2 and the
  // computed break-point column. Both shapes coexist; game-ui.ts adapts.
  d1?: number;
  d2?: number;
  breakPoint?: number;
}

export interface ClaimWindowEntry {
  // Both absent is legacy; an advertised context must contain both valid fields.
  gameId?: string;
  stateVersion?: number;
  // Names the server pushes; render in 中文 primary + pinyin sublabel per
  // Default #5 (Vasquez Q5).
  available: Array<'Pung' | 'Chow' | 'Kong' | 'Hu'>;
  // Epoch ms when the claim window closes. Client auto-passes on expiry.
  deadline: number;
  // Discarding seat (0..3).
  source: number;
  // Physical tile ID being claimed (0..107 in Changsha).
  tile: number;
  // Legal physical partner pairs, private to the owning claimant.
  chowOptions?: number[][];
}

export interface OwnTurnEntry {
  gameId: string;
  stateVersion: number;
  hu: boolean;
  concealedKongs: number[][];
  addedKongs: number[];
}

export type OwnTurnCommand = {
  gameId: string;
  expectedVersion: number;
} & (
  { action: 'hu' } |
  { action: 'concealedKong' | 'addedKong'; tileIds: number[] }
);

export interface ActionRejectedEntry {
  action: string;
  reason: string;
  requestedSeat: number | null;
  ownedSeat: number | null;
}

// Stuck-turn fix (Hicks) — authoritative turn signal.
//
// Bishop's backend translator emits this on a dedicated ephemeral `turn`
// collection (singleton key "current") so the client can render the "your
// turn / waiting for seat N" affordance from AUTHORITATIVE runtime state
// instead of inferring it from `things` geometry (which lags a claim/deal by
// one UPDATE batch and briefly reads the wrong hand size — the root of the
// "post-claim looks stuck" perception).
//
// WIRE ASSUMPTION (documented for the Bishop integration seam):
//   ["turn", "current", { activeSeat, phase, awaitingDiscard }]
//     • activeSeat      — seat (0..3) whose action the runtime awaits, or null
//                         when no seat is on the clock (pre-deal, scoring,
//                         game-complete, or a claim window resolving).
//     • phase           — ChangshaPhase name (e.g. "AwaitingDiscard",
//                         "AwaitingClaim", "RollingDice", ...). Optional.
//     • awaitingDiscard — true when `activeSeat` must discard (phase ===
//                         AwaitingDiscard). Optional; the client also derives
//                         it from `phase` when omitted.
// The client tolerates alternate field spellings (activeSeatIndex/seat) so a
// small backend shape drift does not strand the affordance — see
// world.ts:onTurn / normalizeTurnEntry.
export interface TurnEntry {
  activeSeat: number | null;
  phase?: string;
  awaitingDiscard?: boolean;
}

export interface ScoreDelta {
  seat: number;
  delta: number;
}

export interface HandResultEntry {
  // Winning seat (0..3) — only meaningful when type === 'Hu' or 'ZhaHu'.
  winner: number;
  type: 'Hu' | 'Draw' | 'ZhaHu';
  score: Array<ScoreDelta>;
  hand: Array<number>;
  nextBanker: number;
  continuation?: HandResultContinuation | null;
}

export interface HandResultAckCommand {
  gameId: string;
  handNumber: number;
  resultToken: string;
}

export interface HandResultContinuation extends HandResultAckCommand {
  requiredSeats: number[];
  acknowledgedSeats: number[];
  waitingSeats: number[];
}

export interface DiceEntry {
  d1: number;
  d2: number;
  breakPoint: number;
}

// Phase F — server-pushed pickup affordance entry (singleton key=0).
// Drives the manual-pickup HUD, dice click visibility and per-tile click gate
// in world.ts. See Ripley Phase F design §2.4 for the protocol contract.
export interface BreakPointWire {
  // Hudson rev2 — the authoritative break address the backend emits for
  // `pickup.breakPoint` (AutotableProtocol.BreakPointWire). It is an OBJECT, not a
  // bare number: `wallIndex` is the physical wall/seat side (0..3), `stackIndex` is
  // the stack (column) within that wall, `tileIndex` the flat wall index of the break.
  wallIndex: number;
  stackIndex: number;
  tileIndex: number;
}

export interface PickupEntry {
  // 'rollDice' | 'breakPointMarked' | 'pickup-r1' | 'pickup-r2' | 'pickup-r3'
  //   | 'single' | 'dealer-extra' | 'inPlay'
  // Server emits Pascal-cased phase names (BreakPointMarked, PickupRound1, ...);
  // the bundle accepts either spelling — lowercase + dashes preferred for the
  // wire-format normalised by AutotableProtocol.PickupEntry, Pascal accepted
  // as a fallback so the bundle keeps working if the backend emits raw enum
  // names directly.
  phase: string;
  seatIndex: number;            // whose turn to click next (0..3)
  count: number;                // tiles to take this click (1 or 4)
  dealMode: DealMode;
  breakPoint?: BreakPointWire | null;   // authoritative break ADDRESS { wallIndex, stackIndex, tileIndex }
  wallIndex?: number | null;    // draw offset into the remaining wall (front = 0), NOT the break wall
  // Server-designated manual-pickup TRIGGER (SC-4, Ripley/Frost/Vasquez latest).
  // `targetSlots` is EXACTLY length 1 — the co-derived `Wall[0]` trigger slot;
  // `count` (above) carries the batch size and the server takes the whole batch
  // `Wall[0..count-1]`. Client match: `targetSlots.length===1 &&
  // targetSlots[0]===hovered.slot.name` (see world.ts wallTileInDesignatedSet /
  // changsha-mode-policy tileInDesignatedTrigger). FAILS CLOSED on
  // missing/empty/multiple — no any-wall/batch-set fallback (G17). No raw wall
  // ids on the wire (G19); the take carries only {seatIndex,count}. Optional
  // until Bishop co-emits the trigger.
  targetSlots?: string[];
}

export enum TileVariant {
  NO_LABELS = 'NO_LABELS',
  LABELS = 'LABELS',
}

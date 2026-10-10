/* eslint no-console: 0 */

import { EventEmitter } from 'events';

import { Entry } from '../server/protocol';

import { BaseClient, Game, type ClientMode } from './base-client';
import {
  ThingInfo,
  MatchInfo,
  MouseInfo,
  SoundInfo,
  SeatInfo,
  DiceInfo,
  ClaimWindowEntry,
  HandResultEntry,
  HandResultAckCommand,
  PickupEntry,
  TurnEntry,
  OwnTurnEntry,
  ActionRejectedEntry,
} from './types';
import { clearSession, saveSession } from './reconnect';
import {
  loadProfile,
  refreshProfile,
  snapshotStatsForGame,
  onProfile,
  initProfileHubBindings,
} from './profile';
import { getHubConnection } from './hub';
import { clearGameState, refreshGameState, setGameRoomConnected } from './game-state';


// Phase J Wave 2 — Server-pushed end-of-game payload.  Bishop's runtime
// emits this when the configured hand count (default 4) is exhausted.
//
// The exact key/field names live behind Bishop's memo
// (.squad/decisions/inbox/bishop-phase-j-wave-2.md); the bundle accepts a
// flexible superset so the modal renders regardless of which subset the
// backend ends up shipping:
//
//   • `isComplete`        — required boolean flag (true → game over).
//   • `totalScores`       — optional Map<seat,points> for the per-seat
//                           summary table.  When absent we derive totals
//                           client-side by summing the `result.current`
//                           updates seen during the match.
//   • `handHistory`       — optional Array<HandResultEntry> recap.  When
//                           absent we use the client-side history built
//                           from `result.current`.
//   • `maxHands`          — optional number echoing the runtime's
//                           configured limit so the modal can show "4/4".
//
// Tombstone (value=null) on a new game so the modal hides automatically.
export interface GameCompleteEntry {
  isComplete: boolean;
  totalScores?: Record<string, number>;
  handHistory?: Array<HandResultEntry>;
  maxHands?: number;
  // Tolerated PascalCase / alt-name fallbacks for forward-compat with
  // whatever wire vocabulary Bishop settles on:
  IsComplete?: boolean;
  TotalScores?: Record<string, number>;
  HandHistory?: Array<HandResultEntry>;
  MaxHands?: number;
  isGameComplete?: boolean;
  IsGameComplete?: boolean;
}


// Hicks playability iter2 — outbound shape of a human click-to-discard.
// Mirrors the backend's TryHandleDiscardActionAsync parser: it reads
// `tileId` (required) off the value object; the seat is taken from the
// collection entry key.
export interface DiscardCommand {
  tileId: number;
}


export class Client extends BaseClient {
  match: Collection<number, MatchInfo>;
  seats: Collection<string, SeatInfo>;
  // FE-7/SC-2 (G19) — the `things` key is `string|number`: numeric 0..107 for
  // real/entitled tiles, an OPAQUE per-viewer STRING handle for hidden tiles
  // (foreign concealed hands, ALL wall tiles, concealed kongs). Consumers MUST
  // branch on `typeof key === 'string'` (see changsha-mode-policy.isOpaqueHandle)
  // and never do tile-index arithmetic on a string handle.
  things: Collection<string | number, ThingInfo>;
  nicks: Collection<string, string>;
  mouse: Collection<string, MouseInfo>;
  sound: Collection<number, SoundInfo>;
  // dice was numeric-keyed in upstream; widened to string|number so Bishop's
  // `'current'` key (Phase D protocol) and the legacy local-deal key 0 both
  // work without a collection rename or a parallel event system.
  dice: Collection<string | number, DiceInfo>;
  // Phase D — Changsha protocol extensions emitted by AutotableWsEndpoint.
  claim: Collection<string, ClaimWindowEntry>;
  ownTurn: Collection<number, OwnTurnEntry>;
  actionRejected: Collection<string, ActionRejectedEntry>;
  result: Collection<string, HandResultEntry>;
  handResultAck: Collection<string, HandResultAckCommand>;
  // Phase F — manual-pickup state machine.  Singleton (key=0) carrying the
  // currently-expected pickup affordance pushed by ChangshaToAutotableTranslator.
  //   • Inbound  : ["pickup", 0, { phase, seatIndex, count, dealMode, breakPoint, wallIndex, targetSlots }]
  //   • Outbound : ["pickup", "rollDice", { seatIndex }]  (dealer clicks dice)
  //                ["pickup", "take",     { seatIndex, count }]  (player takes the batch; SC-4/G19 — no wall ids/slot/handle on the wire, server validates by phase/seat/count)
  // The collection is ephemeral — it never participates in connect-time
  // replay (the backend pushes the live phase on JOIN/NEW).  Keys widened to
  // string|number so the singleton snapshot and the command-shaped outbound
  // entries share one collection without a parallel event system.
  pickup: Collection<string | number, PickupEntry>;

  // Stuck-turn fix (Hicks) — authoritative turn signal from Bishop's
  // translator.  Singleton key="current": ["turn","current",{ activeSeat,
  // phase, awaitingDiscard }].  Ephemeral (pushed live on JOIN/NEW, never
  // replayed).  Read-only from the client's side; drives the turn banner +
  // the click-to-discard gate so the "your turn to discard" affordance is
  // authoritative and survives the one-batch `things` geometry lag after a
  // claim/deal.  Absent (empty) when the backend hasn't landed the signal —
  // the client then falls back to meld-aware geometry (see world.ts).
  turn: Collection<string, TurnEntry>;

  // Hicks playability iter2 — human click-to-discard.  Keyed by seat index
  // (0..3), value = { tileId: int }.  Outbound only (the backend interprets
  // the entry and emits the resulting tile move via the `things` collection;
  // there's no server-emitted form of this collection).  Ephemeral so the
  // entry doesn't survive a reconnect replay.
  discard: Collection<string | number, DiscardCommand>;

  // Phase J Wave 2 — singleton key="current" carrying the end-of-game
  // payload Bishop's runtime emits when MaxHands is exhausted.  We treat
  // this as a server-authoritative signal: when the singleton's
  // `isComplete`-style flag flips true the GameUi renders the end-of-game
  // modal.  Ephemeral by design — a fresh game wipes it on the new JOIN.
  gameComplete: Collection<string, GameCompleteEntry>;

  private legacySeat: number | null = 0;
  get seat(): number | null {
    return this.connectionMode === 'changsha' ? this.viewerSeat : this.legacySeat;
  }
  seatPlayers: Array<string | null> = new Array(4).fill(null);

  // Phase J Wave 4 — last seen `gameId` from a JOIN response.  Public so
  // the reconnect-token save path can read it without poking BaseClient's
  // private `game` reference (which TypeScript's compile-time access
  // check refuses to widen even with a cast).  Set on the `connect` event
  // handler below, cleared on user-initiated disconnect.
  lastGameId: string | null = null;
  serverSnapshotGameId: string | null = null;
  private roomMetadataSignature: string | null = null;

  constructor(mode: ClientMode = 'offline') {
    super(mode);

    // Make sure match is first, as it triggers reorganization of slots and things.
    this.match = new Collection('match', this, { sendOnConnect: true });

    this.seats = new Collection('seats', this, { unique: 'seat', perPlayer: true });
    this.things = new Collection('things', this, { unique: 'slotName', sendOnConnect: true });
    this.nicks = new Collection('nicks', this, { perPlayer: true });
    this.mouse = new Collection('mouse', this, { rateLimit: 100, perPlayer: true });
    this.sound = new Collection('sound', this, { ephemeral: true });
    this.dice = new Collection('dice', this, { ephemeral: true });
    this.claim = new Collection('claim', this, { ephemeral: true });
    this.ownTurn = new Collection('ownTurn', this, { ephemeral: true });
    this.actionRejected = new Collection('actionRejected', this, { ephemeral: true });
    this.result = new Collection('result', this);
    this.handResultAck = new Collection('handResultAck', this, { ephemeral: true });
    this.pickup = new Collection('pickup', this, { ephemeral: true });
    this.turn = new Collection('turn', this, { ephemeral: true });
    this.discard = new Collection('discard', this, { ephemeral: true });
    this.gameComplete = new Collection('gameComplete', this, { ephemeral: true });
    this.seats.on('update', this.onSeats.bind(this));
    this.on('update', (_entries, full) => {
      if (full) {
        const bound = this.connected()
          && (this.connectionMode !== 'changsha' || this.turn.get('current') !== null);
        this.serverSnapshotGameId = bound ? this.lastGameId : null;
        setGameRoomConnected(this.connectionMode === 'changsha' ? this.serverSnapshotGameId : null);
      }
      this.refreshRoomMetadata();
    });
    this.on('disconnect', () => {
      this.serverSnapshotGameId = null;
      this.roomMetadataSignature = null;
      setGameRoomConnected(null);
    });
    this.on('joining', () => {
      this.serverSnapshotGameId = null;
      this.roomMetadataSignature = null;
      setGameRoomConnected(null);
    });

    // Phase J Wave 4 — keep the reconnect session in sync with live
    // server state:
    //   • On `connect` (server JOIN ack): stash the freshly-issued
    //     gameId so subsequent saveReconnectSession() reads have a
    //     gameId to key the localStorage entry on.
    //   • On every `seats.update`: re-save (seat may have shifted via
    //     move-seat or kick).  Save is cheap; the localStorage write is
    //     fire-and-forget.
    this.on('connect', (game: Game) => {
      this.serverSnapshotGameId = null;
      this.lastGameId = game.gameId;
      this.saveReconnectSession();
      // JOINED may precede runtime binding. Room-scoped HTTP waits for the
      // authoritative FULL snapshot, after all collections have been applied.
      setGameRoomConnected(null);
      // Phase J Wave 5 — connect to Bishop's SignalR hub (idempotent
      // singleton) and load the player profile.  The hub's
      // OnConnectedAsync fires a `ProfileLoaded` event which
      // profile.ts is already subscribed to via
      // initProfileHubBindings.  We also kick off an explicit
      // loadProfile() so the local cache lands even if the hub
      // already pushed before our listener was installed.
      initProfileHubBindings();
      // The first bound FULL update populates per-table metadata for voice,
      // settings and owner-only controls; JOINED alone is not ready.
      this.roomMetadataSignature = null;
      void (async (): Promise<void> => {
        try {
          await getHubConnection();
          const profile = await loadProfile(game.playerId);
          // Mirror the local player's displayName into the WS-broadcast
          // nicks collection so every other surface (move log, seat
          // chips on remote tabs) renders the profile-edited name.
          this.nicks.set(game.playerId, profile.displayName);
          // Cache the pre-game stats snapshot so the post-game modal
          // can render a delta.
          snapshotStatsForGame();
        } catch {
          // Profile load is best-effort; lobby/UI degrade gracefully.
        }
      })();
    });
    this.seats.on('update', () => this.saveReconnectSession());

    // Phase J Wave 5 — propagate the local profile's displayName into
    // the WS-broadcast nicks collection on every profile update, so
    // the lobby + move-log + other surfaces re-render with the new
    // name immediately after Save in the profile drawer.
    onProfile((profile) => {
      const pid = this.playerId();
      if (pid !== null && pid !== '') {
        this.nicks.set(pid, profile.displayName);
      }
    });

    // Phase J Wave 5 — when the server pushes a gameComplete singleton
    // with the complete flag set, refresh the profile so the post-game
    // modal renders the updated stats with the correct delta against
    // the pre-game snapshot.
    this.gameComplete.on('update', () => {
      const cur = this.gameComplete.get('current');
      if (cur === null || cur === undefined) return;
      if (!readCompleteFlag(cur)) return;
      void refreshProfile();
    });
  }

  private onSeats(): void {
    const legacy = this.connectionMode !== 'changsha';
    if (legacy) this.legacySeat = null;
    this.seatPlayers.fill(null);
    for (const [playerId, seatInfo] of this.seats.entries()) {
      if (legacy && playerId === this.playerId()) {
        this.legacySeat = seatInfo.seat;
      }
      if (seatInfo.seat !== null) {
        this.seatPlayers[seatInfo.seat] = playerId;
      }
    }
  }

  private refreshRoomMetadata(): void {
    const variant = new URLSearchParams(window.location.search).get('variant') ?? 'changsha';
    if (!this.connected() || this.lastGameId === null || variant.toLowerCase() !== 'changsha'
      || this.serverSnapshotGameId !== this.lastGameId || this.turn.get('current') === null) return;
    const seats = Array.from(this.seats.entries()).map(([id, seat]) => [id, seat.seat]).sort();
    const signature = JSON.stringify([this.lastGameId, seats, this.turn.get('current')?.phase]);
    if (signature === this.roomMetadataSignature) return;
    this.roomMetadataSignature = signature;
    void refreshGameState(this.lastGameId);
  }

  // Phase J Wave 4 — persist the current (gameId, playerId, seat) to
  // localStorage so a refresh / clean tab-reopen within TOKEN_TTL_MS
  // can auto-rejoin.  No-op when we haven't completed a JOIN yet
  // (lastGameId === null).  Fire-and-forget — localStorage write
  // errors (privacy mode, quota) are swallowed by reconnect.ts.
  private saveReconnectSession(): void {
    if (!this.connected() || this.lastGameId === null) return;
    const playerId = this.playerId();
    if (playerId === null || playerId === '') return;
    saveSession({
      gameId: this.lastGameId,
      playerId,
      seat: this.seat,
      connectionId: null,
    });
  }

  // Phase J Wave 4 — explicit clear, invoked by client-ui.ts when the
  // user clicks Disconnect or New Game (intentional teardown — we
  // don't want to silently auto-rejoin a game the user just left).
  clearReconnectSession(): void {
    if (this.lastGameId !== null) {
      clearSession(this.lastGameId);
    }
    this.lastGameId = null;
    // Leaving a table does not disconnect the server-wide social lobby.
    clearGameState();
  }
}

// Phase J Wave 5 — read the "is complete" flag from a gameComplete
// payload tolerating Bishop's PascalCase / alt-name variants.  Mirrors
// the GameUi side's readCompleteFlag helper so we don't drift if the
// runtime wire shape shifts.
function readCompleteFlag(v: GameCompleteEntry): boolean {
  return Boolean(
    v.isComplete
    || v.IsComplete
    || v.isGameComplete
    || v.IsGameComplete,
  );
}

interface CollectionOptions {
  // Key that has to be kept unique. Enforced by the server.
  // For example, for 'things', the unique key is 'slotName', and if you
  // attempt to store two things with the same slots, server will reject the
  // update.
  unique?: string;

  // Updates will be sent to other players, but not stored on the server (new
  // will not receive them on connection).
  ephemeral?: boolean;

  // This is a collection indexed by player ID, and values will be deleted
  // when a player disconnect.
  perPlayer?: boolean;

  // The server will not send all updates, but limit to N per second.
  rateLimit?: number;

  // If we are initializing the server (i.e. we're the first player), send
  // our value.
  sendOnConnect?: boolean;
}

export class Collection<K extends string | number, V> {
  private kind: string;
  private client: Client;
  private map: Map<K, V> = new Map();
  private pending: Map<K, V | null> = new Map();
  private events: EventEmitter = new EventEmitter();
  private options: CollectionOptions;
  private intervalId: any | null = null;
  private lastUpdate: number = 0;

  constructor(
    kind: string,
    client: Client,
    options?: CollectionOptions) {

    this.kind = kind;
    this.client = client;
    this.options = options ?? {};

    this.client.on('update', this.onUpdate.bind(this));
    this.client.on('joining', this.clearPending.bind(this));
    this.client.on('connect', this.onConnect.bind(this));
    this.client.on('disconnect', this.onDisconnect.bind(this));
  }

  entries(): Iterable<[K, V]> {
    return this.map.entries();
  }

  get(key: K): V | null {
    return this.map.get(key) ?? null;
  }

  update(localEntries: Array<[K, V | null]>): void {
    if (!this.client.connected()) {
      for (const [key, value] of localEntries) {
        if (value !== null) {
          this.map.set(key, value);
        } else {
          this.map.delete(key);
        }
      }
      this.events.emit('update', localEntries, false);
    } else {
      const now = new Date().getTime();
      for (const [key, value] of localEntries) {
        this.pending.set(key, value);
      }
      if (!this.options.rateLimit || now > this.lastUpdate + this.options.rateLimit) {
        this.sendPending();
      }
    }
  }

  set(key: K, value: V | null): void {
    this.update([[key, value]]);
  }

  on(what: 'update', handler: (localEntries: Array<[K, V | null]>, full: boolean) => void): void;
  on(what: string, handler: (...args: any[]) => void): void {
    this.events.on(what, handler);
  }

  private onUpdate(entries: Array<Entry>, full: boolean): void {
    if (full) {
      this.map.clear();
    }
    const localEntries = [];
    for (const [kind, key, value] of entries) {
      if (kind === this.kind) {
        localEntries.push([key, value]);
        if (value !== null) {
          this.map.set(key as K, value);
        } else {
          this.map.delete(key as K);
        }
      }
    }
    if (full || localEntries.length > 0) {
      console.log(full ? 'full update' : 'update', this.kind, localEntries.length);
      this.events.emit('update', localEntries, full);
    }
  }

  private onConnect(game: Game, isFirst: boolean): void {
    this.clearPending();
    if (isFirst) {
      if (this.options.unique) {
        this.client.update([['unique', this.kind, this.options.unique]]);
      }
      if (this.options.ephemeral) {
        this.client.update([['ephemeral', this.kind, true]]);
      }
      if (this.options.perPlayer) {
        this.client.update([['perPlayer', this.kind, true]]);
      }
      if (this.options.sendOnConnect) {
        const entries: Array<Entry> = [];
        for (const [key, value] of this.map.entries()) {
          entries.push([this.kind, key, value]);
        }
        this.client.update(entries);
      }
    }
    if (this.options.rateLimit) {
      this.intervalId = setInterval(this.sendPending.bind(this), this.options.rateLimit);
    }
  }

  private onDisconnect(game: Game | null): void {
    this.clearPending();
    if (game && this.options.perPlayer) {
      const localEntries: Array<Entry> = [];
      for (const [key, value] of this.map.entries()) {
        localEntries.push([this.kind, key, null]);
        if (key === game.playerId) {
          localEntries.push([this.kind, 'offline', value]);
        }
      }
      this.onUpdate(localEntries, true);
    }
  }

  private clearPending(): void {
    this.pending.clear();
    if (this.intervalId !== null) {
      clearInterval(this.intervalId);
      this.intervalId = null;
    }
  }

  private sendPending(): void {
    if (this.pending.size > 0) {
      const entries: Array<Entry> = [];
      for (const [k, v] of this.pending.entries()) {
        entries.push([this.kind, k, v]);
      }
      this.client.update(entries);
      this.lastUpdate = new Date().getTime();
      this.pending.clear();
    }
  }
}

import { Vector3 } from "three";
import { Movement } from "./movement";
import { Client } from "./client";
import { readSpectatorFromUrl, readDealModeFromUrl, readVariantFromUrl } from "./client-ui";
import { mostCommon, rectangleOverlap, filterMostCommon, compareZYX } from "./utils";
import { MouseTracker } from "./mouse-tracker";
import { Setup } from './setup';
import type { RendererSink, Render } from "./renderer/renderer-sink";
import { SoundPlayer } from "./sound-player";
import {
  Conditions, ThingInfo, SoundType, Place, ThingType, Size, DealType,
  DiceInfo, GameType, PickupEntry, TurnEntry,
} from "./types";
import { Slot } from "./slot";
import { Thing } from "./thing";
import { hasExtraDiscardTile, HandSlotView } from "./hand-accounting";
import {
  isMyDiscardTurn as cueIsMyDiscardTurn,
  resolveActiveSeat as cueResolveActiveSeat,
  TurnCueInput,
} from "./turn-cue";
import {
  blocksLocalDeal as changshaBlocksLocalDeal,
  bootstrapDealFromUrl as changshaBootstrapDealFromUrl,
  changshaAllowsPointer,
  partitionThingEntries as changshaPartitionThingEntries,
  pickupTakeCommand as changshaPickupTakeCommand,
  pickupTriggerActionable as changshaPickupTriggerActionable,
  wallTileInteractive as changshaWallTileInteractive,
  isServerAuthoritative as changshaIsServerAuthoritative,
  parseRealTileId,
} from "./changsha-mode-policy";
import {
  reconcileHiddenBacks as sc2ReconcileHiddenBacks,
  reconcileRealVisibility as sc2ReconcileRealVisibility,
} from "./sc2-hidden-pool";
// Hicks 2026-05-26 — first-play P1 unblock (B4 / Vasquez P0-H).  When
// a click-to-discard is silently rejected we surface a one-line toast
// so the user knows why the click didn't fire instead of assuming the
// game is broken.  showToast is the shared `#toast-region` helper.
import { showToast } from "./toast";
import { ownHandTiles, sortHand, type HandSortMode, type HandTile } from './hand-sort';
import { presentLocalHand } from './table-fit';
import type { Interaction } from './renderer/renderer-contract';


interface Select extends Place {
  id: any;
}

const SHIFT_TIME = 100;

// Stuck-turn fix (Hicks) — tolerant reader for Bishop's `turn` signal.
// Canonical shape is `{ activeSeat, phase, awaitingDiscard }` (see
// types.ts:TurnEntry); we also accept `activeSeatIndex` / `seat` spellings and
// derive `awaitingDiscard` from the phase name so a small backend shape drift
// does not silently strand the affordance.  Returns null when the entry is
// absent or not turn-shaped (the caller then falls back to meld-aware
// geometry).
function normalizeTurnEntry(raw: unknown): TurnEntry | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  // Determine the active-seat field, treating an EXPLICIT null as meaningful
  // ("no seat on the clock" — Bishop emits it on every non-AwaitingDiscard
  // phase to retract the cue) vs an ABSENT field (not a turn entry).  Using
  // `in` rather than `??` is essential: `?? ` would collapse an explicit null
  // to the next candidate / undefined and defeat the retraction, letting stale
  // `things` geometry resurrect a discard cue during Scoring/claim windows.
  let cand: unknown;
  if ('activeSeat' in o) cand = o.activeSeat;
  else if ('activeSeatIndex' in o) cand = o.activeSeatIndex;
  else if ('seat' in o) cand = o.seat;
  else return null; // no active-seat field ⇒ not a turn entry
  let activeSeat: number | null;
  if (typeof cand === 'number') activeSeat = cand;
  else if (cand === null) activeSeat = null;
  else return null;
  const phase = typeof o.phase === 'string' ? o.phase : undefined;
  let awaitingDiscard: boolean | undefined;
  if (typeof o.awaitingDiscard === 'boolean') awaitingDiscard = o.awaitingDiscard;
  else if (phase !== undefined) awaitingDiscard = /awaitingdiscard/i.test(phase);
  return { activeSeat, phase, awaitingDiscard };
}

export class World {
  private setup: Setup;

  private objectView: RendererSink;

  slots: Map<string, Slot>;
  things: Map<number, Thing>;
  private pushes: Array<[Slot, Slot]>;

  private hovered: Thing | null = null;
  private selected: Array<Thing> = [];
  private mouse: Vector3 | null = null;

  private movement: Movement | null = null;
  private heldMouse: Vector3 | null = null;
  private rendererGesture: { anchor: Thing; actor: number; target: Slot | null } | null = null;
  mouseTracker: MouseTracker;

  private handMode: HandSortMode = 'suit';
  private handPointerDown = false;
  private handPlaces = new Map<number, Place>();
  private presentedHand: HandTile[] = [];
  private handSignature = '';
  private handDirty = true;
  private presentedSeat: number | null = null;
  private drawnTile: number | null = null;
  private handScale = 1;
  private handSnapshotSeat: number | null = null;
  private shadowSeat: number | null | undefined;

  soundPlayer: SoundPlayer;

  // Phase J Wave 1 — initial seat is 0 for the standard local-deal/non-
  // connected path (first-person view from seat 0).  For a page that
  // declares ?seat=-1 (spectator) we start at null so main-view.ts's
  // existing `fromTop = (seat === null)` branch puts the camera in the
  // top-down overhead pose from the very first frame, instead of briefly
  // flashing the seat-0 view until the WS sends the first seats update.
  // Non-spectator behaviour is unchanged.
  private authoritySeat: number | null = readSpectatorFromUrl() ? null : 0;
  private viewpointAuthoritySeat: number | null = this.authoritySeat;
  get seat(): number | null {
    return this.authoritySeat;
  }
  set seat(seat: number | null) {
    // The existing spectator-follow controls write world.seat. Treat their
    // request as a viewpoint only, never as permission to act for that seat.
    if (this.client?.connected() && this.client.seat === null) {
      this.viewSeat = seat;
    } else {
      this.authoritySeat = seat;
    }
  }
  // A disconnected seat is an authority revocation, not a spectator viewpoint.
  // Only presentation remembers this seat; input/privacy always use live authority.
  viewSeat: number | null = this.seat;

  static WIDTH = 174;

  private client: Client;

  conditions: Conditions;

  // Phase F — latest server-pushed pickup affordance.  Drives the "your turn
  // to pick" gate: when set and `seatIndex === this.seat`, drag-start on a
  // wall tile is intercepted and `pickup.take` is emitted instead.
  private pickup: PickupEntry | null = null;
  private onPickupChanged: ((p: PickupEntry | null) => void) | null = null;

  // Phase K Wave 9 — Commentary tile-ref → 3D mesh outline.  W8 wired a
  // 2D CSS overlay; W9 adds the actual mesh-outline pulse alongside it.
  // The CSS overlay stays for spec parity (`commentary-tile-ref-latency`
  // observes `data-highlight-tile-id`) and as a fallback when the
  // requested tile is currently inside an InstancedMesh batch that the
  // ObjectView hasn't promoted to a per-tile mesh yet.
  //
  // `highlightedThing` is the Thing currently flagged for outline; the
  // World re-evaluates it every frame in `updateViewThings` and clears
  // it once `HIGHLIGHT_DURATION_MS` elapses.  Re-entry (a second
  // highlight while the first is in flight) resets the timer — the
  // most-recently-clicked chip wins.
  private highlightedThing: Thing | null = null;
  private highlightStartMs: number = 0;
  static readonly HIGHLIGHT_DURATION_MS = 2000;

  // Hicks playability iter2 — throttle for stale-occupant log lines (see
  // onThings).  Static across world instances; we only ever construct one
  // World at a time but a module-level epoch makes the suppression robust
  // to remount scenarios.
  private static _lastSlotConflictLogMs: number = 0;

  // FE-7 / SC-2 (G19) — dedicated ANONYMOUS hidden-back Thing pool. Capacity for
  // 108 backs (worst case: the whole deck hidden pre-deal) IN ADDITION to the
  // 108 pre-baked REAL Things. Backs carry a sentinel typeIndex (0), always
  // render face-down, never infer a face/type from a handle, and are keyed by an
  // opaque handle for stable reuse (render/animation continuity + reconnect
  // stability). Built lazily on first SC-2 activation; re-built after a setup
  // rebuild reassigns `this.things`.
  private static readonly HIDDEN_BACK_BASE = 108;
  private static readonly HIDDEN_BACK_COUNT = 108;
  private hiddenBackPool: Array<Thing> | null = null;
  private handleToBack: Map<string, Thing> = new Map();
  // Blocker A (Bishop rev2) — the SC-2 back plan computed in the VACATE phase of
  // `onThings`, applied (placed) AFTER the entitled-real numeric loop so a back never
  // lands on a slot an entitled real is about to claim. Null off the SC-2 path.
  private _pendingBackPlan:
    | { place: Array<{ handle: string; slotName: string; rotationIndex?: number }>; release: Array<string> }
    | null = null;

  constructor(objectView: RendererSink, soundPlayer: SoundPlayer, client: Client) {
    this.setup = new Setup();
    this.slots = this.setup.slots;
    this.things = this.setup.things;
    this.pushes = this.setup.pushes;
    this.conditions = Conditions.initial();

    // Hicks 2026-05-27 — In manual deal mode the canonical pre-game render
    // is "all 108 tiles in the four walls, face-down".  The default
    // `dealType: HANDS` would lay 13 tiles into each seat's hand and
    // 14/15/13/13 tiles into the walls — that pre-WS state briefly shows
    // FACE-UP hands + asymmetric walls, which Stephen's 2026-05-27 directive
    // flagged as "scattered, not the canonical 4-simple-walls square".
    //
    // SC-1 / RC-13 (Ripley integration sub-contract) — bootstrap the deal
    // MODE from the URL at first paint (BEFORE any WS field arrives), so an
    // `?dealMode=auto` Changsha URL does NOT inherit the `defaultsFor('manual')`
    // default. The pre-WS placeholder is ALWAYS the canonical all-in-walls
    // square (INITIAL, 108 face-down) for BOTH auto and manual — the HANDS
    // local deal is never used, because it pre-guesses a deal client-side and
    // scatters `world.things` vs the server's contiguous `client.things` arc
    // (the "four half-walls" Vasquez/Frost flagged). The authoritative server
    // snapshot drives the REAL deal on JOIN (auto ⇒ dealt hands + arc atomically;
    // manual ⇒ the ceremony); `dealMode` is tracked verbatim so the handshake is
    // unchanged. Logic lives in the pure {@link bootstrapDealFromUrl} so it is
    // regression-locked browser-free.
    const { dealMode, dealType } = changshaBootstrapDealFromUrl(
      readDealModeFromUrl(), this.conditions);
    this.conditions = { ...this.conditions, dealMode, dealType };
    this.setup.setup(this.conditions);

    this.objectView = objectView;
    this.setupView();

    this.client = client;
    this.mouseTracker = new MouseTracker(this.client);

    this.soundPlayer = soundPlayer;

    this.client.seats.on('update', this.onSeat.bind(this));
    this.client.things.on('update', this.onThings.bind(this));
    this.client.match.on('update', this.onMatch.bind(this));
    this.client.dice.on('update', this.onDice.bind(this));
    this.client.pickup.on('update', this.onPickup.bind(this));
    const refreshHand = (): void => { this.handDirty = true; };
    this.client.things.on('update', refreshHand);
    this.client.seats.on('update', refreshHand);
    this.client.match.on('update', refreshHand);
    this.client.on('connect', refreshHand);
    this.client.on('disconnect', refreshHand);
    this.sendUpdate();
  }

  // TODO Phase D: banker rotation becomes server-authoritative; this client-side
  // dealer cycler must collapse to display-only once the changsha.banker
  // collection arrives.
  toggleDealer(): void {
    // FE-1 (UAT §9 mode boundary) — legacy relay control. `client.match.set`
    // is a LOCAL match write that `sendUpdate`-broadcasts to peers; in
    // server-authoritative Changsha the dealer is engine-owned, so this is
    // inert even if the DOM still exposes `#toggle-dealer` (Ferro hides it).
    if (changshaBlocksLocalDeal(this.conditions.gameType)) {
      return;
    }
    const match = this.client.match.get(0) ?? { dealer: 3, honba: 0, conditions: Conditions.initial()};
    match.dealer = (match.dealer + 1) % 4;
    this.client.match.set(0, match);
  }

  // Phase F — restored from upstream (98d4cca^).  Riichi honba counter
  // rotates through 0..7.  Hidden in Changsha (the button is `display: none`
  // when conditions.gameType === CHANGSHA).
  toggleHonba(): void {
    // FE-1 — same legacy relay local match write; inert in Changsha (honba is
    // a Riichi concept and the button is CSS-hidden, but no-op defensively).
    if (changshaBlocksLocalDeal(this.conditions.gameType)) {
      return;
    }
    const match = this.client.match.get(0) ?? { dealer: 0, honba: 0, conditions: Conditions.initial() };
    match.honba = (match.honba + 1) % 8;
    this.client.match.set(0, match);
  }

  private onSeat(): void {
    this.authoritySeat = this.client.seat;
    if (this.client.connectionMode !== 'changsha'
      || (this.client.connected() && this.client.serverSnapshotGameId !== null
        && this.client.serverSnapshotGameId === this.client.lastGameId)) {
      if (this.seat !== this.viewpointAuthoritySeat || this.seat !== null) {
        this.viewSeat = this.seat;
      }
      this.viewpointAuthoritySeat = this.seat;
    }
  }

  private onThings(allEntries: Array<[string | number, ThingInfo | null]>, full: boolean = false): void {
    const now = new Date().getTime();
    if (full) this.handSnapshotSeat = this.client.seat;

    // FE-7 / SC-2 (G19) — opaque hidden-tile handles arrive as STRING keys
    // (foreign concealed hands, ALL wall tiles, concealed kongs); real entitled
    // tiles use numeric 0..107. Partition by key TYPE (pure, tested
    // {@link partitionThingEntries}): the numeric `real` path below is UNCHANGED
    // and never does tile-index arithmetic on a handle; string `hidden` handles
    // render as ANONYMOUS BACKS from a dedicated pool.
    const { real: entries, hidden } = changshaPartitionThingEntries(allEntries);
    const liveReal = new Set(entries.filter(([, info]) => info !== null).map(([index]) => index));
    const removedReal = new Set(entries.filter(([, info]) => info === null).map(([index]) => index));
    for (const thing of this.things.values()) {
      if (thing.hiddenHandle !== null || (thing.type === ThingType.TILE && thing.index >= World.HIDDEN_BACK_BASE
        && this.hiddenBackPool?.includes(thing))) continue;
      if (removedReal.has(thing.index) || (full && !liveReal.has(thing.index))) {
        this.parkThing(thing);
        thing.hidden = true;
        thing.claimedBy = null;
        thing.shiftSlot = null;
        thing.sent = true;
        this.selected = this.selected.filter(selected => selected !== thing);
        if (this.hovered === thing) this.hovered = null;
      }
    }

    // SC-2 activates only once opaque handles are in play (this snapshot has a
    // hidden handle, or we already have backs assigned). Until then the numeric
    // path below is byte-for-byte the pre-SC-2 behaviour (no real-visibility
    // hiding, no back pool) — so a purely-numeric relay/legacy snapshot is
    // unaffected. Bishop's emission stays gated until this + dist integrate.
    const sc2Active = hidden.length > 0 || this.handleToBack.size > 0
      || (this.client.connectionMode === 'changsha' && this.hiddenBackPool !== null);
    if (sc2Active) {
      // Blocker A (Bishop rev2) — single-owner slot reconciliation with an ATOMIC
      // vacate-before-(re)bind discipline, so exactly ONE Thing owns each authoritative
      // wall/hand slot and `thing.slot` / `slot.thing` stay symmetric on both sides.
      //
      // The pre-fix code hid a non-entitled REAL (real.hidden=true) without vacating its
      // slot pointer, then placed a back into the SAME slot while only nulling `slot.thing`
      // (never the displaced real's `real.slot`). That left asymmetric pointers
      // (real.slot===S while S.thing===back), double-occupied slots, and stray face-up
      // center tiles that flickered between full/incremental snapshots. Now:
      //   1. VACATE first: conceal (hide + symmetric-park) every non-entitled real and
      //      release (symmetric-park + recycle) every revealed/absent back, freeing slots.
      this.ensureHiddenBackPool();
      const backPlan = this.computeHiddenBackPlan(hidden, full);
      this.reconcileRealThingVisibility(entries, full);
      this.releaseHiddenBacks(backPlan);
      // 2. BIND entitled reals via the numeric placement loop below (moveTo keeps both
      //    pointers symmetric; a displaced back is recycled there, relay path untouched).
      // 3. PLACE backs (this.placeHiddenBacks) runs AFTER that loop so a back never lands
      //    on a slot an entitled real is about to claim — see below.
      this._pendingBackPlan = backPlan;
    } else {
      this._pendingBackPlan = null;
    }

    // Hicks 2026-05-29 — Two-pass slot merge (Vasquez integration-audit
    // memo `.squad/decisions/inbox/vasquez-integration-audit.md`).
    //
    // The backend `things` broadcast frequently contains tile-swap
    // pairs and slot-takeover batches where the target slot is still
    // occupied at the start of the batch. Naïvely calling
    // `thing.moveTo(slot)` on those throws "slot not empty"; the old
    // logic guarded against the throw by silently skipping the move
    // (the "skipped stale moveTo" console.warn), which caused the
    // client view to drift away from the authoritative server state.
    // Vasquez's audit counted ~97 dropped moves in 5 minutes of play —
    // dropped moves included legitimate discards, breaking scenarios
    // A (manual discard round-robin), B (bot autoplay), and D (claim
    // window appearance).
    //
    // Earlier iterations had two pre-passes:
    //   (1) `thing.prepareMove()` on every batched thing — nulls the
    //       thing's CURRENT slot pointer.
    //   (2) Force-displace target-slot occupants ONLY when the
    //       occupant is NOT also in the batch, on the assumption that
    //       within-batch occupants were already cleared by pass 1.
    //
    // The assumption broke in the orphan-stale-ownership case:
    //   slot X.thing === Z   AND   Z.slot === some-other-slot
    // where Z's `.slot` was reassigned by a prior moveTo but slot X's
    // `.thing` pointer was never cleared (moveTo only writes
    // target.thing, never sources). When the new batch said
    // [W → X, Z → Y], pass 1 cleared Z.slot.thing (= some-other-slot,
    // NOT X) and pass 2 skipped force-displacing slot X (because Z
    // was "in the batch"). The placement loop then saw X still
    // occupied by Z and silently dropped W's move.
    //
    // Fix:
    //   1a. Pre-vacate every source slot (existing `prepareMove`).
    //   1b. Pre-vacate every target slot whose CURRENT occupant is a
    //       different tile, regardless of whether that tile is in the
    //       batch. Setting `slot.thing = null` is safe — if the
    //       displaced tile has its own batch entry, it will be re-
    //       bound by the placement loop below; if not, it becomes an
    //       orphan that the next UPDATE rebinds (the existing pattern
    //       — see `emitDiscard` orphan handling in this file).
    //   2.  Placement loop still defends against an unreachable
    //       "still occupied" branch with a throttled warning, but
    //       force-clears + places instead of silently skipping. Last-
    //       write-wins guarantees the client tracks the server.
    // Pass 1a — vacate each batched thing's CURRENT slot.
    for (const [thingIndex, thingInfo] of entries) {
      // TODO handle deletion
      if (thingInfo === null) {
        continue;
      }

      const thing = this.things.get(thingIndex);
      if (!thing) continue;
      thing.prepareMove();
    }

    // Pass 1b — vacate each batched target slot regardless of whether
    // the previous occupant is in this batch.  Replaces the older
    // "skip if occupant in batch" optimisation that misfired on
    // stale-ownership pointers (occupant's .slot was already
    // reassigned by a previous moveTo but THIS slot's .thing pointer
    // still referenced it).
    for (const [thingIndex, thingInfo] of entries) {
      if (thingInfo === null) continue;
      const slot = this.slots.get(thingInfo.slotName);
      if (!slot || slot.thing === null) continue;
      if (slot.thing.index === thingIndex) continue;
      // Blocker A (Bishop rev2) — when the displaced occupant is an anonymous SC-2
      // BACK (identified by a non-null hiddenHandle — REAL tiles and every relay/legacy
      // Thing always have hiddenHandle===null, so this branch is inert off the SC-2 path),
      // an entitled real is REVEALING into its slot. Recycle the back atomically (park +
      // drop its handle mapping) so it can never linger double-occupying the slot on an
      // incremental reveal that carried no explicit release tombstone.
      if (slot.thing.hiddenHandle !== null) {
        this.recycleBack(slot.thing);
      }
      slot.thing = null;
    }

    for (const [thingIndex, thingInfo] of entries) {
      if (thingInfo === null) {
        continue;
      }

      const thing = this.things.get(thingIndex);
      if (!thing) continue;
      const slot = this.slots.get(thingInfo.slotName);
      if (!slot) continue;
      // Phase D — tile face privacy. Bishop's WS endpoint strips `face` to
      // null on tiles concealed from this viewer. The bundle defends against
      // a backend that forgets to flip rotationIndex by coercing the visible
      // rotation to a face-down index for **hand slots only**.
      //
      // Hicks 2026-05-27 — RESTRICTED to `slot.group === 'hand'` (was
      // unconditional on any slot with multiple rotations).  The previous
      // "pick the last rotation index" heuristic was tied to the hand
      // slot's `[STANDING, FACE_UP, FACE_DOWN]` shape — last = FACE_DOWN.
      // For the WALL slot (`[FACE_DOWN, FACE_UP]`) last = FACE_UP, so
      // the guard FLIPPED walls face-UP whenever Bishop's filter stripped
      // `face` for foreign seats — which is every wall tile when viewerSeat
      // is null (spectator/unseated) or != the wall's owning seat.  Same
      // miscarriage hit DISCARD (`[FACE_UP, FACE_UP_SIDEWAYS, FACE_DOWN,
      // FACE_DOWN_SIDEWAYS]` → last = FACE_DOWN_SIDEWAYS) so other
      // seats' discards rendered as upside-down sideways tiles.
      //
      // The backend already authors the correct rotation for every non-
      // hand slot (wall=FACE_DOWN, discard=FACE_UP, exposed meld=FACE_UP,
      // concealed kong=FACE_DOWN), and explicitly forces hand-slot rotation
      // to FACE_DOWN when stripping face (see AutotableWsEndpoint.
      // FilterEntriesForViewer → StripFace with forceHandFaceDown=true).
      // Trust the backend's authored rotation for everything except the
      // hand-slot belt-and-suspenders guard.
      //
      // R-B / G6 (own-hand face-up) is a BACKEND concern, ALREADY FIXED
      // server-side by BE-5 (Ripley verified in uat-backend): the endpoint
      // rebinds `connection.ViewerSeat = seatIndex` on TakeSeat and re-projects a
      // full snapshot, so the translator authors the owner's own hand FACE-UP
      // per-viewer (RC-2 was ViewerSeat==null ⇒ every hand treated as foreign).
      // This client override is therefore a REDUNDANT, consistent fallback — NOT
      // the fix. Kept as belt-and-suspenders so a pre-BE-5 / relay backend that
      // strips or forgets to flip own-hand rotation still shows the local seat's
      // hand face-up (hand rotations [STANDING, FACE_UP, FACE_DOWN] ⇒ index 1),
      // and a face-stripped foreign hand stays face-down.
      let rotationIndex = thingInfo.rotationIndex;
      const isLocalSeatHand =
        slot.group === 'hand' &&
        this.seat !== null &&
        slot.seat === this.seat;
      if (isLocalSeatHand) {
        rotationIndex = 1;
      } else if (
        thingInfo.face === null &&
        slot.group === 'hand' &&
        slot.rotations.length > 1
      ) {
        rotationIndex = slot.rotations.length - 1;
      }
      // Hicks 2026-05-29 — defensive last-line guard.  After the two-
      // pass slot merge above, this branch SHOULD be unreachable for
      // any well-formed backend batch.  If we still see an occupied
      // target slot, the batch probably double-targets the slot (two
      // entries → same slotName).  Prefer last-write-wins so the
      // client tracks the server, and emit a throttled warning so the
      // condition is visible without spamming the console.
      if (slot.thing !== null && slot.thing !== thing) {
        if (now - World._lastSlotConflictLogMs > 1000) {
          World._lastSlotConflictLogMs = now;
          console.warn(
            `autotable: forcing stale moveTo ${thing.index} -> ${slot.name}`,
            `(occupant=${slot.thing.index})`,
          );
        }
        // Blocker A (Bishop rev2) — recycle a displaced SC-2 back (inert off the SC-2
        // path: hiddenHandle is always null for real/relay Things) so a reveal can never
        // leave the back double-occupying the slot.
        if (slot.thing.hiddenHandle !== null) {
          this.recycleBack(slot.thing);
        }
        slot.thing = null;
      }
      thing.moveTo(slot, rotationIndex);
      thing.hidden = false;
      thing.sent = true;

      // #119 (Hicks): normalise claimedBy to the `number | null` invariant.
      // The server OMITS `claimedBy` for unclaimed tiles, so it deserialises
      // to `undefined` on the wire (108 of 109 things in a fresh Changsha
      // deal).  Assigning it verbatim left `thing.claimedBy === undefined`,
      // which fails every `claimedBy === null` gate in this file — most
      // critically `toSelect()`, whose result seeds the mouse-ui raycast
      // targets.  The upshot: only the single tile the server happened to
      // send an explicit `null` for was hoverable/selectable, so a human
      // could not click ANY of their 14 hand tiles (no hover → no
      // click-to-discard).  `?? null` coerces undefined → null while
      // preserving a real seat index (0 must NOT collapse to null, so this
      // is `??`, never `||`).
      thing.claimedBy = thingInfo.claimedBy ?? null;
      thing.heldRotation.set(
        thingInfo.heldRotation.x,
        thingInfo.heldRotation.y,
        thingInfo.heldRotation.z,
        thingInfo.heldRotation.w,
      );

      const shiftSlot = thingInfo.shiftSlotName ? this.slots.get(thingInfo.shiftSlotName)! : null;
      if (thing.shiftSlot !== shiftSlot) {
        thing.lastShiftSlot = thing.shiftSlot;
        thing.lastShiftSlotTime = now;
        thing.shiftSlot = shiftSlot;
      }
    }
    // Blocker A (Bishop rev2) — PLACE the anonymous backs LAST, after every entitled
    // real has claimed its slot via moveTo. By construction a physical tile is EITHER an
    // entitled numeric real OR a hidden back for a viewer (never both), so a back never
    // targets a slot an entitled real just claimed; placing backs last (with a symmetric
    // vacate of any stale occupant) guarantees exactly one Thing per authoritative slot.
    if (this._pendingBackPlan !== null) {
      this.placeHiddenBacks(this._pendingBackPlan);
      this._pendingBackPlan = null;
    }
    this.checkPushes();
    this.sendUpdate();
  }

  /**
   * FE-7 / SC-2 / G19 — lazily build the anonymous hidden-back pool: {@link
   * HIDDEN_BACK_COUNT} face-down back Things (sentinel typeIndex 0, `hidden=true`,
   * reserved indices from {@link HIDDEN_BACK_BASE}) parked in the Setup-owned
   * off-table slot. Adding them to `this.things` makes them part of the tile
   * InstancedMesh (capacity grows to 216) and the render pass. Idempotent;
   * re-adds after a setup rebuild swapped `this.things`.
   *
   * The park slot itself is NOT created here: it is owned by {@link Setup} and
   * re-registered by every `addSlots()` rebuild, so it exists before any Thing
   * can target it and survives a `Setup.replace()` (see Setup.hiddenParkSlot).
   */
  private ensureHiddenBackPool(): void {
    if (this.hiddenBackPool !== null && this.things.has(World.HIDDEN_BACK_BASE)) {
      return;
    }
    const park = this.hiddenParkSlot;
    const pool: Array<Thing> = [];
    for (let i = 0; i < World.HIDDEN_BACK_COUNT; i++) {
      const back = new Thing(World.HIDDEN_BACK_BASE + i, ThingType.TILE, 0, park);
      back.hidden = true;
      this.things.set(back.index, back);
      pool.push(back);
    }
    // The park slot is multi-tenant; `new Thing(...)` wrote its own back-pointer
    // into it. Clear it so no parked object is mistaken for the slot's occupant.
    park.thing = null;
    this.hiddenBackPool = pool;
    this.handleToBack.clear();
    // Grow the tile InstancedMesh to include the 108 backs (contiguous indices
    // 108..215 after the 108 reals ⇒ instance capacity 216). Idempotent rebuild;
    // only runs on first activation (or after a setup rebuild dropped the pool).
    this.objectView.replaceThings(this.things);
  }

  /** The Setup-owned off-table park slot that holds every non-rendered Thing. */
  private get hiddenParkSlot(): Slot {
    return this.setup.hiddenParkSlot;
  }

  /**
   * FE-7 / SC-2 / G19 — reconcile the anonymous back pool against a `things`
   * snapshot's hidden (string-keyed) entries. Logic is the pure, tested
   * {@link reconcileHiddenBacks}; the two apply phases ({@link releaseHiddenBacks},
   * {@link placeHiddenBacks}) move the Thing objects. Blocker A (Bishop rev2) — split
   * into a plan-compute + two ordered apply phases so `onThings` can VACATE (release)
   * before the numeric entitled-real placement loop and BIND (place) after it, keeping
   * exactly one Thing per authoritative slot with symmetric `thing.slot` / `slot.thing`.
   * Stable reuse by handle ⇒ render continuity + reconnect stability; never derives a
   * face from the handle.
   */
  private computeHiddenBackPlan(
    hidden: Array<[string, ThingInfo | null]>,
    full: boolean,
  ): { place: Array<{ handle: string; slotName: string; rotationIndex?: number }>; release: Array<string> } {
    const infos = hidden.map(([handle, info]): [string, { slotName: string; rotationIndex?: number } | null] =>
      [handle, info === null ? null : { slotName: info.slotName, rotationIndex: info.rotationIndex }]);
    return sc2ReconcileHiddenBacks(infos, this.handleToBack.keys(), full);
  }

  /**
   * Blocker A (Bishop rev2) — symmetric-detach a Thing from its current slot and PARK
   * it off-screen. Only clears `slot.thing` when this Thing actually owns it, so it can
   * never steal a slot another Thing legitimately owns; leaves the caller to set
   * `hidden`. Parked backs/reals share the single off-screen park slot (its `.thing`
   * pointer is intentionally not authoritative for parked objects — they render nothing).
   *
   * The rotation is reset to 0 because the park slot has exactly one rotation:
   * a Thing parked while carrying a slot-specific rotation (a sideways discard,
   * a face-down hand tile) would otherwise index past `Slot.places` and make
   * `Thing.place()` undefined — which is what fed the per-frame
   * `Cannot read properties of undefined (reading 'x')` raycast cascade.
   */
  private parkThing(thing: Thing): void {
    const park = this.hiddenParkSlot;
    if (thing.slot !== park && thing.slot.thing === thing) {
      thing.slot.thing = null;
    }
    thing.slot = park;
    thing.rotationIndex = 0;
  }

  /** Blocker A — release a back to the free pool: symmetric-park + drop its handle map. */
  private recycleBack(back: Thing): void {
    this.parkThing(back);
    back.hidden = true;
    if (back.hiddenHandle !== null) {
      this.handleToBack.delete(back.hiddenHandle);
      back.hiddenHandle = null;
    }
  }

  /** Blocker A — VACATE phase: release every back the plan retired (revealed / absent). */
  private releaseHiddenBacks(plan: { release: Array<string> }): void {
    for (const handle of plan.release) {
      const back = this.handleToBack.get(handle);
      if (back) this.recycleBack(back);
    }
  }

  /** Blocker A — BIND phase: place/reuse a back per handle at its authoritative slot. */
  private placeHiddenBacks(
    plan: { place: Array<{ handle: string; slotName: string; rotationIndex?: number }> },
  ): void {
    for (const { handle, slotName, rotationIndex } of plan.place) {
      const slot = this.slots.get(slotName);
      if (!slot) continue; // unknown slot ⇒ skip (defensive)
      let back = this.handleToBack.get(handle);
      if (!back) {
        back = (this.hiddenBackPool ?? []).find(b => b.hiddenHandle === null && b.hidden);
        if (!back) continue; // pool exhausted (>108 concurrent) ⇒ skip (defensive)
        back.hiddenHandle = handle;
        this.handleToBack.set(handle, back);
      }
      // Stable-reuse move: vacate the back's PREVIOUS slot symmetrically first.
      if (back.slot !== slot && back.slot.thing === back) {
        back.slot.thing = null;
      }
      // Vacate the TARGET slot's current occupant symmetrically. A displaced REAL is being
      // concealed by this back ⇒ hide + park it so its real identity can never render off a
      // stale slot pointer; a displaced BACK (hiddenHandle set) is recycled.
      const occupant = slot.thing;
      if (occupant !== null && occupant !== back) {
        if (occupant.hiddenHandle !== null) {
          this.recycleBack(occupant);
        } else {
          this.parkThing(occupant);
          occupant.hidden = true;
        }
        slot.thing = null;
      }
      // Bind the back to the authoritative slot, face-down. NEVER a real typeIndex.
      back.slot = slot;
      back.rotationIndex = rotationIndex ?? 0;
      back.hidden = false;
      slot.thing = back;
    }
  }

  /**
   * FE-7 / SC-2 / G19 — hide every pre-baked REAL Thing (0..107) NOT present in
   * the entitled numeric snapshot, so a non-entitled real identity/face can
   * never render or leak; show the ones that are present. Pure plan via
   * {@link reconcileRealVisibility}. Only runs while SC-2 is active (opaque
   * handles in play), so the numeric/relay path is otherwise unchanged.
   */
  private reconcileRealThingVisibility(
    entries: Array<[number, ThingInfo | null]>,
    full: boolean,
  ): void {
    const present = entries.filter(([, info]) => info !== null).map(([id]) => id);
    const plan = sc2ReconcileRealVisibility(present, World.HIDDEN_BACK_COUNT, full);
    for (const id of plan.show) {
      const real = this.things.get(id);
      // Show it; the numeric placement loop rebinds it to its authoritative slot.
      if (real) real.hidden = false;
    }
    for (const id of plan.hide) {
      const real = this.things.get(id);
      if (!real) continue;
      // Blocker A (Bishop rev2) — conceal AND symmetric-park: vacate the real's slot on
      // BOTH sides so a back can own it cleanly and no stray face-up real lingers at a
      // now-concealed slot (the pre-fix code set hidden=true but left real.slot pointing
      // at the slot, producing double-occupancy + stray center tiles).
      real.hidden = true;
      this.parkThing(real);
    }
  }

  private onMatch(): void {
    const match = this.client.match.get(0);
    if (!match) {
      return;
    }

    // Hicks 2026-06-01 — Stephen's broken-deal-repro (2026-06-01T19:46Z
    // directive) traced the "walls flat / corner wedges / center score
    // panel" symptoms to a single root cause: the backend translator's
    // `ChangshaToAutotableTranslator.BuildMatch` hardcodes
    // `gameType="FOUR_PLAYER"` (legacy compat from when this bundle was
    // Riichi-only) and OMITS `dealMode` / `baseUnit`.  Naively pushing
    // that payload through `updateConditions(...)` flipped a Changsha
    // table into the upstream Riichi layout, which:
    //
    //   • Swapped the 108-tile Changsha catalog for the 136-tile Riichi
    //     one — 28 phantom tiles got scattered into wall slots that the
    //     backend never overwrites, producing the visual "flat single-
    //     row" walls with stray bumps.
    //   • Added 60 stick `Thing`s into corner `tray.*` slots — the gray
    //     triangular corner wedges in Stephen's screenshot.
    //   • Re-enabled the Riichi center score panel and the upstream
    //     "Dealer / Setup / 4p, no red" sidebar (both gated on
    //     `gameType !== CHANGSHA`).
    //
    // Backend translator change is Frost's lane (see
    // `.squad/decisions/inbox/hicks-broken-deal-fix.md`).  Frontend
    // mitigation: pin `gameType` to whatever the URL declared, and fall
    // back to the locally-known `dealMode` / `baseUnit` when the
    // backend conditions don't include them.  Behaviour for non-
    // Changsha variants is unchanged — the URL variant IS the upstream
    // gameType in those flows.
    const urlVariant = readVariantFromUrl();
    const pinnedGameType = (urlVariant ?? this.conditions.gameType) as Conditions['gameType'];
    const conditions: Conditions = {
      ...this.conditions,
      ...match.conditions,
      gameType: pinnedGameType,
    };

    if (!Conditions.equals(conditions, this.conditions)) {
      this.updateConditions(conditions);

      // Prevent selection persisting after deal
      this.selected.splice(0);
    }
  }

  private onDice(): void {
    // #119 (Hicks): dropped a dead `this.objectView;` expression that
    // tripped @typescript-eslint/no-unused-expressions.  Dice rendering is
    // owned by Center (center.ts subscribes to the same dice collection);
    // this World listener is an intentional no-op kept for update
    // fan-out symmetry.
  }

  // Phase F — runtime-pushed pickup affordance.  Stored locally so subsequent
  // drag-starts can intercept wall-tile clicks.  Game-ui registers a callback
  // via setPickupListener() so the HUD can render the "Take N" banner.
  private onPickup(): void {
    // The pickup collection is a singleton on key "current" (matching the
    // result/claim conventions in Phase F).  Outbound command keys
    // ('rollDice' / 'take' — ours to write, never to read back).  Only
    // "current" is authoritative.
    const entry = this.client.pickup.get('current') ?? null;
    this.pickup = entry;
    if (this.onPickupChanged) this.onPickupChanged(entry);
  }

  setPickupListener(fn: ((p: PickupEntry | null) => void) | null): void {
    this.onPickupChanged = fn;
  }

  /** True if the runtime currently expects this client's seat to click. */
  isMyPickupTurn(): boolean {
    return this.pickup !== null
        && this.seat !== null
        && this.pickup.seatIndex === this.seat
        && this.pickup.count > 0;
  }

  /** Convenience snapshot for the HUD. */
  currentPickup(): PickupEntry | null {
    return this.pickup;
  }

  /**
   * R-1 §D10 (Vasquez oracle) — is THIS wall tile interactive right now? Single
   * source of truth for wall interactivity in Changsha: only during a
   * manual-deal pickup phase this seat owes, and only for a tile in the
   * server-designated batch (see {@link wallTileInDesignatedSet}). The phase is
   * read from the AUTHORITATIVE `turn` signal (Bishop) — NOT the sticky `pickup`
   * entry — so a lingering pickup can't keep the wall live after
   * →AwaitingDiscard (R-1 §E3). AUTO ⇒ no pickup ⇒ always inert.
   */
  private wallTileInteractive(thing: Thing): boolean {
    if (thing.slot.group !== 'wall') return false;
    const signal = this.currentTurnSignal();
    const pickup = this.pickup;
    return changshaWallTileInteractive({
      variantIsChangsha: this.isChangsha(),
      dealModeIsManual: (pickup?.dealMode ?? readDealModeFromUrl()) === 'manual',
      pickupIsMine: this.isMyPickupTurn(),
      authoritativePhase: signal ? (signal.phase ?? null) : null,
     inDesignatedSet: this.wallTileInDesignatedSet(thing),
   });
  }

  /**
   * PICKUP-MATCH ADAPTER — the single place that maps a hovered Thing → the
   * pickup designation. Match key is PARENT-LOCKED / IMMUTABLE FINAL SC-4
   * (`ripley-SC4-FINAL-single-trigger-slot`): `hovered.slotName ===
   * pickup.targetSlots[0]` — the ONE exposed-end trigger (Wall[0]); the other
   * batch tiles are NOT clickable. `targetSlots` is EXACTLY length 1 and `count`
   * carries the batch (server takes `Wall[0..count-1]` atomically). It does NOT
   * infer/compare raw tile ids or physical slot FRAMES (F1-unsound) — it compares
   * the server-emitted public slot NAME; and it is NOT the SC-2 opaque handle
   * (handles govern hidden-tile RENDERING only — orthogonal). `batchPreviewSlots`
   * if present is display/animation-only — never gated on.
   *
   * The pure {@link tileInDesignatedTrigger} FAILS CLOSED on
   * missing/empty/multiple (no any-wall/batch-set fallback — fails G17). F2
   * (Ralph/Vasquez): the trigger must be the REACHABLE TOP-layer tile — a
   * covered/bottom slot fails closed via {@link pickupTriggerActionable}
   * (defense-in-depth beside canSelect coverage). Until Bishop co-emits the
   * trigger slot this stays inert (manual pickup intentionally not-yet-actionable);
   * Auto is always inert.
   */
  private wallTileInDesignatedSet(thing: Thing): boolean {
    return changshaPickupTriggerActionable(
      thing.slot.name, this.pickup?.targetSlots, this.wallTileCovered(thing));
  }

  /**
   * F2 — is this wall tile COVERED (occluded by the tile in the stack above)?
   * A 2-high wall stack's bottom (layer 0) tile has its `up` slot occupied and
   * is unreachable; only the top (layer 1) tile is the reachable draw frontier.
   */
  private wallTileCovered(thing: Thing): boolean {
    const up = thing.slot.links.up;
    return up !== undefined && up.thing !== null;
  }

  /**
   * Phase F — dealer clicks the dice button.  Server-side this transitions
   * the RollingDice phase into BreakPointMarked + emits the dice roll.  The
   * bundle does NOT optimistically render dice; it waits for the server's
   * `dice` UPDATE.
   */
  emitRollDice(): void {
    if (this.seat === null) return;
    // Cast: the pickup collection is typed as PickupEntry but command-shaped
    // outbound entries deliberately differ (see client.ts comment).
    this.client.pickup.set('rollDice', { seatIndex: this.seat } as any);
  }

  /**
   * Phase F — player takes their next N wall tiles.  Backend interprets the
   * `count` against `state.PickupSeatIndex` and the current phase's expected
   * count and takes the front batch `Wall[0..count-1]` itself.  The take carries
   * NO client-provided target (SC-4/G19 — see below).  Returns true if the emit
   * was attempted.
   */
  emitTakePickup(): boolean {
    if (!this.isMyPickupTurn()) return false;
    const seatIndex = this.seat!;
    const count = this.pickup!.count;
    // SC-4 / G19 / P0 — the take carries NO trusted client-provided target: no
    // raw wall ids, no slot, no handle. Only count-based `{seatIndex,count}` (see
    // the pure {@link pickupTakeCommand}). The server validates by phase/seat and
    // consumes the front `count` tiles; the tile moves solely via the server
    // `things` snapshot (no optimistic client move).
    this.client.pickup.set('take', changshaPickupTakeCommand(seatIndex, count) as any);
    return true;
  }

  /**
   * Hicks playability iter2 — human click-to-discard.  Sends a discard
   * command on behalf of the local player.  The backend validates phase +
   * active seat; an out-of-turn click is a no-op server-side.  The resulting
   * tile-move animation comes back through the normal `things` UPDATE
   * channel (matching the bot autoplay visual).
   *
   * Accepts either a {@link Thing} (canonical, validated against the local
   * hand slot) or a numeric tileId (Vasquez Gap 4 informational backdoor:
   * playtest harnesses and external drivers can pass the raw id without
   * needing to look up the Thing first).  When a numeric id is provided we
   * look up the Thing locally for the same hand-slot validation; if the
   * tile isn't in our local view we still emit the discard (the backend's
   * `TryHandleDiscardActionAsync` is the authoritative validator).
   */
  emitDiscard(tileOrId: Thing | number): boolean {
    if (this.seat === null) {
      this.surfaceDiscardRejection('Take a seat first');
      return false;
    }

    let tileId: number;
    let tile: Thing | undefined;
    if (typeof tileOrId === 'number') {
      tileId = tileOrId;
      tile = this.things.get(tileId);
    } else {
      tile = tileOrId;
      tileId = tile.index;
    }

    // Hicks 2026-05-29 — dealer-extra preview-tile fix (Bishop memo
    // `.squad/decisions/inbox/bishop-dealerextra-fix.md`).
    //
    // joinMatch runs an optimistic local `setup.deal('HANDS')` that
    // pre-places tiles into the hand slots (incl. `hand.extra@N` for
    // the dealer's 14th preview).  The backend then progressively
    // pushes its OWN tile-ids into `hand.0..13@N` during the pickup
    // ceremony.  Two failure shapes arise after the backend take:
    //
    //   (a) Orphan — `onThings` force-displaces the stale local-deal
    //       occupant via `Thing.prepareMove()`, which clears
    //       `slot.thing` but does NOT clear the displaced Thing's
    //       own `.slot` reference (see thing.ts §prepareMove).  The
    //       orphan still reports `slot.group === 'hand'` even though
    //       `slot.thing` is now the backend tile.
    //   (b) hand.extra preview — `hand.extra@N` is a frontend-only
    //       slot (the backend's `AutotableSlotMap.HandSlot` only emits
    //       `hand.0..13@N`).  The local-deal preview tile sits here
    //       unowned by the backend.
    //
    // A click on either phantom (or the playtest harness's M7
    // `claimedBy === null` fallback in
    // playtest-artifacts/playtest-playable-interaction.spec.mjs) would
    // emit `discard` carrying a tileId that the runtime still owns in
    // the wall, which `ChangshaStateMachine.Discard()` silently rejects
    // ("Tile X not in seat N's hand").  Remap (a) onto the slot's
    // authoritative occupant; reject (b) outright so the caller / UI
    // can pick a real hand tile instead.
    if (tile && tile.slot.thing !== null && tile.slot.thing !== tile) {
      const occupant = tile.slot.thing;
      if (occupant.slot.group === 'hand' && occupant.slot.seat === this.seat) {
        tile = occupant;
        tileId = occupant.index;
      }
    }
    if (tile && tile.slot.name.startsWith('hand.extra@')) {
      // Pre-deal preview tile — the runtime hasn't pushed the real
      // 14th tile yet.  Tell the user to wait for the deal to settle.
      this.surfaceDiscardRejection('Pick a tile after the deal completes');
      return false;
    }

    if (tile && (tile.slot.group !== 'hand' || tile.slot.seat !== this.seat)) {
      this.surfaceDiscardRejection('That tile is not in your hand');
      return false;
    }
    this.client.discard.set(this.seat, { tileId });
    return true;
  }

  // Hicks 2026-05-26 — first-play P1 unblock (B4 / Vasquez P0-H).
  // Throttled toast surface for silently-rejected discard attempts.
  // Without this, off-turn / pre-pickup hand clicks are NO-OPs and the
  // user has no idea why; with it, they see "Not your turn" / "Pick
  // from the wall first" for ~2 s and learn the rules by playing.
  // The 1500 ms guard prevents toast spam if the user keeps clicking.
  private lastDiscardToastAt = 0;

  /**
   * Stuck-turn fix (Hicks, Req 5) — authoritative, non-misleading reason a
   * hand-tile click did not discard.  Called only when the viewer clicked
   * their OWN hand tile but it is not their discard turn.  Prefers the true
   * cause (pickup owed / another seat on the clock) over the old blanket
   * "Not your turn", which was wrong during the post-claim geometry lag.
   */
  private describeWhyCannotDiscard(): string {
    if (this.isMyPickupTurn()) return 'Pick your tiles from the wall first';
    const active = this.effectiveActiveSeat();
    if (active !== null && active !== this.seat) {
      return `Waiting for Seat ${active} to play`;
    }
    return 'Not your turn yet';
  }

  private surfaceDiscardRejection(reason: string): void {
    const now = Date.now();
    if (now - this.lastDiscardToastAt < 1500) return;
    this.lastDiscardToastAt = now;
    try {
      showToast(reason, 'info', 2000);
    } catch {
      // Defensive: showToast falls back to console.warn if the region
      // is missing.  We never want a toast failure to break the click
      // path, so swallow any unexpected exception.
    }
  }

  /**
   * Hicks playability iter2 — heuristic: this seat holds an extra tile it
   * must discard to continue its turn (and no pickup affordance is pending).
   * Used to gate the click-to-discard intercept in {@link onDragStart} so a
   * casual drag of a tile in-place between draws doesn't accidentally discard.
   *
   * #147 (Hicks) — the old test counted only `hand`-group (concealed) tiles
   * and required `> 13`.  But Changsha routes every exposed/concealed meld
   * (Pung / Chow / exposed·concealed·added Kong) into the separate `meld`
   * slot group (`ChangshaToAutotableTranslator.BuildThings` →
   * `AutotableSlotMap.MeldSlot`).  After a claim the seat holds only 11
   * concealed tiles + a meld while the runtime has ALREADY handed it the turn
   * (`ActiveSeat == self`, `Phase == AwaitingDiscard`); the concealed-only
   * count read 11 → false → the sole real discard route (click-to-discard →
   * {@link emitDiscard}) was refused and the hand hard-stalled.
   *
   * Correct mahjong hand-size accounting: a "rest" hand is 13 tiles =
   * concealed + 3 per meld — each Kong's 4th physical tile is offset by its
   * replacement draw, so EVERY meld (Pung, Chow, or any Kong) counts as 3
   * toward the 13.  The seat owes a discard exactly when that total exceeds
   * 13, which holds for a normal draw (14 + 0) AND every post-meld case
   * (11 + 3, 8 + 6, …).  This reads the authoritative tile state the backend
   * pushed; `TryHandleDiscardActionAsync` stays the authoritative validator,
   * and the `> 13` bound means the intercept only fires on the seat's own
   * turn (a rest hand totals exactly 13), never out of turn.
   *
   * Relay variants (four_player/three_player/bamboo/minefield) have no server
   * rules engine and drive melds/discards by free drag, so the meld
   * contribution is gated to server-authoritative Changsha — relay keeps the
   * exact upstream concealed-only behaviour.
   */
  hasExtraHandTile(): boolean {
    if (this.seat === null) return false;
    if (this.isMyPickupTurn()) return false;

    // Delegate the tile accounting to the pure, dependency-free helper (see
    // hand-accounting.ts) so every meld variant is covered by a deterministic
    // contract test.  We hand it a lightweight slot view of each rendered
    // tile; the helper filters by seat + slot ownership.
    const entries: HandSlotView[] = [];
    for (const thing of this.things.values()) {
      const slot = thing.slot;
      entries.push({
        group: slot.group,
        seat: slot.seat,
        name: slot.name,
        ownsSlot: slot.thing === thing,
      });
    }
    return hasExtraDiscardTile(
      entries,
      this.seat,
      this.conditions.gameType === GameType.CHANGSHA,
    );
  }

  // ── Stuck-turn fix (Hicks) — authoritative turn signal + turn cue ─────────

  /** Bishop's authoritative turn signal, normalized, or null when absent. */
  private currentTurnSignal(): TurnEntry | null {
    return normalizeTurnEntry(this.client.turn.get('current') ?? null);
  }

  rendererTurnSignal(): TurnEntry | null {
    return this.currentTurnSignal();
  }

  private isChangsha(): boolean {
    return this.conditions.gameType === GameType.CHANGSHA;
  }

  /**
   * True when it is the local seat's turn to discard.  Delegates to the pure
   * {@link cueIsMyDiscardTurn}: Bishop's authoritative `turn` signal wins when
   * present (fires the cue regardless of `things` snapshot timing AND honours
   * the retraction so it can't linger past AwaitingDiscard); meld-aware
   * geometry is the defense-in-depth fallback consulted only when the signal is
   * absent (older backend / before the first `turn` UPDATE).
   */
  isMyDiscardTurn(): boolean {
    if (this.seat === null) return false;
    if (this.isMyPickupTurn()) return false;
    const signal = this.isChangsha() ? this.currentTurnSignal() : null;
    return cueIsMyDiscardTurn({
      mySeat: this.seat,
      activeSeatSignal: signal ? signal.activeSeat : undefined,
      awaitingDiscardSignal: signal ? signal.awaitingDiscard : undefined,
      myHasExtraTile: this.hasExtraHandTile(),
    });
  }

  /** Per-seat effective hand size (concealed + 3·melds), Changsha meld-aware. */
  private perSeatEffective(): number[] {
    const hand = [0, 0, 0, 0];
    const melds: Array<Set<string>> = [new Set(), new Set(), new Set(), new Set()];
    const meldAware = this.isChangsha();
    for (const thing of this.things.values()) {
      const slot = thing.slot;
      if (slot.thing !== thing) continue; // owned slots only
      const s = slot.seat;
      if (s === null || s < 0 || s > 3) continue;
      if (slot.group === 'hand') {
        if (!slot.name.startsWith('hand.extra@')) hand[s]++;
      } else if (meldAware && slot.group === 'meld') {
        const mi = slot.name.split('.')[1];
        if (mi !== undefined && mi !== '') melds[s].add(mi);
      }
    }
    return hand.map((h, i) => h + 3 * melds[i].size);
  }

  /**
   * Geometry heuristic for whose turn it is when Bishop's signal is absent:
   * the sole seat holding 14 effective tiles (owes a discard).  Ambiguity
   * (zero or more than one such seat — e.g. mid-claim-window) ⇒ null.
   */
  private activeSeatByGeometry(): number | null {
    if (!this.isChangsha()) return null;
    const eff = this.perSeatEffective();
    let found: number | null = null;
    for (let i = 0; i < 4; i++) {
      if (eff[i] > 13) {
        if (found !== null) return null;
        found = i;
      }
    }
    return found;
  }

  /** All four seats currently occupied (human or bot). */
  private allSeatsOccupied(): boolean {
    return this.client.seatPlayers.every(p => p !== null && p !== '');
  }

  /** A hand has been dealt / is live (tiles are in play), server-authoritative. */
  private isHandInProgress(): boolean {
    const signal = this.currentTurnSignal();
    if (signal !== null && signal.activeSeat !== null) return true;
    for (const thing of this.things.values()) {
      const slot = thing.slot;
      if (slot.thing !== thing) continue;
      if (slot.group === 'discard') return true;
      if ((slot.group === 'hand' || slot.group === 'meld') && slot.seat !== null) return true;
    }
    return false;
  }

  /** Authoritative-first active seat (0..3) or null — for waiting cues/toasts. */
  private effectiveActiveSeat(): number | null {
    const signal = this.isChangsha() ? this.currentTurnSignal() : null;
    return cueResolveActiveSeat({
      activeSeatSignal: signal ? signal.activeSeat : undefined,
      activeSeatByGeometry: this.activeSeatByGeometry(),
    });
  }

  /**
   * Snapshot of everything the pure {@link computeTurnCue} resolver needs.
   * game-ui consumes this to render the low-priority banner states (discard /
   * waiting / spectating / no-open-seat) after it has handled the claim +
   * pickup affordances.  Non-Changsha (relay) variants have no server turn
   * model, so we feed a minimal input that yields only discard-or-none — the
   * exact upstream free-drag behaviour.
   */
  getTurnCueInput(isSpectatorUrl: boolean): TurnCueInput {
    if (!this.isChangsha()) {
      return {
        mySeat: this.seat,
        isSpectatorUrl,
        inProgress: false,
        activeSeatSignal: undefined,
        awaitingDiscardSignal: undefined,
        myHasExtraTile: this.hasExtraHandTile(),
        activeSeatByGeometry: null,
        allSeatsOccupied: false,
      };
    }
    const signal = this.currentTurnSignal();
    return {
      mySeat: this.seat,
      isSpectatorUrl,
      inProgress: this.isHandInProgress(),
      activeSeatSignal: signal ? signal.activeSeat : undefined,
      awaitingDiscardSignal: signal ? signal.awaitingDiscard : undefined,
      myHasExtraTile: this.hasExtraHandTile(),
      activeSeatByGeometry: this.activeSeatByGeometry(),
      allSeatsOccupied: this.allSeatsOccupied(),
    };
  }

  updateConditions(conditions: Conditions): void {
    this.conditions = conditions;
    this.setup.replace(conditions);
    // Hicks 2026-06-01 round 2 — propagate variant flips into ObjectView so
    // it can hide / show the Riichi-only static scenery (stick trays + center
    // score readout).  No-op when the variant didn't change.
    this.objectView.setVariant(conditions.gameType);
    this.setupView();
  }

  private sendUpdate(full?: boolean): void {
    const entries: Array<[number, ThingInfo | null]> = [];
    if (full) {
      for (const thing of this.things.values()) {
        entries.push([thing.index, this.describeThing(thing)]);
        thing.sent = true;
      }
      for (const [index,] of this.client.things.entries()) {
        // FE-7/SC-2 — only numeric (real, locally-authored) keys reconcile
        // against `this.things` (Map<number>); opaque STRING handles are
        // server-owned and never authored/deleted from this local push.
        if (typeof index === 'number' && !this.things.has(index)) {
          entries.push([index, null]);
        }
      }
      this.client.things.update(entries);
    } else {
      for (const thing of this.things.values()) {
        if (!thing.sent) {
          const desc = this.describeThing(thing);
          if (JSON.stringify(desc) !== JSON.stringify(this.client.things.get(thing.index))) {
            entries.push([thing.index, desc]);
          }
          thing.sent = true;
        }
      }
      if (entries.length > 0) {
        this.client.things.update(entries);
      }
    }
  }

  private sendMouse(): void {
    if (this.seat !== null) {
      this.mouseTracker.update(this.mouse, this.heldMouse);
    }
  }

  private describeThing(thing: Thing): ThingInfo {
    return {
      slotName: thing.slot.name,
      rotationIndex: thing.rotationIndex,
      claimedBy: thing.claimedBy,
      heldRotation:
        {
          x: thing.heldRotation.x,
          y: thing.heldRotation.y,
          z: thing.heldRotation.z,
          w: thing.heldRotation.w,
      },
      shiftSlotName: thing.shiftSlot?.name ?? null,
    };
  }

  /**
   * Phase F — Deal entry point.  Game-ui passes any conditions overrides
   * (gameType / fives / points / dealMode) so the dropdown UI can mutate the
   * conditions before the deal happens in one transaction.
   */
  deal(dealType: DealType, overrides: Partial<Conditions> = {}): void {
    if (this.seat === null) {
      return;
    }

    // FE-1 (UAT §9 mode boundary) — in server-authoritative Changsha the client
    // must NEVER run the upstream local deal: `setup.deal(seat)` scatters all
    // 108 tiles client-side (the "four half-walls" HANDS-scatter) and the
    // `match`/`dice`/`things` `sendUpdate(true)` broadcast relays that corrupt
    // scene to peers.  Auto/manual start comes from the backend snapshot only.
    // The legacy relay Deal/Setup controls are therefore inert here even if the
    // DOM still exposes them.  (Non-Changsha relay variants keep the upstream
    // local-deal behaviour unchanged.)
    if (changshaBlocksLocalDeal((overrides.gameType ?? this.conditions.gameType))) {
      return;
    }

    for (const thing of this.things.values()) {
      thing.release();
    }
    this.selected.splice(0);
    this.checkPushes();

    // Phase F — upstream's deal toggles the tile-back colour every deal so
    // shuffling order is visible to humans.  Carry the toggle forward for
    // non-Changsha variants; Changsha pins back=0 always.
    const isChangsha = (overrides.gameType ?? this.conditions.gameType) === GameType.CHANGSHA;
    const nextBack = isChangsha ? 0 : (1 - this.conditions.back);

    const conditions: Conditions = {
      ...this.conditions,
      ...overrides,
      back: nextBack,
      dealType,
    };

    // Changsha has no honba (Vasquez §1.9); pin to 0 so the center renderer
    // never paints it.  Riichi variants get the upstream's running honba.
    const existingMatch = this.client.match.get(0);
    const honba = isChangsha ? 0 : (existingMatch?.honba ?? 0);
    const match = { dealer: this.seat, honba, conditions };

    this.updateConditions(conditions);
    const dice = this.setup.deal(this.seat);
    const diceInfo: DiceInfo = { dice, state: this.setup.usesDice() ? 'rolled' : 'ignore' };

    this.client.transaction(() => {
      this.client.match.set(0, match);
      this.client.dice.set(0, diceInfo);
      this.sendUpdate(true);
    });

    // P0 (Hudson/Vasquez 09:47) — NO client auto-drive of HUMAN-owned ceremony
    // turns. The former `driveManualDealChain` auto-emitted rollDice AND auto-took
    // every pickup, making the manual-deal pickup windows transient and defeating
    // the requested INTERACTIVE manual deal. It is removed outright: the human
    // clicks the roll-dice button (shown in the RollingDice phase by
    // game-ui.renderRollDiceButton) then clicks the exact SC-4 `targetSlots[0]`
    // once per batch (onDragStart manual-pickup intercept → emitTakePickup;
    // count-based `{seatIndex,count}`, no optimistic move; window stays stable
    // until the click). Only bots/server automation advances bot turns
    // (ChangshaGameRuntime.ScheduleBotIfNeededAsync). (This local path is inert
    // in Changsha anyway — deal() early-returns at the FE-1 gate — but the
    // auto-drive is deleted so it can never run for a human seat.)
  }

  /**
   * Phase F — restored from upstream (98d4cca^).  Force-replaces the per-seat
   * stick trays for a new starting-points selection.  Changsha is a no-op
   * (the variant doesn't render sticks).
   */
  resetPoints(points: Conditions['points']): void {
    if (this.conditions.gameType === GameType.CHANGSHA) return;

    for (const thing of this.things.values()) {
      thing.release();
    }
    this.selected.splice(0);
    this.checkPushes();

    const conditions: Conditions = { ...this.conditions, points };
    this.setup.replace(conditions, /*replacePoints*/ true);
    this.setupView();
    this.conditions = conditions;

    const existingMatch = this.client.match.get(0)
      ?? { dealer: 0, honba: 0, conditions: this.conditions };
    const match = { ...existingMatch, conditions };

    this.client.transaction(() => {
      this.client.match.set(0, match);
      this.sendUpdate(true);
    });
  }

  private isHolding(): boolean {
    if (this.seat === null) {
      return false;
    }

    for (const thing of this.things.values()) {
      if (thing.claimedBy === this.seat) {
        return true;
      }
    }
    return false;
  }

  onHover(id: any): void {
    if (!this.isHolding()) {
      this.hovered = id === null ? null : this.things.get(id as number)!;

      if (this.hovered !== null && !this.canSelect(this.hovered, [])) {
        this.hovered = null;
      }
    }
  }

  onSelect(ids: Array<any>): void {
    this.selected = ids.map(id => this.things.get(id as number)!);
    this.selected = this.selected.filter(
      thing => this.canSelect(thing, this.selected));

    if (this.selected.length === 0) {
      return;
    }

    this.selected = filterMostCommon(this.selected, thing => thing.slot.group + '@' + thing.slot.seat);
  }

  onMove(mouse: Vector3 | null): void {
    if ((this.mouse === null && mouse === null) ||
        (this.mouse !== null && mouse !== null && this.mouse.equals(mouse))) {
      return;
    }

    this.mouse = mouse;
    this.sendMouse();

    this.drag();
    this.sendUpdate();
  }

  private drag(): void {
    if (this.mouse === null || this.heldMouse === null) {
      return;
    }

    this.movement = new Movement();

    const held: Array<Thing> = [];

    for (const thing of this.things.values()) {
      if (thing.claimedBy === this.seat) {
        if (thing.shiftSlot !== null) {
          thing.release();
        } else {
          held.push(thing);
        }
      }
    }
    // this.things.filter(thing => thing.claimedBy === this.seat);
    held.sort((a, b) => compareZYX(a.slot.origin, b.slot.origin));

    for (let i = 0; i < held.length; i++) {
      const thing = held[i];
      const place = thing.place();
      const x = place.position.x + this.mouse.x - this.heldMouse.x;
      const y = place.position.y + this.mouse.y - this.heldMouse.y;

      const targetSlot = this.findSlot(x, y, place.size.x, place.size.y, thing.type);
      if (targetSlot === null) {
        this.movement = null;
        return;
      }
      this.movement.move(thing, targetSlot);
    }

    const relevantThings = [...this.things.values()].filter(thing =>
      thing.type === held[0].type
    );
    if (!this.movement.findShift(relevantThings, [
      slot => slot.links.shiftLeft ?? null,
      slot => slot.links.shiftRight ?? null,
    ])) {
      this.movement = null;
      return;
    }
    this.movement.rotateHeld();
    this.movement.applyShift(this.seat!);
  }

  private canSelect(thing: Thing, otherSelected: Array<Thing>): boolean {
    // FE-7 / SC-2 (G19) — a hidden pre-baked real or a free back pool object is
    // never selectable/raycastable. A PLACED anonymous back (hidden===false) is
    // raycastable via its slot so the SC-4 manual pickup can target it, but it is
    // never discardable/flippable (its slot group + the wall gate below decide).
    if (thing.hidden) {
      return false;
    }
    // FE-2 (UAT §9 input allowlist) — in server-authoritative Changsha only the
    // local seat's own hand tiles, and (during a manual pickup this seat owes,
    // for the server-designated batch tile) wall tiles, may be hovered/selected.
    // Wall tiles in Auto, wrong-phase/non-target wall tiles in Manual, other
    // seats' hands, discards, exposed melds and other runtime-owned things are
    // NON-interactive (no hover/select ⇒ no hold/drag/sendUpdate). Relay
    // variants keep the upstream free-select behaviour.
    if (changshaIsServerAuthoritative(this.conditions.gameType)) {
      const allowed = thing.slot.group === 'wall'
        ? this.wallTileInteractive(thing)                       // R-1 §D10
        : changshaAllowsPointer(
            { group: thing.slot.group, seat: thing.slot.seat },
            this.seat);
      if (!allowed) {
        return false;
      }
    }
    const upSlot = thing.slot.links.up;
    if (upSlot && upSlot.thing !== null) {
      if (otherSelected.indexOf(upSlot.thing) !== -1) {
        // the player is also selecting the tile above, let them pick it up
        return true;
      }
      if (upSlot.thing.claimedBy !== null) {
        // someone else is holding this tile
        return true;
      }
      return false;
    }
    return true;
  }

  rendererSnapshotReady(): boolean {
    if (this.client.connectionMode === 'offline') return !this.isChangsha();
    return this.client.connected() && this.client.lastGameId !== null
      && this.client.serverSnapshotGameId === this.client.lastGameId
      && (this.client.connectionMode !== 'changsha'
        || (this.client.turn.get('current') !== null && this.handSnapshotSeat === this.client.seat));
  }

  rendererCanSelect(thing: Thing, selection: readonly Thing[] = []): boolean {
    return this.rendererSnapshotReady() && this.seat !== null
      && this.things.get(thing.index) === thing && !thing.slot.offTable
      && thing.slot.thing === thing && thing.claimedBy === null && this.canSelect(thing, [...selection]);
  }

  rendererInteraction(thing: Thing, selection: readonly Thing[] = []): Interaction {
    const canSelect = this.rendererCanSelect(thing, selection);
    const relay = !this.isChangsha();
    const ownHand = thing.type === ThingType.TILE && thing.hiddenHandle === null
      && parseRealTileId(thing.index) !== null && thing.slot.group === 'hand'
      && thing.slot.seat === this.client.seat && !thing.slot.name.startsWith('hand.extra@')
      && thing.slot.thing === thing;
    return {
      canSelect,
      canDrag: canSelect && relay,
      canFlip: canSelect && relay,
      canDiscard: canSelect && !relay && ownHand && this.isMyDiscardTurn(),
      canPickup: canSelect && !relay && this.wallTileInteractive(thing),
    };
  }

  rendererCanRollDice(): boolean {
    const phase = this.currentTurnSignal()?.phase?.toLowerCase();
    return this.isChangsha() && this.rendererSnapshotReady() && this.seat !== null
      && this.seat === this.client.seat && this.conditions.dealMode === 'manual'
      && (phase === 'rollingdice' || phase === 'rolldice')
      && this.client.match.get(0)?.dealer === this.seat;
  }

  beginRendererDrag(anchor: Thing, selection: readonly Thing[]): boolean {
    if (this.isChangsha() || this.rendererGesture !== null || !selection.includes(anchor)
      || selection.some(thing => !this.rendererCanSelect(thing, selection)
        || thing.slot.group !== anchor.slot.group || thing.slot.seat !== anchor.slot.seat)) return false;
    this.onMove(anchor.place().position.clone());
    this.onSelect(selection.map(thing => thing.index));
    this.onHover(anchor.index);
    if (!this.onDragStart()) return false;
    this.rendererGesture = { anchor, actor: this.seat!, target: null };
    this.setHandPointerDown(true);
    return true;
  }

  dragRendererToSlot(slot: Slot): boolean {
    const gesture = this.rendererGesture;
    if (!gesture || !this.rendererSnapshotReady() || this.seat !== gesture.actor
      || !this.heldMouse || slot.offTable || this.slots.get(slot.name) !== slot
      || slot.type !== gesture.anchor.type || gesture.anchor.claimedBy !== gesture.actor) return false;
    const from = gesture.anchor.place().position;
    const to = slot.placeWithOffset(0).position;
    this.onMove(new Vector3(
      this.heldMouse.x + to.x - from.x, this.heldMouse.y + to.y - from.y, this.heldMouse.z,
    ));
    const accepted = this.movement?.get(gesture.anchor) === slot && this.canDrop();
    gesture.target = accepted ? slot : null;
    return accepted;
  }

  endRendererDrag(commit: boolean): boolean {
    const gesture = this.rendererGesture;
    if (!gesture) return false;
    const accepted = commit && gesture.target !== null && this.canDrop()
      && this.movement?.get(gesture.anchor) === gesture.target;
    if (accepted) this.onDragEnd();
    else this.dropInPlace();
    this.rendererGesture = null;
    this.setHandPointerDown(false);
    return accepted;
  }

  /** Revoke local input without authoring a stale relay UPDATE after a seat/room loss. */
  revokeRendererInput(): void {
    const actor = this.rendererGesture?.actor;
    if (actor !== undefined) {
      for (const thing of this.things.values()) if (thing.claimedBy === actor) {
        thing.claimedBy = null;
        thing.shiftSlot = null;
        thing.sent = true;
      }
    }
    this.rendererGesture = null;
    this.selected.splice(0);
    this.hovered = null;
    this.mouse = null;
    this.heldMouse = null;
    this.movement = null;
    this.handPointerDown = false;
    this.clearDrawnHandTile();
  }

  private findSlot(x: number, y: number, w: number, h: number, thingType: ThingType): Slot | null {
    const minOverlap = 1;
    let bestOverlap = minOverlap ;
    let bestSlot = null;

    // Empty slots
    for (const slot of this.slots.values()) {
      if (slot.type !== thingType) {
        continue;
      }

      // Off-table park slots hold non-rendered Things and are never a drop
      // target (their origin is off-scene, so an overlap test against them is
      // meaningless). Keeps relay drag-and-drop behaviour identical.
      if (slot.offTable) {
        continue;
      }

      if (slot.thing !== null && slot.thing.claimedBy !== this.seat) {
        // Occupied. But can it be potentially shifted?
        if (!slot.links.shiftLeft && !slot.links.shiftRight) {
          continue;
        }
      }
      // Already proposed for another thing
      if (this.movement?.hasSlot(slot)) {
        continue;
      }
      // The slot requires other slots to be occupied first
      if (slot.links.requires && slot.links.requires.thing === null) {
        continue;
      }

      const place = slot.placeWithOffset(0);

      const margin = Size.TILE.x / 2;
      const overlap1 = rectangleOverlap(
        x, y, w, h,
        place.position.x, place.position.y, place.size.x, place.size.y,
      );
      const overlap2 = rectangleOverlap(
        x, y, w + margin, h + margin,
        place.position.x, place.position.y, place.size.x + margin, place.size.y + margin,
      );
      const overlap = overlap1 + overlap2 * 0.5;
      if (overlap > bestOverlap) {
        bestOverlap = overlap;
        bestSlot = slot;
      }
    }
    return bestSlot;
  }

  onDragStart(): boolean {
    if (this.seat === null) {
      return false;
    }

    // Phase F — manual-pickup intercept.  When the runtime expects this seat
    // to pick the next N wall tiles, a drag-start on the server-designated batch
    // tile becomes a pickup.take emit instead of a free-drag.  We do NOT
    // optimistically move the tile — the backend will respond with a `things`
    // UPDATE that places the tile into the hand.  The wall-interactivity gate
    // (R-1 §D10) is the single source of truth: wrong-phase, non-target or
    // Auto-mode wall presses fall through to the inert block below.
    if (this.hovered !== null
        && this.wallTileInteractive(this.hovered)) {
      this.emitTakePickup();
      this.hovered = null;
      this.selected.splice(0);
      return false;
    }

    // Hicks playability iter2 — click-to-discard.  When it is the local
    // player's turn to discard and they click one of their own hand tiles,
    // treat the click as a single-action discard instead of a drag.  Matches
    // "playing in person" semantics: tap a tile, it goes to the discard area.
    // The backend validates phase + active-seat — an off-turn click is
    // silently dropped server-side.
    //
    // Stuck-turn fix (Hicks): gate on the AUTHORITATIVE {@link isMyDiscardTurn}
    // (Bishop's turn signal ∪ meld-aware geometry) rather than geometry alone,
    // so a legal post-claim discard is offered the instant the runtime hands
    // us the turn — even while the `things` snapshot confirming our 14th tile
    // is still one UPDATE batch behind (the "post-claim looks stuck" case).
    //
    // Hicks 2026-05-26 — first-play P1 unblock (B4 / Vasquez P0-H).  When it is
    // NOT our turn the legacy path silently fell through to drag; we surface a
    // one-line toast so the user learns why the click didn't discard instead of
    // perceiving a NO-OP.  Wording is now derived from authoritative state
    // (Req 5) so we never say "Not your turn" when it demonstrably is.  We
    // still let the drag fall through so power-users can re-order tiles.
    if (this.isChangsha() && this.hovered !== null
        && this.hovered.slot.group === 'hand'
        && this.hovered.slot.seat === this.seat) {
      if (this.isMyDiscardTurn()) {
        const tile = this.hovered;
        this.emitDiscard(tile);
        this.hovered = null;
        this.selected.splice(0);
        return false;
      }
      // Inform — but don't intercept; the drag-fallthrough still runs so
      // existing re-ordering UX is unchanged.
      this.surfaceDiscardRejection(this.describeWhyCannotDiscard());
    }

    // FE-2 (UAT §9 input allowlist) — defense-in-depth beyond canSelect: in
    // server-authoritative Changsha the ONLY sanctioned pointer actions are the
    // discard intercept (own hand tile on your turn) and the manual-pickup
    // intercept (wall tile while you owe a pickup), both handled above. Anything
    // that reaches here (wall in Auto, off-turn hand tile, other-seat / discard
    // / meld tiles) must NOT enter the upstream free-drag: no hold, no local
    // `things` mutation, no `sendUpdate`, no visual movement. The server's next
    // snapshot remains the sole author of tile positions.
    if (changshaIsServerAuthoritative(this.conditions.gameType)) {
      this.hovered = null;
      this.selected.splice(0);
      return false;
    }

    if (this.hovered !== null && !this.isHolding()) {
      let toHold;
      if (this.selected.indexOf(this.hovered) !== -1) {
        toHold = [...this.selected];
      } else {
        toHold = [this.hovered];
        this.selected.splice(0);
      }

      toHold = toHold.filter(thing => thing.claimedBy === null);

      for (const thing of toHold) {
        thing.hold(this.seat);
      }
      this.hovered = null;
      this.heldMouse = this.mouse;

      this.drag();
      this.sendMouse();
      this.sendUpdate();

      return true;
    }
    return false;
  }

  onDragEnd(): void {
    if (this.isHolding()) {
      if (this.heldMouse !== null && this.mouse !== null &&
          this.heldMouse.equals(this.mouse)) {

        // No movement; unselect
        this.selected.splice(0);
        this.dropInPlace();
        // if (this.hovered !== null) {
        //   this.selected.push(this.hovered);
        // }
      } else if (this.canDrop()) {
        // Successful movement
        this.drop();
      } else {
        this.dropInPlace();
      }
    }

  }

  onFlip(direction: number, animated?: boolean): void {
    if (this.isHolding()) {
      return;
    }

    if (this.selected.length > 0) {
      const rotationIndex = mostCommon(this.selected, thing => thing.rotationIndex)!;
      const toFlip = [];
      for (const thing of this.selected) {
        if (this.selected.length === 1 || thing.slot.canFlipMultiple) {
          toFlip.push(thing);
        }
      }
      if (toFlip.length > 1 && animated) {
        toFlip.sort((a, b) => a.slot.name.localeCompare(b.slot.name, undefined, { numeric: true }));
        this.flipAnimated(toFlip, 0, rotationIndex + direction);
      } else {
        for (const thing of toFlip) {
          thing.flip(rotationIndex + direction);
        }
        this.checkPushes();
        this.selected.splice(0);
      }
    } else if (this.hovered !== null) {
      this.hovered.flip(this.hovered.rotationIndex + direction);
      this.sendUpdate();
      this.checkPushes();
    }
    this.sendUpdate();

  }

  private flipAnimated(things: Array<Thing>, i: number, rotationIndex: number): void {
    const thing = things[i];
    if (this.selected.indexOf(things[i]) === -1) {
      this.selected.splice(0);
      return;
    }
    thing.flip(rotationIndex);
    this.sendUpdate();
    if (i + 1 < things.length) {
      setTimeout(() => this.flipAnimated(things, i + 1, rotationIndex), 100);
    } else {
      this.selected.splice(0);
    }
  }

  private drop(): void {
    if(!this.movement) {
      return;
    }

    const sourceSlots = [];
    let discardSide = null;
    for (const thing of this.movement.things()) {
      const source = thing.slot;
      const target = this.movement.get(thing)!;
      if (target.group === 'discard' &&
        !(source.group === 'discard' && source.seat === target.seat)) {
        discardSide = target.seat;
      }
      sourceSlots.push(source);
    }

    this.movement.apply();
    this.checkPushes();
    this.finishDrop(sourceSlots);

    if (discardSide !== null) {
      this.soundPlayer.play(SoundType.DISCARD, discardSide);
    }
  }

  private dropInPlace(): void {
    this.finishDrop([]);
  }

  private finishDrop(sourceSlots: Array<Slot>): void {
    const targetSlots = [];
    for (const thing of this.things.values()) {
      if (thing.claimedBy === this.seat) {
        thing.release();
        targetSlots.push(thing.slot);
      }
    }
    this.selected.splice(0);
    this.heldMouse = null;
    this.movement = null;

    for (const slot of sourceSlots) {
      if (slot.links.up) {
        this.dropDown(slot.links.up);
      }
    }
    for (const slot of targetSlots) {
      this.dropDown(slot);
    }

    this.sendUpdate();
    this.sendMouse();
  }

  private dropDown(slot: Slot): void {
    const thing = slot.thing;
    if (thing && thing.claimedBy === null) {
      const downSlot = slot.links.down;
      if (downSlot && downSlot.thing === null) {
        thing.prepareMove();
        thing.moveTo(downSlot);
      }
    }
  }

  private canDrop(): boolean {
    return this.movement ? this.movement.valid() : false;
  }

  private checkPushes(): void {
    for (const [source, target] of this.pushes) {
      target.handlePush(source);
    }
  }

  updateView(handScale = 1): void {
    this.onSeat();
    if (handScale !== this.handScale || this.shadowSeat !== this.viewSeat) {
      this.handScale = handScale;
      this.updateViewShadows();
    }
    this.updateHandPresentation();
    this.updateViewThings();
    this.updateViewDropShadows();
    this.objectView.updateScores(this.setup.getScores());
  }

  setHandSortMode(mode: HandSortMode): void {
    if (mode !== this.handMode) this.handDirty = true;
    this.handMode = mode;
  }

  setHandPointerDown(down: boolean): void {
    this.handPointerDown = down;
  }

  ownHandPresentation(): { tiles: readonly HandTile[]; drawn: number | null } {
    return { tiles: this.presentedHand, drawn: this.drawnTile };
  }

  clearDrawnHandTile(): void {
    this.drawnTile = null;
    this.handSignature = '';
    this.handDirty = true;
  }

  private updateHandPresentation(): void {
    const seat = this.client.connected() && this.hasCurrentHandAuthority()
      && this.conditions.gameType === GameType.CHANGSHA
      ? this.client.seat : null;
    // Authority loss/move clears the old view even while a gesture defers sorting.
    if (seat !== this.presentedSeat) {
      this.presentedSeat = seat;
      this.handDirty = true;
      this.handSignature = '';
      this.drawnTile = null;
      this.presentedHand = [];
      this.handPlaces.clear();
    }
    if (!this.handDirty) return;
    if (seat !== null && (this.handPointerDown || this.isHolding()
      || this.client.claim.get(String(seat)) !== null)) return;
    this.handDirty = false;
    const candidates = [...this.client.things.entries()].map(([id, info]) => {
      const realId = parseRealTileId(id);
      const thing = realId !== null ? this.things.get(realId) : undefined;
      return {
        id: realId ?? id, slotName: info?.slotName ?? '', face: info?.face,
        knownFace: thing?.typeIndex, hidden: thing?.hidden,
        confirmed: !!info && !!thing && thing.type === ThingType.TILE && thing.hiddenHandle === null
          && thing.claimedBy === null && thing.slot.thing === thing && thing.slot.name === info.slotName,
      };
    });
    const hand = ownHandTiles(candidates, seat);
    const signature = `${seat}:${this.handMode}:` + hand.map(tile => `${tile.id}/${tile.face}/${tile.slotName}`).join(',');
    if (signature === this.handSignature) return;
    const previous = new Set(this.presentedHand.map(tile => tile.id));
    const added = hand.filter(tile => !previous.has(tile.id));
    if (previous.size > 0 && added.length === 1 && hand.length % 3 === 2) this.drawnTile = added[0].id;
    if (!hand.some(tile => tile.id === this.drawnTile)) this.drawnTile = null;
    this.handSignature = signature;
    this.presentedHand = sortHand(hand, this.handMode);
    const slots = hand.map(tile => this.slots.get(tile.slotName)!)
      .sort((a, b) => a.indexes[0] - b.indexes[0]);
    this.handPlaces.clear();
    this.presentedHand.forEach((tile, i) => {
      this.handPlaces.set(tile.id, slots[i].placeWithOffset(this.things.get(tile.id)!.rotationIndex));
    });
  }

  private presentedPlace(thing: Thing): Place {
    const handPlace = thing.slot.group === 'hand' && thing.slot.seat === this.client.seat
      ? this.handPlaces.get(thing.index) : undefined;
    return handPlace && this.client.seat !== null
      ? presentLocalHand(handPlace, this.client.seat, this.handScale) : thing.place();
  }

  private hasCurrentHandAuthority(): boolean {
    return this.client.connectionMode !== 'changsha'
      || (this.client.connected() && this.client.serverSnapshotGameId !== null
        && this.client.serverSnapshotGameId === this.client.lastGameId
        && this.handSnapshotSeat === this.client.seat);
  }

  private presentationHidden(thing: Thing): boolean {
    return thing.hidden || (this.conditions.gameType === GameType.CHANGSHA
      && thing.type === ThingType.TILE && thing.hiddenHandle === null
      && thing.slot.group === 'hand' && !this.hasCurrentHandAuthority());
  }

  private updateViewThings(): void {
    const toRender: Array<Render> = [];
    const canDrop = this.canDrop();
    const now = new Date().getTime();

    // Phase K Wave 9 — Compute the commentary highlight envelope once
    // per frame.  When the elapsed time exceeds the pulse window we
    // clear `highlightedThing` so the Render flag below stays in
    // sync with `objectView.highlightIntensity` and the outline pool
    // drops the mesh next frame.
    let highlightIntensity = 0;
    if (this.highlightedThing !== null) {
      const elapsed = now - this.highlightStartMs;
      if (elapsed >= 0 && elapsed < World.HIGHLIGHT_DURATION_MS) {
        // Two sin-wave cycles over the 2 s window with a linear
        // fade-out envelope so the pulse decays as it expires.  The
        // 0.5 baseline keeps the outline visible at the troughs
        // (avoids a flicker-to-zero that would read as a bug).
        const t = elapsed / World.HIGHLIGHT_DURATION_MS;
        const wave = 0.5 + 0.5 * Math.sin(t * Math.PI * 4);
        highlightIntensity = wave * (1 - t);
      } else {
        this.highlightedThing = null;
      }
    }
    this.objectView.highlightIntensity = highlightIntensity;

    for (const thing of this.things.values()) {
      // Hidden Things must still clear their cached custom/instanced geometry.
      // Submit their stable park place without held, hover or highlight effects.
      if (this.presentationHidden(thing)) {
        toRender.push({
          type: thing.type,
          thingIndex: thing.index,
          hidden: true,
          place: thing.place(),
          selected: false,
          hovered: false,
          held: false,
          temporary: false,
          bottom: false,
          highlighted: false,
        });
        continue;
      }
      let place = this.presentedPlace(thing);

      if (thing.claimedBy !== null && thing.shiftSlot === null) {
        let mouse = null, heldMouse = null;
        if (thing.claimedBy === this.seat) {
          mouse = this.mouse;
          heldMouse = this.heldMouse;
        } else {
          mouse = this.mouseTracker.getMouse(thing.claimedBy, now);
          heldMouse = this.mouseTracker.getHeld(thing.claimedBy);
        }

        if (mouse && heldMouse) {
          place = {
            ...place,
            position: place.position.clone(),
            rotation: thing.heldRotation.clone(),
          };
          place.position.x += mouse.x - heldMouse.x;
          place.position.y += mouse.y - heldMouse.y;
        }
      } else if (thing.lastShiftSlotTime >= now - SHIFT_TIME) {
        if (thing.lastShiftSlot !== null) {
          place = thing.lastShiftSlot.places[thing.rotationIndex];
        }
      } else if (thing.claimedBy !== null && thing.shiftSlot !== null) {
        place = thing.shiftSlot.places[thing.rotationIndex];
      }

      const held = thing.claimedBy !== null && thing.shiftSlot === null;
      const selected = this.selected.indexOf(thing) !== -1;
      const hovered = thing === this.hovered ||
        (selected && this.selected.indexOf(this.hovered!) !== -1);
      const temporary = held && thing.claimedBy === this.seat && !canDrop;

      const slot = thing.slot;

      const bottom =
        !held &&
        slot.links.up !== undefined &&
        (slot.links.up.thing === null ||
         slot.links.up.thing.claimedBy !== null);

      // Phase K Wave 9 — Flag the commentary-highlighted Thing so the
      // ObjectView force-promotes it to a per-tile Mesh (the outline
      // hull needs a stable Mesh reference; an InstancedMesh draw call
      // has no per-tile Object3D to attach to).
      const highlighted = thing === this.highlightedThing
        && highlightIntensity > 0;

      toRender.push({
        type: thing.type,
        thingIndex: thing.index,
        drawn: thing.index === this.drawnTile && this.handPlaces.has(thing.index),
        place,
        selected,
        hovered,
        held,
        temporary,
        bottom,
        highlighted,
      });
    }
    this.objectView.updateThings(toRender);
  }

  private updateViewDropShadows(): void {
    const places = [];
    if (this.canDrop()) {
      for (const slot of this.movement!.slots()) {
        places.push(slot.placeWithOffset(0));
      }
    }
    this.objectView.updateDropShadows(places);
  }

  toSelect(): Array<Select> {
    const result = [];
    if (this.seat !== null && !this.isHolding()) {
      for (const thing of this.things.values()) {
        // FE-7 / SC-2 — a hidden Thing (non-entitled REAL, or a free/parked
        // back-pool object) is not rendered by `updateViewThings`, so it must
        // not be a raycast target either: an invisible mesh can never be
        // hovered, selected or dragged, and a parked Thing has no on-table
        // place to raycast against (`Thing.place()` is undefined for it, which
        // made MouseUi.prepareObjects call Vector3.copy(undefined) every frame).
        if (this.presentationHidden(thing)) {
          continue;
        }
        if (thing.claimedBy === null) {
          const place = this.presentedPlace(thing);
          result.push({...place, id: thing.index});
        }
      }
    }
    return result;
  }

  setupView(): void {
    this.objectView.replaceThings(this.things);
    this.updateViewShadows();
  }

  private updateViewShadows(): void {
    this.shadowSeat = this.viewSeat;
    const places = [];
    for (const slot of this.slots.values()) {
      if (slot.drawShadow) {
        const place = slot.places[slot.shadowRotation];
        places.push(this.conditions.gameType === GameType.CHANGSHA && slot.group === 'hand'
          && this.viewSeat !== null && slot.seat === this.viewSeat
          ? presentLocalHand(place, this.viewSeat, this.handScale) : place);
      }
    }
    this.objectView.replaceShadows(places);
  }

  /**
   * Phase K Wave 9 — Map a commentary-record tile id (wire format
   * `"man5"`, `"pin3"`, `"sou9"`, `"east"`, `"red"`, `"3b"`, etc.)
   * to the first Thing on the table whose face matches.  Returns
   * `null` when the id is unparsable OR no in-play tile carries
   * that face (e.g. the tile is currently in a dead-wall slot the
   * spectator can't observe).
   *
   * The face-id format mirrors Bishop's `CommentaryRecord
   * .TileReferences` doc-comment: `<suit><rank>` for numbered tiles
   * (`man`/`pin`/`sou` × 1..9 — also accepts the legacy single-
   * letter `m`/`p`/`s` notation `"5m"` / `"3b"`) and a fixed
   * vocabulary for honors (`east|south|west|north|white|green|red`
   * — also accepts `wind-e` / `dragon-w` shorthands).  The red-five
   * tiles map to the dedicated typeIndex slots 34/35/36 (set up by
   * `setup.ts:tileIndex` under `fives='121'`).
   *
   * Lookup is `typeIndex % 37` to ignore the per-deal back-color
   * cycling (W8 setup adds `37 * conditions.back` to mix the two
   * tile-back textures).
   */
  findThingByFace(tileId: string): Thing | null {
    const face = parseTileFace(tileId);
    if (face === null) return null;
    for (const thing of this.things.values()) {
      if (thing.type !== ThingType.TILE) continue;
      if ((thing.typeIndex % 37) === face) {
        return thing;
      }
    }
    return null;
  }

  /**
   * Phase K Wave 9 — Set (or clear) the commentary-highlight target.
   * The pulse runs for `HIGHLIGHT_DURATION_MS` (2 s) from the moment
   * this is called; re-entry resets the timer so the
   * most-recently-clicked chip wins.  Passing `null` cancels the
   * highlight on the next frame.
   */
  setHighlightedThing(thing: Thing | null): void {
    this.highlightedThing = thing;
    this.highlightStartMs = thing === null ? 0 : new Date().getTime();
  }
}

/**
 * Phase K Wave 9 — Parse a commentary wire-string tile id to its
 * canonical face index (`0..36`).  Returns `null` for unknown / empty
 * input.  Mirrors the standard mahjong notation Bishop's commentary
 * generator emits per `ICommentaryGenerator.cs:110-113`.
 *
 * Layout (set up by `setup.ts:tileIndex`, mirrors the texture atlas
 * row-major order at 8 cols/row):
 *
 *   • 0..8   → `man1`..`man9`   (characters / wan-zu)
 *   • 9..17  → `pin1`..`pin9`   (dots / pin-zu)
 *   • 18..26 → `sou1`..`sou9`   (bamboo / sou-zu)
 *   • 27..30 → `east|south|west|north`   (winds, ESWN order)
 *   • 31..33 → `white|green|red`         (dragons, WGR order)
 *   • 34..36 → `red-man5|red-pin5|red-sou5` (aka-dora red fives)
 *
 * Tolerated shorthands (commentary text is LLM-generated and not
 * pinned to one spelling):
 *   • Suit-first: `"man5"`, `"pin3"`, `"sou9"`
 *   • Rank-first: `"5m"`, `"3p"`, `"9s"`, `"3b"` (b = bamboo = sou)
 *   • Single-letter suits: `m`, `p`, `s`, `b` (bamboo), `c` (man)
 *   • Honour aliases: `east|east-wind|wind-e|e`, `white|haku`, etc.
 */
export function parseTileFace(tileId: string): number | null {
  if (typeof tileId !== 'string') return null;
  const id = tileId.trim().toLowerCase();
  if (id === '') return null;

  // Honor tiles first — fixed vocabulary, no rank suffix.
  const winds: Record<string, number> = {
    east: 27, e: 27, 'wind-e': 27, 'east-wind': 27, ton: 27,
    south: 28, s: 28, 'wind-s': 28, 'south-wind': 28, nan: 28,
    west: 29, w: 29, 'wind-w': 29, 'west-wind': 29, sha: 29,
    north: 30, n: 30, 'wind-n': 30, 'north-wind': 30, pei: 30,
  };
  if (winds[id] !== undefined) return winds[id];
  const dragons: Record<string, number> = {
    white: 31, haku: 31, 'dragon-w': 31, 'white-dragon': 31,
    green: 32, hatsu: 32, 'dragon-g': 32, 'green-dragon': 32,
    red: 33, chun: 33, 'dragon-r': 33, 'red-dragon': 33,
  };
  if (dragons[id] !== undefined) return dragons[id];

  // Red-five aka-dora aliases.
  if (id === 'red-man5' || id === 'aka-man5' || id === '0m' || id === 'man0') return 34;
  if (id === 'red-pin5' || id === 'aka-pin5' || id === '0p' || id === 'pin0') return 35;
  if (id === 'red-sou5' || id === 'aka-sou5' || id === '0s' || id === 'sou0'
      || id === 'red-bam5' || id === '0b') return 36;

  // Suit + rank — try suit-first then rank-first.
  // Suit-first: e.g. "man5", "pin3", "sou9".
  const suitFirst = /^(man|pin|sou|bam|wan|crak|crack|character|dot|bamboo)\s*-?\s*([1-9])$/.exec(id);
  if (suitFirst !== null) {
    const suit = normalizeSuit(suitFirst[1]);
    const rank = Number.parseInt(suitFirst[2], 10);
    if (suit !== null) return suit * 9 + (rank - 1);
  }
  // Rank-first: e.g. "5m", "3p", "9s", "3b".
  const rankFirst = /^([1-9])\s*-?\s*(m|p|s|b|c|man|pin|sou|bam|wan)$/.exec(id);
  if (rankFirst !== null) {
    const rank = Number.parseInt(rankFirst[1], 10);
    const suit = normalizeSuit(rankFirst[2]);
    if (suit !== null) return suit * 9 + (rank - 1);
  }
  return null;
}

function normalizeSuit(token: string): 0 | 1 | 2 | null {
  switch (token) {
    case 'm':
    case 'c':
    case 'man':
    case 'wan':
    case 'crak':
    case 'crack':
    case 'character':
      return 0;
    case 'p':
    case 'pin':
    case 'dot':
      return 1;
    case 's':
    case 'b':
    case 'sou':
    case 'bam':
    case 'bamboo':
      return 2;
    default:
      return null;
  }
}

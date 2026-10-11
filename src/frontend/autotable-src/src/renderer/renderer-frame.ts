import type { Client } from '../client';
import type { World } from '../world';
import type { Thing } from '../thing';
import type { Slot } from '../slot';
import { GAME_TYPES, GameType, ThingType, type Place, type ThingInfo, type PickupEntry } from '../types';
import { partitionThingEntries, resolveTileFace } from '../changsha-mode-policy';
import { BufferedRendererSink } from './renderer-sink';
import {
  assertDto, CONTRACT_VERSION, ContractError, opaqueId,
  type ClearReason, type Entity, type Frame, type Group, type Layout, type Phase, type Pose, type Presentation,
} from './renderer-contract';

export interface FramePort {
  clear(sessionId: string, reason: ClearReason): void;
  publish(layout: Layout, frame: Frame): void;
  unavailable(message: string): void;
}
export interface LiveEntity { entity: Entity; thing: Thing; sourceKey: string | number }
interface FrameContext { rule: string; entities: Map<string, { thing: Thing; slot: Slot; rotation: number }> }

const phases = new Set<string>([
  'Seating', 'RollingDice', 'Dealing', 'BreakPointMarked', 'PickupRound1', 'PickupRound2', 'PickupRound3',
  'SingleTilePickup', 'DealerExtra', 'AwaitingDiscard', 'AwaitingClaim', 'DeclaringKong', 'DrawingReplacement',
  'Scoring', 'EndHand', 'RotatingBanker', 'WallExhausted', 'GameComplete',
]);
const legacyPhases: Record<string, Phase> = {
  rollDice: 'RollingDice', 'roll-dice': 'RollingDice', 'breakPointMarked': 'BreakPointMarked',
  'break-point-marked': 'BreakPointMarked', 'pickup-r1': 'PickupRound1', 'pickup-r2': 'PickupRound2',
  'pickup-r3': 'PickupRound3', single: 'SingleTilePickup', 'dealer-extra': 'DealerExtra',
  inPlay: 'AwaitingDiscard', EndGame: 'GameComplete',
};
export function normalizePhase(value: string | undefined): Phase | null {
  if (value === undefined) return null;
  if (phases.has(value)) return value as Phase;
  if (Object.prototype.hasOwnProperty.call(legacyPhases, value)) return legacyPhases[value];
  throw new ContractError('Unsupported source phase; renderer unavailable.');
}
function pose(place: Place): Pose {
  return {
    position: { x: place.position.x, y: place.position.y, z: place.position.z },
    rotation: { x: place.rotation.x, y: place.rotation.y, z: place.rotation.z, w: place.rotation.w },
    size: { x: place.size.x, y: place.size.y, z: place.size.z },
  };
}
function faceVisible(slot: Slot, rotationIndex: number, actor: number | null): boolean {
  switch (slot.group) {
    case 'hand': return rotationIndex === 1 || (rotationIndex === 0 && actor !== null && actor === slot.seat);
    case 'wall': return rotationIndex === 1;
    case 'wall.open': return rotationIndex === 0 && actor !== null && actor === slot.seat;
    case 'discard': case 'meld': return rotationIndex < 2;
    default: return true;
  }
}

/** Projects only the current Client collection, never the pre-baked model's hidden identity catalog. */
export class RendererFrameAdapter {
  sessionId: string;
  frameId = 0;
  layoutRevision = 0;
  latest: Frame | null = null;
  layout: Layout | null = null;
  private ids = new Map<string | number, { id: string; thing: Thing }>();
  private live = new Map<string, LiveEntity>();
  private contexts = new Map<number, FrameContext>();
  private authority = '';
  private fullAuthority: string | null = null;
  private ready = false;
  private layoutSignature = '';
  private catalog: Map<string, Slot> | null = null;
  private catalogSlots: Slot[] = [];
  private sinkGeneration = -1;
  private failed = false;
  private disposed = false;
  private presentation: Presentation = {
    viewMode: 'perspective', boardLayout: 'classic', handLayout: 'in-table', tileLabels: false, lookDown: 0, zoom: 0,
  };
  private readonly joining = (): void => this.reset('join');
  private readonly disconnected = (): void => this.reset('disconnect');
  private readonly protocolFailed = (): void => this.fail('Primary game protocol unavailable.');
  private readonly updated = (_entries: unknown, full: boolean): void => {
    this.reconcileAuthority();
    if (full) this.fullAuthority = this.authorityKey();
    if (!this.failed && !this.disposed) this.refresh();
  };

  constructor(
    readonly instanceId: string, readonly client: Client, readonly world: World,
    readonly sink: BufferedRendererSink, private readonly port: FramePort,
    private readonly idFactory: (prefix: string) => string = opaqueId,
  ) {
    this.sessionId = idFactory('session');
    client.on('joining', this.joining);
    client.on('connect', this.joining);
    client.on('disconnect', this.disconnected);
    client.on('protocol-unavailable', this.protocolFailed);
    // Registered after Client/Collection/World listeners: the complete UPDATE has finished here.
    client.on('update', this.updated);
    this.authority = this.authorityKey();
    port.clear(this.sessionId, 'join');
  }

  private authorityKey(): string {
    const viewer = this.client.viewerAuthority;
    return JSON.stringify([
      this.client.connectionMode, this.client.connected(), this.client.lastGameId,
      this.client.connectionMode === 'offline' ? this.world.seat : this.client.seat,
      viewer?.roomId ?? null, viewer?.revision ?? null,
    ]);
  }

  private reconcileAuthority(): void {
    const authority = this.authorityKey();
    if (authority !== this.authority) this.reset('authority-change');
  }

  reset(reason: ClearReason): void {
    this.world.revokeRendererInput();
    this.sink.clear();
    this.sessionId = this.idFactory('session');
    this.frameId = 0;
    this.latest = null;
    this.ids.clear();
    this.live.clear();
    this.contexts.clear();
    this.catalog = null;
    this.sinkGeneration = -1;
    this.layout = null;
    this.authority = this.authorityKey();
    this.ready = false;
    this.fullAuthority = null;
    this.failed = reason === 'protocol-unavailable';
    this.port.clear(this.sessionId, reason);
  }

  fail(message: string): void {
    if (this.failed || this.disposed) return;
    this.reset('protocol-unavailable');
    this.port.unavailable(message);
  }

  cancelGesture(): void {
    if (this.client.connected() || this.client.connectionMode === 'offline') this.world.endRendererDrag(false);
    else this.world.revokeRendererInput();
  }

  setPresentation(presentation: Presentation): void {
    this.presentation = { ...presentation };
  }

  private readLayout(): Layout {
    const slots = [...this.world.slots.values()].filter(slot => !slot.offTable).map(slot => ({
      slotName: slot.name, group: slot.group as Group, seat: slot.seat, indexes: slot.indexes.slice(), kind: slot.type,
      poses: slot.places.map(pose), drawShadow: slot.drawShadow, shadowRotationIndex: slot.shadowRotation,
      canFlipMultiple: slot.canFlipMultiple, rotateHeld: slot.rotateHeld,
    }));
    const signature = JSON.stringify([this.world.conditions.gameType, slots]);
    const catalogSlots = [...this.world.slots.values()].filter(slot => !slot.offTable);
    const replaced = catalogSlots.length !== this.catalogSlots.length || catalogSlots.some((slot, i) => slot !== this.catalogSlots[i]);
    if (signature !== this.layoutSignature || replaced) { this.layoutRevision++; this.layoutSignature = signature; }
    this.catalogSlots = catalogSlots;
    const layout: Layout = {
      contractVersion: CONTRACT_VERSION, instanceId: this.instanceId, sessionId: this.sessionId, type: 'layout',
      layoutRevision: Math.max(1, this.layoutRevision), gameType: this.world.conditions.gameType,
      activeSeats: [...GAME_TYPES[this.world.conditions.gameType].seats], canonicalBoard: { width: 174, depth: 174 }, slots,
    };
    assertDto<Layout>('RendererLayout', layout);
    if (new Set(slots.map(slot => slot.slotName)).size !== slots.length) throw new ContractError('Duplicate source slot catalog.');
    return layout;
  }

  private projectedThings(): Array<{ key: string | number; thing: Thing; info: ThingInfo }> {
    const { real, hidden } = partitionThingEntries([...this.client.things.entries()]);
    const backs = new Map<string, Thing>();
    for (const thing of this.world.things.values()) if (thing.hiddenHandle !== null) backs.set(thing.hiddenHandle, thing);
    const result = new Map<string | number, { key: string | number; thing: Thing; info: ThingInfo }>();
    for (const [key, info] of [...real, ...hidden]) {
      const thing = typeof key === 'number' ? this.world.things.get(key) : backs.get(key);
      if (!info || !thing || thing.hidden || thing.slot.offTable || this.world.slots.get(thing.slot.name) !== thing.slot) continue;
      if (this.client.connectionMode === 'changsha' && this.world.slots.get(info.slotName) !== thing.slot) continue;
      if (this.client.connectionMode === 'changsha' && typeof key === 'number'
        && (key > 107 || thing.type !== ThingType.TILE || thing.hiddenHandle !== null)) continue;
      result.set(key, { key, thing, info });
    }
    return [...result.values()];
  }

  private entityAppearance(key: string | number, thing: Thing, info: ThingInfo, actor: number | null): Entity['appearance'] {
    if (thing.type !== ThingType.TILE) return {
      faceIndex: null, knownFace: false, showFace: false, backIndex: 0, valueIndex: thing.typeIndex,
    };
    const visible = faceVisible(thing.slot, thing.rotationIndex, actor);
    let faceIndex: number | null;
    if (this.client.connectionMode === 'changsha') {
      faceIndex = resolveTileFace(key, info.face).faceIndex;
    } else {
      const projectedSlot = this.world.slots.get(info.slotName);
      const projectedOwned = projectedSlot && actor !== null && projectedSlot.seat === actor
        && (projectedSlot.group === 'hand' || projectedSlot.group === 'meld') && info.face !== null;
      const projectedPublic = projectedSlot && faceVisible(projectedSlot, info.rotationIndex ?? 0, actor)
        && (projectedSlot.group !== 'hand' || info.face !== null);
      const entitled = projectedOwned || projectedPublic;
      const ownedHand = thing.slot.group === 'hand' && actor !== null && thing.slot.seat === actor;
      const publicFace = visible && (thing.slot.group !== 'hand' || info.face !== null);
      const ownedKong = thing.slot.group === 'meld' && actor !== null && thing.slot.seat === actor;
      faceIndex = thing.hiddenHandle === null && entitled && (ownedHand || publicFace || ownedKong) ? thing.typeIndex % 37 : null;
    }
    return {
      faceIndex, knownFace: faceIndex !== null, showFace: faceIndex !== null && visible,
      backIndex: this.client.connectionMode === 'changsha' ? 0 : Math.floor(thing.typeIndex / 37), valueIndex: null,
    };
  }

  ruleContext(): string {
    const ownTurn = this.client.seat !== null ? this.client.ownTurn.get(this.client.seat) : null;
    return JSON.stringify([
      this.client.match.get(0)?.dealer, this.world.rendererTurnSignal(), this.world.currentPickup(),
      ownTurn ? [ownTurn.gameId, ownTurn.stateVersion] : null,
    ]);
  }

  resolveEntity(entityId: string, contextFrameId: number, rules = false): LiveEntity | null {
    this.reconcileAuthority();
    if (this.failed || !this.world.rendererSnapshotReady()) return null;
    const record = this.live.get(entityId);
    const context = this.contexts.get(contextFrameId);
    const previous = context?.entities.get(entityId);
    if (!record || !previous || previous.thing !== record.thing || previous.slot !== record.thing.slot
      || this.world.things.get(record.thing.index) !== record.thing
      || (rules && context!.rule !== this.ruleContext())) return null;
    // Collections can have moved/tombstoned an entity since the last 30Hz publication.
    const stillLive = this.projectedThings().some(row => row.key === record.sourceKey && row.thing === record.thing);
    return stillLive ? record : null;
  }

  contextKnown(frameId: number, rules = false): boolean {
    this.reconcileAuthority();
    const context = this.contexts.get(frameId);
    return !!context && (!rules || context.rule === this.ruleContext()) && !this.failed;
  }

  private hasSnapshotAuthority(): boolean {
    // Relay seat deltas retain the bound room snapshot; Changsha must rebind private state with a fresh FULL.
    return this.world.rendererSnapshotReady()
      && (this.client.connectionMode !== 'changsha' || this.fullAuthority === this.authorityKey());
  }

  actionable(): boolean {
    this.reconcileAuthority();
    return !this.failed && !!this.latest?.session.snapshotReady && this.latest.session.actorSeat !== null
      && this.hasSnapshotAuthority();
  }

  refresh(): Frame | null {
    if (this.disposed || this.failed) return null;
    try {
      this.reconcileAuthority();
      this.world.updateView();
      const snapshotReady = this.hasSnapshotAuthority();
      if (this.ready && !snapshotReady) this.reset('authority-change');
      this.ready = snapshotReady;
      if (this.layout === null || this.catalog !== this.world.slots || this.sinkGeneration !== this.sink.generation) {
        this.layout = this.readLayout();
        this.catalog = this.world.slots;
        this.sinkGeneration = this.sink.generation;
      }
      const actor = this.client.connectionMode === 'offline' ? this.world.seat
        : snapshotReady ? this.client.seat : null;
      const projections = snapshotReady ? this.projectedThings() : [];
      const rows = new Map(this.sink.renders.map(row => [row.thingIndex, row]));
      const selection = projections.map(row => row.thing);
      const entities: Entity[] = [];
      const live = new Map<string, LiveEntity>();
      const present = new Set<string | number>();
      for (const { key, thing, info } of projections) {
        const row = rows.get(thing.index);
        if (!row || row.hidden) continue;
        let identity = this.ids.get(key);
        if (!identity || identity.thing !== thing) {
          identity = { id: this.idFactory('entity'), thing };
          this.ids.set(key, identity);
        }
        present.add(key);
        const entity: Entity = {
          entityId: identity.id, kind: thing.type, slotName: thing.slot.name, group: thing.slot.group as Group,
          seat: thing.slot.seat, indexes: thing.slot.indexes.slice(), rotationIndex: thing.rotationIndex,
          pose: pose(row.place), scale: row.place.scale ?? 1,
          appearance: this.entityAppearance(key, thing, info, actor),
          display: {
            selected: row.selected, hovered: row.hovered, held: row.held, temporary: row.temporary,
            bottom: row.bottom, drawn: row.drawn ?? false, highlight: row.highlighted ? this.sink.highlightIntensity : 0,
          },
          interaction: this.world.rendererInteraction(thing, selection),
        };
        entities.push(entity);
        live.set(identity.id, { entity, thing, sourceKey: key });
      }
      for (const key of this.ids.keys()) if (!present.has(key)) this.ids.delete(key);
      this.live = live;
      const own = this.world.ownHandPresentation();
      const hand = this.world.conditions.gameType === GameType.CHANGSHA
        ? own.tiles.map(tile => [...live.values()].find(row => row.thing.index === tile.id)?.entity).filter((entity): entity is Entity => !!entity)
        : entities.filter(entity => actor !== null && entity.seat === actor && entity.group === 'hand')
          .sort((a, b) => (a.indexes[0] ?? Number.MAX_SAFE_INTEGER) - (b.indexes[0] ?? Number.MAX_SAFE_INTEGER));
      const localHand = hand.filter(entity => actor !== null && entity.seat === actor && entity.group === 'hand');
      const turnSource = snapshotReady ? this.world.rendererTurnSignal() : null;
      if (snapshotReady && this.client.turn.get('current') !== null && turnSource === null) throw new ContractError('Malformed source turn.');
      const pickupSource = snapshotReady ? this.world.currentPickup() : null;
      const dice = snapshotReady ? this.client.dice.get('current') ?? this.client.dice.get(0) : null;
      const match = snapshotReady ? this.client.match.get(0) : null;
      if (snapshotReady && (!match || !match.conditions
        || ['back', 'fives', 'points', 'dealType'].some(key => !(key in match.conditions)))) throw new ContractError('Incomplete source match.');
      const canRoll = snapshotReady && this.world.rendererCanRollDice();
      const complete = snapshotReady ? this.client.gameComplete.get('current') : null;
      const score = snapshotReady ? this.client.result.get('current')?.score : null;
      const totals = complete?.totalScores ?? complete?.TotalScores;
      const frame: Frame = {
        contractVersion: CONTRACT_VERSION, instanceId: this.instanceId, sessionId: this.sessionId, type: 'frame',
        frameId: ++this.frameId, layoutRevision: this.layout.layoutRevision,
        session: {
          gameId: this.client.lastGameId, mode: this.client.connectionMode, connected: this.client.connected(),
          snapshotReady, actorSeat: actor, viewSeat: this.world.viewSeat, viewerRevision: this.client.viewerAuthority?.revision ?? null,
        },
        presentation: { ...this.presentation },
        match: match ? {
          dealer: match.dealer, honba: match.honba,
          conditions: {
            gameType: this.world.conditions.gameType, back: this.world.conditions.back,
            fives: this.world.conditions.fives, points: this.world.conditions.points,
            dealType: this.world.conditions.dealType, baseUnit: this.world.conditions.baseUnit,
            dealMode: this.world.conditions.dealMode,
          },
        } : null,
        entities,
        seats: this.layout.activeSeats.map(seat => {
          const playerId = snapshotReady ? this.client.seatPlayers[seat] ?? null : null;
          return {
            seat, playerId, nick: playerId !== null ? this.client.nicks.get(playerId) : null,
            score: this.world.conditions.gameType === GameType.CHANGSHA
              ? totals?.[String(seat)] ?? score?.find(row => row.seat === seat)?.delta ?? null : this.sink.scores[seat] ?? null,
          };
        }),
        localHand: {
          seat: actor, entityIds: localHand.map(entity => entity.entityId),
          drawnEntityId: localHand.find(entity => entity.display.drawn)?.entityId ?? null,
        },
        turn: turnSource ? {
          activeSeat: turnSource.activeSeat, phase: normalizePhase(turnSource.phase), awaitingDiscard: turnSource.awaitingDiscard ?? null,
        } : null,
        pickup: pickupSource ? this.readPickup(pickupSource, entities) : null,
        dice: dice ? {
          values: [dice.d1 ?? dice.dice?.[0], dice.d2 ?? dice.dice?.[1]], state: dice.state, canRoll,
        } : null,
      };
      assertDto<Frame>('RendererFrame', frame);
      this.latest = frame;
      this.contexts.set(frame.frameId, {
        rule: this.ruleContext(),
        entities: new Map([...live].map(([id, record]) => [id, {
          thing: record.thing, slot: record.thing.slot, rotation: record.thing.rotationIndex,
        }])),
      });
      if (this.contexts.size > 240) this.contexts.delete(this.contexts.keys().next().value!);
      this.port.publish(this.layout, frame);
      return frame;
    } catch (error) {
      this.fail(error instanceof ContractError ? error.message : 'Unable to project the current game model safely.');
      return null;
    }
  }

  private readPickup(source: PickupEntry, entities: Entity[]): NonNullable<Frame['pickup']> {
    const phase = normalizePhase(source.phase);
    if (phase === null) throw new ContractError('Missing source pickup phase.');
    const trigger = source.targetSlots?.length === 1 ? source.targetSlots[0] : null;
    const preview = (source as PickupEntry & { batchPreviewSlots?: string[] }).batchPreviewSlots;
    return {
      phase, seatIndex: source.seatIndex, count: source.count, dealMode: source.dealMode,
      breakPoint: source.breakPoint ? {
        wallIndex: source.breakPoint.wallIndex, stackIndex: source.breakPoint.stackIndex, tileIndex: source.breakPoint.tileIndex,
      } : null,
      wallIndex: source.wallIndex ?? null, triggerSlotName: trigger, batchPreviewSlotNames: preview ? [...preview] : [],
      canTake: entities.some(entity => entity.slotName === trigger && entity.interaction.canPickup),
    };
  }

  dispose(): void {
    if (this.disposed) return;
    for (const [event, handler] of [
      ['joining', this.joining], ['connect', this.joining], ['disconnect', this.disconnected],
      ['protocol-unavailable', this.protocolFailed], ['update', this.updated],
    ] as const) this.client.off(event, handler);
    this.reset('dispose');
    this.disposed = true;
  }
}

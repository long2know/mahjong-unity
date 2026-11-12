import schema from '../../../../../contracts/renderer-v1.schema.json';
import type { Conditions, GameType, MatchInfo, ThingType } from '../types';

export const CONTRACT_VERSION = '1.0.0' as const;

export type Group = 'hand' | 'meld' | 'wall' | 'wall.open' | 'discard' | 'kita' | 'tray' | 'payment' | 'riichi' | 'marker';
export type Phase = 'Seating' | 'RollingDice' | 'Dealing' | 'BreakPointMarked' | 'PickupRound1' | 'PickupRound2'
  | 'PickupRound3' | 'SingleTilePickup' | 'DealerExtra' | 'AwaitingDiscard' | 'AwaitingClaim' | 'DeclaringKong'
  | 'DrawingReplacement' | 'Scoring' | 'EndHand' | 'RotatingBanker' | 'WallExhausted' | 'GameComplete';
export interface Vector { x: number; y: number; z: number }
export interface Rotation extends Vector { w: number }
export interface Pose { position: Vector; rotation: Rotation; size: Vector }
export interface Envelope { contractVersion: typeof CONTRACT_VERSION; instanceId: string }
export interface SessionEnvelope extends Envelope { sessionId: string }
export interface Interaction { canSelect: boolean; canDrag: boolean; canFlip: boolean; canDiscard: boolean; canPickup: boolean }
export interface Entity {
  entityId: string;
  kind: ThingType;
  slotName: string;
  group: Group;
  seat: number | null;
  indexes: number[];
  rotationIndex: number;
  pose: Pose;
  scale: number;
  appearance: { faceIndex: number | null; knownFace: boolean; showFace: boolean; backIndex: number; valueIndex: number | null };
  display: { selected: boolean; hovered: boolean; held: boolean; temporary: boolean; bottom: boolean; drawn: boolean; highlight: number };
  interaction: Interaction;
}
export interface Layout extends SessionEnvelope {
  type: 'layout';
  layoutRevision: number;
  gameType: GameType;
  activeSeats: number[];
  canonicalBoard: { width: 174; depth: 174 };
  slots: Array<{
    slotName: string; group: Group; seat: number | null; indexes: number[]; kind: ThingType; poses: Pose[];
    drawShadow: boolean; shadowRotationIndex: number; canFlipMultiple: boolean; rotateHeld: boolean;
  }>;
}
export interface CameraView {
  scale: number;
  yaw: number;
  pitch: number | null;
  roll: number;
}
export interface Presentation {
  viewMode: 'perspective' | 'flat';
  boardLayout: 'classic' | 'rectangular';
  handLayout: 'in-table' | 'separate';
  tileLabels: boolean;
  lookDown: number;
  zoom: number;
  camera?: CameraView | null;
}
export interface Frame extends SessionEnvelope {
  type: 'frame';
  frameId: number;
  layoutRevision: number;
  session: {
    gameId: string | null; mode: 'offline' | 'relay' | 'changsha'; connected: boolean; snapshotReady: boolean;
    actorSeat: number | null; viewSeat: number | null; viewerRevision: number | null;
  };
  presentation: Presentation;
  match: MatchInfo | null;
  entities: Entity[];
  seats: Array<{ seat: number; playerId: string | null; nick: string | null; score: number | null }>;
  localHand: { seat: number | null; entityIds: string[]; drawnEntityId: string | null };
  turn: { activeSeat: number | null; phase: Phase | null; awaitingDiscard: boolean | null } | null;
  pickup: {
    phase: Phase; seatIndex: number; count: number; dealMode: Conditions['dealMode'];
    breakPoint: { wallIndex: number; stackIndex: number; tileIndex: number } | null; wallIndex: number | null;
    triggerSlotName: string | null; batchPreviewSlotNames: string[]; canTake: boolean;
  } | null;
  dice: { values: [number, number]; state: 'ignore' | 'rolled'; canRoll: boolean } | null;
}
export interface Viewport extends Envelope {
  type: 'viewport';
  resizeRevision: number;
  widthCssPx: number;
  heightCssPx: number;
  devicePixelRatio: number;
  renderPixelRatio: number;
  widthPx: number;
  heightPx: number;
  insetsCssPx: { top: number; right: number; bottom: number; left: number };
  handBandCssPx: number;
}
export type Intent =
  | { kind: 'discard'; entityId: string }
  | { kind: 'takePickup'; triggerEntityId: string }
  | { kind: 'rollDice' }
  | { kind: 'hover'; entityId: string | null }
  | { kind: 'select'; entityIds: string[] }
  | { kind: 'beginDrag'; gestureId: string; anchorEntityId: string; entityIds: string[] }
  | { kind: 'dragToSlot'; gestureId: string; slotName: string }
  | { kind: 'endDrag'; gestureId: string; commit: boolean }
  | { kind: 'flip'; entityIds: string[]; direction: -1 | 1; animated: boolean };
export interface RendererAction extends SessionEnvelope { type: 'action'; actionId: string; contextFrameId: number; intent: Intent }
export interface ActionResult extends SessionEnvelope {
  type: 'actionResult'; actionId: string; status: 'submitted' | 'local' | 'ignored' | 'rejected';
  reason: 'stale-session' | 'stale-entity' | 'not-actionable' | 'invalid-target' | 'protocol-unavailable' | null;
}
export interface FrameRendered extends SessionEnvelope { type: 'frameRendered'; frameId: number; layoutRevision: number }
export interface RendererError extends Envelope {
  type: 'error'; sessionId: string | null;
  code: 'unsupported-contract' | 'invalid-message' | 'missing-layout' | 'stale-frame' | 'renderer-failure';
  message: string;
}
export type ClearReason = 'join' | 'disconnect' | 'authority-change' | 'protocol-unavailable' | 'dispose';

type Schema = {
  $ref?: string; type?: string; const?: unknown; enum?: unknown[]; properties?: Record<string, Schema>; required?: string[];
  additionalProperties?: boolean; items?: Schema; minItems?: number; maxItems?: number; uniqueItems?: boolean;
  minimum?: number; maximum?: number; exclusiveMinimum?: number; minLength?: number; maxLength?: number; pattern?: string;
  anyOf?: Schema[]; oneOf?: Schema[]; allOf?: Schema[]; if?: Schema; then?: Schema;
};
const definitions = schema.definitions as unknown as Record<string, Schema>;
export class ContractError extends Error {}

function valid(rule: Schema, value: unknown, path: string): string | null {
  // Expected union/conditional mismatches are results, not thrown exceptions.
  const matches = (candidate: Schema): boolean => {
    try { return valid(candidate, value, path) === null; } catch { return false; }
  };
  if (rule.$ref) {
    const target = definitions[rule.$ref.replace('#/definitions/', '')];
    if (!target) return path;
    const failure = valid(target, value, path);
    if (failure !== null) return failure;
  }
  if ('const' in rule && value !== rule.const) return path;
  if (rule.enum && !rule.enum.includes(value)) return path;
  if (rule.anyOf && !rule.anyOf.some(matches)) return path;
  if (rule.oneOf && rule.oneOf.filter(matches).length !== 1) return path;
  if (rule.allOf) for (const child of rule.allOf) {
    const failure = valid(child, value, path);
    if (failure !== null) return failure;
  }
  if (rule.if && rule.then && matches(rule.if)) {
    const failure = valid(rule.then, value, path);
    if (failure !== null) return failure;
  }
  switch (rule.type) {
    case 'null': if (value !== null) return path; break;
    case 'boolean': if (typeof value !== 'boolean') return path; break;
    case 'integer': if (typeof value !== 'number' || !Number.isSafeInteger(value)) return path; break;
    case 'number': if (typeof value !== 'number' || !Number.isFinite(value)) return path; break;
    case 'string': if (typeof value !== 'string') return path; break;
    case 'array': if (!Array.isArray(value)) return path; break;
    case 'object': if (!value || typeof value !== 'object' || Array.isArray(value)) return path; break;
  }
  if (typeof value === 'number') {
    if (!Number.isFinite(value) || (rule.minimum !== undefined && value < rule.minimum)
      || (rule.maximum !== undefined && value > rule.maximum)
      || (rule.exclusiveMinimum !== undefined && value <= rule.exclusiveMinimum)) return path;
  }
  if (typeof value === 'string' && ((rule.minLength !== undefined && value.length < rule.minLength)
    || (rule.maxLength !== undefined && value.length > rule.maxLength)
    || (rule.pattern && !new RegExp(rule.pattern).test(value)))) return path;
  if (Array.isArray(value)) {
    if ((rule.minItems !== undefined && value.length < rule.minItems)
      || (rule.maxItems !== undefined && value.length > rule.maxItems)
      || (rule.uniqueItems && new Set(value.map(item => JSON.stringify(item))).size !== value.length)) return path;
    if (rule.items) for (let i = 0; i < value.length; i++) {
      const failure = valid(rule.items, value[i], `${path}[${i}]`);
      if (failure !== null) return failure;
    }
  } else if (value && typeof value === 'object') {
    const object = value as Record<string, unknown>;
    for (const key of rule.required ?? []) if (!Object.prototype.hasOwnProperty.call(object, key)) return path;
    if (rule.additionalProperties === false && Object.keys(object).some(key => !Object.prototype.hasOwnProperty.call(rule.properties, key))) return path;
    for (const [key, child] of Object.entries(rule.properties ?? {})) {
      if (Object.prototype.hasOwnProperty.call(object, key)) {
        const failure = valid(child, object[key], `${path}.${key}`);
        if (failure !== null) return failure;
      }
    }
  }
  return null;
}

/** Validate against the accepted schema itself, including nested extra-field/privacy restrictions. */
export function assertDto<T>(definition: string, value: unknown): asserts value is T {
  const rule = definitions[definition];
  if (!rule) throw new ContractError('Unknown renderer DTO definition.');
  const failure = valid(rule, value, definition);
  if (failure !== null) throw new ContractError(`Invalid renderer DTO at ${failure}.`);
}

export function opaqueId(prefix: string): string {
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return `${prefix}_${Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('')}`;
}

import schema from '../../../../../contracts/renderer-v1.schema.json';
import type { Conditions, GameType, MatchInfo, ThingType } from '../types';

export const CONTRACT_VERSION = '1.0.0' as const;
export const UNITY_VERSION = '6000.3.24f1' as const;
export const BRIDGE_RECEIVER = 'MahjongBridge';
export const MANIFEST_URL = '/autotable/unity/renderer-build.json';

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
export interface Artifact { path: string; bytes: number; sha256: string }
export interface BuildManifest {
  artifactKind: 'unity-webgl'; contractVersion: typeof CONTRACT_VERSION; buildId: string; unityVersion: typeof UNITY_VERSION;
  unityRevision: '4e7b9b5b6244'; compression: 'none'; threading: 'single-threaded'; developmentBuild: boolean;
  baselineRevision: 'd10d351e543aa3e341960de9800828b6c0662a82'; rendererSourceRevision: string;
  rendererSourceTreeSha256: string; sourceDirty: boolean; builtAtUtc: string;
  loader: Artifact; framework: Artifact; data: Artifact; wasm: Artifact; streamingAssetsPath: string | null;
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
export interface Ready extends Envelope { type: 'ready'; buildId: string; unityVersion: typeof UNITY_VERSION }
export interface FrameRendered extends SessionEnvelope { type: 'frameRendered'; frameId: number; layoutRevision: number }
export interface RendererError extends Envelope {
  type: 'error'; sessionId: string | null;
  code: 'unsupported-contract' | 'invalid-message' | 'missing-layout' | 'stale-frame' | 'renderer-failure';
  message: string;
}
export type ClearReason = 'join' | 'disconnect' | 'authority-change' | 'protocol-unavailable' | 'dispose';
export type ReceiverMethod = 'Configure' | 'Clear' | 'ApplyLayout' | 'ApplyFrame' | 'SetViewport' | 'SetPlayback' | 'CancelInput' | 'ReceiveActionResult';

type Schema = {
  $ref?: string; type?: string; const?: unknown; enum?: unknown[]; properties?: Record<string, Schema>; required?: string[];
  additionalProperties?: boolean; items?: Schema; minItems?: number; maxItems?: number; uniqueItems?: boolean;
  minimum?: number; maximum?: number; exclusiveMinimum?: number; minLength?: number; maxLength?: number; pattern?: string;
  anyOf?: Schema[]; oneOf?: Schema[]; allOf?: Schema[]; if?: Schema; then?: Schema;
};
const definitions = schema.definitions as unknown as Record<string, Schema>;
export class ContractError extends Error {}

function valid(rule: Schema, value: unknown, path: string): void {
  const fail = (): never => { throw new ContractError(`Invalid renderer DTO at ${path}.`); };
  const matches = (candidate: Schema): boolean => {
    try { valid(candidate, value, path); return true; } catch { return false; }
  };
  if (rule.$ref) {
    const target = definitions[rule.$ref.replace('#/definitions/', '')];
    if (!target) fail();
    valid(target, value, path);
  }
  if ('const' in rule && value !== rule.const) fail();
  if (rule.enum && !rule.enum.includes(value)) fail();
  if (rule.anyOf && !rule.anyOf.some(matches)) fail();
  if (rule.oneOf && rule.oneOf.filter(matches).length !== 1) fail();
  if (rule.allOf) for (const child of rule.allOf) valid(child, value, path);
  if (rule.if && rule.then && matches(rule.if)) valid(rule.then, value, path);
  switch (rule.type) {
    case 'null': if (value !== null) fail(); break;
    case 'boolean': if (typeof value !== 'boolean') fail(); break;
    case 'integer': if (typeof value !== 'number' || !Number.isSafeInteger(value)) fail(); break;
    case 'number': if (typeof value !== 'number' || !Number.isFinite(value)) fail(); break;
    case 'string': if (typeof value !== 'string') fail(); break;
    case 'array': if (!Array.isArray(value)) fail(); break;
    case 'object': if (!value || typeof value !== 'object' || Array.isArray(value)) fail(); break;
  }
  if (typeof value === 'number') {
    if (!Number.isFinite(value) || (rule.minimum !== undefined && value < rule.minimum)
      || (rule.maximum !== undefined && value > rule.maximum)
      || (rule.exclusiveMinimum !== undefined && value <= rule.exclusiveMinimum)) fail();
  }
  if (typeof value === 'string' && ((rule.minLength !== undefined && value.length < rule.minLength)
    || (rule.maxLength !== undefined && value.length > rule.maxLength)
    || (rule.pattern && !new RegExp(rule.pattern).test(value)))) fail();
  if (Array.isArray(value)) {
    if ((rule.minItems !== undefined && value.length < rule.minItems)
      || (rule.maxItems !== undefined && value.length > rule.maxItems)
      || (rule.uniqueItems && new Set(value.map(item => JSON.stringify(item))).size !== value.length)) fail();
    if (rule.items) value.forEach((item, i) => valid(rule.items!, item, `${path}[${i}]`));
  } else if (value && typeof value === 'object') {
    const object = value as Record<string, unknown>;
    for (const key of rule.required ?? []) if (!Object.prototype.hasOwnProperty.call(object, key)) fail();
    if (rule.additionalProperties === false && Object.keys(object).some(key => !Object.prototype.hasOwnProperty.call(rule.properties, key))) fail();
    for (const [key, child] of Object.entries(rule.properties ?? {})) {
      if (Object.prototype.hasOwnProperty.call(object, key)) valid(child, object[key], `${path}.${key}`);
    }
  }
}

/** Validate against the accepted schema itself, including nested extra-field/privacy restrictions. */
export function assertDto<T>(definition: string, value: unknown): asserts value is T {
  const rule = definitions[definition];
  if (!rule) throw new ContractError('Unknown renderer DTO definition.');
  valid(rule, value, definition);
}

export function opaqueId(prefix: string): string {
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return `${prefix}_${Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('')}`;
}

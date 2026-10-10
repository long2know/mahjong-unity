import type { Thing } from '../thing';
import { GameType } from '../types';
import { RendererFrameAdapter, type LiveEntity } from './renderer-frame';
import {
  assertDto, CONTRACT_VERSION, type ActionResult, type RendererAction,
} from './renderer-contract';

export interface ActionPort {
  result(result: ActionResult): void;
  notice(message: string): void;
  unavailable(message: string): void;
}
interface Gesture { token: string; session: string; actor: number; anchor: LiveEntity; entities: LiveEntity[] }

/** Unity supplies semantic intent only; every wire write stays in the retained World/Client pipeline. */
export class RendererActionRouter {
  private seen = new Set<string>();
  private epoch = '';
  private gesture: Gesture | null = null;

  constructor(private readonly adapter: RendererFrameAdapter, private readonly port: ActionPort) {}

  handle(input: unknown): ActionResult | null {
    let action: RendererAction;
    try {
      const value: unknown = typeof input === 'string' ? JSON.parse(input) : input;
      assertDto<RendererAction>('RendererAction', value);
      action = value;
    } catch {
      this.cancelGesture();
      this.port.unavailable('Malformed Unity action. No game command was sent.');
      return null;
    }
    const { adapter } = this;
    if (this.epoch !== adapter.sessionId) {
      this.epoch = adapter.sessionId;
      this.seen.clear();
      this.gesture = null;
    }
    const result = (status: ActionResult['status'], reason: ActionResult['reason'] = null, deliver = true): ActionResult => {
      const value: ActionResult = {
        contractVersion: CONTRACT_VERSION, instanceId: action.instanceId, sessionId: action.sessionId,
        type: 'actionResult', actionId: action.actionId, status, reason,
      };
      if (deliver) this.port.result(value);
      return value;
    };
    if (action.instanceId !== adapter.instanceId || action.sessionId !== adapter.sessionId) {
      this.port.notice('Ignored an action from an obsolete Unity session.');
      return result('ignored', 'stale-session', false);
    }
    if (this.seen.has(action.actionId)) {
      this.port.notice('Ignored a duplicate Unity action.');
      return result('ignored', null, false);
    }
    this.seen.add(action.actionId);
    if (!adapter.contextKnown(action.contextFrameId)) return result('rejected', 'stale-entity');
    if (!adapter.actionable()) return result('rejected', adapter.client.protocolUnavailableReason ? 'protocol-unavailable' : 'not-actionable');

    const intent = action.intent;
    const world = adapter.world;
    const submitted = (): ActionResult => {
      adapter.refresh();
      return result(adapter.client.connected() ? 'submitted' : 'local');
    };
    const resolve = (id: string, rules = false): LiveEntity | null => adapter.resolveEntity(id, action.contextFrameId, rules);
    const selected = (ids: string[]): LiveEntity[] | null => {
      const records = ids.map(id => resolve(id));
      if (records.some(record => record === null)) return null;
      const live = records as LiveEntity[];
      const things = live.map(record => record.thing);
      const anchor = things[0];
      return live.every(record => world.rendererCanSelect(record.thing, things)
        && record.thing.slot.group === anchor.slot.group && record.thing.slot.seat === anchor.slot.seat) ? live : null;
    };
    switch (intent.kind) {
      case 'discard': {
        const record = resolve(intent.entityId, true);
        if (!record) return result('rejected', 'stale-entity');
        if (!record.entity.interaction.canDiscard || !world.rendererInteraction(record.thing).canDiscard
          || !world.emitDiscard(record.thing)) return result('rejected', 'not-actionable');
        return submitted();
      }
      case 'takePickup': {
        const record = resolve(intent.triggerEntityId, true);
        if (!record) return result('rejected', 'stale-entity');
        if (!record.entity.interaction.canPickup || !world.rendererInteraction(record.thing).canPickup
          || !world.emitTakePickup()) return result('rejected', 'not-actionable');
        return submitted();
      }
      case 'rollDice':
        if (!adapter.contextKnown(action.contextFrameId, true) || !adapter.latest?.dice?.canRoll
          || !world.rendererCanRollDice()) return result('rejected', 'not-actionable');
        world.emitRollDice();
        return submitted();
      case 'hover': {
        if (this.gesture) return result('rejected', 'not-actionable');
        const record = intent.entityId === null ? null : resolve(intent.entityId);
        if (intent.entityId !== null && (!record || !world.rendererCanSelect(record.thing))) return result('rejected', 'stale-entity');
        world.onHover(record?.thing.index ?? null);
        adapter.refresh();
        return result('local');
      }
      case 'select': {
        if (this.gesture) return result('rejected', 'not-actionable');
        const records = selected(intent.entityIds);
        if (records === null) return result('rejected', 'not-actionable');
        world.onSelect(records.map(record => record.thing.index));
        adapter.refresh();
        return result('local');
      }
      case 'flip': {
        if (this.gesture || world.conditions.gameType === GameType.CHANGSHA) return result('rejected', 'not-actionable');
        const records = selected(intent.entityIds);
        if (records === null || records.some(record => !record.entity.interaction.canFlip
          || (records.length > 1 && !record.thing.slot.canFlipMultiple))) return result('rejected', 'not-actionable');
        world.onSelect(records.map(record => record.thing.index));
        world.onFlip(intent.direction, intent.animated);
        return submitted();
      }
      case 'beginDrag': {
        if (this.gesture || world.conditions.gameType === GameType.CHANGSHA) return result('rejected', 'not-actionable');
        const records = selected(intent.entityIds);
        const anchor = records?.find(record => record.entity.entityId === intent.anchorEntityId);
        if (!records || !anchor || records.some(record => !record.entity.interaction.canDrag)
          || !world.beginRendererDrag(anchor.thing, records.map(record => record.thing))) return result('rejected', 'not-actionable');
        this.gesture = {
          token: intent.gestureId, session: adapter.sessionId, actor: adapter.latest!.session.actorSeat!,
          anchor, entities: records,
        };
        return submitted();
      }
      case 'dragToSlot': {
        if (!this.activeGesture(intent.gestureId, action.contextFrameId)) return result('rejected', 'not-actionable');
        const slot = world.slots.get(intent.slotName);
        if (!slot || slot.offTable || !world.dragRendererToSlot(slot)) return result('rejected', 'invalid-target');
        return submitted();
      }
      case 'endDrag': {
        if (!this.activeGesture(intent.gestureId, action.contextFrameId)) return result('rejected', 'not-actionable');
        const committed = world.endRendererDrag(intent.commit);
        this.gesture = null;
        if (intent.commit && !committed) return result('rejected', 'invalid-target');
        return submitted();
      }
    }
  }

  private activeGesture(token: string, frameId: number): boolean {
    const gesture = this.gesture;
    if (!gesture || gesture.token !== token) return false;
    const live = gesture.session === this.adapter.sessionId
      && this.adapter.latest?.session.actorSeat === gesture.actor
      && this.adapter.contextKnown(frameId)
      && gesture.entities.every(record => record.thing.claimedBy === gesture.actor
        && this.adapter.world.things.get(record.thing.index) === record.thing
        && this.adapter.resolveEntity(record.entity.entityId, frameId) !== null);
    if (!live) this.cancelGesture();
    return live;
  }

  cancelGesture(): void {
    if (this.gesture) {
      if (this.gesture.session === this.adapter.sessionId && this.adapter.actionable()) this.adapter.world.endRendererDrag(false);
      else this.adapter.world.revokeRendererInput();
    }
    this.gesture = null;
  }
}

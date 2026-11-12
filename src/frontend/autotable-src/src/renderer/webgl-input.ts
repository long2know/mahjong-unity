import { opaqueId, type ActionResult, type Entity, type Frame, type Intent } from './renderer-contract';

interface Target { kind: 'entity' | 'dice'; id: string | null }
export interface WebglInputHooks {
  frame(): Frame | null;
  pick(x: number, y: number, touch: boolean): Target | null;
  slot(x: number, y: number): string | null;
  intent(intent: Intent): void;
  pan(dx: number, dy: number): void;
  resumeGameplay?(): boolean;
}
interface Contact {
  pointer: number; x: number; y: number; start: number; target: Target | null;
  touch: boolean; right: boolean; moved: boolean; drag: string | null; slot: string | null;
}

export class WebglInput {
  private readonly events = new AbortController();
  private contact: Contact | null = null;
  private panning: { pointer: number; x: number; y: number } | null = null;
  private click: { id: string; at: number } | null = null;
  private hovered: string | null = null;
  private lastPointerTime = 0;

  constructor(private readonly canvas: HTMLCanvasElement, private readonly hooks: WebglInputHooks) {
    const signal = this.events.signal;
    canvas.addEventListener('pointerdown', this.down, { signal });
    window.addEventListener('pointermove', this.move, { signal });
    window.addEventListener('pointerup', this.up, { signal });
    window.addEventListener('pointercancel', this.canceled, { signal });
    canvas.addEventListener('lostpointercapture', this.lost, { signal });
    canvas.addEventListener('pointerleave', () => {
      if (!this.contact && !this.panning) this.hover(null);
    }, { signal });
    canvas.addEventListener('keydown', event => {
      if (event.key === 'Escape') { this.cancel(); this.hooks.intent({ kind: 'select', entityIds: [] }); }
      if (!event.altKey && !event.ctrlKey && !event.metaKey && !event.repeat && ['f', 'r'].includes(event.key.toLowerCase())) {
        if (this.hooks.resumeGameplay && !this.hooks.resumeGameplay()) return;
        const entities = this.hooks.frame()?.entities.filter(entity => entity.display.selected && entity.interaction.canFlip) ?? [];
        if (entities.length) {
          event.preventDefault();
          this.hooks.intent({ kind: 'flip', entityIds: entities.map(entity => entity.entityId),
            direction: event.key.toLowerCase() === 'f' ? 1 : -1, animated: event.shiftKey });
        }
      }
    }, { signal });
  }

  private point(event: PointerEvent): { x: number; y: number } {
    const rect = this.canvas.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }
  private pointerTime(event: PointerEvent): number | null {
    const time = event.timeStamp;
    if (!Number.isFinite(time) || time < 0 || time > performance.now() || time < this.lastPointerTime) {
      this.cancel(); return null;
    }
    this.lastPointerTime = time;
    return time;
  }
  private entity(id: string | null): Entity | null {
    return id ? this.hooks.frame()?.entities.find(entity => entity.entityId === id) ?? null : null;
  }
  private hover(id: string | null): void {
    if (this.hovered === id) return;
    this.hovered = id;
    this.hooks.intent({ kind: 'hover', entityId: id });
  }
  private readonly down = (event: PointerEvent): void => {
    if (event.defaultPrevented || event.altKey || event.button === 1 || !event.isPrimary) { this.cancel(); return; }
    const time = this.pointerTime(event);
    if (time === null) return;
    if (!this.hooks.frame()) return;
    const at = this.point(event);
    if (event.button === 2 && event.shiftKey) {
      this.cancel();
      this.panning = { pointer: event.pointerId, ...at };
      this.canvas.setPointerCapture(event.pointerId);
      event.preventDefault();
      return;
    }
    if (event.button !== 0 && event.button !== 2) return;
    this.cancelContact();
    const target = this.hooks.pick(at.x, at.y, event.pointerType === 'touch');
    // Frame receipt/picking work must not consume the native gesture's timing budget.
    this.contact = { pointer: event.pointerId, ...at, start: time, target,
      touch: event.pointerType === 'touch', right: event.button === 2, moved: false, drag: null, slot: null };
    this.canvas.focus({ preventScroll: true });
    this.canvas.setPointerCapture(event.pointerId);
    if (event.pointerType !== 'touch' && event.button === 0) {
      const entity = this.entity(target?.id ?? null);
      if (entity?.interaction.canSelect) {
        const selected = event.shiftKey ? (this.hooks.frame()?.entities.filter(item => item.display.selected
          && item.seat === entity.seat && item.group === entity.group).map(item => item.entityId) ?? []) : [];
        this.hooks.intent({ kind: 'select', entityIds: [...new Set([...selected, entity.entityId])] });
      } else if (!target) this.hooks.intent({ kind: 'select', entityIds: [] });
    }
    event.preventDefault();
  };
  private readonly move = (event: PointerEvent): void => {
    const at = this.point(event);
    if (this.panning?.pointer === event.pointerId) {
      this.hooks.pan(at.x - this.panning.x, at.y - this.panning.y);
      this.panning = { pointer: event.pointerId, ...at };
      event.preventDefault(); return;
    }
    const contact = this.contact;
    if (!contact) {
      if (event.target === this.canvas && event.pointerType !== 'touch' && !event.buttons) {
        const hit = this.hooks.pick(at.x, at.y, false);
        const entity = this.entity(hit?.id ?? null);
        this.hover(entity?.interaction.canSelect ? entity.entityId : null);
      }
      return;
    }
    if (event.pointerId !== contact.pointer) return;
    if (Math.hypot(at.x - contact.x, at.y - contact.y) > (contact.touch ? 10 : 6)) contact.moved = true;
    if (!contact.moved || contact.right) return;
    const entity = this.entity(contact.target?.id ?? null);
    if (!contact.drag && entity?.interaction.canDrag) {
      contact.drag = opaqueId('gesture');
      const selected = this.hooks.frame()?.entities.filter(item => item.display.selected && item.interaction.canDrag
        && item.seat === entity.seat && item.group === entity.group).map(item => item.entityId) ?? [];
      this.hooks.intent({ kind: 'beginDrag', gestureId: contact.drag, anchorEntityId: entity.entityId,
        entityIds: [...new Set([...selected, entity.entityId])] });
    }
    if (contact.drag) {
      const slot = this.hooks.slot(at.x, at.y);
      if (slot && slot !== contact.slot) {
        contact.slot = slot;
        this.hooks.intent({ kind: 'dragToSlot', gestureId: contact.drag, slotName: slot });
      }
    }
  };
  private readonly up = (event: PointerEvent): void => {
    if (this.panning?.pointer === event.pointerId) { this.panning = null; return; }
    const contact = this.contact;
    if (!contact || contact.pointer !== event.pointerId) return;
    const time = this.pointerTime(event);
    if (time === null) return;
    this.contact = null;
    if (contact.drag) {
      this.hooks.intent({ kind: 'endDrag', gestureId: contact.drag, commit: contact.slot !== null });
      this.click = null; return;
    }
    const at = this.point(event);
    if (event.defaultPrevented || contact.moved || Math.hypot(at.x - contact.x, at.y - contact.y) > (contact.touch ? 10 : 6)
      || time - contact.start > 650) { this.click = null; return; }
    const target = this.hooks.pick(at.x, at.y, contact.touch);
    if (target?.kind !== contact.target?.kind || target?.id !== contact.target?.id) { this.click = null; return; }
    if (target?.kind === 'dice') {
      this.hooks.intent({ kind: 'rollDice' }); this.click = null; return;
    }
    const entity = this.entity(target?.id ?? null);
    if (!entity) { this.click = null; return; }
    if (contact.right && entity.interaction.canFlip) {
      this.hooks.intent({ kind: 'flip', entityIds: [entity.entityId], direction: event.shiftKey ? -1 : 1, animated: true });
      this.click = null; return;
    }
    if (entity.interaction.canPickup) {
      this.hooks.intent({ kind: 'takePickup', triggerEntityId: entity.entityId }); this.click = null; return;
    }
    if (contact.touch && entity.interaction.canDiscard || !contact.right && entity.interaction.canDiscard
      && this.click?.id === entity.entityId && time - this.click.at < 400) {
      this.hooks.intent({ kind: 'discard', entityId: entity.entityId }); this.click = null; return;
    }
    if (contact.touch && entity.interaction.canSelect) this.hooks.intent({ kind: 'select', entityIds: [entity.entityId] });
    this.click = !contact.right ? { id: entity.entityId, at: time } : null;
  };
  private readonly canceled = (event: PointerEvent): void => {
    if (this.contact?.pointer === event.pointerId || this.panning?.pointer === event.pointerId) this.cancel();
  };
  private readonly lost = (event: PointerEvent): void => {
    if (this.contact?.pointer === event.pointerId || this.panning?.pointer === event.pointerId) this.cancel();
  };
  private cancelContact(): void {
    const contact = this.contact;
    this.contact = null;
    if (contact?.drag) this.hooks.intent({ kind: 'endDrag', gestureId: contact.drag, commit: false });
  }
  cancel(): void {
    this.cancelContact(); this.panning = null; this.click = null; this.hovered = null;
  }
  rejected(reason?: ActionResult['reason']): void {
    if (reason === 'invalid-target' && this.contact?.drag) this.contact.slot = null;
    else this.contact = null;
    this.click = null;
  }
  dispose(): void { this.cancel(); this.events.abort(); }
}

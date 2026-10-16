import { getSettings, resetTableCamera, setSettings, type TableCameraSettings } from '../settings-drawer';
import type { Viewport } from './renderer-contract';

interface CameraHooks {
  viewport(): Viewport | null;
  pitch(): number;
  cancel(): void;
}
interface Contact { x: number; y: number; board: boolean; touch: boolean }
interface CameraGesture {
  mouse: number | null;
  x: number;
  y: number;
  distance: number;
  angle: number;
  pitch: number;
  settings: TableCameraSettings;
  turn: boolean;
  tilt: boolean;
  zoom: boolean;
  roll: boolean;
  rollOnly: boolean;
}

const angle = (value: number): number => ((value + 180) % 360 + 360) % 360 - 180;

/** Camera contacts never become a tile gesture, including their final remaining finger. */
export class TableCameraControls {
  private readonly events = new AbortController();
  private readonly contacts = new Map<number, Contact>();
  private gesture: CameraGesture | null = null;
  private frame: number | null = null;
  private consumed = false;
  blocksGameplay = false;

  get manipulating(): boolean { return this.gesture !== null; }

  constructor(private readonly canvas: HTMLCanvasElement, private readonly hooks: CameraHooks) {
    const signal = this.events.signal;
    canvas.setAttribute('aria-keyshortcuts', 'ArrowLeft ArrowRight ArrowUp ArrowDown + - Home');
    canvas.addEventListener('pointerdown', this.down, { capture: true, signal });
    window.addEventListener('pointermove', this.move, { capture: true, signal });
    window.addEventListener('pointerup', this.up, { capture: true, signal });
    window.addEventListener('pointercancel', this.canceled, { capture: true, signal });
    canvas.addEventListener('lostpointercapture', this.lost, { signal });
    window.addEventListener('mousedown', this.mouseDown, { capture: true, signal });
    canvas.addEventListener('wheel', this.wheel, { passive: false, signal });
    canvas.addEventListener('keydown', this.key, { signal });
    window.addEventListener('blur', this.blur, { signal });
    document.addEventListener('visibilitychange', this.visibility, { signal });
    document.addEventListener('pointerdown', event => {
      if (event.target !== canvas) this.cancel();
    }, { capture: true, signal });
  }

  private onBoard(event: MouseEvent): boolean {
    const viewport = this.hooks.viewport();
    const rect = this.canvas.getBoundingClientRect();
    if (!viewport || rect.width <= 0 || rect.height <= 0) return false;
    const x = (event.clientX - rect.left) * viewport.widthCssPx / rect.width;
    const y = (event.clientY - rect.top) * viewport.heightCssPx / rect.height;
    const inset = viewport.insetsCssPx;
    return x >= inset.left && x < viewport.widthCssPx - inset.right && y >= inset.top
      && y < viewport.heightCssPx - inset.bottom - viewport.handBandCssPx;
  }

  private block(): void {
    if (this.blocksGameplay) return;
    this.blocksGameplay = true;
    this.hooks.cancel();
  }

  private start(x: number, y: number, distance: number, rotation: number, mouse: number | null, rollOnly = false): CameraGesture {
    const { tableZoom, tableYaw, tablePitch, tableRoll } = getSettings();
    return {
      x, y, distance, angle: rotation, mouse, rollOnly, pitch: this.hooks.pitch(),
      settings: { tableZoom, tableYaw, tablePitch, tableRoll },
      turn: false, tilt: false, zoom: false, roll: false,
    };
  }

  private pair(): { x: number; y: number; distance: number; angle: number } | null {
    const contacts = [...this.contacts.values()];
    if (contacts.length !== 2 || contacts.some(contact => !contact.touch || !contact.board)) return null;
    const [a, b] = contacts;
    return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2,
      distance: Math.hypot(b.x - a.x, b.y - a.y), angle: Math.atan2(b.y - a.y, b.x - a.x) * 180 / Math.PI };
  }

  private down = (event: PointerEvent): void => {
    if (event.pointerType !== 'touch' && event.button !== 0 && event.button !== 1) {
      if (event.button === 2 && this.contacts.size === 0) this.resumeGameplay();
      return;
    }
    const fresh = this.contacts.size === 0;
    this.contacts.set(event.pointerId, {
      x: event.clientX, y: event.clientY, board: this.onBoard(event), touch: event.pointerType === 'touch',
    });
    if (fresh) {
      this.consumed = false;
      this.blocksGameplay = false;
      this.canvas.focus({ preventScroll: true });
      if (event.pointerType !== 'touch' && this.onBoard(event) && (event.button === 1 || event.altKey)) {
        this.block();
        this.gesture = this.start(event.clientX, event.clientY, 0, 0, event.pointerId, event.shiftKey);
        this.canvas.setPointerCapture(event.pointerId);
        event.preventDefault();
      }
    }
    if (this.contacts.size < 2) return;
    this.block();
    const pair = this.pair();
    if (!this.consumed && pair && pair.distance >= 8) {
      this.gesture = this.start(pair.x, pair.y, pair.distance, pair.angle, null);
      for (const id of this.contacts.keys()) this.canvas.setPointerCapture(id);
    } else {
      this.cancel();
    }
  };

  private move = (event: PointerEvent): void => {
    const contact = this.contacts.get(event.pointerId);
    if (!contact) return;
    contact.x = event.clientX;
    contact.y = event.clientY;
    const gesture = this.gesture;
    if (!gesture) return;
    if (gesture.mouse !== null && gesture.mouse !== event.pointerId) return;
    event.preventDefault();
    if (this.frame === null) this.frame = requestAnimationFrame(() => {
      this.frame = null;
      this.updateGesture();
    });
  };

  private updateGesture(): void {
    const gesture = this.gesture;
    if (!gesture) return;
    const mouse = gesture.mouse === null ? null : this.contacts.get(gesture.mouse);
    const current = mouse ? { x: mouse.x, y: mouse.y, distance: 0, angle: 0 } : this.pair();
    if (!current) return;
    const dx = current.x - gesture.x, dy = current.y - gesture.y;
    const patch: Partial<TableCameraSettings> = {};
    const threshold = gesture.mouse === null ? 8 : 2;
    if (gesture.rollOnly) {
      gesture.roll ||= Math.abs(dx) > threshold;
      if (gesture.roll) patch.tableRoll = angle(gesture.settings.tableRoll + dx * 0.4);
    } else {
      gesture.turn ||= Math.abs(dx) > threshold;
      gesture.tilt ||= Math.abs(dy) > threshold;
      if (gesture.turn) patch.tableYaw = angle(gesture.settings.tableYaw - dx * 0.35);
      if (gesture.tilt) patch.tablePitch = gesture.pitch - dy * 0.25;
      if (gesture.mouse === null) {
        const ratio = current.distance / gesture.distance;
        const twist = angle(current.angle - gesture.angle);
        gesture.zoom ||= Math.abs(ratio - 1) > 0.025;
        gesture.roll ||= Math.abs(twist) > 3;
        if (gesture.zoom) patch.tableZoom = gesture.settings.tableZoom * ratio;
        if (gesture.roll) patch.tableRoll = angle(gesture.settings.tableRoll + twist);
      }
    }
    this.apply(patch);
  }

  private apply(patch: Partial<TableCameraSettings>): void {
    const current = getSettings();
    if ((['tableZoom', 'tableYaw', 'tablePitch', 'tableRoll'] as const)
      .some(key => key in patch && patch[key] !== current[key])
      || typeof patch.tablePitch === 'number' && current.cameraMode !== 'perspective') setSettings(patch);
  }

  private up = (event: PointerEvent): void => {
    const contact = this.contacts.get(event.pointerId);
    if (!contact) return;
    if (this.gesture) {
      contact.x = event.clientX;
      contact.y = event.clientY;
      this.stopFrame();
      this.updateGesture();
      this.gesture = null;
      this.consumed = true;
    }
    this.contacts.delete(event.pointerId);
    if (this.contacts.size === 0) this.consumed = false;
    // Do not unblock here: Unity may still be processing this contact's release.
  };

  private canceled = (event: PointerEvent): void => {
    if (!this.contacts.has(event.pointerId)) return;
    this.cancel();
    this.up(event);
  };

  private lost = (event: PointerEvent): void => {
    if (this.contacts.has(event.pointerId)) this.cancel();
  };

  private mouseDown = (event: MouseEvent): void => {
    if (this.gesture && this.gesture.mouse !== null && event.target === this.canvas) {
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  };

  private wheel = (event: WheelEvent): void => {
    if (!this.onBoard(event)) return;
    event.preventDefault();
    this.cancel();
    const height = this.hooks.viewport()!.heightCssPx;
    const pixels = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? height : 1);
    this.apply({ tableZoom: getSettings().tableZoom * Math.exp(-Math.max(-500, Math.min(500, pixels)) / 500) });
  };

  private key = (event: KeyboardEvent): void => {
    if (document.activeElement !== this.canvas || event.altKey || event.ctrlKey || event.metaKey || event.defaultPrevented) return;
    const settings = getSettings();
    const patch: Partial<TableCameraSettings> = {};
    switch (event.key) {
      case '+': case '=': patch.tableZoom = settings.tableZoom * 1.1; break;
      case '-': case '_': patch.tableZoom = settings.tableZoom / 1.1; break;
      case 'ArrowLeft':
      case 'ArrowRight': {
        const direction = event.key === 'ArrowLeft' ? -1 : 1;
        if (event.shiftKey) patch.tableRoll = angle(settings.tableRoll + direction * 5);
        else patch.tableYaw = angle(settings.tableYaw + direction * 5);
        break;
      }
      case 'ArrowUp': patch.tablePitch = this.hooks.pitch() + 5; break;
      case 'ArrowDown': patch.tablePitch = this.hooks.pitch() - 5; break;
      case 'Home': break;
      default: return;
    }
    event.preventDefault();
    this.cancel();
    if (event.key === 'Home') resetTableCamera();
    else this.apply(patch);
  };

  cancel(): void {
    this.stopFrame();
    this.gesture = null;
    this.consumed = this.contacts.size > 0;
    this.block();
  }

  resumeGameplay(): boolean {
    if (this.blocksGameplay && this.contacts.size > 0) return false;
    this.blocksGameplay = false;
    return true;
  }

  private stopFrame(): void {
    if (this.frame !== null) cancelAnimationFrame(this.frame);
    this.frame = null;
  }

  private blur = (): void => {
    this.cancel();
    for (const id of this.contacts.keys()) if (this.canvas.hasPointerCapture(id)) this.canvas.releasePointerCapture(id);
    this.contacts.clear();
  };

  private visibility = (): void => { if (document.hidden) this.blur(); };

  dispose(): void {
    this.events.abort();
    this.blur();
  }
}

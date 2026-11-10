import {
  assertDto, CONTRACT_VERSION, opaqueId, type ActionResult, type ClearReason, type Frame, type FrameRendered,
  type Intent, type Layout, type Presentation, type RendererAction, type Viewport,
} from './renderer-contract';
import type { HostHooks, HostState, RendererHost } from './renderer-host';
import { rendererViewport } from './renderer-viewport';
import { WebglScene } from './webgl-scene';
import { WebglInput } from './webgl-input';
import './unity-host.css';
import './webgl-host.css';

export class WebglHost implements RendererHost {
  readonly engine = 'webgl2';
  readonly instanceId = opaqueId('instance');
  readonly canvas: HTMLCanvasElement;
  state: HostState = 'idle';
  viewport: Viewport | null = null;
  lastRendered = 0;
  readonly metrics = { renderedFrames: 0, firstFrameAt: 0, frameMilliseconds: [] as number[] };
  private scene: WebglScene | null = null;
  private input: WebglInput | null = null;
  private readonly events = new AbortController();
  private readonly panel: HTMLElement;
  private readonly description: HTMLElement;
  private readonly insets: HTMLElement;
  private observer: ResizeObserver | null = null;
  private mutations: MutationObserver | null = null;
  private media: MediaQueryList | null = null;
  private disposed = false;
  private suspended = false;
  private separateHand = false;
  private sessionId: string | null = null;
  private queue: { layout: Layout; frame: Frame } | null = null;
  private inFlight: { frame: Frame; viewport: number; started: number } | null = null;
  private animation = 0;
  private startupTimer: ReturnType<typeof setTimeout> | null = null;
  private lastSentAt = -Infinity;
  private inputBlocked = true;

  constructor(private readonly main: HTMLElement, private readonly hooks: HostHooks) {
    main.classList.add('unity-table');
    main.dataset.renderer = 'webgl2';
    document.body.dataset.rendererEngine = 'webgl2';
    this.canvas = document.createElement('canvas');
    this.canvas.id = 'unity-canvas';
    this.canvas.dataset.engine = 'webgl2';
    this.canvas.tabIndex = 0;
    this.canvas.setAttribute('aria-label', 'Mahjong table — experimental WebGL2 renderer');
    this.canvas.addEventListener('contextmenu', event => event.preventDefault(), { signal: this.events.signal });
    this.canvas.addEventListener('webglcontextlost', event => {
      event.preventDefault();
      this.unavailable('WebGL context was lost. Reload table to recreate GPU resources; no renderer fallback was started.');
    }, { signal: this.events.signal });
    this.panel = document.createElement('section');
    this.panel.id = 'unity-status';
    this.panel.setAttribute('role', 'status');
    this.panel.setAttribute('aria-live', 'polite');
    const heading = document.createElement('h2');
    heading.textContent = 'Experimental WebGL2 table';
    this.description = document.createElement('p');
    this.description.textContent = 'Preparing tile artwork and GPU resources…';
    const reload = document.createElement('button');
    reload.className = 'btn btn-outline-light';
    reload.textContent = 'Reload table';
    reload.addEventListener('click', () => window.location.reload(), { signal: this.events.signal });
    this.panel.append(heading, this.description, reload);
    this.insets = document.createElement('div');
    this.insets.className = 'unity-inset-probe';
    this.insets.setAttribute('aria-hidden', 'true');
    main.append(this.canvas, this.panel, this.insets);
    const loading = document.getElementById('loading');
    if (loading) loading.hidden = true;
    this.suspended = document.hidden || !document.hasFocus();
    const signal = this.events.signal;
    window.addEventListener('resize', this.resize, { signal });
    window.addEventListener('orientationchange', this.resize, { signal });
    window.visualViewport?.addEventListener('resize', this.resize, { signal });
    window.visualViewport?.addEventListener('scroll', this.resize, { signal });
    window.addEventListener('blur', this.visibility, { signal });
    window.addEventListener('focus', this.visibility, { signal });
    document.addEventListener('visibilitychange', this.visibility, { signal });
    window.addEventListener('mahjong:camera-reset', () => {
      if (this.scene) this.scene.pan = [0, 0];
      this.cancelInput(); this.hooks.resumed(); this.schedule();
    }, { signal });
    if (typeof ResizeObserver !== 'undefined') {
      this.observer = new ResizeObserver(this.resize);
      this.observer.observe(main);
      const chat = document.getElementById('chat-panel');
      if (chat) this.observer.observe(chat);
    }
    if (typeof MutationObserver !== 'undefined') {
      this.mutations = new MutationObserver(this.resize);
      this.mutations.observe(document.body, { childList: true, subtree: true, attributes: true,
        attributeFilter: ['class', 'style', 'hidden'] });
    }
    this.watchDpr();
    this.measure();
  }

  async mount(): Promise<void> {
    if (this.state !== 'idle' || this.disposed) return;
    this.state = 'loading';
    this.main.setAttribute('aria-busy', 'true');
    this.watchStartup();
    try {
      if (!('WebGL2RenderingContext' in window) || typeof ResizeObserver === 'undefined') {
        this.unavailable('This comparison requires WebGL2 and ResizeObserver.', 'unsupported');
        return;
      }
      const scene = await WebglScene.create(this.canvas);
      if (this.disposed || this.state !== 'loading') { scene.dispose(); return; }
      this.scene = scene;
      this.input = new WebglInput(this.canvas, {
        frame: () => this.actionFrame(),
        pick: (x, y, touch) => this.actionFrame() ? this.scene!.pick(x, y, touch) : null,
        slot: (x, y) => this.actionFrame() ? this.scene!.pickSlot(x, y) : null,
        intent: intent => this.intent(intent),
        resumeGameplay: () => this.hooks.resumeGameplay?.() ?? true,
        pan: (dx, dy) => {
          if (!this.scene) return;
          this.inputBlocked = true;
          this.scene.pan = [Math.max(-3, Math.min(3, this.scene.pan[0] - dx * .003)),
            Math.max(-3, Math.min(3, this.scene.pan[1] + dy * .003))];
          this.hooks.resumed(); this.schedule();
        },
      });
      this.state = 'awaiting-ready';
      this.watchStartup();
      this.hooks.notice('Experimental WebGL2 renderer. Shift + right-drag pans; existing camera controls orbit, zoom and reset.');
      this.schedule();
    } catch (error) {
      this.unavailable(error instanceof Error ? error.message : 'WebGL2 startup failed.');
    }
  }

  clear(sessionId: string, _reason: ClearReason): void {
    if (this.disposed) return;
    this.input?.rejected();
    this.sessionId = sessionId; this.queue = null; this.inFlight = null; this.lastRendered = 0;
    this.inputBlocked = true; this.scene?.clear(); this.removeMarkers();
    if (this.animation) cancelAnimationFrame(this.animation);
    this.animation = 0;
    if (this.state === 'ready') this.state = 'awaiting-ready';
  }

  publish(layout: Layout, frame: Frame): void {
    if (this.disposed || ['failed', 'unsupported', 'unavailable'].includes(this.state)) return;
    try {
      assertDto<Layout>('RendererLayout', layout); assertDto<Frame>('RendererFrame', frame);
      if (frame.instanceId !== this.instanceId || layout.instanceId !== this.instanceId
        || frame.sessionId !== this.sessionId || layout.sessionId !== this.sessionId
        || frame.layoutRevision !== layout.layoutRevision || frame.frameId <= this.lastRendered
        || this.queue && frame.frameId <= this.queue.frame.frameId
        || this.inFlight && frame.frameId <= this.inFlight.frame.frameId) throw Error('Mismatched or non-monotonic renderer publication.');
      const previous = this.scene?.committed?.frame;
      if (previous && (previous.layoutRevision !== layout.layoutRevision
        || previous.session.viewSeat !== frame.session.viewSeat
        || JSON.stringify(previous.presentation) !== JSON.stringify(frame.presentation))) this.cancelInput();
      this.queue = { layout, frame };
      this.schedule();
    } catch (error) {
      this.unavailable(error instanceof Error ? error.message : 'Invalid renderer publication.');
    }
  }

  private actionFrame(): Frame | null {
    if (this.disposed || this.suspended || this.state !== 'ready') return null;
    try { this.receipt(); }
    catch (error) { this.unavailable(error instanceof Error ? error.message : 'WebGL input frame failed.'); return null; }
    const commit = this.scene?.committed;
    return !this.inputBlocked && commit?.frame.sessionId === this.sessionId
      && commit.viewport.resizeRevision === this.viewport?.resizeRevision ? commit.frame : null;
  }
  private intent(intent: Intent): void {
    const frame = this.actionFrame();
    if (!frame) return;
    const action: RendererAction = {
      contractVersion: CONTRACT_VERSION, instanceId: this.instanceId, sessionId: frame.sessionId,
      type: 'action', actionId: opaqueId('action'), contextFrameId: frame.frameId, intent,
    };
    assertDto('RendererAction', action);
    window.dispatchEvent(new CustomEvent('mahjong:renderer-action', { detail: { engine: this.engine, action } }));
    this.hooks.action(action);
  }

  actionResult(result: ActionResult): void {
    if (result.instanceId !== this.instanceId || result.sessionId !== this.sessionId || this.disposed) return;
    assertDto('ActionResult', result);
    if (result.status === 'rejected' || result.status === 'ignored') this.input?.rejected(result.reason);
    window.dispatchEvent(new CustomEvent('mahjong:renderer-action-result', { detail: { engine: this.engine, result } }));
  }
  cancelInput(): void {
    this.input?.cancel();
    this.inputBlocked = true;
  }
  setPresentation(presentation: Presentation): void {
    if (this.separateHand === (presentation.handLayout === 'separate')) return;
    this.separateHand = presentation.handLayout === 'separate';
    this.measure(true);
  }

  private schedule(): void {
    if (this.disposed || this.suspended || !this.scene || this.animation || !this.viewport
      || ['failed', 'unsupported', 'unavailable'].includes(this.state) || !this.queue && !this.inFlight) return;
    this.animation = requestAnimationFrame(this.tick);
  }
  private readonly tick = (): void => {
    this.animation = 0;
    if (this.disposed || this.suspended || !this.scene || !this.viewport) return;
    try {
      this.receipt();
      if (this.inFlight && performance.now() - this.inFlight.started > 30000) throw Error('WebGL stopped completing GPU frames.');
      if (!this.inFlight && this.queue && performance.now() - this.lastSentAt >= 1000 / 30) {
        const { layout, frame } = this.queue;
        this.queue = null;
        this.scene.draw(layout, frame, this.viewport);
        this.inFlight = { frame, viewport: this.viewport.resizeRevision, started: performance.now() };
        this.lastSentAt = performance.now();
      }
      this.schedule();
    } catch (error) { this.unavailable(error instanceof Error ? error.message : 'WebGL frame failed.'); }
  };
  private receipt(): void {
    if (!this.inFlight || !this.scene || this.suspended) return;
    const commit = this.scene.poll();
    if (!commit) return;
    const sent = this.inFlight;
    this.inFlight = null;
    const frame = commit.frame;
    if (frame.instanceId !== this.instanceId || frame.sessionId !== this.sessionId
      || frame.frameId !== sent.frame.frameId || frame.layoutRevision !== sent.frame.layoutRevision
      || commit.viewport.resizeRevision !== sent.viewport || frame.frameId <= this.lastRendered) {
      throw Error('Mismatched GPU rendered-frame receipt.');
    }
    const receipt: FrameRendered = {
      contractVersion: CONTRACT_VERSION, instanceId: this.instanceId, sessionId: frame.sessionId,
      type: 'frameRendered', frameId: frame.frameId, layoutRevision: frame.layoutRevision,
    };
    assertDto('FrameRendered', receipt);
    this.lastRendered = frame.frameId;
    this.inputBlocked = !!this.queue && (this.queue.frame.session.viewSeat !== frame.session.viewSeat
      || JSON.stringify(this.queue.frame.presentation) !== JSON.stringify(frame.presentation))
      || commit.viewport.resizeRevision !== this.viewport?.resizeRevision;
    this.state = 'ready'; this.panel.hidden = true;
    this.watchStartup();
    this.main.setAttribute('aria-busy', 'false');
    this.metrics.renderedFrames++;
    this.metrics.firstFrameAt ||= performance.now();
    this.metrics.frameMilliseconds.push(commit.milliseconds);
    if (this.metrics.frameMilliseconds.length > 300) this.metrics.frameMilliseconds.shift();
    this.mark('webgl-renderer-ready'); this.mark('scene-shell-ready');
    window.dispatchEvent(new CustomEvent('mahjong:renderer-frame', { detail: { engine: this.engine, receipt,
      drawMilliseconds: commit.milliseconds, drawCalls: this.scene.drawCount } }));
  }

  private mark(name: string): void {
    if (document.body.getAttribute(`data-${name}`) === 'true') return;
    document.body.setAttribute(`data-${name}`, 'true');
    const marker = document.createElement('span');
    marker.id = name; marker.hidden = true; marker.setAttribute('data-testid', name);
    document.body.append(marker);
    window.dispatchEvent(new CustomEvent(`mahjong:${name}`));
  }
  private removeMarkers(): void {
    for (const name of ['webgl-renderer-ready', 'scene-shell-ready']) {
      document.body.removeAttribute(`data-${name}`); document.getElementById(name)?.remove();
    }
  }

  private readonly resize = (): void => this.measure();
  private measure(force = false): void {
    if (this.disposed) return;
    const visual = window.visualViewport;
    const top = Math.max(0, visual?.offsetTop ?? 0);
    document.documentElement.style.setProperty('--game-viewport-top', `${top}px`);
    document.documentElement.style.setProperty('--game-viewport-bottom',
      `${Math.max(0, window.innerHeight - (visual?.height ?? window.innerHeight) - top)}px`);
    const rect = this.main.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return;
    const style = getComputedStyle(this.insets), number = (value: string): number => Math.max(0, parseFloat(value) || 0);
    let insets = { top: number(style.paddingTop), right: number(style.paddingRight),
      bottom: number(style.paddingBottom), left: number(style.paddingLeft) };
    for (const id of ['new-game', 'turn-banner', 'settings-toggle', 'settings-button', 'move-log-toggle',
      'lobby-toggle', 'variant-badge', 'pickup-hud', 'own-turn-actions', 'claim-window-actions', 'pwa-install-button']) {
      const chrome = document.getElementById(id);
      if (!chrome || chrome.hidden) continue;
      const bounds = chrome.getBoundingClientRect(), computed = getComputedStyle(chrome);
      if (computed.display !== 'none' && computed.visibility !== 'hidden' && computed.visibility !== 'collapse'
        && bounds.width > 0 && bounds.height > 0 && bounds.right > rect.left && bounds.left < rect.right
        && bounds.top < rect.top + rect.height * .45 && bounds.bottom > rect.top) {
        insets.top = Math.max(insets.top, bounds.bottom - rect.top + 8);
      }
    }
    for (const id of ['chat-panel', 'move-log', 'pwa-install-button', 'bot-banner']) {
      const overlay = document.getElementById(id);
      if (!overlay || overlay.hidden) continue;
      const bounds = overlay.getBoundingClientRect(), computed = getComputedStyle(overlay);
      const left = Math.max(rect.left, bounds.left), right = Math.min(rect.right, bounds.right);
      const upper = Math.max(rect.top, bounds.top), lower = Math.min(rect.bottom, bounds.bottom);
      if (computed.display === 'none' || computed.visibility === 'hidden' || right <= left || lower <= upper) continue;
      const candidates = [
        { ...insets, bottom: Math.max(insets.bottom, rect.bottom - upper) },
        { ...insets, top: Math.max(insets.top, lower - rect.top) },
        { ...insets, right: Math.max(insets.right, rect.right - left) },
        { ...insets, left: Math.max(insets.left, right - rect.left) },
      ];
      const area = (value: Viewport['insetsCssPx']): number =>
        Math.max(0, rect.width - value.left - value.right) * Math.max(0, rect.height - value.top - value.bottom);
      insets = candidates.reduce((best, candidate) => area(candidate) > area(best) ? candidate : best);
    }
    const next = rendererViewport(this.instanceId, (this.viewport?.resizeRevision ?? 0) + 1,
      { width: rect.width, height: rect.height, dpr: window.devicePixelRatio || 1, insets, separateHand: this.separateHand });
    if (!force && this.viewport && JSON.stringify({ ...this.viewport, resizeRevision: next.resizeRevision }) === JSON.stringify(next)) return;
    this.cancelInput(); this.scene?.cancelPending(); this.inFlight = null;
    this.viewport = next; this.hooks.resized(next);
    if (this.disposed || this.viewport !== next) return;
    this.canvas.width = next.widthPx; this.canvas.height = next.heightPx;
    this.schedule();
  }
  private watchDpr(): void {
    this.media?.removeEventListener('change', this.dprChanged);
    this.media = window.matchMedia?.(`(resolution: ${window.devicePixelRatio || 1}dppx)`) ?? null;
    this.media?.addEventListener('change', this.dprChanged, { once: true });
  }
  private readonly dprChanged = (): void => { this.measure(); this.watchDpr(); };
  private readonly visibility = (): void => {
    const suspended = document.hidden || !document.hasFocus();
    if (suspended === this.suspended) return;
    this.suspended = suspended;
    this.watchStartup();
    this.cancelInput();
    if (suspended) {
      if (this.animation) cancelAnimationFrame(this.animation);
      this.animation = 0; this.scene?.cancelPending(); this.inFlight = null;
    } else {
      this.measure(true); this.hooks.resumed(); this.schedule();
    }
  };
  private watchStartup(): void {
    if (this.startupTimer) clearTimeout(this.startupTimer);
    this.startupTimer = null;
    if (!this.suspended && ['loading', 'awaiting-ready'].includes(this.state)) this.startupTimer = setTimeout(() => {
      this.unavailable('WebGL2 did not complete startup and a rendered frame. Reload table to retry; no fallback was started.');
    }, 30000);
  }

  unavailable(message: string, state: 'failed' | 'unsupported' = 'failed'): void {
    if (this.disposed || ['failed', 'unavailable', 'unsupported'].includes(this.state)) return;
    this.state = state;
    this.watchStartup();
    this.description.textContent = message;
    this.panel.hidden = false; this.main.setAttribute('aria-busy', 'false');
    if (this.animation) cancelAnimationFrame(this.animation);
    this.animation = 0; this.inputBlocked = true; this.input?.rejected();
    this.queue = null; this.inFlight = null; this.scene?.clear();
    this.removeMarkers(); this.hooks.failure();
  }
  async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true; this.state = 'disposed';
    this.watchStartup();
    if (this.animation) cancelAnimationFrame(this.animation);
    this.events.abort(); this.observer?.disconnect(); this.mutations?.disconnect();
    this.media?.removeEventListener('change', this.dprChanged);
    this.input?.dispose(); this.scene?.dispose(); this.scene = null; this.input = null;
    this.queue = null; this.inFlight = null; this.removeMarkers();
    this.canvas.remove(); this.panel.remove(); this.insets.remove();
    document.body.removeAttribute('data-renderer-engine');
  }
}

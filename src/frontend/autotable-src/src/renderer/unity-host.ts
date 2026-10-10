import {
  assertDto, BRIDGE_RECEIVER, CONTRACT_VERSION, MANIFEST_URL, UNITY_VERSION, opaqueId,
  type ActionResult, type Artifact, type BuildManifest, type ClearReason, type Frame, type FrameRendered,
  type Layout, type Presentation, type Ready, type ReceiverMethod, type RendererAction, type RendererError, type Viewport,
} from './renderer-contract';
import { rendererViewport } from './renderer-viewport';
import './unity-host.css';

export interface UnityInstance {
  SendMessage(receiver: string, method: string, json: string): void;
  Quit(): Promise<void>;
}
type Loader = (canvas: HTMLCanvasElement, config: Record<string, unknown>, progress: (value: number) => void) => Promise<UnityInstance>;
interface BridgeWindow extends Window {
  MahjongUnityBridge?: { emit(json: string): void };
  createUnityInstance?: Loader;
}
export interface HostHooks {
  action(action: RendererAction): void;
  resized(viewport: Viewport): void;
  resumed(): void;
  failure(): void;
  notice(message: string): void;
}
export type HostState = 'idle' | 'loading' | 'awaiting-ready' | 'ready' | 'unavailable' | 'unsupported' | 'failed' | 'disposed';
class HostError extends Error {
  constructor(readonly state: HostState, message: string) { super(message); }
}
const methods: Record<ReceiverMethod, string> = {
  Configure: 'Configure', Clear: 'Clear', ApplyLayout: 'RendererLayout', ApplyFrame: 'RendererFrame',
  SetViewport: 'Viewport', SetPlayback: 'Playback', ReceiveActionResult: 'ActionResult',
};

export function artifactUrl(path: string, origin: string, suffix?: string): string {
  if (!/^[A-Za-z0-9_./-]+$/.test(path) || path.startsWith('/')
    || path.split('/').some(segment => segment === '' || segment === '.' || segment === '..')
    || (suffix && !path.endsWith(suffix))) throw new HostError('unavailable', 'Unsafe or incompatible Unity artifact path.');
  const base = new URL('/autotable/unity/', origin);
  const url = new URL(path, base);
  if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) throw new HostError('unavailable', 'Unity artifacts must be same-origin.');
  return url.href;
}

export function validateBuildManifest(value: unknown, origin: string, allowUnqualifiedBuild = false): BuildManifest {
  assertDto<BuildManifest>('BuildManifest', value);
  const paths = [
    artifactUrl(value.loader.path, origin, '.loader.js'), artifactUrl(value.framework.path, origin, '.framework.js'),
    artifactUrl(value.data.path, origin, '.data'), artifactUrl(value.wasm.path, origin, '.wasm'),
  ];
  if (new Set(paths).size !== paths.length) throw new HostError('unavailable', 'Unity artifact paths must be distinct.');
  if (value.streamingAssetsPath !== null) artifactUrl(value.streamingAssetsPath, origin);
  if (!Number.isFinite(Date.parse(value.builtAtUtc))) throw new HostError('unavailable', 'Invalid Unity build timestamp.');
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(new URL(origin).hostname);
  if (value.developmentBuild) throw new HostError('unavailable', 'Unity development builds are not accepted. Generate a non-development WebGL build.');
  if (value.sourceDirty && !(allowUnqualifiedBuild && local)) throw new HostError('unavailable', 'Unity dirty-source build is not release qualified. Local inspection requires explicit localhost qualification.');
  return value;
}

/** Loads only a validated, real Unity build. A missing/failed build never selects another renderer. */
export class UnityHost {
  readonly instanceId = opaqueId('instance');
  readonly canvas: HTMLCanvasElement;
  state: HostState = 'idle';
  viewport: Viewport | null = null;
  private instance: UnityInstance | null = null;
  private manifest: BuildManifest | null = null;
  private script: HTMLScriptElement | null = null;
  private queue: { layout: Layout; frame: Frame } | null = null;
  private sessionId: string | null = null;
  private clearReason: ClearReason = 'join';
  private handshake = false;
  private disposed = false;
  private suspended = false;
  private sentLayout = 0;
  private sentViewport = 0;
  private lastSentAt = 0;
  private lastRendered = 0;
  private sentFrames = new Map<number, { session: string; layout: number }>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private startupTimer: ReturnType<typeof setTimeout> | null = null;
  private observer: ResizeObserver | null = null;
  private media: MediaQueryList | null = null;
  private readonly events = new AbortController();
  private readonly requests = new AbortController();
  private readonly bridge: { emit(json: string): void };
  private readonly previousBridge: BridgeWindow['MahjongUnityBridge'];
  private readonly panel: HTMLElement;
  private readonly heading: HTMLElement;
  private readonly description: HTMLElement;
  private readonly progress: HTMLProgressElement;
  private readonly retry: HTMLButtonElement;
  private readonly insets: HTMLElement;
  private separateHand = false;

  constructor(private readonly main: HTMLElement, private readonly hooks: HostHooks, private readonly options: { allowUnqualifiedBuild?: boolean } = {}) {
    main.classList.add('unity-table');
    this.canvas = document.createElement('canvas');
    this.canvas.id = 'unity-canvas';
    this.canvas.setAttribute('aria-label', 'Mahjong table — Unity renderer');
    this.canvas.tabIndex = 0;
    this.canvas.addEventListener('contextmenu', event => event.preventDefault(), { signal: this.events.signal });
    this.panel = document.createElement('section');
    this.panel.id = 'unity-status';
    this.panel.setAttribute('role', 'status');
    this.panel.setAttribute('aria-live', 'polite');
    this.panel.setAttribute('data-testid', 'unity-status');
    this.heading = document.createElement('h2');
    this.description = document.createElement('p');
    this.progress = document.createElement('progress');
    this.progress.max = 1;
    this.progress.value = 0;
    this.progress.setAttribute('aria-label', 'Unity loading progress');
    this.retry = document.createElement('button');
    this.retry.type = 'button';
    this.retry.textContent = 'Reload table';
    this.retry.className = 'btn btn-outline-light';
    this.retry.hidden = true;
    this.retry.addEventListener('click', () => {
      void this.dispose().finally(() => window.location.reload());
    }, { signal: this.events.signal });
    this.panel.append(this.heading, this.description, this.progress, this.retry);
    this.insets = document.createElement('div');
    this.insets.className = 'unity-inset-probe';
    this.insets.setAttribute('aria-hidden', 'true');
    main.append(this.canvas, this.panel, this.insets);
    const legacyLoading = document.getElementById('loading');
    if (legacyLoading) legacyLoading.hidden = true;
    const win = window as BridgeWindow;
    this.previousBridge = win.MahjongUnityBridge;
    this.bridge = { emit: json => this.callback(json) };
    win.MahjongUnityBridge = this.bridge;
    this.suspended = document.hidden;
    window.addEventListener('resize', this.resize, { signal: this.events.signal });
    window.addEventListener('orientationchange', this.resize, { signal: this.events.signal });
    window.visualViewport?.addEventListener('resize', this.resize, { signal: this.events.signal });
    window.visualViewport?.addEventListener('scroll', this.resize, { signal: this.events.signal });
    document.addEventListener('visibilitychange', this.visibility, { signal: this.events.signal });
    document.addEventListener('securitypolicyviolation', this.csp, { signal: this.events.signal });
    if (typeof ResizeObserver !== 'undefined') {
      this.observer = new ResizeObserver(this.resize);
      this.observer.observe(main);
    }
    this.watchDpr();
    this.resize();
    this.status('idle', 'Unity table', 'The browser lobby and controls remain available while the renderer loads.');
  }

  private status(state: HostState, title: string, message: string): void {
    this.state = state;
    this.panel.dataset.state = state;
    this.heading.textContent = title;
    this.description.textContent = message;
    this.panel.hidden = false;
    this.progress.hidden = state !== 'loading';
    this.retry.hidden = !['unavailable', 'unsupported', 'failed'].includes(state);
    this.main.setAttribute('aria-busy', String(state === 'loading' || state === 'awaiting-ready'));
  }

  async mount(): Promise<void> {
    if (this.state !== 'idle' || this.disposed) return;
    this.status('loading', 'Loading Unity renderer', 'Checking the same-origin Unity build manifest…');
    try {
      if (typeof WebAssembly === 'undefined' || !('WebGL2RenderingContext' in window)
        || typeof ResizeObserver === 'undefined' || !globalThis.crypto?.subtle) {
        throw new HostError('unsupported', 'Unity requires WebAssembly, WebGL 2, ResizeObserver and a secure browser context. Lobby/chat remain available.');
      }
      const response = await fetch(MANIFEST_URL, {
        cache: 'no-store', credentials: 'same-origin', redirect: 'error',
        headers: { Accept: 'application/json' }, signal: this.requests.signal,
      });
      if (!response.ok) throw new HostError('unavailable', `Unity build unavailable (manifest HTTP ${response.status}). Generate and stage a licensed Unity WebGL build; lobby/chat/reconnect still work.`);
      if (response.headers.get('content-type')?.split(';')[0].trim() !== 'application/json') {
        throw new HostError('unavailable', 'Unity manifest is not JSON. An HTML fallback/login page is not a Unity build.');
      }
      const manifest = validateBuildManifest(await response.json(), window.location.origin, this.options.allowUnqualifiedBuild);
      this.manifest = manifest;
      for (const [kind, file] of [
        ['loader', manifest.loader], ['framework', manifest.framework], ['data', manifest.data], ['wasm', manifest.wasm],
      ] as const) {
        this.description.textContent = `Checking Unity ${kind} integrity…`;
        await this.verifyArtifact(kind, file);
        if (this.disposed) return;
      }
      this.description.textContent = 'Loading the Unity-generated loader…';
      await this.loadScript(manifest.loader);
      if (this.disposed) return;
      const loader = (window as BridgeWindow).createUnityInstance;
      if (typeof loader !== 'function') throw new HostError('failed', 'The generated Unity loader did not expose createUnityInstance.');
      const url = (file: Artifact): string => artifactUrl(file.path, window.location.origin);
      const instance = await loader(this.canvas, {
        dataUrl: url(manifest.data), frameworkUrl: url(manifest.framework), codeUrl: url(manifest.wasm),
        streamingAssetsUrl: manifest.streamingAssetsPath !== null ? artifactUrl(manifest.streamingAssetsPath, window.location.origin) : '',
        companyName: 'mahjong-unity', productName: 'Mahjong Renderer', productVersion: CONTRACT_VERSION,
        matchWebGLToCanvasSize: false, devicePixelRatio: this.viewport?.renderPixelRatio ?? Math.min(window.devicePixelRatio || 1, 2),
        showBanner: (_message: string, type: string) => {
          if (type === 'error') this.unavailable('Unity runtime loading failed. Check the build files, WebGL support and CSP; no alternate renderer was started.');
        },
      }, value => {
        if (!this.disposed && this.state === 'loading') this.progress.value = Math.min(1, Math.max(0, value));
      });
      if (this.disposed || ['failed', 'unavailable', 'unsupported'].includes(this.state)) {
        await instance.Quit();
        return;
      }
      if (!instance || typeof instance.SendMessage !== 'function' || typeof instance.Quit !== 'function') {
        throw new HostError('failed', 'Invalid Unity runtime instance.');
      }
      this.instance = instance;
      this.status('awaiting-ready', 'Starting Unity renderer', 'Waiting for the matching C# bridge handshake and rendered-frame receipt…');
      this.startupTimer = setTimeout(() => {
        this.unavailable('Unity did not complete its C# handshake/rendered-frame receipt. Reload the table or inspect the legitimate build.');
      }, 30000);
      this.send('Configure', {
        contractVersion: CONTRACT_VERSION, instanceId: this.instanceId, type: 'configure', buildId: manifest.buildId,
      });
    } catch (error) {
      if (!this.disposed) {
        const state = error instanceof HostError ? error.state : 'failed';
        const message = error instanceof HostError ? error.message : 'Unity build loading failed (network, corrupt manifest/artifact, loader or CSP). Lobby/chat remain available.';
        this.fail(state, message);
      }
    }
  }

  private async verifyArtifact(kind: string, file: Artifact): Promise<void> {
    const response = await fetch(artifactUrl(file.path, window.location.origin), {
      cache: 'no-cache', credentials: 'same-origin', redirect: 'error', signal: this.requests.signal,
    });
    if (!response.ok) throw new HostError('unavailable', `Unity ${kind} file unavailable (HTTP ${response.status}).`);
    const mime = response.headers.get('content-type')?.split(';')[0].trim();
    const expected = kind === 'wasm' ? ['application/wasm'] : kind === 'data' ? ['application/octet-stream']
      : ['application/javascript', 'text/javascript'];
    if (!mime || !expected.includes(mime)) throw new HostError('unavailable', `Unity ${kind} has an incompatible content type (HTML fallbacks are rejected).`);
    const bytes = await response.arrayBuffer();
    if (bytes.byteLength !== file.bytes) throw new HostError('unavailable', `Unity ${kind} file length differs from its build manifest.`);
    if (kind === 'wasm' && ![0, 97, 115, 109].every((value, i) => new Uint8Array(bytes)[i] === value)) {
      throw new HostError('unavailable', 'Unity wasm file does not contain WebAssembly magic.');
    }
    const hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), value => value.toString(16).padStart(2, '0')).join('');
    if (hash !== file.sha256) throw new HostError('unavailable', `Unity ${kind} SHA-256 differs from its build manifest.`);
  }

  private loadScript(file: Artifact): Promise<void> {
    return new Promise((resolve, reject) => {
      const script = document.createElement('script');
      this.script = script;
      const digest = file.sha256.match(/../g)!.map(byte => String.fromCharCode(parseInt(byte, 16))).join('');
      script.integrity = `sha256-${btoa(digest)}`;
      script.crossOrigin = 'anonymous';
      script.src = artifactUrl(file.path, window.location.origin);
      script.async = true;
      script.onload = () => resolve();
      script.onerror = () => reject(new HostError('failed', 'Unity loader was blocked or failed to load (network, integrity or CSP).'));
      document.head.appendChild(script);
    });
  }

  clear(sessionId: string, reason: ClearReason): void {
    if (this.disposed) return;
    this.sessionId = sessionId;
    this.clearReason = reason;
    this.queue = null;
    this.sentLayout = 0;
    this.sentFrames.clear();
    this.lastRendered = 0;
    this.removeMarkers();
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    if (this.handshake) this.send('Clear', {
      contractVersion: CONTRACT_VERSION, instanceId: this.instanceId, sessionId, type: 'clear', reason,
    });
    if (reason === 'disconnect' && this.handshake) {
      this.status('awaiting-ready', 'Table connection lost', 'Private tiles and gestures were cleared. Waiting for the existing reconnect flow; lobby/chat remain available.');
    }
  }

  publish(layout: Layout, frame: Frame): void {
    if (this.disposed || ['unavailable', 'unsupported', 'failed'].includes(this.state)) return;
    try {
      assertDto<Layout>('RendererLayout', layout);
      assertDto<Frame>('RendererFrame', frame);
      if (layout.instanceId !== this.instanceId || frame.instanceId !== this.instanceId
        || frame.sessionId !== this.sessionId || layout.sessionId !== this.sessionId
        || frame.layoutRevision !== layout.layoutRevision) throw new Error('Mismatched renderer publication.');
      this.queue = { layout, frame };
      this.schedule();
    } catch { this.unavailable('Invalid or stale browser renderer publication; no frame was sent.'); }
  }

  actionResult(result: ActionResult): void {
    if (this.handshake && result.instanceId === this.instanceId && result.sessionId === this.sessionId) this.send('ReceiveActionResult', result);
  }

  setPresentation(presentation: Presentation): void {
    if (this.separateHand !== (presentation.handLayout === 'separate')) {
      this.separateHand = presentation.handLayout === 'separate';
      this.measure(true);
    }
  }

  private schedule(): void {
    if (!this.handshake || this.suspended || this.timer || !this.queue || !this.viewport) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      this.flush();
    }, Math.max(0, 1000 / 30 - (performance.now() - this.lastSentAt)));
  }

  private flush(): void {
    if (!this.handshake || this.disposed || this.suspended || !this.queue || !this.viewport) return;
    const { layout, frame } = this.queue;
    this.queue = null;
    if (this.viewport.resizeRevision !== this.sentViewport) {
      this.send('SetViewport', this.viewport);
      this.sentViewport = this.viewport.resizeRevision;
    }
    if (layout.layoutRevision !== this.sentLayout) {
      this.send('ApplyLayout', layout);
      this.sentLayout = layout.layoutRevision;
    }
    this.sentFrames.set(frame.frameId, { session: frame.sessionId, layout: frame.layoutRevision });
    this.send('ApplyFrame', frame);
    this.lastSentAt = performance.now();
    if (this.sentFrames.size > 240) this.sentFrames.delete(this.sentFrames.keys().next().value!);
  }

  private send(method: ReceiverMethod, value: unknown): void {
    if (!this.instance || this.disposed || (method !== 'Configure' && !this.handshake)) return;
    try {
      assertDto(methods[method], value);
      this.instance.SendMessage(BRIDGE_RECEIVER, method, JSON.stringify(value));
    } catch { this.unavailable(`Unity ${method} failed; renderer stopped without a fallback.`); }
  }

  private callback(json: string): void {
    if (this.disposed || ['unavailable', 'unsupported', 'failed'].includes(this.state)) return;
    try {
      if (typeof json !== 'string' || json.length > 65536) throw new Error('Invalid Unity callback.');
      const value: unknown = JSON.parse(json);
      if (!value || typeof value !== 'object') throw new Error('Invalid Unity callback.');
      const message = value as { type?: string; instanceId?: string; sessionId?: string };
      const definition = { ready: 'Ready', frameRendered: 'FrameRendered', action: 'RendererAction', error: 'RendererError' }[message.type ?? ''];
      if (!definition) throw new Error('Unknown Unity callback.');
      assertDto(definition, value);
      if (message.instanceId !== this.instanceId || (message.sessionId !== undefined && message.sessionId !== null && message.sessionId !== this.sessionId)) {
        this.hooks.notice('Ignored a callback from an obsolete Unity instance/session.');
        return;
      }
      switch (message.type) {
        case 'ready': {
          const ready = value as Ready;
          if (!this.instance || this.handshake || ready.buildId !== this.manifest?.buildId || ready.unityVersion !== UNITY_VERSION) {
            throw new Error('Incompatible Unity handshake.');
          }
          this.handshake = true;
          if (this.sessionId) this.send('Clear', {
            contractVersion: CONTRACT_VERSION, instanceId: this.instanceId, sessionId: this.sessionId, type: 'clear', reason: this.clearReason,
          });
          if (this.viewport) {
            this.send('SetViewport', this.viewport);
            this.sentViewport = this.viewport.resizeRevision;
          }
          this.send('SetPlayback', {
            contractVersion: CONTRACT_VERSION, instanceId: this.instanceId, type: 'playback', suspended: this.suspended,
          });
          this.schedule();
          break;
        }
        case 'frameRendered': {
          const receipt = value as FrameRendered;
          const sent = this.sentFrames.get(receipt.frameId);
          if (!this.handshake || !sent || sent.session !== receipt.sessionId || sent.layout !== receipt.layoutRevision
            || receipt.frameId <= this.lastRendered) throw new Error('Invalid rendered-frame receipt.');
          this.lastRendered = receipt.frameId;
          if (this.startupTimer) clearTimeout(this.startupTimer);
          this.startupTimer = null;
          this.state = 'ready';
          this.main.setAttribute('aria-busy', 'false');
          this.panel.hidden = true;
          for (const name of ['unity-renderer-ready', 'scene-shell-ready']) this.mark(name);
          if (this.manifest?.sourceDirty || this.manifest?.developmentBuild) this.hooks.notice('Local Unity build rendered; dirty/development source remains unqualified for release.');
          break;
        }
        case 'action':
          if (!this.handshake || this.lastRendered === 0) throw new Error('Unity input before a rendered frame.');
          this.hooks.action(value as RendererAction);
          break;
        case 'error': {
          const error = value as RendererError;
          this.unavailable(`Unity bridge ${error.code}. The renderer stopped; lobby/chat remain available.`);
          break;
        }
      }
    } catch { this.unavailable('Malformed/incompatible Unity callback or handshake. No game command was sent.'); }
  }

  private mark(name: string): void {
    const key = `data-${name}`;
    if (document.body.getAttribute(key) === 'true') return;
    document.body.setAttribute(key, 'true');
    const marker = document.createElement('span');
    marker.id = name;
    marker.hidden = true;
    marker.setAttribute('data-testid', name);
    marker.setAttribute('aria-hidden', 'true');
    document.body.appendChild(marker);
    window.dispatchEvent(new CustomEvent(`mahjong:${name}`));
  }

  private removeMarkers(): void {
    for (const name of ['unity-renderer-ready', 'scene-shell-ready']) {
      document.body.removeAttribute(`data-${name}`);
      document.getElementById(name)?.remove();
    }
  }

  private readonly resize = (): void => { this.measure(); };
  private measure(force = false): void {
    if (this.disposed) return;
    const visual = window.visualViewport;
    const top = Math.max(0, visual?.offsetTop ?? 0);
    const bottom = Math.max(0, window.innerHeight - (visual?.height ?? window.innerHeight) - top);
    document.documentElement.style.setProperty('--game-viewport-top', `${top}px`);
    document.documentElement.style.setProperty('--game-viewport-bottom', `${bottom}px`);
    const rect = this.main.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return;
    const padding = getComputedStyle(this.insets);
    const number = (value: string): number => Math.max(0, parseFloat(value) || 0);
    const measurement = {
      width: rect.width, height: rect.height, dpr: window.devicePixelRatio || 1, separateHand: this.separateHand,
      insets: { top: number(padding.paddingTop), right: number(padding.paddingRight), bottom: number(padding.paddingBottom), left: number(padding.paddingLeft) },
    };
    const next = rendererViewport(this.instanceId, (this.viewport?.resizeRevision ?? 0) + 1, measurement);
    const previous = this.viewport ? { ...this.viewport, resizeRevision: next.resizeRevision } : null;
    if (!force && JSON.stringify(previous) === JSON.stringify(next)) return;
    // A presentation change in the resize hook can synchronously supersede this measurement.
    this.viewport = next;
    this.hooks.resized(next);
    if (this.disposed || this.viewport !== next) return;
    this.canvas.width = next.widthPx;
    this.canvas.height = next.heightPx;
    if (this.handshake) {
      this.send('SetViewport', next);
      this.sentViewport = next.resizeRevision;
    }
    this.schedule();
  }

  private watchDpr(): void {
    if (typeof window.matchMedia !== 'function') return;
    this.media?.removeEventListener('change', this.dprChanged);
    this.media = window.matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
    this.media.addEventListener('change', this.dprChanged, { once: true });
  }
  private readonly dprChanged = (): void => { this.measure(); this.watchDpr(); };
  private readonly visibility = (): void => {
    this.suspended = document.hidden;
    this.send('SetPlayback', {
      contractVersion: CONTRACT_VERSION, instanceId: this.instanceId, type: 'playback', suspended: this.suspended,
    });
    if (!this.suspended) { this.measure(true); this.hooks.resumed(); this.schedule(); }
  };
  private readonly csp = (event: SecurityPolicyViolationEvent): void => {
    if (event.blockedURI === 'wasm-eval' || event.blockedURI.startsWith(`${window.location.origin}/autotable/unity/`)) {
      this.unavailable('CSP blocked the Unity runtime. This browser/policy is incompatible; no security headers or renderer were substituted.');
    }
  };

  unavailable(message: string): void { this.fail('failed', message); }
  private fail(state: HostState, message: string): void {
    if (this.disposed || ['unavailable', 'unsupported', 'failed'].includes(this.state)) return;
    this.status(state, state === 'unsupported' ? 'Unity unsupported' : state === 'unavailable' ? 'Unity build unavailable' : 'Unity renderer unavailable', message);
    this.removeMarkers();
    this.queue = null;
    if (this.timer) clearTimeout(this.timer);
    if (this.startupTimer) clearTimeout(this.startupTimer);
    this.timer = this.startupTimer = null;
    this.hooks.failure();
    this.requests.abort();
    this.canvas.hidden = true;
    const instance = this.instance;
    this.instance = null;
    this.handshake = false;
    if (instance) void instance.Quit().catch(() => { this.hooks.notice('Unity runtime teardown failed; reload the table.'); });
  }

  async dispose(): Promise<void> {
    if (this.disposed) return;
    if (this.sessionId) this.clear(this.sessionId, 'dispose');
    this.disposed = true;
    this.hooks.failure();
    this.events.abort();
    this.requests.abort();
    this.observer?.disconnect();
    this.media?.removeEventListener('change', this.dprChanged);
    if (this.timer) clearTimeout(this.timer);
    if (this.startupTimer) clearTimeout(this.startupTimer);
    this.timer = this.startupTimer = null;
    this.queue = null;
    this.sentFrames.clear();
    this.removeMarkers();
    const win = window as BridgeWindow;
    if (win.MahjongUnityBridge === this.bridge) {
      if (this.previousBridge) win.MahjongUnityBridge = this.previousBridge;
      else delete win.MahjongUnityBridge;
    }
    this.script?.remove();
    const instance = this.instance;
    this.instance = null;
    if (instance) await instance.Quit();
    this.state = 'disposed';
    this.canvas.remove();
    this.panel.remove();
    this.insets.remove();
  }
}

import { World } from './world';
import { Client } from './client';
import { onlineModeForVariant } from './base-client';
import { Animation } from './utils';
import { ClientUi } from './client-ui';
import { SoundPlayer } from './sound-player';
import type { GameUi } from './game-ui';
import { getSettings, installPerspectiveSetting, installRendererLayoutSettings, onSettingsChange, type AppSettings } from './settings-drawer';
import { BufferedRendererSink } from './renderer/renderer-sink';
import { RendererFrameAdapter } from './renderer/renderer-frame';
import { RendererActionRouter } from './renderer/renderer-actions';
import { UnityHost } from './renderer/unity-host';
import type { Presentation } from './renderer/renderer-contract';
import { GameType } from './types';

export class Game {
  readonly client: Client;
  readonly world: World;
  readonly renderer: UnityHost;
  readonly sink = new BufferedRendererSink();
  private readonly clientUi: ClientUi;
  private readonly soundPlayer: SoundPlayer;
  private gameUi: GameUi | null = null;
  private adapter: RendererFrameAdapter | null = null;
  private actions: RendererActionRouter | null = null;
  private readonly events = new AbortController();
  private unsubscribeSettings: (() => void) | null = null;
  private frameHandle = 0;
  private lastUpdate = -Infinity;
  private started = false;
  private disposed = false;
  private width = window.innerWidth;

  benchmark = false;
  settings: {
    perspective: HTMLInputElement; tileLabels: HTMLInputElement; benchmark: HTMLInputElement;
    muted: HTMLInputElement; sticky: HTMLInputElement;
  };
  private lookDown = new Animation(150);
  private zoom = new Animation(150);
  private lookDownState = 0;
  keys: Set<string> = new Set();

  constructor() {
    this.lookDown.update();
    this.zoom.update();
    const query = new URLSearchParams(window.location.search);
    const online = (query.get('gameId') ?? '').trim() !== '' || query.has('rejoin');
    this.client = new Client(online ? onlineModeForVariant(query.get('variant')) : 'offline');
    this.soundPlayer = new SoundPlayer(this.client);
    this.world = new World(this.sink, this.soundPlayer, this.client);
    this.clientUi = new ClientUi(this.client);
    this.settings = {
      perspective: document.getElementById('perspective') as HTMLInputElement,
      tileLabels: document.getElementById('tile-labels') as HTMLInputElement,
      benchmark: document.getElementById('benchmark') as HTMLInputElement,
      muted: document.getElementById('muted') as HTMLInputElement,
      sticky: document.getElementById('sticky') as HTMLInputElement,
    };
    this.renderer = new UnityHost(document.getElementById('main')!, {
      action: action => this.actions?.handle(action),
      resized: viewport => {
        this.actions?.cancelGesture();
        this.width = viewport.widthCssPx;
        if (this.adapter) this.updateSettings();
      },
      resumed: () => this.adapter?.refresh(),
      failure: () => { this.actions?.cancelGesture(); this.adapter?.fail('Unity renderer unavailable; game commands remain browser-owned.'); },
      notice: message => { document.getElementById('unity-notice')!.textContent = message; },
    }, { allowUnqualifiedBuild: query.get('unityLocalBuild') === '1' });
    this.adapter = new RendererFrameAdapter(this.renderer.instanceId, this.client, this.world, this.sink, {
      clear: (sessionId, reason) => this.renderer.clear(sessionId, reason),
      publish: (layout, frame) => this.renderer.publish(layout, frame),
      unavailable: message => this.renderer.unavailable(message),
    });
    this.actions = new RendererActionRouter(this.adapter, {
      result: result => this.renderer.actionResult(result),
      notice: message => { document.getElementById('unity-notice')!.textContent = message; },
      unavailable: message => this.adapter?.fail(message),
    });
    this.setupEvents();
  }

  installGameUi(ctor: new (c: Client, w: World) => GameUi): GameUi {
    if (this.gameUi === null) this.gameUi = new ctor(this.client, this.world);
    return this.gameUi;
  }

  private setupEvents(): void {
    window.addEventListener('keydown', event => this.onKeyDown(event), { signal: this.events.signal });
    window.addEventListener('keyup', event => this.onKeyUp(event), { signal: this.events.signal });
    window.addEventListener('pagehide', () => { void this.dispose(); }, { signal: this.events.signal });
    for (const input of Object.values(this.settings)) input?.addEventListener('change', () => this.updateSettings(), { signal: this.events.signal });
    installPerspectiveSetting();
    installRendererLayoutSettings();
    this.unsubscribeSettings = onSettingsChange(() => this.updateSettings());
    window.addEventListener('mahjong:highlight-tile', event => {
      const detail: unknown = (event as CustomEvent).detail;
      const tileId = detail && typeof detail === 'object' ? (detail as { tileId?: unknown }).tileId : null;
      this.world.setHighlightedThing(typeof tileId === 'string' ? this.world.findThingByFace(tileId) : null);
    }, { signal: this.events.signal });
  }

  private presentation(settings: AppSettings): Presentation {
    const compact = this.width < 768;
    return {
      viewMode: this.settings.perspective.checked ? 'perspective' : 'flat',
      boardLayout: settings.boardLayout === 'auto' ? compact ? 'rectangular' : 'classic' : settings.boardLayout,
      handLayout: settings.handLayout === 'auto' ? compact ? 'separate' : 'in-table' : settings.handLayout,
      tileLabels: this.settings.tileLabels.checked, lookDown: this.lookDown.pos, zoom: this.zoom.pos,
    };
  }

  private updateSettings(): void {
    const settings = getSettings();
    this.soundPlayer.muted = this.settings.muted.checked || !settings.soundEnabled;
    const presentation = this.presentation(settings);
    this.world.setHandSortMode(settings.handSort);
    this.adapter?.setPresentation(presentation);
    this.renderer?.setPresentation(presentation);
    this.adapter?.refresh();
  }

  start(): void {
    if (this.started || this.disposed) return;
    this.started = true;
    this.clientUi.start();
    this.updateSettings();
    void this.renderer.mount();
    this.frameHandle = requestAnimationFrame(time => this.mainLoop(time));
  }

  mainLoop(time: number): void {
    if (this.disposed) return;
    this.frameHandle = requestAnimationFrame(next => this.mainLoop(next));
    if (time - this.lastUpdate < 1000 / 30) return;
    this.lastUpdate = time;
    this.lookDown.update();
    this.zoom.update();
    this.adapter?.setPresentation(this.presentation(getSettings()));
    this.adapter?.refresh();
  }

  private onKeyDown(event: KeyboardEvent): void {
    const active = document.activeElement as HTMLElement | null;
    if (active && (['INPUT', 'TEXTAREA', 'SELECT'].includes(active.tagName) || active.isContentEditable
      || (event.key === ' ' && ['BUTTON', 'A'].includes(active.tagName)))) return;
    if (this.keys.has(event.key)) return;
    this.keys.add(event.key);
    switch (event.key) {
      case 'f': case 'F': case 'r': case 'R':
        if (this.renderer.state === 'ready' && this.adapter?.actionable()
          && this.world.conditions.gameType !== GameType.CHANGSHA) {
          this.world.onFlip(event.key.toLowerCase() === 'f' ? 1 : -1, event.key === event.key.toUpperCase());
        }
        break;
      case ' ': this.lookDown.start(1); break;
      case 'z': this.zoom.start(1); break;
      case 'x': this.zoom.start(-1); break;
      case 'q':
        this.lookDownState = 1 - this.lookDownState;
        this.lookDown.start(this.lookDownState);
        break;
      case 'p':
        this.settings.perspective.checked = !this.settings.perspective.checked;
        this.settings.perspective.dispatchEvent(new Event('change', { bubbles: true }));
        break;
      case 'l':
        this.settings.tileLabels.checked = !this.settings.tileLabels.checked;
        this.updateSettings();
        break;
    }
  }

  private onKeyUp(event: KeyboardEvent): void {
    this.keys.delete(event.key);
    if ([' ', 'z', 'x'].includes(event.key)) {
      if (event.key === ' ') this.lookDown.start(0);
      else this.zoom.start(0);
    }
  }

  async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    cancelAnimationFrame(this.frameHandle);
    this.events.abort();
    this.unsubscribeSettings?.();
    this.actions?.cancelGesture();
    this.adapter?.dispose();
    this.client.disconnect();
    await this.renderer.dispose();
  }
}

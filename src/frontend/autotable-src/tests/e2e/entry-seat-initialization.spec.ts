import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import * as ts from 'typescript';
import { GameType, type PickupEntry, type TurnEntry } from '../../src/types';
import { computeTurnCue, type TurnCueInput } from '../../src/turn-cue';
import { hideEl, setElHidden, showEl } from '../../src/dom-utils';
import { randomUUID } from 'node:crypto';
import { test, expect, type Page, type TestInfo } from '@playwright/test';

interface EntryGame {
  clientUi: unknown;
  gameUi: { connectionLost: boolean } | null;
  client: {
    connected(): boolean;
    seat: number | null;
    seatPlayers: Array<string | null>;
  };
  world: {
    seat: number | null;
    things: Map<number, {
      slot?: { group: string; seat: number | null; thing: unknown } | null;
      rotationIndex: number;
    }>;
  };
}

interface EntryState {
  game: boolean;
  clientUi: boolean;
  gameUi: boolean;
  connected: boolean;
  seat: number | null;
  seatPlayers: Array<string | null>;
  ownHand: number;
  faceUpHands: number;
}

interface InitialChrome {
  connected: boolean;
  seat: number | null;
  connectionLost: boolean | null;
  seatRowVisible: boolean;
  leaveHidden: boolean;
  leaveDisabled: boolean;
  dealDisabled: boolean;
  setupDisabled: boolean;
  turnVisible: boolean;
  spectating: boolean;
}

interface ModuleGate {
  requested(): boolean;
  release(): void;
}

function entryUrl(baseURL: string | undefined, params: Record<string, string>): string {
  const url = new URL(baseURL ?? 'http://localhost:8080/autotable/');
  url.search = new URLSearchParams(params).toString();
  return url.href;
}

async function prepare(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('mahjong.tour.completed.v1', 'true');
    localStorage.setItem('mahjong.identity.onboarded.v1', 'true');
  });
}

  class CueClassList {
    private readonly tokens = new Set<string>();

    add(...tokens: string[]): void { for (const token of tokens) this.tokens.add(token); }
    remove(...tokens: string[]): void { for (const token of tokens) this.tokens.delete(token); }
    contains(token: string): boolean { return this.tokens.has(token); }
    toggle(token: string, force: boolean): boolean {
      if (force) this.tokens.add(token);
      else this.tokens.delete(token);
      return force;
    }
  }

  class CueElement {
    hidden = true;
    disabled = false;
    className = '';
    title = '';
    offsetHeight = 1;
    readonly classList = new CueClassList();
    readonly style: Record<string, string> = {};
    readonly dataset: Record<string, string> = {};
    readonly children: CueElement[] = [];
    private content = '';

    get textContent(): string { return this.content + this.children.map((child) => child.textContent).join(''); }
    set textContent(value: string) { this.content = value; this.children.length = 0; }
    appendChild(child: CueElement): void { this.children.push(child); }
    setAttribute(_name: string, _value: string): void {}
  }

  class CueCollection<T> {
    private readonly updates: Array<() => void> = [];

    constructor(public value: T | null = null) {}
    get(_key: unknown): T | null { return this.value; }
    on(_event: string, handler: () => void): void { this.updates.push(handler); }
    update(value: T | null): void {
      this.value = value;
      for (const handler of this.updates) handler();
    }
  }

  interface CueInstance {
    connectionLost: boolean;
    elements: {
      turnBanner: CueElement;
      leaveSeat: CueElement;
      rollDice: CueElement;
      pickupHud: CueElement;
      pickupTakeCount: CueElement;
    };
    refreshTurnBanner(): void;
  }

  interface CueOptions {
    variant: GameType;
    connected: boolean;
    search?: string;
    lastGameId?: string | null;
    seat?: number | null;
    input?: Partial<TurnCueInput>;
    turn?: TurnEntry;
    pickup?: PickupEntry;
  }

  interface CueHarness {
    ui: CueInstance;
    body: CueElement;
    element(selector: string): CueElement;
    commands: string[];
    input: TurnCueInput;
    pickup: CueCollection<PickupEntry>;
    turn: CueCollection<TurnEntry>;
    setConnection(connected: boolean): void;
  }

  // Execute the actual constructor, subscriptions, HUD, seat and banner methods.
  // Only unrelated setup/render infrastructure is stubbed; no browser is launched.
  function sourceCueHarness(options: CueOptions): CueHarness {
    const filename = resolve(__dirname, '../../src/game-ui.ts');
    const source = ts.createSourceFile(filename, readFileSync(filename, 'utf8'), ts.ScriptTarget.Latest, true);
    const original = source.statements.find((node): node is ts.ClassDeclaration =>
      ts.isClassDeclaration(node) && node.name?.text === 'GameUi',
    );
    if (!original) throw new Error('Production GameUi class was not found');
    const kept = new Set([
      'setupPickupHud', 'onPickupUpdate', 'renderPickupHud', 'renderRollDiceButton', 'renderBreakMarker',
      'setupTurnBanner', 'updateSeats', 'refreshTurnBanner', 'applyTurnBannerState',
      'onConnectionLost', 'onConnectionRestored',
    ]);
    const declaration = ts.factory.updateClassDeclaration(
      original, undefined, original.name, original.typeParameters, original.heritageClauses,
      original.members.filter((member) => ts.isConstructorDeclaration(member) || ts.isPropertyDeclaration(member)
        || (ts.isMethodDeclaration(member) && kept.has(member.name.getText(source)))),
    );
    const javascript = ts.transpileModule(
      ts.createPrinter().printNode(ts.EmitHint.Unspecified, declaration, source),
      { compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS } },
    ).outputText;
    const elements = new Map<string, CueElement>();
    const element = (selector: string): CueElement => {
      let node = elements.get(selector);
      if (!node) { node = new CueElement(); elements.set(selector, node); }
      return node;
    };
    const body = new CueElement();
    const search = options.search ?? `?variant=${options.variant.toLowerCase()}&seat=0`;
    const constructor: unknown = runInNewContext(`${javascript}\nGameUi;`, {
      GameType, computeTurnCue, hideEl, showEl, setElHidden,
      readSpectatorFromUrl: (): boolean => false,
      // Settings installation is unrelated infrastructure; cue and transport
      // methods above still execute directly from the production class.
      installPerspectiveSetting: (): void => {},
      Replay: class {},
      URLSearchParams,
      window: { location: { search } },
      document: {
        body,
        getElementById: element,
        querySelector: element,
        querySelectorAll: (): CueElement[] => [],
        createElement: (): CueElement => new CueElement(),
      },
    });
    if (typeof constructor !== 'function') throw new Error('Production GameUi did not compile to a constructor');
    const GameUi = constructor as new (client: object, world: object) => CueInstance;
    for (const member of original.members) {
      if (!ts.isMethodDeclaration(member) || kept.has(member.name.getText(source))) continue;
      Object.defineProperty(GameUi.prototype, member.name.getText(source), {
        configurable: true, value: (): void => {},
      });
    }
    Object.defineProperty(GameUi.prototype, 'resolvePhaseFParams', {
      value: (): { variant: GameType } => ({ variant: options.variant }),
    });
    let connected = options.connected;
    const seat = options.seat === undefined ? 0 : options.seat;
    const callbacks = new Map<string, Array<() => void>>();
    const pickup = new CueCollection<PickupEntry>(options.pickup ?? null);
    const turn = new CueCollection<TurnEntry>(options.turn ?? null);
    const client = {
      connected: (): boolean => connected,
      seat,
      lastGameId: options.lastGameId ?? null,
      seatPlayers: new Array<string | null>(4).fill(null),
      pickup,
      turn,
      match: new CueCollection({ dealer: 0 }),
      things: new CueCollection(),
      seats: new CueCollection(),
      nicks: new CueCollection<string>(),
      on(event: string, handler: () => void): void {
        const handlers = callbacks.get(event) ?? [];
        handlers.push(handler);
        callbacks.set(event, handlers);
      },
    };
    const input: TurnCueInput = {
      mySeat: seat, isSpectatorUrl: false, inProgress: true,
      activeSeatSignal: undefined, awaitingDiscardSignal: undefined,
      myHasExtraTile: true, activeSeatByGeometry: 0, allSeatsOccupied: false,
      ...options.input,
    };
    const commands: string[] = [];
    const ui = new GameUi(client, {
      getTurnCueInput: (): TurnCueInput => input,
      emitRollDice: (): void => { commands.push('roll'); },
      emitTakePickup: (): void => { commands.push('take'); },
    });
    test.info().annotations.push({
      type: 'actual-source-constructor',
      description: JSON.stringify({
        variant: options.variant, connected, search, lastGameId: client.lastGameId,
        connectionLost: ui.connectionLost, bannerHidden: ui.elements.turnBanner.hidden,
        banner: ui.elements.turnBanner.textContent, discardCursor: body.classList.contains('my-turn-discard'),
        commands,
      }),
    });
    return {
      ui, body, element, commands, input, pickup, turn,
      setConnection(value: boolean): void {
        connected = value;
        for (const callback of callbacks.get(value ? 'connect' : 'disconnect') ?? []) callback();
      },
    };
  }

  function expectDiscardCue(harness: CueHarness, expected: boolean): void {
    expect(harness.ui.elements.turnBanner.hidden).toBe(!expected);
    expect(harness.ui.elements.turnBanner.classList.contains('turn-banner-discard')).toBe(expected);
    expect(harness.body.classList.contains('my-turn-discard')).toBe(expected);
    expect(harness.ui.elements.turnBanner.textContent).toBe(expected ? 'Your turn — click a tile to discard' : '');
    expect(harness.commands).toEqual([]);
  }

  test.describe('Ferro R2 source-only — offline relay versus disconnected session', () => {
    for (const variant of [GameType.FOUR_PLAYER, GameType.THREE_PLAYER, GameType.BAMBOO, GameType.MINEFIELD]) {
      test(`deliberately offline ${variant} retains the extra-tile discard banner and cursor`, () => {
        const harness = sourceCueHarness({ variant, connected: false });
        expect(harness.ui.connectionLost).toBe(false);
        expectDiscardCue(harness, true);
        expect(harness.ui.elements.leaveSeat.hidden).toBe(false);
      });
    }

    test('an empty gameId still denotes a requested connection, matching ClientUi.start', () => {
      const harness = sourceCueHarness({
        variant: GameType.FOUR_PLAYER, connected: false, search: '?variant=four_player&seat=0&gameId=',
      });
      expect(harness.ui.connectionLost).toBe(true);
      expectDiscardCue(harness, false);
    });

    test('connected relay keeps its geometry-driven discard cue', () => {
      const harness = sourceCueHarness({
        variant: GameType.FOUR_PLAYER, connected: true, search: '?variant=four_player&gameId=relay-room',
        lastGameId: 'relay-room',
      });
      expect(harness.ui.connectionLost).toBe(false);
      expectDiscardCue(harness, true);
    });

    test('relay awaiting a requested server session does not advertise stale geometry', () => {
      const harness = sourceCueHarness({
        variant: GameType.FOUR_PLAYER, connected: false, search: '?variant=four_player&gameId=relay-room',
      });
      expect(harness.ui.connectionLost).toBe(true);
      expectDiscardCue(harness, false);
    });

    test('a remembered disconnected relay session stays retracted even without a URL gameId', () => {
      const harness = sourceCueHarness({ variant: GameType.FOUR_PLAYER, connected: false, lastGameId: 'prior-room' });
      expect(harness.ui.connectionLost).toBe(true);
      expectDiscardCue(harness, false);
    });

    test('offline Changsha does not advertise its unconfirmed default seat or discard cue', () => {
      const harness = sourceCueHarness({ variant: GameType.CHANGSHA, connected: false });
      expect(harness.ui.connectionLost).toBe(true);
      expectDiscardCue(harness, false);
      expect(harness.ui.elements.leaveSeat.hidden).toBe(true);
      expect(harness.ui.elements.leaveSeat.disabled).toBe(true);
      expect(harness.element('.seat-buttons').style.display).toBe('none');
    });

    test('connected Changsha still follows the authoritative discard signal', () => {
      const harness = sourceCueHarness({
        variant: GameType.CHANGSHA, connected: true,
        input: { activeSeatSignal: 0, awaitingDiscardSignal: true, myHasExtraTile: false },
      });
      expectDiscardCue(harness, true);
    });

    test('actual registered disconnect/connect handlers retract and restore a relay cue', () => {
      const harness = sourceCueHarness({ variant: GameType.FOUR_PLAYER, connected: true, lastGameId: 'relay-room' });
      expectDiscardCue(harness, true);
      harness.setConnection(false);
      expect(harness.ui.connectionLost).toBe(true);
      expectDiscardCue(harness, false);
      harness.setConnection(true);
      expect(harness.ui.connectionLost).toBe(false);
      expectDiscardCue(harness, true);
    });

    test('connected late empty-seat chrome is still initialized without another update', () => {
      const harness = sourceCueHarness({
        variant: GameType.FOUR_PLAYER, connected: true, seat: null,
        input: { inProgress: false, activeSeatByGeometry: null, myHasExtraTile: false },
      });
      expect(harness.element('.seat-buttons').style.display).toBe('block');
      expect(harness.ui.elements.leaveSeat.hidden).toBe(true);
      expect(harness.ui.elements.leaveSeat.disabled).toBe(true);
      expectDiscardCue(harness, false);
    });

    test('cached Roll/pickup hydration and pickup tombstones remain action-free and idempotent', () => {
      const roll = sourceCueHarness({
        variant: GameType.CHANGSHA, connected: true,
        turn: { phase: 'RollingDice', activeSeat: null, awaitingDiscard: false },
        input: { inProgress: false, myHasExtraTile: false, activeSeatByGeometry: null },
      });
      expect(roll.ui.elements.rollDice.hidden).toBe(false);
      expect(roll.commands).toEqual([]);
      const pickup = sourceCueHarness({
        variant: GameType.CHANGSHA, connected: true,
        turn: { phase: 'PickupRound1', activeSeat: null, awaitingDiscard: false },
        pickup: { phase: 'PickupRound1', seatIndex: 0, count: 4, dealMode: 'manual' },
        input: { inProgress: false, myHasExtraTile: false, activeSeatSignal: null, awaitingDiscardSignal: false },
      });
      expect(pickup.ui.elements.pickupHud.hidden).toBe(false);
      expect(pickup.ui.elements.pickupTakeCount.textContent).toBe('4');
      expect(pickup.ui.elements.rollDice.hidden).toBe(true);
      expect(pickup.ui.elements.turnBanner.children).toHaveLength(1);
      expect(pickup.commands).toEqual([]);
      pickup.input.inProgress = true;
      pickup.input.myHasExtraTile = true;
      pickup.input.activeSeatSignal = 0;
      pickup.input.awaitingDiscardSignal = true;
      pickup.turn.update({ phase: 'AwaitingDiscard', activeSeat: 0, awaitingDiscard: true });
      pickup.pickup.update(null);
      expect(pickup.ui.elements.pickupHud.style.display).toBe('none');
      expect(pickup.ui.elements.rollDice.hidden).toBe(true);
      expectDiscardCue(pickup, true);
      expect(pickup.ui.elements.turnBanner.children).toHaveLength(1);
    });
  });

async function readState(page: Page): Promise<EntryState> {
  return page.evaluate(() => {
    const game = (window as unknown as { game?: EntryGame }).game;
    const hands = Array.from(game?.world.things.values() ?? []).filter((thing) =>
      thing.slot?.group === 'hand' && thing.slot.thing === thing,
    );
    return {
      game: !!game,
      clientUi: !!game?.clientUi,
      gameUi: !!game?.gameUi,
      connected: !!game?.client.connected(),
      seat: game?.client.seat ?? null,
      seatPlayers: game?.client.seatPlayers ?? [],
      ownHand: typeof game?.world.seat === 'number'
        ? hands.filter((thing) => thing.slot?.seat === game.world.seat).length : 0,
      faceUpHands: hands.filter((thing) => thing.rotationIndex === 1).length,
    };
  });
}

// Only delivery is delayed: never replace JavaScript, mutate game state, or
// dispatch a synthetic UI event. Each gate releases the original module.
async function delayModule(page: Page, module: 'client-ui' | 'scene-effects' | 'action-router'): Promise<ModuleGate> {
  let requested = false;
  let release!: () => void;
  const released = new Promise<void>((resolve) => { release = resolve; });
  await page.route(`**/${module}.*.js`, async (route) => {
    requested = true;
    await released;
    await route.continue();
  });
  return { requested: () => requested, release };
}

async function activate(page: Page, selector: string, testInfo: TestInfo): Promise<void> {
  const control = page.locator(selector);
  await expect(control).toBeVisible();
  await expect(control).toBeEnabled();
  if (testInfo.project.name === 'mobile-chrome') await control.tap();
  else await control.click();
}

function observeNavigation(page: Page): {
  documents: string[];
  sockets: Array<{ url: URL; closed: boolean }>;
  errors: string[];
} {
  const documents: string[] = [];
  const sockets: Array<{ url: URL; closed: boolean }> = [];
  const errors: string[] = [];
  page.on('request', (request) => {
    if (request.isNavigationRequest() && request.frame() === page.mainFrame()) {
      documents.push(request.url());
    }
  });
  page.on('websocket', (ws) => {
    if (!new URL(ws.url()).pathname.endsWith('/autotable/ws')) return;
    const socket = { url: new URL(ws.url()), closed: false };
    sockets.push(socket);
    ws.on('close', () => { socket.closed = true; });
  });
  page.on('pageerror', (error) => errors.push(error.message));
  return { documents, sockets, errors };
}

async function waitForSeat(page: Page, seat: number): Promise<void> {
  await expect.poll(async () => {
    const state = await readState(page);
    return state.connected && state.seat === seat;
  }, { timeout: 30_000 }).toBe(true);
}

function expectConfig(url: URL, expected: Record<string, string>): void {
  for (const [key, value] of Object.entries(expected)) {
    expect(url.searchParams.get(key), `${key} must survive entry on ${url.pathname}`).toBe(value);
  }
}

// Capture chrome synchronously when the real effects installer publishes
// readiness. A later seats/nicks event cannot conceal a missed initial render.
async function observeInitialChrome(page: Page): Promise<void> {
  await page.addInitScript(() => {
    window.addEventListener('mahjong:scene-effects-ready', () => {
      const observed = window as unknown as { game?: EntryGame; entrySeatChrome?: InitialChrome };
      const visible = (selector: string): boolean => {
        const el = document.querySelector<HTMLElement>(selector)!;
        const style = getComputedStyle(el);
        const box = el.getBoundingClientRect();
        return !el.hidden && style.display !== 'none' && style.visibility !== 'hidden'
          && box.width > 0 && box.height > 0;
      };
      const leave = document.getElementById('leave-seat') as HTMLButtonElement;
      observed.entrySeatChrome = {
        connected: !!observed.game?.client.connected(),
        seat: observed.game?.client.seat ?? null,
        connectionLost: observed.game?.gameUi?.connectionLost ?? null,
        seatRowVisible: visible('.seat-buttons'),
        leaveHidden: leave.hidden,
        leaveDisabled: leave.disabled,
        dealDisabled: (document.getElementById('deal') as HTMLButtonElement).disabled,
        setupDisabled: (document.getElementById('toggle-setup') as HTMLButtonElement).disabled,
        turnVisible: visible('#turn-banner'),
        spectating: document.body.classList.contains('spectating'),
      };
    }, { once: true });
  });
}

async function releaseEffects(page: Page, gate: ModuleGate, testInfo: TestInfo): Promise<InitialChrome> {
  await expect.poll(gate.requested).toBe(true);
  const before = await readState(page);
  expect(before.gameUi, 'The cached state must precede GameUi construction').toBe(false);
  await testInfo.attach('before-effects', { body: JSON.stringify(before), contentType: 'application/json' });
  gate.release();
  await expect.poll(() => page.evaluate(() =>
    !!(window as unknown as { entrySeatChrome?: InitialChrome }).entrySeatChrome,
  ), { timeout: 15_000 }).toBe(true);
  const chrome = await page.evaluate(() =>
    (window as unknown as { entrySeatChrome: InitialChrome }).entrySeatChrome,
  );
  await testInfo.attach('initial-seat-chrome', { body: JSON.stringify(chrome), contentType: 'application/json' });
  return chrome;
}

test.describe('Entry and seat initialization — original lazy modules, real gestures', () => {
  test.use({ serviceWorkers: 'block' });
  test.setTimeout(60_000);

  const coldEntries: Array<{
    name: string;
    module: 'client-ui' | 'action-router';
    params: Record<string, string>;
    expected: Record<string, string>;
  }> = [
    {
      name: 'bare seat-0 defaults',
      module: 'client-ui',
      params: { seat: '0' },
      expected: { seat: '0', variant: 'changsha', dealMode: 'auto', botCount: '3', botDifficulty: 'Hard', handCount: '4' },
    },
    {
      name: 'explicit manual configuration',
      module: 'client-ui',
      params: { seat: '2', variant: 'changsha', dealMode: 'manual', botCount: '0', handCount: '8', seed: '94209' },
      expected: { seat: '2', variant: 'changsha', dealMode: 'manual', botCount: '0', handCount: '8', seed: '94209' },
    },
    {
      name: 'pending PWA action',
      module: 'action-router',
      params: { action: 'new-game', seat: '0' },
      expected: { seat: '0', variant: 'changsha', dealMode: 'auto', botCount: '3', botDifficulty: 'Hard', handCount: '4' },
    },
  ];

  for (const entry of coldEntries) {
    test(`first New Game activation works while original ${entry.module} is held — ${entry.name}`, async ({ page, baseURL }, testInfo) => {
      await prepare(page);
      const observed = observeNavigation(page);
      const gate = await delayModule(page, entry.module);
      try {
        await page.goto(entryUrl(baseURL, entry.params), { waitUntil: 'domcontentloaded' });
        await expect.poll(gate.requested).toBe(true);
        const before = await readState(page);
        expect(before.game).toBe(false);
        expect(before.clientUi).toBe(false);
        expect(observed.sockets).toHaveLength(0);
        const initialDocuments = observed.documents.length;
        await testInfo.attach('before-first-activation', { body: JSON.stringify(before), contentType: 'application/json' });

        await Promise.all([
          page.waitForURL((url) => /^changsha-[0-9a-f]{8}$/.test(url.searchParams.get('gameId') ?? ''), {
            waitUntil: 'domcontentloaded', timeout: 20_000,
          }),
          activate(page, '[data-testid="new-game-button"]', testInfo),
        ]);
        // Navigation must precede release: waiting for lazy UI and clicking
        // again is expressly not a cold-entry fix.
        const fresh = new URL(page.url());
        expectConfig(fresh, entry.expected);
        expect(fresh.searchParams.has('action')).toBe(false);
        expect(fresh.searchParams.has('rejoin')).toBe(false);
        expect(observed.documents.slice(initialDocuments)).toHaveLength(1);
        gate.release();
        await waitForSeat(page, Number(entry.expected.seat));
        expect(observed.documents.slice(initialDocuments)).toHaveLength(1);
        expect(observed.sockets).toHaveLength(1);
        expect(observed.sockets[0].url.searchParams.get('gameId')).toBe(fresh.searchParams.get('gameId'));
        expectConfig(observed.sockets[0].url, entry.expected);
        expect(observed.errors).toEqual([]);
        await testInfo.attach('new-game-navigation', {
          body: JSON.stringify({ documents: observed.documents, handshake: observed.sockets[0].url.href }),
          contentType: 'application/json',
        });
      } finally {
        gate.release();
      }
    });
  }

  test('ready ClientUi replaces the eager action and hands off a genuinely acquired seat exactly once', async ({ page, baseURL }, testInfo) => {
    await prepare(page);
    const observed = observeNavigation(page);
    const config = { variant: 'changsha', dealMode: 'manual', botCount: '0', handCount: '8', seed: '94209' };
    const oldId = `entry-owner-${randomUUID()}`;
    await page.goto(entryUrl(baseURL, { ...config, gameId: oldId }), { waitUntil: 'domcontentloaded' });
    await expect.poll(async () => {
      const state = await readState(page);
      return state.connected && state.gameUi && state.seat === null;
    }, { timeout: 30_000 }).toBe(true);
    await activate(page, '.seat-button-2 .take-seat', testInfo);
    await waitForSeat(page, 2);
    expect(new URL(page.url()).searchParams.get('seat'), 'The chair came from a real Take seat, not a URL hint').toBeNull();
    expect(observed.sockets).toHaveLength(1);
    const initialDocuments = observed.documents.length;

    await Promise.all([
      page.waitForURL((url) => url.searchParams.get('gameId') !== oldId, {
        waitUntil: 'domcontentloaded', timeout: 20_000,
      }),
      activate(page, '[data-testid="new-game-button"]', testInfo),
    ]);
    await waitForSeat(page, 2);
    const fresh = new URL(page.url());
    expect(fresh.searchParams.get('gameId')).toMatch(/^changsha-[0-9a-f]{8}$/);
    expectConfig(fresh, { ...config, seat: '2' });
    expect(observed.documents.slice(initialDocuments)).toHaveLength(1);
    expect(observed.sockets).toHaveLength(2);
    expect(observed.sockets[1].url.searchParams.get('gameId')).toBe(fresh.searchParams.get('gameId'));
    expectConfig(observed.sockets[1].url, { ...config, seat: '2' });
    await expect.poll(() => observed.sockets[0].closed).toBe(true);
    expect(observed.errors).toEqual([]);
  });

  test('the lobby-only PWA New Game entry uses the same eager action', async ({ page, baseURL }) => {
    await prepare(page);
    const observed = observeNavigation(page);
    await page.goto(entryUrl(baseURL, { action: 'new-game', seat: '0' }), { waitUntil: 'commit' });
    await page.waitForURL((url) => /^changsha-[0-9a-f]{8}$/.test(url.searchParams.get('gameId') ?? ''), {
      waitUntil: 'domcontentloaded', timeout: 20_000,
    });
    await waitForSeat(page, 0);
    expect(observed.documents).toHaveLength(2);
    expect(observed.sockets).toHaveLength(1);
    expect(new URL(page.url()).searchParams.has('action')).toBe(false);
    expectConfig(observed.sockets[0].url, coldEntries[0].expected);
    expect(observed.errors).toEqual([]);
  });

  test('late relay GameUi hydrates an already-connected empty seat map without another update', async ({ page, baseURL }, testInfo) => {
    await prepare(page);
    await observeInitialChrome(page);
    const gate = await delayModule(page, 'scene-effects');
    try {
      await page.goto(entryUrl(baseURL, { variant: 'four_player', gameId: `entry-empty-${randomUUID()}`, botCount: '0' }), {
        waitUntil: 'domcontentloaded',
      });
      await expect.poll(async () => {
        const state = await readState(page);
        return state.connected && state.seat === null && state.seatPlayers.length === 4
          && state.seatPlayers.every((player) => player === null);
      }, { timeout: 30_000 }).toBe(true);
      const chrome = await releaseEffects(page, gate, testInfo);
      expect(chrome).toMatchObject({
        connected: true, seat: null, connectionLost: false, seatRowVisible: true,
        leaveHidden: true, leaveDisabled: true, dealDisabled: true, setupDisabled: true, spectating: false,
      });
      for (let seat = 0; seat < 4; seat++) {
        await expect(page.locator(`.seat-button-${seat} .take-seat`)).toBeVisible();
        await expect(page.locator(`.seat-button-${seat} .take-seat`)).toBeEnabled();
      }
    } finally {
      gate.release();
    }
  });

  for (const spectator of [false, true]) {
    test(`late Changsha seat chrome respects ${spectator ? 'spectator mode' : 'an occupied URL seat'} without projecting a private hand`, async ({ page, browser, baseURL }, testInfo) => {
      const ownerContext = await browser.newContext({ serviceWorkers: 'block' });
      const owner = await ownerContext.newPage();
      const config = {
        variant: 'changsha', gameId: `entry-occupied-${randomUUID()}`, dealMode: 'auto',
        botCount: '3', botDifficulty: 'Medium', handCount: '4', seed: '94209',
      };
      await prepare(page);
      await observeInitialChrome(page);
      const gate = await delayModule(page, 'scene-effects');
      try {
        await prepare(owner);
        await owner.goto(entryUrl(baseURL, { ...config, seat: '0' }), { waitUntil: 'domcontentloaded' });
        await waitForSeat(owner, 0);
        await expect.poll(async () => (await readState(owner)).ownHand, { timeout: 30_000 }).toBe(14);
        await page.goto(entryUrl(baseURL, { ...config, seat: spectator ? '-1' : '0' }), { waitUntil: 'domcontentloaded' });
        await expect.poll(async () => {
          const state = await readState(page);
          return state.connected && state.seat === null && state.seatPlayers.length === 4
            && state.seatPlayers.every((player) => player !== null);
        }, { timeout: 30_000 }).toBe(true);
        const chrome = await releaseEffects(page, gate, testInfo);
        expect(chrome).toMatchObject({
          connected: true, seat: null, connectionLost: false, seatRowVisible: !spectator,
          leaveHidden: true, leaveDisabled: true, dealDisabled: true, setupDisabled: true, spectating: spectator,
        });
        const viewer = await readState(page);
        expect(viewer.ownHand, 'An unseated viewer must not acquire the owner hand projection').toBe(0);
        expect(viewer.faceUpHands, 'Every concealed hand must remain face-down for an unseated viewer').toBe(0);
        expect((await readState(owner)).seat, 'The original owner must retain seat 0').toBe(0);
      } finally {
        gate.release();
        await ownerContext.close();
      }
    });
  }

  test('late disconnected Changsha UI does not present the offline default seat as owned', async ({ page, baseURL }, testInfo) => {
    await prepare(page);
    await observeInitialChrome(page);
    const observed = observeNavigation(page);
    const gate = await delayModule(page, 'scene-effects');
    try {
      await page.goto(entryUrl(baseURL, { seat: '0' }), { waitUntil: 'domcontentloaded' });
      await expect.poll(gate.requested, { timeout: 30_000 }).toBe(true);
      const before = await readState(page);
      expect(before.clientUi).toBe(true);
      expect(before.connected).toBe(false);
      expect(before.seat).toBe(0);
      expect(observed.sockets).toHaveLength(0);
      const chrome = await releaseEffects(page, gate, testInfo);
      expect(chrome).toMatchObject({
        connected: false, connectionLost: true, seatRowVisible: false,
        leaveHidden: true, leaveDisabled: true, dealDisabled: true, setupDisabled: true, turnVisible: false,
      });
      await expect(page.getByTestId('new-game-button')).toBeVisible();
      await expect(page.getByTestId('new-game-button')).toBeEnabled();
      expect(observed.errors).toEqual([]);
    } finally {
      gate.release();
    }
  });
});

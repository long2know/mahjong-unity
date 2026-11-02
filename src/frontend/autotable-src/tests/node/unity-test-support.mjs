import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { createContext, runInContext } from 'node:vm';
import { EventEmitter } from 'node:events';
import { webcrypto } from 'node:crypto';

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
export const require = createRequire(path.join(root, 'package.json'));
const ts = require('typescript');
export const plain = value => JSON.parse(JSON.stringify(value));

export function sourceLoader(globals = {}, overrides = {}) {
  const cache = new Map();
  const stubs = new Map(Object.entries(overrides).map(([name, value]) => [path.resolve(root, 'src', name), value]));
  const context = createContext({
    console, setTimeout, clearTimeout, setInterval, clearInterval, URL, URLSearchParams, AbortController,
    Event, EventTarget, CustomEvent, Response, Headers, crypto: webcrypto, performance, btoa,
    ...globals,
  });
  const load = file => {
    let absolute = path.isAbsolute(file) ? file : path.resolve(root, 'src', file);
    if (!path.extname(absolute)) absolute += '.ts';
    if (stubs.has(absolute)) return stubs.get(absolute);
    if (absolute.endsWith('.css')) return {};
    if (absolute.endsWith('.json')) return JSON.parse(fs.readFileSync(absolute, 'utf8'));
    if (cache.has(absolute)) return cache.get(absolute).exports;
    const module = { exports: {} };
    cache.set(absolute, module);
    const code = ts.transpileModule(fs.readFileSync(absolute, 'utf8'), {
      compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS, esModuleInterop: true },
      fileName: absolute,
    }).outputText;
    const localRequire = id => id === 'events' ? { EventEmitter } : id.startsWith('.')
      ? load(path.resolve(path.dirname(absolute), id)) : require(id);
    const execute = runInContext(`(function(require, module, exports) { ${code}\n})`, context, { filename: absolute });
    execute(localRequire, module, module.exports);
    return module.exports;
  };
  return { load, context };
}

/** Real primary client/collections and World/Setup/Slot/Thing/Movement; only unrelated browser services are stubbed. */
export function gameHarness(variant = 'CHANGSHA', { spectator = false } = {}) {
  const location = new URL(`http://renderer.test/autotable/?variant=${variant}${spectator ? '&seat=-1' : ''}`);
  const sockets = [], frames = [], clears = [], failures = [], sounds = [];
  class Socket {
    static OPEN = 1; static CLOSING = 2; static CLOSED = 3;
    readyState = 0;
    sent = [];
    constructor(url) { this.url = url; sockets.push(this); }
    open() { this.readyState = 1; this.onopen?.(); }
    send(json) { this.sent.push(JSON.parse(json)); }
    receive(message) { this.onmessage?.({ data: JSON.stringify(message) }); }
    close() { this.readyState = 3; this.onclose?.(); }
  }
  const loader = sourceLoader({
    window: { location, localStorage: { getItem: () => null, setItem() {} } },
    document: { getElementById: () => null },
    WebSocket: Socket, console: { log() {}, warn() {}, error() {} },
    setInterval: () => 1, clearInterval() {},
  }, {
    'profile.ts': {
      loadProfile: async () => ({ displayName: 'Player' }), refreshProfile: async () => {},
      snapshotStatsForGame() {}, onProfile() {}, initProfileHubBindings() {},
    },
    'hub.ts': { getHubConnection: async () => ({}) },
    'reconnect.ts': { clearSession() {}, saveSession() {} },
    'game-state.ts': { clearGameState() {}, refreshGameState: async () => {}, setGameRoomConnected() {} },
    'lobby.ts': { bindNewGameControls() {} },
    'identity.ts': { bootstrapIdentity: async () => null, getIdentityBootstrapState: () => ({ status: 'ready' }) },
    'ui/rule-action-controls.ts': { getRuleActionControls: () => ({}) },
    'i18n.ts': { t: key => key, onLanguageChange() {} },
    'toast.ts': { showToast() {} },
  });
  const { Client } = loader.load('client.ts');
  const { World } = loader.load('world.ts');
  const { BufferedRendererSink } = loader.load('renderer/renderer-sink.ts');
  const { RendererFrameAdapter } = loader.load('renderer/renderer-frame.ts');
  const client = new Client(variant === 'CHANGSHA' ? 'changsha' : 'relay');
  const sink = new BufferedRendererSink();
  const world = new World(sink, { play: (...args) => sounds.push(args) }, client);
  const adapter = new RendererFrameAdapter('test-instance', client, world, sink, {
    clear: (session, reason) => clears.push({ session, reason }),
    publish: (layout, frame) => frames.push({ layout, frame }),
    unavailable: message => failures.push(message),
  });
  let revision = 1, seat = spectator ? null : 0, room = 'room-a';
  const join = (id = room, actor = seat, rev = 1) => {
    room = id; seat = actor; revision = rev;
    client.join(`ws://renderer.test/autotable/ws?variant=${variant}`, room);
    const socket = sockets.at(-1);
    socket.open();
    socket.receive({
      type: 'JOINED', gameId: room, playerId: 'verified-player', isFirst: false,
      ...(variant === 'CHANGSHA' ? { viewer: { roomId: room, revision, seat } } : {}),
    });
    return socket;
  };
  const types = loader.load('types.ts');
  const snapshot = (things, {
    full = true, phase = 'AwaitingDiscard', activeSeat = seat, pickup = null, actor = seat, rev = revision,
    extra = [], bound = true,
  } = {}) => {
    revision = rev; seat = actor;
    const conditions = types.Conditions.defaultsFor(types.GameType[variant]);
    const entries = [
      ['match', 0, { dealer: 0, honba: 0, conditions }],
      ['seats', 'verified-player', { seat: actor }],
      ['dice', 'current', { dice: [2, 5], state: 'rolled' }],
      ...things.map(([key, value]) => ['things', key, value]),
      ...(bound ? [['turn', 'current', { activeSeat, phase, awaitingDiscard: phase === 'AwaitingDiscard' }]] : []),
      ['pickup', 'current', pickup], ...extra,
    ];
    sockets.at(-1).receive({
      type: 'UPDATE', entries, full,
      ...(variant === 'CHANGSHA' ? { viewer: { roomId: room, revision, seat: actor } } : {}),
    });
    return adapter.latest;
  };
  const delta = (entries, { actor = seat, rev = revision } = {}) => {
    revision = rev; seat = actor;
    sockets.at(-1).receive({
      type: 'UPDATE', entries, full: false,
      ...(variant === 'CHANGSHA' ? { viewer: { roomId: room, revision, seat: actor } } : {}),
    });
    return adapter.latest;
  };
  const close = () => { adapter.dispose(); client.disconnect(); };
  return { ...loader, client, world, sink, adapter, types, frames, clears, failures, sounds, sockets, join, snapshot, delta, close, location };
}

export function thing(slotName, face, rotationIndex = 0) {
  return {
    slotName, rotationIndex, claimedBy: null, shiftSlotName: null, heldRotation: { x: 0, y: 0, z: 0, w: 1 },
    ...(face !== undefined ? { face } : {}),
  };
}

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { runInNewContext } from 'node:vm';
import { EventEmitter } from 'node:events';
import { setImmediate as settle } from 'node:timers/promises';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(path.join(root, 'package.json'));
const ts = require('typescript');
const compile = file => ts.transpileModule(fs.readFileSync(path.join(root, 'src', file), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText;

function harness({ status = 200, deferMetadata = false } = {}) {
  const requests = [], sockets = [], deferred = [], identityListeners = [], hubListeners = [];
  const identity = { playerId: 'verified-player', displayName: 'Verified player', avatarColor: '#234567', isFirstVisit: false };
  let verified = identity, globalConnections = 0;
  const location = new URL('http://readiness.test/autotable/?variant=changsha&gameId=room-a');
  const rootElement = {
    hidden: true, style: {}, classList: { toggle() {} }, setAttribute() {},
  };
  class Socket {
    static OPEN = 1;
    static CLOSED = 3;
    static CLOSING = 2;
    readyState = 0;
    sent = [];
    constructor() { sockets.push(this); }
    send(value) { this.sent.push(JSON.parse(value)); }
    open() { this.readyState = 1; this.onopen?.(); }
    receive(value) { this.onmessage?.({ data: JSON.stringify(value) }); }
    close() { if (this.readyState === 3) return; this.readyState = 3; this.onclose?.(); }
  }
  const response = url => {
    const room = decodeURIComponent(new URL(url, location).pathname.split('/')[3]);
    const data = url.includes('/chat?') ? { messages: [] } : {
      gameId: room, ownerId: identity.playerId, viewerIsOwner: true, phase: 'Seating',
      isPublic: false, publicName: null, voiceEnabled: false, viewerCanManageVoice: true,
      botCount: 0, seatedCount: 1, openHumanSeats: 3, canMakePublic: true, canInvite: true,
    };
    return new Response(JSON.stringify(data), { status, headers: { 'content-type': 'application/json' } });
  };
  const modules = {
    events: { EventEmitter },
    './identity': {
      bootstrapIdentity: async () => verified,
      getVerifiedIdentity: () => verified,
      getIdentityBootstrapState: () => ({ status: verified ? 'ready' : 'unavailable', error: verified ? null : 'offline' }),
      onIdentityBootstrap: fn => { identityListeners.push(fn); fn({ status: verified ? 'ready' : 'unavailable', error: null }); return () => {}; },
    },
    './hub': {
      getHubConnection: async () => { globalConnections++; return {}; },
      onHubConnected: fn => { hubListeners.push(fn); },
    },
    './profile': { loadProfile: async () => identity, refreshProfile: async () => {}, snapshotStatsForGame() {},
      onProfile() {}, initProfileHubBindings() {} },
    './reconnect': { clearSession() {}, saveSession() {} },
    './sound': { Sound: { play() {} } },
    './i18n': { t: key => key, onLanguageChange() {}, translateElements() {} },
    './dom-utils': { showEl: e => { e.hidden = false; }, setElHidden: (e, value) => { e.hidden = value; } },
    './lobby-presence': {
      getLobbyPresence: () => ({ players: [], invites: [], status: 'ready' }),
      subscribeLobbyPresence() {}, inviteExpired: () => false, markInvitesRead() {},
      sendTableInvite() {}, startLobbyPresence() {},
    },
    './room-join-url': { buildRoomJoinUrl: id => '/autotable/?gameId=' + id },
    './session-url': { readConcreteGameId: search => new URLSearchParams(search).get('gameId') },
    './settings-drawer': { getSettings: () => ({ mobileInfoPanel: 'none' }), onSettingsChange() {}, setSettings() {} },
    './mobile-overlay-policy': { COMPACT_VIEW_QUERY: '(max-width:900px)', toggleMobilePanel: () => 'none' },
  };
  const globals = {
    URL, URLSearchParams, Response, AbortController, WebSocket: Socket, console: { log() {}, error() {} },
    setInterval: () => 1, clearInterval() {},
    window: { location, localStorage: { getItem: () => null, setItem() {} },
      matchMedia: () => ({ matches: false, addEventListener() {} }),
      setInterval: () => 1, clearInterval() {}, setTimeout: () => 1, clearTimeout() {} },
    document: { getElementById: id => id === 'chat-panel' ? rootElement : null },
    fetch: (input, options = {}) => {
      const url = String(input);
      requests.push({ url, signal: options.signal });
      if (deferMetadata && !url.includes('/chat?')) {
        return new Promise(resolve => deferred.push(() => resolve(response(url))));
      }
      return Promise.resolve(response(url));
    },
  };
  const load = (name, key) => {
    const module = { exports: {} };
    runInNewContext(compile(name), {
      ...globals, module, exports: module.exports,
      require: id => { if (!(id in modules)) throw Error('Unexpected source dependency: ' + id); return modules[id]; },
    });
    modules[key] = module.exports;
    return module.exports;
  };
  const gameState = load('game-state.ts', './game-state');
  load('base-client.ts', './base-client');
  const { Client } = load('client.ts', './client');
  const chat = load('chat.ts', './chat');
  const client = new Client('changsha');
  chat.installChatPanel(client);
  const start = (room, create = false, variant = 'changsha') => {
    location.search = new URLSearchParams({ variant, gameId: room }).toString();
    const url = 'ws://readiness.test/autotable/ws?variant=' + variant + '&gameId=' + room;
    if (create) client.new(url);
    else client.join(url, room);
    const socket = sockets.at(-1);
    socket.open();
    socket.receive({ type: 'JOINED', gameId: room, playerId: identity.playerId, isFirst: false,
      viewer: { roomId: room, revision: 1, seat: 0 } });
    return socket;
  };
  const snapshot = (socket, room, bound, full = true) => socket.receive({
    type: 'UPDATE', full, viewer: { roomId: room, revision: 1, seat: 0 },
    entries: [
      ['seats', identity.playerId, { seat: 0 }],
      ['match', 0, { conditions: { gameType: 'CHANGSHA' } }],
      ...(bound ? [['turn', 'current', { phase: 'Seating', activeSeat: null, awaitingDiscard: false }]] : []),
    ],
  });
  return { requests, client, gameState, start, snapshot, deferred, rootElement,
    globalConnections: () => globalConnections,
    hubConnected: () => hubListeners.forEach(fn => fn()),
    setIdentity: value => { verified = value; identityListeners.forEach(fn => fn({ status: value ? 'ready' : 'unavailable', error: null })); },
  };
}

for (const create of [true, false]) {
  test(`${create ? 'NEW' : 'JOIN'} URL, JOINED and unbound FULL never trigger room HTTP`, async () => {
    const h = harness();
    await h.gameState.loadGameState('room-a');
    h.hubConnected();
    await settle();
    assert.equal(h.requests.length, 0);
    const socket = h.start('room-a', create);
    await settle();
    assert.equal(h.requests.length, 0);
    assert.equal(h.globalConnections(), 1, 'global lobby/profile connection is not delayed by room readiness');
    h.snapshot(socket, 'room-a', false);
    await settle();
    assert.equal(h.client.serverSnapshotGameId, null);
    assert.equal(h.requests.length, 0);
    h.snapshot(socket, 'room-a', true, false);
    await settle();
    assert.equal(h.requests.length, 0, 'a partial turn cannot promote the earlier unbound FULL');
    h.snapshot(socket, 'room-a', true);
    await settle();
    assert.equal(h.client.serverSnapshotGameId, 'room-a');
    assert.deepEqual(h.requests.map(r => r.url).sort(), ['/api/games/room-a', '/api/games/room-a/chat?limit=200']);
    assert.equal(h.gameState.getGameStateStatus().status, 'ready');
    h.snapshot(socket, 'room-a', true);
    await settle();
    assert.equal(h.requests.length, 2, 'duplicate bound snapshots do not refetch identical metadata/history');
  });
}

test('disconnect/reconnect revokes readiness and fetches promptly only after a new bound FULL', async () => {
  const h = harness();
  let socket = h.start('room-a');
  h.snapshot(socket, 'room-a', true);
  await settle();
  h.client.disconnect();
  assert.equal(h.gameState.getGameState(), null);
  const mark = h.requests.length;
  await h.gameState.refreshGameState('room-a');
  h.hubConnected();
  socket = h.start('room-a');
  h.snapshot(socket, 'room-a', false);
  await settle();
  assert.equal(h.requests.length, mark);
  h.snapshot(socket, 'room-a', true);
  await settle();
  assert.equal(h.requests.length, mark + 2);
});

test('room switch cancels old metadata and prevents URL/JOINED history from crossing the boundary', async () => {
  const h = harness({ deferMetadata: true });
  const first = h.start('room-a');
  h.snapshot(first, 'room-a', true);
  await settle();
  const oldRequest = h.requests.find(r => r.url === '/api/games/room-a');
  const mark = h.requests.length;
  const next = h.start('room-b');
  assert.equal(oldRequest.signal.aborted, true);
  await h.gameState.loadGameState('room-b');
  h.snapshot(next, 'room-b', false);
  await settle();
  assert.equal(h.requests.length, mark);
  h.deferred.shift()();
  await settle();
  assert.equal(h.gameState.getGameState(), null);
  h.snapshot(next, 'room-b', true);
  await settle();
  assert.deepEqual(h.requests.slice(mark).map(r => r.url).sort(), ['/api/games/room-b', '/api/games/room-b/chat?limit=200']);
  h.deferred.shift()();
  await settle();
  assert.equal(h.gameState.getGameState().gameId, 'room-b');
});

test('bound state does not bypass verified identity, and a real404 remains a failure', async () => {
  const unverified = harness();
  unverified.setIdentity(null);
  const socket = unverified.start('room-a');
  unverified.snapshot(socket, 'room-a', true);
  await settle();
  assert.equal(unverified.requests.length, 0);
  assert.equal(unverified.gameState.getGameStateStatus().status, 'identity-required');

  const missing = harness({ status: 404 });
  const joined = missing.start('room-a');
  missing.snapshot(joined, 'room-a', true);
  await settle();
  assert.equal(missing.requests.length, 2);
  assert.equal(missing.gameState.getGameState(), null);
  assert.equal(missing.gameState.getGameStateStatus().status, 'not-found');
  assert.equal(missing.gameState.getGameStateStatus().error, 'HTTP 404');
});

test('relay snapshots keep their normal client marker without requesting Changsha room APIs', async () => {
  const h = harness();
  const socket = h.start('relay-room', false, 'four_player');
  h.snapshot(socket, 'relay-room', false);
  await settle();
  assert.equal(h.client.serverSnapshotGameId, 'relay-room');
  assert.equal(h.globalConnections(), 1);
  await h.gameState.loadGameState('relay-room');
  assert.equal(h.requests.length, 0);
});

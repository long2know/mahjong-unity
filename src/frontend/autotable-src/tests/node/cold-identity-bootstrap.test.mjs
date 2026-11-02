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
const compile = source => ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText;
const read = file => fs.readFileSync(path.join(root, 'src', file), 'utf8');
const identityA = { playerId: 'verified-A', displayName: 'Player A', avatarColor: '#2980b9', isNewProfile: true };

function harness({ cache = null, authCache = null } = {}) {
  const requests = [], identityRequests = [], ws = [], hubStarts = [];
  const storage = new Map();
  if (cache) storage.set('mahjong.identity.cache.v1', JSON.stringify(cache));
  if (authCache) storage.set('mahjong.auth.cache.v1', JSON.stringify(authCache));
  const localStorage = { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) };
  let bodyParsed = false;
  class HubConnection {
    state = 'Disconnected';
    onreconnecting() {}
    onreconnected() {}
    onclose() {}
    async start() { hubStarts.push({ bodyParsed }); this.state = 'Connected'; }
    async stop() { this.state = 'Disconnected'; }
  }
  class HubConnectionBuilder {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    configureLogging() { return this; }
    build() { return new HubConnection(); }
  }
  const modules = {
    events: { EventEmitter },
    './profile': { AVATAR_COLOR_PRESETS: ['#2980b9'], validateAvatarColor: () => true },
    './dom-utils': { showEl() {}, hideEl() {} },
    '@microsoft/signalr': {
      HubConnectionBuilder, HubConnectionState: { Connected: 'Connected', Reconnecting: 'Reconnecting', Disconnecting: 'Disconnecting' },
      LogLevel: { Warning: 1 },
    },
  };
  const globals = {
    window: { localStorage, location: { pathname: '/autotable/', search: '?variant=changsha&gameId=room', href: 'http://bootstrap.test/autotable/?variant=changsha&gameId=room' } },
    localStorage, document: { cookie: '', getElementById: id => id === 'connect' ? { disabled: true } : null },
    console: { log() {}, error() {} },
    URL, URLSearchParams, AbortController,
    fetch: (url, options) => {
      requests.push({ url, method: options?.method, bodyParsed });
      if (url === '/api/identity') {
        return new Promise(resolve => identityRequests.push({
          resolve: (payload = identityA, status = 200, bodyGate = Promise.resolve()) => resolve({
            ok: status >= 200 && status < 300, status,
            json: async () => { await bodyGate; bodyParsed = true; return payload; },
          }),
        }));
      }
      if (url === '/api/auth/providers') return Promise.resolve({ status: 200, ok: true, json: async () => ({ providers: ['email'] }) });
      if (url === '/api/auth/me') return Promise.resolve({ status: 200, ok: true, json: async () => ({ authenticated: false, playerId: 'verified-A' }) });
      throw Error('Unexpected bootstrap request: ' + url);
    },
  };
  const load = (file, key) => {
    const module = { exports: {} };
    runInNewContext(compile(read(file)), {
      ...globals, module, exports: module.exports,
      require: name => { if (!(name in modules)) throw Error('Unexpected source import: ' + name); return modules[name]; },
    });
    modules[key] = module.exports;
    return module.exports;
  };
  const identity = load('identity.ts', './identity');
  const hub = load('hub.ts', './hub');
  const auth = load('auth.ts', './auth');
  const tree = ts.createSourceFile('client-ui.ts', read('client-ui.ts'), ts.ScriptTarget.Latest, true);
  const ui = tree.statements.find(s => ts.isClassDeclaration(s) && s.name?.text === 'ClientUi');
  const connect = ui.members.find(m => ts.isMethodDeclaration(m) && m.name.getText(tree) === 'connectWithVerifiedIdentity');
  const method = ts.createPrinter().printNode(ts.EmitHint.Unspecified, connect, tree);
  const UiFixture = runInNewContext(compile('class UiFixture {' + method + '}\nUiFixture;'), {
    ...globals, ...identity, isJoinOnly: () => false, t: key => key,
  });
  const clientUi = new UiFixture();
  Object.assign(clientUi, {
    connectEpoch: 0, identityConnectPending: false, creatingGameId: 'room',
    connectionBannerText: null, setStatus() {}, showBannerFailed() {},
    client: { new: url => ws.push({ command: 'NEW', url, bodyParsed }), join: (url, gameId) => ws.push({ command: 'JOIN', url, gameId, bodyParsed }) },
  });
  return { identity, hub, auth, clientUi, requests, identityRequests, ws, hubStarts,
    resolveAll: (payload = identityA, status = 200) => identityRequests.forEach(r => r.resolve(payload, status)) };
}

test('actual identity loading-event reentrancy is one flight, not a second cookie-less POST', async () => {
  const h = harness();
  let reentered = false, inner;
  h.identity.onIdentityBootstrap(state => {
    if (state.status === 'loading' && !reentered) { reentered = true; inner = h.identity.bootstrapIdentity(); }
  });
  const outer = h.identity.bootstrapIdentity();
  await settle();
  assert.equal(h.identityRequests.length, 1);
  h.resolveAll();
  const [a, b] = await Promise.all([outer, inner]);
  assert.equal(a.playerId, 'verified-A');
  assert.equal(b.playerId, 'verified-A');
});

test('identity-publication reentrancy and concurrent callers share the same verified result', async () => {
  const h = harness();
  let inner;
  h.identity.onIdentity(() => { inner = h.identity.bootstrapIdentity(); });
  const a = h.identity.bootstrapIdentity(), b = h.identity.bootstrapIdentity();
  await settle();
  assert.equal(h.identityRequests.length, 1);
  h.resolveAll();
  await Promise.all([a, b]);
  assert.equal((await inner).playerId, 'verified-A');
  await h.identity.bootstrapIdentity();
  assert.equal(h.identityRequests.length, 1);
});

test('actual primary ClientUi and lobby hub cannot start before verified bootstrap body completion', async () => {
  const h = harness();
  const primary = h.clientUi.connectWithVerifiedIdentity('ws://bootstrap.test/autotable/ws?variant=changsha', 'room');
  const lobby = h.hub.getHubConnection();
  await settle();
  assert.equal(h.identityRequests.length, 1);
  assert.deepEqual(h.ws, []);
  assert.deepEqual(h.hubStarts, []);
  let release;
  const body = new Promise(resolve => { release = resolve; });
  h.identityRequests[0].resolve(identityA, 200, body);
  await settle();
  assert.deepEqual(h.ws, [], 'headers alone are not verified bootstrap completion');
  assert.deepEqual(h.hubStarts, []);
  release();
  await Promise.all([primary, lobby]);
  assert.equal(h.ws.length, 1);
  assert.equal(h.ws[0].bodyParsed, true);
  assert.equal(h.hubStarts.length, 1);
  assert.equal(h.hubStarts[0].bodyParsed, true);
});

test('actual auth/me and auth refresh wait behind the single guest-identity bootstrap', async () => {
  const h = harness();
  const identity = h.identity.bootstrapIdentity();
  const auth = h.auth.bootstrapAuth();
  await settle();
  assert.equal(h.identityRequests.length, 1);
  assert.equal(h.requests.some(r => r.url === '/api/auth/me'), false);
  const refresh = h.auth.refreshAuth();
  await settle();
  assert.equal(h.requests.some(r => r.url === '/api/auth/me'), false);
  h.resolveAll();
  await Promise.all([identity, auth, refresh]);
  const me = h.requests.filter(r => r.url === '/api/auth/me');
  assert(me.length >= 1);
  assert(me.every(r => r.bodyParsed));
  assert.equal(h.identityRequests.length, 1);
});

test('actual lobby connecting-event reentrancy cannot start the same connection twice', async () => {
  const h = harness();
  let reentered = false, inner;
  h.hub.onHubStatus(status => {
    if (status.state === 'connecting' && !reentered) { reentered = true; inner = h.hub.getHubConnection(); }
  });
  const outer = h.hub.getHubConnection();
  await settle();
  assert.equal(h.identityRequests.length, 1);
  h.resolveAll();
  await Promise.all([outer, inner]);
  assert.equal(h.hubStarts.length, 1);
});

test('cached auth publication reentrancy does not duplicate bootstrap provider requests', async () => {
  const h = harness({ authCache: { authenticated: false, email: null, primaryProvider: null } });
  let enabled = false, reentered = false, inner;
  h.auth.onAuth(() => {
    if (enabled && !reentered) { reentered = true; inner = h.auth.bootstrapAuth(); }
  });
  enabled = true;
  const outer = h.auth.bootstrapAuth();
  await settle();
  assert.equal(h.requests.filter(r => r.url === '/api/auth/providers').length, 1);
  h.resolveAll();
  await Promise.all([outer, inner]);
});

test('failed identity keeps a display cache but authorizes neither primary nor lobby transport; retry is fresh', async () => {
  const h = harness({ cache: identityA });
  const primary = h.clientUi.connectWithVerifiedIdentity('ws://bootstrap.test/autotable/ws', 'room');
  const lobby = h.hub.getHubConnection().catch(() => null);
  await settle();
  h.resolveAll({}, 503);
  await Promise.all([primary, lobby]);
  assert.equal(h.identity.getVerifiedIdentity(), null);
  assert.deepEqual(h.ws, []);
  assert.deepEqual(h.hubStarts, []);
  const retry = h.identity.bootstrapIdentity();
  await settle();
  assert.equal(h.identityRequests.length, 2);
  h.identityRequests[1].resolve(identityA);
  assert.equal((await retry).playerId, 'verified-A');
});

test('auth/me also stays blocked after failed bootstrap and an explicit refresh retries identity first', async () => {
  const h = harness();
  const first = h.auth.bootstrapAuth();
  await settle();
  assert.equal(h.identityRequests.length, 1);
  h.resolveAll({}, 503);
  await first;
  assert.equal(h.requests.some(r => r.url === '/api/auth/me'), false);
  const refresh = h.auth.refreshAuth();
  await settle();
  assert.equal(h.identityRequests.length, 2);
  assert.equal(h.requests.some(r => r.url === '/api/auth/me'), false);
  h.identityRequests[1].resolve(identityA);
  await refresh;
  assert.equal(h.requests.filter(r => r.url === '/api/auth/me').length, 1);
});

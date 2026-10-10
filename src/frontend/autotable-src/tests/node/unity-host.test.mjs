import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { setTimeout as wait } from 'node:timers/promises';
import { sourceLoader, plain } from './unity-test-support.mjs';

class Element extends EventTarget {
  constructor(tag) {
    super();
    this.tagName = tag.toUpperCase();
    this.children = [];
    this.style = { setProperty() {} };
    this.dataset = {};
    this.attributes = {};
    this.classList = { add() {} };
    this.hidden = false;
    this.rect = { width: 390, height: 844 };
  }
  setAttribute(key, value) { this.attributes[key] = String(value); }
  getAttribute(key) { return this.attributes[key] ?? null; }
  removeAttribute(key) { delete this.attributes[key]; }
  append(...children) { children.forEach(child => this.appendChild(child)); }
  appendChild(child) { this.children.push(child); child.parent = this; return child; }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); }
  getBoundingClientRect() { return this.rect; }
}
function fixture({ missing = false, mime = 'application/json', html = false, unsupported = false, loaderFailure = false, deferred = false, dirty = false, development = false, allow = false, corrupt } = {}) {
  const requests = [], calls = [], actions = [], notices = [], resizes = [];
  let failures = 0, quits = 0, reloads = 0, resolveInstance;
  const body = new Element('body'), head = new Element('head');
  const main = new Element('main');
  body.append(main);
  const walk = (element, id) => element.id === id ? element : element.children.map(child => walk(child, id)).find(Boolean);
  const document = Object.assign(new EventTarget(), {
    body, head, hidden: false, documentElement: new Element('html'),
    createElement: tag => new Element(tag), getElementById: id => walk(body, id),
  });
  const visual = Object.assign(new EventTarget(), { height: 844, offsetTop: 0 });
  const window = Object.assign(new EventTarget(), {
    location: new URL('http://localhost/autotable/'), innerHeight: 844, devicePixelRatio: 3, visualViewport: visual,
    matchMedia: () => new EventTarget(),
    ...(!unsupported ? { WebGL2RenderingContext: class {} } : {}),
  });
  window.location.reload = () => reloads++;
  const content = {
    loader: Buffer.from('// Mock generated-loader bytes. No Unity or graphics are executed by this test.'),
    framework: Buffer.from('// Mock framework bytes.'),
    data: Buffer.from([3, 4, 5]),
    wasm: Buffer.from([0, 97, 115, 109, 1, 0, 0, 0]),
  };
  const extensions = { loader: 'loader.js', framework: 'framework.js', data: 'data', wasm: 'wasm' };
  const artifacts = Object.fromEntries(Object.entries(content).map(([kind, bytes]) => [kind, {
    path: 'Build/mock.' + extensions[kind], bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex'),
  }]));
  const manifest = {
    artifactKind: 'unity-webgl', contractVersion: '1.0.0', buildId: 'mock-unit-build', unityVersion: '6000.3.24f1',
    unityRevision: '4e7b9b5b6244', compression: 'none', threading: 'single-threaded', developmentBuild: development,
    baselineRevision: 'd10d351e543aa3e341960de9800828b6c0662a82', rendererSourceRevision: 'a'.repeat(40),
    rendererSourceTreeSha256: 'b'.repeat(64), sourceDirty: dirty, builtAtUtc: '2026-10-10T03:00:00Z',
    ...artifacts, streamingAssetsPath: null,
  };
  const MockUnityInstance = {
    SendMessage: (receiver, method, json) => calls.push({ receiver, method, value: JSON.parse(json) }),
    Quit: async () => { quits++; },
  };
  const fetch = async (input, options) => {
    const url = new URL(String(input), window.location);
    requests.push({ url, options });
    if (url.pathname.endsWith('/renderer-build.json')) {
      return new Response(html ? '<!doctype html><title>Fallback</title>' : JSON.stringify(manifest), {
        status: missing ? 404 : 200, headers: { 'content-type': mime },
      });
    }
    const kind = Object.keys(artifacts).find(kind => url.pathname.endsWith(artifacts[kind].path));
    if (!kind) return new Response('missing', { status: 404 });
    const data = corrupt === kind ? Buffer.from([8, 8, 8]) : content[kind];
    return new Response(data, { headers: { 'content-type': kind === 'wasm' ? 'application/wasm' : kind === 'data' ? 'application/octet-stream' : 'application/javascript' } });
  };
  const append = head.appendChild.bind(head);
  head.appendChild = element => {
    append(element);
    window.createUnityInstance = async (canvas, config, progress) => {
      calls.push({ method: 'createUnityInstance', canvas, config });
      progress(0.65);
      if (loaderFailure) throw Error('Mock WebGL startup failure');
      return deferred ? new Promise(resolve => { resolveInstance = resolve; }) : MockUnityInstance;
    };
    queueMicrotask(() => element.onload());
    return element;
  };
  const observers = [];
  class Observer {
    constructor(callback) { this.callback = callback; observers.push(this); }
    observe() {}
    disconnect() { this.disconnected = true; }
  }
  const { UnityHost, validateBuildManifest, artifactUrl } = sourceLoader({
    document, window, fetch, ResizeObserver: Observer,
    getComputedStyle: () => ({ paddingTop: '20px', paddingRight: '0px', paddingBottom: '10px', paddingLeft: '0px' }),
  }).load('renderer/unity-host.ts');
  const host = new UnityHost(main, {
    action: value => actions.push(value), resized: value => resizes.push(value), resumed() {},
    failure: () => failures++, notice: value => notices.push(value),
  }, { allowUnqualifiedBuild: allow });
  const sessionId = 'test-session';
  host.clear(sessionId, 'join');
  const layout = {
    contractVersion: '1.0.0', instanceId: host.instanceId, sessionId, type: 'layout', layoutRevision: 1,
    gameType: 'CHANGSHA', activeSeats: [0, 1, 2, 3], canonicalBoard: { width: 174, depth: 174 }, slots: [],
  };
  const frame = frameId => ({
    contractVersion: '1.0.0', instanceId: host.instanceId, sessionId, type: 'frame', frameId, layoutRevision: 1,
    session: { gameId: 'room-a', mode: 'changsha', connected: true, snapshotReady: false, actorSeat: null, viewSeat: null, viewerRevision: 1 },
    presentation: { viewMode: 'perspective', boardLayout: 'classic', handLayout: 'in-table', tileLabels: true, lookDown: 0, zoom: 0 },
    match: null, entities: [], seats: [], localHand: { seat: null, entityIds: [], drawnEntityId: null }, turn: null, pickup: null, dice: null,
  });
  const emit = value => window.MahjongUnityBridge.emit(JSON.stringify(value));
  const ready = (extra = {}) => emit({
    contractVersion: '1.0.0', instanceId: host.instanceId, type: 'ready', buildId: manifest.buildId,
    unityVersion: '6000.3.24f1', ...extra,
  });
  const rendered = frameId => emit({
    contractVersion: '1.0.0', instanceId: host.instanceId, sessionId, type: 'frameRendered', frameId, layoutRevision: 1,
  });
  return {
    host, manifest, layout, frame, ready, rendered, emit, document, window, main, calls, requests, resizes, notices, actions,
    validateBuildManifest, artifactUrl, observers, failures: () => failures, quits: () => quits,
    resolveInstance: () => resolveInstance?.(MockUnityInstance),
  };
}

test('missing build, HTML fallback and unsupported browser are visible failures without any alternate renderer', async t => {
  for (const options of [{ missing: true }, { mime: 'text/html', html: true }, { html: true }, { unsupported: true }]) {
    const f = fixture(options);
    t.after(() => f.host.dispose());
    await f.host.mount();
    assert(['unavailable', 'failed', 'unsupported'].includes(f.host.state));
    assert.equal(f.calls.length, 0);
    assert.equal(f.document.body.getAttribute('data-unity-renderer-ready'), null);
    assert.equal(f.document.getElementById('unity-status').hidden, false);
    assert.equal(f.failures(), 1);
  }
});

test('manifest validation enforces pins, safe same-origin paths, no development bypass and explicit local dirty-source qualification', t => {
  const f = fixture();
  t.after(() => f.host.dispose());
  for (const mutation of [
    { contractVersion: '2.0.0' }, { unityVersion: '6000.3.25f1' }, { unityRevision: 'incorrect' },
    { loader: { ...f.manifest.loader, path: '../evil.loader.js' } }, { loader: { ...f.manifest.loader, path: '/evil.loader.js' } },
    { wasm: { ...f.manifest.wasm, path: 'https://outside.test/mock.wasm' } }, { receiver: 'OtherBridge' },
    { developmentBuild: true },
  ]) assert.throws(() => f.validateBuildManifest({ ...f.manifest, ...mutation }, 'http://localhost', true));
  assert.throws(() => f.validateBuildManifest({ ...f.manifest, sourceDirty: true }, 'http://localhost'));
  assert.throws(() => f.validateBuildManifest({ ...f.manifest, sourceDirty: true }, 'https://production.test', true));
  assert.doesNotThrow(() => f.validateBuildManifest({ ...f.manifest, sourceDirty: true }, 'http://localhost', true));
  assert.throws(() => f.artifactUrl('Build//mock.wasm', 'http://localhost'));
});

test('actual host checks fixture integrity/MIME before invoking the mock loader and preserves exact config/Configure receiver', async t => {
  const f = fixture();
  t.after(() => f.host.dispose());
  await f.host.mount();
  assert.equal(f.host.state, 'awaiting-ready');
  assert.equal(f.document.body.getAttribute('data-unity-renderer-ready'), null);
  assert.deepEqual(f.requests.map(r => r.url.pathname), [
    '/autotable/unity/renderer-build.json', '/autotable/unity/Build/mock.loader.js', '/autotable/unity/Build/mock.framework.js',
    '/autotable/unity/Build/mock.data', '/autotable/unity/Build/mock.wasm',
  ]);
  const loader = f.calls.find(c => c.method === 'createUnityInstance');
  assert.equal(loader.canvas, f.host.canvas);
  assert.equal(loader.config.matchWebGLToCanvasSize, false);
  assert.equal(loader.config.devicePixelRatio, 2);
  assert.equal(loader.config.codeUrl, 'http://localhost/autotable/unity/Build/mock.wasm');
  assert.deepEqual(f.calls.find(c => c.method === 'Configure'), {
    receiver: 'MahjongBridge', method: 'Configure',
    value: { contractVersion: '1.0.0', instanceId: f.host.instanceId, type: 'configure', buildId: 'mock-unit-build' },
  });
});

test('mock Ready is not readiness; only a matching simulated FrameRendered for the newest complete publication sets markers', async t => {
  const f = fixture();
  t.after(() => f.host.dispose());
  f.host.publish(f.layout, f.frame(1));
  f.host.publish(f.layout, f.frame(2));
  await f.host.mount();
  f.ready();
  assert.equal(f.document.body.getAttribute('data-scene-shell-ready'), null);
  await wait(40);
  assert.deepEqual(f.calls.filter(c => ['Clear', 'SetViewport', 'ApplyLayout', 'ApplyFrame'].includes(c.method)).map(c => c.method),
    ['Clear', 'SetViewport', 'ApplyLayout', 'ApplyFrame']);
  assert.equal(f.calls.find(c => c.method === 'ApplyFrame').value.frameId, 2);
  f.rendered(2);
  assert.equal(f.host.state, 'ready');
  assert.equal(f.document.body.getAttribute('data-unity-renderer-ready'), 'true');
  assert.equal(f.document.body.getAttribute('data-scene-shell-ready'), 'true');
  f.host.clear('another-session', 'authority-change');
  assert.equal(f.document.body.getAttribute('data-unity-renderer-ready'), null);
  assert.equal(f.calls.at(-1).method, 'Clear');
  f.emit({ contractVersion: '1.0.0', instanceId: f.host.instanceId, sessionId: 'test-session', type: 'frameRendered', frameId: 2, layoutRevision: 1 });
  assert.equal(f.document.body.getAttribute('data-unity-renderer-ready'), null, 'late old-session receipt cannot resurrect private state');
});

test('corrupt files, wrong handshake and loader rejection fail visibly and never report graphics success', async t => {
  for (const options of [{ corrupt: 'wasm' }, { loaderFailure: true }, {}]) {
    const f = fixture(options);
    t.after(() => f.host.dispose());
    await f.host.mount();
    if (!options.corrupt && !options.loaderFailure) f.ready({ buildId: 'another-build' });
    assert(['failed', 'unavailable'].includes(f.host.state));
    assert.equal(f.document.body.getAttribute('data-unity-renderer-ready'), null);
    assert.equal(f.document.getElementById('unity-status').hidden, false);
  }
});

test('resizes/orientation/DPR/insets/hand band update the real host buffer and cancel before changed picking transforms', async t => {
  const f = fixture();
  t.after(() => f.host.dispose());
  await f.host.mount();
  f.ready();
  assert.equal(f.host.viewport.widthPx, 780);
  assert.equal(f.host.viewport.heightPx, 1688);
  assert.deepEqual(plain(f.host.viewport.insetsCssPx), { top: 20, right: 0, bottom: 10, left: 0 });
  assert.equal(f.host.viewport.handBandCssPx, 0);
  f.host.setPresentation({ ...f.frame(1).presentation, handLayout: 'separate' });
  assert(f.host.viewport.handBandCssPx > 0);
  const before = f.host.viewport.resizeRevision;
  f.main.rect = { width: 844, height: 390 };
  f.window.devicePixelRatio = 1;
  f.window.innerHeight = 390;
  f.window.visualViewport.height = 390;
  f.window.dispatchEvent(new Event('orientationchange'));
  assert(f.host.viewport.resizeRevision > before);
  assert.equal(f.host.canvas.width, 844);
  assert.equal(f.host.canvas.height, 390);
  assert.equal(f.resizes.at(-1).widthCssPx, 844);
  assert.equal(f.calls.at(-1).method, 'SetViewport');
  f.host.setPresentation({ ...f.frame(1).presentation, handLayout: 'in-table' });
  assert.equal(f.host.viewport.handBandCssPx, 0);
});

test('visibility suspends visual playback only; disposal detaches observers/bridge and awaits runtime Quit', async () => {
  const f = fixture();
  await f.host.mount();
  f.ready();
  f.document.hidden = true;
  f.document.dispatchEvent(new Event('visibilitychange'));
  assert.equal(f.calls.at(-1).method, 'SetPlayback');
  assert.equal(f.calls.at(-1).value.suspended, true);
  const oldBridge = f.window.MahjongUnityBridge;
  await f.host.dispose();
  assert.equal(f.host.state, 'disposed');
  assert.equal(f.quits(), 1);
  assert.equal(f.window.MahjongUnityBridge, undefined);
  assert.equal(f.observers[0].disconnected, true);
  const count = f.calls.length;
  oldBridge.emit('{"type":"ready"}');
  f.window.dispatchEvent(new Event('resize'));
  assert.equal(f.calls.length, count);
});

test('disposal during createUnityInstance quits a late instance without Configure or readiness', async () => {
  const f = fixture({ deferred: true });
  const mount = f.host.mount();
  await wait(30);
  await f.host.dispose();
  f.resolveInstance();
  await mount;
  assert.equal(f.quits(), 1);
  assert.equal(f.calls.some(c => c.method === 'Configure'), false);
  assert.equal(f.document.body.getAttribute('data-unity-renderer-ready'), null);
});

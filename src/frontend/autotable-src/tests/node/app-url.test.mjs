import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { runInNewContext } from 'node:vm';
import { sourceLoader, root, require } from './renderer-test-support.mjs';
const ts = require('typescript');

function fixture(base) {
  const calls = [], window = { location: new URL(`https://host.test${base}/autotable/?gameId=fixture`) };
  const loader = sourceLoader({ window, fetch: async (...args) => { calls.push(args); return new Response('ok'); } });
  return { ...loader.load('app-url.ts'), calls, window };
}

test('one runtime resolver preserves root, nested and escaped mount paths without affecting external links', async () => {
  for (const prefix of ['', '/mahjong', '/games/mahjong', '/My%20Game', '/api', '/autotable']) {
    const f = fixture(prefix);
    assert.equal(f.appBasePath(), prefix);
    for (const endpoint of ['/api/identity', '/hubs/changsha', '/autotable/', '/health?simple=1']) {
      assert.equal(f.appUrl(endpoint), prefix + endpoint);
      await f.appFetch(endpoint, { credentials: 'same-origin' });
      assert.equal(f.calls.at(-1)[0], prefix + endpoint);
    }
    assert.equal(f.appWebSocketUrl('/autotable/ws'), `wss://host.test${prefix}/autotable/ws`);
    assert.equal(f.appUrl('https://outside.test/api'), 'https://outside.test/api');
    assert.equal(f.appUrl('//outside.test/path'), '//outside.test/path');
    assert.equal(f.appUrl('./sw.js'), './sw.js');
    f.window.location = new URL(`https://host.test${prefix}/spectate/fixture`);
    assert.equal(f.appUrl('/api/identity'), prefix + '/api/identity', 'history routing must not forget the original mount');
  }
  assert.equal(fixture('/m%61hjong').appUrl('/api/identity'), '/mahjong/api/identity');
});

test('room invitations and hub/native socket construction use the same mounted namespace', () => {
  const calls = [], window = { location: new URL('http://host.test/games/mahjong/autotable/') };
  const loader = sourceLoader({ window });
  const { buildRoomJoinUrl } = loader.load('room-join-url.ts');
  assert.equal(buildRoomJoinUrl('my-table'), '/games/mahjong/autotable/?gameId=my-table&variant=changsha&join=1');
  assert.equal(buildRoomJoinUrl('my-table', true),
    'http://host.test/games/mahjong/autotable/?gameId=my-table&variant=changsha&join=1');
  assert.equal(loader.load('app-url.ts').appWebSocketUrl('/autotable/ws'), 'ws://host.test/games/mahjong/autotable/ws');
});

test('every production direct fetch call is bound to the shared adapter, not an unmounted global', () => {
  const folder = path.join(root, 'src');
  const walk = directory => fs.readdirSync(directory, { withFileTypes: true })
    .flatMap(entry => entry.isDirectory() ? walk(path.join(directory, entry.name)) : [path.join(directory, entry.name)]);
  for (const file of walk(folder).filter(file => file.endsWith('.ts') && !file.endsWith('/app-url.ts'))) {
    const text = fs.readFileSync(file, 'utf8'), source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
    let direct = false;
    const visit = node => {
      if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'fetch') direct = true;
      ts.forEachChild(node, visit);
    };
    visit(source);
    if (direct) assert.match(text, /import \{[^}]*appFetch as fetch[^}]*\} from ['"].*app-url['"]/,
      `${path.relative(root, file)} must not regress into root-only HTTP requests`);
  }
});

function worker(prefix) {
  const handlers = {}, fetched = [], deleted = [], opened = [], added = [];
  const namespace = `mahjong-web:${encodeURIComponent(prefix || '/')}:`;
  const self = { location: new URL(`https://host.test${prefix}/autotable/sw.js`),
    addEventListener: (name, handler) => { handlers[name] = handler; }, skipWaiting: async () => {},
    clients: { claim: async () => {} } };
  const caches = {
    keys: async () => [namespace + 'v1-static', 'mahjong-web:%2Fother:v1-static'],
    delete: async key => { deleted.push(key); },
    open: async key => {
      opened.push(key);
      return { match: async () => undefined, put: async () => {}, addAll: async urls => { added.push(...urls); } };
    },
  };
  const fetch = async input => {
    const url = typeof input === 'string' ? input : input.url;
    fetched.push(url);
    return url.endsWith('manifest-precache.json')
      ? new Response(JSON.stringify({ assets: ['./index.html', './main.1234abcd.js', '/wrong-root.js', 'https://outside.test/a.js'] }))
      : new Response('asset');
  };
  runInNewContext(fs.readFileSync(path.join(root, 'sw.js'), 'utf8'), { self, caches, fetch, URL, Response });
  return { handlers, fetched, deleted, opened, added, namespace };
}

test('real service worker install resolves precache against its mount and activation cannot erase another mount cache', async () => {
  for (const prefix of ['', '/mahjong', '/games/My%20Game']) {
    const f = worker(prefix);
    let task;
    f.handlers.install({ waitUntil: promise => { task = promise; } }); await task;
    assert.deepEqual(Array.from(f.added), [`https://host.test${prefix}/autotable/index.html`,
      `https://host.test${prefix}/autotable/main.1234abcd.js`]);
    assert(f.fetched.every(url => url.startsWith(`https://host.test${prefix}/autotable/`)));
    f.handlers.activate({ waitUntil: promise => { task = promise; } }); await task;
    assert.deepEqual(f.deleted, [f.namespace + 'v1-static']);
  }
});

test('mounted service worker never caches private API/hub traffic or another application path', async () => {
  const f = worker('/games/mahjong');
  for (const suffix of ['/api/identity', '/api/auth/me', '/hubs/changsha/negotiate']) {
    let handled = false;
    f.handlers.fetch({ request: { url: 'https://host.test/games/mahjong' + suffix, method: 'GET' },
      respondWith() { handled = true; } });
    assert.equal(handled, false, suffix);
  }
  f.handlers.fetch({ request: { url: 'https://host.test/other/main.1234abcd.js', method: 'GET' },
    respondWith() { assert.fail('another mount must not be cached'); } });
  let task;
  f.handlers.fetch({ request: { url: 'https://host.test/games/mahjong/api/games/public', method: 'GET' },
    respondWith: promise => { task = promise; } });
  assert.equal((await task).status, 200);
  assert(f.opened.every(key => key.startsWith(f.namespace)));
});

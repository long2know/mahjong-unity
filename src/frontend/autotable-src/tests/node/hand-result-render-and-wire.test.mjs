import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { runInNewContext } from 'node:vm';
import { EventEmitter } from 'node:events';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(path.join(root, 'package.json'));
const ts = require('typescript');
const javascript = source => ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText;

class Element {
  textContent = '';
  style = {};
  children = [];
  dataset = {};
  attributes = {};
  hidden = false;
  setAttribute(name, value) { this.attributes[name] = value; }
  set innerHTML(value) { this.children = []; this.textContent = value; }
  appendChild(child) { this.children.push(child); }
}

const guiSource = ts.createSourceFile('game-ui.ts', fs.readFileSync(path.join(root, 'src/game-ui.ts'), 'utf8'), ts.ScriptTarget.Latest, true);
const gui = guiSource.statements.find(s => ts.isClassDeclaration(s) && s.name?.text === 'GameUi');
const renderer = gui.members.find(m => ts.isMethodDeclaration(m) && m.name.getText(guiSource) === 'renderResult');
const handRenderer = gui.members.find(m => ts.isMethodDeclaration(m) && m.name.getText(guiSource) === 'renderResultHand');
const readiness = gui.members.find(m => ts.isMethodDeclaration(m) && m.name.getText(guiSource) === 'renderResultReadiness');
const renderSource = [renderer, handRenderer, readiness].map(method =>
  ts.createPrinter().printNode(ts.EmitHint.Unspecified, method, guiSource)).join('\n');
const nodes = new Map();
const en = JSON.parse(fs.readFileSync(path.join(root, 'src/i18n/en.json')));
function fixtureClass(catalog = en, warnings = []) {
  return runInNewContext(javascript('class RenderFixture {' + renderSource + '}\nRenderFixture;'), {
    document: { createElement: () => new Element(), getElementById: id => nodes.get(id) ?? null },
    t: (key, params = {}) => (catalog[key] ?? key).replace(/\{(\w+)\}/g, (whole, name) => String(params[name] ?? whole)),
    console: { warn: message => warnings.push(message) },
  });
}
const RenderFixture = fixtureClass();

function resultUi(catalog = en, warnings = []) {
  const Fixture = fixtureClass(catalog, warnings);
  const ui = new Fixture();
  ui.elements = Object.fromEntries(['resultHeadline', 'resultWinner', 'resultScoreBody', 'resultHand'].map(key => [key, new Element()]));
  ui.nickForSeat = seat => seat === 0 ? 'Human One' : 'Bot ' + seat;
  ui.renderResultWinTypeBadge = ui.renderResultPatternChips = ui.renderResultScoreBreakdown = () => {};
  return ui;
}

function readinessUi() {
  nodes.clear();
  for (const id of ['result-dialog-status', 'result-dialog-error', 'result-refresh']) nodes.set(id, new Element());
  const ui = new RenderFixture();
  ui.elements = { resultNext: new Element(), resultModal: new Element() };
  ui.nickForSeat = seat => seat === 3 ? 'Human Three' : 'Human One';
  return ui;
}
const presentation = mode => ({ identity: 'hand-identity', visible: true, mode, waitingSeats: [3], error: null });

test('actual readiness DOM labels required Continue and observer Dismiss distinctly', () => {
  const ui = readinessUi();
  ui.renderResultReadiness(presentation('continue'));
  assert.equal(ui.elements.resultNext.textContent, 'Continue');
  assert.equal(ui.elements.resultNext.disabled, false);
  assert.match(nodes.get('result-dialog-status').textContent, /waits for every human/);
  ui.renderResultReadiness(presentation('observer'));
  assert.equal(ui.elements.resultNext.textContent, 'Dismiss');
  assert.equal(ui.elements.resultNext.disabled, false);
});

test('actual readiness DOM disables pending/acknowledged owners and names the remaining players', () => {
  const ui = readinessUi();
  for (const mode of ['pending', 'waiting', 'advancing']) {
    ui.renderResultReadiness(presentation(mode));
    assert.equal(ui.elements.resultNext.disabled, true);
    assert.equal(ui.elements.resultNext.attributes['aria-busy'], String(mode === 'pending'));
    if (mode === 'waiting') assert.match(nodes.get('result-dialog-status').textContent, /Waiting.*Human Three/);
  }
});

test('actual readiness DOM gives reconnect/invalid-data recovery without enabling Continue', () => {
  const ui = readinessUi();
  for (const mode of ['reconnecting', 'invalid']) {
    ui.renderResultReadiness(presentation(mode));
    assert.equal(ui.elements.resultNext.disabled, true);
    assert.equal(nodes.get('result-refresh').hidden, false);
    assert.equal(nodes.get('result-refresh').textContent, 'Refresh status');
  }
});

test('actual readiness DOM shows the exact rejection, retaining the result for an explicit retry', () => {
  const ui = readinessUi();
  ui.renderResultReadiness({ ...presentation('continue'), error: 'stale-hand-result' });
  assert.equal(nodes.get('result-dialog-error').hidden, false);
  assert.match(nodes.get('result-dialog-error').textContent, /stale-hand-result/);
  assert.equal(ui.elements.resultNext.disabled, false);
  assert.equal(ui.elements.resultModal.dataset.continuationState, 'continue');
  ui.renderResultReadiness(presentation('pending'));
  assert.equal(nodes.get('result-dialog-error').hidden, true);
});

for (const [description, type, winner, expected] of [
  ['human win', 'Hu', 0, 'Human One 赢了 wins!'],
  ['bot win', 'Hu', 2, 'Bot 2 赢了 wins!'],
  ['draw', 'Draw', -1, 'No winner this hand'],
  ['false win', 'ZhaHu', 0, 'Human One declared a false win'],
]) {
  test(`actual result renderer retains readable winner and authoritative score rows for ${description}`, () => {
    const ui = resultUi();
    const score = Object.freeze([
      Object.freeze({ seat: 2, delta: 12 }), Object.freeze({ seat: 0, delta: -4 }),
      Object.freeze({ seat: 3, delta: -4 }), Object.freeze({ seat: 1, delta: -4 }),
    ]);
    const payload = Object.freeze({ type, winner, score, hand: Object.freeze([]), nextBanker: 2 });
    ui.renderResult(payload);
    assert.equal(ui.elements.resultWinner.textContent, expected);
    assert.equal(ui.elements.resultScoreBody.children.length, 4);
    assert.deepEqual(ui.elements.resultScoreBody.children.map(row => row.children[0].textContent), ['0', '1', '2', '3']);
    assert.deepEqual(ui.elements.resultScoreBody.children.map(row => row.children[2].textContent), ['-4', '-4', '+12', '-4']);
    assert.deepEqual(score.map(row => row.seat), [2, 0, 3, 1]);
  });
}

  test('actual result renderer maps all 108 physical IDs to original DOM label cards and localized face names', () => {
    const ui = resultUi();
    const hand = Object.freeze(Array.from({ length: 108 }, (_, id) => id));
    ui.renderResult({ type: 'Hu', winner: 0, score: [], hand, nextBanker: 0 });
    assert.equal(ui.elements.resultHand.children.length, 108);
    for (let face = 0; face < 27; face++) {
      for (let copy = 0; copy < 4; copy++) {
        const id = face * 4 + copy;
        const cell = ui.elements.resultHand.children[id];
        const label = `${face % 9 + 1} ${['characters', 'circles', 'bamboo'][Math.floor(face / 9)]}`;
        assert.equal(cell.dataset.tileId, String(id));
        assert.equal(cell.dataset.face, String(face));
        assert.equal(cell.attributes.role, 'img');
        assert.equal(cell.attributes['aria-label'], label);
        assert.equal(cell.title, label);
        assert.equal(cell.textContent, label, 'original label art must remain readable without an inherited atlas');
        assert.equal(cell.style.backgroundPosition, undefined);
        assert.equal(cell.attributes.tabindex, undefined);
        assert.equal(cell.onclick, undefined);
      }
    }
  });

  test('actual result renderer preserves unsorted authoritative order, all kong copies, and repeated entries', () => {
    const ui = resultUi();
    const hand = Object.freeze([107, 36, 0, 1, 2, 3, 72, 35, 71, 104, 16, 17, 18, 19, 107]);
    const result = Object.freeze({ type: 'Hu', winner: 2, score: [], hand, nextBanker: 2 });
    ui.renderResult(result);
    const expected = hand.map(String);
    assert.deepEqual(ui.elements.resultHand.children.map(cell => cell.dataset.tileId), expected);
    ui.renderResult(result);
    assert.deepEqual(ui.elements.resultHand.children.map(cell => cell.dataset.tileId), expected);
    assert.equal(ui.elements.resultHand.children.length, 15);
  });

  test('invalid physical IDs and opaque handles remain unknown instead of wrapping or exposing an invented face', () => {
    const warnings = [];
    const ui = resultUi(en, warnings);
    const invalid = [-1, 108, 135, 1000, 0.5, NaN, Infinity, -Infinity, '0', 'h_opaque', null, undefined, {}];
    ui.renderResult({ type: 'Hu', winner: 0, score: [], hand: [...invalid, 107], nextBanker: 0 });
    assert.equal(ui.elements.resultHand.children.length, invalid.length + 1);
    for (const cell of ui.elements.resultHand.children.slice(0, -1)) {
      assert.equal(cell.className, 'result-tile result-tile-unknown');
      assert.equal(cell.textContent, '?');
      assert.equal(cell.attributes['aria-label'], 'Unknown tile');
      assert.equal(cell.dataset.face, undefined);
      assert.equal(cell.dataset.tileId, undefined);
      assert.equal(cell.style.backgroundPosition, undefined);
    }
    assert.equal(warnings.length, invalid.length);
    assert.equal(ui.elements.resultHand.children.at(-1).attributes['aria-label'], '9 bamboo');
  });

  for (const [locale, expected] of [
    ['en', ['1 characters', '9 characters', '1 circles', '9 circles', '1 bamboo', '9 bamboo']],
    ['zh-Hans', ['1万', '9万', '1筒', '9筒', '1条', '9条']],
    ['zh-Hant', ['1萬', '9萬', '1筒', '9筒', '1條', '9條']],
  ]) {
    test(`actual result artwork has localized accessible names across every suit boundary (${locale})`, () => {
      const catalog = JSON.parse(fs.readFileSync(path.join(root, `src/i18n/${locale}.json`)));
      const ui = resultUi(catalog);
      ui.renderResult({ type: 'Hu', winner: 0, score: [], hand: [0, 35, 36, 71, 72, 107], nextBanker: 0 });
      assert.deepEqual(ui.elements.resultHand.children.map(cell => cell.attributes['aria-label']), expected);
    });
  }

  test('empty Hu, Draw and ZhaHu results clear prior artwork without retaining stale faces or changing score data', () => {
    const ui = resultUi();
    const score = Object.freeze([Object.freeze({ seat: 0, delta: 8 })]);
    for (const type of ['Hu', 'Draw', 'ZhaHu']) {
      ui.renderResult({ type: 'Hu', winner: 0, score, hand: [0, 36, 72], nextBanker: 0 });
      assert.equal(ui.elements.resultHand.children.length, 3);
      ui.renderResult({ type, winner: type === 'Draw' ? -1 : 0, score, hand: [], nextBanker: 0 });
      assert.equal(ui.elements.resultHand.children.length, 0);
      assert.equal(ui.elements.resultScoreBody.children[0].children[2].textContent, '+8');
    }
  });

function transport() {
  const sockets = [];
  class Socket {
    static OPEN = 1;
    static CLOSED = 3;
    static CLOSING = 2;
    readyState = 0;
    sent = [];
    constructor() { sockets.push(this); }
    send(message) { this.sent.push(JSON.parse(message)); }
    open() { this.readyState = 1; this.onopen?.(); }
    receive(message) { this.onmessage?.({ data: JSON.stringify(message) }); }
    close() { this.readyState = 3; this.onclose?.(); }
  }
  const modules = {
    events: { EventEmitter },
    './reconnect': { clearSession() {}, saveSession() {} },
    './profile': { loadProfile: async () => ({ displayName: 'Human One' }), refreshProfile: async () => {},
      snapshotStatsForGame() {}, onProfile() {}, initProfileHubBindings() {} },
    './hub': { getHubConnection: async () => ({}) },
    './game-state': { clearGameState() {}, refreshGameState: async () => {}, setGameRoomConnected() {} },
  };
  const load = name => {
    const module = { exports: {} };
    runInNewContext(javascript(fs.readFileSync(path.join(root, 'src', name), 'utf8')), {
      module, exports: module.exports,
      require: id => {
        if (!(id in modules)) throw new Error('Unexpected source dependency: ' + id);
        return modules[id];
      },
      window: { location: { href: 'http://result.test/autotable/?variant=changsha' } },
      URL, URLSearchParams, WebSocket: Socket, setInterval: () => 1, clearInterval() {},
      console: { log() {}, error() {} },
    });
    return module.exports;
  };
  modules['./base-client'] = load('base-client.ts');
  const { Client } = load('client.ts');
  const client = new Client('changsha');
  const connect = (seat = 0) => {
    client.join('ws://result.test/autotable/ws?variant=changsha', 'browser-room-alias');
    const socket = sockets.at(-1);
    socket.open();
    socket.receive({ type: 'JOINED', gameId: 'browser-room-alias', playerId: 'signed-player', isFirst: true,
      viewer: { roomId: 'browser-room-alias', revision: 1, seat } });
    return socket;
  };
  return { client, connect };
}

test('actual Client collection emits only the frozen UPDATE/handResultAck envelope', () => {
  const h = transport();
  const socket = h.connect();
  assert.equal(socket.sent.flatMap(frame => frame.entries ?? []).some(([kind]) => kind === 'handResultAck'), false);
  const command = { gameId: 'canonical-runtime-guid', handNumber: 1, resultToken: 'opaque-token' };
  h.client.handResultAck.set('current', command);
  assert.deepEqual(socket.sent.at(-1), { type: 'UPDATE', entries: [['handResultAck', 'current', command]], full: false });
});

test('actual Client reconnect and full snapshots never replay or invent an acknowledgement', () => {
  const h = transport();
  const first = h.connect();
  h.client.handResultAck.set('current', { gameId: 'canonical-runtime-guid', handNumber: 1, resultToken: 'old-token' });
  h.client.disconnect();
  // Even a local disconnected cache write cannot turn into a reconnect command.
  h.client.handResultAck.set('current', { gameId: 'canonical-runtime-guid', handNumber: 1, resultToken: 'cached-token' });
  const second = h.connect();
  second.receive({ type: 'UPDATE', full: true, viewer: { roomId: 'browser-room-alias', revision: 1, seat: 0 },
    entries: [['result', 'current', { winner: 2, type: 'Hu', score: [], hand: [], nextBanker: 2,
      continuation: { gameId: 'canonical-runtime-guid', handNumber: 1, resultToken: 'held-token',
        requiredSeats: [0], acknowledgedSeats: [], waitingSeats: [0] } }]] });
  assert.equal(first.sent.flatMap(frame => frame.entries ?? []).filter(([kind]) => kind === 'handResultAck').length, 1);
  assert.equal(second.sent.flatMap(frame => frame.entries ?? []).filter(([kind]) => kind === 'handResultAck').length, 0);
  assert.equal(h.client.result.get('current').continuation.resultToken, 'held-token');
});

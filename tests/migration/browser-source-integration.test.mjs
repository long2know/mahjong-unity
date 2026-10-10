import test from 'node:test';
import assert from 'node:assert/strict';
import { sourceLoader } from '../../src/frontend/autotable-src/tests/node/unity-test-support.mjs';

class Element extends EventTarget {
  constructor(tag) {
    super();
    this.tagName = tag.toUpperCase();
    this.children = [];
    this.attributes = {};
    this.dataset = {};
    this.style = { setProperty() {} };
    this.classList = { add() {} };
    this.rect = { width: 1280, height: 800 };
    this.checked = false;
  }
  append(...children) { children.forEach(child => this.appendChild(child)); }
  appendChild(child) { this.children.push(child); child.parent = this; return child; }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); }
  setAttribute(key, value) { this.attributes[key] = String(value); }
  getAttribute(key) { return this.attributes[key] ?? null; }
  removeAttribute(key) { delete this.attributes[key]; }
  getBoundingClientRect() { return this.rect; }
}

function actualGame({ advanceAnimations = false } = {}) {
  const body = new Element('body');
  const find = (element, id) => element.id === id ? element
    : element.children.map(child => find(child, id)).find(Boolean);
  const document = Object.assign(new EventTarget(), {
    body, head: new Element('head'), hidden: false, activeElement: null,
    documentElement: new Element('html'),
    createElement: tag => new Element(tag),
    getElementById: id => find(body, id),
  });
  for (const id of ['main', 'loading', 'perspective', 'tile-labels', 'benchmark', 'muted', 'sticky', 'unity-notice']) {
    const element = new Element(id === 'main' ? 'main' : 'input');
    element.id = id;
    element.checked = id === 'perspective';
    body.append(element);
  }
  const window = Object.assign(new EventTarget(), {
    location: new URL('http://localhost/autotable/'),
    innerWidth: 1280, innerHeight: 800, devicePixelRatio: 2,
    visualViewport: Object.assign(new EventTarget(), { height: 800, offsetTop: 0 }),
    localStorage: { getItem: () => null, setItem() {} },
    matchMedia: () => new EventTarget(),
  });
  // Browser services are unrelated to this geometry gate; Game, World, Setup,
  // frame projection, presentation selection, and UnityHost are actual source.
  const loader = sourceLoader({
    document, window,
    ResizeObserver: class { observe() {} disconnect() {} },
    getComputedStyle: () => ({
      paddingTop: '0px', paddingRight: '0px', paddingBottom: '0px', paddingLeft: '0px',
    }),
    requestAnimationFrame: () => 1, cancelAnimationFrame() {},
    setInterval: () => 1, clearInterval() {},
    console: { log() {}, warn() {}, error() {} },
  }, {
    'sound-player.ts': { SoundPlayer: class { play() {} } },
    'settings-drawer.ts': {
      getSettings: () => ({ boardLayout: 'auto', handLayout: 'auto', handSort: 'suit', soundEnabled: true }),
      installPerspectiveSetting() {}, installRendererLayoutSettings() {},
      onSettingsChange: () => () => {},
    },
    'profile.ts': {
      loadProfile: async () => ({ displayName: 'Source test player' }), refreshProfile: async () => {},
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
  loader.load('client-ui.ts').ClientUi = class { start() {} };
  const { Game } = loader.load('game.ts');
  const game = new Game();
  const { Conditions, GameType } = loader.load('types.ts');
  game.client.match.set(0, {
    dealer: 0, honba: 0, conditions: Conditions.defaultsFor(GameType.FOUR_PLAYER),
  });
  const publications = [];
  const publish = game.renderer.publish.bind(game.renderer);
  game.renderer.publish = (layout, frame) => {
    publications.push(frame);
    publish(layout, frame);
  };
  if (advanceAnimations) {
    game.mainLoop(0);
    game.updateSettings();
  } else {
    game.start();
  }
  return {
    game, window, publications,
    resize(width, height) {
      document.getElementById('main').rect = { width, height };
      window.innerWidth = width;
      window.innerHeight = height;
      window.visualViewport.height = height;
      window.dispatchEvent(new Event('resize'));
    },
  };
}

test('actual Game first settings publication is valid before the first animation tick', async t => {
  const h = actualGame();
  t.after(() => h.game.dispose());
  assert(h.publications.length > 0, h.game.renderer.description.textContent);
  assert(h.publications[0].presentation.lookDown >= 0);
  assert(h.publications[0].presentation.lookDown <= 1);
});

test('actual Game auto-layout resize leaves the matching separate-hand viewport, not a superseded outer measurement', async t => {
  const h = actualGame({ advanceAnimations: true });
  t.after(() => h.game.dispose());
  assert.notEqual(h.game.adapter.latest, null, h.game.renderer.description.textContent);
  assert.equal(h.game.adapter.latest.presentation.handLayout, 'in-table');
  assert.equal(h.game.renderer.viewport.handBandCssPx, 0);
  const transmitted = [];
  // Only the receiver boundary is simulated; every measurement, hook, and
  // outbound DTO is produced by the actual Game/UnityHost implementation.
  h.game.renderer.instance = {
    SendMessage(receiver, method, json) {
      assert.equal(receiver, 'MahjongBridge');
      if (method === 'SetViewport') transmitted.push(JSON.parse(json));
    },
    Quit: async () => {},
  };
  h.game.renderer.handshake = true;
  const before = h.game.renderer.viewport.resizeRevision;
  h.resize(390, 844);
  assert.equal(h.game.adapter.latest.presentation.boardLayout, 'rectangular');
  assert.equal(h.game.adapter.latest.presentation.handLayout, 'separate');
  assert(h.game.renderer.viewport.resizeRevision > before);
  assert(h.game.renderer.viewport.handBandCssPx > 0,
    'Actual Game resize hook re-enters UnityHost measurement; the newest separate-hand measurement must win');
  assert.equal(transmitted.length, 1, 'A superseded outer measurement must not be transmitted');
  assert.equal(transmitted[0].handBandCssPx, h.game.renderer.viewport.handBandCssPx);
  assert.equal(transmitted[0].resizeRevision, h.game.renderer.viewport.resizeRevision);
  const portrait = h.game.renderer.viewport.resizeRevision;
  h.resize(844, 390);
  assert.equal(h.game.adapter.latest.presentation.handLayout, 'in-table');
  assert.equal(h.game.renderer.viewport.handBandCssPx, 0);
  assert(h.game.renderer.viewport.resizeRevision > portrait);
  assert.equal(transmitted.length, 2);
  assert(transmitted[1].resizeRevision > transmitted[0].resizeRevision);
  assert.equal(transmitted[1].handBandCssPx, 0);
  assert.equal(transmitted[1].widthPx, Math.round(844 * transmitted[1].renderPixelRatio));
  assert.equal(transmitted[1].heightPx, Math.round(390 * transmitted[1].renderPixelRatio));
  h.resize(844, 390);
  assert.equal(transmitted.length, 2, 'An unchanged resize must not duplicate a viewport revision');
});

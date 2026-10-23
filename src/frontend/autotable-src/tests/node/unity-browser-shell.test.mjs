import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { runInNewContext } from 'node:vm';
import { root, require, sourceLoader } from './unity-test-support.mjs';

const ts = require('typescript');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const catalogs = Object.fromEntries(['en', 'zh-Hans', 'zh-Hant'].map(locale =>
  [locale, JSON.parse(fs.readFileSync(path.join(root, `src/i18n/${locale}.json`), 'utf8'))]));
const javascript = source => ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
}).outputText;

class Element extends EventTarget {
  constructor(tag, document) {
    super();
    this.tagName = tag.toUpperCase();
    this.document = document;
    this.children = [];
    this.attributes = new Map();
    this.classes = new Set();
    this.classList = {
      add: (...names) => names.forEach(name => this.classes.add(name)),
      remove: (...names) => names.forEach(name => this.classes.delete(name)),
      contains: name => this.classes.has(name),
      toggle: (name, on = !this.classes.has(name)) => {
        if (on) this.classes.add(name); else this.classes.delete(name);
        return on;
      },
    };
    this.hidden = false;
    this.inert = false;
    this.disabled = false;
    this.tabIndex = /^(BUTTON|INPUT|SELECT|TEXTAREA)$/.test(this.tagName) ? 0 : -1;
    this.rect = { height: 86 };
    this.properties = new Map();
    this.writes = 0;
    this.style = {
      setProperty: (name, value) => { this.properties.set(name, value); this.writes++; },
      getPropertyValue: name => this.properties.get(name) ?? '',
    };
  }
  set className(value) { this.classes = new Set(value.split(/\s+/).filter(Boolean)); }
  get className() { return [...this.classes].join(' '); }
  set textContent(value) { this.text = String(value); this.children = []; }
  get textContent() { return (this.text ?? '') + this.children.map(child => child.textContent).join(''); }
  setAttribute(name, value) {
    this.attributes.set(name, String(value));
    if (name === 'tabindex') this.tabIndex = Number(value);
  }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  removeAttribute(name) { this.attributes.delete(name); }
  append(...children) { children.forEach(child => this.appendChild(child)); }
  appendChild(child) {
    if (child.parentElement) child.parentElement.children = child.parentElement.children.filter(item => item !== child);
    child.parentElement = this;
    this.children.push(child);
    return child;
  }
  replaceChildren(...children) {
    this.children.forEach(child => { child.parentElement = null; });
    this.children = [];
    this.text = '';
    this.append(...children);
  }
  contains(node) { return this === node || this.children.some(child => child.contains(node)); }
  focus() { if (!this.closest('[hidden], [inert]')) this.document.activeElement = this; }
  matches(selector) { return selector === ':disabled' && this.disabled; }
  closest() {
    for (let node = this; node; node = node.parentElement) if (node.hidden || node.inert) return node;
    return null;
  }
  querySelectorAll() {
    return this.children.flatMap(child => [child, ...child.querySelectorAll()])
      .filter(child => /^(BUTTON|INPUT|SELECT|TEXTAREA|A)$/.test(child.tagName) || child.attributes.has('tabindex'));
  }
  getClientRects() { return this.hidden ? [] : [this.rect]; }
  getBoundingClientRect() { return this.rect; }
}

function fixture(missing) {
  const document = Object.assign(new EventTarget(), { activeElement: null });
  document.body = new Element('body', document);
  document.documentElement = new Element('html', document);
  document.createElement = tag => new Element(tag, document);
  const all = node => [node, ...node.children.flatMap(all)];
  document.getElementById = id => all(document.body).find(node => node.id === id) ?? null;
  // Required controls come from the shipped markup, not a second inventory of
  // invented IDs. Unrelated hierarchy is omitted by this browser-free harness.
  for (const match of html.matchAll(/<([a-z][\w-]*)\b([^>]*\bid="([^"]+)"[^>]*)>/gi)) {
    if (match[3] === missing) continue;
    const node = document.createElement(match[1]);
    node.id = match[3];
    for (const attribute of match[2].matchAll(/([\w-]+)="([^"]*)"/g)) node.setAttribute(attribute[1], attribute[2]);
    node.hidden = /\shidden(?:\s|$)/.test(match[2]);
    document.body.append(node);
  }
  const observers = [], languages = [];
  let locale = 'en';
  const loader = sourceLoader({
    document,
    ResizeObserver: class {
      constructor(callback) { this.callback = callback; observers.push(this); }
      observe(node) { this.node = node; }
    },
  }, {
    'i18n.ts': {
      getActiveLocale: () => locale,
      t: key => catalogs[locale][key] ?? catalogs.en[key] ?? key,
      onLanguageChange: callback => languages.push(callback),
    },
  });
  return {
    document, observers, loader,
    node: id => document.getElementById(id),
    language: next => { locale = next; languages.forEach(callback => callback(next)); },
    languages,
  };
}

function declarations(file, names, classMethods = false) {
  const source = ts.createSourceFile(file, fs.readFileSync(path.join(root, 'src', file), 'utf8'), ts.ScriptTarget.Latest, true);
  const nodes = classMethods
    ? source.statements.find(node => ts.isClassDeclaration(node) && node.members.some(member =>
      names.includes(member.name?.getText(source)))).members
    : source.statements;
  return nodes.filter(node => names.includes(node.name?.getText(source)))
    .map(node => ts.createPrinter().printNode(ts.EmitHint.Unspecified, node, source)).join('\n');
}

function key(name, shiftKey = false) {
  return Object.assign(new Event('keydown', { cancelable: true }), { key: name, shiftKey });
}

test('the production shell reparents the original controls without dropping their actions, state or IDs', () => {
  const h = fixture();
  const controls = ['new-game', 'lobby-toggle', 'settings-toggle', 'settings-button', 'move-log-toggle'];
  const original = controls.map(h.node);
  let activations = 0;
  h.node('new-game').addEventListener('click', () => activations++);
  h.node('new-game').disabled = true;
  const { installBrowserShell } = h.loader.load('browser-shell.ts');
  assert.equal(installBrowserShell(), true);
  assert.deepEqual(h.node('browser-shell').children[0].children, original);
  assert.equal(h.node('new-game').getAttribute('data-action'), 'new-game');
  assert.equal(h.node('new-game').disabled, true);
  h.node('new-game').disabled = false;
  h.node('new-game').dispatchEvent(new Event('click'));
  assert.equal(activations, 1);
  assert.equal(h.node('turn-banner').getAttribute('role'), 'status');
  assert.equal(h.node('turn-banner').getAttribute('aria-live'), 'polite');
  assert.equal(h.node('chat-panel').parentElement, h.node('move-log').parentElement);
  assert.equal(h.node('lobby-toggle').getAttribute('aria-controls'), 'lobby-panel');
  assert.equal(h.node('settings-drawer').inert, true, 'closed controls must be inert even before their lazy controller loads');
  assert.equal(h.node('settings-drawer-v2').inert, true);
  assert.equal(h.node('pickup-hud').parentElement.id, 'browser-action-slot');
  assert.equal(h.node('own-turn-actions').parentElement.id, 'browser-action-slot');
});

test('mounting is idempotent and resize observation only writes a changed measured height', () => {
  const h = fixture();
  const api = h.loader.load('browser-shell.ts');
  api.installBrowserShell();
  const shell = h.node('browser-shell'), style = h.document.documentElement.style;
  api.installBrowserShell();
  assert.equal(h.node('browser-shell'), shell);
  assert.equal(h.observers.length, 1);
  assert.equal(h.languages.length, 1);
  assert.equal(style.getPropertyValue('--browser-shell-height'), '86px');
  const writes = h.document.documentElement.writes;
  h.observers[0].callback();
  assert.equal(h.document.documentElement.writes, writes, 'an unchanged observation must not create a mutation/resize feedback loop');
  shell.rect.height = 98.25;
  h.observers[0].callback();
  assert.equal(style.getPropertyValue('--browser-shell-height'), '99px');
  assert.equal(h.document.documentElement.writes, writes + 1);
});

test('a missing production control leaves the original shell untouched rather than partially mounting it', () => {
  for (const id of ['new-game', 'lobby-toggle', 'settings-toggle', 'settings-button', 'move-log-toggle',
    'variant-badge', 'turn-banner', 'chat-panel', 'move-log']) {
    const h = fixture(id), original = [...h.document.body.children];
    assert.equal(h.loader.load('browser-shell.ts').installBrowserShell(), false, id);
    assert.deepEqual(h.document.body.children, original, id);
    assert.equal(h.document.body.classList.contains('browser-shell-active'), false, id);
    assert.equal(h.observers.length, 0, id);
  }
});

test('both settings actions and the move log retain visible, distinct labels after each language change', () => {
  const h = fixture();
  h.loader.load('browser-shell.ts').installBrowserShell();
  for (const locale of ['en', 'zh-Hans', 'zh-Hant']) {
    h.language(locale);
    const short = id => h.node(id).children[1].textContent;
    assert.notEqual(short('settings-toggle'), short('settings-button'), locale);
    for (const id of ['lobby-toggle', 'settings-toggle', 'settings-button', 'move-log-toggle']) {
      assert(h.node(id).children.every(child => child.textContent.length > 0), `${locale}: ${id}`);
      assert(!h.node(id).textContent.includes('⚙'), `${locale}: no competing icon-only settings`);
    }
    assert.equal(h.node('move-log-toggle').getAttribute('aria-label'), catalogs[locale]['replay.move_log']);
  }
});

test('the actual claim overlay attachment joins the same action stack, or keeps its original body fallback without a game shell', () => {
  const method = declarations('ui/claim-window-overlay.ts', ['attach'], true);
  for (const withShell of [true, false]) {
    const h = fixture();
    if (withShell) h.loader.load('browser-shell.ts').installBrowserShell();
    let subscriptions = 0, bindings = 0, syncs = 0;
    const Fixture = runInNewContext(javascript(`class Fixture { ${method} }\nFixture;`), {
      document: h.document,
      getRuleActionControls: () => ({
        onChange: () => { subscriptions++; return () => {}; },
      }),
    });
    const overlay = new Fixture();
    overlay.root = null;
    overlay.game = { client: {} };
    overlay.buildDom = () => h.document.createElement('div');
    overlay.bindKeyboard = () => bindings++;
    overlay.syncFromCollection = () => syncs++;
    overlay.attach();
    assert.equal(overlay.root.parentElement, withShell ? h.node('browser-action-slot') : h.document.body);
    overlay.attach();
    assert.equal(subscriptions, 1);
    assert.equal(bindings, 1);
    assert.equal(syncs, 1);
  }
});

test('closing a real drawer makes its descendants inert and restores focus only when focus was inside it', () => {
  const h = fixture();
  const { setDrawerAccessibility } = h.loader.load('ui/drawer-accessibility.ts');
  const drawer = h.node('settings-drawer'), toggle = h.node('settings-toggle'), close = h.node('settings-close');
  drawer.append(close);
  setDrawerAccessibility(drawer, toggle, true);
  close.focus();
  setDrawerAccessibility(drawer, toggle, false);
  assert.equal(drawer.inert, true);
  assert.equal(drawer.getAttribute('aria-hidden'), 'true');
  assert.equal(toggle.getAttribute('aria-expanded'), 'false');
  assert.equal(h.document.activeElement, toggle);
  close.focus();
  assert.equal(h.document.activeElement, toggle, 'an offscreen input must not take focus');
  setDrawerAccessibility(drawer, toggle, true);
  const outside = h.node('new-game');
  outside.focus();
  setDrawerAccessibility(drawer, toggle, false);
  assert.equal(h.document.activeElement, outside, 'outside-click dismissal must not steal the clicked control’s focus');
});

test('closed drawers and Escape already handled by a nested widget cannot consume keyboard input', () => {
  const h = fixture();
  const { handleDrawerKey, setDrawerAccessibility } = h.loader.load('ui/drawer-accessibility.ts');
  const drawer = h.node('settings-drawer'), toggle = h.node('settings-toggle');
  let closes = 0;
  setDrawerAccessibility(drawer, toggle, false);
  const closed = key('Escape');
  handleDrawerKey(closed, drawer, () => closes++);
  assert.equal(closes, 0, 'a closed drawer must not handle Escape');
  assert.equal(closed.defaultPrevented, false);
  setDrawerAccessibility(drawer, toggle, true);
  const nested = key('Escape');
  nested.preventDefault();
  handleDrawerKey(nested, drawer, () => closes++);
  assert.equal(closes, 0, 'a listbox’s Escape must not also dismiss its drawer');
  const open = key('Escape');
  handleDrawerKey(open, drawer, () => closes++);
  assert.equal(closes, 1);
  assert.equal(open.defaultPrevented, true);
});

test('Tab wraps inside the open drawer, skips disabled/hidden/inert controls, and does not intercept interior movement', () => {
  const h = fixture();
  const { handleDrawerKey, setDrawerAccessibility } = h.loader.load('ui/drawer-accessibility.ts');
  const drawer = h.node('settings-drawer'), toggle = h.node('settings-toggle');
  const [first, middle, last, disabled, hidden] = Array.from({ length: 5 }, () => h.document.createElement('button'));
  disabled.disabled = true;
  hidden.hidden = true;
  drawer.append(first, disabled, hidden, middle, last);
  setDrawerAccessibility(drawer, toggle, true);
  last.focus();
  const forward = key('Tab');
  handleDrawerKey(forward, drawer, () => assert.fail('Tab must not close'));
  assert.equal(forward.defaultPrevented, true);
  assert.equal(h.document.activeElement, first);
  const backward = key('Tab', true);
  handleDrawerKey(backward, drawer, () => {});
  assert.equal(h.document.activeElement, last);
  middle.focus();
  const interior = key('Tab');
  handleDrawerKey(interior, drawer, () => {});
  assert.equal(interior.defaultPrevented, false);
  drawer.replaceChildren();
  const empty = key('Tab');
  handleDrawerKey(empty, drawer, () => {});
  assert.equal(empty.defaultPrevented, true);
  assert.equal(h.document.activeElement, drawer);
});

test('the actual per-game drawer controller wires inert state, expanded state, keyboard wrapping and Escape focus return', () => {
  const h = fixture(), document = h.document;
  const methods = declarations('game-ui.ts', ['setupSettingsDrawer', 'openSettingsDrawer', 'closeSettingsDrawer'], true);
  const helpers = h.loader.load('ui/drawer-accessibility.ts');
  const Fixture = runInNewContext(javascript(`class Fixture { ${methods} }\nFixture;`), {
    document, ...helpers, installLegacyDisplaySettings() {},
    readSettingsState: () => ({ botStrength: 'Hard', handCount: 4, autoDeal: false, sound: true }),
    Sound: { setMuted() {} },
  });
  const controller = new Fixture();
  controller.elements = Object.fromEntries([
    ['settingsToggle', 'settings-toggle'], ['settingsDrawer', 'settings-drawer'], ['settingsClose', 'settings-close'],
    ['settingsBotStrength', 'settings-bot-strength'], ['settingsHandCount', 'settings-hand-count'],
    ['settingsAutoDeal', 'settings-auto-deal'], ['settingsSound', 'settings-sound'], ['settingsApply', 'settings-apply'],
  ].map(([name, id]) => [name, h.node(id)]));
  const { settingsDrawer: drawer, settingsToggle: toggle, settingsClose: close, settingsApply: apply } = controller.elements;
  drawer.append(close, apply);
  controller.setupSettingsDrawer();
  assert.equal(drawer.inert, true);
  assert.equal(toggle.getAttribute('aria-controls'), drawer.id);
  toggle.onclick({ stopPropagation() {} });
  assert.equal(drawer.inert, false);
  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
  assert.equal(document.activeElement, close);
  apply.focus();
  document.dispatchEvent(key('Tab'));
  assert.equal(document.activeElement, close);
  document.dispatchEvent(key('Escape'));
  assert.equal(drawer.inert, true);
  assert.equal(toggle.getAttribute('aria-expanded'), 'false');
  assert.equal(document.activeElement, toggle);
});

test('the actual lazy application drawer installer uses the same guards and focuses Display immediately on a compact table', () => {
  const h = fixture(), document = h.document;
  const names = ['installSettingsDrawerV2', 'openDrawer', 'closeDrawer', 'activateTab'];
  const functions = declarations('settings-drawer.ts', names);
  const helpers = h.loader.load('ui/drawer-accessibility.ts');
  const drawer = h.node('settings-drawer-v2'), toggle = h.node('settings-button');
  drawer.append(h.node('settings-close-v2'), h.node('settings-drawer-v2-tabs'), h.node('settings-drawer-v2-panels'),
    h.node('settings-reset'), h.node('settings-save'));
  const tabs = ['general', 'audio', 'display', 'network', 'rule-presets'];
  const panel = id => {
    const element = document.createElement('div');
    element.id = `settings-panel-${id}`;
    return element;
  };
  const api = runInNewContext(javascript(`
    let installed = false, drawerOpened = false, activeTab = 'general';
    const current = {};
    const TABS = ${JSON.stringify(tabs.map(id => ({ id, labelKey: `settings.tab.${id}`, fallback: id })))};
    ${functions}
    ({ installSettingsDrawerV2, openDrawer, closeDrawer });
  `), {
    document, ...helpers, exports: {},
    window: { matchMedia: () => ({ matches: true }) },
    COMPACT_VIEW_QUERY: 'compact', t: key => catalogs.en[key] ?? key,
    installPerspectiveSetting() {}, ensureSettingsLoaded() {}, applyDerivedSettings() {}, emit() {},
    buildGeneralPanel: () => panel('general'), buildAudioPanel: () => panel('audio'),
    buildDisplayPanel: () => panel('display'), buildNetworkPanel: () => panel('network'),
    buildRulePresetsPanel: () => panel('rule-presets'),
    onProfile() {}, onLanguageChange() {},
  });
  api.installSettingsDrawerV2();
  assert.equal(drawer.inert, true);
  toggle.dispatchEvent(new Event('click'));
  assert.equal(drawer.inert, false);
  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
  assert.equal(document.activeElement.id, 'settings-tab-display');
  const nested = key('Escape');
  nested.preventDefault();
  document.dispatchEvent(nested);
  assert.equal(drawer.inert, false);
  document.dispatchEvent(key('Escape'));
  assert.equal(drawer.inert, true);
  assert.equal(document.activeElement, toggle);
});

test('the production game bootstrap mounts the existing shell before the scene can measure its chrome', async () => {
  const h = fixture();
  const { installBrowserShell } = h.loader.load('browser-shell.ts');
  const body = declarations('game-bootstrap.ts', ['bootstrapGame']);
  let scenes = 0;
  const bootstrap = runInNewContext(javascript(`let booted = false;\n${body}\nbootstrapGame;`), {
    installBrowserShell, exports: {},
    markShellReady: () => assert(h.node('browser-shell')),
    window: { location: { search: '' } },
    require: () => ({ mountScene: async () => {
      scenes++;
      assert.equal(h.node('settings-toggle').parentElement.className, 'browser-toolbar');
      return {};
    } }),
    shouldEnableVoice: () => false,
    showToast: () => assert.fail('bootstrap should succeed'),
    document: h.document,
  });
  await bootstrap();
  await bootstrap();
  assert.equal(scenes, 1);
  assert.equal(h.observers.length, 1);
});

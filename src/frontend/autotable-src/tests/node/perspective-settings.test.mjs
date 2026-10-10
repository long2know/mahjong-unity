import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { runInNewContext } from 'node:vm';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const require = createRequire(path.join(root, 'package.json'));
const ts = require('typescript');
const javascript = source => ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText;

class Input extends EventTarget {
  checked = true;
  value = 'suit';
  attributes = {};
  setAttribute(name, value) { this.attributes[name] = value; }
}

function settingsHarness(saved = {}, withCameraInput = true) {
  const storage = new Map([['mahjong.settings.v1', JSON.stringify(saved)]]);
  const native = new Input(), display = new Input(), legacy = new Input();
  const order = new Input(), legacyOrder = new Input();
  const nodes = new Map([
    ['settings-perspective-toggle', display], ['settings-game-perspective', legacy],
    ['settings-hand-sort', order], ['settings-game-hand-sort', legacyOrder],
    ['settings-table-display', {}],
  ]);
  if (withCameraInput) nodes.set('perspective', native);
  const document = {
    activeElement: null, getElementById: id => nodes.get(id) ?? null,
    body: { classList: { toggle: () => {} } },
    documentElement: { style: { setProperty: () => {} } },
  };
  let cameraUpdates = 0;
  native.addEventListener('change', () => { cameraUpdates++; });
  const module = { exports: {} };
  runInNewContext(javascript(fs.readFileSync(path.join(root, 'src/settings-drawer.ts'), 'utf8')), {
    module, exports: module.exports, document, Event,
    window: { localStorage: {
      getItem: key => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value),
    } },
    require: id => id === './hand-sort' ? { handSortMode: value => value === 'groups' ? 'groups' : 'suit' }
      : id === './mobile-overlay-policy' ? { mobileInfoPanel: value => ['chat', 'move-log'].includes(value) ? value : 'none' }
        : id === './i18n' ? { translateElements: () => {}, t: key => key, onLanguageChange: () => {} } : {},
  });
  return {
    settings: module.exports, native, display, legacy, order, legacyOrder, document, nodes,
    saved: () => JSON.parse(storage.get('mahjong.settings.v1')),
    cameraUpdates: () => cameraUpdates,
  };
}

test('saved flat view reaches the canonical camera input before the lazy drawer is mounted', () => {
  const h = settingsHarness({ perspective: false, handSort: 'groups', lang: 'zh-Hans' });
  h.settings.installPerspectiveSetting();
  assert.equal(h.native.checked, false);
  assert.equal(h.display.checked, false);
  assert.equal(h.cameraUpdates(), 1);
  h.settings.installPerspectiveSetting();
  assert.equal(h.cameraUpdates(), 1, 'binding is idempotent');
  assert.equal(h.settings.getSettings().perspective, false);
  assert.equal(h.saved().handSort, 'groups');
  assert.equal(h.saved().lang, 'zh-Hans');
});

test('drawer changes and legacy changes persist and synchronize without an event feedback loop', () => {
  const h = settingsHarness({ perspective: true, handSort: 'groups', lang: 'zh-Hant' });
  h.settings.installPerspectiveSetting();
  let changes = 0;
  h.settings.onSettingsChange(() => { changes++; });
  h.settings.setSettings({ perspective: false });
  assert.equal(h.native.checked, false);
  assert.equal(h.display.checked, false);
  assert.equal(h.saved().perspective, false);
  assert.equal(h.cameraUpdates(), 1);
  assert.equal(changes, 1);
  h.native.checked = true;
  h.native.dispatchEvent(new Event('change', { bubbles: true }));
  assert.equal(h.display.checked, true);
  assert.equal(h.settings.getSettings().perspective, true);
  assert.equal(h.saved().perspective, true);
  assert.equal(h.cameraUpdates(), 2);
  assert.equal(changes, 2);
  assert.equal(h.saved().handSort, 'groups');
  assert.equal(h.saved().lang, 'zh-Hant');
});

test('sorting does not reset a keyboard/legacy-selected view; resetting settings updates both controls', () => {
  const h = settingsHarness();
  h.settings.installPerspectiveSetting();
  h.native.checked = false;
  h.native.dispatchEvent(new Event('change'));
  h.settings.setSettings({ handSort: 'groups', mobileTableStatus: true });
  assert.equal(h.cameraUpdates(), 1);
  assert.equal(h.native.checked, false);
  assert.equal(h.display.checked, false);
  assert.equal(h.saved().perspective, false);
  h.settings.resetSettings();
  assert.equal(h.native.checked, true);
  assert.equal(h.display.checked, true);
  assert.equal(h.saved().perspective, true);
  assert.equal(h.cameraUpdates(), 2);
});

test('a missing camera input can be bound later without losing the saved view', () => {
  const h = settingsHarness({ perspective: false }, false);
  h.settings.installPerspectiveSetting();
  h.nodes.set('perspective', h.native);
  h.settings.installPerspectiveSetting();
  assert.equal(h.native.checked, false);
  assert.equal(h.cameraUpdates(), 1);
});

test('the original gear drawer restores and writes the same sorting/perspective preferences as Display', () => {
  const h = settingsHarness({ perspective: false, handSort: 'groups', lang: 'zh-Hans' });
  h.settings.installLegacyDisplaySettings();
  assert.equal(h.legacy.checked, false);
  assert.equal(h.native.checked, false);
  assert.equal(h.legacyOrder.value, 'groups');
  assert.equal(h.order.value, 'groups');
  let changes = 0;
  h.settings.onSettingsChange(() => { changes++; });
  h.settings.installLegacyDisplaySettings();
  assert.equal(changes, 0, 'no duplicate bindings or preference writes');
  h.legacyOrder.value = 'suit';
  h.legacyOrder.dispatchEvent(new Event('change'));
  assert.equal(h.order.value, 'suit');
  assert.equal(h.saved().handSort, 'suit');
  assert.equal(changes, 1);
  h.legacy.checked = true;
  h.legacy.dispatchEvent(new Event('change'));
  assert.equal(h.native.checked, true);
  assert.equal(h.display.checked, true);
  assert.equal(h.saved().perspective, true);
  assert.equal(changes, 2);
  h.settings.setSettings({ handSort: 'groups', perspective: false });
  assert.equal(h.legacyOrder.value, 'groups');
  assert.equal(h.legacy.checked, false);
  assert.equal(h.display.checked, false);
  assert.equal(changes, 3);
  assert.equal(h.saved().lang, 'zh-Hans');
});

test('the actual P shortcut takes the canonical change route and respects held keys and focused inputs', () => {
  const h = settingsHarness();
  h.settings.installPerspectiveSetting();
  const source = ts.createSourceFile('game.ts', fs.readFileSync(path.join(root, 'src/game.ts'), 'utf8'), ts.ScriptTarget.Latest, true);
  const game = source.statements.find(s => ts.isClassDeclaration(s) && s.name?.text === 'Game');
  const methods = game.members.filter(m => ts.isMethodDeclaration(m) && ['onKeyDown', 'onKeyUp'].includes(m.name.getText(source)));
  const body = methods.map(m => ts.createPrinter().printNode(ts.EmitHint.Unspecified, m, source)).join('\n');
  const Fixture = runInNewContext(javascript(`class Fixture { ${body} }\nFixture;`), { document: h.document, Event });
  const fixture = new Fixture();
  fixture.keys = new Set();
  fixture.settings = { perspective: h.native };
  fixture.onKeyDown({ key: 'p' });
  assert.equal(h.settings.getSettings().perspective, false);
  assert.equal(h.display.checked, false);
  assert.equal(h.saved().perspective, false);
  fixture.onKeyDown({ key: 'p' });
  assert.equal(h.cameraUpdates(), 1);
  fixture.onKeyUp({ key: 'p' });
  fixture.onKeyDown({ key: 'p' });
  assert.equal(h.saved().perspective, true);
  assert.equal(h.display.checked, true);
  assert.equal(h.cameraUpdates(), 2);
  fixture.onKeyUp({ key: 'p' });
  for (const tagName of ['INPUT', 'TEXTAREA', 'SELECT']) {
    h.document.activeElement = { tagName };
    for (const key of ['p', 'q', 'z', ' ']) fixture.onKeyDown({ key });
    assert.equal(h.cameraUpdates(), 2, 'typing in chat/settings must not invoke game shortcuts');
  }
  h.document.activeElement = { tagName: 'BUTTON' };
  fixture.onKeyDown({ key: ' ' });
  assert.equal(fixture.keys.has(' '), false, 'a native button press must not start camera look-down');
});

test('all shipped languages label the checkbox, its explanation and the existing settings gear', () => {
  for (const locale of ['en', 'zh-Hans', 'zh-Hant']) {
    const catalog = JSON.parse(fs.readFileSync(path.join(root, `src/i18n/${locale}.json`), 'utf8'));
    for (const key of ['settings.open_display', 'settings.perspective', 'settings.perspective_hint', 'chat.open', 'chat.collapse']) {
      assert.equal(typeof catalog[key], 'string', `${locale}: ${key}`);
      assert(catalog[key].length > 0);
    }
  }
});

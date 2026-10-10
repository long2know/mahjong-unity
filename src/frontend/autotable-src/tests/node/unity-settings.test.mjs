import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { runInNewContext } from 'node:vm';
import { root, require, sourceLoader, plain } from './unity-test-support.mjs';

const ts = require('typescript');
class Input extends EventTarget {
  checked = true;
  value = 'auto';
}
function settingsFixture(saved = {}) {
  const storage = new Map([['mahjong.settings.v1', JSON.stringify(saved)]]);
  const board = new Input(), hand = new Input(), drawerBoard = new Input(), drawerHand = new Input();
  const nodes = new Map([
    ['renderer-board-layout', board], ['renderer-hand-layout', hand],
    ['settings-board-layout', drawerBoard], ['settings-hand-layout', drawerHand],
  ]);
  const { load } = sourceLoader({
    window: { localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) } },
    document: {
      getElementById: id => nodes.get(id) ?? null, body: { classList: { toggle() {} } },
      documentElement: { style: { setProperty() {} } },
    },
  }, {
    'profile.ts': {}, 'rule-presets.ts': {}, 'theme.ts': {}, 'game-state.ts': {},
    'i18n.ts': { t: key => key, translateElements() {}, onLanguageChange() {} },
  });
  return { settings: load('settings-drawer.ts'), board, hand, drawerBoard, drawerHand, saved: () => JSON.parse(storage.get('mahjong.settings.v1')) };
}

test('actual layout settings default/normalize to auto and persist synchronized legacy/Display choices without resetting language', () => {
  const f = settingsFixture({ boardLayout: 'stretched', handLayout: 'all-hands', lang: 'zh-Hans', perspective: false });
  f.settings.installRendererLayoutSettings();
  assert.equal(f.settings.getSettings().boardLayout, 'auto');
  assert.equal(f.settings.getSettings().handLayout, 'auto');
  f.board.value = 'rectangular';
  f.board.dispatchEvent(new Event('change'));
  assert.equal(f.drawerBoard.value, 'rectangular');
  assert.equal(f.saved().boardLayout, 'rectangular');
  f.settings.setSettings({ handLayout: 'separate' });
  assert.equal(f.hand.value, 'separate');
  assert.equal(f.drawerHand.value, 'separate');
  assert.equal(f.saved().lang, 'zh-Hans');
  assert.equal(f.saved().perspective, false);
  f.settings.resetSettings();
  assert.equal(f.board.value, 'auto');
  assert.equal(f.hand.value, 'auto');
});

test('the actual Game presentation resolver uses measured CSS width, preserves explicit layouts and flat/perspective/labels', () => {
  const source = ts.createSourceFile('game.ts', fs.readFileSync(path.join(root, 'src/game.ts'), 'utf8'), ts.ScriptTarget.Latest, true);
  const game = source.statements.find(s => ts.isClassDeclaration(s) && s.name?.text === 'Game');
  const method = game.members.find(m => ts.isMethodDeclaration(m) && m.name.getText(source) === 'presentation');
  const body = ts.createPrinter().printNode(ts.EmitHint.Unspecified, method, source);
  const code = ts.transpileModule(`class Fixture { ${body} }\nFixture;`, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText;
  const Fixture = runInNewContext(code);
  const fixture = new Fixture();
  fixture.settings = { perspective: { checked: false }, tileLabels: { checked: true } };
  fixture.lookDown = { pos: 0.5 };
  fixture.zoom = { pos: -0.2 };
  for (const [width, boardLayout, handLayout] of [[390, 'rectangular', 'separate'], [767, 'rectangular', 'separate'], [768, 'classic', 'in-table'], [1024, 'classic', 'in-table']]) {
    fixture.width = width;
    const presentation = fixture.presentation({ boardLayout: 'auto', handLayout: 'auto' });
    assert.deepEqual(plain(presentation), { viewMode: 'flat', boardLayout, handLayout, tileLabels: true, lookDown: 0.5, zoom: -0.2 });
  }
  fixture.width = 390;
  fixture.settings.perspective.checked = true;
  const explicit = fixture.presentation({ boardLayout: 'classic', handLayout: 'in-table' });
  assert.equal(explicit.boardLayout, 'classic');
  assert.equal(explicit.handLayout, 'in-table');
  assert.equal(explicit.viewMode, 'perspective');
});

test('viewport DTOs round real backing buffers, cap DPR at two and remove the separate hand band when disabled', () => {
  const { rendererViewport } = sourceLoader().load('renderer/renderer-viewport.ts');
  for (const [width, height, dpr] of [[390, 844, 1], [844, 390, 2], [1024, 768, 3], [375.5, 700.5, 1.25]]) {
    const measurement = { width, height, dpr, separateHand: true, insets: { top: 20, right: 5, bottom: 10, left: 5 } };
    const value = rendererViewport('instance', 1, measurement);
    assert.equal(value.renderPixelRatio, Math.min(dpr, 2));
    assert.equal(value.widthPx, Math.round(width * Math.min(dpr, 2)));
    assert.equal(value.heightPx, Math.round(height * Math.min(dpr, 2)));
    assert(value.handBandCssPx > 0 && value.handBandCssPx < height);
    assert.equal(rendererViewport('instance', 2, { ...measurement, separateHand: false }).handBandCssPx, 0);
  }
  assert.throws(() => rendererViewport('instance', 1, { width: 0, height: 844, dpr: 1, insets: { top: 0, right: 0, bottom: 0, left: 0 }, separateHand: false }));
});

test('all shipped locales label the new controls and inherited art is absent from the active shell/result CSS', () => {
  for (const locale of ['en', 'zh-Hans', 'zh-Hant']) {
    const catalog = JSON.parse(fs.readFileSync(path.join(root, `src/i18n/${locale}.json`), 'utf8'));
    for (const key of ['settings.board_layout', 'settings.hand_layout', 'settings.layout_auto', 'settings.layout_classic', 'settings.layout_rectangular', 'settings.layout_in-table', 'settings.layout_separate']) {
      assert.equal(typeof catalog[key], 'string');
      assert(catalog[key].length > 0);
    }
  }
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  const css = fs.readFileSync(path.join(root, 'src/style.css'), 'utf8');
  assert.match(html, /id="sticky" disabled/);
  assert.match(html, /Sticky drag is unavailable in renderer v1/);
  assert.equal(/tiles-labels\.auto|Segment7Standard\.otf|models\.auto|dice\.auto\.png|\.pdf/.test(html + css), false);
});

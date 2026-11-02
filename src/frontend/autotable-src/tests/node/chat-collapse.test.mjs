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
const source = ts.createSourceFile('chat.ts', fs.readFileSync(path.join(root, 'src/chat.ts'), 'utf8'), ts.ScriptTarget.Latest, true);
const names = ['setCollapsed', 'applyCollapsed', 'renderCollapseControls', 'captureMessageScroll', 'restoreMessageScroll'];
const methods = source.statements.filter(node => ts.isFunctionDeclaration(node) && names.includes(node.name?.text));
const body = methods.map(node => ts.createPrinter().printNode(ts.EmitHint.Unspecified, node, source)).join('\n');

function fixture(compact = false) {
  const attributes = new Map(), collapsedClasses = [];
  const state = { collapsed: false, messages: [{ id: 'kept', body: 'message survives collapsing' }] };
  const frames = [], revealed = [];
  let physicalScrollTop = 360;
  const rows = Array.from({ length: 6 }, (_, index) => ({
    getBoundingClientRect: () => ({ top: 40 + index * 80 - physicalScrollTop, bottom: 120 + index * 80 - physicalScrollTop }),
    scrollIntoView: options => revealed.push({ index, ...options }),
  }));
  const messages = {
    get clientHeight() { return state.collapsed ? 0 : 120; },
    get scrollHeight() { return state.collapsed ? 0 : 480; },
    get scrollTop() { return state.collapsed ? 0 : physicalScrollTop; },
    set scrollTop(value) { physicalScrollTop = Math.max(0, Math.min(360, value)); },
    getBoundingClientRect: () => ({ top: 40 }),
    querySelectorAll: () => rows,
  };
  const nodes = new Map([
    ['chat-panel', { classList: { toggle: (_name, on) => collapsedClasses.push(on) } }],
    ['chat-toggle', { setAttribute: (key, value) => attributes.set(key, value) }],
    ['chat-collapse', { hidden: false, setAttribute: () => {} }],
    ['chat-messages', messages],
    ['chat-input', { value: 'unsent draft' }],
  ]);
  const storage = new Map([['mahjong.chat.collapsed.v1', 'false']]);
  let preferences = { mobileInfoPanel: 'chat' }, started = 0, stopped = 0;
  const api = runInNewContext(ts.transpileModule(`${body}\n({setCollapsed, renderCollapseControls, captureMessageScroll});`, {
    compilerOptions: { target: ts.ScriptTarget.ES2022 },
  }).outputText, {
    state, messageScrollTop: 0, messageAtBottom: true,
    COMPACT_VIEW_QUERY: 'compact', COLLAPSED_LS_KEY: 'mahjong.chat.collapsed.v1',
    document: { getElementById: id => nodes.get(id) },
    window: {
      matchMedia: () => ({ matches: compact }),
      localStorage: { setItem: (key, value) => storage.set(key, value) },
      requestAnimationFrame: callback => frames.push(callback),
    },
    getSettings: () => preferences,
    setSettings: next => { preferences = { ...preferences, ...next }; },
    toggleMobilePanel: (_current, panel, open) => open ? panel : 'none',
    setElHidden: (element, hidden) => { element.hidden = hidden; },
    t: key => ({ 'chat.open': 'Open chat', 'chat.collapse': 'Collapse chat' })[key],
    stopPolling: () => { stopped++; }, startPolling: () => { started++; },
    refreshHistory: () => Promise.resolve(), markInvitesRead: () => {},
  });
  return { api, state, storage, attributes, nodes, collapsedClasses, messages, revealed,
    paint: () => { while (frames.length) frames.shift()(); },
    preferences: () => preferences, polling: () => ({ started, stopped }) };
}

test('desktop collapse/reopen is explicit, accessible and preserves messages plus the desktop preference', () => {
  const h = fixture();
  const messages = h.state.messages;
  h.api.setCollapsed(true);
  assert.equal(h.state.collapsed, true);
  assert.equal(h.attributes.get('aria-expanded'), 'false');
  assert.equal(h.attributes.get('aria-label'), 'Open chat');
  assert.equal(h.nodes.get('chat-collapse').hidden, true);
  assert.equal(h.storage.get('mahjong.chat.collapsed.v1'), 'true');
  h.api.setCollapsed(false);
  assert.equal(h.state.collapsed, false);
  assert.equal(h.attributes.get('aria-expanded'), 'true');
  assert.equal(h.nodes.get('chat-collapse').hidden, false);
  assert.equal(h.storage.get('mahjong.chat.collapsed.v1'), 'false');
  assert.equal(h.state.messages, messages);
  assert.deepEqual(h.polling(), { started: 1, stopped: 1 });
  assert.equal(h.preferences().mobileInfoPanel, 'chat');
});

test('mobile collapse still uses the mobile overlay choice, without overwriting desktop collapse persistence', () => {
  const h = fixture(true);
  h.api.setCollapsed(true);
  assert.equal(h.preferences().mobileInfoPanel, 'none');
  assert.equal(h.storage.get('mahjong.chat.collapsed.v1'), 'false');
  h.api.setCollapsed(false);
  assert.equal(h.preferences().mobileInfoPanel, 'chat');
  assert.equal(h.storage.get('mahjong.chat.collapsed.v1'), 'false');
  assert.equal(h.state.messages.length, 1);
});

test('reopening restores the latest message and reveals it through the outer compact scroll container', () => {
  const h = fixture(true);
  const retained = h.state.messages;
  h.api.setCollapsed(true);
  h.messages.scrollTop = 0;
  h.api.setCollapsed(false);
  assert.equal(h.messages.scrollTop, 360, 'restore before asynchronous history can read the hidden zero-size layout');
  h.paint();
  assert.deepEqual(h.revealed, [{ index: 5, block: 'nearest', inline: 'nearest' }]);
  assert.equal(h.nodes.get('chat-input').value, 'unsent draft');
  assert.equal(h.state.messages, retained);
});

test('collapse and hidden history renders do not discard the position of someone reading older messages', () => {
  const h = fixture(true);
  h.messages.scrollTop = 80;
  h.api.setCollapsed(true);
  h.messages.scrollTop = 0;
  h.api.captureMessageScroll();
  h.api.setCollapsed(false);
  h.paint();
  assert.equal(h.messages.scrollTop, 80);
  assert.deepEqual(h.revealed, [{ index: 1, block: 'nearest', inline: 'nearest' }]);
});

test('a queued reopen does not scroll hidden messages when the panel is immediately collapsed again', () => {
  const h = fixture(true);
  h.api.setCollapsed(true);
  h.api.setCollapsed(false);
  h.api.setCollapsed(true);
  h.paint();
  assert.deepEqual(h.revealed, []);
});

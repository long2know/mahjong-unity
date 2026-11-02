import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as esbuild from 'esbuild';
import { hasBuildIdentity, parseServerBuild, shortBuildId } from '../../src/build-info';

const ROOT = path.resolve(__dirname, '../..');
const HTML = fs.readFileSync(path.join(ROOT, 'index.html'), 'utf8');
const FOOTER = HTML.slice(HTML.indexOf('        <div class="lobby-footer">'),
  HTML.indexOf('        <div class="lobby-about">'));
const VIEWPORT = HTML.match(/<meta name="viewport"[\s\S]*?>/)![0];
const CSS = fs.readFileSync(path.join(ROOT, 'node_modules/bootstrap/dist/css/bootstrap.css'), 'utf8')
  + fs.readFileSync(path.join(ROOT, 'src/style.css'), 'utf8')
  + fs.readFileSync(path.join(ROOT, 'src/main.css'), 'utf8')
  + fs.readFileSync(path.join(ROOT, 'src/ui/hicks-mobile-sidebar.css'), 'utf8');
const UI_BUILD = `${'a'.repeat(40)}-dirty-20260918T170000Z`;
const SERVER_BUILD = `${'b'.repeat(40)}-20260918T180000Z`;
const VERSION = '0.32.0.0';

type BuildModule = typeof import('../../src/build-info') & typeof import('../../src/i18n');
type PendingRequest = {
  url: string;
  cache: RequestCache | undefined;
  signal: AbortSignal;
  resolve: (response: Response) => void;
  reject: (reason: Error) => void;
};
type TestWindow = Window & {
  BuildInfoTest: BuildModule;
  buildRequests: PendingRequest[];
  disposeBuildInfo: () => void;
  gameActions: number;
};

function bundle(build: string): string {
  return esbuild.buildSync({
    stdin: {
      contents: "export * from './src/build-info'; export * from './src/i18n';",
      resolveDir: ROOT,
    },
    bundle: true, write: false, format: 'iife', globalName: 'BuildInfoTest',
    define: { __BUILD_SHA__: JSON.stringify(build) },
    logLevel: 'silent',
  }).outputFiles[0].text;
}
const MODULE_JS = bundle(UI_BUILD);

async function mount(page: Page, build = UI_BUILD): Promise<void> {
  await page.clock.install();
  await page.setContent(`<!doctype html><html><head>${VIEWPORT}<style>${CSS}</style></head>
    <body><div id="lobby-panel" class="lobby-open">${FOOTER}</div></body></html>`);
  await page.addScriptTag({ content: build === UI_BUILD ? MODULE_JS : bundle(build) });
  await page.evaluate(() => {
    const w = window as unknown as TestWindow;
    w.buildRequests = [];
    w.gameActions = 0;
    window.fetch = (input, options) => new Promise<Response>((resolve, reject) => {
      w.buildRequests.push({
        url: String(input), cache: options?.cache, signal: options!.signal!,
        resolve, reject,
      });
    });
    document.getElementById('lobby-apply')!.addEventListener('click', () => { w.gameActions++; });
    w.BuildInfoTest.installI18n();
    w.disposeBuildInfo = w.BuildInfoTest.installBuildInfo();
  });
}

async function respond(page: Page, body: unknown, status = 200, index = 0, raw = false): Promise<void> {
  await page.evaluate(({ body, status, index, raw }) => {
    const w = window as unknown as TestWindow;
    w.buildRequests[index].resolve(new Response(raw ? String(body) : JSON.stringify(body), {
      status, headers: { 'Content-Type': 'application/json' },
    }));
  }, { body, status, index, raw });
}

test('metadata parser validates only the public fields it uses', () => {
  expect(parseServerBuild({ buildSha: UI_BUILD, version: VERSION, unrelated: 'not rendered' }))
    .toEqual({ buildSha: UI_BUILD, version: VERSION });
  for (const value of [null, [], {}, { buildSha: UI_BUILD }, { version: VERSION },
    { buildSha: 123, version: VERSION }, { buildSha: ' ', version: VERSION },
    { buildSha: 'a'.repeat(513), version: VERSION }, { buildSha: 'bad\nid', version: VERSION },
    { buildSha: UI_BUILD, version: 31 }, { buildSha: UI_BUILD, version: '<img src=x>' },
    { buildSha: UI_BUILD, version: '1'.repeat(65) }]) {
    expect(parseServerBuild(value)).toBeNull();
  }
});

test('short IDs keep the dirty marker and timestamp; placeholders cannot assert a match', () => {
  expect(shortBuildId(UI_BUILD)).toBe('aaaaaaaaaaaa-dirty-20260918T170000Z');
  expect(shortBuildId('c'.repeat(64))).toBe('c'.repeat(12));
  expect(shortBuildId('x'.repeat(100)).length).toBeLessThanOrEqual(40);
  for (const value of ['', 'dev', ' DEV ', 'development', 'local', 'unknown', 'unidentified', 'n/a']) {
    expect(hasBuildIdentity(value)).toBe(false);
  }
  expect(hasBuildIdentity('local-source-unavailable-20260918T170000Z')).toBe(true);
});

test('build label is eagerly installed, outside the game/auth bootstrap', () => {
  const entry = fs.readFileSync(path.join(ROOT, 'src/index.ts'), 'utf8');
  expect(entry.indexOf('installBuildInfo();')).toBeGreaterThan(entry.indexOf('installI18n();'));
  expect(entry.indexOf('installBuildInfo();')).toBeLessThan(entry.indexOf('bindNewGameControls();'));
  expect(entry).not.toContain('await installBuildInfo');
  const imports = fs.readFileSync(path.join(ROOT, 'src/build-info.ts'), 'utf8').match(/^import .+$/gm);
  expect(imports).toEqual(["import { onLanguageChange, t } from './i18n';"]);
});

test('matching builds expose the loaded UI, actual server and assembly version with one uncached request', async ({ page }) => {
  await mount(page);
  await expect(page.locator('#build-summary')).toHaveText(`Build / Version · UI: ${shortBuildId(UI_BUILD)}`);
  await expect(page.locator('#build-status')).toHaveText('Checking server build…');
  expect(await page.evaluate(() => {
    const w = window as unknown as TestWindow;
    w.BuildInfoTest.installBuildInfo();
    return w.buildRequests.map(r => ({ url: r.url, cache: r.cache }));
  })).toEqual([{ url: '/health?simple=1', cache: 'no-store' }]);
  await respond(page, { buildSha: UI_BUILD, version: VERSION });
  await expect(page.locator('#build-summary')).toHaveText(`Build / Version · Server: ${VERSION} · UI: ${shortBuildId(UI_BUILD)}`);
  await expect(page.locator('#build-status')).toContainText('Page and server report the same build');
  await expect(page.locator('#build-mismatch')).toBeHidden();
  await expect(page.locator('#build-retry')).toBeHidden();
  await page.locator('#build-summary').focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#build-ui-full')).toBeVisible();
  await expect(page.locator('#build-ui-full')).toHaveText(UI_BUILD);
  await expect(page.locator('#build-server-full')).toHaveText(UI_BUILD);
  await expect(page.locator('#build-server-version')).toHaveText(VERSION);
  await page.clock.fastForward(6000);
  await expect(page.locator('#build-status')).toContainText('same build');
  expect(await page.evaluate(() => (window as unknown as TestWindow).buildRequests.length)).toBe(1);
});

test('mismatch remains visible with details collapsed and reload happens only on a user click', async ({ page }) => {
  await mount(page);
  let navigations = 0;
  page.on('framenavigated', () => { navigations++; });
  await respond(page, { buildSha: SERVER_BUILD, version: VERSION });
  await expect(page.locator('#lobby-build-info details')).not.toHaveAttribute('open', '');
  await expect(page.locator('#build-mismatch')).toBeVisible();
  await expect(page.locator('#build-mismatch')).toContainText('page and server are different builds');
  await expect(page.locator('#build-summary')).toContainText(shortBuildId(UI_BUILD));
  await expect(page.locator('#build-server-full')).toHaveText(SERVER_BUILD);
  await page.clock.fastForward(6000);
  expect(navigations).toBe(0);
  await Promise.all([page.waitForEvent('framenavigated'), page.locator('#build-reload').click()]);
  expect(navigations).toBe(1);
});

for (const [name, body, status, raw] of [
  ['HTTP failure', { buildSha: UI_BUILD, version: VERSION }, 503, false],
  ['missing fields', { buildSha: UI_BUILD }, 200, false],
  ['malformed fields', { buildSha: {}, version: VERSION }, 200, false],
  ['invalid JSON', '{not JSON', 200, true],
  ['network failure', null, 0, false],
] as const) {
  test(`${name} never blocks game controls and offers a successful manual retry`, async ({ page }) => {
    await mount(page);
    await page.locator('#lobby-apply').click();
    if (status === 0) {
      await page.evaluate(() => {
        (window as unknown as TestWindow).buildRequests[0].reject(new TypeError('offline'));
      });
    } else {
      await respond(page, body, status, 0, raw);
    }
    await expect(page.locator('#build-status')).toContainText('Server build unavailable');
    await expect(page.locator('#build-mismatch')).toBeHidden();
    await expect(page.locator('#build-server-full')).toHaveText('Unavailable');
    await page.locator('#lobby-apply').click();
    expect(await page.evaluate(() => (window as unknown as TestWindow).gameActions)).toBe(2);
    await page.clock.fastForward(6000);
    expect(await page.evaluate(() => (window as unknown as TestWindow).buildRequests.length)).toBe(1);
    await page.locator('#build-retry').click();
    await expect(page.locator('#build-status')).toHaveText('Checking server build…');
    expect(await page.evaluate(() => (window as unknown as TestWindow).buildRequests.length)).toBe(2);
    await respond(page, { buildSha: UI_BUILD, version: VERSION }, 200, 1);
    await expect(page.locator('#build-status')).toContainText('same build');
    await expect(page.locator('#build-retry')).toBeHidden();
  });
}

test('timeout aborts even an uncooperative fetch; stale responses cannot overwrite a retry or its timer', async ({ page }) => {
  await mount(page);
  await page.clock.fastForward(5000);
  await expect(page.locator('#build-status')).toContainText('Server build unavailable');
  expect(await page.evaluate(() => (window as unknown as TestWindow).buildRequests[0].signal.aborted)).toBe(true);
  await page.locator('#build-retry').click();
  await respond(page, { buildSha: SERVER_BUILD, version: VERSION });
  await expect(page.locator('#build-status')).toHaveText('Checking server build…');
  await expect(page.locator('#build-mismatch')).toBeHidden();
  await page.clock.fastForward(5000);
  await expect(page.locator('#build-retry')).toBeVisible();
  expect(await page.evaluate(() => (window as unknown as TestWindow).buildRequests[1].signal.aborted)).toBe(true);
  await page.locator('#build-retry').click();
  await respond(page, { buildSha: UI_BUILD, version: VERSION }, 200, 2);
  await expect(page.locator('#build-status')).toContainText('same build');
});

test('disposal aborts and clears the timer and language subscription without touching lobby controls', async ({ page }) => {
  await mount(page);
  await page.evaluate(() => {
    const w = window as unknown as TestWindow;
    w.disposeBuildInfo();
    w.BuildInfoTest.setLanguage('zh-Hans');
  });
  expect(await page.evaluate(() => (window as unknown as TestWindow).buildRequests[0].signal.aborted)).toBe(true);
  await respond(page, { buildSha: SERVER_BUILD, version: VERSION });
  await page.clock.fastForward(6000);
  await expect(page.locator('#build-status')).toHaveText('Checking server build…');
  await page.locator('#lobby-apply').click();
  expect(await page.evaluate(() => (window as unknown as TestWindow).gameActions)).toBe(1);
});

for (const [ui, server] of [['dev', SERVER_BUILD], [UI_BUILD, 'local'], ['dev', 'dev']]) {
  test(`unidentified identity (${ui === 'dev' ? 'UI dev' : 'UI stamped'}/${server === SERVER_BUILD ? 'server stamped' : server}) makes no match claim`, async ({ page }) => {
    await mount(page, ui);
    await respond(page, { buildSha: server, version: VERSION });
    await expect(page.locator('#build-status')).toContainText('comparison unavailable');
    await expect(page.locator('#build-mismatch')).toBeHidden();
    await expect(page.locator('#build-ui-full')).toHaveText(ui);
    await expect(page.locator('#build-server-full')).toHaveText(server);
    if (ui === 'dev') await expect(page.locator('#build-summary')).toContainText('dev / unidentified');
  });
}

test('English, Simplified and Traditional Chinese update live without changing identities or refetching', async ({ page }) => {
  await mount(page);
  await page.evaluate(() => (window as unknown as TestWindow).BuildInfoTest.setLanguage('zh-Hans'));
  await expect(page.locator('#build-status')).toHaveText('正在查询服务器构建…');
  await respond(page, { buildSha: SERVER_BUILD, version: VERSION });
  await expect(page.locator('#build-summary')).toContainText('构建 / 版本');
  await expect(page.locator('#build-mismatch')).toContainText('此页面与服务器的构建不同');
  await expect(page.locator('#build-reload')).toHaveText('重新加载页面');
  await page.locator('#build-summary').click();
  await expect(page.locator('[data-i18n="build.version"]')).toHaveText('服务器程序集版本');
  await page.evaluate(() => (window as unknown as TestWindow).BuildInfoTest.setLanguage('zh-Hant'));
  await expect(page.locator('#build-summary')).toContainText('建置 / 版本');
  await expect(page.locator('#build-mismatch')).toContainText('此頁面與伺服器的建置不同');
  await expect(page.locator('#build-reload')).toHaveText('重新載入頁面');
  await expect(page.locator('[data-i18n="build.version"]')).toHaveText('伺服器組件版本');
  await page.evaluate(() => (window as unknown as TestWindow).BuildInfoTest.setLanguage('en'));
  await expect(page.locator('#build-summary')).toContainText('Build / Version');
  await expect(page.locator('#build-ui-full')).toBeVisible();
  await expect(page.locator('#build-ui-full')).toHaveText(UI_BUILD);
  await expect(page.locator('#build-server-full')).toHaveText(SERVER_BUILD);
  expect(await page.evaluate(() => (window as unknown as TestWindow).buildRequests.length)).toBe(1);
});

test('build IDs are rendered as text, never HTML, including the embedded UI identity', async ({ page }) => {
  const dangerous = '<img src=x onerror="window.buildUnsafe=true">';
  await mount(page, `ui-${dangerous}`);
  await respond(page, { buildSha: dangerous, version: VERSION });
  await page.locator('#build-summary').click();
  await expect(page.locator('#build-ui-full')).toHaveText(`ui-${dangerous}`);
  await expect(page.locator('#build-server-full')).toHaveText(dangerous);
  await expect(page.locator('#lobby-build-info img, #lobby-build-info script')).toHaveCount(0);
  expect(await page.evaluate(() => 'buildUnsafe' in window)).toBe(false);
});

test('footer stays readable on a narrow screen and survives closing/reopening the room lobby', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 568 });
  await mount(page);
  await respond(page, { buildSha: 'long-server-id-'.repeat(30), version: VERSION });
  await page.locator('#build-summary').click();
  const layout = await page.locator('#lobby-build-info').evaluate(root => {
    const box = root.getBoundingClientRect();
    const style = getComputedStyle(root);
    return {
      left: box.left, right: box.right, width: window.innerWidth,
      overflow: root.scrollWidth > root.clientWidth,
      fontSize: Number.parseFloat(style.fontSize),
      color: style.color,
      reloadHeight: document.getElementById('build-reload')!.getBoundingClientRect().height,
    };
  });
  expect(layout.left).toBeGreaterThanOrEqual(0);
  expect(layout.right).toBeLessThanOrEqual(layout.width);
  expect(layout.overflow).toBe(false);
  expect(layout.fontSize).toBeGreaterThanOrEqual(12);
  expect(layout.color).toBe('rgb(240, 240, 240)');
  expect(layout.reloadHeight).toBeGreaterThanOrEqual(44);
  await page.evaluate(() => document.getElementById('lobby-panel')!.classList.remove('lobby-open'));
  await expect(page.locator('#build-summary')).toBeHidden();
  await page.evaluate(() => document.getElementById('lobby-panel')!.classList.add('lobby-open'));
  await expect(page.locator('#build-summary')).toBeVisible();
  await expect(page.locator('#build-mismatch')).toBeVisible();
  expect(await page.evaluate(() => (window as unknown as TestWindow).buildRequests.length)).toBe(1);
});

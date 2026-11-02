import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { root, require } from './unity-test-support.mjs';

const { chromium } = require('@playwright/test');
const { buildSync } = require('esbuild');

// Execute the actual shell, move-log and viewport host in a real DOM, with no
// server, player build, network requests or invented renderer-readiness marker.
function productionModules() {
  return buildSync({
    absWorkingDir: root, bundle: true, write: false, format: 'iife', platform: 'browser',
    target: 'es2022', loader: { '.css': 'empty' }, logLevel: 'silent',
    stdin: {
      resolveDir: root,
      contents: `
        import * as shell from './src/browser-shell.ts';
        import * as drawers from './src/ui/drawer-accessibility.ts';
        import * as host from './src/renderer/unity-host.ts';
        import * as moves from './src/move-log.ts';
        import * as claims from './src/ui/claim-window-overlay.ts';
        window.shellSource = {
          'browser-shell.ts': shell, 'ui/drawer-accessibility.ts': drawers,
          'renderer/unity-host.ts': host, 'move-log.ts': moves, 'ui/claim-window-overlay.ts': claims,
        };
      `,
    },
  }).outputFiles[0].text;
}

const markup = fs.readFileSync(path.join(root, 'index.html'), 'utf8')
  .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '')
  .replace(/<link\b[^>]*>/gi, '');
const styles = [
  'node_modules/bootstrap/dist/css/bootstrap.css', 'src/style.css', 'src/main.css',
  'src/ui/hicks-mobile-sidebar.css', 'src/browser-shell.css', 'src/mobile-table.css',
  'src/renderer/unity-host.css', 'src/ui/rule-action-controls.css', 'src/ui/claim-window-overlay.css',
].map(file => fs.readFileSync(path.join(root, file), 'utf8')).join('\n');

async function measure(page) {
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  return page.evaluate(() => {
    const rect = element => {
      const bounds = element.getBoundingClientRect();
      return { x: bounds.x, y: bounds.y, left: bounds.left, top: bounds.top, right: bounds.right,
        bottom: bounds.bottom, width: bounds.width, height: bounds.height };
    };
    const visible = element => {
      const style = getComputedStyle(element);
      return !element.hidden && style.display !== 'none' && style.visibility !== 'hidden'
        && element.getBoundingClientRect().width > 0 && element.getBoundingClientRect().height > 0;
    };
    const toolbar = [...document.querySelectorAll('.browser-toolbar > button, .chat-panel-collapsed .chat-toggle')]
      .filter(visible).map(element => ({ id: element.id, text: element.innerText, ...rect(element),
        hit: document.elementFromPoint(element.getBoundingClientRect().x + element.getBoundingClientRect().width / 2,
          element.getBoundingClientRect().y + element.getBoundingClientRect().height / 2)?.closest('button')?.id }));
    const viewport = window.shellHost.viewport;
    const { top, right, bottom, left } = viewport.insetsCssPx;
    const clear = { left, top, right: viewport.widthCssPx - right, bottom: viewport.heightCssPx - bottom };
    const hand = { ...clear, top: clear.bottom - viewport.handBandCssPx };
    const overlays = ['chat-panel', 'move-log', 'turn-banner', 'pickup-hud', 'claim-window-actions', 'own-turn-actions', 'bot-banner']
      .map(id => document.getElementById(id)).filter(element => element && visible(element))
      .map(element => ({ id: element.id, ...rect(element) }));
    const variant = document.getElementById('variant-badge');
    const mainStyle = getComputedStyle(document.getElementById('main'));
    return {
      width: innerWidth, height: innerHeight, docWidth: document.documentElement.scrollWidth,
      toolbar, overlays, viewport, clear, hand, main: rect(document.getElementById('main')),
      shell: rect(document.getElementById('browser-shell')),
      mainStyle: { top: mainStyle.top, left: mainStyle.left, inset: mainStyle.inset,
        position: mainStyle.position, transform: mainStyle.transform, transition: mainStyle.transition },
      variant: { ...rect(variant), scrollWidth: variant.scrollWidth, clientWidth: variant.clientWidth },
      turn: rect(document.getElementById('turn-banner')),
      reduced: getComputedStyle(document.getElementById('settings-button')).transitionDuration,
      markers: [...document.querySelectorAll('#unity-renderer-ready, #scene-shell-ready')].length,
    };
  });
}

function overlap(a, b) {
  return a.left < b.right - 1 && a.right > b.left + 1 && a.top < b.bottom - 1 && a.bottom > b.top + 1;
}

function assertLayout(result, state) {
  const at = `${result.width}×${result.height} ${state}`;
  assert(result.docWidth <= result.width, `${at}: document overflow ${result.docWidth}`);
  assert.equal(result.markers, 0, 'a source-only measurement must never claim native readiness');
  assert(result.clear.top >= result.shell.bottom - 1,
    `${at}: host did not reserve the header: ${JSON.stringify({ shell: result.shell, main: result.main, style: result.mainStyle, viewport: result.viewport })}`);
  assert(result.variant.scrollWidth <= result.variant.clientWidth + 1, `${at}: variant is truncated`);
  assert(result.turn.left >= 0 && result.turn.right <= result.width, `${at}: turn prompt outside viewport`);
  assert(result.shell.height <= (result.width <= 600 ? 116 : 96), `${at}: chrome is unexpectedly tall (${result.shell.height})`);
  for (const target of result.toolbar) {
    assert(target.width >= 44 && target.height >= 44, `${at}: small touch target ${target.id}: ${target.width}×${target.height}`);
    assert(target.left >= 0 && target.right <= result.width, `${at}: ${target.id} outside viewport`);
    assert.equal(target.hit, target.id, `${at}: ${target.id} center is occluded by ${target.hit}`);
    assert(target.text.trim().length > 0, `${at}: icon-only action ${target.id}`);
  }
  for (let index = 0; index < result.toolbar.length; index++) {
    for (const other of result.toolbar.slice(index + 1)) {
      assert(!overlap(result.toolbar[index], other), `${at}: ${result.toolbar[index].id} overlaps ${other.id}`);
    }
  }
  for (const panel of result.overlays) {
    assert(panel.left >= 0 && panel.right <= result.width && panel.top >= 0 && panel.bottom <= result.height + 1,
      `${at}: ${panel.id} outside viewport: ${JSON.stringify(panel)}`);
    assert(!overlap(panel, result.hand), `${at}: ${panel.id} overlaps the host's reserved hand band`);
  }
  const chat = result.overlays.find(panel => panel.id === 'chat-panel');
  if (chat) {
    for (const panel of result.overlays.filter(panel => panel.id !== 'chat-panel')) {
      assert(!overlap(chat, panel), `${at}: chat overlaps ${panel.id}`);
    }
  }
  assert(result.clear.right - result.clear.left >= 280, `${at}: table's clear width is unusable`);
  assert(result.clear.bottom - result.clear.top - result.viewport.handBandCssPx >= 120, `${at}: no clear board above hand`);
}

test('production shell + UnityHost reserve unobstructed controls, panels and hand space at phone, landscape and desktop sizes', async t => {
  const executablePath = process.env.CHROMIUM_EXECUTABLE_PATH;
  if (executablePath) assert(fs.existsSync(executablePath), 'CHROMIUM_EXECUTABLE_PATH must point to an installed browser');
  // Chromium's singleton socket must stay below the Unix-domain path limit.
  const runtime = path.join(root, 'tests/node/.b');
  fs.mkdirSync(runtime);
  const previousTemp = process.env.TMPDIR;
  process.env.TMPDIR = runtime;
  let browser;
  t.after(async () => {
    await browser?.close();
    if (previousTemp === undefined) delete process.env.TMPDIR; else process.env.TMPDIR = previousTemp;
    fs.rmSync(runtime, { recursive: true, force: true });
  });
  browser = await chromium.launch({
    ...(executablePath ? { executablePath } : {}), headless: true,
    args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-breakpad'],
    env: { ...process.env, HOME: runtime, XDG_CACHE_HOME: runtime, XDG_CONFIG_HOME: runtime },
  });
  const bundle = productionModules();
  for (const [width, height] of [[360, 800], [390, 844], [500, 900], [844, 390], [1280, 800]]) {
    await t.test(`${width}×${height}`, async () => {
      const context = await browser.newContext({ viewport: { width, height }, reducedMotion: 'reduce' });
      const page = await context.newPage();
      try {
        await page.route('**/*', route => route.abort());
        await page.setContent(markup.replace('</head>', `<style>${styles}</style></head>`));
        await page.addScriptTag({ content: bundle });
        await page.evaluate(() => {
          document.body.className = 'variant-changsha theme-light';
          const chat = document.getElementById('chat-panel');
          chat.hidden = false;
          const turn = document.getElementById('turn-banner');
          turn.hidden = false;
          turn.className = 'visible turn-banner-discard';
          turn.textContent = 'Your turn — tap a tile to discard';
          new window.shellSource['move-log.ts'].MoveLog({});
          document.querySelector('.move-log-entries').innerHTML =
            '<p>A player discarded a tile.</p>'.repeat(8);
          document.getElementById('online-players-status').textContent = 'Other players are online.';
          document.getElementById('chat-messages').innerHTML = '<div class="chat-message">A retained room message.</div>'.repeat(6);
          window.shellSource['browser-shell.ts'].installBrowserShell();
          const claim = new window.shellSource['ui/claim-window-overlay.ts'].ClaimWindowOverlay({}).buildDom();
          document.getElementById('browser-action-slot').appendChild(claim);
          const { UnityHost } = window.shellSource['renderer/unity-host.ts'];
          window.shellHost = new UnityHost(document.getElementById('main'), {
            action() {}, resized() {}, resumed() {}, failure() {}, notice() {},
          });
          // No player is mounted. Only the actual host's DOM measurement and
          // validated viewport DTO are used to assert the rendering boundary.
          document.getElementById('unity-status').hidden = true;
          window.shellHost.setPresentation({ handLayout: 'separate' });
        });
        let result = await measure(page);
        assertLayout(result, 'collapsed');
        assert(result.reduced.split(',').every(duration => parseFloat(duration) <= .001),
          'OS reduced-motion must disable visible chrome transitions');
        const collapsed = result;
        await page.evaluate(() => {
          document.getElementById('chat-panel').classList.remove('chat-panel-collapsed');
          document.getElementById('chat-collapse').hidden = false;
          document.getElementById('chat-room-selectors').hidden = false;
        });
        result = await measure(page);
        assertLayout(result, 'chat open');
        assert(result.overlays.some(panel => panel.id === 'chat-panel' && panel.height >= 180));
        const openChat = result;
        await page.evaluate(() => {
          document.getElementById('own-turn-actions').hidden = false;
          document.querySelectorAll('#own-turn-actions button').forEach(button => { button.disabled = false; });
        });
        assertLayout(await measure(page), 'chat and own-turn declarations');
        await page.evaluate(() => {
          document.getElementById('own-turn-actions').hidden = true;
          document.getElementById('pickup-hud').hidden = false;
        });
        assertLayout(await measure(page), 'chat and manual pickup');
        await page.evaluate(() => {
          document.getElementById('pickup-hud').hidden = true;
          const claim = document.getElementById('claim-window-actions');
          claim.hidden = false;
          claim.classList.add('ferro-claim-overlay-visible');
          claim.querySelectorAll('.ferro-claim-badge').forEach(button => {
            button.classList.add('ferro-claim-badge-available');
            button.disabled = false;
          });
        });
        assertLayout(await measure(page), 'chat and claim window');
        await page.evaluate(() => {
          document.getElementById('claim-window-actions').hidden = true;
          document.body.classList.add('mobile-table-status-visible');
          const status = document.getElementById('bot-banner');
          status.innerHTML = '<div class="bot-banner-summary">1 human, 3 bots, 0 open human seats</div>';
          status.hidden = false;
        });
        assertLayout(await measure(page), 'chat and optional table status');
        await page.evaluate(() => {
          document.getElementById('bot-banner').hidden = true;
          document.body.classList.remove('mobile-table-status-visible');
          document.getElementById('chat-panel').classList.add('chat-panel-collapsed');
          document.getElementById('chat-collapse').hidden = true;
          document.body.classList.add('move-log-open');
        });
        result = await measure(page);
        assertLayout(result, 'move log open');
        assert(result.overlays.some(panel => panel.id === 'move-log' && panel.height > 44));
        await page.evaluate(() => {
          document.body.classList.remove('move-log-open');
          document.getElementById('variant-badge').textContent = '🎴 Riichi 3p';
          const turn = document.getElementById('turn-banner');
          turn.textContent = 'No open seat in this game — start a New Game from the Lobby.';
          turn.setAttribute('role', 'button');
          turn.tabIndex = 0;
          turn.style.pointerEvents = 'auto';
        });
        assertLayout(await measure(page), 'long actionable cue');
        if (process.env.BROWSER_SHELL_SCREENSHOT && width === 390) {
          await page.screenshot({ path: path.resolve(root, process.env.BROWSER_SHELL_SCREENSHOT), fullPage: true });
        }
        t.diagnostic(JSON.stringify({
          size: `${width}×${height}`, headerHeight: collapsed.shell.height,
          collapsedInsets: collapsed.viewport.insetsCssPx, chatInsets: openChat.viewport.insetsCssPx,
          handBand: collapsed.viewport.handBandCssPx, qualification: 'production-source DOM/viewport only; no Unity player or physical device',
        }));
      } finally {
        await page.evaluate(() => window.shellHost?.dispose());
        await context.close();
      }
    });
  }
  for (const [width, height] of [[390, 844], [844, 390], [1280, 800]]) {
    await t.test(`relay Deal remains reachable below the ${width}×${height} header`, async () => {
      const context = await browser.newContext({ viewport: { width, height }, reducedMotion: 'reduce' });
      const page = await context.newPage();
      try {
        await page.route('**/*', route => route.abort());
        await page.setContent(markup.replace('</head>', `<style>${styles}</style></head>`));
        await page.addScriptTag({ content: bundle });
        await page.evaluate(() => {
          document.body.className = 'variant-riichi theme-light';
          new window.shellSource['move-log.ts'].MoveLog({});
          window.shellSource['browser-shell.ts'].installBrowserShell();
          window.dealClicks = 0;
          document.getElementById('deal').addEventListener('click', () => window.dealClicks++);
        });
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        const bounds = await page.locator('#deal').evaluate(button => {
          const box = button.getBoundingClientRect();
          const header = document.getElementById('browser-shell').getBoundingClientRect();
          const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
          return { top: box.top, bottom: box.bottom, headerBottom: header.bottom,
            unobstructed: hit === button || button.contains(hit) };
        });
        assert(bounds.top >= bounds.headerBottom && bounds.bottom <= height,
          `The real relay Deal control must be below, not underneath, the fixed header: ${JSON.stringify(bounds)}`);
        assert(bounds.unobstructed, 'The real relay Deal control must receive ordinary pointer input');
        await page.locator('#deal').click({ timeout: 1500 });
        assert.equal(await page.evaluate(() => window.dealClicks), 1);
      } finally {
        await context.close();
      }
    });
  }
  for (const [width, height, dpr] of [[1280, 800, 1], [390, 844, 2]]) {
    await t.test(`the ${width}×${height} header must not cull the WebGL hand before keyboard focus`, async () => {
      const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: dpr });
      const page = await context.newPage();
      try {
        await page.route('**/*', route => route.abort());
        await page.setContent(markup.replace('</head>', `<style>${styles}</style></head>`));
        await page.addScriptTag({ content: bundle });
        await page.evaluate(() => {
          document.body.className = 'variant-changsha theme-light';
          new window.shellSource['move-log.ts'].MoveLog({});
          window.shellSource['browser-shell.ts'].installBrowserShell();
          const { UnityHost } = window.shellSource['renderer/unity-host.ts'];
          window.shellHost = new UnityHost(document.getElementById('main'), {
            action() {}, resized() {}, resumed() {}, failure() {}, notice() {},
          });
          document.getElementById('unity-status').hidden = true;
          window.shellHost.setPresentation({ handLayout: 'separate' });
          const canvas = window.shellHost.canvas;
          const gl = canvas.getContext('webgl2');
          if (!gl) throw new Error('Actual WebGL2 is required for this browser-composition regression');
          window.paintHand = () => {
            gl.disable(gl.SCISSOR_TEST);
            gl.clearColor(0, 0, 0, 0);
            gl.clear(gl.COLOR_BUFFER_BIT);
            gl.enable(gl.SCISSOR_TEST);
            gl.scissor(0, 0, canvas.width, canvas.height -
              Math.ceil(document.getElementById('browser-shell').getBoundingClientRect().height * devicePixelRatio));
            gl.clearColor(17 / 255, 93 / 255, 157 / 255, 1);
            gl.clear(gl.COLOR_BUFFER_BIT);
            window.paintHandle = requestAnimationFrame(window.paintHand);
          };
          window.paintHand();
        });
        await measure(page);
        for (const focused of [false, true]) {
          if (focused) {
            await page.locator('#unity-canvas').focus();
            await page.keyboard.press('ArrowLeft');
          }
          const screenshot = await page.screenshot();
          const pixels = await page.evaluate(async ({ bytes, dpr }) => {
            const image = await createImageBitmap(new Blob([Uint8Array.from(bytes)], { type: 'image/png' }));
            const canvas = new OffscreenCanvas(image.width, image.height);
            const ctx = canvas.getContext('2d');
            ctx.drawImage(image, 0, 0);
            return [innerWidth * .25, innerWidth * .5].map(x =>
              [...ctx.getImageData(Math.round(x * dpr), Math.round((innerHeight - 10) * dpr), 1, 1).data]);
          }, { bytes: [...screenshot], dpr });
          for (const pixel of pixels)
            assert.deepEqual(pixel, [17, 93, 157, 255],
              `The real composited hand band must remain visible ${focused ? 'after' : 'before'} keyboard focus`);
        }
      } finally {
        await page.evaluate(() => { cancelAnimationFrame(window.paintHandle); window.shellHost?.dispose(); });
        await context.close();
      }
    });
  }
});

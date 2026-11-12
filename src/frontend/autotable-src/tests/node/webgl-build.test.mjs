import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { build } from 'vite';
import { root } from './renderer-test-support.mjs';

const options = { root, configFile: path.join(root, 'vite.config.ts'), logLevel: 'silent',
  build: { write: false, emptyOutDir: false } };

test('ordinary default Vite build contains only WebGL2 and local artwork; no prebuilt engine or external Autotable package', async () => {
  const output = (await build(options)).output;
  const graph = JSON.parse(String(output.find(asset => asset.fileName === 'renderer-module-graph.json').source));
  assert.equal(graph.engine, 'webgl2');
  assert.equal(graph.externalAutotableRuntimeRequired, false);
  for (const name of ['renderer/webgl-host.ts', 'renderer/webgl-scene.ts', 'renderer/webgl-input.ts',
    'renderer/renderer-actions.ts', 'renderer/renderer-frame.ts', 'renderer/gpu/tile-mesh.ts', 'renderer/gpu/picking.ts']) {
    assert(graph.modules.some(module => module.endsWith(`src/${name}`)), name);
  }
  assert.equal(graph.modules.some(module => /unity-host|\/unity\/|renderer-webgl2|node_modules\/.*autotable/i.test(module)), false);
  assert(output.some(asset => /FacesLabelled\..*\.png$/.test(asset.fileName)));
  assert(output.some(asset => /^mahjong\.[0-9a-f]+\.js$/.test(asset.fileName)));
  assert.equal(output.some(asset => /autotable/i.test(asset.fileName)), false);
  assert.equal(output.some(asset => /\.wasm$|\.data$|\.loader\.js$|\.framework\.js$|renderer-build\.json/.test(asset.fileName)), false);
  const math = output.find(asset => /^model-math\..*\.js$/.test(asset.fileName));
  assert.equal(math.code.includes('WebGLRenderer'), false);
  assert.equal(math.code.includes('GLTFLoader'), false);
  const shell = output.filter(file => file.type === 'chunk').map(file => file.code).join('\n');
  assert.equal(shell.includes('createUnityInstance'), false);
  assert.equal(shell.includes('MahjongUnityBridge'), false);
  assert.equal(shell.includes('__WEBGL_COMPARISON__'), false);
});

test('actual bundle mutation with an engine WASM file fails the no-engine-artifact build guard', async () => {
  await assert.rejects(() => build({
    ...options, plugins: [{ name: 'mutate-engine-artifact', enforce: 'pre',
      generateBundle() { this.emitFile({ type: 'asset', fileName: 'old.wasm', source: new Uint8Array([0, 97, 115, 109]) }); },
    }],
  }), /Unexpected engine artifact in the browser bundle/);
});

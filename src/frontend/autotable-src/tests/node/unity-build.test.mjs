import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { build } from 'vite';
import { root } from './unity-test-support.mjs';

const options = {
  root, configFile: path.join(root, 'vite.config.ts'), logLevel: 'silent',
  build: { write: false, emptyOutDir: false },
};

test('the real Vite production graph includes the source Unity host/model and no retired GPU/assets', async () => {
  const output = await build(options);
  const graphAsset = output.output.find(file => file.fileName === 'renderer-module-graph.json');
  const graph = JSON.parse(String(graphAsset.source));
  assert.equal(graph.qualification, 'source-only');
  for (const name of ['renderer/renderer-sink.ts', 'renderer/renderer-frame.ts', 'renderer/renderer-actions.ts', 'renderer/unity-host.ts', 'world.ts', 'movement.ts', 'slot.ts', 'thing.ts', 'client.ts', 'game-ui.ts']) {
    assert(graph.modules.some(module => module.endsWith('src/' + name)), name);
  }
  assert.equal(graph.modules.some(module => /src\/(?:asset-loader|main-view|object-view|mouse-ui|three-renderer|thing-group|center)\.ts|src\/renderer-webgl2\//.test(module)), false);
  const math = output.output.find(file => /^model-math\..*\.js$/.test(file.fileName));
  assert.equal(math.code.includes('WebGLRenderer'), false);
  assert.equal(math.code.includes('GLTFLoader'), false);
  assert.equal(output.output.some(file => /renderer-build\.json|\.wasm$|\.data$|\.loader\.js$/.test(file.fileName)), false,
    'a browser build must not fabricate Unity artifacts');
});

test('a mutation of the actual primary entry to import a retired renderer is rejected by the Vite graph gate', async () => {
  await assert.rejects(() => build({
    ...options,
    plugins: [{
      name: 'test-retired-primary-import-mutation', enforce: 'pre',
      transform(code, id) {
        if (id === path.join(root, 'src/index.ts')) return `import('./renderer-webgl2/hello');\n${code}`;
      },
    }],
  }), /Retired GPU module entered the primary browser graph/);
});

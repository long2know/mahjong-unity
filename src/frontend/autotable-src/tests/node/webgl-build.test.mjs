import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { build } from 'vite';
import { root } from './unity-test-support.mjs';

const options = { root, configFile: path.join(root, 'vite.config.ts'), logLevel: 'silent',
  build: { write: false, emptyOutDir: false } };

test('comparison graph includes only selected retained primitives and real tile artwork, never a Three renderer or fabricated Unity build', async () => {
  const previous = process.env.MAHJONG_WEBGL_COMPARISON;
  process.env.MAHJONG_WEBGL_COMPARISON = '1';
  try {
    const output = (await build(options)).output;
    const graph = JSON.parse(String(output.find(asset => asset.fileName === 'renderer-module-graph.json').source));
    for (const name of ['renderer/webgl-host.ts', 'renderer/webgl-scene.ts', 'renderer/webgl-input.ts',
      'renderer/renderer-actions.ts', 'renderer/renderer-frame.ts', 'renderer-webgl2/tile-mesh.ts', 'renderer-webgl2/picking.ts']) {
      assert(graph.modules.some(module => module.endsWith(`src/${name}`)), name);
    }
    assert.equal(graph.modules.some(module => /renderer-webgl2\/(?:hello|scene|tile-drag)\.ts/.test(module)), false);
    assert(output.some(asset => /FacesLabelled\..*\.png$/.test(asset.fileName)), 'actual owned face artwork must be emitted');
    assert(output.some(asset => /^webgl-host\..*\.js$/.test(asset.fileName)), 'the selected GPU host is a real lazy chunk');
    assert.equal(output.some(asset => /renderer-build\.json|\.wasm$|\.data$|\.loader\.js$/.test(asset.fileName)), false);
    const math = output.find(asset => /^model-math\..*\.js$/.test(asset.fileName));
    assert.equal(math.code.includes('WebGLRenderer'), false);
    assert.equal(math.code.includes('GLTFLoader'), false);
    await assert.rejects(() => build({
      ...options, plugins: [{ name: 'mutate-comparison-to-retired-demo', enforce: 'pre',
        transform(code, id) { if (id === path.join(root, 'src/index.ts')) return `import('./renderer-webgl2/hello');\n${code}`; } }],
    }), /Retired GPU module entered the primary browser graph/);
  } finally {
    if (previous === undefined) delete process.env.MAHJONG_WEBGL_COMPARISON;
    else process.env.MAHJONG_WEBGL_COMPARISON = previous;
  }
});

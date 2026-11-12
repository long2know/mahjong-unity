import { defineConfig, type Plugin } from 'vite';
import { resolve, relative } from 'node:path';
import { copyFileSync, existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';

const retired = /[/\\](?:unity[/\\]|unity-host\.ts|src[/\\](?:(?:asset-loader|main-view|object-view|mouse-ui|three-renderer|thing-group|center|selection-box)\.ts(?:\?|$)|renderer-webgl2[/\\]))/i;
const engineArtifact = /(?:\.wasm|\.data|\.loader\.js|\.framework\.js)$|(?:^|\/)(?:unity|renderer-build\.json)(?:\/|$)/i;

function browserAssets(): Plugin {
  let write = true;
  let built = false;
  return {
    name: 'mahjong-web-assets',
    configResolved(config) { write = config.build.write; },
    generateBundle(_options, bundle) {
      const modules = Object.values(bundle).flatMap(output => output.type === 'chunk' ? Object.keys(output.modules) : []);
      const forbidden = modules.find(id => retired.test(id));
      if (forbidden) this.error(`Retired engine module entered the browser graph: ${relative(__dirname, forbidden)}`);
      const artifact = Object.keys(bundle).find(name => engineArtifact.test(name));
      if (artifact) this.error(`Unexpected engine artifact in the browser bundle: ${artifact}`);
      this.emitFile({
        type: 'asset', fileName: 'renderer-module-graph.json',
        source: JSON.stringify({
          artifactKind: 'browser-source-module-graph', qualification: 'source-only',
          engine: 'webgl2', externalAutotableRuntimeRequired: false,
          modules: [...new Set(modules.map(id => relative(__dirname, id.replace(/\0/g, ''))))].sort(),
        }, null, 2),
      });
    },
    writeBundle() { built = true; },
    closeBundle() {
      if (!write || !built) return;
      const out = resolve(__dirname, '..', 'autotable');
      for (const name of ['sw.js', 'manifest.webmanifest', 'about.html', 'COPYING', 'UPSTREAM_SHA', 'app-icon.svg']) {
        const source = resolve(__dirname, name);
        if (existsSync(source)) copyFileSync(source, resolve(out, name));
      }
      execFileSync(process.execPath, [resolve(__dirname, 'scripts/generate-sw-manifest.js')], { stdio: 'inherit' });
      const files = readdirSync(out).filter(name => !['web-build.json'].includes(name));
      if (files.some(name => engineArtifact.test(name))) throw Error('Engine artifacts are forbidden in the default browser output.');
      writeFileSync(resolve(out, 'web-build.json'), JSON.stringify({
        artifactKind: 'mahjong-web-build', engine: 'webgl2', sourceRevision: process.env.BUILD_SHA || 'dev',
        files: files.filter(name => !name.startsWith('.')).map(name => {
          const bytes = readFileSync(resolve(out, name));
          return { path: name, bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };
        }),
      }, null, 2) + '\n');
    },
  };
}

export default defineConfig({
  root: __dirname,
  publicDir: false,
  base: './',
  cacheDir: resolve(__dirname, '.vite'),
  server: {
    proxy: {
      '/hubs': { target: process.env.AUTOTABLE_BACKEND ?? 'http://localhost:5000', ws: true, changeOrigin: true },
      '/ws': { target: process.env.AUTOTABLE_BACKEND ?? 'http://localhost:5000', ws: true, changeOrigin: true },
      '/api': { target: process.env.AUTOTABLE_BACKEND ?? 'http://localhost:5000', changeOrigin: true },
    },
  },
  build: {
    outDir: resolve(__dirname, '..', 'autotable'),
    emptyOutDir: true,
    target: 'es2017',
    sourcemap: false,
    cssCodeSplit: false,
    rollupOptions: {
      input: resolve(__dirname, 'index.html'),
      treeshake: { moduleSideEffects: id => !id.includes('node_modules/three/') },
      output: {
        assetFileNames: '[name].[hash:8].[ext]',
        chunkFileNames: '[name].[hash:8].js',
        entryFileNames: 'mahjong.[hash:8].js',
        hashCharacters: 'hex',
        manualChunks(id) {
          if (id.includes('node_modules/hls.js/')) return 'hls';
          if (id.includes('node_modules/@sentry/')) return 'sentry';
          if (id.includes('node_modules/three/')) return 'model-math';
          return undefined;
        },
      },
    },
  },
  define: {
    __BUILD_SHA__: JSON.stringify(process.env.BUILD_SHA || 'dev'),
    'process.env.NODE_ENV': JSON.stringify('production'),
  },
  plugins: [browserAssets()],
});

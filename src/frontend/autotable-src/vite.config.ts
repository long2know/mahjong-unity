import { defineConfig, type Plugin } from 'vite';
import { resolve, relative } from 'node:path';
import { copyFileSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

const retired = /[/\\]src[/\\](?:(?:asset-loader|main-view|object-view|mouse-ui|three-renderer|thing-group|center|selection-box)\.ts(?:\?|$)|renderer-webgl2[/\\])/;

function browserAssets(): Plugin {
  let write = true;
  let built = false;
  return {
    name: 'unity-browser-shell-assets',
    configResolved(config) { write = config.build.write; },
    generateBundle(_options, bundle) {
      const modules = Object.values(bundle).flatMap(output => output.type === 'chunk' ? Object.keys(output.modules) : []);
      const forbidden = modules.find(id => retired.test(id));
      if (forbidden) this.error(`Retired GPU module entered the primary browser graph: ${relative(__dirname, forbidden)}`);
      this.emitFile({
        type: 'asset', fileName: 'renderer-module-graph.json',
        source: JSON.stringify({
          artifactKind: 'browser-source-module-graph', qualification: 'source-only',
          modules: [...new Set(modules.map(id => relative(__dirname, id)))].sort(),
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
      '/autotable/ws': { target: process.env.AUTOTABLE_BACKEND ?? 'http://localhost:5000', ws: true, changeOrigin: true },
      '/autotable/unity': { target: process.env.AUTOTABLE_BACKEND ?? 'http://localhost:5000', changeOrigin: true },
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
        entryFileNames: 'autotable-src.[hash:8].js',
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

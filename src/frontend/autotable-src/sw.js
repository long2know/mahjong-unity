// Phase K Wave 2 → Wave 3 — Service worker.
//
// Caching strategy:
//   • cache-first for static immutable assets (anything matching the
//     parcel content-hash filename pattern: `<name>.<8hex>.<ext>`).
//     Parcel re-builds mint new filenames, so we can hold these
//     indefinitely; the old hashed files coexist until the next
//     install-cycle's `activate` purges them.
//   • cache-first for fonts + tile textures.
//   • network-first with stale-while-revalidate fallback for the
//     `/api/games/public` lobby browse endpoint so users with a dead
//     connection still see the last-known game list (the lobby
//     overlays a `pwa-offline-banner` while this happens).
//   • network-only for everything else under `/api/` and `/hubs/`
//     (live data must always go to the wire).
//
// Phase K Wave 3 — Install-time pre-cache.
// `scripts/generate-sw-manifest.js` emits `manifest-precache.json`
// after every parcel build.  The install handler fetches it and
// pre-warms `STATIC_CACHE` with the eager lobby chain (autotable-src.
// {js,css}, game-bootstrap.{js}, PWA icons, index.html).  A returning
// user with a flaky network now sees a cached lobby on page-load
// rather than a spinner.

const CACHE_VERSION = 'autotable-unity-v1';
const STATIC_CACHE = `${CACHE_VERSION}-static`;
const LOBBY_CACHE = `${CACHE_VERSION}-lobby`;
const PRECACHE_MANIFEST_URL = 'manifest-precache.json';

const HASHED_ASSET_RE = /\.[0-9a-f]{8}\.(?:js|css|png|jpg|jpeg|svg|woff2?|otf|ttf|wav|mp4|glb)$/i;

self.addEventListener('install', (event) => {
  // Phase K Wave 3 — Fetch the precache manifest emitted by
  // `scripts/generate-sw-manifest.js` and warm the static cache with
  // the assets it lists.  Missing manifest = degrade gracefully
  // (first navigations hydrate the cache organically, as in Wave 2).
  event.waitUntil((async () => {
    try {
      const resp = await fetch(PRECACHE_MANIFEST_URL, { cache: 'no-store' });
      if (resp.ok) {
        const manifest = await resp.json();
        if (manifest && Array.isArray(manifest.assets) && manifest.assets.length > 0) {
          const cache = await caches.open(STATIC_CACHE);
          // `cache.addAll` is atomic — if any request 404s, none land.
          // Filter out anything that 404s on a HEAD probe so a single
          // stale entry doesn't fail the whole install.
          const reachable = [];
          for (const url of manifest.assets) {
            try {
              const head = await fetch(url, { method: 'HEAD' });
              if (head.ok) reachable.push(url);
            } catch (_e) { /* skip */ }
          }
          if (reachable.length > 0) {
            await cache.addAll(reachable);
          }
        }
      }
    } catch (_e) {
      // Manifest fetch failed (network / 404).  Wave-2 behaviour
      // takes over: the cache hydrates organically as users navigate.
    }
    await self.skipWaiting();
  })());
});

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.map((key) => {
      if (key.startsWith('autotable-') && !key.startsWith(CACHE_VERSION)) {
        return caches.delete(key);
      }
      return Promise.resolve();
    }));
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;

  // Skip the service worker itself + manifest (Chrome refetches the
  // manifest on every install — we never want a stale one).
  if (url.pathname.endsWith('/sw.js')) return;
  if (url.pathname.endsWith('/manifest.webmanifest')) return;
  // Phase K Wave 3 — the precache manifest is also always fetched
  // fresh so the next install cycle picks up the latest hashed names.
  if (url.pathname.endsWith('/manifest-precache.json')) return;
  // Unity manifest/loader/framework/data/wasm must never reuse a previous build's SW cache.
  if (url.pathname.startsWith('/autotable/unity/')) return;

  // Lobby browse fallback — network-first, cache-on-success.
  if (url.pathname === '/api/games/public' || url.pathname.startsWith('/api/games/public?')) {
    event.respondWith(networkFirst(req, LOBBY_CACHE));
    return;
  }

  // Hashed parcel artefacts + img directory: cache-first, never expire.
  if (HASHED_ASSET_RE.test(url.pathname) || url.pathname.startsWith('/img/')) {
    event.respondWith(cacheFirst(req, STATIC_CACHE));
    return;
  }

  // Live API + hub traffic — network-only.  Don't even attempt cache,
  // so a stale `/api/auth/me` never lets an unauthenticated client
  // appear authenticated.
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/')) {
    return;
  }

  // HTML / everything else — network-first with a cached fallback so
  // the bundle still boots offline (returning a cached index.html lets
  // the SPA shell render its own offline banner).
  if (req.mode === 'navigate' || req.destination === 'document') {
    event.respondWith(networkFirst(req, STATIC_CACHE));
    return;
  }
});

async function cacheFirst(req, cacheName) {
  const cache = await caches.open(cacheName);
  const hit = await cache.match(req);
  if (hit !== undefined) {
    // Refresh in the background — best-effort, ignore failures.
    fetch(req).then((resp) => {
      if (resp.ok) cache.put(req, resp.clone());
    }).catch(() => { /* offline */ });
    return hit;
  }
  try {
    const resp = await fetch(req);
    if (resp.ok) await cache.put(req, resp.clone());
    return resp;
  } catch (err) {
    // Last-chance cache lookup with a fuzzier match (ignoring search).
    const fallback = await cache.match(req, { ignoreSearch: true });
    if (fallback !== undefined) return fallback;
    throw err;
  }
}

async function networkFirst(req, cacheName) {
  const cache = await caches.open(cacheName);
  try {
    const resp = await fetch(req);
    if (resp.ok) await cache.put(req, resp.clone());
    return resp;
  } catch (err) {
    const hit = await cache.match(req);
    if (hit !== undefined) return hit;
    // Synthesise a 503 for the lobby endpoint so the frontend can
    // surface "(offline)" rather than throwing.
    return new Response(JSON.stringify({ games: [], offline: true }), {
      status: 503,
      statusText: 'Offline',
      headers: { 'Content-Type': 'application/json' },
    });
  }
}

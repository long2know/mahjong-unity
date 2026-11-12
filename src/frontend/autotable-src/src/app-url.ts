/** Public mount inferred from the canonical /autotable/ page, not a Vite build-time setting. */
function baseFrom(href: string): string {
  const path = new URL(href).pathname;
  const at = path.lastIndexOf('/autotable');
  return at >= 0 && (path.length === at + 10 || path[at + 10] === '/')
    ? encodeURI(decodeURIComponent(path.slice(0, at))) : '';
}
const initialHref = typeof window === 'undefined' ? 'http://localhost/' :
  window.location?.href ?? `${window.location?.origin ?? 'http://localhost'}${window.location?.pathname ?? '/'}`;
const mountedBase = baseFrom(initialHref);
export function appBasePath(href?: string): string { return href === undefined ? mountedBase : baseFrom(href); }

/** Resolve a logical site-root application path; absolute/external and relative URLs stay unchanged. */
export function appUrl(path: string): string {
  return path.startsWith('/') && !path.startsWith('//') ? appBasePath() + path : path;
}

export function appWebSocketUrl(path: string): string {
  const url = new URL(appUrl(path), window.location.origin);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  return url.href;
}

/** Local alias, not a global fetch monkeypatch. Request/absolute URL callers retain their explicit target. */
export function appFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  return globalThis.fetch(typeof input === 'string' ? appUrl(input) : input, init);
}

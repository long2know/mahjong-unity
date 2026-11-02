// Frontend Sentry bootstrap — Phase J Wave 8 (Apone).
//
// Sentry is loaded lazily and only initialised when:
//   - an `<meta name="sentry-dsn" content="…">` tag is present in
//     index.html with a non-empty value, OR
//   - `window.__SENTRY_DSN__` is set before this module runs.
//
// With no DSN the SDK is never imported (Parcel still bundles
// `@sentry/browser` because it's an `import`, but `init` is gated by
// the DSN check) and no network I/O is performed.  This matches the
// backend's `Sentry:Dsn`-empty-is-a-no-op contract.
//
// What we capture:
//   - Uncaught exceptions (`window.onerror`)
//   - Unhandled promise rejections (`unhandledrejection`)
//   - Optional manual breadcrumbs via `recordSentryBreadcrumb` / events
//     via `captureSentryError` (helpers re-exported below).
//
// What we DO NOT capture:
//   - Console logs (too chatty; ChatTele/move-log already streams these
//     to the backend if needed).
//   - User input (no DOM event auto-breadcrumbs — the default
//     BrowserTracing integration is intentionally off).
//
// PII redaction:
//   - `sendDefaultPii: false` (Sentry default; explicitly pinned here)
//   - `mahjong_pid` cookie is HttpOnly so JS cannot read it; instead
//     we tag the event with the SHA-256 of `localStorage["mahjong.identity.onboarded.v1"]`
//     when present, which is the same anonymous identifier used in
//     /api/identity correlation but never the raw value.

import type { BrowserOptions } from '@sentry/browser';

declare global {
  interface Window {
    __SENTRY_DSN__?: string;
    __SENTRY_ENV__?: string;
    __SENTRY_RELEASE__?: string;
    __MAHJONG_SENTRY_READY__?: boolean;
  }
}

const PID_LOCAL_STORAGE_KEY = 'mahjong.identity.onboarded.v1';

function readMeta(name: string): string | null {
  const el = document.querySelector(`meta[name="${name}"]`);
  if (!el) return null;
  const content = (el as HTMLMetaElement).content ?? '';
  return content.trim() === '' ? null : content.trim();
}

function resolveDsn(): string | null {
  if (typeof window !== 'undefined' && typeof window.__SENTRY_DSN__ === 'string' && window.__SENTRY_DSN__.trim() !== '') {
    return window.__SENTRY_DSN__.trim();
  }
  return readMeta('sentry-dsn');
}

function resolveEnvironment(): string {
  if (typeof window !== 'undefined' && typeof window.__SENTRY_ENV__ === 'string' && window.__SENTRY_ENV__.trim() !== '') {
    return window.__SENTRY_ENV__.trim();
  }
  return readMeta('sentry-environment') ?? 'production';
}

function resolveRelease(): string | undefined {
  if (typeof window !== 'undefined' && typeof window.__SENTRY_RELEASE__ === 'string' && window.__SENTRY_RELEASE__.trim() !== '') {
    return window.__SENTRY_RELEASE__.trim();
  }
  return readMeta('sentry-release') ?? undefined;
}

async function hashAnonymousPid(): Promise<string | null> {
  try {
    const raw = window.localStorage.getItem(PID_LOCAL_STORAGE_KEY);
    if (!raw) return null;
    const buf = new TextEncoder().encode(raw);
    const digest = await crypto.subtle.digest('SHA-256', buf);
    const hex = Array.from(new Uint8Array(digest)).map(b => b.toString(16).padStart(2, '0')).join('');
    return hex.substring(0, 16);
  } catch {
    return null;
  }
}

let sentryModule: typeof import('@sentry/browser') | null = null;

export async function initSentry(): Promise<boolean> {
  const dsn = resolveDsn();
  if (!dsn) {
    return false;
  }
  if (window.__MAHJONG_SENTRY_READY__) {
    return true;
  }

  const Sentry = await import('@sentry/browser');
  sentryModule = Sentry;

  const options: BrowserOptions = {
    dsn,
    environment: resolveEnvironment(),
    release: resolveRelease(),
    sendDefaultPii: false,
    tracesSampleRate: 0,
    sampleRate: 1.0,
    autoSessionTracking: false,
    defaultIntegrations: false,
    integrations: [
      Sentry.globalHandlersIntegration({ onerror: true, onunhandledrejection: true }),
      Sentry.dedupeIntegration(),
      Sentry.functionToStringIntegration(),
      Sentry.linkedErrorsIntegration(),
      Sentry.inboundFiltersIntegration(),
    ],
    beforeSend(event) {
      // Strip raw URLs of any query string that may carry the rejoin
      // token (`?rejoin=…`) — the token is short-lived but still
      // sensitive enough to keep out of the issue tracker.
      if (event.request?.url) {
        try {
          const u = new URL(event.request.url);
          if (u.searchParams.has('rejoin')) {
            u.searchParams.set('rejoin', '[redacted]');
            event.request.url = u.toString();
          }
        } catch {
          // ignore — leave the URL untouched if it's not parseable
        }
      }
      return event;
    },
  };

  Sentry.init(options);

  hashAnonymousPid().then(hash => {
    if (hash) {
      Sentry.setUser({ id: `anon:${hash}` });
    }
  });

  window.__MAHJONG_SENTRY_READY__ = true;
  return true;
}

export function captureSentryError(err: unknown, context?: Record<string, unknown>): void {
  if (!sentryModule || !window.__MAHJONG_SENTRY_READY__) return;
  if (context) {
    sentryModule.withScope(scope => {
      for (const [k, v] of Object.entries(context)) {
        scope.setExtra(k, v);
      }
      sentryModule!.captureException(err);
    });
    return;
  }
  sentryModule.captureException(err);
}

export function recordSentryBreadcrumb(category: string, message: string, data?: Record<string, unknown>): void {
  if (!sentryModule || !window.__MAHJONG_SENTRY_READY__) return;
  sentryModule.addBreadcrumb({
    category,
    message,
    data,
    level: 'info',
  });
}

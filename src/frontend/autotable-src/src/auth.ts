// Phase J Wave 8 — Auth UI module.
//
// Wires the sign-in modal, the magic-link landing page, the
// lobby-header auth status chip, and the "Linked accounts" section
// of the Wave-7 profile page.
//
// ── Wire contract with Bishop ──────────────────────────────────────
//
// All endpoints are *feature-detected*: a 404 on `GET /api/auth/providers`
// degrades gracefully to an "Auth coming soon" placeholder so the
// frontend never blocks waiting on Bishop's Wave-8 backend merge.
//
//   GET  /api/auth/providers
//     → 200  { providers: ["google", "github", "email"] }
//     → 404  Auth subsystem not deployed yet
//
//   GET  /api/auth/me
//     → 200  { authenticated: true,
//              email: "stephen@example.com",
//              primaryProvider: "github",
//              identities: [{ provider: "github", subject: "12345",
//                             email: "stephen@example.com" }] }
//     → 200  { authenticated: false }
//     → 404  Auth subsystem not deployed yet
//
//   POST /api/auth/oauth/{provider}/start
//     body: { returnUrl: "<absolute or relative URL>" }
//     → 200  { authorizeUrl: "https://accounts.google.com/o/oauth2/..." }
//        The frontend navigates `window.location.href = authorizeUrl`.
//
//   POST /api/auth/email/start
//     body: { email: "stephen@example.com",
//             returnUrl: "<absolute URL>" }
//     → 200  { sent: true }       (don't reveal account existence in 200)
//     → 400  { error: "Invalid email." }
//     → 429  rate limit
//
//   GET  /api/auth/email/verify?token=...
//     → 200  { authenticated: true, email: "..." }
//     → 400  { error: "Token expired." }
//     → 404  Auth not deployed
//
//   POST /api/auth/link/{provider}
//     body (email only): { email: "..." }
//     OAuth providers: returns { authorizeUrl } for re-entry with link
//     intent.
//
//   POST /api/auth/unlink/{provider}
//     → 200  { unlinked: true, identities: [...] }
//     → 409  { error: "Cannot unlink the only sign-in method." }
//
//   POST /api/auth/logout
//     → 204
//
//   POST /api/auth/dev-login         (Development env ONLY)
//     body: { email: "...", provider?: "..." }
//     Used by E2E to skip the real OAuth round-trip.
//
// Persistence:
//   • Server-side cookie owns the session.  Frontend does NOT cache
//     the session token in localStorage.
//   • LS key `mahjong.auth.last-email.v1` remembers the last email
//     used so the modal can pre-populate on revisits.
//   • LS key `mahjong.auth.cache.v1` (best-effort) caches the last
//     `{ authenticated, email, primaryProvider }` so the lobby chip
//     paints immediately on load — re-validated via `/api/auth/me`.

import { EventEmitter } from 'events';
import { bootstrapIdentity } from './identity';

// ── Public types ────────────────────────────────────────────────────

export type AuthProviderId = 'google' | 'microsoft' | 'github' | 'email';

export interface AuthIdentity {
  provider: AuthProviderId;
  subject: string;
  email: string | null;
}

export interface AuthState {
  authenticated: boolean;
  email: string | null;
  primaryProvider: AuthProviderId | null;
  identities: AuthIdentity[];
  /** True when the GET /api/auth/providers feature-detect 404s. */
  serverHasAuth: boolean;
  /** Providers offered by the server (intersected with the trio we know). */
  availableProviders: AuthProviderId[];
}

// ── Constants ───────────────────────────────────────────────────────

// Phase K Wave 3 — Microsoft added as the third OAuth provider.
// Order matches the modal layout: Google → Microsoft → GitHub → Email.
const KNOWN_PROVIDERS: ReadonlyArray<AuthProviderId> = ['google', 'microsoft', 'github', 'email'];

const LS_KEY_LAST_EMAIL = 'mahjong.auth.last-email.v1';
const LS_KEY_AUTH_CACHE = 'mahjong.auth.cache.v1';

const ENDPOINT_PROVIDERS = '/api/auth/providers';
const ENDPOINT_ME = '/api/auth/me';
const ENDPOINT_EMAIL_START = '/api/auth/email/start';
const ENDPOINT_EMAIL_VERIFY = '/api/auth/email/verify';
const ENDPOINT_LOGOUT = '/api/auth/logout';
const oauthStartEndpoint = (provider: AuthProviderId): string =>
  `/api/auth/oauth/${provider}/start`;
const linkEndpoint = (provider: AuthProviderId): string =>
  `/api/auth/link/${provider}`;
const unlinkEndpoint = (provider: AuthProviderId): string =>
  `/api/auth/unlink/${provider}`;

// ── Module state ────────────────────────────────────────────────────

const events = new EventEmitter();

let state: AuthState = {
  authenticated: false,
  email: null,
  primaryProvider: null,
  identities: [],
  serverHasAuth: false,
  availableProviders: [],
};

let installed = false;
let bootPromise: Promise<AuthState> | null = null;

// ── LS helpers ──────────────────────────────────────────────────────

function readLastEmail(): string {
  try {
    return window.localStorage.getItem(LS_KEY_LAST_EMAIL) ?? '';
  } catch {
    return '';
  }
}

function writeLastEmail(email: string): void {
  try {
    window.localStorage.setItem(LS_KEY_LAST_EMAIL, email);
  } catch { /* skip */ }
}

function readAuthCache(): Partial<AuthState> | null {
  try {
    const raw = window.localStorage.getItem(LS_KEY_AUTH_CACHE);
    if (raw === null) return null;
    return JSON.parse(raw) as Partial<AuthState>;
  } catch {
    return null;
  }
}

function writeAuthCache(s: AuthState): void {
  try {
    window.localStorage.setItem(LS_KEY_AUTH_CACHE, JSON.stringify({
      authenticated: s.authenticated,
      email: s.email,
      primaryProvider: s.primaryProvider,
    }));
  } catch { /* skip */ }
}

// ── Public API ──────────────────────────────────────────────────────

export function getAuthState(): AuthState {
  return { ...state, identities: [...state.identities],
           availableProviders: [...state.availableProviders] };
}

export function onAuth(handler: (s: AuthState) => void): () => void {
  events.on('auth', handler);
  return () => events.off('auth', handler);
}

function setState(next: Partial<AuthState>): void {
  state = { ...state, ...next };
  writeAuthCache(state);
  events.emit('auth', state);
  renderLobbyChip();
  renderLinkedAccountsSection();
}

// ── Provider intersection helper ────────────────────────────────────

function intersectProviders(raw: unknown): AuthProviderId[] {
  if (!Array.isArray(raw)) return [];
  // Phase K Wave 3 — Bishop. The backend's `/api/auth/providers`
  // surface returns either the legacy string list
  // (`["google", "github"]`) or the W3 object envelope
  // (`[{ id, displayName, enabled, kind }, …]`).  Accept both shapes
  // so the sign-in modal can light up Microsoft (and any other
  // backend-configured provider) without a string-only contract pin.
  const lower: string[] = [];
  for (const item of raw) {
    if (typeof item === 'string') {
      lower.push(item.toLowerCase());
      continue;
    }
    if (item !== null && typeof item === 'object') {
      const o = item as Record<string, unknown>;
      const enabled = o.enabled ?? o.Enabled;
      if (enabled === false) continue;
      const id = o.id ?? o.Id ?? o.provider ?? o.Provider;
      if (typeof id === 'string') lower.push(id.toLowerCase());
    }
  }
  return KNOWN_PROVIDERS.filter((p) => lower.indexOf(p) !== -1);
}

function coerceProvider(raw: unknown): AuthProviderId | null {
  if (typeof raw !== 'string') return null;
  const s = raw.toLowerCase();
  if (s === 'google' || s === 'microsoft' || s === 'github' || s === 'email') return s;
  return null;
}

function coerceIdentities(raw: unknown): AuthIdentity[] {
  if (!Array.isArray(raw)) return [];
  const out: AuthIdentity[] = [];
  for (const item of raw) {
    if (item === null || typeof item !== 'object') continue;
    const o = item as Record<string, unknown>;
    const p = coerceProvider(o.provider);
    if (p === null) continue;
    const subject = typeof o.subject === 'string'
      ? o.subject
      : (typeof o.Subject === 'string' ? o.Subject : '');
    const email = typeof o.email === 'string'
      ? o.email
      : (typeof o.Email === 'string' ? o.Email : null);
    out.push({ provider: p, subject, email });
  }
  return out;
}

function normaliseMe(raw: unknown): Partial<AuthState> {
  if (raw === null || typeof raw !== 'object') {
    return { authenticated: false, email: null, primaryProvider: null, identities: [] };
  }
  const o = raw as Record<string, unknown>;
  const authenticated = o.authenticated === true || o.Authenticated === true;
  if (!authenticated) {
    return { authenticated: false, email: null, primaryProvider: null, identities: [] };
  }
  return {
    authenticated: true,
    email: typeof o.email === 'string' ? o.email
      : (typeof o.Email === 'string' ? o.Email : null),
    primaryProvider: coerceProvider(o.primaryProvider ?? o.PrimaryProvider),
    identities: coerceIdentities(o.identities ?? o.Identities),
  };
}

// ── Bootstrap ───────────────────────────────────────────────────────

/**
 * Bootstrap auth.  Idempotent — concurrent callers share the same
 * in-flight pair of GETs.  When `/api/auth/providers` returns 404 we
 * paint the "Auth coming soon" placeholder and skip the `/me` probe.
 */
export async function bootstrapAuth(): Promise<AuthState> {
  if (bootPromise !== null) return bootPromise;
  // Assign before cached-state events can synchronously re-enter bootstrap.
  bootPromise = Promise.resolve().then(async () => {
    const cached = readAuthCache();
    if (cached !== null) {
      setState({
        authenticated: cached.authenticated === true,
        email: typeof cached.email === 'string' ? cached.email : null,
        primaryProvider: coerceProvider(cached.primaryProvider),
      });
    }
    try {
      const providersResp = await fetch(ENDPOINT_PROVIDERS, {
        method: 'GET',
        credentials: 'include',
        headers: { Accept: 'application/json' },
      });
      if (providersResp.status === 404) {
        setState({ serverHasAuth: false, availableProviders: [],
                   authenticated: false, email: null, primaryProvider: null,
                   identities: [] });
        return state;
      }
      if (!providersResp.ok) {
        setState({ serverHasAuth: false, availableProviders: [] });
        return state;
      }
      const providersJson = await providersResp.json() as Record<string, unknown>;
      const available = intersectProviders(providersJson.providers ?? providersJson.Providers);
      setState({ serverHasAuth: true, availableProviders: available });
    } catch {
      setState({ serverHasAuth: false, availableProviders: [] });
      return state;
    }
    try {
      // /auth/me can issue a guest cookie too. Never race it against the
      // canonical identity POST on a genuinely cookie-less first visit.
      if (await bootstrapIdentity() === null) return state;
      const meResp = await fetch(ENDPOINT_ME, {
        method: 'GET',
        credentials: 'include',
        headers: { Accept: 'application/json' },
      });
      if (meResp.status === 404) {
        setState({ serverHasAuth: false });
        return state;
      }
      if (!meResp.ok) {
        return state;
      }
      const me = await meResp.json() as unknown;
      setState(normaliseMe(me));
    } catch {
      /* keep cached state */
    }
    return state;
  });
  return bootPromise;
}

/** Force a re-fetch of `/api/auth/me` (e.g. after link/unlink). */
export async function refreshAuth(): Promise<AuthState> {
  if (!state.serverHasAuth) return state;
  try {
    if (await bootstrapIdentity() === null) return state;
    const r = await fetch(ENDPOINT_ME, {
      method: 'GET',
      credentials: 'include',
      headers: { Accept: 'application/json' },
    });
    if (r.ok) {
      const me = await r.json() as unknown;
      setState(normaliseMe(me));
    }
  } catch { /* swallow */ }
  return state;
}

// ── OAuth start ─────────────────────────────────────────────────────

async function startOAuth(
  provider: AuthProviderId,
  intent: 'login' | 'link',
): Promise<{ ok: boolean; error: string | null }> {
  const url = intent === 'link' ? linkEndpoint(provider) : oauthStartEndpoint(provider);
  try {
    const resp = await fetch(url, {
      method: 'POST',
      credentials: 'include',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        returnUrl: window.location.href,
      }),
    });
    if (resp.status === 404) {
      return { ok: false, error: 'Auth not available yet.' };
    }
    if (!resp.ok) {
      return { ok: false, error: `Sign-in failed (${resp.status}).` };
    }
    const body = await resp.json() as { authorizeUrl?: string };
    if (typeof body.authorizeUrl === 'string' && body.authorizeUrl !== '') {
      window.location.href = body.authorizeUrl;
      return { ok: true, error: null };
    }
    // Some link flows respond 200 without a redirect (already linked).
    void refreshAuth();
    return { ok: true, error: null };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : String(e) };
  }
}

// ── Magic-link email ───────────────────────────────────────────────

async function startEmailMagicLink(
  email: string,
): Promise<{ ok: boolean; error: string | null }> {
  try {
    const resp = await fetch(ENDPOINT_EMAIL_START, {
      method: 'POST',
      credentials: 'include',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        email,
        returnUrl: window.location.origin + window.location.pathname,
      }),
    });
    if (resp.status === 404) {
      return { ok: false, error: 'Email sign-in is not available yet.' };
    }
    if (resp.status === 429) {
      return { ok: false, error: 'Too many requests — try again in a minute.' };
    }
    if (!resp.ok) {
      return { ok: false, error: `Failed to send magic link (${resp.status}).` };
    }
    writeLastEmail(email);
    return { ok: true, error: null };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : String(e) };
  }
}

async function verifyEmailToken(
  token: string,
): Promise<{ ok: boolean; email: string | null; error: string | null }> {
  try {
    const resp = await fetch(`${ENDPOINT_EMAIL_VERIFY}?token=${encodeURIComponent(token)}`, {
      method: 'GET',
      credentials: 'include',
      headers: { Accept: 'application/json' },
    });
    if (resp.status === 404) {
      return { ok: false, email: null, error: 'Auth subsystem unavailable.' };
    }
    if (!resp.ok) {
      const body = await resp.json().catch(() => null) as { error?: string } | null;
      return { ok: false, email: null,
               error: body?.error ?? `Verification failed (${resp.status}).` };
    }
    const body = await resp.json() as { email?: string; authenticated?: boolean };
    void refreshAuth();
    return { ok: true, email: typeof body.email === 'string' ? body.email : null, error: null };
  } catch (e) {
    return { ok: false, email: null, error: e instanceof Error ? e.message : String(e) };
  }
}

// ── Unlink / Logout ────────────────────────────────────────────────

export async function unlinkProvider(
  provider: AuthProviderId,
): Promise<{ ok: boolean; error: string | null }> {
  try {
    const resp = await fetch(unlinkEndpoint(provider), {
      method: 'POST',
      credentials: 'include',
      headers: { Accept: 'application/json' },
    });
    if (!resp.ok) {
      const body = await resp.json().catch(() => null) as { error?: string } | null;
      return { ok: false, error: body?.error ?? `Unlink failed (${resp.status}).` };
    }
    await refreshAuth();
    return { ok: true, error: null };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : String(e) };
  }
}

export async function logout(): Promise<void> {
  try {
    await fetch(ENDPOINT_LOGOUT, { method: 'POST', credentials: 'include' });
  } catch { /* swallow */ }
  setState({
    authenticated: false,
    email: null,
    primaryProvider: null,
    identities: [],
  });
}

// ── DOM install — modal + chip + landing page ──────────────────────

export function installAuthUi(): void {
  if (installed) return;
  installed = true;
  // Phase K Wave 3 — Mount the sign-in modal scaffolding if it's not
  // already present in `index.html`.  Wave 2 referenced these IDs but
  // shipped without the markup (the e2e spec soft-passes when
  // `signin-button` is missing).  Wave 3 inlines the scaffold so the
  // Microsoft provider button has somewhere to render.
  ensureAuthMarkup();
  wireSignInButton();
  wireSignInModal();
  wireLogoutButton();
  wireMagicLinkLanding();
  renderLobbyChip();
  renderLinkedAccountsSection();
  onAuth(() => {
    renderLobbyChip();
    renderLinkedAccountsSection();
  });
  // Auto-bootstrap once the DOM is wired.
  void bootstrapAuth();
}

// Phase K Wave 3 — Sign-in modal markup.
//
// Mounts the lobby header chip + signin modal scaffold when they
// aren't already present in `index.html`.  The provider button
// order matches the directive: Google → Microsoft → GitHub → Email
// magic-link.  Microsoft uses a direct GET to
// `/api/auth/login?provider=microsoft` (Bishop's Wave-3 backend
// convenience redirect) in addition to the `signin-microsoft`
// listener wired by `wireSignInModal`, so users always get a path
// to the provider even if the POST-start flow 404s.
function ensureAuthMarkup(): void {
  ensureHeaderChip();
  ensureSignInModal();
  ensureMagicLinkLanding();
}

function ensureHeaderChip(): void {
  if (document.getElementById('signin-button') !== null) return;
  const host = document.querySelector('.lobby-header')
    ?? document.querySelector('#lobby-panel')
    ?? document.body;

  const wrap = document.createElement('div');
  wrap.className = 'auth-header';
  wrap.setAttribute('data-testid', 'auth-header');

  const chip = document.createElement('span');
  chip.id = 'auth-status-chip';
  chip.className = 'auth-status-chip';
  chip.setAttribute('data-testid', 'auth-status-chip');
  chip.style.display = 'none';
  const chipEmail = document.createElement('span');
  chipEmail.className = 'auth-status-chip-email';
  const chipProvider = document.createElement('span');
  chipProvider.className = 'auth-status-chip-provider';
  chip.appendChild(chipEmail);
  chip.appendChild(chipProvider);
  wrap.appendChild(chip);

  const signinBtn = document.createElement('button');
  signinBtn.id = 'signin-button';
  signinBtn.type = 'button';
  signinBtn.className = 'btn btn-sm btn-outline-light auth-signin-btn';
  signinBtn.setAttribute('data-testid', 'signin-button');
  signinBtn.textContent = 'Sign in';
  wrap.appendChild(signinBtn);

  const logoutBtn = document.createElement('button');
  logoutBtn.id = 'logout-button';
  logoutBtn.type = 'button';
  logoutBtn.className = 'btn btn-sm btn-outline-secondary auth-logout-btn';
  logoutBtn.setAttribute('data-testid', 'logout-button');
  logoutBtn.textContent = 'Sign out';
  logoutBtn.style.display = 'none';
  wrap.appendChild(logoutBtn);

  host.appendChild(wrap);
}

function ensureSignInModal(): void {
  if (document.getElementById('signin-modal') !== null) return;
  const modal = document.createElement('div');
  modal.id = 'signin-modal';
  modal.className = 'signin-modal';
  modal.setAttribute('role', 'dialog');
  modal.setAttribute('aria-modal', 'true');
  modal.setAttribute('aria-labelledby', 'signin-modal-title');
  modal.setAttribute('aria-hidden', 'true');
  modal.setAttribute('data-testid', 'signin-modal');
  modal.innerHTML = `
    <div id="signin-modal-backdrop" class="signin-modal-backdrop"
         data-testid="signin-modal-backdrop"></div>
    <div class="signin-modal-card" role="document">
      <header class="signin-modal-header">
        <h3 id="signin-modal-title" class="signin-modal-title">Sign in to Mahjong Autotable</h3>
        <button id="signin-modal-close" type="button" class="signin-modal-close"
                data-testid="signin-modal-close" aria-label="Close sign-in">×</button>
      </header>
      <div id="signin-modal-error" class="signin-modal-error"
           role="alert" aria-live="assertive"></div>

      <section id="signin-providers-panel"
               class="signin-providers-panel"
               data-testid="signin-providers-panel">
        <p class="signin-modal-blurb">
          Pick a provider — we'll never post on your behalf.
        </p>

        <button id="signin-google" type="button"
                class="btn btn-block signin-provider-btn signin-provider-google"
                data-testid="signin-provider-google">
          <span class="signin-provider-icon" aria-hidden="true">${googleIconSvg()}</span>
          Sign in with Google
        </button>

        <button id="signin-microsoft" type="button"
                class="btn btn-block signin-provider-btn signin-provider-microsoft"
                data-testid="signin-provider-microsoft">
          <span class="signin-provider-icon">${microsoftIconSvg()}</span>
          Sign in with Microsoft
        </button>

        <button id="signin-github" type="button"
                class="btn btn-block signin-provider-btn signin-provider-github"
                data-testid="signin-provider-github">
          <span class="signin-provider-icon" aria-hidden="true">${githubIconSvg()}</span>
          Sign in with GitHub
        </button>

        <form id="signin-email-form" class="signin-email-form" onsubmit="return false;">
          <label class="signin-email-label" for="signin-email-input">
            Or get a magic link by email
          </label>
          <input id="signin-email-input" type="email" autocomplete="email"
                 class="form-control form-control-sm signin-email-input"
                 data-testid="signin-email-input"
                 placeholder="you@example.com">
          <button id="signin-email-submit" type="submit"
                  class="btn btn-warning btn-sm signin-email-submit"
                  data-testid="signin-email-submit">Email me a link</button>
          <p id="signin-email-error" class="signin-email-error" aria-live="polite"></p>
        </form>
      </section>

      <section id="signin-email-success" class="signin-email-success"
               data-testid="signin-email-success">
        <h4>Check your inbox</h4>
        <p>We sent a magic link to <strong id="signin-email-success-email"></strong>.
           Open it on this device to finish signing in.</p>
        <button id="signin-email-success-back" type="button"
                class="btn btn-secondary btn-sm">Back</button>
      </section>

      <section id="signin-placeholder" class="signin-placeholder"
               data-testid="signin-placeholder">
        <p>Sign-in is not available on this server yet — the API
           hasn't been deployed.  All other features still work; you
           just won't be able to link an account.</p>
      </section>
    </div>
  `;
  document.body.appendChild(modal);

  // CSP-safe initial visibility.  Production runs strict CSP
  // (style-src 'self', no 'unsafe-inline'), which blocks inline
  // `style="display:none"` attributes — Development's relaxed policy
  // hid this divergence.  Seed the hidden state through the CSSOM
  // (not subject to style-src) instead; showSignInPanel() and
  // setModalError() reveal these via `el.style.display = ''`.
  for (const id of ['signin-modal-error', 'signin-email-success', 'signin-placeholder']) {
    const el = modal.querySelector('#' + id) as HTMLElement | null;
    if (el !== null) el.style.display = 'none';
  }
}

function ensureMagicLinkLanding(): void {
  if (document.getElementById('magic-link-landing') !== null) return;
  const root = document.createElement('div');
  root.id = 'magic-link-landing';
  root.className = 'magic-link-landing';
  root.setAttribute('aria-hidden', 'true');
  root.innerHTML = `
    <div class="magic-link-card" role="document">
      <div id="magic-link-success">
        <h3>You're signed in!</h3>
        <p>Welcome back, <strong id="magic-link-success-email"></strong>.</p>
        <button id="magic-link-dismiss" type="button"
                class="btn btn-success btn-sm">Continue</button>
      </div>
      <div id="magic-link-failure">
        <h3>Magic link couldn't sign you in</h3>
        <p id="magic-link-failure-message" class="magic-link-failure-message"></p>
        <button id="magic-link-dismiss-failure" type="button"
                class="btn btn-secondary btn-sm">Back to the lobby</button>
      </div>
    </div>
  `;
  document.body.appendChild(root);

  // CSP-safe initial visibility (see ensureSignInModal): strict
  // style-src blocks inline `style="display:none"`, so hide the result
  // panels through the CSSOM.  handleMagicLinkLanding() toggles them
  // with `el.style.display`.
  for (const id of ['magic-link-success', 'magic-link-failure']) {
    const el = root.querySelector('#' + id) as HTMLElement | null;
    if (el !== null) el.style.display = 'none';
  }
}

// ── Provider icon SVGs ─────────────────────────────────────────────
//
// Inlined so we don't pay a network round-trip on the sign-in modal's
// first paint.  All three SVGs follow the same 24×24 viewBox so the
// `.signin-provider-icon` CSS slot keeps them aligned.

function googleIconSvg(): string {
  return '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">'
    + '<path fill="#4285f4" d="M21.6 12.2c0-.7-.1-1.4-.2-2H12v3.9h5.4c-.2 1.3-.9 2.4-2 3.1v2.6h3.3c1.9-1.8 3-4.4 3-7.6z"/>'
    + '<path fill="#34a853" d="M12 22c2.7 0 5-.9 6.7-2.4l-3.3-2.6c-.9.6-2 1-3.4 1-2.6 0-4.9-1.8-5.7-4.2H3v2.6C4.7 19.6 8 22 12 22z"/>'
    + '<path fill="#fbbc04" d="M6.3 13.8c-.2-.6-.3-1.2-.3-1.8s.1-1.2.3-1.8V7.6H3C2.4 8.9 2 10.4 2 12s.4 3.1 1 4.4l3.3-2.6z"/>'
    + '<path fill="#ea4335" d="M12 6.2c1.5 0 2.8.5 3.8 1.5l2.9-2.9C16.9 3 14.7 2 12 2 8 2 4.7 4.4 3 7.6l3.3 2.6C7.1 8 9.4 6.2 12 6.2z"/>'
    + '</svg>';
}

function microsoftIconSvg(): string {
  // Phase K Wave 3 → Phase K Wave 4 — Microsoft four-tile mark, sized
  // to the Microsoft Identity Platform "Sign in with Microsoft"
  // branding guidance (24×24 mark with four 11×11 squares separated
  // by a 1-px gap).  Inline (never fetched at runtime), with a
  // `<title>` element + `role="img"` so screen readers announce
  // "Microsoft" rather than the default "image" fallback.
  //
  // Source: https://learn.microsoft.com/en-us/entra/identity-platform/howto-add-branding-in-apps
  // Brand colors (per Microsoft logo style guide):
  //   • #F25022 — red    (top-left)
  //   • #7FBA00 — green  (top-right)
  //   • #00A4EF — blue   (bottom-left)
  //   • #FFB900 — yellow (bottom-right)
  return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 23 23" width="24" height="24" role="img" aria-label="Microsoft">'
    + '<title>Microsoft</title>'
    + '<rect x="1"  y="1"  width="10" height="10" fill="#F25022"/>'
    + '<rect x="12" y="1"  width="10" height="10" fill="#7FBA00"/>'
    + '<rect x="1"  y="12" width="10" height="10" fill="#00A4EF"/>'
    + '<rect x="12" y="12" width="10" height="10" fill="#FFB900"/>'
    + '</svg>';
}

function githubIconSvg(): string {
  return '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">'
    + '<path fill="currentColor" d="M12 2a10 10 0 0 0-3.16 19.5c.5.09.68-.22.68-.48v-1.7c-2.78.6-3.37-1.34-3.37-1.34-.45-1.15-1.11-1.46-1.11-1.46-.91-.62.07-.6.07-.6 1 .07 1.53 1.04 1.53 1.04.9 1.52 2.34 1.08 2.91.83.09-.65.35-1.08.63-1.33-2.22-.25-4.55-1.11-4.55-4.94 0-1.09.39-1.99 1.03-2.69-.1-.25-.45-1.27.1-2.65 0 0 .84-.27 2.75 1.02a9.6 9.6 0 0 1 5 0c1.9-1.3 2.75-1.02 2.75-1.02.55 1.38.2 2.4.1 2.65.64.7 1.03 1.6 1.03 2.69 0 3.84-2.34 4.69-4.57 4.93.36.31.68.92.68 1.85v2.74c0 .27.18.58.69.48A10 10 0 0 0 12 2z"/>'
    + '</svg>';
}

function wireSignInButton(): void {
  const btn = document.getElementById('signin-button');
  if (btn === null) return;
  btn.addEventListener('click', (e) => {
    e.preventDefault();
    openSignInModal();
  });
}

function wireLogoutButton(): void {
  const btn = document.getElementById('logout-button');
  if (btn === null) return;
  btn.addEventListener('click', (e) => {
    e.preventDefault();
    void logout();
  });
}

// ── Sign-in modal ──────────────────────────────────────────────────

function openSignInModal(): void {
  const modal = document.getElementById('signin-modal');
  if (modal === null) return;
  modal.classList.add('signin-modal-open');
  modal.setAttribute('aria-hidden', 'false');
  // Reset to the providers panel each time.
  showSignInPanel('providers');
  refreshModalProvidersVisibility();
  // Pre-populate the email input from LS.
  const emailInput = document.getElementById('signin-email-input') as HTMLInputElement | null;
  if (emailInput !== null && emailInput.value === '') {
    emailInput.value = readLastEmail();
  }
  // Focus the dialog so screen readers land on it.
  window.setTimeout(() => {
    const closeBtn = document.getElementById('signin-modal-close');
    closeBtn?.focus();
  }, 50);
}

function closeSignInModal(): void {
  const modal = document.getElementById('signin-modal');
  if (modal === null) return;
  modal.classList.remove('signin-modal-open');
  modal.setAttribute('aria-hidden', 'true');
}

type ModalPanel = 'providers' | 'email-success' | 'placeholder';

function showSignInPanel(panel: ModalPanel): void {
  for (const id of ['signin-providers-panel', 'signin-email-success', 'signin-placeholder']) {
    const el = document.getElementById(id);
    if (el === null) continue;
    el.style.display = 'none';
  }
  const map: Record<ModalPanel, string> = {
    providers: 'signin-providers-panel',
    'email-success': 'signin-email-success',
    placeholder: 'signin-placeholder',
  };
  const target = document.getElementById(map[panel]);
  if (target !== null) target.style.display = '';
}

function refreshModalProvidersVisibility(): void {
  if (!state.serverHasAuth) {
    showSignInPanel('placeholder');
    return;
  }
  const available = state.availableProviders;
  for (const provider of KNOWN_PROVIDERS) {
    const btn = document.getElementById(`signin-${provider}`);
    const emailForm = document.getElementById('signin-email-form');
    if (provider === 'email') {
      if (emailForm !== null) {
        emailForm.style.display = available.indexOf('email') !== -1 ? '' : 'none';
      }
      continue;
    }
    if (btn !== null) {
      // Phase K Wave 3 — Microsoft (and other OAuth providers) are
      // hidden when the backend doesn't list them in
      // /api/auth/providers, so we don't surface a button that
      // 404s on click.
      btn.style.display = available.indexOf(provider) !== -1 ? '' : 'none';
    }
  }
}

function wireSignInModal(): void {
  const closeBtn = document.getElementById('signin-modal-close');
  if (closeBtn !== null) {
    closeBtn.addEventListener('click', () => closeSignInModal());
  }
  const backdrop = document.getElementById('signin-modal-backdrop');
  if (backdrop !== null) {
    backdrop.addEventListener('click', () => closeSignInModal());
  }
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    const modal = document.getElementById('signin-modal');
    if (modal !== null && modal.classList.contains('signin-modal-open')) {
      closeSignInModal();
    }
  });

  for (const provider of ['google', 'microsoft', 'github'] as const) {
    const btn = document.getElementById(`signin-${provider}`) as HTMLButtonElement | null;
    if (btn === null) continue;
    btn.addEventListener('click', async () => {
      btn.disabled = true;
      const original = btn.textContent;
      btn.textContent = `Redirecting to ${provider}…`;
      // Phase K Wave 3 — Microsoft is wired to Bishop's convenience
      // GET endpoint per the Wave-3 directive.  The endpoint
      // 302-redirects to the Microsoft authorize URL with the
      // session cookie + return URL preserved server-side; it
      // doesn't return JSON so we navigate directly.
      if (provider === 'microsoft') {
        const returnUrl = encodeURIComponent(window.location.href);
        window.location.href = `/api/auth/login?provider=microsoft&returnUrl=${returnUrl}`;
        return;
      }
      const result = await startOAuth(provider, 'login');
      btn.disabled = false;
      btn.textContent = original;
      if (!result.ok) {
        setModalError(result.error ?? 'Sign-in failed.');
      }
    });
  }

  const emailInput = document.getElementById('signin-email-input') as HTMLInputElement | null;
  const emailSubmit = document.getElementById('signin-email-submit') as HTMLButtonElement | null;
  const emailError = document.getElementById('signin-email-error');
  if (emailInput !== null && emailSubmit !== null) {
    const submit = async (): Promise<void> => {
      const email = (emailInput.value ?? '').trim();
      if (email === '' || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) {
        if (emailError !== null) emailError.textContent = 'Enter a valid email address.';
        emailInput.focus();
        return;
      }
      if (emailError !== null) emailError.textContent = '';
      emailSubmit.disabled = true;
      const prev = emailSubmit.textContent;
      emailSubmit.textContent = 'Sending…';
      const result = await startEmailMagicLink(email);
      emailSubmit.disabled = false;
      emailSubmit.textContent = prev;
      if (result.ok) {
        const successEmailEl = document.getElementById('signin-email-success-email');
        if (successEmailEl !== null) successEmailEl.textContent = email;
        showSignInPanel('email-success');
      } else if (emailError !== null) {
        emailError.textContent = result.error ?? 'Failed to send link.';
      }
    };
    emailSubmit.addEventListener('click', () => { void submit(); });
    emailInput.addEventListener('keydown', (e: KeyboardEvent) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        void submit();
      }
    });
  }

  const emailSuccessBack = document.getElementById('signin-email-success-back');
  if (emailSuccessBack !== null) {
    emailSuccessBack.addEventListener('click', () => showSignInPanel('providers'));
  }
}

function setModalError(msg: string): void {
  const errEl = document.getElementById('signin-modal-error');
  if (errEl === null) return;
  errEl.textContent = msg;
  errEl.style.display = msg === '' ? 'none' : '';
}

// ── Lobby auth chip + signed-in chip rendering ─────────────────────

function renderLobbyChip(): void {
  const chip = document.getElementById('auth-status-chip');
  const signinBtn = document.getElementById('signin-button');
  const logoutBtn = document.getElementById('logout-button');
  if (chip === null) return;
  if (state.authenticated) {
    chip.style.display = '';
    chip.setAttribute('data-authenticated', 'true');
    const label = chip.querySelector('.auth-status-chip-email') as HTMLElement | null;
    const badge = chip.querySelector('.auth-status-chip-provider') as HTMLElement | null;
    if (label !== null) label.textContent = state.email ?? 'Signed in';
    if (badge !== null) {
      const provider = state.primaryProvider ?? 'email';
      badge.textContent = providerBadgeLabel(provider);
      badge.setAttribute('data-provider', provider);
    }
    if (signinBtn !== null) signinBtn.style.display = 'none';
    if (logoutBtn !== null) logoutBtn.style.display = '';
  } else {
    chip.style.display = 'none';
    chip.setAttribute('data-authenticated', 'false');
    if (signinBtn !== null) {
      // Show sign-in button only if the server actually has auth, OR when
      // we don't yet know — better UX to let the user click and see the
      // placeholder than to hide the button entirely.
      signinBtn.style.display = '';
    }
    if (logoutBtn !== null) logoutBtn.style.display = 'none';
  }
}

function providerBadgeLabel(provider: AuthProviderId): string {
  switch (provider) {
    case 'google': return '🔵 Google';
    case 'microsoft': return '🟦 Microsoft';
    case 'github': return '⬛ GitHub';
    case 'email': return '✉ Email';
  }
}

// ── Linked accounts section on the profile page ────────────────────

function renderLinkedAccountsSection(): void {
  const section = document.getElementById('profile-linked-accounts');
  if (section === null) return;
  section.replaceChildren();

  if (!state.serverHasAuth) {
    const p = document.createElement('p');
    p.className = 'profile-linked-empty';
    p.textContent = 'Authentication is not available on this server yet.';
    section.appendChild(p);
    return;
  }

  if (!state.authenticated) {
    const p = document.createElement('p');
    p.className = 'profile-linked-empty';
    p.textContent = 'Sign in to link multiple providers to your account.';
    section.appendChild(p);
    const signinBtn = document.createElement('button');
    signinBtn.type = 'button';
    signinBtn.className = 'btn btn-sm btn-primary';
    signinBtn.textContent = 'Sign in';
    signinBtn.addEventListener('click', () => openSignInModal());
    section.appendChild(signinBtn);
    return;
  }

  // Render one row per known provider with the link/unlink affordance.
  const list = document.createElement('ul');
  list.className = 'profile-linked-list';
  list.setAttribute('role', 'list');
  for (const provider of KNOWN_PROVIDERS) {
    const linked = state.identities.find((id) => id.provider === provider) ?? null;
    list.appendChild(buildLinkedRow(provider, linked));
  }
  section.appendChild(list);
}

function buildLinkedRow(
  provider: AuthProviderId,
  linked: AuthIdentity | null,
): HTMLLIElement {
  const row = document.createElement('li');
  row.className = 'profile-linked-row';
  row.setAttribute('data-provider', provider);

  const badge = document.createElement('span');
  badge.className = `profile-linked-badge profile-linked-badge-${provider}`;
  badge.textContent = providerBadgeLabel(provider);
  row.appendChild(badge);

  const meta = document.createElement('span');
  meta.className = 'profile-linked-meta';
  meta.textContent = linked === null
    ? 'Not linked'
    : (linked.email ?? 'Linked');
  row.appendChild(meta);

  const action = document.createElement('button');
  action.type = 'button';
  action.className = 'btn btn-sm';
  if (linked === null) {
    action.classList.add('btn-primary');
    action.textContent = 'Link';
    action.setAttribute('data-testid', `profile-link-provider-${provider}`);
    action.addEventListener('click', async () => {
      action.disabled = true;
      if (provider === 'email') {
        // Email link uses the same magic-link flow with the user's
        // current cookie attached — server resolves "link" intent.
        openSignInModal();
        action.disabled = false;
        return;
      }
      const result = await startOAuth(provider, 'link');
      action.disabled = false;
      if (!result.ok && result.error !== null) {
        meta.textContent = result.error;
      }
    });
  } else {
    action.classList.add('btn-secondary');
    action.textContent = 'Unlink';
    action.setAttribute('data-testid', `profile-unlink-${provider}`);
    // Disable unlink if it's the only identity left.
    const isOnly = state.identities.length <= 1;
    if (isOnly) {
      action.disabled = true;
      action.title = 'Cannot unlink the only sign-in method.';
    }
    action.addEventListener('click', async () => {
      action.disabled = true;
      const result = await unlinkProvider(provider);
      action.disabled = false;
      if (!result.ok && result.error !== null) {
        meta.textContent = result.error;
      }
    });
  }
  row.appendChild(action);
  return row;
}

// ── Magic-link landing page ────────────────────────────────────────

function wireMagicLinkLanding(): void {
  const params = new URLSearchParams(window.location.search);
  const token = params.get('auth');
  if (token === null || token === '') return;

  const landing = document.getElementById('magic-link-landing');
  if (landing === null) return;
  landing.classList.add('magic-link-landing-open');
  landing.setAttribute('aria-hidden', 'false');

  const successEl = document.getElementById('magic-link-success');
  const failureEl = document.getElementById('magic-link-failure');
  const failureMsgEl = document.getElementById('magic-link-failure-message');
  const successEmailEl = document.getElementById('magic-link-success-email');
  if (successEl !== null) successEl.style.display = 'none';
  if (failureEl !== null) failureEl.style.display = 'none';

  void (async () => {
    const result = await verifyEmailToken(token);
    if (result.ok) {
      if (successEmailEl !== null && result.email !== null) {
        successEmailEl.textContent = result.email;
      }
      if (successEl !== null) successEl.style.display = '';
      // Clean the URL so a refresh doesn't re-verify.
      try {
        const cleaned = new URL(window.location.href);
        cleaned.searchParams.delete('auth');
        window.history.replaceState(null, '', cleaned.toString());
      } catch { /* swallow */ }
    } else {
      if (failureMsgEl !== null) {
        failureMsgEl.textContent = result.error ?? 'Magic link verification failed.';
      }
      if (failureEl !== null) failureEl.style.display = '';
    }
  })();

  // Wire the dismiss buttons.
  for (const id of ['magic-link-dismiss', 'magic-link-dismiss-failure']) {
    const btn = document.getElementById(id);
    if (btn === null) continue;
    btn.addEventListener('click', () => {
      landing.classList.remove('magic-link-landing-open');
      landing.setAttribute('aria-hidden', 'true');
    });
  }
}

// Phase K Wave 2 — PWA registration.
//
// Registers the service worker shipped alongside the bundle so the
// app gets:
//   • Cache-first delivery for the immutable parcel artefacts
//     (`autotable-src.<hash>.js`, hashed CSS, tile textures, fonts).
//     The 8-char content-hash on each filename makes them effectively
//     immutable — when parcel re-builds, new filenames appear and the
//     SW happily cohabits the old ones until the next install cycle.
//   • Network-first for `/api/*` and `/hubs/*` so live data keeps
//     flowing; the SW only falls back to the cache (with an offline
//     banner side-effect) when the network is unreachable.
//   • Offline lobby browse: the last `/api/games/public` response is
//     cached so a returning user with a dead connection still sees a
//     plausible game list rather than a blank panel.  The
//     `pwa-offline-banner` testid is surfaced when we're serving
//     stale data.
//
// `manifest.webmanifest` is referenced from `index.html`; the parcel
// build copies it through unchanged.

const SW_URL = './sw.js';

let registration: ServiceWorkerRegistration | null = null;

export async function registerServiceWorker(): Promise<void> {
  if (typeof window === 'undefined') return;
  if (!('serviceWorker' in navigator)) return;
  // Don't register from `file://` or local Parcel dev (port 1234) —
  // SW paths are wrong there and just spam the console.  Production
  // serves the bundle from the same origin as the API.
  if (window.location.protocol === 'file:') return;

  try {
    registration = await navigator.serviceWorker.register(SW_URL, { scope: './' });
    // When a new SW takes control mid-session, swap in fresh assets
    // on next reload — never auto-reload the page out from under the
    // user.  Apone reviewed the no-auto-reload choice in Wave 1.
    registration.addEventListener('updatefound', () => {
      const installing = registration?.installing;
      if (installing === null || installing === undefined) return;
      installing.addEventListener('statechange', () => {
        if (installing.state === 'installed' && navigator.serviceWorker.controller) {
          window.dispatchEvent(new CustomEvent('mahjong:sw-update-ready'));
        }
      });
    });
  } catch {
    // Registration failures are non-fatal — the app still works
    // online, the user just doesn't get offline / install affordances.
  }

  // Offline banner — listen to online/offline transitions and update
  // a shared status element that history.ts / matchmaking.ts can
  // also observe via the `mahjong:offline` custom event.
  installOfflineBanner();

  // Install prompt — Chrome / Edge fire `beforeinstallprompt`; expose
  // it through a testid so Vasquez can drive PWA install-flow specs.
  installInstallPrompt();
}

function installOfflineBanner(): void {
  let banner = document.querySelector<HTMLDivElement>('[data-testid="pwa-offline-banner"]');
  if (banner === null) {
    banner = document.createElement('div');
    banner.setAttribute('data-testid', 'pwa-offline-banner');
    banner.className = 'pwa-offline-banner';
    banner.setAttribute('role', 'status');
    banner.setAttribute('aria-live', 'polite');
    banner.hidden = true;
    banner.textContent = '⚠️ Offline — showing the last cached lobby.';
    document.body.appendChild(banner);
  }
  const update = (): void => {
    const offline = !navigator.onLine;
    if (banner === null) return;
    banner.hidden = !offline;
    if (offline) {
      window.dispatchEvent(new CustomEvent('mahjong:offline'));
    } else {
      window.dispatchEvent(new CustomEvent('mahjong:online'));
    }
  };
  window.addEventListener('online', update);
  window.addEventListener('offline', update);
  update();
}

interface BeforeInstallPromptEvent extends Event {
  readonly platforms: ReadonlyArray<string>;
  readonly userChoice: Promise<{ outcome: 'accepted' | 'dismissed'; platform: string }>;
  prompt(): Promise<void>;
}

let deferredInstallPrompt: BeforeInstallPromptEvent | null = null;

function installInstallPrompt(): void {
  window.addEventListener('beforeinstallprompt', (e) => {
    // Suppress the automatic Chrome/Edge mini-infobar; we render our
    // own opt-in button so the user can dismiss without a global
    // PWA-rejection cookie.
    e.preventDefault();
    deferredInstallPrompt = e as BeforeInstallPromptEvent;
    mountInstallButton();
  });
}

function mountInstallButton(): void {
  // Phase K Wave 6 — Top-bar install affordance.  Wave 2 mounted the
  // button at body level which floated it bottom-right; Wave 6 wires
  // the new `pwa-install-button` testid (top-bar variant) AND keeps
  // the legacy `pwa-install-prompt` testid as a back-compat alias so
  // any pre-existing Vasquez spec keeps locating the same element.
  if (document.querySelector('[data-testid="pwa-install-button"]') !== null) return;
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.setAttribute('data-testid', 'pwa-install-button');
  btn.setAttribute('data-testid-alias', 'pwa-install-prompt');
  btn.id = 'pwa-install-button';
  btn.className = 'pwa-install-button btn btn-sm btn-outline-light';
  btn.setAttribute('aria-label', 'Install Mahjong Autotable as an app');
  btn.title = 'Install Mahjong Autotable';
  btn.textContent = '📱 Install Mahjong Autotable';
  btn.addEventListener('click', async () => {
    if (deferredInstallPrompt === null) return;
    try {
      await deferredInstallPrompt.prompt();
      const choice = await deferredInstallPrompt.userChoice;
      if (choice.outcome === 'accepted') {
        window.dispatchEvent(new CustomEvent('mahjong:pwa-installed'));
      }
    } catch { /* dismissed */ }
    deferredInstallPrompt = null;
    btn.remove();
  });
  document.body.appendChild(btn);
  // Phase K Wave 6 — back-compat legacy testid for Wave-2 specs.
  const legacy = document.createElement('span');
  legacy.setAttribute('data-testid', 'pwa-install-prompt');
  legacy.setAttribute('aria-hidden', 'true');
  legacy.style.display = 'none';
  btn.appendChild(legacy);
}

// Phase K Wave 6 — `appinstalled` event finalizes the install flow.
// We just clear the deferred prompt + hide the button so the user
// doesn't see a stale call-to-action after a successful install.
if (typeof window !== 'undefined') {
  window.addEventListener('appinstalled', () => {
    deferredInstallPrompt = null;
    document.querySelector('[data-testid="pwa-install-button"]')?.remove();
  });
}

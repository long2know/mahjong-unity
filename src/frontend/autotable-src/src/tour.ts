// Phase K Wave 1 — First-visit onboarding tour.
//
// An 8-step walkthrough gated by the LocalStorage flag
// `mahjong.tour.completed.v1` — fires once per browser, lays an
// overlay + spotlight over each step's target element, and gives the
// user prev / next / skip controls.
//
// Steps:
//   1  Welcome to Changsha Mahjong   (lobby tab strip)
//   2  Pick your seat colour + name  (profile chip)
//   3  Browse rule presets           (rule-preset selector
//                                     → falls back to variant fieldset)
//   4  Bot strength tiers            (bot difficulty fieldset)
//   5  Chat with other players       (chat panel)
//   6  Switch language anytime       (settings drawer button)
//   7  Tournament mode + ELO         (tournaments tab + rating toggle)
//   8  Done!                         (centred)
//
// The overlay is a CSS-class-driven SVG mask (no inline styles beyond
// the spotlight cutout's geometry, which is unavoidable).  Tabbing
// out / Escape closes the tour with the flag *unset* so the user can
// resume next visit.  Clicking "Skip tour" closes with the flag set.

const TOUR_LS_KEY = 'mahjong.tour.completed.v1';

interface TourStep {
  index: number; // 1-based for testids
  title: string;
  body: string;
  /** Primary anchor — first selector that resolves wins; null = centred. */
  selectors: Array<string>;
  /** Optional secondary anchor highlighted by a second outline (step 7). */
  secondarySelectors?: Array<string>;
  /** When set, the tour activates the lobby tab whose testid matches
   *  before the spotlight is painted.  Used for tournament/leaderboard
   *  steps so the highlight has somewhere to land. */
  activateTab?: 'tournaments' | 'leaderboard' | 'my';
}

const STEPS: ReadonlyArray<TourStep> = [
  {
    index: 1,
    title: 'Welcome to Changsha Mahjong',
    body: 'Quick tour: 10 stops, ~45 seconds. You can skip any time — '
      + 'we won\'t show this again unless you reset it in Settings.',
    selectors: ['[data-testid="lobby-my-game-tab"]', '#lobby-my-game-tab'],
  },
  {
    index: 2,
    title: 'Pick your seat colour & display name',
    body: 'Your profile chip lives here — open it any time to update '
      + 'your name and avatar colour.',
    selectors: ['[data-testid="lobby-open-profile"]', '#lobby-open-profile'],
  },
  {
    index: 3,
    title: 'Browse rule presets and choose a game',
    body: 'Switch between Classic Changsha and custom rule sets here. '
      + 'Sign in to save your own presets.',
    selectors: [
      '#lobby-rule-preset-select',
      '[data-testid="lobby-variant-fieldset"]',
      '#lobby-variant-fieldset',
    ],
  },
  {
    index: 4,
    title: 'Master, Hard, Medium, Easy bot tiers',
    body: 'Choose how tough the bots should play. Master uses Bishop\'s '
      + 'top-tier strategy module.',
    selectors: [
      '[data-testid="lobby-bot-difficulty-fieldset"]',
      '#lobby-bot-difficulty-fieldset',
    ],
  },
  {
    index: 5,
    title: 'Chat with other players',
    body: 'Once you\'re in a game, this panel hosts table, spectator, '
      + 'and private chats. /help lists the slash commands.',
    selectors: ['[data-testid="chat-panel"]', '#chat-panel'],
  },
  {
    // Phase K Wave 6 — voice setup walkthrough.  Highlights the mic
    // toggle that voice.ts mounts once a game URL is open.  The
    // selector falls back to a top-bar settings hint when the user
    // is still in the lobby (no mic mounted yet).
    index: 6,
    title: 'Voice chat (push-to-talk)',
    body: 'When a table has voice enabled, the mic toggle lights up '
      + 'here. Click once to join the voice room; hold Space to talk. '
      + 'You can mute or block other players from the settings drawer.',
    selectors: [
      '[data-testid="voice-mic-toggle"]',
      '#voice-mic-toggle',
      '[data-testid="settings-button"]',
      '#settings-button',
    ],
  },
  {
    index: 7,
    title: 'Switch language anytime',
    body: 'Open the settings drawer and pick a language. The lobby '
      + 'and HUD strings update immediately.',
    selectors: ['[data-testid="settings-button"]', '#settings-button'],
  },
  {
    index: 8,
    title: 'Tournament mode + ELO ratings',
    body: 'Tournament brackets live in the Tournaments tab; flip the '
      + 'Rated toggle on the Leaderboard to see season ratings.',
    selectors: ['[data-testid="lobby-tournaments-tab"]', '#lobby-tournaments-tab'],
    secondarySelectors: [
      '[data-testid="leaderboard-rating-toggle"]',
      '#leaderboard-rating-toggle',
    ],
    activateTab: 'tournaments',
  },
  {
    // Phase K Wave 6 — Tournament-view deep dive.  Walks the user
    // through what the bracket / standings panels look like once a
    // tournament is open.  Anchors on `#tournament-detail` so the
    // spotlight lands on the actual bracket viewer; selectors fall
    // back to the tab button when no tournament is selected.
    index: 9,
    title: 'Inside a tournament',
    body: 'Tournaments support single-elim, Swiss, and double-elim '
      + 'formats. Brackets, round-by-round Swiss tables, and the '
      + 'winners + losers double-elim split all live on this panel.',
    selectors: [
      '[data-testid="tournament-detail"]',
      '#tournament-detail',
      '[data-testid="lobby-tournaments-tab"]',
      '#lobby-tournaments-tab',
    ],
    activateTab: 'tournaments',
  },
  {
    index: 10,
    title: 'You\'re ready to play!',
    body: 'That\'s the tour. Quick-Match to start a game vs bots, or '
      + 'create a public lobby to find opponents.',
    selectors: [],
  },
];

interface State {
  installed: boolean;
  active: boolean;
  step: number; // 0-based step index
  root: HTMLElement | null;
  spotlight: SVGRectElement | null;
  cardEl: HTMLElement | null;
  scrollHandler: ((ev: Event) => void) | null;
  resizeHandler: ((ev: Event) => void) | null;
}

const state: State = {
  installed: false,
  active: false,
  step: 0,
  root: null,
  spotlight: null,
  cardEl: null,
  scrollHandler: null,
  resizeHandler: null,
};

// Phase K Wave 2 — Server-authoritative first-launch detection.
//
// Wave 1 read the LS flag only.  Vasquez flagged that a returning user
// on a second device would re-see the tour because the flag is per-
// browser.  Wave 2 prefers `/api/players/me/onboarding-status` —
// when it returns `{ completed: true }`, we never show the tour even
// on a fresh browser.  When the user finishes the tour we POST the
// same endpoint so the server learns about it.  LS remains the
// offline / 404 fallback so the rollout is safe to merge ahead of the
// backend.
//
// Wire contract (Bishop, Phase K Wave 2):
//   GET  /api/players/me/onboarding-status
//     → 200 { completed: boolean, completedAtUtc?: string } | 404
//   POST /api/players/me/onboarding-status
//     body: { completed: true, completedAtUtc: "<iso>" }
//     → 204 No Content | 404
//
// Both verbs are tolerant: 404 = endpoint not deployed, fall back to
// LS; network error = same.

const ONBOARDING_STATUS_URL = '/api/players/me/onboarding-status';

interface OnboardingStatus {
  completed?: boolean;
  Completed?: boolean;
  completedAtUtc?: string;
}

let serverProbed = false;
let serverCompleted = false;
let offlineFallback = false;

async function probeServerOnboardingStatus(): Promise<boolean> {
  if (serverProbed) return serverCompleted;
  serverProbed = true;
  try {
    const r = await fetch(ONBOARDING_STATUS_URL, {
      method: 'GET',
      credentials: 'include',
      headers: { Accept: 'application/json' },
    });
    if (!r.ok) return false;
    const body = (await r.json()) as OnboardingStatus;
    serverCompleted = body.completed === true || body.Completed === true;
    return serverCompleted;
  } catch {
    // Phase K Wave 3 — Offline-friendly fallback.  When the GET throws
    // (network unreachable / DNS failure / etc.) we mark the probe
    // resolved as "not completed" and let the LS flag be the
    // authoritative source.  The caller checks `offlineFallback` so it
    // can short-circuit the persist-on-completion POST.
    offlineFallback = true;
    return false;
  }
}

function persistServerCompletion(): void {
  // Phase K Wave 3 — Fire-and-forget.  When the user finishes the tour
  // we don't want to block the "Done" UX on a network round-trip; the
  // LS flag is the offline-safe source of truth.  We also skip the
  // POST entirely when the probe threw (offline) — we'll re-sync on
  // the next page load once the user is back online.
  if (offlineFallback) return;
  try {
    void fetch(ONBOARDING_STATUS_URL, {
      method: 'POST',
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json',
      },
      body: JSON.stringify({
        completed: true,
        completedAtUtc: new Date().toISOString(),
      }),
    }).catch(() => {
      // Best-effort — LS flag remains the authoritative offline fallback.
    });
  } catch {
    // Defensive: fetch is a global, but if it throws synchronously
    // (older browsers under certain CSPs) we still don't want it to
    // bubble up and break the tour's `Done ✓` click handler.
  }
}

// ── Public API ──────────────────────────────────────────────────────

export function installOnboardingTour(): void {
  if (state.installed) return;
  state.installed = true;
  if (isLocalComplete()) return;
  // Phase K Wave 3 — Non-blocking tour render.  Wave 2 awaited the
  // probe before deciding whether to surface the tour, which meant
  // an offline user (or a slow `/api/players/me/onboarding-status`
  // round-trip) saw nothing for up to 30 s.  Wave 3 races the probe
  // against a 300 ms timer: if the probe resolves in time, we honour
  // a `completed: true` response and skip the tour; if it doesn't,
  // we fall back to the LS flag and show the tour immediately.
  let started = false;
  const startIfNeeded = (): void => {
    if (started) return;
    started = true;
    if (isLocalComplete() || serverCompleted) return;
    startTour();
  };
  void probeServerOnboardingStatus().then((completed) => {
    if (completed) {
      persistLocalComplete();
      started = true; // suppress the timer-driven start
      return;
    }
    startIfNeeded();
  });
  // Hard deadline so an offline user / hung backend doesn't sit
  // staring at a blank lobby.  300 ms is short enough to feel like
  // first-paint behaviour but long enough for a healthy probe to win
  // the race in the common case.
  window.setTimeout(startIfNeeded, 300);
}

export function startTour(): void {
  if (state.active) return;
  state.active = true;
  state.step = 0;
  ensureRoot();
  paintStep();
  window.dispatchEvent(new CustomEvent('mahjong:tour-started'));
}

export function endTour(markComplete: boolean): void {
  if (!state.active) return;
  state.active = false;
  if (markComplete) {
    persistLocalComplete();
    // Phase K Wave 3 — Fire-and-forget; `persistServerCompletion()` no
    // longer returns a Promise so the Done button never awaits the
    // network round-trip.
    persistServerCompletion();
  }
  teardownRoot();
  window.dispatchEvent(new CustomEvent('mahjong:tour-ended', {
    detail: { markComplete },
  }));
}

export function resetTour(): void {
  try { window.localStorage.removeItem(TOUR_LS_KEY); } catch { /* ignore */ }
  serverProbed = false;
  serverCompleted = false;
  offlineFallback = false;
}

/** Returns the cached local-storage flag.  Kept as the offline path. */
export function isComplete(): boolean {
  return isLocalComplete() || serverCompleted;
}

function isLocalComplete(): boolean {
  try { return window.localStorage.getItem(TOUR_LS_KEY) === 'true'; }
  catch { return false; }
}

function persistLocalComplete(): void {
  try { window.localStorage.setItem(TOUR_LS_KEY, 'true'); } catch { /* ignore */ }
}

// ── Root scaffold ───────────────────────────────────────────────────

function ensureRoot(): void {
  if (state.root !== null) return;
  const overlay = document.createElement('div');
  overlay.id = 'tour-overlay';
  overlay.className = 'tour-overlay';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'true');
  overlay.setAttribute('aria-live', 'polite');
  overlay.setAttribute('data-testid', 'tour-overlay');

  // SVG mask for the dim background + spotlight cutout.  We use an
  // SVG with a path that's the viewport rect minus the spotlight rect
  // (using `evenodd` fill-rule).  Geometry is updated per-step.
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'tour-overlay-svg');
  svg.setAttribute('width', '100%');
  svg.setAttribute('height', '100%');
  svg.setAttribute('preserveAspectRatio', 'none');
  const defs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
  const maskId = 'tour-spotlight-mask';
  const mask = document.createElementNS('http://www.w3.org/2000/svg', 'mask');
  mask.setAttribute('id', maskId);
  // White everywhere = visible (dim layer); black inside the rect =
  // transparent (spotlight cutout).
  const fullRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
  fullRect.setAttribute('x', '0');
  fullRect.setAttribute('y', '0');
  fullRect.setAttribute('width', '100%');
  fullRect.setAttribute('height', '100%');
  fullRect.setAttribute('fill', 'white');
  mask.appendChild(fullRect);
  const cutout = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
  cutout.setAttribute('class', 'tour-spotlight-cutout');
  cutout.setAttribute('data-testid', 'tour-spotlight');
  cutout.setAttribute('fill', 'black');
  cutout.setAttribute('rx', '8');
  cutout.setAttribute('ry', '8');
  cutout.setAttribute('x', '-100');
  cutout.setAttribute('y', '-100');
  cutout.setAttribute('width', '0');
  cutout.setAttribute('height', '0');
  mask.appendChild(cutout);
  defs.appendChild(mask);
  svg.appendChild(defs);
  const dim = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
  dim.setAttribute('class', 'tour-overlay-dim');
  dim.setAttribute('x', '0');
  dim.setAttribute('y', '0');
  dim.setAttribute('width', '100%');
  dim.setAttribute('height', '100%');
  dim.setAttribute('mask', `url(#${maskId})`);
  svg.appendChild(dim);
  state.spotlight = cutout;
  overlay.appendChild(svg);

  const card = document.createElement('div');
  card.className = 'tour-card';
  card.setAttribute('role', 'document');
  card.innerHTML = `
    <header class="tour-card-header">
      <h3 id="tour-card-title" class="tour-card-title"></h3>
      <span id="tour-card-step-counter"
            class="tour-card-step-counter"
            aria-live="polite"></span>
    </header>
    <p id="tour-card-body" class="tour-card-body"></p>
    <footer class="tour-card-footer">
      <button id="tour-skip" type="button"
              class="btn btn-secondary btn-sm tour-skip"
              data-testid="tour-skip">Skip tour</button>
      <div class="tour-card-nav">
        <button id="tour-prev" type="button"
                class="btn btn-info btn-sm tour-prev"
                data-testid="tour-prev">‹ Prev</button>
        <button id="tour-next" type="button"
                class="btn btn-success btn-sm tour-next"
                data-testid="tour-next">Next ›</button>
      </div>
    </footer>
  `;
  state.cardEl = card;
  overlay.appendChild(card);

  document.body.appendChild(overlay);
  state.root = overlay;

  // Wire control buttons.
  card.querySelector<HTMLButtonElement>('#tour-skip')
    ?.addEventListener('click', () => endTour(true));
  card.querySelector<HTMLButtonElement>('#tour-prev')
    ?.addEventListener('click', () => prevStep());
  card.querySelector<HTMLButtonElement>('#tour-next')
    ?.addEventListener('click', () => nextStep());

  // Keyboard support — Escape closes (without marking complete so the
  // user can resume), arrow keys advance, Enter triggers Next.
  const onKey = (ev: KeyboardEvent): void => {
    if (!state.active) return;
    if (ev.key === 'Escape') {
      ev.preventDefault();
      endTour(false);
    } else if (ev.key === 'ArrowRight' || ev.key === 'Enter') {
      ev.preventDefault();
      nextStep();
    } else if (ev.key === 'ArrowLeft') {
      ev.preventDefault();
      prevStep();
    }
  };
  document.addEventListener('keydown', onKey);
  // Stash on the root so teardown removes the listener.
  (overlay as unknown as { __keyHandler?: typeof onKey }).__keyHandler = onKey;

  // Re-paint on resize / scroll so the spotlight tracks the anchor.
  const reposition = (): void => {
    if (state.active) repositionSpotlight(currentStep());
  };
  window.addEventListener('resize', reposition);
  window.addEventListener('scroll', reposition, true);
  state.resizeHandler = reposition;
  state.scrollHandler = reposition;
}

function teardownRoot(): void {
  if (state.root === null) return;
  const root = state.root;
  const handler = (root as unknown as { __keyHandler?: (ev: KeyboardEvent) => void }).__keyHandler;
  if (handler) document.removeEventListener('keydown', handler);
  if (state.resizeHandler !== null) window.removeEventListener('resize', state.resizeHandler);
  if (state.scrollHandler !== null) window.removeEventListener('scroll', state.scrollHandler, true);
  // Drop step-marker classes from any element that received one.
  document.querySelectorAll('.tour-step-target').forEach(el => {
    el.classList.remove('tour-step-target');
  });
  document.querySelectorAll('.tour-step-target-secondary').forEach(el => {
    el.classList.remove('tour-step-target-secondary');
  });
  root.remove();
  state.root = null;
  state.spotlight = null;
  state.cardEl = null;
  state.resizeHandler = null;
  state.scrollHandler = null;
}

// ── Step navigation ─────────────────────────────────────────────────

function currentStep(): TourStep {
  return STEPS[state.step] ?? STEPS[0];
}

function nextStep(): void {
  if (state.step >= STEPS.length - 1) {
    endTour(true);
    return;
  }
  state.step += 1;
  paintStep();
}

function prevStep(): void {
  if (state.step <= 0) return;
  state.step -= 1;
  paintStep();
}

function paintStep(): void {
  const step = currentStep();
  if (state.cardEl !== null) {
    const title = state.cardEl.querySelector<HTMLElement>('#tour-card-title');
    const body = state.cardEl.querySelector<HTMLElement>('#tour-card-body');
    const counter = state.cardEl.querySelector<HTMLElement>('#tour-card-step-counter');
    const prev = state.cardEl.querySelector<HTMLButtonElement>('#tour-prev');
    const next = state.cardEl.querySelector<HTMLButtonElement>('#tour-next');
    if (title !== null) title.textContent = step.title;
    if (body !== null) body.textContent = step.body;
    if (counter !== null) counter.textContent = `${step.index} / ${STEPS.length}`;
    if (prev !== null) prev.disabled = state.step <= 0;
    if (next !== null) {
      next.textContent = state.step === STEPS.length - 1 ? 'Done ✓' : 'Next ›';
    }
    state.cardEl.setAttribute('data-testid', `tour-step-${step.index}`);
  }
  // Activate the appropriate lobby tab if requested.
  if (step.activateTab !== undefined) {
    const tabId = `lobby-${step.activateTab}-tab`;
    const btn = document.getElementById(tabId);
    if (btn instanceof HTMLElement) btn.click();
  }
  // Mark target classes so CSS can paint a halo + outline.
  document.querySelectorAll('.tour-step-target').forEach(el => {
    el.classList.remove('tour-step-target');
  });
  document.querySelectorAll('.tour-step-target-secondary').forEach(el => {
    el.classList.remove('tour-step-target-secondary');
  });
  const target = resolveTarget(step);
  if (target !== null) {
    target.classList.add('tour-step-target');
    // Scroll into view for off-screen targets.
    try {
      target.scrollIntoView({ behavior: 'auto', block: 'center', inline: 'center' });
    } catch { /* not all elements support scrollIntoView opts */ }
  }
  if (step.secondarySelectors !== undefined) {
    const secondary = resolveSelectors(step.secondarySelectors);
    if (secondary !== null) secondary.classList.add('tour-step-target-secondary');
  }
  repositionSpotlight(step);
}

function resolveTarget(step: TourStep): HTMLElement | null {
  return resolveSelectors(step.selectors);
}

function resolveSelectors(selectors: ReadonlyArray<string>): HTMLElement | null {
  for (const s of selectors) {
    try {
      const el = document.querySelector(s) as HTMLElement | null;
      if (el !== null && el.offsetParent !== null) return el;
      // Fallback: if not connected to layout but exists, still use it.
      if (el !== null) return el;
    } catch { /* invalid selector — ignore */ }
  }
  return null;
}

function repositionSpotlight(step: TourStep): void {
  if (state.spotlight === null || state.cardEl === null) return;
  const target = resolveTarget(step);
  const card = state.cardEl;
  if (target === null || step.selectors.length === 0) {
    // Centred final step — hide the spotlight by parking it off-screen
    // and centre the card.
    state.spotlight.setAttribute('x', '-100');
    state.spotlight.setAttribute('y', '-100');
    state.spotlight.setAttribute('width', '0');
    state.spotlight.setAttribute('height', '0');
    card.style.left = '';
    card.style.top = '';
    card.classList.add('tour-card-centered');
    return;
  }
  card.classList.remove('tour-card-centered');
  const rect = target.getBoundingClientRect();
  const pad = 8;
  const x = Math.max(0, rect.left - pad);
  const y = Math.max(0, rect.top - pad);
  const w = rect.width + pad * 2;
  const h = rect.height + pad * 2;
  state.spotlight.setAttribute('x', String(x));
  state.spotlight.setAttribute('y', String(y));
  state.spotlight.setAttribute('width', String(Math.max(0, w)));
  state.spotlight.setAttribute('height', String(Math.max(0, h)));

  // Card positioning — try below the spotlight, else above.  Clamp
  // to viewport so the card always stays visible.
  const cardRect = card.getBoundingClientRect();
  const vpW = window.innerWidth;
  const vpH = window.innerHeight;
  const spaceBelow = vpH - (y + h);
  let cardY: number;
  if (spaceBelow >= cardRect.height + 16) {
    cardY = y + h + 12;
  } else if (y >= cardRect.height + 16) {
    cardY = y - cardRect.height - 12;
  } else {
    cardY = Math.max(8, vpH - cardRect.height - 16);
  }
  let cardX = x + (w - cardRect.width) / 2;
  cardX = Math.max(8, Math.min(vpW - cardRect.width - 8, cardX));
  card.style.left = `${cardX}px`;
  card.style.top = `${cardY}px`;
}

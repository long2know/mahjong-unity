// Ferro — Variant picker (additive lobby dropdown).
//
// Stephen's Changsha-realism directive (2026-05-19) — F-3 calls out:
//
//   "Variant switching: Changsha ↔ original autotable variants must coexist."
//
// The autotable backend already understands a `?variant=` query param at
// the WS handshake (`AutotableWsEndpoint.cs:228` — accepted verbatim;
// `changsha` (case-insensitive) routes to the ChangshaRuntime, every
// other value routes to Relay mode which forwards the bundle's local
// Setup for the upstream Riichi / Bamboo / Minefield variants).
//
// This module surfaces that switch as a prominent `<select>` at the top
// of the lobby panel.  Selecting a variant:
//
//   1) Writes the chosen value to `localStorage['mahjong.preferredVariant']`
//      so it sticks across reloads.
//   2) Rewrites the URL `?variant=` param AND mints a FRESH `?gameId=`
//      (via session-url's resolveApplyGameId) so switching variant starts
//      a new, isolated game instead of silently re-joining the stale game
//      already running under the current concrete gameId.  An unchanged
//      concrete URL is left untouched so a deliberate reconnect is kept.
//   3) Reloads the page (`window.location.replace`) so the backend WS
//      handshake re-runs with the new variant and the frontend bundle
//      rehydrates against the matching runtime.
//
// On first render the picker pre-populates from:
//
//   URL `?variant=` (wins)  >  localStorage  >  default `changsha`.
//
// Coexists with Hicks's existing radio-button picker
// (`#lobby-variant-fieldset` in index.html).  No trunk file is modified
// — this overlay attaches via a `MutationObserver` that waits for the
// lobby panel to appear in the DOM and then inserts itself BEFORE the
// existing variant fieldset, matching the additive-overlay pattern
// Ferro used for the claim window and win screen.
//
// Lifecycle:
//   • Module import → installVariantPicker() runs once → MutationObserver
//     waits for `#lobby-panel` → dropdown inserted → handler installed.
//   • Re-entry safe — `installVariantPicker()` and `mountInto()` are
//     idempotent (no-op if the dropdown is already attached).

import './variant-picker.css';
// Co-load the dark-listbox enhancer here too: ferro-bootstrap only imports it
// on game pages (search !== ''), but this picker also mounts on lobby cold
// paths — this guarantees the enhanced (screenshottable, readable) popup wraps
// the variant switcher wherever it appears. Idempotent + fallback-safe.
import './dark-listbox';
import {
  resolveApplyGameId,
  buildFreshGameUrl,
  readConcreteGameId,
  NEW_GAME_DEFAULTS,
  type GameDefiningConfig,
} from '../session-url';

const STORAGE_KEY = 'mahjong.preferredVariant';
const QUERY_PARAM = 'variant';
const DEFAULT_VARIANT = 'changsha';

interface VariantOption {
  value: string;
  label: string;
  /** Optional <optgroup> bucket. */
  group?: string;
  /** When true the option renders but cannot be chosen. */
  disabled?: boolean;
  /** Optional `title` tooltip — surfaces "coming soon" reasons. */
  tooltip?: string;
}

// Backend-accepted variants (AutotableWsEndpoint §RuntimeMode):
//   • `changsha`     → ChangshaRuntime (default — 108 tiles, 258-pair eyes)
//   • everything else → Relay mode (upstream bundle's Setup drives the deal)
// The accepted upstream values are documented inline at lines 1505–1506
// of AutotableWsEndpoint.cs: `four_player` / `three_player` / `bamboo` /
// `minefield`.  Hicks's lobby.ts uses hyphenated forms (`four-player`,
// `three-player`) when writing the URL — the backend takes the string
// verbatim, so either form is accepted by the relay path.  We use the
// hyphenated forms so the URL we emit round-trips through Hicks's
// existing radio-button picker without confusion.
const VARIANT_OPTIONS: ReadonlyArray<VariantOption> = [
  {
    value: 'changsha',
    label: 'Changsha (长沙麻将)',
  },
  {
    value: 'four-player',
    label: 'Riichi — 4 player (日本麻将)',
    group: 'Original Autotable',
  },
  {
    value: 'three-player',
    label: 'Riichi — 3 player',
    group: 'Original Autotable',
  },
  {
    value: 'bamboo',
    label: 'Bamboo (American)',
    group: 'Original Autotable',
  },
  {
    value: 'minefield',
    label: 'Minefield',
    group: 'Original Autotable',
  },
  // Stephen's directive explicitly mentions Hong Kong (港麻) as a future
  // variant.  No backend route today — surface a disabled option with a
  // tooltip so the placeholder is visible without shipping a broken pick.
  {
    value: 'hong-kong',
    label: 'Hong Kong (港麻)',
    group: 'Coming soon',
    disabled: true,
    tooltip: 'Coming soon — backend support pending',
  },
];

const VALID_VARIANT_VALUES = new Set(
  VARIANT_OPTIONS.filter(o => !(o.disabled === true)).map(o => o.value),
);

let installed = false;

/**
 * Idempotent installer.  Imports run this automatically (see bottom of
 * file); ferro-bootstrap also imports the module for its side-effect on
 * game pages so the picker mounts on a Quick-Match reload where the
 * lobby panel is hydrated lazily.
 */
export function installVariantPicker(): void {
  if (installed) return;
  if (typeof window === 'undefined' || typeof document === 'undefined') return;
  installed = true;

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', mountWhenPanelExists, { once: true });
  } else {
    mountWhenPanelExists();
  }
}

function mountWhenPanelExists(): void {
  const existing = document.getElementById('lobby-panel');
  if (existing !== null) {
    mountInto(existing);
    return;
  }

  // Watch for the lobby panel to appear in the DOM.  The lobby markup
  // lives in index.html so on a fresh page it's there immediately, but
  // some bootstrap variants (PWA shortcut handling) tear/replace the
  // panel — we want to re-mount in that case.
  const observer = new MutationObserver(() => {
    const panel = document.getElementById('lobby-panel');
    if (panel !== null) {
      observer.disconnect();
      mountInto(panel);
    }
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
}

function mountInto(panel: HTMLElement): void {
  if (panel.querySelector('.ferro-variant-picker') !== null) return;

  const section = buildDropdownSection();

  // Insert ABOVE the existing #lobby-variant-fieldset so the dropdown
  // sits at the same scope as Hicks's radio-button picker.  The
  // fieldset's actual parent is `#lobby-tab-my-game` (the "My Game"
  // tab pane), not `.lobby-body` directly, so we walk to the
  // fieldset's parent rather than assuming a top-level position.
  // If the fieldset isn't found (markup churn), fall back to the
  // lobby body so the picker is at least reachable.
  const existingFieldset = panel.querySelector<HTMLElement>('#lobby-variant-fieldset');
  if (existingFieldset !== null && existingFieldset.parentNode !== null) {
    existingFieldset.parentNode.insertBefore(section, existingFieldset);
    return;
  }
  const body = panel.querySelector<HTMLElement>('.lobby-body') ?? panel;
  body.appendChild(section);
}

function buildDropdownSection(): HTMLElement {
  const section = document.createElement('section');
  section.className = 'ferro-variant-picker';
  section.setAttribute('role', 'group');
  section.setAttribute('aria-label', 'Game variant');

  const heading = document.createElement('div');
  heading.className = 'ferro-variant-picker-label';
  heading.textContent = 'Variant';
  section.appendChild(heading);

  const select = document.createElement('select');
  select.className = 'ferro-variant-picker-select dark-select';
  select.setAttribute('aria-label', 'Game variant');
  select.setAttribute('data-testid', 'ferro-variant-picker');
  select.id = 'ferro-variant-select';

  // Group options by optgroup bucket so the "Original Autotable" and
  // "Coming soon" labels are honoured.
  const groups = new Map<string | null, HTMLElement>();
  function targetFor(group: string | undefined): HTMLElement {
    const key = group ?? null;
    let host = groups.get(key);
    if (host !== undefined) return host;
    if (key === null) {
      host = select;
    } else {
      const og = document.createElement('optgroup');
      og.label = key;
      select.appendChild(og);
      host = og;
    }
    groups.set(key, host);
    return host;
  }

  const initialValue = resolveInitialVariant();
  for (const opt of VARIANT_OPTIONS) {
    const optionEl = document.createElement('option');
    optionEl.value = opt.value;
    optionEl.textContent = opt.label;
    if (opt.disabled === true) optionEl.disabled = true;
    if (opt.tooltip !== undefined) optionEl.title = opt.tooltip;
    if (opt.value === initialValue) optionEl.selected = true;
    targetFor(opt.group).appendChild(optionEl);
  }

  const hint = document.createElement('div');
  hint.className = 'ferro-variant-picker-hint';
  hint.textContent = 'Switching variant starts a fresh table.';

  section.appendChild(select);
  section.appendChild(hint);

  select.addEventListener('change', () => {
    const value = select.value;
    if (!VALID_VARIANT_VALUES.has(value)) {
      // Defensive — disabled options should be unselectable, but guard
      // anyway so a future markup tweak can't slip a broken pick into
      // the URL.
      select.value = initialValue;
      return;
    }
    onVariantChosen(value);
  });

  // Mirror Hicks's existing radio buttons when the user changes them so
  // the dropdown doesn't go stale while the lobby is open.  Idempotent
  // listener — `change` events bubble from the radio inputs through
  // their parent fieldset to document.
  document.addEventListener('change', (ev) => {
    const target = ev.target;
    if (!(target instanceof HTMLInputElement)) return;
    if (target.name !== 'lobby-variant') return;
    if (!target.checked) return;
    if (select.value !== target.value && VALID_VARIANT_VALUES.has(target.value)) {
      select.value = target.value;
    }
  });

  return section;
}

// Pure, DOM-free decision for where a variant switch should navigate.
// Exported for the browser-free contract test; the DOM handler below is a
// thin wrapper that persists the preference and performs the reload.
//
// Returns the target URL, or `null` when nothing game-defining changes (an
// unchanged concrete `?gameId=` URL — a deliberate reconnect — is left
// untouched).  Switching variant is a reconfiguration, so Hicks's
// `resolveApplyGameId` mints a FRESH, isolated gameId rather than silently
// re-opening the stale/reused game running under the current concrete
// gameId (the "Setting to Changsha reuses the old game" complaint), and
// `buildFreshGameUrl` stamps the honest New-Game defaults for any config the
// URL omits (crucially `dealMode=auto` for Changsha) so the fresh table
// never boots a different dealMode/botCount than intended.  Every other
// query param is carried through verbatim; `mint` is injectable so the
// contract test is deterministic.
export function computeVariantNavigation(
  href: string,
  value: string,
  mint?: () => string,
): string | null {
  const url = new URL(href);
  const search = url.search;

  const cfg = gameConfigFromSearch(search, value);
  const nextGameId = resolveApplyGameId(search, cfg, mint);

  // resolveApplyGameId returns the SAME concrete id only when the URL already
  // targets a concrete game whose game-defining config is unchanged — i.e. a
  // deliberate reconnect.  Nothing to reload for; leave the URL untouched.
  if (nextGameId === readConcreteGameId(search)) {
    return null;
  }

  // Reconfiguration (variant switch) ⇒ start a FRESH, isolated game: set the
  // chosen variant, then let buildFreshGameUrl mint-in the fresh gameId and
  // fill any omitted New-Game defaults.  Params the user already set survive.
  url.searchParams.set(QUERY_PARAM, value);
  return buildFreshGameUrl(url.pathname, url.search, nextGameId);
}

// Build the game-defining config the user is switching TO: the chosen
// variant, plus every other game-defining param carried verbatim from the
// current URL (so the diff detects ONLY the variant change and the user's
// other settings survive the switch).
function gameConfigFromSearch(search: string, variant: string): GameDefiningConfig {
  const p = new URLSearchParams(search);
  const seedRaw = p.get('seed');
  return {
    variant,
    dealMode: p.get('dealMode') ?? undefined,
    botCount: p.has('botCount') ? Number(p.get('botCount')) : NEW_GAME_DEFAULTS.botCount,
    botDifficulty: p.get('botDifficulty') ?? undefined,
    handCount: p.has('handCount') ? Number(p.get('handCount')) : NEW_GAME_DEFAULTS.handCount,
    seed: seedRaw === null || seedRaw.trim() === '' ? null : Number(seedRaw),
  };
}

function onVariantChosen(value: string): void {
  // Persist the preference so a fresh lobby visit restores it even if the
  // navigation below is blocked (private mode / quota exceeded).
  try {
    window.localStorage.setItem(STORAGE_KEY, value);
  } catch {
    // localStorage may be unavailable; the navigation still drives the switch.
  }

  const target = computeVariantNavigation(window.location.href, value);
  if (target === null) {
    // Unchanged concrete URL — nothing to reload for; a deliberate reconnect
    // to the same game is preserved.  (LS was still written above.)
    return;
  }

  // `location.replace` (not `assign`) so the browser back button doesn't
  // bounce between variants — mirrors Hicks's Apply & Start exit path
  // in lobby.ts:apply.addEventListener('click').
  window.location.replace(target);
}

function resolveInitialVariant(): string {
  // Priority: URL > localStorage > DEFAULT.
  try {
    const params = new URLSearchParams(window.location.search);
    const fromUrl = params.get(QUERY_PARAM);
    if (fromUrl !== null && fromUrl !== '') {
      const normalised = fromUrl.toLowerCase().replace(/_/g, '-');
      if (VALID_VARIANT_VALUES.has(normalised)) return normalised;
    }
  } catch {
    // Same-origin URLSearchParams shouldn't throw, but defensive.
  }
  try {
    const fromLs = window.localStorage.getItem(STORAGE_KEY);
    if (fromLs !== null && fromLs !== '') {
      const normalised = fromLs.toLowerCase().replace(/_/g, '-');
      if (VALID_VARIANT_VALUES.has(normalised)) return normalised;
    }
  } catch {
    // Same as above — LS may be unavailable.
  }
  return DEFAULT_VARIANT;
}

// Side-effect: install on import so a single `import './variant-picker'`
// from ferro-bootstrap.ts or index.ts is sufficient to wire the picker
// on both game-page and lobby-only sessions.
installVariantPicker();

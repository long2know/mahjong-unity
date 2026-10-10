// Phase J Wave 9 — Tiny i18n module.
//
// Bundles 3 catalogs (en, zh-Hans, zh-Hant) imported as JSON at build
// time so the lookup table is available synchronously on first paint —
// no network round-trip, no FOUC.
//
// The active language follows this precedence:
//   1. User explicit override from the settings drawer (LS key
//      `mahjong.settings.v1.lang`, persisted as a sibling field of the
//      Wave-7 settings blob).
//   2. `navigator.language` family detection — `zh-CN`, `zh-SG`, `zh-Hans`
//      map to Simplified; `zh-TW`, `zh-HK`, `zh-MO`, `zh-Hant` map to
//      Traditional; everything else falls back to English.
//   3. Bundled English catalog is the absolute fallback when a key is
//      missing from the active catalog.
//
// The body `lang=` attribute is set to the active locale tag so
// CSS / Playwright selectors can target `body[lang="zh-Hans"]`.
//
// API:
//   t(key, params?)  → resolved string (with `{placeholder}` interpolation)
//   setLanguage('auto' | 'en' | 'zh-Hans' | 'zh-Hant')
//   getLanguage()    → the user pref (may be 'auto')
//   getActiveLocale()→ the resolved locale tag ('en' / 'zh-Hans' / 'zh-Hant')
//   onLanguageChange(fn) → subscribe to changes; returns unsubscribe
//
// Strings used in the codebase:
//   • Lobby chrome (panel title, picker labels, CTAs)
//   • Settings drawer tab labels, field labels, motion/theme options
//   • Sign-in / auth chip
//   • Chat panel (channel labels, placeholders, errors)
//   • Replay viewer (tabs, columns, empty states)
//   • Pattern names (PATTERN_LABELS in game-ui.ts)
//
// Catalogs live in `src/i18n/{lang}.json`.  Bishop's
// `/api/i18n/patterns` server-side catalog (if/when it ships) is
// additive — when available, it can `mergeServerCatalog()` the
// pattern.* keys on top of the bundled ones.

import enCatalog from './i18n/en.json';

// Phase K Wave 21 — Hicks (bundle-audit §3.6).  Only the English
// catalog is bundled eagerly; the zh-Hans / zh-Hant tables (~5 KB
// each minified) lazy-import the first time the resolved active
// locale is zh-* (either via auto-detect at boot or an explicit
// `setLanguage()` flip).  Until the chunk arrives, `t()` falls
// back to the English catalog (the documented fallback path).
// For users whose browser language resolves to zh-*, this means
// a ~10-30 ms window of English strings at lobby cold start
// before the zh chunk lands; acceptable trade-off to keep the
// eager bundle under the W21 ceiling.

export type LocaleTag = 'en' | 'zh-Hans' | 'zh-Hant';
export type LanguagePreference = 'auto' | LocaleTag;

type Catalog = Readonly<Record<string, string>>;

const CATALOGS: Record<LocaleTag, Catalog | null> = {
  'en': enCatalog as Catalog,
  'zh-Hans': null,
  'zh-Hant': null,
};

const loadingCatalog: Partial<Record<LocaleTag, Promise<Catalog>>> = {};

function ensureCatalog(locale: LocaleTag): Promise<Catalog> | null {
  if (CATALOGS[locale] !== null) return null;
  const inFlight = loadingCatalog[locale];
  if (inFlight !== undefined) return inFlight;
  const p = (async (): Promise<Catalog> => {
    const mod = locale === 'zh-Hans'
      ? await import('./i18n/zh-Hans.json')
      : await import('./i18n/zh-Hant.json');
    const cat = (mod.default ?? mod) as Catalog;
    CATALOGS[locale] = cat;
    delete loadingCatalog[locale];
    if (locale === activeLocale) emit();
    return cat;
  })();
  loadingCatalog[locale] = p;
  return p;
}

const LS_KEY = 'mahjong.settings.v1';
const LS_FIELD = 'lang';

let userPref: LanguagePreference = 'auto';
let activeLocale: LocaleTag = 'en';
const listeners = new Set<(locale: LocaleTag) => void>();

// ── LS helpers ─────────────────────────────────────────────────────

function loadFromStorage(): LanguagePreference {
  try {
    const raw = window.localStorage.getItem(LS_KEY);
    if (raw === null) return 'auto';
    const j = JSON.parse(raw) as Record<string, unknown>;
    const v = j[LS_FIELD];
    if (v === 'en' || v === 'zh-Hans' || v === 'zh-Hant') return v;
    return 'auto';
  } catch {
    return 'auto';
  }
}

function writeToStorage(pref: LanguagePreference): void {
  try {
    const raw = window.localStorage.getItem(LS_KEY);
    let payload: Record<string, unknown> = {};
    if (raw !== null) {
      try {
        const parsed = JSON.parse(raw);
        if (parsed !== null && typeof parsed === 'object') {
          payload = parsed as Record<string, unknown>;
        }
      } catch { /* ignore */ }
    }
    payload[LS_FIELD] = pref;
    window.localStorage.setItem(LS_KEY, JSON.stringify(payload));
  } catch { /* skip */ }
}

// ── Browser-language detection ─────────────────────────────────────

function detectFromNavigator(): LocaleTag {
  try {
    const langs: string[] = Array.isArray(navigator.languages) && navigator.languages.length > 0
      ? [...navigator.languages]
      : [navigator.language ?? 'en'];
    for (const raw of langs) {
      const lc = String(raw).toLowerCase();
      if (lc === 'zh-tw' || lc === 'zh-hk' || lc === 'zh-mo'
          || lc.startsWith('zh-hant') || lc === 'zh-tw-hant') {
        return 'zh-Hant';
      }
      if (lc === 'zh-cn' || lc === 'zh-sg' || lc.startsWith('zh-hans')
          || lc === 'zh' || lc.startsWith('zh-')) {
        return 'zh-Hans';
      }
      if (lc.startsWith('en')) return 'en';
    }
  } catch { /* skip */ }
  return 'en';
}

function resolveLocale(pref: LanguagePreference): LocaleTag {
  if (pref === 'auto') return detectFromNavigator();
  return pref;
}

// ── Apply derived state ───────────────────────────────────────────

function apply(): void {
  const body = document.body;
  if (body !== null && body !== undefined) {
    body.setAttribute('lang', activeLocale);
  }
  const html = document.documentElement;
  if (html !== null && html !== undefined) {
    html.setAttribute('lang', activeLocale);
  }
}

// ── Public API ────────────────────────────────────────────────────

/**
 * Translate a key.  Falls back to:
 *   1. the active-locale catalog
 *   2. the English catalog
 *   3. the raw key itself (with a console.warn in dev)
 *
 * Param interpolation: `{name}` placeholders are substituted from
 * `params`.  Missing params are left as `{name}` so the dev knows to
 * supply them.
 */
export function t(key: string, params?: Record<string, string | number>): string {
  if (key === 'lobby.room_counts' || key === 'lobby.room_counts_compact') {
    params = {
      ...params,
      humanWord: t(params?.humans === 1 ? 'lobby.human_one' : 'lobby.human_other'),
      botWord: t(params?.bots === 1 ? 'lobby.bot_one' : 'lobby.bot_other'),
      openWord: t(params?.open === 1 ? 'lobby.open_one' : 'lobby.open_other'),
    };
  }
  const active = CATALOGS[activeLocale];
  let str = active === null ? undefined : active[key];
  if (str === undefined || str === null || str === '') {
    str = CATALOGS['en']?.[key];
  }
  if (str === undefined || str === null || str === '') {
    return key;
  }
  if (params === undefined || params === null) return str;
  return str.replace(/\{(\w+)\}/g, (_match, p1: string) => {
    const v = params[p1];
    if (v === undefined || v === null) return `{${p1}}`;
    return String(v);
  });
}

export function getLanguage(): LanguagePreference {
  return userPref;
}

export function getActiveLocale(): LocaleTag {
  return activeLocale;
}

export function setLanguage(pref: LanguagePreference): void {
  if (pref !== 'auto' && pref !== 'en' && pref !== 'zh-Hans' && pref !== 'zh-Hant') {
    return;
  }
  userPref = pref;
  const next = resolveLocale(userPref);
  writeToStorage(pref);
  if (next !== activeLocale) {
    activeLocale = next;
    apply();
    emit();
  }
  // Kick off the lazy catalog load if the new active locale isn't
  // bundled.  Re-emit happens inside `ensureCatalog` when the
  // chunk lands so listeners can re-render with localized strings.
  void ensureCatalog(activeLocale);
}

export function onLanguageChange(handler: (locale: LocaleTag) => void): () => void {
  listeners.add(handler);
  return () => { listeners.delete(handler); };
}

function emit(): void {
  translateElements();
  for (const fn of listeners) {
    try { fn(activeLocale); } catch { /* swallow */ }
  }
}

/**
 * Idempotent boot.  Reads LS, resolves the active locale, applies the
 * body `lang=` attribute.  Safe to call from `lobby.ts:initLobby()`
 * before any other UI install hook so chrome paints with the resolved
 * locale.
 */
let installed = false;
export function installI18n(): void {
  if (installed) return;
  installed = true;
  userPref = loadFromStorage();
  activeLocale = resolveLocale(userPref);
  apply();
  translateElements();
  // If the resolved active locale isn't English, kick off the
  // catalog fetch so `t()` lookups graduate from English fallback
  // to localized strings as soon as the chunk lands.
  void ensureCatalog(activeLocale);
}

export function translateElements(root: ParentNode = document): void {
  for (const element of root.querySelectorAll<HTMLElement>('[data-i18n]')) {
    element.textContent = t(element.dataset.i18n!);
  }
  for (const element of root.querySelectorAll<HTMLElement>('[data-i18n-placeholder]')) {
    element.setAttribute('placeholder', t(element.dataset.i18nPlaceholder!));
  }
}

/**
 * Optional — merge a server-supplied catalog patch on top of the
 * bundled catalogs.  Used when Bishop's `GET /api/i18n/patterns`
 * ships and we want server-canonical pattern names per locale.
 * Existing bundled keys are NOT overwritten unless `force=true`.
 */
export function mergeServerCatalog(
  locale: LocaleTag,
  patch: Readonly<Record<string, string>>,
  force: boolean = false,
): void {
  // The zh-* catalogs may not be resident in memory yet (W21 §3.6
  // lazifies them).  In that case, kick off the load and re-apply
  // the patch after it lands so server-supplied patterns are
  // additive on top of the freshly-arrived bundled keys.
  const target = CATALOGS[locale];
  if (target === null) {
    const loader = ensureCatalog(locale);
    if (loader !== null) {
      void loader.then(() => { mergeServerCatalog(locale, patch, force); });
    }
    return;
  }
  const mut = target as Record<string, string>;
  for (const [k, v] of Object.entries(patch)) {
    if (typeof v !== 'string') continue;
    if (!force && mut[k] !== undefined) continue;
    mut[k] = v;
  }
  if (locale === activeLocale) emit();
}

/**
 * Localize a pattern wire key (Bishop's WinResult.PatternKeys).  The
 * key may be camelCase (`sevenPairs`) or PascalCase (`SevenPairs`);
 * both normalise to the catalog form.  Falls back to a legacy
 * `PatternName` string when supplied (Wave 8 wire shape).
 */
export function tPattern(patternKey: string, legacyName?: string | null): string {
  if (patternKey === '' || patternKey === null || patternKey === undefined) {
    return legacyName ?? '';
  }
  const norm = patternKey.charAt(0).toLowerCase() + patternKey.slice(1);
  const looked = t(`pattern.${norm}`);
  if (looked !== `pattern.${norm}`) return looked;
  return legacyName ?? patternKey;
}

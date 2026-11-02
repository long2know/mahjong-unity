import { Client, GameCompleteEntry } from "./client";
import { Game } from './base-client';
import {
  buildRejoinUrl,
  clearRejoinFromUrl,
  parseRejoinFromUrl,
  readSession,
  SessionToken,
} from './reconnect';
import {
  getPreGameSnapshot,
  getProfile,
  onProfile,
  type PlayerStats,
} from './profile';
import { formatStats, formatStatsDelta } from './stats';
import { showEl, hideEl, setElHidden } from './dom-utils';
import {
  buildFreshGameUrl,
  hasNewGameIntent,
  NEW_GAME_INTENT_PARAM,
  readConcreteGameId,
  resolveHandoffSeat,
} from './session-url';
import { bindNewGameControls } from './lobby';
import { MAX_BASE_UNIT, parseBaseUnit } from './base-unit';
import { t } from './i18n';
import { getRuleActionControls } from './ui/rule-action-controls';
import { bootstrapIdentity, getIdentityBootstrapState } from './identity';
import { isJoinOnly } from './room-join-url';


const TITLE_DISCONNECTED = 'Autotable';
const TITLE_CONNECTED = 'Autotable (online)';

// Phase J Wave 2 — exponential-backoff reconnect.  Replaces the pre-Wave-2
// constant 2 s / 15-attempts loop with a 5-step backoff schedule
// (1/2/4/8/16 s) per directive §Task 2.  Each attempt is surfaced in the
// connection banner so the user sees what's happening; the previous loop
// was silent.
const RECONNECT_DELAYS_MS: ReadonlyArray<number> = [1000, 2000, 4000, 8000, 16000];
const RECONNECT_MAX_ATTEMPTS = RECONNECT_DELAYS_MS.length;

// Lifetime of the green "reconnected" flash before the banner self-hides.
const RECONNECT_OK_FLASH_MS = 2000;

// Phase I Wave 3 — Game ID validation.  Must match the HTML pattern attribute
// on #lobby-gameId so HTML5 form validation and the JS gate agree.  ≤64 chars
// matches Bishop's expected backend cap.
const GAME_ID_MAX_LENGTH = 64;
const GAME_ID_PATTERN = /^[A-Za-z0-9_\-\.]+$/;

// Phase I Wave 4 — sentinel seat value the backend (post-Bishop's cap
// relax) interprets as "spectator: no seat assigned".  Anything in
// 0..3 is a normal seat take; anything else is rejected by the server
// and falls back to the legacy "no preference" flow on our side.
const SPECTATOR_SEAT = -1;

interface GameIdValidation {
  value: string | null;
  error: string | null;
}

function validateGameId(raw: string): GameIdValidation {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return { value: null, error: 'Game ID cannot be empty.' };
  }
  if (trimmed.length > GAME_ID_MAX_LENGTH) {
    return {
      value: null,
      error: `Game ID must be ${GAME_ID_MAX_LENGTH} characters or fewer.`,
    };
  }
  if (!GAME_ID_PATTERN.test(trimmed)) {
    return {
      value: null,
      error: 'Game ID may only contain letters, digits, _, -, and .',
    };
  }
  return { value: trimmed, error: null };
}

// Phase I Wave 4 — exported so game-ui.ts can short-circuit the
// take-seat affordances based on the same URL flag we use to drive the
// body class and the Spectating pill.
export function readSpectatorFromUrl(): boolean {
  if (isJoinOnly()) return false;
  const q = new URLSearchParams(window.location.search);
  const raw = q.get('seat');
  if (raw === null) return false;
  const n = parseInt(raw, 10);
  return !isNaN(n) && n === SPECTATOR_SEAT;
}

// Seat/start deadlock fix (Ferro live-UX rejection) — read a concrete PLAYER
// seat (0..3) from the page URL's `?seat=` handoff, or null for absent /
// invalid / spectator(-1). The connect path turns this HINT into a standard
// seat REQUEST once WS is ready; it is never treated as confirmed ownership
// (the server acks via the seats snapshot). Kept separate from
// readSpectatorFromUrl so a `-1` spectator never reads as a real chair.
export function readPlayerSeatFromUrl(): number | null {
  if (isJoinOnly()) return null;
  try {
    const raw = new URLSearchParams(window.location.search).get('seat');
    if (raw === null) return null;
    const n = parseInt(raw, 10);
    return !isNaN(n) && n >= 0 && n <= 3 ? n : null;
  } catch {
    return null;
  }
}

// Bishop W23 — exported so world.ts can detect human-led manual deal
// mode without the round-tripped match snapshot (which strips the
// dealMode field — see ChangshaToAutotableTranslator.BuildMatch).  The
// URL param is authoritative because it's what's forwarded to the WS
// connection and what the backend uses to drive the runtime's DealMode.
export function readDealModeFromUrl(): 'manual' | 'auto' | null {
  if (isJoinOnly()) return null;
  try {
    const q = new URLSearchParams(window.location.search);
    const raw = q.get('dealMode');
    if (raw === 'manual' || raw === 'auto') return raw;
  } catch {
    // Non-browser test contexts; let callers fall back.
  }
  return null;
}

// Hicks 2026-06-01 — exported so world.ts can pin the local gameType
// to the URL-declared variant.  The backend's
// ChangshaToAutotableTranslator.BuildMatch ships
// `conditions.gameType="FOUR_PLAYER"` as a legacy compatibility hack
// (back when this bundle was Riichi-only); without this pin the
// frontend's `onMatch` would flip the world out of Changsha into the
// Riichi-shaped 136-tile + sticks layout, breaking wall stacking,
// dealer-hand visibility, and corner geometry (Stephen's
// 2026-06-01 broken-deal screenshot).  Returns the canonical GameType
// string for the URL variant, or null if absent / unrecognised.
export function readVariantFromUrl(): string | null {
  try {
    const q = new URLSearchParams(window.location.search);
    const raw = q.get('variant');
    if (!raw) return null;
    const upper = raw.toUpperCase().replace(/-/g, '_');
    // Match the canonical GameType enum string set used by the bundle.
    // Kept as a string-set check (rather than importing GameType) so
    // client-ui has no new module-graph cycle through types.ts.
    if (upper === 'CHANGSHA' || upper === 'FOUR_PLAYER' ||
        upper === 'THREE_PLAYER' || upper === 'BAMBOO' ||
        upper === 'MINEFIELD') {
      return upper;
    }
  } catch {
    // Non-browser test contexts; let callers fall back.
  }
  return null;
}

export class ClientUi {
  url: string;
  client: Client;
  nickElement: HTMLInputElement;
  statusElement: HTMLElement;
  statusTextElement: HTMLElement;
  gameIdInput: HTMLInputElement | null;
  gameIdError: HTMLElement | null;
  currentGameIdElement: HTMLElement | null;
  spectatorPillElement: HTMLElement | null;

  // Phase J Wave 2 — connection-lost banner elements + reconnect state.
  // The banner is the visible counterpart of the exponential-backoff
  // reconnect loop: yellow during attempts, red on failure, green flash
  // on successful reconnect.  All four optional so the banner can be
  // omitted (legacy tests / minimal HTML).
  private connectionBanner: HTMLElement | null;
  private connectionBannerText: HTMLElement | null;
  private connectionBannerActions: HTMLElement | null;
  private connectionBannerRetry: HTMLButtonElement | null;
  private connectionBannerLobby: HTMLButtonElement | null;
  // Phase J Wave 4 — copy-rejoin-link button + toast region.  Both are
  // optional so a stripped-down host page can omit them without breaking
  // the constructor.
  private connectionBannerCopyLink: HTMLButtonElement | null;
  private toastRegion: HTMLElement | null;
  private reconnectTimer: number | null = null;
  private reconnectFlashTimer: number | null = null;
  // True from the first disconnect until the next successful JOIN.  Used
  // to flash the green "Reconnected" pill only when the user actually saw
  // a stale connection (not on the first-page connect).
  private wasDisconnected: boolean = false;

  disconnecting = false;
  reconnectAttempts: number = 0;
  reconnectSeat: number | null = null;
  // Seat/start deadlock fix (Ferro) — one-shot URL `?seat=` handoff state. The
  // claim is deferred to the first authoritative `seats` snapshot (which arrives
  // just after JOINED) and only fires for an OPEN chair, so it never grabs an
  // occupied seat. `Bound` guards a single listener across the connection's life.
  private urlSeatHandoff: number | null = null;
  private urlSeatHandoffDecided: boolean = false;
  private urlSeatHandoffBound: boolean = false;
  // New Game UX P0 — debounce guard: one activation ⇒ exactly one navigation.
  private newGameInFlight: boolean = false;
  private creatingGameId: string | null = null;
  private terminalJoinRejection: string | null = null;
  private identityConnectPending = false;
  private connectEpoch = 0;
  // Phase I Wave 4 — true while the active page URL declares ?seat=-1.
  // Spectator state is page-URL-driven (set on init, refreshed on each
  // connect attempt) so a refresh that lands on the same URL re-joins as
  // spectator without surfacing the take-seat affordances even for the
  // brief moment between page load and WS JOINED.
  spectating: boolean = false;

  constructor(client: Client) {
    this.url = this.getUrl();
    this.client = client;

    this.nickElement = document.getElementById('nick')! as HTMLInputElement;
    this.nickElement.value = localStorage.getItem('autotable.nick') ?? '';

    this.nickElement.onchange = this.onNickChange.bind(this);
    this.nickElement.oninput = this.onNickChange.bind(this);

    this.client.on('connect', this.onConnect.bind(this));
    this.client.on('disconnect', this.onDisconnect.bind(this));
    this.client.on('protocol-unavailable', this.showProtocolUnavailable.bind(this));
    this.onNickChange();

    const connectButton = document.getElementById('connect')!;
    connectButton.onclick = () => this.connect();
    const disconnectButton = document.getElementById('disconnect')!;
    disconnectButton.onclick = this.disconnect.bind(this);
    // Upgrade the eager New Game action with live ownership/teardown without
    // adding a second listener or dropping an activation during lazy startup.
    bindNewGameControls(() => this.newGame());

    this.statusElement = document.getElementById('status') as HTMLElement;
    this.statusTextElement = document.getElementById('status-text') as HTMLElement;

    // Phase I Wave 3 — wire the lobby Game ID input.  Prefill from
    // ?gameId= if present + valid, otherwise the backend default.  Clear
    // any stale error message as the user edits, and re-validate on blur
    // so the red error doesn't linger after the user fixes it.
    this.gameIdInput =
      document.getElementById('lobby-gameId') as HTMLInputElement | null;
    this.gameIdError = document.getElementById('lobby-gameId-error');
    this.currentGameIdElement = document.getElementById('current-game-id');
    this.spectatorPillElement = document.getElementById('spectator-pill');
    if (this.gameIdInput !== null) {
      this.gameIdInput.value = this.readInitialGameId();
      this.gameIdInput.addEventListener('input', () => this.clearGameIdError());
      this.gameIdInput.addEventListener('blur', () => this.refreshGameIdValidity());
      this.gameIdInput.addEventListener('keydown', (event: KeyboardEvent) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          this.connect();
        }
      });
    }

    // Phase I Wave 4 — propagate the page URL's spectator flag onto the
    // body class + pill element BEFORE the first connect.  This keeps
    // the take-seat affordances hidden from the moment the page loads
    // (game-ui.ts updateSeats reads spectatorModeFromUrl()) and ensures
    // the pill is the right initial state on auto-reconnect.
    this.spectating = readSpectatorFromUrl();
    this.applySpectatorClass();

    // Phase J Wave 2 — wire the connection banner.  All elements are
    // optional so a stripped-down host page (e.g. unit-test harness) can
    // omit them without breaking the constructor.
    this.connectionBanner = document.getElementById('connection-banner');
    this.connectionBannerText =
      document.getElementById('connection-banner-text');
    this.connectionBannerActions =
      document.getElementById('connection-banner-actions');
    this.connectionBannerRetry =
      document.getElementById('connection-banner-retry') as HTMLButtonElement | null;
    this.connectionBannerLobby =
      document.getElementById('connection-banner-lobby') as HTMLButtonElement | null;
    if (this.connectionBannerRetry !== null) {
      this.connectionBannerRetry.addEventListener('click', () => this.manualRetry());
    }
    if (this.connectionBannerLobby !== null) {
      this.connectionBannerLobby.addEventListener('click', () => {
        // Punt the user back to the bare URL — the lobby auto-opens
        // when ?... is empty (see lobby.ts:shouldShowOnLoad).
        window.location.search = '';
      });
    }

    // Phase J Wave 4 — copy-rejoin-link button + toast region.
    this.connectionBannerCopyLink = document.getElementById(
      'connection-banner-copy-link') as HTMLButtonElement | null;
    this.toastRegion = document.getElementById('toast-region');
    if (this.connectionBannerCopyLink !== null) {
      this.connectionBannerCopyLink.addEventListener(
        'click', () => this.copyRejoinLink());
    }

    // Phase J Wave 4 — Surface a "session ended" toast on boot when the
    // URL carries a malformed / expired ?rejoin= token, so the user
    // knows why they landed on the lobby instead of a live game.  index.ts
    // already calls applyTokenToUrl() for valid tokens; this consumer
    // handles only the failure case (a valid token would have been
    // stripped + re-encoded on the URL by then).
    this.consumeRejoinTokenAtStartup();

    // Phase J Wave 5 — wire the post-game stats delta panel.  Fires
    // whenever the gameComplete singleton flips to complete, reading
    // the pre-game snapshot from profile.ts to build the delta.
    this.setupPostGameStatsPanel();
    this.client.on('update', (_entries, full) => {
      if (full) this.confirmNewGameCreation();
    });
    this.client.actionRejected.on('update', (entries) => {
      for (const [key, rejection] of entries) {
        if (key === 'current' && rejection?.action === 'join' && isJoinOnly()
            && ['room-not-found', 'room-full', 'room-not-seating'].includes(rejection.reason)) {
          this.rejectJoin(rejection.reason);
          continue;
        }
        if (key !== 'current' || rejection?.action !== 'room') continue;
        const message = t('actions.rejected', { reason: rejection.reason });
        this.setStatus(message);
        this.showBannerFailed();
        if (this.connectionBannerText !== null) this.connectionBannerText.textContent = message;
        this.showToast(message, 'error', 8000);
      }
    });
    getRuleActionControls(client);
  }

  private confirmNewGameCreation(): void {
    const gameId = this.creatingGameId;
    // JOINED may describe an unbound placeholder. Consume only after runtime binding.
    if (gameId === null || !this.client.connected()
        || this.client.lastGameId !== gameId || this.client.serverSnapshotGameId !== gameId
        || this.client.turn.get('current') === null
        || !hasNewGameIntent(window.location.search, gameId)) return;
    const query = new URLSearchParams(window.location.search);
    query.delete(NEW_GAME_INTENT_PARAM);
    history.replaceState(history.state, '', `${location.pathname}?${query}${location.hash}`);
    this.creatingGameId = null;
  }

  // Phase J Wave 5 — Post-game stats delta panel.
  //
  // The post-game modal already exists (game-ui.ts owns the seat-by-seat
  // scoreboard); we slot a sibling section into
  // #game-complete-stats-delta that shows the local player's career
  // stats with deltas vs. the pre-game snapshot stashed in profile.ts.
  //
  // Implementation: listen on the gameComplete collection (re-rendered
  // by Bishop's runtime when MaxHands is exhausted) and on profile
  // updates.  When both `complete=true` and a fresh profile are
  // available we render the delta; otherwise we render a no-delta
  // readout so the modal isn't empty between hands.
  private setupPostGameStatsPanel(): void {
    const host = document.getElementById('game-complete-stats-delta');
    if (host === null) return;

    const render = (): void => {
      const cur = this.client.gameComplete.get('current');
      const complete = cur !== null && cur !== undefined
        && readCompleteFlagFromUi(cur);
      if (!complete) {
        host.replaceChildren();
        hideEl(host);
        return;
      }
      const profile = getProfile();
      if (profile === null) {
        host.replaceChildren();
        hideEl(host);
        return;
      }
      const prev: PlayerStats | null = getPreGameSnapshot();
      host.replaceChildren();
      showEl(host);
      const delta = formatStatsDelta(profile.stats, prev);
      if (delta !== null) {
        host.appendChild(delta);
      } else {
        host.appendChild(this.buildStatsWithoutDelta(profile.stats));
      }
    };

    this.client.gameComplete.on('update', render);
    onProfile(render);
    // Initial paint in case both signals arrived before we wired up.
    render();
  }

  // Fallback render for the post-game modal when no pre-game snapshot
  // exists (first game in a fresh tab) — shows stats with no delta
  // badges so the panel isn't empty.
  private buildStatsWithoutDelta(stats: PlayerStats): DocumentFragment {
    const frag = document.createDocumentFragment();
    const section = document.createElement('div');
    section.className = 'game-complete-section stats-delta-section';
    const title = document.createElement('h4');
    title.className = 'game-complete-section-title';
    title.textContent = 'Your stats';
    section.appendChild(title);
    section.appendChild(formatStats(stats));
    frag.appendChild(section);
    return frag;
  }

  // #153 (Ferro) — Read a concrete ?gameId= from the URL for the input
  // prefill / no-input fallback.  Returns '' when the URL carries no valid
  // gameId so the field is left BLANK instead of pre-seeding the shared
  // legacy `changsha-default` room.  A blank target funnels the connect
  // path into an explicit fresh "New Game" (see connect()), which is what
  // makes the default navigation honest — the previous `changsha-default`
  // fallback silently JOINed one long-lived room and inherited its stale
  // seat/turn/deal state (Hudson #153).
  private readInitialGameId(): string {
    return readConcreteGameId(window.location.search) ?? '';
  }

  private clearGameIdError(): void {
    if (this.gameIdInput !== null) {
      this.gameIdInput.classList.remove('lobby-error-input');
    }
    if (this.gameIdError !== null) {
      hideEl(this.gameIdError);
    }
  }

  private showGameIdError(message: string): void {
    if (this.gameIdInput !== null) {
      this.gameIdInput.classList.add('lobby-error-input');
    }
    if (this.gameIdError !== null) {
      this.gameIdError.textContent = message;
      showEl(this.gameIdError);
    }
  }

  private refreshGameIdValidity(): boolean {
    if (this.gameIdInput === null) return true;
    const { value, error } = validateGameId(this.gameIdInput.value);
    if (error !== null) {
      this.showGameIdError(error);
      return false;
    }
    this.clearGameIdError();
    // Normalize the field to the trimmed value so subsequent reads are stable.
    if (value !== null && this.gameIdInput.value !== value) {
      this.gameIdInput.value = value;
    }
    return true;
  }

  // Phase I Wave 3 — Resolve the gameId to use for this connect attempt.
  // Priority: live input value (if present + valid) > URL ?gameId= > blank.
  // Returns null when the input holds an INVALID id so the caller can
  // surface the inline error and abort.  Returns '' when there is no
  // concrete target at all (blank field + no URL gameId): the caller then
  // funnels into an explicit fresh "New Game" (#153) rather than joining
  // the shared legacy `changsha-default` room.
  private resolveGameIdForConnect(): string | null {
    if (this.gameIdInput !== null) {
      // #153 — a blank field is NOT an error on connect: it means "no
      // explicit room", which the caller resolves to a fresh New Game.
      if (this.gameIdInput.value.trim() === '') {
        this.clearGameIdError();
        return this.readInitialGameId();
      }
      const { value, error } = validateGameId(this.gameIdInput.value);
      if (error !== null) {
        this.showGameIdError(error);
        this.gameIdInput.focus();
        return null;
      }
      if (value !== null) {
        this.clearGameIdError();
        if (this.gameIdInput.value !== value) {
          this.gameIdInput.value = value;
        }
        return value;
      }
    }
    // No input element on the page (legacy callers) — fall back to URL > blank.
    return this.readInitialGameId();
  }

  // Phase I Wave 4 — Build the WS connection URL with ?gameId= appended,
  // plus the spectator/seat/botCount query params parsed off the page
  // URL.  Bishop's WS endpoint reads these from
  // context.Request.Query at connect time (AutotableWsEndpoint.cs:174 for
  // seat, :192 for botCount), so they have to ride the WS URL — the page
  // URL alone doesn't reach the server.
  //
  // We forward seat, botCount, variant, dealMode, botDifficulty, seed and
  // handCount so:
  //   • Spectator (?seat=-1) actually reaches the backend as a "no seat"
  //     connection — the seat-take auto-fill is suppressed and the runtime
  //     auto-deals when bots fill the remaining seats.
  //   • The lobby's chosen botCount actually drives the backend's
  //     auto-bot-fill on this connection.
  //   • Phase K (bot-autoplay) — variant / dealMode / botDifficulty also
  //     ride the WS URL.  Previously the bundle silently dropped them and
  //     the backend always defaulted to dealMode=manual / botDifficulty=
  //     Medium, so the user's `?dealMode=auto` choice was effectively
  //     ignored on the wire (state.DealMode happens to default to Auto
  //     in the runtime so the auto-deal still fired, but logs reported
  //     the wrong mode and the lobby's botDifficulty never reached the
  //     server).  Forwarding them keeps server-side telemetry honest and
  //     lets the backend act on the lobby's settings end-to-end.
  private buildWsUrl(gameId: string): string {
    const params = new URLSearchParams();
    params.set('gameId', gameId);
    const pageQuery = new URLSearchParams(window.location.search);
    if (isJoinOnly()) {
      params.set('variant', 'changsha');
      params.set('join', '1');
      const separator = this.url.indexOf('?') >= 0 ? '&' : '?';
      return `${this.url}${separator}${params.toString()}`;
    }
    const seatRaw = pageQuery.get('seat');
    if (seatRaw !== null) {
      const seatNum = parseInt(seatRaw, 10);
      if (!isNaN(seatNum) && (seatNum === SPECTATOR_SEAT || (seatNum >= 0 && seatNum <= 3))) {
        params.set('seat', String(seatNum));
      }
    }
    const botCountRaw = pageQuery.get('botCount');
    if (botCountRaw !== null) {
      const bc = parseInt(botCountRaw, 10);
      if (!isNaN(bc) && bc >= 0 && bc <= 4) {
        params.set('botCount', String(bc));
      }
    }
    const botsRaw = pageQuery.get('bots');
    if (botsRaw === 'true' || botsRaw === 'false') params.set('bots', botsRaw);
    const variantRaw = pageQuery.get('variant');
    if (variantRaw !== null && variantRaw.length > 0 && variantRaw.length <= 32) {
      params.set('variant', variantRaw);
    }
    const dealModeRaw = pageQuery.get('dealMode');
    if (dealModeRaw === 'manual' || dealModeRaw === 'auto') {
      params.set('dealMode', dealModeRaw);
    }
    const botDifficultyRaw = pageQuery.get('botDifficulty');
    if (botDifficultyRaw !== null && botDifficultyRaw.length > 0 && botDifficultyRaw.length <= 32) {
      params.set('botDifficulty', botDifficultyRaw);
    }
    // Ferro WP-E/#120 (Hudson C-2 determinism gap) — forward the lobby's
    // `?seed=` so a chosen seed actually reaches the backend and reproduces
    // the same game.  The runtime seeds the wall shuffle + dice from this
    // value (ChangshaGameRuntime.CreateGameAsync → ChangshaGameState.Seed);
    // without forwarding it the WS path always bound with seed:null and the
    // deal was random regardless of the URL — determinism was unreachable.
    // Validate against the lobby's 0..0x7fffffff range (coerceSeed / MAX_SEED)
    // so only a value the backend can parse into a 32-bit int rides the wire.
    const seedRaw = pageQuery.get('seed');
    if (seedRaw !== null && /^\d+$/.test(seedRaw)) {
      const seedNum = Number(seedRaw);
      if (Number.isInteger(seedNum) && seedNum >= 0 && seedNum <= 0x7fffffff) {
        params.set('seed', String(seedNum));
      }
    }
    // Ferro WP-E/#120 (Ripley C-2 extension) — forward `?handCount=` so the
    // lobby's N-hand cap reaches the backend as a create-time param. Bishop
    // (WP-A) owns the backend read → ChangshaGameState.MaxHands (first-creator
    // wins); we only plumb the validated value onto the handshake. Whitelist
    // the canonical lobby options {1,4,8,16} — Vasquez §6.3 clamps at 16, so
    // 32 is not offered; absent/invalid ⇒ the runtime keeps its 4-hand default.
    const handCountRaw = pageQuery.get('handCount');
    if (handCountRaw !== null && ['1', '4', '8', '16'].includes(handCountRaw)) {
      params.set('handCount', handCountRaw);
    }
    const baseUnitRaw = pageQuery.get('baseUnit');
    if (baseUnitRaw !== null && (variantRaw ?? 'changsha').toLowerCase() === 'changsha') {
      const unit = parseBaseUnit(baseUnitRaw);
      params.set('baseUnit', unit === null ? baseUnitRaw : String(unit));
    }
    const separator = this.url.indexOf('?') >= 0 ? '&' : '?';
    return `${this.url}${separator}${params.toString()}`;
  }

  getUrlState(): string | null {
    const q = new URLSearchParams(window.location.search);
    return q.get('gameId');
  }

  setUrlState(gameId: string | null): void {
    const query = window.location.search;
    const q = new URLSearchParams(query);
    if (gameId !== null) {
      q.set('gameId', gameId);
    } else {
      q.delete('gameId');
    }
    const newQuery = q.toString();
    if (newQuery !== query.substring(1)) {
      // Phase I Wave 3 — replaceState (not pushState) so refresh re-joins
      // the same game without polluting browser history with every
      // connect attempt.  The previous pushState pre-Wave-3 was wrong
      // for the same reason but rarely triggered (only the server-issued
      // gameId hit this path).
      history.replaceState(undefined, '', newQuery ? '?' + newQuery : "");
    }
  }

  start(): void {
    if (this.getUrlState() !== null) {
      // If connecting right on page load, start from empty seat
      // (to prevent sudden change)
      this.client.seats.set(this.client.playerId(), { seat: null });

      this.connect();
    }
  }

  getUrl(): string {
    // @ts-ignore
    const env = process.env.NODE_ENV;

    if (env !== 'production') {
      return 'ws://localhost:1235';
    }

    let path = window.location.pathname;
    path = path.substring(1, path.lastIndexOf('/')+1);
    const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsHost = window.location.host;
    const wsPath = path + 'ws';
    return `${wsProtocol}//${wsHost}/${wsPath}`;
  }

  onNickChange(): void {
    if (this.client.connected()) {
      this.client.nicks.set(this.client.playerId(), this.nickElement.value);
    }
    localStorage.setItem('autotable.nick', this.nickElement.value);
  }

  onConnect(game: Game): void {
    this.connectionBannerText?.removeAttribute('data-testid');
    this.setStatus(null);
    document.getElementById('server')!.classList.add('connected');
    // Phase I Wave 3 — mirror the .connected toggle onto the lobby Game
    // ID row so its .server-disconnected / .server-connected children
    // swap visibility (the existing #server descendant selectors at
    // style.css:87-88 don't reach the row, which is a sibling).
    document.getElementById('lobby-gameId-row')?.classList.add('connected');
    this.onNickChange();
    this.setUrlState(game.gameId);
    document.getElementsByTagName('title')[0].innerText = TITLE_CONNECTED;

    // Phase I Wave 3 — surface the active game id in the sidebar so
    // the user always knows which game they're in.  The element is
    // gated visible via the .connected class toggle above.
    if (this.currentGameIdElement !== null) {
      this.currentGameIdElement.textContent = game.gameId;
    }
    // Clear any stale validation error that might still be visible
    // (defensive: connect normally blocks on an invalid input, but if
    // the field changed between attempts we don't want a red border
    // lingering after a successful join).
    this.clearGameIdError();

    // Phase I Wave 4 — re-evaluate spectator mode against the post-connect
    // URL (it can change between attempts when the lobby's Apply &
    // Start lands on a fresh URL, or the user edits ?seat= by hand).
    this.spectating = readSpectatorFromUrl();
    this.applySpectatorClass();

    // Phase I Wave 4 — spectators never reconnect into a seat.  The
    // reconnectSeat is captured pre-disconnect; for spectators that
    // capture is always null (no seat was taken), but we belt-and-brace
    // it here so a stray reconnectSeat from a prior seated session
    // doesn't accidentally seat the spectator.
    if (this.spectating || isJoinOnly()) {
      this.reconnectSeat = null;
      this.urlSeatHandoff = null;
      this.urlSeatHandoffDecided = true;
    } else if (this.reconnectSeat !== null) {
      this.client.seats.set(this.client.playerId(), { seat: this.reconnectSeat });
    } else {
      // Seat/start deadlock fix (Ferro live-UX rejection) — a FRESH concrete
      // Changsha connection carrying a `?seat=N` handoff (New Game / Quick Match
      // / Apply & Start / seat preview / shared link) must turn that hint into a
      // real chair via the STANDARD seat command, now that WS is ready. The old
      // code only auto-seated on RECONNECT (reconnectSeat !== null), so a fresh
      // URL seat was never acted on: the runtime waited for a seat command that
      // never came, so seats stayed EMPTY (no bots, no auto-deal) and the table
      // deadlocked. We DEFER the claim to the first authoritative `seats`
      // snapshot and only take the chair when it is genuinely OPEN — so a URL
      // `?seat=` pointed at an OCCUPIED chair never grabs a stranger's seat or
      // projects their concealed hand; the viewer keeps honest Take-Seat controls
      // instead. Relay variants keep their manual Take-Seat flow (gated to
      // authoritative Changsha; absent variant defaults to Changsha).
      const variant = readVariantFromUrl();
      if (variant === null || variant === 'CHANGSHA') {
        const urlSeat = readPlayerSeatFromUrl();
        if (urlSeat !== null) {
          this.armUrlSeatHandoff(urlSeat);
        }
      }
    }

    // Phase J Wave 2 — flash the green "Reconnected" banner only when
    // the user actually saw a stale connection.  Reset the disconnected
    // flag + attempts counter so the next disconnect starts fresh.
    if (this.wasDisconnected) {
      this.showBannerSuccess();
    } else {
      this.hideBanner();
    }
    this.wasDisconnected = false;
    this.reconnectAttempts = 0;
    this.clearReconnectTimer();
  }

  // Seat/start deadlock fix (Ferro) — arm the URL `?seat=` handoff. The
  // authoritative `seats` snapshot lands just AFTER the JOINED/`connect` event
  // (see base-client.ts onMessage), so we cannot read occupancy synchronously
  // here; instead we react to the first `seats` update and claim the chair ONLY
  // when it is confirmed OPEN (or already ours). Bound once per connection life;
  // the decision is made a single time so it never fights a later manual seat.
  private armUrlSeatHandoff(urlSeat: number): void {
    this.urlSeatHandoff = urlSeat;
    this.urlSeatHandoffDecided = false;
    if (!this.urlSeatHandoffBound) {
      this.urlSeatHandoffBound = true;
      this.client.seats.on('update', () => this.maybeClaimUrlSeat());
    }
    // NOTE: we deliberately do NOT claim synchronously here — at `connect` time
    // the seats snapshot has not arrived yet (seatPlayers is empty), so an
    // immediate claim could not tell an OPEN chair from an occupied one. The
    // handler above fires on the first authoritative `seats` update (the server
    // sends one on connect, empty or not) and decides then.
  }

  private maybeClaimUrlSeat(): void {
    if (isJoinOnly()) return;
    if (this.urlSeatHandoffDecided) return;
    const urlSeat = this.urlSeatHandoff;
    if (urlSeat === null) return;
    // A spectator (or a seat we've already confirmed via reconnect/manual/earlier
    // claim) needs no handoff — settle without claiming.
    if (this.spectating || this.client.seat !== null) {
      this.urlSeatHandoffDecided = true;
      return;
    }
    const me = this.client.playerId();
    const owner = this.client.seatPlayers[urlSeat] ?? null;
    // Decide once on this authoritative snapshot. Claim ONLY an OPEN chair (or
    // one already ours). An occupied chair is left untouched so a URL `?seat=`
    // spoof can neither seize a stranger's seat nor project their concealed hand
    // — the viewer keeps the honest Take-Seat controls (updateSeats renders them
    // off the confirmed client.seat).
    this.urlSeatHandoffDecided = true;
    if (owner === null || owner === me) {
      this.client.seats.set(me, { seat: urlSeat });
    }
  }

  onDisconnect(game: Game | null): void {
    document.getElementById('server')!.classList.remove('connected');
    document.getElementById('lobby-gameId-row')?.classList.remove('connected');
    document.getElementsByTagName('title')[0].innerText = TITLE_DISCONNECTED;

    if (this.terminalJoinRejection !== null) {
      this.rejectJoin(this.terminalJoinRejection);
      return;
    }
    if (this.client.protocolUnavailableReason !== null) {
      this.showProtocolUnavailable(this.client.protocolUnavailableReason);
      return;
    }
    if (this.disconnecting) {
      // User-initiated disconnect (Disconnect button, hot-seat swap,
      // Apply & Start).  Don't auto-reconnect or surface a banner — the
      // page is intentionally going stale.
      (document.getElementById('connect')! as HTMLButtonElement).disabled = false;
      this.disconnecting = false;
      this.hideBanner();
      return;
    }

    if (game) {
      // First drop in a chain — capture the seat for re-take and start
      // the exponential-backoff schedule from attempt 1.
      this.reconnectSeat = this.client.seat;
      this.wasDisconnected = true;
      this.reconnectAttempts = 0;
      this.scheduleReconnect();
    } else if (this.wasDisconnected && this.reconnectAttempts < RECONNECT_MAX_ATTEMPTS) {
      // Continuation of a reconnect chain — another attempt failed; keep
      // climbing the backoff ladder.
      this.scheduleReconnect();
    } else {
      // Either we never connected in the first place, or we exhausted
      // every reconnect attempt.
      (document.getElementById('connect')! as HTMLButtonElement).disabled = false;
      if (this.wasDisconnected) {
        this.showBannerFailed();
      }
    }
  }

  // Phase J Wave 2 — schedule the next reconnect attempt according to
  // the exponential-backoff ladder, with the connection banner reflecting
  // the next-attempt timer.  Attempts run 1..RECONNECT_MAX_ATTEMPTS;
  // beyond that onDisconnect surfaces the failure banner.
  private scheduleReconnect(): void {
    if (this.terminalJoinRejection !== null) return;
    if (this.client.protocolUnavailableReason !== null) return;
    if (this.reconnectAttempts >= RECONNECT_MAX_ATTEMPTS) {
      this.showBannerFailed();
      return;
    }
    const delay = RECONNECT_DELAYS_MS[this.reconnectAttempts];
    const attemptNumber = this.reconnectAttempts + 1;
    this.showBannerReconnecting(attemptNumber, RECONNECT_MAX_ATTEMPTS);
    this.clearReconnectTimer();
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = null;
      this.reconnectAttempts += 1;
      this.connect(undefined, this.reconnectSeat ?? undefined);
    }, delay);
  }

  // Phase J Wave 2 — user clicked Retry on the failed-reconnect banner.
  // Resets the chain and starts again from delay #1, so a "ladder
  // exhausted" state is recoverable without a page reload.
  private manualRetry(): void {
    if (this.terminalJoinRejection !== null) return;
    if (this.client.protocolUnavailableReason !== null) {
      this.wasDisconnected = false;
      this.reconnectAttempts = 0;
      this.connect();
      return;
    }
    this.wasDisconnected = true;
    this.reconnectAttempts = 0;
    this.scheduleReconnect();
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== null) {
      window.clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  private rejectJoin(reason: string): void {
    this.terminalJoinRejection = reason;
    this.creatingGameId = null;
    this.wasDisconnected = false;
    this.reconnectSeat = null;
    this.clearReconnectTimer();
    const message = t(`room.join.${reason}`);
    this.setStatus(message);
    this.showBannerFailed();
    if (this.connectionBannerText !== null) {
      this.connectionBannerText.textContent = message;
      this.connectionBannerText.setAttribute('data-testid', 'room-join-error');
    }
    if (this.connectionBannerRetry !== null) hideEl(this.connectionBannerRetry);
    if (this.connectionBannerCopyLink !== null) hideEl(this.connectionBannerCopyLink);
    (document.getElementById('connect') as HTMLButtonElement).disabled = true;
  }

  private showProtocolUnavailable(reason: string): void {
    this.reconnectSeat = null;
    this.wasDisconnected = false;
    this.clearReconnectTimer();
    const message = t('room.connection_failed', { reason: `protocol-unavailable: ${reason}` });
    this.setStatus(message);
    this.showBannerFailed();
    if (this.connectionBannerText !== null) {
      this.connectionBannerText.textContent = message;
      this.connectionBannerText.setAttribute('data-testid', 'protocol-unavailable');
    }
    if (this.connectionBannerCopyLink !== null) hideEl(this.connectionBannerCopyLink);
    if (this.connectionBannerRetry !== null) showEl(this.connectionBannerRetry);
    (document.getElementById('connect') as HTMLButtonElement).disabled = false;
  }

  private showBannerReconnecting(attempt: number, max: number): void {
    if (this.connectionBanner === null || this.connectionBannerText === null) return;
    if (this.reconnectFlashTimer !== null) {
      window.clearTimeout(this.reconnectFlashTimer);
      this.reconnectFlashTimer = null;
    }
    this.connectionBanner.className =
      'connection-banner connection-banner-reconnecting';
    this.connectionBannerText.textContent =
      `⚠️ Connection lost — reconnecting… (attempt ${attempt}/${max})`;
    if (this.connectionBannerActions !== null) {
      // Phase J Wave 4 — reveal the actions row (incl. copy-link) once
      // the first retry has failed; the directive's acceptance criterion
      // is "after the first failed retry (1s)", i.e. attempt >= 2.
      // Until then the banner stays informational-only.
      if (attempt >= 2) {
        showEl(this.connectionBannerActions);
        if (this.connectionBannerCopyLink !== null) {
          showEl(this.connectionBannerCopyLink);
        }
      } else {
        hideEl(this.connectionBannerActions);
      }
    }
    showEl(this.connectionBanner);
    this.connectionBanner.style.display = 'flex';
  }

  private showBannerFailed(): void {
    if (this.connectionBanner === null || this.connectionBannerText === null) return;
    if (this.reconnectFlashTimer !== null) {
      window.clearTimeout(this.reconnectFlashTimer);
      this.reconnectFlashTimer = null;
    }
    this.connectionBanner.className =
      'connection-banner connection-banner-failed';
    this.connectionBannerText.textContent = '❌ Could not reconnect.';
    if (this.connectionBannerActions !== null) {
      showEl(this.connectionBannerActions);
    }
    // Phase J Wave 4 — keep copy-link visible on the failure banner so
    // the user can still hand the URL to themselves (e.g. paste into a
    // chat) before clicking Back to Lobby.
    if (this.connectionBannerCopyLink !== null) {
      showEl(this.connectionBannerCopyLink);
    }
    showEl(this.connectionBanner);
    this.connectionBanner.style.display = 'flex';
  }

  private showBannerSuccess(): void {
    if (this.connectionBanner === null || this.connectionBannerText === null) return;
    this.connectionBanner.className =
      'connection-banner connection-banner-success';
    this.connectionBannerText.textContent = '✅ Reconnected.';
    if (this.connectionBannerActions !== null) {
      hideEl(this.connectionBannerActions);
    }
    // Phase J Wave 4 — also tuck the copy-link button away on a
    // successful reconnect so the success state is clean.
    if (this.connectionBannerCopyLink !== null) {
      hideEl(this.connectionBannerCopyLink);
    }
    showEl(this.connectionBanner);
    this.connectionBanner.style.display = 'flex';
    if (this.reconnectFlashTimer !== null) {
      window.clearTimeout(this.reconnectFlashTimer);
    }
    this.reconnectFlashTimer = window.setTimeout(() => {
      this.reconnectFlashTimer = null;
      this.hideBanner();
    }, RECONNECT_OK_FLASH_MS);
  }

  private hideBanner(): void {
    if (this.connectionBanner === null) return;
    hideEl(this.connectionBanner);
    if (this.connectionBannerActions !== null) {
      hideEl(this.connectionBannerActions);
    }
    // Phase J Wave 4 — reset the copy-link button so subsequent
    // reconnect cycles start with it hidden (it's revealed at attempt
    // >= 2 inside showBannerReconnecting).
    if (this.connectionBannerCopyLink !== null) {
      hideEl(this.connectionBannerCopyLink);
    }
  }

  // Phase J Wave 4 — Copy a rejoin URL for this session to the
  // clipboard.  Prefers the modern navigator.clipboard API; falls back
  // to a hidden textarea + document.execCommand('copy') for older
  // browsers and embedded WebViews; final fallback surfaces the URL
  // inside a long-lived toast for manual copy.
  private copyRejoinLink(): void {
    const gameId = this.client.lastGameId;
    if (gameId === null) {
      this.showToast(
        'No live session yet — nothing to share.', 'info', 4000);
      return;
    }
    const session = readSession(gameId);
    if (session === null) {
      this.showToast(
        'No live session yet — nothing to share.', 'info', 4000);
      return;
    }
    const url = buildRejoinUrl(session);
    const onSuccess = (): void => {
      this.showToast('🔗 Rejoin link copied to clipboard.', 'info', 4000);
    };
    const onFail = (): void => {
      this.showToast(
        `Copy failed — manually share this URL: ${url}`, 'error', 12000);
    };
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(url).then(onSuccess, () => {
          if (!this.execCommandCopy(url)) onFail();
          else onSuccess();
        });
        return;
      }
    } catch {
      /* fall through to execCommand */
    }
    if (this.execCommandCopy(url)) onSuccess();
    else onFail();
  }

  // Phase J Wave 4 — Legacy clipboard fallback.  Some embedded WebViews
  // (e.g. older Electron, certain in-app browsers) ship without
  // navigator.clipboard; document.execCommand('copy') is the only
  // synchronous path that works there.  Returns true iff the copy
  // succeeded so the caller can route to the manual-copy toast on
  // failure.
  private execCommandCopy(text: string): boolean {
    try {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      ta.style.left = '-9999px';
      document.body.appendChild(ta);
      ta.focus();
      ta.select();
      const ok = document.execCommand('copy');
      document.body.removeChild(ta);
      return ok;
    } catch {
      return false;
    }
  }

  // Phase J Wave 4 — Inline toast helper.  Pushes a short notice into
  // the aria-live region with severity + auto-dismiss.  Severity
  // controls the colour band: info = blue, error = red.
  private showToast(
    message: string,
    severity: 'info' | 'error' = 'info',
    duration: number = 4000,
  ): void {
    if (this.toastRegion === null) return;
    const toast = document.createElement('div');
    toast.className = `toast toast-${severity}`;
    toast.setAttribute('role', severity === 'error' ? 'alert' : 'status');
    toast.setAttribute(
      'data-testid', severity === 'error' ? 'toast-error' : 'toast-info');
    toast.textContent = message;
    this.toastRegion.appendChild(toast);
    // Force the entry animation by deferring the visible class.
    window.requestAnimationFrame(() => {
      toast.classList.add('toast-visible');
    });
    window.setTimeout(() => {
      toast.classList.remove('toast-visible');
      window.setTimeout(() => {
        if (toast.parentNode !== null) {
          toast.parentNode.removeChild(toast);
        }
      }, 400);
    }, duration);
  }

  // Phase J Wave 4 — On page load, if the URL carried `?rejoin=…`
  // but the token parser couldn't accept it (malformed or expired),
  // surface a "session ended" toast so the user knows what happened.
  // The valid-token path is handled in index.ts before this constructor
  // runs (it strips the rejoin param and re-encodes gameId+seat); by
  // the time we get here a leftover `?rejoin=` is always invalid.
  private consumeRejoinTokenAtStartup(): void {
    if (window.location.search.indexOf('rejoin=') < 0) return;
    const decoded = parseRejoinFromUrl();
    if (decoded === null) {
      this.showToast(
        'Your previous session has ended.', 'info', 6000);
      clearRejoinFromUrl();
    }
    void (decoded as RejoinHandled | null);
  }

  // Phase I Wave 4 — mirror the URL-derived spectator state onto the body
  // class + pill element.  Centralised so we can call it from the ctor
  // (before first connect), onConnect (after a URL transition), and
  // disconnect (so the body class stays in sync with the URL).
  private applySpectatorClass(): void {
    document.body.classList.toggle('spectating', this.spectating);
    if (this.spectatorPillElement !== null) {
      this.spectatorPillElement.classList.toggle('visible', this.spectating);
    }
  }

  setStatus(status: string | null): void {
    if (status !== null) {
      showEl(this.statusElement);
      this.statusTextElement.innerText = status;
    } else {
      hideEl(this.statusElement);
    }
  }

  // Phase J Wave 2 — connect() signature simplified: legacy callers used
  // `reconnectAttempts` to drive the constant-delay loop; the new
  // exponential-backoff path in scheduleReconnect / onDisconnect owns the
  // attempts counter directly, so connect() no longer takes (or resets) it.
  // `reconnectSeat` is still passed by the reconnect loop so the seat is
  // re-taken after JOIN.
  connect(_legacy?: undefined, reconnectSeat?: number): void {
    if (this.client.connected() || this.identityConnectPending || this.terminalJoinRejection !== null) {
      return;
    }
    const query = new URLSearchParams(window.location.search);
    const baseUnit = query.get('baseUnit');
    if (!isJoinOnly() && (query.get('variant') ?? 'changsha').toLowerCase() === 'changsha'
        && baseUnit !== null && (parseBaseUnit(baseUnit) === null || query.getAll('baseUnit').length !== 1)) {
      const message = t('lobby.base_unit_error', { max: MAX_BASE_UNIT });
      this.setStatus(message);
      this.showToast(message, 'error', 8000);
      (document.getElementById('connect') as HTMLButtonElement).disabled = false;
      return;
    }

    // Phase I Wave 3 — resolve the gameId from the live input (or URL /
    // default fallback), validate, and bake it into both the WS URL
    // (where Bishop's lifted AutotableWsEndpoint parses it as the
    // routing key) and the page URL (so refresh re-joins the same
    // game).  Validation failures show inline and abort the connect.
    const gameId = this.resolveGameIdForConnect();
    if (gameId === null) {
      return;
    }

    // #153 (Ferro) — New Game funnel.  A blank connect target ('') means the
    // navigation carries no concrete gameId (bare `/` or `?variant=changsha`
    // with the field left empty).  Rather than silently JOINing the shared
    // legacy `changsha-default` room — which inherits its stale seat/turn/deal
    // state (Hudson #153) and connects with dealMode/botCount absent (runtime
    // runs Manual while the lobby UI implies Auto) — redirect to an explicit,
    // honest fresh-game URL: a minted non-colliding gameId plus the same
    // default config the lobby's Apply & Start emits (variant / dealMode=auto
    // / botCount=3 / botDifficulty / handCount).  The reload re-reads this one
    // consistent source of truth for BOTH the on-screen config display and the
    // WS handshake, and start()'s `getUrlState() !== null` guard then
    // auto-connects into the new isolated game.
    //
    // A concrete gameId already on the URL (deliberate reload / reconnect /
    // shared-room join) never reaches this branch, so creator-wins semantics
    // and reconnect are preserved.
    if (gameId === '') {
      if (isJoinOnly()) {
        this.rejectJoin('room-not-found');
        return;
      }
      window.location.replace(
        buildFreshGameUrl(window.location.pathname, window.location.search),
      );
      return;
    }
    this.creatingGameId = !isJoinOnly() && hasNewGameIntent(window.location.search, gameId) ? gameId : null;
    this.setUrlState(gameId);

    // Phase I Wave 4 — re-evaluate spectator mode every connect attempt
    // (the URL can shift between attempts when Apply & Start lands on a
    // fresh URL, or the user hand-edits ?seat=).
    this.spectating = readSpectatorFromUrl();
    this.applySpectatorClass();

    (document.getElementById('connect')! as HTMLButtonElement).disabled = true;
    // Preserve any reconnectSeat passed in (used by the reconnect loop to
    // re-take the player's previous chair on JOIN).  A bare manual
    // connect leaves it null.
    if (reconnectSeat !== undefined) {
      this.reconnectSeat = reconnectSeat;
    } else {
      this.reconnectSeat = null;
    }
    const wsUrl = this.buildWsUrl(gameId);
    void this.connectWithVerifiedIdentity(wsUrl, gameId);
  }

  private async connectWithVerifiedIdentity(wsUrl: string, gameId: string): Promise<void> {
    const epoch = ++this.connectEpoch;
    this.identityConnectPending = true;
    this.setStatus(t('room.identity_loading'));
    try {
      const identity = await bootstrapIdentity();
      if (epoch !== this.connectEpoch) return;
      if (identity === null) {
        const message = t('room.identity_failed', { reason: getIdentityBootstrapState().error ?? '' });
        this.setStatus(message);
        this.showBannerFailed();
        if (this.connectionBannerText !== null) this.connectionBannerText.textContent = message;
        (document.getElementById('connect') as HTMLButtonElement).disabled = false;
        return;
      }
      this.setStatus(t('room.connecting'));
      if (this.creatingGameId === gameId && !isJoinOnly()) this.client.new(wsUrl);
      else this.client.join(wsUrl, gameId);
    } catch (failure) {
      if (epoch !== this.connectEpoch) return;
      const message = t('room.connection_failed', { reason: failure instanceof Error ? failure.message : String(failure) });
      this.setStatus(message);
      this.showBannerFailed();
      if (this.connectionBannerText !== null) this.connectionBannerText.textContent = message;
      (document.getElementById('connect') as HTMLButtonElement).disabled = false;
    } finally {
      if (epoch === this.connectEpoch) this.identityConnectPending = false;
    }
  }

  disconnect(): void {
    this.connectEpoch++;
    this.identityConnectPending = false;
    this.disconnecting = true;
    // Phase J Wave 2 — cancel any in-flight reconnect timer so the
    // user-initiated disconnect doesn't race against a pending retry.
    this.clearReconnectTimer();
    this.wasDisconnected = false;
    // Phase J Wave 4 — user-initiated teardown: drop the reconnect
    // session so a subsequent refresh doesn't auto-rejoin an already-left
    // game.
    this.client.clearReconnectSession();
    this.client.disconnect();
    // this.setUrlState(null);
  }

  newGame(): void {
    // New Game UX P0 — one-click AUTHORITATIVE fresh game. A single activation:
    //   • mints a cryptographically-fresh gameId (buildFreshGameUrl →
    //     mintFreshGameId: crypto.randomUUID/getRandomValues);
    //   • PRESERVES variant/dealMode/botCount/botDifficulty/handCount (verbatim
    //     from the URL) + the durable player identity (playerId persists — we
    //     only drop the OLD game's reconnect token so we don't rejoin it);
    //   • hands off the last OWNED seat (live seat, else the pre-disconnect
    //     reconnectSeat — resolveHandoffSeat) so the fresh empty game auto-takes
    //     THAT chair with NO re-pick / Take-Seat click;
    //   • RELEASES the old game deterministically — a clean WS close NOW (not
    //     merely the implicit socket drop on unload) so the backend frees the
    //     old seat/runtime immediately, never left lingering — then navigates
    //     exactly ONCE (location.replace) and re-bootstraps into the fresh
    //     isolated game (the WS handshake forwards seat/botCount/dealMode so the
    //     backend auto-seats bots + auto-deals in Auto to a playable hand, or
    //     begins the manual ceremony).
    // NO legacy world.deal/setup/local-match, NO confirmation/setup dialog, NO
    // stale-game reuse. Debounced so rapid/double clicks are idempotent (exactly
    // ONE navigation); the guard is released if navigation fails to even start so
    // the control can never wedge permanently. Works while disconnected or at
    // GameComplete (pure URL compute + navigate).
    if (this.newGameInFlight) return;
    this.newGameInFlight = true;
    this.connectEpoch++;
    this.identityConnectPending = false;
    try {
      // Capture the handoff seat FIRST — from the LIVE game (client.seat) while
      // it's still intact, else the pre-disconnect reconnectSeat — so the
      // teardown below can never erase the chair we're handing off.
      const ownedSeat = resolveHandoffSeat(this.client.seat, this.reconnectSeat);

      // Release / retire the OLD game (mirrors the intentional-teardown path of
      // disconnect()), so it is cleaned up rather than left lingering:
      //   • mark the teardown intentional + cancel any pending reconnect timer
      //     so the ws close below takes the user-initiated branch in
      //     onDisconnect (no reconnect ladder, no "disconnected" banner flash);
      //   • clearReconnectSession drops the OLD game's reconnect token + SignalR
      //     hub + reactive game-state snapshot (the durable playerId identity is
      //     preserved — it rides the HttpOnly mahjong_pid cookie across reload,
      //     so the fresh game re-derives the SAME player);
      //   • disconnect() sends a clean WS close NOW so the backend releases the
      //     old seat immediately instead of waiting on the browser to drop the
      //     socket during unload.
      this.disconnecting = true;
      this.clearReconnectTimer();
      this.client.clearReconnectSession();
      this.client.disconnect();

      // SINGLE navigation into the fresh isolated game.
      window.location.replace(
        buildFreshGameUrl(window.location.pathname, window.location.search, undefined, ownedSeat),
      );
    } catch (err) {
      // Navigation could not even be initiated — release the debounce guard so a
      // retry is possible rather than leaving New Game permanently dead.
      this.newGameInFlight = false;
      throw err;
    }
  }
}

// Phase J Wave 4 — guard type for the rejoin consumer below.  Silences
// the unused-import warning for the `void` cast in consumeRejoinTokenAtStartup
// without exporting an extra helper.
type RejoinHandled = {
  token: string;
  decoded: SessionToken;
};

// Phase J Wave 5 — read the complete flag from a gameComplete payload.
// Mirrors client.ts:readCompleteFlag (kept local here so client-ui.ts
// doesn't need a runtime import from client.ts beyond the type).
function readCompleteFlagFromUi(v: GameCompleteEntry): boolean {
  return Boolean(
    v.isComplete
    || v.IsComplete
    || v.isGameComplete
    || v.IsGameComplete,
  );
}

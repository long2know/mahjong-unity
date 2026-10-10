# Frontend DOM selectors

This document is the **source of truth** for stable `data-testid` selectors on
the Changsha Mahjong frontend. Future Playwright / Cypress integration tests
(scheduled for a post-Phase-J phase) target these IDs, so:

- Hicks-managed code MUST keep these selectors stable across renames /
  refactors.
- Adding a new testid to the contract means appending here in the same PR.
- Removing or renaming a testid is a breaking change — open a coordinated
  Hicks ↔ Vasquez memo before doing it.

Format conventions (Phase J Wave 5):

- All testids are **kebab-case**, prefixed with their surface
  (`lobby-`, `connection-banner-`, `hud-`, …) so a CSS selector
  `[data-testid^="lobby-"]` finds an entire surface.
- Dynamic / indexed testids carry the `{0..N}` suffix in this doc; at
  runtime they are filled in by template-literal interpolation in TS
  (e.g., `` `lobby-player-chip-${i}` ``).
- Each entry cites the file + line of origin so a future grep keeps the
  doc in sync.

> **Maintenance note.** The catalog is captured as of Phase J Wave 5
> (Hicks's working-tree commit `feat(frontend): phase j wave 5 — public
> matchmaking lobby + profile drawer + stats panel`). When you change a
> testid:
> 1. Update the citation lines below.
> 2. Bump the relevant section's "Phase" annotation.
> 3. Run `grep -rn 'data-testid' src/frontend/autotable-src/` to detect
>    drift before commit.

---

## Lobby

Pre-game seat selection / rule-set picker. The lobby is hidden by
`body.lobby-active` while a game is in flight; the toggle button in
the top-left re-opens it for new-game configuration.

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="lobby-toggle"` | `<button id="lobby-toggle">` | Top-left hamburger that opens / closes the lobby panel mid-session. | `src/frontend/autotable-src/index.html:453` |
| `data-testid="lobby-players-section"` | `<section>` | Wrapper around the joined-players strip; visible in the lobby panel header. | `src/frontend/autotable-src/index.html:693` |
| `data-testid="lobby-players-strip"` | `<div>` | Horizontal flex container holding one chip per joined player. | `src/frontend/autotable-src/index.html:697` |
| `data-testid="lobby-players-empty"` | `<div>` | Placeholder rendered inside `lobby-players-strip` when no humans have joined yet. Dynamically injected. | `src/frontend/autotable-src/src/lobby.ts:707, 726` |
| `data-testid="lobby-player-chip-{0..3}"` | `<div class="lobby-player-chip">` | Per-seat joined-player chip. `chipIndex` is the seat the player took (0=East, 1=South, 2=West, 3=North). Dynamically injected; `data-seat` attribute carries the same seat number for non-test selectors. | `src/frontend/autotable-src/src/lobby.ts:802` |
| `data-testid="lobby-seat-preview"` | `<div class="lobby-seat-preview">` | The 4-cell seat-grid preview shown above the lobby's apply button. | `src/frontend/autotable-src/index.html:705` |
| `data-testid="lobby-seat-preview-{0..3}"` | `<div class="lobby-seat-preview-cell">` | One preview cell per seat. Reflects the current rule-set's bot-mix preview state. Dynamically injected. | `src/frontend/autotable-src/src/lobby.ts:668` |
| `data-testid="lobby-quick-match"` | `<button>` | "Quick Match" deliberately creates a fresh one-human/three-Medium-bot game. Use **Apply**, not Quick Match, when testing the bot-count picker. | `src/frontend/autotable-src/src/lobby.ts` |
| `data-testid="lobby-variant-fieldset"` | `<fieldset>` | Rule-set variant picker (e.g., changsha-v1, changsha-v2). | `src/frontend/autotable-src/index.html:1154` |
| `data-testid="lobby-bot-difficulty-fieldset"` | `<fieldset>` | Bot strength tier picker (Easy / Medium / Hard). | `src/frontend/autotable-src/index.html:1214` |
| `data-testid="lobby-hand-count-fieldset"` | `<fieldset>` | N-hand cap picker. Defaults to 4 (one east-wind rotation); higher values raise `ChangshaGameState.MaxHands`. | `src/frontend/autotable-src/index.html:1238` |
| `data-testid="lobby-open-settings"` | `<button>` | Opens the in-game settings drawer (top-right slide-out) without leaving the lobby. | `src/frontend/autotable-src/index.html:803` |
| `data-testid="lobby-apply"` | `<button>` | Primary CTA — "Apply" / "Start Game". Submits the lobby form and constructs the new-game URL with the chosen variant / difficulty / hands. | `src/frontend/autotable-src/index.html:1536` |

## Mobile drawers

### C-1 collection directions and authoritative dispatch

`protocol-conformance.spec.ts` compares the exact registered kind set, resolves
backend constants, and checks the authoritative `HandleUpdateAsync` branches.
New kinds require an explicit classification; none are implicitly cosmetic.

| Kinds | Direction / authoritative handling |
|---|---|
| `seats`, `claim`, `pickup`, `discard`, `match` | Existing guarded game-command routes. Server-owned scene state is never accepted as arbitrary client authority. |
| `ownTurn` | Server availability plus guarded client own-turn commands through `TryHandleOwnTurnActionAsync`; not cosmetic passthrough. |
| `handResultAck` | Client command, key `current`, exact `{gameId, handNumber, resultToken}` from the held result. `TryHandleHandResultAckAsync` validates owning connection and hand/token identity. No claimed seat, player ID or version. |
| `result`, `gameComplete`, `turn`, `actionRejected` | Server-only collections. Client pushes are explicitly ignored; never echoed, passed through or classified as commands. |
| `things`, `dice` | Legacy relay scene writes. Authoritative Changsha explicitly drops them and corrects the sender's scene. |
| `nicks`, `mouse`, `sound` | Explicit existing cosmetic passthrough set. |

The exact registered set is these 16 kinds. C-1 `viewer` authority remains a
server-envelope field, never a writable collection.

### Authoritative hand-result continuation

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="hand-result-dialog"` | `#result-modal` | Held result; `data-continuation-state` reports its UI state. | `index.html`, `src/game-ui.ts` |
| `data-testid="hand-result-continue"` | `#result-next` | Explicit hand-bound acknowledgement; observers dismiss locally instead. | `index.html`, `src/game-ui.ts` |
| `data-testid="hand-result-status"` | `#result-dialog-status` | Continue, sending, waiting for other players, or reconnect status. | `index.html`, `src/game-ui.ts` |
| `data-testid="hand-result-error"` | `#result-dialog-error` | Specific server rejection reason without dismissing the result. | `index.html`, `src/game-ui.ts` |
| `data-testid="hand-result-refresh"` | `#result-refresh` | User-requested reconnect/status refresh; never acknowledges by itself. | `index.html`, `src/game-ui.ts` |

### Mobile/table hand presentation (September 2026)

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `#main canvas` | WebGL canvas | Own concealed hand stays visible and interactive on the board in portrait, landscape and desktop. | `src/main-view.ts`, `src/world.ts` |
| `data-testid="settings-button"` | Existing app settings gear | Opens Display first during compact-screen gameplay; desktop/lobby retain their usual first tab. No additional gear. | `index.html`, `src/settings-drawer.ts` |
| `data-testid="settings-perspective-toggle"` | `<input type="checkbox">` | Clearly labeled Perspective view under Settings → Display, above Hand order. Immediate camera update; persisted and synchronized with `#perspective` and P. | `src/settings-drawer.ts`, `src/game.ts` |
| `#settings-toggle` / `#settings-drawer` | Existing per-game gear/drawer | The original Bot Strength / Hand Count / Auto-Deal / Sound surface now also exposes both display preferences at the top. | `index.html`, `src/game-ui.ts` |
| `data-testid="settings-game-perspective"` | `<input type="checkbox">` | Immediate Perspective view control in the original Settings drawer; synchronized with Display and P, without Apply & Restart. | `index.html`, `src/settings-drawer.ts` |
| `data-testid="settings-game-hand-sort"` | `<select>` | Both hand-order choices in the original drawer; same persistent on-board sorting as the app settings surface. | `index.html`, `src/settings-drawer.ts` |
| `data-testid="settings-hand-sort"` | `<select>` | Settings → Display → Hand order: persisted Suit + rank / Pairs-triples-first mode, applied to the actual board hand. | `src/settings-drawer.ts` |
| `data-testid="settings-mobile-table-status"` | `<input type="checkbox">` | Persisted compact-screen table-status visibility; default off, desktop unaffected. | `src/settings-drawer.ts` |

The playability pointer helper observes the presented on-board raycast position
on every screen. `_mobile-geometry.ts` measures submitted meshes/instances and
checks their screen coordinates against the real raycast targets. Mobile tests
tap those canvas coordinates; desktop tests use normal mouse presses. Sorting
tests choose both modes through the accessible settings listbox and verify the
physical ID sent and acknowledged by the server. No DOM hand tiles, synthetic
gameplay events or direct collection writes are used.

Phase J Wave 4 — under the 768px breakpoint the move-log slides off-canvas;
the toggle button reveals it as a drawer. Hidden on desktop.

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="mobile-move-log-toggle"` | `<button id="move-log-toggle">` | Hamburger that opens / closes the move-log drawer on mobile widths. | `src/frontend/autotable-src/index.html:445` |

## Reconnect / disconnect banner

Phase J Wave 2 introduced the banner; Phase J Wave 4 added the
copy-rejoin-link button and the toast region. The banner is anchored at
the top-center of the viewport and uses `display: none` until the
WS-reconnect lifecycle surfaces.

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="connection-banner"` | `<div id="connection-banner">` | Root of the reconnect banner. State-classes (`.connecting`, `.failed`, `.recovered`) control color. | `src/frontend/autotable-src/index.html:464` |
| `data-testid="connection-banner-retry"` | `<button id="connection-banner-retry">` | Manual-retry button. Triggers an immediate reconnect attempt outside the exponential-backoff loop. | `src/frontend/autotable-src/index.html:470` |
| `data-testid="reconnect-copy-link"` | `<button id="connection-banner-copy-link">` | **Phase J Wave 4.** Revealed after the first failed retry. Copies a `?rejoin=<token>` URL to the clipboard so the player can resume the session in a fresh browser tab. Token shape: base64url-encoded `{ v, gameId, playerId, seat, savedAt }` (see `src/reconnect.ts`). Falls back to `document.execCommand` when `navigator.clipboard` is unavailable. | `src/frontend/autotable-src/index.html:480` |
| `data-testid="connection-banner-lobby"` | `<button id="connection-banner-lobby">` | Bail-out button — closes the banner and returns to the lobby form. | `src/frontend/autotable-src/index.html:486` |
| `data-testid="toast-region"` | `<div id="toast-region">` | **Phase J Wave 4.** Bottom-center transient-notification region (rejoin-failed, copy-link-confirmed, etc.). Toasts auto-hide after a short interval. | `src/frontend/autotable-src/index.html:495` |

## In-game HUD *(reserved — not yet shipped)*

Hicks's in-game HUD (discard button, claim chips, draw indicator) is not
yet `data-testid`-instrumented. When Wave 5 / Wave 6 adds those surfaces,
append them under this section with the `hud-` prefix:

- `data-testid="hud-discard-button"` — discard the highlighted tile.
- `data-testid="hud-claim-chip-{type}"` — claim CTA per type
  (`pung` / `kong` / `chow` / `win`).
- `data-testid="hud-draw-button"` — manual-draw button (manual-pickup mode).

**Do NOT** add these to production code until the contract entry exists
here — empty placeholders give a false signal to the integration suite.

## Result modal *(reserved — not yet shipped)*

Hicks's end-of-hand and end-of-game modals will need stable selectors so
the integration suite can assert win-pattern chip rendering against the
`/api/changsha/pattern-ordering` contract pinned by
`PatternOrderingEndpointTests`. Proposed surface:

- `data-testid="result-modal"` — modal root.
- `data-testid="result-modal-pattern-chip-{wireName}"` — one chip per
  entry in `winResult.allPatterns`. The `{wireName}` segment matches the
  camelCase keys returned by `/api/changsha/pattern-ordering` (e.g.,
  `heavenlyHand`, `allPungs`, `sevenPairs`, `fullFlush`). Letting Cypress
  assert `chips[0]` is `heavenlyHand` and `chips[n]` is `standard`
  validates the wire-ordering contract end-to-end.
- `data-testid="result-modal-final-scores"` — score-board container.
- `data-testid="result-modal-final-scores-row-{0..3}"` — per-seat score
  row.

## Game-over summary modal *(reserved — pairs with `GameCompleted` SignalR event)*

Fires once per game (asserted by
`GameCompletionLifecycleTests.GameCompletedEvent_Fires_OnceOnly`) when
`IsGameComplete` flips true. Proposed surface for Hicks's end-of-game
modal:

- `data-testid="game-over-modal"` — root.
- `data-testid="game-over-winner-seat"` — winner display (seat + score).
- `data-testid="game-over-final-scores"` — full-seat breakdown.
- `data-testid="game-over-rematch"` — start-new-game CTA.

## Public matchmaking lobby *(Phase J Wave 5)*

Hicks's `src/matchmaking.ts` poll loop (5s cadence — see
`MATCHMAKING_POLL_MS`) reads `GET /api/matchmaking/lobby` and emits
`update` events with the `PublicGame` array. Phase J Wave 5 — Hicks's
`installPublicGamesPane()` in `lobby.ts` now mounts the list host +
per-card chips so every testid in the table below resolves at runtime
when the "Public Games" tab is active.

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="lobby-my-game-tab"` | `<button class="lobby-tab">` | "My Game" tab — re-shows the existing Quick-Match + seat picker pane. | `src/frontend/autotable-src/index.html:694` |
| `data-testid="lobby-public-games-tab"` | `<button class="lobby-tab">` | "Public Games" tab — toggles the matchmaking browser pane and starts/stops the 5 s matchmaking poll loop. | `src/frontend/autotable-src/index.html:699` |
| `data-testid="lobby-public-section"` | `<section id="lobby-tab-public-games">` | Wrapper around the "Public games" list — only visible while the poll loop is active. | `src/frontend/autotable-src/index.html:917` |
| `data-testid="lobby-public-list"` | `<div class="public-games-list">` | Container holding the per-game chips; one child per `PublicGame` entry from the poll cache. | `src/frontend/autotable-src/index.html:937` |
| `data-testid="lobby-public-game-{0..49}"` | `<div class="public-game-card">` | One chip per public game. Index is the lobby cache position (newest-first, capped at `MAX_PUBLIC_GAMES_RENDERED = 50`). | `src/frontend/autotable-src/src/lobby.ts:1016` |
| `data-testid="lobby-public-game-name-{0..49}"` | `<div class="public-game-card-name">` | The friendly host-supplied `publicName` (≤64 chars per `ChangshaGameRuntime.SetGamePublicAsync`); falls back to `"<host>'s game"` when null. | `src/frontend/autotable-src/src/lobby.ts:1022` |
| `data-testid="lobby-public-game-host-{0..49}"` | `<span class="public-game-card-meta-creator">` | The host's `creatorDisplayName` (resolved through `PlayerProfileService`). | `src/frontend/autotable-src/src/lobby.ts:1030` |
| `data-testid="lobby-public-game-seats-{0..49}"` | `<span class="public-game-card-meta-seats">` | The `seatedCount / maxSeats` text. The wire shape is Bishop's `LobbyGameDto` (`seatedCount` + `maxSeats`); `matchmaking.ts:normalizePublicGame` consumes those keys directly. | `src/frontend/autotable-src/src/lobby.ts:1034` |
| `data-testid="lobby-public-game-join-{0..49}"` | `<button class="public-game-card-join">` | Per-card Join uses the canonical alias and join-only URL. Availability uses `openHumanSeats`, excluding bots and recovery reservations. | `src/frontend/autotable-src/src/lobby.ts` |
| `data-testid="lobby-join-random"` | `<button id="lobby-join-random">` | Invokes non-seating `FindJoinableGame`, then navigates using the same join-only URL. The repaired browser must not use native seat-allocating `JoinRandom`. | `src/frontend/autotable-src/src/lobby.ts` |
| `data-testid="lobby-set-public-toggle"` | `<input type="checkbox" id="lobby-make-public-toggle">` | Host-only checkbox in the lobby that flips `SetGamePublic`. Sender must be the host of the current `?gameId=…`. | `src/frontend/autotable-src/index.html:884` |
| `data-testid="lobby-public-name-input"` | `<input id="lobby-make-public-name">` | Friendly public-name input bound to the `SetGamePublic` `publicName` argument. Server trims + caps at 64 chars; blank is sent as `null`. | `src/frontend/autotable-src/index.html:889` |

> **Wire contract reminder.** Each `PublicGame` entry from
> `/api/matchmaking/lobby` is `{ gameId, publicName, creatorDisplayName,
> seatedCount, maxSeats, variant, createdAt, botCount, openHumanSeats }`.
> `matchmaking.ts:normalizePublicGame` consumes the wire shape as-is
> (post-Wave-5 alignment with Bishop's `LobbyGameDto`). Backend
> wire-shape assertions live in `MatchmakingLobbyEndpointTests`.

## Lobby repair: online players and invitations (September 17, 2026)

This is the approved repair's selector contract, published by Hicks before
implementation. The new browser specs assert these controls after integration;
authoring the specs is **not** evidence that the changing implementation passed.
The existing vanilla-TypeScript chat widget is used on both the bare lobby and
table pages.

| Selector | Purpose |
|---|---|
| `data-testid="lobby-open-chat"` | Bare-lobby entry, accessible name **Online players and invitations**. |
| `data-testid="chat-panel"` / `chat-toggle` | Existing widget; toggle exposes `aria-expanded`. |
| `data-testid="chat-collapse"` | Desktop **Collapse chat** × button; native click/keyboard activation returns focus to the **Open chat** toggle. Messages and the separate desktop collapse preference survive. |
| `data-testid="online-players"` | Server-wide roster section, separate from room-private recipients. |
| `data-testid="online-players-status"` | Explicit connecting/unavailable/ready state; outages are not an empty-online-list claim. |
| `data-testid="online-player"` + `data-player-id` | One other signed identity per row; no own row, bots, rooms or transport IDs. |
| `data-testid="online-player-name"` | Server profile display name rendered as text. |
| `data-testid="online-player-invite"` | **Invite to this table** action scoped to that row. |
| `data-testid="online-invite-status"` | Send result or actionable failure. |
| `data-testid="online-players-retry"` | Retry failed verified bootstrap or presence connection. |
| `data-testid="incoming-invites"` | Recipient-only inbox. |
| `data-testid="table-invite"` + `data-invite-id` | One card per invite UUID, including after reload/reconnect. |
| `data-testid="table-invite-sender"` / `table-invite-table` | Server-supplied sender and destination label. |
| `data-testid="table-invite-join"` | Explicit **Join table** anchor. Expired cards have `aria-disabled="true"` and no `href`. |
| `data-testid="table-invite-expired"` | Expired-card explanation. |
| `data-testid="chat-invite-unread"` / `table-invite-toast` | Incoming indication even when chat is collapsed; never auto-navigates. |
| `data-testid="lobby-public-status"` | Creator/phase/disconnected/unknown eligibility explanation. |
| `data-testid="copy-table-link"` | **Copy table link**, using the same join-only URL as cards and invites. |
| `data-testid="lobby-room-configuration"` | Authoritative room configuration rather than a late joiner's preferences. |
| `data-testid="room-join-error"` | Visible missing/full/started admission error without creation fallback. |

Ordinary chat keeps `chat-channel-select`, `chat-recipient-select`,
`chat-messages`, `chat-input` and `chat-send`. The canonical send is
`POST /api/games/{gameId}/chat`; readers consume `senderPlayerId`,
`senderDisplayName`, `sentUtc` and `recipientPlayerId`. The compatibility aliases
`playerId`/`at` do not replace those canonical fields.

Join navigation contains exactly `gameId`, `variant=changsha`, and `join=1`;
copy-link may make that same URL absolute. It must not inherit a numeric seat,
creation intent, bot/config overrides, rejoin data or credentials.

The focused specs are `room-bot-quota.spec.ts`, `lobby-online-presence.spec.ts`
and `lobby-human-invites.spec.ts`. Their narrow `_lobby-repair.ts` helper performs
real UI actions and read-only state/protocol observation. Separate contexts are
separate players; extra pages in one context are intentionally the same player.
No CSS overlay removal, forced clicks, fabricated server responses or game-API
mutations are acceptance shortcuts. The identity-refresh negative test aborts
one real request, then requires successful real bootstrap through the Retry UI.

Internal-runtime binding is asserted in backend tests with an alias deliberately
different from its GUID. Browser metadata is alias-based; do **not** invent or
assert `turn.current.gameId`. The existing turn cue carries `activeSeat`,
`phase` and `awaitingDiscard`, while private claim/own-turn IDs are observed only
when the server actually emits those contexts.

### C1: connection-specific viewer authority

The approved C1 amendment adds `viewer: { roomId, revision, seat }` to `JOINED`
and every server Changsha `UPDATE`; `seat:null` is explicit. Public `seats`
entries keep their real stable player IDs. A live same-identity duplicate must
therefore have `Client.seat === null` and `world.seat === null` even while
`seatPlayers[0]` still identifies that same player in the original tab.

The actual duplicate-tab test is in `lobby-online-presence.spec.ts` and uses a
second real page sharing the owner's browser context, following a normal
join-only link. It is not satisfied by changing the public roster.

`viewer-authority.contract.spec.ts` is deliberately separate **controlled
receive-path unit coverage, not browser acceptance**. It executes the actual
`BaseClient` and `Client` source with a test socket and unrelated profile/metadata
side effects stubbed. It checks ordering before consumers, old revisions,
wrong rooms, callbacks from old sockets, malformed/missing authority,
terminal null authority, forged collections, and explicit relay/offline
compatibility. No results from pre-C1 bundles qualify this amendment.

## Profile drawer *(Phase J Wave 5)*

Hicks's `src/profile.ts` mounts the per-player profile drawer (display
name + avatar colour edit + preview). The drawer surfaces a single
`data-testid` (the colour-preset buttons) plus a stable family of
DOM `id="..."` selectors — both are documented here because the
Wave 5 Playwright suite uses the `id` selectors directly (no
`data-testid` indirection) for the drawer-open/close + name-validation
flows.

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="profile-avatar-color-preset-{0..N}"` | `<button class="profile-avatar-preset">` | Colour-preset buttons in the avatar picker. Index reflects palette position. | `src/frontend/autotable-src/src/profile.ts:470` |
| `id="profile-drawer"` | `<aside>` / `<div>` | Drawer root — toggled visible via `profile-drawer-open` class. | `src/frontend/autotable-src/src/profile.ts:447, 586, 597` |
| `id="profile-drawer-close"` | `<button>` | Close-drawer CTA. | `src/frontend/autotable-src/src/profile.ts:451` |
| `id="profile-display-name-input"` | `<input type="text">` | Name editor; service rejects empty / >32 chars / leading-trailing whitespace (`PlayerProfileService.UpdateDisplayNameAsync`). | `src/frontend/autotable-src/src/profile.ts:452, 590` |
| `id="profile-display-name-error"` | `<span>` / `<div>` | Inline validation message; populated when `UpdateProfile` raises a `HubException` (translated from `ArgumentException`). | `src/frontend/autotable-src/src/profile.ts:453` |
| `id="profile-avatar-presets"` | `<div>` | Host element for the 16-entry colour-preset grid. | `src/frontend/autotable-src/src/profile.ts:454` |
| `id="profile-avatar-color-custom"` | `<input type="color">` | Free-form `#RRGGBB` picker — service-validated via the `^#[0-9A-Fa-f]{6}$` regex. | `src/frontend/autotable-src/src/profile.ts:455` |
| `id="profile-preview-avatar"` | `<div>` | Live preview chip — colour reflects the in-flight edits before save. | `src/frontend/autotable-src/src/profile.ts:456` |
| `id="profile-preview-name"` | `<span>` | Live preview of the in-flight display-name string. | `src/frontend/autotable-src/src/profile.ts:457` |
| `id="profile-save"` | `<button>` | Commit edits via the `UpdateProfile` hub RPC. Disabled while validation fails. | `src/frontend/autotable-src/src/profile.ts:458` |
| `id="profile-reset"` | `<button>` | Reset edits to the server-side current value. | `src/frontend/autotable-src/src/profile.ts:459` |
| `id="profile-saved-note"` | `<span>` | "Saved ✓" toast shown after a successful `UpdateProfile`. | `src/frontend/autotable-src/src/profile.ts:460` |
| `id="lobby-open-profile-avatar"` | `<div>` | Lobby header avatar chip — clicking opens the drawer. Reflects the current profile colour. | `src/frontend/autotable-src/src/profile.ts:612` |
| `id="lobby-open-profile-label"` | `<span>` | Display-name label next to the lobby header avatar. | `src/frontend/autotable-src/src/profile.ts:613` |
| `data-testid="lobby-open-profile"` | `<button id="lobby-open-profile">` | Open-profile shortcut button rendered in the lobby footer settings row. Mirrors the profile-toggle on-canvas chip but lives in the lobby's settings-shortcut strip so it remains reachable while no game is active. | `src/frontend/autotable-src/index.html:967` |
| `data-testid="profile-drawer"` | `<aside id="profile-drawer">` | Supplemental testid on the drawer root for Wave 5 Playwright selectors that prefer `data-testid` over `id`. The drawer's `id` remains the contract anchor (it doubles as `aria-controls` / `aria-labelledby` target). | `src/frontend/autotable-src/index.html:997` |
| `data-testid="profile-display-name-input"` | `<input id="profile-display-name-input">` | Supplemental testid on the display-name editor. | `src/frontend/autotable-src/index.html:1014` |
| `data-testid="profile-avatar-color-custom"` | `<input id="profile-avatar-color-custom">` | Supplemental testid on the free-form colour picker. | `src/frontend/autotable-src/index.html:1031` |
| `data-testid="profile-save"` | `<button id="profile-save">` | Supplemental testid on the Save button. | `src/frontend/autotable-src/index.html:1040` |
| `data-testid="profile-reset"` | `<button id="profile-reset">` | Supplemental testid on the Reset-to-default button. | `src/frontend/autotable-src/index.html:1043` |

> **Drawer-id vs. testid policy.** Wave 5 Hicks made a deliberate choice
> to use plain DOM `id` selectors (instead of `data-testid`) for the
> profile drawer because the drawer is also referenced from inline
> `aria-controls` / `aria-labelledby` attributes that themselves require
> `id` (not `data-testid`). Vasquez accepts this exception — the `id`
> family above is treated as contract-grade for Wave 5+ tests. A future
> wave may unify on `data-testid` if accessibility tooling lands a
> first-class `aria-controls`/`testid` bridge.

## Player stats panel *(Phase J Wave 5)*

Hicks's `src/stats.ts` renders the player stats grid in two surfaces:
the lobby's per-player stats card (`#lobby-stats-panel` host) and the
post-game modal's "Your stats" delta section. The shared `STATS_TESTIDS`
constant is the source of truth; both surfaces consume the same builder
function (`buildPanel`) so a single fact-set covers both renders.

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="lobby-stats-panel"` | `<div id="lobby-stats-panel">` | Host element for the lobby's "Your stats" card. Populated by `lobby.ts:renderLobbyStatsPanel` whenever the profile cache updates. | `src/frontend/autotable-src/index.html:903` |
| `data-testid="stats-panel"` | `<div class="stats-grid">` | Root grid container. Carries the panel testid so callers can scope-query into the rows. | `src/frontend/autotable-src/src/stats.ts:17, 137` |
| `data-testid="stats-games-played"` | `<span>` (value cell) | `PlayerStats.GamesPlayed` integer. Delta span (when shown) carries the change vs the pre-game snapshot. | `src/frontend/autotable-src/src/stats.ts:19, 64` |
| `data-testid="stats-games-won"` | `<span>` (value cell) | `PlayerStats.GamesWon` integer. | `src/frontend/autotable-src/src/stats.ts:20, 71` |
| `data-testid="stats-win-rate"` | `<span>` (value cell) | `gamesWon / gamesPlayed * 100`, 1-decimal-place pct (e.g. `42.9%`); `—` when no games yet. | `src/frontend/autotable-src/src/stats.ts:21, 78` |
| `data-testid="stats-longest-streak"` | `<span>` (value cell) | `PlayerStats.LongestWinStreak` integer (all-time best consecutive-win run). | `src/frontend/autotable-src/src/stats.ts:22, 88` |
| `data-testid="stats-current-streak"` | `<span>` (value cell) | `PlayerStats.CurrentWinStreak` integer (resets to 0 on any loss). | `src/frontend/autotable-src/src/stats.ts:23, 95` |
| `data-testid="stats-highest-score"` | `<span>` (value cell) | `PlayerStats.HighestSingleGameScore` integer (largest positive `finalScores[me]` ever recorded). | `src/frontend/autotable-src/src/stats.ts:24, 102` |

> **Backend contract.** The six counters above are populated by
> `PlayerProfileService.RecordGameCompletedAsync` (one call per
> game-completed transition; bot-prefixed player ids are skipped).
> Counter semantics, including the win-streak reset on loss + the bot
> filter, are pinned by Vasquez's `PlayerStatsAggregationTests`.

## Onboarding *(Phase J Wave 6)*

Hicks's `src/identity.ts` mounts the first-visit onboarding card: the
DOM block lives inline in `index.html` and stays
`style="display: none"` until the bootstrap detects a missing
`mahjong_pid` cookie (Bishop's persistent-id contract — see
`PersistentPlayerIdTests`). The card collects a display name and avatar
colour, then POSTs to `/api/identity` and lets the backend mint the
cookie. The Onboarding flow shares the avatar-preset palette pattern
with the Wave 5 Profile drawer; the only static testids live on the
card root + form controls, while the colour-preset buttons carry a
templated testid for indexed access.

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="onboarding-card"` | `<section id="onboarding-card">` | Card root. Toggled from `display: none` → visible by `identity.ts:openOnboardingCard` after the first-visit cookie check. | `src/frontend/autotable-src/index.html:724` |
| `data-testid="onboarding-display-name-input"` | `<input id="onboarding-display-name-input" type="text">` | Display-name editor. Backend rejects empty / >32 chars / leading-trailing whitespace (mirrors `PlayerProfileService.UpdateDisplayNameAsync`). | `src/frontend/autotable-src/index.html:741` |
| `id="onboarding-display-name-error"` | `<span>` | Inline validation message; populated when the name fails the same rules the server applies (so the user gets feedback before the POST). | `src/frontend/autotable-src/src/identity.ts:305` |
| `id="onboarding-avatar-presets"` | `<div role="radiogroup">` | Host for the colour-preset buttons (one `<button>` per palette entry). Built by `identity.ts:renderAvatarPresets`. | `src/frontend/autotable-src/src/identity.ts:306` |
| `data-testid="onboarding-avatar-color-preset-{0..N}"` | `<button class="onboarding-avatar-preset">` | Colour-preset buttons in the avatar picker; index reflects palette position (same indexing convention as the Profile drawer's `profile-avatar-color-preset-{idx}`). | `src/frontend/autotable-src/src/identity.ts:346` |
| `data-testid="onboarding-avatar-color-custom"` | `<input id="onboarding-avatar-color-custom" type="color">` | Free-form `#RRGGBB` picker — same validation surface as the Profile drawer's custom-colour input. | `src/frontend/autotable-src/index.html:758` |
| `id="onboarding-preview-avatar"` | `<div>` | Live avatar preview; the chip's background colour reflects the in-flight preset/custom selection before the user commits. | `src/frontend/autotable-src/src/identity.ts:309` |
| `data-testid="onboarding-continue"` | `<button id="onboarding-continue">` | Primary CTA — submits the form, POSTs `/api/identity`, dismisses the card on 200. | `src/frontend/autotable-src/index.html:765` |
| `data-testid="onboarding-skip"` | `<button id="onboarding-skip">` | Secondary CTA — dismisses the card without sending; the user gets the server-assigned default name + colour on first hub connect (path falls through to the cookie-mint codepath in `PlayerIdentityService.ResolveOrMint`). | `src/frontend/autotable-src/index.html:770` |

> **Backend contract.** The form sends `POST /api/identity` with a JSON
> body `{ displayName?, avatarColor? }`. The server returns the full
> `PlayerProfile` envelope `{ playerId, displayName, avatarColor,
> createdAt, lastSeenAt }` and sets the `mahjong_pid` cookie
> (`HttpOnly; SameSite=Lax; Max-Age=31536000`). The cookie value is a
> 32-char lowercase hex (`Guid.NewGuid().ToString("N")`); the same id
> is returned in the body and on the hub's `ProfileLoaded` broadcast.
> Wire contract is pinned by Vasquez's `PersistentPlayerIdTests`.

## Leaderboard *(Phase J Wave 6)*

Hicks's `src/leaderboard.ts` mounts the lobby's leaderboard pane (the
third tab in the lobby tab-strip, after "My Game" and "Public Games").
The pane reads `GET /api/leaderboard?sort&limit&offset&minGames` and
renders a paged table; the sort + min-games controls are static, the
row testids are templated by 0-based index, and the table also exposes
`data-rank` + `data-player-id` attributes on each `<tr>` for tests that
want to scope-query by content rather than index.

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="lobby-leaderboard-tab"` | `<button>` | Tab-strip button that activates the leaderboard pane. Wires up to `lobby.ts:setActiveLobbyTab('leaderboard')`. | `src/frontend/autotable-src/index.html:715` |
| `data-testid="lobby-leaderboard-section"` | `<section id="lobby-tab-leaderboard">` | Pane root. Toggled from `display: none` → visible by the tab handler. Hosts every child below. | `src/frontend/autotable-src/index.html:1024` |
| `data-testid="leaderboard-sort-select"` | `<select id="leaderboard-sort-select">` | Sort axis chooser. Values are the wire strings `gamesWon` / `totalScore` / `winRate` / `longestStreak` / `highestScore` (case-insensitive on the server; the frontend ships canonical camelCase). | `src/frontend/autotable-src/index.html:1033` |
| `data-testid="leaderboard-min-games-input"` | `<input type="number" id="leaderboard-min-games-input">` | Minimum-games-played filter. Default value `5` mirrors `LeaderboardService.DefaultMinGames`. Set to `0` to include everyone (admin / debug view). | `src/frontend/autotable-src/index.html:1039` |
| `id="leaderboard-error"` | `<div>` | Error banner — populated when the fetch fails (network, 5xx, or 429 from Apone's rate limiter). Hidden until needed. | `src/frontend/autotable-src/src/leaderboard.ts:394` |
| `id="leaderboard-loading"` | `<div>` | "Loading leaderboard…" placeholder shown while the fetch is in flight. | `src/frontend/autotable-src/src/leaderboard.ts:396` |
| `id="leaderboard-empty"` | `<div>` | Empty-state placeholder shown when `rows.length === 0` (e.g., the min-games threshold filters everyone out). | `src/frontend/autotable-src/src/leaderboard.ts:395` |
| `data-testid="leaderboard-table"` | `<div>` (table host) | Outer host for the rendered `<table>` — `leaderboard.ts:renderTable` clears + repaints this on every refresh. The `<table>` itself is unwrapped child content. | `src/frontend/autotable-src/index.html:1058` |
| `data-testid="leaderboard-prev-page"` | `<button id="leaderboard-prev-page">` | Pagination — previous page. Disabled when `offset === 0`. | `src/frontend/autotable-src/index.html:1063` |
| `data-testid="leaderboard-paging-summary"` | `<span id="leaderboard-paging-summary">` | Paging summary text (e.g. `Rows 21-30 of 60`). Updated alongside the table render. | `src/frontend/autotable-src/index.html:1067` |
| `data-testid="leaderboard-next-page"` | `<button id="leaderboard-next-page">` | Pagination — next page. Disabled when `offset + rows.length >= total`. | `src/frontend/autotable-src/index.html:1071` |
| `id="leaderboard-row-{0..N}"` | `<tr class="leaderboard-row">` | One row per leaderboard entry on the current page. `idx` is the 0-based offset within the page, NOT the global rank. The `<tr>` also exposes `data-rank` (global 1-based) and `data-player-id` for content-based scoping. Cardinality is `0..limit` (default 50, max 100). | `src/frontend/autotable-src/src/leaderboard.ts:507` |

> **Backend contract.** The pane consumes Bishop's
> `GET /api/leaderboard?sort=<axis>&limit=<n>&offset=<m>&minGames=<k>`
> envelope `{ total: int, rows: [{ rank, playerId, displayName,
> avatarColor, gamesPlayed, gamesWon, winRate, totalScore,
> highestSingleGameScore, longestWinStreak }] }`. Defaults are
> `limit=50` (`MaxLimit=100`), `offset=0`, `minGames=5`; sort defaults
> to `gamesWon` and silently falls back to `gamesWon` on unknown axis.
> All four query knobs + the row shape are pinned by Vasquez's
> `LeaderboardEndpointTests`. Apone's `token-bucket-api` policy (30
> tokens, 5/sec refill) is in front of this endpoint when
> `RateLimiting:Enabled=true` — rejection shape is pinned by
> `RateLimitingTests`.

## Phase J Wave 7 — Replay viewer, settings drawer (tabbed v2), profile page

Selectors added by Hicks's Wave 7 work surface three coordinated UX
features: a refreshed replay viewer (with prev/next-hand navigation
and a speed selector); a tabbed settings drawer (the v2 layout
replacing the Wave-3 monolithic form); and a full-overlay player
profile page that surfaces stats, recent games, display-name +
avatar-colour editing.

### Replay viewer

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="replay-screen"` | `<div id="replay-screen">` | Outer overlay root — visibility is gated by `replay.ts:open()/close()`. Tests assert it `.toBeVisible()` after the post-game modal's `[data-testid="game-complete-replay"]` button is clicked. | `src/frontend/autotable-src/index.html:774` |
| `data-testid="replay-viewer"` | `<div class="replay-shell">` | Inner shell — host for every viewer-scope control below. | `src/frontend/autotable-src/index.html:776` |
| `data-testid="replay-close"` | `<button>` | Close button; flips the `replay-screen` overlay back to hidden. | `src/frontend/autotable-src/index.html:792` |
| `data-testid="replay-prev"` | `<button>` | Previous-hand navigation (NEW in Wave 7). Jumps the timeline to the first move of the prior hand. | `src/frontend/autotable-src/index.html:813` |
| `data-testid="replay-step-back"` | `<button>` | Single-move step backwards. Disabled at move 0. | `src/frontend/autotable-src/index.html:818` |
| `data-testid="replay-play"` | `<button>` | Play / pause toggle. The button surface flips ▶ ↔ ⏸ via the class state. | `src/frontend/autotable-src/index.html:823` |
| `data-testid="replay-step-fwd"` | `<button>` | Single-move step forwards. Disabled at the last move. | `src/frontend/autotable-src/index.html:829` |
| `data-testid="replay-next"` | `<button>` | Next-hand navigation (NEW in Wave 7). Jumps the timeline to the first move of the following hand. | `src/frontend/autotable-src/index.html:834` |
| `data-testid="replay-speed-select"` | `<select>` | Playback speed multiplier (1×/2×/4×). Persisted in localStorage at `autotable.phaseJ.v1.replay.speed`. | `src/frontend/autotable-src/index.html:841` |
| `data-testid="replay-scrubber"` | `<input type="range">` | Timeline scrubber — value maps 0..(N-1) of the move index. Bidirectionally bound to the play head; dragging updates the table at the next animation frame. | `src/frontend/autotable-src/index.html:847` |
| `data-testid="replay-event-counter"` | `<span>` | "Move N / M" status text. Stays in sync with the scrubber regardless of which control moved the play head. | `src/frontend/autotable-src/index.html:854` |

> **Reference E2E spec.** `tests/e2e/replay.spec.ts` (Hicks Wave 6 +
> extended Wave 7) covers open → step-fwd → play → close. The
> prev/next-hand affordances are new in Wave 7; the spec gains a
> follow-up assertion when the move counter and scrubber re-base after
> a `[data-testid="replay-prev"]` / `[data-testid="replay-next"]` click.

### Settings drawer (v2 tabbed)

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="lobby-open-settings"` | `<button id="lobby-open-settings">` | Lobby shortcut that opens the drawer. Wave-3 entry point; unchanged by the v2 refactor. | `src/frontend/autotable-src/index.html:1293` |
| `data-testid="settings-button"` | `<button id="settings-button">` | Top-bar ⚙ shortcut (visible inside the game shell). Mirrors `lobby-open-settings` behaviour. | `src/frontend/autotable-src/index.html:456` |
| `data-testid="settings-drawer"` | `<aside id="app-settings-drawer-v2">` | Drawer root — `.settings-open` class toggles the off-canvas visual. The `tablist` + `panels` hosts inside are populated by `settings.ts`. | `src/frontend/autotable-src/index.html:575` |
| `data-testid="settings-close"` | `<button>` | Drawer close button (the × glyph in the header). Removes the `.settings-open` class. | `src/frontend/autotable-src/index.html:580` |
| `data-testid="settings-sound"` | `<input type="checkbox">` | Sound toggle (lives inside the General/Audio tab panel). Mirrored to `localStorage.mahjong:soundEnabled` by `lobby.ts:installSoundEnabledMirror`. | `src/frontend/autotable-src/index.html:550` |
| `data-testid="settings-reset"` | `<button id="settings-reset">` | Reset-to-defaults action. Reverts every field in every panel without persisting. | `src/frontend/autotable-src/index.html:596` |
| `data-testid="settings-save"` | `<button id="settings-save">` | Save action — writes the canonical JSON payload at `autotable.phaseJ.v1.settings.*` and flashes the saved-note element. | `src/frontend/autotable-src/index.html:601` |

> **Reference E2E specs.** `tests/e2e/sound-toggle.spec.ts` (Wave 6,
> single-toggle round-trip) + `tests/e2e/settings-drawer.spec.ts`
> (Wave 7, full open / save / reload / reset / close lifecycle).

### Profile page (full overlay)

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `data-testid="lobby-open-profile"` | `<button id="lobby-open-profile">` | Avatar chip in the lobby header. Click opens the full-overlay profile page (NOT the legacy `data-testid="profile-drawer"` slide-in, which is the Wave-5 surface kept around until Wave 8 migration). | `src/frontend/autotable-src/index.html:1284` |
| `data-testid="profile-page"` | `<div id="profile-page">` | Overlay root; `aria-hidden` toggles `true` ↔ `false` on close / open. Tests assert visibility via the aria attribute rather than a CSS class to stay decoupled from the visual implementation. | `src/frontend/autotable-src/index.html:613` |
| `data-testid="profile-page-close"` | `<button id="profile-page-close">` | Close button — flips `aria-hidden` back to `true`. | `src/frontend/autotable-src/index.html:632` |
| `data-testid="profile-stats-grid"` | `<div id="profile-stats-grid">` | Career-stats grid. Populated by `profile-page.ts:renderStatsGrid()` from the `/api/me/stats` payload. Cardinality 0..N children. | `src/frontend/autotable-src/index.html:642` |
| `data-testid="profile-page-display-name-input"` | `<input type="text" maxlength="32">` | Editable display name. Commits on blur; the canonical write target is `/api/me/profile` (PlayerProfileService). Vasquez's `NegativePathTests` pins the > 32 char rejection at the API boundary. | `src/frontend/autotable-src/index.html:653` |
| `data-testid="profile-page-color-custom"` | `<input type="color">` | Custom avatar-colour picker. Wave 7 also surfaces an 8-preset radio group (the `<div id="profile-page-color-presets">` sibling, no testid — DOM children are dynamic). The custom input's value flows into `PlayerProfile.AvatarColor` on save. | `src/frontend/autotable-src/index.html:670` |
| `data-testid="profile-recent-games"` | `<div id="profile-recent-games">` | Recent-games list — populated from `/api/me/games?limit=10` (Bishop Wave 7). Cardinality 0..10 children. Empty state surfaces via `#profile-recent-games-error`. | `src/frontend/autotable-src/index.html:690` |

> **Reference E2E spec.** `tests/e2e/profile-page.spec.ts` (Wave 7,
> Vasquez) covers open → edit name → reload → state persists → close.
> The `[data-testid="profile-stats-grid"]` and
> `[data-testid="profile-recent-games"]` contents are not asserted
> directly because a fresh test browser has no completed-game
> history; only the containers' presence is checked.

> **Backend contract — replay endpoint.** The viewer's `Watch Replay`
> click loads from Bishop's `GET /api/games/{gameId}/replay`. Envelope:
> `{ gameId, createdAt, events: [{ turn, phase, actor, action,
> tilesJson, timestampUtc }] }`. Wire shape pinned by Vasquez's
> `GameReplayEndpointTests`; phase bucket vocabulary (Setup / Deal /
> Discard / Claim / Hu / Other) is the canonical replay-event
> taxonomy — adding a new bucket REQUIRES coordinating with the
> frontend's icon mapping at `replay.ts:phaseGlyph()`.

## Stability contract

A test relying on a selector from this document gets the following
guarantees from Hicks's surface:

1. **Identity** — the testid is the **only** test-relevant attribute.
   Tests SHOULD NOT key off CSS class names, `id` attributes, ARIA
   labels, or DOM-tree position. These are subject to refactor without
   notice; the testid is not.
2. **Cardinality** — each non-templated testid appears **exactly once**
   in the live DOM. Templated testids (e.g.,
   `lobby-player-chip-{0..3}`) appear 0..N times depending on game
   state; document the cardinality bound in the table above.
3. **Lifetime** — once an element is reachable by its testid, it remains
   reachable for the entire surface it belongs to (e.g., a lobby chip
   does not get re-rendered with a different testid mid-frame). Hicks
   uses `setAttribute` once at element creation, not on every update.
4. **Naming** — kebab-case, surface-prefixed, ASCII-only. No
   punctuation, no Unicode. Indexed segments use zero-based integers.

When in doubt, file a memo at
`.squad/decisions/inbox/hicks-vasquez-<topic>.md` before changing a
testid in this document.

---

## Phase J Wave 8 — Auth, rule presets, spectator follow, motion + theme

### Auth surfaces (`signin-modal`, `magic-link-landing`, `auth-cluster`, `profile-linked-accounts`)

| testid                              | element     | cardinality | notes                                                      |
|-------------------------------------|-------------|-------------|------------------------------------------------------------|
| `signin-button`                     | `<button>`  | 0..1        | top-right header. Hidden when user is signed in.           |
| `logout-button`                     | `<button>`  | 0..1        | top-right header. Hidden when user is signed out.          |
| `auth-status-chip`                  | `<span>`    | 0..1        | shows current email + primary provider.                    |
| `signin-modal`                      | `<div>`     | 0..1        | backdrop + card. Visible only when sign-in flow is active. |
| `signin-modal-close`                | `<button>`  | 0..1        | dismisses the modal.                                       |
| `signin-provider-google`            | `<button>`  | 0..1        | starts Google OAuth flow.                                  |
| `signin-provider-github`            | `<button>`  | 0..1        | starts GitHub OAuth flow.                                  |
| `signin-email-input`                | `<input>`   | 0..1        | magic-link email field.                                    |
| `signin-email-submit`               | `<button>`  | 0..1        | requests a magic-link email.                               |
| `signin-email-error`                | `<span>`    | 0..1        | shows validation / server-side errors.                     |
| `signin-email-success`              | `<div>`     | 0..1        | "Check your email" panel.                                  |
| `signin-placeholder`                | `<div>`     | 0..1        | "Auth coming soon" placeholder when /providers 404s.       |
| `magic-link-landing`                | `<div>`     | 0..1        | full-screen overlay rendered when `?auth=<token>` lands.   |
| `magic-link-landing-success`        | `<div>`     | 0..1        | success message panel.                                     |
| `magic-link-landing-failure`        | `<div>`     | 0..1        | failure message panel.                                     |
| `magic-link-landing-continue`       | `<button>`  | 0..1        | dismisses the landing overlay.                             |
| `profile-linked-accounts`           | `<section>` | 0..1        | linked-accounts section inside the profile page.           |
| `profile-linked-account-{provider}` | `<div>`     | 0..3        | one row per linked provider (google / github / email).     |
| `profile-link-{provider}`           | `<button>`  | 0..3        | "Link" button for an unlinked provider.                    |
| `profile-unlink-{provider}`         | `<button>`  | 0..3        | "Unlink" button for a linked provider.                     |

### Rule presets

| testid                                       | element    | cardinality | notes                                                  |
|----------------------------------------------|------------|-------------|--------------------------------------------------------|
| `lobby-rule-preset-select`                   | `<select>` | 0..1        | dropdown in the lobby "Rule preset" fieldset.          |
| `lobby-create-preset-link`                   | `<a>`      | 0..1        | opens the settings-drawer rule-presets tab.            |
| `settings-tab-rule-presets`                  | `<button>` | 0..1        | tab strip entry in the Wave-7 settings drawer.         |
| `settings-panel-rule-presets`                | `<div>`    | 0..1        | panel host. `renderEditorPanel()` populates the body.  |
| `rule-preset-picker`                         | `<select>` | 0..1        | preset chooser inside the editor.                      |
| `rule-preset-new-button`                     | `<button>` | 0..1        | clones the current preset for editing.                 |
| `rule-preset-edit-name`                      | `<input>`  | 0..1        | display name (custom presets only).                    |
| `rule-preset-edit-handLimit`                 | `<input>`  | 0..1        | max hand limit.                                        |
| `rule-preset-edit-maxScorePerHand`           | `<input>`  | 0..1        | per-hand cap.                                          |
| `rule-preset-edit-allowWashout`              | `<input>`  | 0..1        | washout enabled flag (checkbox).                       |
| `rule-preset-edit-allowKongRobbing`          | `<input>`  | 0..1        | kong-robbing flag (checkbox).                          |
| `rule-preset-edit-allowConcealedKongPromotion` | `<input>` | 0..1        | concealed-kong promotion flag (checkbox).              |
| `rule-preset-save`                           | `<button>` | 0..1        | persists the draft via Bishop's POST/PUT.              |
| `rule-preset-delete`                         | `<button>` | 0..1        | deletes the current custom preset.                     |
| `rule-preset-status`                         | `<div>`    | 0..1        | inline save / error status text.                       |

### Spectator follow-seat (`?seat=-1` mode only)

| testid                          | element    | cardinality | notes                                                |
|---------------------------------|------------|-------------|------------------------------------------------------|
| `spectator-follow-panel`        | `<div>`    | 0..1        | floating bottom-right panel. Visible only when spectating. |
| `spectator-follow-seat-{0..3}`  | `<button>` | 0..4        | "Follow Seat N" button per seat.                     |
| `spectator-follow-topdown`      | `<button>` | 0..1        | reverts to the top-down camera.                      |
| `spectator-show-all-toggle`     | `<input>`  | 0..1        | local "show all hands" hint toggle.                  |

Keyboard shortcuts: 1/2/3/4 follow seats 0..3; 0 or Esc returns to
top-down. Shortcuts are inert outside spectator mode and ignored when
typing in an input or contenteditable element.

### Display preferences (settings drawer Display tab)

| testid                     | element    | cardinality | notes                                                |
|----------------------------|------------|-------------|------------------------------------------------------|
| `settings-motion-select`   | `<select>` | 0..1        | Auto / Reduced / Full.                               |
| `settings-theme-select`    | `<select>` | 0..1        | Auto / Light / Dark.                                 |

The page chrome reflects these picks via `<body>` classes
`reduced-motion`, `full-motion`, `theme-light`, `theme-dark`. Tests
that assert chrome appearance should key off these classes (NOT the
testids, which only locate the controls).

### Master bot tier

| testid                                | element    | cardinality | notes                                            |
|---------------------------------------|------------|-------------|--------------------------------------------------|
| `lobby-bot-difficulty-master`         | `<input>`  | 0..1        | Master radio in the lobby Bot difficulty fieldset. |

The non-lobby surfaces (`#bot-difficulty`, `#settings-bot-strength`)
add a Master option without a separate testid because each select
exposes its `value` attribute directly.

---

### Phase J Wave 8 Playwright coverage — Vasquez

The following e2e specs key off the Wave 8 testids above and live in
`src/frontend/autotable-src/tests/e2e/`. Each spec is
*reflection-defensive*: a missing `data-testid` is logged as a
`soft-pass` annotation rather than a failure, so contract drift before
Hicks's surface lands does not break the gate.

| Spec                          | Surface under test                                        |
|-------------------------------|-----------------------------------------------------------|
| `signin-modal.spec.ts`        | header sign-in chip + modal (providers, dev-login, close) |
| `magic-link.spec.ts`          | `?auth=<token>` landing (success / failure / continue)    |
| `rule-presets.spec.ts`        | lobby dropdown + settings drawer rule-preset editor       |
| `spectator-follow.spec.ts`    | `?seat=-1` floating follow-panel + keyboard shortcuts     |
| `reduced-motion.spec.ts`      | `prefers-reduced-motion: reduce` → body class + CSS clamp |
| `dark-mode.spec.ts`           | `prefers-color-scheme: dark` → body theme-dark + luma     |

When you remove or rename a Wave 8 testid, search
`src/frontend/autotable-src/tests/e2e/` for the literal string before
landing the change — the soft-pass annotation will otherwise hide the
regression from CI.

## Phase J Wave 9 testids — chat / i18n / replay-audit (Vasquez)

Wave 9 ships five new surfaces with selectors that follow the kebab-case,
surface-prefixed contract.  The testids below are *already present* in
`index.html` and Hicks's modules at HEAD; any future rename must update
the matching e2e spec referenced in the next subsection.

| Surface         | testid                       | Where wired                                 |
|-----------------|------------------------------|---------------------------------------------|
| Chat            | `chat-panel`                 | `index.html` (#chat-panel container)        |
| Chat            | `chat-toggle`                | `index.html` (collapse / expand button)     |
| Chat            | `chat-channel-select`        | `index.html` (table / spectators / private) |
| Chat            | `chat-recipient-select`      | `index.html` (private DM recipient picker)  |
| Chat            | `chat-unavailable`           | `index.html` (offline / 404 banner)         |
| Chat            | `chat-messages`              | `index.html` (rendered message log)         |
| Chat            | `chat-input`                 | `index.html` (composer textarea — `maxlength=280`) |
| Chat            | `chat-char-count`            | `index.html` (live `0/280` counter)         |
| Chat            | `chat-send`                  | `index.html` (Send button)                  |
| Chat            | `chat-message-{i}`           | `chat.ts` (per-row id)                      |
| Chat            | `chat-message-{i}-author`    | `chat.ts` (sender pill)                     |
| Chat            | `chat-message-{i}-body`      | `chat.ts` (body text node)                  |
| i18n            | `settings-language-select`   | `settings-drawer.ts` (lang picker)          |
| CSP             | _N/A (header-level)_         | `SecurityHeadersMiddleware.cs` (`DefaultCsp`)|
| Replay (audit)  | `replay-audit-tab`           | `index.html` (admin-only tab, hidden initially) |
| Replay (audit)  | `replay-audit-row-{i}`       | `audit.ts` (per-row id)                     |
| Replay (audit)  | `replay-audit-row-{i}-source`| `audit.ts` (human / bot / system cell)      |
| Replay (audit)  | `replay-audit-row-{i}-duration` | `audit.ts` (durationMs cell)             |
| Replay (audit)  | `replay-audit-row-{i}-score` | `audit.ts` (bot decision score cell)        |
| Reconnect       | `reconnect-copy-link`        | `index.html` (Wave 4, reused for Wave 9 rotation hygiene) |

The CSP row is intentionally header-level — there is no DOM selector to
pin; `csp-headers.spec.ts` reads the `Content-Security-Policy` HTTP
header directly off the root document.

### Phase J Wave 9 Playwright coverage — Vasquez

The Wave 9 e2e specs live alongside the Wave 8 ones in
`src/frontend/autotable-src/tests/e2e/`.  All follow the
reflection-defensive pattern established by `magic-link.spec.ts` —
missing surface → `test.info().annotations.push({ type: 'soft-pass', ... })`
and an early return rather than a hard failure.

| Spec                       | Surface under test                                       |
|----------------------------|----------------------------------------------------------|
| `chat-panel.spec.ts`       | chat-panel mount, composer 280-char cap, channel picker, graceful 404 |
| `i18n-switch.spec.ts`      | settings-language-select → body[lang] flips between en/zh-Hans/zh-Hant |
| `csp-headers.spec.ts`      | root-document `Content-Security-Policy` shape, `unsafe-eval` ban, nonce/strict-dynamic preference |
| `admin-audit-tab.spec.ts`  | `replay-audit-tab` admin gate (hidden non-admin, visible admin, row render, 403 grace) |
| `token-rotation.spec.ts`   | `mahjong:session:v1` localStorage blob present, no token leak in DOM, reload preserves playerId |

Soft-pass annotation contract — keep these stable so CI summaries
remain searchable:

  - `chat-panel surface not yet wired`
  - `settings-language-select not yet wired`
  - `CSP header not yet emitted on root document`
  - `replay-audit-tab not yet wired`
  - `no session/reconnect blob persisted yet`

When you remove or rename a Wave 9 testid, run
`grep -rn '<testid>' src/frontend/autotable-src/tests/e2e/` before
merging — the soft-pass annotations will silently hide regressions.

## Phase J Wave 10 testids — tournaments / avatar / audit-why / spectator chat (Vasquez)

Wave 10 ships four new surfaces. The testids below are documented for
Hicks's bundle to wire up; the e2e specs listed in the next subsection
soft-pass while the surfaces are forward-staged.

| Surface             | testid                                    | Where wired                                       |
|---------------------|-------------------------------------------|---------------------------------------------------|
| Tournaments         | `lobby-tournament-card`                   | `index.html` (lobby tournament panel container)   |
| Tournaments         | `lobby-tournament-list`                   | `index.html` (rendered list of tournaments)       |
| Tournaments         | `lobby-tournament-name`                   | `index.html` (create-form name input)             |
| Tournaments         | `lobby-tournament-create`                 | `index.html` (create-form submit button)          |
| Tournaments         | `tournament-register-btn`                 | tournament card row (per tournament)              |
| Tournaments         | `tournament-registration-status`          | tournament card row (Registered / Open badge)     |
| Tournaments         | `tournament-start-btn`                    | tournament card row (creator-only)                |
| Tournaments         | `tournament-matches-table`                | tournament detail view (after start)              |
| Tournaments         | `tournament-leaderboard`                  | tournament detail view (standings table)          |
| Avatar migration    | `avatar-migration-modal`                  | `index.html` (legacy `#808080` colour prompt)     |
| Avatar migration    | `avatar-migration-pick-{name}`            | colour swatches inside the modal (e.g. `-emerald`)|
| Avatar migration    | `avatar-migration-dismiss`                | modal Dismiss / Close button                      |
| Audit why-expand    | `replay-audit-row-{i}-why`                | `audit.ts` (per-row toggle)                       |
| Audit why-expand    | `replay-audit-row-{i}-reasoning`          | `audit.ts` (expanded reasoning panel)             |
| Audit why-expand    | `replay-audit-row-{i}-reasoning-line-{j}` | `audit.ts` (per-line list-item)                   |
| Audit why-expand    | `[data-strategy]` (attribute)             | `audit.ts` (strategy badge attribute on header)   |
| Spectator chat      | _(re-uses Wave 9 `chat-*` testids)_       | `chat.ts` — spectator channel default + filter    |

### Phase J Wave 10 Playwright coverage — Vasquez

| Spec                            | Surface under test                                        |
|---------------------------------|-----------------------------------------------------------|
| `tournament-flow.spec.ts`       | lobby card mount, create → register → start → leaderboard |
| `avatar-migration.spec.ts`      | `#808080` migration modal, picker persist, dismiss-safe   |
| `csp-no-inline-styles.spec.ts`  | `style-src` lacks `'unsafe-inline'` + DOM has no inline   |
| `audit-why-expand.spec.ts`      | `replay-audit-row-{i}-why` toggle, reasoning lines render |
| `spectator-chat.spec.ts`        | spectator channel default, composer enabled, table-leak   |

Canonical soft-pass annotation strings (keep stable for CI summary scraping):

  - `lobby-tournament-card not yet wired`
  - `lobby-tournament-create form not yet wired`
  - `tournament-register-btn not yet wired`
  - `tournament-start-btn not yet wired`
  - `tournament-matches-table not yet wired`
  - `tournament-leaderboard not yet wired`
  - `avatar-migration-modal not yet wired`
  - `avatar-migration-pick-* not yet wired`
  - `avatar-migration-dismiss not yet wired`
  - `style-src still carries unsafe-inline (CspStrictStyles=false)`
  - `inline style attributes still present`
  - `replay-audit-row-{i}-why toggle not yet wired`
  - `replay-audit-row-{i}-reasoning panel not yet wired`
  - `reasoning-line {i} testids not yet wired`
  - `reasoning data-strategy attribute not yet wired`
  - `chat-panel not yet wired for spectator viewport`
  - `chat-channel-select did not default to spectators`
  - `spectator backfill not yet rendered`
  - `chat-input disabled for spectators (Wave 10 brief expects enabled)`
  - `table channel leaking into spectators view (Wave 10 contract not yet enforced)`

When you remove or rename a Wave 10 testid, run
`grep -rn '<testid>' src/frontend/autotable-src/tests/e2e/` before
merging — the soft-pass annotations will silently hide regressions.

## Phase K Wave 1 testids — tournament SVG bracket / match history / rated leaderboard / onboarding tour (Hicks)

Coverage for the Phase K Wave 1 frontend bring-up.

### Tournaments — SVG bracket + sortable standings + watch-finals pin
- `tournament-bracket-svg` — the SVG host inside `#tournament-bracket`
  (replaces the Wave-10 `<pre>` dump).  Renders single-elim brackets;
  for round-robin / Swiss formats the host is hidden and the
  standings table takes over.
- `tournament-bracket-match-{R}-{N}` — clickable `<g>` cell for round
  R (1-based), match index N (1-based within the round).  Click /
  Enter / Space toggle the inline detail row below the bracket.
- `tournament-bracket-match-{R}-{N}-expand` — the chevron / `+` glyph
  inside the match cell that mirrors the cell's expanded state.
- `tournament-standings-table` — the standings table host inside
  `#tournament-standings`.  Headers cycle through asc/desc/off on
  click; the `<th>` for the active column gets `.sorted-asc` /
  `.sorted-desc`.
- `tournament-standings-row-{N}` — one row per player, N is the
  current sort position (1-based).
- `tournament-watch-finals-{tournamentId}` — the "Watch finals" pin
  rendered on a final-round complete match.  Calls
  `openReplayForGame(gameId)` (lazy-imports `./replay-launcher`).

SignalR contract: `tournaments.ts` subscribes to the
`TournamentMatchCompleted` hub event (alias
`TournamentMatchCompletedV1`) and refreshes the active tournament
detail in-place when it fires.

### Match history export modal (profile → Recent games)
- `profile-history-link` — auto-injected "📥 Match history" button
  inside `#profile-recent-games`.
- `history-modal` — the modal dialog scaffold; mounted lazily by
  `history.ts:installHistoryModal()`.
- `history-date-range` — `<select>` with options 7 / 30 / 90 / 365 /
  custom.  When custom, two `<input type="date">` siblings become
  visible (`history-date-from`, `history-date-to`).
- `history-format-toggle` — radio group with values `json` and `csv`.
- `history-download` — primary action button; triggers blob download
  via `URL.createObjectURL`.
- `history-recent-table` — preview table of the most recent 20
  matches.  Columns sort on header click.
- `history-recent-row-{N}` — one row per match preview.

Wire contract: `GET /api/games?playerId=&format=json|csv&from=<ISO>&to=<ISO>`.
The modal feature-detects 404 → shows "Match-history export is not yet
available" status banner and disables the Download button.

### Leaderboard — ELO rating toggle + season picker + delta arrows
- `leaderboard-rating-toggle` — checkbox that swaps the leaderboard
  data source from `/api/leaderboard` → `/api/ratings/leaderboard`.
  Mode persists in LS `mahjong.leaderboard.rating.v1`.
- `leaderboard-season-select` — `<select>` listing
  current / last / all-time.  Persists in LS
  `mahjong.leaderboard.rating.season.v1`.
- `leaderboard-rating-status` — `aria-live="polite"` badge that
  surfaces "Ratings unavailable — showing stats." when the ratings
  endpoint 404s (falls back to the stats endpoint, persists
  `mode='stats'`).
- `leaderboard-rating-delta-{N}` — per-row delta cell rendered only
  in rating mode.  Carries `▲`/`▼`/`—` plus `.lb-delta-up` /
  `.lb-delta-down` / `.lb-delta-zero` classes.

### Onboarding tour overlay
- `tour-overlay` — fixed-inset overlay with SVG dim mask + spotlight
  cutout.  Gated by LS flag `mahjong.tour.completed.v1`.
- `tour-spotlight` — the SVG `<rect>` cutout; geometry is computed
  per-step from `getBoundingClientRect()` of the step's target.
- `tour-step-{1..8}` — the tour card carries the active step's
  testid; rotates as the user advances.
- `tour-prev` / `tour-next` — navigation buttons.  Prev is disabled
  on step 1; Next becomes "Done ✓" on step 8.
- `tour-skip` — closes the tour and marks the LS flag complete.

Steps:
  1. lobby tab strip — `lobby-my-game-tab`
  2. profile chip — `lobby-open-profile`
  3. rule preset selector — `lobby-rule-preset-select` (fallback
     `lobby-variant-fieldset`)
  4. bot strength tiers — `lobby-bot-difficulty-fieldset`
  5. chat panel — `chat-panel`
  6. settings drawer — `settings-button`
  7. tournaments tab — `lobby-tournaments-tab` (secondary highlight
     on `leaderboard-rating-toggle`); auto-activates the Tournaments
     tab so the highlight has a target.
  8. centred "you're ready to play" card — no spotlight target.

Keyboard support: ←/→ navigate, Enter advances, Esc closes without
marking complete (resumable on next visit).

### Phase K Wave 1 Playwright coverage — Vasquez

The Wave-10 soft-pass annotation pattern still applies — any selector
above that has not yet been wired by a downstream module is listed
here so the e2e suite can stay green during the rollout:

- `tournament-bracket-svg renders only when bracket is single-elim`
- `tournament-bracket-match-* click expands inline detail`
- `tournament-watch-finals-* hidden until the final-round match is complete`
- `history-modal endpoint feature-detect (no /api/games yet on staging)`
- `leaderboard-rating-toggle falls back to stats on 404`
- `tour-overlay only fires when LS flag is unset`

When you remove or rename a Phase K Wave 1 testid, run
`grep -rn '<testid>' src/frontend/autotable-src/tests/e2e/` before
merging — the soft-pass annotations will silently hide regressions.

## Phase K Wave 2 testids — lobby bundle split + voice chat + drag-drop seeding + PWA + server-authoritative tour (Hicks)

Coverage for the Phase K Wave 2 frontend bring-up.  Lobby initial
bundle now < 500 kB; renderer + Client + three / signalr-bound voice
code is deferred behind dynamic imports.

### Lobby bundle budget

The Wave-1 eager bundle (`autotable-src.<hash>.js`) was 1.318 MB.
Wave 2 split out the renderer chain into `game-bootstrap.<hash>.js`
(loaded only when `window.location.search` is non-empty — i.e., the
user has crossed the lobby boundary).  Post-split sizes:

| Asset                                 | Size       | Trigger |
|---|---|---|
| `autotable-src.<hash>.js` (eager)     | **208.4 kB** | Always (lobby + matchmaking + identity + i18n + Sentry shim) |
| `game-bootstrap.<hash>.js` (lazy)     | 1.11 MB    | First non-empty `?…` on URL (Quick Match / Apply / `?gameId=`) |
| `esm.<hash>.js` (Sentry vendor)       | 395 kB     | Only when `<meta name="sentry-dsn">` is non-empty |
| `tournaments.<hash>.js` (lazy)        | 23.8 kB    | Tournaments tab hover/focus/click |
| `history.<hash>.js` (lazy)            | 12.3 kB    | Profile-page open |
| `tour.<hash>.js` (lazy)               | 9.5 kB     | First visit (skipped when server says done) |
| `chat.<hash>.js` (lazy)               | 12.2 kB    | `?gameId=` on URL after game bootstrap |
| `audit.<hash>.js` (lazy)              | 7.4 kB     | Admin probe + replay-tab activation |
| `voice.<hash>.js` (lazy)              | 5.6 kB     | `?voice=1` on a game URL |

The eager CSS budget (`autotable-src.<hash>.css` × 3) is unchanged at
~216 kB.  A cold lobby visitor downloads 208 kB JS + 216 kB CSS +
icons = under 500 kB total transfer before the lobby paints.

`utils.ts` was split into `utils.ts` (three.js-bound) +
`dom-utils.ts` (pure DOM).  Lobby-chain modules now import
`setElHidden` / `showEl` / `hideEl` from `dom-utils` so three is no
longer pulled into the eager graph.

### Voice chat panel
- `voice-panel` — root `<aside>` mounted by `voice.ts` when a player
  joins a table with voice opt-in (`?voice=1` on the URL until
  Bishop's hub broadcasts a per-game `voiceEnabled` flag).
- `voice-mic-toggle` — primary mic button.  Toggles `aria-pressed`,
  swaps "🎙️ Mute" ↔ "🔴 Live".  Disabled + `voice-mic-denied` class
  when `getUserMedia` is rejected.
- `voice-peer-{connectionId}` — per-peer status pill carrying
  "Connecting" / "Connected" / "Failed".  Class
  `voice-peer-status-{state}` mirrors the text.
- `voice-volume-{connectionId}` — `<input type="range">` (0–1, step
  0.05) bound to the peer's `<audio>.volume`.

Wire contract:
  - ICE servers fetched from `GET /api/turn` →
    `{ iceServers: [{ urls, username?, credential? }, …] }`.
    On 404 / error we fall back to a public STUN server so the mesh
    still works on benign NATs.
  - `VoiceHub` (SignalR, `/hubs/voice`) signals via
    `Offer` / `Answer` / `IceCandidate` / `PeerJoined` / `PeerLeft`
    events; client → server methods are `SendOffer`, `SendAnswer`,
    `SendIceCandidate`.  Up to 4 peers per table (full mesh).

### Tournament drag-drop seeding (admin)
- `tournament-seeding-panel` — admin-only seeding surface above the
  bracket SVG.  Renders when (a) the admin probe
  (`GET /api/auth/me` → `role:'admin'` / `roles:['admin']`) succeeds,
  (b) the tournament status is `open` / `registration-open`, and (c)
  the format is single-elim.  Hidden otherwise.
- `tournament-seed-row-{N}` — one draggable `<li>` per seed.  HTML5
  drag-drop reorders the list; `aria-grabbed` flips on `dragstart`/
  `dragend`.  Internal `data-seed-index` + `data-player-id`
  attributes carry the canonical ordering.
- `tournament-seeding-save` — POST `/api/tournaments/{id}/seed` with
  `{ seeds: [playerId, …] }`.  Disabled during the request; on
  success re-opens the tournament detail so the bracket reflects the
  server's canonical layout.
- `tournament-seeding-status` — error/status pill that surfaces
  beneath the Save button when the POST fails.  Auto-removes after
  4 s.

### Replay finals deep-link
- `openReplayForGame(gameId, { finals: true })` — new options arg on
  the launcher.  Auto-scrolls to the last hand + final move + stamps
  `?finals=true` on the URL so a shared link reopens at the finals.
- `?finals=true` URL flag is also honoured when a replay is opened
  without the option (covers cold-link visits).
- All tournament replay entry points (the SVG cell finals pin, the
  detail strip Watch-replay button, and the round-robin/Swiss row
  ▶ buttons) pass `{ finals: true }`.

### Server-authoritative onboarding status
- `tour.ts` probes `GET /api/players/me/onboarding-status` on
  `installOnboardingTour()`.  When the server reports `completed:
  true`, the LS flag is mirrored locally so future visits skip the
  tour even when offline.
- On tour completion `endTour(true)` posts the same endpoint with
  `{ completed: true, completedAtUtc: "<iso>" }`.  POST failure is
  silently ignored — LS is the authoritative offline fallback.
- 404 / network error → falls through to the Wave-1 LS-only path so
  the rollout is safe to merge ahead of Bishop's backend.

### PWA — manifest + service worker + offline lobby
- `manifest.webmanifest` (shipped unhashed at the dist root):
  standalone display, `theme_color #1e2a36`, three icon sizes drawn
  from `img/icon-{16,32,96}.auto.png`.  Linked from `index.html`
  alongside an Apple-touch-icon shim.
- `sw.js` (shipped unhashed at the dist root) caches:
  - cache-first for parcel content-hash assets (`.<8hex>.{js,css,…}`)
    and anything under `/img/`.  Old hashed files survive until the
    next install cycle's `activate` purge.
  - network-first with cache fallback for `/api/games/public` so a
    returning user with a dead connection still sees the last-known
    lobby (and the offline banner appears).
  - network-only for the rest of `/api/*` + `/hubs/*` so auth +
    matchmaking + voice never serve stale data.
  - network-first with a cached index.html fallback so the SPA shell
    boots offline.
- `pwa-offline-banner` — `<div role="status">` injected into
  `<body>` by `pwa.ts`; toggles `hidden` on `online`/`offline`
  transitions and re-broadcasts as `mahjong:offline` / `mahjong:online`
  CustomEvents so other modules (history, matchmaking) can attach.
- `pwa-install-prompt` — `<button>` mounted when Chrome/Edge fires
  `beforeinstallprompt`.  Clicking it invokes the deferred native
  prompt; the button removes itself afterward.

### Phase K Wave 2 Playwright coverage — Vasquez

Soft-pass annotations for selectors whose downstream surface hasn't
yet been wired:

- `voice-mic-toggle hidden until ?voice=1 is on the URL`
- `voice-peer-* requires VoiceHub (Bishop's /hubs/voice)`
- `tournament-seeding-panel hidden when admin probe returns false`
- `tournament-seed-row-* drag-drop reorders + Save POSTs /seed`
- `pwa-install-prompt only fires on Chrome/Edge after beforeinstallprompt`
- `pwa-offline-banner toggles with navigator.onLine`
- `tour completes once when /api/players/me/onboarding-status is 200 { completed:true }`
- `replay deep-link ?finals=true auto-scrolls to last hand`

When you remove or rename a Phase K Wave 2 testid, run
`grep -rn '<testid>' src/frontend/autotable-src/tests/e2e/` before
merging — same drift-detection note as Wave 1.

## Phase K Wave 3 testids — scene split + voice-enabled flag + Microsoft OAuth + seed auto-save + SW pre-cache (Hicks)

Coverage for the Phase K Wave 3 frontend bring-up.  The Wave-2
game-bootstrap chunk (which still carried three.js) is now itself
split: `game-bootstrap.<hash>.js` is a three-free HUD shell, and the
heavy renderer lives in a fresh `scene.<hash>.js` chunk loaded by
dynamic-import after the shell paints.

### Lobby + game bundle budget (post-Wave-3)

The Wave-2 game chunk was 1.11 MB (three.js + AssetLoader + Game +
MoveLog + lobby client attach all eager inside `game-bootstrap.ts`).
Wave 3 splits those out into a dedicated scene module:

| Asset                                 | Size       | Trigger |
|---|---|---|
| `autotable-src.<hash>.js` (eager)     | **214 kB** | Always (lobby + matchmaking + identity + i18n + Sentry shim + auth modal + toast helper) |
| `game-bootstrap.<hash>.js` (lazy)     | **166 kB** | First non-empty `?…` on URL (HUD/chat/voice wiring, no three.js) |
| `scene.<hash>.js` (lazy)              | **922 kB** | Dynamic-imported by `game-bootstrap.ts` after the shell `data-testid="game-shell-ready"` lands — owns three.js, AssetLoader, Game, MoveLog, lobby-client attach |
| `toast.<hash>.js` (lazy)              | ~1.2 kB    | Imported on demand by voice/tournaments error paths |
| `esm.<hash>.js` (Sentry vendor)       | 395 kB     | Only when `<meta name="sentry-dsn">` is non-empty |
| `tournaments.<hash>.js` (lazy)        | ~25 kB     | Tournaments tab hover/focus/click |
| `chat.<hash>.js` (lazy)               | ~16 kB     | `?gameId=` on URL after game bootstrap |

Cold lobby visitor still downloads <500 kB of JS+CSS before the
lobby paints.  A player who follows a `?gameId=` link pays an
additional 166 kB to mount the HUD shell (which paints the
`game-shell-ready` testid) and then the 922 kB scene chunk streams
in concurrently with the lobby tiles loading.

### Scene split — `game-shell-ready` + `game-scene-ready`

- `game-shell-ready` — set as `data-testid` on `<body>` (and
  dispatched as a `mahjong:game-shell-ready` event) by
  `markShellReady()` in `game-bootstrap.ts` once the three-free shell
  has mounted.  Playwright should `await page.waitForSelector('[data-testid="game-shell-ready"]')`
  for HUD-only assertions.
- `game-scene-ready` — set as `data-testid` on `<body>` (and
  dispatched as a `mahjong:game-scene-ready` event) by `scene.ts`
  after the first `requestAnimationFrame` post-mount.  Use this
  rather than `game-shell-ready` for assertions that depend on
  tiles being painted on the canvas.

The two testids are additive — both stay attached for the life of
the page so re-entry into the lobby and back doesn't clear them.

### Voice — per-game `voiceEnabled` flag

Wave 2 gated voice on `?voice=1`; Wave 3 wires it to a per-game
server flag fetched via `GET /api/games/{id}/settings`
(`{ voiceEnabled: bool, viewerIsOwner: bool, ... }`).

- `voice-mic-toggle` — when `voiceEnabled === false` the button
  carries `disabled` + `aria-disabled="true"` + a tooltip
  ("Voice not enabled for this table").  Hover/focus surfaces the
  tooltip; click is a no-op.
- `voice-enable-toggle` — owner-only toggle in the settings drawer's
  Network panel (only renders when `viewerIsOwner === true`).
  Optimistic: flips immediately, POSTs
  `/api/games/{id}/settings/voice` `{ enabled: bool }`, rolls back
  + toasts on non-2xx.  Dispatches `mahjong:voice-enabled`
  CustomEvent (with `{ detail: { enabled } }`) on success so the
  in-flight voice module live-flips the mic without page reload.
- `voice-enable-hint` — sibling `<span class="settings-hint">`
  carrying "When enabled, players join WebRTC voice automatically."
- Toast surfaces on hub-side rejection:
  - `"voice not enabled"` (sent by `VoiceHub.JoinVoice` when the
    server-side flag is off) → "Voice is not enabled for this
    table."
  - `"spectators cannot join voice"` → "Spectators can listen but
    not speak."

### Sign-in modal + Microsoft OAuth

`auth.ts` now mounts a sign-in modal scaffold lazily on first
import (the Wave-2 build had no modal markup in `index.html`; Wave
3's `ensureAuthMarkup()` injects the modal, header chip, and
magic-link landing during module initialisation).

- `signin-button` — header CTA that opens the modal.
- `signin-modal` — root `<div role="dialog" aria-modal="true">`.
- `signin-provider-google` — Google sign-in row (POST flow,
  unchanged from Wave 2's design).
- `signin-provider-microsoft` — **new in Wave 3.** Microsoft brand
  4-tile inline SVG icon (Microsoft brand colours
  `#f25022 / #7fba00 / #00a4ef / #ffb900`).  Clicking it does a
  direct `window.location.href = '/api/auth/login?provider=microsoft&returnUrl=…'`
  (the redirect-via-cookie flow Bishop wired in Wave 3 backend) —
  intentionally different from the POST-then-redirect Google flow
  because Microsoft's Entra token endpoint expects a GET handshake
  with the `returnUrl` round-tripped as a state cookie.
- `signin-provider-github` — GitHub OAuth row (unchanged).
- `signin-provider-email` — magic-link row (unchanged).
- `signin-error` — error pill beneath the provider list (used by
  both OAuth + magic-link failures).
- `auth-header-chip` — top-right pill showing the authenticated
  user's name + provider badge.  Provider badge for Microsoft is
  `🟦 Microsoft`.

### Tournament seeding — auto-POST on each drop

Wave-2 had a manual "Save" button.  Wave 3 auto-POSTs the new
ordering on every successful drop with rollback on failure.

- `tournament-seeding-panel` — unchanged (admin-only).
- `tournament-seed-row-{N}` — drop handler now calls `persistSeeds()`
  which POSTs `/api/tournaments/{id}/seed` with the **Wave-3 wire
  shape** `{ seeds: [{ playerId, seedNumber }, …] }`.  `seedNumber`
  is 1-based.  On HTTP failure the previous ordering is restored,
  the list re-renders, and a toast surfaces "Seed order could not
  be saved — restored previous order."
- `tournament-seeding-save` — still present but functionally a
  belt-and-braces no-op for the current state (manual save remains
  for keyboard reorder users in Wave 4).
- `tournament-seeding-status` — error pill (unchanged).

### Offline-friendly onboarding tour

`tour.ts` Wave 2 probed `GET /api/players/me/onboarding-status`
synchronously before deciding whether to show the tour.  Wave 3
races the probe against a 300 ms timer so an offline visitor isn't
held at a blank page:

- Probe failure or timeout → tour starts immediately, completion
  POST is silently dropped (`offlineFallback = true`).  LS flag
  remains the authoritative offline source of truth.
- Probe success → Wave-2 server-authoritative behaviour preserved.
- No new testids; `onboarding-tour-overlay` continues to drive the
  Playwright assertions.

### Service worker pre-cache manifest

`scripts/generate-sw-manifest.js` (chained from `npm run build:post`
after `parcel build`) emits `manifest-precache.json` at the dist
root and copies the latest `sw.js` into the dist.  The script also
prunes superseded hashed chunks left behind by previous parcel runs
(so the dist doesn't accumulate stale `game-bootstrap.<oldhash>.js`
across waves).

- `manifest-precache.json` shape:
  ```json
  {
    "generatedAt": "<iso>",
    "version": "autotable-v3",
    "assets": ["./index.html", "./autotable-src.<hash>.js", …]
  }
  ```
- `sw.js` `install` handler fetches the manifest with
  `cache: 'no-store'` and calls `cache.addAll(reachable)` after a
  HEAD-probe pass (so one missing entry doesn't fail the whole
  install transaction).  Cache version bumped to `autotable-v3`;
  `activate` purges any cache prefixed `autotable-` that isn't the
  current version — so the Wave-2 → Wave-3 upgrade evicts stale
  precache entries on the first navigation.
- Wave-2 runtime caching strategies (cache-first for hashed assets,
  network-first for `/api/games/public`, network-only for the rest
  of `/api/*` + `/hubs/*`) are unchanged.

### Phase K Wave 3 Playwright coverage — Vasquez

Soft-pass annotations for selectors whose downstream surface
hasn't yet been wired or whose backend ships in a parallel PR:

- `game-shell-ready precedes game-scene-ready (shell within 1 s, scene within 5 s)`
- `voice-mic-toggle disabled + tooltip when voiceEnabled=false`
- `voice-enable-toggle visible only when viewerIsOwner=true`
- `voice-enable-toggle optimistic flip + rollback + toast on POST failure`
- `signin-provider-microsoft renders Microsoft 4-tile SVG icon`
- `signin-provider-microsoft click → window.location.href = /api/auth/login?provider=microsoft`
- `tournament-seed-row drag → auto-POST { seeds: [{ playerId, seedNumber }, …] }`
- `tournament-seed-row POST failure → ordering reverts + toast`
- `tour starts within 300 ms even when /api/players/me/onboarding-status is offline`
- `sw install pre-caches eager + shell + icons via manifest-precache.json`

When you remove or rename a Phase K Wave 3 testid, run
`grep -rn '<testid>' src/frontend/autotable-src/tests/e2e/` before
merging — same drift-detection note as Wave 1.

### Phase K Wave 3 Playwright spec map — Vasquez

The reflection-defensive specs Vasquez landed for Wave 3 (every
fact soft-passes via `test.info().annotations.push({ type:
'soft-pass', … })` when its target test-id or backend hasn't yet
been wired):

- `game-shell-split.spec.ts` — guards the `game-bootstrap` chunk
  size (< 500 kB hard cap, 300 kB target) and verifies the
  `scene` chunk loads lazily after lobby paint.
- `sw-precache.spec.ts` — soft-asserts the SW fetches
  `manifest-precache.json` at install time, the manifest shape is
  `{ assets: [...] }` (array or object form), and the registration
  reaches `activated`/`installing`.
- `tour-offline.spec.ts` — verifies the onboarding tour mounts
  from `localStorage` when `/api/players/me/onboarding-status`
  fails, and that the skip button persists completion locally.
- `voice-enabled-toggle.spec.ts` — owner sees the
  `voice-enabled-toggle`, the mic button is disabled or hidden
  when `VoiceEnabled=false`, and non-owners do not see the toggle.
- `microsoft-oauth.spec.ts` — `signin-provider-microsoft` button
  visibility tied to providers payload, href carries
  `provider=microsoft` (or canonical `/auth/microsoft` route),
  absent/hidden when disabled.
- `tournament-seed-post.spec.ts` — `tournament-seed-handle` is
  keyboard-focusable, `tournament-seed-save` issues
  `POST /api/tournaments/{id}/seed`, non-admins never see the
  save action.


---

## Phase K Wave 4 — Scene split + sparse seeding

### Scene chunk split (Wave 4)

The Wave-3 `scene.<hash>.js` chunk was split into:

- `scene-shell.<hash>.js` (renderer-critical) — mints
  `scene-shell-ready` after the first WebGL frame composites.
  Continues to emit `game-scene-ready` alongside for Wave-3 spec
  back-compat (`scene-shell.ts#markShellReady`).
- `scene-effects.<hash>.js` (deferred) — installs `GameUi` (modals,
  settings drawer, replay viewer) + `MoveLog` sidebar.  Mints
  `scene-effects-ready` once installation completes
  (`scene-effects.ts#markEffectsReady`).

| testid                  | origin                     | notes                                              |
|-------------------------|----------------------------|----------------------------------------------------|
| `scene-shell-ready`     | `scene-shell.ts:markShellReady` | First-frame WebGL composite                   |
| `game-scene-ready`      | `scene-shell.ts:markShellReady` | Wave-3 back-compat (same firing as shell-ready) |
| `scene-effects-ready`   | `scene-effects.ts:markEffectsReady` | GameUi + MoveLog installed                 |

### Tournament sparse seeding (Wave 4)

`buildSeedingPanel` (admin-only, single-elim, registration-open
tournaments) now renders every registered player, with an "Unseeded"
divider separating seeded rows (`#1..#N`) from unseeded rows (`—`).
Dropping a row across the divider promotes/demotes its seed state;
unseeded rows POST with `seedNumber: 0`.  A `400` body-validation
response surfaces the toast "Tournament must have unique sequential
seeds 1..N." (matches Bishop's controller copy).

| testid                                  | origin                                  | notes                                   |
|-----------------------------------------|------------------------------------------|-----------------------------------------|
| `tournament-seeding-unseeded-divider`   | `tournaments.ts:buildSeedingPanel`       | Boundary between seeded + unseeded rows |
| `tournament-seed-row-{i}`               | `tournaments.ts:buildSeedingPanel`       | Sparse rows have `data-seeded="false"` and `aria-label`/rank "—" |
| `tournament-seeding-status`             | `tournaments.ts:buildSeedingPanel`       | Inline error pill on validation failure |

Existing `tournament-seeding-panel` / `tournament-seeding-save` /
`tournament-seed-handle` testids remain unchanged.

### Microsoft brand SVG (Wave 4)

`microsoftIconSvg()` in `auth.ts` is now a 24×24 inline SVG with
`role="img"`, `aria-label="Microsoft"`, and a `<title>Microsoft</title>`
child element — accessibility-name source moved from the wrapper span
into the SVG itself.  The wrapper span at `auth.ts:572` no longer
carries `aria-hidden="true"`.

### Voice toast reason map (Wave 4)

`voice.ts#voiceReasonToText(reason)` maps the Wave-4 typed
`VoiceHubResult.reason` codes (`voice-not-enabled`, `not-seated`,
`spectator`, `rate-limited`, `target-not-found`, `unauthorized`) to
user-facing strings.  `toast.ts#showVoiceToast` keeps the Wave-3
substring heuristic for back-compat with legacy free-text reasons.
No new testids — the toast continues to fire `voice-toast`.

### Phase K Wave 4 Playwright spec map — Vasquez

Wave-4 reflection-defensive specs Vasquez landed (each test
soft-passes via `test.info().annotations.push({ type: 'soft-pass',
… })` when its target test-id, mapper, or chunk shape isn't yet
wired):

- `scene-shell-budget.spec.ts` — total scene/shell/bootstrap JS
  fetched before `networkidle` stays under 500 kB combined, with
  no more than 6 distinct shell-style chunks (waterfall guard).
- `voice-reason-toast.spec.ts` — `voice-failure-toast` text reads
  as human-readable copy (not the raw enum like `rate-limited` or
  the HTTP `429`); unknown reasons fall back to a generic message
  rather than echoing the raw token.  Fires synthetic
  `voice:failure` / `mahjong:voice-failure` `CustomEvent`s so the
  unit under test is `voiceReasonToText`, not the live SignalR hub.
- `tournament-seed-sparse.spec.ts` — admin sparse-seed view of a
  4-slot tournament with only 2 seeded players: `tournament-seed-slot`
  rows render an em-dash (`—`, U+2014) placeholder for unseeded
  rows, never literal `null` or empty string; the 4-row bracket
  does not collapse.
- `microsoft-brand-svg.spec.ts` — the
  `signin-provider-microsoft` button uses an INLINE `<svg>` brand
  glyph and carries no `<img>` whose `src` references a Microsoft
  CDN host (`microsoft.com` / `microsoftonline.com` /
  `static2.sharepointonline.com`); document body likewise carries
  no CDN-hosted Microsoft brand `<img>`.

---

## Phase K Wave 5 — Renderer split + keyboard seeding + typed voice reasons

### Renderer chunk split (Wave 5)

Wave 4 left `scene-shell.<hash>.js` at 886 kB because three.js +
AssetLoader + Game + World + MainView + ClientUi were all statically
imported.  Wave 5 peels every module that imports `from 'three'` out
of the shell graph and into a sibling `three-renderer.<hash>.js`
chunk, dynamic-imported by `scene-shell.ts` when `mountScene()` is
called.  Result:

- `scene-shell.<hash>.js` — **2.3 kB** thin coordinator (no static
  three.js import).  Mints `scene-shell-ready` after the first rAF
  following the renderer boot.
- `three-renderer.<hash>.js` — ~870 kB total (parcel emits two
  sub-chunks of ~145 kB + ~725 kB; both carry three.js + the
  AssetLoader / Game / World / MainView graph).  Dynamic-imported
  by `scene-shell` after `mountScene()` is invoked.  Mints
  `three-renderer-ready` once `Game.start()` returns.
- `scene-effects.<hash>.js` — unchanged from Wave 4 (~60 kB,
  GameUi + MoveLog).
- Both `scene-shell` and `three-renderer` are now in
  `manifest-precache.json` so a returning user with the SW
  installed gets the full WebGL boot path from cache on warm
  game-URL loads (Wave 4 deliberately excluded the renderer chunk
  because pre-caching ~900 kB on install was hostile; with Wave 5's
  thin shell that calculus flips).

The Wave-3 `game-scene-ready` back-compat marker is **retired** in
Wave 5 — Vasquez's Wave-4 specs already gate on `scene-shell-ready`
and the alias just kept dead branches in the renderer chunk.

| testid                  | origin                                     | notes                                              |
|-------------------------|--------------------------------------------|----------------------------------------------------|
| `scene-shell-ready`     | `scene-shell.ts:markShellReady`            | Set after first rAF following renderer boot       |
| `three-renderer-ready`  | `three-renderer.ts:markRendererReady`      | NEW — fires once `Game.start()` returns           |
| `scene-effects-ready`   | `scene-effects.ts:markEffectsReady`        | Unchanged from Wave 4                              |
| ~~`game-scene-ready`~~  | ~~`scene-shell.ts:markShellReady`~~        | **RETIRED in Wave 5** — Wave-3 back-compat dropped |

### Keyboard-accessible sparse seeding (Wave 5)

Wave 4 shipped drag-drop bracket seeding (mouse-only).  Wave 5 adds
a keyboard alternative on each row's seed handle:

- Each handle is now `tabindex="0"` + `role="button"` with a verbose
  `aria-label` describing the current seed state and the available
  keystrokes.  The `aria-hidden="true"` that Wave 4 set on the
  handle is removed.
- **Arrow Up** / **Arrow Down** on a focused handle reorder the row
  by ±1 and persist via the existing `POST /api/tournaments/{id}/seed`
  endpoint.  Focus is restored to the handle's new position on the
  next rAF (lookup by stable `data-player-id`, not the
  index-based testid).
- **Enter** / **Space** on a focused handle opens an inline modal
  dialog (`role="dialog"` + `aria-modal="true"`) carrying
  `data-testid="seed-keyboard-prompt"`.  The dialog has a numeric
  input (1..N to seed at that position, 0 to demote to unseeded),
  Apply + Cancel buttons, and a `role="alert"` validation pill.
- Every reorder / edit announces via a visually-hidden
  `aria-live="polite"` region (`data-testid="seed-live-region"`).
- Drag-drop is unchanged — both interaction models coexist.

| testid                              | origin                                | notes                                              |
|-------------------------------------|---------------------------------------|----------------------------------------------------|
| `seed-row-{playerId}`               | `tournaments.ts:buildSeedingPanel`    | Stamped on the handle; stable across reorders     |
| `seed-keyboard-prompt`              | `tournaments.ts:openSeedKeyboardPrompt` | The inline edit-seed-number dialog              |
| `seed-keyboard-prompt-input`        | `tournaments.ts:openSeedKeyboardPrompt` | Numeric seed-position input                     |
| `seed-keyboard-prompt-ok`           | `tournaments.ts:openSeedKeyboardPrompt` | Apply button                                    |
| `seed-keyboard-prompt-cancel`       | `tournaments.ts:openSeedKeyboardPrompt` | Cancel button                                   |
| `seed-keyboard-prompt-error`        | `tournaments.ts:openSeedKeyboardPrompt` | Inline validation message (`role="alert"`)      |
| `seed-live-region`                  | `tournaments.ts:buildSeedingPanel`    | `aria-live="polite"` announcement region        |
| `tournament-seed-row-{i}`           | `tournaments.ts:buildSeedingPanel`    | Wave-4 index-based row testid (preserved)       |

### Voice reason discriminated union (Wave 5)

Wave 4's `voiceReasonToText` accepted `reason: string` and fell
through to a defensive default-case toast.  Wave 5 promotes the
wire vocabulary to a TypeScript discriminated union so the mapper
is **compile-time exhaustive**:

```ts
export type VoiceReason =
  | 'voice-not-enabled'
  | 'not-seated'
  | 'spectator'
  | 'rate-limited'
  | 'target-not-found'
  | 'unauthorized';
```

The typed entry point `voiceReasonToText(reason: VoiceReason): string`
is an exhaustive switch with a `const _exhaustive: never = reason`
guard — adding a new `VoiceReason` member without updating the
switch becomes a compile-time error.  A second wrapper
`voiceReasonStringToText(reason: string)` normalises legacy
kebab/snake/camel aliases (`not_seated`, `notseated`, `spectators`,
`unauthenticated`, …) and falls back to a generic "Voice chat error:
…" copy for unknown tokens — preserving the Wave-4 default-case
behaviour without compromising exhaustiveness on the typed API.

`ALL_VOICE_REASONS` is exported as a read-only array for Vasquez's
Wave-5 contract test that asserts all 6 reason codes resolve to
non-empty text mappings.

Bishop's Wave-5 backend disambiguates `spectator` from `not-seated`
on the wire; the mapper carried a distinct `spectator` branch since
Wave 4, so no copy change was required.

No new testids — `voice-failure-toast` continues to wrap the
mapper output.

### Phase K Wave 5 Playwright spec map — Vasquez

Wave-5 specs Vasquez has on deck (each one reflection-defensive
with a soft-pass annotation):

- `scene-shell-budget-w5.spec.ts` — `scene-shell.<hash>.js`
  individually under 500 kB (Wave-5 explicit shrink target); total
  scene/shell/bootstrap JS fetched before `networkidle` still
  bounded.
- `tournament-seed-keyboard.spec.ts` — focus a `seed-row-{playerId}`
  handle, press `ArrowDown`, assert the corresponding row's new
  position; press `Enter`, assert `seed-keyboard-prompt` is visible
  and focus moved into its input; submit and assert `seed-live-region`
  carries a non-empty announcement.
- `voice-reason-exhaustive.spec.ts` — for every entry in
  `ALL_VOICE_REASONS`, the mapper returns a non-empty string that
  is neither the raw enum value nor an empty default.

### Phase K Wave 5 Playwright spec map — Vasquez (final)

The five Vasquez-owned W5 specs are now landed (all reflection-
defensive with `soft-pass` annotations; chromium-only via
`test.skip(testInfo.project.name !== 'chromium', ...)`):

- `scene-shell-budget-strict.spec.ts` — STRICT < 500 kB combined
  scene-shell payload (Wave 4 was soft). Excludes the new
  `three-renderer` chunk from the budget (intentional per the
  W5 split). Soft-passes only when no shell chunks emit
  (dev-server / pre-build).
- `keyboard-seed-reorder.spec.ts` — focus a
  `[data-testid="seed-row-handle"]` element and press `ArrowDown`;
  hard-asserts the first two rows' `data-seed-id` attribute swap.
  Soft-passes when the panel hasn't yet mounted.
- `voice-reason-spectator-distinct.spec.ts` — dispatches
  `voice:failure` with `reason: 'spectator'` then with
  `reason: 'not-seated'`, reads the `voice-failure-toast` text
  for each, and hard-asserts the spectator copy is non-empty
  AND differs from the not-seated copy.
- `three-renderer-lazy.spec.ts` — observes JS requests on lobby
  load; hard-asserts no chunk matching `three-renderer|three\..*\.js`
  is fetched before `networkidle`. Soft-passes when no three chunk
  has yet been observed (pre-split / dev-server).
- `jwks-endpoint-shape.spec.ts` — `GET /api/auth/.well-known/jwks.json`
  MUST return HTTP 404 with `Cache-Control: no-store`. Soft-passes
  only on network unreachability (dev-server preview).

Each spec uses `test.info().annotations.push({ type: 'soft-pass', … })`
to record forward-staged surfaces — these annotations are visible in
the Playwright HTML report without inflating the failure count.

### Phase K Wave 6 selector map — Hicks (Frontend)

W6 introduces five new frontend surfaces. The test-IDs below are the
**hard-pin** selectors future Playwright specs (W7+) should consume.
None of the matching elements are present on first-paint of any
existing screen, so e2e specs MUST treat absence as "feature gated
behind route / event / server reply" (use the soft-pass pattern
introduced in W5) — never assume the panel mounts unconditionally.

#### AI commentary side panel — `src/commentary-panel.ts`
Mounts into `<aside id="replay-commentary-host" data-testid="replay-commentary-host">`
on the replay screen when `replay.openServer(payload)` resolves
with a non-zero `gameId`. The fetch hits
`/api/games/{gameId}/commentary/replay`; on `404`/`503` the empty-
state copy renders pointing at the Phase L plan.

- `replay-commentary-host` — the wrapper aside (always present in
  the DOM, even when the panel module hasn't been loaded yet).
- `commentary-panel` — the root element of the live module
  (only appears after dynamic import resolves).
- `commentary-panel-loading` — visible while fetch is in flight.
- `commentary-panel-empty` — visible when the API replies with
  no commentary lines (or 404/503 forward-staged response).
- `commentary-panel-error` — visible when the fetch throws.
- `commentary-line-{idx}` — one per rendered commentary turn,
  `idx` is the zero-based index in the response array.

#### Spectator HLS livestream viewer — `src/spectator-livestream.ts`
Bound to the `#/spectate/{tableId}` hash route by
`installSpectatorRoute()` (called from `scheduleSpectatorRouteLazyMount()`
in `src/index.ts`). HLS.js is loaded from the public CDN on demand
for non-Safari browsers; Safari falls through to native HLS.

- `spectator-livestream-screen` — root container.
- `spectator-livestream-player` — the `<video>` element.
- `spectator-livestream-status` — status text region (announces
  "connecting", "live", "stalled", or error copy).
- `spectator-count` — current spectator count badge driven by
  the SignalR `spectatorCountUpdate` event.
- `spectator-livestream-leave` — the leave button (returns to
  the lobby; releases the spectator group).

#### Bracket renderer strategy — `src/bracket-renderer.ts`
`rerenderBracket()` in `tournaments.ts` now dispatches to a strategy
based on `tournament.format`. The container always carries
`data-testid="bracket-format-{format}"` where `{format}` is one of
`single-elim` / `swiss` / `double-elim` / `round-robin`.

- `bracket-format-{format}` — root container per format.
- `bracket-round-{n}` — column / region per round, 1-indexed (winners side
  for double-elim; the only round-set for single-elim / Swiss / RR).
- `bracket-round` — bare-name round-title label inside each winners-side
  round group (added Wave 8).
- `bracket-match` — Wave 8 canonical match-tile testid. The W6
  `bracket-match-{round}-{matchIndex}` variant is replaced — match
  identifiers now live on `data-match-round` / `data-match-index` /
  `data-match-id` attributes so a Playwright `getAllByTestId('bracket-match')`
  returns the row set in render order.
- `winners-bracket` — winners-bracket column root (Wave 8; W6's
  `bracket-double-elim-winners` testid is replaced).
- `losers-bracket` — losers-bracket column root (Wave 8; W6's
  `bracket-double-elim-losers` testid is replaced).
- `losers-bracket-round-{n}` — losers-bracket round group, 1-indexed.
- `losers-bracket-round` — bare-name losers round-title label inside each
  losers-side round group (added Wave 8).
- `bracket-grand-final` — grand final card (Wave 8; W6's
  `tournament-grand-final` is kept on the same element via
  `data-testid-legacy` for any straggler spec).
- `bracket-match-grand-final` — grand-final match-row child.
- `grand-final-reset` — reset-match card (only present when the
  bracket actually resets; see DoubleElimRenderer for the gating
  rules).
- `bracket-match-grand-final-reset` — reset-match row child.
- `bracket-live-update` — invisible Playwright anchor on the bracket
  wrap, carries `data-update-id="{timestamp}"` that changes on
  every render (Vasquez's `bracket-live-update.spec.ts` mutation-
  observes this attribute to detect a re-render without a page
  reload).
- `bracket-swiss-standings` — Swiss standings table (Swiss + RR formats).

#### PWA install button polish — `src/pwa.ts`
The install affordance is now a real `<button data-testid="pwa-install-button">`
in the top-bar (was an inline prompt). The legacy `pwa-install-prompt`
testid is preserved as an alias on a hidden `<span>` inside the
button so existing W3/W4 e2e specs continue to resolve until rewritten.

- `pwa-install-button` — the visible top-bar control.
- `pwa-install-prompt` — legacy alias (hidden span child).
- `appinstalled` handler at module bottom hides the button after
  install completes; Playwright should not poll the visibility
  state after dispatching the install event.

#### Tour additions — `src/tour.ts`
Two new tour stops are inserted (existing copy updated from
"6 stops" to "10 stops, ~45 seconds"):

- Step 6 — voice-setup walkthrough (anchors on `voice-toggle` /
  `voice-settings`). Selector: `tour-step-voice-setup`.
- Step 9 — tournament-view stop (anchors on `tournament-tab` /
  `bracket-format-*`). Selector: `tour-step-tournament-view`.

Generic per-step containers still expose `data-testid="tour-step"`;
the named selectors above are additive.

---

### Phase K Wave 6 Playwright spec map — Vasquez

Seven new specs land in Wave 6 (each chromium-only via `test.skip(…)`,
each soft-pass annotates `test.info().annotations.push({ type:'soft-pass', … })`
when the underlying surface is forward-staged):

- `commentary-panel-loads.spec.ts` — Hicks's W6 commentary panel mounts
  on the replay route with `data-testid="commentary-panel"`. Mock
  backend returns a 2-item stub envelope (`generator: "stub"`); the
  panel state-machine arms (loading → empty → content) settle into
  the content arm. Soft-passes when the testid root is not yet
  observable (forward-staged Hicks module).
- `spectator-livestream-player.spec.ts` — `<audio>` element with an
  HLS playlist source (`.m3u8` or `application/vnd.apple.mpegurl`)
  mounts under `data-testid="spectator-livestream-viewer"`. Mock
  returns a minimal HLS manifest (`#EXTM3U`). Soft-passes when no
  `<audio>` with HLS-looking source is yet observable.
- `bracket-format-swiss.spec.ts` — Swiss bracket renderer emits
  `data-testid="bracket-format-swiss"` for a tournament with
  `format: "swiss"`. Probes the lobby first and falls back to
  `#/tournaments/{id}`. Soft-passes when the testid is not yet
  observable.
- `bracket-format-double-elim.spec.ts` — symmetric to the Swiss
  spec, asserts `data-testid="bracket-format-double-elim"` for a
  tournament with `format: "double-elim"`.
- `pwa-install-prompt.spec.ts` — synthesises a
  `beforeinstallprompt` event (`new Event(…)` + `prompt()` +
  `userChoice` polyfilled) and asserts the install button at
  `data-testid="pwa-install-button"` is attached. Chromium does
  not fire the event organically in headless / sandboxed mode, so
  the spec is responsible for the synthesis.
- `three-renderer-tree-shake.spec.ts` — three-renderer chunk is
  NOT fetched before `networkidle` AND when observed lazily MUST
  be under the 700 kB W6 ceiling. Soft-passes when no chunk is
  observed at all (canvas not mounted in test env). The pre-
  networkidle assertion is HARD (a regression where the chunk
  rides on lobby load is a hard failure).
- `oidc-discovery-shape.spec.ts` — `GET /.well-known/openid-configuration`
  returns either 404 with a structured `{ error | reason | error_description }`
  body (HS256 default mode) OR 200 with `{ issuer, jwks_uri }` (RS256
  mode). Never 5xx. Soft-passes on dev-server unreachability.

The W6 surface area extends Bishop's auth lane (RS256 migration,
OIDC discovery, voice livestream HLS, spectator SFU stub, AI
commentary stub, Swiss + double-elim brackets) and Hicks's frontend
lane (commentary panel UI, spectator livestream viewer, bracket
renderers, three-renderer tree-shake to <700 kB, PWA install
prompt). Bishop and Hicks pair-land their surfaces; these specs
hard-pin the cross-pair contracts at the Playwright layer.

---

## Phase K Wave 7 — Vite swap + CustomOutline + vendored HLS.js + commentary contract (Hicks)

W7 reshapes three existing surfaces (build tooling, three.js
post-processing stack, HLS.js loader) and one new surface
(commentary panel contract). All four touch testids visible to
Playwright; this section is the hard-pin reference.

### Bundler swap → Vite

The eager + lazy chunks all kept their canonical filenames
(`autotable-src.<hash>.js`, `game-bootstrap.<hash>.js`,
`scene-shell.<hash>.js`, `three-renderer.<hash>.js`, etc.) so
**no Playwright spec needs updating for the bundler swap**.

Vasquez's `three-renderer-tree-shake.spec.ts` continues to assert
the chunk is not loaded before `networkidle` AND, when lazily
observed, is under the **W7 ceiling**:

> W7 lowered the renderer-chunk ceiling from 700 kB (W6) to
> **600 kB**. Update the spec's hard-fail constant accordingly.
> Current build: 578.72 kB (single biggest chunk).

A new spec, `dist-size-monotonic.spec.ts` (forward-staged for
Vasquez's W7 lane), reads `src/frontend/autotable-src/dist-size.json`
and asserts `three-renderer-big` is monotonically non-increasing
across consecutive history entries. Regression = hard fail.

### CustomOutline (OutlinePass replacement)

No testid impact. The yellow outline visible during the discard
selection flow is rendered by `src/render/custom-outline.ts` now
instead of `OutlinePass`; Vasquez's `expect.toHaveScreenshot`
diff threshold remains ≤2% (same color, same thickness).

If a screenshot regression surfaces, see
`docs/frontend-three-budget.md §3` for the visual-parity table.

### Vendored HLS.js

`spectator-livestream.spec.ts` previously asserted (W6 lane)
that the spectator viewer fetched a CDN-hosted HLS.js bundle on
`#/spectate/{tableId}` route entry. **W7 changes the URL to a
same-origin chunk**:

- Before (W6): `https://cdn.jsdelivr.net/npm/hls.js@1.5.13/...`
- After (W7): `/autotable/hls.<hash>.js` (same-origin, served
  by our static handler)

The spec's network mock needs adjusting:

```diff
- await page.route('**/cdn.jsdelivr.net/npm/hls.js**', route => …);
+ await page.route('**/hls.*.js', route => …);
```

`spectator-livestream-status` continues to emit `connecting →
live → stalled` exactly as in W6; no other testid changed.

### Commentary panel — W7 JSON contract rewrite

Bishop's W7 commentary endpoint returns a `CommentaryRecord[]`
shape (richer than W6's `{lines: string[]}` envelope):

```ts
interface CommentaryRecord {
  gameId: string;
  turnNumber: number;
  phase: 'draw' | 'discard' | 'call' | 'win' | 'reveal' | 'narration';
  speaker: 'pbp' | 'color' | 'analyst' | 'narrator';
  text: string;
  emotionIntensity: number;      // 0..100
  tileReferences: string[];      // tile IDs (e.g., "m1", "p5", "s9", "z3")
  generatedAt: string;           // ISO-8601
}
```

The panel renderer groups records by `turnNumber` (collapsible
sections) and emits per-record:

- A **speaker badge** (color-coded per role).
- The **text body**.
- **Tile-reference chips** (clickable; emit a `commentary:tile-ref`
  `CustomEvent` carrying `{tileId, turnNumber}` for the board-pane
  to consume — Wave-8 board-pane integration item).
- An **emotion-intensity bar** (CSS gradient, 0..100% width).

The legacy W6 `{lines:[…]}` envelope is parse-fallback-compatible:
`commentary-panel.ts:normalizeRecords()` accepts either shape so
mid-deploy a stale server reply doesn't crash the panel.

#### W7 testid map

| Testid                                | Element                  | Notes                                                                                          |
|---------------------------------------|--------------------------|------------------------------------------------------------------------------------------------|
| `commentary-panel`                    | Root `<section>`         | Carried over from W6 — same root testid.                                                        |
| `commentary-panel-loading`            | `<div>` (loading spinner)| Carried over from W6.                                                                           |
| `commentary-panel-empty`              | `<div>` (empty state)    | Carried over from W6.                                                                           |
| `commentary-panel-error`              | `<div>` (error state)    | Carried over from W6.                                                                           |
| `commentary-turn-{n}`                 | `<section>` per turn     | NEW. `n` = `turnNumber` from the record. Collapsible group.                                     |
| `commentary-turn-toggle-{n}`          | `<button>` (toggle)      | NEW. Expand/collapse the turn group; ARIA-controlled.                                            |
| `commentary-record-{idx}`             | `<article>` per record   | NEW. `idx` is the zero-based index across the full record array (NOT per-turn).                  |
| `commentary-speaker-{role}`           | `<span>` speaker badge   | NEW. `role` is one of `pbp` / `color` / `analyst` / `narrator`. One badge per record.            |
| `commentary-tile-ref-{tileId}`        | `<button>` chip          | NEW. `tileId` is the tile reference (e.g., `m1`, `z3`). Click dispatches `commentary:tile-ref`.  |
| `commentary-intensity-{idx}`          | `<div>` intensity bar    | NEW. `idx` matches the `commentary-record-{idx}` index. ARIA `progressbar` role + `aria-valuenow`. |

The W6 `commentary-line-{idx}` testid is **retired** — W7 specs
should target `commentary-record-{idx}` (the rename reflects the
shape change from `string` to `CommentaryRecord`).

### Vasquez Playwright additions expected for W7

| Spec                                  | What it asserts                                                                                                                                         |
|---------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------|
| `three-renderer-budget-w7.spec.ts`    | Renderer chunk ≤600 kB (down from 700 kB ceiling). Reads `dist-size.json` for the current wave's `three-renderer-big` entry.                              |
| `dist-size-monotonic.spec.ts`         | `history[].chunks["three-renderer-big"]` is non-increasing across consecutive history entries.                                                            |
| `commentary-record-shape.spec.ts`     | `commentary-record-0` rendered when API replies `[{turnNumber: 1, speaker: 'pbp', text: 'East draws…', tileReferences: ['m1'], emotionIntensity: 40, …}]`. |
| `commentary-tile-ref-click.spec.ts`   | Clicking `commentary-tile-ref-m1` fires a `commentary:tile-ref` CustomEvent on the panel root with `detail = {tileId: 'm1', turnNumber: 1}`.              |
| `commentary-turn-collapse.spec.ts`    | Clicking `commentary-turn-toggle-2` flips `aria-expanded` from `true` → `false` and hides the matching `commentary-record-{idx}` children.                |
| `csp-no-jsdelivr.spec.ts`             | Page response carries `Content-Security-Policy` header without `cdn.jsdelivr.net` in `script-src` (W7 vendoring win).                                     |

All six specs should follow the existing soft-pass pattern (skip
when surface not yet observable; hard-fail when observable AND
violating).

The W7 surface area extends Hicks's frontend lane only (no Bishop
pairing this wave). Apone, Vasquez and Bishop are notified via
the inbox memo `.squad/decisions/inbox/hicks-phase-k-wave-7.md`.

---

### Phase K Wave 7 Playwright spec map — Vasquez

Six new specs land in Wave 7 (each runs on chromium + mobile-chrome
via the default playwright project list, chromium-only via
`test.skip(testInfo.project.name !== 'chromium', …)`; each soft-pass
annotates `test.info().annotations.push({ type:'soft-pass', … })`
when the underlying surface is forward-staged):

- `bundler-swap-no-regression.spec.ts` — Hicks's W7 bundler swap
  (Vite / Rspack / Parcel-manual). Probes the lobby load for
  `console.error` + `pageerror` events. Filters out HMR /
  sourcemap / service-worker / websocket noise. Hard-fails when
  real errors emerge — joined error list surfaces in the failure
  message so Hicks can diagnose what the bundler emitted.
- `commentary-record-rendering.spec.ts` — Bishop's W7
  `CommentaryRecord` envelope ships from
  `/api/replay/{id}/commentary` with `{ items: [{ gameId,
  turnNumber, phase, speaker, text, emotionIntensity,
  tileReferences, generatedAt }] }`. Hicks's panel mounts three
  visualisation axes: `data-testid="commentary-speaker"`,
  `data-testid="commentary-emotion"`,
  `data-testid="commentary-tile-ref"`. Mock-backend supplies a
  2-item record stub. All-three present → hard-assert; any
  partial → soft-pass.
- `outline-shader-visual.spec.ts` — Hicks's W7 outline-shader
  module (OutlinePass replacement). Probes for
  `window.enableOutline()` OR `window.game?.renderer?.enableOutline()`.
  When the hook is observable, invokes it and confirms it does
  NOT throw. Soft-pass when no hook observable.
- `three-renderer-trend.spec.ts` — the wave-over-wave regression
  gate for the three-renderer chunk byte size. Fetches
  `dist-size.json` from `/dist-size.json` (or `/dist/dist-size.json`
  or `/autotable/dist-size.json`). Three schema variants
  tolerated: `{ current, previous }` pair, `{ waves: [{wave, …}] }`
  array, or a flat current-only object. When the previous-wave
  comparison is available, hard-asserts `current ≤ previous`
  bytes. Otherwise asserts the W7 ceiling (≤ 550 kB). Failure
  message names the bytes + suggests `npm run bundle:visualize`.
- `commentary-tile-ref-cross-pane.spec.ts` — the cross-pane
  interaction smoke. Installs a `tile-highlight` event sniffer
  on `document` BEFORE page boot, then clicks a
  `data-testid="commentary-tile-ref"` and waits up to 500ms for
  `window.__lastHighlightedTile` to populate. Hard-pin: the
  highlight detail MUST carry a non-empty tile id. Soft-passes
  when the testid or handler isn't yet observable.
- `pwa-icon-maskable.spec.ts` — Hicks's W7 manifest carries
  `purpose: "maskable"` on at least one icon. Fetches the
  manifest from `/manifest.webmanifest` (or
  `/autotable/manifest.webmanifest`, or `.json` variants);
  searches `manifest.icons[]` for an entry where `purpose`
  includes the `maskable` token. Hard-fails when the icon set
  ships but no maskable entry is present; soft-passes when
  the manifest or icons array isn't yet populated.

The W7 surface area pairs Bishop's `CommentaryRecord` DTO (Wave 7
Bishop lane) with Hicks's commentary-panel rendering + cross-pane
interaction, plus the bundler-swap + outline-shader + PWA-icon
deliverables. Vasquez's specs hard-pin the cross-pair contracts at
the Playwright layer.

## Phase K Wave 8 — Vasquez Playwright additions

Wave 8 ships seven new Playwright specs co-located with the earlier
waves under `tests/e2e/`. These specs hard-pin Bishop's W8 backend
surfaces (commentary streaming, tournament bracket SignalR, Swiss
tiebreaker, Janus voice hub, OpenAI commentary generator) against
Hicks's W8 frontend surfaces (losers-bracket renderer, cross-pane
tile-ref latency, 540 KB bundle cap, PWA Lighthouse score, Vite
dev-server SignalR proxy, live bracket re-render, streaming
commentary panel).

The W8 specs use the same forward-stage tolerance pattern as W7:
when the surface being tested isn't yet observable (testid absent,
endpoint not served, window-hook not exposed), the spec emits a
`forward-staged` annotation and soft-passes. Once Bishop's and
Hicks's W8 deliverables land, the soft-passes flip to hard-asserts
without code changes.

W8 testids registered (shared between Hicks & Vasquez per
`tests/ci/lane-map.json` `shared_files.selectors_md_shared`):

- `losers-bracket` — root container for the double-elim losers half.
- `losers-bracket-round` — per-round label inside the losers half.
- `bracket-grand-final` — the grand-final tile.
- `tournament-bracket` — the live-update bracket pane root.
- `bracket-match` — individual match tiles (incremented as the
  bracket grows via `TournamentBracketUpdated`).
- `commentary-panel` — the streaming commentary pane root.

W8 window hooks:

- `window.__publishTournamentBracketUpdate(payload)` — drives the
  simulated SignalR `TournamentBracketUpdated` message in tests.
- `window.__lastHighlightedTile` / `window.__highlightTimestampMs` —
  W7's tile-highlight observability hooks extended in W8 with the
  receipt timestamp axis for latency assertions.

Wave 8 specs:

- `losers-bracket-render.spec.ts` — mocks the W8 bracket payload
  (3 losers rounds + grand final) and verifies the renderer emits
  `data-testid="losers-bracket"`, three `losers-bracket-round`
  labels, and a `bracket-grand-final` tile. Soft-passes when
  testids aren't yet wired.
- `commentary-tile-ref-latency.spec.ts` — extends W7's cross-pane
  spec with a strict latency hard-assert: tile-ref click to
  `tile-highlight` dispatch MUST be < 500ms. Uses page-context
  `performance.now()` for both endpoints of the measurement to
  share the same time origin.
- `three-renderer-540-hard.spec.ts` — the W8 external bundle gate.
  Reads `dist-size.json` from the dev server, finds the K8 wave
  entry, and hard-asserts `three-renderer-big` (or fallback
  `three-renderer` / `three-renderer-large`) ≤ 540 × 1024 bytes.
  Soft-passes when the K8 history entry isn't recorded yet.
- `pwa-lighthouse-score.spec.ts` — fetches the recorded Lighthouse
  JSON report from canonical artefact paths and hard-asserts the
  PWA category score ≥ 0.95. Tolerates three Lighthouse schema
  variants (vanilla `categories.pwa.score`, flattened `pwa`, and
  `score.pwa` subtree).
- `vite-signalr-proxy.spec.ts` — verifies the Vite dev-server proxy
  forwards `/hub/*` paths to the backend. Detects Vite via
  `/@vite/client` probe; soft-passes against production Docker
  base URLs (no Vite layer to exercise). Hard-fails on 502/504
  (proxy broken); accepts 200/401/404/400/405 (proxy wired,
  backend semantic response).
- `bracket-live-update.spec.ts` — drives a synthetic SignalR
  `TournamentBracketUpdated` message via the
  `window.__publishTournamentBracketUpdate` hook and asserts the
  bracket pane re-renders without a page reload. Watches for a
  new `bracket-match` tile to appear.
- `commentary-streaming.spec.ts` — stages a 3-chunk SSE stream on
  `/api/replay/{id}/commentary/stream` and verifies the commentary
  panel renders the text progressively. Probes the DOM twice
  ~250ms apart; soft-passes when the static fallback wins the
  race (panel is fully populated on the first probe).

The W8 surface area pairs Bishop's commentary-streaming +
tournament-bracket SignalR + Janus voice + OpenAI commentary
generator + Swiss tiebreaker + AuditEvent enrichment + Idempotency
middleware with Hicks's W8 frontend deliverables. The W8 specs
keep the zero-skip streak (forward-stage soft-pass annotations
are not xunit `Skip`; they are early-return success).

## Phase K Wave 8 — Hicks confirmation (W8 implementation footer)

The W8 testids called out above are now wired in code. Concrete
landing points:

- `data-testid="winners-bracket"` / `data-testid="losers-bracket"`
  on the per-side columns (`bracket-renderer.ts`, see
  `buildBracketColumn`).
- `data-testid="losers-bracket-round-{n}"` on each losers-side
  round group; the round-title label inside each group also
  carries `data-testid="losers-bracket-round"` (bare-name) for
  Vasquez's `getAllByTestId('losers-bracket-round')` count assert.
- `data-testid="bracket-grand-final"` on the grand-final card.
- `data-testid="grand-final-reset"` on the reset-match card
  (rendered only when the losers-bracket champion wins the first
  grand final — see `shouldRenderResetMatch` for the rule).
- `data-testid="bracket-live-update"` on a hidden `<div>` with
  `data-update-id={millis}` updated on every renderer call.
- `data-testid="tile-highlight-overlay"` on the highlight-pulse
  layer over the WebGL canvas; the canvas host (`#main`) and the
  overlay both carry `data-highlight-tile-id={id}` while the
  pulse is active (2 s).

Window observability hooks installed:

- `window.__lastHighlightedTile` (string) — last tile id passed to
  `MainView.pulseHighlight`. Written synchronously inside the
  call, before the `tile-highlight` event dispatch.
- `window.__highlightTimestampMs` (DOMHighResTimeStamp) — value of
  `performance.now()` captured at the same call site.
- `window.__publishTournamentBracketUpdate(payload)` — installed
  by `tournaments.ts` when the panel is first activated.  Calls
  the same refresh handler the SignalR `TournamentBracketUpdated`
  hub event drives, so a spec can simulate the push without
  spinning up a real hub.

Event bus (additions to W7's `mahjong:*` event family):

- `mahjong:highlight-tile` (window, `CustomEvent<{ tileId }>`) —
  dispatched by `commentary-panel.ts:renderTileRef` on tile-ref
  chip click.  `MainView` listens and calls `pulseHighlight`.
- `tile-highlight` (window, `CustomEvent<{ tileId, timestamp }>`) —
  dispatched by `MainView.pulseHighlight` AFTER the overlay
  becomes active.  Vasquez's latency spec asserts the
  click→`tile-highlight` round trip < 500 ms.
- `commentary:tile-ref` (window, `CustomEvent<{ tileId }>`) — W7
  legacy event, kept alongside `mahjong:highlight-tile` for
  Hank's analyst-overlay consumer.

Vite dev-server proxy (W8 item 5) — see
`docs/frontend-build-tooling.md §3`. `vite.config.ts:server.proxy`
routes `/hubs/*`, `/autotable/ws`, and `/api/*` from
`http://localhost:5173` to `process.env.AUTOTABLE_BACKEND ??
'http://localhost:5000'` with `ws: true` so SignalR's WebSocket
transport survives the hop.  `hub.ts:hubUrl()` no longer returns
`http://localhost:5000/...` in dev — it returns the same
same-origin `/hubs/changsha` it returns in production, and the
dev proxy handles the routing transparently.

PWA score (W8 item 4) — **1.00** (Lighthouse 11.7.1).  See
`docs/frontend-pwa-audit.md` for the audit recipe + the icon-path
bug found during the audit (manifest icons 404'd because Vite
hashed them to root but the manifest kept the source paths).
Fixed in `vite.config.ts:copyStaticAssets`.



---

## Phase K Wave 9 — testid + DOM axis additions (Vasquez QA)

The W9 forward-stage Playwright suite drives six new specs.  None
adds a *new* `data-testid` — all six reuse existing testids from
W7/W8 — but they introduce two new DOM-side axes that Hicks must
keep stable.

### Axis: `findThingByFace` (three.js raycast helper)

Spec: `tests/e2e/three-mesh-pulse.spec.ts`

`findThingByFace(faceIndex)` MUST be exposed on the global
`window` object during dev/E2E builds (gated behind
`import.meta.env.DEV || window.__e2e === true`).  The function
takes an integer `faceIndex` (the THREE.Raycaster intersection
face index) and returns:

- A reference to the `Thing` (tile / panel / surface mesh) that
  owns that face, OR
- `null` if no Thing is registered for that face.

The companion call `pulseHighlight(Thing)` MUST animate the
returned Thing for ≥ 320 ms with a per-frame transform delta
visible in the Playwright pixel-diff (≥ 4 channel-delta over a
40×40 patch centred on the Thing).  The W9 spec measures the
visible pixel delta directly — no `data-testid` is asserted.

### Axis: `three-mesh-pulse` (legacy event channel)

Spec: `tests/e2e/three-mesh-pulse.spec.ts`

A `window.dispatchEvent(new CustomEvent('three-mesh-pulse',
{ detail: { thingId } }))` MUST drive the same animation path
as `pulseHighlight(Thing)`.  Bishop's commentary-overlay landed
the event channel in W8; the W9 spec asserts the channel still
fires `tile-highlight` after the 100 ms pulse delay.

### Spec inventory (all chromium-only, all forward-stage tolerant)

| Spec | Asserts | Soft-pass condition |
|------|---------|---------------------|
| `three-mesh-pulse.spec.ts` | `findThingByFace` returns a Thing, `pulseHighlight` animates ≥ 320 ms, `tile-highlight` event fires | `window.findThingByFace` or `window.pulseHighlight` missing |
| `three-renderer-510-hard.spec.ts` | `scripts/dist-size.json` K9 entry ≤ 510 KB | `dist-size.json` missing or no K9 entry |
| `lighthouse-13-pwa.spec.ts` | `docs/lighthouse-13-report.json` schema 13.x, PWA score ≥ 0.95 | report file absent |
| `bracket-canonical-shape.spec.ts` | `window.__publishTournamentBracketUpdate({…unknown payload…})` triggers `console.error` mentioning `canonical`/`schema`/`bracket` | publisher fn missing |
| `livestream-canonical-path.spec.ts` | `GET /api/tables/{id}/livestream` returns 301/308 with `Location: /api/voice/livestream/...` (uses `maxRedirects: 0`) | backend not running / route not present |
| `signalr-backpressure.spec.ts` | 5000-msg push keeps `performance.memory.usedJSHeapSize` growth < 50 MB | `performance.memory` unavailable (non-Chromium) |

Hicks: when you wire `findThingByFace` + `pulseHighlight` on the
global, append the canonical citation here (file + line) per the
W5 maintenance note above.

## Phase K Wave 9 — Hicks confirmation (W9 implementation footer)

W9 delivered the frontend half of Vasquez's W9 axes; the spec
files Vasquez authored are forward-stage tolerant by design, but
the production-code surface those specs target is now wired:

### `findThingByFace(tileId)` + `setHighlightedThing(thing)`

Implemented in `src/world.ts:911-980` (canonical citation):

- `World.findThingByFace(tileId: string): Thing | null` parses
  the commentary wire-format tile id and returns the first
  matching `Thing` (or null). Accepted spellings include
  suit-first (`man5`, `pin3`, `sou9`), rank-first (`5m`, `3p`,
  `9s`, `3b`), honors (`east|south|west|north|white|green|red`
  + `wind-e` / `dragon-w` aliases + `haku|hatsu|chun`), and the
  three red-five aka-dora variants (`red-man5`, `0p`, etc.).
  Match is via `thing.typeIndex % 37 === face` so the back-color
  variant is collapsed.
- `World.setHighlightedThing(thing | null)` sets the active
  highlight target and stamps the start time. The pulse runs
  for `World.HIGHLIGHT_DURATION_MS = 2000 ms`; calling again
  resets the timer (most-recent-click wins). Passing null
  clears immediately on the next frame.

The two methods are intentionally INSTANCE methods on `World`
(not `window.*` globals). The W9 directive's
`commentary-tile-ref` spec exercises the public surface via the
`mahjong:highlight-tile` event channel; the bare-globals spec
(`three-mesh-pulse.spec.ts`) is forward-staged until a future
wave wires window-level adapters.

### Event channel: `mahjong:highlight-tile`

Wired in `src/game.ts:96-117`. Payload:
`window.dispatchEvent(new CustomEvent('mahjong:highlight-tile',
{ detail: { tileId: 'man5' } }))`.

The listener calls `world.findThingByFace(detail.tileId)` then
`world.setHighlightedThing(thing)`. The W8 CSS-overlay
listener still fires alongside this one (independent code
path) — both run in parallel and reinforce visually rather than
fight each other.

### Outline pulse hull

`src/render/custom-outline.ts` carries a second hull pool
(separate from the W7 selection pool) keyed by mesh identity.
`outline.setHighlight(meshes, intensity)` attaches the hull;
`outline.setHighlightIntensity(intensity)` and
`outline.setHighlightColor(hex)` adjust per-frame. Default
highlight color: `0xff8c1a` (warm orange — distinct from the
selection ring's edge color). Thickness `0.036` (vs selection
`0.022`) so the highlight sits visually outside the selection
ring when both apply.

The per-frame envelope is `wave * (1 - t)` where
`wave = 0.5 + 0.5 * sin(t * π * 4)` (two cycles over the 2 s
window) and `t = elapsed / DURATION`. Final intensity multiplies
the outline-thickness uniform.

### Bracket canonical shape — `bracket-shape-error` testid

`src/bracket-renderer.ts:DoubleElimRenderer.render` emits a
`<div data-testid="bracket-shape-error" role="alert">` element
plus a `console.error('[bracket] Unknown double-elim wire
shape — expected { layout: { winnersBracket, losersBracket,
grandFinal: { match, resetMatch } } }')` when the input lacks
a canonical `layout` field. The W6→W8 heuristic fallback
(`partitionDoubleElim` scanning round-number signs) is no
longer reached by production code. See
`docs/contracts/bracket-api.md` for the canonical wire-shape
spec.

### Three-renderer trend gate

`scripts/append-dist-size.js` records the W9 row with
`three-renderer-big = 507,474 B` (the K9 wave entry in
`src/frontend/autotable-src/dist-size.json`). The W9 trend
constraint Vasquez ships
(`tests/e2e/three-renderer-510-hard.spec.ts`, forward-staged)
will pass on next CI run once the spec file lands and the
backend serves `dist-size.json` at one of the canonical paths.

---

## Wave 10 additions (Phase K W10 — 2025-Q4)

### Commentary `TileReference` shape — `data-tile-suit` / `data-tile-rank` attrs

W10 lands the canonical `TileReference = { tileId, suit, rank }`
object shape in `tileReferences[]` on commentary records.
`src/commentary-panel.ts:renderTileRef()` reads `ref.suit` +
`ref.rank` and emits them as data attributes on the chip:

```html
<button
  class="commentary-tile-chip"
  data-tile-id="m5"
  data-tile-suit="man"
  data-tile-rank="5"
>5m</button>
```

Spec hooks (Playwright + unit):

- `[data-tile-suit="man"][data-tile-rank="5"]` — selects a specific
  tile chip.
- `[data-tile-id="p3-red"]` — selects a tile chip by identity,
  variant-suffix aware.

A W9 wire-shape (bare string) is coerced via `parseTileIdShape()`
to the same DOM; tests can assert the coercion happened by
checking `console.warn` for the parse-warning. See
`docs/contracts/commentary-tile-ref.md` for the migration
discipline (W9 → W10 → W12 cleanup).

### `mahjong:highlight-tile` event — `source: 'commentary-panel'`

The chip's click handler dispatches:

```ts
new CustomEvent('mahjong:highlight-tile', {
  detail: { tileId: 'm5', source: 'commentary-panel' },
});
```

Spec hooks:

- Listen on `document` for `mahjong:highlight-tile`; assert
  `event.detail.source === 'commentary-panel'`.
- `event.detail.tileId` must equal the chip's `data-tile-id`.
- `scene-effects.ts:wireHighlightHandlers` is the production
  consumer; the W10 strip removes the fallback ring so unknown
  sources `console.warn` instead of silently pulsing.

### Three-renderer trend gate (W10 update)

`src/frontend/autotable-src/dist-size.json` carries a K10 row
with `three-renderer-big = 497,440 B` (−10,034 B vs K9). The
W7 monotonic-decrease invariant holds for a 5th consecutive
wave. The W9 forward-staged spec
(`tests/e2e/three-renderer-510-hard.spec.ts`) and any future
W10 spec (e.g. `three-renderer-500-hard.spec.ts`) both pass
against this row.

### PWA Builder workflow — testid surface

The W10 CI workflow (`.github/workflows/pwa-audit.yml`) doesn't
add DOM selectors, but it produces two CI-only JSON artifacts
that downstream PR-comment renderers + tests can read:

- `.pwa-score.json` — `{ pwaScore: number, subScores: { ... } }`
  from `scripts/manifest-lint.js`.
- `.lighthouse-report.json` — LH13 full output (`audits[]`,
  `categories[]`).

Both are `.gitignore`d; specs that need them should regenerate
locally via the recipes in `docs/frontend-pwa-audit.md §2`.

### Build cache — `.vite/` directory

`vite.config.ts:cacheDir` points at
`src/frontend/autotable-src/.vite/`. Specs that exercise a
cold-build path should set the `VITE_FORCE_DEP_PRE_BUNDLE`
env var or remove the directory before invoking the build.
This is a CI / harness concern only; no DOM surface.

## Phase K Wave 10 — Vasquez Playwright additions (W10 spec inventory)

The W10 wave adds six chromium-only, forward-stage-tolerant
specs that pin the surfaces Hicks documented in the W10 footer
above. Each spec follows the W9 selectors-inventory pattern:
soft-pass when the surface isn't observable, hard-assert when it
is. Together they raise the E2E count from ~20 (W9) to ~26 (W10).

### Spec inventory (all chromium-only, all forward-stage tolerant)

| Spec | Asserts | Soft-pass condition |
|------|---------|---------------------|
| `three-renderer-480-hard.spec.ts` | `dist-size.json` K10 entry ≤ 480 KB (with W9 510 KB regression backstop) | no K10 entry yet OR mid-strip between 480 KB and 510 KB |
| `commentary-dispatch.spec.ts` | click on `data-testid="commentary-tileref-<row>-<idx>"` dispatches `mahjong:highlight-tile` with `detail.tileId` set | autotable shell or commentary tile-refs not in DOM |
| `pwa-audit-workflow.spec.ts` | `.github/workflows/pwa-audit.yml` declares `name: PWA*`, `on: pull_request`, and an `audit`/`pwa-audit`/`lighthouse` job | workflow not mirror-served by dev server |
| `manifest-fields.spec.ts` | `manifest.webmanifest` carries description (≥ 30 chars), categories[], screenshots[] (well-shaped), shortcuts[] | manifest unreachable or individual fields not yet authored |
| `bracket-canonical-no-fallback.spec.ts` | unknown bracket kind triggers `data-testid="bracket-renderer-error"` containing `unknown bracket`; valid single-elim renders `round-heading` testids | `bracket-demo` route or `window.mahjongBracketRenderer` missing |
| `redis-idempotency-replay.spec.ts` | POST `/api/games` with same `Idempotency-Key` + same payload returns identical body; with same key + DIFFERENT payload returns 409 (or 422 collapse) | endpoint unreachable, requires auth, or replay-conflict not yet enforced |

### W10 testid additions (Vasquez side, mirrored from W10 footer)

| Testid | Owner | Producer (Hicks) | Consumer (Vasquez spec) |
|--------|-------|------------------|--------------------------|
| `commentary-tileref-<row>-<idx>` | Hicks | `commentary-panel.ts` | `commentary-dispatch.spec.ts` |
| `bracket-renderer-root` | Hicks | `bracket-renderer.ts` | `bracket-canonical-no-fallback.spec.ts` |
| `bracket-renderer-error` | Hicks | `bracket-renderer.ts` (W10 strict mode) | `bracket-canonical-no-fallback.spec.ts` |
| `round-heading-<n>` | Hicks | `bracket-renderer.ts` | `bracket-canonical-no-fallback.spec.ts` |

### W10 DOM-event additions

| Event | Direction | Owner | Consumer |
|-------|-----------|-------|----------|
| `mahjong:highlight-tile` (with `detail.source === 'commentary-panel'`) | document → scene-shell | Hicks (commentary-panel) | `commentary-dispatch.spec.ts` (round-trip check) |

### Cross-pane references (backend contract test pins)

The W10 Playwright specs each have a backend contract-test
counterpart under
`src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W10/Vasquez/`
so the surface is double-pinned: at the API/file/reflection layer
(backend xunit) and at the rendered-DOM/HTTP layer (Playwright).
This is the W10 "double-pin" convention — see
`docs/test-architecture.md §4.2` for the gap-analysis rationale.

| Playwright spec | Backend xunit pin |
|------------------|--------------------|
| `commentary-dispatch.spec.ts` | `HicksW10FrontendContractTests.CommentaryTileRef_*` |
| `three-renderer-480-hard.spec.ts` | `HicksW10FrontendContractTests.ThreeRendererBig_*` |
| `pwa-audit-workflow.spec.ts` | `HicksW10FrontendContractTests.PwaAuditWorkflow_*` |
| `manifest-fields.spec.ts` | `HicksW10FrontendContractTests.ManifestFields_*` |
| `bracket-canonical-no-fallback.spec.ts` | `HicksW10FrontendContractTests.BracketRenderer_CanonicalShape_*` |
| `redis-idempotency-replay.spec.ts` | `BishopW10RedisIdempotencyClientTests.*` |

Hicks: when you wire any of the producer-side testids above (or
move them to a new location), append the canonical citation here
per the W5 maintenance note. Vasquez will roll those edits into
the next-wave footer.

---

*Phase K Wave 10 — Vasquez (QA). Footer added in W10 as the
spec inventory + cross-pane mapping mirroring the W7/W8/W9
pattern.*

---

## Phase K Wave 11 — Hicks footer (frontend producer side)

W11 introduces the `?action=*` PWA shortcut router and three
new author-time artefacts (real PWA screenshots, the cache-
effectiveness metric, and the LH13 baseline calibration).
This footer mirrors the W10 pattern: the producer-side
selector / event / file-path contract that Vasquez's specs
consume.

### W11 selector additions

| Selector | Owner | Producer (Hicks) | Used by |
|----------|-------|------------------|---------|
| `[data-action="new-game"]` | Hicks | `index.html` (#new-game button) | `action-router.ts` `dispatchNewGame()`; Vasquez's `?action=new-game` spec probe |
| `#lobby-public-games-tab` | Hicks | `lobby-app.ts` (W8) | `action-router.ts` `dispatchSpectate()` |
| `#lobby-tournaments-tab` | Hicks | `lobby-app.ts` (W8) | `action-router.ts` `dispatchTournament()` |

The `data-action` attribute style is the W11-preferred pattern
for new router hooks (more test-friendly than mixing CSS-styling
IDs with behavioural lookups). Existing IDs are kept for
selector stability.

### W11 module-surface additions

`src/action-router.ts` exports three functions (full contract
in `docs/frontend-routing.md`):

| Export | Signature | Behaviour |
|--------|-----------|-----------|
| `parseActionFromUrl` | `() => string \| null` | Returns canonical action keyword or `null`. Pure parse — never mutates URL. |
| `clearActionParam` | `() => void` | `history.replaceState` strips `action=` only. |
| `handlePwaActionFromUrl` | `() => boolean` | Top-level dispatch. Returns `true` if action handled (caller skips game-bootstrap). |

### W11 boot-sequence ordering invariant

`src/index.ts` MUST call `handlePwaActionFromUrl()` BEFORE the
W2 game-bootstrap guard fires. If reorganising `index.ts`, keep
the action-router call immediately after the eager lobby
import and before any `if (window.location.search) …` clause.
Vasquez's W11 spec asserts this by probing
`?action=new-game&game=fake-id` and verifying the renderer
chunk is **not** fetched.

### W11 manifest schema (producer)

Vasquez's `manifest-fields.spec.ts` (W10) is extended in W11 to
also assert:

- `shortcuts[]` has at least 3 entries.
- Every `shortcuts[].url` value with `?action=*` uses a keyword
  in `docs/frontend-routing.md §3`.
- `screenshots[].src` resolves to `screenshots/*.png` (not the
  W10 `img/screenshot-*.auto.png` placeholders).
- `screenshots[]` has at least one `form_factor: 'wide'` and
  one `form_factor: 'narrow'` entry.

### W11 file-path artefacts

| Path | Producer (Hicks) | Asserted by |
|------|------------------|--------------|
| `static/screenshots/main-game.png` | `scripts/capture-screenshots.js` (W11) | Vasquez's manifest spec (path resolution) |
| `static/screenshots/spectator-commentary.png` | same | same |
| `static/screenshots/tournament-dashboard.png` | same | same |
| `dist/screenshots/*.png` | `vite.config.ts:copyStaticAssets` (W11 update) | Build verification (file existence) |
| `.vite-cache-metric.json` | `scripts/build-with-cache-metric.js` (W11) | Local + CI cache-effectiveness gate |
| `.github/workflows/pwa-builder.yml` | Hicks (W11 NEW) | Backend contract test (file-existence pin, similar to W10 `pwa-audit.yml` pin) |

### W11 budget gate

`dist-size.json` K11 row pins:
- `three-renderer-big` = 466,395 B (`<` 475,000 B target ✅)
- `autotable-src-eager` = 216,262 B (no change vs W10)

Vasquez's `three-renderer-480-hard.spec.ts` continues to gate at
< 480 kB hard / < 475 kB soft (W10 thresholds unchanged); the
W11 strip leaves a 9 kB margin under the soft gate.

### W11 LH13 baseline reference

`docs/frontend-pwa-audit.md §7` (Wave 11) is the canonical
LH13 calibration table. CI thresholds in
`.github/workflows/pwa-audit.yml` should reference that table
verbatim. Re-calibration cadence + procedure documented in the
same section.

### Cross-pane references (W11 specs ↔ backend pins)

| Playwright spec | Backend xunit pin (expected) |
|------------------|--------------------------------|
| `action-router.spec.ts` | `HicksW11FrontendContractTests.ActionRouter_*` |
| `manifest-shortcuts-w11.spec.ts` | `HicksW11FrontendContractTests.ManifestShortcuts_*` |
| `pwa-builder-workflow.spec.ts` | `HicksW11FrontendContractTests.PwaBuilderWorkflow_*` |
| `three-renderer-475-soft.spec.ts` | `HicksW11FrontendContractTests.ThreeRendererBig_W11Soft_*` |

(The exact spec / xunit names are placeholders for Vasquez's
W11 inventory — listed here as the producer-side expectation.)

Hicks: future producer-side renames of any of the above
selectors / module exports / file paths require a corresponding
edit to this footer per the W5 maintenance note.

### W11 Vasquez QA inventory (canonical spec names)

The Vasquez W11 QA lane adds six Playwright specs under
`src/frontend/autotable-src/tests/e2e/`. Each spec carries a
file-level header comment with the W11 surface it pins and a
forward-stage tolerance clause:

| Playwright spec (Vasquez W11)            | Target surface                                         |
|------------------------------------------|--------------------------------------------------------|
| `shader-chunk-475-hard.spec.ts`          | `three-renderer-big` ≤ 475 KB (W11 hard cap)            |
| `pwa-builder-platforms.spec.ts`          | Edge / Chrome / Safari PWA Builder score ≥ 75           |
| `lh13-baseline-calibration.spec.ts`      | 3-run LH13 calibration; p95 ≥ 95 + worst-of-3 ≥ 90      |
| `cache-hit-rate.spec.ts`                 | Vite persistent-cache hit rate ≥ 70%                    |
| `manifest-screenshots-real.spec.ts`      | Manifest `screenshots[]` resolve + PNG dimensions match |
| `deep-link-action-routing.spec.ts`       | `?action=new-game|tournaments|history|admin` routing    |

These specs are the canonical W11 frontend gates. Hicks's
producer-side renames of any pinned chunk / manifest field / URL
shape MUST land with a corresponding spec edit.

---

*Phase K Wave 11 — Hicks (Frontend). Footer added in W11 as
the producer-side mirror of the W7/W8/W9/W10 pattern.*

*Phase K Wave 11 — Vasquez (QA). Vasquez W11 QA inventory
appended above with the 6 canonical Playwright spec names that
pin the W11 frontend surfaces.*

---

## W12 producer-side updates (Hicks)

### `?action=replay` co-parameter contract (new)

W12 cashed in the `?action=replay` reservation from
`docs/frontend-routing.md §7`. The router now accepts the
`replay` keyword as a fourth SUPPORTED_ACTION alongside
`new-game` / `tournaments` / `history` / `admin`.

**URL shape pinned:**

```
?action=replay&replayId=<guid>
```

`replayId` is a co-parameter on the same URLSearchParams.
Both `action` and `replayId` are stripped from the URL before
the dispatch + fetch (refresh-safe — re-loading the rewritten
URL does NOT re-trigger the deep link).

**Endpoint pinned:**

`GET /api/replays/{replayId}` (Bishop W12 — id-addressable,
NOT the legacy `/api/games/{gameId}/replay` shape). No
fallback to the game-id endpoint — would mask config drift.

**Success path:**

1. URL rewritten to `/replay/{replayId}` via
   `history.replaceState()`.
2. Lazy-import of `./replay-launcher` resolves
   `openReplayPayload(replayId, body, options?)` — a NEW
   exported function added in W12.
3. Toast `replay.opening` is fired (via the existing
   `./toast` helper).

**Failure path:**

ANY failure (HTTP 404 / 5xx / network / JSON-parse / missing
replayId co-param) → "Replay not found" toast via
`showToast(msg, 'error')` from `./toast`. URL is still
cleared, but no `/replay/*` rewrite happens.

**Pinned exports (do NOT rename without spec edits):**

| Module | Export | Signature |
|--------|--------|-----------|
| `src/action-router.ts` | `SUPPORTED_ACTIONS` (incl. `'replay'`) | `readonly Set<string>` |
| `src/action-router.ts` | `dispatchReplay(replayId)` | private (transitively called from `handlePwaActionFromUrl`) |
| `src/replay-launcher.ts` | `openReplayPayload(id, body, opts?)` | `(string, ServerReplayPayload, { announce?: boolean }) => Promise<void>` |

Vasquez W12 producer-side expectation: a Playwright spec
under `tests/e2e/` named `deep-link-action-replay.spec.ts`
that pins:

- The URL shape (`?action=replay&replayId=<guid>`)
- The `/api/replays/{id}` endpoint contract (intercept +
  mock response with the canonical ServerReplayPayload shape
  from `src/types/replay.ts`)
- The URL-rewrite to `/replay/<id>` post-dispatch
- The error-toast firing on a 404 mock

### Three-renderer K12 dist-size pin (new)

`dist-size.json:history[wave=K12]` records the W12 build
sizes. The pinned budget for Vasquez's W12 QA gates:

| Bundle | W11 (K11) | W12 (K12) | W12 budget |
|--------|-----------|-----------|------------|
| `three-renderer-big` | 466,395 B | **448,648 B** | **< 450 KB stretch** ✅ / < 460 KB acceptable |

Vasquez W12 producer-side expectation: a Playwright spec
`three-renderer-450-stretch.spec.ts` (parallel to W11's
`three-renderer-475-soft.spec.ts`) that fetches
`dist-size.json` and asserts the K12 row meets the new
budget. The W11 hard-cap spec
(`shader-chunk-475-hard.spec.ts`) should keep passing
trivially (448 < 475).

### Placeholder screenshot retirement (W10 → retired in W12)

The W10 placeholder screenshot copy-loop in
`vite.config.ts` has been removed. The legacy paths

- `src/frontend/autotable-src/img/screenshot-lobby.auto.png`
- `src/frontend/autotable-src/img/screenshot-table.auto.png`
- `src/frontend/autotable-src/img/screenshot-mobile.auto.png`

have been `git rm`'d. The PWA manifest already pointed only
at the W11 real captures at `screenshots/*.png` — those URLs
are unchanged. Vasquez: drop any old `expect`s in
`manifest-screenshots-real.spec.ts` (or any spec) that
asserted the existence of the `.auto.png` paths.

### ShaderChunk + UniformsLib strips (W12 extends W11)

W12 extends the W11 `stripUnusedShaderChunks` plugin with 10
more chunks (`shadowmap_*`, `shadowmask_pars_fragment`,
`envmap_*` x6) and adds a NEW `stripUnusedUniformsLib`
plugin that empties 5 unused registry entries
(`roughnessmap`, `metalnessmap`, `gradientmap`, `points`,
`sprite`). Implementation lives in `vite.config.ts` (the
`SHADER_CHUNKS_TO_EMPTY` and `UNIFORMS_LIB_KEYS_TO_EMPTY`
arrays + the new plugin function). Per the W11 pattern:
producer-side strip changes do NOT have a spec-level pin —
only the resulting `three-renderer-big` size does. The
`three-renderer-450-stretch.spec.ts` covers it.

---

*Phase K Wave 12 — Hicks (Frontend). W12 footer appended
with the `?action=replay` co-parameter contract, K12
dist-size pin, W10 placeholder retirement note, and W12
strip extension producer-side expectations.*

---

## W12 QA spec map (Vasquez)

Six new Playwright specs landed under
`src/frontend/autotable-src/tests/e2e/` in the Vasquez W12
lane. Each name → pinned surface → forward-stage stance:

| Spec file | Pinned surface | Forward-stage stance |
|---|---|---|
| `replay-deep-link.spec.ts` | `?action=replay&replayId=<id>` router branch (lobby fallback + 404 toast) | tolerant — annotates when the action branch isn't wired |
| `shader-chunk-450-stretch.spec.ts` | `dist-size.json` `history[wave=K12].chunks.three-renderer-big`; stretch <450 KB, acceptance <460 KB | tolerant — falls back to K11 entry + W11 backstop (<475 KB) |
| `lh13-thresholds-pinned.spec.ts` | LH13 threshold values (0.85 / 0.80 / 0.90 / 0.80) per W11 §7 — SOFT-pinned in W12 per `docs/frontend-pwa-audit.md §6.1`, hard-pin deferred to W13 | tolerant — annotates on per-category mismatch; absolute sanity bound only |
| `oauth-introspect-rate-limit.spec.ts` | `POST /api/oauth/introspect` 60s/100 bucket; 101st = 429 + `Retry-After` | tolerant — annotates when endpoint 404 or middleware not wired |
| `manifest-screenshots-visual.spec.ts` | Per-screenshot visual diff using `toHaveScreenshot({ maxDiffPixelRatio: 0.02 })` per `docs/test-architecture.md §5` | tolerant — first run records baseline; subsequent runs hard-compare |
| `spectator-handoff-token.spec.ts` | `POST /api/spectator/handoff` returns JWT with `role=spectator`, `exp ≈ now + 300s`, echoed `tableId` | tolerant — annotates on 404/401/missing claim |

All six specs are chromium-only (per the W11 lane convention)
and call `test.skip(testInfo.project.name !== 'chromium', …)`.

The `replay-deep-link.spec.ts` row pairs with Hicks's W12
producer-side `?action=replay` co-parameter contract noted
above and with Bishop's `BishopW12ReplayByIdEndpointTests`
backend mirror under
`src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W12/Vasquez/`.

The `manifest-screenshots-visual.spec.ts` row is the first
Playwright spec in the repo to use the §5 visual-regression
pre-flight (animations frozen, fonts loaded, viewport pinned)
and is documented as the W12 reference template for future
visual diffs.

---

*Phase K Wave 12 — Vasquez (QA). W12 footer appended with the
QA-lane spec map for the six new Playwright specs (replay
deep-link, shader-chunk-450 stretch, LH13 soft-pin, OAuth
introspect rate-limit, manifest screenshots visual, spectator
handoff token). Pairs with Hicks's W12 producer-side footer
above and with the backend mirror tests under
`Phase_K_W12/Vasquez/`.*


## Phase K Wave 13 — Hicks footer (frontend producer side)

This W13 footer documents the new selectors and producer-side
contracts added by Hicks in Wave 13. Per `shared_files`
policy, Hicks owns the producer side of these selectors;
Vasquez owns the consumer-side spec inventory in the W13
Vasquez footer (appended separately).

### `?action=spectate&gameId=<guid>` deep link (new W13)

The W11 `?action=spectate` shortcut (lobby-tab activation)
gains a W13 co-parameter form that bypasses the lobby and
drops the viewer straight into a named table:

\`\`\`
/?action=spectate&gameId=<guid>
\`\`\`

Producer-side surface added in `src/action-router.ts`:

| Symbol | Kind | Purpose |
|--------|------|---------|
| `dispatchSpectateWithGameId(gameId)` | function | W13 entry point; calls `fetchHandoffAndOpenSpectator`. |
| `fetchHandoffAndOpenSpectator(gameId)` | function | POSTs `/api/spectator/handoff`; on 200 navigates to `/spectate/<id>?token=<jwt>#/spectate/<id>` and calls `openSpectatorLivestream({tableId})`; on 401 redirects to `/`; on 404/error fires a toast. |
| `redirectToLobbyForSignIn()` | function | Forces a navigation to `/` so `installAuthUi()` mounts the sign-in modal at boot. |
| `showGameNotFoundToast()` | function | Transient toast surface for the 404 / network-error path. |

Vasquez W13 producer-side expectation: a Playwright spec
under `tests/e2e/` named
`deep-link-action-spectate-gameid.spec.ts` that pins:

- The URL shape (`?action=spectate&gameId=<guid>`)
- The `/api/spectator/handoff` endpoint contract (intercept
  + mock response with `{token, expiresAt, scope, ttlSeconds}`)
- The URL-rewrite to `/spectate/<id>?token=<jwt>#/spectate/<id>`
  post-dispatch
- The 401 → `/` redirect path
- The 404 / network → toast path

The W11 `?action=spectate` bare-keyword behaviour (lobby-tab
activation) is unchanged; the W13 spec should pin both code
paths via two scenarios.

### Three-renderer K13 dist-size pin (new)

`dist-size.json:history[wave=K13]` records the W13 build
sizes. The pinned budget for Vasquez W13 QA gates:

| Bundle | W12 (K12) | W13 (K13) | W13 budget |
|--------|-----------|-----------|------------|
| `three-renderer-big` | 448,648 B | **406,635 B** | **< 440 KB stretch** ✅ (~34 KB margin) |

Vasquez W13 producer-side expectation: a Playwright spec
`three-renderer-440-stretch.spec.ts` (parallel to W12
`three-renderer-450-stretch.spec.ts`) that fetches
`dist-size.json` and asserts the K13 row meets the new
budget. The W10 hard-cap spec
(`three-renderer-480-hard.spec.ts`) and W11 soft-pin
(`three-renderer-475-soft.spec.ts`) and W12 stretch
(`three-renderer-450-stretch.spec.ts`) all keep passing
trivially (406 < 440 < 450 < 475 < 480).

### ShaderChunk + UniformsLib strips (W13 extends W12)

W13 extends the W12 `stripUnusedShaderChunks` plugin from 11
to 53 chunks (adds `tonemapping_*`, `lights_phong_*`,
`lights_toon_*`, `lights_physical_*`, `transmission_*`,
`iridescence_*`, `clearcoat_*` partials,
`dithering_*`, `premultiplied_alpha_fragment`, every
map-feature `_fragment` / `_pars_fragment` chain
(alphamap/alphahash/alphatest/aomap/lightmap/emissivemap/
bumpmap/normalmap/specularmap-pars-only/metalnessmap/
roughnessmap/displacementmap), `fog_*`). The
`stripUnusedUniformsLib` plugin extends from 5 to 14
entries (adds `specularmap`, `envmap`, `aomap`, `lightmap`,
`bumpmap`, `normalmap`, `displacementmap`, `emissivemap`,
`fog`). Implementation lives in `vite.config.ts`. Per the
W11 pattern: producer-side strip changes do NOT have a
spec-level pin — only the resulting `three-renderer-big`
size does. The `three-renderer-440-stretch.spec.ts` covers
it.

### Visual-regression baselines (new W13)

W13 captures the three manifest screenshot baselines for
Vasquez W12 spec at the user-specified Jest-style location:

\`\`\`
tests/e2e/__screenshots__/manifest-screenshots-visual.spec.ts/
- main-game.png
- spectator-commentary.png
- tournament-dashboard.png
\`\`\`

Each is 1280x720 PNG, ~25-40 kB, captured by the new
`scripts/capture-visual-baselines.js` Playwright-runtime
side-channel script (workaround for the W12 spec
setContent-without-goto bug documented in
`docs/frontend-pwa-audit.md §11`).

Vasquez W14 follow-up (out-of-lane for W13 Hicks): fix the
spec to add `page.goto(BASE_URL)` before `setContent()` and
add `snapshotPathTemplate` to `playwright.config.ts` so the
spec reads from the Jest-style location the W13 baselines
were captured at.

### bundle-health CI workflow (new W13)

W13 ships `.github/workflows/bundle-health.yml` — a per-PR
auto-report that builds the frontend, parses
`dist-size.json`, posts a sticky PR comment with the
`three-renderer-big` size + delta vs the W12 baseline
(448,648 B), and hard-fails only on >500 KB. Sticky-marker
selector: `<!-- bundle-health-report -->`. The workflow
tags its `dist-size.json` row with `WAVE_NAME=PR-<n>` so
per-PR runs do not mutate the canonical `K13` / `K14` / ...
rows. Per the W11 pattern: workflow shippings do NOT have a
spec-level pin — they are CI-only. The workflow file is the
contract.

---

*Phase K Wave 13 — Hicks (Frontend). W13 footer appended
with the `?action=spectate&gameId` co-parameter contract,
the K13 three-renderer dist-size pin, the deeper
shader-chunk + UniformsLib strip, the visual-regression
baselines, and the bundle-health.yml CI workflow. Pairs
with the W13 Vasquez QA footer (appended later by Vasquez
in her own commit).*

---

## Phase K Wave 13 — Vasquez (QA) footer

Six new Playwright specs land in this wave under
`src/frontend/autotable-src/tests/e2e/`. All chromium-only,
all forward-stage tolerant via `testInfo.annotations.push({
type: 'forward-stage', … })` when surfaces are still
converging.

| # | Spec | Surface pinned |
|---|------|----------------|
| 1 | `spectate-deep-link.spec.ts` | Hicks W13 spectate deep-link action router (`?action=spectate`, `/spectate?room=…`, `/spectate/{room}`); HTML retains the room id on the URL. |
| 2 | `shader-chunk-440-stretch.spec.ts` | Tighter stretch goal vs. the W12 450-byte spec; tracks the shader chunk towards ≤ 440 B without hard-failing (until the goal is met). |
| 3 | `lh13-thresholds-hard-pinned.spec.ts` | LH13 **hard-pin** at the W11 §7 calibration values (perf 0.85 / a11y 0.80 / best-practices 0.90 / seo 0.80) — the W12 soft-pin replacement per `docs/frontend-pwa-audit.md` §6.2. |
| 4 | `bracket-tournament-integration.spec.ts` | Bishop W13 `TournamentService.AdvanceMatchAsync` + `EfBracketStore` integration surface; admin tournaments shell renders bracket / tournament / advance / match noun. |
| 5 | `commentary-cost-warning-toast.spec.ts` | Bishop W13 `CommentaryCostAdminHub` SignalR + frontend warning toast (`[data-testid="commentary-cost-toast"]`, `.toast.commentary-cost`, `[role="status"]`). |
| 6 | `bundle-health-pr-comment.spec.ts` | Hicks W13 bundle-health.yml CI surface — preview `bundle-health.json` / `dist-size.json` / `.bundle-health/report.json` are observable when the workflow is wired. |

### Shared-file authorship pin

`tests/selectors.md` is a `shared_files.selectors_md_shared`
entry in `tests/ci/lane-map.json` (authors: `hicks` +
`vasquez`, primary: `vasquez`). The W13 Vasquez footer
appended here pairs with the W13 Hicks footer above and is
covered by the same shared-file lane-discipline rule.

---

*Phase K Wave 13 — Vasquez (QA). Six Playwright specs land
under `tests/e2e/` and are inventoried above. The matching
backend mirror tests live under
`src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W13/Vasquez/`.*

---

## Phase K Wave 14 — Hicks (Frontend) footer

W14 wires three new deep-link action keywords backed by
Bishop's W14 listing endpoints. Each surface ships its own
lazy chunk + DOM overlay; selectors are documented here so
Vasquez's W14+ specs can wire stable testids before the
overlays land on `main`.

### W14 action keywords contract

| Keyword | Co-param | Endpoint | Surface module |
|---------|----------|----------|----------------|
| `bracket` | `tournamentId=<guid>` | `GET /api/tournaments/{id}/brackets` | `src/bracket-listing.ts` |
| `replays` | — | `GET /api/replays` | `src/replays-listing.ts` |
| `admin-cost` | — | `GET /api/commentary/cost/summary` (admin-gated; pre-flighted via `GET /api/auth/me`) | `src/admin-cost.ts` |

Full router contract: `docs/frontend-routing.md` §3 table +
§3.2 (`bracket`), §3.3 (`replays`), §3.4 (`admin-cost`).

### W14 selector inventory

#### `bracket-listing` (W14, Hicks)

| Selector | Element | Notes |
|----------|---------|-------|
| `data-testid="bracket-listing-overlay"` | Root fixed-position overlay div | Mounted to `document.body` on dispatch; click outside closes. |
| `data-testid="bracket-listing-card"` | Inner card panel | Sits inside the overlay; carries the title + close button + grid. |
| `data-testid="bracket-listing-close"` | Close button | Top-right; also triggered by ESC. |
| `data-testid="bracket-listing-grid"` | Rounds grid container | CSS-grid layout; one column per round. |
| `data-testid="bracket-listing-round-{n}"` | Per-round column | `{n}` is 1-indexed `roundNumber`. |
| `data-testid="bracket-listing-match"` | Per-match card | One per `BracketRecord`. |
| `data-testid="bracket-listing-player-a"` | Player A name / seed | Receives `.bracket-listing-winner` class when `winnerSeed === seedA`. |
| `data-testid="bracket-listing-player-b"` | Player B name / seed | Receives `.bracket-listing-winner` class when `winnerSeed === seedB`. |
| `data-testid="bracket-listing-status"` | Status badge | Renders `completed` / `in-progress` / `pending` text + class. |
| `data-testid="bracket-listing-empty"` | Empty-state placeholder | Renders when `brackets` array is empty / null / parse-failure. |
| `data-testid="bracket-listing-loading"` | Loading-state placeholder | Shown until the fetch resolves. |

#### `replays-listing` (W14, Hicks)

| Selector | Element | Notes |
|----------|---------|-------|
| `data-testid="replays-listing-overlay"` | Root overlay div | Same mounting pattern as bracket-listing. |
| `data-testid="replays-listing-card"` | Inner card panel | Title + close + table. |
| `data-testid="replays-listing-close"` | Close button | Top-right + ESC. |
| `data-testid="replays-listing-table"` | Table element | `<table>`, sorted by `completedAt` desc. |
| `data-testid="replays-listing-row"` | Per-replay row | One per `ReplayMeta`. |
| `data-testid="replays-listing-open"` | Open-replay link | `<a>` with `href="?action=replay&replayId=<id>"`. |
| `data-testid="replays-listing-empty"` | Empty-state | Renders when `replays` array is empty / null / parse-failure. |
| `data-testid="replays-listing-loading"` | Loading-state | Shown until the fetch resolves. |

#### `admin-cost` (W14, Hicks)

| Selector | Element | Notes |
|----------|---------|-------|
| `data-testid="admin-cost-overlay"` | Root overlay div | |
| `data-testid="admin-cost-card"` | Inner card panel | Summary + table. |
| `data-testid="admin-cost-close"` | Close button | |
| `data-testid="admin-cost-summary"` | Summary card container | Wraps current / cap / percent. |
| `data-testid="admin-cost-current"` | Current-month-cost value | Formatted as `$XX.XX`. |
| `data-testid="admin-cost-cap"` | Budget cap value | Formatted as `$XX.XX`. |
| `data-testid="admin-cost-percent"` | Percent-used value | Emits one of `admin-cost-pct-ok` / `admin-cost-pct-warn` / `admin-cost-pct-critical` class based on `<80` / `80-94` / `>=95`. |
| `data-testid="admin-cost-table"` | Per-model table | Sorted by `costUsd` desc. |
| `data-testid="admin-cost-model-row"` | Per-model row | One per `byModel` entry. |
| `data-testid="admin-cost-empty"` | Empty-state placeholder | Renders when `byModel` is empty / 403 / parse-failure. The 403 path renders the "Admins only" copy. |
| `data-testid="admin-cost-loading"` | Loading-state | Shown until the fetch resolves. |

### Defensive wire-shape posture (W14)

All three W14 overlays parse Bishop's wire shape
defensively (per `docs/frontend-routing.md` §3.2-§3.4):

* Top-level shape tolerated as either `{ brackets|replays|byModel: [...] }` OR a bare `[...]`.
* Per-row aliases tolerated (e.g. `id` for `replayId`,
  `completedAtUtc` for `completedAt`).
* `percentUsed` tolerates both `0-100` integer and `0-1`
  fractional ranges.
* Player names tolerate both bare strings and
  `{ displayName: '...' }` objects.

If Bishop's W14 endpoints emit a shape we don't recognise,
the overlays render the `*-empty` placeholder rather than
crashing. Vasquez's W14+ specs should assert this graceful-
drift behaviour explicitly.

### Shared-file authorship pin

`tests/selectors.md` remains a `shared_files.selectors_md_shared`
entry in `tests/ci/lane-map.json` (authors: `hicks` +
`vasquez`, primary: `vasquez`). The W14 Hicks footer
appended here covers only the W14 frontend surfaces; the
W14 Vasquez QA footer (if any) is hers to land.

---

*Phase K Wave 14 — Hicks (Frontend). W14 footer appended
with the three new action keywords (`bracket` / `replays` /
`admin-cost`) + their selector inventories. Pairs with the
W14 Hicks deliverables in `Phase_K_W14/Hicks/`.*

---

## Phase K Wave 14 — Vasquez (QA) footer

Six new Playwright specs land in this wave under
`src/frontend/autotable-src/tests/e2e/`. All chromium-only,
all forward-stage tolerant via `testInfo.annotations.push({
type: 'forward-stage', … })` when surfaces are still
converging. Each spec pairs with a Vasquez backend mirror in
`src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W14/Vasquez/`.

| # | Spec | Surface pinned |
|---|------|----------------|
| 1 | `bracket-ui-route.spec.ts` | Hicks W14 `?action=bracket&tournamentId=…` deep-link route; renders without throwing or forward-stages when not yet wired. Pairs with `HicksW14BracketUiRouteTests.cs`. |
| 2 | `replay-listing-route.spec.ts` | Hicks W14 `?action=replays` deep-link route; renders without throwing or forward-stages. Pairs with `HicksW14ReplayListingUiTests.cs`. |
| 3 | `commentary-cost-admin-panel.spec.ts` | Hicks W14 `?action=admin-cost` admin-only panel route; pairs with `HicksW14CommentaryCostAdminPanelTests.cs` + the Bishop `CommentaryCostController`. |
| 4 | `visual-regression-real-captures.spec.ts` | Real-capture visual regression at the 2% pixel-diff tolerance across canonical app routes; first runs record baselines, missing origins annotate. Extends `manifest-screenshots-visual.spec.ts` with live-origin captures. |
| 5 | `lh13-thresholds-hard-pinned-final.spec.ts` | LH13 FINAL hard-pin at the W11 §7 calibration values — the consumer-side hard-pin gate per `docs/frontend-pwa-audit.md §6.3`. Forward-staged today; flips to hard-assert once the cron converges. |
| 6 | `jwks-overlap-rollback-rejected.spec.ts` | JWKS rotation overlap-window protection: a rollback POST during overlap must return 4xx/5xx. Pairs with `BishopW14JwksOverlapRollbackTests.cs`. |

### Visual-regression spec fix (W14)

`manifest-screenshots-visual.spec.ts` (originally landed in W12)
got a W14 fix: `await page.goto('/')` is now called BEFORE
`page.setContent()` so the relative `<img src="/foo.png">` URLs
inside the content resolve against the baseURL origin instead of
`about:blank`. See `docs/test-architecture.md §5.2` for the
methodology note. The spec retains its forward-stage tolerant
posture (origin-unreachable annotates and passes).

### Shared-file authorship pin

`tests/selectors.md` is a `shared_files.selectors_md_shared`
entry in `tests/ci/lane-map.json` (authors: `hicks` +
`vasquez`, primary: `vasquez`). The W14 Vasquez footer
appended here pairs with the W14 Hicks footer above and is
covered by the same shared-file lane-discipline rule.

---

*Phase K Wave 14 — Vasquez (QA). Six Playwright specs land
under `tests/e2e/` and are inventoried above. The matching
backend mirror tests live under
`src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W14/Vasquez/`.
The W12-era `manifest-screenshots-visual.spec.ts` was fixed
in this wave (page.goto before setContent) — see
`docs/test-architecture.md §5.2`.*

---

## Phase K Wave 15 — Hicks (Frontend) footer

W15 adds one new `?action=*` keyword + ships the foundation of
the Phase L renderer-webgl2 spike + finishes the W14
visual-regression alignment.  Selector inventories below.

### `?action=cost-forecast&days=<n>` overlay (W15)

The W15 admin-cost-forecast surface is wired by
`src/admin-cost-forecast.ts` and dispatched by
`src/action-router.ts:dispatchCostForecast()`.  Lazy chunk:
`admin-cost-forecast.<hash>.js` (W15 baseline 6,108 B).

| Selector | Owner | Surface |
|----------|-------|---------|
| `#admin-cost-forecast-overlay` | Hicks W15 | Root overlay element (role="dialog"). |
| `[data-testid="admin-cost-forecast-overlay"]` | Hicks W15 | Stable test hook on the root. |
| `[data-testid="admin-cost-forecast-card"]` | Hicks W15 | Happy-path summary card. |
| `[data-testid="admin-cost-forecast-loading"]` | Hicks W15 | Pre-fetch loading state. |
| `[data-testid="admin-cost-forecast-empty"]` | Hicks W15 | Placeholder card (400 / 403 / 404 / 5xx / network). |
| `[data-testid="admin-cost-forecast-summary"]` | Hicks W15 | Three-line summary wrapper. |
| `[data-testid="admin-cost-forecast-projected"]` | Hicks W15 | Projected month-end currency value (e.g. `$42.18`). |
| `[data-testid="admin-cost-forecast-confidence"]` | Hicks W15 | Confidence percentage with `strong / moderate / weak` band class. |
| `[data-testid="admin-cost-forecast-days"]` | Hicks W15 | Observed/window day pair (`<obs> / <window>`). |
| `[data-testid="admin-cost-forecast-close"]` | Hicks W15 | Close button. |

URL shape:
* Pre-dispatch: `/?action=cost-forecast&days=<n>` (admin-only;
  401 redirects to `/`).
* Post-dispatch: `/admin/commentary-cost/forecast` (path
  rewritten via `history.replaceState`; `action` + `days`
  stripped from the query string).

Default + clamp behaviour (in `admin-cost-forecast.ts:normaliseDays`):
* Missing / non-numeric `days` → `30`.
* `days < 1` → clamped to `1`.
* `days > 90` → clamped to `90` (Bishop W15 envelope).

Status-code surfaces:
* `400` → "Invalid forecast window — pick a value between 1 and 90 days."
* `401` → close overlay + `window.location.replace('/')`.
* `403` → "Admins only — this surface is gated to admin accounts."
* `404` → "Cost forecast not available."
* `5xx` / network → "Cost forecast unavailable."

### `?renderer=webgl2-hello` Phase L spike entry (W15)

The W15 hand-rolled WebGL2 hello-world is gated behind
`?renderer=webgl2-hello` so it never loads on the lobby cold
path.  Source lives under `src/renderer-webgl2/`; chunk emits
as `renderer-webgl2.<hash>.js` (W15 baseline 6,237 B).

| Selector | Owner | Surface |
|----------|-------|---------|
| `#webgl2-hello-container` | Hicks W15 | Fixed-position root overlay (z-index 99999). |
| `[data-testid="webgl2-hello-container"]` | Hicks W15 | Container test hook. |
| `[data-testid="webgl2-hello-canvas"]` | Hicks W15 | 512×512 WebGL2 canvas where the textured quad lands. |
| `[data-testid="webgl2-hello-status"]` | Hicks W15 | Status line below the canvas (loading / OK / error). |

This is a DEV/SPIKE surface — not user-facing in production.
A future Phase L W1 spec may add a `?renderer=webgl2-hello`
smoke test that asserts the canvas paints something
non-uniform (texture-on-quad) at the supplied viewport, but
W15 deliberately ships ZERO Playwright spec coverage so the
spike doesn't conflate the renderer work with the test
suite.

### Visual-regression spec migration (W15)

`manifest-screenshots-visual.spec.ts` (originally W12) was
fully aligned to Playwright snapshot best-practice in W15:

* All `page.setContent()` calls REMOVED.  The spec now
  `page.goto()`s directly to each manifest screenshot URL.
* `playwright.config.ts` adds a `snapshotPathTemplate`
  pinning baselines under
  `__screenshots__/<spec-basename>/<arg>.png`.
* Forward-stage tolerance is preserved for origin-unreachable
  / no-manifest / empty-`screenshots[]` / asset-404 cases.

See `docs/frontend-pwa-audit.md §7.2` for the convention +
the §7.2.2 migration narrative.

### Defensive wire-shape posture (W15 cost-forecast)

`admin-cost-forecast.ts:parseForecast()` tolerates Bishop W15
wire-shape drift:

* `projectedMonthEndCostUsd` / `projectedCostUsd` /
  `projectedCost` — all three aliases accepted as the same
  semantic value.
* `daysOfData` / `daysWithData` — both accepted.
* `confidence` — both `0-100` integer AND `0-1` fractional
  ranges auto-detected (matches the `percentUsed` convention
  in `admin-cost.ts`).
* `currency` — defaults to `"USD"` when absent.

If Bishop's W15 endpoint emits a shape we don't recognise,
the overlay renders the `cost-forecast-empty` placeholder
rather than crashing.

### LH13 hard-pin status (W15)

Hicks W15 re-queried `gh run list -w pwa-audit.yml -L 30`
and observed **0 scheduled runs / 0 successful runs** — same
shape as W11/W12/W13/W14.  The five-wave cumulative deferral
is documented in `docs/frontend-pwa-audit.md §6.4 + §6.4.1`.
The §6.5 deadlock escalation (Stephen-direct manual trigger
seed) remains the recommended W16 unblock.

### Phase L renderer hello-world bundle math (W15)

`docs/phase-l-renderer-implementation.md` is the new W15
authoritative source for the Phase L bundle envelope.  W15
baseline: `renderer-webgl2.<hash>.js = 6,237 B` (3 % of the
180-220 KB Phase L envelope).  Three-renderer-big hold-line
held at 406,635 B for the fifth wave running (W13 → W14 →
W15).

### Shared-file authorship pin

`tests/selectors.md` remains a `shared_files.selectors_md_shared`
entry in `tests/ci/lane-map.json` (authors: `hicks` +
`vasquez`, primary: `vasquez`).  The W15 Hicks footer
appended here covers only the W15 frontend surfaces; the
W15 Vasquez QA footer (if any) is hers to land.

---

*Phase K Wave 15 — Hicks (Frontend). W15 footer appended
with the `cost-forecast` action keyword + the
`renderer-webgl2` Phase L spike + the manifest-screenshots-
visual snapshotPathTemplate migration.  Pairs with the W15
Hicks deliverables in `Phase_K_W15/Hicks/`.*

---

## Phase K Wave 15 — Vasquez (QA) footer

Six new Playwright specs land in this wave under
`src/frontend/autotable-src/tests/e2e/`. All chromium-only,
all forward-stage tolerant via `testInfo.annotations.push({
type: 'forward-stage', … })` when surfaces are still
converging. Each spec pairs with a Vasquez backend mirror in
`src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W15/Vasquez/`.

| # | Spec | Surface pinned |
|---|------|----------------|
| 1 | `replay-blob-streaming.spec.ts` | Bishop W15 replay blob `Range: bytes=…` chunked download — 206 Partial Content honoured, forward-stages when endpoint not yet reachable. Pairs with `BishopW15ReplayBlobStreamingTests.cs`. |
| 2 | `cost-forecast-route.spec.ts` | Hicks W15 `?action=cost-forecast&days=<n>` admin-only overlay; renders without throwing or forward-stages. Pairs with `HicksW15CostForecastRouteTests.cs` + Bishop's `CommentaryCostForecast` projection. |
| 3 | `phase-l-renderer-bundle.spec.ts` | Phase L hello-world spike: `dist-size.json` must include a `renderer-webgl2` entry inside the 180-220 KB envelope from `docs/phase-l-renderer-implementation.md`. Three-renderer-big hold-line (406,640 B) asserted by `HicksW15ThreeRendererHoldLineTests.cs`. |
| 4 | `lh13-thresholds-w15.spec.ts` | LH13 W15 soft-pin at the W11 §7 calibration values — five-wave cumulative deferral (YELLOW per `docs/frontend-pwa-audit.md §6.4`); §6.5 deadlock escalation in flight. Forward-staged today; flips to hard-assert once cron converges. |
| 5 | `snapshotPathTemplate.spec.ts` | Playwright config migration to the `snapshotPathTemplate` option (Hicks) so visual-regression captures land at deterministic per-browser paths. Pairs with `HicksW15SnapshotPathTemplateTests.cs`. |
| 6 | `bundle-audit-candidates.spec.ts` | Bundle-audit candidate inventory (Hicks + Apone) — audit doc must list ≥3 candidates. Pairs with `HicksW15BundleAuditCandidatesTests.cs`. |

### LH13 5-wave cumulative deferral (W15)

The LH13 hard-pin is now in its fifth consecutive deferral wave
(W11 → W15). Per `docs/frontend-pwa-audit.md §6.4` the cumulative
deferral is flagged **YELLOW**; a sixth deferral wave (W16) flips it
to **RED** and triggers Coordinator escalation. The §6.5 deadlock-
escalation recommendation (Stephen-direct manual `pwa-audit.yml`
trigger × 3) is in flight as of this wave.

### Shared-file authorship pin

`tests/selectors.md` is a `shared_files.selectors_md_shared`
entry in `tests/ci/lane-map.json` (authors: `hicks` +
`vasquez`, primary: `vasquez`). The W15 Vasquez footer
appended here pairs with the W15 Hicks footer above and is
covered by the same shared-file lane-discipline rule. The
W14 footer (above) inventories the prior wave's six specs and
remains in place for traceability.

---

*Phase K Wave 15 — Vasquez (QA). Six Playwright specs land
under `tests/e2e/` and are inventoried above. The matching
backend mirror tests live under
`src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W15/Vasquez/`.
The KW14→KW15 regression class rename and the LH13 5-wave
YELLOW flag land in this wave; see
`Phase_K_W15/Vasquez/vasquez-phase-k-wave-15.md`.*

---

## Playability gate (#122) — real-UI gameplay controls

Structural `id`/`class` selectors the P0 real-UI playability gate
(`tests/e2e/playability-gate.spec.ts` + `tests/e2e/_playability.ts`) and the
promoted walkthrough gate (`tests/e2e/playtest-changsha.spec.ts`) drive through
**real DOM/canvas/pointer interactions** (no WS backdoor). These are
HUD/control elements defined in static `index.html` markup (not `data-testid`),
catalogued here so the gate's contract is explicit and stable.

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `#main` | `<canvas>` | The Three.js WebGL canvas. Real tile discards are produced by `page.mouse.move`+`down` over a projected hand tile on this canvas (MouseUi.onMouseDown → World.onDragStart → emitDiscard). | `src/frontend/autotable-src/index.html` · `src/mouse-ui.ts:72-118` |
| `.take-seat` / `.seat-button-{0..3} .take-seat` | `<button>` | Claim a chair. The gate clicks `.seat-button-0 .take-seat` for seat 0, then reads back `client.seat`. | `src/frontend/autotable-src/index.html:52-88` |
| `#connect` | `<button>` | Establish the WS session (the client also auto-connects when a `gameId` is in the URL). | `src/frontend/autotable-src/index.html:243` · `src/client-ui.ts:191-192,490-497` |
| `#deal` | `<button>` | Start a hand. Current bundle binds a plain `click` (game-ui.ts setupDealButton → world.deal), which then client-drives the manual pickup ceremony. | `src/frontend/autotable-src/index.html:93` · `src/game-ui.ts:705-740` |
| `#lobby-close` / `#lobby-panel` | `<button>` / `<div>` | The lobby panel re-opens after navigation (W23 UX bug); the gate closes it before connecting. | `src/frontend/autotable-src/index.html:1006` |
| `#claim-pung`, `#claim-chow`, `#claim-kong`, `#claim-hu`, `#claim-pass` | `<button>` | Claim-window responses. The gate clicks `#claim-hu` when a real Hu is offered, else `#claim-pass`. | `src/frontend/autotable-src/src/game-ui.ts:483-488,836-851` |
| `#game-complete-modal` | `<div>` (Bootstrap modal) | End-of-match scoring modal. The gate asserts this is visible from a REAL completion — driven server-side by `client.gameComplete["current"].isComplete`. | `src/frontend/autotable-src/src/game-ui.ts:516,2039-2085` |
| `#loading` | `<div>` | First-paint loading overlay; hidden once the scene mounts. | `src/frontend/autotable-src/index.html` · `src/game.ts:68` |

> **Backend contract note (handoff, #122).** `#game-complete-modal` depends on
> the autotable WS backend emitting a `gameComplete` collection entry. At HEAD
> the translator emits only `result["current"]` per hand — there is no
> `ChangshaCollectionKinds.GameComplete`, so the modal is unreachable via the
> real UI today (only via the diagnostic backdoor). Wiring the terminal
> `gameComplete` push is WP-A / Bishop (runtime + `ChangshaToAutotableTranslator`).

### Playability gate (#122) — live-game controls (post #123/#125/#127 merge)

> **Update:** after #123 the autotable WS backend DOES emit an authoritative
> `gameComplete` collection entry (`ChangshaCollectionKinds.GameComplete`), so
> `#game-complete-modal` is reachable via real play (superseding the earlier
> "backdoor-only" note above).

| Selector | Element | Purpose | Source |
|---|---|---|---|
| `#pickup-hud` / `#pickup-take-btn` | `<div>` / `<button>` | Manual-pickup HUD ("Your turn — pick N tiles") + the real "Take N" button the human clicks to draw hands 2..N. | `index.html` · `src/game-ui.ts:504-507,1587` |
| `#result-modal` / `#result-next` | Bootstrap modal / `<button>` | Per-hand scoring modal + "下一局 Next Hand" button (`match[1]={action:'nextHand'}`). `data-backdrop="static"`, keyboard-disabled — dismissable only via `#result-next`. | `index.html:366-392` · `src/game-ui.ts:972-975` |
| `#perspective` | `<input type=checkbox>` | Flat/perspective view toggle (also the `p` key → `MainView.setPerspective`). | `src/game.ts:59,204-207` |

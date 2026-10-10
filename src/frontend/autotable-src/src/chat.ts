import type { Client } from './client';
import { Sound } from './sound';
import { t, onLanguageChange, translateElements } from './i18n';
import { showEl, setElHidden } from './dom-utils';
import { getVerifiedIdentity, onIdentityBootstrap } from './identity';
import { getGameState, getGameStateStatus, refreshGameState, subscribeGameState } from './game-state';
import {
  getLobbyPresence, inviteExpired, markInvitesRead, sendTableInvite, startLobbyPresence,
  subscribeLobbyPresence, type OnlinePlayer, type TableInvite,
} from './lobby-presence';
import { buildRoomJoinUrl } from './room-join-url';
import { readConcreteGameId } from './session-url';
import { getSettings, onSettingsChange, setSettings } from './settings-drawer';
import { COMPACT_VIEW_QUERY, toggleMobilePanel } from './mobile-overlay-policy';

export type ChatChannel = 'table' | 'spectators' | 'private' | 'spectator-private';

export interface ChatMessage {
  id: string;
  gameId: string;
  channel: ChatChannel;
  senderPlayerId: string;
  senderDisplayName: string;
  senderAvatarColor: string | null;
  recipientPlayerId: string | null;
  body: string;
  sentUtc: string;
  isSelf: boolean;
  isSystem: boolean;
}

const MAX_BODY_LEN = 280;
const POLL_INTERVAL_MS = 6000;
const COLLAPSED_LS_KEY = 'mahjong.chat.collapsed.v1';
const LAST_SEEN_LS_KEY = 'mahjong.chat.lastSeenIso.v2';

interface ChatState {
  available: boolean;
  installed: boolean;
  collapsed: boolean;
  channel: ChatChannel;
  channelChosen: boolean;
  privateRecipientId: string | null;
  messages: ChatMessage[];
  seenIds: Set<string>;
  localPlayerId: string | null;
  gameId: string | null;
  pollTimer: number | null;
  lastSeenIso: string;
  historyLoaded: boolean;
  historyPending: boolean;
  historyController: AbortController | null;
  sending: boolean;
  scope: string;
  epoch: number;
  error: string | null;
  client: Client | null;
}

const state: ChatState = {
  available: false, installed: false, collapsed: true,
  channel: 'table', channelChosen: false, privateRecipientId: null,
  messages: [], seenIds: new Set(), localPlayerId: null, gameId: null,
  pollTimer: null, lastSeenIso: '', historyLoaded: false, historyPending: false,
  historyController: null, sending: false, scope: '', epoch: 0, error: null, client: null,
};
const boundClients = new WeakSet<Client>();
const inviteSending = new Set<string>();
let errorTimer: number | null = null;
let messageScrollTop = 0;
let messageAtBottom = true;

function loadCollapsed(): boolean {
  if (window.matchMedia(COMPACT_VIEW_QUERY).matches) return getSettings().mobileInfoPanel !== 'chat';
  try { return window.localStorage.getItem(COLLAPSED_LS_KEY) !== 'false'; }
  catch { return true; }
}

function cursorKey(): string {
  return `${LAST_SEEN_LS_KEY}:${encodeURIComponent(state.gameId ?? '')}:${encodeURIComponent(state.localPlayerId ?? '')}`;
}

function loadLastSeen(): string {
  try {
    const value = window.localStorage.getItem(cursorKey()) ?? '';
    return Number.isFinite(Date.parse(value)) ? value : '';
  } catch { return ''; }
}

function saveLastSeen(): void {
  try { window.localStorage.setItem(cursorKey(), state.lastSeenIso); }
  catch { /* The in-memory cursor still deduplicates when storage is disabled. */ }
}

function isSpectator(): boolean {
  return state.client?.connected() === true ? state.client.seat === null
    : new URLSearchParams(window.location.search).get('seat') === '-1';
}

function isRoomConnected(): boolean {
  return state.gameId !== null && state.localPlayerId !== null
    && getVerifiedIdentity()?.playerId === state.localPlayerId
    && state.client?.connected() === true
    && state.client.lastGameId !== null
    && state.client.serverSnapshotGameId === state.client.lastGameId
    && state.client.turn.get('current') !== null
    && (new URLSearchParams(window.location.search).get('variant') ?? 'changsha').toLowerCase() === 'changsha';
}

function normalizeChannel(raw: unknown): ChatChannel {
  if (raw === 'spectator') return 'spectators';
  if (raw === 'spectators' || raw === 'private' || raw === 'spectator-private' || raw === 'table') return raw;
  if (typeof raw === 'string' && raw.startsWith('private:') && raw.length > 8) return 'private';
  throw new Error('Invalid chat channel.');
}

function needsRecipient(channel: ChatChannel): boolean {
  return channel === 'private' || channel === 'spectator-private';
}

function wireChannel(channel: ChatChannel): 'table' | 'spectators' | 'private' {
  return channel === 'spectator-private' ? 'private' : channel;
}

function normalizeMessage(raw: unknown, gameId: string, localPlayerId: string): ChatMessage {
  if (raw === null || typeof raw !== 'object') throw new Error('Invalid chat message response.');
  const o = raw as Record<string, unknown>;
  const id = o.id ?? o.Id;
  const body = o.body ?? o.Body;
  const rawChannel = o.channel ?? o.Channel;
  const channel = normalizeChannel(rawChannel);
  const senderPlayerId = o.senderPlayerId ?? o.SenderPlayerId ?? o.playerId;
  const senderDisplayName = o.senderDisplayName ?? o.SenderDisplayName ?? senderPlayerId;
  const avatarColor = o.senderAvatarColor ?? o.SenderAvatarColor ?? null;
  const legacyRecipient = typeof rawChannel === 'string' && rawChannel.startsWith('private:')
    ? rawChannel.slice(8) : null;
  const recipientPlayerId = o.recipientPlayerId ?? o.RecipientPlayerId ?? legacyRecipient;
  const sentUtc = o.sentUtc ?? o.SentUtc ?? o.at;
  const canonicalRoom = o.gameId ?? o.GameId;
  if (typeof id !== 'string' || id === '' || typeof body !== 'string' || body === ''
      || typeof senderPlayerId !== 'string' || senderPlayerId === ''
      || typeof senderDisplayName !== 'string'
      || typeof sentUtc !== 'string' || !Number.isFinite(Date.parse(sentUtc))
      || canonicalRoom !== gameId
      || (avatarColor !== null && typeof avatarColor !== 'string')
      || (recipientPlayerId !== null && typeof recipientPlayerId !== 'string')
      || (needsRecipient(channel) && !recipientPlayerId)) {
    throw new Error('Chat message does not match the room/identity contract.');
  }
  return {
    id, gameId, channel, senderPlayerId,
    senderDisplayName: senderDisplayName.trim() || senderPlayerId,
    senderAvatarColor: avatarColor, recipientPlayerId, body, sentUtc,
    isSelf: senderPlayerId === localPlayerId, isSystem: false,
  };
}

class ChatRequestError extends Error {
  constructor(readonly status: number) {
    super(`HTTP ${status}`);
  }
}

function describeError(error: unknown): string {
  if (error instanceof ChatRequestError) {
    if (error.status === 401) return t('chat.identity_required');
    if (error.status === 403) return t('chat.membership_required');
    if (error.status === 404) return t('chat.room_missing');
    if (error.status === 429) return t('chat.send_rate_limited');
    return t('chat.http_failed', { status: error.status });
  }
  return t('chat.request_failed', { reason: error instanceof Error ? error.message : String(error) });
}

function appendMessages(incoming: ChatMessage[], advanceCursor = true): ChatMessage[] {
  const added: ChatMessage[] = [];
  for (const message of incoming) {
    if (advanceCursor && (state.lastSeenIso === '' || Date.parse(message.sentUtc) > Date.parse(state.lastSeenIso))) {
      state.lastSeenIso = message.sentUtc;
    }
    if (state.seenIds.has(message.id)) continue;
    state.seenIds.add(message.id);
    state.messages.push(message);
    added.push(message);
  }
  state.messages.sort((a, b) => Date.parse(a.sentUtc) - Date.parse(b.sentUtc) || a.id.localeCompare(b.id));
  if (advanceCursor) saveLastSeen();
  return added;
}

async function refreshHistory(): Promise<void> {
  if (!isRoomConnected() || state.historyPending || state.gameId === null || state.localPlayerId === null) {
    renderAvailability();
    return;
  }
  const gameId = state.gameId;
  const localPlayerId = state.localPlayerId;
  const epoch = state.epoch;
  const ctrl = new AbortController();
  state.historyController = ctrl;
  state.historyPending = true;
  const previouslyLoaded = state.historyLoaded;
  const params = new URLSearchParams({ limit: '200' });
  // On a fresh page, reload persisted history rather than showing an empty
  // pane after the saved cursor. Overlap subsequent reads to preserve timestamp ties.
  if (state.historyLoaded && state.lastSeenIso !== '') {
    params.set('since', new Date(Date.parse(state.lastSeenIso) - 1).toISOString());
  }
  try {
    const response = await fetch(`/api/games/${encodeURIComponent(gameId)}/chat?${params}`, {
      credentials: 'same-origin', headers: { Accept: 'application/json' }, signal: ctrl.signal,
    });
    if (!response.ok) throw new ChatRequestError(response.status);
    const raw: unknown = await response.json();
    if (epoch !== state.epoch || ctrl.signal.aborted) return;
    const messages = raw !== null && typeof raw === 'object' ? (raw as Record<string, unknown>).messages : null;
    if (!Array.isArray(messages)) throw new Error('Chat history response omitted messages.');
    const incoming = messages.map(message => normalizeMessage(message, gameId, localPlayerId));
    const added = appendMessages(incoming);
    state.available = true;
    state.error = null;
    state.historyLoaded = true;
    if (previouslyLoaded && added.some(message => !message.isSelf)) Sound.play('claim');
    renderMessages();
  } catch (failure) {
    if (epoch !== state.epoch || ctrl.signal.aborted) return;
    state.available = false;
    state.error = describeError(failure);
  } finally {
    if (epoch === state.epoch) {
      state.historyController = null;
      state.historyPending = false;
      renderAvailability();
    }
  }
}

function startPolling(): void {
  if (state.pollTimer !== null || state.collapsed) return;
  state.pollTimer = window.setInterval(() => { void refreshHistory(); }, POLL_INTERVAL_MS);
}

function stopPolling(): void {
  if (state.pollTimer !== null) window.clearInterval(state.pollTimer);
  state.pollTimer = null;
}

function syncRoom(): void {
  const identity = getVerifiedIdentity();
  const gameId = state.client?.connected() === true ? state.client.lastGameId : readConcreteGameId(window.location.search);
  const bound = state.client?.connected() === true && state.client.lastGameId !== null
    && state.client.serverSnapshotGameId === state.client.lastGameId && state.client.turn.get('current') !== null;
  const canonicalRoom = (bound && getGameStateStatus().connected ? getGameState()?.gameId : null) ?? gameId;
  const nextScope = JSON.stringify([canonicalRoom, identity?.playerId ?? null, bound]);
  const scopeChanged = nextScope !== state.scope;
  if (scopeChanged) {
    state.historyController?.abort();
    state.epoch++;
    state.scope = nextScope;
    state.gameId = canonicalRoom;
    state.localPlayerId = identity?.playerId ?? null;
    state.messages = [];
    state.seenIds.clear();
    state.privateRecipientId = null;
    state.channelChosen = false;
    state.historyLoaded = false;
    state.historyPending = false;
    state.sending = false;
    state.available = false;
    state.error = null;
    state.lastSeenIso = loadLastSeen();
    const input = document.getElementById('chat-input') as HTMLTextAreaElement | null;
    if (input !== null) {
      input.value = '';
      updateCharCount(input);
    }
  }
  if (!isRoomConnected()) {
    state.historyController?.abort();
    state.available = false;
  }
  if (!state.channelChosen || (!isSpectator() && (state.channel === 'spectators' || state.channel === 'spectator-private'))) {
    state.channel = isSpectator() ? 'spectators' : 'table';
  }
  renderChannelOptions();
  renderRecipientOptions();
  renderAvailability();
  renderMessages();
  if (scopeChanged && isRoomConnected()) void refreshHistory();
}

function roomRecipients(): Array<{ playerId: string; displayName: string }> {
  if (state.client === null || !isRoomConnected()) return [];
  const humans = new Map(getLobbyPresence().players.map(player => [player.playerId, player]));
  const peers: Array<{ playerId: string; displayName: string }> = [];
  const seats = new Map(state.client.seats.entries());
  const localIds = new Set([...seats.keys(), ...Array.from(state.client.nicks.entries(), ([id]) => id)]);
  for (const playerId of localIds) {
    if (playerId === state.localPlayerId || playerId === 'offline') continue;
    const seat = seats.get(playerId)?.seat;
    if (state.channel === 'spectator-private' && seat !== null && seat !== undefined) continue;
    const human = humans.get(playerId);
    if (human !== undefined) peers.push({ playerId, displayName: human.displayName });
  }
  return peers;
}

function renderRecipientOptions(): void {
  const select = document.getElementById('chat-recipient-select') as HTMLSelectElement | null;
  if (select === null) return;
  const previous = state.privateRecipientId;
  const peers = roomRecipients();
  select.replaceChildren(new Option(t('chat.recipient_none'), ''));
  for (const peer of peers) select.appendChild(new Option(peer.displayName, peer.playerId));
  state.privateRecipientId = peers.some(peer => peer.playerId === previous) ? previous : null;
  select.value = state.privateRecipientId ?? '';
  const wrap = document.getElementById('chat-recipient-wrap');
  if (wrap !== null) setElHidden(wrap, !needsRecipient(state.channel));
}

function renderChannelOptions(): void {
  const select = document.getElementById('chat-channel-select') as HTMLSelectElement | null;
  if (select === null) return;
  const channels: ChatChannel[] = ['table'];
  if (isSpectator()) channels.push('spectators', 'spectator-private');
  channels.push('private');
  select.replaceChildren();
  for (const channel of channels) {
    const key = channel === 'spectator-private' ? 'spectator_private' : channel;
    const option = new Option(t(`chat.channel.${key}`), channel);
    option.setAttribute('data-testid', `chat-channel-${channel}`);
    select.appendChild(option);
  }
  select.value = state.channel;
}

function colorForSender(message: Pick<ChatMessage, 'senderAvatarColor' | 'senderPlayerId'>): string {
  if (message.senderAvatarColor !== null && /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(message.senderAvatarColor)) {
    return message.senderAvatarColor;
  }
  let hash = 5381;
  for (let i = 0; i < message.senderPlayerId.length; i++) hash = ((hash << 5) + hash + message.senderPlayerId.charCodeAt(i)) | 0;
  return `hsl(${(hash & 0xff) * 360 / 256}, 55%, 45%)`;
}

function visibleMessages(): ChatMessage[] {
  const channel = wireChannel(state.channel);
  return state.messages.filter(message => {
    if (message.isSystem) return true;
    if (wireChannel(message.channel) !== channel) return false;
    if (channel !== 'private') return true;
    const self = message.senderPlayerId === state.localPlayerId || message.recipientPlayerId === state.localPlayerId;
    const peer = state.privateRecipientId;
    return self && (peer === null || message.senderPlayerId === peer || message.recipientPlayerId === peer);
  });
}

function captureMessageScroll(): void {
  const list = document.getElementById('chat-messages');
  if (state.collapsed || list === null || list.clientHeight === 0) return;
  messageScrollTop = list.scrollTop;
  messageAtBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 40;
}

function restoreMessageScroll(reveal = false): void {
  const list = document.getElementById('chat-messages');
  if (state.collapsed || list === null || list.clientHeight === 0) return;
  list.scrollTop = messageAtBottom ? list.scrollHeight : messageScrollTop;
  if (!reveal) return;
  const rows = Array.from(list.querySelectorAll<HTMLElement>('.chat-message'));
  const top = list.getBoundingClientRect().top;
  const row = messageAtBottom ? rows[rows.length - 1]
    : rows.find(message => message.getBoundingClientRect().bottom > top);
  // The message scroller is nested inside the social/room scroller. Restoring
  // only its scrollTop can leave the message clipped above the compact dock.
  row?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
}

function renderMessages(): void {
  const list = document.getElementById('chat-messages');
  if (list === null) return;
  captureMessageScroll();
  list.replaceChildren();
  document.getElementById('chat-panel')?.setAttribute('data-channel', state.channel);
  const messages = visibleMessages();
  if (messages.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'chat-empty';
    empty.textContent = needsRecipient(state.channel) && state.privateRecipientId === null
      ? t('chat.empty_private') : t('chat.no_messages');
    list.appendChild(empty);
    return;
  }
  messages.forEach((message, index) => {
    const row = document.createElement('div');
    row.className = `chat-message chat-message-${message.isSelf ? 'self' : 'other'} chat-message-channel-${message.channel}`;
    if (message.isSystem) row.classList.add('chat-message-system');
    row.setAttribute('data-testid', `chat-message-${index}`);
    row.setAttribute('data-channel', message.channel);
    row.setAttribute('data-message-id', message.id);
    const avatar = document.createElement('span');
    avatar.className = 'chat-message-avatar';
    avatar.style.backgroundColor = colorForSender(message);
    avatar.textContent = (message.senderDisplayName || message.senderPlayerId).charAt(0).toUpperCase();
    const bubble = document.createElement('div');
    bubble.className = 'chat-message-bubble';
    const author = document.createElement('span');
    author.className = 'chat-message-author';
    author.setAttribute('data-testid', `chat-message-${index}-author`);
    author.textContent = message.senderDisplayName;
    const body = document.createElement('span');
    body.className = 'chat-message-body';
    body.setAttribute('data-testid', `chat-message-${index}-body`);
    body.textContent = message.body;
    const time = document.createElement('time');
    time.className = 'chat-message-time';
    time.dateTime = message.sentUtc;
    time.textContent = new Date(message.sentUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    bubble.append(author, body, time);
    row.append(...(message.isSelf ? [bubble, avatar] : [avatar, bubble]));
    list.appendChild(row);
  });
  restoreMessageScroll();
}

function renderAvailability(): void {
  const root = document.getElementById('chat-panel');
  const placeholder = document.getElementById('chat-unavailable');
  const available = state.available && isRoomConnected();
  root?.classList.toggle('chat-panel-unavailable', !available);
  if (placeholder !== null) {
    setElHidden(placeholder, available);
    placeholder.textContent = state.gameId === null ? t('chat.join_table')
      : state.localPlayerId === null ? t('chat.identity_required')
      : state.error ?? (!isRoomConnected() ? t('chat.connect_first') : t('common.loading'));
  }
  const input = document.getElementById('chat-input') as HTMLTextAreaElement | null;
  const send = document.getElementById('chat-send') as HTMLButtonElement | null;
  if (input !== null) input.disabled = !available || state.sending;
  if (send !== null) send.disabled = !available || state.sending;
  const selectors = document.getElementById('chat-room-selectors');
  if (selectors !== null) setElHidden(selectors, state.gameId === null);
  const room = document.getElementById('chat-room-content');
  if (room !== null) setElHidden(room, state.gameId === null);
}

function flashError(message: string): void {
  const status = document.getElementById('chat-status');
  if (status === null) return;
  if (errorTimer !== null) window.clearTimeout(errorTimer);
  status.textContent = message;
  status.classList.add('chat-status-visible');
  errorTimer = window.setTimeout(() => {
    status.classList.remove('chat-status-visible');
    status.textContent = '';
    errorTimer = null;
  }, 8000);
}

function pushSystemMessage(body: string): void {
  state.messages.push({
    id: `sys-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
    gameId: state.gameId ?? '', channel: state.channel,
    senderPlayerId: 'system', senderDisplayName: 'system', senderAvatarColor: '#666',
    recipientPlayerId: null, body, sentUtc: new Date().toISOString(), isSelf: false, isSystem: true,
  });
  renderMessages();
}

function executeCommand(raw: string): boolean {
  const match = raw.trim().match(/^\/(\w+)(?:\s+(.*))?$/);
  if (match === null) return false;
  const command = match[1].toLowerCase();
  if (command === 'clear') {
    state.messages = [];
    pushSystemMessage(t('chat.cleared'));
  } else if (command === 'help') {
    pushSystemMessage(t('chat.command_help'));
  } else {
    pushSystemMessage(t('chat.command_unknown', { cmd: command }));
  }
  return true;
}

function updateCharCount(input: HTMLTextAreaElement): void {
  const counter = document.getElementById('chat-char-count');
  if (counter === null) return;
  counter.textContent = t('chat.char_count', { count: input.value.length, max: MAX_BODY_LEN });
  counter.classList.toggle('chat-char-count-over', input.value.length > MAX_BODY_LEN);
}

async function doSend(): Promise<void> {
  const input = document.getElementById('chat-input') as HTMLTextAreaElement | null;
  if (input === null || state.sending || input.value.trim() === '') return;
  const body = input.value;
  if (executeCommand(body)) {
    input.value = '';
    updateCharCount(input);
    return;
  }
  if (body.length > MAX_BODY_LEN) {
    flashError(t('chat.char_count', { count: body.length, max: MAX_BODY_LEN }));
    return;
  }
  if (!state.available || !isRoomConnected() || state.gameId === null || state.localPlayerId === null) {
    flashError(t('chat.connect_first'));
    return;
  }
  const recipient = needsRecipient(state.channel) ? state.privateRecipientId : null;
  if (needsRecipient(state.channel) && recipient === null) {
    flashError(t('chat.empty_private'));
    return;
  }
  const gameId = state.gameId;
  const playerId = state.localPlayerId;
  const epoch = state.epoch;
  state.sending = true;
  renderAvailability();
  try {
    const response = await fetch(`/api/games/${encodeURIComponent(gameId)}/chat`, {
      method: 'POST', credentials: 'same-origin',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify({
        channel: wireChannel(state.channel),
        ...(recipient !== null ? { recipientPlayerId: recipient } : {}),
        body,
      }),
    });
    if (!response.ok) throw new ChatRequestError(response.status);
    const raw: unknown = await response.json();
    if (epoch !== state.epoch) return;
    const message = normalizeMessage(raw, gameId, playerId);
    // The POST must not advance past inbound messages that have not been polled.
    appendMessages([message], false);
    input.value = '';
    updateCharCount(input);
    renderMessages();
    await refreshHistory();
  } catch (failure) {
    if (epoch === state.epoch) flashError(describeError(failure));
  } finally {
    if (epoch === state.epoch) {
      state.sending = false;
      renderAvailability();
      input.focus();
    }
  }
}

function inviteUnavailableReason(player: OnlinePlayer): string | null {
  const presence = getLobbyPresence();
  if (presence.status !== 'ready') return t(presence.status === 'connecting' ? 'social.connecting' : 'social.unavailable');
  if (!player.canReceiveInvites) return t('social.delivery_unavailable');
  const metadata = getGameState();
  const load = getGameStateStatus();
  if (load.roomId === null) return t('social.invite_need_table');
  if (load.status === 'not-found') return t('social.invite_error.room-not-found');
  if (load.status === 'identity-required') return t('social.invite_error.identity-required');
  if (load.status === 'unavailable' || load.status === 'invalid') {
    return t('lobby.public.unavailable', { reason: load.error ?? '' });
  }
  if (metadata === null) return t('lobby.public.loading');
  if (!load.connected) return t('social.invite_need_table');
  if (metadata.phase !== 'Seating') return t('social.invite_error.room-not-seating');
  if (metadata.openHumanSeats === 0) return t('social.invite_error.room-full');
  if (!metadata.canInvite) return t('social.invite_error.not-allowed');
  return null;
}

async function invitePlayer(playerId: string): Promise<void> {
  const player = getLobbyPresence().players.find(value => value.playerId === playerId);
  const metadata = getGameState();
  const status = document.getElementById('online-invite-status');
  if (status === null || inviteSending.has(playerId)) return;
  if (player === undefined) {
    status.textContent = t('social.invite_error.recipient-offline');
    return;
  }
  const reason = inviteUnavailableReason(player);
  if (reason !== null || metadata === null) {
    status.textContent = reason ?? t('social.invite_need_table');
    return;
  }
  inviteSending.add(playerId);
  status.textContent = t('social.invite_sending', { player: player.displayName });
  renderOnline();
  try {
    const result = await sendTableInvite(metadata.gameId, playerId);
    status.textContent = result.success
      ? t('social.invite_sent', { player: player.displayName })
      : t(`social.invite_error.${result.reason}`);
    if (!result.success && ['room-full', 'room-not-seating', 'not-allowed', 'room-not-found'].includes(result.reason)) {
      void refreshGameState();
    }
  } catch (failure) {
    status.textContent = t('social.invite_failed', { reason: failure instanceof Error ? failure.message : String(failure) });
  } finally {
    inviteSending.delete(playerId);
    renderOnline();
  }
}

function renderOnline(): void {
  const list = document.getElementById('online-players-list');
  const status = document.getElementById('online-players-status');
  const retry = document.getElementById('online-players-retry') as HTMLButtonElement | null;
  if (list === null || status === null) return;
  const snapshot = getLobbyPresence();
  status.textContent = snapshot.status === 'ready'
    ? t(snapshot.players.length === 0 ? 'social.no_others' : 'social.online_count', { count: snapshot.players.length })
    : snapshot.status === 'connecting' ? t('social.connecting')
    : t('social.unavailable_reason', { reason: snapshot.error ?? '' });
  if (retry !== null) setElHidden(retry, snapshot.status !== 'unavailable');
  list.setAttribute('aria-busy', String(snapshot.status === 'connecting'));
  const existing = new Map(Array.from(list.querySelectorAll<HTMLElement>('[data-player-id]'))
    .map(row => [row.dataset.playerId, row]));
  snapshot.players.forEach((player, index) => {
    let row = existing.get(player.playerId);
    if (row === undefined) {
      row = document.createElement('div');
      row.className = 'online-player';
      row.setAttribute('role', 'listitem');
      row.setAttribute('data-testid', 'online-player');
      row.dataset.playerId = player.playerId;
      const name = document.createElement('span');
      name.setAttribute('data-testid', 'online-player-name');
      name.className = 'online-player-name';
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'btn btn-sm btn-primary online-player-invite';
      button.setAttribute('data-testid', 'online-player-invite');
      button.addEventListener('click', () => { void invitePlayer(player.playerId); });
      const availability = document.createElement('span');
      availability.className = 'online-player-availability';
      row.append(name, button, availability);
    }
    existing.delete(player.playerId);
    const name = row.querySelector<HTMLElement>('.online-player-name')!;
    name.textContent = player.displayName;
    const button = row.querySelector<HTMLButtonElement>('button')!;
    const reason = inviteUnavailableReason(player);
    button.textContent = t('social.invite_action');
    button.disabled = reason !== null || inviteSending.has(player.playerId);
    button.title = reason ?? '';
    row.querySelector<HTMLElement>('.online-player-availability')!.textContent = reason ?? '';
    if (list.children[index] !== row) list.insertBefore(row, list.children[index] ?? null);
  });
  for (const row of existing.values()) row.remove();
}

function updateInviteCard(card: HTMLElement, invite: TableInvite): void {
  card.setAttribute('aria-label', t('social.invite_received', {
    sender: invite.senderDisplayName, table: invite.publicName ?? invite.gameId,
  }));
  card.querySelector<HTMLElement>('[data-testid="table-invite-sender"]')!.textContent = invite.senderDisplayName;
  card.querySelector<HTMLElement>('[data-testid="table-invite-table"]')!.textContent = invite.publicName ?? invite.gameId;
  const join = card.querySelector<HTMLAnchorElement>('[data-testid="table-invite-join"]')!;
  const expired = inviteExpired(invite);
  join.textContent = t('social.join_table');
  join.setAttribute('aria-disabled', String(expired));
  if (expired) {
    join.removeAttribute('href');
    join.tabIndex = -1;
  } else {
    join.href = buildRoomJoinUrl(invite.gameId);
    join.removeAttribute('tabindex');
  }
  const expiry = card.querySelector<HTMLElement>('[data-testid="table-invite-expired"]')!;
  expiry.textContent = t('social.expired');
  setElHidden(expiry, !expired);
  const deadline = card.querySelector<HTMLElement>('[data-testid="table-invite-expiry"]')!;
  deadline.textContent = t('social.invite_expires', {
    time: new Date(invite.expiresUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
  });
  setElHidden(deadline, expired);
}

function renderInvites(): void {
  const list = document.getElementById('incoming-invites');
  if (list === null) return;
  const snapshot = getLobbyPresence();
  const existing = new Map(Array.from(list.querySelectorAll<HTMLElement>('[data-invite-id]'))
    .map(card => [card.dataset.inviteId, card]));
  snapshot.invites.forEach((invite, index) => {
    let card = existing.get(invite.inviteId);
    if (card === undefined) {
      card = document.createElement('article');
      card.className = 'table-invite';
      card.setAttribute('data-testid', 'table-invite');
      card.dataset.inviteId = invite.inviteId;
      const sender = document.createElement('strong');
      sender.setAttribute('data-testid', 'table-invite-sender');
      const table = document.createElement('span');
      table.setAttribute('data-testid', 'table-invite-table');
      const join = document.createElement('a');
      join.className = 'btn btn-sm btn-success';
      join.setAttribute('role', 'link');
      join.setAttribute('data-testid', 'table-invite-join');
      join.addEventListener('click', event => {
        if (inviteExpired(invite)) {
          event.preventDefault();
          renderInvites();
        }
      });
      const expiry = document.createElement('small');
      expiry.setAttribute('data-testid', 'table-invite-expired');
      const deadline = document.createElement('small');
      deadline.setAttribute('data-testid', 'table-invite-expiry');
      card.append(sender, table, join, deadline, expiry);
    }
    existing.delete(invite.inviteId);
    updateInviteCard(card, invite);
    if (list.children[index] !== card) list.insertBefore(card, list.children[index] ?? null);
  });
  for (const card of existing.values()) card.remove();
  const empty = document.getElementById('incoming-invites-empty');
  if (empty !== null) {
    setElHidden(empty, snapshot.invites.length !== 0);
    empty.textContent = t(snapshot.status === 'ready' ? 'social.no_invites'
      : snapshot.status === 'connecting' ? 'social.connecting' : 'social.unavailable');
  }
}

function setCollapsed(collapsed: boolean): void {
  const previous = state.collapsed;
  if (window.matchMedia(COMPACT_VIEW_QUERY).matches) {
    const current = getSettings().mobileInfoPanel;
    const next = toggleMobilePanel(current, 'chat', !collapsed);
    if (next !== current) setSettings({ mobileInfoPanel: next });
  } else {
    try { window.localStorage.setItem(COLLAPSED_LS_KEY, String(collapsed)); }
    catch { /* The panel still opens when preferences cannot be stored. */ }
  }
  if (state.collapsed === previous) applyCollapsed(collapsed);
}

function applyCollapsed(collapsed: boolean): void {
  const wasCollapsed = state.collapsed;
  captureMessageScroll();
  state.collapsed = collapsed;
  document.getElementById('chat-panel')?.classList.toggle('chat-panel-collapsed', collapsed);
  renderCollapseControls();
  if (collapsed) {
    stopPolling();
  } else {
    restoreMessageScroll();
    window.requestAnimationFrame(() => restoreMessageScroll(wasCollapsed));
    markInvitesRead();
    void refreshHistory();
    startPolling();
  }
}

function renderCollapseControls(): void {
  const toggle = document.getElementById('chat-toggle');
  toggle?.setAttribute('aria-expanded', String(!state.collapsed));
  toggle?.setAttribute('aria-label', t(state.collapsed ? 'chat.open' : 'chat.collapse'));
  toggle?.setAttribute('title', t(state.collapsed ? 'chat.open' : 'chat.collapse'));
  const close = document.getElementById('chat-collapse');
  if (close !== null) {
    setElHidden(close, state.collapsed);
    close.setAttribute('aria-label', t('chat.collapse'));
    close.setAttribute('title', t('chat.collapse'));
  }
}

export function openChatPanel(): void {
  setCollapsed(false);
  document.getElementById('chat-toggle')?.focus();
}

function renderChrome(): void {
  const root = document.getElementById('chat-panel');
  if (root !== null) translateElements(root);
  renderCollapseControls();
  const input = document.getElementById('chat-input') as HTMLTextAreaElement | null;
  if (input !== null) {
    input.placeholder = t('chat.placeholder');
    updateCharCount(input);
  }
  renderChannelOptions();
  renderRecipientOptions();
  renderAvailability();
  renderMessages();
  renderOnline();
  renderInvites();
}

function attachClient(client: Client): void {
  state.client = client;
  if (!boundClients.has(client)) {
    boundClients.add(client);
    const update = (): void => {
      if (state.client !== client) return;
      syncRoom();
    };
    client.on('connect', update);
    client.on('joining', update);
    client.on('disconnect', update);
    client.on('update', (_entries, full) => { if (full) update(); });
    client.seats.on('update', update);
    client.nicks.on('update', renderRecipientOptions);
  }
  syncRoom();
  if (!state.historyLoaded) void refreshHistory();
}

export function installChatPanel(client: Client | null): void {
  if (state.installed) {
    if (client !== null) attachClient(client);
    return;
  }
  const root = document.getElementById('chat-panel');
  if (root === null) return;
  state.installed = true;
  state.collapsed = loadCollapsed();
  state.client = client;
  showEl(root);
  root.classList.toggle('chat-panel-collapsed', state.collapsed);
  const toggle = document.getElementById('chat-toggle');
  toggle?.setAttribute('aria-expanded', String(!state.collapsed));
  toggle?.addEventListener('click', () => setCollapsed(!state.collapsed));
  document.getElementById('chat-collapse')?.addEventListener('click', () => {
    setCollapsed(true);
    toggle?.focus();
  });
  const compact = window.matchMedia(COMPACT_VIEW_QUERY);
  compact.addEventListener('change', () => applyCollapsed(loadCollapsed()));
  onSettingsChange(settings => {
    if (compact.matches && state.collapsed !== (settings.mobileInfoPanel !== 'chat')) {
      applyCollapsed(settings.mobileInfoPanel !== 'chat');
    }
  });
  const channel = document.getElementById('chat-channel-select') as HTMLSelectElement | null;
  channel?.addEventListener('change', () => {
    state.channel = normalizeChannel(channel.value);
    state.channelChosen = true;
    renderRecipientOptions();
    renderMessages();
  });
  const recipient = document.getElementById('chat-recipient-select') as HTMLSelectElement | null;
  recipient?.addEventListener('change', () => {
    state.privateRecipientId = recipient.value || null;
    renderMessages();
  });
  const input = document.getElementById('chat-input') as HTMLTextAreaElement | null;
  input?.addEventListener('input', () => updateCharCount(input));
  input?.addEventListener('keydown', event => {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void doSend();
    }
  });
  document.getElementById('chat-send')?.addEventListener('click', () => { void doSend(); });
  document.getElementById('online-players-retry')?.addEventListener('click', () => { void startLobbyPresence(); });
  subscribeLobbyPresence(() => {
    renderOnline();
    renderInvites();
    renderRecipientOptions();
  });
  subscribeGameState(() => {
    syncRoom();
    renderOnline();
  });
  onIdentityBootstrap(syncRoom);
  onLanguageChange(renderChrome);
  if (client !== null) attachClient(client);
  else syncRoom();
  renderChrome();
  if (!state.collapsed) setCollapsed(false);
}

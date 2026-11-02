import type { HubConnection } from '@microsoft/signalr';
import {
  getHubConnection, hubIsConnected, invokeHub, onHubConnected, onHubConnectionCreated, onHubStatus,
} from './hub';
import { getVerifiedIdentity } from './identity';
import { onLanguageChange, t } from './i18n';
import { buildRoomJoinUrl } from './room-join-url';
import { setElHidden } from './dom-utils';

export interface OnlinePlayer {
  playerId: string;
  displayName: string;
  avatarColor: string | null;
  canReceiveInvites: boolean;
}

export interface TableInvite {
  inviteId: string;
  senderPlayerId: string;
  senderDisplayName: string;
  senderAvatarColor: string | null;
  recipientPlayerId: string;
  gameId: string;
  publicName: string | null;
  joinUrl: string;
  createdUtc: string;
  expiresUtc: string;
}

export type TableInviteFailure = 'identity-required' | 'not-allowed' | 'room-not-found'
  | 'room-not-seating' | 'room-full' | 'recipient-offline' | 'self-invite'
  | 'rate-limited' | 'inbox-full';

export type TableInviteResult = { success: true; invite: TableInvite }
  | { success: false; reason: TableInviteFailure };

export interface LobbyPresenceSnapshot {
  status: 'connecting' | 'ready' | 'unavailable';
  revision: number;
  players: ReadonlyArray<OnlinePlayer>;
  invites: ReadonlyArray<TableInvite>;
  unreadCount: number;
  error: string | null;
}

const failureReasons: ReadonlyArray<string> = [
  'identity-required', 'not-allowed', 'room-not-found', 'room-not-seating', 'room-full',
  'recipient-offline', 'self-invite', 'rate-limited', 'inbox-full',
];
const listeners = new Set<(value: LobbyPresenceSnapshot) => void>();
const inbox = new Map<string, TableInvite>();
let players: OnlinePlayer[] = [];
let revision = -1;
let status: LobbyPresenceSnapshot['status'] = 'connecting';
let error: string | null = null;
let installed = false;
let playerId: string | null = null;
let subscriptionEpoch = 0;
let subscription: Promise<void> | null = null;
let expiryTimer: number | null = null;
let readIds = new Set<string>();
let notifiedIds = new Set<string>();

function record(raw: unknown): Record<string, unknown> {
  if (raw === null || typeof raw !== 'object') throw new Error('Invalid lobby response.');
  return raw as Record<string, unknown>;
}

function requiredString(value: unknown): string {
  if (typeof value !== 'string' || value === '') throw new Error('Invalid lobby response field.');
  return value;
}

function nullableString(value: unknown): string | null {
  if (value === null) return null;
  if (typeof value !== 'string') throw new Error('Invalid lobby response field.');
  return value;
}

function parsePlayer(raw: unknown): OnlinePlayer {
  const o = record(raw);
  if (typeof o.canReceiveInvites !== 'boolean') throw new Error('Missing invite delivery availability.');
  return {
    playerId: requiredString(o.playerId),
    displayName: requiredString(o.displayName),
    avatarColor: nullableString(o.avatarColor),
    canReceiveInvites: o.canReceiveInvites,
  };
}

function parseInvite(raw: unknown): TableInvite {
  const o = record(raw);
  const gameId = requiredString(o.gameId);
  const joinUrl = buildRoomJoinUrl(gameId);
  const suppliedUrl = new URL(requiredString(o.joinUrl), window.location.origin);
  const allowedUrl = new URL(joinUrl, window.location.origin);
  if (suppliedUrl.origin !== allowedUrl.origin || suppliedUrl.pathname !== allowedUrl.pathname
      || suppliedUrl.username !== '' || suppliedUrl.password !== ''
      || suppliedUrl.hash !== '' || suppliedUrl.searchParams.toString() !== allowedUrl.searchParams.toString()) {
    throw new Error('Invitation contained a non-allowlisted table URL.');
  }
  const createdUtc = requiredString(o.createdUtc);
  const expiresUtc = requiredString(o.expiresUtc);
  if (!Number.isFinite(Date.parse(createdUtc)) || !Number.isFinite(Date.parse(expiresUtc))
      || Date.parse(expiresUtc) <= Date.parse(createdUtc)) {
    throw new Error('Invitation contained an invalid expiry.');
  }
  return {
    inviteId: requiredString(o.inviteId),
    senderPlayerId: requiredString(o.senderPlayerId),
    senderDisplayName: requiredString(o.senderDisplayName),
    senderAvatarColor: nullableString(o.senderAvatarColor),
    recipientPlayerId: requiredString(o.recipientPlayerId),
    gameId,
    publicName: nullableString(o.publicName),
    joinUrl,
    createdUtc,
    expiresUtc,
  };
}

export function getLobbyPresence(): LobbyPresenceSnapshot {
  const invites = Array.from(inbox.values()).sort((a, b) => b.createdUtc.localeCompare(a.createdUtc));
  return {
    status, revision, players, invites, error,
    unreadCount: invites.filter(invite => !readIds.has(invite.inviteId) && !inviteExpired(invite)).length,
  };
}

export function inviteExpired(invite: TableInvite): boolean {
  return Date.parse(invite.expiresUtc) <= Date.now();
}

export function subscribeLobbyPresence(callback: (value: LobbyPresenceSnapshot) => void): () => void {
  listeners.add(callback);
  callback(getLobbyPresence());
  return () => { listeners.delete(callback); };
}

function renderBadge(snapshot: LobbyPresenceSnapshot): void {
  const badge = document.getElementById('chat-invite-unread');
  if (badge === null) return;
  badge.textContent = String(snapshot.unreadCount);
  badge.setAttribute('aria-label', t('social.unread', { count: snapshot.unreadCount }));
  setElHidden(badge, snapshot.unreadCount === 0);
  const lobbyBadge = document.getElementById('lobby-invite-unread');
  if (lobbyBadge !== null) {
    lobbyBadge.textContent = snapshot.unreadCount > 0 ? ` (${snapshot.unreadCount})` : '';
  }
}

function emit(): void {
  const snapshot = getLobbyPresence();
  renderBadge(snapshot);
  for (const callback of listeners) callback(snapshot);
}

function fail(failure: unknown): void {
  status = 'unavailable';
  error = failure instanceof Error ? failure.message : String(failure);
  emit();
}

function storageKey(): string {
  return `mahjong.invites.seen.v1:${playerId}`;
}

function restoreSeen(): void {
  readIds = new Set();
  notifiedIds = new Set();
  try {
    const raw: unknown = JSON.parse(window.sessionStorage.getItem(storageKey()) ?? '{}');
    const saved = record(raw);
    if (Array.isArray(saved.read)) readIds = new Set(saved.read.filter((id): id is string => typeof id === 'string'));
    if (Array.isArray(saved.notified)) notifiedIds = new Set(saved.notified.filter((id): id is string => typeof id === 'string'));
  } catch { /* Optional tab-local notification preferences; the server owns the inbox. */ }
}

function saveSeen(): void {
  try {
    window.sessionStorage.setItem(storageKey(), JSON.stringify({
      read: Array.from(readIds).slice(-100),
      notified: Array.from(notifiedIds).slice(-100),
    }));
  } catch { /* Notification dedup still works in memory when storage is unavailable. */ }
}

export function markInvitesRead(): void {
  for (const invite of inbox.values()) readIds.add(invite.inviteId);
  saveSeen();
  emit();
}

function notifyInvite(invite: TableInvite): void {
  if (readIds.has(invite.inviteId) || notifiedIds.has(invite.inviteId) || inviteExpired(invite)) return;
  notifiedIds.add(invite.inviteId);
  saveSeen();
  const region = document.getElementById('toast-region');
  if (region === null) return;
  const toast = document.createElement('div');
  toast.className = 'toast toast-info toast-visible';
  toast.setAttribute('role', 'status');
  toast.setAttribute('data-testid', 'table-invite-toast');
  const message = document.createElement('p');
  message.textContent = t('social.invite_received', {
    sender: invite.senderDisplayName, table: invite.publicName ?? invite.gameId,
  });
  const view = document.createElement('button');
  view.type = 'button';
  view.className = 'btn btn-sm btn-primary';
  view.textContent = t('social.view_invites');
  view.addEventListener('click', () => {
    window.dispatchEvent(new CustomEvent('mahjong:open-chat'));
    toast.remove();
  });
  toast.append(message, view);
  region.appendChild(toast);
  window.setTimeout(() => toast.remove(), 12000);
}

function scheduleExpiry(): void {
  if (expiryTimer !== null) window.clearTimeout(expiryTimer);
  expiryTimer = null;
  const times = Array.from(inbox.values()).map(invite => Date.parse(invite.expiresUtc))
    .filter(time => time > Date.now());
  if (times.length === 0) return;
  expiryTimer = window.setTimeout(() => {
    expiryTimer = null;
    emit();
    scheduleExpiry();
  }, Math.min(2147483647, Math.max(1, Math.min(...times) - Date.now() + 1)));
}

function mergeInvites(raw: unknown[]): void {
  const incoming = raw.map(parseInvite);
  for (const invite of incoming) {
    if (invite.recipientPlayerId !== playerId) throw new Error('Invitation recipient did not match the verified identity.');
  }
  for (const invite of incoming) {
    if (inbox.has(invite.inviteId)) continue;
    if (inviteExpired(invite)) continue;
    inbox.set(invite.inviteId, invite);
    notifyInvite(invite);
  }
  for (const [id, invite] of inbox) {
    if (inbox.size > 50 && inviteExpired(invite)) inbox.delete(id);
  }
  scheduleExpiry();
}

function applyRoster(raw: unknown): void {
  const o = record(raw);
  if (typeof o.revision !== 'number' || !Number.isSafeInteger(o.revision) || o.revision < 0
      || !Array.isArray(o.players)) throw new Error('Invalid lobby roster snapshot.');
  if (o.revision <= revision) return;
  const roster = new Map<string, OnlinePlayer>();
  for (const value of o.players) {
    const player = parsePlayer(value);
    if (player.playerId === playerId || player.playerId === 'offline') continue;
    roster.set(player.playerId, player);
  }
  players = Array.from(roster.values()).sort((a, b) => a.displayName.localeCompare(b.displayName));
  revision = o.revision;
}

async function joinLobby(connection: HubConnection): Promise<void> {
  const identity = getVerifiedIdentity();
  if (identity === null) {
    fail(new Error('Verified identity is required for the online lobby.'));
    return;
  }
  if (playerId !== identity.playerId) {
    playerId = identity.playerId;
    players = [];
    inbox.clear();
    restoreSeen();
  }
  const epoch = ++subscriptionEpoch;
  revision = -1;
  status = 'connecting';
  error = null;
  emit();
  try {
    const snapshot: unknown = await connection.invoke('JoinLobby');
    if (epoch !== subscriptionEpoch || getVerifiedIdentity()?.playerId !== playerId) return;
    const o = record(snapshot);
    if (!Array.isArray(o.invites)) throw new Error('Lobby snapshot omitted the invitation inbox.');
    applyRoster(o);
    mergeInvites(o.invites);
    status = 'ready';
    emit();
  } catch (failure) {
    if (epoch === subscriptionEpoch) fail(failure);
  }
}

async function subscribe(connection: HubConnection): Promise<void> {
  const attempt = joinLobby(connection);
  subscription = attempt;
  try {
    await attempt;
  } finally {
    if (subscription === attempt) subscription = null;
  }
}

function install(): void {
  if (installed) return;
  installed = true;
  onHubConnectionCreated(connection => {
    connection.on('LobbyPlayersChanged', (snapshot: unknown) => {
      if (!hubIsConnected() || getVerifiedIdentity()?.playerId !== playerId) return;
      try {
        applyRoster(snapshot);
        emit();
      } catch (failure) { fail(failure); }
    });
    connection.on('TableInviteReceived', (invite: unknown) => {
      if (!hubIsConnected() || getVerifiedIdentity()?.playerId !== playerId) return;
      try {
        mergeInvites([invite]);
        emit();
      } catch (failure) { fail(failure); }
    });
  });
  onHubStatus(value => {
    if (value.state === 'connected') return;
    subscriptionEpoch++;
    subscription = null;
    revision = -1;
    status = value.state === 'connecting' || value.state === 'reconnecting' ? 'connecting' : 'unavailable';
    error = value.error;
    emit();
  });
  onHubConnected(connection => { void subscribe(connection); });
  onLanguageChange(() => renderBadge(getLobbyPresence()));
  window.addEventListener('online', () => { void startLobbyPresence(); });
}

/** Independent of chat mounting and of the heavyweight game renderer. */
export async function startLobbyPresence(): Promise<void> {
  install();
  try {
    const connection = await getHubConnection();
    if (status !== 'ready' && subscription === null) await subscribe(connection);
    else if (subscription !== null) await subscription;
  } catch (failure) {
    fail(failure);
  }
}

export async function sendTableInvite(gameId: string, recipientPlayerId: string): Promise<TableInviteResult> {
  if (status !== 'ready' || !hubIsConnected()) throw new Error(t('social.unavailable'));
  const response: unknown = await invokeHub('SendTableInvite', gameId, recipientPlayerId);
  const result = record(response);
  if (result.success === true) {
    const invite = parseInvite(result.invite);
    if (invite.gameId !== gameId || invite.recipientPlayerId !== recipientPlayerId
        || invite.senderPlayerId !== getVerifiedIdentity()?.playerId) {
      throw new Error('Invitation confirmation did not match the requested sender, recipient and table.');
    }
    return { success: true, invite };
  }
  if (result.success === false && typeof result.reason === 'string' && failureReasons.includes(result.reason)) {
    return { success: false, reason: result.reason as TableInviteFailure };
  }
  throw new Error('Invalid SendTableInvite response.');
}

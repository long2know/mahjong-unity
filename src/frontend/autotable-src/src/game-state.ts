import {
  bootstrapIdentity, getVerifiedIdentity, getIdentityBootstrapState, onIdentityBootstrap,
} from './identity';
import { onHubConnected } from './hub';
import { buildRoomJoinUrl } from './room-join-url';

export interface GameState {
  gameId: string;
  ownerId: string | null;
  viewerIsOwner: boolean;
  phase: string;
  isPublic: boolean;
  publicName: string | null;
  voiceEnabled: boolean;
  viewerCanManageVoice: boolean;
  botCount: number;
  seatedCount: number;
  openHumanSeats: number;
  canMakePublic: boolean;
  canInvite: boolean;
}

export interface GameStateStatus {
  status: 'idle' | 'loading' | 'ready' | 'not-found' | 'identity-required' | 'invalid' | 'unavailable';
  roomId: string | null;
  playerId: string | null;
  connected: boolean;
  error: string | null;
}

type Listener = (state: GameState | null) => void;
let state: GameState | null = null;
let roomId: string | null = null;
let playerId: string | null = null;
let joinedRoomId: string | null = null;
let status: GameStateStatus['status'] = 'idle';
let error: string | null = null;
let generation = 0;
let controller: AbortController | null = null;
let inflight: Promise<GameState | null> | null = null;
const listeners = new Set<Listener>();

export function getGameState(): GameState | null {
  return playerId === getVerifiedIdentity()?.playerId ? state : null;
}

export function getGameStateStatus(): GameStateStatus {
  return {
    status, roomId, playerId, error,
    connected: joinedRoomId !== null && (joinedRoomId === roomId || joinedRoomId === state?.gameId),
  };
}

function emit(): void {
  for (const cb of listeners) cb(getGameState());
}

export function subscribeGameState(cb: Listener): () => void {
  listeners.add(cb);
  cb(getGameState());
  return () => { listeners.delete(cb); };
}

export function setGameRoomConnected(gameId: string | null): void {
  if (joinedRoomId === gameId) return;
  // This edge comes from a bound runtime snapshot, not a URL or JOINED ack.
  // Revoke pending old-room work immediately on disconnect/switch/reconnect.
  invalidate();
  joinedRoomId = gameId;
  roomId = gameId;
  playerId = getVerifiedIdentity()?.playerId ?? null;
  status = 'idle';
  emit();
}

function invalidate(): void {
  generation++;
  controller?.abort();
  controller = null;
  inflight = null;
  state = null;
  error = null;
}

function parseGamePayload(raw: unknown): GameState {
  if (raw === null || typeof raw !== 'object') throw new Error('Invalid room metadata response.');
  const o = raw as Record<string, unknown>;
  const booleans = ['viewerIsOwner', 'isPublic', 'voiceEnabled', 'viewerCanManageVoice', 'canMakePublic', 'canInvite'];
  const counts = ['botCount', 'seatedCount', 'openHumanSeats'];
  if (typeof o.gameId !== 'string' || o.gameId === ''
      || typeof o.phase !== 'string' || o.phase === ''
      || (o.ownerId !== null && typeof o.ownerId !== 'string')
      || (o.publicName !== null && typeof o.publicName !== 'string')
      || booleans.some(key => typeof o[key] !== 'boolean')
      || counts.some(key => typeof o[key] !== 'number'
        || !Number.isInteger(o[key]) || (o[key] as number) < 0 || (o[key] as number) > 4)) {
    throw new Error('Room metadata does not match the multiplayer contract.');
  }
  buildRoomJoinUrl(o.gameId);
  return {
    gameId: o.gameId,
    ownerId: o.ownerId as string | null,
    viewerIsOwner: o.viewerIsOwner as boolean,
    phase: o.phase,
    isPublic: o.isPublic as boolean,
    publicName: o.publicName as string | null,
    voiceEnabled: o.voiceEnabled as boolean,
    viewerCanManageVoice: o.viewerCanManageVoice as boolean,
    botCount: o.botCount as number,
    seatedCount: o.seatedCount as number,
    openHumanSeats: o.openHumanSeats as number,
    canMakePublic: o.canMakePublic as boolean,
    canInvite: o.canInvite as boolean,
  };
}

/** One active room + verified-identity cache; refreshes invalidate older async responses. */
export function loadGameState(gameId: string, refresh = false): Promise<GameState | null> {
  if (joinedRoomId === null || (gameId !== joinedRoomId
    && !(roomId === joinedRoomId && state?.gameId === gameId))) return Promise.resolve(null);
  const identityId = getVerifiedIdentity()?.playerId ?? null;
  const sameRoom = roomId === gameId || state?.gameId === gameId;
  if (!sameRoom || playerId !== identityId) {
    invalidate();
    roomId = gameId;
    playerId = identityId;
    status = 'idle';
  }
  if (!refresh && status === 'ready') return Promise.resolve(getGameState());
  if (!refresh && inflight !== null) return inflight;
  invalidate();
  status = 'loading';
  const epoch = generation;
  const ctrl = new AbortController();
  controller = ctrl;
  emit();

  const attempt = (async (): Promise<GameState | null> => {
    try {
      const identity = await bootstrapIdentity();
      if (epoch !== generation || joinedRoomId === null) return null;
      if (identity === null) {
        status = 'identity-required';
        error = getIdentityBootstrapState().error;
        emit();
        return null;
      }
      playerId = identity.playerId;
      const response = await fetch(`/api/games/${encodeURIComponent(gameId)}`, {
        credentials: 'same-origin',
        headers: { Accept: 'application/json' },
        signal: ctrl.signal,
      });
      if (epoch !== generation || getVerifiedIdentity()?.playerId !== identity.playerId) return null;
      if (!response.ok) {
        status = response.status === 404 ? 'not-found'
          : response.status === 401 ? 'identity-required'
          : response.status === 400 ? 'invalid' : 'unavailable';
        error = `HTTP ${response.status}`;
        emit();
        return null;
      }
      const payload: unknown = await response.json();
      if (epoch !== generation || getVerifiedIdentity()?.playerId !== identity.playerId) return null;
      state = parseGamePayload(payload);
      status = 'ready';
      emit();
      return state;
    } catch (failure) {
      if (ctrl.signal.aborted || epoch !== generation) return null;
      status = 'unavailable';
      error = failure instanceof Error ? failure.message : String(failure);
      emit();
      return null;
    } finally {
      if (epoch === generation) {
        inflight = null;
        controller = null;
      }
    }
  })();
  inflight = attempt;
  return attempt;
}

export function refreshGameState(gameId = state?.gameId ?? roomId): Promise<GameState | null> {
  return gameId === null ? Promise.resolve(null) : loadGameState(gameId, true);
}

export function clearGameState(): void {
  invalidate();
  roomId = null;
  playerId = null;
  joinedRoomId = null;
  status = 'idle';
  emit();
}

export function resetGameState(): void {
  clearGameState();
  listeners.clear();
}

onIdentityBootstrap(value => {
  if (playerId !== null && (value.status !== 'ready' || getVerifiedIdentity()?.playerId !== playerId)) {
    invalidate();
    playerId = null;
    status = 'identity-required';
    emit();
  }
});

onHubConnected(() => {
  if (roomId !== null) void refreshGameState();
});

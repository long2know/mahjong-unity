import type { HandResultAckCommand, HandResultContinuation, HandResultEntry } from './types';

export interface ResultContext {
  roomId: string | null;
  connected: boolean;
  freshSnapshot: boolean;
  seat: number | null;
  changsha: boolean;
  complete: boolean;
}

export type ResultMode = 'none' | 'continue' | 'pending' | 'waiting' | 'advancing'
  | 'observer' | 'dismiss' | 'reconnecting' | 'invalid' | 'relay';

export interface ResultPresentation {
  identity: string | null;
  visible: boolean;
  mode: ResultMode;
  waitingSeats: readonly number[];
  error: string | null;
}

function identifier(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && value.length <= 512
    && value.trim() === value && !/[\u0000-\u001f\u007f]/.test(value);
}

function seats(value: unknown): value is number[] {
  return Array.isArray(value) && value.every((seat, i) =>
    Number.isInteger(seat) && seat >= 0 && seat < 4 && (i === 0 || value[i - 1] < seat));
}

export function parseHandResultContinuation(value: unknown): HandResultContinuation | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const entry = value as Record<string, unknown>;
  if (!identifier(entry.gameId) || !identifier(entry.resultToken)
    || typeof entry.handNumber !== 'number' || !Number.isSafeInteger(entry.handNumber) || entry.handNumber < 0
    || !seats(entry.requiredSeats) || !seats(entry.acknowledgedSeats) || !seats(entry.waitingSeats)) return null;
  const { requiredSeats, acknowledgedSeats, waitingSeats } = entry;
  if (acknowledgedSeats.some(seat => !requiredSeats.includes(seat) || waitingSeats.includes(seat))
    || waitingSeats.some(seat => !requiredSeats.includes(seat))
    || requiredSeats.length !== acknowledgedSeats.length + waitingSeats.length) return null;
  return {
    gameId: entry.gameId, handNumber: entry.handNumber, resultToken: entry.resultToken,
    requiredSeats: [...requiredSeats], acknowledgedSeats: [...acknowledgedSeats], waitingSeats: [...waitingSeats],
  };
}

/** The canonical runtime ID/token are copied, never inferred from the room URL. */
export function handResultIdentity(result: HandResultEntry, roomId: string | null): string {
  const continuation = parseHandResultContinuation(result.continuation);
  if (continuation) return JSON.stringify([continuation.gameId, continuation.handNumber, continuation.resultToken]);
  const stable = { ...result };
  delete stable.continuation;
  return JSON.stringify([roomId, stable]);
}

/** Local UI state only. Merely observing a result can never create a command. */
export class HandResultState {
  private result: HandResultEntry | null = null;
  private context: ResultContext = { roomId: null, connected: false, freshSnapshot: false, seat: null, changsha: true, complete: false };
  private identity: string | null = null;
  private continuation: HandResultContinuation | null = null;
  private pending = false;
  private attemptedIdentity: string | null = null;
  private dismissed: string | null = null;
  private error: string | null = null;
  private synchronizedRoom: string | null = null;

  update(result: HandResultEntry | null, context: ResultContext): ResultPresentation {
    if (context.roomId !== this.context.roomId) {
      this.identity = this.dismissed = this.error = null;
      this.pending = false;
      this.attemptedIdentity = null;
      this.synchronizedRoom = null;
    }
    if (context.freshSnapshot) this.synchronizedRoom = context.roomId;
    if (context.changsha && (context.roomId === null || this.synchronizedRoom !== context.roomId)) result = null;
    this.context = context;
    this.result = result;
    const identity = result ? handResultIdentity(result, context.roomId) : null;
    if (identity !== this.identity) {
      this.pending = false;
      this.attemptedIdentity = null;
      this.dismissed = this.error = null;
    }
    this.identity = identity;
    this.continuation = result ? parseHandResultContinuation(result.continuation) : null;
    if (!context.connected || !context.freshSnapshot) this.pending = false;
    if (context.seat !== null && this.continuation?.acknowledgedSeats.includes(context.seat)) this.pending = false;
    return this.presentation();
  }

  presentation(): ResultPresentation {
    const base = { identity: this.identity, waitingSeats: this.continuation?.waitingSeats ?? [], error: this.error };
    if (!this.result || this.context.complete) return { ...base, visible: false, mode: 'none' };
    if (!this.context.changsha) return { ...base, visible: this.dismissed !== this.identity, mode: 'relay' };
    if (!this.context.connected || !this.context.freshSnapshot) {
      return { ...base, visible: this.dismissed !== this.identity, mode: 'reconnecting' };
    }
    if (this.result.continuation !== null && this.result.continuation !== undefined && !this.continuation) {
      return { ...base, visible: true, mode: 'invalid' };
    }
    if (!this.continuation) return { ...base, visible: this.dismissed !== this.identity, mode: 'dismiss' };
    const required = this.context.seat !== null && this.continuation.requiredSeats.includes(this.context.seat);
    if (!required) return { ...base, visible: this.dismissed !== this.identity, mode: 'observer' };
    if (this.continuation.waitingSeats.includes(this.context.seat!)) {
      return { ...base, visible: true, mode: this.pending ? 'pending' : 'continue' };
    }
    return { ...base, visible: true, mode: this.continuation.waitingSeats.length > 0 ? 'waiting' : 'advancing' };
  }

  acknowledge(): HandResultAckCommand | null {
    if (this.presentation().mode !== 'continue' || !this.continuation) return null;
    this.pending = true;
    this.attemptedIdentity = this.identity;
    this.error = null;
    const { gameId, handNumber, resultToken } = this.continuation;
    return { gameId, handNumber, resultToken };
  }

  reject(reason: string): void {
    if (!this.result || this.attemptedIdentity !== this.identity) return;
    this.pending = false;
    this.error = reason;
  }

  dismiss(): boolean {
    if (!['dismiss', 'observer', 'relay'].includes(this.presentation().mode)) return false;
    this.dismissed = this.identity;
    return true;
  }
}

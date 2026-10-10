/* eslint no-console: 0 */

import { EventEmitter } from 'events';
import { Message, Entry, ViewerAuthority, ViewerSeat } from '../server/protocol';

export interface Game {
  gameId: string;
  playerId: string;
}

export type ClientMode = 'offline' | 'relay' | 'changsha';

export function onlineModeForVariant(variant: string | null): 'relay' | 'changsha' {
  return variant === null || variant === '' || variant.toLowerCase() === 'changsha' ? 'changsha' : 'relay';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function isViewerSeat(value: unknown): value is ViewerSeat {
  return value === null || value === 0 || value === 1 || value === 2 || value === 3;
}

function parseViewer(value: unknown): Readonly<ViewerAuthority> | null {
  if (!isRecord(value) || !['roomId', 'revision', 'seat'].every(key => Object.prototype.hasOwnProperty.call(value, key))) {
    return null;
  }
  const { roomId, revision, seat } = value;
  if (roomId !== null && (typeof roomId !== 'string' || roomId === '')) return null;
  if (typeof revision !== 'number' || !Number.isSafeInteger(revision) || revision < 0) return null;
  if (!isViewerSeat(seat) || (roomId === null && seat !== null)) return null;
  return Object.freeze({ roomId, revision, seat });
}

function isEntries(value: unknown): value is Entry[] {
  return Array.isArray(value) && value.every(entry => Array.isArray(entry) && entry.length === 3
    && typeof entry[0] === 'string' && (typeof entry[1] === 'string' || typeof entry[1] === 'number'));
}

function terminalRejections(entries: Entry[]): Entry[] {
  return entries.filter(([kind, key, value]) => kind === 'actionRejected' && key === 'current'
    && isRecord(value) && (value.action === 'join' || value.action === 'room')
    && typeof value.reason === 'string' && value.reason !== '');
}

export class BaseClient {
  private ws: WebSocket | null = null;
  private socketUrl: string | null = null;
  private socketEpoch = 0;
  private game: Game | null = null;
  private events = new EventEmitter();
  private pending: Entry[] | null = null;
  private mode: ClientMode;
  private viewer: Readonly<ViewerAuthority> | null = null;
  private viewerRevision = -1;
  private protocolFailure: string | null = null;

  constructor(mode: ClientMode = 'offline') {
    this.mode = mode;
    this.events.setMaxListeners(50);
  }

  get connectionMode(): ClientMode {
    return this.mode;
  }

  get protocolUnavailableReason(): string | null {
    return this.protocolFailure;
  }

  get viewerAuthority(): Readonly<ViewerAuthority> | null {
    return this.connected() && this.viewer?.roomId === this.game?.gameId ? this.viewer : null;
  }

  protected get viewerSeat(): ViewerSeat {
    return this.open() && this.game !== null && this.viewer?.roomId === this.game.gameId
      ? this.viewer.seat : null;
  }

  new(url: string): void {
    this.connect(url, { type: 'NEW' });
  }

  join(url: string, gameId: string): void {
    this.connect(url, { type: 'JOIN', gameId });
  }

  disconnect(): void {
    this.viewer = null;
    if (this.ws !== null) this.finishSocket(this.ws, this.socketEpoch, true);
  }

  private connect(url: string, command: Extract<Message, { type: 'JOIN' | 'NEW' }>): void {
    this.viewer = null;
    this.game = null;
    this.pending = null;
    const parsedUrl = new URL(url, typeof window === 'undefined' ? undefined : window.location.href);
    const variant = parsedUrl.searchParams.get('variant');
    const mode = onlineModeForVariant(variant);
    const reusable = this.ws !== null && this.open() && this.socketUrl === parsedUrl.href && this.mode === mode;
    if (!reusable && this.ws !== null) this.finishSocket(this.ws, this.socketEpoch, false);

    this.mode = mode;
    this.protocolFailure = null;
    this.events.emit('joining');

    if (reusable) {
      this.send(command);
      return;
    }

    this.viewerRevision = -1;
    const epoch = ++this.socketEpoch;
    const socket = new WebSocket(url);
    this.ws = socket;
    this.socketUrl = parsedUrl.href;
    socket.onopen = () => {
      if (this.isCurrentSocket(socket, epoch)) this.send(command);
    };
    socket.onclose = () => this.finishSocket(socket, epoch, true);
    socket.onerror = () => this.finishSocket(socket, epoch, true);
    socket.onmessage = event => {
      if (!this.isCurrentSocket(socket, epoch)) return;
      let message: unknown;
      try {
        message = JSON.parse(event.data as string);
      } catch {
        this.failProtocol('Malformed primary WebSocket JSON.', socket, epoch);
        return;
      }
      this.onMessage(message, socket, epoch);
    };
  }

  on(what: 'joining', handler: () => void): void;
  on(what: 'connect', handler: (game: Game, isFirst: boolean) => void): void;
  on(what: 'disconnect', handler: (game: Game | null) => void): void;
  on(what: 'update', handler: (things: Entry[], full: boolean) => void): void;
  on(what: 'protocol-unavailable', handler: (reason: string) => void): void;
  on(what: string, handler: (...args: any[]) => void): void {
    this.events.on(what, handler);
  }

  off(what: string, handler: (...args: any[]) => void): void {
    this.events.off(what, handler);
  }

  transaction(func: () => void): void {
    this.pending = [];
    try {
      func();
      if (this.pending !== null && this.pending.length > 0) {
        this.send({ type: 'UPDATE', entries: this.pending, full: false });
      }
    } finally {
      this.pending = null;
    }
  }

  update(entries: Entry[]): void {
    const allowed = this.mode === 'changsha' ? entries.filter(([kind]) => kind !== 'viewer') : entries;
    if (allowed.length === 0) return;
    if (this.pending !== null) this.pending.push(...allowed);
    else this.send({ type: 'UPDATE', entries: allowed, full: false });
  }

  private send(message: Message): void {
    if (!this.open() || this.protocolFailure !== null) return;
    this.ws!.send(JSON.stringify(message));
  }

  private open(): boolean {
    return this.ws !== null && this.ws.readyState === WebSocket.OPEN;
  }

  connected(): boolean {
    return this.open() && this.game !== null && this.protocolFailure === null;
  }

  playerId(): string {
    return this.game?.playerId ?? 'offline';
  }

  private isCurrentSocket(socket: WebSocket, epoch: number): boolean {
    return this.ws === socket && this.socketEpoch === epoch;
  }

  private finishSocket(socket: WebSocket, epoch: number, notify: boolean): void {
    if (!this.isCurrentSocket(socket, epoch)) return;
    const game = this.game;
    this.ws = null;
    this.socketUrl = null;
    this.socketEpoch++;
    this.game = null;
    this.viewer = null;
    this.pending = null;
    if (socket.readyState !== WebSocket.CLOSED && socket.readyState !== WebSocket.CLOSING) socket.close();
    if (notify) this.events.emit('disconnect', game);
  }

  private failProtocol(reason: string, socket: WebSocket, epoch: number): void {
    if (!this.isCurrentSocket(socket, epoch) || this.protocolFailure !== null) return;
    this.viewer = null;
    this.protocolFailure = reason;
    console.error(`protocol-unavailable: ${reason}`);
    this.events.emit('protocol-unavailable', reason);
    this.finishSocket(socket, epoch, true);
  }

  private onMessage(message: unknown, socket: WebSocket, epoch: number): void {
    if (!this.isCurrentSocket(socket, epoch) || this.protocolFailure !== null) return;
    if (!isRecord(message) || (message.type !== 'JOINED' && message.type !== 'UPDATE')) {
      if (this.mode === 'changsha') this.failProtocol('Unsupported primary WebSocket envelope.', socket, epoch);
      return;
    }
    if (message.type === 'JOINED') {
      if (typeof message.gameId !== 'string' || message.gameId === ''
          || typeof message.playerId !== 'string' || message.playerId === '' || typeof message.isFirst !== 'boolean') {
        this.failProtocol('Malformed JOINED envelope.', socket, epoch);
        return;
      }
    } else if (!isEntries(message.entries) || typeof message.full !== 'boolean') {
      this.failProtocol('Malformed UPDATE envelope.', socket, epoch);
      return;
    }

    if (this.mode === 'changsha') {
      const authority = parseViewer(message.viewer);
      if (authority === null) {
        this.failProtocol('Changsha requires viewer { roomId, revision, seat }, including explicit seat:null.', socket, epoch);
        return;
      }
      if (authority.revision < this.viewerRevision) return;
      if (message.type === 'JOINED') {
        if (authority.revision <= this.viewerRevision) return;
        if (authority.roomId === null || authority.roomId !== message.gameId) {
          this.failProtocol('JOINED viewer.roomId does not match its acknowledged gameId.', socket, epoch);
          return;
        }
      } else {
        if (authority.roomId !== null && authority.roomId !== this.game?.gameId) return;
        if (authority.revision === this.viewerRevision && this.viewer !== null
            && (authority.roomId !== this.viewer.roomId || authority.seat !== this.viewer.seat)) {
          this.failProtocol('Viewer authority changed without advancing its revision.', socket, epoch);
          return;
        }
        if (authority.roomId === null) {
          this.viewer = null;
          this.viewerRevision = authority.revision;
          const rejections = terminalRejections(message.entries as Entry[]);
          if (rejections.length > 0) this.events.emit('update', rejections, false);
          if (this.isCurrentSocket(socket, epoch)) this.finishSocket(socket, epoch, true);
          return;
        }
      }
      this.viewer = authority;
      this.viewerRevision = authority.revision;
    }

    if (message.type === 'JOINED') {
      this.game = { gameId: message.gameId as string, playerId: message.playerId as string };
      this.events.emit('connect', this.game, message.isFirst);
    } else {
      const entries = message.entries as Entry[];
      try {
        this.events.emit('update', this.mode === 'changsha' ? entries.filter(([kind]) => kind !== 'viewer') : entries, message.full);
      } catch {
        this.failProtocol('Primary model snapshot could not be applied safely.', socket, epoch);
      }
    }
  }
}

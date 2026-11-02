import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { EventEmitter } from 'node:events';
import * as ts from 'typescript';
import { test, expect } from '@playwright/test';

type Entry = [string, string | number, unknown];
type Authority = { roomId: string | null; revision: number; seat: number | null };
interface TestCollection {
  get(key: string | number): unknown;
  set(key: string | number, value: unknown): void;
  on(event: string, callback: (...args: unknown[]) => void): void;
}
interface TestClient {
  seat: number | null;
  seatPlayers: Array<string | null>;
  lastGameId: string | null;
  connectionMode: string;
  protocolUnavailableReason: string | null;
  things: TestCollection;
  seats: TestCollection;
  nicks: TestCollection;
  connected(): boolean;
  join(url: string, gameId: string): void;
  disconnect(): void;
  update(entries: Entry[]): void;
  on(event: string, callback: (...args: unknown[]) => void): void;
}

function harness(mode: 'changsha' | 'relay' | 'offline' = 'changsha') {
  const sockets: FakeSocket[] = [];
  const sessions: Array<{ seat: number | null; gameId: string }> = [];
  const errors: string[] = [];
  class FakeSocket {
    static OPEN = 1;
    static CLOSED = 3;
    static CLOSING = 2;
    readyState = 0;
    sent: string[] = [];
    onopen: (() => void) | null = null;
    onmessage: ((event: { data: string }) => void) | null = null;
    onclose: (() => void) | null = null;
    onerror: (() => void) | null = null;
    constructor(readonly url: string) { sockets.push(this); }
    send(data: string): void { this.sent.push(data); }
    open(): void { this.readyState = 1; this.onopen?.(); }
    receive(value: unknown): void { this.onmessage?.({ data: JSON.stringify(value) }); }
    close(): void { this.readyState = 3; this.onclose?.(); }
  }
  const globals = {
    window: { location: { search: '?variant=changsha', href: 'http://c1.invalid/autotable/?variant=changsha' } },
    WebSocket: FakeSocket, URL, URLSearchParams,
    console: { log: (): void => {}, error: (message: string): void => { errors.push(message); } },
    setInterval: (): number => 1, clearInterval: (): void => {},
  };
  const modules: Record<string, unknown> = {
    events: { EventEmitter },
    './reconnect': {
      clearSession: (): void => {},
      saveSession: (session: { seat: number | null; gameId: string }): void => { sessions.push({ ...session }); },
    },
    './profile': {
      loadProfile: async (): Promise<{ displayName: string }> => ({ displayName: 'Stable player' }),
      refreshProfile: async (): Promise<void> => {},
      snapshotStatsForGame: (): void => {},
      onProfile: (): void => {},
      initProfileHubBindings: (): void => {},
    },
    './hub': { getHubConnection: async (): Promise<object> => ({}) },
    './game-state': {
      clearGameState: (): void => {},
      refreshGameState: async (): Promise<void> => {},
      setGameRoomConnected: (): void => {},
    },
  };
  const load = (name: string): Record<string, unknown> => {
    const filename = resolve(__dirname, '../../src', name);
    const source = readFileSync(filename, 'utf8');
    const javascript = ts.transpileModule(source, {
      compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
      fileName: filename,
    }).outputText;
    const module = { exports: {} as Record<string, unknown> };
    runInNewContext(javascript, {
      ...globals, module, exports: module.exports,
      require: (id: string): unknown => {
        if (!(id in modules)) throw new Error(`Unstubbed side-effect import in C1 unit contract: ${id}`);
        return modules[id];
      },
    }, { filename });
    return module.exports;
  };
  modules['./base-client'] = load('base-client.ts');
  const exported = load('client.ts').Client;
  if (typeof exported !== 'function') throw new Error('Actual Client source did not export its constructor.');
  const Client = exported as new (mode: string) => TestClient;
  const client = new Client(mode);
  const start = (room = 'room-a', variant = 'changsha'): FakeSocket => {
    client.join(`ws://c1.invalid/autotable/ws?variant=${variant}`, room);
    const socket = sockets[sockets.length - 1];
    socket.open();
    return socket;
  };
  return { client, start, sockets, sessions, errors };
}

const joined = (room: string, revision: number, seat: number | null) => ({
  type: 'JOINED', gameId: room, playerId: 'signed-stable-player', isFirst: false,
  viewer: { roomId: room, revision, seat },
});
const update = (viewer: Authority, entries: Entry[], full = false) => ({ type: 'UPDATE', viewer, entries, full });
const occupant: Entry = ['seats', 'signed-stable-player', { seat: 0 }];

test.describe('C1 actual-source receive contracts (controlled transport, NOT real-browser acceptance)', () => {
  for (const grantedSeat of [null, 2]) {
    test(`authority is applied before connect/update/session consumers: seat=${grantedSeat}`, () => {
      const h = harness();
      expect(h.client.seat).toBeNull();
      const connects: Array<number | null> = [];
      const updates: Array<number | null> = [];
      h.client.on('connect', () => { connects.push(h.client.seat); });
      h.client.on('update', () => { updates.push(h.client.seat); });
      const socket = h.start();
      expect(h.client.seat).toBeNull();
      socket.receive(joined('room-a', 1, grantedSeat));
      expect(connects).toEqual([grantedSeat]);
      expect(h.sessions[0]).toEqual(expect.objectContaining({ seat: grantedSeat, gameId: 'room-a' }));
      socket.receive(update({ roomId: 'room-a', revision: 1, seat: grantedSeat },
        [['seats', 'signed-stable-player', { seat: grantedSeat ?? 0 }]], true));
      expect(updates).toEqual([grantedSeat]);
      expect(h.client.seatPlayers[grantedSeat ?? 0]).toBe('signed-stable-player');
      expect(h.client.seat).toBe(grantedSeat);
    });
  }

  test('stale and wrong-room frames cannot apply entries or poison the authority revision', () => {
    const h = harness();
    const socket = h.start();
    socket.receive(joined('room-a', 3, 0));
    socket.receive(update({ roomId: 'room-a', revision: 4, seat: null },
      [occupant, ['things', 'h_current', { slotName: 'hand.0@0', rotationIndex: 2 }]], true));
    let applications = 0;
    h.client.on('update', () => { applications++; });
    const leaked: Entry[] = [['things', 71, { slotName: 'hand.0@0', rotationIndex: 1, face: 71 }]];
    socket.receive(update({ roomId: 'room-a', revision: 3, seat: 0 }, leaked, true));
    socket.receive(update({ roomId: 'wrong-room', revision: 999, seat: 2 }, leaked, true));
    socket.receive(joined('room-a', 4, 0));
    expect(applications).toBe(0);
    expect(h.client.seat).toBeNull();
    expect(h.client.things.get(71)).toBeNull();
    expect(h.client.things.get('h_current')).not.toBeNull();
    socket.receive(update({ roomId: 'room-a', revision: 5, seat: null },
      [['nicks', 'valid-newer', 'accepted']]));
    expect(applications).toBe(1);
    expect(h.client.nicks.get('valid-newer')).toBe('accepted');
    h.client.join(socket.url, 'room-b');
    expect(h.client.seat).toBeNull();
    socket.receive(joined('room-b', 6, 2));
    socket.receive(update({ roomId: 'room-a', revision: 999, seat: 0 }, leaked, true));
    expect(h.client.seat).toBe(2);
    expect(h.client.things.get(71)).toBeNull();
    socket.receive(update({ roomId: 'room-b', revision: 7, seat: null }, [], true));
    expect(h.client.seat).toBeNull();
  });

  test('disconnect never selects offline inference and callbacks from an old socket stay inert', () => {
    const h = harness();
    const oldSocket = h.start();
    oldSocket.receive(joined('room-a', 1, 0));
    oldSocket.receive(update({ roomId: 'room-a', revision: 1, seat: 0 }, [occupant], true));
    const oldReceive = oldSocket.onmessage!;
    const oldClose = oldSocket.onclose!;
    h.client.disconnect();
    expect(h.client.connectionMode).toBe('changsha');
    expect(h.client.seat).toBeNull();
    h.client.seats.set('offline', { seat: 3 });
    expect(h.client.seat).toBeNull();
    const newSocket = h.start('room-b');
    newSocket.receive(joined('room-b', 1, null));
    let applications = 0;
    h.client.on('update', () => { applications++; });
    oldReceive({ data: JSON.stringify(joined('room-a', 100, 0)) });
    oldReceive({ data: JSON.stringify(update({ roomId: 'room-a', revision: 101, seat: 0 },
      [['things', 71, { slotName: 'hand.0@0', rotationIndex: 1 }]], true)) });
    oldClose();
    expect(h.client.connected()).toBe(true);
    expect(h.client.lastGameId).toBe('room-b');
    expect(h.client.seat).toBeNull();
    expect(h.client.things.get(71)).toBeNull();
    expect(applications).toBe(0);
  });

  for (const [label, viewer] of [
    ['missing', undefined],
    ['missing-seat', { roomId: 'room-a', revision: 1 }],
    ['invalid-seat', { roomId: 'room-a', revision: 1, seat: 4 }],
    ['negative-revision', { roomId: 'room-a', revision: -1, seat: null }],
    ['fractional-revision', { roomId: 'room-a', revision: 1.5, seat: null }],
    ['unacknowledged-room', { roomId: 'other-room', revision: 1, seat: 0 }],
    ['null-room-with-grant', { roomId: null, revision: 1, seat: 0 }],
  ] as const) {
    test(`${label} authority fails closed with an explicit protocol-unavailable signal`, () => {
      const h = harness();
      const socket = h.start();
      const unavailable: unknown[] = [];
      let connected = false;
      h.client.on('protocol-unavailable', reason => { unavailable.push(reason); });
      h.client.on('connect', () => { connected = true; });
      socket.receive({ type: 'JOINED', gameId: 'room-a', playerId: 'signed-stable-player', isFirst: false, viewer });
      expect(h.client.seat).toBeNull();
      expect(h.client.connected()).toBe(false);
      expect(connected).toBe(false);
      expect(unavailable).toHaveLength(1);
      expect(h.client.protocolUnavailableReason).toBeTruthy();
      expect(h.errors).toHaveLength(1);
    });
  }

  test('pre-JOIN and terminal revocation process only the real rejection and never its private entries', () => {
    for (const afterJoin of [false, true]) {
      const h = harness();
      const socket = h.start();
      if (afterJoin) socket.receive(joined('room-a', 1, 0));
      const updates: unknown[][] = [];
      h.client.on('update', (...args) => { updates.push(args); });
      socket.receive(update({ roomId: null, revision: afterJoin ? 2 : 0, seat: null }, [
        ['actionRejected', 'current', { action: 'join', reason: 'room-not-found' }],
        ['things', 71, { slotName: 'hand.0@0', rotationIndex: 1 }],
      ], true));
      expect(h.client.seat).toBeNull();
      expect(h.client.connected()).toBe(false);
      expect(h.client.things.get(71)).toBeNull();
      expect(updates).toEqual([[[['actionRejected', 'current', { action: 'join', reason: 'room-not-found' }]], false]]);
    }
  });

  test('viewer collections cannot supply authority while relay and explicitly offline seats still work', () => {
    const h = harness();
    const socket = h.start();
    socket.receive(joined('room-a', 1, null));
    const sent = socket.sent.length;
    h.client.update([['viewer', 'current', { roomId: 'room-a', revision: 9, seat: 0 }]]);
    expect(socket.sent.length).toBe(sent);
    socket.receive(update({ roomId: 'room-a', revision: 1, seat: null },
      [occupant, ['viewer', 'current', { roomId: 'room-a', revision: 9, seat: 0 }]], true));
    expect(h.client.seat).toBeNull();
    expect(h.client.seatPlayers[0]).toBe('signed-stable-player');

    const relay = harness('relay');
    const relaySocket = relay.start('relay-room', 'four_player');
    relaySocket.receive({ type: 'JOINED', gameId: 'relay-room', playerId: 'signed-stable-player', isFirst: false });
    relaySocket.receive({ type: 'UPDATE', entries: [occupant], full: true });
    expect(relay.client.seat).toBe(0);
    expect(relay.client.protocolUnavailableReason).toBeNull();
    const offline = harness('offline');
    offline.client.seats.set('offline', { seat: 2 });
    expect(offline.client.seat).toBe(2);
  });
});

// Browser-free unit controls execute the actual controller module. DOM, transport
// and chooser doubles isolate context binding; this is not live/rules qualification.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import * as ts from 'typescript';
import { test, expect } from '@playwright/test';
import { hideEl, setElHidden } from '../../src/dom-utils';

type Intent = 'Pung' | 'Chow' | 'Kong' | 'Hu' | 'Pass';
type WireEntry = [string, string | number, unknown];
type Metadata = Record<string, unknown>;
type UpdateListener = (entries: WireEntry[], full: boolean) => void;

const sourcePath = resolve(__dirname, '../../src/ui/rule-action-controls.ts');
const source = readFileSync(sourcePath, 'utf8');
const sourceHash = createHash('sha256').update(source).digest('hex');
const javascript = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.CommonJS },
}).outputText;
const INTENTS: Intent[] = ['Pung', 'Chow', 'Kong', 'Hu', 'Pass'];
const RUNTIME_ID = 'runtime-identity-not-the-room-alias';
const PAIR = [0, 4];

function metadata(context: Metadata = { gameId: RUNTIME_ID, stateVersion: 42 }): Metadata {
  return {
    available: ['Pung', 'Chow', 'Kong', 'Hu'],
    deadline: 0, source: 3, tile: 8, chowOptions: [PAIR],
    ...context,
  };
}

class CollectionDouble {
  private readonly values = new Map<string | number, unknown>();
  get(key: string | number): unknown { return this.values.get(key) ?? null; }
  entries(): Iterable<[string | number, unknown]> { return this.values.entries(); }
  clear(): void { this.values.clear(); }
  apply(key: string | number, value: unknown): void {
    if (value === null) this.values.delete(key);
    else this.values.set(key, value);
  }
}

class ClientDouble {
  seat: number | null = 0;
  seatPlayers: Array<string | null> = ['owner', null, null, null];
  lastGameId = 'friendly-room';
  serverSnapshotGameId = 'friendly-room';
  online = true;
  readonly sent: WireEntry[] = [];
  readonly claim = new CollectionDouble();
  readonly ownTurn = new CollectionDouble();
  readonly turn = new CollectionDouble();
  readonly gameComplete = new CollectionDouble();
  readonly actionRejected = new CollectionDouble();
  private readonly listeners = new Map<string, UpdateListener[]>();

  connected(): boolean { return this.online; }
  playerId(): string { return 'owner'; }
  on(event: string, listener: UpdateListener): void {
    this.listeners.set(event, [...this.listeners.get(event) ?? [], listener]);
  }
  update(entries: WireEntry[]): void { this.sent.push(...entries); }
  emit(event: string, entries: WireEntry[] = [], full = false): void {
    for (const listener of this.listeners.get(event) ?? []) listener(entries, full);
  }
  receive(entries: WireEntry[], full = false): void {
    const collections: Record<string, CollectionDouble> = {
      claim: this.claim, ownTurn: this.ownTurn, turn: this.turn,
      gameComplete: this.gameComplete, actionRejected: this.actionRejected,
    };
    if (full) for (const collection of Object.values(collections)) collection.clear();
    for (const [kind, key, value] of entries) collections[kind]?.apply(key, value);
    if (full) this.serverSnapshotGameId = this.lastGameId;
    this.emit('update', entries, full);
  }
  snapshot(claim: Metadata | null, includeClaim = true): void {
    const entries: WireEntry[] = [
      ['turn', 'current', { phase: 'AwaitingClaim', activeSeat: null, awaitingDiscard: false }],
      ['ownTurn', this.seat ?? 0, null],
      ['gameComplete', 'current', null],
    ];
    if (includeClaim) entries.push(['claim', String(this.seat), claim]);
    this.receive(entries, true);
  }
}

class ElementDouble {
  hidden = true;
  disabled = false;
  textContent = '';
  readonly style = { display: '' };
  readonly attributes = new Map<string, string>();
  onclick: (() => void) | null = null;
  addEventListener(_type: string, _listener: unknown): void {}
  setAttribute(name: string, value: string): void { this.attributes.set(name, value); }
}

interface ChoiceRequest {
  context: string;
  choices: ReadonlyArray<{ tileIds: ReadonlyArray<number> }>;
  choose: (tiles: number[], context: string) => void;
}

class ChooserDouble {
  open: ChoiceRequest | null = null;
  get contextKey(): string | null { return this.open?.context ?? null; }
  localize(): void {}
  close(): void { this.open = null; }
  show(_kind: string, context: string, choices: ChoiceRequest['choices'], choose: ChoiceRequest['choose']): void {
    this.open = { context, choices, choose };
  }
}

interface ControllerPort {
  readonly claim: unknown;
  readonly claimPending: boolean;
  requestClaim(intent: Intent): void;
}

function fixture(initial = metadata()): {
  client: ClientDouble;
  controller: ControllerPort;
  chooser: ChooserDouble;
  notices: string[];
  location: { search: string; reload(): void };
} {
  const client = new ClientDouble();
  client.snapshot(initial);
  const chooser = new ChooserDouble();
  const notices: string[] = [];
  const elements = new Map<string, ElementDouble>();
  const element = (id: string): ElementDouble => {
    let value = elements.get(id);
    if (value === undefined) { value = new ElementDouble(); elements.set(id, value); }
    return value;
  };
  const location = { search: '?variant=changsha&gameId=friendly-room', reload(): void {} };
  const timers = new Map<number, () => void>();
  let nextTimer = 0;
  const exported: Record<string, unknown> = {};
  runInNewContext(javascript, {
    exports: exported, URLSearchParams, Date, location,
    window: {
      location,
      setTimeout(callback: () => void): number { timers.set(++nextTimer, callback); return nextTimer; },
      clearTimeout(id: number): void { timers.delete(id); },
    },
    document: { getElementById: element, body: { classList: { contains: (): boolean => false } } },
    require(name: string): unknown {
      switch (name) {
        case '../dom-utils': return { hideEl, setElHidden };
        case '../i18n': return {
          t: (key: string, params?: Record<string, unknown>): string => `${key}:${JSON.stringify(params ?? {})}`,
          onLanguageChange: (): (() => void) => () => {},
        };
        case '../toast': return { showToast: (message: string): void => { notices.push(message); } };
        case './rule-action-choice': return { RuleActionChoice: class { constructor() { return chooser; } } };
        case './rule-action-controls.css': return {};
        default: throw new Error(`Unexpected production import: ${name}`);
      }
    },
  });
  const ctor = exported.RuleActionControls;
  if (typeof ctor !== 'function') throw new Error('Actual controller module did not export its constructor');
  const Controller = ctor as new (client: ClientDouble) => ControllerPort;
  const controller = new Controller(client);
  test.info().annotations.push({ type: 'actual-source-sha256', description: sourceHash });
  return { client, controller, chooser, notices, location };
}

function expected(intent: Intent, context: Metadata): WireEntry {
  return ['claim', '0', {
    action: intent === 'Pass' ? 'pass' : 'claim', type: intent === 'Pass' ? null : intent,
    ...(intent === 'Chow' ? { tileIds: PAIR } : {}),
    ...context,
  }];
}

function selected(chooser: ChooserDouble): ChoiceRequest {
  if (chooser.open === null) throw new Error('The actual controller did not open the chooser');
  return chooser.open;
}

test.describe('Claim context source/behavior contract', () => {
  for (const intent of INTENTS) {
    test(`${intent} echoes the advertised runtime id/version through the shared ordinary path`, () => {
      const f = fixture();
      f.controller.requestClaim(intent);
      expect(f.client.sent).toEqual([expected(intent, { gameId: RUNTIME_ID, expectedVersion: 42 })]);
      expect(f.controller.claimPending).toBe(true);
      expect(f.controller.claim).toBeNull();
    });

    test(`${intent} preserves explicit both-absent legacy compatibility`, () => {
      const f = fixture(metadata({}));
      f.controller.requestClaim(intent);
      expect(f.client.sent).toEqual([expected(intent, {})]);
    });
  }

  for (const version of [0, 0x7fffffff]) {
    test(`copies Int32 boundary version ${version} without truthiness/default substitution`, () => {
      const f = fixture(metadata({ gameId: RUNTIME_ID, stateVersion: version }));
      f.controller.requestClaim('Pass');
      expect(f.client.sent).toEqual([expected('Pass', { gameId: RUNTIME_ID, expectedVersion: version })]);
    });
  }

  const invalid: Array<{ name: string; context: Metadata }> = [
    { name: 'missing version', context: { gameId: RUNTIME_ID } },
    { name: 'missing game', context: { stateVersion: 42 } },
    { name: 'null fields', context: { gameId: null, stateVersion: null } },
    { name: 'present undefined fields', context: { gameId: undefined, stateVersion: undefined } },
    { name: 'empty identity', context: { gameId: '', stateVersion: 42 } },
    { name: 'non-string identity', context: { gameId: 42, stateVersion: 42 } },
    { name: 'string version', context: { gameId: RUNTIME_ID, stateVersion: '42' } },
    { name: 'negative version', context: { gameId: RUNTIME_ID, stateVersion: -1 } },
    { name: 'fractional version', context: { gameId: RUNTIME_ID, stateVersion: 1.5 } },
    { name: 'Int32 overflow', context: { gameId: RUNTIME_ID, stateVersion: 0x80000000 } },
  ];
  for (const sample of invalid) {
    test(`${sample.name} fails closed for every claim/pass intent, never downgrading to legacy`, () => {
      const f = fixture(metadata(sample.context));
      expect(f.controller.claim).toBeNull();
      for (const intent of INTENTS) {
        f.controller.requestClaim(intent);
        expect(f.client.sent).toEqual([]);
      }
      expect(f.chooser.open).toBeNull();
      expect(f.notices).toHaveLength(INTENTS.length);
    });
  }

  test('deadline0 with identical tile/source but a newer version cancels the old chooser and rejects its callback', () => {
    const original = { ...metadata(), chowOptions: [PAIR, [4, 12]] };
    const f = fixture(original);
    f.controller.requestClaim('Chow');
    const old = selected(f.chooser);
    expect(f.client.sent).toEqual([]);
    f.client.snapshot({ ...original, stateVersion: 43 });
    expect(f.chooser.open).toBeNull();
    old.choose([...old.choices[1].tileIds], old.context);
    expect(f.client.sent, 'An old pair must not be promoted to version43').toEqual([]);
    f.controller.requestClaim('Chow');
    const current = selected(f.chooser);
    current.choose([...current.choices[1].tileIds], current.context);
    expect(f.client.sent).toEqual([['claim', '0', {
      action: 'claim', type: 'Chow', tileIds: [4, 12], gameId: RUNTIME_ID, expectedVersion: 43,
    }]]);
  });

  test('room, seat, runtime, source, tile, deadline and metadata-mode changes invalidate captured selections', () => {
    const mutations: Array<(f: ReturnType<typeof fixture>, claim: Metadata) => Metadata | null> = [
      (_f, claim) => ({ ...claim, gameId: 'another-runtime' }),
      (_f, claim) => ({ ...claim, source: 2 }),
      (_f, claim) => ({ ...claim, tile: 9 }),
      (_f, claim) => ({ ...claim, deadline: Date.now() + 60_000 }),
      (_f, claim) => ({ ...claim, gameId: null }),
      () => null,
      () => ({ ...metadata({}), chowOptions: [PAIR, [4, 12]] }),
      (f, claim) => {
        f.location.search = '?variant=changsha&gameId=another-room';
        f.client.lastGameId = 'another-room';
        return claim;
      },
      (f, claim) => {
        f.client.seat = 1;
        f.client.seatPlayers = [null, 'owner', null, null];
        return claim;
      },
    ];
    for (const mutate of mutations) {
      const original = { ...metadata(), chowOptions: [PAIR, [4, 12]] };
      const f = fixture(original);
      f.controller.requestClaim('Chow');
      const old = selected(f.chooser);
      f.client.snapshot(mutate(f, original));
      expect(f.chooser.open).toBeNull();
      old.choose([...old.choices[0].tileIds], old.context);
      expect(f.client.sent).toEqual([]);
    }
  });

  test('version change alone is not a correlated receipt for a pending arbitration', () => {
    const f = fixture();
    f.controller.requestClaim('Pung');
    f.client.snapshot(metadata({ gameId: RUNTIME_ID, stateVersion: 43 }));
    expect(f.controller.claimPending).toBe(true);
    expect(f.controller.claim).toBeNull();
    expect(f.client.sent).toHaveLength(1);
    f.client.snapshot(null, false);
    expect(f.controller.claimPending).toBe(false);
    expect(f.controller.claim).toBeNull();
    expect(f.client.sent).toHaveLength(1);
  });

  for (const [action, reason, intent] of [
    ['claim', 'invalid-claim-choice', 'Chow'],
    ['claim', 'invalid-claim-command', 'Kong'],
    ['claim', 'invalid-claim-context', 'Pung'],
    ['claim', 'stale-game', 'Hu'],
    ['claim', 'stale-version', 'Chow'],
    ['pass', 'claim-not-available', 'Pass'],
  ] as const) {
    test(`actual actionRejected action=${action}, reason=${reason} clears flight and waits for corrective FULL`, () => {
      const f = fixture();
      f.controller.requestClaim(intent);
      expect(f.controller.claimPending).toBe(true);
      f.client.receive([['actionRejected', 'current', { action, reason, requestedSeat: 0, ownedSeat: 0 }]]);
      expect(f.controller.claimPending).toBe(false);
      expect(f.controller.claim).toBeNull();
      expect(f.notices.some((notice) => notice.includes(reason))).toBe(true);
      expect(f.client.sent).toHaveLength(1);
      f.client.receive([['claim', '0', metadata({ gameId: RUNTIME_ID, stateVersion: 43 })]]);
      expect(f.controller.claim, 'Partial state is not the promised corrective FULL snapshot').toBeNull();
      f.client.snapshot(metadata({ gameId: RUNTIME_ID, stateVersion: 43 }));
      expect(f.controller.claim).not.toBeNull();
      expect(f.client.sent, 'Recovery does not fabricate acceptance or resend').toHaveLength(1);
      f.controller.requestClaim(intent);
      expect(f.client.sent[1]).toEqual(expected(intent, { gameId: RUNTIME_ID, expectedVersion: 43 }));
    });
  }
});

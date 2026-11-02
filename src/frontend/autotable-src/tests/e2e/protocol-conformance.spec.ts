// Ferro — WP-E / #120 — D-1 protocol-conformance static gate.
//
// Protects the #1 cross-component contract (C-1, the WS **Collection**
// protocol) by statically asserting that the set of collection *kinds* the
// frontend can put on the wire is a subset of what the backend's
// `AutotableWsEndpoint.HandleUpdateAsync` actually dispatches — so a rename
// or a newly-added collection on either side fails CI instead of silently
// half-shipping.
//
// This is a **static** gate: it reads `client.ts` and the backend
// `AutotableWsEndpoint.cs` / `AutotableProtocol.cs` off disk and never
// touches a browser or a running server.  It runs once (chromium project).

import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

// The frozen C-1 kind set (see .squad issue #120 "Locked cross-component
// contracts" and selectors.md).  Adding/removing a collection kind is a
// contract change and MUST update this list in the same PR.
const C1_KINDS = [
  'match', 'seats', 'things', 'nicks', 'mouse', 'sound',
  'dice', 'claim', 'pickup', 'discard', 'result', 'gameComplete',
  'actionRejected', 'ownTurn', 'handResultAck',
  // Stuck-turn fix (squad/fix-live-gameplay-defects) — server-authoritative
  // discard-turn cue. Server-emitted only (ChangshaCollectionEncoder.EncodeTurn),
  // client push dropped (AutotableWsEndpoint `case ChangshaCollectionKinds.Turn`),
  // FE receives it as an ephemeral collection. Inbound-only, like result/gameComplete.
  'turn',
].sort();

// Server → client only.  The FE receives these; it never sends them as
// commands (C-1: "result / gameComplete are inbound-only").
const INBOUND_ONLY = new Set(['result', 'gameComplete', 'turn', 'actionRejected']);

// FE → server game commands.  The backend MUST route each of these through
// an *explicit* case in HandleUpdateAsync — a silent default-passthrough
// would break authoritative play.
const GAME_COMMAND_KINDS = new Set(['seats', 'claim', 'pickup', 'discard', 'match', 'ownTurn', 'handResultAck']);

// Cosmetic kinds accepted by the authoritative endpoint's default passthrough.
// Keep relay scene writes separate: Changsha explicitly drops things/dice.
// These sets are explicit so a *new* FE
// kind can't quietly slip into the catch-all without a human classifying it.
const PASSTHROUGH_KINDS = new Set(['nicks', 'mouse', 'sound']);
const RUNTIME_SCENE_KINDS = new Set(['things', 'dice']);

function readRepo(rel: string): string {
  // __dirname = <repo>/src/frontend/autotable-src/tests/e2e
  return fs.readFileSync(path.resolve(__dirname, rel), 'utf-8');
}

/** Every collection kind the FE constructs (`new Collection('<kind>', …)`). */
function feCollectionKinds(): Set<string> {
  const src = readRepo('../../src/client.ts');
  const kinds = new Set<string>();
  for (const m of src.matchAll(/new\s+Collection\(\s*['"]([A-Za-z][A-Za-z0-9]*)['"]/g)) {
    kinds.add(m[1]);
  }
  return kinds;
}

/** Map `ChangshaCollectionKinds.<Name>` → wire string from AutotableProtocol.cs. */
function changshaKindConstants(): Map<string, string> {
  const src = readRepo('../../../../../src/backend/src/Mahjong.Autotable.Api/Autotable/AutotableProtocol.cs');
  const map = new Map<string, string>();
  for (const m of src.matchAll(/public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"/g)) {
    map.set(m[1], m[2]);
  }
  return map;
}

/** The kinds HandleUpdateAsync dispatches with an *explicit* `case`. */
function backendDispatchBodies(): Map<string, string> {
  const src = readRepo('../../../../../src/backend/src/Mahjong.Autotable.Api/Autotable/AutotableWsEndpoint.cs');
  const marker = 'private async Task HandleUpdateAsync(';
  const start = src.indexOf(marker);
  expect(start, 'HandleUpdateAsync not found — endpoint renamed?').toBeGreaterThan(-1);
  // Slice to the next method declaration so we only scan this switch body.
  const rest = src.slice(start + marker.length);
  const nextMethod = rest.search(/\n {4}private\s+(async\s+)?\w/);
  const body = nextMethod > -1 ? rest.slice(0, nextMethod) : rest;

  const consts = changshaKindConstants();
  const endpointConstants = new Map<string, string>();
  for (const m of src.matchAll(/(?:public|private|internal)\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"/g)) {
    endpointConstants.set(m[1], m[2]);
  }
  const clean = body.replace(/\/\/[^\n]*/g, '');
  const labels = [...clean.matchAll(/case\s+(?:"([^"]+)"|ChangshaCollectionKinds\.(\w+)|(\w+))\s*:|default\s*:/g)];
  const cases = new Map<string, string>();
  for (let i = 0; i < labels.length; i++) {
    const label = labels[i];
    if (label[0].startsWith('default')) continue;
    const kind = label[1] ?? consts.get(label[2]) ?? endpointConstants.get(label[3]);
    expect(kind, `unresolved backend kind constant: ${label[0]}`).toBeTruthy();
    let j = i;
    let statements = '';
    do {
      const marker = labels[j];
      statements = clean.slice(marker.index! + marker[0].length, labels[j + 1]?.index ?? clean.length).trim();
      j++;
    } while (statements === '' && j < labels.length);
    cases.set(kind!, statements);
  }
  return cases;
}

function backendExplicitDispatchKinds(): Set<string> {
  return new Set(backendDispatchBodies().keys());
}

function endpointHandlerBody(name: string): string {
  const src = readRepo('../../../../../src/backend/src/Mahjong.Autotable.Api/Autotable/AutotableWsEndpoint.cs');
  const marker = `private async Task ${name}(`;
  const start = src.indexOf(marker);
  if (start < 0) throw new Error(`${name} not found in AutotableWsEndpoint.cs`);
  const rest = src.slice(start + marker.length);
  const nextMethod = rest.search(/\n {4}private\s+(static\s+)?(async\s+)?\w/);
  return nextMethod > -1 ? rest.slice(0, nextMethod) : rest;
}

test.describe('WP-E/#120 — WS Collection protocol conformance (C-1) static gate', () => {
  test.beforeEach(({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'Static gate runs once (chromium project).');
  });

  test('FE collection kinds exactly match the frozen C-1 contract set', () => {
    const fe = [...feCollectionKinds()].sort();
    expect(
      fe,
      'client.ts collection kinds drifted from the frozen C-1 set — update the ' +
      'contract (C1_KINDS here + selectors.md) and the backend dispatch together.',
    ).toEqual(C1_KINDS);
  });

  test('every FE-sent kind is known to the backend HandleUpdateAsync dispatch', () => {
    const fe = feCollectionKinds();
    const explicit = backendExplicitDispatchKinds();

    // Partition sanity: what the FE can *send* is exactly the game commands
    // plus the cosmetic passthrough set (inbound-only kinds are never sent).
    const feSent = [...fe].filter((k) => !INBOUND_ONLY.has(k)).sort();
    const expectedSent = [...new Set([...GAME_COMMAND_KINDS, ...PASSTHROUGH_KINDS, ...RUNTIME_SCENE_KINDS])].sort();
    expect(
      feSent,
      'A FE-sent kind is neither a classified game command nor a cosmetic ' +
      'passthrough kind — classify it before shipping (protects C-1).',
    ).toEqual(expectedSent);

    // The load-bearing check: every game-command kind is *explicitly*
    // dispatched by the backend (not silently relayed via `default`).
    for (const kind of GAME_COMMAND_KINDS) {
      expect(
        explicit.has(kind),
        `Backend HandleUpdateAsync has no explicit case for FE game-command '${kind}' — ` +
        'the FE would send it but the runtime would only see a cosmetic relay.',
      ).toBe(true);
    }

    // Every FE-sent kind must be handled somehow: explicit dispatch OR the
    // documented cosmetic passthrough.  Nothing may fall through unclassified.
    for (const kind of feSent) {
      const handled = explicit.has(kind) || PASSTHROUGH_KINDS.has(kind);
      expect(handled, `FE-sent kind '${kind}' has no backend handling path.`).toBe(true);
    }
    for (const kind of RUNTIME_SCENE_KINDS) {
      const body = backendDispatchBodies().get(kind)!;
      expect(body, `${kind} is runtime-owned, not cosmetic`).toContain('droppedRuntimeOwnedScenePush = true;');
      expect(body).not.toContain('passthroughEntries.Add');
    }
  });

  test('inbound-only kinds are never classified as FE→server commands', () => {
    for (const kind of INBOUND_ONLY) {
      expect(
        GAME_COMMAND_KINDS.has(kind),
        `'${kind}' is inbound-only (server → client) and must not be a game command.`,
      ).toBe(false);
    }
    // The load-bearing anti-spoof check: the backend must EXPLICITLY handle (ignore) a client
    // push of EVERY inbound-only kind — both `result` AND `gameComplete`. If either fell through
    // to the `default` passthrough, a malicious/buggy client could relay a fake server-emitted
    // entry (e.g. `gameComplete.isComplete=true`) to peers and spoof an end-of-game modal. The
    // matching runtime behaviour (a client gameComplete is dropped, not relayed) is proven in
    // AutotableWsEndpointTests.ClientPushedGameComplete_IsDropped_NotRelayedToPeers.
    const explicit = backendExplicitDispatchKinds();
    for (const kind of INBOUND_ONLY) {
      expect(
        explicit.has(kind),
        `backend HandleUpdateAsync has no explicit case for inbound-only '${kind}' — a client ` +
        `push of it would hit the default passthrough and relay a spoofed entry to peers.`,
      ).toBe(true);
      expect(backendDispatchBodies().get(kind), `${kind} client pushes must be ignored, not routed or echoed`).toBe('break;');
    }
  });

  test('ownTurn and handResultAck use exact guarded command handlers, never cosmetic passthrough', () => {
    const cases = backendDispatchBodies();
    const routes = {
      ownTurn: 'TryHandleOwnTurnActionAsync',
      handResultAck: 'TryHandleHandResultAckAsync',
    };
    for (const [kind, handler] of Object.entries(routes)) {
      expect(cases.get(kind)?.replace(/\s+/g, ' ').trim()).toBe(
        `await ${handler}(connection, entry, ct); break;`,
      );
      expect(PASSTHROUGH_KINDS.has(kind)).toBe(false);
      expect(INBOUND_ONLY.has(kind)).toBe(false);
    }
    const ack = readRepo('../../../../../src/backend/src/Mahjong.Autotable.Api/Autotable/AutotableConnectionManager.HandResults.cs');
    expect(ack).toMatch(/AuthorizeSeatAction\(connection,\s*runtimeGameId,\s*requestedSeat:\s*null\)/);
    expect(ack).toMatch(/key\s*!=\s*"current"/);
    expect(ack).toMatch(/body\.EnumerateObject\(\)\.Count\(\)\s*!=\s*3/);
    for (const field of ['gameId', 'handNumber', 'resultToken']) {
      expect(ack).toContain(`TryGetProperty("${field}"`);
    }
    expect(ack).toMatch(/string\.Equals\(command\.GameId,\s*runtimeGameId,\s*StringComparison\.Ordinal\)/);
    expect(ack).toMatch(/AcknowledgeHandResultAsync\(runtimeGameId!,\s*connection\.PlayerId,\s*connection\.Id\.ToString\("N"\),\s*command\.HandNumber,\s*command\.ResultToken/);
    const ownTurn = endpointHandlerBody('TryHandleOwnTurnActionAsync');
    expect(ownTurn).toContain('AuthorizeSeatAction(connection, runtimeGameId, requestedSeat)');
    expect(ownTurn).toContain('if (!authorization.IsAuthorized)');
  });

  // #134 — the human claim wire shape. The shipped bundle writes
  //   claim[seat] = { action: 'claim', type: 'Pung'|'Chow'|'Kong'|'Hu' }  (a meld / win)
  //   claim[seat] = { action: 'pass',  type: null }                        (decline)
  // The backend TryHandleClaimActionAsync must read BOTH `action` AND `type`. The regression that
  // motivated this gate read `action` AS the claim type, so every non-Pass human claim passed the
  // literal "claim" to ParseClaimType and stalled. Behaviour is proven end-to-end in
  // AutotableWsEndpointTests / HumanClaimWireContractTests; this static gate stops the two sides
  // from silently drifting apart again.
  test('human claim wire shape: FE sends { action, type }; backend reads both (#134)', () => {
    const ui = readRepo('../../src/game-ui.ts');
    expect(
      ui,
      "game-ui.ts must send a meld/win claim as { action: 'claim', type } — the backend keys off it.",
    ).toMatch(/action:\s*'claim'\s*,\s*type/);
    expect(
      ui,
      "game-ui.ts must send a decline as { action: 'pass', type: null }.",
    ).toMatch(/action:\s*'pass'\s*,\s*type:\s*null/);

    const handler = endpointHandlerBody('TryHandleClaimActionAsync');
    expect(
      handler,
      "backend claim handler must read the 'action' field.",
    ).toMatch(/TryGetProperty\(\s*"action"/);
    expect(
      handler,
      "backend claim handler must read the 'type' field — reading only 'action' (the #134 bug) " +
      'drops every real Pung/Chow/Kong/Hu claim.',
    ).toMatch(/TryGetProperty\(\s*"type"/);
    expect(handler, "backend must branch on action == 'claim'.").toMatch(/"claim"/);
    expect(handler, "backend must branch on action == 'pass'.").toMatch(/"pass"/);
  });
});

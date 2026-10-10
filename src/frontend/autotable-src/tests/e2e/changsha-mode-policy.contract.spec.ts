// Contract tests — Changsha mode/input boundary policy (Hicks, UAT §9 FE-1/FE-2,
// refined to Vasquez R-1 oracle §D10). Browser-free; encodes the binding
// acceptance so the boundary is regression-locked.
import { test, expect } from '@playwright/test';
import {
  isServerAuthoritative,
  blocksLocalDeal,
  bootstrapDealFromUrl,
  changshaAllowsPointer,
  isOpaqueHandle,
  parseRealTileId,
  partitionThingEntries,
  pickupTakeCommand,
  pickupTriggerActionable,
  resolveTileFace,
  tileInDesignatedTrigger,
  wallTileInteractive,
  PICKUP_PHASES,
  WallInteractionInput,
} from '../../src/changsha-mode-policy';
import { reconcileHiddenBacks } from '../../src/sc2-hidden-pool';
import { DealType, GameType } from '../../src/types';

test.describe('changsha-mode-policy — server-authoritative gate', () => {
  test('isServerAuthoritative true only for Changsha', () => {
    expect(isServerAuthoritative(GameType.CHANGSHA)).toBe(true);
    expect(isServerAuthoritative(GameType.FOUR_PLAYER)).toBe(false);
    expect(isServerAuthoritative(GameType.THREE_PLAYER)).toBe(false);
    expect(isServerAuthoritative(GameType.BAMBOO)).toBe(false);
    expect(isServerAuthoritative(GameType.MINEFIELD)).toBe(false);
  });

  test('FE-1: blocksLocalDeal true only for Changsha (relay variants keep local deal)', () => {
    expect(blocksLocalDeal(GameType.CHANGSHA)).toBe(true);
    expect(blocksLocalDeal(GameType.FOUR_PLAYER)).toBe(false);
    expect(blocksLocalDeal(GameType.BAMBOO)).toBe(false);
  });
});

test.describe('changsha-mode-policy — SC-1/RC-13 first-paint deal bootstrap (bootstrapDealFromUrl)', () => {
  // Base = the World ctor seed (Conditions.initial() ⇒ Changsha manual/HANDS).
  const base = { dealMode: 'manual' as const, dealType: DealType.HANDS };

  test('?dealMode=auto boots AUTO but the placeholder is INITIAL, NOT the HANDS local scatter', () => {
    // R-B/four-half-walls (Vasquez/Frost): the auto pre-WS placeholder must be
    // the canonical all-in-walls square (INITIAL), never HANDS — HANDS pre-deals
    // client-side and scatters world.things vs the server's contiguous arc. The
    // dealMode stays 'auto' so the handshake/auto flow is unchanged.
    expect(bootstrapDealFromUrl('auto', base)).toEqual({ dealMode: 'auto', dealType: DealType.INITIAL });
  });

  test('?dealMode=manual boots MANUAL + INITIAL (canonical 108-in-walls, no face-up flash)', () => {
    expect(bootstrapDealFromUrl('manual', base)).toEqual({ dealMode: 'manual', dealType: DealType.INITIAL });
  });

  test('neither explicit mode ever yields the HANDS local-deal scatter', () => {
    expect(bootstrapDealFromUrl('auto', base).dealType).not.toBe(DealType.HANDS);
    expect(bootstrapDealFromUrl('manual', base).dealType).not.toBe(DealType.HANDS);
  });

  test('bare URL (no dealMode) keeps the variant default (non-Changsha safety)', () => {
    expect(bootstrapDealFromUrl(null, base)).toEqual({ dealMode: 'manual', dealType: DealType.HANDS });
    const autoBase = { dealMode: 'auto' as const, dealType: DealType.HANDS };
    expect(bootstrapDealFromUrl(null, autoBase)).toEqual(autoBase);
  });
});

test.describe('changsha-mode-policy — FE-2 non-wall allowlist (changshaAllowsPointer)', () => {
  const SEAT = 0;

  test('own hand tile is interactive (discard / selection)', () => {
    expect(changshaAllowsPointer({ group: 'hand', seat: SEAT }, SEAT)).toBe(true);
  });

  test('other seats\' hands are never interactive', () => {
    expect(changshaAllowsPointer({ group: 'hand', seat: 1 }, SEAT)).toBe(false);
    expect(changshaAllowsPointer({ group: 'hand', seat: 2 }, SEAT)).toBe(false);
  });

  test('wall tiles are NOT governed by this predicate (see wallTileInteractive §D10)', () => {
    // changshaAllowsPointer is hand-only; wall tiles must go through the §D10
    // gate, which requires the manual-pickup phase + target set.
    expect(changshaAllowsPointer({ group: 'wall', seat: 1 }, SEAT)).toBe(false);
  });

  test('discards and exposed melds are never interactive', () => {
    expect(changshaAllowsPointer({ group: 'discard', seat: null }, SEAT)).toBe(false);
    expect(changshaAllowsPointer({ group: 'discard', seat: SEAT }, SEAT)).toBe(false);
    expect(changshaAllowsPointer({ group: 'meld', seat: SEAT }, SEAT)).toBe(false);
    expect(changshaAllowsPointer({ group: 'meld', seat: 3 }, SEAT)).toBe(false);
  });

  test('an unseated viewer (spectator) can interact with nothing', () => {
    expect(changshaAllowsPointer({ group: 'hand', seat: 0 }, null)).toBe(false);
    expect(changshaAllowsPointer({ group: 'hand', seat: 0 }, -1)).toBe(false);
  });
});

test.describe('changsha-mode-policy — R-1 §D10 wall interactivity (wallTileInteractive)', () => {
  // A fully-satisfied manual pickup on the exposed target slot — the ONE case
  // where a wall tile may be touched.
  const legalPickup: WallInteractionInput = {
    variantIsChangsha: true,
    dealModeIsManual: true,
    pickupIsMine: true,
    authoritativePhase: 'PickupRound1',
    inDesignatedSet: true,
  };

  test('interactive on my manual pickup, correct phase, designated target slot', () => {
    expect(wallTileInteractive(legalPickup)).toBe(true);
  });

  test('every ceremony pickup phase is accepted', () => {
    for (const phase of PICKUP_PHASES) {
      expect(wallTileInteractive({ ...legalPickup, authoritativePhase: phase })).toBe(true);
    }
  });

  test('AUTO ⇒ wall ALWAYS inert (the arbitrary-wall-drag defect)', () => {
    // Auto has no pickup this seat owes; the intercept never runs.
    expect(wallTileInteractive({ ...legalPickup, dealModeIsManual: false, pickupIsMine: false }))
      .toBe(false);
  });

  test('post-deal (AwaitingDiscard) inert even if the pickup entry is STICKY (R-1 §E3)', () => {
    // pickupIsMine may still read true off a lingering pickup entry, but the
    // AUTHORITATIVE phase has advanced past the ceremony ⇒ wall must be inert.
    expect(wallTileInteractive({ ...legalPickup, authoritativePhase: 'AwaitingDiscard' }))
      .toBe(false);
    expect(wallTileInteractive({ ...legalPickup, authoritativePhase: 'AwaitingClaim' }))
      .toBe(false);
  });

  test('null authoritative phase ⇒ inert (no ceremony proof)', () => {
    expect(wallTileInteractive({ ...legalPickup, authoritativePhase: null })).toBe(false);
  });

  test('non-target wall tile is inert (only the designated batch slot, R-1 §5 / D-2)', () => {
    expect(wallTileInteractive({ ...legalPickup, inDesignatedSet: false })).toBe(false);
  });

  test("another seat's pickup ⇒ inert for me", () => {
    expect(wallTileInteractive({ ...legalPickup, pickupIsMine: false })).toBe(false);
  });

  test('non-Changsha variant ⇒ §D10 does not apply (relay keeps its own physics)', () => {
    expect(wallTileInteractive({ ...legalPickup, variantIsChangsha: false })).toBe(false);
  });
});

test.describe('changsha-mode-policy — SC-4 exact-1 pickup trigger (tileInDesignatedTrigger)', () => {
  const HOVERED = 'wall.0.0@1';

  test('exact-1: interactive ONLY when targetSlots is [hovered] (length 1, matching)', () => {
    expect(tileInDesignatedTrigger(HOVERED, [HOVERED])).toBe(true);
  });

  test('missing designation ⇒ fail closed (removes the any-wall fallback)', () => {
    expect(tileInDesignatedTrigger(HOVERED, undefined)).toBe(false);
    expect(tileInDesignatedTrigger(HOVERED, null)).toBe(false);
  });

  test('empty designation ⇒ fail closed', () => {
    expect(tileInDesignatedTrigger(HOVERED, [])).toBe(false);
  });

  test('MULTIPLE targets (>1) ⇒ fail closed (batch-set semantics forbidden by SC-4)', () => {
    expect(tileInDesignatedTrigger(HOVERED, [HOVERED, 'wall.0.1@1'])).toBe(false);
    expect(tileInDesignatedTrigger(HOVERED, ['wall.9.9@3', HOVERED, 'wall.0.1@1'])).toBe(false);
  });

  test('length-1 but different slot ⇒ inert (only the exact Wall[0] trigger)', () => {
    expect(tileInDesignatedTrigger(HOVERED, ['wall.0.1@1'])).toBe(false);
  });
});

test.describe('changsha-mode-policy — Ralph BLOCKING: bad pickup signal ⇒ ALL wall inert (composition)', () => {
  // Composes the two pure fns exactly as world.ts does:
  //   inDesignatedSet = tileInDesignatedTrigger(hovered.slot.name, pickup.targetSlots)
  //   interactive     = wallTileInteractive({ ...§D10 gate, inDesignatedSet })
  // Proves that even under an OTHERWISE-FULLY-LEGAL pickup (changsha ∧ manual ∧
  // my seat ∧ correct pickup phase), a missing / empty / multiple / wrong-slot
  // `targetSlots` fails CLOSED — no interim "any wall tile during pickup"
  // fallback. This is the binding G17 acceptance.
  const HOVERED = 'wall.0.0@1';
  const legalGate = {
    variantIsChangsha: true,
    dealModeIsManual: true,
    pickupIsMine: true,
    authoritativePhase: 'PickupRound1',
  };
  const interactiveFor = (targetSlots: string[] | null | undefined): boolean =>
    wallTileInteractive({ ...legalGate, inDesignatedSet: tileInDesignatedTrigger(HOVERED, targetSlots) });

  test('ABSENT targetSlots (undefined/null) ⇒ wall inert', () => {
    expect(interactiveFor(undefined)).toBe(false);
    expect(interactiveFor(null)).toBe(false);
  });

  test('EMPTY targetSlots ([]) ⇒ wall inert', () => {
    expect(interactiveFor([])).toBe(false);
  });

  test('MULTIPLE targetSlots (length != 1) ⇒ wall inert (no batch-set)', () => {
    expect(interactiveFor([HOVERED, 'wall.0.1@1'])).toBe(false);
    expect(interactiveFor(['wall.9.9@3', HOVERED, 'wall.0.1@1'])).toBe(false);
  });

  test('WRONG slot (length-1 but not hovered) ⇒ wall inert', () => {
    expect(interactiveFor(['wall.0.1@1'])).toBe(false);
  });

  test('ONLY the exact single trigger (targetSlots===[hovered]) ⇒ interactive', () => {
    expect(interactiveFor([HOVERED])).toBe(true);
  });
});

test.describe('changsha-mode-policy — F2 reachability: covered/bottom trigger ⇒ inert (pickupTriggerActionable)', () => {
  const TOP = 'wall.0.1@1';   // layer 1 = reachable top of the frontier stack

  test('designated + REACHABLE top (not covered) ⇒ actionable', () => {
    expect(pickupTriggerActionable(TOP, [TOP], /* covered */ false)).toBe(true);
  });

  test('designated but COVERED/bottom ⇒ inert (F2: never the occluded tile)', () => {
    // Even though targetSlots[0] matches the hovered slot exactly, a covered
    // (bottom-layer) tile is unreachable ⇒ fail closed.
    expect(pickupTriggerActionable(TOP, [TOP], /* covered */ true)).toBe(false);
  });

  test('covered AND wrong/absent designation ⇒ inert (both guards)', () => {
    expect(pickupTriggerActionable(TOP, ['wall.9.9@3'], true)).toBe(false);
    expect(pickupTriggerActionable(TOP, undefined, true)).toBe(false);
    expect(pickupTriggerActionable(TOP, [], true)).toBe(false);
  });

  test('not covered but bad designation ⇒ still inert (designation guard holds)', () => {
    expect(pickupTriggerActionable(TOP, ['wall.9.9@3'], false)).toBe(false);
    expect(pickupTriggerActionable(TOP, [TOP, 'wall.0.0@1'], false)).toBe(false);
  });
});

test.describe('changsha-mode-policy — P0 count-based take, zero client tile authority (pickupTakeCommand)', () => {
  test('the take is EXACTLY {seatIndex,count} — no tile id / handle / slot', () => {
    const cmd = pickupTakeCommand(2, 4);
    expect(cmd).toEqual({ seatIndex: 2, count: 4 });
    // No leaked tile authority: only the two count-based keys exist.
    expect(Object.keys(cmd).sort()).toEqual(['count', 'seatIndex']);
  });

  test('count is carried verbatim (server takes Wall[0..count-1] by count)', () => {
    expect(pickupTakeCommand(0, 1)).toEqual({ seatIndex: 0, count: 1 }); // single/dealer-extra
    expect(pickupTakeCommand(3, 4)).toEqual({ seatIndex: 3, count: 4 }); // 4-batch
  });
});

test.describe('changsha-mode-policy — SC-2 mixed entitled + opaque keys (partitionThingEntries + resolveTileFace)', () => {
  // A realistic mixed `things` snapshot: my own hand tile (real id, face-up),
  // a foreign concealed hand tile (opaque handle), a wall tile (opaque handle),
  // a public discard (real id), and a tombstone (opaque handle → null).
  type Info = { face?: number | null } | null;
  const snapshot: Array<[string | number, Info]> = [
    [42, { face: 10 }],                 // my hand — entitled, face-up (numeric key)
    ['7', { face: undefined }],         // my hand — entitled numeric-STRING key ("7")
    ['h_Qk9r3xM2ab12cd34ef', { face: null }],  // foreign concealed — opaque `h_` back
    ['h_bb22ZZ99aa11cc33dd', { face: null }],  // wall tile — opaque `h_` back
    [7, { face: undefined }],           // public discard — entitled, ⌊7/4⌋
    ['h_cc33WW88vv77uu66tt', null],     // tombstoned handle
  ];

  test('partition routes numeric + numeric-STRING → real (normalized to number), `h_` → hidden', () => {
    const { real, hidden } = partitionThingEntries(snapshot);
    // numeric-string "7" is normalized to the number 7 on the real path
    expect(real.map(e => e[0])).toEqual([42, 7, 7]);
    expect(hidden.map(e => e[0])).toEqual(['h_Qk9r3xM2ab12cd34ef', 'h_bb22ZZ99aa11cc33dd', 'h_cc33WW88vv77uu66tt']);
    // every real key is a NUMBER (never a numeric-string leaking through); every hidden key is a string
    expect(real.every(([k]) => typeof k === 'number')).toBe(true);
    expect(hidden.every(([k]) => typeof k === 'string')).toBe(true);
  });

  test('each key resolves to the correct render (opaque ⇒ back, entitled ⇒ face)', () => {
    expect(resolveTileFace(42, 10)).toEqual({ faceDown: false, faceIndex: 10 });      // entitled explicit
    expect(resolveTileFace(7, undefined)).toEqual({ faceDown: false, faceIndex: 1 }); // entitled ⌊7/4⌋
    expect(resolveTileFace('42', undefined)).toEqual({ faceDown: false, faceIndex: 10 }); // numeric-STRING ⌊42/4⌋
    expect(resolveTileFace('h_Qk9r3xM2ab12cd34ef', null)).toEqual({ faceDown: true, faceIndex: null });
    expect(resolveTileFace('h_bb22ZZ99aa11cc33dd', null)).toEqual({ faceDown: true, faceIndex: null });
  });

  test('empty + all-one-type batches partition cleanly', () => {
    expect(partitionThingEntries([])).toEqual({ real: [], hidden: [] });
    expect(partitionThingEntries([[0, 1], [107, 1]]).hidden).toEqual([]);
    expect(partitionThingEntries([['0', 1], ['107', 1]]).hidden).toEqual([]);  // numeric-strings ⇒ real
    expect(partitionThingEntries([['h_x1', 1], ['h_y2', 1]]).real).toEqual([]);
  });
});

test.describe('changsha-mode-policy — numeric local-thing indices never render as anonymous backs (marker@0 dup regression)', () => {
  // Regression for `slot not empty: 108 marker@0` (rejected candidate :18084).
  // Opaque hidden handles are STRING keys ONLY. A NUMERIC key is a local/real
  // thing INDEX: entitled tiles are 0..107, but world.sendUpdate ECHOES local
  // non-tile things back through onThings — the marker (index 2000) and the SC-2
  // hidden-back pool (108..215). If those numeric keys leak into the `hidden`
  // bucket, reconcileHiddenBackPool assigns an anonymous back to the slot they
  // name (e.g. `marker@0`), orphaning the real marker's slot pointer; the next
  // setup.replace then maps two things to marker@0 and throws.
  test('numeric keys above 107 (marker=2000, back-pool=108..215, sticks) route to real, NEVER hidden', () => {
    const { real, hidden } = partitionThingEntries([
      [108, { slotName: 'hiddenpool@0' }],   // SC-2 back-pool base
      [215, { slotName: 'hiddenpool@0' }],   // SC-2 back-pool top
      [2000, { slotName: 'marker@0' }],       // the local marker
      [1000, { slotName: 'tray.0.0@0' }],     // a local stick index
      ['h_wall55', { slotName: 'wall.0.0@0' }],
    ]);
    expect(hidden.map((e) => e[0])).toEqual(['h_wall55']);
    expect(real.map((e) => e[0]).sort((a, b) => a - b)).toEqual([108, 215, 1000, 2000]);
    expect(real.every(([k]) => typeof k === 'number')).toBe(true);
  });

  test('an echoed local marker cannot reach the anonymous-back reconcile → no marker@0 placement', () => {
    // The exact batch world.sendUpdate re-emits while SC-2 is active: a real
    // discard, a genuine hidden wall handle, and the echoed local marker.
    const echoed: Array<[string | number, { slotName: string } | null]> = [
      [7, { slotName: 'discard.0.0@0' }],
      ['h_wall9', { slotName: 'wall.4.1@2' }],
      [2000, { slotName: 'marker@0' }],
    ];
    const { hidden } = partitionThingEntries(echoed);
    const plan = reconcileHiddenBacks(
      hidden.map(([h, i]) => [h, i === null ? null : { slotName: i.slotName }]),
      [],
      false,
    );
    expect(hidden.some(([k]) => k === '2000')).toBe(false);
    expect(plan.place.some((p) => p.slotName === 'marker@0')).toBe(false);
  });

  test('mutant guard: the pre-fix routing (marker in hidden bucket) WOULD hijack marker@0', () => {
    // Prove this suite is non-vacuous: had the marker leaked into `hidden`, the
    // reconcile plan would target marker@0 — exactly the defect we removed.
    const buggyHidden: Array<[string, { slotName: string } | null]> = [['2000', { slotName: 'marker@0' }]];
    const plan = reconcileHiddenBacks(buggyHidden, [], false);
    expect(plan.place.some((p) => p.slotName === 'marker@0')).toBe(true);
  });
});

test.describe('changsha-mode-policy — SC-2 reconnect reconciliation', () => {
  type Info = { face?: number | null } | null;

  test('a stable per-player handle re-partitions + re-resolves IDENTICALLY across reconnect', () => {
    // Reconnect delivers a fresh FULL snapshot; the same opaque handle (stable
    // per player identity) must land in `hidden` and render as a back both times.
    const snap: Array<[string | number, Info]> = [[42, { face: 10 }], ['h:stable', { face: null }]];
    const first = partitionThingEntries(snap);
    const second = partitionThingEntries(snap); // simulated reconnect: same handle
    expect(second).toEqual(first);
    expect(resolveTileFace('h:stable', null)).toEqual(resolveTileFace('h:stable', null));
    expect(resolveTileFace('h:stable', null)).toEqual({ faceDown: true, faceIndex: null });
  });

  test('reveal transition: opaque back BEFORE, entitled real-id face AFTER (tombstone→real)', () => {
    // Before: hidden wall tile is an opaque handle ⇒ back (no identity).
    expect(resolveTileFace('h:draw', null)).toEqual({ faceDown: true, faceIndex: null });
    // Server tombstones the handle and emits the real id into the hand ⇒ face-up
    // from the entitled identity. The handle NEVER resolves to that identity.
    expect(resolveTileFace(40, 10)).toEqual({ faceDown: false, faceIndex: 10 });
  });
});

test.describe('changsha-mode-policy — FE-7/SC-2/G19 hidden-tile render (resolveTileFace)', () => {
  test('OPAQUE string handle ⇒ face-down, face NEVER derived from the key', () => {
    expect(resolveTileFace('h:9f3a2b7c', undefined)).toEqual({ faceDown: true, faceIndex: null });
    // even if a (spoofed) face rides along, an opaque key stays hidden
    expect(resolveTileFace('h:9f3a2b7c', 12)).toEqual({ faceDown: true, faceIndex: null });
  });

  test('entitled explicit strip (face === null) ⇒ face-down back', () => {
    expect(resolveTileFace(42, null)).toEqual({ faceDown: true, faceIndex: null });
  });

  test('entitled real tile with explicit face ⇒ face-up from the entitled face', () => {
    expect(resolveTileFace(42, 7)).toEqual({ faceDown: false, faceIndex: 7 });
  });

  test('entitled real tile, face omitted ⇒ type from the real tileId (⌊key/4⌋)', () => {
    expect(resolveTileFace(0, undefined)).toEqual({ faceDown: false, faceIndex: 0 });
    expect(resolveTileFace(7, undefined)).toEqual({ faceDown: false, faceIndex: 1 });
    expect(resolveTileFace(107, undefined)).toEqual({ faceDown: false, faceIndex: 26 });
    // numeric-STRING keys resolve identically (normalized before the ⌊id/4⌋)
    expect(resolveTileFace('0', undefined)).toEqual({ faceDown: false, faceIndex: 0 });
    expect(resolveTileFace('107', undefined)).toEqual({ faceDown: false, faceIndex: 26 });
  });
});

test.describe('changsha-mode-policy — FE-7/SC-2/G19 opaque detection (isOpaqueHandle / parseRealTileId)', () => {
  // Wire (Ripley 10:44): VISIBLE tiles keep numeric-STRING keys "0".."107"
  // (or the equivalent numbers); HIDDEN tiles are opaque `h_<22 base64url>`.
  // The discriminator is VALUE-based (int-parses to 0..107), NOT `typeof`.
  test('opaque `h_` handle ⇒ opaque; out-of-range number-like ⇒ opaque', () => {
    expect(isOpaqueHandle('h_Qk9r3xM2ab12cd34ef56g')).toBe(true);
    expect(isOpaqueHandle('h:deadbeef')).toBe(true);        // any non-numeric string
    expect(isOpaqueHandle('99999999')).toBe(true);          // numeric-string > 107 ⇒ opaque
    expect(isOpaqueHandle('108')).toBe(true);               // first index past the real range
    expect(isOpaqueHandle('-1')).toBe(true);                // negative ⇒ not a real id
  });

  test('numeric-STRING "0".."107" ⇒ REAL (NOT opaque — the key int-parses in range)', () => {
    expect(isOpaqueHandle('0')).toBe(false);                // ← corrected: "0" is a visible real tile
    expect(isOpaqueHandle('42')).toBe(false);
    expect(isOpaqueHandle('107')).toBe(false);
  });

  test('numeric key ⇒ real only within 0..107 (value-based, not a ≥10^7 threshold)', () => {
    expect(isOpaqueHandle(0)).toBe(false);
    expect(isOpaqueHandle(107)).toBe(false);
    expect(isOpaqueHandle(108)).toBe(true);                 // past the real range ⇒ opaque
    expect(isOpaqueHandle(10_000_000)).toBe(true);          // ← corrected: value-based, so > 107 ⇒ opaque
    expect(isOpaqueHandle(Number.MAX_SAFE_INTEGER)).toBe(true);
  });
});

test.describe('changsha-mode-policy — SC-2 real-id parse (parseRealTileId)', () => {
  test('number OR numeric-string 0..107 ⇒ the numeric id; else null', () => {
    expect(parseRealTileId(0)).toBe(0);
    expect(parseRealTileId(107)).toBe(107);
    expect(parseRealTileId('0')).toBe(0);                   // numeric-string normalizes to a number
    expect(parseRealTileId('42')).toBe(42);
    expect(parseRealTileId('107')).toBe(107);
  });

  test('opaque handles / out-of-range / malformed ⇒ null (fail closed, no arithmetic on a handle)', () => {
    expect(parseRealTileId('h_Qk9r3xM2ab12cd34ef56g')).toBeNull();
    expect(parseRealTileId('h:deadbeef')).toBeNull();
    expect(parseRealTileId('108')).toBeNull();
    expect(parseRealTileId(108)).toBeNull();
    expect(parseRealTileId('-1')).toBeNull();
    expect(parseRealTileId('4.5')).toBeNull();
    expect(parseRealTileId('12x')).toBeNull();
    expect(parseRealTileId('')).toBeNull();
  });
});

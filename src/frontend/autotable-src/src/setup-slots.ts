import { Euler, Quaternion, Vector2, Vector3 } from "three";
import { Slot } from "./slot";
import { Size, ThingType, GameType } from "./types";

const WORLD_SIZE = 174;
const CHANGSHA_WALL_STACKS = [14, 14, 13, 13];
const CHANGSHA_WALL_RUN = (CHANGSHA_WALL_STACKS.reduce((sum, count) => sum + count, 0)
  / CHANGSHA_WALL_STACKS.length - 1) * Size.TILE.x;
const WALL_CORNER_CONTACT = (Size.TILE.x + Size.TILE.y) / 2;

const Rotation = {
  FACE_UP: new Quaternion().setFromEuler(new Euler(0, 0, 0)),
  FACE_UP_SIDEWAYS: new Quaternion().setFromEuler(new Euler(0, 0, Math.PI / 2)),
  STANDING: new Quaternion().setFromEuler(new Euler(Math.PI / 2, 0, 0)),
  FACE_DOWN: new Quaternion().setFromEuler(new Euler(Math.PI, 0, 0)),
  FACE_DOWN_SIDEWAYS: new Quaternion().setFromEuler(new Euler(Math.PI, 0, Math.PI / 2)),
  FACE_DOWN_REVERSE: new Quaternion().setFromEuler(new Euler(Math.PI, 0, Math.PI)),
};

type SlotOp = (slots: Array<Slot>) => Array<Slot>;
type SlotGroup = Array<SlotOp>;

export function makeSlots(gameType: GameType): Array<Slot> {
  const slots = [];

  for (const group of SLOT_GROUPS[gameType]) {
    let current: Array<Slot> = [];
    for (const op of group) {
      current = op(current);
    }
    slots.push(...current);
  }

  fixupSlots(slots, gameType);
  return slots;
}

function start(name: string): SlotOp {
  return _ => [START[name]];
}

interface RepeatOptions {
  stack?: boolean;
  shift?: boolean;
  push?: boolean;
}

function repeat(count: number, offset: Vector3, options: RepeatOptions = {}): SlotOp {
  return (slots: Array<Slot>) => {
    const result: Array<Slot> = [];
    for (const slot of slots) {
      const totalOffset = new Vector3(0, 0, 0);
      for (let i = 0; i < count; i++) {
        const copied = slot.copy(`.${i}`);
        copied.origin = copied.origin.clone().add(totalOffset);
        copied.indexes.push(i);
        result.push(copied);
        totalOffset.add(offset);

        if (i < count - 1) {
          const next = `${slot.name}.${i+1}`;
          if (options.stack) copied.linkDesc.up = next;
          if (options.shift) copied.linkDesc.shiftRight = next;
          if (options.push) copied.linkDesc.push = next;
        }

        if (i > 0) {
          const prev = `${slot.name}.${i-1}`;
          if (options.stack) copied.linkDesc.down = prev;
          if (options.shift) copied.linkDesc.shiftLeft = prev;
        }
      }
    }
    return result;
  };
}

function row(count: number, dx?: number, options: RepeatOptions = {}): SlotOp {
  return repeat(count, new Vector3(dx ?? Size.TILE.x, 0, 0), options);
}

function column(count: number, dy?: number): SlotOp {
  return repeat(count, new Vector3(0, dy ?? Size.TILE.y, 0));
}

function stack(dz?: number): SlotOp {
  return repeat(2, new Vector3(0, 0, dz ?? Size.TILE.z), {stack: true});
}

function seats(which?: Array<number>): SlotOp {
  const seats = which ?? [0, 1, 2, 3];
  return (slots: Array<Slot>) => {
    const result: Array<Slot> = [];
    for (const seat of seats) {
      for (const slot of slots) {
        const copied = slot.copy(`@${seat}`);
        copied.rotate(seat, WORLD_SIZE);
        result.push(copied);
      }
    }
    return result;
  };
}

function addedKongSlots(slots: Array<Slot>): Array<Slot> {
  const upper = slots.filter(slot => slot.indexes[1] === 1).map(base => {
    const slot = base.copy('');
    slot.name = `meld.${base.indexes[0]}.4`;
    slot.indexes = [base.indexes[0], 4];
    slot.origin = base.origin.clone().add(new Vector3(0, 0, Size.TILE.z));
    // Do not give the base an up link: that would shade an ordinary Pung
    // as an exposed wall bottom. The added tile is not in the flat push chain.
    slot.linkDesc = { requires: base.name };
    return slot;
  });
  return [...slots, ...upper];
}

const START: Record<string, Slot> = {
  'hand': new Slot({
    name: 'hand',
    group: 'hand',
    origin: new Vector3(46, 0, 0),
    rotations: [Rotation.STANDING, Rotation.FACE_UP, Rotation.FACE_DOWN],
    canFlipMultiple: true,
    drawShadow: true,
    shadowRotation: 1,
    rotateHeld: true,
  }),

  'hand.3p': new Slot({
    name: 'hand',
    group: 'hand',
    origin: new Vector3(37, 0, 0),
    rotations: [Rotation.STANDING, Rotation.FACE_UP, Rotation.FACE_DOWN],
    canFlipMultiple: true,
    drawShadow: true,
    shadowRotation: 1,
    rotateHeld: true,
  }),

  'hand.extra': new Slot({
    name: `hand.extra`,
    group: `hand`,
    origin: new Vector3(
      46 + 14.5*Size.TILE.x,
      0,
      0,
    ),
    rotations: [Rotation.STANDING, Rotation.FACE_UP, Rotation.FACE_DOWN],
    canFlipMultiple: true,
    rotateHeld: true,
  }),

  'meld': new Slot({
    name: `meld`,
    group: `meld`,
    origin: new Vector3(174, 0, 0),
    direction: new Vector2(-1, 1),
    rotations: [Rotation.FACE_UP, Rotation.FACE_UP_SIDEWAYS, Rotation.FACE_DOWN],
  }),

  'kita': new Slot({
    name: `kita`,
    group: `kita`,
    origin: new Vector3(146, 0, 0),
    direction: new Vector2(-1, 1),
    rotations: [Rotation.FACE_UP],
  }),

  'wall': new Slot({
    name: 'wall',
    group: 'wall',
    origin: new Vector3(30, 20, 0),
    rotations: [Rotation.FACE_DOWN, Rotation.FACE_UP],
  }),

  // Centre anchoring makes origin and rendered-centre seam measurements agree.
  // Rotating this start gives a constant mean side run and a perpendicular
  // half-width + half-depth contact offset. Unequal side lengths contribute
  // +/- half a pitch along the seam, without intersecting adjacent wall bodies.
  'wall.cs': new Slot({
    name: 'wall',
    group: 'wall',
    origin: new Vector3(
      (WORLD_SIZE - CHANGSHA_WALL_RUN + WALL_CORNER_CONTACT) / 2,
      (WORLD_SIZE - CHANGSHA_WALL_RUN - WALL_CORNER_CONTACT) / 2,
      0,
    ),
    direction: new Vector2(0, 0),
    rotations: [Rotation.FACE_DOWN, Rotation.FACE_UP],
  }),

  'wall.demo': new Slot({
    name: 'wall',
    group: 'wall',
    origin: new Vector3(30, 20, 0),
    rotations: [Rotation.FACE_DOWN, Rotation.FACE_UP],
    canFlipMultiple: true,
  }),

  'wall.open': new Slot({
    name: 'wall.open',
    group: 'wall.open',
    origin: new Vector3(36, 14, 0),
    rotations: [Rotation.STANDING, Rotation.FACE_DOWN],
    canFlipMultiple: true,
  }),

  'discard': new Slot({
    name: `discard`,
    group: `discard`,
    origin: new Vector3(69, 60, 0),
    direction: new Vector2(1, 1),
    rotations: [Rotation.FACE_UP, Rotation.FACE_UP_SIDEWAYS, Rotation.FACE_DOWN, Rotation.FACE_DOWN_SIDEWAYS],
    drawShadow: true,
  }),

  // Keep the existing Changsha discard tray inside the wall. Real-asset
  // geometry tests check separation from every side and the opposite tray.
  'discard.cs': new Slot({
    name: `discard`,
    group: `discard`,
    origin: new Vector3(69, 77.25, 0),
    direction: new Vector2(1, 1),
    rotations: [Rotation.FACE_UP, Rotation.FACE_UP_SIDEWAYS, Rotation.FACE_DOWN, Rotation.FACE_DOWN_SIDEWAYS],
    drawShadow: true,
  }),

  'discard.extra': new Slot({
    name: `discard.extra`,
    group: `discard`,
    origin: new Vector3(69 + 6 * Size.TILE.x, 60 - 2 * Size.TILE.y, 0),
    direction: new Vector2(1, 1),
    rotations: [Rotation.FACE_UP, Rotation.FACE_UP_SIDEWAYS, Rotation.FACE_DOWN, Rotation.FACE_DOWN_SIDEWAYS],
  }),

  // Apone 2026-08-10 — Changsha discard overflow row, shifted inward with the
  // main tray (same +17.25 offset) so the rare >18-discard overflow also lands
  // inside the ring.  Backend never assigns `discard.extra` for Changsha (the
  // slot map tops out at discard.2.5@seat), so this only matters for parity /
  // defensive rendering; it stays relatively positioned to `discard.cs`.
  'discard.extra.cs': new Slot({
    name: `discard.extra`,
    group: `discard`,
    origin: new Vector3(69 + 6 * Size.TILE.x, 77.25 - 2 * Size.TILE.y, 0),
    direction: new Vector2(1, 1),
    rotations: [Rotation.FACE_UP, Rotation.FACE_UP_SIDEWAYS, Rotation.FACE_DOWN, Rotation.FACE_DOWN_SIDEWAYS],
  }),

  'tray': new Slot({
    name: `tray`,
    group: `tray`,
    type: ThingType.STICK,
    origin: new Vector3(15, -25, 0),
    rotations: [Rotation.FACE_UP],
  }),

  'payment': new Slot({
    name: 'payment',
    group: 'payment',
    type: ThingType.STICK,
    origin: new Vector3(42, 42, 0),
    rotations: [Rotation.FACE_UP_SIDEWAYS],
  }),

  'riichi': new Slot({
    name: 'riichi',
    group: 'riichi',
    type: ThingType.STICK,
    origin: new Vector3(
      (WORLD_SIZE - Size.STICK.x) / 2,
      71.5,
      1.5,
    ),
    rotations: [Rotation.FACE_UP],
  }),

  'marker': new Slot({
    name: 'marker',
    group: 'marker',
    type: ThingType.MARKER,
    origin: new Vector3(
      166, -8, 0,
    ),
    rotations: [Rotation.FACE_DOWN_REVERSE, Rotation.FACE_UP],
  }),
};

export const SLOT_GROUPS: Record<GameType, Array<SlotGroup>> = {
  // Phase F — Changsha keeps the 108-tile shape Hicks built in Phase B.  No
  // tray / payment / riichi sticks; Changsha scoring is server-authoritative
  // numeric units (Vasquez §1.14) and never renders sticks.
  //
  // Hicks 2026-06-01 round 2 — wall row count now matches backend exactly:
  // `AutotableSlotMap.WallStackCount` ships seats 0,1 → 14 stacks and seats
  // 2,3 → 13 stacks (28+28+26+26 = 108 tiles).  The legacy `row(19)` blanket
  // emitted phantom slots `wall.14..18@*` (seats 0,1) and `wall.13..18@*`
  // (seats 2,3) that the backend never wrote to — they showed up as the
  // visible mid-wall gap and the THREE.js `Computed radius is NaN` log
  // Vasquez captured at HEAD `dd2608d`.  Splitting the wall group per-seat
  // makes every emitted slot something the backend will populate.
  CHANGSHA: [
    [start('hand'), row(14, undefined, {shift: true}), seats()],
    [start('hand.extra'), seats()],
    [start('meld'), column(4), row(4, -Size.TILE.x, {push: true, shift: true}), addedKongSlots, seats()],
    ...CHANGSHA_WALL_STACKS.map((count, seat) =>
      [start('wall.cs'), row(count), stack(), seats([seat])]),
    [start('discard.cs'), column(3, -Size.TILE.y), row(6, undefined, {push: true}), seats()],
    [start('discard.extra.cs'), row(4, undefined, {push: true}), seats()],
  ],

  // Phase F — upstream Riichi 4-player, restored from commit 98d4cca^ verbatim.
  FOUR_PLAYER: [
    [start('hand'), row(14, undefined, {shift: true}), seats()],
    [start('hand.extra'), seats()],
    [start('meld'), column(4), row(4, -Size.TILE.x, {push: true, shift: true}), seats()],
    [start('wall'), row(19), stack(), seats()],
    [start('discard'), column(3, -Size.TILE.y), row(6, undefined, {push: true}), seats()],
    [start('discard.extra'), row(4, undefined, {push: true}), seats()],
    [start('tray'), row(6, 24), column(10, -3), seats()],
    [start('payment'), row(8, 3), seats()],
    [start('riichi'), seats()],
    [start('marker'), seats()],
  ],

  // Phase F — upstream 3-player Riichi (sanma).  Seats [0,1,2] only.
  THREE_PLAYER: [
    [start('hand.3p'), row(14, undefined, {shift: true}), seats([0, 1, 2])],
    [start('meld'), column(4), row(4, -Size.TILE.x, {push: true, shift: true}), seats([0, 1, 2])],
    [start('kita'), row(4, -Size.TILE.x, {shift: true}), seats([0, 1, 2])],
    [start('wall'), row(19), stack(), seats()],
    [start('discard'), column(3, -Size.TILE.y), row(6, undefined, {push: true}), seats([0, 1, 2])],
    [start('discard.extra'), row(4, undefined, {push: true}), seats([0, 1, 2])],
    [start('tray'), row(6, 24), column(10, -3), seats([0, 1, 2])],
    [start('payment'), row(8, 3), seats()],
    [start('riichi'), seats([0, 1, 2])],
    [start('marker'), seats([0, 1, 2])],
  ],

  // Phase F — upstream 2-player bamboo.  Seats [0,2] only.
  BAMBOO: [
    [start('hand'), row(14, undefined, {shift: true}), seats([0, 2])],
    [start('hand.extra'), seats([0, 2])],
    [start('meld'), column(4), row(4, -Size.TILE.x, {push: true, shift: true}), seats([0, 2])],
    [start('wall'), row(19), stack(), seats([0, 2])],
    [start('discard'), column(3, -Size.TILE.y), row(6, undefined, {push: true}), seats([0, 2])],
    [start('tray'), row(6, 24), column(10, -3), seats([0, 2])],
    [start('payment'), row(8, 3), seats()],
    [start('riichi'), seats([0, 2])],
    [start('marker'), seats([0, 2])],
  ],

  // Phase F — upstream 2-player minefield.  Seats [0,2] hold open hands /
  // seats [1,3] hold closed walls.
  MINEFIELD: [
    [start('hand'), row(13, undefined, {shift: true}), seats([0, 2])],
    [start('wall'), row(19), stack(), seats([1, 3])],
    [start('wall.open'), column(2, Size.TILE.y * 1.6), row(17, undefined, {shift: true}), seats([0, 2])],
    [start('discard'), column(3, -Size.TILE.y), row(6, undefined, {push: true}), seats([0, 2])],
    [start('tray'), row(6, 24), column(10, -3), seats([0, 2])],
    [start('payment'), row(8, 3), seats()],
    [start('riichi'), seats([0, 2])],
    [start('marker'), seats([0, 2])],
  ],
};

function fixupSlots(slots: Array<Slot>, gameType: GameType): void {
  for (const slot of slots) {
    if (slot.name.startsWith('discard.extra')) {
      slot.linkDesc.requires = `discard.2.5@${slot.seat}`;
    }
    if (slot.name.startsWith('discard.2.5')) {
      slot.linkDesc.push = `discard.extra.0@${slot.seat}`;
    }
    if (slot.group === 'wall' && slot.indexes[1] === 0) {
      // Drop-shadow under interior wall columns only (skip the two end
      // columns so the wall-end miter doesn't bleed shadow off the table).
      // Hicks 2026-06-01 round 2 — per-seat last-col index now reflects the
      // variant-aware wall depth: Changsha uses 14 stacks for seats 0,1 and
      // 13 stacks for seats 2,3 (canonical 28+28+26+26 = 108 layout).
      let lastCol: number;
      if (gameType === GameType.CHANGSHA) {
        lastCol = slot.seat === 0 || slot.seat === 1 ? 13 : 12;
      } else {
        lastCol = 18;
      }
      if (slot.indexes[0] !== 0 && slot.indexes[0] !== lastCol) {
        slot.drawShadow = true;
      }
    }
    if (slot.group === 'meld' && slot.indexes[0] > 0 && slot.indexes[1] < 4) {
      slot.linkDesc.requires = `meld.${slot.indexes[0]-1}.1@${slot.seat}`;
    }
    if (slot.group === 'wall.open' && slot.indexes[0] === 1 && slot.indexes[1] === 16) {
      slot.linkDesc.shiftRight = `wall.open.0.0@${slot.seat}`;
    }
    if (slot.group === 'wall.open' && slot.indexes[0] === 0 && slot.indexes[1] === 0) {
      slot.linkDesc.shiftLeft = `wall.open.1.16@${slot.seat}`;
    }
  }
}

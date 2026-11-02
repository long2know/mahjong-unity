import { Quaternion, Vector3 } from "three";
import { shuffle } from "./utils";
import { Conditions, DealType, ThingType, GameType, Points, GAME_TYPES } from "./types";
import { DEALS, DealPart, POINTS } from "./setup-deal";
import { makeSlots } from "./setup-slots";
import { Slot } from "./slot";
import { Thing } from "./thing";


export class Setup {
  slots: Map<string, Slot> = new Map();
  slotNames: Array<string> = [];
  things: Map<number, Thing> = new Map();
  counters: Map<ThingType, number> = new Map();
  start: Record<ThingType, number> = {
    'TILE': 0,
    'STICK': 1000,
    'MARKER': 2000,
  };
  pushes: Array<[Slot, Slot]> = [];
  conditions!: Conditions;

  /** Name of the off-table park slot (see {@link hiddenParkSlot}). */
  static readonly HIDDEN_PARK = 'hiddenpool@0';

  // FE-7 / SC-2 — the single off-table park slot every non-rendered Thing lives
  // in (World's anonymous hidden-back pool plus every REAL tile the viewer is not
  // entitled to see). It is OWNED by Setup — not lazily created by World — for
  // two invariants:
  //
  //   1. Ordering: the park slot exists from construction, so a snapshot can
  //      never reslot a Thing into a slot that doesn't exist yet.
  //   2. Lifetime: `addSlots()` rebuilds `this.slots` from `makeSlots()` on every
  //      variant/conditions change, which used to DESTROY a World-registered park
  //      slot while 108 Things still pointed at it — `replace()` then threw
  //      "trying to move thing to slot hiddenpool@0, but it doesn't exist",
  //      abandoning the scene mid-rebuild (things pointing at a dead slot
  //      generation, asymmetric slot.thing pointers, dropped WS collection
  //      updates). Re-registering this SAME instance on every rebuild keeps
  //      `thing.slot` identity stable across rebuilds.
  //
  // It is off-table (`offTable`) so it is never a drop target, is excluded from
  // `slotNames` so no deal range can walk into it, and is intentionally
  // MULTI-TENANT: many parked Things share it and its `.thing` back-pointer is
  // not authoritative (parked Things render nothing).
  private readonly hiddenPark: Slot = new Slot({
    name: Setup.HIDDEN_PARK,
    group: 'hiddenpool',
    origin: new Vector3(0, 0, -100000),
    rotations: [new Quaternion()],
    offTable: true,
  });

  /** The stable off-table park slot; always present in {@link slots}. */
  get hiddenParkSlot(): Slot {
    return this.hiddenPark;
  }

  /** True when `slot` is the multi-tenant off-table park slot. */
  isHiddenPark(slot: Slot | undefined | null): boolean {
    return slot === this.hiddenPark;
  }

  setup(conditions: Conditions): void {
    this.conditions = conditions;

    this.addSlots(conditions.gameType);
    this.addTiles(conditions);
    if (conditions.gameType !== GameType.CHANGSHA) {
      this.addSticks(conditions.gameType, conditions.points);
      this.addMarker();
    }
    this.deal(0);
  }

  private wallSlots(): Array<Slot> {
    return [...this.slots.values()].filter(
      slot => slot.name.startsWith('wall'));
  }

  private addTiles(conditions: Conditions): void {
    const wallSlots = this.wallSlots().map(slot => slot.name);
    this.maybeShuffle(wallSlots, conditions);
    let j = 0;
    // Phase F — variant-aware tile catalog.  Changsha runs the Phase B
    // 108-tile loop (3 suits × 9 ranks × 4 copies).  Upstream variants run
    // the original 136-tile loop with fives/back/honors/bamboo/3p filters.
    const tileLimit = conditions.gameType === GameType.CHANGSHA ? 108 : 136;
    for (let i = 0; i < tileLimit; i++) {
      const tileIndex = this.tileIndex(i, conditions);
      if (tileIndex !== null) {
        this.addThing(ThingType.TILE, tileIndex, wallSlots[j++]);
      }
    }
  }

  private maybeShuffle<T>(array: Array<T>, conditions?: Conditions): void {
    if ((conditions ?? this.conditions).dealType !== DealType.UNSHUFFLED) {
      shuffle(array);
    }
  }

  /**
   * Replace the scene for a new {@link Conditions}.
   *
   * Phase B narrowed this to a Changsha-only no-stick variant.  Phase F
   * restores the upstream-shaped optional `replacePoints` parameter — Changsha
   * still doesn't have sticks, but the four restored upstream variants do, and
   * the bundle's local "Reset points" handler needs to force a stick refresh
   * even when only `points` changed.
   */
  replace(conditions: Conditions, replacePoints: boolean = false): void {
    const wasChangsha = this.conditions.gameType === GameType.CHANGSHA;
    const isChangsha = conditions.gameType === GameType.CHANGSHA;

    const whatReplace: Record<ThingType, boolean> = {
      TILE: (
        conditions.gameType !== this.conditions.gameType ||
        conditions.back !== this.conditions.back ||
        conditions.fives !== this.conditions.fives
      ),
      STICK: !isChangsha && (
        wasChangsha ||
        replacePoints ||
        conditions.gameType !== this.conditions.gameType ||
        conditions.points !== this.conditions.points
      ),
      MARKER: conditions.gameType !== this.conditions.gameType,
    };

    const map = new Map<number, string>();
    for (const thing of [...this.things.values()]) {
      thing.prepareMove();
      // Phase F — when switching INTO Changsha from a stick-bearing variant,
      // sticks have no home slots (Changsha SLOT_GROUPS omits tray / payment
      // / riichi). Drop them outright so we don't try to re-place them.
      if (whatReplace[thing.type] || (isChangsha && thing.type === ThingType.STICK)) {
        this.things.delete(thing.index);
      } else {
        map.set(thing.index, thing.slot.name);
      }
    }
    this.addSlots(conditions.gameType);
    if (whatReplace.TILE) {
      this.counters.set(ThingType.TILE, 0);
      this.addTiles(conditions);
    }
    if (whatReplace.STICK) {
      this.counters.set(ThingType.STICK, 0);
      this.addSticks(conditions.gameType, conditions.points);
    }
    if (whatReplace.MARKER) {
      this.counters.set(ThingType.MARKER, 0);
      if (!isChangsha) this.addMarker();
    }

    for (const thing of this.things.values()) {
      if (!whatReplace[thing.type]) {
        const slotName = map.get(thing.index);
        if (slotName === undefined) {
          throw `couldn't recover slot name for thing ${thing.index}`;
        }
        const slot = this.slots.get(slotName);
        if (slot === undefined) {
          throw `trying to move thing to slot ${slotName}, but it doesn't exist`;
        }
        // The park slot is multi-tenant and off-table: every parked Thing keeps
        // it as its home across the rebuild. `moveTo`'s single-occupant contract
        // (and its "slot not empty" throw) does not apply here.
        if (this.isHiddenPark(slot)) {
          thing.slot = slot;
          continue;
        }
        thing.moveTo(slot, thing.rotationIndex);
      }
    }
    this.conditions = conditions;
  }

  private tileIndex(i: number, conditions: Conditions): number | null {
    // Phase F — Changsha keeps Phase B's logical-tile formula: floor(i/4)
    // over 108 tiles → ids 0..26 (3 suits × 9 ranks × 4 copies). No honors,
    // no red fives, no back-colour cycling, no bamboo/3p suit filtering.
    if (conditions.gameType === GameType.CHANGSHA) {
      return Math.floor(i / 4);
    }

    // Upstream 136-tile path, restored verbatim from 98d4cca^.
    let tileIndex = Math.floor(i / 4);

    if (conditions.fives !== '000') {
      if (tileIndex === 4 && i % 4 === 0) {
        tileIndex = 34;
      } else if (tileIndex === 13 &&
          (i % 4 === 0 || (i % 4 === 1 && conditions.fives === '121'))) {
        tileIndex = 35;
      } else if (tileIndex === 22 && i % 4 === 0) {
        tileIndex = 36;
      }
    }

    if (conditions.gameType === GameType.BAMBOO) {
      if (!((18 <= tileIndex && tileIndex < 27) || tileIndex === 36)) {
        return null;
      }
    }

    if (conditions.gameType === GameType.THREE_PLAYER) {
      if ((1 <= tileIndex && tileIndex < 8) || tileIndex === 34) {
        return null;
      }
    }

    tileIndex += 37 * conditions.back;
    return tileIndex;
  }

  deal(seat: number): [number, number] {
    const gameType = this.conditions.gameType;
    const dealType = this.conditions.dealType;

    const dice: [number, number] = [
      Math.floor(Math.random() * 6 + 1),
      Math.floor(Math.random() * 6 + 1)
    ];

    if (GAME_TYPES[gameType].seats.indexOf(seat) === -1) {
      seat = 0;
    }

    const dealParts = DEALS[gameType][dealType];
    if (dealParts === undefined) {
      // Phase F — silent no-op when the variant doesn't define this dealType.
      // E.g. MINEFIELD has no INITIAL deal; auto-deal in manual mode is also
      // an explicit no-op (backend will drive placement).
      return dice;
    }

    const tiles = [...this.things.values()].filter(thing => thing.type === ThingType.TILE);
    for (const thing of tiles) {
      thing.prepareMove();
    }

    this.maybeShuffle(tiles);

    const roll = dice[0] + dice[1];
    for (const part of dealParts) {
      this.dealPart(part, tiles, roll, seat);
    }

    if (tiles.length !== 0) {
      throw `bad deal: ${tiles.length} remaining`;
    }

    return dice;
  }

  usesDice(): boolean {
    const gameType = this.conditions.gameType;
    const dealType = this.conditions.dealType;
    const dealParts = DEALS[gameType][dealType];
    if (!dealParts) return false;
    for (const part of dealParts) {
      if (part.roll) return true;
    }
    return false;
  }

  private dealPart(dealPart: DealPart, tiles: Array<Thing>, roll: number, seat: number): void {
    if (dealPart.roll !== undefined && dealPart.roll !== roll) {
      return;
    }
    if (dealPart.tiles !== undefined) {
      const searched = [...dealPart.tiles];
      this.maybeShuffle(searched);

      for (let i = 0; i < searched.length; i++) {
        // HACK: typeIndex includes back color (upstream Riichi only).
        const idx = tiles.findIndex(tile =>
          (tile.typeIndex === searched[i] || tile.typeIndex === searched[i] + 37));
        if (idx === -1) {
          throw `not found: ${searched[i]}`;
        }
        const targetIdx = tiles.length - i - 1;
        const temp = tiles[targetIdx];
        tiles[targetIdx] = tiles[idx];
        tiles[idx] = temp;
      }
    }

    for (const [slotName, slotSeat, n] of dealPart.ranges) {
      if (tiles.length < n) {
        throw `tile underflow at ${slotName}`;
      }

      const idx = this.slotNames.indexOf(slotName);
      if (idx === -1) {
        throw `slot not found: ${slotName}`;
      }
      const effectiveSeat = dealPart.absolute ? slotSeat : (slotSeat + seat) % 4;
      for (let i = idx; i < idx + n; i++) {
        const targetSlotName = this.slotNames[i] + '@' + effectiveSeat;
        const slot = this.slots.get(targetSlotName);
        if (slot === undefined) {
          throw `slot not found: ${targetSlotName}`;
        }
        if (slot.thing !== null) {
          throw `slot occupied: ${targetSlotName}`;
        }

        const thing = tiles.pop()!;
        thing.moveTo(slot, dealPart.rotationIndex);
      }
    }
  }

  // Phase F — restored from upstream 98d4cca^.  Renders point sticks in the
  // per-seat trays for Riichi variants.  Changsha intentionally skips this
  // path (server-authoritative numeric scoring per Vasquez §1.14).
  private addSticks(gameType: GameType, points: Points): void {
    if (gameType === GameType.CHANGSHA) {
      return; // Changsha has no sticks — defensive guard.
    }
    const seats = GAME_TYPES[gameType].seats;
    const add = (index: number, n: number, slot: number): void => {
      for (const seat of seats) {
        for (let j = 0; j < n; j++) {
          this.addThing(ThingType.STICK, index, `tray.${slot}.${j}@${seat}`);
        }
      }
    };

    add(5, POINTS[points][0], 0); // -10k debt
    add(4, POINTS[points][1], 1); // 10k
    add(3, POINTS[points][2], 2); // 5k
    add(2, POINTS[points][3], 3); // 1k
    add(1, POINTS[points][4], 4); // 500
    add(0, POINTS[points][5], 5); // 100
  }

  private addMarker(): void {
    this.addThing(ThingType.MARKER, 0, 'marker@0');
  }

  private addThing(
    type: ThingType,
    typeIndex: number,
    slotName: string,
    rotationIndex?: number
  ): void {
    if (this.slots.get(slotName) === undefined) {
      throw `Unknown slot: ${slotName}`;
    }

    const counter = this.counters.get(type) ?? 0;
    this.counters.set(type, counter + 1);
    const thingIndex = this.start[type] + counter;
    const slot = this.slots.get(slotName)!;

    const thing = new Thing(thingIndex, type, typeIndex, slot);
    this.things.set(thingIndex, thing);
    if (rotationIndex !== undefined) {
      thing.rotationIndex = rotationIndex;
    }
  }

  private addSlots(gameType: GameType): void {
    this.slots.clear();
    this.slotNames.splice(0);
    this.pushes.splice(0);

    const slotNames: Set<string> = new Set();
    for (const slot of makeSlots(gameType)) {
      this.slots.set(slot.name, slot);
      const shortName = slot.name.replace(/@.*/, '');
      if (!slotNames.has(shortName)) {
        slotNames.add(shortName);
      }
    }
    // Re-register the SAME off-table park slot instance after every rebuild so
    // parked Things (SC-2 hidden backs + concealed reals) always resolve to a
    // live slot. Deliberately kept out of `slotNames`: it is not a deal target
    // and must never be walked by a `dealPart` range.
    this.slots.set(this.hiddenPark.name, this.hiddenPark);
    this.slotNames.push(...slotNames.values());
    Slot.setLinks(this.slots);

    this.pushes.push(...Slot.computePushes([...this.slots.values()]));
  }

  // Phase F — variant-aware score readout.
  //   • Changsha — server-authoritative numeric units (Vasquez §1.14); we
  //     return all nulls and center.ts skips the score draw.
  //   • Upstream variants — sum stick values per seat (restored from
  //     98d4cca^), so the Riichi UI renders real point totals.
  getScores(): Array<number | null> {
    if (this.conditions.gameType === GameType.CHANGSHA) {
      return [null, null, null, null, null];
    }

    const scores = new Array(4).fill(-20000);
    scores.push((25000 + 20000) * 4); // remaining bank
    const stickScores = [100, 500, 1000, 5000, 10000, 10000];

    for (const slot of this.slots.values()) {
      if (slot.group === 'tray' && slot.thing !== null) {
        const score = stickScores[slot.thing.typeIndex];
        scores[slot.seat!] += score;
        scores[4] -= score;
      }
    }

    const result = new Array(4).fill(null);
    for (const seat of GAME_TYPES[this.conditions.gameType].seats) {
      result[seat] = scores[seat];
    }
    return result;
  }
}

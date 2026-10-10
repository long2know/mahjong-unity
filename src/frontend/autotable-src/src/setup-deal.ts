import { DealType, GameType, Points } from "./types";

type DealRange = [string, 0 | 1 | 2 | 3, number];

export interface DealPart {
  roll?: number;
  tiles?: Array<number>;
  rotationIndex?: number;
  ranges: Array<DealRange>;
  absolute?: boolean;
}

// Phase F — variant-aware deal tables.  Changsha keeps the Phase B 108-tile
// shape (no dice-conditional break point — backend drives placement
// authoritatively).  The four upstream variants restore the original
// dice-roll-conditional break logic from pwmarcz/autotable commit 98d4cca^,
// verbatim, so relay mode is byte-identical to upstream.
export const DEALS: Record<GameType, Partial<Record<DealType, Array<DealPart>>>> = {
  // -------------------------------------------------------------------
  // Changsha (Phase B baseline).
  //
  // Wall layout: 14/14/13/13 stacks per seat (28/28/26/26 tiles) = 108 total.
  // HANDS deals 13 to each seat + 1 extra to the dealer (hand.extra@0).
  // Dealer's 14th tile counts as the first draw.
  //
  // INITIAL also supplies the local pre-snapshot wall in both deal modes.
  // Its ranges must begin at canonical column0; server snapshots subsequently
  // own all real dealing and pickup, rather than these local table recipes.
  // -------------------------------------------------------------------
  CHANGSHA: {
    INITIAL: [
      {
        ranges: [
          ['wall.0.0', 0, 28],
          ['wall.0.0', 1, 28],
          ['wall.0.0', 2, 26],
          ['wall.0.0', 3, 26],
        ],
      },
    ],
    HANDS: [
      {
        ranges: [
          ['hand.0', 0, 13],
          ['hand.0', 1, 13],
          ['hand.0', 2, 13],
          ['hand.0', 3, 13],
          ['hand.extra', 0, 1],
        ],
        rotationIndex: 2,
      },
      {
        ranges: [
          ['wall.1.0', 0, 14],
          ['wall.1.0', 1, 15],
          ['wall.0.0', 2, 13],
          ['wall.0.0', 3, 13],
        ],
      },
    ],
    UNSHUFFLED: [
      {
        ranges: [
          ['wall.0.0', 0, 28],
          ['wall.0.0', 1, 28],
          ['wall.0.0', 2, 26],
          ['wall.0.0', 3, 26],
        ],
      },
    ],
  },

  // -------------------------------------------------------------------
  // Upstream Riichi 4-player — restored verbatim from 98d4cca^.
  // -------------------------------------------------------------------
  FOUR_PLAYER: {
    INITIAL: [
      {
        ranges: [
          ['wall.1.0', 0, 34],
          ['wall.1.0', 1, 34],
          ['wall.1.0', 2, 34],
          ['wall.1.0', 3, 34],
        ],
      },
    ],
    WINDS: [
      {
        tiles: [27, 28, 29, 30],
        ranges: [['hand.5', 0, 4]],
        rotationIndex: 2,
      },
      {
        ranges: [
          ['wall.1.0', 0, 32],
          ['wall.1.0', 1, 34],
          ['wall.1.0', 2, 32],
          ['wall.1.0', 3, 34],
        ],
      },
    ],
    HANDS: [
      {
        ranges: [
          ['hand.0', 0, 13],
          ['hand.0', 1, 13],
          ['hand.0', 2, 13],
          ['hand.0', 3, 13],
        ],
        rotationIndex: 2,
      },

      { roll: 2,  ranges: [['wall.16.0', 1, 4],  ['wall.0.0', 2, 10], ['wall.6.0', 2, 24], ['wall.1.0', 3, 34], ['wall.1.0', 0, 12]] },
      { roll: 3,  ranges: [['wall.15.0', 2, 6],  ['wall.0.0', 3, 8],  ['wall.5.0', 3, 26], ['wall.1.0', 0, 34], ['wall.1.0', 1, 10]] },
      { roll: 4,  ranges: [['wall.14.0', 3, 8],  ['wall.0.0', 0, 6],  ['wall.4.0', 0, 28], ['wall.1.0', 1, 34], ['wall.1.0', 2, 8]]  },
      { roll: 5,  ranges: [['wall.13.0', 0, 10], ['wall.0.0', 1, 4],  ['wall.3.0', 1, 30], ['wall.1.0', 2, 34], ['wall.1.0', 3, 6]]  },
      { roll: 6,  ranges: [['wall.12.0', 1, 12], ['wall.0.0', 2, 2],  ['wall.2.0', 2, 32], ['wall.1.0', 3, 34], ['wall.1.0', 0, 4]]  },
      { roll: 7,  ranges: [['wall.11.0', 2, 14], ['wall.1.0', 3, 34], ['wall.1.0', 0, 34], ['wall.1.0', 1, 2]]                       },
      { roll: 8,  ranges: [['wall.9.0',  3, 14], ['wall.17.0', 3, 2], ['wall.1.0', 0, 34], ['wall.1.0', 1, 34]]                      },
      { roll: 9,  ranges: [['wall.8.0',  0, 14], ['wall.16.0', 0, 4], ['wall.1.0', 1, 34], ['wall.1.0', 2, 32]]                      },
      { roll: 10, ranges: [['wall.7.0',  1, 14], ['wall.15.0', 1, 6], ['wall.1.0', 2, 34], ['wall.1.0', 3, 30]]                      },
      { roll: 11, ranges: [['wall.6.0',  2, 14], ['wall.14.0', 2, 8], ['wall.1.0', 3, 34], ['wall.1.0', 0, 28]]                      },
      { roll: 12, ranges: [['wall.5.0',  3, 14], ['wall.13.0', 3, 10],['wall.1.0', 0, 34], ['wall.1.0', 1, 26]]                      },
    ],
  },

  // -------------------------------------------------------------------
  // Upstream Riichi 3-player (sanma).
  // -------------------------------------------------------------------
  THREE_PLAYER: {
    INITIAL: [
      {
        ranges: [
          ['wall.3.0', 0, 28],
          ['wall.3.0', 1, 26],
          ['wall.2.0', 2, 28],
          ['wall.3.0', 3, 26],
        ],
        absolute: true,
      },
    ],
    WINDS: [
      {
        tiles: [27, 28, 29],
        ranges: [['hand.6', 0, 3]],
        rotationIndex: 2,
      },
      {
        ranges: [
          ['wall.3.0', 0, 28],
          ['wall.3.0', 1, 26],
          ['wall.2.0', 2, 28],
          ['wall.3.0', 3, 23],
        ],
        absolute: true,
      },
    ],
    HANDS: [
      {
        ranges: [
          ['hand.0', 0, 13],
          ['hand.0', 1, 13],
          ['hand.0', 2, 13],
        ],
        rotationIndex: 2,
        absolute: true,
      },
      {
        ranges: [
          ['wall.10.0', 0, 14],
          ['wall.3.0',  1, 26],
          ['wall.2.0',  2, 29],
        ],
      },
    ],
  },

  // -------------------------------------------------------------------
  // Upstream 2-player bamboo.
  // -------------------------------------------------------------------
  BAMBOO: {
    INITIAL: [{ ranges: [['wall.1.0', 0, 36]], absolute: true }],
    WINDS: [
      {
        tiles: [18, 26],
        ranges: [['hand.6', 0, 2]],
        rotationIndex: 2,
      },
      {
        ranges: [['wall.1.0', 0, 34]],
      },
    ],
    HANDS: [
      {
        ranges: [
          ['hand.0', 0, 13],
          ['hand.0', 2, 13],
        ],
        rotationIndex: 2,
      },
      {
        ranges: [['wall.1.0', 0, 10]],
      },
    ],
  },

  // -------------------------------------------------------------------
  // Upstream 2-player minefield.
  // -------------------------------------------------------------------
  MINEFIELD: {
    HANDS: [
      {
        ranges: [
          ['wall.1.0', 1, 34],
          ['wall.1.0', 3, 34],
        ],
      },
      {
        ranges: [
          ['wall.open.0.0', 0, 17],
          ['wall.open.1.0', 0, 17],
          ['wall.open.0.0', 2, 17],
          ['wall.open.1.0', 2, 17],
        ],
        rotationIndex: 1,
      },
    ],
  },
};

// Phase F — restored from upstream 98d4cca^.  Drives Setup.addSticks() for
// non-Changsha variants.  Changsha never invokes this table (it renders no
// sticks per Vasquez §1.14).
//
// Tuple order: [-10k, 10k, 5k, 1k, 500, 100] stick counts per seat.
export const POINTS: Record<Points, Array<number>> = {
  '25':  [2, 1, 2, 4, 1, 5],
  '30':  [2, 1, 3, 4, 1, 5],
  '35':  [2, 2, 2, 4, 1, 5],
  '40':  [2, 2, 3, 4, 1, 5],
  '100': [2, 7, 5, 4, 1, 5],
};

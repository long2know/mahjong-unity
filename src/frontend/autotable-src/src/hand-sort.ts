export type HandSortMode = 'suit' | 'groups';

export interface HandTile {
  id: number;
  face: number;
  slotName: string;
}

export interface HandCandidate {
  id: string | number;
  slotName: string;
  face?: number | null;
  knownFace?: number;
  hidden?: boolean;
  confirmed: boolean;
}

export function handSortMode(value: unknown): HandSortMode {
  return value === 'groups' ? 'groups' : 'suit';
}

/** Only already-revealed, authoritative own tiles enter the presentation. */
export function ownHandTiles(candidates: readonly HandCandidate[], seat: number | null): HandTile[] {
  if (seat === null) return [];
  const hand = new RegExp(`^hand\\.\\d+@${seat}$`);
  const result: HandTile[] = [];
  for (const tile of candidates) {
    // Entitled numeric entries can omit face; their existing rendered catalog
    // face is known. An opaque handle or an explicit null must never use it.
    const face = tile.face === null ? null : tile.knownFace ?? tile.face;
    if (tile.confirmed && !tile.hidden && typeof tile.id === 'number'
      && Number.isInteger(tile.id) && hand.test(tile.slotName)
      && typeof face === 'number' && Number.isInteger(face) && face >= 0 && face < 27) {
      result.push({ id: tile.id, face, slotName: tile.slotName });
    }
  }
  return result;
}

export function sortHand(tiles: readonly HandTile[], mode: HandSortMode): HandTile[] {
  const counts = new Map<number, number>();
  for (const tile of tiles) counts.set(tile.face, (counts.get(tile.face) ?? 0) + 1);
  return [...tiles].sort((a, b) => {
    if (mode === 'groups') {
      const grouped = Number((counts.get(b.face) ?? 0) > 1) - Number((counts.get(a.face) ?? 0) > 1);
      if (grouped !== 0) return grouped;
    }
    return a.face - b.face || a.id - b.id;
  });
}

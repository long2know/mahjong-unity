// #147 (Hicks) — pure, dependency-free hand-size accounting for discard
// readiness.  Extracted from `World.hasExtraHandTile()` so every meld variant
// (Pung / Chow / exposed·concealed·added Kong) can be covered by a deterministic
// contract test without a browser or a full `World`/three.js graph.

/**
 * The minimal authoritative view of one rendered tile's slot that discard
 * readiness needs: its slot `group`, owning `seat`, slot `name`, and whether
 * the slot still authoritatively owns this tile (`ownsSlot === slot.thing ===
 * thing`) — orphaned local-deal residues have `ownsSlot === false`.
 */
export interface HandSlotView {
  group: string;
  seat: number | null;
  name: string;
  ownsSlot: boolean;
}

/**
 * True when `seat` holds an extra tile it must discard to continue its turn.
 *
 * Correct mahjong hand-size accounting: a "rest" hand is 13 tiles =
 * concealed + 3 per meld.  Every meld — Pung, Chow, or ANY Kong — counts as 3
 * toward the 13 (a Kong's 4th physical tile is offset by its replacement
 * draw).  The seat owes a discard exactly when that total exceeds 13, which
 * holds for a normal draw (14 concealed + 0 melds) AND every post-meld case
 * (11 + 3, 8 + 6, …), and is exactly 13 (→ false) at rest, so the caller's
 * click-to-discard intercept only fires on the seat's own turn.
 *
 * In server-authoritative Changsha (`meldAware === true`) exposed/concealed
 * melds live in `meld.{m}.{t}@{seat}` slots — distinct `{m}` indices are the
 * meld count.  Relay variants have no rules engine and drive melds by free
 * drag, so they pass `meldAware === false` and count concealed tiles only
 * (exact upstream behaviour).
 *
 * @param entries  every rendered tile's slot view (the caller filters by seat)
 * @param seat     the local seat
 * @param meldAware count melds toward the total (Changsha only)
 */
export function hasExtraDiscardTile(
  entries: Iterable<HandSlotView>,
  seat: number,
  meldAware: boolean,
): boolean {
  let handCount = 0;
  const meldIndices = new Set<string>();
  for (const entry of entries) {
    // Count only backend-authoritative tiles this seat's slot still owns; skip
    // orphans whose `.slot` points at a slot since re-bound to another tile.
    if (entry.seat !== seat || !entry.ownsSlot) continue;

    if (entry.group === 'hand') {
      // Skip the frontend-only `hand.extra@N` local-deal preview slot so a
      // pre-deal preview tile never trips the intercept.
      if (!entry.name.startsWith('hand.extra@')) handCount++;
    } else if (meldAware && entry.group === 'meld') {
      // meld slot name: `meld.{m}.{t}@{seat}` — the `{m}` index identifies the
      // meld, so distinct `{m}` values give the meld count (each counts as 3).
      const meldIndex = entry.name.split('.')[1];
      if (meldIndex !== undefined && meldIndex !== '') meldIndices.add(meldIndex);
    }
  }

  return handCount + 3 * meldIndices.size > 13;
}

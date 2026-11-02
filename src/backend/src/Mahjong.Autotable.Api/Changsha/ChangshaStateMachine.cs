using Mahjong.Autotable.Api.Changsha.Replay;

namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Pure-functional event-sourced state machine for Changsha Mahjong.
/// Transitions: (state, command) → (newState, events[])
///
/// States per Vasquez §7:
///   SEATING → ROLLING_DICE → DEALING → AWAITING_DISCARD → AWAITING_CLAIM →
///   SCORING → END_HAND → ROTATING_BANKER → (back to ROLLING_DICE or END_GAME)
/// </summary>
public sealed class ChangshaGameStateMachine
{
    private const int SeatCount = 4;
    private const int TotalHands = 16;
    private const int HandsPerRound = 4;
    internal const string RngAlgorithmId = "fisher-yates-changsha-v1";

    // ── Factory ────────────────────────────────────────────────────

    public static (ChangshaGameState State, List<ChangshaEvent> Events) CreateGame(
        int seed,
        int[]? botSeatIndexes = null,
        int baseUnit = 1)
        => CreateGame(seed, botSeatIndexes, baseUnit, null, null);

    internal static (ChangshaGameState State, List<ChangshaEvent> Events) CreateGame(
        int seed, int[]? botSeatIndexes, int baseUnit, ChangshaReplayContext? replay, string? gameId)
    {
        ChangshaBaseUnit.Validate(baseUnit);
        var state = new ChangshaGameState { Seed = seed, BaseUnit = baseUnit };
        if (gameId is not null) state.GameId = gameId;
        var events = new List<ChangshaEvent>();

        var bots = botSeatIndexes ?? [1, 2, 3];
        var botSet = new HashSet<int>(bots);

        for (var i = 0; i < SeatCount; i++)
        {
            state.Seats.Add(new ChangshaSeatState
            {
                SeatIndex = i,
                Wind = (Wind)i,
                PlayerId = botSet.Contains(i) ? $"bot-{i}" : $"human-{i}",
                IsBot = botSet.Contains(i),
                IsDealer = i == 0
            });
            state.Hands.Add(new ChangshaHandState { SeatIndex = i });
            state.CumulativeScores[i] = 0;
        }

        state.DealerSeatIndex = 0;
        state.Phase = ChangshaPhase.Seating;
        events.Add(CreateEvent(state, "game-created", -1, detail: $"seed:{seed}", replay: replay));

        return (state, events);
    }

    // ── Commands ───────────────────────────────────────────────────

    public static List<ChangshaEvent> StartGame(ChangshaGameState state)
        => StartGame(state, null);

    internal static List<ChangshaEvent> StartGame(ChangshaGameState state, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.Seating);
        state.Phase = ChangshaPhase.RollingDice;
        return [CreateEvent(state, "game-started", state.DealerSeatIndex,
            detail: $"dealer:{state.DealerSeatIndex},round:{state.RoundWind}", replay: replay)];
    }

    public static List<ChangshaEvent> RollDice(ChangshaGameState state, IDiceService diceService)
        => RollDice(state, diceService, null);

    internal static List<ChangshaEvent> RollDice(ChangshaGameState state, IDiceService diceService, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.RollingDice);
        var roll = diceService.Roll();
        state.LastDiceRoll = roll;

        var breakPointService = new BreakPointService();
        state.BreakPoint = breakPointService.ComputeBreakPoint(roll.Sum, state.DealerSeatIndex);

        state.Phase = ChangshaPhase.Dealing;
        return [CreateEvent(state, "dice-rolled", state.DealerSeatIndex,
            detail: $"die1:{roll.Die1},die2:{roll.Die2},sum:{roll.Sum}", replay: replay)];
    }

    public static List<ChangshaEvent> Deal(ChangshaGameState state)
        => Deal(state, null);

    internal static List<ChangshaEvent> Deal(ChangshaGameState state, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.Dealing);

        // Per-hand wall seed mixing — different hands of the same game produce different
        // shuffled walls while remaining deterministic for replay (same seed + HandNumber
        // → identical wall). Fixes pre-Phase-3 bug where every hand of a game used the
        // same `state.Seed` and therefore the same wall ordering.
        //
        // NOTE: deliberately NOT using <see cref="HashCode.Combine"/> — that helper is
        // randomized per-process (DoS mitigation) and breaks seed-determinism across
        // process boundaries, surfacing as rare flakes in parallel xUnit runs. We use
        // a deterministic mix (Knuth-style hash combiner) here so the wall ordering is
        // a pure function of (Seed, HandNumber).
        var mixed = unchecked((int)((uint)state.Seed * 2654435761u + (uint)state.HandNumber));
        var rng = new Random(mixed);
        var wall = BuildShuffledWall(rng);

        // Apply break point — reorder wall so drawing starts from break point
        wall = ApplyBreakPointToWall(wall, state.BreakPoint);

        var dealService = new DealService();
        var dealResult = dealService.Deal(wall, state.DealerSeatIndex);

        for (var i = 0; i < SeatCount; i++)
        {
            state.Hands[i].ConcealedTiles = dealResult.Hands[i];
            state.Hands[i].Melds.Clear();
        }

        state.Wall = dealResult.RemainingWall;
        state.WallDrawIndex = 0;
        state.WallBackIndex = state.Wall.Count - 1;
        state.WallBackDrawn = 0;
        state.ActiveSeatIndex = state.DealerSeatIndex;
        state.TurnNumber = 1;
        state.DiscardPile.Clear();
        state.DiscardsThisHand = 0;
        state.LastDrawSeatIndex = state.DealerSeatIndex;
        state.ClaimWindow = null;
        state.CurrentWin = null;
        state.CurrentScore = null;
        state.MissedWinSeats.Clear(); // §3.6: missed-win flags reset on new hand

        // Auto-deal path tombstones the pickup cursor.
        state.PickupSeatIndex = null;
        state.PickupRoundIndex = 0;

        state.Phase = ChangshaPhase.AwaitingDiscard;

        // Phase I Wave 1 — new hand starts with no kong-replacement context. Dealer's
        // initial 14-tile hand is eligible for 天和 (HeavenlyHand) as long as they
        // self-draw before any other action.
        state.LastDrawWasKongReplacement = false;

        var events = new List<ChangshaEvent>
        {
            CreateEvent(state, "tiles-dealt", state.DealerSeatIndex,
                detail: $"wall-remaining:{state.Wall.Count}", replay: replay)
        };

        return events;
    }

    // ── Phase F: manual-pickup deal path ──

    /// <summary>
    /// Consumes a <see cref="DiceRoll"/> from the dealer's roll, computes the break
    /// point, builds the shuffled wall, rotates it so <c>state.Wall[0]</c> is the next
    /// tile to draw, and parks the state machine at
    /// <see cref="ChangshaPhase.BreakPointMarked"/> with the dealer as the active picker.
    /// Mirrors the prelude of <see cref="Deal"/> but stops short of depositing tiles
    /// into hands — those flow in through <see cref="TakeTilesFromWall"/>.
    /// </summary>
    /// <remarks>
    /// Called by <c>ChangshaGameRuntime.RollDiceAsync</c> when the game's
    /// <see cref="ChangshaGameState.DealMode"/> is <see cref="DealMode.Manual"/>. The
    /// auto path (<see cref="DealMode.Auto"/>) still uses <see cref="RollDice"/> +
    /// <see cref="Deal"/> as a single transaction. Both paths share
    /// <see cref="BuildShuffledWall"/> and <see cref="ApplyBreakPointToWall"/> so the
    /// wall ordering is bit-identical for a given (Seed, HandNumber, BreakPoint).
    ///
    /// <para>Phase transitions during pickup (per Vasquez Phase F acceptance):
    /// <list type="bullet">
    ///   <item>RollingDice → BreakPointMarked (this method)</item>
    ///   <item>BreakPointMarked → PickupRound1 (first TakeTilesFromWall by dealer)</item>
    ///   <item>PickupRound1 → PickupRound2 → PickupRound3 → SingleTilePickup →
    ///   DealerExtra → AwaitingDiscard (each subsequent TakeTilesFromWall)</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static List<ChangshaEvent> BeginManualDeal(ChangshaGameState state, DiceRoll roll)
        => BeginManualDeal(state, roll, null);

    internal static List<ChangshaEvent> BeginManualDeal(ChangshaGameState state, DiceRoll roll, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.RollingDice);

        state.LastDiceRoll = roll;
        var breakPointService = new BreakPointService();
        state.BreakPoint = breakPointService.ComputeBreakPoint(roll.Sum, state.DealerSeatIndex);

        var mixed = unchecked((int)((uint)state.Seed * 2654435761u + (uint)state.HandNumber));
        var rng = new Random(mixed);
        var wall = BuildShuffledWall(rng);
        state.Wall = ApplyBreakPointToWall(wall, state.BreakPoint);

        // Hands fill incrementally via TakeTilesFromWall — start clean.
        for (var i = 0; i < SeatCount; i++)
        {
            state.Hands[i].ConcealedTiles = new List<int>();
            state.Hands[i].Melds.Clear();
        }
        state.WallDrawIndex = 0;
        state.WallBackIndex = state.Wall.Count - 1;
        state.WallBackDrawn = 0;
        state.ActiveSeatIndex = state.DealerSeatIndex;
        state.TurnNumber = 1;
        state.DiscardPile.Clear();
        state.DiscardsThisHand = 0;
        state.LastDrawSeatIndex = null;
        state.ClaimWindow = null;
        state.CurrentWin = null;
        state.CurrentScore = null;
        state.MissedWinSeats.Clear();

        state.DealMode = DealMode.Manual;
        state.Phase = ChangshaPhase.BreakPointMarked;
        state.PickupSeatIndex = state.DealerSeatIndex;
        state.PickupRoundIndex = 0;

        // Phase I Wave 1 — same reset as the auto-deal path (Deal). Manual-deal dealer's
        // 14th tile (DealerExtra pickup) is eligible for 天和 because no discards or
        // kong-replacements have happened yet.
        state.LastDrawWasKongReplacement = false;

        return new List<ChangshaEvent>
        {
            CreateEvent(state, "dice-rolled", state.DealerSeatIndex,
                detail: $"die1:{roll.Die1},die2:{roll.Die2},sum:{roll.Sum}", replay: replay),
            CreateEvent(state, "manual-deal-begun", state.DealerSeatIndex,
                detail: $"wall:{state.Wall.Count},dealer:{state.DealerSeatIndex}", replay: replay)
        };
    }

    /// <summary>
    /// Consumes <paramref name="requestedCount"/> tiles from the front of the wall
    /// into <paramref name="seatIndex"/>'s hand and advances the pickup cursor.
    /// Throws when called outside a pickup phase, when the requesting seat is not the
    /// active picker, when the count disagrees with
    /// <see cref="ExpectedPickupCount(ChangshaPhase)"/>, or when the wall underflows.
    /// </summary>
    public static List<ChangshaEvent> TakeTilesFromWall(
        ChangshaGameState state,
        int seatIndex,
        int requestedCount)
        => TakeTilesFromWall(state, seatIndex, requestedCount, null);

    internal static List<ChangshaEvent> TakeTilesFromWall(
        ChangshaGameState state, int seatIndex, int requestedCount, ChangshaReplayContext? replay)
    {
        if (!IsPickupPhase(state.Phase))
        {
            throw new InvalidOperationException(
                $"TakeTilesFromWall called in phase {state.Phase}; not a pickup phase.");
        }
        if (state.PickupSeatIndex != seatIndex)
        {
            throw new InvalidOperationException(
                $"Seat {seatIndex} is not the active pickup seat (expected {state.PickupSeatIndex}).");
        }
        var expected = ExpectedPickupCount(state.Phase);
        if (requestedCount != expected)
        {
            throw new InvalidOperationException(
                $"Pickup count mismatch: requested {requestedCount}, expected {expected} for phase {state.Phase}.");
        }
        if (state.Wall.Count < expected)
        {
            throw new InvalidOperationException(
                $"Wall underflow: requested {expected} tiles, wall has {state.Wall.Count}.");
        }

        var takenTiles = state.Wall.GetRange(0, expected);
        state.Wall.RemoveRange(0, expected);
        state.WallBackIndex = state.Wall.Count - 1;
        state.Hands[seatIndex].ConcealedTiles.AddRange(takenTiles);

        var prePhase = state.Phase;
        var pickupEvent = CreateEvent(state, "tiles-picked-up", seatIndex,
            detail: $"phase:{prePhase},count:{expected},wall-remaining:{state.Wall.Count}", replay: replay);

        AdvancePickupCursor(state);

        var events = new List<ChangshaEvent> { pickupEvent };
        if (state.Phase == ChangshaPhase.AwaitingDiscard)
        {
            events.Add(CreateEvent(state, "tiles-dealt", state.DealerSeatIndex,
                detail: $"wall-remaining:{state.Wall.Count}", replay: replay));
        }
        return events;
    }

    /// <summary>The number of tiles the active seat is expected to take while parked
    /// in <paramref name="phase"/>. Zero for non-pickup phases; callers should still
    /// gate on <see cref="IsPickupPhase"/>.
    /// <para>
    /// Note: <see cref="ChangshaPhase.BreakPointMarked"/> returns 4 because the very
    /// first pickup from that phase IS the dealer's round-1 take. After that first
    /// pickup, the phase advances to <see cref="ChangshaPhase.PickupRound1"/>.
    /// </para>
    /// </summary>
    public static int ExpectedPickupCount(ChangshaPhase phase) => phase switch
    {
        ChangshaPhase.BreakPointMarked => 4,
        ChangshaPhase.PickupRound1 => 4,
        ChangshaPhase.PickupRound2 => 4,
        ChangshaPhase.PickupRound3 => 4,
        ChangshaPhase.SingleTilePickup => 1,
        ChangshaPhase.DealerExtra => 1,
        _ => 0
    };

    /// <summary>True when <paramref name="phase"/> is one of the manual-deal pickup phases.</summary>
    public static bool IsPickupPhase(ChangshaPhase phase) => phase
        is ChangshaPhase.BreakPointMarked
        or ChangshaPhase.PickupRound1
        or ChangshaPhase.PickupRound2
        or ChangshaPhase.PickupRound3
        or ChangshaPhase.SingleTilePickup
        or ChangshaPhase.DealerExtra;

    /// <summary>
    /// Advances the pickup cursor after a successful <see cref="TakeTilesFromWall"/>.
    /// Rotates the active picker clockwise from the dealer; transitions to the next
    /// pickup phase when a round completes (4 picks for the multi-tile rounds + the
    /// single-tile round; 1 pick for DealerExtra). DealerExtra → AwaitingDiscard
    /// finalises the deal.
    /// <para>
    /// Special case: <see cref="ChangshaPhase.BreakPointMarked"/> transitions to
    /// <see cref="ChangshaPhase.PickupRound1"/> on the very first advance, so the
    /// phase the dealer's first 4-pickup completed in is correctly named
    /// "PickupRound1" even though it was initiated from BreakPointMarked.
    /// </para>
    /// </summary>
    private static void AdvancePickupCursor(ChangshaGameState state)
    {
        // BreakPointMarked is a degenerate "start of round 1" — promote to PickupRound1
        // BEFORE doing the round-complete arithmetic so the round counter works.
        if (state.Phase == ChangshaPhase.BreakPointMarked)
        {
            state.Phase = ChangshaPhase.PickupRound1;
        }

        state.PickupRoundIndex++;

        var seatsPerRound = state.Phase == ChangshaPhase.DealerExtra ? 1 : SeatCount;
        if (state.PickupRoundIndex < seatsPerRound)
        {
            // Still mid-round — rotate cursor clockwise from dealer.
            state.PickupSeatIndex = (state.DealerSeatIndex + state.PickupRoundIndex) % SeatCount;
            return;
        }

        // Round complete — transition to next phase.
        state.Phase = state.Phase switch
        {
            ChangshaPhase.PickupRound1 => ChangshaPhase.PickupRound2,
            ChangshaPhase.PickupRound2 => ChangshaPhase.PickupRound3,
            ChangshaPhase.PickupRound3 => ChangshaPhase.SingleTilePickup,
            ChangshaPhase.SingleTilePickup => ChangshaPhase.DealerExtra,
            ChangshaPhase.DealerExtra => ChangshaPhase.AwaitingDiscard,
            _ => state.Phase
        };
        state.PickupRoundIndex = 0;

        if (state.Phase == ChangshaPhase.AwaitingDiscard)
        {
            // Manual deal complete — clear pickup cursor and hand off to the discard loop.
            state.PickupSeatIndex = null;
            state.ActiveSeatIndex = state.DealerSeatIndex;
            state.LastDrawSeatIndex = state.DealerSeatIndex;
            state.TurnNumber = 1;
        }
        else
        {
            // Next round starts at the dealer.
            state.PickupSeatIndex = state.DealerSeatIndex;
        }
    }

    /// <summary>Rotates <paramref name="wall"/> so the tile at the break point is
    /// <c>wall[0]</c>. Returns the original list when <paramref name="breakPoint"/>
    /// is null. Extracted from <see cref="Deal"/> so the auto and manual deal paths
    /// share the exact same wall layout for a given (Seed, HandNumber, BreakPoint).</summary>
    private static List<int> ApplyBreakPointToWall(List<int> wall, BreakPointResult? breakPoint)
    {
        if (breakPoint is null) return wall;
        var bp = breakPoint.Value;
        var reordered = new List<int>(wall.Count);
        for (var i = bp.TileIndex; i < wall.Count; i++)
            reordered.Add(wall[i]);
        for (var i = 0; i < bp.TileIndex; i++)
            reordered.Add(wall[i]);
        return reordered;
    }

    public static List<ChangshaEvent> DrawTile(ChangshaGameState state)
        => DrawTile(state, null);

    internal static List<ChangshaEvent> DrawTile(ChangshaGameState state, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.AwaitingDiscard);

        if (state.Wall.Count == 0)
        {
            state.LastDrawSeatIndex = null;
            state.Phase = ChangshaPhase.WallExhausted;
            return [CreateEvent(state, "wall-exhausted", state.ActiveSeatIndex, replay: replay)];
        }

        var tileId = DrawFromFront(state);
        var hand = GetHand(state, state.ActiveSeatIndex);
        hand.ConcealedTiles.Add(tileId);
        state.LastDrawSeatIndex = state.ActiveSeatIndex;

        // §3.6 missed-win (过胡) decay: per Baidu §过水 — the lockout is "until your next draw."
        // Drawing a tile clears the active seat's lockout, restoring their ability to declare Hu
        // on subsequent discards within this hand. Self-draw was never blocked.
        state.MissedWinSeats.Remove(state.ActiveSeatIndex);

        // Phase I Wave 1 — regular front-of-wall draw breaks any kong-replacement chain;
        // a subsequent self-draw win is 海底捞月 (LastTileFromWall) at best, never 杠上开花.
        state.LastDrawWasKongReplacement = false;

        return [CreateEvent(state, "tile-drawn", state.ActiveSeatIndex, tileId: tileId,
            detail: $"wall-remaining:{state.Wall.Count}", replay: replay)];
    }

    public static List<ChangshaEvent> Discard(ChangshaGameState state, int seatIndex, int tileId)
        => Discard(state, seatIndex, tileId, null);

    internal static List<ChangshaEvent> Discard(ChangshaGameState state, int seatIndex, int tileId, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.AwaitingDiscard);
        RequireActiveSeat(state, seatIndex);

        var hand = GetHand(state, seatIndex);
        var discardCount = checked(state.DiscardsThisHand + 1);
        if (!hand.ConcealedTiles.Remove(tileId))
            throw new InvalidOperationException($"Tile {tileId} not in seat {seatIndex}'s hand.");

        state.DiscardsThisHand = discardCount;
        state.LastDrawSeatIndex = null;
        // Phase I Wave 1 — discarding always breaks the kong-replacement chain; even
        // a discard immediately following a kong-replacement draw makes a subsequent
        // self-draw NOT a 杠上开花. Cleared here so the next draw or claim sees a
        // clean slate.
        state.LastDrawWasKongReplacement = false;

        state.DiscardPile.Add(new ChangshaDiscard
        {
            SeatIndex = seatIndex,
            TileId = tileId,
            TurnNumber = state.TurnNumber
        });

        var events = new List<ChangshaEvent>
        {
            CreateEvent(state, "tile-discarded", seatIndex, tileId: tileId, replay: replay)
        };

        // Check for claims
        var adjudicator = new ClaimAdjudicator();
        var opportunities = adjudicator.GetOpportunities(seatIndex, tileId, state.Hands);

        // §3.6 missed-win (过胡): seats that previously declined a winning discard this hand
        // cannot win on a subsequent discard. Strip their Hu opportunities here so the rest of
        // the resolver never sees them. Pung/Kong/Chow remain eligible.
        if (state.MissedWinSeats.Count > 0)
        {
            opportunities = opportunities
                .Where(o => !(o.ClaimType == Tables.TableClaimType.Hu
                              && state.MissedWinSeats.Contains(o.SeatIndex)))
                .ToList();
        }

        if (opportunities.Count > 0)
        {
            state.ClaimWindow = new ChangshaClaimWindow
            {
                DiscardSeatIndex = seatIndex,
                DiscardTileId = tileId,
                Opportunities = opportunities,
                OpenedAtUnixMs = replay?.ClaimOpenedUnixMs() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            state.Phase = ChangshaPhase.AwaitingClaim;
            events.Add(CreateEvent(state, "claim-window-open", seatIndex, tileId: tileId,
                detail: $"opportunities:{opportunities.Count}", replay: replay));
        }
        else
        {
            AdvanceToNextPlayer(state, seatIndex);
        }

        state.TurnNumber++;
        return events;
    }

    public static List<ChangshaEvent> ResolveClaim(
        ChangshaGameState state,
        int claimingSeatIndex,
        Tables.TableClaimType claimType)
        => ResolveClaim(state, claimingSeatIndex, claimType, chosenTileIds: null);

    /// <summary>
    /// Resolves a claim. For Chow claims, <paramref name="chosenTileIds"/> may carry the 2 concealed
    /// tile IDs the claimant wishes to combine with the discarded tile. When provided, the IDs are
    /// validated (both held + form a valid sequential chow with the discard); when null/empty the
    /// resolver falls back to the lowest-rank valid pattern for legacy-client compatibility.
    /// </summary>
    public static List<ChangshaEvent> ResolveClaim(
        ChangshaGameState state,
        int claimingSeatIndex,
        Tables.TableClaimType claimType,
        int[]? chosenTileIds)
        => ResolveClaim(state, claimingSeatIndex, claimType, chosenTileIds, null);

    internal static List<ChangshaEvent> ResolveClaim(
        ChangshaGameState state, int claimingSeatIndex, Tables.TableClaimType claimType,
        int[]? chosenTileIds, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.AwaitingClaim);
        var claimWindow = state.ClaimWindow
            ?? throw new InvalidOperationException("No claim window open.");

        var events = new List<ChangshaEvent>();

        // Phase H Wave 2 — robbing-the-added-kong window: only Hu is legal. Pung/Kong/Chow
        // on a kong-target tile are mechanically impossible (the tile is already mid-meld).
        if (claimWindow.IsKongRobbing && claimType != Tables.TableClaimType.Hu)
        {
            throw new InvalidOperationException(
                $"Only Hu claims are valid on a robbing-the-added-kong window; got {claimType}.");
        }

        if (claimType == Tables.TableClaimType.Hu)
        {
            return ResolveHuClaim(state, claimingSeatIndex, claimWindow, replay);
        }

        var hand = GetHand(state, claimingSeatIndex);
        var discardLogical = ChangshaDeckBuilder.GetLogicalTile(claimWindow.DiscardTileId);
        // Resolve and validate the complete choice before consuming either hand or river.
        var (kind, consumed) = claimType switch
        {
            Tables.TableClaimType.Pung => (MeldKind.Pung, SelectMatchingTiles(hand, discardLogical, 2)),
            Tables.TableClaimType.Kong => (MeldKind.ExposedKong, SelectMatchingTiles(hand, discardLogical, 3)),
            Tables.TableClaimType.Chow => (MeldKind.Chow, SelectChowTiles(hand, claimWindow.DiscardTileId, chosenTileIds)),
            _ => throw new InvalidOperationException($"Unsupported meld claim {claimType}.")
        };
        if (claimType == Tables.TableClaimType.Chow) replay?.ObserveChowPartners(consumed);
        var meldTiles = consumed.Append(claimWindow.DiscardTileId).OrderBy(t => t).ToList();
        RemoveLastDiscard(state, claimWindow);
        foreach (var tile in consumed)
            hand.ConcealedTiles.Remove(tile);
        hand.Melds.Add(new Meld
        {
            Kind = kind,
            TileIds = meldTiles,
            ClaimedFromSeatIndex = claimWindow.DiscardSeatIndex
        });

        events.Add(CreateEvent(state, "claim-resolved", claimingSeatIndex,
            tileId: claimWindow.DiscardTileId,
            detail: $"type:{claimType}", replay: replay));

        // §3.6 missed-win: this claim was NOT a Hu. Any seat that had a Hu opportunity in
        // this window but didn't take it is now blocked from winning on subsequent discards
        // this hand.
        FlagMissedWinSeats(state, claimWindow, declaringHuSeat: -1);

        state.ClaimWindow = null;
        state.ActiveSeatIndex = claimingSeatIndex;
        state.LastDrawSeatIndex = null;
        state.LastDrawWasKongReplacement = false;

        if (claimType == Tables.TableClaimType.Kong)
        {
            // Kong replacement draw from back of wall
            if (state.Wall.Count > 0)
            {
                var replacementTile = DrawFromBack(state);
                hand.ConcealedTiles.Add(replacementTile);
                state.LastDrawSeatIndex = claimingSeatIndex;
                // Phase I Wave 1 — exposed-kong (claimed-from-discard) replacement
                // also arms 杠上开花. The 4-tile claim is mechanically identical to
                // a concealed kong from the replacement-draw perspective.
                state.LastDrawWasKongReplacement = true;
                events.Add(CreateEvent(state, "kong-replacement-drawn", claimingSeatIndex,
                    tileId: replacementTile, replay: replay));
            }
            else
            {
                state.Phase = ChangshaPhase.WallExhausted;
                events.Add(CreateEvent(state, "wall-exhausted", claimingSeatIndex, replay: replay));
                return events;
            }
        }

        state.Phase = ChangshaPhase.AwaitingDiscard;
        return events;
    }

    public static List<ChangshaEvent> PassClaim(ChangshaGameState state)
        => PassClaim(state, null);

    internal static List<ChangshaEvent> PassClaim(ChangshaGameState state, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.AwaitingClaim);
        var claimWindow = state.ClaimWindow
            ?? throw new InvalidOperationException("No claim window open.");

        // Phase H Wave 2 — robbing-the-added-kong path: all opportunities have passed,
        // so complete the kong on the original declarer's behalf and resume their turn.
        if (claimWindow.IsKongRobbing)
        {
            return ResolveAddedKongPassed(state, claimWindow, replay);
        }

        // §3.6 missed-win: every seat that had a Hu opportunity in this window has now passed
        // on a winning discard. Mark them so their future Hu claims this hand are rejected.
        FlagMissedWinSeats(state, claimWindow, declaringHuSeat: -1);

        state.ClaimWindow = null;
        AdvanceToNextPlayer(state, claimWindow.DiscardSeatIndex);

        return [CreateEvent(state, "claim-passed", claimWindow.DiscardSeatIndex,
            tileId: claimWindow.DiscardTileId, replay: replay)];
    }

    public static bool CanDeclareSelfDrawWin(ChangshaGameState state, int seatIndex) =>
        HasOwnDrawForTurn(state, seatIndex) && DetectSelfDrawWin(state, seatIndex).IsWin;

    public static IReadOnlyList<int> GetConcealedKongCandidates(ChangshaGameState state, int seatIndex)
    {
        if (!IsOwnDiscardTurn(state, seatIndex)) return [];
        return GetHand(state, seatIndex).ConcealedTiles
            .GroupBy(ChangshaDeckBuilder.GetLogicalTile)
            .Where(group => group.Count() >= 4)
            .Select(group => group.Key)
            .Order()
            .ToArray();
    }

    public static IReadOnlyList<int> GetAddedKongCandidates(ChangshaGameState state, int seatIndex)
    {
        if (!IsOwnDiscardTurn(state, seatIndex)) return [];
        var hand = GetHand(state, seatIndex);
        return hand.ConcealedTiles
            .Where(tile => FindAddedKongPung(hand, ChangshaDeckBuilder.GetLogicalTile(tile)) is not null)
            .Distinct()
            .Order()
            .ToArray();
    }

    public static bool CanDeclareConcealedKong(ChangshaGameState state, int seatIndex, int logicalTile) =>
        GetConcealedKongCandidates(state, seatIndex).Contains(logicalTile);

    public static bool CanDeclareAddedKong(ChangshaGameState state, int seatIndex, int tileId) =>
        GetAddedKongCandidates(state, seatIndex).Contains(tileId);

    private static bool IsOwnDiscardTurn(ChangshaGameState state, int seatIndex) =>
        seatIndex is >= 0 and < SeatCount
        && state.Phase == ChangshaPhase.AwaitingDiscard
        && state.ClaimWindow is null
        && state.ActiveSeatIndex == seatIndex
        && GetHand(state, seatIndex) is var hand
        && hand.ConcealedTiles.Count + 3 * hand.Melds.Count == 14;

    private static bool HasOwnDrawForTurn(ChangshaGameState state, int seatIndex) =>
        IsOwnDiscardTurn(state, seatIndex) && state.LastDrawSeatIndex == seatIndex;

    private static WinDetectionResult DetectSelfDrawWin(ChangshaGameState state, int seatIndex)
    {
        var hand = GetHand(state, seatIndex);
        var detector = new ChangshaWinDetector();

        var context = new WinContext
        {
            IsHeavenlyHand = state.DiscardsThisHand == 0
                && state.DiscardPile.Count == 0
                && seatIndex == state.DealerSeatIndex
                && state.Hands.All(candidate => candidate.Melds.Count == 0)
                && HasUninterruptedOpening(state, allowedDiscards: 0),
            IsLastTileFromWall = state.Wall.Count == 0,
            IsKongReplacementWin = state.LastDrawWasKongReplacement
        };
        return detector.Detect(hand, method: WinMethod.SelfDraw, context: context);
    }

    public static List<ChangshaEvent> DeclareSelfDrawWin(ChangshaGameState state, int seatIndex)
        => DeclareSelfDrawWin(state, seatIndex, null);

    internal static List<ChangshaEvent> DeclareSelfDrawWin(ChangshaGameState state, int seatIndex, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.AwaitingDiscard);
        RequireActiveSeat(state, seatIndex);
        if (!HasOwnDrawForTurn(state, seatIndex))
            throw new InvalidOperationException("Self-draw requires an actual own draw on the current turn.");

        var hand = GetHand(state, seatIndex);
        var result = DetectSelfDrawWin(state, seatIndex);
        if (!result.IsWin)
            throw new InvalidOperationException("Hand is not a winning hand.");

        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = seatIndex,
            Method = WinMethod.SelfDraw,
            Pattern = result.Pattern!.Value,
            WinningTileId = hand.ConcealedTiles[^1], // last drawn tile
            SourceSeatIndex = seatIndex,
            IsFullFlush = result.IsFullFlush,
            // Phase J Wave 3 — explicit axes mirrored from the WinContext / Method so
            // downstream consumers don't infer them from Method + AllPatterns.
            IsSelfDraw = true,
            IsKongReplacement = state.LastDrawWasKongReplacement,
            AllPatterns = result.AllPatterns,
            // Phase J Wave 9 — pre-resolved i18n keys for every pattern,
            // mirrored at win-declaration time so the wire surface
            // (WinDeclared event + replay) carries the catalog keys.
            PatternKeys = result.AllPatterns
                .Select(Mahjong.Autotable.Api.Changsha.Patterns.PatternResourceCatalog.KeyFor)
                .ToArray(),
        };

        state.Phase = ChangshaPhase.Scoring;
        return [CreateEvent(state, "win-declared", seatIndex,
            detail: $"method:selfDraw,pattern:{result.Pattern}", replay: replay)];
    }

    public static List<ChangshaEvent> DeclareConcealedKong(ChangshaGameState state, int seatIndex, int logicalTile)
        => DeclareConcealedKong(state, seatIndex, logicalTile, null);

    internal static List<ChangshaEvent> DeclareConcealedKong(
        ChangshaGameState state, int seatIndex, int logicalTile, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.AwaitingDiscard);
        RequireActiveSeat(state, seatIndex);

        var hand = GetHand(state, seatIndex);
        var matching = hand.ConcealedTiles
            .Where(t => ChangshaDeckBuilder.GetLogicalTile(t) == logicalTile)
            .OrderBy(t => t)
            .ToList();

        if (!CanDeclareConcealedKong(state, seatIndex, logicalTile))
            throw new InvalidOperationException("Not enough tiles for concealed kong.");

        var kongTiles = matching.Take(4).ToList();
        foreach (var t in kongTiles)
            hand.ConcealedTiles.Remove(t);

        hand.Melds.Add(new Meld
        {
            Kind = MeldKind.ConcealedKong,
            TileIds = kongTiles
        });
        state.LastDrawSeatIndex = null;
        state.LastDrawWasKongReplacement = false;

        var events = new List<ChangshaEvent>
        {
            CreateEvent(state, "concealed-kong", seatIndex, detail: $"logical:{logicalTile}", replay: replay)
        };

        // Replacement draw from back of wall
        if (state.Wall.Count > 0)
        {
            var replacementTile = DrawFromBack(state);
            hand.ConcealedTiles.Add(replacementTile);
            state.LastDrawSeatIndex = seatIndex;
            // Phase I Wave 1 — concealed-kong replacement: arms the 杠上开花 flag.
            // Cleared on the next Discard / regular DrawTile.
            state.LastDrawWasKongReplacement = true;
            events.Add(CreateEvent(state, "kong-replacement-drawn", seatIndex,
                tileId: replacementTile, replay: replay));
        }
        else
        {
            state.Phase = ChangshaPhase.WallExhausted;
            events.Add(CreateEvent(state, "wall-exhausted", seatIndex, replay: replay));
        }

        return events;
    }

    public static List<ChangshaEvent> DeclareAddedKong(ChangshaGameState state, int seatIndex, int tileId)
        => DeclareAddedKong(state, seatIndex, tileId, null);

    internal static List<ChangshaEvent> DeclareAddedKong(
        ChangshaGameState state, int seatIndex, int tileId, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.AwaitingDiscard);
        RequireActiveSeat(state, seatIndex);

        var hand = GetHand(state, seatIndex);
        if (!hand.ConcealedTiles.Contains(tileId))
            throw new InvalidOperationException($"Tile {tileId} not in hand.");

        var logicalTile = ChangshaDeckBuilder.GetLogicalTile(tileId);
        var existingPung = FindAddedKongPung(hand, logicalTile);

        if (existingPung is null || !CanDeclareAddedKong(state, seatIndex, tileId))
            throw new InvalidOperationException("No existing pung to extend.");

        // Phase H Wave 2 §2.2 — 抢杠胡 (Robbing the Added Kong) opportunity scan.
        // BEFORE mutating the hand, ask the adjudicator if any other seat can Hu on
        // the candidate tile. If none can, fall through to the legacy "complete kong
        // immediately" path so there's zero latency cost for the common case.
        // §3.6 missed-win: seats flagged as MissedWin cannot win on a robbed kong
        // (treated the same as a subsequent discard), so strip those opportunities
        // here just like Discard() does.
        var adjudicator = new ClaimAdjudicator();
        var huOpportunities = adjudicator
            .GetHuOnlyOpportunitiesForKong(seatIndex, tileId, state.Hands)
            .Where(o => !state.MissedWinSeats.Contains(o.SeatIndex))
            .ToList();

        if (huOpportunities.Count > 0)
        {
            state.LastDrawSeatIndex = null;
            state.LastDrawWasKongReplacement = false;
            // Open a kong-robbing claim window — DO NOT yet upgrade the meld. The
            // declarer's hand is mutated only when the window resolves (CompleteAddedKongAfterPass).
            state.ClaimWindow = new ChangshaClaimWindow
            {
                DiscardSeatIndex = seatIndex,
                DiscardTileId = tileId,
                Opportunities = huOpportunities,
                IsKongRobbing = true,
                KongDeclarerSeatIndex = seatIndex,
                OpenedAtUnixMs = replay?.ClaimOpenedUnixMs() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            state.Phase = ChangshaPhase.AwaitingClaim;
            return [
                CreateEvent(state, "added-kong-declared", seatIndex, tileId: tileId,
                    detail: $"logical:{logicalTile}", replay: replay),
                CreateEvent(state, "claim-window-open", seatIndex, tileId: tileId,
                    detail: $"kongRobbing:true,opportunities:{huOpportunities.Count}", replay: replay)
            ];
        }

        // No Hu opportunities — fast path. Complete the kong exactly as the pre-Wave-2
        // implementation did.
        return CompleteAddedKong(state, hand, existingPung, tileId, seatIndex, replay);
    }

    /// <summary>
    /// Phase H Wave 2 — completes an added-kong after the robbing-kong claim window
    /// closes with no Hu (either all opponents passed, or there were no opportunities
    /// in the first place — the fast path in <see cref="DeclareAddedKong"/>). Mutates
    /// the hand: removes the 4th tile from concealed, upgrades the matching Pung meld
    /// to AddedKong, and draws a replacement from the back of the wall.
    /// </summary>
    private static List<ChangshaEvent> CompleteAddedKong(
        ChangshaGameState state,
        ChangshaHandState hand,
        Meld existingPung,
        int tileId,
        int seatIndex,
        ChangshaReplayContext? replay)
    {
        state.ActiveSeatIndex = seatIndex;
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.LastDrawSeatIndex = null;
        state.LastDrawWasKongReplacement = false;
        hand.ConcealedTiles.Remove(tileId);
        existingPung.TileIds.Add(tileId);
        existingPung.TileIds.Sort();
        // Upgrade kind to AddedKong — the meld object is mutable so we create a replacement
        var index = hand.Melds.IndexOf(existingPung);
        hand.Melds[index] = new Meld
        {
            Kind = MeldKind.AddedKong,
            TileIds = existingPung.TileIds,
            ClaimedFromSeatIndex = existingPung.ClaimedFromSeatIndex
        };

        var events = new List<ChangshaEvent>
        {
            CreateEvent(state, "added-kong", seatIndex, tileId: tileId, replay: replay)
        };

        // Replacement draw from back of wall
        if (state.Wall.Count > 0)
        {
            var replacementTile = DrawFromBack(state);
            hand.ConcealedTiles.Add(replacementTile);
            state.LastDrawSeatIndex = seatIndex;
            // Phase I Wave 1 — added-kong replacement (also reached from the
            // robbing-the-added-kong pass-through via ResolveAddedKongPassed) — arms
            // the 杠上开花 flag. Cleared on the next Discard / regular DrawTile.
            state.LastDrawWasKongReplacement = true;
            events.Add(CreateEvent(state, "kong-replacement-drawn", seatIndex,
                tileId: replacementTile, replay: replay));
        }
        else
        {
            state.Phase = ChangshaPhase.WallExhausted;
            events.Add(CreateEvent(state, "wall-exhausted", seatIndex, replay: replay));
        }

        return events;
    }

    /// <summary>
    /// Phase H Wave 2 — robbing-kong window resolution: every Hu-eligible seat passed.
    /// Completes the added kong on the original declarer's behalf. <see cref="ChangshaClaimWindow.IsKongRobbing"/>
    /// must be true; called by <see cref="PassAddedKongClaim"/>.
    /// </summary>
    private static List<ChangshaEvent> ResolveAddedKongPassed(
        ChangshaGameState state, ChangshaClaimWindow window, ChangshaReplayContext? replay)
    {
        var declarerSeat = window.KongDeclarerSeatIndex
            ?? throw new InvalidOperationException("KongDeclarerSeatIndex is required for a kong-robbing window.");
        var tileId = window.DiscardTileId;
        var hand = GetHand(state, declarerSeat);
        var logicalTile = ChangshaDeckBuilder.GetLogicalTile(tileId);

        var existingPung = hand.Melds.FirstOrDefault(m =>
            m.Kind == MeldKind.Pung &&
            m.TileIds.All(t => ChangshaDeckBuilder.GetLogicalTile(t) == logicalTile))
            ?? throw new InvalidOperationException("Added-kong target pung disappeared from declarer's hand mid-window.");

        // §3.6 missed-win: any seat that had Hu on the robbing-kong window but didn't
        // claim it forfeits future Hu opportunities this hand.
        FlagMissedWinSeats(state, window, declaringHuSeat: -1);

        state.ClaimWindow = null;
        state.ActiveSeatIndex = declarerSeat;
        // After CompleteAddedKong we land back in AwaitingDiscard (replacement drawn) or
        // WallExhausted — matches the pre-Wave-2 DeclareAddedKong fall-through.
        var events = new List<ChangshaEvent>
        {
            CreateEvent(state, "claim-passed", declarerSeat, tileId: tileId,
                detail: "kongRobbing:true", replay: replay)
        };
        events.AddRange(CompleteAddedKong(state, hand, existingPung, tileId, declarerSeat, replay));
        return events;
    }

    public static List<ChangshaEvent> Score(
        ChangshaGameState state,
        Scoring.ChangshaScoringOptions? scoringOptions = null)
        => Score(state, scoringOptions, null);

    internal static List<ChangshaEvent> Score(
        ChangshaGameState state, Scoring.ChangshaScoringOptions? scoringOptions, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.Scoring);
        if (state.CurrentWin is null)
            throw new InvalidOperationException("No win to score.");

        // #117 — spec §5.1 is authoritative. The default (SpecPure) emits the §5.1
        // payment table verbatim: no fan-catalog bonus folded into the money, no
        // Big-Win stacking multiplier. The fan catalog is still evaluated and surfaced
        // on Fans/FanPoints as a read-only breakdown (query-only wrt payment amounts).
        // A future house-rule/tournament mode (ChangshaScoringOptions.HouseRules) can
        // opt back into the additive fan layer + stacking without a second rewrite.
        var options = scoringOptions ?? Scoring.ChangshaScoringOptions.SpecPure;

        var scoringService = new ScoringService();
        // Big-Win stacking is opt-in (spec §5.1 has no stacking table). Spec-pure passes
        // count=1 → the multiplier clamps to ×1. House-rules passes AllPatterns.Count so
        // the ×1/×2/×3-cap multiplier applies (see ScoringService.CalculateScore).
        var bigWinPatternCount = options.ApplyBigWinStacking
            ? state.CurrentWin.AllPatterns.Count
            : 1;
        var baseScore = scoringService.CalculateScore(
            state.CurrentWin, state.DealerSeatIndex, state.CurrentWin.IsFullFlush, bigWinPatternCount);

        // Evaluate Frost's 14-fan catalog for the read-only breakdown surfaced on
        // Fans/FanPoints (frontend win-screen chips, replay, audit). In spec-pure mode
        // this breakdown does NOT move chips: payments stay at the §5.1 magnitude and
        // BasePoints == Σ Payments.Amount (base only). In house-rules mode the bonus is
        // folded into every base payment (one fan-bonus PaymentEntry per base × fan).
        var fanResult = EvaluateFanBonuses(state);
        var payments = options.ApplyFanBonuses
            ? ApplyFanBonusesToPayments(baseScore.Payments, fanResult)
            : baseScore.Payments;
        payments = ScoringService.ScalePayments(payments, state.BaseUnit);
        var totalBasePoints = payments.Sum(p => p.Amount);
        var cumulativeScores = ScoringService.ApplyPayments(state.CumulativeScores, payments);

        state.CurrentScore = new ScoreResult
        {
            Category = baseScore.Category,
            BasePoints = totalBasePoints,
            Payments = payments,
            Fans = fanResult.Detected,
            FanPoints = fanResult.TotalPoints,
        };

        state.CumulativeScores = cumulativeScores;

        state.Phase = ChangshaPhase.EndHand;
        return [CreateEvent(state, "scoring-complete", state.CurrentWin.WinningSeatIndex,
            detail: $"category:{state.CurrentScore.Category},fans:{fanResult.Detected.Count},fanPoints:{fanResult.TotalPoints}", replay: replay)];
    }

    /// <summary>
    /// Composes a <see cref="Scoring.FanContext"/> + <see cref="Scoring.WinningHand"/>
    /// from the current win state and runs <see cref="Scoring.FanCalculator.EvaluateHand"/>.
    /// Pure helper — no state mutation. Returns <see cref="Scoring.FanResult.Empty"/> if no
    /// win/hand is in flight (guarded by the caller, but defensive belt-and-braces).
    /// </summary>
    private static Scoring.FanResult EvaluateFanBonuses(ChangshaGameState state)
    {
        var win = state.CurrentWin;
        if (win is null) return Scoring.FanResult.Empty;

        var winningHand = GetHand(state, win.WinningSeatIndex);
        var seatWind = state.Seats.FirstOrDefault(s => s.SeatIndex == win.WinningSeatIndex)?.Wind ?? Wind.East;

        // The state machine has already authoritatively decided every situational axis
        // (IsSelfDraw, IsKongReplacement, IsRobbedKong, AllPatterns containing LastTileFromWall /
        // LastDiscardCatch / HeavenlyHand / EarthlyHand). Just mirror them into FanContext.
        var ctx = new Scoring.FanContext
        {
            IsSelfDraw = win.IsSelfDraw,
            IsKongReplacement = win.IsKongReplacement,
            IsLastTileFromWall = win.AllPatterns.Contains(WinPattern.LastTileFromWall),
            IsLastDiscardCatch = win.AllPatterns.Contains(WinPattern.LastDiscardCatch),
            IsRobbingKong = win.IsRobbedKong,
            IsHeavenlyHand = win.AllPatterns.Contains(WinPattern.HeavenlyHand),
            IsEarthlyHand = win.AllPatterns.Contains(WinPattern.EarthlyHand),
            SeatWind = seatWind,
            RoundWind = state.RoundWind,
            Variant = Scoring.FanVariant.Changsha,
        };
        var hand = new Scoring.WinningHand
        {
            ConcealedTileIds = winningHand.ConcealedTiles.ToList(),
            Melds = winningHand.Melds.ToList(),
            WinningTileId = win.WinningTileId,
        };
        return Scoring.FanCalculator.EvaluateHand(hand, ctx);
    }

    /// <summary>
    /// Returns a NEW list containing every base <paramref name="basePayments"/> entry
    /// followed by one fan-bonus <see cref="PaymentEntry"/> per detected fan per base
    /// payment. Reason is <c>"fan:{fanName}"</c> (camelCase) so the wire surface can be
    /// rendered as a stacked breakdown. The (from, to) pair of each fan-bonus row mirrors
    /// the corresponding base payment so zero-sum holds across the full hand.
    /// </summary>
    private static List<PaymentEntry> ApplyFanBonusesToPayments(
        IReadOnlyList<PaymentEntry> basePayments,
        Scoring.FanResult fanResult)
    {
        var combined = new List<PaymentEntry>(basePayments);
        if (fanResult.Detected.Count == 0) return combined;

        foreach (var basePayment in basePayments)
        {
            foreach (var fan in fanResult.Detected)
            {
                combined.Add(new PaymentEntry
                {
                    FromSeatIndex = basePayment.FromSeatIndex,
                    ToSeatIndex = basePayment.ToSeatIndex,
                    Amount = fan.Points,
                    Reason = $"fan:{FanWireName(fan.Fan)}",
                });
            }
        }
        return combined;
    }

    /// <summary>
    /// Wire-shape mapping from <see cref="Scoring.Fan"/> to camelCase identifier
    /// (matches the convention used by <see cref="Mahjong.Autotable.Api.Autotable.FanEntry.Fan"/>).
    /// Centralised here so both the in-state <see cref="PaymentEntry.Reason"/> column and
    /// the translator emit the same identifier.
    /// </summary>
    internal static string FanWireName(Scoring.Fan fan)
    {
        var name = fan.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static List<ChangshaEvent> HandleWallExhausted(ChangshaGameState state)
        => HandleWallExhausted(state, null);

    internal static List<ChangshaEvent> HandleWallExhausted(ChangshaGameState state, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.WallExhausted);
        state.Phase = ChangshaPhase.EndHand;
        return [CreateEvent(state, "draw-hand", -1, detail: "wall-exhausted", replay: replay)];
    }

    /// <summary>
    /// Records a 诈胡 (false-Hu) declaration per Baidu §诈胡处罚 and applies the resulting
    /// penalty payments to <see cref="ChangshaGameState.CumulativeScores"/>. Stateless wrt
    /// the hand/turn machine — the offending player keeps their seat and the hand continues
    /// unchanged (Score/RotateBanker are not driven from here). Idempotency: each call appends
    /// a new entry to <see cref="ChangshaGameState.FalseHuPenalties"/>.
    /// </summary>
    public static FalseHuPenalty RecordFalseHu(ChangshaGameState state, int seatIndex)
    {
        if (seatIndex is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(seatIndex));

        var penalty = new ScoringService().CalculateFalseHuPenalty(seatIndex, state.BaseUnit);
        var initializedScores = new Dictionary<int, int>(state.CumulativeScores);
        for (var seat = 0; seat < SeatCount; seat++)
            initializedScores.TryAdd(seat, 0);
        var cumulativeScores = ScoringService.ApplyPayments(initializedScores, penalty.Payments);

        state.CumulativeScores = cumulativeScores;
        state.FalseHuPenalties.Add(penalty);
        CreateEvent(state, "false-hu-penalty", seatIndex,
            detail: $"perOpponent:{penalty.PenaltyPerOpponent}");

        return penalty;
    }

    public static List<ChangshaEvent> RotateBanker(ChangshaGameState state)
        => RotateBanker(state, null);

    internal static List<ChangshaEvent> RotateBanker(ChangshaGameState state, ChangshaReplayContext? replay)
    {
        RequirePhase(state, ChangshaPhase.EndHand);

        var events = new List<ChangshaEvent>();
        var previousDealer = state.DealerSeatIndex;
        string reason;

        // Canonical Changsha v1.2 banker rotation (per docs/rules/changsha-spec.md §6.2):
        //   - Winner: winner becomes next dealer (degenerates to "dealer keeps seat" if dealer won).
        //   - Washout: current dealer keeps the seat.
        // No cyclic +1/-1 rotation in v1.
        if (state.CurrentWin is not null)
        {
            state.DealerSeatIndex = state.CurrentWin.WinningSeatIndex;
            reason = state.DealerSeatIndex == previousDealer
                ? "dealerRetained"
                : "winnerBecomesDealer";
        }
        else
        {
            // Washout — dealer keeps the seat (state.DealerSeatIndex unchanged).
            reason = "washoutDealerRetained";
        }

        // Update seat dealer flags
        foreach (var seat in state.Seats)
            seat.IsDealer = seat.SeatIndex == state.DealerSeatIndex;

        events.Add(CreateEvent(state, "banker-rotated", state.DealerSeatIndex,
            detail: $"previous:{previousDealer},reason:{reason}", replay: replay));

        // Advance hand/round counters
        state.HandNumber++;
        state.HandInRound++;

        // Phase J Wave 2 — N-hand rotation cap. After incrementing HandNumber,
        // if it now exceeds the configured MaxHands the game terminates: set
        // Phase=GameComplete + IsGameComplete=true and emit a single
        // "game-ended" event with detail "hands:{MaxHands}" so downstream
        // listeners (runtime → SignalR / autotable surface) can fire the
        // GameCompleted notification. The pre-Wave-2 16-hand path below remains
        // for tournament configurations that raise MaxHands beyond 16.
        if (state.HandNumber > state.MaxHands)
        {
            state.Phase = ChangshaPhase.GameComplete;
            state.IsGameComplete = true;
            events.Add(CreateEvent(state, "game-ended", -1,
                detail: $"hands:{state.MaxHands},reason:maxHandsReached", replay: replay));
            return events;
        }

        if (state.HandInRound > HandsPerRound)
        {
            state.HandInRound = 1;
            state.RoundNumber++;

            if (state.RoundNumber > 4)
            {
                // Phase J Wave 4 — EndGame and GameComplete are now aliases of
                // the same enum value; either symbol fires the canonical
                // terminal phase. We reference EndGame here to preserve the
                // historical signal for tournament configurations that raise
                // MaxHands > 16 (the only way to reach this branch). At the
                // value level this is identical to GameComplete; on the wire
                // state.Phase.ToString() always emits "GameComplete" since it
                // is declared first in ChangshaPhase.
                state.Phase = ChangshaPhase.EndGame;
                state.IsGameComplete = true;
                events.Add(CreateEvent(state, "game-ended", -1,
                    detail: $"hands:{state.HandNumber - 1}", replay: replay));
                return events;
            }

            state.RoundWind = (Wind)(state.RoundNumber - 1);
            events.Add(CreateEvent(state, "round-changed", -1,
                detail: $"round:{state.RoundNumber},wind:{state.RoundWind}", replay: replay));
        }

        // Reset for next hand
        state.CurrentWin = null;
        state.CurrentScore = null;
        state.LastDrawSeatIndex = null;
        state.LastDrawWasKongReplacement = false;
        state.DiscardsThisHand = 0;
        state.Phase = ChangshaPhase.RollingDice;

        return events;
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static List<ChangshaEvent> ResolveHuClaim(
        ChangshaGameState state,
        int claimingSeatIndex,
        ChangshaClaimWindow claimWindow,
        ChangshaReplayContext? replay)
    {
        // Phase H Wave 2 — kong-robbing wins (抢杠胡) take the same hand-mutation path
        // (add the winning tile to concealed for detection / display) but DO NOT touch
        // the discard pile (the tile is mid-meld, never entered the river). The detector
        // still validates the hand as a standard 4+pair / 7-pairs / FullFlush / etc. — the
        // RobbingKong tag is purely a method-side annotation.
        var hand = GetHand(state, claimingSeatIndex);
        var isKongRobbing = claimWindow.IsKongRobbing;

        // Claimed discards leave the river, so its size alone cannot establish the
        // first-discard condition. Require uninterrupted, current-hand history too.
        var context = new WinContext
        {
            IsEarthlyHand = !isKongRobbing
                && state.DiscardsThisHand == 1
                && state.DiscardPile.Count == 1
                && state.DiscardPile[0].SeatIndex == state.DealerSeatIndex
                && claimingSeatIndex != state.DealerSeatIndex
                && state.Hands.All(candidate => candidate.Melds.Count == 0)
                && HasUninterruptedOpening(state, allowedDiscards: 1),
            IsLastDiscardCatch = !isKongRobbing && state.Wall.Count == 0
        };

        var kongDeclarer = isKongRobbing
            ? GetHand(state, claimWindow.KongDeclarerSeatIndex ?? claimWindow.DiscardSeatIndex)
            : null;
        if (kongDeclarer is not null && !kongDeclarer.ConcealedTiles.Contains(claimWindow.DiscardTileId))
            throw new InvalidOperationException("The robbed tile is no longer held by the kong declarer.");

        var detector = new ChangshaWinDetector();
        var method = isKongRobbing ? WinMethod.RobbingKong : WinMethod.Discard;
        var winningHand = new ChangshaHandState
        {
            SeatIndex = hand.SeatIndex,
            ConcealedTiles = [.. hand.ConcealedTiles, claimWindow.DiscardTileId],
            Melds = hand.Melds
        };
        var result = detector.Detect(winningHand, claimWindow.DiscardTileId, method, context);

        if (!result.IsWin)
            throw new InvalidOperationException("Claimed Hu but hand is not winning.");

        if (kongDeclarer is not null)
            kongDeclarer.ConcealedTiles.Remove(claimWindow.DiscardTileId);
        else
            RemoveLastDiscard(state, claimWindow);
        hand.ConcealedTiles.Add(claimWindow.DiscardTileId);

        state.CurrentWin = new WinResult
        {
            WinningSeatIndex = claimingSeatIndex,
            Method = method,
            Pattern = result.Pattern!.Value,
            WinningTileId = claimWindow.DiscardTileId,
            // For kong-robbing the source is the kong declarer (KongDeclarerSeatIndex),
            // which mirrors DiscardSeatIndex by construction — kept as a separate read
            // path so future changes to ChangshaClaimWindow shape don't silently break
            // robbing-kong scoring attribution.
            SourceSeatIndex = isKongRobbing
                ? (claimWindow.KongDeclarerSeatIndex ?? claimWindow.DiscardSeatIndex)
                : claimWindow.DiscardSeatIndex,
            IsFullFlush = result.IsFullFlush,
            IsRobbedKong = isKongRobbing,
            // Phase J Wave 3 — explicit axes. ResolveHuClaim is the discard/robbing-kong
            // path, so IsSelfDraw is always false here. IsKongReplacement is likewise
            // false — robbing-the-added-kong is NOT a kong-replacement win (the winner
            // intercepted the kong rather than drawing its replacement).
            IsSelfDraw = false,
            IsKongReplacement = false,
            AllPatterns = result.AllPatterns,
            // Phase J Wave 9 — pre-resolved i18n keys.
            PatternKeys = result.AllPatterns
                .Select(Mahjong.Autotable.Api.Changsha.Patterns.PatternResourceCatalog.KeyFor)
                .ToArray(),
        };

        // §3.6 missed-win: if multiple seats had Hu in this window and only one declared,
        // the others have effectively passed on a winning discard and are now blocked.
        FlagMissedWinSeats(state, claimWindow, declaringHuSeat: claimingSeatIndex);

        state.ClaimWindow = null;
        state.ActiveSeatIndex = claimingSeatIndex;
        state.LastDrawSeatIndex = null;
        state.LastDrawWasKongReplacement = false;
        state.Phase = ChangshaPhase.Scoring;

        return [CreateEvent(state, "win-declared", claimingSeatIndex,
            tileId: claimWindow.DiscardTileId,
            detail: $"method:{(isKongRobbing ? "robbingKong" : "discard")},pattern:{result.Pattern}", replay: replay)];
    }

    /// <summary>
    /// §3.6 — Marks every seat that had a Hu opportunity in <paramref name="claimWindow"/>
    /// but did NOT win on this discard. Those seats are forbidden from winning on subsequent
    /// discards within the same hand (self-draw remains allowed). Cleared on <see cref="Deal"/>.
    /// </summary>
    private static void FlagMissedWinSeats(
        ChangshaGameState state,
        ChangshaClaimWindow claimWindow,
        int declaringHuSeat)
    {
        foreach (var opp in claimWindow.Opportunities)
        {
            if (opp.ClaimType != Tables.TableClaimType.Hu) continue;
            if (opp.SeatIndex == declaringHuSeat) continue;
            state.MissedWinSeats.Add(opp.SeatIndex);
        }
    }

    private static void AdvanceToNextPlayer(ChangshaGameState state, int currentSeatIndex)
    {
        state.ActiveSeatIndex = (currentSeatIndex + 1) % SeatCount;
        if (state.Wall.Count == 0)
        {
            state.Phase = ChangshaPhase.WallExhausted;
        }
        else
        {
            state.Phase = ChangshaPhase.AwaitingDiscard;
        }
    }

    private static bool HasUninterruptedOpening(ChangshaGameState state, int allowedDiscards)
    {
        var discards = 0;
        for (var index = state.EventLog.Count - 1; index >= 0; index--)
        {
            switch (state.EventLog[index].EventType)
            {
                case "tiles-dealt":
                    return discards == allowedDiscards;
                case "tile-discarded":
                    if (++discards > allowedDiscards) return false;
                    break;
                case "game-created":
                case "banker-rotated":
                case "tile-drawn":
                case "claim-resolved":
                case "concealed-kong":
                case "added-kong-declared":
                case "added-kong":
                case "kong-replacement-drawn":
                    return false;
            }
        }
        return false;
    }

    private static Meld? FindAddedKongPung(ChangshaHandState hand, int logicalTile) =>
        hand.Melds.FirstOrDefault(meld => meld.Kind == MeldKind.Pung && meld.TileIds.Count == 3
            && meld.TileIds.All(tile => ChangshaDeckBuilder.GetLogicalTile(tile) == logicalTile));

    private static int DrawFromFront(ChangshaGameState state)
    {
        if (state.Wall.Count == 0)
            throw new InvalidOperationException("Cannot draw from empty wall.");
        var tileId = state.Wall[0];
        state.Wall.RemoveAt(0);
        return tileId;
    }

    private static int DrawFromBack(ChangshaGameState state)
    {
        if (state.Wall.Count == 0)
            throw new InvalidOperationException("Cannot draw from empty wall.");
        var last = state.Wall.Count - 1;
        var tileId = state.Wall[last];
        state.Wall.RemoveAt(last);
        // #152 — rendering bookkeeping so the translator can derive the
        // front-draw anchor (108 - Wall.Count - WallBackDrawn) and keep the
        // remaining wall tiles at stable physical slots.
        state.WallBackDrawn++;
        return tileId;
    }

    private static ChangshaHandState GetHand(ChangshaGameState state, int seatIndex) =>
        state.Hands.Single(h => h.SeatIndex == seatIndex);

    private static void RequirePhase(ChangshaGameState state, ChangshaPhase expected)
    {
        if (state.Phase != expected)
            throw new InvalidOperationException(
                $"Expected phase {expected} but current phase is {state.Phase}.");
    }

    private static void RequireActiveSeat(ChangshaGameState state, int seatIndex)
    {
        if (state.ActiveSeatIndex != seatIndex)
            throw new InvalidOperationException(
                $"Seat {seatIndex} is not the active seat (active: {state.ActiveSeatIndex}).");
    }

    private static void RemoveLastDiscard(ChangshaGameState state, ChangshaClaimWindow claimWindow)
    {
        var idx = state.DiscardPile.FindLastIndex(d =>
            d.SeatIndex == claimWindow.DiscardSeatIndex &&
            d.TileId == claimWindow.DiscardTileId);
        if (idx >= 0)
            state.DiscardPile.RemoveAt(idx);
    }

    private static List<int> SelectMatchingTiles(ChangshaHandState hand, int logicalTile, int count)
    {
        var matches = hand.ConcealedTiles
            .Where(t => ChangshaDeckBuilder.GetLogicalTile(t) == logicalTile)
            .OrderBy(t => t)
            .Take(count)
            .ToList();

        if (matches.Count < count)
            throw new InvalidOperationException($"Not enough matching tiles for claim.");

        return matches;
    }

    /// <summary>
    /// Selects the 2 concealed tiles that complete a chow with <paramref name="discardTileId"/>.
    /// When <paramref name="chosenTileIds"/> is supplied (the modern client contract), those exact
    /// tiles are validated without mutation. When null/empty (legacy clients), falls back to the
    /// lowest-rank valid pattern. Throws <see cref="Tables.TableRuleException"/> with code
    /// <c>CHOW_TILES_INVALID</c> when supplied IDs fail validation.
    /// Runtime callers can validate here before recording a pending claim or stopping its timer.
    /// </summary>
    public static List<int> SelectChowTiles(
        ChangshaHandState hand,
        int discardTileId,
        int[]? chosenTileIds = null)
    {
        var discardLogical = ChangshaDeckBuilder.GetLogicalTile(discardTileId);

        if (chosenTileIds is { Length: > 0 })
        {
            return SelectChowTilesByChoice(hand, discardLogical, chosenTileIds);
        }

        return SelectChowTilesByLowestPattern(hand, discardLogical);
    }

    private static List<int> SelectChowTilesByChoice(
        ChangshaHandState hand,
        int discardLogical,
        int[] chosenTileIds)
    {
        if (chosenTileIds.Length != 2)
            throw new Tables.TableRuleException(
                Tables.TableActionErrorCodes.ChowTilesInvalid,
                $"Chow requires exactly 2 tile ids; got {chosenTileIds.Length}.",
                stateVersion: 0, actionSequence: 0);

        var a = chosenTileIds[0];
        var b = chosenTileIds[1];
        if (a == b)
            throw new Tables.TableRuleException(
                Tables.TableActionErrorCodes.ChowTilesInvalid,
                "Chow tile ids must be distinct.",
                stateVersion: 0, actionSequence: 0);

        if (!hand.ConcealedTiles.Contains(a) || !hand.ConcealedTiles.Contains(b))
            throw new Tables.TableRuleException(
                Tables.TableActionErrorCodes.ChowTilesInvalid,
                $"Chow tile ids [{a},{b}] are not both in the claimant's concealed hand.",
                stateVersion: 0, actionSequence: 0);

        // Validate the three tiles form a sequential chow in a single suit.
        var logicals = new[]
        {
            discardLogical,
            ChangshaDeckBuilder.GetLogicalTile(a),
            ChangshaDeckBuilder.GetLogicalTile(b)
        };
        var suits = logicals.Select(l => l / 9).Distinct().Count();
        if (suits != 1)
            throw new Tables.TableRuleException(
                Tables.TableActionErrorCodes.ChowTilesInvalid,
                $"Chow tiles must all share a suit (discard logical {discardLogical}).",
                stateVersion: 0, actionSequence: 0);

        var sorted = logicals.OrderBy(l => l).ToArray();
        if (sorted[1] - sorted[0] != 1 || sorted[2] - sorted[1] != 1)
            throw new Tables.TableRuleException(
                Tables.TableActionErrorCodes.ChowTilesInvalid,
                $"Chow tiles must be three consecutive ranks; got logicals [{sorted[0]},{sorted[1]},{sorted[2]}].",
                stateVersion: 0, actionSequence: 0);

        return [a, b];
    }

    private static List<int> SelectChowTilesByLowestPattern(ChangshaHandState hand, int discardLogical)
    {
        var rank = discardLogical % 9;

        // Try each possible chow pattern (lowest-rank first).
        var patterns = new List<(int, int)>();
        if (rank >= 2) patterns.Add((discardLogical - 2, discardLogical - 1));
        if (rank >= 1 && rank <= 7) patterns.Add((discardLogical - 1, discardLogical + 1));
        if (rank <= 6) patterns.Add((discardLogical + 1, discardLogical + 2));

        foreach (var (a, b) in patterns)
        {
            var tileA = hand.ConcealedTiles.FirstOrDefault(t => ChangshaDeckBuilder.GetLogicalTile(t) == a, -1);
            var tileB = hand.ConcealedTiles.FirstOrDefault(t => ChangshaDeckBuilder.GetLogicalTile(t) == b, -1);

            if (tileA >= 0 && tileB >= 0)
            {
                return [tileA, tileB];
            }
        }

        throw new InvalidOperationException("Cannot find tiles for chow.");
    }

    private static List<int> BuildShuffledWall(Random rng)
    {
        var wall = ChangshaDeckBuilder.Build();
        // Fisher-Yates shuffle
        for (var i = wall.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (wall[i], wall[j]) = (wall[j], wall[i]);
        }
        return wall;
    }

    private static ChangshaEvent CreateEvent(
        ChangshaGameState state,
        string eventType,
        int seatIndex,
        int? tileId = null,
        string detail = "",
        ChangshaReplayContext? replay = null)
    {
        state.EventSequence++;
        state.StateVersion++;
        var evt = new ChangshaEvent
        {
            Sequence = state.EventSequence,
            EventType = eventType,
            SeatIndex = seatIndex,
            TurnNumber = state.TurnNumber,
            TileId = tileId,
            Detail = detail,
            OccurredUtc = replay?.EventUtc() ?? DateTime.UtcNow
        };
        state.EventLog.Add(evt);
        replay?.ObserveEvent(state, evt);
        return evt;
    }
}

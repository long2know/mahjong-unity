using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Autotable;

/// <summary>
/// Pure-functional translator that converts authoritative <see cref="ChangshaGameState"/>
/// into the upstream pwmarcz/autotable collection-entry stream
/// (<c>match</c>, <c>seats</c>, <c>things</c>, <c>nicks</c>, <c>dice</c>).
///
/// <para>The translator emits a full snapshot on every call. Phase 5a uses
/// full snapshots on every state change for simplicity; incremental diffing
/// is a documented Phase 5c optimization (see
/// <c>.squad/decisions/inbox/bishop-phase5a-backend.md</c>).</para>
///
/// <para><b>Thing-index ↔ Changsha tileId mapping (locked at fives='000'):</b></para>
/// <para>The translator forces the bundle's <c>Conditions.fives = '000'</c> via the
/// <c>match</c> collection. This gives 1:1 alignment between Changsha tile ids
/// 0..107 and upstream thing-indices 0..107 (both have the same
/// <c>typeIndex = id / 4</c>). Bundle thing-indices 108..135 are winds /
/// dragons (typeIndex 27..33) and are unused in Changsha v1 — they remain at
/// the bundle's initial wall positions (Phase 5b cleanup). The translator
/// emits exactly 108 <c>things</c> entries per snapshot.</para>
///
/// <para><b>Rotation indices (from upstream <c>setup-slots.ts</c>):</b></para>
/// <list type="bullet">
///   <item><c>wall</c>: <c>[FACE_DOWN, FACE_UP]</c> — 0 = face-down (canonical wall).</item>
///   <item><c>hand</c>: <c>[STANDING, FACE_UP, FACE_DOWN]</c> — viewer's seat uses 1, others use 2.</item>
///   <item><c>discard</c>: <c>[FACE_UP, FACE_UP_SIDEWAYS, FACE_DOWN, FACE_DOWN_SIDEWAYS]</c> — 0 = face-up.</item>
///   <item><c>meld</c>: <c>[FACE_UP, FACE_UP_SIDEWAYS, FACE_DOWN]</c> — 0 = face-up, 2 = face-down (concealed kong).</item>
/// </list>
/// </summary>
public static class ChangshaToAutotableTranslator
{
    // ── Rotation index constants (see upstream src/setup-slots.ts) ────

    private const int WallRotFaceDown = 0;
    private const int HandRotStanding = 0;
    private const int HandRotFaceUp = 1;
    private const int HandRotFaceDown = 2;
    private const int DiscardRotFaceUp = 0;
    private const int MeldRotFaceUp = 0;
    private const int MeldRotFaceDown = 2;

    // ── Identity rotation (no held override) ──────────────────────────

    private static readonly object IdentityQuaternion = new
    {
        x = 0.0,
        y = 0.0,
        z = 0.0,
        w = 1.0
    };

    /// <summary>
    /// Translate a Changsha snapshot into the full set of upstream collection
    /// entries needed for the bundle to render it.
    /// </summary>
    /// <param name="state">Authoritative Changsha state. May be null when no game is bound (always-available pattern).</param>
    /// <param name="viewerSeat">Optional seat of the connected viewer. If supplied, that seat's hand tiles render face-up; other seats stay face-down.</param>
    /// <param name="viewerPlayerId">Optional player id of the viewer (used to populate the <c>seats</c> collection so the bundle's camera rotates correctly).</param>
    /// <param name="claimWindowTimeoutMs">Frost 2026-05-29 — total claim-window duration (matches
    /// <see cref="Changsha.Runtime.ChangshaRuntimeOptions.ClaimWindowTimeoutMs"/>). When &gt; 0
    /// the translator emits an absolute deadline = <c>state.ClaimWindow.OpenedAtUnixMs + claimWindowTimeoutMs</c>
    /// so the autotable client can render a meaningful countdown. When 0 (default), the deadline
    /// stays 0 — clients must treat that as "no client-side countdown" (server enforces the timeout)
    /// rather than "already expired".</param>
    public static IReadOnlyList<CollectionEntry> Translate(
        ChangshaGameState? state,
        int? viewerSeat = null,
        string? viewerPlayerId = null,
        int claimWindowTimeoutMs = 0,
        ChangshaPrivacyProjector? privacy = null)
    {
        var entries = new List<CollectionEntry>();

        // match[0] — always present; even with no game we ship the conditions
        // override so the bundle re-creates tiles with fives='000' (clean 1:1
        // thing-index ↔ Changsha tileId mapping). Without this, fives='111'
        // collapses one copy of each 5 to a red-5 (typeIndex 34..36) and our
        // mapping breaks.
        entries.Add(new CollectionEntry("match", 0, BuildMatch(state)));

        if (state is null)
        {
            // Always-available pattern: an empty-but-valid snapshot keeps the
            // bundle's 15× auto-reconnect loop quiet when no Changsha game is
            // bound to the requested gameId.
            return entries;
        }

        var ownsViewerSeat = viewerSeat.HasValue && viewerPlayerId is not null
            && state.Seats.Any(seat => seat.SeatIndex == viewerSeat.Value
                && !seat.IsBot && string.Equals(seat.PlayerId, viewerPlayerId, StringComparison.Ordinal));

        // seats — one entry per Changsha seat. Keys are synthetic player ids.
        // The viewer's playerId (if known) maps to viewerSeat; other seats use
        // their bot/human PlayerId from the Changsha state.
        foreach (var seat in state.Seats)
        {
            var key = (viewerPlayerId is not null && viewerSeat == seat.SeatIndex)
                ? viewerPlayerId
                : SeatPlayerKey(seat);
            entries.Add(new CollectionEntry("seats", key, new { seat = seat.SeatIndex }));
        }

        // nicks — display names for the score-pad readout.
        foreach (var seat in state.Seats)
        {
            var key = (viewerPlayerId is not null && viewerSeat == seat.SeatIndex)
                ? viewerPlayerId
                : SeatPlayerKey(seat);
            entries.Add(new CollectionEntry("nicks", key, SeatNickname(seat)));
        }

        // dice — Default #1 is auto-roll, so we always reflect the latest roll.
        if (state.LastDiceRoll is { } roll)
        {
            entries.Add(new CollectionEntry("dice", 0, new
            {
                dice = new[] { roll.Die1, roll.Die2 },
                state = "rolled"
            }));
        }

        // R-1 FINAL (Vasquez/Ripley, BINDING) — compute the wall front→slot mapping EXACTLY ONCE,
        // here, and share it with BOTH the wall `things` emission AND pickup.targetSlots. Capturing
        // targetSlots from the SAME computation (never a second copy of the ordinal math) makes
        // `hovered.slot ∈ targetSlots` self-consistent by construction and correct REGARDLESS of §F1
        // (F1 only shifts the whole arc anchor — SC-3 — it never desyncs these two co-derived views).
        var wallFrontSlots = ComputeWallFrontSlots(state);

        // things — 108 entries: one per Changsha tile placed at its current slot.
        foreach (var entry in BuildThingEntries(state, viewerSeat, privacy, wallFrontSlots))
        {
            entries.Add(entry);
        }

        // claim window — one entry per seat that currently has an opportunity.
        // The bundle's claim collection drives the 碰/吃/杠/胡 buttons (Phase B scene).
        //
        // BE-4 (Ripley §9.1 / RC-6) — claim-close tombstone. The bundle caches
        // `activeClaim` and only clears it on an EXPLICIT self-seat null entry
        // (game-ui.ts:onClaimUpdate); an omitted `claim` slice leaves a stale "Pung"
        // window coexisting with the discard cue. So on EVERY snapshot we emit an
        // explicit `claim[seat]=null` tombstone for every seat that does NOT currently
        // hold an opportunity (all four when no window is open). Idempotent + harmless;
        // the move-log skips null entries, the overlay clears.
        var opportunitySeats = new HashSet<int>();
        if (state.ClaimWindow is { } window && state.Phase == ChangshaPhase.AwaitingClaim)
        {
            // Frost 2026-05-29 — emit a real deadline (epoch ms) so the autotable
            // overlay + side-panel show a meaningful countdown instead of treating
            // the value as "expired now" and auto-passing.  Falls back to 0 when:
            //   • caller didn't supply ClaimWindowTimeoutMs (back-compat for tests
            //     that don't care about the deadline), OR
            //   • OpenedAtUnixMs is 0 (rehydrated state from before this field
            //     existed) — in which case the client must treat 0 as "no client
            //     timer" rather than "expired".
            var deadlineUnixMs = (claimWindowTimeoutMs > 0 && window.OpenedAtUnixMs > 0)
                ? window.OpenedAtUnixMs + claimWindowTimeoutMs
                : 0L;
            foreach (var seatGroup in window.Opportunities.GroupBy(o => o.SeatIndex))
            {
                var available = seatGroup
                    .Select(o => ClaimTypeToWire(o.ClaimType))
                    .Distinct()
                    .ToList();
                opportunitySeats.Add(seatGroup.Key);
                entries.Add(ChangshaCollectionEncoder.EncodeClaimWindow(
                    seatGroup.Key,
                    available,
                    window.DiscardSeatIndex,
                    window.DiscardTileId,
                    deadlineUnixMs: deadlineUnixMs,
                    chowOptions: ownsViewerSeat && seatGroup.Key == viewerSeat
                        && available.Contains("Chow")
                        ? BuildChowOptions(state.Hands.Single(hand => hand.SeatIndex == seatGroup.Key),
                            window.DiscardTileId)
                        : null,
                    gameId: state.GameId,
                    stateVersion: state.StateVersion));
            }
        }
        for (var seat = 0; seat < state.Seats.Count; seat++)
        {
            if (!opportunitySeats.Contains(seat))
                entries.Add(ChangshaCollectionEncoder.EncodeClaimWindowClosed(seat));

            var actions = ownsViewerSeat && seat == viewerSeat
                ? ChangshaOwnTurnActions.Available(state, seat) : null;
            entries.Add(ChangshaCollectionEncoder.EncodeOwnTurn(seat, actions is null ? null : new OwnTurnEntry
            {
                GameId = state.GameId,
                StateVersion = state.StateVersion,
                Hu = actions.Hu,
                ConcealedKongs = actions.ConcealedKongs,
                AddedKongs = actions.AddedKongs
            }));
        }

        // result — populated while the completed hand is on screen (EndHand), and explicitly
        // TOMBSTONED (result['current']=null) the moment the hand advances past EndHand.
        //
        // `result` is a NON-ephemeral collection (client.ts: `new Collection('result', this)`),
        // so AutotableGameState stores it and the WS re-ships it on every StateChanged full
        // snapshot. Without the tombstone the stored hand-1 result re-broadcasts during hand 2+,
        // and the bundle's result handler re-opens #result-modal on every broadcast — blocking
        // multi-hand play (#132). The bundle hides the modal ONLY on an explicit
        // result['current']=null, so we wire the (previously dead) EncodeHandResultCleared() here
        // as the authoritative clear. EndHand is the marker: CurrentWin+CurrentScore OR draw event.
        if (state.Phase == ChangshaPhase.EndHand)
        {
            entries.Add(ChangshaCollectionEncoder.EncodeHandResult(BuildHandResult(state)));
        }
        else
        {
            entries.Add(ChangshaCollectionEncoder.EncodeHandResultCleared());
        }

        // gameComplete — #116/#122 (Hudson P0). The authoritative end-of-match signal that
        // makes the bundle's #game-complete-modal reachable through real play. Emitted once the
        // game reaches its terminal phase (IsGameComplete, set by RotateBanker at MaxHands). The
        // existing StateChanged → translator → ApplyUpdate(Runtime) broadcast (PersistSnapshotAsync
        // fires StateChanged after RotateBanker) delivers this entry to every bound WS client;
        // there is no separate/synthetic emission path.
        if (state.IsGameComplete)
        {
            entries.Add(ChangshaCollectionEncoder.EncodeGameComplete(BuildGameComplete(state)));
        }

        // ── Phase F: pickup collection ──
        // Emitted while the manual-deal state machine is parked in any pickup phase;
        // drives the autotable scene's "Take Tiles" affordance.
        //
        // R-1 E3 (Vasquez, BINDING) — when NOT in a pickup phase (e.g. the deal completed
        // and handed off to AwaitingDiscard, or an auto game), emit an EXPLICIT pickup
        // tombstone (pickup['current']=null). Pre-E3 this was only described in a comment and
        // never emitted, so the bundle's sticky `pickup` collection kept isMyPickupTurn()
        // TRUE after the deal and left the wall wrongly interactive during the discard turn.
        if (ChangshaGameStateMachine.IsPickupPhase(state.Phase))
        {
            entries.Add(ChangshaCollectionEncoder.EncodePickup(BuildPickupEntry(state, wallFrontSlots)));
        }
        else
        {
            entries.Add(ChangshaCollectionEncoder.EncodePickupCleared());
        }

        // ── C-1/C-2: authoritative turn cue ──
        // Emit the turn cue on every snapshot (wire shape locked with Hicks:
        // { activeSeat, phase, awaitingDiscard }). While AwaitingDiscard holds, activeSeat is the
        // seat that owes the discard and awaitingDiscard is true; every path that reaches
        // AwaitingDiscard (dealer's initial 14, an ordinary auto-draw, a Chow/Pung claim, or a
        // Kong replacement draw — human or bot) sets ActiveSeatIndex to that seat, so this single
        // check covers them all (C-2). On every other phase activeSeat is an explicit null and
        // awaitingDiscard is false, retracting the cue so it can never linger past phase exit
        // (C-1's tombstone discipline). The explicit null (rather than a JS-null tombstone) is
        // deliberate: the frontend trusts an explicit null over stale `things` geometry, keeping
        // the turn authoritative rather than geometry-derived.
        var awaitingDiscard = state.Phase == ChangshaPhase.AwaitingDiscard;
        entries.Add(ChangshaCollectionEncoder.EncodeTurn(
            activeSeat: awaitingDiscard ? state.ActiveSeatIndex : null,
            phase: state.Phase.ToString(),
            awaitingDiscard: awaitingDiscard));

        return entries;
    }

    private static List<int[]> BuildChowOptions(ChangshaHandState hand, int discardedTileId)
    {
        var discarded = ChangshaDeckBuilder.GetLogicalTile(discardedTileId);
        var suitStart = discarded / 9 * 9;
        var held = hand.ConcealedTiles.GroupBy(ChangshaDeckBuilder.GetLogicalTile)
            .ToDictionary(group => group.Key, group => group.OrderBy(tile => tile).ToArray());
        var options = new List<int[]>();
        for (var start = Math.Max(suitStart, discarded - 2);
            start <= Math.Min(suitStart + 6, discarded); start++)
        {
            var partners = Enumerable.Range(start, 3).Where(logical => logical != discarded).ToArray();
            if (!held.TryGetValue(partners[0], out var first)
                || !held.TryGetValue(partners[1], out var second))
                continue;

            // Copies of one rank are interchangeable for the choice UI. Keep one
            // deterministic physical pair per distinct legal sequence.
            options.Add([first[0], second[0]]);
        }
        return options;
    }

    private static PickupEntry BuildPickupEntry(ChangshaGameState state, List<string> wallFrontSlots)
    {
        BreakPointWire? bp = null;
        if (state.BreakPoint is { } b)
        {
            bp = new BreakPointWire
            {
                WallIndex = b.WallIndex,
                StackIndex = b.StackIndex,
                TileIndex = b.TileIndex
            };
        }

        var count = ChangshaGameStateMachine.ExpectedPickupCount(state.Phase);

        var entry = new PickupEntry
        {
            Phase = state.Phase.ToString(),
            SeatIndex = state.PickupSeatIndex ?? state.DealerSeatIndex,
            Count = count,
            DealMode = state.DealMode == DealMode.Manual ? "manual" : "auto",
            BreakPoint = bp,
            // Wall front is always index 0 after BreakPointToWall rotation, but expose
            // the actual remaining-wall count so the bundle can decide UI affordances
            // (e.g., "Wall: 55 tiles left").
            WallIndex = 0
        };

        ApplyPickupTargetSlots(entry, wallFrontSlots, count);
        return entry;
    }

    /// <summary>
    /// Populates the manual-pickup match key. <b>Ripley CANONICAL KEY LOCK (2026-08-07T11:23, "no more
    /// pivots"):</b> the pickup match key is <c>pickup.targetSlots</c> (public render slots) — the opaque
    /// <c>targetHandles</c> refinement is DEAD/superseded. SC-2 opaque handles remain the render/identity
    /// key for hidden wall/foreign-hand <c>things</c> (orthogonal — a separate field, NOT the pickup key).
    /// All match-key population lives ONLY here (single seam), co-derived from the shared
    /// <see cref="ComputeWallFrontSlots"/> pass (the same slots the wall <c>things</c> were placed at — one
    /// pass, no second ordinal-math copy).
    /// <para><b>Length = SINGLE trigger</b> (<c>targetSlots = [Wall[0]]</c>, reachable top-first). This
    /// matches (a) Hicks's SHIPPED matcher <c>hovered.slot.name === targetSlots[0]</c>, which FAILS CLOSED
    /// on <c>length != 1</c>, and (b) Vasquez's rules invariant "single actionable exposed-end tile takes
    /// the whole batch." The full <c>Wall[0..count-1]</c> batch is exposed separately as display-only
    /// <see cref="PickupEntry.BatchPreviewSlots"/>; the server takes <c>Wall[0..count-1]</c> by count.</para>
    /// <para><b>No raw <c>targetTileIds</c></b> (parent 11:14): omitted here entirely. Ripley scopes
    /// <c>targetTileIds</c> as per-viewer PROJECTED reveal/animation only (never the match key); if a
    /// future reveal needs it, emit it PROJECTED (opaque handles when privacy is on) so no raw wall id
    /// ever crosses the wire. The explicit <c>EncodePickupCleared</c> tombstone is already emitted
    /// state-driven (R-1 E3) when leaving a pickup phase.</para>
    /// </summary>
    private static void ApplyPickupTargetSlots(PickupEntry entry, List<string> wallFrontSlots, int count)
    {
        entry.TargetSlots = wallFrontSlots.Count > 0
            ? new List<string> { wallFrontSlots[0] }
            : new List<string>();

        var take = Math.Min(count, wallFrontSlots.Count);
        entry.BatchPreviewSlots = wallFrontSlots.GetRange(0, Math.Max(0, take));
    }

    internal static HandResultEntry BuildHandResult(ChangshaGameState state)
    {
        var win = state.CurrentWin;
        var winnerSeat = win?.WinningSeatIndex ?? -1;
        string type;
        List<int> winningHand = [];

        if (win is not null)
        {
            type = "Hu";
            var hand = state.Hands.FirstOrDefault(h => h.SeatIndex == winnerSeat);
            if (hand is not null)
            {
                winningHand.AddRange(hand.ConcealedTiles);
                foreach (var meld in hand.Melds)
                    winningHand.AddRange(meld.TileIds);
            }
        }
        else
        {
            // Wall exhaustion or false-Hu only — caller distinguishes via FalseHuPenalties.
            type = state.FalseHuPenalties.Count > 0 ? "ZhaHu" : "Draw";
        }

        // nextBanker mirrors the runtime's RotateBanker policy: winner becomes dealer;
        // washout retains current dealer. Match the rule here without mutating state.
        var nextBanker = win is not null ? win.WinningSeatIndex : state.DealerSeatIndex;

        // Phase I Wave 1 — surface winResult + scoreResult on the bundle WS path
        // so the frontend result modal (chip strip + multiplier breakdown +
        // RobbingKong badge) renders without a second SignalR subscription.
        // Null on draw/false-Hu — frontend already handles the null case.
        var winResult = win is null ? null : new WinResultEntry
        {
            WinningSeatIndex = win.WinningSeatIndex,
            WinType = WinMethodToWire(win.Method),
            WinPattern = WinPatternToWire(win.Pattern),
            WinningTileId = win.WinningTileId,
            SourceSeatIndex = win.SourceSeatIndex,
            AllPatterns = win.AllPatterns.Select(WinPatternToWire).ToList(),
            IsRobbedKong = win.IsRobbedKong,
            // Phase J Wave 3 — explicit IsSelfDraw + IsKongReplacement axes mirrored
            // onto the bundle WS path so the autotable collection-entry payload
            // matches the SignalR WinDeclared shape (Hicks's UI consumes both
            // transports). See WinResult.IsSelfDraw / IsKongReplacement.
            IsSelfDraw = win.IsSelfDraw,
            IsKongReplacement = win.IsKongReplacement
        };

        var score = state.CurrentScore;
        var scoreResult = (win is null || score is null) ? null : new ScoreResultEntry
        {
            Category = score.Category switch
            {
                ScoreCategory.SmallWin => "smallWin",
                ScoreCategory.BigWin => "bigWin",
                _ => score.Category.ToString().ToLowerInvariant()
            },
            BasePoints = score.BasePoints,
            Payments = score.Payments.Select(p => new ScorePaymentEntry
            {
                FromSeatIndex = p.FromSeatIndex,
                ToSeatIndex = p.ToSeatIndex,
                Amount = p.Amount,
                Reason = p.Reason
            }).ToList(),
            // Post-W23 — Frost's fan catalog breakdown surfaced on the bundle WS path
            // so the win-screen modal can render per-fan chips with Chinese/Pinyin/English
            // labels without a second round-trip. Empty list = no fans applied (legacy
            // 258-pair Standard win off a discard, etc.).
            Fans = score.Fans.Select(f =>
            {
                var info = Mahjong.Autotable.Api.Changsha.Scoring.FanCatalog.Get(f.Fan);
                return new FanEntry
                {
                    Fan = ChangshaGameStateMachine.FanWireName(f.Fan),
                    Points = f.Points,
                    Chinese = info.Chinese,
                    Pinyin = info.Pinyin,
                    English = info.English,
                };
            }).ToList(),
            FanPoints = score.FanPoints,
        };

        return new HandResultEntry
        {
            Winner = winnerSeat,
            Type = type,
            // Project Dictionary<int,int> CumulativeScores → ordered List<ScoreDeltaEntry>.
            // The wire contract is an ARRAY of { seat, delta } so the frontend's
            // `[...result.score]` spread + sort works directly. Emitting the dict
            // serializes as a JSON object {"0":..., "1":...} which is NOT iterable —
            // see game-ui.ts:renderResult + ScoreDeltaEntry XML doc. OrderBy(seat)
            // gives deterministic wire ordering across snapshots; the frontend
            // re-sorts anyway, so this is purely defensive.
            Score = state.CumulativeScores
                .OrderBy(kv => kv.Key)
                .Select(kv => new ScoreDeltaEntry { Seat = kv.Key, Delta = kv.Value })
                .ToList(),
            Hand = winningHand,
            NextBanker = nextBanker,
            WinResult = winResult,
            ScoreResult = scoreResult,
            Continuation = BuildHandResultContinuation(state)
        };
    }

    private static HandResultContinuationEntry? BuildHandResultContinuation(ChangshaGameState state)
    {
        if (state.HandResultContinuation is not { } continuation || state.Phase != ChangshaPhase.EndHand)
            return null;
        var required = continuation.RequiredSeats(state);
        return new()
        {
            GameId = state.GameId,
            HandNumber = continuation.HandNumber,
            ResultToken = continuation.ResultToken,
            RequiredSeats = required,
            AcknowledgedSeats = required.Where(continuation.AcknowledgedSeats.Contains).ToArray(),
            WaitingSeats = continuation.WaitingSeats(state)
        };
    }

    /// <summary>
    /// #116/#122 — builds the authoritative end-of-match payload from the terminal state.
    /// Mirrors the SignalR <c>GameCompleted</c> event's data (cumulative scores + hand cap) onto
    /// the autotable collection wire shape the bundle consumes. Winner is derived client-side from
    /// the highest total, so no winner field is required here.
    /// </summary>
    internal static GameCompleteEntry BuildGameComplete(ChangshaGameState state) => new()
    {
        IsComplete = true,
        MaxHands = state.MaxHands,
        TotalScores = state.CumulativeScores
            .OrderBy(kv => kv.Key)
            .ToDictionary(
                kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                kv => kv.Value)
    };

    // Phase I Wave 1 — wire mappings mirror Runtime/ChangshaGameRuntime.cs so
    // the bundle path emits identical strings to the SignalR path.
    private static string WinMethodToWire(WinMethod m) => m switch
    {
        WinMethod.SelfDraw => "selfDraw",
        WinMethod.Discard => "discard",
        WinMethod.RobbingKong => "robbingKong",
        _ => m.ToString().ToLowerInvariant()
    };

    private static string WinPatternToWire(WinPattern p) => p switch
    {
        WinPattern.Standard => "standard",
        WinPattern.SevenPairs => "sevenPairs",
        WinPattern.AllPungs => "allPungs",
        WinPattern.FullFlush => "fullFlush",
        WinPattern.NineTerminals => "nineTerminals",
        WinPattern.HeavenlyHand => "heavenlyHand",
        WinPattern.EarthlyHand => "earthlyHand",
        WinPattern.LastTileFromWall => "lastTileFromWall",
        WinPattern.LastDiscardCatch => "lastDiscardCatch",
        WinPattern.KongReplacementWin => "kongReplacementWin",
        _ => "standard"
    };

    private static string ClaimTypeToWire(Tables.TableClaimType t) => t switch
    {
        Tables.TableClaimType.Hu => "Hu",
        Tables.TableClaimType.Kong => "Kong",
        Tables.TableClaimType.Pung => "Pung",
        Tables.TableClaimType.Chow => "Chow",
        _ => "Pass"
    };

    // ── match ────────────────────────────────────────────────────────

    private static object BuildMatch(ChangshaGameState? state)
    {
        // BE-1 (Ripley §9.1 / RC-1/RC-10/RC-13) — authoritative match identity.
        //
        // The pre-BE-1 payload hardcoded gameType="FOUR_PLAYER" + dealType="INITIAL"
        // (a Riichi-only legacy compat lie): the bundle's `updateVariantBadge` reads
        // conditions.gameType and painted "🎴 Riichi 4p" over a Changsha game, and an
        // Auto game booted MANUAL/INITIAL (all-in-walls, face-down) because no
        // authoritative dealMode was surfaced. We now surface the TRUE variant and
        // deal identity while keeping the tile-catalog decoupled:
        //
        //   • gameType   = "CHANGSHA"  — honest; the bundle already pins gameType to
        //                  the URL variant (world.onMatch), so this is a no-op for the
        //                  108-tile catalog/geometry yet stops the surfaced lie.
        //   • variant    = "changsha" — dedicated trusted field FE-4 reads for every
        //                  label/badge/body-class (decoupled from the catalog selector).
        //   • fives      = "000"       — UNCHANGED: red-5 disabled ⇒ clean 1:1
        //                  thing-index==tileId, typeIndex==tileId/4. Catalog stays 108.
        //   • dealMode   = auto|manual — authoritative (RC-13); the chrome derives
        //                  manual-pickup vs auto expectations from THIS, not a default.
        //   • dealType   = HANDS (auto, hands already dealt) | INITIAL (manual, pre-deal
        //                  all-in-walls) — authoritative, removes the INITIAL lie for Auto.
        var mode = state?.DealMode ?? DealMode.Auto;
        var conditions = new
        {
            gameType = "CHANGSHA",
            variant = "changsha",
            back = 0,
            fives = "000",
            points = "25",
            baseUnit = state?.BaseUnit ?? 1,
            dealMode = mode == DealMode.Manual ? "manual" : "auto",
            dealType = mode == DealMode.Manual ? "INITIAL" : "HANDS"
        };

        var dealer = state?.DealerSeatIndex ?? 0;
        // Changsha has no honba counter — keep zero.
        return new { dealer, honba = 0, conditions };
    }

    // ── things ───────────────────────────────────────────────────────

    /// <summary>
    /// R-1 FINAL (Vasquez/Ripley) — the SINGLE source of the wall front→slot mapping. Index i is the
    /// render slot of the i-th tile from the exposed front (state.Wall[i]); the pre-deal synthesized
    /// wall walks ordinal 0..107. Called ONCE per snapshot and shared by the wall `things` emission and
    /// pickup.targetSlots so the two can never diverge (and stay consistent under any §F1 anchor).
    /// </summary>
    private static List<string> ComputeWallFrontSlots(ChangshaGameState state)
    {
        var slots = new List<string>();
        if (ShouldSynthesizeWall(state))
        {
            for (var ordinal = 0; ordinal < AutotableSlotMap.TotalTiles; ordinal++)
            {
                var (seat, col, layer) = AutotableSlotMap.WallOrdinalToSlot(ordinal);
                slots.Add(AutotableSlotMap.WallSlot(seat, col, layer));
            }
            return slots;
        }

        var dealerOrigin = AutotableSlotMap.WallDealerOriginOrdinal(state.DealerSeatIndex);
        var breakOrdinal = state.BreakPoint is { } bp ? dealerOrigin + bp.TileIndex : dealerOrigin;
        var frontDrawn = AutotableSlotMap.TotalTiles - state.Wall.Count - state.WallBackDrawn;
        if (frontDrawn < 0) frontDrawn = 0;
        for (var i = 0; i < state.Wall.Count; i++)
        {
            var (seat, col, layer) = AutotableSlotMap.WallOrdinalToSlot(breakOrdinal + frontDrawn + i);
            slots.Add(AutotableSlotMap.WallSlot(seat, col, layer));
        }
        return slots;
    }

    private static IEnumerable<CollectionEntry> BuildThingEntries(
        ChangshaGameState state,
        int? viewerSeat,
        ChangshaPrivacyProjector? privacy,
        List<string> wallFrontSlots)
    {
        // Compute per-seat discard layout up front. Discards land row-major
        // into a 3×6 radial tray per seat (upstream supports up to 18 + 4
        // overflow slots; Changsha rarely exceeds the primary grid in v1).
        var perSeatDiscardCounts = new int[4];

        // Track which tiles have been placed (sanity check — every Changsha
        // tileId 0..107 must end up exactly once across hands+melds+discards+wall).
        var placedTiles = new HashSet<int>();

        // Hands (concealed + melds).
        foreach (var hand in state.Hands.OrderBy(h => h.SeatIndex))
        {
            var seat = hand.SeatIndex;
            var concealedRotation = (viewerSeat == seat) ? HandRotFaceUp : HandRotFaceDown;

            for (var i = 0; i < hand.ConcealedTiles.Count; i++)
            {
                var tileId = hand.ConcealedTiles[i];
                var slot = AutotableSlotMap.HandSlot(seat, i);
                placedTiles.Add(tileId);
                // SC-2/G19 entitlement: own concealed hand is visible; a foreign concealed hand is hidden.
                yield return BuildThingEntry(tileId, slot, concealedRotation, seat != viewerSeat, privacy);
            }

            for (var m = 0; m < hand.Melds.Count; m++)
            {
                var meld = hand.Melds[m];
                // Concealed kong shows face-down to indicate the tiles are
                // hidden; all other melds are exposed.
                var isConcealedKong = meld.Kind == MeldKind.ConcealedKong;
                var rotation = isConcealedKong ? MeldRotFaceDown : MeldRotFaceUp;
                // SC-2/G19 entitlement: exposed melds (chow/pung/exposed+added kong) are public;
                // a concealed kong is visible only to its OWNER, hidden from everyone else.
                var meldHidden = isConcealedKong && seat != viewerSeat;
                int? addedTileId = meld.Kind == MeldKind.AddedKong
                    ? AddedKongTileId(state, seat, meld)
                    : null;
                var flatIndex = 0;

                for (var t = 0; t < meld.TileIds.Count; t++)
                {
                    var tileId = meld.TileIds[t];
                    var slot = tileId == addedTileId
                        ? AutotableSlotMap.AddedKongSlot(seat, m)
                        : AutotableSlotMap.MeldSlot(seat, m, flatIndex++);
                    placedTiles.Add(tileId);
                    yield return BuildThingEntry(tileId, slot, rotation, meldHidden, privacy);
                }
            }
        }

        // Discards — fall back to a flat 3×6 grid per seat (Phase 5a overflow
        // beyond 18 wraps around the last column; v1 hands rarely exceed 24
        // discards per seat so this is acceptable for MVP).
        foreach (var discard in state.DiscardPile)
        {
            var seat = discard.SeatIndex;
            var index = perSeatDiscardCounts[seat]++;
            var row = Math.Min(2, index / 6);
            var col = Math.Min(5, index % 6);
            placedTiles.Add(discard.TileId);
            // SC-2/G19 entitlement: ALL discards are public → real tileId (never a handle).
            yield return BuildThingEntry(
                discard.TileId,
                AutotableSlotMap.DiscardSlot(seat, row, col),
                DiscardRotFaceUp, hidden: false, privacy);
        }

        // Wall — remaining tiles. Place them into wall slots in canonical
        // 14/14/13/13 order. After deal there are 55 wall tiles; during the
        // manual-pickup ceremony there are 108..55 wall tiles depending on
        // pickup progress. In Seating (before any deal) and RollingDice
        // (manual: after StartGame, before BeginManualDeal materializes the
        // shuffled wall) state.Wall is empty.
        //
        // Stephen 2026-05-27 face-down-walls directive: the bundle MUST show
        // four canonical face-down walls from the moment the user connects
        // through the full pickup ceremony. When the authoritative wall is
        // empty AND no tiles have been dealt out yet (no hands, no melds,
        // no discards) we synthesize a 108-tile face-down placement so the
        // bundle's local "HANDS"-style dealType animation is overridden by
        // the authoritative snapshot the moment the deal click round-trips
        // to the server. Tile ids are emitted in canonical order (0..107);
        // the actual shuffled ordering is materialized later by
        // BeginManualDeal — at which point state.Wall takes over.
        // Wall — remaining tiles placed at STABLE physical slots so the live
        // wall depletes contiguously from the dice break point around the
        // perimeter (issue #152) instead of being re-packed/scattered evenly
        // across all four seats on every mutation.
        //
        // Each remaining tile maps from its authoritative rules-engine flat wall
        // index to a render-ring ordinal:
        //   ordinal = WallDealerOriginOrdinal(dealer) + (BreakPoint.TileIndex + frontDrawn + listIndex)   (mod 108)
        // where frontDrawn = 108 - Wall.Count - WallBackDrawn. The rules engine's
        // flat wall index 0 is the first tile of the DEALER's physical wall
        // (BreakPointService counts counter-clockwise from there), so anchoring on
        // WallDealerOriginOrdinal(dealer) reproduces the true draw order as ONE
        // contiguous arc for every dealer seat 0–3 (C-3). Because a front-draw
        // decrements Wall.Count and shifts every remaining tile's listIndex down by
        // one, `frontDrawn + listIndex` is invariant per tile — a given tile keeps
        // the same slot until it is itself drawn, and the drawn slots simply go
        // empty. Kong replacements draw from the BACK (state.WallBackDrawn), so they
        // consume the far end of the arc while ordinary front draws consume the near
        // (break) end — both leave a single contiguous middle arc.
        //
        // Pre-deal (Seating / RollingDice, empty wall, nothing dealt) still
        // synthesizes a full 108-tile face-down wall so the four canonical
        // walls are visible from connect through the pickup ceremony
        // (Stephen 2026-05-27). A full wall fills every slot regardless of
        // anchor, so the ordinal walk is identity there.
        if (ShouldSynthesizeWall(state))
        {
            for (var ordinal = 0; ordinal < AutotableSlotMap.TotalTiles; ordinal++)
            {
                placedTiles.Add(ordinal);
                // SC-2/G19: ALL wall tiles are hidden from every viewer (pre-take) → opaque handle.
                // Slot comes from the shared ComputeWallFrontSlots pass (no second ordinal math).
                yield return BuildThingEntry(ordinal, wallFrontSlots[ordinal], WallRotFaceDown, hidden: true, privacy);
            }
        }
        else
        {
            if (state.Wall.Count > AutotableSlotMap.TotalTiles)
            {
                throw new InvalidOperationException(
                    $"Wall slot capacity exceeded — state.Wall has {state.Wall.Count} tiles " +
                    $"but only {AutotableSlotMap.TotalTiles} canonical Changsha wall slots exist.");
            }

            for (var i = 0; i < state.Wall.Count; i++)
            {
                placedTiles.Add(state.Wall[i]);
                // SC-2/G19: ALL wall tiles hidden from every viewer (incl. the pre-take pickup target) →
                // opaque handle. Slot is the co-derived wallFrontSlots[i] (same pass as pickup.targetSlots).
                yield return BuildThingEntry(state.Wall[i], wallFrontSlots[i], WallRotFaceDown, hidden: true, privacy);
            }
        }
    }

    private static int AddedKongTileId(ChangshaGameState state, int seat, Meld meld)
    {
        if (meld.TileIds.Count != 4 || meld.TileIds.Distinct().Count() != 4)
            throw new InvalidOperationException("An added-Kong layout requires four distinct physical tiles.");

        // TileIds are sorted by the rules engine. The committed event, retained
        // in snapshots, identifies the added copy without moving the old Pung.
        for (var index = state.EventLog.Count - 1; index >= 0; index--)
        {
            var entry = state.EventLog[index];
            if (entry.EventType is "tiles-dealt" or "manual-deal-begun" or "game-created")
                break;
            if (entry.EventType == "added-kong" && entry.SeatIndex == seat
                && entry.TileId is int tileId && meld.TileIds.Contains(tileId))
                return tileId;
        }

        // Legacy/imported snapshots may omit event history. Keep all four IDs
        // and choose a stable display copy; no rule or ownership state changes.
        return meld.TileIds.Max();
    }

    /// <summary>
    /// True when the authoritative wall is empty but the table has not yet
    /// distributed any tiles, so the translator should synthesize a 108-tile
    /// face-down wall placement to keep the four canonical walls visible.
    /// Gates strictly on <see cref="ChangshaPhase.Seating"/> and
    /// <see cref="ChangshaPhase.RollingDice"/> — the only two pre-deal phases
    /// where state.Wall can legitimately be empty. End-of-hand / wall-exhausted
    /// / game-complete states also have an empty wall but already have hands,
    /// discards, or melds, so they fall through to the authoritative path.
    /// </summary>
    private static bool ShouldSynthesizeWall(ChangshaGameState state)
    {
        if (state.Wall.Count != 0) return false;
        if (state.Phase is not (ChangshaPhase.Seating or ChangshaPhase.RollingDice)) return false;
        if (state.DiscardPile.Count != 0) return false;
        foreach (var hand in state.Hands)
        {
            if (hand.ConcealedTiles.Count != 0) return false;
            if (hand.Melds.Count != 0) return false;
        }
        return true;
    }

    private static CollectionEntry BuildThingEntry(int changshaTileId, string slotName, int rotationIndex,
        bool hidden = false, ChangshaPrivacyProjector? privacy = null)
    {
        // Wire KEY: real Changsha tileId when the tile is visible to the viewer (client resolves
        // face via tileId/4). SC-2/G19 — when the tile is HIDDEN from the viewer and a per-viewer
        // privacy projector is supplied, the key becomes an opaque server-secret handle that reveals
        // no rank/suit. With no projector (pre-SC-2 / privacy disabled) the key stays the real tileId,
        // preserving the current bundle's key==tileId contract.
        object key = privacy is not null ? privacy.Key(changshaTileId, hidden) : changshaTileId;

        // Typed ThingInfo (not an anonymous object) so claimedBy / shiftSlotName carry an EXPLICIT
        // null on the wire — the C-1 contract types them number|null / string|null (present), and
        // the relay path already emits explicit null. An anonymous object's nulls are dropped by the
        // shared WhenWritingNull serializer; ThingInfo's [JsonIgnore(Never)] overrides that.
        return new CollectionEntry("things", key, new ThingInfo
        {
            SlotName = slotName,
            RotationIndex = rotationIndex,
            ClaimedBy = null,
            HeldRotation = IdentityQuaternion,
            ShiftSlotName = null
        });
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static string SeatPlayerKey(ChangshaSeatState seat) =>
        string.IsNullOrEmpty(seat.PlayerId) ? $"seat-{seat.SeatIndex}" : seat.PlayerId;

    private static string SeatNickname(ChangshaSeatState seat) =>
        seat.IsBot ? $"Bot {seat.SeatIndex}" : $"Seat {seat.SeatIndex}";
}

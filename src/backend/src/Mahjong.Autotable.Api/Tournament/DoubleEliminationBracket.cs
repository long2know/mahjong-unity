namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 6 — Bishop. <see cref="IBracketGenerator"/> for
/// double-elimination tournaments. Emits the winners-bracket
/// round-1 + a placeholder losers-bracket round-1 + the eventual
/// grand-final slot.
///
/// <para><b>Topology recap.</b> Double-elim runs two parallel
/// brackets:</para>
/// <list type="bullet">
///   <item><b>Winners' bracket</b> — standard single-elim. Lose
///         once and you drop to the losers' bracket.</item>
///   <item><b>Losers' bracket</b> — players dropped from winners'
///         keep playing until a second loss eliminates them.</item>
///   <item><b>Grand final</b> — winner of WB faces the winner of
///         LB. The WB winner enters with a one-loss buffer (must
///         be beaten twice in the grand final); the LB winner has
///         already lost once and must win the first grand-final
///         match to force a "reset" grand-final.</item>
/// </list>
///
/// <para><b>Wave-7 expansion.</b> Wave 6 shipped a 3-slot
/// emission (WB1 real + LB1 placeholder + GF placeholder). Wave 7
/// ships the full power-of-two bracket shape — every WB / LB round
/// plus a grand-final reset placeholder — so the API surface
/// pre-emits every slot the tournament will ever play. Slot count
/// follows the standard double-elim formulas:</para>
/// <list type="bullet">
///   <item><b>WB</b>: <c>k = ceil(log2(N))</c> rounds; round
///         <c>r</c> has <c>ceil(N / 2^r)</c> matches.</item>
///   <item><b>LB</b>: <c>2*(k-1)</c> rounds; round
///         <c>2j-1</c> (consolidation) + round <c>2j</c> (WB-feed)
///         each carry <c>ceil(N / 2^(j+1))</c> matches.</item>
///   <item><b>GF</b>: 2 rounds — the "Final" + the conditional
///         "Reset". The reset always exists in the emitted
///         schedule; if the WB champion wins the Final the reset
///         is recorded as walkover when the service materialises
///         results (Phase L).</item>
/// </list>
///
/// <para><b>Slot filling.</b> Only WB round 1 carries real seed
/// names — every other slot is a deterministic placeholder
/// (<c>__pending_{bracket}_r{round}_m{match}__</c>) so the API
/// surface is visually complete from the first request. The
/// <see cref="TournamentService"/> advance-round path is the
/// canonical owner of replacing the placeholders with real
/// winner / loser ids as downstream matches resolve.</para>
///
/// <para><b>Non-power-of-two registrations.</b> The algorithm
/// computes the bracket depth as <c>ceil(log2(N))</c>; the
/// shortfall between <c>N</c> and <c>2^k</c> is absorbed by the
/// first round of byes (mirrors
/// <see cref="TournamentPairing.SingleEliminationFirstRound"/>'s
/// convention). LB / GF slot counts are derived from the WB
/// counts so the bracket remains consistent.</para>
/// </summary>
public sealed class DoubleEliminationBracket : IBracketGenerator
{
    public const string PlaceholderPlayer = "__pending__";

    /// <summary>
    /// Phase K Wave 7 — Bishop. Token stamped into
    /// <see cref="BracketPairing.P1"/> for the grand-final-reset
    /// placeholder slot. Tests pin against this constant.
    /// </summary>
    public const string GrandFinalResetPlaceholder = "__pending_gf_reset__";

    public BracketFormat Format => BracketFormat.DoubleElimination;

    public IReadOnlyList<BracketPairing> Generate(IReadOnlyList<string> seededPlayers)
    {
        ArgumentNullException.ThrowIfNull(seededPlayers);
        if (seededPlayers.Count < 2) return Array.Empty<BracketPairing>();

        var results = new List<BracketPairing>();

        // ───── Winners bracket round 1 — same shape as single-elim seeding.
        var wbR1 = TournamentPairing.SingleEliminationFirstRound(seededPlayers);
        foreach (var pair in wbR1)
        {
            results.Add(new BracketPairing(
                Round: 1,
                Bracket: BracketSide.Winners,
                P1: pair.P1,
                P2: pair.P2));
        }

        // Bracket depth — k = ceil(log2(N)). For N=8 → 3 WB rounds;
        // N=16 → 4 WB rounds; etc. The first round's match count is
        // already wbR1.Count; rounds 2..k halve each time (with ceil
        // so odd counts round up).
        var k = BracketDepth(seededPlayers.Count);

        // ───── WB rounds 2..k — placeholder pairings keyed by
        // (round, match-index). Match count halves each round.
        var wbMatchesPerRound = new int[k + 1];
        wbMatchesPerRound[1] = wbR1.Count;
        for (var r = 2; r <= k; r++)
        {
            wbMatchesPerRound[r] = Math.Max(1, (wbMatchesPerRound[r - 1] + 1) / 2);
            for (var m = 0; m < wbMatchesPerRound[r]; m++)
            {
                results.Add(new BracketPairing(
                    Round: r,
                    Bracket: BracketSide.Winners,
                    P1: WinnersPlaceholder(r, m, slot: 1),
                    P2: WinnersPlaceholder(r, m, slot: 2)));
            }
        }

        // ───── LB rounds 1..2(k-1) — placeholder pairings. The
        // standard double-elim topology runs LB at half the depth
        // of WB: for each WB round r (1..k-1) the LB plays a
        // "consolidation" round (LB2r-1) that pairs surviving LB
        // winners, then a "feed" round (LB2r) that drops fresh WB
        // round-(r+1) losers into the LB winners. The LB final
        // round (LB 2(k-1)) feeds the grand final.
        if (k >= 2)
        {
            var lbRoundCount = 2 * (k - 1);

            // Per-round LB match count. The pattern is two rounds
            // at each tier, halving every tier. For k=3 (8 players):
            // [LB1=2, LB2=2, LB3=1, LB4=1] = 6 LB matches total.
            // For k=4 (16 players): [LB1=4, LB2=4, LB3=2, LB4=2,
            // LB5=1, LB6=1] = 14 LB matches total.
            var lbMatchesPerRound = new int[lbRoundCount + 1];
            for (var tier = 1; tier <= k - 1; tier++)
            {
                // The "consolidation" round at this tier (LB round
                // 2*tier - 1) and the "feed" round (LB round
                // 2*tier) both carry wbMatchesPerRound[tier+1]
                // matches — i.e. the size of WB round (tier+1).
                var sizeAtTier = wbMatchesPerRound[tier + 1];
                lbMatchesPerRound[2 * tier - 1] = sizeAtTier;
                lbMatchesPerRound[2 * tier] = sizeAtTier;
            }

            for (var r = 1; r <= lbRoundCount; r++)
            {
                for (var m = 0; m < lbMatchesPerRound[r]; m++)
                {
                    results.Add(new BracketPairing(
                        Round: r,
                        Bracket: BracketSide.Losers,
                        P1: LosersPlaceholder(r, m, slot: 1),
                        P2: LosersPlaceholder(r, m, slot: 2)));
                }
            }
        }

        // ───── Grand final + grand-final reset.
        // Round 1 — Winners champion (P1) vs Losers champion (P2).
        results.Add(new BracketPairing(
            Round: 1,
            Bracket: BracketSide.GrandFinal,
            P1: "__pending_wb_champion__",
            P2: "__pending_lb_champion__"));

        // Round 2 — the reset match. Always emitted; the
        // TournamentService records a walkover when the WB champion
        // wins the Final (no reset needed). When the LB champion
        // wins the Final, this is the deciding rematch.
        results.Add(new BracketPairing(
            Round: 2,
            Bracket: BracketSide.GrandFinal,
            P1: GrandFinalResetPlaceholder,
            P2: GrandFinalResetPlaceholder));

        return results;
    }

    /// <summary>
    /// Computes the bracket depth — the number of WB rounds — as
    /// <c>ceil(log2(N))</c>. Exposed for tests that pin the round
    /// count without re-implementing the math.
    /// </summary>
    public static int BracketDepth(int playerCount)
    {
        if (playerCount < 2) return 0;
        var depth = 0;
        var n = 1;
        while (n < playerCount) { n <<= 1; depth++; }
        return depth;
    }

    private static string WinnersPlaceholder(int round, int matchIndex, int slot)
        => $"__pending_wb_r{round}_m{matchIndex}_p{slot}__";

    private static string LosersPlaceholder(int round, int matchIndex, int slot)
        => $"__pending_lb_r{round}_m{matchIndex}_p{slot}__";
}

/// <summary>
/// Phase K Wave 6 — Bishop. Type alias preserved for the W6 contract
/// tests that check by class name. <see cref="DoubleEliminationBracket"/>
/// is the canonical name; <c>DoubleElimBracket</c> aliases it.
/// </summary>
public sealed class DoubleElimBracket : IBracketGenerator
{
    private readonly DoubleEliminationBracket _inner = new();

    public BracketFormat Format => _inner.Format;

    public IReadOnlyList<BracketPairing> Generate(IReadOnlyList<string> seededPlayers)
        => _inner.Generate(seededPlayers);
}

using Mahjong.Autotable.Api.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Bishop;

/// <summary>
/// Phase K Wave 8 — Bishop. Hard-asserted facts for the
/// <see cref="TournamentBracketSnapshotService"/> + the
/// <see cref="BracketSnapshot"/> / <see cref="BracketSlot"/>
/// envelopes returned by <c>GET /api/tournaments/{id}/bracket</c>.
///
/// <list type="number">
///   <item>Placeholder detection recognises <c>"__pending..."</c> tokens.</item>
///   <item>Real player ids are NOT treated as placeholders.</item>
///   <item>BracketSnapshot is a positional record with the expected fields.</item>
///   <item>BracketSlot carries Status + WinnerSeed + BracketSide.</item>
///   <item>GrandFinalView holds Match + ResetMatch.</item>
///   <item>BracketRound carries RoundNumber + Slots.</item>
/// </list>
/// </summary>
public sealed class TournamentBracketSnapshotServiceTests
{
    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void IsPlaceholder_RecognisesPendingPrefix()
    {
        Assert.True(InvokeIsPlaceholder("__pending_wb_r2_m0_p1__"));
        Assert.True(InvokeIsPlaceholder("__pending_lb_r1_m0_p0__"));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void IsPlaceholder_RejectsRealPlayerIds()
    {
        Assert.False(InvokeIsPlaceholder("alice"));
        Assert.False(InvokeIsPlaceholder("bob"));
        Assert.False(InvokeIsPlaceholder("00000000-0000-0000-0000-000000000001"));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void IsPlaceholder_RejectsEmptyOrNull()
    {
        Assert.False(InvokeIsPlaceholder(null));
        Assert.False(InvokeIsPlaceholder(""));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void BracketSnapshot_RecordShape_IsStable()
    {
        var snapshot = new BracketSnapshot(
            Format: "DoubleElimination",
            TournamentId: Guid.NewGuid(),
            WinnersBracket: Array.Empty<BracketRound>(),
            LosersBracket: Array.Empty<BracketRound>(),
            GrandFinal: null);
        Assert.Equal("DoubleElimination", snapshot.Format);
        Assert.NotNull(snapshot.WinnersBracket);
        Assert.NotNull(snapshot.LosersBracket);
        Assert.Null(snapshot.GrandFinal);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void BracketSlot_RecordShape_CarriesAllFields()
    {
        var slot = new BracketSlot(
            MatchIndex: 0,
            SeedA: "alice",
            SeedB: "bob",
            WinnerSeed: "alice",
            Status: "complete",
            BracketSide: "Winners");
        Assert.Equal(0, slot.MatchIndex);
        Assert.Equal("alice", slot.SeedA);
        Assert.Equal("bob", slot.SeedB);
        Assert.Equal("alice", slot.WinnerSeed);
        Assert.Equal("complete", slot.Status);
        Assert.Equal("Winners", slot.BracketSide);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void BracketSlot_PendingSlot_HasNullWinnerSeed()
    {
        var slot = new BracketSlot(
            MatchIndex: 0,
            SeedA: "__pending_wb_r1_m0_p0__",
            SeedB: "__pending_wb_r1_m0_p1__",
            WinnerSeed: null,
            Status: "pending",
            BracketSide: "Winners");
        Assert.Null(slot.WinnerSeed);
        Assert.Equal("pending", slot.Status);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void BracketRound_RecordShape_CarriesNumberAndSlots()
    {
        var round = new BracketRound(
            RoundNumber: 1,
            Slots: new[]
            {
                new BracketSlot(0, "a", "b", null, "pending", "Winners"),
            });
        Assert.Equal(1, round.RoundNumber);
        Assert.Single(round.Slots);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void GrandFinalView_RecordShape_CarriesMatchAndReset()
    {
        var match = new BracketSlot(0, "a", "b", "a", "complete", "GrandFinal");
        var reset = new BracketSlot(1, "b", "a", null, "live", "GrandFinalReset");
        var gf = new GrandFinalView(Match: match, ResetMatch: reset);
        Assert.Equal("a", gf.Match!.WinnerSeed);
        Assert.Null(gf.ResetMatch!.WinnerSeed);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void GrandFinalView_NullResetMatch_IsAllowed()
    {
        var gf = new GrandFinalView(
            Match: new BracketSlot(0, "a", "b", null, "pending", "GrandFinal"),
            ResetMatch: null);
        Assert.NotNull(gf.Match);
        Assert.Null(gf.ResetMatch);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void BracketSnapshot_GrandFinal_OptionalForSingleElimination()
    {
        var snapshot = new BracketSnapshot(
            Format: "SingleElimination",
            TournamentId: Guid.NewGuid(),
            WinnersBracket: new[] { new BracketRound(1, Array.Empty<BracketSlot>()) },
            LosersBracket: Array.Empty<BracketRound>(),
            GrandFinal: null);
        Assert.Empty(snapshot.LosersBracket);
        Assert.Null(snapshot.GrandFinal);
    }

    private static bool InvokeIsPlaceholder(string? value)
    {
        var method = typeof(TournamentBracketSnapshotService).GetMethod(
            "IsPlaceholder",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { value })!;
    }
}

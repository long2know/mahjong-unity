#if TESTING_SHIM
using Mahjong.Autotable.Api.Tests.Shims;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W6;

/// <summary>
/// Phase K Wave 6 — Vasquez. Sanity coverage for the new
/// <see cref="CommentaryGeneratorTestShim"/> (parallel to the
/// W5 <c>TestHttpClientExtensions.WithDirectSession</c> shim
/// sanity facts).
///
/// <para>Pins four contracts:</para>
/// <list type="number">
///   <item><b>Determinism</b> — same gameId → same items across
///         two independent calls.</item>
///   <item><b>Distinctness</b> — different gameIds → different
///         items (no hash collision in the truncation).</item>
///   <item><b>Speaker rotation</b> — across 4 items, the speaker
///         visits each of the 3 canonical roster names at least
///         once.</item>
///   <item><b>Empty / null gameId</b> — throws
///         <see cref="ArgumentException"/> (no silent empty-stream).</item>
/// </list>
/// </summary>
public class CommentaryGeneratorTestShimSanityTests
{
    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-6")]
    public void Generate_SameGameId_ReturnsSameItems()
    {
        var first = CommentaryGeneratorTestShim.Generate("phase-k-w6-game-001");
        var second = CommentaryGeneratorTestShim.Generate("phase-k-w6-game-001");
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Sequence, second[i].Sequence);
            Assert.Equal(first[i].Speaker, second[i].Speaker);
            Assert.Equal(first[i].Text, second[i].Text);
        }
    }

    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-6")]
    public void Generate_DifferentGameIds_DistinctOutput()
    {
        var a = CommentaryGeneratorTestShim.Generate("game-a");
        var b = CommentaryGeneratorTestShim.Generate("game-b");
        // The hash slices MUST differ — flag a degenerate hash truncation
        // by checking item.Text inequality.
        Assert.NotEqual(a[0].Text, b[0].Text);
        Assert.NotEqual(a[3].Text, b[3].Text);
    }

    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-6")]
    public void Generate_SpeakerRotation_CoversAllRosterNames()
    {
        var items = CommentaryGeneratorTestShim.Generate("rotation-test");
        // 4 items, 3 speakers → each speaker MUST appear at least once.
        var distinctSpeakers = items.Select(i => i.Speaker).Distinct().ToHashSet();
        Assert.Equal(3, distinctSpeakers.Count);
        Assert.Contains("ShimAnalyst", distinctSpeakers);
        Assert.Contains("ShimColourCommentary", distinctSpeakers);
        Assert.Contains("ShimSidelineReporter", distinctSpeakers);
    }

    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-6")]
    public void Generate_EmptyOrNullGameId_Throws()
    {
        Assert.Throws<ArgumentException>(() => CommentaryGeneratorTestShim.Generate(""));
        Assert.Throws<ArgumentException>(() => CommentaryGeneratorTestShim.Generate("   "));
        Assert.Throws<ArgumentException>(() => CommentaryGeneratorTestShim.Generate(null!));
    }

    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-6")]
    public void HashSeed_IsLowercase64HexChars()
    {
        var hash = CommentaryGeneratorTestShim.HashSeed("some-game-id");
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"HashSeed MUST be lowercase hex; found '{c}'."));
    }

    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-6")]
    public void ProductionInterfaceShipped_ReturnsBool()
    {
        // The probe MUST NOT throw — it's a safe forward-stage check.
        // The actual return value is whatever Bishop's current state is;
        // we just assert the call is total.
        _ = CommentaryGeneratorTestShim.ProductionInterfaceShipped();
    }

    [Fact, Trait("Category", "Shim"), Trait("Wave", "Phase-K-6")]
    public void Generate_FourItems_SequenceMonotonic()
    {
        var items = CommentaryGeneratorTestShim.Generate("seq-test");
        Assert.Equal(4, items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            Assert.Equal(i + 1, items[i].Sequence);
        }
    }
}
#endif

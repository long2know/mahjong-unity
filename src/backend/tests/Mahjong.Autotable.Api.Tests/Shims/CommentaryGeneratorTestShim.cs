#if TESTING_SHIM
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Mahjong.Autotable.Api.Tests.Shims;

/// <summary>
/// Phase K Wave 6 — Vasquez. Test-only commentary-generator stub.
///
/// <para><b>Why a shim instead of using the production
/// <c>ICommentaryGenerator</c>?</b> Bishop's W6 brief delivers a
/// no-op default impl that always returns an empty <c>items</c>
/// array. For tests that need non-trivial content (the W6 Playwright
/// spec for the commentary panel, the deterministic-per-game
/// regression check), we want a stub that returns predictable
/// content keyed by <c>gameId</c> hash so the assertion is stable
/// across runs without needing a real LLM.</para>
///
/// <para><b>Gated by <c>TESTING_SHIM</c></b> — entire file
/// compiled out when the symbol is not defined. The test project
/// defines it in its csproj; the production assembly never sees
/// this code. See <c>docs/test-shims.md</c> § "Production-leakage
/// guarantee" for the verification recipe.</para>
///
/// <para><b>Surface:</b></para>
/// <list type="bullet">
///   <item><see cref="Generate(string)"/> — pure function returning
///         a deterministic list of <see cref="CommentaryItem"/>
///         records derived from a SHA-256 hash of the gameId.
///         Same gameId → same items in same order, EVERY run.</item>
///   <item><see cref="HashSeed(string)"/> — exposed so tests can
///         assert "I called Generate with X and got the hash-
///         derived content I expected".</item>
/// </list>
///
/// <para>The shim does NOT implement <c>ICommentaryGenerator</c>
/// directly because the production interface lands in Bishop's
/// W6 commit. Once the interface is shipped, an adapter file
/// (also gated by TESTING_SHIM) can wrap this static and register
/// it via DI. The current shim is consumable by direct call
/// (the sanity tests do exactly this).</para>
/// </summary>
public static class CommentaryGeneratorTestShim
{
    /// <summary>Per-game commentary item the shim emits.</summary>
    public sealed record CommentaryItem(int Sequence, string Speaker, string Text);

    /// <summary>The fixed roster of speakers — deterministic rotation.</summary>
    private static readonly string[] Speakers =
    {
        "ShimAnalyst",
        "ShimColourCommentary",
        "ShimSidelineReporter",
    };

    /// <summary>
    /// Emit a deterministic per-game commentary stream. Returns 4
    /// items rotating through <see cref="Speakers"/>. Identical
    /// gameId → identical output across runs.
    /// </summary>
    public static IReadOnlyList<CommentaryItem> Generate(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            throw new ArgumentException("gameId must be non-empty.", nameof(gameId));
        }

        var seed = HashSeed(gameId);
        var items = new List<CommentaryItem>(capacity: 4);

        // 4 deterministic items per game — keyed by sliding 8-byte
        // windows of the SHA-256 digest.
        for (var i = 0; i < 4; i++)
        {
            var speaker = Speakers[i % Speakers.Length];
            // 8 hex chars per item from the 64-char digest — fully
            // deterministic, distinguishable across games.
            var slice = seed.Substring(i * 8, 8);
            var text = $"[shim] event-{i + 1} for game {slice}";
            items.Add(new CommentaryItem(Sequence: i + 1, Speaker: speaker, Text: text));
        }

        return items;
    }

    /// <summary>
    /// SHA-256 hex digest of the gameId. Exposed so a sanity test
    /// can pin "same input → same output" without re-implementing
    /// the digest.
    /// </summary>
    public static string HashSeed(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(gameId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Phase K Wave 7 — Bishop. Emits a deterministic list of
    /// <see cref="Mahjong.Autotable.Api.Commentary.CommentaryRecord"/>
    /// values matching the finalised Phase-L JSON contract. The shim
    /// rotates phase/speaker/tile-reference values by the hash digest
    /// of <paramref name="gameId"/> so the same id maps to the same
    /// record sequence every time the test harness runs.
    ///
    /// <para>Returns 4 records per call (parity with the Wave-6
    /// <see cref="Generate"/> shape). Used by the
    /// <c>Phase_K_W7/Bishop/CommentaryRecordContractTests</c>
    /// to exercise the wire surface without standing up an LLM.</para>
    /// </summary>
    public static IReadOnlyList<Mahjong.Autotable.Api.Commentary.CommentaryRecord> GenerateRecords(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            throw new ArgumentException("gameId must be non-empty.", nameof(gameId));
        }

        var seed = HashSeed(gameId);
        var phases = new[]
        {
            Mahjong.Autotable.Api.Commentary.CommentaryPhases.Draw,
            Mahjong.Autotable.Api.Commentary.CommentaryPhases.Discard,
            Mahjong.Autotable.Api.Commentary.CommentaryPhases.Claim,
            Mahjong.Autotable.Api.Commentary.CommentaryPhases.Win,
        };
        var speakers = new[]
        {
            Mahjong.Autotable.Api.Commentary.CommentarySpeakers.PlayByPlay,
            Mahjong.Autotable.Api.Commentary.CommentarySpeakers.Color,
            Mahjong.Autotable.Api.Commentary.CommentarySpeakers.Analyst,
        };
        // Deterministic UTC timestamp per game so the record list
        // sorts identically across runs without grabbing wall-clock.
        var anchor = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var records = new List<Mahjong.Autotable.Api.Commentary.CommentaryRecord>(capacity: 4);
        for (var i = 0; i < 4; i++)
        {
            var slice = seed.Substring(i * 8, 8);
            // Map the slice nibble pair to an intensity in [0, 1].
            var hex = Convert.ToInt32(slice.Substring(0, 2), 16);
            var intensity = Math.Round(hex / 255.0, 3);
            records.Add(new Mahjong.Autotable.Api.Commentary.CommentaryRecord(
                GameId: gameId,
                TurnNumber: i + 1,
                Phase: phases[i],
                Speaker: speakers[i % speakers.Length],
                Text: $"[shim] {phases[i]} commentary for game {slice}",
                EmotionIntensity: intensity,
                TileReferences: new[] { Mahjong.Autotable.Api.Commentary.TileReference.Parse($"man{(i % 9) + 1}") },
                GeneratedAt: anchor.AddSeconds(i)));
        }
        return records;
    }

    /// <summary>
    /// Reflection probe — returns true if <c>ICommentaryGenerator</c>
    /// is present on the production assembly. Used by the sanity
    /// test to forward-stage when Bishop hasn't shipped the
    /// interface yet.
    /// </summary>
    public static bool ProductionInterfaceShipped()
    {
        var asm = typeof(Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime).Assembly;
        return asm.GetTypes().Any(t => t.Name == "ICommentaryGenerator");
    }
}
#endif

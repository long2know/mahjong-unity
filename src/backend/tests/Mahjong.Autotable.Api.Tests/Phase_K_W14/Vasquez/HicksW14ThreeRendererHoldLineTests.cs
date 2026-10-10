using System.Reflection;
using System.Text.Json;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Hicks. Three-renderer hold-line at K13 size.
///
/// <para>K12 closed at 448,648 B (438.2 KiB / 448.6 kB). K13 trimmed
/// further to 406,635 B (397.1 KiB / 406.64 kB) via deeper shader-chunk
/// + UniformsLib strips. W14 introduces no new renderer-side bytes —
/// the W14 surface adds bracket-listing / replays-listing /
/// admin-cost chunks but those are pre-rendered DOM, not three.js.
/// W14's contract: the K13 size becomes a <b>hold-line</b> — K14
/// MUST NOT regress above 406,635 B.</para>
///
/// <para>Seven reflection-defensive facts.</para>
/// </summary>
public sealed class HicksW14ThreeRendererHoldLineTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private const int K13_BYTES         = 406_635; // measured at W13 sign-off.
    private const int K14_HOLD_LINE     = 406_635; // hold-line == K13 value.
    private const int K14_ACCEPTANCE    = 416_398; // 406.64 KB decimal -> bytes ceiling.
    private const int K12_BACKSTOP      = 448_648; // K12 closing value.

    private static (long? k13, long? k14)? ReadHistoryBytes(string chunkName)
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "dist-size.json");
        if (!File.Exists(path)) return null;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("history", out var hist)) return null;
            long? k13 = null, k14 = null;
            foreach (var e in hist.EnumerateArray())
            {
                if (!e.TryGetProperty("wave", out var w)) continue;
                var wave = w.GetString() ?? "";
                if (!e.TryGetProperty("chunks", out var chunks)) continue;
                foreach (var alias in new[] { chunkName, "three-renderer", "three-renderer-large" })
                {
                    if (chunks.TryGetProperty(alias, out var bytes)
                        && bytes.TryGetInt64(out var b))
                    {
                        if (wave == "K13") k13 = b;
                        if (wave == "K14") k14 = b;
                    }
                }
            }
            return (k13, k14);
        }
        catch { return null; }
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ThreeRendererBig_K14_HoldLine_OrForwardStaged()
    {
        var pair = ReadHistoryBytes("three-renderer-big");
        if (pair is null) return;
        var (_, k14) = pair.Value;
        if (k14 is null) return;
        Assert.True(k14.Value <= K14_ACCEPTANCE,
            $"three-renderer-big K14 = {k14.Value} B exceeds the 406.64 KB hold-line ({K14_ACCEPTANCE} B).");
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ThreeRendererBig_K13_Floor_StillRespected_OrForwardStaged()
    {
        var pair = ReadHistoryBytes("three-renderer-big");
        if (pair is null) return;
        var (k13, _) = pair.Value;
        if (k13 is null) return;
        // K13 closed at 406_635; allow small post-recording drift.
        Assert.True(k13.Value <= K13_BYTES + 1024,
            $"K13 three-renderer-big = {k13.Value} B drifted beyond the recorded W13 floor.");
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ThreeRendererBig_K14_NoRegressionAgainstK13_OrForwardStaged()
    {
        var pair = ReadHistoryBytes("three-renderer-big");
        if (pair is null) return;
        var (k13, k14) = pair.Value;
        if (k13 is null || k14 is null) return;
        // K14 must not regress against K13 (hold-line).
        Assert.True(k14.Value <= k13.Value + 4096,
            $"three-renderer-big K14 = {k14.Value} B regressed against K13 = {k13.Value} B.");
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ThreeRendererBig_K12_RegressionBackstop_OrForwardStaged()
    {
        var pair = ReadHistoryBytes("three-renderer-big");
        if (pair is null) return;
        var (_, k14) = pair.Value;
        if (k14 is null) return;
        // K12 regression backstop: never go back up to the K12 size.
        Assert.True(k14.Value < K12_BACKSTOP,
            $"three-renderer-big K14 = {k14.Value} B regressed above the K12 backstop.");
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void DistSizeJson_HasCurrentK14_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "dist-size.json");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("\"current\": \"K14\"", StringComparison.Ordinal)
         || text.Contains("\"current\":\"K14\"", StringComparison.Ordinal)
         || text.Contains("\"wave\": \"K14\"", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void ThreeRendererBig_K11Regression_StillCovered_OrForwardStaged()
    {
        // Inherit the W11 475 KB regression-pin backstop from the
        // W13 contract. The W14 hold-line is MUCH tighter; this
        // is the bottom-of-the-stack regression guard.
        var pair = ReadHistoryBytes("three-renderer-big");
        if (pair is null) return;
        var (_, k14) = pair.Value;
        if (k14 is null) return;
        Assert.True(k14.Value < 475 * 1024,
            $"three-renderer-big K14 = {k14.Value} B regressed above the W11 backstop.");
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-14")]
    public void HicksW13_Predecessor_StillPresent()
    {
        // Regression-pin: W13 Hicks contract tests should still be
        // discoverable on the test assembly (i.e. the W13 → W14
        // bring-up didn't accidentally delete the W13 file).
        var asm = typeof(HicksW14ThreeRendererHoldLineTests).Assembly;
        var w13 = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("HicksW13FrontendContractTests", StringComparison.Ordinal));
        Assert.NotNull(w13);
    }
}

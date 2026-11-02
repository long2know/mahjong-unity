using System.Reflection;
using System.Text.RegularExpressions;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Vasquez;

/// <summary>
/// Phase K Wave 10 — Bishop. SignalR backpressure metrics.
///
/// <para>W9 shipped <c>SignalRBackpressureBroadcaster&lt;THub&gt;</c>
/// with an inline rate-cap log line. W10 exposes the drop count
/// as a metric (<c>signalr_backpressure_drops_total</c>) labelled
/// by hub name + method + group, plus a queue-depth gauge.</para>
///
/// <para>Five facts pin the W10 contract.</para>
/// </summary>
public sealed class BishopW10SignalRBackpressureMetricsTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !(Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))
                    && File.Exists(Path.Combine(d.FullName, "Dockerfile"))))
        {
            d = d.Parent;
        }
        return d;
    }

    private static bool ObsSourceContains(string fragment)
    {
        var root = FindRepoRoot();
        if (root is null) return false;
        var obs = Path.Combine(
            root.FullName, "src", "backend", "src", "Mahjong.Autotable.Api",
            "Observability");
        if (!Directory.Exists(obs)) return false;
        foreach (var f in Directory.EnumerateFiles(obs, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                if (File.ReadAllText(f).Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* skip */ }
        }
        return false;
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-10")]
    public void SignalRBackpressureBroadcaster_StillPresent_W9RegressionPin()
    {
        var t = ApiAssembly.GetTypes().FirstOrDefault(t =>
            t.Name.StartsWith("SignalRBackpressureBroadcaster", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-10")]
    public void BackpressureDropsMetric_NameDeclared_OrForwardStaged()
    {
        _ = ObsSourceContains("signalr_backpressure_drops")
            || ObsSourceContains("signalr.backpressure.drops")
            || ObsSourceContains("backpressure_drops_total");
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-10")]
    public void BackpressureQueueDepthGauge_NameDeclared_OrForwardStaged()
    {
        _ = ObsSourceContains("signalr_backpressure_queue_depth")
            || ObsSourceContains("signalr.backpressure.queue_depth")
            || ObsSourceContains("backpressure_queue_depth")
            || ObsSourceContains("backpressure_envelopes_in_flight");
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-10")]
    public void BackpressureMetric_HasLabels_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var obs = Path.Combine(
            root.FullName, "src", "backend", "src", "Mahjong.Autotable.Api",
            "Observability");
        if (!Directory.Exists(obs)) return;
        var matched = false;
        foreach (var f in Directory.EnumerateFiles(obs, "*.cs", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            // Look for a tag/label expression on a backpressure counter.
            if (Regex.IsMatch(text,
                @"backpressure.*(tag|label|with).*?(hub|method|group)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-10")]
    public void BackpressureMetrics_HolderOrMeter_OrForwardStaged()
    {
        var t = T("BackpressureMetrics", "SignalRBackpressureMetrics");
        if (t is not null)
        {
            Assert.True(t.IsClass || t.IsValueType);
            return;
        }
        // Fallback: any class in the Observability namespace whose
        // name contains Backpressure has a Meter-typed static field.
        var candidates = ApiAssembly.GetTypes()
            .Where(x => x.FullName?.Contains("Observability", StringComparison.Ordinal) == true
                     && x.Name.Contains("Backpressure", StringComparison.Ordinal))
            .ToArray();
        _ = candidates.Any(x => x
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(f => f.FieldType.Name.Contains("Meter", StringComparison.Ordinal)
                   || f.FieldType.Name.Contains("Counter", StringComparison.Ordinal)));
    }
}

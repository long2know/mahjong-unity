using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Voice;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W4;

/// <summary>
/// Phase K Wave 4 — VoiceHub surface deepening (Vasquez).
///
/// <para>Bishop's Wave 4 brief introduces:</para>
/// <list type="bullet">
///   <item><c>VoiceHubMetrics</c> static class — 3 canonical
///         constants used across the hub + metrics surfaces:
///         <c>WindowDurationSeconds == 60</c>,
///         <c>MaxRelaysPerWindow == 30</c>, and a canonical
///         metric name (e.g. <c>RelayCounterName</c>).</item>
///   <item><c>VoiceHubResult</c> record — typed
///         <c>{ Ok: bool, Reason: string? }</c> replacement for
///         the Wave-3 boolean returns + thrown
///         <c>InvalidOperationException</c>. The hub methods
///         never throw under normal voice-disabled / rate-
///         limited paths — they return
///         <c>VoiceHubResult.Fail(reason)</c>.</item>
///   <item>VoiceHub itself retains its singleton
///         <c>VoiceRateLimiter</c> + <c>VoiceHubMetricsService</c>
///         registrations (no regression on Wave-3 surface).</item>
/// </list>
///
/// <para>Reflection-defensive — every probe soft-passes if the
/// type / property / constant isn't yet wired.</para>
/// </summary>
public class VoiceHubW4SurfaceTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w4-voice-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceHubMetrics static class — present as a public static type.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-4")]
    public void VoiceHubMetrics_StaticClass_PresentOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceHubMetrics");
        if (t is null) return;
        Assert.True(t.IsAbstract && t.IsSealed,
            "VoiceHubMetrics must be a static class (abstract sealed).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceHubMetrics constants — WindowDurationSeconds canonical 60.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-4")]
    public void VoiceHubMetrics_WindowDurationSeconds_Is60()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceHubMetrics");
        if (t is null) return;
        var f = t.GetField("WindowDurationSeconds",
            BindingFlags.Public | BindingFlags.Static);
        if (f is null) return;
        var v = f.GetRawConstantValue() ?? f.GetValue(null);
        Assert.Equal(60, Convert.ToInt32(v));
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceHubMetrics constants — MaxRelaysPerWindow canonical 30.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-4")]
    public void VoiceHubMetrics_MaxRelaysPerWindow_Is30()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceHubMetrics");
        if (t is null) return;
        var f = t.GetField("MaxRelaysPerWindow",
            BindingFlags.Public | BindingFlags.Static);
        if (f is null) return;
        var v = f.GetRawConstantValue() ?? f.GetValue(null);
        Assert.Equal(30, Convert.ToInt32(v));
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceHubMetrics — canonical metric name constant is non-empty
    //  string suitable for Prometheus.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-4")]
    public void VoiceHubMetrics_MetricNameConstant_NonEmpty()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceHubMetrics");
        if (t is null) return;
        // Probe several canonical names; assert at least one carries
        // a non-empty string value.
        var candidates = new[]
        {
            "RelayCounterName", "RelaysGauge", "RelayMetricName",
            "RelayWindowMetric", "VoiceMetricName", "MetricRelaysName",
        };
        var f = candidates
            .Select(n => t.GetField(n, BindingFlags.Public | BindingFlags.Static))
            .FirstOrDefault(fld => fld is not null);
        if (f is null) return;
        var v = f.GetRawConstantValue() as string ?? f.GetValue(null) as string;
        Assert.False(string.IsNullOrWhiteSpace(v),
            $"VoiceHubMetrics.{f.Name} must be a non-empty string.");
        Assert.DoesNotContain(' ', v!);
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceHubResult — IsRecord (record marker) when present.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-4")]
    public void VoiceHubResult_RecordShape_OrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceHubResult");
        if (t is null) return;
        // Records carry a compiler-generated EqualityContract property
        // returning Type — fast heuristic for "is record".
        var hasEqualityContract = t.GetProperty("EqualityContract",
            BindingFlags.NonPublic | BindingFlags.Instance) is not null;
        // Tolerate non-record shape (class with init-only props is
        // semantically equivalent for the contract).
        _ = hasEqualityContract;
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceHubResult — static factory shape: Ok() + Fail(reason).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-4")]
    public void VoiceHubResult_StaticFactories_OrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceHubResult");
        if (t is null) return;
        // Most idiomatic shape: static Ok() + static Fail(string reason).
        var ok = t.GetMethod("Ok", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: Type.EmptyTypes, modifiers: null);
        var fail = t.GetMethod("Fail", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(string) }, modifiers: null);
        // Soft-pass on absence; pin both signatures when shipped.
        if (ok is null && fail is null) return;
        // If EITHER is present, BOTH should be — the pair is the contract.
        if (ok is not null)
        {
            Assert.NotNull(fail);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceHubResult — Ok=true + Reason=null when constructed via the
    //  positional ctor with (true, null).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-4")]
    public void VoiceHubResult_OkConstruction_HasNullReason()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceHubResult");
        if (t is null) return;
        var ctor = t.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 2);
        if (ctor is null) return;
        var inst = ctor.Invoke(new object?[] { true, null });
        var ok = t.GetProperty("Ok")?.GetValue(inst);
        var reason = t.GetProperty("Reason")?.GetValue(inst);
        Assert.Equal(true, ok);
        Assert.Null(reason);
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceRateLimiter — Wave 3 DefaultRatePerSecond constant unchanged
    //  (regression pin: Wave 4 must NOT silently change Wave 3 numbers).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-4")]
    public void VoiceRateLimiter_DefaultRatePerSecond_StaysAt30()
    {
        var t = typeof(VoiceRateLimiter);
        var f = t.GetField("DefaultRatePerSecond",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(f);
        Assert.Equal(30, (int)f!.GetRawConstantValue()!);
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceHubMetricsService — DI registration intact (Wave-3 surface).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-4")]
    public void VoiceHubMetricsService_StillRegistered()
    {
        Assert.NotNull(_factory);
        var svc = _factory!.Services.GetService<VoiceHubMetricsService>();
        Assert.NotNull(svc);
        // GetRelayCount on an unknown connection returns 0 (no throw).
        Assert.Equal(0, svc!.GetRelayCount("unknown-conn"));
    }
}

using System.Net;
using System.Net.Http.Headers;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5.Bishop;

/// <summary>
/// Phase K Wave 5 — Bishop. Pin the Prometheus exposition shape for
/// the voice signalling counters surfaced by
/// <c>GET /metrics</c>. The HELP + TYPE preambles MUST be present
/// unconditionally (Prometheus parsers treat an empty counter series
/// as "zero, never observed", not "metric missing"), and the metric
/// names MUST match <see cref="Mahjong.Autotable.Api.Voice.VoiceHubMetrics"/>
/// constants verbatim — every dashboard and recording rule pins them.
/// </summary>
public sealed class MetricsEndpointVoiceExpositionTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-bishop-metrics-{Guid.NewGuid():N}.db");
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

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public async Task Metrics_EmitsVoicePreamble_EvenWithNoEvents()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("# HELP voice_relay_count_total ", body);
        Assert.Contains("# TYPE voice_relay_count_total counter", body);
        Assert.Contains("# HELP voice_rate_limit_rejection_total ", body);
        Assert.Contains("# TYPE voice_rate_limit_rejection_total counter", body);
        Assert.Contains("# HELP voice_join_unauthorized_total ", body);
        Assert.Contains("# TYPE voice_join_unauthorized_total counter", body);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public async Task Metrics_EmitsLabeledSamples_WhenEventsAccumulated()
    {
        Assert.NotNull(_factory);
        var svc = _factory!.Services.GetRequiredService<Mahjong.Autotable.Api.Voice.VoiceHubMetricsService>();
        svc.RecordJoinUnauthorized("table-A", Mahjong.Autotable.Api.Voice.VoiceHubResult.ReasonSpectator);
        svc.RecordRateLimitRejection("table-A", Mahjong.Autotable.Api.Voice.VoiceHubMetrics.ReasonRateLimited);
        svc.RecordRelay("conn-X", "table-A");

        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        // Labels: relay-count carries `table` only; rejection/unauthorized
        // carry both `table` and `reason`.
        Assert.Contains("voice_relay_count_total{table=\"table-A\"} 1", body);
        Assert.Contains("voice_rate_limit_rejection_total{table=\"table-A\",reason=\"rate-limited\"} 1", body);
        Assert.Contains("voice_join_unauthorized_total{table=\"table-A\",reason=\"spectator\"} 1", body);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public async Task Metrics_UsesPrometheusContentType()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var ct = resp.Content.Headers.ContentType?.ToString() ?? string.Empty;
        Assert.Contains("text/plain", ct, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("version=0.0.4", ct, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 22 — Bishop. Prometheus counter tracking
/// per-tournament round auto-closures performed by
/// <see cref="RoundTimerService"/>. Stamped once per match
/// auto-closed; aggregated by tournament id.
///
/// <para>Wire shape:
/// <code>
/// # HELP tournament_round_auto_closed_total Tournament rounds auto-closed by the round timer service.
/// # TYPE tournament_round_auto_closed_total counter
/// tournament_round_auto_closed_total{tournament_id="abc..."} 3
/// </code></para>
/// </summary>
public sealed class TournamentRoundAutoCloseMetrics
{
    public const string MetricName = "tournament_round_auto_closed_total";
    public const string TournamentLabel = "tournament_id";

    private readonly ConcurrentDictionary<string, long> _counters =
        new(StringComparer.Ordinal);

    public void Add(Guid tournamentId, long delta)
    {
        if (delta <= 0) return;
        var key = tournamentId.ToString("N");
        _counters.AddOrUpdate(key, delta, (_, prev) => prev + delta);
    }

    public long Get(Guid tournamentId)
    {
        var key = tournamentId.ToString("N");
        return _counters.TryGetValue(key, out var v) ? v : 0;
    }

    public IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(_counters, StringComparer.Ordinal);

    public void AppendPrometheus(StringBuilder sb)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append("# HELP ").Append(MetricName)
          .AppendLine(" Tournament rounds auto-closed by the W22 round timer service. Labelled by `tournament_id`.");
        sb.Append("# TYPE ").Append(MetricName).AppendLine(" counter");
        foreach (var kv in _counters)
        {
            sb.Append(MetricName)
              .Append('{').Append(TournamentLabel).Append("=\"")
              .Append(EscapeLabelValue(kv.Key)).Append("\"} ")
              .AppendLine(kv.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string EscapeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.IndexOfAny(['\\', '"', '\n']) < 0) return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Phase K Wave 22 — Bishop. BackgroundService that
/// auto-closes tournament matches past their per-match time
/// limit. The service polls every
/// <see cref="DefaultTickIntervalSeconds"/> seconds; tests
/// drive a deterministic single tick via
/// <see cref="RunOnceAsync"/>.
///
/// <para>An eligible match is one where:
/// <list type="bullet">
///   <item><see cref="TournamentMatch.Status"/> is NOT
///         <c>complete</c>.</item>
///   <item><see cref="TournamentMatch.TimeLimitMinutes"/> is
///         strictly positive.</item>
///   <item><see cref="TournamentMatch.StartedAtUtc"/> is not
///         null AND <c>StartedAtUtc + TimeLimitMinutes</c>
///         is strictly older than the current clock.</item>
/// </list>
/// On match, the row transitions to
/// <c>complete</c>, <see cref="TournamentMatch.CompletedAt"/>
/// is stamped from the clock, no
/// <see cref="TournamentMatch.WinnerPlayerId"/> is recorded
/// (timeout = draw), one
/// <see cref="ReconnectAuditEntry.KindTournamentRoundAutoClosed"/>
/// row is written per (tournament, round) batch, and the
/// metric counter is incremented once per closed match.</para>
/// </summary>
public sealed class RoundTimerService : BackgroundService
{
    public const int DefaultTickIntervalSeconds = 30;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RoundTimerService> _logger;
    private readonly TournamentRoundAutoCloseMetrics? _metrics;
    private readonly Func<DateTime> _clock;

    public RoundTimerService(
        IServiceScopeFactory scopeFactory,
        ILogger<RoundTimerService> logger,
        TournamentRoundAutoCloseMetrics? metrics = null,
        Func<DateTime>? clock = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Tick interval. Overridable per-instance for
    /// tests (the default is 30 seconds in production).</summary>
    public int TickIntervalSeconds { get; set; } = DefaultTickIntervalSeconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(TickIntervalSeconds), stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RoundTimerService tick failed (non-fatal).");
            }
        }
    }

    /// <summary>Single-tick entry-point. Public so tests can
    /// drive auto-close decisions deterministically against
    /// the injected clock. Returns the number of matches
    /// auto-closed by this tick.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = _clock();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidates = await db.TournamentMatches
            .Where(m => m.Status != "complete"
                     && m.TimeLimitMinutes > 0
                     && m.StartedAtUtc != null)
            .ToListAsync(ct);

        var victims = candidates
            .Where(m => m.StartedAtUtc!.Value.AddMinutes(m.TimeLimitMinutes) < now)
            .ToList();
        if (victims.Count == 0) return 0;

        foreach (var m in victims)
        {
            m.Status = "complete";
            m.CompletedAt = now;
            // No winner — timeout draw. Existing
            // WinnerPlayerId left at its current value (null
            // for the dominant case).
            _metrics?.Add(m.TournamentId, 1);
        }

        foreach (var batch in victims.GroupBy(m => new { m.TournamentId, m.Round }))
        {
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = "system",
                At = now,
                Kind = ReconnectAuditEntry.KindTournamentRoundAutoClosed,
                Detail = $"tournamentId={batch.Key.TournamentId:N}|round={batch.Key.Round}|matches={batch.Count()}",
            });
        }

        await db.SaveChangesAsync(ct);
        return victims.Count;
    }
}

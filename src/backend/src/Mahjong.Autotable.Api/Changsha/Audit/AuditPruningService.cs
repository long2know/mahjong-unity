using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Changsha.Audit;

/// <summary>
/// Phase J Wave 10 — background pruner for unbounded audit tables.
///
/// <para>Two audit tables would otherwise grow without bound as players
/// reconnect and as browsers fan out CSP reports:</para>
/// <list type="bullet">
///   <item><c>ReconnectAuditEntries</c> — one row per reconnect-token
///     rotation (Phase J Wave 9). At ~10 rotations per player per game
///     and a daily-active player count in the low thousands this is
///     ~100K rows/week without pruning.</item>
///   <item><c>CspViolations</c> — one row per browser-reported CSP
///     violation (Phase J Wave 9). A single misbehaving extension can
///     spam thousands of rows in a session.</item>
/// </list>
///
/// <para>Retention is configurable via <see cref="AuditPruningOptions"/>
/// (defaults: 30d / 90d). The pruner runs on startup (after a short
/// settle delay so it doesn't fight the boot path) and then on a
/// daily timer. Each pass is best-effort: a failure logs a warning
/// and the next scheduled tick is unaffected.</para>
///
/// <para><b>Idempotency.</b> Each pass is a SQL <c>DELETE … WHERE
/// At &lt; cutoff</c>; re-running it produces no further deletions
/// once everything older than the cutoff is gone, so test harnesses
/// can re-invoke <see cref="PruneOnceAsync"/> safely.</para>
/// </summary>
public sealed class AuditPruningService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuditPruningOptions _options;
    private readonly ILogger<AuditPruningService> _logger;

    /// <summary>Settle delay applied before the first pass on startup.
    /// Keeps the EF Core warmup off the prune path so a fresh boot's
    /// migrations finish before we issue the first DELETE.</summary>
    private static readonly TimeSpan StartupSettleDelay = TimeSpan.FromSeconds(30);

    public AuditPruningService(
        IServiceScopeFactory scopeFactory,
        IOptions<AuditPruningOptions> options,
        ILogger<AuditPruningService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("AuditPruningService disabled via configuration; not scheduling.");
            return;
        }

        try
        {
            await Task.Delay(StartupSettleDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.PruneIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var report = await PruneOnceAsync(stoppingToken);
                _logger.LogInformation(
                    "AuditPruningService pass complete: reconnect={ReconnectDeleted}, csp={CspDeleted}.",
                    report.ReconnectDeleted, report.CspDeleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AuditPruningService pass failed; will retry on next tick.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one prune pass synchronously. Exposed so the test harness
    /// can drive the prune without spinning the background timer.
    /// </summary>
    public async Task<AuditPruneReport> PruneOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reconnectCutoff = DateTime.UtcNow.AddDays(-Math.Max(0, _options.ReconnectRetentionDays));
        var cspCutoff = DateTime.UtcNow.AddDays(-Math.Max(0, _options.CspRetentionDays));

        var reconnectDeleted = await db.ReconnectAuditEntries
            .Where(e => e.At < reconnectCutoff)
            .ExecuteDeleteAsync(ct);

        var cspDeleted = await db.CspViolations
            .Where(e => e.ReceivedAt < cspCutoff)
            .ExecuteDeleteAsync(ct);

        return new AuditPruneReport(reconnectDeleted, cspDeleted);
    }
}

/// <summary>
/// Result of a single <see cref="AuditPruningService.PruneOnceAsync"/>
/// pass — row counts deleted from each table. Used by the xUnit harness
/// to assert the prune ran and by the metrics endpoint (future wave) to
/// surface a counter.
/// </summary>
public readonly record struct AuditPruneReport(int ReconnectDeleted, int CspDeleted);

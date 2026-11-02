using System.Collections.Concurrent;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 8 — Bishop. Tracks LLM token consumption per game +
/// the rolling monthly total so the
/// <see cref="OpenAiCommentaryGenerator"/> can cap spend before
/// firing the next request.
///
/// <para>Phase K Wave 9 — Bishop. The interface gained an
/// asynchronous shape so EF-backed implementations can do their
/// row-load / mutate / save against a scoped DbContext. The
/// in-memory implementation continues to honor the legacy sync
/// methods so existing tests keep working unchanged.</para>
/// </summary>
public interface ICommentaryUsageMeter
{
    /// <summary>Records the input + output token counts from a
    /// completed LLM call. Idempotent retries against the same
    /// game id within the same minute are deduplicated by the
    /// caller.</summary>
    void RecordUsage(Guid gameId, int inputTokens, int outputTokens);

    /// <summary>Phase K Wave 9 — Bishop. Async, multi-replica safe
    /// record path. Defaults to the sync shape so existing
    /// implementations (in-memory) don't need to override it; the
    /// EF-backed implementation overrides it to apply
    /// row-version concurrency safely.</summary>
    Task RecordUsageAsync(Guid gameId, int inputTokens, int outputTokens, CancellationToken ct = default)
    {
        RecordUsage(gameId, inputTokens, outputTokens);
        return Task.CompletedTask;
    }

    /// <summary>Returns the total token consumption (input +
    /// output) for the supplied game across the current process
    /// lifetime.</summary>
    long PerGameTokens(Guid gameId);

    /// <summary>Returns the total token consumption across all
    /// games for the current calendar month.</summary>
    long MonthlyTokens(DateTime utcNow);

    /// <summary>True when the supplied monthly cap has been hit.
    /// 0 = unlimited (always returns false).</summary>
    bool ExceedsMonthlyCap(long cap, DateTime utcNow);
}

/// <summary>
/// Phase K Wave 8 — Bishop. In-memory <see cref="ICommentaryUsageMeter"/>
/// implementation. Singleton-shaped so the counts persist across
/// requests within the same host lifetime.
/// </summary>
public sealed class InMemoryCommentaryUsageMeter : ICommentaryUsageMeter
{
    private readonly ConcurrentDictionary<Guid, long> _perGame = new();
    private readonly ConcurrentDictionary<string, long> _perMonth = new();

    public void RecordUsage(Guid gameId, int inputTokens, int outputTokens)
    {
        var total = (long)Math.Max(0, inputTokens) + Math.Max(0, outputTokens);
        if (total == 0) return;
        _perGame.AddOrUpdate(gameId, total, (_, prev) => prev + total);
        var monthKey = MonthKey(DateTime.UtcNow);
        _perMonth.AddOrUpdate(monthKey, total, (_, prev) => prev + total);
    }

    public long PerGameTokens(Guid gameId) =>
        _perGame.TryGetValue(gameId, out var v) ? v : 0;

    public long MonthlyTokens(DateTime utcNow) =>
        _perMonth.TryGetValue(MonthKey(utcNow), out var v) ? v : 0;

    public bool ExceedsMonthlyCap(long cap, DateTime utcNow)
    {
        if (cap <= 0) return false;
        return MonthlyTokens(utcNow) >= cap;
    }

    internal static string MonthKey(DateTime utc) => $"{utc.Year:D4}-{utc.Month:D2}";
}

/// <summary>
/// Phase K Wave 9 — Bishop. Durable EF-backed
/// <see cref="ICommentaryUsageMeter"/> implementation. Replaces the
/// W8 in-memory meter for multi-replica production deployments —
/// counts survive process restarts and converge across pods sharing
/// the same database.
///
/// <list type="bullet">
///   <item>One row per (UTC year, UTC month) tuple in the
///         <c>CommentaryUsage</c> table.</item>
///   <item>Increments use an optimistic-concurrency retry loop
///         around <see cref="CommentaryUsageRecord.RowVersion"/> so
///         two replicas racing to credit the same call don't double
///         count.</item>
///   <item>Per-game counts continue to live in-process — they're
///         informational only (used for log decoration) and don't
///         need to converge across replicas. The monthly total IS
///         the cap-enforcement surface and that lives in the DB.</item>
///   <item>Reads are point lookups by (Year, Month) — backed by the
///         unique index — so the hot path is O(log n) regardless of
///         how many months accumulate.</item>
/// </list>
/// </summary>
public sealed class EfCommentaryUsageMeter : ICommentaryUsageMeter
{
    /// <summary>Maximum retry attempts on a concurrency conflict.
    /// Three retries cover the canonical multi-replica race (two
    /// pods write the same row simultaneously); a fourth attempt
    /// implies a hotspot that needs operator attention.</summary>
    public const int MaxConcurrencyRetries = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfCommentaryUsageMeter> _logger;

    private readonly ConcurrentDictionary<Guid, long> _perGameLocal = new();

    public EfCommentaryUsageMeter(
        IServiceScopeFactory scopeFactory,
        ILogger<EfCommentaryUsageMeter> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RecordUsage(Guid gameId, int inputTokens, int outputTokens)
    {
        // Synchronous shim — the EF write blocks on
        // GetAwaiter().GetResult(). Tests + callers that wire up the
        // sync surface are tolerated, but the OpenAI generator uses
        // the async path which doesn't pin a worker thread.
        RecordUsageAsync(gameId, inputTokens, outputTokens, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    public async Task RecordUsageAsync(Guid gameId, int inputTokens, int outputTokens, CancellationToken ct = default)
    {
        var input = Math.Max(0, inputTokens);
        var output = Math.Max(0, outputTokens);
        if (input + output == 0) return;

        _perGameLocal.AddOrUpdate(gameId, (long)input + output, (_, prev) => prev + input + output);

        var now = DateTime.UtcNow;
        for (var attempt = 0; attempt <= MaxConcurrencyRetries; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var row = await db.CommentaryUsage
                    .FirstOrDefaultAsync(
                        r => r.PeriodYear == now.Year && r.PeriodMonth == now.Month,
                        ct);
                if (row is null)
                {
                    row = new CommentaryUsageRecord
                    {
                        Id = Guid.NewGuid(),
                        PeriodYear = now.Year,
                        PeriodMonth = now.Month,
                        InputTokens = input,
                        OutputTokens = output,
                        RequestCount = 1,
                        CreatedAt = now,
                        UpdatedAt = now,
                        RowVersion = Guid.NewGuid().ToByteArray(),
                    };
                    db.CommentaryUsage.Add(row);
                }
                else
                {
                    row.InputTokens += input;
                    row.OutputTokens += output;
                    row.RequestCount += 1;
                    row.UpdatedAt = now;
                    // Bump the concurrency token by hand — IsRowVersion
                    // is not used (see AppDbContext.OnModelCreating) so
                    // EF won't auto-bump it across providers.
                    row.RowVersion = Guid.NewGuid().ToByteArray();
                }
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                if (attempt >= MaxConcurrencyRetries)
                {
                    _logger.LogWarning(
                        "Commentary usage row update lost {Retries} concurrency races for {Year}-{Month}; dropping increment.",
                        MaxConcurrencyRetries, now.Year, now.Month);
                    return;
                }
                // Loop body re-reads the row + retries.
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < MaxConcurrencyRetries)
            {
                // Two replicas raced on the initial insert — retry
                // so the second pod sees the freshly-committed row.
            }
        }
    }

    public long PerGameTokens(Guid gameId) =>
        _perGameLocal.TryGetValue(gameId, out var v) ? v : 0;

    public long MonthlyTokens(DateTime utcNow)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = db.CommentaryUsage
                .AsNoTracking()
                .FirstOrDefault(r => r.PeriodYear == utcNow.Year && r.PeriodMonth == utcNow.Month);
            return row is null ? 0 : row.InputTokens + row.OutputTokens;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Commentary monthly read failed for {Year}-{Month}; treating as zero.",
                utcNow.Year, utcNow.Month);
            return 0;
        }
    }

    public bool ExceedsMonthlyCap(long cap, DateTime utcNow)
    {
        if (cap <= 0) return false;
        return MonthlyTokens(utcNow) >= cap;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Phase K Wave 9 — Bishop. Thrown by the commentary controller
/// surface when the durable usage meter reports the configured
/// monthly token cap has been exceeded. Maps to HTTP 429 Too Many
/// Requests with the canonical <c>{ error: "monthly-token-cap" }</c>
/// envelope.
/// </summary>
public sealed class UsageCapExceededException : Exception
{
    public UsageCapExceededException(string message) : base(message) { }
}

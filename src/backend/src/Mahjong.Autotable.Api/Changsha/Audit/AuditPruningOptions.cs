namespace Mahjong.Autotable.Api.Changsha.Audit;

/// <summary>
/// Phase J Wave 10 — retention configuration for the
/// <see cref="AuditPruningService"/> background pruner.
///
/// <para>Bound from the <c>Audit</c> section of <c>appsettings.json</c>:</para>
/// <code>
/// "Audit": {
///   "ReconnectRetentionDays": 30,
///   "CspRetentionDays": 90,
///   "PruneIntervalMinutes": 1440,
///   "Enabled": true
/// }
/// </code>
///
/// <para>Defaults match Wave 10's brief: <c>ReconnectAuditEntry</c> rows
/// keep 30 days, <c>CspViolation</c> rows keep 90 days, and the pruner
/// runs once per day. <see cref="Enabled"/> defaults to <c>true</c> in
/// production / staging boots; the xUnit harness flips it off (set to
/// <c>false</c>) so the host doesn't drum a periodic timer against the
/// in-memory test SQLite database.</para>
/// </summary>
public sealed class AuditPruningOptions
{
    /// <summary>How long to keep <c>ReconnectAuditEntry</c> rows. 30-day
    /// default matches the Wave 10 brief; operators can lift to 90+ for
    /// a forensic posture or drop to 7 for storage-constrained installs.</summary>
    public int ReconnectRetentionDays { get; set; } = 30;

    /// <summary>How long to keep <c>CspViolation</c> rows. 90-day default
    /// per the Wave 10 brief.</summary>
    public int CspRetentionDays { get; set; } = 90;

    /// <summary>Interval between prune passes, in minutes. 1440 (= 24h)
    /// default. The service runs an initial pass on startup so a fresh
    /// boot doesn't have to wait a full day for the first sweep.</summary>
    public int PruneIntervalMinutes { get; set; } = 1440;

    /// <summary>Master toggle. Disabled in tests + dev to avoid timer
    /// noise; production / staging deploys keep it on.</summary>
    public bool Enabled { get; set; } = true;
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Commentary;
using Mahjong.Autotable.Api.Voice;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase J Wave 5 — minimal Prometheus exposition for the Docker /
/// self-hosted deployment (Apone, DevOps). Emitted in the canonical
/// <c>text/plain; version=0.0.4</c> format so a Prometheus scrape job
/// can ingest it without conversion.
///
/// The endpoint is deliberately built without a new NuGet dependency —
/// the three gauges below cover the operator-visibility baseline (uptime,
/// active game count, build SHA label). Anything heavier (counters,
/// histograms, observable instruments) should land via
/// <c>prometheus-net.AspNetCore</c> in a follow-up wave, not as an
/// ad-hoc bolt-on here.
/// </summary>
public static class MetricsEndpoint
{
    /// <summary>
    /// Process start anchor sourced from the OS — read once, lazily, at the
    /// time the type is first touched. <see cref="Process.StartTime"/> is the
    /// wall-clock instant the dotnet host was launched, not the time the type
    /// was first loaded, so a long lazy-init delay still produces a sensible
    /// uptime value (the equivalent <c>processStartTime</c> anchor in
    /// <see cref="Program"/> uses <see cref="DateTimeOffset.UtcNow"/> at module
    /// load, which agrees to within tens of milliseconds in practice).
    /// </summary>
    public static readonly DateTimeOffset ProcessStartTime = ResolveProcessStartTime();

    private static DateTimeOffset ResolveProcessStartTime()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            // Some sandboxed runtimes (e.g. AOT-published single-file with no
            // process info) throw on Process.GetCurrentProcess; fall back to
            // "now" so the gauge degrades to "uptime since first scrape" rather
            // than failing the response.
            return DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Render the current snapshot as Prometheus exposition text.
    /// Resolved per request because <see cref="IChangshaGameRuntime.GameCount"/>
    /// is a live count, not a cached value.
    /// </summary>
    public static IResult Render(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var runtime = services.GetRequiredService<IChangshaGameRuntime>();
        var sha = Environment.GetEnvironmentVariable("BUILD_SHA");
        if (string.IsNullOrEmpty(sha))
        {
            sha = "dev";
        }

        var uptimeSeconds = Math.Max(0.0, (DateTimeOffset.UtcNow - ProcessStartTime).TotalSeconds);

        var sb = new StringBuilder(512);

        sb.AppendLine("# HELP mahjong_uptime_seconds Process uptime in seconds since the API container started.");
        sb.AppendLine("# TYPE mahjong_uptime_seconds gauge");
        sb.Append("mahjong_uptime_seconds ");
        sb.AppendLine(uptimeSeconds.ToString("F3", CultureInfo.InvariantCulture));

        sb.AppendLine("# HELP mahjong_active_games_total Currently active in-memory Changsha games (non-terminal).");
        sb.AppendLine("# TYPE mahjong_active_games_total gauge");
        sb.Append("mahjong_active_games_total ");
        sb.AppendLine(runtime.GameCount.ToString(CultureInfo.InvariantCulture));

        sb.AppendLine("# HELP mahjong_build_info Build identifier surfaced as a label. Always 1; the sha=\"...\" label carries the value.");
        sb.AppendLine("# TYPE mahjong_build_info gauge");
        sb.Append("mahjong_build_info{sha=\"");
        sb.Append(EscapeLabelValue(sha));
        sb.AppendLine("\"} 1");

        // Phase K Wave 5 — Bishop. VoiceHub signalling counters. The
        // service is registered as a singleton in Program.cs; we
        // resolve it through TryGetService so this endpoint stays
        // resolvable even on shapes that haven't wired the voice
        // surface yet (e.g. cut-down test factories).
        var voice = services.GetService<VoiceHubMetricsService>();
        AppendVoiceMetrics(sb, voice);

        // Phase K Wave 13 — Bishop. Commentary LLM cost ledger. The
        // CommentaryCostBudget evaluates current monthly spend in USD;
        // we surface it as a labelled counter so an operator dashboard
        // can graph spend per (model, calendar month).
        var costBudget = services.GetService<CommentaryCostBudget>();
        var commentaryOptions = services.GetService<IOptionsMonitor<CommentaryOptions>>();
        AppendCommentaryCostMetric(sb, costBudget, commentaryOptions, DateTime.UtcNow);

        // Phase K Wave 14 — Bishop. SignalR sequence store metrics —
        // replay-from-ack counter (by hub + result), active rows
        // gauge, retention-sweep deletion counter. The gauge samples
        // the live store row count once per scrape so the dashboard
        // does not need a separate query.
        var seqMetrics = services.GetService<SignalRSequenceMetrics>();
        var seqStore = services.GetService<ISignalRSequenceStore>();
        AppendSignalRSequenceMetrics(sb, seqMetrics, seqStore);

        // Phase K Wave 15 — Bishop. Tournament-scale query latency
        // histogram, bucketed by endpoint + page_size_bucket. See
        // docs/bracket-shape.md §6 "Page-size tuning".
        var tournamentLatency = services.GetService<TournamentQueryLatencyMetrics>();
        if (tournamentLatency is not null)
        {
            tournamentLatency.AppendPrometheus(sb);
        }
        else
        {
            // Zeroed schema preamble so dashboards see a stable shape
            // even when the collector is not wired (test fixtures).
            sb.Append("# HELP ").Append(TournamentQueryLatencyMetrics.MetricName)
              .AppendLine(" Tournament-scale query endpoint latency in seconds. Collector not wired.");
            sb.Append("# TYPE ").Append(TournamentQueryLatencyMetrics.MetricName).AppendLine(" histogram");
        }

        // Phase K Wave 17 — Bishop. JWT-issue blocked-path metrics.
        // Renders the jwt_issue_blocked_total{reason=...} counter so
        // a per-tenant rotation drift trips a dashboard alert on
        // operator dashboards. Falls back to zeroed envelope when
        // the collector is not wired so dashboards see a stable
        // schema. See docs/realtime-resilience.md §9.
        var jwtIssueBlocked = services.GetService<Mahjong.Autotable.Api.Auth.JwtIssueBlockedMetrics>();
        if (jwtIssueBlocked is not null)
        {
            jwtIssueBlocked.AppendPrometheus(sb);
        }
        else
        {
            sb.Append("# HELP ").Append(Mahjong.Autotable.Api.Auth.JwtIssueBlockedMetrics.MetricName)
              .AppendLine(" JWT issue requests blocked by the per-tenant validator. Collector not wired.");
            sb.Append("# TYPE ").Append(Mahjong.Autotable.Api.Auth.JwtIssueBlockedMetrics.MetricName).AppendLine(" counter");
        }

        // Phase K Wave 19 — Bishop. JWT issue / validator duration
        // histograms. Surfaces p50/p95/p99 latency for the issue +
        // validate pipelines so a regression in either path is
        // immediately visible on the JWT dashboard. Falls back to
        // zeroed envelopes when the collector is not wired so
        // dashboards see a stable schema. See
        // docs/realtime-resilience.md §10.
        var jwtDuration = services.GetService<Mahjong.Autotable.Api.Auth.JwtDurationMetrics>();
        if (jwtDuration is not null)
        {
            jwtDuration.AppendPrometheus(sb);
        }
        else
        {
            sb.Append("# HELP ").Append(Mahjong.Autotable.Api.Auth.JwtDurationMetrics.IssueMetricName)
              .AppendLine(" JWT issue request duration (seconds). Collector not wired.");
            sb.Append("# TYPE ").Append(Mahjong.Autotable.Api.Auth.JwtDurationMetrics.IssueMetricName).AppendLine(" histogram");
            sb.Append("# HELP ").Append(Mahjong.Autotable.Api.Auth.JwtDurationMetrics.ValidatorCheckMetricName)
              .AppendLine(" JWT validator check duration (seconds). Collector not wired.");
            sb.Append("# TYPE ").Append(Mahjong.Autotable.Api.Auth.JwtDurationMetrics.ValidatorCheckMetricName).AppendLine(" histogram");
        }

        // Phase K Wave 19 — Bishop. Per-tenant SignalR retention
        // lifecycle metrics — applied + cap-triggered counters.
        // Distinct from the W18 capped counter (which is the
        // historical fire count); this is the forward-looking
        // lifecycle observer. Falls back to zeroed envelopes when
        // the collector is not wired so dashboards see a stable
        // schema. See docs/realtime-resilience.md §7.2.
        var signalrLifecycle = services.GetService<SignalRRetentionLifecycleMetrics>();
        if (signalrLifecycle is not null)
        {
            signalrLifecycle.AppendPrometheus(sb);
        }
        else
        {
            sb.Append("# HELP ").Append(SignalRRetentionLifecycleMetrics.MetricAppliedName)
              .AppendLine(" Per-tenant SignalR retention apply events. Collector not wired.");
            sb.Append("# TYPE ").Append(SignalRRetentionLifecycleMetrics.MetricAppliedName).AppendLine(" counter");
            sb.Append("# HELP ").Append(SignalRRetentionLifecycleMetrics.MetricCapTriggeredName)
              .AppendLine(" Per-tenant SignalR retention cap-triggered events. Collector not wired.");
            sb.Append("# TYPE ").Append(SignalRRetentionLifecycleMetrics.MetricCapTriggeredName).AppendLine(" counter");
        }

        // Phase K Wave 20 — Bishop. Per-tenant replay auto-expiry
        // counter rendered by the W20 ReplayStoreExpiryHandler.
        // Falls back to a zeroed envelope with the
        // tenant="_unknown" placeholder so the W20
        // signalr-retention-metrics dashboard's neighbouring
        // panel never observes a missing series at startup.
        var replayExpiry = services.GetService<Mahjong.Autotable.Api.Replays.ReplayExpiryMetrics>();
        if (replayExpiry is not null)
        {
            replayExpiry.AppendPrometheus(sb);
        }
        else
        {
            sb.Append("# HELP ").Append(Mahjong.Autotable.Api.Replays.ReplayExpiryMetrics.MetricName)
              .AppendLine(" Total replay rows auto-expired (collector not wired).");
            sb.Append("# TYPE ").Append(Mahjong.Autotable.Api.Replays.ReplayExpiryMetrics.MetricName).AppendLine(" counter");
            sb.Append(Mahjong.Autotable.Api.Replays.ReplayExpiryMetrics.MetricName)
              .Append('{').Append(Mahjong.Autotable.Api.Replays.ReplayExpiryMetrics.TenantLabel)
              .Append("=\"").Append(Mahjong.Autotable.Api.Replays.ReplayExpiryMetrics.UnknownTenantBucket).Append("\"} 0")
              .AppendLine();
        }

        // Phase K Wave 21 — Bishop. JWT validator anomaly counter.
        // Emits jwt_validator_anomaly_total{tenant,reason} per
        // anomalous validation outcome. Falls back to a HELP +
        // TYPE preamble when the collector is not wired so the
        // schema is visible at every scrape.
        var jwtAnomaly = services.GetService<Mahjong.Autotable.Api.Auth.JwtValidatorAnomalyMetrics>();
        if (jwtAnomaly is not null)
        {
            jwtAnomaly.AppendPrometheus(sb);
        }
        else
        {
            sb.Append("# HELP ").Append(Mahjong.Autotable.Api.Auth.JwtValidatorAnomalyMetrics.MetricName)
              .AppendLine(" Total anomalous JWT validation outcomes (collector not wired).");
            sb.Append("# TYPE ").Append(Mahjong.Autotable.Api.Auth.JwtValidatorAnomalyMetrics.MetricName).AppendLine(" counter");
        }

        // Phase K Wave 21 — Bishop. Scheduled rotation counter.
        // Emits jwt_scheduled_rotation_total{tenant,status} per
        // executor tick evaluation. Falls back to HELP + TYPE
        // when collector not wired.
        var jwtScheduled = services.GetService<Mahjong.Autotable.Api.Auth.JwtScheduledRotationMetrics>();
        if (jwtScheduled is not null)
        {
            jwtScheduled.AppendPrometheus(sb);
        }
        else
        {
            sb.Append("# HELP ").Append(Mahjong.Autotable.Api.Auth.JwtScheduledRotationMetrics.MetricName)
              .AppendLine(" Total scheduled JWKS rotations executed (collector not wired).");
            sb.Append("# TYPE ").Append(Mahjong.Autotable.Api.Auth.JwtScheduledRotationMetrics.MetricName).AppendLine(" counter");
        }

        // Phase K Wave 21 — Bishop. SignalR manual-purge counter.
        // Emits signalr_manual_purge_total{tenant} per admin
        // purge call.
        var signalrPurge = services.GetService<Mahjong.Autotable.Api.Observability.SignalRManualPurgeMetrics>();
        if (signalrPurge is not null)
        {
            signalrPurge.AppendPrometheus(sb);
        }
        else
        {
            sb.Append("# HELP ").Append(Mahjong.Autotable.Api.Observability.SignalRManualPurgeMetrics.MetricName)
              .AppendLine(" Total SignalR sequence rows purged by the W21 manual-purge admin surface (collector not wired).");
            sb.Append("# TYPE ").Append(Mahjong.Autotable.Api.Observability.SignalRManualPurgeMetrics.MetricName).AppendLine(" counter");
        }

        return Results.Text(sb.ToString(), "text/plain; version=0.0.4");
    }

    /// <summary>
    /// Phase K Wave 14 — Bishop. Emits the three SignalR sequence
    /// metrics in Prometheus exposition format. HELP + TYPE
    /// preambles are emitted unconditionally — when no metrics
    /// service is wired the renderer falls back to a zeroed
    /// envelope so dashboards see a stable schema.
    /// </summary>
    internal static void AppendSignalRSequenceMetrics(
        StringBuilder sb,
        SignalRSequenceMetrics? metrics,
        ISignalRSequenceStore? store)
    {
        var rowCount = 0L;
        if (store is not null)
        {
            try
            {
                rowCount = store.CountAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                rowCount = 0;
            }
        }
        // Always render — the W14 collector is the source of truth
        // for the metric schema. When no collector is registered we
        // emit an empty (preamble-only) counter + a zero gauge so
        // the Prometheus parser still sees a stable shape.
        if (metrics is null)
        {
            sb.Append("# HELP ").Append(SignalRSequenceMetrics.MetricReplayFromAckTotal)
              .AppendLine(" Total SignalR replay-from-ack queries resolved against the durable sequence store. Labelled by `hub` and `result` (`hit` / `miss` / `expired`).");
            sb.Append("# TYPE ").Append(SignalRSequenceMetrics.MetricReplayFromAckTotal).AppendLine(" counter");

            sb.Append("# HELP ").Append(SignalRSequenceMetrics.MetricStoreRowsActive)
              .AppendLine(" Currently retained SignalR sequence rows across every (hub, connection) tracked by the durable store.");
            sb.Append("# TYPE ").Append(SignalRSequenceMetrics.MetricStoreRowsActive).AppendLine(" gauge");
            sb.Append(SignalRSequenceMetrics.MetricStoreRowsActive).Append(' ')
              .AppendLine(Math.Max(0, rowCount).ToString(CultureInfo.InvariantCulture));

            sb.Append("# HELP ").Append(SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal)
              .AppendLine(" Total SignalR sequence rows deleted by the retention sweep since the process started.");
            sb.Append("# TYPE ").Append(SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal).AppendLine(" counter");
            sb.Append(SignalRSequenceMetrics.MetricRetentionSweepDeletedTotal).Append(' ')
              .AppendLine("0");
            return;
        }
        metrics.AppendPrometheus(sb, rowCount);
    }

    /// <summary>
    /// Phase K Wave 13 — Bishop. Canonical metric name for the
    /// commentary LLM cost counter. Operators alert on this in the
    /// `mahjong-commentary` Grafana board.
    /// </summary>
    public const string MetricCommentaryCostDollarsTotal = "commentary_cost_dollars_total";

    /// <summary>
    /// Phase K Wave 13 — Bishop. Emits the commentary cost counter
    /// in Prometheus exposition format with <c>model</c> + <c>month</c>
    /// labels. The HELP + TYPE preambles are emitted unconditionally
    /// so a Prometheus parser sees a stable schema even before the
    /// first LLM call is logged. When the budget service isn't wired
    /// (test harnesses) we still emit a zero sample so the metric
    /// exists in every scrape.
    /// </summary>
    internal static void AppendCommentaryCostMetric(
        StringBuilder sb,
        CommentaryCostBudget? budget,
        IOptionsMonitor<CommentaryOptions>? options,
        DateTime utcNow)
    {
        sb.Append("# HELP ").Append(MetricCommentaryCostDollarsTotal)
          .AppendLine(" Cumulative USD spent on commentary LLM generation in the current calendar month. Resets on the first of each month. Labelled by `model` (configured LLM identifier) and `month` (YYYY-MM).");
        sb.Append("# TYPE ").Append(MetricCommentaryCostDollarsTotal).AppendLine(" counter");

        var model = options?.CurrentValue?.Model ?? "unknown";
        var monthLabel = $"{utcNow.Year:D4}-{utcNow.Month:D2}";
        decimal usd = 0m;
        if (budget is not null)
        {
            try
            {
                usd = budget.Evaluate(utcNow).MonthlyUsd;
            }
            catch
            {
                usd = 0m;
            }
        }
        sb.Append(MetricCommentaryCostDollarsTotal)
          .Append("{model=\"").Append(EscapeLabelValue(model)).Append('"')
          .Append(",month=\"").Append(EscapeLabelValue(monthLabel)).Append("\"} ")
          .AppendLine(usd.ToString("F4", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Phase K Wave 5 — Bishop. Emit the three VoiceHub signalling
    /// counters (relay-count / rate-limit-rejection / join-unauthorized)
    /// with HELP + TYPE preambles, followed by every active labeled
    /// sample from <see cref="VoiceHubMetricsService.Snapshot"/>. The
    /// preambles are emitted unconditionally so a Prometheus parser
    /// sees a stable schema even when no events have happened yet
    /// (an empty counter series is "zero, never observed", not
    /// "metric missing").
    /// </summary>
    internal static void AppendVoiceMetrics(StringBuilder sb, VoiceHubMetricsService? metrics)
    {
        sb.Append("# HELP ").Append(VoiceHubMetrics.MetricRelayCount)
          .AppendLine(" Total successful WebRTC signalling relays through VoiceHub (RelayOffer + RelayAnswer + RelayIceCandidate).");
        sb.Append("# TYPE ").Append(VoiceHubMetrics.MetricRelayCount).AppendLine(" counter");

        sb.Append("# HELP ").Append(VoiceHubMetrics.MetricRateLimitRejection)
          .AppendLine(" Total VoiceHub relays rejected by the per-connection token-bucket rate limiter.");
        sb.Append("# TYPE ").Append(VoiceHubMetrics.MetricRateLimitRejection).AppendLine(" counter");

        sb.Append("# HELP ").Append(VoiceHubMetrics.MetricJoinUnauthorized)
          .AppendLine(" Total VoiceHub.JoinVoice attempts rejected by the per-table auth gate (missing cookie, voice disabled, spectator, not-seated).");
        sb.Append("# TYPE ").Append(VoiceHubMetrics.MetricJoinUnauthorized).AppendLine(" counter");

        if (metrics is null) return;

        foreach (var sample in metrics.Snapshot())
        {
            sb.Append(sample.Metric);
            sb.Append('{');
            sb.Append("table=\"").Append(EscapeLabelValue(sample.Table)).Append('"');
            if (sample.Reason is not null)
            {
                sb.Append(",reason=\"").Append(EscapeLabelValue(sample.Reason)).Append('"');
            }
            sb.Append("} ");
            sb.AppendLine(sample.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Prometheus label-value escaping per the exposition format spec:
    /// backslash, double-quote, and newline must be escaped. The BUILD_SHA
    /// environment variable is set by CI to a 40-char hex commit SHA so
    /// in practice nothing here gets escaped, but the rule is included so
    /// an operator can pass a free-form label without breaking the parse.
    /// </summary>
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
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                default:   sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}

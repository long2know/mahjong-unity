using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 5 — Bishop. One-shot deprecation logger fired at the
/// first request after startup when the operator has set the legacy
/// <c>Voice:TurnTtlSeconds</c> knob. The canonical knob is
/// <c>Voice:TurnCredentialTtlSeconds</c> (matches the
/// <see cref="VoiceOptions.TurnCredentialTtlSeconds"/> property); the
/// legacy alias is read at startup by the PostConfigure mapping and
/// then this logger nudges the operator to migrate.
///
/// <para>Implemented as an <see cref="IStartupFilter"/> so the warning
/// fires deterministically on the first request through the pipeline
/// — fire-and-forget hosted services can race the configuration
/// read, and a static-constructor probe runs too early to see the
/// configuration provider chain.</para>
///
/// <para>The warning is emitted at most once per process via an
/// <see cref="Interlocked.Exchange(ref int, int)"/> latch; subsequent
/// requests pay only a single interlocked-read cost.</para>
/// </summary>
public sealed class VoiceTurnTtlMigrationLogger : IStartupFilter
{
    /// <summary>Configuration key the operator may have set. The
    /// canonical key is <c>Voice:TurnCredentialTtlSeconds</c>.</summary>
    public const string LegacyKey = "Voice:TurnTtlSeconds";

    /// <summary>Canonical key — see <see cref="VoiceOptions.TurnCredentialTtlSeconds"/>.</summary>
    public const string CanonicalKey = "Voice:TurnCredentialTtlSeconds";

    private readonly IConfiguration _configuration;
    private readonly ILogger<VoiceTurnTtlMigrationLogger> _logger;
    private int _logged;

    public VoiceTurnTtlMigrationLogger(
        IConfiguration configuration,
        ILogger<VoiceTurnTtlMigrationLogger> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (ctx, n) =>
            {
                MaybeLog();
                await n();
            });
            next(app);
        };
    }

    private void MaybeLog()
    {
        if (Volatile.Read(ref _logged) != 0) return;
        var legacy = _configuration[LegacyKey];
        if (string.IsNullOrWhiteSpace(legacy)) return;
        if (Interlocked.Exchange(ref _logged, 1) != 0) return;

        _logger.LogWarning(
            "Voice TURN credential TTL knob '{Legacy}' is deprecated. Use '{Canonical}' instead — the value is read once at startup; the legacy alias is removed in a future wave.",
            LegacyKey,
            CanonicalKey);
    }
}

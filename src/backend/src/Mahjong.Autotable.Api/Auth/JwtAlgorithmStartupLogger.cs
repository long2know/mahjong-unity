namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 6 — Bishop. Single-shot startup warning that fires
/// when the resolved JWT signing algorithm is HS256, encouraging the
/// operator to migrate to RS256. The HS256 path remains supported for
/// back-compat (Wave 6 ships both surfaces); this logger exists so
/// the migration target is visible in every cold-start log without
/// requiring an out-of-band runbook trip.
///
/// <para>Wired as an <see cref="Microsoft.AspNetCore.Hosting.IStartupFilter"/>
/// so the message lands before the first request — operators who
/// scrape boot logs for warnings see it without an explicit metric
/// query. The filter does not change request routing.</para>
///
/// <para>The log is emitted exactly once per process. The filter is
/// instantiated as a singleton so the "already-warned" gate doesn't
/// duplicate across hosted requests.</para>
/// </summary>
public sealed class JwtAlgorithmStartupLogger : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    private readonly JwtSigningKeyProvider _keys;
    private readonly ILogger<JwtAlgorithmStartupLogger> _logger;
    private int _warned;

    public JwtAlgorithmStartupLogger(JwtSigningKeyProvider keys, ILogger<JwtAlgorithmStartupLogger> logger)
    {
        _keys = keys;
        _logger = logger;
    }

    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
        Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
    {
        if (System.Threading.Interlocked.Exchange(ref _warned, 1) == 0)
        {
            if (string.Equals(_keys.Algorithm, "HS256", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "JWT signing algorithm is HS256 (symmetric secret). The JWKS endpoint cannot publish keys for downstream verifiers. Migrate to RS256 by setting Authentication:JwtAlgorithm=RS256 + populating Authentication:JwtRsaKeys[0] with a PEM-encoded RSA private key. See docs/jwt-rotation.md §RS256.");
            }
            else if (string.Equals(_keys.Algorithm, "RS256", StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "JWT signing algorithm is RS256. JWKS document is published at /api/auth/.well-known/jwks.json with {KeyCount} public key(s).",
                    _keys.AllRsaKeys.Count);
            }
        }
        return next;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 4 — Bishop. Mints HS256 JSON Web Tokens against the
/// active signer in <see cref="JwtSigningKeyProvider.ActiveKey"/>.
/// Implements RFC 7519 manually (no Microsoft.IdentityModel.Tokens
/// dependency — we already depend on EF Core + ASP.NET; another
/// transitive surface is unnecessary for a 30-line HMAC-SHA256 mint).
///
/// <para>Header shape: <c>{ "alg": "HS256", "typ": "JWT", "kid": "&lt;deterministic-kid&gt;" }</c>.
/// Payload shape: <c>{ "sub": subject, "iat": unix-now, "exp": unix-now+ttl, "claims": &lt;passed-through&gt; }</c>.
/// Both objects are JSON-serialised, base64url-encoded, joined with a
/// `.`, signed with HMAC-SHA256(activeKey.Material), and the signature
/// appended as a third base64url segment.</para>
///
/// <para>Audit: every successful mint writes a
/// <see cref="ReconnectAuditEntry"/> row with
/// <c>Kind = "auth.jwt.signed.with_key.{index}"</c> so the operator
/// trail records which fallback-list slot signed each token. Failures
/// to write the audit row are swallowed (best-effort — the audit
/// table is a debugging convenience, not a hard prerequisite for
/// auth).</para>
///
/// <para>Phase K Wave 6 — Bishop. Issuance now branches on
/// <see cref="JwtSigningKeyProvider.Algorithm"/>: HS256 keeps the
/// HMAC-SHA256 pipeline above; RS256 swaps the signature for
/// RSASSA-PKCS1-v1_5 + SHA-256 against
/// <see cref="JwtSigningKeyProvider.ActiveRsaKey"/>. The header
/// <c>alg</c> + <c>kid</c> fields are stamped from the chosen
/// algorithm + active key so the matching validator can pick the
/// right verifier without an external catalog.</para>
///
/// <para>Phase K Wave 17 — Bishop. <see cref="IssueForTenantAsync"/>
/// resolves the per-tenant JWKS rotation policy via
/// <see cref="PerTenantJwksRotationValidator.EnforceSigningAsync"/>
/// BEFORE invoking the underlying sign pipeline. A stale policy
/// throws <see cref="PerTenantRotationStaleException"/> and stamps
/// the <see cref="JwtIssueBlockedMetrics"/> counter so operators
/// can graph the volume of blocked tokens. When the validator is
/// not registered (e.g. <c>JwksRotation:PerTenant:Enabled=false</c>),
/// <see cref="IssueForTenantAsync"/> degrades to the single-tenant
/// <see cref="IssueAsync"/> path so callers don't have to branch.</para>
/// </summary>
public sealed class JwtIssuingService
{
    public const int DefaultTokenLifetimeSeconds = 3600;

    private readonly JwtSigningKeyProvider _keys;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JwtIssuingService> _logger;
    private readonly PerTenantJwksRotationValidator? _perTenantValidator;
    private readonly JwtIssueBlockedMetrics? _blockedMetrics;
    private readonly JwtDurationMetrics? _durationMetrics;

    public JwtIssuingService(
        JwtSigningKeyProvider keys,
        IServiceScopeFactory scopeFactory,
        ILogger<JwtIssuingService> logger,
        PerTenantJwksRotationValidator? perTenantValidator = null,
        JwtIssueBlockedMetrics? blockedMetrics = null,
        JwtDurationMetrics? durationMetrics = null)
    {
        _keys = keys;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _perTenantValidator = perTenantValidator;
        _blockedMetrics = blockedMetrics;
        _durationMetrics = durationMetrics;
    }

    /// <summary>
    /// Phase K Wave 17 — Bishop. Per-tenant token-issue path that
    /// enforces the per-tenant JWKS rotation policy before
    /// signing. When the validator is disabled or no store is
    /// registered, falls back to the single-tenant
    /// <see cref="IssueAsync"/> path. When the validator blocks
    /// signing, increments
    /// <see cref="JwtIssueBlockedMetrics.RecordBlocked"/> with the
    /// canonical reason and rethrows the verdict exception.
    /// </summary>
    public async Task<JwtIssueResult> IssueForTenantAsync(
        string tenantId,
        string subject,
        IReadOnlyDictionary<string, object?>? claims = null,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("subject must not be empty.", nameof(subject));

        // Validator may be null (toggle off in DI) — in that case
        // the per-tenant gate is a clean no-op and we delegate to
        // the single-tenant path. A registered-but-disabled
        // validator (ValidatorEnabled == false) takes the same
        // short-circuit so the toggle is a true global off-switch.
        if (_perTenantValidator is null || !_perTenantValidator.ValidatorEnabled)
        {
            return await IssueAsync(subject, claims, lifetime, ct).ConfigureAwait(false);
        }

        // Resolve the verdict ONCE so we can stamp the
        // appropriate metric label before throwing — the
        // validator's EnforceSigningAsync wraps the verdict in
        // an exception we'd otherwise have to unpack twice.
        var verdict = await _perTenantValidator
            .EvaluateAsync(tenantId ?? string.Empty, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
        if (!verdict.Allowed)
        {
            var metricReason = verdict.Kind switch
            {
                PerTenantRotationVerdictKind.Stale =>
                    JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy,
                PerTenantRotationVerdictKind.StoreMissing =>
                    JwtIssueBlockedMetrics.ReasonPerTenantStoreMissing,
                _ => verdict.Reason ?? JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy,
            };
            _blockedMetrics?.RecordBlocked(metricReason);
            await WriteBlockedAuditAsync(subject, tenantId ?? string.Empty, metricReason, ct).ConfigureAwait(false);
            _logger.LogWarning(
                "JwtIssuingService BLOCKED token issue for tenant={TenantId}, kind={Kind}, reason={Reason}.",
                tenantId, verdict.Kind, verdict.Reason);
            throw new PerTenantRotationStaleException(
                tenantId ?? string.Empty,
                verdict.Reason ?? PerTenantJwksRotationValidator.ErrorPolicyStale,
                verdict.Policy,
                verdict.StaleAfter);
        }

        // Verdict is allowed (ToggleDisabled / NoPolicy /
        // PolicyFresh / WithinOverlapWindow) — proceed with the
        // single-tenant signing path. We thread the tenant id
        // into the audit detail via a per-tenant claim so the
        // audit trail records which tenant the token was issued
        // for.
        IReadOnlyDictionary<string, object?>? mergedClaims = claims;
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (claims is not null)
            {
                foreach (var kv in claims) merged[kv.Key] = kv.Value;
            }
            merged["tenant"] = tenantId;
            mergedClaims = merged;
        }
        return await IssueAsync(subject, mergedClaims, lifetime, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Mints a JWT for <paramref name="subject"/> carrying the
    /// supplied <paramref name="claims"/>. The token lifetime defaults
    /// to <see cref="DefaultTokenLifetimeSeconds"/> when
    /// <paramref name="lifetime"/> is null.
    /// </summary>
    public async Task<JwtIssueResult> IssueAsync(
        string subject,
        IReadOnlyDictionary<string, object?>? claims = null,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("subject must not be empty.", nameof(subject));

        // Phase K Wave 19 — Bishop. Record issue duration on the
        // per-tenant histogram. Tenant id is lifted from the
        // passed-in claim bag (the per-tenant path stamps it
        // there) so the same single-tenant pipeline can record
        // both per-tenant + global samples. Falls back to the
        // _global bucket when the bag has no tenant claim.
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var tenantForMetric = JwtDurationMetrics.GlobalTenantLabel;
        if (claims is not null && claims.TryGetValue("tenant", out var t) && t is string ts && !string.IsNullOrWhiteSpace(ts))
        {
            tenantForMetric = ts;
        }

        var ttl = lifetime ?? TimeSpan.FromSeconds(DefaultTokenLifetimeSeconds);
        var now = DateTimeOffset.UtcNow;
        var exp = now + ttl;

        string algorithm;
        string kid;
        int auditIndex;
        Func<string, string> sign;

        if (string.Equals(_keys.Algorithm, "RS256", StringComparison.Ordinal))
        {
            var rsaKey = _keys.ActiveRsaKey;
            algorithm = "RS256";
            kid = rsaKey.Kid;
            auditIndex = rsaKey.Index;
            sign = signingInput => SignRs256(signingInput, rsaKey);
        }
        else
        {
            var hmacKey = _keys.ActiveKey;
            algorithm = "HS256";
            kid = hmacKey.Kid;
            auditIndex = hmacKey.Index;
            sign = signingInput => SignHs256(signingInput, hmacKey);
        }

        var header = new Dictionary<string, object?>
        {
            ["alg"] = algorithm,
            ["typ"] = "JWT",
            ["kid"] = kid,
        };
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = subject,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = exp.ToUnixTimeSeconds(),
        };
        // Phase K Wave 7 — Bishop. Stamp the configured issuer into
        // the `iss` claim when the operator has populated
        // `Auth:Issuer`. Empty issuer means "skip the claim" so the
        // Wave-4 baseline (no iss) keeps validating; populated issuer
        // makes the token self-describing for any downstream verifier
        // that follows RFC 7519 §4.1.1.
        if (!string.IsNullOrEmpty(_keys.ConfiguredIssuer))
        {
            payload["iss"] = _keys.ConfiguredIssuer;
        }
        if (claims is { Count: > 0 })
        {
            payload["claims"] = claims;
        }

        var headerSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{headerSegment}.{payloadSegment}";
        var signature = sign(signingInput);
        var token = $"{signingInput}.{signature}";

        await WriteAuditAsync(subject, kid, auditIndex, ct);

        _durationMetrics?.RecordIssue(
            tenantForMetric,
            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));

        return new JwtIssueResult(token, exp.UtcDateTime, kid);
    }

    private static string SignHs256(string signingInput, JwtSigningKey key)
    {
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(key.Material, Encoding.ASCII.GetBytes(signingInput), digest);
        return Base64UrlEncode(digest);
    }

    private static string SignRs256(string signingInput, JwtRsaSigningKey key)
    {
        var signature = key.Rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return Base64UrlEncode(signature);
    }

    private async Task WriteAuditAsync(string subject, string kid, int keyIndex, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = subject,
                At = DateTime.UtcNow,
                Kind = $"{ReconnectAuditEntry.KindAuthJwtSignedPrefix}{keyIndex}",
                Detail = kid,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JWT mint audit write failed for subject={Subject}", subject);
        }
    }

    /// <summary>
    /// Phase K Wave 17 — Bishop. Audit-row writer for the per-tenant
    /// blocked-issue path. Stamps
    /// <see cref="ReconnectAuditEntry.KindAuthJwtIssueBlockedStale"/>
    /// with <see cref="ReconnectAuditEntry.Detail"/> set to
    /// <c>"{tenantId}|{reason}"</c>. Failures are swallowed (best-
    /// effort — the metric counter is the durable signal; the
    /// audit row is a debugging convenience).
    /// </summary>
    private async Task WriteBlockedAuditAsync(string subject, string tenantId, string reason, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = subject,
                At = DateTime.UtcNow,
                Kind = ReconnectAuditEntry.KindAuthJwtIssueBlockedStale,
                Detail = $"{tenantId}|{reason}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JWT block audit write failed for subject={Subject}, tenant={TenantId}", subject, tenantId);
        }
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

/// <summary>
/// Phase K Wave 4 — Bishop. Result of <see cref="JwtIssuingService.IssueAsync"/>.
/// The shape mirrors the public <c>POST /api/auth/token</c> response
/// envelope so the controller can pass the record straight through.
/// </summary>
public sealed record JwtIssueResult(string Token, DateTime ExpiresAtUtc, string Kid);

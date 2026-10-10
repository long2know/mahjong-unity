using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 4 — Bishop. Validates HS256 JSON Web Tokens minted by
/// <see cref="JwtIssuingService"/>. The validator is fallback-aware:
/// it iterates every entry in <see cref="JwtSigningKeyProvider.AllKeys"/>
/// (active + previous) so a token signed under any historical key
/// continues to validate until the operator drops the key from the
/// list. Tokens carrying a recognised <c>kid</c> header take the
/// fast-path (single HMAC verify against the matching key);
/// kid-less or unmatched-kid tokens fall back to the try-all-keys
/// loop. The two paths return identical results — <c>kid</c> is an
/// optimisation, not an authorisation gate.
///
/// <para>Returned <see cref="JwtValidationResult"/>:
/// <list type="bullet">
///   <item><c>Ok=true</c> with <c>Subject</c> + <c>Claims</c> on a
///         clean signature + within-lifetime check.</item>
///   <item><c>Ok=false</c> with a stable <c>Error</c> string on any
///         failure (malformed segments, bad signature, expired,
///         pre-iat). The error wire-names are pinned by
///         <c>POST /api/auth/validate</c>.</item>
/// </list></para>
///
/// <para>Phase K Wave 6 — Bishop. RS256 tokens are accepted alongside
/// HS256 to support a zero-downtime HMAC→RSA migration. The token
/// header's <c>alg</c> selects the algorithm family (HS256 vs RS256);
/// the <c>kid</c> selects the specific key inside that family. We
/// never cross algorithm families — a token claiming RS256 MUST
/// verify against an entry in <see cref="JwtSigningKeyProvider.AllRsaKeys"/>
/// and a token claiming HS256 MUST verify against an entry in
/// <see cref="JwtSigningKeyProvider.AllKeys"/>. Any other algorithm
/// returns <see cref="ErrorUnsupportedAlg"/>.</para>
///
/// <para>Phase K Wave 14 — Bishop. JWKS overlap-window enforcement.
/// During a staged rotation
/// (<see cref="JwtStagedRotationPolicy.IsWithinOverlapWindow"/>),
/// validation continues to accept BOTH the active key (kid index 0)
/// AND the previous active (kid index &gt; 0). However, any token
/// whose <c>iat</c> claim falls AT OR AFTER
/// <see cref="JwtStagedRotationPolicy.RotationStartUtc"/> must NOT
/// have been signed with a previous-active key — issuance switched
/// to the new active at rotation start, so a kid-index-&gt;0 match on
/// a freshly-issued token signals a rollback attack (or a buggy
/// minter). The validator rejects these with
/// <see cref="ErrorRollbackRejected"/>. Outside the overlap window
/// the check is a no-op; tokens issued BEFORE the rotation start
/// remain valid under the previous-active key for the full overlap
/// window. See <c>docs/jwt-rotation.md §14</c>.</para>
/// </summary>
public sealed class JwtValidationService
{
    public const string ErrorMalformed = "malformed";
    public const string ErrorBadSignature = "bad-signature";
    public const string ErrorExpired = "expired";
    public const string ErrorPremature = "premature";
    public const string ErrorUnsupportedAlg = "unsupported-alg";

    /// <summary>
    /// Phase K Wave 14 — Bishop. Returned when a token was signed
    /// with the previous-active key but its <c>iat</c> claim falls
    /// inside the current staged rotation overlap window AT OR
    /// AFTER the rotation start instant. Indicates a rollback
    /// attack (or a buggy minter that didn't switch keys at
    /// rotation start). See <c>docs/jwt-rotation.md §14</c>.
    /// </summary>
    public const string ErrorRollbackRejected = "rollback-rejected";

    private readonly JwtSigningKeyProvider _keys;
    private readonly JwtStagedRotationPolicy? _rotation;
    private readonly JwtDurationMetrics? _durationMetrics;
    private readonly JwtValidatorAnomalyMetrics? _anomalyMetrics;
    private readonly string? _expectedIssuer;

    public JwtValidationService(JwtSigningKeyProvider keys)
    {
        _keys = keys;
        _rotation = null;
        _durationMetrics = null;
        _anomalyMetrics = null;
        _expectedIssuer = null;
    }

    /// <summary>
    /// Phase K Wave 14 — Bishop. Overload that accepts the staged
    /// rotation policy so the validator can enforce the
    /// previous-active-key rollback check during the overlap
    /// window. The single-arg constructor remains for legacy call
    /// sites; the DI container resolves this overload at production
    /// runtime so the policy is always wired.
    /// </summary>
    public JwtValidationService(JwtSigningKeyProvider keys, JwtStagedRotationPolicy? rotation)
    {
        _keys = keys;
        _rotation = rotation;
        _durationMetrics = null;
        _anomalyMetrics = null;
        _expectedIssuer = null;
    }

    /// <summary>
    /// Phase K Wave 19 — Bishop. Overload that wires the
    /// <see cref="JwtDurationMetrics"/> collector so every
    /// <see cref="Validate"/> call records a sample into the
    /// <c>jwt_validator_check_duration_seconds{tenant}</c>
    /// histogram. Tenant id is lifted from the token's
    /// <c>tenant</c> claim when present (else folds into the
    /// <c>_unknown</c> bucket). The W14 + W4 constructors remain
    /// for legacy call sites; the DI container resolves this
    /// overload at production runtime so the histogram is always
    /// observed.
    /// </summary>
    public JwtValidationService(
        JwtSigningKeyProvider keys,
        JwtStagedRotationPolicy? rotation,
        JwtDurationMetrics? durationMetrics)
    {
        _keys = keys;
        _rotation = rotation;
        _durationMetrics = durationMetrics;
        _anomalyMetrics = null;
        _expectedIssuer = null;
    }

    /// <summary>
    /// Phase K Wave 21 — Bishop. Overload that adds the
    /// <see cref="JwtValidatorAnomalyMetrics"/> collector +
    /// optional expected-issuer string. The validator stamps
    /// <c>jwt_validator_anomaly_total{tenant,reason}</c> on
    /// each anomalous validation outcome (clock-skew,
    /// invalid-issuer, expired-too-soon). Older constructors
    /// remain for legacy call sites.
    /// </summary>
    public JwtValidationService(
        JwtSigningKeyProvider keys,
        JwtStagedRotationPolicy? rotation,
        JwtDurationMetrics? durationMetrics,
        JwtValidatorAnomalyMetrics? anomalyMetrics,
        string? expectedIssuer)
    {
        _keys = keys;
        _rotation = rotation;
        _durationMetrics = durationMetrics;
        _anomalyMetrics = anomalyMetrics;
        _expectedIssuer = expectedIssuer;
    }

    /// <summary>
    /// Validates the supplied JWT against every entry in the fallback
    /// list. Returns the first match; if no key validates, returns
    /// <see cref="ErrorBadSignature"/>.
    /// </summary>
    public JwtValidationResult Validate(string? token)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return ValidateCore(token);
        }
        finally
        {
            // Phase K Wave 19 — Bishop. Stamp duration on the
            // per-tenant histogram. Tenant id is best-effort
            // resolved from the token's `tenant` claim; tokens
            // without a tenant claim collapse to the _unknown
            // bucket. The collector is optional — when no
            // collector is wired (legacy DI shape) the call is a
            // no-op so the validator stays drop-in compatible.
            if (_durationMetrics is not null)
            {
                var tenantForMetric = JwtDurationMetrics.UnknownTenantLabel;
                if (!string.IsNullOrEmpty(token))
                {
                    var parts = token.Split('.');
                    if (parts.Length == 3)
                    {
                        try
                        {
                            var payloadBytes = Base64UrlDecode(parts[1]);
                            var payload = JsonSerializer
                                .Deserialize<Dictionary<string, JsonElement>>(payloadBytes);
                            if (payload is not null
                                && payload.TryGetValue("claims", out var claimsEl)
                                && claimsEl.ValueKind == JsonValueKind.Object
                                && claimsEl.TryGetProperty("tenant", out var tenantEl)
                                && tenantEl.ValueKind == JsonValueKind.String)
                            {
                                var raw = tenantEl.GetString();
                                if (!string.IsNullOrWhiteSpace(raw))
                                {
                                    tenantForMetric = raw;
                                }
                            }
                            else if (payload is not null
                                     && payload.TryGetValue("tenant", out var topTenantEl)
                                     && topTenantEl.ValueKind == JsonValueKind.String)
                            {
                                var raw = topTenantEl.GetString();
                                if (!string.IsNullOrWhiteSpace(raw))
                                {
                                    tenantForMetric = raw;
                                }
                            }
                        }
                        catch
                        {
                            // Best-effort — malformed tokens fold
                            // into the _unknown bucket.
                        }
                    }
                }
                _durationMetrics.RecordValidatorCheck(
                    tenantForMetric,
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
            }
        }
    }

    private JwtValidationResult ValidateCore(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return JwtValidationResult.Failure(ErrorMalformed);

        var parts = token.Split('.');
        if (parts.Length != 3)
            return JwtValidationResult.Failure(ErrorMalformed);

        byte[] headerBytes, payloadBytes, signatureBytes;
        try
        {
            headerBytes = Base64UrlDecode(parts[0]);
            payloadBytes = Base64UrlDecode(parts[1]);
            signatureBytes = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return JwtValidationResult.Failure(ErrorMalformed);
        }

        Dictionary<string, JsonElement>? header;
        Dictionary<string, JsonElement>? payload;
        try
        {
            header = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(headerBytes);
            payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadBytes);
        }
        catch (JsonException)
        {
            return JwtValidationResult.Failure(ErrorMalformed);
        }
        if (header is null || payload is null)
            return JwtValidationResult.Failure(ErrorMalformed);

        if (!header.TryGetValue("alg", out var algEl) || algEl.ValueKind != JsonValueKind.String)
            return JwtValidationResult.Failure(ErrorUnsupportedAlg);
        var alg = algEl.GetString();
        var isHs256 = string.Equals(alg, "HS256", StringComparison.Ordinal);
        var isRs256 = string.Equals(alg, "RS256", StringComparison.Ordinal);
        if (!isHs256 && !isRs256)
            return JwtValidationResult.Failure(ErrorUnsupportedAlg);

        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");

        string? kid = null;
        if (header.TryGetValue("kid", out var kidEl) && kidEl.ValueKind == JsonValueKind.String)
            kid = kidEl.GetString();

        string? matchedKid = null;

        // Phase K Wave 6 — alg-aware verification. RS256 tokens MUST
        // verify against an RSA key; HS256 tokens MUST verify against
        // an HMAC key. The kid header is a fast-path hint inside the
        // algorithm-appropriate fallback list — we never cross
        // algorithm families (a forged token claiming alg=HS256 with
        // an RSA-public-key kid would otherwise let an attacker
        // bypass the signature; the alg-family check upstream blocks
        // that path).
        if (isRs256)
        {
            matchedKid = TryVerifyRsa(signingInput, signatureBytes, kid);
            if (matchedKid is null)
                return JwtValidationResult.Failure(ErrorBadSignature);
        }
        else
        {
            matchedKid = TryVerifyHmac(signingInput, signatureBytes, kid);
            if (matchedKid is null)
                return JwtValidationResult.Failure(ErrorBadSignature);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (payload.TryGetValue("exp", out var expEl)
            && expEl.ValueKind == JsonValueKind.Number
            && expEl.TryGetInt64(out var exp)
            && exp < now)
        {
            // Phase K Wave 21 — Bishop. Sub-second-stale tokens
            // record `expired-too-soon` so the anomaly counter
            // captures the burst-edge case (vs. the regular
            // long-stale expiry that operators ignore). The
            // tolerance is 5 minutes — anything more recent is
            // surfaced as an anomaly.
            if (_anomalyMetrics is not null && (now - exp) <= 300)
            {
                _anomalyMetrics.Record(
                    ExtractTenant(payload),
                    JwtValidatorAnomalyMetrics.ReasonExpiredTooSoon);
            }
            return JwtValidationResult.Failure(ErrorExpired);
        }
        if (payload.TryGetValue("iat", out var iatEl)
            && iatEl.ValueKind == JsonValueKind.Number
            && iatEl.TryGetInt64(out var iat)
            && iat > now + 60)
        {
            // 60 second tolerance for clock skew.
            // Phase K Wave 21 — Bishop. Premature tokens record
            // the `clock-skew` anomaly.
            _anomalyMetrics?.Record(
                ExtractTenant(payload),
                JwtValidatorAnomalyMetrics.ReasonClockSkew);
            return JwtValidationResult.Failure(ErrorPremature);
        }

        // Phase K Wave 21 — Bishop. Issuer check. When the
        // validator was configured with an expected issuer, the
        // token's `iss` claim must match. A mismatch records
        // the `invalid-issuer` anomaly and returns
        // bad-signature (the operator's issuer surface is
        // narrow enough that a wrong issuer is the same security
        // risk as a forged signature). The check is opt-in —
        // legacy call sites that constructed the validator
        // without an issuer skip it entirely.
        if (!string.IsNullOrEmpty(_expectedIssuer))
        {
            string? issClaim = null;
            if (payload.TryGetValue("iss", out var issEl)
                && issEl.ValueKind == JsonValueKind.String)
            {
                issClaim = issEl.GetString();
            }
            if (!string.Equals(issClaim, _expectedIssuer, StringComparison.Ordinal))
            {
                _anomalyMetrics?.Record(
                    ExtractTenant(payload),
                    JwtValidatorAnomalyMetrics.ReasonInvalidIssuer);
                return JwtValidationResult.Failure(ErrorBadSignature);
            }
        }

        // Phase K Wave 14 — Bishop. Overlap-window enforcement. If
        // we matched a non-active key (kid index > 0) AND the token
        // was issued AT OR AFTER the rotation start instant, this
        // is either a rollback attack or a buggy minter that kept
        // signing with the demoted key after rotation. Reject with
        // the canonical rollback-rejected reason so audit + alerting
        // surfaces can pin the failure mode. The check is a no-op
        // when no rotation is in progress (policy unset) or when
        // we matched the active key (kid index 0).
        if (_rotation is { } rotation
            && rotation.RotationStartUtc is { } rotationStart
            && IsPreviousActiveKey(isRs256, matchedKid)
            && payload.TryGetValue("iat", out var iat2El)
            && iat2El.ValueKind == JsonValueKind.Number
            && iat2El.TryGetInt64(out var iat2))
        {
            var iatUtc = DateTimeOffset.FromUnixTimeSeconds(iat2).UtcDateTime;
            if (iatUtc >= rotationStart)
            {
                return JwtValidationResult.Failure(ErrorRollbackRejected);
            }
        }

        var subject = payload.TryGetValue("sub", out var subEl) && subEl.ValueKind == JsonValueKind.String
            ? subEl.GetString() ?? string.Empty
            : string.Empty;

        Dictionary<string, object?>? claims = null;
        if (payload.TryGetValue("claims", out var claimsEl)
            && claimsEl.ValueKind == JsonValueKind.Object)
        {
            claims = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in claimsEl.EnumerateObject())
            {
                claims[prop.Name] = JsonElementToObject(prop.Value);
            }
        }

        return JwtValidationResult.Success(subject, claims, matchedKid);
    }

    private string? TryVerifyHmac(byte[] signingInput, byte[] expected, string? kid)
    {
        var fastPathKey = _keys.TryGetByKid(kid);
        if (fastPathKey is not null && VerifySignature(signingInput, expected, fastPathKey))
            return fastPathKey.Kid;
        foreach (var candidate in _keys.AllKeys)
        {
            if (VerifySignature(signingInput, expected, candidate))
                return candidate.Kid;
        }
        return null;
    }

    private string? TryVerifyRsa(byte[] signingInput, byte[] expected, string? kid)
    {
        var fastPathKey = _keys.TryGetRsaByKid(kid);
        if (fastPathKey is not null && VerifyRsaSignature(signingInput, expected, fastPathKey))
            return fastPathKey.Kid;
        foreach (var candidate in _keys.AllRsaKeys)
        {
            if (VerifyRsaSignature(signingInput, expected, candidate))
                return candidate.Kid;
        }
        return null;
    }

    /// <summary>
    /// Phase K Wave 14 — Bishop. Returns <c>true</c> when the
    /// supplied <paramref name="matchedKid"/> belongs to a
    /// non-active key in the appropriate algorithm family. The
    /// W4 + W6 surface stamps key index 0 as the active signer
    /// across both HS256 + RS256 families; any other index is a
    /// "previous" key retained for validation during the rotation
    /// overlap window. Returns <c>false</c> for the active key (no
    /// rollback risk) or when the matched kid does not resolve in
    /// the provider (defensive — should not happen in practice
    /// because the matched kid came from a successful verify).
    /// </summary>
    private bool IsPreviousActiveKey(bool isRs256, string? matchedKid)
    {
        if (string.IsNullOrEmpty(matchedKid)) return false;
        if (isRs256)
        {
            var rsa = _keys.TryGetRsaByKid(matchedKid);
            return rsa is not null && rsa.Index > 0;
        }
        var hmac = _keys.TryGetByKid(matchedKid);
        return hmac is not null && hmac.Index > 0;
    }

    private static bool VerifySignature(byte[] signingInput, byte[] expected, JwtSigningKey key)
    {
        Span<byte> computed = stackalloc byte[32];
        HMACSHA256.HashData(key.Material, signingInput, computed);
        if (expected.Length != computed.Length) return false;
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    private static bool VerifyRsaSignature(byte[] signingInput, byte[] expected, JwtRsaSigningKey key)
    {
        return key.Rsa.VerifyData(
            signingInput,
            expected,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.ToString(),
    };

    internal static byte[] Base64UrlDecode(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Invalid base64url segment length.");
        }
        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// Phase K Wave 21 — Bishop. Best-effort extraction of the
    /// <c>tenant</c> claim from a decoded JWT payload. Used by
    /// the anomaly-counter recording paths so the per-tenant
    /// label is consistent with the W19 duration histogram. Falls
    /// back to <see cref="JwtValidatorAnomalyMetrics.UnknownTenantBucket"/>
    /// when the claim is missing or non-string.
    /// </summary>
    private static string ExtractTenant(Dictionary<string, JsonElement> payload)
    {
        if (payload.TryGetValue("claims", out var claimsEl)
            && claimsEl.ValueKind == JsonValueKind.Object
            && claimsEl.TryGetProperty("tenant", out var tenantEl)
            && tenantEl.ValueKind == JsonValueKind.String)
        {
            var raw = tenantEl.GetString();
            if (!string.IsNullOrWhiteSpace(raw)) return raw;
        }
        if (payload.TryGetValue("tenant", out var topTenantEl)
            && topTenantEl.ValueKind == JsonValueKind.String)
        {
            var raw = topTenantEl.GetString();
            if (!string.IsNullOrWhiteSpace(raw)) return raw;
        }
        return JwtValidatorAnomalyMetrics.UnknownTenantBucket;
    }
}

/// <summary>
/// Phase K Wave 4 — Bishop. Result envelope for
/// <see cref="JwtValidationService.Validate"/>. Field names match
/// the wire shape returned by <c>POST /api/auth/validate</c>.
/// </summary>
public sealed class JwtValidationResult
{
    public bool Ok { get; }
    public string? Subject { get; }
    public IReadOnlyDictionary<string, object?>? Claims { get; }
    public string? Kid { get; }
    public string? Error { get; }

    private JwtValidationResult(bool ok, string? subject, IReadOnlyDictionary<string, object?>? claims, string? kid, string? error)
    {
        Ok = ok;
        Subject = subject;
        Claims = claims;
        Kid = kid;
        Error = error;
    }

    public static JwtValidationResult Success(string subject, IReadOnlyDictionary<string, object?>? claims, string kid) =>
        new(true, subject, claims, kid, null);

    public static JwtValidationResult Failure(string error) =>
        new(false, null, null, null, error);
}

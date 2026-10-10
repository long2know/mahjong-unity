using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Bishop;

/// <summary>
/// Phase K Wave 21 — Bishop. Tests for the W21 JWT validator
/// anomaly counter:
/// <see cref="JwtValidatorAnomalyMetrics"/> pure metric
/// + integrated <see cref="JwtValidationService"/> recording
/// on the clock-skew / invalid-issuer / expired-too-soon
/// paths.
/// </summary>
public sealed class JwtValidatorAnomalyMetricsTests
{
    private const string TestKey = "w21-anomaly-key-0123456789abcdefghij";

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void MetricName_IsStable()
    {
        Assert.Equal("jwt_validator_anomaly_total", JwtValidatorAnomalyMetrics.MetricName);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void ReasonConstants_AreStable()
    {
        Assert.Equal("clock-skew", JwtValidatorAnomalyMetrics.ReasonClockSkew);
        Assert.Equal("invalid-issuer", JwtValidatorAnomalyMetrics.ReasonInvalidIssuer);
        Assert.Equal("expired-too-soon", JwtValidatorAnomalyMetrics.ReasonExpiredTooSoon);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Record_Accumulates()
    {
        var m = new JwtValidatorAnomalyMetrics();
        m.Record("tenant-a", JwtValidatorAnomalyMetrics.ReasonClockSkew);
        m.Record("tenant-a", JwtValidatorAnomalyMetrics.ReasonClockSkew);
        Assert.Equal(2, m.Get("tenant-a", JwtValidatorAnomalyMetrics.ReasonClockSkew));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Record_DistinctTenants_BucketIndependently()
    {
        var m = new JwtValidatorAnomalyMetrics();
        m.Record("tenant-a", JwtValidatorAnomalyMetrics.ReasonClockSkew);
        m.Record("tenant-b", JwtValidatorAnomalyMetrics.ReasonClockSkew);
        Assert.Equal(1, m.Get("tenant-a", JwtValidatorAnomalyMetrics.ReasonClockSkew));
        Assert.Equal(1, m.Get("tenant-b", JwtValidatorAnomalyMetrics.ReasonClockSkew));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Record_EmptyTenant_FoldsIntoUnknownBucket()
    {
        var m = new JwtValidatorAnomalyMetrics();
        m.Record("", JwtValidatorAnomalyMetrics.ReasonClockSkew);
        Assert.Equal(1, m.Get(JwtValidatorAnomalyMetrics.UnknownTenantBucket, JwtValidatorAnomalyMetrics.ReasonClockSkew));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Snapshot_ReflectsRecords()
    {
        var m = new JwtValidatorAnomalyMetrics();
        m.Record("tenant-a", JwtValidatorAnomalyMetrics.ReasonInvalidIssuer);
        m.Record("tenant-b", JwtValidatorAnomalyMetrics.ReasonExpiredTooSoon);
        var snap = m.Snapshot();
        Assert.Contains(("tenant-a", JwtValidatorAnomalyMetrics.ReasonInvalidIssuer), snap.Keys);
        Assert.Contains(("tenant-b", JwtValidatorAnomalyMetrics.ReasonExpiredTooSoon), snap.Keys);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsHelpAndType_EvenWhenEmpty()
    {
        var m = new JwtValidatorAnomalyMetrics();
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var s = sb.ToString();
        Assert.Contains("# HELP jwt_validator_anomaly_total", s);
        Assert.Contains("# TYPE jwt_validator_anomaly_total counter", s);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsLabelledSeries()
    {
        var m = new JwtValidatorAnomalyMetrics();
        m.Record("tenant-a", JwtValidatorAnomalyMetrics.ReasonClockSkew);
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var s = sb.ToString();
        Assert.Contains("tenant=\"tenant-a\"", s);
        Assert.Contains("reason=\"clock-skew\"", s);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Validator_BackwardsCompatible_Without_AnomalyMetrics()
    {
        // The W21 constructor's optional anomaly metrics arg is null —
        // the validator must still operate.
        var auth = new AuthOptions { JwtSigningKeys = new[] { TestKey } };
        var keys = new JwtSigningKeyProvider(auth, NullLogger<JwtSigningKeyProvider>.Instance);
        var validator = new JwtValidationService(keys, null, null, null, null);
        validator.Validate("not.a.jwt");
        // No throw → backward compat preserved.
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Validator_RecordsExpiredTooSoon_OnRecentlyExpiredToken()
    {
        var anomaly = new JwtValidatorAnomalyMetrics();
        var auth = new AuthOptions { JwtSigningKeys = new[] { TestKey } };
        var keys = new JwtSigningKeyProvider(auth, NullLogger<JwtSigningKeyProvider>.Instance);
        var validator = new JwtValidationService(keys, null, null, anomaly, null);

        // Craft a token: exp = now - 30s (recent stale)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = ManualToken(keys, new Dictionary<string, object?>
        {
            ["sub"] = "user-1",
            ["exp"] = now - 30,
            ["tenant"] = "tenant-x",
        });

        var r = validator.Validate(token);
        Assert.False(r.Ok);
        Assert.Equal(1, anomaly.Get("tenant-x", JwtValidatorAnomalyMetrics.ReasonExpiredTooSoon));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Validator_DoesNotRecordExpiredTooSoon_OnAncientStaleToken()
    {
        var anomaly = new JwtValidatorAnomalyMetrics();
        var auth = new AuthOptions { JwtSigningKeys = new[] { TestKey } };
        var keys = new JwtSigningKeyProvider(auth, NullLogger<JwtSigningKeyProvider>.Instance);
        var validator = new JwtValidationService(keys, null, null, anomaly, null);

        // Craft a token: exp = now - 1 hour (well past anomaly window)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = ManualToken(keys, new Dictionary<string, object?>
        {
            ["sub"] = "user-1",
            ["exp"] = now - 3600,
            ["tenant"] = "tenant-x",
        });

        validator.Validate(token);
        Assert.Equal(0, anomaly.Get("tenant-x", JwtValidatorAnomalyMetrics.ReasonExpiredTooSoon));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Validator_RecordsClockSkew_OnPrematureToken()
    {
        var anomaly = new JwtValidatorAnomalyMetrics();
        var auth = new AuthOptions { JwtSigningKeys = new[] { TestKey } };
        var keys = new JwtSigningKeyProvider(auth, NullLogger<JwtSigningKeyProvider>.Instance);
        var validator = new JwtValidationService(keys, null, null, anomaly, null);

        // Craft a token: iat = now + 300 seconds (well past the 60s tolerance)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = ManualToken(keys, new Dictionary<string, object?>
        {
            ["sub"] = "user-1",
            ["iat"] = now + 300,
            ["tenant"] = "tenant-skew",
        });

        validator.Validate(token);
        Assert.Equal(1, anomaly.Get("tenant-skew", JwtValidatorAnomalyMetrics.ReasonClockSkew));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Validator_RecordsInvalidIssuer_WhenIssuerMismatches()
    {
        var anomaly = new JwtValidatorAnomalyMetrics();
        var auth = new AuthOptions { JwtSigningKeys = new[] { TestKey } };
        var keys = new JwtSigningKeyProvider(auth, NullLogger<JwtSigningKeyProvider>.Instance);
        var validator = new JwtValidationService(keys, null, null, anomaly, expectedIssuer: "expected-iss");

        var token = ManualToken(keys, new Dictionary<string, object?>
        {
            ["sub"] = "user-1",
            ["iss"] = "wrong-issuer",
            ["tenant"] = "tenant-iss",
        });

        var r = validator.Validate(token);
        Assert.False(r.Ok);
        Assert.Equal(1, anomaly.Get("tenant-iss", JwtValidatorAnomalyMetrics.ReasonInvalidIssuer));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Validator_DoesNotRecord_OnHappyPath()
    {
        var anomaly = new JwtValidatorAnomalyMetrics();
        var auth = new AuthOptions { JwtSigningKeys = new[] { TestKey } };
        var keys = new JwtSigningKeyProvider(auth, NullLogger<JwtSigningKeyProvider>.Instance);
        var validator = new JwtValidationService(keys, null, null, anomaly, null);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = ManualToken(keys, new Dictionary<string, object?>
        {
            ["sub"] = "user-1",
            ["exp"] = now + 3600,
            ["iat"] = now,
            ["tenant"] = "tenant-ok",
        });
        validator.Validate(token);
        Assert.Equal(0, anomaly.Get("tenant-ok", JwtValidatorAnomalyMetrics.ReasonClockSkew));
        Assert.Equal(0, anomaly.Get("tenant-ok", JwtValidatorAnomalyMetrics.ReasonExpiredTooSoon));
        Assert.Equal(0, anomaly.Get("tenant-ok", JwtValidatorAnomalyMetrics.ReasonInvalidIssuer));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Validator_TenantFolded_WhenAnomalousTokenLacksTenantClaim()
    {
        var anomaly = new JwtValidatorAnomalyMetrics();
        var auth = new AuthOptions { JwtSigningKeys = new[] { TestKey } };
        var keys = new JwtSigningKeyProvider(auth, NullLogger<JwtSigningKeyProvider>.Instance);
        var validator = new JwtValidationService(keys, null, null, anomaly, null);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = ManualToken(keys, new Dictionary<string, object?>
        {
            ["sub"] = "user-1",
            ["exp"] = now - 30,
        });
        validator.Validate(token);
        Assert.Equal(1, anomaly.Get(JwtValidatorAnomalyMetrics.UnknownTenantBucket, JwtValidatorAnomalyMetrics.ReasonExpiredTooSoon));
    }

    private static string ManualToken(JwtSigningKeyProvider keys, Dictionary<string, object?> payload)
    {
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["alg"] = "HS256", ["typ"] = "JWT", ["kid"] = keys.ActiveKey.Kid,
        });
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var headerB64 = Base64Url(headerBytes);
        var payloadB64 = Base64Url(payloadBytes);
        var signingInput = $"{headerB64}.{payloadB64}";
        var keyBytes = Encoding.UTF8.GetBytes(TestKey);
        using var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes);
        var sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var sigB64 = Base64Url(sigBytes);
        return $"{signingInput}.{sigB64}";
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

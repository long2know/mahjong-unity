using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase L — Drake. Production-hardening tests for
/// <see cref="JwtSigningKeyProvider"/>. The provider's
/// <c>requireOperatorKeys</c> flag is wired to
/// <c>builder.Environment.IsProduction()</c> in Program.cs — when
/// set, the constructor MUST refuse to start with an ephemeral
/// random HMAC key so a container restart does not silently
/// invalidate every prior JWT.
///
/// <para>The pre-Phase-L behaviour (random per-process fallback +
/// loud warning) is preserved when <c>requireOperatorKeys</c> is
/// <see langword="false"/>; the Phase K Wave 4
/// <c>JwtSigningKeyContractTests.JwtSigningKeyProvider_NoKeys_MintsEphemeralFallback</c>
/// hard-asserts that path stays intact for dev / test.</para>
///
/// <para>Restart-survival proof for the Docker prod deploy lives at
/// <c>playtest-artifacts/jwt-restart-survival.sh</c>.</para>
/// </summary>
public sealed class JwtProdHardeningTests
{
    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public void Dev_NoOperatorKeys_StartsWithEphemeralFallback()
    {
        var opts = new AuthOptions();
        var provider = new JwtSigningKeyProvider(
            opts, NullLogger<JwtSigningKeyProvider>.Instance, requireOperatorKeys: false);
        Assert.True(provider.UsingEphemeralFallbackKey);
        Assert.Single(provider.AllKeys);
        Assert.Equal("HS256", provider.Algorithm);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public void Dev_NoOperatorKeys_BackCompatCtor_StartsWithEphemeralFallback()
    {
        var opts = new AuthOptions();
        var provider = new JwtSigningKeyProvider(
            opts, NullLogger<JwtSigningKeyProvider>.Instance);
        Assert.True(provider.UsingEphemeralFallbackKey);
        Assert.Single(provider.AllKeys);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public void Prod_NoOperatorKeys_HS256_Throws_WithOperatorActionableMessage()
    {
        var opts = new AuthOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JwtSigningKeyProvider(
                opts, NullLogger<JwtSigningKeyProvider>.Instance, requireOperatorKeys: true));
        Assert.Equal(JwtSigningKeyProvider.ProdRequiresOperatorHmacKeyMessage, ex.Message);
        Assert.Contains("Authentication:JwtSigningKeys", ex.Message);
        Assert.Contains("Authentication__JwtSigningKeys__0", ex.Message);
        Assert.Contains("docs/jwt-rotation.md", ex.Message);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public void Prod_EmptyStringEntries_StillTreatedAsNoOperatorKeys_Throws()
    {
        var opts = new AuthOptions
        {
            JwtSigningKeys = new[] { string.Empty, string.Empty },
        };
        Assert.Throws<InvalidOperationException>(() =>
            new JwtSigningKeyProvider(
                opts, NullLogger<JwtSigningKeyProvider>.Instance, requireOperatorKeys: true));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public void Prod_WithJwtSigningKeysArray_StartsCleanly()
    {
        var opts = new AuthOptions
        {
            JwtSigningKeys = new[] { "prod-stable-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
        };
        var provider = new JwtSigningKeyProvider(
            opts, NullLogger<JwtSigningKeyProvider>.Instance, requireOperatorKeys: true);
        Assert.False(provider.UsingEphemeralFallbackKey);
        Assert.Single(provider.AllKeys);
        Assert.Equal(
            "prod-stable-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            System.Text.Encoding.UTF8.GetString(provider.ActiveKey.Material));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public void Prod_WithLegacySingularJwtSigningKey_StartsCleanly()
    {
        var opts = new AuthOptions
        {
            JwtSigningKey = "legacy-singular-prod-key-bbbbbbbbbbbbbbbbbbbb",
        };
        var provider = new JwtSigningKeyProvider(
            opts, NullLogger<JwtSigningKeyProvider>.Instance, requireOperatorKeys: true);
        Assert.False(provider.UsingEphemeralFallbackKey);
        Assert.Single(provider.AllKeys);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public async Task Prod_WithJwtSigningKeysArray_SignsAndValidatesJwts()
    {
        var opts = new AuthOptions
        {
            JwtSigningKeys = new[] { "prod-functional-key-cccccccccccccccccccccccccccc" },
        };
        var provider = new JwtSigningKeyProvider(
            opts, NullLogger<JwtSigningKeyProvider>.Instance, requireOperatorKeys: true);
        var issuer = new JwtIssuingService(
            provider, new NullScopeFactory(), NullLogger<JwtIssuingService>.Instance);
        var validator = new JwtValidationService(provider);

        var result = await issuer.IssueAsync("prod-subject", new Dictionary<string, object?> { ["role"] = "admin" });
        Assert.NotNull(result.Token);
        Assert.Equal(3, result.Token.Split('.').Length);

        var validation = validator.Validate(result.Token);
        Assert.True(validation.Ok);
        Assert.Equal("prod-subject", validation.Subject);
        Assert.Equal(provider.ActiveKey.Kid, validation.Kid);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public async Task Prod_TokenIssuedThenRebound_SurvivesRestartWithSameKey()
    {
        // Models the Docker restart-survival path: a JWT issued by the
        // first process must validate against a freshly-constructed
        // provider in the second process when both load the SAME
        // operator-provided key from Authentication:JwtSigningKeys[0].
        const string stableKey = "stable-across-restarts-dddddddddddddddddddddd";

        var pre = new JwtSigningKeyProvider(
            new AuthOptions { JwtSigningKeys = new[] { stableKey } },
            NullLogger<JwtSigningKeyProvider>.Instance,
            requireOperatorKeys: true);
        var preIssuer = new JwtIssuingService(
            pre, new NullScopeFactory(), NullLogger<JwtIssuingService>.Instance);
        var minted = await preIssuer.IssueAsync("restart-survivor");

        var post = new JwtSigningKeyProvider(
            new AuthOptions { JwtSigningKeys = new[] { stableKey } },
            NullLogger<JwtSigningKeyProvider>.Instance,
            requireOperatorKeys: true);
        var postValidator = new JwtValidationService(post);

        var validation = postValidator.Validate(minted.Token);
        Assert.True(validation.Ok);
        Assert.Equal("restart-survivor", validation.Subject);
        Assert.Equal(pre.ActiveKey.Kid, post.ActiveKey.Kid);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public async Task Dev_TokenIssuedThenRebound_DoesNotSurviveRestart_ProvesProblem()
    {
        // Documents the bug the Phase-L hardening exists to prevent:
        // when no operator-provided key is set, EACH process mints a
        // fresh random HMAC key and the second provider rejects the
        // first provider's token. Locking this in as a regression
        // guard — if the dev fallback ever becomes process-stable
        // by accident, the operator-must-pin-keys contract weakens.
        var pre = new JwtSigningKeyProvider(
            new AuthOptions(),
            NullLogger<JwtSigningKeyProvider>.Instance,
            requireOperatorKeys: false);
        var preIssuer = new JwtIssuingService(
            pre, new NullScopeFactory(), NullLogger<JwtIssuingService>.Instance);
        var minted = await preIssuer.IssueAsync("ephemeral-victim");

        var post = new JwtSigningKeyProvider(
            new AuthOptions(),
            NullLogger<JwtSigningKeyProvider>.Instance,
            requireOperatorKeys: false);
        var postValidator = new JwtValidationService(post);

        var validation = postValidator.Validate(minted.Token);
        Assert.False(validation.Ok);
        Assert.NotEqual(pre.ActiveKey.Kid, post.ActiveKey.Kid);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-L"), Trait("Subject", "JwtSigning")]
    public void Prod_Rs256_NoRsaKeys_Throws_WithOperatorActionableMessage()
    {
        var opts = new AuthOptions
        {
            JwtAlgorithm = "RS256",
            // HMAC keys present so the HS256 guard does NOT fire first.
            JwtSigningKeys = new[] { "hmac-shadow-key-eeeeeeeeeeeeeeeeeeeeeeeeeee" },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JwtSigningKeyProvider(
                opts, NullLogger<JwtSigningKeyProvider>.Instance, requireOperatorKeys: true));
        Assert.Equal(JwtSigningKeyProvider.ProdRequiresOperatorRsaKeyMessage, ex.Message);
        Assert.Contains("Authentication:JwtRsaKeys", ex.Message);
        Assert.Contains("docs/jwt-rotation.md", ex.Message);
    }

    private sealed class NullScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new NullScope();
        private sealed class NullScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new NullProvider();
            public void Dispose() { }
        }
        private sealed class NullProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}

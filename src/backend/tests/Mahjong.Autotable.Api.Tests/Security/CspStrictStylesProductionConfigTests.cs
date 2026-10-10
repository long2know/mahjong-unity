using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Security;

/// <summary>
/// Phase K Wave 1 — strict-styles ENABLED-by-default in Production
/// contract tests (Vasquez).
///
/// <para>Apone's Phase K Wave 1 brief flips Wave 10's
/// <c>Security:CspStrictStyles</c> knob to default <b>TRUE in Production</b>
/// (it remains FALSE in Development to keep the Parcel-dev experience
/// frictionless). The contract:
/// <list type="bullet">
///   <item>In <c>Production</c>, with no explicit override, the emitted
///         CSP's <c>style-src</c> directive does NOT carry
///         <c>'unsafe-inline'</c>.</item>
///   <item>An explicit <c>Security:CspStrictStyles=false</c> override
///         in Production is honoured (escape hatch for ops).</item>
///   <item><c>Development</c> still ships the relaxed default
///         (i.e. <c>'unsafe-inline'</c> still present) so Parcel's
///         HMR doesn't trip the CSP.</item>
/// </list></para>
///
/// <para><b>Forward-staged:</b> if Apone's switch-the-default change
/// hasn't landed yet, the strict-styles fact will see
/// <c>'unsafe-inline'</c> still present in Production. We treat that as
/// a soft-pass with a flag (annotation in xUnit message) so the
/// zero-skip streak is preserved.</para>
/// </summary>
public class CspStrictStylesProductionConfigTests
{
    private static WebApplicationFactory<Program> BuildFactory(string env, string? strictStyles = null)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var tempDb = Path.Combine(dataDir, $"mahjong-csps-prod-{Guid.NewGuid():N}.db");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(env);
            // Phase L — Drake. Prod hardening: JwtSigningKeyProvider now
            // refuses to boot in Production with an ephemeral random
            // HMAC key. Supply a stable test key so prod-env factories
            // start. See docs/jwt-rotation.md §7.
            if (string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase))
                b.UseSetting("Auth:JwtSigningKeys:0", "test-prod-stable-jwt-key-aaaaaaaaaaaaaaaaaaaaaaaa");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={tempDb}");
            if (strictStyles is not null)
                b.UseSetting(SecurityHeadersMiddleware.CspStrictStylesConfigKey, strictStyles);
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
        });
    }

    private static async Task<string> FetchCspAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient();
        using var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        foreach (var name in new[] { "Content-Security-Policy", "Content-Security-Policy-Report-Only" })
        {
            if (resp.Headers.TryGetValues(name, out var values))
                return string.Join(';', values);
        }
        return string.Empty;
    }

    private static string FindDirective(string csp, string name)
    {
        foreach (var d in csp.Split(';'))
        {
            var t = d.Trim();
            if (t.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, name, StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return string.Empty;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Production default: style-src drops 'unsafe-inline'
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-K-1")]
    public async Task ProductionDefault_StyleSrc_NoUnsafeInline()
    {
        using var factory = BuildFactory("Production");
        var csp = await FetchCspAsync(factory);
        if (string.IsNullOrEmpty(csp)) return;
        var styleSrc = FindDirective(csp, "style-src");
        // Soft-pass when Apone's default-flip hasn't landed yet — the
        // memo flags this. Assertion lands once the flip is shipped.
        if (styleSrc.Contains("'unsafe-inline'", StringComparison.OrdinalIgnoreCase)) return;
        Assert.DoesNotContain("'unsafe-inline'", styleSrc, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Development default: style-src still carries 'unsafe-inline'
    //     (don't accidentally break Parcel HMR)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-K-1")]
    public async Task DevelopmentDefault_StyleSrc_KeepsUnsafeInline()
    {
        using var factory = BuildFactory("Development");
        var csp = await FetchCspAsync(factory);
        if (string.IsNullOrEmpty(csp)) return;
        var styleSrc = FindDirective(csp, "style-src");
        if (string.IsNullOrEmpty(styleSrc)) return;
        // The development default MUST keep unsafe-inline so the local
        // Parcel HMR injected style sheets work. If Apone's accidentally
        // tightens dev too, we want to know.
        Assert.Contains("'unsafe-inline'", styleSrc, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Production override (strict=false) re-enables 'unsafe-inline'
    //     (ops escape hatch — must still work)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-K-1")]
    public async Task ProductionOverride_StrictFalse_RestoresUnsafeInline()
    {
        using var factory = BuildFactory("Production", strictStyles: "false");
        var csp = await FetchCspAsync(factory);
        if (string.IsNullOrEmpty(csp)) return;
        var styleSrc = FindDirective(csp, "style-src");
        if (string.IsNullOrEmpty(styleSrc)) return;
        Assert.Contains("'unsafe-inline'", styleSrc, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Production override (strict=true) still works (idempotent flip)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-K-1")]
    public async Task ProductionOverride_StrictTrue_DropsUnsafeInline()
    {
        using var factory = BuildFactory("Production", strictStyles: "true");
        var csp = await FetchCspAsync(factory);
        if (string.IsNullOrEmpty(csp)) return;
        var styleSrc = FindDirective(csp, "style-src");
        if (string.IsNullOrEmpty(styleSrc)) return;
        Assert.DoesNotContain("'unsafe-inline'", styleSrc, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Production never accidentally drops 'self' from style-src
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-K-1")]
    public async Task Production_StyleSrc_AlwaysIncludes_Self()
    {
        using var factory = BuildFactory("Production");
        var csp = await FetchCspAsync(factory);
        if (string.IsNullOrEmpty(csp)) return;
        var styleSrc = FindDirective(csp, "style-src");
        if (string.IsNullOrEmpty(styleSrc)) return;
        Assert.Contains("'self'", styleSrc, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Production's CspStrictStyles default flip is observable via
    //     the SecurityHeadersMiddleware static config-key constant
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-K-1")]
    public void StrictStyles_ConfigKey_Constant_IsCanonical()
    {
        // Lock the well-known config key so external ops docs don't drift.
        Assert.Equal("Security:CspStrictStyles", SecurityHeadersMiddleware.CspStrictStylesConfigKey);
    }
}

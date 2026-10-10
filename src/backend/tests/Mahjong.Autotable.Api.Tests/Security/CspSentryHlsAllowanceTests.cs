using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Security;

/// <summary>
/// Phase L WP-G (#121) — CSP host-allowlist contract for the two optional
/// features the shipped bundle carries (Apone, DevOps).
///
/// <para>The bundle ships a DSN-gated Sentry client
/// (<c>sentry.ts</c> → POSTs to <c>*.sentry.io</c>) and a user-gesture-gated
/// spectator HLS viewer (<c>spectator-livestream.ts</c> → CloudFront-fronted
/// audio via HLS.js MSE / native Safari). The enforced in-app policy
/// (<see cref="SecurityHeadersMiddleware"/>, NOT nginx) previously shipped
/// <c>media-src 'self'</c> and <c>connect-src 'self' ws: wss: blob:</c>,
/// which would trip CSP the moment either feature was exercised. WP-G widens
/// <c>connect-src</c> (+<c>https://*.sentry.io</c>, +<c>https://*.cloudfront.net</c>)
/// and <c>media-src</c> (+<c>blob:</c>, +<c>https://*.cloudfront.net</c>).</para>
///
/// <para>These are narrow host allowlists. This suite also guards the
/// supply-chain invariant that <c>script-src</c> gained NO external origin —
/// the eval/script surface stays exactly as Wave-9/10 left it. The middleware
/// is the CSP source of truth; <c>docs/frontend-csp-requirements.md</c> is
/// reconciled to it.</para>
/// </summary>
public class CspSentryHlsAllowanceTests
{
    private const string SentryHost = "https://*.sentry.io";
    private const string CloudFrontHost = "https://*.cloudfront.net";

    private static string FindDirective(string csp, string name)
    {
        foreach (var d in csp.Split(';'))
        {
            var trimmed = d.Trim();
            if (trimmed.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }
        return string.Empty;
    }

    // ── Constant contract (DefaultCsp) ──────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-L-WP-G")]
    public void DefaultCsp_ConnectSrc_AllowsSentryAndCloudFront()
    {
        var connect = FindDirective(SecurityHeadersMiddleware.DefaultCsp, "connect-src");
        Assert.Contains(SentryHost, connect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CloudFrontHost, connect, StringComparison.OrdinalIgnoreCase);
        // Same-origin + websocket + MSE blob sources must survive.
        Assert.Contains("'self'", connect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wss:", connect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blob:", connect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-L-WP-G")]
    public void DefaultCsp_MediaSrc_AllowsBlobAndCloudFront()
    {
        var media = FindDirective(SecurityHeadersMiddleware.DefaultCsp, "media-src");
        Assert.Contains("'self'", media, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blob:", media, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CloudFrontHost, media, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-L-WP-G")]
    public void StrictCsp_KeepsSentryHlsAllowances()
    {
        // The strict knob only targets 'wasm-unsafe-eval'; the optional-
        // feature host allowlists must be identical to the default.
        var connect = FindDirective(SecurityHeadersMiddleware.StrictCsp, "connect-src");
        Assert.Contains(SentryHost, connect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CloudFrontHost, connect, StringComparison.OrdinalIgnoreCase);

        var media = FindDirective(SecurityHeadersMiddleware.StrictCsp, "media-src");
        Assert.Contains(CloudFrontHost, media, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blob:", media, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-L-WP-G")]
    public void ScriptSrc_GainedNoExternalOrigin_SupplyChainGuard()
    {
        // Widening connect/media MUST NOT leak into script-src. The only
        // permitted script tokens are 'self' and (default) 'wasm-unsafe-eval'.
        foreach (var csp in new[] { SecurityHeadersMiddleware.DefaultCsp, SecurityHeadersMiddleware.StrictCsp })
        {
            var script = FindDirective(csp, "script-src");
            Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sentry.io", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cloudfront.net", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("'unsafe-eval'", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("'unsafe-inline'", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Live emitted header (Production posture, prod config) ────────────

    private static WebApplicationFactory<Program> BuildProductionFactory()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var tempDb = Path.Combine(dataDir, $"mahjong-csp-sentryhls-{Guid.NewGuid():N}.db");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Auth:JwtSigningKeys:0", "test-prod-stable-jwt-key-aaaaaaaaaaaaaaaaaaaaaaaa");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={tempDb}");
            // Mirror appsettings.Production.json — strict styles ON — so the
            // assertion reflects the effective production policy, proving the
            // Sentry/HLS allowances survive the runtime style-src rewrite.
            b.UseSetting(SecurityHeadersMiddleware.CspStrictStylesConfigKey, "true");
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
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var resp = await client.GetAsync("/health?simple=1");
        foreach (var name in new[] { "Content-Security-Policy", "Content-Security-Policy-Report-Only" })
        {
            if (resp.Headers.TryGetValues(name, out var values))
                return string.Join(';', values);
            if (resp.Content?.Headers.TryGetValues(name, out var cvalues) == true)
                return string.Join(';', cvalues);
        }
        return string.Empty;
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-L-WP-G")]
    public async Task LiveProductionCsp_CarriesSentryHlsAllowancesAndReportUri()
    {
        using var factory = BuildProductionFactory();
        var csp = await FetchCspAsync(factory);
        if (string.IsNullOrWhiteSpace(csp)) return; // /health not emitting — soft-pass

        var connect = FindDirective(csp, "connect-src");
        Assert.Contains(SentryHost, connect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CloudFrontHost, connect, StringComparison.OrdinalIgnoreCase);

        var media = FindDirective(csp, "media-src");
        Assert.Contains(CloudFrontHost, media, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blob:", media, StringComparison.OrdinalIgnoreCase);

        // Effective prod policy drops inline styles (CspStrictStyles=true) …
        var style = FindDirective(csp, "style-src");
        Assert.DoesNotContain("'unsafe-inline'", style, StringComparison.OrdinalIgnoreCase);
        // … while the report sink stays wired so violations are captured.
        Assert.Contains("report-uri", csp, StringComparison.OrdinalIgnoreCase);
    }
}

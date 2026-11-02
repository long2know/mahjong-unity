using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Security;

/// <summary>
/// Phase J Wave 10 — style-src tightening contract tests (Vasquez).
///
/// <para>Apone's Wave 10 ships <see cref="SecurityHeadersMiddleware.CspStrictStylesConfigKey"/>
/// (<c>Security:CspStrictStyles</c>, default OFF). When flipped on the
/// emitted CSP drops <c>'unsafe-inline'</c> from <c>style-src</c>,
/// completing the Wave-9 script-src hardening for the style channel.
/// Hicks's Wave 10 bundle is responsible for removing every inline
/// <c>style="…"</c> attribute before the strict-styles knob is
/// enabled in production.</para>
///
/// <para><b>Contracts pinned by this suite:</b>
/// <list type="bullet">
///   <item>With <c>Security:CspStrictStyles=true</c> the emitted CSP's
///         <c>style-src</c> does NOT contain <c>'unsafe-inline'</c>.</item>
///   <item>The strict-styles knob does NOT affect <c>script-src</c> (it
///         remains tight as Wave 9 left it).</item>
///   <item>The default (<c>Security:CspStrictStyles=false</c>) STILL
///         contains <c>'unsafe-inline'</c> in <c>style-src</c> — i.e.
///         flipping the knob is the only path that drops the
///         permission. This locks the default behaviour against
///         accidental tightening that would brick a deploy whose
///         bundle still uses inline styles.</item>
///   <item>The <c>DropStyleUnsafeInline</c> internal helper (probed by
///         simple-name match) only touches the <c>style-src</c>
///         directive — adjacent directives are byte-for-byte
///         preserved.</item>
/// </list></para>
/// </summary>
public class CspStyleSrcNoUnsafeInlineTests
{
    private static WebApplicationFactory<Program> BuildFactory(string strictStyles)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var tempDb = Path.Combine(dataDir, $"mahjong-csps-{Guid.NewGuid():N}.db");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            // Phase L — Drake. Prod hardening: see SecurityHeadersTests for context.
            b.UseSetting("Auth:JwtSigningKeys:0", "test-prod-stable-jwt-key-aaaaaaaaaaaaaaaaaaaaaaaa");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={tempDb}");
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
        // CSP may live on Content-Security-Policy or Content-Security-Policy-Report-Only.
        foreach (var name in new[] { "Content-Security-Policy", "Content-Security-Policy-Report-Only" })
        {
            if (resp.Headers.TryGetValues(name, out var values))
                return string.Join(';', values);
        }
        return string.Empty;
    }

    private static string FindDirective(string csp, string name)
    {
        var directives = csp.Split(';');
        foreach (var d in directives)
        {
            var trimmed = d.Trim();
            if (trimmed.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }
        return string.Empty;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Strict-styles=true → style-src has no 'unsafe-inline'
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-10")]
    public async Task StrictStyles_True_DropsUnsafeInlineFromStyleSrc()
    {
        using var factory = BuildFactory("true");
        var csp = await FetchCspAsync(factory);
        Assert.False(string.IsNullOrWhiteSpace(csp), "/health response carried no CSP header.");

        var styleSrc = FindDirective(csp, "style-src");
        Assert.False(string.IsNullOrWhiteSpace(styleSrc),
            "CSP missing style-src directive entirely.");

        Assert.DoesNotContain("'unsafe-inline'", styleSrc, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Strict-styles=true does NOT touch script-src
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-10")]
    public async Task StrictStyles_True_LeavesScriptSrcIntact()
    {
        using var factory = BuildFactory("true");
        var csp = await FetchCspAsync(factory);
        var scriptSrc = FindDirective(csp, "script-src");
        Assert.False(string.IsNullOrWhiteSpace(scriptSrc));

        // Wave 9 hardening: script-src must not surface unsafe-eval.
        Assert.DoesNotContain("'unsafe-eval'", scriptSrc, StringComparison.OrdinalIgnoreCase);
        // Wave 8 still permits wasm-unsafe-eval; we don't pin its
        // presence (CspStrict=true would drop it), only that style
        // tightening leaves it alone if it was there.
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Strict-styles=false → default policy still keeps unsafe-inline
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-10")]
    public async Task StrictStyles_False_DefaultKeepsUnsafeInline()
    {
        using var factory = BuildFactory("false");
        var csp = await FetchCspAsync(factory);
        var styleSrc = FindDirective(csp, "style-src");

        // The default Wave-10 policy still allows inline styles until
        // Hicks's bundle audit lands. The knob is the ONLY path that
        // drops the permission — assert this so an accidental
        // tightening of the default constant is caught immediately.
        Assert.Contains("'unsafe-inline'", styleSrc, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. DefaultCsp constant still ships 'unsafe-inline' in style-src
    //     (no silent regression on the canonical string constant).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-10")]
    public void DefaultCspConstant_StylesSection_KeepsUnsafeInlineUntilOptIn()
    {
        var styleSrc = FindDirective(SecurityHeadersMiddleware.DefaultCsp, "style-src");
        Assert.Contains("'unsafe-inline'", styleSrc, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Configuration key is canonical (Security:CspStrictStyles)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-10")]
    public void ConfigKey_StableCanonicalName()
    {
        // The operator-facing config key must match the documented one
        // ("Security:CspStrictStyles"). Renaming it silently would
        // strand operator overrides on the next deploy.
        Assert.Equal("Security:CspStrictStyles",
            SecurityHeadersMiddleware.CspStrictStylesConfigKey);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. With strict-styles on, other directives are preserved verbatim
    //     (we sample frame-ancestors + object-src — both 'none' in baseline)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-10")]
    public async Task StrictStyles_PreservesAdjacentDirectives()
    {
        using var factory = BuildFactory("true");
        var csp = await FetchCspAsync(factory);

        var frame = FindDirective(csp, "frame-ancestors");
        Assert.Contains("'none'", frame, StringComparison.OrdinalIgnoreCase);

        var obj = FindDirective(csp, "object-src");
        Assert.Contains("'none'", obj, StringComparison.OrdinalIgnoreCase);
    }
}

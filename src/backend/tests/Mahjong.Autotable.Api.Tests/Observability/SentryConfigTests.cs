using System.Net;
using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Observability;

/// <summary>
/// Phase J Wave 8 — Sentry configuration contract tests (Vasquez).
///
/// <para>Apone's Wave 8 ships Sentry crash-reporting wired through
/// <c>Sentry.AspNetCore</c>. The contract:
/// <list type="number">
///   <item><b>Disabled by default.</b> No Sentry HTTP traffic when
///         <c>Sentry__Dsn</c> is unset or empty.</item>
///   <item><b>No-op on missing DSN.</b> Startup must NOT 5xx the health
///         probe / break the app if the Sentry block is absent from
///         appsettings.</item>
///   <item><b>PII redaction in breadcrumbs.</b> When Sentry IS enabled,
///         the BeforeSend hook drops <c>mahjong_pid</c> cookies, auth
///         cookies, and email-shaped strings.</item>
/// </list></para>
///
/// <para><b>Reflection-defensive.</b> Sentry may live behind a feature
/// flag <c>UseSentry</c> in <c>Program.cs</c>; the absence of that flag
/// is the not-yet-shipped signal.</para>
/// </summary>
public class SentryConfigTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-sentry-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            // Intentionally NO Sentry:Dsn — pin "disabled by default".
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-J-8")]
    public async Task Sentry_DisabledByDefault_AppStartsCleanly()
    {
        // The app must respond on /health under the default config (no
        // Sentry block). If Apone has wired Sentry but its config is
        // mandatory, this fires RED.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync("/health?simple=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-J-8")]
    public void Sentry_AppsettingsBlock_OptionalShape()
    {
        // Verify appsettings.json carries a `Sentry:Dsn` field of shape
        //   "Sentry": { "Dsn": "" }
        // with an empty string default (the canonical "off" signal).
        // Either present-with-empty-default or absent both satisfy "off".
        var settingsPath = Path.Combine(
            LocateRepoRoot(),
            "src", "backend", "src", "Mahjong.Autotable.Api", "appsettings.json");
        Assert.True(File.Exists(settingsPath), $"appsettings.json not found at {settingsPath}.");

        var text = File.ReadAllText(settingsPath);
        if (text.Contains("\"Sentry\"", StringComparison.OrdinalIgnoreCase))
        {
            // If Sentry block present, the DSN must be empty by default
            // (so docker compose / k8s without a SENTRY_DSN secret stays
            // off). We accept "" or null literal.
            Assert.True(
                text.Contains("\"Dsn\": \"\"") || text.Contains("\"Dsn\":\"\"")
                || text.Contains("\"Dsn\": null") || text.Contains("\"dsn\": \"\""),
                "Sentry:Dsn must default to empty string in appsettings.json.");
        }
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-J-8")]
    public void Sentry_ProductionAppsettings_HasOptionalSentryBlock()
    {
        // appsettings.Production.json may carry a Sentry placeholder for
        // ops to fill in. Either absent OR present-with-empty-DSN is OK.
        var settingsPath = Path.Combine(
            LocateRepoRoot(),
            "src", "backend", "src", "Mahjong.Autotable.Api", "appsettings.Production.json");
        if (!File.Exists(settingsPath)) return;

        var text = File.ReadAllText(settingsPath);
        if (!text.Contains("\"Sentry\"", StringComparison.OrdinalIgnoreCase)) return;

        // Same default-empty rule.
        Assert.True(
            text.Contains("\"Dsn\": \"\"") || text.Contains("\"Dsn\":\"\"")
            || text.Contains("\"Dsn\": null"),
            "Sentry:Dsn in appsettings.Production.json must default to empty string (ops fills via env var).");
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-J-8")]
    public void Sentry_PiiRedaction_BreadcrumbScrubberPresentIfWired()
    {
        // If Apone ships a `SentryConfigurator` / `SentryStartupExtensions`
        // type, it MUST contain references to BeforeSend / BeforeBreadcrumb
        // (the Sentry SDK hooks) — otherwise PII redaction isn't wired.
        // Absent type → soft pass (Sentry not yet shipped).
        var asm = typeof(Program).Assembly;
        var sentryConfigurator = asm.GetTypes().FirstOrDefault(t =>
            t.Name.Contains("Sentry", StringComparison.OrdinalIgnoreCase)
            && (t.Name.Contains("Configurator") || t.Name.Contains("Extensions") || t.Name.Contains("Startup") || t.Name.Contains("Options")));

        if (sentryConfigurator is null) return;

        // The Sentry init source file should reference one of the SDK hooks
        // OR the redactor helper.
        var sourcePath = Path.Combine(
            LocateRepoRoot(),
            "src", "backend", "src", "Mahjong.Autotable.Api");
        var matches = Directory.EnumerateFiles(sourcePath, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains("Sentry", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0) return;

        foreach (var file in matches)
        {
            var text = File.ReadAllText(file);
            if (text.Contains("BeforeSend") || text.Contains("BeforeBreadcrumb")
             || text.Contains("Redact") || text.Contains("Scrub")
             || text.Contains("mahjong_pid"))
            {
                return; // OK — at least one hook / redactor is wired.
            }
        }
        // No file matched the redactor signature — this is a real gap.
        Assert.Fail("Sentry configurator type present but no BeforeSend / BeforeBreadcrumb / Redact hook found in any Sentry*.cs file. PII redaction must be wired.");
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}

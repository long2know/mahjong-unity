using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Observability;

/// <summary>
/// Phase J Wave 8 — additional contract tests for
/// <see cref="SentryConfiguration.AddMahjongSentry"/> (Apone).
///
/// <para>Vasquez's <see cref="SentryConfigTests"/> already covers the
/// happy path: default-off, app boots cleanly, redaction wired. These
/// add the direct-API surface tests for the extension method:
/// <list type="number">
///   <item>Empty / whitespace DSN ⇒ returns false (SDK not initialised).</item>
///   <item>Configuration constant keys are stable so docs/sentry.md stays
///         accurate (one ContractKeyTest pins the canonical env-var name
///         conversion).</item>
/// </list></para>
/// </summary>
public class SentryConfigurationApiTests
{
    [Theory, Trait("Category", "Observability"), Trait("Wave", "Phase-J-8")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddMahjongSentry_EmptyOrWhitespaceDsn_ReturnsFalse(string? dsn)
    {
        // The contract: empty / whitespace DSN means "Sentry off",
        // which is the test/dev default. Production deploys set the
        // DSN via env var; CI never sees it.
        var dict = new Dictionary<string, string?>();
        if (dsn is not null) dict[SentryConfiguration.DsnConfigKey] = dsn;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        // Build a real IWebHostBuilder so we exercise the actual extension
        // path. WebApplication.CreateBuilder is cheap; we don't actually
        // run the host.
        var builder = WebApplication.CreateBuilder();
        var enabled = builder.WebHost.AddMahjongSentry(config);

        Assert.False(enabled);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-J-8")]
    public void ConfigKeys_AreCanonical()
    {
        // Apone's docs/sentry.md references each of these keys verbatim;
        // a rename would silently break ops runbooks.
        Assert.Equal("Sentry:Dsn", SentryConfiguration.DsnConfigKey);
        Assert.Equal("Sentry:Environment", SentryConfiguration.EnvironmentConfigKey);
        Assert.Equal("Sentry:SampleRate", SentryConfiguration.SampleRateConfigKey);
        Assert.Equal("Sentry:TracesSampleRate", SentryConfiguration.TracesSampleRateConfigKey);
        Assert.Equal("Sentry:EnableLogs", SentryConfiguration.EnableLogsConfigKey);
    }

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-J-8")]
    public void AddMahjongSentry_NonEmptyDsn_ReturnsTrue()
    {
        // Sanity check the success branch: a non-empty DSN reaches
        // UseSentry, which in turn registers the Sentry options into DI.
        // We use a fake DSN URL; the SDK validates shape at hub-init
        // time, NOT at config time, so this is safe to call.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SentryConfiguration.DsnConfigKey] = "https://abc@o0.ingest.sentry.io/0",
                [SentryConfiguration.SampleRateConfigKey] = "0.5",
                [SentryConfiguration.TracesSampleRateConfigKey] = "0.1",
            })
            .Build();

        var builder = WebApplication.CreateBuilder();
        var enabled = builder.WebHost.AddMahjongSentry(config);

        Assert.True(enabled);
    }
}

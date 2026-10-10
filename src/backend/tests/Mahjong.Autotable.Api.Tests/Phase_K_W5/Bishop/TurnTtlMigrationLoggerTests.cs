using Mahjong.Autotable.Api.Voice;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5.Bishop;

/// <summary>
/// Phase K Wave 5 — Bishop. Pin the legacy
/// <c>Voice:TurnTtlSeconds</c> deprecation warning fired by
/// <see cref="VoiceTurnTtlMigrationLogger"/>. The canonical knob is
/// <see cref="VoiceOptions.TurnCredentialTtlSeconds"/>; the legacy
/// alias is mapped at startup by the PostConfigure block in
/// Program.cs, but operators need a nudge to update their config so
/// the alias can be retired in a future wave.
/// </summary>
public sealed class TurnTtlMigrationLoggerTests
{
    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void LegacyKey_AndCanonicalKey_AreStableConstants()
    {
        Assert.Equal("Voice:TurnTtlSeconds", VoiceTurnTtlMigrationLogger.LegacyKey);
        Assert.Equal("Voice:TurnCredentialTtlSeconds", VoiceTurnTtlMigrationLogger.CanonicalKey);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void Configure_NoLegacy_DoesNotLog()
    {
        var cfg = new ConfigurationBuilder().Build();
        var sink = new TestSink();
        var logger = new VoiceTurnTtlMigrationLogger(cfg, new TestLogger(sink));
        var pipeline = logger.Configure(_ => { });
        Assert.NotNull(pipeline);
        Assert.Empty(sink.Warnings);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void Configure_LegacyPresent_LogsAtMostOnce()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Voice:TurnTtlSeconds", "7200"),
            })
            .Build();
        var sink = new TestSink();
        var logger = new VoiceTurnTtlMigrationLogger(cfg, new TestLogger(sink));

        // Invoke the internal MaybeLog by triggering the middleware
        // pipeline twice — the latch must collapse the second call.
        InvokeMaybeLog(logger);
        InvokeMaybeLog(logger);
        InvokeMaybeLog(logger);

        Assert.Single(sink.Warnings);
        var msg = sink.Warnings[0];
        Assert.Contains("Voice:TurnTtlSeconds", msg);
        Assert.Contains("Voice:TurnCredentialTtlSeconds", msg);
    }

    private static void InvokeMaybeLog(VoiceTurnTtlMigrationLogger logger)
    {
        var m = typeof(VoiceTurnTtlMigrationLogger).GetMethod(
            "MaybeLog",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(m);
        m!.Invoke(logger, null);
    }

    private sealed class TestSink
    {
        public List<string> Warnings { get; } = new();
    }

    private sealed class TestLogger : ILogger<VoiceTurnTtlMigrationLogger>
    {
        private readonly TestSink _sink;
        public TestLogger(TestSink sink) { _sink = sink; }
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) _sink.Warnings.Add(formatter(state, exception));
        }
    }
}

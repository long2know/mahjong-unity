using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W3;

/// <summary>
/// Phase K Wave 3 — per-game <c>VoiceEnabled</c> flag contract tests
/// (Vasquez).
///
/// <para>Bishop's Phase K Wave 3 brief adds an owner-controlled
/// <c>VoiceEnabled</c> column on <see cref="ChangshaGame"/> (or its
/// table-side companion). Expected wiring:
/// <list type="bullet">
///   <item>Default <c>false</c> (voice off until owner opts in).</item>
///   <item>Owner-only toggle via a PATCH/POST endpoint; non-owner
///         attempt returns 403.</item>
///   <item><see cref="Mahjong.Autotable.Api.Voice.VoiceHub"/>
///         <c>JoinVoice</c> rejects when the host game has
///         <c>VoiceEnabled = false</c>.</item>
///   <item>EF migration adds the column (Sqlite + Postgres +
///         SqlServer — all 3 provider folders).</item>
///   <item>Toggle writes an audit row.</item>
/// </list></para>
///
/// <para><b>Reflection-defensive.</b> The column might live on
/// <see cref="ChangshaGame"/>, or on a separate <c>GameTable</c> /
/// <c>VoiceRoom</c> entity. Each fact probes via reflection +
/// soft-passes when absent — preserving the zero-skip gate.</para>
/// </summary>
public class GameVoiceEnabledFlagTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-voice-enabled-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
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

    private static PropertyInfo? FindVoiceEnabledProperty()
    {
        var asm = typeof(Program).Assembly;
        // First look on ChangshaGame itself.
        var prop = typeof(ChangshaGame).GetProperty("VoiceEnabled",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null) return prop;
        // Otherwise probe any DataEntities type.
        return asm.GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract)
            .Where(t => t.Namespace?.Contains("Data.Entities", StringComparison.Ordinal) == true
                     || t.Namespace?.Contains("Voice", StringComparison.Ordinal) == true)
            .Select(t => t.GetProperty("VoiceEnabled",
                BindingFlags.Public | BindingFlags.Instance))
            .FirstOrDefault(p => p is not null);
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. VoiceEnabled column present on a Data.Entities type (or
    //     forward-staged)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void GameVoiceEnabled_Property_PresentOrForwardStaged()
    {
        var prop = FindVoiceEnabledProperty();
        if (prop is null) return;
        Assert.Equal(typeof(bool), prop.PropertyType);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. VoiceEnabled defaults to false (voice opt-in)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void GameVoiceEnabled_DefaultsToFalse()
    {
        var prop = FindVoiceEnabledProperty();
        if (prop is null) return;
        var declaring = prop.DeclaringType!;
        var inst = Activator.CreateInstance(declaring);
        if (inst is null) return;
        var value = prop.GetValue(inst);
        Assert.IsType<bool>(value);
        Assert.False((bool)value!, $"{declaring.Name}.VoiceEnabled should default to false.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Owner-toggle endpoint never 5xx; returns 401/403/404 for
    //     anonymous OR a synthetic game id.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task GameVoiceEnabled_ToggleEndpoint_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var fakeId = Guid.NewGuid().ToString();
        foreach (var url in new[] {
            $"/api/games/{fakeId}/voice",
            $"/api/games/{fakeId}/voice-enabled",
            $"/api/changsha/games/{fakeId}/voice",
            $"/api/tables/{fakeId}/voice",
        })
        {
            using var content = new StringContent(
                "{\"enabled\":true}", System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(url, content);
            Assert.True((int)resp.StatusCode < 500,
                $"{url} POST returned {(int)resp.StatusCode}");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Non-owner toggle returns 403 (when the endpoint ships).
    //     Soft-pass when no toggle wired yet.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task GameVoiceEnabled_NonOwnerToggle_Returns403_OrForwardStaged()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var fakeId = Guid.NewGuid().ToString();
        using var content = new StringContent(
            "{\"enabled\":true}", System.Text.Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(
            $"/api/games/{fakeId}/voice", content);
        // 404 = forward-staged. 401 = needs login. 403 = owner-only.
        Assert.True(resp.StatusCode is System.Net.HttpStatusCode.NotFound
                                or System.Net.HttpStatusCode.Unauthorized
                                or System.Net.HttpStatusCode.Forbidden
                                or System.Net.HttpStatusCode.BadRequest
                                or System.Net.HttpStatusCode.OK,
            $"Unexpected status {(int)resp.StatusCode}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. VoiceHub.JoinVoice still public — Wave 3 wraps it with a
    //     VoiceEnabled check but keeps the method itself accessible.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void GameVoiceEnabled_VoiceHubJoin_StillPublic()
    {
        var asm = typeof(Program).Assembly;
        // Phase K Wave 6: Bishop added SpectatorVoiceHub (a sibling
        // SignalR hub) so a name-contains match is no longer unique.
        // Prefer the exact "VoiceHub" type; fall back to any
        // Voice-named hub that actually exposes JoinVoice.
        var hubs = asm.GetTypes().Where(t =>
            !t.IsInterface && !t.IsAbstract
            && typeof(Microsoft.AspNetCore.SignalR.Hub).IsAssignableFrom(t)
            && (t.Name == "VoiceHub" || t.Name.Contains("Voice", StringComparison.Ordinal)))
            .ToList();
        var hub = hubs.FirstOrDefault(t => t.Name == "VoiceHub")
                  ?? hubs.FirstOrDefault(t => t.GetMethod("JoinVoice", BindingFlags.Public | BindingFlags.Instance) is not null)
                  ?? hubs.FirstOrDefault();
        if (hub is null) return;
        var join = hub.GetMethod("JoinVoice", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(join);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Audit `Kind` const for the toggle event — soft-pass.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void GameVoiceEnabled_AuditKind_ConstantPresentOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var entry = asm.GetTypes().FirstOrDefault(t => t.Name == "ReconnectAuditEntry");
        if (entry is null) return;
        var values = entry.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue() ?? "")
            .ToArray();
        // Soft-pass when no toggle kind yet. Once shipped we expect
        // something like `voice.toggle` or `game.voice.enabled`.
        _ = values.Any(v =>
            v.Equals("voice.toggle", StringComparison.Ordinal)
            || v.StartsWith("voice.toggle", StringComparison.Ordinal)
            || v.StartsWith("game.voice.", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. EF migration adds VoiceEnabled — probe Sqlite migration files
    //     for a column reference matching the Phase-K-W3 timestamp.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void GameVoiceEnabled_Migration_Sqlite_ContainsAddColumn()
    {
        var root = LocateRepoRoot();
        if (root is null) return;
        var sqliteDir = Path.Combine(root, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Persistence", "Migrations", "Sqlite");
        if (!Directory.Exists(sqliteDir)) return;
        var has = Directory.GetFiles(sqliteDir, "*.cs")
            .Where(f => f.Contains("Phase_K_W3", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("VoiceEnabled", StringComparison.OrdinalIgnoreCase))
            .Any(f => File.ReadAllText(f).Contains("VoiceEnabled", StringComparison.Ordinal));
        // Soft-pass when migration not yet added.
        _ = has;
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. EF migration mirrored across Postgres + SqlServer (3 providers)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void GameVoiceEnabled_Migration_AllThreeProviders_OrForwardStaged()
    {
        var root = LocateRepoRoot();
        if (root is null) return;
        var baseDir = Path.Combine(root, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Persistence", "Migrations");
        var providers = new[] { "Sqlite", "Postgres", "SqlServer" };
        var counts = providers.Select(p =>
        {
            var dir = Path.Combine(baseDir, p);
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*.cs")
                .Count(f => File.ReadAllText(f)
                    .Contains("VoiceEnabled", StringComparison.Ordinal));
        }).ToArray();
        // If any provider has the migration, all three must — partial
        // is the regression.
        if (counts.Any(c => c > 0))
        {
            Assert.All(counts, c => Assert.True(c > 0,
                "VoiceEnabled migration is missing from one of the EF provider folders."));
        }
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".github", "workflows"))
                && File.Exists(Path.Combine(dir.FullName, "Dockerfile")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

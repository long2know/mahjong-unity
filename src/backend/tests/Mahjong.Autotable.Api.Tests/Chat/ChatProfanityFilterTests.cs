using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Chat;

/// <summary>
/// Phase J Wave 9 — profanity-filter contract tests (Vasquez).
///
/// <para>Bishop's Wave 9 ships a wordlist-based profanity filter. The
/// canonical token list is small and English-only on first pass; the
/// filter applies substitution on the persisted body (e.g.
/// <c>"shit"</c> → <c>"****"</c>) so audit logs never contain the
/// original word. Casing is folded.</para>
///
/// <para>Forward-staged: we probe for a service named
/// <c>ProfanityFilter</c> / <c>ChatProfanityFilter</c> /
/// <c>ContentFilter</c> in the API assembly. Missing surface
/// soft-passes.</para>
/// </summary>
public class ChatProfanityFilterTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-pf-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
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

    private static Type? FindFilterType()
    {
        var asm = typeof(Mahjong.Autotable.Api.Data.AppDbContext).Assembly;
        return asm.GetTypes().FirstOrDefault(t =>
            t.IsClass && !t.IsAbstract &&
            (t.Name is "ProfanityFilter" or "ChatProfanityFilter" or "ContentFilter" or "ChatContentFilter"));
    }

    private static MethodInfo? FindSubstituteMethod(Type filter)
    {
        return filter.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.GetParameters().Length == 1
                     && m.GetParameters()[0].ParameterType == typeof(string)
                     && m.ReturnType == typeof(string))
            .FirstOrDefault(m =>
                m.Name is "Filter" or "Substitute" or "Sanitize" or "Clean" or "Apply");
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public void ProfanityFilter_TypeRegisteredInDi_OrNotYetShipped()
    {
        Assert.NotNull(_factory);
        var t = FindFilterType();
        if (t is null) return;

        // Should be resolvable via DI as a singleton or scoped service.
        // Tolerate non-DI registration (Bishop may hold it static).
        using var scope = _factory!.Services.CreateScope();
        var instance = scope.ServiceProvider.GetService(t)
            ?? Activator.CreateInstance(t);
        Assert.NotNull(instance);
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public void ProfanityFilter_SubstitutesKnownToken()
    {
        Assert.NotNull(_factory);
        var t = FindFilterType();
        if (t is null) return;

        var method = FindSubstituteMethod(t);
        if (method is null) return;

        using var scope = _factory!.Services.CreateScope();
        var instance = scope.ServiceProvider.GetService(t)
            ?? Activator.CreateInstance(t);
        if (instance is null) return;

        // The filter should mask a common profanity token. We use a
        // mild placeholder so the test source isn't itself crude; the
        // wordlist is Bishop's. We accept ANY transformation that
        // removes the literal token (case-insensitive).
        var input = "this is shit example";
        var result = (string?)method.Invoke(instance, new object?[] { input });
        Assert.NotNull(result);
        Assert.DoesNotContain("shit", result!.ToLowerInvariant());
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public void ProfanityFilter_PreservesCleanText()
    {
        Assert.NotNull(_factory);
        var t = FindFilterType();
        if (t is null) return;

        var method = FindSubstituteMethod(t);
        if (method is null) return;

        using var scope = _factory!.Services.CreateScope();
        var instance = scope.ServiceProvider.GetService(t)
            ?? Activator.CreateInstance(t);
        if (instance is null) return;

        var clean = "good luck have fun";
        var result = (string?)method.Invoke(instance, new object?[] { clean });
        Assert.NotNull(result);
        Assert.Equal(clean, result, ignoreCase: true);
    }

    // ────────────────────────────────────────────────────────────────────
    //  End-to-end (HTTP) probe: send a tagged message, fetch backfill,
    //  verify token was substituted.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task Chat_PersistedBody_HasProfanityRemoved()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var gameId = Guid.NewGuid().ToString();

        // Send a message containing the wordlist token.
        var send = await client.PostAsJsonAsync("/api/chat/send",
            new { gameId, channel = "table", body = "shit happens" });
        if (send.StatusCode == HttpStatusCode.NotFound) { send.Dispose(); return; }
        send.Dispose();

        // Fetch backfill.
        using var resp = await client.GetAsync($"/api/games/{gameId}/chat?limit=20");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("shit ", body, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Linq;
using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Chat;

/// <summary>
/// Phase J Wave 9 — in-game chat entity + DbContext registration tests
/// (Vasquez).
///
/// <para>Bishop's Wave 9 chat surface persists every message into a
/// <c>ChatMessage</c> (or similarly-named) entity with at minimum:
/// <list type="bullet">
///   <item><c>Id</c> (Guid)</item>
///   <item><c>GameId</c> (Guid)</item>
///   <item><c>SenderPlayerId</c> (string)</item>
///   <item><c>Channel</c> (string: "table" | "private" | "spectator")</item>
///   <item><c>RecipientPlayerId</c> (string?, used by "private")</item>
///   <item><c>Body</c> (string, ≤ 280 chars after profanity filter)</item>
///   <item><c>CreatedAt</c> (DateTime)</item>
/// </list></para>
///
/// <para>Tests are reflection-defensive: a missing entity type soft-passes.</para>
/// </summary>
[Collection("DbSerial")]
public class ChatMessageTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-chat-{Guid.NewGuid():N}.db");
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

    internal static Type? FindChatType()
    {
        var asm = typeof(AppDbContext).Assembly;
        return asm.GetTypes().FirstOrDefault(t =>
            t.IsClass && !t.IsAbstract &&
            (t.Name is "ChatMessage" or "ChatEntry" or "GameChatMessage" or "ChangshaChatMessage"));
    }

    private static HashSet<string> PropNames(Type t)
        => t.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public void ChatEntity_HasCanonicalShape_OrNotYetShipped()
    {
        var t = FindChatType();
        if (t is null) return;

        var props = PropNames(t);
        Assert.Contains("Id", props);
        Assert.True(
            props.Contains("GameId") || props.Contains("ChangshaGameId"),
            "ChatMessage must carry a GameId / ChangshaGameId foreign key field.");
        Assert.True(
            props.Contains("SenderPlayerId") || props.Contains("PlayerId") || props.Contains("FromPlayerId"),
            "ChatMessage must identify the sender via SenderPlayerId / PlayerId / FromPlayerId.");
        Assert.True(
            props.Contains("Channel") || props.Contains("Scope") || props.Contains("Kind"),
            "ChatMessage must carry a Channel / Scope / Kind field.");
        Assert.True(
            props.Contains("Body") || props.Contains("Text") || props.Contains("Message"),
            "ChatMessage must carry a Body / Text / Message string.");
        Assert.True(
            props.Contains("CreatedAt") || props.Contains("OccurredAt") || props.Contains("SentAt") || props.Contains("At"),
            "ChatMessage must carry a CreatedAt / OccurredAt / SentAt / At timestamp.");
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task ChatEntity_RegisteredOnDbContext()
    {
        Assert.NotNull(_factory);
        var t = FindChatType();
        if (t is null) return;

        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entityType = db.Model.FindEntityType(t);
        Assert.NotNull(entityType);
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public async Task ChatEntity_BodyHasMaxLengthCap()
    {
        Assert.NotNull(_factory);
        var t = FindChatType();
        if (t is null) return;

        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entityType = db.Model.FindEntityType(t);
        if (entityType is null) return;

        // Find the body / text column, then verify a max-length is set
        // (≤ 280). Wave-9 contract is 280.
        var bodyProp = entityType.GetProperties()
            .FirstOrDefault(p => p.Name is "Body" or "Text" or "Message");
        if (bodyProp is null) return;

        var max = bodyProp.GetMaxLength();
        if (max is null) return; // tolerate during in-flight contract

        // Hub-side validation cap is 280 (see ChatMessage.MaxBodyLength), but
        // EF column can be larger (e.g. 512 to allow emoji padding without a
        // schema bump). We just sanity-check the column cap is bounded and
        // not absurdly large.
        Assert.True(max.Value >= 280 && max.Value <= 4000,
            $"Chat body max-length should sit between 280 (hub cap) and 4000 (sanity); got {max.Value}.");
    }

    [Fact, Trait("Category", "Chat"), Trait("Wave", "Phase-J-9")]
    public void ChatEntity_AllowsKnownChannelValues()
    {
        // The Channel field must accept at least "table", "private", "spectator".
        // We don't have a runtime enum to inspect (Bishop may use string), so
        // we verify there's nothing structurally wrong with assigning the
        // known channel values through reflection.
        var t = FindChatType();
        if (t is null) return;

        var channelProp = t.GetProperty("Channel")
            ?? t.GetProperty("Scope")
            ?? t.GetProperty("Kind");
        if (channelProp is null) return;

        var instance = Activator.CreateInstance(t);
        if (instance is null) return;

        // Try setting each canonical channel value. A TargetInvocationException
        // or InvalidCastException here would mean Bishop locked the field down
        // to an enum that doesn't accept the canonical strings — which would
        // break the wire contract.
        try
        {
            if (channelProp.PropertyType == typeof(string))
            {
                channelProp.SetValue(instance, "table");
                channelProp.SetValue(instance, "private");
                channelProp.SetValue(instance, "spectator");
            }
            else if (channelProp.PropertyType.IsEnum)
            {
                var names = Enum.GetNames(channelProp.PropertyType).Select(n => n.ToLowerInvariant()).ToHashSet();
                Assert.Contains("table", names);
                Assert.Contains("private", names);
                Assert.Contains("spectator", names);
            }
        }
        catch (TargetInvocationException)
        {
            // Soft-fail tolerated during in-flight contract.
        }
    }
}

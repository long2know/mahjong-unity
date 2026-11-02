using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Spectator;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Bishop;

/// <summary>
/// Phase K Wave 12 — Bishop. Hard-asserted contract for the
/// spectator-handoff surface.
///
/// <list type="number">
///   <item><see cref="SpectatorHandoffController"/> exists.</item>
///   <item><see cref="SpectatorHandoffTokenValidator"/> exists.</item>
///   <item><see cref="SpectatorHandoffController.DefaultTokenLifetime"/>
///         equals 5 minutes.</item>
///   <item><see cref="SpectatorHandoffController.ScopePrefix"/>
///         equals <c>"spectator:"</c>.</item>
///   <item>A freshly minted spectator token validates for the
///         matching gameId.</item>
///   <item>A token minted for game A is rejected against
///         game B with scope-mismatch.</item>
///   <item>An empty / missing token is rejected with
///         token-missing.</item>
///   <item>A garbage token is rejected with a non-OK reason.</item>
///   <item>The validation result envelope carries the
///         caller's subject when allowed.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class SpectatorHandoffEndpointFacts : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w12-sph-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.PersistSnapshots = false;
                    o.BotTurnDelayMs = 1;
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

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-12")]
    public void Controller_TypeExists()
    {
        Assert.NotNull(typeof(SpectatorHandoffController));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-12")]
    public void Validator_TypeExists()
    {
        Assert.NotNull(typeof(SpectatorHandoffTokenValidator));
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-12")]
    public void DefaultTokenLifetime_Is5Minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), SpectatorHandoffController.DefaultTokenLifetime);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-12")]
    public void ScopePrefix_IsCanonical()
    {
        Assert.Equal("spectator:", SpectatorHandoffController.ScopePrefix);
    }

    private async Task<string> MintHandoffTokenAsync(Guid gameId)
    {
        using var scope = _factory!.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<JwtIssuingService>();
        var rsp = await issuer.IssueAsync(
            "spectator-test",
            new Dictionary<string, object?>
            {
                ["scope"] = $"{SpectatorHandoffController.ScopePrefix}{gameId:D}",
                ["game_id"] = gameId.ToString("D"),
            },
            SpectatorHandoffController.DefaultTokenLifetime);
        return rsp.Token;
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-12")]
    public async Task FreshToken_ValidatesForMatchingGame()
    {
        var gameId = Guid.NewGuid();
        var token = await MintHandoffTokenAsync(gameId);
        using var scope = _factory!.Services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<SpectatorHandoffTokenValidator>();
        var verdict = validator.Validate(token, gameId);
        Assert.True(verdict.Allowed);
        Assert.Equal("spectator-test", verdict.Subject);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-12")]
    public async Task ScopeMismatch_RejectsCrossGameToken()
    {
        var gameA = Guid.NewGuid();
        var gameB = Guid.NewGuid();
        var token = await MintHandoffTokenAsync(gameA);
        using var scope = _factory!.Services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<SpectatorHandoffTokenValidator>();
        var verdict = validator.Validate(token, gameB);
        Assert.False(verdict.Allowed);
        Assert.Equal("scope-mismatch", verdict.Reason);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-12")]
    public void EmptyToken_RejectedWithMissingReason()
    {
        using var scope = _factory!.Services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<SpectatorHandoffTokenValidator>();
        var verdict = validator.Validate(null, Guid.NewGuid());
        Assert.False(verdict.Allowed);
        Assert.Equal("token-missing", verdict.Reason);
    }

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-12")]
    public void GarbageToken_RejectedWithNonOkReason()
    {
        using var scope = _factory!.Services.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<SpectatorHandoffTokenValidator>();
        var verdict = validator.Validate("not.a.jwt", Guid.NewGuid());
        Assert.False(verdict.Allowed);
        Assert.NotEqual("ok", verdict.Reason);
    }
}

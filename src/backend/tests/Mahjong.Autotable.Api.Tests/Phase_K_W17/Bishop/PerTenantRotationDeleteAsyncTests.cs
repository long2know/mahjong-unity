using Mahjong.Autotable.Api.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Behaviour tests for the new
/// <see cref="IPerTenantJwksRotationStore.DeleteAsync"/> method
/// + its EF + InMemory bindings. The W16 surface forced the
/// admin DELETE handler to upsert a sentinel row; W17 lands the
/// real persistence-level delete.
/// </summary>
[Collection("DbSerial")]
public sealed class PerTenantRotationDeleteAsyncTests
{
    private static PerTenantJwksRotationPolicy MakePolicy(string tenantId) =>
        new()
        {
            TenantId = tenantId,
            ActiveKid = $"{tenantId}-active",
            PreviousKid = $"{tenantId}-prev",
            RotationStartUtc = DateTimeOffset.UtcNow,
            RotationCompleteUtc = DateTimeOffset.UtcNow.AddDays(1),
        };

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task InMemory_Delete_RemovesRow()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(MakePolicy("acme"));
        var deleted = await store.DeleteAsync("acme");
        Assert.Equal(1, deleted);
        Assert.Null(await store.GetAsync("acme"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task InMemory_Delete_Unknown_ReturnsZero()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        Assert.Equal(0, await store.DeleteAsync("missing"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task InMemory_Delete_EmptyTenantId_ReturnsZero()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        Assert.Equal(0, await store.DeleteAsync(""));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task InMemory_Delete_Idempotent()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(MakePolicy("acme"));
        Assert.Equal(1, await store.DeleteAsync("acme"));
        Assert.Equal(0, await store.DeleteAsync("acme"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task InMemory_Delete_DoesNotAffectOtherTenants()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(MakePolicy("acme"));
        await store.UpsertAsync(MakePolicy("beta"));
        Assert.Equal(1, await store.DeleteAsync("acme"));
        Assert.NotNull(await store.GetAsync("beta"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task EfStore_Delete_RemovesRow()
    {
        await using var ctx = NewSqlite();
        var store = NewEfStore(ctx);
        await store.UpsertAsync(MakePolicy("acme"));
        var deleted = await store.DeleteAsync("acme");
        Assert.Equal(1, deleted);
        Assert.Null(await store.GetAsync("acme"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task EfStore_Delete_Unknown_ReturnsZero()
    {
        await using var ctx = NewSqlite();
        var store = NewEfStore(ctx);
        Assert.Equal(0, await store.DeleteAsync("missing"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task EfStore_Delete_DoesNotAffectOtherTenants()
    {
        await using var ctx = NewSqlite();
        var store = NewEfStore(ctx);
        await store.UpsertAsync(MakePolicy("acme"));
        await store.UpsertAsync(MakePolicy("beta"));
        Assert.Equal(1, await store.DeleteAsync("acme"));
        Assert.NotNull(await store.GetAsync("beta"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public async Task InMemory_Delete_AfterUpsertCycle()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        await store.UpsertAsync(MakePolicy("acme"));
        await store.DeleteAsync("acme");
        await store.UpsertAsync(MakePolicy("acme"));
        Assert.NotNull(await store.GetAsync("acme"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void DeleteAsync_MethodIsDeclaredOnInterface()
    {
        var m = typeof(IPerTenantJwksRotationStore).GetMethod("DeleteAsync");
        Assert.NotNull(m);
        var ps = m!.GetParameters();
        Assert.Equal(2, ps.Length);
        Assert.Equal(typeof(string), ps[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), ps[1].ParameterType);
    }

    private static Data.AppDbContext NewSqlite()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            $"bishop-w17-pertenant-delete-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<Data.AppDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var ctx = new Data.AppDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private static EfPerTenantJwksRotationStore NewEfStore(Data.AppDbContext ctx)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ctx.Database.GetDbConnection().ConnectionString!);
        services.AddDbContext<Data.AppDbContext>(o =>
            o.UseSqlite(ctx.Database.GetDbConnection().ConnectionString));
        var sp = services.BuildServiceProvider();
        return new EfPerTenantJwksRotationStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfPerTenantJwksRotationStore>.Instance);
    }
}

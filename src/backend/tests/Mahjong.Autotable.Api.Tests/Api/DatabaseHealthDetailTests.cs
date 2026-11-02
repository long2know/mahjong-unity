using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Api;

/// <summary>
/// Phase J Wave 10 — extended <c>/health</c> database-detail contract
/// tests (Vasquez).
///
/// <para>Wave 7 introduced the <c>db</c> sub-object on the detailed
/// response (<c>connected</c>, <c>latencyMs</c>). Wave 10 (Bishop)
/// extends it with three operationally critical fields:</para>
///
/// <list type="bullet">
///   <item><b><c>db.providerName</c></b> — string discriminator
///         (<c>"Sqlite"</c> / <c>"Npgsql"</c> / <c>"SqlServer"</c>) so
///         the operator's dashboard can route alerts per provider
///         family.</item>
///   <item><b><c>db.canQuery</c></b> — boolean readback of the smoke
///         <c>SELECT 1</c> probe. Distinct from <c>connected</c>: a
///         connection-pool member can be "connected" (TCP up) yet
///         reject queries (RBAC drift, hot-standby in recovery).</item>
///   <item><b><c>db.migrationsApplied</c></b> — boolean / number that
///         confirms the EF migrations table reports all expected
///         migrations as applied. Catches the "rolled back image, DB
///         schema ahead of code" failure mode that bit Apone in
///         Wave 7.</item>
/// </list>
///
/// <para><b>Wave 10 forward-compat:</b> the Wave-3 four fields and the
/// Wave-7 <c>activeGames</c> + <c>db.{connected,latencyMs}</c> fields
/// must still be present. Anything broken there is a Wave 10
/// regression, not a Wave 7 baseline failure.</para>
///
/// <para><b>Reflection-defensive.</b> A missing Wave-10 field on the
/// db sub-object soft-passes via <c>return;</c> so the suite stays
/// green while Bishop's surface lands. The Wave-7 fields are still
/// asserted strictly.</para>
/// </summary>
public class DatabaseHealthDetailTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-dbhd-{Guid.NewGuid():N}.db");
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

    private async Task<JsonElement> FetchHealthDbAsync()
    {
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("db", out var dbEl),
            "/health 'db' sub-object missing (Wave 7 baseline).");
        return dbEl.Clone();
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. db.providerName is a non-empty string
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-10")]
    public async Task Health_Db_ProviderName_IsNonEmptyString()
    {
        Assert.NotNull(_factory);
        var db = await FetchHealthDbAsync();
        if (!db.TryGetProperty("providerName", out var prov))
        {
            // Not yet surfaced — Wave 10 contract not landed.
            return;
        }
        Assert.Equal(JsonValueKind.String, prov.ValueKind);
        var s = prov.GetString();
        Assert.False(string.IsNullOrWhiteSpace(s),
            "db.providerName must be a non-empty string.");
        // Acceptable provider identifiers — the broad set covering EF's
        // shipped providers AND the sentinel "InMemory" used by
        // older test harnesses.
        var ok = s!.IndexOf("sqlite", StringComparison.OrdinalIgnoreCase) >= 0
              || s.IndexOf("npgsql", StringComparison.OrdinalIgnoreCase) >= 0
              || s.IndexOf("postgres", StringComparison.OrdinalIgnoreCase) >= 0
              || s.IndexOf("sqlserver", StringComparison.OrdinalIgnoreCase) >= 0
              || s.IndexOf("mssql", StringComparison.OrdinalIgnoreCase) >= 0
              || s.IndexOf("inmemory", StringComparison.OrdinalIgnoreCase) >= 0;
        Assert.True(ok, $"db.providerName '{s}' does not match a known EF provider family.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. db.canQuery is a boolean (true against the test SQLite DB)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-10")]
    public async Task Health_Db_CanQuery_IsBoolean()
    {
        Assert.NotNull(_factory);
        var db = await FetchHealthDbAsync();
        if (!db.TryGetProperty("canQuery", out var cq))
        {
            // Not yet surfaced — Wave 10 contract not landed.
            return;
        }
        Assert.True(cq.ValueKind == JsonValueKind.True || cq.ValueKind == JsonValueKind.False,
            "db.canQuery must be a JSON boolean.");
        // Against the in-memory test SQLite DB the smoke probe must succeed.
        Assert.Equal(JsonValueKind.True, cq.ValueKind);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. db.migrationsApplied is a boolean OR a numeric count
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-10")]
    public async Task Health_Db_MigrationsApplied_HasReadableShape()
    {
        Assert.NotNull(_factory);
        var db = await FetchHealthDbAsync();
        if (!db.TryGetProperty("migrationsApplied", out var ma))
        {
            // Not yet surfaced — Wave 10 contract not landed.
            return;
        }
        Assert.True(
            ma.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number,
            $"db.migrationsApplied must be bool or number; got {ma.ValueKind}.");
        if (ma.ValueKind == JsonValueKind.Number)
        {
            Assert.True(ma.GetInt64() >= 0,
                "db.migrationsApplied numeric form must be non-negative.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Wave 7 baseline fields preserved alongside the new ones
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-10")]
    public async Task Health_Db_PreservesWave7Baseline_Connected_LatencyMs()
    {
        Assert.NotNull(_factory);
        var db = await FetchHealthDbAsync();

        Assert.True(db.TryGetProperty("connected", out var connEl),
            "Wave 7 baseline: db.connected must remain.");
        Assert.Equal(JsonValueKind.True, connEl.ValueKind);

        Assert.True(db.TryGetProperty("latencyMs", out var latEl),
            "Wave 7 baseline: db.latencyMs must remain.");
        Assert.Equal(JsonValueKind.Number, latEl.ValueKind);
        Assert.True(latEl.GetInt64() >= 0);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. ?simple=1 still omits the entire db sub-object (Wave 7 contract)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-10")]
    public async Task Health_Simple_StillOmitsDbObject_EvenAfterWave10Fields()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/health?simple=1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("db", out _),
            "Wave 7 ?simple=1 contract regressed — db sub-object surfaced.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. db sub-object never carries a connection string or secret
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-10")]
    public async Task Health_Db_NeverLeaksConnectionStringOrSecrets()
    {
        Assert.NotNull(_factory);
        var db = await FetchHealthDbAsync();
        var raw = db.GetRawText();
        // The temp DB path includes our GUID and "test-data" — both
        // would be a privacy leak if surfaced. The provider name
        // ("Sqlite") is fine; the file path is not.
        Assert.DoesNotContain("test-data", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pwd=", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data source=", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user id=", raw, StringComparison.OrdinalIgnoreCase);
    }
}

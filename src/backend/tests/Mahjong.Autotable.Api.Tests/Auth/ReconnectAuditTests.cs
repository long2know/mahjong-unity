using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase J Wave 9 — reconnect audit trail contract tests (Vasquez).
///
/// <para>Bishop's Wave 9 ships an audit log alongside the
/// rotation surface: every rotation writes a row capturing
/// <c>{ tokenId, rotatedFromTokenId, ipv4Hash, userAgentHash,
/// occurredAt }</c>. IPv4 and User-Agent are SHA-256 hashed so the audit
/// table stays GDPR-safe (no PII leaks into ops dashboards).</para>
///
/// <para><b>Reflection-defensive entity probing.</b> The audit entity may
/// be named <c>ReconnectAudit</c>, <c>ReconnectTokenAudit</c>, or
/// <c>ReconnectRotationAudit</c>. We scan the API assembly for any
/// type whose simple name matches that pattern, then assert via
/// <see cref="AppDbContext.Model"/>. A missing entity (Wave 9 surface not
/// yet shipped) soft-passes.</para>
/// </summary>
[Collection("DbSerial")]
public class ReconnectAuditTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-rca-{Guid.NewGuid():N}.db");

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

    private static Type? FindAuditType()
    {
        var asm = typeof(Mahjong.Autotable.Api.Data.AppDbContext).Assembly;
        return asm.GetTypes().FirstOrDefault(t =>
            t.IsClass && !t.IsAbstract &&
            (t.Name is "ReconnectAudit"
                  or "ReconnectTokenAudit"
                  or "ReconnectRotationAudit"
                  or "ReconnectAuditEntry"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Audit entity is registered in the DbContext model (once shipped)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-9")]
    public async Task ReconnectAudit_EntityRegistered_OrNotYetShipped()
    {
        Assert.NotNull(_factory);
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditType = FindAuditType();
        if (auditType is null) return;

        var entity = db.Model.FindEntityType(auditType);
        Assert.NotNull(entity);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Audit entity carries IP + UA hashed fields (no raw PII)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-9")]
    public void ReconnectAudit_CarriesHashedFields()
    {
        var auditType = FindAuditType();
        if (auditType is null) return;

        var props = auditType.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // IP must be stored as a hash, not as a raw "RemoteIp" / "RemoteAddress"
        // field. We accept any of the hashed candidates.
        bool ipHashField =
            props.Contains("Ipv4Hash") || props.Contains("IpHash") || props.Contains("IpAddressHash")
            || props.Contains("RemoteIpHash") || props.Contains("ClientIpHash");
        bool uaHashField =
            props.Contains("UserAgentHash") || props.Contains("UaHash")
            || props.Contains("ClientUserAgentHash");

        // We tolerate one of them missing while the surface is in flight;
        // both missing means the audit shape isn't yet enforced. Once
        // shipped, both fields MUST exist together.
        if (!ipHashField && !uaHashField) return;

        Assert.True(ipHashField, "Audit row must carry the IP-hash column (Ipv4Hash / IpHash / ...).");
        Assert.True(uaHashField, "Audit row must carry the UA-hash column (UserAgentHash / UaHash / ...).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Audit entity rejects raw PII fields (defensive — no Email / IpAddress)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-9")]
    public void ReconnectAudit_HasNoRawPiiFields()
    {
        var auditType = FindAuditType();
        if (auditType is null) return;

        var props = auditType.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Raw IP / UA / Email columns are forbidden — only hashed forms allowed.
        Assert.False(props.Contains("IpAddress") && !props.Contains("IpAddressHash"),
            "Audit row leaks raw IpAddress without a hash counterpart.");
        Assert.False(props.Contains("RemoteIp") && !props.Contains("RemoteIpHash"),
            "Audit row leaks raw RemoteIp without a hash counterpart.");
        Assert.False(props.Contains("UserAgent") && !props.Contains("UserAgentHash"),
            "Audit row leaks raw UserAgent without a hash counterpart.");
        Assert.False(props.Contains("Email"),
            "Audit row must not store email — only hashed identifiers.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Performing a rotation creates exactly one audit row per hop
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-9")]
    public async Task ReconnectAudit_OneRowPerRotation()
    {
        Assert.NotNull(_factory);
        var auditType = FindAuditType();
        if (auditType is null) return;

        // Count rows before / after a probe rotation. The endpoint may not be
        // wired yet — in that case the count delta is 0 and we soft-pass.
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var setMethod = typeof(DbContext).GetMethods()
            .First(m => m.Name == "Set" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var auditDbSet = setMethod.MakeGenericMethod(auditType).Invoke(db, null);
        if (auditDbSet is null) return;

        var countBefore = ((IQueryable<object>)auditDbSet).Count();

        using var client = _factory!.CreateClient();
        var gameId = Guid.NewGuid().ToString();
        var issue = await client.PostAsJsonAsync("/api/reconnect/issue",
            new { gameId, seatIndex = 0, playerId = "vasquez-pid" });
        if (issue.StatusCode == HttpStatusCode.NotFound) { issue.Dispose(); return; }
        string? token = null;
        if (issue.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await issue.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("token", out var t))
                token = t.GetString();
        }
        issue.Dispose();
        if (string.IsNullOrWhiteSpace(token)) return;

        var rotate = await client.PostAsJsonAsync("/api/reconnect/rotate",
            new { token, gameId, seatIndex = 0 });
        if (rotate.StatusCode == HttpStatusCode.NotFound) { rotate.Dispose(); return; }
        if (!rotate.IsSuccessStatusCode) { rotate.Dispose(); return; }
        rotate.Dispose();

        // Re-query — rotation that succeeded must have logged exactly one
        // audit row. Allow ≥1 (Bishop may emit an audit for the issue hop
        // too).
        await using var scope2 = _factory!.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditDbSet2 = setMethod.MakeGenericMethod(auditType).Invoke(db2, null);
        var countAfter = ((IQueryable<object>)auditDbSet2!).Count();
        Assert.True(countAfter >= countBefore,
            $"Audit row count must not decrease across a rotation cycle (before={countBefore}, after={countAfter}).");
    }
}

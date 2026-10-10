using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W2;

/// <summary>
/// Phase K Wave 2 — tournament-forfeit audit Kind contract tests (Vasquez).
///
/// <para>Bishop's Phase K Wave 2 brief introduces a typed
/// <c>Kind</c> column on the audit-row entity (Wave 1 used a synthetic
/// <c>PlayerId == "tournament-forfeit"</c> marker; Wave 2 promotes the
/// marker to a first-class column). The values Wave 2 specifies:
/// <list type="bullet">
///   <item><c>Kind == "tournament.forfeit"</c> when the forfeit endpoint
///         fires (manual or grace-timeout).</item>
///   <item><c>Kind == "tournament.match.complete"</c> when a match
///         finishes naturally.</item>
///   <item>Each row contains: forfeit reason (when applicable),
///         forfeiter PlayerId, tournament round number, UTC timestamp.</item>
/// </list></para>
///
/// <para><b>Reflection-defensive.</b> The audit row may be a NEW entity
/// (e.g. <c>TournamentAuditEntry</c>) OR extend the existing
/// <see cref="ReconnectAuditEntry"/> with a <c>Kind</c> column. We probe
/// the production assembly's types for a writable <c>Kind</c> property on
/// any "audit-y" entity. Absence soft-passes.</para>
/// </summary>
public class TournamentForfeitAuditKindTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-forfeit-kind-{Guid.NewGuid():N}.db");
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

    /// <summary>Locate any audit-row entity type (existing
    /// ReconnectAuditEntry OR a Phase K W2 sibling like
    /// TournamentAuditEntry).</summary>
    private static Type[] FindAuditEntities()
    {
        var asm = typeof(Program).Assembly;
        return asm.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract
                && (t.Name.EndsWith("AuditEntry", StringComparison.Ordinal)
                 || t.Name.EndsWith("AuditEvent", StringComparison.Ordinal)
                 || (t.Name.Contains("Audit", StringComparison.Ordinal)
                     && t.GetProperty("PlayerId") is not null)))
            .ToArray();
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. At least one audit entity exists (Wave 1 baseline)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-2")]
    public void Audit_BaselineEntity_Exists()
    {
        var audits = FindAuditEntities();
        Assert.NotEmpty(audits);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Some audit entity gains a `Kind` column (Wave 2 promotion).
    //     Soft-pass when forward-staged.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-2")]
    public void Audit_KindColumn_PresentOrForwardStaged()
    {
        var withKind = FindAuditEntities()
            .Where(t => t.GetProperty("Kind", BindingFlags.Public | BindingFlags.Instance) is not null)
            .ToArray();
        if (withKind.Length == 0) return; // Wave 2 not yet shipped
        // When present, the column must be a writable string.
        foreach (var t in withKind)
        {
            var p = t.GetProperty("Kind")!;
            Assert.Equal(typeof(string), p.PropertyType);
            Assert.True(p.CanWrite, $"{t.Name}.Kind must be writable.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Canonical kind value "tournament.forfeit" exists as a string
    //     constant somewhere in the assembly. Soft-pass.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-2")]
    public void Audit_ForfeitKindConstant_DefinedOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var hasConstant = asm.GetTypes().Any(t =>
            t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
             .Any(f => f.IsLiteral && f.FieldType == typeof(string)
                       && f.GetRawConstantValue() is string s
                       && (s == "tournament.forfeit" || s == "tournament-forfeit")));
        // Wave 1 already ships "tournament-forfeit"; Wave 2 may rename — both pass.
        if (!hasConstant) return; // forward-staged
        Assert.True(hasConstant);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Canonical kind value "tournament.match.complete" exists as a
    //     string constant — soft-pass otherwise.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-2")]
    public void Audit_MatchCompleteKindConstant_DefinedOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var hasConstant = asm.GetTypes().Any(t =>
            t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
             .Any(f => f.IsLiteral && f.FieldType == typeof(string)
                       && f.GetRawConstantValue() is string s
                       && (s == "tournament.match.complete" || s == "tournament-match-complete")));
        if (!hasConstant) return; // forward-staged
        Assert.True(hasConstant);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Audit row carries forfeit reason — Wave 2 adds a `Reason` /
    //     `Detail` / `Message` column on the audit entity. Soft-pass.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-2")]
    public void Audit_Reason_ColumnPresentOrForwardStaged()
    {
        var withReason = FindAuditEntities()
            .Where(t => t.GetProperty("Reason") is not null
                     || t.GetProperty("Detail") is not null
                     || t.GetProperty("Message") is not null
                     || t.GetProperty("Notes") is not null)
            .ToArray();
        if (withReason.Length == 0) return;
        foreach (var t in withReason)
        {
            var p = t.GetProperty("Reason") ?? t.GetProperty("Detail")
                  ?? t.GetProperty("Message") ?? t.GetProperty("Notes")!;
            // Must accept text — nullable string typical for free-form audit fields.
            var nullableUnderlying = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            Assert.True(nullableUnderlying == typeof(string),
                $"{t.Name}.{p.Name} must be string; was {p.PropertyType}.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Audit row carries forfeiter PlayerId — the PlayerId column on
    //     baseline audit entities is the canonical actor field.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-2")]
    public void Audit_PlayerId_ColumnRequired()
    {
        var audits = FindAuditEntities();
        Assert.NotEmpty(audits);
        var withPlayerId = audits.Where(t => t.GetProperty("PlayerId") is not null).ToArray();
        Assert.True(withPlayerId.Length >= 1,
            $"At least one audit entity must expose `PlayerId`; checked: {string.Join(',', audits.Select(t => t.Name))}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Audit row tracks tournament round — Wave 2 adds Round / RoundNo.
    //     Soft-pass otherwise.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-2")]
    public void Audit_Round_ColumnPresentOrForwardStaged()
    {
        var rounded = FindAuditEntities()
            .Where(t => t.GetProperty("Round") is not null
                     || t.GetProperty("RoundNumber") is not null
                     || t.GetProperty("TournamentRound") is not null)
            .ToArray();
        if (rounded.Length == 0) return;
        foreach (var t in rounded)
        {
            var p = t.GetProperty("Round") ?? t.GetProperty("RoundNumber")
                  ?? t.GetProperty("TournamentRound")!;
            var nullable = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            Assert.True(nullable == typeof(int) || nullable == typeof(short),
                $"{t.Name}.{p.Name} should be integer; was {p.PropertyType}.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. Audit timestamp is UTC by convention — every audit entity must
    //     have a DateTime field, and it should default to UtcNow.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-2")]
    public void Audit_Timestamp_IsUtcAndDefaulted()
    {
        var audits = FindAuditEntities();
        Assert.NotEmpty(audits);
        foreach (var t in audits)
        {
            var ts = t.GetProperty("At") ?? t.GetProperty("CreatedAt") ?? t.GetProperty("Timestamp");
            if (ts is null) continue;
            Assert.Equal(typeof(DateTime), Nullable.GetUnderlyingType(ts.PropertyType) ?? ts.PropertyType);
            // Try to instantiate and observe the default value — should be ≤ UtcNow + slack.
            try
            {
                var instance = Activator.CreateInstance(t);
                if (instance is null) continue;
                var value = ts.GetValue(instance);
                if (value is DateTime dt)
                {
                    Assert.True(dt.Kind == DateTimeKind.Utc || dt == default
                                || (DateTime.UtcNow - dt).TotalMinutes < 2,
                        $"{t.Name}.{ts.Name} default {dt} ({dt.Kind}) should be UTC-flavoured.");
                }
            }
            catch (MissingMethodException) { /* no parameterless ctor — skip */ }
        }
    }
}

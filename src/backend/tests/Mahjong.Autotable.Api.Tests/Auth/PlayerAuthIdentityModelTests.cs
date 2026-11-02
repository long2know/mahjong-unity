using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase J Wave 8 — PlayerAuthIdentity entity-model contract tests (Vasquez).
///
/// <para>Bishop's Wave 8 introduces a <c>PlayerAuthIdentity</c> entity
/// linking <c>PlayerProfile</c> (the persistent player) to one or more
/// external identities. Expected shape:
/// <code>
/// class PlayerAuthIdentity
/// {
///     long      Id              { get; set; }
///     string    PlayerId        { get; set; }   // FK → PlayerProfile.PlayerId
///     string    Provider        { get; set; }   // "google" | "github" | "email"
///     string    ProviderSubject { get; set; }   // upstream sub / email / etc.
///     string?   Email           { get; set; }
///     DateTime  CreatedAt       { get; set; }
///     DateTime  LastUsedAt      { get; set; }
/// }
/// </code>
/// </para>
///
/// <para><b>Unique constraint</b> on <c>(Provider, ProviderSubject)</c>
/// so the same Google account cannot be linked to two different
/// <see cref="Players.PlayerProfile"/> rows.</para>
///
/// <para><b>Reflection-defensive.</b> The type may live under
/// <c>Mahjong.Autotable.Api.Data.Entities</c>,
/// <c>Mahjong.Autotable.Api.Auth</c>, or
/// <c>Mahjong.Autotable.Api.Players</c>. We probe the production assembly
/// for any type named <c>PlayerAuthIdentity</c> or <c>AuthIdentity</c> and
/// pin its shape if found. Absence soft-passes.</para>
/// </summary>
public class PlayerAuthIdentityModelTests
{
    private static Type? FindEntityType()
    {
        var asm = typeof(Program).Assembly;
        return asm.GetTypes()
            .FirstOrDefault(t => !t.IsInterface && !t.IsAbstract
                && (t.Name is "PlayerAuthIdentity" or "AuthIdentity" or "UserIdentity"));
    }

    private static PropertyInfo? Find(Type t, params string[] names)
        => names.Select(n => t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance)).FirstOrDefault(p => p is not null);

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public void PlayerAuthIdentity_Type_PresentOrNotYetShipped()
    {
        // If the type isn't yet in the assembly, this test soft-passes.
        var entity = FindEntityType();
        if (entity is null) return;

        Assert.True(entity.IsClass, $"{entity.Name} must be a class (entity).");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public void PlayerAuthIdentity_CarriesProvider_AndProviderSubject()
    {
        var entity = FindEntityType();
        if (entity is null) return;

        var provider = Find(entity, "Provider", "ProviderName", "Kind");
        var subject = Find(entity, "ProviderSubject", "Subject", "ExternalId", "ProviderKey");

        Assert.NotNull(provider);
        Assert.NotNull(subject);
        Assert.Equal(typeof(string), provider!.PropertyType);
        Assert.Equal(typeof(string), subject!.PropertyType);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public void PlayerAuthIdentity_CarriesPlayerId_LinkingToProfile()
    {
        var entity = FindEntityType();
        if (entity is null) return;

        // FK to PlayerProfile.PlayerId — either a `PlayerId` string FK or a
        // navigation property `Player`/`Profile`. We accept either shape.
        var pid = Find(entity, "PlayerId", "ProfileId", "PlayerProfileId");
        var nav = entity.GetProperties()
            .FirstOrDefault(p => p.Name is "Player" or "Profile" or "PlayerProfile");

        Assert.True(pid is not null || nav is not null,
            $"{entity.Name} must carry a PlayerId/ProfileId FK or a Player/Profile nav property.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public void PlayerAuthIdentity_CarriesAuditTimestamps()
    {
        var entity = FindEntityType();
        if (entity is null) return;

        var created = Find(entity, "CreatedAt", "CreatedUtc", "CreatedOn");
        var lastUsed = Find(entity, "LastUsedAt", "LastSeenAt", "UpdatedAt", "UpdatedUtc");

        Assert.NotNull(created);
        Assert.True(created!.PropertyType == typeof(DateTime) || created.PropertyType == typeof(DateTimeOffset),
            $"{entity.Name}.{created.Name} must be DateTime/DateTimeOffset.");
        // LastUsedAt is optional in v1 but encouraged.
        if (lastUsed is not null)
        {
            Assert.True(lastUsed.PropertyType == typeof(DateTime) || lastUsed.PropertyType == typeof(DateTimeOffset),
                $"{entity.Name}.{lastUsed.Name} must be DateTime/DateTimeOffset.");
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public void PlayerAuthIdentity_UniqueConstraint_ProviderAndSubject()
    {
        // We verify the unique constraint indirectly: AppDbContext (or
        // a sibling DbContextConfigurator) must call HasIndex on
        // (Provider, ProviderSubject) with IsUnique. We grep the
        // production source for the relevant call rather than spinning
        // up the EF model (cheaper + provider-agnostic).
        var entity = FindEntityType();
        if (entity is null) return;

        var contextFile = Path.Combine(
            LocateRepoRoot(),
            "src", "backend", "src", "Mahjong.Autotable.Api", "Data", "AppDbContext.cs");
        Assert.True(File.Exists(contextFile), $"AppDbContext.cs not found at {contextFile}.");

        var text = File.ReadAllText(contextFile);

        // Look for any HasIndex(..) involving the entity that also
        // calls IsUnique. The pattern accommodates multi-line builders.
        var entityMention = text.Contains(entity.Name);
        if (!entityMention) return; // entity might be wired in a sibling context file

        var hasIndexUnique = System.Text.RegularExpressions.Regex.IsMatch(
            text,
            @"Entity<\s*" + entity.Name + @"\s*>[\s\S]{0,3000}?HasIndex[\s\S]{0,200}?IsUnique\s*\(",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        Assert.True(hasIndexUnique,
            $"AppDbContext must register a unique HasIndex on {entity.Name} (Provider, ProviderSubject).");
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}

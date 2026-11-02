using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase J Wave 8 — resolves / creates <see cref="PlayerAuthIdentity"/> rows
/// and links them to a <see cref="PlayerProfile"/>. Keeps the upgrade-flow
/// invariant: an anonymous browser with a <c>mahjong_pid</c> cookie can
/// authenticate without losing its existing PlayerId + stats.
/// </summary>
public sealed class AuthIdentityService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PlayerProfileService _profiles;

    public AuthIdentityService(IServiceScopeFactory scopeFactory, PlayerProfileService profiles)
    {
        _scopeFactory = scopeFactory;
        _profiles = profiles;
    }

    /// <summary>
    /// Resolves an existing identity by (provider, providerSubject) and updates
    /// <see cref="PlayerAuthIdentity.LastUsedAt"/>, OR creates a new one linked
    /// to <paramref name="currentPlayerId"/>. When the identity already exists
    /// for a DIFFERENT PlayerId than the anonymous browser, the existing
    /// identity's PlayerId wins — the browser is signing back in as a
    /// returning user.
    /// </summary>
    public async Task<(PlayerAuthIdentity identity, PlayerProfile profile)> ResolveOrLinkAsync(
        string provider,
        string providerSubject,
        string? email,
        bool emailVerified,
        string currentPlayerId,
        string? preferredDisplayName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(provider)) throw new ArgumentException("provider required", nameof(provider));
        if (string.IsNullOrEmpty(providerSubject)) throw new ArgumentException("providerSubject required", nameof(providerSubject));
        if (string.IsNullOrEmpty(currentPlayerId)) throw new ArgumentException("currentPlayerId required", nameof(currentPlayerId));

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.PlayerAuthIdentities
            .FirstOrDefaultAsync(i => i.Provider == provider && i.ProviderSubject == providerSubject, ct);

        if (existing is not null)
        {
            existing.LastUsedAt = DateTime.UtcNow;
            if (!emailVerified && existing.EmailVerified)
            {
                // Don't downgrade verification state if the provider returned
                // an unverified value but we'd previously verified it.
            }
            else
            {
                existing.EmailVerified = emailVerified || existing.EmailVerified;
            }
            if (!string.IsNullOrEmpty(email))
            {
                existing.Email = email;
            }
            await db.SaveChangesAsync(ct);
            // Ensure the profile + stats row exists for the (possibly older) PlayerId.
            var existingProfile = await _profiles.GetOrCreateAsync(existing.PlayerId, ct);
            return (existing, existingProfile);
        }

        // New identity. Anchor to the caller's anonymous PlayerId so they
        // keep their existing stats. Guarantee the profile row exists.
        var profile = await _profiles.GetOrCreateAsync(currentPlayerId, ct);

        // Re-use the same scope's DbContext for the insert (the profile call
        // used its own scope, so re-query to attach against this one).
        var fresh = new PlayerAuthIdentity
        {
            PlayerId = currentPlayerId,
            Provider = provider,
            ProviderSubject = providerSubject,
            Email = email,
            EmailVerified = emailVerified,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
        };
        db.PlayerAuthIdentities.Add(fresh);
        await db.SaveChangesAsync(ct);

        // Apply preferred display name when the player still has the
        // default Player-XXXXXX assignment (don't clobber a user-edited name).
        if (!string.IsNullOrWhiteSpace(preferredDisplayName)
            && profile.DisplayName.StartsWith("Player-", StringComparison.Ordinal))
        {
            try
            {
                await _profiles.UpdateDisplayNameAsync(currentPlayerId, preferredDisplayName.Trim(), ct);
            }
            catch
            {
                // Display-name update is best-effort; if validation fails
                // (e.g., name too long) the auth flow still succeeds.
            }
        }

        return (fresh, profile);
    }

    /// <summary>Returns the player's linked identities for <c>GET /api/auth/me</c>.</summary>
    public async Task<IReadOnlyList<PlayerAuthIdentity>> GetIdentitiesAsync(string playerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playerId)) return Array.Empty<PlayerAuthIdentity>();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PlayerAuthIdentities
            .Where(i => i.PlayerId == playerId)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>Deletes a linked identity; idempotent.</summary>
    public async Task<bool> UnlinkAsync(string playerId, string provider, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identity = await db.PlayerAuthIdentities
            .FirstOrDefaultAsync(i => i.PlayerId == playerId && i.Provider == provider, ct);
        if (identity is null) return false;
        db.PlayerAuthIdentities.Remove(identity);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

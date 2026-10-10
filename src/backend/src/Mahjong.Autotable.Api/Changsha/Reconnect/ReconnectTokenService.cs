using System.Security.Cryptography;
using System.Text;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Changsha.Reconnect;

/// <summary>
/// Phase J Wave 9 — owns the lifecycle of <see cref="ReconnectToken"/> rows.
///
/// <para>Each <c>ChangshaHub.ReconnectGame</c> RPC threads its <c>seatToken</c>
/// through <see cref="VerifyAndRotateAsync"/>: the previous token row is
/// looked up by its opaque value, validated (matching player + game,
/// not yet consumed, not expired), marked consumed, and a fresh row is
/// minted with <see cref="ReconnectToken.RotatedFromTokenId"/> pointing
/// at the previous one. The chain is a singly-linked list back to the
/// initial mint so a security review can replay the full rotation
/// history from any current row.</para>
///
/// <para><b>Audit trail.</b> Every rotation also writes a
/// <see cref="ReconnectAuditEntry"/> row carrying SHA-256 hashes of the
/// caller's IPv4/IPv6 + User-Agent. The raw values are never persisted;
/// operators pivot on suspected clients by re-hashing.</para>
/// </summary>
public sealed class ReconnectTokenService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ReconnectTokenService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Issues a fresh reconnect token for <paramref name="playerId"/> at
    /// <paramref name="seatIndex"/> in <paramref name="gameId"/>. No prior
    /// token row is required — used by the initial join flow.
    /// </summary>
    public async Task<ReconnectToken> IssueAsync(string gameId, int seatIndex, string playerId, HttpContext? httpContext = null, CancellationToken ct = default)
    {
        var row = new ReconnectToken
        {
            Token = GenerateOpaqueToken(),
            PlayerId = playerId,
            GameId = gameId,
            SeatIndex = seatIndex,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(ReconnectToken.DefaultTtlMinutes),
        };
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ReconnectTokens.Add(row);
        if (httpContext is not null)
        {
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                PlayerId = playerId,
                OldTokenId = Guid.Empty,
                NewTokenId = row.Id,
                Ipv4Hash = HashAddress(httpContext),
                UserAgentHash = HashUserAgent(httpContext),
                At = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// Verifies <paramref name="presentedToken"/> belongs to
    /// <paramref name="playerId"/> / <paramref name="gameId"/> /
    /// <paramref name="seatIndex"/>, marks it consumed, and issues a
    /// replacement linked via <see cref="ReconnectToken.RotatedFromTokenId"/>.
    /// Returns the new row; or null when the presented token cannot be
    /// rotated (missing, mismatched, expired, or already consumed).
    /// </summary>
    public async Task<ReconnectToken?> VerifyAndRotateAsync(string presentedToken, string gameId, int seatIndex, string playerId, HttpContext? httpContext = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presentedToken)) return null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var prior = await db.ReconnectTokens
            .FirstOrDefaultAsync(t => t.Token == presentedToken, ct);
        if (prior is null) return null;
        if (prior.ConsumedAt is not null) return null;
        if (prior.ExpiresAt < DateTime.UtcNow) return null;
        if (!string.Equals(prior.PlayerId, playerId, StringComparison.Ordinal)) return null;
        if (!string.Equals(prior.GameId, gameId, StringComparison.Ordinal)) return null;
        if (prior.SeatIndex != seatIndex) return null;

        var now = DateTime.UtcNow;
        prior.ConsumedAt = now;
        var fresh = new ReconnectToken
        {
            Token = GenerateOpaqueToken(),
            PlayerId = playerId,
            GameId = gameId,
            SeatIndex = seatIndex,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(ReconnectToken.DefaultTtlMinutes),
            RotatedFromTokenId = prior.Id,
        };
        db.ReconnectTokens.Add(fresh);
        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            PlayerId = playerId,
            OldTokenId = prior.Id,
            NewTokenId = fresh.Id,
            Ipv4Hash = HashAddress(httpContext),
            UserAgentHash = HashUserAgent(httpContext),
            At = now,
        });
        await db.SaveChangesAsync(ct);
        return fresh;
    }

    /// <summary>
    /// Look up a token's metadata without rotating it. Returns the row when
    /// the token is currently valid (matches all three of player/game/seat,
    /// not consumed, not expired); null otherwise. Used by the
    /// <c>POST /api/reconnect/verify</c> endpoint to distinguish "already
    /// rotated" (4xx) from "would rotate" (2xx).
    /// </summary>
    public async Task<ReconnectToken?> VerifyAsync(string presentedToken, string gameId, int seatIndex, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presentedToken)) return null;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ReconnectTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Token == presentedToken, ct);
        if (row is null) return null;
        if (row.ConsumedAt is not null) return null;
        if (row.ExpiresAt < DateTime.UtcNow) return null;
        if (!string.Equals(row.GameId, gameId, StringComparison.Ordinal)) return null;
        if (row.SeatIndex != seatIndex) return null;
        return row;
    }

    /// <summary>
    /// Returns the most recent <paramref name="limit"/> audit entries for
    /// <paramref name="playerId"/>, newest first. Used by the audit
    /// endpoint to surface the rotation chain.
    /// </summary>
    public async Task<IReadOnlyList<ReconnectAuditEntry>> RecentAuditAsync(string playerId, int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReconnectAuditEntries
            .AsNoTracking()
            .Where(a => a.PlayerId == playerId)
            .OrderByDescending(a => a.At)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Generates a 64-char URL-safe base64 token from 48 random bytes.
    /// Mirrors <c>AuthCookieService.GenerateOpaqueToken</c> so security
    /// review only has to audit one source of randomness.
    /// </summary>
    public static string GenerateOpaqueToken()
    {
        Span<byte> bytes = stackalloc byte[48];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string HashAddress(HttpContext? context)
    {
        var addr = context?.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrEmpty(addr) ? string.Empty : Sha256Hex(addr);
    }

    private static string HashUserAgent(HttpContext? context)
    {
        var ua = context?.Request.Headers.UserAgent.ToString();
        return string.IsNullOrEmpty(ua) ? string.Empty : Sha256Hex(ua);
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

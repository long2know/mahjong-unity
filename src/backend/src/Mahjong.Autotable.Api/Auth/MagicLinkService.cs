using System.Security.Cryptography;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase J Wave 8 — issues and consumes email magic-link tokens. Tokens are
/// single-use (consumed-at gate), TTL-bound (default 15 min), and
/// cryptographically random (64-char URL-safe base64 of 48 bytes). Stored
/// in <see cref="EmailMagicLinkToken"/>; <c>Token</c> is uniquely indexed
/// for O(1) lookup.
///
/// <para>The dev / test path uses <see cref="LogEmailSender"/> /
/// <see cref="InMemoryEmailSender"/> so QA can read the token directly
/// without an SMTP round-trip.</para>
/// </summary>
public sealed class MagicLinkService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEmailSender _email;
    private readonly AuthOptions _options;
    private readonly ILogger<MagicLinkService> _logger;

    public MagicLinkService(IServiceScopeFactory scopeFactory, IEmailSender email, AuthOptions options, ILogger<MagicLinkService> logger)
    {
        _scopeFactory = scopeFactory;
        _email = email;
        _options = options;
        _logger = logger;
    }

    public TimeSpan TokenLifetime => TimeSpan.FromMinutes(Math.Max(1, _options.MagicLinkTtlMinutes));

    /// <summary>
    /// Creates a magic-link token, persists it, and emails the verify URL
    /// to <paramref name="email"/>. <paramref name="verifyUrl"/> is the
    /// base URL of the verify endpoint (the token is appended as
    /// <c>?token=</c>). Returns the issued token (the controller passes it
    /// back in the response body when running in dev so Hicks's UI and
    /// Vasquez's tests can verify without parsing email content).
    /// </summary>
    public async Task<EmailMagicLinkToken> IssueAsync(string email, string? requestedPlayerId, string verifyUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(email)) throw new ArgumentException("email required", nameof(email));
        var trimmed = email.Trim().ToLowerInvariant();
        if (!IsLikelyValidEmail(trimmed))
            throw new ArgumentException("email is not in a recognised shape.", nameof(email));

        var token = GenerateToken();
        var row = new EmailMagicLinkToken
        {
            Token = token,
            Email = trimmed,
            RequestedPlayerId = string.IsNullOrEmpty(requestedPlayerId) ? null : requestedPlayerId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(TokenLifetime),
        };

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EmailMagicLinkTokens.Add(row);
            await db.SaveChangesAsync(ct);
        }

        var url = $"{verifyUrl}{(verifyUrl.Contains('?') ? '&' : '?')}token={Uri.EscapeDataString(token)}";
        var body = $"Sign in to Mahjong Autotable:\n\n{url}\n\nThis link expires in {(int)TokenLifetime.TotalMinutes} minutes.";
        try
        {
            await _email.SendAsync(trimmed, "Mahjong Autotable sign-in link", body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Magic-link email delivery failed (token still valid in the DB).");
        }
        return row;
    }

    /// <summary>
    /// Consumes a token. Returns the row when the token is valid +
    /// unexpired + unused; returns null on any failure mode (missing,
    /// expired, already used). The DB row is marked
    /// <see cref="EmailMagicLinkToken.ConsumedAt"/> on success so a
    /// double-submit can't grant two sessions.
    /// </summary>
    public async Task<EmailMagicLinkToken?> ConsumeAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) return null;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.EmailMagicLinkTokens.FirstOrDefaultAsync(t => t.Token == token, ct);
        if (row is null) return null;
        if (row.ConsumedAt is not null) return null;
        if (row.ExpiresAt < DateTime.UtcNow) return null;
        row.ConsumedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return row;
    }

    private static string GenerateToken()
    {
        Span<byte> buf = stackalloc byte[48];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>Cheap structural validation; intentionally loose — the real
    /// gate is the magic-link round-trip.</summary>
    private static bool IsLikelyValidEmail(string email)
    {
        if (email.Length is < 3 or > 254) return false;
        var at = email.IndexOf('@');
        if (at < 1 || at == email.Length - 1) return false;
        if (email.IndexOf('@', at + 1) >= 0) return false;
        return email[(at + 1)..].Contains('.');
    }
}

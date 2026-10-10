namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Production fail-closed guard for the durable identity credential.
///
/// <para>The <c>mahjong_pid</c> cookie is signed with a key derived from the active JWT signing
/// key (see <see cref="PlayerIdentityTokenProtector"/>).
/// <see cref="Auth.JwtSigningKeyProvider"/> already refuses to boot a Production host that left
/// <c>Authentication:JwtSigningKeys</c> empty — but only when the resolved algorithm is HS256.
/// An RS256 Production host with no HMAC keys still falls through to a <b>per-process random</b>
/// HMAC key, which would silently invalidate every identity cookie on restart: every returning
/// player would be re-minted as a brand-new identity, losing profile, career stats and seat
/// reconnect.</para>
///
/// <para>This validator closes that gap by refusing the boot whenever Production would run on an
/// ephemeral identity-signing key, mirroring the existing
/// <c>ChangshaPrivacyStartupValidator</c> / <c>requireOperatorKeys</c> convention. Development
/// and Test keep the historical per-process shape (no operator secret required for
/// <c>dotnet run</c> / the test suite) — with the restart-resets-identity trade-off logged
/// explicitly.</para>
/// </summary>
public static class PlayerIdentityStartupValidator
{
    /// <summary>Operator-actionable message thrown when Production has no stable signing key.</summary>
    public const string ProductionRequiresStableSigningKeyMessage =
        "The durable player-identity cookie (mahjong_pid) is signed with the active JWT signing " +
        "key, but no operator key is configured and a per-process random key was minted. In " +
        "Production this fails closed: a restart would invalidate every issued identity, and no " +
        "node in a multi-instance deployment could verify another's cookies. Set " +
        "Authentication__JwtSigningKeys__0=<base64-key, 48 bytes recommended> (and optionally " +
        "Authentication__JwtSigningKeys__1=<previous-key> for rotation). See " +
        "docs/jwt-rotation.md §1 + §7.";

    /// <summary>Startup log message emitted for the non-Production ephemeral-key path.</summary>
    public const string EphemeralKeyDevelopmentMessage =
        "Player-identity cookies are signed with the per-process random JWT signing key. " +
        "Identities are unforgeable but reset on every restart. Configure " +
        "Authentication:JwtSigningKeys[0] for restart-stable identities.";

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when <paramref name="isProduction"/> is
    /// true and the identity-signing key is the ephemeral per-process fallback. Otherwise logs
    /// the resolved posture and returns.
    /// </summary>
    public static void Validate(bool isProduction, bool usingEphemeralSigningKey, ILogger? logger = null)
    {
        if (!usingEphemeralSigningKey)
        {
            logger?.LogInformation(
                "Player-identity cookies are signed with the configured JWT signing key set; identities survive restarts and rotation.");
            return;
        }

        if (isProduction)
        {
            throw new InvalidOperationException(ProductionRequiresStableSigningKeyMessage);
        }

        logger?.LogWarning(EphemeralKeyDevelopmentMessage);
    }
}

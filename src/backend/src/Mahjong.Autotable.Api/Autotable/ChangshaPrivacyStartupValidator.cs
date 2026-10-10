namespace Mahjong.Autotable.Api.Autotable;

/// <summary>
/// Blocker E (Bishop rev2) — SC-2/G19 privacy startup guard. Decides whether the server may
/// boot with opaque hidden-tile handles enabled, given the resolvable server-secret IKM.
///
/// <para>The <see cref="AutotableConnectionManager"/> builds its shared
/// <see cref="OpaqueTileHandleProvider"/> from an IKM resolved as: a present, base64-decodable
/// <c>Privacy:HandleSecret</c> is used AS-IS (it does <b>not</b> fall back to the JWT key even
/// when short), otherwise the active JWT signing-key material. The provider then requires at
/// least <see cref="OpaqueTileHandleProvider.MinimumSecretLengthBytes"/> bytes; anything shorter
/// (or absent) makes the manager log a warning and DISABLE handles — which silently emits REAL
/// tile ids for every concealed wall/foreign-hand tile (a fail-OPEN privacy hole).</para>
///
/// <para>This validator replicates that exact resolution so a Production deployment that would
/// fail open instead fails CLOSED at startup — mirroring the JWT <c>requireOperatorKeys</c>
/// convention. Development / Test keep the historical warn-and-disable shape so no operator
/// secret is required for <c>dotnet run</c> / the test suite.</para>
/// </summary>
public static class ChangshaPrivacyStartupValidator
{
    /// <summary>Operator-actionable message thrown when Production would fail open.</summary>
    public const string OpaqueHandlesRequireIkmMessage =
        "SC-2 opaque tile-handle privacy is enabled (ChangshaRuntime:OpaqueHiddenHandles) but the " +
        "resolved server-secret IKM is below the required " +
        "32 bytes. In Production this fails closed to avoid silently emitting real concealed tile " +
        "ids. Configure Privacy:HandleSecret (base64, >=32 bytes) or a JWT signing key of >=32 " +
        "bytes, or set ChangshaRuntime:OpaqueHiddenHandles=false to explicitly opt out of tile " +
        "privacy.";

    /// <summary>
    /// Resolves the effective IKM byte length the connection manager would use, mirroring its
    /// priority: a base64-decodable <paramref name="handleSecretBase64"/> (used as-is, even when
    /// short — no fallback), otherwise <paramref name="jwtKeyMaterial"/>. A whitespace/invalid
    /// base64 handle secret is treated as absent (the manager's <c>catch</c> path). Returns 0
    /// when neither yields bytes.
    /// </summary>
    public static int EffectiveIkmLength(string? handleSecretBase64, byte[]? jwtKeyMaterial)
    {
        if (!string.IsNullOrWhiteSpace(handleSecretBase64))
        {
            try { return Convert.FromBase64String(handleSecretBase64).Length; }
            catch { /* invalid base64 ⇒ manager falls back to the JWT key */ }
        }
        return jwtKeyMaterial?.Length ?? 0;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when <paramref name="isProduction"/> is true,
    /// opaque handles are enabled, and no IKM of at least
    /// <see cref="OpaqueTileHandleProvider.MinimumSecretLengthBytes"/> bytes is resolvable. A no-op
    /// otherwise (non-Production, handles explicitly disabled, or a sufficient IKM present).
    /// </summary>
    public static void Validate(
        bool isProduction,
        bool opaqueHandlesEnabled,
        string? handleSecretBase64,
        byte[]? jwtKeyMaterial)
    {
        if (!isProduction || !opaqueHandlesEnabled)
        {
            return;
        }
        var ikmLength = EffectiveIkmLength(handleSecretBase64, jwtKeyMaterial);
        if (ikmLength < OpaqueTileHandleProvider.MinimumSecretLengthBytes)
        {
            throw new InvalidOperationException(OpaqueHandlesRequireIkmMessage);
        }
    }
}

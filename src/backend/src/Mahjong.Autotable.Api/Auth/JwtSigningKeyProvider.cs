using System.Collections.Generic;
using System.Linq;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 4 — Bishop. Singleton key store materialised from
/// <see cref="AuthOptions.JwtSigningKeys"/>. The list is cached for
/// the lifetime of the process — rotation requires a pod restart
/// (matches the existing OAuthStateProtector contract; the operator
/// runbook is <c>docs/jwt-rotation.md</c> §4).
///
/// <para>Load-time precedence:
/// <list type="number">
///   <item><see cref="AuthOptions.JwtSigningKeys"/> array — the
///         canonical Wave-3 shape. Entry 0 is the active signer.</item>
///   <item>Legacy <see cref="AuthOptions.JwtSigningKey"/> singular —
///         consumed when the array is empty (one-wave backward
///         compatibility, slated for Wave-5 removal).</item>
///   <item>Per-process random fallback — minted when both above are
///         empty so dev / test never need explicit secrets. A warning
///         is logged so operators notice the unsuitable-for-prod
///         state.</item>
/// </list></para>
///
/// <para>Phase K Wave 6 — Bishop. <see cref="AuthOptions.JwtAlgorithm"/>
/// selects between HS256 (HMAC; <see cref="AllKeys"/> populated) and
/// RS256 (RSA; <see cref="AllRsaKeys"/> populated). The HMAC fallback
/// list is always loaded even when the active algorithm is RS256 so
/// validation of legacy HMAC tokens continues to work during the
/// migration window — issuance, however, branches strictly on the
/// resolved algorithm.</para>
///
/// <para>Phase L — Drake. <c>requireOperatorKeys</c> hardens the
/// production deploy path: when <see langword="true"/> and the
/// operator left <see cref="AuthOptions.JwtSigningKeys"/> AND
/// <see cref="AuthOptions.JwtSigningKey"/> empty (and the resolved
/// algorithm is HS256), the constructor throws
/// <see cref="InvalidOperationException"/> BEFORE the host starts
/// listening. Without this guard, the ephemeral per-process random
/// HMAC key is re-minted on every restart, silently invalidating
/// every JWT issued by the prior process — see
/// <c>docs/jwt-rotation.md</c> §7 for the operator runbook. The same
/// guard fires for RS256 when no PEM is supplied. Program.cs passes
/// <c>builder.Environment.IsProduction()</c>; tests default to
/// <see langword="false"/> so the legacy dev / test fallback shape
/// is unchanged.</para>
/// </summary>
public sealed class JwtSigningKeyProvider
{
    private readonly IReadOnlyList<JwtSigningKey> _keys;
    private readonly Dictionary<string, JwtSigningKey> _byKid;
    private readonly IReadOnlyList<JwtRsaSigningKey> _rsaKeys;
    private readonly Dictionary<string, JwtRsaSigningKey> _rsaByKid;
    private readonly bool _fallbackKeyInUse;
    private readonly string _algorithm;
    private readonly string _issuer;

    /// <summary>Operator-actionable error message thrown when
    /// <c>requireOperatorKeys</c> is set, the resolved algorithm is
    /// HS256, and no HMAC key material is configured. Public so tests
    /// (and ops tooling) can hard-assert against the canonical
    /// wording — emitted verbatim in the prod-startup failure path.</summary>
    public const string ProdRequiresOperatorHmacKeyMessage =
        "Authentication:JwtSigningKeys is required in Production but is empty. " +
        "Set Authentication__JwtSigningKeys__0=<base64-key>=48-bytes-recommended (and " +
        "optionally Authentication__JwtSigningKeys__1=<previous-key> for rotation) so " +
        "JWTs survive container restarts. See docs/jwt-rotation.md §1 + §7.";

    /// <summary>Operator-actionable error message thrown when
    /// <c>requireOperatorKeys</c> is set, the resolved algorithm is
    /// RS256, and no RSA PEM material is configured.</summary>
    public const string ProdRequiresOperatorRsaKeyMessage =
        "Authentication:JwtAlgorithm=RS256 in Production but Authentication:JwtRsaKeys is empty. " +
        "Set Authentication__JwtRsaKeys__0=<PEM-encoded private key> so JWTs survive " +
        "container restarts. See docs/jwt-rotation.md §1 + §7.";

    public JwtSigningKeyProvider(AuthOptions options, ILogger<JwtSigningKeyProvider> logger)
        : this(options, logger, requireOperatorKeys: false)
    {
    }

    public JwtSigningKeyProvider(AuthOptions options, ILogger<JwtSigningKeyProvider> logger, bool requireOperatorKeys)
    {
        // Phase K Wave 6 — resolve the configured algorithm. Anything
        // other than the two supported values defaults to HS256 +
        // emits a warning so an operator typo doesn't silently fall
        // into a "neither algorithm works" state.
        _algorithm = NormaliseAlgorithm(options.JwtAlgorithm, logger);
        _issuer = (options.Issuer ?? string.Empty).Trim();

        var resolved = new List<JwtSigningKey>();
        if (options.JwtSigningKeys is { Length: > 0 })
        {
            for (var i = 0; i < options.JwtSigningKeys.Length; i++)
            {
                var raw = options.JwtSigningKeys[i];
                if (string.IsNullOrEmpty(raw)) continue;
                resolved.Add(new JwtSigningKey(resolved.Count, raw));
            }
        }
        if (resolved.Count == 0 && !string.IsNullOrEmpty(options.JwtSigningKey))
        {
            logger.LogWarning(
                "AuthOptions.JwtSigningKey (singular) is set but JwtSigningKeys (array) is empty — falling back to the legacy key. This path is removed in Wave 5; populate Authentication:JwtSigningKeys instead.");
            resolved.Add(new JwtSigningKey(0, options.JwtSigningKey));
        }
        if (resolved.Count == 0)
        {
            // Phase L — Drake. Production hardening: when the host
            // is bound to Production AND the resolved algorithm is
            // HS256, refuse to start with an ephemeral random key.
            // Without this guard a container restart silently
            // invalidates every JWT minted by the prior process
            // because the new random key never matches the old
            // signature. Operators MUST set Authentication:JwtSigningKeys[0]
            // (or, for legacy deploys, the deprecated singular
            // Authentication:JwtSigningKey). See docs/jwt-rotation.md §7.
            if (requireOperatorKeys && string.Equals(_algorithm, "HS256", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ProdRequiresOperatorHmacKeyMessage);
            }
            // Phase K Wave 4 — dev / test fallback: mint a per-process
            // random key so the issuance + validation surface stays
            // resolvable without explicit operator config. The warning
            // is loud so production deployments notice.
            var random = new byte[48];
            System.Security.Cryptography.RandomNumberGenerator.Fill(random);
            var fallback = Convert.ToBase64String(random);
            resolved.Add(new JwtSigningKey(0, fallback));
            _fallbackKeyInUse = true;
            logger.LogWarning(
                "No JWT signing keys configured (Authentication:JwtSigningKeys empty AND Authentication:JwtSigningKey unset). Minted a per-process random HMAC key. Set Authentication:JwtSigningKeys[0] for production deployments — see docs/jwt-rotation.md.");
        }
        else if (_fallbackKeyInUse == false)
        {
            logger.LogInformation(
                "JWT signing keys loaded: {Count} key(s); active signer kid={Kid}.",
                resolved.Count, resolved[0].Kid);
        }
        _keys = resolved;
        _byKid = resolved.ToDictionary(k => k.Kid, StringComparer.Ordinal);

        // Phase K Wave 6 — load any configured RSA private keys
        // regardless of the active algorithm so a future flip from
        // HS256→RS256 doesn't require a pod restart to pre-load the
        // material. A PEM that fails to parse is logged + skipped so
        // a single bad entry never bricks the surface.
        var rsa = new List<JwtRsaSigningKey>();
        if (options.JwtRsaKeys is { Length: > 0 })
        {
            for (var i = 0; i < options.JwtRsaKeys.Length; i++)
            {
                var pem = options.JwtRsaKeys[i];
                if (string.IsNullOrWhiteSpace(pem)) continue;
                try
                {
                    rsa.Add(new JwtRsaSigningKey(rsa.Count, pem));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "RSA signing key at index {Index} failed to parse — skipping. Provide a valid PKCS#1 or PKCS#8 PEM blob.",
                        i);
                }
            }
        }
        if (_algorithm == "RS256" && rsa.Count == 0)
        {
            // Phase L — Drake. Production hardening: when the host is
            // bound to Production AND the resolved algorithm is RS256,
            // refuse to start without operator-provided PEM material —
            // the issuer would otherwise throw on first ActiveRsaKey
            // resolve, taking the listener down mid-request. Fail fast
            // at boot so the deploy pipeline catches the misconfig
            // before traffic lands. See docs/jwt-rotation.md §7.
            if (requireOperatorKeys)
            {
                throw new InvalidOperationException(ProdRequiresOperatorRsaKeyMessage);
            }
            logger.LogError(
                "Authentication:JwtAlgorithm is RS256 but Authentication:JwtRsaKeys is empty. Token issuance will fail until at least one PEM-encoded private key is provided. See docs/jwt-rotation.md §RS256.");
        }
        else if (rsa.Count > 0 && _algorithm == "RS256")
        {
            logger.LogInformation(
                "JWT RSA signing keys loaded: {Count} key(s); active RSA signer kid={Kid}.",
                rsa.Count, rsa[0].Kid);
        }
        _rsaKeys = rsa;
        _rsaByKid = rsa.ToDictionary(k => k.Kid, StringComparer.Ordinal);
    }

    /// <summary>Resolved JWT signing algorithm — either <c>HS256</c>
    /// or <c>RS256</c>. Determines which key set the issuer signs
    /// with and which JWKS shape the discovery endpoint publishes.</summary>
    public string Algorithm => _algorithm;

    /// <summary>Phase K Wave 7 — Bishop. Configured issuer URL stamped
    /// into RS256 tokens (<c>iss</c> claim) and advertised by the OIDC
    /// discovery document (<c>issuer</c> field). Empty when the
    /// operator left <c>Auth:Issuer</c> unset; the discovery endpoint
    /// falls back to the request's own scheme+host in that case
    /// (HS256 hosts intentionally publish no discovery document, so
    /// the empty fallback is unreachable there).</summary>
    public string ConfiguredIssuer => _issuer;

    /// <summary>Active HMAC signer used for new HS256 token issuance.</summary>
    public JwtSigningKey ActiveKey => _keys[0];

    /// <summary>Active RSA signer used for new RS256 token issuance.
    /// Throws <see cref="InvalidOperationException"/> when no RSA key
    /// is configured — callers should check <see cref="Algorithm"/>
    /// first.</summary>
    public JwtRsaSigningKey ActiveRsaKey
    {
        get
        {
            if (_rsaKeys.Count == 0)
                throw new InvalidOperationException(
                    "No RSA signing keys configured. Populate Authentication:JwtRsaKeys[0] with a PEM-encoded private key.");
            return _rsaKeys[0];
        }
    }

    /// <summary>Full HMAC fallback list in load order. Index 0 is the
    /// active signer; later entries are accepted on validation only.</summary>
    public IReadOnlyList<JwtSigningKey> AllKeys => _keys;

    /// <summary>Full RSA fallback list in load order. Index 0 is the
    /// active signer; later entries are accepted on validation only.
    /// May be empty when only HMAC keys are configured.</summary>
    public IReadOnlyList<JwtRsaSigningKey> AllRsaKeys => _rsaKeys;

    /// <summary>True when the active key was minted at process start
    /// because the operator left both knobs unset — diagnostic-only.</summary>
    public bool UsingEphemeralFallbackKey => _fallbackKeyInUse;

    /// <summary>Looks up an HMAC key by its deterministic
    /// <see cref="JwtSigningKey.Kid"/>. Returns null when the token's
    /// <c>kid</c> header does not match any loaded HMAC key.</summary>
    public JwtSigningKey? TryGetByKid(string? kid)
    {
        if (string.IsNullOrEmpty(kid)) return null;
        return _byKid.TryGetValue(kid, out var key) ? key : null;
    }

    /// <summary>Looks up an RSA key by its deterministic
    /// <see cref="JwtRsaSigningKey.Kid"/>. Returns null when the
    /// token's <c>kid</c> header does not match any loaded RSA key —
    /// validators then fall through to the HMAC path.</summary>
    public JwtRsaSigningKey? TryGetRsaByKid(string? kid)
    {
        if (string.IsNullOrEmpty(kid)) return null;
        return _rsaByKid.TryGetValue(kid, out var key) ? key : null;
    }

    private static string NormaliseAlgorithm(string? configured, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configured)) return "HS256";
        var trimmed = configured.Trim();
        if (string.Equals(trimmed, "HS256", StringComparison.OrdinalIgnoreCase)) return "HS256";
        if (string.Equals(trimmed, "RS256", StringComparison.OrdinalIgnoreCase)) return "RS256";
        logger.LogWarning(
            "Authentication:JwtAlgorithm value '{Value}' is not supported (expected HS256 or RS256). Falling back to HS256.",
            configured);
        return "HS256";
    }
}

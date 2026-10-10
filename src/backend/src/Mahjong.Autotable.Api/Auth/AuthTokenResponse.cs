using System.Text.Json.Serialization;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 5 — Bishop. Pinned response envelope for
/// <c>POST /api/auth/token</c>. The wire shape is canonical and
/// regression-asserted in <c>AuthTokenResponseContractTests</c>
/// (Phase_K_W5/). DO NOT add or rename fields without a versioned
/// migration — every downstream client SDK, dashboard, and
/// integration smoke pins these exact names.
///
/// <para>Field summary:</para>
/// <list type="bullet">
///   <item><c>token</c> — minted HS256 JWT (3 base64url segments).</item>
///   <item><c>expiresAtUtc</c> — absolute expiry instant, ISO-8601
///         with explicit UTC suffix.</item>
///   <item><c>kid</c> — deterministic key identifier (matches the
///         <c>kid</c> header inside the JWT).</item>
///   <item><c>tokenType</c> — bearer-token bookkeeping. Always
///         <c>"Bearer"</c> in Wave 5; the field exists so HTTP
///         <c>Authorization: Bearer …</c> consumers can pin against
///         a stable token-type literal.</item>
///   <item><c>expiresInSeconds</c> — relative TTL in seconds at
///         response-render time. Mirrors the OAuth 2.0
///         <c>expires_in</c> convention so clients that share the
///         renewal-scheduler with OAuth flows don't need a separate
///         clock-sync path.</item>
/// </list>
/// </summary>
public sealed record AuthTokenResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expiresAtUtc")] DateTime ExpiresAtUtc,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("tokenType")] string TokenType,
    [property: JsonPropertyName("expiresInSeconds")] int ExpiresInSeconds)
{
    /// <summary>Pinned token-type literal — RFC 6750 "Bearer".</summary>
    public const string BearerTokenType = "Bearer";
}

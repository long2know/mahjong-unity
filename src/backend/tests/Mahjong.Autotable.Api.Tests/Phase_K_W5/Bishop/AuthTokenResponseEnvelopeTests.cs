using System.Globalization;
using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5.Bishop;

/// <summary>
/// Phase K Wave 5 — Bishop. Contract tests pinning the
/// <see cref="AuthTokenResponse"/> envelope returned by
/// <c>POST /api/auth/token</c>.
///
/// <para>Every downstream client SDK, dashboard, and integration
/// smoke pins on these exact field names + types — the W4 baseline
/// returned an anonymous object with three properties; the W5
/// upgrade adds <c>tokenType</c> + <c>expiresInSeconds</c> while
/// keeping the original three byte-stable.</para>
/// </summary>
public sealed class AuthTokenResponseEnvelopeTests
{
    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void AuthTokenResponse_HasFiveJsonPinnedFields()
    {
        var record = typeof(AuthTokenResponse);
        var props = record.GetProperties();

        var byJsonName = props
            .Select(p => new
            {
                Prop = p,
                Json = (System.Text.Json.Serialization.JsonPropertyNameAttribute?)
                    Attribute.GetCustomAttribute(p, typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute)),
            })
            .Where(x => x.Json is not null)
            .ToDictionary(x => x.Json!.Name, x => x.Prop);

        Assert.True(byJsonName.ContainsKey("token"), "token field MUST exist");
        Assert.True(byJsonName.ContainsKey("expiresAtUtc"), "expiresAtUtc field MUST exist");
        Assert.True(byJsonName.ContainsKey("kid"), "kid field MUST exist");
        Assert.True(byJsonName.ContainsKey("tokenType"), "tokenType field MUST exist (Wave 5)");
        Assert.True(byJsonName.ContainsKey("expiresInSeconds"), "expiresInSeconds field MUST exist (Wave 5)");

        Assert.Equal(typeof(string), byJsonName["token"].PropertyType);
        Assert.Equal(typeof(DateTime), byJsonName["expiresAtUtc"].PropertyType);
        Assert.Equal(typeof(string), byJsonName["kid"].PropertyType);
        Assert.Equal(typeof(string), byJsonName["tokenType"].PropertyType);
        Assert.Equal(typeof(int), byJsonName["expiresInSeconds"].PropertyType);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void AuthTokenResponse_SerializesWithCamelCaseLiterals()
    {
        var inst = new AuthTokenResponse(
            Token: "abc.def.ghi",
            ExpiresAtUtc: new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc),
            Kid: "kid-xyz",
            TokenType: AuthTokenResponse.BearerTokenType,
            ExpiresInSeconds: 3600);
        var json = JsonSerializer.Serialize(inst);

        Assert.Contains("\"token\":", json);
        Assert.Contains("\"expiresAtUtc\":", json);
        Assert.Contains("\"kid\":", json);
        Assert.Contains("\"tokenType\":\"Bearer\"", json);
        Assert.Contains("\"expiresInSeconds\":3600", json);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void AuthTokenResponse_BearerConstantIsRfc6750Bearer()
    {
        Assert.Equal("Bearer", AuthTokenResponse.BearerTokenType);
    }
}

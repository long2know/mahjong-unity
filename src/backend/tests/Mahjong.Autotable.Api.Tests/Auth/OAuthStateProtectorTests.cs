using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase K Wave 1 — unit tests for the HMAC-signed OAuth state token.
/// Asserts roundtrip, tamper-detection, expiry handling, and the
/// auto-key-mint behaviour when <c>AuthOptions.StateSigningKey</c> is
/// blank.
/// </summary>
public sealed class OAuthStateProtectorTests
{
    [Fact]
    public void Issue_then_Verify_roundtrips_with_nonce_match()
    {
        var p = Build("super-secret-test-key-32-bytes-min");
        var issue = p.Issue();
        Assert.False(string.IsNullOrEmpty(issue.Token));
        Assert.False(string.IsNullOrEmpty(issue.Nonce));
        var verify = p.Verify(issue.Token);
        Assert.True(verify.Ok);
        Assert.Equal(issue.Nonce, verify.Nonce);
    }

    [Fact]
    public void Verify_rejects_tampered_state()
    {
        var p = Build("super-secret-test-key");
        var issue = p.Issue();
        // Flip a character mid-token.
        var tampered = issue.Token.Substring(0, issue.Token.Length - 4)
                       + (issue.Token[^4] == 'A' ? "B" : "A")
                       + issue.Token.Substring(issue.Token.Length - 3);
        var verify = p.Verify(tampered);
        Assert.False(verify.Ok);
        Assert.Equal("bad-signature", verify.Reason);
    }

    [Fact]
    public void Verify_rejects_expired_state()
    {
        var p = Build("super-secret-test-key");
        var issue = p.Issue(TimeSpan.FromMilliseconds(1));
        Thread.Sleep(1500); // wait past 1s resolution of unix-seconds
        var verify = p.Verify(issue.Token);
        Assert.False(verify.Ok);
        Assert.Equal("expired", verify.Reason);
    }

    [Fact]
    public void Verify_returns_malformed_for_garbage_input()
    {
        var p = Build("super-secret-test-key");
        Assert.False(p.Verify("").Ok);
        Assert.False(p.Verify("not-base64-!").Ok);
        Assert.False(p.Verify("AAAA").Ok); // too short
    }

    [Fact]
    public void DifferentKeys_produce_incompatible_tokens()
    {
        var alice = Build("alice-key");
        var mallory = Build("mallory-key");
        var token = alice.Issue().Token;
        var verify = mallory.Verify(token);
        Assert.False(verify.Ok);
        Assert.Equal("bad-signature", verify.Reason);
    }

    [Fact]
    public void EmptyConfiguredKey_mints_a_per_process_key()
    {
        // Two distinct protectors with empty config should mint two
        // different keys, so tokens issued by one can't verify on the
        // other.
        var a = Build("");
        var b = Build("");
        var token = a.Issue().Token;
        Assert.False(b.Verify(token).Ok);
        // But the protector that issued the token can still verify it.
        Assert.True(a.Verify(token).Ok);
    }

    private static OAuthStateProtector Build(string signingKey)
    {
        return new OAuthStateProtector(
            new AuthOptions { StateSigningKey = signingKey },
            NullLogger<OAuthStateProtector>.Instance);
    }
}

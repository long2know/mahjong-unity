using System.Text;
using Mahjong.Autotable.Api.Autotable;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Blocker E (Bishop rev2) — SC-2/G19 privacy fail-closed startup guard
/// (<see cref="ChangshaPrivacyStartupValidator"/>). Mirrors the Phase-L JWT prod-hardening
/// test shape (direct on the extractable validator) so no full-host boot is needed. Pins:
/// Production + opaque handles + no viable (>=32-byte) IKM ⇒ refuse to boot; a sufficient IKM
/// (dedicated Privacy:HandleSecret OR a long-enough JWT key) boots; and Dev/Test never fail.
/// </summary>
public sealed class ChangshaPrivacyStartupValidatorTests
{
    private static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)i).ToArray();
    private static string B64(int n) => Convert.ToBase64String(Bytes(n));
    private const int Min = OpaqueTileHandleProvider.MinimumSecretLengthBytes; // 32

    // ── fail closed ─────────────────────────────────────────────────────────────────
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "privacy-failclosed")]
    public void Prod_OpaqueOn_NoIkm_Throws_WithOperatorActionableMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ChangshaPrivacyStartupValidator.Validate(
                isProduction: true, opaqueHandlesEnabled: true,
                handleSecretBase64: null, jwtKeyMaterial: null));
        Assert.Equal(ChangshaPrivacyStartupValidator.OpaqueHandlesRequireIkmMessage, ex.Message);
        Assert.Contains("Privacy:HandleSecret", ex.Message);
        Assert.Contains("OpaqueHiddenHandles=false", ex.Message);
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "privacy-failclosed")]
    public void Prod_OpaqueOn_ShortJwtKey_NoHandleSecret_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaPrivacyStartupValidator.Validate(
                isProduction: true, opaqueHandlesEnabled: true,
                handleSecretBase64: null, jwtKeyMaterial: Bytes(Min - 1)));
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "privacy-failclosed")]
    public void Prod_OpaqueOn_ShortHandleSecret_DoesNotFallBackToLongJwt_Throws()
    {
        // The manager uses a present HandleSecret AS-IS (no JWT fallback), so a short
        // HandleSecret fails closed even when a long JWT key is also configured.
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaPrivacyStartupValidator.Validate(
                isProduction: true, opaqueHandlesEnabled: true,
                handleSecretBase64: B64(Min - 1), jwtKeyMaterial: Bytes(Min + 16)));
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "privacy-failclosed")]
    public void Prod_OpaqueOn_InvalidBase64HandleSecret_FallsBackToShortJwt_Throws()
    {
        // Invalid base64 => treated as absent => falls back to the (short) JWT key => fails.
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaPrivacyStartupValidator.Validate(
                isProduction: true, opaqueHandlesEnabled: true,
                handleSecretBase64: "!!!not-base64!!!", jwtKeyMaterial: Bytes(4)));
    }

    // ── boots ───────────────────────────────────────────────────────────────────────
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "privacy-failclosed")]
    public void Prod_OpaqueOn_SufficientHandleSecret_Boots()
    {
        ChangshaPrivacyStartupValidator.Validate(
            isProduction: true, opaqueHandlesEnabled: true,
            handleSecretBase64: B64(Min), jwtKeyMaterial: null);
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "privacy-failclosed")]
    public void Prod_OpaqueOn_SufficientJwtKey_NoHandleSecret_Boots()
    {
        ChangshaPrivacyStartupValidator.Validate(
            isProduction: true, opaqueHandlesEnabled: true,
            handleSecretBase64: null, jwtKeyMaterial: Encoding.UTF8.GetBytes(new string('k', Min)));
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "privacy-failclosed")]
    public void Prod_OpaqueDisabled_NoIkm_Boots_ExplicitOptOut()
    {
        ChangshaPrivacyStartupValidator.Validate(
            isProduction: true, opaqueHandlesEnabled: false,
            handleSecretBase64: null, jwtKeyMaterial: null);
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "privacy-failclosed")]
    public void NonProduction_OpaqueOn_NoIkm_Boots_HistoricalWarnAndDisable()
    {
        ChangshaPrivacyStartupValidator.Validate(
            isProduction: false, opaqueHandlesEnabled: true,
            handleSecretBase64: null, jwtKeyMaterial: null);
    }

    // ── EffectiveIkmLength resolution parity ─────────────────────────────────────────
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "privacy-failclosed")]
    public void EffectiveIkmLength_PrefersHandleSecret_ThenJwt()
    {
        Assert.Equal(Min, ChangshaPrivacyStartupValidator.EffectiveIkmLength(B64(Min), Bytes(4)));
        Assert.Equal(4, ChangshaPrivacyStartupValidator.EffectiveIkmLength(null, Bytes(4)));
        Assert.Equal(0, ChangshaPrivacyStartupValidator.EffectiveIkmLength("   ", null));
    }
}

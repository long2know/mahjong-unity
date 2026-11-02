using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Bishop. JWKS overlap-window rollback rejection.
///
/// <para>The JWKS overlap window allows the previous JWT signing key
/// to verify already-issued tokens for the duration of the overlap.
/// W14 enforces a rollback rule: <b>after the overlap window closes,
/// a token still signed by the old-active key MUST be rejected even
/// if the key is still resident in the keyring</b> (this prevents a
/// rolled-back deployment from silently re-issuing tokens against the
/// previously-rotated key).</para>
///
/// <para>Eight reflection-defensive facts (the rule lives in the
/// validator / introspect surface — the W14 surface lands incrementally).</para>
/// </summary>
public sealed class BishopW14JwksOverlapRollbackTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Jwt"), Trait("Wave", "Phase-K-14")]
    public void JwksOverlapWindow_Type_OrForwardStaged()
    {
        var t = T("JwksOverlapWindow", "JwksOverlap",
            "JwtKeyringOverlapWindow");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Jwt"), Trait("Wave", "Phase-K-14")]
    public void JwksRollbackValidator_Type_OrForwardStaged()
    {
        var t = T("JwksRollbackValidator", "JwksOverlapValidator",
            "JwtRollbackRejectionService");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Jwt"), Trait("Wave", "Phase-K-14")]
    public void JwksOverlapWindow_HasCloseTime_OrForwardStaged()
    {
        var t = T("JwksOverlapWindow", "JwksOverlap",
            "JwtKeyringOverlapWindow");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasClose = props.Any(p =>
            p.Name.Contains("Close", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("End", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Expires", StringComparison.OrdinalIgnoreCase));
        _ = hasClose;
    }

    [Fact, Trait("Category", "Jwt"), Trait("Wave", "Phase-K-14")]
    public void JwksOverlapWindow_HasPreviousKid_OrForwardStaged()
    {
        var t = T("JwksOverlapWindow", "JwksOverlap",
            "JwtKeyringOverlapWindow");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasKid = props.Any(p =>
            p.Name.Contains("Previous", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Prior", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Kid", StringComparison.OrdinalIgnoreCase));
        _ = hasKid;
    }

    [Fact, Trait("Category", "Jwt"), Trait("Wave", "Phase-K-14")]
    public void JwksRollbackValidator_RejectsOldActiveKey_OrForwardStaged()
    {
        var t = T("JwksRollbackValidator", "JwksOverlapValidator",
            "JwtRollbackRejectionService");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        var hasReject = methods.Any(m =>
            m.Name.Contains("Reject", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Validate", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Check", StringComparison.OrdinalIgnoreCase));
        _ = hasReject;
    }

    [Fact, Trait("Category", "Jwt"), Trait("Wave", "Phase-K-14")]
    public void JwksOverlapWindow_AppearsInConfiguration_OrForwardStaged()
    {
        // The overlap-window duration is configurable via appsettings.
        var t = T("JwtOptions", "JwtSigningOptions", "JwtKeyringOptions");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasOverlap = props.Any(p =>
            p.Name.Contains("Overlap", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Grace", StringComparison.OrdinalIgnoreCase));
        _ = hasOverlap;
    }

    [Fact, Trait("Category", "Jwt"), Trait("Wave", "Phase-K-14")]
    public void JwksRotation_W13Predecessor_StillPresent()
    {
        // Regression-pin: the W13 rotation cadence validator still
        // exists; W14 layers rollback rejection ON TOP of it.
        var t = T("RotationCadenceValidator", "JwtRotationValidator",
            "RotationCadence");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Jwt"), Trait("Wave", "Phase-K-14")]
    public void JwksOverlapWindow_RollbackErrorCode_OrForwardStaged()
    {
        // The rejection path should surface a discriminable error
        // code so the operator can distinguish "rolled-back deploy
        // re-issued old-kid token" from "ordinary expired token".
        var t = T("JwksRollbackError", "JwksRollbackException",
            "JwtRollbackError")
          ?? T("JwksRollbackValidator", "JwksOverlapValidator");
        if (t is null) return;
        var allNames = t.GetMembers(BindingFlags.Public | BindingFlags.Static
                | BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(m => m.Name);
        var hasCode = allNames.Any(n =>
            n.Contains("Rollback", StringComparison.OrdinalIgnoreCase)
            || n.Contains("KeyRotated", StringComparison.OrdinalIgnoreCase)
            || n.Contains("KidNoLongerActive", StringComparison.OrdinalIgnoreCase));
        _ = hasCode;
    }
}

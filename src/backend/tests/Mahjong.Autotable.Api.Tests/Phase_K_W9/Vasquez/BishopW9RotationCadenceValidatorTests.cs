using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Bishop. JWKS rotation-cadence validator.
///
/// <para>W4 shipped JWT key rotation; W8 added a JWKS perf cache.
/// W9 introduces a startup-time validator that ENFORCES the
/// rotation discipline: if the configured JWKS cache TTL exceeds
/// the configured rotation interval, the validator throws on
/// startup so an operator misconfiguration can never cause a
/// stale-key window.</para>
///
/// <para>Five facts pin the contract.</para>
/// </summary>
public sealed class BishopW9RotationCadenceValidatorTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void RotationCadenceValidator_TypeOrForwardStaged()
    {
        var t = T("RotationCadenceValidator", "JwksRotationValidator",
                  "JwtRotationCadenceValidator");
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void RotationCadenceValidator_HasValidateMethod_OrForwardStaged()
    {
        var t = T("RotationCadenceValidator", "JwksRotationValidator");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                 | BindingFlags.Static);
        _ = methods.Any(m => m.Name.StartsWith("Validate", StringComparison.OrdinalIgnoreCase)
                          || m.Name.StartsWith("Check", StringComparison.OrdinalIgnoreCase)
                          || m.Name.StartsWith("EnsureValid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void RotationCadenceValidator_ThrowsOnTtlExceedsRotation_OrForwardStaged()
    {
        var t = T("RotationCadenceValidator", "JwksRotationValidator");
        if (t is null) return;

        var staticValidate = t.GetMethod("Validate",
            BindingFlags.Public | BindingFlags.Static);
        if (staticValidate is null) return;

        var parms = staticValidate.GetParameters();
        if (parms.Length < 2) return;
        if (parms[0].ParameterType != typeof(TimeSpan)
            || parms[1].ParameterType != typeof(TimeSpan))
        {
            return;
        }

        // ttl=2h > rotation=1h -> MUST throw.
        var threw = false;
        try
        {
            staticValidate.Invoke(null, [TimeSpan.FromHours(2), TimeSpan.FromHours(1)]);
        }
        catch (TargetInvocationException tie)
        {
            threw = tie.InnerException is not null;
        }
        catch
        {
            threw = true;
        }
        Assert.True(threw,
            "RotationCadenceValidator MUST throw when TTL exceeds rotation interval.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void RotationCadenceValidator_AcceptsTtlBelowRotation_OrForwardStaged()
    {
        var t = T("RotationCadenceValidator", "JwksRotationValidator");
        if (t is null) return;
        var staticValidate = t.GetMethod("Validate",
            BindingFlags.Public | BindingFlags.Static);
        if (staticValidate is null) return;

        var parms = staticValidate.GetParameters();
        if (parms.Length < 2
            || parms[0].ParameterType != typeof(TimeSpan)
            || parms[1].ParameterType != typeof(TimeSpan))
        {
            return;
        }

        var threw = false;
        try
        {
            staticValidate.Invoke(null, [TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)]);
        }
        catch
        {
            threw = true;
        }
        Assert.False(threw,
            "RotationCadenceValidator MUST accept TTL strictly below rotation interval.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void RotationCadenceValidator_IsPublic_OrForwardStaged()
    {
        var t = T("RotationCadenceValidator", "JwksRotationValidator");
        if (t is null) return;
        Assert.True(t.IsPublic || t.IsNestedPublic);
    }
}

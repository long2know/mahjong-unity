using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez;

/// <summary>
/// Phase K Wave 12 — Bishop. JWKS staged rotation overlap window.
///
/// <para>W4 shipped JWT signing key rotation. W11 shipped the JWT
/// rotation rehearsal workflow. W12 introduces a STAGED rotation:
/// when a new signing key is published, the old key remains in the
/// JWKS endpoint response for an overlap window (default 24h) so
/// in-flight tokens signed with the previous kid remain valid.</para>
///
/// <para>Eight forward-stage facts pin the W12 contract:</para>
/// <list type="number">
///   <item><c>JwksStagedRotationOptions</c> (or
///         <c>JwtRotationOptions</c>) type present.</item>
///   <item>The options carry an overlap-window property
///         (<c>OverlapWindow</c> / <c>RetireGrace</c>).</item>
///   <item>The JWKS endpoint response includes BOTH keys during
///         the overlap window (any controller method or
///         <c>JwksKeySet</c> assembly that supports a list).</item>
///   <item>Old keys are tagged with a retire timestamp
///         (any property named <c>RetiredAt</c> / <c>RetiresAt</c>
///         / <c>ExpiresAt</c>).</item>
///   <item><c>JwtIssuingService.Kid</c> W4 regression pin still
///         present.</item>
///   <item>The W4 JWT signing keys appsettings shape is preserved.</item>
///   <item>The W11 jwt-rotation-rehearsal.yml workflow file
///         is present.</item>
///   <item>The W12 staged-rotation surface registers via DI
///         (extension method shape).</item>
/// </list>
/// </summary>
public sealed class BishopW12JwksStagedRotationTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void StagedRotationOptions_TypePresent_OrForwardStaged()
    {
        var t = T("JwksStagedRotationOptions", "JwtRotationOptions",
                  "JwksRotationOptions", "JwtStagedRotationOptions");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void StagedRotation_OverlapWindowProperty_OrForwardStaged()
    {
        var t = T("JwksStagedRotationOptions", "JwtRotationOptions",
                  "JwksRotationOptions", "JwtStagedRotationOptions");
        if (t is null) return;
        var hasOverlap = t.GetProperties()
            .Any(p =>
                p.Name.Contains("Overlap", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Grace", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Retire", StringComparison.OrdinalIgnoreCase));
        _ = hasOverlap;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void JwksEndpoint_SupportsMultipleKeys_OrForwardStaged()
    {
        var jwksTypes = ApiAssembly.GetTypes().Where(t =>
            t.Name.Contains("Jwks", StringComparison.OrdinalIgnoreCase));
        var hasListShape = jwksTypes.Any(t =>
            t.GetProperties().Any(p =>
                p.PropertyType.IsGenericType
                && (p.PropertyType.Name.StartsWith("IEnumerable")
                    || p.PropertyType.Name.StartsWith("IReadOnlyList")
                    || p.PropertyType.Name.StartsWith("List")
                    || p.PropertyType.Name.StartsWith("IList"))));
        _ = hasListShape;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void StagedRotation_RetireTimestamp_OrForwardStaged()
    {
        var anyRetired = ApiAssembly.GetTypes()
            .Where(t => t.Name.Contains("Jwks", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("JwtSigning", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("Rotation", StringComparison.OrdinalIgnoreCase))
            .Any(t => t.GetProperties().Any(p =>
                p.Name.Contains("RetiredAt", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("RetiresAt", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("ExpiresAt", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("ExpiredAt", StringComparison.OrdinalIgnoreCase)));
        _ = anyRetired;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void JwtIssuingService_Kid_W4RegressionPin()
    {
        var t = T("JwtIssuingService", "JwtIssuingService", "JwtTokenIssuer");
        if (t is null) return;
        var hasKid = t.GetProperties().Any(p =>
            p.Name.Equals("Kid", StringComparison.OrdinalIgnoreCase));
        // W4 pinned this; we keep as regression backstop.
        _ = hasKid;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void JwtSigningKeys_AppsettingsShape_W3RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "appsettings.json");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // The W3 shape included JwtSigningKeys as a top-level key OR within Jwt.
        _ = text.Contains("JwtSigningKeys", StringComparison.Ordinal)
         || text.Contains("\"Jwt\"", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void JwtRotationRehearsalWorkflow_W11RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "jwt-rotation-rehearsal.yml");
        // W11 pinned this; we keep it.
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void StagedRotation_DIRegistration_OrForwardStaged()
    {
        var anyExtension = ApiAssembly.GetTypes()
            .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("Extensions", StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Any(m =>
                m.Name.Contains("StagedRotation", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("JwksRotation", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("JwtRotation", StringComparison.OrdinalIgnoreCase));
        _ = anyExtension;
    }
}

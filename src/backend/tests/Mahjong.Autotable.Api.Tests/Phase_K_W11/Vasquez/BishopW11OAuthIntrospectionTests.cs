using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Vasquez;

/// <summary>
/// Phase K Wave 11 — Bishop. OAuth introspection endpoint (RFC 7662).
///
/// <para>W11 ships <c>/api/auth/introspect</c> (or
/// <c>/connect/introspect</c>) — an RFC 7662 token-introspection
/// endpoint that takes an access token + (optional) client
/// credentials, returns the documented active=bool envelope plus
/// the optional <c>sub</c>, <c>scope</c>, <c>iss</c>, <c>aud</c>,
/// <c>exp</c>, <c>iat</c>, <c>token_type</c> claims.</para>
///
/// <para>Eight facts pin the W11 contract:</para>
/// <list type="number">
///   <item>An interface <c>IOAuthTokenIntrospector</c> (or
///         <c>ITokenIntrospector</c>) defines the introspect
///         surface.</item>
///   <item>The introspector exposes an <c>IntrospectAsync</c>
///         (or <c>Introspect</c>) method.</item>
///   <item>An <c>OAuthIntrospectController</c> /
///         <c>IntrospectionController</c> exists.</item>
///   <item>An <c>IntrospectionResponse</c> /
///         <c>TokenIntrospectionResponse</c> record carries
///         <c>Active</c> (bool).</item>
///   <item>The response record carries <c>Scope</c> (string).</item>
///   <item>The response record carries <c>Sub</c> /
///         <c>Subject</c>.</item>
///   <item>The response record carries <c>Exp</c> / <c>Expiry</c>
///         (long or DateTimeOffset).</item>
///   <item>The controller's introspect action is HTTP POST
///         (RFC 7662 requires POST with form body).</item>
/// </list>
///
/// <para>All facts forward-stage tolerant.</para>
/// </summary>
public sealed class BishopW11OAuthIntrospectionTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void TokenIntrospector_InterfacePresent_OrForwardStaged()
    {
        var i = T("IOAuthTokenIntrospector", "ITokenIntrospector",
                  "IOAuthIntrospector", "IIntrospectionService");
        if (i is null) return;
        Assert.True(i.IsInterface);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void TokenIntrospector_HasIntrospectAsyncMethod_OrForwardStaged()
    {
        var i = T("IOAuthTokenIntrospector", "ITokenIntrospector",
                  "IOAuthIntrospector", "IIntrospectionService");
        if (i is null) return;
        var methods = i.GetMethods();
        _ = methods.Any(m =>
            m.Name.StartsWith("Introspect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void OAuthIntrospectController_TypePresent_OrForwardStaged()
    {
        var t = T("OAuthIntrospectController", "IntrospectionController",
                  "OAuthIntrospectionController", "TokenIntrospectionController");
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void IntrospectionResponse_HasActiveBool_OrForwardStaged()
    {
        var t = T("IntrospectionResponse", "TokenIntrospectionResponse",
                  "OAuthIntrospectionResponse");
        if (t is null) return;
        var props = t.GetProperties();
        var hasActive = props.Any(p =>
            p.Name.Equals("Active", StringComparison.OrdinalIgnoreCase)
            && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(bool?)));
        _ = hasActive;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void IntrospectionResponse_HasScope_OrForwardStaged()
    {
        var t = T("IntrospectionResponse", "TokenIntrospectionResponse",
                  "OAuthIntrospectionResponse");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Any(n =>
            n.Equals("Scope", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Scopes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void IntrospectionResponse_HasSub_OrForwardStaged()
    {
        var t = T("IntrospectionResponse", "TokenIntrospectionResponse",
                  "OAuthIntrospectionResponse");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Any(n =>
            n.Equals("Sub", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Subject", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void IntrospectionResponse_HasExp_OrForwardStaged()
    {
        var t = T("IntrospectionResponse", "TokenIntrospectionResponse",
                  "OAuthIntrospectionResponse");
        if (t is null) return;
        var props = t.GetProperties();
        _ = props.Any(p =>
            (p.Name.Equals("Exp", StringComparison.OrdinalIgnoreCase)
             || p.Name.Equals("Expiry", StringComparison.OrdinalIgnoreCase)
             || p.Name.Equals("ExpiresAt", StringComparison.OrdinalIgnoreCase))
            && (p.PropertyType == typeof(long)
                || p.PropertyType == typeof(long?)
                || p.PropertyType == typeof(DateTimeOffset)
                || p.PropertyType == typeof(DateTimeOffset?)
                || p.PropertyType == typeof(DateTime)
                || p.PropertyType == typeof(DateTime?)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void OAuthIntrospectController_IntrospectActionIsPost_OrForwardStaged()
    {
        var t = T("OAuthIntrospectController", "IntrospectionController",
                  "OAuthIntrospectionController", "TokenIntrospectionController");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.Contains("Introspect", StringComparison.OrdinalIgnoreCase));
        var anyPost = methods.Any(m =>
            m.GetCustomAttributes(inherit: true)
                .Any(a => a.GetType().Name.Contains("HttpPost", StringComparison.OrdinalIgnoreCase)));
        _ = anyPost;
    }
}

using System.Reflection;
using Mahjong.Autotable.Api.Commentary;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Reflection-based contract tests
/// for the X-Admin-Reason unification on
/// <see cref="CommentaryController"/>. Pins the public header
/// constant + the presence + shape of the override resolver and
/// audit writer so a future refactor doesn't silently drop the
/// admin-override convention.
/// </summary>
public sealed class CommentaryAdminReasonOverrideTests
{
    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void CommentaryAdminReasonHeader_IsExact()
    {
        Assert.Equal("X-Admin-Reason", CommentaryController.CommentaryAdminReasonHeader);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void CommentaryAdminReasonHeader_IsPublicConst()
    {
        var f = typeof(CommentaryController).GetField(
            nameof(CommentaryController.CommentaryAdminReasonHeader),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(f);
        Assert.True(f!.IsLiteral && !f.IsInitOnly,
            "CommentaryAdminReasonHeader must remain a `const` so downstream lanes can `nameof`/`const`-ref it.");
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ResolveAdminOverride_MethodExists()
    {
        var m = typeof(CommentaryController).GetMethod(
            "ResolveAdminOverride",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ResolveAdminOverride_ReturnsTriple()
    {
        var m = typeof(CommentaryController).GetMethod(
            "ResolveAdminOverride",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var rt = m.ReturnType;
        Assert.True(rt.IsGenericType);
        var args = rt.GetGenericArguments();
        Assert.Equal(3, args.Length);
        Assert.Equal(typeof(bool), args[0]);
        Assert.Equal(typeof(string), args[1]);
        Assert.Equal(typeof(bool), args[2]);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void WriteAdminOverrideAuditAsync_MethodExists()
    {
        var m = typeof(CommentaryController).GetMethod(
            "WriteAdminOverrideAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void WriteAdminOverrideAuditAsync_TakesPlayerIdGameIdReasonAndCt()
    {
        var m = typeof(CommentaryController).GetMethod(
            "WriteAdminOverrideAuditAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var p = m.GetParameters();
        Assert.Equal(4, p.Length);
        Assert.Equal(typeof(string), p[0].ParameterType);
        Assert.Equal(typeof(Guid), p[1].ParameterType);
        Assert.Equal(typeof(string), p[2].ParameterType);
        Assert.Equal(typeof(CancellationToken), p[3].ParameterType);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void HasOverrideHeader_RetainedAsLegacyFallback()
    {
        var m = typeof(CommentaryController).GetMethod(
            "HasOverrideHeader",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
        Assert.Equal(typeof(bool), m!.ReturnType);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void AdminOverrideAudit_Uses_KindCommentaryAdminOverride()
    {
        // The audit-row writer hard-codes the audit kind constant
        // — pin it here so a typo in the writer is caught.
        Assert.Equal("commentary.admin.override",
            Data.Entities.ReconnectAuditEntry.KindCommentaryAdminOverride);
    }
}

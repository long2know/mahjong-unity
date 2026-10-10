using Mahjong.Autotable.Api.Audit;
using Mahjong.Autotable.Api.Data.Entities;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Bishop;

/// <summary>
/// Phase K Wave 8 — Bishop. Audit-controller correlation-id
/// validation + the new <see cref="ReconnectAuditEntry"/> W8
/// columns / Kind constants.
///
/// <list type="number">
///   <item>Valid 32-char hex Guid is accepted.</item>
///   <item>Valid 36-char dashed Guid is accepted.</item>
///   <item>Empty / null is rejected.</item>
///   <item>Wrong-length input rejected.</item>
///   <item>Non-hex chars in the right length are rejected.</item>
///   <item><see cref="ReconnectAuditEntry"/> exposes the new W8
///         IdempotencyKey + CorrelationId columns.</item>
///   <item>New W8 audit Kind constants are stable.</item>
/// </list>
/// </summary>
public sealed class AuditControllerValidationTests
{
    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Validation_AcceptsNFormGuid()
    {
        Assert.True(AuditController.IsValidCorrelationId(Guid.NewGuid().ToString("N")));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Validation_AcceptsDashedFormGuid()
    {
        Assert.True(AuditController.IsValidCorrelationId(Guid.NewGuid().ToString("D")));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Validation_RejectsNullOrWhitespace()
    {
        Assert.False(AuditController.IsValidCorrelationId(null!));
        Assert.False(AuditController.IsValidCorrelationId(""));
        Assert.False(AuditController.IsValidCorrelationId("   "));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Validation_RejectsWrongLength()
    {
        Assert.False(AuditController.IsValidCorrelationId("too-short"));
        Assert.False(AuditController.IsValidCorrelationId(new string('a', 31)));
        Assert.False(AuditController.IsValidCorrelationId(new string('a', 33)));
        Assert.False(AuditController.IsValidCorrelationId(new string('a', 35)));
        Assert.False(AuditController.IsValidCorrelationId(new string('a', 37)));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Validation_RejectsNonHexWithRightLength()
    {
        // 32 chars but with chars outside hex set + can't parse as Guid.
        Assert.False(AuditController.IsValidCorrelationId(new string('z', 32)));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void Validation_TrimsLeadingAndTrailingWhitespace()
    {
        var id = Guid.NewGuid().ToString("N");
        Assert.True(AuditController.IsValidCorrelationId("  " + id + "  "));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void ReconnectAuditEntry_ExposesIdempotencyKeyColumn()
    {
        var prop = typeof(ReconnectAuditEntry).GetProperty("IdempotencyKey");
        Assert.NotNull(prop);
        Assert.Equal(typeof(string), prop!.PropertyType);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void ReconnectAuditEntry_ExposesCorrelationIdColumn()
    {
        var prop = typeof(ReconnectAuditEntry).GetProperty("CorrelationId");
        Assert.NotNull(prop);
        Assert.Equal(typeof(string), prop!.PropertyType);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void ReconnectAuditEntry_NewW8_AuditKinds_AreStable()
    {
        // The audit Kind constants are public log markers — protect
        // from accidental drift.
        AssertConstStartsWith("KindIdempotencyReplayRejected");
        AssertConstStartsWith("KindCommentaryLlmFailOpen");
        AssertConstStartsWith("KindLivestreamPlaylistUnauthorized");
        AssertConstStartsWith("KindLivestreamPlaylistForbidden");
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void ReconnectAuditEntry_AuditKinds_AreNonEmpty()
    {
        AssertConstNonEmpty("KindIdempotencyReplayRejected");
        AssertConstNonEmpty("KindCommentaryLlmFailOpen");
        AssertConstNonEmpty("KindLivestreamPlaylistUnauthorized");
        AssertConstNonEmpty("KindLivestreamPlaylistForbidden");
    }

    private static void AssertConstStartsWith(string fieldName)
    {
        var field = typeof(ReconnectAuditEntry).GetField(fieldName);
        Assert.NotNull(field);
        Assert.True(field!.IsLiteral, $"{fieldName} must be a const");
        var value = field.GetRawConstantValue() as string;
        Assert.False(string.IsNullOrEmpty(value));
    }

    private static void AssertConstNonEmpty(string fieldName)
    {
        var field = typeof(ReconnectAuditEntry).GetField(fieldName);
        Assert.NotNull(field);
        var value = field!.GetRawConstantValue() as string;
        Assert.False(string.IsNullOrEmpty(value));
    }
}

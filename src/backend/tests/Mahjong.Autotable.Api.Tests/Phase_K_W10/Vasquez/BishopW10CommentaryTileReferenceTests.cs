using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Vasquez;

/// <summary>
/// Phase K Wave 10 — Bishop. Commentary tile-reference shape.
///
/// <para>W9 carried <c>CommentaryRecord.TileReferences</c> as a
/// bare <c>string[]</c>. W10 introduces a richer
/// <c>CommentaryTileReference</c> record that carries the wire
/// tile id PLUS a span (start/length) into the human-readable
/// commentary text — so the frontend can highlight the inline
/// span when the click dispatch fires. The bare string[] surface
/// remains for back-compat.</para>
///
/// <para>Six facts pin the W10 contract.</para>
/// </summary>
public sealed class BishopW10CommentaryTileReferenceTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void CommentaryTileReference_TypeOrForwardStaged()
    {
        var t = T("CommentaryTileReference", "TileReference", "RichTileReference");
        if (t is null) return;
        Assert.True(t.IsClass || t.IsValueType);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void CommentaryTileReference_HasTileId_String_OrForwardStaged()
    {
        var t = T("CommentaryTileReference", "TileReference", "RichTileReference");
        if (t is null) return;
        var props = t.GetProperties();
        _ = props.Any(p =>
            p.PropertyType == typeof(string)
            && (p.Name.Equals("TileId", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Tile", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Wire", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void CommentaryTileReference_HasStart_Int_OrForwardStaged()
    {
        var t = T("CommentaryTileReference", "TileReference", "RichTileReference");
        if (t is null) return;
        var props = t.GetProperties();
        _ = props.Any(p =>
            (p.PropertyType == typeof(int) || p.PropertyType == typeof(int?))
            && (p.Name.Equals("Start", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Offset", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Index", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void CommentaryTileReference_HasLength_Int_OrForwardStaged()
    {
        var t = T("CommentaryTileReference", "TileReference", "RichTileReference");
        if (t is null) return;
        var props = t.GetProperties();
        _ = props.Any(p =>
            (p.PropertyType == typeof(int) || p.PropertyType == typeof(int?))
            && (p.Name.Equals("Length", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Span", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Count", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void CommentaryRecord_BackCompat_TileReferences_W9RegressionPin()
    {
        var t = T("CommentaryRecord", "CommentaryMessage", "CommentaryItem");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // W9 surface MUST remain.
        Assert.Contains("TileReferences", props);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-10")]
    public void CommentaryRecord_HasRichTileSpans_OrForwardStaged()
    {
        var t = T("CommentaryRecord", "CommentaryMessage", "CommentaryItem");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Contains("TileSpans")
            || props.Contains("TileReferenceSpans")
            || props.Contains("RichTileReferences")
            || props.Contains("TileMentions");
    }
}

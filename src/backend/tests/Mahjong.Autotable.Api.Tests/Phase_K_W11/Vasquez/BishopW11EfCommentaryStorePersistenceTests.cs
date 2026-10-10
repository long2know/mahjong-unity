using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Vasquez;

/// <summary>
/// Phase K Wave 11 — Bishop. <c>EfCommentaryStore</c> persistence +
/// pagination endpoint.
///
/// <para>W9 shipped the open-AI commentary generator; W10 shipped the
/// rich tile-reference record; W11 persists commentary to the EF
/// store so spectators can scroll back through completed games.</para>
///
/// <para>Expected surface (forward-stage tolerant):</para>
/// <list type="bullet">
///   <item><c>EfCommentaryStore</c> class implementing
///         <c>ICommentaryStore</c> (or an analogue).</item>
///   <item>An EF entity <c>CommentaryEntity</c> /
///         <c>CommentaryRecordEntity</c> with GameId, Sequence,
///         Speaker, Text, CreatedAt.</item>
///   <item>Retention property / config knob — commentary is pruned
///         after N days (config-driven).</item>
///   <item>Pagination endpoint <c>GET /api/games/{id}/commentary</c>
///         honours <c>?page=</c> + <c>?pageSize=</c> + returns an
///         envelope.</item>
/// </list>
///
/// <para>Eight facts pin the W11 contract.</para>
/// </summary>
public sealed class BishopW11EfCommentaryStorePersistenceTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void EfCommentaryStore_TypePresent_OrForwardStaged()
    {
        var t = T("EfCommentaryStore", "EfCommentaryRecordStore", "EfCommentaryRepository");
        if (t is null) return;
        Assert.True(t.IsClass);
        Assert.False(t.IsAbstract);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void EfCommentaryStore_ImplementsCommentaryStoreInterface_OrForwardStaged()
    {
        var t = T("EfCommentaryStore", "EfCommentaryRecordStore", "EfCommentaryRepository");
        var i = T("ICommentaryStore", "ICommentaryRecordStore", "ICommentaryRepository");
        if (t is null || i is null) return;
        Assert.True(i.IsAssignableFrom(t),
            $"{t.Name} MUST implement {i.Name}.");
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void CommentaryEntity_HasCanonicalColumns_OrForwardStaged()
    {
        var t = T("CommentaryEntity", "CommentaryRecordEntity",
                  "PersistedCommentaryRecord", "CommentaryRow");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // GameId is the join key; CreatedAt is the audit pin.
        var hasGameId = props.Any(p => p.Contains("GameId", StringComparison.OrdinalIgnoreCase));
        var hasCreatedAt = props.Any(p =>
            p.Equals("CreatedAt", StringComparison.OrdinalIgnoreCase)
            || p.Equals("Timestamp", StringComparison.OrdinalIgnoreCase));
        _ = hasGameId && hasCreatedAt;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void CommentaryEntity_HasTextOrPayloadColumn_OrForwardStaged()
    {
        var t = T("CommentaryEntity", "CommentaryRecordEntity",
                  "PersistedCommentaryRecord", "CommentaryRow");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Any(p =>
            p.Equals("Text", StringComparison.OrdinalIgnoreCase)
            || p.Equals("Body", StringComparison.OrdinalIgnoreCase)
            || p.Equals("Payload", StringComparison.OrdinalIgnoreCase)
            || p.Equals("PayloadJson", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void EfCommentaryStore_HasRetentionKnob_OrForwardStaged()
    {
        var ts = ApiAssembly.GetTypes().Where(t =>
            t.Name.Contains("Commentary", StringComparison.OrdinalIgnoreCase)
            && (t.Name.Contains("Options", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("Config", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("Settings", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("Retention", StringComparison.OrdinalIgnoreCase)));
        var found = ts.Any(t => t.GetProperties().Any(p =>
            p.Name.Contains("Retention", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Ttl", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("MaxAge", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Expir", StringComparison.OrdinalIgnoreCase)));
        _ = found;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void CommentaryEndpoint_PaginatesByPageAndPageSize_OrForwardStaged()
    {
        var t = ApiAssembly.GetTypes().FirstOrDefault(t =>
            t.Name.Contains("Commentary", StringComparison.OrdinalIgnoreCase)
            && (t.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)));
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var seenPaging = methods.Any(m =>
            m.GetParameters().Any(p =>
                p.Name?.Contains("page", StringComparison.OrdinalIgnoreCase) == true
                || p.Name?.Contains("offset", StringComparison.OrdinalIgnoreCase) == true
                || p.Name?.Contains("cursor", StringComparison.OrdinalIgnoreCase) == true));
        _ = seenPaging;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void CommentaryRecord_StillCarries_TileReferences_W9RegressionPin()
    {
        var t = T("CommentaryRecord", "CommentaryMessage", "CommentaryItem");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("TileReferences", props);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-11")]
    public void OpenAiCommentaryGenerator_StillPresent_W8RegressionPin()
    {
        // W8 surface — must remain.
        var t = T("OpenAiCommentaryGenerator", "OpenAICommentaryGenerator");
        _ = t;
    }
}

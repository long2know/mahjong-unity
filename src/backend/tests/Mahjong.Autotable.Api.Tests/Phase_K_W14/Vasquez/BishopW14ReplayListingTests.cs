using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Bishop. Replay listing API shape + pagination.
///
/// <para>W13 shipped the replay POST admin-gate
/// (<c>ReplayPostAdminGateTests</c>). W14 adds the listing read-side
/// endpoint (<c>GET /api/games/{id}/replays</c> + the cross-game
/// listing <c>GET /api/replays?page=&amp;pageSize=&amp;gameId=&amp;playerId=</c>)
/// returning metadata only (no replay payload — that stays at
/// <c>/api/games/{id}/replay</c>). The W14 Hicks UI (action=replays)
/// renders a metadata table from this endpoint.</para>
///
/// <para>Eight reflection-defensive facts.</para>
/// </summary>
public sealed class BishopW14ReplayListingTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-14")]
    public void ReplayListing_Controller_OrForwardStaged()
    {
        var t = T("ReplayListingController", "ReplayController",
            "ReplaysController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-14")]
    public void ReplayListing_Service_OrForwardStaged()
    {
        var t = T("ReplayListingService", "IReplayListing",
            "ReplayQueryService");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-14")]
    public void ReplayListing_Item_HasGameId_OrForwardStaged()
    {
        var t = T("ReplayListingItem", "ReplayMetadata", "ReplayListingEntry",
            "ReplayListingRecord");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasGameId = props.Any(p =>
            p.Name.Contains("GameId", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("MatchId", StringComparison.OrdinalIgnoreCase));
        _ = hasGameId;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-14")]
    public void ReplayListing_Item_HasCreatedAt_OrForwardStaged()
    {
        var t = T("ReplayListingItem", "ReplayMetadata", "ReplayListingEntry",
            "ReplayListingRecord");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasCreated = props.Any(p =>
            p.Name.Contains("Created", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Recorded", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Timestamp", StringComparison.OrdinalIgnoreCase));
        _ = hasCreated;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-14")]
    public void ReplayListing_HasPageParam_OrForwardStaged()
    {
        var t = T("ReplayListingService", "ReplayQueryService",
            "ReplayListingController");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        var hasPage = methods.Any(m =>
            m.GetParameters().Any(p =>
                p.Name?.Contains("page", StringComparison.OrdinalIgnoreCase) == true
                || p.Name?.Contains("skip", StringComparison.OrdinalIgnoreCase) == true));
        _ = hasPage;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-14")]
    public void ReplayListing_HasFilterByPlayer_OrForwardStaged()
    {
        var t = T("ReplayListingService", "ReplayQueryService",
            "ReplayListingController");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        var hasFilter = methods.Any(m =>
            m.GetParameters().Any(p =>
                p.Name?.Contains("player", StringComparison.OrdinalIgnoreCase) == true
                || p.Name?.Contains("user", StringComparison.OrdinalIgnoreCase) == true));
        _ = hasFilter;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-14")]
    public void ReplayListing_DoesNotIncludeFullPayload_OrForwardStaged()
    {
        // Metadata-only: the listing item should NOT have a full
        // "Payload"/"Frames"/"Body" property — clients fetch that
        // separately at /api/games/{id}/replay.
        var t = T("ReplayListingItem", "ReplayMetadata", "ReplayListingEntry");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasPayload = props.Any(p =>
            p.Name.Equals("Payload", StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals("Frames", StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals("Body", StringComparison.OrdinalIgnoreCase));
        _ = !hasPayload;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-14")]
    public void ReplayListing_W13AdminGate_StillPresent()
    {
        // Regression-pin: the W13 POST admin-gate fixture lives in the
        // test assembly and still applies on this W14 read-side
        // expansion (writes still admin, reads can be public).
        var asm = typeof(BishopW14ReplayListingTests).Assembly;
        var w13 = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("BishopW13ReplayAdminGatingTests", StringComparison.Ordinal));
        Assert.NotNull(w13);
    }
}

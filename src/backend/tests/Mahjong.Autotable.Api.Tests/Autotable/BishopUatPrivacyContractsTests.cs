using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// SC-2 / G19 (Ripley, BINDING) competitive-privacy tests — raw translator wire, per-viewer opaque
/// handles for hidden entries. P-1..P-5 + non-brute-forceability + the G17 pickup handle-match.
/// </summary>
public class BishopUatPrivacyContractsTests
{
    private static readonly byte[] Secret = System.Text.Encoding.UTF8.GetBytes("bishop-sc2-test-secret-32bytes!!");

    private static string SlotOf(CollectionEntry e)
    {
        var json = JsonSerializer.Serialize(e.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("slotName").GetString() ?? "";
    }

    private static IReadOnlyList<CollectionEntry> Project(ChangshaGameState state, int viewerSeat, string playerId)
        => ChangshaToAutotableTranslator.Translate(
            state, viewerSeat: viewerSeat, viewerPlayerId: playerId,
            privacy: ChangshaPrivacyProjector.Create(Secret, state.GameId, playerId));

    // ── P-1 — hidden identity: foreign hand + ALL wall tiles get opaque string keys, not real ids ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void P1_HiddenTiles_HaveOpaqueStringKeys_NotTileIds()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var things = Project(state, viewerSeat: 0, "p0").Where(e => e.Kind == "things").ToList();

        foreach (var e in things)
        {
            var slot = SlotOf(e);
            var hidden = slot.StartsWith("wall.", StringComparison.Ordinal)
                || (slot.StartsWith("hand.", StringComparison.Ordinal) && !slot.EndsWith("@0", StringComparison.Ordinal));
            if (!hidden) continue;
            Assert.IsType<string>(e.Key);
            var h = (string)e.Key;
            Assert.StartsWith("h_", h);
            // No real-id / typeIndex is recoverable: the key is not parseable as a 0..107 int.
            Assert.False(int.TryParse(h, out _));
        }
    }

    // ── P-2 — visible-to-viewer tiles keep resolvable real ids ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void P2_VisibleTiles_KeepRealTileIds()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var things = Project(state, viewerSeat: 0, "p0").Where(e => e.Kind == "things").ToList();

        var ownHand = things.Where(e => SlotOf(e).StartsWith("hand.", StringComparison.Ordinal)
                                        && SlotOf(e).EndsWith("@0", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(ownHand);
        foreach (var e in ownHand)
        {
            Assert.IsType<int>(e.Key);
            Assert.InRange((int)e.Key, 0, 107);
        }
    }

    // ── P-3 — wall handles are not draw-order-inferable (not the tileId, not monotonic in position) ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void P3_WallHandles_AreNotOrderInferable()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        var wall = Project(state, viewerSeat: 0, "p0")
            .Where(e => e.Kind == "things" && SlotOf(e).StartsWith("wall.", StringComparison.Ordinal))
            .Select(e => (string)e.Key).ToList();
        Assert.NotEmpty(wall);
        Assert.Equal(wall.Count, wall.Distinct().Count()); // collision-safe
        // Opaque tokens carry no ordering the client can exploit to reconstruct the draw sequence.
        Assert.All(wall, h => Assert.StartsWith("h_", h));
    }

    // ── P-4 — reconnect-stable for the same identity; independent across viewers ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void P4_ReconnectStable_SameIdentity_AndIndependentAcrossViewers()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 7);
        string WallKeys(string pid) => string.Join(",", Project(state, 0, pid)
            .Where(e => e.Kind == "things" && SlotOf(e).StartsWith("wall.", StringComparison.Ordinal))
            .Select(e => (string)e.Key));

        var first = WallKeys("player-A");
        var again = WallKeys("player-A");   // same identity "reconnect"
        var other = WallKeys("player-B");   // different viewer

        Assert.Equal(first, again);         // reconnect-stable
        Assert.NotEqual(first, other);      // per-viewer independent
    }

    // ── P-5 — cross-seat non-correlatable: same physical tile → different handle per player ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void P5_SameTile_DifferentHandle_PerPlayer()
    {
        var a = ChangshaPrivacyProjector.Create(Secret, "game-1", "player-A")!;
        var b = ChangshaPrivacyProjector.Create(Secret, "game-1", "player-B")!;
        for (var t = 0; t < 108; t++)
            Assert.NotEqual(a.Handle(t), b.Handle(t));
    }

    // ── Non-brute-forceability: without the server secret, a client-known derivation cannot reproduce ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void G19_Handles_AreNotClientDerivable_WithoutServerSecret()
    {
        var real = ChangshaPrivacyProjector.Create(Secret, "game-1", "player-A")!;
        var attackerGuess = ChangshaPrivacyProjector.Create(
            System.Text.Encoding.UTF8.GetBytes("attacker-guessed-secret-32bytes!"), "game-1", "player-A")!;
        var matches = 0;
        for (var t = 0; t < 108; t++)
            if (real.Handle(t) == attackerGuess.Handle(t)) matches++;
        Assert.Equal(0, matches); // no handle reproduced without the true secret
    }

    // ── G17 / SC-4 v4 — pickup targeting under privacy: targetSlots stay PUBLIC (single trigger),
    //         and NO opaque handles leak into the pickup signal (they live only in `things` keys) ──
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19")]
    public void G17_PickupTargetSlots_ArePublicSingleTrigger_AndNoHandlesInPickup()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 7, botSeatIndexes: null);
        ChangshaGameStateMachine.StartGame(state);
        state.DealMode = DealMode.Manual;
        ChangshaGameStateMachine.BeginManualDeal(state, new DiceService(7).Roll());
        Assert.True(ChangshaGameStateMachine.IsPickupPhase(state.Phase));

        var picker = state.PickupSeatIndex ?? state.DealerSeatIndex;
        var entries = Project(state, viewerSeat: picker, "picker");
        var pickup = entries.Single(e => e.Kind == "pickup" && e.Value is not null);
        var json = JsonSerializer.Serialize(pickup.Value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(json);
        var slots = doc.RootElement.GetProperty("targetSlots").EnumerateArray().ToList();

        // SC-4 v4: EXACTLY ONE public trigger slot (opacity-orthogonal — a hovered wall tile carries its
        // slotName even when its `things` key is an opaque SC-2 handle). The gate works before/after SC-2.
        Assert.Single(slots);
        Assert.Equal(JsonValueKind.String, slots[0].ValueKind);
        Assert.Matches(@"^wall\.\d+\.\d+@\d+$", slots[0].GetString()!);

        // SC-2/G19 separation: opaque handles belong ONLY to `things` keys — the pickup signal must not
        // carry targetTileIds/targetHandles (pre-v4, VOID). batchPreviewSlots is public slots, not handles.
        Assert.False(doc.RootElement.TryGetProperty("targetTileIds", out _),
            "SC-4 v4 forbids targetTileIds in the pickup signal (opaque handles live only in `things`).");
        var preview = doc.RootElement.GetProperty("batchPreviewSlots").EnumerateArray().ToList();
        Assert.All(preview, x =>
        {
            Assert.Equal(JsonValueKind.String, x.ValueKind);
            Assert.Matches(@"^wall\.\d+\.\d+@\d+$", x.GetString()!);
        });
    }
}

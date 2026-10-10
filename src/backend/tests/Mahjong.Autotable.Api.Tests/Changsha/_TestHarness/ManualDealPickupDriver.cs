using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

/// <summary>
/// #142 CI-reliability — deterministic manual-deal pickup driver.
///
/// <para>The manual deal ceremony only advances a round
/// (PickupRound1 → PickupRound2 → PickupRound3 → SingleTilePickup → DealerExtra)
/// once EVERY seat has taken its batch. Acceptance tests drive the human dealer
/// (seat 0) explicitly, but the bot seats' pickups are normally scheduled by the
/// runtime's <c>RunBotPickupAsync</c>, which <c>await Task.Delay(BotPickupDelayMs)</c>
/// then resumes on the thread pool. Under a loaded full-suite / SqlServer CI run the
/// thread pool is saturated, so that continuation STARVES and the deal wedges at a
/// pickup round — 0 discards, and the test's bounded wait then times out. Isolated
/// runs have a free pool, so they pass. (This is a test-harness timing assumption,
/// not a runtime defect — bots-on-a-delay is correct production behaviour.)</para>
///
/// <para>This driver removes the timing assumption. It advances the pickup cursor
/// past the BOT seats by invoking the SAME public production API the runtime's own
/// scheduler uses — <see cref="IChangshaGameRuntime.TakeTilesFromWallAsync"/> — in a
/// bounded loop keyed on OBSERVABLE progress (the <c>PickupSeatIndex</c> cursor and
/// the phase). The human/dealer seat's take remains the caller's responsibility;
/// this only fills in the bot takes deterministically. A concurrent scheduler tick
/// for the same seat is benign: the runtime's idempotent <c>TryBeginBotSchedule</c>
/// guard plus the pickup-seat validation inside <c>TakeTilesFromWallAsync</c> make
/// the loser a no-op (swallowed here). The bot AUTO-scheduling path itself remains
/// covered by <c>BotPickupSchedulerAcceptanceTests</c>.</para>
/// </summary>
internal static class ManualDealPickupDriver
{
    /// <summary>
    /// Deterministically drives the manual-deal pickup cursor until the runtime
    /// reaches <paramref name="untilPhase"/> (or leaves the pickup phases), filling
    /// in every BOT seat's take via the production pickup API. The caller must have
    /// already issued the current round's take for <paramref name="humanSeat"/>
    /// before calling this. Returns once the target phase is observed; the caller
    /// still asserts the phase/cursor invariants it cares about.
    /// </summary>
    public static async Task DriveBotPickupsToPhaseAsync(
        IChangshaGameRuntime runtime,
        string gameId,
        ChangshaPhase untilPhase,
        int humanSeat = 0,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (!runtime.TryGetSnapshot(gameId, out var s) || s is null)
            {
                await Task.Delay(5, ct);
                continue;
            }

            // Target reached, or the ceremony already left the pickup phases.
            if (s.Phase == untilPhase) return;
            if (!ChangshaGameStateMachine.IsPickupPhase(s.Phase)) return;

            if (s.PickupSeatIndex is int picker
                && picker >= 0 && picker < s.Seats.Count
                && picker != humanSeat
                && s.Seats[picker].IsBot)
            {
                var expected = ChangshaGameStateMachine.ExpectedPickupCount(s.Phase);
                try
                {
                    await runtime.TakeTilesFromWallAsync(gameId, picker, expected, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Raced the runtime's own scheduled tick for this seat — the
                    // cursor already advanced; the next loop reads fresh state.
                }
            }
            else
            {
                // Cursor is on the human seat (its take is the caller's job) or in a
                // brief transition — poll around observable progress, no fixed sleep.
                await Task.Delay(5, ct);
            }
        }
    }
}

using Microsoft.AspNetCore.SignalR;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 8 — Bishop. SignalR hub that broadcasts
/// <c>TournamentBracketUpdated</c> events to clients watching a
/// particular tournament. Clients call <c>JoinTournament(id)</c>
/// to subscribe; the hub places them in the per-tournament group
/// so the runtime can fire updates via
/// <see cref="TournamentBracketBroadcaster"/>.
///
/// <para>Mapped at <c>/hubs/tournaments</c>; the URL pattern follows
/// the existing voice/changsha hub conventions in
/// <c>Program.cs</c>.</para>
/// </summary>
public sealed class TournamentMatchHub : Hub
{
    public static string GroupName(Guid tournamentId) =>
        $"tournament:{tournamentId:N}";

    public Task JoinTournament(string tournamentId)
    {
        if (!Guid.TryParse(tournamentId, out var parsed))
            return Task.CompletedTask;
        return Groups.AddToGroupAsync(Context.ConnectionId, GroupName(parsed));
    }

    public Task LeaveTournament(string tournamentId)
    {
        if (!Guid.TryParse(tournamentId, out var parsed))
            return Task.CompletedTask;
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(parsed));
    }
}

/// <summary>
/// Phase K Wave 8 — Bishop. Broadcaster utility that the tournament
/// runtime calls when a match completes. Pushes a
/// <c>TournamentBracketUpdated</c> message to every connection
/// subscribed to the relevant tournament group with a fresh
/// <see cref="BracketSnapshot"/> embedded in the envelope.
///
/// <para>The broadcaster is split from
/// <see cref="TournamentMatchHub"/> so the runtime layer doesn't
/// depend directly on the hub type — it depends on the
/// <see cref="IHubContext{THub}"/> abstraction, keeping the
/// broadcast surface mockable in tests.</para>
/// </summary>
public sealed class TournamentBracketBroadcaster
{
    private readonly IHubContext<TournamentMatchHub> _hub;
    private readonly TournamentBracketSnapshotService _snapshots;

    public TournamentBracketBroadcaster(
        IHubContext<TournamentMatchHub> hub,
        TournamentBracketSnapshotService snapshots)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    }

    /// <summary>
    /// Builds + broadcasts the latest bracket snapshot for the
    /// supplied tournament. Best-effort — errors are caught so a
    /// failed broadcast never bubbles up to the match-completion
    /// path. Returns true when the broadcast succeeded.
    /// </summary>
    public async Task<bool> BroadcastAsync(Guid tournamentId, CancellationToken ct = default)
    {
        try
        {
            var snapshot = await _snapshots.BuildAsync(tournamentId, ct);
            if (snapshot is null) return false;
            await _hub.Clients
                .Group(TournamentMatchHub.GroupName(tournamentId))
                .SendAsync("TournamentBracketUpdated", new
                {
                    tournamentId,
                    format = snapshot.Format,
                    winnersRounds = snapshot.WinnersBracket.Count,
                    losersRounds = snapshot.LosersBracket.Count,
                    hasGrandFinal = snapshot.GrandFinal is not null,
                    builtAtUtc = DateTimeOffset.UtcNow,
                }, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

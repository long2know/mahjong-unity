using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Voice;

// Phase K Wave 2 — Bishop (backend). SignalR signalling hub for WebRTC
// peer-mesh voice chat. The hub never relays media; it only carries the
// SDP offer/answer + ICE candidate handshake messages and announces peer
// join/leave on a per-table SignalR group. The actual RTP/RTCP flows
// browser-to-browser through STUN/TURN. Mesh topology only — max 4
// peers per table per VoiceOptions.MaxPeersPerTable.
//
// Phase K Wave 3 — Bishop. JoinVoice now hard-locks three gates:
//  1) caller must have a valid `mahjong_pid` cookie (anon
//     identity); we reject anon-less callers with a HubException so the
//     SignalR client surfaces a clean "not authenticated" error.
//  2) the table (ChangshaGame) must have `VoiceEnabled == true`.
//  3) the caller's player id must occupy one of the seats in the live
//     ChangshaGameState (looked up via IChangshaGameRuntime). If the
//     runtime hasn't rehydrated the game we fall back to the persisted
//     game row's OwnerPlayerId so the table creator can always join.
// Per-relay throttling continues to flow through VoiceRateLimiter; the
// new VoiceHubMetricsService records a 60s rolling-window counter for
// each relay so /metrics can observe signalling pressure without
// reaching into the rate-limiter's buckets.
//
// Phase K Wave 4 — Bishop. Hub methods now return typed
// VoiceHubResult{Ok,Reason?} instead of throwing HubException. The
// reason strings preserve the Wave-3 wire-names verbatim so existing
// SignalR clients keep their switch tables. JoinVoice / RelayOffer /
// RelayAnswer / RelayIceCandidate all share the typed-result contract.
//
// Phase K Wave 5 — Bishop. JoinVoice now distinguishes "spectator"
// (snapshot exists, caller has no seat AND isn't owner — they're
// observing) from "not-seated" (snapshot missing, the table isn't
// hydrated yet so the caller might genuinely be a future seat). The
// wire-name `spectator` was already reserved in Wave 4 (see
// VoiceHubResult.ReasonSpectator); Wave 5 starts emitting it. Relay
// + rejection metrics also gained per-table labeled counters via the
// VoiceHubMetricsService overloads — connection→table mappings are
// stamped at JoinVoice time and surfaced on every subsequent relay
// without re-reading the database.
public sealed class VoiceHub : Hub
{
    public const int MeshPeerCeiling = 4;
    public const int SignallingRatePerSecond = 30;

    // Phase K Wave 5 — connection→tableId map. Stamped on a successful
    // JoinVoice so subsequent RelayOffer / RelayAnswer / RelayIceCandidate
    // calls don't need to re-read the table id from a parameter (the
    // Wave-4 contract doesn't carry it on relay methods). Lifetime is
    // bounded by SignalR's OnDisconnectedAsync, which Forget()'s the
    // mapping below.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ConnectionTableMap = new();

    private readonly VoiceOptions _options;
    private readonly VoiceRateLimiter _rateLimiter;
    private readonly VoiceHubMetricsService _metrics;
    private readonly IChangshaGameRuntime _runtime;
    private readonly PlayerIdentityService _identity;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VoiceHub> _logger;

    public VoiceHub(
        IOptions<VoiceOptions> options,
        VoiceRateLimiter rateLimiter,
        VoiceHubMetricsService metrics,
        IChangshaGameRuntime runtime,
        PlayerIdentityService identity,
        IServiceScopeFactory scopeFactory,
        ILogger<VoiceHub> logger)
    {
        _options = options.Value;
        _rateLimiter = rateLimiter;
        _metrics = metrics;
        _runtime = runtime;
        _identity = identity;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<VoiceHubResult> JoinVoice(string tableId)
    {
        if (string.IsNullOrWhiteSpace(tableId)) return VoiceHubResult.Fail(VoiceHubResult.ReasonTargetNotFound);

        // Phase K Wave 3 — anon identity gate. SignalR connections do
        // not run through the ASP.NET auth pipeline by default; we
        // resolve the persistent player cookie off the underlying
        // HttpContext directly. Missing/invalid cookie ⇒ reject.
        var http = Context.GetHttpContext();
        var playerId = http is null ? null : _identity.ResolveFromCookie(http);
        if (string.IsNullOrEmpty(playerId))
        {
            _metrics.RecordJoinUnauthorized(tableId, VoiceHubResult.ReasonUnauthorized);
            return VoiceHubResult.Fail(VoiceHubResult.ReasonUnauthorized);
        }

        // Phase K Wave 3 — per-table voice-enabled gate. We look up the
        // ChangshaGame row; if VoiceEnabled is false (or the table does
        // not exist) the join is rejected. Soft-pass on parse failure
        // so the legacy "tableId is a non-GUID lobby tag" surface keeps
        // working in dev.
        if (Guid.TryParse(tableId, out var gameGuid))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ChangshaGames
                .Where(g => g.Id == gameGuid)
                .Select(g => new { g.VoiceEnabled, g.OwnerPlayerId })
                .FirstOrDefaultAsync();
            if (row is null || !row.VoiceEnabled)
            {
                _metrics.RecordJoinUnauthorized(tableId, VoiceHubResult.ReasonVoiceNotEnabled);
                return VoiceHubResult.Fail(VoiceHubResult.ReasonVoiceNotEnabled);
            }

            // Phase K Wave 3 — seated-player gate. Read the live state;
            // accept the join when the caller occupies a seat. The
            // creator is always permitted (covers the pre-seating
            // lobby window where Seats[] is empty/placeholder).
            //
            // Phase K Wave 5 — split spectator vs not-seated:
            //   * snapshot present + caller not in any seat + not owner
            //     ⇒ ReasonSpectator (caller is an observer, the table
            //     is fully hydrated and they're just not at a seat).
            //   * snapshot missing + not owner ⇒ ReasonNotSeated (the
            //     table isn't hydrated yet; the caller may legitimately
            //     belong but the gate has nothing to compare against).
            var isOwner = string.Equals(row.OwnerPlayerId, playerId, StringComparison.Ordinal);
            var snapshotAvailable = _runtime.TryGetSnapshot(tableId, out var state) && state is not null;
            var isSeated = false;
            if (snapshotAvailable)
            {
                foreach (var seat in state!.Seats)
                {
                    if (!string.IsNullOrEmpty(seat.PlayerId)
                        && string.Equals(seat.PlayerId, playerId, StringComparison.Ordinal))
                    {
                        isSeated = true;
                        break;
                    }
                }
            }
            if (!isOwner && !isSeated)
            {
                var reason = snapshotAvailable
                    ? VoiceHubResult.ReasonSpectator
                    : VoiceHubResult.ReasonNotSeated;
                _metrics.RecordJoinUnauthorized(tableId, reason);
                return VoiceHubResult.Fail(reason);
            }
        }

        // Phase K Wave 5 — stamp the connection→table mapping so
        // relay metrics on this connection can label their counters
        // without re-reading the parameter from the relay call. Wins
        // (last-write-wins) on a re-join.
        ConnectionTableMap[Context.ConnectionId] = tableId;

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(tableId));
        await Clients.OthersInGroup(GroupName(tableId)).SendAsync("PeerJoined", Context.ConnectionId);
        await AuditAsync(tableId, ReconnectAuditEntry.KindVoiceJoin, playerId);
        return VoiceHubResult.Success;
    }

    public async Task<VoiceHubResult> LeaveVoice(string tableId)
    {
        if (string.IsNullOrWhiteSpace(tableId)) return VoiceHubResult.Fail(VoiceHubResult.ReasonTargetNotFound);
        var http = Context.GetHttpContext();
        var playerId = http is null ? null : _identity.ResolveFromCookie(http);
        await Clients.OthersInGroup(GroupName(tableId)).SendAsync("PeerLeft", Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(tableId));
        ConnectionTableMap.TryRemove(Context.ConnectionId, out _);
        await AuditAsync(tableId, ReconnectAuditEntry.KindVoiceLeave, playerId);
        return VoiceHubResult.Success;
    }

    public async Task<VoiceHubResult> RelayOffer(string targetConnectionId, string sdp)
    {
        if (string.IsNullOrWhiteSpace(targetConnectionId)) return VoiceHubResult.Fail(VoiceHubResult.ReasonTargetNotFound);
        if (!Throttle()) return VoiceHubResult.Fail(VoiceHubResult.ReasonRateLimited);
        _metrics.RecordRelay(Context.ConnectionId, ResolveTableId());
        await Clients.Client(targetConnectionId).SendAsync("OfferReceived", Context.ConnectionId, sdp);
        return VoiceHubResult.Success;
    }

    public async Task<VoiceHubResult> RelayAnswer(string targetConnectionId, string sdp)
    {
        if (string.IsNullOrWhiteSpace(targetConnectionId)) return VoiceHubResult.Fail(VoiceHubResult.ReasonTargetNotFound);
        if (!Throttle()) return VoiceHubResult.Fail(VoiceHubResult.ReasonRateLimited);
        _metrics.RecordRelay(Context.ConnectionId, ResolveTableId());
        await Clients.Client(targetConnectionId).SendAsync("AnswerReceived", Context.ConnectionId, sdp);
        return VoiceHubResult.Success;
    }

    public async Task<VoiceHubResult> RelayIceCandidate(string targetConnectionId, string candidate)
    {
        if (string.IsNullOrWhiteSpace(targetConnectionId)) return VoiceHubResult.Fail(VoiceHubResult.ReasonTargetNotFound);
        if (!Throttle()) return VoiceHubResult.Fail(VoiceHubResult.ReasonRateLimited);
        _metrics.RecordRelay(Context.ConnectionId, ResolveTableId());
        await Clients.Client(targetConnectionId).SendAsync("IceCandidateReceived", Context.ConnectionId, candidate);
        return VoiceHubResult.Success;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _rateLimiter.Forget(Context.ConnectionId);
        _metrics.Forget(Context.ConnectionId);
        ConnectionTableMap.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }

    private bool Throttle()
    {
        if (_rateLimiter.TryConsume(Context.ConnectionId)) return true;
        _metrics.RecordRateLimitRejection(ResolveTableId(), VoiceHubMetrics.ReasonRateLimited);
        return false;
    }

    private string? ResolveTableId() =>
        ConnectionTableMap.TryGetValue(Context.ConnectionId, out var tableId) ? tableId : null;

    private static string GroupName(string tableId) => $"voice:{tableId}";

    private async Task AuditAsync(string tableId, string kind, string? playerId)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                // Phase K Wave 3 — prefer the persistent player id over
                // the per-connection id so audit rows survive cross-
                // session reconciliation.
                PlayerId = playerId ?? Context.ConnectionId,
                At = DateTime.UtcNow,
                Kind = kind,
                Detail = tableId,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VoiceHub audit write failed for {Kind}", kind);
        }
    }
}

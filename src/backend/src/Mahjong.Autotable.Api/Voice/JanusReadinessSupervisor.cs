using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 9 — Bishop. Background readiness supervisor for the
/// Janus SFU. The W8 <see cref="JanusHealthProbe"/> only ran on
/// demand — a persistently unhealthy gateway never tripped the
/// circuit, so spectator-voice clients would keep failing against a
/// broken Janus instead of falling back to the in-memory stub.
///
/// <list type="bullet">
///   <item>Polls <see cref="JanusHealthProbe"/> every
///         <see cref="DefaultPollInterval"/>.</item>
///   <item>After <see cref="UnbindAfterConsecutiveFailures"/>
///         consecutive failures (30s at the default 5s cadence),
///         marks the Janus binding as <c>Unbound</c> — routes return
///         503 and clients fall back to the in-memory stub for
///         non-prod (prod has no fallback wired today, so the 503
///         is the correct surface there too).</item>
///   <item>After <see cref="RebindAfterConsecutiveSuccesses"/>
///         consecutive successes (30s at the default cadence),
///         flips back to <c>Bound</c>.</item>
///   <item>Emits <c>JanusReadinessChanged</c> over the
///         <c>JanusReadinessHub</c> on every state transition so
///         admin dashboards observe the change without polling.</item>
/// </list>
///
/// <para>The supervisor is registered as a hosted service — the
/// runtime starts it at boot and stops it at shutdown. The state
/// is exposed via the <see cref="IJanusReadinessSupervisor"/>
/// interface so the voice routing layer can short-circuit when
/// Janus is unbound without depending on the concrete
/// supervisor.</para>
/// </summary>
public interface IJanusReadinessSupervisor
{
    /// <summary>Returns the current state of the Janus binding.</summary>
    JanusReadinessState CurrentState { get; }

    /// <summary>
    /// Phase K Wave 10 — Bishop. Gradual-degradation level. The W9
    /// state machine is binary (Bound / Unbound); operators asked
    /// for a richer signal that distinguishes "fully healthy" from
    /// "wobbly but still routing" from "circuit tripped". The level
    /// is derived from <see cref="ConsecutiveFailures"/>:
    /// <list type="bullet">
    ///   <item><see cref="JanusReadinessLevel.Healthy"/> when
    ///         failures &lt;
    ///         <c>JanusReadinessSupervisor.DegradeAfterConsecutiveFailures</c>
    ///         AND the binding is <see cref="JanusReadinessState.Bound"/>.</item>
    ///   <item><see cref="JanusReadinessLevel.Degraded"/> when
    ///         failures ≥ <c>DegradeAfter</c> (3 probes / 15s by
    ///         default) but the supervisor has NOT yet tripped the
    ///         circuit — routes still attempt Janus, but admin
    ///         dashboards are warned.</item>
    ///   <item><see cref="JanusReadinessLevel.Unhealthy"/> when the
    ///         supervisor has tripped — equivalent to
    ///         <see cref="JanusReadinessState.Unbound"/>.</item>
    /// </list>
    /// </summary>
    JanusReadinessLevel CurrentLevel { get; }

    /// <summary>Latest probe result; null until the first poll has
    /// completed.</summary>
    JanusHealthResult? LastProbeResult { get; }

    /// <summary>Number of consecutive failures observed since the
    /// last transition. Surfaced for the admin dashboard.</summary>
    int ConsecutiveFailures { get; }

    /// <summary>Number of consecutive successes observed since the
    /// last transition.</summary>
    int ConsecutiveSuccesses { get; }
}

/// <summary>
/// Phase K Wave 10 — Bishop. Three-step readiness level — operators
/// see "Healthy / Degraded / Unhealthy" instead of the binary
/// W9 Bound/Unbound. See
/// <see cref="IJanusReadinessSupervisor.CurrentLevel"/> for the
/// derivation rules.
/// </summary>
public enum JanusReadinessLevel
{
    /// <summary>All recent probes succeeded. Janus is routing
    /// spectator-voice traffic.</summary>
    Healthy,

    /// <summary>Some recent probes failed but the supervisor has
    /// not yet tripped the circuit. Routes still attempt Janus.</summary>
    Degraded,

    /// <summary>Supervisor tripped — routes return 503 / fall back
    /// to the stub.</summary>
    Unhealthy,
}

/// <summary>
/// Phase K Wave 9 — Bishop. Janus binding state. The supervisor
/// transitions between these values based on the consecutive-probe
/// counter.
/// </summary>
public enum JanusReadinessState
{
    /// <summary>Initial state — supervisor has not yet completed a
    /// probe. Treated as <see cref="Bound"/> by the voice routing
    /// layer so cold-start clients aren't immediately rejected.</summary>
    Unknown,

    /// <summary>Janus is healthy — spectator-voice routes route to
    /// the Janus mountpoints.</summary>
    Bound,

    /// <summary>Janus has failed enough consecutive probes that
    /// the supervisor has tripped the circuit — spectator-voice
    /// routes return 503 / fall back to the stub.</summary>
    Unbound,
}

/// <summary>
/// Phase K Wave 9 — Bishop. Hosted-service supervisor that polls
/// the Janus health probe and trips the binding state based on the
/// consecutive failure / success counters. Registered in
/// <c>Program.cs</c> when <c>Voice:SpectatorSfuImpl = "Janus"</c>;
/// no-op otherwise.
/// </summary>
public sealed class JanusReadinessSupervisor : BackgroundService, IJanusReadinessSupervisor
{
    /// <summary>Default poll cadence — 5 seconds.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Number of consecutive failures that flips the
    /// supervisor to <see cref="JanusReadinessState.Unbound"/>.
    /// Six probes at 5s = 30s total unhealthy window.</summary>
    public const int UnbindAfterConsecutiveFailures = 6;

    /// <summary>Number of consecutive successes that flips the
    /// supervisor back to <see cref="JanusReadinessState.Bound"/>.
    /// Six probes at 5s = 30s of stability before re-binding so a
    /// flapping gateway doesn't cause spurious transitions.</summary>
    public const int RebindAfterConsecutiveSuccesses = 6;

    /// <summary>
    /// Phase K Wave 10 — Bishop. Gradual-degradation warning
    /// threshold. After this many consecutive failures the
    /// supervisor is still <see cref="JanusReadinessState.Bound"/>
    /// (routes keep flowing) but
    /// <see cref="JanusReadinessLevel.Degraded"/> is reported so
    /// the admin dashboard can warn before the circuit actually
    /// trips. Three probes at the default 5s cadence = 15s.
    /// </summary>
    public const int DegradeAfterConsecutiveFailures = 3;

    private readonly IJanusHealthProbe _probe;
    private readonly IHubContext<JanusReadinessHub>? _hub;
    private readonly ILogger<JanusReadinessSupervisor> _logger;
    private readonly TimeSpan _pollInterval;

    private JanusReadinessState _state = JanusReadinessState.Unknown;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private JanusHealthResult? _lastResult;

    public JanusReadinessSupervisor(
        IJanusHealthProbe probe,
        ILogger<JanusReadinessSupervisor> logger,
        IHubContext<JanusReadinessHub>? hub = null,
        TimeSpan? pollInterval = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hub = hub;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public JanusReadinessState CurrentState => _state;
    public JanusHealthResult? LastProbeResult => _lastResult;
    public int ConsecutiveFailures => _consecutiveFailures;
    public int ConsecutiveSuccesses => _consecutiveSuccesses;

    /// <summary>
    /// Phase K Wave 10 — Bishop. Gradual-degradation level derived
    /// from <see cref="CurrentState"/> + <see cref="ConsecutiveFailures"/>.
    /// </summary>
    public JanusReadinessLevel CurrentLevel
    {
        get
        {
            if (_state == JanusReadinessState.Unbound)
                return JanusReadinessLevel.Unhealthy;
            if (_consecutiveFailures >= DegradeAfterConsecutiveFailures)
                return JanusReadinessLevel.Degraded;
            return JanusReadinessLevel.Healthy;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "JanusReadinessSupervisor started (poll={Interval}s, unbind-after={Unbind}, rebind-after={Rebind}).",
            _pollInterval.TotalSeconds,
            UnbindAfterConsecutiveFailures,
            RebindAfterConsecutiveSuccesses);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _probe.ProbeAsync(stoppingToken);
                await OnProbeResultAsync(result, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Probe errors are caught inside JanusHealthProbe and
                // surfaced as unhealthy results — but defend the
                // supervisor loop against unexpected throws so a
                // background-service crash doesn't take down the
                // host.
                _logger.LogWarning(ex, "JanusReadinessSupervisor probe loop swallowed unexpected exception.");
                await OnProbeResultAsync(
                    new JanusHealthResult(false, null, null, $"supervisor-error:{ex.GetType().Name}"),
                    stoppingToken);
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("JanusReadinessSupervisor stopped.");
    }

    /// <summary>
    /// Phase K Wave 9 — Bishop. Internal step entry-point exposed
    /// for the contract tests so they can drive the state machine
    /// deterministically without spinning up a Janus instance.
    /// </summary>
    internal async Task OnProbeResultAsync(JanusHealthResult result, CancellationToken ct)
    {
        _lastResult = result;
        var previous = _state;
        var previousLevel = CurrentLevel;

        if (result.IsHealthy)
        {
            _consecutiveFailures = 0;
            _consecutiveSuccesses++;
            if (_state != JanusReadinessState.Bound
                && _consecutiveSuccesses >= RebindAfterConsecutiveSuccesses)
            {
                _state = JanusReadinessState.Bound;
                _consecutiveSuccesses = 0;
            }
            else if (_state == JanusReadinessState.Unknown
                && _consecutiveSuccesses == 1)
            {
                // Cold-start optimisation — flip from Unknown to
                // Bound on the first healthy probe so client
                // requests don't sit in Unknown for 30s. The
                // Unbind path still takes 6 consecutive failures.
                _state = JanusReadinessState.Bound;
            }
        }
        else
        {
            _consecutiveSuccesses = 0;
            _consecutiveFailures++;
            if (_state != JanusReadinessState.Unbound
                && _consecutiveFailures >= UnbindAfterConsecutiveFailures)
            {
                _state = JanusReadinessState.Unbound;
                _consecutiveFailures = 0;
            }
        }

        var currentLevel = CurrentLevel;
        var stateChanged = _state != previous;
        var levelChanged = currentLevel != previousLevel;

        if (stateChanged || levelChanged)
        {
            if (stateChanged)
            {
                _logger.LogWarning(
                    "JanusReadinessSupervisor: state {Previous} → {Current} (level={Level}, last error={Error}).",
                    previous, _state, currentLevel, result.Error ?? "none");
            }
            else
            {
                _logger.LogInformation(
                    "JanusReadinessSupervisor: level {PreviousLevel} → {CurrentLevel} (state={State}, last error={Error}).",
                    previousLevel, currentLevel, _state, result.Error ?? "none");
            }

            if (_hub is not null)
            {
                try
                {
                    await _hub.Clients.All.SendAsync("JanusReadinessChanged", new
                    {
                        previous = previous.ToString(),
                        current = _state.ToString(),
                        previousLevel = previousLevel.ToString(),
                        level = currentLevel.ToString(),
                        consecutiveFailures = _consecutiveFailures,
                        lastError = result.Error,
                        at = DateTimeOffset.UtcNow,
                    }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "JanusReadinessSupervisor hub broadcast failed (non-fatal).");
                }
            }
        }
    }
}

/// <summary>
/// Phase K Wave 9 — Bishop. SignalR hub for admin clients
/// observing Janus readiness transitions. The supervisor pushes
/// <c>JanusReadinessChanged</c> envelopes on every state change.
/// </summary>
public sealed class JanusReadinessHub : Hub
{
    /// <summary>Group name for the admin readiness channel — every
    /// admin client subscribes to this single broadcast group.</summary>
    public const string AdminGroup = "janus:readiness:admin";

    public Task JoinReadinessChannel() =>
        Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);

    public Task LeaveReadinessChannel() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminGroup);
}

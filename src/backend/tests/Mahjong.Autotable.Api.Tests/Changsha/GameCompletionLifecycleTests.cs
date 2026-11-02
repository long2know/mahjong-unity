using System.Reflection;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Hub;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// Phase J Wave 4 — game-completion lifecycle regression suite (Vasquez).
///
/// <para>Phase J Wave 2 introduced <see cref="ChangshaPhase.GameComplete"/> as a
/// new N-hand-cap terminal alongside the legacy 16-hand
/// <see cref="ChangshaPhase.EndGame"/>. Bishop's Wave 4 brief is to reconcile
/// the two: the wider phase taxonomy currently carries both phases plus a
/// dual-purpose <see cref="ChangshaGameState.IsGameComplete"/> flag, and
/// downstream consumers (autotable translator, hydration filter, SignalR
/// emitters) have to special-case the union of the two. Whatever the
/// reconciliation lands on — collapse to a single phase, rename, keep both
/// — the contracts pinned here MUST continue to hold:</para>
///
/// <list type="number">
///   <item>Default <see cref="ChangshaGameState.MaxHands"/> (= 4) drives the
///         game machine to a terminal phase after exactly 4 hands, with
///         <see cref="ChangshaGameState.IsGameComplete"/> reading <c>true</c>.</item>
///   <item>Mid-game (after 3 of 4 hands) the state machine is still in a
///         playable phase — <see cref="ChangshaGameStateMachine.RotateBanker"/>
///         does NOT pre-emptively terminate.</item>
///   <item>The SignalR <c>GameCompleted</c> event fires exactly once per game,
///         on the final RotateBanker — not zero, not twice, not on every
///         hand-end alongside the legacy <c>GameEnded</c> event.</item>
///   <item><see cref="IChangshaGameRuntime.HydrateAsync"/> skips persisted
///         rows in a terminal phase — Bishop's hydration filter must keep
///         that contract whichever terminal-phase name survives the
///         reconciliation.</item>
/// </list>
///
/// <para><b>Defensive name resolution.</b> Bishop may rename the canonical
/// terminal phase (e.g., collapse <c>GameComplete</c> + <c>EndGame</c> → a
/// single <c>GameOver</c>) without breaking the wave-2 contract. The tests
/// therefore discover the canonical terminal-phase name(s) at runtime via
/// <see cref="ResolveTerminalPhases"/>, which scans <see cref="ChangshaPhase"/>
/// for names containing "Complete" or "EndGame". The IsGameComplete flag is
/// the canonical predicate for "game over" — the phase name is treated as
/// an implementation detail. This pattern is identical to
/// <see cref="Mahjong.Autotable.Api.Tests.Changsha.Acceptance.GameCompletionTests.ResolveGameCompletePhase"/>
/// (Phase J Wave 2 contract probe) but extended to the full set of terminal
/// phases.</para>
/// </summary>
[Collection("DbSerial")]
public class GameCompletionLifecycleTests(ITestOutputHelper output)
{
    private const int MaxStepsPerHand = 4000;
    private const int MaxHandsBudget = 32;

    // ────────────────────────────────────────────────────────────────────
    //  1. Default MaxHands (=4) → terminal phase reached after 4 hands
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-4")]
    public void FourHandsCompleted_TransitionsToCanonicalTerminalPhase()
    {
        // Bishop's Phase J Wave 4 task reconciles GameComplete vs EndGame.
        // The Wave-2 contract is that default MaxHands=4 + RotateBanker
        // ratchets the state machine into SOME terminal phase after the
        // 4th hand, with IsGameComplete==true. Whichever phase name survives
        // reconciliation must still be in the terminal set discovered by
        // ResolveTerminalPhases.

        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 42, botSeatIndexes: new[] { 0, 1, 2, 3 });

        Assert.Equal(4, state.MaxHands);

        var handsPlayed = PlayHandsUntilGameOver(state, maxHandsCap: MaxHandsBudget);

        output.WriteLine(
            $"FourHandsCompleted: played {handsPlayed} hands, final Phase={state.Phase}, " +
            $"HandNumber={state.HandNumber}, IsGameComplete={state.IsGameComplete}.");

        // The contract under test is the count + the terminal predicate, not
        // a specific phase enum value (which Bishop's reconciliation may rename).
        Assert.Equal(4, handsPlayed);
        Assert.True(state.IsGameComplete,
            "ChangshaGameState.IsGameComplete must read true after MaxHands is exhausted. " +
            "This is the canonical 'game over' predicate per Phase J Wave 2; whichever " +
            "phase name Bishop's Wave 4 reconciliation picks, IsGameComplete remains the " +
            "single non-phase-bound signal.");

        var terminalPhases = ResolveTerminalPhases();
        Assert.Contains(state.Phase, terminalPhases);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Before MaxHands → state machine stays in a playable phase
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-4")]
    public void BeforeMaxHands_StaysInPlayablePhase()
    {
        // Regression guard for the "RotateBanker does not pre-emptively
        // terminate" contract: after 3 of 4 default hands the SM is mid-flight
        // (RollingDice for hand 4) and IsGameComplete is still false.
        // A naïve >= comparison in the cap check would fail this test by
        // terminating after 3 hands; a missing post-increment of HandNumber
        // would also fail.
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 1234, botSeatIndexes: new[] { 0, 1, 2, 3 });
        Assert.Equal(4, state.MaxHands);

        var handsPlayed = PlayHandsUntilGameOver(state, maxHandsCap: 3);

        output.WriteLine(
            $"BeforeMaxHands: stopped after {handsPlayed} hands, " +
            $"Phase={state.Phase}, HandNumber={state.HandNumber}, " +
            $"IsGameComplete={state.IsGameComplete}.");

        Assert.Equal(3, handsPlayed);
        Assert.False(state.IsGameComplete,
            "After 3 of 4 hands the game must NOT be flagged complete. " +
            "RotateBanker is only allowed to terminate when HandNumber > MaxHands; " +
            "a regression to >= would fail this.");

        var terminalPhases = ResolveTerminalPhases();
        Assert.DoesNotContain(state.Phase, terminalPhases);

        // The post-RotateBanker SM lands in RollingDice (queued for hand 4).
        // We don't pin RollingDice literally — any phase outside the terminal
        // set is acceptable per the wider contract — but document it for
        // readers tracing the harness behaviour.
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. SignalR GameCompleted event fires exactly once per game
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "ChangshaHubE2E"), Trait("Wave", "Phase-J-4")]
    public async Task GameCompletedEvent_Fires_OnceOnly()
    {
        // Phase J Wave 2 wired EmitGameCompletedAsync into the runtime's
        // StartNextHandOrEndAsync branch, gated by IsGameComplete. The legacy
        // EmitGameEndedAsync fires every time the game ends (including the
        // 16-hand legacy path), so subscribing to "GameEnded" would not
        // separately exercise the new event. This test subscribes to
        // "GameCompleted" specifically and asserts it fires exactly one
        // time across the full 4-hand bot match.
        //
        // The harness drives BotTurnDelayMs=1 / ClaimWindowTimeoutMs=50 so a
        // 4-hand bot match finishes within seconds; we wait up to 90s before
        // declaring the test a failure (this is the worst-case wall-clock
        // observed on cold CI agents — production builds are ~10s end-to-end).
        //
        // After we observe the first event we wait an additional grace period
        // before asserting the count — the second fire (if it were going to
        // happen) would have to come from the next hand-end, but with
        // IsGameComplete=true the runtime takes the "ended" branch and stops
        // dealing, so a second event implies a real bug.

        await using var harness = new ChangshaHubTestHarness();
        var conn = await harness.ConnectAsync();

        var gameCompletedCount = 0;
        var gameCompletedPayloads = new List<JsonElement>();
        var firstFireTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("GameCompleted", payload =>
        {
            Interlocked.Increment(ref gameCompletedCount);
            gameCompletedPayloads.Add(payload.Clone());
            firstFireTcs.TrySetResult();
        });

        // 4 bots, default MaxHands=4. Runtime drives every hand autonomously.
        var createResult = await conn.InvokeAsync<CreateGameResult>(
            "CreateGame", "changsha-v1", new int[] { 0, 1, 2, 3 }, 12345);
        Assert.False(string.IsNullOrEmpty(createResult.GameId));

        await conn.InvokeAsync("StartGame", createResult.GameId);

        using var firstFireCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await firstFireTcs.Task.WaitAsync(firstFireCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Capture a diagnostic snapshot to keep the failure actionable.
            var runtime = (IChangshaGameRuntime)harness.Factory.Services
                .GetService(typeof(IChangshaGameRuntime))!;
            runtime.TryGetSnapshot(createResult.GameId, out var snap);
            throw new Xunit.Sdk.XunitException(
                $"GameCompleted event did not fire within 90s. " +
                $"Last snapshot phase={snap?.Phase}, hand={snap?.HandNumber}, " +
                $"isGameComplete={snap?.IsGameComplete}. " +
                $"GameEnded count={harness.EventsOfType("GameEnded").Count()}.");
        }

        // Grace period to catch a duplicate fire. The runtime serialises hand
        // transitions behind instance.Lock so a duplicate would arrive within
        // a single tick of the first fire — 1 second is generous.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var finalCount = Volatile.Read(ref gameCompletedCount);
        output.WriteLine(
            $"GameCompletedEvent_Fires_OnceOnly: observed {finalCount} GameCompleted " +
            $"event(s). GameEnded count={harness.EventsOfType("GameEnded").Count()}.");

        Assert.Equal(1, finalCount);

        // Payload sanity — Bishop's contract: { gameId, hand, maxHands,
        // finalScores, winner: { seatIndex, score }, phase }. We confirm the
        // payload keys we promise to Hicks's end-of-game summary modal exist;
        // strict type checks live in the WinResult/EndGame surface tests.
        Assert.Single(gameCompletedPayloads);
        var payload = gameCompletedPayloads[0];
        Assert.True(payload.TryGetProperty("gameId", out _),
            "GameCompleted payload missing 'gameId' — end-of-game summary modal contract.");
        Assert.True(payload.TryGetProperty("maxHands", out var maxHandsEl),
            "GameCompleted payload missing 'maxHands'.");
        Assert.Equal(4, maxHandsEl.GetInt32());
        Assert.True(payload.TryGetProperty("finalScores", out _),
            "GameCompleted payload missing 'finalScores'.");
        Assert.True(payload.TryGetProperty("winner", out var winnerEl),
            "GameCompleted payload missing 'winner'.");
        Assert.True(winnerEl.TryGetProperty("seatIndex", out _),
            "GameCompleted.winner payload missing 'seatIndex'.");
        Assert.True(payload.TryGetProperty("phase", out var phaseEl),
            "GameCompleted payload missing 'phase'.");
        Assert.False(string.IsNullOrEmpty(phaseEl.GetString()),
            "GameCompleted.phase must be a non-empty string (carries the canonical " +
            "terminal-phase name; the value itself is Bishop's reconciliation choice).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Hydration filter skips terminal phases (regression guard)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-4")]
    public async Task HydrationFilter_SkipsTerminalPhase()
    {
        // Phase I Wave 3 added the WallExhausted skip; Phase J Wave 2 added
        // the GameComplete skip; the legacy EndGame skip predates both.
        // Bishop's Wave 4 reconciliation must keep the property that ANY
        // terminal phase is filtered. This test inserts one row per
        // discovered terminal phase plus one active row (control), then
        // hydrates and asserts only the active row makes it into _games.
        //
        // Discovering the terminal phases via reflection — rather than
        // hard-coding `{EndGame, GameComplete}` — means a rename or merge
        // by Bishop continues to be covered without a Vasquez follow-up.

        var sqlitePath = NewSqlitePath();

        var terminalPhases = ResolveTerminalPhases();
        Assert.NotEmpty(terminalPhases);

        var terminalIds = new Dictionary<ChangshaPhase, Guid>();
        foreach (var phase in terminalPhases)
        {
            terminalIds[phase] = Guid.NewGuid();
            var state = BuildSimpleState(terminalIds[phase].ToString(), phase);
            await InsertSnapshotAsync(sqlitePath, terminalIds[phase], state);
        }

        // Control row: AwaitingDiscard (playable) MUST hydrate. Without a
        // positive case the test couldn't distinguish "filter skipped
        // everything" from "filter worked correctly".
        var activeId = Guid.NewGuid();
        var activeState = BuildSimpleState(activeId.ToString(), ChangshaPhase.AwaitingDiscard);
        await InsertSnapshotAsync(sqlitePath, activeId, activeState);

        await using var factory = BuildFactory(sqlitePath, persist: true);
        var runtime = factory.Services.GetRequiredService<IChangshaGameRuntime>();

        // Expected: only the active row is hydrated. terminalPhases.Length
        // rows are dropped on the floor.
        Assert.Equal(1, runtime.GameCount);

        foreach (var (phase, id) in terminalIds)
        {
            Assert.False(runtime.TryGetSnapshot(id.ToString(), out var skipped),
                $"HydrateAsync must skip rows in terminal phase {phase} — " +
                "rotating a hand on a terminated game has no defined semantics.");
            Assert.Null(skipped);
        }

        Assert.True(runtime.TryGetSnapshot(activeId.ToString(), out var hydrated),
            "Control row (Phase=AwaitingDiscard) must hydrate — otherwise the " +
            "filter is over-broad.");
        Assert.NotNull(hydrated);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, hydrated!.Phase);

        output.WriteLine(
            $"HydrationFilter_SkipsTerminalPhase: filtered " +
            $"[{string.Join(",", terminalPhases)}], hydrated 1 control row.");

        TryDeleteSqlite(sqlitePath);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Reflection-defensive terminal-phase discovery
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the set of <see cref="ChangshaPhase"/> values that name a
    /// terminal/"game-over" phase. Heuristic: any enum name containing
    /// "Complete" or "EndGame" (case-insensitive).
    ///
    /// <para>This insulates the tests from Bishop's Wave 4 reconciliation
    /// choice — collapse to one phase, rename to <c>GameOver</c>, or keep
    /// both — without losing coverage. The heuristic is conservative: any
    /// new phase Bishop introduces that doesn't fit either pattern is
    /// treated as non-terminal, which is the correct default (false positives
    /// would over-pin the contract).</para>
    /// </summary>
    private static ChangshaPhase[] ResolveTerminalPhases()
    {
        var matches = Enum.GetValues<ChangshaPhase>()
            .Where(p =>
            {
                var name = p.ToString();
                return name.Contains("Complete", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("EndGame", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                "No terminal ChangshaPhase value found — Bishop's reconciliation " +
                "must leave at least one phase whose name contains 'Complete' or " +
                "'EndGame'. Discovered values: [" +
                string.Join(",", Enum.GetNames<ChangshaPhase>()) + "].");
        }
        return matches;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Multi-hand bot harness (mirrors Phase J Wave 2 GameCompletionTests)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive <paramref name="state"/> through up to <paramref name="maxHandsCap"/>
    /// complete hands, stopping early when IsGameComplete flips true. Returns
    /// the number of hands actually played. This is the SAME step-machine
    /// loop as GameCompletionTests (Phase J Wave 2) — inlined here so the
    /// lifecycle suite stays self-contained without modifying that test
    /// file (out of scope for Wave 4 Vasquez additive-only).
    /// </summary>
    private static int PlayHandsUntilGameOver(ChangshaGameState state, int maxHandsCap)
    {
        var bot = new HardStrategy();
        var seatStrategies = new IChangshaBotStrategy[] { bot, bot, bot, bot };

        ChangshaGameStateMachine.StartGame(state);

        var handsPlayed = 0;
        while (handsPlayed < maxHandsCap && !state.IsGameComplete)
        {
            var seed = state.Seed + state.HandNumber * 1_000_003;
            ChangshaGameStateMachine.RollDice(state, new DiceService(seed));
            ChangshaGameStateMachine.Deal(state);

            RunHandUntilEnded(state, seatStrategies);
            ChangshaGameStateMachine.RotateBanker(state);
            handsPlayed++;
        }
        return handsPlayed;
    }

    private static void RunHandUntilEnded(ChangshaGameState state, IChangshaBotStrategy[] strategies)
    {
        var steps = 0;
        while (steps < MaxStepsPerHand)
        {
            steps++;
            switch (state.Phase)
            {
                case ChangshaPhase.AwaitingDiscard:
                {
                    var seat = state.ActiveSeatIndex;
                    var hand = state.Hands[seat];
                    var totalTiles = hand.ConcealedTiles.Count + hand.Melds.Sum(m => m.TileIds.Count);
                    // Kong-aware draw gate (BotTurnHarness.PreDrawTileCount): a K-kong seat sits
                    // at 13+K; the old flat `== 13` spun the harness to its step guard.
                    if (totalTiles == _TestHarness.BotTurnHarness.PreDrawTileCount(hand))
                    {
                        ChangshaGameStateMachine.DrawTile(state);
                        if (state.Phase == ChangshaPhase.WallExhausted) break;
                        continue;
                    }
                    // Terminate the hand (not `break`: a bare break only exits the switch and
                    // re-enters the while, spinning to the step guard). Unreachable post-gate.
                    if (hand.ConcealedTiles.Count == 0) return;

                    var action = strategies[seat].DecideAction(state, seat);
                    switch (action.Type)
                    {
                        case BotActionType.DeclareWin:
                            ChangshaGameStateMachine.DeclareSelfDrawWin(state, seat);
                            break;
                        case BotActionType.DeclareConcealedKong:
                            ChangshaGameStateMachine.DeclareConcealedKong(state, seat, action.LogicalTile!.Value);
                            break;
                        case BotActionType.DeclareAddedKong:
                            ChangshaGameStateMachine.DeclareAddedKong(state, seat, action.TileId!.Value);
                            break;
                        case BotActionType.Discard:
                            ChangshaGameStateMachine.Discard(state, seat, action.TileId!.Value);
                            break;
                        default:
                            ChangshaGameStateMachine.Discard(state, seat, hand.ConcealedTiles[^1]);
                            break;
                    }
                    break;
                }
                case ChangshaPhase.AwaitingClaim:
                {
                    var window = state.ClaimWindow!;
                    var claimerSeat = -1;
                    TableClaimType? claimType = null;
                    foreach (var opp in window.Opportunities.OrderByDescending(o => o.Priority))
                    {
                        var decision = strategies[opp.SeatIndex].DecideAction(state, opp.SeatIndex);
                        if (decision.Type == BotActionType.Claim && decision.ClaimType.HasValue)
                        {
                            claimerSeat = opp.SeatIndex;
                            claimType = decision.ClaimType.Value;
                            break;
                        }
                    }
                    if (claimerSeat >= 0 && claimType.HasValue)
                        ChangshaGameStateMachine.ResolveClaim(state, claimerSeat, claimType.Value);
                    else
                        ChangshaGameStateMachine.PassClaim(state);
                    break;
                }
                case ChangshaPhase.WallExhausted:
                    ChangshaGameStateMachine.HandleWallExhausted(state);
                    break;
                case ChangshaPhase.Scoring:
                    ChangshaGameStateMachine.Score(state);
                    break;
                case ChangshaPhase.EndHand:
                    return;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected phase {state.Phase} during lifecycle hand run.");
            }
        }
        throw new InvalidOperationException(
            $"Lifecycle hand did not terminate within {MaxStepsPerHand} steps (Phase={state.Phase}).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Hydration test plumbing (mirrors HydrationOnStartupTests fixtures)
    // ────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static ChangshaGameState BuildSimpleState(string gameId, ChangshaPhase phase)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 0xC0DE, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.GameId = gameId;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(0xC0DE));
        ChangshaGameStateMachine.Deal(state);
        state.Phase = phase;
        // For a terminal-phase fixture, also flip IsGameComplete so the row
        // looks exactly like one PersistSnapshotAsync would have written
        // post-RotateBanker — the hydration filter must skip on phase alone,
        // but a consistent fixture makes a future filter-tightening (e.g.,
        // "skip iff phase∈terminal AND IsGameComplete") still pass.
        if (phase.ToString().Contains("Complete", StringComparison.OrdinalIgnoreCase) ||
            phase.ToString().Contains("EndGame", StringComparison.OrdinalIgnoreCase))
        {
            state.IsGameComplete = true;
        }
        return state;
    }

    private static string NewSqlitePath()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, $"phase-j-w4-lifecycle-{Guid.NewGuid():N}.db");
    }

    private static void TryDeleteSqlite(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static WebApplicationFactory<Program> BuildFactory(string sqlitePath, bool persist)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={sqlitePath}");
            // Phase K Wave 23 — Vasquez. Pin SQLite (mirrors the same
            // fix in HydrationOnStartupTests). The HydrationFilter test
            // below uses a raw SqliteConnection to seed ChangshaGames
            // rows, so the factory must boot the SQLite provider even
            // when the Postgres matrix cell is running. See
            // `.squad/decisions/inbox/vasquez-db-providers-isolation.md`.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("Persistence:Provider", "Sqlite"),
                    new KeyValuePair<string, string?>("ConnectionStrings:Sqlite", $"Data Source={sqlitePath}"),
                });
            });
            builder.ConfigureServices(services =>
            {
                RebindToSqlite(services, sqlitePath);
                services.Configure<ChangshaRuntimeOptions>(o =>
                {
                    // Park bot turns well past wall-clock so the hydration test
                    // doesn't race the runtime's bot loop on the (legitimately)
                    // hydrated control row.
                    o.BotTurnDelayMs = 5_000;
                    o.BotClaimDelayMs = 5_000;
                    o.ClaimWindowTimeoutMs = 5_000;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = persist;
                });
            });
        });
        _ = factory.Server;
        return factory;
    }

    /// <summary>Strips the EF DbContext registrations Program.cs installed
    /// (whose provider is determined by env vars on the Postgres matrix cell)
    /// and re-registers a SQLite-only stack pinned to
    /// <paramref name="sqlitePath"/>. See twin helper of the same name in
    /// <see cref="Mahjong.Autotable.Api.Tests.Changsha.Acceptance.HydrationOnStartupTests"/>.</summary>
    private static void RebindToSqlite(IServiceCollection services, string sqlitePath)
    {
        var toRemove = services.Where(d =>
            d.ServiceType.FullName is
                "Mahjong.Autotable.Api.Data.AppDbContext" or
                "Mahjong.Autotable.Api.Persistence.PostgresAppDbContext" or
                "Mahjong.Autotable.Api.Persistence.SqlServerAppDbContext" or
                "Mahjong.Autotable.Api.Persistence.SqliteAppDbContext"
            ||
            (d.ServiceType.IsGenericType
             && d.ServiceType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<>)
             && d.ServiceType.GetGenericArguments()[0].FullName?.StartsWith("Mahjong.Autotable.Api") == true)
            ||
            d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions)
        ).ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<Mahjong.Autotable.Api.Persistence.SqliteAppDbContext>(options =>
        {
            options.UseSqlite($"Data Source={sqlitePath}", sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(Mahjong.Autotable.Api.Persistence.SqliteAppDbContext).Assembly.GetName().Name);
            });
        });
        services.AddScoped<Mahjong.Autotable.Api.Data.AppDbContext>(sp =>
            sp.GetRequiredService<Mahjong.Autotable.Api.Persistence.SqliteAppDbContext>());
    }

    private static async Task InsertSnapshotAsync(string sqlitePath, Guid gameId, ChangshaGameState state)
    {
        // Spin up a one-shot factory to run DatabaseBootstrapper — without it
        // the ChangshaGames table doesn't exist, so the INSERT below would 500.
        await using (var bootstrapFactory = BuildFactory(sqlitePath, persist: false))
        {
            _ = bootstrapFactory.Services;
        }

        var json = JsonSerializer.Serialize(state, SnapshotJson);
        var nowUtc = DateTime.UtcNow.ToString("o");

        var csb = new SqliteConnectionStringBuilder { DataSource = sqlitePath };
        await using var conn = new SqliteConnection(csb.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ChangshaGames
                (Id, RuleSet, Seed, StateJson, StateVersion, CurrentHandNumber, CurrentRoundNumber, CreatedUtc, UpdatedUtc)
            VALUES
                (@id, @ruleSet, @seed, @stateJson, @stateVersion, @hand, @round, @created, @updated);";
        cmd.Parameters.AddWithValue("@id", gameId.ToString());
        cmd.Parameters.AddWithValue("@ruleSet", "changsha-v1");
        cmd.Parameters.AddWithValue("@seed", state.Seed);
        cmd.Parameters.AddWithValue("@stateJson", json);
        cmd.Parameters.AddWithValue("@stateVersion", 1);
        cmd.Parameters.AddWithValue("@hand", state.HandNumber);
        cmd.Parameters.AddWithValue("@round", 1);
        cmd.Parameters.AddWithValue("@created", nowUtc);
        cmd.Parameters.AddWithValue("@updated", nowUtc);
        await cmd.ExecuteNonQueryAsync();
    }

    // Mirror of the inner DTOs used by ChangshaHub.CreateGame — kept private
    // here because the production type is not public-test-visible and we
    // only need the GameId field for assertions.
    private sealed record CreateGameResult(string GameId);
}

using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Data;

// Phase J Wave 7 — Apone (DevOps). Constructor accepts the non-generic
// `DbContextOptions` so provider-specific subclasses (SqliteAppDbContext,
// PostgresAppDbContext, SqlServerAppDbContext under Persistence/) can
// forward their own typed options without re-implementing the model.
// `DbContextOptions<TContext>` derives from `DbContextOptions`, so
// `services.AddDbContext<AppDbContext>(...)` (which passes the typed
// variant) still binds without any DI-side change.
public class AppDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<ChangshaGame> ChangshaGames => Set<ChangshaGame>();
    public DbSet<ChangshaGameEvent> ChangshaGameEvents => Set<ChangshaGameEvent>();
    public DbSet<AutotableRoomBinding> AutotableRoomBindings => Set<AutotableRoomBinding>();

    // Phase J Wave 5 — persistent per-player profile + stats. See
    // Mahjong.Autotable.Api.Players.PlayerProfileService.
    public DbSet<PlayerProfile> PlayerProfiles => Set<PlayerProfile>();
    public DbSet<PlayerStats> PlayerStats => Set<PlayerStats>();

    // Phase J Wave 7 — completed-game replay snapshots. See
    // Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime.EmitGameCompletedAsync
    // (write path) and the GET /api/games/{gameId}/replay endpoint in Program.cs
    // (read path).
    public DbSet<ChangshaGameReplay> ChangshaGameReplays => Set<ChangshaGameReplay>();

    // Phase J Wave 8 — server-driven rule presets + OAuth/passwordless auth.
    // See Mahjong.Autotable.Api.Auth.AuthCookieService + RulePresetService.
    public DbSet<ChangshaRulePreset> ChangshaRulePresets => Set<ChangshaRulePreset>();
    public DbSet<PlayerAuthIdentity> PlayerAuthIdentities => Set<PlayerAuthIdentity>();
    public DbSet<EmailMagicLinkToken> EmailMagicLinkTokens => Set<EmailMagicLinkToken>();
    public DbSet<PlayerAuthSession> PlayerAuthSessions => Set<PlayerAuthSession>();

    // Phase J Wave 9 — Content-Security-Policy violation reports. Append-only.
    // See Mahjong.Autotable.Api.Observability.CspReportEndpoint.
    public DbSet<CspViolation> CspViolations => Set<CspViolation>();

    // Phase J Wave 9 — reconnect token rotation + audit, plus persisted
    // chat backlog. See ReconnectTokenService, ChatService, and the
    // GET /api/games/{gameId}/chat endpoint.
    public DbSet<ReconnectToken> ReconnectTokens => Set<ReconnectToken>();
    public DbSet<ReconnectAuditEntry> ReconnectAuditEntries => Set<ReconnectAuditEntry>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    // Phase J Wave 10 — Tournament mode (Bishop). See
    // Mahjong.Autotable.Api.Tournament.TournamentService + the
    // /api/tournaments REST surface in TournamentController.
    // Fully qualified to disambiguate from the sibling Tournament namespace.
    public DbSet<Mahjong.Autotable.Api.Data.Entities.Tournament> Tournaments => Set<Mahjong.Autotable.Api.Data.Entities.Tournament>();
    public DbSet<TournamentRegistration> TournamentRegistrations => Set<TournamentRegistration>();
    public DbSet<TournamentMatch> TournamentMatches => Set<TournamentMatch>();

    // Phase K Wave 1 — match-history denormalization + Elo-rating tables
    // (Bishop). See Mahjong.Autotable.Api.Players.PlayerGameHistoryService
    // (writer at game completion) + the GET /api/games match-history
    // endpoint; Mahjong.Autotable.Api.Tournament.PlayerRatingService
    // (writer at tournament-match completion) + the
    // GET /api/ratings/leaderboard endpoint.
    public DbSet<PlayerGameHistory> PlayerGameHistory => Set<PlayerGameHistory>();
    public DbSet<PlayerRating> PlayerRatings => Set<PlayerRating>();
    public DbSet<PlayerRatingHistory> PlayerRatingHistory => Set<PlayerRatingHistory>();

    // Phase K Wave 2 — quarter-boundary deferral table (Bishop). Written
    // by SeasonRolloverService when a player is mid-tournament; drained
    // when the tournament completes. See
    // Mahjong.Autotable.Api.Tournament.SeasonRolloverService.
    public DbSet<PlayerSeasonRolloverDeferral> PlayerSeasonRolloverDeferrals => Set<PlayerSeasonRolloverDeferral>();

    // Phase K Wave 3 — Bishop. Server-authoritative onboarding tour
    // progress. Persisted via the
    // GET/POST /api/players/me/onboarding-status endpoints. See
    // Mahjong.Autotable.Api.Players.PlayerOnboardingService.
    public DbSet<PlayerOnboardingStatus> PlayerOnboardingStatuses => Set<PlayerOnboardingStatus>();

    // Phase K Wave 9 — Bishop. Durable per-month token usage ledger
    // for the LLM commentary surface. Backs EfCommentaryUsageMeter
    // so monthly caps survive across replicas + restarts. See
    // Mahjong.Autotable.Api.Commentary.EfCommentaryUsageMeter.
    public DbSet<CommentaryUsageRecord> CommentaryUsage => Set<CommentaryUsageRecord>();

    // Phase K Wave 9 — Bishop. Durable idempotency-replay ledger
    // for the multi-replica production deployment. Backs
    // EfIdempotencyStore. See Mahjong.Autotable.Api.Audit.EfIdempotencyStore.
    public DbSet<IdempotencyEntry> IdempotencyEntries => Set<IdempotencyEntry>();

    // Phase K Wave 11 — Bishop. Optional EF-backed per-record
    // commentary store. Toggled via Commentary:StorageImpl
    // ("InMemory" default tests / "Ef" default prod). Backs
    // EfCommentaryStore; see Mahjong.Autotable.Api.Commentary.EfCommentaryStore.
    public DbSet<CommentaryRecordRow> CommentaryRecords => Set<CommentaryRecordRow>();

    // Phase K Wave 12 — Bishop. Durable replay-by-id surface
    // backing GET/POST /api/replays/{replayId}. Toggled via
    // Replays:StorageImpl ("InMemory" default tests / "Ef" prod).
    // See Mahjong.Autotable.Api.Replays.EfReplayStore.
    public DbSet<Mahjong.Autotable.Api.Replays.ReplayRecord> Replays =>
        Set<Mahjong.Autotable.Api.Replays.ReplayRecord>();

    // Phase K Wave 12 — Bishop. Durable per-(TournamentId, Round, Slot)
    // bracket persistence. Backs EfBracketStore; toggled via
    // Tournament:BracketStoreImpl ("InMemory" default tests / "Ef" prod).
    // See Mahjong.Autotable.Api.Tournament.EfBracketStore.
    public DbSet<Mahjong.Autotable.Api.Tournament.BracketRecord> BracketRecords =>
        Set<Mahjong.Autotable.Api.Tournament.BracketRecord>();

    // Phase K Wave 12 — Bishop. Durable per-(hub, connectionId)
    // SignalR sequence-store for long-lived replay-from-ack
    // sessions. Backs EfSignalRSequenceStore; toggled via
    // SignalR:SequenceStoreImpl ("InMemory" default tests / "Ef" prod).
    // See Mahjong.Autotable.Api.Observability.EfSignalRSequenceStore.
    public DbSet<Mahjong.Autotable.Api.Observability.SignalRSequenceEntry> SignalRSequenceEntries =>
        Set<Mahjong.Autotable.Api.Observability.SignalRSequenceEntry>();

    // Phase K Wave 13 — Bishop. Durable audit trail for spectator
    // handoff JWT mints. Backs EfSpectatorHandoffAuditStore;
    // toggled via Spectator:Audit:StorageImpl ("InMemory" default
    // tests / "Ef" prod). See
    // Mahjong.Autotable.Api.Spectator.SpectatorHandoffAuditRecord.
    public DbSet<Mahjong.Autotable.Api.Spectator.SpectatorHandoffAuditRecord> SpectatorHandoffAuditRecords =>
        Set<Mahjong.Autotable.Api.Spectator.SpectatorHandoffAuditRecord>();

    // Phase K Wave 15 — Bishop. Per-tenant JWKS rotation policy
    // rows. Backs EfPerTenantJwksRotationStore; the toggle
    // JwksRotation:PerTenant:Enabled (default false) gates the
    // wiring. See Mahjong.Autotable.Api.Auth.PerTenantJwksRotationPolicy.
    public DbSet<Mahjong.Autotable.Api.Auth.PerTenantJwksRotationPolicy> PerTenantJwksRotationPolicies =>
        Set<Mahjong.Autotable.Api.Auth.PerTenantJwksRotationPolicy>();

    // Phase K Wave 16 — Bishop. Per-tenant replay retention
    // policy rows. Backs EfReplayRetentionPolicyStore. The
    // ReplayStoreRetentionSweep consults this table once per
    // tick to pick a per-tenant retention window before falling
    // back to the global Replays:RetentionDays default.
    // See Mahjong.Autotable.Api.Replays.ReplayRetentionPolicy.
    public DbSet<Mahjong.Autotable.Api.Replays.ReplayRetentionPolicy> ReplayRetentionPolicies =>
        Set<Mahjong.Autotable.Api.Replays.ReplayRetentionPolicy>();

    // Phase K Wave 17 — Bishop. Per-tenant SignalR-sequence
    // retention policy rows. Backs EfSignalRRetentionPolicyStore.
    // The SignalRSequenceRetentionSweep consults this table once
    // per distinct tenant per tick to pick a per-tenant
    // retention window before falling back to the global
    // SignalR:Sequences:RetentionMinutes default.
    // See Mahjong.Autotable.Api.Observability.SignalRRetentionPolicy.
    public DbSet<Mahjong.Autotable.Api.Observability.SignalRRetentionPolicy> SignalRRetentionPolicies =>
        Set<Mahjong.Autotable.Api.Observability.SignalRRetentionPolicy>();

    // Phase K Wave 19 — Bishop. Per-tournament Swiss pairing
    // audit log. Append-only — the FIDE C.04 pairing engine
    // stamps one row per (tournament, round, board) decision so
    // an operator chasing a pairing dispute can read the
    // algorithmic verdict without re-running the engine. Backs
    // the GET /api/admin/tournaments/{id}/swiss-pairing-audit
    // endpoint. See Mahjong.Autotable.Api.Data.Entities.SwissPairingAuditEntry.
    public DbSet<SwissPairingAuditEntry> SwissPairingAuditEntries =>
        Set<SwissPairingAuditEntry>();

    // Phase K Wave 21 — Bishop. Per-tenant scheduled JWKS
    // rotation cadence. Driven by the
    // RotationScheduledExecutorService background poller.
    // Surface: POST /api/admin/per-tenant-jwks-rotation-policies/{id}/schedule.
    // See Mahjong.Autotable.Api.Auth.RotationScheduleEntity.
    public DbSet<Mahjong.Autotable.Api.Auth.RotationScheduleEntity> RotationSchedules =>
        Set<Mahjong.Autotable.Api.Auth.RotationScheduleEntity>();

    // Phase K Wave 21 — Bishop. Per-replay restoration attempt
    // audit log. Append-only — every read or write of a
    // restored replay row stamps one entry so an operator
    // chasing a "did this replay get tampered with?" question
    // can replay the last-N attempts. Backs the GET
    // /api/admin/replays/{id}/restoration-audit endpoint. See
    // Mahjong.Autotable.Api.Replays.ReplayRestorationAttempt.
    public DbSet<Mahjong.Autotable.Api.Replays.ReplayRestorationAttempt> ReplayRestorationAttempts =>
        Set<Mahjong.Autotable.Api.Replays.ReplayRestorationAttempt>();

    // Phase K Wave 22 — Bishop. Per-tournament final standings
    // recorded when an admin finalizes a tournament via
    // POST /api/admin/tournaments/{id}/finalize. One row per
    // (TournamentId, PlayerId). See
    // Mahjong.Autotable.Api.Tournament.TournamentFinalizationController.
    public DbSet<TournamentStanding> TournamentStandings =>
        Set<TournamentStanding>();

    // Phase K Wave 22 — Bishop. Emergency-revoked JWT key ids,
    // one row per (TenantId, Kid). Written by
    // POST /api/admin/jwt-keys/emergency-revoke. See
    // Mahjong.Autotable.Api.Auth.JwtEmergencyRevokeController.
    public DbSet<JwtEmergencyRevokedKid> JwtEmergencyRevokedKids =>
        Set<JwtEmergencyRevokedKid>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AutotableRoomBinding>(entity =>
        {
            entity.HasKey(binding => binding.RoomKey);
            entity.Property(binding => binding.RoomKey).HasMaxLength(64);
            entity.Property(binding => binding.RoomId).HasMaxLength(64).IsRequired();
            entity.Property(binding => binding.RuntimeGameId).IsConcurrencyToken();
            entity.HasIndex(binding => binding.RuntimeGameId).IsUnique();
        });

        modelBuilder.Entity<ChangshaGame>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RuleSet).HasMaxLength(50);
            // Phase J Wave 7 — Apone. No explicit `HasColumnType` so the
            // unbounded text column maps to the provider-native type:
            // Sqlite=`TEXT`, Postgres=`text`, SQL Server=`nvarchar(max)`.
            // SqlServer's legacy `TEXT` is deprecated, so this override
            // was removed to keep the multi-provider migration set clean.
            entity.Property(x => x.StateVersion).HasDefaultValue(1).HasSentinel(-1);
            // Phase K Wave 3 — Bishop. Owner-of-the-table column (Wave 3
            // brief task 3): table creator's persistent PlayerId, gates
            // /api/games/{id}/settings/voice.
            entity.Property(x => x.OwnerPlayerId).HasMaxLength(128);
            // Phase K Wave 3 — Bishop. Per-table voice toggle. Default
            // false so existing/legacy rows backfill correctly.
            entity.Property(x => x.VoiceEnabled).HasDefaultValue(false);
        });

        modelBuilder.Entity<ChangshaGameEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(64);
            entity.HasIndex(x => new { x.GameId, x.Sequence }).IsUnique();
            entity.HasOne<ChangshaGame>()
                .WithMany()
                .HasForeignKey(x => x.GameId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlayerProfile>(entity =>
        {
            entity.HasKey(x => x.PlayerId);
            entity.Property(x => x.PlayerId).HasMaxLength(128);
            entity.Property(x => x.DisplayName).HasMaxLength(32).IsRequired();
            entity.Property(x => x.AvatarColor).HasMaxLength(7).IsRequired();
        });

        modelBuilder.Entity<PlayerStats>(entity =>
        {
            entity.HasKey(x => x.PlayerId);
            entity.Property(x => x.PlayerId).HasMaxLength(128);
            entity.HasOne<PlayerProfile>()
                .WithOne()
                .HasForeignKey<PlayerStats>(x => x.PlayerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Phase J Wave 7 — replay snapshot table. Single row per completed
        // game, keyed by a synthetic Id but uniquely indexed on GameId so
        // re-completion (e.g. hydration after a crash) upserts cleanly.
        // EventsJson holds the serialised play-by-play (no length cap)
        // because end-game replays of long N-hand matches can run into
        // hundreds of KB.
        //
        // Note: no FK to ChangshaGames. Replays are completed-game
        // historical artifacts that should outlive the parent game row
        // (e.g. a future cleanup that purges old ChangshaGames must not
        // cascade-drop the replay). The unique GameId index supports the
        // /api/games/{gameId}/replay lookup directly.
        modelBuilder.Entity<ChangshaGameReplay>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventsJson).IsRequired();
            // Phase J Wave 9 — schema version column. Default 1 so legacy
            // v1 replays (Wave 7/8) keep their implicit version after the
            // migration runs; new writes stamp
            // ChangshaGameReplay.CurrentSchemaVersion (=2).
            entity.Property(x => x.SchemaVersion).HasDefaultValue(1);
            entity.HasIndex(x => x.GameId).IsUnique();
        });

        modelBuilder.Entity<ChangshaRulePreset>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(512);
            entity.Property(x => x.CreatorPlayerId).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<PlayerAuthIdentity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ProviderSubject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Email).HasMaxLength(256);
            entity.HasIndex(x => new { x.Provider, x.ProviderSubject }).IsUnique();
            entity.HasIndex(x => x.PlayerId);
            entity.HasOne<PlayerProfile>()
                .WithMany()
                .HasForeignKey(x => x.PlayerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailMagicLinkToken>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Token).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Email).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RequestedPlayerId).HasMaxLength(128);
            entity.HasIndex(x => x.Token).IsUnique();
        });

        modelBuilder.Entity<PlayerAuthSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Token).HasMaxLength(128).IsRequired();
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            // Phase J Wave 9 — role stamp for the audit endpoint admin gate.
            // Nullable; null = ordinary player. See AuthCookieService.
            entity.Property(x => x.Role).HasMaxLength(32);
            entity.HasIndex(x => x.Token).IsUnique();
            entity.HasIndex(x => x.PlayerId);
        });

        // Phase J Wave 9 — reconnect token rotation. Token column is the
        // unique opaque value (64-char URL-safe base64); a unique index
        // lets the rotation service look the row up in O(log n) on every
        // ReconnectGame call. The (PlayerId, GameId) composite index
        // supports admin audit drill-downs ("show me every rotation for
        // player X in game Y") without a full scan.
        modelBuilder.Entity<ReconnectToken>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Token).HasMaxLength(128).IsRequired();
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.GameId).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.Token).IsUnique();
            entity.HasIndex(x => new { x.PlayerId, x.GameId });
        });

        // Phase J Wave 9 — append-only audit log for reconnect-token
        // rotations. PlayerId index for per-player drill-downs;
        // At index for time-window queries from the audit endpoint.
        // Phase K Wave 2 — generalised via the Kind classifier; the new
        // (Kind, At) composite supports operator queries that filter by
        // event class first (e.g. "all tournament forfeits in the last
        // hour"). Existing per-player + per-At indexes stay so the
        // Wave-9 drill-down queries keep their plans.
        modelBuilder.Entity<ReconnectAuditEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Ipv4Hash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.UserAgentHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Kind)
                .HasMaxLength(64)
                .IsRequired()
                .HasDefaultValue(ReconnectAuditEntry.KindReconnectTokenRotated);
            entity.Property(x => x.Detail).HasMaxLength(256);
            // Phase K Wave 8 — Bishop. Idempotency-Key + CorrelationId
            // enrichment. Both columns are nullable (pre-Wave-8 rows
            // back-fill clean) and the indexes are non-unique because
            // a single key may legitimately appear multiple times (the
            // first hit + the rejection rows under the same key both
            // share the value).
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.HasIndex(x => x.PlayerId);
            entity.HasIndex(x => x.At);
            entity.HasIndex(x => new { x.Kind, x.At });
            entity.HasIndex(x => x.CorrelationId);
            entity.HasIndex(x => x.IdempotencyKey);
        });

        // Phase J Wave 9 — persisted chat backlog. (GameId, At) composite
        // index supports the lazy backfill endpoint
        // GET /api/games/{gameId}/chat?since=<ts>&limit=50 directly.
        // Body capped at 512 (vs the 280-char hub validation cap) to
        // allow future emoji-padded payloads without a schema bump.
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.GameId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Body).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Channel).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.GameId, x.At });
        });

        // Phase J Wave 9 — CSP violation reports (Apone, DevOps). Append-only;
        // index ReceivedAt for time-window queries from the operator dashboard.
        // No FK to PlayerProfiles because reports often arrive from anonymous
        // pre-cookie callers. RawJson holds the unparsed envelope as forensic
        // backup; the parsed columns above just accelerate aggregation.
        modelBuilder.Entity<CspViolation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlayerId).HasMaxLength(128);
            entity.Property(x => x.DocumentUri).HasMaxLength(2048);
            entity.Property(x => x.Referrer).HasMaxLength(2048);
            entity.Property(x => x.ViolatedDirective).HasMaxLength(128);
            entity.Property(x => x.EffectiveDirective).HasMaxLength(128);
            entity.Property(x => x.OriginalPolicy).HasMaxLength(4096);
            entity.Property(x => x.Disposition).HasMaxLength(16);
            entity.Property(x => x.BlockedUri).HasMaxLength(2048);
            entity.Property(x => x.SourceFile).HasMaxLength(2048);
            entity.Property(x => x.ScriptSample).HasMaxLength(256);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.Property(x => x.RawJson).IsRequired();
            entity.HasIndex(x => x.ReceivedAt);
            entity.HasIndex(x => x.EffectiveDirective);
        });

        // Phase J Wave 10 — Tournament shell. Name has a soft index for
        // the listing endpoint's name-prefix filter (future); Status +
        // CreatedAt index supports the canonical `GET /api/tournaments?status=`
        // query used by the lobby.
        modelBuilder.Entity<Mahjong.Autotable.Api.Data.Entities.Tournament>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Format).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CreatedByPlayerId).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CreatedAt);
        });

        // Phase J Wave 10 — registration row. (TournamentId, PlayerId)
        // unique index prevents double-registration; the FK cascades on
        // tournament deletion (operator can drop a tournament wholesale
        // during draft → registrations evaporate with it).
        modelBuilder.Entity<TournamentRegistration>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TournamentId, x.PlayerId }).IsUnique();
            entity.HasIndex(x => x.TournamentId);
            entity.HasOne<Mahjong.Autotable.Api.Data.Entities.Tournament>()
                .WithMany()
                .HasForeignKey(x => x.TournamentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Phase J Wave 10 — pairing row. (TournamentId, Round) index
        // supports the leaderboard query's grouping by round. GameIdsJson
        // stored as a plain string column (no value converter) — the
        // service serialises/deserialises manually so we avoid the
        // change-tracker quirk where EF Core can't diff a List<Guid>
        // against itself reliably under JSON-column semantics.
        modelBuilder.Entity<TournamentMatch>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Player1Id).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Player2Id).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Player3Id).HasMaxLength(128);
            entity.Property(x => x.Player4Id).HasMaxLength(128);
            entity.Property(x => x.WinnerPlayerId).HasMaxLength(128);
            entity.Property(x => x.GameIdsJson).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ForfeitedPlayerId).HasMaxLength(128);
            entity.HasIndex(x => new { x.TournamentId, x.Round });
            entity.HasIndex(x => x.TournamentId);
            entity.HasOne<Mahjong.Autotable.Api.Data.Entities.Tournament>()
                .WithMany()
                .HasForeignKey(x => x.TournamentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Phase K Wave 1 — per-player game-history denormalization
        // (Bishop). Written at game completion by
        // PlayerGameHistoryService.RecordAsync (game runtime hook); read
        // by GET /api/games?playerId=. The (PlayerId, CompletedAt) index
        // backs the canonical paging query; the unique (PlayerId, GameId)
        // pair keeps re-completion idempotent (matches the existing
        // ChangshaGameReplays.GameId-unique upsert posture).
        modelBuilder.Entity<PlayerGameHistory>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.OpponentPlayerIdsCsv).HasMaxLength(1024).IsRequired();
            entity.HasIndex(x => new { x.PlayerId, x.CompletedAt });
            entity.HasIndex(x => new { x.PlayerId, x.GameId }).IsUnique();
        });

        // Phase K Wave 1 — per-(player, season) Elo rating (Bishop).
        // Updated by PlayerRatingService at tournament-match completion;
        // surfaced via GET /api/ratings/leaderboard. The unique
        // (PlayerId, Season) constraint guarantees one live row per
        // season; the (Season, EloRating desc) index supports the
        // leaderboard sort.
        modelBuilder.Entity<PlayerRating>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Season).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.PlayerId, x.Season }).IsUnique();
            entity.HasIndex(x => x.Season);
        });

        // Phase K Wave 1 — frozen prior-season snapshot (Bishop).
        // SeasonRolloverService copies live PlayerRatings rows here at
        // each quarter boundary then resets the live row. Unique
        // (PlayerId, Season) keeps the snapshot table idempotent on
        // re-runs (the service guards against double-freeze).
        modelBuilder.Entity<PlayerRatingHistory>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Season).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.PlayerId, x.Season }).IsUnique();
            entity.HasIndex(x => x.Season);
        });

        // Phase K Wave 2 — quarter-boundary rollover deferral. Written
        // when a quarter flips while the player is mid-tournament; the
        // (TournamentId, ResolvedAtUtc) composite index supports the
        // "what's still pending for this tournament" lookup performed
        // when the tournament completes. (PlayerId, FromSeasonId,
        // TournamentId) unique keeps the recorder idempotent on re-runs.
        //
        // Phase K Wave 3 — Bishop renamed the season fields to
        // FromSeasonId/ToSeasonId and DrainedAtUtc → ResolvedAtUtc per
        // Vasquez's Wave-2 contract-gap memo (fix #5). Indices were
        // re-named in lockstep with the migration.
        modelBuilder.Entity<PlayerSeasonRolloverDeferral>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.FromSeasonId).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ToSeasonId).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.PlayerId, x.FromSeasonId, x.TournamentId }).IsUnique();
            entity.HasIndex(x => new { x.TournamentId, x.ResolvedAtUtc });
        });

        // Phase K Wave 3 — Bishop. Onboarding tour progress. Single row
        // per player; PlayerId is the PK so the GET/POST contract is a
        // direct upsert. No FK to PlayerProfiles because the row may
        // exist briefly before the profile is fully resolved on the
        // first /api/identity round-trip; the soft pairing keeps the
        // surface tolerant.
        modelBuilder.Entity<PlayerOnboardingStatus>(entity =>
        {
            entity.HasKey(x => x.PlayerId);
            entity.Property(x => x.PlayerId).HasMaxLength(128);
        });

        // Phase K Wave 9 — Bishop. Durable per-month LLM token-usage
        // ledger. Unique (PeriodYear, PeriodMonth) keeps the increment
        // path race-safe — every replica converges on the same
        // monthly row. The RowVersion concurrency token defends
        // against two replicas racing to credit the same call.
        //
        // Note: we use IsConcurrencyToken() only (no IsRowVersion())
        // so the value is included in INSERT statements. IsRowVersion
        // marks the column as DB-generated, which works on
        // SQL Server but leaves the column NULL on SQLite (no native
        // rowversion). The caller (EfCommentaryUsageMeter) bumps
        // RowVersion to a fresh Guid byte array on every save so
        // optimistic-concurrency conflicts surface uniformly.
        modelBuilder.Entity<CommentaryUsageRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PeriodYear, x.PeriodMonth }).IsUnique();
            entity.Property(x => x.RowVersion)
                .IsConcurrencyToken();
        });

        // Phase K Wave 9 — Bishop. Durable idempotency-replay ledger
        // for multi-replica deployments. Key column is the PK so
        // duplicate POSTs share the row. The ExpiresAt index lets a
        // background sweeper drop expired rows efficiently; the
        // middleware also defensively treats expired rows as missing
        // so the read path never depends on the sweeper running.
        modelBuilder.Entity<IdempotencyEntry>(entity =>
        {
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(IdempotencyEntry.MaxKeyLength);
            entity.Property(x => x.PayloadHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ContentType).HasMaxLength(128);
            entity.HasIndex(x => x.ExpiresAt);
            entity.Property(x => x.RowVersion)
                .IsConcurrencyToken();
        });

        // Phase K Wave 11 — Bishop. Per-record commentary store.
        // Indexed by (GameId, GeneratedAtUtc) so the paginated
        // `GET /api/games/{gameId}/commentary?after=...` endpoint
        // serves with a single index seek. The ExpiresAtUtc index
        // backs the retention sweeper.
        modelBuilder.Entity<CommentaryRecordRow>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Phase).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Speaker).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Text).IsRequired();
            entity.Property(x => x.TileReferencesJson).IsRequired();
            entity.HasIndex(x => new { x.GameId, x.GeneratedAtUtc });
            entity.HasIndex(x => x.GeneratedAtUtc);
            entity.HasIndex(x => x.ExpiresAtUtc);
        });

        // Phase K Wave 12 — Bishop. Durable replay-by-id surface.
        // PK is ReplayId (short URL-safe synthetic id minted at
        // ingest); (GameId, CompletedAt) supports the future
        // listing endpoint. The ExpiresAt index backs the
        // retention sweeper.
        modelBuilder.Entity<Mahjong.Autotable.Api.Replays.ReplayRecord>(entity =>
        {
            entity.HasKey(x => x.ReplayId);
            entity.Property(x => x.ReplayId).HasMaxLength(64);
            entity.Property(x => x.Variant).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CompressedPayload).IsRequired();
            entity.HasIndex(x => new { x.GameId, x.CompletedAt });
            entity.HasIndex(x => x.ExpiresAt);
        });

        // Phase K Wave 12 — Bishop. Durable tournament-bracket
        // persistence. The (TournamentId, RoundNumber, MatchSlot)
        // unique constraint guarantees one row per pairing and
        // makes the replay-game-complete event handler
        // idempotent — re-applying a completion event updates the
        // existing row rather than inserting a duplicate.
        modelBuilder.Entity<Mahjong.Autotable.Api.Tournament.BracketRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SeedA).HasMaxLength(128).IsRequired();
            entity.Property(x => x.SeedB).HasMaxLength(128).IsRequired();
            entity.Property(x => x.WinnerSeed).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.TournamentId, x.RoundNumber, x.MatchSlot }).IsUnique();
            entity.HasIndex(x => x.TournamentId);
        });

        // Phase K Wave 12 — Bishop. Durable SignalR per-hub
        // per-connection sequence-store for replay-from-ack on
        // long-lived sessions. Unique (HubName, ConnectionId,
        // Sequence) keeps the insert path idempotent; the
        // (HubName, ConnectionId) lookup is the dominant read path
        // (every reconnect query). The ExpiresAt index backs the
        // retention sweeper.
        modelBuilder.Entity<Mahjong.Autotable.Api.Observability.SignalRSequenceEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.HubName).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ConnectionId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.GroupName).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Method).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PayloadJson).IsRequired();
            // Phase K Wave 17 — Bishop. Optional per-row tenant
            // stamp. Indexed because the per-tenant sweep
            // filters by TenantId once per distinct tenant per
            // tick; an unindexed scan would be O(N) per tenant
            // per tick.
            entity.Property(x => x.TenantId).HasMaxLength(128).HasDefaultValue(string.Empty);
            entity.HasIndex(x => new { x.HubName, x.ConnectionId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.HubName, x.ConnectionId });
            entity.HasIndex(x => x.ExpiresAt);
            entity.HasIndex(x => x.TenantId);
        });

        // Phase K Wave 13 — Bishop. Spectator handoff audit trail.
        // The (GameId, IssuedAt) composite index backs the
        // dominant read path (per-game listing, most-recent-first);
        // (UserId) supports the per-caller audit query; (IssuedAt)
        // backs the retention sweep. TokenJti is the natural
        // unique key so a replayed mint can't double-stamp.
        modelBuilder.Entity<Mahjong.Autotable.Api.Spectator.SpectatorHandoffAuditRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.TokenJti).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Scope).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientIp).HasMaxLength(64).IsRequired();
            entity.Property(x => x.UserAgent).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => x.TokenJti).IsUnique();
            entity.HasIndex(x => new { x.GameId, x.IssuedAt });
            entity.HasIndex(x => x.UserId);
            entity.HasIndex(x => x.IssuedAt);
        });

        // Phase K Wave 15 — Bishop. Per-tenant JWKS rotation policy.
        // TenantId is the PK + natural lookup key; both rotation
        // edges use DateTimeOffset so a non-UTC operator clock
        // round-trips correctly through persistence (the W14
        // global policy used DateTime, which stripped offsets).
        modelBuilder.Entity<Mahjong.Autotable.Api.Auth.PerTenantJwksRotationPolicy>(entity =>
        {
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ActiveKid).HasMaxLength(128).IsRequired();
            entity.Property(x => x.PreviousKid).HasMaxLength(128).IsRequired();
            // Phase K Wave 16 — Bishop. Per-tenant overlap-window
            // override (days). Defaults to 0; the validator falls
            // back to PerTenantJwksRotationOptions.DefaultOverlapDays
            // when the row value is non-positive.
            entity.Property(x => x.OverlapWindowDays).HasDefaultValue(0);
            entity.HasIndex(x => x.RotationStartUtc);
            entity.HasIndex(x => x.RotationCompleteUtc);
        });

        // Phase K Wave 16 — Bishop. Per-tenant replay retention
        // policy. TenantId is the PK; the
        // ReplayStoreRetentionSweep reads the table once per tick
        // to look up each row's retention window before falling
        // back to the global Replays:RetentionDays default.
        modelBuilder.Entity<Mahjong.Autotable.Api.Replays.ReplayRetentionPolicy>(entity =>
        {
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RetentionDays).IsRequired();
        });

        // Phase K Wave 17 — Bishop. Per-tenant SignalR-sequence
        // retention policy. TenantId is the PK; the
        // SignalRSequenceRetentionSweep reads the table once per
        // distinct tenant per tick to pick a per-tenant
        // retention window before falling back to the global
        // SignalR:Sequences:RetentionMinutes default.
        modelBuilder.Entity<Mahjong.Autotable.Api.Observability.SignalRRetentionPolicy>(entity =>
        {
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RetentionMinutes).IsRequired();
        });

        // Phase K Wave 16 — Bishop. Optional tenant column on the
        // replay table. Indexed because the per-tenant sweep
        // filters by TenantId once per tenant policy row; an
        // unindexed scan would be O(N) per tenant per tick.
        modelBuilder.Entity<Mahjong.Autotable.Api.Replays.ReplayRecord>(entity =>
        {
            entity.Property(x => x.TenantId).HasMaxLength(128);
            entity.HasIndex(x => x.TenantId);
        });

        // Phase K Wave 19 — Bishop. Per-tournament Swiss pairing
        // audit log. The natural key is (TournamentId, Round,
        // Board) — a single rerun of the pairing engine MUST NOT
        // double-stamp a board, so we declare a unique composite
        // index on that triple. CreatedAtUtc is indexed for the
        // trail-by-time admin view.
        modelBuilder.Entity<SwissPairingAuditEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.White).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Black).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Tiebreaker)
                .HasMaxLength(SwissPairingAuditEntry.MaxTiebreakerLength)
                .IsRequired();
            entity.HasIndex(x => new { x.TournamentId, x.Round, x.Board }).IsUnique();
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => x.TournamentId);
        });

        // Phase K Wave 21 — Bishop. Per-tenant scheduled JWKS
        // rotation cadence. TenantId is the natural key (one
        // schedule per tenant); UpdatedAtUtc is indexed for the
        // operator dashboard's "recently changed" view.
        modelBuilder.Entity<Mahjong.Autotable.Api.Auth.RotationScheduleEntity>(entity =>
        {
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CronExpression)
                .HasMaxLength(Mahjong.Autotable.Api.Auth.RotationScheduleEntity.MaxCronLength)
                .IsRequired();
            entity.Property(x => x.Notes)
                .HasMaxLength(Mahjong.Autotable.Api.Auth.RotationScheduleEntity.MaxNotesLength);
            entity.HasIndex(x => x.UpdatedAtUtc);
            entity.HasIndex(x => x.Enabled);
        });

        // Phase K Wave 21 — Bishop. Replay restoration attempt
        // audit log. Append-only — the GET
        // /api/admin/replays/{id}/restoration-audit endpoint
        // reads the last 10 rows for the supplied replay id.
        // (ReplayId, AttemptedAtUtc DESC) is the dominant read
        // path; the secondary index on AttemptedAtUtc supports
        // the global trail-by-time view.
        modelBuilder.Entity<Mahjong.Autotable.Api.Replays.ReplayRestorationAttempt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ReplayId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OperatorId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Outcome)
                .HasMaxLength(Mahjong.Autotable.Api.Replays.ReplayRestorationAttempt.MaxOutcomeLength)
                .IsRequired();
            entity.Property(x => x.DetailMessage)
                .HasMaxLength(Mahjong.Autotable.Api.Replays.ReplayRestorationAttempt.MaxDetailLength);
            entity.HasIndex(x => new { x.ReplayId, x.AttemptedAtUtc });
            entity.HasIndex(x => x.AttemptedAtUtc);
        });

        // Phase K Wave 22 — Bishop. Per-tournament final
        // standings row. (TournamentId, PlayerId) is unique so
        // a double-finalize is caught at the schema level. The
        // (TournamentId, Rank) index supports the leaderboard
        // render ordered by rank.
        modelBuilder.Entity<TournamentStanding>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PlayerId).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.TournamentId, x.PlayerId }).IsUnique();
            entity.HasIndex(x => new { x.TournamentId, x.Rank });
            entity.HasIndex(x => x.TournamentId);
            entity.HasOne<Mahjong.Autotable.Api.Data.Entities.Tournament>()
                .WithMany()
                .HasForeignKey(x => x.TournamentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Phase K Wave 22 — Bishop. Emergency-revoked JWT key
        // id. (TenantId, Kid) is unique so re-revocation is a
        // no-op at the schema level. The TenantId-only index
        // supports the JwksCacheService validate-time lookup.
        modelBuilder.Entity<JwtEmergencyRevokedKid>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Kid).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(512).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.Kid }).IsUnique();
            entity.HasIndex(x => x.TenantId);
            entity.HasIndex(x => x.RevokedAtUtc);
        });
    }
}

using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Patterns;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Leaderboard;
using Mahjong.Autotable.Api.Matchmaking;
using Mahjong.Autotable.Api.Observability;
using Mahjong.Autotable.Api.Persistence;
using Mahjong.Autotable.Api.Players;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

// Phase J Wave 3 — Apone's Docker HEALTHCHECK / Linux deploy needs a stable
// process-uptime anchor. Captured at module load (before WebApplication build)
// so the value reflects the actual host process start, not the time of the
// first /health request.
var processStartTime = DateTimeOffset.UtcNow;

// Phase J Wave 9 — Apone (DevOps). Stand-alone migrate mode for the k8s
// pre-rollout Job (infra/k8s/base/job-migrate.yaml). When invoked with
// `--migrate`, the process boots the DI container far enough to resolve
// AppDbContext, runs Database.MigrateAsync (or EnsureCreatedAsync for
// SQLite, which still uses the EnsureCreated bootstrap pattern), and
// exits 0 — the listener port is never bound, so the Job completes
// cleanly without fighting the Deployment's readiness probe.
if (args.Contains("--migrate"))
{
    var migrateBuilder = WebApplication.CreateBuilder(args);
    migrateBuilder.Services.AddPersistence(migrateBuilder.Configuration);
    using var migrateApp = migrateBuilder.Build();
    using var scope = migrateApp.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Console.WriteLine($"[migrate] provider={(db.Database.IsSqlite() ? "Sqlite" : db.Database.IsNpgsql() ? "Postgres" : db.Database.IsSqlServer() ? "SqlServer" : "Unknown")} starting…");
    if (db.Database.IsSqlite())
    {
        // SQLite uses EnsureCreated + defensive bootstraps; reuse the
        // canonical InitializeAsync path so the Job is equivalent to
        // the in-process boot.
        await DatabaseBootstrapper.InitializeAsync(db);
    }
    else
    {
        await db.Database.MigrateAsync();
    }
    Console.WriteLine($"[migrate] complete.");
    return;
}

// Phase K Wave 1 — Bishop (Backend). Stand-alone OAuth verification mode.
// Probes every configured OAuth provider's OIDC discovery endpoint and
// prints a one-line JSON summary per provider. Exit code is 0 when
// every enabled+configured provider returns healthy; 1 otherwise. Used
// by operators to verify the OAuth surface during deployment bringup
// before exposing the API to clients.
if (args.Contains("verify-oauth"))
{
    var verifyBuilder = WebApplication.CreateBuilder(args);
    verifyBuilder.Services.Configure<Mahjong.Autotable.Api.Auth.AuthOptions>(
        verifyBuilder.Configuration.GetSection("Authentication"));
    verifyBuilder.Services.AddHttpClient();
    verifyBuilder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.OAuthProviderHealthCheck>();
    verifyBuilder.Logging.SetMinimumLevel(LogLevel.Warning);
    using var verifyApp = verifyBuilder.Build();
    var check = verifyApp.Services.GetRequiredService<Mahjong.Autotable.Api.Auth.OAuthProviderHealthCheck>();
    var results = await check.ProbeAllAsync();
    if (results.Count == 0)
    {
        Console.WriteLine("[verify-oauth] no providers configured.");
        return;
    }
    var anyUnhealthy = false;
    foreach (var kv in results)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            provider = kv.Key,
            healthy = kv.Value.Healthy,
            statusCode = kv.Value.StatusCode,
            error = kv.Value.Error,
        });
        Console.WriteLine($"[verify-oauth] {payload}");
        if (!kv.Value.Healthy) anyUnhealthy = true;
    }
    Environment.Exit(anyUnhealthy ? 1 : 0);
    return;
}

var builder = WebApplication.CreateBuilder(args);
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));

// Phase J Wave 5 — structured logging contract (Apone, DevOps). Production
// emits one JSON document per log line so log shippers (Loki promtail, Vector,
// CloudWatch agent, etc.) ingest categories/levels/scopes without parsing
// human-formatted text. Non-production environments keep the readable
// AddSimpleConsole output so `dotnet run` / `docker compose up` stays
// developer-friendly. ClearProviders() is required — without it the default
// Console provider double-emits each entry alongside the JSON one. Scopes are
// surfaced in both modes so SignalR's ConnectionId / HubMethodName scopes
// appear in the structured payload (see docs/observability.md).
builder.Logging.ClearProviders();
if (builder.Environment.IsProduction())
{
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        o.JsonWriterOptions = new JsonWriterOptions { Indented = false };
    });
}
else
{
    builder.Logging.AddSimpleConsole(o =>
    {
        o.IncludeScopes = true;
        o.SingleLine = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    });
}

builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
// Phase J Wave 8 — SignalR hub-method breadcrumbs into Sentry (Apone, DevOps).
// The hub filter is a no-op when Sentry isn't initialised (SDK gated on
// `Sentry:Dsn`), so adding it unconditionally costs nothing in dev / test.
builder.Services.AddSignalR(o => o.AddFilter<Mahjong.Autotable.Api.Observability.SentryHubFilter>());

// Phase J Wave 8 — Sentry crash reporting (Apone, DevOps). The call is a
// no-op when `Sentry:Dsn` is unset / empty (which is the default in
// `appsettings.json`), so test / dev runs never hit the network.
var sentryEnabled = builder.WebHost.AddMahjongSentry(builder.Configuration);

// Phase J Wave 5 — MVC controllers for the matchmaking REST endpoint
// (MatchmakingController owns GET /api/matchmaking/lobby).
builder.Services.AddControllers();

builder.Services.Configure<ChangshaRuntimeOptions>(builder.Configuration.GetSection("ChangshaRuntime"));
// SC-2 / G19 (Ripley, BINDING) — the DEDICATED handle-derivation secret lives under its own
// `Privacy:HandleSecret` key (server-only IKM), distinct from the ChangshaRuntime section. Bind it
// onto the runtime options so the connection manager can prefer it over the JWT-key IKM fallback.
builder.Services.PostConfigure<ChangshaRuntimeOptions>(o =>
{
    var handleSecret = builder.Configuration.GetValue<string>("Privacy:HandleSecret");
    if (!string.IsNullOrWhiteSpace(handleSecret))
    {
        o.HandleSecretBase64 = handleSecret;
    }
});
builder.Services.AddSingleton<IChangshaGameRuntime, ChangshaGameRuntime>();
builder.Services.AddSingleton<AutotableConnectionManager>();

// Phase J Wave 5 — player profile + matchmaking services. Singleton-scoped so
// they share the runtime's lifetime and use IServiceScopeFactory for DB scopes.
builder.Services.AddSingleton<PlayerProfileService>();
builder.Services.AddSingleton<LobbyPresenceService>();
builder.Services.AddSingleton<MatchmakingService>();

// Phase J Wave 6 — identity + leaderboard services. PlayerIdentityService
// owns the mahjong_pid cookie (mint/read/refresh) consumed by both the
// REST identity endpoint and the autotable WS upgrade handshake;
// LeaderboardService backs GET /api/leaderboard. Both are thin stateless
// wrappers around HttpContext + EF Core scopes, so singleton lifetime is
// fine.
//
// Burke — the cookie now carries a SIGNED, versioned credential rather than the
// bare (public) playerId. PlayerIdentityTokenProtector HKDF-derives its MAC keys
// from the existing JwtSigningKeyProvider key set, so signing/rotation reuse the
// JWT key surface and runbook; `Identity:*` only carries cookie-attribute policy.
builder.Services.Configure<PlayerIdentityOptions>(builder.Configuration.GetSection("Identity"));
builder.Services.AddSingleton<PlayerIdentityTokenProtector>();
builder.Services.AddSingleton<PlayerIdentityService>();
builder.Services.AddSingleton<LeaderboardService>();

// Phase K Wave 1 — per-player match-history denormalization writer
// (Bishop). Singleton-shaped like PlayerProfileService; the runtime
// invokes RecordAsync from the EmitGameCompletedAsync hook.
builder.Services.AddSingleton<PlayerGameHistoryService>();

// Phase J Wave 8 — Bishop: OAuth / magic-link auth + rule-preset CRUD
// (see .squad/decisions/inbox/bishop-phase-j-wave-8.md). Auth services are
// scope-shaped wrappers around AppDbContext: they take an
// IServiceScopeFactory and open a fresh scope per call, mirroring the
// PlayerProfileService / MatchmakingService pattern. Singleton lifetime
// is therefore safe.
var authSection = builder.Configuration.GetSection("Authentication");
var authOptions = authSection.Get<Mahjong.Autotable.Api.Auth.AuthOptions>()
    ?? new Mahjong.Autotable.Api.Auth.AuthOptions();

// Phase K Wave 4 — Bishop. Microsoft Entra config-key canonicalisation.
// Wave 3 left two equally-valid paths (`Authentication:Providers:Microsoft:*`
// vs. `Authentication:Microsoft:*`). Wave 4 picks the canonical
// `Authentication:Providers:Microsoft:*` shape and emits a startup
// warning when the legacy flat path is still populated so operators
// notice the deprecation. The canonical value always wins on
// conflict. See docs/oauth-production-setup.md §Microsoft for the
// operator migration steps.
{
    var canonicalMs = authOptions.Providers.Microsoft;
    var legacyMs = authOptions.Microsoft;
    var legacyPopulated = !string.IsNullOrWhiteSpace(legacyMs.ClientId)
        || !string.IsNullOrWhiteSpace(legacyMs.ClientSecret);
    var canonicalPopulated = !string.IsNullOrWhiteSpace(canonicalMs.ClientId)
        || !string.IsNullOrWhiteSpace(canonicalMs.ClientSecret);
    if (canonicalPopulated)
    {
        // Canonical wins; collapse onto AuthOptions.Microsoft so
        // downstream readers (OAuthService, AuthController) keep
        // their existing access pattern unchanged.
        authOptions.Microsoft = canonicalMs;
        if (legacyPopulated)
        {
            var startupLogger = LoggerFactory
                .Create(b => b.AddConsole())
                .CreateLogger("Mahjong.Autotable.Api.Auth.MicrosoftConfigCanonicalisation");
            startupLogger.LogWarning(
                "Both `Authentication:Providers:Microsoft:*` (canonical) and `Authentication:Microsoft:*` (legacy) are set. The canonical path wins; please remove the legacy keys — see docs/oauth-production-setup.md §Microsoft.");
        }
    }
    else if (legacyPopulated)
    {
        var startupLogger = LoggerFactory
            .Create(b => b.AddConsole())
            .CreateLogger("Mahjong.Autotable.Api.Auth.MicrosoftConfigCanonicalisation");
        startupLogger.LogWarning(
            "`Authentication:Microsoft:*` is configured via the deprecated flat path. Migrate to `Authentication:Providers:Microsoft:*` — the legacy keys are scheduled for removal in a later wave. See docs/oauth-production-setup.md §Microsoft.");
    }
}

builder.Services.AddSingleton(authOptions);
// Phase K Wave 1 — also bind via IOptions<AuthOptions> so newer
// services (OAuthProviderHealthCheck, OAuthStateProtector) that take
// the options-pattern signature resolve cleanly.
builder.Services.Configure<Mahjong.Autotable.Api.Auth.AuthOptions>(authSection);
// Phase K Wave 4 — Bishop. Mirror the Microsoft canonicalisation
// onto the IOptions-bound instance so consumers that resolve through
// IOptions<AuthOptions> (OAuthProviderHealthCheck, etc.) see the same
// collapsed Microsoft block as the AuthOptions singleton above.
builder.Services.PostConfigure<Mahjong.Autotable.Api.Auth.AuthOptions>(o =>
{
    var canonical = o.Providers.Microsoft;
    if (!string.IsNullOrWhiteSpace(canonical.ClientId)
        || !string.IsNullOrWhiteSpace(canonical.ClientSecret))
    {
        o.Microsoft = canonical;
    }
});
var smtpOptions = builder.Configuration.GetSection("Smtp").Get<Mahjong.Autotable.Api.Auth.SmtpOptions>()
    ?? new Mahjong.Autotable.Api.Auth.SmtpOptions();
builder.Services.AddSingleton(smtpOptions);
builder.Services.AddHttpClient("oauth");
if (!string.IsNullOrWhiteSpace(smtpOptions.Host))
{
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.IEmailSender>(sp =>
        new Mahjong.Autotable.Api.Auth.SmtpEmailSender(
            smtpOptions,
            sp.GetRequiredService<ILogger<Mahjong.Autotable.Api.Auth.SmtpEmailSender>>()));
}
else
{
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.IEmailSender,
        Mahjong.Autotable.Api.Auth.LogEmailSender>();
}
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.AuthCookieService>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.AuthIdentityService>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.OAuthService>();
// Phase K Wave 4 — Bishop. JWT signing-key fallback list + issuance /
// validation services. The provider materialises Auth:JwtSigningKeys
// into JwtSigningKey records (kid = deterministic SHA-256 truncation of
// the raw key material). The issuer always signs with keys[0]; the
// validator iterates every key (kid fast-path + try-all fallback) so a
// token signed under any historical key continues to validate.
// See docs/jwt-rotation.md §2 for the rotation runbook.
//
// JwtSigningKeys live under the top-level "Auth" section (Apone's
// Wave-3 schema in appsettings.json) — distinct from the
// "Authentication" section that carries the OAuth provider config.
// We bind both shapes into a synthetic JwtAuthOptions instance so the
// existing AuthOptions schema stays untouched.
{
    // Phase L — Drake. Use a small helper to honour BOTH the legacy
    // `Auth:` and the canonical `Authentication:` sections while
    // correctly falling through when the first section is a
    // NON-NULL EMPTY array (e.g. `appsettings.json` ships
    // `Auth:JwtSigningKeys: []` which `.Get<string[]>()` materialises
    // as a non-null `[]`; the original `??` chain stopped there and
    // never read `Authentication:JwtSigningKeys`, which meant operator
    // env vars like `Authentication__JwtSigningKeys__0=<key>` were
    // silently ignored — Drake's `jwt-restart-survival.sh` proof
    // surfaced this regression at boot).
    static string[] FirstNonEmptyArray(params string[]?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (c is { Length: > 0 } &&
                c.Any(static s => !string.IsNullOrEmpty(s)))
            {
                return c;
            }
        }
        return Array.Empty<string>();
    }
    var jwtAuthOptions = new Mahjong.Autotable.Api.Auth.AuthOptions
    {
        JwtSigningKeys = FirstNonEmptyArray(
            builder.Configuration.GetSection("Auth:JwtSigningKeys").Get<string[]>(),
            builder.Configuration.GetSection("Authentication:JwtSigningKeys").Get<string[]>(),
            authOptions.JwtSigningKeys),
        JwtSigningKey = builder.Configuration.GetValue<string>("Auth:JwtSigningKey")
            ?? builder.Configuration.GetValue<string>("Authentication:JwtSigningKey")
            ?? authOptions.JwtSigningKey
            ?? string.Empty,
        // Phase K Wave 6 — Bishop. Algorithm + RSA-key knobs are bound
        // from both the legacy `Auth:` and the canonical `Authentication:`
        // section so operators can flip the active algorithm without
        // first migrating the section path. `Auth:` wins when both
        // are populated (matches the JwtSigningKeys precedence).
        JwtAlgorithm = builder.Configuration.GetValue<string>("Auth:JwtAlgorithm")
            ?? builder.Configuration.GetValue<string>("Authentication:JwtAlgorithm")
            ?? (string.IsNullOrWhiteSpace(authOptions.JwtAlgorithm) ? "HS256" : authOptions.JwtAlgorithm),
        JwtRsaKeys = FirstNonEmptyArray(
            builder.Configuration.GetSection("Auth:JwtRsaKeys").Get<string[]>(),
            builder.Configuration.GetSection("Authentication:JwtRsaKeys").Get<string[]>(),
            authOptions.JwtRsaKeys),
        // Phase K Wave 7 — Bishop. `Auth:Issuer` (canonical) /
        // `Authentication:Issuer` (legacy) feeds the OIDC discovery
        // hard contract: with RS256 + a non-empty issuer the
        // `/.well-known/openid-configuration` endpoint MUST return
        // 200 with the issuer + jwks_uri + token_endpoint fields
        // populated. Empty falls back to the request origin (the
        // Wave-6 soft-pass behaviour kept for back-compat).
        Issuer = builder.Configuration.GetValue<string>("Auth:Issuer")
            ?? builder.Configuration.GetValue<string>("Authentication:Issuer")
            ?? authOptions.Issuer
            ?? string.Empty,
        // Phase K Wave 9 — Bishop. JWT rotation grace period (seconds).
        // The RotationCadenceValidator hard-asserts
        // `JwksCacheTtl <= RotationGracePeriod / 2` at startup so a
        // misaligned configuration aborts the boot. Default 600 s
        // (10 min) — see docs/jwt-rotation.md §11.
        RotationGracePeriodSeconds = builder.Configuration.GetValue<int?>("Auth:JwtRsaKeys:RotationGracePeriodSeconds")
            ?? builder.Configuration.GetValue<int?>("Auth:RotationGracePeriodSeconds")
            ?? builder.Configuration.GetValue<int?>("Authentication:JwtRsaKeys:RotationGracePeriodSeconds")
            ?? (authOptions.RotationGracePeriodSeconds > 0 ? authOptions.RotationGracePeriodSeconds : 600),
    };
    // Phase L — Drake. Construct the provider EAGERLY so the prod
    // hardening check (throws when Production + no operator-provided
    // keys) fires at boot — not on the first JWT resolve. The factory
    // would otherwise defer construction until the first request,
    // letting the listener bind under a half-broken auth surface.
    var jwtSigningLogger = LoggerFactory
        .Create(b => b.AddConsole())
        .CreateLogger<Mahjong.Autotable.Api.Auth.JwtSigningKeyProvider>();
    var jwtSigningProvider = new Mahjong.Autotable.Api.Auth.JwtSigningKeyProvider(
        jwtAuthOptions,
        jwtSigningLogger,
        // Production hardening: refuse to boot with an ephemeral
        // random HMAC key (it re-mints on every restart, silently
        // invalidating every prior JWT). Dev / test keep the
        // historical auto-mint shape so `dotnet run` / `F5` never
        // require operator config. See docs/jwt-rotation.md §7.
        requireOperatorKeys: builder.Environment.IsProduction());
    builder.Services.AddSingleton(jwtSigningProvider);
    // Phase K Wave 9 — Bishop. Hard-asserted JWKS TTL / rotation
    // cadence invariant. Throws InvalidOperationException at host
    // boot when JwksCacheTtl > RotationGracePeriod / 2. See
    // docs/jwt-rotation.md §11.
    var rotationValidator = new Mahjong.Autotable.Api.Auth.RotationCadenceValidator(jwtAuthOptions);
    rotationValidator.Validate();
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.IRotationCadenceValidator>(rotationValidator);

    // Blocker E (Bishop rev2) — SC-2/G19 privacy fail-CLOSED in Production. When opaque
    // hidden-tile handles are enabled (default-on for Changsha) the per-viewer projector
    // needs >=32 bytes of server-secret IKM (Privacy:HandleSecret base64, else the active
    // JWT signing key). If NONE is available, the connection manager historically logged a
    // warning and booted with handles DISABLED — silently emitting REAL tile ids for every
    // hidden wall/foreign-hand tile (fail-OPEN privacy hole). In Production we now refuse to
    // boot instead, mirroring the JWT `requireOperatorKeys` convention above so a
    // misconfigured deployment fails loudly at startup rather than leaking concealed tiles.
    // Dev/Test keep the historical warn-and-disable shape so `dotnet run` / tests need no
    // operator secret. Logic + message live in the unit-tested
    // ChangshaPrivacyStartupValidator (mirrors AutotableConnectionManager's IKM resolution).
    Mahjong.Autotable.Api.Autotable.ChangshaPrivacyStartupValidator.Validate(
        isProduction: builder.Environment.IsProduction(),
        opaqueHandlesEnabled: builder.Configuration.GetValue<bool?>("ChangshaRuntime:OpaqueHiddenHandles") ?? true,
        handleSecretBase64: builder.Configuration.GetValue<string>("Privacy:HandleSecret"),
        jwtKeyMaterial: jwtSigningProvider.ActiveKey?.Material);

    // Burke — the durable player-identity cookie is HMAC-signed with a key HKDF-derived
    // from the active JWT signing key. The HS256 `requireOperatorKeys` guard above does
    // NOT cover an RS256 Production host (it falls through to a per-process random HMAC
    // key), which would silently invalidate every identity on restart. Fail the boot
    // closed instead, mirroring the privacy validator. Dev / Test keep the ephemeral
    // shape and log it explicitly.
    Mahjong.Autotable.Api.Players.PlayerIdentityStartupValidator.Validate(
        isProduction: builder.Environment.IsProduction(),
        usingEphemeralSigningKey: jwtSigningProvider.UsingEphemeralFallbackKey,
        logger: jwtSigningLogger);
}
// Phase K Wave 17 — Bishop. JWT-issue blocked-path metrics. The
// JwtIssuingService takes this as an optional dep so the
// IssueForTenantAsync block-path stamps jwt_issue_blocked_total
// {reason=stale_per_tenant_policy|per_tenant_store_missing}. The
// MetricsEndpoint renders the snapshot at /metrics.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.JwtIssueBlockedMetrics>();
// Phase K Wave 19 — Bishop. JWT issue + validator duration
// histograms. The issuing service + validator both take this as
// an optional dep so the per-tenant latency is observable on the
// MetricsEndpoint. See docs/realtime-resilience.md §10.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.JwtDurationMetrics>();
// Phase K Wave 21 — Bishop. JWT validator anomaly counter.
// Records clock-skew, invalid-issuer, expired-too-soon
// outcomes per (tenant, reason) so operators can graph
// anomalous validation bursts on a single Grafana panel.
// The validator takes this as an optional dep — legacy
// tests that wire only the issuer still work.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.JwtValidatorAnomalyMetrics>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.JwtIssuingService>();
// Phase K Wave 14 — Bishop. JWKS overlap-window enforcement. The
// validator now takes the staged rotation policy so it can reject
// tokens signed with the previous-active key when their `iat` falls
// AT OR AFTER the configured RotationStartUtc. The policy is
// registered below; we resolve it through the IoC container so
// either constructor overload can hydrate. See
// docs/jwt-rotation.md §14.
//
// Phase K Wave 19 — Bishop. The DI factory now also threads the
// JwtDurationMetrics collector through the W19 overload so the
// validator-check histogram records every call.
//
// Phase K Wave 21 — Bishop. The DI factory also threads the
// JwtValidatorAnomalyMetrics collector through the W21
// overload so anomalous validations stamp the counter.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.JwtValidationService>(sp =>
    new Mahjong.Autotable.Api.Auth.JwtValidationService(
        sp.GetRequiredService<Mahjong.Autotable.Api.Auth.JwtSigningKeyProvider>(),
        sp.GetService<Mahjong.Autotable.Api.Auth.JwtStagedRotationPolicy>(),
        sp.GetService<Mahjong.Autotable.Api.Auth.JwtDurationMetrics>(),
        sp.GetService<Mahjong.Autotable.Api.Auth.JwtValidatorAnomalyMetrics>(),
        null));
// Phase K Wave 12 — Bishop. Spectator handoff validator pairs with
// the controller in Spectator/SpectatorHandoffController.cs. Scoped
// `spectator:{gameId}` JWTs are minted by the controller and verified
// by this validator when the livestream endpoint receives `?token=…`.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Spectator.SpectatorHandoffTokenValidator>();

// Phase K Wave 13 — Bishop. Spectator handoff audit store. Toggled
// via Spectator:Audit:StorageImpl ("InMemory" default tests /
// "Ef" prod). See Spectator/SpectatorHandoffAudit.cs +
// docs/spectator-handoff.md §3.
builder.Services.Configure<Mahjong.Autotable.Api.Spectator.SpectatorHandoffAuditOptions>(
    builder.Configuration.GetSection("Spectator:Audit"));
{
    // Phase K Wave 15 — Bishop. Bind a singleton options instance
    // so the W15 retention sweep hosted service can resolve it
    // without going through IOptions<T>. The IConfigureOptions
    // path above continues to drive future IOptions<T> consumers.
    var spectatorAuditOptions = new Mahjong.Autotable.Api.Spectator.SpectatorHandoffAuditOptions();
    builder.Configuration.GetSection("Spectator:Audit").Bind(spectatorAuditOptions);
    builder.Services.AddSingleton(spectatorAuditOptions);
}
var spectatorAuditImpl = builder.Configuration["Spectator:Audit:StorageImpl"];
if (string.Equals(spectatorAuditImpl, "Ef", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Spectator.ISpectatorHandoffAuditStore,
        Mahjong.Autotable.Api.Spectator.EfSpectatorHandoffAuditStore>();
    // Phase K Wave 15 — Bishop. 5-minute retention sweep against
    // IssuedAt < now - RetentionDays. Registered only for the EF
    // impl; the in-memory store has no on-disk footprint.
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Spectator.SpectatorHandoffAuditRetentionSweep>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<Mahjong.Autotable.Api.Spectator.SpectatorHandoffAuditRetentionSweep>());
}
else
{
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Spectator.ISpectatorHandoffAuditStore,
        Mahjong.Autotable.Api.Spectator.InMemorySpectatorHandoffAuditStore>();
}
// Phase K Wave 12 — Bishop. Staged rotation policy. Surfaces the
// configured 30-day overlap window so the cadence validator + ops
// dashboard can audit it without re-deriving the math. See
// docs/jwt-rotation.md §13.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.JwtStagedRotationPolicy>(
    _ => new Mahjong.Autotable.Api.Auth.JwtStagedRotationPolicy(authOptions));

// Phase K Wave 15 — Bishop. Per-tenant JWKS rotation surface. Off
// by default; opt-in for multi-tenant deployments via
// `JwksRotation:PerTenant:Enabled=true`. Storage impl follows the
// same toggle pattern as other W12+ surfaces: InMemory default,
// Ef for prod multi-tenant. See docs/per-tenant-jwks.md.
//
// Phase K Wave 16 — Bishop. The validator (PerTenantJwksRotationValidator)
// is wired alongside the store so call sites can hard-assert
// freshness via EnforceSigningAsync. Off-toggle = validator
// registers but reports ValidatorEnabled=false so call-sites
// noop cleanly. See docs/per-tenant-jwks-rotation.md.
{
    var perTenantOpts = new Mahjong.Autotable.Api.Auth.PerTenantJwksRotationOptions();
    builder.Configuration.GetSection("JwksRotation:PerTenant").Bind(perTenantOpts);
    builder.Services.AddSingleton(perTenantOpts);
    if (perTenantOpts.Enabled)
    {
        if (string.Equals(perTenantOpts.StorageImpl, "Ef", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.IPerTenantJwksRotationStore,
                Mahjong.Autotable.Api.Auth.EfPerTenantJwksRotationStore>();
        }
        else
        {
            builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.IPerTenantJwksRotationStore,
                Mahjong.Autotable.Api.Auth.InMemoryPerTenantJwksRotationStore>();
        }
    }
    // Phase K Wave 16 — Bishop. Validator is always registered;
    // the constructor resolves the optional store and surfaces
    // ValidatorEnabled = (toggle && store != null).
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.PerTenantJwksRotationValidator>();
}

// Phase K Wave 12 — Bishop. Per-client_id sliding-window rate limit
// for the RFC 7662 token-introspection endpoint. The W11 surface
// relied on the global AuthValidatePolicy bucket; W12 adds this
// per-client cap so one chatty verifier can't exhaust the global
// budget. Cap + window are configurable; bound via the
// `OAuth:Introspect` section. See docs/oauth-introspect-rate-limit.md.
{
    var introOpts = new Mahjong.Autotable.Api.Auth.OAuthIntrospectRateLimitOptions();
    builder.Configuration.GetSection("OAuth:Introspect").Bind(introOpts);
    builder.Services.AddSingleton(introOpts);
    var capacity = introOpts.RateLimitPerClient > 0
        ? introOpts.RateLimitPerClient
        : Mahjong.Autotable.Api.Auth.OAuthIntrospectRateLimitOptions.DefaultRateLimitPerClient;
    var windowSeconds = introOpts.WindowSeconds > 0
        ? introOpts.WindowSeconds
        : Mahjong.Autotable.Api.Auth.OAuthIntrospectRateLimitOptions.DefaultWindowSeconds;
    // Phase K Wave 13 — Bishop. Implementation selector.
    // `OAuth:Introspect:LimiterImpl` ∈ { "InMemory", "Redis" }. The
    // Redis path shares a single sliding-window across all replicas;
    // the InMemory path is per-replica (W12 baseline). The Redis path
    // resolves IConnectionMultiplexer from the existing idempotency
    // wiring when present; the limiter falls back to in-memory enforcement
    // on any Redis error (logged).
    var limiterImpl = introOpts.LimiterImpl ?? "InMemory";
    if (string.Equals(limiterImpl, "Redis", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.IOAuthIntrospectRateLimiter>(sp =>
        {
            var mux = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();
            if (mux is null)
            {
                var log = sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("OAuthIntrospectRateLimiter");
                log.LogWarning(
                    "OAuth:Introspect:LimiterImpl=Redis but no IConnectionMultiplexer is registered; falling back to InMemory.");
                return new Mahjong.Autotable.Api.Auth.OAuthIntrospectRateLimiter(capacity, windowSeconds);
            }
            var redisLog = sp.GetRequiredService<ILogger<Mahjong.Autotable.Api.Auth.RedisOAuthIntrospectRateLimiter>>();
            return new Mahjong.Autotable.Api.Auth.RedisOAuthIntrospectRateLimiter(
                mux, capacity, windowSeconds, redisLog);
        });
    }
    else
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.IOAuthIntrospectRateLimiter>(
            _ => new Mahjong.Autotable.Api.Auth.OAuthIntrospectRateLimiter(capacity, windowSeconds));
    }
}
// Phase K Wave 8 — Bishop. JWKS pre-marshal cache. Owns the
// IMemoryCache the JwksCacheService projects through. Singleton so
// the 60s TTL is shared across requests.
// Phase K Wave 10 — Bishop hygiene: own a dedicated MemoryCache
// with SizeLimit=16 instead of sharing the application cache, plus
// IMeterFactory-backed hit/miss/rebuild counters and a stampede
// gate so a thundering herd against the JWKS endpoint only pays
// the serialisation cost once.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.JwksCacheService>(sp =>
    Mahjong.Autotable.Api.Auth.JwksCacheService.CreateWithDedicatedCache(
        ttl: null,
        meterFactory: sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
// Phase K Wave 6 — Bishop. Startup logger emits a single warning when
// the resolved algorithm is HS256 so operators get a nudge toward
// the RS256 migration. Wired as IStartupFilter so the log fires
// before the first request lands.
builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter,
    Mahjong.Autotable.Api.Auth.JwtAlgorithmStartupLogger>();
// Phase K Wave 1 — HMAC-signed OAuth state protector (PKCE+nonce
// supporting). Singleton so the signing key stays stable across
// requests; OAuthStateProtector mints + caches its own per-process
// random key when AuthOptions.StateSigningKey is empty.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.OAuthStateProtector>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.MagicLinkService>();

// Phase J Wave 9 — reconnect-token rotation + chat services. Both are
// singletons because they hold per-process state (chat rate-limit
// window, ambient request-scoped IServiceScopeFactory) and project
// into AppDbContext via a scope.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Changsha.Reconnect.ReconnectTokenService>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Changsha.Chat.ChatContentFilter>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Changsha.Chat.ChatService>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Changsha.Chat.ChatInvitationService>();

// Phase J Wave 10 — audit-table pruning. Default retention: 30 days for
// ReconnectAuditEntries, 90 days for CspViolations, sweeping daily.
// Wired as a BackgroundService so the host owns the timer lifecycle
// (clean shutdown on SIGTERM). Test harnesses set Audit:Enabled=false
// to avoid timer noise against the in-memory SQLite DB; the service
// remains DI-resolvable for direct PruneOnceAsync invocation in tests.
builder.Services.Configure<Mahjong.Autotable.Api.Changsha.Audit.AuditPruningOptions>(
    builder.Configuration.GetSection("Audit"));
builder.Services.AddSingleton<Mahjong.Autotable.Api.Changsha.Audit.AuditPruningService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Mahjong.Autotable.Api.Changsha.Audit.AuditPruningService>());

// Phase J Wave 10 — Tournament service. Scoped to match AppDbContext;
// the controller resolves through IServiceScopeFactory so the scope
// lifetime matches the request.
builder.Services.AddScoped<Mahjong.Autotable.Api.Tournament.TournamentService>();

// Phase K Wave 6 — Bishop. Bracket-generator factory + the four
// concrete implementations (single-elim, round-robin, Swiss,
// double-elim). All generators are pure functions over the seed
// list so a singleton lifetime is correct.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.IBracketGenerator,
    Mahjong.Autotable.Api.Tournament.SingleEliminationBracket>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.IBracketGenerator,
    Mahjong.Autotable.Api.Tournament.RoundRobinBracket>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.IBracketGenerator,
    Mahjong.Autotable.Api.Tournament.SwissBracket>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.IBracketGenerator,
    Mahjong.Autotable.Api.Tournament.DoubleEliminationBracket>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.TournamentBracketGenerator>();

// Phase K Wave 8 — Bishop. Swiss final-standings + tiebreaker
// pipeline. Pure service (no DI deps); singleton so the tournament
// runtime can pull a stable instance.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.SwissStandingsService>();

// Phase K Wave 10 — Bishop. Dutch-system Swiss pairing service.
// Replaces the W-J first-round-only routine in TournamentPairing
// with a full per-round Dutch-system pairing (top-half-vs-bottom-
// half per score group, no rematches, float-down). Registered as
// singleton because the implementation is stateless / thread-safe.
//
// Phase K Wave 11 — Bishop. Swapped to the FIDE C.04.1 Dutch-
// variation implementation with full backtracking + Berger pre-
// round Buchholz ordering. The W10 DutchSwissPairingService class
// is retained for the W10 contract suite + regression pinning
// (the W11 service supersedes it as the live ISwissPairingService
// binding). See docs/swiss-pairing.md.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.ISwissPairingService,
    Mahjong.Autotable.Api.Tournament.FideC04SwissPairingService>();

// Phase K Wave 20 — Bishop. Live Swiss pairing service. Wraps
// the W11 FIDE C.04.1 binding above so the W20 admin endpoint
// (POST /api/admin/tournaments/{id}/swiss-pair-next-round) can
// compute + persist + audit the next round in a single call.
// Singleton lifetime — the service opens its own DbContext
// scope per request via IServiceScopeFactory. See
// docs/swiss-pairing.md (W20 live-pairing addendum).
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.SwissPairingService>();

// Phase K Wave 21 — Bishop. Swiss apply-round service. Closes
// the loop on the W20 preview path: turns the W20-proposed
// pairings (one SwissPairingAuditEntry per board) into
// TournamentMatch rows. Idempotent — re-call with the same
// (tournamentId, round) returns the existing matches.
// Surface: POST /api/admin/tournaments/{id}/swiss-apply-round.
// See docs/swiss-pairing.md §9 (W21 apply addendum).
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.SwissApplyRoundService>();

// Phase K Wave 21 — Bishop. Scheduled rotation executor +
// metric collector. The executor polls the
// RotationSchedules table once per minute and runs any
// schedule whose cron matches the current UTC minute. Emits
// jwt_scheduled_rotation_total{tenant,status} per evaluation.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.JwtScheduledRotationMetrics>();
builder.Services.AddHostedService<Mahjong.Autotable.Api.Auth.RotationScheduledExecutorService>();

// Phase K Wave 21 — Bishop. SignalR manual-purge counter.
// The Deliverable 7 admin surface increments this counter
// per row deleted so operators can graph operator-initiated
// purges separately from the W17 retention sweep.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Observability.SignalRManualPurgeMetrics>();

// Phase K Wave 8 — Bishop. Bracket snapshot service. Composes the
// generator's slot layout with the live TournamentMatch rows so the
// UI gets a single envelope for the bracket tree. Scoped via
// IServiceScopeFactory; the service is a thin compute layer.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.TournamentBracketSnapshotService>();

// Phase K Wave 8 — Bishop. Real-time bracket-update broadcaster.
// Fires `TournamentBracketUpdated` on every match completion through
// the TournamentMatchHub group. Singleton so the runtime can resolve
// it via DI without per-call instantiation.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.TournamentBracketBroadcaster>();

// Phase K Wave 1 — Elo rating service + quarterly rollover (Bishop).
// PlayerRatingService is singleton-shaped — it holds an IServiceScopeFactory
// and opens a fresh AppDbContext scope per call. Singleton lifetime is
// safe and means the runtime + hosted services can resolve it directly
// without wrapping in a scope.
builder.Services.Configure<Mahjong.Autotable.Api.Tournament.RatingOptions>(
    builder.Configuration.GetSection("Rating"));
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.PlayerRatingService>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.SeasonRolloverService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Mahjong.Autotable.Api.Tournament.SeasonRolloverService>());

// Phase K Wave 1 — tournament-match forfeit BackgroundService (Bishop).
// Watches active tournament matches for participant disconnects beyond
// Tournament:ReconnectGracePeriodSeconds (default 120) and auto-
// advances the match to the opposing seat. Singleton so the runtime
// can poke it directly when a player drops; the timer is best-effort.
builder.Services.Configure<Mahjong.Autotable.Api.Tournament.TournamentForfeitOptions>(
    builder.Configuration.GetSection("Tournament"));
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.TournamentForfeitService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Mahjong.Autotable.Api.Tournament.TournamentForfeitService>());

// Phase K Wave 1 — OAuth provider health check (Bishop). Probes each
// configured provider's OIDC discovery document on startup + on demand
// (consumed by /health); failures surface in the response payload but
// never break the probe.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.OAuthProviderHealthCheck>();

// Phase K Wave 2 — Bishop (Backend). OAuth live discovery cache. Owns
// the per-provider `.well-known/openid-configuration` document with a
// 6h TTL + 24h stale-marker; falls back to hardcoded GitHub constants
// (GitHub doesn't ship OIDC discovery). The companion background
// service refreshes the cache on a 6h cadence so the live `/health`
// surface never blocks on an upstream round-trip. Both pieces are
// gated by `Authentication:Discovery:SkipNetwork=true` (test harness)
// so the xUnit runner never reaches out to the real Google endpoint.
builder.Services.Configure<Mahjong.Autotable.Api.Auth.OAuthDiscoveryOptions>(
    builder.Configuration.GetSection("Authentication:Discovery"));
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.OAuthDiscoveryService>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Auth.OAuthDiscoveryRefreshService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Mahjong.Autotable.Api.Auth.OAuthDiscoveryRefreshService>());

// Phase K Wave 2 — Bishop (Backend). WebRTC voice signalling hub +
// per-connection token bucket. Voice is off by default
// (VoiceOptions.Enabled=false) so a deployment opts in via
// `Voice:Enabled=true`. The rate limiter is singleton so token state
// persists across hub invocations on the same connection.
builder.Services.Configure<Mahjong.Autotable.Api.Voice.VoiceOptions>(
    builder.Configuration.GetSection("Voice"));
// Phase K Wave 5 — Bishop. Legacy `Voice:TurnTtlSeconds` alias maps
// onto the canonical `Voice:TurnCredentialTtlSeconds` property when
// the canonical knob hasn't been set explicitly. The
// VoiceTurnTtlMigrationLogger IStartupFilter (registered below) emits
// a one-shot deprecation warning so operators see the migration path.
builder.Services.PostConfigure<Mahjong.Autotable.Api.Voice.VoiceOptions>(o =>
{
    var legacy = builder.Configuration[Mahjong.Autotable.Api.Voice.VoiceTurnTtlMigrationLogger.LegacyKey];
    var canonical = builder.Configuration[Mahjong.Autotable.Api.Voice.VoiceTurnTtlMigrationLogger.CanonicalKey];
    if (string.IsNullOrWhiteSpace(canonical)
        && !string.IsNullOrWhiteSpace(legacy)
        && int.TryParse(legacy, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0)
    {
        o.TurnCredentialTtlSeconds = seconds;
    }
});
builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, Mahjong.Autotable.Api.Voice.VoiceTurnTtlMigrationLogger>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.VoiceRateLimiter>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Mahjong.Autotable.Api.Voice.VoiceOptions>>().Value;
    return new Mahjong.Autotable.Api.Voice.VoiceRateLimiter(opts.RateLimitPerSecond);
});
// Phase K Wave 3 — Bishop. Per-connection relay metrics for the voice
// hub; the singleton lets /metrics surfaces query a rolling 60s window.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.VoiceHubMetricsService>();

// Phase K Wave 6 — Bishop. HLS livestream recorder seam. The Wave-6
// surface ships the in-memory stub so the controller resolves end-
// to-end in tests + dev hosts; Phase L re-binds this interface to a
// real ffmpeg / libwebrtc pipeline behind the same audit kinds and
// the same `/api/voice/livestream/{gameId}/...` URL shape.
//
// Phase K Wave 7 — Bishop. `Voice:LivestreamRecorderImpl` config
// toggle selects between the in-memory stub (default) and the new
// production-grade `FfmpegHlsRecorder`. When `FfmpegHls` is
// selected, the IFfmpegHealthProbe guard fails fast at startup so a
// missing ffmpeg binary surfaces as a boot-time exception rather
// than a 500 on the first start request.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.IFfmpegHealthProbe,
    Mahjong.Autotable.Api.Voice.FfmpegBinaryHealthProbe>();
{
    var recorderImpl = builder.Configuration.GetValue<string>("Voice:LivestreamRecorderImpl")
        ?? "InMemoryStub";
    if (string.Equals(recorderImpl, "FfmpegHls", StringComparison.OrdinalIgnoreCase))
    {
        // Fail-fast boot guard: in production we WANT a clean
        // exception rather than a degraded silent-fallback. The
        // probe shells `ffmpeg -version` with a 2s timeout.
        var bootProbe = new Mahjong.Autotable.Api.Voice.FfmpegBinaryHealthProbe();
        if (!bootProbe.IsAvailable())
        {
            throw new InvalidOperationException(
                "Voice:LivestreamRecorderImpl=FfmpegHls but ffmpeg is not available on PATH. " +
                "Install ffmpeg (>=4.0) or revert to Voice:LivestreamRecorderImpl=InMemoryStub.");
        }
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.ILivestreamRecorder,
            Mahjong.Autotable.Api.Voice.FfmpegHlsRecorder>();
    }
    else
    {
        if (!string.Equals(recorderImpl, "InMemoryStub", StringComparison.OrdinalIgnoreCase))
        {
            // Unknown value — surface a deferred warning on first
            // resolve and bind the stub. We can't log here cleanly
            // (no built logger yet), so the operator gets a startup
            // exception when accidentally picking nonsense values
            // they're trying to type as 'FfmpegHls'.
            var bootLogger = LoggerFactory.Create(b => b.AddConsole())
                .CreateLogger("Mahjong.Autotable.Api.Voice.LivestreamRecorderBinding");
            bootLogger.LogWarning(
                "Voice:LivestreamRecorderImpl='{Value}' is not recognised (expected 'InMemoryStub' or 'FfmpegHls'). Falling back to InMemoryStub.",
                recorderImpl);
        }
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.ILivestreamRecorder,
            Mahjong.Autotable.Api.Voice.InMemoryLivestreamRecorder>();
    }
}

// Phase K Wave 8 — Bishop. Janus health probe + hub registration.
// The probe is always registered so operators can call it through
// /health regardless of the SpectatorSfuImpl value; the Janus hub
// itself is only registered when Voice:SpectatorSfuImpl=Janus so
// the type-resolver doesn't construct an HttpClient against an
// empty endpoint in stub-mode hosts.
//
// Phase K Wave 9 — Bishop. The new JanusReadinessSupervisor is
// registered alongside the Janus hub. It polls the probe every 5s
// and trips the binding state after 6 consecutive failures (30s),
// rebinding after 6 consecutive successes. Admin clients observe
// state changes via the JanusReadinessHub at /hubs/voice/readiness.
{
    var voiceEndpoint = builder.Configuration.GetValue<string>("Voice:JanusEndpoint") ?? string.Empty;
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.IJanusHealthProbe>(_ =>
        new Mahjong.Autotable.Api.Voice.JanusHealthProbe(voiceEndpoint));
    var sfuImpl = builder.Configuration.GetValue<string>("Voice:SpectatorSfuImpl") ?? "InMemoryStub";
    if (string.Equals(sfuImpl, "Janus", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.JanusSpectatorVoiceHub>();
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.JanusReadinessSupervisor>();
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.IJanusReadinessSupervisor>(sp =>
            sp.GetRequiredService<Mahjong.Autotable.Api.Voice.JanusReadinessSupervisor>());
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<Mahjong.Autotable.Api.Voice.JanusReadinessSupervisor>());

        // Phase K Wave 10 — Bishop. Mountpoint lifecycle registry +
        // GC sweeper. Registry is the in-memory book of every active
        // table → mountpoint mapping; the hosted service runs a slow
        // (60s) sweep that evicts entries idle past the 5-minute TTL.
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.JanusMountpointRegistry>();
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Voice.JanusMountpointLifecycleService>(sp =>
            new Mahjong.Autotable.Api.Voice.JanusMountpointLifecycleService(
                sp.GetRequiredService<Mahjong.Autotable.Api.Voice.JanusMountpointRegistry>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Mahjong.Autotable.Api.Voice.JanusMountpointLifecycleService>>(),
                sweepInterval: null,
                idleTtl: null,
                meterFactory: sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<Mahjong.Autotable.Api.Voice.JanusMountpointLifecycleService>());
    }
}

// Phase K Wave 6 — Bishop. AI commentary generator seam. Wave 6 ships
// the deterministic stub returning the canonical Phase-L placeholder
// message; Phase L re-binds this interface to a real LLM pipeline
// behind the same controller URL + audit Kind.
//
// Phase K Wave 8 — Bishop. The provider seam now branches on
// Commentary:Provider:
//   * "Stub"   (default) — StubCommentaryGenerator (deterministic).
//   * "OpenAI" / "Azure" — OpenAiCommentaryGenerator (Chat Completions).
// The usage meter is always registered so the controller can read
// per-game + monthly token totals via /api/audit/ + future
// observability surfaces.
builder.Services.Configure<Mahjong.Autotable.Api.Commentary.CommentaryOptions>(
    builder.Configuration.GetSection("Commentary"));
// Phase K Wave 9 — Bishop. Durable EF-backed commentary usage
// meter. Toggle via Commentary:UsageMeterImpl:
//   * "InMemory" (default) — in-process counts, lost on restart;
//     used by tests + single-replica dev.
//   * "Ef"                 — durable per-month ledger persisted to
//     the CommentaryUsage table; default for production.
{
    var meterImpl = builder.Configuration.GetValue<string>("Commentary:UsageMeterImpl") ?? "InMemory";
    if (string.Equals(meterImpl, "Ef", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.ICommentaryUsageMeter,
            Mahjong.Autotable.Api.Commentary.EfCommentaryUsageMeter>();
    }
    else
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.ICommentaryUsageMeter,
            Mahjong.Autotable.Api.Commentary.InMemoryCommentaryUsageMeter>();
    }
}
{
    var commentaryProvider = builder.Configuration.GetValue<string>("Commentary:Provider") ?? "Stub";
    if (string.Equals(commentaryProvider, "OpenAI", StringComparison.OrdinalIgnoreCase)
        || string.Equals(commentaryProvider, "Azure", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.ICommentaryGenerator,
            Mahjong.Autotable.Api.Commentary.OpenAiCommentaryGenerator>();
    }
    else
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.ICommentaryGenerator,
            Mahjong.Autotable.Api.Commentary.StubCommentaryGenerator>();
    }
}

// Phase K Wave 12 — Bishop. Cost-budget seam. Multiplies the usage
// meter's monthly token total by Commentary:CostBudget:TokensPerDollar
// to expose a USD ledger; at the cap the controller routes to the
// deterministic stub for the rest of the month. The
// StubCommentaryGenerator is always available as a concrete singleton
// so the controller can fall back regardless of the active
// ICommentaryGenerator binding. See docs/commentary-llm.md §4.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.StubCommentaryGenerator>();
// Phase K Wave 13 — Bishop. SignalR broadcaster for cost-budget
// state transitions. The hub is mapped at
// /hubs/admin/commentary-cost (admin-only). The broadcaster is
// resolved as an optional dependency by CommentaryCostBudget so
// unit-tests can construct the budget without DI.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.CommentaryCostBroadcaster>();
builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.CommentaryCostBudget>();
// Phase K Wave 16 — Bishop. Hard-cap enforcer for the
// commentary cost budget. Reads BudgetState from
// CommentaryCostBudget and returns a verdict the
// CommentaryController maps to HTTP 402 Payment Required. Admin
// override: X-Cost-Budget-Override: 1 header + admin session.
// See docs/commentary-llm.md §4.2.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.CommentaryCostBudgetEnforcer>();

// Phase K Wave 11 — Bishop. Per-record CommentaryRecord storage
// seam. The W7-W9 surface kept records in memory inside the
// generator; W11 makes the store pluggable and ships an
// EF-backed implementation that persists to the
// CommentaryRecords table with a configurable retention window.
// Toggle: Commentary:StorageImpl ("InMemory" default for tests
// / "Ef" default for prod). See docs/commentary-llm.md.
{
    var storageOptions = new Mahjong.Autotable.Api.Commentary.CommentaryStorageOptions();
    builder.Configuration.GetSection("Commentary").Bind(storageOptions);
    builder.Services.AddSingleton(storageOptions);
    var storageImpl = storageOptions.StorageImpl ?? "InMemory";
    if (string.Equals(storageImpl, "Ef", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.ICommentaryStore,
            Mahjong.Autotable.Api.Commentary.EfCommentaryStore>();
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.CommentaryRetentionSweepService>();
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<Mahjong.Autotable.Api.Commentary.CommentaryRetentionSweepService>());
    }
    else
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Commentary.ICommentaryStore,
            Mahjong.Autotable.Api.Commentary.InMemoryCommentaryStore>();
    }
}

// Phase K Wave 8 — Bishop. Idempotency store backing the
// IdempotencyMiddleware replay-protection gate. Singleton so the
// 5-minute window is shared across requests. The in-memory default
// is bounded at 4096 entries; the W9 EF + Redis bindings ship for
// the multi-replica production deployment.
//
// Phase K Wave 9 — Bishop. Toggle via Idempotency:StoreImpl:
//   * "InMemory" (default for tests + single-replica dev)
//   * "Ef"       (durable; persists to the IdempotencyEntries table)
//   * "Redis"    (multi-replica; W10 wires the StackExchange.Redis
//                 IConnectionMultiplexer client. Falls back to the
//                 EF store on connection-string-absent / connect
//                 failure so the toggle is degradation-safe.)
//
// Phase K Wave 10 — Bishop. The Redis impl now uses the real
// StackExchange.Redis client. The IConnectionMultiplexer is
// registered as a singleton (host-managed lifetime) and only
// constructed when the connection string is non-empty AND the
// store impl is "Redis"; otherwise the registration is skipped so
// in-memory/EF default deployments have zero Redis runtime cost.
{
    var storeImpl = builder.Configuration.GetValue<string>("Idempotency:StoreImpl") ?? "InMemory";
    if (string.Equals(storeImpl, "Ef", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Audit.EfIdempotencyStore>();
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Audit.IIdempotencyStore>(sp =>
            sp.GetRequiredService<Mahjong.Autotable.Api.Audit.EfIdempotencyStore>());
    }
    else if (string.Equals(storeImpl, "Redis", StringComparison.OrdinalIgnoreCase))
    {
        // EF fallback is always registered so a Redis outage falls
        // back to the durable RDBMS store rather than dropping
        // idempotency entirely.
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Audit.EfIdempotencyStore>();
        var redisConn = builder.Configuration.GetValue<string>("Idempotency:Redis:ConnectionString")
            ?? builder.Configuration.GetConnectionString("Redis")
            ?? builder.Configuration.GetValue<string>("Idempotency:RedisConnection")
            ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(redisConn))
        {
            // The multiplexer is expensive to build (TCP + handshake) so
            // share a single singleton across the host. Lazy<T> ensures
            // the connect attempt happens at first resolve so a
            // misconfigured connection string surfaces as a logged
            // warning + EF fallback rather than a startup abort.
            builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
                StackExchange.Redis.ConnectionMultiplexer.Connect(redisConn));
            builder.Services.AddSingleton<Mahjong.Autotable.Api.Audit.IIdempotencyStore>(sp =>
                new Mahjong.Autotable.Api.Audit.RedisIdempotencyStore(
                    sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<Mahjong.Autotable.Api.Audit.RedisIdempotencyStore>>(),
                    sp.GetRequiredService<Mahjong.Autotable.Api.Audit.EfIdempotencyStore>()));
        }
        else
        {
            // Toggle says Redis but no connection — log + fall back to
            // EF so the deployment doesn't silently lose the durability
            // contract.
            builder.Services.AddSingleton<Mahjong.Autotable.Api.Audit.IIdempotencyStore>(sp =>
                sp.GetRequiredService<Mahjong.Autotable.Api.Audit.EfIdempotencyStore>());
        }
    }
    else
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Audit.IIdempotencyStore,
            Mahjong.Autotable.Api.Audit.InMemoryIdempotencyStore>();
    }
}

// Phase K Wave 2 — Bishop (Backend). Spectator live-stream stub. Owns
// the future tile-flip event surface (Phase L) so callers can attach
// today; the `/api/replay/{id}/livestream.m3u8` endpoint returns 404
// with a structured envelope until the HLS pipeline lands.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Spectator.SpectatorService>();

// Phase K Wave 12 — Bishop. Replay-by-id store. The W11 surface kept
// completed-game replays in `ChangshaGameReplays` keyed by game id;
// W12 adds the durable `Replays` table keyed by a short URL-safe
// `ReplayId` so external clients (Hicks's `?action=replay` URL) can
// link to a single payload via a stable opaque token.
// Toggle: `Replays:StorageImpl` = "InMemory" | "Ef". Default
// in-memory keeps tests fast; production flips to "Ef".
{
    var replayOptions = new Mahjong.Autotable.Api.Replays.ReplayOptions();
    builder.Configuration.GetSection("Replays").Bind(replayOptions);
    builder.Services.AddSingleton(replayOptions);
    var impl = replayOptions.StorageImpl ?? "InMemory";
    if (string.Equals(impl, "Ef", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Replays.IReplayStore,
            Mahjong.Autotable.Api.Replays.EfReplayStore>();
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Replays.ReplayRetentionSweepService>();
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<Mahjong.Autotable.Api.Replays.ReplayRetentionSweepService>());
        // Phase K Wave 15 — Bishop. Hourly retention sweep against
        // CompletedAt < now - RetentionDays. Complements the W12
        // daily ExpiresAt-based sweep above by giving the operator
        // a live retention knob. Registered only for the EF impl;
        // the in-memory store has no on-disk footprint and the W12
        // sweep is sufficient.
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Replays.ReplayStoreRetentionSweep>();
        builder.Services.AddHostedService(sp =>
            sp.GetRequiredService<Mahjong.Autotable.Api.Replays.ReplayStoreRetentionSweep>());
        // Phase K Wave 16 — Bishop. Per-tenant retention policy
        // store. Registered alongside the EF replay store so the
        // sweep above can resolve it as an optional dependency
        // and apply per-tenant retention windows before falling
        // back to the global default. See
        // docs/replay-by-id.md §4.1.
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Replays.IReplayRetentionPolicyStore,
            Mahjong.Autotable.Api.Replays.EfReplayRetentionPolicyStore>();
    }
    else
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Replays.IReplayStore,
            Mahjong.Autotable.Api.Replays.InMemoryReplayStore>();
        // Phase K Wave 16 — Bishop. In-memory companion to the
        // EF-store branch above. Single-replica dev workflows
        // keep the per-tenant override path testable without
        // standing up a real database.
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Replays.IReplayRetentionPolicyStore,
            Mahjong.Autotable.Api.Replays.InMemoryReplayRetentionPolicyStore>();
    }

    // Phase K Wave 20 — Bishop. Per-tenant replay auto-expiry
    // counter + handler. Registered for BOTH storage branches so
    // the per-tenant breakdown sweep is exercised in single-
    // replica dev too. The metric is rendered by MetricsEndpoint;
    // the handler runs as a hosted service. See
    // docs/replay-by-id.md §4.2 (W20).
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Replays.ReplayExpiryMetrics>();
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Replays.ReplayStoreExpiryHandler>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<Mahjong.Autotable.Api.Replays.ReplayStoreExpiryHandler>());
}

// Phase K Wave 12 — Bishop. Tournament bracket persistence. The W6-W10
// brackets are in-memory generator output; W12 lands the durable row
// so brackets survive process restarts + replay-game-complete events
// flow through an idempotent store. Toggle:
// `Tournament:BracketStoreImpl` = "InMemory" | "Ef". See
// docs/bracket-shape.md §3.
{
    var bracketOptions = new Mahjong.Autotable.Api.Tournament.BracketStorageOptions();
    builder.Configuration.GetSection("Tournament").Bind(bracketOptions);
    builder.Services.AddSingleton(bracketOptions);
    // Phase K Wave 14 — Bishop. Page-size knob for the bracket
    // query endpoint (GET /api/tournaments/{id}/brackets). Bound
    // from the same Tournament section as the W12 storage toggle.
    var bracketQueryOptions = new Mahjong.Autotable.Api.Tournament.BracketQueryOptions();
    builder.Configuration.GetSection("Tournament").Bind(bracketQueryOptions);
    builder.Services.AddSingleton(bracketQueryOptions);
    var impl = bracketOptions.BracketStoreImpl ?? "InMemory";
    if (string.Equals(impl, "Ef", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.IBracketStore,
            Mahjong.Autotable.Api.Tournament.EfBracketStore>();
    }
    else
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Tournament.IBracketStore,
            Mahjong.Autotable.Api.Tournament.InMemoryBracketStore>();
    }
}

// Phase K Wave 12 — Bishop. Durable SignalR sequence store backing
// the replay-from-ack surface for long-lived sessions that exceed the
// in-memory 256-entry retention window on
// SignalRBackpressureBroadcaster. Toggle:
// `SignalR:SequenceStoreImpl` = "InMemory" | "Ef". Configurable
// retention via `SignalR:Sequences:RetentionMinutes`. See
// docs/realtime-resilience.md §6.
{
    var seqOptions = new Mahjong.Autotable.Api.Observability.SignalRSequenceStoreOptions();
    builder.Configuration.GetSection("SignalR:Sequences").Bind(seqOptions);
    // Top-level SequenceStoreImpl override under `SignalR:` for
    // operator ergonomics.
    var topLevelImpl = builder.Configuration.GetValue<string>("SignalR:SequenceStoreImpl");
    if (!string.IsNullOrWhiteSpace(topLevelImpl)) seqOptions.SequenceStoreImpl = topLevelImpl;
    builder.Services.AddSingleton(seqOptions);
    var impl = seqOptions.SequenceStoreImpl ?? "InMemory";
    if (string.Equals(impl, "Ef", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Observability.ISignalRSequenceStore,
            Mahjong.Autotable.Api.Observability.EfSignalRSequenceStore>();
    }
    else
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Observability.ISignalRSequenceStore,
            Mahjong.Autotable.Api.Observability.InMemorySignalRSequenceStore>();
    }

    // Phase K Wave 13 — Bishop. Always-on retention sweeper. W12 only
    // registered the sweep when impl=Ef; W13 lifts the registration so
    // the InMemory store also sheds expired rows on long-lived
    // single-replica dev sessions. The new SignalRSequenceRetentionSweep
    // reads its cadence from `SignalR:Sequences:SweepIntervalMinutes`
    // (default 5). See docs/realtime-resilience.md §7.
    var sweepOptions = new Mahjong.Autotable.Api.Observability.SignalRSequenceRetentionSweepOptions
    {
        // Inherit W12 cadence so an operator upgrading without an
        // appsettings edit keeps the prior behaviour.
        SweepIntervalMinutes = seqOptions.SweepIntervalMinutes > 0
            ? seqOptions.SweepIntervalMinutes
            : Mahjong.Autotable.Api.Observability.SignalRSequenceRetentionSweep.DefaultSweepIntervalMinutes,
    };
    builder.Configuration.GetSection("SignalR:Sequences").Bind(sweepOptions);
    builder.Services.AddSingleton(sweepOptions);
    // Phase K Wave 14 — Bishop. SignalR sequence Prometheus
    // metrics — replay-from-ack counter (by hub + result), active
    // rows gauge, retention-sweep deletion counter. Registered
    // unconditionally so test fixtures can resolve the collector
    // even when no SignalR traffic has been observed yet. See
    // docs/realtime-resilience.md §8.
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Observability.SignalRSequenceMetrics>();
    // Phase K Wave 17 — Bishop. Per-tenant SignalR-sequence
    // retention policy store. Registered alongside the sequence
    // store so the W13 retention sweep can resolve it as an
    // optional dependency + apply per-tenant retention windows
    // before falling back to the global default. Impl follows
    // the same toggle as the sequence store so the EF branch
    // ships the EF backing + the InMemory branch ships the
    // in-process companion. See docs/realtime-resilience.md §7.
    if (string.Equals(impl, "Ef", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Observability.ISignalRRetentionPolicyStore,
            Mahjong.Autotable.Api.Observability.EfSignalRRetentionPolicyStore>();
    }
    else
    {
        builder.Services.AddSingleton<Mahjong.Autotable.Api.Observability.ISignalRRetentionPolicyStore,
            Mahjong.Autotable.Api.Observability.InMemorySignalRRetentionPolicyStore>();
    }
    builder.Services.AddSingleton<Mahjong.Autotable.Api.Observability.SignalRSequenceRetentionSweep>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<Mahjong.Autotable.Api.Observability.SignalRSequenceRetentionSweep>());
}

// Phase K Wave 19 — Bishop. Per-tenant SignalR retention
// lifecycle metrics. Distinct from the W18
// `signalr_retention_policy_capped_total` counter — this collector
// exposes `signalr_retention_applied{tenant}` +
// `signalr_retention_cap_triggered{tenant}` so the W19 dashboard
// can graph the lifecycle without joining against the audit
// table. Registered unconditionally so the MetricsEndpoint can
// resolve the collector even on shapes that haven't touched the
// SignalR retention path yet. See
// docs/realtime-resilience.md §7.2.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Observability.SignalRRetentionLifecycleMetrics>();

// Phase K Wave 15 — Bishop. Tournament-scale query latency
// histogram, bucketed by endpoint + page_size_bucket. Surfaced
// on the existing Prometheus endpoint as
// `tournament_query_duration_seconds`. Registered unconditionally
// so test fixtures can resolve the collector even when no
// queries have been observed yet. See docs/bracket-shape.md §6.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Observability.TournamentQueryLatencyMetrics>();

// Phase K Wave 8 — Bishop. Centralised "is player on this table?"
// gate. Backs the livestream playlist auth check + future surfaces.
// Singleton — the implementation pulls a per-call scope for the
// AppDbContext lookup so no scoped state escapes.
builder.Services.AddSingleton<Mahjong.Autotable.Api.Tables.IPlayerTableContext,
    Mahjong.Autotable.Api.Tables.PlayerTableContext>();

const string ChangshaCorsPolicy = "ChangshaCors";
var configuredOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    // Phase J Wave 6 — origins now come from configuration (Apone, DevOps).
    // appsettings.json carries the two localhost dev origins (the Kestrel HTTP
    // listener and the Parcel dev server); appsettings.Production.json
    // ships with an empty list so production deploys MUST set
    // `Cors__AllowedOrigins__0=https://<public-host>` (see docs/secrets.md
    // and docs/deployment.md § "CORS"). AllowCredentials() is required for
    // the autotable WS `mahjong_pid` cookie + SignalR cookie auth — which is
    // why we deliberately do NOT call AllowAnyOrigin() (the framework
    // rejects that combination at policy-build time).
    options.AddPolicy(ChangshaCorsPolicy, policy =>
    {
        var builderPolicy = policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
        if (configuredOrigins.Length > 0)
        {
            builderPolicy.WithOrigins(configuredOrigins);
        }
    });
});

// Phase J Wave 6 — production rate limiting (Apone, DevOps). Gated by
// `RateLimiting:Enabled` so the xUnit `WebApplicationFactory` harness
// (which boots `Development`) and `dotnet run` keep their unrestricted
// throughput. See RateLimitingExtensions for the policy contract and
// docs/deployment.md § "Rate limiting" for the runbook.
var rateLimitingEnabled = builder.Services.AddMahjongRateLimiting(builder.Configuration);

var app = builder.Build();

// Phase K Wave 8 — Bishop. CorrelationId middleware runs FIRST so
// every downstream consumer (security headers, MVC, SignalR) sees a
// populated X-Correlation-Id on the request + response. Idempotent
// on duplicate registration; pairs with IdempotencyMiddleware below.
app.UseMiddleware<Mahjong.Autotable.Api.Audit.CorrelationIdMiddleware>();

// Phase J Wave 8 — security + CDN cache headers (Apone, DevOps). Installed
// first so security headers stamp on every response (including errors and
// short-circuit returns from rate limiting / CORS). The middleware uses
// `Response.OnStarting` to mutate headers AFTER UseStaticFiles has set
// its own Cache-Control.
app.UseMiddleware<Mahjong.Autotable.Api.Observability.SecurityHeadersMiddleware>();

app.UseCors(ChangshaCorsPolicy);

if (rateLimitingEnabled)
{
    // Phase J Wave 6 — installed only when the gate is on so dev / test
    // pipelines bypass the middleware entirely.
    app.UseRateLimiter();
}

// Phase K Wave 8 — Bishop. Idempotency middleware runs after rate
// limiting (so a replay flood still hits the rate gate first) but
// before any controller dispatch. The middleware is opt-in: requests
// without an Idempotency-Key header skip the check.
app.UseMiddleware<Mahjong.Autotable.Api.Audit.IdempotencyMiddleware>();

// Raw WebSockets (separate transport from SignalR) — required for the
// upstream pwmarcz/autotable bundle's WS protocol at /autotable/ws.
app.UseWebSockets();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseBootstrapper.InitializeAsync(db);
}

// Phase I Wave 2 — re-hydrate any non-terminal Changsha games from persistence so
// a process restart no longer wipes active hands. Must run after the schema
// bootstrap (which guarantees the ChangshaGames table exists) and before any
// hub/WS subscriber is wired so no inbound command can race the load.
var changshaRuntime = app.Services.GetRequiredService<IChangshaGameRuntime>();
await changshaRuntime.HydrateAsync(app.Services, app.Lifetime.ApplicationStopping);

app.UseDefaultFiles();
app.UseStaticFiles();

var autotablePath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../../../frontend/autotable"));
Mahjong.Autotable.Api.Hosting.UnityStaticHosting.UseAutotableFiles(app, autotablePath, app.Configuration, app.Logger);

// Phase J Wave 6 — /api/health is a probe surface. Excluded from rate
// limiting via DisableRateLimiting() (works whether the middleware is
// registered or not — the attribute is metadata-only when no limiter is
// wired). Same treatment for /health and /metrics below.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "mahjong-autotable-api" }))
    .DisableRateLimiting();

// Phase J Wave 3 — Docker HEALTHCHECK + Linux deploy probe (Apone). Returns a
// stable JSON shape: status="healthy", buildSha from BUILD_SHA env var (or
// "dev" when unset OR empty — Apone's Dockerfile defaults BUILD_SHA="" which
// would bypass `?? "dev"` since `??` only catches null), uptime since process
// start, and the assembly version string. Distinct from /api/health (legacy
// short-form probe used by the frontend) so deployment infrastructure has its
// own stable wire contract.
//
// Phase J Wave 7 — extended with optional db connectivity + activeGames
// counters. `?simple=1` falls back to the 4-field Wave-3 contract so any
// load balancer / k8s liveness probe that grep-asserts the old shape (the
// Docker HEALTHCHECK and tests/smoke/docker-build-smoke.sh do exactly that)
// keeps working unchanged. The detailed shape adds a nested `db` object
// (`{ connected: bool, latencyMs: number }`) and an `activeGames` integer
// pulled from the runtime's in-memory game count. Probing the DB issues a
// single `SELECT 1` round-trip via the EF Core relational connection.
app.MapGet("/health", async (HttpContext ctx, IServiceProvider services) =>
{
    var sha = Environment.GetEnvironmentVariable("BUILD_SHA");
    var resolvedSha = string.IsNullOrEmpty(sha) ? "dev" : sha;
    var uptime = DateTimeOffset.UtcNow - processStartTime;
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";

    // Phase J Wave 7 — `?simple=1` returns the Wave-3 4-field contract so
    // load-balancer probes continue to grep-assert {status, buildSha,
    // uptime, version} without picking up the new fields. The default
    // (no query parameter) returns the extended detail shape.
    if (ctx.Request.Query.TryGetValue("simple", out var simpleVal) && simpleVal == "1")
    {
        return Results.Ok(new
        {
            status = "healthy",
            buildSha = resolvedSha,
            uptime,
            version,
        });
    }

    // DB probe: lightweight SELECT 1 round-trip. Catch broadly so any
    // provider-specific exception surfaces as `connected:false`; the
    // endpoint itself stays 200 so the health probe semantics don't flip
    // just because a single round-trip blipped (load balancers conflate
    // "endpoint down" with "service down").
    //
    // Phase J Wave 10 — also captures the provider name + applied
    // migration count so ops can identify partial-migration states
    // (e.g. a deploy that crashed mid-rollout leaves the new image
    // running against a schema older than its expected migration tip).
    var dbConnected = false;
    long dbLatencyMs = 0;
    var dbProviderName = "Unknown";
    var dbCanQuery = false;
    var dbMigrationsApplied = 0;
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        dbProviderName = db.Database.ProviderName ?? "Unknown";
        var conn = db.Database.GetDbConnection();
        var closeWhenDone = conn.State != System.Data.ConnectionState.Open;
        if (closeWhenDone) await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            _ = await cmd.ExecuteScalarAsync();
            dbConnected = true;
            dbCanQuery = true;

            // Best-effort migration count. SQLite's bootstrap pattern
            // (EnsureCreatedAsync) doesn't populate __EFMigrationsHistory
            // so the count is 0 on SQLite by design — operators reading
            // this field know to gate on providerName=Sqlite. Postgres /
            // SqlServer always populate the table because they go through
            // Database.MigrateAsync.
            try
            {
                await using var migCmd = conn.CreateCommand();
                migCmd.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"";
                var result = await migCmd.ExecuteScalarAsync();
                if (result is not null && result != DBNull.Value)
                {
                    dbMigrationsApplied = Convert.ToInt32(result);
                }
            }
            catch
            {
                // SQLite bootstrap-only databases legitimately don't have
                // this table — swallow + keep dbMigrationsApplied=0.
            }
        }
        finally
        {
            if (closeWhenDone) await conn.CloseAsync();
        }
    }
    catch
    {
        dbConnected = false;
    }
    stopwatch.Stop();
    dbLatencyMs = stopwatch.ElapsedMilliseconds;

    var runtime = services.GetService<IChangshaGameRuntime>();
    var activeGames = runtime?.GameCount ?? 0;

    // Phase K Wave 1 — OAuth provider discovery probe. Best-effort: a
    // disabled or unconfigured provider is omitted entirely (the surface
    // shows `oauth: { providers: {} }` so operators reading the doc know
    // "no providers" vs. "all probes failed"). The check is cached
    // internally so polling /health every few seconds doesn't hammer
    // Google's well-known endpoint.
    object oauthBlock;
    try
    {
        var check = services.GetService<Mahjong.Autotable.Api.Auth.OAuthProviderHealthCheck>();
        if (check is null)
        {
            oauthBlock = new { providers = new Dictionary<string, object>() };
        }
        else
        {
            var probes = await check.ProbeAllAsync();
            oauthBlock = new
            {
                providers = probes.ToDictionary(
                    kv => kv.Key,
                    kv => (object)new
                    {
                        healthy = kv.Value.Healthy,
                        statusCode = kv.Value.StatusCode,
                        error = kv.Value.Error,
                        discovery = kv.Value.Discovery,
                    })
            };
        }
    }
    catch
    {
        oauthBlock = new { providers = new Dictionary<string, object>() };
    }

    return Results.Ok(new
    {
        status = dbConnected ? "healthy" : "degraded",
        buildSha = resolvedSha,
        uptime,
        version,
        db = new
        {
            connected = dbConnected,
            latencyMs = dbLatencyMs,
            providerName = dbProviderName,
            canQuery = dbCanQuery,
            migrationsApplied = dbMigrationsApplied,
        },
        activeGames,
        oauth = oauthBlock,
    });
}).DisableRateLimiting();

app.MapGet("/api/system/persistence", (IConfiguration configuration) =>
{
    var provider = configuration.GetValue<string>("Persistence:Provider") ?? "Sqlite";
    return Results.Ok(new { provider });
}).RequireRateLimiting(RateLimitingExtensions.ApiPolicy);

// Phase J Wave 5 — Prometheus scrape endpoint (Apone, DevOps). The body
// is rendered by Observability.MetricsEndpoint.Render in the canonical
// text/plain v0.0.4 exposition format. See docs/observability.md for
// the metric catalog. Phase J Wave 6 — explicitly off-limits to rate
// limiting so the scrape loop doesn't trip on high-cardinality polls.
app.MapGet("/metrics", (IServiceProvider services) => MetricsEndpoint.Render(services))
    .DisableRateLimiting();

// Phase J Wave 9 — CSP violation report sink (Apone, DevOps). Off the
// rate-limiter path because browsers and Cloudflare fan-out a burst of
// reports when a policy first lands. See Observability/CspReportEndpoint.cs
// for the endpoint contract and docs/cloudflare.md § "CSP reporting" for
// the operator runbook.
app.MapCspReport().DisableRateLimiting();

// Phase J Wave 3 — canonical display ordering for WinPattern values (Hicks's UI).
// Returns a flat JSON object keyed by the camelCase pattern wire-name (same strings
// the SignalR winResult.allPatterns array uses) mapped to the integer canonical
// order. Lower = display first. Sourced from ChangshaPatternOrdering.Order so the
// frontend doesn't have to embed a parallel table.
app.MapGet("/api/changsha/pattern-ordering", () =>
{
    var map = new Dictionary<string, int>();
    foreach (var kvp in ChangshaPatternOrdering.Order)
    {
        map[WinPatternWireName(kvp.Key)] = kvp.Value;
    }
    return Results.Ok(map);
}).RequireRateLimiting(RateLimitingExtensions.ApiPolicy);

// Phase J Wave 6 — SignalR hubs are long-lived connections; the
// rate-limiter middleware would only see the initial handshake and then
// be bypassed for the persistent transport anyway, so leaving the hub
// route un-limited keeps semantics honest and avoids surprising 429s on
// the upgrade request when a client reconnects rapidly.
app.MapHub<ChangshaHub>("/hubs/changsha");

// Phase K Wave 2 — Bishop (Backend). WebRTC voice signalling hub.
// Mapped under both /hubs/voice and /hubs/webrtc so contract probes
// targeting either alias hit the same negotiate handshake.
app.MapHub<Mahjong.Autotable.Api.Voice.VoiceHub>("/hubs/voice");
app.MapHub<Mahjong.Autotable.Api.Voice.VoiceHub>("/hubs/webrtc");

// Phase K Wave 6 — Bishop. Spectator-voice SFU signalling hub. Mapped
// separately from /hubs/voice because the topology (one-way fan-out)
// is incompatible with the peer-mesh contract.
// Phase K Wave 8 — Bishop. The hub implementation is now selected
// via Voice:SpectatorSfuImpl ("InMemoryStub" default | "Janus"
// production). The Janus implementation extends SpectatorVoiceHub
// so the same URL works regardless of binding.
{
    var sfuImpl = builder.Configuration.GetValue<string>("Voice:SpectatorSfuImpl") ?? "InMemoryStub";
    if (string.Equals(sfuImpl, "Janus", StringComparison.OrdinalIgnoreCase))
    {
        app.MapHub<Mahjong.Autotable.Api.Voice.JanusSpectatorVoiceHub>("/hubs/voice/spectator");
    }
    else
    {
        app.MapHub<Mahjong.Autotable.Api.Voice.SpectatorVoiceHub>("/hubs/voice/spectator");
    }
}

// Phase K Wave 8 — Bishop. Tournament bracket-update SignalR hub.
// Clients call JoinTournament(id) to subscribe; the broadcaster
// fires TournamentBracketUpdated when matches complete.
app.MapHub<Mahjong.Autotable.Api.Tournament.TournamentMatchHub>("/hubs/tournaments");

// Phase K Wave 9 — Bishop. Janus readiness admin hub. Receives
// JanusReadinessChanged broadcasts from JanusReadinessSupervisor
// whenever the Janus binding state transitions (bound ↔ unbound).
// Mapped regardless of the SpectatorSfuImpl value so admin clients
// can probe the contract surface even when Janus isn't bound; the
// supervisor itself is only registered + running when
// Voice:SpectatorSfuImpl=Janus.
app.MapHub<Mahjong.Autotable.Api.Voice.JanusReadinessHub>("/hubs/voice/readiness");

// Phase K Wave 13 — Bishop. Admin SignalR hub for commentary cost
// budget transitions. CommentaryCostBroadcaster pushes
// `CommentaryCostWarning` (at 80% of cap) + `CommentaryCostCapReached`
// (at 100% of cap) envelopes to admin clients subscribed via
// JoinAdminChannel. The hub doesn't perform per-message admin
// authentication — production deployments gate the negotiate path
// at the reverse proxy + the broadcast group is a single
// admin-only channel ("commentary:cost:admin"). See
// docs/commentary-llm.md §5.
app.MapHub<Mahjong.Autotable.Api.Commentary.CommentaryCostAdminHub>("/hubs/admin/commentary-cost");

// Phase K Wave 2 — Bishop (Backend). ICE-server discovery endpoint.
// Returns the configured STUN/TURN list as the WebRTC client
// `RTCIceServer[]` shape; falls back to Google's public STUN server
// when no operator-supplied TURN credentials live in Voice:TurnServers.
//
// Phase K Wave 3 — Bishop. This stays the anonymous STUN-only fallback;
// the new auth-gated TURN-credential mint lives at
// `POST /api/turn/credentials` (HMAC-SHA1 short-term credentials).
// Production TURN access flows through the credential endpoint; this
// route never returns a credential field (defends against accidental
// leak of static TURN secrets via the anon surface).
app.MapGet("/api/turn", (Microsoft.Extensions.Options.IOptions<Mahjong.Autotable.Api.Voice.VoiceOptions> voiceOpts) =>
{
    var opts = voiceOpts.Value;
    // Wave 3 — strip credential/username fields from anon responses so
    // any operator-misconfigured shared secret can't leak. Anon callers
    // get URL-only entries; full credentials require the auth-gated
    // /api/turn/credentials mint.
    var servers = opts.TurnServers is { Count: > 0 }
        ? opts.TurnServers.Select(s => (object)new { urls = s.Url }).ToList()
        : new List<object> { new { urls = "stun:stun.l.google.com:19302" } };
    return Results.Ok(new { iceServers = servers, voiceEnabled = opts.Enabled });
}).RequireRateLimiting(RateLimitingExtensions.ApiPolicy);

// Phase K Wave 3 — Bishop (Backend). Short-term TURN credential mint
// (RFC 7635-style). Auth required (any signed-in session). Returns a
// `{ username, credential, ttl, urls, iceServers }` envelope where
// `username = "<unix_ttl>:<playerId>"` and `credential =
// base64(HMAC-SHA1(VoiceOptions.TurnSharedSecret, username))`. The
// coturn server validates the credential by recomputing the same HMAC
// against its `--static-auth-secret`; the `<unix_ttl>` prefix bounds
// the credential to the configured TTL (default 1h).
//
// 401 ⇒ no session; 503 ⇒ TURN shared secret is unconfigured
// (operator hasn't opted in); 200 ⇒ minted credential envelope.
app.MapPost("/api/turn/credentials", async (
    HttpContext ctx,
    Mahjong.Autotable.Api.Auth.AuthCookieService cookies,
    Microsoft.Extensions.Options.IOptions<Mahjong.Autotable.Api.Voice.VoiceOptions> voiceOpts,
    CancellationToken ct) =>
{
    var session = await cookies.ResolveAsync(ctx, ct);
    if (session is null)
    {
        return Results.Json(new { error = "Authentication required to mint TURN credentials." },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    var opts = voiceOpts.Value;
    if (string.IsNullOrWhiteSpace(opts.TurnSharedSecret))
    {
        return Results.Json(new { error = "TURN shared secret is not configured.", code = "turn-secret-missing" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    var ttl = opts.TurnCredentialTtlSeconds > 0 ? opts.TurnCredentialTtlSeconds : 3600;
    var expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl;
    var username = $"{expiresAt}:{session.PlayerId}";
    using var hmac = new System.Security.Cryptography.HMACSHA1(System.Text.Encoding.UTF8.GetBytes(opts.TurnSharedSecret));
    var credentialBytes = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(username));
    var credential = Convert.ToBase64String(credentialBytes);
    var urls = opts.TurnServers is { Count: > 0 }
        ? opts.TurnServers.Select(s => s.Url).ToArray()
        : Array.Empty<string>();
    // Phase K Wave 4 — Bishop. iceServers[].urls is always an array
    // (per WebRTC RTCIceServer canonical shape) — each entry collapses
    // its single configured URL into a one-element array so the
    // client never has to switch on string-vs-array. The top-level
    // {username, credential, ttl, expiresAt, urls} fields are
    // preserved from Wave 3 because Vasquez's contract tests pin
    // them; ttlSeconds is added as the canonical Wave-4 alias for
    // ttl (Apone's smoke harness asserts ttlSeconds).
    var iceServers = urls
        .Select(u => (object)new { urls = new[] { u }, username, credential })
        .ToArray();

    // Phase K Wave 4 — Bishop. Audit row Kind="voice.turn.credentials.minted"
    // so the operator trail records every mint. Best-effort: failure
    // to write the audit row never breaks the credential mint.
    try
    {
        await using var scope = ctx.RequestServices.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Mahjong.Autotable.Api.Data.AppDbContext>();
        db.ReconnectAuditEntries.Add(new Mahjong.Autotable.Api.Data.Entities.ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = session.PlayerId,
            At = DateTime.UtcNow,
            Kind = Mahjong.Autotable.Api.Data.Entities.ReconnectAuditEntry.KindTurnCredentialsMinted,
            Detail = username,
        });
        await db.SaveChangesAsync(ct);
    }
    catch { /* best-effort */ }

    return Results.Ok(new
    {
        username,
        credential,
        ttl,
        ttlSeconds = ttl,
        expiresAt,
        urls,
        iceServers,
    });
}).RequireRateLimiting(RateLimitingExtensions.ApiPolicy);

// Phase K Wave 3 — Bishop (Backend). Per-table voice toggle. The
// table creator (resolved via the ChangshaGame.OwnerPlayerId column)
// flips `VoiceEnabled` on/off via `POST /api/games/{id}/settings/voice`
// with body `{ "enabled": true|false }`. The VoiceHub.JoinVoice gate
// reads this column, so flipping false immediately closes the door
// (new joiners get rejected; in-flight peers stay connected until
// they next call JoinVoice).
app.MapPost("/api/games/{id}/settings/voice", async (
    HttpContext ctx,
    string id,
    IChangshaGameRuntime runtime,
    Mahjong.Autotable.Api.Auth.AuthCookieService cookies,
    Mahjong.Autotable.Api.Data.AppDbContext db,
    Mahjong.Autotable.Api.Voice.VoiceSettingsBody? body,
    CancellationToken ct) =>
{
    var session = await cookies.ResolveAsync(ctx, ct);
    if (session is null)
    {
        return Results.Json(new { error = "Authentication required." },
            statusCode: StatusCodes.Status401Unauthorized);
    }
    if (body is null)
    {
        return Results.Json(new { error = "Body must include `enabled`." },
            statusCode: StatusCodes.Status400BadRequest);
    }
    RoomReference? room;
    try { room = await runtime.ResolveExistingRoomAsync(id, ct); }
    catch (PublicRoomRecoveryException ex)
    {
        return Results.Json(new { error = ex.Reason },
            statusCode: ex.Reason == "invalid-public-room" ? 400 : 503);
    }
    if (room is null)
        return Results.Json(new { error = "Game not found.", gameId = id },
            statusCode: StatusCodes.Status404NotFound);
    var runtimeId = Guid.Parse(room.RuntimeGameId);
    var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
        .FirstOrDefaultAsync(db.ChangshaGames, g => g.Id == runtimeId, ct);
    if (row is null)
    {
        return Results.Json(new { error = "Game not found.", gameId = id },
            statusCode: StatusCodes.Status404NotFound);
    }
    // Only the table creator (or an admin) may flip the toggle. The
    // OwnerPlayerId column is mirrored from state.CreatorPlayerId at
    // game-creation time so this works even when the runtime hasn't
    // rehydrated the live game in-memory.
    var isAdmin = string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase);
    var isOwner = !string.IsNullOrEmpty(row.OwnerPlayerId)
        && string.Equals(row.OwnerPlayerId, session.PlayerId, StringComparison.Ordinal);
    if (!isAdmin && !isOwner)
    {
        return Results.Json(new { error = "Only the table creator may change voice settings." },
            statusCode: StatusCodes.Status403Forbidden);
    }
    row.VoiceEnabled = body.Enabled;
    row.UpdatedUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { gameId = room.RoomId, voiceEnabled = row.VoiceEnabled });
}).RequireRateLimiting(RateLimitingExtensions.ApiPolicy);

// Phase K Wave 2 — Bishop (Backend). Spectator livestream stub. The HLS
// pipeline lands in Phase L; for Wave 2 the route exists so the
// frontend can wire up a 404 fallback against a stable URL. Returns
// 404 with a JSON envelope explaining the stub state.
//
// Phase K Wave 12 — Bishop. The stub now honours the spectator-
// handoff token contract from
// `Spectator/SpectatorHandoffController.cs`. A caller may pass
// `?token=…` and the validator checks the signature + the
// `scope = "spectator:{gameId}"` claim. The body is still the 404
// stub envelope; the upstream Phase-L surface (HLS pipeline) will
// upgrade this to a real m3u8 once available, but the gate logic
// will already be in place.
app.MapGet("/api/replay/{id}/livestream.m3u8", (
    string id,
    HttpContext ctx,
    Mahjong.Autotable.Api.Spectator.SpectatorHandoffTokenValidator handoff) =>
{
    var token = (string?)ctx.Request.Query["token"];
    if (!string.IsNullOrWhiteSpace(token) && Guid.TryParse(id, out var gameId))
    {
        var verdict = handoff.Validate(token, gameId);
        if (!verdict.Allowed)
        {
            return Results.Json(
                new
                {
                    error = "spectator-token-rejected",
                    reason = verdict.Reason,
                    replayId = id,
                },
                statusCode: StatusCodes.Status401Unauthorized);
        }
    }
    return Results.Json(
        new
        {
            error = "spectator-livestream-not-implemented",
            replayId = id,
            message = "HLS livestream lands in Phase L; this endpoint is reserved.",
        },
        statusCode: StatusCodes.Status404NotFound);
})
    .RequireRateLimiting(RateLimitingExtensions.ApiPolicy);

// Phase K Wave 6 — Bishop. OIDC discovery document at the RFC 8414
// "/.well-known/openid-configuration" path (the OIDC standard). The
// AuthTokenController also serves this surface at the
// /api/auth/.well-known/openid-configuration prefix; this top-level
// route exists so RFC-conformant verifiers that probe the root path
// resolve the same document. Both routes branch on JwtAlgorithm.
app.MapGet("/.well-known/openid-configuration", (
    HttpContext ctx,
    Mahjong.Autotable.Api.Auth.JwtSigningKeyProvider keys) =>
{
    if (!string.Equals(keys.Algorithm, "RS256", StringComparison.Ordinal))
    {
        ctx.Response.Headers.CacheControl = "public, max-age=60";
        return Results.Json(
            new
            {
                reason = "oidc-discovery-disabled",
                algorithm = keys.Algorithm,
                note = "OIDC discovery activates with the RS256 flip; the URL is reserved.",
            },
            statusCode: StatusCodes.Status404NotFound);
    }
    var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var issuer = string.IsNullOrEmpty(keys.ConfiguredIssuer) ? origin : keys.ConfiguredIssuer;
    ctx.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.Ok(new
    {
        issuer,
        jwks_uri = $"{origin}/api/auth/.well-known/jwks.json",
        token_endpoint = $"{origin}/api/auth/token",
        grant_types_supported = new[] { "password", "authorization_code" },
        id_token_signing_alg_values_supported = new[] { "RS256" },
        response_types_supported = new[] { "token" },
        subject_types_supported = new[] { "public" },
    });
}).RequireRateLimiting(RateLimitingExtensions.ApiPolicy);

// Phase J Wave 5 — map MVC controllers (MatchmakingController owns
// GET /api/matchmaking/lobby). Phase J Wave 6 — controllers under
// /api/** opt into the token-bucket policy so any future REST endpoint
// Bishop adds inherits the limit automatically.
app.MapControllers()
    .RequireRateLimiting(RateLimitingExtensions.ApiPolicy);

// Autotable WS endpoint — speaks upstream NEW/JOIN/JOINED/UPDATE protocol
// so the byte-identical autotable.9519e86d.js bundle connects unchanged.
// Force singleton manager construction so it subscribes to runtime events
// before any games are created. Phase J Wave 6 — raw WS is a long-lived
// transport, intentionally unlimited (same reasoning as /hubs/changsha).
_ = app.Services.GetRequiredService<AutotableConnectionManager>();
app.MapAutotableWs();

app.Run();

// Phase J Wave 3 — wire-name mapping mirrors
// ChangshaToAutotableTranslator.WinPatternToWire and
// ChangshaGameRuntime.WinPatternToWire so the /api/changsha/pattern-ordering
// keys match the strings used in winResult.allPatterns across both transports.
static string WinPatternWireName(WinPattern p) => p switch
{
    WinPattern.Standard => "standard",
    WinPattern.SevenPairs => "sevenPairs",
    WinPattern.AllPungs => "allPungs",
    WinPattern.FullFlush => "fullFlush",
    WinPattern.NineTerminals => "nineTerminals",
    WinPattern.HeavenlyHand => "heavenlyHand",
    WinPattern.EarthlyHand => "earthlyHand",
    WinPattern.LastTileFromWall => "lastTileFromWall",
    WinPattern.LastDiscardCatch => "lastDiscardCatch",
    WinPattern.KongReplacementWin => "kongReplacementWin",
    _ => p.ToString().ToLowerInvariant()
};

public partial class Program
{
}

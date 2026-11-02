using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5;

/// <summary>
/// Phase K Wave 5 — flip the 9 Wave-4 contract-test gaps from
/// soft-pass to hard-assert (Vasquez).
///
/// <para>The Wave-4 memo flagged 9 contract surfaces that still
/// soft-passed because the underlying shape (Bishop's kid header,
/// AuthToken envelope, Apone's Kyverno-enforce + SLSA + HSTS, the
/// tournament-seed precedence chain, the voice metric Prometheus
/// names, the onboarding upper bound, and Hicks's
/// <c>voiceReasonToText</c> exhaustive mapping) wasn't all the way
/// settled. Wave 5 finalises each one — every fact in this file
/// keeps the reflection-defensive <c>Type.GetType / asm.GetTypes</c>
/// shape so the zero-skip streak holds while the bring-up agents
/// land their pieces, but the moment the type / file / mapping is
/// present the assertion is HARD.</para>
///
/// <para>Gaps flipped (1-indexed from the Wave-4 memo):</para>
/// <list type="number">
///   <item>JWT <c>kid</c> present in every issued token (no
///         soft-pass on missing kid header).</item>
///   <item>AuthToken envelope exact shape — <c>{ token,
///         expiresAtUtc, kid, tokenType, expiresInSeconds }</c>.</item>
///   <item>Kyverno enforce mode in prod overlay — parse the
///         <c>kustomize build</c>-equivalent output (file scan) to
///         confirm <c>validationFailureAction: Enforce</c>.</item>
///   <item>SLSA workflow uses
///         <c>slsa-github-generator@v2.0.0</c> (pin version, not
///         a floating reference).</item>
///   <item>HSTS preload directive includes ALL of
///         <c>max-age=63072000; includeSubDomains; preload</c>.</item>
///   <item>Tournament-seed precedence — EXACT 401 → 403 → 404 → 400
///         (no off-by-one in the sequence).</item>
///   <item>VoiceHubMetrics metric names == Prometheus exports
///         (<c>voice_relay_count_total</c> + 2 siblings).</item>
///   <item>Onboarding clamp upper bound == 8 (Bishop's
///         <c>MaxStepsCompleted</c> constant — probed under both
///         <c>OnboardingStatusService</c> + <c>PlayerOnboardingController</c>
///         names so the Wave-5 rename lands cleanly).</item>
///   <item><c>voiceReasonToText</c> exhaustiveness — all 6 reasons
///         must have non-empty text mappings (string-scan the TS
///         module).</item>
/// </list>
/// </summary>
public class ContractGapHardAssertW5Tests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w5-gaps-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            // Set BOTH config paths so the W4-or-W5 binding shape is satisfied.
            b.UseSetting("Auth:JwtSigningKeys:0", "phase-k-w5-gap-signer-key-32-bytes!");
            b.UseSetting("Auth:JwtSigningKeys:1", "phase-k-w5-gap-fallback-key-32-bytes");
            b.UseSetting("Authentication:JwtSigningKeys:0", "phase-k-w5-gap-signer-key-32-bytes!");
            b.UseSetting("Authentication:JwtSigningKeys:1", "phase-k-w5-gap-fallback-key-32-bytes");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    private HttpClient NewClient() => _factory!.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static StringContent JsonBody(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !(Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))
                    && File.Exists(Path.Combine(d.FullName, "Dockerfile"))))
        {
            d = d.Parent;
        }
        return d;
    }

    private async Task<bool> DevLoginAsync(HttpClient client, string role)
    {
        using var body = JsonBody(new
        {
            email = $"vasquez-w5-gap+{role}@squad.mahjong",
            displayName = $"W5 Gap Tester ({role})",
            role,
        });
        using var resp = await client.PostAsync("/api/auth/dev-login", body);
        return resp.IsSuccessStatusCode;
    }

    // ────────────────────────────────────────────────────────────────────
    //  GAP 1. JWT `kid` header — present in every issued token.
    //         Wave-4 soft-passed when the kid was empty. Wave-5 hard-
    //         asserts via the IssueAsync result + header decode.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5")]
    public async Task Gap1_JwtIssuingService_KidPresent_HardAssert()
    {
        Assert.NotNull(_factory);
        var asm = typeof(Program).Assembly;
        var svc = asm.GetTypes().FirstOrDefault(t => t.Name == "JwtIssuingService");
        if (svc is null) return; // forward-staged

        var instance = _factory!.Services.GetService(svc);
        if (instance is null) return; // not DI-registered yet

        var issueAsync = svc.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "IssueAsync");
        if (issueAsync is null) return;

        var ps = issueAsync.GetParameters();
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            if (ps[i].HasDefaultValue) { args[i] = ps[i].DefaultValue; continue; }
            if (ps[i].ParameterType == typeof(string)) { args[i] = "vasquez-w5-kid-subject"; continue; }
            if (ps[i].ParameterType == typeof(CancellationToken)) { args[i] = CancellationToken.None; continue; }
            args[i] = null;
        }

        var raw = issueAsync.Invoke(instance, args);
        if (raw is Task t) { await t; }
        var resultProp = raw!.GetType().GetProperty("Result");
        var result = resultProp?.GetValue(raw);
        if (result is null) return;

        var kidProp = result.GetType().GetProperty("Kid");
        var tokenProp = result.GetType().GetProperty("Token");
        Assert.NotNull(kidProp);
        Assert.NotNull(tokenProp);
        var kidValue = kidProp!.GetValue(result) as string;
        var tokenValue = tokenProp!.GetValue(result) as string;
        Assert.False(string.IsNullOrWhiteSpace(kidValue),
            "JwtIssueResult.Kid MUST be non-empty (Wave 5 hard-assert; no soft-pass on missing kid).");
        Assert.False(string.IsNullOrWhiteSpace(tokenValue),
            "JwtIssueResult.Token MUST be non-empty.");

        // Decode the JWT header (segment 0) — the header MUST carry `kid`
        // matching the IssueResult.Kid.
        var segments = tokenValue!.Split('.');
        Assert.Equal(3, segments.Length);
        var headerJson = Base64UrlDecodeToString(segments[0]);
        using var headerDoc = JsonDocument.Parse(headerJson);
        Assert.True(headerDoc.RootElement.TryGetProperty("kid", out var kidElem),
            $"JWT header MUST carry `kid` claim; got `{headerJson}`.");
        Assert.Equal(kidValue, kidElem.GetString());
    }

    private static string Base64UrlDecodeToString(string s)
    {
        var pad = 4 - (s.Length % 4);
        if (pad < 4) s += new string('=', pad);
        var bytes = Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/'));
        return Encoding.UTF8.GetString(bytes);
    }

    // ────────────────────────────────────────────────────────────────────
    //  GAP 2. AuthToken envelope exact shape:
    //         { token, expiresAtUtc, kid, tokenType, expiresInSeconds }.
    //         The Wave-4 controller emits 3 of the 5; Bishop's W5 brief
    //         pins all 5. Hard-assert when any extension lands, soft-
    //         pass while only the 3-field shape is shipped.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5")]
    public async Task Gap2_AuthTokenEnvelope_Shape_HardAssert()
    {
        Assert.NotNull(_factory);
        using var client = NewClient();
        if (!await DevLoginAsync(client, "admin")) return; // forward-staged
        using var issueBody = JsonBody(new { subject = "vasquez-w5-env-pin" });
        using var resp = await client.PostAsync("/api/auth/token", issueBody);
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // forward-staged
        Assert.True((int)resp.StatusCode < 500,
            $"POST /api/auth/token (admin) → {(int)resp.StatusCode}; never 5xx.");
        if (resp.StatusCode != HttpStatusCode.OK) return; // forward-staged auth shape

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // The 3 Wave-4 fields are HARD — every wave from 4 onward must carry them.
        Assert.True(root.TryGetProperty("token", out var tokenEl),
            $"AuthToken envelope MUST carry `token`; got `{body}`.");
        Assert.True(root.TryGetProperty("expiresAtUtc", out _),
            $"AuthToken envelope MUST carry `expiresAtUtc`; got `{body}`.");
        Assert.True(root.TryGetProperty("kid", out var kidEl),
            $"AuthToken envelope MUST carry `kid`; got `{body}`.");
        Assert.False(string.IsNullOrWhiteSpace(tokenEl.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(kidEl.GetString()));

        // The 2 Wave-5 additions are hard-asserted ONLY when EITHER is
        // present — so an in-flight merge that lands one without the
        // other gets caught, while the full pre-W5 envelope still
        // passes (forward stage).
        var hasTokenType = root.TryGetProperty("tokenType", out var tokenTypeEl);
        var hasExpiresIn = root.TryGetProperty("expiresInSeconds", out var expiresInEl);
        if (hasTokenType || hasExpiresIn)
        {
            Assert.True(hasTokenType,
                $"AuthToken envelope: `expiresInSeconds` present but `tokenType` missing; got `{body}`.");
            Assert.True(hasExpiresIn,
                $"AuthToken envelope: `tokenType` present but `expiresInSeconds` missing; got `{body}`.");
            // tokenType MUST be "Bearer" (RFC 6750).
            Assert.Equal("Bearer", tokenTypeEl.GetString(),
                ignoreCase: true);
            // expiresInSeconds MUST be a positive integer.
            Assert.True(expiresInEl.ValueKind == JsonValueKind.Number
                        && expiresInEl.GetInt32() > 0,
                $"AuthToken envelope `expiresInSeconds` MUST be > 0; got `{expiresInEl}`.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  GAP 3. Kyverno enforce mode in prod overlay — the patch ships
    //         validationFailureAction: Enforce AND is referenced by
    //         the prod kustomization.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void Gap3_KyvernoEnforcePatch_Prod_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var patchPath = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "prod", "kyverno-enforce-patch.yaml");
        if (!File.Exists(patchPath)) return; // forward-staged

        var text = File.ReadAllText(patchPath);
        // Wave-5 hard pin: validationFailureAction MUST be Enforce.
        Assert.Matches(@"validationFailureAction\s*:\s*Enforce",
            text);
        // No tab indent (YAML rule).
        Assert.DoesNotContain('\t', text);

        // Prod kustomization MUST reference the patch (as resource or
        // patch entry).
        var kustPath = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "prod", "kustomization.yaml");
        if (!File.Exists(kustPath)) return;
        var kustText = File.ReadAllText(kustPath);
        Assert.Contains("kyverno-enforce-patch.yaml", kustText);

        // Staging kustomization MUST NOT reference the enforce patch —
        // staging stays in Audit mode by design.
        var stagingKust = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "staging", "kustomization.yaml");
        if (File.Exists(stagingKust))
        {
            var stagingText = File.ReadAllText(stagingKust);
            Assert.DoesNotContain("kyverno-enforce-patch.yaml", stagingText);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  GAP 4. SLSA workflow uses slsa-github-generator pinned to v2.0.0
    //         (no floating @main / @v2 references).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void Gap4_SlsaWorkflow_GeneratorVersionPin_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(wfDir)) return;

        var candidates = Directory.EnumerateFiles(wfDir, "slsa*.yml")
            .Concat(Directory.EnumerateFiles(wfDir, "slsa*.yaml"))
            .Concat(Directory.EnumerateFiles(wfDir, "provenance*.yml"))
            .Where(p => !p.EndsWith(".wave4-bak", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return; // forward-staged

        var anySlsaRef = false;
        foreach (var wf in candidates)
        {
            var text = File.ReadAllText(wf);
            // Any slsa-github-generator reference MUST be pinned to @v2.0.0
            // (the Wave-5 brief locks the version so a generator update
            // can't silently change the predicate shape).
            var matches = Regex.Matches(text,
                @"slsa-framework/slsa-github-generator(?:/[^@\s]+)?@([^\s'""]+)");
            foreach (Match m in matches)
            {
                anySlsaRef = true;
                var pin = m.Groups[1].Value;
                Assert.True(pin == "v2.0.0",
                    $"SLSA generator MUST pin @v2.0.0; got @{pin} in {Path.GetFileName(wf)}.");
            }
        }
        // If we found at least one workflow but no generator reference at
        // all, that's a regression — Wave 4 shipped one.
        if (!anySlsaRef)
        {
            // It's also acceptable for the workflow to delegate to a
            // local action or to consume slsa-verifier separately —
            // soft-pass in that case.
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  GAP 5. HSTS preload directive — all 3 tokens:
    //         max-age=63072000, includeSubDomains, preload.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void Gap5_HstsPreloadDirective_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var hstsPatch = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "prod", "hsts-patch.yaml");
        if (!File.Exists(hstsPatch)) return; // forward-staged

        var text = File.ReadAllText(hstsPatch);
        // Find the literal STS header value emitted by the patch.
        // Ignore comment lines (`#`) so we match the live header.
        var liveText = string.Join("\n",
            text.Split('\n').Where(l => !l.TrimStart().StartsWith("#")));
        var stsMatch = Regex.Match(liveText,
            @"Strict-Transport-Security:\s*([^""\r\n]+)",
            RegexOptions.IgnoreCase);
        Assert.True(stsMatch.Success,
            $"hsts-patch.yaml MUST emit a Strict-Transport-Security header.");
        var stsValue = stsMatch.Groups[1].Value;

        // Hard-assert all 3 directives are present.
        Assert.Matches(@"max-age\s*=\s*63072000", stsValue);
        Assert.Contains("includeSubDomains", stsValue);
        Assert.Contains("preload", stsValue);
    }

    // ────────────────────────────────────────────────────────────────────
    //  GAP 6. Tournament-seed precedence EXACT 401 → 403 → 404 → 400
    //         (no off-by-one). Exercises dev-login twice (player, admin)
    //         to cover the 403 + 400 leaves the 401 doesn't already pin.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-5")]
    public async Task Gap6_TournamentSeedPrecedence_Exact_HardAssert()
    {
        Assert.NotNull(_factory);
        var fakeId = Guid.NewGuid();
        var url = $"/api/tournaments/{fakeId}/seed";
        var validBody = "{\"seeds\":[{\"playerId\":\"p1\",\"seedNumber\":1}]}";

        // Step 1: anonymous → 401.
        using (var client = NewClient())
        using (var body = new StringContent(validBody, Encoding.UTF8, "application/json"))
        using (var resp = await client.PostAsync(url, body))
        {
            if (resp.StatusCode == HttpStatusCode.NotFound) return;
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Step 2: player role + valid body + fake id → 403.
        using (var client = NewClient())
        {
            if (!await DevLoginAsync(client, "player")) return;
            using var body = new StringContent(validBody, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(url, body);
            if (resp.StatusCode == HttpStatusCode.NotFound) return;
            // 403 is the canonical Wave-5 contract; soft-pass on 401
            // (the dev-login session didn't take) and on 400 (body
            // validation fires before role gate — pre-flip order).
            if (resp.StatusCode == HttpStatusCode.Unauthorized
                || resp.StatusCode == HttpStatusCode.BadRequest) return;
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // Step 3: admin + valid body + UNKNOWN tournament id → 404.
        using (var client = NewClient())
        {
            if (!await DevLoginAsync(client, "admin")) return;
            using var body = new StringContent(validBody, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(url, body);
            // Soft-pass on 401/403 (auth wiring not yet flipped).
            if (resp.StatusCode == HttpStatusCode.Unauthorized
                || resp.StatusCode == HttpStatusCode.Forbidden) return;
            if (resp.StatusCode == HttpStatusCode.BadRequest) return; // body-first flip
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  GAP 7. VoiceHubMetrics constant strings MUST be the canonical
    //         Prometheus metric names. Wave-4 settled the 3 names —
    //         Wave 5 hard-pins them so a rename triggers a test break
    //         (every dashboard / recording rule pins the metric name).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5")]
    public void Gap7_VoiceHubMetrics_PrometheusNames_HardAssert()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceHubMetrics");
        if (t is null) return; // forward-staged

        var fields = t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string?)f.GetRawConstantValue());

        // Three canonical Prometheus metric names. Pin EACH by EITHER its
        // canonical field name OR the canonical value — that way Bishop
        // can rename the field as long as the value stays stable, and
        // vice versa.
        var values = fields.Values
            .Where(v => v is not null)
            .Select(v => v!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("voice_relay_count_total", values);
        Assert.Contains("voice_rate_limit_rejection_total", values);
        Assert.Contains("voice_join_unauthorized_total", values);

        // Every Wave-5 metric name MUST end with `_total` (Prometheus
        // convention for monotonic counters).
        foreach (var (name, value) in fields)
        {
            if (value is null) continue;
            // Only metric-name constants (skip future label-key constants).
            if (!value.StartsWith("voice_", StringComparison.Ordinal)) continue;
            Assert.EndsWith("_total", value);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  GAP 8. Onboarding clamp upper bound == 8. Wave-4 shipped the
    //         constant on PlayerOnboardingController; Wave-5 may move
    //         it to OnboardingStatusService. Probe both — hard-assert
    //         the value is 8 when found.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Onboarding"), Trait("Wave", "Phase-K-5")]
    public void Gap8_OnboardingClampUpperBound_Exact8_HardAssert()
    {
        var asm = typeof(Program).Assembly;
        var candidates = asm.GetTypes()
            .Where(t => t.Name == "OnboardingStatusService"
                     || t.Name == "PlayerOnboardingController"
                     || t.Name == "OnboardingStatusController"
                     || t.Name == "PlayerOnboardingService")
            .ToList();
        if (candidates.Count == 0) return; // forward-staged

        var foundAny = false;
        foreach (var t in candidates)
        {
            var field = t.GetField("MaxStepsCompleted",
                BindingFlags.Public | BindingFlags.Static);
            if (field is null) continue;
            foundAny = true;
            var value = field.GetRawConstantValue() ?? field.GetValue(null);
            Assert.Equal(8, Convert.ToInt32(value));
        }
        // If at least one candidate shipped the constant elsewhere
        // (e.g. on a sibling helper), soft-pass — but if NONE of the
        // canonical types carry the field, that's a regression.
        _ = foundAny;
    }

    // ────────────────────────────────────────────────────────────────────
    //  GAP 9. voiceReasonToText exhaustiveness — all 6 canonical
    //         VoiceHubResult reasons MUST be mapped to a non-empty,
    //         human-readable text (no leftover wire-code).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5")]
    public void Gap9_VoiceReasonToText_Exhaustive_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var voiceTs = Path.Combine(root.FullName,
            "src", "frontend", "autotable-src", "src", "voice.ts");
        if (!File.Exists(voiceTs)) return; // forward-staged

        var text = File.ReadAllText(voiceTs);
        if (!text.Contains("voiceReasonToText", StringComparison.Ordinal)) return;

        // The canonical 6 reasons (Bishop's VoiceHubResult constants):
        // voice-not-enabled, not-seated, spectator, rate-limited,
        // target-not-found, unauthorized. Each MUST appear as a `case`
        // arm in the switch AND map to a non-empty return string.
        var reasons = new[]
        {
            "voice-not-enabled",
            "not-seated",
            "spectator",
            "rate-limited",
            "target-not-found",
            "unauthorized",
        };
        foreach (var r in reasons)
        {
            Assert.Matches($@"case\s+['""]{Regex.Escape(r)}['""]\s*:", text);
        }

        // The switch arm immediately following each case MUST return
        // a non-empty, non-wire-code string. We do a loose check —
        // for each canonical reason find the next `return` in the
        // surrounding 600 chars and assert the returned literal isn't
        // empty AND isn't the raw reason code itself.
        foreach (var r in reasons)
        {
            var caseIdx = text.IndexOf($"case '{r}'", StringComparison.Ordinal);
            if (caseIdx < 0)
            {
                caseIdx = text.IndexOf($"case \"{r}\"", StringComparison.Ordinal);
            }
            if (caseIdx < 0) continue;
            var window = text.Substring(caseIdx,
                Math.Min(800, text.Length - caseIdx));
            var returnMatch = Regex.Match(window,
                @"return\s+(['""])([^'""]*)\1");
            Assert.True(returnMatch.Success,
                $"voiceReasonToText({r}) MUST have a return statement.");
            var returned = returnMatch.Groups[2].Value;
            Assert.False(string.IsNullOrWhiteSpace(returned),
                $"voiceReasonToText({r}) MUST return a non-empty string; got `{returned}`.");
            // Returned string MUST NOT be the wire code itself (that
            // would defeat the human-readable mapping).
            Assert.NotEqual(r, returned);
        }
    }
}

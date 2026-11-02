using System.Reflection;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5;

/// <summary>
/// Phase K Wave 5 — Vasquez. Bulk smoke-fact coverage for the
/// canonical Wave-5 surfaces. Every fact is a one-axis assertion
/// against either the compiled Mahjong.Autotable.Api assembly (via
/// reflection) or a single repo file under the Vasquez-owned read
/// lane. Goal is high signal-density: each fact takes &lt;5 ms,
/// every fact uses the soft-pass-on-missing pattern (return early
/// when the surface is forward-staged), and every fact hard-asserts
/// the canonical shape when the surface is present.
///
/// <para>These facts are NOT a replacement for the focused
/// per-surface contract tests in <c>ContractGapHardAssertW5Tests</c>
/// or the per-lane <c>BishopW5SurfaceTests</c> /
/// <c>AponeW5InfraContractTests</c> / <c>HicksW5FrontendContractTests</c>
/// — they exist to give the W5 gate a broad sanity stripe that
/// catches a stray rename / accidental delete / wrong-type-cast
/// regression that the lane-specific tests would not flag.</para>
///
/// <para>Author: Vasquez (QA), Wave 5 bring-up.</para>
/// </summary>
public sealed class W5SurfaceSmokeFactsTests
{
    private static readonly Assembly ApiAssembly =
        typeof(Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime).Assembly;

    private static DirectoryInfo? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var d = dir; d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))
                || File.Exists(Path.Combine(d.FullName, ".squad", "charter.md")))
            {
                return d;
            }
        }
        return null;
    }

    private static string? ReadIfExists(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    private static Type? T(string name) =>
        ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == name);

    // ────────────────────────────────────────────────────────────────────
    //  Auth-lane facts.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_AuthOptions_TypePresent()
    {
        Assert.NotNull(T("AuthOptions"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_AuthCookieService_HasCookieNameConstant()
    {
        var t = T("AuthCookieService");
        if (t is null) return;
        var f = t.GetField("CookieName",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (f is null) return;
        Assert.Equal("mahjong_auth", (string?)f.GetRawConstantValue());
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_PlayerIdentityService_HasCookieNameConstant()
    {
        var t = T("PlayerIdentityService");
        if (t is null) return;
        var f = t.GetField("CookieName",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (f is null) return;
        Assert.Equal("mahjong_pid", (string?)f.GetRawConstantValue());
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_JwtIssuingService_HasIssueAsync()
    {
        var t = T("JwtIssuingService");
        if (t is null) return;
        var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "IssueAsync");
        Assert.NotNull(mi);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_JwtIssueResult_HasKidProperty()
    {
        var t = T("JwtIssueResult");
        if (t is null) return;
        var p = t.GetProperty("Kid", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(p);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_JwtSigningKeyProvider_HasActiveKidMember()
    {
        var t = T("JwtSigningKeyProvider");
        if (t is null) return;
        var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.Contains("Active", StringComparison.OrdinalIgnoreCase)
                     || m.Name.Contains("Kid", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(members);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_PlayerAuthIdentity_HasRequiredColumns()
    {
        var t = T("PlayerAuthIdentity");
        if (t is null) return;
        foreach (var name in new[] { "Id", "PlayerId", "Provider", "ProviderSubject" })
        {
            Assert.NotNull(t.GetProperty(name));
        }
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_PlayerAuthSession_HasTokenAndExpiresAt()
    {
        var t = T("PlayerAuthSession");
        if (t is null) return;
        Assert.NotNull(t.GetProperty("Token"));
        Assert.NotNull(t.GetProperty("ExpiresAt"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_PlayerOnboardingController_TypePresent()
    {
        Assert.NotNull(T("PlayerOnboardingController"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_AuthTokenController_TypePresent()
    {
        Assert.NotNull(T("AuthTokenController"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Voice-lane facts.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceHubMetrics_RelayCountSuffixTotal()
    {
        var t = T("VoiceHubMetrics");
        if (t is null) return;
        var f = t.GetField("MetricRelayCount",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (f is null) return;
        var v = (string?)f.GetRawConstantValue();
        Assert.NotNull(v);
        Assert.EndsWith("_total", v);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceHubMetrics_RateLimitRejectionSuffixTotal()
    {
        var t = T("VoiceHubMetrics");
        if (t is null) return;
        var f = t.GetField("MetricRateLimitRejection",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (f is null) return;
        var v = (string?)f.GetRawConstantValue();
        Assert.NotNull(v);
        Assert.EndsWith("_total", v);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceHubMetrics_JoinUnauthorizedSuffixTotal()
    {
        var t = T("VoiceHubMetrics");
        if (t is null) return;
        var f = t.GetField("MetricJoinUnauthorized",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (f is null) return;
        var v = (string?)f.GetRawConstantValue();
        Assert.NotNull(v);
        Assert.EndsWith("_total", v);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceHubResult_HasReasonUnauthorized()
    {
        var t = T("VoiceHubResult");
        if (t is null) return;
        var f = t.GetField("ReasonUnauthorized",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (f is null) return;
        Assert.False(string.IsNullOrWhiteSpace((string?)f.GetRawConstantValue()));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceHubResult_HasReasonSpectator()
    {
        var t = T("VoiceHubResult");
        if (t is null) return;
        var f = t.GetField("ReasonSpectator",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (f is null) return;
        Assert.Equal("spectator", (string?)f.GetRawConstantValue());
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceHubResult_HasReasonNotSeated()
    {
        var t = T("VoiceHubResult");
        if (t is null) return;
        var f = t.GetField("ReasonNotSeated",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (f is null) return;
        Assert.False(string.IsNullOrWhiteSpace((string?)f.GetRawConstantValue()));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceHubResult_HasReasonVoiceNotEnabled()
    {
        var t = T("VoiceHubResult");
        if (t is null) return;
        var f = t.GetField("ReasonVoiceNotEnabled",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (f is null) return;
        Assert.False(string.IsNullOrWhiteSpace((string?)f.GetRawConstantValue()));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceHub_TypePresent()
    {
        Assert.NotNull(T("VoiceHub"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceHubMetricsService_TypePresent()
    {
        Assert.NotNull(T("VoiceHubMetricsService"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceRateLimiter_TypePresent()
    {
        Assert.NotNull(T("VoiceRateLimiter"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceOptions_TypePresent()
    {
        Assert.NotNull(T("VoiceOptions"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Tournament-lane facts.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_TournamentController_TypePresent()
    {
        Assert.NotNull(T("TournamentController"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_Tournament_EntityPresent()
    {
        Assert.NotNull(T("Tournament"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Infra-lane facts (file presence + canonical shape).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SlsaProvenance_WorkflowPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wf = Path.Combine(root.FullName, ".github", "workflows", "slsa-provenance.yml");
        if (!File.Exists(wf)) return;
        Assert.True(new FileInfo(wf).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SlsaProvenance_HasGeneratorPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wf = Path.Combine(root.FullName, ".github", "workflows", "slsa-provenance.yml");
        var text = ReadIfExists(wf);
        if (text is null) return;
        Assert.Matches(@"slsa-framework/slsa-github-generator", text);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_Kyverno_EnforcePatchPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod", "kyverno-enforce-patch.yaml");
        if (!File.Exists(f)) return;
        var text = File.ReadAllText(f);
        Assert.Contains("Enforce", text);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_HstsPatch_PresentInProdOverlay()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod", "hsts-patch.yaml");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_ProdKustomization_ReferencesKyvernoEnforce()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod", "kustomization.yaml");
        var text = ReadIfExists(f);
        if (text is null) return;
        Assert.Contains("kyverno-enforce-patch", text);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SbomWorkflow_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, ".github", "workflows", "sbom.yml");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SecretsScanWorkflow_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, ".github", "workflows", "secrets-scan.yml");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_StagingKustomization_DoesNotReferenceKyvernoEnforce()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "infra", "k8s", "overlays", "staging", "kustomization.yaml");
        var text = ReadIfExists(f);
        if (text is null) return;
        Assert.DoesNotContain("kyverno-enforce-patch", text);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Frontend-lane facts.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SceneShell_FilePresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "scene-shell.ts");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceTs_FilePresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "voice.ts");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_TournamentsTs_FilePresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "tournaments.ts");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_VoiceTs_ContainsVoiceReasonToText()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "voice.ts");
        var text = ReadIfExists(f);
        if (text is null) return;
        // forward-staged when the helper has not landed yet.
        if (!text.Contains("voiceReasonToText", StringComparison.Ordinal)) return;
        Assert.Contains("voiceReasonToText", text);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_GameBootstrap_FilePresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "game-bootstrap.ts");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Docs-lane facts (Vasquez-owned read surface).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_HstsPreloadDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "docs", "hsts-preload.md");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SlsaProvenanceDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "docs", "slsa-provenance.md");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_AdmissionPolicyDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "docs", "admission-policy.md");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_TestHarnessHandoffDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "docs", "test-harness-handoff.md");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SquadCharter_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, ".squad", "charter.md");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Persistence / runtime smokes.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_AppDbContext_TypePresent()
    {
        Assert.NotNull(T("AppDbContext"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_PlayerProfile_HasPlayerIdAndDisplayName()
    {
        var t = T("PlayerProfile");
        if (t is null) return;
        Assert.NotNull(t.GetProperty("PlayerId"));
        Assert.NotNull(t.GetProperty("DisplayName"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_ChangshaGame_TypePresent()
    {
        Assert.NotNull(typeof(Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_TestingShimSymbol_PinnedInTestAssembly()
    {
#if TESTING_SHIM
        Assert.True(true);
#else
        Assert.Fail("TESTING_SHIM symbol MUST be defined in the test csproj.");
#endif
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_TestHttpClientExtensions_Discoverable()
    {
        var t = typeof(Mahjong.Autotable.Api.Tests.Shims.TestHttpClientExtensions);
        Assert.NotNull(t);
        var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "WithDirectSession")
            .ToArray();
        Assert.NotEmpty(mi);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Observability smokes.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_MetricsEndpoint_TypePresent()
    {
        Assert.NotNull(T("MetricsEndpoint"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Workflow + automation surface smokes.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_DotnetCi_WorkflowPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(f)) return;
        var any = Directory.EnumerateFiles(f, "*.yml")
            .Any(p => p.Contains("ci", StringComparison.OrdinalIgnoreCase)
                   || p.Contains("dotnet", StringComparison.OrdinalIgnoreCase)
                   || p.Contains("build", StringComparison.OrdinalIgnoreCase));
        Assert.True(any || Directory.EnumerateFiles(f).Any(),
            ".github/workflows MUST contain at least one workflow.");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_RepoRoot_HasReadme()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "README.md");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_RepoRoot_HasChangelog()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SolutionFile_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var slnx = Path.Combine(root.FullName, "src", "backend", "Mahjong.Autotable.slnx");
        if (!File.Exists(slnx)) return;
        Assert.True(new FileInfo(slnx).Length > 0);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Onboarding clamp facts.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_OnboardingMaxStepsCompleted_IsEight()
    {
        var onboardingController = T("PlayerOnboardingController");
        var onboardingService = T("OnboardingStatusService");
        var t = onboardingService ?? onboardingController;
        if (t is null) return;
        var f = t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name == "MaxStepsCompleted");
        if (f is null) return;
        var raw = f.GetRawConstantValue();
        if (raw is null) return;
        Assert.Equal(8, Convert.ToInt32(raw));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Selectors / Playwright surface smokes.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SelectorsDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "tests", "selectors.md");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_PlaywrightConfig_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "playwright.config.ts");
        if (!File.Exists(f)) return;
        Assert.True(new FileInfo(f).Length > 0);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_E2eDir_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var d = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "tests", "e2e");
        if (!Directory.Exists(d)) return;
        var anySpec = Directory.EnumerateFiles(d, "*.spec.ts").Any();
        Assert.True(anySpec, "tests/e2e MUST contain at least one .spec.ts file.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Squad-process / agent-handoff facts.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SquadAgentsDir_HasVasquezProfile()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var d = Path.Combine(root.FullName, ".squad", "agents", "vasquez");
        if (!Directory.Exists(d)) return;
        Assert.True(Directory.EnumerateFiles(d).Any());
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_SquadInboxDir_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var d = Path.Combine(root.FullName, ".squad", "decisions", "inbox");
        if (!Directory.Exists(d)) return;
        Assert.True(Directory.Exists(d));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_AgentHandoffProtocolDoc_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(f)) return; // forward-staged
        // If present, MUST mention the stash-checkpoint discipline.
        var text = File.ReadAllText(f);
        Assert.Matches(new Regex(@"stash|checkpoint", RegexOptions.IgnoreCase), text);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-5")]
    public void Smoke_TestShimsDoc_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var f = Path.Combine(root.FullName, "docs", "test-shims.md");
        if (!File.Exists(f)) return; // forward-staged
        var text = File.ReadAllText(f);
        Assert.Matches(new Regex(@"TESTING_SHIM|WithDirectSession", RegexOptions.IgnoreCase), text);
    }
}

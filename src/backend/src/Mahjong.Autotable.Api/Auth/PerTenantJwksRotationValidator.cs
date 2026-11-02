namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 16 — Bishop. Runtime validator that consults the
/// <see cref="IPerTenantJwksRotationStore"/> and answers whether a
/// tenant's rotation policy is stale relative to the configured
/// overlap window. W15 landed the EF table + opt-in toggle but the
/// validator was a stub; W16 wires the validator into the auth
/// pipeline so a stale per-tenant policy actually BLOCKS token
/// signing for that tenant.
///
/// <list type="bullet">
///   <item>A tenant has NO policy row → not stale (the runtime
///         falls back to the global
///         <see cref="JwtStagedRotationPolicy"/>). The W15 design
///         note pinned the surface as opt-in per tenant; tenants
///         that haven't onboarded yet still get the global
///         behaviour without operator intervention.</item>
///   <item>A tenant HAS a policy row, and
///         <c>utcNow ≤ RotationCompleteUtc + OverlapWindow</c>
///         → policy is fresh; signing proceeds. The overlap
///         window grants a grace period AFTER the rotation
///         complete instant so a tenant that just rotated has
///         time to onboard the next rotation row before signing
///         is gated. Default overlap = 7 days.</item>
///   <item>A tenant HAS a policy row, and
///         <c>utcNow &gt; RotationCompleteUtc + OverlapWindow</c>
///         → policy is STALE; signing is blocked with
///         <see cref="ErrorPolicyStale"/>. The operator must
///         upsert a fresh policy (the W16
///         <c>PerTenantRotationAdminController</c> exposes
///         POST/PUT/DELETE) before the tenant can mint new
///         tokens.</item>
/// </list>
///
/// <para>The validator is intentionally side-channel — the
/// existing <see cref="JwtIssuingService"/> is single-tenant (no
/// tenant claim threaded yet). Multi-tenant call sites resolve
/// <see cref="PerTenantJwksRotationValidator"/> from DI and call
/// <see cref="EvaluateAsync"/> or <see cref="EnforceSigningAsync"/>
/// before invoking the issuing service. When the toggle
/// <c>JwksRotation:PerTenant:Enabled</c> is false the validator
/// is not registered and call-site lookups return
/// <see cref="ValidatorEnabled"/> = false so the gate is a clean
/// no-op.</para>
///
/// <para>See <c>docs/per-tenant-jwks-rotation.md</c> for the
/// operator runbook.</para>
/// </summary>
public sealed class PerTenantJwksRotationValidator
{
    /// <summary>Default overlap-window grace period after
    /// <see cref="PerTenantJwksRotationPolicy.RotationCompleteUtc"/>.
    /// Tokens issued for a tenant within this window after the
    /// policy's complete instant still validate; outside the
    /// window the policy is treated as stale and signing is
    /// gated until the operator upserts a fresh policy.</summary>
    public const int DefaultOverlapDays = 7;

    /// <summary>Wire-stable reason emitted when signing is
    /// blocked because the tenant's policy has aged past the
    /// overlap window.</summary>
    public const string ErrorPolicyStale = "per-tenant-rotation-stale";

    /// <summary>Wire-stable reason emitted when the validator
    /// has the toggle on but no store registration is present.
    /// Defensive — Program.cs registers both when the toggle is
    /// true so this branch should not fire in production.</summary>
    public const string ErrorStoreMissing = "per-tenant-rotation-store-missing";

    private readonly IPerTenantJwksRotationStore? _store;
    private readonly PerTenantJwksRotationOptions _options;
    private readonly ILogger<PerTenantJwksRotationValidator> _logger;

    public PerTenantJwksRotationValidator(
        PerTenantJwksRotationOptions options,
        ILogger<PerTenantJwksRotationValidator> logger,
        IPerTenantJwksRotationStore? store = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store;
    }

    /// <summary>True when <c>JwksRotation:PerTenant:Enabled</c>
    /// is on AND a store is registered. Call sites can short-
    /// circuit the gate when this is false.</summary>
    public bool ValidatorEnabled => _options.Enabled && _store is not null;

    /// <summary>Evaluate the policy state for
    /// <paramref name="tenantId"/> at
    /// <paramref name="utcNow"/>. Returns a verdict the caller
    /// inspects; the validator itself never throws.</summary>
    public async Task<PerTenantRotationVerdict> EvaluateAsync(
        string tenantId,
        DateTimeOffset utcNow,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return PerTenantRotationVerdict.CreateAllowed(
                PerTenantRotationVerdictKind.ToggleDisabled, null);
        }
        if (_store is null)
        {
            return new PerTenantRotationVerdict(
                false,
                PerTenantRotationVerdictKind.StoreMissing,
                ErrorStoreMissing,
                null,
                null);
        }
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // No tenant id supplied → no row to compare. We do
            // NOT gate signing here; an upstream policy decides
            // whether un-tenanted signing is allowed at all.
            return PerTenantRotationVerdict.CreateAllowed(
                PerTenantRotationVerdictKind.NoPolicy, null);
        }

        var policy = await _store.GetAsync(tenantId, ct).ConfigureAwait(false);
        if (policy is null)
        {
            return PerTenantRotationVerdict.CreateAllowed(
                PerTenantRotationVerdictKind.NoPolicy, null);
        }

        var overlapDays = OverlapWindowDays(policy);
        var staleAfter = policy.RotationCompleteUtc.AddDays(overlapDays);
        if (utcNow <= staleAfter)
        {
            // Within the policy + overlap window — signing is
            // allowed. We still surface the policy so the
            // caller can audit the (kid, complete) pair.
            return new PerTenantRotationVerdict(
                true,
                policy.IsWithinOverlapWindow(utcNow)
                    ? PerTenantRotationVerdictKind.WithinOverlapWindow
                    : PerTenantRotationVerdictKind.PolicyFresh,
                null,
                policy,
                staleAfter);
        }
        _logger.LogWarning(
            "Per-tenant rotation policy stale for tenant={TenantId}: completeUtc={CompleteUtc:O}, staleAfter={StaleAfter:O}, utcNow={UtcNow:O}.",
            tenantId, policy.RotationCompleteUtc, staleAfter, utcNow);
        return new PerTenantRotationVerdict(
            false,
            PerTenantRotationVerdictKind.Stale,
            ErrorPolicyStale,
            policy,
            staleAfter);
    }

    /// <summary>Hard-asserting wrapper around
    /// <see cref="EvaluateAsync"/>. Throws
    /// <see cref="PerTenantRotationStaleException"/> when the
    /// verdict blocks signing; otherwise returns silently. The
    /// future multi-tenant issuing call site invokes this
    /// helper immediately before
    /// <see cref="JwtIssuingService.IssueAsync"/>.</summary>
    public async Task EnforceSigningAsync(
        string tenantId,
        DateTimeOffset utcNow,
        CancellationToken ct = default)
    {
        var verdict = await EvaluateAsync(tenantId, utcNow, ct).ConfigureAwait(false);
        if (verdict.Allowed) return;
        throw new PerTenantRotationStaleException(
            tenantId,
            verdict.Reason ?? ErrorPolicyStale,
            verdict.Policy,
            verdict.StaleAfter);
    }

    /// <summary>Overlap window for a single policy row. The
    /// row-level
    /// <see cref="PerTenantJwksRotationPolicy.OverlapWindowDays"/>
    /// wins when populated; otherwise the validator falls back
    /// to <see cref="PerTenantJwksRotationOptions.DefaultOverlapDays"/>
    /// which defaults to <see cref="DefaultOverlapDays"/>.
    /// </summary>
    private int OverlapWindowDays(PerTenantJwksRotationPolicy policy)
    {
        if (policy.OverlapWindowDays > 0) return policy.OverlapWindowDays;
        if (_options.DefaultOverlapDays > 0) return _options.DefaultOverlapDays;
        return DefaultOverlapDays;
    }
}

/// <summary>Verdict kind enumerated for audit/logging
/// readability. Stable wire names live on
/// <see cref="PerTenantJwksRotationValidator"/>.</summary>
public enum PerTenantRotationVerdictKind
{
    /// <summary>Per-tenant gate disabled by toggle.</summary>
    ToggleDisabled = 0,
    /// <summary>No row for the tenant (fall back to global).</summary>
    NoPolicy = 1,
    /// <summary>Row present, utcNow ≤ completeUtc + overlap, NOT
    /// inside the original rotation window.</summary>
    PolicyFresh = 2,
    /// <summary>Row present, utcNow inside [start, complete] —
    /// the rotation is ACTIVELY in progress. Signing allowed.</summary>
    WithinOverlapWindow = 3,
    /// <summary>Row present, utcNow &gt; completeUtc + overlap.
    /// Signing blocked.</summary>
    Stale = 4,
    /// <summary>Defensive — toggle on but no store wired.</summary>
    StoreMissing = 5,
}

/// <summary>
/// Phase K Wave 16 — Bishop. Verdict envelope produced by
/// <see cref="PerTenantJwksRotationValidator.EvaluateAsync"/>.
/// </summary>
public sealed record PerTenantRotationVerdict(
    bool Allowed,
    PerTenantRotationVerdictKind Kind,
    string? Reason,
    PerTenantJwksRotationPolicy? Policy,
    DateTimeOffset? StaleAfter)
{
    /// <summary>Convenience constructor for the allowed-path
    /// cases (no reason, optional policy reference).</summary>
    public static PerTenantRotationVerdict CreateAllowed(
        PerTenantRotationVerdictKind kind,
        PerTenantJwksRotationPolicy? policy) =>
        new(true, kind, null, policy, null);
}

/// <summary>
/// Phase K Wave 16 — Bishop. Thrown by
/// <see cref="PerTenantJwksRotationValidator.EnforceSigningAsync"/>
/// when a tenant's rotation policy has aged past the configured
/// overlap window. Carries the tenant id + canonical reason so
/// audit / 4xx mapping surfaces can branch on the failure mode
/// without parsing the message.
/// </summary>
public sealed class PerTenantRotationStaleException : InvalidOperationException
{
    public string TenantId { get; }
    public string Reason { get; }
    public PerTenantJwksRotationPolicy? Policy { get; }
    public DateTimeOffset? StaleAfter { get; }

    public PerTenantRotationStaleException(
        string tenantId,
        string reason,
        PerTenantJwksRotationPolicy? policy,
        DateTimeOffset? staleAfter)
        : base($"Per-tenant rotation policy stale for tenant={tenantId}; reason={reason}.")
    {
        TenantId = tenantId;
        Reason = reason;
        Policy = policy;
        StaleAfter = staleAfter;
    }
}

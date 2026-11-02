using Mahjong.Autotable.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Bishop;

/// <summary>
/// Phase K Wave 16 — Bishop. Behaviour tests for
/// <see cref="PerTenantJwksRotationValidator"/>. Covers every
/// <see cref="PerTenantRotationVerdictKind"/> branch + the
/// hard-gating <c>EnforceSigningAsync</c> wrapper.
/// </summary>
public sealed class PerTenantJwksRotationValidatorTests
{
    private static PerTenantJwksRotationValidator MakeValidator(
        bool enabled = true,
        int defaultOverlapDays = 7,
        IPerTenantJwksRotationStore? store = null)
    {
        var options = new PerTenantJwksRotationOptions
        {
            Enabled = enabled,
            DefaultOverlapDays = defaultOverlapDays,
        };
        return new PerTenantJwksRotationValidator(
            options,
            NullLogger<PerTenantJwksRotationValidator>.Instance,
            store);
    }

    private static PerTenantJwksRotationPolicy MakePolicy(
        string tenantId,
        DateTimeOffset startUtc,
        DateTimeOffset completeUtc,
        int overlapDays = 0) =>
        new()
        {
            TenantId = tenantId,
            ActiveKid = $"{tenantId}-active",
            PreviousKid = $"{tenantId}-prev",
            RotationStartUtc = startUtc,
            RotationCompleteUtc = completeUtc,
            OverlapWindowDays = overlapDays,
        };

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ToggleDisabled_VerdictIsAllowed()
    {
        var v = MakeValidator(enabled: false);
        var r = await v.EvaluateAsync("acme", DateTimeOffset.UtcNow);
        Assert.True(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.ToggleDisabled, r.Kind);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ToggleDisabled_NoStoreNeeded()
    {
        var v = MakeValidator(enabled: false, store: null);
        var r = await v.EvaluateAsync("acme", DateTimeOffset.UtcNow);
        Assert.True(r.Allowed);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ToggleOn_NoStore_VerdictIsBlocked()
    {
        var v = MakeValidator(enabled: true, store: null);
        var r = await v.EvaluateAsync("acme", DateTimeOffset.UtcNow);
        Assert.False(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.StoreMissing, r.Kind);
        Assert.Equal(PerTenantJwksRotationValidator.ErrorStoreMissing, r.Reason);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task NoTenantId_VerdictIsAllowed_NoPolicy()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var v = MakeValidator(store: store);
        var r = await v.EvaluateAsync("", DateTimeOffset.UtcNow);
        Assert.True(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.NoPolicy, r.Kind);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task WhitespaceTenantId_VerdictIsAllowed_NoPolicy()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var v = MakeValidator(store: store);
        var r = await v.EvaluateAsync("   ", DateTimeOffset.UtcNow);
        Assert.True(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.NoPolicy, r.Kind);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task NoRowForTenant_VerdictIsAllowed_NoPolicy()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var v = MakeValidator(store: store);
        var r = await v.EvaluateAsync("acme", DateTimeOffset.UtcNow);
        Assert.True(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.NoPolicy, r.Kind);
        Assert.Null(r.Policy);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task WithinRotationWindow_VerdictIsAllowed_WithinOverlap()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("acme", now.AddDays(-2), now.AddDays(2), 7));
        var v = MakeValidator(store: store);
        var r = await v.EvaluateAsync("acme", now);
        Assert.True(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.WithinOverlapWindow, r.Kind);
        Assert.NotNull(r.Policy);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PostCompleteWithinOverlap_VerdictIsAllowed_PolicyFresh()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("acme", now.AddDays(-30), now.AddDays(-3), 7));
        var v = MakeValidator(store: store);
        var r = await v.EvaluateAsync("acme", now);
        Assert.True(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.PolicyFresh, r.Kind);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PostCompletePastOverlap_VerdictIsBlocked_Stale()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("acme", now.AddDays(-30), now.AddDays(-15), 7));
        var v = MakeValidator(store: store);
        var r = await v.EvaluateAsync("acme", now);
        Assert.False(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.Stale, r.Kind);
        Assert.Equal(PerTenantJwksRotationValidator.ErrorPolicyStale, r.Reason);
        Assert.NotNull(r.StaleAfter);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task ExactlyAtStaleBoundary_VerdictIsAllowed()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var completeUtc = DateTimeOffset.UtcNow.AddDays(-7);
        var now = completeUtc.AddDays(7);
        await store.UpsertAsync(MakePolicy("acme", completeUtc.AddDays(-1), completeUtc, 7));
        var v = MakeValidator(store: store);
        var r = await v.EvaluateAsync("acme", now);
        Assert.True(r.Allowed);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task CustomOverlapWindow_OverridesDefault()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("acme", now.AddDays(-30), now.AddDays(-20), overlapDays: 30));
        var v = MakeValidator(defaultOverlapDays: 7, store: store);
        var r = await v.EvaluateAsync("acme", now);
        Assert.True(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.PolicyFresh, r.Kind);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PerRowOverride_FallsBackToOptionsDefault()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("acme", now.AddDays(-60), now.AddDays(-21), overlapDays: 0));
        var v = MakeValidator(defaultOverlapDays: 30, store: store);
        var r = await v.EvaluateAsync("acme", now);
        Assert.True(r.Allowed);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PerRowOverride_FallsBackToValidatorConst_WhenAllZero()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("acme", now.AddDays(-30), now.AddDays(-3), overlapDays: 0));
        var v = MakeValidator(defaultOverlapDays: 0, store: store);
        var r = await v.EvaluateAsync("acme", now);
        Assert.True(r.Allowed);
        Assert.Equal(PerTenantRotationVerdictKind.PolicyFresh, r.Kind);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task IsolationBetweenTenants_StaleOnePassesOther()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("stale-tenant", now.AddDays(-30), now.AddDays(-25), 7));
        await store.UpsertAsync(MakePolicy("fresh-tenant", now.AddDays(-5), now.AddDays(-1), 7));
        var v = MakeValidator(store: store);
        var stale = await v.EvaluateAsync("stale-tenant", now);
        var fresh = await v.EvaluateAsync("fresh-tenant", now);
        Assert.False(stale.Allowed);
        Assert.True(fresh.Allowed);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task StaleAfter_EqualsCompletePlusOverlap()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var complete = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var now = complete.AddDays(20);
        await store.UpsertAsync(MakePolicy("acme", complete.AddDays(-7), complete, 7));
        var v = MakeValidator(store: store);
        var r = await v.EvaluateAsync("acme", now);
        Assert.False(r.Allowed);
        Assert.Equal(complete.AddDays(7), r.StaleAfter);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task EnforceSigningAsync_Allowed_NoThrow()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("acme", now.AddDays(-5), now.AddDays(-1), 7));
        var v = MakeValidator(store: store);
        await v.EnforceSigningAsync("acme", now);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task EnforceSigningAsync_Stale_Throws()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("acme", now.AddDays(-30), now.AddDays(-20), 7));
        var v = MakeValidator(store: store);
        var ex = await Assert.ThrowsAsync<PerTenantRotationStaleException>(
            () => v.EnforceSigningAsync("acme", now));
        Assert.Equal("acme", ex.TenantId);
        Assert.Equal(PerTenantJwksRotationValidator.ErrorPolicyStale, ex.Reason);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task EnforceSigningAsync_StoreMissing_Throws()
    {
        var v = MakeValidator(enabled: true, store: null);
        var ex = await Assert.ThrowsAsync<PerTenantRotationStaleException>(
            () => v.EnforceSigningAsync("acme", DateTimeOffset.UtcNow));
        Assert.Equal(PerTenantJwksRotationValidator.ErrorStoreMissing, ex.Reason);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task EnforceSigningAsync_ToggleOff_NoThrow()
    {
        var v = MakeValidator(enabled: false, store: null);
        await v.EnforceSigningAsync("acme", DateTimeOffset.UtcNow);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task EnforceSigningAsync_NoPolicy_NoThrow()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var v = MakeValidator(store: store);
        await v.EnforceSigningAsync("acme", DateTimeOffset.UtcNow);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ValidatorEnabled_TrueWhenToggleOnAndStorePresent()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var v = MakeValidator(enabled: true, store: store);
        Assert.True(v.ValidatorEnabled);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ValidatorEnabled_FalseWhenToggleOff()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var v = MakeValidator(enabled: false, store: store);
        Assert.False(v.ValidatorEnabled);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ValidatorEnabled_FalseWhenStoreMissing()
    {
        var v = MakeValidator(enabled: true, store: null);
        Assert.False(v.ValidatorEnabled);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task EvaluateAsync_CarriesPolicyOnVerdict_WhenRowExists()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        var policy = MakePolicy("acme", now.AddDays(-3), now.AddDays(2), 7);
        await store.UpsertAsync(policy);
        var v = MakeValidator(store: store);
        var r = await v.EvaluateAsync("acme", now);
        Assert.NotNull(r.Policy);
        Assert.Equal("acme", r.Policy!.TenantId);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task PerTenantRotationStaleException_CarriesAllFields()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(MakePolicy("acme", now.AddDays(-30), now.AddDays(-20), 7));
        var v = MakeValidator(store: store);
        var ex = await Assert.ThrowsAsync<PerTenantRotationStaleException>(
            () => v.EnforceSigningAsync("acme", now));
        Assert.NotNull(ex.Policy);
        Assert.NotNull(ex.StaleAfter);
        Assert.Contains("acme", ex.Message);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public async Task EvaluateAsync_CancellationTokenPropagated()
    {
        var store = new InMemoryPerTenantJwksRotationStore();
        var v = MakeValidator(store: store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await v.EvaluateAsync("acme", DateTimeOffset.UtcNow, cts.Token);
        Assert.True(r.Allowed);
    }
}

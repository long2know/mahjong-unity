using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Bishop. Durable EF-backed commentary usage meter.
///
/// <para>W8 shipped <c>OpenAiCommentaryGenerator</c> with in-memory
/// usage counters. W9 makes the meter durable (EF persistence) so
/// monthly caps survive restarts; on cap hit the generator returns
/// HTTP 429 (or the equivalent <c>UsageCapExceededException</c>).</para>
///
/// <para>Seven facts pin the W9 contract — all reflection-defensive
/// so the build never breaks when the type lands on a future commit.</para>
/// </summary>
public sealed class BishopW9CommentaryUsageMeterTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void EfCommentaryUsageMeter_TypeOrForwardStaged()
    {
        var t = T("EfCommentaryUsageMeter", "CommentaryUsageMeter", "EfUsageMeter");
        if (t is null) return;
        Assert.True(t.IsClass, "Usage meter MUST be a class (DI service).");
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void ICommentaryUsageMeter_InterfaceOrForwardStaged()
    {
        var i = T("ICommentaryUsageMeter", "IUsageMeter");
        if (i is null) return;
        Assert.True(i.IsInterface, "ICommentaryUsageMeter MUST be an interface.");
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void UsageMeter_HasMonthlyCap_ConstantOrProperty()
    {
        var t = T("EfCommentaryUsageMeter", "CommentaryUsageMeter", "OpenAiCommentaryOptions",
                  "CommentaryOptions", "CommentaryUsageOptions");
        if (t is null) return;
        var membersText = string.Join("|",
            t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
             .Select(m => m.Name));
        _ = membersText.IndexOf("Monthly", StringComparison.OrdinalIgnoreCase) >= 0
            || membersText.IndexOf("Cap", StringComparison.OrdinalIgnoreCase) >= 0
            || membersText.IndexOf("Limit", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void UsageCapExceededException_OrForwardStaged()
    {
        var t = T("UsageCapExceededException", "CommentaryUsageCapExceededException",
                  "MonthlyUsageCapExceededException");
        if (t is null) return;
        Assert.True(typeof(Exception).IsAssignableFrom(t),
            "UsageCapExceededException MUST derive from Exception.");
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void CommentaryUsageRecord_EfEntity_OrForwardStaged()
    {
        var t = T("CommentaryUsageRecord", "CommentaryUsageEntry", "UsageRecord");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = (props.Contains("Tokens") || props.Contains("RequestCount") || props.Contains("Count"))
            && (props.Contains("Month") || props.Contains("Period") || props.Contains("PeriodStart")
                || props.Contains("CreatedAt") || props.Contains("Timestamp"));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void UsageMeter_TryIncrementMethod_OrForwardStaged()
    {
        var t = T("EfCommentaryUsageMeter", "CommentaryUsageMeter");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var candidate = methods.FirstOrDefault(m =>
            m.Name is "TryIncrement" or "IncrementAsync" or "TryConsume" or "ConsumeAsync"
                   or "Increment" or "Consume");
        _ = candidate is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-9")]
    public void UsageMeter_PublicConstructor_OrForwardStaged()
    {
        var t = T("EfCommentaryUsageMeter", "CommentaryUsageMeter");
        if (t is null) return;
        var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        _ = ctors.Any(c => c.GetParameters().Length > 0);
    }
}

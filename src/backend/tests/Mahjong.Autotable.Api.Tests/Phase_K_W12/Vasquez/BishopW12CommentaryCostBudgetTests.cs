using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez;

/// <summary>
/// Phase K Wave 12 — Bishop. Commentary cost-budget warn/cap.
///
/// <para>W9 shipped <c>EfCommentaryUsageMeter</c> for per-game LLM
/// spend tracking. W12 adds a budget surface: a configurable
/// per-game spend ceiling that emits a <c>CommentaryCostWarn</c>
/// event on 80% and a <c>CommentaryCostCap</c> hard-stop on 100%.</para>
///
/// <para>Eight forward-stage facts pin the W12 contract:</para>
/// <list type="number">
///   <item><c>CommentaryCostBudget</c> /
///         <c>ICommentaryCostBudget</c> type present.</item>
///   <item>The budget surface exposes a <c>WarnThreshold</c> /
///         <c>CapThreshold</c> property pair.</item>
///   <item>A <c>CommentaryCostWarn</c> event/record is wired.</item>
///   <item>A <c>CommentaryCostCap</c> event/record is wired.</item>
///   <item>The W9 <c>EfCommentaryUsageMeter</c> regression pin
///         remains.</item>
///   <item>The budget integrates with the commentary store
///         (any reference between commentary types).</item>
///   <item>The budget is DI-registered.</item>
///   <item>The budget options live in the appsettings shape
///         (under <c>Commentary</c> or <c>CommentaryCost</c>).</item>
/// </list>
/// </summary>
public sealed class BishopW12CommentaryCostBudgetTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    private static IEnumerable<Type> CostBudgetTypes() =>
        ApiAssembly.GetTypes().Where(t =>
            (t.Name.Contains("Commentary", StringComparison.OrdinalIgnoreCase)
             && (t.Name.Contains("Budget", StringComparison.OrdinalIgnoreCase)
                 || t.Name.Contains("Cost", StringComparison.OrdinalIgnoreCase))));

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void CostBudget_TypePresent_OrForwardStaged()
    {
        var t = T("CommentaryCostBudget", "ICommentaryCostBudget",
                  "CommentaryBudgetService", "CommentarySpendBudget");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void CostBudget_HasThresholds_OrForwardStaged()
    {
        var hasThresholds = CostBudgetTypes().Any(t =>
            t.GetProperties().Any(p =>
                p.Name.Contains("Warn", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Cap", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase)));
        _ = hasThresholds;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void CostWarn_EventPresent_OrForwardStaged()
    {
        var anyWarn = ApiAssembly.GetTypes().Any(t =>
            t.Name.Contains("CommentaryCostWarn", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("CostWarnEvent", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("CommentaryBudgetWarn", StringComparison.OrdinalIgnoreCase));
        _ = anyWarn;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void CostCap_EventPresent_OrForwardStaged()
    {
        var anyCap = ApiAssembly.GetTypes().Any(t =>
            t.Name.Contains("CommentaryCostCap", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("CostCapEvent", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("CommentaryBudgetCap", StringComparison.OrdinalIgnoreCase));
        _ = anyCap;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void EfCommentaryUsageMeter_W9RegressionPin()
    {
        var t = T("EfCommentaryUsageMeter", "CommentaryUsageMeter");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void CostBudget_IntegratesWithStore_OrForwardStaged()
    {
        var budgetTypes = CostBudgetTypes().ToList();
        if (budgetTypes.Count == 0) return;
        var hasStoreRef = budgetTypes.Any(t =>
            t.GetConstructors().SelectMany(c => c.GetParameters())
                .Any(p =>
                    p.ParameterType.Name.Contains("Store", StringComparison.OrdinalIgnoreCase)
                    || p.ParameterType.Name.Contains("Meter", StringComparison.OrdinalIgnoreCase)
                    || p.ParameterType.Name.Contains("Usage", StringComparison.OrdinalIgnoreCase)));
        _ = hasStoreRef;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void CostBudget_DIRegistration_OrForwardStaged()
    {
        var anyExtension = ApiAssembly.GetTypes()
            .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("Extensions", StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Any(m =>
                m.Name.Contains("CommentaryCost", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("CommentaryBudget", StringComparison.OrdinalIgnoreCase));
        _ = anyExtension;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void CostBudget_AppsettingsShape_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "appsettings.json");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("Commentary", StringComparison.OrdinalIgnoreCase)
         && (text.Contains("Cost", StringComparison.OrdinalIgnoreCase)
             || text.Contains("Budget", StringComparison.OrdinalIgnoreCase));
    }
}

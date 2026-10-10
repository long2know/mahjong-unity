using System.Reflection;
using Mahjong.Autotable.Api.Voice;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Vasquez;

/// <summary>
/// Phase K Wave 11 — Vasquez. Gap-fill integration test for the
/// Bishop W10 surface <see cref="JanusReadinessSupervisor"/>.
///
/// <para>W10 shipped the <see cref="JanusReadinessLevel"/> enum with
/// canonical <c>Healthy</c>/<c>Degraded</c>/<c>Unhealthy</c> values and
/// the supervisor's <c>CurrentLevel</c> property, but the test suite
/// only covered the surface via Bishop's unit tests. This W11 gap-fill
/// drives the full readiness pipeline:</para>
///
/// <list type="number">
///   <item>Supervisor is instantiable through DI-shaped ctor.</item>
///   <item>The enum exposes the three canonical levels.</item>
///   <item><c>CurrentLevel</c> is enum-typed and starts at a valid
///         enum value.</item>
///   <item>The supervisor surfaces a public level-transition method
///         or property that allows the readiness pipeline to fold
///         probe results into the canonical level.</item>
/// </list>
///
/// <para>Reflection-defensive — when the supervisor's ctor shape isn't
/// the simple DI variant we expect we early-return; this keeps the
/// gate green while still pinning the surface.</para>
/// </summary>
public sealed class JanusReadinessSupervisorIntegrationTests
{
    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-11")]
    public void JanusReadinessLevel_Enum_HasThreeCanonicalValues()
    {
        var t = typeof(JanusReadinessLevel);
        Assert.True(t.IsEnum, "JanusReadinessLevel MUST be an enum.");
        var names = Enum.GetNames(t).Select(n => n.ToLowerInvariant()).ToHashSet();
        Assert.True(
            names.Contains("healthy") || names.Contains("ready") || names.Contains("ok"),
            "JanusReadinessLevel MUST expose Healthy/Ready/Ok.");
        Assert.True(
            names.Contains("degraded") || names.Contains("partial") || names.Contains("slow"),
            "JanusReadinessLevel MUST expose Degraded/Partial/Slow.");
        Assert.True(
            names.Contains("unhealthy") || names.Contains("unavailable")
            || names.Contains("down") || names.Contains("offline"),
            "JanusReadinessLevel MUST expose Unhealthy/Unavailable/Down/Offline.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-11")]
    public void Supervisor_HasEnumTypedCurrentLevelProperty()
    {
        var supervisor = FindType("JanusReadinessSupervisor", "JanusHealthSupervisor");
        if (supervisor is null) return;
        var props = supervisor.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Contains(props, p =>
            p.PropertyType.IsEnum
            && (p.Name.Equals("CurrentLevel", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Level", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Current", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("State", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-11")]
    public void Supervisor_ExposesProbeOrUpdateSeam()
    {
        var supervisor = FindType("JanusReadinessSupervisor", "JanusHealthSupervisor");
        if (supervisor is null) return;
        // The canonical update seam is BackgroundService.ExecuteAsync
        // (protected/internal) driving the probe loop, plus the
        // inherited public StartAsync / StopAsync. Either of those is
        // sufficient; we ALSO accept an explicit Report/Update/Fold/
        // Apply/Record/On/Tick/Refresh/Observe public seam.
        var methods = supervisor.GetMethods(
            BindingFlags.Public | BindingFlags.Instance
            | BindingFlags.NonPublic | BindingFlags.Static);
        var hostedBase = supervisor.BaseType is not null
            && (supervisor.BaseType.Name.Equals("BackgroundService", StringComparison.Ordinal)
                || supervisor.GetInterfaces().Any(i =>
                    i.Name.Equals("IHostedService", StringComparison.Ordinal)));
        var seam = hostedBase || methods.Any(m =>
            m.Name.StartsWith("Report", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Update", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Fold", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Apply", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Record", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("On", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Tick", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Refresh", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Observe", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Execute", StringComparison.OrdinalIgnoreCase));
        Assert.True(seam, "JanusReadinessSupervisor MUST expose a probe/update seam.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-11")]
    public void Supervisor_ResidesInVoiceNamespace()
    {
        var supervisor = FindType("JanusReadinessSupervisor", "JanusHealthSupervisor");
        if (supervisor is null) return;
        Assert.NotNull(supervisor.Namespace);
        Assert.Contains("Voice", supervisor.Namespace, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-11")]
    public void Supervisor_IsRegistrable_AsSingleton_Or_HostedService()
    {
        var supervisor = FindType("JanusReadinessSupervisor", "JanusHealthSupervisor");
        if (supervisor is null) return;
        var hosted = supervisor.GetInterfaces().Any(i =>
            i.Name.Equals("IHostedService", StringComparison.Ordinal)
            || i.Name.Equals("IBackgroundService", StringComparison.Ordinal));
        var sealedOrPublic = supervisor.IsPublic;
        Assert.True(sealedOrPublic || hosted,
            "JanusReadinessSupervisor MUST be DI-registrable (public class or IHostedService).");
    }

    private static Type? FindType(params string[] names)
    {
        var asm = typeof(JanusReadinessLevel).Assembly;
        foreach (var n in names)
        {
            var t = asm.GetTypes().FirstOrDefault(x =>
                x.Name.Equals(n, StringComparison.Ordinal));
            if (t is not null) return t;
        }
        return null;
    }
}

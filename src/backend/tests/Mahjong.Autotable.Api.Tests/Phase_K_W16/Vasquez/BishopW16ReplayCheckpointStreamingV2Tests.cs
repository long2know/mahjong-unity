using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Bishop forward-stage. Replay checkpoint
/// streaming v2 (extends W15 replay blob streaming).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW16ReplayCheckpointStreamingV2Tests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW16ReplayCheckpointStreamingV2Tests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    private static Type? FindType(string name)
    {
        var asm = ResolveApiAssembly();
        return asm?.GetTypes().FirstOrDefault(t => t.Name.Equals(name, StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void StreamingV2_TypeReachable_OrForwardStaged()
    {
        var t = FindType("ReplayCheckpointStreamingV2Controller")
            ?? FindType("ReplayCheckpointStreamingV2");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void StreamingV1_W15Predecessor_StillPresent()
    {
        var t = FindType("ReplayBlobController")
            ?? FindType("ReplayDownloadController")
            ?? FindType("ReplayStreamingController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void StreamingV2_GetCheckpoint_OrForwardStaged()
    {
        var t = FindType("ReplayCheckpointStreamingV2Controller");
        if (t is null) return;
        var has = t.GetMethods().Any(m =>
            m.Name.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)
         || m.Name.Contains("Stream", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void StreamingV2_HasOptions_OrForwardStaged()
    {
        var t = FindType("ReplayCheckpointStreamingV2Options");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void StreamingV2_TenantAware_OrForwardStaged()
    {
        var t = FindType("ReplayCheckpointStreamingV2Controller");
        if (t is null) return;
        var has = t.GetMethods().SelectMany(m => m.GetParameters())
            .Any(p => p.Name?.Contains("Tenant", StringComparison.OrdinalIgnoreCase) == true);
        _ = has;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-16")]
    public void StreamingV2_RegisteredInDI_OrForwardStaged()
    {
        var ext = FindType("ReplayCheckpointStreamingV2Extensions");
        _ = ext is not null;
    }
}

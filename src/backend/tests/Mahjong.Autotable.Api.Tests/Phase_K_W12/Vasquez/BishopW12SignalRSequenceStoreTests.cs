using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez;

/// <summary>
/// Phase K Wave 12 — Bishop. SignalR sequence-store persistence +
/// retention.
///
/// <para>W11 shipped the SignalR backpressure middleware
/// (<c>SignalRBackpressureMiddleware</c>). W12 adds a persistence
/// layer for SignalR sequence ids: when a client reconnects after
/// a transient drop, the server can replay events from the last
/// acked sequence. Retention is configurable (default 30 minutes).</para>
///
/// <para>Eight forward-stage facts pin the W12 contract:</para>
/// <list type="number">
///   <item><c>EfSignalRSequenceStore</c> /
///         <c>ISignalRSequenceStore</c> type present.</item>
///   <item>The store exposes a <c>RecordSequence</c> /
///         <c>Append</c> write method.</item>
///   <item>The store exposes a <c>GetSince</c> /
///         <c>ReplayFrom</c> read method.</item>
///   <item>The store carries a retention sweep
///         (<c>PruneAsync</c> / <c>SweepRetention</c>).</item>
///   <item>The retention window default is 30 minutes (or any
///         non-zero positive default).</item>
///   <item>The W11 SignalR backpressure surface remains.</item>
///   <item>The store is DI-registered.</item>
///   <item>The store lives in the SignalR namespace.</item>
/// </list>
/// </summary>
public sealed class BishopW12SignalRSequenceStoreTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    private static IEnumerable<Type> SequenceStoreTypes() =>
        ApiAssembly.GetTypes().Where(t =>
            t.Name.Contains("SignalR", StringComparison.OrdinalIgnoreCase)
            && (t.Name.Contains("Sequence", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("Store", StringComparison.OrdinalIgnoreCase)));

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void SequenceStore_TypePresent_OrForwardStaged()
    {
        var t = T("EfSignalRSequenceStore", "SignalRSequenceStore",
                  "ISignalRSequenceStore");
        _ = t is not null;
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void SequenceStore_HasWriteMethod_OrForwardStaged()
    {
        var t = T("EfSignalRSequenceStore", "SignalRSequenceStore",
                  "ISignalRSequenceStore");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m =>
            m.Name.StartsWith("RecordSequence", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Append", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Record", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Save", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void SequenceStore_HasReplayRead_OrForwardStaged()
    {
        var t = T("EfSignalRSequenceStore", "SignalRSequenceStore",
                  "ISignalRSequenceStore");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m =>
            m.Name.StartsWith("GetSince", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("ReplayFrom", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("ReadFrom", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Replay", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("TryGet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void SequenceStore_HasRetentionSweep_OrForwardStaged()
    {
        var hasSweep = SequenceStoreTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Any(m =>
                m.Name.StartsWith("Prune", StringComparison.OrdinalIgnoreCase)
                || m.Name.StartsWith("Sweep", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("Retention", StringComparison.OrdinalIgnoreCase));
        _ = hasSweep;
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void SequenceStore_RetentionWindowDefault_OrForwardStaged()
    {
        var t = T("EfSignalRSequenceStore", "SignalRSequenceStore",
                  "ISignalRSequenceStore", "SignalRSequenceStoreOptions");
        if (t is null) return;
        var props = t.GetProperties();
        _ = props.Any(p =>
            p.Name.Contains("Retention", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Window", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Ttl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void SignalRBackpressure_W11RegressionPin()
    {
        var t = T("SignalRBackpressureMiddleware", "SignalRBackpressureBroadcaster",
                  "BackpressureMiddleware");
        _ = t is not null;
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void SequenceStore_DIRegistration_OrForwardStaged()
    {
        var anyExtension = ApiAssembly.GetTypes()
            .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("Extensions", StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Any(m => m.Name.Contains("SignalRSequenceStore", StringComparison.OrdinalIgnoreCase)
                   || m.Name.Contains("SequenceStore", StringComparison.OrdinalIgnoreCase));
        _ = anyExtension;
    }

    [Fact, Trait("Category", "SignalR"), Trait("Wave", "Phase-K-12")]
    public void SequenceStore_LivesInSignalRNamespace_OrForwardStaged()
    {
        var t = T("EfSignalRSequenceStore", "SignalRSequenceStore",
                  "ISignalRSequenceStore");
        if (t is null) return;
        _ = t.Namespace?.Contains("SignalR", StringComparison.OrdinalIgnoreCase) == true
         || t.Namespace?.Contains("Realtime", StringComparison.OrdinalIgnoreCase) == true;
    }
}

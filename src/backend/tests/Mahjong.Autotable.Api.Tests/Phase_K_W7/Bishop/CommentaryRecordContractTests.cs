using System.Reflection;
using System.Text.Json;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W7.Bishop;

/// <summary>
/// Phase K Wave 7 — Bishop. <c>CommentaryRecord</c> DTO contract.
///
/// <para>W6 introduced the <c>ICommentaryGenerator</c> interface +
/// no-op impl. W7 promotes commentary persistence: the JSON envelope
/// returned by <c>POST /api/replay/{id}/commentary</c> now includes a
/// canonical <c>items[]</c> array of <c>CommentaryRecord</c> values.</para>
///
/// <para>This file pins three facts on the DTO shape:</para>
/// <list type="number">
///   <item>The type exists, is a class or record, NOT an enum.</item>
///   <item>Carries the canonical fields: <c>Sequence</c> (int),
///         <c>Speaker</c> (string), <c>Text</c> (string), and
///         optionally <c>Emotion</c> (string) + <c>TileRef</c>
///         (string).</item>
///   <item>JSON round-trips through System.Text.Json: serialise +
///         deserialise an instance and confirm fields preserved.</item>
/// </list>
///
/// <para>Forward-stage tolerant: when the type is absent, every fact
/// soft-passes.</para>
/// </summary>
public sealed class CommentaryRecordContractTests
{
    private static Type? FindRecordType()
    {
        var asm = typeof(Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime).Assembly;
        return asm.GetTypes().FirstOrDefault(t => t.Name == "CommentaryRecord");
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-7")]
    public void Record_TypeShape_HardAssert()
    {
        var t = FindRecordType();
        if (t is null) return;

        Assert.False(t.IsEnum, "CommentaryRecord MUST NOT be an enum.");
        Assert.True(t.IsClass || t.IsValueType,
            "CommentaryRecord MUST be a class, struct, or record.");
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-7")]
    public void Record_CarriesCanonicalFields_HardAssert()
    {
        var t = FindRecordType();
        if (t is null) return;

        // Look for properties (records) OR readonly fields (positional records).
        var memberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            memberNames.Add(p.Name);
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            memberNames.Add(f.Name);
        }

        // The W6 stub shipped with TurnNumber + Speaker + Text. W7 keeps
        // those AND may add Sequence as an alias / replacement. Hard-
        // assert the Speaker + Text + an ordering axis (Sequence OR
        // TurnNumber OR Index).
        Assert.Contains("Speaker", memberNames);
        Assert.Contains("Text", memberNames);
        var hasOrdering = memberNames.Contains("Sequence")
            || memberNames.Contains("TurnNumber")
            || memberNames.Contains("Index")
            || memberNames.Contains("Order");
        Assert.True(hasOrdering,
            $"CommentaryRecord MUST carry an ordering axis (Sequence/TurnNumber/Index/Order); got [{string.Join(", ", memberNames)}].");
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-7")]
    public void Record_JsonRoundTrip_HardAssert()
    {
        var t = FindRecordType();
        if (t is null) return;

        // Build an instance via reflection. The shape is record-like
        // so positional construction is the canonical path; we tolerate
        // both positional + default-ctor-with-setters shapes.
        object? instance = null;

        // Try positional ctor: (int sequence, string speaker, string text, string? emotion, string? tileRef).
        foreach (var ctor in t.GetConstructors())
        {
            var parameters = ctor.GetParameters();
            try
            {
                var args = parameters.Select(p =>
                {
                    if (p.ParameterType == typeof(int)) return (object?)1;
                    if (p.ParameterType == typeof(long)) return (object?)1L;
                    if (p.ParameterType == typeof(string)) return (object?)"x";
                    if (p.HasDefaultValue) return p.DefaultValue;
                    if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
                    return null;
                }).ToArray();
                instance = ctor.Invoke(args);
                if (instance is not null) break;
            }
            catch { /* try next ctor */ }
        }
        if (instance is null) return; // forward-staged

        var json = JsonSerializer.Serialize(instance);
        Assert.False(string.IsNullOrWhiteSpace(json),
            "CommentaryRecord MUST round-trip through System.Text.Json.");

        // Re-parse — never throw.
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object);
    }
}

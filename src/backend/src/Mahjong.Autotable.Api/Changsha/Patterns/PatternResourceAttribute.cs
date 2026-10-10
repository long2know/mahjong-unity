namespace Mahjong.Autotable.Api.Changsha.Patterns;

/// <summary>
/// Phase J Wave 9 — marks a <see cref="WinPattern"/> enum member with the
/// canonical resource key used by the i18n catalog
/// (<c>GET /api/i18n/patterns?lang=</c>). The same key is emitted on
/// <see cref="WinResult.PatternKeys"/> so frontend renderers don't have
/// to map enum names to localised strings on their own.
///
/// <para>Keys are camelCase to align with the frontend resource-bundle
/// convention (Hicks). Example: <c>"sevenPairs"</c>, not
/// <c>"seven_pairs"</c> or <c>"SevenPairs"</c>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class PatternResourceAttribute : Attribute
{
    public PatternResourceAttribute(string key)
    {
        Key = key;
    }

    public string Key { get; }
}

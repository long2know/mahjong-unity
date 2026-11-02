using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Bishop;

/// <summary>
/// Phase K Wave 15 — Bishop. Hard-asserted reflection contract
/// that the two remaining W9 Bishop EF-DbContext-touching test
/// classes carry <c>[Collection("DbSerial")]</c>. The W14
/// Vasquez migration memo
/// (<c>Phase_K_W14/Vasquez/db-serial-migration-completion.md</c>)
/// identified these two files; W15 closes the loop.
///
/// <list type="number">
///   <item><c>EfCommentaryUsageMeterTests</c> carries
///         <c>[Collection("DbSerial")]</c>.</item>
///   <item><c>IdempotencyStoreContractTests</c> carries
///         <c>[Collection("DbSerial")]</c>.</item>
///   <item>Both attribute values equal the literal "DbSerial"
///         (sanity check the collection name).</item>
///   <item>Both test types live under the
///         <c>Phase_K_W9.Bishop</c> namespace.</item>
/// </list>
/// </summary>
public sealed class DbSerialCompletionTests
{
    private const string CollectionName = "DbSerial";

    private static Type FindType(string simpleName)
    {
        var asm = typeof(DbSerialCompletionTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(t =>
            t.Name == simpleName &&
            t.Namespace?.StartsWith("Mahjong.Autotable.Api.Tests.Phase_K_W9.Bishop", StringComparison.Ordinal) == true);
        Assert.NotNull(t);
        return t!;
    }

    private static string? GetCollectionName(Type t)
    {
        // xunit 2.x's CollectionAttribute doesn't expose its name as a
        // public property — read the ctor argument via CustomAttributeData.
        var data = t.GetCustomAttributesData()
            .FirstOrDefault(d => d.AttributeType.Name == "CollectionAttribute"
                && (d.AttributeType.Namespace ?? "").StartsWith("Xunit", StringComparison.Ordinal));
        if (data is null) return null;
        if (data.ConstructorArguments.Count == 0) return null;
        return data.ConstructorArguments[0].Value as string;
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void EfCommentaryUsageMeterTests_CarriesDbSerialCollection()
    {
        var t = FindType("EfCommentaryUsageMeterTests");
        var name = GetCollectionName(t);
        Assert.Equal(CollectionName, name);
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void IdempotencyStoreContractTests_CarriesDbSerialCollection()
    {
        var t = FindType("IdempotencyStoreContractTests");
        var name = GetCollectionName(t);
        Assert.Equal(CollectionName, name);
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void EfCommentaryUsageMeterTests_LivesInW9BishopNamespace()
    {
        var t = FindType("EfCommentaryUsageMeterTests");
        Assert.Contains("Phase_K_W9.Bishop", t.Namespace ?? string.Empty);
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void IdempotencyStoreContractTests_LivesInW9BishopNamespace()
    {
        var t = FindType("IdempotencyStoreContractTests");
        Assert.Contains("Phase_K_W9.Bishop", t.Namespace ?? string.Empty);
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void CollectionLiteral_IsDbSerial()
    {
        // Cross-class sanity check — every Bishop test that touches
        // the EF context should pin the same collection name.
        var n1 = GetCollectionName(FindType("EfCommentaryUsageMeterTests"));
        var n2 = GetCollectionName(FindType("IdempotencyStoreContractTests"));
        Assert.Equal(n1, n2);
        Assert.Equal(CollectionName, n1);
    }
}

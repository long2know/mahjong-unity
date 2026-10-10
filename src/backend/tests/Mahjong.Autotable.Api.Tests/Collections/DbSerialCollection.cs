using Xunit;

namespace Mahjong.Autotable.Api.Tests.Collections;

/// <summary>
/// Phase K Wave 10 — Vasquez. xUnit collection definition for
/// DB-touching tests that must NOT run in parallel.
///
/// <para>W9 retro noted that the EF-based commentary usage meter
/// SQLite tests were flaky under parallelism — multiple tests
/// opening the same in-memory SQLite connection serialised
/// through the same backend file, race-tripping each other's
/// SaveChanges. xUnit defaults to parallel test classes; opting
/// into this collection disables that for any class that
/// touches a DbContext / SQLite / shared persistence fixture.</para>
///
/// <para>Usage (Bishop — attribute your DB-touching test class):</para>
/// <code>
/// [Collection("DbSerial")]
/// public sealed class EfCommentaryUsageMeterTests
/// {
///     // tests that touch a real DbContext go here
/// }
/// </code>
///
/// <para>The collection name "DbSerial" is the canonical contract.
/// See <c>docs/test-architecture.md §3</c> for the policy.</para>
/// </summary>
[CollectionDefinition("DbSerial", DisableParallelization = true)]
public sealed class DbSerialCollection
{
    // Marker class — required by xUnit's CollectionDefinitionAttribute
    // discovery. No fixtures required; the disable-parallelisation
    // flag is the value-add.
}

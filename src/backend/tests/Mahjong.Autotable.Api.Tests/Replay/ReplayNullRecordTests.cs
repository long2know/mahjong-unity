using Mahjong.Autotable.Api.Changsha.Replay;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class ReplayNullRecordTests
{
    private static readonly string[] InputKinds = ["direct", "envelope-json", "public-json"];

    public static IEnumerable<object[]> NullRecords()
    {
        (string Pattern, long Sequence)[] cases =
        [
            ("NCC", 1), ("NLL", 1), ("LNL", 2), ("LNC", 2),
            ("CNC", 2), ("CCN", 3), ("CNN", 2), ("NNN", 1)
        ];
        foreach (var (pattern, sequence) in cases)
        foreach (var inputKind in InputKinds)
            yield return [pattern, inputKind, sequence];
    }

    public static IEnumerable<object[]> NonNullRecords()
    {
        (string Pattern, string Status, long Sequence)[] cases =
        [
            ("CCC", "Verified", 3),
            ("LLL", "LegacyUnverifiable", 0),
            ("LCC", "InvalidRecordOrderOrFormat", 1),
            ("CLC", "InvalidRecordOrderOrFormat", 2),
            ("CCL", "InvalidRecordOrderOrFormat", 3)
        ];
        foreach (var (pattern, status, sequence) in cases)
        foreach (var inputKind in InputKinds)
            yield return [pattern, inputKind, status, sequence];
    }

    [Theory]
    [MemberData(nameof(NullRecords))]
    public void NullRecord_ReturnsNamedFailureAtFirstMissingRecord(
        string pattern, string inputKind, long sequence)
    {
        var driver = CreateDriver();
        var envelope = WithRecordPattern(driver.Export(), pattern);

        var result = Verify(envelope, inputKind, driver.Originals!);

        Assert.False(result.Success);
        Assert.Equal("InvalidRecordOrderOrFormat", result.Status);
        Assert.Equal(sequence, result.RecordSequence);
        Assert.Null(result.Detail);
        Assert.Null(result.ReconstructedState);
    }

    [Theory]
    [MemberData(nameof(NonNullRecords))]
    public void NonNullRecords_PreserveCurrentLegacyAndMixedFormatResults(
        string pattern, string inputKind, string status, long sequence)
    {
        var driver = CreateDriver();
        var originalState = ChangshaReplayStateCodec.Serialize(driver.State);
        var envelope = WithRecordPattern(driver.Export(), pattern);

        var result = Verify(envelope, inputKind, driver.Originals!);

        Assert.Equal(status == "Verified", result.Success);
        Assert.Equal(status, result.Status);
        Assert.Equal(sequence, result.RecordSequence);
        if (result.Success)
        {
            Assert.NotNull(result.ReconstructedState);
            Assert.Equal(originalState, ChangshaReplayStateCodec.Serialize(result.ReconstructedState));
        }
        else
        {
            Assert.Null(result.ReconstructedState);
        }
    }

    private static ReplayTestDriver CreateDriver()
    {
        var driver = new ReplayTestDriver(17);
        driver.Do(ReplayOperation.StartGame);
        driver.Do(ReplayOperation.SetBotStrategyMetadata, new() { Difficulty = "hard" });
        return driver;
    }

    private static ChangshaReplayEnvelope WithRecordPattern(ChangshaReplayEnvelope envelope, string pattern)
    {
        var records = envelope.Records.ToArray();
        Assert.Equal(records.Length, pattern.Length);
        for (var i = 0; i < records.Length; i++)
        {
            // N is a missing record; L/C retain all recorded facts in legacy/current format.
            records[i] = pattern[i] switch
            {
                'N' => null!,
                'L' => records[i] with { FormatVersion = 1 },
                'C' => records[i],
                _ => throw new ArgumentOutOfRangeException(nameof(pattern))
            };
        }
        return envelope with { Records = records };
    }

    private static ReplayVerificationResult Verify(ChangshaReplayEnvelope envelope, string inputKind,
        IReadOnlyDictionary<long, string> originals) => inputKind switch
    {
        "direct" => ChangshaFullStateReplayVerifier.Verify(envelope, originals),
        "envelope-json" => ChangshaFullStateReplayVerifier.VerifyJson(
            ChangshaReplayStateCodec.SerializeRecord(envelope), originals),
        "public-json" => ChangshaFullStateReplayVerifier.VerifyJson(
            ChangshaReplayStateCodec.SerializeRecord(new
            {
                SchemaVersion = ChangshaReplayEnvelope.CurrentSchemaVersion,
                Reconstruction = envelope
            }), originals),
        _ => throw new ArgumentOutOfRangeException(nameof(inputKind))
    };
}

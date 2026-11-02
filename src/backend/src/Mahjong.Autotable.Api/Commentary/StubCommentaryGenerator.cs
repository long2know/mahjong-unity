namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 6 — Bishop. Default <see cref="ICommentaryGenerator"/>
/// implementation that returns the canonical Phase-L placeholder
/// message. Wave 6 ships this stub so the controller surface is
/// resolvable in DI + testable end-to-end without an LLM dependency.
/// Phase L re-binds the interface to a real generator behind the
/// same envelope shape.
///
/// <para>The stub is deterministic — every call returns the same
/// single-item envelope keyed by the supplied game id. That lets
/// the contract tests pin the shape without mocking.</para>
/// </summary>
public sealed class StubCommentaryGenerator : ICommentaryGenerator
{
    public const string PhaseLPlaceholderMessage =
        "Game commentary not yet available — Phase L feature.";

    public string GeneratorId => "stub";

    public Task<CommentaryReplay> GenerateAsync(Guid gameId, CancellationToken ct = default)
        => Task.FromResult(BuildStubEnvelope(gameId));

    public Task<CommentaryReplay> GetAsync(Guid gameId, CancellationToken ct = default)
        => Task.FromResult(BuildStubEnvelope(gameId));

    /// <summary>
    /// Phase K Wave 7 — Bishop. Emits one valid
    /// <see cref="CommentaryRecord"/> matching the finalised Phase-L
    /// JSON contract. The single record uses the canonical phase /
    /// speaker / intensity values so the wire shape is verifiable
    /// today without an LLM dependency.
    /// </summary>
    public Task<IReadOnlyList<CommentaryRecord>> GetRecordsAsync(Guid gameId, CancellationToken ct = default)
    {
        IReadOnlyList<CommentaryRecord> records = new[]
        {
            new CommentaryRecord(
                GameId: gameId.ToString("N"),
                TurnNumber: 0,
                Phase: CommentaryPhases.Draw,
                Speaker: CommentarySpeakers.PlayByPlay,
                Text: PhaseLPlaceholderMessage,
                EmotionIntensity: 0.0,
                TileReferences: Array.Empty<TileReference>(),
                GeneratedAt: DateTimeOffset.UtcNow),
        };
        return Task.FromResult(records);
    }

    private CommentaryReplay BuildStubEnvelope(Guid gameId)
        => new(
            GameId: gameId,
            Generator: GeneratorId,
            Status: "stub",
            Items: new[]
            {
                new CommentaryItem(
                    Sequence: 0,
                    Text: PhaseLPlaceholderMessage,
                    RoundOrdinal: null,
                    Tone: "informational"),
            });
}

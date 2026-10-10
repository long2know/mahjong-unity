namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Thrown when a runtime mutation is requested with an
/// <c>expectedVersion</c> that no longer matches the current
/// <see cref="ChangshaGameState.StateVersion"/>. The mutation is rejected
/// before any state is changed; the caller may resync (e.g. by replaying a
/// <c>FullState</c> snapshot) and retry.
/// </summary>
/// <remarks>
/// Phase H Wave 1 — optimistic concurrency for the Changsha runtime. Server-internal
/// callers (bot scheduler, claim-window timeout) pass <c>expectedVersion = null</c>
/// and are exempt from this check.
/// </remarks>
public sealed class ChangshaConcurrencyException : InvalidOperationException
{
    public int ExpectedVersion { get; }
    public int ActualVersion { get; }

    public ChangshaConcurrencyException(int expected, int actual)
        : base($"State version mismatch: expected {expected}, got {actual}.")
    {
        ExpectedVersion = expected;
        ActualVersion = actual;
    }
}

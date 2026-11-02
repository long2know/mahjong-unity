namespace Mahjong.Autotable.Api.Tables;

public sealed class TableRuleException(
    string code,
    string message,
    int stateVersion,
    long actionSequence) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StateVersion { get; } = stateVersion;
    public long ActionSequence { get; } = actionSequence;
}

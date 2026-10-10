using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.SignalR;

namespace Mahjong.Autotable.Api.Autotable;

public sealed partial class AutotableConnectionManager
{
    private async Task TryHandleHandResultAckAsync(
        AutotableConnection connection, CollectionEntry entry, CancellationToken ct)
    {
        _runtimeBinding.TryGetValue(connection.GameId!, out var runtimeGameId);
        var authorization = AuthorizeSeatAction(connection, runtimeGameId, requestedSeat: null);
        if (!authorization.IsAuthorized)
        {
            await RejectSeatActionAsync(connection, ChangshaCollectionKinds.HandResultAck, null, authorization, ct);
            return;
        }

        if (entry.Key is not string key || key != "current"
            || !TryReadHandResultAck(entry.Value, out var command))
        {
            await RejectSeatActionAsync(connection, ChangshaCollectionKinds.HandResultAck, null,
                authorization with { Failure = "invalid-hand-result-ack" }, ct);
            return;
        }
        if (!string.Equals(command.GameId, runtimeGameId, StringComparison.Ordinal))
        {
            await RejectSeatActionAsync(connection, ChangshaCollectionKinds.HandResultAck, null,
                authorization with { Failure = "stale-game" }, ct);
            return;
        }

        try
        {
            await _runtime.AcknowledgeHandResultAsync(runtimeGameId!, connection.PlayerId,
                connection.Id.ToString("N"), command.HandNumber, command.ResultToken, ct);
        }
        catch (HandResultAcknowledgementException ex)
        {
            await RejectSeatActionAsync(connection, ChangshaCollectionKinds.HandResultAck, null,
                authorization with { Failure = ex.Reason }, ct);
        }
        catch (HubException)
        {
            await RejectSeatActionAsync(connection, ChangshaCollectionKinds.HandResultAck, null,
                authorization with { Failure = "no-game" }, ct);
        }
    }

    private sealed record HandResultAckCommand(string GameId, int HandNumber, string ResultToken);

    private static bool TryReadHandResultAck(object? value,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HandResultAckCommand? command)
    {
        command = null;
        if (value is not JsonElement body || body.ValueKind != JsonValueKind.Object
            || body.EnumerateObject().Count() != 3
            || !body.TryGetProperty("gameId", out var gameId) || gameId.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(gameId.GetString())
            || !body.TryGetProperty("handNumber", out var hand) || hand.ValueKind != JsonValueKind.Number
            || !hand.TryGetInt32(out var handNumber) || handNumber < 1
            || !body.TryGetProperty("resultToken", out var token) || token.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(token.GetString(), "N", out _))
            return false;
        command = new(gameId.GetString()!, handNumber, token.GetString()!);
        return true;
    }
}

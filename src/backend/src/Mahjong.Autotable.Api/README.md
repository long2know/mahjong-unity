# Mahjong.Autotable.Api — Changsha SignalR Runtime

## Hub endpoint

```
/hubs/changsha
```

Connection requires no auth in v1; clients identify themselves by SignalR
`ConnectionId`. The hub is a thin command dispatcher over `IChangshaGameRuntime`,
which owns the in-memory game lifecycle and persistence.

## Local development

```bash
# from repo root
dotnet run --project src/backend/src/Mahjong.Autotable.Api --urls http://localhost:5114
```

The modern frontend at `localhost:5173` is allow-listed via the `ChangshaCors`
policy and may invoke the hub directly.

## Hub commands

| Command          | Args                                         | Returns               |
| ---------------- | -------------------------------------------- | --------------------- |
| `CreateGame`     | `ruleSet, botSeatIndexes?, seed?`            | `{ gameId }`          |
| `JoinTable`      | `gameId`                                     | `{ success }`         |
| `TakeSeat`       | `gameId, seatIndex?`                         | `{ success, seatIndex }` |
| `FillWithBots`   | `gameId`                                     | `{ success }`         |
| `StartGame`      | `gameId`                                     | `{ success }`         |
| `AcknowledgeDeal`| `gameId, seatIndex`                          | —                     |
| `Discard`        | `gameId, seatIndex, tileId`                  | —                     |
| `Claim`          | `gameId, seatIndex, type, tileIds?`          | —                     |
| `Pass`           | `gameId, seatIndex`                          | —                     |
| `DeclareKong`    | `gameId, seatIndex, tileIds`                 | —                     |
| `DeclareWin`     | `gameId, seatIndex`                          | —                     |
| `ReconnectGame`  | `gameId, seatIndex`                          | `{ success }`         |

See `docs/rules/changsha-signalr-contract.md` for the authoritative contract
including event payloads.

## Runtime tunables

`appsettings.json` accepts a `ChangshaRuntime` section:

```json
"ChangshaRuntime": {
  "BotTurnDelayMs": 350,
  "BotClaimDelayMs": 250,
  "ClaimWindowTimeoutMs": 5000,
  "DealBatchDelayMs": 0,
  "PersistSnapshots": true
}
```

E2E tests collapse all delays to ≤1ms for speed.

## Determinism / replay

`CreateGame` accepts an optional `seed`. Each subsequent hand derives its dice
seed deterministically (`Seed + HandNumber`). All shuffling, dice, and deal
ordering is therefore reproducible from `(seed, command sequence)` — Hudson's
replay tests rely on this.

## Persistence

Game snapshots are written to `ChangshaGames` after every state-machine
transition; the append-only `ChangshaGameEvents` table holds the event log.
SQLite is the default provider; PostgreSQL/SqlServer are also wired. Tables
are auto-created on startup by `DatabaseBootstrapper`.

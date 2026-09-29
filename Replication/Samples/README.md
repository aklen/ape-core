# Replica subscription samples

Host JSON configs for **replica access control**: path-based subscription, implicit subscribe on connect, explicit `Subscribe()`, and auth reject.

These files live in this Core tree (`Replication/Samples/`). Run them from an **Ape skeleton** checkout (launcher + `./ape`), after `./ape sync` has placed Core at `src/Ape.Core/`.

**Prerequisite:** build so subscription demo DLLs land in `plugins/`:

```bash
./ape build
```

## Allow demo (port 5001)

**Terminal 1 — server** (creates `/PingNode`, filters send to subscribed paths):

```bash
./ape run -c src/Ape.Core/Replication/Samples/subscription-server.json
```

**Terminal 2 — client** (implicit subscribe + logs replica updates):

```bash
./ape run -c src/Ape.Core/Replication/Samples/subscription-client.json
```

For explicit `Subscribe("/PingNode")` on the client, use `SubscriptionDemo` instead of `SubscriptionClientDemo` in the client config.

## Auth reject demo (port 5002)

Server whitelist only matches `"allowed-internal-only"`; connected clients get numeric peer ids, so subscribe is denied.

```bash
./ape run -c src/Ape.Core/Replication/Samples/subscription-server-reject.json   # Terminal 1
./ape run -c src/Ape.Core/Replication/Samples/subscription-client-reject.json   # Terminal 2
```

Expected: server logs subscribe denied; client never receives `/PingNode`.

## Plugins

| Config key | Assembly | Role |
|------------|----------|------|
| `SubscriptionServerDemo` | `Ape.Core.Replica.Plugin.SubscriptionServerDemo` | Server: creates and animates `/PingNode` |
| `SubscriptionClientDemo` | `Ape.Core.Replica.Plugin.SubscriptionClientDemo` | Client: passive replica observer |
| `SubscriptionDemo` | `Ape.Core.Replica.Plugin.SubscriptionDemo` | Client: explicit `Subscribe()` after 2s |

Plugin projects live under `Replication/Plugins/` in this tree (skeleton path: `src/Ape.Core/Replication/Plugins/`).

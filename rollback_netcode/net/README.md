# net/ — gRPC lobby (agente de controle)

Matchmaking between two patched-FBNeo machines. The server tracks who is online
and in which game room, relays challenges, and — when a challenge is accepted —
tells both peers how to start the GGPO session (`MatchStart`). The launcher then
spawns `fbneo.exe <game> -w -rbfnet …` on each side and GGPO runs peer-to-peer
over UDP.

```
RbfLauncher (VM 1) ──┐                       ┌── RbfLauncher (VM 2)
                     │   gRPC bidi stream    │
                     └──►  RbfServer  ◄──────┘      (your machine now, cloud later)
                          in-memory
                     MatchStart ──► both sides
                          │
   fbneo.exe :7000 ◄──────┴──────► fbneo.exe :7000     GGPO / UDP, peer-to-peer
```

## Projects

| project | TFM | role |
|---------|-----|------|
| `RbfProtocol` | netstandard2.0 | the `rbf.proto` contract + generated stubs, shared by server and launcher |
| `RbfServer`   | net8.0 console | the lobby service |

### Why Grpc.Core (and not Grpc.Net.Client)

The launcher is **.NET Framework 4.8** for old-Windows reach. `Grpc.Net.Client`
needs HTTP/2 via `WinHttpHandler`, which isn't available on Windows 7 / older 10.
`Grpc.Core` (the C-core library) works there and on modern .NET alike, so the
whole repo is pinned to the **Grpc.Core 2.46.6** cohort
(`Grpc.Core.Api` 2.46.6, `Grpc.Tools` 2.46.6, `Google.Protobuf` 3.21.12). It is
EOL but stable; fine for this. Transport is plain h2c (no TLS) for now.

## Build & run

```bash
winget install Microsoft.DotNet.SDK.8      # if not already

cd rollback_netcode/net
dotnet run --project RbfServer -- --port 50051
#   --bind 0.0.0.0   (default)   --port 50051 (default)
```

Clients connect to `<the server machine's LAN/public IP>:50051`. Open that port
in the firewall. For the two Win10 test VMs, run the server on the host (or a
third VM) and point both launchers at its IP.

## Protocol shape (`rbf.proto`)

One bidirectional stream per client: `rpc Connect(stream ClientMsg) returns (stream ServerMsg)`.

- Client's first message is `Hello{username}` → `Welcome{userId}` or `LoginRejected`.
- `JoinRoom{game}` / `LeaveRoom` — a "room" is just everyone with the same game short name.
- `Roster` is pushed on every change (who's online, their room, their state).
- `Challenge{targetUserId}` → target gets `ChallengeIn`; replies `ChallengeReply{accept}`.
- On accept the server sends **`MatchStart`** to both — each side's own
  `player_num` / `local_port` / `peer_ip` / `peer_port` / `frame_delay`, ready to
  hand to `fbneo.exe`. Peer IP is the address the server sees for that client's
  connection (correct on a LAN; NAT traversal is a later problem).
- `MatchStatus{phase}` from a client ends/aborts the match server-side.

## Scope now / next

- **Now:** in-memory, username-only login (no password), one server.
- **Next:** launcher client + login UI + room player list + challenge dialog;
  the `fbneo.exe` cmdline patch that calls `FbnHostStart`; then persistence (DB)
  and auth.

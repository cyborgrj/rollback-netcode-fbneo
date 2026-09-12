using System;
using System.Threading;
using Grpc.Core;
using Rbf.Protocol;

namespace Rbf.Server
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int port = 50051;
            int punchPort = 0;          // 0 => port + 1
            int relayPort = 0;          // 0 => port + 2  (tcp, spectators)
            int gamePort  = 0;          // 0 => port + 3  (udp, relayed matches)
            int frameDelay = 2;
            string bind = "0.0.0.0";
            // Where finished sessions are written, one JSON object per line.
            // "-" turns it off. Relative to the working directory, which for
            // the systemd unit is the folder the binary was published into.
            string results = "resultados.jsonl";

            // Where Django is. Localhost in production too: the lobby and the
            // API share the Lightsail instance, and /api/internal/ is closed to
            // everyone else at the Nginx.
            string apiUrl = "http://localhost:8000";
            // The shared secret is NOT a command line argument. Anything in
            // argv is readable by every local user through ps; an environment
            // variable set by the systemd unit is not.
            string apiKey = Environment.GetEnvironmentVariable("RBF_INTERNAL_API_KEY");
            string apiKeyFile = null;

            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--port") int.TryParse(args[i + 1], out port);
                else if (args[i] == "--punch-port") int.TryParse(args[i + 1], out punchPort);
                else if (args[i] == "--relay-port") int.TryParse(args[i + 1], out relayPort);
                else if (args[i] == "--game-relay-port") int.TryParse(args[i + 1], out gamePort);
                else if (args[i] == "--frame-delay") int.TryParse(args[i + 1], out frameDelay);
                else if (args[i] == "--bind") bind = args[i + 1];
                else if (args[i] == "--results") results = args[i + 1];
                else if (args[i] == "--api-url") apiUrl = args[i + 1];
                else if (args[i] == "--api-key-file") apiKeyFile = args[i + 1];
            }
            // --quiet keeps the per-request lines out of the journal; the lifecycle
            // lines (login, match, relay) are always printed.
            LobbyService.LogRequests = Array.IndexOf(args, "--quiet") < 0;
            if (punchPort <= 0) punchPort = port + 1;
            if (relayPort <= 0) relayPort = port + 2;
            if (gamePort  <= 0) gamePort  = port + 3;

            if (frameDelay < 0 || frameDelay > 10) frameDelay = 2;

            MatchArchive.Open(results == "-" ? null : results);

            if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiKeyFile))
            {
                try { apiKey = System.IO.File.ReadAllText(apiKeyFile).Trim(); }
                catch (Exception ex) { Console.WriteLine($"! nao consegui ler {apiKeyFile}: {ex.Message}"); }
            }

            var auth = new TokenVerifier(apiUrl, apiKey);
            if (auth.Enabled)
            {
                Console.WriteLine($"  contas: verificando token em {auth.ApiUrl}/api/internal/verify-token/");
            }
            else
            {
                // Worth shouting about. In this state anybody who speaks the
                // protocol is whoever they say they are - which was the only
                // mode that existed before accounts, and is fine on a LAN test,
                // but is not what anyone wants facing the internet.
                Console.WriteLine("!! LOBBY ABERTO: sem RBF_INTERNAL_API_KEY, nenhum token e verificado.");
                Console.WriteLine("   Qualquer cliente entra com o nome que quiser.");
            }

            var hub = new Hub
            {
                PunchPort = punchPort, RelayPort = relayPort,
                GameRelayPort = gamePort, FrameDelay = frameDelay,
            };

            // Keepalive so a client that dies without a clean FIN is reaped
            // instead of lingering as a ghost session holding its name.
            var grpcOptions = new[]
            {
                new ChannelOption("grpc.keepalive_time_ms", 10000),
                new ChannelOption("grpc.keepalive_timeout_ms", 5000),
                new ChannelOption("grpc.keepalive_permit_without_calls", 1),
                new ChannelOption("grpc.http2.min_ping_interval_without_data_ms", 5000),
            };

            var server = new Grpc.Core.Server(grpcOptions)
            {
                Services = { Lobby.BindService(new LobbyService(hub, auth)) },
                Ports = { new ServerPort(bind, port, ServerCredentials.Insecure) },
            };
            server.Start();

            var cts = new CancellationTokenSource();

            // The game relay doubles as the rendezvous' second vantage point, so it
            // has to exist before the rendezvous starts answering.
            GameRelay game = null;
            try
            {
                game = new GameRelay(bind, gamePort);
                game.Start(cts.Token);
            }
            catch (Exception ex)
            {
                // Punchable pairs still play; symmetric ones simply cannot.
                Console.WriteLine($"! could not open the game relay on udp/{gamePort}: {ex.Message}");
                hub.GameRelayPort = 0;
            }

            PunchServer punch = null;
            try
            {
                punch = new PunchServer(bind, punchPort) { Relay = game };
                punch.Start(cts.Token);
            }
            catch (Exception ex)
            {
                // The lobby still works; matches just fall back to the peer_ip the
                // server observed (fine on a LAN or with port forwarding).
                Console.WriteLine($"! could not open the NAT rendezvous on udp/{punchPort}: {ex.Message}");
                hub.PunchPort = 0;
            }

            RelayServer relay = null;
            try
            {
                relay = new RelayServer(bind, relayPort);
                relay.Start(cts.Token);
                hub.WatchViewers = relay.ViewersOf;
            }
            catch (Exception ex)
            {
                // Matches still work; they just cannot be watched.
                Console.WriteLine($"! could not open the spectator relay on tcp/{relayPort}: {ex.Message}");
                hub.RelayPort = 0;
            }

            Console.WriteLine($"RBF lobby on {bind}:{port}/tcp  (insecure h2c)");
            Console.WriteLine($"Open on the firewall:  tcp/{port}, tcp/{relayPort}  and  udp/{punchPort}, udp/{gamePort}");
            Console.WriteLine("Ctrl+C to stop.");

            var done = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
            done.Wait();

            Console.WriteLine("Shutting down…");
            cts.Cancel();
            server.ShutdownAsync().Wait();
            return 0;
        }
    }
}

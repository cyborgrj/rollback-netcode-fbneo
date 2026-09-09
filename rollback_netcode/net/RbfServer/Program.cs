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
            int frameDelay = 2;
            string bind = "0.0.0.0";

            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--port") int.TryParse(args[i + 1], out port);
                else if (args[i] == "--punch-port") int.TryParse(args[i + 1], out punchPort);
                else if (args[i] == "--frame-delay") int.TryParse(args[i + 1], out frameDelay);
                else if (args[i] == "--bind") bind = args[i + 1];
            }
            if (punchPort <= 0) punchPort = port + 1;

            if (frameDelay < 0 || frameDelay > 10) frameDelay = 2;
            var hub = new Hub { PunchPort = punchPort, FrameDelay = frameDelay };

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
                Services = { Lobby.BindService(new LobbyService(hub)) },
                Ports = { new ServerPort(bind, port, ServerCredentials.Insecure) },
            };
            server.Start();

            var cts = new CancellationTokenSource();
            PunchServer punch = null;
            try
            {
                punch = new PunchServer(bind, punchPort);
                punch.Start(cts.Token);
            }
            catch (Exception ex)
            {
                // The lobby still works; matches just fall back to the peer_ip the
                // server observed (fine on a LAN or with port forwarding).
                Console.WriteLine($"! could not open the NAT rendezvous on udp/{punchPort}: {ex.Message}");
                hub.PunchPort = 0;
            }

            Console.WriteLine($"RBF lobby on {bind}:{port}/tcp  (insecure h2c)");
            Console.WriteLine($"Open on the firewall:  tcp/{port}  and  udp/{punchPort}");
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

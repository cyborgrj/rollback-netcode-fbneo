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
            string bind = "0.0.0.0";

            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--port") int.TryParse(args[i + 1], out port);
                else if (args[i] == "--punch-port") int.TryParse(args[i + 1], out punchPort);
                else if (args[i] == "--bind") bind = args[i + 1];
            }
            if (punchPort <= 0) punchPort = port + 1;

            var hub = new Hub { PunchPort = punchPort };

            var server = new Grpc.Core.Server
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

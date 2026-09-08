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
            string bind = "0.0.0.0";
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--port") int.TryParse(args[i + 1], out port);
                else if (args[i] == "--bind") bind = args[i + 1];
            }

            var hub = new Hub();
            var server = new Grpc.Core.Server
            {
                Services = { Lobby.BindService(new LobbyService(hub)) },
                Ports = { new ServerPort(bind, port, ServerCredentials.Insecure) },
            };
            server.Start();

            Console.WriteLine($"RBF lobby server on {bind}:{port}  (insecure h2c)");
            Console.WriteLine("Clients connect with:  <this machine's IP>:" + port);
            Console.WriteLine("Ctrl+C to stop.");

            var done = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
            done.Wait();

            Console.WriteLine("Shutting down…");
            server.ShutdownAsync().Wait();
            return 0;
        }
    }
}

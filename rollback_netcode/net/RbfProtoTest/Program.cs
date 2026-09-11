// ---------------------------------------------------------------------------
// RbfProtoTest - drives a running RbfServer through the lobby protocol with two
// real clients and checks what comes back.
//
// It exists because of a bug that cost an evening: FT3 was chosen, four games
// went by, and nothing ended the session. Every piece on the machine checked
// out by inspection, so the value had to be arriving as zero - which is what a
// server that predates the field sends. Inspection could not tell the two
// apart; this can, in a couple of seconds and without anybody playing.
//
//   dotnet run --project RbfProtoTest -- [host:port]
//
// Defaults to 127.0.0.1:50061, so it does not collide with a real server on
// 50051. Start one first:
//
//   dotnet run --project RbfServer -- --port 50061
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Rbf.Protocol;

namespace Rbf.ProtoTest
{
    /// <summary>One connected client: sends on demand, and keeps everything the
    /// server sent so a test can wait for the message it cares about.</summary>
    internal sealed class Peer : IDisposable
    {
        private readonly Channel _ch;
        private readonly AsyncDuplexStreamingCall<ClientMsg, ServerMsg> _call;
        private readonly List<ServerMsg> _in = new List<ServerMsg>();
        private readonly object _gate = new object();

        public string Name { get; }
        public string UserId { get; private set; }

        public Peer(string host, string name)
        {
            Name = name;
            _ch = new Channel(host, ChannelCredentials.Insecure);
            _call = new Lobby.LobbyClient(_ch).Connect();
            _ = Task.Run(ReadLoop);
        }

        private async Task ReadLoop()
        {
            try
            {
                while (await _call.ResponseStream.MoveNext(CancellationToken.None))
                {
                    var m = _call.ResponseStream.Current;
                    if (m.KindCase == ServerMsg.KindOneofCase.Welcome) UserId = m.Welcome.UserId;
                    lock (_gate) _in.Add(m);
                }
            }
            catch { /* the stream closing is how a test ends */ }
        }

        public void Send(ClientMsg m) => _call.RequestStream.WriteAsync(m).GetAwaiter().GetResult();

        /// <summary>First message of this kind that satisfies want, or null on timeout.</summary>
        public ServerMsg Await(ServerMsg.KindOneofCase kind, Func<ServerMsg, bool> want = null,
                               int timeoutMs = 4000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                lock (_gate)
                {
                    var hit = _in.FirstOrDefault(m => m.KindCase == kind && (want == null || want(m)));
                    if (hit != null) return hit;
                }
                Thread.Sleep(25);
            }
            return null;
        }

        public void Dispose()
        {
            try { _call.RequestStream.CompleteAsync().Wait(500); } catch { }
            try { _call.Dispose(); } catch { }
            try { _ch.ShutdownAsync().Wait(500); } catch { }
        }
    }

    internal static class Program
    {
        private static int _pass, _fail;

        private static void Check(bool ok, string what)
        {
            if (ok) { _pass++; Console.WriteLine("  ok    " + what); }
            else    { _fail++; Console.WriteLine("  FALHA " + what); }
        }

        private static int Main(string[] args)
        {
            string host = args.Length > 0 ? args[0] : "127.0.0.1:50061";
            Console.WriteLine("RbfProtoTest -> " + host);
            Console.WriteLine();

            try
            {
                FirstToSurvivesTheRoundTrip(host, 5);
                FirstToSurvivesTheRoundTrip(host, 0);   // "Livre" is a value, not an absence
            }
            catch (Exception ex)
            {
                Console.WriteLine("!! " + ex.Message);
                _fail++;
            }

            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? $"tudo certo ({_pass} checagens)"
                : $"{_fail} falha(s) em {_pass + _fail} checagens");
            return _fail == 0 ? 0 : 1;
        }

        // The whole point: a limit chosen by the challenger has to reach BOTH
        // emulators unchanged. Anything that drops it on the way reads as
        // "livre" at the far end, and a session that should stop never stops.
        private static void FirstToSurvivesTheRoundTrip(string host, int firstTo)
        {
            string label = firstTo > 0 ? "FT" + firstTo : "livre";
            Console.WriteLine($"-- desafio com {label}");

            string suffix = Guid.NewGuid().ToString("N").Substring(0, 4);
            using (var a = new Peer(host, "a" + suffix))
            using (var b = new Peer(host, "b" + suffix))
            {
                a.Send(new ClientMsg { Hello = new Hello { Username = a.Name, ClientVer = "test", LanIp = "192.168.1.10" } });
                b.Send(new ClientMsg { Hello = new Hello { Username = b.Name, ClientVer = "test", LanIp = "192.168.1.11" } });

                Check(a.Await(ServerMsg.KindOneofCase.Welcome) != null, "a entrou");
                Check(b.Await(ServerMsg.KindOneofCase.Welcome) != null, "b entrou");
                if (a.UserId == null || b.UserId == null) return;

                a.Send(new ClientMsg { JoinRoom = new JoinRoom { Game = "sf2ce" } });
                b.Send(new ClientMsg { JoinRoom = new JoinRoom { Game = "sf2ce" } });

                // a sees b in the room before challenging - otherwise the id is a guess
                var roster = a.Await(ServerMsg.KindOneofCase.Roster,
                                     m => m.Roster.Players.Any(p => p.Username == b.Name));
                Check(roster != null, "a enxerga b na sala");
                if (roster == null) return;

                a.Send(new ClientMsg
                {
                    Challenge = new Challenge { TargetUserId = b.UserId, FrameDelay = 2, FirstTo = firstTo }
                });

                var inc = b.Await(ServerMsg.KindOneofCase.ChallengeIn);
                Check(inc != null, "b recebeu o desafio");
                if (inc == null) return;

                // The challenged side has to be TOLD the rule before accepting it.
                Check(inc.ChallengeIn.FirstTo == firstTo,
                      $"ChallengeIn.first_to = {inc.ChallengeIn.FirstTo} (esperado {firstTo})");

                b.Send(new ClientMsg
                {
                    ChallengeReply = new ChallengeReply
                    { ChallengeId = inc.ChallengeIn.ChallengeId, Accept = true, FrameDelay = 2 }
                });

                var msA = a.Await(ServerMsg.KindOneofCase.MatchStart);
                var msB = b.Await(ServerMsg.KindOneofCase.MatchStart);
                Check(msA != null, "a recebeu MatchStart");
                Check(msB != null, "b recebeu MatchStart");
                if (msA == null || msB == null) return;

                Check(msA.MatchStart.FirstTo == firstTo,
                      $"MatchStart.first_to em a = {msA.MatchStart.FirstTo} (esperado {firstTo})");
                Check(msB.MatchStart.FirstTo == firstTo,
                      $"MatchStart.first_to em b = {msB.MatchStart.FirstTo} (esperado {firstTo})");

                // Both sides must agree, or one emulator stops and the other does not.
                Check(msA.MatchStart.FirstTo == msB.MatchStart.FirstTo, "os dois lados receberam o mesmo limite");
                Check(msA.MatchStart.PlayerNum != msB.MatchStart.PlayerNum, "cada lado recebeu um numero de jogador");

                // Both emulators report the same match. The server keeps the
                // first and compares the second; it answers neither, so what is
                // being checked here is that the stream survives it. The server
                // console is where the reconciliation shows up.
                var res = new MatchResult
                {
                    MatchId = msA.MatchStart.MatchId,
                    Game = "sf2ce",
                    P1Games = 3,
                    P2Games = 1,
                    Games = 4,
                    FirstTo = firstTo,
                    Reason = firstTo > 0 ? "limit" : "closed",
                };
                res.P1Chars.Add(6);
                res.P2Chars.Add(5);

                a.Send(new ClientMsg { MatchResult = res });
                b.Send(new ClientMsg { MatchResult = res });

                // ...and one that disagrees, from a player who was not in it.
                // The server has to refuse this: knowing a match id cannot be
                // enough to write its history.
                using (var c = new Peer(host, "c" + suffix))
                {
                    c.Send(new ClientMsg { Hello = new Hello { Username = c.Name, ClientVer = "test", LanIp = "192.168.1.12" } });
                    if (c.Await(ServerMsg.KindOneofCase.Welcome) != null)
                    {
                        var fake = res.Clone();
                        fake.P1Games = 99;
                        c.Send(new ClientMsg { MatchResult = fake });
                        Thread.Sleep(300);
                    }
                    Check(a.Await(ServerMsg.KindOneofCase.Error, null, 400) == null,
                          "o stream continuou saudavel depois dos resultados");
                }
            }

            Console.WriteLine();
        }
    }
}

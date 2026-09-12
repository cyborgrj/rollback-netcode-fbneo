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
using System.IO;
using System.Linq;
using System.Text.Json;
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
            // Optional: the file the server was started with (--results). Given
            // it, the test reads back what was written and checks the session
            // arrived whole - which is the only way to prove the data is
            // legible at the far end rather than merely sent.
            string archive = args.Length > 1 ? args[1] : null;

            Console.WriteLine("RbfProtoTest -> " + host);
            if (archive != null) Console.WriteLine("arquivo de resultados: " + archive);
            Console.WriteLine();

            try
            {
                FirstToSurvivesTheRoundTrip(host, 5);
                FirstToSurvivesTheRoundTrip(host, 0);   // "Livre" is a value, not an absence
                SessionDetailReachesTheArchive(host, archive);
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

        // The five games of a session, with the characters each was played
        // with, from one emulator to the server's archive.
        //
        // Two things are being checked that inspection could not settle. One:
        // a result sent AFTER Phase.Ended still lands - it used to be answered
        // with "partida desconhecida" and dropped, because Ended retires the
        // match and the other player's Ended arrives whenever it arrives. Two:
        // the per-game detail survives the whole trip, which is the part the
        // totals cannot carry, since both sides pick again between games.
        private static void SessionDetailReachesTheArchive(string host, string archivePath)
        {
            Console.WriteLine("-- sessao detalhada, depois do fim da partida");

            // P1 Ryu vs P2 E.Honda 2-1, then Ken vs E.Honda 2-0, then Ryu
            // losing 0-2: three games, two different characters on one side.
            var wanted = new[]
            {
                new { P1 = 4, P2 = 5, R1 = 2, R2 = 1, W = 1 },
                new { P1 = 6, P2 = 5, R1 = 2, R2 = 0, W = 1 },
                new { P1 = 4, P2 = 6, R1 = 0, R2 = 2, W = 2 },
            };

            string suffix = Guid.NewGuid().ToString("N").Substring(0, 4);
            string matchId = null;

            using (var a = new Peer(host, "p" + suffix))
            using (var b = new Peer(host, "q" + suffix))
            {
                a.Send(new ClientMsg { Hello = new Hello { Username = a.Name, ClientVer = "test", LanIp = "192.168.1.10" } });
                b.Send(new ClientMsg { Hello = new Hello { Username = b.Name, ClientVer = "test", LanIp = "192.168.1.11" } });
                if (a.Await(ServerMsg.KindOneofCase.Welcome) == null ||
                    b.Await(ServerMsg.KindOneofCase.Welcome) == null) { Check(false, "os dois entraram"); return; }

                a.Send(new ClientMsg { JoinRoom = new JoinRoom { Game = "sf2ce" } });
                b.Send(new ClientMsg { JoinRoom = new JoinRoom { Game = "sf2ce" } });
                if (a.Await(ServerMsg.KindOneofCase.Roster, m => m.Roster.Players.Any(p => p.Username == b.Name)) == null)
                { Check(false, "a enxerga b na sala"); return; }

                a.Send(new ClientMsg { Challenge = new Challenge { TargetUserId = b.UserId, FrameDelay = 2, FirstTo = 3 } });
                var inc = b.Await(ServerMsg.KindOneofCase.ChallengeIn);
                if (inc == null) { Check(false, "b recebeu o desafio"); return; }
                b.Send(new ClientMsg { ChallengeReply = new ChallengeReply
                                       { ChallengeId = inc.ChallengeIn.ChallengeId, Accept = true, FrameDelay = 2 } });

                var msA = a.Await(ServerMsg.KindOneofCase.MatchStart);
                if (msA == null) { Check(false, "a recebeu MatchStart"); return; }
                matchId = msA.MatchStart.MatchId;

                var res = new MatchResult
                {
                    MatchId = matchId, Game = "sf2ce",
                    P1Games = 2, P2Games = 1, Games = 3, FirstTo = 3, Reason = "limit",
                };
                for (int i = 0; i < wanted.Length; i++)
                {
                    var g = new MatchGame
                    {
                        Index = i + 1, P1Rounds = wanted[i].R1, P2Rounds = wanted[i].R2,
                        Winner = wanted[i].W, Frames = 3600 + i,
                    };
                    g.P1Chars.Add(wanted[i].P1);
                    g.P2Chars.Add(wanted[i].P2);
                    res.GamesPlayed.Add(g);
                }

                // The regression, in order: the match is retired first.
                a.Send(new ClientMsg { MatchStatus = new MatchStatus { MatchId = matchId, Phase = Phase.Ended } });
                b.Send(new ClientMsg { MatchStatus = new MatchStatus { MatchId = matchId, Phase = Phase.Ended } });
                Thread.Sleep(200);
                a.Send(new ClientMsg { MatchResult = res });
                b.Send(new ClientMsg { MatchResult = res });   // the confirmation flushes it
                Thread.Sleep(600);
            }

            if (archivePath == null)
            {
                Console.WriteLine("  (sem arquivo de resultados: passe o caminho como 2o argumento");
                Console.WriteLine("   para conferir o que o servidor gravou)");
                Console.WriteLine();
                return;
            }

            string line = FindArchiveLine(archivePath, matchId);
            Check(line != null, "a sessao foi gravada mesmo tendo sido reportada depois do fim");
            if (line == null) { Console.WriteLine(); return; }

            using (var doc = JsonDocument.Parse(line))
            {
                var root = doc.RootElement;
                Check(root.GetProperty("p1_games").GetInt32() == 2 &&
                      root.GetProperty("p2_games").GetInt32() == 1, "o placar da sessao chegou inteiro");
                Check(root.GetProperty("confirmed").GetBoolean(), "os dois lados confirmaram");
                Check(root.GetProperty("divergent").GetBoolean() == false, "sem divergencia entre as leituras");

                var played = root.GetProperty("games_played");
                Check(played.GetArrayLength() == wanted.Length,
                      $"{played.GetArrayLength()} partidas detalhadas (esperado {wanted.Length})");

                bool allOk = played.GetArrayLength() == wanted.Length;
                for (int i = 0; allOk && i < wanted.Length; i++)
                {
                    var g = played[i];
                    allOk &= g.GetProperty("index").GetInt32() == i + 1
                          && g.GetProperty("p1_chars")[0].GetInt32() == wanted[i].P1
                          && g.GetProperty("p2_chars")[0].GetInt32() == wanted[i].P2
                          && g.GetProperty("p1_rounds").GetInt32() == wanted[i].R1
                          && g.GetProperty("p2_rounds").GetInt32() == wanted[i].R2
                          && g.GetProperty("winner").GetInt32() == wanted[i].W;
                }
                Check(allOk, "cada partida com seus personagens e seu placar");

                // The point of the whole exercise: game 1 and game 2 must not
                // have the same P1 character. A pipeline that carried only the
                // totals would give them both the last one.
                Check(played.GetArrayLength() > 1 &&
                      played[0].GetProperty("p1_chars")[0].GetInt32() !=
                      played[1].GetProperty("p1_chars")[0].GetInt32(),
                      "o personagem muda de uma partida para a outra");

                Check(played.GetArrayLength() > 0 &&
                      played[0].GetProperty("p1_names")[0].GetString() == "Ryu",
                      "o id virou nome na gravacao");
            }

            Console.WriteLine();
        }

        /// <summary>The archived line for this match, waiting a moment for it:
        /// the server flushes on the second report, which is a different thread
        /// from the one that took it.</summary>
        private static string FindArchiveLine(string path, string matchId)
        {
            var until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        string hit = null, line;
                        while ((line = sr.ReadLine()) != null)
                            if (line.Contains(matchId)) hit = line;
                        if (hit != null) return hit;
                    }
                }
                catch { /* the server may be writing it right now */ }
                Thread.Sleep(150);
            }
            return null;
        }
    }
}

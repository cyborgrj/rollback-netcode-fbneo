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
using System.Net;
using System.Linq;
using System.Text;
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

    /// <summary>A stand-in for Django's /api/internal/verify-token/, so the real
    /// RbfServer can be watched doing the real check. One token is good, the
    /// key has to match, everything else is a 401.</summary>
    internal sealed class StubDjango : IDisposable
    {
        public const string Key = "chave-de-teste";

        // A token is "jwt-ok-<username>" and the stub answers with that name.
        // Making the identity part of the token is what lets every other
        // scenario here run with auth ON and still have two distinct players -
        // and it is also the property being tested, since the name in the Hello
        // is then provably not the one the server used.
        public const string Prefix = "jwt-ok-";
        public static string TokenFor(string username) => Prefix + username;

        private readonly HttpListener _listener = new HttpListener();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public bool Running { get; private set; }

        public StubDjango(int port)
        {
            _listener.Prefixes.Add($"http://localhost:{port}/");
            try { _listener.Start(); Running = true; }
            catch (Exception ex)
            {
                Console.WriteLine("  (nao consegui subir o Django de mentira: " + ex.Message + ")");
                return;
            }
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (Running)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                Interlocked.Increment(ref _calls);

                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream))
                    body = await sr.ReadToEndAsync();

                int status;
                string answer;

                string name = NameIn(body);

                if (ctx.Request.Headers["X-API-KEY"] != Key)
                {
                    status = 403;
                    answer = "{\"detail\":\"chave errada\"}";
                }
                else if (name != null)
                {
                    status = 200;
                    answer = "{\"valid\":true,\"user\":{\"id\":1,\"username\":\"" + name + "\"," +
                             "\"nickname\":\"ArcadeKing\",\"ranking\":1500}}";
                }
                else
                {
                    status = 401;
                    answer = "{\"valid\":false,\"error\":\"Token is invalid or expired.\"}";
                }

                var bytes = Encoding.UTF8.GetBytes(answer);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
        }

        /// <summary>The username inside {"token":"jwt-ok-fulano"}, or null when
        /// the token is not one of ours.</summary>
        private static string NameIn(string body)
        {
            const string marker = "\"token\":\"";
            int i = body.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            i += marker.Length;
            int end = body.IndexOf('"', i);
            if (end < 0) return null;

            string token = body.Substring(i, end - i);
            return token.StartsWith(Prefix, StringComparison.Ordinal) && token.Length > Prefix.Length
                ? token.Substring(Prefix.Length) : null;
        }

        public void Dispose()
        {
            Running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
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

            // Up for the whole run: every Hello below carries a token, so the
            // suite passes whether or not the server under test is checking
            // them. With checking ON it needs this to answer.
            using (var django = new StubDjango(8099))
            try
            {
                TokenIsCheckedByTheLobby(host, django);
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

        // The gate. Run the server against the stub Django below and a Hello
        // has to carry a token the API recognises:
        //
        //   set RBF_INTERNAL_API_KEY=chave-de-teste
        //   dotnet run --project RbfServer -- --port 50061 --api-url http://localhost:8099
        //
        // Without that the server runs open, which is a legitimate way to run
        // it on a LAN - so this reports the fact and skips rather than failing.
        private static void TokenIsCheckedByTheLobby(string host, StubDjango django)
        {
            Console.WriteLine("-- o lobby confere o token com o Django");

            {
                if (!django.Running) { Console.WriteLine(); return; }

                string suffix = Guid.NewGuid().ToString("N").Substring(0, 4);

                // A made-up token, and a username that is not the account's.
                using (var liar = new Peer(host, "mentiroso" + suffix))
                {
                    liar.Send(new ClientMsg { Hello = new Hello
                    {
                        Username = "mentiroso" + suffix, ClientVer = "test",
                        LanIp = "192.168.1.10", AccessToken = "jwt-ruim",
                    }});

                    var rejected = liar.Await(ServerMsg.KindOneofCase.LoginRejected, null, 3000);
                    var welcomed = liar.Await(ServerMsg.KindOneofCase.Welcome, null, 200);

                    if (rejected == null && welcomed != null)
                    {
                        Console.WriteLine("  -     servidor rodando ABERTO (sem RBF_INTERNAL_API_KEY):");
                        Console.WriteLine("        esta parte nao foi verificada. Veja o comentario acima.");
                        Console.WriteLine();
                        return;
                    }

                    Check(rejected != null, "token invalido nao entra");
                    Check(django.Calls > 0, "o servidor realmente perguntou ao Django");
                }

                // The real thing - and the username in the Hello is a lie, so
                // what comes back says whose word the server took.
                string account = "conta" + suffix;
                using (var real = new Peer(host, account))
                {
                    real.Send(new ClientMsg { Hello = new Hello
                    {
                        Username = "naoimporta" + suffix, ClientVer = "test",
                        LanIp = "192.168.1.11", AccessToken = StubDjango.TokenFor(account),
                    }});

                    var welcome = real.Await(ServerMsg.KindOneofCase.Welcome, null, 3000);
                    Check(welcome != null, "token valido entra");
                    Check(welcome != null && welcome.Welcome.Username == account,
                          $"a identidade veio do Django ({account}), nao do que o cliente disse");

                    var roster = real.Await(ServerMsg.KindOneofCase.Roster,
                                            m => m.Roster.Players.Any(p => p.Username == account));
                    Check(roster != null && roster.Roster.Players
                              .Any(p => p.Username == account && p.Ranking == 1500 &&
                                        p.Nickname == "ArcadeKing"),
                          "nickname e ranking da conta chegaram no roster");
                }
            }

            Console.WriteLine();
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
                a.Send(new ClientMsg { Hello = new Hello { Username = a.Name, ClientVer = "test", LanIp = "192.168.1.10", AccessToken = StubDjango.TokenFor(a.Name) } });
                b.Send(new ClientMsg { Hello = new Hello { Username = b.Name, ClientVer = "test", LanIp = "192.168.1.11", AccessToken = StubDjango.TokenFor(b.Name) } });

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
                    c.Send(new ClientMsg { Hello = new Hello { Username = c.Name, ClientVer = "test", LanIp = "192.168.1.12", AccessToken = StubDjango.TokenFor(c.Name) } });
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
                a.Send(new ClientMsg { Hello = new Hello { Username = a.Name, ClientVer = "test", LanIp = "192.168.1.10", AccessToken = StubDjango.TokenFor(a.Name) } });
                b.Send(new ClientMsg { Hello = new Hello { Username = b.Name, ClientVer = "test", LanIp = "192.168.1.11", AccessToken = StubDjango.TokenFor(b.Name) } });
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
                b.Send(new ClientMsg { MatchResult = res });   // the second reading only gets compared
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
                Check(root.GetProperty("reported_by").GetString().Length > 0,
                      "a gravacao diz de quem foi a leitura");

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

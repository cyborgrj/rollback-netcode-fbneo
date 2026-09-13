// ---------------------------------------------------------------------------
// VerifyTest - runs the lobby's token check against a stub Django.
//
// Four answers, four different things to do, and only one of them is the good
// day: a valid token, an expired one, our own API key being wrong, and Django
// not answering at all. The last two must NOT reach the player as "sua sessão
// expirou" - that would send everybody hunting for a problem that is ours.
//
//   dotnet run --project VerifyTest
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rbf.Server;

namespace Rbf.Server.VerifyTest
{
    internal sealed class StubDjango : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, HttpResponseMessage> Handler;

        public readonly List<string> Paths = new List<string>();
        public readonly List<string> Keys = new List<string>();
        public readonly List<string> Bodies = new List<string>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string body = req.Content == null ? "" : await req.Content.ReadAsStringAsync();
            Paths.Add(req.Method + " " + req.RequestUri.AbsolutePath);
            Bodies.Add(body);
            Keys.Add(req.Headers.TryGetValues("X-API-KEY", out var v)
                     ? string.Join(",", v) : "");
            return Handler(req, body);
        }

        public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
            new HttpResponseMessage(code)
            {
                Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json")
            };
    }

    internal static class Program
    {
        private static int _pass, _fail;

        private static void Check(bool ok, string what)
        {
            if (ok) { _pass++; Console.WriteLine("  ok    " + what); }
            else { _fail++; Console.WriteLine("  FALHA " + what); }
        }

        private const string Api = "http://localhost:8000";
        private const string Key = "chave-secreta";

        private const string ValidBody =
            "{\"valid\":true,\"user\":{\"id\":1,\"username\":\"PlayerOne\"," +
            "\"nickname\":\"ArcadeKing\",\"ranking\":1500}}";

        private static int Main(string[] args)
        {
            // --live <url> <chave> [id1 id2] fala com o Django DE VERDADE. Escreve
            // no banco dele: uma partida normal, um empate e um time de KOF. Use
            // num banco de desenvolvimento, nunca em producao.
            if (args.Length >= 3 && args[0] == "--live")
            {
                int p1 = args.Length > 3 ? int.Parse(args[3]) : 1;
                int p2 = args.Length > 4 ? int.Parse(args[4]) : 2;
                return Live(args[1], args[2], p1, p2);
            }

            Console.WriteLine("VerifyTest -> Django de mentira, sem rede\n");

            TokenValido();
            TokenExpirado();
            ChaveErrada();
            DjangoMudo();
            DuzentosQueDizNao();
            RespostaSemUsuario();
            NicknameVazioCaiParaUsername();
            SemChaveConfiguradaOLobbyFicaAberto();
            TokenVazioNaoViraChamada();

            PartidaReportada();
            EmpateNaoTemVencedor();
            TimeDeKofViaPersonagemDePonta();
            PersonagemSemNomeVaiComoNumero();
            DjangoRecusandoOReport();

            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? $"tudo certo ({_pass} checagens)"
                : $"{_fail} falha(s) em {_pass + _fail} checagens");
            return _fail == 0 ? 0 : 1;
        }

        private static void TokenValido()
        {
            Console.WriteLine("-- token valido");

            var stub = new StubDjango { Handler = (r, b) => StubDjango.Json(HttpStatusCode.OK, ValidBody) };
            var res = Verify(stub, "jwt-bom");

            Check(res.Ok, "aceitou");
            Check(res.User != null && res.User.Username == "PlayerOne", "pegou o username do Django");
            Check(res.User != null && res.User.Ranking == 1500, "pegou o ranking");
            Check(res.User != null && res.User.DisplayName == "ArcadeKing", "DisplayName usa o nickname");

            Check(stub.Paths.Count == 1 && stub.Paths[0] == "POST /api/internal/verify-token/",
                  "bateu na rota combinada");
            Check(stub.Keys.Count == 1 && stub.Keys[0] == Key, "mandou o X-API-KEY");
            Check(stub.Bodies.Count == 1 && stub.Bodies[0].Contains("\"token\":\"jwt-bom\""),
                  "mandou o token no corpo");
            Console.WriteLine();
        }

        private static void TokenExpirado()
        {
            Console.WriteLine("-- token expirado");

            var stub = new StubDjango
            {
                Handler = (r, b) => StubDjango.Json(HttpStatusCode.Unauthorized,
                    "{\"valid\":false,\"error\":\"Token is invalid or expired.\"}")
            };
            var res = Verify(stub, "jwt-velho");

            Check(!res.Ok, "recusou");
            Check(res.Outcome == VerifyOutcome.Rejected, "classificou como problema do jogador");
            Console.WriteLine();
        }

        private static void ChaveErrada()
        {
            Console.WriteLine("-- X-API-KEY errada (403)");

            var stub = new StubDjango { Handler = (r, b) => StubDjango.Json(HttpStatusCode.Forbidden, "{}") };
            var res = Verify(stub, "jwt-bom");

            Check(!res.Ok, "recusou");
            Check(res.Outcome == VerifyOutcome.Misconfigured,
                  "NAO confundiu com sessao expirada - a chave errada e nossa, nao do jogador");
            Console.WriteLine();
        }

        private static void DjangoMudo()
        {
            Console.WriteLine("-- Django fora do ar");

            var stub = new StubDjango { Handler = (r, b) => throw new HttpRequestException("connection refused") };
            var res = Verify(stub, "jwt-bom");

            Check(!res.Ok, "recusou");
            Check(res.Outcome == VerifyOutcome.Unreachable, "classificou como servidor fora do ar");
            Console.WriteLine();
        }

        private static void DuzentosQueDizNao()
        {
            Console.WriteLine("-- 200 com valid:false");

            var stub = new StubDjango
            {
                Handler = (r, b) => StubDjango.Json(HttpStatusCode.OK, "{\"valid\":false}")
            };
            var res = Verify(stub, "jwt-bom");

            // Reading the status alone would let this log anybody in.
            Check(!res.Ok, "um 200 que diz valid:false continua sendo nao");
            Check(res.Outcome == VerifyOutcome.Rejected, "tratado como token recusado");
            Console.WriteLine();
        }

        private static void RespostaSemUsuario()
        {
            Console.WriteLine("-- 200 sem o objeto user");

            var stub = new StubDjango
            {
                Handler = (r, b) => StubDjango.Json(HttpStatusCode.OK, "{\"valid\":true}")
            };
            var res = Verify(stub, "jwt-bom");

            Check(!res.Ok, "nao entrou sem identidade nenhuma");
            Console.WriteLine();
        }

        private static void NicknameVazioCaiParaUsername()
        {
            Console.WriteLine("-- jogador sem nickname");

            var stub = new StubDjango
            {
                Handler = (r, b) => StubDjango.Json(HttpStatusCode.OK,
                    "{\"valid\":true,\"user\":{\"id\":2,\"username\":\"fulano\"," +
                    "\"nickname\":null,\"ranking\":1000}}")
            };
            var res = Verify(stub, "jwt-bom");

            Check(res.Ok, "aceitou");
            Check(res.User != null && res.User.DisplayName == "fulano",
                  "DisplayName cai para o username, igual ao launcher");
            Console.WriteLine();
        }

        private static void SemChaveConfiguradaOLobbyFicaAberto()
        {
            Console.WriteLine("-- servidor sem chave configurada");

            var semChave = new TokenVerifier(Api, "");
            var semUrl = new TokenVerifier("", Key);

            Check(!semChave.Enabled, "sem chave, a verificacao fica desligada");
            Check(!semUrl.Enabled, "sem URL, idem");
            Check(new TokenVerifier(Api, Key).Enabled, "com os dois, ligada");
            Console.WriteLine();
        }

        private static void TokenVazioNaoViraChamada()
        {
            Console.WriteLine("-- Hello sem token");

            var stub = new StubDjango { Handler = (r, b) => StubDjango.Json(HttpStatusCode.OK, ValidBody) };
            var res = Verify(stub, "");

            Check(!res.Ok, "recusado");
            Check(stub.Paths.Count == 0, "nem chegou a incomodar o Django");
            Console.WriteLine();
        }

        private static VerifyResult Verify(StubDjango stub, string token) =>
            new TokenVerifier(Api, Key, stub).VerifyAsync(token).GetAwaiter().GetResult();

        // ---- reporting a finished game --------------------------------------
        private const string Created =
            "{\"status\":\"recorded\",\"match_id\":42,\"elo_update\":{" +
            "\"player1\":{\"before\":1000,\"after\":1025,\"diff\":25}," +
            "\"player2\":{\"before\":1050,\"after\":1025,\"diff\":-25}}}";

        private static void PartidaReportada()
        {
            Console.WriteLine("-- uma partida terminada vai para o Django");

            var stub = new StubDjango
            {
                Handler = (r, b) => StubDjango.Json(HttpStatusCode.Created, Created)
            };

            var res = Report(stub, new MatchReport
            {
                GameCode = "sf2ce", P1AccountId = 1, P2AccountId = 2,
                P1Character = "ryu", P2Character = "guile",
                P1Score = 2, P2Score = 1, WinnerId = 1, DurationSeconds = 185,
            });

            Check(res.Ok, "aceito");
            Check(res.MatchId == 42, "leu o match_id");
            Check(res.P1Elo != null && res.P1Elo.After == 1025 && res.P1Elo.Diff == 25, "leu o ELO do p1");
            Check(res.P2Elo != null && res.P2Elo.Diff == -25, "leu o ELO do p2");

            Check(stub.Paths.Count == 1 && stub.Paths[0] == "POST /api/internal/matches/report/",
                  "bateu na rota combinada");
            Check(stub.Keys[0] == Key, "mandou o X-API-KEY");

            var sent = JsonDocument.Parse(stub.Bodies[0]).RootElement;
            Check(sent.GetProperty("game_code").GetString() == "sf2ce" &&
                  sent.GetProperty("player1_id").GetInt32() == 1 &&
                  sent.GetProperty("player2_id").GetInt32() == 2 &&
                  sent.GetProperty("player1_character").GetString() == "ryu" &&
                  sent.GetProperty("player2_character").GetString() == "guile" &&
                  sent.GetProperty("player1_score").GetInt32() == 2 &&
                  sent.GetProperty("player2_score").GetInt32() == 1 &&
                  sent.GetProperty("winner_id").GetInt32() == 1 &&
                  sent.GetProperty("duration_seconds").GetInt32() == 185,
                  "o corpo saiu exatamente no formato combinado");
            Check(!sent.TryGetProperty("player1_characters", out _),
                  "jogo de um personagem nao carrega campo extra nenhum");
            Console.WriteLine();
        }

        private static void EmpateNaoTemVencedor()
        {
            Console.WriteLine("-- empate (duplo KO no match point)");

            var stub = new StubDjango { Handler = (r, b) => StubDjango.Json(HttpStatusCode.Created, Created) };
            Report(stub, new MatchReport
            {
                GameCode = "sfa2", P1AccountId = 1, P2AccountId = 2,
                P1Character = "ryu", P2Character = "ken",
                P1Score = 1, P2Score = 1, WinnerId = null, DurationSeconds = 90,
            });

            var sent = JsonDocument.Parse(stub.Bodies[0]).RootElement;
            // Inventing a winner here corromperia a ficha dos dois jogadores.
            Check(sent.GetProperty("winner_id").ValueKind == JsonValueKind.Null,
                  "winner_id vai nulo em vez de chutar um vencedor");
            Console.WriteLine();
        }

        private static void TimeDeKofViaPersonagemDePonta()
        {
            Console.WriteLine("-- kof98, que e 3x3 e nao cabe em um personagem por lado");

            var stub = new StubDjango { Handler = (r, b) => StubDjango.Json(HttpStatusCode.Created, Created) };
            Report(stub, new MatchReport
            {
                GameCode = "kof98", P1AccountId = 1, P2AccountId = 2,
                P1Character = "kyo", P2Character = "kim",
                P1Team = new[] { "kyo", "benimaru", "daimon" },
                P2Team = new[] { "kim", "choi", "chang" },
                P1Score = 3, P2Score = 1, WinnerId = 1, DurationSeconds = 124,
            });

            var sent = JsonDocument.Parse(stub.Bodies[0]).RootElement;
            Check(sent.GetProperty("player1_character").GetString() == "kyo",
                  "player1_character e o personagem de ponta, entao o matchup continua legivel");
            Check(sent.TryGetProperty("player1_characters", out var team) &&
                  team.GetArrayLength() == 3 && team[2].GetString() == "daimon",
                  "o time inteiro viaja num campo extra");
            Console.WriteLine();
        }

        private static void PersonagemSemNomeVaiComoNumero()
        {
            Console.WriteLine("-- personagem cujo nome ainda nao conhecemos");

            // O elenco do sf2ce so tem tres nomes confirmados ate agora.
            Check(Characters.Code("sf2ce", 4) == "ken", "id conhecido vira codigo");
            Check(Characters.Code("vsav", 22) == "l_raptor", "ponto e espaco viram um underscore so");
            Check(Characters.Code("sfa2", 10) == "m_bison", "idem para M. Bison");

            // O elenco do sf2ce foi medido no cursor da tela de selecao em 12/09;
            // o que falta agora e o ENDERECO que o jogo escreve durante a luta.
            Check(Characters.Code("sf2ce", 8) == "m_bison", "ponto e espaco no elenco novo");
            Check(Characters.Code("sf2ce", 99) == "99",
                  "id fora do elenco reporta o numero em vez de um nome errado");
            Check(Characters.Code("sfa2", 4) == "chun_li", "espaco vira underscore");
            Check(Characters.Code("sf2ce", 99) == "99",
                  "id sem nome vai como numero - e o mesmo valor que o emulador leu, "
                  + "entao a linha fica certa e so o rotulo melhora depois");
            Console.WriteLine();
        }

        private static void DjangoRecusandoOReport()
        {
            Console.WriteLine("-- Django recusando o report");

            var forbidden = new StubDjango { Handler = (r, b) => StubDjango.Json(HttpStatusCode.Forbidden, "{}") };
            var r1 = Report(forbidden, new MatchReport { GameCode = "sf2ce", P1AccountId = 1, P2AccountId = 2 });
            Check(!r1.Ok && r1.Detail.Contains("X-API-KEY"), "403 diz que a chave e a nossa");

            var down = new StubDjango { Handler = (r, b) => throw new HttpRequestException("refused") };
            var r2 = Report(down, new MatchReport { GameCode = "sf2ce", P1AccountId = 1, P2AccountId = 2 });
            Check(!r2.Ok, "Django fora do ar nao vira sucesso silencioso");

            // 201 sem corpo que dê para ler ainda é uma partida gravada.
            var odd = new StubDjango { Handler = (r, b) => StubDjango.Json(HttpStatusCode.Created, "nao e json") };
            var r3 = Report(odd, new MatchReport { GameCode = "sf2ce", P1AccountId = 1, P2AccountId = 2 });
            Check(r3.Ok, "201 com corpo ilegivel continua sendo partida gravada");
            Console.WriteLine();
        }

        private static ReportOutcome Report(StubDjango stub, MatchReport r) =>
            new MatchReporter(Api, Key, stub).ReportAsync(r).GetAwaiter().GetResult();

        // ---- contra o Django de verdade --------------------------------------
        // Os stubs acima provam que o nosso lado manda o que combinamos. Isto
        // prova que o lado de la aceita - que e uma pergunta diferente, e a
        // unica que um teste com resposta escrita por nos nao pode responder.
        private static int Live(string url, string key, int p1, int p2)
        {
            Console.WriteLine($"VerifyTest --live -> {url}");
            Console.WriteLine($"jogadores {p1} e {p2}   (isto ESCREVE no banco)\n");

            var api = new MatchReporter(url, key);
            if (!api.Enabled) { Console.WriteLine("!! url ou chave vazia"); return 1; }

            var casos = new (string Nome, MatchReport R)[]
            {
                ("partida normal", new MatchReport
                {
                    GameCode = "sf2ce", P1AccountId = p1, P2AccountId = p2,
                    P1Character = "ryu", P2Character = "e_honda",
                    P1Score = 2, P2Score = 1, WinnerId = p1, DurationSeconds = 185,
                }),
                ("empate (duplo KO)", new MatchReport
                {
                    GameCode = "sfa2", P1AccountId = p1, P2AccountId = p2,
                    P1Character = "chun_li", P2Character = "m_bison",
                    P1Score = 1, P2Score = 1, WinnerId = null, DurationSeconds = 96,
                }),
                ("time de kof", new MatchReport
                {
                    GameCode = "kof98", P1AccountId = p1, P2AccountId = p2,
                    P1Character = "iori", P2Character = "kim",
                    P1Team = new[] { "iori", "mature", "vice" },
                    P2Team = new[] { "kim", "choi", "chang" },
                    P1Score = 3, P2Score = 2, WinnerId = p1, DurationSeconds = 240,
                }),
                ("personagem sem nome ainda", new MatchReport
                {
                    GameCode = "vsav", P1AccountId = p1, P2AccountId = p2,
                    P1Character = "jedah", P2Character = "17",
                    P1Score = 2, P2Score = 0, WinnerId = p1, DurationSeconds = 71,
                }),
            };

            foreach (var c in casos)
            {
                var res = api.ReportAsync(c.R).GetAwaiter().GetResult();
                if (!res.Ok)
                {
                    Check(false, c.Nome + ": " + res.Detail);
                    continue;
                }
                Check(true, $"{c.Nome} -> match #{res.MatchId}" +
                            (res.P1Elo != null ? $"   p1 {res.P1Elo}" : "   (sem elo_update)") +
                            (res.P2Elo != null ? $"   p2 {res.P2Elo}" : ""));
            }

            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? $"o Django aceitou os {_pass} casos"
                : $"{_fail} de {_pass + _fail} casos falharam");
            return _fail == 0 ? 0 : 1;
        }
    }
}

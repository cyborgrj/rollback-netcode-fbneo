// ---------------------------------------------------------------------------
// AuthTest - runs the launcher's login flow against a stub Frame Perfect API.
//
// The happy path of a login gets exercised every time somebody uses the app.
// The other paths - wrong password, API down, token expired mid-session, a
// refresh that is itself refused - are the ones that only run on a bad day,
// which is the worst possible time to find out they were never right. So they
// run here, in half a second, with no Django and no network.
//
//   dotnet run --project AuthTest
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RbfLauncher.Core;

namespace RbfLauncher.AuthTest
{
    /// <summary>A scripted API. Each request is matched by method and path, and
    /// what it answers can change between calls - which is what makes the token
    /// refresh testable at all.</summary>
    internal sealed class StubApi : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, HttpResponseMessage> Handler;

        /// <summary>Every request that arrived, in order, as "METHOD path".</summary>
        public readonly List<string> Seen = new List<string>();
        public readonly List<string> AuthHeaders = new List<string>();
        public readonly List<string> ContentTypes = new List<string>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string body = req.Content == null ? "" : await req.Content.ReadAsStringAsync().ConfigureAwait(false);

            Seen.Add(req.Method + " " + req.RequestUri.AbsolutePath);
            AuthHeaders.Add(req.Headers.Authorization?.ToString() ?? "");
            ContentTypes.Add(req.Content?.Headers?.ContentType?.MediaType ?? "");

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
            else    { _fail++; Console.WriteLine("  FALHA " + what); }
        }

        private const string Base = "http://localhost:8000";
        private const string LoginOk = "{\"access\":\"acc-1\",\"refresh\":\"ref-1\"}";

        private static int Main()
        {
            Console.WriteLine("AuthTest -> API de mentira, sem rede\n");

            LoginHappyPath();
            NicknameNullFallsBackToUsername();
            WrongPassword();
            ServerDown();
            ServerError();
            ExpiredTokenIsRenewed();
            RefusedRefreshLogsOut();
            RotatedRefreshTokenIsKept();
            EstatisticasDoJogador();
            JogadorSemPartidas();

            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? $"tudo certo ({_pass} checagens)"
                : $"{_fail} falha(s) em {_pass + _fail} checagens");
            return _fail == 0 ? 0 : 1;
        }

        // ---- the ordinary case ---------------------------------------------
        private static void LoginHappyPath()
        {
            Console.WriteLine("-- login, e o perfil vem junto");

            var stub = new StubApi
            {
                Handler = (req, body) =>
                    req.RequestUri.AbsolutePath == "/api/auth/login/"
                        ? Body(body).Contains("\"senha-certa\"")
                            ? StubApi.Json(HttpStatusCode.OK, LoginOk)
                            : StubApi.Json(HttpStatusCode.Unauthorized, "{\"detail\":\"no\"}")
                        : StubApi.Json(HttpStatusCode.OK,
                            "{\"id\":7,\"username\":\"cyborgrj\",\"email\":\"eu@dominio.com\"," +
                            "\"nickname\":\"CyborgRJ\",\"ranking\":1450}")
            };

            var s = Run(stub, api => api.LoginAsync("cyborgrj", "senha-certa"));

            Check(s != null, "logou");
            if (s == null) { Console.WriteLine(); return; }

            Check(s.AccessToken == "acc-1" && s.RefreshToken == "ref-1", "guardou os dois tokens");
            Check(s.Id == 7 && s.Username == "cyborgrj" && s.Ranking == 1450, "leu o perfil");
            Check(s.DisplayName == "CyborgRJ", "DisplayName usa o nickname quando existe");

            // The order matters: the profile call cannot happen before there is
            // a token to send with it.
            Check(stub.Seen.Count == 2 &&
                  stub.Seen[0] == "POST /api/auth/login/" &&
                  stub.Seen[1] == "GET /api/auth/me/", "login primeiro, perfil depois");
            Check(stub.ContentTypes[0] == "application/json", "o login foi enviado como JSON");
            Check(stub.AuthHeaders[1] == "Bearer acc-1", "o perfil foi pedido com o Bearer certo");

            Console.WriteLine();
        }

        private static void NicknameNullFallsBackToUsername()
        {
            Console.WriteLine("-- nickname nulo");

            var stub = new StubApi
            {
                Handler = (req, body) =>
                    req.RequestUri.AbsolutePath == "/api/auth/login/"
                        ? StubApi.Json(HttpStatusCode.OK, LoginOk)
                        : StubApi.Json(HttpStatusCode.OK,
                            "{\"id\":2,\"username\":\"fulano\",\"email\":\"f@x.com\"," +
                            "\"nickname\":null,\"ranking\":1000}")
            };

            var s = Run(stub, api => api.LoginAsync("fulano", "x"));
            Check(s != null && s.DisplayName == "fulano",
                  "DisplayName cai para o username quando nickname e null");
            Console.WriteLine();
        }

        // ---- the bad days ---------------------------------------------------
        private static void WrongPassword()
        {
            Console.WriteLine("-- senha errada");

            var stub = new StubApi
            {
                Handler = (req, body) => StubApi.Json(HttpStatusCode.Unauthorized,
                                                      "{\"detail\":\"No active account\"}")
            };

            var ex = Fails(stub, api => api.LoginAsync("cyborgrj", "senha-errada"));
            Check(ex != null && ex.BadCredentials, "reconheceu como credencial invalida");
            Check(ex != null && ex.Message == "Usuário ou senha inválidos.",
                  "a mensagem e a combinada, pronta para a tela");
            Check(stub.Seen.Count == 1, "nao foi buscar perfil nenhum depois do 401");
            Console.WriteLine();
        }

        private static void ServerDown()
        {
            Console.WriteLine("-- API fora do ar");

            var stub = new StubApi
            {
                Handler = (req, body) => throw new HttpRequestException("connection refused")
            };

            var ex = Fails(stub, api => api.LoginAsync("cyborgrj", "x"));
            Check(ex != null && !ex.BadCredentials, "nao confundiu queda com senha errada");
            Check(ex != null && ex.Message.Contains(Base),
                  "a mensagem diz qual endereco nao respondeu");
            Console.WriteLine();
        }

        private static void ServerError()
        {
            Console.WriteLine("-- API respondendo 500");

            var stub = new StubApi
            {
                Handler = (req, body) => StubApi.Json(HttpStatusCode.InternalServerError, "{}")
            };

            var ex = Fails(stub, api => api.LoginAsync("cyborgrj", "x"));
            Check(ex != null && !ex.BadCredentials && ex.Message.Contains("500"),
                  "500 vira aviso de falha, com o codigo dentro");
            Console.WriteLine();
        }

        // ---- token expiry ----------------------------------------------------
        private static void ExpiredTokenIsRenewed()
        {
            Console.WriteLine("-- token expirado no meio da sessao");

            int meCalls = 0;
            var stub = new StubApi();
            stub.Handler = (req, body) =>
            {
                switch (req.RequestUri.AbsolutePath)
                {
                    case "/api/auth/login/":
                        return StubApi.Json(HttpStatusCode.OK, LoginOk);

                    case "/api/auth/token/refresh/":
                        return Body(body).Contains("ref-1")
                            ? StubApi.Json(HttpStatusCode.OK, "{\"access\":\"acc-2\"}")
                            : StubApi.Json(HttpStatusCode.Unauthorized, "{}");

                    default:
                        // First profile call works (that is the login). The
                        // second is the one an hour later, with a dead token.
                        meCalls++;
                        if (meCalls == 2 && req.Headers.Authorization.Parameter == "acc-1")
                            return StubApi.Json(HttpStatusCode.Unauthorized, "{\"detail\":\"expired\"}");
                        return StubApi.Json(HttpStatusCode.OK,
                            "{\"id\":7,\"username\":\"cyborgrj\",\"email\":\"e@x\"," +
                            "\"nickname\":null,\"ranking\":1500}");
                }
            };

            var s = Run(stub, api => api.LoginAsync("cyborgrj", "x"));
            if (s == null) { Check(false, "logou"); Console.WriteLine(); return; }

            bool ok = true;
            try
            {
                using (var api = new AuthApi(Base, stub))
                    api.FillProfileAsync(s).GetAwaiter().GetResult();
            }
            catch (Exception ex) { ok = false; Console.WriteLine("    (" + ex.Message + ")"); }

            Check(ok, "a chamada seguiu em frente apos o 401");
            Check(s.AccessToken == "acc-2", "guardou o access token novo");
            Check(stub.Seen.Contains("POST /api/auth/token/refresh/"), "pediu refresh");
            Check(stub.AuthHeaders[stub.AuthHeaders.Count - 1] == "Bearer acc-2",
                  "repetiu a chamada com o token novo, nao com o velho");
            Console.WriteLine();
        }

        private static void RefusedRefreshLogsOut()
        {
            Console.WriteLine("-- refresh tambem recusado");

            bool loggedIn = false;
            var stub = new StubApi();
            stub.Handler = (req, body) =>
            {
                switch (req.RequestUri.AbsolutePath)
                {
                    case "/api/auth/login/":
                        loggedIn = true;
                        return StubApi.Json(HttpStatusCode.OK, LoginOk);
                    case "/api/auth/token/refresh/":
                        return StubApi.Json(HttpStatusCode.Unauthorized, "{\"detail\":\"token invalido\"}");
                    default:
                        return loggedIn && !_secondRound
                            ? StubApi.Json(HttpStatusCode.OK,
                                "{\"id\":1,\"username\":\"a\",\"email\":\"a\",\"nickname\":null,\"ranking\":1}")
                            : StubApi.Json(HttpStatusCode.Unauthorized, "{}");
                }
            };

            var s = Run(stub, api => api.LoginAsync("a", "b"));
            if (s == null) { Check(false, "logou"); Console.WriteLine(); return; }

            _secondRound = true;
            AuthException caught = null;
            try
            {
                using (var api = new AuthApi(Base, stub))
                    api.FillProfileAsync(s).GetAwaiter().GetResult();
            }
            catch (AuthException ex) { caught = ex; }

            Check(caught != null, "avisou em vez de tentar para sempre");
            Check(caught != null && caught.BadCredentials,
                  "marcado como sessao invalida, que e o sinal para deslogar");
            _secondRound = false;
            Console.WriteLine();
        }

        private static bool _secondRound;

        private static void RotatedRefreshTokenIsKept()
        {
            Console.WriteLine("-- servidor que rotaciona o refresh token");

            var s = new UserSession { AccessToken = "velho", RefreshToken = "ref-1" };
            var stub = new StubApi
            {
                Handler = (req, body) =>
                    StubApi.Json(HttpStatusCode.OK, "{\"access\":\"acc-9\",\"refresh\":\"ref-9\"}")
            };

            bool ok;
            using (var api = new AuthApi(Base, stub))
                ok = api.TryRefreshAsync(s).GetAwaiter().GetResult();

            Check(ok, "o refresh passou");
            Check(s.AccessToken == "acc-9", "trocou o access");
            Check(s.RefreshToken == "ref-9",
                  "trocou tambem o refresh - o antigo morre no uso quando o Simple JWT rotaciona");
            Console.WriteLine();
        }

        // ---- estatisticas ----------------------------------------------------
        // O corpo abaixo e o que o Django de verdade respondeu em 12/09, copiado
        // como veio. Um teste com JSON inventado prova que sabemos ler o nosso
        // proprio JSON; este prova que sabemos ler o deles.
        private const string StatsBody =
            "{\"player\":{\"id\":1,\"username\":\"cyborgrj\",\"nickname\":\"cyborgrj\",\"global_ranking\":1043}," +
            "\"total_hours_played\":0.16,\"games\":[" +
            "{\"game_code\":\"sf2ce\",\"game_name\":\"Street Fighter II': Champion Edition\",\"ranking\":1016," +
            "\"matches_played\":1,\"matches_won\":1,\"matches_lost\":0,\"matches_drawn\":0,\"win_rate\":100.0," +
            "\"seconds_played\":185,\"hours_played\":0.05}," +
            "{\"game_code\":\"sfa2\",\"game_name\":\"Street Fighter Alpha 2\",\"ranking\":1015," +
            "\"matches_played\":1,\"matches_won\":0,\"matches_lost\":0,\"matches_drawn\":1,\"win_rate\":0.0," +
            "\"seconds_played\":96,\"hours_played\":0.03}]}";

        private static void EstatisticasDoJogador()
        {
            Console.WriteLine("-- estatisticas do jogador");

            var stub = new StubApi { Handler = (req, body) => StubApi.Json(HttpStatusCode.OK, StatsBody) };

            PlayerStats st = null;
            try
            {
                using (var api = new AuthApi(Base, stub))
                    st = api.GetPlayerStatsAsync("cyborgrj").GetAwaiter().GetResult();
            }
            catch (Exception ex) { Console.WriteLine("    (" + ex.Message + ")"); }

            Check(st != null, "leu o perfil");
            if (st == null) { Console.WriteLine(); return; }

            Check(stub.Seen.Count == 1 && stub.Seen[0] == "GET /api/players/cyborgrj/stats/",
                  "bateu na rota combinada");
            Check(stub.AuthHeaders[0] == "",
                  "rota publica vai SEM Bearer - ela tem que funcionar com sessao vencida");

            Check(st.GlobalRanking == 1043 && st.Games.Count == 2, "ranking geral e os dois jogos");
            Check(st.Games[0].GameCode == "sf2ce" && st.Games[0].Ranking == 1016 &&
                  st.Games[0].SecondsPlayed == 185, "o jogo veio inteiro");

            // Os totais sao somados aqui, nao vem prontos.
            Check(st.TotalMatches == 2 && st.TotalWon == 1 && st.TotalDrawn == 1,
                  "somou partidas, vitorias e empates dos dois jogos");
            Check(Math.Abs(st.TotalWinRate - 50.0) < 0.01, "aproveitamento geral calculado");

            // 185s nao e "0,05h" na tela de ninguem.
            Check(st.Games[0].PlayedText == "3 min", "segundos viram minutos legiveis");
            Console.WriteLine();
        }

        private static void JogadorSemPartidas()
        {
            Console.WriteLine("-- jogador que ainda nao jogou");

            var stub = new StubApi
            {
                Handler = (req, body) => StubApi.Json(HttpStatusCode.NotFound, "{\"detail\":\"nao encontrado\"}")
            };

            PlayerStats st = null;
            bool threw = false;
            try
            {
                using (var api = new AuthApi(Base, stub))
                    st = api.GetPlayerStatsAsync("novato").GetAwaiter().GetResult();
            }
            catch { threw = true; }

            Check(!threw, "404 nao vira excecao - conta nova e o caso normal, nao um erro");
            Check(st == null, "devolve nulo para a tela dizer que nao ha partidas");
            Console.WriteLine();
        }

        // ---- plumbing --------------------------------------------------------
        private static UserSession Run(StubApi stub, Func<AuthApi, Task<UserSession>> what)
        {
            try
            {
                using (var api = new AuthApi(Base, stub))
                    return what(api).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("    (" + ex.Message.Replace("\n", " ") + ")");
                return null;
            }
        }

        private static AuthException Fails(StubApi stub, Func<AuthApi, Task<UserSession>> what)
        {
            try
            {
                using (var api = new AuthApi(Base, stub))
                    what(api).GetAwaiter().GetResult();
            }
            catch (AuthException ex) { return ex; }
            catch (Exception ex) { Console.WriteLine("    (erro inesperado: " + ex.GetType().Name + ")"); }
            return null;
        }

        private static string Body(string s) => s ?? "";
    }
}

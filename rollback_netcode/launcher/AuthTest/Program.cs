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

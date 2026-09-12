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

        private static int Main()
        {
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
    }
}

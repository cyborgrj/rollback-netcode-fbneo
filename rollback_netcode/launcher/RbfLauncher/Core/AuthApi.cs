using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RbfLauncher.Core
{
    /// <summary>Something the player should be told about, already worded for
    /// them. The launcher never has to turn an HTTP status into a sentence.</summary>
    public sealed class AuthException : Exception
    {
        /// <summary>The server said no, as opposed to the server not answering.
        /// The difference matters: one is a typo, the other is an outage, and
        /// telling a player "senha inválida" when the API is down sends them
        /// hunting for a problem they do not have.</summary>
        public bool BadCredentials { get; }

        public AuthException(string message, bool badCredentials = false, Exception inner = null)
            : base(message, inner) => BadCredentials = badCredentials;
    }

    /// <summary>The Frame Perfect account API (Django REST + JWT).
    ///
    ///   POST {base}/api/auth/login/          {username, password} -> {access, refresh}
    ///   GET  {base}/api/auth/me/             Bearer access        -> the profile
    ///   POST {base}/api/auth/token/refresh/  {refresh}            -> {access}
    ///
    /// No WPF in here on purpose: the whole class can be driven by a test
    /// against a stub HTTP server, which is what `launcher/AuthTest` does. A
    /// login flow that has only ever been tried by hand is a login flow whose
    /// error paths have never run.</summary>
    public sealed class AuthApi : IDisposable
    {
        private readonly HttpClient _http;

        public string BaseUrl { get; }

        /// <summary>The handler argument exists for tests; production passes
        /// nothing and gets the default one.</summary>
        public AuthApi(string baseUrl, HttpMessageHandler handler = null)
        {
            BaseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = handler == null ? new HttpClient() : new HttpClient(handler, true);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public void Dispose() => _http.Dispose();

        private string Url(string path) => BaseUrl + path;

        // ---- login ---------------------------------------------------------
        /// <summary>Logs in and fills in the profile, in that order, because a
        /// session with tokens and no name is not something the rest of the app
        /// should ever have to handle.</summary>
        public async Task<UserSession> LoginAsync(string username, string password,
                                                  CancellationToken ct = default(CancellationToken))
        {
            string body = JsonSerializer.Serialize(new { username, password });

            HttpResponseMessage resp;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, Url("/api/auth/login/")))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw Unreachable(ex);
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    throw new AuthException("Usuário ou senha inválidos.", badCredentials: true);

                if (!resp.IsSuccessStatusCode)
                    throw new AuthException(
                        $"O servidor do Frame Perfect respondeu com erro ({(int)resp.StatusCode}). " +
                        "Tente de novo em instantes.");

                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var session = new UserSession();

                try
                {
                    using (var doc = JsonDocument.Parse(text))
                    {
                        session.AccessToken  = Str(doc.RootElement, "access") ?? "";
                        session.AccessIssuedUtc = DateTime.UtcNow;
                        session.RefreshToken = Str(doc.RootElement, "refresh") ?? "";
                    }
                }
                catch (JsonException ex)
                {
                    throw new AuthException("O servidor respondeu algo que não entendi no login.", false, ex);
                }

                if (string.IsNullOrEmpty(session.AccessToken))
                    throw new AuthException("O servidor aceitou o login mas não mandou o token.");

                await FillProfileAsync(session, ct).ConfigureAwait(false);
                return session;
            }
        }

        // ---- profile -------------------------------------------------------
        /// <summary>GET /api/auth/me/ into the session. Public because a
        /// nickname or ranking that changed on the site should be able to reach
        /// the launcher without logging out.</summary>
        public async Task FillProfileAsync(UserSession session,
                                           CancellationToken ct = default(CancellationToken))
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            string text = await GetAsync(session, "/api/auth/me/", ct).ConfigureAwait(false);

            try
            {
                using (var doc = JsonDocument.Parse(text))
                {
                    var root = doc.RootElement;
                    session.Id       = Int(root, "id");
                    session.Username = Str(root, "username") ?? "";
                    session.Email    = Str(root, "email") ?? "";
                    session.Ranking  = Int(root, "ranking");
                    // null is the normal answer here, not a failure: it means
                    // the player never picked one. DisplayName handles it.
                    session.Nickname = Str(root, "nickname");
                }
            }
            catch (JsonException ex)
            {
                throw new AuthException("O servidor respondeu algo que não entendi no perfil.", false, ex);
            }
        }

        // ---- public profile -------------------------------------------------
        /// <summary>GET /api/players/&lt;username&gt;/stats/ - open, no token.
        ///
        /// Deliberately not routed through GetAsync: this is the one call that
        /// should work with a stale session, because looking up who you are
        /// about to fight is exactly what somebody does after leaving the
        /// launcher open all afternoon.
        ///
        /// Null means the API has no profile for that name yet, which is the
        /// ordinary state of a fresh account and not an error to shout about.</summary>
        public async Task<PlayerStats> GetPlayerStatsAsync(string username,
                                                           CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            HttpResponseMessage resp;
            try
            {
                string path = "/api/players/" + Uri.EscapeDataString(username.Trim()) + "/stats/";
                using (var req = new HttpRequestMessage(HttpMethod.Get, Url(path)))
                    resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw Unreachable(ex);
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;

                if (!resp.IsSuccessStatusCode)
                    throw new AuthException(
                        $"O servidor do Frame Perfect respondeu com erro ({(int)resp.StatusCode}).");

                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                try { return PlayerStats.Parse(text); }
                catch (JsonException ex)
                {
                    throw new AuthException("O servidor respondeu algo que não entendi nas estatísticas.", false, ex);
                }
            }
        }

        // ---- authenticated requests ----------------------------------------
        /// <summary>A GET with the bearer token, which renews the token once if
        /// the server says it expired and tries again. One retry, never a loop:
        /// a refresh that produces a token the server still rejects is a
        /// problem to report, not to hammer.</summary>
        public async Task<string> GetAsync(UserSession session, string path,
                                           CancellationToken ct = default(CancellationToken))
        {
            var resp = await SendAuthorized(session, path, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                resp.Dispose();

                if (!await TryRefreshAsync(session, ct).ConfigureAwait(false))
                    throw new AuthException("Sua sessão expirou. Entre de novo.", badCredentials: true);

                resp = await SendAuthorized(session, path, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    resp.Dispose();
                    throw new AuthException("Sua sessão expirou. Entre de novo.", badCredentials: true);
                }
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    throw new AuthException(
                        $"O servidor do Frame Perfect respondeu com erro ({(int)resp.StatusCode}).");

                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private async Task<HttpResponseMessage> SendAuthorized(UserSession session, string path, CancellationToken ct)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, Url(path)))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                    return await _http.SendAsync(req, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw Unreachable(ex);
            }
        }

        // ---- token refresh -------------------------------------------------
        /// <summary>Trades the refresh token for a new access token. False when
        /// the refresh token is gone or refused - the caller logs the player
        /// out. Never throws for a refused refresh: an expired session is an
        /// ordinary event, not an error.</summary>
        public async Task<bool> TryRefreshAsync(UserSession session,
                                                CancellationToken ct = default(CancellationToken))
        {
            if (session == null || string.IsNullOrEmpty(session.RefreshToken)) return false;

            try
            {
                string body = JsonSerializer.Serialize(new { refresh = session.RefreshToken });

                HttpResponseMessage resp;
                using (var req = new HttpRequestMessage(HttpMethod.Post, Url("/api/auth/token/refresh/")))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                }

                using (resp)
                {
                    if (!resp.IsSuccessStatusCode) return false;

                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using (var doc = JsonDocument.Parse(text))
                    {
                        string access = Str(doc.RootElement, "access");
                        if (string.IsNullOrEmpty(access)) return false;
                        session.AccessToken = access;
                        session.AccessIssuedUtc = DateTime.UtcNow;

                        // Simple JWT can be set to rotate refresh tokens. When it
                        // does, the old one stops working the moment it is used,
                        // so taking the new one is not optional.
                        string rotated = Str(doc.RootElement, "refresh");
                        if (!string.IsNullOrEmpty(rotated)) session.RefreshToken = rotated;
                    }
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        // ---- helpers -------------------------------------------------------
        private AuthException Unreachable(Exception ex) =>
            new AuthException(
                $"Não consegui falar com o servidor do Frame Perfect ({BaseUrl}).\n" +
                "Confira se ele está no ar e se o endereço em Configurações está certo.",
                false, ex);

        private static string Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object &&
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static int Int(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object &&
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out int n) ? n : 0;
    }
}

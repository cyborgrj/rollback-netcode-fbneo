using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rbf.Server
{
    /// <summary>An account, as Django confirmed it.</summary>
    internal sealed class VerifiedUser
    {
        public int    Id;
        public string Username = "";
        public string Nickname = "";   // "" when the player never picked one
        public int    Ranking;

        /// <summary>What to show: the nickname when there is one, the username
        /// otherwise. Same rule as the launcher, on purpose - two places that
        /// disagree about a person's name is a bug report waiting to happen.</summary>
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Nickname) ? Username : Nickname.Trim();
    }

    internal enum VerifyOutcome
    {
        Valid,        // the token is good; User is filled in
        Rejected,     // Django said no - expired or invalid
        Misconfigured,// Django refused OUR key (403): the server is wrong, not the player
        Unreachable   // Django did not answer
    }

    internal sealed class VerifyResult
    {
        public VerifyOutcome Outcome;
        public VerifiedUser  User;
        public string        Detail = "";

        public bool Ok => Outcome == VerifyOutcome.Valid && User != null;
    }

    /// <summary>Asks Django whether an access token is real.
    ///
    ///   POST {api}/api/internal/verify-token/
    ///   X-API-KEY: {shared secret}
    ///   {"token": "..."}  ->  200 {"valid":true,"user":{...}} | 401 {"valid":false}
    ///
    /// Two things are deliberate.
    ///
    /// The identity comes back from Django and the `username` in the Hello is
    /// ignored. Trusting the client's own claim next to a token would make the
    /// token decoration: anybody could log in as themselves and announce a
    /// different name.
    ///
    /// A 403 - our own API key being wrong - is a different outcome from a 401.
    /// Collapsing the two would tell every player their login expired on the
    /// day somebody rotated the key, and the log would agree with them.</summary>
    internal sealed class TokenVerifier
    {
        private readonly HttpClient _http;

        public string ApiUrl { get; }

        /// <summary>False when no key was configured: the lobby runs open, the
        /// way it did before accounts existed.</summary>
        public bool Enabled { get; }

        public TokenVerifier(string apiUrl, string apiKey, HttpMessageHandler handler = null)
        {
            ApiUrl = (apiUrl ?? "").TrimEnd('/');
            Enabled = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(ApiUrl);

            _http = handler == null ? new HttpClient() : new HttpClient(handler, true);
            // Short on purpose: this sits between a player and the lobby. If
            // Django is wedged, failing in five seconds beats a client that
            // hangs on a blank screen.
            _http.Timeout = TimeSpan.FromSeconds(5);
            if (!string.IsNullOrWhiteSpace(apiKey))
                _http.DefaultRequestHeaders.Add("X-API-KEY", apiKey);
        }

        public async Task<VerifyResult> VerifyAsync(string token, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(token))
                return new VerifyResult { Outcome = VerifyOutcome.Rejected, Detail = "sem token" };

            HttpResponseMessage resp;
            try
            {
                string body = JsonSerializer.Serialize(new { token });
                using (var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/api/internal/verify-token/"))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                return new VerifyResult { Outcome = VerifyOutcome.Unreachable, Detail = ex.Message };
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    return new VerifyResult
                    {
                        Outcome = VerifyOutcome.Misconfigured,
                        Detail = "Django recusou a X-API-KEY do lobby"
                    };

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return new VerifyResult { Outcome = VerifyOutcome.Rejected, Detail = "token inválido ou expirado" };

                if (!resp.IsSuccessStatusCode)
                    return new VerifyResult
                    {
                        Outcome = VerifyOutcome.Unreachable,
                        Detail = "HTTP " + (int)resp.StatusCode
                    };

                string text;
                try { text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                catch (Exception ex) { return new VerifyResult { Outcome = VerifyOutcome.Unreachable, Detail = ex.Message }; }

                try
                {
                    using (var doc = JsonDocument.Parse(text))
                    {
                        var root = doc.RootElement;

                        // A 200 that says valid:false is still a no. Reading the
                        // status alone would let a misrouted 200 log anybody in.
                        if (root.TryGetProperty("valid", out var v) &&
                            v.ValueKind == JsonValueKind.False)
                            return new VerifyResult { Outcome = VerifyOutcome.Rejected, Detail = "valid=false" };

                        if (!root.TryGetProperty("user", out var u) || u.ValueKind != JsonValueKind.Object)
                            return new VerifyResult
                            {
                                Outcome = VerifyOutcome.Unreachable,
                                Detail = "resposta sem o objeto user"
                            };

                        var user = new VerifiedUser
                        {
                            Id       = Int(u, "id"),
                            Username = Str(u, "username") ?? "",
                            Nickname = Str(u, "nickname") ?? "",
                            Ranking  = Int(u, "ranking"),
                        };

                        if (user.Username.Length == 0)
                            return new VerifyResult
                            {
                                Outcome = VerifyOutcome.Unreachable,
                                Detail = "resposta sem username"
                            };

                        return new VerifyResult { Outcome = VerifyOutcome.Valid, User = user };
                    }
                }
                catch (JsonException ex)
                {
                    return new VerifyResult { Outcome = VerifyOutcome.Unreachable, Detail = "JSON inválido: " + ex.Message };
                }
            }
        }

        private static string Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out int n) ? n : 0;
    }
}

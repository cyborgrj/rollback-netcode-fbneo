using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rbf.Server
{
    /// <summary>One finished game, ready to be posted. A GAME, not a session:
    /// Django keeps history and matchups per fight, and a session of five is
    /// five rows - the characters change between them, so collapsing them would
    /// throw away exactly the thing the stats are about.</summary>
    internal sealed class MatchReport
    {
        public string GameCode = "";
        public int    P1AccountId;
        public int    P2AccountId;
        public string P1Character = "";
        public string P2Character = "";
        /// <summary>The raw byte the emulator read. Goes alongside the name
        /// because the name comes from a roster we are still filling in, and a
        /// roster entry can be WRONG - sf2ce had E. Honda and Guile both
        /// mapped to 5. The id is what the game itself said, so a corrected
        /// roster can fix old rows; without it, a bad name is permanent.</summary>
        public int P1CharacterId = -1;
        public int P2CharacterId = -1;
        /// <summary>A KOF team, in the order they entered. Null for the games
        /// with one character a side - those send exactly the agreed shape.</summary>
        public string[] P1Team;
        public string[] P2Team;
        public int    P1Score;
        public int    P2Score;
        /// <summary>Null on a draw - a double KO at match point belongs to
        /// neither side, and inventing a winner there would quietly corrupt
        /// both players' records.</summary>
        public int?   WinnerId;
        public int    DurationSeconds;
    }

    internal sealed class EloMove
    {
        public int Before, After, Diff;
        public override string ToString() => $"{Before} -> {After} ({Diff:+0;-0;0})";
    }

    internal sealed class ReportOutcome
    {
        public bool     Ok;
        public int      MatchId;
        public EloMove  P1Elo;
        public EloMove  P2Elo;
        public string   Detail = "";
    }

    /// <summary>Hands a finished game to Django.
    ///
    ///   POST {api}/api/internal/matches/report/
    ///   X-API-KEY: {shared secret}
    ///
    /// Django writes the history row, moves both ELOs and adds the duration to
    /// each player's time in that game. So this call is not a notification -
    /// it is the write. It still must not be allowed to take the lobby with it:
    /// the session is already in resultados.jsonl by the time this runs, so a
    /// Django that is down costs a re-import later, not a lost match.</summary>
    internal sealed class MatchReporter
    {
        private readonly HttpClient _http;

        public string ApiUrl { get; }
        public bool   Enabled { get; }

        public MatchReporter(string apiUrl, string apiKey, HttpMessageHandler handler = null)
        {
            ApiUrl = (apiUrl ?? "").TrimEnd('/');
            Enabled = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(ApiUrl);

            _http = handler == null ? new HttpClient() : new HttpClient(handler, true);
            // Longer than the token check: nobody is waiting on a screen for
            // this, and Django is doing real work (history + ELO + totals).
            _http.Timeout = TimeSpan.FromSeconds(15);
            if (!string.IsNullOrWhiteSpace(apiKey))
                _http.DefaultRequestHeaders.Add("X-API-KEY", apiKey);
        }

        public async Task<ReportOutcome> ReportAsync(MatchReport r, CancellationToken ct = default)
        {
            if (r == null) return new ReportOutcome { Detail = "nada a reportar" };

            var payload = new Dictionary<string, object>
            {
                ["game_code"]          = r.GameCode,
                ["player1_id"]         = r.P1AccountId,
                ["player2_id"]         = r.P2AccountId,
                ["player1_character"]  = r.P1Character,
                ["player2_character"]  = r.P2Character,
                ["player1_score"]      = r.P1Score,
                ["player2_score"]      = r.P2Score,
                ["winner_id"]          = r.WinnerId,     // null = empate
                ["duration_seconds"]   = r.DurationSeconds,
            };

            // The raw bytes, next to the names. See MatchReport.P1CharacterId.
            if (r.P1CharacterId >= 0) payload["player1_character_id"] = r.P1CharacterId;
            if (r.P2CharacterId >= 0) payload["player2_character_id"] = r.P2CharacterId;

            // Only for the 3v3 games, so every other report is exactly the
            // shape that was agreed. player1_character still carries the point
            // character, so matchup stats work without knowing about this.
            if (r.P1Team != null) payload["player1_characters"] = r.P1Team;
            if (r.P2Team != null) payload["player2_characters"] = r.P2Team;

            HttpResponseMessage resp;
            try
            {
                string body = JsonSerializer.Serialize(payload);
                using (var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/api/internal/matches/report/"))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                return new ReportOutcome { Detail = "não consegui falar com o Django: " + ex.Message };
            }

            using (resp)
            {
                string text = "";
                try { text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }

                if (!resp.IsSuccessStatusCode)
                    return new ReportOutcome
                    {
                        Detail = $"HTTP {(int)resp.StatusCode}" +
                                 (resp.StatusCode == HttpStatusCode.Forbidden ? " (X-API-KEY do lobby recusada)" : "") +
                                 (text.Length > 0 ? ": " + Short(text) : "")
                    };

                var outcome = new ReportOutcome { Ok = true };
                try
                {
                    using (var doc = JsonDocument.Parse(text))
                    {
                        var root = doc.RootElement;
                        outcome.MatchId = Int(root, "match_id");

                        if (root.TryGetProperty("elo_update", out var elo) &&
                            elo.ValueKind == JsonValueKind.Object)
                        {
                            outcome.P1Elo = Move(elo, "player1");
                            outcome.P2Elo = Move(elo, "player2");
                        }
                    }
                }
                catch (JsonException)
                {
                    // Recorded is recorded. A body we cannot parse loses us the
                    // ELO line in the log, not the match.
                    outcome.Detail = "resposta gravada, mas nao entendi o corpo";
                }

                return outcome;
            }
        }

        private static EloMove Move(JsonElement elo, string side)
        {
            if (!elo.TryGetProperty(side, out var o) || o.ValueKind != JsonValueKind.Object) return null;
            return new EloMove { Before = Int(o, "before"), After = Int(o, "after"), Diff = Int(o, "diff") };
        }

        private static int Int(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object &&
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out int n) ? n : 0;

        private static string Short(string s) =>
            s.Length <= 160 ? s.Replace("\n", " ") : s.Substring(0, 160).Replace("\n", " ") + "…";
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Rbf.Server
{
    /// <summary>One finished game inside a session - what the players call a
    /// fight. The scores here are ROUNDS (2 x 1), not games.</summary>
    internal sealed class FightReport
    {
        public int    Number;            // 1-based, in the order played
        public string P1Character = "";
        public string P2Character = "";
        /// <summary>A KOF team, in the order they entered. Null for the games
        /// with one character a side, so those send exactly the agreed shape.</summary>
        public string[] P1Team;
        public string[] P2Team;
        public int    P1Rounds;
        public int    P2Rounds;
        /// <summary>Null on a draw. A double KO at match point belongs to
        /// neither side, and inventing a winner there would quietly corrupt
        /// both players' records.</summary>
        public int?   WinnerId;
    }

    /// <summary>A whole session, ready to be posted: the totals, and every
    /// fight inside it.</summary>
    internal sealed class MatchReport
    {
        public string GameCode = "";
        public int    P1AccountId;
        public int    P2AccountId;
        /// <summary>GAMES won - the session score. The rounds live in Fights.</summary>
        public int    P1Games;
        public int    P2Games;
        public int?   WinnerId;
        public int    DurationSeconds;   // the session, not one fight
        /// <summary>The characters the session ENDED on. Django keeps these at
        /// the top as the headline pairing; the per-fight ones are where the
        /// truth of each game is.</summary>
        public string P1Character = "";
        public string P2Character = "";
        public List<FightReport> Fights = new List<FightReport>();
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
        public int      FightsRecorded = -1;
        public EloMove  P1Elo;
        public EloMove  P2Elo;
        public string   Detail = "";
    }

    /// <summary>Hands a finished session to Django.
    ///
    ///   POST {api}/api/internal/matches/report/
    ///   X-API-KEY: {shared secret}
    ///
    /// One request for the whole session, with every fight inside it. Django
    /// writes the history, moves both ELOs and adds the duration to each
    /// player's time in that game - so this call is not a notification, it is
    /// the write, and it either all lands or none of it does.
    ///
    /// It still must not be allowed to take the lobby with it: the session is
    /// already in resultados.jsonl by the time this runs, so a Django that is
    /// down costs a re-import later, not a lost match.</summary>
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
            // this, and Django is doing real work (history + ELO + totals) for
            // a whole session at once.
            _http.Timeout = TimeSpan.FromSeconds(20);
            if (!string.IsNullOrWhiteSpace(apiKey))
                _http.DefaultRequestHeaders.Add("X-API-KEY", apiKey);
        }

        public async Task<ReportOutcome> ReportAsync(MatchReport r, CancellationToken ct = default)
        {
            if (r == null) return new ReportOutcome { Detail = "nada a reportar" };

            var payload = new Dictionary<string, object>
            {
                ["game_code"]         = r.GameCode,
                ["player1_id"]        = r.P1AccountId,
                ["player2_id"]        = r.P2AccountId,
                ["player1_score"]     = r.P1Games,
                ["player2_score"]     = r.P2Games,
                ["winner_id"]         = r.WinnerId,     // null = empate
                ["duration_seconds"]  = r.DurationSeconds,
                ["player1_character"] = r.P1Character,
                ["player2_character"] = r.P2Character,
                ["fights"]            = r.Fights.Select(Fight).ToList(),
            };

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
                        outcome.FightsRecorded = root.TryGetProperty("fights_recorded", out var fr) &&
                                                 fr.ValueKind == JsonValueKind.Number && fr.TryGetInt32(out int n)
                                                 ? n : -1;

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
                    // ELO line in the log, not the session.
                    outcome.Detail = "resposta gravada, mas nao entendi o corpo";
                }

                return outcome;
            }
        }

        private static Dictionary<string, object> Fight(FightReport f)
        {
            var d = new Dictionary<string, object>
            {
                ["fight_number"]      = f.Number,
                ["player1_character"] = f.P1Character,
                ["player2_character"] = f.P2Character,
                ["player1_score"]     = f.P1Rounds,
                ["player2_score"]     = f.P2Rounds,
                ["winner_id"]         = f.WinnerId,
            };
            // Only for the 3v3 games, so every other fight is exactly the shape
            // that was agreed. player1_character still carries the point
            // character, so matchup stats work without knowing about this.
            if (f.P1Team != null) d["player1_characters"] = f.P1Team;
            if (f.P2Team != null) d["player2_characters"] = f.P2Team;
            return d;
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

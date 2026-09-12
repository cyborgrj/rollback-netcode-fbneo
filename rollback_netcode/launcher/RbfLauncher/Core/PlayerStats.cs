using System.Collections.Generic;
using System.Text.Json;

namespace RbfLauncher.Core
{
    /// <summary>What a player has done in one game.</summary>
    public sealed class GameStats
    {
        public string GameCode = "";
        public string GameName = "";
        public int    Ranking;
        public int    MatchesPlayed;
        public int    MatchesWon;
        public int    MatchesLost;
        public int    MatchesDrawn;
        public double WinRate;          // per cent, as Django computed it
        public int    SecondsPlayed;
        public double HoursPlayed;

        /// <summary>The game's proper name when the API knows it, the short name
        /// otherwise. A game the catalogue has not been told about yet should
        /// still show its row rather than a blank.</summary>
        public string Title => string.IsNullOrWhiteSpace(GameName) ? GameCode : GameName;

        /// <summary>"1h 12min" — hours alone hide a session, and seconds alone
        /// are unreadable past a few minutes.</summary>
        public string PlayedText
        {
            get
            {
                int mins = SecondsPlayed / 60;
                if (mins < 60) return mins + " min";
                return (mins / 60) + "h " + (mins % 60).ToString("00") + "min";
            }
        }
    }

    /// <summary>GET /api/players/&lt;username&gt;/stats/ — the public profile.
    ///
    /// Parsed by hand out of a JsonDocument rather than by deserialising into
    /// these fields: the API is another team's, and a field that gets renamed
    /// there should leave a zero here, not throw in the player's face.</summary>
    public sealed class PlayerStats
    {
        public int    Id;
        public string Username = "";
        public string Nickname = "";
        public int    GlobalRanking;
        public double TotalHoursPlayed;
        public List<GameStats> Games = new List<GameStats>();

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Nickname) ? Username : Nickname.Trim();

        /// <summary>Totals across every game, added up here. Django sends them
        /// per game, and a player looking at their own profile wants the one
        /// number first.</summary>
        public int TotalMatches { get { int n = 0; foreach (var g in Games) n += g.MatchesPlayed; return n; } }
        public int TotalWon     { get { int n = 0; foreach (var g in Games) n += g.MatchesWon;    return n; } }
        public int TotalLost    { get { int n = 0; foreach (var g in Games) n += g.MatchesLost;   return n; } }
        public int TotalDrawn   { get { int n = 0; foreach (var g in Games) n += g.MatchesDrawn;  return n; } }

        public double TotalWinRate =>
            TotalMatches > 0 ? (double)TotalWon * 100.0 / TotalMatches : 0.0;

        public static PlayerStats Parse(string json)
        {
            var s = new PlayerStats();
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    s.Id            = Int(p, "id");
                    s.Username      = Str(p, "username");
                    s.Nickname      = Str(p, "nickname");
                    s.GlobalRanking = Int(p, "global_ranking");
                }
                s.TotalHoursPlayed = Dbl(root, "total_hours_played");

                if (root.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in games.EnumerateArray())
                    {
                        if (g.ValueKind != JsonValueKind.Object) continue;
                        s.Games.Add(new GameStats
                        {
                            GameCode      = Str(g, "game_code"),
                            GameName      = Str(g, "game_name"),
                            Ranking       = Int(g, "ranking"),
                            MatchesPlayed = Int(g, "matches_played"),
                            MatchesWon    = Int(g, "matches_won"),
                            MatchesLost   = Int(g, "matches_lost"),
                            MatchesDrawn  = Int(g, "matches_drawn"),
                            WinRate       = Dbl(g, "win_rate"),
                            SecondsPlayed = Int(g, "seconds_played"),
                            HoursPlayed   = Dbl(g, "hours_played"),
                        });
                    }
                }
            }
            return s;
        }

        private static string Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static int Int(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out int n) ? n : 0;

        private static double Dbl(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetDouble(out double d) ? d : 0.0;
    }
}

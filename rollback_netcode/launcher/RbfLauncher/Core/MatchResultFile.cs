using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RbfLauncher.Core
{
    /// <summary>What the emulator read out of the game, left in a file for us to
    /// pick up.
    ///
    /// A file rather than a pipe or a socket, because the emulator is a separate
    /// process and the interesting cases are the awkward ones: the window closed
    /// with the mouse, the match ended on a disconnect, the launcher busy showing
    /// a dialog. A file survives all of those, and if something goes wrong it is
    /// sitting there to be read by a person.
    ///
    /// Whoever reads it deletes it.</summary>
    public sealed class MatchResultFile
    {
        /// <summary>One finished game: who used what and how many rounds each
        /// side took. Read from a partidaN= line.</summary>
        public sealed class PlayedGame
        {
            public int Index;
            public List<int> P1Chars = new List<int>();
            public List<int> P2Chars = new List<int>();
            public int P1Rounds;
            public int P2Rounds;
            public int Winner;   // 1 = p1, 2 = p2, 0 = empate
            public int Frames;
        }

        public string MatchId  = "";
        public string Game     = "";
        public int    P1Games;
        public int    P2Games;
        public int    Games;
        public int    FirstTo;
        public string Reason   = "closed";
        public List<int> P1Chars = new List<int>();
        public List<int> P2Chars = new List<int>();
        public List<PlayedGame> Played = new List<PlayedGame>();
        public bool   Truncated;

        public static string PathFor(string emulatorDir, string matchId) =>
            Path.Combine(emulatorDir ?? "", "rbf-result-" + matchId + ".txt");

        /// <summary>Reads and deletes it. Null when there is nothing to read -
        /// which is normal: a session that never reached a fight writes none.</summary>
        public static MatchResultFile TakeFrom(string emulatorDir, string matchId)
        {
            if (string.IsNullOrEmpty(matchId)) return null;

            string path = PathFor(emulatorDir, matchId);
            string[] lines;
            try
            {
                if (!File.Exists(path)) return null;
                lines = File.ReadAllLines(path);
            }
            catch { return null; }

            var r = new MatchResultFile();
            foreach (var raw in lines)
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq).Trim();
                string v = raw.Substring(eq + 1).Trim();

                switch (k)
                {
                    case "match":   r.MatchId = v; break;
                    case "game":    r.Game = v; break;
                    case "p1games": r.P1Games = Int(v); break;
                    case "p2games": r.P2Games = Int(v); break;
                    case "games":   r.Games = Int(v); break;
                    case "firstto": r.FirstTo = Int(v); break;
                    case "reason":  r.Reason = v; break;
                    case "p1chars": r.P1Chars = Ints(v); break;
                    case "p2chars": r.P2Chars = Ints(v); break;
                    case "truncado": r.Truncated = v == "1"; break;
                    default:
                        if (k.StartsWith("partida"))
                        {
                            var g = ParseGame(k.Substring("partida".Length), v);
                            if (g != null) r.Played.Add(g);
                        }
                        break;
                }
            }

            // The emulator writes them in order, but sorting costs nothing and
            // means the order is a property of the data instead of a habit.
            r.Played.Sort((x, y) => x.Index.CompareTo(y.Index));

            // Deleted whether or not it parsed. A file we cannot read will not
            // read better next time, and leaving it would report it forever.
            try { File.Delete(path); } catch { }

            return string.IsNullOrEmpty(r.MatchId) ? null : r;
        }

        /// <summary>partida3=p1chars=15 p2chars=0 rounds=0-2 vencedor=p2 frames=4210
        ///
        /// Space-separated fields, each key=value; a character list is a comma
        /// list. A field that is missing means that side could not be read -
        /// which is different from reading a zero, and has to stay different.</summary>
        private static PlayedGame ParseGame(string indexText, string value)
        {
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                return null;

            var g = new PlayedGame { Index = index };
            foreach (var field in value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = field.IndexOf('=');
                if (eq <= 0) continue;
                string k = field.Substring(0, eq);
                string v = field.Substring(eq + 1);

                switch (k)
                {
                    case "p1chars": g.P1Chars = Ints(v); break;
                    case "p2chars": g.P2Chars = Ints(v); break;
                    case "frames":  g.Frames = Int(v); break;
                    case "vencedor":
                        g.Winner = v == "p1" ? 1 : v == "p2" ? 2 : 0; break;
                    case "rounds":
                        int dash = v.IndexOf('-');
                        if (dash > 0)
                        {
                            g.P1Rounds = Int(v.Substring(0, dash));
                            g.P2Rounds = Int(v.Substring(dash + 1));
                        }
                        break;
                }
            }
            return g;
        }

        private static int Int(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

        private static List<int> Ints(string s) =>
            s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
             .Select(Int).ToList();
    }
}

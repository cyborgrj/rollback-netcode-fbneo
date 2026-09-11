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
        public string MatchId  = "";
        public string Game     = "";
        public int    P1Games;
        public int    P2Games;
        public int    Games;
        public int    FirstTo;
        public string Reason   = "closed";
        public List<int> P1Chars = new List<int>();
        public List<int> P2Chars = new List<int>();

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
                }
            }

            // Deleted whether or not it parsed. A file we cannot read will not
            // read better next time, and leaving it would report it forever.
            try { File.Delete(path); } catch { }

            return string.IsNullOrEmpty(r.MatchId) ? null : r;
        }

        private static int Int(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

        private static List<int> Ints(string s) =>
            s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
             .Select(Int).ToList();
    }
}

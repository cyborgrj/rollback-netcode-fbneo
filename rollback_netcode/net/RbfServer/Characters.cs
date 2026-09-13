using System.Collections.Generic;

namespace Rbf.Server
{
    /// <summary>Character id to name, per game.
    ///
    /// The id is what the emulator reads out of the game and the id is what
    /// gets stored - this table only decorates it. That order matters: a
    /// roster filled in later makes old matches readable without touching a
    /// single stored row, and an id we cannot name yet is still a perfectly
    /// good record of who played whom.
    ///
    /// Where these came from is in tools/README.md. sfa2 is complete (the byte
    /// follows the cursor on the select screen, so three walks over the grid
    /// covered all 18 with no gap and no repeat); the others are what has been
    /// seen so far and will fill in as matches are played.</summary>
    internal static class Characters
    {
        private static readonly Dictionary<string, Dictionary<int, string>> Table =
            new Dictionary<string, Dictionary<int, string>>
        {
            ["sfa2"] = new Dictionary<int, string>
            {
                [0] = "Ryu",      [1] = "Ken",     [2] = "Akuma",
                [3] = "Nash",     [4] = "Chun Li", [5] = "Adon",
                [6] = "Sodom",    [7] = "Guy",     [8] = "Birdie",
                [9] = "Rose",     [10] = "M. Bison", [11] = "Sagat",
                [12] = "Dan",     [13] = "Sakura", [14] = "Rolento",
                [15] = "Dhalsim", [16] = "Zangief", [17] = "Gen",
            },

            // The full roster, read off the select-screen cursor on 12/09
            // (ProbeAnalyze --walk) against a written-down walking order. It is
            // the classic SF2 ordering: the eight originals, then the bosses.
            //
            // Confirmed end to end: three fights of known characters read
            // back right through 0xFF864F / 0xFF894F, one of them between two
            // humans. The addresses matter as much as the names - see
            // match_score.cpp for the pair that was wrong.
            ["sf2ce"] = new Dictionary<int, string>
            {
                [0] = "Ryu",     [1] = "E. Honda", [2] = "Blanka",  [3] = "Guile",
                [4] = "Ken",     [5] = "Chun Li",  [6] = "Zangief", [7] = "Dhalsim",
                [8] = "M. Bison", [9] = "Sagat",   [10] = "Balrog", [11] = "Vega",
            },

            // The sf2ce twelve keep their ids, and the four Super newcomers
            // follow - which is its own confirmation: an independent walk on a
            // different board landed on the same numbers for the same people.
            //
            // Akuma is almost certainly 16 but nobody has picked him on a
            // recording, so he is not here. The "old" versions (old Sagat and
            // so on) share the id of the new one; telling them apart would need
            // one more recording to find the flag byte, and for now they count
            // as the same character, which is what was asked for.
            ["ssf2t"] = new Dictionary<int, string>
            {
                [0] = "Ryu",     [1] = "E. Honda", [2] = "Blanka",  [3] = "Guile",
                [4] = "Ken",     [5] = "Chun Li",  [6] = "Zangief", [7] = "Dhalsim",
                [8] = "M. Bison", [9] = "Sagat",   [10] = "Balrog", [11] = "Vega",
                [12] = "Cammy",  [13] = "T. Hawk", [14] = "Fei Long", [15] = "Dee Jay",
            },

            // Incomplete. 19 and 20 came from the order a CPU team entered the
            // fight, which is weaker evidence than a human picking them.
            ["kof98"] = new Dictionary<int, string>
            {
                [0] = "Kyo", [1] = "Benimaru", [2] = "Daimon",
                [18] = "Kim", [19] = "Choi", [20] = "Chang",
                [27] = "Iori", [28] = "Mature", [29] = "Vice",
            },

            // Incomplete, and P2 cannot be read at all yet - see PENDENCIAS.md.
            ["vsav"] = new Dictionary<int, string>
            {
                [22] = "L. Raptor", [36] = "Jedah",
            },
        };

        /// <summary>The name, or null when this id is not known for this game.
        /// Null rather than a made-up label: a caller that wants to show the
        /// raw number should be able to tell that is all there is.</summary>
        public static string Name(string game, int id)
        {
            if (game != null && Table.TryGetValue(game, out var roster) &&
                roster.TryGetValue(id, out var name)) return name;
            return null;
        }

        /// <summary>The stable string Django keys its matchup stats on:
        /// lowercase, dots dropped, spaces to underscores - "ryu", "e_honda",
        /// "m_bison", "chun_li". An id we cannot name yet goes as its number
        /// ("17"), which is not a placeholder to be fixed later: it is the same
        /// value the emulator read, so the row stays correct and only its label
        /// improves when the roster fills in.
        ///
        /// The full mapping is printed in net/README.md - Django needs the same
        /// list, and a convention that lives in one head is a convention that
        /// drifts.</summary>
        public static string Code(string game, int id)
        {
            string name = Name(game, id);
            if (name == null) return id.ToString();

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name.ToLowerInvariant())
            {
                if (c == '.') continue;
                sb.Append(c == ' ' || c == '/' || c == '-' ? '_' : c);
            }
            // "e. honda" leaves a double underscore behind; collapse it.
            return sb.ToString().Replace("__", "_").Trim('_');
        }

        /// <summary>"Ryu" for one, "Kyo/Benimaru/Daimon" for a team, and the
        /// bare id where the name is unknown: "Ryu/27/Vice".</summary>
        public static string Describe(string game, IEnumerable<int> ids)
        {
            if (ids == null) return "?";
            var parts = new List<string>();
            foreach (var id in ids) parts.Add(Name(game, id) ?? id.ToString());
            return parts.Count == 0 ? "?" : string.Join("/", parts);
        }
    }
}

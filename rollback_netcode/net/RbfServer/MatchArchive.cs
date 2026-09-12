using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Rbf.Protocol;

namespace Rbf.Server
{
    /// <summary>Where a finished session lands until there is a database.
    ///
    /// One JSON object per line, appended and flushed as each match finishes.
    /// The format is chosen for what comes next, not for what is easy now: a
    /// line is a complete record that needs nothing else to be understood, so
    /// importing the file into Postgres later is a loop over lines, and losing
    /// the tail of the file to a crash costs the last match instead of all of
    /// them.
    ///
    /// Why a file at all, when the plan is a database: the lobby lives on the
    /// same small machine as everything else, and a server that cannot write
    /// its results because Postgres is restarting should keep taking matches,
    /// not stop. When the database arrives this becomes its input, and the
    /// file stays as the thing you read when the import looks wrong.
    ///
    /// Character NAMES are written next to the ids as a convenience for
    /// reading; the id is the record. A roster filled in later fixes old lines
    /// by being applied at read time - never by rewriting what was stored.</summary>
    internal static class MatchArchive
    {
        private static readonly object _gate = new object();
        private static string _path;
        private static bool _complained;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = false,
            // A username with an accent in it should be readable in the file.
            // ç is valid JSON and unreadable to a person.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Null or empty turns archiving off - the console line still
        /// gets printed, nothing is written.</summary>
        public static void Open(string path)
        {
            lock (_gate)
            {
                _path = string.IsNullOrWhiteSpace(path) ? null : path;
                if (_path == null) return;

                try
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(_path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    // Touch it now, so a permission problem shows up at startup
                    // rather than after somebody has played a session.
                    using (File.Open(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                    Console.WriteLine($"  resultados -> {Path.GetFullPath(_path)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"! nao consigo escrever em {_path}: {ex.Message}");
                    Console.WriteLine("  os resultados vao so para o console");
                    _path = null;
                }
            }
        }

        public static void Write(Match m, MatchResult r, string reporterId)
        {
            if (m == null || r == null) return;

            // From the match, not from the session list: by the time a result
            // is archived the player may well have closed the launcher, and a
            // record that says "?" because somebody went to bed is no record.
            Func<string, string> nameOf = id =>
                id == m.P1Id ? m.P1Name : id == m.P2Id ? m.P2Name : null;

            var record = new Dictionary<string, object>
            {
                ["v"]         = 1,
                ["match_id"]  = m.Id,
                ["game"]      = r.Game ?? m.Game,
                ["started"]   = m.StartedUtc.ToString("o"),
                ["ended"]     = DateTime.UtcNow.ToString("o"),
                ["first_to"]  = r.FirstTo,
                ["reason"]    = r.Reason ?? "",
                ["frame_delay"] = m.FrameDelay,
                ["p1"]        = new Dictionary<string, object> { ["user_id"] = m.P1Id, ["username"] = m.P1Name },
                ["p2"]        = new Dictionary<string, object> { ["user_id"] = m.P2Id, ["username"] = m.P2Name },
                ["p1_games"]  = r.P1Games,
                ["p2_games"]  = r.P2Games,
                ["games"]     = r.Games,
                ["winner"]    = r.P1Games > r.P2Games ? 1 : r.P2Games > r.P1Games ? 2 : 0,
                ["games_played"]    = r.GamesPlayed.Select(g => Row(r.Game ?? m.Game, g)).ToList(),
                ["games_truncated"] = r.GamesTruncated,
                // Whose emulator read it. Part of the record because a result is
                // one machine's reading, not a fact from nowhere.
                ["reported_by"] = nameOf(reporterId) ?? "",
            };

            // A game that was cut off mid-way is not a result, but the characters
            // are worth keeping: it is how an interrupted session says what was
            // being played when it stopped.
            if (r.P1Chars.Count > 0 || r.P2Chars.Count > 0)
            {
                record["interrupted"] = new Dictionary<string, object>
                {
                    ["p1_chars"] = r.P1Chars.ToList(),
                    ["p1_names"] = r.P1Chars.Select(id => Characters.Name(r.Game, id)).ToList(),
                    ["p2_chars"] = r.P2Chars.ToList(),
                    ["p2_names"] = r.P2Chars.Select(id => Characters.Name(r.Game, id)).ToList(),
                };
            }

            string line;
            try { line = JsonSerializer.Serialize(record, Json); }
            catch (Exception ex) { Console.WriteLine("! nao consegui serializar o resultado: " + ex.Message); return; }

            lock (_gate)
            {
                if (_path == null) return;
                try
                {
                    // Append, flush, close. A session ends every few minutes at
                    // most; holding a handle open buys nothing and costs the
                    // guarantee that the file on disk is complete right now.
                    File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
                    _complained = false;
                }
                catch (Exception ex)
                {
                    // Say it once per outage, not once per match.
                    if (!_complained)
                    {
                        Console.WriteLine($"! falhei ao gravar o resultado em {_path}: {ex.Message}");
                        _complained = true;
                    }
                }
            }
        }

        private static Dictionary<string, object> Row(string game, MatchGame g) =>
            new Dictionary<string, object>
            {
                ["index"]    = g.Index,
                ["p1_chars"] = g.P1Chars.ToList(),
                ["p1_names"] = g.P1Chars.Select(id => Characters.Name(game, id)).ToList(),
                ["p2_chars"] = g.P2Chars.ToList(),
                ["p2_names"] = g.P2Chars.Select(id => Characters.Name(game, id)).ToList(),
                ["p1_rounds"] = g.P1Rounds,
                ["p2_rounds"] = g.P2Rounds,
                ["winner"]    = g.Winner,
                ["frames"]    = g.Frames,
                ["seconds"]   = g.Frames > 0 ? (int)Math.Round(g.Frames / 60.0) : 0,
            };
    }
}

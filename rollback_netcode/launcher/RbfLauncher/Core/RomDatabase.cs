using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RbfLauncher.Core
{
    /// <summary>One entry from a rom-database JSON, keyed by set name:
    /// <code>{ "&lt;name&gt;": { "download": "&lt;url&gt;", "require": ["&lt;dep&gt;", ...] } }</code></summary>
    public sealed class RomDbEntry
    {
        public string Key;
        public string Download;
        public string[] Require;
    }

    /// <summary>Optional. Reads any <c>*.json</c> in the <c>json_roms\</c> folder next
    /// to the exe. The launcher ships without one; if the user drops a compatible
    /// file in, the room screen's download button becomes usable. No file, no
    /// download - the app never carries ROM URLs itself.</summary>
    public sealed class RomDatabase
    {
        private readonly Dictionary<string, RomDbEntry> _map;

        private RomDatabase(Dictionary<string, RomDbEntry> map) { _map = map; }

        public static string DbDir => Path.Combine(AppContext.BaseDirectory, "json_roms");

        public bool Loaded => _map.Count > 0;
        public int Count => _map.Count;

        public static RomDatabase Load()
        {
            var map = new Dictionary<string, RomDbEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(DbDir))
                {
                    foreach (var file in Directory.GetFiles(DbDir, "*.json"))
                        MergeFile(file, map);
                }
            }
            catch { }
            return new RomDatabase(map);
        }

        private static void MergeFile(string path, Dictionary<string, RomDbEntry> map)
        {
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                        var e = new RomDbEntry { Key = prop.Name };

                        if (prop.Value.TryGetProperty("download", out var d) && d.ValueKind == JsonValueKind.String)
                            e.Download = d.GetString();

                        if (prop.Value.TryGetProperty("require", out var r) && r.ValueKind == JsonValueKind.Array)
                        {
                            var deps = new List<string>();
                            foreach (var it in r.EnumerateArray())
                                if (it.ValueKind == JsonValueKind.String) deps.Add(it.GetString());
                            e.Require = deps.ToArray();
                        }
                        map[prop.Name] = e;   // later files win on duplicate keys
                    }
                }
            }
            catch { /* skip malformed file */ }
        }

        public bool TryGet(string key, out RomDbEntry entry) => _map.TryGetValue(key, out entry);

        /// <summary>Dependency-first, de-duplicated list of entries that carry a URL:
        /// each <c>require</c> before the set that needs it, the requested set last.</summary>
        public List<RomDbEntry> ResolveChain(string key)
        {
            var outList = new List<RomDbEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Visit(key, seen, outList);
            return outList;
        }

        private void Visit(string key, HashSet<string> seen, List<RomDbEntry> outList)
        {
            if (!seen.Add(key)) return;
            if (!_map.TryGetValue(key, out var e)) return;
            if (e.Require != null)
                foreach (var dep in e.Require) Visit(dep, seen, outList);
            if (!string.IsNullOrEmpty(e.Download)) outList.Add(e);
        }
    }
}

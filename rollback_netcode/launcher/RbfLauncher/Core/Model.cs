using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RbfLauncher.Core
{
    /// <summary>One featured game. <see cref="ShortName"/> is BOTH the FBNeo driver
    /// name and the ROM zip base name (roms/arcade/&lt;ShortName&gt;.zip). Download
    /// URLs are NOT here - they come from the json_roms database at runtime.</summary>
    public sealed class GameInfo
    {
        public string ShortName { get; set; }
        public string Title { get; set; }

        /// <summary>Art base names, standardised on the ROM short name.
        /// Extension is resolved by ArtLoader (png / jpg / ...).
        ///   src\&lt;short&gt;.*      - ~4:3 screenshot for the library grid
        ///   src\&lt;short&gt;box.*   - portrait art for the room screen</summary>
        public string Thumb => ShortName;
        public string Box => ShortName + "box";
    }

    /// <summary>The fixed starter catalogue.</summary>
    public static class GameCatalog
    {
        public static readonly IReadOnlyList<GameInfo> All = new[]
        {
            new GameInfo { ShortName = "vsav",  Title = "Vampire Savior" },
            new GameInfo { ShortName = "kof98", Title = "The King of Fighters '98" },
            new GameInfo { ShortName = "sfa2",  Title = "Street Fighter Alpha 2" },
            new GameInfo { ShortName = "sf2ce", Title = "Street Fighter II': Champion Edition" },
        };
    }

    /// <summary>Persisted next to the exe as rbf-launcher.json.</summary>
    public sealed class AppConfig
    {
        [JsonPropertyName("emulatorPath")]
        public string EmulatorPath { get; set; } = "fbneo.exe";

        [JsonPropertyName("romsDir")]
        public string RomsDir { get; set; } = Path.Combine("roms", "arcade");

        /// <summary>Extra args appended after the game name. FBNeo boots FULLSCREEN
        /// when given only a game name; "-w" forces a window. Other useful values:
        /// "-r 800 x 600 x 32" (fullscreen at that mode).</summary>
        [JsonPropertyName("emulatorArgs")]
        public string EmulatorArgs { get; set; } = "-w";

        [JsonPropertyName("playerName")]
        public string PlayerName { get; set; } = Environment.UserName;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "rbf-launcher.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
            }
            catch { }
            return new AppConfig();
        }

        public void Save()
        {
            try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts)); }
            catch { }
        }

        public string ResolvedEmulatorPath => ResolveAgainstBase(EmulatorPath);
        public string ResolvedRomsDir => ResolveAgainstBase(RomsDir);

        private static string ResolveAgainstBase(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return AppContext.BaseDirectory;
            return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p));
        }
    }
}

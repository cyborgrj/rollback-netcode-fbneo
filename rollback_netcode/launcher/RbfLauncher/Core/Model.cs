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
        /// <summary>Lobby a fresh install points at, so a new build is playable
        /// without visiting Configurações first. Lightsail hands out a new public
        /// address on every stop/start, so this needs editing whenever that
        /// happens - attach a static IP (or a hostname) to stop chasing it.</summary>
        public const string DefaultServerHost = "18.228.40.244";

        [JsonPropertyName("emulatorPath")]
        public string EmulatorPath { get; set; } = "fbneo.exe";

        /// <summary>Where the .zip ROM sets live. Leave EMPTY to track the
        /// emulator automatically: &lt;fbneo.exe folder&gt;\roms\arcade — the exact
        /// place FBNeo itself reads. Set an explicit path only to override.</summary>
        [JsonPropertyName("romsDir")]
        public string RomsDir { get; set; } = "";

        /// <summary>Extra args appended after the game name. FBNeo boots FULLSCREEN
        /// when given only a game name; "-w" forces a window. "-noscan" skips the
        /// startup ROM audit, which we never want (EmulatorService adds it anyway,
        /// but having it here means Configurações shows what is really being run).
        /// Other useful values: "-r 800 x 600 x 32" (fullscreen at that mode).</summary>
        [JsonPropertyName("emulatorArgs")]
        public string EmulatorArgs { get; set; } = "-w -noscan";

        [JsonPropertyName("playerName")]
        public string PlayerName { get; set; } = Environment.UserName;

        [JsonPropertyName("serverHost")]
        public string ServerHost { get; set; } = DefaultServerHost;

        [JsonPropertyName("serverPort")]
        public int ServerPort { get; set; } = 50051;

        /// <summary>Where the account API (Django REST) lives — login, perfil,
        /// ranking. Separate from the lobby on purpose: the lobby is a gRPC
        /// stream on its own port and the two will not always live on the same
        /// machine. Editável em Configurações e em `rbf-launcher.json`, porque
        /// vai apontar para localhost em desenvolvimento e para o domínio em
        /// produção sem recompilar nada.</summary>
        [JsonPropertyName("apiBaseUrl")]
        public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

        public const string DefaultApiBaseUrl = "http://localhost:8000";

        /// <summary>Sem barra no fim, que é como o AuthApi monta as URLs.</summary>
        public string ResolvedApiBaseUrl =>
            string.IsNullOrWhiteSpace(ApiBaseUrl)
                ? DefaultApiBaseUrl : ApiBaseUrl.Trim().TrimEnd('/');

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

        /// <summary>Explicit RomsDir if set; otherwise
        /// &lt;emulator folder&gt;\roms\arcade — where FBNeo reads ROMs when the
        /// launcher spawns it (its working dir is the emulator's folder).</summary>
        public string ResolvedRomsDir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RomsDir))
                    return ResolveAgainstBase(RomsDir);

                string emuDir = Path.GetDirectoryName(ResolvedEmulatorPath);
                if (string.IsNullOrEmpty(emuDir)) emuDir = AppContext.BaseDirectory;
                return Path.Combine(emuDir, "roms", "arcade");
            }
        }

        private static string ResolveAgainstBase(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return AppContext.BaseDirectory;
            return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p));
        }
    }
}

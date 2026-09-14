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
            new GameInfo { ShortName = "vsav",    Title = "Vampire Savior" },
            new GameInfo { ShortName = "kof98",   Title = "The King of Fighters '98" },
            new GameInfo { ShortName = "kof2002", Title = "The King of Fighters 2002" },
            new GameInfo { ShortName = "sfa2",    Title = "Street Fighter Alpha 2" },
            new GameInfo { ShortName = "sf2ce",   Title = "Street Fighter II': Champion Edition" },
            new GameInfo { ShortName = "ssf2t",   Title = "Super Street Fighter II Turbo" },
        };
    }

    /// <summary>Persisted next to the exe as rbf-launcher.json.</summary>
    public sealed class AppConfig
    {
        /// <summary>Lobby a fresh install points at, so a new build is playable
        /// without visiting Configurações first. A name, not the address: the
        /// Lightsail instance has a static IP now (56.126.42.71, 13/09), and
        /// lobby.frameperfect.cc is a DNS-only record pointing at it - if the IP
        /// ever changes, only the Cloudflare record does. Not the bare domain:
        /// that one goes through Cloudflare's proxy, which carries HTTP/HTTPS
        /// only, and the lobby is gRPC on 50051 with UDP for the matches.</summary>
        public const string DefaultServerHost = "lobby.frameperfect.cc";

        /// <summary>Relative to the launcher's folder. The Frame Perfect package
        /// keeps the launcher in launcher\ and the emulator at the root, hence
        /// the "..". An existing rbf-launcher.json keeps whatever it says.</summary>
        [JsonPropertyName("emulatorPath")]
        public string EmulatorPath { get; set; } = @"..\fbneo64d.exe";

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

        /// <summary>O site público (as páginas de perfil) - o frontend React,
        /// que NÃO é o Django: em desenvolvimento é o Vite, na porta padrão
        /// 5173. Quando o site ganhar domínio próprio, basta trocar em
        /// Configurações.</summary>
        [JsonPropertyName("siteBaseUrl")]
        public string SiteBaseUrl { get; set; } = DefaultSiteBaseUrl;

        public const string DefaultSiteBaseUrl = "http://localhost:5173";

        public string ResolvedSiteBaseUrl =>
            string.IsNullOrWhiteSpace(SiteBaseUrl)
                ? DefaultSiteBaseUrl : SiteBaseUrl.Trim().TrimEnd('/');

        /// <summary>{site}/player/{username} - a página completa do jogador.</summary>
        public string PlayerProfileUrl(string username) =>
            ResolvedSiteBaseUrl + "/player/" + Uri.EscapeDataString(username ?? "");

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "rbf-launcher.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
                    cfg.FixKnownMistakes();
                    return cfg;
                }
            }
            catch { }
            return new AppConfig();
        }

        /// <summary>Lobby addresses that look right and cannot work.
        ///
        /// 14/09: a package built before lobby.frameperfect.cc existed had the
        /// lobby at frameperfect.cc - the site's name, which goes through
        /// Cloudflare's proxy, and the proxy carries HTTP/HTTPS only. The API
        /// and Site fields next to it DO take "https://frameperfect.cc", so
        /// typing the same into the lobby is the natural mistake. Both are
        /// corrected on load instead of asking every player to find the field.</summary>
        internal void FixKnownMistakes()
        {
            string h = (ServerHost ?? "").Trim();
            if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) h = h.Substring(8);
            else if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) h = h.Substring(7);
            h = h.TrimEnd('/');

            if (string.Equals(h, "frameperfect.cc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h, "www.frameperfect.cc", StringComparison.OrdinalIgnoreCase))
                h = DefaultServerHost;

            if (h != ServerHost) ServerHost = h;
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

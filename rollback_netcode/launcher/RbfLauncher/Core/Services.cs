using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RbfLauncher.Core
{
    /// <summary>ROM presence + (optional) download driven by <see cref="RomDatabase"/>.</summary>
    public sealed class RomService
    {
        private readonly AppConfig _cfg;
        private readonly RomDatabase _db;

        public RomService(AppConfig cfg, RomDatabase db) { _cfg = cfg; _db = db; }

        public bool DatabaseLoaded => _db.Loaded;
        public int DatabaseCount => _db.Count;

        public string RomPath(string key) => Path.Combine(_cfg.ResolvedRomsDir, key + ".zip");
        public string RomPath(GameInfo g) => RomPath(g.ShortName);

        public bool RomExists(GameInfo g) => File.Exists(RomPath(g));

        public long RomSize(GameInfo g)
        {
            try { return RomExists(g) ? new FileInfo(RomPath(g)).Length : 0; }
            catch { return 0; }
        }

        /// <summary>True when the database knows this set and it has a URL.</summary>
        public bool CanDownload(GameInfo g) =>
            _db.TryGet(g.ShortName, out var e) && !string.IsNullOrEmpty(e.Download);

        /// <summary>Sets from the resolved chain (deps + game) that are not on disk yet.</summary>
        public List<RomDbEntry> MissingSets(GameInfo g)
        {
            var missing = new List<RomDbEntry>();
            foreach (var e in _db.ResolveChain(g.ShortName))
                if (!File.Exists(RomPath(e.Key))) missing.Add(e);
            return missing;
        }

        /// <summary>Downloads every missing set in the game's chain (dependencies
        /// first). Each file goes to &lt;name&gt;.zip.part then is moved into place.</summary>
        public async Task DownloadAsync(GameInfo g, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            var chain = _db.ResolveChain(g.ShortName);
            if (chain.Count == 0)
                throw new InvalidOperationException(
                    "Nenhuma entrada para \"" + g.ShortName + "\" no banco de ROMs (json_roms).");

            Directory.CreateDirectory(_cfg.ResolvedRomsDir);

            var todo = new List<RomDbEntry>();
            foreach (var e in chain)
                if (!File.Exists(RomPath(e.Key))) todo.Add(e);

            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await DownloadOne(todo[i], i + 1, todo.Count, progress, ct).ConfigureAwait(false);
            }
        }

        private async Task DownloadOne(RomDbEntry e, int index, int count,
                                       IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            string finalPath = RomPath(e.Key);
            string tmpPath = finalPath + ".part";

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            using (var resp = await http.GetAsync(e.Download, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;

                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        done += n;
                        progress?.Report(new DownloadProgress(e.Key, index, count, done, total));
                    }
                }
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmpPath, finalPath);
        }
    }

    public readonly struct DownloadProgress
    {
        public DownloadProgress(string file, int index, int count, long received, long? total)
        {
            File = file; Index = index; Count = count; Received = received; Total = total;
        }

        public string File { get; }
        public int Index { get; }
        public int Count { get; }
        public long Received { get; }
        public long? Total { get; }

        public double? Fraction =>
            Total.HasValue && Total.Value > 0 ? (double?)Received / Total.Value : null;
    }

    /// <summary>Spawns the patched fbneo.exe on a chosen game.</summary>
    public sealed class EmulatorService
    {
        private readonly AppConfig _cfg;
        public EmulatorService(AppConfig cfg) { _cfg = cfg; }

        public bool EmulatorExists => System.IO.File.Exists(_cfg.ResolvedEmulatorPath);

        /// <summary>Boot straight into a game: <c>fbneo.exe &lt;short&gt; [args] [extraArgs]</c>.
        /// FBNeo reads roms from &lt;workdir&gt;/roms/arcade, so it runs from its own
        /// folder. <paramref name="extraArgs"/> carries the <c>-rbfnet …</c> session
        /// string for a networked match.</summary>
        public Process Launch(GameInfo g, string extraArgs = null)
        {
            string exe = _cfg.ResolvedEmulatorPath;
            if (!System.IO.File.Exists(exe))
                throw new FileNotFoundException("Emulador não encontrado. Ajuste o caminho em Configurações.", exe);

            string args = g.ShortName;
            if (!string.IsNullOrWhiteSpace(_cfg.EmulatorArgs))
                args += " " + _cfg.EmulatorArgs.Trim();
            if (!string.IsNullOrWhiteSpace(extraArgs))
                args += " " + extraArgs.Trim();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,                       // e.g. "sfa2 -w"  (-w = windowed; FBNeo defaults to fullscreen)
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
            };
            return Process.Start(psi);
        }
    }
}

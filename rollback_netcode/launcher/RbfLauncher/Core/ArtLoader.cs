using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace RbfLauncher.Core
{
    /// <summary>Loads game art from the project-wide <c>src\</c> folder (copied
    /// next to the exe at build time). Accepts either a full file name
    /// ("vampire.png") or a base name ("vsavbox") - in the latter case it probes
    /// the common image extensions. Returns null when nothing matches so the UI
    /// can show a placeholder.</summary>
    public static class ArtLoader
    {
        private static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

        private static readonly Dictionary<string, BitmapImage> Cache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        public static string ArtDir => Path.Combine(AppContext.BaseDirectory, "src");

        public static BitmapImage Load(string nameOrBase)
        {
            if (string.IsNullOrWhiteSpace(nameOrBase)) return null;
            if (Cache.TryGetValue(nameOrBase, out var cached)) return cached;

            string path = Resolve(nameOrBase);
            BitmapImage bmp = path == null ? null : Decode(path);
            Cache[nameOrBase] = bmp;
            return bmp;
        }

        private static string Resolve(string nameOrBase)
        {
            if (Path.HasExtension(nameOrBase))
            {
                string direct = Path.Combine(ArtDir, nameOrBase);
                if (File.Exists(direct)) return direct;
            }

            string baseName = Path.HasExtension(nameOrBase)
                ? Path.GetFileNameWithoutExtension(nameOrBase)
                : nameOrBase;

            foreach (var ext in Exts)
            {
                string p = Path.Combine(ArtDir, baseName + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static BitmapImage Decode(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;      // don't lock the file
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}

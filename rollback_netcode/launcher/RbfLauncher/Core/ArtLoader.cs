using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace RbfLauncher.Core
{
    /// <summary>Loads game art from the <c>src\</c> folder next to the exe.
    /// Returns null when the file is missing so the UI can show a placeholder.</summary>
    public static class ArtLoader
    {
        private static readonly Dictionary<string, BitmapImage> Cache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        public static string ArtDir => Path.Combine(AppContext.BaseDirectory, "src");

        public static BitmapImage Load(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (Cache.TryGetValue(fileName, out var cached)) return cached;

            string path = Path.Combine(ArtDir, fileName);
            if (!File.Exists(path)) { Cache[fileName] = null; return null; }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;      // don't lock the file
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                Cache[fileName] = bmp;
                return bmp;
            }
            catch
            {
                Cache[fileName] = null;
                return null;
            }
        }
    }
}

using System;
using System.IO;
using System.Text;

namespace RbfLauncher.Core
{
    /// <summary>rbf-launcher.log, next to the exe - the launcher's side of what
    /// rbf-netplay.log is for the emulator.
    ///
    /// A dialog shows the player one sentence; this keeps the whole exception
    /// (type, inner exceptions, stack), so a failure on somebody else's machine
    /// can be diagnosed from the file instead of from a remembered message.
    /// Opened per line and never throws: logging must not be the next failure.</summary>
    public static class LauncherLog
    {
        private static readonly object Gate = new object();

        /// <summary>Past this size the file starts over, so it cannot grow for
        /// months on a machine nobody looks at.</summary>
        private const long MaxBytes = 1024 * 1024;

        public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "rbf-launcher.log");

        public static void Info(string message) => Write("INFO", message, null);

        public static void Error(string message, Exception ex = null) => Write("ERRO", message, ex);

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ")
                  .Append(level).Append("  ").Append(message).AppendLine();
                if (ex != null) sb.Append(ex).AppendLine();   // ToString carries inner exceptions and stack

                lock (Gate)
                {
                    var fi = new FileInfo(Path);
                    if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
                    File.AppendAllText(Path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { /* never the reason something else failed */ }
        }
    }
}

using System;
using System.IO;
using System.Reflection;

namespace RbfLauncher.Core
{
    /// <summary>"Frame Perfect.lnk" pointing at this launcher: at the root of
    /// the program folder, and on the desktop.
    ///
    /// A shortcut cannot simply ship inside the zip: a .lnk stores an absolute
    /// path, and nobody extracts to the folder the zip was made in. The package's
    /// "Abrir Frame Perfect.cmd" writes both on first use; the launcher keeps them
    /// right from then on, because a player who moves the whole folder leaves
    /// both pointing at a folder that is gone.
    ///
    /// The root one is always (re)written. The desktop one is only corrected,
    /// never created: a player who deleted it from the desktop meant it.
    ///
    /// Only in the packaged layout (launcher\ next to fbneo64d.exe), so a
    /// development build never litters its output folder's parent.</summary>
    public static class Shortcuts
    {
        public const string Name = "Frame Perfect.lnk";

        public static void EnsureRootShortcut()
        {
            try
            {
                string exe = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(exe)) return;

                string launcherDir = Path.GetDirectoryName(exe);
                string root = Path.GetDirectoryName(launcherDir);
                if (string.IsNullOrEmpty(root)) return;
                if (!File.Exists(Path.Combine(root, "fbneo64d.exe"))) return;

                Write(Path.Combine(root, Name), exe, launcherDir, onlyIfExists: false);

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop))
                    Write(Path.Combine(desktop, Name), exe, launcherDir, onlyIfExists: true);
            }
            catch (Exception ex)
            {
                // A shortcut is a convenience; failing to write one must not stop
                // anybody from playing.
                LauncherLog.Error("nao consegui criar/atualizar o atalho do Frame Perfect", ex);
            }
        }

        private static void Write(string lnk, string exe, string workDir, bool onlyIfExists)
        {
            bool exists = File.Exists(lnk);
            if (onlyIfExists && !exists) return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);

            // CreateShortcut opens an existing one too, so the same object says
            // whether it already points here.
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var scType = sc.GetType();
            string current = scType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
            if (exists && string.Equals(current, exe, StringComparison.OrdinalIgnoreCase)) return;

            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Frame Perfect" });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);

            LauncherLog.Info((exists ? "atalho corrigido: " : "atalho criado: ") + lnk);
        }
    }
}

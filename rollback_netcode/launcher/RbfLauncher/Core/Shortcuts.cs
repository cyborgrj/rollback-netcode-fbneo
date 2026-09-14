using System;
using System.IO;
using System.Reflection;

namespace RbfLauncher.Core
{
    /// <summary>"Frame Perfect.lnk" at the root of the program folder, pointing
    /// at this launcher.
    ///
    /// A shortcut cannot simply ship inside the zip: a .lnk stores an absolute
    /// path, and nobody extracts to the same folder the zip was made in. So the
    /// launcher writes it itself, with the path it is actually running from -
    /// and rewrites it whenever that path changes, which is what happens when a
    /// player moves the whole folder somewhere else.
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

                string lnk = Path.Combine(root, Name);
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);

                // CreateShortcut opens an existing one too, so the same object
                // says whether it already points here.
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var scType = sc.GetType();
                string current = scType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                if (File.Exists(lnk) && string.Equals(current, exe, StringComparison.OrdinalIgnoreCase)) return;

                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { launcherDir });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Frame Perfect" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);

                LauncherLog.Info("atalho criado: " + lnk);
            }
            catch (Exception ex)
            {
                // A shortcut is a convenience; failing to write one must not stop
                // anybody from playing.
                LauncherLog.Error("nao consegui criar o atalho na pasta do programa", ex);
            }
        }
    }
}

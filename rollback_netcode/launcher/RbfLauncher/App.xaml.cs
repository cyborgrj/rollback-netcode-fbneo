using System;
using System.Net;
using System.Windows;
using System.Windows.Threading;

namespace RbfLauncher
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Older Windows (7 / early 10) default to TLS 1.0 for .NET Framework,
            // which HTTPS mirrors reject. Opt into modern TLS for the downloader.
            try
            {
                ServicePointManager.SecurityProtocol |=
                    SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* Tls13 */;
            }
            catch { /* value not supported on this OS - system default stays */ }

            DispatcherUnhandledException += OnUnhandled;
            // Background threads (the lobby read loop, downloads) do not go
            // through the dispatcher; without this their crash leaves no trace.
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
                Core.LauncherLog.Error("erro nao tratado fora da interface", ev.ExceptionObject as Exception);

            Core.LauncherLog.Info("launcher iniciado, " + Core.AppVersion.Label);

            // "Frame Perfect.lnk" next to fbneo64d.exe, pointing here - see Shortcuts.
            Core.Shortcuts.EnsureShortcuts();

            // The login window is a dialog shown before any main window exists,
            // so the default "quit when the last window closes" would end the
            // process the moment it is dismissed - including on a successful
            // login, in the gap before the main window opens.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            ShowLogin();
        }

        /// <summary>Login first, library second. Called again by "Sair da conta",
        /// which is why it is a method and not a few lines in OnStartup.</summary>
        internal void ShowLogin()
        {
            var cfg = Core.AppConfig.Load();
            var login = new Views.LoginWindow(cfg);

            if (login.ShowDialog() != true) { Shutdown(); return; }

            var main = new MainWindow(login.Session);
            MainWindow = main;
            main.Show();
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Core.LauncherLog.Error("erro nao tratado", e.Exception);
            MessageBox.Show(e.Exception.Message + "\n\nDetalhes em rbf-launcher.log, ao lado do launcher.",
                            "Frame Perfect - erro",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}

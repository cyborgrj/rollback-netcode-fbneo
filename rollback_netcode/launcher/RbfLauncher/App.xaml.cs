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
            MessageBox.Show(e.Exception.Message, "RBF Launcher - erro",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}

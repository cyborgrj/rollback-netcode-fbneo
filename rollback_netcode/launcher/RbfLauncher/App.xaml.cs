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
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.Message, "RBF Launcher - erro",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}

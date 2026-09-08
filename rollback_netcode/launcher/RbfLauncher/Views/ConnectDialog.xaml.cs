using System.Windows;
using RbfLauncher.Core;

namespace RbfLauncher.Views
{
    public partial class ConnectDialog : Window
    {
        public string Host { get; private set; }
        public int Port { get; private set; }
        public string PlayerName { get; private set; }

        public ConnectDialog(AppConfig cfg)
        {
            InitializeComponent();
            UserBox.Text = cfg.PlayerName;
            HostBox.Text = cfg.ServerHost;
            PortBox.Text = cfg.ServerPort.ToString();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string name = UserBox.Text?.Trim() ?? "";
            string host = HostBox.Text?.Trim() ?? "";
            if (name.Length < 1) { ErrorText.Text = "Informe um nome."; return; }
            if (host.Length < 1) { ErrorText.Text = "Informe o servidor."; return; }
            if (!int.TryParse(PortBox.Text?.Trim(), out int port) || port < 1 || port > 65535)
            {
                ErrorText.Text = "Porta inválida.";
                return;
            }
            Host = host;
            Port = port;
            PlayerName = name;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}

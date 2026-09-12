using System;
using System.Windows;
using System.Windows.Input;
using RbfLauncher.Core;

namespace RbfLauncher.Views
{
    /// <summary>The door. The launcher opens here, not on the game library:
    /// the account decides who you are in the lobby, so there is nothing
    /// sensible to show before it.
    ///
    /// DialogResult true means <see cref="Session"/> is filled in and
    /// <see cref="UserSession.Current"/> is set.</summary>
    public partial class LoginWindow : Window
    {
        private readonly AppConfig _config;
        private bool _busy;

        public UserSession Session { get; private set; }

        public LoginWindow(AppConfig config)
        {
            InitializeComponent();
            _config = config;

            UserBox.Text = _config.PlayerName ?? "";
            ApiBox.Text  = _config.ResolvedApiBaseUrl;
            HostBox.Text = _config.ServerHost;
            PortBox.Text = _config.ServerPort.ToString();

            // Straight to the password when the name is already remembered.
            Loaded += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(UserBox.Text)) UserBox.Focus();
                else PassBox.Focus();
            };
        }

        private void Field_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Login_Click(sender, null);
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            string user = UserBox.Text?.Trim() ?? "";
            string pass = PassBox.Password ?? "";
            string api  = ApiBox.Text?.Trim() ?? "";

            if (user.Length == 0) { Fail("Informe o usuário."); UserBox.Focus(); return; }
            if (pass.Length == 0) { Fail("Informe a senha.");   PassBox.Focus(); return; }
            if (api.Length  == 0) { Fail("Informe o endereço da API em Servidores."); return; }

            if (!int.TryParse(PortBox.Text?.Trim(), out int port) || port < 1 || port > 65535)
            {
                Fail("Porta do lobby inválida.");
                ServerExpander.IsExpanded = true;
                return;
            }

            SetBusy(true);
            try
            {
                using (var auth = new AuthApi(api))
                {
                    Session = await auth.LoginAsync(user, pass);
                }

                UserSession.SetCurrent(Session);

                // Only now. Saving the typed addresses before the login works
                // would make a wrong one stick for the next launch too.
                _config.PlayerName = Session.Username;
                _config.ApiBaseUrl = api;
                _config.ServerHost = HostBox.Text?.Trim() ?? _config.ServerHost;
                _config.ServerPort = port;
                _config.Save();

                DialogResult = true;
            }
            catch (AuthException ex)
            {
                Fail(ex.Message);
                if (ex.BadCredentials) { PassBox.Clear(); PassBox.Focus(); }
                else ServerExpander.IsExpanded = true;
            }
            catch (Exception ex)
            {
                Fail("Falha inesperada no login: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void Fail(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            LoginButton.IsEnabled = !busy;
            LoginButton.Content = busy ? "Entrando…" : "Entrar";
            UserBox.IsEnabled = PassBox.IsEnabled = !busy;
            if (busy) ErrorText.Visibility = Visibility.Collapsed;
        }
    }
}

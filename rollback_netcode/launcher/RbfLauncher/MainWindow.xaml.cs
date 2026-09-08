using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Rbf.Protocol;
using RbfLauncher.Core;
using RbfLauncher.Net;
using RbfLauncher.Views;

namespace RbfLauncher
{
    public partial class MainWindow : Window
    {
        private readonly AppConfig _config;
        private readonly RomService _roms;
        private readonly EmulatorService _emu;

        private LobbyClient _client;
        private IReadOnlyList<RosterEntry> _roster = new List<RosterEntry>();
        private ChallengeDialog _openChallenge;
        private MatchReadyDialog _openMatch;
        private string _currentGame;   // room the RoomView is showing, null in the library

        public MainWindow()
        {
            InitializeComponent();

            _config = AppConfig.Load();
            _roms = new RomService(_config, RomDatabase.Load());
            _emu = new EmulatorService(_config);

            UpdateHeader();
            ShowLibrary();
        }

        // ---- navigation ---------------------------------------------------
        public void ShowLibrary()
        {
            _currentGame = null;
            if (_client != null && _client.LoggedIn) _client.LeaveRoom();
            Host.Content = new LibraryView(_roms, ShowRoom);
        }

        public void ShowRoom(GameInfo game)
        {
            _currentGame = game.ShortName;
            var view = new RoomView(game, _config, _roms, _emu, ShowLibrary, _client);
            Host.Content = view;
            if (_client != null && _client.LoggedIn)
            {
                _client.JoinRoom(game.ShortName);
                view.SetRoster(_roster);
            }
        }

        private RoomView CurrentRoom => Host.Content as RoomView;

        // ---- connection --------------------------------------------------
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null && _client.LoggedIn)
            {
                Disconnect("desconectado");
                return;
            }

            var dlg = new ConnectDialog(_config) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            _config.PlayerName = dlg.PlayerName;
            _config.ServerHost = dlg.Host;
            _config.ServerPort = dlg.Port;
            _config.Save();

            var client = new LobbyClient();
            WireClient(client);

            ConnectButton.IsEnabled = false;
            PlayerLabel.Text = "conectando…";
            try
            {
                await client.ConnectAsync(dlg.Host, dlg.Port, dlg.PlayerName, TimeSpan.FromSeconds(8));
                _client = client;
            }
            catch (Exception ex)
            {
                client.Dispose();
                MessageBox.Show("Não foi possível conectar:\n" + ex.Message, "RBF",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateHeader();
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        private void WireClient(LobbyClient c)
        {
            c.LoginOk += _ =>
            {
                UpdateHeader();
                if (_currentGame != null) _client?.JoinRoom(_currentGame);
            };
            c.LoginRejected += reason =>
            {
                MessageBox.Show("Login recusado: " + reason, "RBF", MessageBoxButton.OK, MessageBoxImage.Warning);
                Disconnect("login recusado");
            };
            c.RosterUpdated += list =>
            {
                _roster = list;
                CurrentRoom?.SetRoster(_roster);
            };
            c.ChallengeReceived += OnChallengeIn;
            c.ChallengeResolved += OnChallengeResult;
            c.MatchStarting += OnMatchStart;
            c.MatchAborted += ab =>
            {
                CloseTransientDialogs();
                MessageBox.Show("Partida cancelada: " + ab.Reason, "RBF",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            };
            c.ServerError += msg =>
                MessageBox.Show(msg, "RBF", MessageBoxButton.OK, MessageBoxImage.Warning);
            c.Disconnected += reason => Disconnect(reason);
        }

        private void Disconnect(string reason)
        {
            CloseTransientDialogs();
            _client?.Dispose();
            _client = null;
            _roster = new List<RosterEntry>();
            CurrentRoom?.SetRoster(_roster);
            CurrentRoom?.SetClient(null);
            UpdateHeader();
            if (!string.IsNullOrEmpty(reason) && reason != "desconectado")
                PlayerLabel.Text = "offline (" + reason + ")";
        }

        private void UpdateHeader()
        {
            bool on = _client != null && _client.LoggedIn;
            PlayerLabel.Text = on ? "● " + _client.Username : _config.PlayerName + " · offline";
            ConnectButton.Content = on ? "Desconectar" : "Conectar";
        }

        // ---- challenge / match flow ------------------------------------
        private void OnChallengeIn(ChallengeIn ci)
        {
            _openChallenge?.ForceClose();
            var title = GameCatalog.All.FirstOrDefault(g => g.ShortName == ci.Game)?.Title ?? ci.Game;
            var dlg = new ChallengeDialog(ci.FromUsername, title) { Owner = this };
            _openChallenge = dlg;
            bool? ok = dlg.ShowDialog();
            _openChallenge = null;
            _client?.ReplyChallenge(ci.ChallengeId, ok == true);
        }

        private void OnChallengeResult(ChallengeResult cr)
        {
            if (cr.Outcome == Outcome.OutcomeAccepted) return;   // MatchStart follows
            string why = cr.Outcome switch
            {
                Outcome.OutcomeDeclined => "recusou o desafio",
                Outcome.OutcomeExpired => "não respondeu a tempo",
                Outcome.OutcomeCancelled => "desafio cancelado",
                _ => "desafio encerrado"
            };
            var who = string.IsNullOrEmpty(cr.PeerUsername) ? "O jogador" : cr.PeerUsername;
            MessageBox.Show(who + " " + why + ".", "RBF", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnMatchStart(MatchStart ms)
        {
            CloseTransientDialogs();
            var dlg = new MatchReadyDialog(ms, _emu, _client) { Owner = this };
            _openMatch = dlg;
            dlg.ShowDialog();
            _openMatch = null;
        }

        private void CloseTransientDialogs()
        {
            _openChallenge?.ForceClose();
            _openMatch?.ForceClose();
        }

        // ---- settings --------------------------------------------------
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_config) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _config.Save();
                UpdateHeader();
                var game = _currentGame;
                if (game == null) ShowLibrary();
                else ShowRoom(GameCatalog.All.First(g => g.ShortName == game));
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _client?.Dispose();
            base.OnClosed(e);
        }
    }
}

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
        private IReadOnlyList<MatchEntry> _matches = new List<MatchEntry>();
        private bool _chatOpen = true;   // sidebar shown whenever connected
        private DelayDialog _openChallenge;
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
            Chat.SetRoom(null);
            Host.Content = new LibraryView(_roms, ShowRoom);
        }

        public void ShowRoom(GameInfo game)
        {
            _currentGame = game.ShortName;
            var view = new RoomView(game, _config, _roms, _emu, ShowLibrary, _client);
            Host.Content = view;
            Chat.SetRoom(_client != null && _client.LoggedIn ? game.ShortName : null);
            if (_client != null && _client.LoggedIn)
            {
                _client.JoinRoom(game.ShortName);
                view.SetRoster(_roster);
                view.SetMatches(_matches);
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
                Chat.SetClient(c);
                Chat.SetRoom(_currentGame);
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
            c.MatchesUpdated += list =>
            {
                _matches = list;
                CurrentRoom?.SetMatches(_matches);
            };
            c.ChatReceived += m => Chat.OnChat(m);
            c.ChatLogReceived += l => Chat.OnChatLog(l);
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
            _matches = new List<MatchEntry>();
            Chat.SetClient(null);
            Chat.SetRoom(null);
            CurrentRoom?.SetRoster(_roster);
            CurrentRoom?.SetMatches(_matches);
            CurrentRoom?.SetClient(null);
            UpdateHeader();
            if (!string.IsNullOrEmpty(reason) && reason != "desconectado")
                PlayerLabel.Text = "offline (" + reason + ")";
        }

        private void UpdateHeader()
        {
            bool on = _client != null && _client.LoggedIn;
            PlayerLabel.Text = on ? "● " + _client.Username : _config.PlayerName + " · offline";
            PlayerLabel.ToolTip = on && !string.IsNullOrEmpty(_client.LanIp)
                ? "meu IP na rede: " + _client.LanIp
                : null;
            ConnectButton.Content = on ? "Desconectar" : "Conectar";
            ChatToggle.IsEnabled = on;
            Chat.Visibility = on && _chatOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChatToggle_Click(object sender, RoutedEventArgs e)
        {
            _chatOpen = !_chatOpen;
            UpdateHeader();
        }

        // ---- challenge / match flow ------------------------------------
        private void OnChallengeIn(ChallengeIn ci)
        {
            _openChallenge?.ForceClose();
            var title = GameCatalog.All.FirstOrDefault(g => g.ShortName == ci.Game)?.Title ?? ci.Game;
            // Same slider the challenger saw, pre-set to what they asked for.
            // The server averages both answers into the match\x27s frame delay.
            var dlg = DelayDialog.ForIncoming(ci.FromUsername, title,
                                              ci.SuggestedDelay, ci.FromFrameDelay, 25, this);
            _openChallenge = dlg;
            bool? ok = dlg.ShowDialog();
            _openChallenge = null;
            _client?.ReplyChallenge(ci.ChallengeId, ok == true, dlg.FrameDelay);
        }

        private void OnChallengeResult(ChallengeResult cr)
        {
            if (cr.Outcome == Outcome.Accepted) return;   // MatchStart follows
            string why = cr.Outcome switch
            {
                Outcome.Declined => "recusou o desafio",
                Outcome.Expired => "não respondeu a tempo",
                Outcome.Cancelled => "desafio cancelado",
                _ => "desafio encerrado"
            };
            var who = string.IsNullOrEmpty(cr.PeerUsername) ? "O jogador" : cr.PeerUsername;
            MessageBox.Show(who + " " + why + ".", "RBF", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // A challenge that both sides committed to (Desafiar / Aceitar) launches
        // the emulator immediately on both machines - no extra confirmation, so
        // the two starts stay close together.
        private void OnMatchStart(MatchStart ms)
        {
            CloseTransientDialogs();

            var game = GameCatalog.All.FirstOrDefault(g => g.ShortName == ms.Game)
                       ?? new GameInfo { ShortName = ms.Game, Title = ms.Game };

            if (!_emu.EmulatorExists)
            {
                _client?.ReportMatch(ms.MatchId, Phase.Failed, "fbneo.exe não encontrado");
                MessageBox.Show("Partida vs " + ms.PeerUsername +
                                ": fbneo.exe não encontrado. Ajuste em Configurações.",
                                "RBF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string args = string.Format(
                "-rbfnet player={0},localport={1},peerip={2},peerport={3},delay={4}",
                ms.PlayerNum, ms.LocalPort, ms.PeerIp, ms.PeerPort, ms.FrameDelay);

            // NAT hole punching: the emulator announces itself at the lobby host on
            // this UDP port, from the same port libggpo binds, and gets the peer's
            // public endpoint back. Without it we keep the server-observed peer_ip,
            // which only works on a LAN or with port forwarding.
            if (ms.PunchPort > 0 && !string.IsNullOrWhiteSpace(_config.ServerHost))
                args += string.Format(",punchip={0},punchport={1},matchid={2}",
                                      _config.ServerHost, ms.PunchPort, ms.MatchId);

            try
            {
                _client?.ReportMatch(ms.MatchId, Phase.Launching);
                var proc = _emu.Launch(game, args);
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (s, e) => Dispatcher.BeginInvoke((Action)(() =>
                        _client?.ReportMatch(ms.MatchId, Phase.Ended)));
                }
                catch { /* process may already have exited */ }
                _client?.ReportMatch(ms.MatchId, Phase.Running);
            }
            catch (Exception ex)
            {
                _client?.ReportMatch(ms.MatchId, Phase.Failed, ex.Message);
                MessageBox.Show("Falha ao abrir o emulador:\n" + ex.Message, "RBF",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseTransientDialogs()
        {
            _openChallenge?.ForceClose();
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

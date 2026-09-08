using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Rbf.Protocol;
using RbfLauncher.Core;
using RbfLauncher.Net;

namespace RbfLauncher.Views
{
    public partial class MatchReadyDialog : Window
    {
        private readonly MatchStart _match;
        private readonly EmulatorService _emu;
        private readonly LobbyClient _client;
        private readonly GameInfo _game;
        private bool _launched;

        public MatchReadyDialog(MatchStart match, EmulatorService emu, LobbyClient client)
        {
            InitializeComponent();
            _match = match;
            _emu = emu;
            _client = client;
            _game = GameCatalog.All.FirstOrDefault(g => g.ShortName == match.Game)
                    ?? new GameInfo { ShortName = match.Game, Title = match.Game };

            TitleText.Text = _game.Title + " — vs " + match.PeerUsername;
            DetailText.Text = string.Format(
                "Você é o Jogador {0}.  local :{1}   ↔   {2}:{3}   (delay {4})",
                match.PlayerNum, match.LocalPort, match.PeerIp, match.PeerPort, match.FrameDelay);

            PlayButton.IsEnabled = _emu.EmulatorExists;
            if (!_emu.EmulatorExists)
                StatusText.Text = "fbneo.exe não encontrado — ajuste em Configurações.";
        }

        private string RbfNetArgs() => string.Format(
            "-rbfnet player={0},localport={1},peerip={2},peerport={3},delay={4}",
            _match.PlayerNum, _match.LocalPort, _match.PeerIp, _match.PeerPort, _match.FrameDelay);

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_launched) return;
            try
            {
                var proc = _emu.Launch(_game, RbfNetArgs());
                _launched = true;
                _client.ReportMatch(_match.MatchId, Phase.PhaseLaunching);

                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (s, ev) => Dispatcher.BeginInvoke((Action)(() =>
                        _client.ReportMatch(_match.MatchId, Phase.PhaseEnded)));
                }
                catch { /* process may have exited already */ }

                _client.ReportMatch(_match.MatchId, Phase.PhaseRunning);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Falha ao abrir: " + ex.Message;
                _client.ReportMatch(_match.MatchId, Phase.PhaseFailed, ex.Message);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (!_launched)
                _client.ReportMatch(_match.MatchId, Phase.PhaseFailed, "cancelado pelo jogador");
            DialogResult = false;
        }

        public void ForceClose()
        {
            try { if (IsVisible) DialogResult = false; } catch { Close(); }
        }
    }
}

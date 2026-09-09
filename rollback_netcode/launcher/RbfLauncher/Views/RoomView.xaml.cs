using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Rbf.Protocol;
using RbfLauncher.Core;
using RbfLauncher.Net;

namespace RbfLauncher.Views
{
    public partial class RoomView : UserControl
    {
        private readonly GameInfo _game;
        private readonly AppConfig _config;
        private readonly RomService _roms;
        private readonly EmulatorService _emu;
        private readonly Action _back;

        private LobbyClient _client;
        private IReadOnlyList<RosterEntry> _roster = new List<RosterEntry>();
        private IReadOnlyList<MatchEntry> _matches = new List<MatchEntry>();
        private readonly DispatcherTimer _clock;

        private bool _downloading;
        private CancellationTokenSource _cts;

        public RoomView(GameInfo game, AppConfig config, RomService roms, EmulatorService emu, Action back,
                        LobbyClient client)
        {
            InitializeComponent();
            _game = game;
            _config = config;
            _roms = roms;
            _emu = emu;
            _back = back;
            _client = client;

            TitleText.Text = game.Title;

            // portrait <short>box art; fall back to the 4:3 thumb, then a placeholder
            var bmp = ArtLoader.Load(game.Box) ?? ArtLoader.Load(game.Thumb);
            if (bmp != null)
            {
                ArtImage.Source = bmp;
            }
            else
            {
                ArtImage.Visibility = Visibility.Collapsed;
                ArtPlaceholder.Visibility = Visibility.Visible;
                ArtPlaceholder.Text = game.ShortName;
            }

            Refresh();
            RebuildPlayers();

            // The only thing that changes without a server push is how long each
            // match has been running, so redraw that list once a second.
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (s, e) => RebuildMatches();
            Loaded += (s, e) => _clock.Start();
            Unloaded += (s, e) => _clock.Stop();
        }

        // ---- online room -------------------------------------------------
        public void SetClient(LobbyClient client)
        {
            _client = client;
            if (client == null)
            {
                _roster = new List<RosterEntry>();
                _matches = new List<MatchEntry>();
                RebuildMatches();
            }
            RebuildPlayers();
        }

        public void SetRoster(IReadOnlyList<RosterEntry> roster)
        {
            _roster = roster ?? new List<RosterEntry>();
            RebuildPlayers();
        }

        public void SetMatches(IReadOnlyList<MatchEntry> matches)
        {
            _matches = matches ?? new List<MatchEntry>();
            RebuildMatches();
        }

        private void RebuildMatches()
        {
            MatchesList.Items.Clear();

            var here = _matches.Where(m => m.Game == _game.ShortName)
                               .OrderBy(m => m.StartedUtc)
                               .ToList();

            MatchesHeader.Visibility = here.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var m in here) MatchesList.Items.Add(BuildMatchRow(m));
        }

        private UIElement BuildMatchRow(MatchEntry m)
        {
            var vs = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
            vs.Inlines.Add(new System.Windows.Documents.Run(m.P1Username) { FontWeight = FontWeights.SemiBold });
            vs.Inlines.Add(new System.Windows.Documents.Run("   vs   ")
                { Foreground = (Brush)FindResource("TextDim"), FontSize = 12 });
            vs.Inlines.Add(new System.Windows.Documents.Run(m.P2Username) { FontWeight = FontWeights.SemiBold });

            var t = m.Elapsed;
            string extra = m.Watchable && m.Viewers > 0
                           ? string.Format("   ·   {0} assistindo", m.Viewers) : "";
            var info = new TextBlock
            {
                Text = string.Format("{0:D2}:{1:D2}   ·   {2}f{3}",
                                     (int)t.TotalMinutes, t.Seconds, m.FrameDelay, extra),
                Foreground = (Brush)FindResource("TextDim"),
                FontSize = 12,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var row = new DockPanel { LastChildFill = false };

            // Only matches whose host is actually broadcasting can be watched; the
            // rest of the list stays informational.
            if (m.Watchable)
            {
                var watch = new Button
                {
                    Content = "Assistir",
                    Style = (Style)FindResource("Btn"),
                    Padding = new Thickness(12, 4, 12, 4),
                    Margin = new Thickness(10, 0, 0, 0)
                };
                watch.Click += (s, e) => Watch(m);
                DockPanel.SetDock(watch, Dock.Right);
                row.Children.Add(watch);
            }

            DockPanel.SetDock(info, Dock.Right);
            row.Children.Add(info);
            row.Children.Add(vs);
            return new Border
            {
                Background = (Brush)FindResource("BgPanel"),
                BorderBrush = (Brush)FindResource("Stroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = row
            };
        }

        private void RebuildPlayers()
        {
            PlayersList.Items.Clear();

            if (_client == null || !_client.LoggedIn)
            {
                OnlineHint.Text = "Offline. Clique em \"Conectar\" no topo para ver quem está na sala.";
                return;
            }

            var mine = _roster.FirstOrDefault(p => p.UserId == _client.UserId);
            bool iAmFree = mine != null && mine.State == PlayerState.PlayerInRoom;

            var others = _roster
                .Where(p => p.UserId != _client.UserId && p.Game == _game.ShortName)
                .OrderBy(p => p.Username)
                .ToList();

            OnlineHint.Text = others.Count == 0
                ? "Você está na sala. Ninguém mais aqui ainda."
                : others.Count + (others.Count == 1 ? " jogador na sala." : " jogadores na sala.");

            foreach (var p in others)
                PlayersList.Items.Add(BuildPlayerRow(p, iAmFree));
        }

        private void SendChallengeTo(RosterEntry p)
        {
            if (_client == null || !_client.LoggedIn) return;

            var mine = _roster.FirstOrDefault(x => x.UserId == _client.UserId);
            int suggested = Latency.SuggestDelay(mine != null ? mine.PingMs : 0, p.PingMs);

            var dlg = DelayDialog.ForOutgoing(p.Username, p.PingMs, suggested, Window.GetWindow(this));
            if (dlg.ShowDialog() == true)
                _client.SendChallenge(p.UserId, dlg.FrameDelay);
        }

        private UIElement BuildPlayerRow(RosterEntry p, bool iAmFree)
        {
            var name = new TextBlock
            {
                Text = p.Username,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14
            };

            int nBars = Latency.Bars(p.PingMs);
            Brush barOn = nBars >= 3 ? (Brush)FindResource("Ok")
                        : nBars == 2 ? (Brush)FindResource("Warn")
                                     : (Brush)FindResource("Bad");
            var bars = SignalBars.Build(nBars, barOn, (Brush)FindResource("Stroke"));
            var ping = new TextBlock
            {
                Text = p.PingMs > 0 ? p.PingMs + " ms" : "--",
                Foreground = (Brush)FindResource("TextDim"),
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            string stateText;
            switch (p.State)
            {
                case PlayerState.PlayerInMatch:    stateText = "em partida"; break;
                case PlayerState.PlayerChallenging: stateText = "em desafio"; break;
                default:                           stateText = "livre"; break;
            }
            var state = new TextBlock
            {
                Text = stateText,
                Foreground = (Brush)FindResource("TextDim"),
                FontSize = 12,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var btn = new Button
            {
                Content = "Desafiar",
                Style = (Style)FindResource("Btn"),
                Padding = new Thickness(12, 4, 12, 4),
                IsEnabled = iAmFree && p.State == PlayerState.PlayerInRoom
            };
            btn.Click += (s, e) => SendChallengeTo(p);

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(bars);
            left.Children.Add(ping);
            left.Children.Add(new TextBlock { Width = 10 });
            left.Children.Add(name);

            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = false };
            DockPanel.SetDock(btn, Dock.Right);
            DockPanel.SetDock(state, Dock.Right);
            row.Children.Add(btn);
            row.Children.Add(state);
            row.Children.Add(left);

            return new Border
            {
                Background = (Brush)FindResource("BgPanel"),
                BorderBrush = (Brush)FindResource("Stroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = row
            };
        }

        // Watching opens a second emulator in spectator mode. It replays the
        // host's input stream from the relay, so it needs the same ROM but no
        // connection to either player.
        private void Watch(MatchEntry m)
        {
            if (_client == null || !_client.LoggedIn) return;

            if (_client.RelayPort <= 0)
            {
                MessageBox.Show("Este servidor não está com o relay de espectador ligado.", "RBF",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!_roms.RomExists(_game))
            {
                MessageBox.Show("Você precisa da ROM de " + _game.Title + " para assistir.", "RBF",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string args = string.Format("-rbfwatch matchid={0},relayip={1},relayport={2}",
                                        m.MatchId, _config.ServerHost, _client.RelayPort);
            try
            {
                _emu.Launch(_game, args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Não foi possível abrir o emulador",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Refresh()
        {
            bool hasRom = _roms.RomExists(_game);
            bool hasEmu = _emu.EmulatorExists;

            SubText.Text = _game.ShortName + "  ·  " + _roms.RomPath(_game);

            if (hasRom)
            {
                long kb = _roms.RomSize(_game) / 1024;
                RomStatusText.Text = "●  ROM instalada (" + kb.ToString("N0") + " KB)";
                RomStatusText.Foreground = (Brush)FindResource("Ok");
            }
            else
            {
                RomStatusText.Text = "○  ROM não encontrada";
                RomStatusText.Foreground = (Brush)FindResource("Warn");
            }

            EmuStatusText.Text = hasEmu
                ? "Emulador: " + _config.ResolvedEmulatorPath
                : "Emulador não encontrado em \"" + _config.ResolvedEmulatorPath + "\" — ajuste em Configurações.";
            EmuStatusText.Foreground = (Brush)FindResource(hasEmu ? "TextDim" : "Warn");

            bool canDownload = _roms.CanDownload(_game);

            PlayButton.IsEnabled = hasRom && hasEmu && !_downloading;
            DownloadButton.IsEnabled = !hasRom && !_downloading && canDownload;
            DownloadButton.Content = hasRom ? "ROM já baixada" : "Baixar ROM";

            if (_downloading || hasRom)
            {
                HintText.Visibility = Visibility.Collapsed;
            }
            else if (!canDownload)
            {
                HintText.Text = _roms.DatabaseLoaded
                    ? "\"" + _game.ShortName + "\" não está no banco de ROMs. Adicione a ROM manualmente em " + _config.ResolvedRomsDir + "."
                    : "Sem banco de ROMs (json_roms). Adicione a ROM manualmente em " + _config.ResolvedRomsDir + ".";
                HintText.Visibility = Visibility.Visible;
            }
            else
            {
                var missing = _roms.MissingSets(_game);
                HintText.Text = missing.Count > 1
                    ? "Vai baixar " + missing.Count + " arquivos (jogo + dependências)."
                    : "";
                HintText.Visibility = missing.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _back();
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _emu.Launch(_game);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Não foi possível abrir o emulador",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_downloading) return;
            _downloading = true;
            _cts = new CancellationTokenSource();
            ProgressPanel.Visibility = Visibility.Visible;
            DownloadBar.IsIndeterminate = true;
            ProgressText.Text = "Conectando…";
            Refresh();

            var progress = new Progress<DownloadProgress>(p =>
            {
                string prefix = p.Count > 1
                    ? string.Format("[{0}/{1}] {2}  ", p.Index, p.Count, p.File)
                    : p.File + "  ";
                if (p.Fraction.HasValue)
                {
                    DownloadBar.IsIndeterminate = false;
                    DownloadBar.Value = p.Fraction.Value;
                    ProgressText.Text = prefix + string.Format("{0:N0} / {1:N0} KB  ({2:P0})",
                        p.Received / 1024, (p.Total ?? 0) / 1024, p.Fraction.Value);
                }
                else
                {
                    ProgressText.Text = prefix + string.Format("{0:N0} KB", p.Received / 1024);
                }
            });

            try
            {
                await _roms.DownloadAsync(_game, progress, _cts.Token);
                ProgressText.Text = "Concluído.";
            }
            catch (OperationCanceledException)
            {
                ProgressText.Text = "Cancelado.";
            }
            catch (Exception ex)
            {
                ProgressText.Text = "Falhou: " + ex.Message;
            }
            finally
            {
                _downloading = false;
                DownloadBar.IsIndeterminate = false;
                _cts.Dispose();
                _cts = null;
                Refresh();
            }
        }
    }
}

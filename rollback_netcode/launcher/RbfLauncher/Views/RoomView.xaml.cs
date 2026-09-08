using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RbfLauncher.Core;

namespace RbfLauncher.Views
{
    public partial class RoomView : UserControl
    {
        private readonly GameInfo _game;
        private readonly AppConfig _config;
        private readonly RomService _roms;
        private readonly EmulatorService _emu;
        private readonly Action _back;

        private bool _downloading;
        private CancellationTokenSource _cts;

        public RoomView(GameInfo game, AppConfig config, RomService roms, EmulatorService emu, Action back)
        {
            InitializeComponent();
            _game = game;
            _config = config;
            _roms = roms;
            _emu = emu;
            _back = back;

            TitleText.Text = game.Title;

            var bmp = ArtLoader.Load(game.ArtFile);
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

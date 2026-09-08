using System.Windows;
using Microsoft.Win32;
using RbfLauncher.Core;

namespace RbfLauncher.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly AppConfig _config;

        public SettingsWindow(AppConfig config)
        {
            InitializeComponent();
            _config = config;
            PlayerBox.Text = config.PlayerName;
            EmuBox.Text = config.EmulatorPath;
            RomsBox.Text = config.RomsDir;
        }

        private void BrowseEmu_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "fbneo.exe|fbneo*.exe|Executável (*.exe)|*.exe|Todos|*.*",
                Title = "Selecione o fbneo.exe patcheado"
            };
            if (dlg.ShowDialog() == true)
                EmuBox.Text = dlg.FileName;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _config.PlayerName = string.IsNullOrWhiteSpace(PlayerBox.Text) ? _config.PlayerName : PlayerBox.Text.Trim();
            _config.EmulatorPath = string.IsNullOrWhiteSpace(EmuBox.Text) ? "fbneo.exe" : EmuBox.Text.Trim();
            _config.RomsDir = string.IsNullOrWhiteSpace(RomsBox.Text) ? "roms\\arcade" : RomsBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}

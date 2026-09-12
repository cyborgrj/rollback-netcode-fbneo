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
            ApiBox.Text = config.ResolvedApiBaseUrl;
            HostBox.Text = config.ServerHost;
            PortBox.Text = config.ServerPort.ToString();
            EmuBox.Text = config.EmulatorPath;
            ArgsBox.Text = config.EmulatorArgs;
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
            if (!string.IsNullOrWhiteSpace(ApiBox.Text)) _config.ApiBaseUrl = ApiBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(HostBox.Text)) _config.ServerHost = HostBox.Text.Trim();
            if (int.TryParse(PortBox.Text?.Trim(), out int port) && port >= 1 && port <= 65535) _config.ServerPort = port;
            _config.EmulatorPath = string.IsNullOrWhiteSpace(EmuBox.Text) ? "fbneo.exe" : EmuBox.Text.Trim();
            _config.EmulatorArgs = ArgsBox.Text?.Trim() ?? "";
            _config.RomsDir = RomsBox.Text?.Trim() ?? "";   // empty => auto: <fbneo.exe folder>\roms\arcade
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}

using System.Windows;
using RbfLauncher.Core;
using RbfLauncher.Views;

namespace RbfLauncher
{
    public partial class MainWindow : Window
    {
        private readonly AppConfig _config;
        private readonly RomService _roms;
        private readonly EmulatorService _emu;

        public MainWindow()
        {
            InitializeComponent();

            _config = AppConfig.Load();
            _roms = new RomService(_config, RomDatabase.Load());
            _emu = new EmulatorService(_config);

            PlayerLabel.Text = _config.PlayerName;
            ShowLibrary();
        }

        public void ShowLibrary()
        {
            Host.Content = new LibraryView(_roms, ShowRoom);
        }

        public void ShowRoom(GameInfo game)
        {
            Host.Content = new RoomView(game, _config, _roms, _emu, ShowLibrary);
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_config) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _config.Save();
                PlayerLabel.Text = _config.PlayerName;
                ShowLibrary(); // re-evaluate ROM / emulator paths
            }
        }
    }
}

using System;
using System.Windows;
using System.Windows.Threading;

namespace RbfLauncher.Views
{
    public partial class ChallengeDialog : Window
    {
        private readonly DispatcherTimer _timer;
        private int _left = 25;

        public ChallengeDialog(string fromUser, string gameTitle)
        {
            InitializeComponent();
            MsgText.Text = fromUser + " desafiou você para " + gameTitle + ".";
            UpdateCount();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                _left--;
                UpdateCount();
                if (_left <= 0) { _timer.Stop(); SafeClose(false); }
            };
            _timer.Start();
        }

        private void UpdateCount() => CountText.Text = "Expira em " + _left + "s";

        private void Accept_Click(object sender, RoutedEventArgs e) => SafeClose(true);
        private void Decline_Click(object sender, RoutedEventArgs e) => SafeClose(false);

        /// <summary>Called by the owner if the challenge was resolved elsewhere
        /// (peer cancelled, expired server-side, …).</summary>
        public void ForceClose() => SafeClose(false);

        private void SafeClose(bool accepted)
        {
            _timer?.Stop();
            if (IsLoaded && IsVisible)
            {
                try { DialogResult = accepted; } catch { Close(); }
            }
        }
    }
}

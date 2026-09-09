using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RbfLauncher.Net;

namespace RbfLauncher.Views
{
    /// <summary>Used on both ends of a challenge: the challenger picks a delay
    /// before inviting, the challenged can adjust it before accepting. The server
    /// averages the two. Pre-set from the ping-based suggestion.</summary>
    public partial class DelayDialog : Window
    {
        private readonly DispatcherTimer _timer;
        private int _left;

        public int FrameDelay => (int)Math.Round(DelaySlider.Value);

        /// <summary>Outgoing challenge: "do you want to invite X?"</summary>
        public static DelayDialog ForOutgoing(string opponent, int opponentPingMs, int suggested, Window owner)
        {
            var d = new DelayDialog(owner);
            d.Title = "Desafiar";
            d.TitleText.Text = "Desafiar " + opponent;
            d.ShowPing(opponentPingMs);
            d.YesButton.Content = "Enviar desafio";
            d.NoButton.Content = "Cancelar";
            d.Init(suggested, 0);
            return d;
        }

        /// <summary>Incoming challenge: "X invited you", with their chosen delay.</summary>
        public static DelayDialog ForIncoming(string fromUser, string gameTitle, int suggested,
                                              int theirDelay, int secondsToAnswer, Window owner)
        {
            var d = new DelayDialog(owner);
            d.Title = "Desafio";
            d.TitleText.Text = fromUser + " desafiou você para " + gameTitle + ".";
            d.PingRow.Visibility = Visibility.Collapsed;
            d.YesButton.Content = "Aceitar";
            d.NoButton.Content = "Recusar";
            d.Init(theirDelay > 0 ? theirDelay : suggested, secondsToAnswer);
            d.HintText.Text = fromUser + " escolheu " + theirDelay +
                              ". O valor final é a média entre a escolha dos dois.";
            return d;
        }

        private DelayDialog(Window owner)
        {
            InitializeComponent();
            Owner = owner;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        }

        private void Init(int startValue, int secondsToAnswer)
        {
            DelaySlider.Value = Math.Max(0, Math.Min(10, startValue));
            DelaySlider.ValueChanged += (s, e) => UpdateDelayText();
            UpdateDelayText();

            if (secondsToAnswer <= 0) return;

            _left = secondsToAnswer;
            CountText.Visibility = Visibility.Visible;
            UpdateCount();
            _timer.Tick += (s, e) =>
            {
                _left--;
                UpdateCount();
                if (_left <= 0) { _timer.Stop(); Close(false); }
            };
            _timer.Start();
        }

        private void UpdateCount() => CountText.Text = "Expira em " + _left + "s";

        private void UpdateDelayText()
        {
            int f = FrameDelay;
            DelayText.Text = f + (f == 1 ? " frame" : " frames") +
                             "  (~" + Math.Round(f * 16.67) + " ms de atraso no seu input)";
            if (string.IsNullOrEmpty(HintText.Text))
                HintText.Text = "Mais delay = menos rollback e som mais limpo, mas o comando demora mais a sair.";
        }

        private void ShowPing(int pingMs)
        {
            int bars = Latency.Bars(pingMs);
            Brush on = bars >= 3 ? (Brush)FindResource("Ok")
                     : bars == 2 ? (Brush)FindResource("Warn")
                                 : (Brush)FindResource("Bad");
            BarsHost.Content = SignalBars.Build(bars, on, (Brush)FindResource("Stroke"));
            PingText.Text = pingMs > 0 ? pingMs + " ms" : "ping desconhecido";
        }

        private void Yes_Click(object sender, RoutedEventArgs e) => Close(true);
        private void No_Click(object sender, RoutedEventArgs e) => Close(false);

        /// <summary>Called by the owner when the challenge was resolved elsewhere.</summary>
        public void ForceClose() => Close(false);

        private void Close(bool ok)
        {
            _timer?.Stop();
            if (IsLoaded && IsVisible)
            {
                try { DialogResult = ok; } catch { base.Close(); }
            }
        }
    }
}

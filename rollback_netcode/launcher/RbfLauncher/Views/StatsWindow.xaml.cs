using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RbfLauncher.Core;

namespace RbfLauncher.Views
{
    /// <summary>A player's record, from GET /api/players/&lt;username&gt;/stats/.
    ///
    /// Works for anybody, not just the person logged in - looking up who you
    /// are about to fight is the point of having this in a lobby at all.</summary>
    public partial class StatsWindow : Window
    {
        private readonly string _username;
        private readonly string _apiBaseUrl;

        public StatsWindow(string username, string apiBaseUrl)
        {
            InitializeComponent();
            _username = username ?? "";
            _apiBaseUrl = apiBaseUrl;

            Title = "Estatísticas — " + _username;
            NameText.Text = _username;
            AccountText.Text = "";
            RankText.Text = "";
            StatusText.Text = "Carregando…";

            Loaded += async (s, e) => await Load();
        }

        public static void Open(string username, string apiBaseUrl, Window owner)
        {
            var w = new StatsWindow(username, apiBaseUrl) { Owner = owner };
            w.ShowDialog();
        }

        private async System.Threading.Tasks.Task Load()
        {
            PlayerStats st;
            try
            {
                using (var api = new AuthApi(_apiBaseUrl))
                    st = await api.GetPlayerStatsAsync(_username);
            }
            catch (AuthException ex) { Fail(ex.Message); return; }
            catch (Exception ex)     { Fail("Não consegui carregar: " + ex.Message); return; }

            if (st == null)
            {
                Fail("Esse jogador ainda não tem partidas registradas.");
                return;
            }

            NameText.Text = st.DisplayName;
            AccountText.Text = st.DisplayName != st.Username ? "@" + st.Username : "";
            RankText.Text = st.GlobalRanking.ToString();

            BuildSummary(st);

            if (st.Games.Count == 0)
            {
                StatusText.Text = "Nenhuma partida registrada ainda.";
                return;
            }

            StatusText.Visibility = Visibility.Collapsed;
            foreach (var g in st.Games) GamesPanel.Children.Add(BuildGameRow(g));
        }

        private void Fail(string message)
        {
            StatusText.Text = message;
            StatusText.Foreground = (Brush)FindResource("Warn");
        }

        // ---- the numbers at the top -------------------------------------------
        private void BuildSummary(PlayerStats st)
        {
            SummaryPanel.Children.Clear();
            AddStat("Partidas", st.TotalMatches.ToString(), null);
            AddStat("Vitórias", st.TotalWon.ToString(), "Ok");
            AddStat("Derrotas", st.TotalLost.ToString(), "Bad");
            // A draw is rare enough that a permanent zero would be noise.
            if (st.TotalDrawn > 0) AddStat("Empates", st.TotalDrawn.ToString(), null);
            AddStat("Aproveitamento", st.TotalWinRate.ToString("0.#") + "%", null);
            AddStat("Tempo de jogo", Hours(st.TotalHoursPlayed), null);
        }

        private void AddStat(string label, string value, string brushKey)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 28, 0) };
            box.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = brushKey != null ? (Brush)FindResource(brushKey) : (Brush)FindResource("Text"),
            });
            box.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 0),
                Foreground = (Brush)FindResource("TextDim"),
            });
            SummaryPanel.Children.Add(box);
        }

        private static string Hours(double h)
        {
            // Django sends 0.16 for ten minutes. Printing "0,2h" reads as
            // nothing at all; minutes read as ten minutes.
            int mins = (int)Math.Round(h * 60.0);
            if (mins < 60) return mins + " min";
            return (mins / 60) + "h " + (mins % 60).ToString("00") + "min";
        }

        // ---- one game ---------------------------------------------------------
        private UIElement BuildGameRow(GameStats g)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = g.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Text"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            left.Children.Add(new TextBlock
            {
                Text = g.MatchesWon + "V  " + g.MatchesLost + "D" +
                       (g.MatchesDrawn > 0 ? "  " + g.MatchesDrawn + "E" : "") +
                       "   ·   " + g.WinRate.ToString("0.#") + "%" +
                       "   ·   " + g.PlayedText,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)FindResource("TextDim"),
            });

            // The win-rate bar. A number is exact; a bar is the thing you can
            // compare between four games without reading any of them.
            var track = new Border
            {
                Height = 5,
                Background = (Brush)FindResource("Stroke"),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 7, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var fill = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = g.WinRate >= 50 ? (Brush)FindResource("Ok") : (Brush)FindResource("Bad"),
            };
            // Width comes from the track once it has one, so the bar is right
            // at any window size instead of at the one it was written for.
            track.SizeChanged += (s, e) => fill.Width = Math.Max(0, e.NewSize.Width * Math.Min(g.WinRate, 100.0) / 100.0);
            track.Child = new Grid { Children = { fill } };
            left.Children.Add(track);

            var rank = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            rank.Children.Add(new TextBlock
            {
                Text = g.Ranking.ToString(),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = (Brush)FindResource("Accent"),
            });
            rank.Children.Add(new TextBlock
            {
                Text = "rank",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = (Brush)FindResource("TextDim"),
            });

            Grid.SetColumn(left, 0);
            Grid.SetColumn(rank, 1);
            grid.Children.Add(left);
            grid.Children.Add(rank);

            return new Border
            {
                Background = (Brush)FindResource("BgPanel"),
                BorderBrush = (Brush)FindResource("Stroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
            };
        }
    }
}

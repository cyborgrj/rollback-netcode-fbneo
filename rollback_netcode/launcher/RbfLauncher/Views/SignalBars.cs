using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RbfLauncher.Views
{
    /// <summary>The little 4-bar signal icon used next to a player's name.
    /// 4 bars = under 10 ms, 1 bar = 60 ms or worse (see Net.Latency.Bars).</summary>
    internal static class SignalBars
    {
        private static readonly double[] Heights = { 5, 8, 11, 14 };

        public static UIElement Build(int bars, Brush on, Brush off)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 14
            };

            for (int i = 0; i < Heights.Length; i++)
            {
                panel.Children.Add(new Rectangle
                {
                    Width = 3,
                    Height = Heights[i],
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = i < bars ? on : off,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0)
                });
            }
            return panel;
        }
    }
}

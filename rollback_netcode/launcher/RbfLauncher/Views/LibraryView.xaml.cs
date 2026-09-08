using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RbfLauncher.Core;

namespace RbfLauncher.Views
{
    public partial class LibraryView : UserControl
    {
        private readonly RomService _roms;
        private readonly Action<GameInfo> _open;

        public LibraryView(RomService roms, Action<GameInfo> open)
        {
            InitializeComponent();
            _roms = roms;
            _open = open;

            foreach (var game in GameCatalog.All)
                Grid.Children.Add(BuildCard(game));
        }

        private UIElement BuildCard(GameInfo game)
        {
            bool hasRom = _roms.RomExists(game);

            // art or placeholder
            FrameworkElement art;
            BitmapImage bmp = ArtLoader.Load(game.ArtFile);
            if (bmp != null)
            {
                art = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill,
                    Height = 160
                };
            }
            else
            {
                art = new Border
                {
                    Height = 160,
                    Background = (Brush)FindResource("BgPanelHi"),
                    Child = new TextBlock
                    {
                        Text = game.ShortName,
                        FontSize = 20,
                        Foreground = (Brush)FindResource("TextDim"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }
            var artClip = new Border
            {
                CornerRadius = new CornerRadius(8, 8, 0, 0),
                ClipToBounds = true,
                Child = art
            };

            var title = new TextBlock
            {
                Text = game.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(12, 10, 12, 2)
            };

            var badge = new TextBlock
            {
                Text = hasRom ? "●  ROM instalada" : "○  Sem ROM",
                FontSize = 12,
                Foreground = (Brush)FindResource(hasRom ? "Ok" : "Warn"),
                Margin = new Thickness(12, 0, 12, 12)
            };

            var stack = new StackPanel();
            stack.Children.Add(artClip);
            stack.Children.Add(title);
            stack.Children.Add(badge);

            var card = new Button
            {
                Style = null,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(8),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("Stroke"),
                Background = (Brush)FindResource("BgPanel"),
                Content = stack,
                Template = (ControlTemplate)FindResource("CardButtonTemplate")
            };
            card.Click += (s, e) => _open(game);
            return card;
        }
    }
}

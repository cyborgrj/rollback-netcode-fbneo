using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Rbf.Protocol;
using RbfLauncher.Net;

namespace RbfLauncher.Views
{
    /// <summary>Lobby chat: one global tab plus the current game room. Both logs
    /// are kept client-side so switching tabs is instant and nothing is refetched.
    /// </summary>
    public partial class ChatPanel : UserControl
    {
        private const int MaxLines = 300;

        private readonly List<ChatMsg> _global = new List<ChatMsg>();
        private readonly List<ChatMsg> _room = new List<ChatMsg>();

        private LobbyClient _client;
        private ChatScope _tab = ChatScope.ChatGlobal;
        private string _roomName;          // null when not in a room
        private bool _roomUnread, _globalUnread;

        public ChatPanel()
        {
            InitializeComponent();
            Render();
            UpdateTabs();
        }

        public void SetClient(LobbyClient client)
        {
            _client = client;
            if (client == null)
            {
                _global.Clear();
                _room.Clear();
                Render();
            }
            UpdateTabs();
        }

        /// <summary>Called when the app enters or leaves a game room. Leaving
        /// drops the room log - the next join is answered with a fresh backlog.</summary>
        public void SetRoom(string shortName)
        {
            if (_roomName == shortName) return;
            _roomName = shortName;
            _room.Clear();
            if (_roomName == null && _tab == ChatScope.ChatRoom) _tab = ChatScope.ChatGlobal;
            Render();
            UpdateTabs();
        }

        // ---- incoming -----------------------------------------------------
        public void OnChat(ChatMsg m)
        {
            var list = Bucket(m.Scope);
            if (list == null) return;
            list.Add(m);
            Trim(list);

            if (m.Scope == _tab) Append(m, scrollToEnd: true);
            else
            {
                if (m.Scope == ChatScope.ChatGlobal) _globalUnread = true; else _roomUnread = true;
                UpdateTabs();
            }
        }

        public void OnChatLog(ChatLog log)
        {
            var list = Bucket(log.Scope);
            if (list == null) return;
            list.Clear();
            list.AddRange(log.Messages);
            Trim(list);
            if (log.Scope == _tab) Render();
        }

        private List<ChatMsg> Bucket(ChatScope s) =>
            s == ChatScope.ChatGlobal ? _global : (_roomName != null ? _room : null);

        private static void Trim(List<ChatMsg> l)
        {
            if (l.Count > MaxLines) l.RemoveRange(0, l.Count - MaxLines);
        }

        // ---- rendering -----------------------------------------------------
        private void Render()
        {
            LogHost.Children.Clear();
            var list = _tab == ChatScope.ChatGlobal ? _global : _room;
            foreach (var m in list) Append(m, scrollToEnd: false);
            LogScroll.ScrollToEnd();
        }

        // ---- name colours --------------------------------------------------
        // Hash the NAME, never the session id: the server hands out a fresh GUID
        // on every login, so ids would repaint everybody on each reconnect and
        // would not agree between machines. The name is stable and identical
        // everywhere, so one person is one colour, for everyone, forever.
        private static uint Fnv1a(string s)
        {
            uint h = 2166136261;
            foreach (char c in s.ToLowerInvariant())
            {
                h ^= c;
                h *= 16777619;
            }
            return h;
        }

        private readonly Dictionary<string, Brush> _nameBrush =
            new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        private int _paletteSize = -1;   // counted from the theme on first use

        /// <summary>One of the ChatName* brushes in Theme.xaml, chosen by name.
        /// UI thread only, which is why the cache needs no lock.</summary>
        private Brush ColorFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return (Brush)FindResource("Accent");
            if (_nameBrush.TryGetValue(username, out var cached)) return cached;

            if (_paletteSize < 0)
            {
                // Count what the theme actually defines, so adding a colour there is
                // the whole change.
                _paletteSize = 0;
                while (TryFindResource("ChatName" + (_paletteSize + 1)) != null) _paletteSize++;
            }

            Brush b = _paletteSize == 0
                ? (Brush)FindResource("Accent")
                : (Brush)FindResource("ChatName" + (Fnv1a(username) % (uint)_paletteSize + 1));

            _nameBrush[username] = b;
            return b;
        }

        private void Append(ChatMsg m, bool scrollToEnd)
        {
            bool atBottom = LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 24;

            var line = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
                FontSize = 12.5,
            };

            var when = DateTimeOffset.FromUnixTimeMilliseconds(m.T).ToLocalTime();
            line.Inlines.Add(new Run(when.ToString("HH:mm") + " ")
            {
                Foreground = (Brush)FindResource("TextDim"),
                FontSize = 11,
            });

            if (string.IsNullOrEmpty(m.UserId))
            {
                // server notice - no author, dimmed
                line.Inlines.Add(new Run(m.Text)
                {
                    Foreground = (Brush)FindResource("TextDim"),
                    FontStyle = FontStyles.Italic,
                });
            }
            else
            {
                // Compared by name, not by id, for the same reason the colour is:
                // your own backlog stays green after a reconnect changes your id.
                bool mine = _client != null &&
                            string.Equals(m.Username, _client.Username, StringComparison.OrdinalIgnoreCase);
                line.Inlines.Add(new Run(m.Username + ": ")
                {
                    Foreground = mine ? (Brush)FindResource("Ok") : ColorFor(m.Username),
                    FontWeight = FontWeights.SemiBold,
                });
                line.Inlines.Add(new Run(m.Text) { Foreground = (Brush)FindResource("Text") });
            }

            LogHost.Children.Add(line);
            while (LogHost.Children.Count > MaxLines) LogHost.Children.RemoveAt(0);

            // Don't yank the view away from someone reading back through history.
            if (scrollToEnd && atBottom) LogScroll.ScrollToEnd();
        }

        private void UpdateTabs()
        {
            bool on = _client != null && _client.LoggedIn;
            bool inRoom = on && _roomName != null;

            RoomTab.IsEnabled = inRoom;
            RoomTab.Content = (inRoom ? "Sala · " + _roomName : "Sala") + (_roomUnread ? "  ●" : "");
            GlobalTab.Content = "Geral" + (_globalUnread ? "  ●" : "");

            var active = (Brush)FindResource("Accent");
            var idle = (Brush)FindResource("Stroke");
            GlobalTab.BorderBrush = _tab == ChatScope.ChatGlobal ? active : idle;
            RoomTab.BorderBrush = _tab == ChatScope.ChatRoom ? active : idle;

            InputBox.IsEnabled = on && (_tab == ChatScope.ChatGlobal || inRoom);
            SendButton.IsEnabled = InputBox.IsEnabled;
            InputBox.ToolTip = on ? null : "Conecte-se para falar no chat.";
        }

        private void Switch(ChatScope scope)
        {
            _tab = scope;
            if (scope == ChatScope.ChatGlobal) _globalUnread = false; else _roomUnread = false;
            Render();
            UpdateTabs();
            InputBox.Focus();
        }

        private void GlobalTab_Click(object sender, RoutedEventArgs e) => Switch(ChatScope.ChatGlobal);
        private void RoomTab_Click(object sender, RoutedEventArgs e) => Switch(ChatScope.ChatRoom);

        // ---- sending -------------------------------------------------------
        private void SendButton_Click(object sender, RoutedEventArgs e) => SendCurrent();

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SendCurrent();
        }

        private void SendCurrent()
        {
            var text = (InputBox.Text ?? "").Trim();
            InputBox.Text = "";
            if (text.Length == 0 || _client == null || !_client.LoggedIn) return;
            _client.SendChat(_tab, text);
        }
    }
}

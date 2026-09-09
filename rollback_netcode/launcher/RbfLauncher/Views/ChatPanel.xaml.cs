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
                bool mine = _client != null && m.UserId == _client.UserId;
                line.Inlines.Add(new Run(m.Username + ": ")
                {
                    Foreground = (Brush)FindResource(mine ? "Ok" : "Accent"),
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

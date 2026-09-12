using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Rbf.Protocol;

namespace Rbf.Server
{
    /// <summary>One connected client. Outbound messages go through an unbounded
    /// channel drained by the LobbyService write pump - the Hub never touches
    /// the gRPC stream directly.</summary>
    internal sealed class Session
    {
        public string UserId;
        public string Username;
        public string RemoteIp = "";
        public string Game = "";                       // room short name, "" = no room
        public PlayerState State = PlayerState.PlayerIdle;
        public string ActiveMatchId;
        public int    PingMs;        // round trip to this server, from the client's own Ping
        public DateTime LastChatUtc;   // rate limit, see Hub.Chat

        public readonly Channel<ServerMsg> Out =
            Channel.CreateUnbounded<ServerMsg>(new UnboundedChannelOptions { SingleReader = true });

        public void Send(ServerMsg m) => Out.Writer.TryWrite(m);
        public void Close() => Out.Writer.TryComplete();
    }

    internal sealed class Challenge
    {
        public string Id;
        public string FromId;
        public string ToId;
        public string Game;
        public int    DelayFrom;   // frames the challenger asked for
        public int    FirstTo;     // games that end the session; 0 = no limit
        public int    DelayTo;     // frames the challenged answered with
        public CancellationTokenSource Expiry;
    }

    internal sealed class Match
    {
        public string Id;
        public string Game;
        public string P1Id;
        public string P2Id;
        // Copied at creation, not looked up later. A result can arrive after
        // the player who reported it has gone, and the archived record has to
        // say who played rather than "?".
        public string P1Name;
        public string P2Name;
        public int Port;
        public int FrameDelay;
        public int FirstTo;         // games that end the session; 0 = free play
        public DateTime StartedUtc;
        public int PeerPingMs;      // estimated RTT between the two players

        // What the emulators read out of the game. Both players report the same
        // match, so the first one in is kept and the second is compared against
        // it - see ReportResult.
        public MatchResult Result;
        public string ResultFromId;
        public bool Archived;       // written to the archive; write it once
    }

    /// <summary>Last N chat lines for one scope. Bounded on purpose: the lobby
    /// keeps no history on disk, so a long-running server cannot grow here.</summary>
    internal sealed class ChatRing
    {
        private readonly Queue<ChatMsg> _q = new Queue<ChatMsg>();
        private readonly int _cap;
        public ChatRing(int cap) => _cap = cap;

        public void Add(ChatMsg m)
        {
            _q.Enqueue(m);
            while (_q.Count > _cap) _q.Dequeue();
        }

        public ChatLog Snapshot(ChatScope scope, string room)
        {
            var log = new ChatLog { Scope = scope, Room = room ?? "" };
            log.Messages.AddRange(_q);
            return log;
        }
    }

    /// <summary>All lobby state, guarded by one lock. Small scale - a single
    /// gate keeps the logic obviously correct.</summary>
    internal sealed class Hub
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Session> _sessions = new();
        private readonly Dictionary<string, Challenge> _challenges = new();
        private readonly Dictionary<string, Match> _matches = new();

        // Matches that have ended but can still be spoken about.
        //
        // This is not a nicety. The launcher sends Phase.Ended when the
        // emulator closes and the result right after it, and the OTHER player
        // closing their emulator sends an Ended for the same match - so by the
        // time a result arrives the match has usually been retired already,
        // whatever order one client uses. Without this, results were being
        // answered with "partida desconhecida" and dropped on the floor.
        private readonly Dictionary<string, Match> _recent = new();
        private readonly Queue<string> _recentOrder = new Queue<string>();
        private const int RecentCap = 128;
        private readonly ChatRing _globalChat = new ChatRing(ChatBacklog);
        private readonly Dictionary<string, ChatRing> _roomChat = new();

        // udp/ port of the NAT rendezvous, echoed to clients in MatchStart. 0 = off.
        public int PunchPort { get; set; }

        // tcp/ port of the spectator relay, handed to clients in Welcome. 0 = off.
        public int RelayPort { get; set; }

        // udp/ port that relays game traffic for pairs the rendezvous cannot punch.
        public int GameRelayPort { get; set; }

        // How many people are watching a match, or -1 when it is not being
        // published. Supplied by the relay; null means nothing is watchable.
        public Func<string, int> WatchViewers { get; set; }

        private int _epoch;
        private int _matchEpoch;
        private const int ChatBacklog = 100;
        private const int ChatMaxLen  = 300;
        private static readonly TimeSpan ChatMinGap = TimeSpan.FromMilliseconds(400);
        private int _nextPort = 7000;
        // Input delay in frames, applied to each player's local input. Higher =
        // fewer rollbacks (cleaner audio) but more input lag. Tunable per server.
        public int FrameDelay { get; set; } = 2;
        private static readonly TimeSpan ChallengeTtl = TimeSpan.FromSeconds(30);

        // ---- login / disconnect --------------------------------------------
        public Session Login(string username, string connIp, string lanIp, out string reject)
        {
            reject = null;
            username = (username ?? "").Trim();
            if (username.Length < 1 || username.Length > 24) { reject = "Nome inválido (1–24 caracteres)."; return null; }

            // Prefer the IP the client reports for itself - the address the server
            // sees is wrong when the client is behind NAT relative to the server.
            string peerIp = !string.IsNullOrWhiteSpace(lanIp) ? lanIp.Trim() : (connIp ?? "");

            lock (_gate)
            {
                // Name takeover instead of refusal. A half-open TCP connection can
                // outlive a crashed client by minutes, and that ghost session would
                // otherwise hold the name hostage and lock the player out.
                var ghost = _sessions.Values.FirstOrDefault(
                    x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
                if (ghost != null)
                {
                    Console.WriteLine($"~ {username}: replacing stale session {ghost.UserId}");
                    _sessions.Remove(ghost.UserId);
                    CancelChallengesInvolvingLocked(ghost.UserId, Outcome.Cancelled);
                    AbortMatchesInvolvingLocked(ghost.UserId, "adversário reconectou");
                    ghost.Send(Err("Sua sessão foi substituída por um novo login."));
                    ghost.Close();
                }

                var s = new Session
                {
                    UserId = Guid.NewGuid().ToString("N").Substring(0, 12),
                    Username = username,
                    RemoteIp = peerIp,
                };
                _sessions[s.UserId] = s;
                s.Send(new ServerMsg
                {
                    Welcome = new Welcome
                    {
                        UserId = s.UserId, Username = s.Username, RelayPort = RelayPort
                    }
                });
                s.Send(new ServerMsg { ChatLog = _globalChat.Snapshot(ChatScope.ChatGlobal, "") });
                BroadcastLobbyLocked();
                Console.WriteLine($"+ {username} ({s.UserId})  peer-ip={s.RemoteIp}  (conn {connIp}, reported {lanIp})");
                return s;
            }
        }

        public void Disconnect(Session s)
        {
            if (s == null) return;
            lock (_gate)
            {
                if (!_sessions.Remove(s.UserId)) return;
                CancelChallengesInvolvingLocked(s.UserId, Outcome.Cancelled);
                AbortMatchesInvolvingLocked(s.UserId, "adversário desconectou");
                if (!string.IsNullOrEmpty(s.Game))
                    PostChatLocked(ChatScope.ChatRoom, s.Game, "", "", s.Username + " saiu da sala.");
                BroadcastLobbyLocked();
                Console.WriteLine($"- {s.Username} ({s.UserId})");
            }
            s.Close();
        }

        // ---- rooms --------------------------------------------------------
        public void JoinRoom(Session s, string game)
        {
            game = (game ?? "").Trim();
            lock (_gate)
            {
                // Changing rooms implicitly abandons whatever the player was in.
                // Never refuse: a stale InMatch/Challenging state would strand them
                // in the old room forever (their emulator may have died silently).
                if (s.State == PlayerState.PlayerChallenging)
                    CancelChallengesInvolvingLocked(s.UserId, Outcome.Cancelled);
                if (s.ActiveMatchId != null)
                {
                    AbortMatchesInvolvingLocked(s.UserId, "adversário saiu da partida");
                    s.ActiveMatchId = null;
                }

                string previous = s.Game;
                s.Game = game;
                s.State = string.IsNullOrEmpty(game) ? PlayerState.PlayerIdle : PlayerState.PlayerInRoom;

                // Notices go out AFTER s.Game moved, so the fan-out below picks
                // the right set of listeners for each room.
                if (!string.IsNullOrEmpty(previous) && previous != game)
                    PostChatLocked(ChatScope.ChatRoom, previous, "", "", s.Username + " saiu da sala.");
                if (!string.IsNullOrEmpty(game) && previous != game)
                {
                    s.Send(new ServerMsg { ChatLog = RoomChatLocked(game).Snapshot(ChatScope.ChatRoom, game) });
                    PostChatLocked(ChatScope.ChatRoom, game, "", "", s.Username + " entrou na sala.");
                }

                BroadcastLobbyLocked();
            }
        }

        public void LeaveRoom(Session s) => JoinRoom(s, "");

        // ---- challenges -------------------------------------------------
        /// <summary>Frames of input delay to suggest for a pairing. Both players
        /// reach each other roughly via this server, so peer RTT ~= the sum of
        /// their RTTs here; half of that is one way, and a frame is ~16.67ms.</summary>
        public static int SuggestDelay(int pingA, int pingB)
        {
            int frames = (int)Math.Round((pingA + pingB) / 2.0 / 16.67, MidpointRounding.AwayFromZero);
            if (frames < 1) frames = 1;
            if (frames > 10) frames = 10;
            return frames;
        }

        public void Challenge(Session from, string targetUserId, int frameDelay, int firstTo)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(targetUserId ?? "", out var to) || ReferenceEquals(to, from))
                {
                    from.Send(Err("Jogador indisponível."));
                    return;
                }
                if (from.State != PlayerState.PlayerInRoom || to.State != PlayerState.PlayerInRoom ||
                    from.Game != to.Game || string.IsNullOrEmpty(from.Game))
                {
                    from.Send(Err("Vocês precisam estar na mesma sala e livres."));
                    return;
                }

                var c = new Challenge
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    FromId = from.UserId,
                    ToId = to.UserId,
                    Game = from.Game,
                    DelayFrom = Math.Max(0, Math.Min(10, frameDelay)),
                    // 0 stays 0 - that is "livre", not "unset".
                    FirstTo   = Math.Max(0, Math.Min(99, firstTo)),
                    Expiry = new CancellationTokenSource(),
                };
                _challenges[c.Id] = c;
                from.State = PlayerState.PlayerChallenging;
                to.State = PlayerState.PlayerChallenging;

                to.Send(new ServerMsg
                {
                    ChallengeIn = new ChallengeIn
                    {
                        ChallengeId = c.Id,
                        FromUserId = from.UserId,
                        FromUsername = from.Username,
                        Game = c.Game,
                        FromFrameDelay = c.DelayFrom,
                        FirstTo        = c.FirstTo,
                        SuggestedDelay = SuggestDelay(from.PingMs, to.PingMs),
                    }
                });
                BroadcastLobbyLocked();
                _ = ExpireChallengeAfter(c);
            }
        }

        private async Task ExpireChallengeAfter(Challenge c)
        {
            try { await Task.Delay(ChallengeTtl, c.Expiry.Token).ConfigureAwait(false); }
            catch { return; }

            lock (_gate)
            {
                if (_challenges.Remove(c.Id))
                    ResolveChallengeLocked(c, Outcome.Expired);
            }
        }

        public void ChallengeReply(Session replier, string challengeId, bool accept, int frameDelay)
        {
            lock (_gate)
            {
                if (!_challenges.TryGetValue(challengeId ?? "", out var c) || c.ToId != replier.UserId)
                {
                    replier.Send(Err("Desafio inválido ou expirado."));
                    return;
                }
                _challenges.Remove(c.Id);
                c.Expiry.Cancel();

                c.DelayTo = Math.Max(0, Math.Min(10, frameDelay));

                if (!accept) { ResolveChallengeLocked(c, Outcome.Declined); return; }

                if (!_sessions.TryGetValue(c.FromId, out var from) || !_sessions.TryGetValue(c.ToId, out var to))
                    return;

                var m = new Match
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    Game = c.Game,
                    P1Id = from.UserId,
                    P2Id = to.UserId,
                    P1Name = from.Username,
                    P2Name = to.Username,
                    Port = AllocPortLocked(),
                    // meet in the middle, rounding up. Both zero means neither
                    // side expressed a preference (old client) -> server default.
                    FrameDelay = (c.DelayFrom == 0 && c.DelayTo == 0)
                                 ? FrameDelay
                                 : (c.DelayFrom + c.DelayTo + 1) / 2,
                    // Not negotiated: the challenger set the rule and the other
                    // side accepted it by accepting the challenge.
                    FirstTo    = c.FirstTo,
                    StartedUtc = DateTime.UtcNow,
                    PeerPingMs = from.PingMs + to.PingMs,
                };
                _matches[m.Id] = m;
                from.State = to.State = PlayerState.PlayerInMatch;
                from.ActiveMatchId = to.ActiveMatchId = m.Id;

                from.Send(new ServerMsg
                {
                    ChallengeResult = new ChallengeResult
                    {
                        ChallengeId = c.Id, Outcome = Outcome.Accepted, PeerUsername = to.Username,
                    }
                });

                SendMatchStartLocked(m, from, 1, to);
                SendMatchStartLocked(m, to, 2, from);
                BroadcastLobbyLocked();
                Console.WriteLine($"= match {m.Id} {m.Game}: {from.Username}@{from.RemoteIp} (P1, {from.PingMs}ms, wants {c.DelayFrom}) vs {to.Username}@{to.RemoteIp} (P2, {to.PingMs}ms, wants {c.DelayTo})  udp :{m.Port}  delay {m.FrameDelay}  {(m.FirstTo > 0 ? "FT" + m.FirstTo : "livre")}");
            }
        }

        private void SendMatchStartLocked(Match m, Session me, int playerNum, Session peer)
        {
            me.Send(new ServerMsg
            {
                MatchStart = new MatchStart
                {
                    MatchId = m.Id,
                    Game = m.Game,
                    PlayerNum = playerNum,
                    LocalBind = "0.0.0.0",
                    LocalPort = m.Port,
                    PeerIp = peer.RemoteIp,
                    PeerPort = m.Port,
                    FrameDelay = m.FrameDelay,
                    PeerUsername = peer.Username,
                    PunchPort = PunchPort,
                    GameRelayPort = GameRelayPort,
                    FirstTo = m.FirstTo,
                }
            });
        }

        private void ResolveChallengeLocked(Challenge c, Outcome outcome)
        {
            if (_sessions.TryGetValue(c.FromId, out var from))
            {
                from.State = string.IsNullOrEmpty(from.Game) ? PlayerState.PlayerIdle : PlayerState.PlayerInRoom;
                from.Send(new ServerMsg { ChallengeResult = new ChallengeResult { ChallengeId = c.Id, Outcome = outcome } });
            }
            if (_sessions.TryGetValue(c.ToId, out var to))
                to.State = string.IsNullOrEmpty(to.Game) ? PlayerState.PlayerIdle : PlayerState.PlayerInRoom;
            BroadcastLobbyLocked();
        }

        // ---- match status --------------------------------------------
        public void MatchStatus(Session s, string matchId, Phase phase, string detail)
        {
            lock (_gate)
            {
                if (!_matches.TryGetValue(matchId ?? "", out var m)) return;
                if (phase != Phase.Ended && phase != Phase.Failed) return;

                RetireMatchLocked(m);
                foreach (var id in new[] { m.P1Id, m.P2Id })
                {
                    if (!_sessions.TryGetValue(id, out var p)) continue;
                    p.ActiveMatchId = null;
                    p.State = string.IsNullOrEmpty(p.Game) ? PlayerState.PlayerIdle : PlayerState.PlayerInRoom;
                    if (phase == Phase.Failed && id != s.UserId)
                        p.Send(new ServerMsg { MatchAborted = new MatchAborted { MatchId = m.Id, Reason = detail ?? "adversário falhou" } });
                }
                BroadcastLobbyLocked();
            }
        }

        // Ping doubles as a lobby resync: answering with the roster guarantees the
        // client converges within one ping period even if a broadcast was missed.
        public void Pong(Session s, long t, int rttMs)
        {
            lock (_gate)
            {
                if (rttMs > 0 && rttMs < 60000) s.PingMs = rttMs;
                s.Send(new ServerMsg { Pong = new Pong { T = t } });
                s.Send(BuildRosterMsgLocked());
                s.Send(BuildMatchListMsgLocked());
            }
        }

        /// <summary>Move an ended match out of the live table but keep it
        /// reachable. See _recent for why.</summary>
        private void RetireMatchLocked(Match m)
        {
            _matches.Remove(m.Id);
            if (_recent.ContainsKey(m.Id)) return;

            _recent[m.Id] = m;
            _recentOrder.Enqueue(m.Id);
            while (_recentOrder.Count > RecentCap)
                _recent.Remove(_recentOrder.Dequeue());
        }

        private Match FindMatchLocked(string id) =>
            _matches.TryGetValue(id ?? "", out var live) ? live
            : _recent.TryGetValue(id ?? "", out var done) ? done
            : null;

        // ---- results ------------------------------------------------
        /// <summary>A player reporting what their emulator read out of the game
        /// at the end of the session.
        ///
        /// The first reading is written down and that is the record. The second
        /// player reports the same match and is only compared against it - two
        /// readings that disagree are worth a line in the log, but waiting for
        /// the second one would mean a launcher that crashed costs the record of
        /// a session that was played. Register what arrived, and move on.</summary>
        public void ReportResult(Session s, MatchResult r)
        {
            if (s == null || r == null || string.IsNullOrEmpty(r.MatchId)) return;

            lock (_gate)
            {
                var m = FindMatchLocked(r.MatchId);
                if (m == null)
                {
                    Console.WriteLine($"! resultado de {s.Username} para partida desconhecida {r.MatchId}");
                    return;
                }

                // Only the two people who played it. Without this, anybody who
                // learns a match id can write its history.
                if (s.UserId != m.P1Id && s.UserId != m.P2Id)
                {
                    Console.WriteLine($"! {s.Username} reportou resultado de uma partida que nao jogou ({r.MatchId})");
                    return;
                }

                if (m.Result == null)
                {
                    m.Result = r;
                    m.ResultFromId = s.UserId;
                    PrintResult(m, r, s.Username);

                    m.Archived = true;
                    MatchArchive.Write(m, r, s.UserId);
                    return;
                }

                if (m.ResultFromId == s.UserId) return;   // the same client saying it twice

                // Two readings of one match that disagree means a desync or a
                // client that was changed. Neither is something to swallow -
                // but the record is already written, and it stays written.
                if (m.Result.P1Games != r.P1Games || m.Result.P2Games != r.P2Games)
                {
                    Console.WriteLine($"!! resultados divergentes em {m.Id}: " +
                                      $"{m.Result.P1Games}x{m.Result.P2Games} vs {r.P1Games}x{r.P2Games} " +
                                      $"(de {s.Username}) - gravado o primeiro");
                }
            }
        }

        /// <summary>The session as a person would read it: the totals, then one
        /// line per game with the characters that game was actually played
        /// with. Totals alone cannot say that, because both sides pick again
        /// between games.</summary>
        private static void PrintResult(Match m, MatchResult r, string reporter)
        {
            string game = r.Game ?? m.Game;
            Console.WriteLine($"# resultado {m.Id} {game}: " +
                              $"{m.P1Name} {r.P1Games} x {r.P2Games} {m.P2Name} " +
                              $"({r.Games} partidas, {r.Reason}" +
                              (r.FirstTo > 0 ? $", FT{r.FirstTo}" : ", livre") + ")" +
                              $"  [de {reporter}]");

            foreach (var g in r.GamesPlayed)
            {
                string p1 = Characters.Describe(game, g.P1Chars);
                string p2 = Characters.Describe(game, g.P2Chars);
                string who = g.Winner == 1 ? m.P1Name : g.Winner == 2 ? m.P2Name : "empate";
                Console.WriteLine($"    {g.Index,2}. {p1,-14} {g.P1Rounds} x {g.P2Rounds} {p2,-14} -> {who}");
            }
            if (r.GamesTruncated)
                Console.WriteLine("    (sessao longa: o detalhe para nas primeiras partidas)");
            if (r.GamesPlayed.Count == 0 && r.Games > 0)
                Console.WriteLine("    (sem detalhe por partida: emulador antigo?)");
        }

        public void Chat(Session s, ChatScope scope, string text)
        {
            // Collapse anything that would break the one-line-per-message
            // rendering on the client.
            text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (text.Length == 0) return;
            if (text.Length > ChatMaxLen) text = text.Substring(0, ChatMaxLen);

            lock (_gate)
            {
                // Cheap flood guard. Dropping silently beats an error popup for
                // someone who just held Enter down.
                var now = DateTime.UtcNow;
                if (now - s.LastChatUtc < ChatMinGap) return;
                s.LastChatUtc = now;

                string room = scope == ChatScope.ChatRoom ? s.Game : "";
                if (scope == ChatScope.ChatRoom && string.IsNullOrEmpty(room))
                {
                    s.Send(Err("Entre numa sala para falar nela."));
                    return;
                }

                PostChatLocked(scope, room, s.UserId, s.Username, text);
            }
        }

        /// <summary>Publish one line. user_id '' is a server notice.</summary>
        private void PostChatLocked(ChatScope scope, string room, string userId, string username, string text)
        {
            var m = new ChatMsg
            {
                Scope = scope,
                Room = room ?? "",
                UserId = userId ?? "",
                Username = username ?? "",
                Text = text,
                T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            if (scope == ChatScope.ChatGlobal) _globalChat.Add(m);
            else RoomChatLocked(room).Add(m);

            var msg = new ServerMsg { Chat = m };
            foreach (var t in _sessions.Values)
                if (scope == ChatScope.ChatGlobal || t.Game == room)
                    t.Send(msg);
        }

        private ChatRing RoomChatLocked(string room)
        {
            if (!_roomChat.TryGetValue(room, out var ring))
                _roomChat[room] = ring = new ChatRing(ChatBacklog);
            return ring;
        }

        // ---- who is playing whom ------------------------------------
        private ServerMsg BuildMatchListMsgLocked()
        {
            var list = new MatchList { Epoch = ++_matchEpoch };
            foreach (var m in _matches.Values)
            {
                _sessions.TryGetValue(m.P1Id, out var p1);
                _sessions.TryGetValue(m.P2Id, out var p2);
                int viewers = WatchViewers != null ? WatchViewers(m.Id) : -1;
                list.Matches.Add(new LiveMatch
                {
                    MatchId = m.Id,
                    Game = m.Game,
                    P1UserId = m.P1Id,
                    P1Username = p1?.Username ?? "?",
                    P2UserId = m.P2Id,
                    P2Username = p2?.Username ?? "?",
                    StartedT = new DateTimeOffset(m.StartedUtc).ToUnixTimeMilliseconds(),
                    FrameDelay = m.FrameDelay,
                    PingMs = m.PeerPingMs,
                    Watchable = viewers >= 0,
                    Viewers = viewers > 0 ? viewers : 0,
                });
            }
            return new ServerMsg { Matches = list };
        }

        /// <summary>Display name for a session id, for log lines. Never throws -
        /// a stale id from a client just reads back as "?".</summary>
        public string NameOf(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return "?";
            lock (_gate)
                return _sessions.TryGetValue(userId, out var s) ? s.Username : "?";
        }

        // ---- helpers ------------------------------------------------
        private int AllocPortLocked()
        {
            int p = _nextPort;
            _nextPort += 2;
            if (_nextPort > 7100) _nextPort = 7000;
            return p;
        }

        private void CancelChallengesInvolvingLocked(string userId, Outcome outcome)
        {
            foreach (var c in _challenges.Values.Where(x => x.FromId == userId || x.ToId == userId).ToList())
            {
                _challenges.Remove(c.Id);
                c.Expiry.Cancel();
                ResolveChallengeLocked(c, outcome);
            }
        }

        private void AbortMatchesInvolvingLocked(string userId, string reason)
        {
            foreach (var m in _matches.Values.Where(x => x.P1Id == userId || x.P2Id == userId).ToList())
            {
                RetireMatchLocked(m);
                string other = m.P1Id == userId ? m.P2Id : m.P1Id;
                if (_sessions.TryGetValue(other, out var p))
                {
                    p.ActiveMatchId = null;
                    p.State = string.IsNullOrEmpty(p.Game) ? PlayerState.PlayerIdle : PlayerState.PlayerInRoom;
                    p.Send(new ServerMsg { MatchAborted = new MatchAborted { MatchId = m.Id, Reason = reason } });
                }
            }
        }

        private ServerMsg BuildRosterMsgLocked()
        {
            var r = new Roster { Epoch = ++_epoch };
            foreach (var s in _sessions.Values)
                r.Players.Add(new Player { UserId = s.UserId, Username = s.Username, Game = s.Game, State = s.State, PingMs = s.PingMs });
            return new ServerMsg { Roster = r };
        }

        /// <summary>Push the two views of lobby state that every screen shows:
        /// who is connected, and which matches are running. They change together
        /// often enough that splitting them would only risk one going stale.</summary>
        private void BroadcastLobbyLocked()
        {
            var roster = BuildRosterMsgLocked();
            var matches = BuildMatchListMsgLocked();
            foreach (var s in _sessions.Values) { s.Send(roster); s.Send(matches); }
        }

        private static ServerMsg Err(string m) => new ServerMsg { Error = new ServerError { Message = m } };
    }
}

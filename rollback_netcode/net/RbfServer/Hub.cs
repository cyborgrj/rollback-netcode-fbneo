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
        public CancellationTokenSource Expiry;
    }

    internal sealed class Match
    {
        public string Id;
        public string Game;
        public string P1Id;
        public string P2Id;
        public int Port;
    }

    /// <summary>All lobby state, guarded by one lock. Small scale - a single
    /// gate keeps the logic obviously correct.</summary>
    internal sealed class Hub
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Session> _sessions = new();
        private readonly Dictionary<string, Challenge> _challenges = new();
        private readonly Dictionary<string, Match> _matches = new();

        private int _epoch;
        private int _nextPort = 7000;
        private const int FrameDelay = 2;
        private static readonly TimeSpan ChallengeTtl = TimeSpan.FromSeconds(30);

        // ---- login / disconnect --------------------------------------------
        public Session Login(string username, string remoteIp, out string reject)
        {
            reject = null;
            username = (username ?? "").Trim();
            if (username.Length < 1 || username.Length > 24) { reject = "Nome inválido (1–24 caracteres)."; return null; }

            lock (_gate)
            {
                if (_sessions.Values.Any(s => string.Equals(s.Username, username, StringComparison.OrdinalIgnoreCase)))
                {
                    reject = "Nome já em uso.";
                    return null;
                }

                var s = new Session
                {
                    UserId = Guid.NewGuid().ToString("N").Substring(0, 12),
                    Username = username,
                    RemoteIp = remoteIp ?? "",
                };
                _sessions[s.UserId] = s;
                s.Send(new ServerMsg { Welcome = new Welcome { UserId = s.UserId, Username = s.Username } });
                BroadcastRosterLocked();
                Console.WriteLine($"+ {username} ({s.UserId}) @ {s.RemoteIp}");
                return s;
            }
        }

        public void Disconnect(Session s)
        {
            if (s == null) return;
            lock (_gate)
            {
                if (!_sessions.Remove(s.UserId)) return;
                CancelChallengesInvolvingLocked(s.UserId, Outcome.OutcomeCancelled);
                AbortMatchesInvolvingLocked(s.UserId, "adversário desconectou");
                BroadcastRosterLocked();
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
                if (s.State == PlayerState.PlayerInMatch || s.State == PlayerState.PlayerChallenging)
                {
                    s.Send(Err("Termine a partida/desafio antes de trocar de sala."));
                    return;
                }
                s.Game = game;
                s.State = string.IsNullOrEmpty(game) ? PlayerState.PlayerIdle : PlayerState.PlayerInRoom;
                BroadcastRosterLocked();
            }
        }

        public void LeaveRoom(Session s) => JoinRoom(s, "");

        // ---- challenges -------------------------------------------------
        public void Challenge(Session from, string targetUserId)
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
                    }
                });
                BroadcastRosterLocked();
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
                    ResolveChallengeLocked(c, Outcome.OutcomeExpired);
            }
        }

        public void ChallengeReply(Session replier, string challengeId, bool accept)
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

                if (!accept) { ResolveChallengeLocked(c, Outcome.OutcomeDeclined); return; }

                if (!_sessions.TryGetValue(c.FromId, out var from) || !_sessions.TryGetValue(c.ToId, out var to))
                    return;

                var m = new Match
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                    Game = c.Game,
                    P1Id = from.UserId,
                    P2Id = to.UserId,
                    Port = AllocPortLocked(),
                };
                _matches[m.Id] = m;
                from.State = to.State = PlayerState.PlayerInMatch;
                from.ActiveMatchId = to.ActiveMatchId = m.Id;

                from.Send(new ServerMsg
                {
                    ChallengeResult = new ChallengeResult
                    {
                        ChallengeId = c.Id, Outcome = Outcome.OutcomeAccepted, PeerUsername = to.Username,
                    }
                });

                SendMatchStartLocked(m, from, 1, to);
                SendMatchStartLocked(m, to, 2, from);
                BroadcastRosterLocked();
                Console.WriteLine($"= match {m.Id} {m.Game}: {from.Username} vs {to.Username} :{m.Port}");
            }
        }

        private static void SendMatchStartLocked(Match m, Session me, int playerNum, Session peer)
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
                    FrameDelay = FrameDelay,
                    PeerUsername = peer.Username,
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
            BroadcastRosterLocked();
        }

        // ---- match status --------------------------------------------
        public void MatchStatus(Session s, string matchId, Phase phase, string detail)
        {
            lock (_gate)
            {
                if (!_matches.TryGetValue(matchId ?? "", out var m)) return;
                if (phase != Phase.PhaseEnded && phase != Phase.PhaseFailed) return;

                _matches.Remove(m.Id);
                foreach (var id in new[] { m.P1Id, m.P2Id })
                {
                    if (!_sessions.TryGetValue(id, out var p)) continue;
                    p.ActiveMatchId = null;
                    p.State = string.IsNullOrEmpty(p.Game) ? PlayerState.PlayerIdle : PlayerState.PlayerInRoom;
                    if (phase == Phase.PhaseFailed && id != s.UserId)
                        p.Send(new ServerMsg { MatchAborted = new MatchAborted { MatchId = m.Id, Reason = detail ?? "adversário falhou" } });
                }
                BroadcastRosterLocked();
            }
        }

        public void Pong(Session s, long t) => s.Send(new ServerMsg { Pong = new Pong { T = t } });

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
                _matches.Remove(m.Id);
                string other = m.P1Id == userId ? m.P2Id : m.P1Id;
                if (_sessions.TryGetValue(other, out var p))
                {
                    p.ActiveMatchId = null;
                    p.State = string.IsNullOrEmpty(p.Game) ? PlayerState.PlayerIdle : PlayerState.PlayerInRoom;
                    p.Send(new ServerMsg { MatchAborted = new MatchAborted { MatchId = m.Id, Reason = reason } });
                }
            }
        }

        private void BroadcastRosterLocked()
        {
            var r = new Roster { Epoch = ++_epoch };
            foreach (var s in _sessions.Values)
                r.Players.Add(new Player { UserId = s.UserId, Username = s.Username, Game = s.Game, State = s.State });

            var msg = new ServerMsg { Roster = r };
            foreach (var s in _sessions.Values) s.Send(msg);
        }

        private static ServerMsg Err(string m) => new ServerMsg { Error = new ServerError { Message = m } };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Rbf.Protocol;

namespace RbfLauncher.Net
{
    public sealed class RosterEntry
    {
        public string UserId;
        public string Username;
        public string Game;
        public PlayerState State;
        public int PingMs;
    }

    public sealed class MatchEntry
    {
        public string MatchId;
        public string Game;
        public string P1UserId, P1Username;
        public string P2UserId, P2Username;
        public DateTime StartedUtc;
        public int FrameDelay;
        public int PingMs;
        public bool Watchable;
        public int  Viewers;

        public TimeSpan Elapsed
        {
            get
            {
                var d = DateTime.UtcNow - StartedUtc;
                return d < TimeSpan.Zero ? TimeSpan.Zero : d;
            }
        }
    }

    public static class Latency
    {
        /// <summary>Frames of input delay to suggest for a pairing. Both players
        /// reach each other roughly via the lobby, so peer RTT ~= the sum of their
        /// RTTs to it; half is one way, and a frame is ~16.67ms at 60fps.
        /// Mirrors Hub.SuggestDelay on the server.</summary>
        public static int SuggestDelay(int pingA, int pingB)
        {
            int frames = (int)Math.Round((pingA + pingB) / 2.0 / 16.67, MidpointRounding.AwayFromZero);
            return frames < 1 ? 1 : (frames > 10 ? 10 : frames);
        }

        /// <summary>0..4 signal bars. 4 = under 10ms, 1 = 60ms or worse.</summary>
        public static int Bars(int pingMs)
        {
            if (pingMs <= 0)  return 0;   // not measured yet
            if (pingMs < 10)  return 4;
            if (pingMs < 30)  return 3;
            if (pingMs < 60)  return 2;
            return 1;
        }
    }

    /// <summary>Thin wrapper over the bidirectional Lobby stream. Events are
    /// marshalled back to the thread that called <see cref="ConnectAsync"/>
    /// (the WPF UI thread), so handlers can touch controls directly.</summary>
    public sealed class LobbyClient : IDisposable
    {
        private Channel _channel;
        private AsyncDuplexStreamingCall<ClientMsg, ServerMsg> _call;
        private CancellationTokenSource _cts;
        private SynchronizationContext _ui;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public string UserId { get; private set; }
        public string Username { get; private set; }
        public string LanIp { get; private set; } = "";
        public int LastRttMs { get; private set; }
        /// <summary>tcp port of the spectator relay on the lobby host, 0 = off.</summary>
        public int RelayPort { get; private set; }
        public bool LoggedIn => UserId != null;

        /// <summary>Best-guess local LAN IPv4: the source address the OS would use
        /// to reach the given host (or, failing that, the first non-loopback
        /// IPv4). No packet is sent - UDP Connect only sets route selection.</summary>
        public static string GuessLanIp(string towardHost)
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(towardHost, 9);
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch { }
            try
            {
                foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                        return a.ToString();
            }
            catch { }
            return "";
        }

        public event Action<string> LoginOk;                             // username
        public event Action<string> LoginRejected;                       // reason
        public event Action<IReadOnlyList<RosterEntry>> RosterUpdated;
        public event Action<ChallengeIn> ChallengeReceived;
        public event Action<ChallengeResult> ChallengeResolved;
        public event Action<MatchStart> MatchStarting;
        public event Action<MatchAborted> MatchAborted;
        public event Action<string> ServerError;                         // message
        public event Action<string> Disconnected;                        // reason
        public event Action<ChatMsg> ChatReceived;
        public event Action<ChatLog> ChatLogReceived;
        public event Action<IReadOnlyList<MatchEntry>> MatchesUpdated;

        public async Task ConnectAsync(string host, int port, string username, TimeSpan timeout)
        {
            _ui = SynchronizationContext.Current;
            _cts = new CancellationTokenSource();

            _channel = new Channel(host, port, ChannelCredentials.Insecure, new[]
            {
                // Detect a dead link quickly, so the server drops our session and
                // a later reconnect is not blocked by our own ghost.
                new ChannelOption("grpc.keepalive_time_ms", 10000),
                new ChannelOption("grpc.keepalive_timeout_ms", 5000),
                new ChannelOption("grpc.keepalive_permit_without_calls", 1),
                new ChannelOption("grpc.http2.min_time_between_pings_ms", 10000),
            });
            await _channel.ConnectAsync(DateTime.UtcNow.Add(timeout)).ConfigureAwait(false);

            LanIp = GuessLanIp(host);

            _call = new Lobby.LobbyClient(_channel).Connect(cancellationToken: _cts.Token);
            await _call.RequestStream.WriteAsync(new ClientMsg
            {
                Hello = new Hello { Username = username, ClientVer = "0.1", LanIp = LanIp }
            }).ConfigureAwait(false);

            _ = Task.Run(() => ReadLoopAsync(_cts.Token));
            _ = Task.Run(() => HeartbeatAsync(_cts.Token));
        }

        // Periodic ping. The server answers with Pong AND the current roster, so the
        // lobby self-heals within one period if a push was ever missed.
        private async Task HeartbeatAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    // carry the previous measurement so the server can publish it
                    Send(new ClientMsg { Ping = new Ping { T = DateTime.UtcNow.Ticks, RttMs = LastRttMs } });
                }
            }
            catch { /* cancelled */ }
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            string reason = "conexão encerrada";
            try
            {
                while (await _call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
                    Handle(_call.ResponseStream.Current);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { return; }
            catch (OperationCanceledException) { return; }
            catch (RpcException ex) { reason = Friendly(ex.StatusCode); }
            catch (Exception) { reason = "erro inesperado"; }
            Post(() => Disconnected?.Invoke(reason));
        }

        /// <summary>A raw gRPC failure reads "Status(StatusCode=Unavailable,
        /// Detail=\"Error starting gRPC call... end of TCP stream\")". Accurate,
        /// and no use at all to somebody who just wants to know they dropped.</summary>
        private static string Friendly(StatusCode code)
        {
            switch (code)
            {
                case StatusCode.Unavailable:      return "o servidor não respondeu";
                case StatusCode.DeadlineExceeded: return "o servidor demorou demais";
                case StatusCode.Unauthenticated:
                case StatusCode.PermissionDenied: return "o servidor recusou a sessão";
                case StatusCode.Internal:         return "erro interno no servidor";
                default:                          return "conexão perdida";
            }
        }

        private void Handle(ServerMsg m)
        {
            switch (m.KindCase)
            {
                case ServerMsg.KindOneofCase.Welcome:
                    UserId = m.Welcome.UserId;
                    Username = m.Welcome.Username;
                    RelayPort = m.Welcome.RelayPort;
                    Post(() => LoginOk?.Invoke(Username));
                    break;
                case ServerMsg.KindOneofCase.LoginRejected:
                    Post(() => LoginRejected?.Invoke(m.LoginRejected.Reason));
                    break;
                case ServerMsg.KindOneofCase.Roster:
                    var list = m.Roster.Players.Select(p => new RosterEntry
                    {
                        UserId = p.UserId,
                        Username = p.Username,
                        Game = p.Game,
                        State = p.State,
                        PingMs = p.PingMs
                    }).ToList();
                    Post(() => RosterUpdated?.Invoke(list));
                    break;
                case ServerMsg.KindOneofCase.ChallengeIn:
                    Post(() => ChallengeReceived?.Invoke(m.ChallengeIn));
                    break;
                case ServerMsg.KindOneofCase.ChallengeResult:
                    Post(() => ChallengeResolved?.Invoke(m.ChallengeResult));
                    break;
                case ServerMsg.KindOneofCase.MatchStart:
                    Post(() => MatchStarting?.Invoke(m.MatchStart));
                    break;
                case ServerMsg.KindOneofCase.MatchAborted:
                    Post(() => MatchAborted?.Invoke(m.MatchAborted));
                    break;
                case ServerMsg.KindOneofCase.Pong:
                {
                    // T is the tick count we stamped when sending; the echo gives RTT.
                    long ms = (DateTime.UtcNow.Ticks - m.Pong.T) / TimeSpan.TicksPerMillisecond;
                    if (ms >= 0 && ms < 60000) LastRttMs = (int)ms;
                    break;
                }
                case ServerMsg.KindOneofCase.Chat:
                {
                    var c = m.Chat;
                    Post(() => ChatReceived?.Invoke(c));
                    break;
                }
                case ServerMsg.KindOneofCase.ChatLog:
                {
                    var l = m.ChatLog;
                    Post(() => ChatLogReceived?.Invoke(l));
                    break;
                }
                case ServerMsg.KindOneofCase.Matches:
                {
                    var ms = m.Matches.Matches.Select(x => new MatchEntry
                    {
                        MatchId = x.MatchId,
                        Game = x.Game,
                        P1UserId = x.P1UserId,
                        P1Username = x.P1Username,
                        P2UserId = x.P2UserId,
                        P2Username = x.P2Username,
                        StartedUtc = DateTimeOffset.FromUnixTimeMilliseconds(x.StartedT).UtcDateTime,
                        FrameDelay = x.FrameDelay,
                        PingMs = x.PingMs,
                        Watchable = x.Watchable,
                        Viewers = x.Viewers,
                    }).ToList();
                    Post(() => MatchesUpdated?.Invoke(ms));
                    break;
                }
                case ServerMsg.KindOneofCase.Error:
                    Post(() => ServerError?.Invoke(m.Error.Message));
                    break;
            }
        }

        private void Post(Action a)
        {
            if (_ui != null) _ui.Post(_ => a(), null);
            else a();
        }

        private async void Send(ClientMsg m)
        {
            if (_call == null) return;
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try { await _call.RequestStream.WriteAsync(m).ConfigureAwait(false); }
            catch { /* the read loop will surface the disconnect */ }
            finally { _writeLock.Release(); }
        }

        public void JoinRoom(string game) => Send(new ClientMsg { JoinRoom = new JoinRoom { Game = game } });
        public void LeaveRoom() => Send(new ClientMsg { LeaveRoom = new LeaveRoom() });
        public void SendChallenge(string userId, int frameDelay, int firstTo) =>
            Send(new ClientMsg { Challenge = new Challenge { TargetUserId = userId, FrameDelay = frameDelay, FirstTo = firstTo } });
        public void ReplyChallenge(string id, bool accept, int frameDelay) =>
            Send(new ClientMsg { ChallengeReply = new ChallengeReply { ChallengeId = id, Accept = accept, FrameDelay = frameDelay } });
        public void SendChat(ChatScope scope, string text) =>
            Send(new ClientMsg { Chat = new ChatSend { Scope = scope, Text = text ?? "" } });
        public void ReportMatch(string matchId, Phase phase, string detail = "") =>
            Send(new ClientMsg { MatchStatus = new MatchStatus { MatchId = matchId, Phase = phase, Detail = detail ?? "" } });

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _call?.Dispose(); } catch { }
            try { _channel?.ShutdownAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            UserId = null;
        }
    }
}

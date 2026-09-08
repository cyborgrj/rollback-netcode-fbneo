using System;
using System.Collections.Generic;
using System.Linq;
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
        public bool LoggedIn => UserId != null;

        public event Action<string> LoginOk;                             // username
        public event Action<string> LoginRejected;                       // reason
        public event Action<IReadOnlyList<RosterEntry>> RosterUpdated;
        public event Action<ChallengeIn> ChallengeReceived;
        public event Action<ChallengeResult> ChallengeResolved;
        public event Action<MatchStart> MatchStarting;
        public event Action<MatchAborted> MatchAborted;
        public event Action<string> ServerError;                         // message
        public event Action<string> Disconnected;                        // reason

        public async Task ConnectAsync(string host, int port, string username, TimeSpan timeout)
        {
            _ui = SynchronizationContext.Current;
            _cts = new CancellationTokenSource();

            _channel = new Channel(host, port, ChannelCredentials.Insecure);
            await _channel.ConnectAsync(DateTime.UtcNow.Add(timeout)).ConfigureAwait(false);

            _call = new Lobby.LobbyClient(_channel).Connect(cancellationToken: _cts.Token);
            await _call.RequestStream.WriteAsync(new ClientMsg
            {
                Hello = new Hello { Username = username, ClientVer = "0.1" }
            }).ConfigureAwait(false);

            _ = Task.Run(() => ReadLoopAsync(_cts.Token));
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
            catch (Exception ex) { reason = ex.Message; }
            Post(() => Disconnected?.Invoke(reason));
        }

        private void Handle(ServerMsg m)
        {
            switch (m.KindCase)
            {
                case ServerMsg.KindOneofCase.Welcome:
                    UserId = m.Welcome.UserId;
                    Username = m.Welcome.Username;
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
                        State = p.State
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
        public void SendChallenge(string userId) => Send(new ClientMsg { Challenge = new Challenge { TargetUserId = userId } });
        public void ReplyChallenge(string id, bool accept) =>
            Send(new ClientMsg { ChallengeReply = new ChallengeReply { ChallengeId = id, Accept = accept } });
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

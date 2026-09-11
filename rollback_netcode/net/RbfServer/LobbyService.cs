using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Rbf.Protocol;

namespace Rbf.Server
{
    internal sealed class LobbyService : Lobby.LobbyBase
    {
        private readonly Hub _hub;
        public LobbyService(Hub hub) => _hub = hub;

        public override async Task Connect(
            IAsyncStreamReader<ClientMsg> requestStream,
            IServerStreamWriter<ServerMsg> responseStream,
            ServerCallContext context)
        {
            Session session = null;
            Task pump = null;
            string ip = ParseIp(context.Peer);

            try
            {
                while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
                {
                    var msg = requestStream.Current;

                    if (session == null)
                    {
                        if (msg.KindCase != ClientMsg.KindOneofCase.Hello)
                        {
                            await responseStream.WriteAsync(new ServerMsg
                            {
                                LoginRejected = new LoginRejected { Reason = "Envie Hello primeiro." }
                            }).ConfigureAwait(false);
                            return;
                        }

                        session = _hub.Login(msg.Hello.Username, ip, msg.Hello.LanIp, out var reject);
                        if (session == null)
                        {
                            await responseStream.WriteAsync(new ServerMsg
                            {
                                LoginRejected = new LoginRejected { Reason = reject }
                            }).ConfigureAwait(false);
                            return;
                        }

                        pump = PumpAsync(session, responseStream, context.CancellationToken);
                        continue;
                    }

                    Dispatch(session, msg);
                }
            }
            catch (OperationCanceledException) { /* client went away */ }
            catch (Exception ex)
            {
                Console.WriteLine($"! stream error: {ex.Message}");
            }
            finally
            {
                _hub.Disconnect(session);
                if (pump != null)
                {
                    try { await pump.ConfigureAwait(false); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>Every message a client sends passes through here, so this is
        /// the one place that can show the traffic live. Ping is left out on
        /// purpose: it arrives every 3s per client and would bury everything else
        /// - the round trip it carries is already visible in the roster.</summary>
        public static bool LogRequests { get; set; } = true;

        private static void Req(Session s, string what)
        {
            if (LogRequests) Console.WriteLine($"> {s.Username,-16} {what}");
        }

        private static string Short(string text, int max = 60)
        {
            text = (text ?? "").Trim();
            return text.Length <= max ? text : text.Substring(0, max - 1) + "2026";
        }

        private void Dispatch(Session s, ClientMsg m)
        {
            switch (m.KindCase)
            {
                case ClientMsg.KindOneofCase.JoinRoom:
                    Req(s, "join_room " + (m.JoinRoom.Game.Length == 0 ? "(nenhuma)" : m.JoinRoom.Game));
                    _hub.JoinRoom(s, m.JoinRoom.Game); break;
                case ClientMsg.KindOneofCase.LeaveRoom:
                    Req(s, "leave_room");
                    _hub.LeaveRoom(s); break;
                case ClientMsg.KindOneofCase.Challenge:
                    Req(s, $"challenge -> {_hub.NameOf(m.Challenge.TargetUserId)}  (delay {m.Challenge.FrameDelay})");
                    _hub.Challenge(s, m.Challenge.TargetUserId, m.Challenge.FrameDelay, m.Challenge.FirstTo); break;
                case ClientMsg.KindOneofCase.ChallengeReply:
                    Req(s, $"challenge_reply {(m.ChallengeReply.Accept ? "aceita" : "recusa")}  (delay {m.ChallengeReply.FrameDelay})");
                    _hub.ChallengeReply(s, m.ChallengeReply.ChallengeId, m.ChallengeReply.Accept,
                                        m.ChallengeReply.FrameDelay); break;
                case ClientMsg.KindOneofCase.MatchStatus:
                    Req(s, $"match_status {m.MatchStatus.Phase}{(m.MatchStatus.Detail.Length > 0 ? " - " + Short(m.MatchStatus.Detail) : "")}");
                    _hub.MatchStatus(s, m.MatchStatus.MatchId, m.MatchStatus.Phase, m.MatchStatus.Detail); break;
                case ClientMsg.KindOneofCase.MatchResult:
                    Req(s, $"match_result {m.MatchResult.Game} {m.MatchResult.P1Games}x{m.MatchResult.P2Games} ({m.MatchResult.Reason})");
                    _hub.ReportResult(s, m.MatchResult); break;
                case ClientMsg.KindOneofCase.Ping:
                    _hub.Pong(s, m.Ping.T, m.Ping.RttMs); break;
                case ClientMsg.KindOneofCase.Chat:
                    Req(s, $"chat {(m.Chat.Scope == ChatScope.ChatRoom ? "sala" : "geral")}: {Short(m.Chat.Text)}");
                    _hub.Chat(s, m.Chat.Scope, m.Chat.Text); break;
            }
        }

        private static async Task PumpAsync(Session s, IServerStreamWriter<ServerMsg> outStream, CancellationToken ct)
        {
            try
            {
                await foreach (var msg in s.Out.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await outStream.WriteAsync(msg).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"! pump: {ex.Message}"); }
        }

        /// <summary>"ipv4:1.2.3.4:56789" / "ipv6:[::1]:56789" -> bare address.
        /// Also unwraps an IPv4-mapped IPv6 (::ffff:1.2.3.4) to plain IPv4, which
        /// is what fbneo.exe / GGPO can actually use.</summary>
        internal static string ParseIp(string peer)
        {
            if (string.IsNullOrEmpty(peer)) return "";
            int firstColon = peer.IndexOf(':');
            string rest = firstColon >= 0 ? peer.Substring(firstColon + 1) : peer;

            string ip;
            if (rest.StartsWith("["))
            {
                int close = rest.IndexOf(']');
                ip = close > 0 ? rest.Substring(1, close - 1) : rest;
            }
            else
            {
                int lastColon = rest.LastIndexOf(':');
                ip = lastColon > 0 ? rest.Substring(0, lastColon) : rest;
            }

            int m = ip.LastIndexOf("::ffff:", StringComparison.OrdinalIgnoreCase);
            if (m >= 0 && ip.IndexOf('.', m) > 0) ip = ip.Substring(m + "::ffff:".Length);
            return ip;
        }
    }
}

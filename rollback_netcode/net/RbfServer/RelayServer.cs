using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Rbf.Server
{
    /// <summary>Spectator relay.
    ///
    /// The host of a match dials in and publishes its confirmed input stream:
    ///     "RBF1 PUB &lt;matchId&gt; &lt;game&gt; &lt;players&gt; &lt;inputBytes&gt; &lt;stateLen&gt; &lt;p1&gt; &lt;p2&gt;"
    ///     &lt;stateLen bytes: the save state the match starts from&gt;
    ///     then one fixed-size record per frame: int32 frame + players*inputBytes
    ///
    /// A spectator asks for it:
    ///     "RBF1 SUB &lt;matchId&gt;"
    /// and gets the same header back as "RBF1 HDR ...", the state, every frame so
    /// far, and then the live stream. Their emulator loads the state and replays
    /// the inputs, fast-forwarding silently until it catches up.
    ///
    /// Why this and not libggpo's own spectator mode: that one refuses to admit
    /// anyone once the game is running, buffers 64 frames, and makes every viewer
    /// another NAT traversal against the host's upstream. This is ~480 bytes/s of
    /// bookkeeping here instead, both ends dial out, and there is no limit on how
    /// many people watch.</summary>
    internal sealed class RelayServer
    {
        private const string Magic = "RBF1";
        private const int MaxMatches = 16;
        private const int MaxStateBytes = 16 * 1024 * 1024;
        private const int MaxFrames = 60 * 60 * 60 * 2;      // two hours at 60fps
        private const int SubQueueLimit = 16384;             // frames a viewer may fall behind
        private static readonly TimeSpan Linger = TimeSpan.FromSeconds(30);

        private sealed class Subscriber
        {
            public readonly Channel<byte[]> Queue =
                Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            public volatile bool Dropped;

            // ChannelReader.Count is not implemented by every channel flavour (the
            // single-reader unbounded one throws), so the backlog is counted here.
            public int Pending;
        }

        private sealed class RelayMatch
        {
            public string Id, Game, P1, P2;
            public int Players, InputBytes, RecBytes;
            public byte[] State;

            public readonly object Gate = new object();
            public byte[] Frames = new byte[64 * 1024];
            public int Len;                     // bytes used in Frames
            public int Count;                   // frames appended
            public bool Live = true;
            public DateTime EndedUtc;
            public readonly List<Subscriber> Subs = new List<Subscriber>();

            public void Append(byte[] rec)
            {
                lock (Gate)
                {
                    if (Count >= MaxFrames) return;
                    if (Len + rec.Length > Frames.Length)
                    {
                        int cap = Frames.Length;
                        while (cap < Len + rec.Length) cap *= 2;
                        Array.Resize(ref Frames, cap);
                    }
                    Buffer.BlockCopy(rec, 0, Frames, Len, rec.Length);
                    Len += rec.Length;
                    Count++;

                    foreach (var s in Subs)
                    {
                        if (s.Dropped) continue;
                        // A viewer this far behind is not coming back; cutting them
                        // loose keeps one bad connection from growing without bound.
                        if (Volatile.Read(ref s.Pending) > SubQueueLimit)
                        {
                            s.Dropped = true;
                            s.Queue.Writer.TryComplete();
                            continue;
                        }
                        if (s.Queue.Writer.TryWrite(rec)) Interlocked.Increment(ref s.Pending);
                    }
                }
            }
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, RelayMatch> _matches =
            new Dictionary<string, RelayMatch>(StringComparer.OrdinalIgnoreCase);
        private readonly TcpListener _listener;

        public int Port { get; }

        public RelayServer(string bind, int port)
        {
            Port = port;
            var addr = bind == "0.0.0.0" || string.IsNullOrEmpty(bind) ? IPAddress.Any : IPAddress.Parse(bind);
            _listener = new TcpListener(addr, port);
        }

        public void Start(CancellationToken ct)
        {
            _listener.Start();
            Console.WriteLine($"Spectator relay on tcp/{Port}");
            _ = AcceptLoopAsync(ct);
            _ = ReapLoopAsync(ct);
        }

        /// <summary>Viewers currently watching a match, or -1 when it is not being
        /// published at all. The Hub reads this when it builds the match list.</summary>
        public int ViewersOf(string matchId)
        {
            if (string.IsNullOrEmpty(matchId)) return -1;
            lock (_gate)
            {
                if (!_matches.TryGetValue(matchId, out var m) || !m.Live) return -1;
                lock (m.Gate) return m.Subs.Count(s => !s.Dropped);
            }
        }

        // ---- accept ---------------------------------------------------------
        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Console.WriteLine($"! relay accept: {ex.Message}"); continue; }

                _ = HandleAsync(c, ct);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            string who = "?";
            try
            {
                client.NoDelay = true;
                using (client)
                using (var s = client.GetStream())
                {
                    who = client.Client.RemoteEndPoint?.ToString() ?? "?";
                    string line = await ReadLineAsync(s, ct).ConfigureAwait(false);
                    if (line == null) return;

                    var parts = line.Split(' ');
                    if (parts.Length < 3 || parts[0] != Magic) return;

                    if (parts[1] == "PUB")       await PublishAsync(s, parts, who, ct).ConfigureAwait(false);
                    else if (parts[1] == "SUB")  await SubscribeAsync(s, parts[2], who, ct).ConfigureAwait(false);
                }
            }
            catch (IOException) { /* the other end went away */ }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"! relay {who}: {ex.Message}"); }
        }

        // ---- publisher ------------------------------------------------------
        private async Task PublishAsync(NetworkStream s, string[] p, string who, CancellationToken ct)
        {
            // RBF1 PUB <matchId> <game> <players> <inputBytes> <stateLen> <p1> <p2>
            if (p.Length < 9) return;
            string id = p[2], game = p[3];
            if (!int.TryParse(p[4], out int players) || !int.TryParse(p[5], out int inputBytes) ||
                !int.TryParse(p[6], out int stateLen)) return;
            if (players < 1 || players > 4 || inputBytes < 1 || inputBytes > 8) return;
            if (stateLen < 0 || stateLen > MaxStateBytes) return;

            var m = new RelayMatch
            {
                Id = id,
                Game = game,
                P1 = p[7],
                P2 = p[8],
                Players = players,
                InputBytes = inputBytes,
                RecBytes = 4 + players * inputBytes,
                State = new byte[stateLen],
            };

            if (stateLen > 0 && !await ReadExactAsync(s, m.State, stateLen, ct).ConfigureAwait(false)) return;

            lock (_gate)
            {
                if (_matches.Count >= MaxMatches && !_matches.ContainsKey(id))
                {
                    Console.WriteLine($"! relay: too many live matches, refusing {id}");
                    return;
                }
                _matches[id] = m;
            }
            Console.WriteLine($"~ relay: {m.P1} vs {m.P2} ({game}) publishing as {id} from {who}, state {stateLen}B");

            var rec = new byte[m.RecBytes];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(s, rec, m.RecBytes, ct).ConfigureAwait(false)) break;
                    var copy = new byte[m.RecBytes];
                    Buffer.BlockCopy(rec, 0, copy, 0, m.RecBytes);
                    m.Append(copy);
                }
            }
            finally
            {
                lock (m.Gate)
                {
                    m.Live = false;
                    m.EndedUtc = DateTime.UtcNow;
                    foreach (var sub in m.Subs) sub.Queue.Writer.TryComplete();
                }
                Console.WriteLine($"~ relay: {id} ended after {m.Count} frames");
            }
        }

        // ---- subscriber -----------------------------------------------------
        private async Task SubscribeAsync(NetworkStream s, string id, string who, CancellationToken ct)
        {
            RelayMatch m;
            bool available;
            lock (_gate) available = _matches.TryGetValue(id, out m) && m.Live;

            if (!available)
            {
                await WriteLineAsync(s, $"{Magic} ERR nao-disponivel", ct).ConfigureAwait(false);
                return;
            }

            var sub = new Subscriber();
            byte[] backlog;
            lock (m.Gate)
            {
                // Snapshot and register under the same lock, so no frame is either
                // missed between the two or delivered twice.
                backlog = new byte[m.Len];
                Buffer.BlockCopy(m.Frames, 0, backlog, 0, m.Len);
                m.Subs.Add(sub);
            }

            Console.WriteLine($"~ relay: {who} watching {id} ({m.Count} frames of backlog)");
            try
            {
                await WriteLineAsync(s,
                    $"{Magic} HDR {m.Game} {m.Players} {m.InputBytes} {m.State.Length} {m.P1} {m.P2}",
                    ct).ConfigureAwait(false);

                if (m.State.Length > 0)
                    await s.WriteAsync(m.State, 0, m.State.Length, ct).ConfigureAwait(false);
                if (backlog.Length > 0)
                    await s.WriteAsync(backlog, 0, backlog.Length, ct).ConfigureAwait(false);
                await s.FlushAsync(ct).ConfigureAwait(false);

                await foreach (var rec in sub.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    int left = Interlocked.Decrement(ref sub.Pending);
                    await s.WriteAsync(rec, 0, rec.Length, ct).ConfigureAwait(false);
                    // Flush once the burst is drained, not once per frame.
                    if (left <= 0) await s.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                sub.Dropped = true;
                lock (m.Gate) m.Subs.Remove(sub);
                Console.WriteLine($"~ relay: {who} stopped watching {id}");
            }
        }

        // ---- housekeeping ---------------------------------------------------
        private async Task ReapLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
                catch { return; }

                lock (_gate)
                {
                    // Finished matches linger briefly so viewers still draining the
                    // tail are not cut off mid-round.
                    foreach (var kv in _matches.Where(
                                 kv => !kv.Value.Live && DateTime.UtcNow - kv.Value.EndedUtc > Linger).ToList())
                        _matches.Remove(kv.Key);
                }
            }
        }

        // ---- wire helpers ---------------------------------------------------
        private static async Task<string> ReadLineAsync(NetworkStream s, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < 512)
            {
                int k = await s.ReadAsync(one, 0, 1, ct).ConfigureAwait(false);
                if (k <= 0) return null;
                if (one[0] == (byte)'\n') return sb.ToString();
                if (one[0] != (byte)'\r') sb.Append((char)one[0]);
            }
            return null;
        }

        private static async Task WriteLineAsync(NetworkStream s, string line, CancellationToken ct)
        {
            var b = Encoding.ASCII.GetBytes(line + "\n");
            await s.WriteAsync(b, 0, b.Length, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, int n, CancellationToken ct)
        {
            int got = 0;
            while (got < n)
            {
                int k = await s.ReadAsync(buf, got, n - got, ct).ConfigureAwait(false);
                if (k <= 0) return false;
                got += k;
            }
            return true;
        }
    }
}

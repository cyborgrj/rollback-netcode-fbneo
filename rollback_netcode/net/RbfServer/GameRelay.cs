using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rbf.Server
{
    /// <summary>UDP relay for the game traffic itself, and the second vantage
    /// point that exposes a symmetric NAT.
    ///
    /// Hole punching cannot work against a carrier-grade NAT that remaps per
    /// destination: the mapping the rendezvous observed is only good for the
    /// rendezvous, and packets aimed at it from anywhere else are dropped. Mobile
    /// networks do exactly this. Such a pair has to be relayed.
    ///
    /// Registration, sent from the very port libggpo will bind:
    ///     "RBF1 GREG &lt;matchId&gt; &lt;side&gt;"   -&gt;   "RBF1 GSELF &lt;ip&gt; &lt;port&gt;"
    ///
    /// After that, anything arriving from a registered endpoint is forwarded
    /// verbatim to the other side. libggpo needs no changes at all: it is simply
    /// told that this relay IS the peer, and since every packet it receives comes
    /// from that one address, its own peer-address check passes.
    ///
    /// Because we see each client here AND on the rendezvous port, comparing the
    /// two source endpoints tells us whether their NAT remaps per destination -
    /// the same test STUN uses, without a second rendezvous port.</summary>
    internal sealed class GameRelay
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

        private sealed class Pair
        {
            public IPEndPoint Side1, Side2;
            public DateTime Seen = DateTime.UtcNow;
            public long Forwarded;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Pair> _pairs =
            new Dictionary<string, Pair>(StringComparer.OrdinalIgnoreCase);

        // Reverse index so a forwarded packet costs one lookup, not a scan.
        private readonly Dictionary<IPEndPoint, (string Match, int Side)> _byEndpoint =
            new Dictionary<IPEndPoint, (string, int)>();

        private readonly UdpClient _udp;

        public int Port { get; }

        public GameRelay(string bind, int port)
        {
            IPAddress addr;
            if (string.IsNullOrWhiteSpace(bind) || bind == "0.0.0.0" || !IPAddress.TryParse(bind, out addr))
                addr = IPAddress.Any;

            _udp = new UdpClient(new IPEndPoint(addr, port));
            Port = port;

            // Same Windows quirk as the rendezvous: one ICMP port-unreachable from
            // a peer that quit would otherwise poison every later receive.
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { /* not Windows, or unsupported - harmless */ }
        }

        public void Start(CancellationToken ct)
        {
            Console.WriteLine($"Game relay on udp/{Port}");
            _ = Task.Run(() => LoopAsync(ct));
        }

        /// <summary>Where we saw this side register, or null if it never did.
        /// The rendezvous compares this with its own observation.</summary>
        public IPEndPoint ObservedEndpoint(string matchId, int side)
        {
            if (string.IsNullOrEmpty(matchId)) return null;
            lock (_gate)
            {
                if (!_pairs.TryGetValue(matchId, out var p)) return null;
                return side == 1 ? p.Side1 : p.Side2;
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { continue; }

                try { Handle(r); }
                catch (Exception ex) { Console.WriteLine("! game relay: " + ex.Message); }
            }
        }

        private void Handle(UdpReceiveResult r)
        {
            // A registration is the only thing we ever parse. Everything else is
            // opaque libggpo traffic and must be forwarded untouched.
            if (IsRegistration(r.Buffer, out string matchId, out int side))
            {
                Register(matchId, side, r.RemoteEndPoint);
                return;
            }

            IPEndPoint other = null;
            lock (_gate)
            {
                if (_byEndpoint.TryGetValue(r.RemoteEndPoint, out var who) &&
                    _pairs.TryGetValue(who.Match, out var p))
                {
                    other = who.Side == 1 ? p.Side2 : p.Side1;
                    p.Seen = DateTime.UtcNow;
                    p.Forwarded++;
                }
            }

            // An unknown source is either a stale match or somebody poking the
            // port; either way there is nowhere to send it.
            if (other != null)
                try { _udp.Send(r.Buffer, r.Buffer.Length, other); } catch { }
        }

        private static bool IsRegistration(byte[] buf, out string matchId, out int side)
        {
            matchId = null;
            side = 0;
            // Registrations are short ASCII; game packets are binary and longer.
            if (buf.Length < 12 || buf.Length > 120) return false;
            if (buf[0] != (byte)'R' || buf[1] != (byte)'B' || buf[2] != (byte)'F' || buf[3] != (byte)'1') return false;

            string msg = Encoding.ASCII.GetString(buf).Trim();
            var parts = msg.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || parts[1] != "GREG") return false;
            if (!int.TryParse(parts[3], out side) || (side != 1 && side != 2)) return false;
            matchId = parts[2];
            return true;
        }

        private void Register(string matchId, int side, IPEndPoint from)
        {
            lock (_gate)
            {
                Prune();
                if (!_pairs.TryGetValue(matchId, out var p))
                {
                    p = new Pair();
                    _pairs[matchId] = p;
                }
                p.Seen = DateTime.UtcNow;

                // Re-registering from a new endpoint (a reconnect) must not leave
                // the old one routing into this match.
                var previous = side == 1 ? p.Side1 : p.Side2;
                if (previous != null && !previous.Equals(from)) _byEndpoint.Remove(previous);

                if (side == 1) p.Side1 = from; else p.Side2 = from;
                _byEndpoint[from] = (matchId, side);

                if (p.Side1 != null && p.Side2 != null && previous == null)
                    Console.WriteLine($"~ game relay {matchId}: {p.Side1} <-> {p.Side2}");
            }

            var b = Encoding.ASCII.GetBytes($"RBF1 GSELF {from.Address} {from.Port}\n");
            try { _udp.Send(b, b.Length, from); } catch { }
        }

        private void Prune()
        {
            if (_pairs.Count == 0) return;
            var now = DateTime.UtcNow;
            List<string> dead = null;
            foreach (var kv in _pairs)
                if (now - kv.Value.Seen > Ttl) (dead ??= new List<string>()).Add(kv.Key);
            if (dead == null) return;

            foreach (var k in dead)
            {
                var p = _pairs[k];
                if (p.Side1 != null) _byEndpoint.Remove(p.Side1);
                if (p.Side2 != null) _byEndpoint.Remove(p.Side2);
                _pairs.Remove(k);
                if (p.Forwarded > 0) Console.WriteLine($"~ game relay {k}: closed after {p.Forwarded} packets");
            }
        }
    }
}

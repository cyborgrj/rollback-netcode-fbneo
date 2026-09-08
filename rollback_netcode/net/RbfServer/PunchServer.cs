using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rbf.Server
{
    /// <summary>UDP rendezvous for NAT hole punching.
    ///
    /// Each peer announces itself from the very UDP port libggpo will bind:
    ///     "RBF1 REG &lt;matchId&gt; &lt;side&gt;"
    /// We answer with the public endpoint we observed (that IS their NAT mapping):
    ///     "RBF1 SELF &lt;ip&gt; &lt;port&gt;"
    /// and, once both sides of a match have announced, hand each the other's:
    ///     "RBF1 PEER &lt;ip&gt; &lt;port&gt;"
    ///
    /// Both then fire packets at each other, which opens both NATs. We never
    /// relay game traffic - this port only ever sees these tiny announcements.</summary>
    internal sealed class PunchServer
    {
        private sealed class Reg
        {
            public IPEndPoint Side1;
            public IPEndPoint Side2;
            public DateTime Seen = DateTime.UtcNow;
        }

        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(3);

        private readonly object _gate = new object();
        private readonly Dictionary<string, Reg> _regs = new Dictionary<string, Reg>(StringComparer.OrdinalIgnoreCase);
        private readonly UdpClient _udp;

        public int Port { get; }

        public PunchServer(string bind, int port)
        {
            IPAddress addr;
            if (string.IsNullOrWhiteSpace(bind) || bind == "0.0.0.0" || !IPAddress.TryParse(bind, out addr))
                addr = IPAddress.Any;

            _udp = new UdpClient(new IPEndPoint(addr, port));
            Port = port;

            // Windows: without this, one ICMP "port unreachable" from a peer that
            // went away makes every later ReceiveAsync throw ConnectionReset.
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { /* not Windows, or not supported - harmless */ }
        }

        public void Start(CancellationToken ct)
        {
            Console.WriteLine($"NAT punch rendezvous on udp/{Port}");
            _ = Task.Run(() => LoopAsync(ct));
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
                catch (Exception ex) { Console.WriteLine("! punch: " + ex.Message); }
            }
        }

        private void Handle(UdpReceiveResult r)
        {
            string msg = Encoding.ASCII.GetString(r.Buffer).Trim();
            var parts = msg.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || parts[0] != "RBF1" || parts[1] != "REG") return;

            string matchId = parts[2];
            if (!int.TryParse(parts[3], out int side) || (side != 1 && side != 2)) return;

            IPEndPoint mine = r.RemoteEndPoint;
            IPEndPoint theirs;

            lock (_gate)
            {
                PruneLocked();
                if (!_regs.TryGetValue(matchId, out var reg))
                {
                    reg = new Reg();
                    _regs[matchId] = reg;
                }
                reg.Seen = DateTime.UtcNow;
                if (side == 1) reg.Side1 = mine; else reg.Side2 = mine;
                theirs = (side == 1) ? reg.Side2 : reg.Side1;
            }

            Send($"RBF1 SELF {mine.Address} {mine.Port}\n", mine);

            if (theirs != null)
            {
                // Idempotent: peers keep re-announcing until they get this, so
                // answering on every REG is exactly the retry behaviour we want.
                Send($"RBF1 PEER {theirs.Address} {theirs.Port}\n", mine);
                Send($"RBF1 PEER {mine.Address} {mine.Port}\n", theirs);
                Console.WriteLine($"~ punch {matchId}: {mine} <-> {theirs}");
            }
        }

        private void Send(string text, IPEndPoint to)
        {
            var b = Encoding.ASCII.GetBytes(text);
            try { _udp.Send(b, b.Length, to); } catch { /* peer gone */ }
        }

        private void PruneLocked()
        {
            if (_regs.Count == 0) return;
            var now = DateTime.UtcNow;
            List<string> dead = null;
            foreach (var kv in _regs)
                if (now - kv.Value.Seen > Ttl) (dead ??= new List<string>()).Add(kv.Key);
            if (dead != null) foreach (var k in dead) _regs.Remove(k);
        }
    }
}

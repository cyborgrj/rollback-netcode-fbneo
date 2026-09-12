// ---------------------------------------------------------------------------
// ProbeAnalyze - read the .rbfp files that -rbfprobe writes and say which
// addresses in the game's work RAM hold the score and the characters.
//
// Nothing here knows anything about any particular game. It looks for shapes:
//
//   a round counter   sits at 0, steps to 1, steps to 2, stops. Over three
//                     minutes of a fighting game almost nothing else in RAM
//                     behaves like that, so the filter is nearly exact.
//
//   a character id    is written once at the select screen and then never
//                     moves. On its own that describes thousands of bytes, so
//                     it takes two runs with different characters: constant in
//                     both, different between them.
//
// Usage:
//   ProbeAnalyze <file.rbfp>                  summary + round-counter candidates
//   ProbeAnalyze <file.rbfp> --trace <addr>   every value that address took
//   ProbeAnalyze <file.rbfp> --dump <addr> [n]  bytes around an address, per sample
//   ProbeAnalyze <a.rbfp> <b.rbfp> --chars    character-id candidates
//
// Addresses are CPU addresses (0xFF8xxx on CPS, 0x10xxxx on the Neo Geo) and
// may be written with or without the 0x.
// ---------------------------------------------------------------------------

using System.Text;

namespace Rbf.ProbeAnalyze;

internal sealed class Header
{
    public int    Version;
    public uint   RamLen;
    public uint   CpuBase;
    // FBNeo keeps 68000 memory with the two bytes of each word the other way
    // round (cpu/m68000_intf.cpp does a ^= 1 on every mapped byte access), so
    // a buffer index is not a CPU address. Undoing it here is what makes a
    // multi-byte field read back in order and match a published address.
    public bool   ByteSwap = true;
    public uint   ChunkSize;
    public uint   Interval;
    public string Game = "";

    public int  IndexOf(uint cpuAddr) { uint o = cpuAddr - CpuBase; return (int)(ByteSwap ? (o ^ 1) : o); }
    public uint AddrOf(int index)     { uint o = (uint)index;       return CpuBase + (ByteSwap ? (o ^ 1) : o); }
}

// What one address did over the whole recording. Volatile bytes - timers,
// animation frames, the RNG - blow past MaxTrans immediately and are dropped;
// they cannot be what we are looking for, and keeping them would cost more
// memory than the recording itself.
internal sealed class Track
{
    public const int MaxTrans = 48;

    public byte First, Last, Min, Max;
    public int  Changes;
    public bool Noisy;
    public List<int> Trans;   // (sampleIndex << 8) | newValue
}

internal sealed class Recording
{
    public Header  H = new();
    public int     Samples;
    public List<uint> Frames = new();
    public Track[] Tracks;

    public uint AddrOf(int index) => H.AddrOf(index);
    public int  IndexOf(uint addr) => H.IndexOf(addr);
    public bool Holds(uint addr) => addr >= H.CpuBase && addr < H.CpuBase + H.RamLen;
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        var files = args.Where(a => !a.StartsWith("--")).ToList();
        var flags = args.Where(a => a.StartsWith("--")).ToList();
        Loose = flags.Contains("--loose");

        try
        {
            if (flags.Contains("--chars"))
            {
                if (files.Count < 2)
                {
                    Console.Error.WriteLine("--chars needs two recordings, made with different characters.");
                    return 1;
                }
                Chars(Load(files[0]), files[0], Load(files[1]), files[1]);
                return 0;
            }

            if (files.Count < 1) { Usage(); return 1; }

            int iTrace = args.ToList().IndexOf("--trace");
            if (iTrace >= 0 && iTrace + 1 < args.Length)
            {
                Trace(files[0], ParseAddr(args[iTrace + 1]));
                return 0;
            }

            // Machine-readable: one address per line, so two runs can be
            // intersected from the shell. "the winner's counter ended at 2 in
            // both matches" is a far tighter net than any shape filter.
            int iEnds = args.ToList().IndexOf("--ends");
            if (iEnds >= 0 && iEnds + 1 < args.Length)
            {
                var rec = Load(files[0]);
                int want = int.Parse(args[iEnds + 1]);
                var ends = new List<string>();
                for (int i = 0; i < rec.Tracks.Length; i++)
                {
                    var t = rec.Tracks[i];
                    if (t.First != 0 || t.Last != want) continue;
                    if (want != 0 && !LooksLikeCounter(t)) continue;
                    if (want == 0 && t.Changes != 0) continue;
                    ends.Add($"0x{rec.AddrOf(i):X6}");
                }
                ends.Sort(StringComparer.Ordinal);
                foreach (var e in ends) Console.WriteLine(e);
                return 0;
            }

            // The shipping rule, run over a recording whose result we already
            // know. This is the oracle the emulator-side reader has to agree
            // with - it is cheap to re-check every recording here, and there is
            // no way to re-check anything once it is inside the emulator.
            if (flags.Contains("--score"))
            {
                foreach (var f in files) Score(f);
                return 0;
            }

            // Where did the rounds end? Nothing in the file says so directly,
            // but a round transition rewrites half the world - the KO, the
            // win pose, the next round being set up - while the middle of a
            // round only moves the two fighters. The spikes are the answer.
            if (flags.Contains("--activity"))
            {
                Activity(files[0]);
                return 0;
            }

            // The tightest net there is: an address whose entire life story is
            // "changed at these moments, and never otherwise". Feed it the
            // round ends that --activity found and the score falls out.
            int iSteps = args.ToList().IndexOf("--steps");
            if (iSteps >= 0 && iSteps + 1 < args.Length)
            {
                var want = args[iSteps + 1].Split(',')
                                           .Select(x => uint.Parse(x.Trim().TrimStart('f')))
                                           .ToList();
                long tol = (iSteps + 2 < args.Length && long.TryParse(args[iSteps + 2], out long tv)) ? tv : 60;
                Steps(Load(files[0]), files[0], want, tol);
                return 0;
            }

            // Health is a better handle on the score than any win counter: it
            // says who lost the round AND when, it is the same shape in every
            // fighting game, and it needs no agreement about how wins are
            // stored. It also changes far too often to keep a transition list
            // for, so this is its own streaming pass.
            if (flags.Contains("--health"))
            {
                Health(files[0]);
                return 0;
            }

            // "Never changed in the whole recording" is the wrong question for
            // anything inside the player struct: the game clears that struct
            // before the fight and again after it. What matters is that the
            // byte held still for the length of the fight.
            int iConst = args.ToList().IndexOf("--constin");
            if (iConst >= 0 && iConst + 1 < args.Length)
            {
                var parts = args[iConst + 1].Split('-');
                ConstIn(files[0], uint.Parse(parts[0]), uint.Parse(parts[1]));
                return 0;
            }

            int iDump = args.ToList().IndexOf("--dump");
            if (iDump >= 0 && iDump + 1 < args.Length)
            {
                int width = (iDump + 2 < args.Length && int.TryParse(args[iDump + 2], out int w)) ? w : 16;
                Dump(files[0], ParseAddr(args[iDump + 1]), width);
                return 0;
            }

            Summary(Load(files[0]), files[0]);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("!! " + ex.Message);
            return 2;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("ProbeAnalyze <file.rbfp>                 summary + round-counter candidates");
        Console.WriteLine("ProbeAnalyze <file.rbfp> --trace <addr>  every value that address took");
        Console.WriteLine("ProbeAnalyze <file.rbfp> --dump <addr> [n]  bytes around an address, per sample");
        Console.WriteLine("ProbeAnalyze <a.rbfp> <b.rbfp> --chars   character-id candidates");
    }

    private static uint ParseAddr(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return Convert.ToUInt32(s, 16);
    }

    // ---------------------------------------------------------------------
    //  Reading
    // ---------------------------------------------------------------------

    private static Header ReadHeader(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        return ReadHeaderFrom(br, path);
    }

    private static Header ReadHeaderFrom(BinaryReader br, string path)
    {
        var magic = br.ReadBytes(8);
        if (Encoding.ASCII.GetString(magic) != "RBFPROBE")
            throw new InvalidDataException(path + " is not an .rbfp recording.");

        var h = new Header
        {
            Version   = (int)br.ReadUInt32(),
            RamLen    = br.ReadUInt32(),
            CpuBase   = br.ReadUInt32(),
            ChunkSize = br.ReadUInt32(),
            Interval  = br.ReadUInt32(),
        };
        h.Game = Encoding.ASCII.GetString(br.ReadBytes(32)).TrimEnd('\0');
        if (h.Version >= 2)
        {
            h.ByteSwap = br.ReadUInt32() != 0;
            br.ReadBytes(28);   // reserved
        }
        else
        {
            // Version 1 did not record it, and every driver it could have been
            // made with is a 68000.
            h.ByteSwap = true;
            br.ReadBytes(32);
        }

        if (h.Version > 2) throw new InvalidDataException($"recording version {h.Version} is newer than this tool.");
        if (h.RamLen == 0 || h.RamLen > 8u * 1024 * 1024) throw new InvalidDataException("implausible RAM size in header.");
        return h;
    }

    // Walk the file, handing each reconstructed snapshot to onSample. The
    // caller never sees the delta encoding.
    private static Header Walk(string path, Action<int, uint, byte[]> onSample)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        var h = ReadHeaderFrom(br, path);
        var ram = new byte[h.RamLen];
        int sample = 0;

        while (true)
        {
            int tag = fs.ReadByte();
            if (tag < 0 || tag == 0) break;      // clean end, or a run that was cut short

            if (tag == 1)
            {
                uint frame = br.ReadUInt32();
                if (br.Read(ram, 0, (int)h.RamLen) != (int)h.RamLen) break;
                onSample(sample++, frame, ram);
            }
            else if (tag == 2)
            {
                uint frame   = br.ReadUInt32();
                uint nChunks = br.ReadUInt32();
                if (nChunks > h.RamLen / h.ChunkSize + 1) throw new InvalidDataException("delta record is corrupt.");
                bool ok = true;
                for (uint i = 0; i < nChunks; i++)
                {
                    uint c   = br.ReadUInt32();
                    uint off = c * h.ChunkSize;
                    var buf  = br.ReadBytes((int)h.ChunkSize);
                    if (buf.Length != h.ChunkSize) { ok = false; break; }
                    uint len = Math.Min(h.ChunkSize, h.RamLen - off);
                    if (off >= h.RamLen) continue;
                    Array.Copy(buf, 0, ram, off, len);
                }
                if (!ok) break;
                onSample(sample++, frame, ram);
            }
            else throw new InvalidDataException($"unknown record tag {tag}.");
        }

        return h;
    }

    private static Recording Load(string path)
    {
        var r = new Recording();
        Track[] tracks = null;

        r.H = Walk(path, (sample, frame, ram) =>
        {
            if (tracks == null)
            {
                tracks = new Track[ram.Length];
                for (int i = 0; i < ram.Length; i++)
                    tracks[i] = new Track { First = ram[i], Last = ram[i], Min = ram[i], Max = ram[i] };
                r.Frames.Add(frame);
                return;
            }

            for (int i = 0; i < ram.Length; i++)
            {
                var t = tracks[i];
                byte v = ram[i];
                if (v == t.Last) continue;

                t.Changes++;
                if (v < t.Min) t.Min = v;
                if (v > t.Max) t.Max = v;

                if (!t.Noisy)
                {
                    t.Trans ??= new List<int>();
                    if (t.Trans.Count >= Track.MaxTrans) { t.Noisy = true; t.Trans = null; }
                    else t.Trans.Add((sample << 8) | v);
                }
                t.Last = v;
            }
            r.Frames.Add(frame);
        });

        if (tracks == null) throw new InvalidDataException(path + " has no samples in it.");
        r.Tracks  = tracks;
        r.Samples = r.Frames.Count;
        return r;
    }

    // ---------------------------------------------------------------------
    //  Summary + round counters
    // ---------------------------------------------------------------------

    private static void Summary(Recording r, string path)
    {
        Console.WriteLine($"{Path.GetFileName(path)}");
        Console.WriteLine($"  game       {r.H.Game}");
        Console.WriteLine($"  work RAM   {r.H.RamLen} bytes at cpu 0x{r.H.CpuBase:X6}");
        Console.WriteLine($"  samples    {r.Samples}, every {r.H.Interval} frames " +
                          $"(frames {r.Frames.First()}..{r.Frames.Last()}, about {(r.Frames.Last() - r.Frames.First()) / 60} s)");

        int never = 0, noisy = 0;
        for (int i = 0; i < r.Tracks.Length; i++)
        {
            if (r.Tracks[i].Changes == 0) never++;
            else if (r.Tracks[i].Noisy) noisy++;
        }
        Console.WriteLine($"  bytes      {never} never changed, {noisy} changed constantly, " +
                          $"{r.Tracks.Length - never - noisy} in between");
        Console.WriteLine();

        var hits = new List<(uint addr, Track t, uint twin)>();
        for (int i = 0; i < r.Tracks.Length; i++)
            if (LooksLikeCounter(r.Tracks[i])) hits.Add((r.AddrOf(i), r.Tracks[i], 0));
        for (int i = 0; i < hits.Count; i++)
            hits[i] = (hits[i].addr, hits[i].t, Twin(r, hits[i].addr));

        // Best first. A byte that counted 0-1-2 is a far better story than one
        // that ticked to 1 and stopped, and a byte with a partner one stride
        // away is what two players' scores actually look like. Any real match
        // leaves a handful of coincidences in a 64K RAM, and this is what keeps
        // them from burying the answer.
        hits.Sort((x, y) =>
        {
            int sx = x.t.Changes * 10 + (x.twin != 0 ? 5 : 0) + x.t.Max;
            int sy = y.t.Changes * 10 + (y.twin != 0 ? 5 : 0) + y.t.Max;
            if (sx != sy) return sy - sx;
            return x.addr.CompareTo(y.addr);
        });

        Console.WriteLine($"round-counter candidates ({hits.Count}), best first - starts at 0, only ever steps up by one:");
        if (hits.Count == 0)
            Console.WriteLine("  none. Either the match was too short, or this game counts rounds somewhere odd.");

        foreach (var (addr, t, twin) in hits.Take(200))
        {
            var sb = new StringBuilder();
            sb.Append($"  0x{addr:X6}  0");
            foreach (int e in t.Trans)
            {
                int sample = e >> 8, val = e & 0xFF;
                sb.Append($" -> {val}@f{r.Frames[sample]}");
            }
            if (twin != 0) sb.Append($"   (pairs with 0x{twin:X6})");
            Console.WriteLine(sb.ToString());
        }
        if (hits.Count > 200) Console.WriteLine($"  ... and {hits.Count - 200} more");

        Console.WriteLine();
        Console.WriteLine("Next: --trace <addr> to see one of them in full, and a second recording");
        Console.WriteLine("with different characters plus --chars to pin the character ids.");
    }

    // Loose mode also accepts a reset to zero mid-recording. Real work RAM is
    // full of one-bit flags that toggle all match long, and letting them reset
    // lets every one of them through - two hundred candidates instead of a
    // dozen. A score does not go back down, so strict is the default.
    private static bool Loose;

    private static bool LooksLikeCounter(Track t)
    {
        if (t.Noisy || t.Trans == null) return false;
        if (t.Changes < 1 || t.Changes > (Loose ? 12 : 6)) return false;
        if (t.First != 0) return false;      // a score starts at zero
        if (t.Max > 6) return false;          // best-of-five is as far as these games go

        byte prev = t.First;
        foreach (int e in t.Trans)
        {
            byte v = (byte)(e & 0xFF);
            if (v == prev + 1) { prev = v; continue; }
            if (Loose && v == 0) { prev = v; continue; }
            return false;
        }
        return true;
    }

    // Another counter candidate close by, which is what the two players'
    // scores look like: same structure, one stride apart.
    private static uint Twin(Recording r, uint addr)
    {
        foreach (int d in new[] { 1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80, 0x100, 0x200, 0x400 })
        {
            foreach (int sign in new[] { 1, -1 })
            {
                long other = addr + (long)d * sign;
                if (!r.Holds((uint)other)) continue;
                if (LooksLikeCounter(r.Tracks[r.IndexOf((uint)other)])) return (uint)other;
            }
        }
        return 0;
    }

    // ---------------------------------------------------------------------
    //  One address, in full
    // ---------------------------------------------------------------------

    private static void Trace(string path, uint addr)
    {
        byte last = 0; bool firstSample = true;
        var lines = new List<string>();

        var h = ReadHeader(path);
        int off = h.IndexOf(addr);
        if (off < 0 || off >= h.RamLen)
            throw new ArgumentException($"0x{addr:X6} is outside this recording (0x{h.CpuBase:X6}..0x{h.CpuBase + h.RamLen - 1:X6}).");

        Walk(path, (sample, frame, ram) =>
        {
            byte v = ram[off];
            if (firstSample || v != last)
            {
                lines.Add($"  f{frame,-8} = {v,3}  (0x{v:X2})");
                last = v; firstSample = false;
            }
        });

        Console.WriteLine($"0x{addr:X6} in {Path.GetFileName(path)} ({h.Game}) - {lines.Count} distinct steps:");
        foreach (var l in lines) Console.WriteLine(l);
    }

    // One line per byte that never moved between the two frames: "0xADDR VALUE".
    // Meant to be joined against the same listing from another recording.
    private static void ConstIn(string path, uint from, uint to)
    {
        byte[] first = null;
        bool[] moved = null;

        var h = Walk(path, (sample, frame, ram) =>
        {
            if (frame < from || frame > to) return;
            if (first == null) { first = (byte[])ram.Clone(); moved = new bool[ram.Length]; return; }
            for (int i = 0; i < ram.Length; i++)
                if (ram[i] != first[i]) moved[i] = true;
        });

        if (first == null) { Console.Error.WriteLine("no samples in that window."); return; }
        // Sorted, because the whole point of this listing is to be joined
        // against the same listing from another recording - and the byte swap
        // means buffer order is not address order.
        var outp = new List<string>();
        for (int i = 0; i < first.Length; i++)
            if (!moved[i]) outp.Add($"0x{h.AddrOf(i):X6} {first[i]}");
        outp.Sort(StringComparer.Ordinal);
        foreach (var line in outp) Console.WriteLine(line);
    }

    private static void Health(string path)
    {
        byte[] max = null, last = null;
        int[] ups = null, bigUps = null, downs = null, zeroed = null, over = null;
        List<uint>[] refillAt = null;
        List<uint>[] zeroAt = null;
        List<uint>[] overAt = null;

        var h = Walk(path, (sample, frame, ram) =>
        {
            if (max == null)
            {
                max = (byte[])ram.Clone();
                last = (byte[])ram.Clone();
                ups = new int[ram.Length]; bigUps = new int[ram.Length];
                downs = new int[ram.Length]; zeroed = new int[ram.Length];
                refillAt = new List<uint>[ram.Length];
                zeroAt   = new List<uint>[ram.Length];
                overAt   = new List<uint>[ram.Length];
                over     = new int[ram.Length];
                return;
            }

            for (int i = 0; i < ram.Length; i++)
            {
                byte v = ram[i], p = last[i];
                if (v == p) continue;
                if (v > max[i]) max[i] = v;

                if (v > p)
                {
                    ups[i]++;
                    // A refill is the round starting over. A hit never adds
                    // health, so anything smaller than a big jump disqualifies
                    // the byte outright.
                    if (v - p >= 24) { bigUps[i]++; (refillAt[i] ??= new List<uint>()).Add(frame); }
                }
                else
                {
                    downs[i]++;
                    if (v == 0) { zeroed[i]++; (zeroAt[i] ??= new List<uint>()).Add(frame); }
                }

                // The real end-of-round marker in every one of these games: a
                // life bar that runs out does not stop at zero, it goes past it
                // and wraps. Reading it as a fall to a huge number is what makes
                // it findable without knowing which game this is.
                if (p <= 0x40 && v >= 0xF0) { over[i]++; (overAt[i] ??= new List<uint>()).Add(frame); }
                last[i] = v;
            }
        });

        Console.WriteLine($"{Path.GetFileName(path)} ({h.Game}) - bytes that behave like a life bar:");
        Console.WriteLine("refills in one jump, only ever falls otherwise, and reaches zero at least once.");
        Console.WriteLine();

        int found = 0;
        for (int i = 0; i < max.Length; i++)
        {
            if (max[i] < 48) continue;              // a life bar is not a three-bit field
            // Normally a life bar we care about is one that ran out. --loose keeps
            // the winner side too, which is how you find the pair.
            if (!Loose && zeroed[i] < 1 && over[i] < 1) continue;
            if (bigUps[i] < 1) continue;            // and the round has to have started over
            if (ups[i] != bigUps[i]) continue;      // no healing, ever
            if (downs[i] < 8) continue;             // it takes more than a couple of hits
            if (ups[i] > 6) continue;

            uint addr = h.AddrOf(i);
            string zeros   = zeroAt[i]   == null ? "-" : string.Join(",", zeroAt[i].Select(f => "f" + f));
            string overs   = overAt[i]   == null ? "-" : string.Join(",", overAt[i].Select(f => "f" + f));
            string refills2 = refillAt[i] == null ? "-" : string.Join(",", refillAt[i].Select(f => "f" + f));
            Console.WriteLine($"  0x{addr:X6}  max {max[i],3}  {downs[i],3} quedas   estourou em {overs}   zerou em {zeros}   encheu em {refills2}");
            found++;
        }

        if (found == 0) Console.WriteLine("  nada com essa forma.");
        else Console.WriteLine($"\n({found} bytes)");
    }

    private static void Steps(Recording r, string path, List<uint> want, long tol)
    {
        Console.WriteLine($"{Path.GetFileName(path)} ({r.H.Game}) - bytes whose ONLY changes were at " +
                          string.Join(", ", want.Select(f => "f" + f)) + $" (+-{tol} frames):");
        Console.WriteLine();

        int found = 0;
        for (int i = 0; i < r.Tracks.Length; i++)
        {
            var t = r.Tracks[i];
            if (t.Noisy || t.Trans == null) continue;

            // Whatever the byte did before the first round is not our business:
            // the attract mode plays a demo fight, with its own rounds and its
            // own wins, and the game resets the counter when the real match
            // starts. Only the tail has to match.
            byte before = t.First;
            int idx = 0;
            var tr = t.Trans.Select(e => (f: (long)r.Frames[e >> 8], v: (byte)(e & 0xFF))).ToList();
            while (idx < tr.Count && tr[idx].f < want[0] - tol) { before = tr[idx].v; idx++; }

            var tail = tr.Skip(idx).ToList();
            if (tail.Count != want.Count) continue;

            bool ok = true;
            for (int k = 0; k < want.Count; k++)
                if (Math.Abs(tail[k].f - want[k]) > tol) { ok = false; break; }
            if (!ok) continue;

            var sb = new StringBuilder($"  0x{r.AddrOf(i):X6}   {before}");
            foreach (var s in tail) sb.Append($" -> {s.v}");
            if (idx > 0) sb.Append($"   ({idx} antes do 1o round)");
            Console.WriteLine(sb.ToString());
            found++;
        }

        if (found == 0) Console.WriteLine("  none - try a wider tolerance, or a different set of moments.");
        else Console.WriteLine($"\n({found} bytes)");
    }

    // ---------------------------------------------------------------------
    //  The shipping rule
    // ---------------------------------------------------------------------

    // One entry per game we can read. Addresses are CPU addresses; life is a
    // signed 16-bit big-endian word and a side has lost when its own word goes
    // negative. Characters are one byte, or three in a row for a KOF team, and
    // are only meaningful once the fight has started.
    private sealed class GameMap
    {
        public string Game;
        public uint LifeP1, LifeP2;
        public uint CharP1, CharP2;   // 0 = not known for this game
        public int  CharCount = 1;
        public int  Full;
        // vsav has no rounds: one gauge worth two 144-unit bars, no refill, no
        // round break. The score there is how many bars each side lost.
        public bool Bars;
    }

    private static readonly GameMap[] Maps =
    {
        new GameMap { Game = "sf2ce", LifeP1 = 0xFF83E8, LifeP2 = 0xFF86E8,
                      CharP1 = 0xFF83D9, CharP2 = 0xFF86D9, Full = 0x90 },
        new GameMap { Game = "sfa2",  LifeP1 = 0xFF8450, LifeP2 = 0xFF8850,
                      CharP1 = 0xFF8482, CharP2 = 0xFF8882, Full = 0x90 },
        new GameMap { Game = "vsav",  LifeP1 = 0xFF8450, LifeP2 = 0xFF8850,
                      CharP1 = 0xFF841D, CharP2 = 0,       Full = 0x120, Bars = true },
        new GameMap { Game = "kof98", LifeP1 = 0x108238, LifeP2 = 0x108438,
                      CharP1 = 0x10A84E, CharP2 = 0x10A85F, CharCount = 3, Full = 0x67 },
    };

    private static int Life(Header h, byte[] ram, uint a)
        => (short)((ram[h.IndexOf(a)] << 8) | ram[h.IndexOf(a + 1)]);

    private static string Who(Header h, byte[] ram, uint a, int n)
    {
        if (a == 0) return "?";
        var v = new List<string>();
        for (uint k = 0; k < n; k++) v.Add(ram[h.IndexOf(a + k)].ToString());
        return string.Join("/", v);
    }

    private static void Score(string path)
    {
        var h = ReadHeader(path);
        var m = Maps.FirstOrDefault(x => x.Game == h.Game);
        if (m == null)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: no map for {h.Game}.");
            return;
        }

        int p1Won = 0, p2Won = 0;          // rounds won, i.e. the OTHER side died
        bool p1Down = false, p2Down = false;
        int p1Low = int.MaxValue, p2Low = int.MaxValue;
        string c1 = "?", c2 = "?";
        bool started = false;
        uint startFrame = 0, endFrame = 0;

        // A recording can hold a whole session, and a session is a series of
        // games with the characters picked again between them. So the rounds
        // above belong to the CURRENT game, and each finished one is closed off
        // into this list - the same split match_score.cpp makes, which is the
        // point of keeping this tool in step with it.
        var games = new List<(string C1, string C2, int R1, int R2, uint From, uint To)>();
        int p1Games = 0, p2Games = 0;
        bool matchOver = false;

        void CloseGame(uint frame)
        {
            int r1 = p1Won, r2 = p2Won;
            if (m.Bars)
            {
                r1 = p2Down ? 2 : (p2Low <= m.Full / 2 ? 1 : 0);
                r2 = p1Down ? 2 : (p1Low <= m.Full / 2 ? 1 : 0);
            }
            if (r1 > r2) p1Games++; else if (r2 > r1) p2Games++;
            games.Add((c1, c2, r1, r2, startFrame, frame));

            p1Won = p2Won = 0;
            p1Down = p2Down = false;
            p1Low = p2Low = int.MaxValue;
            c1 = c2 = "?";           // both sides pick again
            started = false;
            startFrame = endFrame = 0;
        }

        Walk(path, (sample, frame, ram) =>
        {
            int l1 = Life(h, ram, m.LifeP1);
            int l2 = Life(h, ram, m.LifeP2);

            // Two full bars at once happens nowhere but the start of a fight.
            if (!started && l1 == m.Full && l2 == m.Full)
            {
                started = true;
                startFrame = frame;
            }
            if (!started) return;

            // Read the characters on every frame where both sides are alive,
            // and keep the last such reading. Capturing once at the start is
            // wrong twice over: sf2ce fills the field a moment AFTER the bars
            // go full, and the arcade writes the NEXT opponent into it as soon
            // as the match is over. Between those two, the field is stable.
            if (l1 > 0 && l2 > 0)
            {
                c1 = Who(h, ram, m.CharP1, m.CharCount);
                c2 = Who(h, ram, m.CharP2, m.CharCount);
            }

            // Zero is the struct being cleared at the end of the match, not a
            // life total - counting it as a low told us a player who finished
            // untouched had lost a bar.
            if (l1 > 0 && l1 < p1Low) p1Low = l1;
            if (l2 > 0 && l2 < p2Low) p2Low = l2;

            // Edge, not level: the value sits negative for the whole knockout
            // animation. It only counts again once that side has life back -
            // and "back" means above zero, because zero is the struct being
            // cleared when the match is over, not a new round.
            if (l1 < 0) { if (!p1Down) { p1Down = true; p2Won++; endFrame = frame; } }
            else if (l1 > 0) p1Down = false;

            if (l2 < 0) { if (!p2Down) { p2Down = true; p1Won++; endFrame = frame; } }
            else if (l2 > 0) p2Down = false;

            // Both words cleared is the match being over, which is a different
            // event from a round ending - there the loser goes negative and
            // then refills. This is where a GAME is awarded, and it is why the
            // rule is not "first to two": a double KO can take a match past two
            // rounds, and five matches each won by a single round would
            // otherwise read as two games won.
            if (l1 == 0 && l2 == 0)
            {
                if (!matchOver) { matchOver = true; CloseGame(frame); }
            }
            else matchOver = false;
        });

        // A recording that stops mid-game still has a game worth reporting.
        if (started && (p1Won > 0 || p2Won > 0)) CloseGame(endFrame);

        if (games.Count == 0)
        {
            Console.WriteLine($"{Path.GetFileName(path)} ({h.Game}): no fight in this recording.");
            return;
        }

        string verdict = p1Games > p2Games ? "P1 venceu" : p2Games > p1Games ? "P2 venceu" : "empate";
        Console.WriteLine($"{Path.GetFileName(path),-42} {h.Game,-6} " +
                          $"{p1Games} x {p2Games}   {verdict}   ({games.Count} partida(s))");
        for (int i = 0; i < games.Count; i++)
        {
            var g = games[i];
            string w = g.R1 > g.R2 ? "P1" : g.R2 > g.R1 ? "P2" : "empate";
            Console.WriteLine($"    {i + 1,2}. P1[{g.C1}] {g.R1} x {g.R2} P2[{g.C2}]   {w,-6} " +
                              $"(f{g.From}..f{g.To})");
        }
    }

    private static void Activity(string path)
    {
        byte[] prev = null;
        var rows = new List<(uint frame, int changed)>();

        var h = Walk(path, (sample, frame, ram) =>
        {
            if (prev == null) { prev = (byte[])ram.Clone(); return; }
            int n = 0;
            for (int i = 0; i < ram.Length; i++) if (ram[i] != prev[i]) n++;
            rows.Add((frame, n));
            Array.Copy(ram, prev, ram.Length);
        });

        double avg = rows.Count > 0 ? rows.Average(r => r.changed) : 0;
        Console.WriteLine($"{Path.GetFileName(path)} ({h.Game}) - bytes changed since the previous sample");
        Console.WriteLine($"average {avg:F0}. Bars are relative to the busiest sample.");
        Console.WriteLine();

        int max = rows.Count > 0 ? rows.Max(r => r.changed) : 1;
        foreach (var (frame, changed) in rows)
        {
            int bar = max > 0 ? changed * 48 / max : 0;
            string mark = changed > avg * 2.5 ? " <<<" : "";
            Console.WriteLine($"  f{frame,-6} {frame / 60,4}s {changed,6}  {new string('#', bar)}{mark}");
        }
    }

    private static void Dump(string path, uint addr, int width)
    {
        byte[] prev = null;

        var h = ReadHeader(path);
        if (h.IndexOf(addr) < 0 || h.IndexOf(addr + (uint)width - 1) >= h.RamLen)
            throw new ArgumentException($"0x{addr:X6}+{width} is outside this recording.");

        Console.WriteLine($"0x{addr:X6}..0x{addr + width - 1:X6} in {Path.GetFileName(path)} ({h.Game}), " +
                          "one line per change:");
        Walk(path, (sample, frame, ram) =>
        {
            var slice = new byte[width];
            for (int k = 0; k < width; k++) slice[k] = ram[h.IndexOf(addr + (uint)k)];
            if (prev != null && slice.AsSpan().SequenceEqual(prev)) return;
            Console.WriteLine($"  f{frame,-8} " + string.Join(" ", slice.Select(b => b.ToString("X2"))));
            prev = slice;
        });
    }

    // ---------------------------------------------------------------------
    //  Character ids, from two runs
    // ---------------------------------------------------------------------

    private static void Chars(Recording a, string pathA, Recording b, string pathB)
    {
        if (a.H.Game != b.H.Game)
            Console.WriteLine($"!! these are different games ({a.H.Game} vs {b.H.Game}) - the result will be noise.");
        if (a.H.CpuBase != b.H.CpuBase || a.H.RamLen != b.H.RamLen)
            throw new InvalidDataException("the two recordings do not describe the same memory.");

        Console.WriteLine($"{Path.GetFileName(pathA)}  vs  {Path.GetFileName(pathB)}   ({a.H.Game})");
        Console.WriteLine("bytes that never moved in either run, but hold a different value in each -");
        Console.WriteLine("which is what a character id looks like when you play two different characters:");
        Console.WriteLine();

        var hits = new List<uint>();
        for (int i = 0; i < a.Tracks.Length; i++)
        {
            var ta = a.Tracks[i];
            var tb = b.Tracks[i];
            if (ta.Changes != 0 || tb.Changes != 0) continue;
            if (ta.First == tb.First) continue;
            if (ta.First > 63 || tb.First > 63) continue;   // rosters are small
            hits.Add(a.AddrOf(i));
        }
        hits.Sort();

        if (hits.Count == 0)
        {
            Console.WriteLine("  none. If the two runs used the same character this is the expected answer;");
            Console.WriteLine("  otherwise the id may live somewhere that also holds live state.");
            return;
        }

        // Print as runs of consecutive addresses. A fighting game keeps the two
        // players in parallel structures, so the answer usually shows up as two
        // short runs a fixed distance apart, and that shape is the confirmation.
        int shown = 0;
        for (int k = 0; k < hits.Count && shown < 400; )
        {
            uint start = hits[k];
            int len = 1;
            while (k + len < hits.Count && hits[k + len] == start + (uint)len) len++;

            var sb = new StringBuilder($"  0x{start:X6}");
            if (len > 1) sb.Append($"..0x{start + (uint)len - 1:X6}");
            sb.Append("   ");
            for (int j = 0; j < len && j < 16; j++)
            {
                int idx = a.IndexOf(start + (uint)j);
                sb.Append($"{a.Tracks[idx].First:X2}/{b.Tracks[idx].First:X2} ");
            }
            Console.WriteLine(sb.ToString());

            k += len; shown += len;
        }
        if (hits.Count > shown) Console.WriteLine($"  ... and {hits.Count - shown} more bytes");

        Console.WriteLine();
        Console.WriteLine($"({hits.Count} bytes in total. Values are shown as {Path.GetFileName(pathA)}/{Path.GetFileName(pathB)}.)");
    }
}

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
    public uint   ChunkSize;
    public uint   Interval;
    public string Game = "";
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

    public uint AddrOf(int index) => H.CpuBase + (uint)index;
    public int  IndexOf(uint addr) => (int)(addr - H.CpuBase);
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
        br.ReadBytes(32);   // reserved

        if (h.Version != 1) throw new InvalidDataException($"recording version {h.Version} is newer than this tool.");
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

    private static bool LooksLikeCounter(Track t)
    {
        if (t.Noisy || t.Trans == null) return false;
        if (t.Changes < 1 || t.Changes > 12) return false;
        if (t.First != 0) return false;      // a score starts at zero
        if (t.Max > 6) return false;          // best-of-five is as far as these games go

        byte prev = t.First;
        foreach (int e in t.Trans)
        {
            byte v = (byte)(e & 0xFF);
            // Up by one, or back to zero for the next match. Anything else is
            // some other kind of byte that happens to stay small.
            if (v != prev + 1 && v != 0) return false;
            prev = v;
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
        int off = (int)(addr - h.CpuBase);
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

    private static void Dump(string path, uint addr, int width)
    {
        byte[] prev = null;

        var h = ReadHeader(path);
        int off = (int)(addr - h.CpuBase);
        if (off < 0 || off + width > h.RamLen)
            throw new ArgumentException($"0x{addr:X6}+{width} is outside this recording.");

        Console.WriteLine($"0x{addr:X6}..0x{addr + width - 1:X6} in {Path.GetFileName(path)} ({h.Game}), " +
                          "one line per change:");
        Walk(path, (sample, frame, ram) =>
        {
            var slice = new byte[width];
            Array.Copy(ram, off, slice, 0, width);
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
                int idx = a.IndexOf(start) + j;
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

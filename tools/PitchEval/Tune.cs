// Neo Tune on real singing (docs/specs/neo-tune.md §9): the machine's engine (Machines/NeoTune/Engine, linked) run over a VocalSet subset in
// 256-sample blocks. For each recording and setting: the pitch of the output (the shared tracker, Voice Quality) against the nearest note
// of the scale, before and after; how much of the voiced singing stays voiced and how much of the rest stays unvoiced; and an artefact
// proxy (spectral flux of the output against the input, and the rate of sudden high-band rises that the input does not have, which is
// what a bad grain join sounds like). Six short excerpts, before and after, are written for listening (VocalSet is CC BY 4.0: the
// README beside them carries the attribution). Nothing is committed; the excerpts are uploaded as a workflow artifact only.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using NeoBuzz.Dsp;
using NeoBuzz.Tune;

namespace NeoBuzz.PitchEval
{
    internal static class TuneEval
    {
        sealed class Setting
        {
            public string Name; public double Natural;
            public int Range;          // 0 Low latency, 1 Quality
            public bool Major;         // major, the key listened for (Key from Listen; scales and arpeggios only), else chromatic
        }

        static readonly Setting[] Settings =
        {
            new() { Name = "Natural 0 (hard effect), Quality, chromatic", Natural = 0.0, Range = 1 },
            new() { Name = "Natural 0 (hard effect), Low latency, chromatic", Natural = 0.0, Range = 0 },
            new() { Name = "Natural 50, Quality, chromatic", Natural = 0.5, Range = 1 },
            new() { Name = "Natural 100 (transparent), Quality, chromatic", Natural = 1.0, Range = 1 },
            new() { Name = "Natural 0 (hard effect), Quality, major, key listened for", Natural = 0.0, Range = 1, Major = true },
        };

        sealed class Acc
        {
            public int Files;
            public double Seconds;
            public long Frames;            // voiced input frames, active, measured on both
            public long BothVoiced;
            public double AbsIn, AbsOut;
            public long In10, Out10, In25, Out25;
            public List<double> DevIn = new(), DevOut = new();
            public long InVoiced, OutVoicedOfIn, InUnvoiced, OutVoicedOfUnvoiced;
            public double FluxIn, FluxOut; public long FluxN;
            public List<double> FluxInAll = new(), FluxOutAll = new();
            public double ClicksIn, ClicksOut;   // per minute, summed over seconds (weighted below)
            public long ClickIn, ClickOut; public double ClickSeconds;
        }

        // ------------------------------------------------------------------------------------------------ settings

        static void Apply(ParamValues v, Setting s, int scale, int root, bool listen = false)
        {
            v.Set(P.Amount, Params.Table[P.Amount].Raw(100));
            TuneNatural.Apply(v, s.Natural);
            v.Set(P.Range, s.Range);
            v.Set(P.Scale, scale);
            v.Set(P.Key, root);
            v.Set(P.Source, listen ? 1 : 0);
        }

        static float[] RenderTune(float[] x, int rate, Setting s, int scale, int root, out int latency, out int listened)
        {
            var v = new ParamValues(Params.Table);
            Apply(v, s, scale, root, s.Major);
            var e = new TuneEngine(v);
            e.SetSampleRate(rate);
            latency = e.Latency;
            int total = x.Length + latency + 256;
            var o = new float[x.Length];
            var l = new float[256];
            var r = new float[256];
            int written = 0;
            for (int pos = 0; pos < total; pos += 256)
            {
                int n = Math.Min(256, total - pos);
                for (int i = 0; i < n; i++) l[i] = r[i] = pos + i < x.Length ? x[pos + i] : 0f;
                e.Process(l, r, n);
                // The output sample (pos + i) is the input of sample (pos + i - latency).
                for (int i = 0; i < n; i++)
                {
                    int at = pos + i - latency;
                    if (at >= 0 && at < o.Length) { o[at] = l[i]; written++; }
                }
            }
            listened = e.Root;
            return o;
        }

        // ------------------------------------------------------------------------------------------------ measuring

        const float ActiveRms = 0.00316f;

        static Acc Measure(float[] x, float[] y, int rate, int mask)
        {
            var a = new Acc { Files = 1, Seconds = x.Length / (double)rate };
            var qi = Track.Run(x, rate, PitchRange.Voice, PitchQuality.Quality);
            var qo = Track.Run(y, rate, PitchRange.Voice, PitchQuality.Quality);
            // Per input estimate, the output estimate at the same moment.
            for (int i = 0; i < qi.Count; i++)
            {
                if (qi.Rms[i] <= ActiveRms) continue;
                int j = qo.Nearest(qi.T[i]);
                bool outVoiced = j >= 0 && Math.Abs(qo.T[j] - qi.T[i]) < 0.004 && qo.Voiced[j];
                if (qi.Voiced[i])
                {
                    a.InVoiced++;
                    if (outVoiced) a.OutVoicedOfIn++;
                }
                else
                {
                    a.InUnvoiced++;
                    if (outVoiced) a.OutVoicedOfUnvoiced++;
                }
                if (!qi.Voiced[i] || !outVoiced) continue;
                a.BothVoiced++;
                double mi = 69 + 12 * Math.Log2(qi.F0[i] / 440.0), mo = 69 + 12 * Math.Log2(qo.F0[j] / 440.0);
                int ni = TuneScales.Nearest(mi, mask), no = TuneScales.Nearest(mo, mask);
                double di = Math.Abs(mi - ni) * 100, dOut = Math.Abs(mo - no) * 100;
                a.AbsIn += di; a.AbsOut += dOut;
                if (di <= 10) a.In10++; if (dOut <= 10) a.Out10++;
                if (di <= 25) a.In25++; if (dOut <= 25) a.Out25++;
                a.DevIn.Add(di); a.DevOut.Add(dOut);
            }
            Flux(x, y, rate, qi, a);
            return a;
        }

        /// <summary>Spectral flux (mean absolute dB change of the 100 Hz - 8 kHz log spectrum between frames of 1024, hop 256) and the rate of
        /// high-band (4 - 10 kHz) rises over 12 dB in one hop, over the frames where the input is active.</summary>
        static void Flux(float[] x, float[] y, int rate, Track qi, Acc a)
        {
            const int N = 1024, Hop = 256;
            var fft = new ComplexFft(N);
            var win = new float[N];
            for (int i = 0; i < N; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
            int lo = (int)(100.0 * N / rate), hi = Math.Min(N / 2, (int)(8000.0 * N / rate));
            int hlo = (int)(4000.0 * N / rate), hhi = Math.Min(N / 2, (int)(10000.0 * N / rate));
            int frames = (x.Length - N) / Hop;
            if (frames < 3) return;
            var re = new float[N]; var im = new float[N];
            float[] PrevX = null, PrevY = null; double hbX = 0, hbY = 0;
            for (int f = 0; f < frames; f++)
            {
                double rms = 0;
                for (int i = 0; i < N; i++) rms += x[f * Hop + i] * x[f * Hop + i];
                bool active = Math.Sqrt(rms / N) > ActiveRms;
                var sx = Spectrum(x, f * Hop, N, win, fft, re, im, lo, hi, hlo, hhi, out double hx);
                var sy = Spectrum(y, f * Hop, N, win, fft, re, im, lo, hi, hlo, hhi, out double hy);
                if (PrevX != null && active)
                {
                    double fx = 0, fy = 0;
                    for (int k = 0; k < sx.Length; k++) { fx += Math.Abs(sx[k] - PrevX[k]); fy += Math.Abs(sy[k] - PrevY[k]); }
                    fx /= sx.Length; fy /= sy.Length;
                    a.FluxIn += fx; a.FluxOut += fy; a.FluxN++;
                    a.FluxInAll.Add(fx); a.FluxOutAll.Add(fy);
                    a.ClickSeconds += Hop / (double)rate;
                    if (hx - hbX > 12) a.ClickIn++;
                    if (hy - hbY > 12) a.ClickOut++;
                }
                PrevX = sx; PrevY = sy; hbX = hx; hbY = hy;
            }
        }

        static float[] Spectrum(float[] s, int at, int n, float[] win, ComplexFft fft, float[] re, float[] im, int lo, int hi, int hlo, int hhi, out double highDb)
        {
            for (int i = 0; i < n; i++) { re[i] = s[at + i] * win[i]; im[i] = 0; }
            fft.Transform(re, im, false);
            var o = new float[hi - lo];
            for (int k = lo; k < hi; k++) o[k - lo] = (float)(10 * Math.Log10(re[k] * re[k] + im[k] * im[k] + 1e-10));
            double e = 0;
            for (int k = hlo; k < hhi; k++) e += re[k] * re[k] + im[k] * im[k];
            highDb = 10 * Math.Log10(e + 1e-10);
            return o;
        }

        // ------------------------------------------------------------------------------------------------ the run

        public static void Run(string zipPath, int perGroup, string excerptDir, StringBuilder md)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var all = VocalSet.Select(zip, perGroup);
            // The techniques a corrector is used on: held and moving notes, with and without vibrato, and the awkward voices.
            string[] groups = { "long tones, straight", "scales, straight", "arpeggios, straight", "arpeggios, vibrato", "arpeggios, breathy", "arpeggios, vocal fry", "excerpts, straight" };
            var selected = all.Where(t => groups.Contains(t.Group)).ToList();
            Console.WriteLine($"Neo Tune: {selected.Count} files");
            var accs = new Dictionary<(string Setting, string Group), Acc>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int Batch = 16;
            for (int b = 0; b < selected.Count; b += Batch)
            {
                var chunk = selected.Skip(b).Take(Batch).ToList();
                var bytes = new byte[chunk.Count][];
                for (int i = 0; i < chunk.Count; i++)
                {
                    using var s = chunk[i].E.Open();
                    var ms = new MemoryStream((int)chunk[i].E.Length);
                    s.CopyTo(ms);
                    bytes[i] = ms.ToArray();
                }
                var res = new (Setting S, string Group, Acc A)[chunk.Count][];
                Parallel.For(0, chunk.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, i =>
                {
                    var (x, rate) = Wav.Read(new MemoryStream(bytes[i]));
                    // 20 s at most per file: the rest adds nothing but time.
                    if (x.Length > rate * 20) Array.Resize(ref x, rate * 20);
                    var list = new List<(Setting, string, Acc)>();
                    foreach (var s in Settings)
                    {
                        int scale = 0;
                        if (s.Major)
                        {
                            if (!(chunk[i].Group.StartsWith("scales") || chunk[i].Group.StartsWith("arpeggios"))) continue;
                            scale = 1;
                        }
                        var y = RenderTune(x, rate, s, scale, 0, out _, out int root);
                        // The scale the output is held to is the one on the key the machine listened for (a scale or an arpeggio has one key).
                        list.Add((s, chunk[i].Group, Measure(x, y, rate, TuneScales.Mask(scale, Math.Max(0, root), 0))));
                    }
                    res[i] = list.ToArray();
                });
                foreach (var r in res)
                    foreach (var (s, g, a) in r)
                    {
                        foreach (var k in new[] { (s.Name, g), (s.Name, "**all**") })
                        {
                            if (!accs.TryGetValue(k, out var t)) accs[k] = t = new Acc();
                            Merge(t, a);
                        }
                    }
                if ((b / Batch) % 5 == 0) Console.WriteLine($"  {Math.Min(b + Batch, selected.Count)}/{selected.Count} files, {sw.Elapsed.TotalSeconds:F0} s");
            }

            TuneGates.Run(selected, md);
            md.AppendLine("## Neo Tune on VocalSet (pitch error, voicing, flux; NT2 at Natural 0, 50 and 100)");
            md.AppendLine();
            md.AppendLine($"{selected.Count} recordings from VocalSet 1.1 (the subset of the techniques below, at most 20 s each), through the engine of Neo Tune as the machine runs it (256-sample blocks, 44.1 kHz material as recorded). The output is shifted back by the reported latency and its pitch measured with the shared tracker (Voice range, Quality). **Error** is the distance in cents of the pitch from the nearest note of the scale, over the frames where the input is voiced and active and the output is voiced too; \"before\" is the same measure on the input. The settings use the scale named: chromatic (the nearest semitone, the strictest and the one that does not depend on the key), or major on the key the machine listened for (Key from Listen; scales and arpeggios, whose notes keep to one key). **Within 10 / 25 ct** are the shares of frames inside that distance. **Voiced kept** is the share of voiced, active input frames that are voiced in the output; **false voiced** is the share of unvoiced, active input frames that the output calls voiced (breath and consonants must pass through as they were). **Artefact proxy**: the spectral flux (mean change in dB of the 100 Hz - 8 kHz spectrum from one 5.8 ms hop to the next, frames of 1024) of the output against the input over the active frames, and **clicks**, the rate per minute of one-hop rises of the 4 - 10 kHz band over 12 dB: a bad grain join shows as flux and clicks the input does not have.");
            md.AppendLine();
            foreach (var s in Settings)
            {
                md.AppendLine($"### {s.Name}");
                md.AppendLine();
                md.AppendLine("| Technique | files | frames | error before (median / mean ct) | error after (median / mean ct) | within 10 ct before > after | within 25 ct before > after | voiced kept | false voiced | flux in / out (dB) | flux p99 in / out | clicks /min in / out |");
                md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
                foreach (var g in groups.Append("**all**"))
                    if (accs.TryGetValue((s.Name, g), out var a)) md.AppendLine(Row(g, a));
                md.AppendLine();
            }

            // The excerpts.
            if (excerptDir != null) Excerpts(zip, selected, excerptDir, md);
        }

        static void Merge(Acc t, Acc a)
        {
            t.Files += a.Files; t.Seconds += a.Seconds; t.BothVoiced += a.BothVoiced;
            t.AbsIn += a.AbsIn; t.AbsOut += a.AbsOut; t.In10 += a.In10; t.Out10 += a.Out10; t.In25 += a.In25; t.Out25 += a.Out25;
            t.DevIn.AddRange(a.DevIn); t.DevOut.AddRange(a.DevOut);
            t.InVoiced += a.InVoiced; t.OutVoicedOfIn += a.OutVoicedOfIn; t.InUnvoiced += a.InUnvoiced; t.OutVoicedOfUnvoiced += a.OutVoicedOfUnvoiced;
            t.FluxIn += a.FluxIn; t.FluxOut += a.FluxOut; t.FluxN += a.FluxN; t.FluxInAll.AddRange(a.FluxInAll); t.FluxOutAll.AddRange(a.FluxOutAll);
            t.ClickIn += a.ClickIn; t.ClickOut += a.ClickOut; t.ClickSeconds += a.ClickSeconds;
        }

        static string Row(string name, Acc a)
        {
            double n = Math.Max(1, a.BothVoiced);
            string Share(long x) => Fmt.P(x / n);
            return $"| {name} | {a.Files} | {a.BothVoiced} | {Fmt.F(Fmt.Median(a.DevIn))} / {Fmt.F(a.AbsIn / n)} | {Fmt.F(Fmt.Median(a.DevOut))} / {Fmt.F(a.AbsOut / n)} | {Share(a.In10)} > {Share(a.Out10)} | {Share(a.In25)} > {Share(a.Out25)} | {Fmt.P(a.OutVoicedOfIn / (double)Math.Max(1, a.InVoiced))} | {Fmt.P(a.OutVoicedOfUnvoiced / (double)Math.Max(1, a.InUnvoiced))} | {Fmt.F(a.FluxIn / Math.Max(1, a.FluxN), "F2")} / {Fmt.F(a.FluxOut / Math.Max(1, a.FluxN), "F2")} | {Fmt.F(Fmt.Pct(a.FluxInAll, 0.99), "F1")} / {Fmt.F(Fmt.Pct(a.FluxOutAll, 0.99), "F1")} | {Fmt.F(a.ClickIn * 60 / Math.Max(1e-9, a.ClickSeconds), "F1")} / {Fmt.F(a.ClickOut * 60 / Math.Max(1e-9, a.ClickSeconds), "F1")} |";
        }

        static void Excerpts(ZipArchive zip, List<(ZipArchiveEntry E, string Group, string Singer, string Vowel)> selected, string dir, StringBuilder md)
            => TuneGates.Excerpts(zip, selected, dir, md);

        internal static void WriteWav(string path, float[] x, int rate)
        {
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            int bytes = x.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (var f in x) w.Write((short)Math.Clamp((int)Math.Round(f * 32767.0), -32768, 32767));
        }
    }
}

using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Prism;
using static NeoBuzz.MachineTests.PrismRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Prism listening pack (docs/specs/neo-prism.md): every factory preset on one four-bar drum loop (Neo Drums) with a four-chord loop (Neo Poly)
    /// under it at 120 BPM, played twice (8 bars, 16 s), written as 16-bit 48 kHz WAV files with a README. The engine's latency is taken out
    /// (a host compensates it), the song runs from beat 0. Local only, never committed: run it by hand with NEO_PRISM_PACK set to the output folder.
    ///   NEO_PRISM_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~NeoPrismPack"
    /// </summary>
    [TestFixture]
    public class NeoPrismPack
    {
        static ParamValues PresetValues(ParamDef[] table, string machine, string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == machine && p.Preset == preset);
            var v = new ParamValues(table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static (float[] L, float[] R) Run(Action<float[], float[], int> process, int length, Action<int> atBlock)
        {
            var ol = new float[length];
            var or = new float[length];
            var l = new float[256];
            var r = new float[256];
            for (int pos = 0; pos < length; pos += 256)
            {
                atBlock(pos);
                int n = Math.Min(256, length - pos);
                Array.Clear(l, 0, 256);
                Array.Clear(r, 0, 256);
                process(l, r, n);
                Array.Copy(l, 0, ol, pos, n);
                Array.Copy(r, 0, or, pos, n);
            }
            return (ol, or);
        }

        const int Bars = 8;
        static readonly int StepSamples = (int)(Spb(120) / 4);          // a sixteenth
        static readonly int Length = (int)(Spb(120) * 4 * Bars);

        /// <summary>Neo Drums: a four-bar pattern played twice.</summary>
        static (float[], float[]) DrumsSource()
        {
            var v = PresetValues(NeoBuzz.Drums.Params.Table, "Neo Drums", "909 — Classic");
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            return Run((l, r, n) => e.Process(l, r, n), Length, pos =>
            {
                for (int s = 0; s < Bars * 16; s++)
                {
                    int at = s * StepSamples;
                    if (at < pos || at >= pos + 256) continue;
                    int b = s % 16, bar = (s / 16) % 4;
                    if (b == 0 || b == 10 || b == 8 || (b == 13 && bar % 2 == 1)) e.NoteOn(0, 36, b == 0 ? 0.95f : 0.8f);
                    if (b == 4 || b == 12) e.NoteOn(1, 38, 0.9f);
                    if (b % 2 == 0) e.NoteOn(2, 42, b % 4 == 0 ? 0.8f : 0.5f);
                    if (b == 14) e.NoteOn(3, 46, 0.6f);
                }
            });
        }

        /// <summary>Neo Poly: Am, F, C, G, a bar each, played twice.</summary>
        static (float[], float[]) ChordSource()
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Pad — Warm Strings");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[][] chords = { new[] { 45, 52, 57, 60 }, new[] { 41, 48, 57, 60 }, new[] { 48, 52, 55, 60 }, new[] { 43, 50, 55, 59 } };
            int bar = (int)(Spb(120) * 4);
            return Run((l, r, n) => e.Process(l, r, n), Length, pos =>
            {
                for (int c = 0; c < Bars; c++)
                {
                    if (c * bar >= pos && c * bar < pos + 256)
                        for (int k = 0; k < 4; k++) e.NoteOn(k, chords[c % 4][k], 0.7f);
                    int off = c * bar + (int)(bar * 0.9);
                    if (off >= pos && off < pos + 256)
                        for (int k = 0; k < 4; k++) e.NoteOff(k);
                }
            });
        }

        static void WriteWav(string path, float[] l, float[] r)
        {
            using var f = File.Create(path);
            using var w = new BinaryWriter(f);
            int n = l.Length, bytes = n * 4;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            for (int i = 0; i < n; i++)
            {
                w.Write((short)Math.Clamp((int)Math.Round(l[i] * 32767.0), -32768, 32767));
                w.Write((short)Math.Clamp((int)Math.Round(r[i] * 32767.0), -32768, 32767));
            }
        }

        /// <summary>Each preset's comment from Presets.txt (what the preset does), found by walking up from the test folder.</summary>
        static Dictionary<string, string> Comments
        {
            get
            {
                var map = new Dictionary<string, string>();
                string dir = TestContext.CurrentContext.TestDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "NeoPrism", "Presets.txt"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) return map;
                string name = null;
                foreach (var line in File.ReadAllLines(Path.Combine(dir, "Machines", "NeoPrism", "Presets.txt")))
                {
                    if (line.StartsWith("preset \"")) name = line.Substring(8).TrimEnd('"');
                    else if (name != null && line.StartsWith("comment \"")) map[name] = line.Substring(9).TrimEnd('"');
                }
                return map;
            }
        }

        static string Slug(string s) =>
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        /// <summary>The pack's song (drums plus pad) turned to <paramref name="peakDb"/> dBFS.</summary>
        static (float[] L, float[] R) Song(double peakDb)
        {
            var (dl, dr) = DrumsSource();
            var (cl, cr) = ChordSource();
            var l = new float[Length];
            var r = new float[Length];
            double peak = 1e-9;
            for (int i = 0; i < Length; i++)
            {
                l[i] = dl[i] + 0.45f * cl[i]; r[i] = dr[i] + 0.45f * cr[i];
                peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            }
            float g = (float)(Math.Pow(10, peakDb / 20) / peak);
            for (int i = 0; i < Length; i++) { l[i] *= g; r[i] *= g; }
            return (l, r);
        }

        static double PeakDb(float[] a, float[] b, int from, int to)
        {
            double pk = 1e-12;
            for (int i = from; i < to; i++) pk = Math.Max(pk, Math.Max(Math.Abs(a[i]), Math.Abs(b[i])));
            return 20 * Math.Log10(pk);
        }

        static double RmsDb(float[] a, float[] b, int from, int to)
        {
            double sq = 0;
            for (int i = from; i < to; i++) sq += (double)a[i] * a[i] + (double)b[i] * b[i];
            return 10 * Math.Log10(sq / (2.0 * (to - from)) + 1e-30);
        }

        /// <summary>The two Scramble presets used to turn a -3 dBFS song into +2.2 dBFS (preset 03): now they stay under -1 dBFS, and 03 keeps its level.</summary>
        [Test]
        public void TheScramblePresetsPeakUnderMinusOneDbfsOnThePackSong()
        {
            var (l, r) = Song(-3.0);
            var lat = 4095;
            foreach (var (name, rmsBefore) in new[] { ("Scramble — Bin Shuffle Glitch", -23.56), ("Scramble — Robot Scramble", double.NaN) })
            {
                var p = PresetTests.AllPresets().First(x => x.Machine == "Neo Prism" && x.Preset == name);
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var e = Engine(v);
                var (wl, wr) = Render(e, l, r);
                double pk = PeakDb(wl, wr, 0, Length), rms = RmsDb(wl, wr, lat, Length);
                TestContext.Out.WriteLine($"{name}: peak {pk:0.00} dBFS, rms {rms:0.00} dBFS (before the fix: rms {rmsBefore:0.00})");
                Assert.That(pk, Is.LessThanOrEqualTo(-1.0), name);
                if (!double.IsNaN(rmsBefore)) Assert.That(Math.Abs(rms - rmsBefore), Is.LessThan(2.0), name + " keeps its level");
            }
        }

        /// <summary>Any Scramble setting at Amount 100 % on full-scale music: the peak stays within 1 dB of the input peak.</summary>
        [TestCase(2, 20.0, "Free", 3)]
        [TestCase(16, 8.0, "Free", 1)]
        [TestCase(64, 5.0, "1/8", 11)]
        [TestCase(6, 50.0, "1/16", 5)]
        public void AnyScrambleSettingKeepsTheSongsPeak(int width, double rate, string sync, int seed)
        {
            var (l, r) = Song(0.0);
            var v = Fresh(q =>
            {
                Switch(q, P.ScrOn, true); Phys(q, P.ScrAmount, 100); Phys(q, P.ScrWidth, width); Choice(q, P.ScrSync, sync);
                Phys(q, P.ScrRate, rate); Phys(q, P.ScrSeed, seed);
            });
            var (wl, wr) = Render(Engine(v), l, r);
            double pin = PeakDb(l, r, 0, Length), pout = PeakDb(wl, wr, 0, Length);
            TestContext.Out.WriteLine($"width {width}, {sync}, {rate} Hz: in {pin:0.00} dBFS, out {pout:0.00} dBFS");
            Assert.That(pout, Is.LessThanOrEqualTo(pin + 1.0));
        }

        /// <summary>The peak guard must not pump: on a steady pad the 50 ms RMS of the scramble's output moves under 1 dB from block to block.</summary>
        [TestCase(2, 20.0), TestCase(16, 8.0), TestCase(64, 3.0)]
        public void TheScrambleDoesNotPumpOnASteadyPad(int width, double rate)
        {
            // A steady, dense pad: a bed of noise with a darker tilt (a one-pole low-pass), so every band holds energy and a shuffle itself
            // does not move the level: any step in the output's level comes from the peak guard.
            int len = Rate * 8;
            var pl = new float[len];
            var pr = new float[len];
            var rng = new Random(4);
            float zl = 0, zr = 0;
            for (int i = 0; i < len; i++)
            {
                zl += 0.3f * ((float)(rng.NextDouble() * 2 - 1) * 0.5f - zl);
                zr += 0.3f * ((float)(rng.NextDouble() * 2 - 1) * 0.5f - zr);
                pl[i] = zl; pr[i] = zr;
            }
            var sv = Fresh(q =>
            {
                Switch(q, P.ScrOn, true); Phys(q, P.ScrAmount, 100); Phys(q, P.ScrWidth, width); Choice(q, P.ScrSync, "Free"); Phys(q, P.ScrRate, rate);
            });
            var (wl, wr) = Render(Engine(sv), pl, pr);
            int blk = Rate / 20;
            double prev = double.NaN, worst = 0;
            for (int s = Rate * 2; s + blk <= len; s += blk)
            {
                double d = RmsDb(wl, wr, s, s + blk);
                if (!double.IsNaN(prev)) worst = Math.Max(worst, Math.Abs(d - prev));
                prev = d;
            }
            TestContext.Out.WriteLine($"width {width}, {rate} Hz: worst 50 ms RMS step {worst:0.00} dB");
            Assert.That(worst, Is.LessThan(1.0));
        }

        [Test, Explicit("writes the listening pack: set NEO_PRISM_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_PRISM_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_PRISM_PACK");
            Directory.CreateDirectory(dir);
            var (dl, dr) = DrumsSource();
            var (cl, cr) = ChordSource();
            var l = new float[Length];
            var r = new float[Length];
            for (int i = 0; i < Length; i++) { l[i] = dl[i] + 0.45f * cl[i]; r[i] = dr[i] + 0.45f * cr[i]; }
            double peak = 1e-9;
            for (int i = 0; i < Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            float g = (float)(Math.Pow(10, -3.0 / 20) / peak);              // -3 dBFS
            for (int i = 0; i < Length; i++) { l[i] *= g; r[i] *= g; }

            int lat = 4095;
            var log = new StringBuilder();
            log.AppendLine("# Neo Prism listening pack");
            log.AppendLine();
            log.AppendLine("Source: a four-bar drum loop (Neo Drums, \"909 — Classic\") with a four-chord loop (Neo Poly, \"Pad — Warm Strings\") under it, 120 BPM in 4/4,");
            log.AppendLine($"played twice ({Bars} bars, {Length / (double)Rate:0.0} s), at -3 dBFS peak before Neo Prism. Every file: 48 kHz, 16-bit, stereo WAV. The song runs from beat 0");
            log.AppendLine($"(the synced sections follow it); Neo Prism's latency ({lat} samples at its default FFT size) is taken out of the files, as a host does. Every preset runs at its own FFT size (the default, 4096).");
            log.AppendLine();
            log.AppendLine("Each file is the machine at one factory preset. Click detector (second difference against its local mean) on every file: clicks the source itself does not have.");
            log.AppendLine();
            log.AppendLine("| File | Preset | What to listen for | Differs from dry | Peak | RMS | Clicks |");
            log.AppendLine("|---|---|---|---|---|---|---|");
            int index = 0, clicksTotal = 0;
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Prism"))
            {
                index++;
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var e = Engine(v);
                var (wl, wr) = Render(e, l, r);
                // The latency out: the file is the output shifted forward by it (the tail past the end is cut).
                var ol = new float[Length]; var or = new float[Length];
                Array.Copy(wl, e.Latency, ol, 0, Length - e.Latency);
                Array.Copy(wr, e.Latency, or, 0, Length - e.Latency);
                var clicks = ClickDetector.Find(ol).Where(c => !ClickDetector.Find(l).Any(o => Math.Abs(o - c) <= 96))
                    .Union(ClickDetector.Find(or).Where(c => !ClickDetector.Find(r).Any(o => Math.Abs(o - c) <= 96))).ToList();
                clicksTotal += clicks.Count;
                double pk = 0;
                for (int i = 0; i < Length; i++) pk = Math.Max(pk, Math.Max(Math.Abs(ol[i]), Math.Abs(or[i])));
                double d = 0, s = 0;
                for (int i = 0; i < Length - e.Latency; i++) { double x = ol[i] - l[i]; d += x * x; s += (double)l[i] * l[i]; }
                double sq = 0;
                for (int i = 0; i < Length; i++) sq += (double)ol[i] * ol[i] + (double)or[i] * or[i];
                double rms = Math.Sqrt(sq / (2.0 * Length));
                string file = $"{index:00}-{Slug(p.Preset)}.wav";
                WriteWav(Path.Combine(dir, file), ol, or);
                log.AppendLine($"| {file} | {p.Preset} | {Comments.GetValueOrDefault(p.Preset, "")} | {10 * Math.Log10(d / s):0.0} dB | {20 * Math.Log10(pk):0.0} dBFS | {20 * Math.Log10(rms):0.0} dBFS | {clicks.Count} |");
            }
            log.AppendLine();
            log.AppendLine($"Click detector, all files: {clicksTotal} clicks.");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            Assert.That(clicksTotal, Is.EqualTo(0));
        }
    }
}

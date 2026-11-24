using System.Globalization;
using System.Text;
using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.Organ;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Organ's listening pack (docs/specs/neo-organ.md, AGENTS.md §5): every preset on the same 8-bar chord progression with a
    /// melody line, and one take that switches the rotary speaker Slow → Fast → Slow, as 48 kHz stereo WAV with a click check.
    /// Explicit: it runs only when asked, and writes to the folder in ORGAN_PACK (kept local, never committed):
    /// <c>ORGAN_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~OrganPack</c>
    /// </summary>
    [TestFixture]
    public class OrganPack
    {
        const int Rate = 48000;
        const double Bpm = 100;
        static double Beat => 60.0 / Bpm;

        // 8 bars of 4 beats: Cmaj7, Am7, Dm7, G7, Cmaj7, Fmaj7, Dm7, Cmaj7, each chord held for the bar.
        static readonly int[][] Chords =
        {
            new[] { 48, 55, 59, 64 }, new[] { 45, 55, 60, 64 }, new[] { 50, 57, 60, 65 }, new[] { 43, 53, 59, 62 },
            new[] { 48, 55, 59, 64 }, new[] { 53, 57, 60, 64 }, new[] { 50, 57, 60, 65 }, new[] { 48, 55, 60, 64 },
        };

        // The melody: (beat, length in beats, MIDI note).
        static readonly (double At, double Len, int Note)[] Melody =
        {
            (0, 1, 76), (1, 1, 79), (2, 1.5, 83), (3.5, 0.5, 81),
            (4, 1, 76), (5, 1, 72), (6, 2, 69),
            (8, 1, 74), (9, 1, 77), (10, 1.5, 81), (11.5, 0.5, 79),
            (12, 1, 74), (13, 1, 71), (14, 2, 67),
            (16, 1, 76), (17, 1, 79), (18, 1.5, 84), (19.5, 0.5, 83),
            (20, 1, 81), (21, 1, 77), (22, 2, 72),
            (24, 1, 74), (25, 1, 77), (26, 1, 79), (27, 1, 71),
            (28, 2, 72), (30, 2, 67),
        };

        internal static List<(string Name, Dictionary<int, int> Values)> Presets()
        {
            var file = Path.Combine(FindRepo(), "Machines", "NeoOrgan", "Neo Organ.prs.xml");
            var list = new List<(string, Dictionary<int, int>)>();
            foreach (var item in XDocument.Load(file).Root!.Elements("Item"))
            {
                var d = new Dictionary<int, int>();
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter")) d[(int)p.Attribute("Index")!] = (int)p.Attribute("Value")!;
                list.Add(((string)item.Attribute("Key")!, d));
            }
            return list;
        }

        static string FindRepo()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ReBuzz.sln"))) d = d.Parent;
            return d!.FullName;
        }

        /// <summary>Plays the 8 bars with a preset; <paramref name="atBar"/> runs at the start of each bar (to switch a parameter).</summary>
        internal static (float[] L, float[] R, List<int> Events) Play(Dictionary<int, int> preset, Action<ParamValues, int> atBar = null)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) v.Set(kv.Key, kv.Value);
            var e = new OrganEngine(v);
            e.SetSampleRate(Rate);
            int bar = (int)Math.Round(4 * Beat * Rate);
            int total = bar * 8 + (int)(1.5 * Rate);
            var ev = new List<(int At, bool On, int Note, int Slot)>();
            int slot = 1;
            for (int b = 0; b < 8; b++)
                foreach (int n in Chords[b])
                {
                    ev.Add((b * bar, true, n, slot));
                    ev.Add(((int)((b * 4 + 3.92) * Beat * Rate), false, n, slot));
                    slot++;
                }
            foreach (var m in Melody)
            {
                ev.Add(((int)(m.At * Beat * Rate), true, m.Note, slot));
                ev.Add(((int)((m.At + m.Len * 0.95) * Beat * Rate), false, m.Note, slot));
                slot++;
            }
            ev.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.On.CompareTo(b.On));   // note offs before note ons at the same time
            var yl = new float[total];
            var yr = new float[total];
            var l = new float[OrganEngine.MaxBlock];
            var r = new float[OrganEngine.MaxBlock];
            var applied = new List<int>();
            int ei = 0, lastBar = -1;
            for (int pos = 0; pos < total; pos += OrganEngine.MaxBlock)
            {
                int n = Math.Min(OrganEngine.MaxBlock, total - pos);
                if (pos / bar != lastBar && pos / bar < 8) { lastBar = pos / bar; atBar?.Invoke(v, lastBar); }
                while (ei < ev.Count && ev[ei].At < pos + n)
                {
                    var x = ev[ei++];
                    if (x.On) e.NoteOn(1000 + x.Slot, x.Note, 1f); else e.NoteOff(1000 + x.Slot);
                    if (applied.Count == 0 || applied[^1] != pos) applied.Add(pos);   // where the engine took the event
                }
                e.Process(l, r, n);
                Array.Copy(l, 0, yl, pos, n);
                Array.Copy(r, 0, yr, pos, n);
            }
            return (yl, yr, applied);
        }

        /// <summary>
        /// The click detector: the second difference |x[i+1] − 2 x[i] + x[i−1]| against its local mean (±256 samples, the 3 nearest
        /// left out). A candidate is above 24 times that mean (and above 0.004). <paramref name="strict"/> counts every candidate;
        /// otherwise a click must also stand alone (no second difference of half its size within ±1024 samples: a hard-driven wave
        /// has a corner like it every period, a key click a burst of them) and be narrow (above 6 times the mean 3 to 8 samples
        /// away: a corner of a driven wave spreads over many samples). <paramref name="exempt"/>: positions (note events) near which a
        /// click is the key click, by design.
        /// </summary>
        internal static int Clicks(float[] x, bool strict, List<int> exempt = null)
        {
            int n = x.Length, count = 0, last = -100000;
            var d2 = new double[n];
            for (int i = 1; i < n - 1; i++) d2[i] = Math.Abs(x[i + 1] - 2.0 * x[i] + x[i - 1]);
            var cum = new double[n + 1];
            for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + d2[i];
            const int W = 256, G = 3;
            for (int i = W; i < n - W; i++)
            {
                if (d2[i] < 0.004) continue;
                double mean = ((cum[i + W + 1] - cum[i - W]) - (cum[i + G + 1] - cum[i - G])) / (2 * W - 2 * G);
                if (d2[i] <= 24 * (mean + 1e-6)) continue;
                if (!strict)
                {
                    double other = 0;
                    for (int k = Math.Max(1, i - 1024); k <= Math.Min(n - 2, i + 1024); k++) if (Math.Abs(k - i) > 8) other = Math.Max(other, d2[k]);
                    if (other >= 0.5 * d2[i]) continue;
                    double near = 0;
                    for (int k = i - 8; k <= i + 8; k++) if (Math.Abs(k - i) >= 3) near += d2[k];
                    if (d2[i] < 6 * near / 12) continue;
                    if (exempt != null && exempt.Any(e => i >= e - 64 && i <= e + OrganEngine.MaxBlock + 600)) continue;
                }
                if (i - last > 64) count++;
                last = i;
            }
            return count;
        }

        /// <summary>
        /// The review's check, with no exemptions: the largest |second difference| in each 1 ms window (both channels), against the
        /// median of those over the take. Returns how many windows are above 20 times the median, and the largest value.
        /// </summary>
        internal static (int Windows, double Max) D2Windows(float[] l, float[] r)
        {
            int w = Rate / 1000, count = l.Length / w;
            var m = new double[count];
            double max = 0;
            for (int k = 0; k < count; k++)
            {
                double v = 0;
                for (int i = Math.Max(1, k * w); i < Math.Min(l.Length - 1, (k + 1) * w); i++)
                {
                    v = Math.Max(v, Math.Abs(l[i + 1] - 2.0 * l[i] + l[i - 1]));
                    v = Math.Max(v, Math.Abs(r[i + 1] - 2.0 * r[i] + r[i - 1]));
                }
                m[k] = v;
                max = Math.Max(max, v);
            }
            var sorted = (double[])m.Clone();
            Array.Sort(sorted);
            double median = sorted[count / 2];
            return (m.Count(v => v > 20 * median), max);
        }

        internal static void WriteWav(string path, float[] l, float[] r)
        {
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            int n = l.Length;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 4); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 4);
            for (int i = 0; i < n; i++)
            {
                w.Write((short)Math.Clamp(l[i] * 32767f, -32768f, 32767f));
                w.Write((short)Math.Clamp(r[i] * 32767f, -32768f, 32767f));
            }
        }

        static string Slug(string s) =>
            string.Join("-", new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));

        static (double RmsDb, double PeakDb) Level(float[] l, float[] r)
        {
            double s = 0, pk = 0;
            for (int i = 0; i < l.Length; i++)
            {
                s += l[i] * (double)l[i] + r[i] * (double)r[i];
                pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            }
            return (10 * Math.Log10(s / (2 * l.Length) + 1e-20), 20 * Math.Log10(pk + 1e-20));
        }

        [Test, Explicit("writes the local listening pack")]
        public void Pack()
        {
            var dir = Environment.GetEnvironmentVariable("ORGAN_PACK");
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "ORGAN_PACK names the folder");
            Directory.CreateDirectory(dir!);
            var report = new StringBuilder("take\trms dBFS\tpeak dBFS\tclicks\tclicks (key click 0)\tstrict candidates\tstrict (key click 0)\t1-ms windows > 20x median\tmax |d2|\tsame windows with key click 0\n");
            int clicks = 0;
            void Take(string file, string name, Dictionary<int, int> values, Action<ParamValues, int> atBar = null)
            {
                var (l, r, events) = Play(values, atBar);
                var quiet = new Dictionary<int, int>(values) { [P.KeyClick] = 0 };
                var (ql, qr, _) = Play(quiet, atBar);
                var lv = Level(l, r);
                int c = Clicks(l, false, events) + Clicks(r, false, events);
                int q = Clicks(ql, false) + Clicks(qr, false);
                int sc = Clicks(l, true) + Clicks(r, true);
                int sq = Clicks(ql, true) + Clicks(qr, true);
                var (windows, maxD2) = D2Windows(l, r);
                var (qWindows, _) = D2Windows(ql, qr);
                // The review's windows check counts any bright onset over a dull take (the rotary take's percussion through the
                // fast horn reads 21 times the median with the click off too): the click may add none.
                clicks += c + q + Math.Max(0, windows - qWindows);
                WriteWav(Path.Combine(dir!, file), l, r);
                report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{name}\t{lv.RmsDb:0.0}\t{lv.PeakDb:0.0}\t{c}\t{q}\t{sc}\t{sq}\t{windows}\t{maxD2:0.000}\t{qWindows}"));
            }
            var presets = Presets();
            foreach (var (name, values) in presets) Take(Slug(name) + ".wav", name, values);
            // The speaker take: Jazz Trio, Slow for bars 1-2, Fast for bars 3-5, Slow again from bar 6.
            var jazz = new Dictionary<int, int>(presets.First(p => p.Name.Contains("Jazz Trio")).Values) { [P.Rotary] = 1 };
            Take("rotary-slow-fast-slow.wav", "Rotary Slow-Fast-Slow (Jazz Trio)", jazz, (v, bar) => v.Set(P.Speed, bar < 2 ? 0 : bar < 5 ? 1 : 0));
            File.WriteAllText(Path.Combine(dir!, "levels.txt"), report.ToString());
            TestContext.Progress.WriteLine(report.ToString());
            File.WriteAllText(Path.Combine(dir!, "click-table.txt"), OrganClickFigures.Table());
            Assert.That(clicks, Is.EqualTo(0), "clicks in the pack's renders (the detector's, and 1-ms windows above 20 times the median that the click adds)");
        }
    }
}

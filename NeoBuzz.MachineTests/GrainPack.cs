using System.Globalization;
using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Grain;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Grain's listening pack (docs/specs/neo-grain.md, AGENTS.md §5): each preset on one short phrase of our own (four bars at 68 bpm, about 15 s: a
    /// chord a bar and a sparse tune above it), as 48 kHz 16-bit stereo WAV, each with a click check. Every preset reads one of the machine's own built-in
    /// sources (made by code), so the pack uses no third-party audio.
    /// Explicit: it runs only when asked, and writes to the folder in GRAIN_PACK (kept local, never committed):
    /// <c>GRAIN_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~GrainPack</c>
    /// </summary>
    [TestFixture]
    public class GrainPack
    {
        const int Rate = 48000;
        internal const double Bpm = 68;
        internal static double Beat => 60.0 / Bpm;

        // 4 bars of 4 beats: (beat, length in beats, MIDI note).
        static readonly (double At, double Len, int Note)[] Chords =
        {
            (0, 3.9, 48), (0, 3.9, 55), (0, 3.9, 60), (0, 3.9, 64),            // C
            (4, 3.9, 45), (4, 3.9, 55), (4, 3.9, 60), (4, 3.9, 64),            // Am7
            (8, 3.9, 41), (8, 3.9, 53), (8, 3.9, 57), (8, 3.9, 60),            // F
            (12, 3.9, 43), (12, 3.9, 50), (12, 3.9, 55), (12, 3.9, 59),        // G
        };
        static readonly (double At, double Len, int Note)[] Tune =
        {
            (1.5, 1, 72), (2.5, 0.5, 74), (3, 1, 76),
            (5.5, 1, 76), (6.5, 0.5, 74), (7, 1, 72),
            (9.5, 1, 69), (10.5, 0.5, 72), (11, 1, 74),
            (13.5, 1, 67), (14.5, 0.5, 71), (15, 1, 72),
        };

        static string Slug(string s) =>
            string.Join("-", new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));

        internal static int Length => (int)(Rate * (Beat * 16 + 1.2));

        internal static List<(double At, double Len, int Note, float Vel)> Phrase()
        {
            var list = new List<(double, double, int, float)>();
            foreach (var c in Chords) list.Add((c.At, c.Len, c.Note, 0.5f));
            int i = 0;
            foreach (var m in Tune) list.Add((m.At, m.Len * 0.95, m.Note, 0.5f + 0.25f * (float)((i++ * 37 % 11) / 11.0)));
            return list;
        }

        internal static GrainTests.Take Play(Dictionary<int, int> preset, List<(double At, double Len, int Note, float Vel)> notes, int length)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) v.Set(kv.Key, kv.Value);
            var e = new GrainEngine(v); e.SetSampleRate(Rate);
            var ev = new List<(int At, bool On, int Key, int Note, float Vel)>();
            int key = 0;
            foreach (var n in notes)
            {
                int k = key++;
                ev.Add(((int)(n.At * Beat * Rate / 256) * 256, true, k, n.Note, n.Vel));
                ev.Add(((int)((n.At + n.Len) * Beat * Rate / 256) * 256, false, k, n.Note, 0));
            }
            ev.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.On.CompareTo(b.On));
            var t = new GrainTests.Take { L = new float[length], R = new float[length] };
            var l = new float[256]; var r = new float[256];
            int ei = 0;
            for (int pos = 0; pos < length; pos += 256)
            {
                while (ei < ev.Count && ev[ei].At <= pos) { var x = ev[ei++]; if (x.On) e.NoteOn(x.Key, x.Note, x.Vel); else e.NoteOff(x.Key); }
                int n = Math.Min(256, length - pos);
                Array.Clear(l, 0, 256); Array.Clear(r, 0, 256);
                e.Process(l, r, 256);
                Array.Copy(l, 0, t.L, pos, n); Array.Copy(r, 0, t.R, pos, n);
            }
            return t;
        }

        [Test]
        public void ThePacksPhraseIsAtTheLevelOfAnInstrumentAndClickFree()
        {
            var bad = new List<string>();
            foreach (var (name, vals) in GrainTests.FactoryPresets())
            {
                var t = Play(vals, Phrase(), Length);
                double peak = Math.Max(Measure.Peak(t.L, 0, t.L.Length), Measure.Peak(t.R, 0, t.R.Length));
                var clicks = ClickDetector.Find(t.L, t.R);
                TestContext.Out.WriteLine($"{name}: pack phrase peak {Measure.Db(peak):0.0} dBFS, clicks {clicks.Count}");
                if (Measure.Db(peak) > -1.0 || Measure.Db(peak) < -12.0) bad.Add($"{name} {Measure.Db(peak):0.0} dBFS");
                if (clicks.Count > 0) bad.Add($"{name}: {clicks.Count} clicks");
            }
            Assert.That(bad, Is.Empty);
        }

        [Test]
        public void WriteTheListeningPack()
        {
            var dir = Environment.GetEnvironmentVariable("GRAIN_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("Set GRAIN_PACK to a folder to write the listening pack there.");
            Directory.CreateDirectory(dir!);
            var presets = GrainTests.FactoryPresets().ToList();
            Assert.That(presets.Count, Is.EqualTo(8));
            var readme = new StringBuilder();
            readme.AppendLine("Neo Grain listening pack (GRN1). 48 kHz, 16-bit stereo WAV, each about 15 s. Generated by NeoBuzz.MachineTests/GrainPack.cs; kept local, never committed.");
            readme.AppendLine();
            readme.AppendLine("Each of the eight presets plays one short phrase of our own: four bars at 68 bpm, a four-note chord a bar (C, Am7, F, G) and a sparse tune above it.");
            readme.AppendLine("Every preset reads one of Neo Grain's built-in sources (a chord, bells or noise, made by code in Engine/GrainSources.cs): no third-party audio. Neo Grain is an instrument: nothing goes in.");
            readme.AppendLine("Click detector: the project's (second difference against its local mean); 0 clicks in every file.");
            readme.AppendLine();
            readme.AppendLine("| # | file | preset | source | peak dBFS | RMS dB | clicks |");
            readme.AppendLine("|---|---|---|---|---|---|---|");
            string[] sources = { "Chord", "Bells", "Noise" };
            int index = 1;
            foreach (var (name, vals) in presets)
            {
                var t = Play(vals, Phrase(), Length);
                var clicks = ClickDetector.Find(t.L, t.R);
                double peak = Math.Max(Measure.Peak(t.L, 0, t.L.Length), Measure.Peak(t.R, 0, t.R.Length));
                double rms = Math.Sqrt((Measure.Rms(t.L, 0, t.L.Length) * Measure.Rms(t.L, 0, t.L.Length) + Measure.Rms(t.R, 0, t.R.Length) * Measure.Rms(t.R, 0, t.R.Length)) / 2);
                string file = $"{index:00}-{Slug(name)}.wav";
                OrganPack.WriteWav(Path.Combine(dir!, file), t.L, t.R);
                readme.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3} | {4:0.0} | {5:0.0} | {6} |", index, file, name, sources[vals.TryGetValue(P.Source, out int s) ? s : 0], Measure.Db(peak), Measure.Db(rms), clicks.Count));
                TestContext.Out.WriteLine($"{file}: peak {Measure.Db(peak):0.0} dBFS, RMS {Measure.Db(rms):0.0} dB, clicks {clicks.Count}" + (clicks.Count > 0 ? " at " + string.Join(",", clicks.Take(8)) : ""));
                Assert.That(clicks.Count, Is.EqualTo(0), $"{name}: clicks");
                index++;
            }
            File.WriteAllText(Path.Combine(dir!, "README.md"), readme.ToString());
        }
    }
}

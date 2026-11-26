using System.Globalization;
using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Mallet;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Mallet's listening pack (docs/specs/neo-mallet.md, AGENTS.md §5): each preset on one short melody of our own (about 16 s, 100 bpm, a bass note a bar),
    /// and the Resonator preset on a drum loop rendered from Neo Drums with a held chord, as 48 kHz 16-bit stereo WAV, each with a click check.
    /// Explicit: it runs only when asked, and writes to the folder in MALLET_PACK (kept local, never committed):
    /// <c>MALLET_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~MalletPack</c>
    /// </summary>
    [TestFixture]
    public class MalletPack
    {
        const int Rate = 48000;
        const double Bpm = 100;
        static double Beat => 60.0 / Bpm;

        // 6 bars of 4 beats (14.4 s): (beat, length in beats, MIDI note). A pentatonic tune of our own.
        static readonly (double At, double Len, int Note)[] Melody =
        {
            (0, 1, 72), (1, 0.5, 74), (1.5, 0.5, 76), (2, 2, 79),
            (4, 1, 76), (5, 1, 74), (6, 0.5, 72), (6.5, 0.5, 69), (7, 1, 67),
            (8, 1.5, 69), (9.5, 0.5, 72), (10, 1, 76), (11, 1, 74),
            (12, 1, 72), (13, 0.5, 74), (13.5, 0.5, 72), (14, 2, 69),
            (16, 1, 67), (17, 1, 69), (18, 1, 72), (19, 1, 76),
            (20, 1, 79), (21, 1, 76), (22, 2, 72),
        };
        static readonly (double At, double Len, int Note)[] Bass =
        {
            (0, 3.5, 48), (4, 3.5, 43), (8, 3.5, 45), (12, 3.5, 41), (16, 3.5, 43), (20, 4, 48),
        };

        static string Slug(string s) =>
            string.Join("-", new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));

        static MalletTests.Take Play(Dictionary<int, int> preset, float[] input, List<(double At, double Len, int Note, float Vel)> notes, int length)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) v.Set(kv.Key, kv.Value);
            var e = new MalletEngine(v); e.SetSampleRate(Rate);
            var ev = new List<(int At, bool On, int Key, int Note, float Vel)>();
            int key = 0;
            foreach (var n in notes)
            {
                int k = key++;
                ev.Add(((int)(n.At * Beat * Rate / 256) * 256, true, k, n.Note, n.Vel));
                ev.Add(((int)((n.At + n.Len) * Beat * Rate / 256) * 256, false, k, n.Note, 0));
            }
            ev.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.On.CompareTo(b.On));
            var t = new MalletTests.Take { L = new float[length], R = new float[length] };
            var l = new float[256]; var r = new float[256];
            int ei = 0;
            for (int pos = 0; pos < length; pos += 256)
            {
                while (ei < ev.Count && ev[ei].At <= pos) { var x = ev[ei++]; if (x.On) e.NoteOn(x.Key, x.Note, x.Vel); else e.NoteOff(x.Key); }
                int n = Math.Min(256, length - pos);
                for (int i = 0; i < 256; i++) l[i] = r[i] = input != null && pos + i < input.Length ? input[pos + i] : 0f;
                e.Process(l, r, 256);
                Array.Copy(l, 0, t.L, pos, n); Array.Copy(r, 0, t.R, pos, n);
            }
            return t;
        }

        static List<(double, double, int, float)> Tune()
        {
            var list = new List<(double, double, int, float)>();
            int i = 0;
            foreach (var m in Melody) list.Add((m.At, m.Len * 0.95, m.Note, 0.62f + 0.3f * (float)((i++ * 37 % 11) / 11.0)));
            foreach (var b in Bass) list.Add((b.At, b.Len, b.Note, 0.7f));
            return list;
        }

        /// <summary>Four bars of a drum loop from Neo Drums (kick, snare, closed hat), 100 bpm, repeated to <paramref name="length"/>.</summary>
        static float[] DrumLoop(int length)
        {
            var v = new ParamValues(NeoBuzz.Drums.Params.Table);
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            var y = new float[length];
            var l = new float[256]; var r = new float[256];
            double step = Beat / 2;                                         // eighth notes
            int next = 0;
            for (int pos = 0; pos < length; pos += 256)
            {
                while (next * step * Rate < pos + 256)
                {
                    int at = next % 16;
                    int hit = (int)(next * step * Rate) - pos;
                    if (hit >= 0)
                    {
                        if (at == 0 || at == 6 || at == 8 || at == 14) e.NoteOn(0, 36, 0.9f);
                        if (at == 4 || at == 12) e.NoteOn(1, 38, 0.85f);
                        e.NoteOn(2, 42, at % 2 == 0 ? 0.7f : 0.45f);
                    }
                    next++;
                }
                int n = Math.Min(256, length - pos);
                e.Process(l, r, 256);
                for (int i = 0; i < n; i++) y[pos + i] = 0.5f * (l[i] + r[i]);
            }
            return y;
        }

        [Test]
        public void WriteTheListeningPack()
        {
            var dir = Environment.GetEnvironmentVariable("MALLET_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("Set MALLET_PACK to a folder to write the listening pack there.");
            Directory.CreateDirectory(dir!);
            var presets = MalletTests.FactoryPresets().ToList();
            Assert.That(presets.Count, Is.EqualTo(8));
            int length = (int)(Rate * (Beat * 24 + 1.8));
            var readme = new StringBuilder();
            readme.AppendLine("Neo Mallet listening pack (MAL1). 48 kHz, 16-bit stereo WAV, each about 16 s. Generated by NeoBuzz.MachineTests/MalletPack.cs; kept local, never committed.");
            readme.AppendLine();
            readme.AppendLine("Items 1 to 7: each preset plays one short melody of our own (a pentatonic tune at 100 bpm with a bass note a bar). Item 8: the Resonator preset rings a drum loop from Neo Drums,");
            readme.AppendLine("with a held chord (C, E, G; then A, C, E; F, A, C; G, B, D, a bar each) on the keys. Click detector: the project's (second difference against its local mean).");
            readme.AppendLine();
            readme.AppendLine("| # | file | preset | peak dBFS | RMS dB | clicks |");
            readme.AppendLine("|---|---|---|---|---|---|");
            int index = 1;
            foreach (var (name, vals) in presets)
            {
                bool resonator = vals.TryGetValue(P.Source, out int src) && src == 1;
                MalletTests.Take t;
                float[] input = null;
                if (!resonator) t = Play(vals, null, Tune(), length);
                else
                {
                    input = DrumLoop(length);
                    var chords = new List<(double, double, int, float)>();
                    int[][] c = { new[] { 48, 52, 55 }, new[] { 45, 48, 52 }, new[] { 41, 45, 48 }, new[] { 43, 47, 50 } };
                    for (int bar = 0; bar < 6; bar++) foreach (int n in c[bar % 4]) chords.Add((bar * 4, 3.9, n + 12, 0.8f));
                    t = Play(vals, input, chords, length);
                }
                var clicks = ClickDetector.Find(t.L, t.R);
                double peak = Math.Max(Measure.Peak(t.L, 0, t.L.Length), Measure.Peak(t.R, 0, t.R.Length));
                double rms = Math.Sqrt((Measure.Rms(t.L, 0, t.L.Length) * Measure.Rms(t.L, 0, t.L.Length) + Measure.Rms(t.R, 0, t.R.Length) * Measure.Rms(t.R, 0, t.R.Length)) / 2);
                string file = $"{index:00}-{Slug(name)}.wav";
                OrganPack.WriteWav(Path.Combine(dir!, file), t.L, t.R);
                int inputClicks = input != null ? ClickDetector.Find(input, input).Count : 0;
                readme.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3:0.0} | {4:0.0} | {5}{6} |", index, file, name, Measure.Db(peak), Measure.Db(rms), clicks.Count, resonator ? $" (the drum loop alone: {inputClicks})" : ""));
                TestContext.Out.WriteLine($"{file}: peak {Measure.Db(peak):0.0} dBFS, RMS {Measure.Db(rms):0.0} dB, clicks {clicks.Count}" + (clicks.Count > 0 ? " at " + string.Join(",", clicks.Take(8)) : ""));
                Assert.That(clicks.Count, Is.LessThanOrEqualTo(inputClicks), $"{name}: clicks beyond the input's own");
                index++;
            }
            File.WriteAllText(Path.Combine(dir!, "README.md"), readme.ToString());
        }
    }
}

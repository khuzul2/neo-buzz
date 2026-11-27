using System.Globalization;
using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Keys;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Keys' listening pack (docs/specs/neo-keys.md, AGENTS.md §5): each preset on one phrase of our own (six bars at 96 bpm, 15 s: chords and a melody with varied
    /// velocities and the sustain pedal), as 48 kHz 16-bit stereo WAV, each with a click check.
    /// Explicit: it runs only when asked, and writes to the folder in KEYS_PACK (kept local, never committed):
    /// <c>KEYS_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~KeysPack</c>
    /// </summary>
    [TestFixture, Explicit("writes the listening pack when KEYS_PACK is set")]
    public class KeysPack
    {
        const int Rate = 48000;
        const double Bpm = 96;
        static double Beat => 60.0 / Bpm;

        // Chords of two bars each beat a bar: (beat, notes). Voicings of our own around C3 to E4; the pedal goes down on each chord and up a hair before the next.
        static readonly (double At, double Len, int[] Notes, float Vel)[] Chords =
        {
            (0, 3.9, new[] { 48, 55, 59, 64 }, 0.62f),     // C major seventh
            (4, 3.9, new[] { 45, 52, 55, 60 }, 0.58f),     // A minor seventh
            (8, 3.9, new[] { 50, 53, 57, 60 }, 0.66f),     // D minor seventh
            (12, 3.9, new[] { 43, 53, 59, 62 }, 0.74f),    // G dominant seventh
            (16, 3.9, new[] { 48, 55, 59, 64 }, 0.55f),    // C major seventh
            (20, 4.5, new[] { 41, 52, 57, 60, 64 }, 0.80f),// F major seventh, the last chord rings out
        };

        // (beat, length in beats, MIDI note, velocity): a melody of our own, soft to hard and back.
        static readonly (double At, double Len, int Note, float Vel)[] Melody =
        {
            (0.5, 0.5, 76, 0.45f), (1, 1, 79, 0.55f), (2, 0.5, 76, 0.5f), (2.5, 1.5, 72, 0.6f),
            (4.5, 0.5, 72, 0.5f), (5, 1, 76, 0.65f), (6, 0.5, 79, 0.7f), (6.5, 1.5, 76, 0.55f),
            (8.5, 0.5, 74, 0.6f), (9, 1, 77, 0.75f), (10, 0.5, 81, 0.85f), (10.5, 1.5, 77, 0.7f),
            (12, 0.5, 74, 0.8f), (12.5, 0.5, 79, 0.9f), (13, 1, 83, 1.0f), (14, 0.5, 79, 0.85f), (14.5, 1.5, 74, 0.7f),
            (16.5, 0.5, 79, 0.5f), (17, 1, 76, 0.45f), (18, 0.5, 72, 0.4f), (18.5, 1.5, 76, 0.5f),
            (20, 1, 81, 0.9f), (21, 1, 79, 0.8f), (22, 0.5, 76, 0.65f), (22.5, 0.5, 72, 0.5f), (23, 1.5, 69, 0.4f),
        };

        static string Slug(string s) =>
            string.Join("-", new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Split('-', StringSplitOptions.RemoveEmptyEntries));

        internal static KeysTests.Take Play(Dictionary<int, int> preset, int length, List<int> onsets = null)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) v.Set(kv.Key, kv.Value);
            var e = new KeysEngine(v); e.SetSampleRate(Rate);
            var ev = new List<(int At, int Order, Action Do)>();
            int Pos(double beats) => (int)(beats * Beat * Rate / 256) * 256;
            int key = 0;
            foreach (var c in Chords)
            {
                ev.Add((Pos(c.At) - 256, 0, () => e.Sustain(false)));
                ev.Add((Pos(c.At) + 256, 0, () => e.Sustain(true)));
                for (int i = 0; i < c.Notes.Length; i++)
                {
                    int k = key++, n = c.Notes[i];
                    float vel = Math.Min(1f, c.Vel + 0.04f * ((i * 5) % 3 - 1));
                    ev.Add((Pos(c.At) + 256 + 256 * i * 3, 1, () => e.NoteOn(k, n, vel)));         // a hair of roll
                    onsets?.Add(Pos(c.At) + 256 + 256 * i * 3);
                    ev.Add((Pos(c.At + c.Len), 2, () => e.NoteOff(k)));
                }
            }
            foreach (var m in Melody)
            {
                int k = key++, n = m.Note; float vel = m.Vel;
                ev.Add((Pos(m.At), 1, () => e.NoteOn(k, n, vel)));
                onsets?.Add(Pos(m.At));
                ev.Add((Pos(m.At + m.Len * 0.9), 2, () => e.NoteOff(k)));
            }
            ev.Add((Pos(24) + 512, 3, () => e.Sustain(false)));
            ev.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Order.CompareTo(b.Order));
            var t = new KeysTests.Take { L = new float[length], R = new float[length] };
            var l = new float[256]; var r = new float[256];
            int ei = 0;
            for (int pos = 0; pos < length; pos += 256)
            {
                while (ei < ev.Count && ev[ei].At <= pos) ev[ei++].Do();
                int n = Math.Min(256, length - pos);
                for (int i = 0; i < 256; i++) l[i] = r[i] = 0f;
                e.Process(l, r, 256);
                Array.Copy(l, 0, t.L, pos, n); Array.Copy(r, 0, t.R, pos, n);
            }
            return t;
        }

        /// <summary>The band above <paramref name="hz"/> as dB under the total, in a 1024-point frame from <paramref name="at"/> (mono, Blackman-Harris window).</summary>
        internal static double BandGap(float[] l, float[] r, int at, double hz = 10000, int n = 1024)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * (i + 0.5) / n;
                w[i] = (float)(0.5 * (l[at + i] + r[at + i]) * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
            }
            var m = Fft.Magnitudes(w, 0, n);
            double hi = 0, tot = 0;
            for (int k = 1; k < m.Length; k++) { double p = m[k] * m[k]; tot += p; if (k * (double)Rate / n >= hz) hi += p; }
            return 10 * Math.Log10(Math.Max(hi, 1e-30) / Math.Max(tot, 1e-30));
        }

        /// <summary>The gap above 10 kHz at every onset of the phrase: (the smallest gap's dB as a negative number, the gap at the loudest onset).</summary>
        internal static (double Worst, double AtLoudest) OnsetGaps(KeysTests.Take t, List<int> onsets, double hz = 10000)
        {
            double worst = double.NegativeInfinity, loudestGap = 0, loudest = -1;
            foreach (int at in onsets)
            {
                if (at + 1024 > t.L.Length) continue;
                double g = BandGap(t.L, t.R, at, hz);
                double e = 0;
                for (int i = 0; i < 1024; i++) e += t.L[at + i] * t.L[at + i] + t.R[at + i] * t.R[at + i];
                if (g > worst) worst = g;
                if (e > loudest) { loudest = e; loudestGap = g; }
            }
            return (worst, loudestGap);
        }

        static readonly Dictionary<string, string> Listen = new()
        {
            ["Tine — Classic Ballad"] = "Round and sweet: a soft tine with a tone bar singing under it. Listen for the bell-like sustain on the held chords and a gentle bark only on the loudest melody notes (bars 3 and 4).",
            ["Tine — Bark Drive"] = "The pickup close and the preamp pushed: soft notes are clean, hard notes growl. Listen for the bark on the high melody notes of bar 4 that smooths out as each note dies away.",
            ["Tine — Stereo Tremolo"] = "A tine with the tremolo at 4.5 Hz swinging the sound from left to right. Use headphones; listen for the chords sweeping across the stereo field.",
            ["Reed — Soul Keys"] = "A steel reed: short, woody and buzzy with a quick decay and a little drive. Listen for the odd-harmonic reediness and how the chords stop soon after the pedal is lifted.",
            ["String — Funky Pluck"] = "A struck string read at the bridge: bright, thin and twangy, with a fast decay. Listen for the pluck at the start of each note and the key noise between them.",
            ["String — Muted Staccato"] = "A muted string: dry, short and percussive; each note stops almost at once even with the pedal down. Listen for the clipped chords and the key noise.",
            ["Tine — Glass Bells"] = "A hard hammer on a tine with a long ring and the pickup away: glassy, bell-like partials that ring long above the notes. Listen for the high ping at each note start and the long shimmer under the pedal.",
        };

        /// <summary>Prints each preset's onset gaps above 10 kHz and a hash of its render, for comparing two builds (KEYS_REPORT=1).</summary>
        [Test]
        public void ReportOnsetGapsAndHashes()
        {
            if (Environment.GetEnvironmentVariable("KEYS_REPORT") == null) Assert.Ignore("Set KEYS_REPORT=1.");
            int length = (int)(Rate * (Beat * 24 + 1.6));
            foreach (var (name, vals) in KeysTests.FactoryPresets())
            {
                var on = new List<int>();
                var t = Play(vals, length, on);
                var (worst, loud) = OnsetGaps(t, on);
                if (Environment.GetEnvironmentVariable("KEYS_VERBOSE") != null) TestContext.Out.WriteLine("REPORT   " + name + ": " + string.Join(" ", on.Where(a => a + 1024 <= t.L.Length).Select(a => string.Format(CultureInfo.InvariantCulture, "{0:0.0}s={1:0}", a / (double)Rate, BandGap(t.L, t.R, a)))));
                var bytes = new byte[t.L.Length * 8];
                Buffer.BlockCopy(t.L, 0, bytes, 0, t.L.Length * 4); Buffer.BlockCopy(t.R, 0, bytes, t.L.Length * 4, t.R.Length * 4);
                double peak = Math.Max(Measure.Peak(t.L, 0, t.L.Length), Measure.Peak(t.R, 0, t.R.Length));
                TestContext.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "REPORT {0} | worst onset gap {1:0.0} dB | loudest onset gap {2:0.0} dB | peak {3:0.00} dBFS | sha {4}", name, worst, loud, Measure.Db(peak), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
            }
        }

        // Round 1 (8f1690bd5): the band above 10 kHz, dB under the total, at the worst onset and at the loudest, of the five presets that were sent back.
        static readonly Dictionary<string, (double Worst, double Loudest)> Round1 = new()
        {
            ["Tine — Classic Ballad"] = (-20.1, -32.1), ["Tine — Bark Drive"] = (-23.6, -31.4), ["Tine — Stereo Tremolo"] = (-24.1, -35.7),
            ["Reed — Soul Keys"] = (-24.1, -32.1),
        };

        [Test]
        public void WriteTheListeningPack()
        {
            var dir = Environment.GetEnvironmentVariable("KEYS_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Ignore("Set KEYS_PACK to a folder to write the listening pack there.");
            Directory.CreateDirectory(dir!);
            var presets = KeysTests.FactoryPresets().ToList();
            Assert.That(presets.Count, Is.EqualTo(7));
            bool round2 = Environment.GetEnvironmentVariable("KEYS_PACK_ROUND") == "2";   // only the five sent back, with the onset numbers beside round 1's
            int length = (int)(Rate * (Beat * 24 + 1.6));
            var readme = new StringBuilder();
            readme.AppendLine("Neo Keys listening pack (KEY1, issue 193). 48 kHz, 16-bit stereo WAV, each about 16 s. Generated by NeoBuzz.MachineTests/KeysPack.cs; kept local, never committed.");
            readme.AppendLine();
            readme.AppendLine("Each of the seven instrument presets plays one phrase of our own: six bars at 96 bpm of seventh chords (with a little roll) and a melody, velocities from soft to full, and the sustain pedal changed on every chord.");
            readme.AppendLine("Neo Keys is an instrument: nothing goes in. Click detector: the project's (second difference against its local mean).");
            readme.AppendLine();
            if (round2)
            {
                readme.AppendLine("Round 2 (KEY1b): the five presets sent back after round 1, with the thump rebuilt as a low knock (a short decaying resonance, Tine 120 Hz and Reed 180 Hz, plus a little noise through a 4th-order low-pass), so it no longer hisses. Same phrase, same file names. Key noise: Classic Ballad 22 to 36 %, Stereo Tremolo 14 to 6 % (so the two stay clearly different); the others unchanged.");
                readme.AppendLine("The numbers: the band above 10 kHz in a 1024-point frame from each note onset, dB under the total, worst onset and loudest onset (round 1 -> now).");
                readme.AppendLine();
                readme.AppendLine("| # | file | preset | peak dBFS | RMS dB | clicks | >10 kHz at worst onset (dB) | >10 kHz at loudest onset (dB) |");
                readme.AppendLine("|---|---|---|---|---|---|---|---|");
            }
            else
            {
                readme.AppendLine("| # | file | preset | peak dBFS | RMS dB | clicks |");
                readme.AppendLine("|---|---|---|---|---|---|");
            }
            var notes = new StringBuilder();
            int index = 1;
            foreach (var (name, vals) in presets)
            {
                if (round2 && !Round1.ContainsKey(name)) { index++; continue; }
                var on = new List<int>();
                var t = Play(vals, length, on);
                var clicks = ClickDetector.Find(t.L, t.R);
                double peak = Math.Max(Measure.Peak(t.L, 0, t.L.Length), Measure.Peak(t.R, 0, t.R.Length));
                double lr = Measure.Rms(t.L, 0, t.L.Length), rr = Measure.Rms(t.R, 0, t.R.Length);
                double rms = Math.Sqrt((lr * lr + rr * rr) / 2);
                string file = $"{index:00}-{Slug(name)}.wav";
                OrganPack.WriteWav(Path.Combine(dir!, file), t.L, t.R);
                if (round2)
                {
                    var (worst, loud) = OnsetGaps(t, on);
                    var r1 = Round1[name];
                    readme.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3:0.0} | {4:0.0} | {5} | {6:0.0} -> {7:0.0} | {8:0.0} -> {9:0.0} |", index, file, name, Measure.Db(peak), Measure.Db(rms), clicks.Count, r1.Worst, worst, r1.Loudest, loud));
                }
                else readme.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3:0.0} | {4:0.0} | {5} |", index, file, name, Measure.Db(peak), Measure.Db(rms), clicks.Count));
                notes.AppendLine(string.Format(CultureInfo.InvariantCulture, "- **{0}** ({1}): {2}", file, name, Listen.TryGetValue(name, out var s) ? s : ""));
                TestContext.Out.WriteLine($"{file}: peak {Measure.Db(peak):0.0} dBFS, RMS {Measure.Db(rms):0.0} dB, clicks {clicks.Count}" + (clicks.Count > 0 ? " at " + string.Join(",", clicks.Take(8)) : ""));
                Assert.That(clicks.Count, Is.EqualTo(0), $"{name}: clicks");
                index++;
            }
            readme.AppendLine();
            readme.AppendLine("## What to listen for");
            readme.AppendLine();
            readme.Append(notes);
            File.WriteAllText(Path.Combine(dir!, "README.md"), readme.ToString());
        }
    }
}

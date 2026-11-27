// Neo Drive's listening pack for EXT3 (issue #192, AGENTS.md §5): the five new factory presets, each on the source it suits, and Hard clip, Rectify
// and Transformer on a second source: 8 files of 8 bars at 120 BPM (16 s), 48 kHz 16-bit stereo WAV, a README, a click check. Explicit: it runs only
// when asked and writes to the folder in NEO_DRIVE_PACK (kept local, never committed):
//   NEO_DRIVE_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~NeoDrivePack
using System.Text;
using NeoBuzz.Drive;
using NeoBuzz.Dsp;
using static NeoBuzz.MachineTests.ChopRig;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class NeoDrivePack
    {
        const int Bars = 8;
        static readonly int Length = (int)(Spb(120) * 4 * Bars);
        static readonly int Bar = (int)(Spb(120) * 4);
        static readonly int Step = (int)(Spb(120));                       // a sixteenth

        static ParamValues PresetValues(ParamDef[] table, string machine, string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == machine && p.Preset == preset);
            var v = new ParamValues(table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static (float[] L, float[] R) Run(Action<float[], float[], int> process, int length, Action<int> atBlock)
        {
            var ol = new float[length]; var or = new float[length];
            var l = new float[256]; var r = new float[256];
            for (int pos = 0; pos < length; pos += 256)
            {
                atBlock(pos);
                int n = Math.Min(256, length - pos);
                Array.Clear(l, 0, 256); Array.Clear(r, 0, 256);
                process(l, r, n);
                Array.Copy(l, 0, ol, pos, n); Array.Copy(r, 0, or, pos, n);
            }
            return (ol, or);
        }

        static bool In(int at, int pos) => at >= pos && at < pos + 256;

        /// <summary>Neo Poly: a saw bass riff in E, eighths with rests and a pickup, a plain patch (a saw through a low-pass, a plucked envelope).</summary>
        static (float[], float[]) BassSource()
        {
            var v = ArpPack.PlainPatch();
            v.Set(NeoBuzz.Poly.P.F1Cutoff, NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.F1Cutoff].Raw(1400));
            v.Set(NeoBuzz.Poly.P.AmpD, NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.AmpD].Raw(0.3));
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            // 16 steps of a sixteenth: note, or -1 for a rest; the second bar of each pair moves up a fourth
            int[] riff = { 40, -1, 40, 43, -1, 40, -1, 45, 40, -1, 40, 47, -1, 45, 43, -1 };
            return Run((l, r, n) => e.Process(l, r, n), Length, pos =>
            {
                for (int bar = 0; bar < Bars; bar++)
                    for (int s = 0; s < 16; s++)
                    {
                        int note = riff[s];
                        if (note < 0) continue;
                        if (bar % 4 == 3) note += 5;
                        int on = bar * Bar + s * Step, off = on + (int)(Step * 1.6);
                        if (In(on, pos)) e.NoteOn(s % 4, note, s % 4 == 0 ? 0.95f : 0.75f);
                        if (In(off, pos)) e.NoteOff(s % 4);
                    }
            });
        }

        /// <summary>Neo Drums: a four-bar pattern played twice.</summary>
        static (float[], float[]) DrumSource()
        {
            var v = PresetValues(NeoBuzz.Drums.Params.Table, "Neo Drums", "Classic — Punchy Kit");
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            return Run((l, r, n) => e.Process(l, r, n), Length, pos =>
            {
                for (int s = 0; s < Bars * 16; s++)
                {
                    int at = s * Step;
                    if (!In(at, pos)) continue;
                    int b = s % 16, bar = (s / 16) % 4;
                    if (b == 0 || b == 10 || b == 8 || (b == 13 && bar % 2 == 1)) e.NoteOn(0, 36, b == 0 ? 0.95f : 0.8f);
                    if (b == 4 || b == 12) e.NoteOn(1, 38, 0.9f);
                    if (b % 2 == 0) e.NoteOn(2, 42, b % 4 == 0 ? 0.8f : 0.5f);
                    if (b == 14) e.NoteOn(3, 46, 0.6f);
                }
            });
        }

        /// <summary>Neo Poly: Am, F, C, G, a bar each, twice, on the "Pad — Warm Strings" preset.</summary>
        static (float[], float[]) PadSource()
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Pad — Warm Strings");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[][] chords = { new[] { 45, 52, 57, 60 }, new[] { 41, 48, 57, 60 }, new[] { 48, 52, 55, 60 }, new[] { 43, 50, 55, 59 } };
            return Run((l, r, n) => e.Process(l, r, n), Length, pos =>
            {
                for (int c = 0; c < Bars; c++)
                {
                    if (In(c * Bar, pos)) for (int k = 0; k < 4; k++) e.NoteOn(k, chords[c % 4][k], 0.7f);
                    if (In(c * Bar + (int)(Bar * 0.9), pos)) for (int k = 0; k < 4; k++) e.NoteOff(k);
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

        static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        static (float[] L, float[] R) Through(ParamValues v, float[] l, float[] r)
        {
            var e = new DriveEngine(v);
            e.SetSampleRate(Rate);
            var ol = new float[l.Length]; var or = new float[l.Length];
            var bl = new float[256]; var br = new float[256];
            for (int pos = 0; pos < l.Length; pos += 256)
            {
                int n = Math.Min(256, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n); Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n); Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static (double Peak, double Rms) Levels(float[] l, float[] r)
        {
            double pk = 1e-9, s = 0;
            for (int i = 0; i < l.Length; i++) { pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i]))); s += (double)l[i] * l[i] + (double)r[i] * r[i]; }
            return (20 * Math.Log10(pk), 10 * Math.Log10(s / (2.0 * l.Length)));
        }

        [Test, Explicit("writes the local listening pack")]
        public void Pack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_DRIVE_PACK");
            Assert.That(dir, Is.Not.Null.And.Not.Empty, "NEO_DRIVE_PACK names the folder");
            Directory.CreateDirectory(dir);
            var sources = new Dictionary<string, (float[] L, float[] R)>
            {
                ["Neo Poly saw bass riff"] = BassSource(),
                ["Neo Drums loop"] = DrumSource(),
                ["Neo Poly chord pad"] = PadSource(),
            };
            // Every source at -6 dBFS peak, like a track in a mix.
            foreach (var k in sources.Keys.ToList())
            {
                var (l, r) = sources[k];
                double pk = 1e-9;
                for (int i = 0; i < l.Length; i++) pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                float g = (float)(Math.Pow(10, -6.0 / 20) / pk);
                for (int i = 0; i < l.Length; i++) { l[i] *= g; r[i] *= g; }
            }
            var plan = new (string Preset, string Source, string Listen)[]
            {
                ("Overdrive — Warm Crunch", "Neo Poly saw bass riff", "a mid-forward crunch on the riff: the hump at the centre of the Character range makes the notes speak, the lows stay warm"),
                ("Fuzz — Sputter Fuzz", "Neo Poly saw bass riff", "hard asymmetric fuzz on the riff; as each note decays the starved bias gates it and it spits. The detector may flag the spitting: it is intended"),
                ("Hard clip — Loud Clip", "Neo Drums loop", "a clean, loud clipper on the drums: flat tops without grit, the hats and snare get louder"),
                ("Rectify — Octave Grit", "Neo Poly chord pad", "octave-up grit under the chords: half-wave and full-wave rectification blended, at 75 % mix"),
                ("Transformer — Heavy Iron", "Neo Drums loop", "the lows saturate first: the kick thickens and compresses, the hats stay open"),
                ("Hard clip — Loud Clip", "Neo Poly saw bass riff", "the same clipper on a saw bass: odd harmonics in a clean hard square"),
                ("Rectify — Octave Grit", "Neo Drums loop", "octave-up grit on drums: the kick rings an octave higher, the snare buzzes"),
                ("Transformer — Heavy Iron", "Neo Poly chord pad", "iron on the pad: the low chord notes thicken and soften, the top stays clear"),
            };
            var log = new StringBuilder();
            log.AppendLine("# Neo Drive listening pack (EXT3, issue #192)");
            log.AppendLine();
            log.AppendLine("Five new Modes (Overdrive, Fuzz, Hard clip, Rectify, Transformer) at their new factory presets, each on the source it suits, then Hard clip, Rectify and");
            log.AppendLine("Transformer on a second source. Sources, at 120 BPM, 8 bars (16 s), each at -6 dBFS peak before Neo Drive: a Neo Poly saw bass riff (plain patch: a saw through");
            log.AppendLine("a 1.4 kHz low-pass, plucked), a Neo Drums loop (\"Classic — Punchy Kit\"), a Neo Poly chord pad (\"Pad — Warm Strings\": Am, F, C, G, twice).");
            log.AppendLine("Every file: 48 kHz, 16-bit, stereo WAV, Neo Drive at its factory preset (Quality 4x, Auto gain on), played through the engine block by block.");
            log.AppendLine("Peak and RMS are of the file (both channels, dBFS). \"Differs from dry\" is the RMS of (file minus the source delayed by the latency) against the source, in dB: -6 dB or more is plainly audible, 0 dB is as loud as the source itself. Click detector (second difference against its local mean, ClickDetector.cs): clicks in the file that the");
            log.AppendLine("source does not have itself. Fuzz sputter on decays is intended, and is labelled where the detector flags it.");
            log.AppendLine();
            log.AppendLine("| File | Preset | Source | What to listen for | Source peak / RMS | Peak | RMS | Differs from dry | Clicks |");
            log.AppendLine("|---|---|---|---|---|---|---|---|---|");
            int index = 0, unexpected = 0;
            foreach (var (preset, source, listen) in plan)
            {
                index++;
                var (l, r) = sources[source];
                var (ol, or) = Through(PresetValues(Params.Table, "Neo Drive", preset), l, r);
                var clicks = NewClicks(l, ol).Union(NewClicks(r, or)).ToList();
                var (pk, rms) = Levels(ol, or);
                var (spk, srms) = Levels(l, r);
                double dd = 0, ds = 0;
                int lat = DriveEngine.Latency;
                for (int i = lat; i < l.Length; i++) { double e = ol[i] - l[i - lat]; dd += e * e; ds += (double)l[i - lat] * l[i - lat]; }
                double differs = 10 * Math.Log10(dd / ds);
                string file = $"{index:00}-{Slug(preset)}-{Slug(source)}.wav";
                WriteWav(Path.Combine(dir, file), ol, or);
                bool sputter = preset.StartsWith("Fuzz");
                if (!sputter) unexpected += clicks.Count;
                string note = clicks.Count == 0 ? "0" : sputter ? $"{clicks.Count} (sputter, intended)" : clicks.Count.ToString();
                log.AppendLine($"| {file} | {preset} | {source} | {listen} | {spk:0.0} / {srms:0.0} dBFS | {pk:0.0} dBFS | {rms:0.0} dBFS | {differs:0.0} dB | {note} |");
                TestContext.Out.WriteLine($"{file}: peak {pk:0.0} dBFS, RMS {rms:0.0} dBFS, differs from dry {differs:0.0} dB, clicks {clicks.Count}");
            }
            log.AppendLine();
            log.AppendLine($"Click detector, all files except the fuzz sputter: {unexpected} clicks.");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            Assert.That(unexpected, Is.EqualTo(0));
        }
    }
}

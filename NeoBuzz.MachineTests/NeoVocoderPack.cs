using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Learn;
using NeoBuzz.Vocoder;
using static NeoBuzz.MachineTests.VocoderRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Vocoder listening pack (docs/specs/neo-vocoder.md): every factory preset for 15 s at 100 BPM, the modulator dry for the first 3 s
    /// (so the ear has it) and then through the preset. The modulators are made in code: a sung vowel line from Neo Learn Formant with noise-burst
    /// consonants, and a Neo Drums loop; the carriers are the machine's synth playing chords or lines, its noise, and a Neo Poly pad wired into the
    /// sidechain for the Sidechain preset. 48 kHz, 16-bit, stereo WAV, with a README. Local only, never committed: run it by hand with
    /// NEO_VOCODER_PACK set to the output folder.
    ///   NEO_VOCODER_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~NeoVocoderPack"
    /// </summary>
    [TestFixture]
    public class NeoVocoderPack
    {
        const double Bpm = 100;
        const int Length = 15 * Rate;
        const int DrySamples = 3 * Rate;
        static readonly double Beat = Rate * 60.0 / Bpm;

        static void Fill(ParamValues v, IReadOnlyDictionary<int, int> values) { foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value); }

        static bool At(double sample, int pos, int block) => (int)Math.Ceiling(sample) >= pos && (int)Math.Ceiling(sample) < pos + block;

        static (float[] L, float[] R) Run(Action<float[], float[], int> process, Action<int> atBlock)
        {
            var ol = new float[Length]; var or = new float[Length];
            var l = new float[256]; var r = new float[256];
            for (int pos = 0; pos < Length; pos += 256)
            {
                atBlock?.Invoke(pos);
                int n = Math.Min(256, Length - pos);
                Array.Clear(l, 0, 256); Array.Clear(r, 0, 256);
                process(l, r, n);
                Array.Copy(l, 0, ol, pos, n); Array.Copy(r, 0, or, pos, n);
            }
            return (ol, or);
        }

        static void Normalise(float[] l, float[] r, double db)
        {
            double peak = 1e-9;
            for (int i = 0; i < l.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            float g = (float)(Math.Pow(10, db / 20) / peak);
            for (int i = 0; i < l.Length; i++) { l[i] *= g; r[i] *= g; }
        }

        /// <summary>A sung vowel line: Neo Learn Formant ("Choir — Ah Pad") playing a melody, with a short burst of high-passed noise at the start of every note (the consonant).</summary>
        static (float[], float[]) Voice()
        {
            var p = PresetTests.AllPresets().First(x => x.Machine == "Neo Learn Formant" && x.Preset == "Choir — Ah Pad");
            var v = new ParamValues(NeoBuzz.LearnFormant.Params.Table);
            Fill(v, p.Values);
            var h = new LearnHost(new FormantMap(NeoBuzz.LearnFormant.Params.Table), v);
            h.SetSampleRate(Rate);
            int[] line = { 57, 60, 64, 62, 60, 57, 55, 57, 60, 64, 67, 64, 62, 60, 57, 0, 52, 55, 59, 57, 55, 52, 50, 52, 57, 0, 0, 0 };
            double step = Beat / 2;
            var rng = new Rng(77);
            float hp = 0;
            var (l, r) = Run((a, b, n) => h.Process(a, b, n), pos =>
            {
                for (int s = 0; s < line.Length; s++)
                {
                    if (line[s] == 0) continue;
                    if (At(s * step, pos, 256)) h.Engine.NoteOn(s % 8, line[s], 0.85f);
                    if (At(s * step + step * 0.85, pos, 256)) h.Engine.NoteOff(s % 8);
                }
            });
            // The consonants: 40 ms Hann-windowed noise, differentiated (a high-pass), at the start of each note.
            for (int s = 0; s < line.Length; s++)
            {
                if (line[s] == 0) continue;
                int start = (int)(s * step), len = Rate * 40 / 1000;
                for (int i = 0; i < len && start + i < Length; i++)
                {
                    float x = rng.NextBipolar();
                    float d = x - hp; hp = x;
                    float w = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * (i + 0.5f) / len);
                    l[start + i] += 0.12f * d * w; r[start + i] += 0.12f * d * w;
                }
            }
            Normalise(l, r, -3);
            return (l, r);
        }

        /// <summary>A Neo Drums loop: kick, snare, hats and an open hat, 100 BPM.</summary>
        static (float[], float[]) Drums()
        {
            var p = PresetTests.AllPresets().First(x => x.Machine == "Neo Drums" && x.Preset == "Classic — Punchy Kit");
            var v = new ParamValues(NeoBuzz.Drums.Params.Table);
            Fill(v, p.Values);
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            double step = Beat / 4;
            var (l, r) = Run((a, b, n) => e.Process(a, b, n), pos =>
            {
                for (int s = 0; s < 14 * 4 * 4 / 4 * 4; s++)
                {
                    if (!At(s * step, pos, 256)) continue;
                    int b = s % 16, bar = s / 16;
                    if (b == 0 || b == 10 || (b == 7 && bar % 2 == 1)) e.NoteOn(0, 36, b == 0 ? 0.95f : 0.8f);
                    if (b == 4 || b == 12) e.NoteOn(1, 38, 0.9f);
                    if (b % 2 == 0) e.NoteOn(2, 42, b % 4 == 0 ? 0.8f : 0.5f);
                    if (b == 14) e.NoteOn(3, 46, 0.6f);
                }
            });
            Normalise(l, r, -3);
            return (l, r);
        }

        /// <summary>A Neo Poly pad ("Pad — Warm Strings") holding a C minor 7 chord, the carrier of the Sidechain preset.</summary>
        static (float[], float[]) Pad()
        {
            var p = PresetTests.AllPresets().First(x => x.Machine == "Neo Poly" && x.Preset == "Pad — Warm Strings");
            var v = new ParamValues(NeoBuzz.Poly.Params.Table);
            Fill(v, p.Values);
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            var (l, r) = Run((a, b, n) => e.Process(a, b, n), pos =>
            {
                if (pos == 0) { e.NoteOn(0, 48, 0.8f); e.NoteOn(1, 55, 0.8f); e.NoteOn(2, 63, 0.8f); e.NoteOn(3, 70, 0.8f); }
            });
            Normalise(l, r, -6);
            return (l, r);
        }

        /// <summary>The notes the Synth carrier plays for each preset (start in beats from 3 s, length in beats, keys); none for Noise and Sidechain.</summary>
        static List<Note> Notes(string preset)
        {
            var list = new List<Note>();
            int t0 = DrySamples;
            void Chord(double beat, double len, params int[] keys)
            {
                foreach (int k in keys)
                {
                    list.Add(new Note(t0 + (int)(beat * Beat), true, k, 100));
                    list.Add(new Note(t0 + (int)((beat + len) * Beat), false, k));
                }
            }
            switch (preset)
            {
                case "Robot — Classic 16 Band":
                    for (int i = 0; i < 20; i++) Chord(i * 1.0, 0.9, new[] { 48, 51, 55, 51, 53, 56, 60, 56 }[i % 8]);
                    break;
                case "Choir — Wide Chord Pad":
                    Chord(0, 3.6, 48, 55, 60, 63, 67); Chord(4, 3.6, 44, 51, 56, 60, 63); Chord(8, 3.6, 46, 53, 58, 62, 65); Chord(12, 5, 43, 50, 55, 59, 62);
                    break;
                case "Bright — 40 Band Detail":
                    Chord(0, 7.5, 48, 55, 60, 63, 67, 70); Chord(8, 8, 46, 53, 58, 62, 65, 69);
                    break;
                case "Deep — Formant Down":
                    for (int i = 0; i < 20; i++) Chord(i * 1.0, 0.9, new[] { 36, 36, 39, 36, 41, 36, 39, 43 }[i % 8], new[] { 36, 36, 39, 36, 41, 36, 39, 43 }[i % 8] + 7);
                    break;
                case "Small — Formant Up":
                    Chord(0, 3.8, 60, 64, 67, 72); Chord(4, 3.8, 57, 60, 64, 69); Chord(8, 3.8, 55, 59, 62, 67); Chord(12, 5, 60, 64, 67, 72);
                    break;
                case "Drone — Frozen Vowel":
                    Chord(0, 20, 36, 43, 48, 51, 55, 60);
                    break;
            }
            return list;
        }

        static string Slug(string s) =>
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

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

        static Dictionary<string, string> Comments
        {
            get
            {
                var map = new Dictionary<string, string>();
                string dir = TestContext.CurrentContext.TestDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "NeoVocoder", "Presets.txt"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) return map;
                string name = null;
                foreach (var line in File.ReadAllLines(Path.Combine(dir, "Machines", "NeoVocoder", "Presets.txt")))
                {
                    if (line.StartsWith("preset \"")) name = line.Substring(8).TrimEnd('"');
                    else if (name != null && line.StartsWith("comment \"")) map[name] = line.Substring(9).TrimEnd('"');
                }
                return map;
            }
        }

        [Test, Explicit("writes the listening pack: set NEO_VOCODER_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_VOCODER_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_VOCODER_PACK");
            Directory.CreateDirectory(dir);
            var voice = Voice();
            var drums = Drums();
            var pad = Pad();
            var onDrums = new HashSet<string> { "Bright — 40 Band Detail", "Sidechain — Synth Carrier" };

            var log = new StringBuilder();
            log.AppendLine("# Neo Vocoder listening pack");
            log.AppendLine();
            log.AppendLine($"Every file: {Length / Rate} s at {Bpm:0} BPM, 48 kHz, 16-bit stereo WAV. The modulator is heard **dry for the first 3 s**, then through the preset (a 20 ms crossfade). Modulators, both made in code at -3 dBFS peak:");
            log.AppendLine("- **voice**: a sung vowel line (Neo Learn Formant, \"Choir — Ah Pad\") with a burst of high-passed noise at every note start as the consonant;");
            log.AppendLine("- **drums**: a Neo Drums loop (\"Classic — Punchy Kit\": kick, snare, closed and open hats).");
            log.AppendLine("Carriers: the machine's own synth playing the chords or the line listed below (Robot, Choir, Bright, Deep, Small, Drone), its noise (Whisper), and a Neo Poly pad (\"Pad — Warm Strings\", a C minor seventh chord) wired into the sidechain (Sidechain). No third-party audio, no speech model. Click detector (second difference against its local mean) on every file: clicks that the dry modulator does not have.");
            log.AppendLine();
            log.AppendLine("| File | Preset | Modulator | Carrier | What to listen for | Peak | RMS | Clicks |");
            log.AppendLine("|---|---|---|---|---|---|---|---|");
            int index = 0, clicksTotal = 0;
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Vocoder"))
            {
                index++;
                var v = new ParamValues(Params.Table);
                Fill(v, p.Values);
                bool d = onDrums.Contains(p.Preset);
                var (l, r) = d ? drums : voice;
                var e = Engine(v);
                bool side = v.Choice(P.Carrier) == (int)VocoderCarrier.Sidechain;
                var (wl, wr) = Render(e, l, r, side ? pad.Item1 : null, side ? pad.Item2 : null, 256, v.Choice(P.Carrier) == (int)VocoderCarrier.Synth ? Notes(p.Preset) : null);
                // Dry for the first 3 s, then the vocoder, over a 20 ms crossfade.
                var ol = new float[Length]; var or = new float[Length];
                int fade = Rate * 20 / 1000;
                for (int i = 0; i < Length; i++)
                {
                    float w = i < DrySamples ? 0f : i >= DrySamples + fade ? 1f : (float)(i - DrySamples) / fade;
                    ol[i] = l[i] * (1 - w) + wl[i] * w;
                    or[i] = r[i] * (1 - w) + wr[i] * w;
                }
                var dryClicks = new HashSet<int>(ClickDetector.Find(l).Concat(ClickDetector.Find(r)));
                var clicks = ClickDetector.Find(ol).Concat(ClickDetector.Find(or)).Where(c => !dryClicks.Any(x => Math.Abs(x - c) < 64)).ToList();
                clicksTotal += clicks.Count;
                double pk = 0, sq = 0;
                for (int i = DrySamples; i < Length; i++)
                {
                    pk = Math.Max(pk, Math.Max(Math.Abs(ol[i]), Math.Abs(or[i])));
                    sq += 0.5 * ((double)ol[i] * ol[i] + (double)or[i] * or[i]);
                }
                string file = $"{index:00}-{Slug(p.Preset)}.wav";
                WriteWav(Path.Combine(dir, file), ol, or);
                string carrier = side ? "Neo Poly pad in the sidechain" : v.Choice(P.Carrier) == (int)VocoderCarrier.Noise ? "noise" : "synth, " + string.Join(" ", Notes(p.Preset).Where(n => n.On).Select(n => n.Key).Distinct().Take(8)) + (Notes(p.Preset).Count(n => n.On) > 8 ? " ..." : "");
                log.AppendLine($"| {file} | {p.Preset} | {(d ? "drums" : "voice")} | {carrier} | {Comments.GetValueOrDefault(p.Preset, "")} | {20 * Math.Log10(pk):0.0} dBFS | {10 * Math.Log10(sq / (Length - DrySamples)):0.0} dBFS | {clicks.Count} |");
            }
            log.AppendLine();
            log.AppendLine($"Click detector, all files: {clicksTotal} new clicks. Peak and RMS are of the vocoded part (after 3 s).");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            TestContext.Out.WriteLine(log.ToString());
            Assert.That(clicksTotal, Is.EqualTo(0));
        }
    }
}

using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.Filter;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Filter vowel listening pack (EXT1): the four Vowel presets, each on two sources, 8 bars at 120 BPM (16 s), written as 16-bit 48 kHz
    /// stereo WAV files with a README. The sources are a Neo Drums loop and a Neo Poly saw riff. Local only, never committed: run it by hand with
    /// NEO_FILTER_VOWEL_PACK set to the output folder.
    ///   NEO_FILTER_VOWEL_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~FilterVowelPack"
    /// </summary>
    [TestFixture]
    public class FilterVowelPack
    {
        const int Rate = Measure.Rate;
        const int Bars = 8;
        static readonly double Spb = Math.Round(60.0 * Rate / (120 * 4)) * 4;       // samples per beat, a whole number of samples per tick
        static readonly int Step = (int)(Spb / 2);                                  // an eighth note
        static readonly int Length = (int)(Spb * 4 * Bars);

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
                atBlock?.Invoke(pos);
                int n = Math.Min(256, length - pos);
                Array.Clear(l, 0, 256);
                Array.Clear(r, 0, 256);
                process(l, r, n);
                Array.Copy(l, 0, ol, pos, n);
                Array.Copy(r, 0, or, pos, n);
            }
            return (ol, or);
        }

        /// <summary>Neo Drums: a four-bar pattern played twice.</summary>
        static (float[], float[]) DrumsSource()
        {
            var v = PresetValues(NeoBuzz.Drums.Params.Table, "Neo Drums", "Classic — Punchy Kit");
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            int sixteenth = (int)(Spb / 4);
            return Run((l, r, n) => e.Process(l, r, n), Length, pos =>
            {
                for (int s = 0; s < Bars * 16; s++)
                {
                    int at = s * sixteenth;
                    if (at < pos || at >= pos + 256) continue;
                    int b = s % 16, bar = (s / 16) % 4;
                    if (b == 0 || b == 10 || b == 8 || (b == 13 && bar % 2 == 1)) e.NoteOn(0, 36, b == 0 ? 0.95f : 0.8f);
                    if (b == 4 || b == 12) e.NoteOn(1, 38, 0.9f);
                    if (b % 2 == 0) e.NoteOn(2, 42, b % 4 == 0 ? 0.8f : 0.5f);
                    if (b == 14) e.NoteOn(3, 46, 0.6f);
                }
            });
        }

        /// <summary>Neo Poly: a saw riff on eighth notes, in A minor, four bars played twice.</summary>
        static (float[], float[]) RiffSource()
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Lead — Smooth Saw");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[][] bars =
            {
                new[] { 57, 57, 60, 57, 64, 62, 60, 57 },
                new[] { 53, 53, 57, 53, 60, 59, 57, 53 },
                new[] { 48, 48, 52, 48, 55, 53, 52, 48 },
                new[] { 55, 55, 59, 55, 62, 60, 59, 55 },
            };
            return Run((l, r, n) => e.Process(l, r, n), Length, pos =>
            {
                for (int s = 0; s < Bars * 8; s++)
                {
                    int on = s * Step, off = on + (int)(Step * 0.85);
                    int key = s % 4, note = bars[(s / 8) % 4][s % 8];
                    if (on >= pos && on < pos + 256) e.NoteOn(key, note, 0.8f);
                    if (off >= pos && off < pos + 256) e.NoteOff(key);
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

        static string Slug(string s) =>
            new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        static readonly Dictionary<string, string> Listen = new()
        {
            ["Vowel — Talking Wah"] = "The sound talks in time: the vowel travels A, E, I, O, U and back once every two beats, a man's voice. On the drums the hats and snares turn into 'wah' syllables; on the riff every note takes the moving vowel.",
            ["Vowel — Choir Ah"] = "One fixed vowel, a woman's Ah, on everything. The riff turns into a choir singing the line; the drums get a vowel colour on every hit (kick: a soft 'oo-ah' thump, hats: hissy 'ah').",
            ["Vowel — Envelope Yeah"] = "Each loud hit opens from I to A (a 'yeah') and closes again as it fades. Listen for a vowel on every note or drum hit, following its loudness; the quiet notes stay at I.",
            ["Vowel — Robot Vowels"] = "A new vowel every eighth note, chosen by a random step: a man's voice that jumps between A, E, I, O and U, sharper and slightly driven. The same sequence returns every time the song plays.",
        };

        [Test, Explicit("writes the listening pack: set NEO_FILTER_VOWEL_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_FILTER_VOWEL_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_FILTER_VOWEL_PACK");
            Directory.CreateDirectory(dir);
            var sources = new (string Name, string Detail, (float[] L, float[] R) Audio)[]
            {
                ("drums", "Neo Drums loop (\"Classic — Punchy Kit\", four bars played twice)", DrumsSource()),
                ("riff", "Neo Poly saw riff (\"Lead — Smooth Saw\", eighth notes in A minor, four bars played twice)", RiffSource()),
            };
            int samplesPerTick = (int)(Spb / 4);
            var log = new StringBuilder();
            log.AppendLine("# Neo Filter vowel mode: listening pack (EXT1, #190)");
            log.AppendLine();
            log.AppendLine($"The four \"Vowel\" presets of Neo Filter, each on two sources, at 120 BPM in 4/4: {Bars} bars, {Length / (double)Rate:0.0} s each, every file 48 kHz, 16-bit, stereo WAV.");
            log.AppendLine("Each source is scaled to -6 dBFS peak before the filter, and each output file is then brought to -3 dBFS peak (the gain is in the table: the vowel filter's level against a source differs by about 20 dB between the drums and the riff). The song plays from beat 0 (the presets' LFOs follow the song).");
            log.AppendLine("Click detector (second difference against its local mean, `ClickDetector`) on the output; \"new clicks\" are those the dry source does not have within 2 ms.");
            log.AppendLine();
            log.AppendLine("| File | Preset | Source | What to listen for | Peak | RMS | Gain applied | Clicks | New clicks |");
            log.AppendLine("|---|---|---|---|---|---|---|---|---|");
            int index = 0, newTotal = 0;
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Filter" && p.Preset.StartsWith("Vowel")))
                foreach (var (name, detail, audio) in sources)
                {
                    index++;
                    double peak0 = 1e-9;
                    for (int i = 0; i < Length; i++) peak0 = Math.Max(peak0, Math.Max(Math.Abs(audio.L[i]), Math.Abs(audio.R[i])));
                    float g = (float)(Math.Pow(10, -6.0 / 20) / peak0);
                    var dl = audio.L.Select(x => x * g).ToArray();
                    var dr = audio.R.Select(x => x * g).ToArray();
                    var v = new ParamValues(Params.Table);
                    foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                    var e = new FilterEngine(v) { Tempo = 120 };
                    e.SetSampleRate(Rate);
                    int block = 0;
                    var ol = new float[Length];
                    var or = new float[Length];
                    var bl = new float[256];
                    var br = new float[256];
                    for (int pos = 0; pos < Length; pos += 256, block++)
                    {
                        int n = Math.Min(256, Length - pos);
                        e.Transport = new FilterTransport { Playing = true, Tick = pos / samplesPerTick, PosInTick = pos % samplesPerTick, SamplesPerTick = samplesPerTick, TicksPerBeat = 4 };
                        Array.Copy(dl, pos, bl, 0, n);
                        Array.Copy(dr, pos, br, 0, n);
                        e.Process(bl, br, n);
                        Array.Copy(bl, 0, ol, pos, n);
                        Array.Copy(br, 0, or, pos, n);
                    }
                    // Each file is brought to -3 dBFS peak (the vowel filter's level against a source varies by 20 dB between the drums and the riff), the same gain on the dry source for the click comparison.
                    double raw = 1e-9, rawSq = 0;
                    for (int i = 0; i < Length; i++)
                    {
                        raw = Math.Max(raw, Math.Max(Math.Abs(ol[i]), Math.Abs(or[i])));
                        rawSq += 0.5 * ((double)ol[i] * ol[i] + (double)or[i] * or[i]);
                    }
                    float g2 = (float)(Math.Pow(10, -3.0 / 20) / raw);
                    for (int i = 0; i < Length; i++) { ol[i] *= g2; or[i] *= g2; dl[i] *= g2; dr[i] *= g2; }
                    var clicks = ClickDetector.Find(ol, or);
                    var own = ClickDetector.Find(dl, dr);
                    var fresh = clicks.Where(c => !own.Any(o => Math.Abs(o - c) <= 96)).ToList();
                    newTotal += fresh.Count;
                    double pk = 0, sq = 0;
                    for (int i = 0; i < Length; i++)
                    {
                        pk = Math.Max(pk, Math.Max(Math.Abs(ol[i]), Math.Abs(or[i])));
                        sq += 0.5 * ((double)ol[i] * ol[i] + (double)or[i] * or[i]);
                    }
                    string file = $"{index:00}-{Slug(p.Preset)}-{name}.wav";
                    WriteWav(Path.Combine(dir, file), ol, or);
                    log.AppendLine($"| {file} | {p.Preset} | {detail} | {Listen.GetValueOrDefault(p.Preset, "")} | {20 * Math.Log10(pk):0.0} dBFS | {10 * Math.Log10(sq / Length):0.0} dBFS | {20 * Math.Log10(g2):+0.0;-0.0} dB (the filter alone: peak {20 * Math.Log10(raw):0.0} dBFS, RMS {10 * Math.Log10(rawSq / Length):0.0} dBFS) | {clicks.Count} | {fresh.Count} |");
                    TestContext.Out.WriteLine($"{file}: peak {20 * Math.Log10(pk):0.0} dBFS, rms {10 * Math.Log10(sq / Length):0.0} dBFS, clicks {clicks.Count} (new {fresh.Count}; source's own {own.Count})");
                }
            log.AppendLine();
            log.AppendLine($"Click detector, all files: {newTotal} new clicks.");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            Assert.That(newTotal, Is.EqualTo(0));
        }
    }
}

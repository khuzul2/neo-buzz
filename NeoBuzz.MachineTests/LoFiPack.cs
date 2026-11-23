using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.LoFi;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Lo-Fi listening pack (docs/specs/neo-lofi.md): every factory preset on three sources made by our own Neo
    /// machines (a Neo Drums loop, a Neo Poly chord progression, Neo Sampler keys), written as 16-bit WAV files with a README.
    /// Local only, never committed: run it by hand with NEO_LOFI_PACK set to the output folder.
    ///   NEO_LOFI_PACK=/some/folder dotnet test NeoBuzz.MachineTests -c Release --filter "FullyQualifiedName~LoFiPack"
    /// </summary>
    [TestFixture]
    public class LoFiPack
    {
        const int Rate = Measure.Rate;

        static ParamValues PresetValues(ParamDef[] table, string machine, string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == machine && p.Preset == preset);
            var v = new ParamValues(table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static (float[] L, float[] R) Render(Action<float[], float[], int> process, int length, Action<int> atBlock)
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

        /// <summary>Neo Drums: four bars at 112 BPM, kick, snare, closed and open hat.</summary>
        static (float[], float[]) Drums()
        {
            var v = PresetValues(NeoBuzz.Drums.Params.Table, "Neo Drums", "909 — Classic");
            var e = new NeoBuzz.Drums.DrumsEngine(v);
            e.SetSampleRate(Rate);
            int step = (int)(Rate * 60.0 / 112 / 4);
            int length = step * 64 + Rate;
            return Render((l, r, n) => e.Process(l, r, n), length, pos =>
            {
                // The steps that start in this block.
                for (int s = 0; s < 64; s++)
                {
                    int at = s * step;
                    if (at < pos || at >= pos + 256) continue;
                    int b = s % 16;
                    if (b == 0 || b == 4 + 6 || b == 8 || b == 12 + 1 && s / 16 % 2 == 1) e.NoteOn(0, 36, b == 0 ? 0.95f : 0.8f);
                    if (b == 4 || b == 12) e.NoteOn(1, 38, 0.9f);
                    if (b % 2 == 0) e.NoteOn(2, 42, b % 4 == 0 ? 0.8f : 0.5f);
                    if (b == 14) e.NoteOn(3, 46, 0.6f);
                }
            });
        }

        /// <summary>Neo Poly: Am, F, C, G as four-note chords, 2.2 s each.</summary>
        static (float[], float[]) Chords()
        {
            var v = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", "Pad — Warm Strings");
            var e = new NeoBuzz.Poly.PolyEngine(v);
            e.SetSampleRate(Rate);
            int[][] chords = { new[] { 45, 52, 57, 60 }, new[] { 41, 48, 57, 60 }, new[] { 48, 52, 55, 60 }, new[] { 43, 50, 55, 59 } };
            int each = (int)(2.2 * Rate);
            int length = each * 4 + Rate;
            return Render((l, r, n) => e.Process(l, r, n), length, pos =>
            {
                for (int c = 0; c < 4; c++)
                {
                    if (c * each >= pos && c * each < pos + 256)
                        for (int k = 0; k < 4; k++) e.NoteOn(k, chords[c][k], 0.7f);
                    int off = c * each + (int)(2.0 * Rate);
                    if (off >= pos && off < pos + 256)
                        for (int k = 0; k < 4; k++) e.NoteOff(k);
                }
            });
        }

        /// <summary>Neo Sampler: an electric-piano riff, chords and a melody on top, 9 s.</summary>
        static (float[], float[]) Keys()
        {
            NeoBuzz.Sampler.BuiltIns.BuildNow();
            var bank = new NeoBuzz.Sampler.SampleBank();
            for (int k = 1; k <= 16; k++) bank.Set(k, NeoBuzz.Sampler.BuiltIns.Get(k));
            var v = PresetValues(NeoBuzz.Sampler.Params.Table, "Neo Sampler", "Keys — Electric Piano (Two Velocity Layers)");
            var e = new NeoBuzz.Sampler.SamplerEngine(v, bank);
            e.SetSampleRate(Rate);
            // (start in beats of 0.5 s, length in beats, note, velocity)
            var notes = new List<(double At, double Len, int Note, float Vel)>();
            int[][] chords = { new[] { 57, 60, 64 }, new[] { 53, 57, 60 }, new[] { 55, 60, 64 }, new[] { 55, 59, 62 } };
            for (int c = 0; c < 4; c++)
                foreach (int n in chords[c]) notes.Add((c * 4, 3.5, n, 0.6f));
            int[] melody = { 76, 74, 72, 74, 72, 69, 67, 69, 72, 71, 69, 67, 67, 71, 74, 71 };
            for (int i = 0; i < melody.Length; i++) notes.Add((i, 0.9, melody[i], 0.8f));
            int length = (int)(9.5 * Rate);
            return Render((l, r, n) => e.Process(l, r, n), length, pos =>
            {
                for (int i = 0; i < notes.Count; i++)
                {
                    var nt = notes[i];
                    int a = (int)(nt.At * 0.5 * Rate), b = (int)((nt.At + nt.Len) * 0.5 * Rate);
                    if (a >= pos && a < pos + 256) e.NoteOn(i, nt.Note, nt.Vel);
                    if (b >= pos && b < pos + 256) e.NoteOff(i);
                }
            });
        }

        static void Normalise((float[] L, float[] R) x, double peakDb)
        {
            double peak = 1e-9;
            for (int i = 0; i < x.L.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(x.L[i]), Math.Abs(x.R[i])));
            float g = (float)(Math.Pow(10, peakDb / 20) / peak);
            for (int i = 0; i < x.L.Length; i++) { x.L[i] *= g; x.R[i] *= g; }
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

        [Test, Explicit("writes the listening pack: set NEO_LOFI_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_LOFI_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_LOFI_PACK");
            Directory.CreateDirectory(dir);
            var sources = new (string Name, Func<(float[], float[])> Make)[] { ("drums", Drums), ("chords", Chords), ("keys", Keys) };
            var presets = PresetTests.AllPresets().Where(p => p.Machine == "Neo Lo-Fi").ToList();
            var readme = new StringBuilder();
            readme.AppendLine("# Neo Lo-Fi listening pack");
            readme.AppendLine();
            readme.AppendLine("Every factory preset of Neo Lo-Fi on three sources, written by `NeoBuzz.MachineTests/LoFiPack.cs` (an explicit test; nothing here is committed).");
            readme.AppendLine("This is not a blind test: it is here for you to decide whether the sound is right. Each file is the whole machine at that preset, 48 kHz, 16-bit WAV, stereo.");
            readme.AppendLine();
            readme.AppendLine("## Sources (all made by our own Neo machines, the dry files are `00-dry__*.wav`)");
            readme.AppendLine();
            readme.AppendLine("- `drums`: Neo Drums, preset \"909 — Classic\", a four-bar loop at 112 BPM (kick, snare, closed and open hat).");
            readme.AppendLine("- `chords`: Neo Poly, preset \"Pad — Warm Strings\", Am - F - C - G, four notes each, 2.2 s a chord.");
            readme.AppendLine("- `keys`: Neo Sampler, preset \"Keys — Electric Piano (Two Velocity Layers)\", chords and a melody, 9.5 s.");
            readme.AppendLine();
            readme.AppendLine("Each source is normalised to a -3 dBFS peak before the machine; the processed files are written as the machine returns them (no loudness matching: the presets that add gain or noise are louder or quieter on purpose, so judge the character, then level).");
            readme.AppendLine();
            readme.AppendLine("## Presets");
            readme.AppendLine();
            var made = new Dictionary<string, (float[] L, float[] R)>();
            foreach (var (name, make) in sources)
            {
                var (l, r) = make();
                var x = (l, r);
                Normalise(x, -3);
                made[name] = x;
                WriteWav(Path.Combine(dir, $"00-dry__{name}.wav"), l, r);
            }
            int index = 1;
            foreach (var p in presets)
            {
                readme.AppendLine($"{index:00}. **{p.Preset}**");
                foreach (var (name, _) in sources)
                {
                    var v = new ParamValues(Params.Table);
                    foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                    var e = new LoFiEngine(v);
                    e.SetSampleRate(Rate);
                    var l = (float[])made[name].L.Clone();
                    var r = (float[])made[name].R.Clone();
                    var bl = new float[256];
                    var br = new float[256];
                    for (int pos = 0; pos < l.Length; pos += 256)
                    {
                        int n = Math.Min(256, l.Length - pos);
                        Array.Copy(l, pos, bl, 0, n);
                        Array.Copy(r, pos, br, 0, n);
                        e.Process(bl, br, n);
                        Array.Copy(bl, 0, l, pos, n);
                        Array.Copy(br, 0, r, pos, n);
                    }
                    WriteWav(Path.Combine(dir, $"{index:00}-{Slug(p.Preset)}__{name}.wav"), l, r);
                }
                index++;
            }
            File.WriteAllText(Path.Combine(dir, "README.md"), readme.ToString());
        }
    }
}

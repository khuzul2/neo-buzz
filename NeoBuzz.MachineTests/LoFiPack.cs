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

        /// <summary>The machine at a setting over a source, as the pack renders it (and the log's figures).</summary>
        internal static (float[] L, float[] R) Process(ParamValues v, (float[] L, float[] R) src, out LoFiEngine engine)
        {
            engine = new LoFiEngine(v);
            engine.SetSampleRate(Rate);
            var l = (float[])src.L.Clone();
            var r = (float[])src.R.Clone();
            var bl = new float[256];
            var br = new float[256];
            for (int pos = 0; pos < l.Length; pos += 256)
            {
                int n = Math.Min(256, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                engine.Process(bl, br, n);
                Array.Copy(bl, 0, l, pos, n);
                Array.Copy(br, 0, r, pos, n);
            }
            return (l, r);
        }

        static ParamValues Preset(PresetEntry p, double? amountPercent = null)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            if (amountPercent != null) v.Set(P.Amount, Params.Table[P.Amount].Raw(amountPercent.Value));
            return v;
        }

        internal sealed record PresetEntry(string Preset, Dictionary<int, int> Values);

        /// <summary>The factory presets; NEO_LOFI_PRESETS names another preset file (the first pack's, for the before figures).</summary>
        internal static List<PresetEntry> FactoryPresets()
        {
            string other = Environment.GetEnvironmentVariable("NEO_LOFI_PRESETS");
            if (string.IsNullOrEmpty(other))
                return PresetTests.AllPresets().Where(p => p.Machine == "Neo Lo-Fi").Select(p => new PresetEntry(p.Preset, p.Values)).ToList();
            var list = new List<PresetEntry>();
            foreach (var item in System.Xml.Linq.XDocument.Load(other).Root!.Elements("Item"))
            {
                var values = new Dictionary<int, int>();
                foreach (var q in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter")) values[(int)q.Attribute("Index")!] = (int)q.Attribute("Value")!;
                list.Add(new PresetEntry((string)item.Attribute("Key")!, values));
            }
            return list;
        }

        /// <summary>What is left of the wet signal after the best gain and delay (-12 to +12 samples) of the dry is taken away, in dB under the dry's level: the crush, noise and pops as a listener hears them, whatever the hold's or the motion's small delays.</summary>
        static double ResidualDb((float[] L, float[] R) wet, (float[] L, float[] R) dry)
        {
            double best = double.PositiveInfinity;
            int n = dry.L.Length;
            for (int lag = -12; lag <= 12; lag++)
            {
                double wd = 0, dd = 0, ww = 0;
                for (int i = 16; i < n - 16; i++)
                {
                    double w = wet.L[i + lag], d = dry.L[i];
                    wd += w * d; dd += d * d; ww += w * w;
                }
                double g = wd / dd;
                best = Math.Min(best, (ww - g * wd) / (g * g * dd));
            }
            return 10 * Math.Log10(Math.Max(1e-30, best));
        }

        /// <summary>Every parameter as the machine shows it, for the log: proof that a render ran with the preset's values.</summary>
        static string State(ParamValues v) =>
            string.Join("; ", Params.Table.Select(d => $"{d.Name} {d.Describe(v[d.Index])}"));

        /// <summary>Error to signal in dB: the RMS of (wet - dry) over the RMS of dry, both channels.</summary>
        static double ErrorToSignalDb((float[] L, float[] R) wet, (float[] L, float[] R) dry)
        {
            double e = 0, s = 0;
            for (int i = 0; i < dry.L.Length; i++)
            {
                double dl = wet.L[i] - dry.L[i], dr = wet.R[i] - dry.R[i];
                e += dl * dl + dr * dr;
                s += (double)dry.L[i] * dry.L[i] + (double)dry.R[i] * dry.R[i];
            }
            return 10 * Math.Log10(Math.Max(1e-30, e / s));
        }

        static string Rms((float[] L, float[] R) x) => $"{Measure.Db(Math.Sqrt((Measure.Rms(x.L, 0, x.L.Length) * Measure.Rms(x.L, 0, x.L.Length) + Measure.Rms(x.R, 0, x.R.Length) * Measure.Rms(x.R, 0, x.R.Length)) / 2)):0.0}";

        static (string Name, Func<(float[], float[])> Make)[] Sources() => new (string, Func<(float[], float[])>)[] { ("drums", Drums), ("chords", Chords), ("keys", Keys) };

        [Test, Explicit("writes the listening pack: set NEO_LOFI_PACK to an output folder (NEO_LOFI_NOWAV=1: only the log)")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_LOFI_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_LOFI_PACK");
            bool wav = Environment.GetEnvironmentVariable("NEO_LOFI_NOWAV") != "1";
            Directory.CreateDirectory(dir);
            var presets = FactoryPresets();
            var log = new StringBuilder();
            var made = new Dictionary<string, (float[] L, float[] R)>();
            foreach (var (name, make) in Sources())
            {
                var (l, r) = make();
                var x = (l, r);
                Normalise(x, -3);
                made[name] = x;
                if (wav) WriteWav(Path.Combine(dir, $"00-dry__{name}.wav"), l, r);
                log.AppendLine($"dry | {name} | peak {Measure.Db(Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length))):0.0} dBFS | rms {Rms(x)} dBFS | {l.Length} samples");
            }
            var report = new StringBuilder();
            report.AppendLine(LoFiStageReport.Header);
            var changes = LoFiPackChanges.Text;
            int index = 1;
            var readmeItems = new StringBuilder();
            foreach (var p in presets)
            {
                var v100 = Preset(p);
                report.AppendLine(LoFiStageReport.Row(p.Preset, LoFiStageReport.Measure_(v100)));
                readmeItems.AppendLine($"{index:00}. **{p.Preset}**" + (changes.TryGetValue(p.Preset, out var c) ? $": {c}" : ""));
                foreach (var (name, _) in Sources())
                {
                    var (l, r) = Process(Preset(p), made[name], out var e);
                    if (wav) WriteWav(Path.Combine(dir, $"{index:00}-{Slug(p.Preset)}__{name}.wav"), l, r);
                    log.AppendLine($"{index:00} {p.Preset} | {name} | {State(Preset(p))} | end of render: {e.CurrentBits:0.00} bits, hold {e.CurrentRate:0} Hz | residual {ResidualDb((l, r), made[name]):0.0} dB | rms {Rms((l, r))} dBFS | peak {Measure.Db(Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length))):0.0} dBFS");
                }
                if (changes.ContainsKey(p.Preset) && changes[p.Preset].Length > 0 && LoFiPackChanges.Retuned.Contains(p.Preset))
                    foreach (double amount in new[] { 50.0, 150.0 })
                    {
                        var v = Preset(p, amount);
                        var (l, r) = Process(v, made["drums"], out var e);
                        if (wav) WriteWav(Path.Combine(dir, $"{index:00}-{Slug(p.Preset)}__drums__amount{amount:000}.wav"), l, r);
                        log.AppendLine($"{index:00} {p.Preset} | drums at Amount {amount:0} % | {State(v)} | end of render: {e.CurrentBits:0.00} bits, hold {e.CurrentRate:0} Hz | residual {ResidualDb((l, r), made["drums"]):0.0} dB | rms {Rms((l, r))} dBFS | peak {Measure.Db(Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length))):0.0} dBFS");
                    }
                index++;
            }
            File.WriteAllText(Path.Combine(dir, "renders.log"), log.ToString());
            File.WriteAllText(Path.Combine(dir, "stages.md"), report.ToString());
            var readme = new StringBuilder();
            readme.AppendLine("# Neo Lo-Fi listening pack 2 (LF2)");
            readme.AppendLine();
            readme.AppendLine("Every factory preset of Neo Lo-Fi on three sources, written by `NeoBuzz.MachineTests/LoFiPack.cs` (an explicit test; nothing here is committed).");
            readme.AppendLine("This is not a blind test: it is here for you to decide whether the sound is right. Each file is the whole machine at that preset, 48 kHz, 16-bit WAV, stereo.");
            readme.AppendLine("The sources and their levels are the ones of the first pack, so you can put the two side by side. `renders.log` lists, for every file, the machine's parameter values as the render ran with them, and how far the output is from the dry source (the residual: what is left after the best gain and delay of the dry is taken away, in dB under the source; 0 dB is as loud as the source itself, -30 dB is hardly there).");
            readme.AppendLine();
            readme.AppendLine("## Sources (all made by our own Neo machines, the dry files are `00-dry__*.wav`)");
            readme.AppendLine();
            readme.AppendLine("- `drums`: Neo Drums, preset \"909 — Classic\", a four-bar loop at 112 BPM (kick, snare, closed and open hat).");
            readme.AppendLine("- `chords`: Neo Poly, preset \"Pad — Warm Strings\", Am - F - C - G, four notes each, 2.2 s a chord.");
            readme.AppendLine("- `keys`: Neo Sampler, preset \"Keys — Electric Piano (Two Velocity Layers)\", chords and a melody, 9.5 s.");
            readme.AppendLine();
            readme.AppendLine("Each source is normalised to a -3 dBFS peak before the machine; the processed files are written as the machine returns them (no loudness matching: the presets that add gain or noise are louder or quieter on purpose, so judge the character, then level).");
            readme.AppendLine();
            readme.AppendLine("## What is new");
            readme.AppendLine();
            readme.AppendLine("- **Amount** (0 to 200 %, 100 % = the preset as it stands): one knob for how strong the whole effect is. Files named `...__drums__amount050.wav` and `...__amount150.wav` are the seven retuned presets at 50 % and 150 % on the drums.");
            readme.AppendLine("- Seven presets are stronger. 04 Broken toy, 06 Worn tape and 08 AM radio, which you kept, are unchanged.");
            readme.AppendLine("- A fresh Neo Lo-Fi now sounds lo-fi: the defaults are 8 bits held at 12 kHz, not 12 bits and nothing else.");
            readme.AppendLine();
            readme.AppendLine("## Presets");
            readme.AppendLine();
            readme.Append(readmeItems);
            readme.AppendLine();
            readme.AppendLine("## What each stage measures at the preset (other stages switched off, test signals)");
            readme.AppendLine();
            readme.Append(report);
            File.WriteAllText(Path.Combine(dir, "README.md"), readme.ToString());
        }
    }
}

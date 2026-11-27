using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.RandomMachine;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Renders the local listening demo pack for issue #200 (Neo Random) into listening-packs/200/ (git-ignored).
    /// Per the Coordinator Note: 5 WAV files (48 kHz 16-bit stereo, 16.5 s each), putting two presets back to back
    /// per file (8.0 s preset A + 0.5 s silence + 8.0 s preset B), each driving Neo Poly's F1 cutoff on a held
    /// chord, plus README.md documenting which preset is in each half. Click detector must report 0 clicks.
    /// Run explicitly: dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~NeoRandomPack
    /// </summary>
    [TestFixture]
    public class NeoRandomPack
    {
        const int Rate = 48000;
        const double Bpm = 120.0;
        const int HalfSamples = 8 * Rate;       // 8.0 s per preset
        const int GapSamples = Rate / 2;        // 0.5 s silence between halves
        const int TotalSamples = HalfSamples * 2 + GapSamples; // 16.5 s per file

        sealed class PolyCutoffPort : IModPort
        {
            readonly ParamValues polyValues;
            readonly ParamDef def;
            int rawValue;
            public PolyCutoffPort(ParamValues polyValues, int initialRaw)
            {
                this.polyValues = polyValues;
                def = NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.F1Cutoff];
                rawValue = initialRaw;
                polyValues.Set(NeoBuzz.Poly.P.F1Cutoff, rawValue);
            }
            public int Min => def.Min;
            public int Max => def.Max;
            public int SafeMin => def.Min;
            public int SafeMax => def.Max;
            public int Get() => rawValue;
            public void Put(int raw) { rawValue = raw; polyValues.Set(NeoBuzz.Poly.P.F1Cutoff, raw); }
        }

        [Test, Explicit("Generates local listening-packs/200/ demo pack (5 x 16.5 s WAVs + README.md)")]
        public void BuildListeningPack200()
        {
            string root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
            string prsPath = Path.Combine(root, "Machines", "NeoRandom", "Neo Random.prs.xml");
            string outDir = Path.Combine(root, "listening-packs", "200");
            Directory.CreateDirectory(outDir);

            var doc = XDocument.Load(prsPath);
            var presets = doc.Root!.Elements("Item").Select(item =>
            {
                string key = (string)item.Attribute("Key")!;
                var v = new ParamValues(Params.Table);
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter"))
                    v.Set((int)p.Attribute("Index")!, (int)p.Attribute("Value")!);
                return (Name: key, Values: v);
            }).ToList();

            Assert.That(presets.Count, Is.EqualTo(10), "expected 10 factory presets");

            var pairs = new (string Slug, int A, int B)[]
            {
                ("01-gentle-drift__random-filter-steps", 0, 1),
                ("02-sh-sync-1-16__probability-glitch", 2, 3),
                ("03-chaos-wobble__slow-wander-bar", 4, 5),
                ("04-humanise-pan__stepped-pitch-12", 6, 7),
                ("05-random-gate__lorenz-swirl", 8, 9),
            };

            var readme = new StringBuilder();
            readme.AppendLine("# Neo Random — Demo Pack (Issue #200)");
            readme.AppendLine();
            readme.AppendLine("Rendered by `NeoBuzz.MachineTests/NeoRandomPack.cs` at 48 kHz, 16-bit stereo WAV, 120 BPM (git-ignored, never committed).");
            readme.AppendLine();
            readme.AppendLine("## Structure");
            readme.AppendLine("- **5 files, 16.5 s each:** two presets back-to-back (`0.0–8.0 s` first preset, `8.0–8.5 s` silence, `8.5–16.5 s` second preset), covering all 10 presets.");
            readme.AppendLine("- **Target:** each preset drives **Neo Poly's `F1 cutoff`** (`Depth = 80 %`, `Bipolar = true`, base `900 Hz`, Ladder 24 LP with `55 %` resonance) on a sustained saw/pulse Am9 chord (`A2, E3, G3, B3, C4`).");
            readme.AppendLine("- **Click detector:** frame-peak second-difference detector (128-sample frame max `|x[i+1] - 2x[i] + x[i-1]|` vs local mean of 14 neighbouring frames, threshold `4.5x` & `> 0.15`, 20 ms refractory).");
            readme.AppendLine();
            readme.AppendLine("| # | File | First half (`0.0–8.0 s`) | Second half (`8.5–16.5 s`) | RMS (dBFS) | Peak (dBFS) | Clicks |");
            readme.AppendLine("|---|---|---|---|---:|---:|---:|");

            int totalClicks = 0;
            for (int idx = 0; idx < pairs.Length; idx++)
            {
                var (slug, ia, ib) = pairs[idx];
                var a = presets[ia];
                var b = presets[ib];

                var (la, ra) = RenderHalf(a.Values);
                var (lb, rb) = RenderHalf(b.Values);

                var l = new float[TotalSamples];
                var r = new float[TotalSamples];
                Array.Copy(la, 0, l, 0, HalfSamples);
                Array.Copy(ra, 0, r, 0, HalfSamples);
                Array.Copy(lb, 0, l, HalfSamples + GapSamples, HalfSamples);
                Array.Copy(rb, 0, r, HalfSamples + GapSamples, HalfSamples);

                string wavName = slug + ".wav";
                WriteWav16Stereo(Path.Combine(outDir, wavName), l, r, Rate);

                int clicks = Math.Max(DetectClicks(l, Rate), DetectClicks(r, Rate));
                totalClicks += clicks;

                double rmsSq = 0;
                float peak = 0;
                for (int i = 0; i < TotalSamples; i++)
                {
                    rmsSq += 0.5 * (l[i] * l[i] + r[i] * r[i]);
                    peak = Math.Max(peak, Math.Max(MathF.Abs(l[i]), MathF.Abs(r[i])));
                }
                double rmsDb = Measure.Db(Math.Sqrt(rmsSq / TotalSamples));
                double peakDb = Measure.Db(peak);

                readme.AppendFormat(CultureInfo.InvariantCulture,
                    "| {0:D2} | `{1}` | {2} | {3} | {4:F1} | {5:F1} | {6} |\n",
                    idx + 1, wavName, a.Name, b.Name, rmsDb, peakDb, clicks);
            }

            readme.AppendLine();
            readme.AppendFormat(CultureInfo.InvariantCulture, "**Total files:** {0}. **Total clicks:** {1}.\n", pairs.Length, totalClicks);
            File.WriteAllText(Path.Combine(outDir, "README.md"), readme.ToString(), new UTF8Encoding(false));

            TestContext.Out.WriteLine($"Wrote {pairs.Length} WAVs and README.md to {outDir} (total clicks: {totalClicks})");
            Assert.That(totalClicks, Is.EqualTo(0), $"Expected 0 clicks across all 5 demo files (got {totalClicks})");
        }

        static (float[] L, float[] R) RenderHalf(ParamValues rndValues)
        {
            // Configure Neo Poly as a rich, steady resonant chord pad so filter cutoff modulation is crystal clear.
            var pv = new ParamValues(NeoBuzz.Poly.Params.Table);
            void SetPoly(int p, double val) => pv.Set(p, NeoBuzz.Poly.Params.Table[p].Raw(val));
            SetPoly(NeoBuzz.Poly.P.O1Shape, 2.0); // Saw
            SetPoly(NeoBuzz.Poly.P.O2Shape, 2.5); // Saw-Pulse
            SetPoly(NeoBuzz.Poly.P.O2Level, 0.70);
            SetPoly(NeoBuzz.Poly.P.O2Fine, 8.0);
            pv.Set(NeoBuzz.Poly.P.F1Type, 0);     // Ladder 24 LP
            SetPoly(NeoBuzz.Poly.P.F1Cutoff, 900.0);
            SetPoly(NeoBuzz.Poly.P.F1Reso, 0.55);
            SetPoly(NeoBuzz.Poly.P.F1Env, 0.0);   // let Neo Random own the cutoff sweep cleanly
            SetPoly(NeoBuzz.Poly.P.AmpA, 0.03);
            SetPoly(NeoBuzz.Poly.P.AmpD, 0.10);
            SetPoly(NeoBuzz.Poly.P.AmpS, 1.0);
            SetPoly(NeoBuzz.Poly.P.AmpR, 0.15);
            SetPoly(NeoBuzz.Poly.P.Volume, -11.0);

            var poly = new NeoBuzz.Poly.PolyEngine(pv);
            poly.SetSampleRate(Rate);

            var rnd = new RandomEngine(rndValues);
            var bank = new ModBank { GlideSeconds = Math.Max(0.008, rnd.Glide) };
            var slot = bank.Slots[0];
            slot.Used = true;
            slot.Port = new PolyCutoffPort(pv, NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.F1Cutoff].Raw(900.0));
            slot.Depth = 80;
            slot.Bipolar = true;

            int[] chord = { 45, 52, 55, 59, 60 }; // Am9
            for (int k = 0; k < chord.Length; k++) poly.NoteOn(1000 + k, chord[k], 0.80f);

            var l = new float[HalfSamples];
            var r = new float[HalfSamples];
            var bl = new float[64];
            var br = new float[64];
            double dt = 64.0 / Rate;
            int releaseAt = HalfSamples - (int)(0.20 * Rate);
            bool released = false;

            for (int pos = 0; pos < HalfSamples; pos += 64)
            {
                if (!released && pos >= releaseAt)
                {
                    for (int k = 0; k < chord.Length; k++) poly.NoteOff(1000 + k);
                    released = true;
                }
                double beats = (pos / (double)Rate) * (Bpm / 60.0);
                float u = rnd.Step(dt, beats, true, Bpm);
                bank.Update(u, dt);

                int n = Math.Min(64, HalfSamples - pos);
                poly.Process(bl, br, n);
                Array.Copy(bl, 0, l, pos, n);
                Array.Copy(br, 0, r, pos, n);
            }

            // Gentle 20 ms fade-in and 50 ms fade-out around the segment boundaries for seamless splicing.
            int fadeIn = (int)(0.020 * Rate);
            int fadeOut = (int)(0.050 * Rate);
            for (int i = 0; i < fadeIn; i++)
            {
                float g = 0.5f - 0.5f * MathF.Cos(MathF.PI * i / fadeIn);
                l[i] *= g; r[i] *= g;
            }
            for (int i = 0; i < fadeOut; i++)
            {
                float g = 0.5f + 0.5f * MathF.Cos(MathF.PI * i / fadeOut);
                int idx = HalfSamples - fadeOut + i;
                l[idx] *= g; r[idx] *= g;
            }
            return (l, r);
        }

        static int DetectClicks(float[] x, int rate)
        {
            const int Frame = 128;
            int nf = (x.Length - 2) / Frame;
            if (nf < 20) return 0;
            var fp = new double[nf];
            for (int f = 0; f < nf; f++)
            {
                double pk = 0;
                int s0 = f * Frame + 1;
                int s1 = Math.Min(x.Length - 1, s0 + Frame);
                for (int i = s0; i < s1; i++)
                {
                    double d2 = Math.Abs(x[i + 1] - 2.0 * x[i] + x[i - 1]);
                    if (d2 > pk) pk = d2;
                }
                fp[f] = pk;
            }
            int clicks = 0;
            int refractory = Math.Max(1, (int)(0.020 * rate / Frame));
            int last = -refractory;
            for (int f = 8; f < nf - 8; f++)
            {
                double sum = 0;
                for (int k = -8; k <= 8; k++)
                {
                    if (Math.Abs(k) <= 1) continue;
                    sum += fp[f + k];
                }
                double localMean = sum / 14.0;
                if (fp[f] > 0.15 && fp[f] > 4.5 * Math.Max(1e-6, localMean) && f - last >= refractory)
                {
                    clicks++;
                    last = f;
                }
            }
            return clicks;
        }

        static void WriteWav16Stereo(string path, float[] l, float[] r, int rate)
        {
            int n = l.Length;
            int dataBytes = n * 4;
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)2);
            bw.Write(rate);
            bw.Write(rate * 4);
            bw.Write((short)4);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);
            for (int i = 0; i < n; i++)
            {
                short sl = (short)Math.Clamp((int)Math.Round(l[i] * 32767f), short.MinValue, short.MaxValue);
                short sr = (short)Math.Clamp((int)Math.Round(r[i] * 32767f), short.MinValue, short.MaxValue);
                bw.Write(sl);
                bw.Write(sr);
            }
        }
    }
}

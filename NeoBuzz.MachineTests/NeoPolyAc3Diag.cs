using NeoBuzz.Dsp;
using NeoBuzz.Poly;
using static NeoBuzz.MachineTests.AcidLines;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class NeoPolyAc3Diag
    {

        static readonly Dictionary<string, Dictionary<int, int>> Presets = PresetTests.AllPresets().Where(p => p.Machine == "Neo Poly").ToDictionary(p => p.Preset, p => p.Values);

        [TestCase("Acid — Squelch", 6)]
        [TestCase("Acid — Squelch", 0)]
        [TestCase("Acid — Rubber", 6)]
        [TestCase("Acid — Sub", 6)]
        public void CutoffPerStep(string name, int filter)
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            var ev = Sequence(Pack, 2);
            int steps = 32;
            var maxCut = new double[steps];
            var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2), null, 16, (e, pos) =>
            {
                int st = Math.Min(steps - 1, (int)(pos / Step));
                for (int i = 0; i < PolyEngine.VoiceCount; i++) { var v = e.Voice(i); if (v.IsActive) maxCut[st] = Math.Max(maxCut[st], v.Cut1Hz); }
            });
            var m = Mid(l, r);
            var rows = AcidGate.Analyse(m, ev);
            Measure.Report($"{name} filter {filter}: max cutoff per step (Hz) and max2d");
            for (int i = 0; i < steps; i++) Measure.Report($"  step {i + 1,2}: cut {maxCut[i],7:F0}  max2d {rows[i].Max2d:F4} near {rows[i].Near2d:F4} bb {rows[i].BroadbandDb:F1}");
        }
    
        [Test]
        public void GateTable()
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            string ceil = Environment.GetEnvironmentVariable("NEO_CEIL");
            Action<ParamValues> tw = ceil == null ? null : v => v.Set(P.AcidRange, Params.Table[P.AcidRange].Raw(double.Parse(ceil)));
            foreach (var name in Presets.Keys.Where(k => k.StartsWith("Acid")).OrderBy(k => k))
                foreach (var (lineName, line) in new[] { ("pack", Pack), ("busy", Busy) })
                    foreach (int filter in lineName == "busy" ? new[] { -1 } : new[] { 6, 0 })
                    {
                        var ev = Sequence(line, 2);
                        var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2), tw);
                        var rows = AcidGate.Analyse(Mid(l, r), ev);
                        Measure.Report($"{name,-16} {lineName,-5} {filter,2}: worst2d {AcidGate.Worst2d(rows):F4} peak {Measure.Peak(l, 0, l.Length):F2} bad {rows.Count(x => x.Bad)}: {AcidGate.Flags(rows)}");
                    }
        }
    

        [Test]
        public void RatioTable()
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            string ceil = Environment.GetEnvironmentVariable("NEO_CEIL");
            Action<ParamValues> tw = ceil == null ? null : v => v.Set(P.AcidRange, Params.Table[P.AcidRange].Raw(double.Parse(ceil)));
            foreach (var name in Presets.Keys.Where(k => k.StartsWith("Acid")).OrderBy(k => k))
                foreach (var (lineName, line) in new[] { ("pack", Pack), ("busy", Busy) })
                    foreach (int filter in new[] { -1, 0 })
                    {
                        var ev = Sequence(line, 2);
                        var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2), tw);
                        var rows = AcidGate.Analyse(Mid(l, r), ev, AcidGate.IsDark(name));
                        var worst = rows.OrderByDescending(x => x.Ratio).Take(3).Select(x => $"s{x.Step}:{x.Ratio:F1}x({x.Near2d:F4})");
                        Measure.Report($"{name,-16} {lineName,-5} {filter,2}: worst {AcidGate.WorstRatio(rows):F1}x  {string.Join(" ", worst)}  bad {rows.Count(x => x.Bad)}: {AcidGate.Flags(rows)}");
                    }
        }


        static object F(object o, string n) { var t = o.GetType(); while (t != null) { var f = t.GetField(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance); if (f != null) return f.GetValue(o); var pr = t.GetProperty(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance); if (pr != null) return pr.GetValue(o); t = t.BaseType; } return null; }

        [TestCase("Acid — Plucky", 13)]
        [TestCase("Acid — Plucky", 31)]
        [TestCase("Acid — Screamer", 1)]
        [TestCase("Acid — Squelch", 9)]
        [TestCase("Acid — Squelch", 17)]
        [TestCase("Acid — Rubber", 4)]
        public void Trace(string name, int step)
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            string ceil = Environment.GetEnvironmentVariable("NEO_CEIL");
            Action<ParamValues> tw = ceil == null ? null : v => v.Set(P.AcidRange, Params.Table[P.AcidRange].Raw(double.Parse(ceil)));
            var ev = Sequence(Busy, 2);
            int on = (int)((step - 1) * Step);
            var log = new List<string>();
            var (l, r, _) = Render(Presets[name], -1, ev, LineSeconds(2), tw, 16, (e, pos) =>
            {
                if (pos < on - 800 || pos > on + 1500) return;
                for (int i = 0; i < PolyEngine.VoiceCount; i++)
                {
                    var v = e.Voice(i);
                    if (!v.IsActive) continue;
                    var fe = F(v, "fltEnv"); var ae = F(v, "ampEnv");
                    log.Add($"{pos - on,5} v{i} cut {v.Cut1Hz,7:F0} fenv {F(fe, "Level") ?? ""} amp {F(ae, "Level") ?? ""} accD {F(v, "accDepthCur"):F3} accG {F(v, "accGainRun"):F3} pitch {F(v, "pitch"):F2} acc {F(v, "accented")} envOct {F(v, "envNowOct"):F2}");
                }
            });
            var m = Mid(l, r);
            double pk = 0; int at = 0;
            { var (ons, offs) = AcidGate.Boundaries(ev); var d2 = new float[m.Length]; for (int i = 1; i < m.Length - 1; i++) d2[i] = Math.Abs(m[i + 1] - 2 * m[i] + m[i - 1]);
              foreach (int b in ons.Concat(offs).OrderBy(v => v).Where(v => v > on - 6000 && v < on + 6000))
              { double pk2 = 0; int at2 = 0; for (int i = b - 240; i < b + 240; i++) if (d2[i] > pk2) { pk2 = d2[i]; at2 = i; }
                double ip = 0; for (int i = b + 480; i < b + 3000; i++) ip = Math.Max(ip, d2[i]); double il = 0; for (int i = b - 3000; i < b - 380; i++) il = Math.Max(il, d2[i]);
                log.Insert(0, $"boundary {(ons.Contains(b) ? "on " : "off")} at {b - on,6}: near {pk2:F5} (at {at2 - b}) right {ip:F5} left {il:F5}"); } }
            var gr = AcidGate.Analyse(m, ev, AcidGate.IsDark(name))[step - 1]; log.Insert(0, $"gate row: near {gr.Near2d:F4} interior {gr.Interior:F4} ratio {gr.Ratio:F1}");
            for (int i = Math.Max(1, on - 400); i < on + 800; i++) { double d = Math.Abs(m[i + 1] - 2 * m[i] + m[i - 1]); if (d > pk) { pk = d; at = i; } }
            Measure.Report($"TRACE {name} step {step}: peak 2d {pk:F4} at {at - on} samples from the step start\n" + string.Join("\n", log));
        }


        [TestCase("Acid — Plucky")]
        public void AccentVsPlain(string name)
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            var lines = new List<string>();
            var cuts = new Dictionary<float, float[]>(); var extra = new Dictionary<(float, int), string>();
            foreach (float vel in new[] { 90 / 127f, 1f })
            {
                var c = new float[60];
                Render(Presets[name], 6, new List<Ev> { new(0, 0, 33, vel), new(14400, 0, -1, 0) }, 0.4, null, 16, (e, pos) =>
                {
                    int k = pos / 48;
                    if (k < 60) for (int i = 0; i < PolyEngine.VoiceCount; i++) { var v = e.Voice(i); if (v.IsActive) { c[k] = Math.Max(c[k], v.Cut1Hz); if (vel == 1f || true) extra[(vel, k)] = $"fenv {F(F(v, "fltEnv"), "Level"):F2} envOct {F(v, "envNowOct"):F2} accD {F(v, "accDepthCur"):F2} cutOct {F(v, "cutOct"):F2}"; } }
                });
                cuts[vel] = c;
            }
            var pl = cuts[90 / 127f]; var ac = cuts[1f];
            Measure.Report("ACC " + name + " cutoff per ms plain | accent:\n" + string.Join("\n", Enumerable.Range(0, 60).Where(i => i % 2 == 0 || i < 12).Select(i => $"{i} ms {pl[i],7:F0} {ac[i],7:F0}  | plain {extra[(90 / 127f, i)]} | accent {extra[(1f, i)]}")));
        }


        [Test]
        public void HfTrace()
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            string name = Environment.GetEnvironmentVariable("NEO_PRESET") ?? "Acid — Screamer";
            int step = int.Parse(Environment.GetEnvironmentVariable("NEO_STEP") ?? "3");
            var ev = Sequence(AllAccented(Pack), 2);
            var cut = new List<float>();
            var (l, r, _) = Render(Presets[name], 6, ev, LineSeconds(2), null, 16, (e, pos) => { for (int i = 0; i < PolyEngine.VoiceCount; i++) { var v = e.Voice(i); if (v.IsActive) { cut.Add(v.Cut1Hz); break; } } });
            var m = Mid(l, r);
            var hp = AcidGate.HighPass6k(m);
            int on = (int)((step - 1) * Step);
            var lines = new List<string>();
            for (int ms = 0; ms < 90; ms += 3)
            {
                double e1 = 0; int a = on + ms * 48, b = a + 144;
                for (int i = a; i < b; i++) e1 += hp[i] * hp[i];
                int blk = a / 16;
                lines.Add($"{ms,3} ms  hf {10 * Math.Log10(e1 / 144 + 1e-14),6:F1} dB  cut {(blk < cut.Count ? cut[blk] : 0),7:F0}  level {Measure.Peak(m, a, b):F3}");
            }
            Measure.Report("HF " + name + " step " + step + "\n" + string.Join("\n", lines));
        }

        [TestCase("Acid — Squelch", 6)]
        [TestCase("Acid — Screamer", 0)]
        [TestCase("Acid — Screamer", 6)]
        public void Table(string name, int filter)
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            var ev = Sequence(Busy, 2);
            var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2));
            var rows = AcidGate.Analyse(Mid(l, r), ev, AcidGate.IsDark(name));
            Measure.Report($"TABLE {name} {filter}\n" + AcidGate.Table(rows));
        }
    
        [Test]
        public void DumpWavs()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_DIAG_WAV");
            if (dir == null) Assert.Ignore("diag");
            Directory.CreateDirectory(dir);
            foreach (var name in Presets.Keys.Where(k => k.StartsWith("Acid")).OrderBy(k => k))
                foreach (var (lineName, line) in new[] { ("pack", Pack), ("busy", Busy) })
                    foreach (int filter in lineName == "busy" ? new[] { -1 } : new[] { 6, 0 })
                    {
                        var ev = Sequence(line, 2);
                        var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2));
                        NeoPolyClickTests.WriteWav24(Path.Combine(dir, name.Replace(" — ", "-").Replace(' ', '-') + "_" + lineName + "_" + filter + ".wav"), Mid(l, r));
                    }
        }
    }
}

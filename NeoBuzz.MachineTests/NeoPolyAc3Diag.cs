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
        public void CutCurvature()
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            string only = Environment.GetEnvironmentVariable("NEO_PRESET");
            foreach (var name in Presets.Keys.Where(k => k.StartsWith("Acid") && (only == null || k == only)).OrderBy(k => k))
                foreach (int filter in new[] { 6, 0 })
                {
                    var ev = Sequence(Pack, 2);
                    var cut = new List<double>(); var age = new List<double>();
                    var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2), null, 16, (e, pos) =>
                    {
                        double best = double.NaN, ba = 1e9;
                        for (int i = 0; i < PolyEngine.VoiceCount; i++) { var v = e.Voice(i); if (!v.IsActive) continue; double a = Convert.ToDouble(F(v, "noteAge")); if (a < ba) { ba = a; best = Convert.ToDouble(F(v, "cutSm2")); } }
                        cut.Add(best); age.Add(ba);
                    });
                    var m = Mid(l, r);
                    var rows = new List<string>();
                    const int K = 3; double dt = K * 16.0 / 48000;
                    for (int step = 1; step <= 32; step++)
                    {
                        int on = (int)((step - 1) * Step); int b0 = on / 16;
                        double worst = 0; int wat = 0;
                        for (int b = b0 + 3 * K; b < b0 + (int)(Step * 0.5 / 16) && b + K < cut.Count; b++)
                        {
                            if (double.IsNaN(cut[b - K]) || double.IsNaN(cut[b + K])) continue;
                            double dd = Math.Abs(cut[b + K] - 2 * cut[b] + cut[b - K]) / (dt * dt);
                            if (dd > worst) { worst = dd; wat = b * 16 - on; }
                        }
                        double ap = 0; int aat = 0;
                        for (int i = on + 384; i < on + (int)(Step * 0.5); i++) { double d = Math.Abs(m[i + 1] - 2 * m[i] + m[i - 1]); if (d > ap) { ap = d; aat = i - on; } }
                        rows.Add($"  s{step,2}: cut2 {worst,9:F0} oct/s2 at {wat / 48.0,5:F1} ms | audio2d {ap:F4} at {aat / 48.0,5:F1} ms");
                    }
                    Measure.Report($"CURV {name} f{filter}\n" + string.Join("\n", rows));
                }
        }


        [TestCase("Acid — Squelch", 1, 6)]
        [TestCase("Acid — Squelch", 15, 6)]
        [TestCase("Acid — Deep", 25, 6)]
        [TestCase("Acid — Plucky", 15, 6)]
        [TestCase("Acid — Sub", 10, 6)]
        public void PackTrace(string name, int step, int filter)
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            var ev = Sequence(Pack, 2); int on = (int)((step - 1) * Step); var log = new List<string>(); double prev = 0;
            var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2), null, 16, (e, pos) =>
            {
                if (pos < on - 400 || pos > on + 2400) return;
                for (int i = 0; i < PolyEngine.VoiceCount; i++)
                {
                    var v = e.Voice(i); if (!v.IsActive) continue;
                    log.Add($"{(pos - on) / 48.0,6:F2}ms v{i} age {F(v, "noteAge"):F4} cutOct {F(v, "cutOct"):F3} sm2 {F(v, "cutSm2"):F3} so {F(v, "diagSo"):F3} fenv {F(F(v, "fltEnv"), "Level"):F3} accD {F(v, "accDepthCur"):F3} cut {v.Cut1Hz,6:F0}");
                }
            });
            Measure.Report($"PTRACE {name} s{step} f{filter}\n" + string.Join("\n", log));
        }


        /// <summary>Prototype: the spike inside each note. Windows of 0.5 ms (24 samples) over the note's interior; ratio of the biggest window to the biggest window more than 4 ms away.</summary>
        internal static (double Ratio, double AtMs, double Peak)[] InteriorSpikes(float[] m, List<Ev> ev, int lead = 8, int tail = 8)
        {
            var (ons, offs) = AcidGate.Boundaries(ev);
            var res = new (double, double, double)[ons.Count];
            for (int s = 0; s < ons.Count; s++)
            {
                int a = ons[s] + lead * 48, bEnd = Math.Min(offs.Where(o => o > ons[s]).DefaultIfEmpty(int.MaxValue).First(), s + 1 < ons.Count ? ons[s + 1] : int.MaxValue) - tail * 48;
                bEnd = Math.Min(bEnd, m.Length - 2);
                if (bEnd - a < 240) { res[s] = (0, 0, 0); continue; }
                int W = 24, nw = (bEnd - a) / W; var E = new double[nw];
                for (int k = 0; k < nw; k++) for (int i = a + k * W; i < a + (k + 1) * W; i++) E[k] = Math.Max(E[k], Math.Abs(m[i + 1] - 2 * m[i] + m[i - 1]));
                int kb = Array.IndexOf(E, E.Max()); double outside = 0;
                for (int k = 0; k < nw; k++) if (Math.Abs(k - kb) > 8) outside = Math.Max(outside, E[k]);
                res[s] = (E[kb] / Math.Max(outside, 1e-4), (a + kb * W - ons[s]) / 48.0, E[kb]);
            }
            return res;
        }

        [Test]
        public void SpikeTable()
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            var sb = new List<string>();
            foreach (var name in Presets.Keys.Where(k => k.StartsWith("Acid")).OrderBy(k => k))
                foreach (var (ln, line) in new[] { ("pack", Pack), ("busy", Busy) })
                    foreach (int filter in new[] { 6, 0 })
                    {
                        var ev = Sequence(line, 2);
                        var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2));
                        var sp = InteriorSpikes(Mid(l, r), ev);
                        var top = sp.Select((x, i) => (x, i)).OrderByDescending(t => t.x.Ratio).Take(6).Select(t => $"s{t.i + 1}:{t.x.Ratio:F1}@{t.x.AtMs:F0}({t.x.Peak:F4})");
                        sb.Add($"SPK {name,-16} {ln,-5} f{filter}: {string.Join(" ", top)}");
                    }
            Measure.Report(string.Join("\n", sb));
        }


        [Test]
        public void Probe()
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            string name = Environment.GetEnvironmentVariable("NEO_PRESET") ?? "Acid — Squelch";
            var ev = Sequence(Pack, 2); var sb = new List<string>();
            foreach (var (label, tw) in new (string, Action<ParamValues>)[] {
                ("as is", null),
                ("reso 0", v => v.Set(P.F1Reso, 0)),
                ("range 3k", v => v.Set(P.AcidRange, Params.Table[P.AcidRange].Raw(3000))),
                ("no drive", v => v.Set(P.F1Drive, 0)) })
            {
                var (l, r, _) = Render(Presets[name], 6, ev, LineSeconds(2), tw);
                var m = Mid(l, r);
                foreach (int step in new[] { 1, 15 })
                {
                    int on = (int)((step - 1) * Step); double pk = 0; int at = 0;
                    for (int i = on + 384; i < on + 2800; i++) { double d = Math.Abs(m[i + 1] - 2 * m[i] + m[i - 1]); if (d > pk) { pk = d; at = i - on; } }
                    sb.Add($"PRB {label,-9} s{step}: peak {pk:F4} at {at / 48.0:F1} ms; rms 20-40ms {Measure.Rms(m, on + 960, on + 1920):F4}");
                    if (label == "as is" && step == 1) sb.Add("   d2 around: " + string.Join(" ", Enumerable.Range(at - 6, 14).Select(i => $"{(m[on + i + 1] - 2 * m[on + i] + m[on + i - 1]):F4}")) + "\n   x: " + string.Join(" ", Enumerable.Range(at - 6, 14).Select(i => $"{m[on + i]:F3}")));
                }
            }
            Measure.Report(string.Join("\n", sb));
        }


        [Test]
        public void CurvTable()
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            var sb = new List<string>();
            var combos = (Environment.GetEnvironmentVariable("NEO_COMBOS") ?? "0:6,0.6:2").Split(',').Select(c => c.Split(':')).Select(a => (float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture)));
            foreach (var (sm, fall) in combos)
            {
                foreach (var name in Presets.Keys.Where(k => k.StartsWith("Acid")).OrderBy(k => k))
                {
                    var ev = Sequence(Pack, 2);
                    var (m, rows) = AcidCutoff.RenderTrace(Presets[name], 6, ev, LineSeconds(2));
                    var acc = new[] { 1, 4, 7, 9, 12, 15, 17, 20, 23, 25, 28, 31 };
                    double hz = rows.Where(x => acc.Contains(x.Step)).Average(x => x.MaxHz), hp = rows.Where(x => !acc.Contains(x.Step)).Average(x => x.MaxHz);
                    sb.Add($"CT sm {sm} fall {fall} {name,-16}: worst {AcidCutoff.Worst(rows) / 1000:F0}k flagged {rows.Count(x => x.Curvature > AcidCutoff.MaxCurvature)} | mean max cutoff accent {hz:F0} Hz plain {hp:F0} Hz");
                }
            }
            Measure.Report(string.Join("\n", sb));
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

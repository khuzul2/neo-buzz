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
    
        [TestCase("Acid — Squelch", 6)]
        [TestCase("Acid — Plucky", 6)]
        [TestCase("Acid — Screamer", 6)]
        public void Table(string name, int filter)
        {
            if (Environment.GetEnvironmentVariable("NEO_DIAG") == null) Assert.Ignore("diag");
            var ev = Sequence(Pack, 2);
            var (l, r, _) = Render(Presets[name], filter, ev, LineSeconds(2));
            var rows = AcidGate.Analyse(Mid(l, r), ev);
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

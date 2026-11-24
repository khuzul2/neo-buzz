using NeoBuzz.Dsp;
using NeoBuzz.Poly;
using static NeoBuzz.MachineTests.AcidLines;

namespace NeoBuzz.MachineTests
{
    /// <summary>AC3: the acid voice against the new click gate (<see cref="AcidGate"/>), the filter range and its bounds, the quality choices and the extremes.</summary>
    [TestFixture]
    public class NeoPolyAc3Tests
    {
        const int Rate = Measure.Rate;

        static readonly Dictionary<string, Dictionary<int, int>> Presets = PresetTests.AllPresets()
            .Where(p => p.Machine == "Neo Poly")
            .ToDictionary(p => p.Preset, p => p.Values);

        static readonly string[] Acid = Presets.Keys.Where(k => k.StartsWith("Acid")).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static List<Ev> Line(string line) => line switch
        {
            "pack" => Sequence(Pack, 2),
            "busy" => Sequence(Busy, 2),
            "allslid" => Sequence(AllSlid(Pack), 2),
            "allaccent" => Sequence(AllAccented(Pack), 2),
            "allaccentslid" => Sequence(AllAccented(AllSlid(Busy)), 2),
            "firstnote" => AfterSilence(),
            _ => throw new ArgumentException(line),
        };

        static double Seconds(string line) => line == "firstnote" ? 6.5 : LineSeconds(2);

        public static IEnumerable<TestCaseData> Cases()
        {
            foreach (var name in Acid)
                foreach (int filter in new[] { 6, 0 })
                    foreach (var line in new[] { "pack", "busy", "allslid", "allaccent", "firstnote" })
                        foreach (int block in line is "pack" or "busy" ? new[] { 16, 64, 256 } : new[] { 16 })
                            yield return new TestCaseData(name, filter, line, block).SetName($"{name} {(filter == 6 ? "Diode18" : "Ladder24")} {line} block {block}");
        }

        [TestCaseSource(nameof(Cases))]
        public void TheGateFindsNothing(string name, int filter, string line, int block)
        {
            var ev = Line(line);
            var (l, r, _) = Render(Presets[name], filter, ev, Seconds(line), null, block);
            var rows = AcidGate.Analyse(Mid(l, r), ev, AcidGate.IsDark(name));
            if (rows.Any(x => x.Bad)) Measure.Report($"{name} {filter} {line} {block}\n" + AcidGate.Table(rows));
            Assert.That(rows.Where(x => x.Bad).Select(x => x.Step), Is.Empty, AcidGate.Flags(rows));
            Assert.That(AcidGate.Worst2d(rows), Is.LessThanOrEqualTo(AcidGate.MaxEverywhere));
        }

        /// <summary>Filter 1's cutoff, logged every sample while the voice renders, for the notes of <paramref name="line"/>.</summary>
        static float MaxCutoff(Dictionary<int, int> preset, int filter, List<Ev> ev, double seconds, Action<ParamValues> tweak = null)
        {
            int total = (int)(seconds * Rate);
            var voices = new List<PolyVoice>();
            float max = 0;
            Render(preset, filter, ev, seconds, tweak, 16, (e, pos) =>
            {
                if (voices.Count == 0)
                    for (int i = 0; i < PolyEngine.VoiceCount; i++) { var v = e.Voice(i); v.CutLog = new float[total]; voices.Add(v); }
            });
            foreach (var v in voices) for (int i = 0; i < v.CutLogCount; i++) max = Math.Max(max, v.CutLog[i]);
            return max;
        }

        [TestCase(1800.0)] [TestCase(1000.0)] [TestCase(6000.0)] [TestCase(18000.0)]
        public void TheCutoffNeverPassesTheFilterRangeNotEvenOnSixteenAccentedSlides(double range)
        {
            foreach (var name in Acid)
                foreach (int filter in new[] { 6, 0 })
                {
                    float max = MaxCutoff(Presets[name], filter, Line("allaccentslid"), LineSeconds(2), v => Set(v, P.AcidRange, range));
                    Assert.That(max, Is.GreaterThan(100f), "the probe logged the cutoff");
                    Assert.That(max, Is.LessThanOrEqualTo(Math.Min(range * Math.Pow(2, PolyVoice.AcidAccentLiftOct) * 1.002, Rate * 0.48)), $"{name} {filter} at range {range}");
                }
        }

        [Test]
        public void TheDefaultFilterRangeIsTheOneThePresetsAreCleanAt()
        {
            Assert.That(Params.Table[P.AcidRange].Default, Is.EqualTo(163));
            Assert.That(Params.Table[P.AcidRange].Describe(Params.Table[P.AcidRange].Default), Does.Contain("1.6"));
            Assert.That(Params.Table[P.AcidSweepMax].Default, Is.EqualTo(500));
            Assert.That(Params.Table[P.AcidQuality].Default, Is.EqualTo(1), "High (2x)");
            Assert.That(Params.Table[P.AcidSoft].Describe(Params.Table[P.AcidSoft].Default), Does.Contain("3.5").Or.Contain("3.4").Or.Contain("3.6"));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void EveryFilterQualityRendersCleanly(int quality)
        {
            foreach (var name in Acid)
            {
                var ev = Line("busy");
                var (l, r, _) = Render(Presets[name], -1, ev, LineSeconds(2), v => v.Set(P.AcidQuality, quality));
                Assert.That(l.All(float.IsFinite) && r.All(float.IsFinite), Is.True, name);
                var rows = AcidGate.Analyse(Mid(l, r), ev, AcidGate.IsDark(name));
                // Eco (1x) has no oversampling: the gate's second difference may reach a little more than at 2x and 4x, but a resonant filter below the range does not click.
                Assert.That(AcidGate.Worst2d(rows), Is.LessThan(quality == 0 ? 0.02 : AcidGate.MaxEverywhere + 1e-9), $"{name} quality {quality}");
            }
        }

        [Test]
        public void TheHalfBandChainsDelayTheFilterBy15And23Samples()
        {
            Assert.That(new FirHalfband(new FirHalfbandDesign(8, 80)).RoundTripLatency, Is.EqualTo(15));
            Assert.That(PolyVoice.AcidLatencySamples(1), Is.EqualTo(0));
            Assert.That(PolyVoice.AcidLatencySamples(2), Is.EqualTo(15));
            Assert.That(PolyVoice.AcidLatencySamples(4), Is.EqualTo(23));
        }

        [Test]
        public void AtTheExtremesTheClicksAndTheBuzzCanComeBackByTheUsersChoice()
        {
            double worstDefault = 0, worstExtreme = 0;
            foreach (var name in new[] { "Acid — Squelch", "Acid — Rubber" })
            {
                var ev = Line("busy");
                var (l, r, _) = Render(Presets[name], -1, ev, LineSeconds(2));
                worstDefault = Math.Max(worstDefault, AcidGate.Worst2d(AcidGate.Analyse(Mid(l, r), ev)));
                var (l2, r2, _) = Render(Presets[name], -1, ev, LineSeconds(2), v =>
                {
                    Set(v, P.AcidRange, 18000); Set(v, P.AcidSweepMax, 100); Set(v, P.AcidSoft, 0.5); v.Set(P.AcidQuality, 0);
                });
                worstExtreme = Math.Max(worstExtreme, AcidGate.Worst2d(AcidGate.Analyse(Mid(l2, r2), ev)));
            }
            Measure.Report($"worst 2nd difference: defaults {worstDefault:F4}, range 18 kHz + sweep 100 % + soft start 0.5 ms + Eco {worstExtreme:F4}");
            Assert.That(worstExtreme, Is.GreaterThan(AcidGate.MaxEverywhere), "the limits do something: the extremes are not clean");
        }

        [Test]
        public void SoftStartAndTheReleaseHaveTheirFloors()
        {
            // The release of an acid note is never shorter than 5 ms or the Soft start: a preset with a 1 ms release, rendered, has no corner at the note end.
            var preset = new Dictionary<int, int>(Presets["Acid — Squelch"]);
            Assert.That(PolyVoice.AcidLatencySamples(2), Is.GreaterThan(0));
            var ev = Line("pack");
            var (l, r, _) = Render(preset, -1, ev, LineSeconds(2), v => Set(v, P.AmpR, 0.0005));
            var rows = AcidGate.Analyse(Mid(l, r), ev);
            Assert.That(rows.Select(x => x.Near2d).Max(), Is.LessThanOrEqualTo(AcidGate.MaxAtBoundary), AcidGate.Table(rows));
        }
    }
}

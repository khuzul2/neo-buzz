using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Learn presets are written in real units (seconds for times). Gen-machine-presets reads seconds, so a time written in
    /// milliseconds (Attack = 1, Decay = 400) saturates the control. This catches values that sit on a range's end or are
    /// implausible for what the control is for.
    /// </summary>
    [TestFixture]
    public class LearnPresetTimeTests
    {
        static IEnumerable<(string Machine, string Preset, ParamDef Def, int Raw)> LearnTimes()
        {
            foreach (var (_, machine, preset, values) in PresetTests.AllPresets())
            {
                if (!machine.StartsWith("Neo Learn ")) continue;
                var table = PresetTests.Machines[machine].Table;
                foreach (var kv in values)
                    if (kv.Key >= 0 && table[kv.Key].Kind == MapKind.Time) yield return (machine, preset, table[kv.Key], kv.Value);
            }
        }

        [Test]
        public void LearnPresetTimesAreInsideTheirRangesAndPlausible()
        {
            int n = 0;
            foreach (var (machine, preset, d, raw) in LearnTimes())
            {
                n++;
                double s = d.Value(raw);
                string who = $"{machine} / {preset} / {d.Name} = {s:G4} s (raw {raw})";
                Assert.That(raw, Is.LessThan(d.Max), who + ": at the top of its range (a time written in the wrong unit?)");
                if (d.Name != "Attack") Assert.That(raw, Is.GreaterThan(d.Min), who + ": at the bottom of its range (a time written in the wrong unit?)");
                if (d.Name == "Attack") Assert.That(s, Is.LessThanOrEqualTo(3.0), who + ": an attack of more than 3 s is not a preset's attack");
                if (d.Name == "Release") Assert.That(s, Is.LessThanOrEqualTo(6.0), who + ": a release of more than 6 s");
                if (d.Name == "Decay") Assert.That(s, Is.LessThanOrEqualTo(6.0), who + ": a decay of more than 6 s");
            }
            Assert.That(n, Is.GreaterThan(60), "the presets were found");
        }

        [Test]
        public void TheGuardCatchesAMillisecondValueWrittenAsSeconds()
        {
            // Attack = 1 (meant 1 ms) is a one second attack; Decay = 400 is clamped to the range's end.
            var attack = NeoBuzz.LearnSub.Params.Table.First(d => d.Name == "Attack");
            var decay = NeoBuzz.LearnSub.Params.Table.First(d => d.Name == "Decay");
            Assert.That(attack.Value(attack.Max), Is.EqualTo(5.0).Within(1e-6));
            Assert.That(decay.Value(decay.Max), Is.EqualTo(8.0).Within(1e-6), "400 s asks for more than the range has: raw saturates at Max");
        }
    }
}

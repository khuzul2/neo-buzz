using System.Text.Json.Nodes;
using ReBuzz.Agent.Model;

namespace ReBuzz.AgentTests
{
    /// <summary>Neo Learn L0 (docs/specs/neo-learn.md §5.3): the manifest's How and Sound, and what machine_describe adds from them.</summary>
    [TestFixture]
    public class ExplainFieldsTests
    {
        static string RepoRoot()
        {
            string? env = Environment.GetEnvironmentVariable("NEO_BUZZ_ROOT");
            if (!string.IsNullOrEmpty(env)) return env;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "ReBuzz.sln"))) return dir.FullName;
            throw new InvalidOperationException("repository root not found");
        }

        static MachineManifest Learn() =>
            MachineManifest.Load(Path.Combine(RepoRoot(), "Machines", "NeoLearnSubtractive", "Neo Learn Subtractive" + MachineManifest.Suffix))!;

        [Test]
        public void TheLearnManifestCarriesIdsHowAndSoundForAllTwelveControls()
        {
            var m = Learn();
            Assert.That(m.Parameters.Count, Is.EqualTo(12));
            Assert.That(m.Parameters.Select(p => p.Id), Is.EqualTo(new[]
            {
                "filter_cutoff", "filter_resonance", "wave_morph", "sub_mix", "filter_env_amt", "analog_drive",
                "env_attack", "env_decay", "env_sustain", "env_release", "unison_detune", "output_gain",
            }));
            Assert.That(m.Parameters, Has.All.Matches<ManifestParameter>(p => p.How.Length > 0 && p.Sound.Length > 0));
        }

        // Neo Learn L1a: the three other synths, 12 controls each with ids, How and Sound.
        [TestCase("NeoLearnFM", "Neo Learn FM", "mod_ratio,fm_index,mod_decay,feedback,op3_harmonic,vel_sensitivity")]
        [TestCase("NeoLearnWavetable", "Neo Learn Wavetable", "wt_position,phase_warp,table_select,scan_speed,scan_depth,sub_anchor")]
        [TestCase("NeoLearnAdditive", "Neo Learn Additive", "spectral_tilt,odd_even_balance,partial_count,inharmonicity,harmonic_comb,partial_shimmer")]
        public void TheOtherLearnManifestsCarryTwelveControlsWithIdsHowAndSound(string folder, string gear, string core)
        {
            var m = MachineManifest.Load(Path.Combine(RepoRoot(), "Machines", folder, gear + MachineManifest.Suffix))!;
            Assert.That(m.Parameters.Count, Is.EqualTo(12));
            Assert.That(m.Parameters.Select(p => p.Id).Take(6), Is.EqualTo(core.Split(',')));
            Assert.That(m.Parameters.Select(p => p.Id).Skip(6), Is.EqualTo(new[]
                { "env_attack", "env_decay", "env_sustain", "env_release", "unison_detune", "output_gain" }));
            Assert.That(m.Parameters, Has.All.Matches<ManifestParameter>(p => p.How.Length > 0 && p.Sound.Length > 0));
            var o = new JsonObject();
            ExplainFields.Add(o, m.Parameters[0]);
            Assert.That((string)o["id"]!, Is.EqualTo(core.Split(',')[0]));
            Assert.That((string)o["how"]!, Is.Not.Empty);
        }

        [Test]
        public void DescribeAddsIdUnitRangeDefaultCurveHowAndSound()
        {
            var m = Learn();
            var o = new JsonObject();
            ExplainFields.Add(o, m.Parameters.First(p => p.Id == "filter_cutoff"));
            Assert.That((string)o["id"]!, Is.EqualTo("filter_cutoff"));
            Assert.That((string)o["unit"]!, Is.EqualTo("Hz"));
            Assert.That((double)o["min"]!, Is.EqualTo(20).Within(0.01));
            Assert.That((double)o["max"]!, Is.EqualTo(20000).Within(0.5));
            Assert.That((double)o["default_number"]!, Is.InRange(200, 2000));
            Assert.That((string)o["curve"]!, Is.EqualTo("logarithmic"));
            Assert.That((string)o["how"]!, Is.Not.Empty);
            Assert.That((string)o["sound"]!, Is.Not.Empty);

            var gain = new JsonObject();
            ExplainFields.Add(gain, m.Parameters.First(p => p.Id == "output_gain"));
            Assert.That((string)gain["unit"]!, Is.EqualTo("dB"));
            Assert.That((double)gain["min"]!, Is.EqualTo(-36).Within(0.01));
            Assert.That((double)gain["max"]!, Is.EqualTo(12).Within(0.01));
            Assert.That((double)gain["default_number"]!, Is.EqualTo(-5.5).Within(0.1));
        }

        [Test]
        public void ParametersWithoutExplainTextAreDescribedAsBefore()
        {
            var poly = MachineManifest.Load(Path.Combine(RepoRoot(), "Machines", "NeoPoly", "Neo Poly" + MachineManifest.Suffix))!;
            var o = new JsonObject();
            foreach (var p in poly.Parameters) ExplainFields.Add(o, p);
            Assert.That(o, Is.Empty, "no Explain text: no new fields");
        }
    }
}

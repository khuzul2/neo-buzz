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
        [TestCase("NeoLearnGranular", "Neo Learn Granular", "sample_position,grain_size,grain_density,position_spray,pitch_shimmer,grain_shape")]
        [TestCase("NeoLearnPhysical", "Neo Learn Physical", "material,exciter_type,exciter_force,resonator_shape,damping,body_size")]
        [TestCase("NeoLearnString", "Neo Learn String", "pluck_position,string_damping,pick_hardness,string_decay,dispersion,sympathetic_res")]
        [TestCase("NeoLearnFormant", "Neo Learn Formant", "vowel_morph,vocal_tract_scale,formant_resonance,glottal_edge,breath_air,vowel_env_mod")]
        [TestCase("NeoLearnWavefolder", "Neo Learn Wavefolder", "fold_drive,fold_symmetry,fold_stages,linear_fm,lpg_mode,lpg_ping")]
        [TestCase("NeoLearnPhaseDistortion", "Neo Learn Phase Distortion", "phase_bend,resonance,pd_wave_shape,bend_decay,ring_mod,chorus")]
        [TestCase("NeoLearnVector", "Neo Learn Vector", "vector_x,vector_y,corner_palette,orbit_speed,orbit_radius,orbit_shape")]
        [TestCase("NeoLearnSampler", "Neo Learn Sampler", "sample_offset,timbre_tilt,source,loop,vintage_dac,room_space")]
        [TestCase("NeoLearnSpectral", "Neo Learn Spectral", "spectral_shift,spectral_blur,spectral_freeze,spectral_source,bin_gate,tonal_noise")]
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
            // A machine with no Explain text (every Neo machine and Dye Essence have it now): a hand-made manifest.
            var json = JsonNode.Parse("{\"format\":\"rebuzz-machine-manifest/1\",\"gear\":\"Neo X\",\"kind\":\"effect\",\"parameters\":[{\"index\":0,\"id\":\"a\",\"name\":\"A\",\"min\":0,\"max\":1,\"default\":0,\"kind\":\"linear\"}]}")!.ToJsonString();
            var path = Path.Combine(Path.GetTempPath(), "explainfields-" + Guid.NewGuid().ToString("N") + MachineManifest.Suffix);
            var o = new JsonObject();
            try
            {
                File.WriteAllText(path, json);
                foreach (var p in MachineManifest.Load(path)!.Parameters) ExplainFields.Add(o, p);
            }
            finally { File.Delete(path); }
            Assert.That(o, Is.Empty, "no Explain text: no new fields");
        }
    }
}

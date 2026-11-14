using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using ReBuzzTests.Automation;
using DyeP = NeoBuzz.DyeEssence.P;
using FP = NeoBuzz.LearnFm.P;
using WP = NeoBuzz.LearnWt.P;
using AP = NeoBuzz.LearnAdd.P;
using GP = NeoBuzz.LearnGrain.P;
using PP = NeoBuzz.LearnPhys.P;
using SP = NeoBuzz.LearnString.P;
using VP = NeoBuzz.LearnFormant.P;
using XP = NeoBuzz.LearnFold.P;
using YP = NeoBuzz.LearnPd.P;
using ZP = NeoBuzz.LearnVector.P;
using QP = NeoBuzz.LearnSampler.P;
using LS = NeoBuzz.LearnSpectral.P;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Learn (docs/specs/neo-learn.md §5.5), L1a: Neo Learn FM, Wavetable and Additive. Each plays Dye Essence's engine from its 12 controls
    /// (LearnHost, a MacroMap). The shared bars for all three (controls, macro-map ranges and continuity, allocation, finite output, pinned
    /// renders, levels, CPU), then what each machine and the shared-engine additions it uses (exact ratio, PM self-feedback; comb, shimmer,
    /// inharmonicity) must do. LearnTests covers the host itself and the pilot.
    /// </summary>
    [TestFixture]
    public partial class LearnL1Tests
    {
        const int Rate = Measure.Rate;
        const int Block = DyeEngine.MaxBlock;

        /// <summary>One of the three machines under test.</summary>
        public sealed class M
        {
            public string Gear;
            internal ParamDef[] Table;
            internal Func<MacroMap> Map;
            internal string[] Ids;
            /// <summary>Dye controls the map may step (an enumeration or a count), with why.</summary>
            internal Dictionary<int, string> Detents;
            /// <summary>Four distinct settings: name → values.</summary>
            internal Dictionary<string, Action<ParamValues>> Patches;
            internal Action<ParamValues> Heaviest;
            public override string ToString() => Gear;
        }

        static readonly string[] GlobalIds = { "env_attack", "env_decay", "env_sustain", "env_release", "unison_detune", "output_gain" };

        static Dictionary<int, string> Unison(params (int, string)[] more)
        {
            var d = new Dictionary<int, string>
            {
                [DyeP.AUnison] = "Unison / Spread: one voice at 0%, more above",
                [DyeP.BUnison] = "Unison / Spread: one voice at 0%, more above",
                [DyeP.CUnison] = "Unison / Spread: one voice at 0%, more above",
            };
            foreach (var (k, v) in more) d[k] = v;
            return d;
        }

        internal static readonly M Fm = new()
        {
            Gear = "Neo Learn FM",
            Table = NeoBuzz.LearnFm.Params.Table,
            Map = () => new FmMap(NeoBuzz.LearnFm.Params.Table),
            Ids = new[] { "mod_ratio", "fm_index", "mod_decay", "feedback", "op3_harmonic", "vel_sensitivity" }.Concat(GlobalIds).ToArray(),
            Detents = Unison(),
            Patches = new()
            {
                ["init"] = null,
                ["tine-keys"] = x => { x.Set(FP.ModRatio, 500); x.Set(FP.FmIndex, 450); x.Set(FP.ModDecay, 500); x.Set(FP.Op3Harmonic, 500); x.Set(FP.EnvDecay, 600); x.Set(FP.EnvSustain, 100); },
                ["growl-bass"] = x => { x.Set(FP.ModRatio, 667); x.Set(FP.FmIndex, 700); x.Set(FP.Feedback, 550); x.Set(FP.ModDecay, 450); x.Set(FP.VelSensitivity, 800); x.Set(FP.EnvSustain, 600); },
                ["bell-pad"] = x => { x.Set(FP.ModRatio, 636); x.Set(FP.FmIndex, 300); x.Set(FP.Op3Harmonic, 250); x.Set(FP.UnisonDetune, 500); x.Set(FP.EnvAttack, 600); x.Set(FP.EnvRelease, 700); },
            },
            Heaviest = x => { x.Set(FP.UnisonDetune, 1000); x.Set(FP.FmIndex, 1000); x.Set(FP.Feedback, 1000); x.Set(FP.Op3Harmonic, 1000); x.Set(FP.ModRatio, 1000); },
        };

        internal static readonly M Wt = new()
        {
            Gear = "Neo Learn Wavetable",
            Table = NeoBuzz.LearnWt.Params.Table,
            Map = () => new WavetableMap(NeoBuzz.LearnWt.Params.Table),
            Ids = new[] { "wt_position", "phase_warp", "table_select", "scan_speed", "scan_depth", "sub_anchor" }.Concat(GlobalIds).ToArray(),
            Detents = Unison((DyeP.ATable, "Wavetable: a choice of table")),
            Patches = new()
            {
                ["init"] = null,
                ["sync-lead"] = x => { x.Set(WP.TableSelect, 4); x.Set(WP.PhaseWarp, 800); x.Set(WP.ScanDepth, 700); x.Set(WP.ScanSpeed, 700); x.Set(WP.EnvSustain, 800); },
                ["pinch-bass"] = x => { x.Set(WP.TableSelect, 0); x.Set(WP.PhaseWarp, 150); x.Set(WP.SubAnchor, 600); x.Set(WP.WtPosition, 200); x.Set(WP.ScanDepth, 500); },
                ["motion-pad"] = x => { x.Set(WP.TableSelect, 3); x.Set(WP.ScanDepth, 900); x.Set(WP.ScanSpeed, 300); x.Set(WP.UnisonDetune, 700); x.Set(WP.EnvAttack, 600); },
            },
            Heaviest = x => { x.Set(WP.UnisonDetune, 1000); x.Set(WP.PhaseWarp, 1000); x.Set(WP.ScanDepth, 1000); x.Set(WP.ScanSpeed, 1000); x.Set(WP.SubAnchor, 1000); x.Set(WP.TableSelect, 5); },
        };

        internal static readonly M Add = new()
        {
            Gear = "Neo Learn Additive",
            Table = NeoBuzz.LearnAdd.Params.Table,
            Map = () => new AdditiveMap(NeoBuzz.LearnAdd.Params.Table),
            Ids = new[] { "spectral_tilt", "odd_even_balance", "partial_count", "inharmonicity", "harmonic_comb", "partial_shimmer" }.Concat(GlobalIds).ToArray(),
            Detents = Unison((DyeP.AHPartials, "Partials: a count")),
            Patches = new()
            {
                ["init"] = null,
                ["reed"] = x => { x.Set(AP.OddEvenBalance, 0); x.Set(AP.PartialCount, 23); x.Set(AP.SpectralTilt, 600); },
                ["bell"] = x => { x.Set(AP.Inharmonicity, 700); x.Set(AP.PartialCount, 23); x.Set(AP.EnvDecay, 700); x.Set(AP.EnvSustain, 0); x.Set(AP.SpectralTilt, 900); },
                ["choir"] = x => { x.Set(AP.PartialShimmer, 800); x.Set(AP.HarmonicComb, 600); x.Set(AP.UnisonDetune, 500); x.Set(AP.PartialCount, 63); x.Set(AP.EnvAttack, 500); },
            },
            Heaviest = x => { x.Set(AP.UnisonDetune, 1000); x.Set(AP.PartialCount, 63); x.Set(AP.PartialShimmer, 1000); x.Set(AP.HarmonicComb, 500); x.Set(AP.Inharmonicity, 500); x.Set(AP.SpectralTilt, 1000); },
        };

        internal static readonly M Grain = new()
        {
            Gear = "Neo Learn Granular",
            Table = NeoBuzz.LearnGrain.Params.Table,
            Map = () => new GranularMap(NeoBuzz.LearnGrain.Params.Table),
            Ids = new[] { "sample_position", "grain_size", "grain_density", "position_spray", "pitch_shimmer", "grain_shape" }.Concat(GlobalIds).ToArray(),
            Detents = Unison(),
            Patches = new()
            {
                ["init"] = null,
                ["frozen-choir"] = x => { x.Set(GP.SamplePosition, 300); x.Set(GP.GrainSize, 780); x.Set(GP.GrainDensity, 760); x.Set(GP.GrainShape, 900); x.Set(GP.EnvAttack, 600); x.Set(GP.UnisonDetune, 400); },
                ["glitch-stutter"] = x => { x.Set(GP.GrainSize, 270); x.Set(GP.GrainDensity, 330); x.Set(GP.PositionSpray, 50); x.Set(GP.GrainShape, 0); x.Set(GP.EnvAttack, 100); x.Set(GP.EnvSustain, 0); x.Set(GP.EnvDecay, 450); },
                ["octave-glow"] = x => { x.Set(GP.PitchShimmer, 900); x.Set(GP.GrainSize, 800); x.Set(GP.GrainDensity, 800); x.Set(GP.PositionSpray, 0); x.Set(GP.GrainShape, 1000); x.Set(GP.UnisonDetune, 300); },
            },
            Heaviest = x => { x.Set(GP.UnisonDetune, 1000); x.Set(GP.GrainSize, 1000); x.Set(GP.GrainDensity, 1000); x.Set(GP.PitchShimmer, 1000); x.Set(GP.PositionSpray, 1000); x.Set(GP.GrainShape, 500); },
        };

        internal static readonly M Phys = new()
        {
            Gear = "Neo Learn Physical",
            Table = NeoBuzz.LearnPhys.Params.Table,
            Map = () => new PhysicalMap(NeoBuzz.LearnPhys.Params.Table),
            Ids = new[] { "material", "exciter_type", "exciter_force", "resonator_shape", "damping", "body_size" }.Concat(GlobalIds).ToArray(),
            Detents = Unison((DyeP.AMGeom, "Geometry: the stop the blend runs from; GeomBlend carries the spacing across with no jump (tested below)")),
            Patches = new()
            {
                ["init"] = null,
                ["marimba"] = x => { x.Set(PP.Material, 50); x.Set(PP.ExciterType, 0); x.Set(PP.ExciterForce, 550); x.Set(PP.ResonatorShape, 667); x.Set(PP.Damping, 450); },
                ["nylon-pluck"] = x => { x.Set(PP.Material, 300); x.Set(PP.ExciterType, 500); x.Set(PP.ResonatorShape, 0); x.Set(PP.Damping, 400); },
                ["cello-bow"] = x => { x.Set(PP.Material, 250); x.Set(PP.ExciterType, 1000); x.Set(PP.ResonatorShape, 0); x.Set(PP.BodySize, 850); x.Set(PP.EnvAttack, 450); x.Set(PP.UnisonDetune, 250); },
            },
            Heaviest = x => { x.Set(PP.UnisonDetune, 1000); x.Set(PP.ExciterForce, 1000); x.Set(PP.Material, 1000); x.Set(PP.Damping, 0); x.Set(PP.ExciterType, 750); x.Set(PP.ResonatorShape, 330); },
        };

        internal static readonly M Str = new()
        {
            Gear = "Neo Learn String",
            Table = NeoBuzz.LearnString.Params.Table,
            Map = () => new StringMap(NeoBuzz.LearnString.Params.Table),
            Ids = new[] { "pluck_position", "string_damping", "pick_hardness", "string_decay", "dispersion", "sympathetic_res" }.Concat(GlobalIds).ToArray(),
            Detents = Unison(),
            Patches = new()
            {
                ["init"] = null,
                ["steel-harp"] = x => { x.Set(SP.PickPosition, 400); x.Set(SP.StringDamping, 200); x.Set(SP.PickHardness, 750); x.Set(SP.StringDecay, 800); x.Set(SP.SympatheticRes, 450); },
                ["stiff-chime"] = x => { x.Set(SP.PickPosition, 500); x.Set(SP.StringDamping, 100); x.Set(SP.Dispersion, 850); x.Set(SP.StringDecay, 900); x.Set(SP.PickHardness, 850); },
                ["sitar-drone"] = x => { x.Set(SP.PickPosition, 80); x.Set(SP.StringDecay, 980); x.Set(SP.SympatheticRes, 850); x.Set(SP.UnisonDetune, 200); x.Set(SP.EnvAttack, 300); },
            },
            Heaviest = x => { x.Set(SP.UnisonDetune, 1000); x.Set(SP.SympatheticRes, 1000); x.Set(SP.StringDecay, 1000); x.Set(SP.Dispersion, 1000); x.Set(SP.StringDamping, 0); },
        };

        internal static readonly M Vox = new()
        {
            Gear = "Neo Learn Formant",
            Table = NeoBuzz.LearnFormant.Params.Table,
            Map = () => new FormantMap(NeoBuzz.LearnFormant.Params.Table),
            Ids = new[] { "vowel_morph", "vocal_tract_scale", "formant_resonance", "glottal_edge", "breath_air", "vowel_env_mod" }.Concat(GlobalIds).ToArray(),
            Detents = Unison(),
            Patches = new()
            {
                ["init"] = null,
                ["ooh-swell"] = x => { x.Set(VP.VowelMorph, 1000); x.Set(VP.TractScale, 550); x.Set(VP.EnvAttack, 600); x.Set(VP.BreathAir, 150); },
                ["wah-talker"] = x => { x.Set(VP.TractScale, 300); x.Set(VP.FormantQ, 600); x.Set(VP.GlottalEdge, 700); x.Set(VP.TalkingEnv, 800); x.Set(VP.BreathAir, 0); },
                ["whisper"] = x => { x.Set(VP.VowelMorph, 500); x.Set(VP.BreathAir, 900); x.Set(VP.GlottalEdge, 50); x.Set(VP.FormantQ, 450); },
            },
            Heaviest = x => { x.Set(VP.UnisonDetune, 1000); x.Set(VP.FormantQ, 1000); x.Set(VP.GlottalEdge, 1000); x.Set(VP.BreathAir, 1000); x.Set(VP.TalkingEnv, 1000); x.Set(VP.TractScale, 1000); },
        };

        internal static readonly M Fold = new()
        {
            Gear = "Neo Learn Wavefolder",
            Table = NeoBuzz.LearnFold.Params.Table,
            Map = () => new WavefolderMap(NeoBuzz.LearnFold.Params.Table),
            Ids = new[] { "fold_drive", "fold_symmetry", "fold_stages", "linear_fm", "lpg_mode", "lpg_ping" }.Concat(GlobalIds).ToArray(),
            Detents = Unison(),
            Patches = new()
            {
                ["init"] = null,
                ["bongo-ping"] = x => { x.Set(XP.FoldDrive, 550); x.Set(XP.FoldStages, 2); x.Set(XP.LpgMode, 850); x.Set(XP.GateStrike, 850); x.Set(XP.EnvSustain, 0); x.Set(XP.EnvDecay, 450); },
                ["sizzle-lead"] = x => { x.Set(XP.FoldDrive, 750); x.Set(XP.FoldStages, 3); x.Set(XP.FoldSymmetry, 600); x.Set(XP.TimbreFm, 350); x.Set(XP.LpgMode, 300); },
                ["rust-pad"] = x => { x.Set(XP.FoldDrive, 550); x.Set(XP.FoldStages, 4); x.Set(XP.UnisonDetune, 350); x.Set(XP.EnvAttack, 600); x.Set(XP.LpgMode, 150); },
            },
            Heaviest = x => { x.Set(XP.UnisonDetune, 1000); x.Set(XP.FoldDrive, 1000); x.Set(XP.FoldStages, 5); x.Set(XP.TimbreFm, 1000); x.Set(XP.FoldSymmetry, 1000); x.Set(XP.GateStrike, 1000); },
        };

        internal static readonly M Pd = new()
        {
            Gear = "Neo Learn Phase Distortion",
            Table = NeoBuzz.LearnPd.Params.Table,
            Map = () => new PhaseDistortionMap(NeoBuzz.LearnPd.Params.Table),
            Ids = new[] { "phase_bend", "resonance", "pd_wave_shape", "bend_decay", "ring_mod", "chorus" }.Concat(GlobalIds).ToArray(),
            Detents = Unison(),
            Patches = new()
            {
                ["init"] = null,
                ["bend-lead"] = x => { x.Set(YP.PhaseBend, 800); x.Set(YP.PdWaveShape, 1); x.Set(YP.BendDecay, 400); x.Set(YP.Resonance, 100); },
                ["ring-bell"] = x => { x.Set(YP.RingMod, 700); x.Set(YP.PdWaveShape, 3); x.Set(YP.Resonance, 100); x.Set(YP.EnvSustain, 0); x.Set(YP.EnvDecay, 650); },
                ["resonant-saw"] = x => { x.Set(YP.PdWaveShape, 0); x.Set(YP.Resonance, 800); x.Set(YP.BendDecay, 700); x.Set(YP.Chorus, 600); x.Set(YP.UnisonDetune, 250); },
            },
            Heaviest = x => { x.Set(YP.UnisonDetune, 1000); x.Set(YP.PhaseBend, 1000); x.Set(YP.Resonance, 1000); x.Set(YP.RingMod, 1000); x.Set(YP.Chorus, 1000); x.Set(YP.PdWaveShape, 5); },
        };

        /// <summary>The Vector corners swap sources with the palette (a type, a shape, a table or a sample): a choice, not a sweep.</summary>
        static Dictionary<int, string> VectorDetents() => Unison(
            (DyeP.AType, "Corner palette: each palette sets what its corners are"), (DyeP.BType, "Corner palette"), (DyeP.CType, "Corner palette"),
            (DyeP.AShape, "Corner palette"), (DyeP.BShape, "Corner palette"), (DyeP.CShape, "Corner palette"),
            (DyeP.ATable, "Corner palette"), (DyeP.BTable, "Corner palette"), (DyeP.CTable, "Corner palette"),
            (DyeP.ASmpSrc, "Corner palette"), (DyeP.BSmpSrc, "Corner palette"), (DyeP.CSmpSrc, "Corner palette"), (DyeP.SubShape, "Corner palette"));

        internal static readonly M Vec = new()
        {
            Gear = "Neo Learn Vector",
            Table = NeoBuzz.LearnVector.Params.Table,
            Map = () => new VectorMap(NeoBuzz.LearnVector.Params.Table),
            Ids = new[] { "vector_x", "vector_y", "corner_palette", "orbit_speed", "orbit_radius", "orbit_shape" }.Concat(GlobalIds).ToArray(),
            Detents = VectorDetents(),
            Patches = new()
            {
                ["init"] = null,
                ["west-corner"] = x => { x.Set(ZP.VectorX, 0); x.Set(ZP.OrbitRadius, 0); },
                ["orbit-circle"] = x => { x.Set(ZP.OrbitSpeed, 500); x.Set(ZP.OrbitRadius, 800); x.Set(ZP.OrbitShape, 0); x.Set(ZP.EnvSustain, 900); },
                ["cyber-eight"] = x => { x.Set(ZP.CornerPalette, 2); x.Set(ZP.VectorX, 700); x.Set(ZP.OrbitShape, 1); x.Set(ZP.OrbitRadius, 500); x.Set(ZP.OrbitSpeed, 450); },
            },
            Heaviest = x => { x.Set(ZP.UnisonDetune, 1000); x.Set(ZP.OrbitRadius, 1000); x.Set(ZP.OrbitSpeed, 900); x.Set(ZP.CornerPalette, 3); x.Set(ZP.OrbitShape, 3); },
        };

        internal static readonly M Smp = new()
        {
            Gear = "Neo Learn Sampler",
            Table = NeoBuzz.LearnSampler.Params.Table,
            Map = () => new SamplerMap(NeoBuzz.LearnSampler.Params.Table),
            Ids = new[] { "sample_offset", "timbre_tilt", "source", "loop", "vintage_dac", "room_space" }.Concat(GlobalIds).ToArray(),
            Detents = Unison((DyeP.ASmpSrc, "Source: which sample; a choice"), (DyeP.ASmpLoop, "Loop: off or forward")),
            Patches = new()
            {
                ["init"] = null,
                ["strings-loop"] = x => { x.Set(QP.Source, 1); x.Set(QP.Loop, 500); x.Set(QP.EnvAttack, 500); },
                ["bell-grit"] = x => { x.Set(QP.Source, 2); x.Set(QP.VintageDac, 800); x.Set(QP.TimbreTilt, 300); },
                ["texture-room"] = x => { x.Set(QP.Source, 4); x.Set(QP.RoomSpace, 900); x.Set(QP.TimbreTilt, 800); x.Set(QP.SampleOffset, 300); },
            },
            Heaviest = x => { x.Set(QP.UnisonDetune, 1000); x.Set(QP.Loop, 1000); x.Set(QP.VintageDac, 1000); x.Set(QP.RoomSpace, 1000); x.Set(QP.Source, 4); },
        };

        internal static readonly M Spc = new()
        {
            Gear = "Neo Learn Spectral",
            Table = NeoBuzz.LearnSpectral.Params.Table,
            Map = () => new SpectralMap(NeoBuzz.LearnSpectral.Params.Table),
            Ids = new[] { "spectral_shift", "spectral_blur", "spectral_freeze", "spectral_source", "bin_gate", "tonal_noise" }.Concat(GlobalIds).ToArray(),
            Detents = Unison((DyeP.ASmpSrc, "Source: which sample; a choice"), (DyeP.ASpPartials, "Partial Gate: a count of partials")),
            Patches = new()
            {
                ["init"] = null,
                ["frozen-choir"] = x => { x.Set(LS.SpectralFreeze, 1000); x.Set(LS.SpectralBlur, 400); x.Set(LS.EnvAttack, 500); x.Set(LS.EnvSustain, 1000); },
                ["bell-gated"] = x => { x.Set(LS.Source, 2); x.Set(LS.PartialGate, 700); x.Set(LS.SpectralShift, 450); x.Set(LS.ToneNoise, 850); },
                ["texture-ghost"] = x => { x.Set(LS.Source, 4); x.Set(LS.ToneNoise, 150); x.Set(LS.SpectralFreeze, 400); x.Set(LS.UnisonDetune, 500); },
            },
            Heaviest = x => { x.Set(LS.UnisonDetune, 1000); x.Set(LS.PartialGate, 0); x.Set(LS.SpectralBlur, 1000); x.Set(LS.SpectralShift, 1000); x.Set(LS.Source, 4); },
        };

        internal static IEnumerable<M> All => new[] { Fm, Wt, Add, Grain, Phys, Str, Vox, Fold, Pd, Vec, Smp, Spc };

        static void Tables()
        {
            for (int i = 0; i < TableBank.Count; i++) TableBank.GetNow(i);
            for (int i = 0; i < SampleSources.Count; i++) SampleSources.GetNow(i);
            for (int i = 0; i < SpectralSources.Count; i++) SpectralSources.GetNow(i);
        }

        static (LearnHost Host, ParamValues Values) Make(M m, Action<ParamValues> tweak = null)
        {
            Tables();
            var v = new ParamValues(m.Table);
            tweak?.Invoke(v);
            var h = new LearnHost(m.Map(), v);
            h.SetSampleRate(Rate);
            return (h, v);
        }

        static (float[] L, float[] R) Render(LearnHost h, int frames, Action<int> perBlock = null)
        {
            var l = new float[frames];
            var r = new float[frames];
            var bl = new float[Block];
            var br = new float[Block];
            for (int pos = 0, b = 0; pos < frames; pos += Block, b++)
            {
                int n = Math.Min(Block, frames - pos);
                perBlock?.Invoke(b);
                h.Process(bl, br, n);
                Array.Copy(bl, 0, l, pos, n);
                Array.Copy(br, 0, r, pos, n);
            }
            return (l, r);
        }

        static ulong Hash(float[] x)
        {
            ulong h = 1469598103934665603UL;
            foreach (var v in x) h = (h ^ BitConverter.SingleToUInt32Bits(v)) * 1099511628211UL;
            return h;
        }

        static void Chord(LearnHost h, params int[] notes)
        {
            for (int i = 0; i < notes.Length; i++) h.Engine.NoteOn(i, notes[i], 0.8f);
        }

        // ------------------------------------------------------------------ the controls

        [TestCaseSource(nameof(All))]
        public void TheTwelveControlsAreInSpecOrderWithTheirExplainText(M m)
        {
            Assert.That(m.Table.Select(d => d.Id), Is.EqualTo(m.Ids));
            foreach (var d in m.Table)
            {
                Assert.That(d.How, Is.Not.Empty, d.Id);
                Assert.That(d.Sound, Is.Not.Empty, d.Id);
                Assert.That(d.Description, Is.EqualTo(d.How), d.Id + ": the Buzz description is the How sentence");
                if (d.Kind is not (MapKind.Enum or MapKind.Int or MapKind.Switch))
                    Assert.That(d.Max - d.Min, Is.GreaterThanOrEqualTo(1000), d.Id + ": at least 1000 raw steps");
                else
                    Assert.That(d.Max - d.Min, Is.GreaterThanOrEqualTo(d.Id switch { "fold_stages" or "pd_wave_shape" or "source" or "spectral_source" => 5, "corner_palette" or "orbit_shape" => 3, _ => 10 }),
                        d.Id + ": a count or a choice (Fold Stages is 1 to 6, the PD Wave six geometries, Source six samples (Spectral too), Palette and Orbit Shape four: steps by nature)");
            }
            var t = m.Table;
            Assert.That(t[6].Value(t[6].Min), Is.EqualTo(0.0005).Within(1e-12));
            Assert.That(t[6].Value(t[6].Max), Is.EqualTo(5).Within(1e-9));
            Assert.That(t[7].Value(t[7].Min), Is.EqualTo(0.005).Within(1e-12));
            Assert.That(t[7].Value(t[7].Max), Is.EqualTo(8).Within(1e-9));
            Assert.That(t[9].Value(t[9].Min), Is.EqualTo(0.005).Within(1e-12));
            Assert.That(t[9].Value(t[9].Max), Is.EqualTo(10).Within(1e-9));
            Assert.That(t[11].Value(t[11].Min), Is.EqualTo(-36));
            Assert.That(t[11].Value(t[11].Max), Is.EqualTo(12));
        }

        [Test]
        public void TheCoreRangesAreTheAppendixOnes()
        {
            var f = NeoBuzz.LearnFm.Params.Table;
            Assert.That(f[0].Value(f[0].Min), Is.EqualTo(0.25).Within(1e-9));
            Assert.That(f[0].Value(f[0].Max), Is.EqualTo(16).Within(1e-9));
            Assert.That(f[0].Unit, Is.EqualTo("×"));
            Assert.That(f[2].Value(f[2].Min), Is.EqualTo(0.005).Within(1e-12));
            Assert.That(f[2].Value(f[2].Max), Is.EqualTo(5).Within(1e-9));
            var w = NeoBuzz.LearnWt.Params.Table;
            Assert.That(w[1].Value(w[1].Min), Is.EqualTo(-1));
            Assert.That(w[1].Value(w[1].Max), Is.EqualTo(1));
            Assert.That(w[2].Names, Is.EqualTo(new[] { "Basic", "PWM", "Harmonics", "Formants", "Sync sweep", "FM sweep", "Organ", "Resonant", "Digital", "Grit", "User" }));
            Assert.That(w[3].Value(w[3].Min), Is.EqualTo(0.05).Within(1e-9));
            Assert.That(w[3].Value(w[3].Max), Is.EqualTo(15).Within(1e-9));
            var a = NeoBuzz.LearnAdd.Params.Table;
            Assert.That(a[0].Value(a[0].Min), Is.EqualTo(-24).Within(1e-9));
            Assert.That(a[0].Value(a[0].Max), Is.EqualTo(6).Within(1e-9));
            Assert.That(a[2].Value(a[2].Min), Is.EqualTo(1));
            Assert.That(a[2].Value(a[2].Max), Is.EqualTo(64));
        }

        [Test]
        public void TheRatioHasDetentsAtWholeAndHalfRatios()
        {
            var d = NeoBuzz.LearnFm.Params.Table[0];
            var seen = new SortedSet<double>();
            for (int raw = d.Min; raw <= d.Max; raw++)
            {
                double v = d.Value(raw);
                double nearest = Math.Round(v * 2) / 2;
                if (nearest > 0 && Math.Abs(v - nearest) < 1e-9) seen.Add(nearest);
            }
            foreach (double want in new[] { 0.5, 1, 1.5, 2, 2.5, 3, 4, 5.5, 8, 15.5, 16 })
                Assert.That(seen, Does.Contain(want), $"{want}x has a detent");
            // A detent rests: several neighbouring steps read the same ratio, and a round trip lands on it.
            int run = 0;
            for (int raw = d.Min; raw <= d.Max; raw++) if (d.Value(raw) == 2.0) run++;
            Assert.That(run, Is.GreaterThanOrEqualTo(3), "2x spans a few steps of the knob");
            foreach (double want in new[] { 0.5, 1, 2, 3.5, 7, 16 }) Assert.That(d.Value(d.Raw(want)), Is.EqualTo(want), $"{want}");
            Assert.That(d.Value(d.Raw(1.25)), Is.EqualTo(1.25).Within(0.01), "between detents the ratio is free");
            Assert.That(d.Value(d.Raw(1.3)), Is.EqualTo(1.3).Within(0.01));
            Assert.That(d.Describe(d.Raw(2.0)), Is.EqualTo("2.00×"));
        }

        // ------------------------------------------------------------------ the macro maps

        sealed class Recorder : IDyeSink
        {
            public readonly Dictionary<int, double> Values = new();
            public readonly Dictionary<(int Slot, SlotExt What), double> Exts = new();
            public void Set(int dyeParam, double value) => Values[dyeParam] = value;
            public void Ext(int slot, SlotExt what, double value) => Exts[(slot, what)] = value;
        }

        static double[] Controls(M m, int[] raw)
        {
            var c = new double[MacroMap.Count];
            for (int i = 0; i < c.Length; i++) c[i] = m.Table[i].Value(raw[i]);
            return c;
        }

        static int[] Defaults(M m) => m.Table.Select(d => d.Default).ToArray();

        [TestCaseSource(nameof(All))]
        public void EveryControlMapsInRangeAndContinuously(M m)
        {
            var map = m.Map();
            var dye = NeoBuzz.DyeEssence.Params.Table;
            for (int control = 0; control < MacroMap.Count; control++)
            {
                var d = m.Table[control];
                Recorder prev = null;
                int[] raw = Defaults(m);
                for (int x = d.Min; x <= d.Max; x++)
                {
                    raw[control] = x;
                    var rec = new Recorder();
                    map.Map(Controls(m, raw), rec);
                    foreach (var (index, value) in rec.Values)
                    {
                        Assert.That(double.IsFinite(value), Is.True, $"{d.Id}={x}: {dye[index].Id}");
                        int dr = dye[index].Raw(value);
                        Assert.That(dr, Is.InRange(dye[index].Min, dye[index].Max), $"{d.Id}={x}: {dye[index].Id}");
                        if (prev == null || !prev.Values.TryGetValue(index, out double before)) continue;
                        var def = dye[index];
                        double step = def.Kind == MapKind.Power
                            ? Math.Abs(def.Value(def.Raw(value)) - def.Value(def.Raw(before))) / Math.Abs(def.B - def.A)
                            : Math.Abs(def.Normalized(def.Raw(value)) - def.Normalized(def.Raw(before)));
                        if (def.Kind is MapKind.Enum or MapKind.Int or MapKind.Switch)
                        {
                            if (step > 0) Assert.That(m.Detents.ContainsKey(index), Is.True, $"{d.Id} {x}: {def.Id} steps without a documented detent");
                        }
                        else Assert.That(step, Is.LessThanOrEqualTo(0.012), $"{d.Id} {x}: {def.Id} jumps by {step:F4} of its range");
                    }
                    foreach (var ((slot, what), value) in rec.Exts)
                    {
                        bool signed = what is SlotExt.FTract or SlotExt.FoldSym or SlotExt.FEnvMod;       // -1..+1: the tract scale and the fold's symmetry offset
                        Assert.That(double.IsFinite(value) && (signed || value >= 0), Is.True, $"{d.Id}={x}: {what}");
                        if (prev == null || !prev.Exts.TryGetValue((slot, what), out double before)) continue;
                        // Geometry's blend and its target move together across a stop: the spacing is continuous (GeometryIsContinuousAcrossItsStops).
                        if (what is SlotExt.GeomBlend or SlotExt.GeomTo or SlotExt.FoldStages or SlotExt.PdShape) continue;     // FoldStages: a count, 1 to 6; PdShape: a choice of six geometries
                        if (what == SlotExt.ExactRatio)
                        {
                            // The modulator's ratio: free between its whole and half detents (a snap is up to 1.5%).
                            if (value != before) Assert.That(Math.Abs(Math.Log2(value / before)), Is.LessThan(0.03), $"{d.Id} {x}: ratio {before} -> {value}");
                        }
                        else
                        {
                            double span = what switch { SlotExt.Feedback => FmMap.FeedbackCycles, SlotExt.Inharm => AdditiveMap.StiffnessAtFull, SlotExt.CombPeriod => 8, SlotExt.StrDecay => 20, SlotExt.FTract or SlotExt.FoldSym or SlotExt.FEnvMod => 2, _ => 1 };
                            Assert.That(Math.Abs(value - before) / span, Is.LessThanOrEqualTo(0.012), $"{d.Id} {x}: {what} {before} -> {value}");
                        }
                    }
                    prev = rec;
                }
            }
        }

        [Test]
        public void TheModulatorsRatioIsExactAndTheDetentsAreDocumented()
        {
            var map = Fm.Map();
            foreach (double want in new[] { 0.5, 1, 2, 3, 4, 7.5, 16 })
            {
                var raw = Defaults(Fm);
                raw[FmMap.ModRatio] = Fm.Table[0].Raw(want);
                var rec = new Recorder();
                map.Map(Controls(Fm, raw), rec);
                Assert.That(rec.Exts[(FmMap.Modulator, SlotExt.ExactRatio)], Is.EqualTo(want), "an exact ratio, not a pitch in cents");
                Assert.That(rec.Exts[(FmMap.Op3, SlotExt.ExactRatio)], Is.EqualTo(FmMap.Op3Ratio));
            }
        }

        // ------------------------------------------------------------------ the host

        [TestCaseSource(nameof(All))]
        public void TheHostPlaysTheInitSoundTheSameEveryTime(M m)
        {
            ulong first = 0;
            for (int run = 0; run < 2; run++)
            {
                var (h, _) = Make(m);
                Chord(h, 48, 55, 60);
                var (l, r) = Render(h, Rate);
                Assert.That(Measure.Rms(l, 0, l.Length), Is.GreaterThan(0.01), "it sounds");
                ulong x = Hash(l) ^ (Hash(r) * 31);
                if (run == 0) first = x;
                else Assert.That(x, Is.EqualTo(first), "the base patch is code: the same bits");
            }
        }

        // ------------------------------------------------------------------ pinned renders

        static (float[] L, float[] R) Pinned(M m, string patch)
        {
            var (h, v) = Make(m, m.Patches[patch]);
            h.Engine.NoteOn(0, 45, 0.9f);
            h.Engine.NoteOn(1, 52, 0.7f);
            h.Engine.NoteOn(2, 57, 0.8f);
            return Render(h, Rate * 3 / 2, b =>
            {
                if (b == 60) h.Engine.NoteOff(1);
                if (b == 150) h.Engine.NoteOff(0);
            });
        }

        static readonly Dictionary<(string, string), ulong> Pins = new()
        {
            [("Neo Learn Additive", "bell")] = 0x1AB91719295F7B3AUL,
            [("Neo Learn Additive", "choir")] = 0xB73958E0FBDD6F40UL,
            [("Neo Learn Additive", "init")] = 0xDFDDEAB5E3ADF074UL,
            [("Neo Learn Additive", "reed")] = 0x162E71E59FA5D6F2UL,
            [("Neo Learn FM", "bell-pad")] = 0x2862B2B2B2C42816UL,
            [("Neo Learn FM", "growl-bass")] = 0xE381F9D7999675E8UL,
            [("Neo Learn FM", "init")] = 0x6D5D48FFAD5067A0UL,
            [("Neo Learn FM", "tine-keys")] = 0x6B8A9B45372CCA7AUL,
            [("Neo Learn Granular", "frozen-choir")] = 0x045AB989C0510C67UL,
            [("Neo Learn Granular", "glitch-stutter")] = 0x02BF11B324982447UL,
            [("Neo Learn Granular", "init")] = 0x94C307C8E58D38F5UL,
            [("Neo Learn Granular", "octave-glow")] = 0x193FABF8DB38DDA7UL,
            [("Neo Learn Physical", "cello-bow")] = 0x0647AEA1D95C424EUL,
            [("Neo Learn Physical", "init")] = 0x7975571E5EAEDA74UL,
            [("Neo Learn Physical", "marimba")] = 0x111EA98B4064725AUL,
            [("Neo Learn Physical", "nylon-pluck")] = 0x2C8A96A392C8AEE8UL,
            [("Neo Learn Formant", "init")] = 0xD61B3F0872032A34UL,
            [("Neo Learn Formant", "ooh-swell")] = 0x0E4A2FB03BB6D0E8UL,
            [("Neo Learn Formant", "wah-talker")] = 0x6ACA19DCC3AADEF2UL,
            [("Neo Learn Formant", "whisper")] = 0xFF07786644BD77F4UL,
            [("Neo Learn String", "init")] = 0x8C20D2B5956FC6BAUL,
            [("Neo Learn String", "sitar-drone")] = 0x17794696305CBDD6UL,
            [("Neo Learn String", "steel-harp")] = 0xD51222BA72D677C4UL,
            [("Neo Learn String", "stiff-chime")] = 0x03D2A3C7F77C19F4UL,
            [("Neo Learn Wavefolder", "bongo-ping")] = 0xC5DEB23B0761EFF2UL,
            [("Neo Learn Wavefolder", "init")] = 0x0CA73907C9AF1CB4UL,
            [("Neo Learn Wavefolder", "rust-pad")] = 0x0ED5832A3AC5CFF1UL,
            [("Neo Learn Wavefolder", "sizzle-lead")] = 0xE7501CC043F322D0UL,
            [("Neo Learn Phase Distortion", "bend-lead")] = 0x7EC7E23D722D02FDUL,
            [("Neo Learn Phase Distortion", "init")] = 0x945FF1E37748AC5AUL,
            [("Neo Learn Phase Distortion", "resonant-saw")] = 0xB5E35FDF5738F59FUL,
            [("Neo Learn Phase Distortion", "ring-bell")] = 0x0EEC227988EEEE8BUL,
            [("Neo Learn Sampler", "bell-grit")] = 0xDD2C4807E09969BDUL,
            [("Neo Learn Sampler", "init")] = 0x51879498562C8087UL,
            [("Neo Learn Sampler", "strings-loop")] = 0x9C0020512F1BDAC7UL,
            [("Neo Learn Sampler", "texture-room")] = 0x3D3A137BF81903F2UL,
            [("Neo Learn Spectral", "bell-gated")] = 0x5CFAB6C7FF46C43AUL,
            [("Neo Learn Spectral", "frozen-choir")] = 0x73021C578E7B7B82UL,
            [("Neo Learn Spectral", "init")] = 0xD36E5E4B424A0C08UL,
            [("Neo Learn Spectral", "texture-ghost")] = 0x30866E5B1930E3E7UL,
            [("Neo Learn Vector", "cyber-eight")] = 0x0718A197A7BE4C84UL,
            [("Neo Learn Vector", "init")] = 0xC25FE09F9A3F2F62UL,
            [("Neo Learn Vector", "orbit-circle")] = 0x4A1D59F2DBF4FAB4UL,
            [("Neo Learn Vector", "west-corner")] = 0x001222D104BFAF10UL,
            [("Neo Learn Wavetable", "init")] = 0x6F81FD8A5E4982B4UL,
            [("Neo Learn Wavetable", "motion-pad")] = 0xF956CE4240287174UL,
            [("Neo Learn Wavetable", "pinch-bass")] = 0x5EAE2C23152D379AUL,
            [("Neo Learn Wavetable", "sync-lead")] = 0x73876DF2E84E7AD0UL,
        };

        internal static IEnumerable<TestCaseData> PinCases()
        {
            foreach (var m in All)
                foreach (var p in m.Patches.Keys)
                    yield return new TestCaseData(m, p).SetName($"PinnedRendersAreTheSameOnEveryPlatform({m.Gear}, {p})");
        }

        [TestCaseSource(nameof(PinCases))]
        public void PinnedRendersAreTheSameOnEveryPlatform(M m, string patch)
        {
            var (l, r) = Pinned(m, patch);
            ulong h = Hash(l) * 1099511628211UL ^ Hash(r);
            TestContext.Out.WriteLine($"PIN {m.Gear}|{patch}: 0x{h:X16}UL");
            Assert.That(Pins.TryGetValue((m.Gear, patch), out ulong pinned), Is.True, "no pin yet");
            Assert.That(h, Is.EqualTo(pinned), $"{m.Gear} {patch} on {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        }

        // ------------------------------------------------------------------ measuring helpers

        /// <summary>The windowed magnitude spectrum of <paramref name="n"/> samples from <paramref name="at"/> (4-term Blackman-Harris).</summary>
        static double[] Spec(float[] x, int at, int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                w[i] = (float)(x[at + i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
            }
            return Fft.Magnitudes(w, 0, n);
        }

        /// <summary>The strongest bin's magnitude within <paramref name="rel"/> of <paramref name="hz"/> (and its frequency).</summary>
        static double Near(double[] mag, int n, double hz, out double at, double rel = 0.02)
        {
            double bin = (double)Rate / n, best = 0;
            at = hz;
            for (int k = Math.Max(1, (int)(hz * (1 - rel) / bin)); k <= Math.Min(mag.Length - 1, (int)(hz * (1 + rel) / bin) + 1); k++)
                if (mag[k] > best) { best = mag[k]; at = k * bin; }
            return best;
        }

        /// <summary>The share of the energy within a few bins of the fundamental (1: a pure sine).</summary>
        static double Fundamental(float[] x, int at, int n, double hz)
        {
            var mag = Spec(x, at, n);
            double bin = (double)Rate / n, near = 0, all = 0;
            int k0 = (int)Math.Round(hz / bin);
            for (int k = 1; k < mag.Length; k++)
            {
                double e = mag[k] * mag[k];
                all += e;
                if (Math.Abs(k - k0) <= 4) near += e;
            }
            return near / Math.Max(all, 1e-30);
        }

        /// <summary>The spectral centroid, Hz.</summary>
        static double Centroid(float[] x, int at, int n)
        {
            var mag = Spec(x, at, n);
            double bin = (double)Rate / n, num = 0, den = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                num += k * bin * mag[k] * mag[k];
                den += mag[k] * mag[k];
            }
            return num / Math.Max(den, 1e-30);
        }

        static float[] Note(M m, int note, int frames, Action<ParamValues> tweak, float velocity = 1f, Action<LearnHost, int> perBlock = null)
        {
            var (h, _) = Make(m, tweak);
            h.Engine.NoteOn(0, note, velocity);
            return Render(h, frames, b => perBlock?.Invoke(h, b)).L;
        }

        // ------------------------------------------------------------------ safety

        [TestCaseSource(nameof(All))]
        public void ExtremeSettingsStayBoundedAndFinite(M m)
        {
            var rng = new Random(17);
            var (h, v) = Make(m);
            for (int round = 0; round < 40; round++)
            {
                foreach (var d in m.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                v.Set(11, m.Table[11].Default);
                for (int k = 0; k < 6; k++) h.Engine.NoteOn(k, 24 + rng.Next(90), (float)rng.NextDouble());
                var (l, r) = Render(h, 9600);
                foreach (var s in l.Concat(r))
                {
                    Assert.That(float.IsFinite(s), Is.True, $"{m.Gear} round {round}");
                    Assert.That(Math.Abs(s), Is.LessThan(30), $"{m.Gear} round {round}");
                }
                h.Engine.AllNotesOff(true);
            }
        }

        [Test]
        public void FeedbackAtTheTopStaysBoundedAndLeavesNoDenormals()
        {
            var (h, _) = Make(Fm, x =>
            {
                x.Set(FP.Feedback, 1000);
                x.Set(FP.FmIndex, 1000);
                x.Set(FP.Op3Harmonic, 1000);
                x.Set(FP.EnvRelease, 300);
            });
            Chord(h, 36, 43, 60, 84);
            var (l0, _) = Render(h, 24000);
            Assert.That(l0.Max(Math.Abs), Is.LessThan(6), "feedback never runs away");
            h.Engine.AllNotesOff(false);
            var (l, r) = Render(h, 6 * Rate);
            int tiny = 0;
            foreach (var s in l.Concat(r))
                if (s != 0 && Math.Abs(s) < 1.2e-38f) tiny++;
            Assert.That(tiny, Is.EqualTo(0), "no denormal in the output, through the tail");
        }

        [TestCaseSource(nameof(All))]
        public void TailsLeaveNoDenormals(M m)
        {
            var (h, _) = Make(m, m.Heaviest);
            Chord(h, 36, 43, 60);
            Render(h, 9600);
            h.Engine.AllNotesOff(false);
            var (l, r) = Render(h, 6 * Rate);
            int tiny = 0;
            foreach (var s in l.Concat(r))
                if (s != 0 && Math.Abs(s) < 1.2e-38f) tiny++;
            Assert.That(tiny, Is.EqualTo(0), "no denormal in the output, through the tail");
        }

        [TestCaseSource(nameof(All))]
        public void AudioPathAllocatesNothing(M m)
        {
            var (h, v) = Make(m, x => x.Set(10, 500));
            var bl = new float[Block];
            var br = new float[Block];
            for (int k = 0; k < 8; k++) h.Engine.NoteOn(k, 40 + 3 * k, 0.9f);
            for (int i = 0; i < 200; i++) h.Process(bl, br, Block);   // warm up
            var (processBytes, processWhy) = AllocationCheck.Run(2000, b =>
            {
                if (b % 50 == 0) v.Set(0, 300 + b % 600);
                if (b % 70 == 0) v.Set(1, b % 1000);
                if (b % 60 == 0) v.Set(3, b % 1000);
                if (b % 80 == 0) v.Set(4, b % 1000);
                if (b % 90 == 0) v.Set(10, b % 2 * 700);
                if (b % 400 == 0)
                {
                    h.Engine.NoteOff(b / 400 % 8);
                    h.Engine.NoteOn(b / 400 % 8, 36 + b / 40 % 30, 0.7f);
                }
                h.Process(bl, br, Block);
            });
            Assert.That(processBytes, Is.EqualTo(0), m.Gear + ": " + processWhy);
        }

        // ------------------------------------------------------------------ presets

        static double ChordLevel(M m, ParamValues v)
        {
            Tables();
            var host = new LearnHost(m.Map(), v);
            host.SetSampleRate(Rate);
            var y = PresetTests.RenderSynth(host.Engine, (e, l, r, n) => host.Process(l, r, n), Rate);
            double loudest = 0;
            for (int at = 0; at + 2400 <= y.Length * 2 / 3; at += 1200) loudest = Math.Max(loudest, Measure.Rms(y, at, 2400));
            return Measure.Db(loudest);
        }

        static IEnumerable<(string Name, Dictionary<int, int> Values)> Presets(M m) =>
            PresetTests.AllPresets().Where(p => p.Machine == m.Gear).Select(p => (p.Preset, p.Values.Where(kv => kv.Key >= 0).ToDictionary(kv => kv.Key, kv => kv.Value)));

        [TestCaseSource(nameof(All))]
        public void EveryFactoryPresetIsLevelledToMinus22dB(M m)
        {
            var presets = Presets(m).ToList();
            Assert.That(presets.Count, Is.InRange(8, 12), "Init plus about 8 factory presets");
            foreach (var (name, values) in presets.Prepend(("Init", new Dictionary<int, int>())))
            {
                var v = new ParamValues(m.Table);
                foreach (var kv in values) v.Set(kv.Key, kv.Value);
                double level = ChordLevel(m, v);
                TestContext.Out.WriteLine($"{m.Gear} {name}: loudest 50 ms {level:F2} dB");
                Assert.That(level, Is.InRange(-23.0, -21.0), $"{m.Gear} {name}");
            }
        }

        [Test, Explicit("prints the Output each preset needs")]
        public void PrintLevels()
        {
            foreach (var m in All)
                foreach (var (name, values) in Presets(m).Prepend(("Init", new Dictionary<int, int>())))
                {
                    var v = new ParamValues(m.Table);
                    foreach (var kv in values) v.Set(kv.Key, kv.Value);
                    v.Set(11, 750);
                    double level = ChordLevel(m, v);
                    TestContext.Out.WriteLine($"LEVEL {m.Gear}|{name}|{-22 - level:F1}");
                }
        }

        // ------------------------------------------------------------------ CPU

        [TestCaseSource(nameof(All))]
        public void SixteenNotesOnTheDefaultPatchRunAtLeastFourTimesRealTime(M m)
        {
            Throughput.Guard(m.Gear + ", 16 notes, default patch", Throughput.Floor(x64: 4, arm64: 2), 2, Rate, Block, () =>
            {
                var (h, _) = Make(m);
                for (int k = 0; k < 16; k++) h.Engine.NoteOn(k, 36 + 3 * k, 1f);
                var l = new float[Block];
                var r = new float[Block];
                for (int i = 0; i < 20; i++) h.Process(l, r, Block);
                return Throughput.InBlocks(Block, (pos, n) => h.Process(l, r, n));
            }, Measure.Report);
        }

        [TestCaseSource(nameof(All))]
        public void SixteenNotesOnTheHeaviestSettingsRunAtLeastTwiceRealTime(M m)
        {
            Throughput.Guard(m.Gear + ", 16 notes, heaviest settings", Throughput.Floor(x64: 2, arm64: 1), 2, Rate, Block, () =>
            {
                var (h, _) = Make(m, m.Heaviest);
                for (int k = 0; k < 16; k++) h.Engine.NoteOn(k, 36 + 3 * k, 1f);
                var l = new float[Block];
                var r = new float[Block];
                for (int i = 0; i < 20; i++) h.Process(l, r, Block);
                return Throughput.InBlocks(Block, (pos, n) => h.Process(l, r, n));
            }, Measure.Report);
        }

        // ------------------------------------------------------------------ aliasing

        static double Alias(M m, int note, Action<ParamValues> tweak)
        {
            var l = Note(m, note, 8192 + Measure.N, tweak);
            return PolyTests.AliasFloorWindowed(l, 8192, Measure.N, DspMath.NoteToHz(note));
        }

        [TestCase(33)]
        [TestCase(45)]
        [TestCase(57)]
        public void FmWithAWholeRatioAliasesUnderMinus80dBAtStandard(int note)
        {
            // The default sound's shape (ratio 2x, depth 35 %), held: a whole ratio keeps every sideband on a harmonic, so what is left is alias.
            double floor = Alias(Fm, note, x => { x.Set(FP.ModRatio, 500); x.Set(FP.FmIndex, 350); x.Set(FP.ModDecay, 1000); x.Set(FP.EnvAttack, 0); x.Set(FP.EnvSustain, 1000); });
            Measure.Report($"Neo Learn FM, 2x at depth 35 %, note {note} ({DspMath.NoteToHz(note):F0} Hz), Standard: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(-80));
        }

        [TestCase(33, 1000)]
        [TestCase(57, 1000)]
        [TestCase(57, 700)]
        [TestCase(81, 350)]
        public void FmAtHigherIndicesAndNotesReportsItsAliasFloor(int note, int depth)
        {
            double floor = Alias(Fm, note, x => { x.Set(FP.ModRatio, 500); x.Set(FP.FmIndex, depth); x.Set(FP.ModDecay, 1000); x.Set(FP.EnvAttack, 0); x.Set(FP.EnvSustain, 1000); });
            Measure.Report($"Neo Learn FM, 2x at depth {depth / 10} %, note {note} ({DspMath.NoteToHz(note):F0} Hz), Standard: alias floor {floor:F1} dB");
        }

        [TestCase(33)]
        [TestCase(57)]
        [TestCase(94)]
        public void WavetableAliasesUnderMinus80dBUnwarpedAtStandardOrWarpedAtHigh(int note)
        {
            // The unwarped Basic table at the centre of Phase Warp (the warps at 0 read plainly), then Bend and Sync at High (4x): reported.
            double plain = Alias(Wt, note, x => { x.Set(WP.TableSelect, 0); x.Set(WP.WtPosition, 667); x.Set(WP.ScanDepth, 500); x.Set(WP.EnvAttack, 0); x.Set(WP.EnvSustain, 1000); });
            double bend = Alias(Wt, note, x => { x.Set(WP.TableSelect, 0); x.Set(WP.WtPosition, 667); x.Set(WP.PhaseWarp, 100); x.Set(WP.ScanDepth, 500); x.Set(WP.EnvAttack, 0); x.Set(WP.EnvSustain, 1000); });
            double sync = Alias(Wt, note, x => { x.Set(WP.TableSelect, 0); x.Set(WP.WtPosition, 667); x.Set(WP.PhaseWarp, 800); x.Set(WP.ScanDepth, 500); x.Set(WP.EnvAttack, 0); x.Set(WP.EnvSustain, 1000); });
            Measure.Report($"Neo Learn Wavetable, note {note} ({DspMath.NoteToHz(note):F0} Hz), Basic table, High: unwarped {plain:F1} dB, Bend 80 % {bend:F1} dB, Sync 60 % {sync:F1} dB");
            Assert.That(plain, Is.LessThan(-80));
        }

        [TestCase(33)]
        [TestCase(57)]
        [TestCase(94)]
        public void AdditiveHasNoAliasToMeasure(int note)
        {
            // Partials above the band are not rendered (the engine fades them out), so the floor is the renderer's own.
            double floor = Alias(Add, note, x => { x.Set(AP.PartialCount, 63); x.Set(AP.SpectralTilt, 1000); x.Set(AP.EnvAttack, 0); x.Set(AP.EnvSustain, 1000); });
            double shimmer = Alias(Add, note, x => { x.Set(AP.PartialCount, 63); x.Set(AP.SpectralTilt, 1000); x.Set(AP.EnvAttack, 0); x.Set(AP.EnvSustain, 1000); x.Set(AP.PartialShimmer, 1000); });
            Measure.Report($"Neo Learn Additive, note {note} ({DspMath.NoteToHz(note):F0} Hz), 64 partials: floor {floor:F1} dB, with shimmer (its partials move, so not harmonics) {shimmer:F1} dB");
            Assert.That(floor, Is.LessThan(-80));
        }

        // ------------------------------------------------------------------ Neo Learn FM

        static void Held(ParamValues x)
        {
            x.Set(FP.ModDecay, 1000);
            x.Set(FP.EnvAttack, 0);
            x.Set(FP.EnvSustain, 1000);
            x.Set(FP.VelSensitivity, 0);
        }

        [Test]
        public void AWholeRatioKeepsEverySidebandOnAHarmonicAndAFractionalOneDoesNot()
        {
            double whole = Alias(Fm, 45, x => { Held(x); x.Set(FP.ModRatio, Fm.Table[0].Raw(2.0)); x.Set(FP.FmIndex, 500); });
            double frac = Alias(Fm, 45, x => { Held(x); x.Set(FP.ModRatio, Fm.Table[0].Raw(2.3)); x.Set(FP.FmIndex, 500); });
            Measure.Report($"Neo Learn FM: non-harmonic energy at 2x {whole:F1} dB, at 2.3x {frac:F1} dB");
            Assert.That(whole, Is.LessThan(-80), "an exact ratio: every sideband is a harmonic of the note");
            Assert.That(frac, Is.GreaterThan(-40), "2.3x: clangorous, inharmonic sidebands");
        }

        [Test]
        public void ARatioOfHalfPutsSidebandsAtHalfMultiples()
        {
            var l = Note(Fm, 45, 8192 + Measure.N, x => { Held(x); x.Set(FP.ModRatio, Fm.Table[0].Raw(1.5)); x.Set(FP.FmIndex, 400); });
            var mag = Spec(l, 8192, Measure.N);
            double f0 = DspMath.NoteToHz(45);
            // Sidebands at 1 + 1.5 n: 2.5 and 4 above, and the first one below folds from -0.5 up to 0.5; nothing sits at 1.5 or 3.
            double up = Near(mag, Measure.N, f0 * 2.5, out double hz1, 0.004), folded = Near(mag, Measure.N, f0 * 0.5, out _, 0.01), none = Near(mag, Measure.N, f0 * 1.5, out _, 0.004);
            Assert.That(up, Is.GreaterThan(0), "carrier + modulator");
            Assert.That(Math.Abs(hz1 - f0 * 2.5), Is.LessThan(Measure.BinFrequency(1)), "at exactly 2.5 x the note (to the nearest bin)");
            Assert.That(Measure.Db(folded / up), Is.GreaterThan(-30), "the lower sideband folds to half the note's pitch");
            Assert.That(Measure.Db(none / up), Is.LessThan(-50), "and nothing sits on a whole multiple in between");
        }

        [Test]
        public void WithNoDepthItIsAPureSine()
        {
            var l = Note(Fm, 57, 8192 + Measure.N, x => { Held(x); x.Set(FP.FmIndex, 0); x.Set(FP.Op3Harmonic, 0); });
            double share = Fundamental(l, 8192, Measure.N, DspMath.NoteToHz(57));
            Measure.Report($"Neo Learn FM, depth 0: {100 * share:F4} % of the energy at the fundamental");
            Assert.That(share, Is.GreaterThan(0.9999));
        }

        [Test]
        public void DepthAddsSidebandsAndEnv2LetsThemDie()
        {
            // The sound starts bright (Env2 at its peak) and settles towards the body (a share of the depth).
            double Share(int at) => 0;
            var l = Note(Fm, 57, 24000, x =>
            {
                x.Set(FP.ModRatio, Fm.Table[0].Raw(3.0));
                x.Set(FP.FmIndex, 600);
                x.Set(FP.ModDecay, 300);   // about 0.07 s
                x.Set(FP.EnvAttack, 0);
                x.Set(FP.EnvSustain, 1000);
                x.Set(FP.VelSensitivity, 0);
            });
            double early = Fundamental(l, 200, 2048, DspMath.NoteToHz(57)), late = Fundamental(l, 20000, 2048, DspMath.NoteToHz(57));
            Measure.Report($"Neo Learn FM, 3x at depth 60 %, Mod Decay ~70 ms: fundamental share {100 * early:F1} % at the start, {100 * late:F1} % later");
            Assert.That(early, Is.LessThan(late - 0.1), "Env2 drives the index: bright at the start, more of the sine later");
            Assert.That(late, Is.LessThan(0.999), "the body keeps some of the depth");
        }

        [Test]
        public void VelocityMovesTheIndexByVelocityToFm()
        {
            double Brightness(float velocity, int vs)
            {
                var l = Note(Fm, 57, 20000, x =>
                {
                    x.Set(FP.ModRatio, Fm.Table[0].Raw(3.0)); x.Set(FP.FmIndex, 150); x.Set(FP.ModDecay, 1000); x.Set(FP.EnvAttack, 0); x.Set(FP.EnvSustain, 1000);
                    x.Set(FP.VelSensitivity, vs);
                }, velocity);
                return 1 - Fundamental(l, 8000, 4096, DspMath.NoteToHz(57));
            }
            double soft = Brightness(0.3f, 1000), hard = Brightness(1f, 1000);
            double softOff = Brightness(0.3f, 0), hardOff = Brightness(1f, 0);
            Measure.Report($"Neo Learn FM, sideband share: velocity 0.3 {soft:F3}, 1.0 {hard:F3} at Velocity to FM 100 %; {softOff:F3} and {hardOff:F3} at 0 %");
            Assert.That(hard, Is.GreaterThan(soft + 0.05), "hard notes bark");
            Assert.That(Math.Abs(hardOff - softOff), Is.LessThan(0.01), "at 0 % velocity does not move the index");
        }

        [Test]
        public void FeedbackTurnsTheModulatorGrittyAndKeepsItBounded()
        {
            double Share(int fb) => Fundamental(Note(Fm, 57, 20000, x => { Held(x); x.Set(FP.ModRatio, Fm.Table[0].Raw(1.0)); x.Set(FP.FmIndex, 300); x.Set(FP.Feedback, fb); }), 8000, 4096, DspMath.NoteToHz(57));
            double none = Share(0), some = Share(500), full = Share(1000);
            Measure.Report($"Neo Learn FM, fundamental share at feedback 0 / 50 / 100 %: {none:F3} / {some:F3} / {full:F3}");
            Assert.That(Math.Abs(some - none), Is.GreaterThan(0.05), "feedback changes the spectrum");
            Assert.That(full, Is.LessThan(none - 0.1), "and at the top the sound is full of partials");
            var peak = Note(Fm, 57, 20000, x => { Held(x); x.Set(FP.Feedback, 1000); x.Set(FP.FmIndex, 1000); }).Max(Math.Abs);
            Assert.That(peak, Is.LessThan(4));
        }

        [Test]
        public void MetallicEdgeIsAFastTransient()
        {
            double Early(int edge) => 1 - Fundamental(Note(Fm, 57, 12000, x => { Held(x); x.Set(FP.FmIndex, 0); x.Set(FP.ModRatio, 500); x.Set(FP.Op3Harmonic, edge); }), 100, 2048, DspMath.NoteToHz(57));
            double Late(int edge) => 1 - Fundamental(Note(Fm, 57, 24000, x => { Held(x); x.Set(FP.FmIndex, 0); x.Set(FP.ModRatio, 500); x.Set(FP.Op3Harmonic, edge); }), 20000, 2048, DspMath.NoteToHz(57));
            double e0 = Early(0), e1 = Early(1000), l1 = Late(1000);
            Measure.Report($"Neo Learn FM, sideband share at the start: edge 0 {e0:F3}, edge 100 % {e1:F3}; later at 100 %: {l1:F3}");
            Assert.That(e0, Is.LessThan(0.001));
            Assert.That(e1, Is.GreaterThan(0.05), "the third operator speaks at the start");
            Assert.That(l1, Is.LessThan(e1 * 0.5), "and dies away fast");
        }

        [Test]
        public void TheModulatorsAreNotHeardOnTheirOwn()
        {
            var (h, _) = Make(Fm);
            Assert.That(h.DyeValues.F(DyeP.BLevel), Is.EqualTo(0f));
            Assert.That(h.DyeValues.F(DyeP.CLevel), Is.EqualTo(0f));
            Assert.That(h.DyeValues.Choice(DyeP.AWarp1), Is.EqualTo(9), "A is phase-modulated ...");
            Assert.That(h.DyeValues.Choice(DyeP.AMod), Is.EqualTo(1), "... by B");
            Assert.That(h.DyeValues.Choice(DyeP.BMod), Is.EqualTo(2), "which is phase-modulated by C");
            Assert.That(h.Engine.Bank.S.Osc[1].ExactRatio, Is.EqualTo(2f), "B's exact ratio reaches the engine (the default is 2x)");
            Assert.That(h.Engine.Bank.S.Osc[2].ExactRatio, Is.EqualTo((float)FmMap.Op3Ratio));
        }

        // ------------------------------------------------------------------ Neo Learn Wavetable

        [Test]
        public void BendIsOnTheLeftOfCentreAndSyncOnTheRightAtHighQuality()
        {
            var (h, v) = Make(Wt, x => x.Set(WP.PhaseWarp, 500));
            var d = h.DyeValues;
            Assert.That(d.Choice(DyeP.Quality), Is.EqualTo(1), "High (4x)");
            Assert.That(d.Choice(DyeP.AWarp1), Is.EqualTo(1), "Bend+");
            Assert.That(d.Choice(DyeP.AWarp2), Is.EqualTo(6), "Sync");
            Assert.That(d.F(DyeP.AWarp1Amt), Is.EqualTo(0f));
            Assert.That(d.F(DyeP.AWarp2Amt), Is.EqualTo(0f), "the centre reads the table plainly");
            v.Set(WP.PhaseWarp, 100);
            Render(h, Block);
            Assert.That(d.F(DyeP.AWarp1Amt), Is.EqualTo(0.8f).Within(0.002), "left: Bend");
            Assert.That(d.F(DyeP.AWarp2Amt), Is.EqualTo(0f));
            v.Set(WP.PhaseWarp, 900);
            Render(h, Block);
            Assert.That(d.F(DyeP.AWarp1Amt), Is.EqualTo(0f));
            Assert.That(d.F(DyeP.AWarp2Amt), Is.EqualTo(0.8f).Within(0.002), "right: Sync");
        }

        [Test]
        public void SyncBrightensAndBendPinches()
        {
            double C(int warp) => Centroid(Note(Wt, 45, 20000, x => { x.Set(WP.TableSelect, 0); x.Set(WP.ScanDepth, 500); x.Set(WP.PhaseWarp, warp); x.Set(WP.EnvAttack, 0); x.Set(WP.EnvSustain, 1000); }), 6000, 8192);
            double plain = C(500), sync = C(900), bend = C(100);
            Measure.Report($"Neo Learn Wavetable, Basic table, spectral centroid: centre {plain:F0} Hz, Sync 80 % {sync:F0} Hz, Bend 80 % {bend:F0} Hz");
            Assert.That(sync, Is.GreaterThan(plain * 1.5), "hard sync adds upper harmonics");
            Assert.That(Math.Abs(bend - plain), Is.GreaterThan(plain * 0.1), "bending changes the timbre");
        }

        [Test]
        public void EveryTableSoundsAndTheLfoScansTheFrames()
        {
            for (int table = 0; table <= 9; table++)
            {
                var l = Note(Wt, 48, 9600, x => { x.Set(WP.TableSelect, table); x.Set(WP.ScanDepth, 500); });
                Assert.That(Measure.Rms(l, 4800, 4800), Is.GreaterThan(0.005), $"table {table}");
            }
            // Formants: the frames differ, so a scan changes the colour over time; with no depth it holds still.
            double Spread(int depth)
            {
                var l = Note(Wt, 48, 3 * Rate, x => { x.Set(WP.TableSelect, 3); x.Set(WP.ScanDepth, depth); x.Set(WP.ScanSpeed, 500); x.Set(WP.EnvAttack, 0); x.Set(WP.EnvSustain, 1000); x.Set(WP.WtPosition, 500); });
                var c = new List<double>();
                for (int at = 12000; at + 4096 < l.Length; at += 6000) c.Add(Centroid(l, at, 4096));
                return (c.Max() - c.Min()) / c.Average();
            }
            double still = Spread(500), scan = Spread(1000);
            Measure.Report($"Neo Learn Wavetable, Formants: centroid spread over 3 s: still {still:F3}, scanning (100 %) {scan:F3}");
            Assert.That(still, Is.LessThan(0.02));
            Assert.That(scan, Is.GreaterThan(0.1));
        }

        [Test]
        public void MotionDepthSetsTheDirectionAndTheSubIsTheRootSine()
        {
            var (h, v) = Make(Wt, x => { x.Set(WP.ScanDepth, 1000); x.Set(WP.SubAnchor, 400); });
            var d = h.DyeValues;
            Assert.That(d.Value(DyeP.R1Amt), Is.EqualTo(WavetableMap.MotionTravel).Within(0.002));
            v.Set(WP.ScanDepth, 0);
            Render(h, Block);
            Assert.That(d.Value(DyeP.R1Amt), Is.EqualTo(-WavetableMap.MotionTravel).Within(0.002), "negative depth runs the scan the other way");
            Assert.That(d.Choice(DyeP.SubShape), Is.EqualTo(0), "a sine");
            Assert.That(d.Choice(DyeP.SubOct), Is.EqualTo(0), "at the root pitch");
            Assert.That(d.F(DyeP.SubLevel), Is.EqualTo(0.4f).Within(0.002));
            Assert.That(d.Value(DyeP.L1Rate), Is.EqualTo(Wt.Table[3].Value(Wt.Table[3].Default)).Within(0.02));
        }

        [Test]
        public void AUserTableIsPlayedWhenChosen()
        {
            var cycle = new float[2048];
            for (int i = 0; i < cycle.Length; i++) cycle[i] = (float)(0.8 * Math.Sin(2 * Math.PI * 3 * i / cycle.Length));   // 3 x the note's pitch
            var (h, v) = Make(Wt, x => { x.Set(WP.TableSelect, 10); x.Set(WP.ScanDepth, 500); });
            h.Engine.NoteOn(0, 45, 1f);
            var (l, _) = Render(h, 4800);
            double basic = Fundamental(l, 2000, 2048, DspMath.NoteToHz(45));
            h.Engine.SetUserTable(0, new TableArena(NeoBuzz.Wave.WavReader.ToTable("three", cycle, 0)), "three");
            h.Engine.AllNotesOff(true);
            h.Engine.NoteOn(1, 45, 1f);
            var (l2, _) = Render(h, 9600);
            double three = Near(Spec(l2, 4800, 4096), 4096, 3 * DspMath.NoteToHz(45), out _, 0.01), one = Near(Spec(l2, 4800, 4096), 4096, DspMath.NoteToHz(45), out _, 0.01);
            Assert.That(Measure.Rms(l, 2000, 2000), Is.GreaterThan(0.005), "no user table loaded: the first table (Basic) plays");
            Assert.That(three, Is.GreaterThan(one * 20), "the user's table plays: a sine at three times the note");
        }

        // ------------------------------------------------------------------ Neo Learn Additive

        static double Partial(float[] l, int at, int n, int note, int k, double stretch = 1, double rel = 0.01)
        {
            var mag = Spec(l, at, n);
            return Near(mag, n, k * DspMath.NoteToHz(note) * stretch, out _, rel);
        }

        static void HeldAdd(ParamValues x)
        {
            x.Set(AP.PartialCount, 63);
            x.Set(AP.SpectralTilt, 800);
            x.Set(AP.EnvAttack, 0);
            x.Set(AP.EnvSustain, 1000);
        }

        [Test]
        public void OneIsASineAndTheCountBuildsTheSeries()
        {
            var one = Note(Add, 45, 8192 + Measure.N, x => { HeldAdd(x); x.Set(AP.PartialCount, 0); });
            Assert.That(Fundamental(one, 8192, Measure.N, DspMath.NoteToHz(45)), Is.GreaterThan(0.9999));
            var eight = Note(Add, 45, 8192 + Measure.N, x => { HeldAdd(x); x.Set(AP.PartialCount, 7); });
            double p8 = Partial(eight, 8192, Measure.N, 45, 8), p9 = Partial(eight, 8192, Measure.N, 45, 9), p1 = Partial(eight, 8192, Measure.N, 45, 1);
            Assert.That(Partial(eight, 8192, Measure.N, 45, 4), Is.GreaterThan(p1 * 0.1), "partial 4 sounds at a count of 8");
            Assert.That(p8, Is.GreaterThan(p1 * 0.01), "so does the top one, a little softer (the top few fade out)");
            Assert.That(p9, Is.LessThan(p1 * 0.001), "partial 9 does not");
        }

        [Test]
        public void OddEvenLeavesOddOrEvenPartials()
        {
            double Ratio(int oddEven, int k)
            {
                var l = Note(Add, 45, 8192 + Measure.N, x => { HeldAdd(x); x.Set(AP.OddEvenBalance, oddEven); x.Set(AP.PartialCount, 15); });
                return Partial(l, 8192, Measure.N, 45, k) / Math.Max(Partial(l, 8192, Measure.N, 45, k % 2 == 0 ? 3 : 2), 1e-12);
            }
            // Left (-100 %): odd partials only (the clarinet); right (+100 %): even only (octave-like, no fundamental).
            Assert.That(Measure.Db(Ratio(0, 2)), Is.LessThan(-40), "left: the even partials are gone");
            Assert.That(Measure.Db(Ratio(1000, 3)), Is.LessThan(-40), "right: the odd partials are gone");
            var right = Note(Add, 45, 8192 + Measure.N, x => { HeldAdd(x); x.Set(AP.OddEvenBalance, 1000); x.Set(AP.PartialCount, 15); });
            Assert.That(Measure.Db(Partial(right, 8192, Measure.N, 45, 1) / Partial(right, 8192, Measure.N, 45, 2)), Is.LessThan(-40), "including the fundamental: octave-like");
        }

        [Test]
        public void InharmonicityStretchesTheUpperPartialsAsAStiffString()
        {
            var l = Note(Add, 45, 8192 + Measure.N, x => { HeldAdd(x); x.Set(AP.Inharmonicity, 700); x.Set(AP.PartialCount, 23); });
            double b = AdditiveMap.StiffnessAtFull * 0.7 * 0.7, f0 = DspMath.NoteToHz(45);
            foreach (int k in new[] { 1, 5, 10, 20 })
            {
                double want = k * f0 * Math.Sqrt(1 + b * k * k);
                double level = Near(Spec(l, 8192, Measure.N), Measure.N, want, out double at, 0.03);
                Assert.That(level, Is.GreaterThan(0), $"partial {k}");
                Assert.That(Math.Abs(at - want), Is.LessThanOrEqualTo(Measure.BinFrequency(1)), $"partial {k} at {want:F1} Hz, found {at:F1}");
            }
            // Without it: whole multiples.
            var plain = Note(Add, 45, 8192 + Measure.N, x => { HeldAdd(x); x.Set(AP.PartialCount, 23); });
            Near(Spec(plain, 8192, Measure.N), Measure.N, 10 * f0, out double at10, 0.03);
            Assert.That(Math.Abs(at10 - 10 * f0), Is.LessThanOrEqualTo(Measure.BinFrequency(1)));
        }

        [Test]
        public void TheCombCarvesNotchesAcrossThePartials()
        {
            // At 100 % the period is 4 partials: partials 3, 7, 11 ... are the notches, 1, 5, 9 ... pass.
            double Db(int comb, int k, int over) =>
                Measure.Db(Partial(CombNote(comb), 8192, Measure.N, 45, k) / Math.Max(Partial(CombNote(comb), 8192, Measure.N, 45, over), 1e-12));
            float[] CombNote(int comb) => combCache.TryGetValue(comb, out var c) ? c : combCache[comb] = Note(Add, 45, 8192 + Measure.N, x => { HeldAdd(x); x.Set(AP.HarmonicComb, comb); x.Set(AP.SpectralTilt, 500); });
            double without = Db(0, 7, 5), with = Db(1000, 7, 5);
            Measure.Report($"Neo Learn Additive, partial 7 against partial 5: without the comb {without:F1} dB, at 100 % {with:F1} dB");
            Assert.That(with, Is.LessThan(without - 40), "a notch");
            Assert.That(Measure.Db(Partial(CombNote(1000), 8192, Measure.N, 45, 1) / Partial(CombNote(0), 8192, Measure.N, 45, 1)), Is.GreaterThan(-8), "the fundamental stays (loudness is levelled)");
            Assert.That(Math.Abs(AdditiveMap.CombPeriodHard - 4), Is.EqualTo(0));
        }

        static readonly Dictionary<int, float[]> combCache = new();

        [Test]
        public void ShimmerMovesTheUpperPartialsAndLeavesTheFundamental()
        {
            double Cv(int shimmer, int k)
            {
                var l = Note(Add, 45, 4 * Rate, x => { HeldAdd(x); x.Set(AP.PartialShimmer, shimmer); x.Set(AP.PartialCount, 31); });
                var v = new List<double>();
                for (int at = 12000; at + 2048 < l.Length; at += 3000)
                    v.Add(Near(Spec(l, at, 2048), 2048, k * DspMath.NoteToHz(45), out _, 0.02));
                double mean = v.Average();
                return Math.Sqrt(v.Select(x => (x - mean) * (x - mean)).Average()) / mean;
            }
            double off = Cv(0, 8), on = Cv(1000, 8), fundamental = Cv(1000, 1);
            Measure.Report($"Neo Learn Additive, level variation (std/mean) of partial 8: shimmer 0 {off:F3}, 100 % {on:F3}; fundamental at 100 %: {fundamental:F3}");
            Assert.That(off, Is.LessThan(0.02));
            Assert.That(on, Is.GreaterThan(0.15), "the upper partials drift");
            Assert.That(fundamental, Is.LessThan(on * 0.3), "the fundamental stays");
        }
    }
}

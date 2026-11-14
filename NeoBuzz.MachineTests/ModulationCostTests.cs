// The modulation-cost guard (docs/specs/machine-safety.md): the canvas modulators (Neo LFO, Envelope, Step, Macro) write a
// target parameter up to once per 64-sample sub-block, so a machine must not cost more for a parameter that moves every block
// than for one that stands still. Neo Drive once re-ran a 4096-sample auto-gain probe for ANY parameter change (Mix included):
// its block cost with Drive automated every 64 samples was ~3x static. For every Neo*, Dye Essence and Neo Learn machine this
// times a second of audio with every continuous parameter changed every 64 samples (a small deterministic random walk around
// the default, inside +-10 % of each range) against a second with them standing still, with Throughput.CompareCost (the same
// chunks of the same work, each chunk its fastest of the reps, a ratio, retried as the other guards are), and flags a ratio
// over Limit. A machine that cannot meet it yet is listed in Known with the reason, never skipped.

using System.Globalization;
using NeoBuzz.Dsp;
using ReBuzzTests.Automation;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class ModulationCostTests
    {
        const int Block = 64;
        const double Seconds = 1.5;
        public const double Limit = 1.5;

        /// <summary>
        /// A ratio over Limit is allowed while the extra stays under this share of a core (0.5 %: 6.7 µs per 64-sample block at 48 kHz):
        /// a machine that idles at 1000 times real time costs 1.3 µs a block, and a modulator that doubles that costs nothing anyone
        /// can hear or see in the meters. Neo Drive's probe cost 7.5 % of a core.
        /// </summary>
        public const double Slack = 0.005;

        /// <summary>Machines over the limit that a local fix cannot bring under it: name, the measured ratio, the reason and what would fix it.</summary>
        internal static readonly Dictionary<string, string> Known = new()
        {
            ["Neo EQ"] = "5.0x with all 136 parameters moving, 2.0x for ONE band's frequency or Q alone (+13 % of a core): a moving band glides its coefficients with a trigonometric design every few samples (EqEngine's b.Moving paths), by design, so that sweeps are smooth. A cheaper glide (a block-rate design with an interpolated state) changes the sound of sweeps and is its own task.",
            ["Neo Multiband"] = "1.9x, +1.6 % of a core, from the three crossover frequencies alone (1.9x each; every other parameter 1.06x): a crossover that moves designs the linear-phase split again, already spread over the blocks in steps (MultibandTests: the slowest block under 2x). Cheaper would be a shorter filter while moving, a change to its latency and its tests.",
        };

        /// <summary>A machine made for these values (sample rate set, notes struck for a synth): its audio call.</summary>
        delegate Action<float[], float[], int> Make(ParamValues v);

        static readonly Dictionary<string, (ParamDef[] Table, Make Make)> Machines = new()
        {
            ["Neo Analyzer"] = (NeoBuzz.Analyzer.Params.Table, v => { var e = new NeoBuzz.Analyzer.AnalyzerEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Drive"] = (NeoBuzz.Drive.Params.Table, v => { var e = new NeoBuzz.Drive.DriveEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Comp"] = (NeoBuzz.Comp.Params.Table, v => { var e = new NeoBuzz.Comp.CompEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, null, null, n); }),
            ["Neo Verb"] = (NeoBuzz.Verb.Params.Table, v => { var e = new NeoBuzz.Verb.VerbEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo EQ"] = (NeoBuzz.Eq.Params.Table, v => { var e = new NeoBuzz.Eq.EqEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Utility"] = (NeoBuzz.Utility.Params.Table, v => { var e = new NeoBuzz.Utility.UtilityEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Limiter"] = (NeoBuzz.Limiter.Params.Table, v => { var e = new NeoBuzz.Limiter.LimiterEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Ensemble"] = (NeoBuzz.Ensemble.Params.Table, v => { var e = new NeoBuzz.Ensemble.EnsembleEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Delay"] = (NeoBuzz.Delay.Params.Table, v => { var e = new NeoBuzz.Delay.DelayEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Multiband"] = (NeoBuzz.Multiband.Params.Table, v => { var e = new NeoBuzz.Multiband.MultibandEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Scope"] = (NeoBuzz.Scope.Params.Table, v => { var e = new NeoBuzz.Scope.ScopeEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n, default); }),
            ["Neo Filter"] = (NeoBuzz.Filter.Params.Table, v => { var e = new NeoBuzz.Filter.FilterEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Clipper"] = (NeoBuzz.Clipper.Params.Table, v => { var e = new NeoBuzz.Clipper.ClipperEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Transient"] = (NeoBuzz.Transient.Params.Table, v => { var e = new NeoBuzz.Transient.TransientEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Low End"] = (NeoBuzz.LowEnd.Params.Table, v => { var e = new NeoBuzz.LowEnd.LowEndEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Imager"] = (NeoBuzz.Imager.Params.Table, v => { var e = new NeoBuzz.Imager.ImagerEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Tape"] = (NeoBuzz.Tape.Params.Table, v => { var e = new NeoBuzz.Tape.TapeEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo De-esser"] = (NeoBuzz.DeEsser.Params.Table, v => { var e = new NeoBuzz.DeEsser.DeEsserEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Gate"] = (NeoBuzz.Gate.Params.Table, v => { var e = new NeoBuzz.Gate.GateEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, null, null, n); }),
            ["Neo Exciter"] = (NeoBuzz.Exciter.Params.Table, v => { var e = new NeoBuzz.Exciter.ExciterEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Dither"] = (NeoBuzz.Dither.Params.Table, v => { var e = new NeoBuzz.Dither.DitherEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Strip"] = (NeoBuzz.Strip.Params.Table, v => { var e = new NeoBuzz.Strip.StripEngine(v); e.SetSampleRate(Measure.Rate); return (l, r, n) => e.Process(l, r, n); }),
            ["Neo Poly"] = (NeoBuzz.Poly.Params.Table, v => Synth(new NeoBuzz.Poly.PolyEngine(v), (e, l, r, n) => ((NeoBuzz.Poly.PolyEngine)e).Process(l, r, n))),
            ["Neo Wave"] = (NeoBuzz.Wave.Params.Table, v =>
            {
                for (int i = 0; i < NeoBuzz.Wave.Tables.Names.Length; i++) NeoBuzz.Wave.Tables.GetNow(i);
                return Synth(new NeoBuzz.Wave.WaveEngine(v), (e, l, r, n) => ((NeoBuzz.Wave.WaveEngine)e).Process(l, r, n));
            }),
            ["Neo FM6"] = (NeoBuzz.Fm6.Params.Table, v => Synth(new NeoBuzz.Fm6.FmEngine(v), (e, l, r, n) => ((NeoBuzz.Fm6.FmEngine)e).Process(l, r, n))),
            ["Dye Essence"] = (NeoBuzz.DyeEssence.Params.Table, v =>
            {
                for (int i = 0; i < NeoBuzz.DyeEssence.TableBank.Count; i++) NeoBuzz.DyeEssence.TableBank.GetNow(i);
                for (int i = 0; i < NeoBuzz.DyeEssence.SampleSources.Count; i++) NeoBuzz.DyeEssence.SampleSources.GetNow(i);
                var e = new NeoBuzz.DyeEssence.DyeEngine(v);
                e.Fx.BuildNow();
                return Synth(e, (s, l, r, n) => ((NeoBuzz.DyeEssence.DyeEngine)s).Process(l, r, n));
            }),
            ["Neo Sampler"] = (NeoBuzz.Sampler.Params.Table, v =>
            {
                NeoBuzz.Sampler.BuiltIns.BuildNow();
                var bank = new NeoBuzz.Sampler.SampleBank();
                for (int k = 1; k <= 16; k++) bank.Set(k, NeoBuzz.Sampler.BuiltIns.Get(k));
                return Synth(new NeoBuzz.Sampler.SamplerEngine(v, bank), (e, l, r, n) => ((NeoBuzz.Sampler.SamplerEngine)e).Process(l, r, n));
            }),
            ["Neo Drums"] = (NeoBuzz.Drums.Params.Table, v => Synth(new NeoBuzz.Drums.DrumsEngine(v), (e, l, r, n) => ((NeoBuzz.Drums.DrumsEngine)e).Process(l, r, n), new[] { 36, 38, 42 })),
            ["Neo Learn FM"] = (NeoBuzz.LearnFm.Params.Table, v => Learn(new NeoBuzz.Learn.FmMap(NeoBuzz.LearnFm.Params.Table), v)),
            ["Neo Learn Wavetable"] = (NeoBuzz.LearnWt.Params.Table, v => Learn(new NeoBuzz.Learn.WavetableMap(NeoBuzz.LearnWt.Params.Table), v)),
            ["Neo Learn Additive"] = (NeoBuzz.LearnAdd.Params.Table, v => Learn(new NeoBuzz.Learn.AdditiveMap(NeoBuzz.LearnAdd.Params.Table), v)),
            ["Neo Learn Granular"] = (NeoBuzz.LearnGrain.Params.Table, v => Learn(new NeoBuzz.Learn.GranularMap(NeoBuzz.LearnGrain.Params.Table), v)),
            ["Neo Learn Physical"] = (NeoBuzz.LearnPhys.Params.Table, v => Learn(new NeoBuzz.Learn.PhysicalMap(NeoBuzz.LearnPhys.Params.Table), v)),
            ["Neo Learn String"] = (NeoBuzz.LearnString.Params.Table, v => Learn(new NeoBuzz.Learn.StringMap(NeoBuzz.LearnString.Params.Table), v)),
            ["Neo Learn Formant"] = (NeoBuzz.LearnFormant.Params.Table, v => Learn(new NeoBuzz.Learn.FormantMap(NeoBuzz.LearnFormant.Params.Table), v)),
            ["Neo Learn Wavefolder"] = (NeoBuzz.LearnFold.Params.Table, v => Learn(new NeoBuzz.Learn.WavefolderMap(NeoBuzz.LearnFold.Params.Table), v)),
            ["Neo Learn Phase Distortion"] = (NeoBuzz.LearnPd.Params.Table, v => Learn(new NeoBuzz.Learn.PhaseDistortionMap(NeoBuzz.LearnPd.Params.Table), v)),
            ["Neo Learn Vector"] = (NeoBuzz.LearnVector.Params.Table, v => Learn(new NeoBuzz.Learn.VectorMap(NeoBuzz.LearnVector.Params.Table), v)),
            ["Neo Learn Sampler"] = (NeoBuzz.LearnSampler.Params.Table, v => Learn(new NeoBuzz.Learn.SamplerMap(NeoBuzz.LearnSampler.Params.Table), v)),
            ["Neo Learn Subtractive"] = (NeoBuzz.LearnSub.Params.Table, v => Learn(new NeoBuzz.Learn.SubtractiveMap(NeoBuzz.LearnSub.Params.Table), v)),
        };

        static Action<float[], float[], int> Learn(NeoBuzz.Learn.MacroMap map, ParamValues v)
        {
            for (int i = 0; i < NeoBuzz.DyeEssence.TableBank.Count; i++) NeoBuzz.DyeEssence.TableBank.GetNow(i);
            var host = new NeoBuzz.Learn.LearnHost(map, v);
            return Synth(host.Engine, (e, l, r, n) => host.Process(l, r, n));
        }

        /// <summary>A chord held, struck again every 0.25 s so that a decaying voice (a drum) keeps sounding.</summary>
        static Action<float[], float[], int> Synth(INoteSink engine, Func<INoteSink, float[], float[], int, bool> process, int[] notes = null)
        {
            ((dynamic)engine).SetSampleRate((float)Measure.Rate);
            notes ??= new[] { 48, 52, 55, 59 };
            int at = 0;
            const int Again = Measure.Rate / 4;
            return (l, r, n) =>
            {
                if (at % Again < n)
                    for (int k = 0; k < notes.Length; k++) { engine.NoteOff(k); engine.NoteOn(k, notes[k], 0.8f); }
                at += n;
                process(engine, l, r, n);
            };
        }

        /// <summary>
        /// Every continuous parameter's walk: a step of up to 2 % of its range each block, kept within 10 % of its range around
        /// its default (inside what the machines are built for); the same walk every time. A machine standing still stands at the centres.
        /// </summary>
        // Continuous: not a choice, a switch or an integer (a wave slot, a semitone, a step count: stepping them selects something else).
        static bool Walked(ParamDef d) => d.Kind != MapKind.Enum && d.Kind != MapKind.Switch && d.Kind != MapKind.Int && d.Names == null && d.Max - d.Min >= 8;

        sealed class Walk
        {
            readonly ParamValues v;
            readonly int[] index, lo, hi, step, centre;
            readonly int only;
            uint state = 0x9E3779B9;

            /// <param name="only">A parameter to move alone (the others stand at their centres); -1: all move.</param>
            public Walk(ParamValues v, int only = -1)
            {
                this.v = v;
                this.only = only;
                var ix = new List<int>();
                foreach (var d in v.Table)
                    if (Walked(d)) ix.Add(d.Index);
                index = ix.ToArray();
                lo = new int[index.Length]; hi = new int[index.Length]; step = new int[index.Length]; centre = new int[index.Length];
                for (int i = 0; i < index.Length; i++)
                {
                    var d = v.Table[index[i]];
                    int range = d.Max - d.Min;
                    // Around the default, but never reaching the ends of the range: a parameter whose default is its minimum is
                    // often a feature's "off" (Shimmer 0), and moving into "on" would measure the feature, not the moving.
                    centre[i] = Math.Clamp(d.Default, d.Min + range * 15 / 100, d.Max - range * 15 / 100);
                    lo[i] = centre[i] - range / 10;
                    hi[i] = centre[i] + range / 10;
                    step[i] = Math.Max(1, range / 50);
                }
                Park();
            }

            public int Count => index.Length;

            /// <summary>Every walked parameter at its centre: where a machine standing still stands, and where the walk starts.</summary>
            void Park()
            {
                for (int i = 0; i < index.Length; i++) v.Set(index[i], centre[i]);
            }

            /// <summary>One block's step. A machine standing still gets the same loop (the same arithmetic, the same Set calls) writing the values it already has, so the walk's own cost is in both.</summary>
            public void Next(bool moving)
            {
                for (int i = 0; i < index.Length; i++)
                {
                    if (only >= 0 && index[i] != only) continue;
                    state = state * 1664525u + 1013904223u;
                    int s = (int)((state >> 16) % (uint)(2 * step[i] + 1)) - step[i];
                    int x = Math.Clamp(v[index[i]] + s, lo[i], hi[i]);
                    v.Set(index[i], moving ? x : v[index[i]]);
                }
            }
        }

        static Func<Func<int, Action<int>>> Start(ParamDef[] table, Make make, float[] x, bool moving, int only = -1) => () =>
        {
            var v = new ParamValues(table);
            var process = make(v);
            var walk = new Walk(v, only);          // parks every walked parameter at its centre, moving or not
            var bl = new float[Block];
            var br = new float[Block];
            void One(int pos, int n)
            {
                walk.Next(moving);
                Array.Copy(x, pos % (x.Length - Block), bl, 0, n);
                Array.Copy(bl, br, n);
                process(bl, br, n);
            }
            // Untimed warm-up (the JIT, the first voices) with the same work as the timed blocks.
            for (int pos = 0; pos < Measure.Rate / 4; pos += Block) One(pos, Block);
            return Throughput.InBlocks(Block, One);
        };

        static float[] Noise()
        {
            var x = new float[(int)(Seconds * Measure.Rate)];
            uint s = 12345;
            for (int i = 0; i < x.Length; i++) { s = s * 1664525u + 1013904223u; x[i] = ((s >> 8) / (float)(1 << 24) * 2 - 1) * 0.25f; }
            return x;
        }

        // Local diagnosis: NEOBUZZ_MODCOST_ONLY="Neo Drive;Neo EQ" measures just those; NEOBUZZ_MODCOST_DETAIL (the same list) also
        // times each parameter moving alone for a machine over the limit, and prints the five dearest.
        static string Only => Environment.GetEnvironmentVariable("NEOBUZZ_MODCOST_ONLY");
        static bool Detail(string name) => Environment.GetEnvironmentVariable("NEOBUZZ_MODCOST_DETAIL") is { Length: > 0 } d && d.Split(';').Contains(name);

        [Test]
        public void EveryNeoMachineIsMeasured()
        {
            Assert.That(PresetTests.Machines.Keys.Except(Machines.Keys), Is.Empty, "a Neo machine the preset tests know that this guard doesn't");
            Assert.That(Known.Keys.Except(Machines.Keys), Is.Empty, "a known exception for a machine that doesn't exist");
        }

        [Test]
        public void ParametersMovingEveryBlockCostLittleMoreThanStandingStill()
        {
            var x = Noise();
            var lines = new List<string>();
            var over = new List<string>();
            var fixedNow = new List<string>();
            foreach (var (name, (table, make)) in Machines)
            {
                if (Only is { Length: > 0 } && !Only.Split(';').Contains(name)) continue;
                var cost = GuardPolicy.Retry(() => Throughput.CompareCost(name, Measure.Rate, Block, Seconds,
                    Start(table, make, x, false), Seconds, Start(table, make, x, true)), c => c.Ratio <= Limit || 1 / c.TestRealTime - 1 / c.BaseRealTime <= Slack, out _);
                string line = string.Format(CultureInfo.InvariantCulture, "{0}: {1:F2}x ({2} parameters moving; {3:F1}x real time, still {4:F1}x)",
                    name, cost.Ratio, new Walk(new ParamValues(table)).Count, cost.TestRealTime, cost.BaseRealTime);
                bool known = Known.TryGetValue(name, out var why);
                if (known) line += " [known: " + why + "]";
                if (cost.Ratio > Limit && Detail(name))
                    line += " | alone: " + string.Join(", ", table.Where(Walked)
                        .Select(d => (d.Id, Throughput.CompareCost(name, Measure.Rate, Block, Seconds / 3, Start(table, make, x, false), Seconds / 3, Start(table, make, x, true, d.Index)).Ratio))
                        .OrderByDescending(t => t.Ratio).Take(5).Select(t => string.Format(CultureInfo.InvariantCulture, "{0} {1:F2}x", t.Id, t.Ratio)));
                double extra = 1 / cost.TestRealTime - 1 / cost.BaseRealTime;      // a share of a core
                if (cost.Ratio > Limit) line += string.Format(CultureInfo.InvariantCulture, " +{0:F1} µs/block{1}", extra * Block / Measure.Rate * 1e6, extra <= Slack ? " (under the slack)" : "");
                lines.Add(line);
                if (cost.Ratio > Limit && extra > Slack && !known) over.Add(line);
                else if (cost.Ratio <= Limit && known) fixedNow.Add(name);   // reported, not asserted: a ratio near the limit is noise
            }
            Measure.Report("Modulation cost (every continuous parameter changed every 64 samples, against standing still):\n" + string.Join("\n", lines));
            Assert.That(over, Is.Empty, "block cost with parameters moving every 64 samples is over " + Limit + "x standing still and more than " + Slack * 100 + " % of a core above it (fix the machine, or list it in Known with the reason)");
            if (fixedNow.Count > 0) Measure.Report("Listed in Known, under the limit this time: " + string.Join(", ", fixedNow));
        }
    }
}

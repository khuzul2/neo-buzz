using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using ReBuzzTests.Automation;
using DyeP = NeoBuzz.DyeEssence.P;
using LP = NeoBuzz.LearnSub.P;
using LParams = NeoBuzz.LearnSub.Params;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Learn (docs/specs/neo-learn.md §4, §5.5), L0: the host and the macro maps, and Neo Learn Subtractive, the pilot.
    /// The host runs Dye Essence's engine from the machine's 12 controls: the map's ranges and continuity, the output gain
    /// and clip light, the readings the agent reads, no allocation, finite output, aliasing, pinned renders and CPU.
    /// </summary>
    [TestFixture]
    public class LearnTests
    {
        const int Rate = Measure.Rate;
        const int Block = DyeEngine.MaxBlock;

        static (LearnHost Host, ParamValues Values) Make(Action<ParamValues> tweak = null)
        {
            TableBank.GetNow(0);
            TableBank.GetNow(1);
            var v = new ParamValues(LParams.Table);
            tweak?.Invoke(v);
            var h = new LearnHost(new SubtractiveMap(LParams.Table), v);
            h.SetSampleRate(Rate);
            return (h, v);
        }

        static (float[] L, float[] R) Render(LearnHost h, int frames, Action<int> perBlock = null)
        {
            var l = new float[frames];
            var r = new float[frames];
            var bl = new float[DyeEngine.MaxBlock];
            var br = new float[DyeEngine.MaxBlock];
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

        // ------------------------------------------------------------------ the table and the machine's controls

        [Test]
        public void TheTwelveControlsAreInSpecOrderWithTheirExplainText()
        {
            string[] ids = { "filter_cutoff", "filter_resonance", "wave_morph", "sub_mix", "filter_env_amt", "analog_drive",
                             "env_attack", "env_decay", "env_sustain", "env_release", "unison_detune", "output_gain" };
            Assert.That(LParams.Table.Select(d => d.Id), Is.EqualTo(ids));
            foreach (var d in LParams.Table)
            {
                Assert.That(d.How, Is.Not.Empty, d.Id);
                Assert.That(d.Sound, Is.Not.Empty, d.Id);
                Assert.That(d.Description, Is.EqualTo(d.How), d.Id + ": the Buzz description is the How sentence");
                Assert.That(d.Max - d.Min, Is.GreaterThanOrEqualTo(1000), d.Id + ": at least 1000 raw steps");
            }
            var t = LParams.Table;
            Assert.That(t[0].Value(t[0].Min), Is.EqualTo(20).Within(1e-9));
            Assert.That(t[0].Value(t[0].Max), Is.EqualTo(20000).Within(1e-6));
            Assert.That(t[4].Value(t[4].Min), Is.EqualTo(-1));
            Assert.That(t[4].Value(t[4].Max), Is.EqualTo(1));
            Assert.That(t[6].Value(t[6].Min), Is.EqualTo(0.0005).Within(1e-12));
            Assert.That(t[6].Value(t[6].Max), Is.EqualTo(5).Within(1e-9));
            Assert.That(t[7].Value(t[7].Min), Is.EqualTo(0.005).Within(1e-12));
            Assert.That(t[7].Value(t[7].Max), Is.EqualTo(8).Within(1e-9));
            Assert.That(t[9].Value(t[9].Min), Is.EqualTo(0.005).Within(1e-12));
            Assert.That(t[9].Value(t[9].Max), Is.EqualTo(10).Within(1e-9));
            Assert.That(t[11].Value(t[11].Min), Is.EqualTo(-36));
            Assert.That(t[11].Value(t[11].Max), Is.EqualTo(12));
            Assert.That(t[11].Value(t[11].Default), Is.EqualTo(0).Within(1e-9), "Output defaults to 0 dB");
        }

        // ------------------------------------------------------------------ the host

        [Test]
        public void TheHostPlaysTheInitSoundTheSameEveryTime()
        {
            ulong first = 0;
            for (int run = 0; run < 2; run++)
            {
                var (h, _) = Make();
                Chord(h, 48, 55, 60);
                var (l, r) = Render(h, Rate);
                Assert.That(Measure.Rms(l, 0, l.Length), Is.GreaterThan(0.01), "it sounds");
                ulong x = Hash(l) ^ (Hash(r) * 31);
                if (run == 0) first = x;
                else Assert.That(x, Is.EqualTo(first), "the base patch is code: the same bits");
            }
        }

        [Test]
        public void TheBasePatchAndTheMapReachDye()
        {
            var (h, v) = Make(x =>
            {
                x.Set(LP.FilterCutoff, 400);
                x.Set(LP.WaveMorph, 0);
            });
            var d = h.DyeValues;
            Assert.That(d.Choice(DyeP.AType), Is.EqualTo(2), "slot A is the Analog engine");
            Assert.That(d.Choice(DyeP.F1Type), Is.EqualTo(1), "filter 1 is the 24 dB/oct ladder");
            Assert.That(d.Choice(DyeP.F2Type), Is.EqualTo(0), "filter 2 is off");
            Assert.That(d.Choice(DyeP.SubShape), Is.EqualTo(3), "the sub is a square");
            Assert.That(d.Choice(DyeP.SubOct), Is.EqualTo(1), "one octave down");
            Assert.That(d.F(DyeP.ALevel), Is.EqualTo(1f).Within(1e-3), "wave 0: the sine alone");
            Assert.That(d.F(DyeP.BLevel), Is.EqualTo(0f));
            Assert.That(d.F(DyeP.CLevel), Is.EqualTo(0f));
            Assert.That(d.F(DyeP.F1Cutoff), Is.EqualTo(LParams.Table[0].Value(400)).Within(LParams.Table[0].Value(400) * 0.01));
            // A control that moves later reaches Dye at the next block.
            v.Set(LP.FilterCutoff, 900);
            v.Set(LP.WaveMorph, 1000);
            Render(h, Block);
            Assert.That(d.Choice(DyeP.AShape), Is.EqualTo(4), "wave 100%: slot A plays the pulse");
            Assert.That(d.F(DyeP.ALevel), Is.EqualTo(1f).Within(1e-3));
            Assert.That(d.F(DyeP.F1Cutoff), Is.EqualTo(LParams.Table[0].Value(900)).Within(LParams.Table[0].Value(900) * 0.01));
        }

        [Test]
        public void TheGlobalControlsDriveTheEnvelopesAndTheUnison()
        {
            var (h, v) = Make(x =>
            {
                x.Set(LP.EnvAttack, 500);
                x.Set(LP.EnvSustain, 300);
                x.Set(LP.UnisonDetune, 600);
            });
            var d = h.DyeValues;
            double attack = LParams.Table[6].Value(500);
            Assert.That(d.Value(DyeP.AmpA), Is.EqualTo(attack).Within(attack * 0.01));
            Assert.That(d.Value(DyeP.FltA), Is.EqualTo(attack).Within(attack * 0.01), "the filter envelope follows the one ADSR");
            Assert.That(d.Value(DyeP.AmpS), Is.EqualTo(0.3).Within(0.002));
            Assert.That(d.Value(DyeP.FltS), Is.EqualTo(0.3).Within(0.002));
            Assert.That(d.Value(DyeP.AUnison), Is.EqualTo(5), "5 voices a note above 0%");
            Assert.That(d.Value(DyeP.ADetune), Is.EqualTo(30).Within(0.5), "60% is ±30 cents");
            Assert.That(d.Value(DyeP.ASpread), Is.EqualTo(0.6).Within(0.002));
            v.Set(LP.UnisonDetune, 0);
            Render(h, Block);
            Assert.That(d.Value(DyeP.AUnison), Is.EqualTo(1), "0%: one voice a note");
        }

        [Test]
        public void OutputIsAGainAfterSynthesisWithAClipLight()
        {
            double Level(int raw, out bool clip)
            {
                var (h, v) = Make(x => x.Set(LP.OutputGain, raw));
                Chord(h, 36, 43, 48, 52, 55, 60, 64);
                var (l, _) = Render(h, Rate / 2);
                clip = h.Clipping;
                return Measure.Rms(l, Rate / 4, Rate / 4);
            }
            double zero = Level(750, out bool clip0);
            double plus12 = Level(1000, out bool clip12);
            double minus36 = Level(0, out bool clipLow);
            Assert.That(Measure.Db(plus12 / zero), Is.EqualTo(12).Within(0.05));
            Assert.That(Measure.Db(minus36 / zero), Is.EqualTo(-36).Within(0.05));
            Assert.That(clipLow, Is.False);
            TestContext.Out.WriteLine($"a 7-note chord: 0 dB clip {clip0}, +12 dB clip {clip12}");
            Assert.That(clip12, Is.True, "+12 dB on a loud chord passes 0 dBFS: the light is on");
        }

        [Test]
        public void OutputRampsWithoutSteps()
        {
            var (h, v) = Make();
            Chord(h, 48);
            Render(h, 4800);
            var l = new float[DyeEngine.MaxBlock];
            var r = new float[DyeEngine.MaxBlock];
            v.Set(LP.OutputGain, 0);
            // A 36 dB step in one control change: the gain moves across the block, so no sample-to-sample jump is a step.
            var (a, _) = Render(h, 2 * Block);
            double worst = 0;
            for (int i = Block - 10; i < Block + 10; i++) worst = Math.Max(worst, Math.Abs(a[i + 1] - a[i]));
            double normal = 0;
            for (int i = 10; i < 100; i++) normal = Math.Max(normal, Math.Abs(a[i + 1] - a[i]));
            Assert.That(worst, Is.LessThan(Math.Max(normal * 2, 0.05)));
        }

        [Test]
        public void ReadingsReportTheOutputAndTheVoices()
        {
            var (h, _) = Make();
            var silent = h.Readings();
            Assert.That(silent["active_voices"], Is.EqualTo(0));
            Assert.That(double.IsNegativeInfinity(silent["rms_db"]), Is.True);
            Chord(h, 48, 55, 60);
            Render(h, Rate);
            var r = h.Readings();
            TestContext.Out.WriteLine($"rms {r["rms_db"]:F1} dB, peak {r["peak_db"]:F1} dB, voices {r["active_voices"]}");
            Assert.That(r["active_voices"], Is.EqualTo(3));
            Assert.That(r["rms_db"], Is.InRange(-40, 0));
            Assert.That(r["peak_db"], Is.GreaterThan(r["rms_db"]));
            Assert.That(h.ActiveVoices, Is.EqualTo(3));
            Assert.That(NeoBuzz.LearnSub.Params.Table.Length, Is.EqualTo(12));
        }

        // ------------------------------------------------------------------ the macro map

        /// <summary>Dye's controls the map writes, by index, over a sweep of one learn control.</summary>
        sealed class Recorder : IDyeSink
        {
            public readonly Dictionary<int, double> Values = new();
            public void Set(int dyeParam, double value) => Values[dyeParam] = value;
        }

        static double[] Controls(int[] raw)
        {
            var c = new double[MacroMap.Count];
            for (int i = 0; i < c.Length; i++) c[i] = LParams.Table[i].Value(raw[i]);
            return c;
        }

        static int[] Defaults() => LParams.Table.Select(d => d.Default).ToArray();

        /// <summary>The Dye controls the map may move in steps (an enumeration or a count), with why.</summary>
        static readonly Dictionary<int, string> Detents = new()
        {
            [DyeP.AShape] = "slot A swaps its sine for the pulse at the middle of the morph, where its level is 0",
            [DyeP.AUnison] = "Unison / Spread: one voice at 0%, 5 above",
            [DyeP.BUnison] = "Unison / Spread: one voice at 0%, 5 above",
            [DyeP.CUnison] = "Unison / Spread: one voice at 0%, 5 above",
        };

        [Test]
        public void EveryControlMapsInRangeAndContinuously()
        {
            var map = new SubtractiveMap(LParams.Table);
            var dye = NeoBuzz.DyeEssence.Params.Table;
            for (int control = 0; control < MacroMap.Count; control++)
            {
                var d = LParams.Table[control];
                Dictionary<int, double> prev = null;
                int[] raw = Defaults();
                for (int x = d.Min; x <= d.Max; x++)
                {
                    raw[control] = x;
                    var rec = new Recorder();
                    map.Map(Controls(raw), rec);
                    foreach (var (index, value) in rec.Values)
                    {
                        Assert.That(double.IsFinite(value), Is.True, $"{d.Id}={x}: {dye[index].Id}");
                        int dr = dye[index].Raw(value);
                        Assert.That(dr, Is.InRange(dye[index].Min, dye[index].Max));
                        if (prev == null || !prev.TryGetValue(index, out double before)) continue;
                        var def = dye[index];
                        // The step in the control's own spread (a power curve such as detune is judged in its units, not its raw position).
                        double step = def.Kind == MapKind.Power
                            ? Math.Abs(def.Value(def.Raw(value)) - def.Value(def.Raw(before))) / Math.Abs(def.B - def.A)
                            : Math.Abs(def.Normalized(def.Raw(value)) - def.Normalized(def.Raw(before)));
                        if (def.Kind is MapKind.Enum or MapKind.Int or MapKind.Switch)
                        {
                            if (step > 0)
                                Assert.That(Detents.ContainsKey(index), Is.True, $"{d.Id} {x}: {def.Id} steps without a documented detent");
                        }
                        else
                            Assert.That(step, Is.LessThanOrEqualTo(0.012), $"{d.Id} {x}: {def.Id} jumps by {step:F4} of its range");
                    }
                    prev = rec.Values;
                }
            }
        }

        [Test]
        public void SlotAChangesShapeOnlyWhileItIsSilent()
        {
            var map = new SubtractiveMap(LParams.Table);
            double lastShape = -1;
            var raw = Defaults();
            for (int x = 0; x <= 1000; x++)
            {
                raw[2] = x;
                var rec = new Recorder();
                map.Map(Controls(raw), rec);
                double shape = rec.Values[DyeP.AShape];
                if (lastShape >= 0 && shape != lastShape)
                    Assert.That(rec.Values[DyeP.ALevel], Is.EqualTo(0).Within(1e-9), $"at {x}");
                lastShape = shape;
            }
        }

        [Test]
        public void TheWaveWeightsCrossfadeNeighboursAndSumToOne()
        {
            for (int x = 0; x <= 1000; x++)
            {
                SubtractiveMap.Weights(x / 1000.0, out double a, out double b, out double c, out double d);
                Assert.That(a + b + c + d, Is.EqualTo(1).Within(1e-12));
                int nonzero = (a > 0 ? 1 : 0) + (b > 0 ? 1 : 0) + (c > 0 ? 1 : 0) + (d > 0 ? 1 : 0);
                Assert.That(nonzero, Is.LessThanOrEqualTo(2), "two neighbours at most");
            }
            SubtractiveMap.Weights(0, out double s0, out _, out _, out _);
            SubtractiveMap.Weights(1.0 / 3, out _, out double t1, out _, out _);
            SubtractiveMap.Weights(2.0 / 3, out _, out _, out double w2, out _);
            SubtractiveMap.Weights(1, out _, out _, out _, out double p3);
            Assert.That(new[] { s0, t1, w2, p3 }, Is.All.EqualTo(1).Within(1e-12), "sine 0%, triangle 33%, saw 66%, pulse 100%");
        }

        [Test]
        public void TheMorphIsContinuousInTheRender()
        {
            // Sweep the morph across all four waves on a held note: no click at any step (a jump in the output would show as a
            // spike in the difference signal far above the signal's own slope).
            var (h, v) = Make(x => x.Set(LP.FilterCutoff, 1000));
            Chord(h, 45);
            Render(h, 9600);
            var (y, _) = Render(h, 1000 * Block / 4, b => v.Set(LP.WaveMorph, Math.Min(1000, b * 4)));
            double rms = Measure.Rms(y, 0, y.Length);
            double worst = 0;
            for (int i = 1; i < y.Length; i++) worst = Math.Max(worst, Math.Abs(y[i] - y[i - 1]));
            TestContext.Out.WriteLine($"morph sweep: rms {rms:F3}, biggest step {worst:F3}");
            Assert.That(worst, Is.LessThan(0.6), "no click");
        }

        [Test]
        public void EnablingUnisonDoesNotJumpInLevel()
        {
            double Level(int raw)
            {
                var (h, _) = Make(x => x.Set(LP.UnisonDetune, raw));
                Chord(h, 41, 48, 53);
                var (l, _) = Render(h, Rate);
                return Measure.Rms(l, Rate / 2, Rate / 2);
            }
            double off = Level(0), small = Level(10), full = Level(1000);
            TestContext.Out.WriteLine($"unison off {Measure.Db(off):F1} dB, 1% {Measure.Db(small):F1} dB, 100% {Measure.Db(full):F1} dB");
            Assert.That(Measure.Db(small / off), Is.InRange(-3, 3), "the voices add in power");
            Assert.That(Measure.Db(full / off), Is.InRange(-4, 4));
        }

        // ------------------------------------------------------------------ pinned renders (the null test's cases, in the unit tests)

        /// <summary>Four settings that take the machine's distinct paths: the base patch, the filter envelope, unison and sub, the wave morph.</summary>
        static (float[] L, float[] R) Pinned(string patch)
        {
            Action<ParamValues> set = patch switch
            {
                "init" => null,
                "pluck-bass" => x =>
                {
                    x.Set(LP.WaveMorph, 667); x.Set(LP.SubMix, 600); x.Set(LP.FilterCutoff, 380); x.Set(LP.FilterResonance, 420);
                    x.Set(LP.FilterEnvAmt, 900); x.Set(LP.EnvDecay, 450); x.Set(LP.EnvSustain, 100); x.Set(LP.AnalogDrive, 400);
                },
                "wide-pad" => x =>
                {
                    x.Set(LP.WaveMorph, 1000); x.Set(LP.UnisonDetune, 700); x.Set(LP.EnvAttack, 600); x.Set(LP.EnvRelease, 700);
                    x.Set(LP.FilterCutoff, 800); x.Set(LP.FilterEnvAmt, 500);
                },
                _ => x =>   // "morph-lead"
                {
                    x.Set(LP.WaveMorph, 480); x.Set(LP.FilterCutoff, 900); x.Set(LP.FilterResonance, 700); x.Set(LP.AnalogDrive, 800);
                    x.Set(LP.UnisonDetune, 150);
                },
            };
            var (h, v) = Make(set);
            h.Engine.NoteOn(0, 45, 0.9f);
            h.Engine.NoteOn(1, 52, 0.7f);
            h.Engine.NoteOn(2, 57, 0.8f);
            return Render(h, Rate * 3 / 2, b =>
            {
                if (b == 60) h.Engine.NoteOff(1);
                if (b == 150) { h.Engine.NoteOff(0); v.Set(LP.FilterCutoff, v[LP.FilterCutoff] / 2); }
            });
        }

        [TestCase("init", 0xC70D0FC755E63420UL)]
        [TestCase("pluck-bass", 0xD8E4AA4CBF84367AUL)]
        [TestCase("wide-pad", 0x3EFFBCABDE8BADE6UL)]
        [TestCase("morph-lead", 0xDBF504FDCF73E9B9UL)]
        public void PinnedRendersAreTheSameOnEveryPlatform(string patch, ulong pinned)
        {
            var (l, r) = Pinned(patch);
            ulong h = Hash(l) * 1099511628211UL ^ Hash(r);
            TestContext.Out.WriteLine($"{patch}: 0x{h:X16}");
            Assert.That(h, Is.EqualTo(pinned), $"{patch} on {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        }

        // ------------------------------------------------------------------ safety

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(17);
            var (h, v) = Make();
            for (int round = 0; round < 40; round++)
            {
                foreach (var d in LParams.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                v.Set(LP.OutputGain, 750);
                for (int k = 0; k < 6; k++) h.Engine.NoteOn(k, 24 + rng.Next(90), (float)rng.NextDouble());
                var (l, r) = Render(h, 9600);
                foreach (var s in l.Concat(r))
                {
                    Assert.That(float.IsFinite(s), Is.True, $"round {round}");
                    Assert.That(Math.Abs(s), Is.LessThan(30), $"round {round}");
                }
                h.Engine.AllNotesOff(true);
            }
        }

        [Test]
        public void ARingingFilterLeavesNoDenormals()
        {
            var (h, _) = Make(x =>
            {
                x.Set(LP.FilterResonance, 1000);
                x.Set(LP.FilterCutoff, 300);
                x.Set(LP.EnvRelease, 300);
            });
            Chord(h, 36, 43);
            Render(h, 9600);
            h.Engine.AllNotesOff(false);
            var (l, r) = Render(h, 6 * Rate);
            int tiny = 0;
            foreach (var s in l.Concat(r))
                if (s != 0 && Math.Abs(s) < 1.2e-38f) tiny++;
            Assert.That(tiny, Is.EqualTo(0), "no denormal in the output, through the tail");
        }

        [Test]
        public void AudioPathAllocatesNothing()
        {
            var (h, v) = Make(x => x.Set(LP.UnisonDetune, 500));
            var bl = new float[DyeEngine.MaxBlock];
            var br = new float[DyeEngine.MaxBlock];
            for (int k = 0; k < 8; k++) h.Engine.NoteOn(k, 40 + 3 * k, 0.9f);
            for (int i = 0; i < 200; i++) h.Process(bl, br, Block);   // warm up
            // 2000 blocks with notes coming and going and controls moving (the agent's Readings builds a dictionary on its own thread: not measured).
            var (processBytes, processWhy) = AllocationCheck.Run(2000, b =>
            {
                if (b % 50 == 0) v.Set(LP.FilterCutoff, 300 + b % 600);
                if (b % 70 == 0) v.Set(LP.WaveMorph, b % 1000);
                if (b % 90 == 0) v.Set(LP.UnisonDetune, b % 2 * 700);
                if (b % 400 == 0)
                {
                    h.Engine.NoteOff(b / 400 % 8);
                    h.Engine.NoteOn(b / 400 % 8, 36 + b / 40 % 30, 0.7f);
                }
                h.Process(bl, br, Block);
            });
            Assert.That(processBytes, Is.EqualTo(0), processWhy);
        }

        // ------------------------------------------------------------------ quality

        [TestCase(33)]
        [TestCase(57)]
        [TestCase(94)]
        public void SmoothPathsAliasUnderMinus80dB(int note)
        {
            // The saw (66%) with the filter wide open, at Standard (2×): everything that is not a harmonic, 30 Hz – 20 kHz.
            var (h, _) = Make(x =>
            {
                x.Set(LP.FilterCutoff, 1000);
                x.Set(LP.FilterResonance, 0);
                x.Set(LP.AnalogDrive, 0);
                x.Set(LP.FilterEnvAmt, 500);
                x.Set(LP.WaveMorph, (int)Math.Round(1000 * 2 / 3.0));
                x.Set(LP.EnvAttack, 0);
                x.Set(LP.EnvSustain, 1000);
            });
            h.Engine.NoteOn(0, note, 1f);
            var (l, _) = Render(h, 8192 + Measure.N);
            double floor = PolyTests.AliasFloorWindowed(l, 8192, Measure.N, DspMath.NoteToHz(note));
            Measure.Report($"Neo Learn Subtractive, saw at note {note} ({DspMath.NoteToHz(note):F0} Hz), Standard: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(-80));
        }

        [Test]
        public void SixteenNotesOnTheDefaultPatchRunAtLeastFourTimesRealTime()
        {
            Throughput.Guard("Neo Learn Subtractive, 16 notes, default patch", Throughput.Floor(x64: 4, arm64: 4), 2, Rate, DyeEngine.MaxBlock, () =>
            {
                var (h, _) = Make();
                for (int k = 0; k < 16; k++) h.Engine.NoteOn(k, 36 + 3 * k, 1f);
                var l = new float[DyeEngine.MaxBlock];
                var r = new float[DyeEngine.MaxBlock];
                for (int i = 0; i < 20; i++) h.Process(l, r, DyeEngine.MaxBlock);
                return Throughput.InBlocks(DyeEngine.MaxBlock, (pos, n) => h.Process(l, r, n));
            }, Measure.Report);
        }

        [Test]
        public void SixteenNotesOnTheHeaviestSettingsRunAtLeastTwiceRealTime()
        {
            // Unison 100% (5 voices on each slot), the morph between two waves (two slots playing), resonance and drive at the top.
            Throughput.Guard("Neo Learn Subtractive, 16 notes, heaviest settings", Throughput.Floor(x64: 2, arm64: 2), 2, Rate, DyeEngine.MaxBlock, () =>
            {
                var (h, _) = Make(x =>
                {
                    x.Set(LP.UnisonDetune, 1000);
                    x.Set(LP.WaveMorph, 500);
                    x.Set(LP.FilterResonance, 1000);
                    x.Set(LP.AnalogDrive, 1000);
                    x.Set(LP.SubMix, 1000);
                });
                for (int k = 0; k < 16; k++) h.Engine.NoteOn(k, 36 + 3 * k, 1f);
                var l = new float[DyeEngine.MaxBlock];
                var r = new float[DyeEngine.MaxBlock];
                for (int i = 0; i < 20; i++) h.Process(l, r, DyeEngine.MaxBlock);
                return Throughput.InBlocks(DyeEngine.MaxBlock, (pos, n) => h.Process(l, r, n));
            }, Measure.Report);
        }
    }
}

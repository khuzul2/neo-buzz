using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Mix and Amount on every Neo effect (docs/specs/machines.md, "Mix and Amount"): Amount 0 % or Mix 0 % is the input delayed by the
    /// latency, bit for bit; Amount grows the effect monotonically to 200 % and stays finite and bounded; no comb filtering against the
    /// latency; no allocation and no click when either is automated. The unchanged-at-defaults gate is FxGoldenTests.
    /// </summary>
    [TestFixture]
    public class FxMixAmountTests
    {
        /// <summary>An engine behind one interface: process a stereo block in place, report the latency.</summary>
        internal sealed class Rig
        {
            public Action<float[], float[], int> Process;
            public Func<int> Latency;
        }

        internal sealed class Case
        {
            public string Name;
            public ParamDef[] Table;
            public Func<ParamValues, Rig> Make;
            /// <summary>Settings that make the effect clearly act on <see cref="Material"/>.</summary>
            public Action<ParamValues> Active = _ => { };
            /// <summary>Other settings at which the gates are run too (a linear-phase mode, a lookahead).</summary>
            public (string Name, Action<ParamValues> Set)[] Variants = Array.Empty<(string, Action<ParamValues>)>();
            /// <summary>The input (left, right) the effect acts on.</summary>
            public Func<(float[] L, float[] R)> Material = () => (Signals.Drums, Signals.Mixed);
            /// <summary>The peak (linear) the output may reach at 200 % with <see cref="Active"/> on <see cref="Material"/>.</summary>
            public double PeakLimit = 8;
            /// <summary>The effect has its own Mix (aligned with its own dry) instead of the shared stage's.</summary>
            public bool OwnMix;
            /// <summary>What "the effect" is, when the difference from the input is not it: (input, output, latency) as one number that grows with Amount.</summary>
            public Func<float[], float[], int, double> Measured;
            public override string ToString() => Name;
        }

        static class Signals
        {
            public static readonly float[] Drums = FxGoldenTests.DrumLoop(Measure.Rate, 1.0);
            public static readonly float[] Chord = FxGoldenTests.Chord(Measure.Rate, 1.0);
            public static readonly float[] Mixed = Drums.Zip(Chord, (d, c) => d * 0.8f + c * 0.6f).ToArray();
        }

        static int Idx(ParamDef[] t, string id) { int i = Array.FindIndex(t, p => p.Id == id); Assert.That(i, Is.GreaterThanOrEqualTo(0), "parameter " + id); return i; }

        static void Set(ParamValues v, string id, double physical) { int i = Idx(v.Table, id); v.Set(i, v.Table[i].Raw(physical)); }

        static void SetRaw(ParamValues v, string id, int raw) => v.Set(Idx(v.Table, id), raw);

        static Rig R<T>(T e, Action<T, float[], float[], int> p, Func<T, int> latency) => new Rig { Process = (l, r, n) => p(e, l, r, n), Latency = () => latency(e) };

        static string AmountId(Case c) => c.Name == "Neo Tune" ? "FxAmount" : "Amount";

        internal static readonly Case[] Cases =
        {
            new Case { Name = "Neo Clipper", Table = NeoBuzz.Clipper.Params.Table, OwnMix = true,
                Make = v => { var e = new NeoBuzz.Clipper.ClipperEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "Drive", 9); Set(v, "Ceiling", -6); },
                // Clipping is what lowers the crest factor (peak over RMS), in dB.
                Measured = (x, y, lat) => 20 * Math.Log10((Measure.Peak(x, 0, x.Length - lat) / Measure.Rms(x, 0, x.Length - lat)) / (Measure.Peak(y, lat, y.Length - lat) / Measure.Rms(y, lat, y.Length - lat))) },
            new Case { Name = "Neo Comp", Table = NeoBuzz.Comp.Params.Table, OwnMix = true,
                Make = v => { var e = new NeoBuzz.Comp.CompEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, null, null, n), x => x.Latency); },
                Active = v => { Set(v, "Threshold", -30); Set(v, "Ratio", 8); Set(v, "Attack", 0.005); },
                Variants = new (string, Action<ParamValues>)[] { ("lookahead", v => Set(v, "Lookahead", 5)) } },
            new Case { Name = "Neo De-esser", Table = NeoBuzz.DeEsser.Params.Table,
                Make = v => { var e = new NeoBuzz.DeEsser.DeEsserEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "Threshold", -40); Set(v, "Ratio", 10); Set(v, "Range", 18); Set(v, "Freq", 3000); },
                Variants = new (string, Action<ParamValues>)[] { ("split linear", v => SetRaw(v, "Action", 1)) } },
            new Case { Name = "Neo Delay", Table = NeoBuzz.Delay.Params.Table, OwnMix = true, PeakLimit = 6,
                Make = v => { var e = new NeoBuzz.Delay.DelayEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { SetRaw(v, "Sync", 0); Set(v, "Time", 0.12); Set(v, "Feedback", 55); Set(v, "Mix", 0.5); } },
            new Case { Name = "Neo Dither", Table = NeoBuzz.Dither.Params.Table, PeakLimit = 2,
                Make = v => { var e = new NeoBuzz.Dither.DitherEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "Bits", 8); SetRaw(v, "Shaping", 2); } },
            new Case { Name = "Neo Drive", Table = NeoBuzz.Drive.Params.Table, OwnMix = true, PeakLimit = 4,
                Make = v => { var e = new NeoBuzz.Drive.DriveEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => NeoBuzz.Drive.DriveEngine.Latency); },
                Active = v => { Set(v, "Drive", 15); } },
            new Case { Name = "Neo EQ", Table = NeoBuzz.Eq.Params.Table, PeakLimit = 16,
                Make = v => { var e = new NeoBuzz.Eq.EqEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "B2Gain", 6); Set(v, "B3Gain", -5); Set(v, "B5Gain", 5); },
                Variants = new (string, Action<ParamValues>)[] { ("linear phase", v => SetRaw(v, "Phase", 1)) } },
            new Case { Name = "Neo Ensemble", Table = NeoBuzz.Ensemble.Params.Table, OwnMix = true, PeakLimit = 6,
                Make = v => { var e = new NeoBuzz.Ensemble.EnsembleEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "Depth", 0.5); Set(v, "Feedback", 40); } },
            new Case { Name = "Neo Exciter", Table = NeoBuzz.Exciter.Params.Table, PeakLimit = 6,
                Make = v => { var e = new NeoBuzz.Exciter.ExciterEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => NeoBuzz.Exciter.ExciterEngine.Latency); },
                Active = v => { Set(v, "Amount4", 0.6); Set(v, "Amount3", 0.3); Set(v, "Drive", 12); } },
            new Case { Name = "Neo Filter", Table = NeoBuzz.Filter.Params.Table, OwnMix = true, PeakLimit = 12,
                Make = v => { var e = new NeoBuzz.Filter.FilterEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { SetRaw(v, "Type", 0); Set(v, "Cutoff", 900); Set(v, "Resonance", 0.7); },
                // A low-pass filter takes energy out: how much, in dB.
                Measured = (x, y, lat) => 20 * Math.Log10(Measure.Rms(x, 0, x.Length - lat) / Measure.Rms(y, lat, y.Length - lat)) },
            new Case { Name = "Neo Gate", Table = NeoBuzz.Gate.Params.Table,
                Make = v => { var e = new NeoBuzz.Gate.GateEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, null, null, n), x => x.Latency); },
                Active = v => { Set(v, "Threshold", -22); Set(v, "Range", -40); Set(v, "Release", 0.05); Set(v, "Hold", 0.02); },
                Variants = new (string, Action<ParamValues>)[] { ("lookahead", v => Set(v, "Lookahead", 10)) } },
            new Case { Name = "Neo Imager", Table = NeoBuzz.Imager.Params.Table,
                Make = v => { var e = new NeoBuzz.Imager.ImagerEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { foreach (string w in new[] { "Width1", "Width2", "Width3", "Width4" }) Set(v, w, 40); Set(v, "Stereo4", 0.4); } },
            new Case { Name = "Neo Limiter", Table = NeoBuzz.Limiter.Params.Table, PeakLimit = 4,
                Make = v => { var e = new NeoBuzz.Limiter.LimiterEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "InputGain", 9); },
                Variants = new (string, Action<ParamValues>)[] { ("lookahead 5 ms", v => Set(v, "Lookahead", 5)) } },
            new Case { Name = "Neo Low End", Table = NeoBuzz.LowEnd.Params.Table,
                Make = v => { var e = new NeoBuzz.LowEnd.LowEndEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "Punch", 6); Set(v, "Sustain", -6); },
                Variants = new (string, Action<ParamValues>)[] { ("linear phase", v => SetRaw(v, "Phase", 1)) } },
            new Case { Name = "Neo Multiband", Table = NeoBuzz.Multiband.Params.Table, OwnMix = true, PeakLimit = 12,
                Make = v => { var e = new NeoBuzz.Multiband.MultibandEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { foreach (string b in new[] { "B1", "B2", "B3", "B4" }) { Set(v, b + "Threshold", -30); Set(v, b + "Ratio", 4); } },
                Variants = new (string, Action<ParamValues>)[] { ("minimum phase", v => SetRaw(v, "Phase", 1)) } },
            new Case { Name = "Neo Spectral", Table = NeoBuzz.Spectral.Params.Table, OwnMix = true,
                Make = v => { var e = new NeoBuzz.Spectral.SpectralEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "Depth", 0.7); Set(v, "Lift", 6); } },
            new Case { Name = "Neo Strip", Table = NeoBuzz.Strip.Params.Table, PeakLimit = 12,
                Make = v => { var e = new NeoBuzz.Strip.StripEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { SetRaw(v, "CompOn", 1); Set(v, "CompThresh", -30); Set(v, "CompRatio", 6); Set(v, "HiShelfGain", 6); } },
            new Case { Name = "Neo Tape", Table = NeoBuzz.Tape.Params.Table, OwnMix = true, PeakLimit = 6,
                Make = v => { var e = new NeoBuzz.Tape.TapeEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "Drive", 12); Set(v, "Emphasis", 4); } },
            new Case { Name = "Neo Transient", Table = NeoBuzz.Transient.Params.Table, OwnMix = true,
                Make = v => { var e = new NeoBuzz.Transient.TransientEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { Set(v, "Attack", 8); Set(v, "Sustain", -6); } },
            new Case { Name = "Neo Tune", Table = NeoBuzz.Tune.Params.Table, OwnMix = true, PeakLimit = 2,
                Make = v => { var e = new NeoBuzz.Tune.TuneEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => x.Latency); },
                Active = v => { SetRaw(v, "Scale", 0); Set(v, "Retune", 20); },
                Material = () => { var s = Measure.Sine(226, Measure.Rate, 0.4); return (s, s); } },
            new Case { Name = "Neo Verb", Table = NeoBuzz.Verb.Params.Table, OwnMix = true, PeakLimit = 4,
                Make = v => { var e = new NeoBuzz.Verb.VerbEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => 0); },
                Active = v => { Set(v, "Mix", 0.4); Set(v, "Decay", 3); } },
            new Case { Name = "Neo Utility", Table = NeoBuzz.Utility.Params.Table, PeakLimit = 4,
                Make = v => { var e = new NeoBuzz.Utility.UtilityEngine(v); e.SetSampleRate(Measure.Rate); return R(e, (x, l, r, n) => x.Process(l, r, n), x => 0); },
                Active = v => { Set(v, "Gain", 6); Set(v, "Width", 150); Set(v, "Pan", 0.3); } },
        };

        public static IEnumerable<string> AllCases => Cases.Select(c => c.Name);

        static Case Get(string name) => Cases.First(c => c.Name == name);

        static ParamValues Values(Case c, Action<ParamValues> setup = null)
        {
            var v = new ParamValues(c.Table);
            setup?.Invoke(v);
            return v;
        }

        /// <summary>Renders (left, right) through the case in 256-sample blocks; per block <paramref name="each"/> may change parameters. Returns the output and the latency.</summary>
        static (float[] L, float[] R, int Latency) Render(Case c, ParamValues v, float[] inL, float[] inR, Action<int> each = null)
        {
            var rig = c.Make(v);
            int len = inL.Length;
            var outL = new float[len];
            var outR = new float[len];
            var l = new float[256];
            var r = new float[256];
            int latency = 0;
            for (int pos = 0, b = 0; pos < len; pos += 256, b++)
            {
                each?.Invoke(b);
                int n = Math.Min(256, len - pos);
                Array.Copy(inL, pos, l, 0, n);
                Array.Copy(inR, pos, r, 0, n);
                rig.Process(l, r, n);
                latency = rig.Latency();
                Array.Copy(l, 0, outL, pos, n);
                Array.Copy(r, 0, outR, pos, n);
            }
            return (outL, outR, latency);
        }

        static IEnumerable<(string Name, Action<ParamValues> Set)> Setups(Case c)
        {
            yield return ("active", c.Active);
            foreach (var (name, set) in c.Variants) yield return (name, v => { c.Active(v); set(v); });
        }

        /// <summary>Whether the output is the input delayed by the latency exactly, from the first sample on.</summary>
        static string DelayedExactly(float[] input, float[] output, int latency)
        {
            for (int i = 0; i < output.Length; i++)
            {
                float want = i >= latency ? input[i - latency] : 0f;
                if (output[i] != want) return $"sample {i}: {output[i]:R} against {want:R} (latency {latency})";
            }
            return null;
        }

        [Test]
        public void AmountZeroIsTheInputDelayedByTheLatencyBitForBit([ValueSource(nameof(AllCases))] string name)
        {
            var c = Get(name);
            foreach (var (variant, set) in Setups(c))
            {
                var v = Values(c, set);
                SetRaw(v, AmountId(c), 0);
                var (inL, inR) = c.Material();
                var (l, r, latency) = Render(c, v, inL, inR);
                Assert.That(DelayedExactly(inL, l, latency), Is.Null, $"{c.Name} ({variant}) left");
                Assert.That(DelayedExactly(inR, r, latency), Is.Null, $"{c.Name} ({variant}) right");
            }
        }

        [Test]
        public void MixZeroIsTheInputDelayedByTheLatencyBitForBit([ValueSource(nameof(AllCases))] string name)
        {
            var c = Get(name);
            foreach (var (variant, set) in Setups(c))
            {
                // Neo Multiband's own Mix in minimum phase blends against the bands' sum at unity (its crossovers' allpasses), so a
                // parallel blend never combs, and a soloed band wins over it (MultibandTests): Mix 0 there is that dry, not the input.
                // Amount 0 is the input in every mode.
                if (c.Name == "Neo Multiband" && variant == "minimum phase") continue;
                var v = Values(c, set);
                SetRaw(v, "Mix", 0);
                var (inL, inR) = c.Material();
                var (l, r, latency) = Render(c, v, inL, inR);
                Assert.That(DelayedExactly(inL, l, latency), Is.Null, $"{c.Name} ({variant}) left");
                Assert.That(DelayedExactly(inR, r, latency), Is.Null, $"{c.Name} ({variant}) right");
            }
        }

        [Test]
        public void AmountGrowsTheEffectMonotonicallyAndStaysBoundedAt200Percent([ValueSource(nameof(AllCases))] string name)
        {
            var c = Get(name);
            var (inL, inR) = c.Material();
            var steps = new[] { 0, 250, 500, 750, 1000, 1250, 1500, 1750, 2000 };
            double last = -1, first = 0;
            var line = new List<string>();
            foreach (int raw in steps)
            {
                var v = Values(c, c.Active);
                SetRaw(v, AmountId(c), raw);
                var (l, r, latency) = Render(c, v, inL, inR);
                double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
                Assert.That(double.IsFinite(peak), Is.True, $"{c.Name} at {raw / 10} %: finite");
                Assert.That(peak, Is.LessThan(c.PeakLimit), $"{c.Name} at {raw / 10} %: peak");
                // The effect: what the output differs from the delayed input by (RMS over the whole render).
                double sum = 0;
                for (int i = latency; i < l.Length; i++)
                {
                    double dl = l[i] - inL[i - latency], dr = r[i] - inR[i - latency];
                    sum += dl * dl + dr * dr;
                }
                double m = Math.Sqrt(sum / (2.0 * Math.Max(1, l.Length - latency)));
                if (c.Measured != null) m = c.Measured(inL, l, latency);
                line.Add($"{raw / 10}%: {m:G4}");
                if (raw == 0) first = m;
                Assert.That(m, Is.GreaterThanOrEqualTo(last * 0.99 - 1e-9), $"{c.Name}: the effect fell from {last:G4} to {m:G4} at {raw / 10} % ({string.Join(", ", line)})");
                last = m;
            }
            TestContext.Out.WriteLine($"{c.Name}: {string.Join(", ", line)}");
            Assert.That(first, Is.EqualTo(0).Within(1e-12), c.Name + ": no effect at 0 %");
            Assert.That(last, Is.GreaterThan(1e-4), c.Name + ": the active setting does something at 200 %");
        }

        [Test]
        public void ParametersAreAppendedAtTheEndWithDefaultsAt100Percent([ValueSource(nameof(AllCases))] string name)
        {
            var c = Get(name);
            var t = c.Table;
            int amount = Idx(t, AmountId(c));
            Assert.That(t[amount].Default, Is.EqualTo(1000));
            Assert.That(t[amount].Min, Is.EqualTo(0));
            Assert.That(t[amount].Max, Is.EqualTo(2000));
            Assert.That(t[amount].Value(t[amount].Default), Is.EqualTo(100).Within(1e-9), "100 % at the default");
            Assert.That(t[amount].Value(t[amount].Max), Is.EqualTo(200).Within(1e-9));
            int mix = Idx(t, "Mix");
            Assert.That(t[mix].Value(t[mix].Max), Is.EqualTo(1).Within(1e-9));
            if (!c.OwnMix)
            {
                Assert.That(t[mix].Default, Is.EqualTo(1000), "a new Mix is 100 %");
                Assert.That(mix, Is.EqualTo(t.Length - 1), "Mix is the last parameter");
                Assert.That(amount, Is.EqualTo(t.Length - 2), "Amount is before it");
            }
            else Assert.That(amount, Is.EqualTo(t.Length - 1), "Amount is the last parameter");
        }

        [Test]
        public void AutomatingMixOrAmountAllocatesNothingAndMakesNoClick([ValueSource(nameof(AllCases))] string name)
        {
            var c = Get(name);
            var (inL, inR) = c.Material();
            var v = Values(c, c.Active);
            var rig = c.Make(v);
            var l = new float[256];
            var r = new float[256];
            int blocks = inL.Length / 256 - 1;
            int amountAt = Idx(v.Table, AmountId(c)), mixAt = Idx(v.Table, "Mix");
            void Block(int b)
            {
                // Mix and Amount move every block: Mix across its whole range, Amount from 20 % to 180 %.
                v.Set(mixAt, c.OwnMix ? 1000 : (b * 37) % 1001);
                v.Set(amountAt, 200 + (b * 53) % 1601);
                Array.Copy(inL, b % blocks * 256, l, 0, 256);
                Array.Copy(inR, b % blocks * 256, r, 0, 256);
                rig.Process(l, r, 256);
            }
            var (bytes, why) = AllocationCheck.Run(blocks, Block);
            Assert.That(bytes, Is.EqualTo(0), c.Name + ": " + why);
        }

        [Test]
        public void MixAndAmountHaveNoZipperOnASteadyTone([ValueSource(nameof(AllCases))] string name)
        {
            var c = Get(name);
            if (c.Name == "Neo Tune" || c.Name == "Neo Dither") Assert.Ignore("a quantiser or a pitch corrector has no steady tone to read a click from");
            var tone = Measure.Sine(1000, Measure.Rate, 0.3);
            foreach (string which in new[] { "Mix", "Amount" })
            {
                if (which == "Mix" && c.OwnMix) continue;
                var v = Values(c, c.Active);
                string id = which == "Amount" ? AmountId(c) : "Mix";
                var (l, r, latency) = Render(c, v, tone, tone, b => SetRaw(v, id, which == "Mix" ? (int)(500 + 500 * Math.Sin(b * 0.11)) : (int)(1000 + 800 * Math.Sin(b * 0.09))));
                // Click detector: the second difference against its local mean (±64 samples), past the start-up.
                var d2 = new double[l.Length];
                for (int i = 1; i < l.Length - 1; i++) d2[i] = Math.Abs(l[i + 1] - 2.0 * l[i] + l[i - 1]);
                double worst = 0;
                for (int i = 4800; i < l.Length - 4800; i += 1)
                {
                    double mean = 0;
                    for (int k = -64; k <= 64; k++) mean += d2[i + k];
                    mean /= 129;
                    if (d2[i] > 1e-5) worst = Math.Max(worst, d2[i] / Math.Max(mean, 1e-9));
                }
                TestContext.Out.WriteLine($"{c.Name} {which}: worst second difference {worst:F1}x its local mean");
                Assert.That(worst, Is.LessThan(12), $"{c.Name}: automating {which} clicks");
            }
        }

        /// <summary>Magnitude of the output against the delayed input, per sixth of an octave, dB: the comb a misaligned dry would make.</summary>
        static double WorstRipple(float[] input, float[] output, int latency)
        {
            const int N = 32768;
            int start = 20000;
            var a = new float[N];
            var b = new float[N];
            Array.Copy(input, start - latency, a, 0, N);
            Array.Copy(output, start, b, 0, N);
            var ma = Fft.Magnitudes(a, 0, N);
            var mb = Fft.Magnitudes(b, 0, N);
            double worst = 0;
            for (double f = 100; f * 1.12246 < 16000; f *= 1.12246)
            {
                int lo = (int)(f * N / Measure.Rate), hi = (int)(f * 1.12246 * N / Measure.Rate);
                double ea = 0, eb = 0;
                for (int k = lo; k < hi; k++) { ea += ma[k] * ma[k]; eb += mb[k] * mb[k]; }
                worst = Math.Max(worst, Math.Abs(10 * Math.Log10(eb / ea)));
            }
            return worst;
        }

        static IEnumerable<(string Machine, string Variant)> CombCases() => new[]
        {
            ("Neo EQ", "linear phase"), ("Neo Limiter", "lookahead 5 ms"), ("Neo Spectral", ""),
        };

        [Test]
        public void MixAt50PercentOfALatentEffectSetToNeutralIsFlat([Values("Neo EQ", "Neo Limiter", "Neo Spectral")] string name)
        {
            var c = Get(name);
            var noise = Measure.PinkNoise(Measure.Rate * 2, 0.1);
            var v = Values(c, p =>
            {
                if (name == "Neo EQ") SetRaw(p, "Phase", 1);
                if (name == "Neo Limiter") { Set(p, "Lookahead", 5); Set(p, "Ceiling", 0); }
                if (name == "Neo Spectral") { Set(p, "Depth", 0); Set(p, "Lift", 0); }
                SetRaw(p, "Mix", 500);
            });
            var (l, r, latency) = Render(c, v, noise, noise);
            Assert.That(latency, Is.GreaterThan(0), name + " has a latency");
            double ripple = WorstRipple(noise, l, latency);
            TestContext.Out.WriteLine($"{name}: latency {latency} samples, worst deviation {ripple:F3} dB");
            Assert.That(ripple, Is.LessThan(0.1), name);
        }
    }
}

using System.Diagnostics;
using System.Numerics;
using NeoBuzz.Dsp;
using NeoBuzz.Ensemble;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Ensemble (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class EnsembleTests
    {
        const int Rate = Measure.Rate;

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (EnsembleEngine Engine, ParamValues Values) Make(Action<ParamValues> setup, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new EnsembleEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static (float[] L, float[] R) Run(EnsembleEngine e, float[] l, float[] r, int block = EnsembleEngine.MaxBlock, Action<int> before = null)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[EnsembleEngine.MaxBlock];
            var br = new float[EnsembleEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                before?.Invoke(pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static float[] Noise(int length, double rms, uint seed) => Measure.PinkNoise(length, rms, seed);

        static double Db(double x) => Measure.Db(x);

        /// <summary>The magnitude response of an impulse response at <paramref name="hz"/> (its DTFT).</summary>
        static double Dtft(float[] h, double hz)
        {
            double w = 2 * Math.PI * hz / Rate, re = 0, im = 0;
            for (int k = 0; k < h.Length; k++)
            {
                re += h[k] * Math.Cos(w * k);
                im -= h[k] * Math.Sin(w * k);
            }
            return Math.Sqrt(re * re + im * im);
        }

        /// <summary>A small impulse's response (left channel), scaled back to a unit impulse: linear, the saturator idle.</summary>
        static float[] ImpulseResponse(EnsembleEngine e, int length)
        {
            const float a = 1e-3f;
            var x = new float[length];
            x[0] = a;
            var (l, _) = Run(e, x, x);
            for (int i = 0; i < l.Length; i++) l[i] /= a;
            return l;
        }

        /// <summary>The frequency of the deepest dip of |H| within ±<paramref name="window"/> (relative) of <paramref name="hz"/>, by golden-section search.</summary>
        static (double Hz, double Db) Extreme(Func<double, double> mag, double hz, double window, bool minimum)
        {
            double a = hz * (1 - window), b = hz * (1 + window);
            // A coarse scan first (the window may hold more than one bump), then golden section.
            double best = a, bestV = minimum ? double.MaxValue : double.MinValue;
            for (int k = 0; k <= 400; k++)
            {
                double f = a + (b - a) * k / 400;
                double m = mag(f);
                if (minimum ? m < bestV : m > bestV) { best = f; bestV = m; }
            }
            double lo = Math.Max(a, best - (b - a) / 400), hi = Math.Min(b, best + (b - a) / 400);
            const double phi = 0.6180339887498949;
            for (int it = 0; it < 60; it++)
            {
                double x1 = hi - phi * (hi - lo), x2 = lo + phi * (hi - lo);
                double m1 = mag(x1), m2 = mag(x2);
                if (minimum ? m1 < m2 : m1 > m2) hi = x2; else lo = x1;
            }
            double f0 = 0.5 * (lo + hi);
            return (f0, Db(mag(f0)));
        }

        // ------------------------------------------------------------------ Mix 0 %

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void MixZeroPassesTheInputBitForBit(int mode)
        {
            // Full depth, full feedback, the fastest LFO: at Mix 0 % (Output 0 dB) the input comes out
            // bit for bit, negative zeros and the smallest numbers included.
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, mode);
                v.Set(P.Mix, 0);
                v.Set(P.Feedback, Params.Table[P.Feedback].Max);
                v.Set(P.Depth, 1000);
                v.Set(P.Rate, 1000);
                v.Set(P.Voices, 4);
                v.Set(P.Stages, 12);
            });
            var x = Noise(Rate, 0.3, 7);
            x[10] = -0f;
            x[11] = float.Epsilon;
            x[12] = -float.Epsilon;
            var y = (float[])x.Clone();
            var (l, r) = Run(e, x, y, block: 117);
            for (int i = 0; i < x.Length; i++)
            {
                Assert.That(BitConverter.SingleToInt32Bits(l[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"left {i}");
                Assert.That(BitConverter.SingleToInt32Bits(r[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"right {i}");
            }
            Assert.That(e.Latency, Is.EqualTo(0));
        }

        [Test]
        public void TheOutputDoesNotDependOnHowTheHostSplitsIt()
        {
            // The LFO and the coefficients move in segments that run across blocks: blocks of 256 and of 117 give the same samples.
            foreach (int mode in new[] { 0, 1, 2 })
            {
                Action<ParamValues> setup = v => { v.Set(P.Mode, mode); v.Set(P.Feedback, 1700); v.Set(P.Rate, 800); v.Set(P.Voices, 3); };
                var x = Noise(Rate / 2, 0.2, 3);
                var (a, _) = Run(Make(setup).Engine, x, x, block: 256);
                var (b, _) = Run(Make(setup).Engine, x, x, block: 117);
                Assert.That(b, Is.EqualTo(a), $"mode {mode}");
            }
        }

        // ------------------------------------------------------------------ the phaser's notches

        /// <summary>Notches of N first-order allpasses at fc (bilinear, prewarped), mixed 1:1 with the dry signal.</summary>
        static IEnumerable<double> PhaserNotches(int stages, double fc)
        {
            double t = Math.Tan(Math.PI * fc / Rate);
            for (int k = 0; 2 * k + 1 < stages; k++)
                yield return Rate / Math.PI * Math.Atan(t * Math.Tan((2 * k + 1) * Math.PI / (2 * stages)));
        }

        [TestCase(2, 1000.0)]
        [TestCase(4, 700.0)]
        [TestCase(6, 300.0)]
        [TestCase(8, 1500.0)]
        [TestCase(12, 400.0)]
        public void PhaserNotchesAreWhereTheoryPutsThem(int stages, double centre)
        {
            // Depth 0 (the stages hold Centre), no feedback, Mix 50 %: a notch wherever the stages turn
            // the phase by an odd multiple of 180°, f = fs/π · atan(tan(πfc/fs) · tan((2k + 1)π/2N)).
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, 2);
                v.Set(P.Stages, stages);
                Set(v, P.Centre, centre);
                v.Set(P.Depth, 0);
            });
            var h = ImpulseResponse(e, 16384);
            double fc = Params.Table[P.Centre].Value(Params.Table[P.Centre].Raw(centre));
            double worst = 0, shallowest = -999;
            var found = new List<string>();
            foreach (double f in PhaserNotches(stages, fc))
            {
                if (f > 20000) continue;
                var (at, depth) = Extreme(hz => Dtft(h, hz), f, 0.05, minimum: true);
                double err = (at - f) / f;
                worst = Math.Max(worst, Math.Abs(err));
                shallowest = Math.Max(shallowest, depth);
                found.Add($"{f:F1} Hz → {at:F1} ({depth:F0} dB)");
            }
            Measure.Report($"Neo Ensemble phaser, {stages} stages at {fc:F0} Hz: notches {string.Join(", ", found)}; worst {worst * 100:F4} % off, shallowest {shallowest:F0} dB");
            Assert.That(found.Count, Is.EqualTo(stages / 2).Or.EqualTo(PhaserNotches(stages, fc).Count(f => f <= 20000)));
            Assert.That(worst, Is.LessThan(0.002), "within 0.2 % of theory");
            Assert.That(shallowest, Is.LessThan(-40), "notches at least 40 dB deep");
        }

        // ------------------------------------------------------------------ the flanger's comb

        [TestCase(1.0, 0.0)]
        [TestCase(2.5, 0.0)]
        [TestCase(0.37, 0.0)]
        [TestCase(1.0, 70.0)]
        [TestCase(1.0, -70.0)]
        public void FlangerCombNotchesAreWhereItsDelayPutsThem(double delayMs, double feedback)
        {
            // Depth 0, Mix 50 %: the wet is the input τ later, so dry + wet cancels at odd multiples of
            // 1/(2τ). With feedback g, resonant peaks rise at k/τ (g > 0) or (k + ½)/τ (g < 0).
            var (e, v) = Make(pv =>
            {
                pv.Set(P.Mode, 1);
                Set(pv, P.Delay, delayMs / 1000);
                pv.Set(P.Depth, 0);
                Set(pv, P.Feedback, feedback);
                pv.Set(P.Tone, 1000);
            });
            double tau = e.Delay(0, 0) / (double)Rate;   // the delay the engine runs, s
            double set = v.Value(P.Delay);
            Assert.That(tau, Is.EqualTo(set).Within(1e-6 * set), "the delay is the setting");
            var h = ImpulseResponse(e, 65536);
            double worstNotch = 0, worstPeak = 0;
            var notes = new List<string>();
            for (int k = 0; k < 40; k++)
            {
                double notch = (k + 0.5) / tau;
                if (notch > 10000) break;
                if (feedback == 0)
                {
                    // The 10 Hz high-pass on the line's input leads the wet's phase a little at the lowest
                    // notch (about 3 Hz higher): each notch within 0.5 % or 4 Hz.
                    var (at, depth) = Extreme(hz => Dtft(h, hz), notch, 0.25 / (k + 0.5), minimum: true);
                    worstNotch = Math.Max(worstNotch, Math.Abs(at - notch) / Math.Max(notch, 800));
                    if (k < 3) notes.Add($"notch {notch:F1} → {at:F1} Hz ({depth:F1} dB)");
                }
                double peak = feedback >= 0 ? (k + 1) / tau : (k + 0.5) / tau;
                if (feedback != 0 && peak < 10000)
                {
                    var (at, level) = Extreme(hz => Dtft(h, hz), peak, 0.2 / (k + 1), minimum: false);
                    worstPeak = Math.Max(worstPeak, Math.Abs(at - peak) / peak);
                    if (k < 3) notes.Add($"peak {peak:F1} → {at:F1} Hz ({level:+0.0;−0.0} dB)");
                }
            }
            Measure.Report($"Neo Ensemble flanger, {delayMs} ms, feedback {feedback:+0;−0} %: {string.Join(", ", notes)}; worst notch {worstNotch * 100:F3} %, peak {worstPeak * 100:F3} % off (to 10 kHz)");
            Assert.That(worstNotch, Is.LessThan(0.005), "notches within 0.5 % of (k + ½)/τ, or 4 Hz");
            Assert.That(worstPeak, Is.LessThan(0.005), "peaks within 0.5 %");
        }

        [Test]
        public void TheMeasuredResponseIsTheDesign()
        {
            // The engine's impulse response against EnsembleDesign's response (what the editor draws), the
            // LFO still: chorus voices, flanger and phaser with feedback both ways, Tone low.
            var cases = new (string Name, Action<ParamValues> Setup)[]
            {
                ("chorus, 3 voices, tone 3 kHz", v => { v.Set(P.Voices, 3); Set(v, P.Tone, 3000); v.Set(P.Depth, 0); }),
                ("flanger 1.3 ms, +80 %", v => { v.Set(P.Mode, 1); Set(v, P.Delay, 0.0013); Set(v, P.Feedback, 80); v.Set(P.Depth, 0); }),
                ("flanger 4 ms, −80 %, tone 2 kHz", v => { v.Set(P.Mode, 1); Set(v, P.Delay, 0.004); Set(v, P.Feedback, -80); Set(v, P.Tone, 2000); v.Set(P.Depth, 0); }),
                ("phaser 6 stages, +70 %", v => { v.Set(P.Mode, 2); v.Set(P.Stages, 6); Set(v, P.Feedback, 70); v.Set(P.Depth, 0); }),
                ("phaser 12 stages, −70 %, tone 4 kHz", v => { v.Set(P.Mode, 2); v.Set(P.Stages, 12); Set(v, P.Feedback, -70); Set(v, P.Tone, 4000); v.Set(P.Depth, 0); }),
            };
            double worstAll = 0;
            foreach (var (name, setup) in cases)
            {
                var (e, v) = Make(setup);
                var s = EnsembleDesign.From(v, 120, Rate);
                var h = ImpulseResponse(e, 65536);
                double worst = 0;
                for (int k = 0; k < 160; k++)
                {
                    double hz = 20 * Math.Pow(16000 / 20.0, k / 159.0);
                    double design = Db(EnsembleDesign.Magnitude(s, Rate, e.Phase, 0, hz));
                    double measured = Db(Dtft(h, hz));
                    if (design > -40) worst = Math.Max(worst, Math.Abs(measured - design));
                }
                Measure.Report($"Neo Ensemble response = design ({name}): within {worst:F4} dB, 20 Hz – 16 kHz");
                worstAll = Math.Max(worstAll, worst);
            }
            Assert.That(worstAll, Is.LessThan(0.05));
        }

        // ------------------------------------------------------------------ chorus depth in cents

        /// <summary>Instantaneous frequency from the zero crossings (rising) of a sine, one value per period, with its time.</summary>
        static List<(double T, double Hz)> Crossings(float[] y, int from)
        {
            var list = new List<(double, double)>();
            double last = double.NaN;
            for (int i = from + 1; i < y.Length; i++)
            {
                if (y[i - 1] < 0 && y[i] >= 0)
                {
                    double t = i - 1 + y[i - 1] / (double)(y[i - 1] - y[i]);
                    if (!double.IsNaN(last)) list.Add(((t + last) / 2 / Rate, Rate / (t - last)));
                    last = t;
                }
            }
            return list;
        }

        [TestCase(0.5, 20.0, 0)]
        [TestCase(0.5, 60.0, 0)]
        [TestCase(2.0, 30.0, 0)]
        [TestCase(1.0, 50.0, 1)]
        public void ChorusPitchModulationInCentsMatchesTheSetting(double rateHz, double depthPct, int shape)
        {
            // One voice, wet only: a 1 kHz sine read through a delay τ(t) = Delay · 2^(Depth · 1 oct · lfo(t))
            // comes out at f · (1 − τ′(t)). The largest deviation up and down, in cents, against that.
            const double delay = 0.007, f = 1000;
            var (e, v) = Make(pv =>
            {
                Set(pv, P.Rate, rateHz);
                Set(pv, P.Depth, depthPct / 100);
                Set(pv, P.Delay, delay);
                pv.Set(P.Shape, shape);
                pv.Set(P.Voices, 1);
                pv.Set(P.Mix, 1000);
                pv.Set(P.Tone, 1000);
            });
            double r = v.Value(P.Rate), d = v.Value(P.Depth) * EnsembleDesign.SpanOctaves[0], log2D = Math.Log2(v.Value(P.Delay));
            int seconds = (int)Math.Ceiling(2 / r) + 1;
            var x = Measure.Sine(f, seconds * Rate, 0.5);
            var (y, _) = Run(e, x, x);
            var measured = Crossings(y, Rate / 2);
            // Expected: the same averaging (one period) of f · (1 − τ′), τ from the LFO's own phase.
            double Tau(double t)
            {
                double ph = r * t;
                double lfo = EnsembleDesign.Wave(ph, shape);
                return Math.Pow(2, log2D + d * lfo);
            }
            double maxUp = 0, maxDown = 0, expUp = 0, expDown = 0;
            foreach (var (t, hz) in measured)
            {
                double c = Measure.Cents(hz, f);
                maxUp = Math.Max(maxUp, c);
                maxDown = Math.Min(maxDown, c);
                double half = 0.5 / hz;
                double expected = f * (1 - (Tau(t + half) - Tau(t - half)) / (2 * half));
                double ec = Measure.Cents(expected, f);
                expUp = Math.Max(expUp, ec);
                expDown = Math.Min(expDown, ec);
            }
            Measure.Report($"Neo Ensemble chorus {(shape == 0 ? "sine" : "triangle")} {r:F2} Hz, depth {depthPct} % at 7 ms: pitch +{maxUp:F2} / {maxDown:F2} cents (expected +{expUp:F2} / {expDown:F2})");
            Assert.That(maxUp, Is.EqualTo(expUp).Within(Math.Max(0.3, 0.03 * expUp)), "up");
            Assert.That(maxDown, Is.EqualTo(expDown).Within(Math.Max(0.3, 0.03 * -expDown)), "down");
        }

        [Test]
        public void ChorusVoicesSpreadTheirLfosOverTheCycle()
        {
            // Four voices: each voice's delay follows the LFO a quarter cycle after the one before.
            var (e, v) = Make(pv => { pv.Set(P.Voices, 4); Set(pv, P.Depth, 0.5); Set(pv, P.Rate, 1.0); });
            var x = new float[Rate];
            double worst = 0;
            Run(e, x, x, before: pos =>
            {
                if (pos % 2048 != 0) return;
                var s = EnsembleDesign.From(v, 120, Rate);
                for (int voice = 0; voice < 4; voice++)
                {
                    // At a segment boundary the delay equals the design's for the phase the LFO is at.
                    double expected = EnsembleDesign.DelaySamples(s.Log2Delay, s.DepthOctaves, EnsembleDesign.Wave(e.Phase + voice / 4.0, 0), Rate);
                    worst = Math.Max(worst, Math.Abs(e.Delay(0, voice) - expected) / expected);
                }
            });
            Assert.That(worst, Is.LessThan(1e-5));
        }

        // ------------------------------------------------------------------ stereo

        [Test]
        public void SpreadOffsetsTheRightChannelsLfo()
        {
            // One voice. Spread 0°: the two channels identical (mono in, mono out); 180°: the right
            // channel's delay moves opposite the left's (in octaves about Delay); 90°: a quarter cycle
            // on. The wet channels' correlation for noise falls as the spread opens (and with two
            // voices, whose right voices fall between the left's).
            var x = Noise(4 * Rate, 0.2, 11);
            var corr = new List<string>();
            foreach (int deg in new[] { 0, 90, 180 })
            {
                var (e, v) = Make(pv => { pv.Set(P.Spread, deg); Set(pv, P.Depth, 0.6); pv.Set(P.Mix, 1000); Set(pv, P.Rate, 1.5); pv.Set(P.Voices, 1); });
                double log2D = Math.Log2(v.Value(P.Delay)) + Math.Log2(Rate);
                double worst = 0;
                var (l, r) = Run(e, x, x, before: pos =>
                {
                    if (pos % 1024 != 0) return;
                    double a = Math.Log2(e.Delay(0, 0)) - log2D, b = Math.Log2(e.Delay(1, 0)) - log2D;
                    double lfoL = EnsembleDesign.Wave(e.Phase, 0), lfoR = EnsembleDesign.Wave(e.Phase + deg / 360.0, 0);
                    double depth = v.Value(P.Depth);
                    worst = Math.Max(worst, Math.Max(Math.Abs(a - depth * lfoL), Math.Abs(b - depth * lfoR)));
                    if (deg == 180) worst = Math.Max(worst, Math.Abs(a + b));
                });
                if (deg == 0) Assert.That(r, Is.EqualTo(l), "0°: both channels alike");
                Assert.That(worst, Is.LessThan(1e-5), $"{deg}°: the right channel's LFO {deg}° on");
                double sxy = 0, sxx = 0, syy = 0;
                for (int i = Rate; i < l.Length; i++) { sxy += l[i] * (double)r[i]; sxx += l[i] * (double)l[i]; syy += r[i] * (double)r[i]; }
                corr.Add($"{deg}° {sxy / Math.Sqrt(sxx * syy):F3}");
                var (e2, _) = Make(pv => { pv.Set(P.Spread, deg); Set(pv, P.Depth, 0.6); pv.Set(P.Mix, 1000); Set(pv, P.Rate, 1.5); pv.Set(P.Voices, 2); });
                var (l2, r2) = Run(e2, x, x);
                sxy = sxx = syy = 0;
                for (int i = Rate; i < l2.Length; i++) { sxy += l2[i] * (double)r2[i]; sxx += l2[i] * (double)l2[i]; syy += r2[i] * (double)r2[i]; }
                corr[^1] += $" (2 voices {sxy / Math.Sqrt(sxx * syy):F3})";
                if (deg == 180) Assert.That(sxy / Math.Sqrt(sxx * syy), Is.LessThan(0.8), "two voices at 180°: the channels differ");
            }
            Measure.Report($"Neo Ensemble stereo: wet correlation for pink noise (chorus, depth 60 %, 1.5 Hz): {string.Join(", ", corr)}");
        }

        // ------------------------------------------------------------------ tempo sync

        [Test]
        public void SyncFollowsTheSongsTempo()
        {
            // 1/4 at 120 BPM is 2 Hz; at 90 BPM, 1.5 Hz; 2 bars (8 beats) at 120, 0.25 Hz; 1/8T at 150, 7.5 Hz.
            foreach (var (sync, bpm, hz) in new[] { (7, 120.0, 2.0), (7, 90.0, 1.5), (10, 120.0, 0.25), (4, 150.0, 7.5) })
            {
                var (e, _) = Make(v => v.Set(P.Sync, sync));
                e.Tempo = bpm;
                var x = new float[Rate];
                double p0 = double.NaN;
                Run(e, x, x, block: 240, before: pos => { if (pos == 2400) p0 = e.Phase; });
                double cycles = e.Phase - p0;
                double expected = hz * (Rate - 2400) / (double)Rate;
                double frac = expected - Math.Floor(expected);
                Assert.That(cycles - Math.Floor(cycles), Is.EqualTo(frac).Within(1e-6), $"{Params.Table[P.Sync].Names[sync]} at {bpm} BPM: {hz} Hz");
            }
        }

        // ------------------------------------------------------------------ feedback: bounded, decaying

        /// <summary>The longest time round the loop for a setting (s): the decay the loop gain bound guarantees runs this slowly at most.</summary>
        static double LoopSeconds(ParamValues v)
        {
            var s = EnsembleDesign.From(v, 120, Rate);
            if (s.Mode != EnsembleMode.Phaser)
                return EnsembleDesign.DelaySamples(s.Log2Delay, s.DepthOctaves, 1, Rate) / Rate;
            // An allpass stage delays the lows most: 2/ω₀ samples at its frequency.
            double f = EnsembleDesign.StageHz(s.Log2Centre, s.DepthOctaves, -1, Rate);
            return (1 + s.Stages * 2 / (2 * Math.PI * f / Rate)) / Rate;
        }

        static IEnumerable<TestCaseData> Extremes()
        {
            // Each parameter alone at each end (the others at their defaults, feedback at its most where
            // the parameter isn't feedback itself), then the worst combinations in each mode.
            foreach (var d in Params.Table)
                foreach (int raw in new[] { d.Min, d.Max })
                {
                    var changes = new List<(int, int)> { (d.Index, raw) };
                    if (d.Index != P.Feedback) changes.Add((P.Feedback, Params.Table[P.Feedback].Max));
                    yield return new TestCaseData(d.Name + "=" + raw, changes.ToArray()).SetName($"Extremes({d.Name} {(raw == d.Min ? "min" : "max")})");
                }
            int fbMax = Params.Table[P.Feedback].Max, fbMin = Params.Table[P.Feedback].Min;
            foreach (int mode in new[] { 0, 1, 2 })
                foreach (int fb in new[] { fbMin, fbMax })
                {
                    string m = ((EnsembleMode)mode).ToString();
                    string sign = fb == fbMax ? "+99 %" : "−99 %";
                    // The LFO fastest and slowest, the delay (or the stages' frequency) shortest and longest
                    // (lowest and highest), full depth, Tone open, every voice and stage.
                    foreach (int rate in new[] { 1000, 0 })
                        foreach (int where in new[] { 0, 1000 })
                        {
                            string name = $"{m} {sign}, full depth, {(rate > 0 ? "fastest" : "slowest")}, {(where > 0 ? "longest" : "shortest")}, tone open";
                            yield return new TestCaseData(name, new[] { (P.Mode, mode), (P.Feedback, fb), (P.Depth, 1000), (P.Rate, rate), (P.Delay, where), (P.Centre, where), (P.Tone, 1000), (P.Voices, 4), (P.Stages, 12) })
                                .SetName($"Extremes({name})");
                        }
                    yield return new TestCaseData($"{m} {sign}, no depth, tone open, 2 stages",
                        new[] { (P.Mode, mode), (P.Feedback, fb), (P.Depth, 0), (P.Tone, 1000), (P.Voices, 1), (P.Stages, 2) })
                        .SetName($"Extremes({m} {sign}, no depth, tone open, 1 voice, 2 stages)");
                }
            yield return new TestCaseData("every parameter at its maximum", Params.Table.Select(d => (d.Index, d.Max)).ToArray()).SetName("Extremes(every parameter at its maximum)");
            yield return new TestCaseData("every parameter at its minimum", Params.Table.Select(d => (d.Index, d.Min)).ToArray()).SetName("Extremes(every parameter at its minimum)");
        }

        [TestCaseSource(nameof(Extremes))]
        public void ExtremesStayBoundedAndDecay(string what, (int Index, int Raw)[] changes)
        {
            // 30 s of pink noise (above 30 Hz) at −20 dBFS RMS, then 10 s of silence: finite, far under the engine's
            // guard (+60 dBFS), levelling off under the noise (the last 10 s no louder than the 10 s
            // before), and decaying once the input stops at least half as fast as the loop gain bound
            // guarantees (|Feedback| per time round the loop).
            var (e, v) = Make(pv => { pv.Set(P.Mix, 1000); foreach (var (i, raw) in changes) pv.Set(i, raw); });
            int noise = 30 * Rate, tail = 10 * Rate;
            var x = new float[noise + tail];
            // Pink noise above 30 Hz: its level over 10 s steady (pink noise's own lowest octaves wander by a dB or more).
            var pink = Noise(noise, 0.1, 5);
            var hp1 = new Biquad();
            var hp2 = new Biquad();
            hp1.Set(BiquadType.HighPass, 30, Rate, 0.7071);
            hp2.Set(BiquadType.HighPass, 30, Rate, 0.7071);
            for (int i = 0; i < noise; i++) x[i] = hp2.Process(hp1.Process(pink[i]));
            float scale = (float)(0.1 / Measure.Rms(x, 0, noise));
            for (int i = 0; i < noise; i++) x[i] *= scale;
            var y = (float[])x.Clone();
            for (int i = 0; i < noise; i++) y[i] = x[(i + 7777) % noise];   // a different right channel
            var (l, r) = Run(e, x, y);
            double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
            double Level(int from, int seconds) => Db(Math.Max(Measure.Rms(l, from, seconds * Rate), Measure.Rms(r, from, seconds * Rate)));
            // Growth of the gain (output against input over each 10 s), so the noise's own slow drift doesn't count.
            double In(int from, int seconds) => Db(Math.Max(Measure.Rms(x, from, seconds * Rate), Measure.Rms(y, from, seconds * Rate)));
            double grow = (Level(20 * Rate, 10) - In(20 * Rate, 10)) - (Level(10 * Rate, 10) - In(10 * Rate, 10));
            double first = Level(noise + Rate, 1), last = Level(noise + 8 * Rate, 1);
            double fb = Math.Abs(v.Value(P.Feedback) / 100);
            double loop = LoopSeconds(v);
            double outputDb = v.Value(P.Output);
            // An LFO slower than 5 s per cycle moves the level between 10 s windows by itself: levelling
            // off is measured on the faster ones (the slow ones are bounded and decay all the same).
            var setting = EnsembleDesign.From(v, 120, Rate);
            bool slowSweep = setting.Rate < 0.2 && setting.DepthOctaves > 0;
            double needDrop = fb < 0.01 ? 60 : Math.Min(60, 0.5 * 7 / loop * 20 * Math.Log10(1 / fb));
            Measure.Report($"Neo Ensemble {what}: peak {Db(peak):+0.0;−0.0} dBFS, last 10 s of noise {grow:+0.00;−0.00} dB, tail {Math.Max(-200, last - first):+0.0;−0.0} dB over 7 s (at least −{needDrop:F1})");
            Assert.That(double.IsFinite(peak), Is.True, "finite");
            Assert.That(Db(peak) - outputDb, Is.LessThan(18), "far under the guard's +60 dBFS (before Output's gain)");
            if (!slowSweep) Assert.That(grow, Is.LessThan(1.5), "levels off under noise");
            if (first > -120) Assert.That(last - first, Is.LessThan(-needDrop), "decays after the input stops");
        }

        // ------------------------------------------------------------------ the loop gain, proven

        [Test]
        public void EachFactorOfTheLoopIsAtMostOne()
        {
            // The bound's factors: the loop's linear read at every fraction (and the wet's cubic, for the
            // record), the Tone one-pole at every corner and open, the phaser's high-pass, an allpass
            // stage at every frequency (exactly 1), and the saturator (slope ≤ 1, |S(u)| ≤ |u|, never
            // past Ceiling). The Tone one-pole's −3 dB point is at its setting.
            double read = 0, cubic = 0;
            for (int k = 0; k <= 200; k++)
                for (int j = 0; j <= 512; j++)
                {
                    read = Math.Max(read, EnsembleDesign.Read(2 + k / 200.0, Math.PI * j / 512, cubic: false).Magnitude);
                    cubic = Math.Max(cubic, EnsembleDesign.Read(2 + k / 200.0, Math.PI * j / 512).Magnitude);
                }
            double lp = 0, hp = 0, ap = 0, corner = 0;
            foreach (double hz in new[] { 10.0, 500, 2000, 8000, 12000, 20000, 0.45 * Rate })
            {
                double a = EnsembleDesign.ToneCoefficient(hz, Rate), g = EnsembleDesign.OnePoleG(hz, Rate);
                double k = EnsembleDesign.StageK(Math.Min(hz, 0.45 * Rate), Rate);
                var zc = new Complex(Math.Cos(2 * Math.PI * hz / Rate), -Math.Sin(2 * Math.PI * hz / Rate));
                corner = Math.Max(corner, Math.Abs(Db(EnsembleDesign.Tone(a, zc).Magnitude) + 10 * Math.Log10(2)));
                for (int j = 0; j <= 512; j++)
                {
                    var z1 = new Complex(Math.Cos(Math.PI * j / 512), -Math.Sin(Math.PI * j / 512));
                    lp = Math.Max(lp, Math.Max(EnsembleDesign.Tone(a, z1).Magnitude, EnsembleDesign.Tone(0, z1).Magnitude));
                    hp = Math.Max(hp, (1 - EnsembleDesign.LowPass(g, z1)).Magnitude);
                    ap = Math.Max(ap, Math.Abs(EnsembleDesign.Allpass(k, z1).Magnitude - 1));
                }
            }
            double slope = 0, ratio = 0, top = 0;
            float prev = EnsembleDesign.Saturate(-50f);
            for (int k = -50000 + 1; k <= 50000; k++)
            {
                float u = k * 0.001f;
                float s = EnsembleDesign.Saturate(u);
                slope = Math.Max(slope, (s - prev) / 0.001);
                if (u != 0) ratio = Math.Max(ratio, s / u);
                top = Math.Max(top, Math.Abs(s));
                prev = s;
            }
            top = Math.Max(top, Math.Abs(EnsembleDesign.Saturate(1e30f)));
            Assert.That(EnsembleDesign.Saturate(float.NaN), Is.EqualTo(0f));
            Assert.That(EnsembleDesign.Saturate(float.PositiveInfinity), Is.EqualTo(EnsembleDesign.Ceiling));
            Measure.Report($"Neo Ensemble loop factors: linear read ≤ {read:F12} (the wet's cubic ≤ {cubic:F12}), tone ≤ {lp:F12} (−3 dB at its setting within {corner:E1} dB), high-pass ≤ {hp:F12}, allpass within {ap:E1} of 1, saturator slope ≤ {slope:F4}, |S(u)/u| ≤ {ratio:F6}, |S| ≤ {top:F4}");
            Assert.That(corner, Is.LessThan(1e-6));
            Assert.That(read, Is.LessThanOrEqualTo(1 + 1e-12));
            Assert.That(lp, Is.LessThanOrEqualTo(1 + 1e-12));
            Assert.That(hp, Is.LessThanOrEqualTo(1 + 1e-12));
            Assert.That(ap, Is.LessThan(1e-12));
            Assert.That(slope, Is.LessThanOrEqualTo(1.0001));
            Assert.That(ratio, Is.LessThanOrEqualTo(1));
            Assert.That(top, Is.LessThanOrEqualTo(EnsembleDesign.Ceiling));
        }

        [Test]
        public void TheLoopGainIsBelowOneAtEveryFrequency()
        {
            // Mode × Delay or Centre × Depth × Voices or Stages × Feedback × Tone, the ends included: the
            // loop's transfer from the coefficients the engine runs, at 12 points of the LFO's cycle, both
            // channels, 258 frequencies. Below 1 everywhere, and never above |Feedback| (0.99 at most).
            var grid = new double[258];
            grid[0] = 0;
            grid[^1] = Math.PI;
            for (int k = 1; k < grid.Length - 1; k++) grid[k] = Math.PI * 10 * Math.Pow(24000 / 10.0, (k - 1) / (double)(grid.Length - 3)) / 24000;
            int fbMax = Params.Table[P.Feedback].Max;
            int[] feedbacks = { 0, 495, 1485, fbMax };
            int[] ends = { 0, 500, 1000 };
            double worst = 0, worstOverFb = 0;
            string worstAt = "";
            int settings = 0;
            var taus = new double[4];
            var silence = new float[200];
            foreach (int mode in new[] { 0, 1, 2 })
                foreach (int where in ends)
                    foreach (int depth in ends)
                        foreach (int count in mode == 0 ? new[] { 1, 2, 3, 4 } : mode == 2 ? new[] { 2, 7, 12 } : new[] { 1 })
                            foreach (int fb in feedbacks)
                                foreach (int tone in ends)
                                {
                                    var (e, v) = Make(pv =>
                                    {
                                        pv.Set(P.Mode, mode); pv.Set(P.Delay, where); pv.Set(P.Centre, where); pv.Set(P.Depth, depth);
                                        pv.Set(P.Voices, Math.Min(4, count)); pv.Set(P.Stages, Math.Max(2, count)); pv.Set(P.Feedback, fb);
                                        pv.Set(P.Tone, tone); pv.Set(P.Rate, 1000); pv.Set(P.Spread, 90);
                                    });
                                    settings++;
                                    double fbSet = Math.Abs(v.Value(P.Feedback) / 100);
                                    for (int step = 0; step < 12; step++)
                                    {
                                        e.Process(silence, silence, silence.Length);
                                        var (g, toneA, hpG) = e.Loop;
                                        for (int c = 0; c < 2; c++)
                                        {
                                            for (int i = 0; i < e.Voices; i++) taus[i] = e.Delay(c, i);
                                            double peak = 0;
                                            foreach (double w in grid)
                                                peak = Math.Max(peak, EnsembleDesign.Loop(e.Mode, g, toneA, hpG, taus, e.Voices, e.StageK(c), e.Stages, w).Magnitude);
                                            if (peak > worst) { worst = peak; worstAt = $"{(EnsembleMode)mode}, delay/centre {where}, depth {depth}, voices/stages {count}, feedback {fb}, tone {tone}"; }
                                            worstOverFb = Math.Max(worstOverFb, peak - fbSet);
                                        }
                                    }
                                }
            Measure.Report($"Neo Ensemble loop gain over {settings} settings (12 LFO points, both channels): at most {worst:F6} ({worstAt}); never more than |Feedback| + {Math.Max(0, worstOverFb):E1}");
            Assert.That(worst, Is.LessThan(1));
            Assert.That(worst, Is.LessThanOrEqualTo(EnsembleDesign.MaxFeedback * (1 + 1e-6)));
            Assert.That(worstOverFb, Is.LessThanOrEqualTo(1e-6));
        }

        // ------------------------------------------------------------------ silence after noise

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void SilenceAfterNoiseLeavesNoSubnormalsAndRunsAtFullSpeed(int mode)
        {
            // 2 s of noise, then 20 s of silence with the loop decaying: nothing subnormal in the lines,
            // filters or allpasses (feedback paths are flushed), and silence costs what noise does.
            var (e, _) = Make(v => { v.Set(P.Mode, mode); Set(v, P.Feedback, 90); v.Set(P.Voices, 4); v.Set(P.Stages, 12); Set(v, P.Delay, 0.002); });
            var noise = Noise(2 * Rate, 0.3, 9);
            var silent = new float[20 * Rate];
            var sw = Stopwatch.StartNew();
            Run(e, noise, noise);
            double noiseSeconds = sw.Elapsed.TotalSeconds / 2;
            sw.Restart();
            var (l, r) = Run(e, silent, silent);
            double silenceSeconds = sw.Elapsed.TotalSeconds / 20;
            Assert.That(e.HoldsSubnormals(), Is.False, "no subnormal state");
            Assert.That(l.Concat(r).Any(float.IsSubnormal), Is.False, "no subnormal output");
            Assert.That(l.Concat(r).All(float.IsFinite), Is.True);
            Measure.Report($"Neo Ensemble {(EnsembleMode)mode}, feedback 90 %: silence after noise costs {silenceSeconds / noiseSeconds:F2}× noise; no subnormals");
            Assert.That(silenceSeconds, Is.LessThan(3 * noiseSeconds + 0.002));
        }

        // ------------------------------------------------------------------ smoothing

        [Test]
        public void ParameterJumpsAreSmooth()
        {
            // A 220 Hz sine at −12 dBFS through each mode; each parameter jumps between its ends every 1024
            // samples. The largest step between samples stays within 1.5× the largest step the steady
            // settings at either end make on their own: no clicks, no zipper.
            var problems = new List<string>();
            var lines = new List<string>();
            foreach (int mode in new[] { 0, 1, 2 })
                foreach (var d in Params.Table)
                {
                    if (d.Index == P.Mode) continue;
                    Action<ParamValues> baseSetup = v => { v.Set(P.Mode, mode); Set(v, P.Feedback, 50); };
                    var x = Measure.Sine(220, Rate, 0.25);
                    double Steady(int raw)
                    {
                        var (e, _) = Make(v => { baseSetup(v); v.Set(d.Index, raw); });
                        var (l, r) = Run(e, x, x);
                        return Math.Max(MaxStep(l, Rate / 2), MaxStep(r, Rate / 2));
                    }
                    double reference = Math.Max(Steady(d.Min), Steady(d.Max));
                    var (je, jv) = Make(baseSetup);
                    var (jl, jr) = Run(je, x, x, before: pos => { if (pos >= 4096 && pos % 1024 == 0) jv.Set(d.Index, (pos / 1024) % 2 == 0 ? d.Min : d.Max); });
                    double jump = Math.Max(MaxStep(jl, 4096), MaxStep(jr, 4096));
                    double ratio = jump / reference;
                    lines.Add($"{(EnsembleMode)mode} {d.Name} {ratio:F2}");
                    if (ratio > 1.5) problems.Add($"{(EnsembleMode)mode}: {d.Name} jumps make steps {ratio:F2}× the steady signal's ({jump:F4} against {reference:F4})");
                }
            Measure.Report($"Neo Ensemble parameter jumps, largest step against the steady signal's: {string.Join(", ", lines)}");
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        static double MaxStep(float[] y, int from)
        {
            double m = 0;
            for (int i = Math.Max(1, from); i < y.Length; i++) m = Math.Max(m, Math.Abs(y[i] - y[i - 1]));
            return m;
        }

        [Test]
        public void ModeAndCountSwitchesFadeTheWetOutAndIn()
        {
            // Mode, Voices and Stages switch at the bottom of a 5 ms fade of the wet signal: on a steady
            // sine, wet only, the output dips to silence and comes back, with no step on the way.
            var (e, v) = Make(pv => pv.Set(P.Mix, 1000));
            var x = Measure.Sine(220, Rate / 2, 0.25);
            var (y, _) = Run(e, x, x, before: pos => { if (pos == 8192) v.Set(P.Mode, 2); });
            double before = Measure.Rms(y, 4000, 4000), after = Measure.Rms(y, 16000, 4000);
            double dip = Measure.Peak(y, 8192 + 200, 80);
            Assert.That(dip, Is.LessThan(0.02), "silent at the switch");
            Assert.That(Measure.Rms(y, 16000, 4000), Is.GreaterThan(0.1), "back after it");
            Assert.That(MaxStep(y, 8192), Is.LessThan(1.5 * MaxStep(x, 0) + 0.002));
            TestContext.Out.WriteLine($"before {Db(before):F1} dB, at the switch {Db(dip):F1} dB peak, after {Db(after):F1} dB");
        }

        // ------------------------------------------------------------------ presets, CPU

        [Test]
        public void EveryPresetLoadsAndStaysWithinLevelBounds()
        {
            // Each factory preset on pink noise at −18 dBFS RMS for 3 s: within ±6 dB of the input's
            // level, peaks under full scale plus 3 dB, finite.
            var x = Noise(3 * Rate, DspMath.DbToGain(-18), 21);
            double inRms = Measure.Rms(x, Rate, 2 * Rate);
            int count = 0;
            foreach (var (_, machine, name, values) in PresetTests.AllPresets())
            {
                if (machine != "Neo Ensemble") continue;
                var (e, v) = Make(pv => { foreach (var kv in values) if (kv.Key >= 0) pv.Set(kv.Key, kv.Value); });
                var (l, r) = Run(e, x, x);
                double rms = Math.Max(Measure.Rms(l, Rate, 2 * Rate), Measure.Rms(r, Rate, 2 * Rate));
                double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
                Measure.Report($"Neo Ensemble / {name}: {Db(rms / inRms):+0.0;−0.0} dB, peak {Db(peak):+0.0;−0.0} dBFS");
                Assert.That(double.IsFinite(peak), Is.True, name);
                Assert.That(Db(rms / inRms), Is.InRange(-6.0, 6.0), name);
                Assert.That(Db(peak), Is.LessThan(3), name);
                count++;
            }
            Assert.That(count, Is.InRange(10, 16));
        }

        [Test]
        public void CpuIsSmall()
        {
            // Stereo at 48 kHz: four chorus voices, and twelve phaser stages, each with feedback.
            foreach (var (name, setup) in new (string, Action<ParamValues>)[]
            {
                ("chorus, 4 voices", v => { v.Set(P.Voices, 4); Set(v, P.Feedback, 50); }),
                ("flanger", v => { v.Set(P.Mode, 1); Set(v, P.Feedback, 80); }),
                ("phaser, 12 stages", v => { v.Set(P.Mode, 2); v.Set(P.Stages, 12); Set(v, P.Feedback, 60); }),
            })
            {
                var (e, _) = Make(setup);
                var x = Noise(10 * Rate, 0.2, 4);
                Run(e, x, x);   // warm
                var sw = Stopwatch.StartNew();
                Run(e, x, x);
                double speed = 10 / sw.Elapsed.TotalSeconds;
                Measure.Report($"Neo Ensemble CPU ({name}): {speed:F0}× real time");
                Assert.That(speed, Is.GreaterThan(50));
            }
        }
    }
}

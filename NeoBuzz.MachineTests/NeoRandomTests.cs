using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.RandomMachine;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Acceptance gates for RND1: Neo Random (issue #200, docs/specs/neo-random.md).
    /// Covers distribution, quantisation, synced & free timing, probability, 1/f drift spectrum & bounds,
    /// Lorenz chaos bounds & final-minute variance, bit-exact seeding & Seed=0 variation, Beat/Bar/Note
    /// retriggering across all 4 modes, target modulation / glide / undo-free writes, and audio hygiene.
    /// </summary>
    [TestFixture]
    public class NeoRandomTests
    {
        static (RandomEngine Engine, ParamValues Values) Make(Action<ParamValues> configure = null)
        {
            var v = new ParamValues(Params.Table);
            configure?.Invoke(v);
            var e = new RandomEngine(v);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        sealed class FakePort : IModPort
        {
            public int Min { get; set; } = 0;
            public int Max { get; set; } = 1000;
            public int SafeMin { get; set; } = 0;
            public int SafeMax { get; set; } = 1000;
            public int Value;
            public int Puts;
            public int Get() => Value;
            public void Put(int raw) { Value = raw; Puts++; }
        }

        // ------------------------------------------------------------------ 1. Distribution & Quantise

        [Test]
        public void RandomDistributionOver100kStepsIsUniformWithinThreePercent()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.Random);
                v.Set(P.Sync, 0);
                Set(v, P.Rate, 10.0);
                v.Set(P.Smooth, 0);
                v.Set(P.Depth, 1000);
                v.Set(P.Bias, 500);
                v.Set(P.Range, 1); // Bipolar identity
                v.Set(P.Quantise, 0);
                v.Set(P.Seed, 42);
            });

            const int Steps = 100_000;
            const int Bins = 20;
            var counts = new int[Bins];
            double rate = Params.Table[P.Rate].Value(Params.Table[P.Rate].Raw(10.0));
            double dt = 1.0 / rate; // exactly 1 step per call

            for (int i = 0; i < Steps; i++)
            {
                float y = e.Step(dt, 0.0, false, 120.0);
                int b = Math.Clamp((int)Math.Floor(y * Bins), 0, Bins - 1);
                counts[b]++;
            }

            double expected = Steps / (double)Bins; // 5000
            double worstPct = 0.0;
            for (int b = 0; b < Bins; b++)
            {
                double errPct = Math.Abs(counts[b] - expected) / expected * 100.0;
                if (errPct > worstPct) worstPct = errPct;
                Assert.That(errPct, Is.LessThanOrEqualTo(3.0), $"bin {b}: count {counts[b]} vs {expected} ({errPct:F2}%)");
            }
            Measure.Report($"Neo Random uniform 100k steps (20 bins): worst bin error {worstPct:F2}% (limit +-3.0%)");
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(5)]
        [TestCase(12)]
        [TestCase(16)]
        public void QuantiseNGivesExactlyNDistinctValues(int n)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.Random);
                v.Set(P.Sync, 0);
                Set(v, P.Rate, 10.0);
                v.Set(P.Smooth, 0);
                v.Set(P.Depth, 1000);
                v.Set(P.Bias, 500);
                v.Set(P.Range, 1);
                v.Set(P.Quantise, n);
                v.Set(P.Seed, 77);
            });

            double rate = Params.Table[P.Rate].Value(Params.Table[P.Rate].Raw(10.0));
            double dt = 1.0 / rate;
            var seen = new HashSet<int>();
            for (int i = 0; i < 4000; i++)
            {
                float y = e.Step(dt, 0.0, false, 120.0);
                Assert.That(float.IsFinite(y), Is.True);
                Assert.That(y, Is.InRange(0f, 1f));
                seen.Add((int)Math.Round(y * 100000));
            }
            Assert.That(seen.Count, Is.EqualTo(n), $"Quantise {n} must produce exact {n} distinct values");
            if (n == 1) Assert.That(seen.Single(), Is.EqualTo(50000), "Quantise 1 outputs 0.5");
        }

        // ------------------------------------------------------------------ 2. Timing (Synced & Free)

        [TestCase((int)RandomMode.Random, 120.0)]
        [TestCase((int)RandomMode.Random, 174.0)]
        [TestCase((int)RandomMode.SampleAndHold, 120.0)]
        [TestCase((int)RandomMode.SampleAndHold, 174.0)]
        public void Synced1_16StepsLandWithinOneSampleOfGrid(int mode, double bpm)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, mode);
                v.Set(P.Sync, 8); // 1/16 = 0.25 beats
                v.Set(P.Feel, 0); // Straight
                v.Set(P.Prob, 1000);
                v.Set(P.Smooth, 0);
                v.Set(P.Seed, 102);
                v.Set(P.Retrigger, 0);
            });

            double stepSamples = (60.0 / bpm) * 0.25 * Measure.Rate;
            int totalSteps = 16;
            int totalSamples = (int)Math.Ceiling((totalSteps + 1) * stepSamples);
            double dt = 1.0 / Measure.Rate;

            var edges = new List<int>();
            float prev = e.Step(dt, 0.0, true, bpm);
            for (int s = 1; s < totalSamples; s++)
            {
                double beats = (s / (double)Measure.Rate) * (bpm / 60.0);
                float y = e.Step(dt, beats, true, bpm);
                if (y != prev)
                {
                    edges.Add(s);
                    prev = y;
                }
            }

            Assert.That(edges.Count, Is.GreaterThanOrEqualTo(totalSteps));
            double worst = 0.0;
            for (int k = 0; k < totalSteps; k++)
            {
                double ideal = (k + 1) * stepSamples;
                double err = Math.Abs(edges[k] - ideal);
                if (err > worst) worst = err;
                Assert.That(err, Is.LessThanOrEqualTo(1.0), $"step {k + 1} at {bpm} BPM landed at {edges[k]} vs ideal {ideal:F2}");
            }
            Measure.Report($"Neo Random {(RandomMode)mode} 1/16 at {bpm:F0} BPM: max grid error {worst:F2} samples over {totalSteps} steps");
        }

        [TestCase((int)RandomMode.Random, 4.0)]
        [TestCase((int)RandomMode.SampleAndHold, 13.7)]
        public void FreeRateStepsLandWithinOneSampleOfQuantizedRate(int mode, double targetHz)
        {
            var (e, pv) = Make(v =>
            {
                v.Set(P.Mode, mode);
                v.Set(P.Sync, 0); // Free
                Set(v, P.Rate, targetHz);
                v.Set(P.Prob, 1000);
                v.Set(P.Smooth, 0);
                v.Set(P.Seed, 303);
            });

            // Compare against actual quantized parameter frequency v.Value(P.Rate).
            double actualHz = pv.Value(P.Rate);
            double stepSamples = Measure.Rate / actualHz;
            int totalSteps = 16;
            int totalSamples = (int)Math.Ceiling((totalSteps + 1) * stepSamples);
            double dt = 1.0 / Measure.Rate;

            var edges = new List<int>();
            float prev = e.Step(dt, 0.0, false, 120.0);
            for (int s = 1; s < totalSamples; s++)
            {
                float y = e.Step(dt, 0.0, false, 120.0);
                if (y != prev)
                {
                    edges.Add(s);
                    prev = y;
                }
            }

            Assert.That(edges.Count, Is.GreaterThanOrEqualTo(totalSteps));
            double worst = 0.0;
            for (int k = 0; k < totalSteps; k++)
            {
                double ideal = (k + 1) * stepSamples;
                double err = Math.Abs(edges[k] - ideal);
                if (err > worst) worst = err;
                Assert.That(err, Is.LessThanOrEqualTo(1.0), $"free step {k + 1} at {actualHz:F3} Hz landed at {edges[k]} vs ideal {ideal:F2}");
            }
            Measure.Report($"Neo Random {(RandomMode)mode} free {actualHz:F3} Hz: max error {worst:F2} samples");
        }

        // ------------------------------------------------------------------ 3. Probability

        [Test]
        public void SampleAndHoldProbability25PctWithinTwoPercentagePoints()
        {
            var (e, pv) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.SampleAndHold);
                v.Set(P.Sync, 0);
                Set(v, P.Rate, 10.0);
                v.Set(P.Prob, 250); // 25 %
                v.Set(P.Smooth, 0);
                v.Set(P.Seed, 505);
            });

            const int Steps = 10_000;
            double dt = 1.0 / pv.Value(P.Rate);
            float prev = e.Step(dt, 0.0, false, 120.0);
            int changed = 0;
            for (int i = 0; i < Steps; i++)
            {
                float y = e.Step(dt, 0.0, false, 120.0);
                if (y != prev) { changed++; prev = y; }
            }

            double pct = changed * 100.0 / Steps;
            Measure.Report($"Neo Random S&H Probability 25%: {changed}/{Steps} = {pct:F2}% (target 25% +-2%)");
            Assert.That(pct, Is.InRange(23.0, 27.0));

            // Edge checks: 0 % never changes, 100 % changes every step.
            pv.Set(P.Prob, 0);
            float hold = e.Step(dt, 0.0, false, 120.0);
            for (int i = 0; i < 500; i++)
                Assert.That(e.Step(dt, 0.0, false, 120.0), Is.EqualTo(hold), "0% probability holds forever");

            pv.Set(P.Prob, 1000);
            int fullChanges = 0;
            prev = e.Step(dt, 0.0, false, 120.0);
            for (int i = 0; i < 500; i++)
            {
                float y = e.Step(dt, 0.0, false, 120.0);
                if (y != prev) { fullChanges++; prev = y; }
            }
            Assert.That(fullChanges, Is.EqualTo(500), "100% probability updates every step");
        }

        // ------------------------------------------------------------------ 4. Drift (10 min bounded + 1/f..1/f^2 slope + dt invariance)

        [TestCase(0.5)]
        [TestCase(2.0)]
        public void DriftStaysBoundedForTenMinutesAndHasPinkToBrownSpectrum(double rateHz)
        {
            var (e, pv) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.Drift);
                v.Set(P.Sync, 0);
                Set(v, P.Rate, rateHz);
                v.Set(P.Smooth, 0);
                v.Set(P.Seed, 202);
            });

            // 10 simulated minutes at 64-sample block rate (750 Hz update rate = 450,000 updates).
            double fs = Measure.Rate / 64.0; // 750 Hz
            double dt = 1.0 / fs;
            int total = (int)(600.0 * fs);
            var buf = new float[total];
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < total; i++)
            {
                float y = e.Step(dt, 0.0, false, 120.0);
                Assert.That(float.IsFinite(y), Is.True);
                Assert.That(y, Is.InRange(0f, 1f), "Drift must stay inside [0, 1] for 10 minutes");
                if (y < min) min = y;
                if (y > max) max = y;
                buf[i] = y;
            }
            Assert.That(max - min, Is.GreaterThan(0.5f), "Drift explores a healthy portion of its range");

            // Estimate power spectral density via averaged Hann-windowed 16384-pt FFTs (Welch).
            const int N = 16384;
            int hop = N / 2;
            double[] psd = new double[N / 2];
            int segs = 0;
            var win = new float[N];
            for (int start = 0; start + N <= total; start += hop)
            {
                double mean = 0;
                for (int i = 0; i < N; i++) mean += buf[start + i];
                mean /= N;
                for (int i = 0; i < N; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / N);
                    win[i] = (float)((buf[start + i] - mean) * w);
                }
                var mag = Fft.Magnitudes(win, 0, N);
                for (int k = 1; k < psd.Length; k++) psd[k] += mag[k] * mag[k];
                segs++;
            }

            // Fit log10(PSD) vs log10(f) across the active drift band [0.15 * f_eff, 4.0 * f_eff].
            double fEff = pv.Value(P.Rate);
            double fLo = 0.15 * fEff;
            double fHi = 4.0 * fEff;
            double binHz = fs / N;
            double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
            int count = 0;
            for (int k = 1; k < psd.Length; k++)
            {
                double f = k * binHz;
                if (f < fLo || f > fHi) continue;
                double lx = Math.Log10(f);
                double ly = Math.Log10(Math.Max(1e-20, psd[k] / segs));
                sumX += lx; sumY += ly; sumXX += lx * lx; sumXY += lx * ly;
                count++;
            }
            double slope = (count * sumXY - sumX * sumY) / (count * sumXX - sumX * sumX);
            Measure.Report($"Neo Random Drift ({fEff:F2} Hz, 10 min): range [{min:F3}, {max:F3}], PSD slope {slope:F2} dec/dec over [{fLo:F2}, {fHi:F2}] Hz (target [-2.0, -1.0])");
            Assert.That(slope, Is.InRange(-2.0, -1.0), "Drift spectrum must fall between -1 and -2 decades per decade");
        }

        [Test]
        public void DriftVarianceIsInvariantToStepSizeDt()
        {
            double MeasureStd(double dt)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.Mode, (int)RandomMode.Drift);
                    Set(v, P.Rate, 2.0);
                    v.Set(P.Smooth, 0);
                    v.Set(P.Seed, 909);
                });
                int steps = (int)(60.0 / dt);
                double sum = 0, sumSq = 0;
                for (int i = 0; i < steps; i++)
                {
                    double y = e.Step(dt, 0.0, false, 120.0);
                    sum += y;
                    sumSq += y * y;
                }
                double mean = sum / steps;
                return Math.Sqrt(Math.Max(0.0, sumSq / steps - mean * mean));
            }

            double stdSmall = MeasureStd(1.0 / 3000.0);
            double stdBlock = MeasureStd(64.0 / 48000.0);
            Measure.Report($"Neo Random Drift dt invariance: std(0.33 ms)={stdSmall:F3}, std(1.33 ms)={stdBlock:F3}");
            Assert.That(Math.Abs(stdSmall - stdBlock), Is.LessThan(0.06), "OU drift RMS must stay invariant across dt");
        }

        // ------------------------------------------------------------------ 5. Chaos (10 min bounded + final-minute AC variance + seed sensitivity)

        [TestCase(0)]    // 0 % (calm periodic orbit)
        [TestCase(500)]  // 50 % (blended quasi-chaos)
        [TestCase(1000)] // 100 % (full double-scroll Lorenz)
        public void ChaosStaysBoundedForTenMinutesWithFinalMinuteActivity(int chaosRaw)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.Chaos);
                Set(v, P.Rate, 1.5);
                v.Set(P.Chaos, chaosRaw);
                v.Set(P.Smooth, 1000);
                v.Set(P.Seed, 110);
            });

            double fs = Measure.Rate / 64.0;
            double dt = 1.0 / fs;
            int total = (int)(600.0 * fs);
            int lastMinStart = (int)(540.0 * fs);
            double sum = 0, sumSq = 0;
            int nLast = 0;
            float min = float.MaxValue, max = float.MinValue;

            for (int i = 0; i < total; i++)
            {
                float y = e.Step(dt, 0.0, false, 120.0);
                Assert.That(float.IsFinite(y), Is.True);
                Assert.That(y, Is.InRange(0f, 1f), "Chaos must stay inside [0, 1] for 10 minutes");
                if (y < min) min = y;
                if (y > max) max = y;
                if (i >= lastMinStart)
                {
                    sum += y;
                    sumSq += y * y;
                    nLast++;
                }
            }
            double mean = sum / nLast;
            double std = Math.Sqrt(Math.Max(0.0, sumSq / nLast - mean * mean));
            Measure.Report($"Neo Random Chaos ({chaosRaw / 10}% over 10 min): range [{min:F3}, {max:F3}], final-minute std {std:F4}");
            Assert.That(std, Is.GreaterThan(0.05), $"Chaos {chaosRaw / 10}% must keep non-zero AC variance in minute 10 (no DC collapse)");
        }

        [TestCase(0)]
        [TestCase(920)]
        public void ChaosRepeatsBitExactWithSameSeedAndDiffersWithAnotherSeed(int chaosRaw)
        {
            float[] Run(int seed)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.Mode, (int)RandomMode.Chaos);
                    Set(v, P.Rate, 2.0);
                    v.Set(P.Chaos, chaosRaw);
                    v.Set(P.Smooth, 1000);
                    v.Set(P.Seed, seed);
                });
                var outArr = new float[2000];
                double dt = 64.0 / Measure.Rate;
                for (int i = 0; i < outArr.Length; i++) outArr[i] = e.Step(dt, i * dt * 2.0, true, 120.0);
                return outArr;
            }

            var a1 = Run(110);
            var a2 = Run(110);
            var b = Run(220);

            Assert.That(a1, Is.EqualTo(a2), "same seed repeats bit-for-bit");
            double maxDiff = 0;
            for (int i = 0; i < a1.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(a1[i] - b[i]));
            Assert.That(maxDiff, Is.GreaterThan(0.1), $"different seeds diverge cleanly (chaos={chaosRaw / 10}%)");
        }

        // ------------------------------------------------------------------ 6. Seed determinism (all 4 modes + Seed 0 + transport pre-roll)

        [TestCase((int)RandomMode.Random)]
        [TestCase((int)RandomMode.SampleAndHold)]
        [TestCase((int)RandomMode.Drift)]
        [TestCase((int)RandomMode.Chaos)]
        public void AllModesRepeatBitExactWithFixedSeedAndDifferWithSeedZero(int mode)
        {
            float[] Run(int seed, bool withStoppedPreroll)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.Mode, mode);
                    v.Set(P.Sync, 0);
                    Set(v, P.Rate, 4.0);
                    v.Set(P.Prob, 600);
                    v.Set(P.Chaos, 800);
                    v.Set(P.Smooth, mode == (int)RandomMode.Chaos ? 1000 : 0);
                    v.Set(P.Seed, seed);
                });
                double dt = 64.0 / Measure.Rate;
                if (withStoppedPreroll)
                {
                    // Simulate RenderTools.Render running ~0.1 s stopped before transport starts.
                    for (int i = 0; i < 80; i++) e.Step(dt, 0.0, false, 120.0);
                }
                var buf = new float[1000];
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = e.Step(dt, i * dt * 2.0, true, 120.0);
                return buf;
            }

            var s1 = Run(777, false);
            var s2 = Run(777, false);
            var sPreroll = Run(777, true);
            Assert.That(s1, Is.EqualTo(s2), $"{(RandomMode)mode}: fixed seed repeats bit-for-bit");
            Assert.That(s1, Is.EqualTo(sPreroll), $"{(RandomMode)mode}: fixed seed repeats bit-for-bit after stopped pre-roll (null-test determinism)");

            var z1 = Run(0, false);
            var z2 = Run(0, false);
            bool anyDiff = false;
            for (int i = 0; i < z1.Length; i++) if (z1[i] != z2[i]) { anyDiff = true; break; }
            Assert.That(anyDiff, Is.True, $"{(RandomMode)mode}: Seed 0 differs run to run");
        }

        // ------------------------------------------------------------------ 7. Retrigger (Beat, Bar, Note) & Shaping (Unipolar/Bipolar/Bias)

        [Test]
        public void BeatBarAndNoteRetriggerWorkForFixedSeedAndSeedZero()
        {
            double dt = 64.0 / Measure.Rate;

            // 1. Beat retrigger with fixed Seed > 0 replays the exact same sequence on every beat.
            var (eBeat, _) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.Random);
                v.Set(P.Sync, 8); // 1/16 (4 steps per beat)
                v.Set(P.Retrigger, 1); // Beat
                v.Set(P.Seed, 42);
            });
            var beat1 = new List<float>();
            var beat2 = new List<float>();
            for (int i = 0; i < 1500; i++)
            {
                double beats = i * dt * 2.0; // 120 BPM
                float y = eBeat.Step(dt, beats, true, 120.0);
                if (beats >= 1.02 && beats < 1.98) beat1.Add(y);
                else if (beats >= 2.02 && beats < 2.98) beat2.Add(y);
            }
            int n = Math.Min(beat1.Count, beat2.Count);
            Assert.That(n, Is.GreaterThan(100));
            for (int i = 0; i < n; i++)
                Assert.That(beat2[i], Is.EqualTo(beat1[i]), $"Beat retrigger sample {i} repeats identically");

            // 2. Bar retrigger on Chaos mode with fixed Seed > 0 restarts the exact same bar trajectory.
            var (eBar, _) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.Chaos);
                Set(v, P.Rate, 2.0);
                v.Set(P.Chaos, 850);
                v.Set(P.Smooth, 1000);
                v.Set(P.Retrigger, 2); // Bar
                v.Set(P.Seed, 110);
            });
            float atBar1 = 0, atBar2 = 0;
            for (int i = 0; i < 4000; i++)
            {
                double beats = i * dt * 2.0;
                float y = eBar.Step(dt, beats, true, 120.0);
                if (Math.Abs(beats - 4.0) < dt) atBar1 = y;
                if (Math.Abs(beats - 8.0) < dt) atBar2 = y;
            }
            Assert.That(atBar2, Is.EqualTo(atBar1).Within(0.02f), "Bar retrigger restarts Chaos from the same point");

            // 3. Note retrigger with Seed = 0 forces an immediate fresh step even when cycle was already 0.
            var (eNote, _) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.Random);
                v.Set(P.Sync, 0);
                Set(v, P.Rate, 0.05); // very slow rate so cycle stays 0 without note triggers
                v.Set(P.Retrigger, 3); // Note
                v.Set(P.Seed, 0);
            });
            float before = eNote.Step(dt, 0.0, false, 120.0);
            eNote.Note();
            float after1 = eNote.Step(dt, 0.0, false, 120.0);
            eNote.Note();
            float after2 = eNote.Step(dt, 0.0, false, 120.0);
            Assert.That(after1, Is.Not.EqualTo(before), "Note retrigger with Seed=0 fires a fresh step immediately");
            Assert.That(after2, Is.Not.EqualTo(after1), "Second Note retrigger fires another fresh step");
        }

        [Test]
        public void RangeDepthAndSignedBiasShapeWithoutDefaultClipping()
        {
            double dt = 1.0 / 10.0;
            // Unipolar at default Bias = 0% (raw 500), Depth = 100%: spans [0, 1] with zero clipping.
            var (eUni, vUni) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.Random);
                Set(v, P.Rate, 10.0);
                v.Set(P.Range, 0); // Unipolar
                v.Set(P.Depth, 1000);
                v.Set(P.Bias, 500); // 0%
                v.Set(P.Seed, 11);
            });
            float lo = 1f, hi = 0f;
            int atOne = 0;
            for (int i = 0; i < 2000; i++)
            {
                float y = eUni.Step(dt, 0, false, 120);
                if (y < lo) lo = y;
                if (y > hi) hi = y;
                if (y >= 0.9999f) atOne++;
            }
            Assert.That(lo, Is.LessThan(0.02f), "Unipolar at Bias 0% reaches near 0");
            Assert.That(hi, Is.GreaterThan(0.98f), "Unipolar at Bias 0% reaches near 1");
            Assert.That(atOne, Is.LessThan(10), "Unipolar at default Bias 0% does not clip the upper half");

            // Depth = 0%, Bias = 0%: Bipolar rests at 0.5, Unipolar rests at 0.0.
            vUni.Set(P.Depth, 0);
            Assert.That(eUni.Step(dt, 0, false, 120), Is.EqualTo(0.0f).Within(1e-6f));
            vUni.Set(P.Range, 1); // Bipolar
            Assert.That(eUni.Step(dt, 0, false, 120), Is.EqualTo(0.5f).Within(1e-6f));
        }

        // ------------------------------------------------------------------ 8. Targets (ModBank range, polarity, glide slew, base restore)

        [Test]
        public void TargetsMoveWithinRangePolarityAndWriteGlideSlew()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, (int)RandomMode.Random);
                v.Set(P.Sync, 8); // 1/16
                v.Set(P.Smooth, 0);
                v.Set(P.Seed, 102);
                Set(v, P.Glide, 0.08); // 80 ms full-range glide
            });

            var port = new FakePort { Min = 0, Max = 1000, SafeMin = 150, SafeMax = 850, Value = 500 };
            var bank = new ModBank { GlideSeconds = e.Glide };
            var slot = bank.Slots[0];
            slot.Used = true;
            slot.Port = port;
            slot.Depth = 80;
            slot.Bipolar = true;
            slot.RangeMin = 200;
            slot.RangeMax = 800;

            double dt = 64.0 / Measure.Rate;
            double maxAllowedStep = 600.0 * dt / e.Glide + 1.0; // span = 800 - 200 = 600
            int last = port.Value;
            int minSeen = port.Value, maxSeen = port.Value, worstJump = 0;

            for (int i = 0; i < 3000; i++)
            {
                float u = e.Step(dt, i * dt * 2.0, true, 120.0);
                bank.Update(u, dt);
                int cur = port.Value;
                Assert.That(cur, Is.InRange(200, 800), "target stays inside slot range");
                worstJump = Math.Max(worstJump, Math.Abs(cur - last));
                minSeen = Math.Min(minSeen, cur);
                maxSeen = Math.Max(maxSeen, cur);
                last = cur;
            }

            Assert.That(worstJump, Is.LessThanOrEqualTo(maxAllowedStep), "every target write obeys Write glide slew");
            Assert.That(minSeen, Is.LessThan(350), "bipolar target swings below base");
            Assert.That(maxSeen, Is.GreaterThan(650), "bipolar target swings above base");

            bank.Release(0);
            Assert.That(port.Value, Is.EqualTo(500), "removing target restores its original base value");
        }

        // ------------------------------------------------------------------ 9. Hygiene (0 bytes alloc, no denormals, state round-trip, 10 presets)

        [Test]
        public void ZeroBytesAllocatedOnAudioThreadAcrossAllModes()
        {
            var engines = new RandomEngine[4];
            for (int m = 0; m < 4; m++)
            {
                int mode = m;
                engines[m] = Make(v =>
                {
                    v.Set(P.Mode, mode);
                    Set(v, P.Rate, 5.0);
                    v.Set(P.Prob, 500);
                    v.Set(P.Chaos, 800);
                    v.Set(P.Smooth, 500);
                    v.Set(P.Seed, 100 + mode);
                }).Engine;
            }
            var bank = new ModBank { GlideSeconds = 0.02 };
            var port = new FakePort { Value = 500 };
            bank.Slots[0].Used = true;
            bank.Slots[0].Port = port;
            bank.Slots[0].Depth = 50;
            bank.Slots[0].Bipolar = true;

            double dt = 64.0 / Measure.Rate;
            float sink = 0;
            var (bytes, why) = AllocationCheck.Run(4000, b =>
            {
                float u = engines[b & 3].Step(dt, b * dt * 2.0, true, 120.0);
                bank.Update(u, dt);
                sink += u;
            });
            Assert.That(bytes, Is.EqualTo(0), why);
            Assert.That(float.IsFinite(sink), Is.True);
        }

        [Test]
        public void StateMigrationsRoundTripAndTenPresetsAreValidAndDistinct()
        {
            var chain = new StateMigrations(Params.Table);
            Assert.That(chain.Validate(), Is.Empty);
            Assert.That(chain.Current, Is.EqualTo(0));

            var saved = new int[Params.Table.Length];
            var keyed = new Dictionary<string, int>();
            for (int i = 0; i < Params.Table.Length; i++)
            {
                int val = Params.Table[i].Min + (Params.Table[i].Max - Params.Table[i].Min) / 2;
                saved[i] = val;
                keyed[Params.Table[i].Id] = val;
            }
            Assert.That(chain.MigrateGlobals(0, saved), Is.EqualTo(saved));
            var roundKey = chain.Migrate(0, keyed);
            foreach (var kv in keyed) Assert.That(roundKey[kv.Key], Is.EqualTo(kv.Value));

            string prsPath = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Machines", "NeoRandom", "Neo Random.prs.xml"));
            var doc = XDocument.Load(prsPath);
            var items = doc.Root!.Elements("Item").ToList();
            Assert.That(items.Count, Is.EqualTo(10), "exactly 10 factory presets");

            // Confirm every preset moves its output cleanly over 4 seconds and all 10 trajectories differ.
            var traces = new List<(string Name, float[] Trace)>();
            double dt = 64.0 / Measure.Rate;
            int nSteps = (int)(4.0 / dt);
            foreach (var item in items)
            {
                string name = (string)item.Attribute("Key")!;
                var v = new ParamValues(Params.Table);
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter"))
                    v.Set((int)p.Attribute("Index")!, (int)p.Attribute("Value")!);
                var e = new RandomEngine(v);
                var tr = new float[nSteps];
                float lo = 1f, hi = 0f;
                for (int i = 0; i < nSteps; i++)
                {
                    float y = e.Step(dt, i * dt * 2.0, true, 120.0);
                    Assert.That(y, Is.InRange(0f, 1f));
                    if (y < lo) lo = y;
                    if (y > hi) hi = y;
                    tr[i] = y;
                }
                Assert.That(hi - lo, Is.GreaterThan(0.10f), $"{name} produces clearly audible movement");
                traces.Add((name, tr));
            }
            for (int i = 0; i < traces.Count; i++)
            {
                for (int j = i + 1; j < traces.Count; j++)
                {
                    double diffSq = 0;
                    for (int k = 0; k < nSteps; k++)
                    {
                        double d = traces[i].Trace[k] - traces[j].Trace[k];
                        diffSq += d * d;
                    }
                    double rmsDiff = Math.Sqrt(diffSq / nSteps);
                    Assert.That(rmsDiff, Is.GreaterThan(0.05), $"{traces[i].Name} vs {traces[j].Name} must be clearly distinct (rms diff {rmsDiff:F3})");
                }
            }
        }
    }
}

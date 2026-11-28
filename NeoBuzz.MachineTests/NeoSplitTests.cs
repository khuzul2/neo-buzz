using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Split;
using static NeoBuzz.MachineTests.SplitRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Split's gates (docs/specs/neo-split.md section 7), on the engine: the outputs summed against the input in every mode, the bands'
    /// slopes, the transient and body split on our own drums and pad, smoothing, the audio thread, presets and the editor's handles.
    /// The routing gates (every output to its own chain, a wire added during playback, save and load, the agent) run through the real
    /// engine in ReBuzz.Studio.Tests/NeoSplitRoutingTests.cs.
    /// </summary>
    [TestFixture]
    public class NeoSplitTests
    {
        const int N = 65536;

        // ── the bands ────────────────────────────────────────────────────────────────

        static (float[][] L, float[][] R) Impulse(ParamValues v)
        {
            var e = Engine(v);
            var x = new float[N];
            x[0] = 1;
            return Run(e, x, (float[])x.Clone());
        }

        /// <summary>The magnitude of an impulse response at one frequency, dB (a direct sum over the response).</summary>
        static double MagDb(float[] h, double hz)
        {
            double w = 2 * Math.PI * hz / Rate, re = 0, im = 0;
            for (int n = 0; n < h.Length; n++)
            {
                re += h[n] * Math.Cos(w * n);
                im -= h[n] * Math.Sin(w * n);
            }
            return 20 * Math.Log10(Math.Max(1e-12, Math.Sqrt(re * re + im * im)));
        }

        static ParamValues BandValues(int bands, int slope, double f1, double f2, double f3) =>
            Values(p => { Choose(p, SplitEngine.Mode.Bands, bands, slope); Crossovers(p, f1, f2, f3); });

        [Test]
        public void Bands_Sum_IsFlatWithin0Point1DbFrom20HzTo20kHz([Values(2, 3, 4)] int bands, [Values(0, 1)] int slope, [Values(0, 1)] int set)
        {
            var (l, r) = set == 0 ? Impulse(BandValues(bands, slope, 120, 1000, 6000)) : Impulse(BandValues(bands, slope, 1500, 5000, 16000));
            var sum = Sum(l);
            var mag = Fft.Magnitudes(sum, 0, N);
            double worst = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double hz = k * (double)Rate / N;
                if (hz < 20 || hz > 20000) continue;
                worst = Math.Max(worst, Math.Abs(20 * Math.Log10(mag[k] * N / 2)));
            }
            TestContext.Out.WriteLine($"{bands} bands, {(slope == 0 ? 24 : 48)} dB/oct, set {set}: sum flat within {worst:F4} dB");
            Assert.That(worst, Is.LessThan(0.1));
            // The right channel is the same.
            Assert.That(Sum(r).SequenceEqual(sum), Is.True, "left and right are processed alike");
        }

        [Test]
        public void Bands_EachBandIsMinus6DbAtItsCrossover([Values(0, 1)] int slope)
        {
            double[] xo = { 100, 1000, 8000 };
            var (l, _) = Impulse(BandValues(4, slope, xo[0], xo[1], xo[2]));
            for (int b = 0; b < 4; b++)
            {
                if (b > 0)
                {
                    double db = MagDb(l[b], xo[b - 1]);
                    TestContext.Out.WriteLine($"band {b + 1} at {xo[b - 1]} Hz: {db:F2} dB");
                    Assert.That(db, Is.EqualTo(-6.02).Within(0.15), $"band {b + 1} at its lower crossover");
                }
                if (b < 3)
                {
                    double db = MagDb(l[b], xo[b]);
                    TestContext.Out.WriteLine($"band {b + 1} at {xo[b]} Hz: {db:F2} dB");
                    Assert.That(db, Is.EqualTo(-6.02).Within(0.15), $"band {b + 1} at its upper crossover");
                }
            }
        }

        [Test]
        public void Bands_OneOctaveFromACrossoverIsWithin1DbOfTheSlope([Values(0, 1)] int slope, [Values(150.0, 1000.0)] double fc)
        {
            // Two bands, so that no other crossover is near. The low band an octave above the crossover and the high band an octave below it.
            double nominal = slope == 0 ? 24 : 48;
            var (l, _) = Impulse(BandValues(2, slope, fc, fc * 2, fc * 4));
            double low = MagDb(l[0], fc * 2), lowPass = MagDb(l[0], fc / 8), high = MagDb(l[1], fc / 2), highPass = MagDb(l[1], fc * 8);
            TestContext.Out.WriteLine($"{nominal} dB/oct at {fc} Hz: low band {low - lowPass:F2} dB an octave above, high band {high - highPass:F2} dB an octave below");
            Assert.That(-(low - lowPass), Is.EqualTo(nominal).Within(1.0), "the low band an octave above");
            Assert.That(-(high - highPass), Is.EqualTo(nominal).Within(1.0), "the high band an octave below");
        }

        [Test]
        public void Bands_MatchTheDesignTheEditorDraws([Values(0, 1)] int slope)
        {
            double[] xo = { 100, 1000, 8000 };
            var (l, _) = Impulse(BandValues(4, slope, xo[0], xo[1], xo[2]));
            foreach (double hz in new[] { 30.0, 70, 100, 140, 300, 700, 1000, 1500, 2500 })
                for (int b = 0; b < 4; b++)
                {
                    double design = 20 * Math.Log10(Math.Max(1e-9, SplitDesign.Band(b, 4, hz, xo, slope)));
                    double got = MagDb(l[b], hz);
                    if (design < -35) continue;
                    Assert.That(got, Is.EqualTo(design).Within(design > -20 ? (slope == 0 ? 0.15 : 0.4) : 0.6), $"band {b + 1} at {hz} Hz");   // deep in a stop band the bilinear warp and the float output move the level a little
                }
        }

        [Test]
        public void Bands_FewerBandsAreTheSumOfTheOnesTheyReplace([Values(0, 1)] int slope)
        {
            // Two bands are the first band and the sum of the other three; three are the first, the second and the sum of the last two.
            var four = Impulse(BandValues(4, slope, 120, 1000, 6000)).L;
            var three = Impulse(BandValues(3, slope, 120, 1000, 6000)).L;
            var two = Impulse(BandValues(2, slope, 120, 1000, 6000)).L;
            for (int i = 0; i < N; i += 7)
            {
                Assert.That(three[2][i], Is.EqualTo(four[2][i] + four[3][i]).Within(1e-6));
                Assert.That(two[1][i], Is.EqualTo(four[1][i] + four[2][i] + four[3][i]).Within(1e-6));
                Assert.That(two[0][i], Is.EqualTo(four[0][i]).Within(1e-6));
            }
            Assert.That(three[3].All(x => x == 0), Is.True, "with three bands output 4 is silent");
            Assert.That(two[2].All(x => x == 0) && two[3].All(x => x == 0), Is.True, "with two bands outputs 3 and 4 are silent");
        }

        [Test]
        public void Bands_CrossoversAreKeptInOrderAThirdOfAnOctaveApart()
        {
            var rnd = new Random(1);
            double top = Math.Min(20000, 0.45 * Rate);
            var xo = new double[3];
            for (int t = 0; t < 2000; t++)
            {
                double f1 = 20 * Math.Pow(1000, rnd.NextDouble()), f2 = 20 * Math.Pow(1000, rnd.NextDouble()), f3 = 20 * Math.Pow(1000, rnd.NextDouble());
                for (int bands = 2; bands <= 4; bands++)
                {
                    SplitEngine.EffectiveCrossovers(f1, f2, f3, bands, Rate, xo);
                    int active = bands - 1;
                    for (int i = 0; i < active; i++)
                    {
                        Assert.That(xo[i], Is.InRange(20.0 - 1e-9, top + 1e-9), $"{f1:F0} {f2:F0} {f3:F0} bands {bands}: crossover {i + 1}");
                        if (i > 0) Assert.That(xo[i] / xo[i - 1], Is.GreaterThanOrEqualTo(SplitEngine.MinRatio - 1e-9), $"{f1:F0} {f2:F0} {f3:F0} bands {bands}: crossover {i + 1} against {i}");
                    }
                    for (int i = active; i < 3; i++) Assert.That(xo[i], Is.GreaterThanOrEqualTo(xo[i - 1]), "the unused crossovers stay in order");
                }
            }
            // Values already in order are not moved.
            SplitEngine.EffectiveCrossovers(120, 1000, 6000, 4, Rate, xo);
            Assert.That(xo, Is.EqualTo(new[] { 120.0, 1000.0, 6000.0 }));
            // Pushed up from below, down from above.
            SplitEngine.EffectiveCrossovers(1000, 1010, 1020, 4, Rate, xo);
            Assert.That(xo[1] / xo[0], Is.EqualTo(SplitEngine.MinRatio).Within(1e-9));
            Assert.That(xo[2] / xo[1], Is.EqualTo(SplitEngine.MinRatio).Within(1e-9));
            SplitEngine.EffectiveCrossovers(20000, 20000, 20000, 4, Rate, xo);
            Assert.That(xo[2], Is.EqualTo(top).Within(1e-9));
            Assert.That(xo[0], Is.EqualTo(top / SplitEngine.MinRatio / SplitEngine.MinRatio).Within(1e-6));
        }

        [Test]
        public void Bands_AMovingCrossoverMakesNoZipperNoise([Values(0, 1)] int slope)
        {
            // A 1.5 kHz tone through two bands while Crossover 1 is set to a new value every block (every 5.3 ms) along a slow sweep
            // from 400 Hz to 5 kHz and back. The low band's level moves smoothly as the crossover passes the tone: everything in its
            // spectrum away from the tone (past 150 Hz either side) is below -60 dB of the tone.
            const int len = Rate * 2;
            var tone = Measure.Sine(1500, len, 0.5);
            var v = BandValues(2, slope, 400, 2000, 8000);
            var e = Engine(v);
            var (l, _) = Run(e, tone, tone, Block, b =>
            {
                double phase = b * 256.0 / Rate / 2.0;                    // one sweep in 2 s
                double f = 400 * Math.Pow(12.5, 0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
                Set(v, P.Xo1, f);
            });
            foreach (int o in new[] { 0, 1 })
            {
                var x = l[o];
                int from = Rate / 2, n = 65536;
                var w = new float[n];
                for (int i = 0; i < n; i++) w[i] = (float)(x[from + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)));
                var mag = Fft.Magnitudes(w, 0, n);
                double peak = mag.Max(), worst = 0;
                for (int k = 1; k < mag.Length; k++)
                {
                    double hz = k * (double)Rate / n;
                    if (Math.Abs(hz - 1500) < 150) continue;
                    worst = Math.Max(worst, mag[k]);
                }
                double db = 20 * Math.Log10(worst / peak);
                TestContext.Out.WriteLine($"{(slope == 0 ? 24 : 48)} dB/oct, output {o + 1}: spurs {db:F1} dB under the tone");
                Assert.That(db, Is.LessThan(-60));
            }
        }

        [Test]
        public void ChangingTheModeTheBandsOrTheSlopeDuringPlaybackDoesNotClick()
        {
            // A 220 Hz tone with a 1.1 kHz one, 6 s; every 0.4 s the settings change: the mode, the band count, the slope.
            const int len = Rate * 6;
            var tone = Measure.Sine(220, len, 0.3);
            var tone2 = Measure.Sine(1100, len, 0.2);
            var x = new float[len];
            for (int i = 0; i < len; i++) x[i] = tone[i] + tone2[i];
            var v = BandValues(4, 0, 120, 1000, 6000);
            var e = Engine(v);
            var steps = new (SplitEngine.Mode, int, int)[]
            {
                (SplitEngine.Mode.Bands, 4, 0), (SplitEngine.Mode.Bands, 3, 0), (SplitEngine.Mode.Bands, 2, 0), (SplitEngine.Mode.Bands, 2, 1), (SplitEngine.Mode.Bands, 4, 1),
                (SplitEngine.Mode.TransientBody, 4, 1), (SplitEngine.Mode.MidSide, 4, 1), (SplitEngine.Mode.Bands, 4, 0), (SplitEngine.Mode.MidSide, 4, 0), (SplitEngine.Mode.TransientBody, 4, 0),
                (SplitEngine.Mode.Bands, 3, 1), (SplitEngine.Mode.Bands, 4, 0), (SplitEngine.Mode.Bands, 4, 0), (SplitEngine.Mode.Bands, 4, 0), (SplitEngine.Mode.Bands, 4, 0),
            };
            var (l, r) = Run(e, x, x, Block, b =>
            {
                int step = Math.Min(steps.Length - 1, (int)(b * 256.0 / Rate / 0.4));
                Choose(v, steps[step].Item1, steps[step].Item2, steps[step].Item3);
            });
            for (int o = 0; o < 4; o++)
            {
                int clicks = ClickDetector.Find(l[o]).Count;
                Assert.That(clicks, Is.EqualTo(0), $"output {o + 1}");
            }
            var sum = Sum(l);
            Assert.That(ClickDetector.Find(sum).Count, Is.EqualTo(0), "the outputs summed");
            // And the level of the sum stays near the input's all along (a crossfade between two phases may dip, never to nothing).
            for (int at = Rate / 4; at + 2400 < len; at += 2400)
            {
                double a = Rms(x, at, at + 2400), s = Rms(sum, at, at + 2400);
                Assert.That(20 * Math.Log10(s / a), Is.InRange(-6.0, 1.0), $"at {at / (double)Rate:F2} s");
            }
        }

        // ── Transient and Body, Mid and Side ──────────────────────────────────────────

        static (float[] L, float[] R) Normalised(float[] l, float[] r, double peak = 0.9)
        {
            double p = 1e-9;
            for (int i = 0; i < l.Length; i++) p = Math.Max(p, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            float g = (float)(peak / p);
            return (l.Select(x => x * g).ToArray(), r.Select(x => x * g).ToArray());
        }

        [Test]
        public void TransientBody_And_MidSide_SumToTheInputWithin120Db([Values(1, 2)] int modeIndex)
        {
            var mode = (SplitEngine.Mode)modeIndex;
            var (l, r) = Normalised(Mix(Rate * 6).L, Mix(Rate * 6).R);
            var v = Values(p => Choose(p, mode));
            var e = Engine(v);
            var (ol, or) = Run(e, l, r);
            int latency = e.Latency;
            Assert.That(latency, Is.EqualTo(mode == SplitEngine.Mode.TransientBody ? 96 : 0), "2 ms of lookahead in Transient and Body, none in Mid and Side");
            var sl = Sum(ol);
            var sr = Sum(or);
            double worst = 0;
            for (int i = latency; i < l.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(sl[i] - l[i - latency]));
                worst = Math.Max(worst, Math.Abs(sr[i] - r[i - latency]));
            }
            TestContext.Out.WriteLine($"{mode}: the outputs summed differ from the input (delayed by {latency}) by at most {worst:E2} ({20 * Math.Log10(worst + 1e-30):F1} dB)");
            Assert.That(worst, Is.LessThan(1e-6));
            // The unused outputs are silent.
            Assert.That(ol[2].All(x => x == 0) && ol[3].All(x => x == 0) && or[2].All(x => x == 0) && or[3].All(x => x == 0), Is.True, "outputs 3 and 4 are silent");
        }

        [Test]
        public void MidSide_AMonoInputGivesASideOfExactlyZero()
        {
            var (l, _) = Normalised(Mix(Rate * 3).L, Mix(Rate * 3).R);
            var v = Values(p => Choose(p, SplitEngine.Mode.MidSide));
            var e = Engine(v);
            var (ol, or) = Run(e, l, l);
            Assert.That(ol[1].All(x => x == 0f) && or[1].All(x => x == 0f), Is.True, "side is exactly 0");
            for (int i = 0; i < l.Length; i++) Assert.That(ol[0][i], Is.EqualTo(l[i]), "mid is the input");
            Assert.That(or[0].SequenceEqual(ol[0]), Is.True, "mid is on both sides");
        }

        [Test]
        public void MidSide_TheOutputsAreMidOnBothSidesAndSideWithTheRightInverted()
        {
            var l = Measure.Sine(300, 4096, 0.5);
            var r = Measure.Sine(500, 4096, 0.25);
            var v = Values(p => Choose(p, SplitEngine.Mode.MidSide));
            var e = Engine(v);
            var (ol, or) = Run(e, l, r);
            for (int i = 0; i < l.Length; i++)
            {
                float m = (l[i] + r[i]) * 0.5f, s = (l[i] - r[i]) * 0.5f;
                Assert.That(ol[0][i], Is.EqualTo(m));
                Assert.That(or[0][i], Is.EqualTo(m));
                Assert.That(ol[1][i], Is.EqualTo(s));
                Assert.That(or[1][i], Is.EqualTo(-s));
            }
        }

        static float[] Window(float[] x, int from, int n) => x.Skip(from).Take(n).ToArray();

        [Test]
        public void TransientBody_TheTransientOutputPeaksWithEachHitOfTheDrumLoop()
        {
            // Our Neo Drums loop (a kick, a snare, closed hats and an open hat, two bars at 120 BPM) at the default settings. For each hit the
            // input's own peak within 30 ms of the note is the hit; the transient output's peak (after the 2 ms lookahead) must fall within
            // 2 ms of it and carry at least half of its level, and the body output holds what follows.
            int len = Rate * 5;
            var (dl, dr) = DrumLoop(2, len);
            var v = Values(p => Choose(p, SplitEngine.Mode.TransientBody));
            var e = Engine(v);
            var (ol, _) = Run(e, dl, dr);
            int lat = e.Latency, window = Rate * 30 / 1000;
            double worstOffset = 0, weakest = 9;
            var hits = DrumHits(2).GroupBy(h => h.Sample).Select(g => g.First()).ToList();
            foreach (var (at, key) in hits)
            {
                int pin = at, pout = at;
                float bi = 0, bo = 0;
                for (int i = at; i < at + window; i++)
                {
                    if (Math.Abs(dl[i]) > bi) { bi = Math.Abs(dl[i]); pin = i; }
                    if (Math.Abs(ol[0][i + lat]) > bo) { bo = Math.Abs(ol[0][i + lat]); pout = i; }
                }
                double offset = Math.Abs(pout - pin) * 1000.0 / Rate;
                worstOffset = Math.Max(worstOffset, offset);
                weakest = Math.Min(weakest, bo / bi);
                Assert.That(offset, Is.LessThanOrEqualTo(2.0), $"hit at {at} (key {key}): the transient peaks {offset:F2} ms from the input's peak");
                Assert.That(bo / bi, Is.GreaterThanOrEqualTo(0.5), $"hit at {at} (key {key}): the transient carries {bo / bi:F2} of the input's peak");
            }
            TestContext.Out.WriteLine($"{hits.Count} hits: the transient output's peak is at most {worstOffset:F2} ms from the input's, and at least {weakest:F2} of its level");
        }

        [Test]
        public void TransientBody_ASustainedPadLeavesTheTransientOutput40DbUnderTheInput()
        {
            int len = Rate * 8;
            var (pl, pr) = PadChord("Pad — Warm Strings", new[] { 57, 60, 64, 67 }, 7.5, len);
            var v = Values(p => Choose(p, SplitEngine.Mode.TransientBody));
            var e = Engine(v);
            var (ol, _) = Run(e, pl, pr);
            // Every 50 ms window from 2 s (the pad's own attack is over) to 7 s.
            double worst = -999;
            for (int at = Rate * 2; at + 2400 < Rate * 7; at += 2400)
                worst = Math.Max(worst, 20 * Math.Log10((Rms(ol[0], at + e.Latency, at + e.Latency + 2400) + 1e-12) / Rms(pl, at, at + 2400)));
            for (int at = Rate * 2; at + 2400 < Rate * 7; at += 2400 * 10)
                TestContext.Out.WriteLine($"  at {at / (double)Rate:F2} s: {20 * Math.Log10((Rms(ol[0], at + e.Latency, at + e.Latency + 2400) + 1e-12) / Rms(pl, at, at + 2400)):F1} dB");
            TestContext.Out.WriteLine($"the pad: the transient output is at most {worst:F1} dB under the input in any 50 ms");
            Assert.That(worst, Is.LessThanOrEqualTo(-40));
        }

        [Test]
        public void TransientBody_TheControlsMoveTheSplit()
        {
            int len = Rate * 5;
            var (dl, dr) = DrumLoop(2, len);
            double Share(Action<ParamValues> set)
            {
                var v = Values(p => { Choose(p, SplitEngine.Mode.TransientBody); set(p); });
                var e = Engine(v);
                var (ol, _) = Run(e, dl, dr);
                return Rms(ol[0], 0, Rate * 4) / (Rms(ol[0], 0, Rate * 4) + Rms(ol[1], 0, Rate * 4));
            }
            double low = Share(p => Set(p, P.Sensitivity, 0.1)), mid = Share(p => Set(p, P.Sensitivity, 0.5)), high = Share(p => Set(p, P.Sensitivity, 0.95));
            TestContext.Out.WriteLine($"transient share at sensitivity 10 %, 50 %, 95 %: {low:F3} {mid:F3} {high:F3}");
            Assert.That(low, Is.LessThan(mid));
            Assert.That(mid, Is.LessThan(high));
            double shortHit = Share(p => Set(p, P.Length, 3)), longHit = Share(p => Set(p, P.Length, 60));
            TestContext.Out.WriteLine($"transient share at length 3 ms, 60 ms: {shortHit:F3} {longHit:F3}");
            Assert.That(shortHit, Is.LessThan(longHit));
            double none = Share(p => Set(p, P.Smoothing, 0)), smooth = Share(p => Set(p, P.Smoothing, 80));
            TestContext.Out.WriteLine($"transient share at smoothing 0, 80 ms: {none:F3} {smooth:F3}");
            Assert.That(none, Is.LessThan(smooth), "a slower return of the body leaves more with the transient");
        }

        // ── the audio thread ──────────────────────────────────────────────────────────

        [Test]
        public void AudioThread_AllocatesNothingOver2000BlocksWithEverySettingMoving()
        {
            var noise = Measure.PinkNoise(Rate * 3, 0.2);
            var v = Values();
            var e = Engine(v);
            var l = new float[256];
            var r = new float[256];
            int blocks = noise.Length / 256 - 1;
            var table = Params.Table;
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                v.Set(P.Mode, (b / 300) % 3);
                v.Set(P.Bands, b % 3);
                v.Set(P.Slope, (b / 7) % 2);
                v.Set(P.Xo1, (b * 37) % 10000);
                v.Set(P.Xo2, (b * 53) % 10000);
                v.Set(P.Xo3, (b * 71) % 10000);
                v.Set(P.Sensitivity, (b * 13) % 1001);
                v.Set(P.Length, (b * 29) % 1001);
                v.Set(P.Smoothing, (b * 17) % 1001);
                Array.Copy(noise, b % blocks * 256, l, 0, 256);
                Array.Copy(noise, (b + 3) % blocks * 256, r, 0, 256);
                e.Process(l, r, 256);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void AudioThread_NoDenormalsInTheStateOrTheOutputs([Values(0, 1)] int slope)
        {
            // Loud noise through every path, then silence: nothing in the state or the outputs is subnormal while the tails die out.
            var noise = Measure.PinkNoise(Rate, 0.5);
            var silence = new float[Rate * 6];
            var x = noise.Concat(silence).ToArray();
            foreach (var mode in new[] { SplitEngine.Mode.Bands, SplitEngine.Mode.TransientBody, SplitEngine.Mode.MidSide })
            {
                var v = Values(p => { Choose(p, mode, 4, slope); Set(p, P.Smoothing, 100); });
                var e = Engine(v);
                bool state = false, sub = false;
                var (l, r) = Run(e, x, x, Block, b =>
                {
                    state |= e.HoldsSubnormals();
                });
                string where = "";
                for (int o = 0; o < 4; o++)
                    foreach (var y in new[] { l[o], r[o] })
                        for (int i = 0; i < y.Length; i++) if (float.IsSubnormal(y[i])) { sub = true; where = $"output {o + 1} at {i}: {y[i]:E3}"; break; }
                Assert.That(state, Is.False, $"{mode}: the state holds a subnormal");
                Assert.That(sub, Is.False, $"{mode}: an output holds a subnormal ({where})");
            }
        }

        [Test]
        public void AudioThread_TheBlockSizeDoesNotChangeTheSound()
        {
            var x = Measure.PinkNoise(Rate, 0.3);
            foreach (var mode in new[] { SplitEngine.Mode.Bands, SplitEngine.Mode.TransientBody, SplitEngine.Mode.MidSide })
            {
                var a = Run(Engine(Values(p => Choose(p, mode, 4, 1))), x, x, 256).L;
                var b = Run(Engine(Values(p => Choose(p, mode, 4, 1))), x, x, 64).L;
                for (int o = 0; o < 4; o++) Assert.That(a[o].SequenceEqual(b[o]), Is.True, $"{mode}, output {o + 1}: blocks of 256 and of 64 give the same bits");
            }
        }

        [Test]
        public void TheMatrixSendsEverySourceOfTheModeToExactlyOneOutput()
        {
            var m = new float[SplitEngine.Outputs * SplitEngine.Sources];
            foreach (var mode in new[] { SplitEngine.Mode.Bands, SplitEngine.Mode.TransientBody, SplitEngine.Mode.MidSide })
                for (int bands = 2; bands <= 4; bands++)
                    for (int slope = 0; slope < 2; slope++)
                    {
                        SplitEngine.Matrix(mode, bands, slope, m);
                        int first = mode == SplitEngine.Mode.Bands ? slope * 4 : mode == SplitEngine.Mode.TransientBody ? 8 : 10;
                        int count = mode == SplitEngine.Mode.Bands ? 4 : 2;
                        for (int s = 0; s < SplitEngine.Sources; s++)
                        {
                            float total = 0;
                            for (int o = 0; o < SplitEngine.Outputs; o++) total += m[o * SplitEngine.Sources + s];
                            Assert.That(total, Is.EqualTo(s >= first && s < first + count ? 1f : 0f), $"{mode} {bands} {slope}: source {s}");
                        }
                    }
        }

        // ── presets ───────────────────────────────────────────────────────────────────

        static readonly string[] PresetNames =
        {
            "Bands — Two-Way Bass and Top", "Bands — Three-Way Mix", "Bands — Four-Way Master", "Bands — Sub Under 60 Hz",
            "Transient — Sharp Hits", "Transient — Soft Attacks", "Transient — Long Body", "Mid / Side — Mid and Side",
        };

        static ParamValues Preset(string name)
        {
            var found = PresetTests.AllPresets().Where(p => p.Machine == "Neo Split" && p.Preset == name).ToList();
            Assert.That(found, Has.Count.EqualTo(1), name);
            var v = new ParamValues(Params.Table);
            foreach (var kv in found[0].Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        [Test]
        public void Presets_AreTheEightOfTheBrief()
        {
            var names = PresetTests.AllPresets().Where(p => p.Machine == "Neo Split").Select(p => p.Preset).ToList();
            Assert.That(names, Is.EquivalentTo(PresetNames));
        }

        /// <summary>The level of each output against the input, dB, over the part of the render that is steady.</summary>
        static double[] Levels(string preset, float[] l, float[] r, out int latency)
        {
            var v = Preset(preset);
            var e = Engine(v);
            var (ol, or) = Run(e, l, r);
            latency = e.Latency;
            var levels = new double[4];
            double input = Rms(l, Rate, l.Length - Rate);
            for (int o = 0; o < 4; o++) levels[o] = 20 * Math.Log10((Rms(ol[o], Rate, l.Length - Rate) + 1e-9) / input);
            return levels;
        }

        [Test]
        public void Presets_AreClearlyAudibleAndClearlyDifferent()
        {
            var (l, r) = Normalised(Mix(Rate * 8).L, Mix(Rate * 8).R, 1.0);
            var all = new Dictionary<string, double[]>();
            foreach (string name in PresetNames)
            {
                var lv = Levels(name, l, r, out _);
                all[name] = lv;
                TestContext.Out.WriteLine($"{name}: outputs at {string.Join(", ", lv.Select(x => x.ToString("+0.0;-0.0")))} dB against the input");
                var v = Preset(name);
                int used = SplitDesign.OutputNames((SplitEngine.Mode)v.Choice(P.Mode), (int)Math.Round(v.Value(P.Bands))).Count(s => s.Length > 0);
                // Every used output is clearly audible at first listen, and the split does something: at least one of them is well under the whole.
                for (int o = 0; o < used; o++) Assert.That(lv[o], Is.GreaterThan(-30), $"{name}: output {o + 1} is nearly silent");
                Assert.That(lv.Take(used).Min(), Is.LessThan(-3), $"{name}: no output is clearly separate from the whole");
            }
            foreach (var a in PresetNames)
                foreach (var b in PresetNames)
                {
                    if (string.CompareOrdinal(a, b) >= 0) continue;
                    double d = 0;
                    for (int o = 0; o < 4; o++) d = Math.Max(d, Math.Abs(all[a][o] - all[b][o]));
                    // Two Bands presets with the same band count may differ in where the bands are cut rather than in how loud they are: a first crossover 1.5 times higher is audibly a different split.
                    var va = Preset(a);
                    var vb = Preset(b);
                    bool bandsApart = va.Choice(P.Mode) == (int)SplitEngine.Mode.Bands && vb.Choice(P.Mode) == (int)SplitEngine.Mode.Bands
                        && Math.Max(va.Value(P.Xo1), vb.Value(P.Xo1)) >= 1.5 * Math.Min(va.Value(P.Xo1), vb.Value(P.Xo1));
                    if (!bandsApart) Assert.That(d, Is.GreaterThanOrEqualTo(3.0), $"{a} and {b} differ by only {d:F1} dB in any output");
                }
        }

        [Test]
        public void Presets_AllOfThemSumToTheInput()
        {
            var (l, r) = Normalised(Mix(Rate * 6).L, Mix(Rate * 6).R, 0.9);
            foreach (string name in PresetNames)
            {
                var v = Preset(name);
                var e = Engine(v);
                var (ol, or) = Run(e, l, r);
                var sl = Sum(ol);
                int lat = e.Latency;
                var mode = (SplitEngine.Mode)v.Choice(P.Mode);
                if (mode == SplitEngine.Mode.Bands)
                {
                    // Flat in level: the energy of the sum is the input's within 0.2 dB.
                    double a = Rms(l, Rate, l.Length), s = Rms(sl, Rate, l.Length);
                    Assert.That(20 * Math.Log10(s / a), Is.EqualTo(0).Within(0.2), name);
                }
                else
                {
                    double worst = 0;
                    for (int i = lat; i < l.Length; i++) worst = Math.Max(worst, Math.Abs(sl[i] - l[i - lat]));
                    Assert.That(worst, Is.LessThan(1e-6), name);
                }
            }
        }

        // ── the editor's handles ──────────────────────────────────────────────────────

        sealed class Fake : ISplitCrossovers
        {
            public int Bands { get; set; } = 4;
            public readonly double[] Xo = { 120, 1000, 6000 };
            public double Hz(int i) => Xo[i];
            public void Move(int i, double hz) => Xo[i] = hz;
        }

        [Test]
        public void Handles_DragStopsAThirdOfAnOctaveFromTheNeighbours()
        {
            var graph = new ResponseGraph(-48, 6, 12);
            graph.NeedRoom(SplitHandleInput.GripRadius + 2);
            graph.Layout(new NeoRect(0, 0, 520, 176));
            var fake = new Fake();
            var input = new SplitHandleInput(graph, fake);
            double y = graph.Plot.Top + 40;
            // Press on the second crossover's line and drag it far right, past the third.
            Assert.That(input.Down(graph.X(1000), y, 1, false), Is.True);
            Assert.That(input.Capturing, Is.True);
            Assert.That(input.Move(graph.X(1000) + 20, y, false), Is.True);
            Assert.That(fake.Xo[1], Is.GreaterThan(1000));
            input.Move(graph.Plot.Right, y, false);
            Assert.That(fake.Xo[1], Is.EqualTo(6000 / SplitEngine.MinRatio).Within(1e-6), "stopped a third of an octave under the third crossover");
            input.Move(graph.Plot.Left, y, false);
            Assert.That(fake.Xo[1], Is.EqualTo(120 * SplitEngine.MinRatio).Within(1e-6), "stopped a third of an octave over the first");
            input.Up();
            Assert.That(input.Capturing, Is.False);
            // Fine: a tenth of the distance.
            fake.Xo[1] = 1000;
            input.Down(graph.X(1000), y, 1, true);
            input.Move(graph.X(1000) + 100, y, true);
            Assert.That(graph.X(fake.Xo[1]) - graph.X(1000), Is.EqualTo(10).Within(0.5));
            input.Up();
            // A press away from every line does nothing.
            Assert.That(input.Down(graph.X(300), y, 1, false), Is.False);
            // The wheel steps a twelfth of an octave.
            fake.Xo[0] = 120;
            Assert.That(input.Wheel(graph.X(120), y, 120, false), Is.True);
            Assert.That(fake.Xo[0] / 120, Is.EqualTo(Math.Pow(2, 1.0 / 12)).Within(1e-9));
            // Fewer bands, fewer handles.
            fake.Bands = 2;
            Assert.That(input.Count, Is.EqualTo(1));
            Assert.That(input.CrossoverAt(graph.X(fake.Xo[2]), y), Is.EqualTo(-1), "no handle for a crossover that is not in use");
        }

        [Test]
        public void Handles_DrawOnTheKitsCanvasWithoutAToolkit()
        {
            var graph = new ResponseGraph(-48, 6, 12);
            graph.Layout(new NeoRect(0, 0, 520, 176));
            var input = new SplitHandleInput(graph, new Fake());
            var canvas = new RecordingCanvas();
            input.Draw(canvas, NeoColor.FromRgb(0x6C, 0xB4, 0xE8));
            Assert.That(canvas.Of(NeoDrawKind.Ellipse).Count(), Is.EqualTo(3), "a grip for each of the three crossovers");
        }

        [Test]
        public void OutputNamesFollowTheModeAndTheBandCount()
        {
            Assert.That(SplitDesign.OutputNames(SplitEngine.Mode.Bands, 4), Is.EqualTo(new[] { "Low", "Low Mid", "High Mid", "High" }));
            Assert.That(SplitDesign.OutputNames(SplitEngine.Mode.Bands, 3), Is.EqualTo(new[] { "Low", "Mid", "High", "" }));
            Assert.That(SplitDesign.OutputNames(SplitEngine.Mode.Bands, 2), Is.EqualTo(new[] { "Low", "High", "", "" }));
            Assert.That(SplitDesign.OutputNames(SplitEngine.Mode.TransientBody, 4), Is.EqualTo(new[] { "Transient", "Body", "", "" }));
            Assert.That(SplitDesign.OutputNames(SplitEngine.Mode.MidSide, 4), Is.EqualTo(new[] { "Mid", "Side", "", "" }));
        }

        [Test]
        public void ReadingsNameEveryOutputAndTheLatency()
        {
            var e = Engine(Values(p => Choose(p, SplitEngine.Mode.TransientBody)));
            var x = Measure.PinkNoise(Rate / 2, 0.3);
            Run(e, x, x);
            var r = e.Readings();
            Assert.That(r.Keys, Is.EquivalentTo(new[] { "out1_db", "out2_db", "out3_db", "out4_db", "latency_samples" }));
            Assert.That(r["latency_samples"], Is.EqualTo(96));
            Assert.That(r["out1_db"], Is.GreaterThan(-60));
            Assert.That(r["out3_db"], Is.EqualTo(-120));
        }
    }
}

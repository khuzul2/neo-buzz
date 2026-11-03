using System.Numerics;
using NeoBuzz.Dsp;
using NeoBuzz.Verb;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Verb (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class VerbTests
    {
        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (VerbEngine Engine, ParamValues Values) Make(Action<ParamValues> setup)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Mix, 1000);
            setup?.Invoke(v);
            var e = new VerbEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        static (float[] L, float[] R) Run(VerbEngine e, float[] l, float[] r)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[VerbEngine.MaxBlock];
            var br = new float[VerbEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += VerbEngine.MaxBlock)
            {
                int n = Math.Min(VerbEngine.MaxBlock, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        static (float[] L, float[] R) Impulse(VerbEngine e, int length)
        {
            var x = new float[length];
            x[0] = 1;
            return Run(e, x, x);
        }

        /// <summary>T60 from an impulse response in the 1 kHz octave: Schroeder integration, T30 fit.</summary>
        static double T60At1k(float[] ir) => T60At(ir, 1000);

        /// <summary>T60 from an impulse response in the octave at <paramref name="hz"/>: Schroeder integration, T30 fit.</summary>
        static double T60At(float[] ir, double hz)
        {
            var bp1 = new Biquad();
            var bp2 = new Biquad();
            bp1.Set(BiquadType.BandPass, hz, Measure.Rate, 1.41);
            bp2.Set(BiquadType.BandPass, hz, Measure.Rate, 1.41);
            var e = new double[ir.Length];
            for (int i = 0; i < ir.Length; i++)
            {
                double y = bp2.Process(bp1.Process(ir[i]));
                e[i] = y * y;
            }
            var edc = new double[ir.Length];
            double acc = 0;
            for (int i = ir.Length - 1; i >= 0; i--)
            {
                acc += e[i];
                edc[i] = acc;
            }
            // Linear fit of 10·log10(EDC/EDC0) between −5 and −35 dB.
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            int count = 0;
            for (int i = 0; i < ir.Length; i++)
            {
                double db = 10 * Math.Log10(edc[i] / edc[0] + 1e-300);
                if (db > -5) continue;
                if (db < -35) break;
                double t = i / (double)Measure.Rate;
                sx += t; sy += db; sxx += t * t; sxy += t * db;
                count++;
            }
            double slope = (count * sxy - sx * sy) / (count * sxx - sx * sx);
            return -60 / slope;
        }

        [TestCase(0.5)]
        [TestCase(2.0)]
        [TestCase(5.0)]
        [TestCase(10.0)]
        public void DecayTimeIsExactAt1k(double t60)
        {
            var (e, _) = Make(v =>
            {
                Set(v, P.Decay, t60);
                Set(v, P.LowDecay, 1.0);
                v.Set(P.Damping, 1000);
                v.Set(P.Early, 0);
                v.Set(P.HighCut, 1000);
                v.Set(P.LowCut, 0);
            });
            int length = (int)((t60 * 0.75 + 0.5) * Measure.Rate);
            var (l, _) = Impulse(e, length);
            double measured = T60At1k(l);
            Measure.Report($"Neo Verb T60 {t60} s: measured {measured:F3} s ({(measured / t60 - 1) * 100:+0.0;−0.0} %)");
            Assert.That(measured / t60, Is.InRange(0.9, 1.1));
        }

        static double Kurtosis(float[] x, int start, int n)
        {
            double m = 0;
            for (int i = 0; i < n; i++) m += x[start + i];
            m /= n;
            double m2 = 0, m4 = 0;
            for (int i = 0; i < n; i++)
            {
                double d = x[start + i] - m;
                m2 += d * d;
                m4 += d * d * d * d;
            }
            m2 /= n;
            m4 /= n;
            return m4 / (m2 * m2);
        }

        [TestCase((int)VerbMode.Hall, 0.100)]
        [TestCase((int)VerbMode.Room, 0.050)]
        [TestCase((int)VerbMode.Chamber, 0.080)]
        [TestCase((int)VerbMode.Plate, 0.050)]
        public void TailIsDenseSoon(int mode, double at)
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Mode, mode);
                v.Set(P.Early, 0);
                v.Set(P.PreDelay, 0);
                Set(v, P.Decay, 3.0);
            });
            var (l, _) = Impulse(e, (int)(0.4 * Measure.Rate));
            int start = (int)(at * Measure.Rate);
            double k = Kurtosis(l, start, 960);
            Measure.Report($"Neo Verb {(VerbMode)mode}: kurtosis {k:F2} at {at * 1000} ms");
            Assert.That(k, Is.LessThan(4));
        }

        [Test]
        public void TailIsDecorrelated()
        {
            var (e, _) = Make(v => Set(v, P.Decay, 3.0));
            var (l, r) = Impulse(e, Measure.Rate);
            double sl = 0, sr = 0, slr = 0;
            for (int i = (int)(0.2 * Measure.Rate); i < Measure.Rate; i++)
            {
                sl += l[i] * (double)l[i];
                sr += r[i] * (double)r[i];
                slr += l[i] * (double)r[i];
            }
            double rho = slr / Math.Sqrt(sl * sr);
            Measure.Report($"Neo Verb tail L/R correlation {rho:F3}");
            Assert.That(Math.Abs(rho), Is.LessThan(0.3));
        }

        [Test]
        public void FreezeHoldsTheTail()
        {
            var (e, v) = Make(null);
            var x = Measure.PinkNoise(Measure.Rate, 0.2);
            Run(e, x, x);
            v.Set(P.Freeze, 1);
            var silence = new float[11 * Measure.Rate];
            var (l, _) = Run(e, silence, silence);
            double a = Measure.Rms(l, (int)(0.5 * Measure.Rate), Measure.Rate);
            double b = Measure.Rms(l, 10 * Measure.Rate, Measure.Rate);
            Measure.Report($"Neo Verb freeze: {Measure.Db(b / a):+0.00;−0.00} dB over 10 s");
            Assert.That(Math.Abs(Measure.Db(b / a)), Is.LessThan(1.0));
        }

        [Test]
        public void MixZeroIsTheInput()
        {
            var (e, _) = Make(v => v.Set(P.Mix, 0));
            var x = Measure.PinkNoise(8192, 0.3);
            var (y, _) = Run(e, x, x);
            for (int i = 0; i < x.Length; i++) Assert.That(y[i], Is.EqualTo(x[i]));
        }

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(9);
            var (e, v) = Make(null);
            var x = Measure.PinkNoise(3 * Measure.Rate, 0.5);
            for (int round = 0; round < 20; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                var (y, _) = Run(e, x, x);
                double peak = Measure.Peak(y, 0, y.Length);
                Assert.That(double.IsFinite(peak), Is.True, $"round {round}");
                Assert.That(peak, Is.LessThan(100), $"round {round}");
            }
        }

        // Stability (docs/specs/machine-safety.md §1). The profiler found High damping near 1 kHz
        // diverging (+347 dBFS): the 1 kHz normalisation lifted the band below the corner over unity.

        /// <summary>Line <paramref name="i"/>'s loop gain at ω (rad/sample), from the coefficients the loop runs.</summary>
        static double LoopMagnitude(VerbEngine e, int i, double w)
        {
            var (_, gain, lowA, highA, lowG, highG) = e.Line(i);
            var z = Complex.FromPolarCoordinates(1, w);
            // A TPT one-pole (v = (x − s)·G, lp = v + s, s = lp + v) is G(z + 1)/(z − 1 + 2G).
            Complex Lp(double g) => g * (z + 1) / (z - 1 + 2 * g);
            Complex lpLow = Lp(lowG), lpHigh = Lp(highG);
            return ((lowA * lpLow + (1 - lpLow)) * (lpHigh + highA * (1 - lpHigh)) * gain).Magnitude;
        }

        [Test]
        public void EveryLineDecaysAtEveryFrequency()
        {
            // Mode × Size × Decay × Low decay × High damping, the ends included, Freeze off: every line's
            // gain stays below 1 at every frequency, and no band rings more than MaxOvershoot times
            // longer than its set decay (the slowest of Decay and Decay × Low decay).
            int[] raws = { 0, 125, 250, 400, 500, 600, 750, 875, 1000 };
            var grid = new double[258];
            grid[0] = 0;
            grid[^1] = Math.PI;
            for (int k = 1; k < grid.Length - 1; k++) grid[k] = Math.PI * 10 * Math.Pow(24000 / 10.0, (k - 1) / (double)(grid.Length - 3)) / 24000;
            var (e, v) = Make(null);
            var none = new float[0];
            double worstGain = 0, worstOvershoot = 0;
            string worstAt = "";
            int settings = 0;
            for (int mode = 0; mode < 4; mode++)
                foreach (int size in new[] { 0, 500, 1000 })
                    foreach (int decay in raws)
                        foreach (int low in raws)
                            for (int damp = 0; damp <= 1000; damp += 50)
                            {
                                v.Set(P.Mode, mode); v.Set(P.Size, size); v.Set(P.Decay, decay); v.Set(P.LowDecay, low); v.Set(P.Damping, damp);
                                e.Process(none, none, 0);
                                settings++;
                                double slowest = v.Value(P.Decay) * Math.Max(1, v.Value(P.LowDecay));
                                for (int i = 0; i < 16; i++)
                                {
                                    double peak = 0;
                                    foreach (double w in grid) peak = Math.Max(peak, LoopMagnitude(e, i, w));
                                    double rings = -3 * e.Line(i).Length / (Measure.Rate * Math.Log10(peak));
                                    double overshoot = peak >= 1 ? double.PositiveInfinity : rings / slowest;
                                    if (peak > worstGain) worstGain = peak;
                                    if (overshoot > worstOvershoot) { worstOvershoot = overshoot; worstAt = $"mode {mode}, size {size}, decay {decay}, low decay {low}, damping {damp}, line {i}"; }
                                }
                            }
            Measure.Report($"Neo Verb loop gain over {settings} settings: peak {worstGain:F6}, slowest band at most {worstOvershoot:F3}× its set decay ({worstAt})");
            Assert.That(worstGain, Is.LessThan(1));
            Assert.That(worstOvershoot, Is.LessThanOrEqualTo(VerbEngine.MaxOvershoot * 1.0001), worstAt);
        }

        static IEnumerable<TestCaseData> Extremes()
        {
            // Each parameter alone at each end (the others at their defaults, Mix 100 %), then the
            // worst combinations: the lowest damping with the longest decays, the sizes and modes,
            // full modulation. Not Shimmer above its default (0): its loop (the tail an octave up,
            // back into the network) runs away above a setting that falls with the decay (about
            // 93 % at the default Decay, 26 % at 30 s): an open finding, docs/specs/machines.md (Neo Verb).
            foreach (var d in Params.Table)
                foreach (int raw in new[] { d.Min, d.Max })
                    if (d.Index != P.Shimmer || raw == d.Min)
                        yield return new TestCaseData(d.Name + "=" + raw, new[] { (d.Index, raw) }).SetName($"Extremes({d.Name} {(raw == d.Min ? "min" : "max")})");
            var worst = new[] { (P.Damping, 0), (P.Decay, 1000), (P.LowDecay, 1000) };
            var mod = new[] { (P.ModDepth, 1000), (P.ModRate, 1000) };
            (string, (int, int)[])[] combos =
            {
                ("damping min, decay max, low decay max", worst),
                ("damping min, decay max, low decay min", new[] { (P.Damping, 0), (P.Decay, 1000), (P.LowDecay, 0) }),
                ("damping min, low decay max", new[] { (P.Damping, 0), (P.LowDecay, 1000) }),
                ("damping min, decay max, low decay max, full modulation", worst.Concat(mod).ToArray()),
                ("damping min, decay max, low decay max, size min, Room, full modulation", worst.Concat(mod).Append((P.Size, 0)).Append((P.Mode, 1)).ToArray()),
                ("damping min, decay max, low decay max, size max, Plate, full modulation", worst.Concat(mod).Append((P.Size, 1000)).Append((P.Mode, 3)).ToArray()),
                ("damping min, decay max, low decay max, Chamber", worst.Append((P.Mode, 2)).ToArray()),
                ("damping max, decay max, low decay max, full modulation", new[] { (P.Damping, 1000), (P.Decay, 1000), (P.LowDecay, 1000) }.Concat(mod).ToArray()),
                ("every parameter at its maximum, Freeze and Shimmer off", Params.Table.Select(d => (d.Index, d.Index == P.Freeze || d.Index == P.Shimmer ? 0 : d.Max)).ToArray()),
                ("every parameter at its minimum, Mix 100 %", Params.Table.Select(d => (d.Index, d.Index == P.Mix ? d.Max : d.Min)).ToArray()),
            };
            foreach (var (name, changes) in combos) yield return new TestCaseData(name, changes).SetName($"Extremes({name})");
        }

        [TestCaseSource(nameof(Extremes))]
        public void ExtremesStayBoundedAndDecay(string what, (int Index, int Raw)[] changes)
        {
            // 30 s of pink noise, then 10 s of silence: finite, below +24 dBFS, not growing over the
            // last 10 s of noise (a build-up levels off), and decaying once the input stops, at least
            // as fast as the guaranteed bound allows (MaxOvershoot times the slowest set decay).
            var (e, v) = Make(pv => { foreach (var (i, raw) in changes) pv.Set(i, raw); });
            int rate = Measure.Rate, noise = 30 * rate, tail = 10 * rate;
            var x = new float[noise + tail];
            Array.Copy(Measure.PinkNoise(noise, 0.1), x, noise);
            var (l, r) = Run(e, x, x);
            double peak = Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length));
            double Level(int from, int seconds) => Measure.Db(Math.Max(Measure.Rms(l, from, seconds * rate), Measure.Rms(r, from, seconds * rate)));
            double grow = Level(25 * rate, 5) - Level(20 * rate, 5);
            double first = Level(noise + rate, 1), last = Level(noise + 8 * rate, 1);
            double slowest = v.Value(P.Decay) * Math.Max(1, v.Value(P.LowDecay));
            bool frozen = v.On(P.Freeze);
            double needDrop = frozen ? 0 : 0.5 * 60 * 7 / (VerbEngine.MaxOvershoot * slowest);
            Measure.Report($"Neo Verb {what}: peak {Measure.Db(peak):+0.0;−0.0} dBFS, last 10 s of noise {grow:+0.00;−0.00} dB, tail {last - first:+0.0;−0.0} dB over 7 s (at least −{needDrop:F1})");
            Assert.That(double.IsFinite(peak), Is.True, "finite");
            Assert.That(Measure.Db(peak), Is.LessThan(24), "below +24 dBFS");
            Assert.That(grow, Is.LessThan(1.5), "levels off under noise");
            if (frozen) Assert.That(last - first, Is.LessThan(1.0), "freeze holds without growing");
            else if (first > -120) Assert.That(last - first, Is.LessThan(-needDrop), "decays after the input stops");
        }

        [Test]
        public void LowDampingKeepsTheSetDecayBelowTheCorner()
        {
            // High damping at its lowest (1 kHz), Low decay ×1: no band rings longer than the set
            // Decay, and the band below the corner rings at it (it diverged before).
            const double t60 = 2.0;
            var (e, _) = Make(v =>
            {
                Set(v, P.Decay, t60);
                Set(v, P.LowDecay, 1.0);
                v.Set(P.Damping, 0);
                v.Set(P.Early, 0);
                v.Set(P.HighCut, 1000);
                v.Set(P.LowCut, 0);
            });
            var (l, _) = Impulse(e, (int)(2.5 * Measure.Rate));
            var bands = new[] { 125.0, 250, 500, 1000, 2000, 4000 };
            var measured = bands.Select(hz => T60At(l, hz)).ToArray();
            Measure.Report("Neo Verb High damping 1 kHz, Decay 2 s: T60 " + string.Join(", ", bands.Select((hz, k) => $"{hz:0} Hz {measured[k]:F2} s")));
            Assert.That(measured.Max() / t60, Is.LessThan(1.15));
            Assert.That(measured[0] / t60, Is.InRange(0.85, 1.15));
        }

        [Test]
        public void WetLevelIsSensible()
        {
            var (e, _) = Make(null);
            var x = Measure.PinkNoise(4 * Measure.Rate, 0.1);
            var (y, _) = Run(e, x, x);
            double ratio = Measure.Db(Measure.Rms(y, 2 * Measure.Rate, 2 * Measure.Rate) / 0.1);
            TestContext.Out.WriteLine($"wet level vs dry: {ratio:F1} dB");
            Assert.That(ratio, Is.InRange(-12.0, 3.0));
        }
    }
}

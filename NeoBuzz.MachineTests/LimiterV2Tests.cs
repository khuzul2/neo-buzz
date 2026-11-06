using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Limiter;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Limiter v2 (docs/specs/mastering-tools.md §5.2): its stages, and each style against v1 ("Classic").</summary>
    [TestFixture]
    public class LimiterV2Tests
    {
        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (LimiterEngine Engine, ParamValues Values) Make(Action<ParamValues>? setup = null, int rate = Measure.Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new LimiterEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static (float[] L, float[] R) Run(LimiterEngine e, float[] l, float[] r, int block = LimiterEngine.MaxBlock)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[LimiterEngine.MaxBlock];
            var br = new float[LimiterEngine.MaxBlock];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        /// <summary>The styles, as the "Style —" presets set the stages (Presets.txt); Classic is v1.</summary>
        internal static readonly (string Name, Action<ParamValues> Stages)[] Styles =
        {
            ("Classic", v => { }),
            ("Transparent", v =>
            {
                Set(v, P.Lookahead, 5);
                Set(v, P.SlowStage, 0.5);
                Set(v, P.SlowRelease, 1.5);
                Set(v, P.Unlink, 0.5);
            }),
            ("Punchy", v =>
            {
                Set(v, P.Transients, 3);
                Set(v, P.Unlink, 0.5);
                Set(v, P.SlowStage, 0.3);
            }),
            ("Dense", v =>
            {
                Set(v, P.Upward, 3);
                Set(v, P.UpThreshold, -18);
                v.Set(P.SoftClip, 2);
                Set(v, P.SlowStage, 0.4);
            }),
            ("Loud", v =>
            {
                v.Set(P.SoftClip, 1);
                Set(v, P.Transients, 1.5);
                Set(v, P.SlowStage, 0.3);
            }),
        };

        // ---- the stages -----------------------------------------------------------------------------

        [Test]
        public void TheSoftClipKeepsTheTruePeakUnderTheCeilingAtEveryKneeAndRate()
        {
            double worst = double.NegativeInfinity;
            var lines = new List<string>();
            foreach (int rate in new[] { 44100, 48000, 96000 })
                foreach (int knee in new[] { 1, 2, 3 })
                {
                    var (e, _) = Make(v =>
                    {
                        Set(v, P.InputGain, 12);
                        Set(v, P.Ceiling, -1);
                        v.Set(P.SoftClip, knee);
                    }, rate);
                    var (l, r) = LimiterTests.Drums(rate * 2);
                    var (ol, or) = Run(e, l, r);
                    double tp = 20 * Math.Log10(Math.Max(NeoBuzz.Analysis.Loudness.TruePeak(ol), NeoBuzz.Analysis.Loudness.TruePeak(or)));
                    worst = Math.Max(worst, tp + 1);
                    lines.Add($"{rate / 1000.0} kHz knee {new[] { 0, 3, 9, 30 }[knee]} dB {tp + 1:+0.000;-0.000}");
                    Assert.That(tp, Is.LessThanOrEqualTo(-1 + 0.1), $"{rate} Hz, knee {knee}");
                }
            Measure.Report($"Neo Limiter v2 soft clip, drums +12 dB into −1 dBTP: true peak against the ceiling, worst {worst:+0.000;-0.000} dB (≤ +0.1): " + string.Join(", ", lines));
        }

        [Test]
        public void TheSoftClipStageAliasesLittle()
        {
            const int bin = 4231;   // 3098.9 Hz
            var lines = new List<string>();
            // +12 dB into the knee: the peak 12 dB over where the curve starts to bend (12 − knee over the ceiling), and, for
            // comparison, 12 dB over the ceiling.
            var worstInto = double.NegativeInfinity;
            foreach (int knee in new[] { 1, 2, 3 })
                foreach (bool intoKnee in new[] { true, false })
                {
                    double kneeDb = new[] { 0.0, 3, 9, 30 }[knee];
                    var (e, _) = Make(v =>
                    {
                        Set(v, P.InputGain, intoKnee ? 12 - kneeDb : 12);
                        Set(v, P.Ceiling, 0);
                        v.Set(P.SoftClip, knee);
                        Set(v, P.Lookahead, 5);
                    });
                    var x = Measure.Sine(Measure.BinFrequency(bin), 8192 + Measure.N, 1.0);
                    var (y, _) = Run(e, x, (float[])x.Clone());
                    double floor = Measure.AliasFloorDb(Measure.Spectrum(y, 8192), bin);
                    lines.Add($"knee {kneeDb} dB {(intoKnee ? "+12 dB into the knee" : "+12 dB over the ceiling")} {floor:F1} dB");
                    if (intoKnee) worstInto = Math.Max(worstInto, floor);
                }
            Assert.That(worstInto, Is.LessThan(-90), "the clip stage, +12 dB into the knee");
            var (c, _) = Make(v => { Set(v, P.InputGain, 12); Set(v, P.Ceiling, 0); Set(v, P.Lookahead, 5); });
            var xc = Measure.Sine(Measure.BinFrequency(bin), 8192 + Measure.N, 1.0);
            var (yc, _) = Run(c, xc, (float[])xc.Clone());
            double classic = Measure.AliasFloorDb(Measure.Spectrum(yc, 8192), bin);
            Measure.Report($"Neo Limiter v2, a 3.1 kHz sine +12 dB into the ceiling: alias floor with the soft clip {string.Join(", ", lines)}; Classic (no clip) {classic:F1} dB");
        }

        [Test]
        public void TheSoftClipAddsItsLatencyAndCrossfadesNothingElse()
        {
            var (off, _) = Make();
            var (on, v) = Make(v0 => v0.Set(P.SoftClip, 1));
            Assert.That(on.Latency, Is.EqualTo(off.Latency + LimiterEngine.ClipLatency));
            Assert.That(LimiterEngine.ClipLatency, Is.EqualTo(58));
            // Under the knee, the soft clip passes the signal: an impulse comes out at the latency.
            var x = new float[2048];
            x[300] = 0.01f;
            var (y, _) = Run(on, x, (float[])x.Clone());
            int peak = 0;
            for (int i = 0; i < y.Length; i++) if (Math.Abs(y[i]) > Math.Abs(y[peak])) peak = i;
            Assert.That(peak - 300, Is.EqualTo(on.Latency));
        }

        [Test]
        public void UpwardLiftsQuietPassagesOnlyAndNotSilence()
        {
            // −30 dBFS RMS noise, then −14 dBFS: with a −20 dB threshold and 6 dB, the quiet part rises 5 dB (half the 10 dB
            // under), the loud one not at all; silence stays silent.
            int n = Measure.Rate * 8;
            var quiet = Measure.PinkNoise(n, Math.Pow(10, -30 / 20.0), 3);
            var loud = Measure.PinkNoise(n, Math.Pow(10, -14 / 20.0), 4);
            double Level(Action<ParamValues> st, float[] sig)
            {
                var (e, _) = Make(v => { Set(v, P.InputGain, 0); Set(v, P.Ceiling, 0); st(v); });
                var (y, _) = Run(e, sig, (float[])sig.Clone());
                return Measure.Db(Measure.Rms(y, n / 2, n / 2));
            }
            void Upward(ParamValues v) { Set(v, P.Upward, 6); Set(v, P.UpThreshold, -20); }
            double q = Level(Upward, quiet) - Level(_ => { }, quiet);
            double l = Level(Upward, loud) - Level(_ => { }, loud);
            var (es, _) = Make(v => Upward(v));
            var silence = new float[Measure.Rate];
            var (ys, _) = Run(es, silence, silence);
            Measure.Report($"Neo Limiter v2 Upward 6 dB under −20 dB: a −30 dBFS passage {q:+0.00;-0.00} dB, a −14 dBFS one {l:+0.00;-0.00} dB, silence stays silent");
            Assert.That(q, Is.EqualTo(5).Within(0.6));
            Assert.That(Math.Abs(l), Is.LessThan(0.1));
            Assert.That(ys.All(s => s == 0), Is.True);
        }

        [Test]
        public void TransientsRaiseHitsNotTheirTails()
        {
            // Drums under the ceiling (no limiting): +6 dB of emphasis lifts the first milliseconds of each hit; the tails stay.
            var (l, r) = LimiterTests.Drums(Measure.Rate * 2);
            for (int i = 0; i < l.Length; i++) { l[i] *= 0.1f; r[i] *= 0.1f; }
            var (e0, _) = Make(v => { Set(v, P.InputGain, 0); Set(v, P.Ceiling, 0); });
            var (e1, _) = Make(v => { Set(v, P.InputGain, 0); Set(v, P.Ceiling, 0); Set(v, P.Transients, 6); });
            var (y0, _) = Run(e0, l, r);
            var (y1, _) = Run(e1, l, r);
            int lat = e0.Latency, hit = Measure.Rate / 2 + lat;
            double onset = Measure.Db(Measure.Peak(y1, hit, 96) / Measure.Peak(y0, hit, 96));
            double tail = Measure.Db(Measure.Rms(y1, hit + 6000, 2000) / Measure.Rms(y0, hit + 6000, 2000));
            Measure.Report($"Neo Limiter v2 Transients +6 dB: a kick's first 2 ms {onset:+0.0;-0.0} dB, its tail 125 ms on {tail:+0.00;-0.00} dB");
            Assert.That(onset, Is.GreaterThan(3));
            Assert.That(Math.Abs(tail), Is.LessThan(0.3));
        }

        [Test]
        public void TransientUnlinkLetsTheOtherSideAlone()
        {
            // A hit on the left over a steady tone on both: linked, the right ducks with the left; unlinked on transients, less.
            int n = Measure.Rate;
            var l = Measure.Sine(220, n, 0.3);
            var r = Measure.Sine(220, n, 0.3);
            for (int i = 0; i < 2000; i++) l[n / 2 + i] += (float)(0.9 * Math.Exp(-i / 300.0) * Math.Sin(i * 0.3));
            double Duck(double unlink)
            {
                var (e, _) = Make(v => { Set(v, P.InputGain, 6); Set(v, P.Ceiling, -1); Set(v, P.Unlink, unlink); });
                var (_, yr) = Run(e, l, r);
                int at = n / 2 + e.Latency;
                return Measure.Db(Measure.Rms(yr, at, 600) / Measure.Rms(yr, at - 4000, 2000));
            }
            double linked = Duck(0), unlinked = Duck(1);
            Measure.Report($"Neo Limiter v2 Transient unlink: the right side under a left-only hit {linked:+0.00;-0.00} dB linked, {unlinked:+0.00;-0.00} dB at 100 %");
            Assert.That(unlinked, Is.GreaterThan(linked + 1));
        }

        [Test]
        public void TheSlowStageHoldsTheReductionBetweenHits()
        {
            var (s0, s1) = (Pumping(Styles[0].Stages), Pumping(v => Set(v, P.SlowStage, 0.6)));
            Measure.Report($"Neo Limiter v2 slow stage 60 %: a 1 kHz tone's sidebands under a 50 Hz kick driving 6 dB {s0:F1} → {s1:F1} dB");
            Assert.That(s1, Is.LessThan(s0 - 2), "less pumping");
        }

        [Test]
        public void UnityListenPlaysAtTheInputsLoudness()
        {
            var (l, r) = LimiterTests.Drums(Measure.Rate * 6);
            var (e, _) = Make(v => { Set(v, P.InputGain, 9); v.Set(P.Unity, 1); });
            var (yl, yr) = Run(e, l, r);
            int from = Measure.Rate * 4;
            double a = NeoBuzz.Analysis.Loudness.Measure(l[from..], r[from..], Measure.Rate).Integrated;
            double b = NeoBuzz.Analysis.Loudness.Measure(yl[from..], yr[from..], Measure.Rate).Integrated;
            Measure.Report($"Neo Limiter v2 Unity listen, +9 dB in: the output {b - a:+0.00;-0.00} LU from the input (gain match {e.GainMatchDb:F2} dB)");
            Assert.That(Math.Abs(b - a), Is.LessThan(0.5));
        }

        [Test]
        public void DeltaIsWhatTheLimitingTakesAway()
        {
            var (l, r) = LimiterTests.Drums(Measure.Rate);
            var (wet, _) = Make(v => Set(v, P.InputGain, 9));
            var (on, _) = Make(v => { Set(v, P.InputGain, 9); v.Set(P.Delta, 1); });
            var (yw, _) = Run(wet, l, r);
            var (yd, _) = Run(on, l, r);
            int L = wet.Latency;
            float g = (float)DspMath.DbToGain(Params.Table[P.InputGain].Value(Params.Table[P.InputGain].Raw(9)));
            double worst = 0;
            for (int i = L; i < l.Length; i++) worst = Math.Max(worst, Math.Abs(yd[i] - (yw[i] - l[i - L] * g) / g));
            Measure.Report($"Neo Limiter v2 Delta: the output minus the driven input, at the input's level, within {worst:E1}");
            Assert.That(worst, Is.LessThan(1e-6));
        }

        [Test]
        public void TheStagesDontDependOnBlocksAndAllocateNothing()
        {
            var (l, r) = LimiterTests.Drums(Measure.Rate);
            void Everything(ParamValues v)
            {
                Set(v, P.InputGain, 9);
                Set(v, P.Upward, 3);
                Set(v, P.Transients, 3);
                v.Set(P.SoftClip, 2);
                Set(v, P.ClipMix, 0.7);
                Set(v, P.SlowStage, 0.5);
                Set(v, P.Unlink, 0.5);
                v.Set(P.Unity, 1);
            }
            float[]? reference = null;
            foreach (int block in new[] { 256, 128, 117, 16 })
            {
                var (e, _) = Make(Everything);
                var (y, _) = Run(e, l, r, block);
                if (reference == null) reference = y;
                else Assert.That(y, Is.EqualTo(reference), $"blocks of {block}");
            }
            var (ea, _) = Make(Everything);
            var (bl0, br0) = LimiterTests.Drums(256 * 2100);
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                Array.Copy(bl0, b * 256, bl, 0, 256);
                Array.Copy(br0, b * 256, br, 0, 256);
                ea.Process(bl, br, 256);
            }
            for (int b = 0; b < 50; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, b => Block(b + 50));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void RunsFasterThanRealTimeWithEveryStage()
        {
            static void Setup(ParamValues v)
            {
                Set(v, P.InputGain, 9);
                Set(v, P.Upward, 3);
                Set(v, P.Transients, 3);
                v.Set(P.SoftClip, 2);
                Set(v, P.SlowStage, 0.5);
                Set(v, P.Unlink, 0.5);
            }
            // The card asks 50× (M3: Upward on a 16-sample grid, the transient split's allpasses a section at a time, the
            // FastOversampler and the block ADAA took it from about 30× to about 60× as timed on the 2-core test machine). A
            // throughput guard (Throughput.cs): timed against the reference workload in the same run, 1 s untimed, then 2 s.
            // At the reference's speed: the 2-core Xeon 88, the Linux runner 91, the Mac runner 66.
            var (l, r) = LimiterTests.Drums(Measure.Rate * 3);
            const int Warm = Measure.Rate;
            Throughput.Guard("Neo Limiter v2 CPU (every stage, the soft clip at 4×, drums)", Throughput.Floor(x64: 64, arm64: 50), 2, Measure.Rate,
                LimiterEngine.MaxBlock, () =>
            {
                var (e, _) = Make(Setup);
                var bl = new float[LimiterEngine.MaxBlock];
                var br = new float[LimiterEngine.MaxBlock];
                void Block(int pos, int n)
                {
                    Array.Copy(l, pos, bl, 0, n);
                    Array.Copy(r, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                for (int pos = 0; pos < Warm; pos += LimiterEngine.MaxBlock) Block(pos, LimiterEngine.MaxBlock);
                return Throughput.InBlocks(LimiterEngine.MaxBlock, (pos, n) => Block(Warm + pos, n));
            }, Measure.Report);
        }

        // ---- the styles against v1 --------------------------------------------------------------------

        const int ImdN = 65536;
        const int LowBin = 82, HighBin = 9557;   // 60.06 Hz and 6999.6 Hz at 48 kHz

        /// <summary>SMPTE IMD (60 Hz + 7 kHz, 4:1) through a style, its peaks reduced by <paramref name="reductionDb"/>: the sidebands' RMS against 7 kHz, %.</summary>
        static double Imd(Action<ParamValues> style, double reductionDb, out double lufs)
        {
            int n = 16384 + ImdN;
            var x = new float[n];
            double lo = Measure.BinFrequency(LowBin), hi = Measure.BinFrequency(HighBin);
            for (int i = 0; i < n; i++)
                x[i] = (float)(0.8 * Math.Sin(2 * Math.PI * lo * i / Measure.Rate) + 0.2 * Math.Sin(2 * Math.PI * hi * i / Measure.Rate));
            var (e, _) = Make(v =>
            {
                style(v);
                Set(v, P.Ceiling, -1);
                Set(v, P.InputGain, reductionDb - 1);
            });
            var (y, _) = Run(e, x, (float[])x.Clone());
            var mag = Measure.Spectrum(y, 16384, ImdN);
            double side = 0;
            for (int k = 1; k <= 6; k++)
                foreach (int b in new[] { HighBin - k * LowBin, HighBin + k * LowBin }) side += mag[b] * mag[b];
            lufs = NeoBuzz.Analysis.Loudness.Measure(y[16384..], y[16384..], Measure.Rate).Integrated;
            return 100 * Math.Sqrt(side) / mag[HighBin];
        }

        /// <summary>A 1 kHz tone (−20 dBFS) under a 50 Hz kick twice a second driving 6 dB of reduction: its AM sidebands (1–40 Hz off), dB.</summary>
        static double Pumping(Action<ParamValues> style)
        {
            int n = 16384 + ImdN;
            const int carrier = 1365;   // 999.7 Hz
            var x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Measure.Rate, since = t % 0.5;
                x[i] = (float)(0.1 * Math.Sin(2 * Math.PI * Measure.BinFrequency(carrier) * t) + 0.9 * Math.Exp(-since / 0.08) * Math.Sin(2 * Math.PI * 50 * since));
            }
            var (e, _) = Make(v =>
            {
                style(v);
                Set(v, P.Ceiling, -1);
                Set(v, P.InputGain, 5);
            });
            var (y, _) = Run(e, x, (float[])x.Clone());
            var mag = Measure.Spectrum(y, 16384, ImdN);
            double side = 0;
            int from = (int)Math.Round(1.0 * ImdN / Measure.Rate), to = (int)Math.Round(40.0 * ImdN / Measure.Rate);
            for (int k = Math.Max(2, from); k <= to; k++) side += mag[carrier - k] * mag[carrier - k] + mag[carrier + k] * mag[carrier + k];
            return Measure.Db(Math.Sqrt(side) / mag[carrier]);
        }

        /// <summary>The drum loop's integrated loudness through a style at an input gain, into −1 dBTP.</summary>
        static double DrumsLufs(Action<ParamValues> style, double inputGainDb)
        {
            var (l, r) = LimiterTests.Drums(Measure.Rate * 4);
            var (e, _) = Make(v => { style(v); Set(v, P.Ceiling, -1); Set(v, P.InputGain, inputGainDb); });
            var (yl, yr) = Run(e, l, r);
            return NeoBuzz.Analysis.Loudness.Measure(yl, yr, Measure.Rate).Integrated;
        }

        [Test]
        public void EachStyleAgainstClassic()
        {
            var lines = new List<string>();
            var imdAt = new Dictionary<string, double[]>();
            foreach (var (name, style) in Styles)
            {
                var imd = new[] { 3.0, 6, 9 }.Select(db => Imd(style, db, out _)).ToArray();
                imdAt[name] = imd;
                double pump = Pumping(style);
                // Loudness at a distortion budget: the most drive whose IMD stays at 1 %, and the loudness it reaches.
                double loGain = 0, hiGain = 24;
                for (int k = 0; k < 12; k++)
                {
                    double mid = 0.5 * (loGain + hiGain);
                    if (Imd(style, mid, out _) <= 1) loGain = mid; else hiGain = mid;
                }
                double lufs = DrumsLufs(style, loGain - 1);
                lines.Add($"{name}: IMD {imd[0]:F2} / {imd[1]:F2} / {imd[2]:F2} % at 3 / 6 / 9 dB, pumping {pump:F1} dB, 1 % IMD at {loGain:F1} dB on the peaks (drums there: {lufs:F1} LUFS)");
            }
            Measure.Report("Neo Limiter v2 styles (SMPTE IMD 60 Hz + 7 kHz 4:1; a 1 kHz tone's sidebands under a kick driving 6 dB; the drive at 1 % IMD): " + string.Join("; ", lines));
            foreach (var (name, imd) in imdAt)
                Assert.That(imd.All(double.IsFinite), Is.True, name);
        }
    }
}

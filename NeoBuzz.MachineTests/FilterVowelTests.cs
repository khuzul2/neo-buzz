using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Filter;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Filter's vowel mode (EXT1, docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class FilterVowelTests
    {
        const int Rate = Measure.Rate;
        static readonly int[] VowelRaw = { 0, 250, 500, 750, 1000 };

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (FilterEngine Engine, ParamValues Values) Make(Action<ParamValues> setup, double tempo = 120)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Type, 7);
            Set(v, P.Cutoff, 1000);
            setup?.Invoke(v);
            var e = new FilterEngine(v) { Tempo = tempo };
            e.SetSampleRate(Rate);
            return (e, v);
        }

        static (float[] L, float[] R) Run(FilterEngine e, float[] l, float[] r, int block = FilterEngine.MaxBlock, Action<int> before = null, bool transport = false)
        {
            var ol = new float[l.Length];
            var or = new float[r.Length];
            var bl = new float[FilterEngine.MaxBlock];
            var br = new float[FilterEngine.MaxBlock];
            int samplesPerTick = (int)(60.0 * e.SampleRate / (e.Tempo * 4));
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                before?.Invoke(pos);
                if (transport)
                    e.Transport = new FilterTransport { Playing = true, Tick = pos / samplesPerTick, PosInTick = pos % samplesPerTick, SamplesPerTick = samplesPerTick, TicksPerBeat = 4 };
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, ol, pos, n);
                Array.Copy(br, 0, or, pos, n);
            }
            return (ol, or);
        }

        /// <summary>A band-limited sawtooth: harmonic k at 1/k, up to 20 kHz.</summary>
        internal static float[] Saw(double hz, int length, double amplitude)
        {
            var x = new double[length];
            for (int k = 1; k * hz < 20000; k++)
            {
                double w = 2 * Math.PI * k * hz / Rate, a = amplitude * 2 / Math.PI / k;
                for (int i = 0; i < length; i++) x[i] += a * Math.Sin(w * i);
            }
            return x.Select(d => (float)d).ToArray();
        }

        // ------------------------------------------------------------------ formants

        /// <summary>The spectral envelope of a harmonic signal: its gain at each harmonic of <paramref name="f0"/> (output over a 1/k saw's harmonic), dB.</summary>
        static double[] HarmonicGains(float[] y, double f0, int harmonics)
        {
            const int n = 65536;
            var w = new float[n];
            int from = y.Length - n;
            for (int i = 0; i < n; i++) w[i] = y[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Fft.Magnitudes(w, 0, n);
            var g = new double[harmonics + 1];
            for (int k = 1; k <= harmonics; k++)
            {
                int bin = (int)Math.Round(k * f0 * n / Rate);
                double m = 0;
                for (int b = bin - 3; b <= bin + 3; b++) m = Math.Max(m, mag[b]);
                g[k] = 20 * Math.Log10(Math.Max(1e-12, m * k));
            }
            return g;
        }

        /// <summary>
        /// The first two peaks (Hz) of the envelope: the harmonics' gains joined by a smooth (Catmull-Rom) curve in log frequency, its
        /// local maxima that stand at least 0.5 dB above the valleys either side, within 40 dB of the highest.
        /// </summary>
        internal static (double F1, double F2) FirstTwoPeaks(double[] g, double f0)
        {
            int count = g.Length - 1;
            var xs = new double[count];
            for (int k = 1; k <= count; k++) xs[k - 1] = Math.Log2(k * f0);
            const int Steps = 40;
            var cx = new List<double>();
            var cy = new List<double>();
            for (int k = 1; k < count; k++)
            {
                double y0 = g[Math.Max(1, k - 1)], y1 = g[k], y2 = g[k + 1], y3 = g[Math.Min(count, k + 2)];
                for (int s = 0; s < Steps; s++)
                {
                    double t = s / (double)Steps;
                    cy.Add(0.5 * ((2 * y1) + (-y0 + y2) * t + (2 * y0 - 5 * y1 + 4 * y2 - y3) * t * t + (-y0 + 3 * y1 - 3 * y2 + y3) * t * t * t));
                    cx.Add(xs[k - 1] + (xs[k] - xs[k - 1]) * t);
                }
            }
            double top = cy.Max();
            var peaks = new List<double>();
            for (int i = 1; i < cy.Count - 1; i++)
            {
                if (!(cy[i] > cy[i - 1] && cy[i] >= cy[i + 1]) || cy[i] < top - 40) continue;
                double left = cy[i], right = cy[i];
                for (int j = i - 1; j >= 0 && cy[j] <= cy[i]; j--) left = Math.Min(left, cy[j]);
                for (int j = i + 1; j < cy.Count && cy[j] <= cy[i]; j++) right = Math.Min(right, cy[j]);
                if (cy[i] - Math.Max(left, right) < 0.5) continue;
                peaks.Add(Math.Pow(2, cx[i]));
            }
            return (peaks.Count > 0 ? peaks[0] : 0, peaks.Count > 1 ? peaks[1] : 0);
        }

        [Test]
        public void ASawThroughEachVowelAndVoiceHasItsFormantsWithinTwelvePercent()
        {
            const double f0 = 110;
            var x = Saw(f0, 4 * Rate, 0.25);
            var lines = new List<string>();
            double worst = 0;
            var problems = new List<string>();
            for (int voice = 0; voice < 3; voice++)
                for (int vowel = 0; vowel < 5; vowel++)
                {
                    var (e, _) = Make(v => { v.Set(P.Vowel, VowelRaw[vowel]); v.Set(P.Voice, voice); });
                    var (l, _) = Run(e, x, x);
                    var g = HarmonicGains(l, f0, 70);
                    var (p1, p2) = FirstTwoPeaks(g, f0);
                    double t1 = VowelDesign.Table[voice, vowel, 0], t2 = VowelDesign.Table[voice, vowel, 1];
                    double e1 = p1 / t1 - 1, e2 = p2 / t2 - 1;
                    worst = Math.Max(worst, Math.Max(Math.Abs(e1), Math.Abs(e2)));
                    lines.Add($"{VowelDesign.VoiceNames[voice]} {VowelDesign.VowelNames[vowel]}: F1 {p1:F0} Hz (table {t1:F0}, {e1 * 100:+0.0;-0.0} %), F2 {p2:F0} Hz (table {t2:F0}, {e2 * 100:+0.0;-0.0} %)");
                    if (Math.Abs(e1) > 0.12 || Math.Abs(e2) > 0.12) problems.Add(lines[^1]);
                }
            Measure.Report("Neo Filter vowel formants, 110 Hz saw, peaks of the spectral envelope against the table (worst " + $"{worst * 100:F1} %): " + string.Join("; ", lines));
            foreach (var l in lines) TestContext.Out.WriteLine(l);
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }

        // ------------------------------------------------------------------ design

        [Test]
        public void TheVowelMorphsInTheLogDomainBetweenNeighboursAndTheNamesFollow()
        {
            var f = new double[3];
            for (int voice = 0; voice < 3; voice++)
                for (int vowel = 0; vowel < 4; vowel++)
                {
                    VowelDesign.Frequencies((vowel + 0.5) / 4, voice, 0, f);
                    for (int k = 0; k < 3; k++)
                        Assert.That(f[k], Is.EqualTo(Math.Sqrt(VowelDesign.Table[voice, vowel, k] * VowelDesign.Table[voice, vowel + 1, k])).Within(1e-6), $"voice {voice}, {vowel}-{vowel + 1}, F{k + 1}: the geometric mean");
                }
            // The voices blend the same way, and 1 kHz is no shift while an octave of cutoff is an octave of formants.
            VowelDesign.Frequencies(0, 0.5, 0, f);
            Assert.That(f[0], Is.EqualTo(Math.Sqrt(VowelDesign.Table[0, 0, 0] * VowelDesign.Table[1, 0, 0])).Within(1e-6));
            VowelDesign.Frequencies(0.5, 0, 1, f);
            Assert.That(f[1], Is.EqualTo(2 * VowelDesign.Table[0, 2, 1]).Within(1e-6));
            Assert.That(VowelDesign.CutoffShift(1000), Is.EqualTo(0).Within(1e-12));
            Assert.That(VowelDesign.CutoffShift(20000), Is.EqualTo(2), "clamped to two octaves up");
            Assert.That(VowelDesign.CutoffShift(20), Is.EqualTo(-2), "and two down");
            Assert.That(VowelDesign.Describe(0), Is.EqualTo("A"));
            Assert.That(VowelDesign.Describe(0.5), Is.EqualTo("I"));
            Assert.That(VowelDesign.Describe(0.30), Does.StartWith("E").And.Contain("to I"));
            Assert.That(VowelDesign.Describe(1), Is.EqualTo("U"));
        }

        [Test]
        public void TheEnvelopeAndTheLfoMoveTheVowelAQuarterPerOctaveOrTheFormantsTogether()
        {
            // An LFO square wave of ±1 octave, free, 1 Hz. Vowel mod 100 %: the vowel moves a quarter (one step) each way and the
            // formants stay; Vowel mod 0 %: the formants shift an octave each way and the vowel stays; halfway: half of each.
            foreach (var (mod, steps, octaves) in new[] { (1000, 0.25, 0.0), (0, 0.0, 1.0), (500, 0.125, 0.5) })
            {
                var (e, _) = Make(v => { v.Set(P.Vowel, 500); v.Set(P.VowelMod, mod); v.Set(P.LfoShape, 4); v.Set(P.LfoRetrig, 0); Set(v, P.LfoRate, 1); Set(v, P.LfoDepth, 1); });
                double lo = 9, hi = -9, sLo = 9, sHi = -9;
                var x = new float[Rate / 4];
                for (int b = 0; b < 8; b++)
                {
                    Run(e, x, x);
                    lo = Math.Min(lo, e.VowelPosition(0)); hi = Math.Max(hi, e.VowelPosition(0));
                    sLo = Math.Min(sLo, e.VowelShiftNow(0)); sHi = Math.Max(sHi, e.VowelShiftNow(0));
                }
                Assert.That(hi - 0.5, Is.EqualTo(steps).Within(0.01), $"mod {mod / 10} %: the vowel's step up");
                Assert.That(0.5 - lo, Is.EqualTo(steps).Within(0.01), $"mod {mod / 10} %: the vowel's step down");
                Assert.That(sHi, Is.EqualTo(octaves).Within(0.01), $"mod {mod / 10} %: the formants up");
                Assert.That(sLo, Is.EqualTo(-octaves).Within(0.01), $"mod {mod / 10} %: the formants down");
            }
        }

        [Test]
        public void CutoffShiftsTheFormantsResonanceSharpensThemAndSlopeIsIgnored()
        {
            var x = Saw(110, 3 * Rate, 0.25);
            double[] Peaks(Action<ParamValues> setup)
            {
                var (e, _) = Make(setup);
                var (l, _) = Run(e, x, x);
                var (p1, p2) = FirstTwoPeaks(HarmonicGains(l, 110, 70), 110);
                return new[] { p1, p2 };
            }
            var flat = Peaks(v => v.Set(P.Vowel, 0));
            var up = Peaks(v => { v.Set(P.Vowel, 0); Set(v, P.Cutoff, 2000); });
            Assert.That(up[0] / flat[0], Is.EqualTo(2).Within(0.15), "an octave up in Cutoff, an octave up in F1");
            Assert.That(up[1] / flat[1], Is.EqualTo(2).Within(0.15), "and F2");
            // Slope makes no difference at all.
            var (e1, _) = Make(v => v.Set(P.Slope, 0));
            var (e2, _) = Make(v => v.Set(P.Slope, 1));
            var (a, _) = Run(e1, x, x);
            var (b, _) = Run(e2, x, x);
            Assert.That(a, Is.EqualTo(b), "Slope is ignored");
            // Resonance: a higher Q, narrower formants (the -3 dB width of F1 against the design).
            double Width(double q)
            {
                var f = new double[3]; var k = new double[3]; var g = new double[3];
                VowelDesign.Design(0, 0, 0, VowelDesign.BandwidthScale(q), Rate, f, k, g);
                double peak = VowelDesign.Magnitude(f, k, g, f[0], Rate);
                double lo = f[0], hi = f[0];
                while (VowelDesign.Magnitude(f, k, g, lo, Rate) > peak * 0.7071 && lo > 20) lo *= 0.999;
                while (VowelDesign.Magnitude(f, k, g, hi, Rate) > peak * 0.7071 && hi < 5000) hi *= 1.001;
                return hi - lo;
            }
            double w1 = Width(0.71), w2 = Width(1.42), w3 = Width(0.5);
            Assert.That(w1 / w2, Is.EqualTo(2).Within(0.4), $"Q 1.42 halves the formants' width ({w1:F0} Hz to {w2:F0} Hz)");
            Assert.That(w3, Is.GreaterThan(w1), "and Q 0.5 widens it");
        }

        // ------------------------------------------------------------------ level

        [Test]
        public void TheLevelStaysWithinThreeDbOfTheInputAtTheDefaultResonance()
        {
            var noise = Measure.PinkNoise(4 * Rate, DspMath.DbToGain(-18), 21);
            var saw = Saw(110, 4 * Rate, 0.1);
            var lines = new List<string>();
            double worstNoise = 0, worstSaw = 0;
            foreach (double cutoff in new[] { 1000.0, Params.Table[P.Cutoff].Value(Params.Table[P.Cutoff].Default) })
                for (int voice = 0; voice < 3; voice++)
                    for (int vowel = 0; vowel < 5; vowel++)
                    {
                        double Level(float[] x)
                        {
                            var (e, _) = Make(v => { Set(v, P.Cutoff, cutoff); v.Set(P.Vowel, VowelRaw[vowel]); v.Set(P.Voice, voice); });
                            var (l, r) = Run(e, x, x);
                            return Measure.Db(Measure.Rms(l, Rate, 3 * Rate) / Measure.Rms(x, Rate, 3 * Rate));
                        }
                        double n = Level(noise), s = Level(saw);
                        worstNoise = Math.Max(worstNoise, Math.Abs(n));
                        worstSaw = Math.Max(worstSaw, Math.Abs(s));
                        lines.Add($"{cutoff:F0} Hz {VowelDesign.VoiceNames[voice]} {VowelDesign.VowelNames[vowel]}: noise {n:+0.0;-0.0} dB, saw {s:+0.0;-0.0} dB");
                    }
            Measure.Report($"Neo Filter vowel level at the default Resonance (pink noise at -18 dBFS and a 110 Hz saw; worst {worstNoise:F1} dB noise, {worstSaw:F1} dB saw): " + string.Join("; ", lines));
            foreach (var l in lines) TestContext.Out.WriteLine(l);
            // The gate is pink noise (the input a mix is made of); a sawtooth is darker, and a formant filter's level follows the source's tilt, so
            // it is held to a looser bound and reported.
            Assert.That(worstNoise, Is.LessThanOrEqualTo(3.0), "pink noise");
            Assert.That(worstSaw, Is.LessThanOrEqualTo(4.0), "a 110 Hz saw");
        }

        // ------------------------------------------------------------------ movement

        static double HighEnergyDb(float[] y)
        {
            int n = 131072;
            var w = new float[n];
            int from = y.Length - n;
            for (int i = 0; i < n; i++) w[i] = y[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            var mag = Fft.Magnitudes(w, 0, n);
            double all = 0, high = 0;
            for (int k = 1; k < mag.Length; k++)
            {
                double p = mag[k] * mag[k];
                all += p;
                if (k * (double)Rate / n > 2000) high += p;
            }
            return 10 * Math.Log10(high / all + 1e-30);
        }

        [Test]
        public void AFullVowelSweepMakesNoZipperNoise()
        {
            // A 220 Hz sine at -12 dBFS, the Vowel control stepped to a new value every 256-sample block as automation arrives, A to U to A over
            // 6 s: the energy above 2 kHz against the whole (the sine and its tiny movement are far below that). The same sweep through three
            // plain band-passes switched once per block measures what zipper noise is.
            var x = Measure.Sine(220, 6 * Rate, 0.25);
            int blocks = 0, total = 6 * Rate / 256;
            var (e, v) = Make(null);
            var (l, _) = Run(e, x, x, before: pos =>
            {
                double t = blocks++ / (double)total;
                v.Set(P.Vowel, (int)Math.Round(1000 * (1 - Math.Abs(2 * t - 1))));
            });
            double ours = HighEnergyDb(l);
            var y = new float[x.Length];
            var ic = new double[3, 2];
            var f = new double[3]; var k = new double[3]; var a = new double[3];
            for (int b = 0; b * 256 < x.Length; b++)
            {
                double t = b / (double)total;
                VowelDesign.Design(1 - Math.Abs(2 * t - 1), 0, 0, 1, Rate, f, k, a);
                for (int i = b * 256; i < Math.Min(x.Length, b * 256 + 256); i++)
                {
                    double sum = 0;
                    for (int j = 0; j < 3; j++)
                    {
                        double g = Math.Tan(Math.PI * f[j] / Rate), a1 = 1 / (1 + g * (g + k[j])), a2 = g * a1, a3 = g * a2;
                        double v3 = x[i] - ic[j, 1], v1 = a1 * ic[j, 0] + a2 * v3, v2 = ic[j, 1] + a2 * ic[j, 0] + a3 * v3;
                        ic[j, 0] = 2 * v1 - ic[j, 0];
                        ic[j, 1] = 2 * v2 - ic[j, 1];
                        sum += a[j] * k[j] * v1;
                    }
                    y[i] = (float)sum;
                }
            }
            double switched = HighEnergyDb(y);
            Measure.Report($"Neo Filter vowel sweep (A - U - A in 6 s, a step per 256-sample block, 220 Hz sine): energy above 2 kHz {ours:F1} dB (the same switched per block: {switched:F1} dB)");
            Assert.That(ours, Is.LessThan(-60));
            Assert.That(ours, Is.LessThan(switched - 10), "far under what switching per block makes");
        }

        [Test]
        public void TheOutputDoesNotDependOnHowTheHostSplitsIt()
        {
            foreach (int retrig in new[] { 0, 1, 2 })
            {
                var x = Measure.PinkNoise(Rate, 0.3, 3);
                var y = Measure.PinkNoise(Rate, 0.3, 4);
                Action<ParamValues> setup = pv =>
                {
                    Set(pv, P.Resonance, 3); pv.Set(P.Drive, 120); Set(pv, P.EnvAmount, -2); Set(pv, P.EnvSens, 12); pv.Set(P.LfoSync, 9); Set(pv, P.LfoDepth, 2);
                    pv.Set(P.LfoSpread, 60); pv.Set(P.LfoShape, 5); pv.Set(P.LfoRetrig, retrig); pv.Set(P.Vowel, 500);
                };
                float[] Render(int block, bool changes)
                {
                    var (e, v) = Make(setup);
                    var (l, r) = Run(e, x, y, block, pos =>
                    {
                        if (!changes) return;
                        if (pos == 11776) Set(v, P.Cutoff, 400);
                        if (pos == 23296) v.Set(P.Voice, 2);
                        if (pos == 34816) v.Set(P.Vowel, 900);
                    }, transport: true);
                    return l.Concat(r).ToArray();
                }
                // Blocks of 128 and 16 start on the changes' samples; blocks of 117 don't, so steady they give the same.
                var reference = Render(256, true);
                foreach (int block in new[] { 128, 16 })
                    Assert.That(Render(block, true), Is.EqualTo(reference), $"retrig {retrig}, blocks of {block}");
                Assert.That(Render(117, false), Is.EqualTo(Render(256, false)), $"retrig {retrig}, blocks of 117");
            }
        }

        // ------------------------------------------------------------------ mix and amount

        [Test]
        public void MixAndAmountBehaveAsInTheOtherModes()
        {
            var x = Measure.PinkNoise(Rate, 0.3, 7);
            x[10] = -0f;
            x[11] = float.Epsilon;
            Action<ParamValues> on = v => { Set(v, P.Resonance, 3); v.Set(P.Vowel, 300); v.Set(P.Drive, 100); Set(v, P.EnvAmount, 2); Set(v, P.LfoDepth, 1); Set(v, P.LfoRate, 3); };
            // Amount 0 %: the input, bit for bit (as Mix 0 % with Output 0 dB is), and no latency.
            foreach (var (name, setup) in new (string, Action<ParamValues>)[]
                     { ("Amount 0 %", v => { on(v); v.Set(P.Amount, 0); }), ("Mix 0 %", v => { on(v); v.Set(P.Mix, 0); }) })
            {
                var (e, _) = Make(setup);
                var (l, r) = Run(e, x, x, block: 117);
                for (int i = 0; i < x.Length; i++)
                {
                    Assert.That(BitConverter.SingleToInt32Bits(l[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"{name}: left {i}");
                    Assert.That(BitConverter.SingleToInt32Bits(r[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(x[i])), $"{name}: right {i}");
                }
                Assert.That(e.Latency, Is.EqualTo(0));
            }
            // Mix 50 %: the straight crossfade of the dry and the wet.
            var (we, _) = Make(on);
            var (wet, _) = Run(we, x, x);
            var (me, _) = Make(v => { on(v); v.Set(P.Mix, 500); });
            var (mix, _) = Run(me, x, x);
            double worst = 0;
            for (int i = 0; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(mix[i] - 0.5 * (x[i] + wet[i])));
            Assert.That(worst, Is.LessThan(1e-5), "Mix 50 % is the mean of dry and wet");
            // Amount scales the shift (cutoff in octaves from 1 kHz) and the bandwidth (Q in octaves from 0.71): at 50 % half of each, at 200 % twice.
            var (ae, _) = Make(v => { Set(v, P.Cutoff, 2000); Set(v, P.Resonance, 2.84); v.Set(P.Amount, 500); });
            Run(ae, new float[4800], new float[4800]);
            Assert.That(ae.Setting.CutoffHz, Is.EqualTo(1000 * Math.Sqrt(2)).Within(1), "Amount 50 %: half an octave of shift");
            Assert.That(ae.Setting.Q, Is.EqualTo(0.71 * 2).Within(0.05), "Amount 50 %: half the Q's octaves");
            var (be, _) = Make(v => { Set(v, P.Cutoff, 1250); Set(v, P.Resonance, 1.42); v.Set(P.Amount, 2000); });
            Run(be, new float[4800], new float[4800]);
            Assert.That(be.Setting.CutoffHz, Is.EqualTo(1000 * Math.Pow(1.25, 2)).Within(1), "Amount 200 %: twice the shift");
            Assert.That(be.Setting.Q, Is.EqualTo(0.71 * 4).Within(0.1), "Amount 200 %: twice the Q's octaves");
            // And 200 % stays finite and bounded.
            var (ce, _) = Make(v => { on(v); Set(v, P.Resonance, 50); v.Set(P.Amount, 2000); Set(v, P.Output, 24); });
            var (cl, cr) = Run(ce, x, x);
            Assert.That(cl.Concat(cr).All(float.IsFinite), Is.True);
        }

        // ------------------------------------------------------------------ hygiene

        [Test]
        public void SilenceAfterNoiseLeavesNoSubnormalsInThirtySeconds()
        {
            var noise = Measure.PinkNoise(2 * Rate, 0.3, 9);
            foreach (var (name, setup) in new (string, Action<ParamValues>)[]
                     { ("Q 20", v => Set(v, P.Resonance, 20)), ("default", v => { }), ("drive and Q 60", v => { Set(v, P.Resonance, 60); v.Set(P.Drive, 240); }) })
            {
                var (e, _) = Make(setup);
                Run(e, noise, noise);
                var silent = new float[30 * Rate];
                var (l, r) = Run(e, silent, silent);
                Assert.Multiple(() =>
                {
                    Assert.That(e.HoldsSubnormals(), Is.False, name + ": no subnormal state");
                    Assert.That(e.HoldsNonFinite(), Is.False, name + ": finite");
                    Assert.That(l.Concat(r).Any(float.IsSubnormal), Is.False, name + ": no subnormal output");
                    Assert.That(e.Idle, Is.True, name + ": idle after the tail");
                });
            }
        }

        [Test]
        public void TheAudioPathAllocatesNothingInTheVowelMode()
        {
            var (e, v) = Make(pv => { pv.Set(P.Drive, 100); Set(pv, P.EnvAmount, 2); Set(pv, P.LfoDepth, 1); pv.Set(P.LfoShape, 6); });
            var l = new float[FilterEngine.MaxBlock];
            var r = new float[FilterEngine.MaxBlock];
            var rng = new Random(2);
            void Block(int b)
            {
                for (int i = 0; i < l.Length; i++) l[i] = r[i] = (float)Math.Sin(i * 0.05 + b) * 0.3f;
                v.Set(P.Vowel, rng.Next(1001));
                v.Set(P.Cutoff, rng.Next(10001));
                if (b % 40 == 7) v.Set(P.Voice, rng.Next(3));
                if (b % 50 == 9) v.Set(P.Type, rng.Next(2) == 0 ? 7 : rng.Next(7));
                if (b % 60 == 3) v.Set(P.VowelMod, rng.Next(1001));
                if (b % 90 == 13) v.Set(P.Resonance, rng.Next(1001));
                e.Transport = new FilterTransport { Playing = true, Tick = b, SamplesPerTick = 256, TicksPerBeat = 4 };
                e.Process(l, r, l.Length);
            }
            for (int b = 0; b < 200; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2000, Block);
            Measure.Report($"Neo Filter vowel: {bytes} bytes allocated over 2000 blocks with the vowel, cutoff and voice moving and the type switching in and out of Vowel");
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void EverySettingInTheVowelModeStaysBoundedAndFinite()
        {
            var x = Measure.PinkNoise(Rate, 0.5, 5);
            var rng = new Random(5);
            for (int round = 0; round < 40; round++)
            {
                var (e, v) = Make(pv =>
                {
                    foreach (int p in new[] { P.Cutoff, P.Resonance, P.Drive, P.EnvAmount, P.EnvSens, P.LfoDepth, P.Vowel, P.Voice, P.VowelMod, P.Output, P.Amount, P.LfoRate })
                        pv.Set(p, rng.Next(Params.Table[p].Min, Params.Table[p].Max + 1));
                });
                var (l, r) = Run(e, x, x, transport: true);
                Assert.That(l.Concat(r).All(float.IsFinite), Is.True, "round " + round);
                Assert.That(e.HoldsNonFinite(), Is.False, "round " + round);
            }
        }

        [Test]
        public void VowelModeCostsAtMostTwiceTheSvfAtTwentyFourDecibels()
        {
            // Stereo at 48 kHz, 4 s of noise with everything working (drive, envelope, a fast LFO), each engine's best of five renders, the two interleaved.
            var x = Measure.PinkNoise(4 * Rate, 0.2, 4);
            double Time(int type, int slope)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.Type, type); v.Set(P.Slope, slope); v.Set(P.Drive, 240); Set(v, P.LfoDepth, 2); Set(v, P.LfoRate, 8); Set(v, P.EnvAmount, 3); Set(v, P.Resonance, 8);
                });
                var bl = new float[FilterEngine.MaxBlock];
                var br = new float[FilterEngine.MaxBlock];
                var sw = Stopwatch.StartNew();
                for (int pos = 0; pos < x.Length; pos += FilterEngine.MaxBlock)
                {
                    int n = Math.Min(FilterEngine.MaxBlock, x.Length - pos);
                    Array.Copy(x, pos, bl, 0, n);
                    Array.Copy(x, pos, br, 0, n);
                    e.Process(bl, br, n);
                }
                return sw.Elapsed.TotalSeconds;
            }
            Time(7, 0); Time(2, 1);
            double vowel = double.MaxValue, svf = double.MaxValue;
            for (int rep = 0; rep < 5; rep++)
            {
                vowel = Math.Min(vowel, Time(7, 0));
                svf = Math.Min(svf, Time(2, 1));
            }
            Measure.Report($"Neo Filter CPU: Vowel {vowel * 1000:F0} ms, SVF 24 dB/oct {svf * 1000:F0} ms for 4 s of stereo ({vowel / svf:F2}x)");
            Assert.That(vowel, Is.LessThanOrEqualTo(2 * svf), "Vowel mode costs at most 2x the SVF at 24 dB/oct");
        }
    }
}

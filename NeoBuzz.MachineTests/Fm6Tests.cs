using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Fm6;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo FM6 (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class Fm6Tests
    {
        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        /// <summary>Parameter of operator 1 … 6, named by operator 1's parameter.</summary>
        static int O(int op, int op1Param) => FmEngine.OpParam(op - 1, op1Param - P.O1On);

        static (FmEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new FmEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        static float[] Render(FmEngine e, int length, Action<int> atBlock = null)
        {
            var y = new float[length];
            var l = new float[FmEngine.MaxBlock];
            var r = new float[FmEngine.MaxBlock];
            for (int pos = 0; pos < length; pos += FmEngine.MaxBlock)
            {
                atBlock?.Invoke(pos);
                int n = Math.Min(FmEngine.MaxBlock, length - pos);
                e.Process(l, r, n);
                Array.Copy(l, 0, y, pos, n);
            }
            return y;
        }

        /// <summary>
        /// A component's amplitude in a windowed spectrum: the energy of the window's main lobe
        /// (±5 bins) around <paramref name="hz"/>, so it reads the same wherever between bins it falls.
        /// </summary>
        static double Component(double[] mag, double hz, int n)
        {
            int k0 = (int)Math.Round(hz * n / Measure.Rate);
            double sum = 0;
            for (int k = Math.Max(1, k0 - 5); k <= k0 + 5 && k < mag.Length; k++) sum += mag[k] * mag[k];
            return Math.Sqrt(sum);
        }

        static double[] WindowedSpectrum(float[] x, int offset, int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                w[i] = (float)(x[offset + i] * (0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t)));
            }
            return Fft.Magnitudes(w, 0, n);
        }

        static double BesselJ(int n, double x)
        {
            double sum = 0, term = Math.Pow(x / 2, n);
            for (int k = 1; k <= n; k++) term /= k;
            for (int m = 0; m < 60; m++)
            {
                sum += term;
                term *= -(x / 2) * (x / 2) / ((m + 1) * (double)(m + 1 + n));
            }
            return sum;
        }

        /// <summary>An operator's peak phase deviation (radians) at an output level, full velocity insensitive, as the engine computes it.</summary>
        static double ModIndex(int level)
        {
            int outLevel = Dx7.OutLevel(level, 60, 127, 39, 0, 0, 0, 0, 0);
            double target = Math.Max(16, ((Dx7.ScaleOutLevel(99) >> 1) << 6) + outLevel - 4256);
            return 2 * Math.PI * Dx7Envelope.Amplitude(target);
        }

        // ------------------------------------------------------------------ algorithms

        [Test]
        public void TheAlgorithmsMatchTheDx7Chart()
        {
            int[] carriers = { 2, 2, 2, 2, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 3, 3, 4, 4, 4, 5, 5, 3, 3, 3, 4, 4, 5, 6 };
            string[] feedback = { "6", "2", "6", "4>6", "6", "5>6", "6", "4", "2", "3", "6", "2", "6", "6", "2", "6", "2", "3", "6", "3", "3", "6", "6", "6", "6", "6", "3", "5", "6", "5", "6", "6" };
            for (int a = 1; a <= 32; a++)
            {
                var alg = Algorithms.Get(a);
                Assert.That(alg.CarrierCount, Is.EqualTo(carriers[a - 1]), $"algorithm {a}: carriers");
                string fb = alg.FeedbackFrom == alg.FeedbackTo ? $"{alg.FeedbackFrom + 1}" : $"{alg.FeedbackFrom + 1}>{alg.FeedbackTo + 1}";
                Assert.That(fb, Is.EqualTo(feedback[a - 1]), $"algorithm {a}: feedback");
                // Every operator is heard or modulates something.
                for (int op = 0; op < 6; op++)
                {
                    bool modulates = false;
                    for (int t = 0; t < 6; t++) modulates |= (alg.Modulators[t] & (1 << op)) != 0;
                    Assert.That(alg.IsCarrier(op) || modulates, Is.True, $"algorithm {a}: operator {op + 1} does nothing");
                    Assert.That(alg.IsCarrier(op) && modulates, Is.False, $"algorithm {a}: operator {op + 1} is both");
                }
                var layout = Algorithms.Layout(alg);
                Assert.That(layout.Distinct().Count(), Is.EqualTo(6), $"algorithm {a}: diagram positions overlap");
            }
            Assert.That(Algorithms.Describe(Algorithms.Get(1)), Is.EqualTo("2→1 6→5 5→4 4→3; carriers 1 3; feedback on 6").Or.EqualTo("2→1 4→3 5→4 6→5; carriers 1 3; feedback on 6"));
        }

        // ------------------------------------------------------------------ tuning

        [Test]
        public void InitVoiceIsAPureSineInTune()
        {
            var (e, _) = Make();
            e.NoteOn(0, 69, 0.8f);
            var y = Render(e, 4096 + Measure.N);
            double f = Measure.PeakFrequency(y, 4096, Measure.N);
            double cents = Measure.Cents(f, 440);
            var mag = WindowedSpectrum(y, 4096, Measure.N);
            double fund = Component(mag, 440, Measure.N);
            double worst = 0;
            for (int h = 2; h * 440 < 20000; h++) worst = Math.Max(worst, Component(mag, h * 440, Measure.N));
            double thd = Measure.Db(worst / fund);
            Measure.Report($"Neo FM6, INIT VOICE at A4: {cents:+0.000;−0.000} cents, harmonics {thd:F1} dB");
            Assert.That(Math.Abs(cents), Is.LessThan(0.1));
            Assert.That(thd, Is.LessThan(-90));
        }

        [Test]
        public void RatiosFixedFrequenciesAndDetune()
        {
            double Freq(int note, Action<ParamValues> setup)
            {
                var (e, _) = Make(setup);
                e.NoteOn(0, note, 0.8f);
                var y = Render(e, 4096 + Measure.N);
                return Measure.PeakFrequency(y, 4096, Measure.N);
            }

            double ratio = Freq(57, v => { v.Set(P.O1Coarse, 2); v.Set(P.O1Fine, 50); });
            Assert.That(Math.Abs(Measure.Cents(ratio, 660)), Is.LessThan(0.1), "coarse 2, fine 50: 3 × 220 Hz");
            double half = Freq(69, v => v.Set(P.O1Coarse, 0));
            Assert.That(Math.Abs(Measure.Cents(half, 220)), Is.LessThan(0.1), "coarse 0: ×0.5");
            foreach (int note in new[] { 40, 80 })
            {
                double fixedHz = Freq(note, v => { v.Set(P.O1Mode, 1); v.Set(P.O1Coarse, 3); v.Set(P.O1Fine, 0); });
                Assert.That(Math.Abs(Measure.Cents(fixedHz, 1000)), Is.LessThan(0.1), $"fixed 1000 Hz at note {note}");
            }
            double up = Freq(69, v => v.Set(P.O1Detune, 14));
            double down = Freq(69, v => v.Set(P.O1Detune, 0));
            Measure.Report($"Neo FM6: detune ±7 at A4 = {Measure.Cents(up, 440):+0.0;−0.0} / {Measure.Cents(down, 440):+0.0;−0.0} cents");
            Assert.That(Measure.Cents(up, 440), Is.InRange(4.0, 10.0));
            Assert.That(Measure.Cents(down, 440), Is.InRange(-10.0, -4.0));
        }

        [TestCase(70)]
        [TestCase(75)]
        public void FrequencyModulationFollowsBessel(int level)
        {
            // Algorithm 1: op 2 (ratio 1) modulates op 1 (ratio 8); sidebands at (8 ± k)·f never fold.
            var (e, _) = Make(v =>
            {
                v.Set(P.O1Coarse, 8);
                v.Set(O(2, P.O1Level), level);
            });
            e.NoteOn(0, 36, 1f);
            var y = Render(e, 8192 + Measure.N);
            var mag = WindowedSpectrum(y, 8192, Measure.N);
            double f = DspMath.NoteToHz(36);
            double beta = ModIndex(level);
            double c0 = Component(mag, 8 * f, Measure.N);
            for (int k = 1; k <= 3; k++)
            {
                double expected = Measure.Db(Math.Abs(BesselJ(k, beta)) / Math.Abs(BesselJ(0, beta)));
                double upper = Measure.Db(Component(mag, (8 + k) * f, Measure.N) / c0);
                double lower = Measure.Db(Component(mag, (8 - k) * f, Measure.N) / c0);
                Measure.Report($"Neo FM6, level {level} (β {beta:F2}): sideband {k} {upper:F2} / {lower:F2} dB, Bessel {expected:F2} dB");
                Assert.That(upper, Is.EqualTo(expected).Within(0.1), $"upper sideband {k}");
                Assert.That(lower, Is.EqualTo(expected).Within(0.1), $"lower sideband {k}");
            }
        }

        [Test]
        public void FeedbackTurnsASineIntoASaw()
        {
            double H(int feedback, int h)
            {
                var (e, _) = Make(v =>
                {
                    Set(v, P.Algorithm, 32);
                    v.Set(P.O1Level, 0);
                    v.Set(O(6, P.O1Level), 99);
                    v.Set(P.Feedback, feedback);
                });
                e.NoteOn(0, 57, 0.8f);
                var y = Render(e, 4096 + Measure.N);
                var mag = WindowedSpectrum(y, 4096, Measure.N);
                return Measure.Db(Component(mag, 220 * h, Measure.N) / Component(mag, 220, Measure.N));
            }
            double h0 = H(0, 2), h3 = H(3, 2), h5 = H(5, 2), h7 = H(7, 2);
            Measure.Report($"Neo FM6: 2nd harmonic with feedback 0/3/5/7: {h0:F1} / {h3:F1} / {h5:F1} / {h7:F1} dB");
            Assert.That(h0, Is.LessThan(-90));
            Assert.That(h3, Is.GreaterThan(h0 + 30));
            Assert.That(h5, Is.GreaterThan(h3));
            Assert.That(h5, Is.InRange(-14.0, -3.0), "near a saw (−6 dB)");
        }

        // ------------------------------------------------------------------ envelopes

        /// <summary>RMS in dB over consecutive 10 ms windows.</summary>
        static double[] Contour(float[] y)
        {
            int w = Measure.Rate / 100;
            var c = new double[y.Length / w];
            for (int i = 0; i < c.Length; i++) c[i] = Measure.Db(Measure.Rms(y, i * w, w) * Math.Sqrt(2));
            return c;
        }

        [Test]
        public void DecaysAreStraightLinesInDecibels()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.O1R2, 45);
                v.Set(P.O1L2, 0);
                v.Set(P.O1R3, 45);
                v.Set(P.O1L3, 0);
            });
            e.NoteOn(0, 69, 0.8f);
            var c = Contour(Render(e, Measure.Rate * 3));
            // From 3 dB below the peak to 50 dB below it, fit a line.
            double peak = c.Max();
            int a = Array.FindIndex(c, x => x < peak - 3), b = Array.FindIndex(c, x => x < peak - 50);
            Assert.That(a, Is.GreaterThan(0));
            Assert.That(b, Is.GreaterThan(a + 10), "the decay takes a while at rate 45");
            double slope = (c[b] - c[a]) / (b - a);
            double worst = 0;
            for (int i = a; i <= b; i++) worst = Math.Max(worst, Math.Abs(c[i] - (c[a] + slope * (i - a))));
            Measure.Report($"Neo FM6: decay at rate 45: {-slope * 100:F1} dB/s, straight within {worst:F2} dB");
            Assert.That(worst, Is.LessThan(1.0));
        }

        [Test]
        public void FasterRatesAndRateScalingShortenTheRelease()
        {
            double ReleaseTime(int rate, int note, int rateScaling)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.O1R4, rate);
                    v.Set(P.O1RateSc, rateScaling);
                });
                e.NoteOn(0, note, 0.8f);
                var y = Render(e, Measure.Rate * 6, pos => { if (pos == Measure.Rate / 2 - Measure.Rate / 2 % FmEngine.MaxBlock) e.NoteOff(0); });
                var c = Contour(y);
                double held = c[30];
                int off = Measure.Rate / 2 / (Measure.Rate / 100);
                int quiet = Array.FindIndex(c, off, x => x < held - 40);
                return quiet < 0 ? double.PositiveInfinity : (quiet - off) / 100.0;
            }
            double slow = ReleaseTime(50, 60, 0), fast = ReleaseTime(70, 60, 0);
            double low = ReleaseTime(50, 36, 7), high = ReleaseTime(50, 96, 7);
            Measure.Report($"Neo FM6: −40 dB release: rate 50 {slow:F2} s, rate 70 {fast:F2} s; rate scaling 7: C2 {low:F2} s, C7 {high:F2} s");
            Assert.That(fast, Is.LessThan(slow / 2));
            Assert.That(high, Is.LessThan(low / 2));
        }

        [Test]
        public void AFlatSegmentHoldsForItsRate()
        {
            // Level 1 = level 2: the DX7 holds there for a time set by rate 2 (here about half a second).
            var (e, _) = Make(v =>
            {
                v.Set(P.O1R2, 40);
                v.Set(P.O1L2, 99);
                v.Set(P.O1R3, 99);
                v.Set(P.O1L3, 0);
            });
            e.NoteOn(0, 69, 0.8f);
            var c = Contour(Render(e, Measure.Rate * 2));
            double hold = Dx7.StaticSamples44(40) / 44100.0;
            Measure.Report($"Neo FM6: flat segment at rate 40 holds {hold:F2} s");
            Assert.That(c[(int)(hold * 100 * 0.8)], Is.GreaterThan(c[5] - 1), "still holding");
            Assert.That(c[(int)(hold * 100 * 1.2) + 2], Is.LessThan(c[5] - 40), "then gone");
        }

        [Test]
        public void VelocityAndKeyboardScalingSetTheLevel()
        {
            double Level(int note, float velocity, Action<ParamValues> setup)
            {
                var (e, _) = Make(setup);
                e.NoteOn(0, note, velocity);
                var y = Render(e, Measure.Rate / 2);
                return Measure.Db(Measure.Rms(y, Measure.Rate / 4, Measure.Rate / 4));
            }
            double loud = Level(69, 1f, v => v.Set(P.O1VelSens, 7));
            double soft = Level(69, 64 / 127f, v => v.Set(P.O1VelSens, 7));
            double expected = (Dx7.ScaleVelocity(127, 7) - Dx7.ScaleVelocity(64, 7)) / 256.0 * 6.0206;
            Measure.Report($"Neo FM6: velocity 127 vs 64 at sensitivity 7: {loud - soft:F2} dB (DX7: {expected:F2} dB)");
            Assert.That(loud - soft, Is.EqualTo(expected).Within(0.3));
            Assert.That(Math.Abs(Level(69, 1f, null) - Level(69, 64 / 127f, null)), Is.LessThan(0.1), "sensitivity 0 ignores velocity");

            double low = Level(48, 0.8f, v => v.Set(P.O1RDepth, 99));
            double high = Level(96, 0.8f, v => v.Set(P.O1RDepth, 99));
            double flatLow = Level(48, 0.8f, null), flatHigh = Level(96, 0.8f, null);
            Measure.Report($"Neo FM6: right depth 99 (−lin): C3 {low:F1} dB, C7 {high:F1} dB");
            Assert.That(low - high, Is.GreaterThan(40));
            Assert.That(Math.Abs(flatLow - flatHigh), Is.LessThan(0.5));
        }

        // ------------------------------------------------------------------ modulation

        /// <summary>Frequency over 20 ms windows from interpolated upward zero crossings.</summary>
        static List<double> FrequencyTrack(float[] y, int from)
        {
            var crossings = new List<double>();
            for (int i = from + 1; i < y.Length; i++)
                if (y[i - 1] < 0 && y[i] >= 0) crossings.Add(i - 1 + y[i - 1] / (y[i - 1] - y[i]));
            var track = new List<double>();
            int w = Measure.Rate / 50;
            for (int c = 0; c + 1 < crossings.Count;)
            {
                int d = c + 1;
                while (d + 1 < crossings.Count && crossings[d] - crossings[c] < w) d++;
                track.Add((d - c) * Measure.Rate / (crossings[d] - crossings[c]));
                c = d;
            }
            return track;
        }

        [Test]
        public void LfoVibratoReachesAnOctaveAtFullDepth()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.LfoSpeed, 10);
                v.Set(P.LfoWave, 0);
                v.Set(P.LfoPmd, 99);
                v.Set(P.PitchSens, 7);
            });
            e.NoteOn(0, 69, 0.8f);
            var track = FrequencyTrack(Render(e, Measure.Rate * 2), 2400);
            double lo = track.Min(), hi = track.Max();
            Measure.Report($"Neo FM6: LFO pitch depth 99, sensitivity 7: {lo:F0} … {hi:F0} Hz around 440 (LFO {Dx7.LfoHz(10):F2} Hz)");
            Assert.That(hi / lo, Is.InRange(3.0, 4.2), "±1 octave, less the 20 ms smoothing");
        }

        [Test]
        public void PitchEnvelopeShiftsByItsLevel()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.PegL1, 74);
                v.Set(P.PegL2, 74);
                v.Set(P.PegL3, 74);
            });
            e.NoteOn(0, 69, 0.8f);
            var y = Render(e, Measure.Rate / 2 + Measure.N);
            double f = Measure.PeakFrequency(y, Measure.Rate / 2, Measure.N);
            double expected = 440 * Math.Pow(2, Dx7.PitchLevelOctaves(74));
            Measure.Report($"Neo FM6: pitch envelope level 74 → {Measure.Cents(f, 440):F1} cents");
            Assert.That(Math.Abs(Measure.Cents(f, expected)), Is.LessThan(0.5));
        }

        [Test]
        public void AnOperatorSwitchedOffIsSilent()
        {
            var (e, _) = Make(v => v.Set(P.O1On, 0));
            e.NoteOn(0, 69, 1f);
            var y = Render(e, Measure.Rate / 4);
            Assert.That(Measure.Peak(y, 0, y.Length), Is.LessThan(1e-6));
        }

        // ------------------------------------------------------------------ quality and cost

        [Test]
        public void CleanEngineKeepsBrightPatchesFreeOfAliasing()
        {
            double Floor(int engine)
            {
                var (e, _) = Make(v =>
                {
                    v.Set(O(2, P.O1Level), 99);
                    v.Set(P.Engine, engine);
                });
                e.NoteOn(0, 96, 1f);
                var y = Render(e, 8192 + Measure.N);
                return PolyTests.AliasFloorWindowed(y, 8192, Measure.N, DspMath.NoteToHz(96));
            }
            double clean = Floor(0), raw = Floor(1);
            Measure.Report($"Neo FM6, full-depth 1:1 FM at C7: alias floor Clean {clean:F1} dB, Raw {raw:F1} dB");
            Assert.That(clean, Is.LessThan(-80));
            Assert.That(raw, Is.GreaterThan(clean + 30), "Raw keeps the original's aliasing");
        }

        [Test]
        public void SixteenVoicesOfSixOperatorsRunFasterThanRealTime()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Polyphony, 16);
                for (int op = 1; op <= 6; op++) v.Set(O(op, P.O1Level), 80);
            });
            for (int k = 0; k < 16; k++) e.NoteOn(k, 40 + 3 * k, 0.8f);
            Render(e, 4800);
            var sw = Stopwatch.StartNew();
            Render(e, Measure.Rate * 2);
            double factor = 2.0 / sw.Elapsed.TotalSeconds;
            Measure.Report($"Neo FM6: 16 voices × 6 operators, Clean 2×: {factor:F1}× real time");
            Assert.That(factor, Is.GreaterThan(2), "a guard; the figure is reported");
        }

        // ------------------------------------------------------------------ DX7 data

        static byte[] RandomVoice(Random rng, string name)
        {
            var values = new Dictionary<int, int>();
            var v = Sysex.FromParams(p =>
            {
                if (!values.TryGetValue(p, out int x))
                {
                    var d = Params.Table[p];
                    values[p] = x = rng.Next(d.Min, d.Max + 1);
                }
                return x;
            }, name);
            return v;
        }

        [Test]
        public void Dx7BanksRoundTrip()
        {
            var rng = new Random(7);
            var voices = new List<Sysex.Voice>();
            for (int i = 0; i < 32; i++)
            {
                var name = $"VOICE {i,2}";
                var data = RandomVoice(rng, name);
                voices.Add(new Sysex.Voice(name, data));
            }
            var file = Sysex.WriteBank(voices);
            Assert.That(file.Length, Is.EqualTo(4104));
            var back = Sysex.Read(file);
            Assert.That(back.Count, Is.EqualTo(32));
            for (int i = 0; i < 32; i++)
            {
                Assert.That(back[i].Name, Is.EqualTo(voices[i].Name.TrimEnd()));
                Assert.That(back[i].Data, Is.EqualTo(voices[i].Data), $"voice {i}");
            }
            file[100] ^= 0x01;
            Assert.Throws<InvalidDataException>(() => Sysex.Read(file), "a damaged bank is refused");
        }

        [Test]
        public void ASingleVoiceDumpLoadsAndMapsOntoTheParameters()
        {
            var voice = RandomVoice(new Random(11), "BRASS 1");
            var file = new byte[163];
            file[0] = 0xF0; file[1] = 0x43; file[2] = 0x00; file[3] = 0x00; file[4] = 0x01; file[5] = 0x1B;
            Array.Copy(voice, 0, file, 6, 155);
            int sum = 0;
            foreach (var b in voice) sum += b;
            file[161] = (byte)((-sum) & 0x7F);
            file[162] = 0xF7;
            var read = Sysex.Read(file);
            Assert.That(read.Count, Is.EqualTo(1));
            Assert.That(read[0].Name, Is.EqualTo("BRASS 1"));

            var values = new ParamValues(Params.Table);
            foreach (var (p, x) in Sysex.ToParams(read[0].Data)) values.Set(p, x);
            var again = Sysex.FromParams(p => values[p], "BRASS 1");
            Assert.That(again, Is.EqualTo(voice), "voice → parameters → voice");
            Assert.That(values[P.Algorithm], Is.EqualTo(voice[134]));
            Assert.That(values[P.O1Level], Is.EqualTo(voice[5 * 21 + 16]), "operator 1 is stored last");
            Assert.That(values[O(6, P.O1Level)], Is.EqualTo(voice[16]), "operator 6 first");
        }

        [Test]
        public void ValueTextsSayWhatTheNumbersMean()
        {
            _ = new FmEngine(new ParamValues(Params.Table));
            Assert.That(Params.Table[P.O1Break].Describe(39), Is.EqualTo("39 · C4"));
            Assert.That(Params.Table[P.O1Coarse].Describe(0), Is.EqualTo("0 · ×0.5"));
            Assert.That(Params.Table[P.PegL1].Describe(50), Is.EqualTo("50 · 0 st"));
            Assert.That(Params.Table[P.Algorithm].Describe(31), Is.EqualTo("32"));
            Assert.That(FmText.Frequency(true, 3, 0, 7), Is.EqualTo("1.00 kHz"));
            Assert.That(FmText.Frequency(false, 1, 41, 7), Is.EqualTo("×1.41"));
        }
    }
}

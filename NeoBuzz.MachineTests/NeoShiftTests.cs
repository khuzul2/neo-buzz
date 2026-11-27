using NeoBuzz.Dsp;
using NeoBuzz.Shift;
using static NeoBuzz.MachineTests.ShiftRig;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Shift (docs/specs/neo-shift.md): the acceptance gates, measured, with signals made in code.</summary>
    [TestFixture]
    public class NeoShiftTests
    {
        // ================================================================================== pitch

        [Test]
        public void ASineShiftedSevenUpAndTwelveDownReadsWithinOneCentInFastAndSmooth([Values("Fast", "Smooth")] string quality, [Values(7, -12)] int semitones, [Values(40, 100)] int window)
        {
            if (quality == "Smooth" && window != 40) Assert.Pass("the window is the Fast shifter's");
            var v = Pitch(quality, semitones, 0, window);
            var x = Sine(Rate * 6, 440, 0.5);
            var (l, _) = Render(v, x, Copy(x));
            int n = 1 << 17;
            var m = Spectrum(l, l.Length - n, n);
            double want = 440 * Math.Pow(2, semitones / 12.0);
            double hz = PeakHz(m, n, want);
            double c = Cents(hz, want);
            // the other components: the highest line more than 12 bins from the peak
            int pk = (int)Math.Round(hz * n / Rate);
            double other = 0;
            for (int k = 10; k < m.Length; k++) if (Math.Abs(k - pk) > 40) other = Math.Max(other, m[k]);
            double otherDb = Measure.Db(other / m[pk]);
            Measure.Report($"{quality} {semitones:+0;-0} st, window {window} ms: {hz:0.000} Hz ({c:+0.000;-0.000} cents from {want:0.00}); the strongest other line {otherDb:0.0} dB");
            Assert.That(Math.Abs(c), Is.LessThan(1.0));
            if (quality == "Smooth") Assert.That(otherDb, Is.LessThan(-40));
        }
    
        [Test]
        public void TheFastShifterAtItsShortWindowsStaysWithinTwoCents([Values(7, -12, 5, -7)] int semitones, [Values(10, 20)] int window)
        {
            var v = Pitch("Fast", semitones, 0, window);
            var x = Sine(Rate * 6, 440, 0.5);
            var (l, _) = Render(v, x, Copy(x));
            int n = 1 << 17;
            var m = Spectrum(l, l.Length - n, n);
            double want = 440 * Math.Pow(2, semitones / 12.0);
            double hz = PeakHz(m, n, want);
            double c = Cents(hz, want);
            Measure.Report($"Fast, window {window} ms, {semitones:+0;-0} st: {hz:0.000} Hz ({c:+0.000;-0.000} cents)");
            Assert.That(Math.Abs(c), Is.LessThan(2.0));
        }

        [Test]
        public void ACentsAddUpToTheSemitones([Values("Fast", "Smooth")] string quality)
        {
            var v = Pitch(quality, 3, -40);
            var x = Sine(Rate * 6, 440, 0.5);
            var (l, _) = Render(v, x, Copy(x));
            int n = 1 << 17;
            var m = Spectrum(l, l.Length - n, n);
            double want = 440 * Math.Pow(2, 2.6 / 12);
            double c = Cents(PeakHz(m, n, want), want);
            Assert.That(Math.Abs(c), Is.LessThan(1.0), quality);
        }

        [Test]
        public void TheStereoDetuneShiftsLeftDownAndRightUpByItsCents([Values("Fast", "Smooth")] string quality)
        {
            var v = Pitch(quality, 0);
            Phys(v, P.Detune, 30);
            var x = Sine(Rate * 6, 440, 0.5);
            var (l, r) = Render(v, x, Copy(x));
            int n = 1 << 17;
            double hl = PeakHz(Spectrum(l, l.Length - n, n), n, 440, 90), hr = PeakHz(Spectrum(r, r.Length - n, n), n, 440, 90);
            double cl = Cents(hl, 440), cr = Cents(hr, 440);
            Measure.Report($"{quality}: stereo detune 30 cents: left {cl:+0.00;-0.00}, right {cr:+0.00;-0.00} cents");
            Assert.That(cl, Is.EqualTo(-30).Within(1.0));
            Assert.That(cr, Is.EqualTo(30).Within(1.0));
        }

        // ================================================================================== frequency

        static readonly double[] ImageTones = { 40, 150, 440, 1000, 4000, 10000, 15900 };

        [Test]
        public void AHundredHertzShiftMovesATone100HzAndLeavesTheOtherSidebandUnder60Db([Values(100, -100)] int shift)
        {
            var v = FrequencyMode(shift);
            int n = 1 << 17;
            var lines = new List<string>();
            double worst = -999, worstErr = 0;
            foreach (double f in ImageTones)
            {
                double fin = Math.Round(f * n / Rate) * Rate / (double)n;       // on a bin of the analysis, so that the lines are clean
                var x = Sine(Rate * 6, fin, 0.5);
                var (l, _) = Render(v, x, Copy(x));
                var m = Spectrum(l, l.Length - n, n);
                double want = fin + shift, image = Math.Abs(fin - shift);
                double got = PeakHz(m, n, Math.Abs(want));
                double err = got - Math.Abs(want);
                int kw = (int)Math.Round(Math.Abs(want) * n / Rate), ki = (int)Math.Round(image * n / Rate);
                double wl = 0, il = 0;
                for (int k = kw - 3; k <= kw + 3; k++) wl = Math.Max(wl, m[k]);
                for (int k = Math.Max(1, ki - 3); k <= ki + 3; k++) il = Math.Max(il, m[k]);
                double rej = Measure.Db(il / wl);
                lines.Add($"{fin:0} Hz: moved to {got:0.000} ({err:+0.000;-0.000} Hz), image {rej:0.0} dB");
                worst = Math.Max(worst, rej);
                worstErr = Math.Max(worstErr, Math.Abs(err));
            }
            Measure.Report($"Frequency shift {shift:+0;-0} Hz: " + string.Join("; ", lines));
            Assert.That(worstErr, Is.LessThan(0.1), "every tone moves by the shift");
            Assert.That(worst, Is.LessThan(-60), "the unwanted sideband");
        }

        [Test]
        public void SpreadShiftsTheRightSideTheOtherWay()
        {
            var v = FrequencyMode(100, 0, 100);
            var x = Sine(Rate * 6, 1000, 0.5);
            var (l, r) = Render(v, x, Copy(x));
            int n = 1 << 17;
            double hl = PeakHz(Spectrum(l, l.Length - n, n), n, 1100), hr = PeakHz(Spectrum(r, r.Length - n, n), n, 900);
            Assert.That(hl, Is.EqualTo(1100).Within(0.1));
            Assert.That(hr, Is.EqualTo(900).Within(0.1));
        }

        [Test]
        public void AFeedbackMakesEchoesThatRiseByTheShiftEachTime()
        {
            // 1 kHz in, +200 Hz shift, 60 % feedback: lines at 1200, 1400, 1600, ... each a repeat further up, each quieter.
            var v = FrequencyMode(200, 60);
            var x = Sine(Rate * 6, 1000, 0.3);
            var (l, _) = Render(v, x, Copy(x));
            int n = 1 << 17;
            var m = Spectrum(l, l.Length - n, n);
            double a1 = Line(m, n, 1200), a2 = Line(m, n, 1400), a3 = Line(m, n, 1600);
            Measure.Report($"Frequency feedback 60 %: 1200 Hz {Measure.Db(a1):0.0} dB, 1400 Hz {Measure.Db(a2):0.0} dB, 1600 Hz {Measure.Db(a3):0.0} dB");
            Assert.That(a2, Is.GreaterThan(a1 * 0.2), "a second repeat");
            Assert.That(a2, Is.LessThan(a1));
            Assert.That(a3, Is.LessThan(a2));
            Assert.That(a3, Is.GreaterThan(0.02 * a1));
        }

        static double Line(double[] m, int n, double hz)
        {
            int k = (int)Math.Round(hz * n / Rate);
            double a = 0;
            for (int j = k - 2; j <= k + 2; j++) a = Math.Max(a, m[j]);
            return a;
        }

        // ================================================================================== ring

        [Test]
        public void ASineCarrierPutsSidebandsAtTheSumAndDifferenceAndTheInputIs90DbDown([Values(300.0, 123.456, 1700.0, 4000.0)] double fc)
        {
            var v = RingMode(fc);
            fc = Params.Table[P.Carrier].Value(v[P.Carrier]);       // the carrier the control holds (it moves in steps of a few cents)
            int n = 1 << 17;
            double f = Math.Round(1000.0 * n / Rate) * Rate / n;
            var x = Sine(Rate * 6, f, 0.5);
            var (l, _) = Render(v, x, Copy(x));
            var m = Spectrum(l, l.Length - n, n);
            double up = PeakHz(m, n, f + fc), down = PeakHz(m, n, Math.Abs(f - fc));
            double side = Math.Max(Line(m, n, f + fc), Line(m, n, Math.Abs(f - fc)));
            double input = Line(m, n, f);
            double db = Measure.Db(input / side);
            Measure.Report($"Ring {fc} Hz: sidebands at {up:0.000} and {down:0.000} Hz (wanted {f + fc:0.000} and {Math.Abs(f - fc):0.000}); the input {db:0.0} dB under them");
            Assert.That(up, Is.EqualTo(f + fc).Within(0.1));
            Assert.That(down, Is.EqualTo(Math.Abs(f - fc)).Within(0.1));
            Assert.That(db, Is.LessThan(-90));
        }

        [Test]
        public void DepthZeroIsAmplitudeModulationAndTheInputStays()
        {
            var v = RingMode(300, "Sine", 0);
            double fc = Params.Table[P.Carrier].Value(v[P.Carrier]);
            int n = 1 << 17;
            double f = Math.Round(1000.0 * n / Rate) * Rate / n;
            var (l, _) = Render(v, Sine(Rate * 6, f, 0.5), Sine(Rate * 6, f, 0.5));
            var m = Spectrum(l, l.Length - n, n);
            double input = Line(m, n, f), side = Line(m, n, f + fc);
            // g = 1/2 + c/2: the input at 1/2, each sideband at 1/4 (of its 0.5)
            Assert.That(Measure.Db(input / side), Is.EqualTo(6.0).Within(0.3));
        }

        [Test]
        public void TheSquareAndTriangleCarriersAreBandLimited([Values("Triangle", "Square")] string shape)
        {
            // A 3 kHz square carrier has its odd harmonics at 9, 15, 21 ... kHz: those past 24 kHz must not fold back. A 1 kHz sine in, so every line is
            // 1 kHz + the carrier's harmonics; the folded ones would land on other lines.
            var v = RingMode(3000, shape);
            int n = 1 << 17;
            double f = Math.Round(1000.0 * n / Rate) * Rate / n;
            var x = Sine(Rate * 6, f, 0.5);
            var (l, _) = Render(v, x, Copy(x));
            var m = Spectrum(l, l.Length - n, n);
            // wanted lines: |f +/- k fc| folded, for odd k (square) or odd k (triangle, 1/k^2) up to Nyquist
            var wanted = new List<double>();
            for (int k = 1; k * 3000.0 - f < Rate / 2; k += 2)
                foreach (double w in new[] { f + k * 3000.0, Math.Abs(f - k * 3000.0) })
                    if (w < Rate / 2) wanted.Add(w);
            double strongest = m.Max();
            double worst = 0;
            for (int k = 20; k < m.Length - 20; k++)
            {
                double hz = k * (double)Rate / n;
                if (wanted.Any(w => Math.Abs(w - hz) < 40)) continue;
                worst = Math.Max(worst, m[k]);
            }
            double db = Measure.Db(worst / strongest);
            Measure.Report($"Ring, {shape} carrier at 3 kHz: the strongest line that is not a sideband {db:0.0} dB");
            Assert.That(db, Is.LessThan(-60));
        }

        // ================================================================================== automation

        // Parameters stepped at every block of 90 samples (533.3 Hz) while a tone sweeps: a control that is not smoothed leaves lines at multiples of that
        // rate on either side of each line of the sound. Returns the strongest such line, in dB below the strongest line of the sound (the bands
        // (lo, hi) in Hz that the sound's lines sweep through).
        const int ZipperBlock = 90;

        static double Zipper(float[] y, int from, int n, params (double Lo, double Hi)[] lines)
        {
            var m = Spectrum(y, from, n);
            double hzPerBin = (double)Rate / n;
            double Level(double lo, double hi)
            {
                double a = 0;
                for (int k = Math.Max(1, (int)Math.Floor(lo / hzPerBin)); k <= Math.Min(m.Length - 1, (int)Math.Ceiling(hi / hzPerBin)); k++) a = Math.Max(a, m[k]);
                return a;
            }
            double strongest = 0;
            foreach (var (lo, hi) in lines) strongest = Math.Max(strongest, Level(lo - 5, hi + 5));
            double worst = 0;
            double rate = (double)Rate / ZipperBlock;
            foreach (var (lo, hi) in lines)
                for (int k = 1; k <= 8; k++)
                    foreach (int sign in new[] { -1, 1 })
                    {
                        double a = lo + sign * k * rate - 5, b = hi + sign * k * rate + 5;
                        if (a < 30 || b > Rate * 0.45) continue;
                        if (lines.Any(o => b > o.Lo - 25 && a < o.Hi + 25)) continue;
                        worst = Math.Max(worst, Level(a, b));
                    }
            return Measure.Db(worst / strongest);
        }

        [Test]
        public void ASweepOfPitchMakesNoClickAndNoZipperNoise([Values("Fast", "Smooth")] string quality)
        {
            // A 440 Hz sine, Pitch stepped by a semitone every few blocks (up from -12 to +12 and back; the engine glides each step), then Fine swept a
            // cent at a time.
            var x = Sine(Rate * 12, 440, 0.4);
            var v = Pitch(quality, -12);
            Phys(v, P.Window, 40);
            Func<double, double> fine = t => Math.Round(-100 + 200 * Math.Min(1, Math.Max(0, (t - 4) / 8)));
            var (y, _) = Render(Engine(v), x, Copy(x), ZipperBlock, pos =>
            {
                double t = pos / (double)Rate;
                if (t < 4) Phys(v, P.Pitch, Math.Round(-12 + 24 * Math.Abs(((t / 4) * 2) % 2 - 1)));
                else
                {
                    Phys(v, P.Pitch, 0);
                    Phys(v, P.Fine, fine(t));
                }
            });
            int lat = quality == "Smooth" ? ShiftEngine.SmoothLatency : DelayShifterLatency(40);
            var clicks = NewClicks(x, y, lat);
            Assert.That(clicks, Is.Empty, $"{quality}: clicks at {string.Join(", ", clicks.Take(6))}");
            // the stretch 6.5 s to 7.9 s: Fine from -44 to -19 cents
            int n = 1 << 16, from = (int)(Rate * 6.5);
            double c0 = fine(6.5 + lat / (double)Rate), c1 = fine(6.5 + (n + lat) / (double)Rate);
            double lo = 440 * Math.Pow(2, Math.Min(c0, c1) / 1200.0), hi = 440 * Math.Pow(2, Math.Max(c0, c1) / 1200.0);
            double z = Zipper(y, from, n, (lo - 2, hi + 2));
            Measure.Report($"{quality}: Pitch stepped every {ZipperBlock} samples, Fine swept {c0:0} to {c1:0} cents on 440 Hz: the strongest line at the control rate's multiples {z:0.0} dB");
            Assert.That(z, Is.LessThan(-60));
        }

        static int DelayShifterLatency(int windowMs) => DelayShifter.WindowSamples(windowMs, Rate) / 2;

        [Test]
        public void ASweepOfShiftMakesNoClickAndNoZipperNoise()
        {
            var x = Sine(Rate * 12, 1000, 0.4);
            var v = FrequencyMode(-400);
            Func<double, double> shift = t => Math.Round(-400 + 800 * Math.Min(1, t / 8));
            var (y, _) = Render(Engine(v), x, Copy(x), ZipperBlock, pos => Phys(v, P.Shift, shift(pos / (double)Rate)));
            Assert.That(ClickDetector.Find(y), Is.Empty);
            int n = 1 << 15, from = Rate * 4;
            double s0 = shift(4.0), s1 = shift(4.0 + n / (double)Rate);
            double z = Zipper(y, from, n, (1000 + s0 - 2, 1000 + s1 + 2));
            Measure.Report($"Shift stepped every {ZipperBlock} samples, swept {s0:0} to {s1:0} Hz on 1 kHz: the strongest line at the control rate's multiples {z:0.0} dB");
            Assert.That(z, Is.LessThan(-60));
        }

        [Test]
        public void ASweepOfTheCarrierMakesNoClickAndNoZipperNoise([Values("Sine", "Triangle", "Square")] string shape)
        {
            var x = Sine(Rate * 12, 220, 0.4);
            var v = RingMode(300, shape);
            Func<double, double> carrier = t => 300 + 40 * Math.Min(1, Math.Max(0, (t - 3) / 8));       // 300 Hz to 340 Hz over 8 s
            var (y, _) = Render(Engine(v), x, Copy(x), ZipperBlock, pos => Phys(v, P.Carrier, carrier(pos / (double)Rate)));
            Assert.That(ClickDetector.Find(y), Is.Empty);
            if (shape == "Sine")
            {
                int n = 1 << 16, from = Rate * 6;
                double c0 = carrier(6.0), c1 = carrier(6.0 + n / (double)Rate);
                double z = Zipper(y, from, n, (220 + c0 - 2, 220 + c1 + 2), (c0 - 220 - 2, c1 - 220 + 2));
                Measure.Report($"Carrier stepped every {ZipperBlock} samples, swept {c0:0.0} to {c1:0.0} Hz on 220 Hz (sine): the strongest line at the control rate's multiples {z:0.0} dB");
                Assert.That(z, Is.LessThan(-60));
            }
        }

        // ================================================================================== feedback

        [Test]
        public void FeedbackAt95PercentForAMinuteOfNoiseStaysBoundedAndMakesNoDenormals([Values(95, -95)] int feedback)
        {
            var v = FrequencyMode(300, feedback);
            var e = Engine(v);
            var rng = new Rng(99);
            var l = new float[256]; var r = new float[256];
            double peak = 0;
            long subnormal = 0;
            int blocks = Rate * 60 / 256;
            for (int b = 0; b < blocks + Rate * 6 / 256; b++)
            {
                bool silent = b >= blocks;
                for (int i = 0; i < 256; i++) { l[i] = silent ? 0 : 0.5f * rng.NextBipolar(); r[i] = silent ? 0 : 0.5f * rng.NextBipolar(); }
                e.Process(l, r, 256);
                for (int i = 0; i < 256; i++)
                {
                    float a = Math.Abs(l[i]), c = Math.Abs(r[i]);
                    if (float.IsNaN(a) || float.IsInfinity(a) || float.IsNaN(c) || float.IsInfinity(c)) Assert.Fail($"not finite at block {b}");
                    if (a > peak) peak = a;
                    if (c > peak) peak = c;
                    if ((a != 0 && a < 1.17549435e-38f) || (c != 0 && c < 1.17549435e-38f)) subnormal++;
                }
            }
            Measure.Report($"Frequency feedback {feedback} % for 60 s of noise at 0.5: peak {peak:0.00}, subnormal samples {subnormal}");
            Assert.That(peak, Is.LessThan(8.0), "bounded");
            Assert.That(subnormal, Is.EqualTo(0), "no denormals");
        }
    
        // ================================================================================== latency, Mix and Amount

        static int Peak(float[] x)
        {
            int at = 0;
            for (int i = 0; i < x.Length; i++) if (Math.Abs(x[i]) > Math.Abs(x[at])) at = i;
            return at;
        }

        [Test]
        public void TheReportedLatencyIsTheMeasuredDelayOfTheFastShifterAtEveryWindow([Values(10, 20, 40, 63, 100)] int window)
        {
            var v = Pitch("Fast", 0, 0, window);
            var e = Engine(v);
            var l = new float[Rate]; var r = new float[Rate];
            l[3000] = 0.9f; r[3000] = -0.6f;
            var (ol, or) = Render(e, l, r);
            Assert.That(e.Latency, Is.EqualTo(DelayShifter.WindowSamples(window, Rate) / 2));
            Assert.That(Peak(ol), Is.EqualTo(3000 + e.Latency), "the impulse comes out exactly the reported latency late");
            Assert.That(or[Peak(ol)] / ol[Peak(ol)], Is.EqualTo(-0.6 / 0.9).Within(1e-4), "the impulse keeps its shape (the default 22 kHz high cut rounds its top a little)");
            Assert.That(ol[Peak(ol)], Is.GreaterThan(0.7f));
        }

        [Test]
        public void TheReportedLatencyIsTheMeasuredDelayOfTheSmoothShifter()
        {
            var v = Pitch("Smooth", 0);
            var e = Engine(v);
            var l = new float[Rate]; var r = new float[Rate];
            l[3000] = 0.9f; r[3000] = -0.6f;
            var (ol, _) = Render(e, l, r);
            Assert.That(e.Latency, Is.EqualTo(2047));
            Assert.That(Peak(ol), Is.EqualTo(3000 + 2047));
            Assert.That(ol[Peak(ol)], Is.GreaterThan(0.7f));
        }

        [Test]
        public void TheLatencyBelongsToTheQualityAndIsTheSameInEveryMode([Values("Fast", "Smooth")] string quality)
        {
            int expect = quality == "Smooth" ? 2047 : DelayShifter.WindowSamples(40, Rate) / 2;
            foreach (string mode in new[] { "Pitch", "Frequency", "Ring" })
            {
                var v = Fresh(p => { Choice(p, P.Mode, mode); Choice(p, P.Quality, quality); Phys(p, P.Window, 40); Phys(p, P.Shift, 0); Phys(p, P.Pitch, 0); Phys(p, P.Depth, 0); Phys(p, P.Carrier, 1); });
                var e = Engine(v);
                Assert.That(e.Latency, Is.EqualTo(expect), mode);
                // Noise through the mode at no shift: its cross-correlation with the input peaks at the latency (the frequency shifter's allpass pair adds a few samples of phase delay).
                var x = Noise(Rate * 2, 0.3, 3);
                var (y, _) = Render(e, x, Copy(x));
                int best = -1; double bestC = 0;
                for (int d = expect - 12; d <= expect + 12; d++)
                {
                    double c = 0;
                    for (int i = Rate / 2; i < Rate * 3 / 2; i++) c += (double)y[i] * x[i - d];
                    if (Math.Abs(c) > bestC) { bestC = Math.Abs(c); best = d; }
                }
                Measure.Report($"{quality}, {mode}: reported {expect} samples, measured {best}");
                Assert.That(best, Is.EqualTo(expect).Within(mode == "Frequency" ? 6 : 0), mode);
            }
        }

        [Test]
        public void AmountZeroAndMixZeroPassTheInputBitForBitDelayedByTheLatencyInEveryMode([Values("Pitch", "Frequency", "Ring")] string mode, [Values("Fast", "Smooth")] string quality, [Values("Amount", "Mix")] string which)
        {
            var v = Fresh(p =>
            {
                Choice(p, P.Mode, mode); Choice(p, P.Quality, quality);
                Phys(p, P.Pitch, 7); Phys(p, P.Detune, 20); Phys(p, P.Shift, 300); Phys(p, P.Feedback, 50); Phys(p, P.Carrier, 500);
                Phys(p, which == "Amount" ? P.Amount : P.Mix, 0);
            });
            var e = Engine(v);
            var (l, r) = ChopRig.DrumLoop(Rate * 3, 120);
            var (ol, or) = Render(e, l, r);
            int lat = e.Latency;
            for (int i = lat; i < l.Length; i++)
                if (ol[i] != l[i - lat] || or[i] != r[i - lat]) Assert.Fail($"sample {i}: {ol[i]} against {l[i - lat]}");
        }

        [Test]
        public void MixHalfIsTheAverageOfTheDryDelayedByTheLatencyAndTheWet([Values("Fast", "Smooth")] string quality)
        {
            var full = Pitch(quality, 5);
            var half = Pitch(quality, 5);
            Phys(half, P.Mix, 0.5);
            var x = Noise(Rate * 2, 0.3, 5);
            var (wet, _) = Render(Engine(full), x, Copy(x));
            var e = Engine(half);
            var (mix, _) = Render(e, x, Copy(x));
            int lat = e.Latency;
            double worst = 0;
            for (int i = lat + 4096; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(mix[i] - 0.5f * (wet[i] + x[i - lat])));
            Assert.That(worst, Is.LessThan(1e-5));
        }

        [Test]
        public void AmountAtOneHundredPercentIsTheSettingsAsTheyAreAndScalesTheRest()
        {
            var v = Fresh(p =>
            {
                Phys(p, P.Pitch, 5); Phys(p, P.Fine, 20); Phys(p, P.Detune, 10); Phys(p, P.Shift, 300); Phys(p, P.ShiftFine, 1); Phys(p, P.Feedback, 40);
                Phys(p, P.Spread, 0.3); Phys(p, P.Depth, 0.6);
            });
            var e = Engine(v);
            var x = new float[256]; var y = new float[256];
            int[] scaled = { P.Pitch, P.Fine, P.Detune, P.Shift, P.ShiftFine, P.Feedback, P.Spread, P.Depth };
            e.Process(x, y, 256);
            foreach (int p in scaled) Assert.That(e.Effective.Value(p), Is.EqualTo(v.Value(p)).Within(1e-12), Params.Table[p].Name + " at 100 %");
            Phys(v, P.Amount, 200);
            e.Process(x, y, 256);
            Assert.That(e.Effective.Value(P.Pitch), Is.EqualTo(10).Within(1e-9));
            Assert.That(e.Effective.Value(P.Fine), Is.EqualTo(40).Within(1e-9));
            Assert.That(e.Effective.Value(P.Shift), Is.EqualTo(600).Within(1e-9));
            Assert.That(e.Effective.Value(P.Feedback), Is.EqualTo(80).Within(0.5));
            Assert.That(e.Effective.Value(P.Depth), Is.EqualTo(1.0).Within(1e-9), "kept inside the parameter's range: a full ring is as far as Depth goes");
            Phys(v, P.Amount, 50);
            e.Process(x, y, 256);
            Assert.That(e.Effective.Value(P.Pitch), Is.EqualTo(2.5).Within(1e-9));
            Assert.That(e.Effective.Value(P.Shift), Is.EqualTo(150).Within(1e-9));
        }

        [Test]
        public void AmountTwoHundredMovesASeventhUpToAnOctaveAndTwoSemitones()
        {
            var v = Pitch("Smooth", 7);
            Phys(v, P.Amount, 200);
            var x = Sine(Rate * 6, 440, 0.5);
            var (l, _) = Render(v, x, Copy(x));
            int n = 1 << 17;
            double f = PeakHz(Spectrum(l, l.Length - n, n), n, 440 * Math.Pow(2, 14 / 12.0));
            Assert.That(Math.Abs(Cents(f, 440 * Math.Pow(2, 14 / 12.0))), Is.LessThan(1));
        }

        // ================================================================================== switching

        [Test]
        public void ChangingTheModeOrTheQualityWhilePlayingMakesNoClick()
        {
            var x = Sine(Rate * 8, 440, 0.4);
            var v = Pitch("Fast", 7);
            Phys(v, P.Shift, 100); Phys(v, P.Carrier, 30); Phys(v, P.Depth, 0);
            var steps = new (int Mode, int Quality)[] { (0, 1), (1, 0), (2, 0), (0, 0), (0, 1), (2, 1), (1, 1), (0, 0) };
            var (y, _) = Render(Engine(v), x, Copy(x), 256, pos =>
            {
                int k = pos / (Rate);
                if (pos % Rate < 256 && k < steps.Length)
                {
                    v.Set(P.Mode, steps[k].Mode);
                    v.Set(P.Quality, steps[k].Quality);
                }
            });
            // (the delay of Mix 100 % does not matter: the wet path alone)
            var clicks = ClickDetector.Find(y);
            Assert.That(clicks, Is.Empty, "clicks at " + string.Join(", ", clicks.Take(8)));
        }

        // ================================================================================== notes

        [Test]
        public void FollowNotesShiftsByTheNoteMinusTheRootInPitchMode([Values("Fast", "Smooth")] string quality)
        {
            var v = Pitch(quality, 0);
            Switch(v, P.Follow, true);
            Phys(v, P.Root, 60);
            var x = Sine(Rate * 6, 440, 0.5);
            var e = Engine(v);
            var (l, _) = Render(e, x, Copy(x), 256, null, new[] { (Rate * 2, true, 67), (Rate * 4, true, 55) });
            int n = 1 << 15;
            double up = PeakHz(Spectrum(l, Rate * 4 - n - 3000, n), n, 440 * Math.Pow(2, 7 / 12.0));
            double down = PeakHz(Spectrum(l, l.Length - n, n), n, 440 * Math.Pow(2, -5 / 12.0));
            Assert.That(Cents(up, 440 * Math.Pow(2, 7 / 12.0)), Is.EqualTo(0).Within(1.5), quality + ": G4 over C4 is a fifth up");
            Assert.That(Cents(down, 440 * Math.Pow(2, -5 / 12.0)), Is.EqualTo(0).Within(1.5), quality + ": G3 is a fourth down");
        }

        [Test]
        public void FollowNotesOffLeavesTheNotesWithoutEffect()
        {
            var v = Pitch("Fast", 0);
            var x = Sine(Rate * 3, 440, 0.5);
            var (l, _) = Render(Engine(v), x, Copy(x), 256, null, new[] { (Rate, true, 72) });
            int n = 1 << 15;
            Assert.That(Cents(PeakHz(Spectrum(l, l.Length - n, n), n, 440), 440), Is.EqualTo(0).Within(1));
        }

        [Test]
        public void FollowNotesSetsTheRingCarrierToTheNotesFrequency()
        {
            var v = RingMode(300);
            Switch(v, P.Follow, true);
            var n = 1 << 17;
            double f = Math.Round(1000.0 * n / Rate) * Rate / n;
            var x = Sine(Rate * 6, f, 0.5);
            var (l, _) = Render(Engine(v), x, Copy(x), 256, null, new[] { (Rate, true, 57) });      // A3 = 220 Hz
            var m = Spectrum(l, l.Length - n, n);
            Assert.That(PeakHz(m, n, f + 220), Is.EqualTo(f + 220).Within(0.1));
            Assert.That(PeakHz(m, n, f - 220), Is.EqualTo(f - 220).Within(0.1));
        }

        [Test]
        public void TheEngineFollowsTheLatestNoteAndForgetsThemWhenTheSongStops()
        {
            var v = Pitch("Fast", 3);
            Switch(v, P.Follow, true);
            var e = Engine(v);
            Assert.That(e.FollowedNote, Is.EqualTo(-1));
            e.NoteOn(60); e.NoteOn(64);
            Assert.That(e.FollowedNote, Is.EqualTo(64));
            e.NoteOff(64);
            Assert.That(e.FollowedNote, Is.EqualTo(64), "the shift stays on the last note");
            Assert.That(e.AnyNote, Is.True);
            e.ClearNotes();
            Assert.That(e.FollowedNote, Is.EqualTo(-1));
            Assert.That(e.AnyNote, Is.False);
        }

        // ================================================================================== the wet path

        [Test]
        public void LowCutAndHighCutActOnTheWetPathOnly()
        {
            var v = Pitch("Fast", 0);
            Phys(v, P.LowCut, 500); Phys(v, P.HighCut, 2000);
            var x = Sum(Sine(Rate * 4, 100, 0.2), Sum(Sine(Rate * 4, 1000, 0.2), Sine(Rate * 4, 8000, 0.2)));
            var (y, _) = Render(Engine(v), x, Copy(x));
            int n = 1 << 15;
            var m = Spectrum(y, y.Length - n, n);
            double a100 = Line(m, n, 100), a1k = Line(m, n, 1000), a8k = Line(m, n, 8000);
            Assert.That(Measure.Db(a100 / a1k), Is.LessThan(-12), "100 Hz is under a 500 Hz high-pass");
            Assert.That(Measure.Db(a8k / a1k), Is.LessThan(-20), "8 kHz is over a 2 kHz low-pass");
            // Mix 0: the dry has no cut at all
            var dry = Pitch("Fast", 0); Phys(dry, P.LowCut, 500); Phys(dry, P.HighCut, 2000); Phys(dry, P.Mix, 0);
            var (z, _) = Render(Engine(dry), x, Copy(x));
            var mz = Spectrum(z, z.Length - n, n);
            Assert.That(Measure.Db(Line(mz, n, 100) / Line(mz, n, 1000)), Is.EqualTo(0).Within(0.5));
        }

        [Test]
        public void OutputSetsTheWetLevel()
        {
            var a = Pitch("Fast", 0); var b = Pitch("Fast", 0); Phys(b, P.Output, 6);
            var x = Sine(Rate * 3, 440, 0.25);
            var (ya, _) = Render(Engine(a), x, Copy(x));
            var (yb, _) = Render(Engine(b), x, Copy(x));
            Assert.That(Measure.Db(Measure.Rms(yb, Rate, Rate) / Measure.Rms(ya, Rate, Rate)), Is.EqualTo(6.0).Within(0.05));
        }

        // ================================================================================== the audio thread

        [Test]
        public void NothingIsAllocatedPerBlockInEveryModeWithTheControlsMoving([Values("Fast", "Smooth", "Frequency", "Ring")] string path)
        {
            var v = Fresh(p =>
            {
                Choice(p, P.Mode, path == "Frequency" ? "Frequency" : path == "Ring" ? "Ring" : "Pitch");
                Choice(p, P.Quality, path == "Smooth" ? "Smooth" : "Fast");
                Phys(p, P.Detune, 12); Phys(p, P.Feedback, 40); Phys(p, P.Spread, 0.5); Phys(p, P.Depth, 0.7);
                Switch(p, P.Follow, true);
            });
            var e = Engine(v);
            var (dl, dr) = ChopRig.DrumLoop(Rate * 12, 120);
            var l = new float[256]; var r = new float[256];
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                int pos = (b % 2000) * 256;
                Array.Copy(dl, pos, l, 0, 256);
                Array.Copy(dr, pos, r, 0, 256);
                if (b % 211 == 5) e.NoteOn(60 + b % 13);
                if (b % 211 == 150) e.NoteOff(60);
                v.Set(P.Pitch, 12 + b % 24);
                v.Set(P.Shift, 1500 + b % 1000);
                v.Set(P.Carrier, 3000 + b % 1500);
                v.Set(P.Window, 10 + b % 60);
                v.Set(P.LowCut, b % 300);
                if (b % 500 == 499) { v.Set(P.Mode, (b / 500) % 3); v.Set(P.Quality, (b / 500) % 2); }
                e.Process(l, r, 256);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void TheCpuAgainstTheShifterFxOfDyeEssence()
        {
            // Ten seconds of music through each, the fastest of five passes: Neo Shift's three engines against the two shifter effects of Dye Essence.
            var (dl, dr) = ChopRig.DrumLoop(Rate * 10, 120);
            double Time(Action<float[], float[], int> process)
            {
                var l = new float[256]; var r = new float[256];
                double best = double.MaxValue;
                for (int pass = 0; pass < 6; pass++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int pos = 0; pos + 256 <= Rate * 10; pos += 256)
                    {
                        Array.Copy(dl, pos, l, 0, 256); Array.Copy(dr, pos, r, 0, 256);
                        process(l, r, 256);
                    }
                    if (pass > 0) best = Math.Min(best, sw.Elapsed.TotalSeconds);
                }
                return best;
            }
            var dyeShift = new FrequencyShifter(Rate) { ShiftHz = 100 };
            double tDyeShift = Time((l, r, n) => dyeShift.Process(l, r, n));
            var dyePitch = new PitchShiftDelay(Rate) { Semitones = 7, Feedback = 0, GrainMs = 40, TimeMs = 10 };
            double tDyePitch = Time((l, r, n) => dyePitch.Process(l, r, n));
            double Mine(Action<ParamValues> setup)
            {
                var e = Engine(Fresh(setup));
                return Time((l, r, n) => e.Process(l, r, n));
            }
            double tFast = Mine(p => { Choice(p, P.Quality, "Fast"); Phys(p, P.Pitch, 7); Phys(p, P.Window, 40); });
            double tSmooth = Mine(p => { Choice(p, P.Quality, "Smooth"); Phys(p, P.Pitch, 7); });
            double tFreq = Mine(p => { Choice(p, P.Mode, "Frequency"); Phys(p, P.Shift, 100); });
            double tRing = Mine(p => { Choice(p, P.Mode, "Ring"); Phys(p, P.Carrier, 440); });
            Measure.Report($"CPU, 10 s of stereo music (ms): Dye Essence frequency shifter {tDyeShift * 1000:0}, Dye Essence pitch-shift delay {tDyePitch * 1000:0}; " +
                $"Neo Shift Fast {tFast * 1000:0} ({tFast / tDyePitch:0.00}x the pitch delay), Smooth {tSmooth * 1000:0} ({tSmooth / tDyePitch:0.00}x), " +
                $"Frequency {tFreq * 1000:0} ({tFreq / tDyeShift:0.00}x the frequency shifter), Ring {tRing * 1000:0}");
            Assert.That(tFreq, Is.LessThan(tDyeShift * 4.5), "the frequency shifter, with its 80 dB pair, against Dye Essence's 60 dB one");
            Assert.That(tFast, Is.LessThan(tDyePitch * 4.0 + 0.2), "the Fast shifter, with its waveform alignment");
            Assert.That(tSmooth, Is.LessThan(Rate * 10 / 256 * 0.0005 + 0.5), "the phase vocoder is well under real time");
        }
    }
}

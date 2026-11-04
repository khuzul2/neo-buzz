using System.Diagnostics;
using NeoBuzz.Drums;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Drums (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class DrumsTests
    {
        const int Rate = Measure.Rate;
        const int Kick = 0, Snare = 1, Clap = 2, Rim = 3, ClosedHat = 4, OpenHat = 5, LowTom = 6, MidTom = 7, HighTom = 8, Cowbell = 9, Crash = 10, Ride = 11;

        internal static void Set(ParamValues v, int p, double value) => v.Set(p, NeoBuzz.Drums.Params.Table[p].Raw(value));

        /// <summary>An engine at <paramref name="rate"/>, velocity not touching the level or the tone unless asked.</summary>
        internal static (DrumsEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate, bool velocity = false)
        {
            var v = new ParamValues(NeoBuzz.Drums.Params.Table);
            if (!velocity)
            {
                v.Set(P.VelLevel, 0);
                v.Set(P.VelTone, 0);
            }
            setup?.Invoke(v);
            var e = new DrumsEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        /// <summary>
        /// Renders <paramref name="length"/> samples in blocks of <paramref name="block"/>, split at the events' samples so each
        /// lands exactly there whatever the block size (as ReBuzz splits its buffers at ticks). Left and right.
        /// </summary>
        internal static (float[] L, float[] R) Run(DrumsEngine e, int length, int block = DrumsEngine.MaxBlock, params (int At, Action Do)[] events)
        {
            var l = new float[length];
            var r = new float[length];
            var bl = new float[DrumsEngine.MaxBlock];
            var br = new float[DrumsEngine.MaxBlock];
            var queue = events.OrderBy(x => x.At).ToList();
            int k = 0;
            for (int pos = 0; pos < length;)
            {
                while (k < queue.Count && queue[k].At <= pos) queue[k++].Do();
                int n = Math.Min(block, length - pos);
                if (k < queue.Count) n = Math.Min(n, queue[k].At - pos);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, l, pos, n);
                Array.Copy(br, 0, r, pos, n);
                pos += n;
            }
            return (l, r);
        }

        /// <summary>The mono sum (L + R) / 2 of a hit of <paramref name="voice"/> at velocity 127.</summary>
        static float[] Hit(int voice, double seconds, Action<ParamValues> setup = null, int rate = Rate)
        {
            var (e, _) = Make(setup, rate);
            var (l, r) = Run(e, (int)(seconds * rate), DrumsEngine.MaxBlock, (0, () => e.Hit(voice, 1f)));
            var x = new float[l.Length];
            for (int i = 0; i < x.Length; i++) x[i] = 0.5f * (l[i] + r[i]);
            return x;
        }

        static double MaxStep(float[] x, int from, int to)
        {
            double m = 0;
            for (int i = Math.Max(1, from); i < Math.Min(to, x.Length); i++) m = Math.Max(m, Math.Abs(x[i] - x[i - 1]));
            return m;
        }

        static double Rms(float[] x, int from, int n) => Measure.Rms(x, from, Math.Min(n, x.Length - from));

        /// <summary>Seconds for the 20 ms RMS to fall from 20 dB under its highest to 60 dB under, times 3/2: the T60 of the decay's straight part in dB.</summary>
        static double T60(float[] x, int rate = Rate)
        {
            int w = rate / 50, step = rate / 1000;
            double top = 0;
            int topAt = 0;
            for (int i = 0; i + w <= x.Length; i += step)
            {
                double r = Rms(x, i, w);
                if (r > top) { top = r; topAt = i; }
            }
            int At(double db)
            {
                double limit = top * Math.Pow(10, -db / 20);
                for (int i = topAt; i + w <= x.Length; i += step)
                    if (Rms(x, i, w) < limit) return i;
                return -1;
            }
            int a = At(20), b = At(50);
            Assert.That(a, Is.GreaterThan(0), "it falls 20 dB");
            Assert.That(b, Is.GreaterThan(a), "and 50 dB within the render");
            return (b - a) / (double)rate * 60.0 / 30.0;
        }

        /// <summary>The strongest frequency from <paramref name="from"/> over <paramref name="n"/> samples, 20 Hz – <paramref name="hi"/>.</summary>
        static double Strongest(float[] x, int from, int n, double lo = 20, double hi = 5000)
        {
            const int size = 1 << 17;
            var w = new float[size];
            for (int i = 0; i < n; i++) w[i] = x[from + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / n));
            var mag = Fft.Magnitudes(w, 0, size);
            int kLo = (int)(lo * size / Rate), kHi = (int)(hi * size / Rate);
            int best = kLo;
            for (int k = kLo; k <= kHi; k++) if (mag[k] > mag[best]) best = k;
            double a = Math.Log(mag[best - 1] + 1e-30), b = Math.Log(mag[best] + 1e-30), c = Math.Log(mag[best + 1] + 1e-30);
            return (best + 0.5 * (a - c) / (a - 2 * b + c)) * Rate / size;
        }

        // ------------------------------------------------------------------ the units

        [Test]
        public void TunedVoicesSoundAtTheirTuneOnceTheirSweepHasSettled()
        {
            var worst = new List<string>();
            foreach (var (voice, param, values, settle) in new (int, int, double[], double)[]
                     {
                         (Kick, P.KickTune, new[] { 30.0, 45, 60, 90, 150 }, 0.25),
                         (LowTom, P.LtTune, new[] { 40.0, 80, 160, 320, 500 }, 1.6),
                         (MidTom, P.MtTune, new[] { 60.0, 140, 300 }, 1.6),
                         (HighTom, P.HtTune, new[] { 90.0, 190, 450 }, 1.6),
                         (Snare, P.SnareTune, new[] { 100.0, 185, 400 }, 0.06),
                         (Rim, P.RimTune, new[] { 500.0, 1700, 4000 }, 0.002),
                     })
            {
                double most = 0;
                foreach (double hz in values)
                {
                    var x = Hit(voice, Math.Max(1.5, settle + 0.5), v =>
                    {
                        Set(v, param, hz);
                        if (voice == Kick) Set(v, P.KickDecay, 4.0);
                        if (voice >= LowTom && voice <= HighTom) Set(v, DrumsEngine.Voice[voice].Decay, 3.0);
                        if (voice == Snare) { Set(v, P.SnareDecay, 2.0); Set(v, P.SnareSnap, 0.0); }
                        if (voice == Rim) Set(v, P.RimDecay, 0.5);
                    });
                    int from = (int)(settle * Rate), n = voice == Rim ? Rate / 20 : Rate / 4;
                    double f = Strongest(x, from, n, 0.7 * hz, 1.3 * hz);
                    double cents = Measure.Cents(f, hz);
                    most = Math.Max(most, Math.Abs(cents));
                    Assert.That(Math.Abs(cents), Is.LessThan(5), $"{DrumsEngine.Names[voice]} at {hz} Hz: {f:F2} Hz");
                }
                worst.Add($"{DrumsEngine.Names[voice]} {most:F2} cents");
            }
            Measure.Report("Neo Drums, pitch after the sweep (Kick, toms, the snare's lower tone, the rim's ping): worst " + string.Join(", ", worst));
        }

        [Test]
        public void TheKickSweepsFromItsSweepAboveItsTuneWithItsTimeConstant()
        {
            // Tune 50 Hz, Sweep 24 st, sweep time 20 ms: f(t) = 50 · 2^(2 e^(−t / 20 ms)), 200 Hz at the start. Each cycle of
            // the body (between rising zero crossings) lasts as long as that f takes to make one cycle, within 1 % (a sample's
            // rounding, at 720 Hz falling by 36 st in 5 ms).
            foreach (var (tune, sweep, time) in new[] { (50.0, 24.0, 0.02), (40.0, 12.0, 0.08), (90.0, 36.0, 0.005) })
            {
                var x = Hit(Kick, 1, v => { Set(v, P.KickTune, tune); Set(v, P.KickSweep, sweep); Set(v, P.KickSweepT, time); Set(v, P.KickClick, 0.0); Set(v, P.KickDrive, 0.0); Set(v, P.KickDecay, 4.0); });
                double tuneSet = NeoBuzz.Drums.Params.Table[P.KickTune].Value(NeoBuzz.Drums.Params.Table[P.KickTune].Raw(tune));
                double timeSet = NeoBuzz.Drums.Params.Table[P.KickSweepT].Value(NeoBuzz.Drums.Params.Table[P.KickSweepT].Raw(time));
                double F(double t) => tuneSet * Math.Pow(2, sweep / 12 * Math.Exp(-t / timeSet));
                // The model's crossings: where its phase (cycles since the start) is a whole number.
                var model = new List<double>();
                double phase = 0, dt = 1e-6;
                for (double t = 0; t < 0.5; t += dt)
                {
                    double next = phase + F(t + dt / 2) * dt;
                    if (Math.Floor(next) > Math.Floor(phase)) model.Add(t + dt * (Math.Floor(next) - phase) / (next - phase));
                    phase = next;
                }
                var measured = new List<double>();
                for (int i = 1; i < x.Length && measured.Count < model.Count; i++)
                    if (x[i - 1] < 0 && x[i] >= 0) measured.Add((i - 1 + x[i - 1] / (x[i - 1] - x[i])) / Rate);
                double worst = 0;
                for (int k = 1; k < Math.Min(model.Count, measured.Count); k++)
                {
                    double fm = 1 / (measured[k] - measured[k - 1]), fe = 1 / (model[k] - model[k - 1]);
                    worst = Math.Max(worst, Math.Abs(fm / fe - 1));
                }
                Measure.Report($"Neo Drums, kick {tune} Hz, sweep {sweep} st over {time * 1000} ms: each cycle's pitch within {worst * 100:F3} % of 2^(sweep · e^(−t/time)) above its tune (first {model.Count - 1} cycles)");
                Assert.That(worst, Is.LessThan(0.01));
            }
        }

        [Test]
        public void DecayIsTheTimeToFall60Db()
        {
            var lines = new List<string>();
            foreach (var (voice, values) in new (int, double[])[]
                     {
                         (Kick, new[] { 0.1, 0.5, 2.0 }), (Snare, new[] { 0.1, 0.3, 1.0 }), (Clap, new[] { 0.2, 0.6, 1.5 }), (Rim, new[] { 0.03, 0.1, 0.4 }),
                         (ClosedHat, new[] { 0.05, 0.1, 0.5 }), (OpenHat, new[] { 0.3, 1.0, 3.0 }), (LowTom, new[] { 0.2, 0.6, 2.0 }),
                         (MidTom, new[] { 0.5 }), (HighTom, new[] { 0.5 }), (Cowbell, new[] { 0.3, 1.0 }), (Crash, new[] { 1.0, 3.0 }), (Ride, new[] { 1.0, 3.0 }),
                     })
            {
                var row = new List<string>();
                foreach (double t in values)
                {
                    var x = Hit(voice, Math.Max(1, 1.5 * t + 0.3), v =>
                    {
                        Set(v, DrumsEngine.Voice[voice].Decay, t);
                        if (voice == Snare) Set(v, P.SnareSnap, 1.0);      // the noise's decay is Decay (the body takes about half)
                    });
                    double measured = T60(x);
                    row.Add($"{t:0.###}→{measured:0.###}");
                    Assert.That(measured, Is.EqualTo(t).Within(8).Percent, $"{DrumsEngine.Names[voice]} at {t} s");
                }
                lines.Add($"{DrumsEngine.Names[voice]} {string.Join(" ", row)}");
            }
            Measure.Report("Neo Drums, decay set → measured (s, the straight part's fall to −60 dB): " + string.Join("; ", lines));
        }

        [Test]
        public void LevelIsInDecibelsAndPanIsEqualPower()
        {
            foreach (int voice in Enumerable.Range(0, DrumsEngine.VoiceCount))
            {
                var p = DrumsEngine.Voice[voice];
                double Peak(double db, double pan, bool left)
                {
                    var (e, _) = Make(v => { Set(v, p.Level, db); Set(v, p.Pan, pan); });
                    var (l, r) = Run(e, Rate / 4, DrumsEngine.MaxBlock, (0, () => e.Hit(voice, 1f)));
                    return Measure.Peak(left ? l : r, 0, l.Length);
                }
                double zero = Peak(0, 0, true);
                Assert.That(Measure.Db(Peak(-6, 0, true) / zero), Is.EqualTo(-6).Within(0.01), $"{DrumsEngine.Names[voice]}: −6 dB");
                Assert.That(Measure.Db(Peak(-24.5, 0, true) / zero), Is.EqualTo(-24.5).Within(0.01), $"{DrumsEngine.Names[voice]}: −24.5 dB");
                Assert.That(Peak(double.NegativeInfinity, 0, true), Is.EqualTo(0), $"{DrumsEngine.Names[voice]}: −∞ dB is silence");
                Assert.That(Peak(0, 0, true), Is.EqualTo(Peak(0, 0, false)), "centred: both sides alike");
                Assert.That(Peak(0, -100, false), Is.LessThan(1e-6 * zero), "hard left: nothing on the right");
                Assert.That(Measure.Db(Peak(0, -100, true) / zero), Is.EqualTo(3.01).Within(0.02), "hard left: +3 dB on the left (equal power, unity in the centre)");
            }
        }

        [Test]
        public void VelocityAccentAndVelToneDoWhatTheySay()
        {
            double PeakOf(int voice, float velocity, bool accent, Action<ParamValues> setup)
            {
                var (e, _) = Make(setup, velocity: true);
                var (l, _) = Run(e, Rate / 4, DrumsEngine.MaxBlock, (0, () => e.Hit(voice, velocity, accent)));
                return Measure.Peak(l, 0, l.Length);
            }
            void Flat(ParamValues v) { v.Set(P.VelTone, 0); Set(v, P.VelLevel, 1.0); }
            // Vel → level 100 %: the gain is velocity²; velocity 64 against 127: 20·log10((64/127)²) = −11.95 dB.
            double full = PeakOf(Kick, 1f, false, Flat), half = PeakOf(Kick, 64 / 127f, false, Flat);
            Assert.That(Measure.Db(half / full), Is.EqualTo(40 * Math.Log10(64 / 127.0)).Within(0.02));
            // Vel → level 0 %: every velocity at full level.
            Assert.That(PeakOf(Kick, 0.3f, false, v => { v.Set(P.VelTone, 0); v.Set(P.VelLevel, 0); }), Is.EqualTo(PeakOf(Kick, 1f, false, v => { v.Set(P.VelTone, 0); v.Set(P.VelLevel, 0); })));
            // Accent: louder by Accent gain, exactly (the tone left alone here).
            foreach (double db in new[] { 0.0, 3.5, 12.0 })
            {
                double plain = PeakOf(Snare, 1f, false, v => { Flat(v); Set(v, P.Accent, db); });
                double accented = PeakOf(Snare, 1f, true, v => { Flat(v); Set(v, P.Accent, db); });
                Assert.That(Measure.Db(accented / plain), Is.EqualTo(db).Within(0.01), $"accent {db} dB");
            }
            // Vel → tone: harder hits are brighter (the hats' centroid, the snare's noise against its body); velocity 100 is as set.
            double Centroid(int voice, float velocity, bool accent)
            {
                var (e, _) = Make(v => { v.Set(P.VelLevel, 0); Set(v, P.VelTone, 1.0); }, velocity: true);
                var (l, _) = Run(e, Rate / 2, DrumsEngine.MaxBlock, (0, () => e.Hit(voice, velocity, accent)));
                var mag = Measure.Spectrum(l, 0, 16384);
                double num = 0, den = 0;
                for (int k = 1; k < mag.Length; k++) { double f = Measure.BinFrequency(k, 16384); num += f * mag[k] * mag[k]; den += mag[k] * mag[k]; }
                return num / den;
            }
            foreach (int voice in new[] { Snare, ClosedHat, Crash })
            {
                double soft = Centroid(voice, 40 / 127f, false), mid = Centroid(voice, 100 / 127f, false), hard = Centroid(voice, 1f, false), accent = Centroid(voice, 1f, true);
                Assert.That(soft, Is.LessThan(mid), $"{DrumsEngine.Names[voice]}: soft darker");
                Assert.That(hard, Is.GreaterThan(mid), $"{DrumsEngine.Names[voice]}: hard brighter");
                Assert.That(accent, Is.GreaterThan(hard), $"{DrumsEngine.Names[voice]}: accent brighter still");
                Measure.Report($"Neo Drums, {DrumsEngine.Names[voice]} centroid at velocity 40, 100, 127, 127 accented (Vel → tone 100 %): {soft:F0}, {mid:F0}, {hard:F0}, {accent:F0} Hz");
            }
        }

        // ------------------------------------------------------------------ aliasing

        /// <summary>A 7-term Blackman–Harris window (sidelobes under −180 dB): an inharmonic set of partials measured without leakage.</summary>
        static double[] Bh7(int n)
        {
            double[] a = { 0.27105140069342, 0.43329793923448, 0.21812299954311, 0.06592544638803, 0.01081174209837, 0.00077658482522, 0.00001388721735 };
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int k = 0; k < a.Length; k++) s += (k % 2 == 0 ? 1 : -1) * a[k] * Math.Cos(2 * Math.PI * k * (i + 0.5) / n);
                w[i] = s;
            }
            return w;
        }

        /// <summary>
        /// The loudest component (20 Hz – 20 kHz) that isn't one of the oscillators' odd harmonics below Nyquist, relative to the
        /// loudest that is: with the noise off, all a metallic voice has are its squares' harmonics, filtered, so anything else
        /// is an alias. Measured over 65 536 samples from 0.2 s in, the decay at its longest.
        /// </summary>
        static double AliasFloor(int voice, double tune, double tone, double[] baseHz, int rate = Rate)
        {
            DrumHit.MetalOnly = true;
            try
            {
                const int n = 1 << 16;
                var x = Hit(voice, 0.2 + n / (double)rate + 0.01, v =>
                {
                    var p = DrumsEngine.Voice[voice];
                    if (voice == Cowbell) Set(v, p.Tune, tune);
                    else Set(v, p.Tune, tune);
                    if (p.Tone >= 0) Set(v, p.Tone, tone);
                    v.Set(p.Decay, NeoBuzz.Drums.Params.Table[p.Decay].Max);
                }, rate);
                var w = Bh7(n);
                var re = new double[n];
                int from = (int)(0.2 * rate);
                for (int i = 0; i < n; i++) re[i] = x[from + i] * w[i];
                var mag = new double[n / 2 + 1];
                var power = Power(re);
                for (int k = 0; k < mag.Length; k++) mag[k] = Math.Sqrt(power[k]);
                var expected = new bool[mag.Length];
                double binHz = (double)rate / n;
                foreach (double f0 in baseHz)
                    for (int h = 1; h * f0 < rate / 2.0; h += 2)
                    {
                        int c = (int)Math.Round(h * f0 / binHz);
                        for (int k = Math.Max(0, c - 12); k <= Math.Min(mag.Length - 1, c + 12); k++) expected[k] = true;
                    }
                double loudest = 0, alias = 1e-30;
                int lo = (int)(20 / binHz), hi = (int)(20000 / binHz);
                for (int k = lo; k <= hi; k++)
                {
                    if (expected[k]) loudest = Math.Max(loudest, mag[k]);
                    else alias = Math.Max(alias, mag[k]);
                }
                return Measure.Db(alias / loudest);
            }
            finally
            {
                DrumHit.MetalOnly = false;
            }
        }

        static double[] Power(double[] x)
        {
            int n = x.Length;
            var re = (double[])x.Clone();
            var im = new double[n];
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double a = -2 * Math.PI / len;
                for (int i = 0; i < n; i += len)
                    for (int k = 0; k < len / 2; k++)
                    {
                        double cr = Math.Cos(a * k), ci = Math.Sin(a * k);
                        int p = i + k, q = p + len / 2;
                        double tr = re[q] * cr - im[q] * ci, ti = re[q] * ci + im[q] * cr;
                        re[q] = re[p] - tr; im[q] = im[p] - ti;
                        re[p] += tr; im[p] += ti;
                    }
            }
            var pw = new double[n / 2 + 1];
            for (int k = 0; k < pw.Length; k++) pw[k] = re[k] * re[k] + im[k] * im[k];
            return pw;
        }

        [Test]
        public void HatsCymbalsAndCowbellDoNotAlias()
        {
            var lines = new List<string>();
            double worst = -999;
            foreach (int rate in new[] { 48000, 44100 })
                foreach (var (voice, scale) in new[] { (ClosedHat, 1.0), (OpenHat, 1.0), (Crash, 1.0), (Ride, 1.25) })
                    foreach (double tune in new[] { -12.0, 0, 12 })
                        foreach (double tone in new[] { 4000.0, 16000 })
                        {
                            double t = Math.Pow(2, tune / 12);
                            double floor = AliasFloor(voice, tune, tone, MetalBank.Tr808.Select(f => f * t * scale).ToArray(), rate);
                            worst = Math.Max(worst, floor);
                            if (tone == 16000 && tune == 12) lines.Add($"{DrumsEngine.Names[voice]} +12 st, tone 16 kHz, {rate / 1000.0:0.#} kHz: {floor:F1} dB");
                            Assert.That(floor, Is.LessThan(-90), $"{DrumsEngine.Names[voice]}, tune {tune} st, tone {tone} Hz, {rate} Hz");
                        }
            foreach (double hz in new[] { 300.0, 545, 1500 })
            {
                double floor = AliasFloor(Cowbell, hz, 0, new[] { hz, hz * 1.48 });
                worst = Math.Max(worst, floor);
                lines.Add($"Cowbell {hz} Hz: {floor:F1} dB");
                Assert.That(floor, Is.LessThan(-90), $"Cowbell at {hz} Hz");
            }
            Measure.Report($"Neo Drums, alias floor of the metallic voices (squares alone, noise off; worst of tune ±12 st and tone 4 / 16 kHz at 48 and 44.1 kHz): {worst:F1} dB; " + string.Join(", ", lines));
        }

        // ------------------------------------------------------------------ clicks

        [Test]
        public void RetriggersAndChokesAreClickFree()
        {
            var lines = new List<string>();
            // A retrigger at the hit's loudest moments: no step larger than the two hits' own added (the new one's start and the
            // old one's body there), so the old hit's fade adds none of its own.
            foreach (int voice in Enumerable.Range(0, DrumsEngine.VoiceCount))
            {
                double worstRatio = 0;
                foreach (double at in new[] { 0.004, 0.011, 0.03 })
                {
                    int t = (int)(at * Rate);
                    var lone = Hit(voice, 0.2);
                    double own = MaxStep(lone, 1, Rate / 100) + MaxStep(lone, t - 2, t + Rate / 100);
                    var (e, _) = Make();
                    var (l, r) = Run(e, Rate / 5, DrumsEngine.MaxBlock, (0, () => e.Hit(voice, 1f)), (t, () => e.Hit(voice, 1f)));
                    double step = MaxStep(l, t - 2, t + Rate / 100);
                    worstRatio = Math.Max(worstRatio, step / own);
                    Assert.That(step, Is.LessThanOrEqualTo(own), $"{DrumsEngine.Names[voice]} retriggered at {at * 1000} ms");
                }
                lines.Add($"{DrumsEngine.Short[voice]} {worstRatio:F3}×");
            }
            // Three hits within 2 ms: the oldest is cut, into the declicker.
            {
                var lone = Hit(Kick, 0.1);
                double own = MaxStep(lone, 1, Rate / 50);
                var (e, _) = Make();
                var (l, _) = Run(e, Rate / 10, DrumsEngine.MaxBlock, (0, () => e.Hit(Kick, 1f)), (40, () => e.Hit(Kick, 1f)), (80, () => e.Hit(Kick, 1f)), (96, () => e.Hit(Kick, 1f)));
                double step = MaxStep(l, 1, Rate / 50);
                lines.Add($"four kicks in 2 ms {step / own:F3}×");
                Assert.That(step, Is.LessThanOrEqualTo(2 * own), "hits faster than the fade (the oldest cut into the declicker)");
            }
            // The choke: the open hat alone (the closed one at −∞), cut by a closed hat at its loudest; silent within 5 ms.
            {
                int t = Rate / 10;
                var (e, _) = Make(v => Set(v, P.ChLevel, double.NegativeInfinity));
                var (l, _) = Run(e, Rate / 4, DrumsEngine.MaxBlock, (0, () => e.Hit(OpenHat, 1f)), (t, () => e.Hit(ClosedHat, 1f)));
                double own = MaxStep(l, t - Rate / 50, t);
                double step = MaxStep(l, t, t + Rate / 100);
                double after = Measure.Peak(l, t + (int)(DrumsEngine.ChokeSeconds * Rate) + 1, Rate / 20);
                lines.Add($"choke {step / own:F3}×, silent after {DrumsEngine.ChokeSeconds * 1000:0} ms");
                Assert.That(step, Is.LessThanOrEqualTo(1.05 * own), "the choke fades");
                Assert.That(after, Is.EqualTo(0), "and the open hat is silent after it");
            }
            Measure.Report("Neo Drums, the largest step at a retrigger against the two hits' own added: " + string.Join(", ", lines));
        }

        // ------------------------------------------------------------------ notes, hit columns, determinism

        [Test]
        public void NotesFollowTheGeneralMidiMapFromC2()
        {
            var expected = new Dictionary<int, int>
            {
                [35] = Kick, [36] = Kick, [37] = Rim, [38] = Snare, [39] = Clap, [40] = Snare, [41] = LowTom, [42] = ClosedHat, [43] = LowTom,
                [44] = ClosedHat, [45] = MidTom, [46] = OpenHat, [47] = MidTom, [48] = HighTom, [49] = Crash, [50] = HighTom, [51] = Ride,
                [56] = Cowbell, [57] = Crash, [59] = Ride,
            };
            for (int note = 0; note < 128; note++)
            {
                int v = DrumsEngine.VoiceOfKey(note);
                Assert.That(v, Is.EqualTo(expected.TryGetValue(note, out int e) ? e : -1), $"note {note}");
            }
            for (int voice = 0; voice < DrumsEngine.VoiceCount; voice++)
                Assert.That(DrumsEngine.VoiceOfKey(DrumsEngine.Keys[voice]), Is.EqualTo(voice), "each voice's own key plays it");
            // A note (MIDI or a track's) plays exactly what a hit of its voice does; a track's takes the tick's accent, MIDI doesn't.
            float[] Played(Action<DrumsEngine> play)
            {
                var (e, _) = Make(velocity: true);
                return Run(e, Rate / 4, DrumsEngine.MaxBlock, (0, () => play(e))).L;
            }
            Assert.That(Played(e => e.NoteOn(1000 + 38, 38, 0.7f)), Is.EqualTo(Played(e => e.Hit(Snare, 0.7f))));
            Assert.That(Played(e => { e.TickAccent = true; e.NoteOn(3, 42, 0.7f); }), Is.EqualTo(Played(e => e.Hit(ClosedHat, 0.7f, true))));
            Assert.That(Played(e => { e.TickAccent = true; e.NoteOn(1000 + 42, 42, 0.7f); }), Is.EqualTo(Played(e => e.Hit(ClosedHat, 0.7f, false))));
            Assert.That(Played(e => e.NoteOn(1000 + 60, 60, 1f)), Is.All.EqualTo(0f), "a key with no voice plays nothing");
        }

        [Test]
        public void HitColumnsPlayEachVoiceAtItsVelocityAndAccentTakesTheTick()
        {
            float[] Played(Action<DrumsEngine, PendingHits> play)
            {
                var (e, _) = Make(velocity: true);
                var pending = new PendingHits(16);
                return Run(e, Rate / 4, DrumsEngine.MaxBlock, (0, () =>
                {
                    play(e, pending);
                    bool accent = pending.TakeAccent();
                    pending.Drain(e, accent);
                })).L;
            }
            var both = Played((e, h) => { h.Set(0, Kick, 100); h.Set(5, ClosedHat, 64); });
            var direct = Played((e, h) => { e.Hit(Kick, 100 / 127f); e.Hit(ClosedHat, 64 / 127f); });
            Assert.That(both, Is.EqualTo(direct), "two tracks' hit columns on one tick");
            var accented = Played((e, h) => { h.Set(0, Kick, 100); h.Accent(); });
            Assert.That(accented, Is.EqualTo(Played((e, h) => e.Hit(Kick, 100 / 127f, true))), "the Accent column (any track) accents the tick");
            Assert.That(Played((e, h) => h.Set(0, Kick, 0)), Is.All.EqualTo(0f), "an empty cell plays nothing");
        }

        [Test]
        public void EveryHitOfAVoiceIsTheSameAndRendersAreDeterministic()
        {
            for (int voice = 0; voice < DrumsEngine.VoiceCount; voice++)
            {
                int gap = Rate * 3 / 2;
                var (e, _) = Make();
                var (l, _) = Run(e, 2 * gap, DrumsEngine.MaxBlock, (0, () => e.Hit(voice, 0.8f)), (gap, () => e.Hit(voice, 0.8f)));
                // The second hit starts after the first has ended (every decay at its default is under 1.5 s, the cymbals' aside).
                if (voice == Crash || voice == Ride) continue;
                for (int i = 0; i < gap; i++)
                    if (l[i] != l[gap + i]) Assert.Fail($"{DrumsEngine.Names[voice]}: the second hit differs at sample {i}");
            }
            var a = Hit(OpenHat, 1);
            var b = Hit(OpenHat, 1);
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void BlockSizesGiveTheSameOutput()
        {
            (float[] L, float[] R) Play(int block)
            {
                var (e, v) = Make(velocity: true);
                var events = new List<(int, Action)>();
                for (int k = 0; k < 40; k++)
                {
                    int at = 1000 + k * 2711, voice = (k * 5) % DrumsEngine.VoiceCount;
                    events.Add((at, () => e.Hit(voice, 0.4f + (k % 6) * 0.1f, k % 4 == 0)));
                }
                events.Add((30011, () => Set(v, P.KickLevel, -9.0)));
                events.Add((40000, () => Set(v, P.ChPan, 60.0)));
                events.Add((52001, () => Set(v, P.Volume, -3.0)));
                events.Add((61000, () => e.Hit(ClosedHat, 1f)));
                events.Add((61001, () => e.Hit(OpenHat, 1f)));
                events.Add((61007, () => e.Hit(ClosedHat, 1f)));
                events.Add((90000, () => e.AllNotesOff(true)));
                return Run(e, 2 * Rate + 37, block, events.ToArray());
            }
            var a = Play(256);
            var b = Play(117);
            var c = Play(16);
            Assert.That(b.L, Is.EqualTo(a.L));
            Assert.That(b.R, Is.EqualTo(a.R));
            Assert.That(c.L, Is.EqualTo(a.L));
            Assert.That(c.R, Is.EqualTo(a.R));
            Assert.That(Measure.Rms(a.L, 0, a.L.Length), Is.GreaterThan(0.01));
        }

        // ------------------------------------------------------------------ robustness

        [Test]
        public void ExtremesStayFiniteAndBoundedAndSilenceGoesIdle()
        {
            var rng = new Random(7);
            double loudest = 0;
            for (int round = 0; round < 30; round++)
            {
                var (e, v) = Make(v =>
                {
                    foreach (var d in NeoBuzz.Drums.Params.Table) v.Set(d.Index, rng.Next(2) == 0 ? d.Min : d.Max);
                }, velocity: true);
                var events = Enumerable.Range(0, 24).Select(k => (k * 997, (Action)(() => e.Hit(k % DrumsEngine.VoiceCount, 1f, k % 2 == 0)))).ToArray();
                var (l, r) = Run(e, Rate, DrumsEngine.MaxBlock, events);
                foreach (var s in l.Concat(r)) Assert.That(float.IsFinite(s), Is.True, $"round {round}");
                loudest = Math.Max(loudest, Math.Max(Measure.Peak(l, 0, l.Length), Measure.Peak(r, 0, r.Length)));
            }
            Measure.Report($"Neo Drums, every parameter at random ends, 24 accented hits a round: finite, peak {Measure.Db(loudest):F1} dBFS");
            Assert.That(loudest, Is.LessThan(Math.Pow(10, 40 / 20.0)), "bounded");
            // After the hits end: idle, and Process says so.
            var (idle, _) = Make();
            Run(idle, 12 * Rate, DrumsEngine.MaxBlock, Enumerable.Range(0, DrumsEngine.VoiceCount).Select(k => (k * 100, (Action)(() => idle.Hit(k, 1f)))).ToArray());
            Assert.That(idle.IsActive, Is.False, "idle once every hit has ended");
            var bl = new float[256];
            Assert.That(idle.Process(bl, bl, 256), Is.False, "and says so");
        }

        [Test]
        public void TheAudioPathAllocatesNothing()
        {
            var (e, v) = Make(velocity: true);
            var l = new float[DrumsEngine.MaxBlock];
            var r = new float[DrumsEngine.MaxBlock];
            var pending = new PendingHits(16);
            void Blocks(int count)
            {
                for (int b = 0; b < count; b++)
                {
                    if (b % 3 == 0) pending.Set(b % 16, b % DrumsEngine.VoiceCount, 30 + b % 97);
                    if (b % 7 == 0) pending.Accent();
                    if (b % 5 == 0) e.NoteOn(1000 + 36 + b % 24, 36 + b % 24, 0.5f);
                    if (b % 11 == 0) v.Set(P.KickTune, 100 + b % 500);
                    if (b % 13 == 0) v.Set(P.ChPan, b % 200);
                    if (b % 17 == 0) v.Set(P.OhDecay, 300 + b % 600);
                    if (b % 101 == 0) e.AllNotesOff(true);
                    bool accent = pending.TakeAccent();
                    e.TickAccent = accent;
                    pending.Drain(e, accent);
                    e.Process(l, r, DrumsEngine.MaxBlock);
                }
            }
            Blocks(300);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Blocks(2000);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Measure.Report($"Neo Drums: {bytes} bytes allocated over 2000 blocks (hits, chokes, notes, accents, tune, pan and decay moving)");
            Assert.That(bytes, Is.EqualTo(0));
        }

        [Test]
        public void EveryVoicePlayingAtOnceRunsFasterThanRealTime()
        {
            // Every voice hit every 100 ms, the decays at their longest: twelve hits sounding, and each 100 ms twelve fading out.
            var (e, _) = Make(v =>
            {
                foreach (var p in DrumsEngine.Voice) v.Set(p.Decay, NeoBuzz.Drums.Params.Table[p.Decay].Max);
                Set(v, P.KickDrive, 12.0);
            });
            var l = new float[DrumsEngine.MaxBlock];
            var r = new float[DrumsEngine.MaxBlock];
            int every = Rate / 10, since = every;
            void Seconds(double s)
            {
                for (int pos = 0; pos < s * Rate; pos += DrumsEngine.MaxBlock)
                {
                    if (since >= every)
                    {
                        since = 0;
                        for (int v = 0; v < DrumsEngine.VoiceCount; v++) e.Hit(v, 1f);
                    }
                    e.Process(l, r, DrumsEngine.MaxBlock);
                    since += DrumsEngine.MaxBlock;
                }
            }
            Seconds(1);
            Assert.That(e.ActiveHits, Is.GreaterThanOrEqualTo(DrumsEngine.VoiceCount));
            var sw = Stopwatch.StartNew();
            Seconds(10);
            double factor = 10 / sw.Elapsed.TotalSeconds;
            Measure.Report($"Neo Drums, all 12 voices hit every 100 ms with their longest decays ({e.ActiveHits} hits sounding), 48 kHz: {factor:F1}× real time");
            Assert.That(factor, Is.GreaterThan(10));
        }
    }
}

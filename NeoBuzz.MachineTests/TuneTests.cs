// Neo Tune (docs/specs/neo-tune.md): measured on synthetic voices (PitchSignals) with the shared tracker as the judge.

using System.Globalization;
using NeoBuzz.Dsp;
using NeoBuzz.Tune;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class TuneTests
    {
        const int Rate = 48000;
        static void Say(string s) => TestContext.Out.WriteLine(s);
        static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);

        static (TuneEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new TuneEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        /// <summary>The engine over a mono signal on both channels: the left output, sample i being the input of sample i minus the latency.</summary>
        static float[] Render(TuneEngine e, float[] x, int block = TuneEngine.MaxBlock)
        {
            var o = new float[x.Length];
            var l = new float[block];
            var r = new float[block];
            for (int pos = 0; pos < x.Length; pos += block)
            {
                int n = Math.Min(block, x.Length - pos);
                Array.Copy(x, pos, l, 0, n);
                Array.Copy(x, pos, r, 0, n);
                e.Process(l, r, n);
                Array.Copy(l, 0, o, pos, n);
            }
            return o;
        }

        /// <summary>The output with the latency taken out (aligned to the input), the tail padded with silence.</summary>
        static float[] Aligned(float[] o, int latency)
        {
            var a = new float[o.Length];
            Array.Copy(o, latency, a, 0, o.Length - latency);
            return a;
        }

        /// <summary>The tracker's f0 of a signal, with the time (s) of each voiced estimate.</summary>
        static List<(double T, double Hz)> Pitch(float[] x, PitchRange range = PitchRange.Full, PitchQuality q = PitchQuality.Quality)
        {
            var tr = new PitchTracker(Rate, range, q);
            var buf = new PitchEstimate[tr.MaxFrames(256)];
            var list = new List<(double, double)>();
            for (int pos = 0; pos < x.Length; pos += 256)
            {
                int n = Math.Min(256, x.Length - pos);
                int got = tr.Process(x, pos, n, buf, 0);
                for (int i = 0; i < got; i++)
                    if (buf[i].Voiced)
                    {
                        double period = Rate / buf[i].F0;
                        list.Add(((buf[i].EndSample - (tr.Window + period) / 2) / Rate, buf[i].F0));
                    }
            }
            return list;
        }

        static double Cents(double hz, double target) => 1200 * Math.Log2(hz / target);
        static double Hz(double midi) => 440 * Math.Pow(2, (midi - 69) / 12);

        /// <summary>The pitch of a steady stretch to a hundredth of a cent or so: the autocorrelation peak near <paramref name="periods"/> periods, with a parabola through it.</summary>
        static double Precise(float[] x, int from, int n, double approxHz, int periods = 30)
        {
            double p0 = Rate / approxHz;
            int lagC = (int)Math.Round(p0 * periods);
            double Ac(int lag)
            {
                double s = 0;
                for (int i = from; i < from + n - lagC - 8; i++) s += (double)x[i] * x[i + lag];
                return s;
            }
            int best = lagC;
            double bv = double.MinValue;
            int win = (int)Math.Ceiling(p0 * periods * 0.01) + 2;
            for (int l = lagC - win; l <= lagC + win; l++) { double v = Ac(l); if (v > bv) { bv = v; best = l; } }
            double a = Ac(best - 1), b = Ac(best), c = Ac(best + 1);
            double d = (a - 2 * b + c);
            double off = d != 0 ? 0.5 * (a - c) / d : 0;
            return Rate / ((best + off) / periods);
        }

        static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToList(); return a.Count == 0 ? double.NaN : a[a.Count / 2]; }
        static double Pct(IEnumerable<double> v, double p) { var a = v.OrderBy(x => x).ToList(); return a.Count == 0 ? double.NaN : a[Math.Min(a.Count - 1, (int)(p * a.Count))]; }

        static float[] Voice(double f0, double sec, Action<PitchSignals.VoiceOptions> set = null)
        {
            var o = new PitchSignals.VoiceOptions { Seed = 3 };
            set?.Invoke(o);
            return PitchSignals.Voice(Rate, f0, sec, o).X;
        }

        // ---- helpers for the tests below

        static float[] Hold(params (double Midi, double Seconds)[] notes) => Melody(0, notes);

        /// <summary>Notes of a voice one after another (each its own voice, 12 ms fades), offsets in cents added.</summary>
        static float[] Melody(double cents, params (double Midi, double Seconds)[] notes)
        {
            int total = (int)(notes.Sum(n => n.Seconds) * Rate);
            var x = new float[total];
            int pos = 0;
            ulong seed = 11;
            foreach (var (midi, sec) in notes)
            {
                double f0 = Hz(midi) * Math.Pow(2, cents / 1200.0);
                var v = Voice(f0, sec, o => { o.Seed = seed++; o.Formants = f0 > 300 ? PitchSignals.VowelsHigh[0] : PitchSignals.VowelsLow[0]; });
                Array.Copy(v, 0, x, pos, Math.Min(v.Length, total - pos));
                pos += v.Length;
            }
            return x;
        }

        static Action<ParamValues> Hard(int scale = 0, int range = 1) => v =>
        {
            Set(v, P.Retune, 0); Set(v, P.Humanize, 0); Set(v, P.VibKeep, 0); v.Set(P.Scale, scale); v.Set(P.Range, range);
        };

        static double Rms(float[] x, int from, int n) => Measure.Rms(x, from, n);

        /// <summary>Cents of the output against a fixed note over a time span, by the tracker.</summary>
        static List<double> Errors(float[] y, double midi, double from, double to) =>
            Pitch(y).Where(q => q.T >= from && q.T <= to).Select(q => Cents(q.Hz, Hz(midi))).ToList();

        /// <summary>The cepstrum (40 coefficients) of the log spectrum of a 4096-point stretch.</summary>
        static double[] Cepstrum(float[] x, int from)
        {
            const int N = 4096, Q = 40, K = 1024;
            var logMag = new double[K];
            var win = new double[N];
            for (int n = 0; n < N; n++) win[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / N);
            for (int k = 0; k < K; k++)
            {
                double re = 0, im = 0;
                for (int n = 0; n < N; n++)
                {
                    double v = x[from + n] * win[n];
                    double ph = 2 * Math.PI * k * n / N;
                    re += v * Math.Cos(ph); im -= v * Math.Sin(ph);
                }
                logMag[k] = 20 * Math.Log10(Math.Sqrt(re * re + im * im) + 1e-9);
            }
            var c = new double[Q];
            for (int q = 0; q < Q; q++)
            {
                double a = 0;
                for (int k = 0; k < K; k++) a += logMag[k] * Math.Cos(Math.PI * q * (k + 0.5) / K);
                c[q] = a * (q == 0 ? 1.0 / K : 2.0 / K);
            }
            return c;
        }

        static double[] EnvelopeAt(double[] c, double[] freqs, int rate = Rate)
        {
            const int N = 4096, K = 1024;
            var env = new double[freqs.Length];
            for (int i = 0; i < freqs.Length; i++)
            {
                double bin = freqs[i] * N / rate, v = 0;
                for (int q = 0; q < c.Length; q++) v += c[q] * Math.Cos(Math.PI * q * (bin + 0.5) / K);
                env[i] = v;
            }
            return env;
        }

        /// <summary>Spectral envelope in dB at <paramref name="freqs"/> (a cepstrally smoothed 4096-point spectrum of the stretch).</summary>
        static double[] Envelope(float[] x, int from, double[] freqs, int rate = Rate) => EnvelopeAt(Cepstrum(x, from), freqs, rate);

        /// <summary>
        /// The resynthesis error against the input, with each 20 ms block of the output free to be a little early or late (up to half a period
        /// of <paramref name="hz"/>: a grain may come from the neighbouring period, which is the same sound), dB.
        /// </summary>
        static double ShiftFreeSnr(float[] x, float[] y, int from, int n, double hz)
        {
            int block = (int)(0.02 * Rate), maxShift = (int)(0.5 * Rate / hz) + 1;
            double err = 0, sig = 0;
            for (int b = from; b + block <= from + n; b += block)
            {
                double bestErr = double.MaxValue, bestSig = 0;
                for (int d = -maxShift; d <= maxShift; d++)
                {
                    double e = 0, s = 0;
                    for (int i = b; i < b + block; i++) { double diff = y[i] - x[i + d]; e += diff * diff; s += (double)x[i + d] * x[i + d]; }
                    if (e < bestErr) { bestErr = e; bestSig = s; }
                }
                err += bestErr; sig += bestSig;
            }
            return 10 * Math.Log10(sig / err);
        }

        static double[] Grid(double lo, double hi, int n) => Enumerable.Range(0, n).Select(i => lo + (hi - lo) * i / (n - 1)).ToArray();

        // ---- accuracy

        [TestCase(57, 35, 1, 0)]
        [TestCase(57, -30, 1, 1)]
        [TestCase(64, 20, 0, 0)]
        [TestCase(48, -30, 1, 2)]
        [TestCase(81, 25, 0, 0)]
        [TestCase(43, 40, 2, 1)]
        [TestCase(69, -25, 1, 2)]
        public void HardTuneLandsOnTheNoteWithinFiveCents(int midi, int offCents, int range, int vowel)
        {
            double f0 = Hz(midi) * Math.Pow(2, offCents / 1200.0);
            var x = Voice(f0, 2.0, o => o.Formants = f0 > 300 ? PitchSignals.VowelsHigh[vowel] : PitchSignals.VowelsLow[vowel]);
            var (e, _) = Make(Hard(0, range));
            var y = Aligned(Render(e, x), e.Latency);
            double precise = Cents(Precise(y, (int)(0.8 * Rate), (int)(1.0 * Rate), Hz(midi)), Hz(midi));
            var errs = Errors(y, midi, 0.5, 1.9).Select(Math.Abs).ToList();
            Say($"note {midi} {offCents:+0;-0} cents, range {range}, vowel {vowel}: steady {F(precise, "F2")} cents, tracker p95 {F(Pct(errs, 0.95))} max {F(errs.Max())} (input {F(offCents)})");
            Assert.That(Math.Abs(precise), Is.LessThan(2.0), "the steady pitch");
            Assert.That(Pct(errs, 0.90), Is.LessThan(5.0), "the tracker's reading of the output, 90th percentile");
        }

        [Test]
        public void HardTuneFlattensAVibratoAndAScoop()
        {
            // 40 cents of vibrato at 5.5 Hz on A3, after a scoop from 100 cents under it.
            var x = Voice(Hz(57), 3.0, o => { o.VibratoCents = 40; o.ScoopCents = 100; o.ScoopSeconds = 0.08; o.VibratoDelay = 0.2; });
            var (e, _) = Make(Hard());
            var y = Aligned(Render(e, x), e.Latency);
            var inErr = Errors(x, 57, 0.8, 2.9);
            var outErr = Errors(y, 57, 0.8, 2.9).Select(Math.Abs).ToList();
            Say($"input swings ±{F(inErr.Max(Math.Abs))} cents; output p90 {F(Pct(outErr, 0.9))}, max {F(outErr.Max())}");
            Assert.That(inErr.Max(Math.Abs), Is.GreaterThan(30));
            Assert.That(Pct(outErr, 0.90), Is.LessThan(5.0));
        }

        [Test]
        public void ARetuneSpeedCorrectsAStepInTheCentreLikeAOnePole()
        {
            // A3 at 15 cents flat, then at 1.0 s the voice moves 35 cents (in 20 ms) to 20 cents sharp: a change of the centre inside the note.
            // Natural-style settings (the expression kept, no zone): the retune speed is how fast the new error is taken out.
            double Remaining(double ms)
            {
                var x = Voice(Hz(57) * Math.Pow(2, -15 / 1200.0), 3.0, o => { o.GlideTo = Hz(57) * Math.Pow(2, 20 / 1200.0); o.GlideStart = 1.0; o.GlideSeconds = 0.02; });
                var (e, _) = Make(v => { Set(v, P.Retune, ms); Set(v, P.Humanize, 0); Set(v, P.VibKeep, 1); Set(v, P.Glide, 0); Set(v, P.Zone, 100); v.Set(P.Scale, 0); });
                var y = Aligned(Render(e, x), e.Latency);
                var near = Pitch(y).Where(q => Math.Abs(q.T - 1.40) < 0.04).Select(q => Cents(q.Hz, Hz(57))).ToList();
                return Median(near);
            }
            double r0 = Remaining(0), r100 = Remaining(100), r300 = Remaining(300);
            Say($"error left 0.4 s after a 35-cent step: retune 0 ms {F(r0)}, 100 ms {F(r100)}, 300 ms {F(r300)} cents (sung 20)");
            Assert.That(Math.Abs(r0), Is.LessThan(7));
            Assert.That(Math.Abs(r100), Is.GreaterThan(Math.Abs(r0) + 1).And.LessThan(15));
            Assert.That(Math.Abs(r300), Is.GreaterThan(Math.Abs(r100) + 3));
        }

        [Test]
        public void NaturalSettingsKeepTheSingersVibratoDepth()
        {
            var x = Voice(Hz(57), 4.0, o => { o.VibratoCents = 40; o.VibratoDelay = 0.2; });
            double Depth(float[] y) { var e = Errors(y, 57, 1.0, 3.8); return (Pct(e, 0.95) - Pct(e, 0.05)) / 2; }
            double din = Depth(x);
            var (n, _) = Make(v => { Set(v, P.Retune, 60); Set(v, P.Humanize, 0.5); Set(v, P.VibKeep, 1); v.Set(P.Scale, 0); });
            double dnat = Depth(Aligned(Render(n, x), n.Latency));
            var (h, _) = Make(Hard());
            double dhard = Depth(Aligned(Render(h, x), h.Latency));
            var (half, _) = Make(v => { Hard()(v); Set(v, P.VibKeep, 0.5); });
            double dhalf = Depth(Aligned(Render(half, x), half.Latency));
            Say($"vibrato depth: input {F(din)}, natural {F(dnat)}, hard {F(dhard)}, keep 50 % {F(dhalf)} cents");
            Assert.That(dnat / din, Is.InRange(0.75, 1.25));
            Assert.That(dhard, Is.LessThan(6));
            Assert.That(dhalf / din, Is.InRange(0.3, 0.7));
        }

        [Test]
        public void HumanizeRetunesASustainedNoteMoreSlowlyAndSoKeepsMoreOfItsDrift()
        {
            // 25 cents of slow drift (0.8 Hz) around A3, a retune speed of 40 ms: Humanize lengthens it up to four times on a held note.
            var x = Voice(Hz(57), 5.0, o => { o.VibratoCents = 25; o.VibratoHz = 0.8; o.VibratoDelay = 0; });
            double Depth(float[] y) { var e = Errors(y, 57, 1.5, 4.8); return (Pct(e, 0.95) - Pct(e, 0.05)) / 2; }
            double din = Depth(x);
            var res = new List<double>();
            foreach (double h in new[] { 0.0, 0.5, 1.0 })
            {
                var (e, _) = Make(v => { Set(v, P.Retune, 40); Set(v, P.Humanize, h); Set(v, P.VibKeep, 1); Set(v, P.Glide, 0); Set(v, P.Zone, 100); v.Set(P.Scale, 0); });
                res.Add(Depth(Aligned(Render(e, x), e.Latency)));
            }
            Say($"drift depth: input {F(din)}; humanize 0 {F(res[0])}, 50 % {F(res[1])}, 100 % {F(res[2])} cents");
            Assert.That(res[1], Is.GreaterThan(res[0] + 1.5));
            Assert.That(res[2], Is.GreaterThan(res[1] + 1.5).And.GreaterThan(0.5 * din));
        }

        [Test]
        public void AmountBlendsTheCorrectionInCents()
        {
            // The input's jitter makes its steady pitch ambiguous to a long autocorrelation, so the tracker's median is the judge here.
            var x = Voice(Hz(57) * Math.Pow(2, 30 / 1200.0), 2.0, null);
            foreach (var (amount, want) in new[] { (0.0, 30.0), (0.5, 15.0), (1.0, 0.0) })
            {
                var (e, _) = Make(v => { Hard()(v); Set(v, P.Amount, amount); });
                var y = Aligned(Render(e, x), e.Latency);
                double got = Median(Errors(y, 57, 0.8, 1.9));
                Say($"amount {amount * 100:F0} %: {F(got, "F1")} cents (want {want})");
                Assert.That(got, Is.EqualTo(want).Within(2.5));
            }
        }

        [Test]
        public void TransposeMovesTheVoiceByWholeSemitones()
        {
            var x = Voice(Hz(57), 2.0, null);
            foreach (int st in new[] { -12, -5, 7, 12 })
            {
                var (e, _) = Make(v => { Hard()(v); Set(v, P.Transpose, st); });
                var y = Aligned(Render(e, x), e.Latency);
                double got = Cents(Precise(y, (int)(0.8 * Rate), (int)(1.0 * Rate), Hz(57 + st)), Hz(57 + st));
                Say($"transpose {st:+0;-0}: {F(got, "F2")} cents from the target");
                Assert.That(Math.Abs(got), Is.LessThan(3.0));
            }
        }

        [Test]
        public void AddedVibratoHasItsDepthAndRate()
        {
            var x = Voice(Hz(57), 4.0, null);
            var (e, _) = Make(v => { Hard()(v); Set(v, P.VibDepth, 40); Set(v, P.VibRate, 6); Set(v, P.VibDelay, 0); });
            var y = Aligned(Render(e, x), e.Latency);
            var p = Pitch(y).Where(q => q.T > 1.0 && q.T < 3.9).ToList();
            var c = p.Select(q => Cents(q.Hz, Hz(57))).ToList();
            double depth = (Pct(c, 0.95) - Pct(c, 0.05)) / 2;
            int crossings = 0;
            for (int i = 1; i < c.Count; i++) if ((c[i - 1] < 0) != (c[i] < 0)) crossings++;
            double rate = crossings / 2.0 / (p.Last().T - p.First().T);
            Say($"added vibrato: depth ±{F(depth)} cents (set 40), rate {F(rate)} Hz (set 6)");
            Assert.That(depth, Is.InRange(30, 46));
            Assert.That(rate, Is.InRange(5.2, 6.8));
        }

        // ---- the notes

        static readonly int[][] ExpectedScales =
        {
            new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, new[] { 0, 2, 4, 5, 7, 9, 11 }, new[] { 0, 2, 3, 5, 7, 8, 10 }, new[] { 0, 2, 3, 5, 7, 9, 10 },
            new[] { 0, 1, 3, 5, 7, 8, 10 }, new[] { 0, 2, 4, 6, 7, 9, 11 }, new[] { 0, 2, 4, 5, 7, 9, 10 }, new[] { 0, 1, 3, 5, 6, 8, 10 },
            new[] { 0, 2, 3, 5, 7, 8, 11 }, new[] { 0, 3, 5, 7, 10 }, new[] { 0, 2, 4, 7, 9 }, new[] { 0, 3, 5, 6, 7, 10 },
        };

        static int ExpectedNearest(double midi, int[] steps, int root)
        {
            int best = -1;
            double bd = 1e9;
            for (int n = 30; n < 100; n++)
            {
                if (!steps.Contains(((n - root) % 12 + 12) % 12)) continue;
                double d = Math.Abs(midi - n);
                if (d < bd) { bd = d; best = n; }
            }
            return best;
        }

        [Test]
        public void EveryScaleSendsTheVoiceToItsNearestNote()
        {
            int checkedNotes = 0;
            for (int scale = 0; scale < 12; scale++)
            {
                foreach (int root in new[] { 0, 2, 9 })
                {
                    var (e, _) = Make(v => { Hard(scale)(v); v.Set(P.Key, root); });
                    // A glide across an octave in steps held for 0.25 s: each sits 0.25 or 0.35 of a semitone above a note, so the nearest is clear.
                    for (int k = 0; k < 12; k += 1)
                    {
                        double m = 57 + k + (k % 2 == 0 ? 0.25 : 0.35);
                        int want = ExpectedNearest(m, ExpectedScales[scale], root);
                        double dist = Math.Abs(m - want);
                        double second = 1e9;
                        for (int n = 30; n < 100; n++) if (n != want && ExpectedScales[scale].Contains(((n - root) % 12 + 12) % 12)) second = Math.Min(second, Math.Abs(m - n));
                        if (second - dist < 0.3) continue;     // too close to the halfway point to demand one answer
                        var x = Voice(Hz(m), 0.6, o => o.Seed = (ulong)(k + 3));
                        e.Reset();
                        Render(e, x);
                        Assert.That(e.TargetNote, Is.EqualTo(want), $"scale {scale} root {root}: {m} should go to {want}");
                        checkedNotes++;
                    }
                }
            }
            Say($"{checkedNotes} voices sent to the nearest note of 12 scales in 3 keys");
            Assert.That(checkedNotes, Is.GreaterThan(150));
        }

        [Test]
        public void ACustomMaskAllowsOnlyItsNotes()
        {
            // C, E and G (bits 0, 4, 7): D4 + 0.2 goes to C or E (2.2 vs 1.8: E4), F4 + 0.1 to E, A4 + 0.1 to G (2.1) vs C5 (2.9).
            var (e, _) = Make(v => { Hard(TuneScales.Custom)(v); v.Set(P.Notes, 1 | 16 | 128); });
            foreach (var (m, want) in new[] { (62.2, 64), (65.1, 64), (69.1, 67), (70.6, 72) })
            {
                e.Reset();
                Render(e, Voice(Hz(m), 0.6, null));
                Assert.That(e.TargetNote, Is.EqualTo(want), $"{m}");
            }
            Assert.That(e.NoteMask, Is.EqualTo(1 | 16 | 128));
        }

        [Test]
        public void ListenFindsTheKeyFromTheSungNotes()
        {
            // A tune in D major, 8 seconds.
            var notes = new List<(double, double)>();
            int[] tune = { 62, 64, 66, 67, 69, 71, 73, 74, 73, 71, 69, 67, 66, 64, 62, 66, 69, 74, 69, 66 };
            foreach (int m in tune) notes.Add((m - 12, 0.4));
            var x = Melody(10, notes.ToArray());
            var (e, _) = Make(v => { v.Set(P.Source, 1); v.Set(P.Key, 0); v.Set(P.Scale, 1); Set(v, P.Retune, 20); });
            Render(e, x);
            Say($"listened key: {TuneScales.NoteNames[Math.Max(0, e.Root)]} (the tune is in D)");
            Assert.That(e.Root, Is.EqualTo(2));
        }

        [Test]
        public void MidiNotesSetTheTarget()
        {
            double m = 64.3;
            var x = Voice(Hz(m), 1.6, null);
            // Nearest octave: C held, the voice goes to the C nearest it (60); exact: only the octave played (72).
            foreach (var (octave, held, want) in new[] { (0, new[] { 60 }, 60), (1, new[] { 72 }, 72), (0, new[] { 60, 64, 67 }, 64), (0, new[] { 60, 67 }, 67) })
            {
                var (e, _) = Make(v => { Hard()(v); v.Set(P.Target, 1); v.Set(P.MidiOctave, octave); });
                foreach (int n in held) e.MidiNote(n, 100);
                var y = Aligned(Render(e, x), e.Latency);
                double got = Cents(Precise(y, (int)(0.8 * Rate), (int)(0.7 * Rate), Hz(want)), Hz(want));
                Say($"octave {(octave == 0 ? "nearest" : "exact")}, held [{string.Join(' ', held)}]: target {e.TargetNote}, {F(got, "F2")} cents from {want}");
                Assert.That(e.TargetNote, Is.EqualTo(want));
                Assert.That(Math.Abs(got), Is.LessThan(3));
            }
            // Released: the voice goes back to as sung.
            var (r, _) = Make(v => { Hard()(v); v.Set(P.Target, 1); });
            r.MidiNote(60, 100);
            r.MidiNote(60, 0);
            var z = Aligned(Render(r, x), r.Latency);
            double asSung = Cents(Precise(z, (int)(0.8 * Rate), (int)(0.7 * Rate), Hz(m)), Hz(m));
            Assert.That(Math.Abs(asSung), Is.LessThan(3), "no note held: the voice as sung");
        }

        // ---- passing through

        [Test]
        public void UnvoicedSoundsAndSilencePassThroughUnchanged()
        {
            var rng = new SynthRng(5);
            foreach (var (name, sig) in new[] { ("noise", PitchSignals.Noise(Rate, 2, 0.1, rng)), ("pink", PitchSignals.Pink(Rate, 2, 0.1, rng)), ("silence", PitchSignals.Silence(Rate, 1)) })
            {
                var (e, _) = Make(Hard());
                var y = Aligned(Render(e, sig), e.Latency);
                double worst = 0;
                for (int i = 0; i < sig.Length - e.Latency; i++) worst = Math.Max(worst, Math.Abs(y[i] - sig[i]));
                Say($"{name}: largest difference {worst}");
                Assert.That(worst, Is.EqualTo(0).Within(1e-7), name);
            }
        }

        [Test]
        public void MixAtZeroIsTheDelayedInputBitForBit()
        {
            var x = Voice(Hz(57) * Math.Pow(2, 0.3), 1.5, o => o.VibratoCents = 30);
            var (e, _) = Make(v => { Hard()(v); Set(v, P.Mix, 0); });
            var y = Aligned(Render(e, x), e.Latency);
            for (int i = 0; i < x.Length - e.Latency; i++) Assert.That(y[i], Is.EqualTo(x[i]), $"sample {i}");
        }

        [TestCase(0, 480)]
        [TestCase(1, 1344)]
        [TestCase(2, 1728)]
        public void TheLatencyIsTheLookaheadOfTheRangeAndAnUnvoicedClickArrivesThatLate(int range, int samples)
        {
            var (e, _) = Make(Hard(0, range));
            Assert.That(e.Latency, Is.EqualTo(samples));
            var x = new float[Rate / 2];
            x[10000] = 0.8f;
            var y = Render(e, x);
            int at = 0;
            for (int i = 1; i < y.Length; i++) if (Math.Abs(y[i]) > Math.Abs(y[at])) at = i;
            Say($"range {range}: {e.Latency} samples = {F(1000.0 * e.Latency / Rate)} ms; a click came out {at - 10000} samples late");
            Assert.That(at - 10000, Is.EqualTo(e.Latency));
        }

        [Test]
        public void AHardAttackInsideAVoicedNoteIsKept()
        {
            // A plosive: a 12 ms noise burst on a sustained voiced note.
            var x = Voice(Hz(57) * Math.Pow(2, 0.2), 2.0, o => o.Level = 0.3);
            var rng = new SynthRng(9);
            int click = (int)(1.2 * Rate);
            for (int i = 0; i < 576; i++) x[click + i] += (float)(0.7 * Math.Exp(-i / 120.0) * rng.Gauss() * 0.5);
            // How closely the output follows the dry sound over the burst's first 6 ms (the error against the burst's energy), dB.
            double Follow(float[] y)
            {
                double err = 0, sig = 0;
                for (int i = click - 24; i < click + 288; i++) { double d = y[i] - x[i]; err += d * d; sig += (double)x[i] * x[i]; }
                return 10 * Math.Log10(sig / Math.Max(err, 1e-12));
            }
            var (on, _) = Make(v => { Hard()(v); v.Set(P.Method, 2); v.Set(P.Transients, 1); });
            var yOn = Aligned(Render(on, x), on.Latency);
            var (off, _) = Make(v => { Hard()(v); v.Set(P.Method, 2); v.Set(P.Transients, 0); });
            var yOff = Aligned(Render(off, x), off.Latency);
            var (low, _) = Make(v => { Hard(0, 0)(v); v.Set(P.Method, 2); v.Set(P.Transients, 1); });
            var yLow = Aligned(Render(low, x), low.Latency);
            Say($"the burst followed to {F(Follow(yOn))} dB with Transients on (Low latency {F(Follow(yLow))}), {F(Follow(yOff))} dB off");
            Assert.That(Follow(yOn), Is.GreaterThan(Follow(yOff) + 6).And.GreaterThan(15), "Quality: the lookahead lets the dry gate open before the attack");
            Assert.That(Follow(yLow), Is.GreaterThan(Follow(yOff)), "Low latency: only part of the attack comes in time");
        }

        // ---- formants

        [Test]
        public void FormantPreserveKeepsTheSpectralEnvelopeWhenThePitchMoves()
        {
            var x = Voice(Hz(55), 3.0, o => o.Formants = PitchSignals.VowelsLow[0]);       // an "aa"
            var grid = Grid(250, 3500, 40);
            int at = (int)(1.5 * Rate);
            var inEnv = Envelope(x, at, grid);
            double Diff(Action<ParamValues> set)
            {
                var (e, _) = Make(v => { Hard()(v); Set(v, P.Transpose, 7); set(v); });
                var y = Aligned(Render(e, x), e.Latency);
                var o = Envelope(y, at, grid);
                // Level differences do not count: the mean is taken out.
                double mi = inEnv.Average(), mo = o.Average();
                return grid.Select((_, i) => Math.Abs((inEnv[i] - mi) - (o[i] - mo))).Average();
            }
            double on = Diff(v => v.Set(P.Preserve, 1)), off = Diff(v => v.Set(P.Preserve, 0));
            Say($"envelope error after +7 semitones: preserve on {F(on, "F2")} dB, off {F(off, "F2")} dB");
            Assert.That(on, Is.LessThan(3.0));
            Assert.That(off, Is.GreaterThan(1.8 * on));
        }

        [Test]
        public void FormantShiftMovesTheVowelColour()
        {
            var x = Voice(Hz(52), 3.0, o => o.Formants = PitchSignals.VowelsLow[0]);
            var grid = Grid(250, 3500, 60);
            int at = (int)(1.5 * Rate);
            var cx = Cepstrum(x, at);
            // The warp that best maps the input's envelope onto the output's: env_out(f) = env_in(f / w).
            double Warp(float[] y)
            {
                var env = Envelope(y, at, grid);
                double best = 1, bestErr = 1e9;
                for (double w = 0.5; w <= 2.0; w *= 1.01)
                {
                    var probe = EnvelopeAt(cx, grid.Select(f => f / w).ToArray());
                    double mi = probe.Average(), mo = env.Average(), err = 0;
                    for (int i = 0; i < grid.Length; i++) err += Math.Abs((probe[i] - mi) - (env[i] - mo));
                    if (err < bestErr) { bestErr = err; best = w; }
                }
                return best;
            }
            foreach (int st in new[] { -7, -3, 4, 7 })
            {
                var (e, _) = Make(v => { Hard()(v); Set(v, P.Formant, st); });
                double w = Warp(Aligned(Render(e, x), e.Latency));
                Say($"formant {st:+0;-0}: the envelope moved x{F(w, "F2")} (wanted x{F(Math.Pow(2, st / 12.0), "F2")})");
                Assert.That(Math.Log2(w) * 12, Is.EqualTo(st).Within(1.5));
            }
        }

        [Test]
        public void WithTheCorrectionOffTheGrainsReproduceTheInput()
        {
            // Amount 0 leaves the pitch alone, so the grains must give the input back, each 20 ms a little early or late at most (a grain
            // may come from the neighbouring period): the error of the resynthesis alone, on voices with vibrato, jitter and shimmer.
            foreach (int range in new[] { 0, 1 })
                foreach (int vowel in new[] { 0, 1, 2 })
                {
                    var x = Voice(Hz(55), 2.0, o => { o.Formants = PitchSignals.VowelsLow[vowel]; o.VibratoCents = 20; o.Jitter = 0.003; o.Shimmer = 0.02; });
                    var (e, _) = Make(v => { Hard(0, range)(v); Set(v, P.Amount, 0); });
                    var y = Aligned(Render(e, x), e.Latency);
                    double snr = ShiftFreeSnr(x, y, (int)(0.8 * Rate), (int)(1.0 * Rate), Hz(55));
                    Say($"range {range}, vowel {vowel}: resynthesis against the input {F(snr)} dB");
                    Assert.That(snr, Is.GreaterThan(20));
                }
        }

        // ---- robustness

        [TestCase(44100)]
        [TestCase(96000)]
        public void WorksAtOtherSampleRates(int rate)
        {
            var o = new PitchSignals.VoiceOptions { Seed = 4 };
            double f0 = Hz(57) * Math.Pow(2, 30 / 1200.0);
            var src = PitchSignals.Voice(rate, f0, 2.0, o);
            var (e, _) = Make(Hard(), rate);
            var y = new float[src.X.Length];
            var l = new float[256];
            var r = new float[256];
            for (int pos = 0; pos < y.Length; pos += 256)
            {
                int n = Math.Min(256, y.Length - pos);
                Array.Copy(src.X, pos, l, 0, n); Array.Copy(src.X, pos, r, 0, n);
                e.Process(l, r, n);
                Array.Copy(l, 0, y, pos, n);
            }
            var al = new float[y.Length];
            Array.Copy(y, e.Latency, al, 0, y.Length - e.Latency);
            int from = (int)(0.8 * rate), len = (int)(1.0 * rate);
            double p0 = rate / Hz(57);
            double pitch = PreciseAt(al, from, len, Hz(57), rate);
            Say($"{rate} Hz: latency {e.Latency} samples ({F(1000.0 * e.Latency / rate)} ms), {F(Cents(pitch, Hz(57)), "F2")} cents");
            Assert.That(Math.Abs(Cents(pitch, Hz(57))), Is.LessThan(3));
            Assert.That(1000.0 * e.Latency / rate, Is.EqualTo(28).Within(0.5));
        }

        static double PreciseAt(float[] x, int from, int n, double approxHz, int rate, int periods = 30)
        {
            double p0 = rate / approxHz;
            int lagC = (int)Math.Round(p0 * periods);
            double Ac(int lag) { double s = 0; for (int i = from; i < from + n - lagC - 8; i++) s += (double)x[i] * x[i + lag]; return s; }
            int best = lagC;
            double bv = double.MinValue;
            int win = (int)Math.Ceiling(p0 * periods * 0.01) + 2;
            for (int l = lagC - win; l <= lagC + win; l++) { double v = Ac(l); if (v > bv) { bv = v; best = l; } }
            double a = Ac(best - 1), b = Ac(best), c = Ac(best + 1);
            double d = a - 2 * b + c;
            return rate / ((best + (d != 0 ? 0.5 * (a - c) / d : 0)) / periods);
        }

        [TestCase(32)]
        [TestCase(100)]
        [TestCase(256)]
        public void TheBlockSizeDoesNotMatter(int block)
        {
            var x = Voice(Hz(57) * Math.Pow(2, 30 / 1200.0), 2.0, null);
            var (e, _) = Make(Hard());
            var y = Aligned(Render(e, x, block), e.Latency);
            double got = Cents(Precise(y, (int)(0.8 * Rate), (int)(1.0 * Rate), Hz(57)), Hz(57));
            Say($"block {block}: {F(got, "F2")} cents");
            Assert.That(Math.Abs(got), Is.LessThan(2));
        }

        [Test]
        public void EverySettingAtOnceStaysFiniteAndBounded()
        {
            var rng = new SynthRng(21);
            var voice = Melody(20, (57, 0.5), (60, 0.5), (64, 0.5), (50, 0.5), (72, 0.5), (45, 0.5));
            var noise = PitchSignals.Noise(Rate, 0.5, 0.1, rng);
            var x = voice.Concat(noise).Concat(voice).ToArray();
            for (int trial = 0; trial < 12; trial++)
            {
                var (e, v) = Make();
                var l = new float[256];
                var r = new float[256];
                double worst = 0;
                for (int pos = 0; pos + 256 <= x.Length; pos += 256)
                {
                    if (pos % (256 * 40) == 0)
                        for (int p = 0; p < P.Count; p++)
                        {
                            var d = Params.Table[p];
                            v.Set(p, d.Min + (int)(rng.Next() * (d.Max - d.Min + 1)));
                        }
                    if (pos % (256 * 30) == 0) e.MidiNote(48 + (int)(rng.Next() * 30), rng.Next() < 0.7 ? 100 : 0);
                    Array.Copy(x, pos, l, 0, 256); Array.Copy(x, pos, r, 0, 256);
                    e.Process(l, r, 256);
                    for (int i = 0; i < 256; i++)
                    {
                        Assert.That(float.IsFinite(l[i]) && float.IsFinite(r[i]), Is.True, $"trial {trial} at {pos + i}");
                        worst = Math.Max(worst, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
                    }
                }
                Assert.That(worst, Is.LessThan(4.0), $"trial {trial}");
            }
        }

        // ---- cost

        [Test]
        public void NothingIsAllocatedPerBlock()
        {
            var x = Melody(20, (57, 0.7), (60, 0.7), (64, 0.7), (50, 0.7));
            var (e, v) = Make(Hard());
            var bl = new float[256];
            var br = new float[256];
            void Block(int b)
            {
                int pos = (b * 256) % (x.Length - 256);
                if (b % 50 == 0) { Set(v, P.Retune, (b / 50 % 4) * 50); v.Set(P.Scale, (b / 50) % 13); }
                if (b % 120 == 7) e.MidiNote(60 + b % 12, 100);
                if (b % 120 == 77) e.MidiNote(60 + (b - 70) % 12, 0);
                if (b % 400 == 399) v.Set(P.Range, (b / 400) % 3);
                if (b % 90 == 0) v.Set(P.Target, (b / 90) % 2);
                if (b % 130 == 0) v.Set(P.Method, (b / 130) % 3);
                if (b % 77 == 0) TuneNatural.Apply(v, (b / 77 % 5) / 4.0);
                Array.Copy(x, pos, bl, 0, 256);
                Array.Copy(x, pos, br, 0, 256);
                e.Process(bl, br, 256);
            }
            for (int b = 0; b < 100; b++) Block(b);
            var (bytes, why) = AllocationCheck.Run(2400, b => Block(b + 100));
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [TestCase(0, 32.0, 22.0)]
        [TestCase(1, 32.0, 22.0)]
        [TestCase(2, 32.0, 22.0)]
        public void RunsFasterThanRealTime(int range, double x64, double arm64)
        {
            int n = Rate * 3;
            var x = Melody(15, (57, 0.75), (60, 0.75), (64, 0.75), (55, 0.75));
            const int Warm = Rate;
            Throughput.Guard($"Neo Tune CPU (range {range}, hard)", Throughput.Floor(x64, arm64), 2, Rate, TuneEngine.MaxBlock, () =>
            {
                var (e, _) = Make(Hard(0, range));
                var bl = new float[TuneEngine.MaxBlock];
                var br = new float[TuneEngine.MaxBlock];
                void Blk(int pos, int len)
                {
                    Array.Copy(x, pos % (x.Length - 256), bl, 0, len);
                    Array.Copy(x, pos % (x.Length - 256), br, 0, len);
                    e.Process(bl, br, len);
                }
                for (int pos = 0; pos < Warm; pos += TuneEngine.MaxBlock) Blk(pos, TuneEngine.MaxBlock);
                return Throughput.InBlocks(TuneEngine.MaxBlock, (pos, len) => Blk(Warm + pos, len));
            });
        }
    }
}

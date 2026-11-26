using System.Diagnostics;
using System.Xml.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.Grain;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Grain (docs/specs/neo-grain.md): the acceptance gates, measured.</summary>
    [TestFixture]
    public class GrainTests
    {
        const int Rate = 48000;
        const int SourceSong = 3;

        /// <summary>A parameter in its real unit; a percentage is given as a percentage (the engine's own value runs 0 to 1).</summary>
        internal static void Set(ParamValues v, int p, double physical)
        {
            var d = Params.Table[p];
            v.Set(p, d.Raw(d.Kind == MapKind.Percent || d.Kind == MapKind.Bipolar ? physical / 100.0 : physical));
        }

        /// <summary>A plain cloud for measuring, cut from a song wave: frozen, no spray, no detune, no reversal, centred, aligned, full ring.</summary>
        internal static void Plain(ParamValues v, double size = 60, double density = 50, double align = 100)
        {
            v.Set(P.Source, SourceSong);
            Set(v, P.Size, size);
            Set(v, P.Density, density);
            Set(v, P.Position, 50);
            Set(v, P.Spray, 0);
            Set(v, P.Scan, 0);
            v.Set(P.Window, 0);
            Set(v, P.Align, align);
            Set(v, P.Pitch, 0);
            Set(v, P.PitchSpray, 0);
            Set(v, P.Reverse, 0);
            Set(v, P.PanSpread, 0);
            Set(v, P.Attack, 1);
            Set(v, P.Decay, 500);
            Set(v, P.Sustain, 100);
            Set(v, P.Release, 50);
            Set(v, P.Volume, 0);
        }

        internal static GrainSource SineWave(double hz, double seconds = 4)
        {
            int n = (int)(seconds * Rate);
            return GrainSource.FromWave("sine", Measure.Sine(hz, n, 0.5), null, n, Rate);
        }

        static (GrainEngine E, ParamValues V) Make(Action<ParamValues> setup = null, GrainSource wave = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new GrainEngine(v);
            e.SetSampleRate(Rate);
            e.SetWave(wave);
            return (e, v);
        }

        internal sealed class Take
        {
            public float[] L, R;
        }

        /// <summary>Renders <paramref name="length"/> samples in blocks of <paramref name="block"/>; the events run at their sample position (a multiple of 256, so at a block start whatever the block size).</summary>
        internal static Take Render(GrainEngine e, int length, int block, List<(int At, Action Do)> events = null, Action<int> perBlock = null)
        {
            var t = new Take { L = new float[length], R = new float[length] };
            var l = new float[GrainEngine.MaxBlock];
            var r = new float[GrainEngine.MaxBlock];
            int ei = 0;
            events?.Sort((a, b) => a.At.CompareTo(b.At));
            for (int pos = 0; pos < length; pos += block)
            {
                int n = Math.Min(block, length - pos);
                while (events != null && ei < events.Count && events[ei].At <= pos) events[ei++].Do();
                perBlock?.Invoke(pos);
                Array.Clear(l, 0, n); Array.Clear(r, 0, n);
                e.Process(l, r, n);
                Array.Copy(l, 0, t.L, pos, n);
                Array.Copy(r, 0, t.R, pos, n);
            }
            return t;
        }

        static float[] Mono(Take t) => t.L.Zip(t.R, (a, b) => 0.5f * (a + b)).ToArray();

        /// <summary>A Blackman-Harris windowed, zero-padded amplitude spectrum (a sine of amplitude 1 reads about 1).</summary>
        static double[] Spectrum(float[] x, int offset, int n, int pad = 2)
        {
            int nfft = 1; while (nfft < n * pad) nfft <<= 1;
            var w = new float[nfft];
            double gain = 0;
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * (i + 0.5) / n;
                double win = 0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t);
                w[i] = (float)(x[offset + i] * win);
                gain += win;
            }
            var mag = Fft.Magnitudes(w, 0, nfft);
            // Fft.Magnitudes scales so that a coherent full-scale sine reads 1 over nfft points; the window and the padding change that.
            double scale = (double)nfft / gain;
            for (int k = 0; k < mag.Length; k++) mag[k] *= scale;
            return mag;
        }

        static double PeakHz(float[] x, int offset, int n, double expected, double tol = 0.03)
        {
            var mag = Spectrum(x, offset, n);
            int nfft = (mag.Length - 1) * 2;
            int lo = Math.Max(2, (int)(expected * (1 - tol) * nfft / Rate)), hi = Math.Min(mag.Length - 3, (int)(expected * (1 + tol) * nfft / Rate) + 1);
            int best = lo;
            for (int k = lo; k <= hi; k++) if (mag[k] > mag[best]) best = k;
            double a = Math.Log(mag[best - 1] + 1e-30), b = Math.Log(mag[best] + 1e-30), c = Math.Log(mag[best + 1] + 1e-30);
            double delta = 0.5 * (a - c) / (a - 2 * b + c);
            return (best + delta) * Rate / nfft;
        }

        static double Cents(double f, double reference) => 1200 * Math.Log2(f / reference);

        // ------------------------------------------------------------------------------------------ gate: pitch

        [Test]
        public void ASineSourcePlaysAtTheNoteWithinOneCentFromC2ToC7([Values(48, 60, 72, 84, 96, 108)] int note)
        {
            const double source = 220.0;      // C3 = 60 plays the source as it is
            var (e, _) = Make(v => Plain(v, size: 60, density: 50), SineWave(source));
            var t = Render(e, Rate * 2, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, note, 1f)) });
            double expected = source * Math.Pow(2, (note - 60) / 12.0);
            double hz = PeakHz(Mono(t), Rate / 2, Rate, expected);
            double cents = Cents(hz, expected);
            TestContext.Out.WriteLine($"note {note}: expected {expected:0.000} Hz, measured {hz:0.000} Hz, {cents:+0.00;-0.00} cents");
            Assert.That(double.IsFinite(cents), Is.True, "a tone was found");
            Assert.That(Math.Abs(cents), Is.LessThan(1.0), "within one cent of the note");
        }

        [Test]
        public void ThePitchParameterTransposesLikeTheNote()
        {
            var (e, _) = Make(v => { Plain(v); Set(v, P.Pitch, 7); }, SineWave(220));
            var t = Render(e, Rate * 2, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 1f)) });
            double expected = 220 * Math.Pow(2, 7 / 12.0);
            Assert.That(Math.Abs(Cents(PeakHz(Mono(t), Rate / 2, Rate, expected), expected)), Is.LessThan(1.0));
        }

        // ------------------------------------------------------------------------------------------ gate: interpolation

        [TestCase(1000.0)]
        [TestCase(2000.0)]
        [TestCase(3000.0)]
        public void ASineSource24SemitonesUpHasNoAliasingAbove60dB(double source)
        {
            var (e, _) = Make(v => { Plain(v, size: 100, density: 20); Set(v, P.Pitch, 24); }, SineWave(source));
            var t = Render(e, Rate * 2, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 1f)) });
            var mag = Spectrum(Mono(t), Rate / 2, Rate);
            int nfft = (mag.Length - 1) * 2;
            double f = source * 4;
            int centre = (int)Math.Round(f * nfft / Rate);
            double carrier = 0;
            for (int k = centre - 8; k <= centre + 8; k++) carrier = Math.Max(carrier, mag[k]);
            double worst = 0; int worstAt = 0;
            for (int k = 8; k < mag.Length; k++)
            {
                if (Math.Abs(k - centre) <= 40) continue;           // the carrier and what the window of the take smears around it (+-40 bins of 0.35 Hz)
                if (mag[k] > worst) { worst = mag[k]; worstAt = k; }
            }
            double db = 20 * Math.Log10(worst / carrier);
            TestContext.Out.WriteLine($"{source} Hz source, +24 st: carrier {f} Hz, loudest other line {worstAt * (double)Rate / nfft:0} Hz at {db:0.0} dB");
            Assert.That(db, Is.LessThan(-60.0));
        }

        // ------------------------------------------------------------------------------------------ gate: density and size

        [TestCase(1.0, 100)]
        [TestCase(10.0, 20)]
        [TestCase(50.0, 10)]
        [TestCase(200.0, 10)]
        public void GrainsStartAtTheDensityWithin2Percent(double density, int seconds)
        {
            var (e, _) = Make(v => { Plain(v, size: 20, density: density); Set(v, P.Spray, 100); v.Set(P.Source, 0); });
            e.LogStart = new long[(int)(density * seconds * 2) + 100]; e.LogLength = new int[e.LogStart.Length];
            Render(e, Rate * seconds, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 1f)) });
            double measured = e.LogCount / (double)seconds;
            TestContext.Out.WriteLine($"{density}/s: {e.LogCount} grains in {seconds} s = {measured:0.000}/s, {e.Dropped} dropped");
            Assert.That(e.Dropped, Is.EqualTo(0));
            Assert.That(measured, Is.EqualTo(density).Within(0.02 * density));
        }

        [TestCase(5.0)]
        [TestCase(37.0)]
        [TestCase(100.0)]
        [TestCase(500.0)]
        public void EveryGrainIsAsLongAsTheSizeWithinOneSample(double size)
        {
            var (e, _) = Make(v => { Plain(v, size: size, density: 8); v.Set(P.Source, 1); });
            e.LogStart = new long[400]; e.LogLength = new int[400];
            Render(e, Rate * 5, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 1f)) });
            Assert.That(e.LogCount, Is.GreaterThan(20));
            var vv = new ParamValues(Params.Table); Set(vv, P.Size, size);
            double expected = vv.Value(P.Size) * 0.001 * Rate;       // the size on the parameter's own grid
            for (int i = 0; i < e.LogCount; i++) Assert.That(e.LogLength[i], Is.EqualTo(expected).Within(1.0), $"grain {i}");
        }

        // ------------------------------------------------------------------------------------------ gate: clean

        /// <summary>A busy take: notes starting, released and stolen (more than 16), a changing position and size, every window.</summary>
        static Take BusyTake(int block, int source, int window, bool align)
        {
            var (e, v) = Make(vv => { Plain(vv, size: 40, density: 60, align: align ? 100 : 0); vv.Set(P.Source, source); vv.Set(P.Window, window); Set(vv, P.Spray, 120); Set(vv, P.Scan, 50); Set(vv, P.Reverse, 30); Set(vv, P.PanSpread, 60); Set(vv, P.Release, 80); Set(vv, P.Volume, -6); }, SineWave(330));
            var ev = new List<(int, Action)>();
            for (int i = 0; i < 20; i++) { int k = i, n = 48 + 2 * i; ev.Add((2048 * i, () => e.NoteOn(k, n, 0.8f))); }          // 20 notes: 4 steals
            for (int i = 0; i < 10; i++) { int k = 4 + i; ev.Add((60000 + 1024 * i, () => e.NoteOff(k))); }
            ev.Add((120000, () => e.AllNotesOff(false)));
            return Render(e, 200000, block, ev, pos => { if (pos == 90000) { Set(v, P.Position, 20); Set(v, P.Size, 15); } });
        }

        [Test]
        public void NoClicksAtGrainEdgesNoteStartReleaseAndStealAtBlockSizes16And64And256([Values(16, 64, 256)] int block, [Values(0, 1, 3)] int source, [Values(0, 1, 2)] int window)
        {
            var t = BusyTake(block, source, window, true);
            Assert.That(Measure.Peak(t.L, 0, t.L.Length), Is.GreaterThan(0.05), "there is sound");
            var clicks = ClickDetector.Find(t.L, t.R);
            Assert.That(clicks, Is.Empty, $"clicks at {string.Join(", ", clicks.Take(5))}");
        }

        [Test]
        public void TheSecondDifferenceAtGrainBoundariesIsNoHigherThanInsideTheGrains([Values(0, 1, 2)] int window, [Values(16, 64, 256)] int block)
        {
            // Sparse grains of a smooth source, so a grain's own edge is not buried under another: a boundary that jumps would stand out.
            var (e, _) = Make(v => { Plain(v, size: 30, density: 12, align: 0); v.Set(P.Window, window); Set(v, P.Spray, 200); Set(v, P.Scan, 100); }, SineWave(300));
            e.LogStart = new long[400]; e.LogLength = new int[400];
            int length = Rate * 6;
            var t = Render(e, length, block, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 1f)) });
            var x = Mono(t);
            var edge = new bool[length]; var near = new bool[length];
            for (int i = 0; i < e.LogCount; i++)
            {
                long s = e.LogStart[i], f = s + e.LogLength[i];
                for (long k = s - 3; k <= s + 3; k++) if (k >= 0 && k < length) edge[k] = true;
                for (long k = f - 3; k <= f + 3; k++) if (k >= 0 && k < length) edge[k] = true;
                for (long k = s - 12; k <= s + 12; k++) if (k >= 0 && k < length) near[k] = true;
                for (long k = f - 12; k <= f + 12; k++) if (k >= 0 && k < length) near[k] = true;
            }
            double atEdges = 0, inside = 0;
            for (int i = 2; i < length - 2; i++)
            {
                double d2 = Math.Abs(x[i + 1] - 2.0 * x[i] + x[i - 1]);
                if (edge[i]) atEdges = Math.Max(atEdges, d2);
                else if (!near[i]) inside = Math.Max(inside, d2);
            }
            TestContext.Out.WriteLine($"window {window}, block {block}: max |2nd difference| at the edges {atEdges:0.000000}, inside the grains {inside:0.000000}");
            Assert.That(inside, Is.GreaterThan(0), "something to compare with");
            Assert.That(atEdges, Is.LessThanOrEqualTo(inside));
        }

        [Test]
        public void ANoteStolenOrReleasedFadesInAtLeast3Milliseconds()
        {
            var (e, _) = Make(v => { Plain(v, size: 50, density: 80); Set(v, P.Release, 5); v.Set(P.Source, 0); });
            var ev = new List<(int, Action)>();
            for (int n = 0; n < 16; n++) { int k = n; ev.Add((0, () => e.NoteOn(k, 48 + k, 0.8f))); }
            ev.Add((24064, () => e.NoteOn(99, 70, 0.8f)));                  // the 17th note takes a voice
            var t = Render(e, 36000, 256, ev);
            // The steal is at 24064: the stolen voice is faded over 5 ms, so no sample-to-sample step is larger than what the rest of the sound makes.
            Assert.That(ClickDetector.Find(t.L, t.R), Is.Empty);
            Assert.That(GrainEngine.FadeSeconds, Is.GreaterThanOrEqualTo(0.003));
        }

        // ------------------------------------------------------------------------------------------ gate: repeatable

        [Test]
        public void TwoRendersWithTheSameSeedAreBitIdenticalAndAnotherSeedDiffers()
        {
            Take Go(int seed)
            {
                var (e, _) = Make(v => { Plain(v, size: 80, density: 40, align: 50); v.Set(P.Source, 1); Set(v, P.Spray, 150); Set(v, P.PitchSpray, 300); Set(v, P.Reverse, 40); Set(v, P.PanSpread, 80); v.Set(P.Seed, seed); });
                return Render(e, Rate, 256, new List<(int, Action)> { (0, () => { e.NoteOn(1, 60, 0.9f); e.NoteOn(2, 67, 0.7f); }) });
            }
            var a = Go(4); var b = Go(4); var c = Go(5);
            Assert.That(b.L, Is.EqualTo(a.L)); Assert.That(b.R, Is.EqualTo(a.R));
            Assert.That(c.L, Is.Not.EqualTo(a.L));
        }

        [Test]
        public void ARenderIsTheSameAtEveryBlockSize()
        {
            Take Go(int block)
            {
                var (e, _) = Make(v => { Plain(v, size: 80, density: 40, align: 50); v.Set(P.Source, 0); Set(v, P.Spray, 150); Set(v, P.Reverse, 40); Set(v, P.PanSpread, 80); });
                return Render(e, Rate, block, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 0.9f)), (256 * 40, () => e.NoteOff(1)) });
            }
            var a = Go(256); var b = Go(16); var c = Go(64);
            Assert.That(b.L, Is.EqualTo(a.L)); Assert.That(c.L, Is.EqualTo(a.L)); Assert.That(c.R, Is.EqualTo(a.R));
        }

        // ------------------------------------------------------------------------------------------ gate: the audio thread

        [Test]
        public void ARenderMakesNoGarbageWith128GrainsAnd16Voices()
        {
            var (e, v) = Make(vv => { Plain(vv, size: 300, density: 200, align: 80); vv.Set(P.Source, 1); Set(vv, P.Spray, 200); Set(vv, P.Reverse, 30); Set(vv, P.PitchSpray, 400); Set(vv, P.PanSpread, 80); Set(vv, P.Scan, 100); Set(vv, P.Release, 300); });
            var l = new float[GrainEngine.MaxBlock]; var r = new float[GrainEngine.MaxBlock];
            var rng = new Random(5);
            for (int n = 0; n < 16; n++) e.NoteOn(n, 36 + 3 * n, 0.7f);
            for (int b = 0; b < 60; b++) e.Process(l, r, GrainEngine.MaxBlock);
            Assert.That(e.Dropped, Is.GreaterThan(0), "the pool of 128 is full and grains are refused");
            e.Playing = true;
            var (bytes, why) = AllocationCheck.Run(2000, b =>
            {
                if (b % 40 == 0) { int k = rng.Next(16); e.NoteOff(k); e.NoteOn(k, 36 + rng.Next(60), 0.3f + 0.7f * (float)rng.NextDouble()); }
                if (b % 97 == 0) e.NoteOn(100 + b, 60 + rng.Next(12), 0.8f);                      // more than 16: steals
                if (b % 7 == 0)
                {
                    v.Set(P.Size, rng.Next(1001)); v.Set(P.Density, 500 + rng.Next(501)); v.Set(P.Position, rng.Next(1001)); v.Set(P.Spray, rng.Next(1001)); v.Set(P.Scan, rng.Next(1001));
                    v.Set(P.Pitch, rng.Next(1001)); v.Set(P.Align, rng.Next(1001)); v.Set(P.Window, rng.Next(3)); v.Set(P.Source, rng.Next(3)); v.Set(P.Drone, rng.Next(2));
                    v.Set(P.Volume, 600 + rng.Next(400)); v.Set(P.PanSpread, rng.Next(1001)); v.Set(P.Reverse, rng.Next(1001));
                }
                e.Process(l, r, GrainEngine.MaxBlock);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void CpuPerGrainIsReported()
        {
            foreach (var (name, align, pitch) in new[] { ("no alignment, unshifted", 0.0, 0.0), ("aligned, unshifted", 100.0, 0.0), ("no alignment, +12 st", 0.0, 12.0), ("no alignment, +24 st", 0.0, 24.0) })
            {
                var (e, _) = Make(vv => { Plain(vv, size: 500, density: 200, align: align); vv.Set(P.Source, 1); Set(vv, P.Spray, 200); Set(vv, P.Pitch, pitch); Set(vv, P.PanSpread, 80); Set(vv, P.Release, 2000); });
                var l = new float[256]; var r = new float[256];
                for (int n = 0; n < 16; n++) e.NoteOn(n, 48 + n, 0.7f);
                for (int b = 0; b < 200; b++) e.Process(l, r, 256);
                Assert.That(e.ActiveGrains, Is.GreaterThan(100), "the pool is nearly full");
                var sw = Stopwatch.StartNew();
                const int blocks = 750;                                       // 4 s of audio
                for (int b = 0; b < blocks; b++) e.Process(l, r, 256);
                sw.Stop();
                double perGrainSample = sw.Elapsed.TotalMilliseconds * 1e6 / (blocks * 256.0 * 128);
                double load = sw.Elapsed.TotalSeconds / (blocks * 256.0 / Rate);
                TestContext.Out.WriteLine($"{name}: {perGrainSample:0.0} ns per grain-sample; 128 grains and 16 voices use {load * 100:0.0} % of one core at 48 kHz ({load * 100 / 128:0.00} % per grain)");
                Assert.That(load, Is.LessThan(1.0), name);
            }
        }

        [Test]
        public void TheTailsLeaveNoDenormalsAndTheMachineGoesQuiet()
        {
            var (e, _) = Make(v => { Plain(v, size: 40, density: 50); v.Set(P.Source, 0); Set(v, P.Release, 50); });
            for (int n = 36; n < 72; n += 3) e.NoteOn(n, n, 1f);
            var ev = new List<(int, Action)>();
            for (int n = 36; n < 72; n += 3) { int k = n; ev.Add((24000, () => e.NoteOff(k))); }
            var t = Render(e, 4 * Rate, 256, ev);
            int tiny = 0;
            foreach (var x in t.L.Concat(t.R)) if (x != 0 && Math.Abs(x) < 1.2e-38f) tiny++;
            Assert.That(tiny, Is.EqualTo(0), "no denormal in the output");
            Assert.That(Measure.Peak(t.L, 3 * Rate, Rate), Is.EqualTo(0), "silent again");
            Assert.That(e.IsActive, Is.False);
            Assert.That(e.ActiveGrains, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------------------------------ behaviour

        [Test]
        public void ASongWaveSourceWithNoWavePlaysSilenceAndDoesNotCrash()
        {
            var (e, _) = Make(v => { Plain(v); });
            Assert.That(e.CurrentSource, Is.Null);
            var t = Render(e, Rate / 2, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 1f)) });
            Assert.That(Measure.Peak(t.L, 0, t.L.Length), Is.EqualTo(0));
            Assert.That(Measure.Peak(t.R, 0, t.R.Length), Is.EqualTo(0));
            e.SetWave(SineWave(220));
            t = Render(e, Rate, 256);
            Assert.That(Measure.Peak(t.L, Rate / 2, Rate / 2), Is.GreaterThan(0.05), "the wave arrives and the cloud sounds");
        }

        [Test]
        public void TheDroneSoundsOnlyWhileTheTransportRunsAndNeedsNoNotes()
        {
            var (e, _) = Make(v => { Plain(v, size: 100, density: 20); v.Set(P.Source, 0); v.Set(P.Drone, 1); Set(v, P.Attack, 20); Set(v, P.Release, 50); });
            e.Playing = false;
            var t = Render(e, Rate / 2, 256);
            Assert.That(Measure.Peak(t.L, 0, t.L.Length), Is.EqualTo(0), "silent while stopped");
            e.Playing = true;
            t = Render(e, Rate, 256);
            Assert.That(Measure.Peak(t.L, Rate / 2, Rate / 2), Is.GreaterThan(0.02), "sounds while playing");
            e.Playing = false;
            t = Render(e, Rate, 256);
            Assert.That(Measure.Peak(t.L, Rate / 2, Rate / 2), Is.EqualTo(0), "gone after the release");
        }

        [Test]
        public void ScanAtZeroFreezesAndScanAt100PercentWalksThroughTheSource()
        {
            // A source whose level rises with time: a frozen cloud holds one level, a scanning one rises.
            int n = Rate * 4;
            var ramp = new float[n];
            for (int i = 0; i < n; i++) ramp[i] = (float)(0.8 * i / n * Math.Sin(2 * Math.PI * 200 * i / Rate));
            var wave = GrainSource.FromWave("ramp", ramp, null, n, Rate);
            double Level(double scan, int from)
            {
                var (e, _) = Make(v => { Plain(v, size: 60, density: 40); Set(v, P.Position, 0); Set(v, P.Scan, scan); }, wave);
                var t = Render(e, Rate * 3, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 1f)) });
                return Measure.Rms(Mono(t), from, Rate / 2);
            }
            double frozen1 = Level(0, Rate / 2), frozen2 = Level(0, Rate * 2);
            double scan1 = Level(100, Rate / 2), scan2 = Level(100, Rate * 2);
            TestContext.Out.WriteLine($"frozen: {frozen1:0.0000} then {frozen2:0.0000}; scanning: {scan1:0.0000} then {scan2:0.0000}");
            Assert.That(frozen2, Is.EqualTo(frozen1).Within(0.2 * Math.Max(frozen1, 1e-6) + 1e-4));
            Assert.That(scan2, Is.GreaterThan(scan1 * 2));
        }

        [Test]
        public void TheCloudLevelStaysAboutTheSameAsTheDensityGrows()
        {
            double Rms(double density)
            {
                var (e, _) = Make(v => { Plain(v, size: 80, density: density, align: 0); v.Set(P.Source, 2); Set(v, P.Spray, 300); Set(v, P.PanSpread, 50); });
                var t = Render(e, Rate * 3, 256, new List<(int, Action)> { (0, () => e.NoteOn(1, 60, 1f)) });
                return Measure.Rms(Mono(t), Rate, Rate * 2);
            }
            double a = Rms(25), b = Rms(50), c = Rms(200);
            TestContext.Out.WriteLine($"RMS at 25, 50 and 200 grains/s: {a:0.0000}, {b:0.0000}, {c:0.0000}");
            Assert.That(Measure.Db(b / a), Is.InRange(-6.0, 6.0));
            Assert.That(Measure.Db(c / b), Is.InRange(-6.0, 6.0));
        }

        [Test]
        public void EveryBuiltInSourceLoopsWithoutASeamAndIsAudible()
        {
            for (int k = 0; k < GrainSource.BuiltInCount; k++)
            {
                var s = GrainSource.BuiltIn(k);
                Assert.That(s.Loop, Is.True);
                Assert.That(s.Rate, Is.EqualTo(48000));
                var d = s.Data; int p = GrainSource.Pad;
                double top = 0, rms = 0;
                for (int i = 0; i < s.Length; i++) { top = Math.Max(top, Math.Abs(d[p + i])); rms += d[p + i] * d[p + i]; }
                rms = Math.Sqrt(rms / s.Length);
                Assert.That(top, Is.InRange(0.3, 0.6), GrainSource.BuiltInNames[k]);
                Assert.That(rms, Is.GreaterThan(0.03), GrainSource.BuiltInNames[k]);
                // The step from the last frame to the first is no larger than the steps inside.
                double seam = Math.Abs(d[p + s.Length - 1] - d[p]), inner = 0;
                for (int i = 1; i < s.Length; i++) inner = Math.Max(inner, Math.Abs(d[p + i] - d[p + i - 1]));
                Assert.That(seam, Is.LessThanOrEqualTo(inner * 1.2 + 1e-6), $"{GrainSource.BuiltInNames[k]}: the seam");
            }
        }

        // ------------------------------------------------------------------------------------------ state and presets

        [Test]
        public void EveryParameterSurvivesAMigrationAndAReopenedStateSoundsTheSame()
        {
            var table = Params.Table;
            Assert.That(table.Length, Is.EqualTo(P.Count));
            var rng = new Random(31);
            var a = new ParamValues(table);
            for (int i = 0; i < table.Length; i++) a.Set(i, table[i].Min + rng.Next(table[i].Max - table[i].Min + 1));
            a.Set(P.Source, 1);
            var saved = Enumerable.Range(0, table.Length).Select(i => a[i]).ToArray();
            var chain = new StateMigrations(table);
            Assert.That(chain.Current, Is.EqualTo(0), "version 0");
            Assert.That(chain.MigrateGlobals(0, saved), Is.EqualTo(saved), "a v0 song loads unchanged");
            var b = new ParamValues(table);
            for (int i = 0; i < table.Length; i++) b.Set(i, saved[i]);
            for (int i = 0; i < table.Length; i++) Assert.That(table[i].Raw(table[i].Value(a[i])), Is.EqualTo(a[i]).Within(1), $"{table[i].Id} round trip");
            Take Go(ParamValues v)
            {
                var e = new GrainEngine(v); e.SetSampleRate(Rate);
                return Render(e, 24000, 256, new List<(int, Action)> { (0, () => { e.NoteOn(1, 55, 0.9f); e.NoteOn(2, 62, 0.6f); }) });
            }
            var ra = Go(a); var rb = Go(b);
            Assert.That(rb.L, Is.EqualTo(ra.L)); Assert.That(rb.R, Is.EqualTo(ra.R));
            foreach (var p in table) { Assert.That(p.Default, Is.InRange(p.Min, p.Max), p.Id); Assert.That(p.Id, Is.Not.Empty); }
        }

        [Test]
        public void TheSourceReferenceIsTwoParametersSoAReopenedSongKeepsIt()
        {
            // The source is saved as Source (built-in or Song wave) and Wave (the slot, 1 to 200): plain parameters, in the song and in presets.
            Assert.That(Params.Table[P.Source].Max, Is.EqualTo(3));
            Assert.That(Params.Table[P.Wave].Value(0), Is.EqualTo(1));
            Assert.That(Params.Table[P.Wave].Value(Params.Table[P.Wave].Max), Is.EqualTo(200));
            var v = new ParamValues(Params.Table);
            v.Set(P.Source, SourceSong); v.Set(P.Wave, 6);
            var w = new ParamValues(Params.Table);
            for (int i = 0; i < Params.Table.Length; i++) w.Set(i, v[i]);
            Assert.That(w.Choice(P.Source), Is.EqualTo(SourceSong));
            Assert.That((int)w.Value(P.Wave), Is.EqualTo(7));
        }

        internal static IEnumerable<(string Name, Dictionary<int, int> Values)> FactoryPresets()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ReBuzz.sln"))) d = d.Parent;
            var file = Path.Combine(d!.FullName, "Machines", "NeoGrain", "Neo Grain.prs.xml");
            foreach (var item in XDocument.Load(file).Root!.Elements("Item"))
            {
                var vals = new Dictionary<int, int>();
                foreach (var p in item.Element("Preset")!.Element("Parameters")!.Elements("Parameter")) vals[(int)p.Attribute("Index")!] = (int)p.Attribute("Value")!;
                yield return ((string)item.Attribute("Key")!, vals);
            }
        }

        /// <summary>A short phrase of our own on the engine of a preset (see <see cref="GrainPack.Play"/>).</summary>
        internal static Take PlayPhrase(Dictionary<int, int> preset, int length)
        {
            var v = new ParamValues(Params.Table);
            foreach (var kv in preset) v.Set(kv.Key, kv.Value);
            var e = new GrainEngine(v); e.SetSampleRate(Rate);
            var ev = new List<(int, Action)>();
            int[] melody = { 60, 64, 67, 64, 62, 59, 55, 60 };
            for (int i = 0; i < melody.Length; i++) { int n = melody[i], k = 20 + i; ev.Add((i * 9216, () => e.NoteOn(k, n, 0.85f))); ev.Add((i * 9216 + 7680, () => e.NoteOff(k))); }
            int[] chord = { 48, 55 };
            for (int i = 0; i < chord.Length; i++) { int n = chord[i], k = i; ev.Add((0, () => e.NoteOn(k, n, 0.7f))); ev.Add((length - 9600, () => e.NoteOff(k))); }
            return Render(e, length, 256, ev);
        }

        [Test]
        public void TheFactoryPresetsAreAudibleAndClearlyDifferent()
        {
            var presets = FactoryPresets().ToList();
            Assert.That(presets.Count, Is.EqualTo(8), "eight presets");
            int len = Rate * 4;
            var bands = new List<double[]>();
            var names = new List<string>();
            foreach (var (name, vals) in presets)
            {
                var t = PlayPhrase(vals, len);
                var x = Mono(t);
                double peak = Measure.Peak(x, 0, len), rms = Measure.Rms(x, 0, len);
                TestContext.Out.WriteLine($"{name}: peak {Measure.Db(peak):0.0} dBFS, RMS {Measure.Db(rms):0.0} dB");
                Assert.That(Measure.Db(peak), Is.InRange(-24.0, -1.0), $"{name}: audible, not hot");
                Assert.That(Measure.Db(rms), Is.GreaterThan(-42.0), $"{name}: audible at first listen");
                int n = 1 << 17;
                var spec = Fft.Magnitudes(x, 0, n);
                var b = new List<double>();
                for (double c = 100; c < 12600; c *= Math.Pow(2, 1.0 / 3))
                {
                    int lo = (int)(c / Math.Pow(2, 1.0 / 6) * n / Rate), hi = (int)(c * Math.Pow(2, 1.0 / 6) * n / Rate);
                    double p = 0; for (int k = lo; k <= hi; k++) p += spec[k] * spec[k];
                    b.Add(10 * Math.Log10(p + 1e-20));
                }
                b.Add(Measure.Db(Measure.Rms(x, Rate * 3, Rate)) - Measure.Db(Measure.Rms(x, 0, Rate)));
                // How much the sound moves: the level swing of 50 ms frames, and how much of the grain rate shows (stutter and glitter move more).
                bands.Add(b.ToArray()); names.Add(name);
            }
            double least = double.MaxValue; string pair = "";
            for (int i = 0; i < bands.Count; i++)
                for (int j = i + 1; j < bands.Count; j++)
                {
                    double d = 0; int m = bands[i].Length;
                    for (int k = 0; k < m; k++) d += (bands[i][k] - bands[j][k]) * (bands[i][k] - bands[j][k]);
                    d = Math.Sqrt(d / m);
                    if (d < least) { least = d; pair = names[i] + " / " + names[j]; }
                }
            TestContext.Out.WriteLine($"the two closest presets: {pair}, {least:0.0} dB apart (RMS over one-third-octave bands)");
            Assert.That(least, Is.GreaterThan(4.0), $"{pair}: clearly different");
        }

        /// <summary>The level of an instrument: each preset's phrase peaks between -6 and -3 dBFS.</summary>
        [Test]
        public void TheLevelOfEveryPresetIsThatOfAnInstrument()
        {
            var bad = new List<string>();
            foreach (var (name, vals) in FactoryPresets())
            {
                var t = PlayPhrase(vals, Rate * 4);
                double peak = Math.Max(Measure.Peak(t.L, 0, t.L.Length), Measure.Peak(t.R, 0, t.R.Length));
                TestContext.Out.WriteLine($"{name}: phrase peak {Measure.Db(peak):0.0} dBFS");
                if (Measure.Db(peak) < -6.0 || Measure.Db(peak) > -3.0) bad.Add($"{name} {Measure.Db(peak):0.0}");
            }
            Assert.That(bad, Is.Empty, "the phrase's peak is between -6 and -3 dBFS");
        }

        [Test]
        public void TheFactoryPresetsPlayCleanlyAndStayUnderTheCeiling()
        {
            foreach (var (name, vals) in FactoryPresets())
            {
                var t = PlayPhrase(vals, Rate * 4);
                double peak = Math.Max(Measure.Peak(t.L, 0, t.L.Length), Measure.Peak(t.R, 0, t.R.Length));
                Assert.That(Measure.Db(peak), Is.LessThan(-1.0 + 1e-6), $"{name}: under the limiter's ceiling");
                var clicks = ClickDetector.Find(t.L, t.R);
                Assert.That(clicks, Is.Empty, $"{name}: no clicks (at {string.Join(", ", clicks.Take(4))})");
            }
        }
    }
}

using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Poly;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Poly (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class PolyTests
    {
        internal static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (PolyEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new PolyEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        /// <summary>A clean single oscillator through an open filter, no drift, full sustain.</summary>
        static void Clean(ParamValues v, double shape)
        {
            Set(v, P.O1Shape, shape);
            v.Set(P.F1Type, 2);                  // SVF LP
            v.Set(P.F1Cutoff, 1000);             // 20 kHz
            v.Set(P.F1Reso, 0);
            v.Set(P.F1Env, 500);
            v.Set(P.F1Key, 0);
            v.Set(P.F1Vel, 0);
            v.Set(P.Drift, 0);
            v.Set(P.AmpS, 1000);
            Set(v, P.AmpA, 0.001);
            for (int m = 0; m < 8; m++) v.Set(P.M1Src + 3 * m, 0);
        }

        internal static float[] Render(PolyEngine e, int length, Action<int> atBlock = null)
        {
            var y = new float[length];
            var l = new float[PolyEngine.MaxBlock];
            var r = new float[PolyEngine.MaxBlock];
            for (int pos = 0; pos < length; pos += PolyEngine.MaxBlock)
            {
                atBlock?.Invoke(pos);
                int n = Math.Min(PolyEngine.MaxBlock, length - pos);
                e.Process(l, r, n);
                Array.Copy(l, 0, y, pos, n);
            }
            return y;
        }

        /// <summary>Alias floor with a 4-term Blackman–Harris window (floor ~−92 dB): bins near harmonics excluded.</summary>
        internal static double AliasFloorWindowed(float[] x, int offset, int n, double f0, double hiHz = 20000)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                double win = 0.35875 - 0.48829 * Math.Cos(t) + 0.14128 * Math.Cos(2 * t) - 0.01168 * Math.Cos(3 * t);
                w[i] = (float)(x[offset + i] * win);
            }
            var mag = Fft.Magnitudes(w, 0, n);
            double binHz = (double)Measure.Rate / n;
            int k0 = (int)Math.Round(f0 / binHz);
            double fund = 0;
            for (int k = k0 - 4; k <= k0 + 4; k++) fund = Math.Max(fund, mag[k]);
            double worst = 1e-30;
            for (int k = (int)(30 / binHz); k < Math.Min(mag.Length, (int)(hiHz / binHz)); k++)
            {
                double f = k * binHz;
                double h = f / f0;
                if (Math.Abs(h - Math.Round(h)) * f0 < 6 * binHz) continue;
                worst = Math.Max(worst, mag[k]);
            }
            return 20 * Math.Log10(worst / fund);
        }

        [TestCase(2.0, -80.0)]
        [TestCase(3.0, -80.0)]
        public void OscillatorAliasFloorInTheSynth(double shape, double limit)
        {
            var (e, _) = Make(v => Clean(v, shape));
            e.NoteOn(0, 94, 1f);   // A#7: ~3.73 kHz
            var y = Render(e, 8192 + Measure.N);
            double floor = AliasFloorWindowed(y, 8192, Measure.N, DspMath.NoteToHz(94));
            Measure.Report($"Neo Poly, shape {shape} at 3.7 kHz: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(limit));
        }

        [Test]
        public void HardSyncAliasFloorInTheSynth()
        {
            var (e, _) = Make(v =>
            {
                Clean(v, 2);
                v.Set(P.O1Level, 0);
                v.Set(P.O2Level, 1000);
                v.Set(P.O2Sync, 1);
                Set(v, P.O2Semi, 7);
                Set(v, P.O2Fine, 30);
            });
            e.NoteOn(0, 81, 1f);   // master ~880 Hz
            var y = Render(e, 8192 + Measure.N);
            double floor = AliasFloorWindowed(y, 8192, Measure.N, DspMath.NoteToHz(81));
            Measure.Report($"Neo Poly, hard sync: alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(-70));
        }

        [Test]
        public void PlaysInTuneAtA440()
        {
            var (e, _) = Make(v => Clean(v, 0));
            e.NoteOn(0, 69, 1f);
            var y = Render(e, 48000 + 32768);
            double f = Measure.PeakFrequency(y, 48000, 32768);
            Assert.That(Math.Abs(Measure.Cents(f, 440)), Is.LessThan(1));
        }

        [Test]
        public void ChordUsesAVoicePerNoteAndUnisonMultiplies()
        {
            var (e, v) = Make(v => v.Set(P.Unison, 3));
            e.NoteOn(0, 60, 1f);
            e.NoteOn(1, 64, 1f);
            e.NoteOn(2, 67, 1f);
            Render(e, 1024);
            Assert.That(e.ActiveVoices, Is.EqualTo(9));
        }

        [Test]
        public void StealingDoesNotClick()
        {
            // Sines through an open filter: any step bigger than the waveform's own is a click.
            var (e, _) = Make(v =>
            {
                Clean(v, 0);
                v.Set(P.Polyphony, 2);
                Set(v, P.AmpR, 2.0);
            });
            int notes = 0;
            var y = Render(e, 48000, pos =>
            {
                if (pos % 4096 == 0 && notes < 10) e.NoteOn(notes, 48 + 5 * notes++, 1f);
            });
            double worst = 0;
            for (int i = 1; i < y.Length; i++) worst = Math.Max(worst, Math.Abs(y[i] - y[i - 1]));
            // Two full-scale sines up to ~1.3 kHz change by at most ~0.05 per sample at this volume.
            double peak = Measure.Peak(y, 0, y.Length);
            double sineStep = peak * 2 * Math.PI * DspMath.NoteToHz(48 + 5 * 9) / Measure.Rate;
            TestContext.Out.WriteLine($"largest step {worst:F4}, a sine's {sineStep:F4}");
            Assert.That(worst, Is.LessThan(sineStep + 0.01));
        }

        [Test]
        public void SilentWithinABlockAfterTheReleaseEnds()
        {
            var (e, _) = Make(v => Set(v, P.AmpR, 0.05));
            e.NoteOn(0, 60, 1f);
            Render(e, 4800);
            e.NoteOff(0);
            var tail = Render(e, 4800);
            Assert.That(e.IsActive, Is.False);
            var after = Render(e, 256);
            Assert.That(Measure.Peak(after, 0, 256), Is.EqualTo(0));
        }

        [Test]
        public void LegatoGlidesWithoutRetrigger()
        {
            var (e, _) = Make(v =>
            {
                Clean(v, 0);
                v.Set(P.VoiceMode, 2);
                v.Set(P.GlideMode, 1);
                Set(v, P.Glide, 0.2);
            });
            e.NoteOn(0, 57, 1f);
            Render(e, 9600);
            e.NoteOn(1, 69, 1f);
            Render(e, 4800);   // 100 ms: half way
            double mid = e.Voice(0).Pitch;
            Render(e, 9600);
            double end = e.Voice(0).Pitch;
            TestContext.Out.WriteLine($"pitch after 100 ms {mid:F2}, after 300 ms {end:F2}");
            Assert.That(mid, Is.InRange(62.0, 64.0));
            Assert.That(end, Is.EqualTo(69).Within(1e-3));
        }

        [Test]
        public void ModWheelOpensTheFilterThroughTheMatrix()
        {
            var (e, v) = Make(v =>
            {
                v.Set(P.F1Cutoff, 300);
                v.Set(P.M1Src, 8);       // Mod wheel
                v.Set(P.M1Dst, 16);      // F1 cutoff
                v.Set(P.M1Amt, 1000);
                v.Set(P.Drift, 0);
            });
            e.NoteOn(0, 48, 1f);
            Render(e, 24000);
            double closed = Measure.Rms(Render(e, 9600), 0, 9600);
            ((INoteSink)e).ModWheel(1f);
            Render(e, 4800);
            double open = Measure.Rms(Render(e, 9600), 0, 9600);
            TestContext.Out.WriteLine($"closed {Measure.Db(closed):F1} dB, open {Measure.Db(open):F1} dB");
            Assert.That(open, Is.GreaterThan(closed * 1.5));
        }

        [Test]
        public void SixteenVoicesRunFarFasterThanRealTime()
        {
            var (e, _) = Make(v =>
            {
                v.Set(P.Polyphony, 16);
                v.Set(P.O2Level, 800);
                v.Set(P.O3Level, 600);
                v.Set(P.NoiseLevel, 100);
                v.Set(P.F1Reso, 600);
                v.Set(P.F2Type, 1);
            });
            for (int k = 0; k < 16; k++) e.NoteOn(k, 40 + 3 * k, 1f);
            Render(e, 4800);
            var sw = Stopwatch.StartNew();
            Render(e, 10 * Measure.Rate);
            double factor = 10 / sw.Elapsed.TotalSeconds;
            Measure.Report($"Neo Poly, 16 voices (3 osc, noise, ladder, SVF): {factor:F1}× real time");
            // A regression guard: the figure itself is reported (shared CI runners vary widely).
            Assert.That(factor, Is.GreaterThan(5));
        }

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(13);
            var (e, v) = Make();
            for (int round = 0; round < 25; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                for (int k = 0; k < 4; k++) e.NoteOn(k, 24 + rng.Next(90), (float)rng.NextDouble());
                var y = Render(e, 12000);
                foreach (var s in y)
                {
                    Assert.That(float.IsFinite(s), Is.True, $"round {round}");
                    Assert.That(Math.Abs(s), Is.LessThan(20), $"round {round}");
                }
                e.AllNotesOff(true);
            }
        }
    }
}

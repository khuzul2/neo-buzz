using System.Diagnostics;
using NeoBuzz.Dsp;
using NeoBuzz.Wave;

namespace NeoBuzz.MachineTests
{
    /// <summary>Neo Wave (docs/specs/machines.md §5): the acceptance criteria, measured.</summary>
    [TestFixture]
    public class WaveTests
    {
        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static (WaveEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null)
        {
            for (int i = 0; i < Tables.Names.Length; i++) Tables.GetNow(i);
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new WaveEngine(v);
            e.SetSampleRate(Measure.Rate);
            return (e, v);
        }

        /// <summary>Osc A only, saw (Basic table at two thirds), an open filter, full sustain.</summary>
        static void Clean(ParamValues v)
        {
            v.Set(P.ATable, 0);
            v.Set(P.APos, 667);
            v.Set(P.BLevel, 0);
            v.Set(P.FType, 6);   // off
            v.Set(P.AmpS, 1000);
            Set(v, P.AmpA, 0.001);
            for (int m = 0; m < 8; m++) v.Set(P.M1Src + 3 * m, 0);
        }

        static float[] Render(WaveEngine e, int length, Action<int> atBlock = null)
        {
            var y = new float[length];
            var l = new float[WaveEngine.MaxBlock];
            var r = new float[WaveEngine.MaxBlock];
            for (int pos = 0; pos < length; pos += WaveEngine.MaxBlock)
            {
                atBlock?.Invoke(pos);
                int n = Math.Min(WaveEngine.MaxBlock, length - pos);
                e.Process(l, r, n);
                Array.Copy(l, 0, y, pos, n);
            }
            return y;
        }

        [Test]
        public void TheUserTableCanBeClearedAndReplacedFromAnyThread()
        {
            var (e, _) = Make(Clean);
            var t1 = Wavetable.FromHarmonics("one", 1, (f, h) => (h == 1 ? 1.0 : 0, 0));
            var t2 = Wavetable.FromHarmonics("two", 1, (f, h) => (h == 2 ? 1.0 : 0, 0));
            var l = new float[64];
            var r = new float[64];
            e.SetUserTable(t1);
            e.Process(l, r, 64);
            Assert.That(e.UserTable, Is.SameAs(t1));
            e.SetUserTable(null);
            e.Process(l, r, 64);
            Assert.That(e.UserTable, Is.Null, "a preset or song without a table clears it");
            e.SetUserTable(t1);
            e.SetUserTable(t2);
            e.Process(l, r, 64);
            Assert.That(e.UserTable, Is.SameAs(t2), "the latest one wins");
        }

        [Test]
        public void BuiltInTablesAreSane()
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Tables.Names.Length; i++)
            {
                var t = Tables.GetNow(i);
                Assert.That(t.FrameCount, Is.EqualTo(Tables.Frames), Tables.Names[i]);
                double peak = 0;
                for (int f = 0; f < t.FrameCount; f += 9)
                    foreach (var s in t.FrameSamples(f))
                    {
                        Assert.That(float.IsFinite(s), Is.True, Tables.Names[i]);
                        peak = Math.Max(peak, Math.Abs(s));
                    }
                Assert.That(peak, Is.InRange(0.3, 1.01), Tables.Names[i]);
            }
            Measure.Report($"Neo Wave: built-in tables ready in {sw.ElapsedMilliseconds} ms (after the first test's build)");
        }

        [TestCase(94)]
        [TestCase(81)]
        [TestCase(57)]
        [TestCase(33)]
        public void SawAliasFloor(int note)
        {
            var (e, _) = Make(Clean);
            e.NoteOn(0, note, 1f);
            var y = Render(e, 8192 + Measure.N);
            double floor = PolyTests.AliasFloorWindowed(y, 8192, Measure.N, DspMath.NoteToHz(note));
            Measure.Report($"Neo Wave, saw table at note {note} ({DspMath.NoteToHz(note):F0} Hz): alias floor {floor:F1} dB");
            Assert.That(floor, Is.LessThan(-80));
        }

        [TestCase(45)]   // 110 Hz
        [TestCase(69)]   // 440 Hz
        [TestCase(93)]   // 1760 Hz
        [TestCase(105)]  // 3520 Hz
        public void KeepsTheAudibleBand(int note)
        {
            var (e, _) = Make(Clean);
            e.NoteOn(0, note, 1f);
            var y = Render(e, 8192 + 32768);
            var mag = Fft.Magnitudes(Hann(y, 8192, 32768), 0, 32768);
            double f0 = DspMath.NoteToHz(note);
            int h = (int)(18000 / f0);
            double binHz = (double)Measure.Rate / 32768;
            double At(double hz)
            {
                int k = (int)Math.Round(hz / binHz);
                double m = 0;
                for (int j = k - 3; j <= k + 3; j++) m = Math.Max(m, mag[j]);
                return m;
            }
            double rel = Measure.Db(At(h * f0) / At(f0));
            double ideal = -20 * Math.Log10(h);
            Measure.Report($"Neo Wave, saw at {f0:F0} Hz: harmonic {h} ({h * f0 / 1000:F1} kHz) at {rel:F1} dB (ideal {ideal:F1})");
            Assert.That(rel, Is.EqualTo(ideal).Within(1.5));
        }

        static float[] Hann(float[] x, int offset, int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = (float)(x[offset + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)));
            return w;
        }

        [Test]
        public void SweepingThePositionDoesNotClick()
        {
            var (e, v) = Make(v =>
            {
                Clean(v);
                v.Set(P.ATable, 3);   // formants
                v.Set(P.M1Src, 1);    // LFO 1
                v.Set(P.M1Dst, 4);    // A position
                v.Set(P.M1Amt, 1000);
                Set(v, P.L1Rate, 2);
                v.Set(P.ATable, 3);
            });
            e.NoteOn(0, 57, 1f);
            var y = Render(e, 96000);
            double worst = 0;
            for (int i = 1; i < y.Length; i++) worst = Math.Max(worst, Math.Abs(y[i] - y[i - 1]));
            double peak = Measure.Peak(y, 0, y.Length);
            Measure.Report($"Neo Wave, position sweep: largest step {worst / peak:F3} of the peak");
            Assert.That(worst / peak, Is.LessThan(0.35));
        }

        [Test]
        public void WavTablesRoundTrip()
        {
            // 8 frames of 2048: sines of 1 … 8 cycles.
            var x = new float[8 * 2048];
            for (int f = 0; f < 8; f++)
                for (int i = 0; i < 2048; i++) x[f * 2048 + i] = (float)Math.Sin(2 * Math.PI * (f + 1) * i / 2048);
            var bytes = WavReader.Write(x, frameSize: 2048);
            var (samples, size) = WavReader.Read(bytes);
            Assert.That(size, Is.EqualTo(2048));
            var t = WavReader.ToTable("test", samples, size);
            Assert.That(t.FrameCount, Is.EqualTo(8));
            var frame = t.FrameSamples(4);
            var mag = Fft.Magnitudes(frame, 0, frame.Length);
            int best = 1;
            for (int k = 1; k < mag.Length; k++) if (mag[k] > mag[best]) best = k;
            Assert.That(best, Is.EqualTo(5), "frame 4 is five cycles");

            // A single cycle of another length becomes one frame.
            var cycle = new float[600];
            for (int i = 0; i < 600; i++) cycle[i] = i < 300 ? 1 : -1;
            var single = WavReader.ToTable("square", WavReader.Read(WavReader.Write(cycle)).Samples, 0);
            Assert.That(single.FrameCount, Is.EqualTo(1));
            var fs0 = single.FrameSamples(0);
            var sm = Fft.Magnitudes(fs0, 0, fs0.Length);
            Assert.That(sm[3] / sm[1], Is.EqualTo(1.0 / 3).Within(0.02), "a square's third harmonic");
        }

        [Test]
        public void EightVoicesRunFasterThanRealTime()
        {
            // Timed against the reference workload in the same run (Throughput.cs).
            Throughput.Guard("Neo Wave, 8 voices (2 tables, sub, ladder, 2× oversampled)", Throughput.Floor(x64: 15, arm64: 14), 2, Measure.Rate, WaveEngine.MaxBlock, () =>
            {
                var (e, _) = Make(v =>
                {
                    v.Set(P.BLevel, 700);
                    v.Set(P.SubLevel, 400);
                    v.Set(P.FReso, 500);
                });
                for (int k = 0; k < 8; k++) e.NoteOn(k, 45 + 4 * k, 1f);
                Render(e, 4800);
                var l = new float[WaveEngine.MaxBlock];
                var r = new float[WaveEngine.MaxBlock];
                return Throughput.InBlocks(WaveEngine.MaxBlock, (pos, n) => e.Process(l, r, n));
            }, Measure.Report);
        }

        [Test]
        public void ExtremeSettingsStayBoundedAndFinite()
        {
            var rng = new Random(21);
            var (e, v) = Make();
            for (int round = 0; round < 25; round++)
            {
                foreach (var d in Params.Table) v.Set(d.Index, rng.Next(3) switch { 0 => d.Min, 1 => d.Max, _ => rng.Next(d.Min, d.Max + 1) });
                for (int k = 0; k < 3; k++) e.NoteOn(k, 24 + rng.Next(90), (float)rng.NextDouble());
                var y = Render(e, 9600);
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

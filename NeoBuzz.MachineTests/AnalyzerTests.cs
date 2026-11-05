using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NeoBuzz.Analysis;
using NeoBuzz.Analyzer;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using P = NeoBuzz.Analyzer.P;
using Params = NeoBuzz.Analyzer.Params;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Analyzer (docs/specs/machines.md §5): the acceptance criteria, measured. Its meters are
    /// checked against the analysis library's batch measures (docs/specs/mcp-server.md §7), which
    /// the agent server's analyze uses.
    /// </summary>
    [TestFixture]
    public class AnalyzerTests
    {
        const int Rate = Measure.Rate;

        static (AnalyzerEngine Engine, ParamValues Values) Make(Action<ParamValues> setup = null, int rate = Rate)
        {
            var v = new ParamValues(Params.Table);
            setup?.Invoke(v);
            var e = new AnalyzerEngine(v);
            e.SetSampleRate(rate);
            return (e, v);
        }

        /// <summary>Feeds a stereo signal in blocks of <paramref name="block"/> (copies, so the input is untouched).</summary>
        static void Run(AnalyzerEngine e, float[] l, float[] r, int block = AnalyzerEngine.MaxBlock)
        {
            var bl = new float[block];
            var br = new float[block];
            for (int pos = 0; pos < l.Length; pos += block)
            {
                int n = Math.Min(block, l.Length - pos);
                Array.Copy(l, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
            }
        }

        static AnalyzerSnapshot Snapshot(AnalyzerEngine e)
        {
            var s = new AnalyzerSnapshot();
            e.Read(s);
            return s;
        }

        static float[] Noise(int n, double rms, uint seed)
        {
            var rng = new Rng(seed);
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = rng.NextBipolar();
            double g = rms / Measure.Rms(x, 0, n);
            for (int i = 0; i < n; i++) x[i] = (float)(x[i] * g);
            return x;
        }

        static float[] Scale(float[] x, double db)
        {
            float g = (float)DspMath.DbToGain(db);
            return x.Select(v => v * g).ToArray();
        }

        static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

        // ------------------------------------------------------------------ loudness and true peak

        /// <summary>Signals with the batch's answers, as the EBU test material exercises it.</summary>
        static IEnumerable<(string Name, float[] L, float[] R)> LoudnessSignals()
        {
            // Tech 3341 case 1: 1 kHz at −23 dBFS on both channels, 20 s.
            var tone = Measure.Sine(1000, 20 * Rate, DspMath.DbToGain(-23));
            yield return ("1 kHz −23 dBFS, 20 s", tone, tone);
            // Tech 3342 style: pink noise stepping −20 / −30 / −20 dB (20 s each), so the range matters.
            var pink = Measure.PinkNoise(20 * Rate, DspMath.DbToGain(-20), 7);
            var pinkR = Measure.PinkNoise(20 * Rate, DspMath.DbToGain(-20), 8);
            yield return ("pink −20/−30/−20, 60 s", Concat(pink, Scale(pink, -10), pink), Concat(pinkR, Scale(pinkR, -10), pinkR));
            // Programme-like: noise bursts at six levels with silences (the gates work), uneven channels.
            var parts = new List<float[]>();
            var partsR = new List<float[]>();
            double[] levels = { -12, -30, -18, -45, -8, -24 };
            for (int k = 0; k < levels.Length; k++)
            {
                parts.Add(Noise(3 * Rate + 1234 * k, DspMath.DbToGain(levels[k]), 100u + (uint)k));
                partsR.Add(Noise(3 * Rate + 1234 * k, DspMath.DbToGain(levels[k] - 3), 200u + (uint)k));
                parts.Add(new float[Rate / 2]);
                partsR.Add(new float[Rate / 2]);
            }
            yield return ("bursts and silences, 22 s", Concat(parts.ToArray()), Concat(partsR.ToArray()));
            // Tech 3341 true-peak case: a quarter-rate sine at 45° (inter-sample peaks 3 dB over the samples).
            var q = new float[5 * Rate];
            var qr = new float[5 * Rate];
            for (int i = 0; i < q.Length; i++)
            {
                q[i] = (float)(0.5 * Math.Sin(Math.PI / 2 * i + Math.PI / 4));
                qr[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 997 * i / Rate));
            }
            yield return ("quarter-rate sine at 45°, 5 s", q, qr);
        }

        [Test]
        public void StreamingLoudnessAndTruePeakAgreeWithTheBatch()
        {
            double worstLu = 0, worstRange = 0, worstPeak = 0, worstSeries = 0;
            foreach (var (name, l, r) in LoudnessSignals())
            {
                var batch = Loudness.Measure(l, r, Rate);
                foreach (int block in new[] { 256, 117 })
                {
                    var meter = new LoudnessMeter(Rate);
                    var tl = new TruePeakMeter();
                    var tr = new TruePeakMeter();
                    var momentary = new List<double>();
                    var shortTerm = new List<double>();
                    for (int pos = 0; pos < l.Length; pos += block)
                    {
                        int n = Math.Min(block, l.Length - pos);
                        // Feed up to each step's end, so every step's windows can be recorded.
                        int at = pos;
                        while (at < pos + n)
                        {
                            int m = Math.Min(pos + n - at, meter.SamplesToStepEnd);
                            if (meter.Process(l, r, at, m) > 0 && meter.Steps >= 4)
                            {
                                momentary.Add(meter.Momentary);
                                if (meter.Steps >= 30) shortTerm.Add(meter.ShortTerm);
                            }
                            at += m;
                        }
                        tl.Process(l, pos, n);
                        tr.Process(r, pos, n);
                    }
                    tl.Flush();
                    tr.Flush();
                    double tp = 20 * Math.Log10(Math.Max(tl.Peak, tr.Peak));
                    double di = meter.Integrated - batch.Integrated, dr = meter.Range - batch.Range;
                    double dm = meter.MomentaryMax - batch.MomentaryMax, ds = meter.ShortTermMax - batch.ShortTermMax;
                    double dp = tp - batch.TruePeakDb;
                    TestContext.Out.WriteLine($"{name}, blocks of {block}: integrated {meter.Integrated:F3} vs {batch.Integrated:F3} LUFS, " +
                        $"range {meter.Range:F3} vs {batch.Range:F3} LU, M max {dm:+0.0000;-0.0000}, S max {ds:+0.0000;-0.0000}, true peak {tp:F3} vs {batch.TruePeakDb:F3} dBTP");
                    Assert.That(Math.Abs(di), Is.LessThan(0.05), name + ": integrated");
                    Assert.That(Math.Abs(dr), Is.LessThan(0.05), name + ": range");
                    Assert.That(Math.Abs(dm), Is.LessThan(0.05), name + ": momentary max");
                    Assert.That(Math.Abs(ds), Is.LessThan(0.05), name + ": short-term max");
                    Assert.That(Math.Abs(dp), Is.LessThan(0.05), name + ": true peak");
                    Assert.That(momentary.Count, Is.EqualTo(batch.Momentary.Length), "a momentary value every 100 ms");
                    Assert.That(shortTerm.Count, Is.EqualTo(batch.ShortTerm.Length), "a short-term value every 100 ms");
                    // Above the gate (far below it, the batch's running sum carries rounding from the loud parts).
                    for (int i = 0; i < momentary.Count; i++)
                        if (batch.Momentary[i] > -70) worstSeries = Math.Max(worstSeries, Math.Abs(momentary[i] - batch.Momentary[i]));
                    for (int i = 0; i < shortTerm.Count; i++)
                        if (batch.ShortTerm[i] > -70) worstSeries = Math.Max(worstSeries, Math.Abs(shortTerm[i] - batch.ShortTerm[i]));
                    worstLu = Math.Max(worstLu, Math.Max(Math.Abs(di), Math.Max(Math.Abs(dm), Math.Abs(ds))));
                    worstRange = Math.Max(worstRange, Math.Abs(dr));
                    worstPeak = Math.Max(worstPeak, Math.Abs(dp));
                }
            }
            Assert.That(worstSeries, Is.LessThan(1e-6), "every momentary and short-term value is the batch's");
            Measure.Report($"Neo Analyzer: streaming meters vs Loudness.Measure: integrated and maxima within {worstLu:F4} LU, range {worstRange:F4} LU, true peak {worstPeak:F4} dB, momentary/short-term series {worstSeries:E1} LU");
        }

        [Test]
        public void TheEngineReadsWhatTheBatchMeasures()
        {
            // The machine's own path (blocks, steps, snapshots), over a whole signal.
            foreach (var (name, l, r) in LoudnessSignals())
            {
                var (e, _) = Make();
                Run(e, l, r);
                var s = Snapshot(e);
                var batch = Loudness.Measure(l, r, Rate);
                TestContext.Out.WriteLine($"{name}: I {s.Integrated:F3} ({batch.Integrated:F3}), LRA {s.Range:F3} ({batch.Range:F3}), TP {s.TruePeakMax:F3} ({batch.TruePeakDb:F3})");
                Assert.That(s.Integrated, Is.EqualTo(batch.Integrated).Within(0.05), name);
                Assert.That(s.Range, Is.EqualTo(batch.Range).Within(0.05), name);
                Assert.That(s.MomentaryMax, Is.EqualTo(batch.MomentaryMax).Within(0.05), name);
                Assert.That(s.ShortTermMax, Is.EqualTo(batch.ShortTermMax).Within(0.05), name);
                // The last 11 samples' neighbourhoods are still to come (the batch measures them against silence).
                Assert.That(s.TruePeakMax, Is.EqualTo(batch.TruePeakDb).Within(0.05), name);
                Assert.That(s.Momentary, Is.EqualTo(batch.Momentary[^1]).Within(1e-4), name + ": momentary now");
                Assert.That(s.ShortTerm, Is.EqualTo(batch.ShortTerm[^1]).Within(1e-4), name + ": short-term now");
                Assert.That(s.Seconds, Is.EqualTo(Math.Floor(l.Length / 4800.0) * 0.1).Within(1e-9));
            }
        }

        [Test]
        public void SilenceAfterSoundSettlesToExactZero()
        {
            // U6 (docs/specs/mcp-server.md §12.13): the K-weighting's states settle to zero once the input stops,
            // instead of decaying into denormal numbers (which cost 25 ms of work a second on silence). A second
            // of silence after noise reads −∞ (the momentary window holds only zeros), and the filter's output is
            // exactly zero.
            var (e, _) = Make();
            var noise = Measure.PinkNoise(Rate, 0.1);
            var z = new float[3 * Rate];
            Run(e, noise, noise);
            Run(e, z, z);
            Assert.That(double.IsNegativeInfinity(Snapshot(e).Momentary), Is.True, "silence after sound reads −∞");
            var k = Loudness.KWeighting.For(Rate);
            k.SumOfSquares(noise, 0, noise.Length);
            k.SumOfSquares(z, 0, Rate);
            Assert.That(k.SumOfSquares(z, 0, Rate), Is.EqualTo(0.0), "the K-weighting's output is exactly zero a second into silence");
            Assert.That(Loudness.Measure(noise.Concat(z).ToArray(), null, Rate).Momentary[^1], Is.EqualTo(double.NegativeInfinity), "and the batch reads the same");
        }

        [Test]
        public void SilenceReadsMinusInfinityAndGatesStayShut()
        {
            var (e, _) = Make();
            var z = new float[5 * Rate];
            Run(e, z, z);
            var s = Snapshot(e);
            Assert.That(double.IsNegativeInfinity(s.Integrated) && double.IsNegativeInfinity(s.Momentary) && double.IsNegativeInfinity(s.ShortTerm), Is.True);
            Assert.That(s.Range, Is.EqualTo(0));
            Assert.That(double.IsNegativeInfinity(s.TruePeakMax), Is.True);
            Assert.That(s.NamedDb.All(double.IsNegativeInfinity), Is.True);
            Assert.That(s.Correlation, Is.EqualTo(1), "silence: correlation 1, as the library has it");
            Assert.That(s.Width, Is.EqualTo(0));
        }

        [Test]
        public void ResetStartsTheIntegratedMeasuresAgain()
        {
            var (e, v) = Make();
            var loud = Noise(10 * Rate, DspMath.DbToGain(-10), 1);
            var quiet = Noise(10 * Rate, DspMath.DbToGain(-30), 2);
            Run(e, loud, loud);
            var before = Snapshot(e);
            v.Set(P.Reset, 1);
            Run(e, quiet, quiet);
            var after = Snapshot(e);
            var batch = Loudness.Measure(quiet, quiet, Rate);
            TestContext.Out.WriteLine($"loud: I {before.Integrated:F2}, TP {before.TruePeakMax:F2}; after the reset, quiet: I {after.Integrated:F3} (batch {batch.Integrated:F3}), TP {after.TruePeakMax:F2} ({batch.TruePeakDb:F2})");
            Assert.That(before.Integrated, Is.EqualTo(Loudness.Measure(loud, loud, Rate).Integrated).Within(0.05));
            Assert.That(after.Integrated, Is.EqualTo(batch.Integrated).Within(0.05), "only what came after the reset");
            Assert.That(after.TruePeakMax, Is.EqualTo(batch.TruePeakDb).Within(0.05));
            Assert.That(after.MomentaryMax, Is.EqualTo(batch.MomentaryMax).Within(0.05));
            Assert.That(after.Seconds, Is.EqualTo(10).Within(0.1));
            // On stays On: no second reset until it is switched Off and On again.
            Run(e, loud, loud);
            Assert.That(Snapshot(e).Integrated, Is.GreaterThan(after.Integrated + 5));
            v.Set(P.Reset, 0);
            Run(e, quiet, quiet);
            Assert.That(Snapshot(e).Seconds, Is.GreaterThan(25), "Off doesn't reset");
        }

        // ------------------------------------------------------------------ pass-through and threading

        [Test]
        public void TheInputPassesThroughUntouched()
        {
            var (e, _) = Make();
            var l = Noise(Rate, 0.3, 5);
            var r = Noise(Rate, 0.3, 6);
            var lc = (float[])l.Clone();
            var rc = (float[])r.Clone();
            for (int pos = 0; pos + 256 <= l.Length; pos += 256)
            {
                var bl = l.AsSpan(pos, 256).ToArray();
                var br = r.AsSpan(pos, 256).ToArray();
                var bl0 = (float[])bl.Clone();
                e.Process(bl, br, 256);
                Assert.That(bl.SequenceEqual(bl0) && br.SequenceEqual(r.AsSpan(pos, 256).ToArray()), Is.True, "the engine only reads its blocks");
            }
            Assert.That(l.SequenceEqual(lc) && r.SequenceEqual(rc), Is.True);
        }

        [Test]
        public void ProcessingAllocatesNothing()
        {
            var (e, v) = Make();
            var l = Noise(4 * Rate, 0.2, 11);
            var r = Noise(4 * Rate, 0.2, 12);
            Run(e, l, r);   // warm up (JIT, first frames)
            var bl = new float[256];
            var br = new float[256];
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int pos = 0; pos + 256 <= l.Length; pos += 256)
            {
                Array.Copy(l, pos, bl, 0, 256);
                Array.Copy(r, pos, br, 0, 256);
                if (pos == 256 * 100) { v.Set(P.Smoothing, 3); v.Set(P.Speed, 3); v.Set(P.Reset, 1); }
                e.Process(bl, br, 256);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Measure.Report($"Neo Analyzer: allocation while processing 4 s with parameter changes and a reset: {allocated} bytes");
            Assert.That(allocated, Is.EqualTo(0));
        }

        [Test]
        public void SnapshotsAreNeverTornAndTheWriterNeverWaits()
        {
            // A writer publishing as fast as it can, two readers copying: every snapshot read is whole.
            var buffer = new TripleBuffer<AnalyzerSnapshot>(() => new AnalyzerSnapshot(), (a, b) => a.CopyTo(b));
            const long Count = 200_000;
            int torn = 0;
            long reads = 0;
            var stop = new ManualResetEventSlim();
            void Reader()
            {
                var s = new AnalyzerSnapshot();
                long last = -1;
                while (!stop.IsSet)
                {
                    buffer.Read(s);
                    float tag = s.Curve[0];
                    if (s.Curve.Any(x => x != tag) || s.ThirdsPeak[30] != tag || s.Momentary != tag || s.Sequence < last) Interlocked.Increment(ref torn);
                    last = s.Sequence;
                    Interlocked.Increment(ref reads);
                }
            }
            var readers = new[] { new Thread(Reader), new Thread(Reader) };
            foreach (var t in readers) t.Start();
            var sw = Stopwatch.StartNew();
            for (long k = 1; k <= Count; k++)
            {
                var s = buffer.Back;
                s.Sequence = k;
                s.Momentary = k;
                Array.Fill(s.Curve, k);
                Array.Fill(s.ThirdsPeak, k);
                buffer.Publish();
            }
            double perPublish = sw.Elapsed.TotalMilliseconds * 1000 / Count;
            stop.Set();
            foreach (var t in readers) t.Join();
            TestContext.Out.WriteLine($"{Count} publishes ({perPublish:F2} µs each, filling included), {reads} reads, {torn} torn");
            Assert.That(torn, Is.EqualTo(0));
            Assert.That(reads, Is.GreaterThan(100));
            var last2 = new AnalyzerSnapshot();
            buffer.Read(last2);
            Assert.That(last2.Sequence, Is.EqualTo(Count), "readers see the newest");
        }

        // ------------------------------------------------------------------ spectrum

        [Test]
        public void ASineReadsItsLevelInItsBand()
        {
            // −20 dB RMS (a sine peaking at −16.99 dBFS): the library's scale, plain RMS dB.
            var x = Measure.Sine(1000, 4 * Rate, Math.Sqrt(2) * 0.1);
            var (e, _) = Make();
            Run(e, x, x);
            var s = Snapshot(e);
            int third1k = Array.IndexOf(Bands.ThirdOctaveNominal, 1000.0);
            int mid = Array.FindIndex(Bands.Named, b => b.Name == "mid");
            int point1k = Array.FindIndex(AnalyzerEngine.CurveHz, f => Math.Abs(f - 1000) < 15);
            double curveAt1k = s.Curve.Skip(point1k - 2).Take(5).Max();
            Measure.Report($"Neo Analyzer: 1 kHz sine at −20 dB RMS: third octave {s.Thirds[third1k]:F3} dB, mid band {s.NamedDb[mid]:F3} dB, curve peak {curveAt1k:F3} dB; " +
                $"next third octaves {s.Thirds[third1k - 1]:F1} / {s.Thirds[third1k + 1]:F1} dB");
            Assert.That(s.Thirds[third1k], Is.EqualTo(-20).Within(0.05));
            Assert.That(s.NamedDb[mid], Is.EqualTo(-20).Within(0.05));
            Assert.That(curveAt1k, Is.EqualTo(-20).Within(0.1), "the curve reads a tone's level at every smoothing");
            Assert.That(s.Thirds[third1k - 2], Is.LessThan(-60), "two bands away: down by more than 40 dB");
            Assert.That(s.Thirds[third1k + 2], Is.LessThan(-60));
            // A sine peaking at −20 dBFS: −23.01 dB RMS, as analyze reads it.
            var y = Measure.Sine(1000, 4 * Rate, 0.1);
            var (e2, _) = Make();
            Run(e2, y, y);
            var lib = Spectrum.Measure(y, y, Rate);
            Assert.That(Snapshot(e2).Thirds[third1k], Is.EqualTo(lib.ThirdOctaveDb[third1k]).Within(0.05));
            Assert.That(lib.ThirdOctaveDb[third1k], Is.EqualTo(-23.01).Within(0.01));
        }

        [Test]
        public void BandsAndStereoAgreeWithTheLibrary()
        {
            // Pink noise plus tones in several bands, a little different on each side.
            int n = 3 * Rate;
            var l = Measure.PinkNoise(n, 0.05, 21);
            var r = Measure.PinkNoise(n, 0.05, 22);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double common = 0.2 * Math.Sin(2 * Math.PI * 40 * t) + 0.1 * Math.Sin(2 * Math.PI * 330 * t) + 0.05 * Math.Sin(2 * Math.PI * 4800 * t);
                l[i] += (float)(common + 0.5 * l[i]);
                r[i] += (float)(common + 0.03 * Math.Sin(2 * Math.PI * 9000 * t));
            }
            var (e, _) = Make();
            Run(e, l, r);
            var s = Snapshot(e);
            var spec = Spectrum.Measure(l, r, Rate);
            var st = Stereo.Measure(l, r, Rate);
            double worst = 0;
            for (int b = 0; b < Bands.Named.Length; b++)
            {
                double d = s.NamedDb[b] - spec.NamedDb[b];
                TestContext.Out.WriteLine($"{Bands.Named[b].Name}: {s.NamedDb[b]:F2} dB (library {spec.NamedDb[b]:F2})");
                worst = Math.Max(worst, Math.Abs(d));
            }
            Measure.Report($"Neo Analyzer: named bands vs Spectrum.Measure (3 s of pink noise and tones) within {worst:F2} dB; correlation {s.Correlation:F4} ({st.Correlation:F4}), width {s.Width:F4} ({st.Width:F4})");
            Assert.That(worst, Is.LessThan(0.5));
            Assert.That(s.Correlation, Is.EqualTo(st.Correlation).Within(0.005), "the last 3 s are the whole signal");
            Assert.That(s.Width, Is.EqualTo(st.Width).Within(0.005));
        }

        [TestCase(0)]
        [TestCase(3)]
        public void PinkNoiseReadsFlatInThirdOctaves(int smoothing)
        {
            var l = Measure.PinkNoise(12 * Rate, 0.1, 31);
            var r = Measure.PinkNoise(12 * Rate, 0.1, 32);
            var (e, _) = Make(v => { v.Set(P.Speed, 3); v.Set(P.Smoothing, smoothing); });
            Run(e, l, r);
            var s = Snapshot(e);
            var lib = Spectrum.Measure(l, r, Rate);
            // 100 Hz – 10 kHz, against the library's reading of the same noise (3 s averaging: about ±1 dB of variance at the bottom).
            double worst = 0;
            for (int i = 0; i < AnalyzerEngine.ThirdCount; i++)
            {
                double f = Bands.ThirdOctaveNominal[i];
                if (f < 100 || f > 10000) continue;
                worst = Math.Max(worst, Math.Abs(s.Thirds[i] - lib.ThirdOctaveDb[i]));
            }
            TestContext.Out.WriteLine($"smoothing {smoothing}: third octaves within {worst:F2} dB of the library, 100 Hz – 10 kHz");
            Assert.That(worst, Is.LessThan(1.0));
            if (smoothing == 3)
            {
                // At 1/3 octave the curve passes through the bars.
                for (int i = 0; i < AnalyzerEngine.ThirdCount; i++)
                {
                    int p = Array.FindIndex(AnalyzerEngine.CurveHz, f => Math.Abs(f / Bands.ThirdOctaveCentre(i) - 1) < 0.008);
                    if (p < 0 || Bands.ThirdOctaveNominal[i] > 16000) continue;
                    double edgeRatio = AnalyzerEngine.CurveHz[p] / Bands.ThirdOctaveCentre(i);
                    Assert.That(s.Curve[p], Is.EqualTo(s.Thirds[i]).Within(0.35), $"curve at {AnalyzerEngine.CurveHz[p]:F0} Hz (×{edgeRatio:F4} of the band centre)");
                }
            }
        }

        [Test]
        public void SpeedSetsTheAveragingAndPeaksHoldThenFall()
        {
            // A 1 kHz tone stops: Fast falls within a fraction of a second, Very slow takes seconds;
            // the peak curve holds for the hold time, then falls at 20 dB/s.
            double After(int speed, double seconds)
            {
                var (e, _) = Make(v => v.Set(P.Speed, speed));
                var x = Measure.Sine(1000, 2 * Rate, 0.5);
                Run(e, x, x);
                var z = new float[(int)(seconds * Rate)];
                Run(e, z, z);
                return Snapshot(e).Thirds[17];
            }
            double fast = After(0, 0.5), slow = After(3, 0.5);
            // Power falls 4.34 dB per time constant once the window has left the tone (about 85 ms).
            TestContext.Out.WriteLine($"0.5 s after a −9.03 dB tone stops: Fast {fast:F1} dB (expected about {-9.03 - 4.34 * 0.415 / 0.1:F1}), Very slow {slow:F2} dB (about {-9.03 - 4.34 * 0.415 / 3:F2})");
            Assert.That(fast, Is.EqualTo(-9.03 - 4.34 * 0.415 / 0.1).Within(3));
            Assert.That(slow, Is.EqualTo(-9.03 - 4.34 * 0.415 / 3).Within(0.5));

            var (e2, v2) = Make(v => { v.Set(P.Speed, 0); v.Set(P.Hold, Params.Table[P.Hold].Raw(1.0)); });
            var tone = Measure.Sine(1000, Rate, 0.5);
            Run(e2, tone, tone);
            double top = Snapshot(e2).ThirdsPeak[17];
            var quiet = new float[Rate / 2];
            Run(e2, quiet, quiet);
            double held = Snapshot(e2).ThirdsPeak[17];
            Run(e2, quiet, quiet);
            Run(e2, quiet, quiet);
            double fallen = Snapshot(e2).ThirdsPeak[17];
            TestContext.Out.WriteLine($"peak {top:F2} dB, after 0.5 s {held:F2} dB, after 1.5 s {fallen:F2} dB (hold 1 s, then 20 dB/s)");
            Assert.That(held, Is.EqualTo(top).Within(1e-3), "held");
            Assert.That(fallen, Is.EqualTo(top - 10).Within(1.0), "then falls about 10 dB in the next half second");
        }

        // ------------------------------------------------------------------ stereo

        [Test]
        public void CorrelationIsOneForMonoMinusOneForAntiPhaseAndNearZeroForUnrelatedNoise()
        {
            var a = Noise(4 * Rate, 0.1, 41);
            var b = Noise(4 * Rate, 0.1, 42);
            var inverted = a.Select(x => -x).ToArray();
            var (mono, _) = Make();
            Run(mono, a, a);
            var (anti, _) = Make();
            Run(anti, a, inverted);
            var (unrelated, _) = Make();
            Run(unrelated, a, b);
            var m = Snapshot(mono);
            var o = Snapshot(anti);
            var u = Snapshot(unrelated);
            Measure.Report($"Neo Analyzer: correlation mono {m.Correlation:F6} (width {m.Width:F6}), anti-phase {o.Correlation:F6} (width {o.Width}), unrelated noise {u.Correlation:F4} (width {u.Width:F3}); 400 ms meter {u.CorrelationNow:F3}");
            Assert.That(m.Correlation, Is.EqualTo(1).Within(1e-9));
            Assert.That(m.Width, Is.EqualTo(0).Within(1e-9));
            Assert.That(o.Correlation, Is.EqualTo(-1).Within(1e-9));
            Assert.That(double.IsPositiveInfinity(o.Width), Is.True, "fully opposed: no mid at all");
            Assert.That(u.Correlation, Is.EqualTo(0).Within(0.02));
            Assert.That(u.Width, Is.EqualTo(1).Within(0.05));
            Assert.That(u.CorrelationNow, Is.EqualTo(0).Within(0.05), "the 400 ms meter too");
            var lib = Stereo.Measure(a, b, Rate);
            Assert.That(u.Correlation, Is.EqualTo(lib.Correlation).Within(0.01), "the library's, over the last 3 of the 4 s");
        }

        [Test]
        public void TheGoniometerGetsTheLatestSamplesInOrder()
        {
            var (e, _) = Make();
            var l = Enumerable.Range(0, 5000).Select(i => i / 10000f).ToArray();
            var r = l.Select(x => -x).ToArray();
            Run(e, l, r, 100);
            var s = Snapshot(e);
            Assert.That(s.GonioCount, Is.EqualTo(AnalyzerSnapshot.GonioPoints));
            // The last publish came at the last step or frame boundary; the points end there, in order.
            int end = (int)s.Samples;
            for (int i = 0; i < s.GonioCount; i++)
            {
                Assert.That(s.GonioL[i], Is.EqualTo(l[end - s.GonioCount + i]));
                Assert.That(s.GonioR[i], Is.EqualTo(-s.GonioL[i]));
            }
        }

        // ------------------------------------------------------------------ readings and manifest

        static string MachinesDir([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Machines"));

        [Test]
        public void ReadingsHaveTheManifestsKeysAndTheSnapshotsValues()
        {
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(MachinesDir(), "NeoAnalyzer", "Neo Analyzer.machine.json")))!.AsObject();
            Assert.That(doc["summary"]!.GetValue<string>(), Does.StartWith("Analyzer"));
            var keys = doc["readings"]!.AsArray().Select(x => x!["key"]!.GetValue<string>()).ToArray();
            var (e, _) = Make();
            var x = Measure.Sine(1000, 5 * Rate, 0.3);
            Run(e, x, x);
            var readings = e.Readings();
            Assert.That(readings.Keys.OrderBy(k => k), Is.EqualTo(keys.OrderBy(k => k)), "Params.def's reading lines are the machine's Readings");
            foreach (var key in new[] { "momentary_lufs", "short_term_lufs", "integrated_lufs", "range_lu", "true_peak_dbtp", "correlation", "width",
                "sub_db", "low_db", "low_mid_db", "mid_db", "high_mid_db", "presence_db", "air_db" })
                Assert.That(readings.ContainsKey(key), Is.True, key);
            var s = Snapshot(e);
            Assert.That(readings["integrated_lufs"], Is.EqualTo(s.Integrated));
            Assert.That(readings["true_peak_dbtp"], Is.EqualTo(s.TruePeakMax));
            Assert.That(readings["mid_db"], Is.EqualTo(s.NamedDb[3]));
            Assert.That(readings["correlation"], Is.EqualTo(1).Within(1e-9));
            Assert.That(readings["mid_db"], Is.EqualTo(Measure.Db(0.3 / Math.Sqrt(2))).Within(0.05));
            Assert.That(readings["band_1000hz_db"], Is.EqualTo(Measure.Db(0.3 / Math.Sqrt(2))).Within(0.05), "the 1 kHz third octave");
            Assert.That(readings["band_31_5hz_db"], Is.LessThan(-100));
            Assert.That(AnalyzerSnapshot.ThirdKeys[2], Is.EqualTo("band_31_5hz_db"));
            // A copy: what the audio thread does next doesn't change it.
            var before = readings["integrated_lufs"];
            Run(e, Scale(x, 12), Scale(x, 12));
            Assert.That(readings["integrated_lufs"], Is.EqualTo(before));
            Assert.That(e.Readings()["integrated_lufs"], Is.GreaterThan(before + 3));
            Assert.That(readings is System.Collections.ObjectModel.ReadOnlyDictionary<string, double>, Is.True, "read-only");
        }

        [Test]
        public void ThirdOctaveReadingsAgreeWithTheLibrary()
        {
            var l = Measure.PinkNoise(3 * Rate, 0.1, 61);
            var r = Measure.PinkNoise(3 * Rate, 0.1, 62);
            var (e, _) = Make();
            Run(e, l, r);
            var readings = e.Readings();
            var lib = Spectrum.Measure(l, r, Rate);
            double worst = 0;
            for (int i = 0; i < AnalyzerEngine.ThirdCount; i++)
            {
                // Below 100 Hz a third octave holds a few of the analyzer's 5.9 Hz bins, and 3 s of noise vary there.
                if (Bands.ThirdOctaveNominal[i] < 100) continue;
                worst = Math.Max(worst, Math.Abs(readings[AnalyzerSnapshot.ThirdKeys[i]] - lib.ThirdOctaveDb[i]));
            }
            Measure.Report($"Neo Analyzer: third-octave readings (3 s of pink noise) within {worst:F2} dB of Spectrum.Measure, 100 Hz – 20 kHz");
            Assert.That(worst, Is.LessThan(0.5));
        }

        // ------------------------------------------------------------------ display (toolkit-free)

        static readonly NeoColor Accent = NeoColor.FromRgb(0x5C, 0xD6, 0xF2);

        [Test]
        public void TheSpectrumGraphDrawsBarsCurveAndPeaksInsideItsPlot()
        {
            var x = Measure.Sine(1000, 2 * Rate, 0.5);
            var (e, _) = Make();
            Run(e, x, x);
            var s = Snapshot(e);
            var c = new RecordingCanvas();
            var r = new NeoRect(0, 0, 600, 240);
            SpectrumGraph.Draw(c, r, AnalyzerEngine.CurveHz, s.Curve, s.CurvePeak, AnalyzerEngine.ThirdLo, AnalyzerEngine.ThirdHi, s.Thirds, s.ThirdsPeak, 0, Accent);
            Assert.That(c.ClipDepth == 0 && !c.Unbalanced, Is.True);
            var plot = SpectrumGraph.Plot(r);
            var paths = c.Of(NeoDrawKind.Path).ToList();
            Assert.That(paths.Count, Is.EqualTo(3), "peak curve, curve area, curve");
            var curve = paths.Last();
            Assert.That(curve.Points.Length, Is.EqualTo(AnalyzerEngine.CurvePoints));
            // The curve's highest point is the tone: at 1 kHz, at −9 dB.
            var top = curve.Points.OrderBy(p => p.Y).First();
            Assert.That(top.X, Is.EqualTo(SpectrumGraph.X(plot, 1000)).Within(plot.Width / 200));
            Assert.That(top.Y, Is.EqualTo(SpectrumGraph.Y(plot, -9.03, 0, -96)).Within(0.5));
            // The 1 kHz bar reaches the same level.
            var bar = c.Of(NeoDrawKind.Rect).Where(b => b.Bounds.Left < SpectrumGraph.X(plot, 1000) && b.Bounds.Right > SpectrumGraph.X(plot, 1000)).OrderBy(b => b.Bounds.Top).First();
            Assert.That(bar.Bounds.Top, Is.EqualTo(top.Y).Within(0.5));
            // A tilt lifts the highs: +3 dB/oct moves 8 kHz up by 9 dB.
            var tilted = SpectrumGraph.CurvePath(plot, AnalyzerEngine.CurveHz, Enumerable.Repeat(-40f, AnalyzerEngine.CurvePoints).ToArray(), 3, 0, -96);
            int at8k = Array.FindIndex(AnalyzerEngine.CurveHz, f => f >= 8000);
            double lift = (tilted.Point(Array.FindIndex(AnalyzerEngine.CurveHz, f => f >= 1000)).Y - tilted.Point(at8k).Y) / plot.Height * 96;
            Assert.That(lift, Is.EqualTo(3 * Math.Log2(AnalyzerEngine.CurveHz[at8k] / AnalyzerEngine.CurveHz[Array.FindIndex(AnalyzerEngine.CurveHz, f => f >= 1000)])).Within(0.01));
        }

        [Test]
        public void LoudnessColoursFollowTheTarget()
        {
            Assert.That(AnalysisColors.Loudness(-14.5, -14), Is.EqualTo(AnalysisColors.OnTarget));
            Assert.That(AnalysisColors.Loudness(-20, -14), Is.EqualTo(AnalysisColors.Under));
            Assert.That(AnalysisColors.Loudness(-12, -14), Is.EqualTo(AnalysisColors.Over));
            Assert.That(AnalysisColors.Loudness(-9, -14), Is.EqualTo(AnalysisColors.Hot));
            Assert.That(AnalysisColors.Loudness(double.NegativeInfinity, -14), Is.EqualTo(Palette.DimText));
            Assert.That(AnalysisColors.TruePeak(-0.5), Is.EqualTo(AnalysisColors.Hot));
            var c = new RecordingCanvas();
            var view = new LoudnessView { Momentary = -12, ShortTerm = -14.2, Integrated = -14.4, Range = 6.25, TruePeakNow = -3, TruePeakHold = -1.5, TruePeakMax = -0.8, Target = -14, Seconds = 75, ShortTermMax = -11, MomentaryMax = -9 };
            LoudnessGraph.Draw(c, new NeoRect(0, 0, 250, 240), view, Accent);
            var texts = c.Of(NeoDrawKind.Text).ToList();
            var integrated = texts.Single(t => t.Text == "−14.4");
            Assert.That(integrated.Color, Is.EqualTo(AnalysisColors.OnTarget));
            Assert.That(texts.Any(t => t.Text == "6.3 LU"), Is.True);
            Assert.That(texts.Single(t => t.Text == "−0.8 dBTP").Color, Is.EqualTo(AnalysisColors.Hot));
            Assert.That(texts.Any(t => t.Text == "measured 1:15"), Is.True);
            var bars = LoudnessGraph.Bars(new NeoRect(0, 0, 250, 240));
            var fills = c.Of(NeoDrawKind.Rect).Where(x => x.Fill.IsGradient).ToList();
            Assert.That(fills.Count, Is.EqualTo(3), "M, S and TP");
            Assert.That(fills[0].Bounds.Top, Is.EqualTo(LoudnessGraph.Y(bars, -12)).Within(1e-9));
            Assert.That(LoudnessGraph.Lu(double.NegativeInfinity), Is.EqualTo("—"));
        }

        [Test]
        public void TheGoniometerDrawsMonoUpAndOneSideDiagonally()
        {
            var centre = new NeoPoint(100, 100);
            var mono = Goniometer.Point(centre, 80, 1, 0.5f, 0.5f);
            var left = Goniometer.Point(centre, 80, 1, 0.5f, 0f);
            var right = Goniometer.Point(centre, 80, 1, 0f, 0.5f);
            Assert.That(mono.X, Is.EqualTo(100).Within(1e-9));
            Assert.That(mono.Y, Is.LessThan(100), "mono goes straight up");
            Assert.That(left.X < 100 && left.Y < 100 && Math.Abs((100 - left.X) - (100 - left.Y)) < 1e-9, Is.True, "left alone: up-left at 45°");
            Assert.That(right.X > 100 && right.Y < 100, Is.True, "right alone: up-right");
            var c = new RecordingCanvas();
            var l = Measure.Sine(220, 1024, 0.3);
            Goniometer.Draw(c, new NeoRect(0, 0, 170, 170), l, l, 1024, Accent);
            var path = c.Of(NeoDrawKind.Path).Single();
            Assert.That(path.Points.All(p => Math.Abs(p.X - 85) < 1e-6), Is.True, "a mono signal is a vertical line");
            Assert.That(path.Points.Max(p => 85 - p.Y), Is.EqualTo(0.92 * (170 - 8) / 2).Within(0.5), "scaled to the loudest point");
        }

        [Test]
        public void TheCorrelationBarFillsFromTheCentre()
        {
            var r = new NeoRect(0, 0, 170, 34);
            var c = new RecordingCanvas();
            CorrelationBar.Draw(c, r, -0.5, 0.2, Accent);
            var fill = c.Of(NeoDrawKind.Rect).Skip(1).First();
            Assert.That(fill.Bounds.Right, Is.EqualTo(CorrelationBar.X(r, 0)).Within(1e-9));
            Assert.That(fill.Bounds.Left, Is.EqualTo(CorrelationBar.X(r, -0.5)).Within(1e-9));
            Assert.That(fill.Fill.Color, Is.EqualTo(AnalysisColors.Hot), "negative: red");
        }

        // ------------------------------------------------------------------ CPU

        [Test]
        public void AnalysisCostsUnderTwoPercentOfRealTime()
        {
            var (e, _) = Make();
            var l = Measure.PinkNoise(10 * Rate, 0.2, 51);
            var r = Measure.PinkNoise(10 * Rate, 0.2, 52);
            Run(e, l, r);   // warm up
            // The best of three 10 s passes: other work on a shared runner only ever adds time.
            double share = double.MaxValue;
            for (int k = 0; k < 3; k++)
            {
                var sw = Stopwatch.StartNew();
                Run(e, l, r);
                share = Math.Min(share, sw.Elapsed.TotalSeconds / 10);
            }
            Measure.Report($"Neo Analyzer: stereo at 48 kHz (8192-point FFT every 2048 samples, loudness, true peak, stereo): {100 * share:F2} % of real time ({1 / share:F0}× real time)");
            Assert.That(share, Is.LessThan(0.02));
        }
    }
}
